using Kakudai.Core.Osu;
using Kakudai.Core.Training;
using NUnit.Framework;
using Osu = osu.Game.Beatmaps.IBeatmapDifficultyInfo;

namespace Kakudai.Tests;

public class TrainerTests
{
    private static MapBase Map(double hp = 5, double cs = 4, double ar = 9, double od = 8, double bpm = 160)
        => new(hp, cs, ar, od, bpm, false, "Insane");

    private static readonly TrainerSettings Plain = new(Factor: 1);

    [Test]
    public void MaxArOd_MatchesGameExtrapolation()
    {
        // The game keeps extrapolating linearly past 10: AR 11 = 1800/1200/450 range -> 300 ms.
        Assert.That(Osu.DifficultyRange(11, 1800, 1200, 450), Is.EqualTo(300).Within(1e-9));
        Assert.That(Trainer.MaxArOd, Is.EqualTo(11));
    }

    [Test]
    public void ScaleAr_Rate1_5_Ar9_Gives10_3()
        => Assert.That(Trainer.ScaleAr(9, 1.5), Is.EqualTo(Math.Min(10.3, Trainer.MaxArOd)).Within(1e-9));

    [Test]
    public void ScaleOd_Rate1_5_Od8_Gives9_9()
        => Assert.That(Trainer.ScaleOd(8, 1.5), Is.EqualTo(9.8).Within(1e-9));

    [Test]
    public void FlipMap_ChangesNoStat()
    {
        var r = Trainer.Resolve(Map(hp: 5, cs: 4, ar: 8, od: 5), Plain with { FlipMap = true });
        Assert.That(r.Cs.Final, Is.EqualTo(4));
        Assert.That(r.Ar.Final, Is.EqualTo(8));
        Assert.That(r.Od.Final, Is.EqualTo(5));
        Assert.That(r.Hp.Final, Is.EqualTo(5));
    }

    [TestCase(FlipMode.Vertical, "vflip Insane")]
    [TestCase(FlipMode.Horizontal, "hflip Insane")]
    [TestCase(FlipMode.Both, "hvflip Insane")]
    public void FlipMap_Version(FlipMode mode, string version)
    {
        Assert.That(Trainer.Resolve(Map(), Plain with { FlipMap = true, FlipMode = mode }).Version, Is.EqualTo(version));
        Assert.That(Trainer.Resolve(Map(), Plain with { FlipMode = mode }).Version, Is.EqualTo("Insane"), "mode alone does nothing");
    }

    [Test]
    public void EmulateHr_Automatic_MultipliesAndCaps()
    {
        var r = Trainer.Resolve(Map(hp: 5, cs: 4, ar: 8, od: 5), Plain with { EmulateHr = true });
        Assert.That(r.Cs.Final, Is.EqualTo(5.2).Within(1e-9));
        Assert.That(r.Ar.Final, Is.EqualTo(10).Within(1e-9));   // 11.2 capped to 10
        Assert.That(r.Od.Final, Is.EqualTo(7).Within(1e-9));
        Assert.That(r.Hp.Final, Is.EqualTo(7).Within(1e-9));
        Assert.That(r.Cs.Defined, Is.EqualTo(4), "the slider shows the value before HR");
        Assert.That(r.Ar.Defined, Is.EqualTo(8));
        Assert.That(r.Version, Is.EqualTo("HR Insane"));
    }

    [Test]
    public void EmulateHr_OverManualAr8_WritesAr10()
    {
        var r = Trainer.Resolve(Map(ar: 9), Plain with { EmulateHr = true, Ar = 8 });
        Assert.That(r.Ar.Defined, Is.EqualTo(8));
        Assert.That(r.Ar.Final, Is.EqualTo(10).Within(1e-9));   // 8 × 1.4 = 11.2, capped
    }

    [Test]
    public void EmulateHr_OverLockedCs4_Writes5_2_LockKeepsDefinedValue()
    {
        var r = Trainer.Resolve(Map(cs: 3), Plain with { EmulateHr = true, Cs = 4, CsLocked = true });
        Assert.That(r.Cs.Defined, Is.EqualTo(4));
        Assert.That(r.Cs.Final, Is.EqualTo(5.2).Within(1e-9));
        Assert.That(r.Cs.Expected, Is.EqualTo(3.9).Within(1e-9));   // the automatic value: map 3 × 1.3
        Assert.That(r.Version, Is.EqualTo("HR CS5.2 Insane"));
    }

    // Ruling: Emulate HR never lowers a stat, so a value above the HR cap of 10 is kept.
    [Test]
    public void EmulateHr_ValueAbove10_IsNotLowered()
        => Assert.That(Trainer.Resolve(Map(), Plain with { EmulateHr = true, Ar = 10.5 }).Ar.Final, Is.EqualTo(10.5));

    [Test]
    public void EmulateHr_ManualOrder_HrWithoutScale()
    {
        // Manual: defined -> HR -> clamp, Scale never applies. Automatic: map -> HR -> Scale.
        var r = Trainer.Resolve(Map(ar: 6), Plain with { Rate = 1.5, EmulateHr = true, Ar = 6 });
        Assert.That(r.Ar.Final, Is.EqualTo(8.4).Within(1e-9));
        Assert.That(r.Ar.Expected, Is.EqualTo(Trainer.ScaleAr(8.4, 1.5)).Within(1e-9));
    }

