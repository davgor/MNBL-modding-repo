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

// MMR-31 faction tests, without the game: FactionTests BUNDLE_DIR REPO_DIR OUT_JSON
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
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var bundle = WorldDataBundle.Open(bundleDir, host);
        var campaign = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
        var idMap = Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/id-map.json")));
        var policy = Load(Path.Combine(repo, "worldgen/policies/factions.json"));
        var presentations = Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var contract = Load(Path.Combine(repo, "worldgen/templates/cultures/contract.json"));
        Func<IDictionary<string, object>, Factory.Result> build = p => Factory.Build(campaign, idMap, p, presentations, templates);
        Func<IDictionary<string, object>, Factory.Result> buildCampaign = c => Factory.Build(c, idMap, policy, presentations, templates);
        var r = build(policy);

        var heroes = Records(campaign, "heroes").ToDictionary(h => S(h, "id"));
        var towns = Records(campaign, "towns").ToDictionary(t => S(t, "id"));
        var compilerClans = Records(campaign, "clans").ToDictionary(c => S(c, "id"));
        var compilerKingdoms = Records(campaign, "kingdoms").ToList();
        var lords = heroes.Values.Where(h => S(h, "occupation") == "Lord").Select(h => S(h, "id")).ToList();
        var leaderRoles = new HashSet<string>(L(O(policy, "vassalClans"), "leaderRoles"));
        var kingdomOf = r.Kingdoms.ToDictionary(k => k.Id);

        // c1: every kingdom intent names a ruling clan, a leader and a capital that resolve in the compiler graph.
        Check(r.Kingdoms.Count == compilerKingdoms.Count && r.Kingdoms.All(k => heroes.ContainsKey(k.RulerId) && compilerClans.ContainsKey(k.RulingClanId) &&
              towns.ContainsKey(k.CapitalId) && k.Towns.Contains(k.CapitalId) && S(compilerClans[k.RulingClanId], "leader") == k.RulerId),
              "every one of " + r.Kingdoms.Count + " kingdom intents names a compiler ruler leading its compiler ruling clan and a capital among its towns");
        Check(r.Kingdoms.All(k => k.RulerId != null && S(heroes[k.RulerId], "occupation") == "Lord") && r.Kingdoms.Count(k => S(heroes[k.RulerId], "role") == "regent") == 8,
              "no kingdom has a null ruler: 8 realms without a sovereign are ruled by MMR-27's synthesized regents");
        var clanOf = r.Clans.SelectMany(c => c.Members.Select(m => new KeyValuePair<string, string>(m, c.Id))).ToList();
        Check(lords.All(l => clanOf.Count(x => x.Key == l) == 1) && clanOf.Count == lords.Count &&
              r.Clans.All(c => c.Members.All(m => S(heroes[m], "occupation") == "Lord" && S(heroes[m], "kingdom") == c.KingdomId)),
              "every one of " + lords.Count + " lords is a member of exactly one clan, and every member is a lord of the clan's kingdom");
        var vassals = r.Clans.Where(c => !c.Ruling).ToList();
        int expectedVassals = lords.Count(l => leaderRoles.Contains(S(heroes[l], "role")) && !r.Kingdoms.Any(k => k.RulerId == l));
        Check(vassals.Count == expectedVassals && vassals.All(v => v.Members.SequenceEqual(new[] { v.LeaderId }) && leaderRoles.Contains(S(heroes[v.LeaderId], "role")) &&
              S(heroes[v.LeaderId], "kingdom") == v.KingdomId && v.Uid == "clan:" + S(heroes[v.LeaderId], "uid")),
              "each of " + vassals.Count + " castellans, pretenders and founders leads one vassal clan of its own kingdom");
        // The engine takes a kingdom's ruling clan from its ruler's Heroes.xml faction (Kingdom.Deserialize: RulingClan = owner.Clan), and
        // Clan.Deserialize moves a leader into the clan it leads; the affiliations are what C03 writes, matching the clans exactly.
        var affiliation = r.Affiliations.ToDictionary(a => a.Hero);
        Check(r.Affiliations.Count == lords.Count && clanOf.All(x => affiliation[x.Key].Clan == x.Value && affiliation[x.Key].Kingdom == r.Clans.First(c => c.Id == x.Value).KingdomId) &&
              r.Kingdoms.All(k => affiliation[k.RulerId].Clan == k.RulingClanId) && vassals.All(v => affiliation[v.LeaderId].Clan == v.Id) &&
              vassals.Count(v => S(heroes[v.LeaderId], "clan") != v.Id) == vassals.Count,
              "the lord-to-clan affiliations for C03 match the clans: every ruler in its ruling clan and every vassal leader in its own clan (the compiler's hero.clan still names the ruling clan for all " + vassals.Count + ")");
        // Stable ids: the MMR-27 formula reproduces every ruling clan id, and vassal ids collide with nothing assigned.
        var assigned = new HashSet<string>(((IEnumerable)idMap["entries"]).Cast<IDictionary<string, object>>().Select(e => S(e, "id")));
        Func<string, string> stable = uid => r.WorldPrefix + "l_" + Sha("mnbl.stable-id/1\nclan\n" + uid).Substring(0, 10);
        Check(r.Clans.Where(c => c.Ruling).All(c => stable(c.Uid) == c.Id) && vassals.All(v => stable(v.Uid) == v.Id && !assigned.Contains(v.Id)) &&
              r.Clans.Select(c => c.Id).Distinct().Count() == r.Clans.Count, "clan ids follow the MMR-27 stable-id formula (reproducing every compiler ruling clan id) and vassal ids are new");
        var renamed = r.Kingdoms.Where(k => k.Name != k.SourceName).ToList();
        var duplicateSources = r.Kingdoms.GroupBy(k => k.SourceName).Where(g => g.Count() > 1).ToList();
        Check(r.Kingdoms.Select(k => k.Name).Distinct().Count() == r.Kingdoms.Count && r.Clans.Select(c => c.Name).Distinct().Count() == r.Clans.Count &&
              duplicateSources.Count == 2 && duplicateSources.All(g => g.OrderBy(k => k.Uid, StringComparer.Ordinal).First().Name == g.Key &&
                                                                       g.OrderBy(k => k.Uid, StringComparer.Ordinal).Skip(1).All(k => k.Name == g.Key + " 2")) && renamed.Count == 2 &&
              r.Kingdoms.All(k => k.NameLocalized == "{=" + k.Id + "}" + k.Name.Replace('{', '(').Replace('}', ')')),
              "kingdom and clan names are unique: the 2 repeated realm names take the ordinal (" + String.Join(", ", renamed.Select(k => k.Name)) + "), the source name stays in the intent");
        var tiers = O(O(policy, "vassalClans"), "tierByRole");
        Check(r.Clans.Where(c => c.Ruling).All(c => c.Tier >= 4) && vassals.All(v => v.Tier == Convert.ToInt32(tiers[S(heroes[v.LeaderId], "role")])),
              "ruling clans take their tier from the realm's fiefs and vassal clans from their founder's role");

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
        string incompleteRefusal = Refusal(() => Factory.Build(campaign, idMap, policy, presentations, incomplete));
        Check(incompleteRefusal != null && incompleteRefusal.Contains("no complete culture template"), "a presentation whose template lacks a required contract member is refused");

        // c3: the same input reproduces factions, banners and relationships.
        var again = build(policy);
        Check(again.IntentsSha256 == r.IntentsSha256 && again.Documents.Select(d => d.Value.OuterXml).SequenceEqual(r.Documents.Select(d => d.Value.OuterXml)),
              "building twice gives the same intents (" + r.IntentsSha256.Substring(0, 12) + ") and the same XML");
        var shuffled = Copy(campaign);
        foreach (var key in new[] { "heroes", "kingdoms", "clans", "towns", "castles" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(buildCampaign(shuffled).IntentsSha256 == r.IntentsSha256, "reordering the compiler records changes nothing");
        // Independent streams: re-versioning one stream changes only what it draws.
        Func<string, int, Factory.Result> reversion = (stream, version) => { var p = Copy(policy); O(p, "streams")[stream] = version; return build(p); };
        var banner2 = reversion("banner", 2); var colors2 = reversion("colors", 2); var relations2 = reversion("relations", 2);
        Func<Factory.Result, string> ownership = x => String.Join("|", x.Clans.Select(c => c.Id + ":" + c.LeaderId + ":" + c.KingdomId + ":" + c.Tier + ":" + String.Join(",", c.Members)));
        Func<Factory.Result, string> relations = x => String.Join("|", x.Relations.Select(v => v.A + v.B + v.RulerRelation));
        Func<Factory.Result, string> colors = x => String.Join("|", x.Kingdoms.Select(k => k.Color + k.Color2));
        Func<Factory.Result, string> icons = x => String.Join("|", x.Clans.Select(c => c.BannerKey.Split('.')[10]).Concat(x.Kingdoms.Select(k => k.BannerKey.Split('.')[10])));
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
        var allBanners = r.Kingdoms.Select(k => k.BannerKey).Concat(r.Clans.Select(c => c.BannerKey)).ToList();
        Check(r.Kingdoms.All(k => validBanner(k.BannerKey, k.Culture)) && r.Kingdoms.Select(k => k.Color + "." + k.Color2).Distinct().Count() == r.Kingdoms.Count &&
              allBanners.Distinct().Count() == allBanners.Count,
              "kingdom banners are two-layer keys of the installed background, palette and culture icons; colour pairs are unique among kingdoms and all " + allBanners.Count + " banners are unique in the world");
        Check(r.Clans.All(c => { var k = kingdomOf[c.KingdomId]; var n = c.BannerKey.Split('.'); var kn = k.BannerKey.Split('.');
                                 return validBanner(c.BannerKey, k.Culture) && n[1] == kn[1] && n[11] == kn[11] && n[10] != kn[10]; }),
              "clan banners carry their kingdom's colors with an icon of their own, never the kingdom's");
        int kc = r.Kingdoms.Count;
        var kingdomsXml = r.Documents.Single(d => d.Key == "Kingdoms").Value; var factionsXml = r.Documents.Single(d => d.Key == "Factions").Value;
        Check(r.Relations.Count == kc * (kc - 1) / 2 && r.Relations.All(v => v.Stance == "neutral" && v.RulerRelation >= -20 && v.RulerRelation <= 20 &&
              v.RulerA == kingdomOf[v.A].RulerId && v.RulerB == kingdomOf[v.B].RulerId) && r.Relations.Select(v => v.RulerRelation).Distinct().Count() > 5 &&
              kingdomsXml.SelectNodes("//relationships").Count == 0 && kingdomsXml.SelectNodes("//relationship").Count == 0,
              "every kingdom pair starts neutral with no relationship written (Kingdom.Deserialize declares war on any negative value), and the rulers' relation within the policy spread is an intent for C03");
        var refs = kingdomsXml.SelectNodes("//@*").Cast<XmlAttribute>().Concat(factionsXml.SelectNodes("//@*").Cast<XmlAttribute>())
            .Where(a => a.Value.Contains(".") && (a.Value.StartsWith("Hero.") || a.Value.StartsWith("Settlement.") || a.Value.StartsWith("Kingdom.") || a.Value.StartsWith("Culture."))).ToList();
        Check(refs.All(a => a.Value.StartsWith("Hero.") ? heroes.ContainsKey(a.Value.Substring(5)) : a.Value.StartsWith("Settlement.") ? towns.ContainsKey(a.Value.Substring(11)) :
                            a.Value.StartsWith("Kingdom.") ? kingdomOf.ContainsKey(a.Value.Substring(8)) : templates.ContainsKey(a.Value.Substring(8))) && refs.Count > 200,
              "every Hero, Settlement, Kingdom and Culture reference in the Kingdoms and Factions XML resolves to a compiler record, a kingdom intent or an installed culture (" + refs.Count + " references)");
        var policies = new HashSet<string>(templates.Values.SelectMany(t => L(O(t, "values"), "default_policies")));
        Check(kingdomsXml.SelectNodes("//policy/@id").Cast<XmlAttribute>().All(a => policies.Contains(a.Value)) &&
              kingdomsXml.SelectNodes("//Kingdom/@flag_mesh").Count == 0 && factionsXml.SelectNodes("//Faction[@is_noble='true']").Count == r.Clans.Count,
              "kingdom policies are installed culture defaults and only attributes the engine reads are emitted");
        Check(r.DeclaredKeys.Count == r.Kingdoms.Count + r.Clans.Count && r.DeclaredKeys.All(k => CampaignIdentity.Problem(k.Substring(k.IndexOf('.') + 1), r.WorldPrefix) == null),
              "every kingdom and clan is a declared generated object with a valid world-qualified id (for the MMR-30 registry)");

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
        var wanderer = heroes.Values.First(h => S(h, "occupation") == "Wanderer");
        var withWanderer = Copy(campaign);
        ((IDictionary<string, object>)Records(withWanderer, "clans").First(c => S(c, "id") == firstRuling.Id))["members"] = L(compilerClans[firstRuling.Id], "members").Concat(new[] { S(wanderer, "id") }).ToArray();
        var withoutMember = Copy(campaign);
        // A vassal founder dropped from the compiler's ruling clan members (every lord besides the 40 rulers founds a vassal clan here).
        var dropped = vassals[0].LeaderId; var droppedFrom = kingdomOf[vassals[0].KingdomId].RulingClanId;
        Records(withoutMember, "clans").First(c => S(c, "id") == droppedFrom)["members"] = L(compilerClans[droppedFrom], "members").Where(m => m != dropped).ToArray();
        var overridden = Copy(campaign);
        var overriddenKingdom = Records(overridden, "kingdoms").First(k => S(k, "id") == r.Kingdoms[0].Id);
        overriddenKingdom["presentation"] = templates.Keys.First(c => c != r.Kingdoms[0].Culture);
        var looters = Copy(campaign);
        Records(looters, "clans").First(c => S(c, "id") == firstRuling.Id)["presentation"] = "looters";
        var badLeader = Copy(campaign);
        Records(badLeader, "heroes").First(h => S(h, "id") == vassals[0].LeaderId)["presentation"] = "looters";
        string wandererRefusal = Refusal(() => buildCampaign(withWanderer)), missingRefusal = Refusal(() => buildCampaign(withoutMember)),
               overrideRefusal = Refusal(() => buildCampaign(overridden)), lootersRefusal = Refusal(() => buildCampaign(looters)), leaderRefusal = Refusal(() => buildCampaign(badLeader));
        Check(wandererRefusal != null && wandererRefusal.Contains("not a lord") && missingRefusal != null && missingRefusal.Contains("is in 0 clans") &&
              overrideRefusal != null && overrideRefusal.Contains("is not its civilization's presentation") &&
              lootersRefusal != null && lootersRefusal.Contains("ruling clan " + firstRuling.Id + " culture looters") &&
              leaderRefusal != null && leaderRefusal.Contains("leader " + vassals[0].LeaderId + " culture looters"),
              "a wanderer in a clan, a lord in no clan, an overridden kingdom culture and an invalid ruling or vassal clan culture are each refused");
        // Clan names: two "Ann" founders and one "Ann 2" give three distinct names.
        var anns = Copy(campaign);
        var annLeaders = vassals.Take(3).Select(v => v.LeaderId).ToList();
        var annNames = new[] { "Ann", "Ann", "Ann 2" };
        for (int i = 0; i < 3; i++) Records(anns, "heroes").First(h => S(h, "id") == annLeaders[i])["name"] = annNames[i];
        var annResult = buildCampaign(anns);
        var annClans = annResult.Clans.Where(c => annLeaders.Contains(c.LeaderId)).Select(c => c.Name).ToList();
        Check(annClans.Distinct().Count() == 3 && annResult.Clans.Select(c => c.Name).Distinct().Count() == annResult.Clans.Count,
              "clan-name ordinals never create a new duplicate (" + String.Join(", ", annClans) + ")");
        // Policy robustness: a palette or icon pool that cannot give unique banners, and an ordinal without {n}, are refused.
        var onePalette = Copy(policy); O(onePalette, "banners")["palette"] = ((IEnumerable)banners["palette"]).Cast<object>().Take(1).ToArray();
        var twoPalette = Copy(policy); O(twoPalette, "banners")["palette"] = ((IEnumerable)banners["palette"]).Cast<object>().Take(2).ToArray();
        var noIcons = Copy(policy); O(O(noIcons, "cultures"), firstCulture)["bannerIcons"] = new object[0];
        var fewIcons = Copy(policy); O(O(fewIcons, "cultures"), firstCulture)["bannerIcons"] = L(O(cultures, firstCulture), "bannerIcons").Count > 0 ? ((IEnumerable)O(cultures, firstCulture)["bannerIcons"]).Cast<object>().Take(1).ToArray() : new object[0];
        var badOrdinal = Copy(policy); O(badOrdinal, "names")["ordinal"] = "{name} again";
        string oneRefusal = Refusal(() => build(onePalette)), twoRefusal = Refusal(() => build(twoPalette)), noIconRefusal = Refusal(() => build(noIcons)),
               fewIconRefusal = Refusal(() => build(fewIcons)), ordinalRefusal = Refusal(() => build(badOrdinal));
        Check(oneRefusal != null && oneRefusal.Contains("fewer than two colors") && twoRefusal != null && twoRefusal.Contains("no unique color pair") &&
              noIconRefusal != null && noIconRefusal.Contains("no complete culture template") && fewIconRefusal != null && fewIconRefusal.Contains("icons") &&
              ordinalRefusal != null && ordinalRefusal.Contains("names.ordinal"),
              "a one-colour palette, too few colour pairs, an empty or too small icon pool and an ordinal without {n} are refused, never looped on");

        // The zero-town realm policy.
        var empty = Copy(campaign);
        var emptied = Records(empty, "kingdoms").OrderBy(k => S(k, "id")).First();   // the first by id, so every later kingdom could be disturbed
        emptied["towns"] = new object[0];
        string emptiedId = S(emptied, "id");
        var dissolved = buildCampaign(empty);
        var refusePolicy = Copy(policy); O(refusePolicy, "zeroTownRealm")["action"] = "refuse";
        string zeroRefusal = Refusal(() => Factory.Build(empty, idMap, refusePolicy, presentations, templates));
        var realmLords = lords.Where(l => S(heroes[l], "kingdom") == emptiedId).OrderBy(l => l, StringComparer.Ordinal).ToList();
        var d0 = dissolved.Dissolved.FirstOrDefault();
        Check(dissolved.Dissolved.Count == 1 && d0.Kingdom == emptiedId && dissolved.Kingdoms.All(k => k.Id != emptiedId) && dissolved.Clans.All(c => c.KingdomId != emptiedId) &&
              d0.Lords.SequenceEqual(realmLords) && realmLords.Count > 0 && d0.Castles.SequenceEqual(L(emptied, "castles").OrderBy(x => x, StringComparer.Ordinal)) &&
              d0.Clans.Contains(S(emptied, "rulingClan")) && realmLords.All(l => dissolved.Affiliations.Single(a => a.Hero == l).Clan == null) &&
              zeroRefusal != null && zeroRefusal.Contains("has no town") &&
              dissolved.Kingdoms.All(k => k.BannerKey == kingdomOf[k.Id].BannerKey) && dissolved.Clans.All(c => c.BannerKey == r.Clans.First(x => x.Id == c.Id).BannerKey),
              "a realm with no town is dissolved with all " + realmLords.Count + " of its lords unaffiliated and its castles and clan recorded for C03/C04 (or refused under the refuse policy); no other banner changes");
        var broken = Copy(campaign);
        Records(broken, "kingdoms").First()["ruler"] = "mnbl_ffffffff_h_0000000000";
        string brokenRefusal = Refusal(() => buildCampaign(broken));
        Check(brokenRefusal != null && brokenRefusal.Contains("is not a compiler hero"), "a kingdom whose ruler is not in the compiler graph is refused");

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.factions", ["version"] = 2, ["ticket"] = "MMR-31", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["kingdoms"] = r.Kingdoms.Count, ["regentRuled"] = r.Kingdoms.Count(k => S(heroes[k.RulerId], "role") == "regent"),
            ["renamedKingdoms"] = renamed.Select(k => new SortedDictionary<string, object> { ["id"] = k.Id, ["sourceName"] = k.SourceName, ["name"] = k.Name }).ToList(),
            ["rulingClans"] = r.Clans.Count(c => c.Ruling), ["vassalClans"] = vassals.Count, ["lords"] = lords.Count, ["affiliations"] = r.Affiliations.Count,
            ["vassalFounders"] = vassals.GroupBy(v => S(heroes[v.LeaderId], "role")).ToDictionary(g => g.Key, g => g.Count()),
            ["tiers"] = r.Clans.GroupBy(c => c.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            ["cultures"] = r.Cultures.Select(c => new SortedDictionary<string, object> { ["uid"] = c.Uid, ["presentation"] = c.Presentation, ["kingdoms"] = c.Kingdoms.Count }).ToList(),
            ["banners"] = new SortedDictionary<string, object> { ["total"] = allBanners.Count, ["unique"] = allBanners.Distinct().Count() },
            ["diplomacy"] = new SortedDictionary<string, object> { ["pairs"] = r.Relations.Count, ["neutral"] = r.Relations.Count(v => v.Stance == "neutral"),
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
