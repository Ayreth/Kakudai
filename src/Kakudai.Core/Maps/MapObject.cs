using System.Numerics;

namespace Kakudai.Core.Maps;

public enum ObjectKind { Circle, Slider, Spinner }

/// <summary>
/// One hit object of a loaded map, in osu! playfield coordinates.
/// <paramref name="LineIndex"/> is the index of its source line in <see cref="LoadedMap.Lines"/>.
/// <paramref name="End"/> is where the object ends (slider tail after its repeats, otherwise <paramref name="Start"/>).
/// <paramref name="PathSamples"/> holds 33 points of the slider path (progress 0 to 1, step 1/32),
/// relative to <paramref name="Start"/>; it is empty for circles and spinners.
/// <paramref name="InKiai"/> is true when the object starts in a kiai section.
/// </summary>
public sealed record MapObject(int LineIndex, ObjectKind Kind, double StartTime, double EndTime,
    Vector2 Start, Vector2 End, IReadOnlyList<Vector2> PathSamples, bool InKiai = false);

/// <summary>
/// A decoded osu!standard map. <paramref name="Lines"/> is the source text split on '\n', each line keeping
/// its '\r' if it had one, so <c>string.Join("\n", Lines)</c> gives back the original text exactly.
/// </summary>
public sealed record LoadedMap(string[] Lines, IReadOnlyList<MapObject> Objects,
    Func<double, double> BeatLengthAt, string DifficultyName);
