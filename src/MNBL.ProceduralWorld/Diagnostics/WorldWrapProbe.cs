using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Helpers;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

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
            var map = (DiagnosticMapScene)campaign.MapSceneWrapper;
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
            // Display copies: the fixture places visual-only settlement copies in the strips. They are scene entities with no
            // campaign identity, so every settlement stays one logical object.
            var copies = new List<object>();
            foreach (var s in Settlement.All.Where(s => s.StringId.StartsWith(worldId + "_", StringComparison.Ordinal)))
                foreach (var side in new[] { "west", "east" })
                {
                    var entity = map.Scene.FindEntityWithName(worldId + "_display_" + s.StringId.Substring(worldId.Length + 1) + "_" + side);
                    if (entity == null) continue;
                    var at = entity.GlobalPosition;
                    copies.Add(new Dictionary<string, object> { ["entity"] = entity.Name, ["of"] = s.StringId, ["position"] = new[] { at.x, at.y }, ["offset"] = at.x - s.Position.X });
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
            report["periodicQueries"] = DiagnosticMapScene.PeriodicQueries;
            report["periodicImageWins"] = DiagnosticMapScene.PeriodicImageWins;
            report["periodicNativeQueries"] = DiagnosticMapScene.PeriodicNativeQueries;
            report["periodicPruned"] = DiagnosticMapScene.PeriodicPruned;
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
            if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
            start = context.Seconds;
            while ((PlayerEncounter.Current != null || main.MapEvent != null || !(TaleWorlds.Core.Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState)) && context.Seconds - start < 30)
                yield return null;
            RuntimeMissionProbe.Pause();
            row["afterReturn"] = new Dictionary<string, object> { ["player"] = Xy(main.Position), ["canonical"] = world.IsCanonical(main.Position.X),
                ["displacement"] = world.Distance(before.ToVec2(), main.Position.ToVec2()) };
            if (!world.IsCanonical(main.Position.X)) failures.Add("Player not canonical after the seam battle.");
            if (world.Distance(before.ToVec2(), main.Position.ToVec2()) > 0.635) failures.Add("Player returned away from the seam encounter point.");
            EndWar(enemy, enemyFaction);
        }

        // The nearest row to y0 (0.5 steps, within 40) where every x is on land.
        private static float LandRow(float y0, params float[] xs)
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
        private static void EndWar(MobileParty enemy, IFaction enemyFaction)
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

            var data = TaleWorlds.Core.MBSaveLoad.LoadSaveGameData(saveName);
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

        private static Dictionary<string, object> Tie(DiagnosticMapScene map, PeriodicWorld world, TaleWorlds.CampaignSystem.ComponentInterfaces.PartyNavigationModel nav,
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
                    PathFaceRecord face; Vec2 image; float d;
                    if (!map.NearestTargetImage(a.Face, b.Face, a.ToVec2(), b.ToVec2(), 0.3f, Campaign.PathFindingMaxCostLimit, invalid,
                            distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand, out face, out image, out d)) continue;
                    float direct, west = float.NaN;
                    bool okDirect = map.PlanarPathDistance(a.Face, b.Face, a.ToVec2(), b.ToVec2(), 0.3f, Campaign.PathFindingMaxCostLimit, out direct, invalid,
                        distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                    var westImage = new CampaignVec2(new Vec2(b.X - world.Width, y), false);
                    bool okWest = westImage.Face.IsValid() && map.PlanarPathDistance(a.Face, westImage.Face, a.ToVec2(), westImage.ToVec2(), 0.3f,
                        Campaign.PathFindingMaxCostLimit, out west, invalid, distanceModel.RegionSwitchCostFromLandToSea, distanceModel.RegionSwitchCostFromSeaToLand);
                    var row = new Dictionary<string, object> { ["from"] = Xy(a), ["to"] = Xy(b), ["directDistance"] = okDirect ? (object)direct : null,
                        ["westImageDistance"] = okWest ? (object)west : null, ["chosen"] = new[] { image.X, image.Y }, ["chosenDistance"] = d,
                        ["rule"] = "within 1e-3 keep the earlier image: as given, then +Width (east), then -Width (west)" };
                    bool equal = okDirect && okWest && Math.Abs(direct - west) <= 1e-3f;
                    row["routesEqual"] = equal;
                    if (equal && Math.Abs(image.X - b.X) > 1e-3f) failures.Add("Tie did not keep the as-given image.");
                    if (!equal) continue;  // keep searching for an exactly equal pair
                    return row;
                }
            return new Dictionary<string, object> { ["found"] = false, ["note"] = "no exactly equal east/west route pair found on open sea rows; rule unit-checked in PeriodicWorld" };
        }

        private static void Judge(Dictionary<string, object> record, List<string> failures, PeriodicWorld world, Vec2 from, Vec2 to)
        {
            string leg = (string)record["leg"];
            if (!(bool)record["arrived"]) failures.Add(leg + ": did not arrive.");
            if ((int)record["rebases"] < 1) failures.Add(leg + ": never crossed the seam (no rebase).");
            if ((double)record["maxStepPeriodic"] > 5) failures.Add(leg + ": a sample moved " + record["maxStepPeriodic"] + " units (map-width motion).");
            double planar = from.Distance(to), periodic = world.Distance(from, to);
            if ((float)record["traveledPeriodic"] > planar * 0.9 && periodic < planar * 0.5) failures.Add(leg + ": travelled the long planar way.");
            if (!(bool)record["canonicalThroughout"]) failures.Add(leg + ": party was left non-canonical for more than one sample.");
        }

        private static IEnumerator Travel(ProbeContext context, DiagnosticSandboxBootstrap boot, PeriodicWorld world, MobileParty party, Func<bool> done,
            double timeoutSeconds, Dictionary<string, object> record)
        {
            var campaign = Campaign.Current;
            int rebasesBefore = PeriodicSeam.Events.Count;
            var start = party.Position; double begin = context.Seconds, lastSample = 0, lastProgress = context.Seconds;
            float traveled = 0, maxStep = 0; var last = party.Position.ToVec2(); double startDays = CampaignTime.Now.ToDays;
            var track = new List<float[]>(); int nonCanonicalSamples = 0, nonCanonicalRun = 0, maxNonCanonicalRun = 0;
            var logicalSequence = new List<float>();
            double cameraMax = 0;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            bool arrived = false;
            while (context.Seconds - begin < timeoutSeconds)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                var position = party.Position.ToVec2();
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
            record["traveledPeriodic"] = traveled; record["maxStepPeriodic"] = (double)maxStep;
            record["rebases"] = events.Count;
            record["rebaseEvents"] = events.Select(e => new Dictionary<string, object> { ["fromX"] = e.FromX, ["toX"] = e.ToX, ["y"] = e.Y, ["shift"] = e.Shift,
                ["pathPoints"] = e.PathPointsShifted, ["cameraFields"] = e.CameraFields }).ToList();
            record["nonCanonicalSamples"] = nonCanonicalSamples; record["maxNonCanonicalRun"] = maxNonCanonicalRun;
            record["canonicalThroughout"] = maxNonCanonicalRun <= 1;
            if (party == MobileParty.MainParty) record["cameraFrameMaxDistance"] = cameraMax;
            record["track"] = track;
            context.Checkpoint();
        }

        // Horizontal distance between the map camera frame and the main party (SandBox.View reached by reflection).
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
                        return Math.Sqrt((frame.origin.x - party.X) * (frame.origin.x - party.X) + (frame.origin.y - party.Y) * (frame.origin.y - party.Y));
                    }
            return Double.NaN;
        }

        private static float[] Xy(CampaignVec2 p) { return new[] { p.X, p.Y }; }
    }
}
