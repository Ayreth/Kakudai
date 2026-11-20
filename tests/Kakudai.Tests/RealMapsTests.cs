using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using Kakudai.Core;
using Kakudai.Core.Amplify;
using Kakudai.Core.Library;
using Kakudai.Core.Maps;
using Kakudai.Core.Osu;
using Kakudai.Core.Training;
using NUnit.Framework;

namespace Kakudai.Tests;

/// <summary>
/// Runs the pipeline on maps of the player's own library. Read only: the game folder is never written to
/// (the base is copied to a temporary folder by <see cref="OsuLibrary.Refresh"/>).
/// </summary>
public class RealMapsTests
{
    private const int SampleSize = 300;
    private const int MinChecked = 250;
    private const int MaxListed = 30;
    private const double MinCleanShare = 0.985;
    private static readonly double[] Factors = [1.5, 2, 3];

    /// <summary>The factor the options (accented angles, only the kiai) are measured at.</summary>
    private const double OptionsFactor = 2;
    private static readonly string[] AddedKeys = ["Tags", "BeatmapID", "BeatmapSetID"];

    private string temp = "";

    [SetUp] public void Up() => temp = Path.Combine(Path.GetTempPath(), "kakudai-realmaps-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Down() { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private OsuLibrary Library()
    {
        var library = new OsuLibrary(OsuLibrary.DefaultDataDir, Path.Combine(temp, "realm"));
        library.Refresh();
        return library;
    }

    private static BeatmapEntry[] Shuffled(OsuLibrary library)
    {
        var pool = library.Entries.DistinctBy(e => e.OsuHash).ToArray();
        new Random(7).Shuffle(pool);
        return pool;
    }

    /// <summary>
    /// At k = 1.5, 2 and 3 with streams spaced, the generated .osu, read back the way the game reads it, keeps every
    /// object inside the playfield (objects that already went past the edge in the original are excused) on at least
    /// <see cref="MinCleanShare"/> of the maps (ruling R20: the rounding of the control points of perfect-circle and
    /// Bézier sliders may drift them by a pixel), is never refused, keeps every time, and changes nothing but
    /// positions and the 4 header lines; at k = 1 only the header changes. Also prints, for each k, measured on the
    /// output read back: the average jump, the shape error (similarity with rotation, ±1 beat), the turning angles
    /// changed (all, and inside streams), the stream cuts (Kakudai 2 spec §5.1, each one listed), the jumps over
    /// 400 px, the accidental overlaps, and the slowest map.
    /// At k = 2, also with the options of Kakudai 2: with accented angles (spec §5.2), the same checks, the turns between
    /// two jumps before and after (they must be sharper), as many clean maps and no more overlaps than without the
    /// option; with only the kiai sections (spec §5.3), on the maps that have some, the same checks, and every object
    /// outside the kiai left exactly in place (a stream split at the edge of a kiai section is not counted as a cut).
    /// At k = 1 with accented angles, the same checks, and no overlap at all: without the option, the map is as it is.
    /// </summary>
    [Test, Explicit]
    public void RealMaps_AllInsideAndIdentityHolds()
    {
        var library = Library();
        var sample = Shuffled(library).Take(SampleSize).ToList();

        var unreadable = new List<string>();
        var unsupported = new List<string>();
        var failures = new List<string>();
        var identity = new List<string>();
        var refused = new List<string>();    // generated texts MapLoader does not read back
        var violations = new List<string>(); // generated text: an object that was inside now has a point outside
        var worsened = new List<string>();   // generated text: an object that was outside now goes farther out
        var planned = new List<string>();    // diagnostics: the Amplifier's placements, before writing
        var cuts = new List<string>();       // diagnostics: stream gaps that became jumps (spec Kakudai 2 §5.1)
        var optionCuts = new List<string>(); // the same with the options of Kakudai 2
        var perK = Factors.ToDictionary(k => k, _ => new FactorStats());
        var angles = new FactorStats();      // k = 2, accented angles
        var anglesAt1 = new FactorStats();   // k = 1, accented angles: without the option, the map as it is
        var kiai = new FactorStats();        // k = 2, only the kiai sections, on the maps that have some
        JumpTurnStats[] jumpTurns = [new(), new()]; // k = 2, without and with accented angles
        var movedOutsideKiai = new List<string>();
        int kiaiMaps = 0, kiaiObjects = 0;
        int checkedMaps = 0;
        double starsBefore = 0, starsAfter = 0;
        var slowest = (Ms: 0.0, Map: "");

        foreach (var entry in sample)
        {
            string text;
            try
            {
                text = KakudaiService.ReadText(library.FilePath(entry.OsuHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                unreadable.Add($"{entry.Display} : {ex.Message}");
                continue;
            }

            LoadedMap map;
            try
            {
                map = KakudaiService.LoadMap(text);
            }
            catch (UnsupportedMapException ex)
            {
                unsupported.Add($"{entry.Display} : {ex.Message}");
                continue;
            }

            checkedMaps++;
            var trainerBase = Trainer.ReadBase(text);

            // Generates the map with these options, reads it back the way the game reads it, checks it and measures it
            // into stats; returns what was generated and its layout read back (null when MapLoader refuses it).
            (string Patched, AmplifyResult Result, Layout Layout)? Generate(AmplifyOptions options, FactorStats stats,
                string label)
            {
                var watch = Stopwatch.StartNew();
                var result = Amplifier.Amplify(map.Objects, map.BeatLengthAt, options, out int reverted);
                watch.Stop();
                if (watch.Elapsed.TotalMilliseconds > slowest.Ms)
                    slowest = (watch.Elapsed.TotalMilliseconds, $"{entry.Display} ({map.Objects.Count} objets, {label})");

                var version = Trainer.Resolve(trainerBase, new TrainerSettings(Factor: options.Factor)).Version;
                var patched = OsuPatcher.Patch(map, result, version);
                CheckText(entry.Display, map.Lines, patched.Split('\n'), version, identity, onlyHeader: false);

                // Diagnostics only: where the Amplifier meant each object to go.
                bool plannedOutside = false;
                for (int i = 0; i < map.Objects.Count; i++)
                {
                    var o = map.Objects[i];
                    var p = result.Placements[i];
                    var outside = Points(p.Start, o.PathSamples, p.Rotation).Where(q => !Inside(q)).ToArray();
                    if (outside.Length == 0 || !WasInside(o)) continue;
                    plannedOutside = true;
                    planned.Add($"{entry.Display} ({label}) — objet {i} ({o.Kind}, ligne {o.LineIndex + 1}) : {outside[0]} prévu hors playfield "
                                + $"(tête {p.Start}, rotation {p.Rotation:0.###} rad)");
                }

                // What the game gets: the generated text decoded again. MapLoader throws if an object went
                // missing or is read elsewhere than its line says, so both lists pair up one to one.
                LoadedMap decoded;
                try { decoded = MapLoader.Load(patched); }
                catch (UnsupportedMapException ex)
                {
                    refused.Add($"{entry.Display} ({label}) : {ex.Message}");
                    return null;
                }

                bool inside = true, violation = false, worse = false;
                for (int i = 0; i < map.Objects.Count; i++)
                {
                    var o = map.Objects[i];
                    var d = decoded.Objects[i];
                    // A slider whose line is rewritten may end a fraction of a millisecond off: its length is read back
                    // from control points rounded to the pixel (one map, 0.16 ms); its start stays exact.
                    bool rewritten = o.Kind == ObjectKind.Slider && result.Placements[i] != new Placement(o.Start, 0);
                    if (d.StartTime != o.StartTime || Math.Abs(d.EndTime - o.EndTime) > (rewritten ? 1 : 0) || d.Kind != o.Kind)
                        identity.Add($"{entry.Display} ({label}) — objet {i} : relu {d.Kind} {d.StartTime}–{d.EndTime}, "
                                     + $"attendu {o.Kind} {o.StartTime}–{o.EndTime}");

                    var points = Points(d.Start, d.PathSamples, 0);
                    if (points.All(Inside)) continue;
                    inside = false;
                    string where = $"{entry.Display} ({label}) — objet {i} ({o.Kind}, ligne {o.LineIndex + 1}, t={o.StartTime}) : ";
                    if (WasInside(o))
                    {
                        violation = true;
                        violations.Add(where + points.First(q => !Inside(q)) + $" hors playfield (tête relue {d.Start}, marge prévue {PlannedMargin(o, result.Placements[i]):0.00} px) : « {map.Lines[o.LineIndex].TrimEnd('\r')} »");
                    }
                    else if (FirstViolation(o, points) is Vector2 bad)
                    {
                        worse = true;
                        worsened.Add(where + bad + $" plus loin qu'à l'origine (tête relue {d.Start})");
                    }
                }

                var layout = Layout.From(decoded.Objects);
                var streamCuts = options.KiaiOnly
                    ? KiaiStreamCuts(map, layout)
                    : ShapeMetrics.StreamCuts(map.Objects, map.BeatLengthAt, layout);
                foreach (var c in streamCuts)
                    (options.AccentAngles || options.KiaiOnly ? optionCuts : cuts).Add(string.Create(CultureInfo.InvariantCulture,
                        $"{entry.Display} ({label}) — objet {c.Index} (t={map.Objects[c.Index].StartTime}) : écart {c.Before:0} -> {c.After:0} px "
                        + $"(médianes du stream {c.MedianBefore:0} -> {c.MedianAfter:0} px)"));
                stats.Add(map, result, reverted, layout, streamCuts.Count, violation, worse, inside, plannedOutside,
                    watch.Elapsed.TotalMilliseconds);
                return (patched, result, layout);
            }

            try
            {
                foreach (var k in Factors)
                {
                    var generated = Generate(new AmplifyOptions(k, true), perK[k], $"k={k}");
                    if (generated is { } g && k == Factors[^1])
                    {
                        starsBefore += entry.StarRating;
                        starsAfter += Stars.Calculate(g.Patched).Total;
                    }
                    if (generated is { } plain && k == OptionsFactor)
                        jumpTurns[0].Add(map, plain.Layout);
                }

                // Kakudai 2 spec §5.2, at k = 1: the angles still change, and no overlap appears (none without the option).
                Generate(new AmplifyOptions(1, true, AccentAngles: true), anglesAt1, "k=1 angles");

                // Kakudai 2 spec §5.2, at k = 2: the turns between jumps sharper, as clean, no more overlaps.
                if (Generate(new AmplifyOptions(OptionsFactor, true, AccentAngles: true), angles, $"k={OptionsFactor} angles")
                    is { } accented)
                    jumpTurns[1].Add(map, accented.Layout);

                // Kakudai 2 spec §5.3, at k = 2: the objects outside the kiai sections stay exactly in place.
                if (map.Objects.Any(o => o.InKiai))
                {
                    kiaiMaps++;
                    if (Generate(new AmplifyOptions(OptionsFactor, true, KiaiOnly: true), kiai, $"k={OptionsFactor} kiai")
                        is { } inKiai)
                        for (int i = 0; i < map.Objects.Count; i++)
                        {
                            var (o, p) = (map.Objects[i], inKiai.Result.Placements[i]);
                            if (o.InKiai) kiaiObjects++;
                            else if (p != new Placement(o.Start, 0))
                                movedOutsideKiai.Add($"{entry.Display} — objet {i} (t={o.StartTime}) déplacé hors kiai : {o.Start} -> {p}");
                        }
                }

                // k = 1: only the 4 header lines change.
                var identityResult = Amplifier.Amplify(map.Objects, map.BeatLengthAt, new AmplifyOptions(1, true));
                var identityVersion = Trainer.Resolve(trainerBase, new TrainerSettings(Factor: 1)).Version;
                CheckText(entry.Display, map.Lines, OsuPatcher.Patch(map, identityResult, identityVersion).Split('\n'),
                    identityVersion, identity, onlyHeader: true);
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Display} : {ex}");
            }
        }

        int inSubfolder = library.Entries.Count(e => HasFolder(e.AudioFile) || HasFolder(e.BackgroundFile));
        var log = TestContext.Out;
        log.WriteLine($"Bibliothèque : {library.Entries.Count} diffs osu!, échantillon de {sample.Count} (Random(7)).");
        log.WriteLine($"Maps vérifiées : {checkedMaps} ; ignorées : {unsupported.Count} non prises en charge, {unreadable.Count} illisibles.");
        log.WriteLine($".osu générés refusés à la relecture par MapLoader : {refused.Count}.");
        foreach (var k in Factors)
            log.WriteLine(perK[k].Summary(k, checkedMaps));
        log.WriteLine("Angles accentués (spec Kakudai 2 §5.2) : " + anglesAt1.Summary(1, checkedMaps));
        log.WriteLine("Angles accentués (spec Kakudai 2 §5.2) : " + angles.Summary(OptionsFactor, checkedMaps));
        log.WriteLine($"Virages entre deux sauts (k={OptionsFactor}, .osu relu) : sans l'option {jumpTurns[0].Summary()} ; "
                      + $"avec {jumpTurns[1].Summary()}.");
        log.WriteLine($"Seulement les kiai (spec Kakudai 2 §5.3) : {kiaiMaps} maps avec du kiai, {kiaiObjects} objets en kiai ; "
                      + $"{movedOutsideKiai.Count} objets hors kiai déplacés ; " + kiai.Summary(OptionsFactor, kiaiMaps));
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Étoiles moyennes : {(checkedMaps == 0 ? 0 : starsBefore / checkedMaps):0.00} (base du jeu) -> "
            + $"{(checkedMaps == 0 ? 0 : starsAfter / checkedMaps):0.00} (k={Factors[^1]})."));
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Amplification la plus lente : {slowest.Ms:0} ms — {slowest.Map}."));
        log.WriteLine($"Diffs de la bibliothèque dont l'audio ou le fond est dans un sous-dossier : {inSubfolder}.");
        List("Non prises en charge", unsupported);
        List("Illisibles", unreadable);
        List("Violations (.osu relu)", violations);
        List("Dépassements aggravés (.osu relu)", worsened);
        List("Refusés à la relecture par MapLoader", refused);
        List("Placements prévus hors playfield (diagnostic)", planned);
        List("Coupures de stream (.osu relu)", cuts);
        List("Coupures de stream avec les options (.osu relu)", optionCuts);
        List("Identité", identity);
        List("Objets hors kiai déplacés", movedOutsideKiai);
        List("Exceptions", failures);

        Assert.Multiple(() =>
        {
            Assert.That(checkedMaps, Is.GreaterThanOrEqualTo(MinChecked), "maps vérifiées");
            Assert.That(failures, Is.Empty, "exceptions autres que UnsupportedMapException");
            Assert.That(identity, Is.Empty, "temps, lignes ou en-tête modifiés");
            Assert.That(refused, Is.Empty, ".osu générés refusés à la relecture");
            foreach (var k in Factors)
                Assert.That(perK[k].CleanMaps / (double)checkedMaps, Is.GreaterThanOrEqualTo(MinCleanShare),
                    $"part des maps dont le .osu relu est sans violation (k={k})");
            Assert.That(angles.CleanMaps / (double)checkedMaps, Is.GreaterThanOrEqualTo(MinCleanShare),
                "part des maps sans violation, angles accentués");
            Assert.That(angles.Overlaps, Is.LessThanOrEqualTo(perK[OptionsFactor].Overlaps),
                "chevauchements avec les angles accentués");
            Assert.That(anglesAt1.Overlaps, Is.Zero, "chevauchements avec les angles accentués à k = 1 (aucun sans l'option)");
            Assert.That(anglesAt1.CleanMaps / (double)checkedMaps, Is.GreaterThanOrEqualTo(MinCleanShare),
                "part des maps sans violation, angles accentués à k = 1");
            Assert.That(jumpTurns[1].Mean, Is.GreaterThan(jumpTurns[0].Mean + 5), "virages entre deux sauts accentués");
            Assert.That(movedOutsideKiai, Is.Empty, "objets hors kiai déplacés");
            Assert.That(kiai.CleanMaps / (double)Math.Max(1, kiaiMaps), Is.GreaterThanOrEqualTo(MinCleanShare),
                "part des maps sans violation, seulement les kiai");
        });
    }

