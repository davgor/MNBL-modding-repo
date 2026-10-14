using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR17: engine routes and actual party movement on a generated world. Every route below is the
    // installed native path query (IMapScene.GetPathBetweenAIFaces) or real party movement; no separate
    // graph validator stands in for either.
    internal static class GeneratedNavigationProbe
    {
        private const int ObstructionGroup = 5, LakeGroup = 8;

        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId,
            string placementPath, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var placement = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(placementPath));
            var tests = (Dictionary<string, object>)placement["navigationTests"];
            var campaign = Campaign.Current;
            var map = campaign.MapSceneWrapper;
            var scene = ((SandBox.MapScene)map).Scene;
            var navModel = campaign.Models.PartyNavigationModel;
            var distanceModel = campaign.Models.MapDistanceModel;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            var village1 = Settlement.Find(worldId + "_village_1");

            report["engine"] = new Dictionary<string, object> {
                ["partyNavigationModel"] = navModel.GetType().FullName, ["mapDistanceModel"] = distanceModel.GetType().FullName,
                ["invalidTerrain"] = new Dictionary<string, object> {
                    ["Default"] = navModel.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.Default),
                    ["Naval"] = navModel.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.Naval),
                    ["All"] = navModel.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.All) },
                ["regionSwitchCostLandToSea"] = distanceModel.RegionSwitchCostFromLandToSea,
                ["regionSwitchCostSeaToLand"] = distanceModel.RegionSwitchCostFromSeaToLand,
                ["playerRegionSwitchCostLandToSea"] = Campaign.PlayerRegionSwitchCostFromLandToSea,
                ["pathFindingMaxCostLimit"] = Campaign.PathFindingMaxCostLimit,
                ["embarkDisembarkThreshold"] = navModel.GetEmbarkDisembarkThresholdDistance(),
                ["navigationFaces"] = map.GetNumberOfNavigationMeshFaces(),
                ["stitching"] = "Not exercised: each world loads one generated navmesh.bin; no runtime prefab assembly or stitching.",
                ["nativeBackedCalls"] = new[] { "Scene.GetPathBetweenAIFaces (via IMapScene.GetPathBetweenAIFaces)",
                    "Scene.GetPathDistanceBetweenAIFaces (via IMapScene.GetPathDistanceBetweenAIFaces)",
                    "Scene.GetNavMeshFaceIndex (via CampaignVec2.Face -> IMapScene.GetFaceIndex)", "Scene.SetAbilityOfFacesWithId" },
                ["campaignApisInvoked"] = new[] { "MobileParty.SetMoveGoToPoint", "MobileParty.RecalculateLongTermPath", "MobileParty.SetSailAtPosition",
                    "MobilePartyAi.SetDoNotMakeNewDecisions", "ChangeShipOwnerAction.ApplyByLooting", "LeaveSettlementAction.ApplyForParty",
                    "EnterSettlementAction.ApplyForParty (gate-reset fallback only)", "PartyNavigationModel.GetInvalidTerrainTypesForNavigationType",
                    "MapDistanceModel.GetDistance", "MapDistanceModel.PathExistBetweenPoints", "MapDistanceModel.GetNeighborsOfFortification",
                    "MapDistanceModel.GetMaximumDistanceBetweenTwoConnectedSettlements", "MapDistanceModel.GetPortToGateDistanceForSettlement" } };

            // Face identities at every fixture point.
            Func<double[], bool, CampaignVec2> at = (p, isLand) => new CampaignVec2(new Vec2((float)p[0], (float)p[1]), isLand);
            Func<object, double[]> xy = o => ((IEnumerable)o).Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            Func<CampaignVec2, Dictionary<string, object>> face = v => {
                var f = v.Face;
                return new Dictionary<string, object> { ["x"] = v.X, ["y"] = v.Y, ["onLand"] = v.IsOnLand, ["valid"] = f.IsValid(),
                    ["faceIndex"] = f.FaceIndex, ["group"] = f.FaceGroupIndex, ["island"] = f.FaceIslandIndex };
            };
            var lake = (Dictionary<string, object>)tests["lake"];
            var offLand = tests["offLandmassCoast"] as Dictionary<string, object>;
            var points = new Dictionary<string, CampaignVec2> {
                ["town1Gate"] = town1.GatePosition, ["town1Port"] = town1.PortPosition, ["town2Gate"] = town2.GatePosition, ["town2Port"] = town2.PortPosition,
                ["village1Gate"] = village1.GatePosition, ["lakeCenterAsWater"] = at(xy(lake["center"]), false), ["lakeCenterAsLand"] = at(xy(lake["center"]), true),
                ["lakeWestShore"] = at(xy(lake["westShore"]), true), ["lakeEastShore"] = at(xy(lake["eastShore"]), true),
                ["openSea"] = at(xy(tests["openSea"]), false) };
            if (offLand != null) { points["offLandmassLand"] = at(xy(offLand["land"]), true); points["offLandmassSea"] = at(xy(offLand["sea"]), false); }
            var cellSize = Convert.ToDouble(((Dictionary<string, object>)placement["nav"])["spacing"], CultureInfo.InvariantCulture);
            var obstructionTest = (Dictionary<string, object>)tests["obstruction"];
            var strip = ((IEnumerable)obstructionTest["cells"]).Cast<object>().Select(xy).ToArray();
            points["obstructionTarget"] = at(xy(obstructionTest["to"]), true);
            for (int i = 0; i < strip.Length; i++) points["obstructionCell" + i] = at(new[] { (strip[i][0] + .5) * cellSize, (strip[i][1] + .5) * cellSize }, true);
            report["faces"] = points.ToDictionary(p => p.Key, p => (object)face(p.Value));
            foreach (var p in points.Where(p => p.Key.StartsWith("obstructionCell", StringComparison.Ordinal)))
                if (p.Value.Face.FaceGroupIndex != ObstructionGroup) failures.Add("Obstruction cell is not a steppe face: " + p.Key);
            if (points["lakeCenterAsWater"].Face.FaceGroupIndex != LakeGroup) failures.Add("Lake center is not a lake face.");
            context.Checkpoint();

            var routes = new Dictionary<string, object>(); report["routes"] = routes;
            Func<string, CampaignVec2, CampaignVec2, MobileParty.NavigationType, Dictionary<string, object>> route = (name, a, b, nav) => {
                var watch = Stopwatch.StartNew();
                var path = new NavigationPath();
                var invalid = navModel.GetInvalidTerrainTypesForNavigationType(nav);
                float radius = a.IsOnLand ? 0.3f : navModel.GetEmbarkDisembarkThresholdDistance();
                bool found = map.GetPathBetweenAIFaces(a.Face, b.Face, a.ToVec2(), b.ToVec2(), radius, path, invalid, 2f,
                    distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                double pathMs = watch.Elapsed.TotalMilliseconds;
                float distance;
                bool reachable = map.GetPathDistanceBetweenAIFaces(a.Face, b.Face, a.ToVec2(), b.ToVec2(), radius, Campaign.PathFindingMaxCostLimit, out distance,
                    invalid, distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                var waypoints = new List<Vec2> { a.ToVec2() };
                for (int i = 0; found && i < path.Size; i++) waypoints.Add(path.PathPoints[i]);
                if (found) waypoints.Add(b.ToVec2());
                var record = new Dictionary<string, object> {
                    ["from"] = new[] { a.X, a.Y }, ["fromLand"] = a.IsOnLand, ["to"] = new[] { b.X, b.Y }, ["toLand"] = b.IsOnLand,
                    ["navigationType"] = nav.ToString(), ["found"] = found, ["reachable"] = reachable, ["distance"] = reachable ? (object)distance : null,
                    ["pathMilliseconds"] = pathMs, ["waypoints"] = found ? path.Size : 0,
                    ["points"] = waypoints.Select(w => new[] { w.x, w.y }).ToArray() };
                if (found) record["samples"] = SampleSegments(waypoints, invalid);
                routes[name] = record;
                return record;
            };
            Func<Dictionary<string, object>, string, int> count = (r, key) => r.ContainsKey("samples") ? Convert.ToInt32(((Dictionary<string, object>)r["samples"])[key]) : 0;

            var landVillage = route("landTown1ToVillage1", town1.GatePosition, village1.GatePosition, MobileParty.NavigationType.Default);
            if (!(bool)landVillage["found"] || count(landVillage, "offLand") != 0) failures.Add("Generated land route failed or left land.");
            var sea = route("seaTown1PortToTown2Port", town1.PortPosition, town2.PortPosition, MobileParty.NavigationType.Naval);
            if (!(bool)sea["found"] || count(sea, "offSea") != 0) failures.Add("Generated sea route failed or touched land.");
            // Both directions of the land and sea routes.
            var landBack = route("landVillage1ToTown1", village1.GatePosition, town1.GatePosition, MobileParty.NavigationType.Default);
            if (!(bool)landBack["found"] || count(landBack, "offLand") != 0) failures.Add("Reverse land route failed or left land.");
            var seaBack = route("seaTown2PortToTown1Port", town2.PortPosition, town1.PortPosition, MobileParty.NavigationType.Naval);
            if (!(bool)seaBack["found"] || count(seaBack, "offSea") != 0) failures.Add("Reverse sea route failed or touched land.");
            var landToSea = route("landToOpenSea_Default", town1.GatePosition, points["openSea"], MobileParty.NavigationType.Default);
            if ((bool)landToSea["found"] || (bool)landToSea["reachable"]) failures.Add("Default navigation reached open sea.");
            var seaToLake = route("seaToEnclosedLake_Naval", town1.PortPosition, points["lakeCenterAsWater"], MobileParty.NavigationType.Naval);
            if ((bool)seaToLake["found"] || (bool)seaToLake["reachable"]) failures.Add("Naval navigation reached the enclosed lake across land.");
            var seaToLand = route("seaToInland_Naval", town1.PortPosition, village1.GatePosition, MobileParty.NavigationType.Naval);
            if ((bool)seaToLand["found"] || (bool)seaToLand["reachable"]) failures.Add("Naval navigation reached an inland point.");
            // These two refusals are sanity checks only: each destination face is itself invalid for the
            // navigation type. The barrier evidence is the other-landmass, lake, sea-route and wall checks.
            report["sanityChecks"] = new[] { "landToOpenSea_Default: destination is a group-18 face, invalid for Default",
                "seaToInland_Naval: destination is a group-1 face, invalid for Naval" };
            // Positive control for the enclosed lake: its faces are routable from its own shore.
            var shoreToLake = route("lakeShoreToLake_All", points["lakeWestShore"], points["lakeCenterAsWater"], MobileParty.NavigationType.All);
            if (!(bool)shoreToLake["found"] || count(shoreToLake, "transitions") < 1) failures.Add("Lake is not routable from its own shore (positive control).");
            var aroundLake = route("landAroundLake_Default", points["lakeWestShore"], points["lakeEastShore"], MobileParty.NavigationType.Default);
            if (!(bool)aroundLake["found"] || count(aroundLake, "offLand") != 0) failures.Add("Land route across the lake was not routed around it.");
            if (offLand != null)
            {
                var crossLand = route("landToOtherLandmass_Default", town1.GatePosition, points["offLandmassLand"], MobileParty.NavigationType.Default);
                if ((bool)crossLand["found"] || (bool)crossLand["reachable"]) failures.Add("Default navigation crossed open water to another landmass.");
                var crossAll = route("landToOtherLandmass_All", town1.GatePosition, points["offLandmassLand"], MobileParty.NavigationType.All);
                if (!(bool)crossAll["found"] || count(crossAll, "transitions") < 2) failures.Add("All-navigation route to another landmass did not embark and disembark.");
                var crossBack = route("otherLandmassToLand_All", points["offLandmassLand"], town1.GatePosition, MobileParty.NavigationType.All);
                if (!(bool)crossBack["found"] || count(crossBack, "transitions") < 2) failures.Add("Reverse All-navigation route between landmasses did not embark and disembark.");
            }
            else failures.Add("No second coastal landmass on this terrain; cross-landmass routes not exercised.");

            // Distance-model compatibility: the campaign's settlement distance model next to the engine
            // path distances above. Recorded, not gated; each call is isolated so one failure stays visible.
            var model = new Dictionary<string, object>(); report["distanceModel"] = model;
            Action<string, Func<object>> measure = (name, f) => { try { model[name] = f(); } catch (Exception e) { model[name] = "error: " + e.GetType().Name + ": " + e.Message; } };
            measure("town1ToVillage1_Default", () => new Dictionary<string, object> {
                ["model"] = distanceModel.GetDistance(town1, village1, false, false, MobileParty.NavigationType.Default), ["engine"] = landVillage["distance"] });
            measure("town1PortToTown2Port_Naval", () => new Dictionary<string, object> {
                ["model"] = distanceModel.GetDistance(town1, town2, true, true, MobileParty.NavigationType.Naval), ["engine"] = sea["distance"] });
            measure("town1ToTown2_All", () => { float landRatio; float d = distanceModel.GetDistance(town1, town2, false, false, MobileParty.NavigationType.All, out landRatio);
                return new Dictionary<string, object> { ["model"] = d, ["landRatio"] = landRatio }; });
            measure("portToGateTown1", () => distanceModel.GetPortToGateDistanceForSettlement(town1));
            if (offLand != null)
            {
                var gate = town1.GatePosition; var other = points["offLandmassLand"];
                measure("pathExistsToOtherLandmass_Default", () => distanceModel.PathExistBetweenPoints(in gate, in other, MobileParty.NavigationType.Default));
                measure("pathExistsToOtherLandmass_All", () => distanceModel.PathExistBetweenPoints(in gate, in other, MobileParty.NavigationType.All));
            }
            foreach (var type in new[] { MobileParty.NavigationType.Default, MobileParty.NavigationType.Naval, MobileParty.NavigationType.All })
            {
                var t = type;
                measure("neighborsOfTown1_" + t, () => distanceModel.GetNeighborsOfFortification(town1.Town, t).Select(s => s.StringId).ToArray());
                measure("maxConnectedDistance_" + t, () => distanceModel.GetMaximumDistanceBetweenTwoConnectedSettlements(t));
            }
            context.Checkpoint();

            var movement = new Dictionary<string, object>(); report["movement"] = movement;
            var main = MobileParty.MainParty;
            var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { town1.Culture }).Where(c => c != null)
                .SelectMany(c => c.AvailableShipHulls).FirstOrDefault();
            movement["shipHull"] = hull == null ? null : hull.StringId;
            if (hull == null) failures.Add("No ship hull available from generated cultures.");

            // AI first, before any face-ability change: a generated ruler leaves its stock spawn point,
            // crosses land, embarks, sails, disembarks on the other landmass, then makes the return trip.
            var ruler = MobileParty.All.FirstOrDefault(p => p.LeaderHero != null && p.LeaderHero.StringId == worldId + "_ruler_1");
            if (ruler == null || hull == null || offLand == null) failures.Add("AI cross-landmass trip not exercised: ruler, hull or second landmass unavailable.");
            else
            {
                var ai = new Dictionary<string, object>(); movement["aiCrossLandmass"] = ai;
                if (ruler.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(ruler);
                ai["stateAtSpawn"] = PartyState(ruler); ai["faceCacheAtSpawn"] = FaceCache(ruler);
                ruler.Ai.SetDoNotMakeNewDecisions(true);
                ChangeShipOwnerAction.ApplyByLooting(ruler.Party, new Ship(hull));
                var destination = points["offLandmassLand"];
                ai["engineRoute"] = route("aiRulerToOtherLandmass_All", ruler.Position, destination, MobileParty.NavigationType.All);
                ruler.SetMoveGoToPoint(destination, MobileParty.NavigationType.All);
                ai["stateAfterOrder"] = PartyState(ruler);
                Func<bool> atDestination = () => !ruler.IsCurrentlyAtSea && ruler.Position.ToVec2().Distance(destination.ToVec2()) < 1.5f;
                var trip = Travel(context, boot, ruler, atDestination, 600, ai, true);
                while (trip.MoveNext()) yield return trip.Current;
                if (!(bool)ai["arrived"])
                {
                    // Stalled from spawn: record it as a failure, then still exercise the trip from town_1's gate.
                    failures.Add("AI party did not travel from its spawn point.");
                    var retry = new Dictionary<string, object>(); movement["aiCrossLandmassFromGate"] = retry;
                    if (ruler.CurrentSettlement != town1) EnterSettlementAction.ApplyForParty(ruler, town1);
                    LeaveSettlementAction.ApplyForParty(ruler);
                    ruler.SetMoveGoToPoint(destination, MobileParty.NavigationType.All);
                    var again = Travel(context, boot, ruler, atDestination, 600, retry, true);
                    while (again.MoveNext()) yield return again.Current;
                    ai = retry;
                }
                if (!(bool)ai["arrived"] || Convert.ToInt32(ai["transitions"]) < 2 || Convert.ToInt32(ai["samplesOnLand"]) == 0)
                    failures.Add("AI party did not cross land, embark, sail and disembark to the other landmass.");
                else
                {
                    var home = town1.GatePosition;
                    var back = new Dictionary<string, object>(); movement["aiReturnTrip"] = back;
                    back["engineRoute"] = route("aiRulerReturn_All", ruler.Position, home, MobileParty.NavigationType.All);
                    ruler.SetMoveGoToPoint(home, MobileParty.NavigationType.All);
                    var homeward = Travel(context, boot, ruler, () => !ruler.IsCurrentlyAtSea && ruler.Position.ToVec2().Distance(home.ToVec2()) < 1.5f, 600, back);
                    while (homeward.MoveNext()) yield return homeward.Current;
                    if (!(bool)back["arrived"] || Convert.ToInt32(back["transitions"]) < 2) failures.Add("AI return trip did not embark, sail and disembark back to town_1.");
                }
                ruler.Ai.SetDoNotMakeNewDecisions(false);
            }
            context.Checkpoint();

            // Obstruction: disabling face group 5 (the builder's wall) must reroute the engine path around it.
            var obstructionTarget = points["obstructionTarget"];
            var landOpen = route("landTown1ToObstructionTarget_open", town1.GatePosition, obstructionTarget, MobileParty.NavigationType.Default);
            int toggled = scene.SetAbilityOfFacesWithId(ObstructionGroup, false);  // raw: caches refreshed after the islands record below
            var landClosed = route("landTown1ToObstructionTarget_closed", town1.GatePosition, obstructionTarget, MobileParty.NavigationType.Default);
            report["obstruction"] = new Dictionary<string, object> {
                ["facesToggled"] = toggled, ["wallCells"] = strip.Length, ["openDistance"] = landOpen["distance"], ["closedDistance"] = landClosed["distance"],
                ["openStripSamples"] = count(landOpen, "obstruction"), ["closedStripSamples"] = count(landClosed, "obstruction"),
                ["builderDirectCells"] = obstructionTest["directCells"], ["builderDetourCells"] = obstructionTest["detourCells"] };
            if (toggled != 2 * strip.Length) failures.Add("Disabling group 5 did not toggle exactly the wall's faces (two per cell).");
            // NaN when unreachable, so every comparison against it fails the check.
            float openDistance = landOpen["distance"] == null ? float.NaN : (float)landOpen["distance"];
            float closedDistance = landClosed["distance"] == null ? float.NaN : (float)landClosed["distance"];
            if (!(bool)landOpen["found"] || count(landOpen, "offLand") != 0 || count(landOpen, "obstruction") == 0)
                failures.Add("Open obstruction route failed, left land, or did not cross the wall.");
            if (!(bool)landClosed["found"] || count(landClosed, "offLand") != 0 || count(landClosed, "obstruction") != 0
                || !(closedDistance > openDistance))
                failures.Add("Closing the obstruction did not reroute the engine path around the wall.");
            // Face-ability changes renumber navigation islands. Positions that cached a face record before the
            // change keep the old island, and MobileParty movement requires the current and next islands to
            // match, so cached parties stop moving (runs 22588/21284/17804). Recorded here before the refresh.
            scene.SetAbilityOfFacesWithId(ObstructionGroup, true);
            report["islandsAfterToggle"] = new Dictionary<string, object> {
                ["town1GateFresh"] = new CampaignVec2(town1.GatePosition.ToVec2(), true).Face.FaceIslandIndex,
                ["town1GateAtFixtureCapture"] = ((Dictionary<string, object>)((Dictionary<string, object>)report["faces"])["town1Gate"])["island"],
                ["mainPartyPositionCache"] = FaceCache(main),
                ["otherParties"] = MobileParty.All.Where(p => p != main && p != ruler && p.IsActive && p.CurrentSettlement == null).Take(6)
                    .ToDictionary(p => p.StringId, p => (object)FaceCache(p)) };
            report["faceCacheRefresh"] = new Dictionary<string, object> { ["partiesRefreshed"] = RefreshFaceCaches(),
                ["note"] = "Probe-side compensation: after every SetAbilityOfFacesWithId the probe rebuilds each party's stored CampaignVec2 fields without a cached face." };
            context.Checkpoint();

            // Player on land with the wall closed: the planned path and the walked track must go around it.
            Toggle(scene, false);
            main.SetMoveGoToPoint(obstructionTarget, MobileParty.NavigationType.Default);
            main.RecalculateLongTermPath();
            var closedPlan = PathPoints(main);
            Toggle(scene, true);
            main.RecalculateLongTermPath();
            var openPlan = PathPoints(main);
            Toggle(scene, false);
            main.RecalculateLongTermPath();
            movement["playerPlannedPathOpen"] = openPlan; movement["playerPlannedPathClosed"] = closedPlan;
            bool planChanged = !openPlan.SequenceEqual(closedPlan, new PointComparer());
            movement["playerPlanChangedWithObstruction"] = planChanged;
            var land = new Dictionary<string, object>(); movement["playerLand"] = land;
            var walk = Travel(context, boot, main, () => main.Position.ToVec2().Distance(obstructionTarget.ToVec2()) < 0.6f, 240, land);
            while (walk.MoveNext()) yield return walk.Current;
            Toggle(scene, true);
            if (!(bool)land["arrived"] || Convert.ToInt32(land["samplesOnObstruction"]) != 0 || Convert.ToInt32(land["samplesAtSea"]) != 0 || !planChanged
                || !(Convert.ToSingle(land["traveled"]) > openDistance))
                failures.Add("Player land movement did not walk around the closed obstruction to its target.");

            // Reverse direction with the wall open: the player walks back to town_1 across the wall.
            var town1Gate = town1.GatePosition;
            main.SetMoveGoToPoint(town1Gate, MobileParty.NavigationType.Default);
            var landBackWalk = new Dictionary<string, object>(); movement["playerLandReturnWallOpen"] = landBackWalk;
            var walkBack = Travel(context, boot, main, () => main.Position.ToVec2().Distance(town1Gate.ToVec2()) < 0.6f, 240, landBackWalk);
            while (walkBack.MoveNext()) yield return walkBack.Current;
            if (!(bool)landBackWalk["arrived"] || Convert.ToInt32(landBackWalk["samplesOnObstruction"]) == 0 || Convert.ToInt32(landBackWalk["samplesAtSea"]) != 0)
                failures.Add("Player did not walk back to town_1 across the open wall.");

            // Player ship: from town_1's gate, set sail at its port (the call the town's leave-by-sea menu makes),
            // sail to the nearer of town_2's waters and the other landmass's coast, then disembark there.
            if (hull != null)
            {
                var options = new List<Tuple<string, CampaignVec2, CampaignVec2>> {
                    Tuple.Create("town2", Toward(town2.PortPosition, points["openSea"], 2f, false), Toward(town2.GatePosition, town2.PortPosition, 1.5f, true)) };
                if (offLand != null) options.Add(Tuple.Create("otherLandmass", points["offLandmassSea"], points["offLandmassLand"]));
                var naval = navModel.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.Naval);
                Func<CampaignVec2, float> seaDistance = target => {
                    float d; var from = town1.PortPosition;
                    return map.GetPathDistanceBetweenAIFaces(from.Face, target.Face, from.ToVec2(), target.ToVec2(), navModel.GetEmbarkDisembarkThresholdDistance(),
                        Campaign.PathFindingMaxCostLimit, out d, naval, distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand) ? d : float.MaxValue;
                };
                var choice = options.OrderBy(o => seaDistance(o.Item2)).First();
                movement["playerVoyageDestination"] = choice.Item1;
                movement["playerVoyageOptions"] = options.ToDictionary(o => o.Item1, o => (object)seaDistance(o.Item2));
                ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, new Ship(hull));
                var before = main.Position;
                main.SetSailAtPosition(town1.PortPosition);
                movement["playerSetSail"] = new Dictionary<string, object> { ["atSea"] = main.IsCurrentlyAtSea, ["ships"] = main.Ships.Count,
                    ["from"] = new[] { before.X, before.Y }, ["port"] = new[] { town1.PortPosition.X, town1.PortPosition.Y },
                    ["placementDistance"] = before.ToVec2().Distance(town1.PortPosition.ToVec2()) };
                if (!main.IsCurrentlyAtSea) failures.Add("Player did not set sail at town_1's port.");
                var offshore = choice.Item2;
                main.SetMoveGoToPoint(offshore, MobileParty.NavigationType.Naval);
                var voyage = new Dictionary<string, object>(); movement["playerSea"] = voyage;
                var sail = Travel(context, boot, main, () => main.Position.ToVec2().Distance(offshore.ToVec2()) < 1f, 600, voyage);
                while (sail.MoveNext()) yield return sail.Current;
                if (!(bool)voyage["arrived"] || Convert.ToInt32(voyage["samplesOnLand"]) != 0) failures.Add("Player voyage did not stay at sea to its destination.");
                var ashore = choice.Item3;
                main.SetMoveGoToPoint(ashore, MobileParty.NavigationType.All);
                var landing = new Dictionary<string, object>(); movement["playerDisembark"] = landing;
                var land2 = Travel(context, boot, main, () => !main.IsCurrentlyAtSea && main.Position.ToVec2().Distance(ashore.ToVec2()) < 1f, 240, landing);
                while (land2.MoveNext()) yield return land2.Current;
                if (!(bool)landing["arrived"] || Convert.ToInt32(landing["transitions"]) < 1) failures.Add("Player did not disembark onto generated land.");
            }
            context.Checkpoint();
        }

        private static int Toggle(TaleWorlds.Engine.Scene scene, bool enabled)
        {
            int faces = scene.SetAbilityOfFacesWithId(ObstructionGroup, enabled);
            RefreshFaceCaches();
            return faces;
        }

        // Rebuilds every CampaignVec2 field of every party without its cached face record, so movement compares
        // freshly looked-up islands after a face-ability change.
        private static int RefreshFaceCaches()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var fields = typeof(MobileParty).GetFields(any).Where(f => f.FieldType == typeof(CampaignVec2)).ToArray();
            int parties = 0;
            foreach (var party in MobileParty.All)
            {
                foreach (var f in fields)
                {
                    var v = (CampaignVec2)f.GetValue(party);
                    f.SetValue(party, new CampaignVec2(v.ToVec2(), v.IsOnLand));
                }
                parties++;
            }
            return parties;
        }

        // The party's stored position and whether it carries a cached face record (CampaignVec2._faceCache),
        // next to a fresh lookup at the same point.
        private static Dictionary<string, object> FaceCache(MobileParty p)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object stored = typeof(MobileParty).GetField("_position", any).GetValue(p);
            var valid = typeof(CampaignVec2).GetField("_isPositionCacheValid", any).GetValue(stored);
            var cached = (PathFaceRecord)typeof(CampaignVec2).GetField("_faceCache", any).GetValue(stored);
            var position = (CampaignVec2)stored;
            var fresh = new CampaignVec2(position.ToVec2(), position.IsOnLand).Face;
            return new Dictionary<string, object> { ["position"] = new[] { position.X, position.Y }, ["onLand"] = position.IsOnLand,
                ["cacheValid"] = valid, ["cachedFace"] = cached.FaceIndex, ["cachedIsland"] = cached.FaceIslandIndex,
                ["freshFace"] = fresh.FaceIndex, ["freshIsland"] = fresh.FaceIslandIndex };
        }

        private static Dictionary<string, object> PartyState(MobileParty p)
        {
            return new Dictionary<string, object> {
                ["position"] = new[] { p.Position.X, p.Position.Y }, ["atSea"] = p.IsCurrentlyAtSea, ["active"] = p.IsActive,
                ["settlement"] = p.CurrentSettlement == null ? null : p.CurrentSettlement.StringId, ["mapEvent"] = p.MapEvent != null,
                ["besieging"] = p.BesiegedSettlement == null ? null : p.BesiegedSettlement.StringId, ["army"] = p.Army != null,
                ["attachedTo"] = p.AttachedTo == null ? null : p.AttachedTo.StringId,
                ["defaultBehavior"] = p.DefaultBehavior.ToString(), ["shortTermBehavior"] = p.ShortTermBehavior.ToString(),
                ["targetPosition"] = new[] { p.TargetPosition.X, p.TargetPosition.Y }, ["ships"] = p.Ships.Count,
                ["navigationCapability"] = p.NavigationCapability.ToString(), ["desiredAiNavigationType"] = p.DesiredAiNavigationType.ToString(),
                ["speed"] = p.Speed, ["men"] = p.MemberRoster.TotalManCount, ["doNotMakeNewDecisions"] = p.Ai.DoNotMakeNewDecisions,
                ["pathSize"] = p.Path.Size, ["transition"] = p.IsTransitionInProgress, ["raft"] = p.IsInRaftState };
        }

        // Movement-loop inputs read by MobileParty.TryToMoveThePartyWithCurrentTickMoveData and DoAiPathMode;
        // non-public members are read by reflection for diagnosis only.
        private static Dictionary<string, object> MovementInternals(MobileParty p)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Func<string, object> field = name => { var f = typeof(MobileParty).GetField(name, any); return f == null ? "<missing>" : f.GetValue(p); };
            Func<object, object> vec = v => v is CampaignVec2 ? (object)new object[] { ((CampaignVec2)v).X, ((CampaignVec2)v).Y, ((CampaignVec2)v).IsOnLand, ((CampaignVec2)v).Face.FaceIndex, ((CampaignVec2)v).Face.FaceIslandIndex } : v;
            var face = p.CurrentNavigationFace;
            var path = new List<float[]>();
            for (int i = 0; i < p.Path.Size && i < 12; i++) path.Add(new[] { p.Path.PathPoints[i].x, p.Path.PathPoints[i].y });
            return new Dictionary<string, object> {
                ["position"] = vec(p.Position), ["isMoving"] = p.IsMoving, ["faceValid"] = face.IsValid(), ["faceIndex"] = face.FaceIndex, ["faceIsland"] = face.FaceIslandIndex,
                ["moveTargetPoint"] = vec(field("MoveTargetPoint")), ["nextTargetPosition"] = vec(field("NextTargetPosition")),
                ["pathMode"] = field("_pathMode"), ["aiPathNotFound"] = field("_aiPathNotFound"), ["pathBegin"] = p.PathBegin, ["path"] = path,
                ["forceAiNoPathMode"] = p.ForceAiNoPathMode, ["bearing"] = new[] { p.Bearing.x, p.Bearing.y }, ["transition"] = p.IsTransitionInProgress,
                ["campaignDays"] = CampaignTime.Now.ToDays };
        }

        private sealed class PointComparer : IEqualityComparer<float[]>
        {
            public bool Equals(float[] a, float[] b) { return a.Length == b.Length && Math.Abs(a[0] - b[0]) < 0.01f && Math.Abs(a[1] - b[1]) < 0.01f; }
            public int GetHashCode(float[] a) { return 0; }
        }

        private static List<float[]> PathPoints(MobileParty party)
        {
            var result = new List<float[]>();
            for (int i = 0; i < party.Path.Size; i++) result.Add(new[] { party.Path.PathPoints[i].x, party.Path.PathPoints[i].y });
            return result;
        }

        // A point `offset` away from `target` toward `from`, on the requested region.
        private static CampaignVec2 Toward(CampaignVec2 target, CampaignVec2 from, float offset, bool land)
        {
            var t = target.ToVec2(); var d = from.ToVec2() - t;
            float length = d.Length;
            var p = length > 0.001f ? t + d * (offset / length) : t;
            return new CampaignVec2(p, land);
        }

        // Samples each segment every 0.25 units and classifies the land and sea faces underneath.
        private static Dictionary<string, object> SampleSegments(List<Vec2> points, int[] invalid)
        {
            int total = 0, offLand = 0, offSea = 0, obstruction = 0, lake = 0, transitions = 0; bool? wasLand = null;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                int steps = Math.Max(1, (int)Math.Ceiling(a.Distance(b) / 0.25f));
                for (int s = 0; s <= steps; s++)
                {
                    var p = a + (b - a) * ((float)s / steps);
                    var landFace = new CampaignVec2(p, true).Face; var seaFace = new CampaignVec2(p, false).Face;
                    bool onLand = landFace.IsValid() && !invalid.Contains(landFace.FaceGroupIndex) && landFace.FaceGroupIndex != LakeGroup;
                    bool onSea = seaFace.IsValid() && !invalid.Contains(seaFace.FaceGroupIndex);
                    total++;
                    if (!onLand) offLand++;
                    if (!onSea) offSea++;
                    if (landFace.IsValid() && landFace.FaceGroupIndex == ObstructionGroup) obstruction++;
                    if (seaFace.IsValid() && seaFace.FaceGroupIndex == LakeGroup) lake++;
                    bool land = onLand && !onSea ? true : !onLand && onSea ? false : wasLand ?? onLand;
                    if (wasLand.HasValue && land != wasLand.Value) transitions++;
                    wasLand = land;
                }
            }
            return new Dictionary<string, object> { ["samples"] = total, ["offLand"] = offLand, ["offSea"] = offSea,
                ["obstruction"] = obstruction, ["lake"] = lake, ["transitions"] = transitions };
        }

        // Runs campaign time until `done` or timeout, sampling the party's actual position and face.
        private static IEnumerator Travel(ProbeContext context, DiagnosticSandboxBootstrap boot, MobileParty party, Func<bool> done,
            double timeoutSeconds, Dictionary<string, object> record, bool deepTrace = false)
        {
            var movementTrace = new List<object>();
            var transitionWaits = new List<Dictionary<string, object>>(); record["transitionWaits"] = transitionWaits;
            if (deepTrace) record["movementTrace"] = movementTrace;
            var campaign = Campaign.Current;
            var start = party.Position; double begin = context.Seconds, lastSample = 0, lastProgress = context.Seconds;
            int samples = 0, atSea = 0, onLand = 0, onObstruction = 0, transitions = 0; bool wasAtSea = party.IsCurrentlyAtSea;
            float traveled = 0; var last = party.Position.ToVec2(); double startDays = CampaignTime.Now.ToDays;
            var track = new List<float[]>();
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            bool arrived = false;
            while (context.Seconds - begin < timeoutSeconds)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                var position = party.Position.ToVec2();
                if (position.Distance(last) > 0.01f) { traveled += position.Distance(last); last = position; lastProgress = context.Seconds; }
                if (party.IsCurrentlyAtSea != wasAtSea) { transitions++; wasAtSea = party.IsCurrentlyAtSea; }
                if (context.Seconds - lastSample >= 0.1)
                {
                    lastSample = context.Seconds; samples++;
                    var f = party.CurrentNavigationFace;
                    if (party.IsCurrentlyAtSea) atSea++; else onLand++;
                    if (!party.IsCurrentlyAtSea && f.IsValid() && f.FaceGroupIndex == ObstructionGroup) onObstruction++;
                    if (track.Count < 2000) track.Add(new[] { position.x, position.y, party.IsCurrentlyAtSea ? 1f : 0f });
                    if (deepTrace && movementTrace.Count < 40) movementTrace.Add(MovementInternals(party));
                }
                if (!record.ContainsKey("stateAfter2s") && context.Seconds - begin >= 2) record["stateAfter2s"] = PartyState(party);
                if (done()) { arrived = true; break; }
                if (party.IsTransitionInProgress)
                {
                    lastProgress = context.Seconds;
                    if (!transitionWaits.Any() || !(bool)transitionWaits.Last()["open"])
                        transitionWaits.Add(new Dictionary<string, object> { ["open"] = true, ["atSea"] = party.IsCurrentlyAtSea,
                            ["anchorValid"] = party.Anchor != null && party.Anchor.IsValid, ["durationHours"] = party.NavigationTransitionDuration.ToHours,
                            ["startedDays"] = party.NavigationTransitionStartTime.ToDays });
                }
                else if (transitionWaits.Any() && (bool)transitionWaits.Last()["open"]) transitionWaits.Last()["open"] = false;
                if (context.Seconds - lastProgress > 30) break;  // stuck: record and stop
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            record["stateAtEnd"] = PartyState(party);
            record["arrived"] = arrived; record["realSeconds"] = context.Seconds - begin; record["campaignDays"] = CampaignTime.Now.ToDays - startDays;
            record["start"] = new[] { start.X, start.Y }; record["end"] = new[] { party.Position.X, party.Position.Y };
            record["traveled"] = traveled; record["samples"] = samples; record["samplesAtSea"] = atSea; record["samplesOnLand"] = onLand;
            record["samplesOnObstruction"] = onObstruction; record["transitions"] = transitions; record["endsAtSea"] = party.IsCurrentlyAtSea;
            record["track"] = track;
            context.Checkpoint();
        }
    }
}
