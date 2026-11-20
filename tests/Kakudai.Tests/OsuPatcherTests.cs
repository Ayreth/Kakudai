using System.Numerics;
using Kakudai.Core.Amplify;
using Kakudai.Core.Maps;
using Kakudai.Core.Osu;
using NUnit.Framework;

namespace Kakudai.Tests;

public class OsuPatcherTests
{
    private const string NewVersion = "2x spaced Insane";

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

    private static AmplifyResult Identity(LoadedMap map)
        => new(map.Objects.Select(o => new Placement(o.Start, 0)).ToList(), 0, 0, 0, 0);

    private static AmplifyResult With(LoadedMap map, int index, Placement placement)
    {
        var list = map.Objects.Select(o => new Placement(o.Start, 0)).ToList();
        list[index] = placement;
        return new AmplifyResult(list, 0, 0, 0, 0);
    }

    [Test]
    public void Patch_Factor1_OnlyHeaderChanges()
    {
        var map = MapLoader.Load(Fixture("simple.osu"));
        var result = Amplifier.Amplify(map.Objects, map.BeatLengthAt, new AmplifyOptions(1.0, true));

        var patched = OsuPatcher.Patch(map, result, NewVersion).Split('\n');

        var changed = patched.Zip(map.Lines).Where(p => p.First != p.Second).Select(p => p.First).ToArray();
        Assert.That(patched, Has.Length.EqualTo(map.Lines.Length));
        Assert.That(changed, Is.EqualTo(new[]
        {
            "Version:2x spaced Insane", "Tags:test kakudai", "BeatmapID:0", "BeatmapSetID:-1",
        }));
    }

    [Test]
    public void Patch_Factor1_CrLf_KeepsLineEndings()
    {
        var text = Fixture("simple.osu").Replace("\n", "\r\n");
        var map = MapLoader.Load(text);

        var patched = OsuPatcher.Patch(map, Identity(map), NewVersion);

        Assert.That(patched, Does.Contain("Version:2x spaced Insane\r\n"));
        Assert.That(patched, Does.Contain("BeatmapSetID:-1\r\n"));
        Assert.That(patched.Replace("\r\n", "").Contains('\n'), Is.False);
    }

    [Test]
    public void Patch_MovesCircle()
    {
        var map = MapLoader.Load(Fixture("simple.osu"));

        var patched = OsuPatcher.Patch(map, With(map, 1, new Placement(new Vector2(406, 192), 0)), NewVersion);

        Assert.That(patched.Split('\n'), Does.Contain("406,192,1500,1,0,0:0:0:0:"));
    }

    [Test]
    public void Patch_RotatesSliderCurve()
    {
        var map = MapLoader.Load(Fixture("simple.osu"));

        var patched = OsuPatcher.Patch(map,
            With(map, 2, new Placement(new Vector2(300, 100), MathF.PI / 2)), NewVersion);

        Assert.That(patched.Split('\n'), Does.Contain("300,100,2000,2,0,L|300:200,1,100"));
    }

    [Test]
    public void Patch_TurnedPerfectCircle_KeepsItsPath()
    {
        // A nearly closed perfect-circle slider (from a real map): its head and end are 5 px apart, so rounding its
        // control points naively after a rotation moves the circle by several pixels.
        var map = MapLoader.Load(Fixture("simple.osu")
            .Replace("100,100,2000,2,0,L|200:100,1,100", "196,246,2000,2,0,P|126:279|198:241,1,340"));
        var slider = map.Objects[2];
        Assume.That(slider.Kind, Is.EqualTo(ObjectKind.Slider));

        double worst = 0;
        for (int step = 0; step < 72; step++)
        {
            float angle = step * MathF.PI / 36;
            var placement = new Placement(new Vector2(250.3f + step % 3 * 0.2f, 190.6f - step % 4 * 0.15f), angle);
            var decoded = MapLoader.Load(OsuPatcher.Patch(map, With(map, 2, placement), NewVersion)).Objects[2];

            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            for (int j = 0; j < slider.PathSamples.Count; j++)
            {
                var d = slider.PathSamples[j];
                var wanted = placement.Start + new Vector2(c * d.X - s * d.Y, s * d.X + c * d.Y);
                worst = Math.Max(worst, Vector2.Distance(decoded.Start + decoded.PathSamples[j], wanted));
            }
        }

        TestContext.Out.WriteLine($"écart maximal {worst:0.00} px");
        Assert.That(worst, Is.LessThanOrEqualTo(1.5));
    }

