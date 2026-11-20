using System.Globalization;
using System.Numerics;
using Kakudai.Core.Amplify;
using Kakudai.Core.Maps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;

namespace Kakudai.Core.Osu;

/// <summary>Rewrites the original .osu text with new object positions and a new header, leaving the rest untouched.</summary>
public static class OsuPatcher
{
    public static string Patch(LoadedMap map, AmplifyResult result, string newVersion)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(newVersion);

        var lines = map.Lines.ToList();

        for (int i = 0; i < map.Objects.Count; i++)
        {
            var obj = map.Objects[i];
            var placement = result.Placements[i];
            if (placement.Start == obj.Start && placement.Rotation == 0)
                continue;

            lines[obj.LineIndex] = PatchObjectLine(lines[obj.LineIndex], obj, placement);
        }

        PatchMetadata(lines, newVersion);
        return string.Join("\n", lines);
    }

    private static string PatchObjectLine(string line, MapObject obj, Placement placement)
    {
        string cr = line.EndsWith('\r') ? "\r" : "";
        var fields = line.TrimEnd('\r').Split(',');
        if (fields.Length < 2)
            return line;

        float cos = MathF.Cos(placement.Rotation), sin = MathF.Sin(placement.Rotation);

        // The control points follow the head as it is written (rounded), so that the shape stays the same relative
        // to it: with no rotation, a whole-pixel slider keeps every control point exactly.
        var head = new Vector2(Round(placement.Start.X), Round(placement.Start.Y));

        Vector2 Transform(Vector2 p)
        {
            var d = p - obj.Start;
            return head + new Vector2(cos * d.X - sin * d.Y, sin * d.X + cos * d.Y);
        }

        fields[0] = Format(head.X);
        fields[1] = Format(head.Y);
        if (obj.Kind == ObjectKind.Slider && fields.Length > 5)
        {
            fields[5] = TransformCurve(fields[5], Transform);
            if (placement.Rotation != 0 && fields.Length > 7
                && double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double length))
                fields[5] = RefinePerfectCircle(fields[5], obj, head, cos, sin, length);
        }

        return string.Join(",", fields) + cr;
    }

    /// <summary>Largest distance tolerated between a turned perfect-circle path and the path wanted, in px.</summary>
    private const float PerfectCircleTolerance = 0.75f;

    /// <summary>
    /// A perfect-circle curve ("P" and two points) is defined by three points, so rounding them can move the whole
    /// circle a lot, or even flip the arc to the other side, when they are close to each other (a nearly closed
    /// circle). When the rounded curve strays more than <see cref="PerfectCircleTolerance"/> from the turned path,
    /// tries the whole-pixel points around each rounded one and keeps the curve closest to the turned path (as the
    /// game builds it).
    /// </summary>
    private static string RefinePerfectCircle(string curveField, MapObject obj, Vector2 head, float cos, float sin,
        double length)
    {
        var tokens = curveField.Split('|');
        if (tokens.Length != 3 || tokens[0] != "P" || obj.PathSamples.Count == 0
            || !TryPoint(tokens[1], out var a) || !TryPoint(tokens[2], out var b))
            return curveField;

        var wanted = obj.PathSamples
            .Select(d => new Vector2(cos * d.X - sin * d.Y, sin * d.X + cos * d.Y))
            .ToArray();
        float Deviation(Vector2 p, Vector2 q)
        {
            var path = new SliderPath(
            [
                new PathControlPoint(osuTK.Vector2.Zero, PathType.PERFECT_CURVE),
                new PathControlPoint(new osuTK.Vector2(p.X - head.X, p.Y - head.Y)),
                new PathControlPoint(new osuTK.Vector2(q.X - head.X, q.Y - head.Y)),
            ], length);
            float worst = 0;
            for (int j = 0; j < wanted.Length; j++)
            {
                var at = path.PositionAt((double)j / (wanted.Length - 1));
                worst = MathF.Max(worst, Vector2.Distance(new Vector2(at.X, at.Y), wanted[j]));
            }
            return worst;
        }

        var best = (A: a, B: b, Deviation: Deviation(a, b));
        if (best.Deviation <= PerfectCircleTolerance)
            return curveField;

        // Any point of the arc defines the same circle with the head and the end: besides the turned control
        // points, try points of the wanted path itself, halfway and at its end, which are far apart (well
        // conditioned) whatever the original points were.
        Vector2 Pixel(Vector2 p) => new(Round(p.X), Round(p.Y));
        var middles = new[] { a, Pixel(head + wanted[wanted.Length / 3]), Pixel(head + wanted[wanted.Length / 2]) };
        var ends = new[] { b, Pixel(head + wanted[^1]) };
        foreach (var middle in middles)
        foreach (var end in ends)
        for (int ax = -1; ax <= 1; ax++)
        for (int ay = -1; ay <= 1; ay++)
        for (int bx = -1; bx <= 1; bx++)
        for (int by = -1; by <= 1; by++)
        {
            var p = middle + new Vector2(ax, ay);
            var q = end + new Vector2(bx, by);
            float deviation = Deviation(p, q);
            if (deviation < best.Deviation) best = (p, q, deviation);
        }

        return $"P|{Format(best.A.X)}:{Format(best.A.Y)}|{Format(best.B.X)}:{Format(best.B.Y)}";
    }

    private static bool TryPoint(string token, out Vector2 point)
    {
        point = default;
        var parts = token.Split(':');
        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            return false;
        point = new Vector2(x, y);
        return true;
    }

    /// <summary>Transforms every x:y token of a slider curve field, rounded to whole pixels; other tokens are kept.</summary>
    internal static string TransformCurve(string curveField, Func<Vector2, Vector2> transform)
    {
        var tokens = curveField.Split('|');
        for (int i = 0; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split(':');
            if (parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                continue;

            var p = transform(new Vector2(x, y));
            tokens[i] = Format(p.X) + ":" + Format(p.Y);
        }

        return string.Join("|", tokens);
    }

    private static float Round(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);

    private static string Format(float v)
        => ((int)MathF.Round(v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static void PatchMetadata(List<string> lines, string newVersion)
    {
        int header = lines.FindIndex(l => l.Trim() == "[Metadata]");
        if (header < 0)
            throw new InvalidOperationException("The map has no [Metadata] section.");

        int end = header + 1;
        while (end < lines.Count && !IsSectionHeader(lines[end]))
            end++;

        bool crlf = false;
        int lastNonEmpty = header;
        bool seenId = false, seenSet = false, seenTags = false;

        for (int i = header + 1; i < end; i++)
        {
            string cr = lines[i].EndsWith('\r') ? "\r" : "";
            string body = lines[i].TrimEnd('\r');
            if (body.Trim().Length > 0)
                lastNonEmpty = i;
            if (cr.Length > 0)
                crlf = true;

            int colon = body.IndexOf(':');
            if (colon < 0)
                continue;

            switch (body[..colon].Trim())
            {
                case "Version":
                    lines[i] = "Version:" + newVersion + cr;
                    break;
                case "BeatmapID":
                    lines[i] = "BeatmapID:0" + cr;
                    seenId = true;
                    break;
                case "BeatmapSetID":
                    lines[i] = "BeatmapSetID:-1" + cr;
                    seenSet = true;
                    break;
                case "Tags":
                    string raw = body[(colon + 1)..];
                    lines[i] = "Tags:" + (raw.Trim().Length == 0 ? "kakudai" : raw + " kakudai") + cr;
                    seenTags = true;
                    break;
            }
        }

        string eol = crlf ? "\r" : "";
        var missing = new List<string>();
        if (!seenTags)
            missing.Add("Tags:kakudai" + eol);
        if (!seenId)
            missing.Add("BeatmapID:0" + eol);
        if (!seenSet)
            missing.Add("BeatmapSetID:-1" + eol);

        lines.InsertRange(lastNonEmpty + 1, missing);
    }

    private static bool IsSectionHeader(string line)
    {
        var t = line.Trim();
        return t.Length > 1 && t[0] == '[' && t[^1] == ']';
    }
}
