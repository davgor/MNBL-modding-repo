using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

// MMR-37 runtime surface contract tests, without the game:
//   SurfaceContractTests REPO BUNDLE_A BUNDLE_B SURFACE_TEMPLATE WORLDBRIDGE OUT_DIR OUT_JSON
// For two real compiled worlds: the surface model against the bundle (layout, scale record, chunks, seam), the pinned surface encoder's
// output against the model (heights byte for byte, bounds, per-node ranges and layers), the encoded terrain.bin's chunk seams and heights
// (tools/check_terrain_seams.py), the landmarks and model queries, and the lifetime primitives (generation tokens, late callbacks, the
// resource ledger).
internal static class SurfaceContractTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static double D(IDictionary<string, object> o, string key) { return Convert.ToDouble(o[key], CultureInfo.InvariantCulture); }

    private static int Main(string[] args)
    {
        if (args.Length != 7) { Console.Error.WriteLine("usage: SurfaceContractTests REPO BUNDLE_A BUNDLE_B SURFACE_TEMPLATE WORLDBRIDGE OUT_DIR OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.surface-contract", ["version"] = 1, ["ticket"] = "MMR-37" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[6], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0], template = args[3], bridge = args[4], outDir = args[5];
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var surfaces = new List<WorldSurface>(); var landmarkSets = new List<List<WorldSurface.Landmark>>();
        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var s = WorldSurface.FromBundle(bundle);
            var again = WorldSurface.FromBundle(bundle);
            surfaces.Add(s);
            var projection = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/projection.json")));
            var layout = O(projection, "periodicLayout"); var vertical = O(projection, "vertical"); var target = O(projection, "target");
            string id = bundle.WorldContentId.Substring(0, 12);
            // Layout and the scale record, against the projection read here.
            Check(s.Side == 257 && s.Cells == 64 && s.HeightmapLevel == 6 && s.Extent == D(layout, "physicalWidth") && s.Extent == D(layout, "physicalHeight") && s.Spacing == s.Extent / 256 && s.NodeSize == s.Extent / 4,
                  label + " " + id + ": the native terrain is the projection's square physical domain: " + s.Side + " samples over " + s.Extent + " units (spacing " + s.Spacing + "), 4 x 4 nodes of " + s.Cells + " cells (heightmap level " + s.HeightmapLevel + ")");
            Check((double)s.Scale["verticalExaggeration"] == D(vertical, "exaggeration") && (double)s.Scale["verticalWorldUnitsPerMetre"] == D(vertical, "worldUnitsPerMetre") && (double)s.Scale["logicalWidth"] == D(target, "logicalWidth")
                  && (double)s.Scale["strip"] == D(layout, "strip") && (double)s.Scale["poleMargin"] == D(layout, "poleMargin") && (double)s.Scale["horizontalWorldUnitsPerMetre"] == D(O(projection, "horizontal"), "equatorWorldUnitsPerMetre"),
                  label + ": the scale record keeps campaign XY (logical " + s.LogicalWidth + " x " + s.LogicalHeight + ", strip " + s.Strip + ", pole margin " + s.PoleMargin + ") apart from source metres (" + s.Scale["horizontalWorldUnitsPerMetre"] + " units/m horizontal, " + s.Scale["verticalWorldUnitsPerMetre"] + " vertical) and records the vertical exaggeration " + s.Scale["verticalExaggeration"]);
            // Chunks: tile the domain, deterministic, ranges recomputed here.
            bool tiled = s.Chunks.Count == 16 && s.Chunks.All(c => c.MinX == c.X * s.NodeSize && c.MaxX == (c.X + 1) * s.NodeSize && c.MinY == c.Y * s.NodeSize && c.MaxY == (c.Y + 1) * s.NodeSize && c.FirstColumn == c.X * 64 && c.FirstRow == c.Y * 64)
                && s.Chunks.Select(c => c.X + "," + c.Y).Distinct().Count() == 16;
            bool ranges = s.Chunks.All(c => {
                double lo = double.MaxValue, hi = double.MinValue; bool rock = false;
                for (int r = 0; r <= 64; r++) for (int col = 0; col <= 64; col++) { double h = s.Sample(c.FirstColumn + col, c.FirstRow + r); lo = Math.Min(lo, h); hi = Math.Max(hi, h); rock |= s.Rock(c.FirstColumn + col, c.FirstRow + r) != 0; }
                return lo == c.MinHeight && hi == c.MaxHeight && rock == c.Rock && c.Mask == (rock ? 3 : 1); });
            bool deterministic = again.InputsSha256 == s.InputsSha256 && again.Chunks.Select(Key).SequenceEqual(s.Chunks.Select(Key)) && again.QuantizedHeightfieldBytes().SequenceEqual(s.QuantizedHeightfieldBytes());
            Check(tiled && ranges && deterministic, label + ": the 16 chunks tile the domain exactly, each over its own 65 x 65 samples (borders shared), with its height range and material layers recomputed here, identically on a second read (" + s.Chunks.Count(c => c.Rock) + " chunks carry rock_k)");
            // Periodic seam: the compiled heightfield duplicates columns c and c + logical width / spacing; the model keeps them equal.
            var raw = bundle.ReadVerified("compiled/terrain/heightfield.bin");
            int rawBad = 0, period = (int)Math.Round(s.LogicalWidth / s.Spacing);
            for (int c = 0; c + period < 257; c++) for (int r = 0; r < 257; r++) if (BitConverter.ToDouble(raw, 12 + (r * 257 + c) * 8) != BitConverter.ToDouble(raw, 12 + (r * 257 + c + period) * 8)) rawBad++;
            Check(rawBad == 0 && s.SeamMismatches() == 0, label + ": every compiled sample column c equals column c + " + period + " (the strips copy the other side of the seam), and so do the quantized samples");
            // The compiler's own 16-bit heights (height-q16.bin: f64 hmin, f64 hmax, side^2 big-endian u16) carry the model's codes.
            var q16 = bundle.ReadVerified("compiled/terrain/height-q16.bin");
            int codeBad = 0;
            for (int r = 0; r < 257; r++) for (int c = 0; c < 257; c++) { int i = 12 + 16 + 2 * (r * 257 + c); if (((q16[i] << 8) | q16[i + 1]) != s.Code(c, r)) codeBad++; }
            Check(System.Text.Encoding.ASCII.GetString(q16, 0, 8) == "MNBLQ16B" && codeBad == 0, label + ": the compiler's published 16-bit heights (height-q16.bin) are the model's quantization codes (" + codeBad + " of 66049 differ)");
            // The pinned encoder's output against the model.
            string job = Path.Combine(outDir, label);
            if (Directory.Exists(job)) Directory.Delete(job, true);
            string localTemplate = Path.Combine(job, "template"), output = Path.Combine(job, "surface");
            Directory.CreateDirectory(localTemplate);
            foreach (var f in new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" }) File.Copy(Path.Combine(template, f), Path.Combine(localTemplate, f));
            string sceneName = "mnbl_world_" + id + "_contract";
            File.WriteAllText(Path.Combine(localTemplate, "scene.xscene"), File.ReadAllText(Path.Combine(localTemplate, "scene.xscene")).Replace("<scene name=\"mnbl_runtime_surface\"", "<scene name=\"" + sceneName + "\""), new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(job, "heightfield.bin"), raw); File.WriteAllBytes(Path.Combine(job, "weights.bin"), bundle.ReadVerified("compiled/terrain/weights.bin"));
            int exit = Run(bridge, new[] { "--surface-heightfield", Path.Combine(job, "heightfield.bin"), "--surface-template", localTemplate, "--surface-output", output,
                "--surface-extent", s.Extent.ToString("R", CultureInfo.InvariantCulture), "--surface-scale", "1", "--surface-weights", Path.Combine(job, "weights.bin") });
            var manifest = Parse(File.ReadAllText(Path.Combine(output, "surface.json")));
            bool heightsEqual = File.ReadAllBytes(Path.Combine(output, "quantized-heightfield.bin")).SequenceEqual(s.QuantizedHeightfieldBytes());
            Check(exit == 0 && heightsEqual && D(manifest, "minHeight") == s.Low && D(manifest, "maxHeight") == s.High && Math.Abs(D(manifest, "quantizationStep") - s.Step) <= 1e-15 && D(manifest, "nodeSize") == s.NodeSize
                  && Convert.ToInt32(manifest["gridSide"]) == s.Side && Convert.ToInt32(manifest["heightmapLevel"]) == s.HeightmapLevel,
                  label + ": the pinned surface encoder's published heights equal the model byte for byte (" + (s.Side * s.Side) + " samples), with the model's bounds " + s.Low + ".." + s.High + ", step " + s.Step + " and node size");
            var xml = new XmlDocument(); xml.Load(Path.Combine(output, "scene.xscene"));
            var terrainNode = (XmlElement)xml.SelectSingleNode("//terrain");
            var nodes = xml.SelectNodes("//terrain/nodes/node").OfType<XmlElement>().ToList();
            bool nodeRanges = nodes.Count == 16 && nodes.All(n => {
                var c = s.Chunks.Single(k => k.X == int.Parse(n.GetAttribute("idx")) && k.Y == int.Parse(n.GetAttribute("idy")));
                return Math.Abs(double.Parse(n.GetAttribute("min_height"), CultureInfo.InvariantCulture) - c.MinHeight) <= .0005 && Math.Abs(double.Parse(n.GetAttribute("max_height"), CultureInfo.InvariantCulture) - c.MaxHeight) <= .0005
                    && new[] { "summer", "fall", "winter", "spring" }.All(season => n.GetAttribute("layer_is_used_mask_" + season) == c.Mask.ToString(CultureInfo.InvariantCulture)); });
            Check(nodeRanges && Math.Abs(double.Parse(terrainNode.GetAttribute("node_size"), CultureInfo.InvariantCulture) - s.NodeSize) <= .0005,
                  label + ": the encoded scene's 16 terrain nodes carry the model chunks' height ranges and material layer masks, at the model's node size");
            // The encoded terrain.bin itself: shared chunk edges, layers and heights (tools/check_terrain_seams.py).
            string seams = Path.Combine(outDir, "seams-" + label + ".json");
            int seamExit = Run("python", new[] { "-I", Path.Combine(repo, "tools", "check_terrain_seams.py"), output, seams });
            var seamReport = File.Exists(seams) ? Parse(File.ReadAllText(seams)) : null;
            Check(seamExit == 0 && seamReport != null && Convert.ToInt32(seamReport["passed"]) == Convert.ToInt32(seamReport["total"]),
                  label + ": the encoded terrain.bin has no chunk seam tear: " + (seamReport == null ? "no report" : seamReport["sharedEdgeSamples"] + " shared edge samples with equal heights, normals and material weights; every decoded height is the model's (" + seamReport["passed"] + "/" + seamReport["total"] + ")"));
            // Landmarks.
            var travel = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var marks = s.Landmarks(travel, 24, 12);
            landmarkSets.Add(marks);
            var first = ((IEnumerable)travel["settlements"]).Cast<IDictionary<string, object>>().OrderBy(e => (string)e["id"], StringComparer.Ordinal).First();
            var firstMark = marks.First(m => m.Kind == "settlement");
            Check(marks.Count(m => m.Kind == "settlement") == 24 && marks.Count(m => m.Kind == "coast") == 12 && marks.Count(m => m.Kind == "chunk") == 25 && marks.Count(m => m.Kind == "grid") == 9
                  && marks.All(m => s.Contains(m.X, m.Y)) && firstMark.X == D(O(first, "point"), "x") + s.Strip && firstMark.Y == D(O(first, "point"), "y") + s.PoleMargin
                  && marks.Where(m => m.Kind == "coast").All(m => s.Height(m.X, m.Y) > 0),
                  label + ": " + marks.Count + " landmarks inside the domain: 24 compiler settlements (logical + strip, pole margin; first " + firstMark.Name + " at " + firstMark.X + "," + firstMark.Y + "), 25 chunk corners and centres, 9 MMR-16 grid points, 12 coast samples above the datum");
            // Model queries.
            var m0 = marks[0];
            double atVertex = s.Height(10 * s.Spacing, 20 * s.Spacing), u = .75, v = .25;
            double manual = s.Sample(10, 20) * (1 - u) + s.Sample(11, 20) * (u - v) + s.Sample(11, 21) * v;
            var normal = s.Normal(m0.X, m0.Y); double length = Math.Sqrt(normal.Sum(x => x * x));
            double fraction; bool hit = s.Intersect(new[] { m0.X, m0.Y, s.High + 50 }, new[] { m0.X, m0.Y, s.Low - 50 }, out fraction);
            double hitZ = s.High + 50 + (s.Low - 50 - (s.High + 50)) * fraction;
            bool outside = false; try { s.Height(s.Extent + 1, 10); } catch (ArgumentOutOfRangeException) { outside = true; }
            Check(atVertex == s.Sample(10, 20) && Math.Abs(s.Height((10 + u) * s.Spacing, (20 + v) * s.Spacing) - manual) < 1e-9 && Math.Abs(length - 1) < 1e-12 && hit && Math.Abs(hitZ - s.CollisionHeight(m0.X, m0.Y)) < 1e-6
                  && marks.All(m => Math.Abs(s.CollisionHeight(m.X, m.Y) - s.Height(m.X, m.Y)) <= WorldSurface.CollisionStep + 1e-9) && outside,
                  label + ": model queries: vertex heights are samples, interior heights the main-diagonal triangle, normals unit, a vertical ray meets the collision reference (within 0.03 of the height everywhere), and a query outside the domain throws (no fallback heights)");
            worlds.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["label"] = label, ["worldContentId"] = bundle.WorldContentId, ["inputsSha256"] = s.InputsSha256, ["scale"] = s.Scale,
                ["low"] = s.Low, ["high"] = s.High, ["step"] = s.Step, ["chunks"] = s.Chunks.Select(Key).ToList(), ["landmarks"] = marks.Count, ["seams"] = seamReport });
        }
        int differing = landmarkSets[0].Count(m => Math.Abs(surfaces[0].Height(m.X, m.Y) - surfaces[1].Height(m.X, m.Y)) > .2);
        Check(surfaces[0].InputsSha256 != surfaces[1].InputsSha256 && differing >= landmarkSets[0].Count / 2, "the two worlds differ at " + differing + " of world A's " + landmarkSets[0].Count + " landmark positions, so world B's answers there can be told from A's");

        // Generation tokens and late callbacks.
        var tA = WorldGenerations.Begin("contract A");
        bool aCurrent = !tA.Retired && !tA.Superseded;
        var tB = WorldGenerations.Begin("contract B");
        bool aStale = tA.Retired && tA.Superseded && Throws<StaleWorldException>(() => tA.Require("A after B")) && Throws<StaleWorldException>(() => tA.RequireNotSuperseded("A query after B"));
        bool retireOld = !WorldGenerations.Retire(tA) && WorldGenerations.Current == tB.Id;
        bool retireOwn = WorldGenerations.Retire(tB) && tB.Retired && !tB.Superseded && !Throws<StaleWorldException>(() => tB.RequireNotSuperseded("B's own teardown"));
        var tC = WorldGenerations.Begin("contract C (late)");
        var lateJob = Task.Delay(50).ContinueWith(_ => { try { tC.Require("C's late install"); return "attached"; } catch (StaleWorldException) { return "refused"; } });
        var tD = WorldGenerations.Begin("contract D");
        string lateOutcome = lateJob.Result;
        Check(aCurrent && aStale && retireOld && retireOwn && lateOutcome == "refused" && tB.Superseded && !tD.Retired,
              "generation tokens: beginning a world supersedes the previous one (its install and queries are refused), retiring a superseded token changes nothing, a disposed world is retired but may finish its own teardown, and a callback that completes after a later world began is refused (" + lateOutcome + ")");
        WorldGenerations.Retire(tD);

        // The resource ledger.
        var order = new List<string>();
        var ledger = new ResourceLedger();
        ledger.Acquire("scene", "world", () => order.Add("scene"));
        ledger.Acquire("renderer", "engine owner", null);
        ledger.Acquire("camera", "screen", () => { order.Add("camera"); throw new InvalidOperationException("release failed"); });
        ledger.Acquire("layer", "screen", () => order.Add("layer"));
        bool duplicate = Throws<InvalidOperationException>(() => ledger.Acquire("scene", "world", null));
        var releaseFailures = ledger.ReleaseAll("test");
        var second = ledger.ReleaseAll("test again");
        Check(duplicate && order.SequenceEqual(new[] { "layer", "camera", "scene" }) && releaseFailures.Count == 1 && releaseFailures[0].StartsWith("camera", StringComparison.Ordinal) && ledger.Outstanding == 0 && second.Count == 0
              && ledger.Entries.Single(e => e.Resource == "renderer").HandedTo == "engine owner" && !ledger.Entries.Single(e => e.Resource == "renderer").Released && ledger.Entries.Single(e => e.Resource == "scene").ReleasedBy == "test",
              "the resource ledger releases latest first, each once, records a failing release without skipping the rest, hands a resource another owner releases to that owner (not recorded as released), and refuses holding a resource twice");
        // Content-addressed scene names and the campaign map ordering rule.
        string idA = surfaces[0].WorldContentId, idB = surfaces[1].WorldContentId;
        string nameA = SceneNames.ForWorld(idA, "encoder", "template", 1600, "navmesh"), nameA2 = SceneNames.ForWorld(idA, "encoder", "template", 1600, "navmesh");
        bool namesOk = nameA == nameA2 && nameA != SceneNames.ForWorld(idA, "encoder2", "template", 1600, "navmesh") && nameA != SceneNames.ForWorld(idA, "encoder", "template2", 1600, "navmesh")
            && nameA != SceneNames.ForWorld(idA, "encoder", "template", 1600, "navmesh2")
            && nameA != SceneNames.ForWorld(idB, "encoder", "template", 1600, "navmesh") && nameA.StartsWith("mnbl_world_" + idA.Substring(0, 16) + "_", StringComparison.Ordinal) && nameA.Length <= 96
            && SceneNames.IsWorldScene(nameA) && !SceneNames.IsWorldScene("mnbl_generated_00059042") && !SceneNames.IsWorldScene("mnbl_world_a") && !SceneNames.IsWorldScene("mnbl_world_b")
            && !SceneNames.IsWorldScene("mnbl_world_f596b58cdcfc_620cc952e3b84743") && !SceneNames.IsWorldScene("mnbl_runtime_surface");
        var map = WorldGenerations.Begin("contract campaign map");
        WorldGenerations.CampaignMapLoaded(map);
        bool refusedWhileLive = Throws<InvalidOperationException>(() => WorldGenerations.Begin("contract background world"));
        WorldGenerations.CampaignMapDestroyed(map); WorldGenerations.Retire(map);
        var after = WorldGenerations.Begin("contract after the map");
        WorldGenerations.Retire(after);
        Check(namesOk && refusedWhileLive && after.Id > map.Id,
              "scene names are content-addressed (" + nameA + ": the same world and encoder give the same name, any other world, encoder or template another; only that exact form is the scene store's, never a fixture slot such as mnbl_world_a), and no world generation begins while a campaign map is live");
        evidence["generationLog"] = WorldGenerations.Log;
    }

    private static string Key(WorldSurface.Chunk c) { return c.X + "," + c.Y + " " + c.MinHeight.ToString("R", CultureInfo.InvariantCulture) + ".." + c.MaxHeight.ToString("R", CultureInfo.InvariantCulture) + " mask " + c.Mask; }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }

    private static int Run(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file, String.Join(" ", arguments.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(info))
        {
            string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Console.WriteLine("  (" + Path.GetFileName(file) + " exit " + p.ExitCode + ": " + (stderr + stdout).Trim().Replace(Environment.NewLine, " | ") + ")");
            return p.ExitCode;
        }
    }
}
