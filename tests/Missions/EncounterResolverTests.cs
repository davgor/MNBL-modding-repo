using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Missions;
using MNBL.ProceduralWorld.Surface;

// MMR-42 offline tests: the mission scene catalogue against the installed game, the mission contracts, the battle-cell choice on every
// geography class, full battle maps of compiled worlds A and B (the managed navmesh stand-in and the bundles' biome masks), the periodic seam
// (canonical patch sampling, the short-way encounter direction) and the geographic mismatch check. Usage:
//   EncounterResolverTests.exe <repo> <bundleA> <bundleB> <gameRoot> <evidence.json>
internal static class EncounterResolverTests
{
    private static int checks, failures;
    private static readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label, object detail = null)
    {
        checks++; if (!condition) failures++;
        Console.WriteLine((condition ? "ok    " : "FAIL  ") + label);
        Rows.Add(new Dictionary<string, object> { ["check"] = label, ["passed"] = condition, ["detail"] = detail });
    }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static int Main(string[] args)
    {
        if (args.Length != 5) { Console.Error.WriteLine("usage: EncounterResolverTests <repo> <bundleA> <bundleB> <gameRoot> <evidence.json>"); return 2; }
        string repo = args[0], gameRoot = args[3];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { Path.Combine(gameRoot, "bin", "Win64_Shipping_Client"), Path.Combine(gameRoot, "Modules", "NavalDLC", "bin", "Win64_Shipping_Client"), Path.Combine(gameRoot, "Modules", "SandBox", "bin", "Win64_Shipping_Client") })
                if (File.Exists(Path.Combine(dir, name))) return System.Reflection.Assembly.LoadFrom(Path.Combine(dir, name));
            return null;
        };
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.evidence.encounter-resolver", ["version"] = 1, ["ticket"] = "MMR-42" };
        var catalogueBytes = File.ReadAllBytes(Path.Combine(repo, MissionScenes.PolicyPath));
        var m = MissionScenes.FromBytes(catalogueBytes);
        var table = BattleTable.FromCatalogue(m);
        var resolver = new GeneratedEncounterResolver(m, table);
        var doc = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(catalogueBytes).TrimStart('﻿'));
        evidence["catalogue"] = new Dictionary<string, object> { ["sha256"] = m.Sha256, ["sceneTableSha256"] = m.TableSha256, ["battleScenes"] = m.Battle.Count, ["scenes"] = m.Scenes.Count, ["prototypes"] = m.Prototypes.Count };

        // ---- the catalogue is the installed game's ----
        var sourceRows = ((object[])doc["sources"]).Cast<IDictionary<string, object>>().ToList();
        var stale = sourceRows.Where(r => ((string)r["path"]).StartsWith("Modules/", StringComparison.Ordinal))
            .Where(r => Sha(File.ReadAllBytes(Path.Combine(gameRoot, (string)r["path"]))) != (string)r["sha256"]).Select(r => (string)r["path"]).ToList();
        Check(stale.Count == 0 && table.Sha256 == m.TableSha256 && m.TableSha256 == "3aa0154cbc86905be7ef844b0105032710f7f19e491f09df097b32370b4ccb21" && table.InventorySha256 == m.InventorySha256,
              "catalogue: the installed battle and conversation tables are the catalogue's sources; the battle table hash is the one MMR-60 recorded in game (3aa0154c...), and the inventory hash " +
              "(forest density, ground) is the catalogue's (" + m.InventorySha256.Substring(0, 12) + ")", stale);
        var missing = m.Scenes.Values.Where(s => !File.Exists(Path.Combine(gameRoot, "Modules", s.Module, "SceneObj", s.Id, "scene.xscene"))).Select(s => s.Id).ToList();
        var sample = m.Scenes.Values.OrderBy(s => Sha(Encoding.UTF8.GetBytes(s.Id)), StringComparer.Ordinal).Take(40).ToList();
        var changed = sample.Where(s => Sha(File.ReadAllBytes(Path.Combine(gameRoot, "Modules", s.Module, "SceneObj", s.Id, "scene.xscene"))) != s.Sha256).Select(s => s.Id).ToList();
        var referenced = m.Battle.Select(b => b.Id).Concat(m.Conversations.Select(c => c.Id)).Concat(m.Prototypes.Values.SelectMany(p => p.Locations.Values.SelectMany(v => v))).Where(x => x != null).Distinct().ToList();
        Check(missing.Count == 0 && changed.Count == 0 && referenced.All(m.Scenes.ContainsKey) && m.Scenes.Values.All(s => m.AllowedModules.Contains(s.Module)),
              "catalogue: all " + m.Scenes.Count + " battle, conversation and location scenes are installed (" + String.Join(", ", m.Scenes.Values.GroupBy(s => s.Module).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())) +
              "), in allowed modules; 40 sampled scene files hash as inventoried", new { missing, changed });
        Check(m.Scenes.Values.Where(x => x.Levels.ContainsKey("naval_raid")).All(x => x.VisibleIn("landing_", "naval_raid") && x.VisibleIn("jumping_", "naval_raid")),
              "catalogue: every installed seaborne-raid scene's landing and jumping points are visible in its naval_raid level (per-entity level inventory)");
        Check(m.Battle.All(b => b.SpawnPaths >= 1),
              "fieldBattle/navalBattle: every installed battle scene has spawn paths for the patch placement (" + m.Battle.Count(b => !b.Naval) + " land, " + m.Battle.Count(b => b.Naval) + " naval; min " + m.Battle.Min(b => b.SpawnPaths) + ")");

        // ---- contracts over every prototype a factory can pick ----
        var byKind = new SortedDictionary<string, object>(StringComparer.Ordinal);
        bool allConform = true;
        foreach (var g in m.Prototypes.Values.GroupBy(p => p.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string kind = g.Key.StartsWith("town", StringComparison.Ordinal) ? "town" : g.Key == "castle" ? "castle" : g.Key == "hideout" ? "hideout" : "village";
            bool port = g.Key == "town_port" || g.Key == "village_coastal";
            var failing = g.Where(p => m.PrototypeProblems(p.Id, kind, port).Count > 0).Select(p => p.Id + ": " + m.PrototypeProblems(p.Id, kind, port)[0]).ToList();
            var asPort = g.Key == "village_inland" ? g.Count(p => m.PrototypeProblems(p.Id, "village", true).Count == 0) : -1;
            byKind[g.Key] = new Dictionary<string, object> { ["prototypes"] = g.Count(), ["conforming"] = g.Count() - failing.Count, ["failing"] = failing.Take(5).ToList(), ["raidCapableAsHarbour"] = asPort };
            allConform &= failing.Count == 0;
        }
        evidence["prototypeContracts"] = byKind;
        Check(allConform, "contracts: every installed prototype meets its kind's contracts: town and castle centers have level_1..3 with siege at each wall level and a lord's hall with siege, " +
              "port towns a port scene, coastal villages a seaborne-raid village_center (naval_raid, landing and jumping points), hideouts level_1/level_2, spawnpoint_player and CommonAreaMarker areas", byKind);
        var cultures = m.Prototypes.Values.Where(p => p.Kind == "village_coastal").Select(p => p.Culture).Distinct().ToList();
        var mainCultures = m.Prototypes.Values.Where(p => p.Kind == "castle").Select(p => p.Culture).Distinct().ToList();
        Check(mainCultures.All(cultures.Contains), "contracts: every culture with fiefs has coastal village prototypes, so a harbour village always draws from raid-capable prototypes (" + String.Join(", ", mainCultures) + ")");
        // Refusals: each contract fails a scene that lacks what the installed code reads.
        var battleScene = m.Battle.First(b => !b.Naval && !m.Scenes[b.Id].Levels.ContainsKey("level_2") && !m.Scenes[b.Id].Levels.ContainsKey("siege")).Id;
        var townProto = m.Prototypes.Values.First(p => p.Kind == "town_port");
        var inlandVillage = m.Prototypes.Values.First(p => p.Kind == "village_inland" && m.PrototypeProblems(p.Id, "village", true).Count > 0);
        var hideoutProto = m.Prototypes.Values.First(p => p.Kind == "hideout");
        Func<MissionScenes.Prototype, Action<Dictionary<string, string[]>>, Dictionary<string, string[]>> edit = (p, change) => { var d = p.Locations.ToDictionary(x => x.Key, x => (string[])x.Value.Clone()); change(d); return d; };
        var r1 = m.LocationProblems("town", true, edit(townProto, d => d["center"][2] = battleScene));
        var r2 = m.LocationProblems("town", true, edit(townProto, d => d.Remove("lordshall")));
        var r3 = m.LocationProblems("town", true, edit(townProto, d => d["port"] = new string[4]));
        var r4 = m.LocationProblems("village", true, inlandVillage.Locations);
        var r5 = m.LocationProblems("hideout", false, edit(hideoutProto, d => d["hideout_center"] = new[] { inlandVillage.Locations["village_center"][0], null, null, null }));
        var r6 = m.LocationProblems("castle", false, edit(m.Prototypes.Values.First(p => p.Kind == "castle"), d => d["prison"] = new[] { "mnbl_not_installed", null, null, null }));
        var r7 = m.LocationProblems("castle", false, edit(m.Prototypes.Values.First(p => p.Kind == "castle"), d => d["lordshall"] = new[] { null, d["lordshall"][1], d["lordshall"][2], null }));
        Check(r1.Any(x => x.StartsWith("settlementCenter: ") && x.Contains("level_2")) && r1.Any(x => x.StartsWith("siege: ")) && r2.Any(x => x.StartsWith("siegeLordsHall: no scene")) &&
              r3.Any(x => x.StartsWith("port: no scene")) && r4.Any(x => x.StartsWith("navalRaid: ") && x.Contains("naval_raid")) && r5.Any(x => x.StartsWith("hideout: ")) &&
              r6.Any(x => x.Contains("mnbl_not_installed is not an installed scene")) && r7.Any(x => x.StartsWith("siegeLordsHall: no scene") && x.Contains("wall level 3")),
              "contracts refuse: a battle scene as a wall level 2 center (no level_2, no siege), no lord's hall, a port town without a port scene, an inland village scene for a harbour village (no naval_raid), " +
              "a village scene as a hideout, a scene that is not installed, a lord's hall with no scene at wall level 3 (GetSceneName falls back to an empty level 0)", new { r1, r2, r3, r4, r5, r6, r7 });

        // ---- battle-cell choice on every geography class ----
        Func<string, int> T = name => m.TerrainIds[name];
        Func<string[], string[], string, CellSamples> cell = (land, sea, biome) =>
        {
            var s = new CellSamples { Biome = biome };
            foreach (var l in land) s.Land.Add(T(l)); foreach (var x in sea) s.Sea.Add(T(x));
            if (land.Length > 0) s.CentreLand = T(land[0]);
            return s;
        };
        var probes = new List<KeyValuePair<string, CellSamples>> {
            new KeyValuePair<string, CellSamples>("grassland plain", cell(new[] { "Plain" }, new string[0], "grassland")),
            new KeyValuePair<string, CellSamples>("forest", cell(new[] { "Plain" }, new string[0], "forest")),
            new KeyValuePair<string, CellSamples>("boreal forest", cell(new[] { "Plain" }, new string[0], "boreal_forest")),
            new KeyValuePair<string, CellSamples>("desert", cell(new[] { "Plain" }, new string[0], "desert")),
            new KeyValuePair<string, CellSamples>("marsh", cell(new[] { "Plain" }, new string[0], "marsh")),
            new KeyValuePair<string, CellSamples>("snow", cell(new[] { "Plain" }, new string[0], "snow")),
            new KeyValuePair<string, CellSamples>("tundra", cell(new[] { "Plain" }, new string[0], "tundra")),
            new KeyValuePair<string, CellSamples>("exposed rock", cell(new[] { "Plain" }, new string[0], "exposed_rock")),
            new KeyValuePair<string, CellSamples>("face Steppe (no biome)", cell(new[] { "Steppe" }, new string[0], null)),
            new KeyValuePair<string, CellSamples>("coast (Plain + CoastalSea)", cell(new[] { "Plain" }, new[] { "CoastalSea" }, "grassland")),
            new KeyValuePair<string, CellSamples>("desert coast", cell(new[] { "Plain" }, new[] { "CoastalSea" }, "desert")),
            new KeyValuePair<string, CellSamples>("open sea only", cell(new string[0], new[] { "OpenSea" }, "ocean")),
            new KeyValuePair<string, CellSamples>("shelf (CoastalSea + OpenSea)", cell(new string[0], new[] { "CoastalSea", "OpenSea" }, "ocean")),
            new KeyValuePair<string, CellSamples>("river bank", cell(new[] { "Plain" }, new[] { "River" }, "grassland")),
            new KeyValuePair<string, CellSamples>("bridge crossing (Bridge + UnderBridge)", cell(new[] { "Bridge" }, new[] { "UnderBridge" }, "grassland")),
            new KeyValuePair<string, CellSamples>("ford (Fording + River)", cell(new[] { "Fording" }, new[] { "River" }, "forest")),
            new KeyValuePair<string, CellSamples>("lake shore (Plain + Lake)", cell(new[] { "Plain" }, new[] { "Lake" }, "grassland")),
            new KeyValuePair<string, CellSamples>("river mouth (River + CoastalSea)", cell(new[] { "Plain" }, new[] { "River", "CoastalSea" }, "marsh")),
        };
        var mountain = new CellSamples(); mountain.Blocked.Add(T("Mountain")); mountain.CentreBlocked = T("Mountain");
        probes.Add(new KeyValuePair<string, CellSamples>("mountain (blocked)", mountain));
        var probeRows = new List<object>(); bool probesOk = true;
        foreach (var p in probes)
        {
            var c = resolver.Choose(p.Value, "mnbl_probe", 3, 5);
            bool land = p.Value.Land.Count == 0 || (c.LandScene != null && table.ExpectedScene(c.Index, false, 0).Deterministic);
            bool sea = p.Value.Sea.Distinct().All(t => c.Naval[t] != null && table.ExpectedScene(c.Index, true, t).Scene == c.Naval[t]);
            var landRow = c.LandScene == null ? null : m.Battle.First(b => !b.Naval && b.Id == c.LandScene);
            var cls = m.Classes[c.Class];
            bool fits = landRow == null || landRow.Terrain == cls.Terrain || p.Value.Sea.Count > 0;
            probesOk &= land && sea && fits;
            probeRows.Add(new Dictionary<string, object> { ["cell"] = p.Key, ["class"] = c.Class, ["index"] = c.Index, ["tier"] = c.Tier, ["land"] = c.LandScene, ["landTerrain"] = landRow == null ? null : landRow.Terrain,
                ["forestDensity"] = landRow == null ? null : landRow.ForestDensity, ["terrainTypes"] = landRow == null ? null : landRow.TerrainTypes,
                ["naval"] = c.Naval.ToDictionary(n => resolver.Name(n.Key), n => (object)n.Value), ["deterministic"] = land && sea });
        }
        evidence["probes"] = probeRows;
        Check(probesOk, "cells: grassland, forest, desert, marsh, snow, tundra, rock, coasts, open sea, shelf, river, bridge, ford, lake shore, river mouth and blocked mountain each get an index the engine resolves to " +
              "exactly one land scene (where land was sampled) of the class's terrain, and exactly one naval scene per sea terrain under the party", probeRows);
        var forestRow = (Dictionary<string, object>)probeRows[1]; var rockRow = (Dictionary<string, object>)probeRows[7]; var desertRow = (Dictionary<string, object>)probeRows[3];
        var marshRow = (Dictionary<string, object>)probeRows[4]; var bridgeRow = (Dictionary<string, object>)probeRows[14]; var riverRow = (Dictionary<string, object>)probeRows[13];
        var lakeRow = (Dictionary<string, object>)probeRows[16];
        Func<object, string, string> navalOf = (row, t) => (string)((Dictionary<string, object>)((Dictionary<string, object>)row)["naval"])[t];
        Check((string)forestRow["forestDensity"] == "High", "woodland: a forest biome takes a Plain scene of high forest density (" + forestRow["land"] + ", " + forestRow["forestDensity"] + ")");
        var desertCoast = (Dictionary<string, object>)probeRows[10];
        Check((string)desertCoast["landTerrain"] == "Desert" || (string)desertCoast["landTerrain"] == "Steppe" || (string)desertCoast["landTerrain"] == "Plain",
              "fallback: a desert coast with no Desert + coast index installed falls back to Steppe, then Plain, never to another class's terrain (" + desertCoast["land"] + ", " + desertCoast["landTerrain"] + ", tier " + desertCoast["tier"] + ")");
        Check(((string[])rockRow["terrainTypes"]).Any(t => t == "Mountain" || t == "Canyon") && (string)rockRow["landTerrain"] == "Plain",
              "rock: exposed rock takes a scene with Mountain or Canyon ground (the installed ones are Plain: " + rockRow["land"] + ")");
        Check((string)desertRow["landTerrain"] == "Desert" && (string)marshRow["landTerrain"] == "Swamp", "desert and marsh take Desert and Swamp scenes (" + desertRow["land"] + ", " + marshRow["land"] + ")");
        Check(m.Battle.Any(b => b.Naval && b.Id == navalOf(bridgeRow, "UnderBridge") && b.Terrain == "UnderBridge") && m.Battle.Any(b => b.Naval && b.Id == navalOf(riverRow, "River") && b.Terrain == "River"),
              "crossings: a ship under a bridge gets an UnderBridge scene and a ship on a river a River scene (" + navalOf(bridgeRow, "UnderBridge") + ", " + navalOf(riverRow, "River") + "); MMR-60's map never selected either");
        Check(m.Battle.Any(b => b.Naval && b.Id == navalOf(lakeRow, "Lake") && b.Terrain == "CoastalSea") && (string)lakeRow["tier"] != null,
              "lake: no naval scene is a Lake's own, so the index names one naval scene only, a CoastalSea one (" + navalOf(lakeRow, "Lake") + ", tier " + lakeRow["tier"] + ")");
        // Relief: a ridge prefers mountain ground, a valley water ground, within the class.
        var ridge = cell(new[] { "Plain" }, new string[0], "grassland"); ridge.Landform = "Mountain / ridge";
        var valley = cell(new[] { "Plain" }, new string[0], "grassland"); valley.Landform = "Valley";
        var cr = resolver.Choose(ridge, "mnbl_probe", 3, 5); var cv = resolver.Choose(valley, "mnbl_probe", 3, 5);
        var ridgeRow = m.Battle.First(b => !b.Naval && b.Id == cr.LandScene); var valleyRow = m.Battle.First(b => !b.Naval && b.Id == cv.LandScene);
        Check(cr.Relief && ridgeRow.TerrainTypes.Any(t => t == "Mountain" || t == "Canyon") && cv.Relief && valleyRow.TerrainTypes.Any(t => t == "River" || t == "Water"),
              "relief: a mountain ridge takes a scene with mountain ground (" + cr.LandScene + ") and a valley one with river or water ground (" + cv.LandScene + "), within the open class");
        // A settlement's gate or port: an index every naval-valid terrain resolves deterministically.
        var navalIds = m.NavalTerrains.Select(T).ToList();
        var gate = cell(new[] { "Plain" }, new string[0], "grassland"); gate.Robust = true;
        var portCell = cell(new[] { "Plain" }, new[] { "CoastalSea" }, "grassland"); portCell.Robust = true;
        var cg = resolver.Choose(gate, "mnbl_probe", 3, 5); var cp = resolver.Choose(portCell, "mnbl_probe", 3, 5);
        Func<int, bool> robustIndex = i => navalIds.All(t => table.ExpectedScene(i, true, t).Deterministic) && table.UniqueLand(i) != null;
        Check(cg.Robust && cp.Robust && robustIndex(cg.Index) && robustIndex(cp.Index) && resolver.RobustIndices.Count() >= 8,
              "settlements: a gate cell and a port cell take indices every naval-valid terrain (" + String.Join(", ", m.NavalTerrains) + ") resolves, so a naval map event at a settlement read on land is still " +
              "deterministic (" + cg.Index + ", " + cp.Index + "; " + resolver.RobustIndices.Count() + " such indices installed)");
        // Every class with every sea combination resolves.
        var seaSets = new[] { new string[0], new[] { "CoastalSea" }, new[] { "OpenSea" }, new[] { "River" }, new[] { "UnderBridge" }, new[] { "Lake" }, new[] { "CoastalSea", "OpenSea" }, new[] { "River", "UnderBridge" }, new[] { "Lake", "CoastalSea" }, new[] { "River", "CoastalSea" } };
        int combos = 0, resolved = 0; var unresolved = new List<string>();
        foreach (var biome in m.BiomeClass.Keys)
            foreach (var sea in seaSets)
                foreach (bool hasLand in new[] { true, false })
                {
                    if (!hasLand && sea.Length == 0) continue;
                    combos++;
                    try
                    {
                        var c = resolver.Choose(cell(hasLand ? new[] { "Plain" } : new string[0], sea, biome), "mnbl_combo", combos, 1);
                        bool ok = (!hasLand || c.LandScene != null) && sea.All(t => c.Naval[T(t)] != null);
                        if (ok) resolved++; else unresolved.Add(biome + "/" + String.Join("+", sea) + (hasLand ? "+land" : ""));
                    }
                    catch (InvalidOperationException e) { unresolved.Add(biome + "/" + String.Join("+", sea) + ": " + e.Message); }
                }
        Check(resolved == combos, "cells: all " + combos + " combinations of " + m.BiomeClass.Count + " biomes, 10 sea terrain sets and land present or not resolve deterministically", unresolved);
        var coastalIndices = table.Indices.Where(i => table.UniqueLand(i) != null && new[] { "CoastalSea", "OpenSea" }.Any(t => { bool exact; return table.NavalScene(i, T(t), out exact) != null && exact; })).ToList();
        Check(coastalIndices.Count > 8, "coasts: " + coastalIndices.Count + " installed indices resolve both a land battle and a CoastalSea or OpenSea naval battle deterministically through the engine's terrain tie-break " +
              "(MMR-60 used only the 8 indices naming exactly one naval scene)");

        // ---- compiled worlds A and B: full battle maps ----
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var navPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var calibration = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/calibration/naval-transitions/naval-transitions.json")).TrimStart('﻿'));
        var model = NavigationModel.FromCalibration(calibration);
        var materials = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/policies/terrain-materials.json")).TrimStart('﻿'));
        var biomeNames = ((object[])materials["biomes"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["id"], CultureInfo.InvariantCulture), b => (string)b["core"]);
        var landformNames = ((object[])materials["landforms"]).Cast<IDictionary<string, object>>().ToDictionary(b => Convert.ToInt32(b["index"], CultureInfo.InvariantCulture), b => (string)b["name"]);
        var worlds = new SortedDictionary<string, object>(StringComparer.Ordinal); evidence["worlds"] = worlds;
        var generatedTerrains = new HashSet<int>();
        foreach (var label in new[] { "A", "B" })
        {
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            var surface = WorldSurface.FromBundle(bundle);
            var navInput = NavigationInput.FromBundle(bundle, surface, navPolicy);
            var build = RuntimeNavigationBuilder.Build(navInput, navPolicy);
            var engine = new MeshNavigationEngine(build.Mesh, model);
            double extent = surface.Extent;
            int width = (int)Math.Ceiling(extent / GeneratedBattleTerrainCellSize), height = width;
            double cellX = extent / width, cellY = extent / height;
            double origin = surface.Strip / cellX, period = surface.LogicalWidth / cellX;
            bool whole = Math.Abs(origin - Math.Round(origin)) < 1e-6 && Math.Abs(period - Math.Round(period)) < 1e-6;
            int originCell = (int)Math.Round(origin), periodCells = (int)Math.Round(period);
            var landFaces = new HashSet<int>(); var seaFaces = new HashSet<int>();
            // The cells around every settlement (travel graph points are logical: physical = logical + (strip, pole margin)), a ring of one cell.
            var settlementCells = new HashSet<long>();
            foreach (var st in navInput.Settlements)
            {
                int px = (int)Math.Floor((st.X + surface.Strip) / cellX), py = (int)Math.Floor((st.Y + surface.PoleMargin) / cellY);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    if (py + dy >= 0 && py + dy < height && px + dx >= 0 && px + dx < width) settlementCells.Add((long)(py + dy) * 1000003 + GeneratedEncounterResolver.CanonicalColumn(px + dx, originCell, periodCells));
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var map = resolver.Build(width, height, originCell, periodCells, true, bundle.WorldContentId, (cx, y) =>
            {
                var s = new CellSamples();
                for (int sy = 0; sy < 3; ++sy)
                    for (int sx = 0; sx < 3; ++sx)
                    {
                        double px = (cx + .05 + .45 * sx) * cellX, py = (y + .05 + .45 * sy) * cellY; bool centre = sx == 1 && sy == 1;
                        foreach (bool region in new[] { true, false })
                        {
                            int face, group, island;
                            if (!engine.Face(px, py, region, out face, out group, out island)) continue;
                            bool onLand = model.Valid(group, "Default"), atSea = model.Valid(group, "Naval");
                            if (onLand) { s.Land.Add(group); if (centre) s.CentreLand = group; landFaces.Add(group); }
                            if (atSea) { s.Sea.Add(group); seaFaces.Add(group); }
                            if (!onLand && !atSea) { s.Blocked.Add(group); if (centre) s.CentreBlocked = group; }
                        }
                    }
                string biome; s.Biome = biomeNames.TryGetValue(surface.BiomeAt((cx + .5) * cellX, (y + .5) * cellY), out biome) ? biome : null;
                string landform; s.Landform = landformNames.TryGetValue(surface.LandformAt((cx + .5) * cellX, (y + .5) * cellY), out landform) ? landform : null;
                s.Robust = settlementCells.Contains((long)y * 1000003 + (cx - originCell));
                return s;
            });
            double seconds = watch.Elapsed.TotalSeconds;
            // Dense check: 6 x 6 points in every canonical cell (beyond the builder's 3 x 3), each land-valid face and each naval-valid face's
            // terrain against the engine's rule for the cell's index.
            long densePoints = 0, denseMisses = 0; var missExamples = new List<string>();
            for (int y = 0; y < height; y++)
                for (int lx = 0; lx < periodCells; lx++)
                {
                    var choice = map.Choices[(long)y * 1000003 + lx]; int cxp = originCell + lx;
                    for (int sy = 0; sy < 6; sy++) for (int sx = 0; sx < 6; sx++)
                    {
                        double px = (cxp + (sx + .5) / 6) * cellX, py = (y + (sy + .5) / 6) * cellY;
                        foreach (bool region in new[] { true, false })
                        {
                            int face, group, island;
                            if (!engine.Face(px, py, region, out face, out group, out island)) continue;
                            bool onLand = model.Valid(group, "Default"), atSea = model.Valid(group, "Naval");
                            if (onLand) { densePoints++; if (!table.ExpectedScene(choice.Index, false, group).Deterministic) { denseMisses++; if (missExamples.Count < 5) missExamples.Add("land " + lx + "," + y); } }
                            if (atSea) { densePoints++; if (!table.ExpectedScene(choice.Index, true, group).Deterministic) { denseMisses++; if (missExamples.Count < 5) missExamples.Add(resolver.Name(group) + " " + lx + "," + y); } }
                        }
                    }
                }
            generatedTerrains.UnionWith(landFaces); generatedTerrains.UnionWith(seaFaces);
            var rec = resolver.Record(map);
            // Strip cells equal their canonical cells; a block starts at the seam.
            int stripMismatch = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int lx = GeneratedEncounterResolver.CanonicalColumn(x, originCell, periodCells), canonical = originCell + lx;
                    if (canonical >= width) continue;
                    if (map.Bytes[2 * (y * width + x)] != map.Bytes[2 * (y * width + canonical)] || map.Bytes[2 * (y * width + x) + 1] != map.Bytes[2 * (y * width + canonical) + 1]) stripMismatch++;
                }
            int seamRow = height / 2;
            bool blockAtSeam = (map.Bytes[2 * (seamRow * width + originCell) + 1] & 15) == 0;
            worlds[label] = new Dictionary<string, object> { ["worldContentId"] = bundle.WorldContentId, ["extent"] = extent, ["width"] = width, ["cell"] = cellX, ["originCell"] = originCell,
                ["periodCells"] = periodCells, ["wholeCells"] = whole, ["buildSeconds"] = Math.Round(seconds, 1), ["map"] = rec, ["stripMismatches"] = stripMismatch, ["blockAtSeam"] = blockAtSeam,
                ["landTerrains"] = landFaces.OrderBy(x => x).Select(resolver.Name).ToList(), ["seaTerrains"] = seaFaces.OrderBy(x => x).Select(resolver.Name).ToList(),
                ["densePoints"] = densePoints, ["denseMisses"] = denseMisses, ["denseMissExamples"] = missExamples };
            Check(whole, label + ": the periodic strip and width are whole battle-map cells (" + width + " cells of " + cellX.ToString("0.###", CultureInfo.InvariantCulture) + "; strip " + originCell + ", period " + periodCells + "), as the in-game builder requires");
            Check(map.LandDeterministic == map.LandCells && map.SeaDeterministic == map.SeaChecks,
                  label + ": every one of " + map.LandCells + " cells with land resolves one land scene and every one of " + map.SeaChecks + " (cell, sea terrain) pairs one naval scene (" + map.SeaExact + " of the sea terrain itself) in " +
                  Math.Round(seconds, 1) + " s", rec);
            var biomesSeen = map.Biomes.Keys.ToList();
            Check(biomesSeen.Count >= 4 && map.Classes.Count >= 3 && map.Preferred * 10 >= map.Keys.Values.Sum() * 9,
                  label + ": representative geography: biomes " + String.Join(", ", map.Biomes.Select(b => b.Key + " " + b.Value)) + " -> classes " + String.Join(", ", map.Classes.Select(c => c.Key + " " + c.Value)) +
                  "; " + map.Preferred + " of " + map.Keys.Values.Sum() + " canonical cells take their class's preferred scenes", new { tiers = map.Tiers });
            Check(denseMisses == 0, label + ": dense check: " + densePoints + " land and sea party points (6 x 6 per cell) all resolve deterministically through the engine's rule for their cell's index", missExamples);
            Check(map.RobustCells > 0 && map.Robust == map.RobustCells && map.Relief > 0,
                  label + ": the " + map.RobustCells + " cells around the " + navInput.Settlements.Count + " settlements take naval-robust indices; " + map.Relief + " cells take relief-preferred scenes (landforms " +
                  String.Join(", ", map.Landforms.Select(l => l.Key + " " + l.Value)) + ")");
            Check(stripMismatch == 0 && blockAtSeam, label + ": canonical patch sampling: every strip cell holds its canonical cell's bytes and a 16-cell block starts at the seam column " + originCell);
            // A seam-adjacent pair on the real map: images of the same canonical point read the same patch; the encounter direction points the short way.
            double seamY = (seamRow + .5) * cellY, attackerX = surface.Strip + surface.LogicalWidth - .3, defenderX = surface.Strip + .2;
            double pa1, pa2, pb1, pb2;
            var a1 = GeneratedEncounterResolver.ReadPatch(map.Bytes, width, height, extent, extent, attackerX, seamY, out pa1, out pb1);
            var a2 = GeneratedEncounterResolver.ReadPatch(map.Bytes, width, height, extent, extent, attackerX - surface.LogicalWidth, seamY, out pa2, out pb2);
            var coordinates = new PeriodicWorldCoordinates(surface.LogicalWidth, surface.LogicalHeight, surface.Strip, surface.PoleMargin, surface.Extent);
            double expectedDx = coordinates.DeltaX(defenderX, attackerX);
            var dir = GeneratedEncounterResolver.EncounterDirection(new[] { attackerX, seamY }, new[] { defenderX, seamY }, surface.LogicalWidth);
            Check(a1[0] == a2[0] && pa1 == pa2 && pb1 == pb2 && Math.Sign(dir[0]) == Math.Sign(expectedDx) && Math.Abs(expectedDx) < 1 && Math.Abs(dir[0]) > .99,
                  label + ": a seam-adjacent pair (attacker 0.3 before the seam, defender 0.2 after it): the attacker's position and its other image read the same patch (index " + a1[0] + "), and the encounter " +
                  "direction is the short way through the seam (" + dir[0].ToString("0.###", CultureInfo.InvariantCulture) + ", PeriodicWorldCoordinates.DeltaX " + expectedDx.ToString("0.###", CultureInfo.InvariantCulture) + "; planar would be " +
                  Math.Sign(attackerX - defenderX) + ")");
        }
        evidence["conversations"] = resolver.ConversationCoverage(generatedTerrains);
        var coverage = (SortedDictionary<string, object>)((Dictionary<string, object>)evidence["conversations"])["terrains"];
        Func<string, Dictionary<string, object>> conv = t => coverage.ContainsKey(t) ? (Dictionary<string, object>)coverage[t] : null;
        Check(conv("Plain") != null && ((List<string>)conv("Plain")["scenes"]).Count > 0 && conv("CoastalSea") != null && ((List<string>)conv("CoastalSea")["scenes"]).Count > 0 &&
              (conv("Bridge") == null || conv("Bridge")["countsAs"] == null),
              "conversations: GetConversationSceneForMapPosition's rule on the generated terrains: Plain and the sea terrains have conversation scenes of their own; Bridge and UnderBridge are not counted " +
              "(the dominant counted terrain around them decides)", coverage);

        // ---- synthetic periodic seam fixture ----
        {
            int width = 80, height = 16, originCell = 20, periodCells = 40;
            var map = resolver.Build(width, height, originCell, periodCells, true, "mnbl_seam_fixture", (cx, y) => cell((cx - originCell) % 7 < 3 ? new[] { "Plain" } : new string[0], (cx - originCell) % 7 >= 3 ? new[] { "CoastalSea" } : new string[0], (cx - originCell) % 5 == 0 ? "forest" : "grassland"));
            bool images = true;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int lx = GeneratedEncounterResolver.CanonicalColumn(x, originCell, periodCells);
                foreach (int other in new[] { originCell + lx, originCell + lx + periodCells, originCell + lx - periodCells })
                    if (other >= 0 && other < width && (map.Bytes[2 * (y * width + x)] != map.Bytes[2 * (y * width + other)] || map.Bytes[2 * (y * width + x) + 1] != map.Bytes[2 * (y * width + other) + 1])) images = false;
            }
            double pa, pb, qa, qb;
            var west = GeneratedEncounterResolver.ReadPatch(map.Bytes, width, height, width * 4.0, height * 4.0, (originCell + .5) * 4, 10, out pa, out pb);
            var east = GeneratedEncounterResolver.ReadPatch(map.Bytes, width, height, width * 4.0, height * 4.0, (originCell + periodCells + .5) * 4, 10, out qa, out qb);
            var coordinates = new PeriodicWorldCoordinates(periodCells * 4.0, height * 4.0, originCell * 4.0, 0, width * 4.0);
            double ax = (originCell + periodCells) * 4.0 - .3, bx = originCell * 4.0 + .2;
            var dir = GeneratedEncounterResolver.EncounterDirection(new[] { ax, 20.0 }, new[] { bx, 21.0 }, periodCells * 4.0);
            Check(images && west[0] == east[0] && pa == qa && pb == qb && pa == 0 && Math.Sign(dir[0]) == Math.Sign(coordinates.DeltaX(bx, ax)) && dir[0] < 0,
                  "seam fixture (80 x 16 cells, strip 20, period 40): every cell equals all its images, the first canonical column and its east image read the same patch with x = 0 (a block starts at the seam), " +
                  "and a seam-adjacent attacker and defender get the short-way direction (" + dir[0].ToString("0.###", CultureInfo.InvariantCulture) + ", " + dir[1].ToString("0.###", CultureInfo.InvariantCulture) + ")");
        }

        // ---- geographic mismatch ----
        var naval46 = resolver.Check(46, true, T("CoastalSea"), null);
        var agree = resolver.Check(225, true, T("CoastalSea"), "battle_terrain_coastal_021");
        var wrong = resolver.Check(225, true, T("CoastalSea"), "battle_terrain_coastal_03");
        var none = resolver.Check(0, false, T("Plain"), "battle_terrain_a");
        int unlistedIndex = Enumerable.Range(1, 255).First(i => table.ExpectedScene(i, false, T("Plain")).Candidates.Length == 0);
        var unlisted = resolver.Check(unlistedIndex, false, T("Plain"), null);
        Check(!(bool)none["agrees"] && ((string)none["problem"]).Contains("no generated battle map") && !(bool)naval46["agrees"] && (bool)agree["agrees"] && !(bool)wrong["agrees"] && !(bool)unlisted["agrees"],
              "mismatch: index 0 (no generated map: Calradia indexing would be silent), an index that resolves randomly (46 naval on CoastalSea: two scenes), an opened scene other than the patch's and an " +
              "index no scene lists are each flagged; the patch's own scene agrees", new { none, naval46, agree, wrong, unlisted });

        evidence["checks"] = checks; evidence["failures"] = failures; evidence["rows"] = Rows;
        File.WriteAllText(args[4], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private const double GeneratedBattleTerrainCellSize = 4.0;   // GeneratedBattleTerrain.CellSize
}