    [Test, Explicit]
    public void RealMaps_ServiceGeneratesOsz()
    {
        var library = Library();
        var service = new KakudaiService(library, Path.Combine(temp, "generated"));
        int generated = 0;

        foreach (var entry in Shuffled(library))
        {
            if (generated == 5) break;
            MapInfo info;
            try { info = service.Select(entry.Id); }
            catch (UnsupportedMapException) { continue; }

            var settings = new TrainerSettings(Factor: 2.5);
            var version = service.Resolve(settings).Version;
            var osz = service.Generate(settings);
            generated++;

            using var zip = ZipFile.OpenRead(osz);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{info.Artist} - {info.Title} [{version}] : {names.Count} fichiers, {new FileInfo(osz).Length / 1024} Ko"));

            var osuEntries = zip.Entries.Where(e => e.FullName.EndsWith(".osu", StringComparison.Ordinal)).ToList();
            Assert.That(osuEntries, Has.Count.EqualTo(1));
            using (var reader = new StreamReader(osuEntries[0].Open()))
                Assert.That(reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')),
                    Does.Contain("Version:" + version));
            var full = library.WithFiles(entry);
            Assert.That(full.Files.Any(f => string.Equals(f.Filename, entry.AudioFile, StringComparison.OrdinalIgnoreCase)), Is.True,
                "audio absent des fichiers du set");
            Assert.That(names, Does.Contain(entry.AudioFile).IgnoreCase);
        }

        Assert.That(generated, Is.EqualTo(5));
    }

