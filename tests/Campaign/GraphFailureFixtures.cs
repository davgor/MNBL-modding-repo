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

// MMR-36 graph validation tests, without the game: GraphFailureFixtures BUNDLE_DIR REPO_DIR OUT_JSON
// The real size-129 world goes through every accepted factory (MMR-31 factions, MMR-32 heroes, MMR-33 settlements, MMR-34 startup, MMR-35
// naval); CampaignGraphValidator must pass it with an exact census recomputed here from the factories' results and the compiler records.
// Then each fault class is injected into a fresh copy of the staged graph and must be reported with its code (and nothing else), and all of
// them at once must be reported in one pass. The two in-game fixture worlds of the standard run (the first with the settlement, startup and
// naval fixtures) are built as GeneratedCampaignRegistration builds them and validated as its gate does, and the in-game fault classes
// (GraphFaults) are refused offline too.
internal static class GraphFailureFixtures
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

    private sealed class Fault
    {
        public string Name; public string[] Codes; public Action<CampaignGraphValidator.Input> Apply;
        public Func<CampaignGraphValidator.Report, bool> Detail;   // what the diagnostics must also show (where they point)
        public Fault(string name, Action<CampaignGraphValidator.Input> apply, params string[] codes) { Name = name; Apply = apply; Codes = codes; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: GraphFailureFixtures BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
        try { Run(args[0], args[1], args[2]); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static XmlDocument Doc(CampaignGraphValidator.Input input, string name, Func<XmlDocument, bool> where = null)
    {
        return input.Documents.Where(d => d.Key == name).Select(d => d.Value).First(d => where == null || where(d));
    }
    private static XmlElement Node(CampaignGraphValidator.Input input, string document, string xpath)
    {
        foreach (var d in input.Documents.Where(x => x.Key == document)) { var e = d.Value.DocumentElement.SelectSingleNode(xpath) as XmlElement; if (e != null) return e; }
        throw new InvalidOperationException("no " + xpath + " in " + document);
    }
    private static XmlElement Import(CampaignGraphValidator.Input input, string document, XmlElement source)
    {
        var doc = Doc(input, document); var e = (XmlElement)doc.ImportNode(source, true); doc.DocumentElement.AppendChild(e); return e;
    }

    private static void Run(string bundleDir, string repo, string outJson)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        var bundle = WorldDataBundle.Open(bundleDir, host);
        Func<string, IDictionary<string, object>> read = path => Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified(path)));
        var campaign = read("compiled/campaign/campaign.json"); var idMap = read("compiled/campaign/id-map.json");
        var travel = read("compiled/travel/travel-graph.json"); var ports = read("compiled/travel/ports.json");
        var cells = bundle.ReadVerified("compiled/travel/cells.bin");
        var graphPolicy = Load(Path.Combine(repo, "worldgen/policies/graph-validation.json"));
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var economy = Load(Path.Combine(repo, "worldgen/policies/economy.json"));
        var startup = Load(Path.Combine(repo, "worldgen/policies/startup.json"));
        var naval = Load(Path.Combine(repo, "worldgen/policies/naval-content.json"));
        var prototypes = Load(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var receipt = Load(Path.Combine(repo, S(O(rosters, "provenance"), "catalogReceipt")));
        var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in Records(O(receipt, "outputs"), "documents"))
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256")) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its receipt");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        var defaultsRun = Load(Path.Combine(repo, S(O(rosters, "provenance"), "defaultReferences")));
        var defaults = L(O(Records(O(defaultsRun, "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(rosters, "troops"));
        var factions = FactionFactory.Build(campaign, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates);
        var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, resolver);
        var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, MNBL.ProceduralWorld.Missions.MissionScenes.PolicyPath)));
        var factory = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists, missionScenes);
        var world = factory.Build(campaign, travel, ports, factions, heroes, NavalContentFactory.ShipyardLevels(naval, ports));
        var start = new GeneratedCampaignInitializer(startup, factory, catalog["Settlements"], catalog["Factions"], catalog["SPCultures"]).Build(travel, ports, cells, factions, heroes, world);
        var navalResult = new NavalContentFactory(naval, catalog["Factions"], catalog["SPCultures"], catalog["ShipHulls"], catalog["MissionShips"], catalog["ShipSlots"], catalog["partyTemplates"])
            .Build(campaign, travel, ports, cells, factions, world, start);
        var documents = factions.Documents.Concat(heroes.Documents).Concat(world.Documents).Concat(start.Documents).Concat(navalResult.Documents).ToList();
        var declared = factions.DeclaredKeys.Concat(heroes.DeclaredKeys).Concat(world.DeclaredKeys).Concat(start.DeclaredKeys).Concat(navalResult.DeclaredKeys).ToList();
        var policies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in L(graphPolicy, "policies")) policies[p] = Sha(File.ReadAllBytes(Path.Combine(repo, p)));
        policies[CampaignGraphValidator.PolicyPath] = Sha(File.ReadAllBytes(Path.Combine(repo, CampaignGraphValidator.PolicyPath)));
        policies[CampaignGraphValidator.CatalogReceiptKey] = Sha(File.ReadAllBytes(Path.Combine(repo, S(O(rosters, "provenance"), "catalogReceipt"))));
        policies[CampaignGraphValidator.LiveDefaultsKey] = CampaignGraphValidator.InventorySha256(defaults);
        // The world's spawn positions: the startup's spawn anchors and player starts, and the pirate patrol zones.
        var spawnPoints = start.Anchors.Select(a => new CampaignGraphValidator.SpawnPoint { Label = a.Kind + " anchor " + a.Subject, X = a.X, Y = a.Y })
            .Concat(start.Starts.Select(s => new CampaignGraphValidator.SpawnPoint { Label = "start of " + s.Culture, X = s.X, Y = s.Y }))
            .Concat(navalResult.Pirates.SelectMany(p => p.SpawnPoints).Select(p => new CampaignGraphValidator.SpawnPoint { Label = "pirate zone " + p.Uid, X = p.X, Y = p.Y })).ToList();
        var resolutions = CampaignGraphValidator.Resolutions(factions, heroes, world, navalResult);
        var dispositions = CampaignGraphValidator.Dispositions(factions, heroes);
        Func<CampaignGraphValidator.Input> fresh = () => {
            var input = new CampaignGraphValidator.Input { WorldPrefix = factions.WorldPrefix, Campaign = campaign, IdMap = idMap, Plan = start.Plan };
            foreach (var d in documents) input.Documents.Add(new KeyValuePair<string, XmlDocument>(d.Key, (XmlDocument)d.Value.CloneNode(true)));
            input.Generated.UnionWith(declared); input.Aliases.UnionWith(CampaignIdentity.Aliases);
            foreach (var c in catalog) input.Catalog[c.Key] = c.Value;
            input.Defaults.UnionWith(defaults);
            input.Resolutions.AddRange(resolutions); input.Dispositions.AddRange(dispositions);
            foreach (var p in policies) input.Policies[p.Key] = p.Value;
            input.SpawnPoints.AddRange(spawnPoints);
            foreach (var r in world.Rebound) input.Rebound[r.Key] = r.Value;
            foreach (var r in world.ReboundReason) input.ReboundReason[r.Key] = r.Value;
            input.MissionScenes = missionScenes;
            return input;
        };

        // c2: the complete world validates, with an exact census.
        var clean = CampaignGraphValidator.Validate(graphPolicy, fresh());
        Check(clean.Ok, "the complete size-129 world (" + documents.Count + " documents from the five factories) validates: " + clean.Summary(40));
        var census = clean.Census;
        Func<string, SortedDictionary<string, int>> counts = key => (SortedDictionary<string, int>)census[key];
        var towns = world.Settlements.Count(s => s.Kind == "town"); var castles = world.Settlements.Count(s => s.Kind == "castle"); var villages = world.Settlements.Count(s => s.Kind == "village");
        var expectedGenerated = new SortedDictionary<string, int>(StringComparer.Ordinal) {
            ["Faction"] = factions.Clans.Count, ["Kingdom"] = factions.Kingdoms.Count, ["Hero"] = heroes.XmlHeroes.Count(), ["NPCCharacter"] = heroes.XmlHeroes.Count(),
            ["Settlement"] = world.Settlements.Count + start.Hideouts.Count, ["Town"] = towns + castles, ["Village"] = villages, ["Hideout"] = start.Hideouts.Count };
        var expectedAliases = new SortedDictionary<string, int>(StringComparer.Ordinal) {
            ["Faction"] = start.BanditHomes.Count + navalResult.Pirates.Count, ["Settlement"] = 1, ["RetirementSettlementComponent"] = 1 };
        var expectedReemitted = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["Culture"] = L(O(startup, "starts"), "creationCultures").Count };
        Func<SortedDictionary<string, int>, string> show = d => String.Join(", ", d.Select(x => x.Key + " " + x.Value));
        Check(counts("generated").SequenceEqual(expectedGenerated) && counts("aliases").SequenceEqual(expectedAliases) && counts("reemittedTemplates").SequenceEqual(expectedReemitted),
              "the census is exactly the factories' objects: generated " + show(counts("generated")) + "; world aliases " + show(counts("aliases")) + " (land bandit and pirate clans, the retirement retreat); re-emitted installed templates " + show(counts("reemittedTemplates")) +
              " (the creation cultures with generated start points); expected generated " + show(expectedGenerated));
        // The reached set, recomputed independently of ReferenceSchema: every "Type.id" attribute value of a staged generated or alias record
        // that names an installed record (not staged) must be reached; every reached key is an installed record or a verified live default,
        // none a Calradia world instance; the census counts are the reached set's.
        var installedKeys = new HashSet<string>(catalog.Values.SelectMany(d => d.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("id"))
            .Select(e => (e.Name == "MBPartyTemplate" ? "PartyTemplate" : e.Name == "CraftedItem" ? "Item" : e.Name) + "." + e.GetAttribute("id"))), StringComparer.Ordinal);
        var stagedKeys = new HashSet<string>(documents.SelectMany(d => d.Value.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => (e.Name == "MBPartyTemplate" ? "PartyTemplate" : e.Name) + "." + e.GetAttribute("id"))), StringComparer.Ordinal);
        var directRefs = documents.SelectMany(d => d.Value.SelectNodes("//@*").OfType<XmlAttribute>()).Select(a => a.Value)
            .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Z][A-Za-z]*\.[A-Za-z0-9_-]+$") && installedKeys.Contains(v) && !stagedKeys.Contains(v)).Distinct().ToList();
        var reachedSet = new HashSet<string>(clean.Reached, StringComparer.Ordinal);
        var worldTypeList = L(graphPolicy, "worldTypes");
        var installedHeroes = new HashSet<string>(catalog["Heroes"].DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => "NPCCharacter." + e.GetAttribute("id")), StringComparer.Ordinal);
        Func<SortedDictionary<string, int>, int> total = d => d.Values.Sum();
        Check(directRefs.Count > 0 && directRefs.All(reachedSet.Contains) && clean.Reached.All(k => installedKeys.Contains(k) || defaults.Contains(k)) &&
              clean.Reached.All(k => !worldTypeList.Contains(k.Substring(0, k.IndexOf('.'))) && !installedHeroes.Contains(k)) &&
              total(counts("templatesReferenced")) == clean.Reached.Count(installedKeys.Contains) && total(counts("defaultsReferenced")) == clean.Reached.Count(k => !installedKeys.Contains(k)),
              "the reference closure reaches " + clean.Reached.Count + " installed templates and live defaults, including all " + directRefs.Count + " installed records the staged records name directly (recomputed without the validator), none a Calradia world instance or hero character: templates " +
              show(counts("templatesReferenced")) + "; defaults " + show(counts("defaultsReferenced")) + "; " + clean.OptionalAbsent.Count + " optional references the installed loaders tolerate are noted; " + census["upgradeTreesChecked"] + " troop upgrade trees checked");
        // Sources: every compiler UID, with its outcome recomputed from the compiler records and the factories.
        var sources = counts("sources");
        var exclusions = Records(campaign, "exclusions").ToList();
        var compilerHeroes = new HashSet<string>(Records(campaign, "heroes").Select(h => S(h, "id")), StringComparer.Ordinal);
        var compilerClans = new HashSet<string>(Records(campaign, "clans").Select(c => S(c, "id")), StringComparer.Ordinal);
        var runtimeIds = new HashSet<string>(start.Plan.Notables.Concat(start.Plan.Companions).Select(h => h.Id), StringComparer.Ordinal);
        var expectedSources = new SortedDictionary<string, int>(StringComparer.Ordinal) {
            ["town staged"] = towns, ["castle staged"] = castles, ["village staged"] = villages, ["clan staged"] = factions.Clans.Count(c => compilerClans.Contains(c.Id)), ["kingdom staged"] = factions.Kingdoms.Count,
            ["hero staged"] = heroes.XmlHeroes.Count(h => compilerHeroes.Contains(h.Id)), ["hero runtime intent"] = compilerHeroes.Count(runtimeIds.Contains) };
        foreach (var e in exclusions) { string k = S(e, "kind") + " excluded"; int n; expectedSources.TryGetValue(k, out n); expectedSources[k] = n + 1; }
        foreach (var g in heroes.Dispositions.Where(d => d.Outcome != "instantiated").GroupBy(d => d.Outcome)) expectedSources["hero " + g.Key] = g.Count();
        if (factions.Dissolved.Count > 0) { expectedSources["kingdom dissolved"] = factions.Dissolved.Count; int dc = factions.Dissolved.Sum(d => d.Clans.Count); if (dc > 0) expectedSources["clan dissolved"] = dc; }
        var allUids = Records(idMap, "entries").Select(e => S(e, "uid")).Concat(L(campaign, "notInstantiated")).Distinct().ToList();
        Check(sources.SequenceEqual(expectedSources) && clean.Outcomes.Count == allUids.Count && allUids.All(u => clean.Outcomes.ContainsKey(u) && clean.Outcomes[u] != "MISSING"),
              "every one of the " + allUids.Count + " compiler source UIDs has a generated target or an explicit disposition: " + show(sources) + (sources.SequenceEqual(expectedSources) ? "" : " (expected " + show(expectedSources) + ")") +
              "; besides them the factories synthesize " + (factions.Clans.Count - expectedSources["clan staged"]) + " vassal clans (MMR-31), " + (heroes.XmlHeroes.Count() - expectedSources["hero staged"]) + " XML heroes and " +
              (runtimeIds.Count - expectedSources["hero runtime intent"]) + " runtime hero intents (MMR-32), counted in the census");
        var slots = counts("slots");
        int scenes = Records(campaign, "towns").Count() + Records(campaign, "castles").Count();
        int moot = slots.Where(s => s.Key.EndsWith(" moot", StringComparison.Ordinal)).Sum(s => s.Value);
        int boundedSlots = 2 * heroes.Dispositions.Count(d => d.Outcome == "bounded" && d.Id != null && compilerHeroes.Contains(d.Id));
        int sceneSlots = slots.Where(s => s.Key.EndsWith(" prototypeAndScenes resolved", StringComparison.Ordinal)).Sum(s => s.Value);
        Check(slots.Where(s => s.Key.EndsWith(" PENDING")).Count() == 0 && clean.DeferredSlots.Count == 0 && sceneSlots == scenes &&
              moot == boundedSlots && slots.Where(s => s.Key.Contains(" resolved")).Sum(s => s.Value) + moot == slots.Values.Sum() && (int)census["villagesRebound"] == world.Rebound.Count,
              "every compiler slot of a delivered owner (MMR-27/28/31/32/33/35/42) is resolved and, where the staged graph can show it, shown by it (" + slots.Where(s => s.Key.Contains(" resolved")).Sum(s => s.Value) + " slots, " +
              "the " + sceneSlots + " MMR-42 prototypeAndScenes slots among them: each staged town's and castle's locations meet the mission scene contracts); the " + moot +
              " slots of the bounded heroes are moot (their records have dispositions); nothing is deferred; " + world.Rebound.Count + " villages follow MMR-33's declared rebindings");
        // MMR-42: a staged town whose center scene is not installed fails its claimed prototypeAndScenes slot (and only that).
        {
            var tampered = fresh();
            var town = Records(campaign, "towns").Select(t => S(t, "id")).OrderBy(x => x, StringComparer.Ordinal).First();
            XmlElement location = null;
            foreach (var d in tampered.Documents)
            {
                var e = d.Value.SelectSingleNode("//Settlement[@id='" + town + "']/Locations/Location[@id='center']") as XmlElement;
                if (e != null) location = e;
            }
            if (location != null) location.SetAttribute("scene_name_2", "mnbl_no_such_scene");
            var r = CampaignGraphValidator.Validate(graphPolicy, tampered);
            var codes = r.Diagnostics.Select(d => d.Code + " " + d.GeneratedId + " " + d.Path).ToList();
            Check(location != null && codes.Count == 1 && codes[0] == "graph.pending_slot " + town + " prototypeAndScenes" && r.Diagnostics[0].Reason.Contains("mnbl_no_such_scene is not an installed scene"),
                  "a staged town whose wall level 2 center scene is not installed fails its MMR-42 prototypeAndScenes slot, and nothing else (" + (codes.Count == 1 ? r.Diagnostics[0].Reason : String.Join("; ", codes.Take(3))) + ")");
        }
        // MMR-42: a claimed prototypeAndScenes slot is bound to the staged locations (its reference record must be theirs).
        {
            var bound = fresh();
            int at = bound.Resolutions.FindIndex(x => x.Slot == "prototypeAndScenes");
            var original = bound.Resolutions[at];
            var value = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(original.Value);
            value["referencesSha256"] = new string('0', 64);
            bound.Resolutions[at] = new CampaignGraphValidator.SlotResolution { Record = original.Record, Slot = original.Slot, Value = new JavaScriptSerializer().Serialize(value) };
            var r = CampaignGraphValidator.Validate(graphPolicy, bound);
            var codes = r.Diagnostics.Select(d => d.Code + " " + d.GeneratedId + " " + d.Path).ToList();
            Check(codes.Count == 1 && codes[0] == "graph.pending_slot " + original.Record + " prototypeAndScenes" && r.Diagnostics[0].Reason.Contains("are not the staged locations'"),
                  "a prototypeAndScenes claim whose reference record is not the staged locations' own is refused (" + (codes.Count == 1 ? r.Diagnostics[0].Reason : String.Join("; ", codes.Take(3))) + ")");
        }
        var identity = String.Join("\n", policies.Select(p => p.Key + " " + p.Value));
        Check((string)clean.PolicyIdentity["sha256"] == Sha(new UTF8Encoding(false).GetBytes(identity)) && policies.Count == L(graphPolicy, "policies").Count + L(graphPolicy, "requiredInventory").Count && clean.PendingRuntime.Count == 2 &&
              clean.PendingRuntime.All(p => p.StartsWith("W05", StringComparison.Ordinal) || p.StartsWith("U03", StringComparison.Ordinal)),
              "the report binds the identity of the " + L(graphPolicy, "policies").Count + " resolving policies and templates, the graph policy, the catalog receipt and the live default inventory (" + ((string)clean.PolicyIdentity["sha256"]).Substring(0, 12) + ", for the activation receipt), and of every staged document; it lists the W05/U03 runtime position and route checks as pending; " +
              ((SortedDictionary<string, int>)census["positionsChecked"])["spawnPoints"] + " spawn positions (anchors, starts, pirate zones) and every settlement position are checked against Calradia's");
        var again = CampaignGraphValidator.Validate(graphPolicy, fresh());
        Check(again.Sha256 == clean.Sha256, "validating the same staged graph twice gives the same report (" + clean.Sha256.Substring(0, 12) + ")");

        // c1: each fault class, injected alone into a fresh copy, is reported with its code and nothing else.
        var landlocked = world.Settlements.Where(s => s.Kind == "town" && !s.Port).Select(s => s.Id).ToList();
        var portTowns = world.Settlements.Where(s => s.Kind == "town" && s.Port).Select(s => s.Id).ToList();
        var castleIds = world.Settlements.Where(s => s.Kind == "castle").Select(s => s.Id).ToList();
        var someTown = landlocked[0]; var portTown = portTowns[0]; var castle = castleIds[0];
        var villageList = world.Settlements.Where(s => s.Kind == "village").Select(s => s.Id).ToList();
        var village = villageList[0];
        var plannedHomes = new HashSet<string>(start.Plan.Notables.Concat(start.Plan.Companions).Select(h => h.Settlement), StringComparer.Ordinal);
        var dropped = villageList.Skip(5).FirstOrDefault(v => !plannedHomes.Contains(v)) ?? villageList[5];
        var heroesDoc = heroes.Documents.Single(d => d.Key == "Heroes").Value;
        var heroRefs = new HashSet<string>(documents.SelectMany(d => d.Value.SelectNodes("//@*").OfType<XmlAttribute>().Select(a => a.Value)), StringComparer.Ordinal);
        var lord = heroes.XmlHeroes.First(h => h.Clan != null && factions.Clans.Any(c => c.LeaderId == h.Id)).Id;
        var clan = factions.Clans.First(c => c.LeaderId == lord);
        var compilerStaged = heroes.Dispositions.Where(d => d.Outcome == "instantiated" && d.Id != null && heroesDoc.DocumentElement.SelectSingleNode("Hero[@id='" + d.Id + "']") != null).Select(d => d.Id).ToList();
        var removable = compilerStaged.FirstOrDefault(id => !heroRefs.Contains("Hero." + id));
        bool removableReferenced = removable == null;
        // Every staged compiler hero leads a clan (the compiler's lords lead its clans and MMR-31's vassal clans), so removing one also leaves
        // its clan without a leader: a vassal clan's leader is removed, never a ruler, and not the lord the other faults use.
        if (removable == null) removable = compilerStaged.First(id => factions.Kingdoms.All(k => k.RulerId != id) && id != lord);
        var removedClan = factions.Clans.FirstOrDefault(c => c.LeaderId == removable);
        var removedCodes = removableReferenced ? (removedClan != null ? new[] { "graph.silent_discard", "graph.unresolved_reference", "graph.ownership" } : new[] { "graph.silent_discard", "graph.unresolved_reference" }) : new[] { "graph.silent_discard" };
        var kingdom = factions.Kingdoms.First(k => !factions.Clans.Any(c => c.KingdomId == k.Id && c.Id == clan.Id) && factions.Clans.Count(c => c.KingdomId == k.Id) > 1);
        var vanillaTown = catalog["Settlements"].DocumentElement.SelectSingleNode("Settlement[Components/Town[not(@is_castle='true')]]") as XmlElement;
        var vanillaHero = catalog["Heroes"].DocumentElement.ChildNodes.OfType<XmlElement>().First(h => h.HasAttribute("faction") && !CampaignIdentity.IsAlias(h.GetAttribute("id")));
        var vanillaKingdom = catalog["Kingdoms"].DocumentElement.SelectSingleNode("Kingdom") as XmlElement;
        var vanillaClan = catalog["Factions"].DocumentElement.SelectSingleNode("Faction[not(@is_bandit='true') and @super_faction]") as XmlElement;
        var troop = catalog["NPCCharacters"].DocumentElement.SelectSingleNode("NPCCharacter[upgrade_targets/upgrade_target and not(@is_hero='true')]") as XmlElement;
        var template = heroes.XmlHeroes.First().Template;
        var templateRoster = (resolver.Element("NPCCharacter." + template).SelectSingleNode("Equipments/EquipmentRoster | Equipments/EquipmentSet") as XmlElement);
        var rosterItem = catalog["EquipmentRosters"].DocumentElement.SelectSingleNode("EquipmentRoster[@id='" + templateRoster.GetAttribute("id") + "']//@id[starts-with(., 'Item.')]").Value;
        Func<XmlDocument, string, XmlDocument> without = (source, xpath) => { var copy = (XmlDocument)source.CloneNode(true); var n = copy.DocumentElement.SelectSingleNode(xpath); n.ParentNode.RemoveChild(n); return copy; };
        var notable = start.Plan.Notables.First();
        var nonLeader = heroes.XmlHeroes.First(h => factions.Clans.All(c => c.LeaderId != h.Id) && factions.Kingdoms.All(k => k.RulerId != h.Id)).Id;
        var demoted = landlocked.First(t => t != someTown);
        var demotedCodes = plannedHomes.Contains(demoted) ? new[] { "graph.source_mismatch", "graph.party_intent" } : new[] { "graph.source_mismatch" };
        var swappedVillage = villageList[8];
        var compilerBound = S(Records(campaign, "villages").First(v => S(v, "id") == swappedVillage), "bound");
        var swappedTo = world.Settlements.First(s => (s.Kind == "town" || s.Kind == "castle") && s.Id != compilerBound).Id;
        var installedTown = catalog["Settlements"].DocumentElement.SelectSingleNode("Settlement[Components/Town[not(@is_castle='true')]]") as XmlElement;
        var vanillaCharacter = catalog["NPCCharacters"].DocumentElement.SelectSingleNode("NPCCharacter[@id='" + vanillaHero.GetAttribute("id") + "']") as XmlElement;
        var reachedTroop = clean.Reached.First(k => k.StartsWith("NPCCharacter.", StringComparison.Ordinal) && resolver.Element(k) != null && resolver.Element(k).SelectSingleNode("upgrade_targets/upgrade_target") != null).Substring(13);
        var bounded = dispositions.First(d => d.Outcome == "bounded");
        var hideout = start.Hideouts.First().Id;
        var secondNotable = start.Plan.Notables[1];
        Func<GeneratedCampaignInitializer.StartupPlan, GeneratedCampaignInitializer.StartupPlan> copyPlan = p => { var q = new GeneratedCampaignInitializer.StartupPlan(); q.Notables.AddRange(p.Notables); q.Companions.AddRange(p.Companions); q.Relations.AddRange(p.Relations); return q; };
        Func<GeneratedCampaignInitializer.HeroPlan, string, string, GeneratedCampaignInitializer.HeroPlan> withPlan = (n, t, s) => new GeneratedCampaignInitializer.HeroPlan { Id = n.Id, Uid = n.Uid, Template = t ?? n.Template, Settlement = s ?? n.Settlement, Name = n.Name, Occupation = n.Occupation, Age = n.Age };
        string settlementOf = "Settlement[@id='{0}']";
        var faults = new List<Fault> {
            new Fault("an element with no object type", i => Doc(i, "Settlements").DocumentElement.AppendChild(Doc(i, "Settlements").CreateElement("Starport")).Attributes.Append(Doc(i, "Settlements").CreateAttribute("id")).Value = factions.WorldPrefix + "starport", "graph.unknown_type"),
            new Fault("the same settlement staged twice", i => Import(i, "Settlements", Node(i, "Settlements", String.Format(settlementOf, village))), "graph.duplicate_id"),
            new Fault("a top-level reference to nothing (a hero's culture)", i => Node(i, "NPCCharacters", "NPCCharacter[@id='" + lord + "']").SetAttribute("culture", "Culture.atlantis"), "graph.unresolved_reference"),
            new Fault("a component reference to nothing (a town building type)", i => { var b = Doc(i, "Settlements").CreateElement("Building"); b.SetAttribute("id", "building_space_elevator"); b.SetAttribute("level", "1"); Node(i, "Settlements", String.Format(settlementOf, someTown) + "/Components/Town/Buildings").AppendChild(b); }, "graph.unresolved_reference"),
            new Fault("a nested reference to nothing (a hero's equipment set)", i => { var e = Doc(i, "NPCCharacters").CreateElement("EquipmentSet"); e.SetAttribute("id", "missing_roster"); var eq = Node(i, "NPCCharacters", "NPCCharacter[@id='" + lord + "']"); (eq.SelectSingleNode("Equipments") ?? eq.AppendChild(Doc(i, "NPCCharacters").CreateElement("Equipments"))).AppendChild(e); }, "graph.unresolved_reference"),
            new Fault("an installed template whose own reference resolves to nothing (transitive closure: an item of a roster a generated hero uses)", i => i.Catalog["Items"] = without(catalog["Items"], "*[@id='" + rosterItem.Substring(5) + "']"), "graph.unresolved_reference")
                { Detail = r => r.Diagnostics.Any(d => d.GeneratedId.StartsWith("NPCCharacter." + factions.WorldPrefix, StringComparison.Ordinal) && d.Path.StartsWith("EquipmentRoster.", StringComparison.Ordinal)) },
            new Fault("a malformed typed reference", i => Node(i, "Heroes", "Hero[@id='" + lord + "']").SetAttribute("faction", "Faction.bad id"), "graph.malformed_reference", "graph.ownership"),
            new Fault("a reference to an installed Calradia clan the world does not stage", i => Node(i, "Settlements", String.Format(settlementOf, castle)).SetAttribute("owner", "Faction." + vanillaClan.GetAttribute("id")), "graph.official_reference"),
            new Fault("an installed Calradia town staged unchanged", i => Import(i, "Settlements", vanillaTown), "graph.unexpected_vanilla"),
            new Fault("an installed Calradia hero staged", i => Import(i, "Heroes", vanillaHero), "graph.unexpected_vanilla"),
            new Fault("an installed Calradia kingdom staged", i => Import(i, "Kingdoms", vanillaKingdom), "graph.unexpected_vanilla"),
            new Fault("an object of another world", i => { var e = Import(i, "Settlements", Node(i, "Settlements", String.Format(settlementOf, villageList[3]))); e.SetAttribute("id", "mnbl_00000000_v_0000000000"); ((XmlElement)e.SelectSingleNode("Components/*")).SetAttribute("id", "mnbl_00000000_v_0000000000_component"); }, "graph.unknown_object"),
            new Fault("a kingdom whose ruler leads none of its clans", i => Node(i, "Kingdoms", "Kingdom[@id='" + kingdom.Id + "']").SetAttribute("owner", "Hero." + lord), "graph.ownership"),
            new Fault("a kingdom capital held outside the kingdom", i => Node(i, "Kingdoms", "Kingdom[@id='" + kingdom.Id + "']").SetAttribute("initial_home_settlement", "Settlement." + factions.Kingdoms.First(k => k.Id != kingdom.Id && factions.Clans.All(c => c.KingdomId != kingdom.Id || c.HomeId != k.CapitalId)).CapitalId), "graph.ownership"),
            new Fault("a hero whose character is not a hero", i => Node(i, "NPCCharacters", "NPCCharacter[@id='" + lord + "']").SetAttribute("is_hero", "false"), "graph.hero_role"),
            new Fault("a settlement with two components", i => { var c = (XmlElement)Node(i, "Settlements", String.Format(settlementOf, villageList[1]) + "/Components/Village").CloneNode(true); c.SetAttribute("id", villageList[1] + "_second"); Node(i, "Settlements", String.Format(settlementOf, villageList[1]) + "/Components").AppendChild(c); }, "graph.settlement_component"),
            new Fault("a town without its location complex", i => { var l = Node(i, "Settlements", String.Format(settlementOf, someTown) + "/Locations"); l.RemoveAttribute("complex_template"); }, "graph.settlement_component"),
            new Fault("a village bound to a village", i => Node(i, "Settlements", String.Format(settlementOf, villageList[2]) + "/Components/Village").SetAttribute("bound", "Settlement." + villageList[4]), "graph.village_binding", "graph.source_mismatch"),
            new Fault("a port town without its shipyard (its MMR-35 slot then claims what the graph lacks)", i => { var b = Node(i, "Settlements", String.Format(settlementOf, portTown) + "/Components/Town/Buildings/Building[@id='building_shipyard']"); b.ParentNode.RemoveChild(b); }, "graph.port", "graph.pending_slot"),
            new Fault("a shipyard in a castle", i => { var b = Doc(i, "Settlements").CreateElement("Building"); b.SetAttribute("id", "building_shipyard"); b.SetAttribute("level", "1"); Node(i, "Settlements", String.Format(settlementOf, castle) + "/Components/Town/Buildings").AppendChild(b); }, "graph.port"),
            new Fault("a troop upgrade tree with a cycle", i => { var t = Import(i, "NPCCharacters", troop); var target = (XmlElement)t.SelectSingleNode("upgrade_targets/upgrade_target"); target.SetAttribute("id", "NPCCharacter." + troop.GetAttribute("id")); }, "graph.troop_upgrade"),
            new Fault("a clan whose home is a village", i => Node(i, "Factions", "Faction[@id='" + clan.Id + "']").SetAttribute("initial_home_settlement", "Settlement." + villageList[4]), "graph.party_intent"),
            new Fault("a runtime notable whose template is not a template", i => { var plan = copyPlan(i.Plan); plan.Notables[plan.Notables.FindIndex(n => n.Id == notable.Id)] = withPlan(notable, lord, null); i.Plan = plan; }, "graph.party_intent"),
            new Fault("a runtime notable homed at a hideout", i => { var plan = copyPlan(i.Plan); plan.Notables[plan.Notables.FindIndex(n => n.Id == secondNotable.Id)] = withPlan(secondNotable, null, hideout); i.Plan = plan; }, "graph.party_intent"),
            new Fault("a runtime relation to a hero that does not exist", i => { var plan = copyPlan(i.Plan); plan.Relations.Add(new GeneratedCampaignInitializer.RelationPlan { A = notable.Id, B = factions.WorldPrefix + "h_nobody", Value = 10 }); i.Plan = plan; }, "graph.party_intent"),
            new Fault("a hero whose clan is a kingdom (a reference of the wrong type)", i => Node(i, "Heroes", "Hero[@id='" + nonLeader + "']").SetAttribute("faction", "Kingdom." + kingdom.Id), "graph.reference_type"),
            new Fault("a castle owned by a hero (a reference of the wrong type)", i => Node(i, "Settlements", String.Format(settlementOf, castleIds[1])).SetAttribute("owner", "Hero." + lord), "graph.reference_type"),
            new Fault("a village whose type is a clan (a reference of the wrong type)", i => Node(i, "Settlements", String.Format(settlementOf, villageList[6]) + "/Components/Village").SetAttribute("village_type", "Faction." + clan.Id), "graph.reference_type"),
            new Fault("the character of an installed Calradia hero staged", i => Import(i, "NPCCharacters", vanillaCharacter), "graph.unexpected_vanilla"),
            new Fault("an installed Calradia clan staged", i => Import(i, "Factions", vanillaClan), "graph.unexpected_vanilla"),
            new Fault("a generated character naming an installed Calradia hero's character", i => { var npc = Node(i, "NPCCharacters", "NPCCharacter[@id='" + nonLeader + "']"); var t = Doc(i, "NPCCharacters").CreateElement("upgrade_targets"); var u = Doc(i, "NPCCharacters").CreateElement("upgrade_target"); u.SetAttribute("id", "NPCCharacter." + vanillaHero.GetAttribute("id")); t.AppendChild(u); npc.AppendChild(t); }, "graph.official_reference"),
            new Fault("a reference to the world alias retirement_retreat after the world stopped staging it", i => { var r = Node(i, "Settlements", "Settlement[@id='retirement_retreat']"); r.ParentNode.RemoveChild(r); Node(i, "Heroes", "Hero[@id='" + lord + "']").SetAttribute("born_settlement", "Settlement.retirement_retreat"); }, "graph.official_reference"),
            new Fault("a clan leader who is a wanderer", i => Node(i, "NPCCharacters", "NPCCharacter[@id='" + clan.LeaderId + "']").SetAttribute("occupation", "Wanderer"), "graph.hero_role"),
            new Fault("a village without its location complex", i => { var l = Node(i, "Settlements", String.Format(settlementOf, villageList[7]) + "/Locations"); l.ParentNode.RemoveChild(l); }, "graph.settlement_component"),
            new Fault("a cycle in an installed troop tree the world reaches", i => { var npcs = (XmlDocument)catalog["NPCCharacters"].CloneNode(true); ((XmlElement)npcs.DocumentElement.SelectSingleNode("NPCCharacter[@id='" + reachedTroop + "']/upgrade_targets/upgrade_target")).SetAttribute("id", "NPCCharacter." + reachedTroop); i.Catalog["NPCCharacters"] = npcs; }, "graph.troop_upgrade"),
            new Fault("a creation culture started at an installed Calradia town", i => { var c = Node(i, "SPCultures", "Culture"); c.SetAttribute("start_point_position_x", installedTown.GetAttribute("posX")); c.SetAttribute("start_point_position_y", installedTown.GetAttribute("posY")); }, "graph.fixed_position"),
            new Fault("a spawn anchor at an installed Calradia town", i => i.SpawnPoints.Add(new CampaignGraphValidator.SpawnPoint { Label = "bandit anchor copied from Calradia", X = double.Parse(installedTown.GetAttribute("posX"), System.Globalization.CultureInfo.InvariantCulture), Y = double.Parse(installedTown.GetAttribute("posY"), System.Globalization.CultureInfo.InvariantCulture) }), "graph.fixed_position"),
            new Fault("a disposition with no outcome", i => { i.Dispositions = i.Dispositions.Select(d => d == bounded ? new CampaignGraphValidator.SourceDisposition { Uid = d.Uid, Id = d.Id, Outcome = null, Reason = null } : d).ToList(); }, "graph.disposition", "graph.silent_discard"),
            new Fault("a disposition outside the policy ('dropped')", i => { i.Dispositions = i.Dispositions.Select(d => d == bounded ? new CampaignGraphValidator.SourceDisposition { Uid = d.Uid, Id = d.Id, Outcome = "dropped", Reason = "too many heroes" } : d).ToList(); }, "graph.disposition", "graph.silent_discard"),
            new Fault("a staged town that is also excluded", i => { var c = Copy(i.Campaign); c["exclusions"] = Records(c, "exclusions").Concat(new[] { (IDictionary<string, object>)new Dictionary<string, object> { ["id"] = someTown, ["kind"] = "town", ["uid"] = "x", ["reason"] = "test" } }).ToArray(); i.Campaign = c; }, "graph.disposition"),
            new Fault("a compiler town staged as a castle", i => ((XmlElement)Node(i, "Settlements", String.Format(settlementOf, demoted) + "/Components/Town")).SetAttribute("is_castle", "true"), demotedCodes),
            new Fault("a compiler port town staged without its port (its naval slots then claim what the graph lacks)", i => { var t = Node(i, "Settlements", String.Format(settlementOf, portTowns[1])); t.RemoveAttribute("port_posX"); t.RemoveAttribute("port_posY"); }, "graph.source_mismatch", "graph.pending_slot", "graph.port"),
            new Fault("a village bound to another town than its compiler record", i => Node(i, "Settlements", String.Format(settlementOf, swappedVillage) + "/Components/Village").SetAttribute("bound", "Settlement." + swappedTo), "graph.source_mismatch"),
            new Fault("a kingdom staged without the policies its slot claims", i => { var p = Node(i, "Kingdoms", "Kingdom[@id='" + kingdom.Id + "']/policies"); p.RemoveAll(); }, "graph.pending_slot"),
            new Fault("a staged document without a root element", i => i.Documents.Add(new KeyValuePair<string, XmlDocument>("Empty", new XmlDocument())), "graph.malformed_input"),
            new Fault("a record element without an id", i => Doc(i, "Settlements").DocumentElement.AppendChild(Doc(i, "Settlements").CreateElement("Settlement")).Attributes.Append(Doc(i, "Settlements").CreateAttribute("name")).Value = "nameless", "graph.malformed_input"),
            new Fault("a disposition naming neither a source UID nor an id", i => i.Dispositions.Add(new CampaignGraphValidator.SourceDisposition { Outcome = "bounded", Reason = "orphan" }), "graph.malformed_input"),
            new Fault("a compiler slot without an owner", i => { var c = Copy(i.Campaign); var slot = Records(Records(c, "towns").First(), "slots").First(s => S(s, "status") == "pending"); slot.Remove("owner"); i.Campaign = c; }, "graph.malformed_input"),
            new Fault("a record excluded twice", i => { var c = Copy(i.Campaign); c["exclusions"] = Records(c, "exclusions").Concat(new[] { Records(c, "exclusions").First() }).ToArray(); i.Campaign = c; }, "graph.malformed_input"),
            new Fault("an id-map entry without a uid", i => { var m = Copy(i.IdMap); m["entries"] = Records(m, "entries").Concat(new[] { (IDictionary<string, object>)new Dictionary<string, object> { ["id"] = factions.WorldPrefix + "h_ghost", ["kind"] = "hero" } }).ToArray(); i.IdMap = m; }, "graph.malformed_input"),
            new Fault("a town at an installed Calradia coordinate", i => { var t = Node(i, "Settlements", String.Format(settlementOf, someTown)); t.SetAttribute("posX", vanillaTown.GetAttribute("posX")); t.SetAttribute("posY", vanillaTown.GetAttribute("posY")); }, "graph.fixed_position"),
            new Fault("a compiler village dropped without a disposition (its MMR-33 slots then claim what the graph lacks" + (plannedHomes.Contains(dropped) ? ", and its runtime notables lose their home)" : ")"), i => { var v = Node(i, "Settlements", String.Format(settlementOf, dropped)); v.ParentNode.RemoveChild(v); },
                      plannedHomes.Contains(dropped) ? new[] { "graph.silent_discard", "graph.party_intent", "graph.pending_slot" } : new[] { "graph.silent_discard", "graph.pending_slot" }),
            new Fault("a staged compiler hero removed although its disposition says instantiated" + (removedClan != null ? " (it led " + removedClan.Id + ")" : ""), i => { var h = Node(i, "Heroes", "Hero[@id='" + removable + "']"); h.ParentNode.RemoveChild(h); },
                      removedCodes),
            new Fault("a delivered owner's slot left pending", i => i.Resolutions.RemoveAll(r => r.Slot == "shipyardAndFleet" && r.Record == portTown), "graph.pending_slot"),
            new Fault("a resolving policy missing from the snapshot", i => i.Policies.Remove("worldgen/policies/rosters.json"), "graph.policy_identity"),
        };
        var results = new List<SortedDictionary<string, object>>();
        foreach (var f in faults)
        {
            var input = fresh(); string error = null; CampaignGraphValidator.Report r = null;
            try { f.Apply(input); r = CampaignGraphValidator.Validate(graphPolicy, input); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            var codes = r == null ? new List<string>() : r.Diagnostics.Select(d => d.Code).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            bool ok = r != null && !r.Ok && codes.SequenceEqual(f.Codes.OrderBy(x => x, StringComparer.Ordinal)) && r.Diagnostics.All(d => d.Reason != null && (d.GeneratedId != null || d.SourceUid != null || d.Path != null)) && (f.Detail == null || f.Detail(r));
            Check(ok, "fault: " + f.Name + " -> " + (error ?? (r.Ok ? "NOT refused" : String.Join(", ", codes) + " | " + r.Summary(3))));
            results.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["fault"] = f.Name, ["expected"] = f.Codes, ["reported"] = codes, ["diagnostics"] = r == null ? null : r.Diagnostics.Select(d => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["code"] = d.Code, ["sourceUid"] = d.SourceUid, ["generatedId"] = d.GeneratedId, ["path"] = d.Path, ["reason"] = d.Reason }).ToList(), ["error"] = error });
        }
        // All of them at once (the faults touch different records), in one pass.
        var combined = fresh(); var applied = new List<string>();
        foreach (var f in faults) { try { f.Apply(combined); applied.Add(f.Name); } catch (Exception) { } }
        var all = CampaignGraphValidator.Validate(graphPolicy, combined);
        var expectedAll = faults.SelectMany(f => f.Codes).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var reportedAll = all.Diagnostics.Select(d => d.Code).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(!all.Ok && applied.Count == faults.Count && expectedAll.All(reportedAll.Contains) && all.Diagnostics.Count >= faults.Count,
              "all " + applied.Count + " faults injected together are reported in one pass: " + all.Diagnostics.Count + " diagnostics covering " + reportedAll.Count + " codes (" + String.Join(", ", reportedAll) + ")" +
              (expectedAll.All(reportedAll.Contains) ? "" : "; MISSING " + String.Join(", ", expectedAll.Where(c => !reportedAll.Contains(c)))));
        // Optional references the installed loaders tolerate (an unknown skill on a character) are noted, not refused.
        var optional = fresh();
        var skill = Doc(optional, "NPCCharacters").CreateElement("skill"); skill.SetAttribute("id", "Stargazing"); skill.SetAttribute("value", "10");
        var lordElement = Node(optional, "NPCCharacters", "NPCCharacter[@id='" + lord + "']");
        (lordElement.SelectSingleNode("skills") ?? lordElement.AppendChild(Doc(optional, "NPCCharacters").CreateElement("skills"))).AppendChild(skill);
        var tolerated = CampaignGraphValidator.Validate(graphPolicy, optional);
        Check(tolerated.Ok && tolerated.OptionalAbsent.Any(x => x.StartsWith("Skill.Stargazing from NPCCharacter." + lord, StringComparison.Ordinal)),
              "an optional reference the installed loader tolerates missing (an unknown skill id, PropertyOwner.Deserialize) is noted, not refused: " + tolerated.Summary(2));

        // The dissolved-realm path (the real bundle dissolves none): a manifest of one realm with no town and its clan, dissolved by MMR-31.
        // Its records get the factories' dispositions and its slots are moot, so nothing is discarded silently and no slot is left pending.
        var dissolvedFactions = new FactionFactory.Result { WorldPrefix = "mnbl_00000001_" };
        dissolvedFactions.Dissolved.Add(new FactionFactory.Dissolution { Kingdom = "mnbl_00000001_k_0000000001", Uid = "realm-node-1", Reason = "no town after the compiler and travel exclusions", Clans = { "mnbl_00000001_l_0000000001" } });
        var dissolvedCampaign = Parse(Json.Serialize(new Dictionary<string, object> {
            ["kingdoms"] = new[] { new Dictionary<string, object> { ["id"] = "mnbl_00000001_k_0000000001", ["uid"] = "realm-node-1", ["slots"] = new[] { new Dictionary<string, object> { ["owner"] = "MMR-31", ["name"] = "policies", ["status"] = "pending" }, new Dictionary<string, object> { ["owner"] = "MMR-31", ["name"] = "bannerAndColors", ["status"] = "pending" } } } },
            ["clans"] = new[] { new Dictionary<string, object> { ["id"] = "mnbl_00000001_l_0000000001", ["uid"] = "clan-node-1", ["kingdom"] = "mnbl_00000001_k_0000000001", ["slots"] = new[] { new Dictionary<string, object> { ["owner"] = "MMR-31", ["name"] = "banner", ["status"] = "pending" }, new Dictionary<string, object> { ["owner"] = "MMR-31", ["name"] = "tier", ["status"] = "pending" } } } },
            ["exclusions"] = new object[0], ["notInstantiated"] = new object[0] }));
        var dissolvedIdMap = Parse(Json.Serialize(new Dictionary<string, object> { ["entries"] = new[] {
            new Dictionary<string, object> { ["id"] = "mnbl_00000001_k_0000000001", ["uid"] = "realm-node-1", ["kind"] = "kingdom" }, new Dictionary<string, object> { ["id"] = "mnbl_00000001_l_0000000001", ["uid"] = "clan-node-1", ["kind"] = "clan" } } }));
        var dissolvedInput = new CampaignGraphValidator.Input { WorldPrefix = "mnbl_00000001_", Campaign = dissolvedCampaign, IdMap = dissolvedIdMap };
        dissolvedInput.Resolutions.AddRange(CampaignGraphValidator.Resolutions(dissolvedFactions, null, null, null)); dissolvedInput.Dispositions.AddRange(CampaignGraphValidator.Dispositions(dissolvedFactions, null));
        foreach (var p in policies) dissolvedInput.Policies[p.Key] = p.Value;
        var dissolvedReport = CampaignGraphValidator.Validate(graphPolicy, dissolvedInput);
        var dissolvedSlots = (SortedDictionary<string, int>)dissolvedReport.Census["slots"];
        Check(dissolvedReport.Ok && dissolvedReport.Outcomes["realm-node-1"].StartsWith("dissolved: ", StringComparison.Ordinal) && dissolvedReport.Outcomes["clan-node-1"].StartsWith("dissolved: ", StringComparison.Ordinal) &&
              dissolvedSlots.Keys.All(k => k.EndsWith(" moot", StringComparison.Ordinal)) && dissolvedSlots.Values.Sum() == 4,
              "a dissolved realm and its clan (synthetic manifest: the real bundle dissolves none) have the factories' dissolved dispositions and their 4 slots are moot: " + dissolvedReport.Summary(3) + "; " + show(dissolvedSlots));

        // The in-game fixture worlds: the standard run's placements, seeds, culture and prototypes; the first with the MMR-33/34/35 fixtures.
        var fixtureWorlds = new List<SortedDictionary<string, object>>();
        var graphPolicySha = Sha(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/graph-validation.json")));
        Func<uint, string, bool, KeyValuePair<GeneratedCampaignDataset, CampaignGraphValidator.Input>> fixture = (seed, placementPath, fixtures) => {
            var placement = Load(Path.Combine(repo, placementPath));
            var options = FixturePlacement.Options(placement, seed, "empire", "spc_legion_of_the_betrayed_leader_0", "town_EN2", "village_EN1_3", fixtures ? economy : null, fixtures ? prototypes : null, fixtures, fixtures);
            var dataset = GeneratedCampaignDataset.Build(seed, options, catalog, defaults);
            var loads = dataset.Documents.Select(d => new KeyValuePair<string, XmlDocument>(d.Id, d.CreateDocument())).ToList();
            return new KeyValuePair<GeneratedCampaignDataset, CampaignGraphValidator.Input>(dataset, FixturePlacement.GraphInput(seed, dataset, loads, catalog, defaults, graphPolicySha, policies[CampaignGraphValidator.CatalogReceiptKey],
                fixtures ? Sha(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/economy.json"))) : null, fixtures ? Sha(File.ReadAllBytes(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json"))) : null));
        };
        foreach (var w in new[] { new { Seed = 364610u, Placement = "build/research-env59/w42/placement.json", Fixtures = true }, new { Seed = 364611u, Placement = "build/research-env59/w43/placement.json", Fixtures = false } })
        {
            var built = fixture(w.Seed, w.Placement, w.Fixtures);
            var report = CampaignGraphValidator.Validate(graphPolicy, built.Value);
            var worldDeclared = built.Key.Instances.Concat(built.Key.RequiredAliases).Distinct().ToList();
            Func<IEnumerable<string>, SortedDictionary<string, int>> byType = keys => new SortedDictionary<string, int>(keys.GroupBy(k => k.Substring(0, k.IndexOf('.'))).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
            var expectedWorld = byType(worldDeclared.Where(k => !CampaignIdentity.IsAlias(k.Substring(k.IndexOf('.') + 1))));
            var expectedAliasesWorld = byType(worldDeclared.Where(k => CampaignIdentity.IsAlias(k.Substring(k.IndexOf('.') + 1)) && !k.StartsWith("MobileParty.", StringComparison.Ordinal)));
            var c = report.Census;
            var reemitted = (SortedDictionary<string, int>)c["reemittedTemplates"];
            Check(report.Ok && ((SortedDictionary<string, int>)c["generated"]).SequenceEqual(expectedWorld) && ((SortedDictionary<string, int>)c["aliases"]).SequenceEqual(expectedAliasesWorld) &&
                  reemitted.Keys.All(k => L(graphPolicy, "reusableTypes").Contains(k)) && report.PolicyIdentity.ContainsKey("sha256") && ((SortedDictionary<string, string>)report.PolicyIdentity["policies"]).Count == (w.Fixtures ? 5 : 3) &&
                  reemitted["Culture"] == (w.Fixtures ? L(O(startup, "starts"), "creationCultures").Count : 1),
                  "in-game fixture world " + built.Value.WorldPrefix + (w.Fixtures ? " (settlement, startup and naval fixtures)" : " (no fixtures)") + " validates as the gate validates it: " + report.Summary(10) +
                  "; generated " + show((SortedDictionary<string, int>)c["generated"]) + " (declared by the dataset: " + show(expectedWorld) + "); aliases " + show((SortedDictionary<string, int>)c["aliases"]) + " (declared " + show(expectedAliasesWorld) +
                  ", the player party is a runtime object); re-emitted installed templates " + show(reemitted));
            fixtureWorlds.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["world"] = built.Value.WorldPrefix, ["fixtures"] = w.Fixtures, ["ok"] = report.Ok, ["reportSha256"] = report.Sha256, ["census"] = report.Census,
                ["optionalAbsent"] = report.OptionalAbsent.Count, ["policyIdentity"] = report.PolicyIdentity, ["diagnostics"] = report.Diagnostics.Select(d => d.Code + " " + d.GeneratedId + ": " + d.Reason).ToList() });
        }
        // The in-game probe's fault classes on the second fixture world (the world the probe faults), refused offline with the same codes.
        var gateFaults = new List<SortedDictionary<string, object>>();
        foreach (var fault in GraphFaults.Classes)
        {
            var built = fixture(364611u, "build/research-env59/w43/placement.json", false);
            var faulted = built.Value.Documents.ToList();
            string code = GraphFaults.Apply(fault, built.Value.WorldPrefix, faulted, catalog);
            built.Value.Documents.Clear(); built.Value.Documents.AddRange(faulted);
            var report = CampaignGraphValidator.Validate(graphPolicy, built.Value);
            var codes = report.Diagnostics.Select(d => d.Code).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check(!report.Ok && codes.SequenceEqual(new[] { code }), "in-game fault class " + fault + " on fixture world " + built.Value.WorldPrefix + " is refused with " + code + ": " + report.Summary(3));
            gateFaults.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["fault"] = fault, ["expected"] = code, ["reported"] = codes });
        }

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.graph-validation", ["version"] = 1, ["ticket"] = "MMR-36", ["worldPrefix"] = factions.WorldPrefix,
            ["policySha256"] = Sha(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/graph-validation.json"))),
            ["clean"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["ok"] = clean.Ok, ["reportSha256"] = clean.Sha256, ["census"] = clean.Census, ["deferredSlots"] = clean.DeferredSlots.Count,
                ["deferredExample"] = clean.DeferredSlots.FirstOrDefault(), ["optionalAbsent"] = clean.OptionalAbsent, ["pendingRuntime"] = clean.PendingRuntime, ["policyIdentity"] = clean.PolicyIdentity,
                ["diagnostics"] = clean.Diagnostics.Select(d => d.Code + " " + d.GeneratedId + ": " + d.Reason).ToList() },
            ["outcomes"] = clean.Outcomes, ["faults"] = results, ["fixtureWorlds"] = fixtureWorlds, ["gateFaults"] = gateFaults,
            ["combined"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["faults"] = applied.Count, ["diagnostics"] = all.Diagnostics.Count, ["codes"] = reportedAll },
            ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
