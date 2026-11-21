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
/// R(θ) = [[cos θ, −sin θ], [sin θ, cos θ]] in osu! coordinates) to apply to its shape: the slider control points
/// are turned by it around the head (OsuPatcher). Every object of a pattern gets the rotation of its pattern (with
/// accented angles, plus how much the pattern was bent before it). An object that does not move has exactly its
/// original head and a rotation of 0, so its line is copied as is.
/// </summary>
public readonly record struct Placement(Vector2 Start, float Rotation);

/// <param name="Placements">One placement per input object, in the same order.</param>
/// <param name="Corrected">Objects of the patterns that had to be turned away from the orientation of the previous
/// pattern, or moved from where their entry jump wanted them, to fit in the playfield (plus the objects left on
/// their original spot).</param>
/// <param name="Reduced">Objects of the patterns zoomed less than wanted, or whose entry jump is shorter than
/// 0.9 times the wanted length.</param>
public sealed record AmplifyResult(IReadOnlyList<Placement> Placements, int Corrected, int Reduced,
    double AverageJumpBefore, double AverageJumpAfter);

/// <summary>
/// Spreads the jumps of a map with rigid patterns (spec §8.2-ter): the map is cut into blocks (patterns), each
/// block is zoomed inside by g(median move) / median move, then laid down as one piece, turned and moved but never
/// bent. Blocks are chained like jumps: the jump into a block keeps its amplified length and its turning angles
/// where it can. A beam search picks, for every block, the best trade-off between turning it, shortening its entry
/// jump and zooming it less, so that everything stays inside the playfield.
/// <para>
/// Streams are never cut (Kakudai 2 spec §5.1, §13): a spaced stream is cut into blocks of a beat, and the blocks of
/// one stream form a run, whose zoom changes only smoothly (within 1.15 from a block to the next, within 1.2 over any
/// 8 beats). A block entered by a gap of its stream (a stream gap no longer than 1.3 × the median of its neighbours)
/// is chained to the previous one: the gap is the original one zoomed like the stream, and the block keeps the
/// orientation of the previous one, give or take a bend of at most <see cref="MaxBendSteps"/> (50°), so that each
/// turning angle at the junction changes by 25° at most: a long stream curves back inside the playfield beat by beat.
/// When the run fits nowhere so, it is zoomed less; then it is laid down unzoomed as one rigid piece; last resort, it
/// is left on its original spot.
/// </para>
/// <para>
/// With accented angles (Kakudai 2 spec §5.2), even at k = 1, the turns between two jumps are aimed sharper, toward
/// 180°: inside a block by bending it at each such turn (the rest of the block turned), around the entry jump of a block
/// through the turning angles its flow cost aims at; streams are not touched, and an accent is cut down where it would
/// land an object on a recent one. A final check drops the accents where the result still lands an object on a recent
/// one that it does not land on without the option. With only the kiai (spec §5.3), each kiai section is amplified on
/// its own, from a restart, and every other object keeps its exact place.
/// </para>
/// </summary>
public static class Amplifier
{
    /// <summary>C of g(d) = max(d, C·tanh(k·d / C)), in osu! pixels: short moves are multiplied by about k, long
    /// ones are capped below C.</summary>
    internal const double CapPx = 560;

    /// <summary>Objects this close to the end of the previous one are stacked on it and stay so.</summary>
    private const float StackPx = 3;

    /// <summary>A head that comes at most this many beats after the previous one is a step of a stream
    /// (<see cref="IsStreamStep"/>); the moves between such objects are the stream gaps.</summary>
    private const double StreamGapInBeats = 1.15 / 4;

    /// <summary>A gap of at least this many beats starts a new block (and lowers the weight of the turn into it).</summary>
    private const double PauseBeats = 1;

    /// <summary>A gap of at least this many beats restarts the chain: no jump to keep.</summary>
    private const double RestartBeats = 4;

    /// <summary>A gap outside [r / ratio, ratio·r] of the first gap r of the block starts a new block.</summary>
    private const double RhythmRatio = 1.6;

    private const double MaxBlockBeats = 2;

    /// <summary>A block of a spaced stream lasts a beat at most (Kakudai 2 spec §13): a long stream is a chain of short
    /// rigid pieces that can bend at every beat, so that it curves back inside the playfield instead of being zoomed
    /// less. Lab A3: with blocks of 4 beats, a block of a dense stream zoomed ×2 is about as big as the playfield.</summary>
    private const double MaxStreamBlockBeats = 1;

    /// <summary>A stream that keeps its spacing (spaced streams off) is not bent: its blocks may be longer.</summary>
    private const double MaxKeptStreamBlockBeats = 4;

    private const int MaxBlockObjects = 16;

    /// <summary>Tolerance on the time thresholds, in ms.</summary>
    private const double TimeSlackMs = 1;

    private const int BeamWidth = 4;

    /// <summary>Rotations tried: every 5°.</summary>
    private const int AngleSteps = 72;

    /// <summary>Around the orientation of the previous block, every angle within this; beyond, every
    /// <see cref="FarAngleStep"/>-th.</summary>
    private const double NearDegrees = 45;

    private const int FarAngleStep = 3;

    /// <summary>Largest rotation between two chained blocks of a stream, in angle steps (10 × 5° = 50°); the gap
    /// between them turns by half of it, so each of the two turning angles around the gap changes by 25° at most
    /// (Kakudai 2 spec §13). Lab A3 at k = 2 with blocks of a beat: long streams ×1.49 at 40°, ×1.62 at 50°.</summary>
    private const int MaxBendSteps = 10;

    /// <summary>A gap of a stream no longer than this times its local median (<see cref="StreamMedians"/>) chains its
    /// blocks; a longer one (a jump inside the stream) leaves the next block free to move, still in the run: its zoom
    /// stays within the band of the run (or unzoomed and rigid, or on its original spot, when the run is).</summary>
    private const double ChainRatio = 1.3;

    /// <summary>How many chained blocks ahead a state of a run must be able to reach, bending, to be kept; and how
    /// many, unbent, the extra candidates of a block that starts a chain make room for. Lab A3 at k = 2 (200 maps,
    /// long streams / worst map): 1 block ×1.40 / 265 ms, 2 blocks ×1.58 / 321 ms, 3 blocks ×1.62 / 358 ms, 4 blocks
    /// ×1.64 / 1244 ms.</summary>
    private const int LookAheadBlocks = 3;

    /// <summary>Share f of the wanted extra zoom tried: s = 1 + f·(s* − 1).</summary>
    private static readonly double[] Fractions = [1, 0.8, 0.6, 0.4, 0.2, 0];

    /// <summary>The fractions tried for the blocks of a run: finer, so that its zoom can change smoothly along it.
    /// The even ones are <see cref="Fractions"/>.</summary>
    private static readonly double[] RunFractions = [1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0];

    /// <summary>Two consecutive blocks of a run are zoomed within this ratio of each other (and one step of
    /// <see cref="RunFractions"/> apart at most).</summary>
    private const double NeighbourRatio = 1.15;

    /// <summary>The largest zoom of the band of a block (<see cref="BandBeats"/>) is less than this times its
    /// smallest. A gap may be 1.3 times the median of its neighbours without being a cut, so only a ratio up to
    /// 1.5 / 1.3 ≈ 1.15 rules cuts out by construction; lab A3 at k = 2: 1.15 gives long streams of ×1.55 (Sky Arrow's
    /// down to ×1.11), 1.2 gives ×1.58 with no more cuts.</summary>
    private const double BandRatio = 1.2;

    /// <summary>The band of a run holds over its blocks that start less than this many beats before each block: a
    /// sliding window, not the whole run (Kakudai 2 spec §13), so that a long stream is not held all along at its
    /// tightest passage. Stream cuts are measured against the 8 gaps on each side, 2 beats at 1/4.</summary>
    private const double BandBeats = 8;

    /// <summary>The most blocks the band looks back over (they are kept 4 bits each in a 64-bit word).</summary>
    private const int BandBlocks = 15;

    // Costs (spec §8.2-ter step 6).
    private const double ShortJumpWeight = 4;
    private const double LongJumpWeight = 1;
    private const double ShrunkJumpWeight = 8;
    private const double TurnWeight = 8;
    private const double PauseTurnFactor = 0.3;
    private const double ScaleWeight = 2;
    private const double OrientationWeight = 0.3;

    // Slider flow (SliderTurnFactor) and relative rotation (RelativeWeight), on 300 maps of the library, k = 2 / k = 3,
    // before → after: turn out of a slider 14.9° → 10.3° / 18.3° → 12.9° (90th percentile 50° → 35° / 63° → 44°), into
    // one 7.3° → 4.5° / 9.4° → 5.6°, angle between two sliders less than 2 beats apart 16.1° → 12.2° / 19.2° → 14.6°,
    // all turns 10.7° → 10.1° / 13.2° → 12.4°, overlaps 13.1 → 12.3 ‰ / 13.6 → 13.5 ‰, stream cuts 4 → 1 / 11 → 14;
    // jumps ×1.514 → ×1.496 / ×1.691 → ×1.654. With relative weight 8 the sliders stay a bit more parallel, with a
    // factor of 3 the jumps out of them bend less, both for less spacing. Neither applies to the blocks of a run, which
    // have their own bends (MaxBendSteps): applied there too, they cut 17 streams at k = 3. With back-to-back sliders
    // out of the streams too (IsStreamStep), streams measured the same way: turn out of a slider 10.3° / 13.3°, between
    // two sliders 11.7° / 14.7°, overlaps 12.7 ‰ / 14.7 ‰, stream cuts 7 → 0 / 23 → 5, jumps ×1.509 / ×1.692.

    /// <summary>The turns around a jump measured on the body of a slider (<see cref="SliderFlow"/>) weigh this many times
    /// more than the others, outside streams: a mapper aims the jumps out of a slider along its body, and a player
    /// follows it.</summary>
    private const double SliderTurnFactor = 2;

    /// <summary>Weight of the rotation of a block against the previous one (<see cref="RelativeCost"/>).</summary>
    private const double RelativeWeight = 4;
    private const double RestartMoveWeight = 0.5;
    private const double PinnedCost = 50;

    /// <summary>Cost BendWeight·(β/π)² of a bend β between two chained blocks: the two turning angles around their gap
    /// each change by β/2, weighed like a turn of the flow cost (TurnWeight·2·(β/2π)²). Lab A3 at k = 2: 16 bends
    /// streams less (in-stream turn error 6.2° against 7.1°) but zooms them less too (Sentou ×1.49).</summary>
    private const double BendWeight = 4;

    /// <summary>The weight of a jump inside a stream shrunk against the block before it (<see cref="RunJumpCost"/>).
    /// Lab A3 at k = 2 on 200 maps: local stream cuts 4 without it, 3 at 20, 1 at 50 (or at 100, which zooms Sentou
    /// less).</summary>
    private const double RunJumpWeight = 50;

    // Accented angles (Kakudai 2 spec §5.2), lab A2: at k = 2 on 200 maps, the mean turn between two jumps goes from
    // 120° (114° in the original) to 134°, with fewer overlaps than without the option (5.6 ‰ against 8.1 ‰).

    /// <summary>A turn τ between two jumps is aimed at τ + A·|sin τ| toward 180°, A = 30° (in radians): 90° becomes 120°,
    /// while a straight line and a back and forth stay so. Lab A2: 25° gives 131° at k = 2, 35° gives 136°, with
    /// about as many overlaps.</summary>
    private const double AccentRadians = 30 * Math.PI / 180;

    /// <summary>Moves at least this long that are neither stream gaps nor restarts are jumps, whose turns are accented.
    /// </summary>
    private const float AccentMovePx = 30;

    /// <summary>An accent never brings an object closer than this to an object of the last 2 beats (or than it is
    /// without the accents, when that is closer), in the map zoomed move by move: the distance at which the overlaps
    /// are counted. Lab A2 at k = 1 / 2: 0 px gives 10.4 / 8.3 ‰ overlaps, 30 px 3.3 / 5.6 ‰ (turns 132° / 134°), 60 px
    /// 3.2 / 5.5 ‰ (but a zigzag of 57 px moves only gets 105°), 90 px 2.2 / 5.0 ‰ (turns 128° / 133°).</summary>
    private const float AccentGuardPx = 30;

    /// <summary>The first two objects of a block land on an object of the last 2 beats when they come closer to it than
    /// this, while they were at least <see cref="FarPx"/> from it; each one costs <see cref="CrowdWeight"/>. Lab A2 at
    /// k = 2 (with the guard on the map unzoomed): overlaps 10.4 ‰ with no such cost, 5.1 ‰ with it, the same with a
    /// weight of 4. Most overlaps the accents made were an object landing on the one two before it.</summary>
    private const float CrowdPx = 30;

    private const float FarPx = 60;
    private const double CrowdWeight = 1;

    /// <summary>For the final check of the accents, an object closer than this to a recent object it was far from
    /// overlaps it: the distance at which the overlaps are counted (30 px), plus 2 px for the rounding at writing.
    /// </summary>
    private const float AccentCheckPx = 32;

    /// <summary>An overlap of the result without the option excuses the same object in the accented result only when
    /// it is closer than this: the distance of the metric (30 px) minus 2 px for the rounding at writing. With
    /// <see cref="AccentCheckPx"/> on both sides, an object 31 px from a recent one without the option (no overlap)
    /// would excuse it landing at 20 px with the accents (one more).</summary>
    private const float ExcusedPx = 28;

    /// <summary>Per round of the final check of the accents, how many objects before the object an overlap is on the
    /// accents are dropped for, besides those between the two (the accents before them move where the chain lands);
    /// after the last round, the part is placed as without the option.</summary>
    private static readonly int[] AccentWiden = [0, 2, 4, 8, 16, 32];

    /// <summary>Jumps shorter than this weigh less in the length cost (imp = min(1, |J| / this)).</summary>
    private const double JumpImportancePx = 30;

    /// <summary>Moves shorter than this weigh less in the turn cost (w = min(1, |m| / this)).</summary>
    private const double TurnImportancePx = 60;

    /// <summary>States whose exits fall in the same cell of this size (and share an orientation) are duplicates.</summary>
    private const float DedupPx = 6;

    /// <summary>Kept inside the areas when fitting, so that float rounding never puts a point just outside.</summary>
    private const float FitSlack = 0.01f;

    /// <summary>A moved head goes there (1 px from the edge for the rounding at writing); it may also stay on its
    /// original point.</summary>
    private static readonly Box HeadArea = new(1, 1, 511, 383);

    /// <summary>A moved slider path goes there (2 px from the edge for the rounding of the control points); it may also
    /// stay inside its original bounding box.</summary>
    private static readonly Box BodyArea = new(2, 2, 510, 382);

