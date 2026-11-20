using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Kakudai.Core;
using Kakudai.Core.Library;
using Kakudai.Core.Maps;
using Kakudai.Core.Training;
using NUnit.Framework;

namespace Kakudai.Tests;

public class KakudaiServiceTests
{
    private string root = "";
    private string dataDir = "";
    private string outDir = "";
    private string audioDir = "";

    private static readonly TrainerSettings Spaced2 = new(Factor: 2);

    [SetUp]
    public void Up()
    {
        root = Path.Combine(Path.GetTempPath(), "kakudai-service-" + Guid.NewGuid().ToString("N"));
        dataDir = Path.Combine(root, "data");
        outDir = Path.Combine(root, "generated");
        audioDir = Path.Combine(root, "audio");
    }

    [TearDown]
    public void Down()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

    /// <summary>Stores a file in the fake game data folder, under its SHA-256 like the game does.</summary>
    private string Put(string content) => Put(Encoding.UTF8.GetBytes(content));

    private string Put(byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var path = Path.Combine(dataDir, "files", hash[..1], hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return hash;
    }

    private BeatmapEntry Entry(string osuText, byte[]? audio = null)
    {
        var osuHash = Put(osuText);
        var files = new List<NamedFile>
        {
            new("Tester - Test Song (Mapper) [Insane].osu", osuHash),
            new("audio.mp3", audio is null ? Put("fake audio") : Put(audio)),
            new("bg.jpg", Put("fake background")),
        };
        return new BeatmapEntry(Guid.NewGuid(), "Tester", "Tester", "Test Song", "Test Song", "Mapper", "Insane",
            osuHash, 4.5, 120, 3750, 4, 9, 8, "audio.mp3", "bg.jpg", "test", files, null, DateTimeOffset.UtcNow);
    }

    private KakudaiService Service(BeatmapEntry entry) => Service(new OsuLibrary(new[] { entry }, dataDir));

    private KakudaiService Service(OsuLibrary library) => new(library, outDir, audioDir);

    /// <summary>A real song for BASS to decode: 6 s of 16-bit stereo 44.1 kHz silence with a few clicks, as WAV.</summary>
    private static byte[] Wav()
    {
        const int rate = 44100, channels = 2, frames = rate * 6;
        var pcm = new short[frames * channels];
        foreach (int ms in new[] { 1000, 3000, 5000 })
            for (int i = 0; i < rate / 200; i++)
                for (int c = 0; c < channels; c++)
                    pcm[(rate * ms / 1000 + i) * channels + c] = (short)(0.8 * short.MaxValue * Math.Cos(2 * Math.PI * 2000 * i / rate));

        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = pcm.Length * sizeof(short);
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * sizeof(short));
            w.Write((short)(channels * sizeof(short)));
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (short v in pcm) w.Write(v);
        }
        return stream.ToArray();
    }

    [Test]
    public void Select_LoadsFilesOfSelectedEntryOnly()
    {
        var full = Entry(Fixture("simple.osu"));
        var bare = full with { Files = Array.Empty<NamedFile>() };
        var other = bare with { Id = Guid.NewGuid() };
        var loaded = new List<Guid>();
        var library = new OsuLibrary(new[] { bare, other }, dataDir, e =>
        {
            loaded.Add(e.Id);
            return full.Files;
        });
        var service = Service(library);

        var info = service.Select(bare.Id);

        Assert.That(loaded, Is.EqualTo(new[] { bare.Id }));
        Assert.That(info.BackgroundFilename, Is.EqualTo("bg.jpg"));
        Assert.That(info.BackgroundHash, Is.EqualTo(full.Files[2].Hash));
        var osz = service.Generate(Spaced2);
        using var zip = System.IO.Compression.ZipFile.OpenRead(osz);
        Assert.That(zip.Entries.Select(e => e.Name), Does.Contain("audio.mp3").And.Contain("bg.jpg"));
    }

    [Test]
    public void Select_WithoutFilesAndNoSource_ThrowsLibraryUnavailable()
    {
        var bare = Entry(Fixture("simple.osu")) with { Files = Array.Empty<NamedFile>() };
        var service = Service(bare);

        Assert.Throws<LibraryUnavailableException>(() => service.Select(bare.Id));
        Assert.That(service.Current, Is.Null);
    }

    [Test]
    public void Generate_AudioMissingFromFiles_Throws()
    {
        var full = Entry(Fixture("simple.osu"));
        var noAudio = full with { Files = full.Files.Where(f => f.Filename != "audio.mp3").ToList() };
        var service = Service(noAudio);
        service.Select(noAudio.Id);

        var ex = Assert.Throws<LibraryUnavailableException>(() => service.Generate(Spaced2));
        Assert.That(ex!.Message, Does.Contain("audio.mp3"));
    }

    [Test]
    public void SelectByDisplay_OtherModeIsNotFoundButRecognised()
    {
        var std = Entry(Fixture("simple.osu"));
        var mania = std with { Id = Guid.NewGuid(), DifficultyName = "4K Hard" };
        var service = Service(new OsuLibrary(new[] { std }, dataDir, null, new[] { mania }));

        Assert.That(service.IsOtherMode(mania.Display), Is.True);
        Assert.That(service.IsOtherMode(std.Display), Is.False);
    }

    [Test]
    public void Generate_UnreadableGameFile_IsLibraryErrorNotWriteError()
    {
        var entry = Entry(Fixture("simple.osu"));
        File.Delete(Path.Combine(dataDir, "files", entry.Files[1].Hash[..1], entry.Files[1].Hash[..2], entry.Files[1].Hash));
        var service = Service(entry);
        service.Select(entry.Id);

        var ex = Assert.Throws<LibraryUnavailableException>(() => service.Generate(Spaced2));
        Assert.That(ex!.Message, Does.Contain("audio"));
    }

    [Test]
    public void SelectByDisplay_LoadsFilesOfSelectedEntry()
    {
        var full = Entry(Fixture("simple.osu"));
        var bare = full with { Files = Array.Empty<NamedFile>() };
        var service = Service(new OsuLibrary(new[] { bare }, dataDir, _ => full.Files));

        var info = service.SelectByDisplay(bare.Display);

        Assert.That(info!.BackgroundHash, Is.EqualTo(full.Files[2].Hash));
    }

    [Test]
    public void Service_SelectThenGenerate()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);

        var info = service.Select(entry.Id);

        Assert.That(info.Id, Is.EqualTo(entry.Id));
        Assert.That((info.Artist, info.Title, info.DifficultyName, info.Mapper), Is.EqualTo(("Tester", "Test Song", "Insane", "Mapper")));
        Assert.That(info.BackgroundFilename, Is.EqualTo("bg.jpg"));
        Assert.That(info.BackgroundHash, Is.EqualTo(entry.Files[2].Hash));
        Assert.That(info.IsKakudai, Is.False);
        Assert.That(service.Current, Is.SameAs(info));

        var osz = service.Generate(Spaced2);

        Assert.That(File.Exists(osz), Is.True);
        Assert.That(Path.GetDirectoryName(osz), Is.EqualTo(outDir));
        using var zip = ZipFile.OpenRead(osz);
        const string osuName = "Tester - Test Song (Mapper) [2x spaced Insane] (kakudai).osu";
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { osuName, "audio.mp3", "bg.jpg" }));
        using var reader = new StreamReader(zip.GetEntry(osuName)!.Open());
        var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.That(lines, Does.Contain("Version:2x spaced Insane").And.Contain("AudioFilename: audio.mp3"));
        Assert.That(lines, Does.Contain("256,192,1000,1,0,0:0:0:0:"), "times untouched at rate 1");
        Assert.That(Directory.Exists(audioDir), Is.False, "no audio work at rate 1");
    }

    [Test]
    public void Service_Resolve_UsesSelectedMapBase()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);

        var info = service.Select(entry.Id);
        var resolved = service.Resolve(new TrainerSettings(Factor: 2, Rate: 1.5, ScaleAr: false, ScaleOd: false, Od: 9));

        Assert.That(info.Base, Is.EqualTo(new MapBase(5, 4, 9, 8, 120, false, "Insane")));
        Assert.That(resolved.Rate, Is.EqualTo(1.5));
        Assert.That(resolved.Bpm, Is.EqualTo(180).Within(1e-9));
        Assert.That((resolved.Hp.Map, resolved.Cs.Map, resolved.Ar.Map, resolved.Od.Map), Is.EqualTo((5.0, 4.0, 9.0, 8.0)));
        Assert.That(resolved.Od.Final, Is.EqualTo(9));
        Assert.That(resolved.Version, Is.EqualTo("2x spaced 1.5x (180bpm) OD9 Insane"));
        Assert.That(resolved.NothingToChange, Is.False);
    }

    [Test]
    public void Service_FactorClamped()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        Assert.That(service.Resolve(new TrainerSettings(Factor: 5)).Version, Is.EqualTo("3x spaced Insane"));
        Assert.That(service.Resolve(new TrainerSettings(Factor: 0.2)).NothingToChange, Is.True);
        Assert.That(service.Resolve(new TrainerSettings(Factor: 1.52, CrushStreams: true)).Version,
            Is.EqualTo("1.5x spaced (no streams) Insane"));
        Assert.That(() => service.Resolve(new TrainerSettings(Factor: double.NaN)), Throws.ArgumentException);
        Assert.That(() => service.Resolve(new TrainerSettings(Rate: double.PositiveInfinity)), Throws.ArgumentException);
    }

    [Test]
    public void Service_NoMap_Throws()
    {
        var service = Service(Entry(Fixture("simple.osu")));

        Assert.That(service.Current, Is.Null);
        Assert.That(() => service.Resolve(Spaced2),
            Throws.InvalidOperationException.With.Message.EqualTo("No map selected."));
        Assert.That(() => service.Generate(Spaced2),
            Throws.InvalidOperationException.With.Message.EqualTo("No map selected."));
    }

    [Test]
    public void Service_Generate_NothingToChange_Throws()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var ex = Assert.Throws<UnsupportedMapException>(() => service.Generate(new TrainerSettings(Factor: 1)));

        Assert.That(ex!.Message, Does.StartWith("Nothing to change: "));
        Assert.That(Directory.Exists(outDir), Is.False);
    }

    [Test]
    public void Service_KiaiOnly_NoKiai_Throws()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var ex = Assert.Throws<UnsupportedMapException>(() => service.Generate(new TrainerSettings(Factor: 2, KiaiOnly: true)));

        Assert.That(ex!.Message, Does.Contain("This map has no kiai"));
        Assert.That(Directory.Exists(outDir), Is.False);
    }

    [Test]
    public void Service_KiaiOnly_KiaiWithNoObject_Throws()
    {
        // A kiai section after the last object: the timing points have the kiai bit, but no object starts in kiai.
        var entry = Entry(Fixture("simple.osu").Replace("1000,500,4,2,0,60,1,0", "1000,500,4,2,0,60,1,0\n6000,-100,4,2,0,60,0,1"));
        var service = Service(entry);
        service.Select(entry.Id);

        Assert.That(service.Current!.Base.HasKiai, Is.False);
        var ex = Assert.Throws<UnsupportedMapException>(() => service.Generate(new TrainerSettings(Factor: 2, KiaiOnly: true)));
        Assert.That(ex!.Message, Does.Contain("This map has no kiai"));
    }

    [Test]
    public void Service_KiaiOnly_WithKiai_Generates()
    {
        var entry = Entry(Fixture("simple.osu").Replace("1000,500,4,2,0,60,1,0", "1000,500,4,2,0,60,1,1"));
        var service = Service(entry);
        service.Select(entry.Id);

        Assert.That(service.Current!.Base.HasKiai, Is.True);
        Assert.That(OsuInZip(service.Generate(new TrainerSettings(Factor: 2, KiaiOnly: true))).Split('\n'),
            Does.Contain("Version:2x spaced (kiai) Insane"));
    }

    [Test]
    public void Service_Generate_RateOnly_ContainsMp3AndNoOriginalAudio()
    {
        var entry = Entry(Fixture("simple.osu"), Wav());
        var service = Service(entry);
        service.Select(entry.Id);

        var osz = service.Generate(new TrainerSettings(Factor: 1, Rate: 1.25));

        using var zip = ZipFile.OpenRead(osz);
        const string osuName = "Tester - Test Song (Mapper) [1.25x (150bpm) Insane] (kakudai).osu";
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { osuName, "audio 1.250x.mp3", "bg.jpg" }));
        Assert.That(zip.GetEntry("audio 1.250x.mp3")!.Length, Is.GreaterThan(1000));
        using var reader = new StreamReader(zip.GetEntry(osuName)!.Open());
        var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.That(lines, Does.Contain("AudioFilename: audio 1.250x.mp3"));
        Assert.That(lines, Does.Contain("256,192,800,1,0,0:0:0:0:"), "times divided by the rate");
        Assert.That(lines, Does.Contain("Version:1.25x (150bpm) Insane"));
        Assert.That(Directory.GetFiles(audioDir, "*.mp3"), Has.Length.EqualTo(1), "encoded into the audio cache");
    }

    [Test]
    public void Service_Generate_AllAtOnce_SpacingFlipRateHr()
    {
        // The whole pipeline in one go, on a map with a slider and a stream: spacing (original times), then flip,
        // rate and stats.
        var original = Fixture("simple.osu");
        var entry = Entry(original, Wav());
        var service = Service(entry);
        service.Select(entry.Id);

        var text = OsuInZip(service.Generate(new TrainerSettings(Factor: 2, FlipMap: true, FlipMode: Kakudai.Core.Osu.FlipMode.Both,
            Rate: 1.25, EmulateHr: true)));

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.That(lines, Does.Contain("Version:2x spaced 1.25x (150bpm) HR hvflip Insane"), "order of the name parts");
        Assert.That(lines, Does.Contain("AudioFilename: audio 1.250x.mp3"));

        var before = MapLoader.Load(original).Objects;
        var after = MapLoader.Load(text).Objects;
        Assert.That(after, Has.Count.EqualTo(before.Count));
        Assert.Multiple(() =>
        {
            for (int i = 0; i < before.Count; i++)
            {
                Assert.That(after[i].Kind, Is.EqualTo(before[i].Kind), $"object {i}");
                Assert.That(after[i].StartTime,
                    Is.EqualTo(Math.Round(before[i].StartTime / 1.25, MidpointRounding.AwayFromZero)), $"time of object {i}");
                if (after[i].Kind == ObjectKind.Spinner)
                    Assert.That(after[i].EndTime,
                        Is.EqualTo(Math.Round(before[i].EndTime / 1.25, MidpointRounding.AwayFromZero)), $"spinner end {i}");

                // Heads, tails and every point of the slider path stay on the playfield.
                var points = new[] { after[i].Start, after[i].End }.Concat(after[i].PathSamples.Select(p => after[i].Start + p));
                foreach (var p in points)
                {
                    Assert.That(p.X, Is.InRange(0f, 512f), $"x of object {i}");
                    Assert.That(p.Y, Is.InRange(0f, 384f), $"y of object {i}");
                }
            }
        });

        // The control points written in the slider line too (L|x:y), not only the decoded path.
        var hitObjects = lines.SkipWhile(l => l != "[HitObjects]").Skip(1).Where(l => l.Length > 0).ToList();
        var slider = hitObjects.Single(l => l.Split(',')[3] == "2").Split(',');
        foreach (var point in slider[5].Split('|').Skip(1))
        {
            var xy = point.Split(':').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.That(xy[0], Is.InRange(0, 512), "curve point x");
            Assert.That(xy[1], Is.InRange(0, 384), "curve point y");
        }

        // The stream (the last three circles, 1/4 apart) is spaced, not crushed: each gap is wider than the map's.
        for (int i = before.Count - 2; i < before.Count; i++)
        {
            var was = System.Numerics.Vector2.Distance(before[i - 1].Start, before[i].Start);
            var now = System.Numerics.Vector2.Distance(after[i - 1].Start, after[i].Start);
            Assert.That(now, Is.GreaterThan(was * 1.2), $"stream gap {i - 1} -> {i}: {was} -> {now}");
        }
    }

    [Test]
    public void Service_Generate_FlipAndLockedStats_WritesFlipAndStats()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var lines = OsuInZip(service.Generate(new TrainerSettings(Factor: 1, FlipMap: true, Ar: 9.5, ArLocked: true)))
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.That(lines, Does.Contain("Version:vflip AR9.5 Insane"));
        // Flip changes no stat: only the locked AR differs from the map.
        Assert.That(lines, Does.Contain("ApproachRate:9.5").And.Contain("CircleSize:4").And.Contain("HPDrainRate:5"));
        Assert.That(lines, Does.Contain("256,192,1000,1,0,0:0:0:0:").And.Contain("300,184,4500,1,0,0:0:0:0:"));
    }

    [Test]
    public void Service_Generate_EmulateHrOverLockedAr_WritesHrStatsUnflipped()
    {
        var entry = Entry(Fixture("simple.osu"));   // HP 5, CS 4, OD 8, AR 9
        var service = Service(entry);
        service.Select(entry.Id);

        var lines = OsuInZip(service.Generate(new TrainerSettings(Factor: 1, EmulateHr: true, Ar: 8, ArLocked: true)))
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // AR 8 x 1.4 = 11.2 -> 10, the same as the automatic HR value (9 x 1.4 -> 10): no AR part in the name.
        Assert.That(lines, Does.Contain("Version:HR Insane"));
        Assert.That(lines, Does.Contain("ApproachRate:10").And.Contain("CircleSize:5.2").And.Contain("HPDrainRate:7")
            .And.Contain("OverallDifficulty:10"));
        Assert.That(lines, Does.Contain("256,192,1000,1,0,0:0:0:0:"), "Emulate HR does not flip");
    }

    [Test]
    public void SelectByDisplay_Found_SelectsWithoutRefresh()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);

        var info = service.SelectByDisplay("Tester - Test Song (Mapper) [Insane]");

        Assert.That(info?.Id, Is.EqualTo(entry.Id));
    }

    [Test]
    public void SelectByDisplay_UnknownAndLibraryUnavailable_Propagates()
    {
        var library = new OsuLibrary(Path.Combine(root, "nodata"), Path.Combine(root, "temp"));
        var service = Service(library);

        Assert.Throws<LibraryUnavailableException>(() => service.SelectByDisplay("nope"));
    }

    [Test]
    public void Select_GarbageFile_ThrowsUnsupportedAndClearsCurrent()
    {
        var good = Entry(Fixture("simple.osu"));
        var garbage = Entry("");
        var service = Service(new OsuLibrary(new[] { good, garbage }, dataDir));
        service.Select(good.Id);

        var ex = Assert.Throws<UnsupportedMapException>(() => service.Select(garbage.Id));

        Assert.That(ex!.Message, Does.StartWith("Unsupported map"));
        Assert.That(service.Current, Is.Null);
    }

    private static string OsuInZip(string osz)
    {
        using var zip = ZipFile.OpenRead(osz);
        using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.Ordinal)).Open());
        return reader.ReadToEnd();
    }

    [Test]
    public void Select_Again_UsesNewMap()
    {
        var a = Entry(Fixture("simple.osu"));
        var b = Entry(Fixture("simple.osu").Replace("Version:Insane", "Version:Hard").Replace("CircleSize:4", "CircleSize:3"));
        var service = Service(new OsuLibrary(new[] { a, b }, dataDir));

        service.Select(a.Id);
        service.Resolve(Spaced2);
        service.Select(b.Id);
        var second = service.Resolve(Spaced2);

        Assert.That(second.Version, Is.EqualTo("2x spaced Hard"));
        Assert.That(second.Cs.Map, Is.EqualTo(3));
        Assert.That(OsuInZip(service.Generate(Spaced2)).Split('\n'), Does.Contain("Version:2x spaced Hard"));
    }

    [Test]
    public void Generate_CrushStreams_WritesOsz()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var osz = service.Generate(new TrainerSettings(Factor: 1.5, CrushStreams: true));

        Assert.That(OsuInZip(osz).Split('\n'), Does.Contain("Version:1.5x spaced (no streams) Insane"));
    }

    [Test]
    public void Select_FileOpenForWritingElsewhere_StillReads()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        var path = Path.Combine(dataDir, "files", entry.OsuHash[..1], entry.OsuHash[..2], entry.OsuHash);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.That(service.Select(entry.Id).Base.DifficultyName, Is.EqualTo("Insane"));
    }

    [Test]
    public void Generate_PatchFails_ThrowsReadableError()
    {
        // No [Metadata] section: the map loads, but the patcher has nowhere to write the new name.
        var fixture = Fixture("simple.osu");
        int from = fixture.IndexOf("[Metadata]", StringComparison.Ordinal);
        var entry = Entry(fixture.Remove(from, fixture.IndexOf("[Difficulty]", StringComparison.Ordinal) - from));
        var service = Service(entry);
        service.Select(entry.Id);

        var ex = Assert.Throws<UnsupportedMapException>(() => service.Generate(Spaced2));
        Assert.That(ex!.Message, Does.StartWith("Unsupported map: the new difficulty could not be created"));
    }

    [Test]
    public void Generate_OutputNotWritable_ThrowsReadableError()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);
        Directory.CreateDirectory(root);
        File.WriteAllText(outDir, "a file where the output folder should be");

        var ex = Assert.Throws<IOException>(() => service.Generate(Spaced2));
        Assert.That(ex!.Message, Does.StartWith("Could not write the .osz"));
    }

    // ------------------------------------------------------------------ star rating of the difficulty to generate

    private static readonly TrainerSettings Identity = new(Factor: 1);

    /// <summary>
    /// The fixture's header with 96 circles at 1/2 beats (180 BPM) on a small pentagon around the centre: jumps that
    /// spacing can still widen inside the playfield, enough objects for the star rating to follow.
    /// </summary>
    private static string Jumps()
    {
        var fixture = Fixture("simple.osu");
        var head = fixture[..fixture.IndexOf("[TimingPoints]", StringComparison.Ordinal)];
        var text = new StringBuilder(head);
        text.Append("[TimingPoints]\n1000,333.333333333333,4,2,0,60,1,0\n\n[HitObjects]\n");
        for (int i = 0; i < 96; i++)
        {
            double angle = i * 2 * 2 * Math.PI / 5; // every other corner of the pentagon: a star
            int x = (int)Math.Round(256 + 50 * Math.Cos(angle)), y = (int)Math.Round(192 + 50 * Math.Sin(angle));
            text.Append(FormattableString.Invariant($"{x},{y},{1000 + (int)Math.Round(i * 333.333333333333 / 2)},{(i == 0 ? 5 : 1)},0,0:0:0:0:\n"));
        }
        return text.ToString();
    }

    [Test]
    public void PreviewStars_IdentitySettings_GiveTheMapStars()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var preview = service.PreviewStars(Identity);

        Assert.That(preview.MapId, Is.EqualTo(entry.Id));
        Assert.That(preview.Map, Is.EqualTo(Stars.Calculate(Fixture("simple.osu")).Total).Within(1e-9));
        Assert.That(preview.Value, Is.EqualTo(preview.Map));
    }

    [Test]
    public void PreviewStars_ChangesWithSpacing()
    {
        var entry = Entry(Jumps());
        var service = Service(entry);
        service.Select(entry.Id);

        var map = service.PreviewStars(Identity).Map;
        var spaced = service.PreviewStars(Spaced2).Value!.Value;
        var spacedMore = service.PreviewStars(new TrainerSettings(Factor: 3)).Value!.Value;

        TestContext.Out.WriteLine($"map {map:0.00}, x2 {spaced:0.00}, x3 {spacedMore:0.00}");
        Assert.That(spaced, Is.GreaterThan(map + 0.01));
        Assert.That(spacedMore, Is.GreaterThan(spaced + 0.01));
    }

    [Test]
    public void PreviewStars_ChangesWithRate()
    {
        var entry = Entry(Jumps());
        var service = Service(entry);
        service.Select(entry.Id);

        var map = service.PreviewStars(Identity).Map;
        var faster = service.PreviewStars(Identity with { Rate = 1.5 }).Value!.Value;
        var slower = service.PreviewStars(Identity with { Rate = 0.75 }).Value!.Value;

        TestContext.Out.WriteLine($"map {map:0.00}, 1.5x {faster:0.00}, 0.75x {slower:0.00}");
        Assert.That(faster, Is.GreaterThan(map + 0.01));
        Assert.That(slower, Is.LessThan(map - 0.01));
    }

    [Test]
    public void PreviewStars_IsTheStarsOfWhatGenerateWrites()
    {
        var entry = Entry(Fixture("simple.osu"), Wav());
        var service = Service(entry);
        service.Select(entry.Id);
        var settings = new TrainerSettings(Factor: 2, Rate: 1.2, FlipMap: true, EmulateHr: true, Ar: 9.5);

        var preview = service.PreviewStars(settings);

        var written = OsuInZip(service.Generate(settings));
        Assert.That(preview.Value, Is.EqualTo(Stars.Calculate(written).Total).Within(1e-9));
        Assert.That(preview.Value, Is.Not.EqualTo(preview.Map));
    }

    [Test]
    public void PreviewStars_WritesNothing()
    {
        var entry = Entry(Fixture("simple.osu"), Wav());
        var service = Service(entry);
        service.Select(entry.Id);

        service.PreviewStars(Spaced2 with { Rate = 1.3 });

        Assert.That(Directory.Exists(outDir), Is.False);
        Assert.That(Directory.Exists(audioDir), Is.False);
    }

    [Test]
    public void PreviewStars_KiaiOnlyOnAMapWithoutKiai_IsUnknown()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);

        var preview = service.PreviewStars(Spaced2 with { KiaiOnly = true });

        Assert.That(preview.Value, Is.Null);
        Assert.That(preview.Map, Is.GreaterThan(0));
    }

    [Test]
    public void PreviewStars_Cancelled_Throws()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);
        service.Select(entry.Id);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws(Is.InstanceOf<OperationCanceledException>(), () => service.PreviewStars(Spaced2, cts.Token));
        // The cancelled first use kept nothing: the next request computes the map's rating normally.
        var preview = service.PreviewStars(Identity);
        Assert.That(preview.Map, Is.EqualTo(Stars.Calculate(Fixture("simple.osu")).Total).Within(1e-9));
    }

    [Test]
    public void PreviewStars_NoMap_Throws()
    {
        var entry = Entry(Fixture("simple.osu"));
        var service = Service(entry);

        Assert.Throws<InvalidOperationException>(() => service.PreviewStars(Spaced2));
    }
}