    [Test]
    public void LockedValue_IgnoresScale_EmulateHrAppliesOverIt()
    {
        var s = Plain with { Rate = 1.5, Ar = 7.3, ArLocked = true, Od = 6, OdLocked = true, Cs = 3, CsLocked = true };
        var r = Trainer.Resolve(Map(), s);
        Assert.That(r.Ar.Final, Is.EqualTo(7.3));
        Assert.That(r.Od.Final, Is.EqualTo(6));
        Assert.That(r.Cs.Final, Is.EqualTo(3));

        var hr = Trainer.Resolve(Map(), s with { EmulateHr = true });
        Assert.That(hr.Ar.Final, Is.EqualTo(10).Within(1e-9));   // 10.22 capped
        Assert.That(hr.Od.Final, Is.EqualTo(8.4).Within(1e-9));
        Assert.That(hr.Cs.Final, Is.EqualTo(3.9).Within(1e-9));
        Assert.That(hr.Ar.Defined, Is.EqualTo(7.3));
    }

    [Test]
    public void LockedAr_ExpectedIgnoresLock_VersionShowsIt()
    {
        var r = Trainer.Resolve(Map(ar: 9), Plain with { Rate = 1.5, Ar = 9, ArLocked = true });
        Assert.That(r.Ar.Expected, Is.EqualTo(10.3).Within(1e-9));
        Assert.That(r.Ar.Final, Is.EqualTo(9));
        Assert.That(r.Version, Does.Contain("AR9"));
    }

    [Test]
    public void ManualValue_DefinedEqualsFinalWithoutHr()
    {
        var r = Trainer.Resolve(Map(hp: 5), Plain with { Hp = 6 });
        Assert.That(r.Hp.Defined, Is.EqualTo(6));
        Assert.That(r.Hp.Final, Is.EqualTo(6));
        Assert.That(r.Hp.Expected, Is.EqualTo(5));
    }

    [Test]
    public void LockFlagWithoutManualValue_FinalIsExpected()
    {
        var r = Trainer.Resolve(Map(), Plain with { Rate = 1.5, HpLocked = true, CsLocked = true, ArLocked = true, OdLocked = true });
        Assert.That(r.Ar.Final, Is.EqualTo(r.Ar.Expected));
        Assert.That(r.Od.Final, Is.EqualTo(r.Od.Expected));
        Assert.That(r.Hp.Final, Is.EqualTo(r.Hp.Expected));
        Assert.That(r.Cs.Final, Is.EqualTo(r.Cs.Expected));
    }

    [Test]
    public void ScaleArOff_KeepsMapAr()
    {
        var r = Trainer.Resolve(Map(ar: 9), Plain with { Rate = 1.5, ScaleAr = false });
        Assert.That(r.Ar.Expected, Is.EqualTo(9));
    }

    [Test]
    public void Rate_IsClamped()
    {
        Assert.That(Trainer.Resolve(Map(), Plain with { Rate = 3 }).Rate, Is.EqualTo(2));
        Assert.That(Trainer.Resolve(Map(), Plain with { Rate = 0.1 }).Rate, Is.EqualTo(0.5));
    }

    [Test]
    public void Version_EmptyDifficultyName_NoTrailingSpace()
        => Assert.That(Trainer.Resolve(Map() with { DifficultyName = "" }, new TrainerSettings(Factor: 2)).Version, Is.EqualTo("2x spaced"));

    [Test]
    public void ManualValue_IsFinal_ExpectedStaysAutomatic()
    {
        var r = Trainer.Resolve(Map(), Plain with { Rate = 1.1, Ar = 9.5 });
        Assert.That(r.Ar.Final, Is.EqualTo(9.5));
        Assert.That(r.Ar.Expected, Is.EqualTo(9.4).Within(1e-9));
    }

    [Test]
    public void LockedBpm200_On160_GivesRate1_25()
    {
        var r = Trainer.Resolve(Map(bpm: 160), Plain with { LockedBpm = 200 });
        Assert.That(r.Rate, Is.EqualTo(1.25).Within(1e-9));
        Assert.That(r.Bpm, Is.EqualTo(200).Within(1e-9));
    }

    [Test]
    public void LockedBpm_ClampsRate()
    {
        Assert.That(Trainer.Resolve(Map(bpm: 100), Plain with { LockedBpm = 400 }).Rate, Is.EqualTo(2));
        Assert.That(Trainer.Resolve(Map(bpm: 200), Plain with { LockedBpm = 40 }).Rate, Is.EqualTo(0.5));
    }

