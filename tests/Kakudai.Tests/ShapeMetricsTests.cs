using System.Numerics;
using Kakudai.Core.Maps;
using NUnit.Framework;

namespace Kakudai.Tests;

public class ShapeMetricsTests
{
    private static readonly Func<double, double> Beat500 = _ => 500;

    private static MapObject Circle(Vector2 p, double time) => new(0, ObjectKind.Circle, time, time, p, p, []);

    /// <summary>A zigzag at 1/4 (a stream), 40 px moves.</summary>
    private static MapObject[] Zigzag()
        => Enumerable.Range(0, 12).Select(i => Circle(new Vector2(100 + 30 * i, i % 2 == 0 ? 180 : 206), i * 125)).ToArray();

    private static Vector2 Similar(Vector2 p) // zoom 1.5, turned by 30°, moved
    {
        float c = MathF.Cos(MathF.PI / 6), s = MathF.Sin(MathF.PI / 6);
        return 1.5f * new Vector2(c * p.X - s * p.Y, s * p.X + c * p.Y) + new Vector2(-40, 25);
    }

    [Test]
    public void Similarity_WithRotation_IsZero_ForATurnedCopy()
    {
        var objects = Zigzag();
        var layout = Layout.From(objects.Select(o => Circle(Similar(o.Start), o.StartTime)).ToArray());

        var withRotation = ShapeMetrics.WindowErrors(objects, Beat500, layout.Starts);
        var withoutRotation = ShapeMetrics.WindowErrors(objects, Beat500, layout.Starts, rotation: false);
        var (turns, stream) = ShapeMetrics.TurnErrors(objects, Beat500, layout);

        Assert.Multiple(() =>
        {
            Assert.That(withRotation, Is.Not.Empty);
            Assert.That(withRotation.Max(), Is.LessThan(1e-4));
            Assert.That(withoutRotation.Max(), Is.GreaterThan(0.1), "sans rotation, une copie tournée n'est pas semblable");
            Assert.That(turns.Max(), Is.LessThan(0.01));
            Assert.That(stream, Has.Count.EqualTo(turns.Count), "tous les virages sont dans le stream");
        });
    }

    [Test]
    public void TurnErrors_SeeAStreamPulledStraight()
    {
        var objects = Zigzag();
        var straight = objects.Select(o => Circle(new Vector2(o.Start.X, 193), o.StartTime)).ToArray();

        var (turns, stream) = ShapeMetrics.TurnErrors(objects, Beat500, Layout.From(straight));

        // Each zigzag turn is ±2·atan(26/30) ≈ 82° and becomes 0.
        Assert.That(stream.Average(), Is.EqualTo(2 * Math.Atan(26.0 / 30) * 180 / Math.PI).Within(0.5));
        Assert.That(ShapeMetrics.WindowErrors(objects, Beat500, straight.Select(o => o.Start).ToArray()).Average(),
            Is.GreaterThan(0.2));
        Assert.That(turns, Has.Count.EqualTo(stream.Count));
    }

    /// <summary>A straight stream at 1/4, 20 px apart, preceded by a 1/2 jump (not part of the stream).</summary>
    private static MapObject[] StraightStream()
        =>
        [
            Circle(new Vector2(40, 192), 0),
            .. Enumerable.Range(0, 10).Select(i => Circle(new Vector2(100 + 20 * i, 192), 250 + i * 125)),
        ];

    /// <summary>The objects of <paramref name="objects"/> moved: each gap from object 2 on zoomed by its factor.</summary>
    private static Layout Spread(MapObject[] objects, Func<int, float> factor)
    {
        var starts = new Vector2[objects.Length];
        starts[0] = objects[0].Start;
        starts[1] = objects[1].Start;
        for (int i = 2; i < objects.Length; i++)
            starts[i] = starts[i - 1] + factor(i) * (objects[i].Start - objects[i - 1].Start);
        return new Layout(starts, starts);
    }

    [Test]
    public void StreamCuts_FindsTheGapThatBecameAJump()
    {
        var objects = StraightStream();
        // Every gap of the stream doubled (40 px), except the one into object 6: 100 px, 2.5 × the median.
        var layout = Spread(objects, i => i == 6 ? 5 : 2);

        var cuts = ShapeMetrics.StreamCuts(objects, Beat500, layout);

        Assert.Multiple(() =>
        {
            Assert.That(cuts, Has.Count.EqualTo(1));
            Assert.That(cuts[0].Index, Is.EqualTo(6));
            Assert.That(cuts[0].Before, Is.EqualTo(20).Within(0.01));
            Assert.That(cuts[0].After, Is.EqualTo(100).Within(0.01));
            Assert.That(cuts[0].MedianBefore, Is.EqualTo(20).Within(0.01));
            Assert.That(cuts[0].MedianAfter, Is.EqualTo(40).Within(0.01));
        });
    }

