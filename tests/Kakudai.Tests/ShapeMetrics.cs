using System.Numerics;
using Kakudai.Core.Amplify;
using Kakudai.Core.Maps;

namespace Kakudai.Tests;

/// <summary>Where the heads and ends of the objects of a map are, after amplification.</summary>
internal sealed record Layout(Vector2[] Starts, Vector2[] Ends)
{
    /// <summary>Where the Amplifier means the objects to go (ends turned with their slider).</summary>
    public static Layout From(IReadOnlyList<MapObject> objects, IReadOnlyList<Placement> placements)
    {
        var starts = placements.Select(p => p.Start).ToArray();
        var ends = objects.Select((o, i) =>
        {
            var p = placements[i];
            float c = MathF.Cos(p.Rotation), s = MathF.Sin(p.Rotation);
            var d = o.End - o.Start;
            return p.Start + new Vector2(c * d.X - s * d.Y, s * d.X + c * d.Y);
        }).ToArray();
        return new Layout(starts, ends);
    }

    /// <summary>Where the objects of a map are, e.g. the generated map read back as the game reads it.</summary>
    public static Layout From(IReadOnlyList<MapObject> objects)
        => new(objects.Select(o => o.Start).ToArray(), objects.Select(o => o.End).ToArray());
}

/// <summary>How well an amplified map keeps the shape and the flow of its patterns.</summary>
internal static class ShapeMetrics
{
    /// <summary>Half-length of the windows the structure is measured on, in beats (spec §8.2-ter: ±1 beat).</summary>
    public const double WindowBeats = 1;

    /// <summary>Windows whose original heads spread less than this (RMS, px) are skipped: stacks, tiny patterns.</summary>
    public const double MinSpread = 10;

    /// <summary>Turning angles are only measured between moves at least this long, in px.</summary>
    public const float MinTurnMove = 30;

    private const double StreamGapInBeats = 1.15 / 4;

    /// <summary>
    /// Similarity error of every window: for each object other than a spinner, the heads within
    /// <paramref name="windowBeats"/> of it (spinners split windows) are fitted by the best similarity of their
    /// original positions: a uniform zoom plus a translation, and a rotation when <paramref name="rotation"/> is set.
    /// The error is the RMS residual over the RMS spread of the new heads, between 0 (a perfect local similarity) and
    /// 1 (no resemblance); 0.05 is about a 5 % deformation.
    /// </summary>
    public static List<double> WindowErrors(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        IReadOnlyList<Vector2> starts, double windowBeats = WindowBeats, bool rotation = true)
    {
        var errors = new List<double>();
        var segment = Segments(objects);
        var members = new List<int>();

        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.Kind == ObjectKind.Spinner) continue;
            double half = windowBeats * beatLengthAt(o.StartTime);
            int lo = i;
            while (lo > 0 && objects[lo - 1].StartTime >= o.StartTime - half) lo--;

            members.Clear();
            Vector2 sumP = Vector2.Zero, sumQ = Vector2.Zero;
            for (int j = lo; j < objects.Count && objects[j].StartTime <= o.StartTime + half; j++)
            {
                if (objects[j].Kind == ObjectKind.Spinner || segment[j] != segment[i]) continue;
                members.Add(j);
                sumP += objects[j].Start;
                sumQ += starts[j];
            }
            if (members.Count < 3) continue;

            var meanP = sumP / members.Count;
            var meanQ = sumQ / members.Count;
            double pp = 0, re = 0, im = 0, qq = 0;
            foreach (int j in members)
            {
                var p = objects[j].Start - meanP;
                var q = starts[j] - meanQ;
                pp += p.X * p.X + p.Y * p.Y;
                qq += q.X * q.X + q.Y * q.Y;
                re += p.X * q.X + p.Y * q.Y; // conj(p)·q
                im += p.X * q.Y - p.Y * q.X;
            }
            if (pp / members.Count < MinSpread * MinSpread) continue;

