using System.Globalization;
using Kakudai.Core.Osu;

namespace Kakudai.Core.Training;

/// <summary>
/// One stat. <see cref="Map"/>: the map's value. <see cref="Defined"/>: the value set on the slider, before Emulate HR
/// (the manual value, or the automatic one: map, then Scale at the rate). <see cref="Final"/>: the value written.
/// <see cref="Expected"/>: what would be written with no manual value (map, then HR, then Scale), which the diff name
/// compares <see cref="Final"/> with. Emulate HR never lowers a stat: a value already at 10 or more stays as it is.
/// </summary>
public readonly record struct Stat(double Map, double Expected, double Defined, double Final);

public sealed record Resolved(double Rate, double Bpm, Stat Hp, Stat Cs, Stat Ar, Stat Od,
    string Version, bool NothingToChange);

/// <summary>Stat and rate math of the trainer (spec 5.4, 5.5, 5.7). All roundings are to 0.1, away from zero.</summary>
public static class Trainer
{
    /// <summary>
    /// The game extrapolates AR/OD linearly beyond 10 (<c>IBeatmapDifficultyInfo.DifficultyRange</c> has no clamp),
    /// so the sliders go to 11.
    /// </summary>
    public const double MaxArOd = 11;

    private const double MaxStat = 10;
    private const double MinRate = 0.5;
    private const double MaxRate = 2;

    private readonly record struct Point(double Time, double BeatLength, bool Uninherited);

    /// <summary>
    /// Reads the stats, main BPM and difficulty name of a .osu text. <see cref="MapBase.HasKiai"/> is left false:
    /// whether an object starts in kiai is read from the decoded objects (<c>MapObject.InKiai</c>), which
    /// <see cref="KakudaiService"/> sets over it.
    /// </summary>
    public static MapBase ReadBase(string osuText)
    {
        var difficulty = new Dictionary<string, string>(StringComparer.Ordinal);
        var timing = new List<Point>();
        var objectTimes = new List<double>();
        var versionName = "";
        var section = "";

        foreach (var raw in osuText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line[0] == '[' && line[^1] == ']') { section = line; continue; }

            switch (section)
            {
                case "[Metadata]":
                case "[Difficulty]":
                    var colon = line.IndexOf(':');
                    if (colon <= 0) break;
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (section == "[Metadata]") { if (key == "Version") versionName = value; }
                    else difficulty[key] = value;
                    break;
                case "[TimingPoints]":
                    var t = line.Split(',');
                    if (t.Length < 2 || !TryNum(t[0], out var time) || !TryNum(t[1], out var beat)) break;
                    var uninherited = t.Length < 7 || t[6].Trim() != "0";
                    timing.Add(new Point(time, beat, uninherited));
                    break;
                case "[HitObjects]":
                    var h = line.Split(',');
                    if (h.Length >= 3 && TryNum(h[2], out var objTime)) objectTimes.Add(objTime);
                    break;
            }
        }

        double Get(string key, double fallback)
            => difficulty.TryGetValue(key, out var v) && TryNum(v, out var d) ? d : fallback;
        var od = Get("OverallDifficulty", 5);
        var hp = Get("HPDrainRate", 5);
        var cs = Get("CircleSize", 5);
        var ar = Get("ApproachRate", od);

