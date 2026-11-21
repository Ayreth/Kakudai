namespace Kakudai.Core.Library;

/// <param name="Hash">The file's key: its SHA-256 in lazer's <c>files/</c> folder; for an osu!stable map, a key
/// made from its path (hexadecimal too).</param>
/// <param name="Path">The file on disk when it is not in lazer's <c>files/</c> folder (osu!stable's Songs folder).</param>
public sealed record NamedFile(string Filename, string Hash, string? Path = null);

/// <param name="Files">
/// Files of the beatmap set. Empty on entries returned by <c>OsuLibrary.Refresh</c> (too slow to read for the
/// whole library): use <c>OsuLibrary.WithFiles</c> to get them for one entry.
/// </param>
/// <param name="OsuPath">The .osu file on disk, for an osu!stable map; null for lazer (found by <paramref name="OsuHash"/>).</param>
public sealed record BeatmapEntry(Guid Id, string Artist, string ArtistUnicode, string Title, string TitleUnicode,
    string Mapper, string DifficultyName, string OsuHash, double StarRating, double Bpm, double LengthMs,
    float CircleSize, float ApproachRate, float OverallDifficulty, string AudioFile, string BackgroundFile,
    string Tags, IReadOnlyList<NamedFile> Files, DateTimeOffset? LastPlayed, DateTimeOffset DateAdded,
    string? OsuPath = null)
{
    public string Display => $"{Artist} - {Title} ({Mapper}) [{DifficultyName}]";
    public string DisplayUnicode => $"{ArtistUnicode} - {TitleUnicode} ({Mapper}) [{DifficultyName}]";
    public bool IsKakudai => Tags.Split(' ').Contains("kakudai");
}
