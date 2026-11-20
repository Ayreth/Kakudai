using System.Text.RegularExpressions;
using Kakudai.Core.Library;

namespace Kakudai.Core.Export;

public static class SetFiles
{
    private static readonly Regex HitSoundName = new(
        @"^(normal|soft|drum)-(hitnormal|hitclap|hitwhistle|hitfinish|slidertick|sliderslide|sliderwhistle)\d*\.(wav|ogg|mp3)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<NamedFile> ToInclude(BeatmapEntry entry, string osuText)
    {
        var referenced = ReferencedSamples(osuText);
        var result = new List<NamedFile>();
        foreach (var file in entry.Files)
        {
            bool keep = Eq(file.Filename, entry.AudioFile)
                        || Eq(file.Filename, entry.BackgroundFile)
                        || HitSoundName.IsMatch(file.Filename)
                        || referenced.Contains(file.Filename);
            if (keep) result.Add(file);
        }
        return result;
    }

    private static bool Eq(string a, string b) =>
        !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ReferencedSamples(string osuText)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inObjects = false;
        foreach (var raw in osuText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inObjects = line == "[HitObjects]";
                continue;
            }
            if (!inObjects || line.Length == 0) continue;

            var last = line.Substring(line.LastIndexOf(',') + 1);
            if (last.Contains('|')) continue;
            if (last.Count(c => c == ':') < 4) continue;
            var name = last.Substring(last.LastIndexOf(':') + 1).Trim();
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }
}