    /// <summary>
    /// Measures at one factor, with streams spaced, on the output read back. Like the prototype of the spec, the
    /// averages (jump, shape error, turns) are means of the per-map means, the 90th percentile pools every turn.
    /// </summary>
    private sealed class FactorStats
    {
        public int CleanMaps;
        private int maps, insideMaps, violationMaps, worsenedMaps, plannedMaps, jumpMaps, shapeMaps, turnMaps, streamMaps, cutMaps;
        private double jumpBefore, jumpAfter, shape, turn, streamTurn, milliseconds;
        private long objects, corrected, reduced, reverted, overlaps, moves, over400, cuts;
        private readonly List<double> turns = [];

        public long Overlaps => overlaps;

        public void Add(LoadedMap map, AmplifyResult r, int revertedBlocks, Layout layout, int streamCuts, bool violation,
            bool worse, bool inside, bool plannedOutside, double ms)
        {
            maps++;
            if (!violation) CleanMaps++;
            if (violation) violationMaps++;
            if (worse) worsenedMaps++;
            if (inside) insideMaps++;
            if (plannedOutside) plannedMaps++;
            milliseconds += ms;

            var jumps = ShapeMetrics.Jumps(map.Objects, layout);
            if (jumps.Moves > 0)
            {
                jumpMaps++;
                jumpBefore += jumps.Before;
                jumpAfter += jumps.After;
                moves += jumps.Moves;
                over400 += jumps.Over400;
            }

            var errors = ShapeMetrics.WindowErrors(map.Objects, map.BeatLengthAt, layout.Starts);
            if (errors.Count > 0)
            {
                shapeMaps++;
                shape += errors.Average();
            }

            var (all, stream) = ShapeMetrics.TurnErrors(map.Objects, map.BeatLengthAt, layout);
            if (all.Count > 0)
            {
                turnMaps++;
                turn += all.Average();
                turns.AddRange(all);
            }
            if (stream.Count > 0)
            {
                streamMaps++;
                streamTurn += stream.Average();
            }

            overlaps += ShapeMetrics.Overlaps(map.Objects, map.BeatLengthAt, layout);
            objects += map.Objects.Count;
            corrected += r.Corrected;
            reduced += r.Reduced;
            reverted += revertedBlocks;
            cuts += streamCuts;
            if (streamCuts > 0) cutMaps++;
        }

