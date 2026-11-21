using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Kakudai.Core.Maps;

namespace Kakudai.Core.Amplify;

/// <param name="Factor">Jump factor k (1 to 3).</param>
/// <param name="SpaceStreams">When false, stream notes keep their original spacing.</param>
/// <param name="AccentAngles">Makes the turns of the jumps sharper (Kakudai 2 spec §5.2).</param>
/// <param name="KiaiOnly">Only objects starting in a kiai section move (Kakudai 2 spec §5.3).</param>
public sealed record AmplifyOptions(double Factor, bool SpaceStreams, bool AccentAngles = false, bool KiaiOnly = false);

/// <summary>
/// Where an object goes: its new head position, and the rotation (radians, around the head, applied with
/// R(θ) = [[cos θ, −sin θ], [sin θ, cos θ]] in osu! coordinates) to apply to its shape. Since Kakudai 3 every object
/// keeps its shape as it is: the rotation is always 0, so a slider is only moved. An object that does not move has
/// exactly its original head, so its line is copied as is.
/// </summary>
public readonly record struct Placement(Vector2 Start, float Rotation);

/// <param name="Placements">One placement per input object, in the same order.</param>
/// <param name="Corrected">Objects whose move from the previous one leaves the direction it has in the map by more
/// than 2°, to stay in the playfield or for the accented angles.</param>
/// <param name="Reduced">Objects whose move from the previous one is shorter than 0.9 times the wanted length.</param>
public sealed record AmplifyResult(IReadOnlyList<Placement> Placements, int Corrected, int Reduced,
    double AverageJumpBefore, double AverageJumpAfter);

/// <summary>
/// Spreads the moves of a map while keeping its structure (Kakudai 3, docs/design.md §8.2-quater).
/// <para>
/// Every move (from the end of an object to the head of the next) keeps about the direction it has in the map and is
/// only made longer, from its own length up to g(length). Nothing is moved closer than it was: a vertical jump stays
/// vertical, a pattern keeps its orientation, a stream keeps its curve, and no jump gets shorter than in the map. The
/// one exception: a stream that cannot be spaced where it is (along an edge of a map that already fills the
/// playfield) is turned as one piece by the smallest angle at which it fits, and the move out of it re-aims as little
/// as it can (see <see cref="Turns"/>). The positions are the solution of one convex problem:
/// </para>
/// <list type="bullet">
/// <item>every head (and every point of a slider path) stays in the playfield;</item>
/// <item>each move is a vector a·u + b·u⊥ in the frame of its original direction u, with |m| ≤ a ≤ g(|m|) and
/// |b| ≤ tan(<see cref="ConeDegrees"/>)·a: it may leave its direction by that much at most;</item>
/// <item>the cost asks every move to reach g(|m|), and asks two consecutive moves of a phrase to grow alike and to
/// keep the turn between them (strongly inside a stream: a stream is zoomed as one piece and keeps its curve).</item>
/// </list>
/// <para>
/// It is solved by ADMM (alternating projections on the playfield boxes and on the move sets, with a banded linear
/// solve in between). A convex problem has one solution that changes continuously with k: a slightly larger factor
/// gives a slightly larger map, never a different one.
/// </para>
/// </summary>
public static class Amplifier
{
    /// <summary>C of g(d) = max(d, C·tanh(k·d / C)), in osu! pixels: short moves are multiplied by about k, long
    /// ones are capped below C.</summary>
    internal const double CapPx = 560;

    /// <summary>Moves shorter than this (stacks) are kept exactly as they are.</summary>
    private const float StackPx = 3;

    /// <summary>A head that comes at most this many beats after the end of the previous object is a step of a stream,
    /// unless both are sliders (back-to-back sliders are slider jumps).</summary>
    private const double StreamGapInBeats = 1.15 / 4;

    /// <summary>A gap of at least this many beats ends a phrase: the moves on each side are not tied together.</summary>
    private const double PauseBeats = 1;

    /// <summary>A gap of at least this many beats (or a spinner) restarts the map: the move across it is free.</summary>
    private const double RestartBeats = 4;

    /// <summary>Tolerance on the time thresholds, in ms.</summary>
    private const double TimeSlackMs = 1;

    // Lab (120 maps of the player's library, k from 1.5 to 3 by 0.1, star ratings with the osu! calculator), k = 2:
    //   2.2.1:     jumps ×1.57, turned by 65° on average (64 % by more than 30°), streams ×1.78 (tenth ×1.38) with 105
    //              bends of more than 20° per 1000 stream notes; the star rating fell 175 times when k grew by 0.1, on
    //              69 maps, by up to 1.16.
    //   Kakudai 3: jumps ×1.51, turned by 9°, streams ×1.66 (tenth ×1.41) with 7 such bends; it never fell.
    //   The moves kept in their exact directions give jumps ×1.33 and streams ×1.28: the playfield is too small.

    /// <summary>How far a move may leave its original direction (a jump; a stream step). 10° gives jumps ×1.50 but
    /// turns changed by 10°; the turn ties below keep 25° from bending a pattern. A stream may curve a little more, as
    /// one smooth piece (25°: streams ×1.54; 30°: ×1.66; 60°: ×1.67, but a single step could then turn far enough to
    /// be 1.5 times as long as its neighbours: 13 stream cuts on the 120 maps against 4 at 30°).</summary>
    internal const double ConeDegrees = 25;

    private const double StreamConeDegrees = 30;

    // Streams against an edge (docs/design.md §8.2-quinquies). Lab, 120 maps at k = 2: streams ×1.66 → ×1.70 (tenth
    // ×1.41 → ×1.43, spread 1.091 → 1.078, cuts 4 → 3, bends 7 → 10 per 1000 notes), SR +1.10 → +1.12, never falling
    // as k grows; jumps unchanged. AliA - utopia [Feelings], whose streams run along the edges of a map that already
    // fills the playfield: ten streams gain 0.10 to 0.31 (×1.60 → ×1.91 at 141 s).

    /// <summary>A stream is turned by this much at most.</summary>
    private const double BurstMaxTurnDegrees = 60;

    /// <summary>A stream that reaches less than this share of its growth where it is, is turned (by the smallest angle
    /// at which it reaches it)...</summary>
    private const double BurstReach = 0.95;

