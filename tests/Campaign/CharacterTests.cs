using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-32 hero and roster tests, without the game: CharacterTests BUNDLE_DIR REPO_DIR OUT_JSON
// The compiler records come from the real MMR-29 bundle (read through WorldDataBundle); the faction and roster policies, culture
// presentations and templates from the repository; the installed templates from the merged campaign catalog its receipt names
// (each document hash-checked); engine-default references from the live run the roster policy pins.
internal static class CharacterTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllText(path)); }
    private static IDictionary<string, object> Copy(IDictionary<string, object> o) { return Parse(Json.Serialize(o)); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList() : new List<string>(); }

    private static string Refusal(Func<object> build)
    {
        try { build(); return null; }
        catch (HeroFactory.HeroException e) { return e.Message; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: CharacterTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
        try { Run(args[0], args[1], args[2]); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string bundleDir, string repo, string outJson)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var campaign = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
        var idMap = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/id-map.json")));
        var factionsPolicy = Load(Path.Combine(repo, "worldgen/policies/factions.json"));
        var presentations = Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json"));
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var provenance = O(rosters, "provenance");
        var receipt = Load(Path.Combine(repo, S(provenance, "catalogReceipt")));
        var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in Records(O(receipt, "outputs"), "documents").Where(d => CharacterTemplateResolver.Documents.Contains(S(d, "id"))))
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256") || Sha(bytes) != S(O(provenance, "catalogDocuments"), S(d, "id"))) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its receipt or the roster policy");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        var defaultsPath = Path.Combine(repo, S(provenance, "defaultReferences"));
        if (Sha(File.ReadAllBytes(defaultsPath)) != S(provenance, "defaultReferencesSha256")) throw new InvalidOperationException("default references differ from the roster policy");
        var defaults = L(O(Records(O(Load(defaultsPath), "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var factions = FactionFactory.Build(campaign, idMap, factionsPolicy, presentations, templates);
        Func<IDictionary<string, XmlDocument>, IDictionary<string, object>, CharacterTemplateResolver> resolverOf = (c, p) => new CharacterTemplateResolver(c, defaults, O(p, "troops"));
        var resolver = resolverOf(catalog, rosters);
        Func<IDictionary<string, object>, IDictionary<string, object>, HeroFactory.Result> build = (c, p) => HeroFactory.Build(c, idMap, FactionFactory.Build(c, idMap, factionsPolicy, presentations, templates), p, resolverOf(catalog, p));
        var r = HeroFactory.Build(campaign, idMap, factions, rosters, resolver);

        var compiler = Records(campaign, "heroes").ToDictionary(h => S(h, "id"));
        var towns = Records(campaign, "towns").ToList(); var villages = Records(campaign, "villages").ToList();
        var byId = r.Heroes.ToDictionary(h => h.Id);
        var xml = r.XmlHeroes.ToList(); var runtime = r.RuntimeHeroes.ToList();
        var lords = xml.Where(h => !h.Synthesized).ToList(); var relatives = xml.Where(h => h.Synthesized).ToList();
        var notables = runtime.Where(h => h.Occupation != "Wanderer").ToList(); var companions = runtime.Where(h => h.Occupation == "Wanderer").ToList();
        Func<Dictionary<string, XmlDocument>, string, XmlElement> element = (c, xpath) => (XmlElement)c.Values.Select(d => d.DocumentElement.SelectSingleNode(xpath)).First(n => n != null);
        Func<Action<Dictionary<string, XmlDocument>>, Dictionary<string, XmlDocument>> mutated = mutate =>
        {
            var copy = catalog.ToDictionary(p => p.Key, p => { var d = new XmlDocument { XmlResolver = null }; d.LoadXml(p.Value.OuterXml); return d; }, StringComparer.Ordinal);
            mutate(copy);
            return copy;
        };
        Func<Dictionary<string, XmlDocument>, string> inject = c => Refusal(() => HeroFactory.Build(campaign, idMap, factions, rosters, resolverOf(c, rosters)));

        // c1: required roles resolve.
        Check(factions.Kingdoms.All(k => byId.ContainsKey(k.RulerId) && byId[k.RulerId].Xml && byId[k.RulerId].Clan == k.RulingClanId && byId[k.RulerId].Age >= 20) &&
              factions.Clans.All(c => byId.ContainsKey(c.LeaderId) && byId[c.LeaderId].Clan == c.Id && byId[c.LeaderId].Age >= 20) && lords.Count == 92,
              "every one of " + factions.Kingdoms.Count + " rulers and " + factions.Clans.Count + " clan leaders is an adult XML lord of its clan (" + lords.Count + " source lords)");
        var targets = O(O(rosters, "notables"), "targets");
        Func<string, string, int> count = (settlement, occupation) => notables.Count(h => h.Settlement == settlement && h.Occupation == occupation);
        Check(towns.All(t => O(targets, "town").All(x => count(S(t, "id"), x.Key) >= Convert.ToInt32(x.Value))) &&
              villages.All(v => O(targets, "village").All(x => count(S(v, "id"), x.Key) >= Convert.ToInt32(x.Value))),
              "every one of " + towns.Count + " towns has 2 merchants, 2 gang leaders and an artisan, and every one of " + villages.Count + " villages a headman and 2 rural notables (the installed notable targets)");
        // The pool is the culture's installed list (lord_templates, notable_templates, Wanderer templates): sturgia's lord_templates include
        // four NavalDLC templates of culture nord, and the hero still takes its own culture.
        var scratch = new List<string>();
        Func<HeroFactory.HeroIntent, List<string>> pool = h => h.Occupation == "Lord" ? resolver.LordTemplates(h.Culture, scratch) : h.Occupation == "Wanderer" ? resolver.WandererTemplates(h.Culture, scratch) : resolver.NotableTemplates(h.Culture, h.Occupation, scratch);
        Check(r.Heroes.All(h => pool(h).Contains(h.Template) && resolver.Element("NPCCharacter." + h.Template).GetAttribute("is_template") == "true" &&
              resolver.Element("NPCCharacter." + h.Template).GetAttribute("occupation") == h.Occupation && !r.ExcludedTemplates.ContainsKey("NPCCharacter." + h.Template)) && scratch.Count == 0 &&
              r.Heroes.All(h => resolver.CharacterProblems("NPCCharacter." + h.Template, true, h.Female, h.BattleEquipment == null ? null : new[] { h.BattleEquipment, h.CivilianEquipment }).Count == 0),
              "every hero's template is an installed is_template of its occupation from its culture's own list, and its body, skill set and the equipment the hero wears resolve for the hero's sex (" +
              r.Heroes.Select(h => h.Template).Distinct().Count() + " templates; " + r.ExcludedTemplates.Count + " installed templates or rosters excluded as broken)");
        // Equipment worn, re-derived from the emitted XML and the catalog: every item exists, fits its slot and suits the wearer's sex.
        var characters = r.Documents.Single(d => d.Key == "NPCCharacters").Value; var heroesXml = r.Documents.Single(d => d.Key == "Heroes").Value;
        var items = catalog["Items"].DocumentElement.ChildNodes.OfType<XmlElement>().ToDictionary(e => e.GetAttribute("id"));
        var rosterById = catalog["EquipmentRosters"].DocumentElement.ChildNodes.OfType<XmlElement>().ToDictionary(e => e.GetAttribute("id"));
        Func<XmlElement, string> kind = s => s.GetAttribute("equipmentType") == "Civilian" || s.GetAttribute("civilian") == "true" ? "civilian" : s.GetAttribute("equipmentType") == "Stealth" ? "stealth" : "battle";
        var wornProblems = new List<string>();
        foreach (var e in characters.DocumentElement.ChildNodes.OfType<XmlElement>())
        {
            bool female = e.GetAttribute("is_female") == "true";
            foreach (var reference in e.SelectNodes("Equipments/EquipmentSet").OfType<XmlElement>())
                foreach (var set in rosterById[reference.GetAttribute("id")].ChildNodes.OfType<XmlElement>().Where(s => s.Name == "EquipmentSet" && kind(s) == kind(reference)))
                    foreach (var item in set.ChildNodes.OfType<XmlElement>())
                    {
                        string id = item.GetAttribute("id"); id = id.StartsWith("Item.") ? id.Substring(5) : id;
                        XmlElement i;
                        if (!items.TryGetValue(id, out i)) { wornProblems.Add(e.GetAttribute("id") + " " + id + " missing"); continue; }
                        var flags = i.SelectSingleNode("Flags") as XmlElement;
                        if (flags != null && flags.GetAttribute(female ? "NotUsableByFemale" : "NotUsableByMale") == "true") wornProblems.Add(e.GetAttribute("id") + " wears " + id);
                    }
        }
        var women = xml.Where(h => h.Female).ToList();
        Check(wornProblems.Count == 0 && women.All(h => h.BattleEquipment != null && rosterById[h.BattleEquipment].SelectSingleNode("Flags[@IsFemaleTemplate='true' and @IsLordTemplate='true']") != null &&
                                                         rosterById[h.CivilianEquipment].SelectSingleNode("Flags[@IsFemaleTemplate='true' and @IsLordTemplate='true']") != null) &&
              xml.Where(h => !h.Female).All(h => h.BattleEquipment == null),
              "no XML hero wears an item unusable by its sex: the " + women.Count + " women wear their culture's female lord rosters (battle and civilian), the men their template's equipment");
        var characterIds = new HashSet<string>(characters.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => e.GetAttribute("id")));
        var clanIds = new HashSet<string>(factions.Clans.Select(c => c.Id));
        Check(characters.DocumentElement.ChildNodes.OfType<XmlElement>().All(e => e.GetAttribute("is_hero") == "true" && !e.HasAttribute("is_template") && resolver.Exists(e.GetAttribute("culture")) &&
                  e.SelectSingleNode("face/face_key_template") != null && resolver.Exists(((XmlElement)e.SelectSingleNode("face/face_key_template")).GetAttribute("value")) &&
                  e.SelectNodes("Equipments/EquipmentSet").OfType<XmlElement>().All(s => resolver.Exists("EquipmentRoster." + s.GetAttribute("id")))) &&
              heroesXml.DocumentElement.ChildNodes.OfType<XmlElement>().All(e => characterIds.Contains(e.GetAttribute("id")) && clanIds.Contains(e.GetAttribute("faction").Substring(8)) &&
                  new[] { "spouse", "father", "mother" }.All(a => !e.HasAttribute(a) || characterIds.Contains(e.GetAttribute(a).Substring(5)))) &&
              characterIds.Count == xml.Count && heroesXml.DocumentElement.ChildNodes.Count == xml.Count,
              "the " + xml.Count + " XML heroes are hero NPCCharacters with an installed body range and equipment sets, and each Hero names its own character, a clan intent and XML relatives (Hero.Deserialize reads the character by id)");
        Check(xml.All(h => h.BodyTemplate != null && resolver.Exists(h.BodyTemplate) && h.BodyTemplate == "BodyProperty." + resolver.Element(h.BodyTemplate).GetAttribute("id")) &&
              xml.Select(h => h.BodySeed).Distinct().Count() == xml.Count && runtime.All(h => h.BodySeed == 0),
              "every XML hero has a distinct body seed (" + xml.Select(h => h.BodySeed).Distinct().Count() + ") over its template's installed body range, from which HeroBodies draws its explicit body in game; runtime heroes take HeroCreator's");
        Check(relatives.Where(h => h.Spouse != null).All(h => byId[h.Spouse].Spouse == h.Id && byId[h.Spouse].Female != h.Female && byId[h.Spouse].Clan == h.Clan) &&
              relatives.Where(h => h.Father != null).All(h => !byId[h.Father].Female && byId[h.Mother].Female && byId[h.Father].Age - h.Age >= 18 && byId[h.Mother].Age - h.Age >= 18 && byId[h.Father].Clan == h.Clan) &&
              xml.All(h => h.Age >= 18 && h.Age <= 70),
              "family links are symmetric: " + relatives.Count(h => h.Role == "relative" && h.Spouse != null) + " spouses linked both ways and " + relatives.Count(h => h.Father != null) + " adult heirs with father and mother of their clan");
        var troops = r.Troops; var main = troops.Where(t => !t.Bandit).ToList(); var bandits = troops.Where(t => t.Bandit).ToList();
        Check(main.Count == 7 && bandits.Count == 7 && main.All(t => t.MaxUpgradeDepth >= 4 && t.Troops.Count >= 30 && t.Ships.Count > 0 && t.PartyTemplates.Count >= 8) &&
              bandits.All(t => t.Troops.Count >= 1 && t.Roots.Any(x => x.StartsWith("clan "))) && troops.SelectMany(t => t.Ships).Any(s => s.StartsWith("ShipHull.")),
              "the troop graphs of the 7 presentation and 7 bandit cultures (with the bandit clans' spawn templates) resolve with no cycle, broken link, misfit or wrong-sex item, ship hulls included (" +
              String.Join(", ", main.Select(t => t.Culture + " " + t.Troops.Count + "/" + t.MaxUpgradeDepth)) + " troops/upgrade depth)");
        // Fault injection: each broken reference class refuses the build.
        Func<Dictionary<string, XmlDocument>, string, XmlElement> npc = (c, id) => (XmlElement)c["NPCCharacters"].DocumentElement.SelectSingleNode("NPCCharacter[@id='" + id + "']");
        string basic = S(templates["empire"]["values"] as IDictionary<string, object>, "basic_troop").Substring(13);
        string brokenTarget = inject(mutated(c => ((XmlElement)npc(c, basic).SelectSingleNode("upgrade_targets/upgrade_target")).SetAttribute("id", "NPCCharacter.mnbl_missing_troop")));
        string cycle = inject(mutated(c => {
            var target = ((XmlElement)npc(c, basic).SelectSingleNode("upgrade_targets/upgrade_target")).GetAttribute("id").Substring(13);
            var t = npc(c, target); var targets2 = t.SelectSingleNode("upgrade_targets") as XmlElement ?? (XmlElement)t.AppendChild(t.OwnerDocument.CreateElement("upgrade_targets"));
            var back = t.OwnerDocument.CreateElement("upgrade_target"); back.SetAttribute("id", "NPCCharacter." + basic); targets2.AppendChild(back); }));
        string missingBody = inject(mutated(c => ((XmlElement)npc(c, basic).SelectSingleNode("face/face_key_template")).SetAttribute("value", "BodyProperty.mnbl_missing_body")));
        string misfit = inject(mutated(c => ((XmlElement)npc(c, basic).SelectSingleNode("Equipments/EquipmentRoster/equipment[@slot='Head']")).SetAttribute("id", "Item.iron_spatha_sword_t2")));
        string coastal = r.Troops.SelectMany(t => t.PartyTemplates).First(p => element(catalog, "MBPartyTemplate[@id='" + p.Substring(14) + "']").SelectSingleNode("ship_hulls/ShipTemplateStack") != null);
        string missingHull = inject(mutated(c => ((XmlElement)element(c, "MBPartyTemplate[@id='" + coastal.Substring(14) + "']").SelectSingleNode("ship_hulls/ShipTemplateStack")).SetAttribute("id", "ShipHull.mnbl_missing_hull")));
        string banditTroop = inject(mutated(c => ((XmlElement)element(c, "MBPartyTemplate[@id='looters_template']").SelectSingleNode("stacks/PartyTemplateStack")).SetAttribute("troop", "NPCCharacter.mnbl_missing_looter")));
        Check(brokenTarget != null && brokenTarget.Contains("mnbl_missing_troop") && cycle != null && cycle.Contains("upgrade cycle") && missingBody != null && missingBody.Contains("mnbl_missing_body") &&
              misfit != null && misfit.Contains("does not fit slot Head") && missingHull != null && missingHull.Contains("mnbl_missing_hull") && banditTroop != null && banditTroop.Contains("mnbl_missing_looter"),
              "a missing upgrade target, an upgrade cycle, a missing body, a sword in a head slot, a missing ship hull in " + coastal + " and a missing troop in the looters' spawn template are each refused");
        // Optional templates and rosters: a broken one is excluded and never used; an empty pool refuses.
        string lordTemplate = lords.First(h => !h.Female).Template;
        var brokenLord = HeroFactory.Build(campaign, idMap, factions, rosters, resolverOf(mutated(c => ((XmlElement)npc(c, lordTemplate).SelectSingleNode("Equipments/EquipmentSet")).SetAttribute("id", "mnbl_missing_roster")), rosters));
        // Each culture has one female battle roster (*_bat_template_lady) and several civilian ones: break a civilian one, so excluding it
        // leaves the pool non-empty.
        string womanRoster = women.First(h => resolver.LordEquipment(h.Culture, true, true).Count > 1).CivilianEquipment;
        var unwearable = HeroFactory.Build(campaign, idMap, factions, rosters, resolverOf(mutated(c => ((XmlElement)element(c, "EquipmentRoster[@id='" + womanRoster + "']").SelectSingleNode("EquipmentSet/Equipment[@slot='Body']")).SetAttribute("id", "Item.battanian_savage_armor")), rosters));
        var artisans = resolver.NotableTemplates("empire", "Artisan", new List<string>());
        string noArtisan = inject(mutated(c => { foreach (var a in artisans) ((XmlElement)npc(c, a).SelectSingleNode("face/face_key_template")).SetAttribute("value", "BodyProperty.mnbl_missing_body"); }));
        Check(brokenLord.ExcludedTemplates.ContainsKey("NPCCharacter." + lordTemplate) && brokenLord.Heroes.All(h => h.Template != lordTemplate) &&
              unwearable.ExcludedTemplates.ContainsKey("EquipmentRoster." + womanRoster) && unwearable.ExcludedTemplates["EquipmentRoster." + womanRoster].Contains("not usable by a woman") &&
              unwearable.Heroes.All(h => h.CivilianEquipment != womanRoster) && noArtisan != null && noArtisan.Contains("no usable Artisan notable template"),
              "a broken lord template and a female roster holding armour women cannot wear are excluded and never used; a culture left with no usable artisan template is refused");

        // c3: counts are bounded by the policy, not by population, and every source hero has a disposition.
        var caps = O(O(rosters, "notables"), "caps");
        int bound = 0; float desired = 0.6f * towns.Count; while (bound < desired) bound++;
        Check(towns.All(t => O(caps, "town").All(x => count(S(t, "id"), x.Key) <= Convert.ToInt32(x.Value))) && villages.All(v => O(caps, "village").All(x => count(S(v, "id"), x.Key) <= Convert.ToInt32(x.Value))) &&
              notables.All(h => (h.SettlementKind == "town" ? O(caps, "town") : O(caps, "village")).ContainsKey(h.Occupation)) && companions.Count == bound && bound == 28 &&
              factions.Clans.All(c => relatives.Count(h => h.Clan == c.Id) <= 3) && xml.Count <= lords.Count + 3 * factions.Clans.Count,
              "notables stay within the per-settlement caps (" + notables.Count + "), companions follow the installed loop (" + companions.Count + " at " + towns.Count + " towns), no clan gets more than 3 relatives and XML heroes are at most source lords + 3 per clan (" + xml.Count + ")");
        // Inflate the source: every merchant and wanderer record twenty more times; the output stays within the same bounds.
        var inflated = Copy(campaign);
        var extra = new List<object>();
        foreach (var h in Records(inflated, "heroes").Where(h => S(h, "occupation") == "Merchant" || S(h, "occupation") == "Wanderer").ToList())
            for (int i = 1; i <= 20; i++)
            {
                var clone = Copy(h); string uid = S(h, "uid") + ":clone" + i;
                clone["uid"] = uid; clone["id"] = r.WorldPrefix + "h_" + Sha(Encoding.UTF8.GetBytes("test-clone\n" + uid)).Substring(0, 10);
                extra.Add(clone);
            }
        inflated["heroes"] = Records(inflated, "heroes").Cast<object>().Concat(extra).ToArray();
        var big = build(inflated, rosters);
        Func<HeroFactory.Result, string, string, int> countIn = (x, settlement, occupation) => x.RuntimeHeroes.Count(h => h.Settlement == settlement && h.Occupation == occupation);
        Check(towns.All(t => O(caps, "town").All(x => countIn(big, S(t, "id"), x.Key) <= Convert.ToInt32(x.Value))) && big.RuntimeHeroes.Count(h => h.Occupation == "Wanderer") == bound &&
              big.XmlHeroes.Count() == xml.Count && big.Heroes.Count < r.Heroes.Count + 100 && big.Dispositions.Count == r.Dispositions.Count + extra.Count,
              "twenty more copies of every source merchant and wanderer (" + (compiler.Count + extra.Count) + " source heroes) still give at most the capped notables and " + bound + " companions (" +
              big.Heroes.Count + " heroes, " + big.Dispositions.Count(d => d.Outcome == "bounded") + " bounded): counts follow settlements and towns, not the source population");
        var outcomes = r.Dispositions.GroupBy(d => d.Outcome).ToDictionary(g => g.Key, g => g.Count());
        Check(compiler.Values.All(h => r.Dispositions.Count(d => d.Uid == S(h, "uid")) == 1) && L(campaign, "notInstantiated").All(u => r.Dispositions.Single(d => d.Uid == u).Outcome == "notInstantiated") &&
              r.Dispositions.Where(d => d.Outcome == "bounded").All(d => d.Reason.StartsWith("rosters.")) && r.Dispositions.Count == compiler.Count + L(campaign, "notInstantiated").Count,
              "every source hero has exactly one disposition: " + String.Join(", ", outcomes.OrderBy(p => p.Key).Select(p => p.Value + " " + p.Key)) + "; each bounded one names the policy rule");
        // The MMR-32-owned compiler slots are all resolved.
        var owned = new List<KeyValuePair<string, string>>();
        foreach (var kindName in new[] { "kingdoms", "towns", "castles", "heroes" })
            foreach (var rec in Records(campaign, kindName))
                foreach (var slot in ((IEnumerable)rec["slots"]).Cast<IDictionary<string, object>>().Where(x => S(x, "owner") == "MMR-32"))
                    owned.Add(new KeyValuePair<string, string>(S(rec, "id"), S(slot, "name")));
        Check(owned.Count == 40 + 45 + 26 + 2 * compiler.Count && owned.All(o => r.Slots.Count(x => x.Owner == o.Key && x.Name == o.Value && x.Value != null && x.Value.Count > 0) == 1) &&
              r.Slots.Where(x => x.Name == "troopTreeAndRecruitTemplates").All(x => resolver.Exists((string)x.Value["basicTroop"]) && resolver.Exists((string)x.Value["eliteBasicTroop"]) && resolver.Exists((string)x.Value["partyTemplate"])) &&
              r.Slots.Where(x => x.Name == "garrison").All(x => resolver.Exists((string)x.Value["partyTemplate"])),
              "all " + owned.Count + " MMR-32-owned compiler slots are resolved: kingdom troop trees and recruit templates, town and castle garrison templates, and every hero's template and age/traits/family or disposition");
        // Stable synthesized identities.
        var assigned = new HashSet<string>(((IEnumerable)idMap["entries"]).Cast<IDictionary<string, object>>().Select(e => S(e, "id")));
        var synthesized = r.Heroes.Where(h => h.Synthesized).ToList();
        Check(synthesized.All(h => h.Id == r.WorldPrefix + "h_" + Sha(Encoding.UTF8.GetBytes("mnbl.stable-id/1\nhero\n" + h.Uid)).Substring(0, 10) && !assigned.Contains(h.Id)) &&
              r.Heroes.Where(h => !h.Synthesized).All(h => compiler.ContainsKey(h.Id)) && r.Heroes.Select(h => h.Id).Distinct().Count() == r.Heroes.Count &&
              r.Heroes.All(h => CampaignIdentity.Problem(h.Id, r.WorldPrefix) == null) && synthesized.All(h => h.Provenance.StartsWith("adapter-synthesized")),
              "the " + synthesized.Count + " synthesized heroes take MMR-27 stable ids of documented uids (spouse:, heir:, notable:), collide with no assigned id and are marked adapter-synthesized");
        var again = HeroFactory.Build(campaign, idMap, factions, rosters, resolver);
        var shuffled = Copy(campaign);
        foreach (var key in new[] { "heroes", "towns", "villages", "castles", "clans", "kingdoms" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(again.IntentsSha256 == r.IntentsSha256 && again.Documents.Select(d => d.Value.OuterXml).SequenceEqual(r.Documents.Select(d => d.Value.OuterXml)) && build(shuffled, rosters).IntentsSha256 == r.IntentsSha256,
              "building twice, or from reordered records, gives the same intents (" + r.IntentsSha256.Substring(0, 12) + ") and XML");
        // Streams: each changes what it draws and what derives from that, never ownership, occupations, clans or families.
        Func<string, HeroFactory.Result> reversion = stream => { var p = Copy(rosters); O(p, "streams")[stream] = 2; return build(campaign, p); };
        Func<HeroFactory.Result, Dictionary<string, HeroFactory.HeroIntent>> map = x => x.Heroes.ToDictionary(h => h.Id);
        Func<HeroFactory.Result, string> who = x => String.Join("|", x.Heroes.Select(h => h.Id + h.Occupation + h.Clan + h.Spouse + h.Father + h.Mother));
        var names2 = map(reversion("names")); var templates2 = map(reversion("templates")); var companions2 = reversion("companions"); var bodies2 = map(reversion("bodies"));
        bool namesOnly = r.Heroes.All(h => names2[h.Id].Template == h.Template && names2[h.Id].Age == h.Age && names2[h.Id].Female == h.Female && names2[h.Id].BodySeed == h.BodySeed &&
                                           (h.Synthesized || names2[h.Id].Name == h.Name)) && r.Heroes.Any(h => names2[h.Id].Name != h.Name);
        bool templatesOnly = r.Heroes.All(h => templates2[h.Id].Age == h.Age && templates2[h.Id].BodySeed == h.BodySeed && (h.Synthesized || templates2[h.Id].Name == h.Name) &&
                                               (templates2[h.Id].Female == h.Female || !h.Xml) && (templates2[h.Id].Name == h.Name || templates2[h.Id].Female != h.Female)) &&
                             r.Heroes.Any(h => templates2[h.Id].Template != h.Template);
        bool bodiesOnly = r.Heroes.All(h => bodies2[h.Id].Template == h.Template && bodies2[h.Id].Name == h.Name && bodies2[h.Id].Age == h.Age && bodies2[h.Id].Female == h.Female) &&
                          xml.All(h => bodies2[h.Id].BodySeed != h.BodySeed);
        Check(namesOnly && templatesOnly && bodiesOnly && who(reversion("names")) == who(r) && who(reversion("templates")) == who(r) && who(companions2) != who(r) && companions2.Heroes.Count == r.Heroes.Count,
              "the names stream changes only synthesized names; the templates stream templates, homes and rosters, and with them a runtime hero's sex and a synthesized name that follows it; the bodies stream only body seeds; the companions stream only which wanderers are companions; none changes ownership, occupations, clans or families");
        // The engine numbers in the policy are tied to the pinned TaleWorlds.CampaignSystem assembly.
        var locked = ((IEnumerable)Load(Path.Combine(repo, "config/runtime-api-lock.json"))["assemblies"]).Cast<IDictionary<string, object>>().Single(a => S(a, "name") == "TaleWorlds.CampaignSystem");
        Check(S(provenance, "campaignSystemSha256") == S(locked, "sha256"),
              "the roster policy's engine numbers were read from the TaleWorlds.CampaignSystem assembly config/runtime-api-lock.json pins (" + S(locked, "sha256").Substring(0, 12) + "), which Build.ps1 verifies against the installed game");

        // c2 (offline part): character creation lists the presentation cultures, and each has its creation assets.
        var creation = new[] { "aserai", "battania", "empire", "khuzait", "sturgia", "vlandia", "nord" };
        var rosterIds = rosterById.Keys.ToList();
        var presentationCultures = new HashSet<string>(r.SettlementCultures.Values.Concat(r.Heroes.Select(h => h.Culture)));
        // Installed names: player_char_creation_<culture>_<title>_<m|f>, mother/father_char_creation_<parent occupation>_<culture>.
        Check(presentationCultures.SetEquals(creation) && creation.All(c => rosterIds.Any(x => x.StartsWith("player_char_creation_" + c + "_")) &&
                  new[] { "mother_char_creation_", "father_char_creation_" }.All(p => rosterIds.Any(x => x.StartsWith(p) && x.EndsWith("_" + c))) &&
                  resolver.Exists(resolver.Element("Culture." + c).GetAttribute("default_character_creation_body_property"))),
              "the world presents through exactly the 7 character-creation cultures, and each has player, mother and father creation equipment rosters and a creation body property");

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.heroes", ["version"] = 2, ["ticket"] = "MMR-32", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["xmlHeroes"] = xml.Count, ["sourceLords"] = lords.Count, ["women"] = women.Count, ["spouses"] = relatives.Count(h => h.Spouse != null && h.Father == null), ["heirs"] = relatives.Count(h => h.Father != null),
            ["bodySeeds"] = xml.Select(h => h.BodySeed).Distinct().Count(),
            ["notables"] = notables.GroupBy(h => h.Occupation).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            ["synthesizedNotables"] = notables.Count(h => h.Synthesized), ["sourceNotables"] = notables.Count(h => !h.Synthesized), ["companions"] = companions.Count, ["companionBound"] = bound,
            ["dispositions"] = outcomes, ["templates"] = r.Heroes.Select(h => h.Template).Distinct().Count(), ["excludedTemplates"] = r.ExcludedTemplates, ["slots"] = r.Slots.Count,
            ["inflation"] = new SortedDictionary<string, object> { ["sourceHeroes"] = compiler.Count + extra.Count, ["heroes"] = big.Heroes.Count, ["bounded"] = big.Dispositions.Count(d => d.Outcome == "bounded") },
            ["troops"] = troops.Select(t => new SortedDictionary<string, object> { ["culture"] = t.Culture, ["bandit"] = t.Bandit, ["troops"] = t.Troops.Count, ["maxUpgradeDepth"] = t.MaxUpgradeDepth,
                                                                                    ["partyTemplates"] = t.PartyTemplates.Count, ["ships"] = t.Ships.Distinct().Count() }).ToList(),
            ["documents"] = r.Documents.ToDictionary(d => d.Key, d => Sha(Encoding.UTF8.GetBytes(d.Value.OuterXml))),
            ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