    [Test]
    public void StreamCuts_IgnoresUniformZoomAndGapsLongAlready()
    {
        var objects = StraightStream();
        // A gap already 2 × the others in the original (40 px), zoomed like them, stays 2 × the median: not a cut.
        objects[6] = Circle(objects[6].Start + new Vector2(20, 0), objects[6].StartTime);
        for (int i = 7; i < objects.Length; i++)
            objects[i] = Circle(objects[i].Start + new Vector2(20, 0), objects[i].StartTime);
        var uniform = Spread(objects, _ => 2);
        // Gaps zoomed by 1.7 and 2.2 in turn: none 1.5 × the median but the long one, which was already long.
        var uneven = Spread(objects, i => i % 2 == 0 ? 1.7f : 2.2f);

        Assert.Multiple(() =>
        {
            Assert.That(ShapeMetrics.StreamCuts(objects, Beat500, uniform), Is.Empty, "zoom uniforme");
            Assert.That(ShapeMetrics.StreamCuts(objects, Beat500, uneven), Is.Empty, "zoom irrégulier");
            Assert.That(ShapeMetrics.StreamCuts(objects, Beat500, Layout.From(objects)), Is.Empty, "identité");
        });
    }

    /// <summary>A straight stream at 1/4 of <paramref name="gaps"/> + 1 notes, 20 px apart.</summary>
    private static MapObject[] LongStream(int gaps)
        => Enumerable.Range(0, gaps + 1).Select(i => Circle(new Vector2(10 + 20 * i, 192), i * 125)).ToArray();

    /// <summary>The stream laid down with the given gaps (a straight line: the playfield does not matter here).</summary>
    private static Layout WithGaps(MapObject[] objects, Func<int, float> gap)
    {
        var starts = new Vector2[objects.Length];
        starts[0] = objects[0].Start;
        for (int i = 1; i < objects.Length; i++)
            starts[i] = starts[i - 1] + new Vector2(gap(i), 0);
        return new Layout(starts, starts);
    }

    [Test]
    public void StreamCuts_IgnoreASlowChangeOfTheSpacing()
    {
        // 60 gaps of 20 px: the first 30 kept, then 10 growing by 3 px each, then 20 of 50 px. Against the median of
        // the whole stream (26 px), the gaps of 50 px would be cuts; against their neighbours (Kakudai 2 spec §13), the
        // spacing only changes slowly: no cut.
        var objects = LongStream(60);
        var layout = WithGaps(objects, i => i <= 30 ? 20 : i <= 40 ? 20 + 3 * (i - 30) : 50);

        Assert.That(ShapeMetrics.StreamCuts(objects, Beat500, layout), Is.Empty);
    }

    [Test]
    public void StreamCuts_FindAJumpAgainstItsNeighbours()
    {
        // The same stream, then one gap of 100 px among gaps of 50 px: 2 × its neighbours, a cut, with their medians.
        var objects = LongStream(60);
        var layout = WithGaps(objects, i => i == 50 ? 100 : i <= 30 ? 20 : i <= 40 ? 20 + 3 * (i - 30) : 50);

        var cuts = ShapeMetrics.StreamCuts(objects, Beat500, layout);

        Assert.Multiple(() =>
        {
            Assert.That(cuts, Has.Count.EqualTo(1));
            Assert.That(cuts[0].Index, Is.EqualTo(50));
            Assert.That(cuts[0].MedianBefore, Is.EqualTo(20).Within(0.01));
            Assert.That(cuts[0].MedianAfter, Is.EqualTo(50).Within(0.01));
        });
    }

    [Test]
    public void Overlaps_CountAHeadLandingOnAFarObject()
    {
        MapObject[] objects =
        [
            Circle(new Vector2(100, 100), 0), Circle(new Vector2(200, 100), 250), Circle(new Vector2(300, 100), 500),
        ];
        var moved = objects.Select(o => o.Start).ToArray();
        moved[2] = new Vector2(110, 105); // on the first object, which was 200 px away

        Assert.Multiple(() =>
        {
            Assert.That(ShapeMetrics.Overlaps(objects, Beat500, Layout.From(objects)), Is.Zero);
            Assert.That(ShapeMetrics.Overlaps(objects, Beat500, new Layout(moved, moved)), Is.EqualTo(1));
        });
    }
}