    /// <summary>...or, when no turn reaches it, by the angle that reaches the most, if that gains this share. A turn
    /// is kept when its stream then grows by this much more (×1.40 → ×1.43 at least), and no stream within
    /// <see cref="BurstSpacingObjects"/> of it loses more than <see cref="BurstLoss"/> (without this check, a burst of
    /// AliA - utopia [Feelings] next to a turned stream fell from ×1.40 to ×1.04).</summary>
    private const double BurstGain = 0.03, BurstLoss = 0.02;

    /// <summary>Streams of at least this many moves (3 notes) may be turned.</summary>
    private const int MinBurstMoves = 2;

    /// <summary>Rounds of turns (each solved again), and how many objects after a turned stream wait for the next
    /// round: turning one moves what follows.</summary>
    private const int BurstRounds = 6, BurstSpacingObjects = 16;

    /// <summary>Which streams to turn, and by how much, is decided once per map at this factor (streams grow no further
    /// past <see cref="StreamFactorCap"/>); the turns are whole from <see cref="FullTurnFactor"/> on, and grow from
    /// nothing at k = 1 below it.</summary>
    private const double ReferenceFactor = 2.5, FullTurnFactor = 2;

    /// <summary>A turned stream takes this share of the curve it had at the reference factor (its whole curve: 17 bends
    /// per 1000 notes at k = 2; none, streams ×1.67).</summary>
    private const double CurveShare = 0.5;

    /// <summary>A turned stream may still leave its turned directions by this much.</summary>
    private const double TurnedConeDegrees = 15;

    /// <summary>Weight of the move out of a turned stream leaving its original direction (its share across, squared):
    /// it re-aims as little as it can (aimed at where the next object was, it would turn back on itself).</summary>
    private const double SoftAimWeight = 0.2;

    /// <summary>The move out of a turned stream keeps at least this share of its length along its original direction
    /// (free: one of them fell to 14 % of its length; never shorter: the stream along the edge of the tests ×1.55
    /// instead of ×1.60).</summary>
    private const double SoftLeast = 0.8;

    /// <summary>A stream is spaced as by k up to this factor, no further: at k = 3 a dense stream wanted ×2.9 and could
    /// only fit by curling (71 bends per 1000 notes, the zoom spread 1.19 within a stream); capped at 2.5, streams ×1.84
    /// (tenth ×1.52), 33 bends, spread 1.15 (2.2.1 at k = 3: ×1.77, tenth ×1.19, 94 bends).</summary>
    private const double StreamFactorCap = 2.5;

    /// <summary>Weight of the gap between the growth (new length / original length) of two consecutive moves of a
    /// phrase: in a stream (10 lets a stream go from ×1.7 to ×2.1 along 12 notes, a spread of 1.19 within the
    /// stream; 100, 1.10), and between jumps (0.5 turns a square into a trapezoid to win a few pixels).</summary>
    private const double StreamGrowthTie = 100, JumpGrowthTie = 2;

    /// <summary>Weight of the change of the turn between two consecutive moves of a phrase: in a stream (20 keeps its
    /// curve and spaces it ×1.54; 2 lets it curve back from the edges, ×1.67, still without bends; 0.3, ×1.86 with 93
    /// bends per 1000 notes), and elsewhere (5: jumps ×1.44).</summary>
    private const double StreamTurnTie = 2, JumpTurnTie = 1;

    /// <summary>Weight of an accented turn (it pulls the turn toward its accented value).</summary>
    private const double AccentTurnTie = 4;

    /// <summary>A turn τ between two jumps is aimed at τ + A·|sin τ| toward 180° with accented angles, A = 30°: 90°
    /// becomes 120°, while a straight line and a back and forth stay so.</summary>
    private const double AccentRadians = 30 * Math.PI / 180;

    /// <summary>Moves at least this long that are neither stream steps nor restarts are jumps, whose turns are
    /// accented.</summary>
    private const float AccentMovePx = 30;

    /// <summary>Weight of each move reaching its wanted length (the costs are dimensionless).</summary>
    private const double GrowWeight = 1;

    /// <summary>A very weak pull of every head toward its original spot: it settles where a part of the map goes when
    /// nothing else decides it (after a restart), without holding any jump back (1e-5 kept a back and forth of 300 px
    /// jumps from centering itself).</summary>
    private const double AnchorWeight = 1e-7;

    /// <summary>ADMM penalty, in 1/px² (the costs are dimensionless; 1e-4 converges no better on real maps), and
    /// over-relaxation (Boyd et al. §3.4.3).</summary>
    private const double Rho = 1e-3, Relax = 1.6;

    private const int MaxIterations = 2000;
    private const int MinIterations = 150;

    /// <summary>The iterations stop when no head moves by more than this from one to the next and the constraints
    /// hold within it. At 0.02 px the star rating could still fall by a few hundredths when k grew (7 times on the 120
    /// maps); at 0.002, never. Worst map 336 ms, 108 ms on average.</summary>
    private const double TolerancePx = 0.002;

    /// <summary>Kept inside the areas when fitting, so that float rounding never puts a point just outside.</summary>
    private const float FitSlack = 0.01f;

    /// <summary>A moved head goes there (1 px from the edge for the rounding at writing).</summary>
    private static readonly Vector2 HeadMin = new(1, 1), HeadMax = new(511, 383);

    /// <summary>A slider path goes there (2 px from the edge for the rounding of the control points).</summary>
    private static readonly Vector2 BodyMin = new(2, 2), BodyMax = new(510, 382);

    /// <summary>The amplified length of a move of <paramref name="d"/> px: g(d) = max(d, C·tanh(k·d / C)).</summary>
    internal static double Grow(double d, double k) => Math.Max(d, CapPx * Math.Tanh(k * d / CapPx));

    public static AmplifyResult Amplify(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        AmplifyOptions options)
        => Amplify(objects, beatLengthAt, options, out _);

