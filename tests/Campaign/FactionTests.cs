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
using Factory = MNBL.ProceduralWorld.CampaignRegistry.FactionFactory;

// MMR-31/MMR-280 faction tests, without the game: FactionTests BUNDLE_DIR REPO_DIR OUT_JSON
// The compiler records (campaign.json, id-map.json) come from the real MMR-29 bundle, read through WorldDataBundle; the faction
// policy, culture presentations and culture templates come from the repository. OUT_JSON receives the evidence summary.
internal static class FactionTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllText(path)); }
    private static IDictionary<string, object> Copy(IDictionary<string, object> o) { return Parse(Json.Serialize(o)); }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => x as string).ToList() : new List<string>(); }

    // The refusal message of a build, or null when it builds.
    private static string Refusal(Func<Factory.Result> build)
    {
        try { build(); return null; } catch (Factory.FactionException e) { return e.Message; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: FactionTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
        try { Run(args[0], args[1], args[2]); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string bundleDir, string repo, string outJson)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var campaign = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
        var idMap = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/id-map.json")));
        var policy = Load(Path.Combine(repo, "worldgen/policies/factions.json"));
        var presentations = Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var contract = Load(Path.Combine(repo, "worldgen/templates/cultures/contract.json"));
        var population = Load(Path.Combine(repo, "worldgen/policies/population.json"));
        Func<IDictionary<string, object>, Factory.Result> build = p => Factory.Build(campaign, idMap, p, presentations, templates, population);
        Func<IDictionary<string, object>, Factory.Result> buildCampaign = c => Factory.Build(c, idMap, policy, presentations, templates, population);
        var r = build(policy);

        var heroes = Records(campaign, "heroes").ToDictionary(h => S(h, "id"));
        var towns = Records(campaign, "towns").ToDictionary(t => S(t, "id"));
        var castles = new HashSet<string>(Records(campaign, "castles").Select(c => S(c, "id")));   // a house may be seated at a castle (MMR-279)
        // campaign.json v2 (MMR-279): the planned noble houses are the kingdoms' clans, the companies and outlaw clans the minor clans (MMR-280).
        var compilerClans = Records(campaign, "houses").ToDictionary(c => S(c, "id"));
        var compilerMinor = Records(campaign, "mercenaryCompanies").Concat(Records(campaign, "outlawClans")).ToDictionary(c => S(c, "id"));
        var compilerKingdoms = Records(campaign, "kingdoms").ToList();
        Func<IDictionary<string, object>, bool> houseLord = h => S(h, "occupation") == "Lord" && S(h, "bucket") == "house" && !Equals(h["alive"], false);
        var lords = heroes.Values.Where(houseLord).Select(h => S(h, "id")).ToList();
        var allLords = heroes.Values.Where(h => S(h, "occupation") == "Lord").Select(h => S(h, "id")).ToList();
        var kingdomOf = r.Kingdoms.ToDictionary(k => k.Id);

        // c1: every kingdom intent names a ruling house, a leader and a capital that resolve in the compiler graph.
        Check(r.Kingdoms.Count == compilerKingdoms.Count && r.Kingdoms.All(k => heroes.ContainsKey(k.RulerId) && compilerClans.ContainsKey(k.RulingClanId) &&
              towns.ContainsKey(k.CapitalId) && k.Towns.Contains(k.CapitalId) && S(compilerClans[k.RulingClanId], "leader") == k.RulerId),
              "every one of " + r.Kingdoms.Count + " kingdom intents names a compiler ruler leading its compiler ruling house and a capital among its towns");
        Check(r.Kingdoms.All(k => k.RulerId != null && S(heroes[k.RulerId], "occupation") == "Lord"),
              "no kingdom has a null ruler (" + r.Kingdoms.Count(k => S(heroes[k.RulerId], "role") == "regent") + " ruled by MMR-27's synthesized regents)");
        var clanOf = r.Clans.SelectMany(c => c.Members.Select(m => new KeyValuePair<string, string>(m, c.Id))).ToList();
        Check(lords.All(l => clanOf.Count(x => x.Key == l) == 1) && clanOf.Count == lords.Count &&
              r.Clans.All(c => c.Members.All(m => S(heroes[m], "occupation") == "Lord" && S(heroes[m], "kingdom") == c.KingdomId)),
              "every one of " + lords.Count + " house lords is a member of exactly one clan, and every member is a lord of the clan's kingdom");
        var minors = lords.Where(l => Convert.ToInt32(heroes[l]["age"]) < 18).ToList();
        Check(r.Clans.Count == compilerClans.Count && r.Clans.All(c => compilerClans.ContainsKey(c.Id) && S(compilerClans[c.Id], "kingdom") == c.KingdomId &&
              S(compilerClans[c.Id], "leader") == c.LeaderId && S(compilerClans[c.Id], "home") == c.HomeId && c.Fiefs.Contains(c.HomeId) &&
              c.Members.SequenceEqual(L(compilerClans[c.Id], "members").OrderBy(x => x, StringComparer.Ordinal))) && r.Clans.Count(c => c.Ruling) == r.Kingdoms.Count &&
              minors.Count > 0 && minors.All(m => S(heroes[m], "father") != null && S(heroes[m], "mother") != null),
              "each of " + r.Clans.Count + " planned houses is one clan of its kingdom with its leader, its seat as home and every planned member, its " + minors.Count + " minors with both parents set; one ruling house per kingdom");
        var dead = heroes.Values.Where(h => S(h, "occupation") == "Lord" && Equals(h["alive"], false)).Select(h => S(h, "id")).ToList();
        Check(dead.Count > 0 && dead.All(d => r.Clans.Count(c => c.DeadParents.Contains(d)) == 1 && r.Clans.All(c => !c.Members.Contains(d)) &&
              r.Clans.Single(c => c.DeadParents.Contains(d)).Id == S(heroes[d], "clan") &&
              heroes.Values.Any(h => (S(h, "father") == d || S(h, "mother") == d) && S(h, "clan") == S(heroes[d], "clan"))),
              "the " + dead.Count + " dead parents (alive false) belong to their house without being members, each the parent of a member");
        var affiliation = r.Affiliations.ToDictionary(a => a.Hero);
        Check(r.Affiliations.Count == allLords.Count && clanOf.All(x => affiliation[x.Key].Clan == x.Value && affiliation[x.Key].Kingdom == r.AllClans.First(c => c.Id == x.Value).KingdomId) &&
              r.Kingdoms.All(k => affiliation[k.RulerId].Clan == k.RulingClanId) && allLords.All(l => S(heroes[l], "clan") == affiliation[l].Clan) &&
              dead.All(d => affiliation[d].Dead && affiliation[d].Kingdom == S(heroes[d], "kingdom")) && allLords.Except(dead).All(l => !affiliation[l].Dead),
              "the lord-to-clan affiliations for C03 match the compiler's hero.clan for all " + allLords.Count + " lords: house members, dead parents, company and outlaw clan members");
        var assigned = new HashSet<string>(((IEnumerable)idMap["entries"]).Cast<IDictionary<string, object>>().Select(e => S(e, "id")));
        Func<string, string> stable = uid => r.WorldPrefix + "l_" + Sha("mnbl.stable-id/1\nclan\n" + uid).Substring(0, 10);
        Check(r.Clans.All(c => stable(c.Uid) == c.Id && assigned.Contains(c.Id)) && r.Clans.Select(c => c.Id).Distinct().Count() == r.Clans.Count,
              "clan ids are the compiler's house ids (the MMR-27 stable-id formula, kind clan)");
        // MMR-286: kingdom names come clean and unique from the compiler (its name is shown; a renamed one keeps its source name and says it in
        // its encyclopedia text); house names come unique from the compiler's cleaner.
        var kingdomRecords = Records(campaign, "kingdoms").ToDictionary(k => S(k, "id"));
        var renamed = r.Kingdoms.Where(k => k.Name != k.SourceName).ToList();
        Check(r.Kingdoms.Select(k => k.Name).Distinct().Count() == r.Kingdoms.Count && r.Clans.Select(c => c.Name).Distinct().Count() == r.Clans.Count &&
              r.Kingdoms.All(k => k.Name == S(kingdomRecords[k.Id], "name") && k.SourceName == (S(kingdomRecords[k.Id], "sourceName") ?? k.Name)) &&
              renamed.All(k => k.Text.Contains(k.SourceName.Replace('{', '(').Replace('}', ')'))) && r.Clans.All(c => c.Name == c.SourceName) &&
              r.Kingdoms.All(k => k.NameLocalized == "{=" + k.Id + "}" + k.Name.Replace('{', '(').Replace('}', ')')),
              "kingdom names are the compiler's clean, unique names (" + renamed.Count + " renamed keep their source name in the intent and the encyclopedia text), and the house names come unique from the compiler's cleaner");
        Func<string, int, int> tierOf = (block, fiefs) => ((IEnumerable)O(policy, block)["tierByFiefs"]).Cast<IDictionary<string, object>>().Where(t => fiefs >= Convert.ToInt32(t["minFiefs"])).Select(t => Convert.ToInt32(t["tier"])).DefaultIfEmpty(0).Max();
        Check(r.Clans.Where(c => c.Ruling).All(c => c.Tier == tierOf("rulingClan", kingdomOf[c.KingdomId].Fiefs) && c.Tier >= 4) &&
              r.Clans.Where(c => !c.Ruling).All(c => c.Tier == tierOf("houses", c.Fiefs.Count) && c.Fiefs.Count >= 1 && c.Fiefs.Count <= 4) && !policy.ContainsKey("vassalClans"),
              "ruling houses take their tier from the kingdom's fiefs and the other houses from their own 1..4 fiefs (" + String.Join(", ", r.Clans.GroupBy(c => c.Tier).OrderBy(g => g.Key).Select(g => g.Count() + " at tier " + g.Key)) + "); no vassal-per-lord rule is left");

        // MMR-280: the mercenary companies and outlaw clans are minor clans with their kind's flags, the per-culture troop line, a town home and wars.
        var lines = O(O(population, "troopTemplates"), "cultures");
        Func<Factory.ClanIntent, IDictionary<string, object>> lineOf = c => c.Kind == "company" ? O(O(lines, c.Culture), "mercenary") :
            ((IEnumerable)O(lines, c.Culture)["outlaws"]).Cast<IDictionary<string, object>>().OrderBy(l => S(l, "kind") == c.OutlawKind ? 0 : 1).First();
        var lordParty = new HashSet<string>(templates.Values.Select(t => S(O(t, "values"), "default_party_template").Substring(14)));
        var minorXml = r.Documents.Single(d => d.Key == "Factions").Value.SelectNodes("//Faction[@is_minor_faction='true']").Cast<XmlElement>().ToDictionary(e => e.GetAttribute("id"));
        Check(r.MinorClans.Count == compilerMinor.Count && r.MinorClans.Count(c => c.Kind == "company") == Records(campaign, "mercenaryCompanies").Count() &&
              r.MinorClans.All(c => c.KingdomId == null && c.Members.Contains(c.LeaderId) && towns.ContainsKey(c.HomeId) && S(compilerMinor[c.Id], "leader") == c.LeaderId &&
                                    c.Members.SequenceEqual(L(compilerMinor[c.Id], "members").OrderBy(x => x, StringComparer.Ordinal)) &&
                                    c.Tier == Convert.ToInt32(O(population, c.Kind == "company" ? "companies" : "outlaws")["tier"])),
              "the " + r.MinorClans.Count(c => c.Kind == "company") + " mercenary companies and " + r.MinorClans.Count(c => c.Kind == "outlaw") + " outlaw clans are minor clans with no kingdom, their planned leader and members, a town home and the policy tier");
        var census = O(campaign, "census");
        Check(r.MinorFactionHeroIds.Count() == Convert.ToInt32(census["companyMembers"]) + Convert.ToInt32(census["outlawMembers"]) && r.MinorFactionHeroIds.Distinct().Count() == r.MinorFactionHeroIds.Count() &&
              r.MinorFactionHeroIds.All(id => S(heroes[id], "bucket") == "company" || S(heroes[id], "bucket") == "outlaw"),
              "the " + r.MinorFactionHeroIds.Count() + " minor faction heroes (for MMR-281's IsMinorFactionHero) are exactly the census's company and outlaw clan members");
        Check(r.MinorClans.All(c => minorXml.ContainsKey(c.Id) && !minorXml[c.Id].HasAttribute("super_faction") && minorXml[c.Id].GetAttribute("owner") == "Hero." + c.LeaderId &&
                  (c.Kind == "company" ? minorXml[c.Id].GetAttribute("is_clan_type_mercenary") == "true" && !minorXml[c.Id].HasAttribute("is_outlaw")
                                       : minorXml[c.Id].GetAttribute("is_outlaw") == "true" && new[] { "is_mafia", "is_nomad", "is_sect" }.Count(f => minorXml[c.Id].GetAttribute(f) == "true") == 1 &&
                                         minorXml[c.Id].GetAttribute("is_" + c.OutlawKind) == "true" && !minorXml[c.Id].HasAttribute("is_clan_type_mercenary")) &&
                  minorXml[c.Id].GetAttribute("default_party_template") == "PartyTemplate." + S(lineOf(c), "partyTemplate") && !lordParty.Contains(c.PartyTemplate) &&
                  minorXml[c.Id].SelectNodes("minor_faction_character_templates/template/@id").Cast<XmlAttribute>().Select(a => a.Value.Substring(13)).SequenceEqual(L(lineOf(c), "characterTemplates")) &&
                  minorXml[c.Id].GetAttribute("initial_home_settlement") == "Settlement." + c.HomeId) &&
              r.Documents.Single(d => d.Key == "Factions").Value.SelectNodes("//Faction[@is_minor_faction='true']").Count == r.MinorClans.Count,
              "every minor clan carries the flags of its kind (companies is_clan_type_mercenary, outlaw clans is_outlaw and one of is_mafia/is_nomad/is_sect), no super_faction, and the default_party_template and 4 character templates of its population.json line, never a lord party template");
        var kingdomCulture = r.Kingdoms.ToDictionary(k => k.Id, k => k.Culture);
        int maxWarKingdoms = Convert.ToInt32(O(population, "outlaws")["maxWarKingdoms"]);
        Check(r.MinorClans.Where(c => c.Kind == "outlaw").All(c => c.Wars.SequenceEqual(r.Kingdoms.Where(k => k.Culture == c.Culture).Select(k => k.Id).OrderBy(x => x, StringComparer.Ordinal)) && c.Wars.Count <= maxWarKingdoms) &&
              r.MinorClans.Where(c => c.Kind == "company").All(c => c.Wars.Count == 0),
              "each outlaw clan records its wars with the kingdoms of its culture, at most " + maxWarKingdoms + " (the war guard), and no company records a war");

        // c2: culture presentation is exhaustive and follows the asset policy.
        var civilizations = ((IEnumerable)presentations["civilizations"]).Cast<IDictionary<string, object>>().ToDictionary(c => S(c, "civilization"), c => S(c, "culture"));
        var sourceCivs = heroes.Values.Select(h => S(h, "civilization")).Concat(compilerKingdoms.Select(k => S(k, "civilization"))).Distinct().ToList();
        var required = L(contract, "required");
        Check(sourceCivs.All(c => civilizations.ContainsKey(c) && templates.ContainsKey(civilizations[c])) &&
              templates.Values.All(t => !L(t, "requiredMissing").Any() && !L(t, "navalMissing").Any() && required.All(m => O(t, "values").ContainsKey(m))),
              "all " + sourceCivs.Count + " source civilizations present through an installed main culture whose template sets every one of " + required.Count + " required contract members, naval members included");
        Check(r.Kingdoms.All(k => civilizations[k.Civilization] == k.Culture) && r.Cultures.Sum(c => c.Kingdoms.Count) == r.Kingdoms.Count &&
              r.Cultures.All(c => templates.ContainsKey(c.Presentation)) && r.Clans.All(c => templates.ContainsKey(c.Culture)) &&
              r.Documents.Select(d => d.Key).SequenceEqual(new[] { "Kingdoms", "Factions" }),
              "every kingdom's culture is its civilization's presentation, every clan's culture is an installed main culture, and the world defines no culture of its own");
        var contractMembers = O(contract, "members").Values.Cast<IDictionary<string, object>>().ToList();
        var deserializers = new[] { "CultureObject.Deserialize", "BasicCultureObject.Deserialize" };
        var summary = O(contract, "summary");
        Check(contractMembers.Count >= 100 && !L(summary, "unmapped").Any() && Convert.ToInt32(summary["privateSetter"]) + Convert.ToInt32(summary["noSetter"]) + Convert.ToInt32(summary["publicSetter"]) == contractMembers.Count &&
              contractMembers.Where(m => S(m, "setter") != "none").All(m => L(m, "writers").Any() && L(m, "writers").All(w => deserializers.Contains(w))) &&
              ((IEnumerable)contract["constructors"]).Cast<IDictionary<string, object>>().All(c => !L(c, "parameters").Any()) &&
              L(contract, "navalMembers").All(n => O(contract, "members").ContainsKey(n)),
              "the culture contract inventories every member Deserialize reads, its setter on the declaring type (" + summary["privateSetter"] + " private, " + summary["noSetter"] + " get-only, " +
              summary["publicSetter"] + " public) and its writers (only Deserialize); the constructors take no culture data");
        var incomplete = templates.ToDictionary(t => t.Key, t => Copy(t.Value), StringComparer.Ordinal);
        string firstCulture = r.Kingdoms[0].Culture;
        incomplete[firstCulture]["requiredMissing"] = new object[] { required[0] };
        string incompleteRefusal = Refusal(() => Factory.Build(campaign, idMap, policy, presentations, incomplete, population));
        Check(incompleteRefusal != null && incompleteRefusal.Contains("no complete culture template"), "a presentation whose template lacks a required contract member is refused");

        // c3: the same input reproduces factions, banners and relationships.
        var again = build(policy);
        Check(again.IntentsSha256 == r.IntentsSha256 && again.Documents.Select(d => d.Value.OuterXml).SequenceEqual(r.Documents.Select(d => d.Value.OuterXml)),
              "building twice gives the same intents (" + r.IntentsSha256.Substring(0, 12) + ") and the same XML");
        var shuffled = Copy(campaign);
        foreach (var key in new[] { "heroes", "kingdoms", "houses", "mercenaryCompanies", "outlawClans", "wars", "towns", "castles" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(buildCampaign(shuffled).IntentsSha256 == r.IntentsSha256, "reordering the compiler records changes nothing");
        // Independent streams: re-versioning one stream changes only what it draws.
        Func<string, int, Factory.Result> reversion = (stream, version) => { var p = Copy(policy); O(p, "streams")[stream] = version; return build(p); };
        var banner2 = reversion("banner", 2); var colors2 = reversion("colors", 2); var relations2 = reversion("relations", 2);
        Func<Factory.Result, string> ownership = x => String.Join("|", x.AllClans.Select(c => c.Id + ":" + c.LeaderId + ":" + c.KingdomId + ":" + c.Tier + ":" + String.Join(",", c.Members)));
        Func<Factory.Result, string> relations = x => String.Join("|", x.Relations.Select(v => v.A + v.B + v.RulerRelation + v.Stance));
        Func<Factory.Result, string> colors = x => String.Join("|", x.Kingdoms.Select(k => k.Color + k.Color2).Concat(x.MinorClans.Select(c => c.Color + c.Color2)));
        Func<Factory.Result, string> icons = x => String.Join("|", x.AllClans.Select(c => c.BannerKey.Split('.')[10]).Concat(x.Kingdoms.Select(k => k.BannerKey.Split('.')[10])));
        Check(ownership(banner2) == ownership(r) && relations(banner2) == relations(r) && colors(banner2) == colors(r) && icons(banner2) != icons(r),
              "a new banner stream version redraws the icons and leaves ownership, colors and relations unchanged");
        Check(ownership(colors2) == ownership(r) && relations(colors2) == relations(r) && colors(colors2) != colors(r) && icons(colors2) == icons(r),
              "a new colors stream version redraws the colors and leaves ownership, icons and relations unchanged");
        Check(ownership(relations2) == ownership(r) && relations(relations2) != relations(r) && colors(relations2) == colors(r) && icons(relations2) == icons(r),
              "a new relations stream version redraws the ruler relations and leaves ownership and banners unchanged");

        // Banners, diplomacy and the XML.
        var banners = O(policy, "banners");
        var palette = new HashSet<int>(((IEnumerable)banners["palette"]).Cast<IDictionary<string, object>>().Select(c => Convert.ToInt32(c["id"])));
        var cultures = O(policy, "cultures");
        Func<string, string, bool> validBanner = (key, culture) => {
            var n = key.Split('.').Select(int.Parse).ToArray();
            var pool = ((IEnumerable)O(cultures, culture)["bannerIcons"]).Cast<object>().Select(Convert.ToInt32);
            return n.Length == 20 && n[0] == Convert.ToInt32(banners["backgroundMesh"]) && palette.Contains(n[1]) && palette.Contains(n[11]) && n[1] != n[11] && pool.Contains(n[10]);
        };
        var allBanners = r.Kingdoms.Select(k => k.BannerKey).Concat(r.AllClans.Select(c => c.BannerKey)).ToList();
        var pairs = r.Kingdoms.Select(k => k.Color + "." + k.Color2).Concat(r.MinorClans.Select(c => c.Color + "." + c.Color2)).ToList();
        Check(r.Kingdoms.All(k => validBanner(k.BannerKey, k.Culture)) && r.MinorClans.All(c => validBanner(c.BannerKey, c.Culture) && c.Color != c.Color2) &&
              pairs.Distinct().Count() == pairs.Count && pairs.Count == r.Kingdoms.Count + r.MinorClans.Count && allBanners.Distinct().Count() == allBanners.Count,
              "kingdom and minor clan banners are two-layer keys of the installed background, palette and culture icons; all " + pairs.Count + " colour pairs (kingdoms, companies, outlaw clans) are unique world-wide and all " + allBanners.Count + " banners are unique in the world");
        Check(r.Clans.All(c => { var k = kingdomOf[c.KingdomId]; var n = c.BannerKey.Split('.'); var kn = k.BannerKey.Split('.');
                                 return validBanner(c.BannerKey, k.Culture) && n[1] == kn[1] && n[11] == kn[11] && n[10] != kn[10]; }),
              "clan banners carry their kingdom's colors with an icon of their own, never the kingdom's");
        int kc = r.Kingdoms.Count;
        var kingdomsXml = r.Documents.Single(d => d.Key == "Kingdoms").Value; var factionsXml = r.Documents.Single(d => d.Key == "Factions").Value;
        var wars = Records(campaign, "wars").Select(w => new[] { S(w, "a"), S(w, "b") }).ToList();
        Func<string, string, bool> atWar = (a, b) => wars.Any(w => (w[0] == a && w[1] == b) || (w[0] == b && w[1] == a));
        Func<XmlElement, List<string>> warsIn = k => k.SelectNodes("relationships/relationship").Cast<XmlElement>()
            .Where(e => e.GetAttribute("value") == "-1" && e.GetAttribute("isAtWar") == "true").Select(e => e.GetAttribute("kingdom").Substring(8)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        int maxWars = Convert.ToInt32(O(population, "wars")["maxPerKingdom"]);
        Check(wars.Count > 0 && r.Relations.Count == kc * (kc - 1) / 2 && r.Relations.All(v => v.Stance == (atWar(v.A, v.B) ? "war" : "neutral") && v.RulerRelation >= -20 && v.RulerRelation <= 20 &&
              v.RulerA == kingdomOf[v.A].RulerId && v.RulerB == kingdomOf[v.B].RulerId) && r.Relations.Count(v => v.Stance == "war") == wars.Count &&
              kingdomsXml.SelectNodes("//Kingdom").Cast<XmlElement>().All(k => warsIn(k).SequenceEqual(kingdomOf[k.GetAttribute("id")].Wars) &&
                  kingdomOf[k.GetAttribute("id")].Wars.All(o => atWar(k.GetAttribute("id"), o)) && k.SelectNodes("relationships/relationship").Count == kingdomOf[k.GetAttribute("id")].Wars.Count) &&
              kingdomsXml.SelectNodes("//relationship").Count == 2 * wars.Count && r.Kingdoms.All(k => k.Wars.Count >= 1 && k.Wars.Count <= maxWars),
              "the " + wars.Count + " campaign wars[] pairs start at war: each kingdom carries <relationship kingdom value=\"-1\" isAtWar=\"true\"/> for each of its wars, on both sides, as spkingdoms.xml does; every kingdom starts 1.." + maxWars + " wars (" +
              String.Join(", ", r.Kingdoms.Select(k => k.Wars.Count)) + "); the other pairs are neutral and the rulers' relation within the policy spread is an intent for C03");
        var refs = kingdomsXml.SelectNodes("//@*").Cast<XmlAttribute>().Concat(factionsXml.SelectNodes("//@*").Cast<XmlAttribute>())
            .Where(a => a.Value.Contains(".") && (a.Value.StartsWith("Hero.") || a.Value.StartsWith("Settlement.") || a.Value.StartsWith("Kingdom.") || a.Value.StartsWith("Culture."))).ToList();
        Check(refs.All(a => a.Value.StartsWith("Hero.") ? heroes.ContainsKey(a.Value.Substring(5)) : a.Value.StartsWith("Settlement.") ? (towns.ContainsKey(a.Value.Substring(11)) || castles.Contains(a.Value.Substring(11))) :
                            a.Value.StartsWith("Kingdom.") ? kingdomOf.ContainsKey(a.Value.Substring(8)) : templates.ContainsKey(a.Value.Substring(8))) && refs.Count >= 2 * (r.Kingdoms.Count + r.Clans.Count),
              "every Hero, Settlement, Kingdom and Culture reference in the Kingdoms and Factions XML resolves to a compiler record (a town or castle home), a kingdom intent or an installed culture (" + refs.Count + " references)");
        var policies = new HashSet<string>(templates.Values.SelectMany(t => L(O(t, "values"), "default_policies")));
        Check(kingdomsXml.SelectNodes("//policy/@id").Cast<XmlAttribute>().All(a => policies.Contains(a.Value)) &&
              kingdomsXml.SelectNodes("//Kingdom/@flag_mesh").Count == 0 && factionsXml.SelectNodes("//Faction[@is_noble='true']").Count == r.Clans.Count,
              "kingdom policies are installed culture defaults and only attributes the engine reads are emitted");
        Check(r.DeclaredKeys.Count == r.Kingdoms.Count + r.Clans.Count + r.MinorClans.Count && r.DeclaredKeys.All(k => CampaignIdentity.Problem(k.Substring(k.IndexOf('.') + 1), r.WorldPrefix) == null),
              "every kingdom, house and minor clan is a declared generated object with a valid world-qualified id (for the MMR-30 registry)");

        // Escaping: XML-escaped, braces to parentheses, control characters, U+FFFE and a lone surrogate dropped; a surrogate pair kept.
        var odd = Copy(campaign);
        var oddKingdom = Records(odd, "kingdoms").OrderBy(k => S(k, "id")).First();
        oddKingdom["name"] = "Crown <of> \"Salt\" & {Sea}\u0007￾\uD800 👑";
        var oddResult = buildCampaign(odd);
        var oddXml = oddResult.Documents.Single(d => d.Key == "Kingdoms").Value;
        var reparsed = new XmlDocument(); reparsed.LoadXml(oddXml.OuterXml);
        var title = ((XmlElement)reparsed.SelectSingleNode("//Kingdom[@id='" + S(oddKingdom, "id") + "']")).GetAttribute("title");
        bool strictWrite;
        using (var w = XmlWriter.Create(new StringWriter(), new XmlWriterSettings { CheckCharacters = true }))
            try { oddXml.Save(w); strictWrite = true; } catch (ArgumentException) { strictWrite = false; }
        Check(title == "{=" + S(oddKingdom, "id") + "_title}Crown <of> \"Salt\" & (Sea) 👑" && oddXml.OuterXml.Contains("&lt;of&gt;") && strictWrite &&
              Factory.Fill("{realm} of {civilization}", new Dictionary<string, string> { ["realm"] = "{civilization}", ["civilization"] = "X" }) == "{civilization} of X",
              "names are XML-escaped, TextObject braces become parentheses, control and non-XML characters and lone surrogates are dropped, the XML reparses and writes strictly, and substitution is one pass");

        // Membership and culture closure: anything that would leave a clan or culture invalid is refused, never repaired.
        var firstRuling = r.Clans.First(c => c.Ruling);
        var otherHouse = r.Clans.First(c => !c.Ruling && c.Members.Count > 1);
        var wanderer = heroes.Values.First(h => S(h, "occupation") == "Wanderer");
        var withWanderer = Copy(campaign);
        ((IDictionary<string, object>)Records(withWanderer, "houses").First(c => S(c, "id") == firstRuling.Id))["members"] = L(compilerClans[firstRuling.Id], "members").Concat(new[] { S(wanderer, "id") }).ToArray();
        var withoutMember = Copy(campaign);
        var dropped = otherHouse.Members.First(m => m != otherHouse.LeaderId);
        Records(withoutMember, "houses").First(c => S(c, "id") == otherHouse.Id)["members"] = L(compilerClans[otherHouse.Id], "members").Where(m => m != dropped).ToArray();
        var overridden = Copy(campaign);
        var overriddenKingdom = Records(overridden, "kingdoms").First(k => S(k, "id") == r.Kingdoms[0].Id);
        overriddenKingdom["presentation"] = templates.Keys.First(c => c != r.Kingdoms[0].Culture);
        var looters = Copy(campaign);
        Records(looters, "houses").First(c => S(c, "id") == firstRuling.Id)["presentation"] = "looters";
        var badHouse = Copy(campaign);
        Records(badHouse, "houses").First(c => S(c, "id") == otherHouse.Id)["presentation"] = "looters";
        string wandererRefusal = Refusal(() => buildCampaign(withWanderer)), missingRefusal = Refusal(() => buildCampaign(withoutMember)),
               overrideRefusal = Refusal(() => buildCampaign(overridden)), lootersRefusal = Refusal(() => buildCampaign(looters)), houseRefusal = Refusal(() => buildCampaign(badHouse));
        Check(wandererRefusal != null && wandererRefusal.Contains("not a lord") && missingRefusal != null && missingRefusal.Contains("is in 0 clans") &&
              overrideRefusal != null && overrideRefusal.Contains("is not its civilization's presentation") &&
              lootersRefusal != null && lootersRefusal.Contains("house " + firstRuling.Id + " culture looters") &&
              houseRefusal != null && houseRefusal.Contains("house " + otherHouse.Id + " culture looters"),
              "a wanderer in a house, a lord in no house, an overridden kingdom culture and an invalid ruling or other house culture are each refused");
        // House and kingdom names: the compiler makes them unique; a repeated one is refused, never renamed with an ordinal.
        var twins = Copy(campaign);
        Records(twins, "houses").First(c => S(c, "id") == otherHouse.Id)["name"] = S(compilerClans[firstRuling.Id], "name");
        string twinRefusal = Refusal(() => buildCampaign(twins));
        var twinKingdoms = Copy(campaign);
        var twinKingdomRecords = Records(twinKingdoms, "kingdoms").Where(k => ((IEnumerable)k["towns"]).Cast<object>().Any()).Take(2).ToList();
        string twinKingdomRefusal = "single kingdom";
        if (twinKingdomRecords.Count == 2) { twinKingdomRecords[1]["name"] = S(twinKingdomRecords[0], "name"); twinKingdomRefusal = Refusal(() => buildCampaign(twinKingdoms)); }
        Check(twinRefusal != null && twinRefusal.Contains("share the name") && twinKingdomRefusal != null && (twinKingdomRefusal == "single kingdom" || twinKingdomRefusal.Contains("share the name")),
              "two houses or two kingdoms with one name are refused (the compiler's name cleaner keeps them unique, with no ordinal)");
        // MMR-280 refusals: a minor without both parents, a company on a lord party template, an outlaw clan whose wars leave the war guard, a
        // repeated war and a kingdom above wars.maxPerKingdom.
        var orphan = Copy(campaign);
        var orphanMinor = Records(orphan, "heroes").First(h => minors.Contains(S(h, "id")));
        orphanMinor.Remove("mother");
        var lordTroops = Copy(campaign);
        var firstCompany = Records(lordTroops, "mercenaryCompanies").OrderBy(c => S(c, "id")).First();
        firstCompany["partyTemplate"] = lordParty.First();
        var outlawWars = Copy(campaign);
        var firstOutlaw = Records(outlawWars, "outlawClans").OrderBy(c => S(c, "id")).First();
        firstOutlaw["wars"] = r.Kingdoms.Select(k => (object)k.Id).ToArray();
        var twiceWar = Copy(campaign);
        twiceWar["wars"] = ((IEnumerable)twiceWar["wars"]).Cast<object>().Concat(((IEnumerable)twiceWar["wars"]).Cast<object>().Take(1)).ToArray();
        var manyWars = Copy(campaign);
        string hub = r.Kingdoms[0].Id;
        manyWars["wars"] = r.Kingdoms.Skip(1).Select(k => (object)new Dictionary<string, object> { ["a"] = hub, ["b"] = k.Id, ["partner"] = "test" }).ToArray();
        string orphanRefusal = Refusal(() => buildCampaign(orphan)), lordTroopRefusal = Refusal(() => buildCampaign(lordTroops)), outlawWarRefusal = Refusal(() => buildCampaign(outlawWars)),
               twiceRefusal = Refusal(() => buildCampaign(twiceWar)), manyRefusal = r.Kingdoms.Count - 1 > maxWars ? Refusal(() => buildCampaign(manyWars)) : "maxPerKingdom";
        Check(orphanRefusal != null && orphanRefusal.Contains("minor " + S(orphanMinor, "id")) && orphanRefusal.Contains("both parents") &&
              lordTroopRefusal != null && lordTroopRefusal.Contains("lord party template") && lordTroopRefusal.Contains("population.json troop line") &&
              outlawWarRefusal != null && outlawWarRefusal.Contains("not the kingdoms of its culture") &&
              twiceRefusal != null && twiceRefusal.Contains("listed twice") && manyRefusal != null && manyRefusal.Contains("maxPerKingdom"),
              "a minor without both parents, a company on a lord party template, an outlaw clan at war beyond its culture's kingdoms, a repeated war and a kingdom above wars.maxPerKingdom are each refused");

        // Policy robustness: a palette or icon pool that cannot give unique banners, and a source note without {source}, are refused.
        var onePalette = Copy(policy); O(onePalette, "banners")["palette"] = ((IEnumerable)banners["palette"]).Cast<object>().Take(1).ToArray();
        var twoPalette = Copy(policy); O(twoPalette, "banners")["palette"] = ((IEnumerable)banners["palette"]).Cast<object>().Take(2).ToArray();
        var noIcons = Copy(policy); O(O(noIcons, "cultures"), firstCulture)["bannerIcons"] = new object[0];
        var fewIcons = Copy(policy); O(O(fewIcons, "cultures"), firstCulture)["bannerIcons"] = L(O(cultures, firstCulture), "bannerIcons").Count > 0 ? ((IEnumerable)O(cultures, firstCulture)["bannerIcons"]).Cast<object>().Take(1).ToArray() : new object[0];
        var badNote = Copy(policy); O(badNote, "texts")["sourceNote"] = " Its old records name it.";
        string oneRefusal = Refusal(() => build(onePalette)), twoRefusal = Refusal(() => build(twoPalette)), noIconRefusal = Refusal(() => build(noIcons)),
               fewIconRefusal = Refusal(() => build(fewIcons)), noteRefusal = Refusal(() => build(badNote));
        Check(oneRefusal != null && oneRefusal.Contains("fewer than two colors") && twoRefusal != null && twoRefusal.Contains("no unique color pair") &&
              noIconRefusal != null && noIconRefusal.Contains("no complete culture template") && fewIconRefusal != null && fewIconRefusal.Contains("icons") &&
              noteRefusal != null && noteRefusal.Contains("texts.sourceNote"),
              "a one-colour palette, too few colour pairs, an empty or too small icon pool and a source note without {source} are refused, never looped on");

        // The zero-town realm policy.
        var empty = Copy(campaign);
        var emptied = Records(empty, "kingdoms").OrderBy(k => S(k, "id")).First();   // the first by id, so every later kingdom could be disturbed
        emptied["towns"] = new object[0];
        string emptiedId = S(emptied, "id");
        var dissolved = buildCampaign(empty);
        var refusePolicy = Copy(policy); O(refusePolicy, "zeroTownRealm")["action"] = "refuse";
        string zeroRefusal = Refusal(() => Factory.Build(empty, idMap, refusePolicy, presentations, templates, population));
        var realmLords = lords.Concat(dead).Where(l => S(heroes[l], "kingdom") == emptiedId).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var d0 = dissolved.Dissolved.FirstOrDefault();
        Check(dissolved.Dissolved.Count == 1 && d0.Kingdom == emptiedId && dissolved.Kingdoms.All(k => k.Id != emptiedId) && dissolved.Clans.All(c => c.KingdomId != emptiedId) &&
              d0.Lords.SequenceEqual(realmLords) && realmLords.Count > 0 && d0.Castles.SequenceEqual(L(emptied, "castles").OrderBy(x => x, StringComparer.Ordinal)) &&
              d0.Clans.Contains(S(emptied, "rulingHouse")) && realmLords.All(l => dissolved.Affiliations.Single(a => a.Hero == l).Clan == null) &&
              zeroRefusal != null && zeroRefusal.Contains("has no town") &&
              dissolved.Kingdoms.All(k => k.BannerKey == kingdomOf[k.Id].BannerKey) && dissolved.Clans.All(c => c.BannerKey == r.Clans.First(x => x.Id == c.Id).BannerKey),
              "a realm with no town is dissolved with all " + realmLords.Count + " of its lords unaffiliated and its castles and clan recorded for C03/C04 (or refused under the refuse policy); no other banner changes");
        var broken = Copy(campaign);
        Records(broken, "kingdoms").First()["ruler"] = "mnbl_ffffffff_h_0000000000";
        string brokenRefusal = Refusal(() => buildCampaign(broken));
        Check(brokenRefusal != null && brokenRefusal.Contains("is not a compiler hero"), "a kingdom whose ruler is not in the compiler graph is refused");

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.factions", ["version"] = 3, ["ticket"] = "MMR-31/MMR-280", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["kingdoms"] = r.Kingdoms.Count, ["regentRuled"] = r.Kingdoms.Count(k => S(heroes[k.RulerId], "role") == "regent"),
            ["renamedKingdoms"] = renamed.Select(k => new SortedDictionary<string, object> { ["id"] = k.Id, ["sourceName"] = k.SourceName, ["name"] = k.Name }).ToList(),
            ["rulingHouses"] = r.Clans.Count(c => c.Ruling), ["houses"] = r.Clans.Count, ["lords"] = lords.Count, ["affiliations"] = r.Affiliations.Count,
            ["minors"] = minors.Count, ["deadParents"] = dead.Count, ["companies"] = r.MinorClans.Count(c => c.Kind == "company"), ["outlawClans"] = r.MinorClans.Count(c => c.Kind == "outlaw"),
            ["outlawKinds"] = r.MinorClans.Where(c => c.Kind == "outlaw").GroupBy(c => c.OutlawKind).ToDictionary(g => g.Key, g => g.Count()),
            ["wars"] = r.Kingdoms.ToDictionary(k => k.Id, k => k.Wars),
            ["houseOrigins"] = Records(campaign, "houses").GroupBy(c => S(c, "origin")).ToDictionary(g => g.Key, g => g.Count()),
            ["tiers"] = r.Clans.GroupBy(c => c.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            ["cultures"] = r.Cultures.Select(c => new SortedDictionary<string, object> { ["uid"] = c.Uid, ["presentation"] = c.Presentation, ["kingdoms"] = c.Kingdoms.Count }).ToList(),
            ["banners"] = new SortedDictionary<string, object> { ["total"] = allBanners.Count, ["unique"] = allBanners.Distinct().Count() },
            ["diplomacy"] = new SortedDictionary<string, object> { ["pairs"] = r.Relations.Count, ["neutral"] = r.Relations.Count(v => v.Stance == "neutral"), ["war"] = r.Relations.Count(v => v.Stance == "war"),
                                                                    ["relationshipElements"] = kingdomsXml.SelectNodes("//relationship").Count,
                                                                    ["rulerRelationMin"] = r.Relations.Min(v => v.RulerRelation), ["rulerRelationMax"] = r.Relations.Max(v => v.RulerRelation) },
            ["documents"] = r.Documents.ToDictionary(d => d.Key, d => Sha(d.Value.OuterXml)),
            ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static string Sha(string text)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
}
