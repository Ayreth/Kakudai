using System.Text;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;

namespace Kakudai.Core.Maps;

public readonly record struct StarInfo(double Total, double Aim, double Speed);

public static class Stars
{
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static StarInfo Calculate(string osuText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OsuGameInit.EnsureRegistered();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
        using var reader = new LineBufferedReader(stream);
        var beatmap = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        var working = new FlatWorkingBeatmap(beatmap);
        var attrs = (OsuDifficultyAttributes)new OsuRuleset().CreateDifficultyCalculator(working).Calculate(cancellationToken);
        return new StarInfo(attrs.StarRating, attrs.AimDifficulty, attrs.SpeedDifficulty);
    }
}
