using System.Globalization;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using NVector2 = System.Numerics.Vector2;

namespace Kakudai.Core.Maps;

public sealed class UnsupportedMapException(string message) : Exception(message);

public static class MapLoader
{
    private const int PathSampleSteps = 32;
    private const float MaxPositionMismatch = 0.5f;

    private sealed record SourceObject(int LineIndex, ObjectKind Kind, NVector2 Position, double Time);

    public static LoadedMap Load(string osuText)
    {
        OsuGameInit.EnsureRegistered();

        var lines = osuText.Split('\n');
        var sources = ParseSource(lines);
        var playable = Decode(osuText);

        if (playable.HitObjects.Count != sources.Count)
            throw new UnsupportedMapException(
                $"Unsupported map: {sources.Count} objects in the file, {playable.HitObjects.Count} decoded.");

        // The decoder stably sorts objects by start time; do the same with the source lines to pair them.
        var ordered = sources.OrderBy(s => s.Time).ToList();
        var objects = new List<MapObject>(ordered.Count);

        var controlPoints = playable.ControlPointInfo;
        for (int i = 0; i < ordered.Count; i++)
            objects.Add(Pair(ordered[i], (OsuHitObject)playable.HitObjects[i], controlPoints));

        return new LoadedMap(lines, objects, t => controlPoints.TimingPointAt(t).BeatLength,
            playable.BeatmapInfo.DifficultyName);
    }

    private static IBeatmap Decode(string osuText)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(osuText));
        using var reader = new LineBufferedReader(stream);
        var beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        return new FlatWorkingBeatmap(beatmap).GetPlayableBeatmap(new OsuRuleset().RulesetInfo);
    }

    /// <summary>The decoded object, checked against its source line. It is in kiai when the effect point active at its
    /// start has kiai on (bit 0 of the effects field of the timing points, inherited points included).</summary>
    private static MapObject Pair(SourceObject source, OsuHitObject decoded, ControlPointInfo controlPoints)
    {
        var kind = decoded switch
        {
            Slider => ObjectKind.Slider,
            Spinner => ObjectKind.Spinner,
            _ => ObjectKind.Circle,
        };
        var start = ToNumerics(decoded.Position);

        // Spinners are always decoded at the playfield centre, whatever the line says, so only check the others.
        if (kind != source.Kind
            || (kind != ObjectKind.Spinner && NVector2.Distance(start, source.Position) > MaxPositionMismatch))
            throw new UnsupportedMapException(
                $"Unsupported map: the object on line {source.LineIndex + 1} does not match the decoded object.");

        var samples = decoded is Slider slider
            ? Enumerable.Range(0, PathSampleSteps + 1)
                .Select(i => ToNumerics(slider.Path.PositionAt((double)i / PathSampleSteps)))
                .ToArray()
            : [];

        return new MapObject(source.LineIndex, kind, decoded.StartTime, decoded.GetEndTime(),
            start, ToNumerics(decoded.EndPosition), samples, controlPoints.EffectPointAt(decoded.StartTime).KiaiMode);
    }

    /// <summary>
    /// Reads the [General] Mode and the [HitObjects] lines, skipping lines the same way the osu! legacy decoder does.
    /// </summary>
    private static List<SourceObject> ParseSource(string[] lines)
    {
        var hitObjectLines = new List<(int Index, string Text)>();
        string section = "";
        string mode = "0";

        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;

            if (section != "Metadata")
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment > 0)
                    line = line[..comment];
            }

            line = line.TrimEnd();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            int colon = line.IndexOf(':');
            if (section == "General" && colon > 0 && line[..colon].Trim() == "Mode")
                mode = line[(colon + 1)..].Trim();
            else if (section == "HitObjects")
                hitObjectLines.Add((index, line));
        }

        if (!int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ruleset) || ruleset != 0)
            throw new UnsupportedMapException(
                $"Unsupported map: only osu!standard is supported (Mode: {mode}).");

        return hitObjectLines.Select(l => ParseHitObject(l.Text, l.Index)).ToList();
    }

    private static SourceObject ParseHitObject(string line, int index)
    {
        var fields = line.Split(',');

        if (fields.Length < 4
            || !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
            || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
            throw new UnsupportedMapException($"Unsupported map: unreadable object on line {index + 1}.");

        ObjectKind kind = (type & 1) != 0 ? ObjectKind.Circle
            : (type & 2) != 0 ? ObjectKind.Slider
            : (type & 8) != 0 ? ObjectKind.Spinner
            : throw new UnsupportedMapException(
                $"Unsupported map: unknown object type ({type}) on line {index + 1}.");

        // The decoder truncates coordinates to whole pixels.
        return new SourceObject(index, kind, new NVector2((int)x, (int)y), time);
    }

    private static NVector2 ToNumerics(osuTK.Vector2 v) => new(v.X, v.Y);
}
