using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using HarmonyLib;
using Helpers;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-51: generated campaign gameplay in the bounded first test (campaign_soak; scenarios=<repo>/tests/Runtime/CampaignSoakScenarios.json,
    // which gives the worlds and rules; docs/campaign-behavior-compatibility.md):
    //   world A (a generated world, seed 42, from the world setup screen): its initial state audited and its semantic routes checked against
    //     the live campaign (CampaignPlayability); the player travels from its start town to the nearest other town reachable by land (the
    //     engine's settlement encounter on arrival), buys and sells a trade good through the trade screen's logic (InventoryLogic, as the town
    //     menu's trade option builds it) and recruits a volunteer (the recruitment screen's domain sequence), and leaves through the menu's
    //     leave path; one campaign day of natural simulation observed (party motion by kind, settlement entries, caravan trades, villagers'
    //     sales in their towns, recruitment, battles, unfound paths every frame, stuck and holding parties, navigation, exceptions from campaign
    //     code and the engine's asserts, food, gold and markets); volunteer replenishment after the day; then the diagnostic actions, each
    //     through an installed action: a declared war, a town's ownership change, a clan leader's death (succession) and a ruler's death; six
    //     more hours observed; the state audited again.
    //   world B (MMR-19's fixture world, after A ended in the same process): its fresh state audited for coherence and independence from A,
    //     its routes checked, then twelve hours observed.
    // Diagnostic setup (the player's actions and shelter, the war, the ownership change, the deaths) is recorded apart from natural behaviour
    // (what the installed behaviours did on their own). The probe's own pass lines are a subset: every expectation is recomputed by
    // tools/summarize_campaign_soak.py from the outputs, and that summary is the verdict.
    internal static class CampaignSoakProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private sealed class Soak
        {
            public ProbeContext Context;
            public List<string> Failures = new List<string>();
            public List<object> Checks = new List<object>();
            public Dictionary<string, object> Rules;
            public double Rule(string key) { return Convert.ToDouble(Rules[key], CultureInfo.InvariantCulture); }
            public void Expect(bool ok, string scenario, string what)
            {
                Checks.Add(new Dictionary<string, object> { ["scenario"] = scenario, ["ok"] = ok, ["what"] = what });
                if (!ok) Failures.Add(scenario + ": " + what);
                Context.Stage((ok ? "ok: " : "FAIL: ") + scenario + ": " + what);
            }
        }

        private static readonly AccessTools.FieldRef<MobileParty, bool> PathNotFound = AccessTools.FieldRefAccess<MobileParty, bool>("_aiPathNotFound");
        private static readonly string[] RuleKeys = { "stuckHours", "moveEpsilon", "movedDistance", "timeoutSeconds", "stallSeconds", "maxStalls" };

        private static IEnumerator Body(ProbeContext context)
        {
            string path = context.Str("scenarios", null);
            if (path == null || !File.Exists(path)) throw new InvalidOperationException("scenarios=<tests/Runtime/CampaignSoakScenarios.json> is required.");
            var bytes = File.ReadAllBytes(path);
            var plan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
            var worlds = (Dictionary<string, object>)plan["worlds"];
            var soak = new Soak { Context = context, Rules = (Dictionary<string, object>)plan["rules"] };
            var planA = (Dictionary<string, object>)worlds["A"]; var planB = (Dictionary<string, object>)worlds["B"];
            // Every input checked before any game starts (run 47176 lost world B to a missing path after A's nine minutes).
            string repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))));
            var missing = RuleKeys.Where(k => !soak.Rules.ContainsKey(k)).Select(k => "rules." + k)
                .Concat(new[] { "seeds", "culture", "observeHours", "afterDiagnosticHours", "movedFraction" }.Where(k => !planA.ContainsKey(k)).Select(k => "worlds.A." + k))
                .Concat(new[] { "fixtureSeed", "fixturePlacement", "catalogReceipt", "observeHours", "movedFraction" }.Where(k => !planB.ContainsKey(k)).Select(k => "worlds.B." + k)).ToList();
            if (missing.Count > 0) throw new InvalidOperationException("The scenario file lacks " + String.Join(", ", missing));
            // fixtureB=false (a package-only installation, which holds no fixture world): world B is recorded as blocked and not started.
            bool fixtureB = context.Bool("fixtureB", true);
            string placement = Path.Combine(repo, (string)planB["fixturePlacement"]), receipt = Path.Combine(repo, (string)planB["catalogReceipt"]);
            if (fixtureB) foreach (var file in new[] { placement, receipt }) if (!File.Exists(file)) throw new InvalidOperationException("World B's input is missing: " + file);
            context.Outputs["scope"] = "MMR-51: generated campaign gameplay on worlds A and B (the bounded first test).";
            context.Outputs["scenarioFile"] = new Dictionary<string, object> { ["schema"] = plan["schema"], ["sha256"] = GeneratedWorldPackage.Sha256(bytes) };
            context.Outputs["rules"] = soak.Rules;
            context.Outputs["worldPlans"] = worlds;
            context.Outputs["failures"] = soak.Failures;
            context.Outputs["checks"] = soak.Checks;
            var results = new Dictionary<string, object>(); context.Outputs["worlds"] = results;
            var baselines = new List<object>(); context.Outputs["baselines"] = baselines;
            Action<string> baseline = label =>
            {
                var held = WorldLifetimeScope.Held();
                baselines.Add(new Dictionary<string, object> { ["at"] = label, ["game"] = Game.Current != null, ["held"] = held.ToDictionary(k => k.Key, k => (object)k.Value), ["tracked"] = WorldLifetimeScope.Names.Count });
                soak.Expect(Game.Current == null && held.Count == 0, "baselines", label + ": no game and every tracked static at its baseline" + (held.Count == 0 ? "" : " (held: " + String.Join(", ", held.Keys) + ")"));
            };
            baseline("start");

            // ---- world A
            var a = new Dictionary<string, object>(); results["A"] = a;
            var seeds = ((IEnumerable)planA["seeds"]).Cast<object>().Select(o => Convert.ToUInt32(o, CultureInfo.InvariantCulture)).ToArray();
            yield return TwoWorldMatrixProbe.StartGenerated(context, a, "A", seeds, (string)planA["culture"], "campaign_soak");
            string prefixA = (string)a["prefix"];
            var initA = Audit(prefixA); a["init"] = initA;
            soak.Expect(Count(initA, "invalid") == 0 && Count(initA, "unexpected") == 0 && Count(initA, "offNavigation") == 0, "A.init",
                "a coherent initial state (" + Count(initA, "invalid") + " invalid references, " + Count(initA, "unexpected") + " unexpected objects, " + Count(initA, "offNavigation") + " parties off navigation)");
            a["playability"] = Playability((string)a["worldId"], null, context);
            context.Checkpoint();
            var visit = new Dictionary<string, object>(); a["visit"] = visit;
            yield return Visit(context, soak, prefixA, visit);
            var day = new Dictionary<string, object>(); a["day"] = day;
            var slotsAtStart = Slots();
            yield return Observe(context, soak, "A.day", day, Convert.ToDouble(planA["observeHours"], CultureInfo.InvariantCulture));
            a["replenishment"] = Replenishment(slotsAtStart, Slots(), visit);
            a["afterDay"] = Audit(prefixA);
            context.Checkpoint();
            var diagnostics = new Dictionary<string, object>(); a["diagnostics"] = diagnostics;
            Diagnostics(soak, prefixA, diagnostics);
            context.Checkpoint();
            var after = new Dictionary<string, object>(); a["after"] = after;
            yield return Observe(context, soak, "A.after", after, Convert.ToDouble(planA["afterDiagnosticHours"], CultureInfo.InvariantCulture));
            a["followUp"] = FollowUp(diagnostics);
            var finalA = Audit(prefixA); a["final"] = finalA;
            soak.Expect(Count(finalA, "invalid") == 0 && Count(finalA, "offNavigation") == 0, "A.after",
                "the state stays coherent after the diagnostic actions (" + Count(finalA, "invalid") + " invalid references, " + Count(finalA, "offNavigation") + " parties off navigation)");
            a["endDays"] = CampaignTime.Now.ToDays;
            var idsA = Ids();
            context.Checkpoint();
            yield return TwoWorldMatrixProbe.EndGame(context);
            baseline("after world A ended");

            // ---- world B: MMR-19's fixture world, its placement and catalog receipt named by the scenario file (relative to the repository).
            if (!fixtureB)
            {
                results["B"] = TwoWorldMatrixProbe.BlockedFixture();
                context.Outputs["completed"] = true;
                context.Checkpoint();
                if (soak.Failures.Count > 0) throw new InvalidOperationException("Campaign soak expectations failed: " + String.Join("; ", soak.Failures.Take(12)));
                yield break;
            }
            context.Args["fixtureSeed"] = planB["fixtureSeed"];
            context.Args["fixturePlacement"] = placement;
            context.Args["catalogReceipt"] = receipt;
            var b = new Dictionary<string, object>(); results["B"] = b;
            yield return TwoWorldMatrixProbe.StartFixture(context, b, "B", "campaign_soak");
            string prefixB = (string)b["prefix"];
            var initB = Audit(prefixB); b["init"] = initB;
            var independence = Independence(prefixA, prefixB, idsA, initA); b["independence"] = independence;
            soak.Expect(Count(initB, "invalid") == 0 && Count(initB, "unexpected") == 0 && Count(initB, "offNavigation") == 0 && Equals(independence["aObjects"], 0) && Count(independence, "foreign") == 0,
                "B.init", "a coherent fresh state with nothing of world A (" + Count(initB, "invalid") + " invalid references, " + independence["aObjects"] + " A objects, " + Count(independence, "foreign") + " foreign references)");
            // The fixture world replaces no creation culture's start point (it has no startup fixtures), so no start is checked.
            b["playability"] = Playability((string)b["worldId"], new HashSet<string>(StringComparer.Ordinal), context);
            context.Checkpoint();
            var hours = new Dictionary<string, object>(); b["hours"] = hours;
            yield return Observe(context, soak, "B.hours", hours, Convert.ToDouble(planB["observeHours"], CultureInfo.InvariantCulture));
            var finalB = Audit(prefixB); b["final"] = finalB;
            soak.Expect(Count(finalB, "invalid") == 0 && Count(finalB, "offNavigation") == 0, "B.hours", "the state stays coherent (" + Count(finalB, "invalid") + " invalid references, " + Count(finalB, "offNavigation") + " parties off navigation)");
            context.Checkpoint();
            yield return TwoWorldMatrixProbe.EndGame(context);
            baseline("after world B ended");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (soak.Failures.Count > 0) throw new InvalidOperationException("Campaign soak expectations failed: " + String.Join("; ", soak.Failures.Take(12)));
        }

        // MMR-41's semantic routes against the live campaign (IMapScene paths, the registered navigation caches); the summary kept.
        private static Dictionary<string, object> Playability(string worldId, ICollection<string> startCultures, ProbeContext context)
        {
            try
            {
                var report = CampaignPlayability.Run(worldId, context.Stage, startCultures);
                var all = ((IEnumerable)report["failures"]).Cast<IDictionary<string, object>>().ToList();
                // A world that replaces no creation culture's start (startCultures empty: MMR-19's fixture world) has no player-start route to
                // hold: its refused route is recorded as not applicable, never counted as held.
                bool noStarts = startCultures != null && startCultures.Count == 0;
                var notApplicable = all.Where(f => noStarts && Equals(f["category"], "player-start")).ToList();
                var failures = all.Except(notApplicable).ToList();
                var flags = report.ContainsKey("flags") ? (IDictionary<string, object>)report["flags"] : null;
                return new Dictionary<string, object> { ["activatable"] = report["activatable"], ["routes"] = report["routes"], ["failures"] = failures.Take(30).Cast<object>().ToList(), ["failureCount"] = failures.Count,
                    ["notApplicable"] = notApplicable.Cast<object>().ToList(), ["startsChecked"] = !noStarts,
                    ["flags"] = flags == null ? null : flags.Where(kv => kv.Key != "rows").ToDictionary(kv => kv.Key, kv => kv.Value), ["world"] = report.ContainsKey("world") ? report["world"] : null };
            }
            catch (Exception error) { return new Dictionary<string, object> { ["error"] = error.GetType().Name + ": " + error.Message }; }
        }

        // ---------------------------------------------------------------- the player's visit

        // The player's party goes from its start town to the nearest other town by land (MapDistanceModel) where a notable offers it a
        // volunteer and an affordable trade good is stocked, as a player looking to recruit and trade picks a town (run 4652's nearest town
        // offered the player no volunteer: its notables' low slots were empty); on arrival the engine starts the settlement encounter (the
        // town menu). There, the trade and the recruitment, then the menu's leave path. Other parties ignore the player for the whole test
        // (the probe plays no encounters; MMR-34 run 39908 stalled on one): diagnostic setup, recorded.
        private static IEnumerator Visit(ProbeContext context, Soak soak, string prefix, Dictionary<string, object> visit)
        {
            var campaign = Campaign.Current; var main = MobileParty.MainParty;
            var shelter = CampaignTime.Now + CampaignTime.Days(30);
            main.IgnoreByOtherPartiesTill(shelter);
            visit["setup"] = new Dictionary<string, object> { ["playerIgnoredByOtherPartiesUntilDays"] = shelter.ToDays };
            var from = main.Position.ToVec2();
            var towns = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            var home = towns.OrderBy(s => Distance(from, s.GatePosition.ToVec2())).First();
            var model = campaign.Models.MapDistanceModel;
            var reachable = towns.Where(s => s != home).Select(s => new { Town = s, Distance = model.GetDistance(home, s, false, false, MobileParty.NavigationType.Default) })
                .Where(x => x.Distance > 0 && x.Distance < 1e6f && !float.IsNaN(x.Distance)).OrderBy(x => x.Distance).ThenBy(x => x.Town.StringId, StringComparer.Ordinal).ToList();
            visit["startTown"] = home.StringId; visit["startDistance"] = Distance(from, home.GatePosition.ToVec2());
            visit["reachableTowns"] = reachable.Count;
            var offering = reachable.Where(x => Offered(campaign, x.Town) > 0 && Affordable(x.Town) != null).ToList();
            visit["skippedNearer"] = reachable.TakeWhile(x => offering.Count == 0 || x.Town != offering[0].Town)
                .Select(x => x.Town.StringId + " (" + Offered(campaign, x.Town) + " volunteers offered" + (Affordable(x.Town) == null ? ", no affordable trade good" : "") + ")").Take(10).ToList();
            if (offering.Count == 0) { soak.Expect(false, "A.visit", "a town reachable by land from " + home.StringId + " offers the player a volunteer and an affordable trade good"); yield break; }
            var town = offering[0].Town;
            visit["town"] = town.StringId; visit["landDistance"] = offering[0].Distance; visit["volunteersOffered"] = Offered(campaign, town); visit["startHours"] = CampaignTime.Now.ToHours;
            visit["encounterBefore"] = PlayerEncounter.Current != null; visit["settlementBefore"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId;
            main.SetMoveGoToSettlement(town, MobileParty.NavigationType.Default, false);
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            double start = context.Seconds;
            while (main.CurrentSettlement != town && PlayerEncounter.Current == null && context.Seconds - start < 240)
            {
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop && PlayerEncounter.Current == null) { InformationManager.HideInquiry(); DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward); }
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            yield return TwoWorldMatrixProbe.Frames(10);
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            visit["travelHours"] = CampaignTime.Now.ToHours - (double)visit["startHours"]; visit["travelSeconds"] = context.Seconds - start;
            visit["inSettlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId;
            visit["encounterSettlement"] = PlayerEncounter.EncounterSettlement == null ? null : PlayerEncounter.EncounterSettlement.StringId;
            visit["menu"] = map == null || map.MenuContext == null || map.MenuContext.GameMenu == null ? null : map.MenuContext.GameMenu.StringId;
            bool arrived = main.CurrentSettlement == town && PlayerEncounter.EncounterSettlement == town;
            soak.Expect(arrived && Equals(visit["menu"], "town") && (double)visit["travelHours"] > 0, "A.visit", "the player's party travelled " + F((double)visit["travelHours"]) + " h from " + home.StringId + " to "
                + town.StringId + " and the engine opened its settlement encounter (menu " + visit["menu"] + ")");
            if (!arrived) yield break;
            visit["trade"] = Trade(soak, town);
            visit["recruitment"] = Recruit(soak, campaign, town);
            context.Checkpoint();
            var leave = new Dictionary<string, object>(); visit["leave"] = leave;
            try { if (PlayerEncounter.Current != null) { PlayerEncounter.LeaveSettlement(); PlayerEncounter.Finish(true); } }
            catch (Exception error) { leave["error"] = error.GetType().Name + ": " + error.Message; }
            yield return TwoWorldMatrixProbe.Frames(10);
            map = Game.Current.GameStateManager.ActiveState as MapState;
            leave["inSettlement"] = main.CurrentSettlement == null ? null : main.CurrentSettlement.StringId;
            leave["encounter"] = PlayerEncounter.Current != null;
            leave["menu"] = map == null || map.MenuContext == null || map.MenuContext.GameMenu == null ? null : map.MenuContext.GameMenu.StringId;
            main.SetMoveModeHold();
            soak.Expect(main.CurrentSettlement == null && PlayerEncounter.Current == null && leave["menu"] == null && !leave.ContainsKey("error"), "A.visit",
                "the player left " + town.StringId + " through the menu's leave path, back on the map");
        }

        // One trade good bought, then sold back, through the trade screen's logic: an InventoryLogic for the town built as
        // InventoryScreenHelper.OpenScreenAsTrade builds it (the town menu's trade option), one transfer, then DoneLogic, which commits the
        // exchange (the town's gold through its MerchantInventoryListener, the player's through GiveGoldAction).
        private static Dictionary<string, object> Trade(Soak soak, Settlement town)
        {
            var result = new Dictionary<string, object>();
            var party = MobileParty.MainParty; var hero = Hero.MainHero;
            result["tradeGoodsStocked"] = Enumerable.Range(0, town.ItemRoster.Count).Count(i => { var e = town.ItemRoster.GetElementCopyAtIndex(i); return e.EquipmentElement.Item != null && e.Amount > 1 && e.EquipmentElement.Item.IsTradeGood; });
            var pick = Affordable(town);
            if (pick == null) { soak.Expect(false, "A.trade", "an affordable trade good is stocked in " + town.StringId); return result; }
            var item = pick.Value.EquipmentElement;
            result["item"] = item.Item.StringId;
            Func<Dictionary<string, object>> state = () => new Dictionary<string, object> { ["townStock"] = town.ItemRoster.GetItemNumber(item.Item), ["partyStock"] = party.ItemRoster.GetItemNumber(item.Item),
                ["gold"] = hero.Gold, ["townGold"] = town.Town.Gold };
            var before = state();
            var buy = ScreenTrade(town, item, true); result["buy"] = buy;
            var bought = state();
            var sell = ScreenTrade(town, item, false); result["sell"] = sell;
            var sold = state();
            result["before"] = before; result["afterBuy"] = bought; result["afterSell"] = sold;
            Func<Dictionary<string, object>, string, int> n = (d, k) => (int)d[k];
            int buyPrice = (int)buy["price"], sellPrice = (int)sell["price"];
            bool buyOk = Equals(buy["done"], true) && n(bought, "townStock") == n(before, "townStock") - 1 && n(bought, "partyStock") == n(before, "partyStock") + 1
                && n(before, "gold") - n(bought, "gold") == buyPrice && n(bought, "townGold") - n(before, "townGold") == buyPrice;
            bool sellOk = Equals(sell["done"], true) && n(sold, "townStock") == n(bought, "townStock") + 1 && n(sold, "partyStock") == n(bought, "partyStock") - 1
                && n(sold, "gold") - n(bought, "gold") == sellPrice && n(bought, "townGold") - n(sold, "townGold") == sellPrice;
            soak.Expect(buyOk && sellOk, "A.trade", "bought one " + item.Item.StringId + " for " + buyPrice + " and sold it back for " + sellPrice + " at " + town.StringId
                + " through the trade screen's logic: stock moved one each way, the player's and the town's gold by exactly the quoted prices");
            return result;
        }

        // The first trade good (by id) stocked more than once whose price the player can pay.
        private static ItemRosterElement? Affordable(Settlement town)
        {
            var party = MobileParty.MainParty; var hero = Hero.MainHero;
            return Enumerable.Range(0, town.ItemRoster.Count).Select(i => town.ItemRoster.GetElementCopyAtIndex(i))
                .Where(e => e.EquipmentElement.Item != null && e.Amount > 1 && e.EquipmentElement.Item.IsTradeGood)
                .Where(e => { int p = town.Town.GetItemPrice(e.EquipmentElement, party, false); return p > 0 && p <= hero.Gold; })
                .OrderBy(e => e.EquipmentElement.Item.StringId, StringComparer.Ordinal).Select(e => (ItemRosterElement?)e).FirstOrDefault();
        }

        // Volunteers the town's notables offer the player (VolunteerModel.MaximumIndexHeroCanRecruitFromHero).
        private static int Offered(Campaign campaign, Settlement town)
        {
            int n = 0;
            foreach (var notable in town.Notables)
            {
                int max = campaign.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(Hero.MainHero, notable, -101);
                for (int i = 0; i <= max && i < notable.VolunteerTypes.Length; i++) if (notable.VolunteerTypes[i] != null) n++;
            }
            return n;
        }

        private static Dictionary<string, object> ScreenTrade(Settlement town, EquipmentElement item, bool buy)
        {
            var row = new Dictionary<string, object> { ["direction"] = buy ? "buy" : "sell" };
            try
            {
                var logic = new InventoryLogic(town.Town.Owner);
                // InventoryScreenHelper.GetCurrentMarketDataForPlayer (private) returns the market of the town the player is in.
                logic.Initialize(town.ItemRoster, PartyBase.MainParty.ItemRoster, PartyBase.MainParty.MemberRoster, true, true, CharacterObject.PlayerCharacter,
                    InventoryScreenHelper.InventoryCategoryType.None, town.Town.MarketData, false, InventoryScreenHelper.InventoryMode.Trade, null, null, null);
                var listenerType = typeof(InventoryScreenHelper).GetNestedType("MerchantInventoryListener", BindingFlags.NonPublic);
                var listener = (InventoryListener)Activator.CreateInstance(listenerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { town.Town }, CultureInfo.InvariantCulture);
                logic.SetInventoryListener(listener);
                // The inventory screen's view model hooks four callbacks that InventoryLogic invokes unchecked: AfterTransfer (run 4652: a
                // NullReferenceException after the transfer), TotalAmountChange (run 34968: from the TransactionDebt setter), DonationXpChange
                // and AfterReset. Headless, no-op handlers stand in for the screen's refresh.
                logic.AfterTransfer += (inventoryLogic, results) => { };
                logic.AfterReset += (inventoryLogic, fromCancel) => { };
                logic.TotalAmountChange = amount => { };
                logic.DonationXpChange = () => { };
                row["price"] = logic.GetItemPrice(item, buy);
                var from = buy ? InventoryLogic.InventorySide.OtherInventory : InventoryLogic.InventorySide.PlayerInventory;
                var to = buy ? InventoryLogic.InventorySide.PlayerInventory : InventoryLogic.InventorySide.OtherInventory;
                logic.AddTransferCommand(TransferCommand.Transfer(1, from, to, new ItemRosterElement(item, 1), EquipmentIndex.None, EquipmentIndex.None, CharacterObject.PlayerCharacter));
                row["totalAmount"] = logic.TotalAmount;
                row["done"] = logic.DoneLogic();
            }
            catch (Exception error) { row["error"] = error.ToString(); row["done"] = false; if (!row.ContainsKey("price")) row["price"] = -1; }
            return row;
        }

        // The installed recruitment screen's domain sequence (RecruitmentVM.OnDone) for one troop from a notable of the town. The roster and the
        // slot are written as the screen writes them; what the game decides is the volunteer on offer, its availability and its cost.
        private static Dictionary<string, object> Recruit(Soak soak, Campaign campaign, Settlement town)
        {
            var result = new Dictionary<string, object>();
            var hero = Hero.MainHero; var party = MobileParty.MainParty;
            Hero seller = null; int index = -1; CharacterObject troop = null;
            foreach (var notable in town.Notables.OrderBy(h => h.StringId, StringComparer.Ordinal))
            {
                int max = campaign.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(hero, notable, -101);
                for (int i = 0; i <= max && i < notable.VolunteerTypes.Length && troop == null; i++)
                    if (notable.VolunteerTypes[i] != null) { seller = notable; index = i; troop = notable.VolunteerTypes[i]; }
                if (troop != null) break;
            }
            result["notables"] = town.Notables.Count;
            if (troop == null) { soak.Expect(false, "A.recruit", "a volunteer is available to the player from a notable of " + town.StringId); return result; }
            int cost = campaign.Models.PartyWageModel.GetTroopRecruitmentCost(troop, hero, false).RoundedResultNumber;
            int gold = hero.Gold, count = party.MemberRoster.GetTroopCount(troop);
            result["settlement"] = town.StringId; result["notable"] = seller.StringId; result["slot"] = index; result["troop"] = troop.StringId; result["cost"] = cost;
            result["goldBefore"] = gold; result["countBefore"] = count;
            result["dailyRefillChance"] = campaign.Models.VolunteerModel.GetDailyVolunteerProductionProbability(seller, index, town);
            if (cost > gold) { soak.Expect(false, "A.recruit", "the recruitment cost " + cost + " is within the player's gold " + gold); return result; }
            seller.VolunteerTypes[index] = null;
            party.MemberRoster.AddToCounts(troop, 1);
            CampaignEventDispatcher.Instance.OnUnitRecruited(troop, 1);
            GiveGoldAction.ApplyBetweenCharacters(hero, null, cost, true);
            result["goldAfter"] = hero.Gold; result["countAfter"] = party.MemberRoster.GetTroopCount(troop); result["slotCleared"] = seller.VolunteerTypes[index] == null;
            soak.Expect(party.MemberRoster.GetTroopCount(troop) == count + 1 && hero.Gold == gold - cost && seller.VolunteerTypes[index] == null, "A.recruit",
                "recruited one " + troop.StringId + " from " + seller.StringId + " for " + cost + ": roster, gold and the notable's slot changed");
            return result;
        }

        // ---------------------------------------------------------------- observation

        private sealed class Track
        {
            public string Kind; public Vec2 First, Last; public double Travelled; public int Samples, StillHours, MaxStill, HoldHours, MaxHold;
            public int EntryGoods = -1, EntryGold, EntryTownGold; public string StuckTarget, EntryTown; public Dictionary<string, object> Holding;
        }

        private sealed class SoakCanaryException : Exception { public SoakCanaryException() : base("mnbl campaign soak canary") { } }

        [ThreadStatic] private static bool inThrowHandler;

        // Campaign time runs for the given hours (fast forward); the installed behaviours act on their own while the player holds, sheltered.
        // Recorded: each hour every active party (kind, motion, navigation, a stuck or holding streak); every frame every party's unfound path
        // (MobileParty._aiPathNotFound, cleared on the AI's next decision); every event of interest; exceptions and the engine's asserts.
        private static IEnumerator Observe(ProbeContext context, Soak soak, string scenario, Dictionary<string, object> report, double hours)
        {
            var campaign = Campaign.Current; var main = MobileParty.MainParty;
            var shelter = CampaignTime.Now + CampaignTime.Days(30);
            main.IgnoreByOtherPartiesTill(shelter);
            if (main.CurrentSettlement == null) main.SetMoveModeHold();
            report["hours"] = hours;
            report["setup"] = new Dictionary<string, object> { ["playerIgnoredByOtherPartiesUntilDays"] = shelter.ToDays, ["playerHolding"] = main.CurrentSettlement == null };
            double eps = soak.Rule("moveEpsilon"), stuckWindow = soak.Rule("stuckHours");
            var tracks = new Dictionary<MobileParty, Track>();
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            Action<string> bump = key => { int v; counters.TryGetValue(key, out v); counters[key] = v + 1; };
            Action<string, int> add = (key, n) => { int v; counters.TryGetValue(key, out v); counters[key] = v + n; };
            var samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Action<string, string> sample = (key, text) => { List<string> list; if (!samples.TryGetValue(key, out list)) samples[key] = list = new List<string>(); if (list.Count < 12) list.Add(text); };
            var offNavigation = new List<string>(); int offCount = 0;
            var created = new List<MobileParty>();
            var hourly = new List<object>();
            var errors = new List<string>();
            var unfound = new Dictionary<MobileParty, Dictionary<string, object>>();
            // Listener bodies run inside engine dispatch: nothing may throw into it.
            Action<Action> safe = body => { try { body(); } catch (Exception error) { if (errors.Count < 20) errors.Add(error.GetType().Name + ": " + error.Message); } };
            var owner = new object();
            var thrown = new List<object>(); int thrownCount = 0, campaignThrown = 0; bool canarySeen = false;
            int engineThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            // Exceptions thrown anywhere while time runs: the handler runs on the throwing thread before unwinding (Environment.StackTrace is
            // the throw site). Campaign code is a stack through the campaign, its DLC, SandBox or this module. A canary proves the hook fires.
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onThrow = (sender, e) =>
            {
                if (inThrowHandler) return;
                inThrowHandler = true;
                try
                {
                    if (e.Exception is SoakCanaryException) { canarySeen = true; return; }
                    System.Threading.Interlocked.Increment(ref thrownCount);
                    string stack = Environment.StackTrace;
                    bool fromCampaign = stack.Contains("TaleWorlds.CampaignSystem") || stack.Contains("NavalDLC") || stack.Contains("SandBox") || stack.Contains("MNBL.ProceduralWorld.Campaign");
                    if (fromCampaign) System.Threading.Interlocked.Increment(ref campaignThrown);
                    lock (thrown) if (thrown.Count < 25) thrown.Add(new Dictionary<string, object> { ["type"] = e.Exception.GetType().FullName, ["message"] = e.Exception.Message,
                        ["engineThread"] = System.Threading.Thread.CurrentThread.ManagedThreadId == engineThread, ["campaignCode"] = fromCampaign,
                        ["stack"] = String.Join("\n", stack.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("CampaignSoakProbe") && !l.Contains("System.Environment")).Take(14)) });
                }
                catch { }
                finally { inThrowHandler = false; }
            };
            var asserts = new AssertCapture();
            Action sampleHour = () => safe(() =>
            {
                var pending = created.ToList(); created.Clear();
                foreach (var p in pending) safe(() => { if (p.IsActive) bump("created:" + KindOf(p)); });
                int movingNow = 0, active = 0;
                foreach (var p in MobileParty.All.ToList())
                {
                    if (p == null || !p.IsActive || p.IsMainParty) continue;
                    active++;
                    var at = p.Position.ToVec2();
                    Track t;
                    if (!tracks.TryGetValue(p, out t)) tracks[p] = t = new Track { Kind = KindOf(p), First = at, Last = at };
                    double moved = Distance(t.Last, at); t.Travelled += moved; t.Last = at;
                    bool first = t.Samples++ == 0;
                    if (t.EntryGoods >= 0)
                    {
                        int now = Goods(p, false), sold = t.EntryGoods - now;
                        if (sold > 0) { bump("villagerSold"); add("villagerItemsSold", sold); sample("villagerSold", p.StringId + " @ " + t.EntryTown + ": " + sold + " items, trade gold " + t.EntryGold + " -> " + p.PartyTradeGold); }
                        else if (t.EntryTownGold <= 0) { bump("villagerSoldNothing:townWithoutGold"); sample("villagerSoldNothing", p.StringId + " @ " + t.EntryTown + ": the town had no gold"); }
                        else if (t.EntryGoods == 0) bump("villagerSoldNothing:onlyPackAnimals");
                        else { bump("villagerSoldNothing"); sample("villagerSoldNothing", p.StringId + " @ " + t.EntryTown + ": " + t.EntryGoods + " items on entry, " + now + " now, town gold " + t.EntryTownGold); }
                        t.EntryGoods = -1;
                    }
                    bool free = p.CurrentSettlement == null && p.MapEvent == null && p.BesiegedSettlement == null && p.SiegeEvent == null && (p.Army == null || p.Army.LeaderParty == p) && !p.IsTransitionInProgress;
                    // MobileParty.IsMoving is only "not at its move target point"; a party holding by its own choice (short-term Hold, e.g.
                    // bandits waiting) is not travelling. Stuck: travelling, yet not moving, for the stuck window. A party's first sample
                    // has no motion to measure and counts toward neither.
                    bool travelling = p.ShortTermBehavior != AiBehavior.Hold && p.ShortTermBehavior != AiBehavior.None;
                    if (!first && free && p.IsMoving && travelling)
                    {
                        movingNow++;
                        if (moved < eps) { t.StillHours++; if (t.StillHours > t.MaxStill) { t.MaxStill = t.StillHours; t.StuckTarget = Target(p); } } else t.StillHours = 0;
                    }
                    else t.StillHours = 0;
                    if (!first && free && !travelling && moved < eps)
                    {
                        t.HoldHours++;
                        if (t.HoldHours > t.MaxHold) t.MaxHold = t.HoldHours;
                        if (t.HoldHours >= stuckWindow && t.Holding == null) t.Holding = Holding(p, t);
                    }
                    else t.HoldHours = 0;
                    if (p.CurrentSettlement == null)
                    {
                        string problem = GeneratedSpawnAssertions.Problem(p);
                        if (problem != null) { offCount++; if (offNavigation.Count < 30) offNavigation.Add(t.Kind + " " + p.StringId + " at (" + F(at.x) + ", " + F(at.y) + "): " + problem); }
                    }
                }
                hourly.Add(new Dictionary<string, object> { ["hours"] = Math.Round(CampaignTime.Now.ToHours, 2), ["active"] = active, ["moving"] = movingNow, ["unfoundSoFar"] = unfound.Count });
            });

            double startHours = CampaignTime.Now.ToHours, lastHours = startHours;
            double start = context.Seconds, lastProgress = start;
            double timeout = soak.Rule("timeoutSeconds"), stallSeconds = soak.Rule("stallSeconds");
            var stalls = new List<object>();
            report["startHours"] = startHours;
            report["before"] = Economy();
            try
            {
                CampaignEvents.SettlementEntered.AddNonSerializedListener(owner, (p, s, h) => safe(() =>
                {
                    if (p == null || s == null) return;
                    bump("entered:" + KindOf(p) + ">" + SettlementKind(s));
                    // A villager party sells its goods on entering a town (VillagerCampaignBehavior.OnSettlementEntered ->
                    // SellGoodsForTradeAction.ApplyByVillagerTrade, which raises no transaction event, and sells only what the town's gold pays
                    // for). MbEvent.AddNonSerializedListener puts a listener at the head of the list and InvokeList walks from the head, so this
                    // listener (added after the campaign's) runs before the sale: the goods, trade gold and town gold now are the ones before it,
                    // compared at the next hour.
                    if (p.IsVillager && s.IsTown && KindOf(p) == "villager")
                    {
                        Track t;
                        if (!tracks.TryGetValue(p, out t)) tracks[p] = t = new Track { Kind = KindOf(p), First = p.Position.ToVec2(), Last = p.Position.ToVec2() };
                        t.EntryGoods = Goods(p, false); t.EntryGold = p.PartyTradeGold; t.EntryTownGold = s.Town.Gold; t.EntryTown = s.StringId;
                    }
                }));
                CampaignEvents.OnCaravanTransactionCompletedEvent.AddNonSerializedListener(owner, (p, town, items) => safe(() =>
                {
                    string kind = GeneratedSpawnAssertions.KindOf(p);
                    bump("trade:" + kind); add("tradeItems:" + kind, items.Sum(x => Math.Abs(x.Item2)));
                    sample("trade:" + kind, p.StringId + " @ " + town.Settlement.StringId + ": " + String.Join(", ", items.Take(4).Select(x => x.Item1.Item.StringId + "×" + x.Item2)));
                }));
                CampaignEvents.OnTroopRecruitedEvent.AddNonSerializedListener(owner, (recruiter, s, source, troop, n) => safe(() =>
                {
                    string kind = recruiter == null ? "none" : recruiter == Hero.MainHero ? "player" : recruiter.IsLord ? "lord" : "other";
                    bump("recruited:" + kind); add("recruitedTroops:" + kind, n);
                    sample("recruited:" + kind, (recruiter == null ? "?" : recruiter.StringId) + " @ " + (s == null ? "?" : s.StringId) + ": " + n + " " + (troop == null ? "?" : troop.StringId));
                }));
                CampaignEvents.MapEventStarted.AddNonSerializedListener(owner, (e, attacker, defender) => safe(() =>
                {
                    bump("battle:" + e.EventType + (e.IsNavalMapEvent ? ":naval" : ""));
                    sample("battle:" + e.EventType, (attacker == null ? "?" : attacker.Id) + " > " + (defender == null ? "?" : defender.Id));
                }));
                CampaignEvents.MapEventEnded.AddNonSerializedListener(owner, e => safe(() => bump("battleEnded:" + e.EventType)));
                CampaignEvents.VillageLooted.AddNonSerializedListener(owner, v => safe(() => { bump("villageLooted"); sample("villageLooted", v.Settlement.StringId); }));
                CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(owner, s => safe(() => { bump("siegeStarted"); sample("siegeStarted", s.BesiegedSettlement.StringId); }));
                CampaignEvents.ArmyCreated.AddNonSerializedListener(owner, army => safe(() => { bump("armyCreated"); sample("armyCreated", army.LeaderParty == null ? "?" : army.LeaderParty.StringId); }));
                CampaignEvents.WarDeclared.AddNonSerializedListener(owner, (f1, f2, detail) => safe(() => { bump("warDeclared"); sample("warDeclared", f1.StringId + " vs " + f2.StringId + " (" + detail + ")"); }));
                CampaignEvents.MakePeace.AddNonSerializedListener(owner, (f1, f2, detail) => safe(() => { bump("peace"); sample("peace", f1.StringId + " / " + f2.StringId + " (" + detail + ")"); }));
                CampaignEvents.HeroKilledEvent.AddNonSerializedListener(owner, (victim, killer, detail, notify) => safe(() => { bump("heroKilled:" + detail); sample("heroKilled", victim.StringId + " (" + detail + ")"); }));
                CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(owner, (s, openToClaim, newOwner, oldOwner, capturer, detail) => safe(() => { bump("ownerChanged:" + detail); sample("ownerChanged", s.StringId + ": " + (oldOwner == null ? "?" : oldOwner.StringId) + " > " + (newOwner == null ? "?" : newOwner.StringId)); }));
                CampaignEvents.OnClanLeaderChangedEvent.AddNonSerializedListener(owner, (oldLeader, newLeader) => safe(() => bump("clanLeaderChanged")));
                CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(owner, k => safe(() => { bump("kingdomDestroyed"); sample("kingdomDestroyed", k.StringId); }));
                CampaignEvents.KingdomDecisionAdded.AddNonSerializedListener(owner, (decision, playerMay) => safe(() => { bump("decision:" + decision.GetType().Name); }));
                CampaignEvents.MobilePartyCreated.AddNonSerializedListener(owner, p => safe(() => { if (!ReferenceEquals(p, null)) created.Add(p); }));
                CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(owner, (p, destroyer) => safe(() => bump("destroyed:" + KindOf(p))));
                CampaignEvents.HourlyTickEvent.AddNonSerializedListener(owner, () => sampleHour());
                AppDomain.CurrentDomain.FirstChanceException += onThrow;
                try { throw new SoakCanaryException(); } catch (SoakCanaryException) { }
                asserts.Install();
                sampleHour();
                DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                context.Stage(scenario + ":time_running");
                while (CampaignTime.Now.ToHours - startHours < hours)
                {
                    // Every frame: a party whose path the engine could not find (the flag lasts until its AI decides again).
                    foreach (var p in MobileParty.All)
                    {
                        if (p == null || !p.IsActive || p.IsMainParty || p.CurrentSettlement != null || !PathNotFound(p)) continue;
                        Dictionary<string, object> row;
                        if (!unfound.TryGetValue(p, out row))
                            unfound[p] = row = new Dictionary<string, object> { ["party"] = p.StringId, ["kind"] = KindOf(p), ["target"] = Target(p), ["targetSettlement"] = p.TargetSettlement == null ? null : p.TargetSettlement.StringId,
                                ["atSea"] = p.IsCurrentlyAtSea, ["hours"] = Math.Round(CampaignTime.Now.ToHours - startHours, 2), ["frames"] = 0 };
                        row["frames"] = (int)row["frames"] + 1;
                    }
                    double now = CampaignTime.Now.ToHours;
                    if (now > lastHours) { lastHours = now; lastProgress = context.Seconds; }
                    else if (context.Seconds - lastProgress > stallSeconds)
                    {
                        // What stopped time, then leave it as the player would and go on; repeated stalls fail the scenario.
                        var row = TwoWorldMatrixProbe.TimeState(context.Seconds - start);
                        TwoWorldMatrixProbe.ClearEncounter(row);
                        InformationManager.HideInquiry();
                        stalls.Add(row);
                        context.Checkpoint();
                        if (stalls.Count >= (int)soak.Rule("maxStalls")) break;
                        DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                        lastProgress = context.Seconds;
                    }
                    else if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                    if (context.Seconds - start > timeout) break;
                    yield return null;
                }
            }
            finally
            {
                asserts.Remove();
                AppDomain.CurrentDomain.FirstChanceException -= onThrow;
                foreach (var e in new IMbEventBase[] { CampaignEvents.SettlementEntered, CampaignEvents.OnCaravanTransactionCompletedEvent, CampaignEvents.OnTroopRecruitedEvent,
                    CampaignEvents.MapEventStarted, CampaignEvents.MapEventEnded, CampaignEvents.VillageLooted, CampaignEvents.OnSiegeEventStartedEvent, CampaignEvents.ArmyCreated,
                    CampaignEvents.WarDeclared, CampaignEvents.MakePeace, CampaignEvents.HeroKilledEvent, CampaignEvents.OnSettlementOwnerChangedEvent, CampaignEvents.OnClanLeaderChangedEvent,
                    CampaignEvents.KingdomDestroyedEvent, CampaignEvents.KingdomDecisionAdded, CampaignEvents.MobilePartyCreated, CampaignEvents.MobilePartyDestroyed })
                    e.ClearListeners(owner);
                CampaignEvents.HourlyTickEvent.ClearListeners(owner);
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            yield return TwoWorldMatrixProbe.Frames(5);
            double advanced = CampaignTime.Now.ToHours - startHours;
            report["hoursAdvanced"] = advanced; report["realSeconds"] = context.Seconds - start; report["stalls"] = stalls;
            report["timedOut"] = context.Seconds - start > timeout;
            report["after"] = Economy();
            report["counters"] = counters.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)k.Value);
            report["samples"] = samples;
            report["listenerErrors"] = errors;
            report["hourly"] = hourly;
            report["thrown"] = thrown; report["thrownCount"] = thrownCount; report["campaignThrown"] = campaignThrown; report["exceptionCanarySeen"] = canarySeen;
            report["asserts"] = asserts.Asserts; report["assertCount"] = asserts.Count; report["assertCanarySeen"] = asserts.CanarySeen;
            report["offNavigation"] = offNavigation; report["offNavigationCount"] = offCount;
            // Motion by kind: parties tracked, parties that moved (net or along the way), distance travelled.
            var motion = new Dictionary<string, object>();
            double moveMin = soak.Rule("movedDistance");
            foreach (var group in tracks.Values.GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                motion[group.Key] = new Dictionary<string, object> { ["tracked"] = group.Count(), ["moved"] = group.Count(t => t.Travelled >= moveMin),
                    ["travelled"] = Math.Round(group.Sum(t => t.Travelled), 2), ["maxTravelled"] = Math.Round(group.Max(t => t.Travelled), 2) };
            report["motion"] = motion;
            var stuck = tracks.Where(kv => kv.Value.MaxStill >= stuckWindow).OrderBy(kv => kv.Key.StringId, StringComparer.Ordinal)
                .Select(kv => kv.Value.Kind + " " + kv.Key.StringId + ": still " + kv.Value.MaxStill + " h while moving toward " + kv.Value.StuckTarget).ToList();
            report["stuck"] = stuck.Take(30).ToList(); report["stuckCount"] = stuck.Count;
            // Parties that held still by their own choice for the stuck window: each with its state and the installed reason, if any.
            var holding = tracks.Values.Where(t => t.Holding != null).Select(t => t.Holding).OrderBy(h => (string)h["kind"], StringComparer.Ordinal).ThenBy(h => (string)h["party"], StringComparer.Ordinal).ToList();
            report["holding"] = holding.Take(40).ToList(); report["holdingCount"] = holding.Count;
            report["holdingByKind"] = holding.GroupBy(h => (string)h["kind"]).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            var unexplained = holding.Where(h => ((string)h["kind"] == "villager" || (string)h["kind"] == "fishing") && h["reason"] == null).ToList();
            report["holdingVillagersWithoutReason"] = unexplained.Count;
            var unfoundRows = unfound.Values.OrderBy(r => (string)r["party"], StringComparer.Ordinal).ToList();
            report["unfoundPaths"] = unfoundRows.Take(40).ToList(); report["unfoundPathCount"] = unfoundRows.Count;
            report["unfoundToSettlementCount"] = unfoundRows.Count(r => r["targetSettlement"] != null);
            context.Checkpoint();
            soak.Expect(advanced >= hours - 0.01 && stalls.Count < (int)soak.Rule("maxStalls") && !(bool)report["timedOut"], scenario,
                "campaign time advanced " + F(advanced) + " of " + F(hours) + " hours (" + stalls.Count + " stalls, " + F(context.Seconds - start) + " s)");
            soak.Expect(canarySeen && asserts.CanarySeen && campaignThrown == 0 && asserts.Count == 0 && errors.Count == 0, scenario, campaignThrown + " exceptions thrown from campaign code while time ran ("
                + thrownCount + " in all), " + asserts.Count + " engine asserts, " + errors.Count + " listener errors (canaries seen: exception " + canarySeen + ", assert " + asserts.CanarySeen + ")");
            soak.Expect(offCount == 0 && (int)report["unfoundToSettlementCount"] == 0 && stuck.Count == 0 && unexplained.Count == 0, scenario,
                offCount + " party positions off navigation, " + report["unfoundToSettlementCount"] + " parties whose path to a settlement was not found (" + unfoundRows.Count + " unfound paths in all), "
                + stuck.Count + " stuck and " + unexplained.Count + " villager or fishing parties holding without an installed reason for " + stuckWindow + " h");
        }

        // A party that has held still outside a settlement for the stuck window, with what decides it. A villager holds by the installed AI
        // (VillagerCampaignBehavior.HourlyTickParty) when its home village is raided, or when its target town is besieged or at war with it;
        // any other villager hold has no installed reason. Other kinds hold by their own AI decisions (recorded).
        private static Dictionary<string, object> Holding(MobileParty p, Track t)
        {
            var row = new Dictionary<string, object> { ["party"] = p.StringId, ["kind"] = t.Kind, ["position"] = new[] { Math.Round(t.Last.x, 2), Math.Round(t.Last.y, 2) },
                ["shortTerm"] = p.ShortTermBehavior.ToString(), ["default"] = p.DefaultBehavior.ToString(), ["target"] = Target(p), ["atSea"] = p.IsCurrentlyAtSea,
                ["landCapable"] = p.HasLandNavigationCapability, ["disbanding"] = p.IsDisbanding, ["goods"] = Goods(p), ["navigation"] = GeneratedSpawnAssertions.Problem(p),
                ["home"] = p.HomeSettlement == null ? null : p.HomeSettlement.StringId, ["sinceHours"] = Math.Round(CampaignTime.Now.ToHours, 2) };
            string reason = null;
            if (t.Kind == "fishing")
            {
                // War Sails' FishingPartyCampaignBehavior holds a fishing party at its fishing point (IsFishing) and ends the wait by an hourly
                // chance that grows from about 7.5 hours; a fishing party holding while neither fishing nor roaming has no installed reason.
                bool fishing = Flag(p.PartyComponent, "IsFishing"), roaming = Flag(p.PartyComponent, "IsRoaming");
                row["fishing"] = fishing; row["roaming"] = roaming;
                reason = fishing ? "fishing at its fishing point" : roaming ? "roaming" : null;
            }
            else if (p.IsVillager)
            {
                var home = p.HomeSettlement; var target = p.TargetSettlement;
                var tradeBound = home != null && home.Village != null ? home.Village.TradeBound : null;
                row["homeRaided"] = home != null && home.IsUnderRaid; row["tradeBound"] = tradeBound == null ? null : tradeBound.StringId;
                row["targetBesieged"] = target != null && target.IsUnderSiege;
                row["targetAtWar"] = target != null && target.MapFaction != null && p.MapFaction != null && FactionManager.IsAtWarAgainstFaction(p.MapFaction, target.MapFaction);
                if ((bool)row["homeRaided"]) reason = "home village raided";
                else if ((bool)row["targetBesieged"]) reason = "target town besieged";
                else if ((bool)row["targetAtWar"]) reason = "target town at war";
                else if (p.IsDisbanding) reason = "disbanding";
            }
            else reason = "own AI decision";
            row["reason"] = reason;
            return row;
        }

        // ---------------------------------------------------------------- diagnostic actions

        // Each through an installed action, time stopped; recorded apart from the natural behaviour the following hours show.
        private static void Diagnostics(Soak soak, string prefix, Dictionary<string, object> report)
        {
            var events = new List<string>(); report["events"] = events;
            var errors = new List<string>(); report["listenerErrors"] = errors;
            var owner = new object();
            // Listener bodies run inside engine dispatch: nothing may throw into it.
            Action<Func<string>> note = text => { try { if (events.Count < 80) events.Add(text()); } catch (Exception error) { if (errors.Count < 10) errors.Add(error.GetType().Name + ": " + error.Message); } };
            CampaignEvents.WarDeclared.AddNonSerializedListener(owner, (f1, f2, detail) => note(() => "warDeclared " + f1.StringId + " " + f2.StringId + " " + detail));
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(owner, (s, openToClaim, newOwner, oldOwner, capturer, detail) =>
                note(() => "ownerChanged " + s.StringId + " " + (oldOwner == null ? "-" : oldOwner.StringId) + " " + (newOwner == null ? "-" : newOwner.StringId) + " " + detail));
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(owner, (victim, killer, detail, notify) => note(() => "heroKilled " + victim.StringId + " " + detail));
            CampaignEvents.OnClanLeaderChangedEvent.AddNonSerializedListener(owner, (oldLeader, newLeader) => note(() => "clanLeaderChanged " + (oldLeader == null ? "-" : oldLeader.StringId) + " " + (newLeader == null ? "-" : newLeader.StringId)));
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(owner, (kingdom, clan) => note(() => "rulingClanChanged " + kingdom.StringId + " " + clan.StringId));
            CampaignEvents.KingdomDecisionAdded.AddNonSerializedListener(owner, (decision, playerMay) => note(() => "decisionAdded " + decision.GetType().Name + " " + (decision.Kingdom == null ? "-" : decision.Kingdom.StringId)));
            CampaignEvents.KingdomDecisionConcluded.AddNonSerializedListener(owner, (decision, outcome, playerChoice) => note(() => "decisionConcluded " + decision.GetType().Name + " " + (decision.Kingdom == null ? "-" : decision.Kingdom.StringId)));
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(owner, k => note(() => "kingdomDestroyed " + k.StringId));
            try
            {
                var kingdoms = Kingdom.All.Where(k => !k.IsEliminated && k.StringId.StartsWith(prefix, StringComparison.Ordinal) && k.Leader != null).OrderBy(k => k.StringId, StringComparer.Ordinal).ToList();
                Kingdom k1 = null, k2 = null;
                Step(report, "war", () =>
                {
                    // The target keeps a fief after losing the town below: a kingdom left without fiefs is dissolved by the installed
                    // FactionDiscontinuationCampaignBehavior (run 21164: most generated kingdoms hold one town, MMR-31's one kingdom per realm).
                    Func<Kingdom, int> fiefs = k => k.Settlements.Count(s => s.IsTown || s.IsCastle);
                    foreach (var y in kingdoms.Where(k => fiefs(k) >= 2 && k.Settlements.Any(s => s.IsTown)))
                    {
                        foreach (var x in kingdoms) if (x != y && !x.IsAtWarWith(y)) { k1 = x; k2 = y; break; }
                        if (k1 != null) break;
                    }
                    if (k1 == null) throw new InvalidOperationException("no kingdom of two or more fiefs at peace with another among " + kingdoms.Count);
                    int warsBefore = Wars();
                    DeclareWarAction.ApplyByDefault(k1, k2);
                    var row = new Dictionary<string, object> { ["declarer"] = k1.StringId, ["target"] = k2.StringId, ["targetFiefs"] = fiefs(k2), ["atWar"] = k1.IsAtWarWith(k2) && k2.IsAtWarWith(k1),
                        ["warsBefore"] = warsBefore, ["warsAfter"] = Wars(), ["event"] = events.Any(e => e.StartsWith("warDeclared " + k1.StringId + " " + k2.StringId, StringComparison.Ordinal)) };
                    soak.Expect((bool)row["atWar"] && (bool)row["event"], "A.war", k1.StringId + " declared war on " + k2.StringId + ": both stances at war, the event raised");
                    return row;
                });
                Step(report, "ownership", () =>
                {
                    if (k1 == null) throw new InvalidOperationException("no war pair");
                    var town = Settlement.All.Where(s => s.IsTown && s.MapFaction == k2 && s.SiegeEvent == null && s.OwnerClan != null && s.OwnerClan != Clan.PlayerClan)
                        .OrderBy(s => s.OwnerClan == k2.RulingClan ? 1 : 0).ThenBy(s => s.StringId, StringComparer.Ordinal).First();
                    var newOwner = k1.Clans.Where(c => c != k1.RulingClan && !c.IsEliminated && c.Leader != null && c.Leader.IsAlive && !c.Leader.IsPrisoner).OrderBy(c => c.StringId, StringComparer.Ordinal)
                        .Select(c => c.Leader).FirstOrDefault() ?? k1.Leader;
                    var oldClan = town.OwnerClan;
                    var notables = town.Notables.Select(h => h.StringId).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var villages = town.BoundVillages.Select(v => v.Settlement).ToList();
                    ChangeOwnerOfSettlementAction.ApplyByDefault(newOwner, town);
                    var garrison = town.Town.GarrisonParty;
                    var row = new Dictionary<string, object> { ["town"] = town.StringId, ["from"] = oldClan.StringId, ["to"] = newOwner.Clan.StringId, ["newOwnerHero"] = newOwner.StringId,
                        ["ownerClan"] = town.OwnerClan == null ? null : town.OwnerClan.StringId, ["faction"] = town.MapFaction == null ? null : town.MapFaction.StringId,
                        ["villages"] = villages.Select(v => v.StringId + ":" + (v.OwnerClan == null ? "-" : v.OwnerClan.StringId) + ":" + (v.MapFaction == null ? "-" : v.MapFaction.StringId)).ToList(),
                        ["inNewFiefs"] = newOwner.Clan.Settlements.Contains(town), ["inOldFiefs"] = oldClan.Settlements.Contains(town),
                        ["garrisonFaction"] = garrison == null ? null : garrison.MapFaction == null ? null : garrison.MapFaction.StringId,
                        ["notablesKept"] = notables.SequenceEqual(town.Notables.Select(h => h.StringId).OrderBy(x => x, StringComparer.Ordinal)),
                        ["targetKingdomSurvives"] = !k2.IsEliminated, ["stillAtWar"] = k1.IsAtWarWith(k2),
                        ["governor"] = town.Town.Governor == null ? null : town.Town.Governor.StringId + ":" + (town.Town.Governor.Clan == null ? "-" : town.Town.Governor.Clan.StringId),
                        ["event"] = events.Any(e => e.StartsWith("ownerChanged " + town.StringId + " ", StringComparison.Ordinal) && e.Contains(" " + newOwner.StringId + " ")) };
                    bool ok = town.OwnerClan == newOwner.Clan && town.MapFaction == k1 && villages.All(v => v.OwnerClan == newOwner.Clan && v.MapFaction == k1) && (bool)row["inNewFiefs"] && !(bool)row["inOldFiefs"]
                        && (garrison == null || garrison.MapFaction == k1) && (bool)row["notablesKept"] && (town.Town.Governor == null || town.Town.Governor.Clan == newOwner.Clan) && (bool)row["event"]
                        && !k2.IsEliminated && k1.IsAtWarWith(k2);
                    soak.Expect(ok, "A.ownership", town.StringId + " passed from " + oldClan.StringId + " (" + k2.StringId + ") to " + newOwner.Clan.StringId + " (" + k1.StringId + "): owner, faction, " + villages.Count
                        + " villages, fiefs, garrison, notables, governor and the event follow; " + k2.StringId + " survives at war");
                    report["ownershipTown"] = town.StringId; report["ownershipClan"] = newOwner.Clan.StringId;
                    return row;
                });
                Step(report, "succession.clan", () =>
                {
                    var clan = Clan.All.Where(c => !c.IsEliminated && c.Kingdom != null && c.StringId.StartsWith(prefix, StringComparison.Ordinal) && c != c.Kingdom.RulingClan && c != Clan.PlayerClan
                            && c.Leader != null && c.Leader.IsAlive && !c.Leader.IsPrisoner && c.Leader.CanDie(KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge)
                            && c.Heroes.Any(h => h != c.Leader && h.IsAlive && !h.IsChild) && (report.ContainsKey("ownershipClan") ? c.StringId != (string)report["ownershipClan"] : true))
                        .OrderBy(c => c.Settlements.Count > 0 ? 0 : 1).ThenBy(c => c.StringId, StringComparer.Ordinal).FirstOrDefault();
                    if (clan == null) throw new InvalidOperationException("no non-ruling clan whose leader can die and who has adult kin");
                    var victim = clan.Leader;
                    var fiefs = clan.Settlements.Select(s => s.StringId).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var governed = clan.Settlements.Where(s => s.Town != null && s.Town.Governor == victim).Select(s => s.StringId).ToList();
                    string party = victim.PartyBelongedTo == null ? null : victim.PartyBelongedTo.StringId;
                    KillCharacterAction.ApplyByOldAge(victim, false);
                    var dead = DeadReferences(victim);
                    var row = new Dictionary<string, object> { ["clan"] = clan.StringId, ["victim"] = victim.StringId, ["victimParty"] = party, ["dead"] = victim.IsDead,
                        ["newLeader"] = clan.Leader == null ? null : clan.Leader.StringId, ["newLeaderAlive"] = clan.Leader != null && clan.Leader.IsAlive, ["eliminated"] = clan.IsEliminated,
                        ["fiefsKept"] = fiefs.SequenceEqual(clan.Settlements.Select(s => s.StringId).OrderBy(x => x, StringComparer.Ordinal)), ["fiefs"] = fiefs.Count, ["governedByVictim"] = governed,
                        ["event"] = events.Any(e => e.StartsWith("clanLeaderChanged " + victim.StringId + " ", StringComparison.Ordinal)), ["deadReferences"] = dead };
                    soak.Expect(victim.IsDead && clan.Leader != null && clan.Leader.IsAlive && clan.Leader != victim && clan.Leader.Clan == clan && !clan.IsEliminated && (bool)row["fiefsKept"] && (bool)row["event"] && dead.Count == 0,
                        "A.succession.clan", victim.StringId + " of " + clan.StringId + " died of old age: " + (clan.Leader == null ? "no leader" : clan.Leader.StringId) + " leads the clan, its " + fiefs.Count + " fiefs kept, " + dead.Count + " references to the dead hero");
                    report["heirClan"] = clan.StringId; report["heir"] = clan.Leader == null ? null : clan.Leader.StringId;
                    return row;
                });
                // The installed rule (KillCharacterAction.ApplyInternal): the ruler's clan passes to an adult lord of it if it has one; the
                // candidates are the kingdom's clans not eliminated, not mercenaries and not led by the dead ruler. Two or more: a king
                // selection (KingSelectionKingdomDecision) is added, and Kingdom.AddDecision runs the election of a kingdom other than the
                // player's at once (KingdomElection.StartElection; only the player's kingdom keeps it pending), which applies the elected
                // ruling clan; if the ruler's clan had no heir, ApplyInternal then gives the rule to another eligible clan, overriding the
                // election. One: that clan rules. None: the kingdom is destroyed. A kingdom with two or more candidates is preferred.
                Step(report, "succession.ruler", () =>
                {
                    Func<Kingdom, List<Clan>> candidates = k => k.Clans.Where(c => !c.IsEliminated && !c.IsUnderMercenaryService
                        && (c.Leader != k.Leader || c.Heroes.Any(h => h != k.Leader && h.IsAlive && !h.IsChild && h.IsLord))).OrderBy(c => c.StringId, StringComparer.Ordinal).ToList();
                    var kingdom = kingdoms.Where(k => !k.IsEliminated && k.Leader != null && k.Leader.IsAlive && k.Leader.CanDie(KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge))
                        .OrderBy(k => candidates(k).Count >= 2 ? 0 : 1).ThenBy(k => k == k1 || k == k2 ? 1 : 0).ThenBy(k => k.StringId, StringComparer.Ordinal).FirstOrDefault();
                    if (kingdom == null) throw new InvalidOperationException("no kingdom whose ruler can die");
                    var victim = kingdom.Leader; var rulingBefore = kingdom.RulingClan;
                    var eligible = candidates(kingdom);
                    bool heir = rulingBefore.Heroes.Any(h => h != victim && h.IsAlive && !h.IsChild && h.IsLord);
                    string branch = eligible.Count >= 2 ? "king selection" : eligible.Count == 1 ? "sole candidate rules" : "kingdom destroyed";
                    int mark = events.Count;
                    KillCharacterAction.ApplyByOldAge(victim, false);
                    var dead = DeadReferences(victim);
                    bool decision = kingdom.UnresolvedDecisions.Any(d => d is KingSelectionKingdomDecision);
                    var after = events.Skip(mark).ToList();
                    bool decided = after.Contains("decisionAdded KingSelectionKingdomDecision " + kingdom.StringId);
                    var elected = after.Where(e => e.StartsWith("rulingClanChanged " + kingdom.StringId + " ", StringComparison.Ordinal)).Select(e => e.Substring(e.LastIndexOf(' ') + 1)).ToList();
                    bool playerKingdom = Clan.PlayerClan.Kingdom == kingdom;
                    var row = new Dictionary<string, object> { ["kingdom"] = kingdom.StringId, ["victim"] = victim.StringId, ["dead"] = victim.IsDead, ["rulingClanBefore"] = rulingBefore.StringId,
                        ["candidates"] = eligible.Select(c => c.StringId).ToList(), ["rulingClanHadHeir"] = heir, ["branch"] = branch,
                        ["rulingClan"] = kingdom.RulingClan == null ? null : kingdom.RulingClan.StringId, ["leader"] = kingdom.Leader == null ? null : kingdom.Leader.StringId,
                        ["leaderAlive"] = kingdom.Leader != null && kingdom.Leader.IsAlive, ["eliminated"] = kingdom.IsEliminated, ["kingSelectionPending"] = decision,
                        ["rulingClanChanged"] = kingdom.RulingClan != rulingBefore, ["kingSelectionAdded"] = decided, ["kingSelectionConcluded"] = after.Contains("decisionConcluded KingSelectionKingdomDecision " + kingdom.StringId),
                        ["rulingClansApplied"] = elected, ["playerKingdom"] = playerKingdom, ["deadReferences"] = dead };
                    bool ruled = !kingdom.IsEliminated && kingdom.Leader != null && kingdom.Leader.IsAlive && kingdom.RulingClan != null && kingdom.RulingClan.Leader == kingdom.Leader;
                    bool rule = eligible.Count >= 2 ? ruled && decided && decision == playerKingdom && elected.Count > 0 && kingdom.RulingClan.StringId == elected.Last() && eligible.Contains(kingdom.RulingClan)
                            && (heir || kingdom.RulingClan != rulingBefore)
                        : eligible.Count == 1 ? ruled && !decided && kingdom.RulingClan == eligible[0]
                        : kingdom.IsEliminated;
                    soak.Expect(victim.IsDead && rule && dead.Count == 0, "A.succession.ruler",
                        victim.StringId + ", ruler of " + kingdom.StringId + ", died of old age (" + eligible.Count + " candidate clans: " + branch + "): " + (kingdom.Leader == null ? "no leader" : kingdom.Leader.StringId)
                        + " leads it, ruling clan " + (kingdom.RulingClan == null ? "none" : kingdom.RulingClan.StringId) + (decided ? ", a king selection " + (decision ? "pending" : "held at once, electing " + String.Join(" then ", elected)) : "") + ", " + dead.Count + " references to the dead ruler");
                    report["rulerKingdom"] = kingdom.StringId;
                    return row;
                });
                if (k1 != null) { report["warDeclarer"] = k1.StringId; report["warTarget"] = k2.StringId; }
            }
            finally
            {
                foreach (var e in new IMbEventBase[] { CampaignEvents.WarDeclared, CampaignEvents.OnSettlementOwnerChangedEvent, CampaignEvents.HeroKilledEvent, CampaignEvents.OnClanLeaderChangedEvent,
                    CampaignEvents.RulingClanChanged, CampaignEvents.KingdomDecisionAdded, CampaignEvents.KingdomDecisionConcluded, CampaignEvents.KingdomDestroyedEvent })
                    e.ClearListeners(owner);
            }
            foreach (var key in new[] { "war", "ownership", "succession.clan", "succession.ruler" })
                if (report.ContainsKey(key + ":error")) soak.Expect(false, "A." + key, "the diagnostic step ran: " + report[key + ":error"]);
        }

        // What the simulation left of the diagnostic changes after the following hours (recorded, not required: the AI may make peace,
        // retake the town or a kingdom may be dissolved; elimination is told apart from peace).
        private static Dictionary<string, object> FollowUp(Dictionary<string, object> diagnostics)
        {
            var row = new Dictionary<string, object>();
            if (diagnostics.ContainsKey("warDeclarer"))
            {
                var k1 = Kingdom.All.FirstOrDefault(k => k.StringId == (string)diagnostics["warDeclarer"]); var k2 = Kingdom.All.FirstOrDefault(k => k.StringId == (string)diagnostics["warTarget"]);
                row["declarerEliminated"] = k1 == null || k1.IsEliminated; row["targetEliminated"] = k2 == null || k2.IsEliminated;
                row["stillAtWar"] = k1 != null && k2 != null && !k1.IsEliminated && !k2.IsEliminated && k1.IsAtWarWith(k2);
                row["partiesTargetingEnemy"] = MobileParty.All.Count(p => p.IsActive && p.IsLordParty && k1 != null && k2 != null && (p.MapFaction == k1 || p.MapFaction == k2)
                    && (p.TargetSettlement != null && p.TargetSettlement.MapFaction != null && p.TargetSettlement.MapFaction.IsAtWarWith(p.MapFaction)
                        || p.TargetParty != null && p.TargetParty.MapFaction != null && p.TargetParty.MapFaction.IsAtWarWith(p.MapFaction)));
                row["armies"] = (k1 == null ? 0 : k1.Armies.Count) + (k2 == null ? 0 : k2.Armies.Count);
            }
            if (diagnostics.ContainsKey("ownershipTown"))
            {
                var town = Settlement.Find((string)diagnostics["ownershipTown"]);
                row["townOwner"] = town == null || town.OwnerClan == null ? null : town.OwnerClan.StringId;
                row["townBesieged"] = town != null && town.SiegeEvent != null;
            }
            if (diagnostics.ContainsKey("heirClan"))
            {
                var clan = Clan.All.FirstOrDefault(c => c.StringId == (string)diagnostics["heirClan"]);
                row["heirLeads"] = clan != null && clan.Leader != null && clan.Leader.IsAlive ? clan.Leader.StringId : null;
            }
            if (diagnostics.ContainsKey("rulerKingdom"))
            {
                var kingdom = Kingdom.All.FirstOrDefault(k => k.StringId == (string)diagnostics["rulerKingdom"]);
                row["ruler"] = kingdom == null || kingdom.Leader == null ? null : kingdom.Leader.StringId + (kingdom.Leader.IsAlive ? "" : " (dead)");
                row["kingSelectionPending"] = kingdom != null && kingdom.UnresolvedDecisions.Any(d => d is KingSelectionKingdomDecision);
            }
            return row;
        }

        // Every place a dead hero must no longer be: a clan's or kingdom's leader, a party's leader or member, a governor, a notable.
        private static List<string> DeadReferences(Hero hero)
        {
            var found = new List<string>();
            foreach (var c in Clan.All) if (c.Leader == hero) found.Add("leader of clan " + c.StringId);
            foreach (var k in Kingdom.All) if (k.Leader == hero) found.Add("leader of kingdom " + k.StringId);
            foreach (var p in MobileParty.All) if (p.IsActive && (p.LeaderHero == hero || p.MemberRoster.Contains(hero.CharacterObject))) found.Add("in party " + p.StringId);
            foreach (var s in Settlement.All)
            {
                if (s.Town != null && s.Town.Governor == hero) found.Add("governor of " + s.StringId);
                if (s.Notables.Contains(hero)) found.Add("notable of " + s.StringId);
            }
            return found;
        }

        // ---------------------------------------------------------------- state

        // The campaign's state checked for coherence: every reference the simulation dereferences, objects outside the world, parties off
        // navigation, the census and the economy.
        private static Dictionary<string, object> Audit(string prefix)
        {
            var invalid = new List<string>(); int invalidCount = 0;
            Action<string> bad = text => { invalidCount++; if (invalid.Count < 50) invalid.Add(text); };
            var unexpected = new List<string>();
            Action<string, string> outside = (kind, id) => { if (CampaignIdentity.WorldPrefix(id) != prefix && !CampaignIdentity.IsAlias(id) && unexpected.Count < 50) unexpected.Add(kind + " " + id); };
            Action<string, Action> check = (what, body) => { try { body(); } catch (Exception error) { bad(what + ": check threw " + error.GetType().Name + ": " + error.Message); } };
            var census = new Dictionary<string, int>(StringComparer.Ordinal);
            Action<string> count = key => { int v; census.TryGetValue(key, out v); census[key] = v + 1; };
            var noTradeBound = new List<string>();
            foreach (var s in Settlement.All)
            {
                check("settlement " + s.StringId, () =>
                {
                    outside("settlement", s.StringId);
                    count("settlement:" + SettlementKind(s));
                    if (s.Culture == null) bad("settlement " + s.StringId + " has no culture");
                    if (s.IsTown || s.IsCastle)
                    {
                        if (s.OwnerClan == null) bad(SettlementKind(s) + " " + s.StringId + " has no owner clan");
                        if (s.MapFaction == null) bad(SettlementKind(s) + " " + s.StringId + " has no faction");
                        if (s.Town.GarrisonParty == null || !s.Town.GarrisonParty.IsActive) bad(SettlementKind(s) + " " + s.StringId + " has no garrison"); else count("garrison");
                        if (s.Town.Governor != null && (!s.Town.Governor.IsAlive || s.Town.Governor.Clan != s.OwnerClan)) bad(SettlementKind(s) + " " + s.StringId + " has governor " + s.Town.Governor.StringId + " outside its owner clan or dead");
                        if (Single.IsNaN(s.Town.FoodStocks) || Single.IsNaN(s.Town.Prosperity) || Single.IsNaN(s.Town.Loyalty) || Single.IsNaN(s.Town.Security)) bad(SettlementKind(s) + " " + s.StringId + " has a NaN food, prosperity, loyalty or security");
                    }
                    if (s.IsTown && s.Notables.Count == 0) bad("town " + s.StringId + " has no notables");
                    if (s.IsVillage)
                    {
                        var bound = s.Village.Bound;
                        if (bound == null || !(bound.IsTown || bound.IsCastle)) bad("village " + s.StringId + " is bound to " + (bound == null ? "nothing" : bound.StringId));
                        else if (s.OwnerClan != bound.OwnerClan) bad("village " + s.StringId + " is owned by " + (s.OwnerClan == null ? "no clan" : s.OwnerClan.StringId) + ", its town by " + (bound.OwnerClan == null ? "no clan" : bound.OwnerClan.StringId));
                        // Its villagers sell in its trade town: the bound town, or for a castle's village the town the installed trade model
                        // assigns within DefaultVillageTradeModel.TradeBoundDistanceLimitAsDays (none beyond it).
                        if (s.Village.TradeBound == null) { count("village:noTradeBound"); if (noTradeBound.Count < 30) noTradeBound.Add(s.StringId + " (bound to " + (bound == null ? "-" : bound.StringId) + ")"); }
                    }
                    var hideout = s.SettlementComponent as Hideout;
                    if (hideout != null && hideout.IsInfested) count("hideout:infested");
                    foreach (var n in s.Notables) if (!n.IsAlive) bad("settlement " + s.StringId + " lists dead notable " + n.StringId);
                });
            }
            foreach (var c in Clan.All)
            {
                check("clan " + c.StringId, () =>
                {
                    outside("clan", c.StringId);
                    if (c.IsEliminated) { count("clan:eliminated"); return; }
                    count(c.IsBanditFaction ? "clan:bandit" : c.IsMinorFaction ? "clan:minor" : "clan:noble");
                    if (!c.IsBanditFaction && c.Leader == null) bad("clan " + c.StringId + " has no leader");
                    if (c.Leader != null && !c.Leader.IsAlive) bad("clan " + c.StringId + " is led by dead " + c.Leader.StringId);
                    if (c.Kingdom != null && c.Kingdom.IsEliminated) bad("clan " + c.StringId + " is in eliminated kingdom " + c.Kingdom.StringId);
                    if (c.Culture == null) bad("clan " + c.StringId + " has no culture");
                });
            }
            foreach (var k in Kingdom.All)
            {
                check("kingdom " + k.StringId, () =>
                {
                    outside("kingdom", k.StringId);
                    if (k.IsEliminated) { count("kingdom:eliminated"); return; }
                    count("kingdom");
                    int f = k.Settlements.Count(s => s.IsTown || s.IsCastle);
                    count("kingdom:fiefs:" + (f >= 4 ? "4+" : f.ToString(CultureInfo.InvariantCulture)));
                    if (k.RulingClan == null || k.Leader == null || !k.Leader.IsAlive) bad("kingdom " + k.StringId + " has no living ruler");
                    else if (k.RulingClan.Kingdom != k) bad("kingdom " + k.StringId + "'s ruling clan " + k.RulingClan.StringId + " is not in it");
                });
            }
            foreach (var h in Hero.AllAliveHeroes)
            {
                check("hero " + h.StringId, () =>
                {
                    count(h.IsLord ? "hero:lord" : h.IsNotable ? "hero:notable" : h.IsWanderer ? "hero:wanderer" : "hero:other");
                    if (h.IsLord && h.Clan == null) bad("lord " + h.StringId + " has no clan");
                    if (h.PartyBelongedTo != null && !h.PartyBelongedTo.IsActive) bad("hero " + h.StringId + " belongs to inactive party " + h.PartyBelongedTo.StringId);
                    if (h.GovernorOf != null && h.GovernorOf.Settlement.OwnerClan != h.Clan) bad("hero " + h.StringId + " governs " + h.GovernorOf.Settlement.StringId + " outside its clan");
                    if (h.IsNotable && h.CurrentSettlement == null && h.HomeSettlement == null) bad("notable " + h.StringId + " has no settlement");
                });
            }
            foreach (var h in Hero.DeadOrDisabledHeroes.Where(x => x.IsDead))
                check("dead hero " + h.StringId, () => { if (h.PartyBelongedTo != null && h.PartyBelongedTo.IsActive) bad("dead hero " + h.StringId + " is in party " + h.PartyBelongedTo.StringId); });
            var offNavigation = new List<string>(); int offCount = 0;
            foreach (var p in MobileParty.All)
            {
                if (p == null || !p.IsActive) continue;
                check("party " + p.StringId, () =>
                {
                    count("party:" + KindOf(p));
                    if (p.LeaderHero != null && !p.LeaderHero.IsAlive) bad("party " + p.StringId + " is led by dead " + p.LeaderHero.StringId);
                    if (p.IsLordParty && p.ActualClan == null) bad("lord party " + p.StringId + " has no clan");
                    if ((p.IsCaravan || p.IsVillager) && p.HomeSettlement == null) bad(KindOf(p) + " " + p.StringId + " has no home settlement");
                    if (p.IsVillager && p.HomeSettlement != null && !p.HomeSettlement.IsVillage) bad("villager party " + p.StringId + " is homed at " + p.HomeSettlement.StringId);
                    if (p.Army != null && (p.Army.LeaderParty == null || !p.Army.LeaderParty.IsActive)) bad("party " + p.StringId + " is in an army without an active leader");
                    if (p.MapFaction == null) bad("party " + p.StringId + " has no faction");
                    if (p.CurrentSettlement == null)
                    {
                        string problem = GeneratedSpawnAssertions.Problem(p);
                        if (problem != null) { offCount++; if (offNavigation.Count < 30) offNavigation.Add(KindOf(p) + " " + p.StringId + ": " + problem); }
                    }
                });
            }
            var economy = Economy();
            var noGold = new List<string>(); var noTradeGoods = new List<string>();
            foreach (var row in (List<object>)economy["towns"])
            {
                var t = (Dictionary<string, object>)row;
                if (Convert.ToDouble(t["food"], CultureInfo.InvariantCulture) <= 0) count("town:noFood");
                if ((int)t["gold"] <= 0) { count("town:noGold"); noGold.Add((string)t["id"]); }
                // A market sold out of trade goods is a state of the economy (run 34968: a town sold its last stack 30 hours in), not an
                // invalid reference: recorded; the summary requires every town to stock trade goods at initialization.
                if ((int)t["tradeGoods"] == 0) { count("town:noTradeGoods"); noTradeGoods.Add((string)t["id"]); }
                if ((int)t["volunteers"] == 0) count("town:noVolunteers");
            }
            return new Dictionary<string, object> { ["campaignDays"] = CampaignTime.Now.ToDays, ["census"] = census.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)k.Value),
                ["invalid"] = invalid, ["invalidCount"] = invalidCount, ["unexpected"] = unexpected, ["unexpectedCount"] = unexpected.Count,
                ["offNavigation"] = offNavigation, ["offNavigationCount"] = offCount, ["wars"] = Wars(), ["villagesWithoutTradeTown"] = noTradeBound, ["townsWithoutGold"] = noGold, ["townsWithoutTradeGoods"] = noTradeGoods, ["economy"] = economy };
        }

        // Towns' food, prosperity, gold, market and volunteers; the totals.
        private static Dictionary<string, object> Economy()
        {
            var towns = new List<object>();
            double food = 0; long gold = 0; int goods = 0, items = 0, filled = 0, empty = 0;
            foreach (var s in Settlement.All.Where(x => x.IsTown).OrderBy(x => x.StringId, StringComparer.Ordinal))
            {
                int g = 0, n = 0;
                for (int i = 0; i < s.ItemRoster.Count; i++) { var e = s.ItemRoster.GetElementCopyAtIndex(i); if (e.EquipmentElement.Item == null || e.Amount <= 0) continue; n += e.Amount; if (e.EquipmentElement.Item.IsTradeGood) g++; }
                int v = s.Notables.Sum(h => h.VolunteerTypes.Count(x => x != null)), ve = s.Notables.Sum(h => h.VolunteerTypes.Count(x => x == null));
                food += s.Town.FoodStocks; gold += s.Town.Gold; goods += g; items += n; filled += v; empty += ve;
                towns.Add(new Dictionary<string, object> { ["id"] = s.StringId, ["food"] = Math.Round(s.Town.FoodStocks, 2), ["prosperity"] = Math.Round(s.Town.Prosperity, 1), ["gold"] = s.Town.Gold,
                    ["tradeGoods"] = g, ["items"] = n, ["volunteers"] = v });
            }
            int villageItems = 0;
            foreach (var s in Settlement.All.Where(x => x.IsVillage)) for (int i = 0; i < s.ItemRoster.Count; i++) villageItems += Math.Max(0, s.ItemRoster.GetElementCopyAtIndex(i).Amount);
            return new Dictionary<string, object> { ["townCount"] = towns.Count, ["food"] = Math.Round(food, 1), ["gold"] = gold, ["tradeGoodStacks"] = goods, ["marketItems"] = items,
                ["villageItems"] = villageItems, ["volunteerSlotsFilled"] = filled, ["volunteerSlotsEmpty"] = empty, ["towns"] = towns };
        }

        // Every notable's volunteer slots (notable id/slot -> troop id or null).
        private static Dictionary<string, string> Slots()
        {
            var slots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in Settlement.All)
                foreach (var h in s.Notables)
                    for (int i = 0; i < h.VolunteerTypes.Length; i++) slots[h.StringId + "/" + i] = h.VolunteerTypes[i] == null ? null : h.VolunteerTypes[i].StringId;
            return slots;
        }

        private static Dictionary<string, object> Replenishment(Dictionary<string, string> start, Dictionary<string, string> end, Dictionary<string, object> visit)
        {
            int emptyAtStart = start.Count(kv => kv.Value == null), refilled = start.Count(kv => kv.Value == null && end.ContainsKey(kv.Key) && end[kv.Key] != null);
            int emptied = start.Count(kv => kv.Value != null && end.ContainsKey(kv.Key) && end[kv.Key] == null);
            var row = new Dictionary<string, object> { ["slots"] = start.Count, ["emptyAtStart"] = emptyAtStart, ["refilled"] = refilled, ["emptiedDuringDay"] = emptied, ["emptyAtEnd"] = end.Count(kv => kv.Value == null) };
            var recruitment = visit.ContainsKey("recruitment") ? (Dictionary<string, object>)visit["recruitment"] : null;
            if (recruitment != null && recruitment.ContainsKey("notable"))
            {
                string key = recruitment["notable"] + "/" + recruitment["slot"];
                row["playerSlot"] = new Dictionary<string, object> { ["slot"] = key, ["now"] = end.ContainsKey(key) ? end[key] : null, ["refilled"] = end.ContainsKey(key) && end[key] != null,
                    ["dailyRefillChance"] = recruitment["dailyRefillChance"] };
            }
            var trade = visit.ContainsKey("trade") ? (Dictionary<string, object>)visit["trade"] : null;
            if (trade != null && trade.ContainsKey("afterSell"))
            {
                var town = Settlement.Find((string)visit["town"]); var item = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<ItemObject>((string)trade["item"]);
                row["tradedGood"] = new Dictionary<string, object> { ["item"] = trade["item"], ["stockAfterTrade"] = ((Dictionary<string, object>)trade["afterSell"])["townStock"],
                    ["stockAfterDay"] = town == null || item == null ? (object)null : town.ItemRoster.GetItemNumber(item) };
            }
            return row;
        }

        // ---------------------------------------------------------------- world B

        private static HashSet<string> Ids()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in Settlement.All) ids.Add(s.StringId);
            foreach (var c in Clan.All) ids.Add(c.StringId);
            foreach (var k in Kingdom.All) ids.Add(k.StringId);
            foreach (var h in Hero.AllAliveHeroes) ids.Add(h.StringId);
            foreach (var p in MobileParty.All) ids.Add(p.StringId);
            return ids;
        }

        // B's fresh state against A's: nothing of A registered, no id of A's world or derived from it, B's own campaign time and economy.
        private static Dictionary<string, object> Independence(string prefixA, string prefixB, HashSet<string> idsA, Dictionary<string, object> initA)
        {
            var foreign = new List<string>();
            Action<string, string> look = (kind, id) => { if (id != null && CampaignIdentity.WorldOrigin(id) == prefixA && foreign.Count < 30) foreign.Add(kind + " " + id); };
            foreach (var s in Settlement.All) look("settlement", s.StringId);
            foreach (var c in Clan.All) look("clan", c.StringId);
            foreach (var k in Kingdom.All) look("kingdom", k.StringId);
            foreach (var h in Hero.AllAliveHeroes) look("hero", h.StringId);
            foreach (var p in MobileParty.All) look("party", p.StringId);
            var shared = Ids().Where(id => idsA.Contains(id) && !CampaignIdentity.IsAlias(id) && CampaignIdentity.WorldOrigin(id) == null).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var economy = Economy();
            return new Dictionary<string, object> { ["aObjects"] = WorldSnapshot.Total(WorldSnapshot.Objects(prefixA)), ["bObjects"] = WorldSnapshot.Total(WorldSnapshot.Objects(prefixB)),
                ["foreign"] = foreign, ["foreignCount"] = foreign.Count, ["campaignDays"] = CampaignTime.Now.ToDays, ["aStartDays"] = initA["campaignDays"],
                ["unprefixedIdsAlsoInA"] = shared.Take(30).ToList(), ["unprefixedIdsAlsoInACount"] = shared.Count,
                ["townsFed"] = ((List<object>)economy["towns"]).Count(t => Convert.ToDouble(((Dictionary<string, object>)t)["food"], CultureInfo.InvariantCulture) > 0), ["towns"] = economy["townCount"],
                ["marketItems"] = economy["marketItems"], ["volunteerSlotsFilled"] = economy["volunteerSlotsFilled"] };
        }

        // ---------------------------------------------------------------- helpers

        // A party's items; without pack animals, its goods for sale (SellGoodsForTradeAction keeps a villager's pack animals of the cheapest
        // kind and sells the rest, so a villager carrying only pack animals sells nothing).
        private static int Goods(MobileParty p, bool packAnimals = true)
        {
            int n = 0;
            for (int i = 0; i < p.ItemRoster.Count; i++)
            {
                var e = p.ItemRoster.GetElementCopyAtIndex(i);
                if (e.EquipmentElement.Item == null || e.Amount <= 0) continue;
                if (!packAnimals && e.EquipmentElement.Item.ItemCategory == DefaultItemCategories.PackAnimal) continue;
                n += e.Amount;
            }
            return n;
        }

        private static int Count(Dictionary<string, object> row, string key) { object v; return row.TryGetValue(key + "Count", out v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }

        private static void Step(Dictionary<string, object> report, string name, Func<object> step)
        {
            try { report[name] = step(); }
            catch (Exception error) { report[name + ":error"] = error.GetType().Name + ": " + error.Message; }
        }

        private static int Wars()
        {
            var kingdoms = Kingdom.All.Where(k => !k.IsEliminated).ToList();
            int wars = 0;
            for (int i = 0; i < kingdoms.Count; i++) for (int j = i + 1; j < kingdoms.Count; j++) if (kingdoms[i].IsAtWarWith(kingdoms[j])) wars++;
            return wars;
        }

        // War Sails' fishing parties are VillagerPartyComponents (NavalDLC.FishingPartyComponent): their own kind, by type name.
        private static string KindOf(MobileParty p)
        {
            if (p.IsGarrison) return "garrison";
            if (p.IsMilitia) return "militia";
            if (p.IsPatrolParty) return "patrol";
            if (p.PartyComponent != null && p.PartyComponent.GetType().FullName == "NavalDLC.FishingPartyComponent") return "fishing";
            return GeneratedSpawnAssertions.KindOf(p);
        }

        private static bool Flag(object o, string property)
        {
            var info = o == null ? null : o.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return info != null && Equals(info.GetValue(o), true);
        }

        private static string SettlementKind(Settlement s) { return s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : s.IsHideout ? "hideout" : "other"; }

        private static string Target(MobileParty p)
        {
            return (p.TargetSettlement != null ? p.TargetSettlement.StringId : p.TargetParty != null ? p.TargetParty.StringId : "(" + F(p.MoveTargetPoint.ToVec2().x) + ", " + F(p.MoveTargetPoint.ToVec2().y) + ")")
                + " [" + p.ShortTermBehavior + (p.IsCurrentlyAtSea ? ", at sea" : "") + "]";
        }

        // Planar distance, across the seam on a periodic world.
        private static double Distance(Vec2 a, Vec2 b)
        {
            var periodic = PeriodicWorld.Current;
            if (periodic != null) return Math.Sqrt(periodic.Coordinates.GeometricDistanceSquared(a.x, a.y, b.x, b.y));
            return a.Distance(b);
        }

        private static string F(double x) { return x.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