        public string Summary(double k, int checkedMaps)
        {
            turns.Sort();
            double p90 = turns.Count == 0 ? 0 : turns[(int)Math.Min(turns.Count - 1, 0.9 * turns.Count)];
            double Percent(double part, double whole) => whole == 0 ? 0 : 100 * part / whole;
            return string.Create(CultureInfo.InvariantCulture,
                $"k={k} : saut moyen {(jumpMaps == 0 ? 0 : jumpBefore / jumpMaps):0.0} px -> {(jumpMaps == 0 ? 0 : jumpAfter / jumpMaps):0.0} px "
                + $"(x{(jumpBefore == 0 ? 0 : jumpAfter / jumpBefore):0.00}) ; erreur de forme (similitude avec rotation, ±{ShapeMetrics.WindowBeats} temps) "
                + $"{(shapeMaps == 0 ? 0 : shape / shapeMaps):0.000} ; virages faussés {(turnMaps == 0 ? 0 : turn / turnMaps):0.0}° "
                + $"(90e centile {p90:0}°) ; virages dans les streams {(streamMaps == 0 ? 0 : streamTurn / streamMaps):0.0}° ; "
                + $"coupures de stream {cuts} (dans {cutMaps} maps) ; "
                + $"sauts > 400 px {Percent(over400, moves):0.0} % ; chevauchements {1000.0 * overlaps / Math.Max(1, objects):0.0} ‰ ; "
                + $"maps propres {CleanMaps}/{checkedMaps} ({Percent(CleanMaps, checkedMaps):0.0} %), dont {insideMaps} entièrement dans le playfield, "
                + $"{violationMaps} en violation, {worsenedMaps} avec un dépassement aggravé, {plannedMaps} avec un placement prévu dehors ; "
                + $"objets corrigés {Percent(corrected, objects):0.0} %, réduits {Percent(reduced, objects):0.0} %, "
                + $"blocs remis en place {reverted} ; amplification {milliseconds / Math.Max(1, maps):0.0} ms par map en moyenne.");
        }
    }