    private static readonly double[] Angles = new double[AngleSteps];
    private static readonly double[] Cos = new double[AngleSteps];
    private static readonly double[] Sin = new double[AngleSteps];
    private static readonly double[] OrientationCost = new double[AngleSteps];

    static Amplifier()
    {
        for (int t = 0; t < AngleSteps; t++)
        {
            double a = 2 * Math.PI * t / AngleSteps;
            if (a > Math.PI) a -= 2 * Math.PI;
            Angles[t] = a;
            Cos[t] = t == 0 ? 1 : Math.Cos(a);
            Sin[t] = t == 0 ? 0 : Math.Sin(a);
            OrientationCost[t] = OrientationWeight * (a / Math.PI) * (a / Math.PI);
        }
        for (int h = 0; h < 2 * AngleSteps; h++)
            (HalfCos[h], HalfSin[h]) = (Math.Cos(Math.PI * h / AngleSteps), Math.Sin(Math.PI * h / AngleSteps));
    }

    /// <summary>The amplified length of a move of <paramref name="d"/> px: g(d) = max(d, C·tanh(k·d / C)).</summary>
    internal static double Grow(double d, double k) => Math.Max(d, CapPx * Math.Tanh(k * d / CapPx));

    /// <summary>A pattern: consecutive objects placed as one rigid piece.</summary>
    private sealed class Block
    {
        public int From, To;                  // object indices, inclusive
        public bool Spinner, Restart, Pause;  // Pause: the gap before it is at least a beat
        public bool Pinned;                   // fits nowhere: stays on its original spot

        // Local shape, from the head of the first object: head j = A[j] + s·B[j] (B holds the zoomed moves).
        public Vector2[] A = [], B = [];
        public Vector2 EndA, EndB;            // end of the last object
        public Vector2 LastA, LastB;          // last internal move
        public bool HasLast;
        public Vector2 FirstMove;             // first internal move, original
        public bool HasFirst;
        // Where the cursor goes along the body of a slider (length: that of its path): into the first object when it
        // is a slider, out of the last one when it is. The turns around a jump are measured on them (SliderFlow).
        public Vector2 EntryDir, ExitDir;
        public bool HasEntryDir, HasExitDir;
        public bool HasTurnOut;               // TurnOut is measured: on EntryDir, else on FirstMove
        public bool SliderTurnIn, SliderTurnOut; // the turn is measured on a slider body (weighed SliderTurnFactor)
        public bool AfterRun;                 // the block before it ends a run: no pattern to keep the rotation of
        public double WantedScale = 1;        // s*
        public int ScaledMoves;

        // Entry jump, in original positions.
        public Vector2 Jump;
        public double WantedJump;             // gJ
        public Vector2 MoveBefore;            // original move arriving at the end of the previous block
        public bool HasMoveBefore;

        // Constants of the jump cost: original turning angles around the jump and their weights.
        public double TurnIn, TurnOut, TurnInWeight, TurnOutWeight, FirstMoveAngle;

        /// <summary>[angle·Fractions + fraction]: where the head of the first object may go.</summary>
        public Box[] Fits = [];
        public int Fractions = 1;             // fractions tried: only f = 1 when there is nothing to zoom
        public bool[] FitsAt = [];            // per angle: some fraction fits
        public double[] ScaleCosts = [];      // per fraction: 2·(1 − f)²·√(zoomed moves)
        public double[] Steps = Amplifier.Fractions; // the fractions f tried (RunFractions for a block of a run)

        // Streams (Kakudai 2 spec §5.1).
        public int Run = -1;                  // the run (blocks of one stream) it belongs to, or −1
        public bool RunHead;                  // first block of its run
        public bool Chained;                  // entered by a gap of its stream: placed from the previous block
        public double ChainScale = 1;         // wanted zoom of that gap, s* of the stream
        public Block? NextChained;            // the next block, when chained to this one
        public double RunScale = 1;           // largest wanted zoom of its run: the scale of a run fraction
        public int Band;                      // how many blocks of its run before it the band holds over (BandBeats)
        public Box[] RigidFits = [];          // first block of a run: per angle, where the whole run fits at s = 1
        public Box[]?[]?[]? Reach;            // per run fraction and orientation before it, lazily: see Reach()

        // Accented angles (Kakudai 2 spec §5.2).
        public Vector2 JumpUnit;              // direction of the entry jump in the frame of the block (unit, or zero)
        public double[]? Turned;              // per object: how much its body and the move into it are turned, or null
        public double[] Orientation = OrientationCost; // per angle: the orientation cost
        public int Shift;                     // the expected rotation from the previous block, in angle steps
        public bool CheckBack, CheckSecond;   // its first object was far from the one two before, its second from the last
        /// <summary>A copy that the accents can change: they replace its arrays, never write into them. It shares
        /// <see cref="Reach"/> while its fits are the same; <see cref="ComputeFits"/> drops it.</summary>
        /// <summary>A copy that the accents can change: they replace its arrays, never write into them.</summary>
        public Block Copy() => (Block)MemberwiseClone();
    }

    private struct State
    {
        public int Angle;        // orientation passed on to the next block
        public Vector2 Exit;     // end of the last object
        public Vector2 Move;     // last move (end of the object before the last → head of the last)
        public bool HasMove;
        public Vector2 Flow;     // where the cursor leaves the block: the body of its last slider, else Move; the turn
        public bool HasFlow;     // into the next jump is measured from it
        public double Cost;
        public int Parent;
        public int Choice;       // angle index of this block
        public int Fraction;     // index in the block's Steps
        public double Scale;
        public Vector2 Head;
        public Vector2 LastHead; // head of the last object
        public bool Moved;       // the head is not where the entry jump (or the original spot) wanted it
        public bool Short;       // entry jump shorter than 0.9·gJ
        public bool Pinned;      // left on its original spot
        public int Group;        // in a run: index in RunFractions of this block, PinnedGroup or RigidGroup
        public ulong Recent;     // in a run: the RunFractions indices of its last blocks, 4 bits each, this one lowest
    }

    /// <summary>The group of the states of a run left on its original spot.</summary>
    private static readonly int PinnedGroup = RunFractions.Length;

    /// <summary>The group of the states of a run laid down as one rigid piece at s = 1.</summary>
    private static readonly int RigidGroup = RunFractions.Length + 1;

    /// <summary>
    /// The candidate states of one layer of the beam search. The beam keeps the <see cref="BeamWidth"/> cheapest
    /// candidates with distinct keys (sorted by cost, then by order of creation, duplicates of a key already kept
    /// skipped). <see cref="Bound"/> is an upper bound of the cost of the last one kept, so a candidate that costs at
    /// least that much can never be kept and is not stored: this prunes the search without changing its result.
    /// </summary>
    private sealed class Candidates
    {
        public readonly List<State> Items = [];
        private readonly (int, int, int)[] keys = new (int, int, int)[BeamWidth];
        private readonly double[] costs = new double[BeamWidth];
        private readonly HashSet<(int, int, int)> seen = [];
        private int tracked;

        /// <summary>
        /// The largest cost among <see cref="BeamWidth"/> distinct keys seen, each with a cost at least the cheapest
        /// of its candidates (infinite until that many keys are seen): the cheapest candidates of those keys already
        /// beat anything that costs as much, and come first on equal costs.
        /// </summary>
        public double Bound { get; private set; } = double.PositiveInfinity;

        public void Clear()
        {
            Items.Clear();
            tracked = 0;
            Bound = double.PositiveInfinity;
        }

        public void Add(in State st)
        {
            if (st.Cost >= Bound) return;
            Items.Add(st);

            var key = Key(st);
            for (int i = 0; i < tracked; i++)
            {
                if (keys[i] != key) continue;
                costs[i] = Math.Min(costs[i], st.Cost);
                UpdateBound();
                return;
            }
            if (tracked < BeamWidth)
            {
                keys[tracked] = key;
                costs[tracked++] = st.Cost;
            }
            else
            {
                int worst = 0;
                for (int i = 1; i < tracked; i++)
                    if (costs[i] > costs[worst]) worst = i;
                keys[worst] = key;
                costs[worst] = st.Cost; // < Bound = costs[worst]
            }
            UpdateBound();
        }

        private void UpdateBound()
        {
            if (tracked < BeamWidth) return;
            double max = costs[0];
            for (int i = 1; i < tracked; i++) max = Math.Max(max, costs[i]);
            Bound = max;
        }

        /// <summary>The kept states: the cheapest candidates, in cost order, skipping keys already kept.</summary>
        public List<State> Best()
        {
            var kept = new List<State>(BeamWidth);
            seen.Clear();
            while (kept.Count < BeamWidth)
            {
                int best = -1;
                for (int c = 0; c < Items.Count; c++)
                    if ((best < 0 || Items[c].Cost < Items[best].Cost) && !seen.Contains(Key(Items[c])))
                        best = c;
                if (best < 0) break;
                seen.Add(Key(Items[best]));
                kept.Add(Items[best]);
            }
            return kept;
        }
    }

    public static AmplifyResult Amplify(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        AmplifyOptions options)
        => Amplify(objects, beatLengthAt, options, out _);

    /// <param name="revertedBlocks">Blocks put back on their original spot by the final validation (spec
    /// §8.2-ter step 7); their objects are also counted in <see cref="AmplifyResult.Corrected"/>.</param>
    internal static AmplifyResult Amplify(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        AmplifyOptions options, out int revertedBlocks)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(beatLengthAt);
        ArgumentNullException.ThrowIfNull(options);

        revertedBlocks = 0;
        int n = objects.Count;
        if ((options.Factor == 1 && !options.AccentAngles) || n == 0)
            return Identity(objects); // exactly, by a direct path; with accented angles, k = 1 still changes the angles

