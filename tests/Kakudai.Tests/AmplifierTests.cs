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

        // It cannot reach g(300) ≈ 443 px in a 384 px high playfield: the sides grow as far as they fit, the vertical
        // ones less, so it is a rectangle, not a square turned to fit.
        double zoom = AssertSimilar(square, after, 0.08);
        Assert.Multiple(() =>
        {
            Assert.That(after.All(InHeadArea), Is.True, "tête hors du playfield");
            Assert.That(zoom, Is.InRange(1.2, 1.4), "le carré grandit autant qu'il tient, pas plus");
            for (int i = 1; i < square.Length; i++)
                Assert.That(Vector2.Distance(after[i], after[i - 1]), Is.InRange(300, 445), $"saut {i}");
            Assert.That(result.Placements.Select(p => p.Rotation), Is.All.Zero);
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
            Assert.That(zoom[zoom.Length / 10], Is.GreaterThanOrEqualTo(0.9 * zoom[zoom.Length / 2]),
                "le stream est écarté de la même façon tout du long");
            Assert.That(inStream.Average(), Is.LessThan(6), "le stream garde ses virages, à une courbure douce près");
        });
    }

    /// <summary>The median zoom the deathstream must reach at k = 2. Kakudai 3 keeps its loops and their orientation
    /// (×1.56); 2.1 to 2.2.1 reached ×1.99 by bending it at every beat, up to 25° per note.</summary>
    private const double DeathstreamZoom = 1.5;

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
            // Both ends of the kiai are held in place: the zigzag can only sharpen a little in between.
            Assert.That(after.Average(Math.Abs), Is.GreaterThanOrEqualTo(95), "virage moyen dans le kiai");
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
    public void AccentAngles_NeverTurnASlider()
    {
        // Jumps at 1/2 in a 90° zigzag (moves of 57 px) along the top edge, 12 px from it; object 2 is a slider whose
        // body goes 40 px right along the edge, and the zigzag goes on from its end. The turns are sharpened around it,
        // its body is only moved: a slider keeps its shape and its orientation (Kakudai 3).
        MapObject[] objects =
        [
            Circle(80, 12, 0), Circle(120, 52, 250), StraightSlider(160, 12, 500, 700, new Vector2(40, 0)),
            .. Enumerable.Range(0, 5).Select(i => Circle(240 + 40 * i, i % 2 == 0 ? 52 : 12, 950 + 250 * i)),
        ];

        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(1, true, AccentAngles: true));
        var layout = Layout.From(objects, result.Placements);
        TestContext.Out.WriteLine($"virages {string.Join(" ", Turns(layout, 1, 6).Select(t => t.ToString("0")))}");

        Assert.Multiple(() =>
        {
            Assert.That(Turns(layout, 1, 6).Average(Math.Abs), Is.GreaterThan(105), "virages accentués");
            Assert.That(result.Placements.Select(p => p.Rotation), Is.All.Zero, "rien n'est tourné");
            foreach (var p in PlacedPath(objects[2], result.Placements[2]))
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
            // A jump turns to sharpen the angles: never shorter than it was, longer by 1/cos(25°) at most.
            var gaps = Gaps(layout);
            for (int i = 0; i < gaps.Length; i++)
            {
                double before = Vector2.Distance(objects[i + 1].Start, objects[i].Start);
                Assert.That(gaps[i], Is.InRange(before - 0.5, before / Math.Cos(Amplifier.ConeDegrees * Math.PI / 180) + 0.5),
                    $"à k = 1, le saut {i + 1} garde à peu près sa longueur");
            }
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
            // The whole map is one problem: the accents around the stream may move it and curve it a little, never
            // accent its turns nor cut it.
            var zooms = Enumerable.Range(stream[1], stream.Length - 1)
                .Select(i => Gaps(a)[i - 1] / Vector2.Distance(objects[i].Start, objects[i - 1].Start)).ToArray();
            Assert.That(zooms.Max() / zooms.Min(), Is.LessThan(1.3), "le stream est écarté d'un seul tenant");
            var (turnsO, turnsA) = (Turns(original, stream[1], stream[^2]), Turns(a, stream[1], stream[^2]));
            for (int i = 0; i < turnsO.Length; i++)
                Assert.That(turnsA[i], Is.EqualTo(turnsO[i]).Within(6), $"virage {stream[1] + i} du stream");
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
    public void PlayfieldCorners_AreBroughtInside_AndKeepTheirEdges()
    {
        // The four corners of the playfield: nothing can grow, everything is brought 1 px inside, the edges stay as long
        // as the playfield allows.
        Vector2[] corners = [new(0, 0), new(512, 0), new(512, 384), new(0, 384)];
        var objects = corners.Select((p, i) => Circle(p.X, p.Y, i * 250)).ToArray();

        var result = Amplify(2, objects);
        var after = result.Placements.Select(p => p.Start).ToArray();
        TestContext.Out.WriteLine(string.Join(" ", after));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < after.Length; i++)
                Assert.That(InHeadArea(after[i]), Is.True, $"objet {i} en {after[i]}");
            Assert.That(Vector2.Distance(after[1], after[0]), Is.EqualTo(510).Within(0.1));
            Assert.That(Vector2.Distance(after[2], after[1]), Is.EqualTo(382).Within(0.1));
            Assert.That(Vector2.Distance(after[3], after[2]), Is.EqualTo(510).Within(0.1));
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


    /// <summary>A map of jumps and streams that fills the playfield: random jumps at 1/2, every 8th beat a 1/4 stream
    /// of 12 notes along a gentle curve.</summary>
    private static MapObject[] MixedMap(int seed)
    {
        var jumps = RandomJumps(seed, 160);
        var objects = new List<MapObject>();
        double time = 0;
        for (int i = 0; i < jumps.Length; i++)
        {
            objects.Add(Circle(jumps[i].Start.X, jumps[i].Start.Y, time));
            time += 250;
            if (i % 16 != 15) continue;
            var p = jumps[i].Start;
            var heading = Math.Atan2(192 - p.Y, 256 - p.X);
            for (int j = 0; j < 12; j++)
            {
                heading += 0.12;
                p += 22 * new Vector2((float)Math.Cos(heading), (float)Math.Sin(heading));
                objects.Add(Circle(p.X, p.Y, time));
                time += 125;
            }
            time += 125;
        }
        return objects.ToArray();
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void MoreSpacing_NeverMakesTheMapSmaller(int seed)
    {
        // The player's report: going from 2.5 to 2.6 could lower the star rating. The spacing is the solution of one
        // convex problem: it changes continuously with k, every move is at least as long as in the map, and the map
        // gets bigger as k grows.
        var objects = MixedMap(seed);
        var original = Gaps(Layout.From(objects));
        double previous = 0;
        Assert.Multiple(() =>
        {
            for (double k = 1.5; k <= 3.0001; k += 0.1)
            {
                var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(k, true));
                var gaps = Gaps(Layout.From(objects, result.Placements));
                double mean = gaps.Average();
                Assert.That(mean, Is.GreaterThanOrEqualTo(previous - 0.05), $"k = {k:0.0} : saut moyen {mean:0.00}");
                previous = mean;
                for (int i = 0; i < gaps.Length; i++)
                    Assert.That(gaps[i], Is.GreaterThanOrEqualTo(original[i] - 0.1), $"k = {k:0.0} : saut {i + 1} raccourci");
            }
        });
    }

    [TestCase(2.0)]
    [TestCase(3.0)]
    public void Patterns_AreNeverTurned(double factor)
    {
        // The player's report: the patterns were turned to make room, which broke the structure of the map. Every jump
        // keeps its direction within ConeDegrees. Only a stream that cannot be spaced where it is may be turned as one
        // piece (these 12-note streams aimed at the centre would need 528 px at ×2), and then the jump out of it
        // re-aims, never back on itself.
        var objects = MixedMap(4);
        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true));
        var after = Layout.From(objects, result.Placements);
        var jumps = new List<double>();
        var outs = new List<double>();
        var steps = new List<double>();
        for (int i = 1; i < objects.Length; i++)
        {
            var before = objects[i].Start - objects[i - 1].End;
            if (before.Length() < 3) continue;
            double deviation = Math.Abs(TurnDegrees(before, after.Starts[i] - after.Ends[i - 1]));
            bool step = objects[i].StartTime - objects[i - 1].StartTime <= 125;
            bool afterStream = i >= 2 && objects[i - 1].StartTime - objects[i - 2].StartTime <= 125;
            (step ? steps : afterStream ? outs : jumps).Add(deviation);
        }
        TestContext.Out.WriteLine($"sauts : {jumps.Average():0.0}° en moyenne, {jumps.Max():0.0}° au plus ; "
                                  + $"après un stream : {outs.Max():0.0}° ; streams : {steps.Average():0.0}°, {steps.Max():0.0}°");
        Assert.Multiple(() =>
        {
            Assert.That(result.Placements.Select(p => p.Rotation), Is.All.Zero);
            Assert.That(jumps.Max(), Is.LessThanOrEqualTo(Amplifier.ConeDegrees + 0.5), "un saut quitte sa direction");
            Assert.That(jumps.Average(), Is.LessThan(12));
            Assert.That(outs.Max(), Is.LessThan(90), "le saut après un stream tourné revient en arrière");
            Assert.That(Enumerable.Range(1, objects.Length - 1).Where(i => (objects[i].Start - objects[i - 1].End).Length() >= 3)
                .Min(i => Vector2.Distance(after.Starts[i], after.Ends[i - 1]) / (objects[i].Start - objects[i - 1].End).Length()),
                Is.GreaterThan(0.79), "un saut raccourci de plus de 20 %");
            Assert.That(steps.Max(), Is.LessThanOrEqualTo(90), "un stream tourné de plus de 60° (et 30° de courbe)");
        });
    }

    [Test]
    public void Bursts_AlongAnEdge_AreTurnedToBeSpaced()
    {
        // The player's report (AliA - utopia [Feelings]): streams along the edges of a map that already fills the
        // playfield were not spaced. A 10-note stream runs along the top from x = 330 to x = 15, after a jump from the
        // right: ×2 needs 630 px across. It is turned down as one piece, evenly spaced; a stream with room is not
        // turned.
        var objects = new List<MapObject> { Circle(480, 200, 0), Circle(400, 300, 500), Circle(480, 200, 1000) };
        double time = 1250;
        for (int j = 0; j < 10; j++, time += 125) objects.Add(Circle(330 - 35 * j, 30, time));
        objects.Add(Circle(100, 250, time + 125));
        objects.Add(Circle(200, 330, time + 625));
        time += 1125;
        // A short stream in the middle, with room.
        for (int j = 0; j < 4; j++, time += 125) objects.Add(Circle(230 + 20 * j, 200, time));
        objects.Add(Circle(330, 300, time + 375));
        var map = objects.ToArray();

        var result = Amplifier.Amplify(map, Beat500, new AmplifyOptions(2, true));
        var layout = Layout.From(map, result.Placements);
        var zooms = Enumerable.Range(4, 9).Select(i => Vector2.Distance(layout.Starts[i], layout.Starts[i - 1]) / 35).ToArray();
        TestContext.Out.WriteLine("stream du haut : " + string.Join(" ", zooms.Select(z => z.ToString("0.00"))));
        var middle = Enumerable.Range(map.Length - 4, 3)
            .Select(i => Math.Abs(TurnDegrees(map[i].Start - map[i - 1].Start, layout.Starts[i] - layout.Starts[i - 1])))
            .ToArray();
        Assert.Multiple(() =>
        {
            // 3.0.0, which never turned a stream: ×1.39.
            Assert.That(zooms.Min(), Is.GreaterThan(1.55), "le stream le long du bord est écarté");
            Assert.That(zooms.Max() / zooms.Min(), Is.LessThan(1.1), "d'un seul tenant");
            Assert.That(middle.Max(), Is.LessThan(Amplifier.ConeDegrees + 0.5), "le stream qui a la place n'est pas tourné");
            Assert.That(Layout.From(map).Starts.Zip(layout.Starts).All(p => p.Second.X is >= 0 and <= 512 && p.Second.Y is >= 0 and <= 384));
        });
    }

    [TestCase(1.5)]
    [TestCase(2.0)]
    [TestCase(3.0)]
    public void Streams_AreZoomedAsOnePiece(double factor)
    {
        // The player's report: streams spaced one way here, another there, or bent at odd places. Each stream of the
        // mixed map is zoomed alike all along and keeps its curve.
        var objects = MixedMap(5);
        var result = Amplifier.Amplify(objects, Beat500, new AmplifyOptions(factor, true));
        var layout = Layout.From(objects, result.Placements);
        var gaps = Gaps(layout);
        Assert.Multiple(() =>
        {
            for (int start = 0; start < objects.Length; start++)
            {
                if (start == 0 || objects[start].StartTime - objects[start - 1].StartTime != 125) continue;
                if (start >= 2 && objects[start - 1].StartTime - objects[start - 2].StartTime == 125) continue;
                // A stream: from start − 1, 12 notes.
                var zooms = Enumerable.Range(start, 11).Select(i =>
                    gaps[i - 1] / Vector2.Distance(objects[i].Start, objects[i - 1].Start)).ToArray();
                string all = string.Join(" ", zooms.Select(z => z.ToString("0.00")));
                // A long stream may grow or shrink slowly along a wall, more so at k = 3; never by a jump.
                Assert.That(zooms.Max() / zooms.Min(), Is.LessThan(1.25 + 0.1 * (factor - 1)), $"stream à l'objet {start - 1} : {all}");
                var turnsBefore = Turns(Layout.From(objects), start, start + 9);
                var turnsAfter = Turns(layout, start, start + 9);
                for (int i = 0; i < turnsBefore.Length; i++)
                    Assert.That(Math.Abs(turnsAfter[i] - turnsBefore[i]), Is.LessThan(10 + 2.5 * (factor - 1)), $"virage faussé dans le stream à l'objet {start - 1}");
                for (int i = 1; i < zooms.Length; i++)
                    Assert.That(zooms[i] / zooms[i - 1], Is.InRange(1 / 1.08, 1.08), $"saute dans le stream à l'objet {start - 1} : {all}");
                Assert.That(zooms.Min(), Is.GreaterThan(1.05), $"stream à l'objet {start - 1} écarté");
            }
        });
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
