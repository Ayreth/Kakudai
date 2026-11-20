namespace Kakudai.Core.Library;

public sealed record NamedFile(string Filename, string Hash);

/// <param name="Files">
/// Files of the beatmap set. Empty on entries returned by <c>OsuLibrary.Refresh</c> (too slow to read for the
/// whole library): use <c>OsuLibrary.WithFiles</c> to get them for one entry.
/// </param>
public sealed record BeatmapEntry(Guid Id, string Artist, string ArtistUnicode, string Title, string TitleUnicode,
    string Mapper, string DifficultyName, string OsuHash, double StarRating, double Bpm, double LengthMs,
    float CircleSize, float ApproachRate, float OverallDifficulty, string AudioFile, string BackgroundFile,
    string Tags, IReadOnlyList<NamedFile> Files, DateTimeOffset? LastPlayed, DateTimeOffset DateAdded)
{
    public string Display => $"{Artist} - {Title} ({Mapper}) [{DifficultyName}]";
    public string DisplayUnicode => $"{ArtistUnicode} - {TitleUnicode} ({Mapper}) [{DifficultyName}]";
    public bool IsKakudai => Tags.Split(' ').Contains("kakudai");
}