            double ar = re / pp, ai = rotation ? im / pp : 0; // q ≈ (ar + i·ai)·p
            double residual = 0;
            foreach (int j in members)
            {
                var p = objects[j].Start - meanP;
                var q = starts[j] - meanQ;
                double fx = ar * p.X - ai * p.Y, fy = ai * p.X + ar * p.Y;
                residual += (q.X - fx) * (q.X - fx) + (q.Y - fy) * (q.Y - fy);
            }
            errors.Add(qq <= 0 ? 1 : Math.Sqrt(residual / qq));
        }

        return errors;
    }

    /// <summary>
    /// How much the turning angles changed, in degrees: for every two consecutive moves (spinners excluded) both at
    /// least <see cref="MinTurnMove"/> long in the original map, |Δ(turning angle)| between the original and the new
    /// moves. <c>Stream</c> holds the turns whose two moves are both stream gaps (<see cref="IsStreamGap"/>).
    /// </summary>
    public static (List<double> All, List<double> Stream) TurnErrors(IReadOnlyList<MapObject> objects,
        Func<double, double> beatLengthAt, Layout layout)
    {
        var all = new List<double>();
        var stream = new List<double>();
        for (int i = 2; i < objects.Count; i++)
        {
            if (!HasMove(objects, i) || !HasMove(objects, i - 1)) continue;
            var a0 = objects[i - 1].Start - objects[i - 2].End;
            var b0 = objects[i].Start - objects[i - 1].End;
            if (a0.Length() < MinTurnMove || b0.Length() < MinTurnMove) continue;
            var a1 = layout.Starts[i - 1] - layout.Ends[i - 2];
            var b1 = layout.Starts[i] - layout.Ends[i - 1];
            if (a1.Length() < 1 || b1.Length() < 1) continue;

            double d = Math.Abs(Wrap(Angle(a1, b1) - Angle(a0, b0))) * 180 / Math.PI;
            all.Add(d);
            if (IsStreamGap(objects, beatLengthAt, i) && IsStreamGap(objects, beatLengthAt, i - 1))
                stream.Add(d);
        }
        return (all, stream);
    }

    /// <summary>
    /// Accidental overlaps: heads that land within 30 px of an object of the last 2 beats (its head or its end)
    /// although they were at least 60 px from it in the original map.
    /// </summary>
    public static int Overlaps(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt, Layout layout)
    {
        int overlaps = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Kind == ObjectKind.Spinner) continue;
            double window = 2 * beatLengthAt(objects[i].StartTime);
            for (int j = i - 2; j >= 0 && j >= i - 12; j--)
            {
                if (objects[j].Kind == ObjectKind.Spinner) break;
                if (objects[i].StartTime - objects[j].StartTime > window) break;
                float now = Math.Min(Vector2.Distance(layout.Starts[i], layout.Starts[j]),
                    Vector2.Distance(layout.Starts[i], layout.Ends[j]));
                float before = Math.Min(Vector2.Distance(objects[i].Start, objects[j].Start),
                    Vector2.Distance(objects[i].Start, objects[j].End));
                if (now < 30 && before >= 60)
                {
                    overlaps++;
                    break;
                }
            }
        }
        return overlaps;
    }

    /// <summary>A stream gap that became a jump: the move into object <c>Index</c>, its length before and after, and
    /// the local medians of the moves of its stream around it (<see cref="LocalGaps"/>) before and after.</summary>
    public readonly record struct StreamCut(int Index, double Before, double After, double MedianBefore,
        double MedianAfter);

    /// <summary>How many moves on each side of a move of a stream its local median is taken over (Kakudai 2 spec §13).
    /// </summary>
    public const int LocalGaps = 8;

    /// <summary>
    /// Stream cuts (Kakudai 2 spec §5.1, measured locally as ruled in §13). A stream is a run of consecutive moves
    /// whose gaps are stream gaps (<see cref="IsStreamGap"/>, spinners excluded; moves shorter than 1 px in the
    /// original are left out of it). A move of a stream is a cut when its new length is more than 1.5 × the median of the new lengths of
    /// its neighbours (the <see cref="LocalGaps"/> moves on each side of it and itself), while its original length was
    /// at most 1.3 × the median of their original lengths: a sudden jump against the notes around it, not a slow drift
    /// of the spacing along the stream.
    /// </summary>
    public static List<StreamCut> StreamCuts(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        Layout layout)
    {
        var cuts = new List<StreamCut>();
        var run = new List<(int Index, double Before, double After)>();

        void Close()
        {
            if (run.Count >= 2)
                for (int j = 0; j < run.Count; j++)
                {
                    var near = run.GetRange(Math.Max(0, j - LocalGaps),
                        Math.Min(run.Count - 1, j + LocalGaps) - Math.Max(0, j - LocalGaps) + 1);
                    double before = Median(near.Select(m => m.Before)), after = Median(near.Select(m => m.After));
                    var m = run[j];
                    if (m.After > 1.5 * after && m.Before <= 1.3 * before)
                        cuts.Add(new StreamCut(m.Index, m.Before, m.After, before, after));
                }
            run.Clear();
        }

        for (int i = 1; i < objects.Count; i++)
        {
            if (!HasMove(objects, i) || !IsStreamGap(objects, beatLengthAt, i))
            {
                Close();
                continue;
            }
            float d = Vector2.Distance(objects[i].Start, objects[i - 1].End);
            if (d >= 1)
                run.Add((i, d, Vector2.Distance(layout.Starts[i], layout.Ends[i - 1])));
        }
        Close();
        return cuts;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>Mean move length (≥ 1 px in the original, spinners excluded) before and after, the number of such moves,
    /// and how many are longer than 400 px after.</summary>
    public static (double Before, double After, int Moves, int Over400) Jumps(IReadOnlyList<MapObject> objects,
        Layout layout)
    {
        double before = 0, after = 0;
        int moves = 0, over = 0;
        for (int i = 1; i < objects.Count; i++)
        {
            if (!HasMove(objects, i)) continue;
            float d = Vector2.Distance(objects[i].Start, objects[i - 1].End);
            if (d < 1) continue;
            float now = Vector2.Distance(layout.Starts[i], layout.Ends[i - 1]);
            before += d;
            after += now;
            moves++;
            if (now > 400) over++;
        }
        return moves == 0 ? (0, 0, 0, 0) : (before / moves, after / moves, moves, over);
    }

    private static bool HasMove(IReadOnlyList<MapObject> objects, int i)
        => i > 0 && objects[i].Kind != ObjectKind.Spinner && objects[i - 1].Kind != ObjectKind.Spinner;

    /// <summary>Whether the move into object <paramref name="i"/> is a stream gap, as the Amplifier rules it: at most
    /// 1.15·beat/4 from the end of the previous object, and not from a slider to a slider (back-to-back sliders are
    /// slider jumps, not a stream).</summary>
    public static bool IsStreamGap(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt, int i)
        => objects[i].StartTime - objects[i - 1].EndTime <= StreamGapInBeats * beatLengthAt(objects[i].StartTime)
           && !(objects[i - 1].Kind == ObjectKind.Slider && objects[i].Kind == ObjectKind.Slider);

    private static int[] Segments(IReadOnlyList<MapObject> objects)
    {
        var segment = new int[objects.Count];
        int s = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Kind == ObjectKind.Spinner) s++;
            segment[i] = s;
        }
        return segment;
    }

    private static double Angle(Vector2 a, Vector2 b) => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);

    private static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
