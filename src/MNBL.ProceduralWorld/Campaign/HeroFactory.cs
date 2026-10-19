using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-32: generated heroes (docs/heroes.md). Pure managed. From the MMR-27 compiler heroes, the MMR-31 faction intents
    // (affiliations, clans, ruler relations), the roster policy (worldgen/policies/rosters.json) and the installed templates
    // (CharacterTemplateResolver) it builds:
    //   * lords and their synthesized spouses and heirs as XML heroes: NPCCharacters (made from an installed lord template: body
    //     range, skill set, traits and, for a man, its equipment; a woman takes her culture's female lord equipment rosters) and Heroes
    //     (clan, spouse, father, mother) for the MMR-30 registry, each with a body seed HeroBodies turns into an explicit body in game;
    //   * notables and companions as runtime creation intents (installed template, home settlement, name, age), because the
    //     installed engine creates them through HeroCreator at new game; MMR-34 replaces the vanilla spawns and instantiates them with
    //     HeroCreator.CreateSpecialHero(template, settlement, null, null, age) and SetName, keeping each intent id in its own map
    //     (the engine mints the StringId);
    //   * the resolution of the MMR-32-owned compiler slots (kingdom troop trees and recruit templates, town and castle garrisons,
    //     hero character templates and age/traits/family);
    //   * a disposition for every compiler hero, so nothing is silently dropped.
    // Required roles are filled (every ruler and clan leader an adult lord; every town and village its notable targets), counts are
    // bounded by the policy rather than by population, and every synthesized identity is a stable MMR-27-style id.
    // Determinism: records in id order; every synthesized value from a named, versioned stream (Stream). Engine RNG and mutable
    // campaign state stay outside: the runtime may randomize what these intents leave to HeroCreator.
    internal sealed class HeroFactory
    {
        public sealed class HeroIntent
        {
            public string Id, Uid, Name, SourceName, Occupation, Role, Culture, Template, Clan, Kingdom, Settlement, SettlementKind, Spouse, Father, Mother, Provenance, RaceId;
            public string BattleEquipment, CivilianEquipment, BodyTemplate;   // equipment roster ids replacing the template's (women), the body range
            public int Age, EngineOccupation, BodySeed;
            public bool Female, Xml, Synthesized;
        }

        public sealed class Disposition { public string Uid, Id, Occupation, Outcome, Reason; }
        public sealed class Slot { public string Owner, Kind, Name; public SortedDictionary<string, object> Value; }
        public sealed class HeroRelation { public string A, B; public int Value; }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<HeroIntent> Heroes = new List<HeroIntent>();
            public List<Disposition> Dispositions = new List<Disposition>();
            public List<HeroRelation> Relations = new List<HeroRelation>();
            public List<CharacterTemplateResolver.CultureReport> Troops = new List<CharacterTemplateResolver.CultureReport>();
            public SortedDictionary<string, string> SettlementCultures = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public List<Slot> Slots = new List<Slot>();
            public SortedDictionary<string, string> ExcludedTemplates = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public string IntentsJson, IntentsSha256;
            public IEnumerable<HeroIntent> XmlHeroes { get { return Heroes.Where(h => h.Xml); } }
            public IEnumerable<HeroIntent> RuntimeHeroes { get { return Heroes.Where(h => !h.Xml); } }
        }

        public sealed class HeroException : InvalidOperationException
        {
            public HeroException(IList<string> problems) : base("Hero definitions refused: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; }
            public IList<string> Problems { get; private set; }
        }

        private static readonly string[] RequiredRoots = { "basic_troop", "elite_basic_troop", "melee_militia_troop", "ranged_militia_troop", "default_party_template", "villager_party_template", "militia_party_template" };
        private readonly IDictionary<string, object> policy;
        private readonly CharacterTemplateResolver resolver;
        private readonly Dictionary<string, int> streams = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> problems = new List<string>();
        private string prefix;

        private HeroFactory(IDictionary<string, object> policy, CharacterTemplateResolver resolver)
        {
            this.policy = policy; this.resolver = resolver;
            foreach (var name in new[] { "templates", "ages", "sex", "names", "family", "companions", "bodies" }) streams[name] = Int(Obj(policy, "streams"), name);
        }

        public static Result Build(IDictionary<string, object> campaign, IDictionary<string, object> idMap, FactionFactory.Result factions,
                                   IDictionary<string, object> rosters, CharacterTemplateResolver resolver)
        {
            return new HeroFactory(rosters, resolver).Run(campaign, idMap, factions);
        }

        private Result Run(IDictionary<string, object> campaign, IDictionary<string, object> idMap, FactionFactory.Result factions)
        {
            var result = new Result { WorldPrefix = factions.WorldPrefix };
            prefix = factions.WorldPrefix;
            if (Text(policy, "schema") != "mnbl.rosters" || Int(policy, "version") != 1) Fail("not mnbl.rosters version 1");
            if (Text(campaign, "schema") != "mnbl.campaign-records") Fail("not mnbl.campaign-records");
            var heroes = Records(campaign, "heroes").OrderBy(h => Text(h, "id"), StringComparer.Ordinal).ToList();
            var towns = Records(campaign, "towns").OrderBy(t => Text(t, "id"), StringComparer.Ordinal).ToList();
            var villages = Records(campaign, "villages").OrderBy(v => Text(v, "id"), StringComparer.Ordinal).ToList();
            var castles = Records(campaign, "castles").ToList();
            var assigned = new HashSet<string>(List(idMap, "entries").OfType<IDictionary<string, object>>().Select(e => Text(e, "id")).Where(x => x != null), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var occupations = Obj(Obj(policy, "occupations"), "engine");
            Action<HeroIntent> add = h =>
            {
                if (!used.Add(h.Id)) Fail("hero id " + h.Id + " is generated twice");
                if (h.Synthesized && assigned.Contains(h.Id)) Fail("synthesized hero id " + h.Id + " (" + h.Uid + ") collides with an assigned id");
                string p = CampaignIdentity.Problem(h.Id, prefix);
                if (p != null) Fail("hero id " + h.Id + ": " + p);
                if (!occupations.ContainsKey(h.Occupation)) Fail("hero " + h.Id + " occupation " + h.Occupation + " has no engine occupation");
                h.EngineOccupation = Int(occupations, h.Occupation);
                result.Heroes.Add(h);
            };

            // Settlement cultures: a town presents through its own presentation; a village or castle through the presentation of its
            // source culture uid (as towns and kingdoms carry it), else through its bound town's or kingdom's.
            var byCultureUid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in towns) if (Text(t, "cultureUid") != null) byCultureUid[Text(t, "cultureUid")] = Text(t, "presentation");
            foreach (var k in Records(campaign, "kingdoms")) if (Text(k, "cultureUid") != null && !byCultureUid.ContainsKey(Text(k, "cultureUid"))) byCultureUid[Text(k, "cultureUid")] = Text(k, "presentation");
            var townCulture = towns.ToDictionary(t => Text(t, "id"), t => Text(t, "presentation"), StringComparer.Ordinal);
            var kingdomCulture = Records(campaign, "kingdoms").ToDictionary(k => Text(k, "id"), k => Text(k, "presentation"), StringComparer.Ordinal);
            foreach (var t in towns) result.SettlementCultures[Text(t, "id")] = Text(t, "presentation");
            foreach (var c in castles)
            {
                string culture;
                result.SettlementCultures[Text(c, "id")] = byCultureUid.TryGetValue(Text(c, "cultureUid") ?? "", out culture) ? culture : kingdomCulture.ContainsKey(Text(c, "kingdom") ?? "") ? kingdomCulture[Text(c, "kingdom")] : null;
            }
            foreach (var v in villages)
            {
                string culture;
                result.SettlementCultures[Text(v, "id")] = byCultureUid.TryGetValue(Text(v, "cultureUid") ?? "", out culture) ? culture : result.SettlementCultures.ContainsKey(Text(v, "bound") ?? "") ? result.SettlementCultures[Text(v, "bound")] : null;
            }
            foreach (var p in result.SettlementCultures.Where(p => p.Value == null)) Fail("settlement " + p.Key + " has no presentation culture");

            // Installed template pools of every culture the world presents through, and the troop graphs of those and the bandit cultures.
            var cultures = new SortedSet<string>(heroes.Select(h => Text(h, "presentation")).Concat(result.SettlementCultures.Values).Concat(factions.Clans.Select(c => c.Culture)).Where(c => c != null), StringComparer.Ordinal);
            var lordTemplates = cultures.ToDictionary(c => c, c => resolver.LordTemplates(c, problems), StringComparer.Ordinal);
            var wandererTemplates = cultures.ToDictionary(c => c, c => resolver.WandererTemplates(c, problems), StringComparer.Ordinal);
            foreach (var c in cultures)
            {
                if (lordTemplates[c].Count == 0) Fail("culture " + c + " has no usable lord template");
                if (!resolver.Names(c, false).Any() || !resolver.Names(c, true).Any()) Fail("culture " + c + " has no name lists");
                result.Troops.Add(resolver.Troops(c, RequiredRoots, problems));
            }
            foreach (var c in resolver.BanditCultures()) result.Troops.Add(resolver.Troops(c, new[] { "basic_troop" }, problems));
            var notableTemplates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Func<string, string, List<string>> notablePool = (culture, occupation) =>
            {
                List<string> pool;
                if (!notableTemplates.TryGetValue(culture + "|" + occupation, out pool))
                {
                    notableTemplates[culture + "|" + occupation] = pool = resolver.NotableTemplates(culture, occupation, problems);
                    if (pool.Count == 0) Fail("culture " + culture + " has no usable " + occupation + " notable template");
                }
                return pool;
            };

            // Lords: every compiler lord with a clan (MMR-31 affiliations); a lord of a dissolved realm is not instantiated.
            var affiliation = factions.Affiliations.ToDictionary(a => a.Hero, StringComparer.Ordinal);
            var lordsPolicy = Obj(policy, "lords");
            var notInstantiated = new HashSet<string>(List(campaign, "notInstantiated").Select(Str), StringComparer.Ordinal);
            foreach (var uid in notInstantiated.OrderBy(x => x, StringComparer.Ordinal))
                result.Dispositions.Add(new Disposition { Uid = uid, Outcome = "notInstantiated", Reason = "MMR-27: the compiler does not instantiate this source hero" });
            foreach (var h in heroes.Where(h => Text(h, "occupation") == "Lord"))
            {
                string id = Text(h, "id");
                FactionFactory.Affiliation a;
                if (!affiliation.TryGetValue(id, out a)) { Fail("lord " + id + " has no MMR-31 affiliation"); continue; }
                if (a.Clan == null) { result.Dispositions.Add(new Disposition { Uid = Text(h, "uid"), Id = id, Occupation = "Lord", Outcome = "notInstantiated", Reason = a.Note }); continue; }
                string culture = Text(h, "presentation"), role = Text(h, "role");
                var range = Range(Obj(lordsPolicy, "ages"), role);
                if (range == null) { Fail("lord " + id + " role " + role + " has no age range"); continue; }
                var lord = new HeroIntent {
                    Id = id, Uid = Text(h, "uid"), SourceName = Text(h, "name"), Name = FactionFactory.Localized(id, Text(h, "name")), Occupation = "Lord", Role = role, Culture = culture,
                    Clan = a.Clan, Kingdom = a.Kingdom, Xml = true, Provenance = "source (" + Text(h, "nameProvenance") + ")", RaceId = Text(h, "raceId"),
                    Age = Between("ages", id, range[0], range[1]), Female = Unit("sex", id) < Double(lordsPolicy, "femaleShare") };
                lord.Template = Pick("templates", id, lordTemplates.ContainsKey(culture ?? "") ? lordTemplates[culture] : new List<string>());
                Dress(lord); Body(lord);
                add(lord);
                result.Dispositions.Add(new Disposition { Uid = lord.Uid, Id = id, Occupation = "Lord", Outcome = "instantiated", Reason = "XML lord of " + a.Clan + " (" + a.Note + ")" });
            }

            // Families: a spouse for every clan leader old enough, and up to maxChildren adult heirs per couple, all in the clan.
            var family = Obj(policy, "families");
            var lordById = result.Heroes.ToDictionary(x => x.Id, StringComparer.Ordinal);
            foreach (var clan in factions.Clans.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                HeroIntent leader;
                if (!lordById.TryGetValue(clan.LeaderId ?? "", out leader)) { Fail("clan " + clan.Id + " leader " + clan.LeaderId + " is not an instantiated lord"); continue; }
                if (leader.Age < Int(family, "minimumLeaderAge")) continue;
                var gap = Range(family, "spouseAgeGap");
                string spouseUid = "spouse:" + leader.Uid, spouseId = StableId(spouseUid);
                List<string> pool;
                if (!lordTemplates.TryGetValue(leader.Culture ?? "", out pool)) { Fail("clan " + clan.Id + " leader culture " + leader.Culture + " has no lord templates"); continue; }
                var spouse = Relative(spouseId, spouseUid, leader, !leader.Female, Math.Max(Int(family, "spouseMinimumAge"), leader.Age + Between("family", spouseId, gap[0], gap[1])), pool,
                                      "adapter-synthesized spouse of clan leader " + leader.Id);
                spouse.Spouse = leader.Id; leader.Spouse = spouse.Id;
                add(spouse);
                var father = leader.Female ? spouse : leader; var mother = leader.Female ? leader : spouse;
                int oldestChild = Math.Min(30, Math.Min(father.Age, mother.Age) - 18);
                int children = oldestChild < 18 ? 0 : Draw("family", leader.Id + "|children", 0, Int(family, "maxChildren") + 1);
                for (int i = 1; i <= children; i++)
                {
                    string childUid = "heir:" + leader.Uid + ":" + i.ToString(CultureInfo.InvariantCulture), childId = StableId(childUid);
                    var child = Relative(childId, childUid, leader, Unit("sex", childId) < Double(family, "childFemaleShare"), Between("ages", childId, 18, oldestChild), pool,
                                         "adapter-synthesized heir of " + father.Id + " and " + mother.Id);
                    child.Father = father.Id; child.Mother = mother.Id;
                    add(child);
                }
                if (1 + children > Int(family, "maxPerClan")) Fail("clan " + clan.Id + " has more synthesized relatives than maxPerClan");
            }

            // Notables: source notables fill their settlement up to the caps, in uid order; every target left is synthesized.
            var notables = Obj(policy, "notables");
            var ages = Range(notables, "ages");
            var settlements = towns.Select(t => new { Record = t, Kind = "town" }).Concat(villages.Select(v => new { Record = v, Kind = "village" })).ToList();
            var settlementIds = new HashSet<string>(settlements.Select(s => Text(s.Record, "id")), StringComparer.Ordinal);
            var sourceNotables = heroes.Where(h => Text(h, "settlement") != null && Text(h, "occupation") != "Lord").ToList();
            foreach (var h in sourceNotables.Where(h => !settlementIds.Contains(Text(h, "settlement"))))
                Fail("notable " + Text(h, "id") + " lives in " + Text(h, "settlement") + ", which is not a town or village");
            foreach (var s in settlements)
            {
                string sid = Text(s.Record, "id"), culture = result.SettlementCultures[sid];
                var caps = Obj(Obj(notables, "caps"), s.Kind); var targets = Obj(Obj(notables, "targets"), s.Kind);
                var here = sourceNotables.Where(h => Text(h, "settlement") == sid).OrderBy(h => Text(h, "uid"), StringComparer.Ordinal).ToList();
                var count = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var h in here)
                {
                    string id = Text(h, "id"), occupation = Text(h, "occupation");
                    int n; count.TryGetValue(occupation, out n);
                    if (n >= Int(caps, occupation))
                    {
                        result.Dispositions.Add(new Disposition { Uid = Text(h, "uid"), Id = id, Occupation = occupation, Outcome = "bounded",
                                                                  Reason = "rosters.notables.caps." + s.Kind + "." + occupation + " = " + Int(caps, occupation) + " is already filled in " + sid });
                        continue;
                    }
                    count[occupation] = n + 1;
                    var notable = Notable(id, Text(h, "uid"), occupation, culture, sid, s.Kind, notablePool(culture, occupation), ages, "source (" + Text(h, "nameProvenance") + ")");
                    notable.SourceName = Text(h, "name"); notable.Name = FactionFactory.Localized(id, Text(h, "name")); notable.Role = Text(h, "role"); notable.RaceId = Text(h, "raceId");
                    notable.Kingdom = Text(h, "kingdom");
                    add(notable);
                    result.Dispositions.Add(new Disposition { Uid = notable.Uid, Id = id, Occupation = occupation, Outcome = "instantiated", Reason = "runtime notable of " + sid });
                }
                foreach (var target in targets.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    int have; count.TryGetValue(target, out have);
                    for (int i = have + 1; i <= Int(targets, target); i++)
                    {
                        string uid = "notable:" + Text(s.Record, "uid") + ":" + target + ":" + i.ToString(CultureInfo.InvariantCulture), id = StableId(uid);
                        var notable = Notable(id, uid, target, culture, sid, s.Kind, notablePool(culture, target), ages, "adapter-synthesized: " + s.Kind + " target " + target + " " + Int(targets, target) + " not met by source notables");
                        notable.Synthesized = true;
                        var names = resolver.Names(culture, notable.Female);
                        string first = Plain(names.Count > 0 ? names[Draw("names", id, 0, names.Count)] : target);
                        notable.SourceName = FactionFactory.Fill(Text(notables, "synthesizedName"), new Dictionary<string, string> { ["name"] = first, ["settlement"] = Text(s.Record, "name") });
                        notable.Name = FactionFactory.Localized(id, notable.SourceName);
                        add(notable);
                    }
                }
            }

            // Companions: at most floor(factor * towns) source wanderers, by companions-stream rank; none synthesized.
            var companions = Obj(policy, "companions");
            // CompanionsCampaignBehavior.OnNewGameCreated: for (i = 0; (float)i < 0.6f * Town.AllTowns.Count; i++) (installed IL).
            float desired = (float)Double(companions, "factor") * towns.Count;
            int bound = 0;
            while (bound < desired) bound++;
            var cAges = Range(companions, "ages");
            var wanderers = heroes.Where(h => Text(h, "occupation") == "Wanderer").OrderBy(h => Stream("companions", Text(h, "uid"), 0)).ThenBy(h => Text(h, "uid"), StringComparer.Ordinal).ToList();
            for (int i = 0; i < wanderers.Count; i++)
            {
                var h = wanderers[i];
                string id = Text(h, "id"), culture = Text(h, "presentation");
                if (i >= bound)
                {
                    result.Dispositions.Add(new Disposition { Uid = Text(h, "uid"), Id = id, Occupation = "Wanderer", Outcome = "bounded",
                                                              Reason = "rosters.companions: the installed loop over " + Double(companions, "factor").ToString(CultureInfo.InvariantCulture) + "f * " + towns.Count + " towns makes " + bound + " companions" });
                    continue;
                }
                var homes = towns.Where(t => Text(t, "presentation") == culture).Select(t => Text(t, "id")).ToList();
                if (homes.Count == 0) homes = towns.Select(t => Text(t, "id")).ToList();
                var pool = wandererTemplates.ContainsKey(culture ?? "") ? wandererTemplates[culture] : new List<string>();
                if (pool.Count == 0) { Fail("culture " + culture + " has no usable wanderer template"); continue; }
                string template = Pick("templates", id, pool);
                var companion = new HeroIntent {
                    Id = id, Uid = Text(h, "uid"), SourceName = Text(h, "name"), Name = FactionFactory.Localized(id, Text(h, "name")), Occupation = "Wanderer", Role = Text(h, "role"),
                    Culture = culture, Template = template, Female = resolver.IsFemale(template), Age = Between("ages", id, cAges[0], cAges[1]),
                    Settlement = homes[Draw("templates", id + "|home", 0, homes.Count)], SettlementKind = "town", Provenance = "source (" + Text(h, "nameProvenance") + ")", RaceId = Text(h, "raceId") };
                add(companion);
                result.Dispositions.Add(new Disposition { Uid = companion.Uid, Id = id, Occupation = "Wanderer", Outcome = "instantiated", Reason = "runtime companion at " + companion.Settlement });
            }
            // Every compiler hero has exactly one disposition.
            var disposed = new HashSet<string>(result.Dispositions.Select(d => d.Uid), StringComparer.Ordinal);
            foreach (var h in heroes) if (!disposed.Contains(Text(h, "uid"))) Fail("compiler hero " + Text(h, "id") + " (" + Text(h, "occupation") + ") has no disposition");
            foreach (var g in result.Dispositions.GroupBy(d => d.Uid, StringComparer.Ordinal).Where(g => g.Count() > 1)) Fail("source hero " + g.Key + " has " + g.Count() + " dispositions");

            Slots(result, campaign, factions, heroes, towns, castles);
            foreach (var x in resolver.Excluded) result.ExcludedTemplates[x.Key] = x.Value;
            Validate(result, factions, towns, villages);
            foreach (var r in factions.Relations)
            {
                if (!lordById.ContainsKey(r.RulerA) || !lordById.ContainsKey(r.RulerB)) { Fail("ruler relation " + r.A + "|" + r.B + " names a ruler that is not instantiated"); continue; }
                result.Relations.Add(new HeroRelation { A = r.RulerA, B = r.RulerB, Value = r.RulerRelation });
            }
            if (problems.Count > 0) throw new HeroException(problems.Distinct().ToList());
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("NPCCharacters", CharactersXml(result)));
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Heroes", HeroesXml(result)));
            result.DeclaredKeys.AddRange(result.XmlHeroes.Select(h => "NPCCharacter." + h.Id).Concat(result.XmlHeroes.Select(h => "Hero." + h.Id)));
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        // Required roles and family closure; problems, never repairs.
        private void Validate(Result r, FactionFactory.Result factions, List<IDictionary<string, object>> towns, List<IDictionary<string, object>> villages)
        {
            var byId = r.Heroes.ToDictionary(h => h.Id, StringComparer.Ordinal);
            int minimum = Int(Obj(policy, "lords"), "minimumAge");
            foreach (var k in factions.Kingdoms)
            {
                HeroIntent ruler;
                if (!byId.TryGetValue(k.RulerId ?? "", out ruler) || !ruler.Xml || ruler.Occupation != "Lord") Fail("kingdom " + k.Id + " ruler " + k.RulerId + " is not an instantiated lord");
                else if (ruler.Age < minimum || ruler.Clan != k.RulingClanId) Fail("kingdom " + k.Id + " ruler " + k.RulerId + " is not an adult of the ruling clan");
            }
            foreach (var c in factions.Clans)
            {
                HeroIntent leader;
                if (!byId.TryGetValue(c.LeaderId ?? "", out leader) || leader.Clan != c.Id || leader.Age < minimum) Fail("clan " + c.Id + " leader " + c.LeaderId + " is not an adult lord of the clan");
            }
            foreach (var h in r.XmlHeroes)
            {
                HeroIntent other;
                if (h.Spouse != null && (!byId.TryGetValue(h.Spouse, out other) || other.Spouse != h.Id || other.Female == h.Female || other.Clan != h.Clan)) Fail("hero " + h.Id + " spouse link is not symmetric");
                foreach (var parent in new[] { h.Father, h.Mother }.Where(p => p != null))
                    if (!byId.TryGetValue(parent, out other) || other.Clan != h.Clan || other.Age - h.Age < 18) Fail("hero " + h.Id + " parent " + parent + " is not an adult relative of the clan");
                if (h.Father != null && byId.ContainsKey(h.Father) && byId[h.Father].Female) Fail("hero " + h.Id + " father is female");
                if (h.Mother != null && byId.ContainsKey(h.Mother) && !byId[h.Mother].Female) Fail("hero " + h.Id + " mother is male");
            }
            var targets = Obj(Obj(policy, "notables"), "targets");
            foreach (var s in towns.Select(t => new { Id = Text(t, "id"), Kind = "town" }).Concat(villages.Select(v => new { Id = Text(v, "id"), Kind = "village" })))
                foreach (var t in Obj(targets, s.Kind))
                    if (r.RuntimeHeroes.Count(h => h.Settlement == s.Id && h.Occupation == t.Key) < Convert.ToInt32(t.Value, CultureInfo.InvariantCulture))
                        Fail(s.Kind + " " + s.Id + " lacks its " + t.Key + " target");
            // Every hero's template with the equipment the hero actually wears, judged for the hero's own sex: a woman's lord rosters, a
            // man's template equipment, a notable's or companion's template (whose sex it takes).
            var judged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var h in r.Heroes)
            {
                if (h.Template == null || resolver.Element("NPCCharacter." + h.Template) == null) { Fail("hero " + h.Id + " template " + h.Template + " does not exist"); continue; }
                if (h.Age < 18) Fail("hero " + h.Id + " is not an adult");
                if (h.Xml && (h.BodyTemplate == null || !resolver.Exists(h.BodyTemplate))) Fail("hero " + h.Id + " body template " + h.BodyTemplate + " does not exist");
                string key = h.Template + "|" + h.Female + "|" + h.BattleEquipment + "|" + h.CivilianEquipment;
                List<string> p;
                if (!judged.TryGetValue(key, out p))
                    judged[key] = p = resolver.CharacterProblems("NPCCharacter." + h.Template, true, h.Female, h.BattleEquipment == null ? null : new[] { h.BattleEquipment, h.CivilianEquipment });
                foreach (var problem in p) Fail("hero " + h.Id + ": " + problem);
            }
        }

        private HeroIntent Relative(string id, string uid, HeroIntent leader, bool female, int age, List<string> templates, string provenance)
        {
            var names = resolver.Names(leader.Culture, female);
            var name = names.Count > 0 ? names[Draw("names", id, 0, names.Count)] : null;
            var relative = new HeroIntent {
                Id = id, Uid = uid, SourceName = Plain(name), Name = FactionFactory.Localized(id, Plain(name)), Occupation = "Lord", Role = "relative", Culture = leader.Culture,
                Clan = leader.Clan, Kingdom = leader.Kingdom, Female = female, Age = Math.Min(70, age), Template = Pick("templates", id, templates), Xml = true, Synthesized = true,
                Provenance = provenance, RaceId = leader.RaceId };
            Dress(relative); Body(relative);
            return relative;
        }

        // Lord templates carry men's equipment (no installed lord template is female), so a woman wears her culture's installed female lord
        // equipment rosters (IsLordTemplate, IsFemaleTemplate), one battle and one civilian roster from the templates stream.
        private void Dress(HeroIntent h)
        {
            if (!h.Female) return;
            var battle = resolver.LordEquipment(h.Culture, true, false); var civilian = resolver.LordEquipment(h.Culture, true, true);
            if (battle.Count == 0 || civilian.Count == 0) { Fail("culture " + h.Culture + " has no usable female lord equipment roster"); return; }
            h.BattleEquipment = battle[Draw("templates", h.Id + "|battle", 0, battle.Count)];
            h.CivilianEquipment = civilian[Draw("templates", h.Id + "|civilian", 0, civilian.Count)];
        }

        // An XML hero keeps its template's body range (face key template); HeroBodies draws the explicit body from it in game with this
        // seed (the engine's face generator is native). Runtime heroes take their body from HeroCreator.
        private void Body(HeroIntent h)
        {
            h.BodyTemplate = h.Template == null ? null : resolver.FaceKeyTemplate(h.Template);
            h.BodySeed = (int)(Stream("bodies", h.Id, 0) & 0x7FFFFFFF);
        }

        // The MMR-32-owned compiler slots: every kingdom's troop tree and recruit templates (its culture's installed roots), every town's and
        // castle's garrison template (GarrisonTroopsCampaignBehavior fills it from Town.Culture.DefaultPartyTemplate), and every compiler
        // hero's character template and age/traits/family, or its disposition when it is not instantiated.
        private void Slots(Result r, IDictionary<string, object> campaign, FactionFactory.Result factions, List<IDictionary<string, object>> heroes,
                           List<IDictionary<string, object>> towns, List<IDictionary<string, object>> castles)
        {
            Func<string, string, string> member = (culture, name) => { var c = resolver.Element("Culture." + culture); return c == null ? null : c.GetAttribute(name); };
            Func<string, SortedDictionary<string, object>> troops = culture => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["culture"] = culture, ["basicTroop"] = member(culture, "basic_troop"), ["eliteBasicTroop"] = member(culture, "elite_basic_troop"),
                ["meleeMilitia"] = member(culture, "melee_militia_troop"), ["rangedMilitia"] = member(culture, "ranged_militia_troop"),
                ["partyTemplate"] = member(culture, "default_party_template"), ["recruitment"] = "volunteers from the culture's notables (basic and elite trees)" };
            foreach (var k in Records(campaign, "kingdoms").OrderBy(k => Text(k, "id"), StringComparer.Ordinal))
            {
                var kingdom = factions.Kingdoms.FirstOrDefault(x => x.Id == Text(k, "id"));
                var value = kingdom != null ? troops(kingdom.Culture) : new SortedDictionary<string, object>(StringComparer.Ordinal) { ["disposition"] = "dissolved (MMR-31)" };
                r.Slots.Add(new Slot { Owner = Text(k, "id"), Kind = "kingdom", Name = "troopTreeAndRecruitTemplates", Value = value });
            }
            foreach (var s in towns.Select(t => new { Record = t, Kind = "town" }).Concat(castles.OrderBy(c => Text(c, "id"), StringComparer.Ordinal).Select(c => new { Record = c, Kind = "castle" })))
            {
                string culture = r.SettlementCultures[Text(s.Record, "id")];
                r.Slots.Add(new Slot { Owner = Text(s.Record, "id"), Kind = s.Kind, Name = "garrison", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["culture"] = culture, ["partyTemplate"] = member(culture, "default_party_template"), ["filledBy"] = "GarrisonTroopsCampaignBehavior.FillGarrisonPartyOnNewGame" } });
            }
            var byId = r.Heroes.ToDictionary(h => h.Id, StringComparer.Ordinal);
            var disposition = r.Dispositions.Where(d => d.Id != null).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var h in heroes)
            {
                string id = Text(h, "id");
                HeroIntent intent;
                if (byId.TryGetValue(id, out intent))
                {
                    r.Slots.Add(new Slot { Owner = id, Kind = "hero", Name = "characterTemplate", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        ["template"] = intent.Template, ["battleEquipment"] = intent.BattleEquipment, ["civilianEquipment"] = intent.CivilianEquipment, ["bodyTemplate"] = intent.BodyTemplate,
                        ["bodySeed"] = intent.Xml ? (object)intent.BodySeed : null, ["instantiation"] = intent.Xml ? "XML hero (NPCCharacters + Heroes)" : "runtime intent (HeroCreator.CreateSpecialHero)" } });
                    r.Slots.Add(new Slot { Owner = id, Kind = "hero", Name = "ageTraitsAndFamily", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        ["age"] = intent.Age, ["female"] = intent.Female, ["traits"] = "the template's", ["clan"] = intent.Clan, ["spouse"] = intent.Spouse,
                        ["father"] = intent.Father, ["mother"] = intent.Mother } });
                }
                else
                {
                    Disposition d;
                    var value = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["disposition"] = disposition.TryGetValue(id, out d) ? d.Outcome + ": " + d.Reason : null };
                    if (value["disposition"] == null) Fail("compiler hero " + id + " has neither an intent nor a disposition for its slots");
                    r.Slots.Add(new Slot { Owner = id, Kind = "hero", Name = "characterTemplate", Value = value });
                    r.Slots.Add(new Slot { Owner = id, Kind = "hero", Name = "ageTraitsAndFamily", Value = value });
                }
            }
        }

        private HeroIntent Notable(string id, string uid, string occupation, string culture, string settlement, string kind, List<string> pool, int[] ages, string provenance)
        {
            string template = Pick("templates", id, pool);
            return new HeroIntent {
                Id = id, Uid = uid, Occupation = occupation, Culture = culture, Template = template, Female = template != null && resolver.IsFemale(template),
                Age = Between("ages", id, ages[0], ages[1]), Settlement = settlement, SettlementKind = kind, Provenance = provenance };
        }

        // An NPCCharacter made from the hero's installed template: the template's body, skill set, traits and equipment, with the
        // world's id, name, culture, age and sex. Hero.Deserialize reads it by the same id (installed IL).
        private XmlDocument CharactersXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("NPCCharacters"); doc.AppendChild(root);
            foreach (var h in r.XmlHeroes.OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                var e = (XmlElement)doc.ImportNode(resolver.Element("NPCCharacter." + h.Template), true);
                foreach (var attribute in new[] { "is_template", "faction", "clan", "father", "mother", "spouse", "is_female", "is_hidden_encyclopedia" }) e.RemoveAttribute(attribute);
                e.SetAttribute("id", h.Id); e.SetAttribute("name", h.Name); e.SetAttribute("is_hero", "true"); e.SetAttribute("occupation", h.Occupation);
                e.SetAttribute("culture", "Culture." + h.Culture); e.SetAttribute("age", h.Age.ToString(CultureInfo.InvariantCulture));
                if (h.Female) e.SetAttribute("is_female", "true");
                if (h.BattleEquipment != null)
                {
                    var equipments = e.SelectSingleNode("Equipments") as XmlElement;
                    if (equipments == null) { equipments = doc.CreateElement("Equipments"); e.AppendChild(equipments); }
                    foreach (var child in equipments.ChildNodes.OfType<XmlElement>().Where(c => c.Name == "EquipmentSet" || c.Name == "EquipmentRoster").ToList()) equipments.RemoveChild(child);
                    var battle = doc.CreateElement("EquipmentSet"); battle.SetAttribute("id", h.BattleEquipment); equipments.PrependChild(battle);
                    var civilian = doc.CreateElement("EquipmentSet"); civilian.SetAttribute("id", h.CivilianEquipment); civilian.SetAttribute("equipmentType", "Civilian");
                    equipments.InsertAfter(civilian, battle);
                }
                root.AppendChild(e);
            }
            return doc;
        }

        private XmlDocument HeroesXml(Result r)
        {
            var doc = new XmlDocument(); var root = doc.CreateElement("Heroes"); doc.AppendChild(root);
            foreach (var h in r.XmlHeroes.OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                var e = doc.CreateElement("Hero"); root.AppendChild(e);
                e.SetAttribute("id", h.Id); e.SetAttribute("faction", "Faction." + h.Clan);
                if (h.Spouse != null) e.SetAttribute("spouse", "Hero." + h.Spouse);
                if (h.Father != null) e.SetAttribute("father", "Hero." + h.Father);
                if (h.Mother != null) e.SetAttribute("mother", "Hero." + h.Mother);
            }
            return doc;
        }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.hero-intents", ["version"] = 1, ["worldPrefix"] = r.WorldPrefix,
                ["heroes"] = r.Heroes.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = h.Id, ["uid"] = h.Uid, ["name"] = h.Name, ["sourceName"] = h.SourceName, ["occupation"] = h.Occupation, ["engineOccupation"] = h.EngineOccupation,
                    ["role"] = h.Role, ["culture"] = h.Culture, ["template"] = h.Template, ["clan"] = h.Clan, ["kingdom"] = h.Kingdom, ["settlement"] = h.Settlement,
                    ["settlementKind"] = h.SettlementKind, ["spouse"] = h.Spouse, ["father"] = h.Father, ["mother"] = h.Mother, ["age"] = h.Age, ["female"] = h.Female,
                    ["xml"] = h.Xml, ["synthesized"] = h.Synthesized, ["provenance"] = h.Provenance, ["raceId"] = h.RaceId,
                    ["battleEquipment"] = h.BattleEquipment, ["civilianEquipment"] = h.CivilianEquipment, ["bodyTemplate"] = h.BodyTemplate, ["bodySeed"] = h.BodySeed }).ToList(),
                ["slots"] = r.Slots.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["owner"] = x.Owner, ["kind"] = x.Kind, ["name"] = x.Name, ["value"] = x.Value }).ToList(),
                ["excludedTemplates"] = r.ExcludedTemplates,
                ["dispositions"] = r.Dispositions.OrderBy(d => d.Uid, StringComparer.Ordinal).Select(d => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["uid"] = d.Uid, ["id"] = d.Id, ["occupation"] = d.Occupation, ["outcome"] = d.Outcome, ["reason"] = d.Reason }).ToList(),
                ["relations"] = r.Relations.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["a"] = x.A, ["b"] = x.B, ["value"] = x.Value }).ToList(),
                ["settlementCultures"] = r.SettlementCultures,
                ["troops"] = r.Troops.Select(t => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["culture"] = t.Culture, ["bandit"] = t.Bandit, ["roots"] = t.Roots, ["troops"] = t.Troops.Count, ["partyTemplates"] = t.PartyTemplates, ["ships"] = t.Ships,
                    ["maxUpgradeDepth"] = t.MaxUpgradeDepth }).ToList()
            };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        // "{=id}Name" -> "Name": a culture's installed name string reused inside a new localized string.
        private static string Plain(string localized)
        {
            if (localized == null) return null;
            int close = localized.StartsWith("{=", StringComparison.Ordinal) ? localized.IndexOf('}') : -1;
            return close > 0 ? localized.Substring(close + 1) : localized;
        }

        private string StableId(string uid) { return prefix + "h_" + Sha256("mnbl.stable-id/1\nhero\n" + uid).Substring(0, 10); }

        private ulong Stream(string stream, string entity, int draw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.hero-stream/1\n" + stream + "\n" + streams[stream].ToString(CultureInfo.InvariantCulture) + "\n" +
                                                                               prefix + "\n" + entity + "\n" + draw.ToString(CultureInfo.InvariantCulture)));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        private int Draw(string stream, string entity, int draw, int count)
        {
            if (count <= 0) { Fail("empty " + stream + " pool for " + entity); return 0; }
            return (int)(Stream(stream, entity, draw) % (ulong)count);
        }

        private double Unit(string stream, string entity) { return (Stream(stream, entity, 0) >> 11) / (double)(1UL << 53); }
        private int Between(string stream, string entity, int low, int high) { return high < low ? low : low + Draw(stream, entity, 0, high - low + 1); }
        private string Pick(string stream, string entity, List<string> pool) { return pool.Count == 0 ? null : pool[Draw(stream, entity, 0, pool.Count)]; }
        private void Fail(string problem) { problems.Add(problem); }

        private static int[] Range(IDictionary<string, object> o, string key)
        {
            var values = List(o, key).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
            return values.Length == 2 ? values : null;
        }

        private static string Sha256(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static double Double(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0; }
        private static string Str(object v) { return Convert.ToString(v, CultureInfo.InvariantCulture); }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return List(o, key).OfType<IDictionary<string, object>>(); }
    }
}