    /// <summary>
    /// The turns between two jumps (Kakudai 2 spec §5.2): for every two consecutive moves both at least
    /// <see cref="ShapeMetrics.MinTurnMove"/> long in the original map, neither a stream gap (≤ 1.15·beat/4) nor touching
    /// a spinner, |turning angle| in degrees, before and after. Means of the per-map means, like the other averages.
    /// </summary>
    private sealed class JumpTurnStats
    {
        private int maps, turns;
        private double before, after;

        public double Mean => maps == 0 ? 0 : after / maps;

        public void Add(LoadedMap map, Layout layout)
        {
            var o = map.Objects;
            bool Jump(int i) => o[i].Kind != ObjectKind.Spinner && o[i - 1].Kind != ObjectKind.Spinner
                                && Vector2.Distance(o[i].Start, o[i - 1].End) >= ShapeMetrics.MinTurnMove
                                && o[i].StartTime - o[i - 1].EndTime > 1.15 / 4 * map.BeatLengthAt(o[i].StartTime);
            double sumBefore = 0, sumAfter = 0;
            int count = 0;
            for (int i = 2; i < o.Count; i++)
            {
                if (!Jump(i) || !Jump(i - 1)) continue;
                var a1 = layout.Starts[i - 1] - layout.Ends[i - 2];
                var b1 = layout.Starts[i] - layout.Ends[i - 1];
                if (a1.Length() < 1 || b1.Length() < 1) continue;
                sumBefore += Math.Abs(Turn(o[i - 1].Start - o[i - 2].End, o[i].Start - o[i - 1].End));
                sumAfter += Math.Abs(Turn(a1, b1));
                count++;
            }
            if (count == 0) return;
            maps++;
            turns += count;
            before += sumBefore / count;
            after += sumAfter / count;
        }

