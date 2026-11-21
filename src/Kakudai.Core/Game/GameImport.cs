using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Kakudai.Core.Stable;
using osu.Framework.Platform;
using osu.Game.IPC;

namespace Kakudai.Core.Game;

/// <summary>
/// Hands a new .osz to the game the map came from, which imports it itself (Kakudai never writes in the game's
/// folders), as osu-trainer does:
/// <list type="bullet">
/// <item>osu!stable: started again with the file (osu!.exe, through Wine on Linux); the running game takes it.</item>
/// <item>lazer and Torii: Windows opens the file with its program for .osz; on Linux and macOS, where that
/// association is often missing, the file is sent to the running game through its IPC pipe, as a second osu! would.</item>
/// </list>
/// </summary>
public static class GameImport
{
    /// <summary>Lazer's IPC pipe (OsuGame.IPC_PIPE_NAME).</summary>
    private const string LazerPipe = "osu-lazer";

    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Opens <paramref name="oszPath"/> in the game. Returns null, or a message for the player saying what
    /// failed and where the .osz is.</summary>
    /// <param name="client">"stable", "lazer" or "torii": where the map was selected.</param>
    /// <param name="stable">The running osu!stable, for a map selected there.</param>
    public static string? Open(string oszPath, string? client, StableGame? stable)
    {
        ArgumentException.ThrowIfNullOrEmpty(oszPath);
        var name = Path.GetFileName(oszPath);

        if (client == "stable")
        {
            string? why = null;
            if (stable is not null && TryStartStable(stable, oszPath, out why)) return null;
            if (OperatingSystem.IsWindows() && TryShellOpen(oszPath, out _)) return null;
            return $"The .osz was created ({name}) but osu! could not be started with it{Reason(why)}. "
                   + $"Drag it from \"{Path.GetDirectoryName(oszPath)}\" onto the osu! window.";
        }

        if (OperatingSystem.IsWindows())
        {
            if (TryShellOpen(oszPath, out var shellError) || TrySendToLazer(oszPath)) return null;
            return $"The .osz was created ({name}) but Windows could not open it: {shellError}";
        }

        if (TrySendToLazer(oszPath)) return null;
        if (OperatingSystem.IsMacOS() && TryShellOpen(oszPath, out _)) return null;
        return $"The .osz was created ({name}) but the game did not take it. "
               + $"Drag it from \"{Path.GetDirectoryName(oszPath)}\" onto the osu! window.";
    }

    private static string Reason(string? why) => string.IsNullOrEmpty(why) ? "" : $" ({why})";

