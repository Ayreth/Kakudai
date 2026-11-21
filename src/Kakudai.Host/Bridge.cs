using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kakudai.Core;
using Kakudai.Core.Game;
using Kakudai.Core.Library;
using Kakudai.Core.Maps;
using Kakudai.Core.Settings;
using Kakudai.Core.Stable;
using Kakudai.Core.Training;
using Kakudai.Core.Updates;

namespace Kakudai.Host;

/// <summary>
/// Connects the page to the Core. Reads the page's messages and the game log, runs the work off the UI thread
/// (one call at a time, in the order the requests came in) and sends the answers back as JSON through
/// <c>post</c>, which must accept calls from any thread. Keeps the settings and profiles, saved to
/// <c>settings.json</c> 500 ms after the last change.
/// </summary>
/// <remarks>
/// Protocol (JSON, camelCase):
/// <list type="bullet">
/// <item>page → C#: <c>ready</c>; <c>window {action: minimize|close|drag|height, value?}</c>;
/// <c>settings {settings, mapId?}</c>; <c>profiles {profiles}</c>; <c>generate {mapId, settings}</c>;
/// <c>openUpdate</c> (opens the page of the release announced by <c>update</c>).</item>
/// <item>C# → page: <c>status {client, state}</c> (client "lazer", "torii" or "stable"; state waiting, detected,
/// notFound, unsupported, libraryError or denied — osu!stable runs but its memory cannot be read);
/// <c>map {id, artist, title, difficultyName, mapper, isKakudai,
/// background, base}</c>; <c>noMap</c>; <c>appSettings {current, profiles}</c>; <c>resolved {mapId, rate, bpm, hp,
/// cs, ar, od, version, nothingToChange, maxArOd}</c>; <c>stars {mapId, settings, value, map}</c> (star rating of the
/// difficulty these settings would generate, null if unknown, and of the map; 300 ms after the last settings
/// change); <c>update {version}</c> (a newer release, "2.1.0"); <c>generated {title, version}</c>;
/// <c>error {source, message}</c>.</item>
/// </list>
/// </remarks>
public sealed class Bridge : IDisposable
{
    /// <summary>
    /// The Windows window's background address, followed by the file key. Not under the page's own host: WebView2
    /// does not raise WebResourceRequested for a host mapped to a folder.
    /// </summary>
    public const string DefaultBackgroundPrefix = "https://kakudai-bg.local/";