        return new MapBase(hp, cs, ar, od,
            MainBpm(timing.Where(p => p.Uninherited).OrderBy(p => p.Time).ToList(), objectTimes),
            HasKiai: false, versionName);
    }

    private static double MainBpm(List<Point> points, List<double> objectTimes)
    {
        points = points.Where(p => p.BeatLength > 0).ToList();
        if (points.Count == 0) return 0;
        var end = objectTimes.Count > 0 ? objectTimes.Max() : points[^1].Time;
        var durations = new Dictionary<double, double>();
        for (var i = 0; i < points.Count; i++)
        {
            var to = i + 1 < points.Count ? Math.Min(points[i + 1].Time, end) : end;
            durations[points[i].BeatLength] =
                durations.GetValueOrDefault(points[i].BeatLength) + Math.Max(0, to - points[i].Time);
        }
        var best = points[0].BeatLength;
        foreach (var p in points)
            if (durations[p.BeatLength] > durations[best]) best = p.BeatLength;
        return 60000 / best;
    }

    public static Resolved Resolve(MapBase map, TrainerSettings s)
    {
        var rate = s.LockedBpm is { } locked && map.MainBpm > 0
            ? Math.Clamp(locked / map.MainBpm, MinRate, MaxRate)
            : Math.Clamp(s.Rate, MinRate, MaxRate);
        rate = Math.Round(rate, 2, MidpointRounding.AwayFromZero);

        // Flip only mirrors the map: it changes no stat. Emulate HR applies the HR formulas over every stat.
        var hr = s.EmulateHr;
        var cs = BuildStat(map.Cs, hr ? 1.3 : 1, MaxStat, s.Cs, null);
        var hp = BuildStat(map.Hp, hr ? 1.4 : 1, MaxStat, s.Hp, null);
        var ar = BuildStat(map.Ar, hr ? 1.4 : 1, MaxArOd, s.Ar, s.ScaleAr ? v => ScaleAr(v, rate) : null);
        var od = BuildStat(map.Od, hr ? 1.4 : 1, MaxArOd, s.Od, s.ScaleOd ? v => ScaleOd(v, rate) : null);

        var bpm = map.MainBpm * rate;
        var nothing = s.Factor == 1 && rate == 1 && !s.FlipMap && !s.AccentAngles
            && Same(hp.Map, hp.Final) && Same(cs.Map, cs.Final) && Same(ar.Map, ar.Final) && Same(od.Map, od.Final);

        return new Resolved(rate, bpm, hp, cs, ar, od, Version(map, s, rate, bpm, hp, cs, ar, od), nothing);
    }

    // Automatic value: map -> HR -> scale at the rate -> clamp. Manual value: defined value -> HR -> clamp.
    // Values left untouched by every transform are not rounded.
    private static Stat BuildStat(double map, double hrFactor, double max, double? manual, Func<double, double>? scale)
    {
        var automatic = Automatic(map, 1, max, scale);
        var expected = hrFactor == 1 ? automatic : Automatic(map, hrFactor, max, scale);
        if (manual is not { } m)
            return new Stat(map, expected, automatic, expected);
        var defined = Math.Clamp(m, 0, max);
        return new Stat(map, expected, defined, Math.Clamp(Hr(defined, hrFactor), 0, max));
    }

    private static double Automatic(double map, double hrFactor, double max, Func<double, double>? scale)
    {
        var value = Hr(Math.Clamp(map, 0, max), hrFactor);
        if (scale is not null)
        {
            var scaled = scale(value);
            if (Math.Abs(scaled - value) > 1e-9) value = scaled;
        }
        return Math.Clamp(value, 0, max);
    }

    /// <summary>The HR formula: × factor, rounded to 0.1, capped at 10. A value already at 10 or more (AR/OD go to 11)
    /// is left as it is: HR never lowers a stat.</summary>
    private static double Hr(double value, double factor)
        => factor == 1 || value >= MaxStat ? value : Round1(Math.Min(value * factor, MaxStat));

    public static double ScaleAr(double ar, double rate)
    {
        var ms = ArToMs(ar) / rate;
        var scaled = ms >= 1200 ? (1800 - ms) / 120 : 5 + (1200 - ms) / 150;
        return Math.Clamp(Round1(scaled), 0, MaxArOd);
    }

    public static double ScaleOd(double od, double rate)
    {
        var ms = (79.5 - 6 * od) / rate;
        return Math.Clamp(Round1((79.5 - ms) / 6), 0, MaxArOd);
    }

    private static double ArToMs(double ar) => ar <= 5 ? 1800 - 120 * ar : 1200 - 150 * (ar - 5);

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static bool Same(double a, double b) => Round1(a) == Round1(b);

    private static string Version(MapBase map, TrainerSettings s, double rate, double bpm,
        Stat hp, Stat cs, Stat ar, Stat od)
    {
        var parts = new List<string>();
        var spaced = s.Factor > 1;
        if (spaced) parts.Add($"{Num(s.Factor, 2)}x spaced");
        if (spaced && s.CrushStreams) parts.Add("(no streams)");
        if (s.AccentAngles) parts.Add("(angles)");
        // "Kiai only" restricts the amplifier, which runs for a spacing factor or for "accent angles".
        if ((spaced || s.AccentAngles) && s.KiaiOnly) parts.Add("(kiai)");
        if (rate != 1)
        {
            parts.Add($"{Num(rate, 2)}x ({Math.Round(bpm, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}bpm)");
            if (s.ChangePitch) parts.Add("pitch"); // the same rate with and without pitch are two different songs
        }
        if (s.EmulateHr) parts.Add("HR");
        if (s.FlipMap)
            parts.Add(s.FlipMode switch { FlipMode.Horizontal => "hflip", FlipMode.Both => "hvflip", _ => "vflip" });
        AddStat(parts, "HP", hp);
        AddStat(parts, "CS", cs);
        AddStat(parts, "AR", ar);
        AddStat(parts, "OD", od);
        if (map.DifficultyName.Length > 0) parts.Add(map.DifficultyName);
        return string.Join(' ', parts);
    }

    private static void AddStat(List<string> parts, string label, Stat stat)
    {
        if (!Same(stat.Final, stat.Expected)) parts.Add(label + Num(stat.Final, 1));
    }

    private static string Num(double value, int decimals)
        => Math.Round(value, decimals, MidpointRounding.AwayFromZero)
            .ToString(decimals == 1 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    private static bool TryNum(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
