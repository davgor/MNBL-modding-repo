using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

// MMR-64 periodic coordinate and distance contract tests, without the game:
//   PeriodicCoordinateTests REPO BUNDLE GAME_ROOT OUT_JSON
// Canonicalization (the range ends exactly, many wraps, negatives, single precision at the seam), the tie rule, images, poles and their
// aliases, logical <-> physical round trips, display copies and identities, seam splitting, directed cache keys, save compatibility, the
// policy file, the production layout of a real compiled bundle, and the distance contract: ShortestOverImages over a planar grid "engine"
// against a periodic grid oracle, a blocked corridor and incompatible land/sea capabilities (proximity is not reachability), directed
// transition costs, and the coverage manifest against the pinned TaleWorlds.CampaignSystem and NavalDLC assemblies.
internal static class PeriodicCoordinateTests
{
    private static int checks, failures;
    private static readonly List<object> Rows = new List<object>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static string gameRoot;

    private static void Check(bool condition, string label, object detail = null)
    {
        checks++; if (!condition) failures++;
        Console.WriteLine((condition ? "ok    " : "FAIL  ") + label + (detail == null || condition ? "" : "  [" + Serializer.Serialize(detail) + "]"));
        Rows.Add(new Dictionary<string, object> { ["check"] = label, ["passed"] = condition, ["detail"] = detail });
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; } catch { return false; }
    }

    private static bool Near(double a, double b, double tolerance = 1e-9) { return Math.Abs(a - b) <= tolerance; }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: PeriodicCoordinateTests REPO BUNDLE GAME_ROOT OUT_JSON"); return 2; }
        gameRoot = args[2];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { "bin/Win64_Shipping_Client", "Modules/NavalDLC/bin/Win64_Shipping_Client", "Modules/SandBox/bin/Win64_Shipping_Client" })
            {
                string path = Path.Combine(gameRoot, dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.periodic-coordinates", ["version"] = 1, ["ticket"] = "MMR-64",
            ["policyVersion"] = PeriodicWorldCoordinates.PolicyVersion, ["distanceVersion"] = PeriodicDistanceModel.Version };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["rows"] = Rows;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0];
        // The production layout (W 800, H 400, strips 400, pole margin 600, terrain 1600, 128 source columns).
        var p = new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 128);
        Canonical(p);
        Ties(p);
        Images(p);
        Poles(p);
        Frames(p);
        Identity(p, evidence);
        Splitting(p);
        Keys(p);
        Saves(p);
        Policy(repo, p);
        Layout(args[1], evidence);
        Wide();
        Oracle(evidence);
        Corridor();
        Capability();
        Coverage(evidence);
        SpatialTable(evidence);   // MMR-65
    }

    // ---- canonical x ----
    private static void Canonical(PeriodicWorldCoordinates p)
    {
        Check(p.CanonicalX(p.MinX) == p.MinX && p.IsCanonicalX(p.MinX), "xmin is canonical and maps to itself exactly");
        Check(p.CanonicalX(p.MaxX) == p.MinX && !p.IsCanonicalX(p.MaxX), "xmax is not canonical and maps to xmin (the range is [xmin, xmax))");
        Check(p.CanonicalX(1199.999999) == 1199.999999 && p.CanonicalX(400.0000001) == 400.0000001, "values just inside either end are returned exactly");
        double justBelow = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(400.0) - 1);
        Check(p.CanonicalX(justBelow) < 1200 && p.IsCanonicalX(p.CanonicalX(justBelow)), "the double just below xmin canonicalizes to a canonical value (xmin or just below xmax), never xmax",
            new { below = p.CanonicalX(justBelow) });
        // The MMR-63 single-precision computation (PeriodicWorld.Canonical before MMR-64), on the float just below xmin.
        float fx = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(400f), 0) - 1), 0);
        Func<float, float> mmr63 = x => { float t = (x - 400f) % 800f; if (t < 0) t += 800f; return 400f + t; };
        Check(fx < 400f && mmr63(fx) == 1200f && p.CanonicalX(fx) >= 400f && p.CanonicalX(fx) < 1200f,
            "single precision: the float just below xmin (" + fx.ToString("R", CultureInfo.InvariantCulture) + ") stays canonical; the MMR-63 computation returned xmax",
            new { fx, mmr63 = mmr63(fx), fixedValue = p.CanonicalX(fx) });
        Check(Near(p.CanonicalX(400 + 5 * 800 + 3), 403) && Near(p.CanonicalX(400 + 3 - 7 * 800), 403) && Near(p.CanonicalX(1203), 403) && Near(p.CanonicalX(399), 1199),
            "multiple wraps east and west land on the same canonical x");
        Check(Near(p.CanonicalX(-3), 797) && Near(p.CanonicalX(-803), 797) && Near(p.CanonicalX(-1600 + 400), 400), "negative coordinates canonicalize");
        Check(p.WrapIndex(401) == 0 && p.WrapIndex(1201) == 1 && p.WrapIndex(399) == -1 && Near(p.DisplayOffset(1250), 800) && Near(p.DisplayOffset(10), -800) && p.WrapIndex(1200) == 1,
            "wrap index and display offset name the copy a physical x lies in");
        Check(Throws<ArgumentException>(() => p.CanonicalX(double.NaN)) && Throws<ArgumentException>(() => p.CanonicalX(double.PositiveInfinity)), "non-finite coordinates are refused");
        Check(Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 400, 300, 600, 1600)) && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 400, 400, 600, 1500))
              && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800.0001, 400, 400.0001, 600, 1600.001)) && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 1100, 400, 600, 1600))
              && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 128, double.NaN)) && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 400, 400, 600, double.PositiveInfinity))
              && Throws<ArgumentException>(() => new PeriodicWorldCoordinates(800, 400, 400, 600.0001, 1600)),
            "layouts without width/2 strips, a width or origin that is not whole key quanta, rows outside the terrain, or non-finite values are refused");
    }

    // ---- tie rule ----
    private static void Ties(PeriodicWorldCoordinates p)
    {
        Check(p.DeltaX(500, 900) == 400 && p.DeltaX(900, 500) == 400 && p.DeltaX(450, 1250) == 0, "an exact half-width difference resolves east (+W/2) in both directions; a whole width is 0");
        Check(Near(p.DeltaX(1190, 410), 20) && Near(p.DeltaX(410, 1190), -20) && Near(p.DeltaX(400, 1199.5), -0.5), "the short way across the seam, signed");
        var w1000 = new PeriodicWorldCoordinates(1000, 500, 500, 0, 2000);
        double ax = w1000.ToPhysical(998, 200)[0], bx = w1000.ToPhysical(2, 200)[0];
        Check(Near(w1000.DeltaX(ax, bx), 4) && Near(w1000.GeometricDistance(ax, 200, bx, 200), 4) && Near(Math.Abs(bx - ax), 996),
            "width 1000: (998,200) and (2,200) are 4 apart, not 996", new { delta = w1000.DeltaX(ax, bx), planar = Math.Abs(bx - ax) });
        var first = w1000.Placements(ax, bx)[0];
        Check(Near(first.TargetX, ax + 4) && first.TargetShift == 1 && first.StartShift == 0 && Near(first.Delta, 4), "the nearest placement of (2,200) from (998,200) is 4 east, the +W copy");
        double a = 500, east = a + 400, west = east - 800;   // the same location, given as its canonical copy (900) or its west strip copy (100)
        var fromEast = p.Placements(a, east); var fromWest = p.Placements(a, west);
        Check(fromEast[0].TargetX == 900 && fromWest[0].TargetX == 900 && fromEast.Select(i => i.TargetX).SequenceEqual(fromWest.Select(i => i.TargetX)),
            "a half-width target gives the same placement order whichever copy is passed (east first)", new { east = fromEast.Select(i => i.TargetX), west = fromWest.Select(i => i.TargetX) });
        var random = new Random(642); int disagree = 0;
        for (int i = 0; i < 200000; i++)
        {
            double from = 400 + random.NextDouble() * 800, to = from + 400 + (random.Next(3) - 1) * Math.Pow(2, -random.Next(30, 52)) * 400 + 800 * (random.Next(3) - 1);
            if (p.Placements(from, to)[0].Delta != p.DeltaX(from, to)) disagree++;
        }
        Check(disagree == 0, "near half-width ties (200000 double pairs within 2^-30 of the tie) the first placement's displacement is exactly DeltaX", new { disagree });
    }

    // ---- images ----
    private static void Images(PeriodicWorldCoordinates p)
    {
        Check(p.Images(800).Select(i => i.X).SequenceEqual(new double[] { 800, 1600, 0 }), "x = 800 has three copies inside the closed terrain [0, 1600]");
        Check(p.Images(401).Select(i => i.X).SequenceEqual(new double[] { 401, 1201 }) && p.Images(1199).Select(i => i.X).SequenceEqual(new double[] { 1199, 399 }),
            "near the seam a location has its canonical copy and one strip copy");
        Check(p.Images(1201).Select(i => i.X).SequenceEqual(p.Images(401).Select(i => i.X)) && p.Images(1201)[0].Shift == 0, "the images of any copy are those of the canonical location");
        int pairs = 0; var wrong = new List<object>();
        for (double from = 400; from < 1200; from += 12.5)
            for (double target = 400; target < 1200; target += 12.5)
            {
                pairs++;
                var placements = p.Placements(from, target);
                double d0 = p.DeltaX(from, target);
                var deltas = new[] { d0, d0 + 800, d0 - 800 }.Where(d => Math.Abs(d) < 800).OrderBy(d => Math.Abs(d)).ThenByDescending(d => d).ToList();
                bool ok = placements.Count >= deltas.Count && placements[0].StartShift == 0 && placements[0].Delta == d0
                          && deltas.All(d => placements.Any(q => q.Delta == d)) && placements.All(q => q.FromX >= 0 && q.FromX <= 1600 && q.TargetX >= 0 && q.TargetX <= 1600
                              && Near(q.TargetX - q.FromX, q.Delta) && Near(p.CanonicalX(q.TargetX), p.CanonicalX(target)) && Near(p.CanonicalX(q.FromX), from) && Near(q.FromX - from, q.StartShift * 800));
                if (!ok && wrong.Count < 3) wrong.Add(new { from, target, placements = placements.Select(q => new[] { q.FromX, q.TargetX, q.Delta }) });
            }
        Check(wrong.Count == 0, "from every canonical start (" + pairs + " pairs) the first placement is the short way (DeltaX, no move) and every displacement shorter than a width is placed inside the terrain", wrong);
        var longWays = p.Placements(1100, 1150);
        Check(longWays.Count == 3 && longWays[1].Delta == -750 && longWays[1].StartShift == 0 && longWays[2].Delta == 850 && longWays[2].StartShift == -1 && longWays[2].FromX == 300 && longWays[2].TargetX == 1150,
            "the long ways around: west 750 from the start itself, east 850 from the start's west copy (its target copy 1950 lies outside the terrain)",
            longWays.Select(q => new[] { q.FromX, q.TargetX, q.Delta }));
        var fromStrip = p.Placements(1600, 900)[0];
        Check(fromStrip.FromX == 800 && fromStrip.TargetX == 900 && fromStrip.StartShift == -1 && fromStrip.Delta == 100,
            "a start in the strip (x = 1600) whose nearest target copy (1700) lies outside the terrain is moved a width west (800 -> 900)");
    }

    // ---- poles ----
    private static void Poles(PeriodicWorldCoordinates p)
    {
        Check(p.PoleOf(600) == PeriodicWorldCoordinates.Pole.North && p.PoleOf(550) == PeriodicWorldCoordinates.Pole.North && p.PoleOf(1000) == PeriodicWorldCoordinates.Pole.South
              && p.PoleOf(1200) == PeriodicWorldCoordinates.Pole.South && p.PoleOf(601) == PeriodicWorldCoordinates.Pole.None, "the pole lines and the padding beyond them are poles; the rows between are not");
        Check(p.ClampY(550) == 600 && p.ClampY(1250) == 1000 && p.ClampY(733.5) == 733.5 && p.ClampY(600) == 600 && p.ClampY(1000) == 1000,
              "MMR-278 (RT-19): the camera's y is held between the pole lines (600 and 1000 physical); y between them is unchanged");
        Check(p.LocationKey(410, 600) == "N" && p.LocationKey(1100, 600) == "N" && p.LocationKey(20, 590) == "N" && p.LocationKey(700, 1000) == "S",
            "every x on a pole line is one location (pole alias): the north key is N everywhere, the south S");
        Check(p.SourceUid(410, 600) == "N" && p.SourceUid(1100, 601) == "N" && p.SourceUid(700, 999.5) == "S" && p.SourceUid(400, 800) == "r64c0",
            "source uids collapse on the pole rows and name r<row>c<column> between", new { a = p.SourceUid(1100, 601), b = p.SourceUid(400, 800) });
        Check(p.LocationKey(410, 610) != p.LocationKey(1100, 610), "one row off the pole, different x are different locations");
        Check(p.SourceUid(1199.99, 700) == p.SourceUid(400, 700) && p.SourceUid(1196, 700) == "r32c127", "source columns wrap: the seam's last column rounds onto column 0",
            new { last = p.SourceUid(1196, 700) });
    }

    // ---- frames ----
    private static void Frames(PeriodicWorldCoordinates p)
    {
        bool ok = true; var bad = new List<object>();
        var random = new Random(64);
        for (int i = 0; i < 2000; i++)
        {
            double x = 400 + random.NextDouble() * 800, y = 600 + random.NextDouble() * 400;
            var l = p.ToLogical(x, y); var back = p.ToPhysical(l[0], l[1]);
            if (!Near(back[0], x, 1e-9) || !Near(back[1], y, 1e-9) || l[0] < 0 || l[0] >= 800) { ok = false; if (bad.Count < 3) bad.Add(new { x, y, back }); }
            var copy = p.ToLogical(x + 800 * (random.Next(3) - 1), y);
            if (copy[0] < 0 || copy[0] >= 800 || Math.Abs(p.DeltaX(copy[0], l[0])) > 1e-9 || !Near(copy[1], l[1], 1e-12)) { ok = false; if (bad.Count < 3) bad.Add(new { x, copy }); }
        }
        Check(ok, "physical -> logical -> physical round-trips 2000 canonical points; every copy has the same logical location", bad);
        Check(Near(p.ToPhysical(-10, 5)[0], 1190) && Near(p.ToPhysical(810, 5)[0], 410) && Near(p.ToPhysical(0, 0)[1], 600), "logical x wraps into canonical physical x; y adds the pole margin");
    }

    // ---- identity ----
    private static void Identity(PeriodicWorldCoordinates p, SortedDictionary<string, object> evidence)
    {
        var same = new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 128);
        var variants = new[] { new PeriodicWorldCoordinates(784, 400, 400, 600, 1600, 128), new PeriodicWorldCoordinates(800, 399, 400, 600, 1600, 128),
            new PeriodicWorldCoordinates(800, 400, 401, 600, 1601, 128), new PeriodicWorldCoordinates(800, 400, 400, 601, 1600, 128), new PeriodicWorldCoordinates(800, 400, 400, 600, 1601, 128),
            new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 64), new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 128, 10) };
        var frames = new[] { p }.Concat(variants).Select(v => v.FrameIdentity).ToList(); var full = new[] { p }.Concat(variants).Select(v => v.Identity).ToList();
        Check(same.Identity == p.Identity && same.FrameIdentity == p.FrameIdentity && frames.Distinct().Count() == frames.Count && full.Distinct().Count() == full.Count && p.Identity.Length == 64,
            "both identities are stable for a layout and change with each layout field (width, height, origins, extent, source columns, central meridian)");
        Check(p.PolicyText.StartsWith(p.FrameText, StringComparison.Ordinal) && !p.FrameText.Contains("distance=") && p.PolicyText.Contains("distance=" + PeriodicDistanceModel.Version)
              && p.FrameText.Contains("version=" + PeriodicWorldCoordinates.PolicyVersion) && p.FrameText.Contains("tie=east") && p.FrameIdentity != p.Identity,
            "the frame text (the save identity) names the coordinate version, tie rule and layout but not the distance model; the policy text adds the metric and distance version");
        evidence["productionPolicyText"] = p.PolicyText; evidence["productionIdentity"] = p.Identity; evidence["productionFrameIdentity"] = p.FrameIdentity;
        var keys = new[] { 410.0, 410 + 800, 410 - 800 }.Select(x => p.LocationKey(x, 700)).Distinct().ToList();
        var uids = new[] { 410.0, 1210.0 }.Select(x => p.SourceUid(x, 700)).Distinct().ToList();
        Check(keys.Count == 1 && uids.Count == 1, "display copies of a location computed in double share one location key and one source uid");
        // The documented limit: keys come from canonical state. A copy rounded to single precision can land in the neighbouring quantum.
        var r = new Random(6464); int differ = 0, n = 100000;
        for (int i = 0; i < n; i++) { float x = (float)(400 + r.NextDouble() * 800); float copy = x + 800f; if (p.LocationKey(x, 700) != p.LocationKey(copy, 700)) differ++; }
        evidence["singlePrecisionCopyKeyDisagreement"] = new Dictionary<string, object> { ["samples"] = n, ["differ"] = differ,
            ["note"] = "LocationKey of a float position x and of its float copy x + 800f; keys are built from canonical campaign state, never from a display point" };
    }

    // ---- seam splitting ----
    private static void Splitting(PeriodicWorldCoordinates p)
    {
        var a = p.SplitInterval(790, 810);
        Check(a.Count == 2 && Near(a[0][0], 790) && Near(a[0][1], 800) && Near(a[1][0], 0) && Near(a[1][1], 10), "an interval across the seam splits into [790, 800] and [0, 10]");
        var b = p.SplitInterval(-10, 5);
        Check(b.Count == 2 && Near(b[0][0], 790) && Near(b[0][1], 800) && Near(b[1][1], 5), "a negative interval start wraps before splitting");
        Check(p.SplitInterval(10, 20).Count == 1 && p.SplitInterval(5, 805).Count == 1 && Near(p.SplitInterval(5, 805)[0][1], 800) && p.SplitInterval(790, 800).Count == 1,
            "an interval inside the range, a whole width, or one ending on the seam is one piece");
        Check(Throws<ArgumentException>(() => p.SplitInterval(10, 5)) && Throws<ArgumentException>(() => p.SplitInterval(0, 900)), "a reversed or longer-than-width interval is refused");
        var s = p.SplitSegment(new double[] { 795, 100 }, new double[] { 5, 200 });
        Check(s.Count == 2 && Near(s[0][1][0], 800) && Near(s[0][1][1], 150) && Near(s[1][0][0], 0) && Near(s[1][0][1], 150) && Near(s[1][1][0], 5),
            "a step east across the seam ends at x = 800 and continues from x = 0 at the interpolated y");
        var w = p.SplitSegment(new double[] { 5, 0 }, new double[] { 795, 100 });
        Check(w.Count == 2 && Near(w[0][1][0], 0) && Near(w[0][1][1], 50) && Near(w[1][0][0], 800) && Near(w[1][1][0], 795), "a step west across the seam ends at x = 0 and continues from x = 800");
        var t = p.SplitSegment(new double[] { 100, 0 }, new double[] { 500, 10 });
        Check(t.Count == 1 && Near(t[0][1][0], 500), "a half-width step goes east (the tie rule) and stays one piece");
        var u = p.SplitSegment(new double[] { 600, 0 }, new double[] { 200, 10 });
        Check(u.Count == 2 && Near(u[0][1][0], 800) && Near(u[1][1][0], 200), "the other half-width step also goes east, across the seam");
        Check(p.SplitSegment(new double[] { 790, 0 }, new double[] { 0, 0 }).Count == 1, "a step ending exactly on the seam is one piece");
        var fromSeam = p.SplitSegment(new double[] { 0, 0 }, new double[] { 790, 10 });
        Check(fromSeam.Count == 1 && fromSeam[0][0][0] == 800 && Near(fromSeam[0][1][0], 790), "a step starting on the seam and going west is one piece starting at x = 800 (no zero-length piece)",
            fromSeam.Select(q => new[] { q[0][0], q[1][0] }));
    }

    // ---- cache keys ----
    private static void Keys(PeriodicWorldCoordinates p)
    {
        string ab = p.CacheKey("Naval", 410, 700, true, 1190, 700, false), ba = p.CacheKey("Naval", 1190, 700, false, 410, 700, true);
        Check(ab != ba && ab.StartsWith(PeriodicWorldCoordinates.PolicyVersion + "|" + p.Identity.Substring(0, 16) + "|Naval|", StringComparison.Ordinal),
            "cache keys are directed and carry the policy version and identity", new { ab, ba });
        Check(ab == p.CacheKey("Naval", 1210, 700, true, 390, 700, false), "display copies give the same cache key");
        Check(ab != p.CacheKey("Default", 410, 700, true, 1190, 700, false) && ab != p.CacheKey("Naval", 410, 700, false, 1190, 700, false), "the capability and the regions are part of the key");
        Check(ab != new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 64).CacheKey("Naval", 410, 700, true, 1190, 700, false), "a different policy identity gives different keys");
        Check(Throws<ArgumentException>(() => p.CacheKey("a|b", 0, 0, true, 0, 0, true)), "a capability containing the separator is refused");
    }

    // ---- save compatibility ----
    private static void Saves(PeriodicWorldCoordinates p)
    {
        Func<string, string, Dictionary<string, string>> header = (periodic, policy) => {
            var h = new Dictionary<string, string>(); if (periodic != null) h["periodic"] = periodic; if (policy != null) h["coordinatePolicy"] = policy; return h; };
        Check(!Throws<Exception>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("true", p.FrameIdentity), p)), "a periodic save made under this coordinate frame is accepted");
        Check(Throws<InvalidDataException>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("true", p.Identity), p)), "the save key is the frame identity, not the full policy identity");
        Check(Throws<InvalidDataException>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("true", new string('0', 64)), p)), "a periodic save made under another policy is refused");
        Check(Throws<InvalidDataException>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("true", null), p)), "a periodic save without a policy (MMR-63 era) is refused");
        Check(Throws<InvalidDataException>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("false", "none"), p))
              && Throws<InvalidDataException>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("true", p.FrameIdentity), null)), "a header and world that disagree on periodicity are refused");
        Check(!Throws<Exception>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header("false", "none"), null)) && !Throws<Exception>(() => PeriodicWorldCoordinates.RequireSavedPolicy(header(null, null), null)),
            "a non-periodic save (with or without the MMR-64 keys) is accepted for a non-periodic world");
    }

    // ---- policy file ----
    private static void Policy(string repo, PeriodicWorldCoordinates p)
    {
        var policy = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/periodic-coordinates.json")).TrimStart('﻿'));
        var tie = (IDictionary<string, object>)policy["tieRule"]; var metric = (IDictionary<string, object>)policy["metric"]; var distortion = (IDictionary<string, object>)policy["distortion"];
        Check((string)policy["version"] == PeriodicWorldCoordinates.PolicyVersion && (string)policy["distanceVersion"] == PeriodicDistanceModel.Version && (string)tie["name"] == PeriodicWorldCoordinates.TieRule
              && (string)metric["name"] == PeriodicWorldCoordinates.Metric, "worldgen/policies/periodic-coordinates.json names the code's versions, tie rule and metric");
        Check(Near(Convert.ToDouble(distortion["at60deg"]), 1 / Math.Cos(Math.PI / 3), 1e-9) && Near(Convert.ToDouble(distortion["at80deg"]), 1 / Math.Cos(80 * Math.PI / 180), 1e-3)
              && Near(Convert.ToDouble(distortion["at30deg"]), 1 / Math.Cos(Math.PI / 6), 1e-3), "the recorded distortion factors are 1/cos(latitude)");
    }

    // ---- the production layout of a real bundle ----
    private static void Layout(string bundleDirectory, SortedDictionary<string, object> evidence)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(bundleDirectory, host);
        var surface = WorldSurface.FromBundle(bundle);
        var c = PeriodicWorldCoordinates.FromSurface(surface);
        evidence["bundle"] = new Dictionary<string, object> { ["worldContentId"] = bundle.WorldContentId, ["layout"] = c.Record() };
        Check(c.Width == 800 && c.Height == 400 && c.OriginX == 400 && c.OriginY == 600 && c.Extent == 1600 && c.SourceColumns == 128 && c.CentralMeridianDeg == 0,
            "bundle " + bundle.WorldContentId.Substring(0, 12) + ": the coordinates come from its projection (W 800, H 400, strip 400, pole margin 600, terrain 1600, 128 source columns)", c.Record());
        var projection = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/projection.json")).TrimStart('﻿'));
        var canonical = ((object[])((IDictionary<string, object>)projection["periodicLayout"])["canonicalPhysicalX"]).Select(Convert.ToDouble).ToArray();
        Check(canonical[0] == c.MinX && canonical[1] == c.MaxX, "the bundle's recorded canonical physical x range is [xmin, xmax)");
        Check(c.Identity == new PeriodicWorldCoordinates(800, 400, 400, 600, 1600, 128).Identity, "the bundle's policy identity is the production layout's");
        // Every settlement's source node (its uid names it: node = 1 + (row - 1) * 128 + column) is the SourceUid of its source point.
        var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")).TrimStart('\uFEFF'));
        int settlements = 0, matched = 0, synthesized = 0; var mismatches = new List<object>();
        foreach (IDictionary<string, object> s in (object[])graph["settlements"])
        {
            // A village the quota synthesized (MMR-252) has no source node: its uid names its fortification's.
            if (((string)s["uid"]).StartsWith("mnbl-quota-village:", StringComparison.Ordinal)) { synthesized++; continue; }
            settlements++;
            var m = System.Text.RegularExpressions.Regex.Match((string)s["uid"], @"(?:node-|surface-city-\d+-)(\d+)");
            var point = (IDictionary<string, object>)s["sourcePoint"];
            var physical = c.ToPhysical(Convert.ToDouble(point["x"]), Convert.ToDouble(point["y"]));
            if (!m.Success) { if (mismatches.Count < 3) mismatches.Add(s["uid"]); continue; }
            int node = Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string expected = "r" + ((node - 1) / 128 + 1).ToString(CultureInfo.InvariantCulture) + "c" + ((node - 1) % 128).ToString(CultureInfo.InvariantCulture);
            if (c.SourceUid(physical[0], physical[1]) == expected) matched++; else if (mismatches.Count < 3) mismatches.Add(new { uid = s["uid"], expected, got = c.SourceUid(physical[0], physical[1]) });
        }
        Check(settlements > 0 && matched == settlements, "the SourceUid of every settlement's source point is the source node its uid names (" + matched + "/" + settlements + "; " + synthesized + " quota villages have none)", mismatches);
    }

    // ---- placement fixtures (MMR-63) ----
    private static void Wide()
    {
        var placement = new Dictionary<string, object> { ["extent"] = 1600.0, ["periodic"] = new Dictionary<string, object> { ["axis"] = "x", ["width"] = 800.0, ["originX"] = 400.0,
            ["cell"] = 6.25, ["sourceCells"] = 128, ["poleRows"] = new Dictionary<string, object> { ["north"] = 64, ["south"] = 192 } } };
        var c = PeriodicWorldCoordinates.FromPlacement(placement);
        Check(c != null && c.OriginY == 400 && c.Height == 800 && c.SourceColumns == 128 && PeriodicWorldCoordinates.FromPlacement(new Dictionary<string, object> { ["extent"] = 1600.0 }) == null,
            "an MMR-63 placement's periodic block gives pole rows 400..1200 and 128 source columns; a placement without one is not periodic");
        Check(Throws<ArgumentException>(() => PeriodicWorldCoordinates.FromPlacement(new Dictionary<string, object> { ["extent"] = 1600.0, ["periodic"] = new Dictionary<string, object> { ["axis"] = "y", ["width"] = 800.0, ["originX"] = 400.0 } })),
            "only an x-periodic placement is accepted");
    }

    // ---- the distance contract on grids ----
    // A logical grid of unit cells: 'L' land, 'S' sea, '#' neither. The "engine" is a planar 4-neighbour shortest path on the physical grid
    // (the logical columns copied into strips, no wrap), asked from each placement's start; the oracle is the same search on the logical grid
    // with x wrapping. Two layouts: strips W wide (terrain 3W) and the production ratio, strips W/2 (terrain 2W).
    private sealed class Grid
    {
        public readonly string[] Rows; public readonly int W, H, Strip; public readonly PeriodicWorldCoordinates C;
        public Grid(bool production, params string[] rows)
        {
            Rows = rows; H = rows.Length; W = rows[0].Length; Strip = production ? W / 2 : W;
            C = new PeriodicWorldCoordinates(W, H, Strip, 0, W + 2 * Strip);
        }
        public int Columns { get { return W + 2 * Strip; } }
        public char At(int column, int row) { return Rows[row][((column % W) + W) % W]; }
        public char Physical(int column, int row) { return At(column - Strip, row); }
    }

    private static bool Valid(char c, string capability) { return c != '#' && (capability == "all" || (capability == "land" ? c == 'L' : c == 'S')); }

    // Single-source costs: switching land -> sea adds toSea, sea -> land adds toLand.
    private static double[,] Costs(Grid g, bool periodic, int sx, int sy, string capability, double toSea, double toLand)
    {
        int cols = periodic ? g.W : g.Columns;
        Func<int, int, char> at = (c, r) => periodic ? g.At(c, r) : g.Physical(c, r);
        var cost = new double[cols, g.H];
        for (int c = 0; c < cols; c++) for (int r = 0; r < g.H; r++) cost[c, r] = double.PositiveInfinity;
        if (!Valid(at(sx, sy), capability)) return cost;
        var open = new SortedSet<Tuple<double, int, int>>();
        cost[sx, sy] = 0; open.Add(Tuple.Create(0.0, sx, sy));
        while (open.Count > 0)
        {
            var top = open.Min; open.Remove(top);
            int x = top.Item2, y = top.Item3;
            if (top.Item1 > cost[x, y]) continue;
            foreach (var d in new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } })
            {
                int nx = x + d[0], ny = y + d[1];
                if (ny < 0 || ny >= g.H) continue;
                if (periodic) nx = ((nx % cols) + cols) % cols; else if (nx < 0 || nx >= cols) continue;
                char from = at(x, y), to = at(nx, ny);
                if (!Valid(to, capability)) continue;
                double step = 1 + (from == 'L' && to == 'S' ? toSea : from == 'S' && to == 'L' ? toLand : 0);
                if (cost[x, y] + step < cost[nx, ny]) { cost[nx, ny] = cost[x, y] + step; open.Add(Tuple.Create(cost[nx, ny], nx, ny)); }
            }
        }
        return cost;
    }

    // ShortestOverImages from canonical cell (sx, sy) to logical cell (tx, ty) through the planar engine, asked from each placement's start column.
    private static PeriodicDistanceModel.Result Over(Grid g, string capability, double toSea, double toLand, int sx, int sy, int tx, int ty, Dictionary<int, double[,]> memo = null)
    {
        memo = memo ?? new Dictionary<int, double[,]>();
        double fromX = g.Strip + sx + .5, targetX = g.Strip + tx + .5;
        return PeriodicDistanceModel.ShortestOverImages(g.C, fromX, sy + .5, targetX, ty + .5, 1e6,
            (PeriodicWorldCoordinates.Placement placement, double limit, out double cost) => {
                int start = (int)Math.Floor(placement.FromX);
                double[,] costs;
                if (!memo.TryGetValue(start, out costs)) memo[start] = costs = Costs(g, false, start, sy, capability, toSea, toLand);
                cost = costs[(int)Math.Floor(placement.TargetX), ty];
                return !double.IsInfinity(cost) && cost <= limit;
            });
    }

    private static readonly string[] WorldRows = {
        "SSSSSSSSSSSSSSSS",
        "SSSLLLLSSSSSSSLL",
        "LLLLLLLSSSSSSSLL",
        "LLL#LLLSS##SSSLL",
        "LL##LLLSSS#SSS#L",
        "SSS#SSSSSSSSSS#S",
        "SSSSSSSS#SSSLSSS",   // an isolated island: unreachable by land
        "SSSSSSSSSSSSSSSS" };

    // Every pair of cells of a grid against the periodic oracle, for the given capabilities.
    private static Dictionary<string, object> Sweep(Grid g, string[] capabilities, List<object> bad)
    {
        int pairs = 0, reachable = 0, agree = 0, imageWins = 0, startMoves = 0, pruned = 0, queries = 0;
        foreach (var capability in capabilities)
            for (int sx = 0; sx < g.W; sx++) for (int sy = 0; sy < g.H; sy++)
            {
                if (!Valid(g.At(sx, sy), capability)) continue;
                var oracle = Costs(g, true, sx, sy, capability, 3, 1);
                var memo = new Dictionary<int, double[,]>();
                for (int tx = 0; tx < g.W; tx++) for (int ty = 0; ty < g.H; ty++)
                {
                    if (!Valid(g.At(tx, ty), capability)) continue;
                    pairs++;
                    var r = Over(g, capability, 3, 1, sx, sy, tx, ty, memo);
                    double truth = oracle[tx, ty];
                    bool ok = double.IsInfinity(truth) ? !r.Found : r.Found && Near(r.Cost, truth);
                    double lower = PeriodicDistanceModel.LowerBound(g.C, g.Strip + sx + .5, sy + .5, g.Strip + tx + .5, ty + .5);
                    if (r.Found && r.Cost < lower - 1e-9) ok = false;
                    if (!double.IsInfinity(truth)) reachable++;
                    if (ok) agree++; else if (bad.Count < 5) bad.Add(new { strip = g.Strip, capability, sx, sy, tx, ty, truth, found = r.Found, cost = r.Cost });
                    if (r.Found && r.Placement.TargetShift != 0) imageWins++;
                    if (r.Found && r.Placement.StartShift != 0) startMoves++;
                    pruned += r.Pruned; queries += r.Queries;
                }
            }
        return new Dictionary<string, object> { ["strip"] = g.Strip, ["pairs"] = pairs, ["reachable"] = reachable, ["agree"] = agree, ["imageWins"] = imageWins,
            ["startMoves"] = startMoves, ["pruned"] = pruned, ["queries"] = queries };
    }

    private static void Oracle(SortedDictionary<string, object> evidence)
    {
        var all = new[] { "land", "sea", "all" };
        var rows = new List<object>(); evidence["oracle"] = rows;
        foreach (bool production in new[] { false, true })
        {
            var bad = new List<object>();
            var row = Sweep(new Grid(production, WorldRows), all, bad); row["grid"] = "world"; rows.Add(row);
            int pairs = (int)row["pairs"];
            Check((int)row["agree"] == pairs && (int)row["imageWins"] > 0 && (int)row["pruned"] > 0 && (int)row["reachable"] < pairs,
                "strips " + (production ? "W/2 (production ratio)" : "W") + ": ShortestOverImages over the planar engine equals the periodic oracle on every pair of the mixed grid (land, sea, all; " +
                pairs + " pairs, " + row["reachable"] + " reachable, " + row["imageWins"] + " won by a strip copy, " + row["startMoves"] + " from a moved start, " + row["pruned"] + " pruned), never below the wrapped lower bound", bad);
        }
        // The production ratio with routes that must go the long way around (a wall west of the start): every pair agrees, the long ways
        // included (strips of W/2 alone drop the target copies those routes need).
        foreach (var walls in new[] { new[] { "LLLLL#LLLLLLLLLL", "LLLLL#LLLLLLLLLL" }, new[] { "LL#LLLLLLLLLLLLL", "LLLLLLLLLLLL#LLL", "LL#LLLLLLLLLLLLL" } })
        {
            var bad = new List<object>();
            var row = Sweep(new Grid(true, walls), new[] { "land" }, bad); row["grid"] = String.Join("/", walls); rows.Add(row);
            bool needsMoves = walls.Length == 2;   // the single wall forces long ways whose target copy lies outside the terrain
            Check((int)row["agree"] == (int)row["pairs"] && (!needsMoves || (int)row["startMoves"] > 0),
                "production ratio, walls " + String.Join("/", walls) + ": every pair agrees with the oracle (" + row["agree"] + "/" + row["pairs"] + ", " + row["startMoves"] + " long ways from a moved start)", bad);
        }
        var wall = new Grid(true, "LLLLL#LLLLLLLLLL", "LLLLL#LLLLLLLLLL");
        var around = Over(wall, "land", 3, 1, 0, 0, 6, 0);
        Check(around.Found && Near(around.Cost, 10) && around.Placement.Delta == -10, "production ratio: (0,0) -> (6,0) past a wall is the long way west, cost 10", new { around.Found, around.Cost, around.Placement.Delta });

        // The answer does not depend on which copy of the target is passed, and coincident start and target cost 0 at the canonical copy.
        var g = new Grid(false, WorldRows);
        var costs = Costs(g, false, g.Strip + 15, 2, "land", 3, 1);
        PeriodicDistanceModel.PlacementQuery fromCosts = (PeriodicWorldCoordinates.Placement q, double limit, out double cost) => { cost = costs[(int)q.TargetX, 2]; return q.StartShift == 0 && !double.IsInfinity(cost); };
        var viaWest = PeriodicDistanceModel.ShortestOverImages(g.C, g.Strip + 15.5, 2.5, 0.5, 2.5, 1e6, fromCosts);
        var viaEast = PeriodicDistanceModel.ShortestOverImages(g.C, g.Strip + 15.5, 2.5, 2 * g.W + .5, 2.5, 1e6, fromCosts);
        Check(viaWest.Found && viaEast.Found && viaWest.Placement.TargetX == viaEast.Placement.TargetX && Near(viaWest.Cost, 1), "the same placement and cost whichever copy of the target is passed (1 step across the seam)",
            new { west = viaWest.Placement.TargetX, east = viaEast.Placement.TargetX, viaWest.Cost });
        var self = Over(g, "land", 3, 1, 15, 2, 15, 2);
        Check(self.Found && self.Cost == 0 && self.Placement.TargetShift == 0 && self.Placement.StartShift == 0 && self.Rank == 0, "source and target coincide: cost 0 at the canonical copy");
        // A tie between two copies: a half-width target on an open-sea row; both copies are 8 away and the east one wins.
        var tie = Over(g, "sea", 3, 1, 2, 7, 10, 7);
        Check(tie.Found && Near(tie.Cost, 8) && tie.Placement.Delta == 8, "a path tie between the west and east copies resolves east", new { tie.Placement.TargetX, tie.Cost });
    }

    private static void Corridor()
    {
        // A wall on the seam column: two land cells 2 apart through the seam must go the long way; with a second wall, not at all.
        foreach (bool production in new[] { false, true })
        {
            var open = new Grid(production, "LLLLLLLLLLLLLLL#", "LLLLLLLLLLLLLLL#");
            var walled = new Grid(production, "LLLLLLL#LLLLLLL#", "LLLLLLL#LLLLLLL#");
            string layout = production ? " (strips W/2)" : " (strips W)";
            var r = Over(open, "land", 3, 1, 14, 0, 0, 0);
            double lower = PeriodicDistanceModel.LowerBound(open.C, open.Strip + 14.5, .5, open.Strip + .5, .5);
            Check(Near(lower, 2) && r.Found && Near(r.Cost, 14) && r.Placement.Delta == -14, "a blocked seam corridor: 2 apart through the seam, the path goes the long way (14)" + layout, new { lower, r.Cost, r.Placement.Delta });
            var w = Over(walled, "land", 3, 1, 14, 0, 0, 0);
            Check(Near(lower, 2) && !w.Found, "two walls: 2 apart through the seam and unreachable (proximity is not reachability)" + layout, new { lower, w.Found });
        }
    }

    private static void Capability()
    {
        // Land at the west side of the seam (column 15), sea just across it (column 0): one step apart.
        foreach (bool production in new[] { false, true })
        {
            var g = new Grid(production, "SSSSSSSSSSSSSSSL", "SSSSSSSSSSSSSSSL");
            double toSea = 5, toLand = 1;
            string layout = production ? " (strips W/2)" : " (strips W)";
            var landOnly = Over(g, "land", toSea, toLand, 15, 0, 0, 0);
            var seaOnly = Over(g, "sea", toSea, toLand, 15, 0, 0, 0);
            Check(!landOnly.Found && !seaOnly.Found && Near(g.C.GeometricDistance(g.Strip + 15.5, .5, g.Strip + .5, .5), 1),
                "a land gate and a sea port 1 apart across the seam: unreachable for land-only and sea-only capabilities" + layout);
            var embark = Over(g, "all", toSea, toLand, 15, 0, 0, 0);
            var back = Over(g, "all", toSea, toLand, 0, 0, 15, 0);
            Check(embark.Found && back.Found && Near(embark.Cost, 1 + toSea) && Near(back.Cost, 1 + toLand) && embark.Placement.Delta == 1 && back.Placement.Delta == -1,
                "with both capabilities the cost is directed: gate -> port pays the land-to-sea switch (6), port -> gate the sea-to-land one (2), each through the seam" + layout,
                new { embark.Cost, back = back.Cost });
        }
    }

    // ---- IL reading (call targets of a method body), for the helper coverage ----
    private const BindingFlags Every = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<ushort, System.Reflection.Emit.OpCode> OpCodesByValue = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)).ToDictionary(o => unchecked((ushort)o.Value));

    private static List<MethodBase> Calls(MethodBase method)
    {
        var targets = new List<MethodBase>();
        var body = method.GetMethodBody();
        if (body == null) return targets;
        byte[] il = body.GetILAsByteArray();
        Type[] typeArgs = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            ushort value = il[i++];
            if (value == 0xFE) value = (ushort)(0xFE00 | il[i++]);
            var op = OpCodesByValue[value];
            int size;
            switch (op.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineNone: size = 0; break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget: case System.Reflection.Emit.OperandType.ShortInlineI: case System.Reflection.Emit.OperandType.ShortInlineVar: size = 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: size = 2; break;
                case System.Reflection.Emit.OperandType.InlineI8: case System.Reflection.Emit.OperandType.InlineR: size = 8; break;
                case System.Reflection.Emit.OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(il, i); break;
                default: size = 4; break;
            }
            if (op == System.Reflection.Emit.OpCodes.Call || op == System.Reflection.Emit.OpCodes.Callvirt)
            {
                try { targets.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs)); } catch (ArgumentException) { }
            }
            i += size;
        }
        return targets;
    }

    // ---- MMR-65: the spatial consumer table against the pinned assemblies ----
    // Every PeriodicSpatialTable entry resolves (type, name, Mono.Cecil parameter spelling) and its IL holds exactly the counted calls; every
    // recompiled caller calls a table method or another recompiled caller; the audit scanned exactly the installed assemblies.
    private static string CecilName(Type t)
    {
        if (t.IsByRef) return CecilName(t.GetElementType()) + "&";
        if (t.IsArray) return CecilName(t.GetElementType()) + "[]";
        if (t.IsGenericType && !t.IsGenericTypeDefinition) return CecilName(t.GetGenericTypeDefinition()) + "<" + String.Join(",", t.GetGenericArguments().Select(CecilName)) + ">";
        if (t.IsNested) return CecilName(t.DeclaringType) + "/" + t.Name;
        return String.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name;
    }

    private static int Kind(MethodBase callee)
    {
        string owner = callee.DeclaringType.FullName ?? "", name = callee.Name;
        var ps = callee.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        if (owner == "TaleWorlds.CampaignSystem.CampaignVec2")
        {
            if (name == "Distance") return ps[0] == "CampaignVec2" ? 0 : 1;
            if (name == "DistanceSquared") return ps[0] == "CampaignVec2" ? 2 : 3;
            if (name == "op_Subtraction") return ps[1] == "CampaignVec2" ? 7 : 8;
            if (name == "NearlyEquals") return ps[0] == "CampaignVec2" ? 9 : 10;
        }
        if (owner == "TaleWorlds.Library.Vec2")
        {
            if (name == "Distance" && ps.Length == 1) return 4;
            if (name == "DistanceSquared" && ps.Length == 1) return 5;
            if (name == "op_Subtraction") return 6;
            if (name == "NearlyEquals") return 11;
        }
        if (callee.DeclaringType.IsGenericType && callee.DeclaringType.GetGenericTypeDefinition().FullName == "TaleWorlds.CampaignSystem.Map.LocatorGrid`1" && name == "FindNextLocatable") return 12;
        return -1;
    }

    private static void SpatialTable(SortedDictionary<string, object> evidence)
    {
        var assemblies = new Dictionary<string, Assembly> {
            ["TaleWorlds.CampaignSystem"] = Assembly.LoadFrom(Path.Combine(gameRoot, "bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.dll")),
            ["SandBox"] = Assembly.LoadFrom(Path.Combine(gameRoot, "Modules/SandBox/bin/Win64_Shipping_Client/SandBox.dll")),
            ["NavalDLC"] = Assembly.LoadFrom(Path.Combine(gameRoot, "Modules/NavalDLC/bin/Win64_Shipping_Client/NavalDLC.dll")),
            // The views read as campaign distances (build_spatial_table.py VIEW_PERIODIC).
            ["TaleWorlds.CampaignSystem.ViewModelCollection"] = Assembly.LoadFrom(Path.Combine(gameRoot, "bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.ViewModelCollection.dll")),
            ["SandBox.View"] = Assembly.LoadFrom(Path.Combine(gameRoot, "Modules/SandBox/bin/Win64_Shipping_Client/SandBox.View.dll")) };
        var unresolved = new List<string>(); var mismatched = new List<object>(); int calls = 0, methods = 0;
        var resolved = new Dictionary<MethodBase, MNBL.ProceduralWorld.CampaignBootstrap.PeriodicSpatialQueries.Entry>();
        foreach (var entry in MNBL.ProceduralWorld.CampaignBootstrap.PeriodicSpatialQueries.Table)
        {
            Assembly assembly;
            var type = assemblies.TryGetValue(entry.Assembly, out assembly) ? assembly.GetType(entry.Type, false) : null;
            var method = type == null ? null : type.GetMethods(Every).Cast<MethodBase>().Concat(type.GetConstructors(Every))
                .FirstOrDefault(m => m.Name == entry.Method && String.Join(",", m.GetParameters().Select(p => CecilName(p.ParameterType))) == entry.Parameters);
            if (method == null) { unresolved.Add(entry.Type + "::" + entry.Method + "(" + entry.Parameters + ")"); continue; }
            resolved[method] = entry;
            if (entry.Recompile) continue;
            var counts = new int[13];
            foreach (var callee in Calls(method)) { int k = Kind(callee); if (k >= 0) counts[k]++; }
            calls += entry.Replaced; methods++;
            if (!counts.SequenceEqual(entry.Counts)) mismatched.Add(new { method = entry.Type + "::" + entry.Method, found = counts, table = entry.Counts });
        }
        // A recompiled caller holds a call to a table method or to another recompiled caller (the inlining closure the generator computed).
        var orphans = resolved.Where(r => r.Value.Recompile && !Calls(r.Key).Any(c => resolved.ContainsKey(c))).Select(r => r.Value.Type + "::" + r.Value.Method).ToList();
        int recompiled = resolved.Count(r => r.Value.Recompile);
        // The audit scanned exactly the installed assemblies (the pinned ones among them are the lock's).
        var scanned = new List<object>(); var stale = new List<string>();
        foreach (var pair in MNBL.ProceduralWorld.CampaignBootstrap.PeriodicSpatialQueries.ScannedAssemblies)
        {
            var file = new[] { Path.Combine(gameRoot, "bin/Win64_Shipping_Client", pair[0] + ".dll") }
                .Concat(Directory.GetDirectories(Path.Combine(gameRoot, "Modules")).Select(d => Path.Combine(d, "bin/Win64_Shipping_Client", pair[0] + ".dll"))).FirstOrDefault(File.Exists);
            string sha = null;
            if (file != null) using (var stream = File.OpenRead(file)) using (var hash = System.Security.Cryptography.SHA256.Create())
                sha = String.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
            scanned.Add(new { assembly = pair[0], sha256 = pair[1], installed = sha });
            if (sha != pair[1]) stale.Add(pair[0]);
        }
        evidence["spatialTable"] = new Dictionary<string, object> { ["methods"] = methods, ["replacedCalls"] = calls, ["recompiledCallers"] = recompiled,
            ["unresolved"] = unresolved, ["mismatched"] = mismatched, ["orphanRecompiles"] = orphans, ["scannedAssemblies"] = scanned, ["staleAssemblies"] = stale };
        Check(unresolved.Count == 0 && mismatched.Count == 0 && methods > 0,
            "every spatial consumer of the table resolves in the pinned assemblies with exactly its counted calls (" + methods + " methods, " + calls + " replaced calls)",
            new { unresolved, mismatched });
        Check(orphans.Count == 0 && recompiled > 0, "every recompiled caller (" + recompiled + ") calls a table method or another recompiled caller", orphans);
        Check(stale.Count == 0 && scanned.Count == 7, "the consumer audit scanned exactly the installed campaign assemblies (" + scanned.Count + ", SHA-256 equal)", stale);
    }

    // ---- the installed MapDistanceModel ----
    private static void Coverage(SortedDictionary<string, object> evidence)
    {
        var campaign = Assembly.LoadFrom(Path.Combine(gameRoot, "bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.dll"));
        var naval = Assembly.LoadFrom(Path.Combine(gameRoot, "Modules/NavalDLC/bin/Win64_Shipping_Client/NavalDLC.dll"));
        var baseType = campaign.GetType("TaleWorlds.CampaignSystem.ComponentInterfaces.MapDistanceModel", true);
        var installed = PeriodicDistanceModel.Members(baseType);
        List<string> uncovered, stale;
        PeriodicDistanceModel.Compare(installed, out uncovered, out stale);
        evidence["coverage"] = new Dictionary<string, object> { ["installed"] = installed, ["uncovered"] = uncovered, ["stale"] = stale, ["manifest"] = PeriodicDistanceModel.Record() };
        Check(uncovered.Count == 0 && stale.Count == 0 && installed.Count == PeriodicDistanceModel.Manifest.Count,
            "the coverage manifest lists every member of the pinned MapDistanceModel (" + installed.Count + ") and nothing else", new { uncovered, stale });
        foreach (var model in new[] { campaign.GetType("TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel", true), naval.GetType("NavalDLC.GameComponents.NavalDLCMapDistanceModel", true) })
        {
            var missing = PeriodicDistanceModel.NotOverridden(model);
            Check(missing.Count == 0, model.Name + " declares every manifest method itself", missing);
        }
        // The distance helpers: every planar distance call in their IL (all methods, nested types included) has a HelperCoverage entry.
        var found = new List<string>(); var uncoveredHelpers = new List<string>();
        foreach (var helperName in new[] { "Helpers.DistanceHelper", "Helpers.SettlementHelper" })
        {
            var helper = campaign.GetType(helperName, true);
            foreach (var type in new[] { helper }.Concat(helper.GetNestedTypes(Every)))
                foreach (var method in type.GetMethods(Every).Cast<MethodBase>().Concat(type.GetConstructors(Every)))
                {
                    int sites = Calls(method).Count(t => PeriodicDistanceModel.PlanarDistanceMethods.Contains(PeriodicDistanceModel.PlanarName(t)));
                    if (sites == 0) continue;
                    string member = PeriodicDistanceModel.Signature(method);
                    found.Add(type.FullName + "::" + member + " x" + sites);
                    var listed = PeriodicDistanceModel.HelperCoverage.FirstOrDefault(h => h.Type == type.FullName && h.Member == member);
                    if (listed == null || listed.Sites != sites) uncoveredHelpers.Add(type.FullName + "::" + member + " x" + sites);
                }
        }
        var stale2 = PeriodicDistanceModel.HelperCoverage.Where(h => !found.Contains(h.Type + "::" + h.Member + " x" + h.Sites)).Select(h => h.Type + "::" + h.Member).ToList();
        evidence["helperCoverage"] = new Dictionary<string, object> { ["planarSites"] = found, ["uncovered"] = uncoveredHelpers, ["stale"] = stale2, ["coverage"] = PeriodicDistanceModel.HelperRecord() };
        Check(found.Count > 0 && uncoveredHelpers.Count == 0 && stale2.Count == 0, "every planar distance call in Helpers.DistanceHelper and Helpers.SettlementHelper of the pinned assembly is in HelperCoverage (" + found.Count + " methods)",
            new { found, uncoveredHelpers, stale2 });
        Check(PeriodicDistanceModel.Manifest.All(m => m.Treatments.Count > 0 && !String.IsNullOrEmpty(m.Reason)) &&
              PeriodicDistanceModel.Manifest.Where(m => m.Member.StartsWith("get_RegionSwitch", StringComparison.Ordinal) || m.Member.StartsWith("GetTransitionCostAdjustment", StringComparison.Ordinal)).All(m => m.Directed),
            "every manifest entry has a treatment and a reason; region switch costs and transition costs are marked directed");
    }
}
