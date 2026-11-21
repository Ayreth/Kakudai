using System.Numerics;
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
/// only made longer, from its own length up to g(length). Nothing is ever turned as a block, nothing is moved closer
/// than it was: a vertical jump stays vertical, a pattern keeps its orientation, a stream keeps its curve, and no
/// jump gets shorter than in the map. The positions are the solution of one convex problem:
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
        var problem = new Problem(objects, beats, options);
        var heads = problem.Solve();
        if (options.AccentAngles) (problem, heads) = WithoutNewOverlaps(objects, beats, options, problem, heads);

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
        AmplifyOptions options, Problem problem, Vector2[] heads)
    {
        // Without the option: at k = 1, the map as it is.
        var plain = new Problem(objects, beats, options with { AccentAngles = false });
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

    private static AmplifyResult Result(IReadOnlyList<MapObject> objects, Placement[] placements, int corrected,
        int reduced)
    {
        var (before, after) = AverageJumps(objects, placements);
        return new AmplifyResult(placements, corrected, reduced, before, after);
    }

    /// <summary>The convex problem of one map (see <see cref="Amplifier"/>), and its ADMM solver.</summary>
    private sealed class Problem
    {
        private enum Move { Free, Fixed, Ray }

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

        public Problem(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options)
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
            var stream = new bool[n];
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
        }

        public bool IsRay(int i) => kind[i] == Move.Ray;

        public double Wanted(int i) => top[i];

        /// <summary>The move into object <paramref name="i"/> is wanted longer than it is.</summary>
        public bool Grows(int i) => kind[i] == Move.Ray && top[i] > length[i];

        /// <summary>A move in the frame of the original direction of move <paramref name="i"/>.</summary>
        public (double Along, double Across) Frame(int i, Vector2 move)
            => (Vector2.Dot(move, unit[i]), unit[i].X * move.Y - unit[i].Y * move.X);

        /// <summary>The accents of the turns at the objects marked in <paramref name="dropped"/> are given up (those
        /// turns are kept as in the map); the next <see cref="Solve"/> starts from the last solution.</summary>
        public void DropAccents(bool[] dropped)
        {
            for (int i = 2; i < n; i++)
                if (dropped[i - 1]) turnTarget[i] = 0;
            constant = null;
        }

        public Vector2[] Solve()
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
