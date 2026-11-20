using System.IO.Compression;
using Kakudai.Core.Export;
using Kakudai.Core.Library;
using NUnit.Framework;

namespace Kakudai.Tests;

public class OszBuilderTests
{
    private string dir = "";

    [SetUp] public void Up() => dir = Path.Combine(Path.GetTempPath(), "kakudai-osz-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Down() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    private string MakeFile(string name, string content)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Test]
    public void Build_ZipHasOsuAndFiles()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3", "bg.jpg", "gone.wav");
        var map = new Dictionary<string, string>
        {
            ["hash-audio.mp3"] = MakeFile("a", "A"),
            ["hash-bg.jpg"] = MakeFile("b", "B"),
            ["hash-gone.wav"] = Path.Combine(dir, "missing"),
        };
        var osz = OszBuilder.Build(e, "osu text", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out"));
        using (var zip = ZipFile.OpenRead(osz))
        {
            Assert.That(zip.Entries.Select(x => x.FullName), Is.EquivalentTo(new[]
            {
                "Tester - Test Song (Mapper) [2x spaced Insane] (kakudai).osu", "audio.mp3", "bg.jpg"
            }));
        }
        Assert.DoesNotThrow(() => OszBuilder.Build(e, "osu text", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out")));
    }

    [Test]
    public void Build_SanitizesFileNames()
    {
        var e = SetFilesTests.Entry("Tester", "Re:Zero / ?*\"", "audio.mp3");
        var map = new Dictionary<string, string> { ["hash-audio.mp3"] = MakeFile("a", "A") };
        var osz = OszBuilder.Build(e, "x", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out"));
        var invalid = Path.GetInvalidFileNameChars();
        Assert.That(Path.GetFileName(osz).IndexOfAny(invalid), Is.EqualTo(-1));
        using var zip = ZipFile.OpenRead(osz);
        foreach (var en in zip.Entries) Assert.That(en.FullName.IndexOfAny(invalid), Is.EqualTo(-1), en.FullName);
    }

    [Test]
    public void Build_KeepsSubfolders()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3", "bg/x.jpg");
        var map = new Dictionary<string, string>
        {
            ["hash-audio.mp3"] = MakeFile("a", "A"),
            ["hash-bg/x.jpg"] = MakeFile("b", "B"),
        };
        var osz = OszBuilder.Build(e, "x", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out"));
        using var zip = ZipFile.OpenRead(osz);
        Assert.That(zip.Entries.Select(x => x.FullName), Does.Contain("bg/x.jpg"));
    }

    [Test]
    public void Build_NeverLeavesArchiveRoot()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3", "../evil.wav", "a/../../b.wav", "/abs.wav");
        var map = new Dictionary<string, string> { ["hash-audio.mp3"] = MakeFile("a", "A") };
        foreach (var f in e.Files) map.TryAdd(f.Hash, MakeFile("f" + map.Count, "F"));
        var osz = OszBuilder.Build(e, "x", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out"));
        using var zip = ZipFile.OpenRead(osz);
        var names = zip.Entries.Select(x => x.FullName).ToList();
        Assert.That(names, Does.Contain("evil.wav").And.Contain("a/b.wav").And.Contain("abs.wav"));
        foreach (var n in names)
            Assert.That(n.Split('/'), Has.None.EqualTo("..").And.None.EqualTo(""), n);
    }

    [Test]
    public void Build_NewAudio_AddedAndOriginalLeftOut()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "Audio.mp3", "bg.jpg");
        var map = new Dictionary<string, string>
        {
            ["hash-Audio.mp3"] = Path.Combine(dir, "missing"), // never read: it is left out
            ["hash-bg.jpg"] = MakeFile("b", "B"),
        };
        var mp3 = MakeFile("stretched.mp3", "MP3");

        // The entry's audio file is "audio.mp3": the set's "Audio.mp3" is the same file.
        var osz = OszBuilder.Build(e, "x", "1.1x (132bpm) Insane", e.Files, h => map[h], Path.Combine(dir, "out"),
            new NewAudio("Audio 1.100x.mp3", mp3));

        using var zip = ZipFile.OpenRead(osz);
        Assert.That(zip.Entries.Select(x => x.FullName), Is.EquivalentTo(new[]
        {
            "Tester - Test Song (Mapper) [1.1x (132bpm) Insane] (kakudai).osu", "Audio 1.100x.mp3", "bg.jpg"
        }));
        using var reader = new StreamReader(zip.GetEntry("Audio 1.100x.mp3")!.Open());
        Assert.That(reader.ReadToEnd(), Is.EqualTo("MP3"));
    }

    [Test]
    public void Build_NewAudioUnreadable_SaysAudioCache()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3");
        var ex = Assert.Throws<LibraryUnavailableException>(() =>
            OszBuilder.Build(e, "x", "1.1x (132bpm) Insane", e.Files, _ => Path.Combine(dir, "missing"), Path.Combine(dir, "out"),
                new NewAudio("audio 1.100x.mp3", Path.Combine(dir, "gone.mp3"))));
        Assert.That(ex!.Message, Does.StartWith("Could not read the re-timed audio \"audio 1.100x.mp3\" from the audio cache ("));
        Assert.That(ex.Message, Does.Not.Contain("game folder"));
    }

    [Test]
    public void Build_GameFileUnreadable_SaysGameFolder()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3");
        var audio = MakeFile("a", "A");
        using var locked = new FileStream(audio, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ex = Assert.Throws<LibraryUnavailableException>(() =>
            OszBuilder.Build(e, "x", "2x spaced Insane", e.Files, _ => audio, Path.Combine(dir, "out")));
        Assert.That(ex!.Message, Does.StartWith("Could not read the file \"audio.mp3\" from the game folder ("));
    }

    [Test]
    public void Build_MissingAudioFile_Throws()
    {
        var e = SetFilesTests.Entry("Tester", "Test Song", "audio.mp3", "bg.jpg");
        var map = new Dictionary<string, string>
        {
            ["hash-audio.mp3"] = Path.Combine(dir, "missing"),
            ["hash-bg.jpg"] = MakeFile("b", "B"),
        };
        var ex = Assert.Throws<LibraryUnavailableException>(() =>
            OszBuilder.Build(e, "x", "2x spaced Insane", e.Files, h => map[h], Path.Combine(dir, "out")));
        Assert.That(ex!.Message, Is.EqualTo("The map's audio file is missing from the game folder."));
    }
}
