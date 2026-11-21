namespace Kakudai.Core.Stable;

/// <summary>Turns the Windows paths a game running under Wine sees into the Unix paths behind them, and back.</summary>
public static class WinePaths
{
    /// <summary>True for "C:\…", "c:/…", "\\?\C:\…" and the like.</summary>
    public static bool IsWindowsPath(string path)
    {
        var p = StripDevicePrefix(path);
        return p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':';
    }

    /// <summary>
    /// The Unix path of <paramref name="path"/> inside the Wine prefix <paramref name="prefix"/>: the drive letter
    /// goes through <c>prefix/dosdevices/x:</c> (a link to <c>drive_c</c>, to <c>/</c> for Z:, …). A path that is
    /// already a Unix path is returned as it is. Segments are matched without regard to case when the exact name does
    /// not exist, as Wine does. Null if the drive is unknown.
    /// </summary>
    public static string? ToUnix(string path, string prefix)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(prefix);
        if (path.StartsWith('/')) return ResolveCase(path);
        var p = StripDevicePrefix(path);
        if (!IsWindowsPath(p)) return null;

        var drive = char.ToLowerInvariant(p[0]);
        var root = Path.Combine(prefix, "dosdevices", drive + ":");
        if (!Directory.Exists(root))
        {
            if (drive == 'c') root = Path.Combine(prefix, "drive_c");
            else if (drive == 'z') root = "/";
            else return null;
        }

        var rest = p[2..].Replace('\\', '/').TrimStart('/');
        return ResolveCase(rest.Length == 0 ? root : Path.Combine(root, rest));
    }

    /// <summary>The path Wine shows for a Unix file: Z: is the root of the Unix filesystem in every prefix.</summary>
    public static string ToWindows(string unixPath)
    {
        ArgumentNullException.ThrowIfNull(unixPath);
        return "Z:" + unixPath.Replace('/', '\\');
    }

    /// <summary>
    /// <paramref name="path"/> with each missing segment replaced by an existing one that differs only by case. The
    /// path is returned as it is from the first segment that has no such match.
    /// </summary>
    internal static string ResolveCase(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || !path.StartsWith('/')) return path;
        var current = "/";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var exact = Path.Combine(current, segments[i]);
            if (Directory.Exists(exact) || File.Exists(exact))
            {
                current = exact;
                continue;
            }

            string? match = null;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            if (match is null) return Path.Combine([current, .. segments[i..]]);
            current = match;
        }
        return current;
    }

    private static string StripDevicePrefix(string path)
    {
        foreach (var prefix in new[] { @"\\?\", @"\??\", @"\\.\" })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return path[prefix.Length..];
        }
        return path;
    }
}
