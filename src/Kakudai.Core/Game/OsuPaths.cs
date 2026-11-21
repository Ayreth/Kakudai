namespace Kakudai.Core.Game;

/// <summary>Where the games and Kakudai keep their files, on Windows, Linux and macOS.</summary>
public static class OsuPaths
{
    /// <summary>
    /// The folder osu!framework apps keep their data in, per user: <c>%AppData%</c> on Windows,
    /// <c>$XDG_DATA_HOME</c> (else <c>~/.local/share</c>) on Linux, <c>~/Library/Application Support</c> on macOS.
    /// </summary>
    public static string UserDataRoot
    {
        get
        {
            if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        }
    }

    /// <summary>
    /// Lazer's and Torii's data folders that exist, found the way the game finds them:
    /// <list type="bullet">
    /// <item>their default folders (osu, osu-torii) and wherever their <c>storage.ini</c> (<c>FullPath = …</c>, written
    /// when the data is moved from the game's settings) points, followed from one to the next;</item>
    /// <item>the folder of each running lazer or Torii, for a portable install (data next to the game);</item>
    /// <item>on Linux, the folder of the <c>client.realm</c> each running game has open.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> LazerDataDirs()
        => LazerDataDirs(UserDataRoot, RunningGameDirs());

    internal static IReadOnlyList<string> LazerDataDirs(string root, IEnumerable<string>? running = null)
    {
        var result = new List<string>();
        foreach (var name in new[] { "osu", "osu-torii", "torii" }) Follow(Path.Combine(root, name), 0);
        foreach (var dir in running ?? []) Follow(dir, 0);
        return result;

        void Follow(string dir, int depth)
        {
            if (depth > 4 || !Directory.Exists(dir)) return;
            // The folder the data was moved to comes first: it is the one in use.
            if (ReadStorageIni(Path.Combine(dir, "storage.ini")) is { } moved) Follow(moved, depth + 1);
            var full = Path.GetFullPath(dir);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!result.Any(d => string.Equals(d, full, comparison))) result.Add(full);
        }
    }

    /// <summary>
    /// Data folders the running games show: their own folder when it holds the data (portable install, portable.ini),
    /// and on Linux the folder of every client.realm they have open.
    /// </summary>
    private static IEnumerable<string> RunningGameDirs()
    {
        var dirs = new List<string>();
        foreach (var (exe, openRealms) in RunningGames())
        {
            dirs.AddRange(openRealms);
            var dir = Path.GetDirectoryName(exe);
            if (dir is not null && (File.Exists(Path.Combine(dir, "portable.ini")) || File.Exists(Path.Combine(dir, "client.realm"))))
                dirs.Add(dir);
        }
        return dirs;
    }

    private static IEnumerable<(string Exe, IReadOnlyList<string> OpenRealmDirs)> RunningGames()
    {
        var found = new List<(string, IReadOnlyList<string>)>();
        if (OperatingSystem.IsLinux())
        {
            IEnumerable<string> procs;
            try
            {
                procs = Directory.EnumerateDirectories("/proc").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return found;
            }
            foreach (var proc in procs)
            {
                if (!Path.GetFileName(proc).All(char.IsAsciiDigit)) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(proc, "comm")).Trim();
                    if (comm is not ("osu!" or "torii" or "Torii")) continue;
                    var exe = new FileInfo(Path.Combine(proc, "exe")).LinkTarget;
                    var realms = new List<string>();
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(proc, "fd")))
                    {
                        var target = new FileInfo(fd).LinkTarget;
                        if (target is not null && target.EndsWith("/client.realm", StringComparison.Ordinal))
                            realms.Add(Path.GetDirectoryName(target)!);
                    }
                    if (exe is not null) found.Add((exe, realms));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone, or not ours.
                }
            }
            return found;
        }

        foreach (var name in new[] { "osu!", "torii", "Torii" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } exe) found.Add((exe, []));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                                   or NotSupportedException)
                    {
                        // osu!stable's 32-bit process from a 64-bit Kakudai, or not ours to read: skipped.
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The data folder to follow: of those holding a <c>client.realm</c>, the one whose logs were written last (the
    /// game played last). The default lazer folder if there is none yet, so the window can wait for the game.
    /// </summary>
    public static string ActiveLazerDataDir()
    {
        var dirs = LazerDataDirs().Where(d => File.Exists(Path.Combine(d, "client.realm"))).ToList();
        if (dirs.Count == 0) return Path.Combine(UserDataRoot, "osu");
        return dirs.OrderByDescending(LastLog).First();
    }

    private static DateTime LastLog(string dataDir)
    {
        try
        {
            var logs = new DirectoryInfo(Path.Combine(dataDir, "logs"));
            return logs.Exists
                ? logs.EnumerateFiles("*.runtime.log").Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max()
                : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The <c>FullPath</c> of a lazer storage.ini, or null.</summary>
    internal static string? ReadStorageIni(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq < 0 || !line[..eq].Trim().Equals("FullPath", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(eq + 1)..].Trim();
                return value.Length > 0 && Path.IsPathRooted(value) ? value : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>
    /// Kakudai's own folder for settings, the WebView data and (outside Windows) the generated maps:
    /// <c>%LocalAppData%\Kakudai</c>, <c>~/.local/share/Kakudai</c>, <c>~/Library/Application Support/Kakudai</c>.
    /// </summary>
    public static string KakudaiDataDir
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kakudai")
            : Path.Combine(UserDataRoot, "Kakudai");

    /// <summary>
    /// Where the new .osz files go. On Windows the <c>generated</c> folder next to Kakudai.exe, as always; elsewhere
    /// the app's folder is read-only (an .app bundle, a package), so it is under <see cref="KakudaiDataDir"/>.
    /// </summary>
    public static string GeneratedDir
        => OperatingSystem.IsWindows()
            ? Path.Combine(AppContext.BaseDirectory, "generated")
            : Path.Combine(KakudaiDataDir, "generated");

    /// <summary>Temporary files (the realm copy, the re-timed songs): <c>&lt;temp&gt;/Kakudai</c>.</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), "Kakudai");
}
