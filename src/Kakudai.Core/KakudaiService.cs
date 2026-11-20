using System.Text;
using Kakudai.Core.Amplify;
using Kakudai.Core.Audio;
using Kakudai.Core.Export;
using Kakudai.Core.Library;
using Kakudai.Core.Maps;
using Kakudai.Core.Osu;
using Kakudai.Core.Settings;
using Kakudai.Core.Training;

namespace Kakudai.Core;

/// <summary>The selected map: what the banner shows, and the values the trainer starts from.</summary>
public sealed record MapInfo(Guid Id, string Artist, string Title, string DifficultyName, string Mapper,
    bool IsKakudai, string? BackgroundHash, string? BackgroundFilename, MapBase Base);

/// <summary>Star ratings of a map and of the difficulty some settings would make of it.</summary>
/// <param name="MapId">The map they were computed for.</param>
/// <param name="Value">The difficulty <see cref="KakudaiService.Generate"/> would write; the map's own when there is
/// nothing to change; null when it cannot be generated ("kiai only" on a map without kiai).</param>
/// <param name="Map">The map as it is, by the same calculator.</param>
public sealed record StarsPreview(Guid MapId, double? Value, double Map);

/// <summary>
/// Selects a map of the library, resolves trainer settings against it and generates the .osz. Calls are serialised
/// by an internal lock; <see cref="Current"/> and <see cref="PreviewStars"/> do not wait for it.
/// </summary>
public sealed class KakudaiService
{
    private const double MinFactor = 1, MaxFactor = 3;

    private readonly OsuLibrary _library;
    private readonly string _outputDir;
    private readonly string _audioCacheDir;
    private readonly Lock _gate = new();

    private MapInfo? _current;
    private Selection? _selection;

    /// <param name="MapStars">The map's own star rating, computed on first use.</param>
    private sealed record Selection(BeatmapEntry Entry, LoadedMap Map, MapBase Base, MapStarsCache MapStars);

    /// <summary>
    /// The map's star rating, kept once computed. A cancelled computation keeps nothing: the next caller starts again,
    /// so a stale request never finishes this work for nothing.
    /// </summary>
    private sealed class MapStarsCache(string osuText)
    {
        private readonly Lock _gate = new();
        private double? _value;