    [Test]
    public void Version_Examples()
    {
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, Rate: 1.1)).Version,
            Is.EqualTo("2x spaced 1.1x (176bpm) Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, Rate: 1.1, Ar: 9.5)).Version,
            Is.EqualTo("2x spaced 1.1x (176bpm) AR9.5 Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, CrushStreams: true)).Version,
            Is.EqualTo("2x spaced (no streams) Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, Rate: 1.2, FlipMap: true)).Version,
            Is.EqualTo("1.2x (192bpm) vflip Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, FlipMap: true, FlipMode: FlipMode.Both, EmulateHr: true, Ar: 6)).Version,
            Is.EqualTo("HR hvflip AR8.4 Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, AccentAngles: true)).Version,
            Is.EqualTo("(angles) Insane"));
    }

    [Test]
    public void Version_NoStreamsAndKiaiOnlyWhenSpaced()
    {
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, CrushStreams: true, KiaiOnly: true)).Version,
            Is.EqualTo("Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1.5, AccentAngles: true, KiaiOnly: true)).Version,
            Is.EqualTo("1.5x spaced (angles) (kiai) Insane"));
    }

    [Test]
    public void Version_KiaiOnlyWithAnglesAlone()
    {
        // Accent angles runs the amplifier at 1x too, and "Kiai only" restricts it: two different maps, two names.
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, AccentAngles: true, KiaiOnly: true)).Version,
            Is.EqualTo("(angles) (kiai) Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1, AccentAngles: true)).Version,
            Is.EqualTo("(angles) Insane"));
    }

    [Test]
    public void Version_PitchAfterRate()
    {
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, Rate: 1.1, ChangePitch: true)).Version,
            Is.EqualTo("2x spaced 1.1x (176bpm) pitch Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, Rate: 1.1, ChangePitch: true, EmulateHr: true)).Version,
            Is.EqualTo("2x spaced 1.1x (176bpm) pitch HR Insane"));
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 2, ChangePitch: true)).Version,
            Is.EqualTo("2x spaced Insane"), "no rate: the pitch changes nothing");
    }

    [Test]
    public void Version_ManualValuesUseOneDecimal()
    {
        var r = Trainer.Resolve(Map(), Plain with { Hp = 3, Cs = 4.5, Od = 7.25 });
        Assert.That(r.Version, Is.EqualTo("HP3 CS4.5 OD7.3 Insane"));
    }

    [Test]
    public void NothingToChange_Defaults_AtK1()
    {
        Assert.That(Trainer.Resolve(Map(), Plain).NothingToChange, Is.True);
        Assert.That(Trainer.Resolve(Map(), Plain with { Rate = 1.1 }).NothingToChange, Is.False);
        Assert.That(Trainer.Resolve(Map(), Plain with { FlipMap = true }).NothingToChange, Is.False);
        Assert.That(Trainer.Resolve(Map(), Plain with { EmulateHr = true }).NothingToChange, Is.False);
        Assert.That(Trainer.Resolve(Map(), Plain with { AccentAngles = true }).NothingToChange, Is.False);
        Assert.That(Trainer.Resolve(Map(), Plain with { Ar = 9.5 }).NothingToChange, Is.False);
        Assert.That(Trainer.Resolve(Map(), new TrainerSettings(Factor: 1.5)).NothingToChange, Is.False);
    }

    private const string Osu1 = """
        osu file format v14

        [Metadata]
        Title:T
        Version:Hard

        [Difficulty]
        HPDrainRate:6
        CircleSize:4
        OverallDifficulty:8
        SliderMultiplier:1.4

        [TimingPoints]
        0,500,4,1,0,100,1,0
        1000,-100,4,1,0,100,0,1
        2000,400,4,1,0,100,1,0

        [HitObjects]
        100,100,0,1,0
        4000,100,5000,1,0
        """;

    [Test]
    public void ReadBase_NoApproachRate_UsesOd()
    {
        var b = Trainer.ReadBase(Osu1);
        Assert.That(b.Ar, Is.EqualTo(8));
        Assert.That(b.Od, Is.EqualTo(8));
        Assert.That(b.Hp, Is.EqualTo(6));
        Assert.That(b.Cs, Is.EqualTo(4));
        Assert.That(b.DifficultyName, Is.EqualTo("Hard"));
        Assert.That(b.HasKiai, Is.False, "read from the decoded objects by the service, not here");
    }

    [Test]
    public void ReadBase_ExplicitApproachRate()
        => Assert.That(Trainer.ReadBase(Osu1.Replace("OverallDifficulty:8", "OverallDifficulty:8\nApproachRate:9.3")).Ar, Is.EqualTo(9.3));

    [Test]
    public void ReadBase_MainBpm_LongestSection()
    {
        // 500 ms covers 0-2000; 400 ms covers 2000 up to the last object.
        // Last object at 6000: 400 ms covers 4000 ms and wins. Last object at 2500: 500 ms wins (2000 vs 500).
        var b = Trainer.ReadBase(Osu1.Replace("4000,100,5000", "6000,100,7000"));
        Assert.That(b.MainBpm, Is.EqualTo(150).Within(1e-9));   // 400 ms covers 4000 ms
        Assert.That(Trainer.ReadBase(Osu1.Replace("4000,100,5000", "2500,100,3000")).MainBpm, Is.EqualTo(120).Within(1e-9));
    }
}
