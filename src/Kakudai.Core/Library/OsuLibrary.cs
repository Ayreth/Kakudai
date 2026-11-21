using Realms;
using Realms.Exceptions;

namespace Kakudai.Core.Library;

public sealed class LibraryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OsuLibrary
{
    private string _dataDir;
    private readonly string _tempDir;
    private IReadOnlyList<BeatmapEntry> _entries = Array.Empty<BeatmapEntry>();
    private IReadOnlyList<BeatmapEntry> _otherModes = Array.Empty<BeatmapEntry>();
    private readonly Lock _gate = new();
    private string? _copyPath;
    private readonly Func<BeatmapEntry, IReadOnlyList<NamedFile>>? _fileLoader;

    public OsuLibrary(string dataDir, string tempDir)
    {
        _dataDir = dataDir;
        _tempDir = tempDir;
    }

    /// <param name="fileLoader">Test seam: replaces the read of a set's files in the game base.</param>
    internal OsuLibrary(IEnumerable<BeatmapEntry> entries, string dataDir,
        Func<BeatmapEntry, IReadOnlyList<NamedFile>>? fileLoader = null,
        IEnumerable<BeatmapEntry>? otherModes = null)
    {
        _otherModes = otherModes?.ToList() ?? (IReadOnlyList<BeatmapEntry>)Array.Empty<BeatmapEntry>();
        _fileLoader = fileLoader;
        _dataDir = dataDir;
        _tempDir = "";
        _entries = entries.ToList();
    }

    public static string DefaultDataDir => Path.Combine(Game.OsuPaths.UserDataRoot, "osu");

    /// <summary>The game data folder read (client.realm, files/).</summary>
    public string DataDir => Volatile.Read(ref _dataDir);

