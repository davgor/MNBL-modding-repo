using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Observed gameplay on a generated world: campaign time, one recruitment and one market purchase
    // through installed domain actions. No screen is opened, so this is not UI evidence.
    internal static class GeneratedCampaignGameplay
    {
        // shelterPlayer (MMR-34): other parties ignore the main party while time runs (MobileParty.IgnoreByOtherPartiesTill, the installed
        // call used after an escape). With the startup's bandits a world has bandit parties that engage a player waiting at a gate, and this
        // probe does not play encounters (run 39908 stalled on one). Entering the town without an encounter is not an option: the town menu's
        // init then dereferences the missing encounter (run 31864).
        // aliases: the unprefixed ids this world supplies (its dataset's RequiredAliases); any other unprefixed settlement or clan is a leak.
        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, ICollection<string> aliases,
            Dictionary<string, object> report, double days, bool fastForward, bool entryProbe, bool shelterPlayer = false)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var campaign = Campaign.Current;
            var speed = fastForward ? CampaignTimeControlMode.UnstoppableFastForward : CampaignTimeControlMode.UnstoppablePlay;
            report["timeControl"] = speed.ToString();
            report["before"] = Snapshot(worldId, aliases, failures);
            context.Checkpoint();

            double startDays = CampaignTime.Now.ToDays, lastDays = startDays;
            double start = context.Seconds, lastProgress = start, lastBreadcrumb = start;
            var stalls = new List<object>(); report["stalls"] = stalls;
            // Breadcrumbs are flushed per line so a native crash still leaves the last campaign activity.
            var owner = new object();
            Action<string> crumb = text => context.Stage(worldId + ":crumb:" + CampaignTime.Now.ToHours.ToString("F2", CultureInfo.InvariantCulture) + "h:" + text);
            // Listener bodies run inside engine event dispatch; a throw here would escape into the
            // campaign tick (PartyBase.Id throws for a party still inside MobileParty.CreateParty).
            Action<Func<string>> safe = text => { try { crumb(text()); } catch (Exception error) { crumb("crumb_failed:" + error.GetType().Name); } };
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(owner, () => safe(() => "hourly"));
            CampaignEvents.AiHourlyTickEvent.AddNonSerializedListener(owner, (p, think) => safe(() => "ai:" + p.StringId));
            CampaignEvents.MapEventStarted.AddNonSerializedListener(owner, (e, a, d) => safe(() => "map_event:" + (a == null ? null : a.Id) + ">" + (d == null ? null : d.Id)));
            CampaignEvents.SettlementEntered.AddNonSerializedListener(owner, (p, s, h) => safe(() => "enter:" + (p == null ? null : p.StringId) + ">" + s.StringId));
            CampaignEvents.OnSettlementLeftEvent.AddNonSerializedListener(owner, (p, s) => safe(() => "leave:" + p.StringId + "<" + s.StringId));
            CampaignEvents.MobilePartyCreated.AddNonSerializedListener(owner, p => safe(() => "created:" + p.StringId));
            CampaignEvents.PartyVisibilityChangedEvent.AddNonSerializedListener(owner, p => safe(() => "visibility:" + (p.MobileParty != null ? p.MobileParty.StringId : p.Settlement != null ? p.Settlement.StringId : "?")));
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(owner, () => safe(() => "daily"));
            // The handler runs on the throwing thread before unwinding, so Environment.StackTrace is the
            // throw site. A crash with no record here points at native code instead.
            int firstChance = 0;
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onFirstChance = (sender, e) =>
            {
                if (System.Threading.Interlocked.Increment(ref firstChance) > 25) return;
                try { crumb("first_chance:" + System.Threading.Thread.CurrentThread.ManagedThreadId + ":" + e.Exception.GetType().FullName + ":" + e.Exception.Message + "\n" + Environment.StackTrace); }
                catch { }
            };
            AppDomain.CurrentDomain.FirstChanceException += onFirstChance;
            if (entryProbe)
            {
                // Deterministic repro for the first-settlement-entry native fault seen in PID31916.
                var town = Settlement.Find(worldId + "_town_1");
                var ruler = MobileParty.All.Single(p => p.LeaderHero != null && p.LeaderHero.StringId == worldId + "_ruler_1");
                if (ruler.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(ruler);
                crumb("entry_probe_begin:" + ruler.StringId + ">" + town.StringId);
                EnterSettlementAction.ApplyForParty(ruler, town);
                crumb("entry_probe_applied");
                for (int frame = 0; frame < 30; frame++) { boot.Tick(); yield return null; }
                crumb("entry_probe_frames_ok");
                LeaveSettlementAction.ApplyForParty(ruler);
                crumb("entry_probe_left");
                for (int frame = 0; frame < 30; frame++) { boot.Tick(); yield return null; }
                crumb("entry_probe_complete");
                report["entryProbe"] = "ruler party entered and left " + town.StringId + " at pause without fault";
            }
            if (shelterPlayer)
            {
                MobileParty.MainParty.IgnoreByOtherPartiesTill(CampaignTime.Now + CampaignTime.Days((float)days + 1f));
                report["playerIgnoredUntilDays"] = (CampaignTime.Now + CampaignTime.Days((float)days + 1f)).ToDays;
            }
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, speed);
            context.Stage(worldId + ":gameplay_time_running:" + speed);
            crumb("parties " + PartyLine());
            try
            {
                while (CampaignTime.Now.ToDays - startDays < days)
                {
                    if (context.Seconds - lastBreadcrumb >= 1) { lastBreadcrumb = context.Seconds; crumb("parties " + PartyLine()); }
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    double now = CampaignTime.Now.ToDays;
                    if (now > lastDays) { lastDays = now; lastProgress = context.Seconds; }
                    else if (context.Seconds - lastProgress > 15)
                    {
                        // Record what stopped time before nudging it; repeated stalls fail the run.
                        var active = Game.Current.GameStateManager.ActiveState;
                        stalls.Add(new Dictionary<string, object> {
                            ["campaignDays"] = now, ["realSeconds"] = context.Seconds - start,
                            ["timeControl"] = campaign.TimeControlMode.ToString(),
                            ["activeState"] = active == null ? null : active.GetType().FullName,
                            ["topScreen"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName });
                        context.Checkpoint();
                        if (stalls.Count >= 3) { failures.Add("Campaign time stalled repeatedly."); break; }
                        InformationManager.HideInquiry();
                        DiagnosticSandboxBootstrap.Run(campaign, speed);
                        lastProgress = context.Seconds;
                    }
                    if (context.Seconds - start > 600) { failures.Add("Campaign time advancement timed out."); break; }
                    yield return null;
                }
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= onFirstChance;
                report["firstChanceExceptions"] = firstChance;
                CampaignEvents.HourlyTickEvent.ClearListeners(owner); CampaignEvents.AiHourlyTickEvent.ClearListeners(owner);
                CampaignEvents.MapEventStarted.ClearListeners(owner); CampaignEvents.SettlementEntered.ClearListeners(owner);
                CampaignEvents.OnSettlementLeftEvent.ClearListeners(owner); CampaignEvents.MobilePartyCreated.ClearListeners(owner);
                CampaignEvents.PartyVisibilityChangedEvent.ClearListeners(owner); CampaignEvents.DailyTickEvent.ClearListeners(owner);
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            report["campaignDaysAdvanced"] = CampaignTime.Now.ToDays - startDays;
            report["realSecondsForTime"] = context.Seconds - start;
            context.Stage(worldId + ":gameplay_time_paused");
            for (int frame = 0; frame < 5; frame++) { boot.Tick(); yield return null; }

            report["afterTime"] = Snapshot(worldId, aliases, failures);
            report["recruitment"] = Recruit(campaign, failures);
            context.Checkpoint();
            report["trade"] = Buy(failures);
            report["afterTransactions"] = Snapshot(worldId, aliases, failures);
            context.Stage(worldId + ":gameplay_transactions_complete");
            context.Checkpoint();
        }

        // Same domain sequence as installed RecruitmentVM.OnDone for one troop.
        private static Dictionary<string, object> Recruit(Campaign campaign, List<string> failures)
        {
            var result = new Dictionary<string, object>();
            var hero = Hero.MainHero; var party = MobileParty.MainParty;
            Hero seller = null; Settlement source = null; int index = -1; CharacterObject troop = null;
            foreach (var settlement in Settlement.All.OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                foreach (var notable in settlement.Notables.OrderBy(n => n.StringId, StringComparer.Ordinal))
                {
                    int max = campaign.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(hero, notable, -101);
                    for (int i = 0; i <= max && i < notable.VolunteerTypes.Length && troop == null; i++)
                        if (notable.VolunteerTypes[i] != null) { seller = notable; source = settlement; index = i; troop = notable.VolunteerTypes[i]; }
                    if (troop != null) break;
                }
                if (troop != null) break;
            }
            if (troop == null) { failures.Add("No volunteer is available to the player in generated settlements."); return result; }
            int cost = campaign.Models.PartyWageModel.GetTroopRecruitmentCost(troop, hero, false).RoundedResultNumber;
            int goldBefore = hero.Gold, countBefore = party.MemberRoster.GetTroopCount(troop);
            result["settlement"] = source.StringId; result["notable"] = seller.StringId; result["slot"] = index;
            result["troop"] = troop.StringId; result["troopCulture"] = troop.Culture == null ? null : troop.Culture.StringId;
            result["cost"] = cost; result["goldBefore"] = goldBefore; result["countBefore"] = countBefore;
            if (cost > goldBefore) { failures.Add("Recruitment cost exceeds player gold: " + cost); return result; }
            seller.VolunteerTypes[index] = null;
            party.MemberRoster.AddToCounts(troop, 1);
            CampaignEventDispatcher.Instance.OnUnitRecruited(troop, 1);
            GiveGoldAction.ApplyBetweenCharacters(hero, null, cost, true);
            result["goldAfter"] = hero.Gold; result["countAfter"] = party.MemberRoster.GetTroopCount(troop);
            result["slotCleared"] = seller.VolunteerTypes[index] == null;
            if (party.MemberRoster.GetTroopCount(troop) != countBefore + 1 || hero.Gold != goldBefore - cost || seller.VolunteerTypes[index] != null)
                failures.Add("Recruitment did not apply the expected roster, gold and volunteer changes.");
            return result;
        }

        private static Dictionary<string, object> Buy(List<string> failures)
        {
            var result = new Dictionary<string, object>();
            var party = MobileParty.MainParty; var hero = Hero.MainHero;
            foreach (var town in Settlement.All.Where(s => s.IsTown).OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                for (int i = 0; i < town.ItemRoster.Count; i++)
                {
                    var element = town.ItemRoster.GetElementCopyAtIndex(i);
                    var item = element.EquipmentElement.Item;
                    if (item == null || element.Amount <= 0 || !item.IsTradeGood) continue;
                    int price = town.Town.GetItemPrice(element.EquipmentElement, party, false);
                    if (price <= 0 || price > hero.Gold) continue;
                    int townBefore = town.ItemRoster.GetItemNumber(item), partyBefore = party.ItemRoster.GetItemNumber(item);
                    int goldBefore = hero.Gold, townGoldBefore = town.Town.Gold, taxBefore = town.Town.TradeTaxAccumulated;
                    result["settlement"] = town.StringId; result["item"] = item.StringId; result["price"] = price;
                    result["townStockBefore"] = townBefore; result["partyStockBefore"] = partyBefore;
                    result["goldBefore"] = goldBefore; result["townGoldBefore"] = townGoldBefore; result["tradeTaxBefore"] = taxBefore;
                    SellItemsAction.Apply(town.Party, party.Party, new ItemRosterElement(element.EquipmentElement, 1), 1, town);
                    result["townStockAfter"] = town.ItemRoster.GetItemNumber(item); result["partyStockAfter"] = party.ItemRoster.GetItemNumber(item);
                    result["goldAfter"] = hero.Gold; result["townGoldAfter"] = town.Town.Gold; result["tradeTaxAfter"] = town.Town.TradeTaxAccumulated;
                    // The town's gold plus accumulated trade tax accounts for the player's whole payment.
                    result["townSideReceived"] = (town.Town.Gold - townGoldBefore) + (town.Town.TradeTaxAccumulated - taxBefore);
                    if (town.ItemRoster.GetItemNumber(item) != townBefore - 1 || party.ItemRoster.GetItemNumber(item) != partyBefore + 1 || goldBefore - hero.Gold != price)
                        failures.Add("Market purchase did not move one item from the town to the player for exactly the quoted price.");
                    return result;
                }
            }
            failures.Add("No affordable trade good is stocked in a generated town.");
            return result;
        }

        private static string PartyLine()
        {
            return String.Join(";", MobileParty.All.OrderBy(p => p.StringId, StringComparer.Ordinal).Select(p =>
                p.StringId + "@" + p.Position.X.ToString("F2", CultureInfo.InvariantCulture) + "," + p.Position.Y.ToString("F2", CultureInfo.InvariantCulture) +
                (p.IsMoving ? ",moving" : "") + (p.IsCurrentlyAtSea ? ",sea" : "") + "," + p.ShortTermBehavior +
                (p.TargetSettlement == null ? "" : ">" + p.TargetSettlement.StringId)));
        }

        private static Dictionary<string, object> Snapshot(string worldId, ICollection<string> aliases, List<string> failures)
        {
            var report = new Dictionary<string, object> { ["campaignDays"] = CampaignTime.Now.ToDays };
            var settlements = new List<object>(); report["settlements"] = settlements;
            foreach (var s in Settlement.All.OrderBy(x => x.StringId, StringComparer.Ordinal))
            {
                if (!s.StringId.StartsWith(worldId + "_", StringComparison.Ordinal) && !aliases.Contains(s.StringId)) failures.Add("Unexpected settlement: " + s.StringId);
                var row = new Dictionary<string, object> {
                    ["id"] = s.StringId, ["ownerClan"] = s.OwnerClan == null ? null : s.OwnerClan.StringId,
                    ["notables"] = s.Notables.OrderBy(n => n.StringId, StringComparer.Ordinal).Select(n => new Dictionary<string, object> {
                        ["id"] = n.StringId, ["occupation"] = n.Occupation.ToString(),
                        ["volunteers"] = n.VolunteerTypes.Select(v => v == null ? null : v.StringId).ToArray() }).ToArray(),
                    ["itemStacks"] = s.ItemRoster.Count, ["itemCount"] = Enumerable.Range(0, s.ItemRoster.Count).Sum(i => s.ItemRoster.GetElementCopyAtIndex(i).Amount)
                };
                if (s.IsTown)
                {
                    row["prosperity"] = s.Town.Prosperity; row["foodStocks"] = s.Town.FoodStocks; row["gold"] = s.Town.Gold;
                    var goods = new List<object>(); row["tradeGoods"] = goods;
                    for (int i = 0; i < s.ItemRoster.Count; i++)
                    {
                        var e = s.ItemRoster.GetElementCopyAtIndex(i); var item = e.EquipmentElement.Item;
                        if (item != null && item.IsTradeGood)
                            goods.Add(new Dictionary<string, object> { ["item"] = item.StringId, ["amount"] = e.Amount,
                                ["buyPrice"] = s.Town.GetItemPrice(e.EquipmentElement, MobileParty.MainParty, false) });
                    }
                }
                settlements.Add(row);
            }
            foreach (var clan in Clan.All)
                if (!aliases.Contains(clan.StringId) && !clan.StringId.StartsWith(worldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected clan: " + clan.StringId);
            foreach (var kingdom in Kingdom.All)
                if (!kingdom.StringId.StartsWith(worldId + "_", StringComparison.Ordinal)) failures.Add("Unexpected kingdom: " + kingdom.StringId);
            report["parties"] = MobileParty.All.OrderBy(p => p.StringId, StringComparer.Ordinal).Select(p => new Dictionary<string, object> {
                ["id"] = p.StringId, ["position"] = new[] { p.Position.X, p.Position.Y }, ["moving"] = p.IsMoving,
                ["size"] = p.MemberRoster.TotalManCount }).ToArray();
            report["heroCount"] = Hero.AllAliveHeroes.Count;
            report["playerGold"] = Hero.MainHero.Gold; report["playerPartySize"] = MobileParty.MainParty.MemberRoster.TotalManCount;
            return report;
        }
    }
}
