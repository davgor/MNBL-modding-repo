using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;

// MMR-46 world preview tests, without the game: WorldPreviewTests BUNDLE_A BUNDLE_B REPO_DIR OUT_DIR
// The preview of the two real compiled worlds, built as the setup screen builds it (the world's surface, its settlement layout from
// evidence/world/settlement-position-report.json, its campaign records and FactionFactory's kingdoms). Checks: every layout settlement and
// port is a marker at the layout's own physical position; every settlement stands on a land sample of the raster and every port touches
// water; the regions are the kingdoms holding fortifications, with FactionFactory's names and colours; determinism; the two worlds give two
// different previews; labels are shortened only when long, with the full text kept. Writes the previews (PNG) and their records.
internal static class WorldPreviewTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }
    private static IDictionary<string, object> Parse(byte[] bytes) { return (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllBytes(path)); }

    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: WorldPreviewTests BUNDLE_A BUNDLE_B REPO_DIR OUT_DIR"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.world-preview-tests", ["version"] = 1, ["ticket"] = "MMR-46" };
        try { Run(args[0], args[1], args[2], args[3], evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        Directory.CreateDirectory(args[3]);
        File.WriteAllText(Path.Combine(args[3], "world-preview-tests.json"), Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // Each campaign record's kind (town, castle, village) and name, from the bundle.
    private static Dictionary<string, Tuple<string, string>> CampaignRecords(string bundleDir)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var campaign = Parse(WorldDataBundle.Open(bundleDir, host).ReadVerified("compiled/campaign/campaign.json"));
        var result = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
        foreach (var kind in new[] { "town", "castle", "village" })
            foreach (IDictionary<string, object> r in (IEnumerable)campaign[kind + "s"]) result[(string)r["id"]] = Tuple.Create(kind, (string)r["name"]);
        return result;
    }

    // Each village's bound settlement, from the bundle's campaign records.
    private static Dictionary<string, string> Bounds(string bundleDir)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var campaign = Parse(WorldDataBundle.Open(bundleDir, host).ReadVerified("compiled/campaign/campaign.json"));
        return ((IEnumerable)campaign["villages"]).Cast<IDictionary<string, object>>().Where(v => v.ContainsKey("bound") && v["bound"] is string).ToDictionary(v => (string)v["id"], v => (string)v["bound"], StringComparer.Ordinal);
    }

    private static VillageBinding.Result lastBinding;
    private static WorldPreview Build(string bundleDir, string repo, IDictionary<string, object> layout, out FactionFactory.Result factions)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var surface = WorldSurface.FromBundle(bundle);
        var campaign = Parse(bundle.ReadVerified("compiled/campaign/campaign.json"));
        var idMap = Parse(bundle.ReadVerified("compiled/campaign/id-map.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        factions = FactionFactory.Build(campaign, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates);
        var problems = new List<string>();
        var binding = VillageBinding.FromRecords(Load(Path.Combine(repo, "worldgen/policies/economy.json")), campaign, Parse(bundle.ReadVerified("compiled/travel/travel-graph.json")), problems);
        if (problems.Count > 0) throw new InvalidDataException("village binding: " + String.Join("; ", problems));
        lastBinding = binding;
        return WorldPreview.Build(surface, layout, campaign, factions, "scene of " + bundle.WorldContentId.Substring(0, 12), binding.BoundOf);
    }

    private static void Run(string bundleA, string bundleB, string repo, string outDir, SortedDictionary<string, object> evidence)
    {
        var report = Load(Path.Combine(repo, "evidence/world/settlement-position-report.json"));
        var worlds = (IDictionary<string, object>)report["worlds"];
        var previews = new Dictionary<string, WorldPreview>();
        foreach (var label in new[] { "A", "B" })
        {
            var layout = (IDictionary<string, object>)worlds[label];
            FactionFactory.Result factions;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var p = Build(label == "A" ? bundleA : bundleB, repo, layout, out factions);
            double seconds = watch.Elapsed.TotalSeconds;
            previews[label] = p;
            var rows = ((IEnumerable)layout["settlements"]).Cast<IDictionary<string, object>>().ToList();
            int ports = rows.Count(r => r.ContainsKey("port") && r["port"] != null);
            Func<IDictionary<string, object>, string, double[]> at = (r, key) => ((IEnumerable)r[key]).Cast<object>().Select(x => Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            bool positions = rows.All(r =>
            {
                var c = at(r, "centre");
                var m = p.Markers.FirstOrDefault(x => x.Id == (string)r["id"] && x.Kind != "port");
                bool ok = m != null && m.PhysicalX == c[0] && m.PhysicalY == c[1] && m.Kind == (string)r["kind"];
                if (r.ContainsKey("port") && r["port"] != null) { var q = at(r, "port"); var pm = p.Markers.FirstOrDefault(x => x.Id == (string)r["id"] && x.Kind == "port"); ok = ok && pm != null && pm.PhysicalX == q[0] && pm.PhysicalY == q[1]; }
                return ok;
            });
            // Names and kinds against the campaign records themselves (not the layout row a marker was made from).
            var campaignRecords = CampaignRecords(label == "A" ? bundleA : bundleB);
            var recordProblems = p.Markers.Where(m => { Tuple<string, string> rec; return !campaignRecords.TryGetValue(m.Id, out rec) || (m.Kind != "port" && rec.Item1 != m.Kind) || FactionFactory.Clean(rec.Item2) != m.Name; }).Select(m => m.Id).ToList();
            positions &= recordProblems.Count == 0;
            // Harbour villages (fleets land there) as the layout places them.
            var harbourRows = new HashSet<string>(rows.Where(r => r.ContainsKey("harbour") && Equals(r["harbour"], true)).Select(r => (string)r["id"]), StringComparer.Ordinal);
            positions &= p.Markers.Where(m => m.Kind == "village").All(m => m.Harbour == harbourRows.Contains(m.Id)) && harbourRows.Count == p.Counts["harbourVillages"];
            Check(p.Markers.Count == rows.Count + ports && positions && p.WorldContentId == (string)layout["worldContentId"] && p.LayoutSha256 == (string)layout["layoutSha256"],
                  "world " + label + ": every one of the layout's " + rows.Count + " settlements and " + ports + " ports is a marker at the layout's own physical position (the scene's placement), named and kinded as the campaign records it; content " + p.WorldContentId.Substring(0, 12) + ", layout " + p.LayoutSha256.Substring(0, 12) + " (" + Math.Round(seconds, 2) + " s)");

            // Settlements on land samples, ports beside water, in the raster itself.
            Func<double, double, int[]> pixel = (u, v) => new[] { Math.Min(p.Width - 1, (int)(u * p.Width)), Math.Min(p.Height - 1, (int)(v * p.Height)) };
            Func<int, int, bool> water = (i, j) => j >= 0 && j < p.Height && p.Water[j * p.Width + ((i + p.Width) % p.Width)] != 0;
            // A settlement stands on land: its pixel, or one within 2 px (about 2 world units) for a settlement on the coast, where the surface's
            // water intent (its mask resolution) can put the nearest sample in the sea while the navigation point is on land.
            var offLand = p.Markers.Where(m => m.Kind != "port").Where(m => { var x = pixel(m.U, m.V); for (int dj = -2; dj <= 2; dj++) for (int di = -2; di <= 2; di++) if (!water(x[0] + di, x[1] + dj)) return false; return true; }).Select(m => m.Id).ToList();
            var dryPorts = p.Markers.Where(m => m.Kind == "port").Where(m => { var x = pixel(m.U, m.V); for (int dj = -3; dj <= 3; dj++) for (int di = -3; di <= 3; di++) if (water(x[0] + di, x[1] + dj)) return false; return true; }).Select(m => m.Id).ToList();
            Check(offLand.Count == 0 && dryPorts.Count == 0,
                  "world " + label + ": the raster agrees with the placement: all " + rows.Count + " settlements stand on a land pixel or within 2 px of one (coastal), and every port is within 3 px of water (" +
                  offLand.Count + " settlements off land, " + dryPorts.Count + " ports dry)");
            // Regions: the kingdoms holding fortifications, with FactionFactory's names and colours.
            var holding = new HashSet<string>(p.Markers.Where(m => (m.Kind == "town" || m.Kind == "castle") && m.Kingdom != null).Select(m => m.Kingdom));
            Check(p.Regions.Count == holding.Count && p.Regions.All(r => factions.Kingdoms.Any(k => k.Id == r.Kingdom && k.Name == r.Name)),
                  "world " + label + ": " + p.Regions.Count + " regions, one per kingdom holding a fortification, named as the campaign registers them (FactionFactory's display names, e.g. " + String.Join(", ", p.Regions.Take(3).Select(r => r.Name)) + ")");
            // North up, as the campaign map shows it (+y north): V runs against the map's y (review finding: the first build drew it upside down).
            var northMost = p.Markers.OrderByDescending(m => m.PhysicalY).First(); var southMost = p.Markers.OrderBy(m => m.PhysicalY).First();
            Check(northMost.V < southMost.V && p.Markers.All(m => Math.Abs(m.V - (1 - (m.PhysicalY - p.OriginY) / p.LogicalHeight)) < 1e-9),
                  "world " + label + ": north is up: the northernmost place (" + northMost.Id + ", y " + Math.Round(northMost.PhysicalY, 1) + ") is drawn above the southernmost (y " + Math.Round(southMost.PhysicalY, 1) + "), every marker's height on the picture running against its y");
            // A village is shown in the kingdom of the settlement the campaign's binding chose (VillageBinding, which SettlementFactory runs):
            // runs 43992 and 13920 found 3 world A villages rebound into another kingdom than their record's town.
            var binding = lastBinding;
            var byId = p.Markers.Where(m => m.Kind != "port").ToDictionary(m => m.Id, StringComparer.Ordinal);
            var recordBound = Bounds(label == "A" ? bundleA : bundleB);
            var villageKingdoms = p.Markers.Where(m => m.Kind == "village").Where(m => { string b; MNBL.ProceduralWorld.UI.WorldPreview.Marker owner; return binding.BoundOf.TryGetValue(m.Id, out b) && byId.TryGetValue(b, out owner) && owner.Kingdom != m.Kingdom; }).Select(m => m.Id).ToList();
            int crossKingdom = binding.Rebound.Count(kv => { MNBL.ProceduralWorld.UI.WorldPreview.Marker from, to; string src; return recordBound.TryGetValue(kv.Key, out src) && byId.TryGetValue(src, out from) && byId.TryGetValue(kv.Value, out to) && from.Kingdom != to.Kingdom; });
            Check(villageKingdoms.Count == 0 && p.Markers.Count(m => m.Kind == "village") > 0 && binding.BoundOf.Count == p.Markers.Count(m => m.Kind == "village"),
                  "world " + label + ": every village is shown in the kingdom of its bound settlement as the campaign binds it (" + binding.Rebound.Count + " rebound, " + crossKingdom + " into another kingdom; " + villageKingdoms.Count + " differ)");
            // Determinism.
            FactionFactory.Result again; var q2 = Build(label == "A" ? bundleA : bundleB, repo, layout, out again);
            Check(q2.RasterSha256 == p.RasterSha256 && Json.Serialize(q2.Record()) == Json.Serialize(p.Record()), "world " + label + ": building twice gives the same raster (" + p.RasterSha256.Substring(0, 12) + ") and record");
            var png = p.Png();
            File.WriteAllBytes(Path.Combine(outDir, "preview-" + label + ".png"), png);
            File.WriteAllText(Path.Combine(outDir, "preview-" + label + ".json"), Json.Serialize(p.Record()), new UTF8Encoding(false));
            Check(png.Length > 1000 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', "world " + label + ": the preview PNG is written (" + png.Length + " bytes, " + p.Width + " x " + p.Height + ")");
            evidence["world" + label] = new Dictionary<string, object> { ["record"] = Strip(p.Record()), ["seconds"] = Math.Round(seconds, 2), ["pngSha256"] = WorldPreview.Sha256(png), ["offLandPixels"] = offLand };
        }
        Check(previews["A"].RasterSha256 != previews["B"].RasterSha256 && previews["A"].WorldContentId != previews["B"].WorldContentId,
              "the two worlds give two different previews (A " + previews["A"].RasterSha256.Substring(0, 12) + ", B " + previews["B"].RasterSha256.Substring(0, 12) + "): the preview is the world's, not a fixed picture");
        // Labels: shortened only when long; the panel shows the full text (WorldPreviewViewModel's HoverText and kingdom list).
        var names = previews.Values.SelectMany(p => p.Markers.Select(m => m.Name).Concat(p.Regions.Select(r => r.Name))).Distinct().ToList();
        string longest = names.OrderByDescending(n => n.Length).First();
        Check(WorldPreview.Shorten("Forimar", 18) == "Forimar" && WorldPreview.Shorten(longest, 18).Length <= 18 && WorldPreview.Shorten(longest, 18).EndsWith("…") && longest.Length > 18,
              "labels: a short name is shown whole; the longest generated name (" + longest.Length + " characters, \"" + longest + "\") is shortened to \"" + WorldPreview.Shorten(longest, 18) + "\"");
    }

    // The record without the per-marker list (kept in the preview-<world>.json files).
    private static Dictionary<string, object> Strip(Dictionary<string, object> r) { var c = new Dictionary<string, object>(r); c.Remove("markers"); return c; }
}
