using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;

// MMR-33 settlement tests, without the game: SettlementTests BUNDLE_DIR REPO_DIR OUT_JSON
// The compiler records, travel graph and ports come from the real MMR-29 bundle; factions and heroes are built by the accepted MMR-31
// and MMR-32 factories; the economy policy, prototypes and the installed catalog (hash-checked) from the repository. Every bound the
// factory enforces is recomputed here from the policy and the inputs, not read back from the factory.
internal static class SettlementTests
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
    private static int I(IDictionary<string, object> o, string key) { return Convert.ToInt32(o[key]); }
    private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<IDictionary<string, object>>(); }
    private static List<string> L(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x)).ToList() : new List<string>(); }

    private static string Refusal(Func<object> build)
    {
        try { build(); return null; }
        catch (SettlementFactory.SettlementException e) { return e.Message; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: SettlementTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
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
        var rosters = Load(Path.Combine(repo, "worldgen/policies/rosters.json"));
        var populationPolicy = Load(Path.Combine(repo, "worldgen/policies/population.json"));
        var economy = Load(Path.Combine(repo, "worldgen/policies/economy.json"));
        var prototypes = Load(Path.Combine(repo, "worldgen/templates/settlements/prototypes.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Load(f), StringComparer.Ordinal);
        var receipt = Load(Path.Combine(repo, S(O(rosters, "provenance"), "catalogReceipt")));
        var catalog = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var d in Records(O(receipt, "outputs"), "documents").Where(d => CharacterTemplateResolver.Documents.Contains(S(d, "id")) || S(d, "id") == "Settlements" || S(d, "id") == "LocationComplexTemplates"))
        {
            var bytes = File.ReadAllBytes(S(d, "path"));
            if (Sha(bytes) != S(d, "sha256")) throw new InvalidOperationException("catalog " + S(d, "id") + " differs from its receipt");
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); catalog[S(d, "id")] = doc;
        }
        if (Sha(File.ReadAllBytes(S(Records(O(receipt, "outputs"), "documents").Single(d => S(d, "id") == "Settlements"), "path"))) != S(O(economy, "provenance"), "settlementsSha256") ||
            S(O(prototypes, "provenance"), "settlementsSha256") != S(O(economy, "provenance"), "settlementsSha256")) throw new InvalidOperationException("economy policy or prototypes were made from another Settlements catalog");
        var defaultsRun = Load(Path.Combine(repo, S(O(rosters, "provenance"), "defaultReferences")));
        var defaults = L(O(Records(O(defaultsRun, "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(rosters, "troops"));
        Func<IDictionary<string, object>, FactionFactory.Result> factionsOf = c => FactionFactory.Build(c, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates, Load(Path.Combine(repo, "worldgen/policies/population.json")));
        Func<IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, SettlementFactory.Result> build = (c, t, p, e) => {
            var f = factionsOf(c); var h = HeroFactory.Build(c, idMap, f, rosters, resolver, populationPolicy);
            return new SettlementFactory(e, prototypes, catalog["Settlements"], resolver.Exists).Build(c, t, p, f, h); };
        var factions = factionsOf(campaign); var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, resolver, populationPolicy);
        var r = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes);

        var towns = Records(campaign, "towns").ToList(); var castles = Records(campaign, "castles").ToList(); var villages = Records(campaign, "villages").ToList();
        var byId = r.Settlements.ToDictionary(s => s.Id);
        var doc2 = r.Documents.Single(d => d.Key == "Settlements").Value;
        var elements = doc2.DocumentElement.ChildNodes.OfType<XmlElement>().ToDictionary(e => e.GetAttribute("id"));
        var clans = factions.Clans.ToDictionary(c => c.Id);
        var protos = O(prototypes, "cultures");
        Func<string, string, IEnumerable<string>> protoIds = (culture, kind) => protos.ContainsKey(culture) && O(protos, culture).ContainsKey(kind) ?
            ((IEnumerable)O(protos, culture)[kind]).Cast<IDictionary<string, object>>().Select(x => S(x, "id")) : new string[0];
        var grid = O(travel, "grid"); double cell = D(grid, "spacing"), width = I(grid, "cols") * cell;
        Func<double, double, double, double, double> dist = (x1, y1, x2, y2) => { double dx = Math.Abs(x1 - x2); dx = Math.Min(dx, width - dx); return Math.Sqrt(dx * dx + (y1 - y2) * (y1 - y2)); };
        Func<SettlementFactory.SettlementIntent, SettlementFactory.SettlementIntent, double> between = (a, b) => dist(a.X, a.Y, b.X, b.Y);
        var vIntents = r.Settlements.Where(s => s.Kind == "village").ToList();
        var tIntents = r.Settlements.Where(s => s.Kind == "town").ToList();
        var forts = r.Settlements.Where(s => s.Kind == "town" || s.Kind == "castle").ToList();

        // c1: components, owners, cultures, buildings and mission locations.
        Check(r.Settlements.Count(s => s.Kind == "town") == towns.Count && r.Settlements.Count(s => s.Kind == "castle") == castles.Count && vIntents.Count == villages.Count &&
              towns.All(t => elements[S(t, "id")].SelectSingleNode("Components/Town[@is_castle='false']") != null) && castles.All(c => elements[S(c, "id")].SelectSingleNode("Components/Town[@is_castle='true']") != null) &&
              villages.All(v => elements[S(v, "id")].SelectSingleNode("Components/Village") != null) && elements.Values.All(e => e.SelectNodes("Components/*").Count == 1),
              "every one of " + towns.Count + " towns, " + castles.Count + " castles and " + villages.Count + " villages is a settlement with exactly its component (Town, castle Town, Village)");
        var castellanOf = castles.ToDictionary(c => S(c, "id"), c => Records(campaign, "heroes").Where(h => S(h, "uid") == "hero-castellan-" + S(c, "uid")).Select(h => S(h, "id")).FirstOrDefault());
        var castellanCastles = castellanOf.Where(x => x.Value != null && factions.Clans.Any(c => !c.Ruling && c.LeaderId == x.Value)).Select(x => x.Key).ToList();
        Check(r.Settlements.Where(s => s.Kind != "village").All(s => clans.ContainsKey(s.Owner) && clans[s.Owner].KingdomId == s.Kingdom && elements[s.Id].GetAttribute("owner") == "Faction." + s.Owner) &&
              castellanCastles.All(id => byId[id].Owner == factions.Clans.Single(c => !c.Ruling && c.LeaderId == castellanOf[id]).Id) &&
              castles.Where(c => !castellanCastles.Contains(S(c, "id"))).All(c => byId[S(c, "id")].Owner == S(c, "owner")) &&
              towns.All(t => byId[S(t, "id")].Owner == S(t, "owner")) && vIntents.All(s => !elements[s.Id].HasAttribute("owner")),
              "every town and castle is owned by a clan intent of its own kingdom; each of the " + castellanCastles.Count + " castles whose castellan leads a vassal clan is owned by exactly that clan, the others by the compiler's owner; villages take their owner from their bound settlement");
        Check(towns.All(t => byId[S(t, "id")].Culture == S(t, "presentation")) && r.Settlements.Where(s => s.Kind != "town").All(s => s.Culture == heroes.SettlementCultures[s.Id]) &&
              r.Settlements.All(s => elements[s.Id].GetAttribute("culture") == "Culture." + s.Culture),
              "towns take their MMR-27 presentation and castles and villages the MMR-32 settlement culture the hero and garrison slots used");
        Func<SettlementFactory.SettlementIntent, string> kindOf = s => s.Kind == "town" ? (s.Port || s.AdaptedPrototype ? "town_port" : "town_inland") :
            s.Kind == "village" ? ((s.Port != s.AdaptedPrototype) ? "village_coastal" : "village_inland") : s.Kind;
        Check(r.Settlements.All(s => protoIds(s.Culture, kindOf(s)).Contains(s.Prototype)) &&
              r.Settlements.All(s => !elements[s.Id].HasAttribute("text") || elements[s.Id].GetAttribute("text").StartsWith("{=" + s.Id + "_text}")) &&
              r.Settlements.All(s => resolver.Element("Culture." + s.Culture) != null),
              "every settlement reuses an installed prototype of its culture and kind (port towns a port prototype, harbour villages a coastal one; " + r.Settlements.Count(s => s.AdaptedPrototype) +
              " settlements of a culture without their kind adapt the other) and none copies the prototype's lore text");
        var townBuildings = O(O(O(economy, "buildings"), "towns"), "small").Keys.ToList();
        var castleBuildings = O(O(economy, "buildings"), "castles").Keys.ToList();
        var tiers = new[] { "small", "medium", "capital" };
        Check(tIntents.All(s => townBuildings.All(b => elements[s.Id].SelectSingleNode("Components/Town/Buildings/Building[@id='" + b + "']") != null) &&
                  (elements[s.Id].SelectSingleNode("Components/Town/Buildings/Building[@id='building_shipyard']") != null) == s.Port &&
                  townBuildings.All(b => b == "building_settlement_fortifications" || s.Buildings[b] == I(O(O(O(economy, "buildings"), "towns"), s.CityClass), b))) &&
              r.Settlements.Where(s => s.Kind == "castle").All(s => castleBuildings.All(b => elements[s.Id].SelectSingleNode("Components/Town/Buildings/Building[@id='" + b + "']") != null)) &&
              elements.Values.SelectMany(e => e.SelectNodes(".//Building").OfType<XmlElement>()).All(b => resolver.Exists("BuildingType." + b.GetAttribute("id"))) &&
              townBuildings.All(b => I(O(O(O(economy, "buildings"), "towns"), "small"), b) <= I(O(O(O(economy, "buildings"), "towns"), "medium"), b) &&
                                     I(O(O(O(economy, "buildings"), "towns"), "medium"), b) <= I(O(O(O(economy, "buildings"), "towns"), "capital"), b)),
              "towns have all " + townBuildings.Count + " installed town buildings at their class tier's levels (never lower for a larger class) and a shipyard exactly when they have a port, castles all " +
              castleBuildings.Count + " castle buildings, every one a registered building type");        // MMR-258 (RT-14): the initial wall level (the fortifications building) is drawn from the installed levels of the settlement's prosperity
        // tercile (economy.json walls: towns by class, castles by prosperity rank), never a level its tercile lacks, and not every wall at level 1.
        var wallsPolicy = O(economy, "walls");
        var castlesRanked = r.Settlements.Where(x => x.Kind == "castle").OrderBy(x => x.Prosperity).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        Func<SettlementFactory.SettlementIntent, string> wallTier = x => x.Kind == "town" ? x.CityClass : castlesRanked.IndexOf(x) < castlesRanked.Count / 3 ? "low" : castlesRanked.IndexOf(x) < 2 * castlesRanked.Count / 3 ? "middle" : "high";
        Func<SettlementFactory.SettlementIntent, int> wallLevel = x => x.Buildings[x.Kind == "town" ? "building_settlement_fortifications" : "building_castle_fortifications"];
        var fortified = r.Settlements.Where(x => x.Kind == "town" || x.Kind == "castle").ToList();
        Check(fortified.All(x => { int level = wallLevel(x); object weight; return O(O(wallsPolicy, x.Kind == "town" ? "towns" : "castles"), wallTier(x)).TryGetValue(level.ToString(CultureInfo.InvariantCulture), out weight) && Convert.ToInt32(weight, CultureInfo.InvariantCulture) > 0; })
              && fortified.Count(x => wallLevel(x) > 1) > 0,
              "initial wall levels are drawn from the installed distribution of each settlement's prosperity tercile (RT-14): " + String.Join(", ", fortified.GroupBy(wallLevel).OrderBy(g => g.Key).Select(g => g.Count() + " at level " + g.Key)) + " of " + fortified.Count + ", not all level 1");
        // MMR-258: the village types the scene's look plan reads before the campaign is built (PlanVillageTypes, from the records alone) are the
        // records' types, floors and food pass included.
        var planned = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).PlanVillageTypes(campaign, travel, ports);
        var builtTypes = r.Settlements.Where(x => x.Kind == "village").ToDictionary(x => x.Id, x => x.VillageType, StringComparer.Ordinal);
        Check(planned.Count == builtTypes.Count && planned.All(kv => builtTypes.ContainsKey(kv.Key) && builtTypes[kv.Key] == kv.Value),
              "the look plan's village types (SettlementFactory.PlanVillageTypes, without factions or heroes) are the records' for all " + planned.Count + " villages (" + r.Settlements.Count(x => x.FloorRedraw != null || x.FoodRedraw != null) + " redrawn by the floors or the food pass)");

        var complexes = catalog["LocationComplexTemplates"].DocumentElement.ChildNodes.OfType<XmlElement>().ToDictionary(e => "LocationComplexTemplate." + e.GetAttribute("id"));
        Func<XmlElement, bool> locationsResolve = e => {
            var l = e.SelectSingleNode("Locations") as XmlElement; if (l == null) return false;
            XmlElement complex; if (!complexes.TryGetValue(l.GetAttribute("complex_template"), out complex)) return false;
            var known = new HashSet<string>(complex.SelectNodes(".//Location").OfType<XmlElement>().Select(x => x.GetAttribute("id")));
            return l.SelectNodes("Location").OfType<XmlElement>().All(x => known.Contains(x.GetAttribute("id"))) && l.SelectNodes("Location[@scene_name or @scene_name_1]").Count > 0; };
        Check(elements.Values.All(locationsResolve) && tIntents.All(s => (elements[s.Id].SelectSingleNode("Locations/Location[@id='port']") != null) == s.Port),
              "every settlement's location complex template exists and each of its locations is one the template defines and has scenes; the template gives every town a port location (LocationComplex.Initialize), and only port towns keep their prototype's port scene override, as the installed inland towns");

        // c2: binding (distance, castles, balance), production, food, markets and starting values.
        var binding = O(economy, "binding"); var distance = O(binding, "distance");
        double spacing = tIntents.Average(t => tIntents.Where(u => u != t).Min(u => between(t, u)));
        double preferred = spacing * D(distance, "preferredRatio"), engine = spacing * D(distance, "engineTradeRatio");
        Func<string, int> count = id => vIntents.Count(v => v.Bound == id);
        var compilerFar = villages.Select(v => dist(D(O(Records(travel, "settlements").Single(x => S(x, "id") == S(v, "id")), "point"), "x"), D(O(Records(travel, "settlements").Single(x => S(x, "id") == S(v, "id")), "point"), "y"),
                                                     byId[S(v, "bound")].X, byId[S(v, "bound")].Y) / spacing).Max();
        var remote = vIntents.Where(v => v.Remote).ToList();
        Check(Math.Abs(spacing - r.Spacing) < 1e-6 &&
              vIntents.All(s => byId.ContainsKey(s.Bound) && byId[s.Bound].Kind != "village" && byId[s.Bound].LandGroup == s.LandGroup && Math.Abs(between(s, byId[s.Bound]) - s.BoundDistance) < 1e-6) &&
              villages.All(v => byId[S(v, "id")].Bound == S(v, "bound") && byId[S(v, "bound")].Kind == S(v, "boundKind") && byId[S(v, "id")].SourceBound == S(v, "sourceTown")) && r.Rebound.Count == 0 &&
              vIntents.Where(v => !v.Remote).All(v => v.BoundDistance <= preferred) && vIntents.All(v => v.BoundDistance <= engine) && remote.All(v => v.BoundDistance > preferred),
              "every village keeps its campaign v2 binding (the village quota, MMR-252; VillageBinding passes it through and rebinds none) to a town or castle of its land group; " +
              (vIntents.Count - remote.Count) + " are within " + D(distance, "preferredRatio") + " x the town spacing " + Math.Round(spacing, 1) + " (" + Math.Round(preferred, 1) + "; installed maximum " + D(distance, "installedMaxRatio") +
              " x; farthest " + Math.Round(compilerFar, 2) + " x) and the " + remote.Count + " remote ones stay within the engine's trade limit " + Math.Round(engine, 1));
        var quota = O(campaign, "villageQuota");
        Check(r.Settlements.Where(s => s.Kind == "castle").All(s => count(s.Id) == I(binding, "castleVillages")) && tIntents.All(s => count(s.Id) == I(binding, "townVillages")) &&
              forts.All(f => count(f.Id) > 0) && I(quota, "townVillages") == I(binding, "townVillages") && I(quota, "castleVillages") == I(binding, "castleVillages") && I(quota, "passes") == 2 && (bool)quota["stable"],
              "every town has exactly " + I(binding, "townVillages") + " and every castle exactly " + I(binding, "castleVillages") + " villages, so no fortification has none (SnareTheWealthy, incident_the_quiet_life and incident_wages_of_war_and_diseases throw on one): " +
              Json.Serialize(r.Balance["villagesPerTown"]) + " per town, " + Json.Serialize(r.Balance["villagesPerCastle"]) + " per castle; the quota kept " + I(quota, "kept") + ", reassigned " + I(quota, "reassigned") +
              ", dropped " + I(quota, "dropped") + " and synthesized " + I(quota, "synthesized") + " (stable over its two passes)");
        // RT-10: hearth from the source town's rural population, shared among its villages; castle villages are not all at the minimum.
        var rural = towns.ToDictionary(t => S(t, "id"), t => Convert.ToDouble(O(t, "population")["rural"]));
        var perSource = villages.GroupBy(v => S(v, "sourceTown")).ToDictionary(g => g.Key, g => g.Count());
        var shares = villages.ToDictionary(v => S(v, "id"), v => rural[S(v, "sourceTown")] / perSource[S(v, "sourceTown")]);
        var hearthValues = L(O(economy, "hearth"), "values").Select(int.Parse).OrderBy(x => x).ToList();
        Func<double, int> quantileAt = x => { int below = shares.Values.Count(v => v < x), equal = shares.Values.Count(v => v == x);
            double pct = shares.Count <= 1 ? 0.5 : (below + (equal - 1) / 2.0) / (shares.Count - 1);
            return hearthValues[(int)Math.Round(Math.Max(0, Math.Min(1, pct)) * (hearthValues.Count - 1), MidpointRounding.AwayFromZero)]; };
        var castleHearths = vIntents.Where(v => byId[v.Bound].Kind == "castle").Select(v => v.Hearth).OrderBy(x => x).ToList();
        var townHearths = vIntents.Where(v => byId[v.Bound].Kind == "town").Select(v => v.Hearth).OrderBy(x => x).ToList();
        Check(vIntents.All(v => v.Hearth == quantileAt(shares[v.Id])) && castleHearths.Count > 0 && castleHearths.Distinct().Count() > 1 &&
              castleHearths[castleHearths.Count / 2] >= townHearths[townHearths.Count / 10] && castleHearths.Count(h => h == hearthValues[0]) * 4 < castleHearths.Count,
              "village hearth is the installed quantile at its share of its source town's rural population (sourceTown; the garrison town for a castle's synthesized village, RT-10): castle villages' hearth median " +
              castleHearths[castleHearths.Count / 2] + " (" + castleHearths.Distinct().Count() + " values, " + castleHearths.Count(h => h == hearthValues[0]) + " of " + castleHearths.Count + " at the minimum " + hearthValues[0] +
              ") against town villages' 10th percentile " + townHearths[townHearths.Count / 10] + " and quartiles " + townHearths[townHearths.Count / 4] + "/" + townHearths[townHearths.Count / 2] + "/" + townHearths[3 * townHearths.Count / 4] +
              " (a castle's villages share their garrison town's rural population with the town's own)");
        // RT-08: coastal villages draw a fishing type at the installed coastal share; every culture keeps its horse-ranch and wheat floors.
        var mix = O(economy, "villageMix");
        var coastal = vIntents.Where(v => v.Port && O(O(economy, "villageTypes"), "pools").ContainsKey(v.Culture) &&
                                          O(O(O(economy, "villageTypes"), "pools"), v.Culture).Values.Cast<IDictionary<string, object>>().Any(pool => pool.Keys.Any(t => L(O(economy, "villageTypes"), "fishingTypes").Contains(t)))).ToList();
        double fishedShare = coastal.Count == 0 ? 0 : coastal.Count(v => L(O(economy, "villageTypes"), "fishingTypes").Contains(v.FloorRedraw ?? v.FoodRedraw ?? v.VillageType)) / (double)coastal.Count;
        var floorCounts = (IDictionary<string, object>)((IDictionary<string, object>)r.Balance["villageMix"])["floors"];
        Check(coastal.Count > 0 && Math.Abs(fishedShare - D(mix, "coastalFishingShare")) <= 0.2 &&
              floorCounts.All(f => I(O(floorCounts, f.Key), "horseRanch") >= I(O(floorCounts, f.Key), "horseRanchFloor") && I(O(floorCounts, f.Key), "wheat") >= I(O(floorCounts, f.Key), "wheatFloor")) &&
              floorCounts.Where(f => O(O(O(economy, "villageTypes"), "pools"), f.Key).Values.Cast<IDictionary<string, object>>().Any(pool => pool.Keys.Any(t => t.EndsWith("_horse_ranch")))).All(f => I(O(floorCounts, f.Key), "horseRanchFloor") >= 1),
              "the " + coastal.Count + " coastal villages of cultures with fishing types drew a fishing type at " + Math.Round(fishedShare, 3) + " (the installed coastal share " + D(mix, "coastalFishingShare") +
              "), and every culture keeps its horse-ranch and wheat floors after the food pass (" + String.Join(", ", floorCounts.Select(f => f.Key + " " + I(O(floorCounts, f.Key), "horseRanch") + "/" + I(O(floorCounts, f.Key), "horseRanchFloor") +
              " ranch, " + I(O(floorCounts, f.Key), "wheat") + "/" + I(O(floorCounts, f.Key), "wheatFloor") + " wheat")) + "; " + vIntents.Count(v => v.FloorRedraw != null) + " floor redraws)");
        var castleVillages = vIntents.Where(v => byId[v.Bound].Kind == "castle").ToList();
        Check(castleVillages.All(v => v.TradeTown != null && byId[v.TradeTown].Kind == "town" && byId[v.TradeTown].Kingdom == v.Kingdom && between(v, byId[v.TradeTown]) <= engine &&
                                      tIntents.Where(t => t.Kingdom == v.Kingdom).All(t => between(v, t) >= between(v, byId[v.TradeTown]))),
              "each of the " + castleVillages.Count + " castle villages has a town of its kingdom within the engine's trade limit (VillageTradeBoundCampaignBehavior assigns the nearest; farthest " +
              (castleVillages.Count == 0 ? 0 : Math.Round(castleVillages.Max(v => between(v, byId[v.TradeTown])), 1)) + " of " + Math.Round(engine, 1) + ")");
        var balance = O(economy, "balance"); var band = L(balance, "band").Select(double.Parse).ToList();
        int fiefs = towns.Count + castles.Count;
        double perFief = vIntents.Count / (double)fiefs / D(O(balance, "installed"), "villagesPerFief"), hearthFief = vIntents.Sum(v => v.Hearth) / (double)fiefs / D(O(balance, "installed"), "hearthPerFief");
        Check(perFief >= band[0] && perFief <= band[1] && hearthFief >= band[0] && hearthFief <= band[1],
              "the smaller world's balance is inside the policy band " + band[0] + "-" + band[1] + " x the installed values: villages per fief " + Math.Round(vIntents.Count / (double)fiefs, 2) + " (" + Math.Round(perFief, 2) +
              " x), hearth per fief " + Math.Round(vIntents.Sum(v => v.Hearth) / (double)fiefs, 1) + " (" + Math.Round(hearthFief, 2) + " x); the source fixes the village and fief counts");
        var food = O(economy, "food"); var foodOf = O(food, "villageTypes");
        Func<string, int> supplyOf = t => vIntents.Where(v => v.Bound == t || v.TradeTown == t).Sum(v => I(foodOf, v.VillageType));
        var supplies = tIntents.Select(t => supplyOf(t.Id)).OrderBy(x => x).ToList();
        var redrawn = vIntents.Where(v => v.FoodRedraw != null).ToList();
        Check(supplies.All(x => x >= I(food, "townMinimum")) && redrawn.All(v => I(foodOf, v.VillageType) > I(foodOf, v.FoodRedraw)),
              "every town's trade-area food supply reaches the policy minimum " + I(food, "townMinimum") + " (installed minimum " + I(O(food, "installed"), "minimum") + ", median " + I(O(food, "installed"), "median") +
              "): generated minimum " + supplies[0] + ", median " + supplies[supplies.Count / 2] + "; " + redrawn.Count + " village types were redrawn for food (" + String.Join(", ", redrawn.Select(v => v.FoodRedraw + ">" + v.VillageType)) + ")");
        var pools = O(O(economy, "villageTypes"), "pools");
        var fishing = new HashSet<string>(L(O(economy, "villageTypes"), "fishingTypes"));
        Func<SettlementFactory.SettlementIntent, string, bool> inPools = (s, type) => (O(pools, s.Culture).ContainsKey(s.Production) && O(O(pools, s.Culture), s.Production).ContainsKey(type)) ||
            (O(pools, s.Culture).ContainsKey("farming") && O(O(pools, s.Culture), "farming").ContainsKey(type)) || (!O(pools, s.Culture).ContainsKey(s.Production) && O(pools, s.Culture).Values.Cast<IDictionary<string, object>>().Any(p => p.ContainsKey(type)));
        Check(vIntents.All(s => resolver.Exists("VillageType." + s.VillageType) && inPools(s, s.VillageType) && (s.Port || !fishing.Contains(s.VillageType))),
              "every village type is a registered type from its culture's installed types of its production class (or its farming types), and a fishing type only with a port (" +
              String.Join(", ", vIntents.GroupBy(s => s.Production).Select(g => g.Key + " " + g.Count())) + "; " + vIntents.Count(s => fishing.Contains(s.VillageType)) + " fishing-type villages, all with ports)");
        var harbourCells = new Dictionary<string, List<int>>();
        foreach (var p in Records(ports, "ports")) foreach (var c in Records(p, "candidates")) if (c.ContainsKey("harbourCell")) harbourCells[S(c, "village")] = ((IEnumerable)c["harbourCell"]).Cast<object>().Select(Convert.ToInt32).ToList();
        var villagePorts = O(O(economy, "positions"), "villagePorts");
        double envelope = D(villagePorts, "installedPortCentreMax") + D(villagePorts, "allowance");
        var harbourIds = villages.Where(v => (bool)v["harbour"]).Select(v => S(v, "id")).ToList();
        Check(harbourIds.All(id => byId[id].Port && harbourCells.ContainsKey(id) && byId[id].PortX == (harbourCells[id][1] + 0.5) * cell && byId[id].PortY == (harbourCells[id][0] + 0.5) * cell &&
                                   dist(byId[id].PortX.Value, byId[id].PortY.Value, byId[id].X, byId[id].Y) <= envelope &&
                                   elements[id].GetAttribute("port_posX") == byId[id].PortX.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) &&
              vIntents.Where(v => !harbourIds.Contains(v.Id)).All(v => !v.Port && !elements[v.Id].HasAttribute("port_posX")),
              "each of the " + harbourIds.Count + " MMR-27 harbour villages has its port at the centre of its MMR-28 harbour cell, within the installed village port-centre maximum " + D(villagePorts, "installedPortCentreMax") +
              " plus one cell (farthest " + Math.Round(harbourIds.Max(id => dist(byId[id].PortX.Value, byId[id].PortY.Value, byId[id].X, byId[id].Y)), 2) + ", periodic), and no other village has one");
        var townRange = new[] { "small", "capital" }.SelectMany(k => L(O(O(economy, "prosperity"), "towns"), k)).Select(int.Parse).ToList();
        var castleRange = L(O(O(economy, "prosperity"), "castles"), "values").Select(int.Parse).ToList();
        var hearthRange = L(O(economy, "hearth"), "values").Select(int.Parse).ToList();
        Func<IDictionary<string, object>, double> population = t => Convert.ToDouble(O(t, "population")["estimate"]);
        bool monotone = towns.GroupBy(t => S(t, "cityClass")).All(g => g.OrderBy(population).Select(t => byId[S(t, "id")].Prosperity).Zip(g.OrderBy(population).Skip(1).Select(t => byId[S(t, "id")].Prosperity), (a, b) => a <= b).All(x => x));
        Check(tIntents.All(s => s.Prosperity >= townRange.Min() && s.Prosperity <= townRange.Max()) &&
              r.Settlements.Where(s => s.Kind == "castle").All(s => s.Prosperity >= castleRange.Min() && s.Prosperity <= castleRange.Max()) &&
              vIntents.All(s => s.Hearth >= hearthRange.Min() && s.Hearth <= hearthRange.Max()) && monotone &&
              towns.GroupBy(t => S(t, "cityClass")).All(g => g.All(t => L(O(O(economy, "prosperity"), "towns"), S(t, "cityClass")).Select(int.Parse).Contains(byId[S(t, "id")].Prosperity))),
              "prosperity and hearth stay in the installed ranges (towns " + townRange.Min() + "-" + townRange.Max() + " by class tercile, castles " + castleRange.Min() + "-" + castleRange.Max() + ", hearth " + hearthRange.Min() + "-" + hearthRange.Max() +
              ") and grow with population within a class: means " + r.Balance["meanTownProsperity"] + " / " + r.Balance["meanCastleProsperity"] + " / " + r.Balance["meanHearth"]);
        var scaled = Copy(campaign);
        foreach (var t in Records(scaled, "towns")) { var p = O(t, "population"); foreach (var k in p.Keys.ToList()) if (p[k] is int) p[k] = (int)p[k] * 1000; }
        var scaledResult = build(scaled, travel, ports, economy);
        Check(scaledResult.Settlements.Zip(r.Settlements, (a, b) => a.Id == b.Id && a.Prosperity == b.Prosperity && a.Hearth == b.Hearth).All(x => x),
              "multiplying every town's population by 1000 changes no prosperity or hearth: population only orders settlements, the installed distributions bound the values");
        var owned = new List<KeyValuePair<string, string>>();
        foreach (var kind in new[] { "towns", "villages" })
            foreach (var rec in Records(campaign, kind))
                foreach (var slot in ((IEnumerable)rec["slots"]).Cast<IDictionary<string, object>>().Where(x => S(x, "owner") == "MMR-33")) owned.Add(new KeyValuePair<string, string>(S(rec, "id"), S(slot, "name")));
        Check(owned.Count == towns.Count + 2 * villages.Count && owned.All(o => r.Slots.Count(x => x.Owner == o.Key && x.Name == o.Value && x.Value != null && x.Value.Count > 0) == 1),
              "all " + owned.Count + " MMR-33-owned compiler slots are resolved: town prosperityAndEconomy, village villageType and hearth");

        // Positions (staged) and XML order for the registry.
        var portMax = Convert.ToDouble(O(travel, "engine")["installedPortGateMax"]);
        var portTowns = tIntents.Where(s => s.Port).ToList();
        // Periodic distance: a port across the seam column from its town is one cell away, not a world width (MMR-251 sweep, seeds 43 and 44).
        double seamWidth = Convert.ToDouble(O(travel, "grid")["cols"]) * Convert.ToDouble(O(travel, "grid")["spacing"]);
        Func<double, double, double, double, double> gap = (x1, y1, x2, y2) => { double dx = Math.Abs(x1 - x2); dx = Math.Min(dx, seamWidth - dx); return Math.Sqrt(dx * dx + (y1 - y2) * (y1 - y2)); };
        Check(r.Settlements.All(s => !double.IsNaN(s.X) && s.GateX == s.X && s.GateY == s.Y) && portTowns.All(s => s.PortX.HasValue && gap(s.PortX.Value, s.PortY.Value, s.GateX, s.GateY) <= portMax + cell) &&
              portTowns.Count == towns.Count(t => (bool)t["port"]) && r.Settlements.Where(s => !s.Port).All(s => !elements[s.Id].HasAttribute("port_posX")),
              "every settlement has its staged MMR-28 centre, and gate at the centre until MMR-40 places gates; the " + portTowns.Count + " port towns have their port candidate's sea point within the installed port-gate maximum " + portMax +
              " plus one cell of that staged gate (farthest " + Math.Round(portTowns.Max(s => gap(s.PortX.Value, s.PortY.Value, s.GateX, s.GateY)), 2) + "; the installed port-centre maximum " +
              D(O(economy, "positions"), "installedTownPortCentreMax") + " is not reached by " + portTowns.Count(s => gap(s.PortX.Value, s.PortY.Value, s.X, s.Y) > D(O(economy, "positions"), "installedTownPortCentreMax")) + " until gates are placed)");
        var order = doc2.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => e.GetAttribute("id")).ToList();
        Check(vIntents.All(s => order.IndexOf(s.Bound) < order.IndexOf(s.Id)) &&
              r.DeclaredKeys.Count == 2 * r.Settlements.Count && r.Settlements.All(s => CampaignIdentity.Problem(s.Id, r.WorldPrefix) == null),
              "the Settlements XML lists every bound town or castle before its villages (the MMR-30 registry's ReadyAt rule) and declares each settlement and its component");
        // The whole generated world through the MMR-30 plan: every document the factories emit, in the proven order.
        Func<string, XmlDocument> empty = name => { var x = new XmlDocument(); x.AppendChild(x.CreateElement(name)); return x; };
        var worldDocs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal) { ["SPCultures"] = empty("SPCultures"), ["WorkshopTypes"] = empty("WorkshopTypes"), ["LocationComplexTemplates"] = empty("LocationComplexTemplates") };
        foreach (var d in factions.Documents.Concat(heroes.Documents).Concat(r.Documents)) worldDocs[d.Key] = d.Value;
        var plan = RegistrationPlan.FromDocuments(r.WorldPrefix, RegistrationPlan.NewGameOrder.Select(id => new KeyValuePair<string, XmlDocument>(id, worldDocs[id])).ToList(), false,
                                                  factions.DeclaredKeys.Concat(heroes.DeclaredKeys).Concat(r.DeclaredKeys));
        var allProblems = plan.Problems((t, i) => false, (t, i) => resolver.Exists(t + "." + i));
        // Hero family references (spouse, father, mother) to a hero later in the document: the installed Heroes.xml has the same kind (127 of
        // them), which the engine resolves through presumed objects; MMR-45 added them to the two-pass list (installed IL), so none remains.
        var familyForward = new System.Text.RegularExpressions.Regex(@"^early reference Hero\.[^ ]+\.(spouse|father|mother) -> Hero\.[^ ]+: a forward reference outside the proven two-pass pattern");
        var heroFamily = allProblems.Where(x => familyForward.IsMatch(x)).ToList();
        var planProblems = allProblems.Where(x => !familyForward.IsMatch(x)).ToList();
        var stale = RegistrationPlan.FromCompilerRecords(campaign);
        int staleOwners = stale.Objects.Where(o => o.Type == "Settlement").Count(o => o.References.Any(x => x.Attribute == "owner") && byId.ContainsKey(o.Id) && byId[o.Id].Owner != null &&
                                                                                       o.References.First(x => x.Attribute == "owner").Id != byId[o.Id].Owner);
        int staleBounds = stale.Objects.Where(o => o.Type == "Village").Count(o => o.References.Any(x => x.Attribute == "bound") && o.Parent != null && byId.ContainsKey(o.Parent.Id) &&
                                                                                     o.References.First(x => x.Attribute == "bound").Id != byId[o.Parent.Id].Bound);
        Check(planProblems.Count == 0 && heroFamily.Count == 0 && plan.GeneratedObjects.Count(o => o.Type == "Settlement") == r.Settlements.Count,
              "the whole generated world (factions, heroes and all " + r.Settlements.Count + " settlements) plans through the MMR-30 registry's FromDocuments with no settlement, faction or reference problem (" + planProblems.Count +
              (planProblems.Count == 0 ? "" : ": " + String.Join("; ", planProblems.Take(3))) + "); the hero family forward references (spouse, father, mother; of the kind the installed Heroes.xml has) are in the two-pass list since MMR-45 (" + heroFamily.Count + " refused); FromCompilerRecords still plans the compiler's " +
              staleOwners + " castle owners and " + staleBounds + " village bounds the factory changed (MMR-45 registers from documents)");

        // c3 (offline part): the fixture builder makes a valid town, castle, village and hideout through the same Element(), with the
        // centre, gate and port it is given.
        var fixtures = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists);
        var fTown = fixtures.Fixture("fx_town", "town", "Fixture Harbour", "empire", "fx_clan", null, 100, 100, 101.5, 99, 95, 100, 3600, 0, null);
        var fCastle = fixtures.Fixture("fx_castle", "castle", "Fixture Castle", "empire", "fx_clan", null, 130, 100, 131.875, 100, null, null, 700, 0, null);
        var fVillage = fixtures.Fixture("fx_village", "village", "Fixture Village", "empire", null, "fx_castle", 140, 110, 140, 110, null, null, 0, 300, "wheat_farm");
        var fHideout = fixtures.Hideout("fx_hideout", "Fixture Hideout", "forest_bandits", 150, 120);
        var fdoc = new XmlDocument(); fdoc.AppendChild(fdoc.CreateElement("Settlements"));
        var fe = new[] { fTown, fCastle, fVillage, fHideout }.Select(s => (XmlElement)fdoc.DocumentElement.AppendChild(SettlementFactory.Element(fdoc, s, fixtures.Prototype(s.Prototype)))).ToList();
        Func<XmlElement, string, string> attr = (e, n) => e.HasAttribute(n) ? e.GetAttribute(n) : null;
        Check(fe[0].SelectSingleNode("Components/Town[@is_castle='false']") != null && fe[0].SelectSingleNode("Locations/Location[@id='port']") != null &&
              attr(fe[0], "posX") == "100" && attr(fe[0], "posY") == "100" && attr(fe[0], "gate_posX") == "101.5" && attr(fe[0], "gate_posY") == "99" && attr(fe[0], "port_posX") == "95" && attr(fe[0], "port_posY") == "100" &&
              fe[1].SelectSingleNode("Components/Town[@is_castle='true']") != null && attr(fe[1], "gate_posX") == "131.875" && attr(fe[1], "gate_posY") == "100" && attr(fe[1], "port_posX") == null &&
              fe[2].SelectSingleNode("Components/Village[@bound='Settlement.fx_castle']") != null && attr(fe[2], "gate_posX") == null && attr(fe[2], "port_posX") == null &&
              protoIds("empire", "village_inland").Contains(fVillage.Prototype) &&
              fe[3].GetAttribute("type") == "Hideout" && fe[3].SelectSingleNode("Components/Hideout") != null && attr(fe[3], "text") == "{=fx_hideout_text}An outlaw hideout in the wilds." && fe.All(locationsResolve),
              "the fixture builder makes a coastal town (centre, a distinct gate and a port, exactly as given), a castle with a distinct gate, an inland castle-bound village and a hideout through the world's Element() with resolvable location complexes (registered in game by the fixture probe)");

        // Determinism and refusals.
        var again = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes);
        var shuffled = Copy(campaign); foreach (var key in new[] { "towns", "castles", "villages", "heroes", "kingdoms", "houses" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(again.IntentsSha256 == r.IntentsSha256 && again.Documents[0].Value.OuterXml == r.Documents[0].Value.OuterXml && build(shuffled, travel, ports, economy).IntentsSha256 == r.IntentsSha256,
              "building twice, or from reordered records, gives the same intents (" + r.IntentsSha256.Substring(0, 12) + ") and XML");
        var noPrototype = Copy(prototypes); O(O(noPrototype, "cultures"), "nord").Remove("castle");
        var foreignOwner = Copy(campaign); Records(foreignOwner, "towns").First()["owner"] = "mnbl_ffffffff_l_0000000000";
        var crossGroup = Copy(travel); var vrec = Records(crossGroup, "settlements").First(s => S(s, "kind") == "village" && villages.Any(v => S(v, "id") == S(s, "id"))); vrec["group"] = 999;
        var tightEngine = Copy(economy); O(O(tightEngine, "binding"), "distance")["preferredRatio"] = 0.1; O(O(tightEngine, "binding"), "distance")["engineTradeRatio"] = 0.2;
        var version1 = Copy(campaign); version1["version"] = 1;
        var emptyCastle = Copy(campaign);
        foreach (var v in Records(emptyCastle, "villages").Where(v => S(v, "boundKind") == "castle" && S(v, "bound") == S(Records(emptyCastle, "castles").First(c => Records(campaign, "villages").Any(x => S(x, "bound") == S(c, "id"))), "id")).ToList())
        { v["bound"] = S(v, "sourceTown"); v["boundKind"] = "town"; }
        var floorless = Copy(economy); foreach (var f in O(O(floorless, "villageMix"), "floors").Values.Cast<IDictionary<string, object>>()) f["horseRanchShare"] = 5.0;
        var tightBand = Copy(economy); O(tightBand, "balance")["band"] = new object[] { 0.75, 0.8 };   // the 3/2 quota gives 0.87-1.3 x the installed villages per fief
        var noPopulation = Copy(campaign); O(Records(noPopulation, "towns").First(), "population").Remove("estimate");
        var hungry = Copy(economy); O(hungry, "food")["townMinimum"] = 100000;
        var refusals = new[] {
            Refusal(() => new SettlementFactory(economy, noPrototype, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(foreignOwner, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, crossGroup, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(tightEngine, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(tightBand, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(noPopulation, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(hungry, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes)),
            Refusal(() => new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists).Build(version1, travel, ports, factions, heroes)),
            Refusal(() => build(emptyCastle, travel, ports, economy)),
            Refusal(() => new SettlementFactory(floorless, prototypes, catalog["Settlements"], resolver.Exists).Build(campaign, travel, ports, factions, heroes)) };
        var expected = new[] { "castle prototype", "is not a house intent", "bound across land groups", "beyond the engine trade limit", "outside the band", "has no population estimate", "food supply",
                               "version 2", "has no bound village", "horse ranches (floor" };
        Check(refusals.Zip(expected, (m, e) => m != null && m.Contains(e)).All(x => x),
              "each is refused: a culture without a castle prototype, a town owned by no house intent, a village bound across land groups, a remote village beyond the engine trade limit, villages per fief outside the balance band, a town without a population estimate, a town below the food minimum, campaign records of version 1, a castle left with no village (RT-31) and a culture whose horse-ranch floor no redraw can reach (" +
              String.Join(" | ", refusals.Zip(expected, (m, e) => e + ": " + (m != null && m.Contains(e) ? "refused" : "NOT refused (" + m + ")"))) + ")");

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.settlements", ["version"] = 2, ["ticket"] = "MMR-33", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["balance"] = r.Balance, ["villageQuota"] = quota, ["portTowns"] = portTowns.Count, ["adaptedPrototypes"] = r.Settlements.Count(s => s.AdaptedPrototype),
            ["castellanCastles"] = castellanCastles.Count, ["slots"] = owned.Count, ["compilerBindingMaxRatio"] = Math.Round(compilerFar, 3),
            ["registrationPlan"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["problems"] = planProblems.Count, ["heroFamilyForwardReferences"] = heroFamily.Count, ["fromCompilerRecordsStaleOwners"] = staleOwners, ["fromCompilerRecordsStaleBounds"] = staleBounds },
            ["villageTypes"] = vIntents.GroupBy(s => s.VillageType).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            ["documents"] = r.Documents.ToDictionary(d => d.Key, d => Sha(Encoding.UTF8.GetBytes(d.Value.OuterXml))),
            ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
