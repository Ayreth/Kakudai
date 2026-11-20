using System.Globalization;
using System.Text.Json.Serialization;

namespace Kakudai.Core.Osu;

/// <summary>How "Flip map" mirrors the playfield: vertical y → 384 − y, horizontal x → 512 − x, or both.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlipMode>))]
public enum FlipMode
{
    [JsonStringEnumMemberName("vertical")] Vertical,
    [JsonStringEnumMemberName("horizontal")] Horizontal,
    [JsonStringEnumMemberName("both")] Both,
}

/// <summary>What <see cref="OsuTransform.Apply"/> does to a .osu text; a null <see cref="Flip"/> keeps the positions
/// and a null <see cref="AudioFilename"/> keeps the audio.</summary>
public sealed record TransformOptions(double Rate, FlipMode? Flip, double Hp, double Cs, double Ar, double Od,
    string? AudioFilename);

/// <summary>
/// Field-level rewrite of a .osu text (the output of <see cref="OsuPatcher.Patch"/>): flip, rate, final
/// stats and audio file. Line endings are kept, and an identity transform returns the text unchanged.
/// </summary>
public static class OsuTransform
{
    private const double PlayfieldWidth = 512;
    private const double PlayfieldHeight = 384;

    public static string Apply(string osuText, TransformOptions o)
    {
        ArgumentNullException.ThrowIfNull(osuText);
        ArgumentNullException.ThrowIfNull(o);
        if (!(o.Rate > 0))
            throw new ArgumentOutOfRangeException(nameof(o), "The rate must be positive.");

        var lines = osuText.Split('\n');
        string section = "";
        bool hasAr = false;
        int difficultyEnd = -1, difficultyHeader = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string cr = lines[i].EndsWith('\r') ? "\r" : "";
            string body = lines[i].TrimEnd('\r');

            if (IsSectionHeader(body))
            {
                if (section == "Difficulty" && difficultyEnd < 0)
                    difficultyEnd = i;
                section = body.Trim()[1..^1];
                if (section == "Difficulty")
                    difficultyHeader = i;
                continue;
            }

            if (body.Length == 0)
                continue;

            string? patched = section switch
            {
                "General" => PatchKeyValue(body, o, general: true),
                "Editor" => PatchKeyValue(body, o, general: false),
                "Difficulty" => PatchDifficulty(body, o, ref hasAr),
                "Events" => PatchEvent(body, o.Rate),
                "TimingPoints" => PatchTimingPoint(body, o.Rate),
                "HitObjects" => PatchHitObject(body, o),
                _ => null,
            };
            if (patched != null)
                lines[i] = patched + cr;
        }

        var result = lines.ToList();
        if (!hasAr && difficultyHeader >= 0)
        {
            if (difficultyEnd < 0)
                difficultyEnd = result.Count;
            int at = difficultyEnd;
            while (at > difficultyHeader + 1 && result[at - 1].TrimEnd('\r').Length == 0)
                at--;
            bool crlf = result[difficultyHeader].EndsWith('\r');
            result.Insert(at, "ApproachRate:" + Stat(o.Ar) + (crlf ? "\r" : ""));
        }

