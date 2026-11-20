using System.Numerics;
using Kakudai.Core.Maps;
using NUnit.Framework;

namespace Kakudai.Tests;

public class MapLoaderTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

    private static LoadedMap LoadSimple() => MapLoader.Load(Fixture("simple.osu"));

    [Test]
    public void Load_Simple_ParsesSevenObjects()
    {
        var map = LoadSimple();

        Assert.That(map.Objects.Select(o => o.Kind), Is.EqualTo(new[]
        {
            ObjectKind.Circle, ObjectKind.Circle, ObjectKind.Slider, ObjectKind.Spinner,
            ObjectKind.Circle, ObjectKind.Circle, ObjectKind.Circle,
        }));
    }

    [Test]
    public void Load_Slider_EndAndSamples()
    {
        var slider = LoadSimple().Objects[2];

        Assert.Multiple(() =>
        {
            Assert.That(slider.Kind, Is.EqualTo(ObjectKind.Slider));
            Assert.That(slider.Start, Is.EqualTo(new Vector2(100, 100)));
            Assert.That(slider.End.X, Is.EqualTo(200).Within(0.01));
            Assert.That(slider.End.Y, Is.EqualTo(100).Within(0.01));
            Assert.That(slider.PathSamples, Has.Count.EqualTo(33));
            Assert.That(slider.PathSamples[0], Is.EqualTo(Vector2.Zero));
            Assert.That(slider.PathSamples[16].X, Is.EqualTo(50).Within(0.01));
            Assert.That(slider.PathSamples[16].Y, Is.EqualTo(0).Within(0.01));
            Assert.That(slider.PathSamples[32].X, Is.EqualTo(100).Within(0.01));
            Assert.That(slider.PathSamples[32].Y, Is.EqualTo(0).Within(0.01));
        });
    }

    [Test]
    public void Load_Times_ComeFromDecodedObjects()
    {
        var objects = LoadSimple().Objects;

        Assert.Multiple(() =>
        {
            Assert.That(objects[0].StartTime, Is.EqualTo(1000));
            Assert.That(objects[0].EndTime, Is.EqualTo(1000));
            // 100 px at SliderMultiplier 1.4 and 500 ms per beat: 100 / (140 / 500) ms.
            Assert.That(objects[2].StartTime, Is.EqualTo(2000));
            Assert.That(objects[2].EndTime, Is.EqualTo(2000 + 100 / 0.28).Within(0.01));
            Assert.That(objects[3].StartTime, Is.EqualTo(3000));
            Assert.That(objects[3].EndTime, Is.EqualTo(4000));
        });
    }

    [Test]
    public void Load_NonSliders_EndAtStartWithoutSamples()
    {
        var objects = LoadSimple().Objects;

        Assert.Multiple(() =>
        {
            foreach (var obj in objects.Where(o => o.Kind != ObjectKind.Slider))
            {
                Assert.That(obj.End, Is.EqualTo(obj.Start));
                Assert.That(obj.PathSamples, Is.Empty);
            }
            Assert.That(objects[1].Start, Is.EqualTo(new Vector2(306, 192)));
            Assert.That(objects[3].Start, Is.EqualTo(new Vector2(256, 192)));
        });
    }

    [Test]
    public void Load_LineIndex_PointsToSourceLine()
    {
        var map = LoadSimple();
        var slider = map.Objects[2];

        Assert.That(map.Lines[slider.LineIndex], Does.StartWith("100,100,2000"));
    }

    [Test]
    public void Load_LineIndex_PairsOutOfOrderLinesByTime()
    {
        var text = Fixture("simple.osu");
        var first = "256,192,1000,1,0,0:0:0:0:\n";
        var second = "306,192,1500,1,0,0:0:0:0:\n";
        var swapped = text.Replace(first + second, second + first);
        Assume.That(swapped, Is.Not.EqualTo(text));

        var map = MapLoader.Load(swapped);

        Assert.Multiple(() =>
        {
            Assert.That(map.Objects[0].StartTime, Is.EqualTo(1000));
            Assert.That(map.Lines[map.Objects[0].LineIndex], Does.StartWith("256,192,1000"));
            Assert.That(map.Lines[map.Objects[1].LineIndex], Does.StartWith("306,192,1500"));
        });
    }

    [Test]
    public void Load_FractionalCoordinates_AreTruncatedLikeTheGame()
    {
        var text = Fixture("simple.osu").Replace("306,192,1500,", "306.9,192.8,1500,");

        var map = MapLoader.Load(text);

        Assert.That(map.Objects[1].Start, Is.EqualTo(new Vector2(306, 192)));
    }

    [Test]
    public void Load_OffCentreSpinner_LoadsAtPlayfieldCentre()
    {
        var text = Fixture("simple.osu").Replace("256,192,3000,12,", "100,50,3000,12,");

        var spinner = MapLoader.Load(text).Objects[3];

        Assert.Multiple(() =>
        {
            Assert.That(spinner.Kind, Is.EqualTo(ObjectKind.Spinner));
            Assert.That(spinner.Start, Is.EqualTo(new Vector2(256, 192)));
        });
    }

    [Test]
    public void Load_BeatLengthAt_ReturnsTimingPoint()
        => Assert.That(LoadSimple().BeatLengthAt(1000), Is.EqualTo(500));

    [Test]
    public void MapLoader_MarksKiaiObjects()
    {
        // Kiai from 2000 to 4000 ms: bit 0 of the effects field (8th field), set on an inherited point at 2000 and
        // cleared at 4000; an extra circle at 2500 inside it.
        var text = Fixture("simple.osu")
            .Replace("1000,500,4,2,0,60,1,0\n",
                "1000,500,4,2,0,60,1,0\n2000,-100,4,2,0,60,0,1\n4000,-100,4,2,0,60,0,0\n")
            .Replace("256,192,3000,12,", "400,300,2500,1,0,0:0:0:0:\n256,192,3000,12,");

        var objects = MapLoader.Load(text).Objects;
        Assume.That(objects.Select(o => o.StartTime), Is.EqualTo(new[] { 1000, 1500, 2000, 2500, 3000, 4500, 4625, 4750 }));

        Assert.Multiple(() =>
        {
            Assert.That(objects.Select(o => o.InKiai),
                Is.EqualTo(new[] { false, false, true, true, true, false, false, false }));
            Assert.That(LoadSimple().Objects.Any(o => o.InKiai), Is.False, "la map sans kiai");
        });
    }

    [Test]
    public void Load_DifficultyName_IsVersion()
        => Assert.That(LoadSimple().DifficultyName, Is.EqualTo("Insane"));

    [Test]
    public void Load_Lines_RejoinToOriginalText()
    {
        var text = Fixture("simple.osu");

        Assert.That(string.Join("\n", LoadSimple().Lines), Is.EqualTo(text));
    }

    [Test]
    public void Load_CrlfText_KeepsCarriageReturnsAndRejoins()
    {
        var text = Fixture("simple.osu").Replace("\n", "\r\n");

        var map = MapLoader.Load(text);

        Assert.Multiple(() =>
        {
            Assert.That(string.Join("\n", map.Lines), Is.EqualTo(text));
            Assert.That(map.Lines[map.Objects[2].LineIndex], Is.EqualTo("100,100,2000,2,0,L|200:100,1,100\r"));
            Assert.That(map.Objects, Has.Count.EqualTo(7));
        });
    }

    [Test]
    public void Load_TextWithByteOrderMark_LoadsAndRejoins()
    {
        var text = "﻿" + Fixture("simple.osu");

        var map = MapLoader.Load(text);

        Assert.Multiple(() =>
        {
            Assert.That(map.Objects, Has.Count.EqualTo(7));
            Assert.That(string.Join("\n", map.Lines), Is.EqualTo(text));
        });
    }

    [Test]
    public void Load_MissingMode_DefaultsToStandard()
    {
        var text = Fixture("simple.osu").Replace("Mode: 0\n", "");
        Assume.That(text, Does.Not.Contain("Mode:"));

        Assert.That(MapLoader.Load(text).Objects, Has.Count.EqualTo(7));
    }

    [Test]
    public void Load_ManiaMap_ThrowsUnsupported()
        => Assert.Throws<UnsupportedMapException>(() => MapLoader.Load(Fixture("mania.osu")));

    [Test]
    public void Load_UnknownObjectType_ThrowsUnsupported()
    {
        // Type 128 is a mania hold note: not a circle, slider or spinner.
        var text = Fixture("simple.osu").TrimEnd('\n') + "\n256,192,5000,128,0,5500:0:0:0:0:\n";

        Assert.Throws<UnsupportedMapException>(() => MapLoader.Load(text));
    }

    [Test]
    public void EnsureRegistered_IsIdempotent()
    {
        Assert.DoesNotThrow(() =>
        {
            OsuGameInit.EnsureRegistered();
            OsuGameInit.EnsureRegistered();
        });
    }
}
