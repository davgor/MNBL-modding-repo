using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using State = MNBL.ProceduralWorld.Automation.LandMissionsProbe.State;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-53: naval gameplay on the generated world (naval_missions; scenarios=<repo>/tests/Runtime/NavalScenarios.json). World A's designated
    // test save (MMR-52's) is loaded through the normal load entry, then the player's party takes War Sails' installed paths, the calls its
    // menus and map clicks make, in this order:
    //   shipyard.buy: a ship bought at a port town through the port screen's trade handler (it docks the new fleet there);
    //   route.port: the town's "Go to the port" and the port menu's "Set sail", a voyage to another port town of the basin as a map click
    //     orders it, its arrival (the fleet docked), the town center and the leave path (on land at the gate);
    //   shipyard.repair: a damaged ship repaired through the port menu;
    //   route.island: the same voyage to an island's port town;
    //   blockade: a port town of another kingdom besieged from land by the party owning the fleet (the blockade, its arcs), then lifted;
    //   dropoff: aboard to a harbour village (its menu's disembark), back aboard at the anchor, a coastal landing by a map click on land and
    //     back aboard at the anchor it left;
    //   pirate.battle: a pirate encounter at sea, the naval battle mission, then a retreat;
    //   raid: a seaborne raid on a harbour village of another kingdom (the naval raid mission, the looting);
    //   naval.defeat: a naval battle lost, captivity, the escape, War Sails' raft until it washes ashore;
    // while every AI party's land/sea transition is recorded (OnMobilePartyNavigationStateChangedEvent) and the pirates are audited against
    // the world's spawn plan. The harness (State, Case, menus, returns, the log window) is LandMissionsProbe's. Every expectation is recomputed
    // by tools/summarize_naval_missions.py from the outputs; that summary is the verdict.
    internal static class NavalMissionsProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private sealed class Voyage
        {
            public Settlement Start, Mainland, Island, Village;
            public int StartBasin, MainGroup;
            public bool Pinned;
            public HashSet<Settlement> IslandPorts = new HashSet<Settlement>();
            public Dictionary<string, object> Targets = new Dictionary<string, object>();
        }

        private static IEnumerator Body(ProbeContext context)
        {
            string path = context.Str("scenarios", null);
            if (path == null || !File.Exists(path)) throw new InvalidOperationException("scenarios=<tests/Runtime/NavalScenarios.json> is required.");
            var plan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
            var world = (Dictionary<string, object>)plan["world"];
            var st = new State { Context = context, Rules = (Dictionary<string, object>)plan["rules"], Save = (string)world["designatedSave"], Tag = "naval" };
            st.Tally = st.Cultures;
            st.LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs", "rgl_log_" + context.ProcessId + ".txt");
            var targets = plan.ContainsKey("targets") ? (Dictionary<string, object>)plan["targets"] : new Dictionary<string, object>();
            context.Outputs["scope"] = "MMR-53: naval gameplay on generated world A.";
            context.Outputs["scenarioFile"] = new Dictionary<string, object> { ["schema"] = plan["schema"], ["sha256"] = GeneratedWorldPackage.Sha256(File.ReadAllBytes(path)) };
            context.Outputs["rules"] = st.Rules;
            context.Outputs["failures"] = st.Failures;
            context.Outputs["cases"] = st.Cases;
            context.Outputs["menus"] = st.MenuOptions;
            var baselines = new List<object>(); context.Outputs["baselines"] = baselines;
            Action<string> baseline = label =>
            {
                var held = WorldLifetimeScope.Held();
                baselines.Add(new Dictionary<string, object> { ["at"] = label, ["game"] = Game.Current != null, ["held"] = held.ToDictionary(k => k.Key, k => (object)k.Value), ["tracked"] = WorldLifetimeScope.Names.Count });
                if (Game.Current != null || held.Count > 0) st.Failures.Add("baseline " + label + ": a game or a tracked static remains");
            };
            baseline("start");
            if (MBSaveLoad.GetSaveFileWithName(st.Save) == null)
                throw new InvalidOperationException("the designated save " + st.Save + " is missing: run land_missions phase=prepare first");

            var load = new Dictionary<string, object>(); context.Outputs["load"] = load;
            yield return LandMissionsProbe.LoadDesignated(st, load);
            if (!Equals(load["loaded"], true)) st.Failures.Add("the designated save did not load");
            else
            {
                context.Outputs["setup"] = LandMissionsProbe.Setup(st);
                var traffic = new Traffic(st); context.Outputs["traffic"] = traffic.Record;
                try
                {
                    var voyage = Choose(st, targets); context.Outputs["voyage"] = Describe(voyage);
                    traffic.Islands = voyage.IslandPorts;
                    context.Outputs["pirates"] = Pirates(st);
                    // The cases run in order and share the party and its fleet: a voyage starts at the town where the previous step docked the
                    // fleet, the party at its gate where the leave path put it (LandMissionsProbe.Enter records every placement and its distance).
                    // The blockade moves the party to the besieged town and back to where it found it (recorded setup). A step that fails stops
                    // the steps that need its result ("the previous step did not finish").
                    yield return LandMissionsProbe.Case(st, "shipyard.buy", row => Buy(st, row, voyage));
                    yield return LandMissionsProbe.Case(st, "route.port", row => Sail(st, row, voyage.Start, voyage.Mainland, "port"));
                    yield return LandMissionsProbe.Case(st, "shipyard.repair", row => Repair(st, row, voyage.Mainland));
                    yield return LandMissionsProbe.Case(st, "route.island", row => Sail(st, row, voyage.Mainland, voyage.Island, "island"));
                    yield return LandMissionsProbe.Case(st, "blockade", row => Blockade(st, row, voyage));
                    yield return LandMissionsProbe.Case(st, "dropoff", row => DropOff(st, row, voyage.Island, voyage.Village));
                    yield return LandMissionsProbe.Case(st, "pirate.battle", row => PirateBattle(st, row, false));
                    // The seam at sea: the party sails across it, then meets a pirate placed across it from the party.
                    yield return LandMissionsProbe.Case(st, "route.seam", row => SeamVoyage(st, row));
                    yield return LandMissionsProbe.Case(st, "pirate.seam", row => PirateBattle(st, row, false, true));
                    yield return LandMissionsProbe.Case(st, "raid", row => Raid(st, row, voyage));
                    yield return LandMissionsProbe.Case(st, "naval.defeat", row => PirateBattle(st, row, true));
                }
                finally { traffic.Stop(); }
                context.Outputs["menusSeen"] = st.MenusSeen;
                context.Outputs["reloads"] = st.Reloads;
            }
            context.Checkpoint();
            if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
            baseline("end");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (st.Failures.Count > 0) throw new InvalidOperationException("Naval expectations failed: " + String.Join("; ", st.Failures.Take(12)));
        }

        // ---------------------------------------------------------------- the voyage's settlements

        // The voyage's settlements: the scenario file's pinned targets, each still meeting its rule (a regression replays the same straits and
        // coastal towns); without them, chosen by the rule. The start: a peaceful port town of the main land group with a tradeable ship at its
        // port and, in its basin, another mainland port town, an island's port town (a land group other than the main one) and a harbour
        // village; first by id. The mainland destination is the nearest such port town; the island and the village the nearest to it and to
        // the island. Land groups and basins are the engine's own reachability (CampaignPlayability).
        private static Voyage Choose(State st, Dictionary<string, object> targets)
        {
            var world = CampaignPlayability.FromCampaign(st.WorldId, null, new List<PlayabilityWorld.Start>());
            var main = MobileParty.MainParty;
            var distance = Campaign.Current.Models.MapDistanceModel;
            int mainGroup = world.Sites.Values.GroupBy(s => s.Group).OrderByDescending(g => g.Count()).First().Key;
            Func<Settlement, bool> peaceful = s => !s.MapFaction.IsAtWarWith(main.MapFaction) && s.SiegeEvent == null && !s.IsUnderRaid;
            Func<Settlement, PlayabilityWorld.Site> site = s => world.Sites[s.StringId];
            var ports = Settlement.All.Where(s => s.IsTown && s.HasPort && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && peaceful(s) && world.Sites.ContainsKey(s.StringId))
                .OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            var islandPorts = new HashSet<Settlement>(Settlement.All.Where(s => s.IsTown && s.HasPort && world.Sites.ContainsKey(s.StringId) && world.Sites[s.StringId].Group != mainGroup));
            Func<Settlement, Settlement, float> far = (a, b) => a.PortPosition.ToVec2().Distance(b.PortPosition.ToVec2());
            Func<Settlement, Settlement, bool> byWater = (a, b) => { var p = a.PortPosition; var q = b.PortPosition; return distance.PathExistBetweenPoints(in p, in q, MobileParty.NavigationType.Naval); };
            Func<string, Settlement> pinned = key => targets.ContainsKey(key) ? Settlement.Find((string)targets[key]) : null;
            Settlement ps = pinned("start"), pm = pinned("mainland"), pi = pinned("island"), pv = pinned("village");
            if (ps != null && pm != null && pi != null && pv != null)
            {
                string problem = !ports.Contains(ps) || site(ps).Group != mainGroup || !ps.Party.Ships.Any(x => Raider(x)) ? "start"
                    : !ports.Contains(pm) || site(pm).Group != mainGroup || site(pm).Basin != site(ps).Basin ? "mainland"
                    : !ports.Contains(pi) || site(pi).Group == mainGroup || site(pi).Basin != site(ps).Basin ? "island"
                    : !pv.IsVillage || !pv.HasPort || !peaceful(pv) || !byWater(pi, pv) ? "village" : null;
                if (problem != null) throw new InvalidOperationException("the pinned " + problem + " target no longer meets its rule");
                return new Voyage { Start = ps, Mainland = pm, Island = pi, Village = pv, StartBasin = site(ps).Basin, MainGroup = mainGroup, Pinned = true, IslandPorts = islandPorts, Targets = targets };
            }
            foreach (var start in ports.Where(s => site(s).Group == mainGroup && s.Party.Ships.Any(x => Raider(x))))
            {
                int basin = site(start).Basin;
                var mainland = ports.Where(s => s != start && site(s).Group == mainGroup && site(s).Basin == basin).OrderBy(s => far(start, s)).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (mainland == null) continue;
                var island = ports.Where(s => site(s).Group != mainGroup && site(s).Basin == basin).OrderBy(s => far(mainland, s)).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (island == null) continue;
                var village = Settlement.All.Where(s => s.IsVillage && s.HasPort && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && peaceful(s) && byWater(island, s))
                    .OrderBy(s => far(island, s)).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (village == null) continue;
                return new Voyage { Start = start, Mainland = mainland, Island = island, Village = village, StartBasin = basin, MainGroup = mainGroup, IslandPorts = islandPorts, Targets = targets };
            }
            throw new InvalidOperationException("no peaceful port town with a tradeable shallow-draft ship, a mainland and an island port town and a harbour village in its basin");
        }

        // A ship the seaborne raid can use: tradeable, not a quest's, a shallow-draft hull whose main deck carries the raid's minimum crew.
        private static bool Raider(Ship ship)
        {
            var hull = ((IShipOrigin)ship).Hull;
            return ship.IsTradeable && !ship.IsUsedByQuest && hull.CanNavigateShallowWater && ship.MainDeckCrewCapacity >= 15;
        }

        private static Dictionary<string, object> Describe(Voyage v)
        {
            Func<Settlement, object> s = x => new Dictionary<string, object> { ["id"] = x.StringId, ["name"] = x.Name.ToString(), ["port"] = new[] { x.PortPosition.X, x.PortPosition.Y }, ["gate"] = new[] { x.GatePosition.X, x.GatePosition.Y } };
            return new Dictionary<string, object> { ["start"] = s(v.Start), ["mainland"] = s(v.Mainland), ["island"] = s(v.Island), ["village"] = s(v.Village), ["basin"] = v.StartBasin, ["mainGroup"] = v.MainGroup,
                ["pinned"] = v.Pinned, ["islandPortTowns"] = v.IslandPorts.Select(x => (object)x.StringId).OrderBy(x => (string)x, StringComparer.Ordinal).ToList() };
        }

        // A pinned target by its key, still meeting the case's rule; without a pin, the rule's own choice.
        private static Settlement Pinned(Voyage v, string key, Func<Settlement, bool> rule, Func<Settlement> choose)
        {
            if (!v.Targets.ContainsKey(key)) return choose();
            var s = Settlement.Find((string)v.Targets[key]);
            if (s == null || !rule(s)) throw new InvalidOperationException("the pinned " + key + " " + v.Targets[key] + " no longer meets its rule");
            return s;
        }

        // ---------------------------------------------------------------- the shipyard

        // A ship of the start town's port bought as the port screen's Done buys it: War Sails' PortScreenTradeModeHandler (the trade-mode handler
        // the screen builds for the town and the player) takes the ship (OnBuyShip) and commits (OnConfirmChanges: ApplyByTrade, which takes
        // ShipCostModel's trade value from the buyer, then docks a fleet without a valid anchor at the town). The ship is the cheapest shallow-draft
        // one (the seaborne raid needs it). The player's party gets a crew, food for the voyages and the price (recorded setup).
        private static IEnumerator Buy(State st, Dictionary<string, object> row, Voyage v)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            LandMissionsProbe.Heal();
            setup["crewAdded"] = LandMissionsProbe.Crew(main, (int)st.Rule("crew"));
            var grain = MBObjectManager.Instance.GetObject<ItemObject>("grain");
            if (grain != null) { main.ItemRoster.AddToCounts(grain, (int)st.Rule("grain")); setup["grainAdded"] = (int)st.Rule("grain"); }
            yield return LandMissionsProbe.Enter(st, row, v.Start, "town");
            var ship = v.Start.Party.Ships.Where(Raider).OrderBy(x => Campaign.Current.Models.ShipCostModel.GetShipTradeValue(x, v.Start.Party, PartyBase.MainParty)).ThenBy(x => x.Name.ToString(), StringComparer.Ordinal).First();
            int price = (int)Campaign.Current.Models.ShipCostModel.GetShipTradeValue(ship, v.Start.Party, PartyBase.MainParty);
            int topUp = Math.Max(0, price + (int)st.Rule("goldMargin") - Hero.MainHero.Gold);
            if (topUp > 0) { GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, topUp, true); setup["goldAdded"] = topUp; }
            int gold = Hero.MainHero.Gold, townGold = v.Start.Town.Gold;
            setup["anchorValidBefore"] = main.Anchor.IsValid;
            var handlerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("NavalDLC.ViewModelCollection.Port.PortScreenHandlers.PortScreenTradeModeHandler", false)).FirstOrDefault(t => t != null);
            if (handlerType == null) throw new InvalidOperationException("War Sails' port trade handler (NavalDLC.ViewModelCollection) is not loaded");
            var handler = Activator.CreateInstance(handlerType, v.Start.Party, PartyBase.MainParty);
            // The screen's buy button state (PortActionInfo: relevant, enabled, its gold cost and tooltip).
            var canBuy = handlerType.GetMethod("GetCanBuyShip").Invoke(handler, new object[] { ship });
            var tooltip = canBuy == null ? null : LandMissionsProbe.Field(canBuy, "Tooltip");
            setup["canBuy"] = canBuy == null ? null : new Dictionary<string, object> { ["relevant"] = LandMissionsProbe.Field(canBuy, "IsRelevant"), ["enabled"] = LandMissionsProbe.Field(canBuy, "IsEnabled"),
                ["goldCost"] = LandMissionsProbe.Field(canBuy, "GoldCost"), ["tooltip"] = tooltip == null ? null : tooltip.ToString() };
            handlerType.GetMethod("OnBuyShip").Invoke(handler, new object[] { ship });
            var confirm = new object[] { null };
            bool canConfirm = (bool)handlerType.GetMethod("GetCanConfirm").Invoke(handler, confirm);
            setup["canConfirm"] = canConfirm; setup["confirmHint"] = confirm[0] == null ? null : confirm[0].ToString();
            if (!canConfirm) throw new InvalidOperationException("the port screen would not confirm the purchase: " + setup["confirmHint"]);
            handlerType.GetMethod("OnConfirmChanges").Invoke(handler, null);
            row["ship"] = new Dictionary<string, object> { ["name"] = ship.Name.ToString(), ["hull"] = ((IShipOrigin)ship).Hull.StringId, ["price"] = price, ["hitPoints"] = ship.HitPoints, ["maxHitPoints"] = ship.MaxHitPoints };
            row["bought"] = new Dictionary<string, object> { ["owner"] = ship.Owner == PartyBase.MainParty, ["inParty"] = main.Ships.Contains(ship), ["goldPaid"] = gold - Hero.MainHero.Gold,
                ["townGoldGained"] = v.Start.Town.Gold - townGold, ["anchorDocked"] = main.Anchor.IsAtSettlement(v.Start), ["naval"] = main.HasNavalNavigationCapability };
            if (ship.Owner != PartyBase.MainParty || !main.Ships.Contains(ship)) failures.Add("the bought ship is not the player's");
            if (gold - Hero.MainHero.Gold != price) failures.Add("the player paid " + (gold - Hero.MainHero.Gold) + ", not the trade value " + price);
            if (!main.Anchor.IsAtSettlement(v.Start)) failures.Add("the bought fleet is not docked at " + v.Start.StringId);
            yield return Leave(st, row);
        }

        // A ship damaged by the installed damage path (Ship.OnShipDamaged, recorded setup), then the port menu's repair (RepairShipAction for each
        // damaged docked ship, paid through ShipCostModel's repair cost).
        private static IEnumerator Repair(State st, Dictionary<string, object> row, Settlement town)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            if (!main.Anchor.IsAtSettlement(town)) throw new InvalidOperationException("the fleet is not docked at " + town.StringId + " (the voyage did not arrive)");
            var ship = main.Ships.OrderBy(x => x.Name.ToString(), StringComparer.Ordinal).First();
            float damage = ship.MaxHitPoints * (float)st.Rule("repairDamageFraction"), dealt;
            ship.OnShipDamaged(damage, null, out dealt);
            float damaged = ship.HitPoints;
            int cost = (int)Campaign.Current.Models.ShipCostModel.GetShipRepairCost(ship, PartyBase.MainParty);
            row["setup"] = new Dictionary<string, object> { ["damage"] = damage, ["dealt"] = dealt, ["hitPointsDamaged"] = damaged, ["maxHitPoints"] = ship.MaxHitPoints, ["repairCost"] = cost };
            yield return LandMissionsProbe.Enter(st, row, town, "town");
            LandMissionsProbe.RecordMenu(st);
            RuntimeMissionProbe.RunOption("port");
            yield return LandMissionsProbe.Menu(st, "port_menu");
            int gold = Hero.MainHero.Gold;
            yield return Handler(st);
            RuntimeMissionProbe.RunOption("repair_ships");
            yield return TwoWorldMatrixProbe.Frames(5);
            row["repaired"] = new Dictionary<string, object> { ["hitPoints"] = ship.HitPoints, ["goldPaid"] = gold - Hero.MainHero.Gold, ["menu"] = LandMissionsProbe.CurrentMenu() };
            if (ship.HitPoints < ship.MaxHitPoints - 0.01f) failures.Add("the ship was not repaired: " + ship.HitPoints + " of " + ship.MaxHitPoints);
            if (gold - Hero.MainHero.Gold != cost) failures.Add("the repair cost " + (gold - Hero.MainHero.Gold) + ", not the model's " + cost);
            if (LandMissionsProbe.CurrentMenu() != "port_menu") yield return LandMissionsProbe.Menu(st, "port_menu");
            RuntimeMissionProbe.RunOption("leave_option");
            yield return LandMissionsProbe.Menu(st, "town");
            yield return Leave(st, row);
        }

        // A menu's view attaches its handler a frame or more after the menu opens; options whose consequence refreshes the menu (the repair's
        // GameMenuManager.RefreshMenuOptions calls the handler) wait for it (run 27796: a NullReferenceException there).
        private static IEnumerator Handler(State st)
        {
            double start = st.Context.Seconds;
            while ((Campaign.Current.CurrentMenuContext == null || Campaign.Current.CurrentMenuContext.Handler == null) && st.Context.Seconds - start < 5) yield return null;
            yield return TwoWorldMatrixProbe.Frames(2);
        }

        // ---------------------------------------------------------------- voyages

        // From a town where the fleet is docked: the town menu's port, the port menu's "Set sail" (at sea at the port), the voyage to the
        // destination's port as a map click orders it, campaign time running until the party arrives (sampled every campaign hour: position, sea
        // or land, transition, navigation), the destination's port menu (the fleet docked there), its town center and the leave path.
        private static IEnumerator Sail(State st, Dictionary<string, object> row, Settlement from, Settlement to, string kind)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            row["from"] = from.StringId; row["to"] = to.StringId; row["kind"] = kind;
            if (!main.Anchor.IsAtSettlement(from)) throw new InvalidOperationException("the fleet is not docked at " + from.StringId + " (the previous step did not finish)");
            yield return LandMissionsProbe.Enter(st, row, from, "town");
            yield return SetSail(st, row, from);
            yield return SailTo(st, row, to);
            // A town reached by sea: naval_town_outside switches to the port menu; the party is in the town, on land, the fleet docked.
            yield return LandMissionsProbe.Menu(st, "port_menu");
            row["arrival"] = new Dictionary<string, object> { ["settlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId, ["atSea"] = main.IsCurrentlyAtSea,
                ["anchorDocked"] = main.Anchor.IsAtSettlement(to), ["menus"] = new List<string>(st.MenusSeen.Skip(Math.Max(0, st.MenusSeen.Count - 3))) };
            if (main.CurrentSettlement != to) failures.Add("the party did not enter " + to.StringId + " from the sea");
            if (main.IsCurrentlyAtSea) failures.Add("the party is still at sea in " + to.StringId);
            if (!main.Anchor.IsAtSettlement(to)) failures.Add("the fleet did not dock at " + to.StringId);
            RuntimeMissionProbe.RunOption("leave_option");
            yield return LandMissionsProbe.Menu(st, "town");
            yield return Leave(st, row);
        }

        private static IEnumerator SetSail(State st, Dictionary<string, object> row, Settlement from)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            RuntimeMissionProbe.RunOption("port");
            yield return LandMissionsProbe.Menu(st, "port_menu");
            RuntimeMissionProbe.RunOption("sail_option");
            yield return TwoWorldMatrixProbe.Frames(5);
            row["sailed"] = new Dictionary<string, object> { ["atSea"] = main.IsCurrentlyAtSea, ["settlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId,
                ["fromPort"] = main.Position.ToVec2().Distance(from.PortPosition.ToVec2()), ["anchorValid"] = main.Anchor.IsValid, ["navigation"] = GeneratedSpawnAssertions.Problem(main), ["menu"] = LandMissionsProbe.CurrentMenu() };
            if (!main.IsCurrentlyAtSea || main.CurrentSettlement != null) failures.Add("the port's set sail did not put the party at sea");
            if (GeneratedSpawnAssertions.Problem(main) != null) failures.Add("the party set sail off naval navigation: " + GeneratedSpawnAssertions.Problem(main));
        }

        // The voyage as a map click orders it (NavigationHelper.GetInteractionDataForMainParty: the navigation type and the port target), campaign
        // time running (fast forward; the player sheltered) until the party's encounter with the destination; an hourly sample of the party,
        // with the water's width there away from both ends of the voyage (the narrowest recorded).
        private static IEnumerator SailTo(State st, Dictionary<string, object> row, Settlement to)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            bool canNavigate, isTargetingPort; MobileParty.NavigationType navigation;
            Helpers.NavigationHelper.GetInteractionDataForMainParty(to, out canNavigate, out navigation, out isTargetingPort);
            var order = new Dictionary<string, object> { ["canNavigate"] = canNavigate, ["navigation"] = navigation.ToString(), ["targetingPort"] = isTargetingPort }; row["order"] = order;
            if (!canNavigate) throw new InvalidOperationException("the map click cannot navigate to " + to.StringId);
            main.SetMoveGoToSettlement(to, navigation, isTargetingPort);
            var samples = new List<object>(); row["samples"] = samples;
            double hours = CampaignTime.Now.ToHours, next = hours, lastProgress = context.Seconds;
            var lastPosition = main.Position.ToVec2(); double sailed = 0; int offNavigation = 0, transitions = 0; bool wasAtSea = main.IsCurrentlyAtSea;
            float minWidth = -1f; var narrowest = lastPosition; var departure = lastPosition;
            DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
            double start = context.Seconds;
            while (main.CurrentSettlement != to && CampaignTime.Now.ToHours - hours < st.Rule("voyageHours"))
            {
                double now = CampaignTime.Now.ToHours;
                if (main.IsCurrentlyAtSea != wasAtSea) { transitions++; wasAtSea = main.IsCurrentlyAtSea; }
                if (now >= next)
                {
                    next = now + 1;
                    var p = main.Position.ToVec2(); sailed += p.Distance(lastPosition); if (p.Distance(lastPosition) > 0.01f) lastProgress = context.Seconds; lastPosition = p;
                    string problem = main.IsTransitionInProgress ? null : GeneratedSpawnAssertions.Problem(main);
                    if (problem != null) offNavigation++;
                    bool open = p.Distance(departure) > st.Rule("straitEndDistance") && p.Distance(to.PortPosition.ToVec2()) > st.Rule("straitEndDistance");
                    float width = main.IsCurrentlyAtSea && !main.IsTransitionInProgress && open ? Width(main.Position) : -1f;
                    if (width >= 0 && (minWidth < 0 || width < minWidth)) { minWidth = width; narrowest = p; }
                    // The movement state too (one-shot run 48692: the island voyage went ashore half way and walked back to its start town, and
                    // the samples could not say what the party was heading for).
                    if (samples.Count < 200) samples.Add(new Dictionary<string, object> { ["hours"] = Math.Round(now - hours, 1), ["position"] = new[] { p.X, p.Y }, ["atSea"] = main.IsCurrentlyAtSea,
                        ["transition"] = main.IsTransitionInProgress, ["problem"] = problem, ["width"] = width,
                        ["target"] = main.TargetSettlement == null ? null : main.TargetSettlement.StringId, ["targetingPort"] = main.IsTargetingPort,
                        ["shortTermTarget"] = main.ShortTermTargetSettlement == null ? null : main.ShortTermTargetSettlement.StringId,
                        ["behavior"] = main.DefaultBehavior + "/" + main.ShortTermBehavior, ["targetPosition"] = new[] { main.TargetPosition.X, main.TargetPosition.Y },
                        ["navigation"] = main.DesiredAiNavigationType.ToString() });
                }
                if (context.Seconds - lastProgress > 30) { failures.Add("the party stopped moving at sea (" + LandMissionsProbe.CurrentMenu() + ")"); break; }
                if (LandMissionsProbe.CurrentMenu() != "the map" && main.CurrentSettlement != to) { row["menuOnTheWay"] = LandMissionsProbe.CurrentMenu(); break; }
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop && LandMissionsProbe.CurrentMenu() == "the map") DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                if (context.Seconds - start > st.Rule("voyageSeconds")) { failures.Add("the voyage took longer than " + st.Rule("voyageSeconds") + " s"); break; }
                yield return null;
            }
            RuntimeMissionProbe.Pause();
            row["voyage"] = new Dictionary<string, object> { ["hours"] = CampaignTime.Now.ToHours - hours, ["distance"] = sailed, ["offNavigationSamples"] = offNavigation, ["landSeaChanges"] = transitions,
                ["arrived"] = main.CurrentSettlement == to, ["minWidth"] = minWidth, ["narrowest"] = new[] { narrowest.X, narrowest.Y } };
            if (offNavigation > 0) failures.Add(offNavigation + " hourly samples off navigation during the voyage");
            if (main.CurrentSettlement != to) throw new InvalidOperationException("the party did not arrive at " + to.StringId + " (at " + LandMissionsProbe.CurrentMenu() + ")");
        }

        // The water's width at a position: along each of 8 axes (16 directions in opposite pairs), the distance to the first point off naval
        // navigation one way plus the other (steps of 0.25, up to 8 each way); the narrowest axis. A strait reads its width; open sea reads 16.
        private static float Width(CampaignVec2 position)
        {
            var c = position.ToVec2(); float narrowest = 16f;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 8; float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                float across = Reach(c, dx, dy) + Reach(c, -dx, -dy);
                if (across < narrowest) narrowest = across;
            }
            return narrowest;
        }

        private static float Reach(Vec2 c, float dx, float dy)
        {
            for (float r = 0.25f; r <= 8f; r += 0.25f)
                if (GeneratedSpawnAssertions.Problem(new CampaignVec2(new Vec2(c.X + dx * r, c.Y + dy * r), false), MobileParty.NavigationType.Naval) != null) return r;
            return 8f;
        }

        // From a town menu: the leave path (town_leave: on land at the gate), the return checked there.
        private static IEnumerator Leave(State st, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var town = main.CurrentSettlement;
            if (LandMissionsProbe.CurrentMenu() != "town") yield return LandMissionsProbe.Menu(st, "town");
            RuntimeMissionProbe.RunOption("town_leave");
            yield return LandMissionsProbe.Idle(st, 30);
            row["after"] = RuntimeMissionProbe.Snapshot(false);
            row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
            if (row["afterNavigation"] != null) failures.Add("the party left off navigation: " + row["afterNavigation"]);
            if (town != null) LandMissionsProbe.ReturnTo(st, row, failures, town.GatePosition.ToVec2(), "the town's gate, where the leave path puts the party");
        }

        // From the island's port town: aboard, the voyage to the harbour village's port, its menu (the party at sea in the village), the menu's
        // disembark (on land at the gate, the fleet at the village's port), then back aboard at the anchor as a click on it orders it. Then a
        // coastal landing as a click on land orders it, and back aboard at the anchor that landing left.
        private static IEnumerator DropOff(State st, Dictionary<string, object> row, Settlement from, Settlement village)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            row["from"] = from.StringId; row["to"] = village.StringId;
            if (!main.Anchor.IsAtSettlement(from)) throw new InvalidOperationException("the fleet is not docked at " + from.StringId + " (the previous step did not finish)");
            yield return LandMissionsProbe.Enter(st, row, from, "town");
            yield return SetSail(st, row, from);
            yield return SailTo(st, row, village);
            yield return LandMissionsProbe.Menu(st, "village");
            row["arrival"] = new Dictionary<string, object> { ["settlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId, ["atSea"] = main.IsCurrentlyAtSea };
            if (!main.IsCurrentlyAtSea) failures.Add("the party reached the harbour village off the sea");
            RuntimeMissionProbe.RunOption("disembark");
            yield return LandMissionsProbe.Idle(st, 30);
            row["disembarked"] = new Dictionary<string, object> { ["atSea"] = main.IsCurrentlyAtSea, ["fromGate"] = main.Position.ToVec2().Distance(village.GatePosition.ToVec2()),
                ["anchorAtVillage"] = main.Anchor.IsAtSettlement(village), ["anchorFromPort"] = main.Anchor.IsValid ? main.Anchor.Position.ToVec2().Distance(village.PortPosition.ToVec2()) : -1f,
                ["navigation"] = GeneratedSpawnAssertions.Problem(main) };
            if (main.IsCurrentlyAtSea) failures.Add("the village's disembark left the party at sea");
            if (GeneratedSpawnAssertions.Problem(main) != null) failures.Add("the party disembarked off land navigation: " + GeneratedSpawnAssertions.Problem(main));
            LandMissionsProbe.ReturnTo(st, row, failures, village.GatePosition.ToVec2(), "the village's gate, where the disembark puts the party");
            if (!main.Anchor.IsAtSettlement(village)) failures.Add("the fleet is not at the village's port after the disembark");
            var reembarked = new Dictionary<string, object>(); row["reembarked"] = reembarked;
            yield return Aboard(st, reembarked, failures, "the village's anchor");
            if (!main.IsCurrentlyAtSea) { row["after"] = RuntimeMissionProbe.Snapshot(false); yield break; }

            // A coastal landing as a click on land orders it: MapState.ProcessTravel, the terrain click's own call (NavigationHelper's embark
            // and disembark data for the player, then MobileParty.SetMoveGoToPoint with the navigation CanPlayerNavigateToPosition gives), to a
            // land point 2 to 4 from the village's gate (NavigationHelper.FindPointAroundPosition, reachable on land). The party sails to the
            // coast, makes the sea-to-land transition and walks on; the anchor stays where it came ashore. Then back aboard at that anchor.
            var coast = new Dictionary<string, object>(); row["coastalLanding"] = coast;
            var point = RuntimeMissionProbe.FindPoint(village.GatePosition, MobileParty.NavigationType.Default, 4, 2);
            MobileParty.NavigationType clickNavigation;
            bool canNavigate = Helpers.NavigationHelper.CanPlayerNavigateToPosition(point, out clickNavigation);
            coast["point"] = new[] { point.X, point.Y }; coast["canNavigate"] = canNavigate; coast["navigation"] = clickNavigation.ToString();
            coast["fromSea"] = main.Position.ToVec2().Distance(point.ToVec2());
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            if (!canNavigate || map == null) throw new InvalidOperationException("the map click on land cannot navigate to the coastal point (map state " + (map != null) + ")");
            map.ProcessTravel(point);
            DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
            double hours = CampaignTime.Now.ToHours, start = context.Seconds, still = context.Seconds, transitionStart = -1, transitionHours = -1, landedAfter = -1;
            bool sawTransition = false; var ashore = main.Position.ToVec2(); var last = ashore;
            while (context.Seconds - start < st.Rule("coastSeconds"))
            {
                if (main.IsTransitionInProgress && !sawTransition) { sawTransition = true; transitionStart = CampaignTime.Now.ToHours; }
                if (sawTransition && transitionHours < 0 && !main.IsTransitionInProgress) transitionHours = CampaignTime.Now.ToHours - transitionStart;
                if (landedAfter < 0 && !main.IsCurrentlyAtSea) { landedAfter = CampaignTime.Now.ToHours - hours; ashore = main.Position.ToVec2(); }
                var p = main.Position.ToVec2(); if (p.Distance(last) > 0.001f) { still = context.Seconds; last = p; }
                if (landedAfter >= 0 && !main.IsTransitionInProgress && (p.Distance(point.ToVec2()) < 0.05f || context.Seconds - still > 5)) break;
                if (LandMissionsProbe.CurrentMenu() != "the map") { coast["menuOnTheWay"] = LandMissionsProbe.CurrentMenu(); break; }
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                yield return null;
            }
            RuntimeMissionProbe.Pause();
            yield return LandMissionsProbe.Idle(st, 30);
            var anchor = main.Anchor;
            coast["landed"] = new Dictionary<string, object> { ["atSea"] = main.IsCurrentlyAtSea, ["sawTransition"] = sawTransition, ["transitionHours"] = transitionHours, ["landedAfterHours"] = landedAfter,
                ["hours"] = CampaignTime.Now.ToHours - hours, ["ashore"] = new[] { ashore.X, ashore.Y }, ["fromPoint"] = main.Position.ToVec2().Distance(point.ToVec2()), ["navigation"] = GeneratedSpawnAssertions.Problem(main),
                ["anchorValid"] = anchor.IsValid, ["anchorAtVillage"] = anchor.IsAtSettlement(village), ["anchorFromAshore"] = anchor.IsValid ? anchor.Position.ToVec2().Distance(ashore) : -1f,
                ["anchorNavigation"] = anchor.IsValid ? GeneratedSpawnAssertions.Problem(anchor.Position, MobileParty.NavigationType.Naval) : "no anchor",
                ["lastDisembarkFromAshore"] = anchor.GetLastUsedDisembarkPosition().ToVec2().Distance(ashore) };
            if (main.IsCurrentlyAtSea) failures.Add("the map click on land did not bring the party ashore");
            else
            {
                if (!sawTransition) failures.Add("the coastal landing made no sea-to-land transition");
                if (GeneratedSpawnAssertions.Problem(main) != null) failures.Add("the party landed off land navigation: " + GeneratedSpawnAssertions.Problem(main));
                if (!anchor.IsValid || anchor.IsAtSettlement(village)) failures.Add("the coastal landing left no anchor at the coast");
                var aboard = new Dictionary<string, object>(); coast["reboarded"] = aboard;
                yield return Aboard(st, aboard, failures, "the coastal anchor");
            }
            row["after"] = RuntimeMissionProbe.Snapshot(main.IsCurrentlyAtSea);
        }

        // Back aboard as a click on the anchor orders it (MobileParty.SetMoveGoToInteractablePoint with all navigation); the anchor's
        // interaction sets sail at it.
        private static IEnumerator Aboard(State st, Dictionary<string, object> record, List<string> failures, string which)
        {
            var context = st.Context; var main = MobileParty.MainParty;
            var anchor = main.Anchor; var anchorAt = anchor.Position;
            main.SetMoveGoToInteractablePoint(anchor, MobileParty.NavigationType.All);
            DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
            double start = context.Seconds;
            while (!main.IsCurrentlyAtSea && context.Seconds - start < 60)
            {
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop && LandMissionsProbe.CurrentMenu() == "the map") DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                yield return null;
            }
            RuntimeMissionProbe.Pause();
            yield return LandMissionsProbe.Idle(st, 30);
            record["atSea"] = main.IsCurrentlyAtSea; record["fromAnchor"] = main.Position.ToVec2().Distance(anchorAt.ToVec2()); record["anchorValid"] = main.Anchor.IsValid;
            record["navigation"] = GeneratedSpawnAssertions.Problem(main);
            if (!main.IsCurrentlyAtSea) failures.Add(which + "'s interaction did not take the party aboard");
            else if (GeneratedSpawnAssertions.Problem(main) != null) failures.Add("the party re-embarked at " + which + " off naval navigation: " + GeneratedSpawnAssertions.Problem(main));
        }

        // ---------------------------------------------------------------- pirates (C06, W03)

        // MMR-35's pirate spawn positions (C06) as MMR-39's wrapper serves them to War Sails (W03), against what War Sails made of them: per
        // pirate clan (PiratesCampaignBehavior.IsPirateClan's rule), the plan's points, the served points (each on naval navigation), the patrol
        // zones (each a served point) and the inner scene's own points (the generated map scene has no PirateSpawnPoint entities, so 0 by
        // construction; the installed Calradia coordinates come from those entities); every pirate party at sea, on naval navigation, assigned a
        // served zone. A party's distance beyond its zone's radius is recorded, not checked: a patrolling party chases and flees.
        private static Dictionary<string, object> Pirates(State st)
        {
            var record = new Dictionary<string, object>();
            var wrapper = MNBL.ProceduralWorld.CampaignRegistry.GeneratedNavalMapSceneWrapper.Current;
            record["wrapper"] = wrapper != null; record["planPoints"] = wrapper == null ? 0 : wrapper.PlanCount; record["frameError"] = wrapper == null ? null : MNBL.ProceduralWorld.CampaignRegistry.GeneratedNavalMapSceneWrapper.FrameError;
            var behavior = Campaign.Current.GetCampaignBehavior<NavalDLC.CampaignBehaviors.PiratesCampaignBehavior>();
            var zones = behavior == null ? null : LandMissionsProbe.Field(behavior, "_patrolZones") as IDictionary;
            var assigned = behavior == null ? null : LandMissionsProbe.Field(behavior, "_assignedZones") as IDictionary;
            var clans = Clan.BanditFactions.Where(c => c.Culture != null && !c.Culture.CanHaveSettlement && c.HasNavalNavigationCapability && c.IsBanditFaction).OrderBy(c => c.StringId, StringComparer.Ordinal).ToList();
            var rows = new List<object>(); record["clans"] = rows;
            int planned = 0, served = 0, servedOff = 0, zoneCount = 0, matched = 0, scene = 0, parties = 0, unassigned = 0, beyond = 0, offNavigation = 0, notAtSea = 0, inServed = 0;
            foreach (var clan in clans)
            {
                int clanPlanned = wrapper == null ? 0 : wrapper.PlanCountFor(clan.StringId);
                var points = wrapper == null ? new List<(CampaignVec2, float)>() : wrapper.GetSpawnPoints(clan.StringId);
                int pointsOff = points.Count(q => GeneratedSpawnAssertions.Problem(q.Item1, MobileParty.NavigationType.Naval) != null);
                var fromScene = wrapper == null || wrapper.Inner == null ? 0 : wrapper.Inner.GetSpawnPoints(clan.StringId).Count;
                var clanZones = zones != null && zones.Contains(clan) ? ((IEnumerable)zones[clan]).Cast<object>().ToList() : new List<object>();
                int zoneMatches = clanZones.Count(z => { var zp = ((CampaignVec2)LandMissionsProbe.Field(z, "Position")).ToVec2(); return points.Any(q => q.Item1.ToVec2().Distance(zp) < 0.01f); });
                var members = MobileParty.AllBanditParties.Where(m => m.ActualClan == clan && m.IsActive).ToList();
                int memberUnassigned = 0, memberBeyond = 0, memberOff = 0, memberLand = 0, memberServed = 0;
                foreach (var m in members)
                {
                    if (!m.IsCurrentlyAtSea) memberLand++;
                    if (GeneratedSpawnAssertions.Problem(m) != null) memberOff++;
                    var zone = assigned != null && assigned.Contains(m) ? assigned[m] : null;
                    if (zone == null) { memberUnassigned++; continue; }
                    var zp = ((CampaignVec2)LandMissionsProbe.Field(zone, "Position")).ToVec2(); float radius = (float)LandMissionsProbe.Field(zone, "Radius");
                    if (points.Any(q => q.Item1.ToVec2().Distance(zp) < 0.01f)) memberServed++;
                    if (m.Position.ToVec2().Distance(zp) > radius) memberBeyond++;
                }
                planned += clanPlanned; served += points.Count; servedOff += pointsOff; zoneCount += clanZones.Count; matched += zoneMatches; scene += fromScene; parties += members.Count;
                unassigned += memberUnassigned; beyond += memberBeyond; offNavigation += memberOff; notAtSea += memberLand; inServed += memberServed;
                rows.Add(new Dictionary<string, object> { ["clan"] = clan.StringId, ["planned"] = clanPlanned, ["served"] = points.Count, ["servedOffNaval"] = pointsOff, ["fromScene"] = fromScene,
                    ["zones"] = clanZones.Count, ["zonesFromServed"] = zoneMatches, ["parties"] = members.Count, ["withShips"] = members.Count(m => m.Ships.Count > 0), ["notAtSea"] = memberLand,
                    ["offNavigation"] = memberOff, ["unassigned"] = memberUnassigned, ["beyondZoneRadius"] = memberBeyond, ["inServedZone"] = memberServed });
            }
            record["plannedForPirateClans"] = planned; record["served"] = served; record["servedOffNaval"] = servedOff; record["zones"] = zoneCount; record["zonesFromServed"] = matched; record["fromScene"] = scene;
            record["parties"] = parties; record["unassigned"] = unassigned; record["beyondZoneRadius"] = beyond; record["offNavigation"] = offNavigation; record["notAtSea"] = notAtSea;
            record["pirateClans"] = clans.Count; record["inServedZone"] = inServed;
            return record;
        }

        // A pirate party sent back to patrol its assigned zone, as PiratesCampaignBehavior sends it (SetMovePatrolAroundPoint with naval
        // navigation); "no zone" when it has none.
        private static string Patrol(MobileParty pirate)
        {
            var behavior = Campaign.Current.GetCampaignBehavior<NavalDLC.CampaignBehaviors.PiratesCampaignBehavior>();
            var assigned = behavior == null ? null : LandMissionsProbe.Field(behavior, "_assignedZones") as IDictionary;
            var zone = assigned != null && assigned.Contains(pirate) ? assigned[pirate] : null;
            if (zone == null) return "no zone";
            pirate.SetMovePatrolAroundPoint((CampaignVec2)LandMissionsProbe.Field(zone, "Position"), MobileParty.NavigationType.Naval);
            return "patrol";
        }

        // ---------------------------------------------------------------- naval battles

        // The party at sea meets a pirate party (moved beside it, recorded setup) through the campaign's encounter, its at-sea meeting (a
        // conversation mission) marked done as the encounter's own flag allows (PlayerEncounter.SetMeetingDone, then Init), the encounter menu's
        // attack: the naval battle mission on the patch's naval scene, deployment finished, every agent's spawn checked.
        //   retreat (pirate.battle): a few seconds, then the mission left (OnEndMissionResult, a retreat), the encounter left (the attacker's
        //     leave); the party back at sea within the naval return bound (1.25 x War Sails' 1.5 encounter radius), on naval navigation.
        //   defeat (naval.defeat): the party cut to a crew that all goes aboard, every agent of the player's side falls; the defeat menu's continue
        //     (the player taken prisoner); the escape forced; at sea without a seaworthy fleet the raft menus (War Sails' RaftStateCampaignBehavior),
        //     campaign time running until the raft washes ashore; the party on land, on land navigation.
        // The pirate goes back where it was found, patrolling its zone.
        private static IEnumerator PirateBattle(State st, Dictionary<string, object> row, bool lose, bool acrossSeam = false)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            if (!main.IsCurrentlyAtSea || main.Ships.Count == 0) throw new InvalidOperationException("the party is not at sea with its fleet (the previous step did not finish)");
            LandMissionsProbe.Heal(); setup["crewAdded"] = LandMissionsProbe.Crew(main, (int)st.Rule("crew"));
            // A defeat needs the player's whole side depleted: only the troops the ships carry fight, the rest keep the battle alive (run
            // 36684: the encounter menu after the mission, no capture). The party is cut to a crew that all goes aboard (recorded setup).
            if (lose)
            {
                int keep = (int)st.Rule("defeatTroops");
                foreach (var e in main.MemberRoster.GetTroopRoster().ToList())
                    if (!e.Character.IsHero && e.Number > 0) { int cut = Math.Max(0, e.Number - keep); if (cut > 0) main.MemberRoster.AddToCounts(e.Character, -cut); keep = Math.Max(0, keep - Math.Min(e.Number, keep)); }
                setup["playerTroops"] = main.MemberRoster.TotalManCount;
            }
            // The defeat's pirate is another party than the retreat's (by id).
            string earlier = lose ? Earlier(st, "pirate.battle", "pirate") : null;
            setup["excluded"] = earlier;
            var pirate = MobileParty.AllBanditParties.Where(m => m.IsActive && m.ActualClan != null && !m.ActualClan.Culture.CanHaveSettlement && m.ActualClan.HasNavalNavigationCapability
                    && m.IsCurrentlyAtSea && m.Ships.Count > 0 && m.MapEvent == null && m.MemberRoster.TotalHealthyCount >= 5 && m.StringId != earlier)
                .Where(m => !lose || m.MemberRoster.TotalHealthyCount >= 3 * main.MemberRoster.TotalManCount)
                .OrderBy(m => m.Position.ToVec2().Distance(main.Position.ToVec2())).ThenBy(m => m.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (pirate == null) throw new InvalidOperationException("no pirate party at sea with ships");
            var home = pirate.Position;
            setup["pirate"] = pirate.StringId; setup["clan"] = pirate.ActualClan.StringId; setup["pirateShips"] = pirate.Ships.Count; setup["pirateTroops"] = pirate.MemberRoster.TotalManCount;
            setup["pirateFrom"] = new[] { home.X, home.Y };
            var point = RuntimeMissionProbe.FindPoint(main.Position, MobileParty.NavigationType.Naval, 2, 1);
            if (acrossSeam)
            {
                // The party just inside the west edge of the canonical range, the pirate just inside the east edge: 1.5 apart across the seam.
                var world = PeriodicWorld.Current;
                if (world == null) throw new InvalidOperationException("no periodic world");
                var side = RuntimeMissionProbe.FindPoint(new CampaignVec2(new Vec2(world.OriginX + 0.75f, main.Position.Y), false), MobileParty.NavigationType.Naval, 0.5f, 0);
                main.Position = side; main.SetMoveModeHold();
                point = RuntimeMissionProbe.FindPoint(new CampaignVec2(new Vec2(world.MaxX - 0.75f, main.Position.Y), false), MobileParty.NavigationType.Naval, 0.5f, 0);
                setup["acrossSeam"] = new Dictionary<string, object> { ["party"] = new[] { side.X, side.Y }, ["pirate"] = new[] { point.X, point.Y },
                    ["periodic"] = world.Distance(side.ToVec2(), point.ToVec2()), ["planar"] = side.ToVec2().Distance(point.ToVec2()) };
                if (world.Distance(side.ToVec2(), point.ToVec2()) > 3 || side.ToVec2().Distance(point.ToVec2()) < world.Width / 2) failures.Add("the party and the pirate are not beside each other across the seam");
            }
            pirate.Position = point; pirate.SetMoveModeHold();
            setup["pirateMoved"] = home.ToVec2().Distance(point.ToVec2());
            try
            {
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["before"] = RuntimeMissionProbe.Snapshot(true);
                row["fleetBefore"] = Fleet(main);
                // EncounterManager.StartPartyEncounter's own steps (PlayerEncounter.Start, then its Init with the attacker and the defender), the
                // meeting marked done between them; Init is non-public.
                PlayerEncounter.Start();
                PlayerEncounter.SetMeetingDone();
                typeof(PlayerEncounter).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(PartyBase), typeof(PartyBase), typeof(Settlement) }, null)
                    .Invoke(PlayerEncounter.Current, new object[] { main.Party, pirate.Party, null });
                yield return LandMissionsProbe.Menu(st, "encounter");
                var battle = PlayerEncounter.Battle;
                row["mapEvent"] = new Dictionary<string, object> { ["naval"] = battle != null && battle.IsNavalMapEvent,
                    ["involved"] = battle == null ? null : battle.InvolvedParties.Select(x => x.MobileParty == null ? "settlement" : x.MobileParty.StringId).ToArray() };
                if (battle == null || !battle.IsNavalMapEvent) failures.Add("the encounter at sea is not a naval map event");
                RuntimeMissionProbe.RunOption("attack");
                var fight = new Dictionary<string, object>(); row["fight"] = fight;
                if (lose) yield return LandMissionsProbe.RunMission(st, row, true, m => LandMissionsProbe.Lose(st, m, fight, row), m => m.EndMission(), "mission", true);
                else yield return LandMissionsProbe.RunMission(st, row, true, m => LandMissionsProbe.GearAndSample(st, m, row), m => m.OnEndMissionResult(), "mission", true);
                if (!lose)
                {
                    yield return AfterBattle(st, row);
                    yield return RuntimeMissionProbe.SettleCamera(context);
                    row["after"] = RuntimeMissionProbe.Snapshot(true);
                    row["fleetAfter"] = Fleet(main);
                    RuntimeMissionProbe.CompareReturn(row, failures, true);
                    row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                    if (row["afterNavigation"] != null) failures.Add("the party returned off naval navigation: " + row["afterNavigation"]);
                }
                else
                {
                    PatchScene(row, failures);
                    yield return Captivity(st, row);
                }
            }
            finally
            {
                if (pirate.IsActive && pirate.MapEvent == null) { pirate.Position = home; setup["pirateRestored"] = Patrol(pirate); }
            }
        }

        // The party at sea sails across the seam: from a sea point 4 inside the east edge of the canonical range to one 4 inside the west
        // edge, on the sea row nearest the party that is navigable at 1, 4 and 8 inside both edges, as a map click on the sea orders it
        // (SetMoveGoToPoint, Naval); campaign time runs until it arrives. Its seam rebases (PeriodicSeam events), the periodic distance
        // travelled against the straight periodic distance, and every hourly sample on naval navigation and canonical.
        private static IEnumerator SeamVoyage(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            if (!main.IsCurrentlyAtSea || main.Ships.Count == 0) throw new InvalidOperationException("the party is not at sea with its fleet (the previous step did not finish)");
            var world = PeriodicWorld.Current;
            if (world == null) throw new InvalidOperationException("no periodic world");
            float y = SeaRow(world, main.Position.Y);
            if (float.IsNaN(y)) throw new InvalidOperationException("no sea row navigable on both sides of the seam");
            var from = RuntimeMissionProbe.FindPoint(new CampaignVec2(new Vec2(world.MaxX - 4, y), false), MobileParty.NavigationType.Naval, 1, 0);
            var to = RuntimeMissionProbe.FindPoint(new CampaignVec2(new Vec2(world.OriginX + 4, y), false), MobileParty.NavigationType.Naval, 1, 0);
            row["setup"] = new Dictionary<string, object> { ["row"] = y, ["movedFrom"] = new[] { main.Position.X, main.Position.Y }, ["from"] = new[] { from.X, from.Y }, ["to"] = new[] { to.X, to.Y } };
            main.Position = from; main.SetMoveModeHold();
            yield return RuntimeMissionProbe.SettleCamera(context);
            int events = PeriodicSeam.Events.Count;
            double hours = CampaignTime.Now.ToHours, next = hours, begin = context.Seconds;
            main.SetMoveGoToPoint(to, MobileParty.NavigationType.Naval);
            var samples = new List<object>(); row["samples"] = samples;
            int offNavigation = 0, nonCanonical = 0; float travelled = 0; var last = main.Position.ToVec2();
            DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
            while (world.Distance(main.Position.ToVec2(), to.ToVec2()) > 0.5f && context.Seconds - begin < st.Rule("voyageSeconds"))
            {
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop && LandMissionsProbe.CurrentMenu() == "the map") DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                var p = main.Position.ToVec2(); travelled += world.Distance(last, p); last = p;
                if (CampaignTime.Now.ToHours >= next)
                {
                    next += 1;
                    string problem = main.IsTransitionInProgress ? null : GeneratedSpawnAssertions.Problem(main);
                    if (problem != null) offNavigation++;
                    if (!world.IsCanonical(p.X)) nonCanonical++;
                    if (samples.Count < 100) samples.Add(new Dictionary<string, object> { ["hours"] = Math.Round(CampaignTime.Now.ToHours - hours, 1), ["position"] = new[] { p.X, p.Y }, ["atSea"] = main.IsCurrentlyAtSea, ["problem"] = problem });
                }
                if (LandMissionsProbe.CurrentMenu() != "the map") { row["menuOnTheWay"] = LandMissionsProbe.CurrentMenu(); break; }
                yield return null;
            }
            RuntimeMissionProbe.Pause();
            var crossings = PeriodicSeam.Events.Skip(events).Where(e => e.Party == main.StringId).ToList();
            bool arrived = world.Distance(main.Position.ToVec2(), to.ToVec2()) <= 0.5f;
            float straight = world.Distance(from.ToVec2(), to.ToVec2());
            row["voyage"] = new Dictionary<string, object> { ["arrived"] = arrived, ["hours"] = CampaignTime.Now.ToHours - hours, ["travelledPeriodic"] = travelled, ["straightPeriodic"] = straight,
                ["rebases"] = crossings.Count, ["rebaseShifts"] = crossings.Select(e => (object)e.Shift).ToList(), ["atSea"] = main.IsCurrentlyAtSea, ["offNavigationSamples"] = offNavigation, ["nonCanonicalSamples"] = nonCanonical };
            if (!arrived) failures.Add("the party did not arrive across the seam (" + LandMissionsProbe.CurrentMenu() + ")");
            if (crossings.Count == 0) failures.Add("the party did not cross the seam (no rebase)");
            if (travelled > 2 * straight + 5) failures.Add("the voyage went the long way (" + travelled + " for " + straight + ")");
            if (offNavigation > 0) failures.Add(offNavigation + " hourly samples off naval navigation");
            if (nonCanonical > 0) failures.Add(nonCanonical + " hourly samples outside the canonical range");
            if (!main.IsCurrentlyAtSea) failures.Add("the party is not at sea after the voyage");
        }

        // The sea row nearest y0 navigable at 1, 4 and 8 inside both edges of the canonical range (NaN when none within 600).
        private static float SeaRow(PeriodicWorld world, float y0)
        {
            var xs = new[] { world.MaxX - 8, world.MaxX - 4, world.MaxX - 1, world.OriginX + 1, world.OriginX + 4, world.OriginX + 8 };
            for (float dy = 0; dy < 600; dy += 0.5f)
                foreach (float y in new[] { y0 + dy, y0 - dy })
                    if (xs.All(x => GeneratedSpawnAssertions.Problem(new CampaignVec2(new Vec2(x, y), false), MobileParty.NavigationType.Naval) == null)) return y;
            return float.NaN;
        }

        // A value an earlier case's setup recorded, or null.
        private static string Earlier(State st, string caseName, string key)
        {
            object row, setup, value;
            if (!st.Cases.TryGetValue(caseName, out row) || !(row is Dictionary<string, object>) || !((Dictionary<string, object>)row).TryGetValue("setup", out setup) || !(setup is Dictionary<string, object>)) return null;
            return ((Dictionary<string, object>)setup).TryGetValue(key, out value) ? value as string : null;
        }

        // The defeat's mission against the patch's naval scene (the same rule CompareReturn applies to a returning battle): the scene
        // DefaultSceneModel.GetBattleSceneForMapPatch must open for the party's patch, by MMR-42's table.
        private static void PatchScene(Dictionary<string, object> row, List<string> failures)
        {
            var before = row.ContainsKey("before") ? row["before"] as Dictionary<string, object> : null;
            var mission = row.ContainsKey("mission") ? row["mission"] as Dictionary<string, object> : null;
            var expected = before == null || !before.ContainsKey("expectedBattleScene") ? null : before["expectedBattleScene"] as Dictionary<string, object>;
            if (expected == null || mission == null || !mission.ContainsKey("scene")) { failures.Add("the defeat's mission or its patch's expected scene is not recorded"); return; }
            row["expectedScene"] = expected["scene"]; row["sceneDeterministic"] = expected["deterministic"];
            row["sceneMatchesPatch"] = String.Equals(expected["scene"] as string, mission["scene"] as string, StringComparison.Ordinal);
            if (!Equals(expected["deterministic"], true)) failures.Add("the defeat's patch does not resolve to exactly one battle scene: " + expected["rule"]);
            if (!(bool)row["sceneMatchesPatch"]) failures.Add("the defeat opened " + mission["scene"] + ", not the patch's naval scene " + expected["scene"]);
        }

        // The party's fleet: each ship's hull and hit points, and the troops aboard the party (the crew).
        private static Dictionary<string, object> Fleet(MobileParty party)
        {
            return new Dictionary<string, object> { ["ships"] = party.Ships.Select(x => (object)new Dictionary<string, object> { ["hull"] = ((IShipOrigin)x).Hull.StringId, ["hitPoints"] = x.HitPoints, ["maxHitPoints"] = x.MaxHitPoints }).ToList(),
                ["troops"] = party.MemberRoster.TotalManCount, ["healthy"] = party.MemberRoster.TotalHealthyCount, ["anchorValid"] = party.Anchor.IsValid };
        }

        // After a naval battle the party left: the encounter's menus passed as the player passes them (the attacker's leave, the disengage's
        // continue, a loot screen closed), until the map is idle.
        private static IEnumerator AfterBattle(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var menus = new List<string>(); row["menusAfterBattle"] = menus;
            double start = context.Seconds;
            while (context.Seconds - start < 30)
            {
                string menu = LandMissionsProbe.CurrentMenu();
                if (menus.Count == 0 || menus[menus.Count - 1] != menu) menus.Add(menu);
                CloseScreens(row);
                if (menu == "encounter") { LandMissionsProbe.RecordMenu(st); RuntimeMissionProbe.RunOption("leave"); }
                else if (menu == "naval_encounter_disengaged") { LandMissionsProbe.RecordMenu(st); RuntimeMissionProbe.RunOption("naval_encounter_disengaged_continue"); }
                else if (menu == "the map" && PlayerEncounter.Current == null) break;
                yield return TwoWorldMatrixProbe.Frames(3);
            }
            yield return LandMissionsProbe.Idle(st, 30);
        }

        // The defeat's campaign path: the defeat menu's continue (taken_prisoner_continue) until the player is a prisoner; the escape forced
        // (EndCaptivityAction.ApplyByEscape, where captivity would otherwise wait on its daily escape chance); the raft menus, campaign time
        // running until the raft washes ashore (player_raft_state_wait's tick), the end menu's continue (it gives back the ships the raft
        // started with: none when the defeat lost them all).
        private static IEnumerator Captivity(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var menus = new List<string>(); row["menusAfterDefeat"] = menus;
            int shipsBefore = main.Ships.Count;
            double start = context.Seconds;
            while (!Hero.MainHero.IsPrisoner && context.Seconds - start < 60)
            {
                string menu = LandMissionsProbe.CurrentMenu();
                if (menus.Count == 0 || menus[menus.Count - 1] != menu) { menus.Add(menu); LandMissionsProbe.RecordMenu(st); }
                if ((menu == "defeated_and_taken_prisoner" || menu == "taken_prisoner") && LandMissionsProbe.FirstEnabled("taken_prisoner_continue") != null) RuntimeMissionProbe.RunOption("taken_prisoner_continue");
                CloseScreens(row);
                yield return TwoWorldMatrixProbe.Frames(3);
            }
            row["captured"] = Hero.MainHero.IsPrisoner;
            row["fleetAfterDefeat"] = Fleet(main);
            var captor = Hero.MainHero.PartyBelongedToAsPrisoner;
            row["captor"] = captor == null ? null : captor.MobileParty != null ? captor.MobileParty.StringId : captor.Settlement != null ? captor.Settlement.StringId : "?";
            row["captorAtSea"] = captor != null && captor.MobileParty != null && captor.MobileParty.IsCurrentlyAtSea;
            row["shipsAfterDefeat"] = main.Ships.Count;
            if (!Hero.MainHero.IsPrisoner) throw new InvalidOperationException("the naval defeat did not lead to capture (menus " + String.Join(" > ", menus) + ")");
            RuntimeMissionProbe.Pause();
            yield return TwoWorldMatrixProbe.Frames(10);
            var released = captor == null ? main.Position.ToVec2() : captor.IsMobile ? captor.MobileParty.Position.ToVec2() : captor.Settlement.GatePosition.ToVec2();
            row["releasedAt"] = new[] { released.X, released.Y };
            EndCaptivityAction.ApplyByEscape(Hero.MainHero, null, false);
            yield return TwoWorldMatrixProbe.Frames(5);
            row["afterEscape"] = new Dictionary<string, object> { ["prisoner"] = Hero.MainHero.IsPrisoner, ["atSea"] = main.IsCurrentlyAtSea, ["raft"] = main.IsInRaftState, ["ships"] = main.Ships.Count, ["menu"] = LandMissionsProbe.CurrentMenu() };
            // The raft: its menus' continue; the wait menu runs campaign time itself; on land, the end menu's continue.
            var raftMenus = new List<string>(); row["raftMenus"] = raftMenus;
            start = context.Seconds; var landing = main.Position.ToVec2(); bool sawRaft = false;
            while (context.Seconds - start < st.Rule("raftSeconds"))
            {
                string menu = LandMissionsProbe.CurrentMenu();
                if (raftMenus.Count == 0 || raftMenus[raftMenus.Count - 1] != menu) { raftMenus.Add(menu); LandMissionsProbe.RecordMenu(st); }
                if (main.IsInRaftState) sawRaft = true;
                if (menu == "player_raft_state" || menu == "player_raft_state_after_prisoner") { landing = main.Position.ToVec2(); RuntimeMissionProbe.RunOption("continue"); }
                else if (menu == "player_raft_state_end") { RuntimeMissionProbe.RunOption("continue"); }
                // The raft's wait menu starts campaign time itself (GameMenu.StartWait), which the probe's time lock refuses (run 42652: the raft
                // never moved); time runs as the wait menu would run it.
                else if (menu == "player_raft_state_wait" && Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                else if (menu == "the map" && !main.IsCurrentlyAtSea && !main.IsInRaftState && !Hero.MainHero.IsPrisoner) break;
                else if (menu == "the map" && Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                yield return TwoWorldMatrixProbe.Frames(3);
            }
            RuntimeMissionProbe.Pause();
            yield return LandMissionsProbe.Idle(st, 30);
            row["raft"] = new Dictionary<string, object> { ["sawRaft"] = sawRaft, ["raftAtEnd"] = main.IsInRaftState, ["atSea"] = main.IsCurrentlyAtSea, ["ships"] = main.Ships.Count, ["shipsBefore"] = shipsBefore,
                ["anchorValid"] = main.Anchor.IsValid, ["ashoreFrom"] = main.Position.ToVec2().Distance(landing) };
            row["escaped"] = !Hero.MainHero.IsPrisoner && main.IsActive;
            row["after"] = RuntimeMissionProbe.Snapshot(false);
            row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
            if (main.IsCurrentlyAtSea || main.IsInRaftState) failures.Add("the party did not wash ashore after the defeat");
            if (row["afterNavigation"] != null) failures.Add("the party washed ashore off land navigation: " + row["afterNavigation"]);
        }

        // A victory's loot screens, closed as the player cancels them (nothing taken): the party screen (prisoners, troops:
        // PartyScreenHelper.CloseScreen forced, from cancel), the inventory (InventoryScreenHelper.CloseScreen from cancel) and War Sails' port
        // screen in its loot mode (captured ships: popped). Each closed screen is counted.
        private static void CloseScreens(Dictionary<string, object> row)
        {
            var active = Game.Current.GameStateManager.ActiveState;
            if (active == null || active is MapState || active is TaleWorlds.MountAndBlade.MissionState) return;
            string name = active.GetType().Name;
            try
            {
                if (active is InventoryState) Helpers.InventoryScreenHelper.CloseScreen(true);
                else if (active is PartyState) Helpers.PartyScreenHelper.CloseScreen(true, true);
                else if (name == "PortState") Game.Current.GameStateManager.PopState(0);
                else return;
            }
            catch (Exception e) { row["screenCloseError"] = name + ": " + e.GetType().Name + ": " + e.Message; return; }
            var closed = row.ContainsKey("screensClosed") ? (List<string>)row["screensClosed"] : null;
            if (closed == null) row["screensClosed"] = closed = new List<string>();
            closed.Add(name);
        }

        // ---------------------------------------------------------------- the seaborne raid

        // A harbour village of another kingdom (war declared for the raid, peace made after, recorded): the voyage to its port, its menu (the
        // party at sea in the village), the hostile action's raid (village_hostile_action raid_village), the encounter's attack: the naval troop
        // selection answered with the troops and ships it offers, the naval raid mission on the village's center scene in its naval_raid level
        // (deployment finished, every spawn checked, the landing tags counted, the landing run), its defenders killed by the probe, the mission
        // left; then the raid's wait menu with campaign time running until the village is looted, its end menu (with the map events that ended
        // at the village, recorded as VillageHostileActionCampaignBehavior.OnMapEventEnded reads them); the party still at sea, by the port.
        private static IEnumerator Raid(State st, Dictionary<string, object> row, Voyage v)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            if (!main.IsCurrentlyAtSea || main.Ships.Count == 0) throw new InvalidOperationException("the party is not at sea with its fleet (the previous step did not finish)");
            var from = main.Position;
            var distance = Campaign.Current.Models.MapDistanceModel;
            Func<Settlement, bool> rule = s => s.IsVillage && s.HasPort && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && s.MapFaction is Kingdom && s.MapFaction != main.MapFaction
                && s.Village.VillageState == Village.VillageStates.Normal && s.SiegeEvent == null && s != v.Village && s.MapFaction != v.Island.MapFaction && s.MapFaction != v.Start.MapFaction && s.MapFaction != v.Mainland.MapFaction
                && distance.PathExistBetweenPoints(in from, s.PortPosition, MobileParty.NavigationType.Naval);
            var village = Pinned(v, "raidVillage", rule, () => Settlement.All.Where(rule).OrderBy(s => s.PortPosition.ToVec2().Distance(from.ToVec2())).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault());
            if (village == null) throw new InvalidOperationException("no harbour village of another kingdom reachable by sea");
            row["village"] = village.StringId; row["expectedScene"] = village.LocationComplex == null ? null : village.LocationComplex.GetScene("village_center", 1);
            var war = village.MapFaction;
            if (!war.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, war); setup["warDeclared"] = war.StringId; }
            LandMissionsProbe.Heal(); setup["crewAdded"] = LandMissionsProbe.Crew(main, (int)st.Rule("crew"));
            setup["shallowShips"] = main.Ships.Count(s => ((IShipOrigin)s).Hull.CanNavigateShallowWater);
            var ended = new List<object>(); row["mapEventsEnded"] = ended; var owner = new object();
            CampaignEvents.MapEventEnded.AddNonSerializedListener(owner, e =>
            {
                var defender = e.GetMapEventSide(BattleSideEnum.Defender); var attacker = e.GetMapEventSide(BattleSideEnum.Attacker);
                var settlement = defender == null || defender.LeaderParty == null ? null : defender.LeaderParty.Settlement;
                if (settlement != village && e.MapEventSettlement != village) return;
                var leader = attacker == null || attacker.LeaderParty == null ? null : attacker.LeaderParty.MobileParty;
                ended.Add(new Dictionary<string, object> { ["type"] = e.EventType.ToString(), ["isRaid"] = e.IsRaid, ["playerMapEvent"] = e.IsPlayerMapEvent, ["naval"] = e.IsNavalMapEvent,
                    ["attackerLeader"] = leader == null ? null : leader.StringId, ["attackerLeaderIsMain"] = leader == MobileParty.MainParty, ["defenderLeaderIsVillage"] = settlement == village,
                    ["villageRaided"] = village.IsRaided, ["villageState"] = village.Village.VillageState.ToString(), ["diplomaticallyFinished"] = e.DiplomaticallyFinished,
                    ["endedByRetreat"] = e.EndedByRetreat, ["winner"] = e.WinningSide.ToString() });
            });
            try
            {
                yield return SailTo(st, row, village);
                yield return LandMissionsProbe.Menu(st, "village");
                row["arrival"] = new Dictionary<string, object> { ["atSea"] = main.IsCurrentlyAtSea, ["settlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId };
                RuntimeMissionProbe.RunOption("hostile_action");
                yield return LandMissionsProbe.Menu(st, "village_hostile_action");
                RuntimeMissionProbe.RunOption("raid_village");
                yield return LandMissionsProbe.Menu(st, "encounter");
                if (LandMissionsProbe.FirstEnabled("village_raid_action") != null) { setup["noDefenders"] = true; RuntimeMissionProbe.RunOption("village_raid_action"); }
                else
                {
                    var answer = LandMissionsProbe.TroopSelectionAnswer.Install(setup, true);
                    try { RuntimeMissionProbe.RunOption("attack"); }
                    finally { answer.Remove(); }
                    if (!setup.ContainsKey("navalTroopSelection")) throw new InvalidOperationException("the raid's attack opened no naval troop selection");
                    var fight = new Dictionary<string, object>(); row["fight"] = fight;
                    yield return LandMissionsProbe.RunMission(st, row, true, m => Defeat(st, m, fight, row), m => m.EndMission(), "mission", false);
                }
                // The raid: its wait menu runs campaign time until the village is looted, then its end menu.
                var menus = new List<string>(); row["menusAfterRaid"] = menus;
                double start = context.Seconds;
                while (context.Seconds - start < st.Rule("raidSeconds"))
                {
                    string menu = LandMissionsProbe.CurrentMenu();
                    if (menus.Count == 0 || menus[menus.Count - 1] != menu) { menus.Add(menu); LandMissionsProbe.RecordMenu(st); }
                    // The raid's end: VillageHostileActionCampaignBehavior.OnMapEventEnded shows village_player_raid_ended for a raid map event of the
                    // player's whose attacker leader is the main party with the village raided, village_raid_ended_leaded_by_someone_else otherwise
                    // (run 43188, after the naval raid; the ended map events say which condition did not hold); both continue.
                    if (menu == "village_player_raid_ended" || menu == "village_raid_ended_leaded_by_someone_else") { row["raidEndMenu"] = menu; RuntimeMissionProbe.RunOption("continue"); }
                    else if (menu == "the map" && PlayerEncounter.Current == null) break;
                    else if (menu == "raiding_village" && Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                    else CloseScreens(row);
                    yield return TwoWorldMatrixProbe.Frames(3);
                }
                RuntimeMissionProbe.Pause();
                yield return LandMissionsProbe.Idle(st, 30);
                row["villageState"] = village.Village.VillageState.ToString();
                row["after"] = RuntimeMissionProbe.Snapshot(true);
                row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                row["atSeaAfter"] = main.IsCurrentlyAtSea;
                if (!main.IsCurrentlyAtSea) failures.Add("the party left the seaborne raid off the sea");
                if (row["afterNavigation"] != null) failures.Add("the party left the raid off naval navigation: " + row["afterNavigation"]);
                if (village.Village.VillageState != Village.VillageStates.Looted) failures.Add("the village is " + village.Village.VillageState + ", not looted, after the raid");
                LandMissionsProbe.ReturnTo(st, row, failures, village.PortPosition.ToVec2(), "the village's port, the raid's sea side", st.Rule("returnBoundNaval"), true);
                var mission = row.ContainsKey("mission") ? (Dictionary<string, object>)row["mission"] : null;
                if (mission != null && !String.Equals((string)mission["scene"], (string)row["expectedScene"], StringComparison.Ordinal)) failures.Add("the raid opened " + mission["scene"] + ", not the village's center scene " + row["expectedScene"]);
            }
            finally
            {
                CampaignEvents.MapEventEnded.ClearListeners(owner);
                if (setup.ContainsKey("warDeclared") && war.IsAtWarWith(Clan.PlayerClan)) { MakePeaceAction.Apply(Clan.PlayerClan, war); setup["peaceMade"] = !war.IsAtWarWith(Clan.PlayerClan); }
            }
        }

        // In the raid mission: the scene's naval raid setup counted (its landing, jumping and player spawn tags), the landing as War Sails' raid
        // controller runs it (NavalRaidMissionController: the ships sail the scene's landing paths, ground and the troops land; its
        // _hasLandingStarted and _hasLandingCompleted, with the paths it read, _landingFrames), the player's agents then counted ashore or on a
        // deck (AgentNavalComponent.SteppedShip), then the defenders fall (each half second, killed by the probe in the player's agent's name)
        // until the mission's result is the player's victory. The landing comes first: run 45664's defenders fell while the boats were still
        // offshore.
        private static IEnumerator Defeat(State st, Mission m, Dictionary<string, object> fight, Dictionary<string, object> row)
        {
            var controller = m.GetMissionBehavior<NavalDLC.Missions.MissionLogics.NavalRaidMissionController>();
            var landing = new Dictionary<string, object> { ["controller"] = controller != null }; fight["landing"] = landing;
            if (controller != null)
            {
                var frames = LandMissionsProbe.Field(controller, "_landingFrames") as MatrixFrame[][];
                landing["paths"] = frames == null ? 0 : frames.Length; landing["pathFrames"] = frames == null ? null : frames.Select(f => (object)(f == null ? 0 : f.Length)).ToList();
                var jumping = LandMissionsProbe.Field(controller, "_jumpingFrames") as MatrixFrame[];
                landing["jumpingFrames"] = jumping == null ? 0 : jumping.Length;
                double wait = st.Context.Seconds;
                while (Mission.Current == m && m.MissionResult == null && !Equals(LandMissionsProbe.Field(controller, "_hasLandingCompleted"), true) && st.Context.Seconds - wait < st.Rule("landingSeconds"))
                {
                    if (!landing.ContainsKey("startedAfter") && Equals(LandMissionsProbe.Field(controller, "_hasLandingStarted"), true)) { landing["startedAfter"] = st.Context.Seconds - wait; st.Context.Checkpoint(); }
                    yield return null;
                }
                landing["started"] = Equals(LandMissionsProbe.Field(controller, "_hasLandingStarted"), true);
                landing["completed"] = Equals(LandMissionsProbe.Field(controller, "_hasLandingCompleted"), true);
                landing["seconds"] = st.Context.Seconds - wait;
                // Ashore or aboard as War Sails tracks it: an agent on a ship's deck has that ship as its naval component's SteppedShip.
                int ashore = 0, aboard = 0, untracked = 0;
                foreach (var a in m.PlayerTeam == null ? new List<Agent>() : m.PlayerTeam.ActiveAgents.ToList())
                {
                    if (!a.IsHuman) continue;
                    var naval = a.GetComponent<NavalDLC.Missions.AgentNavalComponent>();
                    if (naval == null) untracked++;
                    else if (naval.SteppedShip != null) aboard++;
                    else ashore++;
                }
                landing["playerAgentsAshore"] = ashore; landing["playerAgentsAboard"] = aboard; landing["playerAgentsUntracked"] = untracked;
            }
            // As NavalRaidMissionController finds them: the landing paths (tag expression landing(_\d+)*, landing_00<ship> per path frame), the
            // jumping zones and the defender player's spawn frame.
            var tags = new Dictionary<string, object>(); fight["sceneTags"] = tags;
            tags["landing"] = m.Scene.FindEntitiesWithTagExpression("landing(_\\d+)*").Count();
            tags["jumping"] = m.Scene.FindEntitiesWithTagExpression("jumping(_\\d+)*").Count();
            tags["playerSpawnFrame"] = m.Scene.FindEntitiesWithTag("player_spawn_frame").Count();
            fight["sceneLevels"] = m.SceneLevels;
            row["gear"] = LandMissionsProbe.Gear(st, m);
            // The player's agent after the landing: War Sails' landing can leave it down (run 42168: no main agent, so no kill, and the mission
            // ended 6 s later without a result). The killer is then any active agent of the player's team.
            fight["mainAgentAfterLanding"] = m.MainAgent == null ? "none" : m.MainAgent.IsActive() ? "active" : m.MainAgent.State.ToString();
            int killed = 0; double start = st.Context.Seconds, last = 0;
            while (st.Context.Seconds - start < 120 && Mission.Current == m)
            {
                if (m.MissionResult != null) break;
                if (st.Context.Seconds - last > 0.5 && m.PlayerEnemyTeam != null)
                {
                    last = st.Context.Seconds;
                    var by = m.MainAgent != null && m.MainAgent.IsActive() ? m.MainAgent
                        : m.PlayerTeam == null ? null : m.PlayerTeam.ActiveAgents.FirstOrDefault(x => x.IsHuman && x.IsActive());
                    foreach (var a in m.PlayerEnemyTeam.ActiveAgents.ToList()) if (a.IsHuman && a.IsActive()) { LandMissionsProbe.Kill(a, by); killed++; }
                }
                yield return null;
            }
            fight["killed"] = killed; fight["seconds"] = st.Context.Seconds - start;
            fight["victory"] = m.MissionResult != null && m.MissionResult.PlayerVictory;
        }

        // ---------------------------------------------------------------- the blockade

        // A port town of another kingdom besieged from land by the party owning its fleet (a siege cannot start from the sea: naval_town_outside
        // has no besiege): war declared and the party moved to the town (recorded setup: where from, how far), the outside menu's besiege; War
        // Sails' SiegeEvent activates the blockade when a besieger has ships. Its placement: the town's BlockadePositionScript arcs (SandBox's map
        // script on the settlement's strategic entity, built from MMR-40's arc entities) with the fleet's ships, every point on naval
        // navigation; the player's anchor disabled. Then the siege lifted through the menu: the blockade deactivated, the anchor enabled; the
        // party moved back to where it was found (recorded).
        private static IEnumerator Blockade(State st, Dictionary<string, object> row, Voyage v)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            if (main.Ships.Count == 0 || main.IsCurrentlyAtSea) throw new InvalidOperationException("the party is not on land with a fleet (the previous step did not finish)");
            Func<Settlement, bool> rule = s => s.IsTown && s.HasPort && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && s.MapFaction is Kingdom && s.SiegeEvent == null
                && s != v.Start && s != v.Mainland && s != v.Island && s.MapFaction != v.Island.MapFaction && s.MapFaction != main.MapFaction
                && GeneratedSpawnAssertions.Problem(s.GatePosition, MobileParty.NavigationType.Default) == null;
            var target = Pinned(v, "blockadeTown", rule, () => Settlement.All.Where(rule).OrderBy(s => s.GatePosition.ToVec2().Distance(main.Position.ToVec2())).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault());
            if (target == null) throw new InvalidOperationException("no port town of another kingdom to besiege");
            row["settlement"] = target.StringId;
            var war = target.MapFaction;
            if (!war.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, war); setup["warDeclared"] = war.StringId; }
            int activated = 0, deactivated = 0; var owner = new object();
            CampaignEvents.OnBlockadeActivatedEvent.AddNonSerializedListener(owner, e => { if (e.BesiegedSettlement == target) activated++; });
            CampaignEvents.OnBlockadeDeactivatedEvent.AddNonSerializedListener(owner, e => { if (e.BesiegedSettlement == target) deactivated++; });
            var found = main.Position; bool moved = false;
            try
            {
                var point = RuntimeMissionProbe.FindPoint(target.GatePosition, MobileParty.NavigationType.Default, 2, 1);
                setup["relocatedFrom"] = new Dictionary<string, object> { ["position"] = new[] { found.X, found.Y }, ["distance"] = found.ToVec2().Distance(point.ToVec2()) };
                main.SetMoveModeHold(); main.Position = point; moved = true;
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["before"] = RuntimeMissionProbe.Snapshot(false);
                EncounterManager.StartSettlementEncounter(main, target);
                double start = context.Seconds;
                while (LandMissionsProbe.CurrentMenu() != "town_outside" && context.Seconds - start < 30) yield return null;
                LandMissionsProbe.RecordMenu(st);
                setup["outsideMenu"] = LandMissionsProbe.CurrentMenu();
                RuntimeMissionProbe.RunOption("town_besiege");
                yield return LandMissionsProbe.Menu(st, "menu_siege_strategies");
                yield return TwoWorldMatrixProbe.Frames(30);
                var siege = target.SiegeEvent;
                var blockading = siege == null ? new List<MobileParty>() : siege.BesiegerCamp.GetInvolvedPartiesForEventType(MapEvent.BattleTypes.BlockadeBattle).Select(x => x.MobileParty).Where(x => x != null).ToList();
                int ships = blockading.Sum(x => x.Ships.Count);
                row["blockade"] = new Dictionary<string, object> { ["siege"] = siege != null, ["active"] = siege != null && siege.IsBlockadeActive, ["activatedEvents"] = activated, ["anchorDisabled"] = main.Anchor.IsDisabled,
                    ["blockadingParties"] = blockading.Select(x => (object)x.StringId).ToList(), ["ships"] = ships, ["arcs"] = Arcs(target, ships) };
                if (siege == null || !siege.IsBlockadeActive) failures.Add("the siege of a port town by a party with ships did not activate a blockade");
                if (!main.Anchor.IsDisabled) failures.Add("the player's anchor is not disabled while blockading");
                // Lifted through the menu: leave, then break the siege.
                RuntimeMissionProbe.RunOption("menu_siege_strategies_leave");
                start = context.Seconds;
                while (LandMissionsProbe.CurrentMenu() != "menu_siege_strategies_break_siege" && LandMissionsProbe.CurrentMenu() != "the map" && context.Seconds - start < 10) yield return null;
                if (LandMissionsProbe.CurrentMenu() == "menu_siege_strategies_break_siege") { LandMissionsProbe.RecordMenu(st); RuntimeMissionProbe.RunOption("menu_siege_strategies_break_siege_go_on"); }
                yield return LandMissionsProbe.Idle(st, 30);
                row["lifted"] = new Dictionary<string, object> { ["siege"] = target.SiegeEvent != null, ["deactivatedEvents"] = deactivated, ["anchorDisabled"] = main.Anchor.IsDisabled };
                if (target.SiegeEvent != null) failures.Add("the siege was not lifted");
                if (main.Anchor.IsDisabled) failures.Add("the anchor stayed disabled after the blockade");
                row["after"] = RuntimeMissionProbe.Snapshot(false);
                row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                if (row["afterNavigation"] != null) failures.Add("the party left the siege off land navigation: " + row["afterNavigation"]);
            }
            finally
            {
                CampaignEvents.OnBlockadeActivatedEvent.ClearListeners(owner); CampaignEvents.OnBlockadeDeactivatedEvent.ClearListeners(owner);
                if (setup.ContainsKey("warDeclared") && war.IsAtWarWith(Clan.PlayerClan)) { MakePeaceAction.Apply(Clan.PlayerClan, war); setup["peaceMade"] = !war.IsAtWarWith(Clan.PlayerClan); }
                if (moved && main.MapEvent == null && main.SiegeEvent == null && main.CurrentSettlement == null)
                {
                    main.SetMoveModeHold(); main.Position = found;
                    setup["movedBack"] = new Dictionary<string, object> { ["distance"] = main.Position.ToVec2().Distance(found.ToVec2()), ["navigation"] = GeneratedSpawnAssertions.Problem(main) };
                }
            }
        }

        // The town's blockade arcs as the map visual lays them (BlockadePositionScript.GetBlockadeArc for the blockading ships, read through
        // reflection: SandBox.View is not a compile reference), each point's navigation.
        private static Dictionary<string, object> Arcs(Settlement town, int ships)
        {
            var record = new Dictionary<string, object>();
            try
            {
                var managerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SandBox.View.Map.Managers.SettlementVisualManager", false)).FirstOrDefault(t => t != null);
                var scriptType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SandBox.View.Map.BlockadePositionScript", false)).FirstOrDefault(t => t != null);
                var manager = managerType == null ? null : managerType.GetProperty("Current").GetValue(null);
                var visual = manager == null ? null : managerType.GetMethod("GetSettlementVisual").Invoke(manager, new object[] { town });
                var entity = visual == null ? null : visual.GetType().GetProperty("StrategicEntity").GetValue(visual) as TaleWorlds.Engine.GameEntity;
                var script = entity == null || scriptType == null ? null : typeof(TaleWorlds.Engine.GameEntity).GetMethod("GetFirstScriptOfType").MakeGenericMethod(scriptType).Invoke(entity, null);
                record["script"] = script != null;
                if (script == null) return record;
                var args = new object[] { Math.Max(1, ships), default(Vec3) };
                var arcs = (IEnumerable)scriptType.GetMethod("GetBlockadeArc").Invoke(script, args);
                int points = 0, off = 0; var samples = new List<object>();
                foreach (IEnumerable arc in arcs)
                    foreach (Vec3 q in arc)
                    {
                        points++;
                        string problem = GeneratedSpawnAssertions.Problem(new CampaignVec2(q.AsVec2, false), MobileParty.NavigationType.Naval);
                        if (problem != null) { off++; if (samples.Count < 5) samples.Add(new[] { q.X, q.Y }); }
                    }
                var center = (Vec3)args[1];
                record["points"] = points; record["offNaval"] = off; record["offSamples"] = samples; record["center"] = new[] { center.X, center.Y };
                // The full arcs: as many ships as the script lays (a request beyond its MaximumNumberOfShips is capped), so the whole blockade
                // geometry it draws is checked; the points laid are recorded (run 45664: 10).
                var full = new object[] { 64, default(Vec3) };
                int fullPoints = 0, fullOff = 0;
                foreach (IEnumerable arc in (IEnumerable)scriptType.GetMethod("GetBlockadeArc").Invoke(script, full))
                    foreach (Vec3 q in arc) { fullPoints++; if (GeneratedSpawnAssertions.Problem(new CampaignVec2(q.AsVec2, false), MobileParty.NavigationType.Naval) != null) fullOff++; }
                record["fullPoints"] = fullPoints; record["fullOffNaval"] = fullOff;
                record["fromPort"] = center.AsVec2.Distance(town.PortPosition.ToVec2());
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        // ---------------------------------------------------------------- AI traffic

        // Every land/sea transition of an AI party while the cases run (the event War Sails raises from MobileParty.IsCurrentlyAtSea's setter and
        // the end of a transition), with the party's kind; an hour later, the party's position's navigation for its domain then (a party created
        // at sea raises the event before its position is set: run 27796's fishing villagers); and the arrivals from the sea at port towns: a
        // landing and the party's entry into a port town in the same tick (EnterSettlementAction.ApplyForParty sets IsCurrentlyAtSea false, then
        // the settlement), counted apart for the island port towns.
        private sealed class Traffic
        {
            private readonly State st;
            private readonly object owner = new object();
            public readonly Dictionary<string, object> Record = new Dictionary<string, object>();
            public HashSet<Settlement> Islands = new HashSet<Settlement>();
            private readonly SortedDictionary<string, int> embarked = new SortedDictionary<string, int>(StringComparer.Ordinal), disembarked = new SortedDictionary<string, int>(StringComparer.Ordinal);
            private readonly SortedDictionary<string, int> arrivalsByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
            private readonly List<object> problems = new List<object>();
            private readonly Dictionary<MobileParty, double> justLanded = new Dictionary<MobileParty, double>();
            private readonly HashSet<Settlement> islandsReached = new HashSet<Settlement>();
            private readonly List<(MobileParty, double)> pending = new List<(MobileParty, double)>();
            private int portArrivals, islandArrivals, entriesWithoutLanding, checkedCount, problemCount;

            public Traffic(State st)
            {
                this.st = st;
                Record["embarked"] = embarked; Record["disembarked"] = disembarked; Record["problems"] = problems; Record["arrivalsByKind"] = arrivalsByKind;
                CampaignEvents.OnMobilePartyNavigationStateChangedEvent.AddNonSerializedListener(owner, Changed);
                CampaignEvents.SettlementEntered.AddNonSerializedListener(owner, Entered);
                CampaignEvents.HourlyTickEvent.AddNonSerializedListener(owner, Hourly);
                Record["atSeaAtStart"] = MobileParty.All.Count(p => p.IsActive && p.IsCurrentlyAtSea);
            }

            private void Changed(MobileParty party)
            {
                if (party == null || party.IsMainParty) return;
                string kind = GeneratedSpawnAssertions.KindOf(party);
                var counts = party.IsCurrentlyAtSea ? embarked : disembarked;
                int n; counts.TryGetValue(kind, out n); counts[kind] = n + 1;
                if (!party.IsCurrentlyAtSea) justLanded[party] = CampaignTime.Now.ToHours;
                pending.Add((party, CampaignTime.Now.ToHours));
            }

            private void Hourly()
            {
                double now = CampaignTime.Now.ToHours;
                foreach (var stale in justLanded.Where(k => now - k.Value > 1).Select(k => k.Key).ToList()) justLanded.Remove(stale);
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var (party, at) = pending[i];
                    if (now - at < 1) continue;
                    pending.RemoveAt(i);
                    if (!party.IsActive || party.CurrentSettlement != null || party.IsTransitionInProgress) continue;
                    checkedCount++;
                    string problem = GeneratedSpawnAssertions.Problem(party);
                    if (problem == null) continue;
                    problemCount++;
                    if (problems.Count < 20) problems.Add(new Dictionary<string, object> { ["party"] = party.StringId, ["kind"] = GeneratedSpawnAssertions.KindOf(party), ["atSea"] = party.IsCurrentlyAtSea, ["problem"] = problem });
                }
            }

            private void Entered(MobileParty party, Settlement settlement, Hero hero)
            {
                if (party == null || party.IsMainParty || settlement == null || !settlement.IsTown || !settlement.HasPort) return;
                double at;
                if (justLanded.TryGetValue(party, out at) && at == CampaignTime.Now.ToHours)
                {
                    portArrivals++;
                    string kind = GeneratedSpawnAssertions.KindOf(party); int n; arrivalsByKind.TryGetValue(kind, out n); arrivalsByKind[kind] = n + 1;
                    if (Islands.Contains(settlement)) { islandArrivals++; islandsReached.Add(settlement); }
                }
                else entriesWithoutLanding++;
                justLanded.Remove(party);
            }

            public void Stop()
            {
                CampaignEvents.OnMobilePartyNavigationStateChangedEvent.ClearListeners(owner);
                CampaignEvents.SettlementEntered.ClearListeners(owner);
                CampaignEvents.HourlyTickEvent.ClearListeners(owner);
                Record["checked"] = checkedCount; Record["problemCount"] = problemCount;
                Record["portArrivals"] = portArrivals; Record["islandArrivals"] = islandArrivals; Record["islandPortTowns"] = Islands.Count;
                Record["islandsReached"] = islandsReached.Select(s => (object)s.StringId).OrderBy(s => (string)s, StringComparer.Ordinal).ToList();
                Record["portEntriesOverLand"] = entriesWithoutLanding;
                Record["atSeaAtEnd"] = MobileParty.All.Count(p => p.IsActive && p.IsCurrentlyAtSea);
            }
        }
    }
}
