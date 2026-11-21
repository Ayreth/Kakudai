using System.Reflection;
using System.Runtime.InteropServices;

namespace Kakudai.Core.Audio;

/// <summary>
/// The few LAME calls Kakudai needs, straight from libmp3lame, on every system: an interleaved float encoder that
/// writes the LAME (Xing) header, so that BASS knows the encoder delay and the music starts at t = 0.
/// </summary>
/// <remarks>
/// Where the library is looked for, in order: next to Kakudai (shipped with the Windows, Linux and macOS builds), then
/// the system: <c>libmp3lame.so.0</c> on Linux (package libmp3lame0 / lame), <c>libmp3lame.0.dylib</c> from Homebrew
/// or MacPorts on macOS.
/// </remarks>
internal sealed class LameEncoder : IDisposable
{
    private const string Library = "mp3lame";

    /// <summary>LAME's "insane" preset: CBR 320 kbps, as Kakudai always used.</summary>
    private const int PresetInsane = 1003;

    private readonly Stream _output;
    private readonly long _start;
    private readonly int _channels;
    private IntPtr _lame;
    private byte[] _buffer = new byte[16 * 1024];

    static LameEncoder() => NativeLibrary.SetDllImportResolver(typeof(LameEncoder).Assembly, Resolve);

    /// <exception cref="LameException">LAME is missing or refuses these parameters (message for the player).</exception>
    public LameEncoder(Stream output, int sampleRate, int channels)
    {
        _output = output;
        _start = output.CanSeek ? output.Position : -1;
        _channels = channels;
        try
        {
            _lame = lame_init();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new LameException(MissingMessage(), ex);
        }
        if (_lame == IntPtr.Zero) throw new LameException("the mp3 encoder (LAME) does not start");

        bool ok = lame_set_in_samplerate(_lame, sampleRate) == 0
                  && lame_set_out_samplerate(_lame, sampleRate) == 0
                  && lame_set_num_channels(_lame, channels) == 0
                  && lame_set_preset(_lame, PresetInsane) >= 0
                  && lame_set_bWriteVbrTag(_lame, 1) == 0
                  && lame_init_params(_lame) >= 0;
        if (!ok)
        {
            Dispose();
            throw new LameException("the mp3 encoder (LAME) refuses the audio format");
        }
    }

    /// <summary>Encodes interleaved float samples (-1..1), any number of whole frames.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / _channels;
        if (frames == 0) return;
        Ensure(frames * 5 / 4 + 7200);
        int bytes;
        unsafe
        {
            fixed (float* pcm = samples)
            fixed (byte* mp3 = _buffer)
                bytes = lame_encode_buffer_interleaved_ieee_float(_lame, pcm, frames, mp3, _buffer.Length);
        }
        if (bytes < 0) throw new LameException($"the mp3 encoder (LAME) failed (error {bytes})");
        _output.Write(_buffer, 0, bytes);
    }

    /// <summary>Writes the last frames and, over the first one, the LAME header with the delay and the length.</summary>
    public void Finish()
    {
        Ensure(7200);
        int bytes;
        unsafe
        {
            fixed (byte* mp3 = _buffer)
                bytes = lame_encode_flush(_lame, mp3, _buffer.Length);
        }
        if (bytes < 0) throw new LameException($"the mp3 encoder (LAME) failed (error {bytes})");
        _output.Write(_buffer, 0, bytes);

        if (_start < 0) return;
        Ensure(2880); // the largest mp3 frame
        int tag;
        unsafe
        {
            fixed (byte* mp3 = _buffer)
                tag = (int)lame_get_lametag_frame(_lame, mp3, (nuint)_buffer.Length);
        }
        if (tag <= 0) return;
        long end = _output.Position;
        _output.Position = _start;
        _output.Write(_buffer, 0, tag);
        _output.Position = end;
    }

    public void Dispose()
    {
        if (_lame == IntPtr.Zero) return;
        lame_close(_lame);
        _lame = IntPtr.Zero;
    }

    /// <summary>True if libmp3lame can be loaded on this machine.</summary>
    public static bool IsAvailable()
    {
        try
        {
            var lame = lame_init();
            if (lame == IntPtr.Zero) return false;
            lame_close(lame);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private void Ensure(int size)
    {
        if (_buffer.Length < size) _buffer = new byte[size];
    }

    private static string MissingMessage()
    {
        if (OperatingSystem.IsMacOS())
            return "the mp3 encoder (LAME) is missing: install it with \"brew install lame\"";
        if (OperatingSystem.IsLinux())
            return "the mp3 encoder (LAME) is missing: install the package \"libmp3lame0\" (or \"lame\")";
        return "the mp3 encoder (libmp3lame.64.dll) is missing next to Kakudai.exe";
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero; // DllNotFoundException, turned into a message by the constructor
    }

    /// <summary>The files tried, most specific first: the copy shipped next to Kakudai, then the system's.</summary>
    internal static IEnumerable<string> Candidates()
    {
        var here = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(here, Environment.Is64BitProcess ? "libmp3lame.64.dll" : "libmp3lame.32.dll");
            yield return Path.Combine(here, "libmp3lame.dll");
            yield return "libmp3lame.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in new[] { here, Path.Combine(here, "..", "Frameworks"), "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib" })
            {
                yield return Path.Combine(dir, "libmp3lame.0.dylib");
                yield return Path.Combine(dir, "libmp3lame.dylib");
            }
            yield return "libmp3lame.0.dylib";
        }
        else
        {
            yield return Path.Combine(here, "libmp3lame.so.0");
            yield return Path.Combine(here, "libmp3lame.so");
            yield return "libmp3lame.so.0";
            yield return "libmp3lame.so";
        }
    }

    [DllImport(Library)] private static extern IntPtr lame_init();
    [DllImport(Library)] private static extern int lame_close(IntPtr gfp);
    [DllImport(Library)] private static extern int lame_set_in_samplerate(IntPtr gfp, int value);
    [DllImport(Library)] private static extern int lame_set_out_samplerate(IntPtr gfp, int value);
    [DllImport(Library)] private static extern int lame_set_num_channels(IntPtr gfp, int value);
    [DllImport(Library)] private static extern int lame_set_preset(IntPtr gfp, int preset);
    [DllImport(Library)] private static extern int lame_set_bWriteVbrTag(IntPtr gfp, int value);
    [DllImport(Library)] private static extern int lame_init_params(IntPtr gfp);

    [DllImport(Library)]
    private static extern unsafe int lame_encode_buffer_interleaved_ieee_float(IntPtr gfp, float* pcm, int frames, byte* mp3, int size);

    [DllImport(Library)] private static extern unsafe int lame_encode_flush(IntPtr gfp, byte* mp3, int size);
    [DllImport(Library)] private static extern unsafe nuint lame_get_lametag_frame(IntPtr gfp, byte* buffer, nuint size);
}

/// <summary>LAME is missing or failed; the message is complete, for the player.</summary>
internal sealed class LameException(string message, Exception? inner = null) : IOException(message, inner);
