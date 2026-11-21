using System.IO.Compression;
using System.Text;
using Kakudai.Core.Library;

namespace Kakudai.Core.Export;

/// <summary>A re-timed song that replaces the set's original audio file in the .osz.</summary>
/// <param name="Filename">Its name in the set, as the new .osu's <c>AudioFilename</c> says.</param>
/// <param name="Path">The file on disk.</param>
public sealed record NewAudio(string Filename, string Path);

public static class OszBuilder
{
    /// <param name="newAudio">When given, it is added and the entry's original audio file is left out: the generated
    /// map is a separate set with this one difficulty.</param>
    public static string Build(BeatmapEntry entry, string newOsuText, string newVersion,
        IReadOnlyList<NamedFile> files, Func<string, string> hashToPath, string outputDir, NewAudio? newAudio = null)
    {
        Directory.CreateDirectory(outputDir);
        var oszPath = Path.Combine(outputDir, Sanitize($"{entry.Artist} - {entry.Title} [{newVersion}].osz"));
        var osuName = Sanitize($"{entry.Artist} - {entry.Title} ({entry.Mapper}) [{newVersion}] (kakudai).osu");

        if (File.Exists(oszPath)) File.Delete(oszPath);
        using var fs = new FileStream(oszPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var osuEntry = zip.CreateEntry(osuName);
        using (var w = osuEntry.Open())
        {
            var bytes = new UTF8Encoding(false).GetBytes(newOsuText);
            w.Write(bytes, 0, bytes.Length);
        }

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { osuName };
        if (newAudio is not null)
        {
            var name = SanitizePath(newAudio.Filename);
            if (name.Length > 0 && added.Add(name))
                AddFile(zip, newAudio.Path, name, "the re-timed audio", "from the audio cache");
        }

        foreach (var file in files)
        {
            bool isAudio = !string.IsNullOrEmpty(entry.AudioFile)
                           && string.Equals(file.Filename, entry.AudioFile, StringComparison.OrdinalIgnoreCase);
            if (isAudio && newAudio is not null) continue;

            var path = hashToPath(file.Hash);
            var name = SanitizePath(file.Filename);
            if (name.Length == 0) continue;
            if (!File.Exists(path))
            {
                // A missing background or sample only costs the set a file; a missing song would make it silent.
                if (isAudio)
                    throw new LibraryUnavailableException("The map's audio file is missing from the game folder.");
                continue;
            }
            if (!added.Add(name)) continue;
            AddFile(zip, path, name, "the file", "from the game folder");
        }
        return oszPath;
    }

    /// <summary>
    /// Copies a file into the archive; a failure to read it is reported as a library problem, as
    /// "Could not read {what} "{name}" {from} (…)".
    /// </summary>
    private static void AddFile(ZipArchive zip, string path, string name, string what, string from)
    {
        FileStream source;
        try
        {
            source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryUnavailableException($"Could not read {what} \"{name}\" {from} ({ex.Message}).", ex);
        }

        using (source)
        {
            using var target = zip.CreateEntry(name).Open();
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                try
                {
                    read = source.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex)
                {
                    throw new LibraryUnavailableException($"Could not read {what} \"{name}\" {from} ({ex.Message}).", ex);
                }
                if (read == 0) break;
                target.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>
    /// Cleans each segment of a path inside the set (folders such as <c>bg/x.jpg</c> are kept) and joins them
    /// with <c>/</c>. Empty, <c>.</c> and <c>..</c> segments are dropped so nothing can leave the archive root.
    /// </summary>
    internal static string SanitizePath(string name)
    {
        var segments = name.Split('/', '\\')
            .Select(Sanitize)
            .Where(s => s.Length > 0 && s != "." && s != "..");
        return string.Join('/', segments);
    }

    /// <summary>
    /// Replaces what Windows refuses in a file name, on every system: the .osz may be opened by a game on Windows or
    /// under Wine, and the file names in it are the ones osu!stable writes to its Songs folder.
    /// </summary>
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