    /// <summary>
    /// Reads another data folder from now on (the game played is another one, or its data moved): what was read from
    /// the previous one is dropped, <see cref="Refresh"/> has to run again. False if it is already this folder.
    /// </summary>
    public bool UseDataDir(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        lock (_gate)
        {
            if (string.Equals(Path.GetFullPath(dataDir), Path.GetFullPath(_dataDir),
                    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return false;
            Volatile.Write(ref _dataDir, dataDir);
            Volatile.Write(ref _entries, Array.Empty<BeatmapEntry>());
            Volatile.Write(ref _otherModes, Array.Empty<BeatmapEntry>());
            _copyPath = null;
            return true;
        }
    }

    /// <summary>The osu!standard difficulties of the game base (the only ones Kakudai can amplify).</summary>
    public IReadOnlyList<BeatmapEntry> Entries => Volatile.Read(ref _entries);

    /// <summary>The taiko, catch and mania difficulties, kept to tell "other mode" from "not found".</summary>
    public IReadOnlyList<BeatmapEntry> OtherModes => Volatile.Read(ref _otherModes);

    /// <summary>True if the game shows <paramref name="display"/> for a difficulty that is not osu!standard only.</summary>
    public bool IsOtherMode(string display) =>
        FindByDisplay(display) is null
        && OtherModes.Any(e => e.Display == display || e.DisplayUnicode == display);

    public void Refresh()
    {
        var source = Path.Combine(_dataDir, "client.realm");
        var copy = Path.Combine(_tempDir, "client.realm");
        var staging = Path.Combine(_tempDir, "client.realm.new");
        Exception? last = null;
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) Thread.Sleep(500);
                try
                {
                    Directory.CreateDirectory(_tempDir);
                    // The previous good copy stays in place until the new one has been read successfully.
                    File.Copy(source, staging, overwrite: true);
                    var (entries, others) = ReadAll(staging);
                    File.Move(staging, copy, overwrite: true);
                    Volatile.Write(ref _otherModes, others);
                    Volatile.Write(ref _entries, entries);
                    _copyPath = copy;
                    return;
                }
                catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    try { File.Delete(staging); } catch (Exception) { /* best effort */ }
                }
            }
        }
        throw new LibraryUnavailableException($"Could not read the osu! database ({source}).", last);
    }

    private static (List<BeatmapEntry> Osu, List<BeatmapEntry> Others) ReadAll(string copyPath)
    {
        var config = new RealmConfiguration(copyPath) { IsDynamic = true, IsReadOnly = true };
        using var realm = Realm.GetInstance(config);
        var result = new List<BeatmapEntry>();
        var others = new List<BeatmapEntry>();
        foreach (var b in realm.DynamicApi.All("Beatmap"))
        {
            var ruleset = b.DynamicApi.Get<IRealmObjectBase>("Ruleset");
            if (ruleset == null) continue;
            var isStandard = ruleset.DynamicApi.Get<string>("ShortName") == "osu";
            var set = b.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
            if (set == null || set.DynamicApi.Get<bool>("DeletePending")) continue;

            var meta = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
            var author = meta?.DynamicApi.Get<IRealmObjectBase>("Author");
            var diff = b.DynamicApi.Get<IRealmObjectBase>("Difficulty");

            (isStandard ? result : others).Add(new BeatmapEntry(
                b.DynamicApi.Get<Guid>("ID"),
                S(meta, "Artist"), S(meta, "ArtistUnicode"), S(meta, "Title"), S(meta, "TitleUnicode"),
                author?.DynamicApi.Get<string>("Username") ?? "",
                b.DynamicApi.Get<string>("DifficultyName") ?? "",
                b.DynamicApi.Get<string>("Hash") ?? "",
                b.DynamicApi.Get<double>("StarRating"),
                b.DynamicApi.Get<double>("BPM"),
                b.DynamicApi.Get<double>("Length"),
                diff?.DynamicApi.Get<float>("CircleSize") ?? 0,
                diff?.DynamicApi.Get<float>("ApproachRate") ?? 0,
                diff?.DynamicApi.Get<float>("OverallDifficulty") ?? 0,
                S(meta, "AudioFile"), S(meta, "BackgroundFile"), S(meta, "Tags"),
                Array.Empty<NamedFile>(),
                b.DynamicApi.Get<DateTimeOffset?>("LastPlayed"),
                set.DynamicApi.Get<DateTimeOffset>("DateAdded")));
        }
        return (result, others);
    }

    /// <summary>
    /// Returns <paramref name="entry"/> with its set's <see cref="BeatmapEntry.Files"/> filled in. Entries built by
    /// <see cref="Refresh"/> have no files (reading them for every map costs seconds), so call this for the one map
    /// that is really used. Reads the temp copy made by <see cref="Refresh"/>; never touches the game folder.
    /// </summary>
    /// <exception cref="LibraryUnavailableException">The base cannot be read.</exception>
    public BeatmapEntry WithFiles(BeatmapEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Files.Count > 0) return entry;
        if (_fileLoader is not null) return entry with { Files = _fileLoader(entry) };
        var unavailable = $"Could not read the files of the map {entry.Display}: the library has to be read again.";
        lock (_gate)
        {
            if (_copyPath is null) throw new LibraryUnavailableException(unavailable);
            try
            {
                var config = new RealmConfiguration(_copyPath) { IsDynamic = true, IsReadOnly = true };
                using var realm = Realm.GetInstance(config);
                var b = realm.DynamicApi.Find("Beatmap", entry.Id);
                var set = b?.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
                if (set == null) throw new LibraryUnavailableException(unavailable);
                var files = new List<NamedFile>();
                foreach (var f in set.DynamicApi.GetList<IRealmObjectBase>("Files"))
                {
                    var file = f.DynamicApi.Get<IRealmObjectBase>("File");
                    files.Add(new NamedFile(f.DynamicApi.Get<string>("Filename") ?? "", file?.DynamicApi.Get<string>("Hash") ?? ""));
                }
                if (files.Count == 0) throw new LibraryUnavailableException(unavailable);
                return entry with { Files = files };
            }
            catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException)
            {
                throw new LibraryUnavailableException(unavailable, ex);
            }
        }
    }

    private static string S(IRealmObjectBase? o, string name) => o?.DynamicApi.Get<string>(name) ?? "";

    public BeatmapEntry? FindByDisplay(string display)
    {
        var matches = _entries.Where(e => e.Display == display).ToList();
        if (matches.Count == 0) matches = _entries.Where(e => e.DisplayUnicode == display).ToList();
        if (matches.Count == 0) return null;
        if (matches.Any(e => !e.IsKakudai)) matches = matches.Where(e => !e.IsKakudai).ToList();
        return matches
            .OrderByDescending(e => e.LastPlayed.HasValue)
            .ThenByDescending(e => e.LastPlayed)
            .ThenByDescending(e => e.DateAdded)
            .First();
    }

    public IReadOnlyList<BeatmapEntry> Search(string query, int limit = 20) =>
        _entries.Where(e => e.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();

    public string FilePath(string hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 2)
            throw new ArgumentException("Invalid file hash (at least 2 characters).", nameof(hash));
        return Path.Combine(_dataDir, "files", hash[..1], hash[..2], hash);
    }
}
