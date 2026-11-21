using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kakudai.Core.Stable;

/// <summary>A memory region of another process that can be read.</summary>
public readonly record struct MemoryRegion(long Start, long Size);

/// <summary>Read-only access to the memory of another process. Nothing is ever written or injected.</summary>
public interface IProcessMemory : IDisposable
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>; false if any byte cannot be read.</summary>
    bool Read(long address, Span<byte> buffer);

    /// <summary>The readable regions below <paramref name="limit"/>, in address order.</summary>
    IEnumerable<MemoryRegion> Regions(long limit);
}

/// <summary>The system refuses to let Kakudai read the game's memory (Linux: ptrace restrictions).</summary>
public sealed class MemoryAccessDeniedException(string message) : Exception(message);

/// <summary>Windows: ReadProcessMemory and VirtualQueryEx on a handle opened for reading only.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessMemory : IProcessMemory
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    // READONLY | READWRITE | WRITECOPY | EXECUTE_READ | EXECUTE_READWRITE | EXECUTE_WRITECOPY
    private const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    private IntPtr _handle;

    /// <exception cref="MemoryAccessDeniedException">The process cannot be opened for reading.</exception>
    public WindowsProcessMemory(int pid)
    {
        _handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (_handle == IntPtr.Zero) throw new MemoryAccessDeniedException("Windows refuses access to osu!'s memory.");
    }

    public unsafe bool Read(long address, Span<byte> buffer)
    {
        if (_handle == IntPtr.Zero || address <= 0) return false;
        fixed (byte* p = buffer)
            return ReadProcessMemory(_handle, (IntPtr)address, (IntPtr)p, buffer.Length, out var read) && read == buffer.Length;
    }

    public IEnumerable<MemoryRegion> Regions(long limit)
    {
        long address = 0;
        var size = (uint)Marshal.SizeOf<MemoryBasicInformation>();
        while (address < limit && VirtualQueryEx(_handle, (IntPtr)address, out var info, size) != 0)
        {
            var regionSize = (long)info.RegionSize;
            if (regionSize <= 0) yield break;
            if (info.State == MemCommit && (info.Protect & PageGuard) == 0 && (info.Protect & PageNoAccess) == 0
                && (info.Protect & ReadableMask) != 0)
                yield return new MemoryRegion((long)info.BaseAddress, regionSize);
            address = (long)info.BaseAddress + regionSize;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>True for a 32-bit process on 64-bit Windows: osu!stable, as opposed to lazer's 64-bit osu!.exe.</summary>
    public static bool Is32Bit(System.Diagnostics.Process process)
    {
        try
        {
            return IsWow64Process(process.Handle, out var wow64) && wow64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, uint length);
}

/// <summary>
/// Linux (osu!stable under Wine): process_vm_readv, and the regions of <c>/proc/&lt;pid&gt;/maps</c>. Reading another
/// process needs the same rights as ptrace: with Yama's <c>ptrace_scope</c> at 1 (Ubuntu, Fedora…), only a parent may
/// read its child, unless the reader has CAP_SYS_PTRACE.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxProcessMemory : IProcessMemory
{
    private const int EPERM = 1;
    private const int ESRCH = 3;

    private readonly int _pid;

    /// <exception cref="MemoryAccessDeniedException">Linux refuses the read (ptrace restrictions).</exception>
    public LinuxProcessMemory(int pid)
    {
        _pid = pid;
        // A first read tells "denied" from "allowed" right away, on the first readable region.
        Span<byte> probe = stackalloc byte[4];
        foreach (var region in Regions(long.MaxValue))
        {
            if (TryRead(region.Start, probe, out var errno)) return;
            if (errno == EPERM)
                throw new MemoryAccessDeniedException(
                    "Linux refuses access to osu!'s memory (ptrace_scope). See the README, or run tosu.");
            if (errno == ESRCH) throw new MemoryAccessDeniedException("osu! has closed.");
        }
    }

    public bool Read(long address, Span<byte> buffer) => address > 0 && TryRead(address, buffer, out _);

    private unsafe bool TryRead(long address, Span<byte> buffer, out int errno)
    {
        errno = 0;
        if (buffer.Length == 0) return true;
        fixed (byte* p = buffer)
        {
            var local = new IoVec { Base = (IntPtr)p, Length = (nuint)buffer.Length };
            var remote = new IoVec { Base = (IntPtr)address, Length = (nuint)buffer.Length };
            var read = process_vm_readv(_pid, &local, 1, &remote, 1, 0);
            if (read == buffer.Length) return true;
            if (read < 0) errno = Marshal.GetLastPInvokeError();
            return false;
        }
    }

    public IEnumerable<MemoryRegion> Regions(long limit)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines($"/proc/{_pid}/maps");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var line in lines)
        {
            if (ProcMaps.ParseLine(line) is { } region && region.Start < limit) yield return region;
        }
    }


    public void Dispose()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public IntPtr Base;
        public nuint Length;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint process_vm_readv(int pid, IoVec* local, ulong localCount, IoVec* remote,
        ulong remoteCount, ulong flags);
}

/// <summary>The <c>/proc/&lt;pid&gt;/maps</c> format (pure, so testable anywhere).</summary>
internal static class ProcMaps
{
    /// <summary>A readable region of a <c>/proc/&lt;pid&gt;/maps</c> line ("start-end perms …"), or null.</summary>
    internal static MemoryRegion? ParseLine(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1].Length == 0 || parts[1][0] != 'r') return null;
        var dash = parts[0].IndexOf('-');
        if (dash <= 0) return null;
        if (!long.TryParse(parts[0].AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(parts[0].AsSpan(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)
            || end <= start)
            return null;
        // [vvar] and the like cannot be read through process_vm_readv.
        if (line.EndsWith("[vvar]", StringComparison.Ordinal) || line.EndsWith("[vsyscall]", StringComparison.Ordinal))
            return null;
        return new MemoryRegion(start, end - start);
    }
}
