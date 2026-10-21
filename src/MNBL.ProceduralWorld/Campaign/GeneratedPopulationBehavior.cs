using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-281: a compiled world's houses, companies and wanderers in play (findings §16, §17 RT-04, RT-07, RT-33; the rules are
    // PopulationRuntime.cs, which the offline tests run):
    //   * Hero.IsMinorFactionHero on every company and outlaw-clan member: at new game right after the world registered
    //     (GeneratedGameManager.AfterRegisterSubModuleObjects, before CampaignObjectManager publishes its lists and before any new-game
    //     handler, so before HeroSpawnCampaignBehavior spawns the minor clans' parties in its OnNewGameCreatedPartialFollowUp), and again
    //     once a load finished (the flag is saved; the re-assert covers a hero the save did not carry it for);
    //   * the wanderer tick in place of CompanionsCampaignBehavior's DailyTick (CompanionsTickPatch.Handler): W from the census, the swap
    //     kept, rotation and top-up (WandererRules), made in the installed four steps with the world's reserve names;
    //   * the homeless-house rule (HomelessHouses): a generated house that left a destroyed kingdom joins the nearest kingdom of its culture
    //     (else the nearest) at the next daily tick, inside FactionDiscontinuationCampaignBehavior's 28 days; never the player's clan.
    // InitialChildGenerationCampaignBehavior stays on (owner decision 15): the generated lords are XML heroes, registered before its new-game
    // follow-up runs, so it counts them; nothing here assigns a hero to a house before it runs. The behaviour belongs to the campaign it was
    // added to; reached from another (a stale static), it does nothing and the installed tick runs.
    internal sealed class GeneratedPopulationBehavior : CampaignBehaviorBase
    {
        internal static GeneratedPopulationBehavior Active { get; private set; }
        private Campaign campaign;
        private readonly PopulationPlan plan;
        private readonly Action<string> trace;
        private readonly WandererRules rules;
        private List<string> pendingHouses = new List<string>();
        private List<string> reserveKeys = new List<string>();
        private List<int> reserveCounts = new List<int>();
        private int rotated, toppedUp;
        // MMR-262 (F06): every clan's kingdom change since the behaviour started, "clan:old->new:detail" (mercenary hires and leaves, defections,
        // dissolutions), for the soak census. In memory only (a soak runs without a save in between), capped.
        private const int KingdomChangeCap = 500;
        private readonly List<string> kingdomChanges = new List<string>();
        private int kingdomChangesDropped;
        public List<string> Problems { get; private set; }
        public List<string> HousesMoved { get; private set; }
        public string MinorFactionMarkedAt { get; private set; }
        public MinorFactionHeroes.Result MinorFactionMarks { get; private set; }
        public int Rotated { get { return rotated + rules.Rotated; } }
        public int ToppedUp { get { return toppedUp + rules.ToppedUp; } }
        public int Wanderers { get { return plan.Wanderers; } }

        public GeneratedPopulationBehavior(PopulationPlan plan, Action<string> trace)
        {
            this.plan = plan; this.trace = trace ?? (s => { }); rules = new WandererRules(plan);
            Problems = new List<string>(); HousesMoved = new List<string>();
            campaign = Campaign.Current; Active = this;
            CompanionsTickPatch.Handler = DailyTickPrefix;
        }

        private static GeneratedPopulationBehavior Current()
        {
            var active = Active;
            if (active == null) return null;
            if (active.campaign != null && ReferenceEquals(active.campaign, Campaign.Current)) return active;
            active.trace("population_stale_active_deactivated");
            Deactivate();
            return null;
        }

        public static void Deactivate()
        {
            Active = null;
            CompanionsTickPatch.Handler = null;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, OnGameLoaded);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            if (dataStore.IsSaving)
            {
                rotated = Rotated; toppedUp = ToppedUp; rules.Rotated = 0; rules.ToppedUp = 0;
                reserveKeys = rules.ReserveUses.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                reserveCounts = reserveKeys.Select(k => rules.ReserveUses[k]).ToList();
            }
            dataStore.SyncData("_mnblPopulationPendingHouses", ref pendingHouses);
            dataStore.SyncData("_mnblPopulationReserveKeys", ref reserveKeys);
            dataStore.SyncData("_mnblPopulationReserveCounts", ref reserveCounts);
            dataStore.SyncData("_mnblPopulationRotated", ref rotated);
            dataStore.SyncData("_mnblPopulationToppedUp", ref toppedUp);
            if (!dataStore.IsSaving)
            {
                rules.ReserveUses.Clear();
                for (int i = 0; i < Math.Min(reserveKeys.Count, reserveCounts.Count); i++) rules.ReserveUses[reserveKeys[i]] = reserveCounts[i];
                if (pendingHouses == null) pendingHouses = new List<string>();
            }
        }

        // ---- IsMinorFactionHero

        // New game: right after the world's registration closed (GeneratedGameManager), the XML heroes by id from the object manager.
        internal void MarkAtRegistration()
        {
            Mark("after_registration", id => MBObjectManager.Instance.GetObject<Hero>(id));
        }

        private void OnGameLoaded(CampaignGameStarter starter)
        {
            try
            {
                var heroes = Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes).GroupBy(h => h.StringId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                Mark("load", id => { Hero h; return heroes.TryGetValue(id, out h) ? h : null; });
            }
            catch (Exception error) { Problems.Add("minor faction heroes on load: " + error.Message); }
        }

        private void Mark(string phase, Func<string, Hero> find)
        {
            var r = MinorFactionHeroes.Mark(plan.MinorFactionHeroes, id => find(id), h => ((Hero)h).IsMinorFactionHero, h => ((Hero)h).IsMinorFactionHero = true);
            MinorFactionMarks = r; MinorFactionMarkedAt = MinorFactionMarkedAt == null ? phase : MinorFactionMarkedAt + "," + phase;
            if (r.Missing.Count > 0) Problems.Add("minor faction heroes not found at " + phase + ": " + String.Join(", ", r.Missing.Take(5)));
            trace("population_minor_faction_heroes:" + phase + ":planned=" + r.Planned + ":set=" + r.Set + ":already=" + r.Already + ":missing=" + r.Missing.Count);
        }

        // ---- wanderers

        private static bool DailyTickPrefix(object companions)
        {
            var active = Current();
            if (active == null) return true;
            try { active.Tick(companions); }
            catch (Exception error) { active.Problems.Add("wanderer tick: " + error.GetType().Name + ": " + error.Message); active.trace("population_wanderer_tick_failed:" + error); }
            return false;
        }

        private void Tick(object companions)
        {
            var made = rules.Tick(new LiveWorld(companions));
            foreach (var m in made)
                trace("population_wanderer_made:" + m.Reason + ":" + ((Hero)m.Hero).StringId + ":" + m.Template + ":" + m.Town + (m.Fallback == null ? "" : ":" + m.Fallback));
            while (rules.Problems.Count > 0) { if (Problems.Count < 50) Problems.Add(rules.Problems[0]); rules.Problems.RemoveAt(0); }
        }

        // The engine side of WandererRules.IWorld.
        private sealed class LiveWorld : WandererRules.IWorld
        {
            private readonly object companions;
            public LiveWorld(object companions) { this.companions = companions; }

            public List<WandererRules.Wanderer> Wanderers()
            {
                var main = Hero.MainHero == null ? null : Hero.MainHero.CurrentSettlement;
                return Hero.AllAliveHeroes.Where(h => h.IsWanderer).Select(h => new WandererRules.Wanderer {
                    Hero = h, Id = h.StringId, Template = h.Template == null ? null : h.Template.StringId, Hired = h.CompanionOf != null, Met = h.HasMet,
                    AtPlayerSettlement = h.CurrentSettlement != null && h.CurrentSettlement == main }).ToList();
            }

            public List<WandererRules.Town> Towns()
            {
                var main = Hero.MainHero == null ? null : Hero.MainHero.CurrentSettlement;
                return Town.AllTowns.Where(t => t != null && t.Settlement != null).Select(t => new WandererRules.Town {
                    Id = t.Settlement.StringId, Culture = t.Settlement.Culture == null ? null : t.Settlement.Culture.StringId,
                    Eligible = t.Settlement != main && t.Settlement.SiegeEvent == null && t.Settlement.HeroesWithoutParty.All(h => !h.IsWanderer || h.CompanionOf != null) }).ToList();
            }

            public ISet<string> LivingNames()
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in Hero.AllAliveHeroes) { if (h.Name != null) names.Add(h.Name.ToString()); if (h.FirstName != null) names.Add(h.FirstName.ToString()); }
                return names;
            }

            public float RandomFloat() { return MBRandom.RandomFloat; }
            public int RandomInt(int n) { return n <= 1 ? 0 : MBRandom.RandomInt(n); }
            public int CompanionAge() { return Campaign.Current.Models.AgeModel.HeroComesOfAge + 5 + MBRandom.RandomInt(12); }
            public bool TemplateUsable(string id) { return MBObjectManager.Instance.GetObject<CharacterObject>(id) != null; }
            public void Swap() { CompanionsTickPatch.Current.Swap.Invoke(companions, null); }
            public void Retire(WandererRules.Wanderer w) { KillCharacterAction.ApplyByRemove((Hero)w.Hero, false, true); }
            public object CreateSpecialHero(string template, string town, int age) { return HeroCreator.CreateSpecialHero(MBObjectManager.Instance.GetObject<CharacterObject>(template), Settlement.Find(town), null, null, age); }
            public void AdjustEquipments(object hero) { CompanionsTickPatch.Current.AdjustEquipments.Invoke(companions, new[] { hero }); }
            public void Activate(object hero) { ((Hero)hero).ChangeState(Hero.CharacterStates.Active); }
            public void EnterSettlement(object hero, string town) { EnterSettlementAction.ApplyForCharacterOnly((Hero)hero, Settlement.Find(town)); }
            public string FirstName(object hero) { var h = (Hero)hero; return h.FirstName == null ? h.Name.ToString() : h.FirstName.ToString(); }
            public void SetName(object hero, string name, string firstName) { ((Hero)hero).SetName(new TextObject(name), new TextObject(firstName)); }
        }

        // ---- homeless houses (RT-07)

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom, ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            // The census record of every change first; a failure here never reaches the campaign event or the homeless-house rule below.
            try
            {
                if (kingdomChanges.Count < KingdomChangeCap)
                    kingdomChanges.Add((clan == null ? "?" : clan.StringId) + ":" + (oldKingdom == null ? "-" : oldKingdom.StringId) + "->" + (newKingdom == null ? "-" : newKingdom.StringId) + ":" + detail);
                else kingdomChangesDropped++;
            }
            catch (Exception error) { if (Problems.Count < 50) Problems.Add("kingdom change record: " + error.Message); }
            try
            {
                if (detail != ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction || clan == null || clan == Clan.PlayerClan || !plan.Houses.Contains(clan.StringId)) return;
                if (!pendingHouses.Contains(clan.StringId)) pendingHouses.Add(clan.StringId);
                trace("population_house_homeless:" + clan.StringId + ":from=" + (oldKingdom == null ? "?" : oldKingdom.StringId));
            }
            catch (Exception error) { Problems.Add("homeless house: " + error.Message); }
        }

        private void OnDailyTick()
        {
            if (pendingHouses.Count == 0) return;
            var due = pendingHouses.ToList(); pendingHouses.Clear();
            foreach (var id in due)
            {
                try
                {
                    var clan = Clan.All.FirstOrDefault(c => c.StringId == id);
                    if (clan == null || clan.IsEliminated || clan.Kingdom != null || clan == Clan.PlayerClan) continue;
                    var home = clan.HomeSettlement == null ? Vec2.Zero : clan.HomeSettlement.Position.ToVec2();
                    var house = new HomelessHouses.House { Id = clan.StringId, Culture = clan.Culture == null ? null : clan.Culture.StringId, Player = clan == Clan.PlayerClan,
                                                           Generated = plan.Houses.Contains(clan.StringId), X = home.x, Y = home.y };
                    var realms = Kingdom.All.Select(k => new HomelessHouses.Realm { Id = k.StringId, Culture = k.Culture == null ? null : k.Culture.StringId, Eliminated = k.IsEliminated,
                        PlayerRuled = k.RulingClan == Clan.PlayerClan,
                        Fiefs = k.Settlements.Where(s => s.IsFortification).Select(s => { var p = s.Position.ToVec2(); return new double[] { p.x, p.y }; }).ToList() }).ToList();
                    var target = HomelessHouses.Destination(house, realms, Distance);
                    if (target == null) { Problems.Add("homeless house " + id + ": no kingdom to join"); continue; }
                    var kingdom = Kingdom.All.First(k => k.StringId == target.Id);
                    ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, default(CampaignTime), true);
                    HousesMoved.Add(id + "->" + target.Id);
                    trace("population_house_joined:" + id + ":" + target.Id + (target.Culture == house.Culture ? ":same_culture" : ":nearest"));
                }
                catch (Exception error) { Problems.Add("homeless house " + id + ": " + error.Message); }
            }
        }

        private static double Distance(double ax, double ay, double bx, double by)
        {
            var a = new Vec2((float)ax, (float)ay);
            return PeriodicSpatialQueries.Vec2Distance(ref a, new Vec2((float)bx, (float)by));
        }

        // ---- the census (CampaignSoakProbe at day 0 and after 30+ days; MMR-247 runs it)

        // The player's clan is never a house or a minor clan here: the installed player_faction carries is_minor_faction (SandBox spclans.xml),
        // and the engine's own minor-clan handling skips it (HeroSpawnCampaignBehavior.OnNewGameCreated). MMR-262 (F06) added the details
        // MMR-247's c18/c20 read: per minor clan (kind, heroes, flags, kingdom, mercenary service), per house (kingdom, size, engine children,
        // eliminated; a planned house is kept once eliminated), the fief owners and every kingdom change. Each detail is guarded on its own, so
        // a fault leaves the rest of the census.
        public static Dictionary<string, object> Census()
        {
            var active = Current();
            var kingdoms = Kingdom.All.Where(k => !k.IsEliminated).ToList();
            int wars = 0;
            for (int i = 0; i < kingdoms.Count; i++) for (int j = i + 1; j < kingdoms.Count; j++) if (FactionManager.IsAtWarAgainstFaction(kingdoms[i], kingdoms[j])) wars++;
            var houses = Clan.All.Where(c => !c.IsEliminated && !c.IsMinorFaction && !c.IsBanditFaction && c != Clan.PlayerClan).ToList();
            Func<Hero, bool> engineChild = h => h.IsAlive && h.Age < 18 && h.Father == null && h.Mother == null;
            var minorAll = Clan.All.Where(c => c.IsMinorFaction && !c.IsBanditFaction && c != Clan.PlayerClan).ToList();
            var minor = minorAll.Where(c => !c.IsEliminated).ToList();
            Func<Clan, string> kind = c => c.IsClanTypeMercenary ? "mercenary" : c.IsMafia ? "mafia" : c.IsNomad ? "nomad" : c.IsSect ? "sect" : c.IsOutlaw ? "outlaw" : "other";
            var wanderers = Hero.AllAliveHeroes.Where(h => h.IsWanderer).ToList();
            object houseDetail, housesEliminated, minorClanDetail, fiefOwners;
            try
            {
                var detail = HouseDetail(active == null ? null : active.plan.Houses, engineChild);
                houseDetail = detail; housesEliminated = detail.Values.Count(x => Equals(x["eliminated"], true));
            }
            catch (Exception error) { houseDetail = housesEliminated = Failed(error); }
            try
            {
                minorClanDetail = minorAll.OrderBy(c => c.StringId, StringComparer.Ordinal).ToDictionary(c => c.StringId, c => (object)new Dictionary<string, object> {
                    ["kind"] = kind(c), ["aliveHeroes"] = c.Heroes.Count(h => h.IsAlive), ["flaggedHeroes"] = c.Heroes.Count(h => h.IsAlive && h.IsMinorFactionHero),
                    ["kingdom"] = c.Kingdom == null ? null : c.Kingdom.StringId, ["underMercenaryService"] = c.IsUnderMercenaryService, ["eliminated"] = c.IsEliminated });
            }
            catch (Exception error) { minorClanDetail = Failed(error); }
            try
            {
                fiefOwners = Settlement.All.Where(s => s.IsTown || s.IsCastle).OrderBy(s => s.StringId, StringComparer.Ordinal)
                    .ToDictionary(s => s.StringId, s => (object)(s.OwnerClan == null ? null : s.OwnerClan.StringId));
            }
            catch (Exception error) { fiefOwners = Failed(error); }
            return new Dictionary<string, object> {
                ["days"] = Math.Round(CampaignTime.Now.ToDays, 2),
                ["kingdoms"] = kingdoms.Count, ["kingdomWars"] = wars,
                ["warsByKingdom"] = kingdoms.ToDictionary(k => k.StringId, k => (object)kingdoms.Count(o => o != k && FactionManager.IsAtWarAgainstFaction(k, o))),
                ["houses"] = houses.Count, ["housesWithoutKingdom"] = houses.Count(c => c.Kingdom == null),
                ["houseSizes"] = houses.GroupBy(c => c.Heroes.Count(h => h.IsAlive && !engineChild(h))).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => (object)g.Count()),
                ["adultLords"] = houses.Sum(c => c.Heroes.Count(h => h.IsAlive && h.Age >= 18 && h.IsLord)),
                ["engineChildren"] = houses.Sum(c => c.Heroes.Count(engineChild)),
                ["engineChildrenByHouse"] = houses.OrderBy(c => c.StringId, StringComparer.Ordinal).ToDictionary(c => c.StringId, c => (object)c.Heroes.Count(engineChild)),
                ["houseDetail"] = houseDetail, ["housesEliminated"] = housesEliminated, ["fiefOwners"] = fiefOwners,
                ["minorClans"] = minor.GroupBy(kind).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["minorClanHeroes"] = minor.Sum(c => c.Heroes.Count(h => h.IsAlive)),
                ["minorFactionHeroes"] = minor.Sum(c => c.Heroes.Count(h => h.IsAlive && h.IsMinorFactionHero)),
                ["minorFactionHeroesOutsideMinorClans"] = Hero.AllAliveHeroes.Count(h => h.IsMinorFactionHero && (h.Clan == null || !h.Clan.IsMinorFaction || h.Clan == Clan.PlayerClan)),
                ["minorClanDetail"] = minorClanDetail,
                ["minorFactionMarkedAt"] = active == null ? null : active.MinorFactionMarkedAt,
                ["minorFactionPlanned"] = active == null || active.MinorFactionMarks == null ? (object)null : active.MinorFactionMarks.Planned,
                ["wanderersTarget"] = active == null ? (object)null : active.Wanderers,
                ["wanderers"] = wanderers.Count, ["wanderersFree"] = wanderers.Count(h => h.CompanionOf == null), ["wanderersHired"] = wanderers.Count(h => h.CompanionOf != null),
                ["wanderersRotated"] = active == null ? 0 : active.Rotated, ["wanderersToppedUp"] = active == null ? 0 : active.ToppedUp,
                ["tickPatched"] = CompanionsTickPatch.Current != null,
                ["housesMoved"] = active == null ? new List<string>() : active.HousesMoved.ToList(),
                ["kingdomChanges"] = active == null ? new List<string>() : active.kingdomChanges.ToList(), ["kingdomChangesDropped"] = active == null ? 0 : active.kingdomChangesDropped,
                ["problems"] = active == null ? new List<string>() : active.Problems.Take(20).ToList() };
        }

        // Per house: its kingdom, its alive size without the engine's children, those children, and whether it is eliminated. Every living house
        // (planned or the engine's, such as a rebel clan) and every planned house however it ended (one no longer in Clan.All is "removed").
        // planned: the plan's houses (null: a world without one, whose eliminated houses are all listed).
        private static Dictionary<string, Dictionary<string, object>> HouseDetail(ICollection<string> planned, Func<Hero, bool> engineChild)
        {
            var detail = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var clans = Clan.All.Where(c => !c.IsMinorFaction && !c.IsBanditFaction && c != Clan.PlayerClan && (!c.IsEliminated || planned == null || planned.Contains(c.StringId)));
            foreach (var c in clans.OrderBy(c => c.StringId, StringComparer.Ordinal))
                detail[c.StringId] = new Dictionary<string, object> {
                    ["kingdom"] = c.Kingdom == null ? null : c.Kingdom.StringId, ["size"] = c.Heroes.Count(h => h.IsAlive && !engineChild(h)), ["engineChildren"] = c.Heroes.Count(engineChild),
                    ["eliminated"] = c.IsEliminated, ["planned"] = planned != null && planned.Contains(c.StringId) };
            if (planned != null)
                foreach (var id in planned.Where(id => !detail.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal))
                    detail[id] = new Dictionary<string, object> { ["kingdom"] = null, ["size"] = 0, ["engineChildren"] = 0, ["eliminated"] = true, ["planned"] = true, ["removed"] = true };
            return detail;
        }

        private static string Failed(Exception error) { return "census failed: " + error.GetType().Name + ": " + error.Message; }
    }
}
