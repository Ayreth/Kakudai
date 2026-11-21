using System.Buffers.Binary;
using System.Text;

namespace Kakudai.Core.Stable;

/// <summary>The map osu!stable shows in song select.</summary>
/// <param name="Checksum">The .osu file's MD5, as the game knows it.</param>
/// <param name="OsuPath">The .osu file, as a path of this system.</param>
/// <param name="Game">The game it was read from (to open the new map with it); null when it came from tosu without a
/// game Kakudai could find itself.</param>
public sealed record StableSelection(string Checksum, string OsuPath, StableGame? Game);

/// <summary>What the last read found, for the status line.</summary>
public enum StableState
{
    NotRunning,
    Loading,
    Denied,
    NoMap,
    Reading,
}

/// <summary>
/// Reads the map selected in osu!stable from the game's memory, the way tosu and osu-trainer do: read-only, nothing
/// is written, nothing is injected. Patterns and offsets are tosu's (packages/tosu/src/memory/stable.ts).
/// </summary>
public sealed class StableReader : IDisposable
{
    // Beatmap: [[Base - 0xC]] → the Beatmap object.
    private static readonly byte?[] BasePattern = Pattern("F8 01 74 04 83 65");

    // osu!stable is a 32-bit process.
    private const long AddressLimit = 0xFFFF_0000;

    private readonly Func<StableGame?> _find;
    private readonly Func<int, IProcessMemory> _open;
    private StableGame? _game;
    private IProcessMemory? _memory;
    private long _base;
    private string? _songsFolder;
    private DateTime _nextAttach = DateTime.MinValue;

    public StableReader() : this(StableGame.Find, OpenMemory)
    {
    }

    /// <param name="find">Test seam: finds the running game.</param>
    /// <param name="open">Test seam: opens its memory.</param>
    internal StableReader(Func<StableGame?> find, Func<int, IProcessMemory> open)
    {
        _find = find;
        _open = open;
    }

    public StableState State { get; private set; } = StableState.NotRunning;

    /// <summary>The game found by the last read, even if its memory could not be read.</summary>
    public StableGame? Game => _game;

    /// <summary>The selected map, or null (see <see cref="State"/>). Never throws.</summary>
    public StableSelection? Read()
    {
        try
        {
            if (!EnsureAttached()) return null;

            var beatmap = ReadPointer(ReadPointer(_base - 0xC));
            if (beatmap == 0)
            {
                State = StableState.NoMap;
                return null;
            }

            var checksum = ReadString(ReadPointer(beatmap + 0x6C));
            var file = ReadString(ReadPointer(beatmap + 0x90));
            var folder = ReadString(ReadPointer(beatmap + 0x78));
            State = StableState.Reading;
            if (checksum.Length == 0 || folder.Length == 0 || !file.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)
                || !IsName(folder.TrimEnd()) || !IsName(file))
                return null;

            var path = Path.Combine(_songsFolder!, folder.TrimEnd(), file);
            if (!File.Exists(path) && _game!.Wine is not null) path = WinePaths.ResolveCase(path);
            return new StableSelection(checksum, path, _game);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Detach();
            return null;
        }
    }

    private bool EnsureAttached()
    {
        if (_memory is not null && _base != 0 && IsAlive(_game!)) return true;
        Detach();
        if (DateTime.UtcNow < _nextAttach) return false;
        _nextAttach = DateTime.UtcNow.AddSeconds(3);

        _game = _find();
        if (_game is null)
        {
            State = StableState.NotRunning;
            return false;
        }

        try
        {
            _memory = _open(_game.Pid);
        }
        catch (MemoryAccessDeniedException)
        {
            State = StableState.Denied;
            return false;
        }

        // The pattern sits in JIT-compiled code, which only exists once the game has finished loading.
        _base = Scan(_memory, BasePattern);
        _songsFolder = _game.FindSongsFolder();
        if (_base == 0 || _songsFolder is null)
        {
            State = StableState.Loading;
            _memory.Dispose();
            _memory = null;
            _base = 0;
            return false;
        }
        return true;
    }

    private static bool IsAlive(StableGame game)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(game.Pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Detach()
    {
        _memory?.Dispose();
        _memory = null;
        _base = 0;
    }

    public void Dispose() => Detach();

    private static IProcessMemory OpenMemory(int pid)
    {
        if (OperatingSystem.IsWindows()) return new WindowsProcessMemory(pid);
        if (OperatingSystem.IsLinux()) return new LinuxProcessMemory(pid);
        throw new MemoryAccessDeniedException("osu!stable's memory cannot be read on this system.");
    }

    // ---------------------------------------------------------------- memory

    private long ReadPointer(long address) => (uint)ReadInt(address);

    private int ReadInt(long address)
    {
        if (address == 0) return 0;
        Span<byte> buffer = stackalloc byte[4];
        return _memory!.Read(address, buffer) ? BinaryPrimitives.ReadInt32LittleEndian(buffer) : 0;
    }

    /// <summary>A .NET string of the game: length at +4, UTF-16 characters from +8.</summary>
    private string ReadString(long address)
    {
        if (address == 0) return "";
        var length = ReadInt(address + 4);
        if (length <= 0 || length > 4096) return "";
        var bytes = new byte[length * 2];
        return _memory!.Read(address + 8, bytes) ? Encoding.Unicode.GetString(bytes) : "";
    }

    /// <summary>A file or folder name, not a path that could leave the Songs folder.</summary>
    private static bool IsName(string name)
        => name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(['/', '\\', '\0']) < 0;

    /// <summary>The address of the first match of <paramref name="pattern"/> in the readable memory, or 0.</summary>
    internal static long Scan(IProcessMemory memory, byte?[] pattern)
    {
        byte[] chunk = [];
        foreach (var region in memory.Regions(AddressLimit))
        {
            if (region.Size > 256L * 1024 * 1024) continue;
            if (chunk.Length < region.Size) chunk = new byte[region.Size];
            var span = chunk.AsSpan(0, (int)region.Size);
            if (!memory.Read(region.Start, span)) continue;
            var at = IndexOf(span, pattern);
            if (at >= 0) return region.Start + at;
        }
        return 0;
    }

    internal static int IndexOf(ReadOnlySpan<byte> data, byte?[] pattern)
    {
        var first = pattern[0]!.Value;
        var last = data.Length - pattern.Length;
        for (var i = 0; i <= last; i++)
        {
            if (data[i] != first) continue;
            var j = 1;
            for (; j < pattern.Length; j++)
            {
                var b = pattern[j];
                if (b.HasValue && data[i + j] != b.Value) break;
            }
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    internal static byte?[] Pattern(string pattern) =>
        pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t == "??" ? (byte?)null : Convert.ToByte(t, 16))
            .ToArray();
}