        public string Summary() => string.Create(CultureInfo.InvariantCulture,
            $"{(maps == 0 ? 0 : before / maps):0.0}° -> {Mean:0.0}° ({turns} virages, {maps} maps)");

        private static double Turn(Vector2 a, Vector2 b) => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y) * 180 / Math.PI;
    }

    /// <summary>
    /// The stream cuts with only the kiai sections moved: a stream across the edge of a kiai section is split there by
    /// design (Kakudai 2 spec §5.3), its part outside the kiai left in place, so the cuts are counted on each part
    /// alone, and the gap at the edge is not one.
    /// </summary>
    private static List<ShapeMetrics.StreamCut> KiaiStreamCuts(LoadedMap map, Layout layout)
    {
        var all = new List<ShapeMetrics.StreamCut>();
        var o = map.Objects;
        for (int from = 0, to; from < o.Count; from = to + 1)
        {
            to = from;
            while (to + 1 < o.Count && o[to + 1].InKiai == o[from].InKiai) to++;
            var part = new Layout(layout.Starts[from..(to + 1)], layout.Ends[from..(to + 1)]);
            foreach (var c in ShapeMetrics.StreamCuts(o.Skip(from).Take(to - from + 1).ToList(), map.BeatLengthAt, part))
                all.Add(c with { Index = c.Index + from });
        }
        return all;
    }

    private static void List(string title, List<string> items)
    {
        if (items.Count == 0) return;
        TestContext.Out.WriteLine($"{title} ({items.Count}) :");
        foreach (var item in items.Take(MaxListed)) TestContext.Out.WriteLine("  " + item);
        if (items.Count > MaxListed) TestContext.Out.WriteLine($"  … et {items.Count - MaxListed} de plus");
    }

    private static bool HasFolder(string file) => file.Contains('/') || file.Contains('\\');

    /// <summary>Smallest distance from the planned path of <paramref name="o"/> to the edge of the playfield.</summary>
    private static float PlannedMargin(MapObject o, Placement p)
        => Points(p.Start, o.PathSamples, p.Rotation).Min(q => Math.Min(Math.Min(q.X, 512 - q.X), Math.Min(q.Y, 384 - q.Y)));

    private static bool Inside(Vector2 p) => p.X >= 0 && p.X <= 512 && p.Y >= 0 && p.Y <= 384;

    /// <summary>Whether the object, in the original map, lies entirely inside the playfield.</summary>
    private static bool WasInside(MapObject o) => Points(o.Start, o.PathSamples, 0).All(Inside);

    /// <summary>The head, then every path sample rotated by <paramref name="rotation"/> (R3) around it.</summary>
    private static Vector2[] Points(Vector2 start, IReadOnlyList<Vector2> samples, float rotation)
    {
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
        return [start, .. samples.Select(s => start + new Vector2(cos * s.X - sin * s.Y, sin * s.X + cos * s.Y))];
    }

    /// <summary>
    /// First point outside the playfield and outside the original bounding box of <paramref name="o"/> (which is
    /// inside the playfield unless the object already went past the edge).
    /// </summary>
    private static Vector2? FirstViolation(MapObject o, Vector2[] points)
    {
        var original = Points(o.Start, o.PathSamples, 0);
        float minX = original.Min(p => p.X), maxX = original.Max(p => p.X);
        float minY = original.Min(p => p.Y), maxY = original.Max(p => p.Y);
        foreach (var p in points)
            if (!Inside(p) && !(p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY))
                return p;
        return null;
    }

    /// <summary>
    /// The generated text must keep every line except the 4 rewritten header lines (Tags, BeatmapID and
    /// BeatmapSetID may be added when missing) and, unless <paramref name="onlyHeader"/>, the positions of the
    /// [HitObjects] lines; times and every other field stay the same.
    /// </summary>
    private static void CheckText(string map, string[] original, string[] patched, string version,
        List<string> identity, bool onlyHeader)
    {
        string label = onlyHeader ? "k=1" : "k=3";
        var (oldBody, oldHeader) = Split(original);
        var (newBody, newHeader) = Split(patched);

        int missing = AddedKeys.Count(k => !oldHeader.ContainsKey(k));
        if (patched.Length != original.Length + missing)
            identity.Add($"{map} ({label}) : {patched.Length} lignes au lieu de {original.Length + missing}");

        // Tags keep their original bytes, " kakudai" added at the end.
        var oldTags = oldHeader.GetValueOrDefault("Tags") ?? "";
        var expectedTags = oldTags.Trim().Length == 0 ? "kakudai" : oldTags + " kakudai";
        if (newHeader.GetValueOrDefault("Version") != version || newHeader.GetValueOrDefault("BeatmapID") != "0"
            || newHeader.GetValueOrDefault("BeatmapSetID") != "-1" || newHeader.GetValueOrDefault("Tags") != expectedTags)
            identity.Add($"{map} ({label}) : en-tête inattendu "
                         + string.Join(" ; ", newHeader.Select(h => $"{h.Key}:{h.Value}")));

        if (oldBody.Count != newBody.Count)
        {
            identity.Add($"{map} ({label}) : {newBody.Count} lignes hors en-tête au lieu de {oldBody.Count}");
            return;
        }

        for (int j = 0; j < oldBody.Count; j++)
        {
            var (section, before) = oldBody[j];
            var after = newBody[j].Line;
            if (before == after) continue;
            if (onlyHeader || section != "HitObjects")
            {
                identity.Add($"{map} ({label}) : ligne [{section}] modifiée « {before.TrimEnd('\r')} » -> « {after.TrimEnd('\r')} »");
                continue;
            }
            if (DiffersBeyondPosition(before, after) is string field)
                identity.Add($"{map} ({label}) : {field} modifié « {before.TrimEnd('\r')} » -> « {after.TrimEnd('\r')} »");
        }
    }

    /// <summary>Name of the first field of a hit object line that changed although it is not a position.</summary>
    private static string? DiffersBeyondPosition(string before, string after)
    {
        var a = before.Split(',');
        var b = after.Split(',');
        if (a.Length != b.Length || a.Length < 4) return "nombre de champs";
        bool slider = int.TryParse(a[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) && (type & 2) != 0;

        for (int f = 2; f < a.Length; f++)
        {
            if (a[f] == b[f]) continue;
            if (f == 5 && slider && SameCurveTypes(a[f], b[f])) continue;
            return f == 2 ? "temps" : $"champ {f + 1}";
        }
        return null;
    }

    /// <summary>Same number of curve tokens, and the same tokens wherever they are not x:y points.</summary>
    private static bool SameCurveTypes(string before, string after)
    {
        var a = before.Split('|');
        var b = after.Split('|');
        return a.Length == b.Length && a.Zip(b).All(t => t.First.Contains(':') || t.First == t.Second);
    }

    /// <summary>
    /// Splits a .osu into the lines that must stay as they are, with their section, and the raw values (text after
    /// the colon, line ending removed) of the 4 [Metadata] keys the patcher rewrites.
    /// </summary>
    private static (List<(string Section, string Line)> Body, Dictionary<string, string> Header) Split(string[] lines)
    {
        var body = new List<(string, string)>();
        var header = new Dictionary<string, string>();
        string section = "";

        foreach (var line in lines)
        {
            var t = line.Trim();
            var content = line.TrimEnd('\r');
            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
                section = t[1..^1];
            else if (section == "Metadata" && content.IndexOf(':') is int colon and > 0
                     && content[..colon].Trim() is "Version" or "Tags" or "BeatmapID" or "BeatmapSetID")
            {
                header[content[..colon].Trim()] = content[(colon + 1)..];
                continue;
            }
            body.Add((section, line));
        }

        return (body, header);
    }
}