    private static bool TryShellOpen(string path, out string? error)
    {
        error = null;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- osu!stable

    /// <summary>Starts osu!.exe again with the file, as a double-click on an .osz does: the running osu! imports it
    /// and the new process exits.</summary>
    private static bool TryStartStable(StableGame game, string oszPath, out string? error)
    {
        error = null;
        try
        {
            var start = game.Wine is { } wine ? WineStart(wine, oszPath) : new ProcessStartInfo(game.ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = game.GameDir,
                ArgumentList = { oszPath },
            };
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// <c>wine osu!.exe Z:\…\map.osz</c>, in the game's own environment (same WINEPREFIX, same wine build and
    /// settings), so that the new process reaches the running game through the same wineserver.
    /// </summary>
    internal static ProcessStartInfo WineStart(WineLaunch wine, string oszPath)
    {
        var start = new ProcessStartInfo(wine.Loader)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (wine.WorkingDirectory is { } cwd && Directory.Exists(cwd)) start.WorkingDirectory = cwd;
        start.Environment.Clear();
        foreach (var (key, value) in wine.Environment) start.Environment[key] = value;
        start.Environment["WINEPREFIX"] = wine.Prefix;
        start.ArgumentList.Add(wine.ExeArgument);
        start.ArgumentList.Add(WinePaths.ToWindows(Path.GetFullPath(oszPath)));
        return start;
    }

    // ---------------------------------------------------------------- lazer, Torii

    /// <summary>
    /// Sends the file to the running lazer (or Torii) over its IPC pipe, as <c>osu! file.osz</c> would. The game only
    /// takes a message whose type names its own osu.Game version, so that version is read from the osu.Game.dll next
    /// to the running game. False if no game answers.
    /// </summary>
    private static bool TrySendToLazer(string oszPath)
    {
        // A game ignores a message for another version without a word, so with lazer and Torii both running each
        // version goes to each pipe: only the right pair imports.
        var versions = RunningLazerVersions().ToList();
        if (versions.Count == 0) return false;
        var sent = false;
        foreach (var pipe in OpenPipes())
        {
            foreach (var version in versions)
            {
                try
                {
                    using var ipc = new NamedPipeIpcProvider(pipe);
                    var message = new IpcMessage
                    {
                        Type = ImportMessageType(version),
                        Value = new ArchiveImportMessage { Path = Path.GetFullPath(oszPath) },
                    };
                    if (ipc.SendMessageAsync(message).Wait(IpcTimeout)) sent = true;
                }
                catch (Exception ex) when (ex is AggregateException or IOException or TimeoutException
                                               or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Nobody listening on that pipe after all.
                }
            }
        }
        return sent;
    }

    /// <summary>What osu!framework prefixes its pipe names with.</summary>
    private const string FrameworkPipePrefix = "osu-framework-";

    /// <summary>
    /// The IPC pipes of the osu!framework games running for this user (lazer's "osu-lazer", Torii's own…): Windows
    /// lists them in <c>\\.\pipe\</c>, .NET on Linux and macOS makes them sockets named <c>CoreFxPipe_…</c> in the
    /// temp folder. Lazer's alone when they cannot be listed.
    /// </summary>
    internal static IEnumerable<string> OpenPipes()
    {
        var names = new List<string>();
        try
        {
            var (dir, prefix) = OperatingSystem.IsWindows()
                ? (@"\\.\pipe\", FrameworkPipePrefix)
                : (Path.GetTempPath(), "CoreFxPipe_" + FrameworkPipePrefix);
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var pipe = name[prefix.Length..];
                if (pipe.Length > 0 && !names.Contains(pipe) && !pipe.Contains("tournament", StringComparison.OrdinalIgnoreCase))
                    names.Add(pipe);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [LazerPipe];
        }
        return names;
    }

    /// <summary>The type name of an import message for an osu.Game of this version.</summary>
    internal static string ImportMessageType(Version version)
        => $"{typeof(ArchiveImportMessage).FullName}, osu.Game, Version={version}, Culture=neutral, PublicKeyToken=null";

    /// <summary>The osu.Game versions of the lazer and Torii processes running (64-bit osu!.exe on Windows).</summary>
    private static IEnumerable<Version> RunningLazerVersions()
    {
        var versions = new List<Version>();
        foreach (var exe in RunningLazerExecutables())
        {
            var dll = Path.Combine(Path.GetDirectoryName(exe) ?? "", "osu.Game.dll");
            try
            {
                if (File.Exists(dll) && AssemblyName.GetAssemblyName(dll).Version is { } v && !versions.Contains(v)) versions.Add(v);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or FileLoadException)
            {
            }
        }
        return versions;
    }

    private static IEnumerable<string> RunningLazerExecutables()
    {
        if (OperatingSystem.IsLinux())
        {
            // /proc/<pid>/exe: the AppImage's mounted binary, or the packaged one.
            foreach (var dir in SafeEnumerate("/proc"))
            {
                var pid = Path.GetFileName(dir);
                if (!pid.All(char.IsAsciiDigit)) continue;
                string comm;
                try
                {
                    comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (comm is not ("osu!" or "torii" or "Torii")) continue;
                string? target = null;
                try
                {
                    target = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                if (target is not null) yield return target;
            }
            yield break;
        }

        foreach (var name in new[] { "osu!", "torii", "Torii" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? exe = null;
                using (process)
                {
                    try
                    {
                        if (OperatingSystem.IsWindows() && name == "osu!" && WindowsProcessMemory.Is32Bit(process)) continue; // stable
                        exe = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                    }
                }
                if (exe is not null) yield return exe;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
