using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using NAudio.Lame;

namespace Kakudai.Core.Audio;

/// <summary>
/// Re-times a song for a rate change: BASS decodes it, BASS_FX changes the tempo (and the pitch on demand), and LAME
/// encodes a 44.1 kHz stereo mp3. The result is cached per source content, rate and pitch.
/// </summary>
/// <remarks>
/// Sync, as measured with BASS decoding the result the way osu! does:
/// <list type="bullet">
/// <item>The LAME header carries the encoder delay and padding, and BASS honours it (with or without
/// BASS_CONFIG_MP3_OLDGAPS): the first sample of the music stays at t = 0 and the length is exact.</item>
/// <item>The tempo change (SoundTouch, inside BASS_FX) shifts the music by a few ms that depend on the tempo; see
/// <see cref="TempoShiftMs"/>. A pitch change alone does not shift it.</item>
/// </list>
/// </remarks>
public static class AudioStretcher
{
    private const int OutputRate = 44100;
    private const int OutputChannels = 2;

    /// <summary>BASS_CONFIG_MP3_OLDGAPS. osu-framework sets it to 1 (AudioManager.InitBass); Kakudai decodes the
    /// source the same way, so the music it re-times is the music the game plays.</summary>
    private const Configuration Mp3OldGaps = (Configuration)68;

    /// <summary>How many re-timed songs the cache keeps: the most recently used ones.</summary>
    public const int CacheSize = 30;

    private static readonly Lock BassLock = new();
    private static bool _bassReady;

