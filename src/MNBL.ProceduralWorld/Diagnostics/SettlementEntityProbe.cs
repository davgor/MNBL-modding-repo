using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using HarmonyLib;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-40 in a campaign (generated_campaign with settlementEntityProbe=1 on a fixture whose settlement entities the SettlementEntityFactory
    // built: tools/Build-FixtureSettlements.ps1; with factoryEntities=1 the fixture castle, castle village and hideout take the runtime path).
    // For every generated settlement:
    //   entity    one scene entity named its id (Scene.GetCampaignEntityWithName), the strategic entity of its settlement visual (SettlementVisual.
    //             OnStartup bound it), made by the scene (baked) or at run time (SettlementEntityRuntime), never by a vanilla prefab
    //   gate      the settlement's gate position is the layout's (the placement's); OnStartup reads the main_map_city_gate child for its banners
    //             (and later removes marker children), so the child itself is checked before the campaign (inspection, offline)
    //   port      a port town's port is its main_map_city_port child; a harbour village's port is its main_map_village_dropoff child (War Sails'
    //             NavalMapSceneWrapper.InitializeDropOffLocations set it from the scene)
    //   blockade  BlockadePositionScript is bound on a port town with arc children; GetBlockadeArc for MaximumNumberOfShips places that many
    //             ships, each over a water face of the campaign map
    //   siege     OnStartup took the camp children's frames into Town.BesiegerCampPositions1/2 (IMapScene.GetSiegeCampFrames), the siege engine,
    //             ram and tower children into the visual's spawn entities (PopulateSiegeEngineFrameListsFromChildren), and the wall sections and
    //             wall engines of every level, of which the current wall level's two sections and four engines are cached
    //             (UpdateDefenderSiegeEntitiesCache: siege bombardment indexes the section cache unguarded); baked and run-time entities alike
    //   levels    with the levels the campaign set (SetSettlementLevelVisibility shows a child whose level mask holds the visual's current
    //             mask): a town's or castle's icon is visible, and exactly one of a village's four icons; an unspotted hideout is hidden by design
    //   cache     the campaign map has a CampaignMapSiegePrefabEntityCache (GeneratedMapScene adds it to a scene without one) and MapScreen took it
    internal static class SettlementEntityProbe
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Dictionary<string, object> Run(GeneratedCampaignRegistration registration, string placementPath, List<string> failures)
        {
            var report = new Dictionary<string, object>();
            var scene = ((TaleWorlds.CampaignSystem.Map.IMapScene)Campaign.Current.MapSceneWrapper);
            var map = Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            var native = map.Scene;
            var placement = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(placementPath));
            var records = ((object[])placement["settlements"]).Cast<Dictionary<string, object>>().ToDictionary(s => (string)s["id"], s => s, StringComparer.Ordinal);
            report["builder"] = placement.ContainsKey("settlementEntities") ? placement["settlementEntities"] : null;
            if (!placement.ContainsKey("settlementEntities")) failures.Add("The fixture's settlement entities were not built by the SettlementEntityFactory (tools/Build-FixtureSettlements.ps1).");
            // Settlement visuals by settlement id, from MapScreen.VisualsOfEntities.
            var visualType = AccessTools.TypeByName("SandBox.View.Map.Visuals.SettlementVisual");
            var strategic = visualType.GetProperty("StrategicEntity", Members); var mapEntity = visualType.GetProperty("MapEntity", Members);
            var registry = AccessTools.TypeByName("SandBox.View.Map.MapScreen").GetProperty("VisualsOfEntities", Members).GetValue(null) as IDictionary;
            var visuals = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var v in registry.Values.Cast<object>().Where(v => v != null && visualType.IsInstanceOfType(v)).Distinct())
            {
                var party = mapEntity.GetValue(v) as TaleWorlds.CampaignSystem.Party.PartyBase;
                if (party != null && party.Settlement != null) visuals[party.Settlement.StringId] = v;
            }
            var blockadeType = AccessTools.TypeByName("SandBox.View.Map.BlockadePositionScript");
            var arcMethod = blockadeType == null ? null : blockadeType.GetMethod("GetBlockadeArc", Members);
            // The siege prefab cache: added to (or found in) the scene by GeneratedMapScene.Load, and taken by MapScreen.OnInitialize.
            var screenType = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
            var screen = screenType.GetProperty("Instance", Members) == null ? null : screenType.GetProperty("Instance", Members).GetValue(null);
            var taken = screen == null || screenType.GetProperty("PrefabEntityCache", Members) == null ? null : screenType.GetProperty("PrefabEntityCache", Members).GetValue(screen);
            report["siegePrefabCache"] = new Dictionary<string, object> { ["source"] = GeneratedMapScene.SiegePrefabCacheSource, ["inScene"] = native.GetFirstEntityWithScriptComponent<SandBox.CampaignMapSiegePrefabEntityCache>() != null, ["takenByMapScreen"] = taken != null };
            if (native.GetFirstEntityWithScriptComponent<SandBox.CampaignMapSiegePrefabEntityCache>() == null || taken == null) failures.Add("The campaign map has no siege prefab cache MapScreen took (" + GeneratedMapScene.SiegePrefabCacheSource + ").");
            report["missingBakedEntities"] = GeneratedMapScene.MissingBakedEntities.ToList();
            var policy = SettlementPlacementPolicy.Installed();
            int maxShips = Int32.Parse(policy.Blockade["MaximumNumberOfShips"], System.Globalization.CultureInfo.InvariantCulture);
            Func<Vec3, bool> water = p => {
                var face = scene.GetFaceIndex(new CampaignVec2(p.AsVec2, false));
                if (!face.IsValid()) return false;
                var t = scene.GetFaceTerrainType(face);
                return t == TaleWorlds.Core.TerrainType.OpenSea || t == TaleWorlds.Core.TerrainType.CoastalSea || t == TaleWorlds.Core.TerrainType.Water || t == TaleWorlds.Core.TerrainType.Lake || t == TaleWorlds.Core.TerrainType.River; };
            var rows = new List<object>(); report["settlements"] = rows;
            int baked = 0, runtime = 0;
            var everything = new List<GameEntity>(); native.GetEntities(ref everything);
            var byName = everything.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var settlement in Settlement.All.Where(s => s.StringId.StartsWith(registration.WorldId + "_", StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                string id = settlement.StringId;
                var row = new Dictionary<string, object> { ["id"] = id, ["kind"] = settlement.IsTown ? "town" : settlement.IsCastle ? "castle" : settlement.IsVillage ? "village" : settlement.IsHideout ? "hideout" : "other" };
                rows.Add(row);
                Action<string> fail = m => { failures.Add(id + ": " + m); row["failure"] = (row.ContainsKey("failure") ? row["failure"] + "; " : "") + m; };
                // One scene entity named the id (every entity scanned), and it is the settlement visual's strategic entity (OnStartup bound it).
                List<GameEntity> named; byName.TryGetValue(id, out named); named = named ?? new List<GameEntity>();
                var entity = named.Count == 1 ? named[0] : null;
                row["lookup"] = native.GetCampaignEntityWithName(id) == null ? "null after start" : "found";   // recorded: see docs/settlement-entities.md
                bool isRuntime = GeneratedMapScene.RuntimeSettlementEntities.Contains(id), prefab = GeneratedMapScene.FixturePrefabs.ContainsKey(id);
                row["entities"] = named.Count; row["made"] = isRuntime ? "runtime" : prefab ? "prefab" : "scene";
                if (isRuntime) runtime++; else if (!prefab) baked++;
                {
                    object v0; visuals.TryGetValue(id, out v0);
                    var b0 = v0 == null ? null : strategic.GetValue(v0) as GameEntity;
                    row["boundName"] = b0 == null ? null : b0.Name; row["boundTags"] = b0 == null ? null : b0.Tags.ToList();
                    row["firstWithName"] = native.GetFirstEntityWithName(id) == null ? null : "found";
                    row["boundInMapScene"] = b0 != null && b0.Scene == native; row["boundParent"] = b0 == null || b0.Parent == null ? null : b0.Parent.Name;
                    // After the campaign starts the scene's name lookups no longer return settlement roots (recorded); the strategic entity the
                    // visual bound at OnStartup, named its id with its kind's tag, is the settlement's entity.
                    if (entity == null && named.Count == 0 && b0 != null && b0.Name == id) entity = b0;
                }
                if (entity == null) { fail(named.Count + " scene entities named its id and no strategic entity of that name"); continue; }
                if (prefab) fail("its entity is a vanilla prefab (FixturePrefabs), not the factory's");
                object visual; visuals.TryGetValue(id, out visual);
                var bound = visual == null ? null : strategic.GetValue(visual) as GameEntity;
                row["visualBound"] = bound != null && bound == entity;
                if (bound == null || bound != entity) fail("its settlement visual's strategic entity is not the scene entity (" + (bound == null ? "none" : bound.Name) + ")");
                Func<string, List<GameEntity>> tagged = t => { var list = new List<GameEntity>(); for (int c = 0; c < entity.ChildCount; c++) if (entity.GetChild(c).HasTag(t)) list.Add(entity.GetChild(c)); return list; };
                Func<Vec3, double[], double> distance = (a, b) => Math.Sqrt((a.x - b[0]) * (a.x - b[0]) + (a.y - b[1]) * (a.y - b[1]));
                Dictionary<string, object> record; records.TryGetValue(id, out record);
                // Gate.
                if (settlement.IsFortification)
                {
                    var gatePos = new[] { (double)settlement.GatePosition.X, (double)settlement.GatePosition.Y };
                    row["gate"] = gatePos;
                    if (record != null && record.ContainsKey("gatePosition") && !isRuntime)
                    {
                        var g = ((object[])record["gatePosition"]).Select(Convert.ToDouble).ToArray();
                        if (Math.Abs(g[0] - gatePos[0]) > 1e-3 || Math.Abs(g[1] - gatePos[1]) > 1e-3) fail("its gate position is not the placement's resolved gate");
                    }
                    {
                        // Siege camps: OnStartup's GetSiegeCampFrames into the town's besieger camp positions, near the town (the installed reach).
                        var camps1 = settlement.Town.BesiegerCampPositions1; var camps2 = settlement.Town.BesiegerCampPositions2;
                        row["besiegerCampPositions"] = new[] { camps1 == null ? -1 : camps1.Length, camps2 == null ? -1 : camps2.Length };
                        Func<MatrixFrame, double> fromCentre = fr => fr.origin.AsVec2.Distance(settlement.Position.ToVec2());
                        if (camps1 == null || camps2 == null || camps1.Length != 1 || camps2.Length != 1) fail("the besieger camp positions are not the one camp child of each kind");
                        else if (camps1.Concat(camps2).Any(fr => fromCentre(fr) > 7 || fromCentre(fr) < 0.5)) fail("a besieger camp position is not near the town (" + String.Join(", ", camps1.Concat(camps2).Select(fr => fromCentre(fr).ToString("0.##"))) + ")");
                        // Siege engines, ram and towers: the visual's spawn entities.
                        Func<string, int> spawns = field => { var value = visualType.GetField(field, Members) == null ? null : visualType.GetField(field, Members).GetValue(visual) as GameEntity[]; return value == null ? -1 : value.Length; };
                        int attackers = spawns("_attackerRangedEngineSpawnEntities"), rams = spawns("_attackerBatteringRamSpawnEntities"), towers = spawns("_attackerSiegeTowerSpawnEntities"), defenders = spawns("_defenderRangedEngineSpawnEntitiesForAllLevels");
                        int walls = spawns("_defenderBreachableWallEntitiesForAllLevels"), wallCache = spawns("_defenderBreachableWallEntitiesCacheForCurrentLevel"), defenderCache = spawns("_defenderRangedEngineSpawnEntitiesCacheForCurrentLevel");
                        row["siegeSpawns"] = new Dictionary<string, object> { ["attackerRanged"] = attackers, ["batteringRam"] = rams, ["siegeTower"] = towers, ["defenderRangedAllLevels"] = defenders,
                            ["breachableWallsAllLevels"] = walls, ["breachableWallsCurrentLevel"] = wallCache, ["defenderRangedCurrentLevel"] = defenderCache, ["wallLevel"] = settlement.Town.GetWallLevel(),
                            ["wallSections"] = settlement.SettlementWallSectionHitPointsRatioList == null ? -1 : settlement.SettlementWallSectionHitPointsRatioList.Count };
                        if (attackers != 4 || rams != 1 || towers != 2) fail("the siege spawn entities are not the factory's (attackers " + attackers + ", rams " + rams + ", towers " + towers + ")");
                        if (walls != 6 || defenders != 12) fail("the wall sections and wall engines of all levels are " + walls + " and " + defenders + ", not 6 and 12");
                        if (wallCache != 2 || defenderCache != 4) fail("the current wall level's cache holds " + wallCache + " wall sections and " + defenderCache + " wall engines, not 2 and 4");
                    }
                }
                // Ports and drop-offs.
                if (settlement.IsTown && record != null && record.ContainsKey("portPosition") && record["portPosition"] != null)
                {
                    var port = tagged("main_map_city_port");
                    row["hasPort"] = settlement.HasPort;
                    if (port.Count != 1) fail(port.Count + " port children");
                    if (!settlement.HasPort) fail("the port town has no port");
                    else if (port.Count == 1 && distance(port[0].GlobalPosition, new double[] { settlement.PortPosition.X, settlement.PortPosition.Y }) > 2e-3) fail("its port child is not its port position");
                    bool arcs = tagged("Blockade_Arc_Start").Count == 1 && tagged("Blockade_Arc_End").Count == 1;
                    object script = null;
                    try { script = blockadeType == null ? null : typeof(GameEntity).GetMethods().First(m => m.Name == "GetFirstScriptOfType" && m.IsGenericMethodDefinition).MakeGenericMethod(blockadeType).Invoke(entity, null); }
                    catch (Exception error) { fail("BlockadePositionScript lookup: " + error.InnerException?.Message); }
                    row["blockadeScript"] = script != null; row["blockadeArcs"] = arcs;
                    if (arcs && script == null) fail("arc children without a bound BlockadePositionScript");
                    if (script != null && arcMethod != null)
                    {
                        try
                        {
                            var args = new object[] { maxShips, Vec3.Zero };
                            var result = arcMethod.Invoke(script, args) as IList;
                            var ships = result == null ? new List<Vec3>() : result.Cast<IList>().SelectMany(a => a.Cast<Vec3>()).ToList();
                            int onWater = ships.Count(water);
                            row["blockadeShips"] = ships.Count; row["blockadeShipsOnWater"] = onWater;
                            if (ships.Count != maxShips) fail("BlockadePositionScript placed " + ships.Count + " of " + maxShips + " ships");
                            else if (onWater != ships.Count) fail((ships.Count - onWater) + " of its blockade ships are not over a water face");
                        }
                        catch (Exception error) { fail("BlockadePositionScript.GetBlockadeArc threw " + (error.InnerException ?? error).GetType().Name + ": " + (error.InnerException ?? error).Message); }
                    }
                }
                if (settlement.IsVillage)
                {
                    var drop = tagged("main_map_village_dropoff");
                    row["dropoffs"] = drop.Count; row["hasPort"] = settlement.HasPort;
                    bool harbour = record != null && record.ContainsKey("harbour") && Convert.ToBoolean(record["harbour"]);
                    if (harbour && drop.Count != 1) fail(drop.Count + " drop-off children on a harbour village");
                    if (harbour && drop.Count == 1 && (!settlement.HasPort || distance(drop[0].GlobalPosition, new double[] { settlement.PortPosition.X, settlement.PortPosition.Y }) > 2e-3))
                        fail("War Sails did not take its drop-off as its port (HasPort " + settlement.HasPort + ")");
                }
                // The icons with the levels the campaign set: one visible (a village has four, one per level and a looted one).
                var icons = Enumerable.Range(0, entity.ChildCount).Select(c => entity.GetChild(c)).Where(c => c.Name == id + "_icon" || c.Name.StartsWith(id + "_icon_", StringComparison.Ordinal)).ToList();
                var visibleIcons = icons.Where(c => c.IsVisibleIncludeParents()).Select(c => c.Name).ToList();
                row["icons"] = icons.Count; row["visibleIcons"] = visibleIcons; row["iconVisible"] = visibleIcons.Count == 1;
                row["iconMasks"] = icons.Select(c => (uint)c.GetUpgradeLevelMask()).ToList();
                bool hiddenByDesign = settlement.IsHideout && !settlement.Hideout.IsSpotted;
                if (hiddenByDesign) row["iconHiddenBecause"] = "unspotted hideout";
                if (icons.Count != (settlement.IsVillage ? 4 : 1)) fail(icons.Count + " icons");
                else if (visibleIcons.Count != 1 && !hiddenByDesign) fail(visibleIcons.Count + " of its icons are visible with the campaign's levels, not one (" + String.Join(", ", visibleIcons) + ")");
            }
            report["baked"] = baked; report["runtime"] = runtime; report["fixturePrefabs"] = GeneratedMapScene.FixturePrefabs.Count;
            report["rootEntities"] = native.RootEntityCount;
            return report;
        }

        // After a world has ended and the next one is running: nothing of the previous world remains. The map screen's visuals registry
        // (MapScreen.VisualsOfEntities, which owns every settlement visual and its strategic entity) holds no visual of a previous world's
        // settlement and exactly one per current settlement, each bound to an entity named its id; no entity of the new scene carries a
        // previous id; the runtime entity, missing-entity and fixture prefab lists hold no previous id.
        internal static Dictionary<string, object> Teardown(string previousWorldId, string currentWorldId, IEnumerable<string> previousIds, List<string> failures)
        {
            var map = Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            var visualType = AccessTools.TypeByName("SandBox.View.Map.Visuals.SettlementVisual");
            var strategic = visualType.GetProperty("StrategicEntity", Members); var mapEntity = visualType.GetProperty("MapEntity", Members);
            var registry = AccessTools.TypeByName("SandBox.View.Map.MapScreen").GetProperty("VisualsOfEntities", Members).GetValue(null) as IDictionary;
            var visualIds = new List<string>(); int unbound = 0;
            foreach (var v in registry.Values.Cast<object>().Where(v => v != null && visualType.IsInstanceOfType(v)).Distinct())
            {
                var party = mapEntity.GetValue(v) as TaleWorlds.CampaignSystem.Party.PartyBase;
                if (party == null || party.Settlement == null) continue;
                visualIds.Add(party.Settlement.StringId);
                var bound = strategic.GetValue(v) as GameEntity;
                if (party.Settlement.StringId.StartsWith(currentWorldId + "_", StringComparison.Ordinal) && (bound == null || bound.Name != party.Settlement.StringId)) unbound++;
            }
            var previousVisuals = visualIds.Where(id => id.StartsWith(previousWorldId + "_", StringComparison.Ordinal)).ToList();
            int currentVisuals = visualIds.Count(id => id.StartsWith(currentWorldId + "_", StringComparison.Ordinal)), currentSettlements = Settlement.All.Count(s => s.StringId.StartsWith(currentWorldId + "_", StringComparison.Ordinal));
            var everything = new List<GameEntity>(); map.Scene.GetEntities(ref everything);
            var names = new HashSet<string>(everything.Select(e => e.Name), StringComparer.Ordinal);
            var leftover = previousIds.Where(names.Contains).ToList();
            Func<IEnumerable<string>, List<string>> previous = ids => ids.Where(id => id.StartsWith(previousWorldId + "_", StringComparison.Ordinal)).ToList();
            var runtimeLeft = previous(GeneratedMapScene.RuntimeSettlementEntities); var missingLeft = previous(GeneratedMapScene.MissingBakedEntities); var prefabsLeft = previous(GeneratedMapScene.FixturePrefabs.Keys);
            if (previousVisuals.Count > 0) failures.Add("previous world's settlement visuals remain: " + String.Join(", ", previousVisuals.Take(10)));
            if (currentVisuals != currentSettlements || currentSettlements == 0) failures.Add(currentVisuals + " settlement visuals for " + currentSettlements + " settlements of the current world");
            if (unbound > 0) failures.Add(unbound + " current settlement visuals are not bound to an entity named their settlement");
            if (leftover.Count > 0) failures.Add("previous world's settlement entities remain: " + String.Join(", ", leftover));
            if (runtimeLeft.Count > 0 || missingLeft.Count > 0) failures.Add("previous world's runtime settlement entities are still listed: " + String.Join(", ", runtimeLeft.Concat(missingLeft)));
            if (prefabsLeft.Count > 0) failures.Add("previous world's fixture prefabs are still listed: " + String.Join(", ", prefabsLeft));
            return new Dictionary<string, object> { ["previousWorld"] = previousWorldId, ["currentWorld"] = currentWorldId, ["previousVisualsLeft"] = previousVisuals, ["currentVisuals"] = currentVisuals,
                ["currentSettlements"] = currentSettlements, ["unboundVisuals"] = unbound, ["previousIdsChecked"] = previousIds.Count(), ["entitiesLeft"] = leftover, ["runtimeListedLeft"] = runtimeLeft,
                ["missingBakedLeft"] = missingLeft, ["prefabsLeft"] = prefabsLeft, ["runtimeListed"] = GeneratedMapScene.RuntimeSettlementEntities.Count, ["rootEntities"] = map.Scene.RootEntityCount };
        }
    }
}
