using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Helpers;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-63: travel, routing and strategic distance across the east/west seam of a periodic generated world
    // (fixture tools/scenebin/build_periodic_world.py). Routes are the installed native path queries through the owned
    // IMapScene, which answers with the target's nearest periodic image; parties are kept canonical by
    // PeriodicSeamBehavior. Every movement below is real party movement under campaign time.
    internal static class WorldWrapProbe
    {
        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var world = PeriodicWorld.Current;
            if (world == null) { failures.Add("No periodic world is active (placement lacks 'periodic')."); yield break; }
            var campaign = Campaign.Current;
            var map = (GeneratedMapScene)campaign.MapSceneWrapper;
            var nav = campaign.Models.PartyNavigationModel;
            var distanceModel = campaign.Models.MapDistanceModel;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            report["geometry"] = new Dictionary<string, object> {
                ["width"] = world.Width, ["originX"] = world.OriginX, ["extent"] = world.Extent,
                ["canonicalRange"] = new[] { world.OriginX, world.MaxX },
                ["settlements"] = Settlement.All.Select(s => new Dictionary<string, object> { ["id"] = s.StringId, ["x"] = s.Position.X, ["y"] = s.Position.Y,
                    ["logicalX"] = world.LogicalX(s.Position.X), ["canonical"] = world.IsCanonical(s.Position.X) }).ToList(),
                ["seamBehaviorRegistered"] = campaign.CampaignBehaviorManager.GetBehavior<PeriodicSeamBehavior>() != null };
            if (!world.IsCanonical(town1.Position.X) || !world.IsCanonical(town2.Position.X)) failures.Add("Settlements are not canonical.");
            // Display copies: visual-only settlement copies in the strips, scene entities with no campaign identity, so every settlement
            // stays one logical object. MMR-66 makes them at run time (PeriodicMapPresentation, replacing the fixture's static ones); a
            // fixture copy still in the scene is listed too.
            var copies = new List<object>();
            foreach (var c in MNBL.ProceduralWorld.World.PeriodicMapPresentation.Copies)
            {
                var s = Settlement.Find(c.Settlement);
                var at = c.Copy.GlobalPosition;
                copies.Add(new Dictionary<string, object> { ["entity"] = c.Entity, ["of"] = c.Settlement, ["position"] = new[] { at.x, at.y }, ["offset"] = s == null ? (object)null : at.x - s.Position.X,
                    ["runtime"] = true, ["replacedFixtureEntity"] = c.ReplacedFixtureEntity });
            }
            foreach (var s in Settlement.All.Where(s => s.StringId.StartsWith(worldId + "_", StringComparison.Ordinal)))
                foreach (var side in new[] { "west", "east" })
                {
                    var entity = map.Scene.FindEntityWithName(worldId + "_display_" + s.StringId.Substring(worldId.Length + 1) + "_" + side);
                    if (entity == null) continue;
                    var at = entity.GlobalPosition;
                    copies.Add(new Dictionary<string, object> { ["entity"] = entity.Name, ["of"] = s.StringId, ["position"] = new[] { at.x, at.y }, ["offset"] = at.x - s.Position.X, ["runtime"] = false });
                }
            int namedDisplay = Settlement.All.Count(s => s.StringId.Contains("_display_")) + MobileParty.All.Count(p => p.StringId.Contains("_display_"));
            bool uniqueIds = Settlement.All.Select(s => s.StringId).Distinct().Count() == Settlement.All.Count;
            report["displayCopies"] = new Dictionary<string, object> { ["entities"] = copies, ["campaignObjectsNamedDisplay"] = namedDisplay, ["settlementIdsUnique"] = uniqueIds };
            if (copies.Count == 0) failures.Add("No display copies found in the scene.");
            if (namedDisplay != 0 || !uniqueIds) failures.Add("A display copy has a campaign identity.");

            // ---- strategic distance: periodic (owned IMapScene) against the unmodified native query ----
            var routes = new List<object>(); report["routes"] = routes;
            Func<string, CampaignVec2, CampaignVec2, MobileParty.NavigationType, Dictionary<string, object>> route = (name, from, to, type) => {
                var invalid = nav.GetInvalidTerrainTypesForNavigationType(type);
                float periodic, planar;
                bool p = ((TaleWorlds.CampaignSystem.Map.IMapScene)map).GetPathDistanceBetweenAIFaces(from.Face, to.Face, from.ToVec2(), to.ToVec2(),
                    nav.GetEmbarkDisembarkThresholdDistance(), Campaign.PathFindingMaxCostLimit, out periodic, invalid,
                    distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                bool q = map.PlanarPathDistance(from.Face, to.Face, from.ToVec2(), to.ToVec2(), nav.GetEmbarkDisembarkThresholdDistance(),
                    Campaign.PathFindingMaxCostLimit, out planar, invalid, distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                var row = new Dictionary<string, object> { ["name"] = name, ["type"] = type.ToString(), ["from"] = Xy(from), ["to"] = Xy(to),
                    ["periodicFound"] = p, ["periodicDistance"] = p ? (object)periodic : null, ["planarFound"] = q, ["planarDistance"] = q ? (object)planar : null,
                    ["straightPeriodic"] = world.Distance(from.ToVec2(), to.ToVec2()), ["straightPlanar"] = from.ToVec2().Distance(to.ToVec2()) };
                routes.Add(row);
                return row;
            };
            var land12 = route("land town1->town2", town1.GatePosition, town2.GatePosition, MobileParty.NavigationType.Default);
            var land21 = route("land town2->town1", town2.GatePosition, town1.GatePosition, MobileParty.NavigationType.Default);
            var sea12 = route("sea port1->port2", town1.PortPosition, town2.PortPosition, MobileParty.NavigationType.Naval);
            var sea21 = route("sea port2->port1", town2.PortPosition, town1.PortPosition, MobileParty.NavigationType.Naval);
            foreach (var r in new[] { land12, land21, sea12, sea21 })
            {
                if (!(bool)r["periodicFound"]) { failures.Add("No periodic route: " + r["name"]); continue; }
                double periodic = Convert.ToDouble(r["periodicDistance"]);
                if (periodic > 3 * Convert.ToDouble(r["straightPeriodic"]) + 30) failures.Add("Periodic route is not the short seam route: " + r["name"]);
                if ((bool)r["planarFound"] && Convert.ToDouble(r["planarDistance"]) < periodic - 1e-3) failures.Add("Periodic route longer than the planar one: " + r["name"]);
            }
            float d12 = distanceModel.GetDistance(town1, town2, false, false, MobileParty.NavigationType.Default);
            float s12 = distanceModel.GetDistance(town1, town2, true, true, MobileParty.NavigationType.Naval);
            report["distanceModel"] = new Dictionary<string, object> { ["type"] = distanceModel.GetType().FullName,
                ["town1ToTown2Land"] = d12, ["town1ToTown2PortToPort"] = s12, ["planarSettlementDistance"] = town1.Position.ToVec2().Distance(town2.Position.ToVec2()) };
            if (d12 > 3 * world.Distance(town1.Position.ToVec2(), town2.Position.ToVec2()) + 30) failures.Add("Distance model does not use the periodic route between the seam towns.");

            // Deterministic tie: a target exactly Width/2 east of the start has two equal images (as given, and -Width).
            report["tie"] = Tie(map, world, nav, distanceModel, failures);
            context.Checkpoint();

            // ---- MMR-64: the coordinate contract and the whole installed distance surface ----
            report["coordinates"] = world.Coordinates.Record(); report["coordinatePolicyText"] = world.Coordinates.PolicyText;
            var coverage = new Dictionary<string, object>(); report["distanceCoverage"] = coverage;
            try { DistanceCoverage(map, world, nav, distanceModel, town1, town2, worldId, coverage); }
            catch (Exception e) { coverage["error"] = e.ToString(); failures.Add("distance coverage: " + e.Message); }
            if (coverage.ContainsKey("failures")) failures.AddRange(((List<string>)coverage["failures"]).Select(f => "distance coverage: " + f));
            context.Checkpoint();

            // ---- player and AI travel across the seam, both directions, land and sea ----
            var main = MobileParty.MainParty;
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            main.Position = town1.GatePosition; main.SetMoveModeHold();
            var travels = new List<object>(); report["travel"] = travels;
            bool travel = context.Int("wrapTravel", 1) != 0;  // 0 skips the travel legs (development reruns of the later phases only)
            report["travelLegsRun"] = travel;
            if (travel)
            foreach (var leg in new[] { Tuple.Create("player land east->west", town1, town2), Tuple.Create("player land west->east", town2, town1) })
            {
                var record = new Dictionary<string, object> { ["leg"] = leg.Item1, ["party"] = main.StringId }; travels.Add(record);
                main.SetMoveGoToPoint(leg.Item3.GatePosition, MobileParty.NavigationType.Default);
                var target = leg.Item3.GatePosition.ToVec2();
                var steps = Travel(context, boot, world, main, () => world.Distance(main.Position.ToVec2(), target) < 1.5f, 120, record);
                while (steps.MoveNext()) yield return steps.Current;
                Judge(record, failures, world, leg.Item2.GatePosition.ToVec2(), target);
            }

            var ai = MobileParty.All.FirstOrDefault(p => p.StringId == worldId + "_ruler_1_party_1") ?? MobileParty.All.FirstOrDefault(p => p.IsLordParty && p != main);
            if (!travel) { }
            else if (ai == null) failures.Add("No AI lord party.");
            else
            {
                if (ai.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(ai);
                ai.Ai.SetDoNotMakeNewDecisions(true);
                ai.Position = town1.GatePosition;  // synthetic: start the AI at the east seam town
                foreach (var leg in new[] { Tuple.Create("ai land east->west", town1, town2), Tuple.Create("ai land west->east", town2, town1) })
                {
                    var record = new Dictionary<string, object> { ["leg"] = leg.Item1, ["party"] = ai.StringId }; travels.Add(record);
                    ai.SetMoveGoToPoint(leg.Item3.GatePosition, MobileParty.NavigationType.Default);
                    var target = leg.Item3.GatePosition.ToVec2();
                    var steps = Travel(context, boot, world, ai, () => world.Distance(ai.Position.ToVec2(), target) < 1.5f, 120, record);
                    while (steps.MoveNext()) yield return steps.Current;
                    Judge(record, failures, world, leg.Item2.GatePosition.ToVec2(), target);
                }
                ai.Ai.SetDoNotMakeNewDecisions(false);
            }

            var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { town1.Culture }).Where(c => c != null).SelectMany(c => c.AvailableShipHulls).FirstOrDefault();
            if (!travel) { }
            else if (hull == null) failures.Add("No ship hull.");
            else
            {
                if (main.Ships.Count == 0)
                {
                    int crew = Math.Max(0, new Ship(hull).SkeletalCrewCapacity - main.MemberRoster.TotalHealthyCount);
                    var troop = Hero.MainHero.Culture.BasicTroop;
                    if (crew > 0) main.MemberRoster.AddToCounts(troop, crew, false, 0, 0, true, -1);
                    ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, new Ship(hull));
                }
                main.SetSailAtPosition(town1.PortPosition); main.SetMoveModeHold();
                foreach (var leg in new[] { Tuple.Create("player sea east->west", town1, town2), Tuple.Create("player sea west->east", town2, town1) })
                {
                    var record = new Dictionary<string, object> { ["leg"] = leg.Item1, ["party"] = main.StringId }; travels.Add(record);
                    main.SetMoveGoToPoint(leg.Item3.PortPosition, MobileParty.NavigationType.Naval);
                    var target = leg.Item3.PortPosition.ToVec2();
                    var steps = Travel(context, boot, world, main, () => world.Distance(main.Position.ToVec2(), target) < 2.5f, 180, record);
                    while (steps.MoveNext()) yield return steps.Current;
                    Judge(record, failures, world, leg.Item2.PortPosition.ToVec2(), target);
                    if (!main.IsCurrentlyAtSea) failures.Add(leg.Item1 + ": the player left the sea.");
                }
            }

            // Caravans are excluded: CaravansCampaignBehavior re-issues their orders regardless of SetDoNotMakeNewDecisions.
            var navalAi = MobileParty.All.Where(p => p != main && p.IsActive && !p.IsCaravan && p.Ships.Count > 0 && p.CurrentSettlement == null && p.MapEvent == null)
                .OrderBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (!travel) { }
            else if (navalAi == null) failures.Add("No AI party with ships.");
            else
            {
                navalAi.Ai.SetDoNotMakeNewDecisions(true);
                navalAi.SetSailAtPosition(town1.PortPosition);  // synthetic: start at the east seam port
                foreach (var leg in new[] { Tuple.Create("ai sea east->west", town1, town2), Tuple.Create("ai sea west->east", town2, town1) })
                {
                    var record = new Dictionary<string, object> { ["leg"] = leg.Item1, ["party"] = navalAi.StringId }; travels.Add(record);
                    navalAi.SetMoveGoToPoint(leg.Item3.PortPosition, MobileParty.NavigationType.Naval);
                    var target = leg.Item3.PortPosition.ToVec2();
                    var steps = Travel(context, boot, world, navalAi, () => world.Distance(navalAi.Position.ToVec2(), target) < 2.5f, 180, record);
                    while (steps.MoveNext()) yield return steps.Current;
                    Judge(record, failures, world, leg.Item2.PortPosition.ToVec2(), target);
                }
                navalAi.Ai.SetDoNotMakeNewDecisions(false);
            }
            // ---- phase 2: proximity, visibility and an engine-triggered encounter across the seam ----
            var seam = new Dictionary<string, object>(); report["seamEncounter"] = seam;
            var steps2 = SeamEncounter(context, boot, world, worldId, town1, seam);
            Exception seamError = null;
            while (true)
            {
                try { if (!steps2.MoveNext()) break; } catch (Exception e) { seamError = e; break; }
                yield return steps2.Current;
            }
            if (seamError != null) { seam["error"] = seamError.ToString(); failures.Add("seam encounter: " + seamError.Message); }
            if (seam.ContainsKey("failures")) failures.AddRange(((List<string>)seam["failures"]).Select(f => "seam encounter: " + f));
            // ---- phase 3: a strip click through the official travel order, then one save near the seam ----
            foreach (var part in new[] { "picking", "seamSave" })
            {
                var row3 = new Dictionary<string, object>(); report[part] = row3;
                var steps3 = part == "picking" ? Picking(context, boot, world, row3) : SeamSave(context, boot, world, row3);
                Exception error3 = null;
                while (true)
                {
                    try { if (!steps3.MoveNext()) break; } catch (Exception e) { error3 = e; break; }
                    yield return steps3.Current;
                }
                if (error3 != null) { row3["error"] = error3.ToString(); failures.Add(part + ": " + error3.Message); }
                if (row3.ContainsKey("failures")) failures.AddRange(((List<string>)row3["failures"]).Select(f => part + ": " + f));
            }
            report["canonicalPicks"] = PeriodicPatches.CanonicalPicks;
            report["canonicalPlacements"] = PeriodicPatches.CanonicalPlacements;
            report["patches"] = PeriodicPatches.Applied;
            report["patchError"] = PeriodicPatches.LastError;
            report["locatorImagePasses"] = PeriodicPatches.ImagePasses;
            if (PeriodicPatches.LastError != null) failures.Add("Periodic patches failed to apply: " + PeriodicPatches.LastError.Split('\n')[0]);
            report["periodicQueries"] = GeneratedMapScene.PeriodicQueries;
            report["periodicImageWins"] = GeneratedMapScene.PeriodicImageWins;
            report["periodicNativeQueries"] = GeneratedMapScene.PeriodicNativeQueries;
            report["periodicPruned"] = GeneratedMapScene.PeriodicPruned;
            report["periodicStartMoves"] = GeneratedMapScene.PeriodicStartMoves;
            report["rebases"] = PeriodicSeam.Rebases;
            report["targetsCanonicalized"] = PeriodicSeam.TargetsCanonicalized;
            report["nonCanonicalPartiesAtEnd"] = MobileParty.All.Where(p => p.IsActive && !world.IsCanonical(p.Position.X)).Select(p => p.StringId).ToList();
            context.Checkpoint();
        }

        // Two parties on the seam-crossing continent, a fraction of a unit apart through the seam and ~Width apart planar.
        // Proximity (LocatorGrid), visibility and interaction range (CanPartyInteract) must see each other; then the player
        // intercepts the held hostile party (the click-on-party order) and the engine's own interaction check starts the
        // encounter while the parties are still on opposite sides.
        private static IEnumerator SeamEncounter(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, string worldId,
            Settlement town1, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            var main = MobileParty.MainParty;
            RuntimeMissionProbe.Pause();
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (main.IsCurrentlyAtSea) main.DisembarkToPosition(town1.GatePosition);  // the official disembark (game menus use it) after the naval legs
            // After days of fast-forwarded travel the party may not be fit to fight (the attack option needs an unwounded hero and
            // troops on both sides). Record the state, then heal the hero and wounded troops (synthetic).
            row["playerStateBeforeBattle"] = new Dictionary<string, object> { ["heroHitPoints"] = Hero.MainHero.HitPoints, ["heroWounded"] = Hero.MainHero.IsWounded,
                ["healthy"] = main.MemberRoster.TotalHealthyCount, ["wounded"] = main.MemberRoster.TotalWounded, ["food"] = main.Food, ["raft"] = main.IsInRaftState, ["ships"] = main.Ships.Count };
            Hero.MainHero.HitPoints = Hero.MainHero.MaxHitPoints;
            for (int i = 0; i < main.MemberRoster.Count; i++)
            {
                int wounded = main.MemberRoster.GetElementWoundedNumber(i);
                if (wounded > 0) main.MemberRoster.AddToCountsAtIndex(i, 0, -wounded, 0, false);
            }
            // A row where both seam edges are land.
            float eastX = world.MaxX - 0.2f, westX = world.OriginX + 0.1f, y = float.NaN;
            for (float dy = 0; dy < 40 && float.IsNaN(y); dy += 0.5f)
                foreach (float cy in new[] { town1.Position.Y + dy, town1.Position.Y - dy })
                {
                    var e = new CampaignVec2(new Vec2(eastX, cy), true); var w = new CampaignVec2(new Vec2(westX, cy), true);
                    if (e.Face.IsValid() && w.Face.IsValid()) { y = cy; break; }
                }
            if (float.IsNaN(y)) { failures.Add("No land row crosses the seam near town 1."); yield break; }
            var enemyFaction = Kingdom.All.FirstOrDefault(k => k != town1.MapFaction && !k.IsEliminated) ?? Kingdom.All.First();
            if (!enemyFaction.IsAtWarWith(main.MapFaction)) DeclareWarAction.ApplyByDefault(Clan.PlayerClan, enemyFaction);
            var enemy = MobileParty.All.Where(p => p != main && p.IsActive && p.MapEvent == null && p.CurrentSettlement == null && p.Army == null && !p.IsGarrison && !p.IsMilitia
                    && p.MapFaction != null && p.MapFaction.IsAtWarWith(main.MapFaction) && p.Ships.Count == 0 && p.MemberRoster.TotalHealthyCount > 0)
                .OrderBy(p => p.MemberRoster.TotalManCount).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (enemy == null) { failures.Add("No hostile land party."); yield break; }
            main.Position = new CampaignVec2(new Vec2(eastX, y), true); main.SetMoveModeHold();
            enemy.Position = new CampaignVec2(new Vec2(westX, y), true); enemy.SetMoveModeHold();
            enemy.Ai.SetDoNotMakeNewDecisions(true);
            row["setup"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["enemy"] = enemy.StringId, ["enemyPosition"] = Xy(enemy.Position),
                ["periodicDistance"] = world.Distance(main.Position.ToVec2(), enemy.Position.ToVec2()), ["planarDistance"] = main.Position.ToVec2().Distance(enemy.Position.ToVec2()),
                ["playerAtSea"] = main.IsCurrentlyAtSea, ["enemyAtSea"] = enemy.IsCurrentlyAtSea,
                ["synthetic"] = "war declared on the kingdom not owning town 1; player and an existing hostile party placed either side of the seam" };

            // Proximity through the real party locator.
            var found = new List<string>();
            var data = MobileParty.StartFindingLocatablesAroundPosition(main.Position.ToVec2(), 5f);
            for (var p = MobileParty.FindNextLocatable(ref data); p != null; p = MobileParty.FindNextLocatable(ref data)) found.Add(p.StringId);
            row["locatorAroundPlayer"] = found;
            // Strategic distance from a party across the seam (the distance model's party legs).
            var model = Campaign.Current.Models.MapDistanceModel;
            float landRatio;
            float partyToParty = model.GetDistance(main, enemy, MobileParty.NavigationType.Default, out landRatio);
            var westTown = Settlement.All.Where(s => s.IsTown && world.LogicalX(s.Position.X) < world.Width / 4).OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            float partyToTown = westTown == null ? float.NaN : model.GetDistance(main, westTown, false, MobileParty.NavigationType.Default, out landRatio);
            row["distanceModelFromPlayer"] = new Dictionary<string, object> { ["toEnemy"] = partyToParty, ["toWestTown"] = partyToTown, ["westTown"] = westTown == null ? null : westTown.StringId,
                ["westTownPeriodicStraight"] = westTown == null ? float.NaN : world.Distance(main.Position.ToVec2(), westTown.GatePosition.ToVec2()),
                ["westTownPlanarStraight"] = westTown == null ? float.NaN : main.Position.ToVec2().Distance(westTown.GatePosition.ToVec2()) };
            if (!(partyToParty < 5)) failures.Add("The distance model measures " + partyToParty + " from the player to the hostile party across the seam.");
            if (!(partyToTown < world.Width / 4)) failures.Add("The distance model measures " + partyToTown + " from the player to the west town across the seam.");
            if (!found.Contains(enemy.StringId)) failures.Add("Party locator does not find the hostile party across the seam.");

            // Visibility after a second of campaign ticks (the engine's main-party visibility pass).
            var campaign = Campaign.Current;
            double hours = CampaignTime.Now.ToHours;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
            double start = context.Seconds;
            while (context.Seconds - start < 1) { boot.Tick(); yield return null; }
            DiagnosticSandboxBootstrap.Pause(campaign);
            row["visibilityWindowCampaignHours"] = CampaignTime.Now.ToHours - hours;
            row["enemyVisible"] = enemy.IsVisible;
            if (!enemy.IsVisible)
            {
                failures.Add("The hostile party is not visible to the player across the seam.");
                enemy.Party.UpdateVisibilityAndInspected(main.Position, 0f);  // diagnosis only: the range check without the engine's pass
                row["enemyVisibleAfterDirectRangeCheck"] = enemy.IsVisible;
                row["seeingRange"] = main.SeeingRange;
            }
            // The player intercepts; the target (the enemy's PartyBase) measures the player against the point it is heading for.
            main.SetMoveEngageParty(enemy, MobileParty.NavigationType.Default);
            bool canInteract = ((TaleWorlds.CampaignSystem.Map.IInteractablePoint)enemy.Party).CanPartyInteract(main, 0.1f);
            row["canPartyInteract"] = canInteract;
            if (!canInteract) failures.Add("CanPartyInteract is false across the seam.");
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
            start = context.Seconds;
            while (PlayerEncounter.Current == null && context.Seconds - start < 30)
            {
                boot.Tick();
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop && PlayerEncounter.Current == null) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            if (PlayerEncounter.Current == null)
            {
                row["noEncounter"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["enemy"] = Xy(enemy.Position), ["playerAtSea"] = main.IsCurrentlyAtSea,
                    ["enemyAtSea"] = enemy.IsCurrentlyAtSea, ["playerBehavior"] = main.DefaultBehavior.ToString(), ["playerTarget"] = main.TargetParty == null ? null : main.TargetParty.StringId,
                    ["periodicDistance"] = world.Distance(main.Position.ToVec2(), enemy.Position.ToVec2()) };
                failures.Add("No encounter started within 30 s."); EndWar(enemy, enemyFaction); yield break;
            }
            row["encounterStart"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["enemy"] = Xy(enemy.Position),
                ["periodicDistance"] = world.Distance(main.Position.ToVec2(), enemy.Position.ToVec2()), ["planarDistance"] = main.Position.ToVec2().Distance(enemy.Position.ToVec2()),
                ["oppositeSides"] = Math.Abs(main.Position.X - enemy.Position.X) > world.Width / 2 };
            var before = main.Position;

            // The engine continues with its own meeting (a map conversation with the enemy leader). Close it, then enter the
            // battle at the same seam positions through the official encounter menu, as the accepted MMR-60 probe does.
            var conversations = Campaign.Current.ConversationManager;
            start = context.Seconds;
            while (context.Seconds - start < 10 && !conversations.IsConversationInProgress && MenuId() == null) yield return null;
            row["engineEncounterContinuation"] = conversations.IsConversationInProgress ? "map conversation" : (MenuId() ?? "none within 10 s");
            if (conversations.IsConversationInProgress) conversations.EndConversation();
            start = context.Seconds;
            while (conversations.IsConversationInProgress && context.Seconds - start < 10) yield return null;
            row["afterMeeting"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["enemy"] = Xy(enemy.Position), ["playerMapEvent"] = main.MapEvent != null,
                ["encounter"] = PlayerEncounter.Current == null ? null : PlayerEncounter.Current.EncounterState.ToString(), ["menu"] = MenuId() };
            // After the meeting the engine starts its own battle and opens the encounter menu (recorded in afterMeeting).
            // The probe enters through the accepted MMR-60 entry (restart, StartBattle, encounter menu) at the same positions.
            row["battleStartedBy"] = "PlayerEncounter.RestartPlayerEncounter + StartBattle (the MMR-60 entry)";
            PlayerEncounter.RestartPlayerEncounter(enemy.Party, main.Party, true, false);
            var battle = PlayerEncounter.StartBattle();
            if (battle == null) { failures.Add("StartBattle returned no map event."); EndWar(enemy, enemyFaction); yield break; }
            var attackerAt = battle.AttackerSide.LeaderParty.Position.ToVec2();
            var defenderAt = battle.DefenderSide.LeaderParty.Position.ToVec2();
            before = main.Position;
            row["battleStart"] = new Dictionary<string, object> { ["parties"] = battle.InvolvedParties.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToList(),
                ["attacker"] = battle.AttackerSide.LeaderParty.MobileParty == null ? null : battle.AttackerSide.LeaderParty.MobileParty.StringId,
                ["attackerPosition"] = new[] { attackerAt.X, attackerAt.Y }, ["defenderPosition"] = new[] { defenderAt.X, defenderAt.Y },
                ["mapEventPosition"] = Xy(battle.Position), ["mapEventCanonical"] = world.IsCanonical(battle.Position.X),
                ["mapEventDistanceToPlayer"] = world.Distance(battle.Position.ToVec2(), main.Position.ToVec2()),
                ["periodicDistance"] = world.Distance(attackerAt, defenderAt), ["planarDistance"] = attackerAt.Distance(defenderAt) };
            if (world.Distance(battle.Position.ToVec2(), main.Position.ToVec2()) > 2) failures.Add("Map event position is away from the parties.");
            GameMenu.ActivateGameMenu("encounter");
            var wait = RuntimeMissionProbe.WaitMenu(context, "encounter");
            while (wait.MoveNext()) yield return wait.Current;
            try { RuntimeMissionProbe.RunOption("attack"); }
            catch (InvalidOperationException)
            {
                var menuContext = ((TaleWorlds.CampaignSystem.GameState.MapState)TaleWorlds.Core.Game.Current.GameStateManager.ActiveState).MenuContext;
                var option = menuContext.GameMenu.MenuOptions.FirstOrDefault(o => o.IdString == "attack");
                bool holds = option != null && option.GetConditionsHold(TaleWorlds.Core.Game.Current, menuContext);
                row["attackUnavailable"] = new Dictionary<string, object> { ["conditionsHold"] = holds, ["enabled"] = option != null && option.IsEnabled,
                    ["tooltip"] = option == null || option.Tooltip == null ? null : option.Tooltip.ToString(), ["heroWounded"] = Hero.MainHero.IsWounded,
                    ["troopsOnBothSides"] = battle.HasTroopsOnBothSides(), ["raft"] = main.IsInRaftState, ["healthy"] = main.MemberRoster.TotalHealthyCount,
                    ["enemyHealthy"] = enemy.MemberRoster.TotalHealthyCount };
                EndWar(enemy, enemyFaction);
                throw;
            }
            var mission = new Dictionary<string, object>(); row["mission"] = mission;
            var observe = RuntimeMissionProbe.ObserveMission(context, "seam", true, false, 0, mission);
            while (observe.MoveNext()) yield return observe.Current;
            // The battle's encounter direction (attacker - defender leader positions) must point the short way through the seam.
            var expected = PeriodicPatches.PeriodicDelta(attackerAt, defenderAt); expected.Normalize();
            var planar = attackerAt - defenderAt; planar.Normalize();
            var observed = mission.ContainsKey("mission") ? (Dictionary<string, object>)mission["mission"] : mission;
            var record = observed.ContainsKey("initializerRecord") ? (Dictionary<string, object>)observed["initializerRecord"] : null;
            var dir = record != null && record.ContainsKey("PatchEncounterDir") ? record["PatchEncounterDir"] as float[] : null;
            row["encounterDirection"] = new Dictionary<string, object> { ["patchEncounterDir"] = dir, ["expectedPeriodic"] = new[] { expected.X, expected.Y }, ["planar"] = new[] { planar.X, planar.Y } };
            if (dir == null || dir[0] * expected.X + dir[1] * expected.Y < 0.9f) failures.Add("The battle's encounter direction does not point through the seam.");
            // MMR-42: canonical patch sampling at the seam: the patch at the main party and at its other strip image are the same cell, and the scene
            // the model opened is the generated patch's (the mismatch guard's record of this choice agrees).
            var wrapper = Campaign.Current.MapSceneWrapper;
            var image = new CampaignVec2(new Vec2(before.X + (before.X > world.OriginX + world.Width / 2 ? -world.Width : world.Width), before.Y), before.IsOnLand);
            var patchHere = wrapper.GetMapPatchAtPosition(in before); var patchImage = wrapper.GetMapPatchAtPosition(in image);
            var guardRow = observed.ContainsKey("scene") ? MNBL.ProceduralWorld.Missions.MissionScenePatches.LastOpening(observed["scene"] as string) : null;
            row["missionScene"] = new Dictionary<string, object> { ["scene"] = observed.ContainsKey("scene") ? observed["scene"] : null,
                ["patch"] = new Dictionary<string, object> { ["position"] = Xy(before), ["index"] = patchHere.sceneIndex, ["coordinates"] = new[] { patchHere.normalizedCoordinates.X, patchHere.normalizedCoordinates.Y } },
                ["imagePatch"] = new Dictionary<string, object> { ["position"] = Xy(image), ["index"] = patchImage.sceneIndex, ["coordinates"] = new[] { patchImage.normalizedCoordinates.X, patchImage.normalizedCoordinates.Y } },
                ["guard"] = guardRow };
            if (patchHere.sceneIndex != patchImage.sceneIndex || patchHere.normalizedCoordinates.X != patchImage.normalizedCoordinates.X || patchHere.normalizedCoordinates.Y != patchImage.normalizedCoordinates.Y)
                failures.Add("The battle-map patch at the seam point differs from the patch at its other image.");
            if (guardRow == null) failures.Add("The battle scene guard did not observe the seam battle's scene choice.");
            else if (!Equals(guardRow["agrees"], true)) failures.Add("The seam battle's scene is not the generated patch's: " + guardRow["problem"]);
            if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
            start = context.Seconds;
            while ((PlayerEncounter.Current != null || main.MapEvent != null || !(TaleWorlds.Core.Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState)) && context.Seconds - start < 30)
                yield return null;
            RuntimeMissionProbe.Pause();
            // MMR-66: the map camera after the return (the map screen re-activates; a second for its camera to settle on the party).
            for (int i = 0; i < 60; i++) yield return null;
            Dictionary<string, object> camera = null;
            if (MNBL.ProceduralWorld.UI.PeriodicMapCameraView.Current != null)
            {
                var t = MNBL.ProceduralWorld.UI.PeriodicMapCameraView.Target();
                var drawn = MNBL.ProceduralWorld.World.PeriodicMapPresentation.DrawnPosition(main);   // the party's drawn entity
                camera = new Dictionary<string, object> { ["target"] = new[] { t.x, t.y }, ["targetCanonical"] = world.IsCanonical(t.x),
                    ["partyEntity"] = drawn == null ? null : new[] { drawn.Value.x, drawn.Value.y },
                    ["distanceToPartyDrawn"] = drawn == null ? float.MaxValue : new Vec2(t.x - drawn.Value.x, t.y - drawn.Value.y).Length,
                    ["followMode"] = MNBL.ProceduralWorld.UI.PeriodicMapCameraView.FollowMode() };
            }
            row["afterReturn"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["canonical"] = world.IsCanonical(main.Position.X),
                ["displacement"] = world.Distance(before.ToVec2(), main.Position.ToVec2()), ["camera"] = camera };
            if (!world.IsCanonical(main.Position.X)) failures.Add("Player not canonical after the seam battle.");
            if (world.Distance(before.ToVec2(), main.Position.ToVec2()) > 0.635) failures.Add("Player returned away from the seam encounter point.");
            EndWar(enemy, enemyFaction);
        }

        // The nearest row to y0 (0.5 steps, within 40) where every x is on land.
        internal static float LandRow(float y0, params float[] xs)
        {
            for (float dy = 0; dy < 40; dy += 0.5f)
                foreach (float y in new[] { y0 + dy, y0 - dy })
                    if (xs.All(x => new CampaignVec2(new Vec2(x, y), true).Face.IsValid())) return y;
            return float.NaN;
        }

        private static string MenuId()
        {
            var map = TaleWorlds.Core.Game.Current.GameStateManager.ActiveState as TaleWorlds.CampaignSystem.GameState.MapState;
            return map != null && map.MenuContext != null && map.MenuContext.GameMenu != null ? map.MenuContext.GameMenu.StringId : null;
        }

        // Undo the synthetic war so no hostile party interrupts the later phases.
        internal static void EndWar(MobileParty enemy, IFaction enemyFaction)
        {
            if (enemyFaction.IsAtWarWith(Clan.PlayerClan.MapFaction)) MakePeaceAction.Apply(Clan.PlayerClan.MapFaction, enemyFaction);
            if (enemy.IsActive) enemy.Ai.SetDoNotMakeNewDecisions(false);
        }

        // A terrain click on the duplicate strip. MapScreen's left-click handler passes the picked point to
        // MapState.ProcessTravel; a strip pick carries display coordinates, so the order must store the canonical point and
        // the party must take the short route through the seam.
        private static IEnumerator Picking(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            var main = MobileParty.MainParty;
            if (PlayerEncounter.Current != null) { row["leftoverEncounterFinished"] = true; PlayerEncounter.Finish(true); }
            RuntimeMissionProbe.Pause();
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            float y = LandRow(main.Position.Y, world.MaxX - 3f, world.MaxX + 4f, world.OriginX + 4f);
            if (float.IsNaN(y)) { failures.Add("No land row for the strip click near the player."); yield break; }
            var start = new CampaignVec2(new Vec2(world.MaxX - 3f, y), true);
            var display = new CampaignVec2(new Vec2(world.MaxX + 4f, y), true);  // the strip copy of (OriginX + 4, y)
            var mapState = TaleWorlds.Core.Game.Current.GameStateManager.ActiveState as TaleWorlds.CampaignSystem.GameState.MapState;
            if (mapState == null) { failures.Add("Not on the map."); yield break; }
            main.Position = start; main.SetMoveModeHold();
            mapState.ProcessTravel(display);
            var stored = main.MoveTargetPoint;
            var canonical = new Vec2(world.Canonical(display.X), display.Y);
            row["click"] = new Dictionary<string, object> { ["start"] = Xy(start), ["displayPoint"] = Xy(display), ["storedTarget"] = Xy(stored),
                ["moveMode"] = main.DefaultBehavior.ToString(), ["path"] = "MapState.ProcessTravel (the MapScreen left-click terrain path); the scene pick itself is not simulated" };
            if (!world.IsCanonical(stored.X) || stored.ToVec2().Distance(canonical) > 0.01f) failures.Add("The travel order stored the display point instead of the canonical point.");
            var record = new Dictionary<string, object> { ["leg"] = "player strip click east->west", ["party"] = main.StringId }; row["travel"] = record;
            var steps = Travel(context, boot, world, main, () => world.Distance(main.Position.ToVec2(), canonical) < 0.5f, 60, record);
            while (steps.MoveNext()) yield return steps.Current;
            Judge(record, failures, world, start.ToVec2(), canonical);
        }

        // Saved CampaignVec2 members of MobileParty (save-definition survey). NextTargetPosition is steering state:
        // ComputePathAfterLoad recomputes the path from MoveTargetPoint, so it may lie in the adjacent image mid-crossing.
        private static readonly string[] SavedPositions = { "_position", "MoveTargetPoint", "_targetPosition", "_pathLastPosition", "<EndPositionForNavigationTransition>k__BackingField" };

        // Save once while the main party is crossing the seam, deserialize the written file with the official reader
        // (TaleWorlds.Core.MBSaveLoad.LoadSaveGameData) and compare every saved party position with its live value and the canonical range.
        // Resuming a generated campaign from the file needs world restoration before deserialization (MMR-49).
        private static IEnumerator SeamSave(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            var main = MobileParty.MainParty;
            var campaign = Campaign.Current;
            RuntimeMissionProbe.Pause();
            float y = LandRow(main.Position.Y, world.OriginX + 2f, world.MaxX - 2f, world.OriginX - 1f);
            if (float.IsNaN(y)) { failures.Add("No land row for the save crossing near the player."); yield break; }
            var from = new CampaignVec2(new Vec2(world.OriginX + 2f, y), true);
            var to = new CampaignVec2(new Vec2(world.MaxX - 2f, y), true);
            main.Position = from; main.SetMoveGoToPoint(to, MobileParty.NavigationType.Default);
            int before = PeriodicSeam.Events.Count;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
            double start = context.Seconds;
            while (!PeriodicSeam.Events.Skip(before).Any(e => e.Party == main.StringId) && context.Seconds - start < 30)
            {
                boot.Tick();
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppablePlay);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            if (!PeriodicSeam.Events.Skip(before).Any(e => e.Party == main.StringId)) { failures.Add("The main party did not cross before the save."); yield break; }

            var nextTarget = main.NextTargetPosition;
            row["atSave"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["moveTarget"] = Xy(main.MoveTargetPoint), ["nextTarget"] = Xy(nextTarget),
                ["nextTargetCanonical"] = world.IsCanonical(nextTarget.X), ["distanceToSeam"] = Math.Min(main.Position.X - world.OriginX, world.MaxX - main.Position.X),
                ["nonCanonicalParties"] = MobileParty.All.Where(p => p.IsActive && !world.IsCanonical(p.Position.X)).Select(p => p.StringId).ToList() };

            string saveName = "mnbl_wrap63_" + Guid.NewGuid().ToString("N");
            if (TaleWorlds.Core.MBSaveLoad.IsSaveGameFileExists(saveName)) { failures.Add("Refusing to replace an existing save."); yield break; }
            var previous = campaign.SandBoxManager.SandBoxSaveManager;
            var observer = new SaveObserver(saveName);
            campaign.SandBoxManager.SandBoxSaveManager = observer;
            row["saveName"] = saveName;
            start = context.Seconds;
            campaign.SaveHandler.SaveAs(saveName);
            while (!observer.Completed && context.Seconds - start < 120) yield return null;
            campaign.SandBoxManager.SandBoxSaveManager = previous;
            row["saveSucceeded"] = observer.Success; row["saveSeconds"] = context.Seconds - start;
            // The save serializes on a later frame (behaviours run their before-save hooks), so compare with the state after it.
            var live = new Dictionary<string, float[][]>();
            foreach (var party in MobileParty.All) live[party.StringId] = ReadSaved(party);
            if (!observer.Success || !TaleWorlds.Core.MBSaveLoad.IsSaveGameFileExists(saveName)) { failures.Add("The save did not complete."); yield break; }

            // MMR-64: the header carries the coordinate policy; the package check accepts it and refuses another policy or none.
            var info = TaleWorlds.Core.MBSaveLoad.GetSaveFileWithName(saveName);
            var header = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in WorldIdentity.Keys) { string value; if (info != null && info.MetaData.TryGetValue(WorldIdentityBehavior.Prefix + key, out value)) header[key] = value; }
            string savedPolicy; header.TryGetValue("coordinatePolicy", out savedPolicy);
            var policy = new Dictionary<string, object> { ["saved"] = savedPolicy, ["expected"] = world.Coordinates.FrameIdentity }; row["savePolicy"] = policy;
            if (savedPolicy != world.Coordinates.FrameIdentity) failures.Add("The save header's coordinate frame is " + (savedPolicy ?? "missing") + ", not the world's.");
            if (header.ContainsKey("package") && header.ContainsKey("manifestSha256"))
            {
                var package = GeneratedWorldPackage.Resolve(header);
                Func<Dictionary<string, string>, string> verdict = h => { try { WorldIdentity.RequireMatch(h, package); return "accepted"; } catch (System.IO.InvalidDataException e) { return "refused: " + e.Message; } };
                var otherPolicy = new Dictionary<string, string>(header) { ["coordinatePolicy"] = new string('0', 64) };
                var noPolicy = new Dictionary<string, string>(header); noPolicy.Remove("coordinatePolicy");
                policy["asSaved"] = verdict(header); policy["anotherPolicy"] = verdict(otherPolicy); policy["noPolicy"] = verdict(noPolicy);
                if ((string)policy["asSaved"] != "accepted" || ((string)policy["anotherPolicy"]).StartsWith("accepted", StringComparison.Ordinal) || ((string)policy["noPolicy"]).StartsWith("accepted", StringComparison.Ordinal))
                    failures.Add("The package check does not accept exactly the saved coordinate policy.");
            }
            else failures.Add("The save header names no world package.");

            var data = MNBL.ProceduralWorld.Persistence.LoadRouting.Deserialize(() => TaleWorlds.Core.MBSaveLoad.LoadSaveGameData(saveName));   // MMR-49: the guard's allowance
            row["deserialized"] = data != null && data.Successful;
            if (data == null || !data.Successful) { failures.Add("The save could not be deserialized."); yield break; }
            var loadedCampaign = ((TaleWorlds.Core.Game)data.Root).GameType as Campaign;
            var objects = HarmonyLib.AccessTools.Property(typeof(Campaign), "CampaignObjectManager").GetValue(loadedCampaign);
            var parties = ((System.Collections.IEnumerable)HarmonyLib.AccessTools.Field(objects.GetType(), "_mobileParties").GetValue(objects)).Cast<MobileParty>().ToList();
            int compared = 0, mismatched = 0, nonCanonical = 0; var problems = new List<string>();
            foreach (var party in parties)
            {
                float[][] saved = ReadSaved(party), current;
                if (!live.TryGetValue(party.StringId, out current)) { problems.Add(party.StringId + ": not in the live campaign"); mismatched++; continue; }
                for (int i = 0; i < SavedPositions.Length; i++)
                {
                    compared++;
                    if (!Same(saved[i][0], current[i][0]) || !Same(saved[i][1], current[i][1])) { mismatched++; if (problems.Count < 20) problems.Add(party.StringId + "." + SavedPositions[i] + " saved " + saved[i][0] + "," + saved[i][1] + " live " + current[i][0] + "," + current[i][1]); }
                    bool unset = float.IsNaN(saved[i][0]) || (saved[i][0] == 0 && saved[i][1] == 0);  // NaN is the engine's unset sentinel
                    if (!unset && !world.IsCanonical(saved[i][0])) { nonCanonical++; if (problems.Count < 20) problems.Add(party.StringId + "." + SavedPositions[i] + " x=" + saved[i][0] + " not canonical"); }
                }
            }
            var loadedMain = parties.FirstOrDefault(p => p.StringId == main.StringId);
            row["reloaded"] = new Dictionary<string, object> { ["parties"] = parties.Count, ["liveParties"] = live.Count, ["fieldsCompared"] = compared,
                ["mismatched"] = mismatched, ["nonCanonical"] = nonCanonical, ["problems"] = problems,
                ["player"] = loadedMain == null ? null : ReadSaved(loadedMain)[0],
                ["scope"] = "deserialized with TaleWorlds.Core.MBSaveLoad.LoadSaveGameData in the running session; resuming a generated campaign from the file is MMR-49" };
            if (loadedMain == null) failures.Add("The main party is missing from the save.");
            if (parties.Count != live.Count) failures.Add("The save holds " + parties.Count + " parties; the live campaign has " + live.Count + ".");
            if (mismatched > 0) failures.Add(mismatched + " saved positions differ from the live values.");
            if (nonCanonical > 0) failures.Add(nonCanonical + " saved positions are not canonical.");
            try { TaleWorlds.Core.MBSaveLoad.DeleteSaveGame(saveName); row["saveDeleted"] = true; } catch (Exception e) { row["saveDeleted"] = e.Message; }
        }

        private static bool Same(float a, float b) { return a == b || (float.IsNaN(a) && float.IsNaN(b)); }

        private static float[][] ReadSaved(MobileParty party)
        {
            return SavedPositions.Select(name => { var v = (CampaignVec2)HarmonyLib.AccessTools.Field(typeof(MobileParty), name).GetValue(party); return new[] { v.X, v.Y }; }).ToArray();
        }

        private sealed class SaveObserver : ISaveManager
        {
            private readonly string expected;
            internal bool Completed, Success;
            internal SaveObserver(string expected) { this.expected = expected; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string name) { Success = success && name == expected; Completed = true; }
        }

        private static Dictionary<string, object> Tie(GeneratedMapScene map, PeriodicWorld world, TaleWorlds.CampaignSystem.ComponentInterfaces.PartyNavigationModel nav,
            TaleWorlds.CampaignSystem.ComponentInterfaces.MapDistanceModel distanceModel, List<string> failures)
        {
            var invalid = nav.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.Naval);
            // An open-sea row: find y where both x0 and x0 + W/2 are navigable sea and the rows between are sea.
            for (float y = world.OriginX * 0 + 200; y < world.Extent - 200; y += 25)
                for (float lx = 100; lx < world.Width / 2 - 100; lx += 50)
                {
                    var a = new CampaignVec2(new Vec2(world.OriginX + lx, y), false);
                    var b = new CampaignVec2(new Vec2(world.OriginX + lx + world.Width / 2, y), false);
                    if (!a.Face.IsValid() || !b.Face.IsValid()) continue;
                    if (!nav.IsTerrainTypeValidForNavigationType((TaleWorlds.Core.TerrainType)a.Face.FaceGroupIndex, MobileParty.NavigationType.Naval)
                        || !nav.IsTerrainTypeValidForNavigationType((TaleWorlds.Core.TerrainType)b.Face.FaceGroupIndex, MobileParty.NavigationType.Naval)) continue;
                    PathFaceRecord fromFace, face; Vec2 from, image; float d;
                    if (!map.NearestTargetImage(a.Face, b.Face, a.ToVec2(), b.ToVec2(), 0.3f, Campaign.PathFindingMaxCostLimit, invalid,
                            distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand, out fromFace, out from, out face, out image, out d)) continue;
                    float direct, west = float.NaN;
                    bool okDirect = map.PlanarPathDistance(a.Face, b.Face, a.ToVec2(), b.ToVec2(), 0.3f, Campaign.PathFindingMaxCostLimit, out direct, invalid,
                        distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                    var westImage = new CampaignVec2(new Vec2(b.X - world.Width, y), false);
                    bool okWest = westImage.Face.IsValid() && map.PlanarPathDistance(a.Face, westImage.Face, a.ToVec2(), westImage.ToVec2(), 0.3f,
                        Campaign.PathFindingMaxCostLimit, out west, invalid, distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                    // MMR-64: the same target passed as its west copy must give the same image (the rule does not depend on the copy).
                    PathFaceRecord fromViaWestFace, faceViaWest; Vec2 fromViaWest, imageViaWest = new Vec2(float.NaN, float.NaN); float dViaWest = float.NaN;
                    bool okViaWest = westImage.Face.IsValid() && map.NearestTargetImage(a.Face, westImage.Face, a.ToVec2(), westImage.ToVec2(), 0.3f, Campaign.PathFindingMaxCostLimit, invalid,
                        distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand, out fromViaWestFace, out fromViaWest, out faceViaWest, out imageViaWest, out dViaWest);
                    var row = new Dictionary<string, object> { ["from"] = Xy(a), ["to"] = Xy(b), ["directDistance"] = okDirect ? (object)direct : null,
                        ["westImageDistance"] = okWest ? (object)west : null, ["chosen"] = new[] { image.X, image.Y }, ["chosenDistance"] = d,
                        ["chosenWhenWestCopyPassed"] = okViaWest ? new[] { imageViaWest.X, imageViaWest.Y } : null,
                        ["rule"] = "MMR-64 (PeriodicDistanceModel.ShortestOverImages): within 1e-3 the nearer |dx| wins, an exact half-width tie resolves east, whichever copy is passed" };
                    bool equal = okDirect && okWest && Math.Abs(direct - west) <= 1e-3f;
                    row["routesEqual"] = equal;
                    if (equal && Math.Abs(image.X - b.X) > 1e-3f) failures.Add("Tie did not resolve east.");
                    if (equal && (!okViaWest || Math.Abs(imageViaWest.X - b.X) > 1e-3f)) failures.Add("Tie depended on the copy passed (the west copy did not resolve east).");
                    if (!equal) continue;  // keep searching for an exactly equal pair
                    return row;
                }
            return new Dictionary<string, object> { ["found"] = false, ["note"] = "no exactly equal east/west route pair found on open sea rows; rule unit-checked in tests/World/PeriodicCoordinateTests.cs" };
        }

        // MMR-64: every member of the installed MapDistanceModel against PeriodicDistanceModel.Manifest, the patch counts, cross-seam values
        // for each overload category, directed transition costs, PathExistBetweenPoints capabilities, the closest entrance near the seam against
        // a brute-force periodic search, and the periodic battle-terrain map. Fixture: town1 by the east seam, town2 by the west.
        private static void DistanceCoverage(GeneratedMapScene map, PeriodicWorld world, PartyNavigationModel nav, MapDistanceModel distanceModel,
            Settlement town1, Settlement town2, string worldId, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            var land = MobileParty.NavigationType.Default; var sea = MobileParty.NavigationType.Naval; var all = MobileParty.NavigationType.All;
            bool navalModel = distanceModel.GetType().FullName == "NavalDLC.GameComponents.NavalDLCMapDistanceModel";

            // 1. Reflection: the manifest names every member of the installed base type, and both models declare every method.
            List<string> uncovered, stale;
            var members = PeriodicDistanceModel.Members(typeof(MapDistanceModel));
            PeriodicDistanceModel.Compare(members, out uncovered, out stale);
            var notOverridden = new[] { typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel), distanceModel.GetType() }.Distinct()
                .ToDictionary(t => t.FullName, t => PeriodicDistanceModel.NotOverridden(t));
            row["reflection"] = new Dictionary<string, object> { ["installedMembers"] = members, ["uncovered"] = uncovered, ["stale"] = stale,
                ["notOverridden"] = notOverridden.ToDictionary(kv => kv.Key, kv => (object)kv.Value), ["activeModel"] = distanceModel.GetType().FullName, ["manifest"] = PeriodicDistanceModel.Record() };
            if (uncovered.Count > 0 || stale.Count > 0) failures.Add("The manifest and the installed MapDistanceModel differ: uncovered [" + String.Join(", ", uncovered) + "], stale [" + String.Join(", ", stale) + "]");
            foreach (var kv in notOverridden) if (kv.Value.Count > 0) failures.Add(kv.Key + " does not declare " + String.Join(", ", kv.Value));

            // 2. Patch counts, the MMR-64 closest settlement order and distance helpers included.
            Func<string, int> replacedIn = part => PeriodicPatches.Applied.Where(a => ((string)a["target"]).Contains(part)).Sum(a => a.ContainsKey("replacedCalls") ? Convert.ToInt32(a["replacedCalls"]) : 0);
            int ordering = replacedIn("<GetClosestSettlementsToPositionInCache>");
            var helperPatches = PeriodicDistanceModel.HelperCoverage.ToDictionary(h => h.Type + "::" + h.Member.Substring(0, h.Member.IndexOf('(')), h => replacedIn(h.Type + "::" + h.Member.Substring(0, h.Member.IndexOf('('))));
            row["patches"] = new Dictionary<string, object> { ["applied"] = PeriodicPatches.Applied.Count, ["orderingLambdas"] = ordering,
                ["helpers"] = helperPatches.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["partyLegs"] = PeriodicPatches.Applied.Where(a => ((string)a["target"]).EndsWith("::GetDistance", StringComparison.Ordinal)).ToDictionary(a => (string)a["target"], a => a["replacedCalls"]) };
            if (ordering != 2) failures.Add("The closest settlement order patch replaced " + ordering + " ordering calls (expected 2).");
            foreach (var h in PeriodicDistanceModel.HelperCoverage)
            {
                string key = h.Type + "::" + h.Member.Substring(0, h.Member.IndexOf('('));
                if (helperPatches[key] != h.Sites) failures.Add("The " + key + " patch replaced " + helperPatches[key] + " planar calls (expected " + h.Sites + ").");
            }

            // 2b. The distance helpers: every planar distance call in their original IL (Harmony's reading) has a HelperCoverage entry.
            var helperScan = new List<object>(); var helperUncovered = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            const System.Reflection.BindingFlags every = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var helper in new[] { typeof(Helpers.DistanceHelper), typeof(Helpers.SettlementHelper) })
                foreach (var type in new[] { helper }.Concat(helper.GetNestedTypes(every)))
                    foreach (var method in type.GetMethods(every))
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        List<HarmonyLib.CodeInstruction> code;
                        try { code = HarmonyLib.PatchProcessor.GetOriginalInstructions(method); }
                        catch (Exception e) { helperUncovered.Add(type.FullName + "::" + method.Name + " could not be read: " + e.Message); continue; }
                        int sites = code.Count(i => (i.opcode == System.Reflection.Emit.OpCodes.Call || i.opcode == System.Reflection.Emit.OpCodes.Callvirt) && i.operand is System.Reflection.MethodBase
                            && PeriodicDistanceModel.PlanarDistanceMethods.Contains(PeriodicDistanceModel.PlanarName((System.Reflection.MethodBase)i.operand)));
                        if (sites == 0) continue;
                        string member = PeriodicDistanceModel.Signature(method);
                        var listed = PeriodicDistanceModel.HelperCoverage.FirstOrDefault(h => h.Type == type.FullName && h.Member == member);
                        seen.Add(type.FullName + "::" + member);
                        helperScan.Add(new Dictionary<string, object> { ["type"] = type.FullName, ["member"] = member, ["planarSites"] = sites, ["listed"] = listed != null });
                        if (listed == null || listed.Sites != sites) helperUncovered.Add(type.FullName + "::" + member + " has " + sites + " planar distance calls" + (listed == null ? " and no coverage entry" : ", the entry lists " + listed.Sites));
                    }
            foreach (var h in PeriodicDistanceModel.HelperCoverage) if (!seen.Contains(h.Type + "::" + h.Member)) helperUncovered.Add(h.Type + "::" + h.Member + " is listed but has no planar call");
            row["helpers"] = new Dictionary<string, object> { ["scan"] = helperScan, ["uncovered"] = helperUncovered, ["coverage"] = PeriodicDistanceModel.HelperRecord() };
            if (helperUncovered.Count > 0) failures.Add("Distance helper coverage: " + String.Join("; ", helperUncovered));

            // 3. Cross-seam values of every overload category: never below the wrapped lower bound, and short (planar is about a width).
            var main = MobileParty.MainParty;
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            main.Position = town1.GatePosition; main.SetMoveModeHold();
            // Any active party on land (War Sails lords carry ships; on land they are land parties for the Default overloads), lords first.
            var other = MobileParty.All.Where(p => p != main && p.IsActive && p.MapEvent == null && !p.IsCurrentlyAtSea && (p.IsLordParty || p.IsCaravan || p.IsBandit))
                .OrderBy(p => p.IsLordParty ? 0 : 1).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (other != null)
            {
                if (other.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(other);
                other.Ai.SetDoNotMakeNewDecisions(true);
                other.Position = town2.GatePosition; other.SetMoveModeHold();
            }
            float planar = town1.GatePosition.ToVec2().Distance(town2.GatePosition.ToVec2());
            var values = new Dictionary<string, object>(); row["values"] = values;
            Action<string, float, Vec2, Vec2> expectShort = (name, value, a, b) => {
                float lower = world.Distance(a, b);
                values[name] = new Dictionary<string, object> { ["value"] = value, ["lowerBound"] = lower, ["planarStraight"] = a.Distance(b) };
                if (float.IsNaN(value) || value < lower - 1e-2f || value > 3 * lower + 30 || value > planar / 2)
                    failures.Add(name + " = " + value + " is not the short cross-seam value (wrapped lower bound " + lower + ", planar " + a.Distance(b) + ")");
            };
            Vec2 g1 = town1.GatePosition.ToVec2(), g2 = town2.GatePosition.ToVec2(), p1 = town1.PortPosition.ToVec2(), p2 = town2.PortPosition.ToVec2();
            float ratio;
            expectShort("settlement->settlement gates 1->2", distanceModel.GetDistance(town1, town2, false, false, land), g1, g2);
            expectShort("settlement->settlement gates 2->1", distanceModel.GetDistance(town2, town1, false, false, land), g2, g1);
            expectShort("settlement->settlement ports 1->2 (land ratio)", distanceModel.GetDistance(town1, town2, true, true, sea, out ratio), p1, p2); values["ports 1->2 land ratio"] = ratio;
            expectShort("settlement->settlement ports 2->1", distanceModel.GetDistance(town2, town1, true, true, sea), p2, p1);
            expectShort("party->settlement gate", distanceModel.GetDistance(main, town2, false, land, out ratio), main.Position.ToVec2(), g2);
            var point = town2.GatePosition;
            expectShort("party->point", distanceModel.GetDistance(main, in point, land, out ratio), main.Position.ToVec2(), point.ToVec2());
            expectShort("settlement->point", distanceModel.GetDistance(town1, in point, false, land), g1, point.ToVec2());
            values["party->party other"] = other == null ? null : other.StringId;
            if (other == null) failures.Add("No AI party on land for the party-to-party overloads.");
            else
            {
                expectShort("party->party", distanceModel.GetDistance(main, other, land, out ratio), main.Position.ToVec2(), other.Position.ToVec2());
                float bounded, boundedRatio;
                bool within = distanceModel.GetDistance(main, other, land, 100f, out bounded, out boundedRatio);
                values["party->party bounded(100) within"] = within;
                if (!within) failures.Add("The bounded party-to-party distance across the seam is not within 100.");
                else expectShort("party->party bounded(100)", bounded, main.Position.ToVec2(), other.Position.ToVec2());
            }
            // The installed settlement cache keeps one value per unordered pair.
            values["cacheSymmetric"] = distanceModel.GetDistance(town1, town2, false, false, land) == distanceModel.GetDistance(town2, town1, false, false, land)
                && distanceModel.GetDistance(town1, town2, true, true, sea) == distanceModel.GetDistance(town2, town1, true, true, sea);

            // 3b. Seam-crossing legs: the party and the point sit by the east seam, where the closest entrance is town2 across the seam, so the
            //     straight legs of every party, point and helper formula cross it. Each value is short, never below the wrapped lower bound,
            //     and differs from the same call with the periodic world switched off (the patched legs fall back to planar; the cache and the
            //     map scene stay periodic), so the periodic legs are what the answer depends on.
            var seamValues = new Dictionary<string, object>(); row["seamLegs"] = seamValues;
            float seamY = LandRow(town2.GatePosition.Y, world.MaxX - 2f, world.OriginX + 3f);
            if (float.IsNaN(seamY)) failures.Add("No land row by the seam for the seam-crossing legs.");
            else
            {
                var east = new CampaignVec2(new Vec2(world.MaxX - 2f, seamY), true);
                var westOfSeam = new CampaignVec2(new Vec2(world.OriginX + 3f, seamY), true);
                var entrance = distanceModel.GetClosestEntranceToFace(east.Face, land).Item1;
                seamValues["eastPoint"] = Xy(east); seamValues["eastPointEntrance"] = entrance == null ? null : entrance.StringId;
                if (entrance != town2) failures.Add("The east seam point's closest entrance is " + (entrance == null ? "none" : entrance.StringId) + ", not town2 across the seam.");
                Func<Func<float>, float> planarOf = call => { var current = PeriodicWorld.Current; PeriodicWorld.Current = null; try { return call(); } finally { PeriodicWorld.Current = current; } };
                Action<string, Func<float>, Vec2, Vec2> seam = (name, call, a, b) => {
                    float value = call(), plain = planarOf(call), lower = world.Distance(a, b);
                    seamValues[name] = new Dictionary<string, object> { ["value"] = value, ["withoutPeriodicWorld"] = plain, ["lowerBound"] = lower };
                    if (float.IsNaN(value) || value < lower - 1e-2f || value > 3 * lower + 30 || Math.Abs(value - plain) <= 1)
                        failures.Add("seam leg " + name + " = " + value + " (without the periodic world " + plain + ", wrapped lower bound " + lower + ")");
                };
                main.Position = east; main.SetMoveModeHold();
                seam("party->settlement", () => { float r; return distanceModel.GetDistance(main, town1, false, land, out r); }, east.ToVec2(), g1);
                var gate = town1.GatePosition;
                seam("party->point", () => { float r; return distanceModel.GetDistance(main, in gate, land, out r); }, east.ToVec2(), g1);
                var eastPoint = east;
                seam("settlement->point", () => distanceModel.GetDistance(town1, in eastPoint, false, land), g1, east.ToVec2());
                if (other != null)
                {
                    other.Position = westOfSeam; other.SetMoveModeHold();
                    seam("party->party (model)", () => { float r; return distanceModel.GetDistance(main, other, land, out r); }, east.ToVec2(), westOfSeam.ToVec2());
                    seam("DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty", () => Helpers.DistanceHelper.FindClosestDistanceFromMobilePartyToMobileParty(main, other, land),
                        east.ToVec2(), westOfSeam.ToVec2());
                    other.Position = town1.GatePosition; other.SetMoveModeHold();
                    seam("DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty", () => { float r; return Helpers.DistanceHelper.GetDistanceBetweenMobilePartyToMobileParty(main, other, land, out r); },
                        east.ToVec2(), g1);
                }
                var nearest = Helpers.SettlementHelper.FindNearestSettlementToPoint(in eastPoint, s => s.IsTown);
                var active = PeriodicWorld.Current; PeriodicWorld.Current = null;
                Settlement nearestPlain;
                try { nearestPlain = Helpers.SettlementHelper.FindNearestSettlementToPoint(in eastPoint, s => s.IsTown); } finally { PeriodicWorld.Current = active; }
                seamValues["SettlementHelper.FindNearestSettlementToPoint (towns)"] = new Dictionary<string, object> { ["value"] = nearest == null ? null : nearest.StringId,
                    ["withoutPeriodicWorld"] = nearestPlain == null ? null : nearestPlain.StringId };
                if (nearest != town2 || nearestPlain == town2) failures.Add("SettlementHelper.FindNearestSettlementToPoint by the east seam gave " + (nearest == null ? "none" : nearest.StringId) + " (expected town2 across the seam).");
                main.Position = town1.GatePosition; main.SetMoveModeHold();
            }
            values["landRatio ports 1->2"] = distanceModel.GetLandRatioOfPathBetweenSettlements(town1, town2, true, true);
            values["portToGate town1"] = distanceModel.GetPortToGateDistanceForSettlement(town1);
            values["maximumConnected"] = new[] { land, sea, all }.ToDictionary(t => t.ToString(), t => (object)distanceModel.GetMaximumDistanceBetweenTwoConnectedSettlements(t));
            var neighbours = distanceModel.GetNeighborsOfFortification(town1.Town, land).Select(s => s.StringId).ToList();
            values["neighbours of town1 (land)"] = neighbours;
            if (!neighbours.Contains(town2.StringId)) failures.Add("town2, across the seam, is not a land neighbour of town1.");

            // 4. Directed transition costs (War Sails): from a port to a gate pays sea to land, the reverse land to sea; port to port across the
            //    seam keeps the sign flip, which needs the cross-seam cache distance to be finite.
            int toSea = distanceModel.RegionSwitchCostFromLandToSea, toLand = distanceModel.RegionSwitchCostFromSeaToLand;
            float portToGate = distanceModel.GetTransitionCostAdjustment(town1, true, town2, false, false, false);
            float gateToPort = distanceModel.GetTransitionCostAdjustment(town2, false, town1, true, false, false);
            float portToPort = distanceModel.GetTransitionCostAdjustment(town1, true, town2, true, false, false);
            // The installed costs may be equal (War Sails 1.2.8: 50 and 50); then direction is not observable here (the offline grid uses 5 and 1).
            row["transitions"] = new Dictionary<string, object> { ["landToSea"] = toSea, ["seaToLand"] = toLand, ["directionObservable"] = toSea != toLand, ["town1 port -> town2 gate"] = portToGate,
                ["town2 gate -> town1 port"] = gateToPort, ["town1 port -> town2 port"] = portToPort };
            if (navalModel ? portToGate != -toLand || gateToPort != -toSea || portToPort != toSea + toLand : portToGate != 0 || gateToPort != 0 || portToPort != 0)
                failures.Add("Transition cost adjustments across the seam are not the directed installed values.");

            // 5. PathExistBetweenPoints: a matching capability across the seam exists.
            var gate1 = town1.GatePosition; var gate2 = town2.GatePosition; var port1 = town1.PortPosition; var port2 = town2.PortPosition;
            var exists = new Dictionary<string, object> { ["gate1 -> gate2 Default"] = distanceModel.PathExistBetweenPoints(in gate1, in gate2, land),
                ["port1 -> port2 Naval"] = distanceModel.PathExistBetweenPoints(in port1, in port2, sea), ["gate1 -> port2 Default"] = distanceModel.PathExistBetweenPoints(in gate1, in port2, land) };
            row["pathExists"] = exists;
            if (!(bool)exists["gate1 -> gate2 Default"] || !(bool)exists["port1 -> port2 Naval"]) failures.Add("PathExistBetweenPoints denies a cross-seam path of a matching capability.");

            // 6. The closest entrance per face (the cache the prefilter builds) near the seam, against a brute force over every candidate
            //    settlement with the periodic path query. Seam-sensitive samples are those whose planar-nearest entrance is not the true one.
            var samples = new List<object>(); row["closestEntrance"] = samples;
            float radius = nav.GetEmbarkDisembarkThresholdDistance();
            int sensitive = 0, wrong = 0;
            var c = world.Coordinates;
            foreach (var type in new[] { land, sea })
            {
                bool naval = type == sea;
                var candidates = Settlement.All.Where(s => (s.IsFortification || s.IsVillage) && (!naval || s.HasPort)).ToList();
                var invalid = nav.GetInvalidTerrainTypesForNavigationType(type);
                var points = new List<CampaignVec2>();
                for (float y = (float)c.OriginY + 4; y < c.OriginY + c.Height - 4; y += 6)
                    foreach (float x in new[] { world.OriginX + 1.5f, world.OriginX + 15f, world.MaxX - 15f, world.MaxX - 1.5f, world.OriginX - 1.5f, world.MaxX + 1.5f })
                    {
                        var at = new CampaignVec2(new Vec2(x, y), !naval);
                        if (at.Face.IsValid() && nav.IsTerrainTypeValidForNavigationType((TaleWorlds.Core.TerrainType)at.Face.FaceGroupIndex, type)) points.Add(at);
                    }
                foreach (var at in points.Take(40))
                {
                    var here = at;
                    Func<Settlement, bool, float> cost = (s, port) => {
                        var e = port ? s.PortPosition : s.GatePosition; float d;
                        return ((TaleWorlds.CampaignSystem.Map.IMapScene)map).GetPathDistanceBetweenAIFaces(here.Face, e.Face, here.ToVec2(), e.ToVec2(), radius, Campaign.PathFindingMaxCostLimit, out d,
                            invalid, toSea, toLand) ? d : float.MaxValue; };
                    var cached = distanceModel.GetClosestEntranceToFace(at.Face, type);
                    Settlement best = null; float bestCost = float.MaxValue;
                    foreach (var s in candidates) { float d = cost(s, naval); if (d < bestCost) { bestCost = d; best = s; } }
                    var planarNearest = candidates.OrderBy(s => (naval ? s.PortPosition : s.GatePosition).ToVec2().DistanceSquared(at.ToVec2())).FirstOrDefault();
                    float cachedCost = cached.Item1 == null ? float.MaxValue : cost(cached.Item1, cached.Item2);
                    bool isSensitive = best != null && planarNearest != best, ok = best == null || (cached.Item1 != null && cachedCost <= bestCost + Math.Max(0.5f, 0.02f * bestCost));
                    if (isSensitive) sensitive++;
                    if (!ok) wrong++;
                    samples.Add(new Dictionary<string, object> { ["type"] = type.ToString(), ["point"] = Xy(at), ["canonical"] = world.IsCanonical(at.X),
                        ["cached"] = cached.Item1 == null ? null : cached.Item1.StringId + (cached.Item2 ? ":port" : ":gate"), ["cachedCost"] = cachedCost,
                        ["best"] = best == null ? null : best.StringId, ["bestCost"] = bestCost, ["planarNearest"] = planarNearest == null ? null : planarNearest.StringId,
                        ["seamSensitive"] = isSensitive, ["ok"] = ok });
                }
            }
            row["closestEntranceSummary"] = new Dictionary<string, object> { ["samples"] = samples.Count, ["seamSensitive"] = sensitive, ["wrong"] = wrong };
            if (samples.Count == 0) failures.Add("No closest-entrance samples near the seam.");
            if (wrong > 0) failures.Add(wrong + " closest-entrance samples near the seam are not the periodic nearest.");

            // 7. The battle-terrain map: every strip cell holds its canonical cell's bytes, and a block starts at the seam.
            var bytes = (byte[])HarmonyLib.AccessTools.Field(typeof(SandBox.MapScene), "_battleTerrainIndexMap").GetValue(map);
            int width = (int)HarmonyLib.AccessTools.Field(typeof(SandBox.MapScene), "_battleTerrainIndexMapWidth").GetValue(map);
            int height = (int)HarmonyLib.AccessTools.Field(typeof(SandBox.MapScene), "_battleTerrainIndexMapHeight").GetValue(map);
            float cell = world.Extent / width;
            int originCell = (int)Math.Round(world.OriginX / cell), periodCells = (int)Math.Round(world.Width / cell), stripCells = 0, mismatched = 0, seamOffset = -1;
            if (bytes != null && bytes.Length == width * height * 2)
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (x >= originCell && x < originCell + periodCells) continue;
                        stripCells++;
                        int image = originCell + ((x - originCell) % periodCells + periodCells) % periodCells;
                        int at = 2 * (y * width + x), canonical = 2 * (y * width + image);
                        if (bytes[at] != bytes[canonical] || bytes[at + 1] != bytes[canonical + 1]) mismatched++;
                    }
                seamOffset = bytes[2 * originCell + 1] & 15;
            }
            row["battleTerrainMap"] = new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["originCell"] = originCell, ["periodCells"] = periodCells,
                ["stripCells"] = stripCells, ["mismatchedStripCells"] = mismatched, ["blockOffsetAtSeam"] = seamOffset };
            if (bytes == null || stripCells == 0 || mismatched > 0 || seamOffset != 0) failures.Add("The battle-terrain map does not wrap (" + mismatched + " strip cells differ from their canonical cells, block offset at the seam " + seamOffset + ").");
            if (other != null) other.Ai.SetDoNotMakeNewDecisions(false);
        }

        internal static void Judge(Dictionary<string, object> record, List<string> failures, PeriodicWorld world, Vec2 from, Vec2 to)
        {
            string leg = (string)record["leg"];
            if (!(bool)record["arrived"]) failures.Add(leg + ": did not arrive.");
            if ((int)record["rebases"] < 1) failures.Add(leg + ": never crossed the seam (no rebase).");
            if ((double)record["maxStepPeriodic"] > 5) failures.Add(leg + ": a sample moved " + record["maxStepPeriodic"] + " units (map-width motion).");
            if (record.ContainsKey("maxPlanarResidual") && (float)record["maxPlanarResidual"] > 5) failures.Add(leg + ": a sample jumped " + record["maxPlanarResidual"] + " units planarly beyond its rebases.");
            double planar = from.Distance(to), periodic = world.Distance(from, to);
            if ((float)record["traveledPeriodic"] > planar * 0.9 && periodic < planar * 0.5) failures.Add(leg + ": travelled the long planar way.");
            if (!(bool)record["canonicalThroughout"]) failures.Add(leg + ": party was left non-canonical for more than one sample.");
        }

        internal static IEnumerator Travel(ProbeContext context, ICampaignTicker boot, PeriodicWorld world, MobileParty party, Func<bool> done,
            double timeoutSeconds, Dictionary<string, object> record)
        {
            var campaign = Campaign.Current;
            int rebasesBefore = PeriodicSeam.Events.Count;
            var start = party.Position; double begin = context.Seconds, lastSample = 0, lastProgress = context.Seconds;
            float traveled = 0, maxStep = 0; var last = party.Position.ToVec2(); double startDays = CampaignTime.Now.ToDays;
            var track = new List<float[]>(); int nonCanonicalSamples = 0, nonCanonicalRun = 0, maxNonCanonicalRun = 0;
            var logicalSequence = new List<float>();
            double cameraMax = 0;
            // MMR-65: the periodic step cannot see a jump of about k·W; the raw planar step less this party's rebase shifts since the last
            // frame can (a rebase moves it by exactly its shift).
            var lastPlanar = party.Position.ToVec2(); int eventsSeen = PeriodicSeam.Events.Count; float maxResidual = 0;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            bool arrived = false;
            while (context.Seconds - begin < timeoutSeconds)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                var position = party.Position.ToVec2();
                float shift = 0;
                for (int e = eventsSeen; e < PeriodicSeam.Events.Count; e++) if (PeriodicSeam.Events[e].Party == party.StringId) shift += PeriodicSeam.Events[e].Shift;
                eventsSeen = PeriodicSeam.Events.Count;
                maxResidual = Math.Max(maxResidual, new Vec2(position.X - shift - lastPlanar.X, position.Y - lastPlanar.Y).Length);
                lastPlanar = position;
                float step = world.Distance(last, position);  // a rebase is not motion
                if (step > 0.01f) { traveled += step; maxStep = Math.Max(maxStep, step); last = position; lastProgress = context.Seconds; }
                if (context.Seconds - lastSample >= 0.1)
                {
                    lastSample = context.Seconds;
                    bool canonical = world.IsCanonical(position.X);
                    if (!canonical) { nonCanonicalSamples++; nonCanonicalRun++; maxNonCanonicalRun = Math.Max(maxNonCanonicalRun, nonCanonicalRun); } else nonCanonicalRun = 0;
                    if (track.Count < 3000) track.Add(new[] { position.x, position.y, world.LogicalX(position.x), party.IsCurrentlyAtSea ? 1f : 0f });
                    if (party == MobileParty.MainParty && context.Seconds - begin > 1)
                    {
                        var camera = CameraFrameDistance();
                        if (!Double.IsNaN(camera)) cameraMax = Math.Max(cameraMax, camera);
                    }
                }
                if (done()) { arrived = true; break; }
                if (context.Seconds - lastProgress > 30) break;
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            var events = PeriodicSeam.Events.Skip(rebasesBefore).Where(e => e.Party == party.StringId).ToList();
            record["arrived"] = arrived; record["realSeconds"] = context.Seconds - begin; record["campaignDays"] = CampaignTime.Now.ToDays - startDays;
            record["start"] = new[] { start.X, start.Y }; record["end"] = new[] { party.Position.X, party.Position.Y };
            record["traveledPeriodic"] = traveled; record["maxStepPeriodic"] = (double)maxStep; record["maxPlanarResidual"] = maxResidual;
            record["rebases"] = events.Count;
            record["rebaseEvents"] = events.Select(e => new Dictionary<string, object> { ["fromX"] = e.FromX, ["toX"] = e.ToX, ["y"] = e.Y, ["shift"] = e.Shift,
                ["pathPoints"] = e.PathPointsShifted }).ToList();
            record["nonCanonicalSamples"] = nonCanonicalSamples; record["maxNonCanonicalRun"] = maxNonCanonicalRun;
            record["canonicalThroughout"] = maxNonCanonicalRun <= 1;
            if (party == MobileParty.MainParty) record["cameraFrameMaxDistance"] = cameraMax;
            record["track"] = track;
            context.Checkpoint();
        }

        // Horizontal distance between the map camera frame and the main party (SandBox.View reached by reflection). MMR-66: periodic, i.e. to
        // the party as drawn (every visual is drawn at its image nearest the camera, so the camera may look at the party's twin image).
        private static double CameraFrameDistance()
        {
            object screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
            if (screen == null) return Double.NaN;
            for (var type = screen.GetType(); type != null; type = type.BaseType)
                foreach (var f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    if (f.FieldType.Name == "MapCameraView")
                    {
                        var view = f.GetValue(screen);
                        var p = view.GetType().GetProperty("CameraFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (p == null) return Double.NaN;
                        var frame = (MatrixFrame)p.GetValue(view);
                        var party = MobileParty.MainParty.Position;
                        var world = PeriodicWorld.Current;
                        return world != null ? world.Distance(frame.origin.AsVec2, party.ToVec2())
                            : Math.Sqrt((frame.origin.x - party.X) * (frame.origin.x - party.X) + (frame.origin.y - party.Y) * (frame.origin.y - party.Y));
                    }
            return Double.NaN;
        }

        internal static float[] Xy(CampaignVec2 p) { return new[] { p.X, p.Y }; }
    }
}