    /// <summary>Returns the path of a cached mp3 in <paramref name="cacheDir"/>, encoding it first if needed.</summary>
    /// <exception cref="IOException">The source cannot be read, decoded or encoded (message for the player).</exception>
    public static string Stretch(string sourcePath, double rate, bool changePitch, string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        if (!(rate > 0) || double.IsInfinity(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), "The rate must be positive.");

        string? temp = null;
        try
        {
            string cached = Path.Combine(cacheDir, CacheName(SourceHash(sourcePath), rate, changePitch));
            if (File.Exists(cached))
            {
                Touch(cached); // most recently used: the last one the cache would drop
                return cached;
            }

            Directory.CreateDirectory(cacheDir);
            temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Encode(sourcePath, rate, changePitch, temp);
            File.Move(temp, cached, overwrite: true);
            Prune(cacheDir, CacheSize);
            return cached;
        }
        catch (Exception ex)
        {
            if (temp != null)
                TryDelete(temp);
            string reason = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => "file not found",
                BassException => ex.Message,
                _ => $"{ex.GetType().Name} : {ex.Message}",
            };
            throw new IOException($"Could not convert the audio \"{Path.GetFileName(sourcePath)}\": {reason}.", ex);
        }
    }

    /// <summary>
    /// Deletes the partial encodes (*.tmp) an interrupted run left in <paramref name="cacheDir"/>. Called at start,
    /// before any encode; a file still in use is left alone.
    /// </summary>
    public static void DeleteTemps(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        foreach (var temp in Files(cacheDir, "*.tmp"))
            TryDelete(temp.FullName);
    }

    /// <summary>Keeps the <paramref name="keep"/> most recently used mp3 of the cache and deletes the others (a file
    /// in use is left alone).</summary>
    internal static void Prune(string cacheDir, int keep)
    {
        foreach (var old in Files(cacheDir, "*.mp3").OrderByDescending(LastUsed).Skip(keep))
            TryDelete(old.FullName);
    }

    private static FileInfo[] Files(string dir, string pattern)
    {
        try
        {
            return new DirectoryInfo(dir).GetFiles(pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // no cache yet, or it cannot be listed: nothing to clean
        }
    }

    /// <summary>When a cached song was encoded or last reused (<see cref="Touch"/> sets the access time, which
    /// Windows may not update by itself).</summary>
    private static DateTime LastUsed(FileInfo file)
        => file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;

    /// <summary>Marks a cached song as just used. Its write time stays the time it was encoded.</summary>
    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>"{name without extension} {rate:0.000}x.mp3", or "... {rate:0.000}x pitch.mp3".</summary>
    public static string OutputName(string originalAudioFilename, double rate, bool changePitch)
    {
        ArgumentNullException.ThrowIfNull(originalAudioFilename);
        return $"{Path.GetFileNameWithoutExtension(originalAudioFilename)} {Suffix(rate, changePitch)}.mp3";
    }

    /// <summary>Initialises BASS once on device 0 (no sound), for decoding only. Thread-safe, and harmless if BASS
    /// was already initialised in this process.</summary>
    internal static void EnsureBass()
    {
        lock (BassLock)
        {
            if (_bassReady)
                return;
            Bass.Configure(Mp3OldGaps, 1);
            if (!Bass.Init(0) && Bass.LastError != Errors.Already)
                throw new BassException("BASS does not start", Bass.LastError);
            // bass_fx.dll and bassmix.dll import bass.dll: load them once bass.dll is in the process.
            _ = BassFx.Version;
            _ = BassMix.Version;
            _bassReady = true;
        }
    }

    /// <summary>
    /// How late (ms, negative = early) SoundTouch puts the music when it changes the tempo with osu!'s settings,
    /// measured with clicks, alone and over a music-like bed, from 0.5 to 2.0 (BASS_FX 2.4.12): the mean error fits
    /// 27.2·(1 − 1/t) − 6.8·(t − 1) within 1.5 ms. Single clicks still move a few ms around it (WSOLA).
    /// </summary>
    internal static double TempoShiftMs(double tempo) => 27.2 * (1 - 1 / tempo) - 6.8 * (tempo - 1);

    private static string Suffix(double rate, bool changePitch)
        => $"{rate.ToString("0.000", CultureInfo.InvariantCulture)}x{(changePitch ? " pitch" : "")}";

    private static string CacheName(string hash, double rate, bool changePitch) => $"{hash} {Suffix(rate, changePitch)}.mp3";

    private static string SourceHash(string path)
    {
        // Shared like every other read of a game file: the game may have it open, for writing included.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }

    private static void Encode(string sourcePath, double rate, bool changePitch, string mp3Path)
    {
        EnsureBass();
        int source = 0, tempo = 0, mixer = 0;
        try
        {
            source = Check(Bass.CreateStream(sourcePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan),
                "unknown format");
            tempo = Check(BassFx.TempoCreate(source, BassFlags.Decode), "tempo change");
            // Same tempo algorithm settings as osu-framework (TrackBass), so it sounds like a rate mod in the game.
            Check(Bass.ChannelSetAttribute(tempo, ChannelAttribute.TempoUseQuickAlgorithm, 1), "tempo settings");
            Check(Bass.ChannelSetAttribute(tempo, ChannelAttribute.TempoOverlapMilliseconds, 4), "tempo settings");
            Check(Bass.ChannelSetAttribute(tempo, ChannelAttribute.TempoSequenceMilliseconds, 30), "tempo settings");
            Check(Bass.ChannelSetAttribute(tempo, ChannelAttribute.Tempo, (rate - 1) * 100), "tempo");
            if (changePitch)
                Check(Bass.ChannelSetAttribute(tempo, ChannelAttribute.Pitch, 12 * Math.Log2(rate)), "pitch");
            Bass.ChannelSetAttribute(tempo, ChannelAttribute.SampleRateConversion, 3); // 32-point sinc, if resampled

            // The mixer resamples to 44.1 kHz and spreads a mono song over both channels.
            mixer = Check(BassMix.CreateMixerStream(OutputRate, OutputChannels,
                BassFlags.Decode | BassFlags.Float | BassFlags.MixerEnd), "mixer");
            Check(BassMix.MixerAddChannel(mixer, tempo, BassFlags.MixerChanNoRampin), "mixer");

            // With the pitch, SoundTouch only resamples: no time-stretch, no shift.
            double shiftMs = changePitch ? 0 : TempoShiftMs(rate);
            int shiftFrames = (int)Math.Round(shiftMs * OutputRate / 1000);

            using var file = File.Create(mp3Path);
            // WriteVBRTag: the LAME header that tells BASS the encoder delay, so the music starts at t = 0.
            using var writer = new LameMP3FileWriter(file,
                NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, OutputChannels),
                new LameConfig { Preset = LAMEPreset.INSANE, WriteVBRTag = true });
            WriteMusic(mixer, writer, shiftFrames);
        }
        finally
        {
            if (mixer != 0) Bass.StreamFree(mixer);
            if (tempo != 0) Bass.StreamFree(tempo);
            if (source != 0) Bass.StreamFree(source);
        }
    }

    /// <summary>Encodes everything the mixer gives, moved <paramref name="lateFrames"/> earlier: a late start is cut,
    /// an early one gets silence in front.</summary>
    private static void WriteMusic(int mixer, Stream writer, int lateFrames)
    {
        var buffer = new float[OutputRate * OutputChannels / 4];
        if (lateFrames < 0)
            writer.Write(MemoryMarshal.AsBytes(new float[-lateFrames * OutputChannels].AsSpan()));
        int skip = Math.Max(lateFrames, 0) * OutputChannels;
        while (true)
        {
            int bytes = Bass.ChannelGetData(mixer, buffer, buffer.Length * sizeof(float) | (int)DataFlags.Float);
            if (bytes < 0 && Bass.LastError != Errors.Ended)
                throw new BassException("decoding", Bass.LastError);
            if (bytes <= 0)
                return;
            int count = bytes / sizeof(float);
            int dropped = Math.Min(skip, count);
            skip -= dropped;
            if (count > dropped)
                writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan(dropped, count - dropped)));
        }
    }

    private static int Check(int handle, string step)
        => handle != 0 ? handle : throw new BassException(step, Bass.LastError);

    private static void Check(bool ok, string step)
    {
        if (!ok)
            throw new BassException(step, Bass.LastError);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A failed BASS call: the step plus the BASS error code.</summary>
    private sealed class BassException(string step, Errors error) : Exception($"{step} (BASS error {error})");
}
