using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

// MMR-41 offline tests, without the game:
//   PlayabilityTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON
//   cache key      the canonical key text; every keyed input (game, models, cost limit, per-type cache parameters, world, scene, navmesh,
//                  native CRCs, periodic identity, adapter, schema, each settlement's kind, position, gate and port, a settlement added or
//                  removed) changes the key and is named by Differences; insertion order does not
//   cache file     the installed format read without the engine (written here as NavigationCache.Serialize writes it): counts, ids,
//                  canonical pairs, the All land ratio, content comparison; truncated, trailing and implausible files refused
//   store          a published cache passes NavigationCacheStore.Check for its key and scene; a moved port, a tampered file, other scene
//                  CRCs, an unknown settlement, a face beyond the navmesh, a missing file and another schema are each refused
//   route set      on worlds A and B: the semantic route set from the bundle and the settlement layout, every category in both
//                  directions, its types and capabilities; refusals for a fief across water of a kingdom without ships and a village off
//                  its market's land
//   dry run        the route set and the pending-runtime flags through the managed mesh engine (NavigationValidator's offline stand-in,
//                  never evidence of engine behaviour): every route agrees, every flag resolves; a failed host check, failed settlement
//                  checks and failed routes each fail their flags and make the report not activatable
internal static class PlayabilityTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 6) { Console.Error.WriteLine("usage: PlayabilityTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON REPORT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.playability-tests", ["version"] = 1, ["ticket"] = "MMR-41" };
        var report = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.playability-offline", ["version"] = 1, ["ticket"] = "MMR-41" };
        try { Run(args, evidence, report); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[4], Serializer.Serialize(evidence), new UTF8Encoding(false));
        File.WriteAllText(args[5], Serializer.Serialize(report), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static NavigationCacheKey SampleKey(Action<NavigationCacheKey> change = null, bool reverse = false)
    {
        var k = new NavigationCacheKey();
        var inputs = new List<KeyValuePair<string, string>> {
            new KeyValuePair<string, string>("game", "v1.4.8.119303 / NavalDLC v1.2.8.119303"), new KeyValuePair<string, string>("distanceModel", "NavalDLC.GameComponents.NavalDLCMapDistanceModel 1.0.0.0"),
            new KeyValuePair<string, string>("partyNavigationModel", "NavalDLC.GameComponents.NavalDLCPartyNavigationModel 1.0.0.0"), new KeyValuePair<string, string>("pathFindingMaxCostLimit", "1000000"),
            new KeyValuePair<string, string>("Default.excludedFaceIds", "1,2,3"), new KeyValuePair<string, string>("Default.regionSwitchCosts", "50,50"), new KeyValuePair<string, string>("Default.agentRadius", "0.3"),
            new KeyValuePair<string, string>("Default.neighbourCostMultiplier", "1.2"), new KeyValuePair<string, string>("worldId", "mnbl_00040042"), new KeyValuePair<string, string>("scene", "mnbl_generated_00040042"),
            new KeyValuePair<string, string>("navmeshSha256", new string('a', 64)), new KeyValuePair<string, string>("sceneXmlCrc", "146201570"), new KeyValuePair<string, string>("navigationMeshCrc", "123456"),
            new KeyValuePair<string, string>("periodic", "planar"), new KeyValuePair<string, string>("distanceVersion", "mmr64/1"), new KeyValuePair<string, string>("adapter", WorldNavigationCacheAdapter),
            new KeyValuePair<string, string>("moduleSha256", new string('c', 64)), new KeyValuePair<string, string>("schema", NavigationCacheStore.Schema + " 1") };
        if (reverse) inputs.Reverse();
        foreach (var kv in inputs) k.Set(kv.Key, kv.Value);
        var settlements = new List<Action>
        {
            () => k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }),
            () => k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null),
            () => k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null)
        };
        if (reverse) settlements.Reverse();
        foreach (var s in settlements) s();
        if (change != null) change(k);
        return k;
    }
    private const string WorldNavigationCacheAdapter = "mmr41/1";

    // A cache file as NavigationCache.Serialize writes it.
    private static byte[] CacheFile(int type, uint xmlCrc, uint navCrc, IList<Tuple<string, bool, string, bool, float, float>> pairs, IList<Tuple<string, string>> neighbours, IList<Tuple<int, string, bool>> faces, Action<BinaryWriter> tail = null)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, new UTF8Encoding(false), true))
        {
            w.Write(xmlCrc); w.Write(navCrc);
            var groups = pairs.GroupBy(p => p.Item1 + ":" + p.Item2).ToList();
            w.Write(groups.Count);
            foreach (var g in groups)
            {
                w.Write(g.First().Item1); w.Write(g.First().Item2); w.Write(g.Count());
                foreach (var p in g) { w.Write(p.Item3); w.Write(p.Item4); w.Write(p.Item5); if (type == NavigationCacheFile.AllType) w.Write(p.Item6); }
            }
            w.Write(neighbours.Count); foreach (var n in neighbours) { w.Write(n.Item1); w.Write(n.Item2); }
            w.Write(faces.Count); foreach (var f in faces) { w.Write(f.Item1); w.Write(f.Item2); w.Write(f.Item3); }
            if (tail != null) tail(w);
        }
        return stream.ToArray();
    }

    private static void Run(string[] args, SortedDictionary<string, object> evidence, SortedDictionary<string, object> report)
    {
        string repo = args[0];
        // ---- cache key ----
        var key = SampleKey();
        Check(key.Text == SampleKey(null, true).Text && key.Sha256 == SampleKey(null, true).Sha256 && key.Text.StartsWith(NavigationCacheKey.Schema + " 1\n"), "key: canonical text and hash independent of insertion order (" + key.Sha256.Substring(0, 12) + ")");
        var changes = new Dictionary<string, Action<NavigationCacheKey>> {
            ["game"] = k => k.Set("game", "v1.4.9.0 / NavalDLC v1.2.8.119303"), ["distanceModel"] = k => k.Set("distanceModel", "TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel 1.0.0.0"),
            ["partyNavigationModel"] = k => k.Set("partyNavigationModel", "x 2.0"), ["pathFindingMaxCostLimit"] = k => k.Set("pathFindingMaxCostLimit", "2000000"),
            ["Default.excludedFaceIds"] = k => k.Set("Default.excludedFaceIds", "1,2"), ["Default.regionSwitchCosts"] = k => k.Set("Default.regionSwitchCosts", "50,51"),
            ["Default.agentRadius"] = k => k.Set("Default.agentRadius", "0.31"), ["Default.neighbourCostMultiplier"] = k => k.Set("Default.neighbourCostMultiplier", "1.3"),
            ["worldId"] = k => k.Set("worldId", "mnbl_00040043"), ["scene"] = k => k.Set("scene", "mnbl_world_x"), ["navmeshSha256"] = k => k.Set("navmeshSha256", new string('b', 64)),
            ["sceneXmlCrc"] = k => k.Set("sceneXmlCrc", "1"), ["navigationMeshCrc"] = k => k.Set("navigationMeshCrc", "1"), ["periodic"] = k => k.Set("periodic", "mmr64/1 " + new string('e', 64)),
            ["distanceVersion"] = k => k.Set("distanceVersion", "mmr65/1"), ["adapter"] = k => k.Set("adapter", "mmr41/2"), ["moduleSha256"] = k => k.Set("moduleSha256", new string('f', 64)),
            ["schema"] = k => k.Set("schema", NavigationCacheStore.Schema + " 2") };
        var keyRows = new List<object>(); bool allChange = true;
        foreach (var c in changes)
        {
            var changed = SampleKey(c.Value);
            var diff = NavigationCacheKey.Differences(key.Text, changed.Text);
            bool ok = changed.Sha256 != key.Sha256 && diff.SequenceEqual(new[] { c.Key });
            allChange &= ok; keyRows.Add(new Dictionary<string, object> { ["input"] = c.Key, ["changesKey"] = changed.Sha256 != key.Sha256, ["named"] = String.Join(",", diff) });
        }
        Check(allChange, "key: each of the " + changes.Count + " keyed inputs (game, models, cost limit, per-type cache parameters, world, scene, navmesh hash, native CRCs, periodic identity, distance version, adapter, module code, settlement order, schema) changes the key and is the one difference named");
        Func<Func<NavigationCacheKey>, string, bool> settlementChange = (make, id) => { var k = make(); return k.Sha256 != key.Sha256 && NavigationCacheKey.Differences(key.Text, k.Text).SequenceEqual(new[] { "settlement " + id }); };
        Func<Action<NavigationCacheKey>, NavigationCacheKey> rebuild = extra => { var k = new NavigationCacheKey(); foreach (var kv in key.Inputs) k.Set(kv.Key, kv.Value); extra(k); return k; };
        bool movedPort = settlementChange(() => rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14.001f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null); k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null); }), "w_town_1");
        bool movedGate = settlementChange(() => rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60.5f }, null); k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null); }), "w_town_2");
        bool movedCentre = settlementChange(() => rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null); k.Settlement("w_village_1", "village", new[] { 12f, 31f }, new[] { 12f, 30f }, null); }), "w_village_1");
        bool lostPort = settlementChange(() => rebuild(k => { k.Settlement("w_town_1", "town", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null); k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null); }), "w_town_1");
        bool added = settlementChange(() => SampleKey(k => k.Settlement("w_castle_1", "castle", new[] { 70f, 70f }, new[] { 71f, 70f }, null)), "w_castle_1");
        bool removed = settlementChange(() => rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }); k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null); }), "w_town_2");
        Check(movedPort && movedGate && movedCentre && lostPort && added && removed, "key: a port moved by 0.001, a gate moved, a centre moved, a port lost, a settlement added and one removed each change the key, named by settlement");
        Check(Throws(() => new NavigationCacheKey().Set("a=b", "x")) && Throws(() => new NavigationCacheKey().Set("a", "x\ny")) && Throws(() => SampleKey(k => k.Settlement("w_town_1", "town", new[] { 0f, 0f }, new[] { 0f, 0f }, null))),
            "key: an input name with '=', a value with a line break and a settlement keyed twice are refused");
        evidence["keyInputs"] = keyRows;

        // ---- cache file ----
        var pairs = new List<Tuple<string, bool, string, bool, float, float>> {
            Tuple.Create("w_town_1", false, "w_town_2", false, 55.5f, 0.25f), Tuple.Create("w_town_1", false, "w_village_1", false, 12.25f, 1f), Tuple.Create("w_town_1", true, "w_town_2", false, 70f, 0.5f) };
        var neighbours = new List<Tuple<string, string>> { Tuple.Create("w_town_1", "w_town_2"), Tuple.Create("w_town_2", "w_town_1") };
        var faces = new List<Tuple<int, string, bool>> { Tuple.Create(0, "w_town_1", false), Tuple.Create(5, "w_town_2", false), Tuple.Create(9, "w_town_1", true) };
        var all = CacheFile(NavigationCacheFile.AllType, 146201570u, 123456u, pairs, neighbours, faces);
        var parsed = NavigationCacheFile.Read(all, NavigationCacheFile.AllType);
        Check(parsed.SceneXmlCrc == 146201570u && parsed.NavigationMeshCrc == 123456u && parsed.Entrances == 2 && parsed.Pairs == 3 && parsed.NeighbourPairs == 2 && parsed.Faces == 3 && parsed.MaxFace == 9
            && parsed.Ids.SetEquals(new[] { "w_town_1", "w_town_2", "w_village_1" }) && parsed.Distances["w_town_1:0|w_town_2:0"] == 55.5f && parsed.Distances["w_town_1:0|w_town_2:0|ratio"] == 0.25f
            && parsed.Distances.ContainsKey("w_town_1:1|w_town_2:0") && parsed.FaceMap[9] == "w_town_1:1",
            "cache file: the installed All format read without the engine (CRCs, " + parsed.Entrances + " entrances, " + parsed.Pairs + " pairs with land ratios, neighbour pairs, the closest entrance per face)");
        var swapped = new List<Tuple<string, bool, string, bool, float, float>> { Tuple.Create("w_town_2", false, "w_town_1", false, 55.5f, 0.25f), Tuple.Create("w_village_1", false, "w_town_1", false, 12.25f, 1f), Tuple.Create("w_town_2", false, "w_town_1", true, 70f, 0.5f) };
        var roundTrip = NavigationCacheFile.Read(CacheFile(NavigationCacheFile.AllType, 146201570u, 123456u, swapped, neighbours, faces), NavigationCacheFile.AllType);
        var changedDistance = NavigationCacheFile.Read(CacheFile(NavigationCacheFile.AllType, 146201570u, 123456u, swapped.Select((p, i) => i == 0 ? Tuple.Create(p.Item1, p.Item2, p.Item3, p.Item4, 55.75f, p.Item6) : p).ToList(), neighbours, faces), NavigationCacheFile.AllType);
        var changedFace = NavigationCacheFile.Read(CacheFile(NavigationCacheFile.AllType, 146201570u, 123456u, swapped, neighbours, faces.Select(f => f.Item1 == 5 ? Tuple.Create(5, "w_town_1", false) : f).ToList()), NavigationCacheFile.AllType);
        Check(NavigationCacheFile.Compare(parsed, roundTrip).Count == 0 && NavigationCacheFile.Compare(parsed, changedDistance).Count == 1 && NavigationCacheFile.Compare(parsed, changedFace).Count == 1,
            "cache file: content compares equal with each pair stored the other way round (NavigationCacheElement.Sort), and a distance or a face's entrance changed is one difference");
        var deflt = NavigationCacheFile.Read(CacheFile(NavigationCacheFile.DefaultType, 1u, 2u, pairs, neighbours, faces), NavigationCacheFile.DefaultType);
        Check(deflt.Pairs == 3 && !deflt.Distances.Keys.Any(k => k.EndsWith("|ratio")), "cache file: the Default format has no land ratios");
        Check(Throws(() => NavigationCacheFile.Read(all.Take(all.Length - 3).ToArray(), NavigationCacheFile.AllType)) && Throws(() => NavigationCacheFile.Read(CacheFile(NavigationCacheFile.AllType, 1u, 2u, pairs, neighbours, faces, w => w.Write(7)), NavigationCacheFile.AllType))
            && Throws(() => NavigationCacheFile.Read(all, NavigationCacheFile.DefaultType)) && Throws(() => NavigationCacheFile.Read(BitConverter.GetBytes(1u).Concat(BitConverter.GetBytes(2u)).Concat(BitConverter.GetBytes(-4)).ToArray(), NavigationCacheFile.AllType)),
            "cache file: a truncated file, trailing bytes, the wrong type's layout and a negative count are refused");

        // ---- store ----
        string temp = Path.Combine(Path.GetTempPath(), "mnbl-playability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var files = new Dictionary<string, byte[]> { ["Default.bin"] = CacheFile(1, 146201570u, 123456u, pairs, neighbours, faces), ["Naval.bin"] = CacheFile(2, 146201570u, 123456u, pairs.Take(1).ToList(), neighbours, faces), ["All.bin"] = all };
            var manifest = NavigationCacheStore.Manifest(key, "mnbl_generated_00040042", "mnbl_00040042", 146201570u, 123456u, f => files[f], new Dictionary<string, object> { ["serializeMilliseconds"] = 1.0 }, "sha");
            var parsedManifest = NavigationCacheStore.ParseManifest(Serializer.Serialize(manifest));
            Func<IDictionary<string, object>, NavigationCacheKey, Func<string, byte[]>, uint, uint, int, List<string>> check = NavigationCacheStore.Check;
            var clean = check(parsedManifest, key, f => files[f], 146201570u, 123456u, 100);
            Check(clean.Count == 0, "store: a published cache passes for its key and scene" + (clean.Count > 0 ? " - " + String.Join("; ", clean) : ""));
            var moved = rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14.5f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null); k.Settlement("w_village_1", "village", new[] { 12f, 30f }, new[] { 12f, 30f }, null); });
            var r1 = check(parsedManifest, moved, f => files[f], 146201570u, 123456u, 100);
            var tampered = new Dictionary<string, byte[]>(files); tampered["All.bin"] = (byte[])all.Clone(); tampered["All.bin"][tampered["All.bin"].Length / 2] ^= 0x5a;
            var r2 = check(parsedManifest, key, f => tampered[f], 146201570u, 123456u, 100);
            var r3 = check(parsedManifest, key, f => files[f], 1u, 123456u, 100);
            var stranger = rebuild(k => { k.Settlement("w_town_1", "town+port", new[] { 10f, 20f }, new[] { 10f, 22.5f }, new[] { 14f, 20f }); k.Settlement("w_town_2", "town", new[] { 50f, 60f }, new[] { 52.5f, 60f }, null); });
            var strangerManifest = NavigationCacheStore.ParseManifest(Serializer.Serialize(NavigationCacheStore.Manifest(stranger, "s", "w", 146201570u, 123456u, f => files[f], new Dictionary<string, object>(), "sha")));
            var r4 = check(strangerManifest, stranger, f => files[f], 146201570u, 123456u, 100);
            var r5 = check(parsedManifest, key, f => files[f], 146201570u, 123456u, 8);
            var r6 = check(parsedManifest, key, f => f == "Naval.bin" ? null : files[f], 146201570u, 123456u, 100);
            var wrongSchema = NavigationCacheStore.ParseManifest(Serializer.Serialize(manifest).Replace(NavigationCacheStore.Schema, "other.schema"));
            var r7 = check(wrongSchema, key, f => files[f], 146201570u, 123456u, 100);
            Check(r1.Any(x => x.Contains("settlement w_town_1")) && r2.Any(x => x.StartsWith("All: the file's hash")) && r3.Count(x => x.Contains("scene CRCs")) == 3 && r4.Any(x => x.Contains("w_village_1"))
                && r5.Count(x => x.Contains("face 9")) == 3 && r6.Any(x => x.StartsWith("Naval: no file")) && r7.Any(x => x.Contains("not " + NavigationCacheStore.Schema)) && check(null, key, f => files[f], 1u, 2u, 3).SequenceEqual(new[] { "no manifest" }),
                "store: refused for a moved port (named), a tampered file, other scene CRCs (each file), a settlement the key lacks, a face beyond the navmesh, a missing file, another schema, no manifest");
            evidence["storeRefusals"] = new Dictionary<string, object> { ["movedPort"] = r1, ["tampered"] = r2, ["otherCrcs"] = r3, ["unknownSettlement"] = r4, ["faceRange"] = r5, ["missingFile"] = r6, ["schema"] = r7 };
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

        // ---- route set and dry run on worlds A and B ----
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var navPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        var placementPolicy = SettlementPlacementPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.PolicyPath)), File.ReadAllBytes(Path.Combine(repo, SettlementPlacementPolicy.NavalPath)));
        var graphPolicy = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/graph-validation.json")).TrimStart('\uFEFF'));
        var pending = RuntimePlayabilityValidator.PendingRuntime(graphPolicy);
        var worlds = new SortedDictionary<string, object>(StringComparer.Ordinal); report["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            var input = NavigationInput.FromBundle(bundle, surface, navPolicy);
            var build = RuntimeNavigationBuilder.Build(input, navPolicy);
            var plan = NavigationPlan.Create(build, model);
            var ground = new NavigationGround(build);
            byte[] campaignJson = bundle.ReadVerified("compiled/campaign/campaign.json");
            var layout = SettlementPositionResolver.Resolve(SettlementPositionResolver.SitesFromBundle(input, campaignJson, bundle.ReadVerified("compiled/travel/ports.json")), plan, ground, placementPolicy);
            var world = PlayabilityWorld.FromBundle(input, campaignJson, layout, ground);
            var routes = RuntimePlayabilityValidator.RouteSet(world);
            var byCategory = RuntimePlayabilityValidator.Categories.ToDictionary(c => c, c => routes.Count(r => r.Category == c));
            int villages = world.Sites.Values.Count(s => s.Kind == "village"), fiefs = world.Sites.Values.Count(s => (s.Kind == "town" || s.Kind == "castle") && s.Kingdom != null && world.Kingdoms.Any(k => k.Id == s.Kingdom && k.Capital != s.Id));
            int ports = world.Sites.Values.Count(s => s.HasPort), portBasins = world.Sites.Values.Where(s => s.HasPort).GroupBy(s => s.Basin).Count();
            bool paired = routes.All(r => routes.Any(o => o.Category == r.Category && o.FromId == r.ToId && o.ToId == r.FromId && o.FromLand == r.ToLand && o.ToLand == r.FromLand));
            Check(byCategory["village-market"] == 2 * villages && byCategory["capital-fief"] == 2 * fiefs && byCategory["port-to-port"] == 2 * (ports - portBasins) && byCategory["faction-access"] == 2 * world.Kingdoms.Count
                && byCategory["player-start"] > 0 && world.Starts.Count > 0 && paired && routes.Select(r => r.Id + "|" + r.Category + "|" + r.Subject).Distinct().Count() == routes.Count,
                label + ": the semantic route set: " + String.Join(", ", byCategory.Select(kv => kv.Value + " " + kv.Key)) + " routes (" + villages + " villages, " + fiefs + " fiefs, " + ports + " ports on " + portBasins + " basins, "
                + world.Starts.Count + " start candidates, " + world.Kingdoms.Count + " kingdoms, " + world.Clans.Count(c => c.Value.Naval) + " of " + world.Clans.Count + " clans may hold ships); every route also runs back");
            var refused = routes.Where(r => r.Refused != null).ToList();
            Check(refused.Count == 0, label + ": no route is refused for the mover's capability" + (refused.Count > 0 ? " - " + String.Join("; ", refused.Take(4).Select(r => r.Id + ": " + r.Refused)) : ""));
            Check(routes.Where(r => r.Category == "village-market").All(r => r.Type == "Default") && routes.Where(r => r.Category == "port-to-port").All(r => r.Type == "Naval" && !r.FromLand && !r.ToLand)
                && routes.Where(r => r.Category == "capital-fief" || r.Category == "faction-access").All(r => r.Type == (world.Sites[r.FromId].Group == world.Sites[r.ToId].Group ? "Default" : "All"))
                && routes.Where(r => r.Category == "player-start").Select(r => r.Subject).Distinct().Count() == world.Starts.Select(x => x.Town).Distinct().Count()
                && world.Sites.Values.Where(x => x.HasPort).All(x => x.Kind == "town"),
                label + ": types follow capability: villagers on land, fleets by sea, lords on land within a land group and embarking across water; one start route set per start town; only towns have ports");

            // Dry run through the managed mesh engine.
            var mesh = new MeshNavigationEngine(build.Mesh, model);
            var navReport = NavigationValidator.Run(plan, mesh, "test_" + label, null);
            var rows = new List<Dictionary<string, object>>();
            foreach (var step in RuntimePlayabilityValidator.Steps(plan, mesh, routes, rows)) { }
            int failed = rows.Count(r => !Equals(r["agrees"], true));
            var settlementChecks = new Dictionary<string, object> { ["passed"] = true };
            byte[] graphJson = bundle.ReadVerified("compiled/travel/travel-graph.json"), portsJson = bundle.ReadVerified("compiled/travel/ports.json");
            var flags = RuntimePlayabilityValidator.ResolveFlags(graphJson, portsJson, pending, navReport, settlementChecks, failed == 0, failed);
            var notFlags = RuntimePlayabilityValidator.NotFlags(bundle.ReadVerified("compiled/validation.json"), File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/product-policy.json")));
            var play = RuntimePlayabilityValidator.Report(bundle.WorldContentId, "test_" + label, layout, navReport, settlementChecks, routes, rows, flags, mesh.Name, null, notFlags);
            var failedFlags = flags.Where(f => (string)f["status"] == "failed").ToList();
            Check(Equals(navReport["activatable"], true) && failed == 0, label + ": through the managed mesh engine every semantic route agrees (" + rows.Count + ")" + (failed > 0 ? " - " + String.Join("; ", rows.Where(r => !Equals(r["agrees"], true)).Take(4).Select(r => r["id"] + ": " + String.Join(", ", (List<string>)r["problems"]))) : ""));
            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(graphJson));
            int hostChecks = ((object[])graph["hostChecks"]).Length, transitions = ((object[])graph["transitions"]).Length;
            Check(failedFlags.Count == 0 && flags.Count == 1 + hostChecks + transitions + 1 + pending.Count && pending.Count == 2 && Equals(play["activatable"], true)
                && ((Dictionary<string, object>)play["notFlags"])["pendingSlots"] != null && ((Dictionary<string, object>)play["notFlags"])["pendingRuntimePolicies"] != null,
                label + ": every compiler pending-runtime flag resolves (" + flags.Count + ": the travel graph, " + hostChecks + " host checks, " + transitions + " transitions, ports, the graph policy's " + pending.Count + " pending-runtime entries); the content slots and runtime policies are listed as not flags" + (failedFlags.Count > 0 ? " - " + String.Join("; ", failedFlags.Take(5).Select(f => f["check"] + " " + f["subject"] + ": " + f["by"])) : ""));
            // Faults: one host check's rows disagree; settlement checks fail; routes fail.
            var faulted = Serializer.Deserialize<Dictionary<string, object>>(Serializer.Serialize(navReport));
            var engineRows = (System.Collections.ArrayList)((Dictionary<string, object>)faulted["engine"])["routes"];
            var portGate = engineRows.Cast<Dictionary<string, object>>().First(r => (string)r["check"] == "portGate");
            portGate["agrees"] = false;
            var f1 = RuntimePlayabilityValidator.ResolveFlags(graphJson, portsJson, pending, faulted, settlementChecks, true, 0);
            var f2 = RuntimePlayabilityValidator.ResolveFlags(graphJson, portsJson, pending, navReport, new Dictionary<string, object> { ["passed"] = false }, true, 0);
            var f3 = RuntimePlayabilityValidator.ResolveFlags(graphJson, portsJson, pending, navReport, settlementChecks, false, 3);
            var f4 = RuntimePlayabilityValidator.ResolveFlags(graphJson, portsJson, pending.Concat(new[] { "X99: something new pending" }).ToList(), navReport, settlementChecks, true, 0);
            var failedRow = new List<Dictionary<string, object>>(rows) { new Dictionary<string, object> { ["id"] = "x", ["category"] = "capital-fief", ["agrees"] = false, ["problems"] = new List<string> { "no path" } } };
            var failedReport = RuntimePlayabilityValidator.Report(bundle.WorldContentId, "test", layout, navReport, settlementChecks, routes, failedRow, f3, mesh.Name, null, notFlags);
            var faultedReport = RuntimePlayabilityValidator.Report(bundle.WorldContentId, "test", layout, navReport, settlementChecks, routes, rows, flags, mesh.Name, new[] { "Bridge faces disabled" }, notFlags);
            Func<List<Dictionary<string, object>>, string, bool> failedKind = (fl, kind) => fl.Any(f => (string)f["kind"] == kind && (string)f["status"] == "failed");
            Check(f1.Count(f => (string)f["status"] == "failed") == 2 && failedKind(f1, "hostCheck") && failedKind(f1, "ports") && failedKind(f2, "ports") && f2.Count(f => (string)f["status"] == "failed") == 2
                && f3.Count(f => (string)f["status"] == "failed") == 1 && ((string)f3.Last()["subject"]).StartsWith("U03") && !Equals(failedReport["activatable"], true) && ((System.Collections.IList)failedReport["failures"]).Count == 1
                && f4.Count(f => (string)f["status"] == "failed") == 1 && (string)f4.Last()["by"] == "no resolver for this pending-runtime entry" && !Equals(faultedReport["activatable"], true),
                label + ": a failed portGate host check fails its flag and the ports flag; failed settlement checks fail W05 and the ports flag; failed routes fail U03 and the report is not activatable; an unknown pending-runtime entry fails; a report made with engine faults is not activatable");
            if (label == "A") RequireTests(play, navReport, layout, failedReport, faultedReport);
            worlds[label] = new Dictionary<string, object> { ["worldContentId"] = bundle.WorldContentId, ["routes"] = play["routes"], ["flags"] = ((Dictionary<string, object>)play["flags"])["byKind"],
                ["kingdoms"] = world.Kingdoms.Count, ["clans"] = world.Clans.Count, ["navalClans"] = world.Clans.Count(c => c.Value.Naval), ["startCandidates"] = world.Starts.Count, ["steepPads"] = play["steepPads"],
                ["notFlags"] = play["notFlags"], ["engine"] = mesh.Name };
        }

        // A coast transition on a seam column (2026-10-19, seed 43's world): its seamTransition host check ("coast group G") resolves from the
        // coastTransition rows of the group's seam-column coast transitions; a group with none routed fails, as does a disagreeing row.
        {
            Func<bool, byte[]> seamReport = agrees => Encoding.UTF8.GetBytes(Serializer.Serialize(new Dictionary<string, object> { ["activatable"] = true, ["failures"] = new object[0],
                ["engine"] = new Dictionary<string, object> { ["faces"] = new object[0], ["islands"] = new Dictionary<string, object> { ["problems"] = new object[0] },
                    ["routes"] = new object[] { new Dictionary<string, object> { ["check"] = "coastTransition", ["subject"] = "group 1 cell 2,3", ["agrees"] = agrees } } } }));
            byte[] seamGraph = Encoding.UTF8.GetBytes(Serializer.Serialize(new Dictionary<string, object> { ["engineValidation"] = "pending", ["grid"] = new Dictionary<string, object> { ["cols"] = 4 },
                ["hostChecks"] = new object[] { new Dictionary<string, object> { ["check"] = "seamTransition", ["subject"] = "coast group 1" }, new Dictionary<string, object> { ["check"] = "seamTransition", ["subject"] = "coast group 2" } },
                ["transitions"] = new object[] {
                    new Dictionary<string, object> { ["kind"] = "coast", ["group"] = 1, ["landCell"] = new[] { 2, 3 }, ["seaCell"] = new[] { 2, 0 }, ["status"] = "pendingEngineValidation" },
                    new Dictionary<string, object> { ["kind"] = "coast", ["group"] = 2, ["landCell"] = new[] { 1, 1 }, ["seaCell"] = new[] { 1, 2 }, ["status"] = "pendingEngineValidation" } } }));
            byte[] seamPorts = Encoding.UTF8.GetBytes("{\"engineValidation\":\"pending\"}");
            Func<bool, Dictionary<string, Dictionary<string, object>>> seamFlags = agrees => RuntimePlayabilityValidator.ResolveFlags(seamGraph, seamPorts, new string[0],
                    (IDictionary<string, object>)Serializer.DeserializeObject(Encoding.UTF8.GetString(seamReport(agrees))), new Dictionary<string, object> { ["passed"] = true }, true, 0)
                .Where(f => (string)f["kind"] == "hostCheck").ToDictionary(f => (string)f["subject"]);
            var agreeing = seamFlags(true); var disagreeing = seamFlags(false);
            Check((string)agreeing["coast group 1"]["status"] == "resolved" && (string)agreeing["coast group 2"]["status"] == "failed" && (string)disagreeing["coast group 1"]["status"] == "failed",
                "a coast seamTransition host check resolves from its group's seam-column coastTransition rows (" + agreeing["coast group 1"]["by"] + "); a group with none routed fails ("
                + agreeing["coast group 2"]["by"] + "), as does a disagreeing row");
        }

        // Capability refusals (per clan: the moving clan's ships decide).
        var small = new PlayabilityWorld { Distance = (x, y) => Math.Sqrt((x[0] - y[0]) * (x[0] - y[0]) + (x[1] - y[1]) * (x[1] - y[1])) };
        Action<string, string, string, string, double, int> site = (id, kind, kingdom, owner, x, group) => small.Sites[id] = new PlayabilityWorld.Site { Id = id, Kind = kind, Kingdom = kingdom, Owner = owner, Gate = new double[] { x, 0 }, Group = group };
        site("c1", "town", "k1", "a", 0, 0); site("f1", "castle", "k1", "a", 50, 1); site("c2", "town", "k2", "b", 60, 1); site("f2", "castle", "k2", "d", 300, 3); site("c3", "town", "k3", "c", 200, 2);
        small.Sites["v1"] = new PlayabilityWorld.Site { Id = "v1", Kind = "village", Bound = "c1", Gate = new double[] { 5, 0 }, Group = 2 };
        small.Clans["a"] = new PlayabilityWorld.Capability { Naval = false, Reason = "a is landlocked" }; small.Clans["b"] = new PlayabilityWorld.Capability { Naval = true, Reason = "b holds ships" };
        small.Clans["c"] = new PlayabilityWorld.Capability { Naval = true, Reason = "c holds ships" }; small.Clans["d"] = new PlayabilityWorld.Capability { Naval = false, Reason = "d is landlocked" };
        foreach (var k in new[] { new[] { "k1", "c1", "a" }, new[] { "k2", "c2", "b" }, new[] { "k3", "c3", "c" } }) small.Kingdoms.Add(new PlayabilityWorld.Faction { Id = k[0], Capital = k[1], RulingClan = k[2] });
        var smallRoutes = RuntimePlayabilityValidator.RouteSet(small);
        Func<string, string, string, SemanticRoute> route = (category, from, to) => smallRoutes.SingleOrDefault(r => r.Category == category && r.FromId == from && r.ToId == to);
        Check(route("capital-fief", "c1", "f1").Refused != null && route("capital-fief", "f1", "c1").Refused != null && route("village-market", "v1", "c1").Refused != null
            && route("capital-fief", "c2", "f2").Refused == null && route("capital-fief", "c2", "f2").Type == "All" && route("capital-fief", "f2", "c2").Refused != null
            && smallRoutes.Where(r => r.Category == "faction-access" && r.Subject == "k1").All(r => r.Refused != null) && smallRoutes.Count(r => r.Category == "faction-access" && r.Subject == "k1") == 2
            && route("faction-access", "c2", "f1").Type == "Default" && route("faction-access", "c3", "c2").Type == "All" && route("faction-access", "c3", "c2").Refused == null && route("faction-access", "c2", "c3").Refused == null
            && smallRoutes.Single(r => r.Category == "player-start").Refused != null,
            "refusals: a fief across water for a landlocked kingdom (both ways), a village off its market's land, a fief whose landlocked owner cannot come back to a capital across water, a shipless kingdom with no foreign fief it can reach both ways, no player start; a clan with ships reaches across water (All) and a land neighbour on land (Default)");
    }

    // MMR-45's gate on the persisted report.
    private static void RequireTests(Dictionary<string, object> play, Dictionary<string, object> navReport, SettlementLayout layout, Dictionary<string, object> failedReport, Dictionary<string, object> faultedReport)
    {
        string store = Path.Combine(Path.GetTempPath(), "mnbl-playability-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string scene = (string)play["sceneName"];
            string navRecord = Path.Combine(store, "nav.json"), layoutReport = Path.Combine(store, "layout.json");
            Directory.CreateDirectory(store);
            File.WriteAllText(navRecord, Serializer.Serialize(new Dictionary<string, object> { ["reportSha256"] = navReport["reportSha256"] }));
            File.WriteAllText(layoutReport, Serializer.Serialize(new Dictionary<string, object> { ["layoutSha256"] = layout.Sha256 }));
            Func<Action, string> refusal = action => { try { action(); return null; } catch (InvalidOperationException e) { return e.Message; } };
            string missing = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            string path = RuntimePlayabilityValidator.Persist(store, play);
            string clean = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            File.WriteAllText(navRecord, Serializer.Serialize(new Dictionary<string, object> { ["reportSha256"] = new string('0', 64) }));
            string staleNavigation = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            File.WriteAllText(navRecord, Serializer.Serialize(new Dictionary<string, object> { ["reportSha256"] = navReport["reportSha256"] }));
            File.WriteAllText(layoutReport, Serializer.Serialize(new Dictionary<string, object> { ["layoutSha256"] = new string('1', 64) }));
            string staleLayout = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            File.WriteAllText(layoutReport, Serializer.Serialize(new Dictionary<string, object> { ["layoutSha256"] = layout.Sha256 }));
            var edited = File.ReadAllText(path).Replace("\\\"activatable\\\":true", "\\\"activatable\\\":false");
            File.WriteAllText(path, edited);
            string tampered = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            RuntimePlayabilityValidator.Persist(store, failedReport.ToDictionary(kv => kv.Key, kv => kv.Key == "sceneName" ? scene : kv.Value).Let(d => { d["reportSha256"] = ResealedSha(d); return d; }));
            string notActivatable = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            RuntimePlayabilityValidator.Persist(store, faultedReport.ToDictionary(kv => kv.Key, kv => kv.Key == "sceneName" ? scene : kv.Value).Let(d => { d["reportSha256"] = ResealedSha(d); return d; }));
            string faulted = refusal(() => RuntimePlayabilityValidator.Require(store, scene, navRecord, layoutReport));
            RuntimePlayabilityValidator.Delete(store, scene);
            bool deleted = !File.Exists(path);
            Check(missing != null && clean == null && staleNavigation != null && staleNavigation.Contains("navigation report") && staleLayout != null && staleLayout.Contains("settlement layout")
                && tampered != null && tampered.Contains("hash") && notActivatable != null && notActivatable.Contains("not activatable") && faulted != null && faulted.Contains("engine faults") && deleted,
                "gate (Require): an intact activatable report bound to the scene's navigation and layout reports passes; no report, another navigation report, another layout, an edited record, a report that is not activatable and one made with engine faults are refused; Delete removes it"
                + (clean != null ? " - clean refused: " + clean : ""));
        }
        finally { try { Directory.Delete(store, true); } catch (IOException) { } }
    }

    private static string ResealedSha(Dictionary<string, object> report)
    {
        var stable = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in report) if (kv.Key != "reportSha256") stable[kv.Key] = kv.Value;
        return Json.Sha(Serializer.Serialize(stable));
    }

    private static T Let<T>(this T value, Func<T, T> f) { return f(value); }

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
}