    /// <param name="revertedObjects">Objects put back on their original spot by the final check (none in practice:
    /// the solution keeps every object in its box).</param>
    internal static AmplifyResult Amplify(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        AmplifyOptions options, out int revertedObjects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(beatLengthAt);
        ArgumentNullException.ThrowIfNull(options);

        revertedObjects = 0;
        int n = objects.Count;
        var placements = new Placement[n];
        for (int i = 0; i < n; i++) placements[i] = new Placement(objects[i].Start, 0);
        if ((options.Factor == 1 && !options.AccentAngles) || n < 2)
            return Result(objects, placements, 0, 0); // exactly, by a direct path

        var beats = new double[n];
        for (int i = 0; i < n; i++) beats[i] = beatLengthAt(objects[i].StartTime);
        var turns = Turns(objects, beats, options);
        var problem = new Problem(objects, beats, options, turns);
        var heads = problem.Solve();
        if (options.AccentAngles) (problem, heads) = WithoutNewOverlaps(objects, beats, options, turns, problem, heads);

        int corrected = 0, reduced = 0;
        for (int i = 0; i < n; i++)
        {
            var p = new Placement(heads[i], 0);
            if (!Valid(objects[i], p))
            {
                p = new Placement(objects[i].Start, 0);
                revertedObjects++;
            }
            placements[i] = p;
        }
        for (int i = 1; i < n; i++)
        {
            if (!problem.IsRay(i)) continue;
            var move = placements[i].Start - (placements[i - 1].Start + (objects[i - 1].End - objects[i - 1].Start));
            var (along, across) = problem.Frame(i, move);
            if (Math.Abs(Math.Atan2(across, along)) > 2 * Math.PI / 180) corrected++;
            if (problem.Grows(i) && move.Length() < 0.9 * problem.Wanted(i)) reduced++;
        }
        return Result(objects, placements, corrected + revertedObjects, reduced);
    }

    /// <summary>
    /// Accented angles must add no overlap (Kakudai 2 spec §5.2): where the accented map lands an object on a recent one
    /// (<see cref="Overlaps"/>) that the map without the option does not, the accents of the turns from the recent
    /// object to it are dropped and the map is solved again, a few rounds; past them, the map is the one without the
    /// option.
    /// </summary>
    private static (Problem, Vector2[]) WithoutNewOverlaps(IReadOnlyList<MapObject> objects, double[] beats,
        AmplifyOptions options, BurstTurn[] turns, Problem problem, Vector2[] heads)
    {
        // Without the option: at k = 1, the map as it is.
        var plain = new Problem(objects, beats, options with { AccentAngles = false }, turns);
        var plainHeads = options.Factor == 1 ? objects.Select(o => o.Start).ToArray() : plain.Solve();
        var excused = new bool[objects.Count];
        foreach (var (i, _) in Overlaps(objects, beats, plainHeads, ExcusedPx)) excused[i] = true;
        var dropped = new bool[objects.Count];
        for (int round = 0; ; round++)
        {
            bool any = false;
            foreach (var (i, j) in Overlaps(objects, beats, heads, AccentCheckPx))
            {
                if (excused[i]) continue;
                any = true;
                for (int m = Math.Max(0, j - AccentWiden[Math.Min(round, AccentWiden.Length - 1)]); m <= i; m++) dropped[m] = true;
            }
            if (!any) return (problem, heads);
            if (round == AccentRounds) return (plain, plainHeads);
            problem.DropAccents(dropped);
            heads = problem.Solve();
        }
    }

    /// <summary>Rounds of <see cref="WithoutNewOverlaps"/> before giving the accents up.</summary>
    private const int AccentRounds = 6;

    /// <summary>Per round, how many objects before the recent object the accents are dropped for too.</summary>
    private static readonly int[] AccentWiden = [0, 2, 4, 8, 16, 32];

    /// <summary>An object closer than this to a recent object it was far from overlaps it: the distance at which the
    /// overlaps are counted (30 px), plus 2 px for the rounding at writing.</summary>
    private const float AccentCheckPx = 32;

    /// <summary>An overlap without the option excuses the same object with it only when it is closer than this: 30 px
    /// minus 2 px for the rounding at writing.</summary>
    private const float ExcusedPx = 28;

    /// <summary>An object landing on a recent one was at least this far from it in the map.</summary>
    private const float FarPx = 60;

