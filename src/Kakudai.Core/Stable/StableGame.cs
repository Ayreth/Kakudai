using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Kakudai.Core.Stable;

/// <summary>How the osu!stable running under Wine was started: what it takes to start it again with a file.</summary>
/// <param name="Prefix">The Wine prefix (WINEPREFIX, or ~/.wine).</param>
/// <param name="Environment">The game's environment (WINEPREFIX, WINEFSYNC, PATH, …).</param>
/// <param name="ExeArgument">The game's executable as Wine was given it (a Windows or a Unix path).</param>
/// <param name="Loader">The wine executable that runs it.</param>
/// <param name="WorkingDirectory">The game's current directory, if it can be read.</param>
public sealed record WineLaunch(string Prefix, IReadOnlyDictionary<string, string> Environment, string ExeArgument,
    string Loader, string? WorkingDirectory);

/// <summary>A running osu!stable.</summary>
/// <param name="GameDir">Its folder, as a path of this system (the Unix path behind Wine's on Linux).</param>
/// <param name="ExePath">osu!.exe, as a path of this system.</param>
/// <param name="Wine">How it runs under Wine; null on Windows.</param>
public sealed record StableGame(int Pid, string GameDir, string ExePath, WineLaunch? Wine)
{
    /// <summary>A Windows path from the game (the cfg's BeatmapDirectory…) as a path of this system.</summary>
    public string? Local(string path)
    {
        if (Wine is null) return Path.IsPathRooted(path) ? path : Path.Combine(GameDir, path);
        if (WinePaths.IsWindowsPath(path) || path.StartsWith('/')) return WinePaths.ToUnix(path, Wine.Prefix);
        return WinePaths.ResolveCase(Path.Combine(GameDir, path.Replace('\\', '/')));
    }

    /// <summary>
    /// Where osu! keeps its maps: BeatmapDirectory from <c>osu!.&lt;user&gt;.cfg</c>, else <c>Songs</c> in the game
    /// folder. Null if neither exists.
    /// </summary>
    public string? FindSongsFolder()
    {
        try
        {
            foreach (var cfg in Directory.EnumerateFiles(GameDir, "osu!.*.cfg"))
            {
                if (Path.GetFileName(cfg).Equals("osu!.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var line in File.ReadLines(cfg))
                {
                    if (!line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase)) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var value = line[(eq + 1)..].Trim();
                    if (value.Length == 0) continue;
                    if (Local(value) is { } path && Directory.Exists(path)) return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var songs = Local("Songs");
        return songs is not null && Directory.Exists(songs) ? songs : null;
    }

    /// <summary>The running osu!stable, or null. Windows: the 32-bit osu!.exe (lazer's is 64-bit). Linux: the Wine
    /// process named osu!.exe. macOS: never (its memory cannot be read).</summary>
    public static StableGame? Find()
    {
        if (OperatingSystem.IsWindows()) return FindWindows();
        if (OperatingSystem.IsLinux()) return FindLinux();
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static StableGame? FindWindows()
    {
        foreach (var process in Process.GetProcessesByName("osu!"))
        {
            using (process)
            {
                if (!WindowsProcessMemory.Is32Bit(process)) continue;
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (exe is null) continue;
                    return new StableGame(process.Id, Path.GetDirectoryName(exe)!, exe, null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile, or not ours to read.
                }
            }
        }
        return null;
    }

    [SupportedOSPlatform("linux")]
    private static StableGame? FindLinux()
    {
        IEnumerable<string> pids;
        try
        {
            pids = Directory.EnumerateDirectories("/proc").Select(Path.GetFileName).Where(n => n!.All(char.IsAsciiDigit))!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var name in pids)
        {
            try
            {
                var game = FromProc(int.Parse(name), $"/proc/{name}");
                if (game is not null) return game;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Gone, or another user's process.
            }
        }
        return null;
    }

    /// <summary>Reads one <c>/proc/&lt;pid&gt;</c> folder (or a copy of it, for tests); null if it is not osu!stable.</summary>
    internal static StableGame? FromProc(int pid, string procDir)
    {
        var comm = ReadOrEmpty(Path.Combine(procDir, "comm")).Trim();
        var args = ReadOrEmpty(Path.Combine(procDir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var exeArgument = args.FirstOrDefault(IsOsuExe);
        if (!comm.Equals("osu!.exe", StringComparison.OrdinalIgnoreCase) && exeArgument is null) return null;

        var environment = ParseEnviron(ReadOrEmpty(Path.Combine(procDir, "environ")));
        var home = environment.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var prefix = environment.GetValueOrDefault("WINEPREFIX") is { Length: > 0 } p ? p : Path.Combine(home, ".wine");
        var cwd = ReadLink(Path.Combine(procDir, "cwd"));

        string? exe = exeArgument is null ? null : WinePaths.ToUnix(exeArgument, prefix);
        if ((exe is null || !File.Exists(exe)) && cwd is not null)
        {
            var inCwd = WinePaths.ResolveCase(Path.Combine(cwd, "osu!.exe"));
            if (File.Exists(inCwd)) exe = inCwd;
        }
        if (exe is null || !File.Exists(exe)) return null;

        var loader = environment.GetValueOrDefault("WINELOADER") is { Length: > 0 } l ? l : FindLoader(ReadLink(Path.Combine(procDir, "exe")));
        var launch = new WineLaunch(prefix, environment, exeArgument ?? exe, loader, cwd);
        return new StableGame(pid, Path.GetDirectoryName(exe)!, exe, launch);
    }

    private static bool IsOsuExe(string arg)
    {
        var name = arg.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("osu!.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The wine binary next to the preloader running the game (wine64-preloader → wine64), else "wine".</summary>
    internal static string FindLoader(string? processExe)
    {
        if (processExe is not null)
        {
            var dir = Path.GetDirectoryName(processExe);
            var file = Path.GetFileName(processExe);
            if (dir is not null && file.Contains("preloader", StringComparison.Ordinal))
            {
                var wine = Path.Combine(dir, file.Replace("-preloader", "", StringComparison.Ordinal));
                if (File.Exists(wine)) return wine;
            }
            if (dir is not null && File.Exists(Path.Combine(dir, "wine"))) return Path.Combine(dir, "wine");
        }
        return "wine";
    }

    internal static Dictionary<string, string> ParseEnviron(string environ)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in environ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq > 0) result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string? ReadLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target) return target;
            return Directory.Exists(path) ? new DirectoryInfo(path).LinkTarget : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