        public double Get(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _value ??= Unsupported(() => Stars.Calculate(osuText, cancellationToken).Total,
                    "the star rating could not be computed");
                return _value.Value;
            }
        }
    }

    /// <param name="audioCacheDir">Where re-timed songs are encoded and kept; by default
    /// <c>%TEMP%\Kakudai\audio</c> (never under the game folder).</param>
    public KakudaiService(OsuLibrary library, string outputDir, string? audioCacheDir = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(outputDir);
        _library = library;
        _outputDir = outputDir;
        _audioCacheDir = audioCacheDir ?? Path.Combine(Path.GetTempPath(), "Kakudai", "audio");
    }

    public MapInfo? Current => Volatile.Read(ref _current);

    /// <summary>Deletes the partial encodes an interrupted run left in the audio cache. Call it once at start.</summary>
    public void DeleteAudioTemps()
    {
        lock (_gate)
            AudioStretcher.DeleteTemps(_audioCacheDir);
    }

    /// <summary>
    /// Selects the map the game shows as <paramref name="display"/>, re-reading the library once if it is not
    /// there (it may have just been imported). Returns null if it still is not found. Whatever happens, the
    /// previous selection is dropped.
    /// </summary>
    /// <exception cref="LibraryUnavailableException">The game base or the map file cannot be read.</exception>
    /// <exception cref="UnsupportedMapException">The map is not one Kakudai can amplify.</exception>
    public MapInfo? SelectByDisplay(string display)
    {
        ArgumentNullException.ThrowIfNull(display);
        lock (_gate)
        {
            Clear();
            var entry = _library.FindByDisplay(display);
            if (entry is null)
            {
                _library.Refresh();
                entry = _library.FindByDisplay(display);
            }

            return entry is null ? null : Load(entry);
        }
    }

    /// <summary>True if the game shows <paramref name="display"/> for a taiko, catch or mania difficulty.</summary>
    public bool IsOtherMode(string display) => _library.IsOtherMode(display);

    /// <summary>Selects a map of the library by its ID. Whatever happens, the previous selection is dropped.</summary>
    /// <exception cref="KeyNotFoundException">No map of the library has this ID.</exception>
    /// <exception cref="LibraryUnavailableException">The map file cannot be read.</exception>
    /// <exception cref="UnsupportedMapException">The map is not one Kakudai can amplify.</exception>
    public MapInfo Select(Guid id)
    {
        lock (_gate)
        {
            Clear();
            var entry = _library.Entries.FirstOrDefault(e => e.Id == id)
                        ?? throw new KeyNotFoundException("Map not found in the library.");
            return Load(entry);
        }
    }

    /// <summary>
    /// What <paramref name="settings"/> give on the selected map: rate, BPM, stats and the name of the new
    /// difficulty. The spacing factor is kept within [1, 3] and rounded to 0.05 first, as <see cref="Generate"/> does.
    /// </summary>
    /// <exception cref="InvalidOperationException">No map is selected.</exception>
    /// <exception cref="ArgumentException">A number of <paramref name="settings"/> is not finite.</exception>
    public Resolved Resolve(TrainerSettings settings)
    {
        lock (_gate)
            return Trainer.Resolve(Selected().Base, Normalize(settings));
    }

    /// <summary>
    /// Writes the selected map, changed by <paramref name="settings"/>, as a .osz in the output folder and returns its
    /// path (it is not opened). Spacing first (original times), then flip, rate and stats (Emulate HR included); with a rate, the song is
    /// re-timed into the audio cache and replaces the original one in the .osz.
    /// </summary>
    /// <exception cref="InvalidOperationException">No map is selected.</exception>
    /// <exception cref="ArgumentException">A number of <paramref name="settings"/> is not finite.</exception>
    /// <exception cref="UnsupportedMapException">Nothing to change, no kiai for "kiai only", or the new difficulty
    /// cannot be built (message for the player).</exception>
    /// <exception cref="IOException">The song cannot be re-timed, or the .osz cannot be written (complete message
    /// for the player).</exception>
    /// <exception cref="LibraryUnavailableException">A file of the game cannot be read (complete message for the player).</exception>
    public string Generate(TrainerSettings settings)
    {
        lock (_gate)
        {
            var selection = Selected();
            var s = Normalize(settings);
            var resolved = Trainer.Resolve(selection.Base, s);
            if (resolved.NothingToChange)
                throw new UnsupportedMapException("Nothing to change: the difficulty would be the same as the map's.");

            // Spacing 1 without "accentuate angles" moves nothing: the amplifier is not run at all.
            bool amplify = s.Factor != 1 || s.AccentAngles;
            if (amplify && s.KiaiOnly && !selection.Base.HasKiai)
                throw new UnsupportedMapException("This map has no kiai: turn off \"Kiai only\".");

            var entry = selection.Entry;
            var audio = FindAudio(entry);
            bool retime = resolved.Rate != 1 && audio is not null;
            var newAudioName = retime ? AudioStretcher.OutputName(entry.AudioFile, resolved.Rate, s.ChangePitch) : null;

            var text = BuildText(selection, s, resolved, amplify, newAudioName, CancellationToken.None);

            NewAudio? newAudio = null;
            if (retime)
            {
                var source = _library.FilePath(audio!.Hash);
                if (!File.Exists(source))
                    throw new LibraryUnavailableException("The map's audio file is missing from the game folder.");
                newAudio = new NewAudio(newAudioName!, AudioStretcher.Stretch(source, resolved.Rate, s.ChangePitch, _audioCacheDir));
            }

            try
            {
                // A file without a valid hash cannot be found in the game folder anyway.
                var files = SetFiles.ToInclude(entry, text).Where(f => f.Hash.Length >= 2).ToList();
                return OszBuilder.Build(entry, text, resolved.Version, files, _library.FilePath, _outputDir, newAudio);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not write the .osz to \"{_outputDir}\" ({ex.Message}).", ex);
            }
        }
    }

    /// <summary>
    /// The star rating of the difficulty <see cref="Generate"/> would write with <paramref name="settings"/> (spacing,
    /// flip, rate and stats; the song does not count), and of the map. Builds the same .osu text as Generate, in
    /// memory: nothing is written. Does not wait for <see cref="Generate"/> or <see cref="Resolve"/>, and can run
    /// while they do; it takes the selection as it is when called.
    /// </summary>
    /// <exception cref="InvalidOperationException">No map is selected.</exception>
    /// <exception cref="ArgumentException">A number of <paramref name="settings"/> is not finite.</exception>
    /// <exception cref="UnsupportedMapException">The difficulty or its star rating cannot be computed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public StarsPreview PreviewStars(TrainerSettings settings, CancellationToken cancellationToken = default)
    {
        var selection = Volatile.Read(ref _selection) ?? throw new InvalidOperationException("No map selected.");
        var s = Normalize(settings);
        var resolved = Trainer.Resolve(selection.Base, s);
        var id = selection.Entry.Id;

        var map = selection.MapStars.Get(cancellationToken);
        if (resolved.NothingToChange) return new StarsPreview(id, map, map);

        bool amplify = s.Factor != 1 || s.AccentAngles;
        if (amplify && s.KiaiOnly && !selection.Base.HasKiai) return new StarsPreview(id, null, map); // Generate refuses

        // The song's new name only, so that the text is the one Generate writes; nothing is encoded.
        var entry = selection.Entry;
        var newAudioName = resolved.Rate != 1 && !string.IsNullOrEmpty(entry.AudioFile)
            ? AudioStretcher.OutputName(entry.AudioFile, resolved.Rate, s.ChangePitch)
            : null;
        var text = BuildText(selection, s, resolved, amplify, newAudioName, cancellationToken);
        var value = Unsupported(() => Stars.Calculate(text, cancellationToken).Total, "the star rating could not be computed");
        return new StarsPreview(id, value, map);
    }

    /// <summary>
    /// The new difficulty as .osu text: spacing first (original times), then flip, rate and stats. Pure: reads the
    /// selection, writes nothing. <paramref name="cancellationToken"/> is checked between the stages.
    /// </summary>
    private static string BuildText(Selection selection, TrainerSettings s, Resolved resolved, bool amplify, string? newAudioName,
        CancellationToken cancellationToken)
        => Unsupported(() =>
        {
            var map = selection.Map;
            cancellationToken.ThrowIfCancellationRequested();
            var result = amplify
                ? Amplifier.Amplify(map.Objects, map.BeatLengthAt,
                    new AmplifyOptions(s.Factor, !s.CrushStreams, s.AccentAngles, s.KiaiOnly))
                : Unchanged(map);
            cancellationToken.ThrowIfCancellationRequested();
            var patched = OsuPatcher.Patch(map, result, resolved.Version);
            cancellationToken.ThrowIfCancellationRequested();
            return OsuTransform.Apply(patched, new TransformOptions(resolved.Rate, s.FlipMap ? s.FlipMode : null, resolved.Hp.Final,
                resolved.Cs.Final, resolved.Ar.Final, resolved.Od.Final, newAudioName));
        }, "the new difficulty could not be created");

    /// <summary>
    /// Decodes a .osu text. Any failure of the osu! decoder (not only the cases <see cref="MapLoader"/> checks
    /// itself) becomes an <see cref="UnsupportedMapException"/> with a message for the player.
    /// </summary>
    internal static LoadedMap LoadMap(string osuText)
        => Unsupported(() => MapLoader.Load(osuText), "the .osu file could not be read");

    /// <summary>Reads a game file as UTF-8, even while the game has it open, for writing included.</summary>
    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The settings as they are applied: the spacing factor within [1, 3], rounded to 0.05.</summary>
    private static TrainerSettings Normalize(TrainerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!SettingsStore.IsValid(settings))
            throw new ArgumentException("Invalid settings.", nameof(settings));
        return settings with { Factor = Math.Round(Math.Clamp(settings.Factor, MinFactor, MaxFactor) * 20) / 20 };
    }

    /// <summary>Every object on its original spot: what the amplifier gives when nothing has to move.</summary>
    private static AmplifyResult Unchanged(LoadedMap map)
        => new(map.Objects.Select(o => new Placement(o.Start, 0)).ToList(), 0, 0, 0, 0);

    /// <summary>
    /// The song in the set's files, or null if the map names none. A song named but missing would make a silent
    /// .osz, so it is refused.
    /// </summary>
    private static NamedFile? FindAudio(BeatmapEntry entry)
    {
        if (string.IsNullOrEmpty(entry.AudioFile)) return null;
        return entry.Files.FirstOrDefault(f => f.Hash.Length >= 2
                                               && string.Equals(f.Filename, entry.AudioFile, StringComparison.OrdinalIgnoreCase))
               ?? throw new LibraryUnavailableException($"The map's audio file \"{entry.AudioFile}\" is missing from the library: the .osz would have no music.");
    }

    private Selection Selected() => _selection ?? throw new InvalidOperationException("No map selected.");

    /// <summary>Runs <paramref name="work"/>, turning any failure but a cancellation into an
    /// <see cref="UnsupportedMapException"/>.</summary>
    private static T Unsupported<T>(Func<T> work, string failure)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (ex is not (UnsupportedMapException or OperationCanceledException))
        {
            throw new UnsupportedMapException($"Unsupported map: {failure} ({ex.Message}).");
        }
    }

    private void Clear()
    {
        Volatile.Write(ref _selection, null);
        Volatile.Write(ref _current, null);
    }

    private MapInfo Load(BeatmapEntry entry)
    {
        entry = _library.WithFiles(entry);
        string text;
        try
        {
            text = ReadText(_library.FilePath(entry.OsuHash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new LibraryUnavailableException($"Could not read the map file {entry.Display}.", ex);
        }

        var map = LoadMap(text);
        // "Kiai only" has something to amplify only if an object starts in kiai (a kiai section with no object in
        // it, or after the last one, does not count).
        var mapBase = Unsupported(() => Trainer.ReadBase(text), "the map settings could not be read")
            with { HasKiai = map.Objects.Any(o => o.InKiai) };

        var background = string.IsNullOrEmpty(entry.BackgroundFile)
            ? null
            : entry.Files.FirstOrDefault(f => string.Equals(f.Filename, entry.BackgroundFile, StringComparison.OrdinalIgnoreCase));

        var info = new MapInfo(entry.Id, entry.Artist, entry.Title, entry.DifficultyName, entry.Mapper,
            entry.IsKakudai, background?.Hash, background?.Filename, mapBase);

        Volatile.Write(ref _selection, new Selection(entry, map, mapBase, new MapStarsCache(text)));
        Volatile.Write(ref _current, info);
        return info;
    }
}