        var beats = new double[n];
        for (int i = 0; i < n; i++)
            beats[i] = beatLengthAt(objects[i].StartTime);
        Outcome outcome;
        if (options.AccentAngles)
            outcome = AmplifyAccented(objects, beats, options);
        else
        {
            outcome = new Outcome(objects);
            AmplifyAll(objects, beats, options, outcome);
        }
        revertedBlocks = outcome.RevertedBlocks;
        return outcome.Result(objects);
    }

    /// <summary>Every object on its original spot.</summary>
    private static AmplifyResult Identity(IReadOnlyList<MapObject> objects)
    {
        var placements = new Placement[objects.Count];
        for (int i = 0; i < placements.Length; i++)
            placements[i] = new Placement(objects[i].Start, 0);
        var (same, _) = AverageJumps(objects, placements);
        return new AmplifyResult(placements, 0, 0, same, same);
    }

    /// <summary>Where every object of a map goes, and how the search placed it.</summary>
    private sealed class Outcome
    {
        public readonly Placement[] Placements;

        /// <summary>Per object: counted in <see cref="AmplifyResult.Corrected"/>, in
        /// <see cref="AmplifyResult.Reduced"/>; the first object of a block put back by the final validation.</summary>
        public readonly bool[] Corrected, Reduced, Reverted;

        /// <summary>Every object on its original spot.</summary>
        public Outcome(IReadOnlyList<MapObject> objects)
        {
            int n = objects.Count;
            Placements = new Placement[n];
            for (int i = 0; i < n; i++)
                Placements[i] = new Placement(objects[i].Start, 0);
            (Corrected, Reduced, Reverted) = (new bool[n], new bool[n], new bool[n]);
        }

        public int RevertedBlocks => Reverted.Count(r => r);

        /// <summary>The objects from <paramref name="from"/> to <paramref name="to"/> as in <paramref name="other"/>.
        /// </summary>
        public void CopyFrom(Outcome other, int from, int to)
        {
            int count = to - from + 1;
            Array.Copy(other.Placements, from, Placements, from, count);
            Array.Copy(other.Corrected, from, Corrected, from, count);
            Array.Copy(other.Reduced, from, Reduced, from, count);
            Array.Copy(other.Reverted, from, Reverted, from, count);
        }

        public AmplifyResult Result(IReadOnlyList<MapObject> objects)
        {
            var (before, after) = AverageJumps(objects, Placements);
            return new AmplifyResult(Placements, Corrected.Count(c => c), Reduced.Count(r => r), before, after);
        }
    }

    /// <summary>
    /// The ranges of objects amplified on their own, each from a restart: the whole map; with only the kiai (Kakudai 2
    /// spec §5.3), each kiai section, every other object keeping its exact place. A stream across the edge of a kiai
    /// section is split there: its part outside the kiai stays where it is.
    /// </summary>
    private static List<(int From, int To)> Parts(IReadOnlyList<MapObject> objects, bool kiaiOnly)
    {
        if (!kiaiOnly) return [(0, objects.Count - 1)];
        var parts = new List<(int, int)>();
        for (int from = 0; from < objects.Count;)
        {
            if (!objects[from].InKiai)
            {
                from++;
                continue;
            }
            int to = from;
            while (to + 1 < objects.Count && objects[to + 1].InKiai) to++;
            parts.Add((from, to));
            from = to + 1;
        }
        return parts;
    }

    /// <summary>
    /// The parts (<see cref="Parts"/>) cut at every restart of the search: a spinner, the object after it, an object
    /// at least <see cref="RestartBeats"/> after the previous one and not stacked on it. Each one is placed
    /// independently of the others, too far from it in time to overlap it.
    /// </summary>
    private static List<(int From, int To)> Units(IReadOnlyList<MapObject> objects, double[] beats, bool kiaiOnly)
    {
        var units = new List<(int, int)>();
        foreach (var (from, to) in Parts(objects, kiaiOnly))
        {
            int start = from;
            for (int i = from + 1; i <= to; i++)
            {
                var (o, previous) = (objects[i], objects[i - 1]);
                if (o.Kind != ObjectKind.Spinner && previous.Kind != ObjectKind.Spinner
                    && (o.StartTime - previous.EndTime < RestartBeats * beats[i]
                        || Vector2.Distance(o.Start, previous.End) < StackPx))
                    continue;
                units.Add((start, i - 1));
                start = i;
            }
            units.Add((start, to));
        }
        return units;
    }

    /// <summary>Amplifies every part of the map (<see cref="Parts"/>), without accented angles, into
    /// <paramref name="outcome"/>, which holds the original spots.</summary>
    private static void AmplifyAll(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options,
        Outcome outcome)
    {
        foreach (var (from, to) in Parts(objects, options.KiaiOnly))
        {
            var part = Plan(objects, beats, from, to, options);
            Finish(part, part.Blocks, Search(part.Objects, part.Blocks), outcome);
        }
    }

    /// <summary>
    /// Amplifies the map with accented angles, which must add no overlap (Kakudai 2 spec §5.2). Where the accented result
    /// lands an object on a recent one (<see cref="Overlaps"/>, at <see cref="AccentCheckPx"/>) and the result without
    /// the option does not (<see cref="Excused"/>), the accents of the turns from the recent object to the object (and
    /// of the <see cref="AccentWiden"/> objects before, more on each round) are dropped, and only the blocks whose
    /// accents changed are searched again, the others keeping their places: the map is repaired from left to right (a
    /// search from scratch would move everything after, and land new objects on each other). After the last round, a
    /// unit (<see cref="Units"/>) that still has such an overlap is placed as without the option. The whole map is
    /// checked, so with only the kiai the objects left in place around a kiai section count. Lab A3, 200 maps: at
    /// k = 1, no overlap (A2: 3.3 ‰) and turns between jumps of 128° (A2: 132°, 114° in the maps); at k = 2, 6.6 ‰
    /// against 10.4 ‰ without the option, turns of 130° (A2: 134°, 120° without), in 2.5 times the time without it.
    /// </summary>
    private static Outcome AmplifyAccented(IReadOnlyList<MapObject> objects, double[] beats, AmplifyOptions options)
    {
        int n = objects.Count;
        var dropped = new bool[n];
        var layouts = (new Layout(objects), new Layout(objects)); // the accents are decided on them, round after round
        var outcome = new Outcome(objects);
        var parts = new List<(Part Part, State[] Chosen, double[] Accents)>();
        var partOf = new int[n];
        Array.Fill(partOf, -1); // left in place, with only the kiai
        foreach (var (from, to) in Parts(objects, options.KiaiOnly))
        {
            var part = Plan(objects, beats, from, to, options);
            var (blocks, accents) = WithAccents(part, objects, beats, dropped, layouts);
            var chosen = Search(part.Objects, blocks);
            Finish(part, blocks, chosen, outcome);
            for (int i = from; i <= to; i++) partOf[i] = parts.Count;
            parts.Add((part, chosen, accents));
        }
        var overlaps = Overlaps(objects, beats, outcome.Placements, AccentCheckPx);
        if (overlaps.Count == 0) return outcome;

        var plain = new Outcome(objects); // at k = 1, the map as it is
        if (options.Factor != 1)
            foreach (var (part, _, _) in parts) // the blocks without the accents
                Finish(part, part.Blocks, Search(part.Objects, part.Blocks), plain);
        var before = Excused(objects, beats, plain.Placements);

        var touched = new bool[parts.Count]; // per part, some accent dropped in the round
        for (int round = 0; round < AccentWiden.Length; round++)
        {
            Array.Clear(touched);
            bool any = false;
            foreach (var (i, j) in overlaps)
            {
                if (before[i]) continue;
                any = true;
                // Two objects left in place are where they are without the option: one of them moved.
                int p = partOf[i] >= 0 ? partOf[i] : partOf[j];
                var part = parts[p].Part;
                int moved = i <= part.To ? i : j; // the later of the two in the part
                for (int m = Math.Max(part.From, Math.Min(j, moved - 1) - AccentWiden[round]); m < moved; m++)
                {
                    touched[p] |= !dropped[m];
                    dropped[m] = true;
                }
            }
            if (!any) return outcome;

            // Only the blocks whose accents changed are searched again, each from the last state before it: the search
            // goes on until the next restart (whose candidates are the same whatever the state before it, but for their
            // cost), or until it finds again, before a block whose accents did not change, the state it had.
            bool changed = false;
            for (int p = 0; p < parts.Count; p++)
            {
                if (!touched[p]) continue;
                var (part, chosen, old) = parts[p];
                var (blocks, accents) = WithAccents(part, objects, beats, dropped, layouts);
                var same = new bool[blocks.Count];
                for (int b = 0; b < blocks.Count; b++)
                {
                    // A block depends on the accents of the object before it and of its own but the last.
                    same[b] = true;
                    for (int m = Math.Max(0, blocks[b].From - 1); m <= Math.Max(blocks[b].From, blocks[b].To - 1); m++)
                        same[b] &= accents[m] == old[m];
                }
                for (int b = 0; b < blocks.Count;)
                {
                    if (same[b])
                    {
                        b++;
                        continue;
                    }
                    int stop = b + 1;
                    while (stop < blocks.Count && !blocks[stop].Restart) stop++;
                    chosen = Search(part.Objects, blocks, chosen, b, stop, same, out b);
                    changed = true;
                }
                Finish(part, blocks, chosen, outcome);
                parts[p] = (part, chosen, accents);
            }
            if (changed) overlaps = Overlaps(objects, beats, outcome.Placements, AccentCheckPx);
        }

        // Last resort: each unit, placed independently of the others, as without the option. With only the kiai, an
        // overlap may tie two kiai sections close in time: both are, until there is no such overlap left.
        var units = Units(objects, beats, options.KiaiOnly);
        var unitOf = new int[n];
        Array.Fill(unitOf, -1);
        for (int u = 0; u < units.Count; u++)
            for (int i = units[u].From; i <= units[u].To; i++) unitOf[i] = u;
        var asPlain = new bool[units.Count];
        while (true)
        {
            bool any = false;
            foreach (var (i, j) in overlaps)
            {
                if (before[i]) continue;
                foreach (int u in (ReadOnlySpan<int>)[unitOf[i], unitOf[j]])
                {
                    if (u < 0 || asPlain[u]) continue;
                    outcome.CopyFrom(plain, units[u].From, units[u].To);
                    asPlain[u] = any = true;
                }
            }
            if (!any) return outcome;
            overlaps = Overlaps(objects, beats, outcome.Placements, AccentCheckPx);
        }
    }

    /// <summary>The extra turn the guard of the accents (<see cref="Accents"/>) gives every object, before any block
    /// is placed or checked: for the tests of the guard alone.</summary>
    internal static double[] GuardedAccents(IReadOnlyList<MapObject> objects, Func<double, double> beatLengthAt,
        AmplifyOptions options)
    {
        var beats = objects.Select(o => beatLengthAt(o.StartTime)).ToArray();
        var accents = new double[objects.Count];
        foreach (var (from, to) in Parts(objects, options.KiaiOnly))
        {
            var part = Plan(objects, beats, from, to, options);
            var around = new Around(objects, beats, from, null, new Layout(objects), new Layout(objects));
            var own = Accents(part.Objects, part.Beats, part.K, part.SpaceStreams, part.Blocks, around);
            Array.Copy(own, 0, accents, from, own.Length);
        }
        return accents;
    }

    /// <summary>Per object, whether the result without the option (<paramref name="plain"/>) already lands it on a
    /// recent object it was far from, closer than <see cref="ExcusedPx"/>: the accented result may do so too.</summary>
    internal static bool[] Excused(IReadOnlyList<MapObject> objects, double[] beats, IReadOnlyList<Placement> plain)
    {
        var excused = new bool[objects.Count];
        foreach (var (i, _) in Overlaps(objects, beats, plain, ExcusedPx)) excused[i] = true;
        return excused;
    }

    /// <summary>
    /// The overlaps of <paramref name="placements"/>, as the metric of the lab counts them: (i, j) for every head i
    /// that lands closer than <paramref name="px"/> to the head or the end of an object j of the 12 before it within 2
    /// beats (the previous one excepted, no spinner between), although it was at least <see cref="FarPx"/> from both.
    /// </summary>
    private static List<(int I, int J)> Overlaps(IReadOnlyList<MapObject> objects, double[] beats,
        IReadOnlyList<Placement> placements, float px)
    {
        var pairs = new List<(int, int)>();
        var ends = new Vector2[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            var (o, p) = (objects[i], placements[i]);
            ends[i] = p.Start + Rotate(o.End - o.Start, p.Rotation);
            if (o.Kind == ObjectKind.Spinner) continue;
            for (int j = i - 2; j >= 0 && j >= i - 12; j--)
            {
                if (objects[j].Kind == ObjectKind.Spinner || o.StartTime - objects[j].StartTime > 2 * beats[i]) break;
                float now = Math.Min(Vector2.Distance(p.Start, placements[j].Start), Vector2.Distance(p.Start, ends[j]));
                if (now >= px) continue;
                float before = Math.Min(Vector2.Distance(o.Start, objects[j].Start),
                    Vector2.Distance(o.Start, objects[j].End));
                if (before >= FarPx) pairs.Add((i, j));
            }
        }
        return pairs;
    }

    /// <summary>A part of the map amplified on its own, from a restart (<see cref="Parts"/>): the objects from
    /// <see cref="From"/> to <see cref="To"/> of the map, the beat length at each, and their blocks before any accent.
    /// </summary>
    private sealed record Part(int From, int To, IReadOnlyList<MapObject> Objects, double[] Beats, double K,
        bool SpaceStreams, List<Block> Blocks);

    /// <summary>The blocks of the objects from <paramref name="from"/> to <paramref name="to"/> of the map, linked
    /// (spec §8.2-ter steps 2 to 5, Kakudai 2 spec §5.1).</summary>
    private static Part Plan(IReadOnlyList<MapObject> map, double[] mapBeats, int from, int to, AmplifyOptions options)
    {
        int n = to - from + 1;
        IReadOnlyList<MapObject> objects = map;
        var beats = mapBeats;
        if (n != map.Count)
        {
            var part = new MapObject[n];
            for (int i = 0; i < n; i++) part[i] = map[from + i];
            (objects, beats) = (part, mapBeats[from..(to + 1)]);
        }
        double k = options.Factor;
        var blocks = new List<Block>();
        foreach (var (start, end, restart, pause) in Ranges(objects, options.SpaceStreams, beats))
            AddFitting(objects, beats, k, options.SpaceStreams, blocks, start, end, restart, pause);
        LinkJumps(objects, beats, k, options.SpaceStreams, blocks);
        LinkRuns(objects, beats, k, options.SpaceStreams, blocks);
        return new Part(from, to, objects, beats, k, options.SpaceStreams, blocks);
    }

    /// <summary>Copies of the blocks of the part with the accents of its turns (Kakudai 2 spec §5.2), the objects marked
    /// in <paramref name="dropped"/> getting none, and the accent of every object; the blocks of the part are left as
    /// they are.</summary>
    private static (List<Block> Blocks, double[] Accents) WithAccents(Part part, IReadOnlyList<MapObject> map,
        double[] mapBeats, bool[] dropped, (Layout Plain, Layout Accented) layouts)
    {
        var blocks = new List<Block>(part.Blocks.Count);
        foreach (var block in part.Blocks) blocks.Add(block.Copy());
        for (int b = 0; b < blocks.Count; b++)
            if (blocks[b].NextChained != null) blocks[b].NextChained = blocks[b + 1]; // chained to the next one
        var accents = Accents(part.Objects, part.Beats, part.K, part.SpaceStreams, blocks,
            new Around(map, mapBeats, part.From, dropped, layouts.Plain, layouts.Accented));
        AccentBlocks(part.Objects, part.Beats, part.SpaceStreams, blocks, accents);
        return (blocks, accents); // the accents as the blocks took them
    }

    /// <summary>Writes where the objects of the part go for the chosen states into <paramref name="outcome"/>, after the
    /// final validation (spec §8.2-ter step 7), and how they were placed.</summary>
    private static void Finish(Part part, List<Block> blocks, State[] chosen, Outcome outcome)
    {
        var (objects, from) = (part.Objects, part.From);
        int n = objects.Count;
        var placements = new Placement[n];
        for (int i = 0; i < n; i++)
            placements[i] = new Placement(objects[i].Start, 0);
        Array.Clear(outcome.Corrected, from, n);
        Array.Clear(outcome.Reduced, from, n);
        Array.Clear(outcome.Reverted, from, n);

        // A block not allowed where it lands goes back to its original spot, and with it the rest of its run, so that
        // its stream is not cut.
        var back = new bool[blocks.Count];
        var runsBack = new HashSet<int>();
        for (int b = 0; b < blocks.Count; b++)
        {
            if (blocks[b].Spinner || chosen[b].Pinned) continue;
            Place(objects, blocks[b], chosen[b], placements);
            if (BlockValid(objects, blocks[b], placements)) continue;
            back[b] = true;
            if (blocks[b].Run >= 0) runsBack.Add(blocks[b].Run);
        }
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            if (block.Spinner || chosen[b].Pinned) continue;
            back[b] |= block.Run >= 0 && runsBack.Contains(block.Run);
            if (!back[b]) continue;
            for (int i = block.From; i <= block.To; i++)
                placements[i] = new Placement(objects[i].Start, 0);
            outcome.Reverted[from + block.From] = true;
        }

        int previousAngle = 0;
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var st = chosen[b];
            if (block.Spinner)
                continue;
            bool corrected = true, reduced = false;
            if (st.Pinned)
            {
                reduced = st.Short;
                previousAngle = 0;
            }
            else if (back[b])
                previousAngle = 0;
            else
            {
                corrected = st.Choice != (previousAngle + block.Shift + AngleSteps) % AngleSteps || st.Moved;
                reduced = (block.WantedScale > 1 && block.Steps[st.Fraction] < 1) || st.Short;
                previousAngle = st.Choice;
            }
            for (int i = block.From; i <= block.To; i++)
                (outcome.Corrected[from + i], outcome.Reduced[from + i]) = (corrected, reduced);
        }
        Array.Copy(placements, 0, outcome.Placements, from, n);
    }

    /// <summary>
    /// Cuts the objects into blocks, in order (spec §8.2-ter step 2): a spinner is a block of its own; an object
    /// stacked on the end of the previous one always joins its block; otherwise a pause, a change of rhythm, a block
    /// already too long or too full starts a new one.
    /// </summary>
    private static List<(int From, int To, bool Restart, bool Pause)> Ranges(IReadOnlyList<MapObject> objects,
        bool spaceStreams, double[] beats)
    {
        var ranges = new List<(int, int, bool, bool)>();
        int from = -1;
        double rhythm = -1;
        bool streamRhythm = false; // the first move of the block is a stream step (IsStreamStep)
        bool restartNext = true, pauseNext = false;

        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.Kind == ObjectKind.Spinner)
            {
                if (from >= 0) ranges.Add((from, i - 1, restartNext, pauseNext));
                ranges.Add((i, i, false, false));
                from = -1;
                restartNext = true;
                pauseNext = false;
                continue;
            }
            if (from < 0)
            {
                from = i;
                rhythm = -1;
                continue;
            }

            double beat = beats[i];
            double gap = o.StartTime - objects[i - 1].EndTime;
            if (Vector2.Distance(o.Start, objects[i - 1].End) < StackPx)
                continue; // stacked: always in the same block

            bool pause = gap >= PauseBeats * beat - TimeSlackMs;
            bool split = pause
                         || (rhythm > 0 && (gap > rhythm * RhythmRatio || gap < rhythm / RhythmRatio))
                         || o.StartTime - objects[from].StartTime
                            > ((rhythm > 0 ? streamRhythm : IsStreamStep(objects, beats, i))
                                ? (spaceStreams ? MaxStreamBlockBeats : MaxKeptStreamBlockBeats) : MaxBlockBeats) * beat
                            + TimeSlackMs
                         || i - from + 1 > MaxBlockObjects;
            if (split)
            {
                ranges.Add((from, i - 1, restartNext, pauseNext));
                restartNext = gap >= RestartBeats * beat;
                pauseNext = pause;
                from = i;
                rhythm = -1;
            }
            else if (rhythm < 0)
                (rhythm, streamRhythm) = (gap, IsStreamStep(objects, beats, i));
        }
        if (from >= 0) ranges.Add((from, objects.Count - 1, restartNext, pauseNext));
        return ranges;
    }

    /// <summary>
    /// Whether object <paramref name="j"/> follows the previous one at the pace of a stream: its head comes at most
    /// <see cref="StreamGapInBeats"/> after the previous head. Measured head to head, not from the end of a slider:
    /// short sliders every 1/3 with 1/6 between the tail and the next head are slider jumps, not a stream (a reviewer's
    /// map, Deadlocked [Zugzwang] 0:49–0:57, was taken for one long stream, which fitted nowhere spaced out and was
    /// left exactly in place). Between circles, both are the same.
    /// </summary>
    private static bool IsStreamStep(IReadOnlyList<MapObject> objects, double[] beats, int j)
        => objects[j].StartTime - objects[j - 1].EndTime <= StreamGapInBeats * beats[j]
           && !(objects[j - 1].Kind == ObjectKind.Slider && objects[j].Kind == ObjectKind.Slider);

    /// <summary>Adds the block, or, when it fits nowhere, its two halves (recursively); a single object that fits
    /// nowhere is pinned to its original spot.</summary>
    private static void AddFitting(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        List<Block> blocks, int from, int to, bool restart, bool pause)
    {
        var block = Build(objects, beats, k, spaceStreams, from, to, restart, pause);
        bool fits = !block.Spinner && AnyFit(block);
        if (block.Spinner || from == to || fits)
        {
            block.Pinned = !block.Spinner && !fits;
            blocks.Add(block);
            return;
        }

        int cut = Cut(objects, from, to);
        AddFitting(objects, beats, k, spaceStreams, blocks, from, cut, restart, pause);
        AddFitting(objects, beats, k, spaceStreams, blocks, cut + 1, to, restart: false, pause: false);
    }

    /// <summary>Where to cut a block in two: after the object closest to the middle that the next object is not
    /// stacked on (the middle itself when every object is stacked).</summary>
    private static int Cut(IReadOnlyList<MapObject> objects, int from, int to)
    {
        double middle = (from + to) / 2.0;
        int best = -1;
        for (int c = from; c < to; c++)
        {
            if (Vector2.Distance(objects[c + 1].Start, objects[c].End) < StackPx) continue;
            if (best < 0 || Math.Abs(c - middle) < Math.Abs(best - middle)) best = c;
        }
        return best >= 0 ? best : (from + to) / 2;
    }

    private static bool AnyFit(Block block)
    {
        foreach (var box in block.Fits)
            if (!box.IsEmpty) return true;
        return false;
    }

    /// <summary>The local shape of a block and where it fits, for every rotation and zoom (spec steps 3 and 4).</summary>
    private static Block Build(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        int from, int to, bool restart, bool pause)
    {
        var block = new Block { From = from, To = to, Restart = restart, Pause = pause };
        if (objects[from].Kind == ObjectKind.Spinner)
        {
            block.Spinner = true;
            return block;
        }

        var moves = new List<double>();
        for (int j = from + 1; j <= to; j++)
        {
            var v = objects[j].Start - objects[j - 1].End;
            if (Zoomed(objects, beats, spaceStreams, j, v.Length()))
                moves.Add(v.Length());
        }
        if (moves.Count > 0)
        {
            moves.Sort();
            double median = moves[moves.Count / 2];
            block.WantedScale = Grow(median, k) / median;
            block.ScaledMoves = moves.Count;
        }

        Shape(objects, beats, spaceStreams, block);
        ComputeFits(objects, block);
        return block;
    }

    /// <summary>The local shape of a block (spec step 3); with <see cref="Block.Turned"/>, each object's body and the
    /// move into it turned by it (accented angles).</summary>
    private static void Shape(IReadOnlyList<MapObject> objects, double[] beats, bool spaceStreams, Block block)
    {
        int from = block.From, to = block.To, n = to - from + 1;
        var turned = block.Turned;
        block.A = new Vector2[n];
        block.B = new Vector2[n];
        for (int j = from + 1; j <= to; j++)
        {
            int jj = j - from;
            var v = objects[j].Start - objects[j - 1].End;
            bool zoomed = Zoomed(objects, beats, spaceStreams, j, v.Length());
            var body = objects[j - 1].End - objects[j - 1].Start;
            if (j == from + 1)
            {
                block.FirstMove = v;
                block.HasFirst = v.Length() >= StackPx;
            }
            if (turned != null)
            {
                v = Rotate(v, Math.Cos(turned[jj]), Math.Sin(turned[jj]));
                body = Rotate(body, Math.Cos(turned[jj - 1]), Math.Sin(turned[jj - 1]));
            }
            var moveA = zoomed ? Vector2.Zero : v;
            var moveB = zoomed ? v : Vector2.Zero;
            block.A[jj] = block.A[jj - 1] + body + moveA;
            block.B[jj] = block.B[jj - 1] + moveB;
            block.LastA = moveA;
            block.LastB = moveB;
            block.HasLast = true;
        }
        var last = objects[to].End - objects[to].Start;
        if (turned != null) last = Rotate(last, Math.Cos(turned[n - 1]), Math.Sin(turned[n - 1]));
        block.EndA = block.A[n - 1] + last;
        block.EndB = block.B[n - 1];

        block.HasEntryDir = SliderFlow(objects[from], out var entry, out _);
        block.HasExitDir = SliderFlow(objects[to], out _, out var exit);
        if (turned != null)
        {
            entry = Rotate(entry, Math.Cos(turned[0]), Math.Sin(turned[0]));
            exit = Rotate(exit, Math.Cos(turned[n - 1]), Math.Sin(turned[n - 1]));
        }
        (block.EntryDir, block.ExitDir) = (entry, exit);
    }

    /// <summary>Samples of a slider path the tangents at its ends are taken over (of 32 steps): an eighth of it.</summary>
    private const int TangentSteps = 4;

    /// <summary>
    /// Where the cursor goes along the body of a slider, as vectors as long as its path: <paramref name="entry"/>, the
    /// direction it starts in from the head; <paramref name="exit"/>, the direction it leaves the end in (the tangent at
    /// the tail, or back toward the head after an odd number of repeats). A mapper builds the flow around these, not
    /// around the head-to-head moves: a jump out of a slider turns from the way its body goes. False for anything but a
    /// slider whose path is at least <see cref="StackPx"/> long.
    /// </summary>
    private static bool SliderFlow(MapObject o, out Vector2 entry, out Vector2 exit)
    {
        (entry, exit) = (Vector2.Zero, Vector2.Zero);
        var p = o.PathSamples;
        if (o.Kind != ObjectKind.Slider || p.Count < 2) return false;
        float length = 0;
        for (int i = 1; i < p.Count; i++) length += Vector2.Distance(p[i - 1], p[i]);
        int m = p.Count - 1, step = Math.Min(TangentSteps, m);
        var start = p[step] - p[0];
        var end = p[m] - p[m - step];
        if (length < StackPx || start.LengthSquared() < 1e-6f || end.LengthSquared() < 1e-6f) return false;
        bool forward = Vector2.Distance(o.End, o.Start + p[m]) <= Vector2.Distance(o.End, o.Start);
        entry = Vector2.Normalize(start) * length;
        exit = Vector2.Normalize(forward ? end : -start) * length;
        return true;
    }

    /// <summary>Whether the move into object <paramref name="j"/> is zoomed: not a stack, and not a stream gap when
    /// streams keep their spacing.</summary>
    private static bool Zoomed(IReadOnlyList<MapObject> objects, double[] beats, bool spaceStreams, int j, float length)
        => length >= StackPx
           && (spaceStreams || !IsStreamStep(objects, beats, j));

    /// <summary>
    /// For every rotation θ and zoom s, the box of the positions of the first head that keep every head in
    /// <see cref="HeadArea"/> and every path point in <see cref="BodyArea"/>, exact axis by axis.
    /// </summary>
    private static void ComputeFits(IReadOnlyList<MapObject> objects, Block block)
    {
        int fractions = block.WantedScale > 1 ? block.Steps.Length : 1;
        block.Fractions = fractions;
        block.Fits = new Box[AngleSteps * fractions];
        block.Reach = null; // worked out from the fits
        block.FitsAt = new bool[AngleSteps];
        block.ScaleCosts = new double[fractions];
        for (int f = 0; f < fractions; f++)
            block.ScaleCosts[f] = block.ScaledMoves > 0
                ? ScaleWeight * (1 - block.Steps[f]) * (1 - block.Steps[f]) * Math.Sqrt(block.ScaledMoves)
                : 0;
        var scales = new float[fractions];
        for (int f = 0; f < fractions; f++)
            scales[f] = (float)(1 + block.Steps[f] * (block.WantedScale - 1));

        var heads = HeadArea.Shrink(FitSlack);
        var bodies = BodyArea.Shrink(FitSlack);
        var lo = new Vector2[fractions];
        var hi = new Vector2[fractions];

        // The extent of a rotated path is that of the convex hull of its samples: fewer points to turn.
        var hulls = new Vector2[block.To - block.From + 1][];
        for (int j = block.From; j <= block.To; j++)
        {
            var hull = Hull(objects[j].PathSamples);
            if (block.Turned is { } turned && turned[j - block.From] != 0)
            {
                // The hull of the turned path is the turned hull (Hull returns a new array).
                double c = Math.Cos(turned[j - block.From]), s = Math.Sin(turned[j - block.From]);
                for (int q = 0; q < hull.Length; q++) hull[q] = Rotate(hull[q], c, s);
            }
            hulls[j - block.From] = hull;
        }

        for (int t = 0; t < AngleSteps; t++)
        {
            double c = Cos[t], s = Sin[t];
            Array.Fill(lo, new Vector2(float.NegativeInfinity));
            Array.Fill(hi, new Vector2(float.PositiveInfinity));

            for (int j = block.From; j <= block.To; j++)
            {
                int jj = j - block.From;
                var a = Rotate(block.A[jj], c, s);
                var b = Rotate(block.B[jj], c, s);

                // Extent of the rotated path around its head.
                var hull = hulls[jj];
                Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                for (int q = 0; q < hull.Length; q++)
                {
                    var r = Rotate(hull[q], c, s);
                    min = Vector2.Min(min, r);
                    max = Vector2.Max(max, r);
                }

                for (int f = 0; f < fractions; f++)
                {
                    var head = a + scales[f] * b;
                    lo[f] = Vector2.Max(lo[f], heads.Min - head);
                    hi[f] = Vector2.Min(hi[f], heads.Max - head);
                    if (hull.Length > 0)
                    {
                        lo[f] = Vector2.Max(lo[f], bodies.Min - (head + min));
                        hi[f] = Vector2.Min(hi[f], bodies.Max - (head + max));
                    }
                }
            }

            for (int f = 0; f < fractions; f++)
            {
                var box = new Box(lo[f].X, lo[f].Y, hi[f].X, hi[f].Y);
                block.Fits[t * fractions + f] = box;
                block.FitsAt[t] |= !box.IsEmpty;
            }
        }
    }

    /// <summary>Vertices of the convex hull of <paramref name="points"/> (monotone chain); empty for no point.</summary>
    private static Vector2[] Hull(IReadOnlyList<Vector2> points)
    {
        if (points.Count <= 2)
            return [.. points];

        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var hull = new Vector2[2 * sorted.Length];
        int count = 0;

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        foreach (var p in sorted)
        {
            while (count >= 2 && Cross(hull[count - 2], hull[count - 1], p) <= 0) count--;
            hull[count++] = p;
        }
        int lower = count + 1;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (count >= lower && Cross(hull[count - 2], hull[count - 1], p) <= 0) count--;
            hull[count++] = p;
        }
        return hull[..Math.Max(1, count - 1)];
    }

    /// <summary>The entry jump of every block, and the original move before it (spec step 5).</summary>
    private static void LinkJumps(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        List<Block> blocks)
    {
        Vector2 previousMove = Vector2.Zero;
        bool hasPrevious = false, fromSlider = false;
        foreach (var block in blocks)
        {
            if (block.Spinner)
            {
                hasPrevious = false;
                continue;
            }

            if (!block.Restart && block.From > 0)
            {
                int f = block.From;
                block.Jump = objects[f].Start - objects[f - 1].End;
                float length = block.Jump.Length();
                block.JumpUnit = length > 0 ? block.Jump / length : Vector2.Zero;
                bool zoomed = spaceStreams || !IsStreamStep(objects, beats, f);
                block.WantedJump = length < StackPx || !zoomed ? length : Grow(length, k);
                block.MoveBefore = previousMove;
                block.HasMoveBefore = hasPrevious && previousMove.Length() > 1;

                double importance = TurnImportance(length);
                if (block.HasMoveBefore)
                {
                    block.TurnIn = Angle(block.MoveBefore, block.Jump);
                    block.TurnInWeight = TurnWeight * (block.Pause ? PauseTurnFactor : 1)
                                         * TurnImportance(block.MoveBefore.Length()) * importance;
                    block.SliderTurnIn = fromSlider;
                }
                // Out of the jump: along the body of a slider when the block starts with one, else its first move.
                block.HasTurnOut = block.HasEntryDir || block.HasFirst;
                if (block.HasTurnOut)
                {
                    var after = block.HasEntryDir ? block.EntryDir : block.FirstMove;
                    block.TurnOut = Angle(block.Jump, after);
                    block.TurnOutWeight = TurnWeight * importance * TurnImportance(after.Length());
                    block.SliderTurnOut = block.HasEntryDir;
                    block.FirstMoveAngle = Math.Atan2(after.Y, after.X);
                }
            }

            // Into the next jump: along the body of the last object when it is a slider, else the last move.
            previousMove = block.HasExitDir ? block.ExitDir : block.HasLast ? block.LastA + block.LastB : block.Jump;
            hasPrevious = block.HasExitDir || block.HasLast || !block.Restart;
            fromSlider = block.HasExitDir;
        }
    }

    /// <summary>
    /// The runs of the streams (Kakudai 2 spec §5.1, §13, <see cref="Chain"/>): every stream gap between two blocks
    /// joins them in a run, chained when it is a gap of the stream. Each block of a run learns the blocks its band holds
    /// over (<see cref="BandBeats"/>), and is fitted for the finer <see cref="RunFractions"/>; the first one learns
    /// where the whole run fits as one rigid piece at s = 1.
    /// </summary>
    private static void LinkRuns(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        List<Block> blocks)
    {
        Chain(objects, beats, k, spaceStreams, blocks);
        for (int b = 1; b < blocks.Count; b++)
            blocks[b].AfterRun = blocks[b - 1].Run >= 0 && blocks[b].Run < 0;
        for (int b = 0; b < blocks.Count; b++)
        {
            if (!blocks[b].RunHead) continue;
            int end = b;
            while (end + 1 < blocks.Count && blocks[end + 1].Run == blocks[b].Run) end++;

            double scale = 1;
            for (int i = b; i <= end; i++)
                scale = Math.Max(scale, Math.Max(blocks[i].WantedScale, blocks[i].ChainScale));
            for (int i = b; i <= end; i++)
            {
                var block = blocks[i];
                block.RunScale = scale;
                // The blocks before it that start less than BandBeats before it (at least the previous one), at most
                // BandBlocks of them: when blocks shorter than a beat (halves of blocks that fit nowhere, single
                // objects) crowd more than that into 8 beats, the band holds over a shorter window, and the zoom still
                // changes by NeighbourRatio at most from a block to the next.
                double start = objects[block.From].StartTime, window = BandBeats * beats[block.From];
                block.Band = 0;
                while (block.Band < Math.Min(i - b, BandBlocks)
                       && (block.Band == 0 || start - objects[blocks[i - block.Band - 1].From].StartTime < window))
                    block.Band++;
                System.Diagnostics.Debug.Assert(block.Band == Math.Min(i - b, block.Band) && block.Band <= BandBlocks
                    && (i == b || block.Band >= 1), "a block after the head of its run has 1 to 15 blocks in its band");
                if (block.WantedScale <= 1) continue;
                block.Steps = RunFractions;
                ComputeFits(objects, block);
            }
            blocks[b].RigidFits = RigidFits(objects, blocks[b].From, blocks[end].To);
            b = end;
        }
    }

    /// <summary>Per angle, where the head of object <paramref name="from"/> may go for the objects from it to
    /// <paramref name="to"/> to fit as one rigid piece, unzoomed.</summary>
    private static Box[] RigidFits(IReadOnlyList<MapObject> objects, int from, int to)
    {
        var piece = new Block { From = from, To = to, A = new Vector2[to - from + 1], B = new Vector2[to - from + 1] };
        for (int j = from; j <= to; j++)
            piece.A[j - from] = objects[j].Start - objects[from].Start;
        ComputeFits(objects, piece);
        return piece.Fits;
    }

    /// <summary>
    /// Every stream gap between two blocks (neither a spinner nor a restart) joins them in a run (Kakudai 2 spec §13:
    /// with blocks of a beat, a run cut at a block of one object would let the stream jump there). The gap chains the
    /// block to the previous one when it is a gap of the stream: at most <see cref="ChainRatio"/> times the local median
    /// of its stream (<see cref="StreamMedians"/>), so that a gap that is not longer than its neighbours is zoomed
    /// like them; a longer one (a jump inside the stream) leaves the block free to move, in the band of the run.
    /// </summary>
    private static void Chain(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        List<Block> blocks)
    {
        var medians = StreamMedians(objects, beats);
        int runs = 0;
        for (int b = 1; b < blocks.Count; b++)
        {
            var (previous, block) = (blocks[b - 1], blocks[b]);
            int f = block.From;
            if (block.Spinner || previous.Spinner || block.Restart
                || !IsStreamStep(objects, beats, f))
                continue;
            float length = block.Jump.Length();
            if (medians[f] > 0 && length <= ChainRatio * medians[f])
            {
                block.Chained = true;
                double wanted = Math.Max(previous.WantedScale, block.WantedScale);
                block.ChainScale = !spaceStreams || length < StackPx ? 1
                    : wanted > 1 ? wanted
                    : Grow(length, k) / length;
                previous.NextChained = block;
            }
            if (previous.Run < 0)
            {
                previous.Run = runs++;
                previous.RunHead = true;
            }
            block.Run = previous.Run;
        }
    }

    /// <summary>How many gaps on each side the local median of a stream gap is taken over, as the stream cuts are
    /// measured (Kakudai 2 spec §13).</summary>
    private const int LocalGaps = 8;

    /// <summary>
    /// For the move into every object of a stream (consecutive stream gaps, spinners excluded; moves shorter than 1 px
    /// left out) that has at least two moves, the local median: that of the lengths of the <see cref="LocalGaps"/>
    /// moves on each side of it and its own (for a move left out, those of the next move). Else 0.
    /// </summary>
    private static double[] StreamMedians(IReadOnlyList<MapObject> objects, double[] beats)
    {
        var medians = new double[objects.Count];
        var moves = new List<(int Index, double Length)>();
        var window = new List<double>(2 * LocalGaps + 1);
        int start = -1;

        void Close(int end)
        {
            if (moves.Count >= 2)
            {
                int next = 0;
                for (int i = start; i < end; i++)
                {
                    while (next < moves.Count - 1 && moves[next].Index < i) next++;
                    window.Clear();
                    for (int m = Math.Max(0, next - LocalGaps); m <= Math.Min(moves.Count - 1, next + LocalGaps); m++)
                        window.Add(moves[m].Length);
                    window.Sort();
                    int c = window.Count;
                    medians[i] = c % 2 == 1 ? window[c / 2] : (window[c / 2 - 1] + window[c / 2]) / 2;
                }
            }
            moves.Clear();
            start = -1;
        }

        for (int i = 1; i < objects.Count; i++)
        {
            if (objects[i].Kind == ObjectKind.Spinner || objects[i - 1].Kind == ObjectKind.Spinner
                || !IsStreamStep(objects, beats, i))
            {
                Close(i);
                continue;
            }
            if (start < 0) start = i;
            float d = Vector2.Distance(objects[i].Start, objects[i - 1].End);
            if (d >= 1) moves.Add((i, d));
        }
        Close(objects.Count);
        return medians;
    }

    /// <summary>The map around a part amplified on its own (<see cref="Part"/>): every object, the beat length at
    /// each, the index of the first object of the part, the objects whose turns get no accent (or null), and the two
    /// layouts the accents are decided on (reused from a round to the next).</summary>
    private readonly record struct Around(IReadOnlyList<MapObject> Map, double[] Beats, int Offset, bool[]? Dropped,
        Layout Plain, Layout Accented);

    /// <summary>
    /// Accented angles (Kakudai 2 spec §5.2): at every object, the extra turn δ aimed at, 0 where the turn is not between
    /// two jumps (<see cref="IsJump"/>) or is inside a stream (a block of a run). τ being the turn from the move into the
    /// object to the move out of it, δ = ±A·|sin τ|, made smaller when needed so as not to land objects on each other:
    /// the accents are decided in order on the map zoomed move by move (each move m by g(|m|) / |m|) and accented so far
    /// (each move and the body after it turned by the accents before it); δ, then δ/2, is kept only if the next object
    /// does not come closer than <see cref="AccentGuardPx"/> to an object of the last 2 beats, nor an object of the next 2
    /// beats left in place after the part to it (or than without the accents, when that is closer). The objects of the
    /// map around the part (<paramref name="around"/>) are on their original spots, so that with only the kiai the
    /// objects left in place just before and after a kiai section count; those marked dropped get no accent.
    /// </summary>
    private static double[] Accents(IReadOnlyList<MapObject> objects, double[] beats, double k, bool spaceStreams,
        List<Block> blocks, Around around)
    {
        int n = objects.Count, offset = around.Offset;
        var accents = new double[n];
        var frozen = new bool[n];
        foreach (var block in blocks)
            if (block.Run >= 0)
                for (int i = block.From + 1; i < block.To; i++) frozen[i] = true;

        // Indexed in the map; an object a restart starts from stays on its original spot.
        var (plain, accented) = (around.Plain, around.Accented);
        plain.Reset(around.Map);
        accented.Reset(around.Map);
        double rotation = 0; // of the move into the next object and of its body, in the accented map
        for (int i = 0; i + 1 < n; i++)
        {
            var o = objects[i + 1];
            if (o.Kind == ObjectKind.Spinner || objects[i].Kind == ObjectKind.Spinner
                || o.StartTime - objects[i].EndTime >= RestartBeats * beats[i + 1])
            {
                rotation = 0; // a restart: back on the map
                continue;
            }

            var move = o.Start - objects[i].End;
            float length = move.Length();
            if (Zoomed(objects, beats, spaceStreams, i + 1, length))
                move *= (float)(Grow(length, k) / length);
            plain.Follow(around.Map, offset + i + 1, move, 0);

            if (!frozen[i] && i > 0 && !(around.Dropped != null && around.Dropped[offset + i])
                && IsJump(objects, beats, i) && IsJump(objects, beats, i + 1))
            {
                double turn = Angle(objects[i].Start - objects[i - 1].End, o.Start - objects[i].End);
                double wanted = Math.Sign(turn) * AccentRadians * Math.Abs(Math.Sin(turn));
                foreach (double share in (ReadOnlySpan<double>)[1, 0.5])
                {
                    double turned = rotation + share * wanted;
                    var head = accented.Ends[offset + i] + Rotate(move, turned);
                    var end = head + Rotate(o.End - o.Start, turned);
                    if (!HasRoom(around, plain, accented, offset + i + 1, head, end, offset + n - 1)) continue;
                    accents[i] = share * wanted;
                    break;
                }
            }
            rotation += accents[i];
            accented.Follow(around.Map, offset + i + 1, move, rotation);
        }
        return accents;
    }

    /// <summary>Where the heads and ends of the objects of a map go, for <see cref="Accents"/>: after
    /// <see cref="Reset"/>, on their original spots.</summary>
    private sealed class Layout
    {
        public readonly Vector2[] Heads, Ends;

        public Layout(IReadOnlyList<MapObject> objects)
            => (Heads, Ends) = (new Vector2[objects.Count], new Vector2[objects.Count]);

        /// <summary>Every object back on its original spot.</summary>
        public void Reset(IReadOnlyList<MapObject> objects)
        {
            for (int i = 0; i < objects.Count; i++)
                (Heads[i], Ends[i]) = (objects[i].Start, objects[i].End);
        }

        /// <summary>Object <paramref name="i"/> after the previous one, by <paramref name="move"/>, both the move and its
        /// body turned by <paramref name="rotation"/>.</summary>
        public void Follow(IReadOnlyList<MapObject> objects, int i, Vector2 move, double rotation)
        {
            Heads[i] = Ends[i - 1] + Rotate(move, rotation);
            Ends[i] = Heads[i] + Rotate(objects[i].End - objects[i].Start, rotation);
        }
    }

    /// <summary>
    /// Whether object <paramref name="i"/> of the map may go to <paramref name="head"/> (its end to
    /// <paramref name="end"/>) in the accented map: no closer than <see cref="AccentGuardPx"/>, or than it is in the map
    /// not accented, to the head or the end of an object of the last 2 beats before the previous one; nor, after the
    /// last object of the part <paramref name="last"/>, the head of an object of the next 2 beats left in place to it.
    /// </summary>
    private static bool HasRoom(Around around, Layout plain, Layout accented, int i, Vector2 head, Vector2 end,
        int last)
    {
        var (objects, beats) = (around.Map, around.Beats);
        var o = objects[i];
        for (int j = i - 2; j >= 0 && j >= i - 12; j--)
        {
            if (objects[j].Kind == ObjectKind.Spinner || o.StartTime - objects[j].StartTime > 2 * beats[i]) break;
            float now = Math.Min(Vector2.Distance(head, accented.Heads[j]), Vector2.Distance(head, accented.Ends[j]));
            float before = Math.Min(Vector2.Distance(plain.Heads[i], plain.Heads[j]),
                Vector2.Distance(plain.Heads[i], plain.Ends[j]));
            if (now < Math.Min(before, AccentGuardPx)) return false;
        }
        for (int j = i + 1; j < objects.Count && j <= i + 12; j++)
        {
            if (objects[j].Kind == ObjectKind.Spinner || objects[j].StartTime - o.StartTime > 2 * beats[j]) break;
            if (j < i + 2 || j <= last) continue;
            var later = objects[j].Start;
            float now = Math.Min(Vector2.Distance(later, head), Vector2.Distance(later, end));
            float before = Math.Min(Vector2.Distance(later, plain.Heads[i]), Vector2.Distance(later, plain.Ends[i]));
            if (now < Math.Min(before, AccentGuardPx)) return false;
        }
        return true;
    }

    /// <summary>Whether the move into object <paramref name="j"/> is a jump: at least <see cref="AccentMovePx"/> long,
    /// neither a stream gap nor a restart, no spinner at either end.</summary>
    private static bool IsJump(IReadOnlyList<MapObject> objects, double[] beats, int j)
    {
        if (objects[j].Kind == ObjectKind.Spinner || objects[j - 1].Kind == ObjectKind.Spinner) return false;
        double gap = objects[j].StartTime - objects[j - 1].EndTime;
        return Vector2.Distance(objects[j].Start, objects[j - 1].End) >= AccentMovePx
               && !IsStreamStep(objects, beats, j) && gap < RestartBeats * beats[j];
    }

    /// <summary>
    /// Accented angles (Kakudai 2 spec §5.2), given the extra turn of every object (<see cref="Accents"/>):
    /// <list type="bullet">
    /// <item>inside a block, each turn is made sharper by turning what follows it in the block (bodies and moves). A
    /// block whose sharper shape fits nowhere keeps its own, and so do the blocks of the runs: streams are not
    /// touched;</item>
    /// <item>around the entry jump of a block, the two turning angles the flow cost aims at are sharper alike, and the
    /// jump is aimed turned the other way in the frame of the block;</item>
    /// <item>the orientation cost counts from the orientation all these turns give the block, and the angles around the
    /// orientation of the previous block are tried around the rotation they expect from it.</item>
    /// </list>
    /// </summary>
    private static void AccentBlocks(IReadOnlyList<MapObject> objects, double[] beats, bool spaceStreams,
        List<Block> blocks, double[] accents)
    {
        const double step = 2 * Math.PI / AngleSteps;
        double sum = 0, previous = 0; // sum: the accents of the objects before next, since the last restart
        bool touched = false;         // one of them is not 0
        int next = 0;
        foreach (var block in blocks)
        {
            if (block.Spinner)
            {
                (sum, previous, next, touched) = (0, 0, block.To + 1, false);
                continue;
            }
            if (block.Restart) (sum, previous, next, touched) = (0, 0, block.From, false);
            for (; next < block.From; next++)
            {
                sum += accents[next];
                touched |= accents[next] != 0;
            }

            // The frame of a block is that of its first move; a single object is turned like the jump into it.
            bool inner = block.To > block.From;
            double first = inner ? accents[block.From] : 0;
            double reference = sum + first;

            if (!block.Restart && block.From > 0)
            {
                block.TurnIn += accents[block.From - 1];
                block.TurnOut += first;
                if (first != 0) block.JumpUnit = Rotate(block.JumpUnit, Math.Cos(-first), Math.Sin(-first));
                block.Shift = (int)Math.Round(Wrap(reference - previous) / step);
                if (touched || first != 0) // where the accents changed nothing, the search is the one without them
                {
                    block.CheckBack = Far(objects, beats, block.From, block.From - 2);
                    block.CheckSecond = inner && Far(objects, beats, block.From + 1, block.From - 1);
                }
            }
            if (reference != 0)
            {
                block.Orientation = new double[AngleSteps];
                for (int t = 0; t < AngleSteps; t++)
                {
                    double a = Wrap(Angles[t] - reference);
                    block.Orientation[t] = OrientationWeight * (a / Math.PI) * (a / Math.PI);
                }
            }
            previous = reference;

            if (inner) Sharpen(objects, beats, spaceStreams, block, accents);
        }
    }

    /// <summary>Turns the objects of a block that follow a jump turn, to make it sharper; keeps its shape, and drops
    /// the accents of its turns when it is in a run (a stream), or when it fits nowhere so.</summary>
    private static void Sharpen(IReadOnlyList<MapObject> objects, double[] beats, bool spaceStreams, Block block,
        double[] accents)
    {
        int from = block.From, to = block.To;
        var turned = new double[to - from + 1];
        turned[0] = -accents[from]; // the first body is turned like the jump into it
        bool any = turned[0] != 0 && objects[from].Kind == ObjectKind.Slider;
        for (int jj = 2; jj < turned.Length; jj++)
        {
            turned[jj] = turned[jj - 1] + accents[from + jj - 1];
            any |= turned[jj] != 0;
        }

        if (any && block.Run < 0)
        {
            var kept = (block.A, block.B, block.EndA, block.EndB, block.LastA, block.LastB, block.Fits, block.FitsAt,
                block.ScaleCosts);
            block.Turned = turned;
            Shape(objects, beats, spaceStreams, block);
            ComputeFits(objects, block);
            if (AnyFit(block)) return;
            block.Turned = null;
            (block.A, block.B, block.EndA, block.EndB, block.LastA, block.LastB, block.Fits, block.FitsAt,
                block.ScaleCosts) = kept;
        }
        for (int i = from + 1; i < to; i++) accents[i] = 0;
    }

    /// <summary>Beam search over the blocks (spec step 6); returns the chosen state of every block.</summary>
    private static State[] Search(IReadOnlyList<MapObject> objects, List<Block> blocks)
        => Search(objects, blocks, null, 0, blocks.Count, null, out _);

    /// <summary>
    /// Beam search over the blocks from <paramref name="keep"/> to before <paramref name="stop"/>, after the state
    /// <paramref name="kept"/> holds for the block before (from scratch without it); the other blocks keep the states
    /// it holds. <paramref name="stop"/> is a restart (or the end), whose candidates are the same whatever the state
    /// before it, but for their cost. With <paramref name="same"/> (the blocks the kept states still fit), the search
    /// also stops at a block before one of those where it finds again the very state kept for it, and the kept states
    /// after it are taken as they are: a path that follows from that state, whose places Finish checks again, though
    /// not always the one a fresh search would pick (their costs changed with the accents). <paramref name="end"/>:
    /// the block after the last one searched.
    /// </summary>
    private static State[] Search(IReadOnlyList<MapObject> objects, List<Block> blocks, State[]? kept, int keep,
        int stop, bool[]? same, out int end)
    {
        var layers = new List<State[]>(stop - keep);
        var beam = keep == 0
            ? new List<State> { new() { Exit = new Vector2(256, 192), Parent = -1, Scale = 1 } }
            : new List<State> { kept![keep - 1] };
        var single = new[] { new Candidates() };
        var targets = new int[RunFractions.Length][];
        var groups = new Candidates[RigidGroup + 1];
        for (int g = 0; g < groups.Length; g++) groups[g] = new Candidates();

        int index = 0; // in the last layer, the state to go back from: the best one
        for (int b = keep; b < stop; b++)
        {
            // A run is searched with one beam per fraction of its current block (the zoom moving smoothly within the
            // band of the run), plus the beam of the run laid down rigid and unzoomed, and that of the run left on its
            // original spot: the best of them from its first block to its last wins.
            var block = blocks[b];
            var output = block.Run >= 0 ? groups : single;
            foreach (var candidates in output) candidates.Clear();
            for (int si = 0; si < beam.Count; si++)
            {
                if (block.Chained) ExpandChained(objects, block, beam[si], si, output);
                else Expand(objects, block, beam[si], si, output, targets);
            }
            if (output.Length == 1)
                beam = output[0].Best();
            else
            {
                beam = output.SelectMany(c => c.Best()).ToList();
                if (b + 1 == blocks.Count || blocks[b + 1].Run != block.Run)
                {
                    // The run is over: its fraction no longer matters, the best states go on, whatever their group.
                    single[0].Clear();
                    foreach (var st in beam) single[0].Add(st);
                    beam = single[0].Best();
                }
            }
            layers.Add(beam.ToArray());

            if (same == null || b + 1 >= stop || !same[b + 1]) continue;
            var old = kept![b];
            int found = beam.FindIndex(st => SamePlace(st, old));
            if (found >= 0) (index, stop) = (found, b + 1);
        }

        var chosen = new State[blocks.Count];
        if (kept != null) Array.Copy(kept, chosen, blocks.Count);
        for (int b = stop - 1; b >= keep; b--)
        {
            chosen[b] = layers[b - keep][index];
            index = chosen[b].Parent;
        }
        end = stop;
        return chosen;
    }

    /// <summary>Whether two states place their block alike and pass on the same to the next one.</summary>
    private static bool SamePlace(in State a, in State b)
        => a.Choice == b.Choice && a.Head == b.Head && a.Fraction == b.Fraction && a.Pinned == b.Pinned
           && a.Angle == b.Angle && a.Exit == b.Exit && a.Move == b.Move && a.HasMove == b.HasMove
           && a.Flow == b.Flow && a.HasFlow == b.HasFlow
           && a.Group == b.Group && a.Recent == b.Recent;

    /// <summary>Every candidate placement of <paramref name="block"/> after the state <paramref name="st"/>.</summary>
    /// <param name="output">One beam; in a run, one beam per fraction (index in <see cref="RunFractions"/>), the beam
    /// of the run left on its original spot (<see cref="PinnedGroup"/>) and that of the run laid down as one rigid
    /// piece (<see cref="RigidGroup"/>).</param>
    /// <param name="targets">Room for the beams of each fraction (<see cref="Targets"/>), reused from a call to the
    /// next.</param>
    private static void Expand(IReadOnlyList<MapObject> objects, Block block, in State st, int si, Candidates[] output,
        int[][] targets)
    {
        if (block.Spinner)
        {
            var spinner = objects[block.From];
            output[0].Add(new State
            {
                Angle = st.Angle, Exit = spinner.End, Cost = st.Cost, Parent = si, Choice = st.Angle, Scale = 1,
                Head = spinner.Start, LastHead = spinner.Start,
            });
            return;
        }

        var original = objects[block.From].Start;
        double moveAngle = st.HasFlow ? Math.Atan2(st.Flow.Y, st.Flow.X) : 0;

        // After the first block of a run, a block entered by a jump inside the stream follows its run.
        if (block.Run >= 0 && !block.RunHead)
        {
            if (st.Group == PinnedGroup)
            {
                output[PinnedGroup].Add(LeftInPlace(block, st, si, original, st.Cost + PinnedCost, isShort: false));
                return;
            }
            if (st.Group == RigidGroup)
            {
                ContinueRigid(block, st, si, output);
                return;
            }
            if (block.Pinned) return; // the run cannot go on moved
        }

        if (block.Pinned || block.RunHead)
        {
            // On its original spot: a block that fits nowhere, or the last resort of a whole run.
            double cost = st.Cost + PinnedCost + (block.Restart ? 0 : JumpCost(block, st, moveAngle, original, 0));
            bool isShort = !block.Restart && Vector2.Distance(original, st.Exit) < 0.9 * block.WantedJump;
            var still = LeftInPlace(block, st, si, original, cost, isShort);
            output[block.RunHead ? PinnedGroup : 0].Add(still);
            if (block.Pinned) return;
        }

        int fractions = block.Fractions;
        for (int f = 0; f < fractions; f++)
            targets[f] = Targets(output, block, st, f);

        bool fits = false;
        for (int pass = 0; pass < 2 && !fits; pass++)
        for (int i = 0; i < AngleSteps; i++)
        {
            // From the expected orientation outwards (0, −1, 1… 36 steps): the cheapest first, so that the beams fill early
            // and prune more.
            int turn = (i + 1) / 2;
            int t = ((st.Angle + block.Shift + ((i & 1) == 0 ? turn : -turn)) % AngleSteps + 2 * AngleSteps) % AngleSteps;
            if (pass == 0 && !block.Restart && turn * 360.0 / AngleSteps > NearDegrees && t % FarAngleStep != 0)
                continue;
            bool here = false;
            for (int f = 0; f < fractions && !here; f++)
                here = targets[f].Length > 0 && !block.Fits[t * fractions + f].IsEmpty;
            if (!here) continue;
            fits = true; // "if nothing fits" is decided on the boxes alone, before any pruning

            // Every cost below is ≥ 0: a candidate that starts above the bound can never be kept.
            double orientation = block.Orientation[t] + RelativeCost(block, st, t);
            double reach = 0; // the bound of the beams this state can reach
            for (int f = 0; f < fractions; f++)
                if (targets[f].Length > 0) reach = Math.Max(reach, BoundOf(output, targets[f]));
            if (st.Cost + orientation >= reach) continue;

            for (int f = 0; f < fractions; f++)
            {
                var box = block.Fits[t * fractions + f];
                if (box.IsEmpty || targets[f].Length == 0) continue;
                double baseCost = st.Cost + orientation + block.ScaleCosts[f];
                if (baseCost >= BoundOf(output, targets[f])) continue;

                AddCandidates(block, st, moveAngle, si, t, f, box, baseCost, output, targets[f], original);

                // At the start of a chain, also where the next chained blocks fit unbent: room for the stream.
                if (block.NextChained == null) continue;
                foreach (int g in targets[f])
                {
                    var room = RoomForChain(block, t, f, g, box);
                    if (!room.IsEmpty && room != box)
                        AddCandidates(block, st, moveAngle, si, t, f, room, baseCost, output, SingleBeam[g], original);
                }
            }
        }

        // Before leaving a run on its original spot: the whole run as one rigid piece, unzoomed.
        if (!block.RunHead) return;
        int[] rigid = [RigidGroup];
        int none = fractions - 1; // the fraction 0: s = 1
        for (int t = 0; t < AngleSteps; t++)
        {
            var box = block.RigidFits[t];
            if (box.IsEmpty) continue;
            double baseCost = st.Cost + block.Orientation[t] + block.ScaleCosts[none];
            if (baseCost >= output[RigidGroup].Bound) continue;
            AddCandidates(block, st, moveAngle, si, t, none, box, baseCost, output, rigid, original);
        }
    }

    /// <summary>
    /// The next block of a run laid down as one rigid piece: unzoomed, turned like the previous one, its entry gap the
    /// original one turned alike (its first block fitted the whole run). A block that fits nowhere cannot be part of a
    /// run that fits whole, so that never happens; were it to, the rigid run stops there (<see cref="Next"/> would
    /// leave the block on its original spot and cut the stream), and only the run left in place goes on.
    /// </summary>
    private static void ContinueRigid(Block block, in State st, int si, Candidates[] output)
    {
        if (block.Pinned) return;
        int t = st.Angle, none = block.Fractions - 1;
        var head = st.Exit + Rotate(block.Jump, Cos[t], Sin[t]);
        var next = Next(block, st, si, t, none, head, st.Cost + block.ScaleCosts[none], moved: false, isShort: false);
        next.Group = RigidGroup;
        output[RigidGroup].Add(next);
    }

    /// <summary>The zoom of fraction <paramref name="c"/> (index in <see cref="RunFractions"/>) of the run of
    /// <paramref name="block"/>.</summary>
    private static double RunZoom(Block block, int c) => 1 + RunFractions[c] * (block.RunScale - 1);

    /// <summary>
    /// Whether a block of a run may take fraction <paramref name="c"/> after a block of fraction
    /// <paramref name="previous"/>, the blocks of its band (<see cref="Band"/>) having used the fractions
    /// <paramref name="top"/> to <paramref name="bottom"/>: within <see cref="NeighbourRatio"/> of the previous zoom,
    /// and the zooms of the band within <see cref="BandRatio"/>.
    /// </summary>
    private static bool Allowed(Block block, int previous, int top, int bottom, int c)
    {
        if (block.RunScale <= 1) return c == previous; // nothing zoomed: every fraction gives the same run
        if (Math.Abs(c - previous) > 1) return false;
        double zoom = RunZoom(block, c), before = RunZoom(block, previous);
        if (Math.Max(zoom, before) > NeighbourRatio * Math.Min(zoom, before) + 1e-9) return false;
        return RunZoom(block, Math.Min(top, c)) < BandRatio * RunZoom(block, Math.Max(bottom, c));
    }

    /// <summary>The smallest and largest fractions (indices in <see cref="RunFractions"/>) of the blocks of the run
    /// that the band of <paramref name="block"/> holds over (<see cref="Block.Band"/>), from the fractions of the last
    /// blocks <paramref name="recent"/>.</summary>
    private static (int Top, int Bottom) Band(Block block, ulong recent)
    {
        int top = int.MaxValue, bottom = int.MinValue;
        for (int i = 0; i < block.Band; i++, recent >>= 4)
        {
            int g = (int)(recent & 15);
            top = Math.Min(top, g);
            bottom = Math.Max(bottom, g);
        }
        return (top, bottom);
    }

    /// <summary>The fractions of the last blocks of a run, after a block of fraction <paramref name="c"/>.</summary>
    private static ulong Push(ulong recent, int c) => (recent << 4) | (uint)c;

    private static readonly int[] OnlyBeam = [0];
    private static readonly int[] NoBeam = [];
    private static readonly int[][] SingleBeam = [.. Enumerable.Range(0, PinnedGroup).Select(c => new[] { c })];

    /// <summary>
    /// The beams a candidate of fraction <paramref name="f"/> of <paramref name="block"/> after <paramref name="st"/>
    /// goes to: the only one outside runs; in a run, the fraction it takes (any, for the first block; one allowed
    /// after the previous block, for the others), every one when the block itself has nothing to zoom.
    /// </summary>
    private static int[] Targets(Candidates[] output, Block block, in State st, int f)
    {
        if (output.Length == 1 || block.RunScale <= 1) return OnlyBeam;
        var (top, bottom) = block.RunHead ? (0, 0) : Band(block, st.Recent);
        if (block.Fractions > 1) // the fraction it takes
            return block.RunHead || Allowed(block, st.Group, top, bottom, f) ? SingleBeam[f] : NoBeam;
        var targets = new List<int>(PinnedGroup);
        for (int c = 0; c < PinnedGroup; c++)
            if (block.RunHead || Allowed(block, st.Group, top, bottom, c)) targets.Add(c);
        return [.. targets];
    }

    /// <summary>The candidates of a block turned by <paramref name="t"/> and zoomed by fraction <paramref name="f"/>
    /// with its head in <paramref name="box"/> (spec step 5): for a restart, its original point brought into the box;
    /// else the wanted point brought into the box, the length closest to gJ on the ray of the jump, the point closest
    /// in angle to the jump on the circle of radius gJ.</summary>
    private static void AddCandidates(Block block, in State st, double moveAngle, int si, int t, int f, Box box,
        double baseCost, Candidates[] output, int[] targets, Vector2 original)
    {
        if (block.Restart)
        {
            var h = box.Clamp(original);
            double away = Vector2.Distance(h, original) / 100;
            double cost = baseCost + RestartMoveWeight * away * away;
            Offer(output, block, st, targets, Next(block, st, si, t, f, h, cost, moved: h != original, isShort: false));
            return;
        }

        float jumpLength = block.Jump.Length();
        var direction = jumpLength > 0 ? Rotate(block.JumpUnit, Cos[t], Sin[t]) : Vector2.Zero;
        var wanted = st.Exit + (float)block.WantedJump * direction;

        // (1) The wanted point, brought into the box.
        var h1 = box.Clamp(wanted);
        AddJump(block, st, moveAngle, si, t, f, h1, baseCost, h1 != wanted, output, targets);
        if (h1 == wanted || jumpLength == 0) return;

        // (2) On the ray of the jump, the length closest to gJ inside the box.
        if (RayBox(st.Exit, direction, box, out double enter, out double leave) && leave >= 0)
        {
            double l = Math.Clamp(block.WantedJump, Math.Max(0, enter), leave);
            AddJump(block, st, moveAngle, si, t, f, st.Exit + (float)l * direction, baseCost, true, output, targets);
        }

        // (3) On the circle of radius gJ, the point closest in angle to the jump.
        if (CircleBox(st.Exit, (float)block.WantedJump, direction, box, out var h3))
            AddJump(block, st, moveAngle, si, t, f, h3, baseCost, true, output, targets);
    }

    /// <summary>Cosine and sine of every half angle step (2.5°): the direction of the entry gap of a chained block,
    /// turned by half its bend.</summary>
    private static readonly double[] HalfCos = new double[2 * AngleSteps], HalfSin = new double[2 * AngleSteps];

    /// <summary>
    /// The cost RelativeWeight·(Δ/π)² of turning a block by <paramref name="t"/> after a block turned by the state's
    /// angle: Δ is how much the two patterns turn against each other (beyond the shift the accents ask for). Two sliders
    /// drawn parallel, or a pattern and its copy turned by some θ, keep that relation instead of each one turning its
    /// own way. None after a restart, nor in a run or right after one: a stream bends at its own pace
    /// (<see cref="MaxBendSteps"/>), and its last block is turned by those bends, not as a pattern.
    /// </summary>
    private static double RelativeCost(Block block, in State st, int t)
    {
        if (block.Restart || block.Run >= 0 || block.AfterRun) return 0;
        double a = Wrap(Angles[t] - Angles[st.Angle] - block.Shift * 2 * Math.PI / AngleSteps) / Math.PI;
        return RelativeWeight * a * a;
    }

    /// <summary>The cost BendWeight·(β/π)² of a bend of <paramref name="bend"/> angle steps.</summary>
    private static double BendCost(int bend)
    {
        double beta = bend * 2 * Math.PI / AngleSteps;
        return BendWeight * (beta / Math.PI) * (beta / Math.PI);
    }

    /// <summary>The bends of a chained block from the straight junction outwards (0, −1, 1, −2, 2…), whose costs
    /// only grow.</summary>
    private static int BendAt(int b) => (b & 1) == 0 ? b / 2 : -(b + 1) / 2;

    /// <summary>
    /// Every placement of a chained block after <paramref name="st"/> (Kakudai 2 spec §5.1): its entry gap is the
    /// original one zoomed like the stream at the fraction the block takes, and the block is turned from the previous
    /// one by a bend β of at most <see cref="MaxBendSteps"/>, its entry gap by β/2, so that each of the two turning
    /// angles around the gap changes by β/2. A run left on its original spot or laid down rigid stays so.
    /// </summary>
    private static void ExpandChained(IReadOnlyList<MapObject> objects, Block block, in State st, int si,
        Candidates[] output)
    {
        if (st.Group == PinnedGroup)
        {
            output[PinnedGroup].Add(LeftInPlace(block, st, si, objects[block.From].Start, st.Cost + PinnedCost,
                isShort: false));
            return;
        }
        if (st.Group == RigidGroup)
        {
            ContinueRigid(block, st, si, output);
            return;
        }
        if (block.Pinned) return; // the run cannot go on moved

        var (top, bottom) = Band(block, st.Recent);
        for (int c = 0; c < PinnedGroup; c++)
        {
            if (!Allowed(block, st.Group, top, bottom, c)) continue;
            int f = block.Fractions > 1 ? c : 0;
            ulong recent = Push(st.Recent, c);
            float scale = (float)(1 + RunFractions[c] * (block.ChainScale - 1));
            var gap = scale * block.Jump;
            bool isShort = scale < 0.9 * block.ChainScale;
            var candidates = output[c];
            double baseCost = st.Cost + block.ScaleCosts[f];
            for (int b = 0; b <= 2 * MaxBendSteps; b++)
            {
                int bend = BendAt(b);
                double cost = baseCost + BendCost(bend);
                if (cost >= candidates.Bound) break; // and so would every wider bend
                int t = (st.Angle + bend + AngleSteps) % AngleSteps;
                var box = block.Fits[t * block.Fractions + f];
                if (box.IsEmpty) continue;
                int half = (2 * st.Angle + bend + 2 * AngleSteps) % (2 * AngleSteps);
                var head = st.Exit + Rotate(gap, HalfCos[half], HalfSin[half]);
                if (!box.Contains(head)) continue;
                var next = Next(block, st, si, t, f, head, cost, moved: false, isShort);
                (next.Group, next.Recent) = (c, recent);
                if (CanContinue(block.NextChained, t, recent, next.Exit, LookAheadBlocks))
                    candidates.Add(next);
            }
        }
    }

    /// <summary>
    /// Whether the chained blocks after a block of a run (the fractions of the last blocks <paramref name="recent"/>,
    /// its own lowest), turned by <paramref name="angle"/> and ending at <paramref name="exit"/>, can follow it, bending
    /// and changing fraction as allowed, for <paramref name="depth"/> blocks: a state that cannot is never kept, so that
    /// the beam keeps states that can.
    /// </summary>
    private static bool CanContinue(Block? next, int angle, ulong recent, Vector2 exit, int depth)
    {
        if (next == null || depth == 0) return true;
        if (next.Pinned) return false;
        int group = (int)(recent & 15);
        var (top, bottom) = Band(next, recent);
        for (int i = 0; i < 2; i++)
        {
            int c = group + i; // the same fraction, or a little less zoom
            if (c >= PinnedGroup || !Allowed(next, group, top, bottom, c)) continue;
            var reach = Reach(next, c, angle);
            if (!reach[^1].Contains(exit)) continue;
            for (int b = 0; b <= 2 * MaxBendSteps; b++)
            {
                if (!reach[b].Contains(exit)) continue;
                if (depth == 1) return true;
                int bend = BendAt(b), t = (angle + bend + AngleSteps) % AngleSteps;
                int half = (2 * angle + bend + 2 * AngleSteps) % (2 * AngleSteps), f = next.Fractions > 1 ? c : 0;
                float scale = (float)(1 + RunFractions[c] * (next.ChainScale - 1));
                float inner = (float)(1 + next.Steps[f] * (next.WantedScale - 1));
                var head = exit + Rotate(scale * next.Jump, HalfCos[half], HalfSin[half]);
                var end = head + Rotate(next.EndA + inner * next.EndB, Cos[t], Sin[t]);
                if (CanContinue(next.NextChained, t, Push(recent, c), end, depth - 1))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Where the exit of the block before <paramref name="block"/>, chained to it and turned by
    /// <paramref name="angle"/>, may be for <paramref name="block"/> to fit at run fraction <paramref name="c"/>, bent
    /// by each bend in turn (<see cref="BendAt"/>): its boxes moved back by its entry gap (shrunk by a thousandth of a
    /// pixel for the rounding), then the box around them all. Worked out once, kept in the block.
    /// </summary>
    private static Box[] Reach(Block block, int c, int angle)
    {
        var byAngle = (block.Reach ??= new Box[]?[]?[PinnedGroup])[c] ??= new Box[]?[AngleSteps];
        if (byAngle[angle] is { } known) return known;

        int f = block.Fractions > 1 ? c : 0;
        var gap = (float)(1 + RunFractions[c] * (block.ChainScale - 1)) * block.Jump;
        var boxes = new Box[2 * MaxBendSteps + 2];
        var all = Box.Empty;
        for (int b = 0; b <= 2 * MaxBendSteps; b++)
        {
            int bend = BendAt(b), t = (angle + bend + AngleSteps) % AngleSteps;
            var box = block.Fits[t * block.Fractions + f];
            int half = (2 * angle + bend + 2 * AngleSteps) % (2 * AngleSteps);
            var w = Rotate(gap, HalfCos[half], HalfSin[half]);
            boxes[b] = box.IsEmpty ? Box.Empty
                : new Box(box.MinX - w.X, box.MinY - w.Y, box.MaxX - w.X, box.MaxY - w.Y).Shrink(1e-3f);
            if (!boxes[b].IsEmpty) all = all.Include(boxes[b].Min).Include(boxes[b].Max);
        }
        boxes[^1] = all;
        return byAngle[angle] = boxes;
    }

    /// <summary>
    /// The heads in <paramref name="box"/> (the block turned by <paramref name="t"/>, zoomed by fraction
    /// <paramref name="f"/>) from which the next <see cref="LookAheadBlocks"/> chained blocks, at the run fraction
    /// <paramref name="group"/>, fit unbent: the box shrunk by each of their boxes, moved back by where their heads
    /// fall from this one. Stops at the last block for which it is not empty.
    /// </summary>
    private static Box RoomForChain(Block block, int t, int f, int group, Box box)
    {
        double c = Cos[t], s = Sin[t];
        var room = box;
        var current = block;
        float scale = (float)(1 + current.Steps[f] * (current.WantedScale - 1));
        var offset = Vector2.Zero; // from this head to the head of the current block
        for (int depth = 0; depth < LookAheadBlocks && current.NextChained is Block next && !next.Pinned; depth++)
        {
            float chain = (float)(1 + RunFractions[group] * (next.ChainScale - 1));
            offset += Rotate(current.EndA + scale * current.EndB + chain * next.Jump, c, s);
            int nf = next.Fractions > 1 ? group : 0;
            var other = next.Fits[t * next.Fractions + nf];
            var shrunk = new Box(Math.Max(room.MinX, other.MinX - offset.X), Math.Max(room.MinY, other.MinY - offset.Y),
                Math.Min(room.MaxX, other.MaxX - offset.X), Math.Min(room.MaxY, other.MaxY - offset.Y));
            if (shrunk.IsEmpty) break;
            room = shrunk;
            current = next;
            scale = (float)(1 + next.Steps[nf] * (next.WantedScale - 1));
        }
        return room;
    }

    /// <summary>
    /// Keeps a candidate in the beams <paramref name="targets"/> (in a run: the fraction it takes, or rigid), after
    /// the state <paramref name="from"/>; in a run, only if the chained blocks after it can follow.
    /// </summary>
    private static void Offer(Candidates[] output, Block block, in State from, int[] targets, State st)
    {
        if (output.Length == 1)
        {
            output[0].Add(st);
            return;
        }
        foreach (int g in targets)
        {
            if (st.Cost >= output[g].Bound) continue; // it would not be kept: no need to look ahead
            st.Group = g;
            if (g < PinnedGroup)
            {
                st.Recent = block.RunHead ? (ulong)g : Push(from.Recent, g);
                if (!CanContinue(block.NextChained, st.Angle, st.Recent, st.Exit, LookAheadBlocks))
                    continue;
            }
            output[g].Add(st);
        }
    }

    /// <summary>A candidate costing this much is kept by none of the beams <paramref name="targets"/>.</summary>
    private static double BoundOf(Candidates[] output, int[] targets)
    {
        if (output.Length == 1) return output[0].Bound;
        double max = 0;
        foreach (int g in targets) max = Math.Max(max, output[g].Bound);
        return max;
    }

    /// <summary>
    /// A jump inside a stream (the entry jump of a block of a run, not chained): RunJumpWeight·e² when it is zoomed
    /// less than the block before it, by more than NeighbourRatio (e: how much shorter, relative), so that it is not
    /// shrunk against the notes around it: next to a dense stream, such a jump is the gap a stream cut is measured
    /// against (Kakudai 2 spec §13).
    /// </summary>
    private static double RunJumpCost(Block block, in State st, Vector2 head)
    {
        double original = block.Jump.Length();
        double target = Math.Min(block.WantedJump, st.Scale * original) / NeighbourRatio;
        double length = Vector2.Distance(head, st.Exit);
        if (length >= target || target <= 0) return 0;
        double e = (target - length) / target;
        return RunJumpWeight * e * e;
    }

    private static void AddJump(Block block, in State st, double moveAngle, int si, int t, int f, Vector2 h,
        double baseCost, bool moved, Candidates[] output, int[] targets)
    {
        // Every cost is ≥ 0: the cheap ones first, the others only for a candidate still under the bound.
        double bound = BoundOf(output, targets);
        double cost = baseCost + LengthCost(block, st, h);
        if (block.Run >= 0 && !block.RunHead) cost += RunJumpCost(block, st, h);
        if (cost >= bound) return;
        cost += TurnCost(block, st, moveAngle, h, Angles[t]);
        if (block.CheckBack || block.CheckSecond) cost += CrowdCost(block, st, t, f, h);
        if (cost >= bound) return;
        bool isShort = Vector2.Distance(h, st.Exit) < 0.9 * block.WantedJump;
        Offer(output, block, st, targets, Next(block, st, si, t, f, h, cost, moved, isShort));
    }

    /// <summary>
    /// With accented angles: <see cref="CrowdWeight"/> for each of the first two objects of the block that the jump to
    /// <paramref name="head"/> (block turned by <paramref name="t"/>, zoomed by fraction <paramref name="f"/>) lands on
    /// an object of the last 2 beats it was far from: the first one on the end of the object two before it, the second
    /// one on the last object of the previous block.
    /// </summary>
    private static double CrowdCost(Block block, in State st, int t, int f, Vector2 head)
    {
        double cost = 0;
        if (block.CheckBack && st.HasMove && Vector2.Distance(head, st.LastHead - st.Move) < CrowdPx)
            cost += CrowdWeight;
        if (block.CheckSecond)
        {
            float scale = (float)(1 + block.Steps[f] * (block.WantedScale - 1));
            var second = head + Rotate(block.A[1] + scale * block.B[1], Cos[t], Sin[t]);
            if (Math.Min(Vector2.Distance(second, st.Exit), Vector2.Distance(second, st.LastHead)) < CrowdPx)
                cost += CrowdWeight;
        }
        return cost;
    }

    /// <summary>Whether objects <paramref name="i"/> and <paramref name="j"/> (before it) are less than 2 beats apart
    /// and the head of <paramref name="i"/> at least <see cref="FarPx"/> from the head and the end of
    /// <paramref name="j"/>, with no spinner from one to the other.</summary>
    private static bool Far(IReadOnlyList<MapObject> objects, double[] beats, int i, int j)
    {
        if (j < 0 || objects[i].StartTime - objects[j].StartTime > 2 * beats[i]) return false;
        for (int x = j; x <= i; x++)
            if (objects[x].Kind == ObjectKind.Spinner) return false;
        return Math.Min(Vector2.Distance(objects[i].Start, objects[j].Start), Vector2.Distance(objects[i].Start, objects[j].End))
               >= FarPx;
    }

    /// <summary>The block on its original spot (θ = 0, s = 1), in the beam of the runs left so when in a run.</summary>
    private static State LeftInPlace(Block block, in State st, int si, Vector2 original, double cost, bool isShort)
    {
        var still = Next(block, st, si, 0, 0, original, cost, moved: false, isShort, pinned: true);
        still.Group = PinnedGroup;
        return still;
    }

    private static State Next(Block block, in State st, int si, int t, int f, Vector2 head, double cost, bool moved,
        bool isShort, bool pinned = false)
    {
        pinned |= block.Pinned;
        double c = Cos[t], s = Sin[t];
        float scale = pinned ? 1 : (float)(1 + block.Steps[f] * (block.WantedScale - 1));
        var exit = head + Rotate(block.EndA + scale * block.EndB, c, s);
        Vector2 move;
        bool hasMove = true;
        if (block.HasLast) move = Rotate(block.LastA + scale * block.LastB, c, s);
        else if (!block.Restart) move = head - st.Exit;
        else
        {
            move = Vector2.Zero;
            hasMove = false;
        }

        hasMove &= move.LengthSquared() > 1;
        var flow = block.HasExitDir ? Rotate(block.ExitDir, c, s) : move;
        return new State
        {
            Angle = pinned ? 0 : t, Exit = exit, Move = move, HasMove = hasMove,
            Flow = flow, HasFlow = block.HasExitDir || hasMove,
            Cost = cost, Parent = si, Choice = t, Fraction = f, Scale = scale, Head = head, Moved = moved,
            Short = isShort, Pinned = pinned, LastHead = head + Rotate(block.A[^1] + scale * block.B[^1], c, s),
        };
    }

    /// <summary>
    /// The length and flow costs of the jump from the state's exit to <paramref name="head"/>, the block turned by
    /// <paramref name="angle"/> (<see cref="LengthCost"/> and <see cref="TurnCost"/>).
    /// </summary>
    private static double JumpCost(Block block, in State st, double moveAngle, Vector2 head, double angle)
        => LengthCost(block, st, head) + TurnCost(block, st, moveAngle, head, angle);

    /// <summary>The length cost of the jump from the state's exit to <paramref name="head"/>: its length against gJ
    /// and |J|.</summary>
    private static double LengthCost(Block block, in State st, Vector2 head)
    {
        double l = Vector2.Distance(head, st.Exit), jl = block.Jump.Length(), g = block.WantedJump;
        double cost = 0;
        double importance = Math.Min(1, jl / JumpImportancePx);
        if (g > 0)
        {
            double e = (l - g) / g;
            cost += (l < g ? ShortJumpWeight : LongJumpWeight) * importance * e * e;
            if (l < jl)
            {
                double shrunk = (jl - l) / jl;
                cost += ShrunkJumpWeight * importance * shrunk * shrunk;
            }
        }
        return cost;
    }

    /// <summary>The flow cost of the jump from the state's exit to <paramref name="head"/>, the block turned by
    /// <paramref name="angle"/>: the change of the two turning angles around the jump (from the previous move into it,
    /// then from it into the first move of the block, whose direction the zoom does not change).</summary>
    private static double TurnCost(Block block, in State st, double moveAngle, Vector2 head, double angle)
    {
        var v = head - st.Exit;
        if (v.Length() <= 1 || block.Jump.Length() <= 1) return 0;
        double cost = 0;
        double jumpAngle = Math.Atan2(v.Y, v.X);
        bool pattern = block.Run < 0; // in a stream, a slider turn weighs like the others
        if (st.HasFlow && block.HasMoveBefore)
        {
            double d = Wrap(jumpAngle - moveAngle - block.TurnIn) / Math.PI;
            cost += block.TurnInWeight * (pattern && block.SliderTurnIn ? SliderTurnFactor : 1) * d * d;
        }
        if (block.HasTurnOut)
        {
            double d = Wrap(block.FirstMoveAngle + angle - jumpAngle - block.TurnOut) / Math.PI;
            cost += block.TurnOutWeight * (pattern && block.SliderTurnOut ? SliderTurnFactor : 1) * d * d;
        }
        return cost;
    }

    private static double TurnImportance(double length) => Math.Min(1, length / TurnImportancePx);

    /// <summary>States with the same key are duplicates: same orientation, exits in the same small cell.</summary>
    private static (int, int, int) Key(in State st)
        => (st.Angle, (int)MathF.Floor(st.Exit.X / DedupPx), (int)MathF.Floor(st.Exit.Y / DedupPx));

    /// <summary>Writes the placements of the objects of <paramref name="block"/> for the chosen state.</summary>
    private static void Place(IReadOnlyList<MapObject> objects, Block block, State st, Placement[] placements)
    {
        var head = objects[block.From].Start;
        if (st.Choice == 0 && st.Scale == 1 && block.Turned == null)
        {
            // A translation: exact, so that a block left where it was keeps its original positions to the bit.
            var delta = st.Head - head;
            for (int i = block.From; i <= block.To; i++)
                placements[i] = new Placement(objects[i].Start + delta, 0);
            return;
        }

        double c = Cos[st.Choice], s = Sin[st.Choice];
        float angle = (float)Angles[st.Choice];
        for (int i = block.From; i <= block.To; i++)
        {
            int j = i - block.From;
            var rotation = block.Turned == null ? angle : (float)Wrap(Angles[st.Choice] + block.Turned[j]);
            placements[i] = new Placement(st.Head + Rotate(block.A[j] + (float)st.Scale * block.B[j], c, s), rotation);
        }
    }

    /// <summary>Every object of the block is allowed where it lands (the v1 rule, spec step 7).</summary>
    private static bool BlockValid(IReadOnlyList<MapObject> objects, Block block, Placement[] placements)
    {
        for (int i = block.From; i <= block.To; i++)
            if (!Valid(objects[i], placements[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Valid when the head lies in <see cref="HeadArea"/> or on its original point, and every other path point lies
    /// in <see cref="BodyArea"/> or in the object's original bounding box (an object that already went past the edge
    /// may keep doing so, no further).
    /// </summary>
    private static bool Valid(MapObject o, Placement p)
    {
        if (!HeadArea.Contains(p.Start) && p.Start != o.Start)
            return false;

        var samples = o.PathSamples;
        if (samples.Count == 0)
            return true;

        float c = MathF.Cos(p.Rotation), s = MathF.Sin(p.Rotation);
        var original = Box.Around(o);
        for (int j = 0; j < samples.Count; j++)
        {
            if (samples[j] == Vector2.Zero)
                continue; // the head itself, checked above
            var q = p.Start + new Vector2(c * samples[j].X - s * samples[j].Y, s * samples[j].X + c * samples[j].Y);
            if (!BodyArea.Contains(q) && !original.Contains(q))
                return false;
        }
        return true;
    }

    /// <summary>Mean move length (≥ 1 px) between consecutive objects of a segment, before and after.</summary>
    private static (double Before, double After) AverageJumps(IReadOnlyList<MapObject> objects, Placement[] placements)
    {
        double before = 0, after = 0;
        int jumps = 0;
        for (int i = 1; i < objects.Count; i++)
        {
            var (o, p) = (objects[i], objects[i - 1]);
            if (o.Kind == ObjectKind.Spinner || p.Kind == ObjectKind.Spinner)
                continue;
            float jump = Vector2.Distance(o.Start, p.End);
            if (jump < 1)
                continue;
            var q = placements[i - 1];
            var newEnd = q.Start + Rotate(p.End - p.Start, MathF.Cos(q.Rotation), MathF.Sin(q.Rotation));
            before += jump;
            after += Vector2.Distance(placements[i].Start, newEnd);
            jumps++;
        }
        return jumps == 0 ? (0, 0) : (before / jumps, after / jumps);
    }

    private static Vector2 Rotate(Vector2 v, double c, double s)
        => new((float)(c * v.X - s * v.Y), (float)(s * v.X + c * v.Y));

    private static Vector2 Rotate(Vector2 v, double angle) => Rotate(v, Math.Cos(angle), Math.Sin(angle));

    /// <summary>Signed angle from <paramref name="a"/> to <paramref name="b"/>, in (−π, π].</summary>
    private static double Angle(Vector2 a, Vector2 b)
        => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);

    private static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }

    /// <summary>The parameters [enter, leave] where the ray o + l·d is inside the box.</summary>
    private static bool RayBox(Vector2 o, Vector2 d, Box box, out double enter, out double leave)
    {
        enter = double.NegativeInfinity;
        leave = double.PositiveInfinity;
        return Slab(o.X, d.X, box.MinX, box.MaxX, ref enter, ref leave)
               && Slab(o.Y, d.Y, box.MinY, box.MaxY, ref enter, ref leave)
               && enter <= leave;
    }

    private static bool Slab(float o, float d, float lo, float hi, ref double enter, ref double leave)
    {
        if (Math.Abs(d) < 1e-9) return o >= lo && o <= hi;
        double t1 = (lo - o) / d, t2 = (hi - o) / d;
        if (t1 > t2) (t1, t2) = (t2, t1);
        enter = Math.Max(enter, t1);
        leave = Math.Min(leave, t2);
        return true;
    }

    /// <summary>The point of the circle (centre <paramref name="c"/>, radius <paramref name="r"/>) on the edge of the
    /// box closest in angle to <paramref name="direction"/>.</summary>
    private static bool CircleBox(Vector2 c, float r, Vector2 direction, Box box, out Vector2 best)
    {
        Vector2 bestPoint = default;
        double bestCos = double.NegativeInfinity;
        bool found = false;

        void Try(Vector2 p)
        {
            if (p.X < box.MinX - 1e-3 || p.X > box.MaxX + 1e-3 || p.Y < box.MinY - 1e-3 || p.Y > box.MaxY + 1e-3)
                return;
            p = box.Clamp(p);
            var u = p - c;
            double cos = Vector2.Dot(direction, u) / Math.Max(u.Length(), 1e-6f); // closest in angle: largest cosine
            if (cos > bestCos)
            {
                bestCos = cos;
                bestPoint = p;
                found = true;
            }
        }

        void OnVertical(float x)
        {
            double dx = x - c.X, q = (double)r * r - dx * dx;
            if (q < 0) return;
            double root = Math.Sqrt(q);
            Try(new Vector2(x, (float)(c.Y + root)));
            Try(new Vector2(x, (float)(c.Y - root)));
        }

        void OnHorizontal(float y)
        {
            double dy = y - c.Y, q = (double)r * r - dy * dy;
            if (q < 0) return;
            double root = Math.Sqrt(q);
            Try(new Vector2((float)(c.X + root), y));
            Try(new Vector2((float)(c.X - root), y));
        }

        OnVertical(box.MinX);
        OnVertical(box.MaxX);
        OnHorizontal(box.MinY);
        OnHorizontal(box.MaxY);
        best = bestPoint;
        return found;
    }

    private readonly record struct Box(float MinX, float MinY, float MaxX, float MaxY)
    {
        public static readonly Box Empty = new(float.PositiveInfinity, float.PositiveInfinity,
            float.NegativeInfinity, float.NegativeInfinity);

        public bool IsEmpty => MinX > MaxX || MinY > MaxY;

        public Vector2 Min => new(MinX, MinY);
        public Vector2 Max => new(MaxX, MaxY);

        /// <summary>Bounding box of the object where it was: its head and every path sample.</summary>
        public static Box Around(MapObject o)
        {
            var box = Empty.Include(o.Start);
            for (int j = 0; j < o.PathSamples.Count; j++)
                box = box.Include(o.Start + o.PathSamples[j]);
            return box;
        }

        public Box Include(Vector2 p)
            => new(MathF.Min(MinX, p.X), MathF.Min(MinY, p.Y), MathF.Max(MaxX, p.X), MathF.Max(MaxY, p.Y));

        public Box Shrink(float by) => new(MinX + by, MinY + by, MaxX - by, MaxY - by);

        public bool Contains(Vector2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        public Vector2 Clamp(Vector2 p) => new(Math.Clamp(p.X, MinX, MaxX), Math.Clamp(p.Y, MinY, MaxY));
    }
}
