using System.Diagnostics;
using System.Numerics;
using Kakudai.Core.Amplify;
using Kakudai.Core.Maps;
using Kakudai.Core.Osu;
using NUnit.Framework;

namespace Kakudai.Tests;

public class AmplifierTests
{
    private static readonly Func<double, double> Beat500 = _ => 500;

    private static MapObject Circle(float x, float y, double time)
        => new(0, ObjectKind.Circle, time, time, new Vector2(x, y), new Vector2(x, y), []);

    private static MapObject Spinner(double start, double end)
        => new(0, ObjectKind.Spinner, start, end, new Vector2(256, 192), new Vector2(256, 192), []);

    /// <summary>Straight slider from its head to head + <paramref name="offset"/>, sampled like MapLoader (33 points).</summary>
    private static MapObject StraightSlider(float x, float y, double start, double end, Vector2 offset)
        => new(0, ObjectKind.Slider, start, end, new Vector2(x, y), new Vector2(x, y) + offset,
            Enumerable.Range(0, 33).Select(i => offset * (i / 32f)).ToArray());

    /// <summary>Half circle of radius <paramref name="r"/> from its head to head + (2r, 0), bulging down by r.</summary>
    private static MapObject HalfCircleSlider(float x, float y, double start, float r)
    {
        var samples = Enumerable.Range(0, 33)
            .Select(i => new Vector2(r - r * MathF.Cos(MathF.PI * i / 32), r * MathF.Sin(MathF.PI * i / 32)))
            .ToArray();
        return new(0, ObjectKind.Slider, start, start + 200, new Vector2(x, y), new Vector2(x, y) + samples[32],
            samples);
    }

