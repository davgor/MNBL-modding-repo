using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The record classes are filled by the in-game side and the tests; a harness that compiles only the dataset never assigns them.
#pragma warning disable 0649

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-281 (findings §16, §17 RT-04, RT-07, RT-33): the runtime rules that keep a generated world's houses, companies and wanderers
    // intact in game, pure managed (no engine or Harmony type, so every harness that compiles CompiledWorldDataset compiles it) and run by
    // the offline tests (tests/Campaign/PopulationRuntimeTests.cs); the in-game side is GeneratedPopulationBehavior, the patch
    // CompanionsTickPatch. Docs: docs/heroes.md "Wanderers in play", docs/factions.md "Minor clans in play" and "Homeless houses".

    // A runtime wanderer's name from campaign.json wandererReserve (the population pass): an FWGC++ resident with its sex and an epithet.
    internal sealed class ReserveName
    {
        public string Name, Epithet, Presentation, Civilization;
        public bool Female;
        public string Key { get { return Presentation + "|" + Name; } }
    }

    // What the runtime reads of a compiled world's population (CompiledWorldDataset.Population; a fixture world has none).
    internal sealed class PopulationPlan
    {
        public int Wanderers;                                    // W: campaign.json census.wanderers (ceil of the float32 0.6 x towns)
        public double RotationChance;                            // population.json wanderers.rotation.dailyChance (vanilla's KillChance)
        public List<string> MinorFactionHeroes = new List<string>();   // every company and outlaw-clan member (FactionFactory.MinorFactionHeroIds)
        public HashSet<string> Houses = new HashSet<string>(StringComparer.Ordinal);   // the noble houses (the homeless-house rule's clans)
        public List<ReserveName> Reserve = new List<ReserveName>();
        public WandererPool Pool;

        public static PopulationPlan From(IDictionary<string, object> campaign, FactionFactory.Result factions, IDictionary<string, object> population)
        {
            var plan = new PopulationPlan { Pool = WandererPool.FromPolicy(population) };
            object v;
            var census = campaign.TryGetValue("census", out v) ? v as IDictionary<string, object> : null;
            if (census == null || !census.TryGetValue("wanderers", out v)) throw new InvalidOperationException("campaign.json v2 has no census.wanderers (W)");
            plan.Wanderers = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            var rotation = (IDictionary<string, object>)((IDictionary<string, object>)population["wanderers"])["rotation"];
            if ((string)rotation["retire"] != "unmetUnhired") throw new InvalidOperationException(WandererPool.PolicyPath + " wanderers.rotation must retire only unmet, unhired wanderers");
            plan.RotationChance = Convert.ToDouble(rotation["dailyChance"], CultureInfo.InvariantCulture);
            plan.MinorFactionHeroes.AddRange(factions.MinorFactionHeroIds);
            plan.Houses.UnionWith(factions.Clans.Select(c => c.Id));
            if (!campaign.TryGetValue("wandererReserve", out v) || !(v is IEnumerable)) throw new InvalidOperationException("campaign.json v2 has no wandererReserve (MMR-281)");
            foreach (var r in ((IEnumerable)v).Cast<IDictionary<string, object>>())
            {
                object e;
                plan.Reserve.Add(new ReserveName { Name = (string)r["name"], Presentation = (string)r["presentation"], Civilization = (string)r["civilization"], Female = (bool)r["female"],
                                                   Epithet = r.TryGetValue("epithet", out e) ? e as string : null });
            }
            return plan;
        }
    }

    // The wanderer tick that replaces CompanionsCampaignBehavior's DailyTick (RT-04, RT-33): the installed SwapCompanions runs; the
    // installed TryKillCompanion never does. Instead, at vanilla's daily chance one unmet, unhired wanderer away from the player's settlement
    // retires without a death notice and a fresh FWGC++-style wanderer replaces it; and while fewer than W heroes of occupation Wanderer are
    // alive (hired included, as vanilla counts) one more is made each day (vanilla's TrySpawnNewCompanion pace). Heroes are counted, not
    // templates. A wanderer is made in MMR-34's four steps, CreateSpecialHero from a template of the backstory-safe pool (MMR-287), the
    // installed AdjustEquipments, ChangeState(Active), EnterSettlementAction, then named: a reserve name of the town's culture with the
    // reserve's sex and epithet (else the template's epithet).
    internal sealed class WandererRules
    {
        public sealed class Wanderer { public object Hero; public string Id, Template; public bool Hired, Met, AtPlayerSettlement; }
        // Eligible: vanilla's spawn town (not the player's settlement, not besieged, no free wanderer in it).
        public sealed class Town { public string Id, Culture; public bool Eligible; }

        public interface IWorld
        {
            List<Wanderer> Wanderers();          // the alive heroes of occupation Wanderer, hired included
            List<Town> Towns();
            ISet<string> LivingNames();          // every alive hero's name and first name
            float RandomFloat();
            int RandomInt(int n);                // [0, n)
            int CompanionAge();                  // vanilla's: HeroComesOfAge + 5 + RandomInt(12)
            bool TemplateUsable(string id);      // registered in this campaign
            void Swap();                         // the installed SwapCompanions
            void Retire(Wanderer w);             // KillCharacterAction.ApplyByRemove(hero, showNotification false, isForced true)
            object CreateSpecialHero(string template, string town, int age);
            void AdjustEquipments(object hero);  // the installed CompanionsCampaignBehavior.AdjustEquipments
            void Activate(object hero);          // Hero.ChangeState(Active)
            void EnterSettlement(object hero, string town);
            string FirstName(object hero);
            void SetName(object hero, string name, string firstName);
        }

        public sealed class Made
        {
            public string Reason, Town, Culture, Template, TemplateCulture, Fallback, Name, Epithet, Reserve;
            public bool Female;
            public object Hero;
            public List<string> Steps = new List<string>();
        }

        private readonly PopulationPlan plan;
        public Dictionary<string, int> ReserveUses = new Dictionary<string, int>(StringComparer.Ordinal);   // saved with the campaign
        public int Rotated, ToppedUp;
        public List<string> Problems = new List<string>();

        public WandererRules(PopulationPlan plan) { this.plan = plan; }

        public List<Made> Tick(IWorld world)
        {
            world.Swap();
            var made = new List<Made>();
            // The installed comparison, in float: TryKillCompanion skips when MBRandom.RandomFloat > KillChance (0.1f).
            if (world.RandomFloat() <= (float)plan.RotationChance)
            {
                var free = world.Wanderers().Where(w => !w.Met && !w.Hired && !w.AtPlayerSettlement).ToList();
                if (free.Count > 0)
                {
                    var retiring = free[world.RandomInt(free.Count)];
                    world.Retire(retiring);
                    Rotated++;
                    var fresh = Make(world, "rotation:" + retiring.Id);
                    if (fresh != null) made.Add(fresh);
                }
            }
            if (world.Wanderers().Count < plan.Wanderers)
            {
                var fresh = Make(world, "topUp");
                if (fresh != null) { made.Add(fresh); ToppedUp++; }
            }
            return made;
        }

        // One fresh wanderer in a town vanilla would spawn in (null, recorded, when none or no template fits).
        public Made Make(IWorld world, string reason)
        {
            var towns = world.Towns().Where(t => t.Eligible).ToList();
            if (towns.Count == 0) { Problems.Add(reason + ": no town without a free wanderer"); return null; }
            var town = towns[world.RandomInt(towns.Count)];
            var living = world.LivingNames();
            string reserveNote;
            var entry = PickReserve(town.Culture, living, world, out reserveNote);
            var uses = world.Wanderers().Where(w => w.Template != null).GroupBy(w => w.Template).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var choice = plan.Pool.Pick(town.Culture, entry == null ? (bool?)null : entry.Female, uses, world.TemplateUsable, n => world.RandomInt(n));
            if (choice == null) { Problems.Add(reason + ": no backstory-safe template for " + town.Culture + (entry == null ? "" : entry.Female ? " women" : " men")); return null; }
            var m = new Made { Reason = reason, Town = town.Id, Culture = town.Culture, Template = choice.Template.Id, TemplateCulture = choice.Template.Culture, Female = choice.Template.Female,
                               Fallback = String.Join(", ", new[] { choice.Fallback, reserveNote }.Where(x => x != null)) };
            if (m.Fallback.Length == 0) m.Fallback = null;
            m.Hero = world.CreateSpecialHero(choice.Template.Id, town.Id, world.CompanionAge()); m.Steps.Add("CreateSpecialHero");
            world.AdjustEquipments(m.Hero); m.Steps.Add("AdjustEquipments");
            world.Activate(m.Hero); m.Steps.Add("ChangeState(Active)");
            world.EnterSettlement(m.Hero, town.Id); m.Steps.Add("EnterSettlementAction");
            string given = entry != null ? entry.Name : world.FirstName(m.Hero);
            m.Epithet = entry != null && !String.IsNullOrWhiteSpace(entry.Epithet) ? entry.Epithet : choice.Template.Epithet;
            m.Name = WandererPool.NameWithEpithet(given, entry == null ? null : entry.Epithet, choice.Template.Epithet);
            m.Reserve = entry == null ? null : entry.Key;
            world.SetName(m.Hero, m.Name, given);
            if (entry != null) { int n; ReserveUses.TryGetValue(entry.Key, out n); ReserveUses[entry.Key] = n + 1; }
            return m;
        }

        // The town culture's least-used reserve name no living hero carries (ties drawn); else another culture's (noted); else none (noted).
        public ReserveName PickReserve(string culture, ISet<string> living, IWorld world, out string note)
        {
            note = null;
            var open = plan.Reserve.Where(r => !living.Contains(r.Name)).ToList();
            var mine = open.Where(r => r.Presentation == culture).ToList();
            if (mine.Count == 0 && open.Count > 0) { mine = open; note = "reserveCrossCulture"; }
            if (mine.Count == 0) { note = "noReserveName"; return null; }
            Func<ReserveName, int> used = r => { int n; return ReserveUses.TryGetValue(r.Key, out n) ? n : 0; };
            int least = mine.Min(used);
            var fresh = mine.Where(r => used(r) == least).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            return fresh[Math.Max(0, Math.Min(fresh.Count - 1, world.RandomInt(fresh.Count)))];
        }
    }

    // The heroes the engine's template spawner would mark Hero.IsMinorFactionHero (findings §16): a generated world's company and outlaw-clan
    // members are XML heroes, so the module marks them (new game: right after registration, before any new-game handler and so before minor
    // clan parties spawn; load: again). find: the hero of an id or null; isSet / set: the flag.
    internal static class MinorFactionHeroes
    {
        public sealed class Result { public int Planned, Found, Set, Already; public List<string> Missing = new List<string>(); }

        public static Result Mark(IEnumerable<string> ids, Func<string, object> find, Func<object, bool> isSet, Action<object> set)
        {
            var r = new Result();
            foreach (var id in ids)
            {
                r.Planned++;
                var hero = find(id);
                if (hero == null) { r.Missing.Add(id); continue; }
                r.Found++;
                if (isSet(hero)) { r.Already++; continue; }
                set(hero); r.Set++;
            }
            return r;
        }
    }

    // RT-07: a kingdom with no settlement is dissolved (FactionDiscontinuationCampaignBehavior, kept): each of its clans leaves it
    // (ChangeKingdomAction.ApplyByLeaveByKingdomDestruction), and a clan still without a kingdom 28 days later is destroyed with every member.
    // A generated house that left by destruction instead joins, at the next daily tick (well inside the 28 days), the nearest kingdom of
    // its culture, else the nearest kingdom, through ChangeKingdomAction.ApplyByJoinToKingdom. Nearest: the least distance from the house's
    // home settlement to a town or castle of the kingdom (periodic on a generated map). The player's clan is never moved, and a kingdom the
    // player rules takes no house (joining it is the player's call in vanilla); eliminated kingdoms and kingdoms without a fief take none.
    internal static class HomelessHouses
    {
        // The installed FactionDiscontinuationCampaignBehavior.SurvivalDurationForIndependentClanInDays (the offline test reads it).
        public const float SurvivalDays = 28f;
        public const float MoveAfterDays = 1f;   // the next daily tick

        public sealed class House { public string Id, Culture; public bool Player, Generated; public double X, Y; }
        public sealed class Realm { public string Id, Culture; public bool Eliminated, PlayerRuled; public List<double[]> Fiefs = new List<double[]>(); }

        public static Realm Destination(House house, IEnumerable<Realm> kingdoms, Func<double, double, double, double, double> distance)
        {
            if (house == null || house.Player || !house.Generated) return null;
            var open = kingdoms.Where(k => !k.Eliminated && !k.PlayerRuled && k.Fiefs.Count > 0).ToList();
            var same = open.Where(k => k.Culture == house.Culture).ToList();
            var pool = same.Count > 0 ? same : open;
            return pool.OrderBy(k => k.Fiefs.Min(f => distance(house.X, house.Y, f[0], f[1]))).ThenBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault();
        }
    }
}
