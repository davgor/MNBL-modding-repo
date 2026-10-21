using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Automation;

// MMR-247: the acceptance probe's place choices (Automation/Map2AcceptancePlan.cs) without the game: Map2AcceptancePlanTests REPO BUNDLE_A
// BUNDLE_B OUT_JSON
//   * synthetic: the most interior spot of a patch is its centre, also across the seam (columns wrap); a biome absent from the grid has no
//     spot; seam rows are those where two different land biomes meet across the cut, the broadest run first; the pad slope and the season
//     extremes;
//   * the A' and B' bundles: every land biome of the shared table present on the world's biome plane (masks.bin, read as
//     GeneratedCampaignInitializer.CellBiomes reads it) gets a spot of that biome, inside the logical world, interior for every biome with
//     enough samples; every seam candidate joins two different land biomes. The chosen spots and seam rows are the evidence.
//   * acc4 triage: world 1's order (the core has no mission or encounter step and runs before the risky tail, the siege last), every step a
//     budget, the land-cell samples (land cells of their own type, every land type of the world, at most perType each, deterministic), the
//     dry-point rule, the installed snow rule's yearly Snowy share and the midday window.
internal static class Map2AcceptancePlanTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: Map2AcceptancePlanTests REPO BUNDLE_A BUNDLE_B OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.map2-acceptance-plan-tests", ["version"] = 1, ["ticket"] = "MMR-247" };
        try
        {
            Synthetic();
            evidence["worlds"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["A"] = World("A", args[1]), ["B"] = World("B", args[2]) };
        }
        catch (Exception e) { Check(false, "unexpected " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[3], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Synthetic()
    {
        // A 10 x 10 square of "b" (logical 40..60) in "a", on a 200 x 100 world sampled every 2 units.
        Func<double, double, string> square = (x, y) => x >= 40 && x < 60 && y >= 40 && y < 60 ? "b" : "a";
        var g = Map2AcceptancePlan.Sample(square, 200, 100, 2);
        var spots = Map2AcceptancePlan.Interior(g, new[] { "a", "b", "c" }, 3);
        var b = spots.ContainsKey("b") ? spots["b"] : null;
        Check(b != null && Math.Abs(b.X - 50) <= 3 && Math.Abs(b.Y - 50) <= 3 && b.Score == b.Window, "the most interior spot of a square patch is its centre, its whole window of the patch (" + (b == null ? "none" : b.X + "," + b.Y + " " + b.Score + "/" + b.Window) + ")");
        Check(!spots.ContainsKey("c") && spots.ContainsKey("a"), "a biome absent from the grid has no spot");
        // The patch across the seam: logical x in [190, 200) and [0, 10).
        Func<double, double, string> seam = (x, y) => (x >= 190 || x < 10) && y >= 40 && y < 60 ? "b" : "a";
        var s = Map2AcceptancePlan.Interior(Map2AcceptancePlan.Sample(seam, 200, 100, 2), new[] { "b" }, 3)["b"];
        Check(s.Score == s.Window && (s.X < 4 || s.X > 196), "columns wrap: a patch across the seam is interior at the cut (" + s.X + "," + s.Y + " " + s.Score + "/" + s.Window + ")");
        // On a noisy grid the summed-area score is the brute-force window count, and the spot holds the best count.
        var rng = new Random(247);
        var noise = new string[30, 60];
        for (int j = 0; j < 30; j++) for (int i = 0; i < 60; i++) noise[j, i] = new[] { "a", "b", "c" }[rng.Next(3)];
        Func<double, double, string> noisy = (x, y) => noise[(int)Math.Floor(y / 2), (int)Math.Floor(x / 2) % 60];
        var ng = Map2AcceptancePlan.Sample(noisy, 120, 60, 2);
        bool same = true;
        foreach (var kv in Map2AcceptancePlan.Interior(ng, new[] { "a", "b", "c" }, 2))
        {
            int best = 0;
            for (int j = 0; j < ng.Rows; j++) for (int i = 0; i < ng.Cols; i++)
            {
                if (ng.At(i, j) != kv.Key) continue;
                int n = 0;
                for (int dj = -2; dj <= 2; dj++) for (int di = -2; di <= 2; di++) if (ng.At(i + di, j + dj) == kv.Key) n++;
                best = Math.Max(best, n);
            }
            int ci = (int)Math.Floor(kv.Value.X / 2), cj = (int)Math.Floor(kv.Value.Y / 2), own = 0;
            for (int dj = -2; dj <= 2; dj++) for (int di = -2; di <= 2; di++) if (ng.At(ci + di, cj + dj) == kv.Key) own++;
            same &= kv.Value.Score == best && own == best;
        }
        Check(same, "on a noisy grid each spot's score is the brute-force best window count of its biome, and its own window holds it");
        // Seam rows: forest west of the cut, desert east of it, for y in [20, 30); water elsewhere on the east.
        Func<double, double, string> cut = (x, y) => x > 100 ? "forest" : (y >= 20 && y < 30 ? "desert" : "ocean");
        var rows = Map2AcceptancePlan.Seam(cut, k => k != "ocean", 200, 100, 1, Map2AcceptancePlan.SeamInsets, 5);
        Check(rows.Count > 0 && rows.All(r => r.Y >= 20 && r.Y < 30 && r.West == "forest" && r.East == "desert" && r.Inset == 1.5) && rows[0].Run >= 4,
              "seam rows join two land biomes across the cut, the shortest and broadest first (" + String.Join(", ", rows.Select(r => r.Y + ":" + r.Run)) + ")");
        // Forest on both sides of the cut, desert from 6 units east of it: the crossing reaches the desert; a lake at 4 east blocks it.
        Func<double, double, string> far = (x, y) => x > 6 && x < 100 ? "desert" : "forest";
        var farRows = Map2AcceptancePlan.Seam(far, k => k != "lake", 200, 100, 1, Map2AcceptancePlan.SeamInsets, 3);
        Func<double, double, string> wet = (x, y) => x >= 3.5 && x < 4.5 ? "lake" : far(x, y);
        Check(farRows.Count == 3 && farRows.All(r => r.Inset == 8 && r.West == "forest" && r.East == "desert") && Map2AcceptancePlan.Seam(wet, k => k != "lake", 200, 100, 1, Map2AcceptancePlan.SeamInsets, 3).Count == 0,
              "a crossing reaches a biome a few units past the cut, over land only (insets " + String.Join(",", farRows.Select(r => r.Inset)) + ")");
        Check(Math.Abs(Map2AcceptancePlan.PadSlopeDegrees(new List<double> { 0, 1, 0, -1, 0.5 }, 1) - 45) < 1e-9 && Double.IsNaN(Map2AcceptancePlan.PadSlopeDegrees(new List<double> { 0 }, 1)),
              "the pad slope is the steepest centre-to-ring angle");
        Check(Map2AcceptancePlan.Extreme(0.95) == "winter" && Map2AcceptancePlan.Extreme(0.03) == "summer" && Map2AcceptancePlan.Extreme(0.5) == null && Map2AcceptancePlan.Extreme(Double.NaN) == null,
              "the season factor's extremes: winter near 1, summer near 0");
        Check(Map2AcceptancePlan.Share(new List<bool> { true, false, false, true }) == 0.5 && Map2AcceptancePlan.Share(new List<bool>()) == null, "the Snowy share of the sampled days");
        // World 1's order: the core free of missions and encounters, the tail after it, the siege last, each step once and with a budget.
        var core = Map2AcceptancePlan.World1Core; var tail = Map2AcceptancePlan.World1Tail;
        var risky = new[] { "conversation", "battle", "town", "castle", "village", "siege" };
        Check(core.All(n => !risky.Any(r => n.Contains("." + r))) && tail.All(n => risky.Any(r => n.Contains("." + r))) && tail.Last() == "w1.siege",
              "world 1's core has no mission or encounter step; the risky tail follows it, the siege last (" + String.Join(", ", tail) + ")");
        Check(core.Concat(tail).Distinct().Count() == core.Length + tail.Length && core.Contains("w1.cast") && core.Contains("w1.saveSoak") && Array.IndexOf(core, "w1.saveSoak") == core.Length - 1 && tail.Contains("w1.battle.mutated"),
              "each step once; the cast, Continue and soak in the core, the soak's save closing it, the zone battle in the tail");
        Check(core.Concat(tail).All(n => Map2AcceptancePlan.Budget(n) > 0) && Map2AcceptancePlan.Budget("w1.start") == 0 && Map2AcceptancePlan.Budget("w1.siege") >= Map2AcceptancePlan.Budget("w1.battle.snow"),
              "every world 1 step past the start has a budget (the start's cache build none); the siege's is the largest of the tail");
        // Land-cell samples: of land cells only, of their own type, at most perType each, spread over the type's cells.
        var classes = new byte[] { 0, 0, 2, 0, 1, 0, 0, 0, 0, 3 }; var types = new byte[] { 5, 5, 0, 7, 0, 5, 5, 7, 5, 0 };
        var picked = Map2AcceptancePlan.CellSamples(classes, types, 2);
        Check(picked.Count == 2 && picked[5].SequenceEqual(new[] { 1, 6 }) && picked[7].SequenceEqual(new[] { 3, 7 }) && picked.Values.SelectMany(v => v).All(i => classes[i] == 0),
              "land-cell samples: per land type, land cells only, spread evenly (" + String.Join("; ", picked.Select(kv => kv.Key + ": " + String.Join(",", kv.Value))) + ")");
        Check(Map2AcceptancePlan.DryAround((x, y) => x > 10, 5, 5, 3) && !Map2AcceptancePlan.DryAround((x, y) => x > 7, 5, 5, 3), "a battle point is dry when no water lies at it or 3 units round it");
        // The installed snow rule: no snow byte is never Snowy, the ice-cap byte always; the share grows with the byte; a byte is Snowy in deep winter first.
        bool monotone = true; double last = -1;
        for (int nb = 0; nb <= 255; nb += 5) { double sh = Map2AcceptancePlan.SnowyShare(nb); monotone &= sh >= last - 1e-12; last = sh; }
        Check(Map2AcceptancePlan.SnowyShare(0) == 0 && Map2AcceptancePlan.SnowyShare(255) == 1 && monotone && Map2AcceptancePlan.Snowy(1, 113) && !Map2AcceptancePlan.Snowy(0, 113),
              "the Snowy share of a weather node's byte: 0 at byte 0, 1 at 255, rising with the byte; a mid byte is Snowy at mid-winter, not at mid-summer (share at 113: " + Map2AcceptancePlan.SnowyShare(113).ToString("0.###") + ")");
        Check(Map2AcceptancePlan.Midday(12) && !Map2AcceptancePlan.Midday(9) && !Map2AcceptancePlan.Midday(13), "the season screenshots' daylight window is [11, 13)");
        // MMR-247 (results/MMR-247-H.md): a land case after a mission on the loaded map starts from a fresh load of the reload save.
        string reason = Map2AcceptancePlan.FreshMapReason(true, true, false, "mnbl_acc_w1_soak");
        Check(reason != null && reason.Contains("mnbl_acc_w1_soak"), "a case after a mission on this map reloads the reload save first (" + reason + ")");
        Check(Map2AcceptancePlan.FreshMapReason(true, false, false, "s") == null && Map2AcceptancePlan.FreshMapReason(false, true, false, "s") == null
              && Map2AcceptancePlan.FreshMapReason(true, true, true, "s") == null && Map2AcceptancePlan.FreshMapReason(true, true, false, null) == null,
              "no fresh load with no mission since the map loaded, with the rule off (MMR-52's probe), on world B (no reload) or without a save");
        // The acc5 cases on their maps (a mission on this map before the case?): forest after snow, castle after the town, siege after the
        // village blocked; snow (no mission since the Continue), settlement and village (first after a load) ran; the town ran after the
        // mutated battle. Under the rule every case that blocked reloads first, and every case with no mission before it is unchanged.
        var cases = new[] { new { name = "acc5 battle.snow", mission = false, hung = false }, new { name = "acc5 battle.forest", mission = true, hung = true },
            new { name = "acc5r1 battle.settlement", mission = false, hung = false }, new { name = "acc5r1 town", mission = true, hung = false },
            new { name = "acc5r1 castle", mission = true, hung = true }, new { name = "acc5r2 village", mission = false, hung = false }, new { name = "acc5r2 siege", mission = true, hung = true } };
        Check(cases.Where(c => c.hung).All(c => Map2AcceptancePlan.FreshMapReason(true, c.mission, false, "save") != null) && cases.Where(c => !c.mission).All(c => Map2AcceptancePlan.FreshMapReason(true, c.mission, false, "save") == null),
              "every acc5 block came after a mission on its map, so the rule reloads before each; the cases with no mission before them run as they did");

        // MMR-247 (D): the decal lab's grid and spots.
        var labColumns = Enumerable.Range(0, 8).Select(i => new Map2AcceptancePlan.LabColumn { Name = "c" + i, Material = "m" + i, Texture = i == 1 ? "swap_d" : null, Cell = new[] { .5f, .5f, 0f, 0f },
            Factor = i == 0 ? 0xFF102030u : 0u, Turn = i == 7 }).ToList();
        var lab = Map2AcceptancePlan.LabLayout(labColumns);
        var rowNames = Map2AcceptancePlan.LabRows.Select(r => r.Name).ToList();
        bool apart = lab.All(p => lab.All(q => ReferenceEquals(p, q) || Math.Abs(p.Dx - q.Dx) >= p.Half + q.Half - 1e-9 || Math.Abs(p.Dy - q.Dy) >= p.Half + q.Half - 1e-9));
        bool ordered = labColumns.All(c => lab.Where(d => d.Column == c.Name).Select(d => d.Dy).SequenceEqual(lab.Where(d => d.Column == c.Name).Select(d => d.Dy).OrderBy(v => v)))
                       && lab.Where(d => d.Row == "full").Select(d => d.Dx).SequenceEqual(lab.Where(d => d.Row == "full").Select(d => d.Dx).OrderBy(v => v));
        bool rowsOk = lab.All(d => (d.Row == "cell" ? d.Atlas.SequenceEqual(new[] { .5f, .5f, 0f, 0f }) : d.Atlas.SequenceEqual(new[] { 1f, 1f, 0f, 0f }))
                                   && (d.Factor != 0) == (d.Column == "c0" && (d.Row == "factor" || d.Row == "large")) && (d.Texture == "swap_d") == (d.Column == "c1") && d.Turn == (d.Column == "c7"));
        Check(lab.Count == 8 * rowNames.Count && lab.Select(d => d.Id).Distinct().Count() == lab.Count && apart && ordered && rowsOk && Math.Abs(lab.Average(d => d.Dx)) < 1e-9
              && rowNames.SequenceEqual(new[] { "full", "cell", "factor", "fit", "clip", "plane", "large" }) && Map2AcceptancePlan.Budget("w1.decalLab") > 0,
              "the decal lab lays every column in every row (" + lab.Count + " decals), no two boxes overlap, columns west to east and rows south to north around the spot, the cell row alone on the column's cell, factors only on factor rows");
        // MMR-291: the mesh columns lay their own grid centred on the same spot; the decal grid is unchanged. MMR-291-W: its rows are the passes
        // against the water (blend, depth, under, forward, afterPostfx), each piece on the spot's surface with its row's pass, under 0.05 below it.
        var withMeshes = labColumns.Concat(new[] { "veins", "rivers", "magicWater" }.Select(m => new Map2AcceptancePlan.LabColumn { Name = "mesh:" + m, Mesh = m, Material = m, Cell = new[] { .25f, .25f, 0f, 0f }, Factor = 0x80FFFFFFu })).ToList();
        var mixed = Map2AcceptancePlan.LabLayout(withMeshes);
        var meshEntries = mixed.Where(d => d.Mesh != null).ToList();
        bool decalsSame = mixed.Where(d => d.Mesh == null).Select(d => d.Id + " " + d.Dx + " " + d.Dy).SequenceEqual(lab.Select(d => d.Id + " " + d.Dx + " " + d.Dy));
        bool meshApart = meshEntries.All(p => meshEntries.All(q => ReferenceEquals(p, q) || Math.Abs(p.Dx - q.Dx) >= p.Half + q.Half - 1e-9 || Math.Abs(p.Dy - q.Dy) >= p.Half + q.Half - 1e-9));
        var passOf = new Dictionary<string, string> { ["blend"] = "blend", ["depth"] = "depth", ["under"] = "depth", ["forward"] = "forward", ["afterPostfx"] = "afterPostfx" };
        Check(decalsSame && meshEntries.Count == 3 * Map2AcceptancePlan.LabMeshRows.Length && meshEntries.All(d => Map2AcceptancePlan.LabMeshRows.Contains(d.Row) && d.Id == d.Column + "/" + d.Row)
              && Math.Abs(meshEntries.Average(d => d.Dx)) < 1e-9 && Math.Abs(meshEntries.Average(d => d.Dy)) < 1e-9 && meshApart
              && meshEntries.All(d => d.Frame == "surface" && d.Factor == 0 && d.Pass == passOf[d.Row] && d.Atlas.SequenceEqual(new[] { .25f, .25f, 0f, 0f })
                                      && (d.Row == "under" ? d.Lift == -0.05 : Double.IsNaN(d.Lift)))
              && Map2AcceptancePlan.LabMeshRows.SequenceEqual(new[] { "blend", "depth", "under", "forward", "afterPostfx" }),
              "the decal lab's mesh columns (MMR-291, rows MMR-291-W) lay a second grid on the same spot, " + meshEntries.Count + " pieces in rows blend, depth, under (the depth pass 0.05 under the surface), forward and afterPostfx, " +
              "each the column's cell on the spot's surface in its row's pass, the decal grid unchanged");
        Func<double, double, double> hill = (x, y) => x < 50 ? 3 + 0.01 * x : 3 + 0.3 * (x - 50);
        Func<double, double, bool> wetNorth = (x, y) => y > 75;
        var labCandidates = new List<double[]> { new double[] { 20, 40 }, new double[] { 70, 40 }, new double[] { 20, 80 } };
        var labLand = Map2AcceptancePlan.LabLand(labCandidates, hill, wetNorth, new List<double[]> { new double[] { 22, 42 } }, 10, 6, 2, 1.5, 0.5, 3);
        Func<double, double, double> basin = (x, y) => x > 100 ? -5 : 2;
        var labSea = Map2AcceptancePlan.LabSea(new List<double[]> { new double[] { 90, 0 }, new double[] { 130, 0 }, new double[] { 300, 0 } }, basin, new double[] { 0, 0 }, 10, 6, 2, 1);
        var labFree = Map2AcceptancePlan.LabLand(labCandidates, hill, wetNorth, null, 10, 6, 2, 1.5, 0.5, 3);
        Check(labLand == null && labFree != null && labFree[0] == 20 && labFree[1] == 40 && labSea != null && labSea[0] == 130,
              "the lab's land spot is the flattest dry candidate clear of every avoided point (none here: the flat one is crowded, the other two are steep or wet), its sea spot the nearest wholly deep one");

        // MMR-247 (integration): the hang repro after the lab: two variants, each a w1 step with its own budget (the orchestrator names the step a
        // game blocked in by its w1.<name> start line); the far teleport takes the point nearest ReproTeleport units, never nearer than the minimum.
        var variants = Map2AcceptancePlan.HangReproVariants;
        Func<double[], double[], double> flat = (p, q) => Math.Sqrt(Math.Pow(p[0] - q[0], 2) + Math.Pow(p[1] - q[1], 2));
        var teleports = new List<double[]> { new double[] { 100, 0 }, new double[] { 240, 0 }, new double[] { 300, 0 }, new double[] { 0, 270 } };
        // MMR-247 X: the bisect variants: one step each, names unique, every switch value a known mode; the lab runs the product's two.
        var repros = Map2AcceptancePlan.HangRepros;
        Check(repros.Length == 7 && repros.Select(r => r.Step).Distinct().Count() == 7 && repros.All(r => (r.Kind == "war" || r.Kind == "castle") && new[] { "Recreate", "Move", "Frozen" }.Contains(r.DressingEntities))
              && repros.Count(r => r.DressingEntities == "Recreate" && r.MagicRefresh && r.CopySync) == 2 && repros.Single(r => r.Step == "w1.hangRepro.warNoDressingMoves").DressingEntities == "Frozen"
              && !repros.Single(r => r.Step == "w1.hangRepro.warNoMagic").MagicRefresh && !repros.Single(r => r.Step == "w1.hangRepro.warNoCopySync").CopySync
              && Map2AcceptancePlan.LabReproVariants.SequenceEqual(new[] { "w1.hangRepro.war", "w1.hangRepro.castle" }) && Map2AcceptancePlan.LabReproVariants.All(v => variants.Contains(v)),
              "the hang repro's seven variants (MMR-247 X): war and castle run the fix, the controls move the dressing entities in place, the bisect variants freeze them or turn the magic refresh or the copy sync off; the lab runs war and castle");
        Check(variants.Length == 7 && variants.All(v => v.StartsWith("w1.hangRepro.", StringComparison.Ordinal) && Map2AcceptancePlan.Budget(v) > 0) && Map2AcceptancePlan.Budget("w1.saveLab") > 0
              && Map2AcceptancePlan.ReproFrames == 300 && Map2AcceptancePlan.FarPoint(teleports, new double[] { 0, 0 }, 256, 150, flat) == 3
              && Map2AcceptancePlan.FarPoint(teleports, new double[] { 0, 0 }, 256, 400, flat) == -1 && Map2AcceptancePlan.FarPoint(null, new double[] { 0, 0 }, 256, 150, flat) == -1,
              "the hang repro's variants (" + String.Join(", ", variants) + ") and the lab's save have budgets; the far teleport takes the point nearest 256 units (270 over 240 and 300), none past the minimum");
        Siege();
    }

    // MMR-247 F (acc6 c11): the siege case's timing, completion and sally guard.
    private static void Siege()
    {
        // acc6's camp went up at campaign hour 2187098.0118 (hour % 4 == 2): the sally check's hour began 1.99 h later and the garrison sallied
        // at 2.06 h, while the old setup (observe 1.5 h, advance to 0.99 at 0.0106 per hour: 0.94 h more) ran to 2.44 h.
        double acc6 = 2187098.0117854723, rate = 0.0106314663;
        double until = Map2AcceptancePlan.HoursToSallyCheck(acc6);
        Check(Math.Abs(until - 1.9882) < 1e-3 && Map2AcceptancePlan.HoursToSallyCheck(2187100.3) == 0 && Math.Abs(Map2AcceptancePlan.HoursToSallyCheck(2187101) - 3) < 1e-9
              && Math.Abs(Map2AcceptancePlan.HoursToSallyCheck(2187099.5) - 0.5) < 1e-9, "the sally check's next hour: 1.99 h after acc6's camp, 0 inside a check hour, 3 h from the hour after one (" + Map2AcceptancePlan.F(until) + ")");
        Check(1.5 + (1 - 0.99) / rate > until, "acc6's old setup ran past the check hour (" + Map2AcceptancePlan.F(1.5 + (1 - 0.99) / rate) + " h against " + Map2AcceptancePlan.F(until) + " h)");
        var t = Map2AcceptancePlan.Timing(acc6, 1.5, 0.1, 0.15, 0.25);
        Check(t.WaitHours == 0 && t.ObserveHours == 1.5 && t.Fits && t.ObserveHours + 0.1 + 0.15 <= until, "at acc6's hour the new setup fits without a wait: observe 1.5 h, complete in 0.1 h, 0.15 h before the check");
        var shortened = Map2AcceptancePlan.Timing(2187099.0, 1.5, 0.1, 0.15, 0.25);
        Check(shortened.WaitHours == 0 && Math.Abs(shortened.ObserveHours - 0.75) < 1e-9 && shortened.Fits, "one hour before the check the observation is shortened to 0.75 h (" + Map2AcceptancePlan.F(shortened.ObserveHours) + ")");
        var waits = Map2AcceptancePlan.Timing(2187099.8, 1.5, 0.1, 0.15, 0.25);
        Check(Math.Abs(waits.WaitHours - 1.2) < 1e-6 && waits.UntilCheck == 3 && waits.ObserveHours == 1.5 && waits.Fits
              && Map2AcceptancePlan.HoursToSallyCheck(2187099.8 + waits.WaitHours) >= waits.ObserveHours + 0.25, "0.2 h before the check the case waits 1.2 h for the check hour to pass, then observes 1.5 h (" + Map2AcceptancePlan.F(waits.WaitHours) + ")");
        var inside = Map2AcceptancePlan.Timing(2187100.4, 1.5, 0.1, 0.15, 0.25);
        Check(Math.Abs(inside.WaitHours - 0.6) < 1e-6 && inside.Fits, "inside a check hour the case waits for it to end (" + Map2AcceptancePlan.F(inside.WaitHours) + " h)");
        bool timed = true;
        for (int i = 0; i < 400; i++)
        {
            double now = 2187000 + i * 0.0137;
            var x = Map2AcceptancePlan.Timing(now, 1.5, 0.1, 0.15, 0.25);
            double up = now + x.WaitHours, done = up + x.ObserveHours + 0.1;
            // No check hour (Floor(h) % 4 == 0) may begin between the camp going up and the camp's completion plus the margin.
            for (double h = Math.Ceiling(up); h < done + 0.15 - 1e-9; h++) if ((long)h % 4 == 0) timed = false;
            if ((long)Math.Floor(up) % 4 == 0 || x.ObserveHours < 0.25 || !x.Fits) timed = false;
        }
        Check(timed, "over 400 start hours no camp is up during a sally check hour before its completion and margin, and every observation is at least the minimum");
        double to = Map2AcceptancePlan.CompletionFrom(rate, 0.1, 0.99);
        Check(Math.Abs(to - (1 - rate * 0.1)) < 1e-12 && Map2AcceptancePlan.CompletionFrom(0, 0.1, 0.99) == 0.99 && Map2AcceptancePlan.CompletionFrom(5, 0.1, 0.99) == 0.99 && to < 1,
              "the advance leaves 0.1 h of the installed rate to the engine's tick (" + to.ToString("0.#####") + "), never under siegeCompletionFrom or at 1");
        // The sally guard: acc6's garrison against 150 recruits (strengths in the engine's units, a recruit 1, the garrison's men 2 each).
        int crew = Map2AcceptancePlan.SallyCrew(388, 150, 1, 1.5, 1000);
        Check(Map2AcceptancePlan.SalliesOut(388, 150) && crew == 141 && !Map2AcceptancePlan.SalliesOut(388, 150 + crew) && 2 * (150 + crew) >= 1.5 * 388
              && Map2AcceptancePlan.SallyCrew(100, 150, 1, 1.5, 1000) == 0 && Map2AcceptancePlan.SallyCrew(388, 150, 1, 1.5, 50) == 50 && Map2AcceptancePlan.SallyCrew(388, 150, 0, 1.5, 1000) == 0,
              "the sally guard adds 141 recruits against a 388-strength garrison (none against 100, at most the cap, none without a troop strength)");
        bool threw = false;
        try { Map2AcceptancePlan.Timing(acc6, 0.1, 0.1, 0.15, 0.25); } catch (ArgumentException) { threw = true; }
        Check(threw, "an observation under the minimum is refused");
    }

    // The bundle's travel cells (cells.bin MNBLTC02: header, then the class and land type planes).
    private static void Cells(string bundle, out byte[] classes, out byte[] types)
    {
        var bytes = File.ReadAllBytes(Path.Combine(bundle, "compiled/travel/cells.bin"));
        if (Encoding.ASCII.GetString(bytes, 0, 8) != "MNBLTC02") throw new InvalidDataException("cells.bin is not MNBLTC02");
        int n = BitConverter.ToInt32(bytes, 8) * BitConverter.ToInt32(bytes, 12);
        classes = new byte[n]; types = new byte[n];
        Array.Copy(bytes, 16, classes, 0, n); Array.Copy(bytes, 16 + 2 * n, types, 0, n);
    }

    private static IDictionary<string, object> Load(string path) { return (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(path).TrimStart('\uFEFF')); }

    // The biome plane as GeneratedCampaignInitializer.CellBiomes reads it: masks.bin plane 0 at the sample nearest physical = logical +
    // (strip, poleMargin), clamped, named by the bundle's own terrain-materials.json.
    private static Func<double, double, string> BiomeAt(string bundle, out double width, out double height, out List<string> land)
    {
        var masks = File.ReadAllBytes(Path.Combine(bundle, "compiled/terrain/masks.bin"));
        if (Encoding.ASCII.GetString(masks, 0, 8) != "MNBLMK01") throw new InvalidDataException("masks.bin is not MNBLMK01");
        int side = (int)BitConverter.ToUInt32(masks, 8);
        var layout = (IDictionary<string, object>)Load(Path.Combine(bundle, "compiled/terrain/projection.json"))["periodicLayout"];
        double physical = Convert.ToDouble(layout["physicalWidth"]), strip = Convert.ToDouble(layout["strip"]), pole = Convert.ToDouble(layout["poleMargin"]);
        var canonical = (object[])layout["canonicalPhysicalX"];
        width = Convert.ToDouble(canonical[1]) - Convert.ToDouble(canonical[0]);
        height = Convert.ToDouble(layout["physicalHeight"]) - 2 * pole;
        double spacing = physical / (side - 1);
        var names = new Dictionary<int, string>(); land = new List<string>();
        foreach (IDictionary<string, object> b in (object[])Load(Path.Combine(bundle, "policies/terrain-materials.json"))["biomes"])
        {
            names[Convert.ToInt32(b["id"])] = (string)b["core"];
            if (!Equals(b["water"], true)) land.Add((string)b["core"]);
        }
        return (x, y) =>
        {
            int c = (int)Math.Max(0, Math.Min(side - 1, Math.Round((x + strip) / spacing))), r = (int)Math.Max(0, Math.Min(side - 1, Math.Round((y + pole) / spacing)));
            string name; return names.TryGetValue(masks[12 + r * side + c], out name) ? name : null;
        };
    }

    private static object World(string key, string bundle)
    {
        double width, height; List<string> land;
        var biomeAt = BiomeAt(bundle, out width, out height, out land);
        var grid = Map2AcceptancePlan.Sample(biomeAt, width, height, 2);
        var census = Map2AcceptancePlan.Census(grid);
        var spots = Map2AcceptancePlan.Interior(grid, land, 6);
        var present = land.Where(b => census.ContainsKey(b)).ToList();
        Check(present.Count > 0 && present.All(b => spots.ContainsKey(b)), key + ": every land biome on the plane has a spot (" + String.Join(", ", present) + ")");
        Check(spots.All(kv => biomeAt(kv.Value.X, kv.Value.Y) == kv.Key && kv.Value.X >= 0 && kv.Value.X < width && kv.Value.Y >= 0 && kv.Value.Y <= height),
              key + ": each spot lies inside the logical world on its own biome");
        // Interior: the spot's window holds more of its biome than the world does on average (the biome planes are speckled at the probe's
        // resolution: A's grassland peaks at 41% of a 26-unit window), for every biome with 400+ samples.
        var big = spots.Where(kv => census[kv.Key] >= 400).ToList();
        int total = grid.Biomes.Length;
        Check(big.All(kv => (double)kv.Value.Score / kv.Value.Window > (double)census[kv.Key] / total),
              key + ": the spot of every biome with 400+ samples is denser in it than the world (" + String.Join(", ", big.Select(kv => kv.Key + " " + kv.Value.Score + "/" + kv.Value.Window + " vs " +
              (100.0 * census[kv.Key] / total).ToString("0.#") + "%")) + ")");
        byte[] classes, types; Cells(bundle, out classes, out types);
        var picked = Map2AcceptancePlan.CellSamples(classes, types, 50);
        var landTypes = types.Where((t, i) => classes[i] == 0 && t != 0).Distinct().OrderBy(t => t).Select(t => (int)t).ToList();
        Check(picked.Keys.SequenceEqual(landTypes) && picked.All(kv => kv.Value.Count == Math.Min(50, types.Where((t, i) => classes[i] == 0 && t == kv.Key).Count()) && kv.Value.All(i => classes[i] == 0 && types[i] == kv.Key))
              && Map2AcceptancePlan.CellSamples(classes, types, 50).SelectMany(kv => kv.Value).SequenceEqual(picked.SelectMany(kv => kv.Value)),
              key + ": land-cell samples cover every land type of the world (" + String.Join(", ", picked.Select(kv => kv.Key + ":" + kv.Value.Count)) + "), land cells of that type only, the same on a re-run");
        var seam = Map2AcceptancePlan.Seam(biomeAt, b => land.Contains(b), width, height, 1, Map2AcceptancePlan.SeamInsets, 12);
        Check(seam.All(r => r.West != r.East && land.Contains(r.West) && land.Contains(r.East)), key + ": " + seam.Count + " seam crossings, each between two different land biomes (" +
              String.Join(", ", seam.Take(4).Select(r => r.West + ">" + r.East + " at y " + r.Y + " inset " + r.Inset)) + ")");
        return new SortedDictionary<string, object>(StringComparer.Ordinal) { ["bundle"] = Path.GetFileName(bundle), ["width"] = width, ["height"] = height, ["census"] = census,
            ["spots"] = spots.ToDictionary(kv => kv.Key, kv => (object)kv.Value.Record()), ["seam"] = seam.Select(r => (object)new object[] { r.Y, r.West, r.East, r.Inset, r.Run }).ToList() };
    }
}