    /// <summary>Where the page loads map backgrounds from, followed by the file key; the window serves them with
    /// <see cref="TryGetBackground"/>.</summary>
    public string BackgroundPrefix { get; init; } = DefaultBackgroundPrefix;

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The star rating is computed this long after the last settings change.</summary>
    private static readonly TimeSpan StarsDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The running version: <c>&lt;Version&gt;</c> of the app's project (Kakudai.App or Kakudai.Desktop, the
    /// entry assembly; not this library's).</summary>
    private static readonly Version AppVersion =
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Bridge).Assembly).GetName().Version ?? new Version(0, 0, 0);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // A NaN value must not make the whole message unserialisable; the page shows it as unknown.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly OsuLibrary _library;
    private readonly KakudaiService _service;
    private readonly LogWatcher _watcher;
    private readonly StableWatcher _stable;
    private readonly Action<string> _post;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Backgrounds of the maps sent to the page (key → file name and path): only these files are served.
    private readonly ConcurrentDictionary<string, (string Filename, string Path)> _backgrounds = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _settingsPath;
    private readonly System.Threading.Timer _saveTimer;
    private readonly Lock _settingsLock = new(); // _appSettings and _savePending
    private readonly Lock _saveGate = new();     // one write of settings.json at a time
    private AppSettings _appSettings;
    private bool _savePending;

    private volatile Status _status = new(null, "waiting");
    private volatile bool _libraryLoaded;
    private volatile bool _disposed;

    // Newest request not yet handled; older ones are dropped (coalescing).
    private GameSelection? _nextSelection;
    private StableSelection? _nextStable;
    private ResolveRequest? _nextResolve;

    // Last map sent to the page, which keeps showing it until another one arrives, the game it came from ("lazer",
    // "torii", "stable") and, for osu!stable, the running game (the new map is handed to it). Only used under _gate.
    private Guid? _shownMapId;
    private string? _shownClient;
    private StableGame? _shownStable;

    // Star rating: computed off the work queue, so it never holds up "resolved" or "generate". At most one
    // computation runs and one request waits (the newest); a newer request cancels the running one. All under _starsLock.
    private readonly Lock _starsLock = new();
    private readonly System.Threading.Timer _starsTimer;
    private StarsRequest? _starsPending;            // newest request, not started yet
    private bool _starsDue;                         // its delay is over: it starts as soon as nothing runs
    private CancellationTokenSource? _starsRunning; // the computation running, if any
    private long _starsSeq;                         // number of the newest request

    // Update banner: a newer release found at start, sent again to a page that (re)loads.
    private readonly UpdateChecker _updates = new(AppVersion);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile ReleaseInfo? _update;

    private sealed record Status(string? Client, string State);

    private sealed record ResolveRequest(string? MapId, TrainerSettings Settings);

    private sealed record StarsRequest(long Seq, string? MapId, TrainerSettings Settings);

    /// <summary>The page asks to minimise ("minimize"), close ("close") or move ("drag", while the mouse button is
    /// down) the window. Raised on the UI thread.</summary>
    public event Action<string>? WindowAction;

    /// <summary>The page asks for a window height, in CSS pixels (its content height). Raised on the UI thread.</summary>
    public event Action<HeightRequest>? HeightRequested;

    /// <summary>The page has (re)loaded and was sent the current state. Raised on the UI thread.</summary>
    public event Action? PageReady;

    /// <param name="dataDir">Lazer's (or Torii's) data folder read first, read only.</param>
    /// <param name="lazerDataDirs">Every data folder a game may use (<see cref="OsuPaths.LazerDataDirs()"/>): the one
    /// whose log was written last is followed, and its library read.</param>
    /// <param name="settingsPath">The settings file, normally <c>%LOCALAPPDATA%\Kakudai\settings.json</c>.</param>
    public Bridge(string dataDir, Func<IReadOnlyList<string>> lazerDataDirs, string tempDir, string outputDir, string settingsPath,
        Action<string> post)
    {
        _library = new OsuLibrary(dataDir, tempDir);
        _service = new KakudaiService(_library, outputDir, Path.Combine(tempDir, "audio"));
        // Every possible data folder is watched: the game played is the one whose log moves, wherever its data is.
        _watcher = new LogWatcher(lazerDataDirs, TimeSpan.FromMilliseconds(250));
        _stable = new StableWatcher(TimeSpan.FromMilliseconds(300));
        _post = post;
        _settingsPath = settingsPath;
        _appSettings = SettingsStore.Load(settingsPath);
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _starsTimer = new System.Threading.Timer(_ => OnStarsDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Reads lazer's base once (if there is one), then follows lazer's log and osu!stable's memory.</summary>
    public void Start()
    {
        // Queued first, so the watchers' first selection waits for it.
        Enqueue("detect", () =>
        {
            _service.DeleteAudioTemps(); // before any encode: what is left is from an interrupted run
            if (!File.Exists(Path.Combine(_library.DataDir, "client.realm"))) return; // no lazer here: osu!stable only, or not yet
            try
            {
                EnsureLibrary();
            }
            catch (LibraryUnavailableException)
            {
                SetStatus(null, "libraryError"); // tried again on the next selection
            }
        });
        _watcher.SelectionChanged += OnSelectionChanged;
        _watcher.Start();
        _stable.SelectionChanged += OnStableSelectionChanged;
        _stable.StateChanged += OnStableStateChanged;
        _stable.Start();
        _ = CheckForUpdateAsync();
    }

    /// <summary>Stops following the game and writes the settings still waiting to be saved.</summary>
    public void Dispose()
    {
        _disposed = true;
        _shutdown.Cancel();
        lock (_starsLock)
        {
            _starsPending = null;
            _starsRunning?.Cancel(); // the computation disposes its own token source when it stops
            _starsTimer.Dispose();
        }
        _watcher.Dispose();
        _stable.Dispose();
        _saveTimer.Dispose();
        _updates.Dispose();
        _shutdown.Dispose(); // the check took its token at start; a cancelled token stays usable
        SaveNow();
    }

    /// <summary>
    /// Handles a message of the page (the JSON of the object it posted). Called on the UI thread, which also runs
    /// the "window" actions synchronously.
    /// </summary>
    public void HandleMessage(string json)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(json);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (message.ValueKind != JsonValueKind.Object) return;
        switch (ReadString(message, "type"))
        {
            case "ready":
                OnReady();
                PageReady?.Invoke();
                break;
            case "window": OnWindow(message); break;
            case "settings": OnSettings(message); break;
            case "profiles": OnProfiles(message); break;
            case "generate": OnGenerate(message); break;
            case "openUpdate": OnOpenUpdate(); break;
        }
    }

    /// <summary>
    /// Finds the game file of a background the page was given. False for any other hash, which is checked to be
    /// hexadecimal before anything touches the disk.
    /// </summary>
    public bool TryGetBackground(string hash, [NotNullWhen(true)] out string? path, [NotNullWhen(true)] out string? contentType)
    {
        path = contentType = null;
        if (!IsHash(hash) || !_backgrounds.TryGetValue(hash, out var background)) return false;

        path = background.Path;
        contentType = Path.GetExtension(background.Filename).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
        return true;
    }

    // ------------------------------------------------------------------ page → host

    private void OnReady()
    {
        // The page may have missed what was sent before it was listening.
        var status = _status;
        Post(new { type = "status", client = status.Client, state = status.State });
        AppSettings settings;
        lock (_settingsLock) settings = _appSettings;
        Post(new { type = "appSettings", current = settings.Current, profiles = settings.Profiles });
        if (_service.Current is { } map) SendMap(map);
        else Post(new { type = "noMap" });
        if (_update is { } update) PostUpdate(update);
    }

    private void OnWindow(JsonElement message)
    {
        var action = ReadString(message, "action");
        switch (action)
        {
            case "height":
                if (ReadPositive(message, "value") is { } height)
                    HeightRequested?.Invoke(new HeightRequest(height, ReadPositive(message, "viewport"),
                        ReadPositive(message, "scale") ?? 1));
                break;
            case "minimize" or "close" or "drag":
                WindowAction?.Invoke(action);
                break;
        }
    }

    private void OnSettings(JsonElement message)
    {
        if (ReadSettings(message) is not { } settings)
        {
            PostError("settings", "Invalid settings.");
            return;
        }

        lock (_settingsLock) _appSettings = _appSettings with { Current = settings };
        ScheduleSave();

        var mapId = ReadString(message, "mapId");
        ScheduleStars(mapId, settings);
        Volatile.Write(ref _nextResolve, new ResolveRequest(mapId, settings));
        Enqueue("settings", () =>
        {
            // An earlier turn may already have taken this request; it only resolves the newest one.
            var request = Interlocked.Exchange(ref _nextResolve, null);
            if (request is null) return;

            // No map: the page got "noMap" and shows "–". Another map: the page is about to receive it and will
            // send its settings again.
            var current = _service.Current;
            if (current is null || (request.MapId is not null && !IsMap(request.MapId, current))) return;

            PostResolved(current.Id, request.Settings, _service.Resolve(request.Settings));
        });
    }

    private void OnProfiles(JsonElement message)
    {
        Profile?[]? profiles = null;
        try
        {
            if (message.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Array)
                profiles = p.Deserialize<Profile?[]>(SettingsStore.Json);
        }
        catch (JsonException)
        {
        }

        if (profiles is null || profiles.Any(x => x?.Settings is { } s && !SettingsStore.IsValid(s)))
        {
            PostError("profiles", "Invalid profiles.");
            return;
        }

        lock (_settingsLock) _appSettings = _appSettings with { Profiles = profiles! };
        ScheduleSave();
    }

    private void OnGenerate(JsonElement message)
    {
        if (ReadSettings(message) is not { } settings)
        {
            PostError("generate", "Invalid settings.");
            return;
        }

        var mapId = ReadString(message, "mapId");
        Enqueue("generate", () =>
        {
            var current = _service.Current;
            if (current is null)
            {
                PostError("generate", "No map selected.");
                return;
            }
            // The page generates what it shows: refuse if the game moved to another map in between.
            if (mapId is null || !IsMap(mapId, current))
            {
                PostError("generate", "The map changed, try again.");
                return;
            }

            string path;
            try
            {
                path = _service.Generate(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PostError("generate", ex.Message); // already "Could not write the .osz …" or "Could not convert the audio …"
                return;
            }

            var version = _service.Resolve(settings).Version; // the name Generate just wrote

            // The game the map came from imports it (stable: osu!.exe again with the file; lazer: its .osz program or
            // its IPC pipe).
            if (GameImport.Open(path, _shownClient, _shownStable) is { } failure)
            {
                PostError("generate", failure);
                return;
            }

            Post(new { type = "generated", title = current.Title, version });
        });
    }

    /// <summary>Opens, in the default browser, the release page found at start; the page never gives the address.</summary>
    private void OnOpenUpdate()
    {
        if (_update is not { } release || !UpdateChecker.IsReleasePage(release.Url)) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                PostError("update", $"The download page could not be opened: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------------ game → host

    // Raised on a thread-pool thread: only queues the work.
    private void OnSelectionChanged(GameSelection selection)
    {
        Volatile.Write(ref _nextSelection, selection);
        Enqueue("detect", ProcessSelection);
    }

    private void ProcessSelection()
    {
        var selection = Interlocked.Exchange(ref _nextSelection, null);
        if (selection is null) return;

        try
        {
            // The map comes from the data folder whose log announced it (moved data, Torii, another install…).
            if (selection.DataDir is { } dataDir && _library.UseDataDir(dataDir)) _libraryLoaded = false;
            EnsureLibrary();
            var map = _service.SelectByDisplay(selection.Display);
            if (map is null)
            {
                SetStatus(selection.Client, _service.IsOtherMode(selection.Display) ? "unsupported" : "notFound");
                RestoreShownMap();
                return;
            }

            SetStatus(selection.Client, "detected");
            ShowMap(map, selection.Client, null);
        }
        catch (UnsupportedMapException ex)
        {
            // An osu!standard map the decoder refuses: say precisely why (the mode itself is supported).
            SetStatus(selection.Client, "detected");
            PostError("detect", ex.Message);
            RestoreShownMap();
        }
        catch (LibraryUnavailableException)
        {
            SetStatus(selection.Client, "libraryError");
            RestoreShownMap();
        }
        catch
        {
            RestoreShownMap();
            throw;
        }
    }

    // Raised on a thread-pool thread: only queues the work.
    private void OnStableSelectionChanged(StableSelection selection)
    {
        Volatile.Write(ref _nextStable, selection);
        Enqueue("detect", ProcessStableSelection);
    }

    private void OnStableStateChanged(StableState state)
    {
        Enqueue("detect", () =>
        {
            var status = _status;
            if (state == StableState.Denied) SetStatus("stable", "denied");
            // osu!stable closed (or tosu stepped in): the "denied" line has nothing left to say.
            else if (status is { Client: "stable", State: "denied" })
                SetStatus(state == StableState.Reading ? "stable" : null, "waiting");
        });
    }

    /// <summary>The map osu!stable selected, read from its folder in Songs (lazer's base is not involved).</summary>
    private void ProcessStableSelection()
    {
        var selection = Interlocked.Exchange(ref _nextStable, null);
        if (selection is null) return;

        try
        {
            var entry = StableMaps.Load(selection, out var otherMode);
            if (entry is null)
            {
                SetStatus("stable", otherMode ? "unsupported" : "notFound");
                RestoreShownMap();
                return;
            }

            var map = _service.SelectExternal(entry);
            SetStatus("stable", "detected");
            ShowMap(map, "stable", selection.Game);
        }
        catch (UnsupportedMapException ex)
        {
            SetStatus("stable", "detected");
            PostError("detect", ex.Message);
            RestoreShownMap();
        }
        catch (LibraryUnavailableException)
        {
            SetStatus("stable", "notFound");
            RestoreShownMap();
        }
        catch
        {
            RestoreShownMap();
            throw;
        }
    }

    // ------------------------------------------------------------------ work

    private void EnsureLibrary()
    {
        if (_libraryLoaded) return;
        _library.Refresh();
        _libraryLoaded = true;
    }

    private void ShowMap(MapInfo map, string? client, StableGame? stable)
    {
        _shownMapId = map.Id;
        _shownClient = client;
        _shownStable = stable;
        SendMap(map);
    }

    /// <summary>
    /// The service drops its map whenever a selection fails, but the page keeps showing the last one: select it
    /// again so that what the page shows is what gets resolved and generated.
    /// </summary>
    private void RestoreShownMap()
    {
        if (_shownMapId is not { } id || _service.Current is not null) return;
        try
        {
            _service.Select(id);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or LibraryUnavailableException or UnsupportedMapException)
        {
            // The page will get "No map selected." if it generates this map.
        }
    }

    /// <summary>
    /// Asks for the star rating of what these settings would generate, <see cref="StarsDelay"/> after the last call.
    /// The request replaces the one still waiting and cancels the computation running, if any; it starts once its
    /// delay is over and nothing runs any more (so at most one computation runs, and one request waits). Off the
    /// work queue: "resolved" and "generate" never wait for it.
    /// </summary>
    private void ScheduleStars(string? mapId, TrainerSettings settings)
    {
        lock (_starsLock)
        {
            if (_disposed) return;
            _starsPending = new StarsRequest(++_starsSeq, mapId, settings);
            _starsDue = false;
            _starsRunning?.Cancel();
            _starsTimer.Change(StarsDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStarsDue()
    {
        lock (_starsLock) _starsDue = true;
        TryStartStars();
    }

    /// <summary>Starts the waiting request if its delay is over and no computation runs.</summary>
    private void TryStartStars()
    {
        StarsRequest request;
        CancellationTokenSource cts;
        lock (_starsLock)
        {
            if (_disposed || _starsRunning is not null || !_starsDue || _starsPending is null) return;
            request = _starsPending;
            _starsPending = null;
            _starsDue = false;
            cts = _starsRunning = new CancellationTokenSource();
        }
        _ = Task.Run(() => ComputeStars(request, cts));
    }

    private void ComputeStars(StarsRequest request, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            // No map: the page shows "–". Another map: the page is about to receive it and will send its settings again.
            var current = _service.Current;
            if (current is null || (request.MapId is not null && !IsMap(request.MapId, current))) return;

            double? value = null, map = null;
            Guid mapId = current.Id;
            try
            {
                var preview = _service.PreviewStars(request.Settings, token);
                (mapId, value, map) = (preview.MapId, preview.Value, preview.Map);
            }
            catch (UnsupportedMapException)
            {
                // The rating cannot be computed for this map: the page shows "–".
            }
            if (mapId != current.Id) return;

            // Checked and posted under the lock that numbers the requests: once a newer request exists, this answer
            // can no longer be posted, so an older answer never follows a newer one.
            lock (_starsLock)
            {
                if (token.IsCancellationRequested || request.Seq != _starsSeq) return;
                Post(new { type = "stars", mapId, settings = request.Settings, value, map });
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change, or closing.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The map was dropped meanwhile (the page gets "noMap" or another map), or invalid settings (refused by
            // OnSettings already).
        }
        finally
        {
            lock (_starsLock) _starsRunning = null;
            cts.Dispose();
            TryStartStars(); // a request that became due while this one ran
        }
    }

    /// <summary>Asks GitHub once whether a newer Kakudai exists; tells the page if so, says nothing otherwise.</summary>
    private async Task CheckForUpdateAsync()
    {
        var release = await _updates.CheckAsync(_shutdown.Token).ConfigureAwait(false); // never throws
        if (release is null || _disposed) return;
        _update = release;
        PostUpdate(release);
    }

    /// <summary>Saves the settings <see cref="SaveDelay"/> after the last call (each call restarts the delay).</summary>
    private void ScheduleSave()
    {
        lock (_settingsLock) _savePending = true;
        try
        {
            if (!_disposed && _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan)) return;
        }
        catch (ObjectDisposedException)
        {
        }
        SaveNow(); // closing: the timer will not fire any more
    }

    private void SaveNow()
    {
        lock (_saveGate)
        {
            AppSettings snapshot;
            lock (_settingsLock)
            {
                if (!_savePending) return;
                _savePending = false;
                snapshot = _appSettings;
            }

            try
            {
                SettingsStore.Save(_settingsPath, snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PostError("settings", $"Could not save the settings to \"{_settingsPath}\" ({ex.Message}).");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool after the work queued before it. The turn is taken here,
    /// on the caller's thread, so turns follow the order of the calls.
    /// </summary>
    private void Enqueue(string source, Action work)
    {
        if (_disposed) return;
        var turn = _gate.WaitAsync();
        _ = Task.Run(async () =>
        {
            await turn.ConfigureAwait(false);
            try
            {
                if (!_disposed) Run(source, work);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void Run(string source, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            PostError(source, Describe(ex));
        }
    }

    // ------------------------------------------------------------------ host → page

    private void SetStatus(string? client, string state)
    {
        _status = new Status(client, state);
        Post(new { type = "status", client, state });
    }

    private void SendMap(MapInfo map)
    {
        string? background = null;
        if (map.BackgroundHash is { } hash && IsHash(hash) && map.BackgroundPath is { } path)
        {
            _backgrounds[hash] = (map.BackgroundFilename ?? "", path);
            background = BackgroundPrefix + hash;
        }

        Post(new
        {
            type = "map",
            id = map.Id,
            artist = map.Artist,
            title = map.Title,
            difficultyName = map.DifficultyName,
            mapper = map.Mapper,
            isKakudai = map.IsKakudai,
            background,
            @base = map.Base,
        });
    }

    // The settings are echoed so the page can tell whether this answer is for what it shows now.
    private void PostResolved(Guid mapId, TrainerSettings settings, Resolved r) => Post(new
    {
        type = "resolved",
        mapId,
        settings,
        rate = r.Rate,
        bpm = r.Bpm,
        hp = r.Hp,
        cs = r.Cs,
        ar = r.Ar,
        od = r.Od,
        version = r.Version,
        nothingToChange = r.NothingToChange,
        maxArOd = Trainer.MaxArOd,
    });

    private void PostUpdate(ReleaseInfo release) => Post(new { type = "update", version = release.DisplayVersion });

    private void PostError(string source, string message) => Post(new { type = "error", source, message });

    private void Post(object message)
    {
        if (_disposed) return;
        _post(JsonSerializer.Serialize(message, Json));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Message for an error the page shows as is.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        LibraryUnavailableException => ex.Message, // already precise (database unreadable, no music, …)
        UnsupportedMapException => ex.Message, // already complete ("Unsupported map: …", "Nothing to change: …")
        KeyNotFoundException => "Map not found in the library.",
        ArgumentException => "Invalid settings.",
        _ => $"Something went wrong: {ex.Message}",
    };

    private static bool IsHash(string hash) => hash.Length is >= 2 and <= 128 && hash.All(char.IsAsciiHexDigit);

    private static bool IsMap(string mapId, MapInfo map) => Guid.TryParse(mapId, out var id) && id == map.Id;

    private static double? ReadPositive(JsonElement message, string name) =>
        message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
        && double.IsFinite(d) && d > 0 ? d : null;

    private static string? ReadString(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The message's <c>settings</c> object, or null if it is missing or invalid (no "NaN", no infinity).</summary>
    private static TrainerSettings? ReadSettings(JsonElement message)
    {
        if (!message.TryGetProperty("settings", out var element) || element.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            var settings = element.Deserialize<TrainerSettings>(SettingsStore.Json);
            return settings is not null && SettingsStore.IsValid(settings) ? settings : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A window height asked by the page.</summary>
/// <param name="Height">The page's content height, in CSS pixels.</param>
/// <param name="Viewport">The page's visible height (innerHeight) when it asked, in CSS pixels: with a system title
/// bar, the window's outer height minus this is the bar.</param>
/// <param name="Scale">devicePixelRatio: device pixels per CSS pixel.</param>
public sealed record HeightRequest(double Height, double? Viewport, double Scale);
