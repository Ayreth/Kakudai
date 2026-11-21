using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Kakudai.Core;
using Kakudai.Core.Game;
using Kakudai.Core.Library;
using Kakudai.Core.Stable;
using Kakudai.Core.Training;
using NUnit.Framework;

namespace Kakudai.Tests;

/// <summary>osu!stable: reading its memory, its Songs folder, tosu, Wine, and handing it the new map.</summary>
public class StableTests
{
    private string root = "";

    [SetUp]
    public void Up() => root = Path.Combine(Path.GetTempPath(), "kakudai-stable-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void Down()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

    /// <summary>A set folder of a Songs folder: the fixture map, its song, its background in a sub-folder.</summary>
    private string SongFolder(string osuText, string folder = "456 Tester - Test Song")
    {
        var dir = Path.Combine(root, "Songs", folder);
        Directory.CreateDirectory(Path.Combine(dir, "sb"));
        File.WriteAllText(Path.Combine(dir, "Tester - Test Song (Mapper) [Insane].osu"), osuText);
        File.WriteAllText(Path.Combine(dir, "audio.mp3"), "fake audio");
        File.WriteAllText(Path.Combine(dir, "sb", "bg.jpg"), "fake background");
        File.WriteAllText(Path.Combine(dir, "normal-hitclap.wav"), "fake sample");
        return dir;
    }

    private const string Checksum = "0123456789abcdef0123456789abcdef";

    // ---------------------------------------------------------------- Songs folder

    [Test]
    public void Load_ReadsTheMapAndEveryFileOfItsFolder()
    {
        var text = Fixture("simple.osu").Replace("0,0,\"bg.jpg\",0,0", "0,0,\"sb\\bg.jpg\",0,0");
        var dir = SongFolder(text);
        var osu = Path.Combine(dir, "Tester - Test Song (Mapper) [Insane].osu");

        var entry = StableMaps.Load(new StableSelection(Checksum, osu, null), out var otherMode)!;

        Assert.That(otherMode, Is.False);
        Assert.That(entry.Display, Is.EqualTo("Tester - Test Song (Mapper) [Insane]"));
        Assert.That(entry.OsuPath, Is.EqualTo(osu));
        Assert.That(entry.AudioFile, Is.EqualTo("audio.mp3"));
        Assert.That(entry.BackgroundFile, Is.EqualTo("sb/bg.jpg"), "a .osu's backslash becomes the set's \"/\"");
        Assert.That(entry.Tags, Is.EqualTo("test"));
        Assert.That(entry.Id, Is.EqualTo(new Guid(Convert.FromHexString(Checksum))));
        Assert.That(entry.Files.Select(f => f.Filename), Is.EquivalentTo(new[]
            { "Tester - Test Song (Mapper) [Insane].osu", "audio.mp3", "sb/bg.jpg", "normal-hitclap.wav" }));
        Assert.That(entry.Files.All(f => File.Exists(f.Path) && f.Hash.Length == 40 && f.Hash.All(char.IsAsciiHexDigit)));
    }

    [Test]
    public void Load_OtherMode_ReturnsNull()
    {
        var dir = SongFolder(Fixture("mania.osu"));
        var osu = Directory.GetFiles(dir, "*.osu").Single();
        Assert.That(StableMaps.Load(new StableSelection(Checksum, osu, null), out var otherMode), Is.Null);
        Assert.That(otherMode, Is.True);
    }

    [Test]
    public void Load_MissingFile_ThrowsLibraryUnavailable()
        => Assert.Throws<LibraryUnavailableException>(() =>
            StableMaps.Load(new StableSelection(Checksum, Path.Combine(root, "nope.osu"), null), out _));

    [Test]
    public void Service_GeneratesAnOszFromTheSongsFolder()
    {
        var dir = SongFolder(Fixture("simple.osu").Replace("0,0,\"bg.jpg\",0,0", "0,0,\"sb\\bg.jpg\",0,0"));
        var entry = StableMaps.Load(new StableSelection(Checksum, Directory.GetFiles(dir, "*.osu").Single(), null), out _)!;
        var library = new OsuLibrary([], Path.Combine(root, "no-lazer"));
        var service = new KakudaiService(library, Path.Combine(root, "generated"), Path.Combine(root, "audio"));

        var info = service.SelectExternal(entry);
        Assert.That(info.BackgroundPath, Is.EqualTo(Path.Combine(dir, "sb", "bg.jpg")));
        Assert.That(service.Select(entry.Id).Id, Is.EqualTo(entry.Id), "found again after a failed selection");

        var osz = service.Generate(new TrainerSettings(Factor: 2));
        using var zip = ZipFile.OpenRead(osz);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.That(names, Does.Contain("audio.mp3"));
        Assert.That(names, Does.Contain("normal-hitclap.wav"));
        var osu = names.Single(n => n.EndsWith(".osu"));
        using var reader = new StreamReader(zip.GetEntry(osu)!.Open());
        var written = reader.ReadToEnd();
        Assert.That(written, Does.Contain("BeatmapSetID:-1"), "a set of its own in osu!stable too");
        Assert.That(written, Does.Contain("kakudai"));
    }

    [Test]
    public void OsuHeader_ReadsGeneralMetadataAndBackground()
    {
        var header = OsuHeader.Parse("osu file format v14\r\n\r\n[General]\r\nAudioFilename: a b.ogg\r\nMode: 1\r\n\r\n"
                                     + "[Metadata]\r\nTitle:T\r\nArtist:A\r\nCreator:C\r\nVersion:V\r\nTags:x y\r\n\r\n"
                                     + "[Events]\r\n//Background and Video events\r\nVideo,0,\"v.mp4\"\r\n0,0,\"bg x.png\",0,0\r\n");
        Assert.That(header, Is.EqualTo(new OsuHeader(1, "A", "", "T", "", "C", "V", "x y", "a b.ogg", "bg x.png")));
    }

    // ---------------------------------------------------------------- memory

    /// <summary>A fake 32-bit osu!stable: the pattern, then [[base - 0xC]] → Beatmap with checksum, folder and file.</summary>
    private sealed class FakeMemory : IProcessMemory
    {
        private readonly byte[] _memory = new byte[0x4000];
        private const long Start = 0x10000;

        public FakeMemory(string checksum, string folder, string file)
        {
            // Pattern at 0x100, base - 0xC holds a pointer to a pointer to the Beatmap object at 0x1000.
            new byte[] { 0xF8, 0x01, 0x74, 0x04, 0x83, 0x65 }.CopyTo(_memory, 0x100);
            Int(0x100 - 0xC, Start + 0x200);
            Int(0x200, Start + 0x1000);
            Int(0x1000 + 0x6C, Start + 0x2000);
            Int(0x1000 + 0x78, Start + 0x2400);
            Int(0x1000 + 0x90, Start + 0x2800);
            String(0x2000, checksum);
            String(0x2400, folder);
            String(0x2800, file);
        }

        private void Int(int at, long value) => BinaryPrimitives.WriteInt32LittleEndian(_memory.AsSpan(at), (int)value);

        private void String(int at, string value)
        {
            Int(at + 4, value.Length);
            Encoding.Unicode.GetBytes(value).CopyTo(_memory, at + 8);
        }

        public bool Read(long address, Span<byte> buffer)
        {
            var offset = address - Start;
            if (offset < 0 || offset + buffer.Length > _memory.Length) return false;
            _memory.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
            return true;
        }

        public IEnumerable<MemoryRegion> Regions(long limit) => [new MemoryRegion(Start, _memory.Length)];

        public void Dispose()
        {
        }
    }

    private StableGame Game()
    {
        var gameDir = Path.Combine(root, "osu!");
        Directory.CreateDirectory(Path.Combine(root, "Songs"));
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "osu!.player.cfg"), $"Skin = Default\nBeatmapDirectory = {Path.Combine(root, "Songs")}\n");
        return new StableGame(Environment.ProcessId, gameDir, Path.Combine(gameDir, "osu!.exe"), null);
    }

    [Test]
    public void Reader_FollowsThePointersToTheSelectedMap()
    {
        var game = Game();
        var reader = new StableReader(() => game, _ => new FakeMemory(Checksum, "456 Tester - Test Song ", "map.osu"));

        var selection = reader.Read();

        Assert.That(reader.State, Is.EqualTo(StableState.Reading));
        Assert.That(selection, Is.EqualTo(new StableSelection(Checksum,
            Path.Combine(root, "Songs", "456 Tester - Test Song", "map.osu"), game)), "the folder's trailing space is dropped");
    }

    [Test]
    public void Reader_RefusesNamesThatLeaveTheSongsFolder()
    {
        var game = Game();
        var reader = new StableReader(() => game, _ => new FakeMemory(Checksum, "..", "map.osu"));
        Assert.That(reader.Read(), Is.Null);
        reader = new StableReader(() => game, _ => new FakeMemory(Checksum, "a", "..\\..\\x.osu"));
        Assert.That(reader.Read(), Is.Null);
    }

    [Test]
    public void Reader_SaysWhyItCannotRead()
    {
        var reader = new StableReader(() => null, _ => throw new InvalidOperationException());
        Assert.That(reader.Read(), Is.Null);
        Assert.That(reader.State, Is.EqualTo(StableState.NotRunning));

        var game = Game();
        reader = new StableReader(() => game, _ => throw new MemoryAccessDeniedException("no"));
        Assert.That(reader.Read(), Is.Null);
        Assert.That(reader.State, Is.EqualTo(StableState.Denied));
        Assert.That(reader.Game, Is.EqualTo(game), "the game is known even when its memory is not");
    }

    [Test]
    public void SongsFolder_FromTheCfgElseSongs()
    {
        var game = Game();
        Assert.That(game.FindSongsFolder(), Is.EqualTo(Path.Combine(root, "Songs")));
        File.Delete(Path.Combine(game.GameDir, "osu!.player.cfg"));
        Directory.CreateDirectory(Path.Combine(game.GameDir, "Songs"));
        Assert.That(game.FindSongsFolder(), Is.EqualTo(Path.Combine(game.GameDir, "Songs")));
    }

    [Test]
    public void Pattern_MatchesWithWildcards()
    {
        var pattern = StableReader.Pattern("C8 FF ?? ?? 81");
        Assert.That(StableReader.IndexOf(new byte[] { 0, 0xC8, 0xFF, 1, 2, 0x81 }, pattern), Is.EqualTo(1));
        Assert.That(StableReader.IndexOf(new byte[] { 0xC8, 0xFF, 1, 2, 0x80 }, pattern), Is.EqualTo(-1));
    }

    [Test]
    public void MapsLine_KeepsReadableRegionsOnly()
    {
        Assert.That(ProcMaps.ParseLine("00400000-00452000 r-xp 00000000 08:02 173521  /usr/bin/wine"),
            Is.EqualTo(new MemoryRegion(0x400000, 0x52000)));
        Assert.That(ProcMaps.ParseLine("7f00a000-7f00b000 ---p 00000000 00:00 0"), Is.Null);
        Assert.That(ProcMaps.ParseLine("7ffd1000-7ffd3000 r--p 00000000 00:00 0  [vvar]"), Is.Null);
        Assert.That(ProcMaps.ParseLine("garbage"), Is.Null);
    }

    // ---------------------------------------------------------------- tosu

    [Test]
    public void Tosu_DirectPathOfAStableMap()
    {
        var dir = SongFolder(Fixture("simple.osu"));
        var osu = Directory.GetFiles(dir, "*.osu").Single();
        var json = $$$"""{"client":"stable","beatmap":{"checksum":"{{{Checksum}}}"},"directPath":{"beatmapFile":{{{System.Text.Json.JsonSerializer.Serialize(osu)}}}}}""";
        Assert.That(TosuSource.Parse(json, null), Is.EqualTo(new StableSelection(Checksum, osu, null)));
    }

    [Test]
    public void Tosu_FoldersWhenThereIsNoDirectPath_AndNothingForLazer()
    {
        var dir = SongFolder(Fixture("simple.osu"));
        var songs = System.Text.Json.JsonSerializer.Serialize(Path.Combine(root, "Songs"));
        var json = $$$"""{"client":"stable","beatmap":{"checksum":"{{{Checksum}}}"},"folders":{"songs":{{{songs}}},"beatmap":"456 Tester - Test Song"},"files":{"beatmap":"Tester - Test Song (Mapper) [Insane].osu"}}""";
        Assert.That(TosuSource.Parse(json, null)?.OsuPath, Is.EqualTo(Directory.GetFiles(dir, "*.osu").Single()));
        Assert.That(TosuSource.Parse(json.Replace("\"stable\"", "\"lazer\""), null), Is.Null);
    }

    // ---------------------------------------------------------------- Wine

    [Test]
    public void Wine_PathsAndEnvironment()
    {
        Assert.That(WinePaths.IsWindowsPath(@"C:\osu!\osu!.exe"), Is.True);
        Assert.That(WinePaths.IsWindowsPath(@"\\?\D:\Songs"), Is.True);
        Assert.That(WinePaths.IsWindowsPath("/home/me/osu!/osu!.exe"), Is.False);
        Assert.That(WinePaths.ToWindows("/home/me/Kakudai/generated/a b.osz"), Is.EqualTo(@"Z:\home\me\Kakudai\generated\a b.osz"));
        Assert.That(StableGame.ParseEnviron("WINEPREFIX=/p\0A=b=c\0\0"),
            Is.EqualTo(new Dictionary<string, string> { ["WINEPREFIX"] = "/p", ["A"] = "b=c" }));
        Assert.That(StableGame.FindLoader(null), Is.EqualTo("wine"));
    }

    [Test, Platform("Linux")] // macOS file systems ignore case: nothing to resolve there
    public void Wine_DriveLettersGoThroughTheDosdevicesOfThePrefix()
    {
        var prefix = Path.Combine(root, "prefix");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "osu!", "Songs"));
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "c:"), "../drive_c");
        Assert.That(WinePaths.ToUnix(@"C:\OSU!\songs", prefix), Is.EqualTo(Path.Combine(prefix, "dosdevices", "c:", "osu!", "Songs")),
            "segments matched without regard to case, like Wine");
        Assert.That(WinePaths.ToUnix(@"Q:\x", prefix), Is.Null);
    }

    [Test, Platform("Linux")]
    public void Wine_FindsTheGameInAProcFolder()
    {
        var gameDir = Path.Combine(root, "osu!");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "osu!.exe"), "");
        var proc = Path.Combine(root, "proc");
        Directory.CreateDirectory(proc);
        File.WriteAllText(Path.Combine(proc, "comm"), "osu!.exe\n");
        File.WriteAllText(Path.Combine(proc, "cmdline"), Path.Combine(gameDir, "osu!.exe") + "\0");
        File.WriteAllText(Path.Combine(proc, "environ"), $"HOME={root}\0WINEPREFIX={root}/pfx\0WINELOADER=/opt/wine/bin/wine\0");

        var game = StableGame.FromProc(42, proc)!;

        Assert.That(game.GameDir, Is.EqualTo(gameDir));
        Assert.That(game.Wine!.Prefix, Is.EqualTo(root + "/pfx"));
        Assert.That(game.Wine.Loader, Is.EqualTo("/opt/wine/bin/wine"));
        var start = GameImport.WineStart(game.Wine, "/tmp/x.osz");
        Assert.That(start.FileName, Is.EqualTo("/opt/wine/bin/wine"));
        Assert.That(start.ArgumentList, Is.EqualTo(new[] { Path.Combine(gameDir, "osu!.exe"), @"Z:\tmp\x.osz" }));
        Assert.That(start.Environment["WINEPREFIX"], Is.EqualTo(root + "/pfx"));
    }

    // ---------------------------------------------------------------- lazer, paths

    [Test]
    public void LazerImportMessage_NamesTheGamesOwnVersion()
    {
        var version = typeof(osu.Game.IPC.ArchiveImportMessage).Assembly.GetName().Version!;
        Assert.That(GameImport.ImportMessageType(version),
            Is.EqualTo(typeof(osu.Game.IPC.ArchiveImportMessage).AssemblyQualifiedName));
    }

    [Test]
    public void LazerDataDirs_FollowStorageIni()
    {
        var moved = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(moved);
        Directory.CreateDirectory(Path.Combine(root, "osu"));
        Directory.CreateDirectory(Path.Combine(root, "osu-torii"));
        File.WriteAllText(Path.Combine(root, "osu", "storage.ini"), $"FullPath = {moved}\n");

        Assert.That(OsuPaths.LazerDataDirs(root),
            Is.EqualTo(new[] { moved, Path.Combine(root, "osu"), Path.Combine(root, "osu-torii") }));
    }

    [Test]
    public void LazerDataDirs_FollowAChainOfStorageIni_AndRunningPortableGames()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var portable = Path.Combine(root, "portable-lazer");
        foreach (var d in new[] { first, second, portable, Path.Combine(root, "osu") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(root, "osu", "storage.ini"), $"FullPath = {first}\n");
        File.WriteAllText(Path.Combine(first, "storage.ini"), $"FullPath = {second}\n"); // moved twice
        File.WriteAllText(Path.Combine(root, "osu", "storage.ini.bak"), "FullPath = nowhere\n");

        Assert.That(OsuPaths.LazerDataDirs(root, [portable, Path.Combine(root, "missing")]),
            Is.EqualTo(new[] { second, first, Path.Combine(root, "osu"), portable }));
    }

    [Test]
    public void Library_SwitchesDataFolder()
    {
        var library = new OsuLibrary(Path.Combine(root, "a"), Path.Combine(root, "tmp"));
        Assert.That(library.UseDataDir(Path.Combine(root, "a")), Is.False);
        Assert.That(library.UseDataDir(Path.Combine(root, "b")), Is.True);
        Assert.That(library.DataDir, Is.EqualTo(Path.Combine(root, "b")));
        Assert.That(library.FilePath("abcd"), Is.EqualTo(Path.Combine(root, "b", "files", "a", "ab", "abcd")));
    }
}