    /// <summary>
    /// (i, j) for every head i that lands closer than <paramref name="px"/> to the head or the end of an object j of
    /// the 12 before it within 2 beats (the previous one excepted, no spinner between), although it was at least
    /// <see cref="FarPx"/> from both in the map.
    /// </summary>
    private static List<(int I, int J)> Overlaps(IReadOnlyList<MapObject> objects, double[] beats, Vector2[] heads,
        float px)
    {
        var pairs = new List<(int, int)>();
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.Kind == ObjectKind.Spinner) continue;
            for (int j = i - 2; j >= 0 && j >= i - 12; j--)
            {
                if (objects[j].Kind == ObjectKind.Spinner || o.StartTime - objects[j].StartTime > 2 * beats[i]) break;
                var end = heads[j] + (objects[j].End - objects[j].Start);
                float now = Math.Min(Vector2.Distance(heads[i], heads[j]), Vector2.Distance(heads[i], end));
                if (now >= px) continue;
                float before = Math.Min(Vector2.Distance(o.Start, objects[j].Start), Vector2.Distance(o.Start, objects[j].End));
                if (before >= FarPx) pairs.Add((i, j));
            }
        }
        return pairs;
    }

    /// <summary>A stream turned as one piece: its moves Start..End, the turn, the directions it is turned from (as
    /// the stream curved at the reference factor), and how much of them is taken at this factor.</summary>
    private sealed record BurstTurn(int Start, int End, double Angle, Vector2[] Directions, double Share = 1);

    private static readonly ConditionalWeakTable<IReadOnlyList<MapObject>, ConcurrentDictionary<bool, BurstTurn[]>> Plans = new();

    /// <summary>
    /// The streams to turn, at this factor. Which ones and by how much is decided once per map, at the
    /// <see cref="ReferenceFactor"/> (streams grow no further past it), so that it never changes with k; the turns
    /// then grow continuously with k from nothing at k = 1, and the map still changes continuously with k.
    /// </summary>
    private static BurstTurn[] Turns(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options)
    {
        if (!options.SpaceStreams) return [];
        var plans = Plans.GetValue(objects, _ => new ConcurrentDictionary<bool, BurstTurn[]>());
        var plan = plans.GetOrAdd(options.KiaiOnly, _ =>
            Plan(objects, beats, options with { Factor = ReferenceFactor, AccentAngles = false }));
        double share = Math.Clamp((options.Factor - 1) / (FullTurnFactor - 1), 0, 1);
        double curve = CurveShare * Math.Clamp((options.Factor - 1) / (ReferenceFactor - 1), 0, 1);
        return plan.Select(t => t with { Angle = share * t.Angle, Share = curve }).ToArray();
    }

    /// <summary>
    /// At the reference factor: the streams that cannot reach their spacing where they are (against an edge of a map
    /// that already fills the playfield), each turned as one piece by the smallest angle at which it fits. Turning one
    /// moves what follows, so only the first of a stretch is turned per round, and the map is solved again between the
    /// rounds. A turn is kept only when its stream gains and no stream near it loses: it would only move the problem.
    /// </summary>
    private static BurstTurn[] Plan(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options)
    {
        var accepted = new List<BurstTurn>();
        var refused = new HashSet<int>();
        var problem = new Problem(objects, beats, options, accepted);
        var runs = problem.Runs();
        var heads = problem.Solve();
        for (int round = 0; round < BurstRounds; round++)
        {
            var taken = accepted.Select(t => t.Start).Concat(refused).ToHashSet();
            var proposed = problem.Propose(heads, runs, taken);
            if (proposed.Count == 0) break;
            // What matters is the growth up to the wanted one (a stream may grow past it, by its cone).
            var before = runs.Select(r => problem.Growth(heads, r.Start, r.End)).ToArray();
            var trial = new Problem(objects, beats, options, [.. accepted, .. proposed]);
            trial.StartFrom(problem);
            var trialHeads = trial.Solve();
            var after = runs.Select(r => trial.Growth(trialHeads, r.Start, r.End)).ToArray();
            bool all = true;
            foreach (var t in proposed)
            {
                bool good = true;
                for (int r = 0; r < runs.Count && good; r++)
                {
                    var (a, b) = runs[r];
                    if (b < t.Start - BurstSpacingObjects || a > t.End + BurstSpacingObjects) continue;
                    double wanted = problem.WantedGrowth(a, b);
                    good = a == t.Start
                        ? after[r] >= before[r] + BurstGain
                        : Math.Min(after[r], wanted) >= Math.Min(before[r], wanted) - BurstLoss;
                }
                if (good) accepted.Add(t);
                else
                {
                    refused.Add(t.Start);
                    all = false;
                }
            }
            if (all) (problem, heads) = (trial, trialHeads);
            else
            {
                var kept = new Problem(objects, beats, options, accepted);
                kept.StartFrom(problem);
                (problem, heads) = (kept, kept.Solve());
            }
        }
        return accepted.ToArray();
    }

    private static AmplifyResult Result(IReadOnlyList<MapObject> objects, Placement[] placements, int corrected,
        int reduced)
    {
        var (before, after) = AverageJumps(objects, placements);
        return new AmplifyResult(placements, corrected, reduced, before, after);
    }

    /// <summary>The convex problem of one map (see <see cref="Amplifier"/>), and its ADMM solver.</summary>
    private sealed class Problem
    {
        private enum Move { Free, Fixed, Ray, Soft }

        private readonly IReadOnlyList<MapObject> objects;
        private readonly int n;
        private readonly Vector2[] lo, hi;      // box of each head
        private readonly Move[] kind;           // the move into object i (i ≥ 1)
        private readonly Vector2[] unit, body;  // original direction (or the whole move when fixed); body of object i−1
        private readonly double[] length, top;  // original and wanted lengths
        private readonly double[] cone;         // tan of how far the move may leave its direction
        private readonly double[] growthTie, turnTie, turnTarget; // pair (i−1, i)
        private readonly double[] anchor;

        // Solver state, kept from one solve to the next (a warm start after DropAccents).
        private double[]? q, zq, yq, zd, yd, constant;
        private Banded? system;
        private readonly bool[] isStream;
        private Vector2[] original = [];        // the directions in the map, before any stream is turned

        public Problem(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options,
            IReadOnlyList<BurstTurn> turns)
        {
            this.objects = objects;
            n = objects.Count;
            lo = new Vector2[n];
            hi = new Vector2[n];
            kind = new Move[n];
            unit = new Vector2[n];
            body = new Vector2[n];
            length = new double[n];
            top = new double[n];
            cone = new double[n];
            growthTie = new double[n];
            turnTie = new double[n];
            turnTarget = new double[n];
            anchor = new double[n];

            for (int i = 0; i < n; i++)
            {
                var o = objects[i];
                // With only the kiai, every object outside a kiai section keeps its place, and so does the first one of
                // each section: the section is spread from there.
                bool pinned = o.Kind == ObjectKind.Spinner
                              || (options.KiaiOnly && (!o.InKiai || i == 0 || !objects[i - 1].InKiai));
                Vector2 a = HeadMin, b = HeadMax;
                foreach (var s in o.PathSamples)
                {
                    a = Vector2.Max(a, BodyMin - s);
                    b = Vector2.Min(b, BodyMax - s);
                }
                // An object that fits nowhere (a slider wider than the playfield) stays where it is. One that went past
                // the edge in the map is brought inside.
                // Shrunk a little, so that float rounding never puts a point just outside.
                (a, b) = (a + new Vector2(FitSlack), b - new Vector2(FitSlack));
                if (pinned || a.X > b.X || a.Y > b.Y) a = b = o.Start;
                (lo[i], hi[i]) = (a, b);
                anchor[i] = AnchorWeight;
            }

            double k = options.Factor;
            var stream = isStream = new bool[n];
            var pause = new bool[n];
            for (int i = 1; i < n; i++)
            {
                var (p, c) = (objects[i - 1], objects[i]);
                body[i] = p.End - p.Start;
                double gap = c.StartTime - p.EndTime;
                if (p.Kind == ObjectKind.Spinner || c.Kind == ObjectKind.Spinner
                    || gap >= RestartBeats * beats[i] - TimeSlackMs)
                    continue; // free
                var v = c.Start - p.End;
                if (lo[i] == hi[i] && lo[i - 1] == hi[i - 1])
                {
                    kind[i] = Move.Fixed; // both kept in place
                    unit[i] = v;
                    continue;
                }
                stream[i] = gap <= StreamGapInBeats * beats[i] + TimeSlackMs
                            && !(p.Kind == ObjectKind.Slider && c.Kind == ObjectKind.Slider);
                pause[i] = gap >= PauseBeats * beats[i] - TimeSlackMs;
                length[i] = v.Length();
                if (length[i] < StackPx)
                {
                    kind[i] = Move.Fixed;
                    unit[i] = v;
                    continue;
                }
                kind[i] = Move.Ray;
                unit[i] = v / (float)length[i];
                top[i] = !stream[i] ? Grow(length[i], k)
                    : options.SpaceStreams ? Grow(length[i], Math.Min(k, StreamFactorCap))
                    : length[i];
            }

            // A move that is not to grow keeps its exact direction, unless it is a jump whose turn is accented.
            var accented = new bool[n + 1];
            for (int i = 2; i < n; i++)
            {
                if (kind[i] != Move.Ray || kind[i - 1] != Move.Ray || pause[i]) continue;
                bool streams = stream[i] && stream[i - 1];
                turnTie[i] = streams ? StreamTurnTie : JumpTurnTie;
                if (!pause[i - 1])
                    growthTie[i] = streams ? StreamGrowthTie : !stream[i] && !stream[i - 1] ? JumpGrowthTie : 0;
                if (options.AccentAngles && IsJump(i) && IsJump(i - 1))
                {
                    // The turn s_i − s_{i−1} (deviations across the moves, as shares of their original lengths, about
                    // the angles times the growth) is pulled toward the accent.
                    double turn = Angle(unit[i - 1], unit[i]);
                    double wanted = Math.Sign(turn) * AccentRadians * Math.Abs(Math.Sin(turn));
                    double growth = 0.5 * (top[i] / length[i] + top[i - 1] / length[i - 1]);
                    turnTarget[i] = Math.Sin(wanted) * growth;
                    turnTie[i] = AccentTurnTie;
                    accented[i] = accented[i - 1] = true;
                }
            }
            for (int i = 1; i < n; i++)
                if (kind[i] == Move.Ray && (top[i] > length[i] || accented[i]))
                    cone[i] = Math.Tan((stream[i] ? StreamConeDegrees : ConeDegrees) * Math.PI / 180);

            bool IsJump(int j) => kind[j] == Move.Ray && !stream[j] && length[j] >= AccentMovePx;
            original = (Vector2[])unit.Clone();
            foreach (var t in turns) Turn(t);
        }

        /// <summary>The stream of <paramref name="t"/> is turned: each move from its original direction toward the one
        /// it had curved to (by the share of the factor), then by the turn; it may curve a little from there. The turns
        /// into and out of it are no longer kept, and the move out of it may re-aim (as little as it can).</summary>
        private void Turn(BurstTurn t)
        {
            for (int j = t.Start; j <= t.End; j++)
            {
                var d = Vector2.Lerp(unit[j], t.Directions[j - t.Start], (float)t.Share);
                unit[j] = Rotate(d.Length() > 1e-3f ? Vector2.Normalize(d) : unit[j], (float)t.Angle);
                cone[j] = Math.Tan(TurnedConeDegrees * Math.PI / 180);
            }
            turnTie[t.Start] = 0;
            turnTarget[t.Start] = 0;
            system = null;
            constant = null;
            int o = t.End + 1;
            if (o >= n) return;
            turnTie[o] = 0;
            turnTarget[o] = 0;
            growthTie[o] = 0;
            if (o + 1 < n) (turnTie[o + 1], turnTarget[o + 1], growthTie[o + 1]) = (0, 0, 0);
            if (kind[o] == Move.Ray) kind[o] = Move.Soft;
        }

        public bool IsRay(int i) => kind[i] is Move.Ray or Move.Soft;

        public double Wanted(int i) => top[i];

        /// <summary>The move into object <paramref name="i"/> is wanted longer than it is.</summary>
        public bool Grows(int i) => kind[i] == Move.Ray && top[i] > length[i];

        /// <summary>A move in the frame of the original direction of move <paramref name="i"/>.</summary>
        public (double Along, double Across) Frame(int i, Vector2 move)
            => (Vector2.Dot(move, original[i]), original[i].X * move.Y - original[i].Y * move.X);

        /// <summary>The accents of the turns at the objects marked in <paramref name="dropped"/> are given up (those
        /// turns are kept as in the map); the next <see cref="Solve"/> starts from the last solution.</summary>
        public void DropAccents(bool[] dropped)
        {
            for (int i = 2; i < n; i++)
                if (dropped[i - 1]) turnTarget[i] = 0;
            constant = null;
        }

        public Vector2[] Solve() => SolveOnce();

        /// <summary>The next <see cref="Solve"/> starts from the solution of <paramref name="other"/> (the same map,
        /// with other streams turned): far fewer iterations.</summary>
        public void StartFrom(Problem other)
        {
            if (other.q == null) return;
            (q, zq, yq, zd, yd) = ((double[])other.q.Clone(), (double[])other.zq!.Clone(), (double[])other.yq!.Clone(),
                (double[])other.zd!.Clone(), (double[])other.yd!.Clone());
        }

        /// <summary>The streams of the map that may be turned: moves Start..End (consecutive stream steps that grow).</summary>
        public List<(int Start, int End)> Runs()
        {
            var runs = new List<(int, int)>();
            for (int i = 1; i < n; i++)
            {
                if (!Step(i)) continue;
                int s0 = i;
                while (Step(i + 1)) i++;
                if (i - s0 + 1 >= MinBurstMoves) runs.Add((s0, i));
            }
            return runs;
        }

        /// <summary>The turns to try this round: for each stretch, the first stream not yet tried that does not reach its
        /// spacing and that a turn helps.</summary>
        public List<BurstTurn> Propose(Vector2[] heads, List<(int Start, int End)> runs, HashSet<int> taken)
        {
            var proposed = new List<BurstTurn>();
            int skipUntil = 0;
            foreach (var (s0, e) in runs)
            {
                if (taken.Contains(s0) || s0 <= skipUntil) continue;
                double theta = FittingTurn(heads, s0, e, out _);
                if (theta == 0) continue;
                var directions = Enumerable.Range(s0, e - s0 + 1).Select(j => Solved(heads, j)).ToArray();
                proposed.Add(new BurstTurn(s0, e, theta, directions));
                skipUntil = e + BurstSpacingObjects;
            }
            return proposed;
        }

        /// <summary>How much the moves Start..End are wanted to grow.</summary>
        public double WantedGrowth(int s0, int e)
        {
            double before = 0, wanted = 0;
            for (int j = s0; j <= e; j++) (before, wanted) = (before + length[j], wanted + top[j]);
            return wanted / before;
        }

        /// <summary>How much the moves Start..End grew: their new length over their length in the map.</summary>
        public double Growth(Vector2[] heads, int s0, int e)
        {
            double before = 0, after = 0;
            for (int j = s0; j <= e; j++)
            {
                before += length[j];
                after += (heads[j] - heads[j - 1] - body[j]).Length();
            }
            return after / before;
        }

        private bool Step(int j) => j >= 1 && j < n && kind[j] == Move.Ray && isStream[j] && top[j] > length[j]
                                    && lo[j] != hi[j];

        /// <summary>The heads s−1..e of a burst turned by θ, its moves grown by the share f of their growth.</summary>
        private Vector2[] Shape(Vector2[] heads, int s0, int e, double theta, double f)
        {
            var points = new Vector2[e - s0 + 2];
            points[0] = heads[s0 - 1];
            for (int j = s0; j <= e; j++)
            {
                double z = length[j] + f * (top[j] - length[j]);
                points[j - s0 + 1] = points[j - s0] + body[j] + Rotate(Solved(heads, j), (float)theta) * (float)z;
            }
            return points;
        }

        /// <summary>The direction of move j as solved (the stream may have curved away from an edge).</summary>
        private Vector2 Solved(Vector2[] heads, int j)
        {
            var v = heads[j] - heads[j - 1] - body[j];
            float d = v.Length();
            return d > 1 ? v / d : unit[j];
        }

        /// <summary>The share of its growth a burst turned by θ reaches inside the playfield (heads in their boxes).</summary>
        private double Reach(Vector2[] heads, int s0, int e, double theta)
        {
            bool Fits(double f)
            {
                var points = Shape(heads, s0, e, theta, f);
                for (int j = s0; j <= e; j++)
                {
                    var p = points[j - s0 + 1];
                    if (p.X < lo[j].X || p.Y < lo[j].Y || p.X > hi[j].X || p.Y > hi[j].Y) return false;
                }
                return true;
            }
            if (Fits(1)) return 1;
            double low = 0, high = 1;
            if (!Fits(0)) return 0;
            for (int it = 0; it < 20; it++)
            {
                double mid = 0.5 * (low + high);
                if (Fits(mid)) low = mid; else high = mid;
            }
            return low;
        }

        /// <summary>The smallest turn at which the burst reaches <see cref="BurstReach"/> of its growth (0 when it does
        /// so as it is, or when no turn helps by much); the reach at that turn.</summary>
        private double FittingTurn(Vector2[] heads, int s0, int e, out double reach)
        {
            reach = Reach(heads, s0, e, 0);
            if (reach >= BurstReach) return 0;
            double best = 0, bestReach = reach;
            for (int deg = 1; deg <= BurstMaxTurnDegrees; deg++)
            {
                double found = 0, foundReach = 0, foundDistance = double.PositiveInfinity;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    double t = sign * deg * Math.PI / 180;
                    double r = Reach(heads, s0, e, t);
                    if (r < BurstReach) { if (r > bestReach + 1e-9) (best, bestReach) = (t, r); continue; }
                    double distance = Vector2.Distance(Shape(heads, s0, e, t, r)[^1], new Vector2(256, 192));
                    if (distance < foundDistance) (found, foundReach, foundDistance) = (t, r, distance);
                }
                if (foundDistance < double.PositiveInfinity) { reach = foundReach; return found; }
            }
            // No turn fits it: the best one, if it gains much.
            if (bestReach < reach + BurstGain) return 0;
            reach = bestReach;
            return best;
        }

        private Vector2[] SolveOnce()
        {
            int m = 2 * n;
            bool warm = q != null;
            if (!warm)
            {
                q = new double[m];
                for (int i = 0; i < n; i++) (q[2 * i], q[2 * i + 1]) = (objects[i].Start.X, objects[i].Start.Y);
                zq = (double[])q.Clone();
                yq = new double[m];
                zd = new double[m];
                yd = new double[m];
                for (int i = 1; i < n; i++)
                {
                    var v = objects[i].Start - objects[i - 1].End;
                    (zd[2 * i], zd[2 * i + 1]) = (v.X, v.Y);
                }
            }
            system ??= Matrix();
            constant ??= Constant();
            return Iterate(q!, zq!, yq!, zd!, yd!, system, constant, warm ? MinIterations / 3 : MinIterations);
        }

        private Vector2[] Iterate(double[] q, double[] zq, double[] yq, double[] zd, double[] yd, Banded system,
            double[] constant, int least)
        {
            int m = 2 * n;
            var rhs = new double[m];
            var previous = new double[m];
            for (int it = 0; it < MaxIterations; it++)
            {
                Array.Copy(zq, previous, m);
                for (int j = 0; j < m; j++) rhs[j] = Rho * (zq[j] - yq[j]) + constant[j];
                for (int i = 1; i < n; i++)
                {
                    if (kind[i] == Move.Free) continue;
                    double tx = Rho * (zd[2 * i] - yd[2 * i] + body[i].X), ty = Rho * (zd[2 * i + 1] - yd[2 * i + 1] + body[i].Y);
                    rhs[2 * i] += tx;
                    rhs[2 * i + 1] += ty;
                    rhs[2 * i - 2] -= tx;
                    rhs[2 * i - 1] -= ty;
                }
                system.Solve(rhs, q);

                // Over-relaxed (Boyd et al., §3.4.3): the projections see Relax·new + (1 − Relax)·last.
                double residual = 0;
                for (int j = 0; j < m; j++)
                {
                    residual = Math.Max(residual, Math.Abs(q[j] - zq[j]));
                    double h = Relax * q[j] + (1 - Relax) * zq[j];
                    var (low, high) = j % 2 == 0 ? (lo[j / 2].X, hi[j / 2].X) : (lo[j / 2].Y, hi[j / 2].Y);
                    zq[j] = Math.Clamp(h + yq[j], low, high);
                    yq[j] += h - zq[j];
                    residual = Math.Max(residual, Math.Abs(zq[j] - previous[j]));
                }

                for (int i = 1; i < n; i++)
                {
                    if (kind[i] == Move.Free) continue;
                    double ex = q[2 * i] - q[2 * i - 2] - body[i].X, ey = q[2 * i + 1] - q[2 * i - 1] - body[i].Y;
                    residual = Math.Max(residual, Math.Max(Math.Abs(ex - zd[2 * i]), Math.Abs(ey - zd[2 * i + 1])));
                    double dx = Relax * ex + (1 - Relax) * zd[2 * i], dy = Relax * ey + (1 - Relax) * zd[2 * i + 1];
                    double vx = dx + yd[2 * i], vy = dy + yd[2 * i + 1];
                    if (kind[i] == Move.Fixed)
                        (zd[2 * i], zd[2 * i + 1]) = (unit[i].X, unit[i].Y);
                    else if (kind[i] == Move.Soft)
                    {
                        // w·(a − T)² + w_b·b² (per px², over L²) + ρ/2·|z − v|², SoftLeast·L ≤ a ≤ T, |b| ≤ T: separable.
                        var u = unit[i];
                        double along = vx * u.X + vy * u.Y, across = u.X * vy - u.Y * vx;
                        double L = length[i], T = top[i], w = 2 * GrowWeight / (L * L), wb = 2 * SoftAimWeight / (L * L);
                        double a = Math.Clamp((w * T + Rho * along) / (w + Rho), SoftLeast * L, T);
                        double b = Math.Clamp(Rho * across / (wb + Rho), -T, T);
                        (zd[2 * i], zd[2 * i + 1]) = (a * u.X - b * u.Y, a * u.Y + b * u.X);
                    }
                    else
                    {
                        var u = unit[i];
                        double along = vx * u.X + vy * u.Y, across = u.X * vy - u.Y * vx;
                        double L = length[i], alpha = 2 * GrowWeight / (L * L) + Rho;
                        double a0 = (2 * GrowWeight * top[i] / (L * L) + Rho * along) / alpha;
                        double b = Trapezoid(a0, across, L, top[i], cone[i], alpha, Rho, out double a);
                        (zd[2 * i], zd[2 * i + 1]) = (a * u.X - b * u.Y, a * u.Y + b * u.X);
                    }
                    yd[2 * i] += dx - zd[2 * i];
                    yd[2 * i + 1] += dy - zd[2 * i + 1];
                }
                if (it >= least && residual < TolerancePx) break;
            }

            var heads = new Vector2[n];
            for (int i = 0; i < n; i++)
                heads[i] = lo[i] == hi[i] ? objects[i].Start : new Vector2((float)zq[2 * i], (float)zq[2 * i + 1]);
            // The moves that must be exact (a stack, a move that may neither grow nor turn) are made so: the iterations
            // only reach them within TolerancePx.
            for (int i = 1; i < n; i++)
            {
                if (lo[i] == hi[i]) continue;
                // (Kept in the box, should the exact move leave it by a hair: the head would be invalid.)
                if (kind[i] == Move.Fixed)
                    heads[i] = Vector2.Clamp(heads[i - 1] + body[i] + unit[i], lo[i], hi[i]);
                else if (kind[i] == Move.Ray && cone[i] == 0 && top[i] == length[i])
                    heads[i] = Vector2.Clamp(heads[i - 1] + body[i] + unit[i] * (float)length[i], lo[i], hi[i]);
            }
            return heads;
        }

        /// <summary>The matrix of the position update, ρ·I + ρ·DᵀD + the Hessian of the ties and of the anchor; banded,
        /// 5 below the diagonal (x and y of 3 consecutive heads), factored.</summary>
        private Banded Matrix()
        {
            var a = new Banded(2 * n, 5);
            for (int i = 0; i < n; i++)
            {
                a.Add(2 * i, 2 * i, Rho + 2 * anchor[i]);
                a.Add(2 * i + 1, 2 * i + 1, Rho + 2 * anchor[i]);
            }
            for (int i = 1; i < n; i++)
            {
                if (kind[i] == Move.Free) continue;
                for (int c = 0; c < 2; c++)
                {
                    int x = 2 * i + c, y = 2 * (i - 1) + c;
                    a.Add(x, x, Rho);
                    a.Add(y, y, Rho);
                    a.Add(x, y, -Rho);
                }
            }
            Ties((index, coef, weight, _) =>
            {
                for (int s = 0; s < 6; s++)
                for (int t = 0; t <= s; t++)
                    a.Add(index[s], index[t], 2 * weight * coef[s] * coef[t]);
            });
            a.Factor();
            return a;
        }

        /// <summary>The constant part of the gradient of the ties and of the anchor.</summary>
        private double[] Constant()
        {
            var constant = new double[2 * n];
            for (int i = 0; i < n; i++)
            {
                constant[2 * i] += 2 * anchor[i] * objects[i].Start.X;
                constant[2 * i + 1] += 2 * anchor[i] * objects[i].Start.Y;
            }
            Ties((index, coef, weight, c0) =>
            {
                for (int s = 0; s < 6; s++) constant[index[s]] -= 2 * weight * c0 * coef[s];
            });
            return constant;
        }

        /// <summary>
        /// Every tie λ·(w_i·(q_i − q_{i−1} − body_i) − w_{i−1}·(q_{i−1} − q_{i−2} − body_{i−1}) − target)², with w = u / L
        /// for the growth and u⊥ / L for the turn: the indices and coefficients of its linear part over the x and y of
        /// heads i−2, i−1 and i, its weight λ and its constant.
        /// </summary>
        private void Ties(Action<int[], double[], double, double> tie)
        {
            var coef = new double[6];
            var index = new int[6];
            for (int i = 2; i < n; i++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    double weight = pass == 0 ? growthTie[i] : turnTie[i];
                    if (weight == 0) continue;
                    var (ui, up) = (unit[i] / (float)length[i], unit[i - 1] / (float)length[i - 1]);
                    if (pass == 1) (ui, up) = (new Vector2(-ui.Y, ui.X), new Vector2(-up.Y, up.X));
                    index[0] = 2 * (i - 2);
                    index[1] = index[0] + 1;
                    index[2] = 2 * (i - 1);
                    index[3] = index[2] + 1;
                    index[4] = 2 * i;
                    index[5] = index[4] + 1;
                    (coef[0], coef[1]) = (up.X, up.Y);
                    (coef[2], coef[3]) = (-ui.X - up.X, -ui.Y - up.Y);
                    (coef[4], coef[5]) = (ui.X, ui.Y);
                    double c0 = -Vector2.Dot(ui, body[i]) + Vector2.Dot(up, body[i - 1]) - (pass == 1 ? turnTarget[i] : 0);
                    tie(index, coef, weight, c0);
                }
            }
        }
    }

    /// <summary>
    /// The point (a, b) of the trapezoid L ≤ a ≤ T, |b| ≤ t·a closest to (<paramref name="a0"/>, <paramref name="b0"/>)
    /// in the metric α·da² + β·db²; returns b.
    /// </summary>
    private static double Trapezoid(double a0, double b0, double L, double T, double t, double alpha, double beta,
        out double a)
    {
        if (a0 >= L && a0 <= T && Math.Abs(b0) <= t * a0)
        {
            a = a0;
            return b0;
        }
        double best = double.PositiveInfinity, bestA = L, bestB = 0;
        void Try(double x, double y)
        {
            double c = alpha * (x - a0) * (x - a0) + beta * (y - b0) * (y - b0);
            if (c < best) (best, bestA, bestB) = (c, x, y);
        }
        Try(L, Math.Clamp(b0, -t * L, t * L));
        Try(T, Math.Clamp(b0, -t * T, t * T));
        for (int sign = -1; sign <= 1; sign += 2)
        {
            double x = Math.Clamp((alpha * a0 + beta * sign * t * b0) / (alpha + beta * t * t), L, T);
            Try(x, sign * t * x);
        }
        a = bestA;
        return bestB;
    }

    /// <summary>A symmetric positive definite band matrix (lower band stored), factored in place by Cholesky.</summary>
    private sealed class Banded(int size, int width)
    {
        private readonly double[] cells = new double[size * (width + 1)];

        private ref double At(int i, int j) => ref cells[i * (width + 1) + (i - j)];

        public void Add(int i, int j, double value)
        {
            if (i < j) (i, j) = (j, i);
            At(i, j) += value;
        }

        public void Factor()
        {
            for (int i = 0; i < size; i++)
            for (int j = Math.Max(0, i - width); j <= i; j++)
            {
                double s = At(i, j);
                for (int k = Math.Max(0, i - width); k < j; k++) s -= At(i, k) * At(j, k);
                At(i, j) = i == j ? Math.Sqrt(Math.Max(s, 1e-12)) : s / At(j, j);
            }
        }

        public void Solve(double[] b, double[] x)
        {
            for (int i = 0; i < size; i++)
            {
                double s = b[i];
                for (int k = Math.Max(0, i - width); k < i; k++) s -= At(i, k) * x[k];
                x[i] = s / At(i, i);
            }
            for (int i = size - 1; i >= 0; i--)
            {
                double s = x[i];
                for (int k = i + 1; k <= Math.Min(size - 1, i + width); k++) s -= At(k, i) * x[k];
                x[i] = s / At(i, i);
            }
        }
    }

    /// <summary>
    /// Valid when the head lies in the head area or on its original point, and every other path point lies in the
    /// body area or in the object's original bounding box (an object that already went past the edge may keep doing
    /// so, no further).
    /// </summary>
    private static bool Valid(MapObject o, Placement p)
    {
        bool inHead = p.Start.X >= HeadMin.X && p.Start.X <= HeadMax.X && p.Start.Y >= HeadMin.Y && p.Start.Y <= HeadMax.Y;
        if (!inHead && p.Start != o.Start) return false;
        if (o.PathSamples.Count == 0) return true;

        Vector2 min = o.Start, max = o.Start;
        foreach (var s in o.PathSamples)
        {
            min = Vector2.Min(min, o.Start + s);
            max = Vector2.Max(max, o.Start + s);
        }
        foreach (var s in o.PathSamples)
        {
            if (s == Vector2.Zero) continue;
            var q = p.Start + s;
            bool inBody = q.X >= BodyMin.X && q.X <= BodyMax.X && q.Y >= BodyMin.Y && q.Y <= BodyMax.Y;
            bool inOriginal = q.X >= min.X && q.X <= max.X && q.Y >= min.Y && q.Y <= max.Y;
            if (!inBody && !inOriginal) return false;
        }
        return true;
    }

    /// <summary>Mean move length (≥ 1 px) between consecutive objects (no spinner), before and after.</summary>
    private static (double Before, double After) AverageJumps(IReadOnlyList<MapObject> objects, Placement[] placements)
    {
        double before = 0, after = 0;
        int jumps = 0;
        for (int i = 1; i < objects.Count; i++)
        {
            var (o, p) = (objects[i], objects[i - 1]);
            if (o.Kind == ObjectKind.Spinner || p.Kind == ObjectKind.Spinner) continue;
            float jump = Vector2.Distance(o.Start, p.End);
            if (jump < 1) continue;
            var q = placements[i - 1];
            var newEnd = q.Start + Rotate(p.End - p.Start, q.Rotation);
            before += jump;
            after += Vector2.Distance(placements[i].Start, newEnd);
            jumps++;
        }
        return jumps == 0 ? (0, 0) : (before / jumps, after / jumps);
    }

    private static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(c * v.X - s * v.Y, s * v.X + c * v.Y);
    }

    /// <summary>Signed angle from <paramref name="a"/> to <paramref name="b"/>, in (−π, π].</summary>
    private static double Angle(Vector2 a, Vector2 b)
        => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
}