    [Test]
    public void TransformCurve_KeepsTypeTokens()
    {
        var result = OsuPatcher.TransformCurve("B|200:100|P|200:200|250:250", p => p + new Vector2(10, 0));

        Assert.That(result, Is.EqualTo("B|210:100|P|210:200|260:250"));
    }

    [Test]
    public void Patch_InsertsMissingIds()
    {
        var text = Fixture("simple.osu").Replace("BeatmapID:123\n", "").Replace("BeatmapSetID:456\n", "");
        var map = MapLoader.Load(text);

        var lines = OsuPatcher.Patch(map, Identity(map), NewVersion).Split('\n');

        int tags = Array.IndexOf(lines, "Tags:test kakudai");
        Assert.That(tags, Is.GreaterThan(0));
        Assert.That(lines[tags + 1], Is.EqualTo("BeatmapID:0"));
        Assert.That(lines[tags + 2], Is.EqualTo("BeatmapSetID:-1"));
        Assert.That(lines[tags + 3], Is.EqualTo(""));
        Assert.That(lines[tags + 4], Is.EqualTo("[Difficulty]"));
    }

    [Test]
    public void Patch_Spinner_Verbatim()
    {
        var map = MapLoader.Load(Fixture("simple.osu"));

        var patched = OsuPatcher.Patch(map, Identity(map), NewVersion).Split('\n');

        Assert.That(patched, Does.Contain("256,192,3000,12,0,4000,0:0:0:0:"));
    }

    private static string PatchText(string text)
    {
        var map = MapLoader.Load(text);
        return OsuPatcher.Patch(map, Identity(map), NewVersion);
    }

    [Test]
    public void Patch_Tags_KeepsOriginalWhitespace()
    {
        var text = Fixture("simple.osu").Replace("Tags:test\n", "Tags: a b \n");

        Assert.That(PatchText(text).Split('\n'), Does.Contain("Tags: a b  kakudai"));
    }

    [Test]
    public void Patch_EmptyTags_BecomesKakudai()
    {
        var text = Fixture("simple.osu").Replace("Tags:test\n", "Tags:\n");

        Assert.That(PatchText(text).Split('\n'), Does.Contain("Tags:kakudai"));
    }

    [Test]
    public void Patch_MovedSlider_KeepsTrailingCr()
    {
        var map = MapLoader.Load(Fixture("simple.osu").Replace("\n", "\r\n"));

        var patched = OsuPatcher.Patch(map,
            With(map, 2, new Placement(new Vector2(300, 100), MathF.PI / 2)), NewVersion);

        Assert.That(patched.Split('\n'), Does.Contain("300,100,2000,2,0,L|300:200,1,100\r"));
    }

    [Test]
    public void Patch_VersionAndTagsOutsideMetadata_Untouched()
    {
        var text = Fixture("simple.osu")
            .Replace("[Events]\n", "[Events]\n//Version:keep\n//Tags:keep\n")
            .Replace("[General]\n", "[General]\nTags:general\nVersion:general\n");

        var lines = PatchText(text).Split('\n');

        Assert.That(lines, Does.Contain("//Version:keep"));
        Assert.That(lines, Does.Contain("//Tags:keep"));
        Assert.That(lines, Does.Contain("Tags:general"));
        Assert.That(lines, Does.Contain("Version:general"));
    }

    [Test]
    public void Patch_NoMetadataSection_Throws()
    {
        var text = Fixture("simple.osu").Replace("[Metadata]", "[Other]");

        Assert.Throws<InvalidOperationException>(() => PatchText(text));
    }
}