        return string.Join("\n", result);
    }

    private static string? PatchKeyValue(string body, TransformOptions o, bool general)
    {
        int colon = body.IndexOf(':');
        if (colon < 0)
            return null;

        string key = body[..colon].Trim();
        string value = body[(colon + 1)..].Trim();
        if (general && key == "AudioFilename" && o.AudioFilename != null)
            return "AudioFilename: " + o.AudioFilename;
        if (o.Rate == 1)
            return null;
        if (general && key == "PreviewTime" && TryNumber(value, out double preview) && preview >= 0)
            return "PreviewTime: " + Time(preview, o.Rate);
        if (!general && key == "Bookmarks")
        {
            var times = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => TryNumber(t, out double v) ? Time(v, o.Rate) : t);
            return body[..(colon + 1)] + " " + string.Join(",", times);
        }

        return null;
    }

    private static string? PatchDifficulty(string body, TransformOptions o, ref bool hasAr)
    {
        int colon = body.IndexOf(':');
        if (colon < 0)
            return null;

        string key = body[..colon].Trim();
        double? value = key switch
        {
            "HPDrainRate" => o.Hp,
            "CircleSize" => o.Cs,
            "ApproachRate" => o.Ar,
            "OverallDifficulty" => o.Od,
            _ => null,
        };
        if (key == "ApproachRate")
            hasAr = true;
        if (value == null)
            return null;

        // A line that already holds the wanted value is kept as written ("5.0" stays "5.0").
        if (TryNumber(body[(colon + 1)..], out double current) && Stat(current) == Stat(value.Value))
            return null;
        return key + ":" + Stat(value.Value);
    }

    private static string? PatchEvent(string body, double rate)
    {
        if (rate == 1)
            return null;
        var f = body.Split(',');
        if (f.Length < 3 || (f[0].Trim() != "2" && f[0].Trim() != "Break"))
            return null;
        if (!TryNumber(f[1], out double start) || !TryNumber(f[2], out double end))
            return null;
        f[1] = Time(start, rate);
        f[2] = Time(end, rate);
        return string.Join(",", f);
    }

    /// <summary>
    /// The time is rounded to the millisecond like the object times (so a timing point stays on the object it
    /// belongs to); the beat length of an uninherited point keeps its decimals (6 at most), since rounding it would
    /// shift the BPM and drift every slider.
    /// </summary>
    private static string? PatchTimingPoint(string body, double rate)
    {
        if (rate == 1)
            return null;
        var f = body.Split(',');
        if (f.Length < 2 || !TryNumber(f[0], out double time) || !TryNumber(f[1], out double beatLength))
            return null;

        bool uninherited = f.Length > 6 ? f[6].Trim() == "1" : beatLength > 0;
        f[0] = Time(time, rate);
        if (uninherited)
            f[1] = (beatLength / rate).ToString("0.######", CultureInfo.InvariantCulture);
        return string.Join(",", f);
    }

    private static string? PatchHitObject(string body, TransformOptions o)
    {
        if (o.Rate == 1 && o.Flip is null)
            return null;
        var f = body.Split(',');
        if (f.Length < 5)
            return null;

        if (o.Flip is { } mode)
        {
            bool flipX = mode is FlipMode.Horizontal or FlipMode.Both;
            bool flipY = mode is FlipMode.Vertical or FlipMode.Both;
            if (flipX && TryNumber(f[0], out double x))
                f[0] = Number(PlayfieldWidth - x);
            if (flipY && TryNumber(f[1], out double y))
                f[1] = Number(PlayfieldHeight - y);
            if (f.Length > 5 && f[5].Contains('|'))
                f[5] = FlipCurve(f[5], flipX, flipY);
        }

        if (o.Rate != 1)
        {
            if (TryNumber(f[2], out double time))
                f[2] = Time(time, o.Rate);
            bool spinner = int.TryParse(f[3], out int type) && (type & 8) != 0;
            if (spinner && f.Length > 5 && TryNumber(f[5], out double end))
                f[5] = Time(end, o.Rate);
        }

        return string.Join(",", f);
    }

    private static string FlipCurve(string curve, bool flipX, bool flipY)
    {
        var tokens = curve.Split('|');
        for (int i = 0; i < tokens.Length; i++)
        {
            var p = tokens[i].Split(':');
            if (p.Length == 2 && TryNumber(p[0], out double x) && TryNumber(p[1], out double y))
                tokens[i] = (flipX ? Number(PlayfieldWidth - x) : p[0]) + ":" + (flipY ? Number(PlayfieldHeight - y) : p[1]);
        }

        return string.Join("|", tokens);
    }

    private static bool TryNumber(string s, out double v)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static string Time(double t, double rate)
        => Math.Round(t / rate, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Stat(double v)
        => Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    private static bool IsSectionHeader(string line)
    {
        var t = line.Trim();
        return t.Length > 1 && t[0] == '[' && t[^1] == ']';
    }
}
