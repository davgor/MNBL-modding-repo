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
    // MMR-32, MMR-280: generated heroes (docs/heroes.md). Pure managed. From the compiler heroes (campaign.json v2: MMR-27 and the MMR-279
    // population pass), the MMR-31 faction intents (affiliations, clans, ruler relations), the roster policy (worldgen/policies/rosters.json),
    // the population policy (lord traits, the wanderer pool) and the installed templates (CharacterTemplateResolver) it builds:
    //   * every planned house, company and outlaw clan Lord, the houses' minors and their dead parents as XML heroes: NPCCharacters (made
    //     from an installed lord template: body range, skill set, non-personality traits and, for a man, its equipment; a woman takes her
    //     culture's female lord equipment rosters, a minor its child or teenager roster; the personality is drawn from the installed lords'
    //     distribution) and Heroes (clan, alive, spouse, father, mother) for the MMR-30 registry, each with a body seed HeroBodies turns into
    //     an explicit body in game, all equal to the campaign v2 census;
    //   * notables and companions as runtime creation intents (installed template, home settlement, name, age), because the
    //     installed engine creates them through HeroCreator at new game; MMR-34 replaces the vanilla spawns and instantiates them with
    //     HeroCreator.CreateSpecialHero(template, settlement, null, null, age) and SetName, keeping each intent id in its own map
    //     (the engine mints the StringId);
    //   * the resolution of the MMR-32-owned compiler slots (kingdom troop trees and recruit templates, town and castle garrisons,
    //     hero character templates and age/traits/family);
    //   * a disposition for every compiler hero, so nothing is silently dropped.
    // Required roles are filled (every ruler and clan leader an adult lord; every town and village its notable targets), notables are
    // bounded by the policy rather than by population, lords and wanderers are exactly the planned ones, and every synthesized identity is
    // a stable MMR-27-style id.
    // Determinism: records in id order; every synthesized value from a named, versioned stream (Stream). Engine RNG and mutable
    // campaign state stay outside: the runtime may randomize what these intents leave to HeroCreator.
    internal sealed class HeroFactory
    {
        public sealed class HeroIntent
        {
            public string Id, Uid, Name, SourceName, Occupation, Role, Culture, Template, Clan, Kingdom, Settlement, SettlementKind, Spouse, Father, Mother, Provenance, RaceId, Bucket;
            public string BattleEquipment, CivilianEquipment, BodyTemplate;   // equipment roster ids replacing the template's (women, minors), the body range
            public int Age, EngineOccupation, BodySeed;
            public int[] Traits;                                              // the drawn personality vector (population.json lordTraits.traits order)
            public bool Female, Xml, Synthesized, Alive = true;
        }

        public sealed class Disposition { public string Uid, Id, Occupation, Outcome, Reason; }
        public sealed class Slot { public string Owner, Kind, Name; public SortedDictionary<string, object> Value; }
        public sealed class HeroRelation { public string A, B; public int Value; }
        public sealed class WandererChoice { public string Id, Culture, Template, TemplateCulture, Fallback, Epithet, Name; public bool Female; }

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
            public List<WandererChoice> Wanderers = new List<WandererChoice>();   // MMR-287: each companion's pool draw
            public SortedDictionary<string, int> Census = new SortedDictionary<string, int>(StringComparer.Ordinal);   // MMR-280: what is instantiated, equal to campaign v2's census
            public string CensusNote;
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
        private WandererPool wandererPool;
        private IDictionary<string, object> population;
        private List<string> traitNames;
        private List<KeyValuePair<int[], long>> traitJoint;
        private readonly Dictionary<string, HashSet<string>> traitsTaken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Draws a lord's personality may take to find a vector no earlier lord of its culture has (then it keeps its first draw).
        private const int TraitDraws = 2;

        private HeroFactory(IDictionary<string, object> policy, CharacterTemplateResolver resolver)
        {
            this.policy = policy; this.resolver = resolver;
            foreach (var name in new[] { "templates", "ages", "names", "bodies" }) streams[name] = Int(Obj(policy, "streams"), name);
        }

        // population: worldgen/policies/population.json: the backstory-safe template pool every companion is drawn from (MMR-287) and the
        // lord-trait distribution every XML lord's personality is drawn from (lordTraits, RT-05).
        public static Result Build(IDictionary<string, object> campaign, IDictionary<string, object> idMap, FactionFactory.Result factions,
                                   IDictionary<string, object> rosters, CharacterTemplateResolver resolver, IDictionary<string, object> population)
        {
            var factory = new HeroFactory(rosters, resolver);
            factory.wandererPool = WandererPool.FromPolicy(population);
            factory.population = population;
            var traits = Obj(population, "lordTraits");
            factory.traitNames = List(traits, "traits").Select(Str).ToList();
            factory.traitJoint = List(traits, "joint").OfType<IDictionary<string, object>>()
                .Select(j => new KeyValuePair<int[], long>(List(j, "values").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray(), Convert.ToInt64(j["count"], CultureInfo.InvariantCulture))).ToList();
            return factory.Run(campaign, idMap, factions);
        }

        // Settlement cultures: a town presents through its own presentation; a village or castle through the presentation of its source culture
        // uid (as towns and kingdoms carry it), else through its bound town's or kingdom's. Static (from the campaign records alone): the
        // scene's look selection (MMR-258, SettlementLookPlan) reads the same cultures before any factory runs. A settlement without one maps to null.
        public static SortedDictionary<string, string> SettlementCulturesOf(IDictionary<string, object> campaign)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var towns = Records(campaign, "towns").OrderBy(t => Text(t, "id"), StringComparer.Ordinal).ToList();
            var villages = Records(campaign, "villages").OrderBy(v => Text(v, "id"), StringComparer.Ordinal).ToList();
            var castles = Records(campaign, "castles").ToList();
            var byCultureUid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in towns) if (Text(t, "cultureUid") != null) byCultureUid[Text(t, "cultureUid")] = Text(t, "presentation");
            foreach (var k in Records(campaign, "kingdoms")) if (Text(k, "cultureUid") != null && !byCultureUid.ContainsKey(Text(k, "cultureUid"))) byCultureUid[Text(k, "cultureUid")] = Text(k, "presentation");
            var kingdomCulture = Records(campaign, "kingdoms").ToDictionary(k => Text(k, "id"), k => Text(k, "presentation"), StringComparer.Ordinal);
            foreach (var t in towns) result[Text(t, "id")] = Text(t, "presentation");
            foreach (var c in castles)
            {
                string culture;
                result[Text(c, "id")] = byCultureUid.TryGetValue(Text(c, "cultureUid") ?? "", out culture) ? culture : kingdomCulture.ContainsKey(Text(c, "kingdom") ?? "") ? kingdomCulture[Text(c, "kingdom")] : null;
            }
            foreach (var v in villages)
            {
                string culture;
                result[Text(v, "id")] = byCultureUid.TryGetValue(Text(v, "cultureUid") ?? "", out culture) ? culture : result.ContainsKey(Text(v, "bound") ?? "") ? result[Text(v, "bound")] : null;
            }
            return result;
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

            foreach (var kv in SettlementCulturesOf(campaign)) result.SettlementCultures[kv.Key] = kv.Value;
            foreach (var p in result.SettlementCultures.Where(p => p.Value == null)) Fail("settlement " + p.Key + " has no presentation culture");

            // Installed template pools of every culture the world presents through, and the troop graphs of those and the bandit cultures.
            var cultures = new SortedSet<string>(heroes.Select(h => Text(h, "presentation")).Concat(result.SettlementCultures.Values).Concat(factions.AllClans.Select(c => c.Culture)).Where(c => c != null), StringComparer.Ordinal);
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

            // Lords (MMR-280): every compiler Lord with a clan (the MMR-31 affiliations over the campaign.json v2 population blocks): the planned
            // members of the houses, mercenary companies and outlaw clans, the houses' minors and their dead parents, each an XML Lord with its
            // FWGC++ sex and mapped age (campaign v2), so the engine's random child generator counts them. A lord of a dissolved realm is not
            // instantiated.
            var affiliation = factions.Affiliations.ToDictionary(a => a.Hero, StringComparer.Ordinal);
            var kin = Obj(policy, "kin");
            int adultAge = Int(kin, "adultAge"), teenagerAge = Int(kin, "teenagerAge");
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
                if (FactionFactory.AgeOf(h) < 0 || !(h.ContainsKey("female") && h["female"] is bool)) { Fail("lord " + id + " has no planned age and sex (campaign v2)"); continue; }
                var lord = new HeroIntent {
                    Id = id, Uid = Text(h, "uid"), SourceName = Text(h, "name"), Name = FactionFactory.Localized(id, Text(h, "name")), Occupation = "Lord", Role = role, Culture = culture,
                    Clan = a.Clan, Kingdom = a.Kingdom, Xml = true, Provenance = "source (" + Text(h, "nameProvenance") + ")", RaceId = Text(h, "raceId"), Bucket = Text(h, "bucket"),
                    Age = FactionFactory.AgeOf(h), Female = Equals(h["female"], true), Alive = !a.Dead };
                lord.Template = Pick("templates", id, lordTemplates.ContainsKey(culture ?? "") ? lordTemplates[culture] : new List<string>());
                if (lord.Alive && lord.Age < adultAge) DressMinor(lord, teenagerAge); else Dress(lord);
                Body(lord); lord.Traits = DrawTraits(id, culture);
                add(lord);
                result.Dispositions.Add(new Disposition { Uid = lord.Uid, Id = id, Occupation = "Lord", Outcome = "instantiated",
                                                          Reason = (lord.Alive ? "XML lord of " : "XML dead parent (alive false) of ") + a.Clan + " (" + a.Note + ")" });
            }
            // Kin (campaign.json v2, MMR-279): spouse on both heroes, father and mother, every one an XML hero; a link to anything else is refused.
            var lordById = result.Heroes.Where(x => x.Xml).ToDictionary(x => x.Id, StringComparer.Ordinal);
            foreach (var h in heroes.Where(h => lordById.ContainsKey(Text(h, "id"))))
            {
                var lord = lordById[Text(h, "id")];
                foreach (var link in new[] { "spouse", "father", "mother" })
                {
                    string other = Text(h, link);
                    if (other == null) continue;
                    if (!lordById.ContainsKey(other)) { Fail("hero " + lord.Id + " " + link + " " + other + " is not an XML hero"); continue; }
                    if (link == "spouse") lord.Spouse = other; else if (link == "father") lord.Father = other; else lord.Mother = other;
                }
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

            // Companions (MMR-280): the planned wanderers of campaign.json v2 (MMR-279), exactly W = census.wanderers of them (the smallest integer
            // not below the float32 product 0.6f x towns, as CompanionsCampaignBehavior.OnNewGameCreated counts; RT-04); none synthesized. Each keeps
            // its FWGC++ sex and planned age. MMR-287 (RT-32): templates only from the backstory-safe pool, usable in this catalog, of the hero's
            // sex, distinct within a culture while the pool allows, else from the culture's fallback order (recorded); the name keeps an epithet.
            var planned = Records(campaign, "wanderers").Where(w => Text(w, "hero") != null).ToDictionary(w => Text(w, "hero"), StringComparer.Ordinal);
            int wTarget = Int(Obj(campaign, "census"), "wanderers");
            if (planned.Count != wTarget) Fail("campaign v2 plans " + planned.Count + " wanderers, not census.wanderers W = " + wTarget);
            var wanderers = heroes.Where(h => Text(h, "occupation") == "Wanderer").ToList();
            foreach (var h in wanderers.Where(h => !planned.ContainsKey(Text(h, "id")))) Fail("wanderer " + Text(h, "id") + " is not a planned wanderer of campaign v2");
            foreach (var id in planned.Keys.Where(id => !wanderers.Any(h => Text(h, "id") == id))) Fail("planned wanderer " + id + " is not a compiler Wanderer");
            var usable = new HashSet<string>(WandererPool.Cultures.Concat(cultures).Distinct(StringComparer.Ordinal)
                .SelectMany(c => wandererTemplates.ContainsKey(c) ? wandererTemplates[c] : resolver.WandererTemplates(c, problems)), StringComparer.Ordinal);
            var templateUses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var h in wanderers.Where(h => planned.ContainsKey(Text(h, "id"))))
            {
                string id = Text(h, "id"), culture = Text(h, "presentation");
                var homes = towns.Where(t => Text(t, "presentation") == culture).Select(t => Text(t, "id")).ToList();
                if (homes.Count == 0) homes = towns.Select(t => Text(t, "id")).ToList();
                if (FactionFactory.AgeOf(h) < 0 || !(h.ContainsKey("female") && h["female"] is bool)) { Fail("wanderer " + id + " has no planned age and sex (campaign v2)"); continue; }
                bool female = Equals(h["female"], true);
                var choice = wandererPool.Pick(culture, female, templateUses, usable.Contains, n => Draw("templates", id, 0, n));
                if (choice == null)
                {
                    Fail("culture " + culture + " has no usable backstory-safe wanderer template" + (female ? " for a woman" : " for a man") + " in its own or a fallback culture");
                    continue;
                }
                string sourceEpithet = Text(planned[id], "epithet");
                string name = WandererPool.NameWithEpithet(Text(h, "name"), sourceEpithet, choice.Template.Epithet);
                var companion = new HeroIntent {
                    Id = id, Uid = Text(h, "uid"), SourceName = name, Name = FactionFactory.Localized(id, name), Occupation = "Wanderer", Role = Text(h, "role"),
                    Culture = culture, Template = choice.Template.Id, Female = choice.Template.Female, Age = FactionFactory.AgeOf(h),
                    Settlement = homes[Draw("templates", id + "|home", 0, homes.Count)], SettlementKind = "town", RaceId = Text(h, "raceId"), Bucket = "wanderer",
                    Provenance = "source (" + Text(h, "nameProvenance") + "); backstory-safe template " + choice.Template.Id + (choice.Fallback == null ? "" : " (" + choice.Fallback + ")") };
                result.Wanderers.Add(new WandererChoice { Id = id, Culture = culture, Female = choice.Template.Female, Template = choice.Template.Id, TemplateCulture = choice.Template.Culture,
                                                          Fallback = choice.Fallback, Epithet = String.IsNullOrWhiteSpace(sourceEpithet) ? choice.Template.Epithet : sourceEpithet, Name = name });
                add(companion);
                result.Dispositions.Add(new Disposition { Uid = companion.Uid, Id = id, Occupation = "Wanderer", Outcome = "instantiated", Reason = "runtime companion at " + companion.Settlement });
            }
            // Every compiler hero has exactly one disposition.
            var disposed = new HashSet<string>(result.Dispositions.Select(d => d.Uid), StringComparer.Ordinal);
            foreach (var h in heroes) if (!disposed.Contains(Text(h, "uid"))) Fail("compiler hero " + Text(h, "id") + " (" + Text(h, "occupation") + ") has no disposition");
            foreach (var g in result.Dispositions.GroupBy(d => d.Uid, StringComparer.Ordinal).Where(g => g.Count() > 1)) Fail("source hero " + g.Key + " has " + g.Count() + " dispositions");

            Census(result, campaign, factions);
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
            int minimum = Int(Obj(policy, "lords"), "minimumAge"), adultAge = Int(Obj(policy, "kin"), "adultAge");
            foreach (var k in factions.Kingdoms)
            {
                HeroIntent ruler;
                if (!byId.TryGetValue(k.RulerId ?? "", out ruler) || !ruler.Xml || ruler.Occupation != "Lord" || !ruler.Alive) Fail("kingdom " + k.Id + " ruler " + k.RulerId + " is not an instantiated living lord");
                else if (ruler.Age < minimum || ruler.Clan != k.RulingClanId) Fail("kingdom " + k.Id + " ruler " + k.RulerId + " is not an adult of the ruling clan");
            }
            foreach (var c in factions.AllClans)
            {
                HeroIntent leader;
                if (!byId.TryGetValue(c.LeaderId ?? "", out leader) || leader.Clan != c.Id || leader.Age < minimum || !leader.Alive) Fail("clan " + c.Id + " leader " + c.LeaderId + " is not a living adult lord of the clan");
                // The minor clans' troop lines resolve in the catalog: an installed party template and 4 installed is_template characters.
                if (c.Minor)
                {
                    if (!resolver.Exists("PartyTemplate." + c.PartyTemplate)) Fail(c.Kind + " " + c.Id + " party template " + c.PartyTemplate + " is not installed");
                    foreach (var t in c.CharacterTemplates)
                        if (resolver.Element("NPCCharacter." + t) == null || resolver.Element("NPCCharacter." + t).GetAttribute("is_template") != "true") Fail(c.Kind + " " + c.Id + " character template " + t + " is not an installed template");
                }
                // The random child generator (kept on, owner decision 15) clones each house child from a same-sex adult Lord of the house and
                // dresses it from its culture's four child and teenager civilian rosters; a house without an adult of each sex makes it look in
                // another clan of the culture, and it can throw at new game.
                else
                {
                    var adults = r.XmlHeroes.Where(h => h.Clan == c.Id && h.Alive && h.Age >= adultAge).ToList();
                    if (!adults.Any(h => h.Female) || !adults.Any(h => !h.Female)) Fail("house " + c.Id + " has no adult Lord of each sex (the random child generator clones a same-sex adult)");
                }
            }
            foreach (var culture in r.XmlHeroes.Where(h => h.Alive).Select(h => h.Culture).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                foreach (var female in new[] { false, true })
                    foreach (var teenager in new[] { false, true })
                        if (resolver.ChildEquipment(culture, female, teenager, false).Count == 0)
                            Fail("lord culture " + culture + " has no " + (teenager ? "teenager" : "child") + " " + (female ? "female" : "male") + " noble civilian roster (the random child generator dresses its children from it)");
            foreach (var h in r.XmlHeroes)
            {
                HeroIntent other;
                if (h.Spouse != null && (!byId.TryGetValue(h.Spouse, out other) || other.Spouse != h.Id || other.Female == h.Female || other.Clan != h.Clan || !other.Alive || !h.Alive)) Fail("hero " + h.Id + " spouse link is not symmetric between living lords of one clan");
                // A dead parent's planned age is its age at death, so only a living parent must be 18 years older.
                foreach (var parent in new[] { h.Father, h.Mother }.Where(p => p != null))
                    if (!byId.TryGetValue(parent, out other) || other.Clan != h.Clan || (other.Alive && other.Age - h.Age < adultAge)) Fail("hero " + h.Id + " parent " + parent + " is not an adult relative of the clan");
                if (h.Father != null && byId.ContainsKey(h.Father) && byId[h.Father].Female) Fail("hero " + h.Id + " father is female");
                if (h.Mother != null && byId.ContainsKey(h.Mother) && !byId[h.Mother].Female) Fail("hero " + h.Id + " mother is male");
                if (h.Alive && h.Age < adultAge && (h.Father == null || h.Mother == null)) Fail("minor " + h.Id + " (age " + h.Age + ") does not have both parents set");
            }
            var targets = Obj(Obj(policy, "notables"), "targets");
            foreach (var s in towns.Select(t => new { Id = Text(t, "id"), Kind = "town" }).Concat(villages.Select(v => new { Id = Text(v, "id"), Kind = "village" })))
                foreach (var t in Obj(targets, s.Kind))
                    if (r.RuntimeHeroes.Count(h => h.Settlement == s.Id && h.Occupation == t.Key) < Convert.ToInt32(t.Value, CultureInfo.InvariantCulture))
                        Fail(s.Kind + " " + s.Id + " lacks its " + t.Key + " target");
            // Every hero's template with the equipment the hero actually wears, judged for the hero's own sex: a woman's lord rosters, a
            // minor's child or teenager civilian roster, a man's template equipment, a notable's or companion's template (whose sex it takes).
            var judged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var h in r.Heroes)
            {
                if (h.Template == null || resolver.Element("NPCCharacter." + h.Template) == null) { Fail("hero " + h.Id + " template " + h.Template + " does not exist"); continue; }
                if (h.Age < adultAge && (!h.Xml || !h.Alive)) Fail("hero " + h.Id + " is not an adult");
                if (h.Xml && (h.BodyTemplate == null || !resolver.Exists(h.BodyTemplate))) Fail("hero " + h.Id + " body template " + h.BodyTemplate + " does not exist");
                string key = h.Template + "|" + h.Female + "|" + h.BattleEquipment + "|" + h.CivilianEquipment;
                List<string> p;
                if (!judged.TryGetValue(key, out p))
                    judged[key] = p = resolver.CharacterProblems("NPCCharacter." + h.Template, true, h.Female, h.BattleEquipment == null && h.CivilianEquipment == null ? null : new[] { h.BattleEquipment, h.CivilianEquipment });
                foreach (var problem in p) Fail("hero " + h.Id + ": " + problem);
            }
        }

        // The census (MMR-280, replacing the families block): what is instantiated equals the campaign.json v2 census (MMR-279). Houses, the
        // adult house lords, the minors, the dead parents (outside every other count), the company and outlaw clan members, the source notables
        // (none beyond HeroFactory's caps) and the wanderers W.
        private void Census(Result r, IDictionary<string, object> campaign, FactionFactory.Result factions)
        {
            var census = Obj(campaign, "census");
            if (census == null) { Fail("campaign v2 has no census"); return; }
            int adultAge = Int(Obj(policy, "kin"), "adultAge");
            var lords = r.XmlHeroes.ToList();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal) {
                ["houses"] = factions.Clans.Count,
                ["mercenaryCompanies"] = factions.MinorClans.Count(c => c.Kind == "company"),
                ["outlawClans"] = factions.MinorClans.Count(c => c.Kind == "outlaw"),
                ["adultLords"] = lords.Count(h => h.Alive && h.Bucket == "house" && h.Age >= adultAge),
                ["minors"] = lords.Count(h => h.Alive && h.Bucket == "house" && h.Age < adultAge),
                ["deadParents"] = lords.Count(h => !h.Alive),
                ["companyMembers"] = lords.Count(h => h.Alive && h.Bucket == "company"),
                ["outlawMembers"] = lords.Count(h => h.Alive && h.Bucket == "outlaw"),
                ["notables"] = r.Dispositions.Count(d => d.Outcome == "instantiated" && d.Occupation != "Lord" && d.Occupation != "Wanderer"),
                ["wanderers"] = r.RuntimeHeroes.Count(h => h.Occupation == "Wanderer") };
            foreach (var c in counts)
            {
                r.Census[c.Key] = c.Value;
                if (!census.ContainsKey(c.Key) || Int(census, c.Key) != c.Value) Fail("census " + c.Key + ": " + c.Value + " instantiated, campaign v2 census " + (census.ContainsKey(c.Key) ? Int(census, c.Key).ToString(CultureInfo.InvariantCulture) : "absent"));
            }
            // The census applies HeroFactory's per-settlement notable caps (MMR-279 spills a capped-out notable to a house), so none is bounded here.
            int bounded = r.Dispositions.Count(d => d.Outcome == "bounded");
            r.Census["notablesBoundedHere"] = bounded;
            if (bounded != 0) Fail("census: " + bounded + " source notables exceed HeroFactory's caps, which the campaign v2 census applies");
            if (factions.Dissolved.Count > 0) r.CensusNote = factions.Dissolved.Count + " dissolved realms: their lords are not instantiated";
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

        // A minor (a planned child with both parents, MMR-279) is dressed as the engine's child generator dresses its children: the culture's
        // noble child roster below teenagerAge, else its teenager roster, as civilian equipment; a girl wears her culture's female lord battle
        // roster, a boy his template's battle equipment. A culture whose child or teenager rosters all hold an item the minor's sex cannot wear
        // (the installed aserai teenager girls' roster carries a man's tunic) leaves the minor its lord civilian equipment, recorded. The body
        // is the template's range: HeroBodies ages it in game.
        private void DressMinor(HeroIntent h, int teenagerAge)
        {
            Dress(h);
            string stage = h.Age >= teenagerAge ? "teenager" : "child";
            var civilian = resolver.ChildEquipment(h.Culture, h.Female, h.Age >= teenagerAge);
            if (civilian.Count == 0)
            {
                if (resolver.ChildEquipment(h.Culture, h.Female, h.Age >= teenagerAge, false).Count == 0) Fail("culture " + h.Culture + " has no " + stage + " noble roster for minor " + h.Id);
                else h.Provenance += "; its culture's " + stage + " rosters hold items its sex cannot wear, so it keeps its lord civilian equipment";
                return;
            }
            h.CivilianEquipment = civilian[Draw("templates", h.Id + "|civilian", 0, civilian.Count)];
        }

        // RT-05: a lord's personality is never the template's; it is one vector of population.json lordTraits.joint (counted from the installed
        // War Sails lords), drawn weighted by count from the population traits stream seeded by the hero id. Lords are drawn in id order; a
        // draw that repeats a vector an earlier lord of the same culture has is drawn again, up to TraitDraws times, so same-culture lords
        // share a personality no more often than the installed lords do (their bound, lordTraits.sameCultureSharedShare).
        private int[] DrawTraits(string id, string culture)
        {
            long total = traitJoint.Sum(j => j.Value);
            if (total <= 0 || traitNames.Count == 0) { Fail("population.json lordTraits has no joint distribution"); return new int[0]; }
            HashSet<string> taken;
            if (!traitsTaken.TryGetValue(culture ?? "", out taken)) traitsTaken[culture ?? ""] = taken = new HashSet<string>(StringComparer.Ordinal);
            int[] pick = null;
            for (int draw = 0; draw < TraitDraws; draw++)
            {
                long u = (long)(PopulationStream("traits", id, draw) % (ulong)total);
                int[] vector = traitJoint[traitJoint.Count - 1].Key;
                foreach (var j in traitJoint)
                {
                    if (u < j.Value) { vector = j.Key; break; }
                    u -= j.Value;
                }
                if (pick == null) pick = vector;
                if (!taken.Contains(String.Join(",", vector))) { pick = vector; break; }
            }
            taken.Add(String.Join(",", pick));
            return pick;
        }

        private ulong PopulationStream(string stream, string entity, int draw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.population-stream/1\n" + stream + "\n" + Int(Obj(population, "streams"), stream).ToString(CultureInfo.InvariantCulture) + "\n" +
                                                                               prefix + "\n" + entity + "\n" + draw.ToString(CultureInfo.InvariantCulture)));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
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
                        ["age"] = intent.Age, ["female"] = intent.Female, ["alive"] = intent.Alive, ["clan"] = intent.Clan, ["spouse"] = intent.Spouse, ["father"] = intent.Father, ["mother"] = intent.Mother,
                        ["traits"] = intent.Traits == null ? (object)"the template's (runtime hero)" : TraitMap(intent.Traits) } });
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

        // The drawn personality as trait name -> value (zeros included).
        private SortedDictionary<string, object> TraitMap(int[] traits)
        {
            var m = new SortedDictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < traitNames.Count && i < traits.Length; i++) m[traitNames[i]] = traits[i];
            return m;
        }

        // An NPCCharacter made from the hero's installed template: the template's body, skill set, non-personality traits and equipment,
        // with the world's id, name, culture, age, sex and drawn personality (RT-05). Hero.Deserialize reads it by the same id (installed IL).
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
                // Replaced equipment: a woman's battle and civilian lord rosters, a minor's child or teenager civilian roster (a boy keeps his
                // template's battle equipment); a kind not replaced keeps the template's sets.
                if (h.BattleEquipment != null || h.CivilianEquipment != null)
                {
                    var equipments = e.SelectSingleNode("Equipments") as XmlElement;
                    if (equipments == null) { equipments = doc.CreateElement("Equipments"); e.AppendChild(equipments); }
                    bool both = h.BattleEquipment != null && h.CivilianEquipment != null;
                    foreach (var child in equipments.ChildNodes.OfType<XmlElement>().Where(c => c.Name == "EquipmentSet" || c.Name == "EquipmentRoster").ToList())
                    {
                        bool civilianSet = child.GetAttribute("civilian") == "true" || child.GetAttribute("equipmentType") == "Civilian", stealth = child.GetAttribute("equipmentType") == "Stealth";
                        if (both || (civilianSet ? h.CivilianEquipment != null : !stealth && h.BattleEquipment != null)) equipments.RemoveChild(child);
                    }
                    XmlElement battle = null;
                    if (h.BattleEquipment != null) { battle = doc.CreateElement("EquipmentSet"); battle.SetAttribute("id", h.BattleEquipment); equipments.PrependChild(battle); }
                    if (h.CivilianEquipment != null)
                    {
                        var civilian = doc.CreateElement("EquipmentSet"); civilian.SetAttribute("id", h.CivilianEquipment); civilian.SetAttribute("equipmentType", "Civilian");
                        if (battle != null) equipments.InsertAfter(civilian, battle); else equipments.AppendChild(civilian);
                    }
                }
                // Personality (RT-05): the template's personality traits are dropped and the drawn vector's non-zero values written.
                var traits = e.SelectSingleNode("Traits") as XmlElement;
                if (traits == null)
                {
                    traits = doc.CreateElement("Traits");
                    var before = e.SelectSingleNode("Equipments");
                    if (before != null) e.InsertBefore(traits, before); else e.AppendChild(traits);
                }
                foreach (var t in traits.ChildNodes.OfType<XmlElement>().Where(t => t.Name == "Trait" && traitNames.Contains(t.GetAttribute("id"))).ToList()) traits.RemoveChild(t);
                for (int i = 0; h.Traits != null && i < h.Traits.Length; i++)
                    if (h.Traits[i] != 0) { var t = doc.CreateElement("Trait"); t.SetAttribute("id", traitNames[i]); t.SetAttribute("value", h.Traits[i].ToString(CultureInfo.InvariantCulture)); traits.AppendChild(t); }
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
                e.SetAttribute("id", h.Id);
                if (!h.Alive) e.SetAttribute("alive", "false");
                e.SetAttribute("faction", "Faction." + h.Clan);
                if (h.Spouse != null) e.SetAttribute("spouse", "Hero." + h.Spouse);
                if (h.Father != null) e.SetAttribute("father", "Hero." + h.Father);
                if (h.Mother != null) e.SetAttribute("mother", "Hero." + h.Mother);
            }
            return doc;
        }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.hero-intents", ["version"] = 2, ["worldPrefix"] = r.WorldPrefix, ["census"] = r.Census, ["censusNote"] = r.CensusNote,
                ["heroes"] = r.Heroes.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = h.Id, ["uid"] = h.Uid, ["name"] = h.Name, ["sourceName"] = h.SourceName, ["occupation"] = h.Occupation, ["engineOccupation"] = h.EngineOccupation,
                    ["role"] = h.Role, ["culture"] = h.Culture, ["template"] = h.Template, ["clan"] = h.Clan, ["kingdom"] = h.Kingdom, ["settlement"] = h.Settlement,
                    ["settlementKind"] = h.SettlementKind, ["spouse"] = h.Spouse, ["father"] = h.Father, ["mother"] = h.Mother, ["age"] = h.Age, ["female"] = h.Female,
                    ["xml"] = h.Xml, ["synthesized"] = h.Synthesized, ["provenance"] = h.Provenance, ["raceId"] = h.RaceId, ["alive"] = h.Alive, ["bucket"] = h.Bucket,
                    ["traits"] = h.Traits == null ? null : h.Traits.ToList(),
                    ["battleEquipment"] = h.BattleEquipment, ["civilianEquipment"] = h.CivilianEquipment, ["bodyTemplate"] = h.BodyTemplate, ["bodySeed"] = h.BodySeed }).ToList(),
                ["slots"] = r.Slots.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["owner"] = x.Owner, ["kind"] = x.Kind, ["name"] = x.Name, ["value"] = x.Value }).ToList(),
                ["excludedTemplates"] = r.ExcludedTemplates,
                ["wanderers"] = r.Wanderers.OrderBy(w => w.Id, StringComparer.Ordinal).Select(w => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = w.Id, ["culture"] = w.Culture, ["female"] = w.Female, ["template"] = w.Template, ["templateCulture"] = w.TemplateCulture, ["fallback"] = w.Fallback,
                    ["epithet"] = w.Epithet }).ToList(),
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

    // MMR-287 (findings §17 RT-32): the backstory-safe wanderer template pool, the one accessor every generated wanderer's template comes from:
    // HeroFactory at new game, MMR-280's emitter and MMR-281's daily top-up. A wanderer's conversation lines are keyed by its template id, and
    // the installed templates whose lines name a Calradian proper noun (population.json wanderers.backstoryUnsafe, from
    // tools/census_wanderer_backstories.py) never enter it. Pick keeps the wanderer's sex, takes its culture's templates least used so far
    // (distinct within a culture while the pool allows, then repeats, recorded), and only when the culture has none of that sex the first
    // fallback culture that has one (wanderers.fallback, recorded). NameWithEpithet keeps an epithet in the name: the source's own (FWGC++
    // epithets are part of the source name), else the template's ("the Scholar").
    internal sealed class WandererPool
    {
        public const string PolicyPath = "worldgen/policies/population.json";
        public static readonly string[] Cultures = { "aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia" };
        public sealed class Template { public string Id, Culture, Epithet; public bool Female; }
        public sealed class Choice { public Template Template; public string Fallback; }   // Fallback: null, "repeat", "crossCulture:<culture>[, repeat]"

        private readonly List<Template> templates = new List<Template>();
        private readonly HashSet<string> unsafeIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> fallback = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public IEnumerable<Template> All { get { return templates; } }
        public IEnumerable<Template> Safe { get { return templates.Where(t => !unsafeIds.Contains(t.Id)); } }
        public IEnumerable<string> Unsafe { get { return unsafeIds.OrderBy(x => x, StringComparer.Ordinal); } }
        public bool IsSafe(string templateId) { return templates.Any(t => t.Id == templateId) && !unsafeIds.Contains(templateId); }
        public IList<string> Fallback(string culture) { List<string> f; return fallback.TryGetValue(culture ?? "", out f) ? f : new List<string>(); }
        public Template Find(string templateId) { return templates.FirstOrDefault(t => t.Id == templateId); }

        // population: the parsed population.json (wanderers.templates, backstoryUnsafe, fallback).
        public static WandererPool FromPolicy(IDictionary<string, object> population)
        {
            object w;
            var wanderers = population != null && population.TryGetValue("wanderers", out w) ? w as IDictionary<string, object> : null;
            if (wanderers == null) throw new InvalidOperationException(PolicyPath + " has no wanderers block");
            var pool = new WandererPool();
            foreach (var t in Items(wanderers, "templates").OfType<IDictionary<string, object>>())
                pool.templates.Add(new Template { Id = (string)t["id"], Culture = (string)t["culture"], Female = (bool)t["female"], Epithet = (string)t["epithet"] });
            foreach (var id in Items(wanderers, "backstoryUnsafe").Cast<string>())
            {
                if (!pool.templates.Any(t => t.Id == id)) throw new InvalidOperationException(PolicyPath + " wanderers.backstoryUnsafe names unknown template " + id);
                pool.unsafeIds.Add(id);
            }
            object f;
            var order = wanderers.TryGetValue("fallback", out f) ? f as IDictionary<string, object> : null;
            if (order == null) throw new InvalidOperationException(PolicyPath + " has no wanderers.fallback");
            foreach (var c in Cultures) pool.fallback[c] = Items(order, c).Cast<string>().ToList();
            if (pool.templates.Count == 0 || !pool.Safe.Any()) throw new InvalidOperationException(PolicyPath + " leaves no backstory-safe wanderer template");
            return pool;
        }

        // The module's own policy (the runtime top-up, MMR-281).
        public static WandererPool Installed()
        {
            using (var stream = typeof(WandererPool).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld.population.json"))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded population.json.");
                using (var reader = new System.IO.StreamReader(stream, new UTF8Encoding(false)))
                    return FromPolicy((IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(reader.ReadToEnd()));
            }
        }

        // One template for a wanderer of culture and sex (null: either). uses: templates drawn so far (counted here); usable: the templates the
        // catalog resolves (null: all); draw(n): a deterministic index in [0, n). Null when neither the culture nor a fallback culture has one.
        public Choice Pick(string culture, bool? female, IDictionary<string, int> uses, Func<string, bool> usable, Func<int, int> draw)
        {
            foreach (var c in new[] { culture }.Concat(Fallback(culture)))
            {
                var candidates = Safe.Where(t => t.Culture == c && (female == null || t.Female == female.Value) && (usable == null || usable(t.Id)))
                    .OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
                if (candidates.Count == 0) continue;
                int least = candidates.Min(t => Uses(uses, t.Id));
                var fresh = candidates.Where(t => Uses(uses, t.Id) == least).ToList();
                var pick = fresh[Math.Max(0, Math.Min(fresh.Count - 1, draw(fresh.Count)))];
                if (uses != null) uses[pick.Id] = least + 1;
                string note = c == culture ? null : "crossCulture:" + c;
                if (least > 0) note = note == null ? "repeat" : note + ", repeat";
                return new Choice { Template = pick, Fallback = note };
            }
            return null;
        }

        // The wanderer's name with an epithet: the source name when it already carries its epithet; else the source name followed by the
        // source's epithet when one is given apart, or by the template's.
        public static string NameWithEpithet(string name, string sourceEpithet, string templateEpithet)
        {
            name = (name ?? "").Trim();
            string epithet = String.IsNullOrWhiteSpace(sourceEpithet) ? templateEpithet : sourceEpithet.Trim();
            if (String.IsNullOrWhiteSpace(epithet) || HasEpithet(name, epithet)) return name;
            return name.Length == 0 ? epithet : name + " " + epithet;
        }

        // Whether a name keeps the epithet: whole words after the given name.
        public static bool HasEpithet(string name, string epithet)
        {
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(epithet)) return false;
            return (" " + name.Trim() + " ").IndexOf(" " + epithet.Trim() + " ", StringComparison.Ordinal) > 0;
        }

        private static int Uses(IDictionary<string, int> uses, string id) { int n; return uses != null && uses.TryGetValue(id, out n) ? n : 0; }
        private static IEnumerable<object> Items(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
    }
}
