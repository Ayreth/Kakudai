using Kakudai.Core.Maps;
using Kakudai.Core.Osu;
using NUnit.Framework;

namespace Kakudai.Tests;

public class OsuTransformTests
{
    private const string Map =
        "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nPreviewTime: 3000\nMode: 0\n\n"
        + "[Editor]\nBookmarks: 1000,2000,3001\n\n"
        + "[Metadata]\nTitle:T\nVersion:Insane\n\n"
        + "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n\n"
        + "[Events]\n0,0,\"bg.jpg\",0,0\n2,1500,2500\n\n"
        + "[TimingPoints]\n1000,500,4,2,0,60,1,0\n2000,-50,4,2,0,60,0,0\n3000,400,4,2,0,60,1,0\n\n"
        + "[HitObjects]\n256,192,1000,1,0,0:0:0:0:\n100,100,2000,2,0,L|200:100|250:50,1,100\n256,192,3000,12,0,4000,0:0:0:0:\n";

    private static TransformOptions Same(double rate = 1, FlipMode? flip = null, string? audio = null)
        => new(rate, flip, 5, 4, 9, 8, audio);

    private static string[] Lines(string text) => text.Split('\n');

    [Test]
    public void Rate_DividesObjectTimesAndUninheritedBeatLengthOnly()
    {
        var lines = Lines(OsuTransform.Apply(Map, Same(rate: 2)));
        Assert.That(lines, Does.Contain("256,192,500,1,0,0:0:0:0:"));
        Assert.That(lines, Does.Contain("100,100,1000,2,0,L|200:100|250:50,1,100"));
        Assert.That(lines, Does.Contain("256,192,1500,12,0,2000,0:0:0:0:"));
        Assert.That(lines, Does.Contain("500,250,4,2,0,60,1,0"));
        Assert.That(lines, Does.Contain("1000,-50,4,2,0,60,0,0"));
        Assert.That(lines, Does.Contain("1500,200,4,2,0,60,1,0"));
    }

    [Test]
    public void Rate_RoundsAfterDivision_HalfAwayFromZero()
    {
        // 1000/1.5 = 666.67 -> 667; 2000/1.5 = 1333.3 -> 1333; 3000/1.5 = 2000.
        var lines = Lines(OsuTransform.Apply(Map, Same(rate: 1.5)));
        Assert.That(lines, Does.Contain("256,192,667,1,0,0:0:0:0:"));
        Assert.That(lines[Array.FindIndex(lines, l => l.StartsWith("100,100,"))], Does.StartWith("100,100,1333,"));
        Assert.That(lines, Does.Contain("667,333.333333,4,2,0,60,1,0"));
    }

    [Test]
    public void Rate_ScalesBreaksPreviewAndBookmarks()
    {
        var lines = Lines(OsuTransform.Apply(Map, Same(rate: 2)));
        Assert.That(lines, Does.Contain("PreviewTime: 1500"));
        Assert.That(lines, Does.Contain("Bookmarks: 500,1000,1501"));
        Assert.That(lines, Does.Contain("2,750,1250"));
        Assert.That(lines, Does.Contain("0,0,\"bg.jpg\",0,0"));
    }

    [Test]
    public void Rate_LeavesNegativePreviewTimeAlone()
    {
        var text = Map.Replace("PreviewTime: 3000", "PreviewTime: -1");
        Assert.That(Lines(OsuTransform.Apply(text, Same(rate: 2))), Does.Contain("PreviewTime: -1"));
    }

    [Test]
    public void Flip_MirrorsHeadsAndControlPoints()
    {
        var lines = Lines(OsuTransform.Apply(Map, Same(flip: FlipMode.Vertical)));
        Assert.That(lines, Does.Contain("256,192,1000,1,0,0:0:0:0:"));
        Assert.That(lines, Does.Contain("100,284,2000,2,0,L|200:284|250:334,1,100"));
        Assert.That(lines, Does.Contain("256,192,3000,12,0,4000,0:0:0:0:"));
    }

    [Test]
    public void FlipHorizontal_MirrorsX()
    {
        var lines = Lines(OsuTransform.Apply(Map.Replace("256,192,1000", "100,50,1000"), Same(flip: FlipMode.Horizontal)));
        Assert.That(lines, Does.Contain("412,50,1000,1,0,0:0:0:0:"));
        Assert.That(lines, Does.Contain("412,100,2000,2,0,L|312:100|262:50,1,100"));
        Assert.That(lines, Does.Contain("256,192,3000,12,0,4000,0:0:0:0:"));
    }

    [Test]
    public void FlipBoth_MirrorsXAndY()
    {
        var lines = Lines(OsuTransform.Apply(Map.Replace("256,192,1000", "100,50,1000"), Same(flip: FlipMode.Both)));
        Assert.That(lines, Does.Contain("412,334,1000,1,0,0:0:0:0:"));
        Assert.That(lines, Does.Contain("412,284,2000,2,0,L|312:284|262:334,1,100"));
    }

    [Test]
    public void Stats_WritesDifficulty_AddsMissingApproachRate()
    {
        var text = Map.Replace("ApproachRate:9\n", "");
        var lines = Lines(OsuTransform.Apply(text, new TransformOptions(1, null, 6.25, 5.5, 9.7, 3, null)));
        Assert.That(lines, Does.Contain("HPDrainRate:6.3"));
        Assert.That(lines, Does.Contain("CircleSize:5.5"));
        Assert.That(lines, Does.Contain("OverallDifficulty:3"));
        Assert.That(lines, Does.Contain("ApproachRate:9.7"));
        Assert.That(lines, Does.Contain("SliderMultiplier:1.4"));
    }

    [Test]
    public void Audio_ReplacesFilename()
    {
        var lines = Lines(OsuTransform.Apply(Map, Same(audio: "audio_1.5x.mp3")));
        Assert.That(lines, Does.Contain("AudioFilename: audio_1.5x.mp3"));
    }

    [Test]
    public void NoOp_IsByteIdentical()
    {
        Assert.That(OsuTransform.Apply(Map, Same()), Is.EqualTo(Map));
        var crlf = Map.Replace("\n", "\r\n");
        Assert.That(OsuTransform.Apply(crlf, Same()), Is.EqualTo(crlf));
    }

    [Test]
    public void Crlf_IsPreserved()
    {
        var crlf = Map.Replace("\n", "\r\n");
        var result = OsuTransform.Apply(crlf, new TransformOptions(2, FlipMode.Both, 6, 5, 10, 7, "a.mp3"));
        Assert.That(result.Replace("\r\n", ""), Does.Not.Contain("\n"));
        Assert.That(result.Split("\r\n").Length, Is.EqualTo(crlf.Split("\r\n").Length));
    }

    [Test]
    public void RoundTrip_DecodesWithMapLoader()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "simple.osu");
        var original = MapLoader.Load(File.ReadAllText(path));
        const double rate = 1.5;
        var output = OsuTransform.Apply(File.ReadAllText(path), new TransformOptions(rate, FlipMode.Vertical, 5, 4, 9, 8, null));
        var map = MapLoader.Load(output);

        Assert.That(map.Objects, Has.Count.EqualTo(original.Objects.Count));
        for (int i = 0; i < map.Objects.Count; i++)
        {
            Assert.That(map.Objects[i].EndTime, Is.EqualTo(original.Objects[i].EndTime / rate).Within(2), $"object {i}");
            Assert.That(map.Objects[i].Start.Y, Is.EqualTo(384 - original.Objects[i].Start.Y).Within(0.5), $"object {i}");
        }
    }
}