    private static AmplifyResult Amplify(double factor, params MapObject[] objects)
        => Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, SpaceStreams: true));

    private static bool InHeadArea(Vector2 p) => p.X is >= 1 and <= 511 && p.Y is >= 1 and <= 383;

    private static bool InBodyArea(Vector2 p) => p.X is >= 2 and <= 510 && p.Y is >= 2 and <= 382;

    /// <summary>R(θ)·v, the rotation OsuPatcher applies to a slider path around its head.</summary>
    private static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(c * v.X - s * v.Y, s * v.X + c * v.Y);
    }

    /// <summary>The path points of <paramref name="o"/> where <paramref name="p"/> puts them.</summary>
    private static IEnumerable<Vector2> PlacedPath(MapObject o, Placement p)
        => o.PathSamples.Select(s => p.Start + Rotate(s, p.Rotation));

    /// <summary>
    /// The new points are a similarity (uniform zoom, rotation and translation) of the old ones, within
    /// <paramref name="tolerance"/>: the best similarity is fitted on all the points, then every pairwise vector must
    /// match its image by that similarity, give or take that share of its length. Returns the zoom.
    /// </summary>
    private static double AssertSimilar(IReadOnlyList<Vector2> before, IReadOnlyList<Vector2> after, double tolerance)
    {
        var mp = before.Aggregate(Vector2.Zero, (a, b) => a + b) / before.Count;
        var mq = after.Aggregate(Vector2.Zero, (a, b) => a + b) / after.Count;
        double pp = 0, re = 0, im = 0;
        for (int i = 0; i < before.Count; i++)
        {
            var p = before[i] - mp;
            var q = after[i] - mq;
            pp += p.LengthSquared();
            re += p.X * q.X + p.Y * q.Y;
            im += p.X * q.Y - p.Y * q.X;
        }
        double ar = re / pp, ai = im / pp; // after ≈ (ar + i·ai)·before
        double zoom = Math.Sqrt(ar * ar + ai * ai);

        Assert.Multiple(() =>
        {
            for (int a = 0; a < before.Count; a++)
            for (int b = a + 1; b < before.Count; b++)
            {
                var old = before[b] - before[a];
                if (old.Length() < 1) continue;
                var now = after[b] - after[a];
                var image = new Vector2((float)(ar * old.X - ai * old.Y), (float)(ai * old.X + ar * old.Y));
                double error = Vector2.Distance(now, image) / (zoom * old.Length());
                Assert.That(error, Is.LessThanOrEqualTo(tolerance),
                    $"paire {a}-{b} : {old} -> {now} (zoom {zoom:0.000}, rotation {Math.Atan2(ai, ar):0.000} rad)");
            }
        });
        return zoom;
    }

    [Test]
    public void Identity_AtFactor1()
    {
        var map = MapLoader.Load(File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "simple.osu")));

        foreach (bool spaceStreams in new[] { true, false })
        {
            var result = Amplifier.Amplify(map.Objects, map.BeatLengthAt, new AmplifyOptions(1, spaceStreams));

            Assert.Multiple(() =>
            {
                Assert.That(result.Placements, Is.EqualTo(map.Objects.Select(o => new Placement(o.Start, 0))));
                Assert.That(result.Corrected, Is.Zero);
                Assert.That(result.Reduced, Is.Zero);
                Assert.That(result.AverageJumpAfter, Is.EqualTo(result.AverageJumpBefore));
            });
        }
    }

    [Test]
    public void StreamNearBottom_KeepsItsCurve()
    {
        // 8 notes at 1/4, 30 px apart, along the bottom edge, curving up by 12 px in the middle.
        var stream = Enumerable.Range(0, 8)
            .Select(i => Circle(150 + 30 * i, 372 - MathF.Round(12 * (1 - MathF.Pow((i - 3.5f) / 3.5f, 2))), i * 125))
            .ToArray();

        var result = Amplify(2, stream);
        var layout = Layout.From(stream, result.Placements);
        var after = layout.Starts;
        TestContext.Out.WriteLine(string.Join(" ", after) + $" rotation {result.Placements[0].Rotation}");

        // Two blocks of a beat (Kakudai 2 spec §13): each one keeps its shape, and their junction may bend by 25° at
        // most per turning angle; every gap is zoomed alike.
        double zoom = AssertSimilar(stream[..5].Select(o => o.Start).ToArray(), after[..5], 0.03);
        AssertSimilar(stream[5..].Select(o => o.Start).ToArray(), after[5..], 0.03);
        var gaps = Gaps(layout);
        var (first, last) = (after[0], after[^1]);
        var normal = Vector2.Normalize(new Vector2(first.Y - last.Y, last.X - first.X));
        double bend = after.Max(p => MathF.Abs(Vector2.Dot(p - first, normal)));

        Assert.Multiple(() =>
        {
            Assert.That(after.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(zoom, Is.GreaterThan(1.5), "le stream doit être écarté");
            Assert.That(gaps.Max() / gaps.Min(), Is.LessThan(1.15), "écarts du stream");
            Assert.That(TurnChanges(stream, layout).Max(), Is.LessThanOrEqualTo(MaxJunctionTurnDegrees + 0.1), "virages");
            // The bend at the junction may flatten the curve a little (12 px in the original, 24 px zoomed rigid).
            Assert.That(bend, Is.GreaterThan(5), "le stream est devenu une ligne droite");
        });
    }

    [Test]
    public void WideSquare_KeepsShape_NoCornerToCorner()
    {
        Vector2[] square = [new(106, 42), new(406, 42), new(406, 342), new(106, 342)];
        var objects = square.Select((p, i) => Circle(p.X, p.Y, i * 250)).ToArray();

        var result = Amplify(2, objects);
        var after = result.Placements.Select(p => p.Start).ToArray();
        TestContext.Out.WriteLine(string.Join(" ", after) + $" Reduced={result.Reduced}");

        double zoom = AssertSimilar(square, after, 0.03);
        Assert.Multiple(() =>
        {
            Assert.That(after.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(zoom, Is.InRange(1.1, 1.3), "le carré grandit autant qu'il tient, pas plus");
            for (int i = 1; i < square.Length; i++)
                Assert.That(Vector2.Distance(after[i], after[i - 1]), Is.LessThan(400), $"saut {i} d'un coin à l'autre");
            Assert.That(result.Reduced, Is.EqualTo(4), "le carré ne tient pas au zoom souhaité");
        });
    }

    [Test]
    public void SmallJumps_ScaleAboutK()
    {
        // g(60) = 560·tanh(2·60/560) ≈ 118.2 px at k = 2.
        var objects = Enumerable.Range(0, 16)
            .Select(i => i % 2 == 0 ? Circle(226, 192, i * 250) : Circle(286, 192, i * 250))
            .ToArray();

        var result = Amplify(2, objects);

        Assert.Multiple(() =>
        {
            Assert.That(result.AverageJumpBefore, Is.EqualTo(60));
            Assert.That(result.AverageJumpAfter, Is.InRange(117, 119.5));
            for (int i = 1; i < objects.Length; i++)
                Assert.That(Vector2.Distance(result.Placements[i].Start, result.Placements[i - 1].Start),
                    Is.InRange(117, 119.5), $"saut {i}");
            Assert.That(result.Corrected, Is.Zero);
            Assert.That(result.Reduced, Is.Zero);
        });
    }

    [Test]
    public void BigJumps_FollowTheCurve_NotCornerToCorner()
    {
        // g(300) = 560·tanh(2·300/560) ≈ 442.6 px at k = 2: amplified, but capped well below ×2.
        var objects = Enumerable.Range(0, 12)
            .Select(i => i % 2 == 0 ? Circle(106, 192, i * 250) : Circle(406, 192, i * 250))
            .ToArray();

        var result = Amplify(2, objects);
        TestContext.Out.WriteLine(string.Join(" ", result.Placements.Select(p => p.Start)));

        Assert.Multiple(() =>
        {
            for (int i = 1; i < objects.Length; i++)
                Assert.That(Vector2.Distance(result.Placements[i].Start, result.Placements[i - 1].Start),
                    Is.InRange(440, 445), $"saut {i}");
            Assert.That(result.Placements.All(p => InHeadArea(p.Start)), Is.True);
            Assert.That(result.Reduced, Is.Zero);
        });
    }

    /// <summary>A jump, a 1/4 stream of 4 notes going right and wiggling, then a jump.</summary>
    private static MapObject[] JumpStreamJump() =>
    [
        Circle(100, 192, 0),
        Circle(200, 192, 500), Circle(220, 200, 625), Circle(240, 192, 750), Circle(260, 200, 875),
        Circle(360, 192, 1375),
    ];

    [Test]
    public void Streams_KeptWhenOptionOff()
    {
        var objects = JumpStreamJump();

        var kept = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(2, SpaceStreams: false));
        var after = kept.Placements.Select(p => p.Start).ToArray();
        TestContext.Out.WriteLine(string.Join(" ", after));

        Assert.Multiple(() =>
        {
            // The stream is moved as a rigid piece: same shape, same size (it may be turned).
            double zoom = AssertSimilar(objects[1..5].Select(o => o.Start).ToArray(), after[1..5], 0.001);
            Assert.That(zoom, Is.EqualTo(1).Within(0.001), "le stream ne doit pas être écarté");
            Assert.That(after.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(Vector2.Distance(after[1], after[0]), Is.GreaterThan(150), "le saut vers le stream doit grandir");
        });
    }

    [Test]
    public void Streams_KeptWhenOptionOff_TailStaysInside()
    {
        // A long stream running to the right edge: the whole stream must still fit, rigid.
        MapObject[] objects =
        [
            Circle(256, 192, 0),
            .. Enumerable.Range(0, 12).Select(i => Circle(290 + 20 * i, 192, 500 + i * 125)),
        ];

        var kept = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(3, SpaceStreams: false));
        var after = kept.Placements.Select(p => p.Start).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(after.All(InHeadArea), Is.True, string.Join(" ", after));
            for (int i = 2; i < objects.Length; i++)
                Assert.That(Vector2.Distance(after[i], after[i - 1]), Is.EqualTo(20).Within(0.01), $"note {i}");
            AssertSimilar(objects[1..].Select(o => o.Start).ToArray(), after[1..], 0.001);
        });
    }

    [Test]
    public void Streams_ScaledWhenOptionOn()
    {
        var objects = JumpStreamJump();

        var spaced = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(2, SpaceStreams: true));
        var after = spaced.Placements.Select(p => p.Start).ToArray();
        TestContext.Out.WriteLine(string.Join(" ", after));

        Assert.Multiple(() =>
        {
            for (int i = 2; i <= 4; i++)
            {
                double growth = Vector2.Distance(after[i], after[i - 1]) / Vector2.Distance(objects[i].Start, objects[i - 1].Start);
                Assert.That(growth, Is.InRange(1.7, 2.05), $"note {i} du stream");
            }
            Assert.That(after.All(InHeadArea), Is.True, "tête hors du playfield");
        });
    }

    private static readonly Func<double, double> Beat180 = _ => 60000.0 / 180;

    /// <summary>The bound of the spec Kakudai 2 §5.1 and §13: at a junction of a stream, a turning angle changes by at
    /// most this (half the largest bend, 50°, between two blocks of a stream).</summary>
    private const double MaxJunctionTurnDegrees = 25;

    /// <summary>A stream of circles at 1/4, 180 BPM, through <paramref name="points"/>.</summary>
    private static MapObject[] Stream180(IEnumerable<Vector2> points)
        => points.Select((p, i) => Circle(p.X, p.Y, i * 60000.0 / 180 / 4)).ToArray();

    /// <summary>The lengths of the moves of <paramref name="layout"/>, from each end to the next head.</summary>
    private static double[] Gaps(Layout layout)
        => Enumerable.Range(1, layout.Starts.Length - 1)
            .Select(i => (double)Vector2.Distance(layout.Starts[i], layout.Ends[i - 1])).ToArray();

    /// <summary>For every two consecutive moves, how much their turning angle changed, in degrees.</summary>
    private static double[] TurnChanges(IReadOnlyList<MapObject> objects, Layout layout)
        => Enumerable.Range(2, objects.Count - 2).Select(i =>
        {
            static double Turn(Vector2 a, Vector2 b) => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
            double before = Turn(objects[i - 1].Start - objects[i - 2].End, objects[i].Start - objects[i - 1].End);
            double after = Turn(layout.Starts[i - 1] - layout.Ends[i - 2], layout.Starts[i] - layout.Ends[i - 1]);
            return Math.Abs(Math.IEEERemainder(after - before, 2 * Math.PI)) * 180 / Math.PI;
        }).ToArray();

    /// <summary>
    /// 48 notes at 1/4, 180 BPM, k = 2: a straight line, 8 px apart along the diagonal (376 px, 752 px at k = 2: too
    /// long to stay straight; v3 folded it at the junctions of its blocks), or a zigzag going right, 9 px forward and
    /// 60 px across (moves of 61 px; v3 cut it).
    /// </summary>
    [TestCase("droit")]
    [TestCase("zigzag")]
    public void LongStream_IsNeverCut(string shape)
    {
        var stream = Stream180(Enumerable.Range(0, 48).Select(i => shape == "droit"
            ? new Vector2(60, 60) + 8 * i * new Vector2(0.8f, 0.6f)
            : new Vector2(40 + 9 * i, i % 2 == 0 ? 162 : 222)));
        double spacing = Vector2.Distance(stream[1].Start, stream[0].Start);

        var result = Amplifier.Amplify(stream, Beat180, new AmplifyOptions(2, SpaceStreams: true));
        var layout = Layout.From(stream, result.Placements);
        var gaps = Gaps(layout);
        double median = gaps.Order().ElementAt(gaps.Length / 2);
        TestContext.Out.WriteLine($"écart médian {median:0.0} px, de {gaps.Min():0.0} à {gaps.Max():0.0} px");

        Assert.Multiple(() =>
        {
            Assert.That(ShapeMetrics.StreamCuts(stream, Beat180, layout), Is.Empty, "coupures de stream");
            for (int i = 0; i < gaps.Length; i++)
                Assert.That(gaps[i], Is.InRange(0.85 * median, 1.15 * median), $"écart {i + 1}");
            Assert.That(layout.Starts.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(median, Is.GreaterThan(1.2 * spacing), "le stream doit être écarté");
        });
    }

    [Test]
    public void LongCurvedStreamNearWall_BendsInsteadOfCutting()
    {
        // 40 notes on an arc of radius 150, 5° (13 px) apart, ending at (256, 364): 20 px from the bottom.
        var stream = Stream180(Enumerable.Range(0, 40).Select(i =>
        {
            double a = (90 - 5 * (39 - i)) * Math.PI / 180;
            return new Vector2((float)(256 + 150 * Math.Cos(a)), (float)(214 + 150 * Math.Sin(a)));
        }));
        Assume.That(stream[^1].Start.Y, Is.EqualTo(364).Within(0.01));

        var result = Amplifier.Amplify(stream, Beat180, new AmplifyOptions(3, SpaceStreams: true));
        var layout = Layout.From(stream, result.Placements);
        var turns = TurnChanges(stream, layout);
        var gaps = Gaps(layout);
        double spacing = Vector2.Distance(stream[1].Start, stream[0].Start);
        double median = gaps.Order().ElementAt(gaps.Length / 2);
        TestContext.Out.WriteLine($"écarts de {gaps.Min():0.0} à {gaps.Max():0.0} px, virages changés jusqu'à {turns.Max():0.0}°");

        Assert.Multiple(() =>
        {
            Assert.That(ShapeMetrics.StreamCuts(stream, Beat180, layout), Is.Empty, "coupures de stream");
            Assert.That(layout.Starts.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(median, Is.GreaterThan(1.2 * spacing), "le stream doit être écarté, pas laissé à sa place");
            for (int i = 0; i < turns.Length; i++)
                Assert.That(turns[i], Is.LessThanOrEqualTo(MaxJunctionTurnDegrees + 0.1), $"virage {i + 1}");
        });
    }

    /// <summary>A deathstream: 320 notes at 1/4 (80 beats, 180 BPM) on loops of radius 130 around the centre, 40 px
    /// apart, their centre drifting slowly from left to right and back. Zoomed as one rigid piece, it would not fit at
    /// any zoom; the stream has to bend along the way (Kakudai 2 spec §13).</summary>
    private static MapObject[] Deathstream()
        => Stream180(Enumerable.Range(0, 320).Select(i =>
        {
            double a = i * 40.0 / 130, drift = 60 * Math.Sin(i * Math.PI / 160);
            return new Vector2((float)(256 + drift + 130 * Math.Cos(a)), (float)(192 + 130 * Math.Sin(a)));
        }));

    [Test]
    public void Deathstream_IsSpacedAllAlongWithoutCuts()
    {
        var stream = Deathstream();
        Assume.That(stream.All(o => InHeadArea(o.Start)), Is.True);

        var result = Amplifier.Amplify(stream, Beat180, new AmplifyOptions(2, SpaceStreams: true));
        var layout = Layout.From(stream, result.Placements);
        var zoom = Gaps(layout).Select((g, i) => g / Vector2.Distance(stream[i + 1].Start, stream[i].Start)).Order().ToArray();
        var (all, inStream) = ShapeMetrics.TurnErrors(stream, Beat180, layout);
        TestContext.Out.WriteLine($"zoom médian ×{zoom[zoom.Length / 2]:0.00} (de ×{zoom[0]:0.00} à ×{zoom[^1]:0.00}), "
                                  + $"virages faussés {inStream.Average():0.0}° en moyenne");

        Assert.Multiple(() =>
        {
            Assert.That(ShapeMetrics.StreamCuts(stream, Beat180, layout), Is.Empty, "coupures de stream (locales)");
            Assert.That(layout.Starts.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(zoom[zoom.Length / 2], Is.GreaterThanOrEqualTo(DeathstreamZoom), "zoom médian du stream");
            Assert.That(zoom[zoom.Length / 10], Is.GreaterThanOrEqualTo(1.6), "zoom du dixième le moins écarté");
        });
    }

    /// <summary>The median zoom the deathstream must reach at k = 2: s* ≈ 2 (lab A3: ×1.99; A2, blocks of 4 beats and a
    /// band over the whole run: ×1.39).</summary>
    private const double DeathstreamZoom = 1.8;

    /// <summary>Signed turning angle from move <paramref name="a"/> to move <paramref name="b"/>, in degrees.</summary>
    private static double TurnDegrees(Vector2 a, Vector2 b)
        => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y) * 180 / Math.PI;

    /// <summary>The turning angles of <paramref name="layout"/> at objects <paramref name="from"/> to
    /// <paramref name="to"/> (between the move into each and the move out of it), in degrees.</summary>
    private static double[] Turns(Layout layout, int from, int to)
        => Enumerable.Range(from, to - from + 1).Select(i => TurnDegrees(layout.Starts[i] - layout.Ends[i - 1],
            layout.Starts[i + 1] - layout.Ends[i])).ToArray();

    /// <summary>16 jumps at 1/2 (moves of 47 px), then a 1/4 stream of 16 notes going right in a zigzag
    /// (moves of 25 px): objects in kiai from <paramref name="kiaiFrom"/> to <paramref name="kiaiTo"/>.</summary>
    private static MapObject[] JumpsThenStream(int kiaiFrom, int kiaiTo)
    {
        var objects = new List<MapObject>();
        for (int i = 0; i < 16; i++)
            objects.Add(Circle(60 + 24 * i, i % 2 == 0 ? 120 : 160, i * 250));
        for (int i = 0; i < 16; i++)
            objects.Add(Circle(150 + 20 * i, i % 2 == 0 ? 280 : 295, 4250 + i * 125));
        return objects.Select((o, i) => o with { InKiai = i >= kiaiFrom && i <= kiaiTo }).ToArray();
    }

    [Test]
    public void KiaiOnly_LeavesObjectsOutsideKiaiExactlyInPlace()
    {
        // Kiai from jump 8 to the 8th note of the stream: the stream crosses the end of the kiai.
        var objects = JumpsThenStream(8, 23);

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(2, true, KiaiOnly: true));
        var layout = Layout.From(objects, result.Placements);
        var gaps = Gaps(layout);
        TestContext.Out.WriteLine(string.Join(" ", gaps.Select(g => g.ToString("0"))));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i].InKiai)
                    Assert.That(InHeadArea(result.Placements[i].Start), Is.True, $"objet {i} hors du playfield");
                else
                    Assert.That(result.Placements[i], Is.EqualTo(new Placement(objects[i].Start, 0)), $"objet {i}");
            }
            // The jumps inside the kiai (moves into objects 9 to 15) are amplified, about g(46) ≈ 92 px at k = 2.
            for (int i = 9; i <= 15; i++)
                Assert.That(gaps[i - 1], Is.GreaterThan(1.5 * Vector2.Distance(objects[i].Start, objects[i - 1].Start)),
                    $"saut {i}");
            // The part of the stream in kiai is spaced (moves into objects 17 to 23).
            for (int i = 17; i <= 23; i++)
                Assert.That(gaps[i - 1], Is.GreaterThan(1.3 * Vector2.Distance(objects[i].Start, objects[i - 1].Start)),
                    $"note {i}");
            // The kiai starts from a restart: its first object stays about where it was.
            Assert.That(Vector2.Distance(result.Placements[8].Start, objects[8].Start), Is.LessThan(1), "objet 8");
        });
    }

    [Test]
    public void KiaiOnly_WithAccentAngles_SharpensOnlyTheKiai()
    {
        // A zigzag of 16 jumps at 1/2, every turn 90° (moves of 42 px), kiai from object 4 to 11; k = 1.
        var objects = Enumerable.Range(0, 16)
            .Select(i => Circle(30 + 30 * i, i % 2 == 0 ? 172 : 202, i * 250) with { InKiai = i is >= 4 and <= 11 })
            .ToArray();
        Assume.That(Turns(Layout.From(objects), 1, objects.Length - 2).Select(Math.Abs), Is.All.EqualTo(90).Within(0.01));

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(1, true, AccentAngles: true, KiaiOnly: true));
        var after = Turns(Layout.From(objects, result.Placements), 5, 10);
        TestContext.Out.WriteLine(string.Join(" ", after.Select(t => t.ToString("0"))));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < objects.Length; i++)
                if (!objects[i].InKiai)
                    Assert.That(result.Placements[i], Is.EqualTo(new Placement(objects[i].Start, 0)), $"objet {i}");
                else
                    Assert.That(InHeadArea(result.Placements[i].Start), Is.True, $"objet {i} hors du playfield");
            Assert.That(after.Average(Math.Abs), Is.GreaterThanOrEqualTo(110), "virage moyen dans le kiai");
            Assert.That(ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects, result.Placements)), Is.Zero,
                "chevauchements");
        });
    }

    [TestCase(1.0)]
    [TestCase(2.0)]
    public void KiaiOnly_WithAccentAngles_AddsNoOverlap(double factor)
    {
        // Random jumps, kiai on two sections: the objects left in place just before and after a kiai section count
        // as much as the others (spec §5.2, §5.3).
        var objects = RandomJumps(3, 400).Select((o, i) => o with { InKiai = i is (>= 60 and < 200) or (>= 230 and < 330) })
            .ToArray();

        var plain = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true, KiaiOnly: true));
        var accented = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true, AccentAngles: true, KiaiOnly: true));
        int without = ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects, plain.Placements));
        int with = ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects, accented.Placements));
        var turns = (Plain: Turns(Layout.From(objects, plain.Placements), 61, 198).Average(Math.Abs),
            Accented: Turns(Layout.From(objects, accented.Placements), 61, 198).Average(Math.Abs));
        TestContext.Out.WriteLine($"chevauchements {without} -> {with}, virage moyen {turns.Plain:0.0}° -> {turns.Accented:0.0}°");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < objects.Length; i++)
                if (!objects[i].InKiai)
                    Assert.That(accented.Placements[i], Is.EqualTo(new Placement(objects[i].Start, 0)), $"objet {i}");
            Assert.That(with, Is.LessThanOrEqualTo(without), "chevauchements");
            Assert.That(turns.Accented, Is.GreaterThan(turns.Plain + 5), "les virages du kiai doivent rester accentués");
        });
    }

    [Test]
    public void AccentAngles_TurnsASliderWithTheMoveIntoIt()
    {
        // Jumps at 1/2 in a 90° zigzag (moves of 57 px) along the top edge, 12 px from it; object 2 is a slider whose
        // body goes 40 px right along the edge, and the zigzag goes on from its end. Inside its block, the slider is
        // turned with the move into it, which lifts its end by about 20 px: the block only fits if its extent is that
        // of the turned body (else it lands outside and goes back to its place, unturned).
        MapObject[] objects =
        [
            Circle(80, 12, 0), Circle(120, 52, 250), StraightSlider(160, 12, 500, 700, new Vector2(40, 0)),
            .. Enumerable.Range(0, 5).Select(i => Circle(240 + 40 * i, i % 2 == 0 ? 52 : 12, 950 + 250 * i)),
        ];

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(1, true, AccentAngles: true));
        var layout = Layout.From(objects, result.Placements);
        var (slider, placement) = (objects[2], result.Placements[2]);
        double bodyBefore = TurnDegrees(slider.Start - objects[1].End, slider.End - slider.Start);
        double bodyAfter = TurnDegrees(layout.Starts[2] - layout.Ends[1], layout.Ends[2] - layout.Starts[2]);
        TestContext.Out.WriteLine($"rotations {string.Join(" ", result.Placements.Select(p => p.Rotation.ToString("0.00")))}, "
                                  + $"virages {string.Join(" ", Turns(layout, 1, 6).Select(t => t.ToString("0")))}");

        Assert.Multiple(() =>
        {
            Assert.That(Turns(layout, 1, 6).Average(Math.Abs), Is.GreaterThan(110), "virages accentués");
            Assert.That(placement.Rotation, Is.Not.EqualTo(result.Placements[0].Rotation), "le slider est tourné dans le bloc");
            Assert.That(bodyAfter, Is.EqualTo(bodyBefore).Within(0.5), "le corps suit le mouvement qui y entre");
            foreach (var p in PlacedPath(slider, placement))
                Assert.That(InBodyArea(p), Is.True, $"{p} hors du playfield");
        });
    }

    [Test]
    public void SliderJumps_KeepTheirAnglesToTheBodies()
    {
        // Parallel sliders 80 px long, in pairs (gaps of 150 then 300 ms: a block of two, then a jump), on a zigzag:
        // each jump leaves the end of a body at about 97° from it. At ×3 the 160 px jumps want about 390 px, more than
        // the playfield is high: the zigzag has to turn. The jumps out of a slider must keep their angle to its body,
        // and the sliders stay nearly parallel, as a mapper drew them (a reviewer's report: "really loose with the
        // angles, especially with sliders").
        var objects = new MapObject[7];
        double time = 0;
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i] = StraightSlider(40 + 60 * i, i % 2 == 0 ? 110 : 270, time, time + 100, new Vector2(80, 0));
            time += 100 + (i % 2 == 0 ? 150 : 300);
        }

        var result = Amplify(3, objects);
        var layout = Layout.From(objects, result.Placements);
        var body = Rotate(new Vector2(80, 0), result.Placements[0].Rotation);
        var outTurns = Enumerable.Range(0, objects.Length - 1).Select(i => Math.Abs(
            TurnDegrees(layout.Ends[i] - layout.Starts[i], layout.Starts[i + 1] - layout.Ends[i])
            - TurnDegrees(objects[i].End - objects[i].Start, objects[i + 1].Start - objects[i].End))).ToArray();
        var parallel = result.Placements.Select(p => Math.Abs(TurnDegrees(body, Rotate(new Vector2(80, 0), p.Rotation))))
            .ToArray();
        TestContext.Out.WriteLine($"virages faussés {string.Join(" ", outTurns.Select(t => t.ToString("0")))}, "
                                  + $"écart au parallèle {string.Join(" ", parallel.Select(t => t.ToString("0")))}, "
                                  + $"sauts ×{result.AverageJumpAfter / result.AverageJumpBefore:0.00}");

        Assert.Multiple(() =>
        {
            Assert.That(result.AverageJumpAfter / result.AverageJumpBefore, Is.GreaterThan(1.3), "espacé");
            Assert.That(outTurns.Max(), Is.LessThanOrEqualTo(20), "angle entre corps et saut");
            Assert.That(parallel.Max(), Is.LessThanOrEqualTo(20), "sliders parallèles");
        });
    }

    [Test]
    public void BackToBackSliders_AreNotAStream()
    {
        // Short sliders every 1/3 beat, 1/6 from a tail to the next head, jumping 100 to 200 px (a reviewer's map,
        // Deadlocked [Zugzwang] 0:49–0:57). Timed from the tails they looked like a stream: one long run, there left
        // exactly in place because no stream zoom fitted its jumps. Back-to-back sliders are slider jumps: with the
        // streams crushed (their spacing kept), these jumps still grow; a slider into a circle at 1/6 stays a stream gap.
        Vector2[] heads = [new(100, 100), new(300, 80), new(200, 260), new(380, 300), new(120, 300), new(260, 140),
            new(420, 120), new(160, 180), new(330, 240), new(90, 220), new(280, 320), new(400, 200)];
        var sliders = heads.Select((h, i) => StraightSlider(h.X, h.Y, i * 500.0 / 3, i * 500.0 / 3 + 500.0 / 6,
            new Vector2(i % 2 == 0 ? 30 : -30, 20))).ToArray();
        MapObject[] intoCircle = [sliders[0], Circle(200, 200, 500.0 / 3)];

        var jumps = Amplifier.Amplify(sliders, Beat500, new AmplifyOptions(2, SpaceStreams: false));
        var gap = Amplifier.Amplify(intoCircle, Beat500, new AmplifyOptions(2, SpaceStreams: false));
        TestContext.Out.WriteLine($"sauts ×{jumps.AverageJumpAfter / jumps.AverageJumpBefore:0.00}");

        Assert.Multiple(() =>
        {
            Assert.That(jumps.AverageJumpAfter / jumps.AverageJumpBefore, Is.GreaterThan(1.3), "slider jumps espacés");
            Assert.That(gap.AverageJumpAfter, Is.EqualTo(gap.AverageJumpBefore).Within(0.01), "écart de stream gardé");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void KiaiOnly_IsIdentityOnMapWithoutKiai(bool accentAngles)
    {
        var objects = JumpsThenStream(-1, -1);

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(2, true, accentAngles, KiaiOnly: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Placements, Is.EqualTo(objects.Select(o => new Placement(o.Start, 0))));
            Assert.That(result.Corrected, Is.Zero);
            Assert.That(result.Reduced, Is.Zero);
            Assert.That(result.AverageJumpAfter, Is.EqualTo(result.AverageJumpBefore));
        });
    }

    [Test]
    public void AccentAngles_SharpensJumpTurns()
    {
        // A zigzag of jumps at 1/2, moves of 57 px, every turn 90°; k = 1: only the angles change.
        var objects = Enumerable.Range(0, 12).Select(i => Circle(36 + 40 * i, i % 2 == 0 ? 172 : 212, i * 250)).ToArray();
        var before = Turns(Layout.From(objects), 1, objects.Length - 2);
        Assume.That(before.Select(Math.Abs), Is.All.EqualTo(90).Within(0.01));

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(1, true, AccentAngles: true));
        var layout = Layout.From(objects, result.Placements);
        var after = Turns(layout, 1, objects.Length - 2);
        TestContext.Out.WriteLine(string.Join(" ", after.Select(t => t.ToString("0"))));

        Assert.Multiple(() =>
        {
            Assert.That(after.Average(Math.Abs), Is.GreaterThanOrEqualTo(110), "virage moyen");
            for (int i = 0; i < objects.Length; i++)
                Assert.That(InHeadArea(result.Placements[i].Start), Is.True, $"objet {i} en {result.Placements[i].Start}");
            var gaps = Gaps(layout);
            for (int i = 0; i < gaps.Length; i++)
                Assert.That(gaps[i], Is.EqualTo(Vector2.Distance(objects[i + 1].Start, objects[i].Start)).Within(0.5),
                    $"à k = 1, le saut {i + 1} garde sa longueur");
        });
    }

    [TestCase(1.0)]
    [TestCase(2.0)]
    public void AccentAngles_LeavesStreamsAlone(double factor)
    {
        // Jumps at 1/2 with 90° turns, then a 1/4 stream of 8 notes in a zigzag, then jumps again.
        MapObject[] objects =
        [
            .. Enumerable.Range(0, 6).Select(i => Circle(60 + 40 * i, i % 2 == 0 ? 80 : 120, i * 250)),
            .. Enumerable.Range(0, 8).Select(i => Circle(180 + 18 * i, i % 2 == 0 ? 220 : 236, 1750 + i * 125)),
            .. Enumerable.Range(0, 6).Select(i => Circle(140 + 40 * i, i % 2 == 0 ? 330 : 290, 3250 + i * 250)),
        ];
        var stream = Enumerable.Range(6, 8).ToArray();
        var original = Layout.From(objects);

        var plain = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true));
        var accented = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true, AccentAngles: true));
        var (p, a) = (Layout.From(objects, plain.Placements), Layout.From(objects, accented.Placements));

        Assert.Multiple(() =>
        {
            Assert.That(Turns(a, 1, 4).Average(Math.Abs), Is.GreaterThan(Turns(p, 1, 4).Average(Math.Abs) + 10),
                "les virages des sauts doivent être accentués");
            var (gapsP, gapsA) = (Gaps(p), Gaps(a));
            for (int i = stream[1]; i <= stream[^1]; i++)
                Assert.That(gapsA[i - 1], Is.EqualTo(gapsP[i - 1]).Within(0.05), $"écart {i} du stream");
            var (turnsO, turnsA) = (Turns(original, stream[1], stream[^2]), Turns(a, stream[1], stream[^2]));
            for (int i = 0; i < turnsO.Length; i++)
                Assert.That(turnsA[i], Is.EqualTo(turnsO[i]).Within(0.05), $"virage {stream[1] + i} du stream");
            Assert.That(a.Starts.All(InHeadArea), Is.True, "tête hors du playfield");
        });
    }


    /// <summary>Random jumps at 1/2 (moves of 60 to 140 px, turns anywhere, bounced off the edges): many turns to
    /// accent, and many ways to land an object on a recent one.</summary>
    private static MapObject[] RandomJumps(int seed, int count)
    {
        var rng = new Random(seed);
        var objects = new MapObject[count];
        var p = new Vector2(256, 192);
        double heading = 0;
        for (int i = 0; i < count; i++)
        {
            objects[i] = Circle(p.X, p.Y, i * 250);
            heading += (rng.NextDouble() * 2 - 1) * Math.PI * 0.9;
            float length = rng.Next(60, 141);
            var next = p + length * new Vector2((float)Math.Cos(heading), (float)Math.Sin(heading));
            if (next.X is < 20 or > 492) heading = Math.PI - heading;
            if (next.Y is < 20 or > 364) heading = -heading;
            p += length * new Vector2((float)Math.Cos(heading), (float)Math.Sin(heading));
            p = Vector2.Clamp(p, new Vector2(20, 20), new Vector2(492, 364));
        }
        return objects;
    }

    [TestCase(245f, 165f, false)] // where the accent would put object 5: the guard drops it
    [TestCase(450f, 300f, true)]  // far away: the full accent
    public void KiaiOnly_AccentGuardSeesTheObjectsLeftInPlaceAfterTheKiai(float x, float y, bool accented)
    {
        // A 90° zigzag at 1/2 (moves of 57 px) in kiai from object 0 to 5, then objects 6 and 7 left in place, within 2
        // beats of object 5. The accents so far (−30°, +30°, −30° at objects 1 to 3) turn the path: accenting the turn
        // at object 4 (+30°) would put object 5 at (249.3, 160.7), 6 px from object 7 at (245, 165) (half of it at
        // (258.3, 149.0), 21 px), and only the guard looking past the end of the kiai can see it. The accents are those
        // of the guard itself, before any block is placed or checked.
        var objects = Enumerable.Range(0, 6)
            .Select(i => Circle(100 + 40 * i, i % 2 == 0 ? 150 : 190, i * 250) with { InKiai = true })
            .Append(Circle(400, 300, 1500)).Append(Circle(x, y, 1750)).ToArray();

        var accents = Amplifier.GuardedAccents(objects, Beat500, new AmplifyOptions(1, true, AccentAngles: true, KiaiOnly: true));
        TestContext.Out.WriteLine(string.Join(" ", accents.Select(a => (a * 180 / Math.PI).ToString("0"))));

        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(accents[3]) * 180 / Math.PI, Is.EqualTo(30).Within(0.01),
                "l'accent d'avant n'est pas concerné");
            Assert.That(Math.Abs(accents[4]) * 180 / Math.PI, accented ? Is.EqualTo(30).Within(0.01) : Is.Zero, "accent en 4");
        });
    }

    [TestCase(31f, false)] // no overlap of the metric (30 px) without the option: it excuses nothing
    [TestCase(29f, false)] // an overlap, but within the rounding margin: a written file may read it back at 30 px
    [TestCase(25f, true)]
    public void AccentAngles_OnlyOverlapsWithoutTheOptionExcuseTheSame(float distance, bool excused)
    {
        // Object 2 was 200 px from object 0; without the option it lands this far from it.
        MapObject[] objects = [Circle(100, 100, 0), Circle(200, 100, 250), Circle(300, 100, 500)];
        Placement[] plain =
        [
            new(objects[0].Start, 0), new(objects[1].Start, 0), new(objects[0].Start + new Vector2(0, distance), 0),
        ];

        var result = Amplifier.Excused(objects, [500, 500, 500], plain);

        Assert.That(result, Is.EqualTo(new[] { false, false, excused }));
    }

    [TestCase(1.0)]
    [TestCase(2.0)]
    public void AccentAngles_AddsNoOverlap(double factor)
    {
        // Spec §5.2: no more overlaps than without the option (at k = 1, the map as it is: none).
        var objects = RandomJumps(5, 400);

        var plain = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true));
        var accented = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true, AccentAngles: true));
        int without = ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects, plain.Placements));
        int with = ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects, accented.Placements));
        var turns = (Plain: Turns(Layout.From(objects, plain.Placements), 1, objects.Length - 2).Average(Math.Abs),
            Accented: Turns(Layout.From(objects, accented.Placements), 1, objects.Length - 2).Average(Math.Abs));
        TestContext.Out.WriteLine($"chevauchements {without} -> {with}, virage moyen {turns.Plain:0.0}° -> {turns.Accented:0.0}°");

        Assert.Multiple(() =>
        {
            Assert.That(with, Is.LessThanOrEqualTo(without), "chevauchements");
            Assert.That(turns.Accented, Is.GreaterThan(turns.Plain + 5), "les virages doivent rester accentués");
        });
    }

    [Test]
    public void Stacks_StayStacked()
    {
        var result = Amplify(3, Circle(100, 100, 0), Circle(256, 192, 500), Circle(256, 192, 750),
            Circle(256, 192, 1000), Circle(400, 300, 1500));
        var after = result.Placements.Select(p => p.Start).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(after[2], Is.EqualTo(after[1]));
            Assert.That(after[3], Is.EqualTo(after[1]));
            Assert.That(Vector2.Distance(after[1], after[0]), Is.GreaterThan(Vector2.Distance(new(256, 192), new(100, 100))));
        });
    }

    [Test]
    public void Spinner_Untouched()
    {
        MapObject[] after = [Circle(300, 300, 2000), Circle(400, 300, 2250), Circle(400, 200, 2500)];
        var result = Amplify(3, [Circle(100, 100, 0), Circle(50, 50, 250), Spinner(500, 1500), .. after]);
        var alone = Amplify(3, after);

        Assert.Multiple(() =>
        {
            Assert.That(result.Placements[2], Is.EqualTo(new Placement(new Vector2(256, 192), 0)));
            // A spinner restarts the chain: what follows does not depend on what came before.
            Assert.That(result.Placements.Skip(3), Is.EqualTo(alone.Placements));
        });
    }

    [Test]
    public void SliderBody_StaysInside_AndShapeKept()
    {
        var slider = StraightSlider(400, 192, 500, 1000, new Vector2(100, 0));
        Assume.That(slider.End, Is.EqualTo(new Vector2(500, 192)));

        var result = Amplify(2, Circle(256, 192, 0), slider, Circle(300, 300, 1250));
        var placement = result.Placements[1];
        TestContext.Out.WriteLine($"{placement}");

        Assert.Multiple(() =>
        {
            Assert.That(InHeadArea(placement.Start), Is.True);
            foreach (var p in PlacedPath(slider, placement))
                Assert.That(InBodyArea(p), Is.True, $"{p} hors du playfield");
            Assert.That(Vector2.Distance(PlacedPath(slider, placement).Last(), placement.Start),
                Is.EqualTo(100).Within(0.01), "le slider garde sa longueur");
        });
    }

    [Test]
    public void SliderPath_RoundTripsWithTheRotation()
    {
        var map = MapLoader.Load(File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "simple.osu")));

        var result = Amplifier.Amplify(map.Objects, map.BeatLengthAt, new AmplifyOptions(2.5, true));
        var decoded = MapLoader.Load(OsuPatcher.Patch(map, result, "2.5x spaced Insane"));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < map.Objects.Count; i++)
            {
                var o = map.Objects[i];
                var p = result.Placements[i];
                if (o.Kind == ObjectKind.Spinner) continue;
                Assert.That(Vector2.Distance(decoded.Objects[i].Start, p.Start), Is.LessThanOrEqualTo(0.71f), $"tête {i}");
                var expected = PlacedPath(o, p).ToArray();
                for (int j = 0; j < expected.Length; j++)
                    Assert.That(Vector2.Distance(decoded.Objects[i].Start + decoded.Objects[i].PathSamples[j], expected[j]),
                        Is.LessThanOrEqualTo(1.5f), $"objet {i}, point {j}");
            }
            Assert.That(result.Placements[2].Start, Is.Not.EqualTo(map.Objects[2].Start), "le slider doit bouger");
        });
    }

    [Test]
    public void HugeSlider_StaysOnItsOriginalSpot()
    {
        // 640 px long: longer than the diagonal of the body area (634 px), it fits nowhere, whatever the rotation,
        // so it keeps the only spot it is allowed on, exactly (OsuPatcher then copies its line).
        var slider = StraightSlider(6, 6, 500, 1000, new Vector2(512, 384));

        var result = Amplify(3, Circle(300, 100, 0), slider, Circle(100, 300, 1500));

        Assert.Multiple(() =>
        {
            Assert.That(result.Placements[1], Is.EqualTo(new Placement(slider.Start, 0)));
            Assert.That(InHeadArea(result.Placements[0].Start), Is.True);
            Assert.That(InHeadArea(result.Placements[2].Start), Is.True);
        });
    }

    [Test]
    public void ImpossibleBlock_IsSplit_AndItsPartsPlaced()
    {
        // The four corners of the playfield in one pattern: 512×384 fits nowhere, at any rotation, so the block is
        // cut in two; each half (one 512 px edge) fits once turned, and is placed instead of staying in its corners.
        Vector2[] corners = [new(0, 0), new(512, 0), new(512, 384), new(0, 384)];
        var objects = corners.Select((p, i) => Circle(p.X, p.Y, i * 250)).ToArray();

        var result = Amplify(2, objects);
        var after = result.Placements.Select(p => p.Start).ToArray();
        TestContext.Out.WriteLine(string.Join(" ", after) + $" Corrected={result.Corrected}");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < after.Length; i++)
                Assert.That(InHeadArea(after[i]), Is.True, $"objet {i} en {after[i]}");
            // Each half is rigid: its 512 px edge is at most zoomed by g(512)/512 ≈ 1.04.
            Assert.That(Vector2.Distance(after[1], after[0]), Is.InRange(511.9, 532.5));
            Assert.That(Vector2.Distance(after[3], after[2]), Is.InRange(511.9, 532.5));
        });
    }

    [TestCase(2.0)]
    [TestCase(3.0)]
    public void SliderHead_StaysInPlayfield_WhenOriginalBodyWentPastTheEdge(double factor)
    {
        // Head (500,100) inside, body out to x=620. The original bounding box may hold the body, never the head.
        var slider = HalfCircleSlider(500, 100, 1000, 60);
        Assume.That(slider.PathSamples.Max(s => slider.Start.X + s.X), Is.GreaterThan(512));
        float minX = slider.PathSamples.Min(s => slider.Start.X + s.X), maxX = slider.PathSamples.Max(s => slider.Start.X + s.X);
        float minY = slider.PathSamples.Min(s => slider.Start.Y + s.Y), maxY = slider.PathSamples.Max(s => slider.Start.Y + s.Y);

        Assert.Multiple(() =>
        {
            for (int ax = 400; ax <= 496; ax += 48)
            for (int ay = 16; ay < 384; ay += 48)
            {
                var placement = Amplify(factor, Circle(ax, ay, 0), slider).Placements[1];
                Assert.That(InHeadArea(placement.Start), Is.True, $"après ({ax},{ay}) : tête en {placement.Start}");
                foreach (var p in PlacedPath(slider, placement))
                    Assert.That(InBodyArea(p) || (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY), Is.True,
                        $"après ({ax},{ay}) : corps en {p}");
            }
        });
    }

    [Test]
    public void AllInsidePlayfield_AtFactor3()
    {
        var rng = new Random(42);
        var objects = Enumerable.Range(0, 400)
            .Select(i => Circle(rng.Next(0, 513), rng.Next(0, 385), i * 300))
            .ToArray();

        var result = Amplify(3, objects);
        TestContext.Out.WriteLine($"Corrected={result.Corrected} Reduced={result.Reduced} "
                                  + $"jump {result.AverageJumpBefore:0.0} -> {result.AverageJumpAfter:0.0}");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < objects.Length; i++)
            {
                var p = result.Placements[i].Start;
                Assert.That(InHeadArea(p) || p == objects[i].Start, Is.True, $"{p} hors du playfield");
            }
            Assert.That(result.Corrected, Is.GreaterThan(0));
            Assert.That(result.AverageJumpAfter, Is.GreaterThan(result.AverageJumpBefore));
        });
    }

    [Test]
    public void OriginalOutOfBounds_AllowedOnlyForThatObject()
    {
        MapObject[] objects =
        [
            Circle(256, 192, 0), Circle(-50, 400, 1000), Circle(30, 360, 2000), Circle(256, 192, 3000),
        ];

        var identity = Amplify(1, objects);
        var amplified = Amplify(2, objects);

        Assert.Multiple(() =>
        {
            Assert.That(identity.Placements, Is.EqualTo(objects.Select(o => new Placement(o.Start, 0))));
            foreach (int i in new[] { 0, 2, 3 })
                Assert.That(InHeadArea(amplified.Placements[i].Start), Is.True,
                    $"objet {i} en {amplified.Placements[i].Start}");
            var outOfBounds = amplified.Placements[1].Start;
            Assert.That(InHeadArea(outOfBounds) || outOfBounds == objects[1].Start, Is.True, $"{outOfBounds}");
        });
    }

    [Test]
    public void Averages_AreZero_WithoutJumps()
    {
        var result = Amplify(2, Circle(256, 192, 0), Spinner(500, 1000), Circle(100, 100, 1500),
            Circle(100, 100, 2000));

        Assert.Multiple(() =>
        {
            Assert.That(result.AverageJumpBefore, Is.Zero);
            Assert.That(result.AverageJumpAfter, Is.Zero);
        });
    }

    /// <summary>2000 objects at random, a slider every 5, at 1/4 or 1/2.</summary>
    private static MapObject[] PerfObjects()
    {
        var rng = new Random(1);
        var objects = new MapObject[2000];
        double t = 0;
        for (int i = 0; i < objects.Length; i++)
        {
            float x = rng.Next(40, 473), y = rng.Next(40, 345);
            objects[i] = i % 5 == 4
                ? StraightSlider(x, y, t, t + 250, new Vector2(rng.Next(-60, 61), rng.Next(-60, 61)))
                : Circle(x, y, t);
            t = objects[i].EndTime + (rng.Next(3) == 0 ? 125 : 250);
        }
        return objects;
    }

    /// <summary>The time of the fastest of 3 runs, after 2 runs to warm up (JIT, tiered compilation, caches): the
    /// first runs of a cold process are much slower, and so is a run that a garbage collection falls into.</summary>
    private static (double Ms, AmplifyResult Result) Timed(MapObject[] objects, AmplifyOptions options)
    {
        AmplifyResult result = Amplifier.Amplify(objects, Beat500, options);
        Amplifier.Amplify(objects, Beat500, options);
        double best = double.PositiveInfinity;
        for (int run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            result = Amplifier.Amplify(objects, Beat500, options);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return (best, result);
    }

    [Test]
    public void Perf_2000Objects_AccentAngles()
    {
        var (ms, result) = Timed(PerfObjects(), new AmplifyOptions(2, true, AccentAngles: true));
        TestContext.Out.WriteLine($"{ms:F2} ms");

        Assert.Multiple(() =>
        {
            Assert.That(result.Placements, Has.Count.EqualTo(2000));
            Assert.That(ms, Is.LessThan(PerfAccentLimitMs));
        });
    }

    [Test]
    public void Perf_2000Objects()
    {
        var (ms, result) = Timed(PerfObjects(), new AmplifyOptions(2, true));
        TestContext.Out.WriteLine($"{ms:F2} ms");

        Assert.Multiple(() =>
        {
            Assert.That(result.Placements, Has.Count.EqualTo(2000));
            Assert.That(ms, Is.LessThan(PerfLimitMs));
        });
    }

    /// <summary>About 3× the time measured on the development machine, at most 1 s: ≈ 660 ms warm in a Debug build
    /// (≈ 180 ms in Release), since the blocks of a beat (Kakudai 2 spec §13) make every 1/4 jump of
    /// <see cref="PerfObjects"/> part of a run, so the cap applies.</summary>
    private const int PerfLimitMs = 1000;

    /// <summary>About 3× the time measured with accented angles (≈ 2.05 s, Debug build): the random jumps of
    /// <see cref="PerfObjects"/> land many objects on each other, so the final check of the accents places much of the
    /// map again.</summary>
    private const int PerfAccentLimitMs = 6000;
}
