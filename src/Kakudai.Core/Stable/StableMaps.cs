using System.Security.Cryptography;
using System.Text;
using Kakudai.Core.Library;

namespace Kakudai.Core.Stable;

/// <summary>The map osu!stable selected, read from its folder in Songs: the same entry lazer's database gives.</summary>
public static class StableMaps
{
    /// <summary>A set folder holds a few dozen files; more than this is not a beatmap folder.</summary>
    private const int MaxFiles = 5000;

    /// <summary>
    /// The entry of <paramref name="selection"/>, with every file of its folder (sub-folders included). Null with
    /// <paramref name="otherMode"/> for a taiko, catch or mania difficulty.
    /// </summary>
    /// <exception cref="LibraryUnavailableException">The .osu file or its folder cannot be read.</exception>
    public static BeatmapEntry? Load(StableSelection selection, out bool otherMode)
    {
        ArgumentNullException.ThrowIfNull(selection);
        otherMode = false;
        string text;
        try
        {
            text = KakudaiService.ReadText(selection.OsuPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new LibraryUnavailableException($"Could not read the map file \"{selection.OsuPath}\".", ex);
        }

        var header = OsuHeader.Parse(text);
        if (header.Mode != 0)
        {
            otherMode = true;
            return null;
        }

        var folder = Path.GetDirectoryName(selection.OsuPath)!;
        var files = new List<NamedFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (files.Count >= MaxFiles) break;
                var name = Path.GetRelativePath(folder, path).Replace('\\', '/');
                files.Add(new NamedFile(name, Key(path), path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryUnavailableException($"Could not read the map folder \"{folder}\".", ex);
        }

        DateTimeOffset added;
        try
        {
            added = File.GetCreationTimeUtc(selection.OsuPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            added = DateTimeOffset.UtcNow;
        }

        return new BeatmapEntry(IdOf(selection.Checksum, text), header.Artist, Or(header.ArtistUnicode, header.Artist),
            header.Title, Or(header.TitleUnicode, header.Title), header.Creator, header.Version, Key(selection.OsuPath),
            0, 0, 0, 0, 0, 0, header.AudioFilename, header.Background, header.Tags, files, null, added,
            selection.OsuPath);
    }

    /// <summary>The map's ID: its MD5 checksum as a GUID (the same map keeps it), else one made from its text.</summary>
    internal static Guid IdOf(string checksum, string text)
    {
        if (checksum.Length == 32 && checksum.All(char.IsAsciiHexDigit)) return new Guid(Convert.FromHexString(checksum));
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The key of a file of the Songs folder: hexadecimal, like lazer's hashes, and stable for one path.</summary>
    internal static string Key(string path) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(path)));

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}

/// <summary>What Kakudai needs from the head of a .osu file: mode, names, audio and background.</summary>
internal sealed record OsuHeader(int Mode, string Artist, string ArtistUnicode, string Title, string TitleUnicode,
    string Creator, string Version, string Tags, string AudioFilename, string Background)
{
    public static OsuHeader Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string background = "";
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                if (section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase)) break; // nothing more needed
                continue;
            }

            if (section.Equals("Events", StringComparison.OrdinalIgnoreCase))
            {
                // 0,0,"bg.jpg",0,0 — the first one is the background.
                if (background.Length == 0 && line.StartsWith("0,", StringComparison.Ordinal))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3) background = parts[2].Trim().Trim('"');
                }
                continue;
            }

            if (!section.Equals("General", StringComparison.OrdinalIgnoreCase)
                && !section.Equals("Metadata", StringComparison.OrdinalIgnoreCase))
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        string V(string key) => values.GetValueOrDefault(key) ?? "";
        int mode = int.TryParse(V("Mode"), out var m) ? m : 0;
        // Lazer and Kakudai name the files of a set with "/"; a .osu may say "bg\x.jpg".
        return new OsuHeader(mode, V("Artist"), V("ArtistUnicode"), V("Title"), V("TitleUnicode"), V("Creator"),
            V("Version"), V("Tags"), V("AudioFilename").Replace('\\', '/'), background.Replace('\\', '/'));
    }
}
