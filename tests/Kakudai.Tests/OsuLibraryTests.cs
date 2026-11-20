using Kakudai.Core.Library;
using NUnit.Framework;

namespace Kakudai.Tests;

public class OsuLibraryTests
{
    private static BeatmapEntry Make(string diff, string tags, DateTimeOffset? played, DateTimeOffset added, string title = "Song") =>
        new(Guid.NewGuid(), "Artist", "ArtistU", title, title + "U", "Mapper", diff, "hash", 5, 180, 100000,
            4, 9, 8, "a.mp3", "bg.jpg", tags, Array.Empty<NamedFile>(), played, added);

    [Test]
    public void Refresh_CorruptFile_ThrowsLibraryUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "kakudai-test-" + Guid.NewGuid());
        var data = Path.Combine(root, "data");
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(data);
        var bytes = new byte[1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(data, "client.realm"), bytes);
        try
        {
            var lib = new OsuLibrary(data, temp);
            Assert.Throws<LibraryUnavailableException>(() => lib.Refresh());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Test]
    public void FindByDisplay_PrefersMostRecentlyPlayed()
    {
        var now = DateTimeOffset.UtcNow;
        var old = Make("Hard", "", now.AddDays(-5), now.AddDays(-10));
        var recent = Make("Hard", "", now.AddDays(-1), now.AddDays(-20));
        var never = Make("Hard", "", null, now);
        var kak = Make("Hard", "kakudai", now, now);
        var lib = new OsuLibrary(new[] { old, never, kak, recent }, "d");
        Assert.That(lib.FindByDisplay(old.Display), Is.SameAs(recent));
        Assert.That(lib.FindByDisplay(old.DisplayUnicode), Is.SameAs(recent));
        Assert.That(lib.FindByDisplay("nope"), Is.Null);
    }

    [Test]
    public void IsOtherMode_OnlyForDifficultiesOfOtherModes()
    {
        var now = DateTimeOffset.UtcNow;
        var std = Make("Hard", "", null, now, "Standard");
        var mania = Make("4K Hard", "", null, now, "Mania");
        var lib = new OsuLibrary(new[] { std }, "d", otherModes: new[] { mania });
        Assert.That(lib.IsOtherMode(mania.Display), Is.True);
        Assert.That(lib.IsOtherMode(mania.DisplayUnicode), Is.True);
        Assert.That(lib.IsOtherMode(std.Display), Is.False);
        Assert.That(lib.IsOtherMode("nope"), Is.False);
        Assert.That(lib.Search("Mania"), Is.Empty);
        Assert.That(lib.FindByDisplay(mania.Display), Is.Null);
    }

    [Test]
    public void Search_IsCaseInsensitive()
    {
        var now = DateTimeOffset.UtcNow;
        var a = Make("Easy", "", null, now, "Hello World");
        var b = Make("Easy", "", null, now, "Other");
        var lib = new OsuLibrary(new[] { a, b }, "d");
        Assert.That(lib.Search("hello WORLD"), Is.EqualTo(new[] { a }));
        Assert.That(lib.Search("easy", 1), Has.Count.EqualTo(1));
    }

    [Test]
    public void Refresh_MissingFile_ThrowsLibraryUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "kakudai-test-" + Guid.NewGuid());
        var lib = new OsuLibrary(Path.Combine(root, "nodata"), Path.Combine(root, "temp"));
        Assert.Throws<LibraryUnavailableException>(() => lib.Refresh());
        try { Directory.Delete(root, true); } catch { }
    }

    [Test]
    public void FilePath_ShortHash_ThrowsArgumentException()
    {
        var lib = new OsuLibrary(Array.Empty<BeatmapEntry>(), "d");
        Assert.Throws<ArgumentException>(() => lib.FilePath("a"));
        Assert.Throws<ArgumentException>(() => lib.FilePath(""));
        Assert.That(lib.FilePath("abcdef"), Is.EqualTo(Path.Combine("d", "files", "a", "ab", "abcdef")));
    }

    [Test, Explicit]
    public void RealLibrary_FindsEntries()
    {
        var lib = new OsuLibrary(OsuLibrary.DefaultDataDir, Path.Combine(Path.GetTempPath(), "Kakudai"));
        lib.Refresh();
        Console.WriteLine($"Entries: {lib.Entries.Count}; first: {lib.Entries[0].Display}");
        Assert.That(lib.Entries.Count, Is.GreaterThan(0));
    }

    [Test, Explicit]
    public void RealLibrary_RefreshIsFast_AndWithFilesLoadsOneSet()
    {
        var lib = new OsuLibrary(OsuLibrary.DefaultDataDir, Path.Combine(Path.GetTempPath(), "Kakudai"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lib.Refresh();
        var refresh = sw.ElapsedMilliseconds;
        sw.Restart();
        var withFiles = lib.WithFiles(lib.Entries[0]);
        var files = sw.ElapsedMilliseconds;
        Console.WriteLine($"Refresh: {refresh} ms ({lib.Entries.Count} entries); WithFiles: {files} ms ({withFiles.Files.Count} files)");
        Assert.That(refresh, Is.LessThan(1500));
        Assert.That(lib.Entries[0].Files, Is.Empty);
        Assert.That(withFiles.Files, Is.Not.Empty);
        Assert.That(withFiles.Files.Any(f => f.Hash.Length >= 2 && File.Exists(lib.FilePath(f.Hash))), Is.True);
    }
}
