using System.Diagnostics;
using Kakudai.Core.Audio;
using ManagedBass;
using NUnit.Framework;

namespace Kakudai.Tests;

public class AudioStretcherTests
{
    private const int SampleRate = 44100;
    private static readonly int[] ClicksMs = [1000, 5000, 9000];

    private string _dir = "";
    private string _wav = "";
    private string _cache = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kakudai-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = Path.Combine(_dir, "cache");
        _wav = Path.Combine(_dir, "clicks.wav");
        WriteWav(_wav, ClickTrack(10_000));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Test]
    public void Stretch_Rate1_25_ClicksAt800_4000_7200()
    {
        var mp3 = AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache);

        AssertClicksAndDuration(mp3, [800, 4000, 7200], 8000);
    }

    [Test]
    public void Stretch_Rate0_8_KeepsSync()
    {
        var mp3 = AudioStretcher.Stretch(_wav, 0.8, changePitch: false, _cache);

        AssertClicksAndDuration(mp3, [1250, 6250, 11250], 12500);
    }

    [Test]
    public void ChangePitch_StillSyncs()
    {
        var mp3 = AudioStretcher.Stretch(_wav, 1.25, changePitch: true, _cache);

        AssertClicksAndDuration(mp3, [800, 4000, 7200], 8000);
    }

    // Slower rates: single clicks move further around the compensated mean (SoundTouch WSOLA), and the compensation
    // adds up to ~24 ms of silence in front. Faster than ~1.4, WSOLA skips a third of the input and a 5 ms click can
    // vanish, so this fixture cannot measure them (1.25 is covered above).
    [TestCase(0.5)]
    [TestCase(0.75)]
    public void Stretch_OtherRates_KeepSync(double rate)
    {
        var mp3 = AudioStretcher.Stretch(_wav, rate, changePitch: false, _cache);

        AssertClicksAndDuration(mp3, ClicksMs.Select(ms => ms / rate).ToArray(), 10_000 / rate,
            clickToleranceMs: 8, durationToleranceMs: 30);
    }

    [Test]
    public void Stretch_48kMonoSource_KeepsSync()
    {
        var wav = Path.Combine(_dir, "mono48.wav");
        WriteWav(wav, ClickTrack(10_000, 48000, 1), 48000, 1);

        var mp3 = AudioStretcher.Stretch(wav, 1.25, changePitch: false, _cache);

        AssertClicksAndDuration(mp3, [800, 4000, 7200], 8000);
        var (samples, rate, channels) = Decode(mp3);
        Assert.That((rate, channels), Is.EqualTo((44100, 2)));
        Assert.That(samples.Where((_, i) => i % 2 == 0).Select(Math.Abs).Max(),
            Is.EqualTo(samples.Where((_, i) => i % 2 == 1).Select(Math.Abs).Max()).Within(0.01), "mono on both sides");
    }

    [Test]
    public void Stretch_WritesStereo44100Mp3()
    {
        var mp3 = AudioStretcher.Stretch(_wav, 1.5, changePitch: false, _cache);

        Assert.That(Path.GetExtension(mp3), Is.EqualTo(".mp3"));
        var bytes = File.ReadAllBytes(mp3);
        Assert.That(bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0, Is.True, "starts with an mp3 frame");
        var (_, rate, channels) = Decode(mp3);
        Assert.That((rate, channels), Is.EqualTo((44100, 2)));
    }

    [Test]
    public void Cache_KeepsThe30MostRecentlyUsed()
    {
        Directory.CreateDirectory(_cache);
        var start = DateTime.UtcNow.AddDays(-1);
        var old = new List<string>();
        for (int i = 0; i < 35; i++)
        {
            var path = Path.Combine(_cache, $"old {i:00}.mp3");
            File.WriteAllText(path, "mp3");
            File.SetLastWriteTimeUtc(path, start.AddMinutes(i));
            File.SetLastAccessTimeUtc(path, start.AddMinutes(i));
            old.Add(path);
        }
        // Encoded long ago but reused just now: kept.
        File.SetLastAccessTimeUtc(old[0], DateTime.UtcNow.AddMinutes(-1));
        var other = Path.Combine(_cache, "notes.txt");
        File.WriteAllText(other, "x");

        var mp3 = AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache);

        var kept = Directory.GetFiles(_cache, "*.mp3");
        Assert.That(kept, Has.Length.EqualTo(AudioStretcher.CacheSize));
        Assert.That(kept, Does.Contain(mp3).And.Contain(old[0]));
        // 36 mp3: the new one, old[0], and old[7..34] are the 30 most recent; old[1..6] go.
        Assert.That(old.Skip(1).Take(6).Where(File.Exists), Is.Empty);
        Assert.That(old.Skip(7).All(File.Exists), Is.True);
        Assert.That(File.Exists(other), Is.True, "only mp3 are pruned");
    }

    [Test]
    public void DeleteTemps_DeletesPartialEncodesOnly()
    {
        Directory.CreateDirectory(_cache);
        var temp = Path.Combine(_cache, "abc 1.250x.mp3.0123.tmp");
        var inUse = Path.Combine(_cache, "def 1.250x.mp3.4567.tmp");
        var song = Path.Combine(_cache, "abc 1.250x.mp3");
        File.WriteAllText(temp, "partial");
        File.WriteAllText(inUse, "partial");
        File.WriteAllText(song, "mp3");

        using (new FileStream(inUse, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AudioStretcher.DeleteTemps(_cache);

        Assert.That(File.Exists(temp), Is.False);
        Assert.That(File.Exists(inUse), Is.True, "a file in use is left alone");
        Assert.That(File.Exists(song), Is.True);
        Assert.DoesNotThrow(() => AudioStretcher.DeleteTemps(Path.Combine(_dir, "no cache yet")));
    }

    [Test]
    public void Stretch_SourceOpenForWriting_StillReads()
    {
        // The game may hold its files open for writing; the hash and the decode must still read the song.
        using var open = new FileStream(_wav, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.That(File.Exists(AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache)), Is.True);
    }

    [Test]
    public void Cache_ReusesFile_DifferentRateDifferentFile()
    {
        var first = AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache);
        var written = File.GetLastWriteTimeUtc(first);

        var again = AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache);
        var otherRate = AudioStretcher.Stretch(_wav, 1.3, changePitch: false, _cache);
        var pitched = AudioStretcher.Stretch(_wav, 1.25, changePitch: true, _cache);

        Assert.That(again, Is.EqualTo(first));
        Assert.That(File.GetLastWriteTimeUtc(again), Is.EqualTo(written), "the cached file is not re-encoded");
        Assert.That(new[] { first, otherRate, pitched }, Is.Unique);
        Assert.That(Path.GetDirectoryName(first), Is.EqualTo(_cache));
        Assert.That(Directory.GetFiles(_cache), Has.Length.EqualTo(3), "no temporary file left behind");

        WriteWav(_wav, ClickTrack(6_000));
        Assert.That(AudioStretcher.Stretch(_wav, 1.25, changePitch: false, _cache), Is.Not.EqualTo(first),
            "another source content gives another file");
    }

    [Test]
    [SetCulture("fr-FR")]
    public void OutputName_Format()
    {
        Assert.That(AudioStretcher.OutputName("audio.mp3", 1.25, false), Is.EqualTo("audio 1.250x.mp3"));
        Assert.That(AudioStretcher.OutputName("My Song.ogg", 0.8, true), Is.EqualTo("My Song 0.800x pitch.mp3"));
        Assert.That(AudioStretcher.OutputName("a.b.wav", 2, false), Is.EqualTo("a.b 2.000x.mp3"));
    }

    [Test]
    public void Stretch_NotAudio_ThrowsReadableIOException()
    {
        var bad = Path.Combine(_dir, "pas-audio.mp3");
        File.WriteAllText(bad, "ceci n'est pas de l'audio");

        var ex = Assert.Throws<IOException>(() => AudioStretcher.Stretch(bad, 1.25, false, _cache));
        Assert.That(ex!.Message, Does.StartWith("Could not convert the audio \"pas-audio.mp3\""));
        Assert.That(Directory.Exists(_cache) ? Directory.GetFiles(_cache) : [], Is.Empty);
    }

    [Test]
    public void Stretch_MissingSource_ThrowsReadableIOException()
    {
        var ex = Assert.Throws<IOException>(
            () => AudioStretcher.Stretch(Path.Combine(_dir, "absent.ogg"), 1.25, false, _cache));
        Assert.That(ex!.Message, Does.StartWith("Could not convert the audio \"absent.ogg\""));
    }

    /// <summary>Not a correctness test: measures the encode time of a 3-minute song (run it on demand).</summary>
    [Test]
    [Explicit]
    public void Timing_ThreeMinuteSong()
    {
        var song = Path.Combine(_dir, "song.wav");
        WriteWav(song, MusicLike(180_000));

        var sw = Stopwatch.StartNew();
        var mp3 = AudioStretcher.Stretch(song, 1.25, changePitch: false, _cache);
        var tempo = sw.Elapsed;
        sw.Restart();
        AudioStretcher.Stretch(song, 1.25, changePitch: true, _cache);
        var pitch = sw.Elapsed;
        sw.Restart();
        AudioStretcher.Stretch(song, 1.25, changePitch: false, _cache);
        var cached = sw.Elapsed;

        TestContext.Out.WriteLine($"3 min @1.25: tempo {tempo.TotalSeconds:0.00} s, pitch {pitch.TotalSeconds:0.00} s, "
            + $"cached {cached.TotalMilliseconds:0} ms, mp3 {new FileInfo(mp3).Length / 1024} KiB");
    }

    // ---- helpers ----

    private static void AssertClicksAndDuration(string mp3, double[] expectedMs, double expectedDurationMs,
        double clickToleranceMs = 5, double durationToleranceMs = 20)
    {
        var (samples, rate, channels) = Decode(mp3);
        var clicks = FindClicks(samples, rate, channels);
        double duration = samples.Length / (double)channels / rate * 1000;
        TestContext.Out.WriteLine($"{Path.GetFileName(mp3)}: clicks at {string.Join(", ", clicks.Select(c => c.ToString("0.00")))} ms,"
            + $" duration {duration:0.00} ms");

        Assert.That(clicks, Has.Count.EqualTo(expectedMs.Length), "click count");
        for (int i = 0; i < expectedMs.Length; i++)
            Assert.That(clicks[i], Is.EqualTo(expectedMs[i]).Within(clickToleranceMs), $"click {i}");
        Assert.That(duration, Is.EqualTo(expectedDurationMs).Within(durationToleranceMs), "duration");
    }

    /// <summary>Decodes the whole file with BASS, as the game does (decode stream, prescan, float samples).</summary>
    private static (float[] Samples, int Rate, int Channels) Decode(string path)
    {
        AudioStretcher.EnsureBass();
        int h = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
        Assert.That(h, Is.Not.Zero, $"BASS cannot open the mp3: {Bass.LastError}");
        try
        {
            var info = Bass.ChannelGetInfo(h);
            var all = new List<float>();
            var buf = new float[16384];
            int got;
            while ((got = Bass.ChannelGetData(h, buf, buf.Length * sizeof(float) | (int)DataFlags.Float)) > 0)
                all.AddRange(buf.AsSpan(0, got / sizeof(float)));
            return (all.ToArray(), info.Frequency, info.Channels);
        }
        finally
        {
            Bass.StreamFree(h);
        }
    }

    /// <summary>Onset of each click: first frame whose level reaches half of the track's peak, then 500 ms of dead time.</summary>
    private static List<double> FindClicks(float[] samples, int rate, int channels)
    {
        float peak = samples.Select(Math.Abs).Max();
        float threshold = peak / 2;
        var onsets = new List<double>();
        int frames = samples.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            float level = 0;
            for (int c = 0; c < channels; c++)
                level = Math.Max(level, Math.Abs(samples[f * channels + c]));
            if (level < threshold)
                continue;
            onsets.Add(f * 1000.0 / rate);
            f += rate / 2;
        }
        return onsets;
    }

    /// <summary>Silence with 5 ms bursts of a 2 kHz cosine (amplitude 0.8) starting at each click time.</summary>
    private static short[] ClickTrack(int durationMs, int sampleRate = SampleRate, int channels = 2)
    {
        int frames = (int)((long)sampleRate * durationMs / 1000);
        var pcm = new short[frames * channels];
        foreach (int ms in ClicksMs.Where(ms => ms < durationMs))
        {
            int start = sampleRate * ms / 1000;
            for (int i = 0; i < sampleRate * 5 / 1000; i++)
            {
                short v = (short)(0.8 * short.MaxValue * Math.Cos(2 * Math.PI * 2000 * i / sampleRate));
                for (int c = 0; c < channels; c++)
                    pcm[(start + i) * channels + c] = v;
            }
        }
        return pcm;
    }

    /// <summary>A busy stereo signal (chords, a beat and some noise) so the encoder does real work.</summary>
    private static short[] MusicLike(int durationMs)
    {
        int frames = (int)((long)SampleRate * durationMs / 1000);
        var pcm = new short[frames * 2];
        var random = new Random(1);
        double[] notes = [220, 277.18, 329.63, 440, 554.37];
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / SampleRate;
            double chord = notes.Sum(n => Math.Sin(2 * Math.PI * n * (1 + 0.1 * (Math.Floor(t / 2) % 3)) * t)) / notes.Length;
            double beat = Math.Exp(-30 * (t % 0.375)) * Math.Sin(2 * Math.PI * 60 * t);
            double noise = (random.NextDouble() * 2 - 1) * 0.05;
            double left = 0.45 * chord + 0.4 * beat + noise;
            double right = 0.45 * chord * Math.Cos(t) + 0.4 * beat - noise;
            pcm[f * 2] = (short)(Math.Clamp(left, -1, 1) * short.MaxValue);
            pcm[f * 2 + 1] = (short)(Math.Clamp(right, -1, 1) * short.MaxValue);
        }
        return pcm;
    }

    private static void WriteWav(string path, short[] pcm, int sampleRate = SampleRate, int channels = 2)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = pcm.Length * sizeof(short);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * sizeof(short));
        w.Write((short)(channels * sizeof(short)));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (short s in pcm)
            w.Write(s);
    }
}
