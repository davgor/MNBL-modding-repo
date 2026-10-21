using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-34 startup and spawn-policy tests, without the game: SpawnPolicyTests BUNDLE_DIR REPO_DIR OUT_JSON
// The real size-129 world goes through the accepted MMR-31/32/33 factories, then GeneratedCampaignInitializer. Every rule is rechecked
// here from the policy, the travel cells and the installed catalog; the compatibility inventory is checked against the behaviour scan.
internal static class SpawnPolicyTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static IDictionary<string, object> Parse(string text) { return (IDictionary<string, object>)Json.DeserializeObject(text.TrimStart('﻿')); }
    private static IDictionary<string, object> Load(string path) { return Parse(File.ReadAllText(path)); }
    private static IDictionary<string, object> Copy(IDictionary<string, object> o) { return Parse(Json.Serialize(o)); }
    private static IDictionary<string, object> O(IDictionary<string, object> o, string key) { return (IDictionary<string, object>)o[key]; }
    private static string S(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
    private static double D(IDictionary<string, object> o, string key) { return Convert.ToDouble(o[key]); }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList() : new List<string>(); }

    private static string Refusal(Func<object> build)
    {
        var e = Refused(build);
        return e == null ? null : e.Message;
    }

    private static GeneratedCampaignInitializer.StartupException Refused(Func<object> build)
    {
        try { build(); return null; }
        catch (GeneratedCampaignInitializer.StartupException e) { return e; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: SpawnPolicyTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
        try { Run(args[0], args[1], args[2]); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string bundleDir, string repo, string outJson)
    {
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(bundleDir, host);
        Func<string, IDictionary<string, object>> read = path => Parse(new UTF8Encoding(false).GetString(bundle.ReadVerified(path)));
        var campaign = read("compiled/campaign/campaign.json"); var idMap = read("compiled/campaign/id-map.json");
        var travel = read("compiled/travel/travel-graph.json"); var ports = read("compiled/travel/ports.json");
        var cells = bundle.ReadVerified("compiled/travel/cells.bin");
        var biomes = GeneratedCampaignInitializer.CellBiomes(bundle.ReadVerified("compiled/terrain/masks.bin"), new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/terrain/projection.json")),
            new UTF8Encoding(false).GetString(bundle.ReadVerified("policies/terrain-materials.json")));
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var population = Load(Path.Combine(repo, "worldgen/policies/population.json"));
        var economy = Load(Path.Combine(repo, "worldgen/policies/economy.json"));
        var startup = Load(Path.Combine(repo, "worldgen/policies/startup.json"));
        var prototypes = Load(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json"));
        var inventory = Load(Path.Combine(repo, "worldgen/policies/campaign-compatibility.json"));
        var scan = Load(Path.Combine(repo, "evidence/fwg/behavior-scan.json"));
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
        if (S(O(startup, "provenance"), "settlementsSha256") != S(O(economy, "provenance"), "settlementsSha256")) throw new InvalidOperationException("startup and economy policies come from different catalogs");
        var defaultsRun = Load(Path.Combine(repo, S(O(rosters, "provenance"), "defaultReferences")));
        var defaults = L(O(Records(O(defaultsRun, "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(rosters, "troops"));
        Func<IDictionary<string, object>, FactionFactory.Result> factionsOf = c => FactionFactory.Build(c, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates, Load(Path.Combine(repo, "worldgen/policies/population.json")));
        Func<IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, XmlDocument, GeneratedCampaignInitializer.Result> buildWith = (c, t, policy, cultureDoc) => {
            var f = factionsOf(c); var h = HeroFactory.Build(c, idMap, f, rosters, resolver, population);
            var sf = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists); var w = sf.Build(c, t, ports, f, h);
            return new GeneratedCampaignInitializer(policy, sf, catalog["Settlements"], catalog["Factions"], cultureDoc).Build(t, ports, cells, f, h, w, biomes); };
        Func<IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, GeneratedCampaignInitializer.Result> build = (c, t, policy) => buildWith(c, t, policy, catalog["SPCultures"]);
        var factions = factionsOf(campaign); var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, resolver, population);
        var factory = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists);
        var world = factory.Build(campaign, travel, ports, factions, heroes);
        var r = new GeneratedCampaignInitializer(startup, factory, catalog["Settlements"], catalog["Factions"], catalog["SPCultures"]).Build(travel, ports, cells, factions, heroes, world, biomes);
        var map = new GeneratedSpawnModel.CellMap(O(travel, "grid"), cells, L(O(startup, "passable"), "classes"), Records(travel, "settlements"));
        var towns = world.Settlements.Where(s => s.Kind == "town").ToList();
        var fixedSettlements = world.Settlements.ToList();
        double spacing = towns.Average(t => towns.Where(u => u != t).Min(u => map.Distance(t.X, t.Y, u.X, u.Y)));
        var townGroups = new HashSet<int>(towns.Select(t => map.Group(t.GateX, t.GateY)));
        var docs = r.Documents.ToDictionary(d => d.Key, d => d.Value);

        // c1: the compatibility inventory covers every installed behaviour and model the scan found, and every world-dependent one is classified.
        var scanned = Records(scan, "types").ToList();
        var types = O(inventory, "types");
        Check(S(O(inventory, "scan"), "sha256") == Sha(File.ReadAllBytes(Path.Combine(repo, "evidence/fwg/behavior-scan.json"))) &&
              scanned.All(t => types.ContainsKey(S(t, "type"))) && types.Count == scanned.Count &&
              types.Values.Cast<IDictionary<string, object>>().All(t => new[] { "retained", "adapted", "inactive", "excluded" }.Contains(S(t, "status")) && !String.IsNullOrEmpty(S(t, "reason"))),
              "the compatibility inventory classifies all " + scanned.Count + " installed behaviours and models the IL scan found (" + Json.Serialize(inventory["counts"]) + "), each with a reason");
        var worldDependent = scanned.Where(t => ((IDictionary<string, object>)t["worldIds"]).Count > 0 || L(t, "coordinates").Count > 0 ||
                                                 ((IDictionary<string, object>)t["sharedIds"]).Keys.Any(k => k.StartsWith("clan|")) ||
                                                 L(t, "lookups").Any(l => !l.Contains("(computed)") && Regex.IsMatch(l, @"^(Settlement|Hero|Clan|Kingdom)\.|GetObject<(Settlement|Hero|Clan|Kingdom)>"))).ToList();
        Check(worldDependent.All(t => S(O(types, S(t, "type")), "status") != "retained" || new[] { "comparison", "cheat", "deferred:MMR-35" }.Contains(S(O(types, S(t, "type")), "basis"))),
              "every one of the " + worldDependent.Count + " types with a world literal, a world lookup, a bandit clan id or a Calradia coordinate is adapted, inactive or excluded, or retained only because the literal is a comparison that is never true, a cheat, or MMR-35's: " +
              String.Join(", ", worldDependent.GroupBy(t => S(O(types, S(t, "type")), "status")).Select(g => g.Key + " " + g.Count())));
        // A predicate lookup whose lambda compares a world id (Settlement.FindFirst(s => s.StringId == "town_ES4")) dereferences what it finds:
        // never a harmless comparison. The scan now covers the models TaleWorlds.Core and TaleWorlds.MountAndBlade register too.
        var predicate = scanned.Where(t => L(t, "lookups").Any(l => l.Contains("(lambda:"))).ToList();
        Check(predicate.Count > 0 && predicate.All(t => S(O(types, S(t, "type")), "status") != "retained") &&
              S(O(types, "TaleWorlds.CampaignSystem.CampaignBehaviors.LordConversationsCampaignBehavior"), "status") == "adapted" &&
              new[] { "TaleWorlds.Core", "TaleWorlds.MountAndBlade" }.All(a => scanned.Any(t => S(t, "assembly") == a)),
              "every one of the " + predicate.Count + " types whose predicate lookup compares a world id inside its lambda is adapted, inactive or excluded, never retained as a comparison (" +
              String.Join("; ", predicate.Select(t => S(t, "type").Split('.').Last() + " " + S(O(types, S(t, "type")), "status") + ": " + String.Join(", ", L(t, "lookups").Where(l => l.Contains("(lambda:")).Select(l => l.Split(' ')[0])))) +
              "); the scan includes the " + scanned.Count(t => S(t, "assembly") == "TaleWorlds.Core" || S(t, "assembly") == "TaleWorlds.MountAndBlade") + " TaleWorlds.Core and TaleWorlds.MountAndBlade models");
        // Literal template lookups (retained) resolve among the installed records every generated world registers.
        var typeDocs = new Dictionary<string, string> { ["CharacterObject"] = "NPCCharacters", ["ItemObject"] = "Items", ["MBEquipmentRoster"] = "EquipmentRosters", ["ItemModifier"] = "ItemModifiers",
                                                         ["PartyTemplateObject"] = "partyTemplates", ["CultureObject"] = "SPCultures", ["ShipHull"] = "ShipHulls" };
        var literal = new Regex("^MBObjectManager\\.GetObject<(\\w+)>\\(\"([^\"]+)\"\\) in ");
        var lookups = scanned.Where(t => S(O(types, S(t, "type")), "status") != "excluded" && S(O(types, S(t, "type")), "status") != "inactive")
            .SelectMany(t => L(t, "lookups")).Select(l => literal.Match(l)).Where(m => m.Success && typeDocs.ContainsKey(m.Groups[1].Value))
            .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToList();
        var defaultItems = L(O(scan, "engineDefaults"), "Item");
        Func<string, string, bool> installed = (doc, id) => (catalog.ContainsKey(doc) && catalog[doc].DocumentElement.SelectNodes(".//*[@id='" + id + "']").Count > 0) || (doc == "Items" && defaultItems.Contains(id));
        var unresolved = lookups.Where(x => !installed(typeDocs[x.Key], x.Value)).ToList();
        Check(lookups.Count > 0 && unresolved.Count == 0,
              "all " + lookups.Count + " literal template lookups of the enabled behaviours and models name an installed record or an engine default item (DefaultItems: " + String.Join(", ", defaultItems) + "; " + String.Join(", ", lookups.GroupBy(x => x.Key).Select(g => g.Key + " " + g.Count())) +
              (unresolved.Count == 0 ? "" : "; unresolved: " + String.Join(", ", unresolved.Select(x => x.Key + ":" + x.Value))) + "); the full world's template closure keeps them (MMR-45)");

        // c2: starts never reference Calradia; every creation culture (the installed main cultures character creation offers, whether or not
        // the world presents through them) gets a start on a generated port town, and no installed start point survives.
        var creation = L(O(startup, "starts"), "creationCultures");
        var installedMain = catalog["SPCultures"].DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.GetAttribute("is_main_culture") == "true" && e.HasAttribute("start_point_position_x")).ToList();
        var installedStarts = catalog["SPCultures"].DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("start_point_position_x"))
            .Select(e => e.GetAttribute("start_point_position_x") + "," + e.GetAttribute("start_point_position_y")).ToList();
        var startDoc = docs["SPCultures"].DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        var byId = world.Settlements.ToDictionary(s => s.Id);
        int foodMinimum = Convert.ToInt32(O(startup, "starts")["foodMinimum"]);
        Check(creation.OrderBy(c => c).SequenceEqual(installedMain.Select(e => e.GetAttribute("id")).OrderBy(c => c)) && creation.Count == 7 &&
              installedMain.All(e => startDoc.Any(s => s.GetAttribute("id") == e.GetAttribute("id") &&
                                                       (s.GetAttribute("start_point_position_x") != e.GetAttribute("start_point_position_x") || s.GetAttribute("start_point_position_y") != e.GetAttribute("start_point_position_y")))),
              "the " + creation.Count + " creation cultures in startup.json are the installed main cultures with a start point (" + String.Join(", ", creation) + "), and each is re-emitted with a generated start point, so no installed Calradia start point survives");
        Check(r.Starts.Select(s => s.Culture).OrderBy(c => c).SequenceEqual(creation.OrderBy(c => c)) &&
              r.Starts.All(s => s.Fallback ? s.Reason != null : byId[s.Town].Culture == s.Culture) &&
              r.Starts.All(s => byId.ContainsKey(s.Town) && byId[s.Town].Kind == "town" && byId[s.Town].Port && s.X == byId[s.Town].GateX && s.Y == byId[s.Town].GateY &&
                                s.PortX == byId[s.Town].PortX && s.Food >= foodMinimum && s.Food == factory.TradeAreaFood(world, s.Town) && s.Notables >= 1 &&
                                s.SeaDestination != null && byId[s.SeaDestination].Port && s.SeaDestination != s.Town && s.ShipSource != null && map.Passable(s.X, s.Y)) &&
              startDoc.Count == r.Starts.Count && startDoc.All(e => r.Starts.Any(s => s.Culture == e.GetAttribute("id") && e.GetAttribute("start_point_position_x") == s.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) &&
              startDoc.All(e => !installedStarts.Contains(e.GetAttribute("start_point_position_x") + "," + e.GetAttribute("start_point_position_y"))),
              "each of the " + r.Starts.Count + " creation cultures starts at a generated port town's gate (CultureObject.StartingPoint; its own culture's town unless reported as a fallback), never an installed Calradia start point: food >= " + foodMinimum +
              ", notables to recruit from, a shipyard, and a sea destination in the same basin (" + String.Join("; ", r.Starts.Select(s => s.Culture + " " + s.Town.Substring(s.Town.Length - 6) + (s.Fallback ? " (fallback)" : "") + " food " + s.Food + " -> " + s.SeaDestination.Substring(s.SeaDestination.Length - 6))) + ")");
        var basins = Records(travel, "basins").ToDictionary(b => Convert.ToString(b["id"]), b => b);
        Check(r.Starts.All(s => basins.ContainsKey(s.Basin) && (bool)basins[s.Basin]["open"] && L(basins[s.Basin], "ports").Contains(s.Town) && L(basins[s.Basin], "ports").Contains(s.SeaDestination)),
              "every start's port and its sea destination lie in the same open MMR-28 sea basin (the operational port route; engine validation is MMR-38/40)");
        // The fallback rule: a culture whose towns do not qualify takes the qualifying town nearest its towns; a creation culture with no town
        // takes the qualifying town the starts stream draws for it.
        // A food minimum just above the poorest culture's best port town, so that culture has no qualifying town of its own.
        var bestFood = creation.Where(c => towns.Any(t => t.Culture == c && t.Port)).ToDictionary(c => c, c => towns.Where(t => t.Culture == c && t.Port).Max(t => factory.TradeAreaFood(world, t.Id)));
        int pickyMinimum = bestFood.Values.Min() + 1;
        var picky = Copy(startup); O(picky, "starts")["foodMinimum"] = pickyMinimum;
        var pickyStarts = build(campaign, travel, picky);
        var extraCultures = (XmlDocument)catalog["SPCultures"].CloneNode(true);
        var extraCulture = (XmlElement)extraCultures.ImportNode(installedMain.First(), true);
        extraCulture.SetAttribute("id", "mnbl_test_culture"); extraCultures.DocumentElement.AppendChild(extraCulture);
        var townless = Copy(startup); O(townless, "starts")["creationCultures"] = creation.Concat(new[] { "mnbl_test_culture" }).ToArray();
        var townlessStarts = buildWith(campaign, travel, townless, extraCultures);
        // MMR-262: a creation culture the world has no town of (khuzait on B') falls back for that reason under any food minimum.
        var absent = pickyStarts.Starts.Where(s => !towns.Any(t => t.Culture == s.Culture)).ToList();
        var fallbacks = pickyStarts.Starts.Where(s => s.Fallback && !absent.Contains(s)).ToList();
        var qualifyingTowns = new HashSet<string>(pickyStarts.Starts.Where(s => !s.Fallback).Select(s => s.Town));
        var noTown = townlessStarts.Starts.FirstOrDefault(s => s.Culture == "mnbl_test_culture");
        Check(fallbacks.Count > 0 && fallbacks.All(s => s.Reason.StartsWith("no " + s.Culture + " town qualifies") && byId[s.Town].Culture != s.Culture && s.Food >= pickyMinimum &&
                                                       towns.Where(t => t.Culture == s.Culture).All(t => pickyStarts.Starts.All(o => o.Culture != t.Culture || o.Fallback))) &&
              absent.All(s => s.Fallback && s.Reason == "the world has no " + s.Culture + " town" && s.Town != null && byId.ContainsKey(s.Town) && s.Food >= pickyMinimum) &&
              noTown != null && noTown.Fallback && noTown.Reason == "the world has no mnbl_test_culture town" && byId.ContainsKey(noTown.Town) && noTown.Food >= foodMinimum &&
              townlessStarts.Documents.Single(d => d.Key == "SPCultures").Value.SelectSingleNode("SPCultures/Culture[@id='mnbl_test_culture']") != null,
              "the fallback rule: with foodMinimum " + pickyMinimum + " (one above the poorest culture's best port town), " + String.Join("; ", fallbacks.Select(s => s.Culture + " falls back to " + byId[s.Town].Culture + " town " + s.Town.Substring(s.Town.Length - 6) + " (food " + s.Food + ")")) +
              String.Concat(absent.Select(s => "; " + s.Culture + " (no town in this world) falls back to " + (s.Town != null && byId.ContainsKey(s.Town) ? byId[s.Town].Culture + " town " + s.Town.Substring(s.Town.Length - 6) : "no town"))) +
              "; a creation culture with no town (a test culture added to the catalog) takes the qualifying town " + (noTown == null ? "?" : noTown.Town.Substring(noTown.Town.Length - 6)) + " the starts stream draws, reported");

        // Hideouts.
        var hp = O(startup, "hideouts");
        int total = (int)Math.Round(D(hp, "perTown") * towns.Count, MidpointRounding.AwayFromZero);
        double away = D(hp, "settlementDistance") * spacing, apart = D(hp, "hideoutSpacing") * spacing;
        var weights = O(hp, "cultureWeights");
        Func<SettlementFactory.SettlementIntent, string> nearestTownCulture = h => byId[r.HideoutTowns[h.Id]].Culture;
        Check(r.Hideouts.Count >= Math.Min(total, 1) && r.Hideouts.All(h => map.Passable(h.X, h.Y) && townGroups.Contains(map.Group(h.X, h.Y))) &&
              r.Hideouts.All(h => fixedSettlements.All(s => map.Distance(h.X, h.Y, s.X, s.Y) >= away - 1e-9)) &&
              r.Hideouts.All(h => r.Hideouts.Where(g => g != h).All(g => map.Distance(h.X, h.Y, g.X, g.Y) >= apart - 1e-9)) &&
              r.Hideouts.All(h => r.HideoutGrounds[h.Id].TownCulture == nearestTownCulture(h) && (r.HideoutGrounds[h.Id].Weights.ContainsKey(h.Culture) || h.Provenance.Contains(" replaced by " + h.Culture + " "))) &&
              r.Hideouts.All(h => CampaignIdentity.Problem(h.Id, r.WorldPrefix) == null && h.MapPrefab != null && h.Prototype != null) &&
              docs["Settlements"].DocumentElement.SelectNodes("Settlement[@type='Hideout']/Components/Hideout").Count == r.Hideouts.Count &&
              L(hp, "requiredCultures").All(c => r.Hideouts.Any(h => h.Culture == c)),
              r.Hideouts.Count + " hideouts (the installed " + D(hp, "perTown") + " per town gives " + total + "; " + String.Join(", ", r.Hideouts.GroupBy(h => h.Culture).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())) +
              "; every one of the " + L(hp, "requiredCultures").Count + " bandit cultures BanditSpawnCampaignBehavior spawns for has one) sit on passable land of a land group with towns, at least " + Math.Round(away, 1) + " from every settlement and " + Math.Round(apart, 1) + " from each other, with a bandit culture weighted by their nearest town's culture and their ground" +
              (r.Notes.Count == 0 ? "" : "; " + String.Join("; ", r.Notes)));
        // MMR-287 (RT-28): each hideout's weights are its nearest town culture's mixed with its cell's ground, recomputed from the bundle's biome
        // plane and the travel cells' sea class; a ground the policy lists moves the draw toward it.
        var hb = O(hp, "biome");
        var regrounded = r.Hideouts.Where(h => !h.Provenance.Contains(" replaced by ")).Where(h => {
            var g = r.HideoutGrounds[h.Id]; var cell = map.Index(h.X, h.Y);
            bool coast = map.Near(cell / map.Cols, cell % map.Cols, (int)D(hb, "coastCells"), "sea");
            var w = GeneratedCampaignInitializer.GroundWeights(weights.ContainsKey(g.TownCulture) ? O(weights, g.TownCulture) : null, hb, biomes(h.X, h.Y), coast);
            return g.Biome == biomes(h.X, h.Y) && g.Coast == coast && w.Count == g.Weights.Count && w.All(kv => g.Weights.ContainsKey(kv.Key) && Math.Abs(g.Weights[kv.Key] - kv.Value) < 1e-12) &&
                   Math.Abs(g.Weights.Values.Sum() - 1) < 1e-9 && g.Weights[h.Culture] > 0; }).Count();
        var desertCell = GeneratedCampaignInitializer.GroundWeights(O(weights, "sturgia"), hb, "desert", false);
        var coastForest = GeneratedCampaignInitializer.GroundWeights(O(weights, "khuzait"), hb, "forest", true);
        var unlisted = GeneratedCampaignInitializer.GroundWeights(O(weights, "khuzait"), hb, "lake", false);
        Check(regrounded == r.Hideouts.Count(h => !h.Provenance.Contains(" replaced by ")) && Math.Abs(desertCell["desert_bandits"] - D(hb, "share")) < 1e-12 &&
              Math.Abs(coastForest["forest_bandits"] - D(hb, "share") / 2) < 1e-12 && Math.Abs(coastForest["sea_raiders"] - D(hb, "share") / 2) < 1e-12 &&
              Math.Abs(coastForest["steppe_bandits"] - (1 - D(hb, "share"))) < 1e-12 && unlisted.Count == 1 && unlisted["steppe_bandits"] == 1,
              "every drawn hideout's weights are (1 - " + D(hb, "share") + ") x its nearest town culture's + " + D(hb, "share") + " x its ground's (biome plane, coast within " + D(hb, "coastCells") +
              " cell of sea), recomputed for " + regrounded + " hideouts; a sturgian town's desert cell weighs desert bandits " + D(hb, "share") + ", a khuzait town's coastal forest cell splits the ground share between forest bandits and sea raiders, an unlisted biome keeps the town's weights");
        // The coverage rule: with every installed weight pointing at forest bandits, every required bandit culture still gets a hideout.
        var forestOnly = Copy(startup);
        foreach (var w in O(O(forestOnly, "hideouts"), "cultureWeights").Values.Cast<IDictionary<string, object>>()) { foreach (var k in w.Keys.ToList()) w.Remove(k); w["forest_bandits"] = 1; }
        foreach (var w in O(O(O(forestOnly, "hideouts"), "biome"), "biomeWeights").Values.Cast<IDictionary<string, object>>()) { foreach (var k in w.Keys.ToList()) w.Remove(k); w["forest_bandits"] = 1; }
        O(O(forestOnly, "hideouts"), "biome")["coastWeight"] = 0;
        var covered = build(campaign, travel, forestOnly);
        Check(L(hp, "requiredCultures").All(c => covered.Hideouts.Count(h => h.Culture == c) >= 1) && covered.Hideouts.Count == r.Hideouts.Count &&
              covered.Notes.Count(n => n.Contains("so every bandit culture has a hideout")) == L(hp, "requiredCultures").Count - 1,
              "with every installed weight pointing at forest bandits, the coverage rule still gives each of the " + L(hp, "requiredCultures").Count + " bandit cultures a hideout (BanditSpawnCampaignBehavior reads its hideouts-by-culture map unchecked; run 37036 crashed without one): " +
              String.Join(", ", covered.Hideouts.GroupBy(h => h.Culture).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())));

        // Bandit clans and the retreat.
        var clanIds = L(O(startup, "banditClans"), "clans");
        var factionsDoc = docs["Factions"].DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        Check(clanIds.Count == 7 && factionsDoc.Select(e => e.GetAttribute("id")).OrderBy(x => x).SequenceEqual(clanIds.OrderBy(x => x)) && clanIds.All(CampaignIdentity.IsAlias) &&
              factionsDoc.All(e => e.GetAttribute("is_bandit") == "true" && r.Hideouts.Any(h => "Settlement." + h.Id == e.GetAttribute("initial_home_settlement"))) &&
              factionsDoc.All(e => { var installedClan = (XmlElement)catalog["Factions"].DocumentElement.SelectSingleNode("Faction[@id='" + e.GetAttribute("id") + "']");
                                      return installedClan.Attributes.Cast<XmlAttribute>().Where(a => a.Name != "initial_home_settlement").All(a => e.GetAttribute(a.Name) == a.Value); }),
              "the " + clanIds.Count + " installed land bandit clans (" + String.Join(", ", clanIds) + ") are re-created as world aliases with their installed records, each homed at a generated hideout (" +
              String.Join(", ", r.BanditHomes.Select(x => x.Key + " -> " + byIdOrHideout(r, x.Value))) + "); the pirate clans are MMR-35's");
        var retreat = docs["Settlements"].DocumentElement.SelectSingleNode("Settlement[@id='retirement_retreat']") as XmlElement;
        var mainComponent = towns.GroupBy(t => map.Group(t.GateX, t.GateY)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var all = fixedSettlements.Concat(r.Hideouts).ToList();
        double best = map.Cells(mainComponent).Max(c => all.Min(s => map.Distance(c.X, c.Y, s.X, s.Y)));
        Check(retreat != null && retreat.SelectSingleNode("Components/CustomSettlementComponent[@component_name='RetirementSettlementComponent'][@id='retirement_component']") != null &&
              CampaignIdentity.IsAlias("retirement_retreat") && CampaignIdentity.IsAlias("retirement_component") && map.Group(r.RetreatX, r.RetreatY) == mainComponent &&
              Math.Abs(all.Min(s => map.Distance(r.RetreatX, r.RetreatY, s.X, s.Y)) - best) < 1e-9 && retreat.GetAttribute("posX") == r.RetreatX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
              "the retirement retreat is the installed record re-created as a world alias at the passable cell of the main land group farthest (" + Math.Round(best, 1) + ") from every settlement and hideout");

        // c3 (offline part): every spawn anchor is passable land connected to a town.
        var kinds = L(O(startup, "spawns"), "kinds");
        Check(kinds.All(k => r.Anchors.Any(a => a.Kind == k)) && r.Anchors.All(a => a.Problem == null && map.Passable(a.X, a.Y) && townGroups.Contains(map.Group(a.X, a.Y))) && map.Conflicts.Count == 0,
              "all " + r.Anchors.Count + " spawn anchors are passable land in an MMR-28 land group with a town (land components joined by the compiler's groups): " + String.Join(", ", r.Anchors.GroupBy(a => a.Kind).Select(g => g.Key + " " + g.Count())));

        // The runtime plan for GeneratedStartupBehavior.
        Func<string, bool> template = id => installed("NPCCharacters", id);
        Check(r.Plan.Notables.Count == heroes.RuntimeHeroes.Count(h => h.Occupation != "Wanderer") && r.Plan.Companions.Count == heroes.RuntimeHeroes.Count(h => h.Occupation == "Wanderer") &&
              r.Plan.Notables.Concat(r.Plan.Companions).All(h => template(h.Template) && byId.ContainsKey(h.Settlement) && h.Age > 0 && !String.IsNullOrEmpty(h.Name)) &&
              r.Plan.Relations.Count == heroes.Relations.Count,
              "the runtime plan carries the " + r.Plan.Notables.Count + " MMR-32 notable intents, " + r.Plan.Companions.Count + " companion intents (installed templates, generated homes) and " + r.Plan.Relations.Count + " ruler relations");

        // The startup documents register with the world through the MMR-30 plan.
        Func<string, XmlDocument> empty = name => { var x = new XmlDocument(); x.AppendChild(x.CreateElement(name)); return x; };
        Func<string, IEnumerable<XmlDocument>, XmlDocument> merge = (name, parts) => { var x = empty(name); foreach (var p in parts.Where(p => p != null)) foreach (XmlNode n in p.DocumentElement.ChildNodes) x.DocumentElement.AppendChild(x.ImportNode(n, true)); return x; };
        var worldDocs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal) {
            ["SPCultures"] = docs["SPCultures"], ["WorkshopTypes"] = empty("WorkshopTypes"), ["LocationComplexTemplates"] = empty("LocationComplexTemplates"),
            ["NPCCharacters"] = heroes.Documents.Single(d => d.Key == "NPCCharacters").Value, ["Heroes"] = heroes.Documents.Single(d => d.Key == "Heroes").Value,
            ["Kingdoms"] = factions.Documents.Single(d => d.Key == "Kingdoms").Value,
            ["Factions"] = merge("Factions", new[] { factions.Documents.Single(d => d.Key == "Factions").Value, docs["Factions"] }),
            ["Settlements"] = merge("Settlements", new[] { world.Documents.Single(d => d.Key == "Settlements").Value, docs["Settlements"] }) };
        var plan = RegistrationPlan.FromDocuments(r.WorldPrefix, RegistrationPlan.NewGameOrder.Select(id => new KeyValuePair<string, XmlDocument>(id, worldDocs[id])).ToList(), false,
                                                  factions.DeclaredKeys.Concat(heroes.DeclaredKeys).Concat(world.DeclaredKeys).Concat(r.DeclaredKeys));
        var planProblems = plan.Problems((t, i) => false, (t, i) => resolver.Exists(t + "." + i)).ToList();
        Check(planProblems.Count == 0 && plan.Objects.Any(o => o.Type == "RetirementSettlementComponent" && o.Id == "retirement_component") &&
              plan.GeneratedObjects.Count(o => o.Type == "Hideout") == r.Hideouts.Count && plan.GeneratedObjects.Count(o => o.Type == "Faction" && CampaignIdentity.IsAlias(o.Id)) == clanIds.Count,
              "the world with its hideouts, bandit clans, retreat and start cultures plans through the MMR-30 registry with no problem, the hero family forward references included (two-pass since MMR-45) (" + planProblems.Count +
              (planProblems.Count == 0 ? "" : ": " + String.Join("; ", planProblems.Take(3))) + "); the retreat's component registers as RetirementSettlementComponent");

        // Determinism and refusals.
        var again = build(campaign, travel, startup);
        var shuffled = Copy(campaign); foreach (var key in new[] { "towns", "castles", "villages", "heroes", "kingdoms", "houses" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(again.IntentsSha256 == r.IntentsSha256 && build(shuffled, travel, startup).IntentsSha256 == r.IntentsSha256 &&
              again.Documents.Select(d => d.Value.OuterXml).SequenceEqual(r.Documents.Select(d => d.Value.OuterXml)),
              "building twice, or from reordered records, gives the same startup intents (" + r.IntentsSha256.Substring(0, 12) + ") and documents");
        var hungry = Copy(startup); O(hungry, "starts")["foodMinimum"] = 100000;
        var noRetreat = Copy(startup); O(noRetreat, "retreat")["settlement"] = "no_such_retreat";
        var flooded = Copy(travel);
        var seaCell = Enumerable.Range(0, map.Cols * map.Rows).First(i => !map.Passable((i % map.Cols + 0.5) * map.Spacing, (i / map.Cols + 0.5) * map.Spacing));
        // A village moved by at most two cells onto a non-land cell: still bound as before, but its spawn anchor is not passable.
        IDictionary<string, object> movedVillage = null; double movedX = 0, movedY = 0;
        foreach (var v in Records(flooded, "settlements").Where(s => S(s, "kind") == "village"))
        {
            double vx = D(O(v, "point"), "x"), vy = D(O(v, "point"), "y");
            for (int dr = -2; dr <= 2 && movedVillage == null; dr++)
                for (int dc = -2; dc <= 2 && movedVillage == null; dc++)
                {
                    double x = vx + dc * map.Spacing, y = vy + dr * map.Spacing;
                    if (y > 0 && y < map.Rows * map.Spacing && !map.Passable(x, y)) { movedVillage = v; movedX = (Math.Floor(x / map.Spacing) + 0.5) * map.Spacing; movedY = (Math.Floor(y / map.Spacing) + 0.5) * map.Spacing; }
                }
            if (movedVillage != null) break;
        }
        O(movedVillage, "point")["x"] = movedX; O(movedVillage, "point")["y"] = movedY;
        var sparse = Copy(startup); O(sparse, "hideouts")["hideoutSpacing"] = 1000;
        var closed = Copy(travel); foreach (var b in Records(closed, "basins")) b["open"] = false;
        var refusals = new[] { Refusal(() => build(campaign, travel, hungry)), Refusal(() => build(campaign, travel, noRetreat)), RefusalOfAny(() => build(campaign, flooded, startup)),
                               Refusal(() => build(campaign, travel, sparse)), Refusal(() => build(campaign, closed, startup)) };
        var expected = new[] { "no town qualifies as a start", "retirement retreat is missing", "not passable land", "fewer hideouts than required cultures", "sea basin is not open" };
        Check(refusals.Zip(expected, (m, e) => m != null && m.Contains(e)).All(x => x),
              "each is refused with an actionable reason: no town that can be a start, a missing installed retreat, a village spawn anchor moved onto a non-land cell, too few hideouts for the required bandit cultures (hideoutSpacing 1000), and every port in a closed basin (" +
              String.Join(" | ", refusals.Zip(expected, (m, e) => e + ": " + (m != null && m.Contains(e) ? "refused" : "NOT refused (" + m + ")"))) + ")");
        // The refusals are bounded quality rejections: each carries startup.json quality codes, retryable only when every code is.
        var codes = O(O(startup, "quality"), "codes");
        var defined = typeof(GeneratedCampaignInitializer).GetFields().Where(f => f.IsLiteral && f.Name.StartsWith("Code", StringComparison.Ordinal)).Select(f => (string)f.GetRawConstantValue()).ToList();
        var coded = new[] { Refused(() => build(campaign, travel, hungry)), Refused(() => build(campaign, travel, noRetreat)), Refused(() => build(campaign, travel, sparse)), Refused(() => build(campaign, closed, startup)) };
        var expectedCodes = new[] { new[] { "quality.startup_no_start" }, new[] { "quality.startup_catalog" }, new[] { "quality.startup_hideouts" }, new[] { "quality.startup_no_start" } };
        var expectedRetry = new[] { true, false, true, true };
        Check(defined.Count == 8 && defined.All(codes.ContainsKey) && codes.Count == defined.Count &&
              coded.Select((e, i) => e != null && e.Codes.SequenceEqual(expectedCodes[i]) && e.Retryable == expectedRetry[i] && e.Message.Contains("[" + expectedCodes[i][0] + "]")).All(x => x),
              "every refusal carries quality codes from startup.json (" + String.Join(", ", defined) + "): " +
              String.Join("; ", coded.Select((e, i) => expected[i == 2 ? 3 : i == 3 ? 4 : i] + " -> " + (e == null ? "none" : String.Join(",", e.Codes) + (e.Retryable ? " (retryable)" : " (not retryable)")))));

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.startup", ["version"] = 1, ["ticket"] = "MMR-34", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["inventory"] = inventory["counts"], ["worldDependentTypes"] = worldDependent.Count, ["templateLookups"] = lookups.Count,
            ["starts"] = r.Starts.Select(s => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["culture"] = s.Culture, ["town"] = s.Town, ["food"] = s.Food, ["notables"] = s.Notables,
                ["seaDestination"] = s.SeaDestination, ["basin"] = s.Basin, ["fallback"] = s.Fallback, ["reason"] = s.Reason }).ToList(),
            ["hideouts"] = r.Hideouts.GroupBy(h => h.Culture).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()), ["hideoutNotes"] = r.Notes,
            ["banditHomes"] = r.BanditHomes, ["retreat"] = new[] { r.RetreatX, r.RetreatY }, ["anchors"] = r.Anchors.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count()),
            ["plan"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["notables"] = r.Plan.Notables.Count, ["companions"] = r.Plan.Companions.Count, ["relations"] = r.Plan.Relations.Count },
            ["documents"] = r.Documents.ToDictionary(d => d.Key, d => Sha(Encoding.UTF8.GetBytes(d.Value.OuterXml))), ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static string byIdOrHideout(GeneratedCampaignInitializer.Result r, string id) { var h = r.Hideouts.FirstOrDefault(x => x.Id == id); return h == null ? id : h.Culture; }

    // A refusal from any factory on the way (a village moved onto a non-land cell may be refused by the settlement factory first).
    private static string RefusalOfAny(Func<object> build)
    {
        try { build(); return null; }
        catch (GeneratedCampaignInitializer.StartupException e) { return e.Message; }
        catch (SettlementFactory.SettlementException e) { return e.Message; }
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
