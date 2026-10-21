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

// MMR-35 naval content tests, without the game: NavalContentTests BUNDLE_DIR REPO_DIR OUT_JSON
// The real size-129 world goes through the accepted MMR-31/32/33/34 factories, then NavalContentFactory. Every rule is rechecked here from the
// policy, the travel cells, ports and graph, and the installed catalog.
internal static class NavalContentTests
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

    private static NavalContentFactory.NavalException Refused(Func<object> build)
    {
        try { build(); return null; }
        catch (NavalContentFactory.NavalException e) { return e; }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: NavalContentTests BUNDLE_DIR REPO_DIR OUT_JSON"); return 2; }
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
        foreach (var key in new[] { "shipHullsSha256", "factionsSha256", "culturesSha256", "partyTemplatesSha256" })
        {
            var doc = key == "shipHullsSha256" ? "ShipHulls" : key == "factionsSha256" ? "Factions" : key == "culturesSha256" ? "SPCultures" : "partyTemplates";
            if (S(O(naval, "provenance"), key) != Records(O(receipt, "outputs"), "documents").First(d => S(d, "id") == doc)["sha256"] as string) throw new InvalidOperationException("naval policy made from another " + doc);
        }
        var defaultsRun = Load(Path.Combine(repo, S(O(rosters, "provenance"), "defaultReferences")));
        var defaults = L(O(Records(O(defaultsRun, "outputs"), "worlds").First(), "registration"), "verifiedLiveDefaultReferences");
        var resolver = new CharacterTemplateResolver(catalog, defaults, O(rosters, "troops"));
        Func<IDictionary<string, object>, FactionFactory.Result> factionsOf = c => FactionFactory.Build(c, idMap, Load(Path.Combine(repo, "worldgen/policies/factions.json")), Load(Path.Combine(repo, "worldgen/policies/culture-presentations.json")), templates, Load(Path.Combine(repo, "worldgen/policies/population.json")));
        Func<IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, XmlDocument, NavalContentFactory.Result> buildWith = (c, t, pr, policy, hulls) => {
            var f = factionsOf(c); var h = HeroFactory.Build(c, idMap, f, rosters, resolver, population);
            var sf = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists);
            var w = sf.Build(c, t, pr, f, h, NavalContentFactory.ShipyardLevels(policy, pr));
            var st = new GeneratedCampaignInitializer(startup, sf, catalog["Settlements"], catalog["Factions"], catalog["SPCultures"]).Build(t, pr, cells, f, h, w, biomes);
            return new NavalContentFactory(policy, catalog["Factions"], catalog["SPCultures"], hulls, catalog["MissionShips"], catalog["ShipSlots"], catalog["partyTemplates"]).Build(c, t, pr, cells, f, w, st); };
        Func<IDictionary<string, object>, IDictionary<string, object>, IDictionary<string, object>, NavalContentFactory.Result> build = (c, t, policy) => buildWith(c, t, ports, policy, catalog["ShipHulls"]);
        var factions = factionsOf(campaign); var heroes = HeroFactory.Build(campaign, idMap, factions, rosters, resolver, population);
        var factory = new SettlementFactory(economy, prototypes, catalog["Settlements"], resolver.Exists);
        var levels = NavalContentFactory.ShipyardLevels(naval, ports);
        var world = factory.Build(campaign, travel, ports, factions, heroes, levels);
        var startupResult = new GeneratedCampaignInitializer(startup, factory, catalog["Settlements"], catalog["Factions"], catalog["SPCultures"]).Build(travel, ports, cells, factions, heroes, world, biomes);
        var r = new NavalContentFactory(naval, catalog["Factions"], catalog["SPCultures"], catalog["ShipHulls"], catalog["MissionShips"], catalog["ShipSlots"], catalog["partyTemplates"])
            .Build(campaign, travel, ports, cells, factions, world, startupResult);
        var land = new GeneratedSpawnModel.CellMap(O(travel, "grid"), cells, new[] { "land" }, Records(travel, "settlements"));
        var waterMap = new GeneratedSpawnModel.CellMap(O(travel, "grid"), cells, new[] { "sea", "lake", "river" }, new IDictionary<string, object>[0]);
        var byId = world.Settlements.ToDictionary(s => s.Id);
        var settlementsDoc = world.Documents.Single(d => d.Key == "Settlements").Value;
        Func<string, XmlElement> element = id => (XmlElement)settlementsDoc.DocumentElement.SelectSingleNode("Settlement[@id='" + id + "']");
        var basins = Records(travel, "basins").ToDictionary(b => Convert.ToInt32(b["id"]), b => b);

        // c1: every naval reference resolves in the installed catalog.
        var hullsDoc = catalog["ShipHulls"]; var missions = catalog["MissionShips"]; var slots = catalog["ShipSlots"];
        Func<string, XmlElement> hull = id => (XmlElement)hullsDoc.DocumentElement.SelectSingleNode("ShipHull[@id='" + id + "']");
        Func<string, bool> chain = id => hull(id) != null && missions.DocumentElement.SelectSingleNode("MissionShip[@id='" + hull(id).GetAttribute("mission_ship") + "']") != null &&
                                         hull(id).SelectNodes("AvailableSlots/ShipSlot").OfType<XmlElement>().All(s => slots.DocumentElement.SelectSingleNode("ShipSlot[@id='" + s.GetAttribute("id") + "']") != null);
        var policyHulls = O(O(naval, "hulls"), "cultures");
        var roleHulls = O(O(naval, "hulls"), "roles").Values.Cast<IDictionary<string, object>>().SelectMany(c => O(c, "templateHulls").Values.Cast<IEnumerable>().SelectMany(st => st.Cast<IDictionary<string, object>>()).Select(x => S(x, "hull")));
        var allHulls = policyHulls.Values.Cast<IDictionary<string, object>>().SelectMany(c => c.Keys).Concat(roleHulls)
            .Concat(O(O(naval, "pirates"), "clans").Values.Cast<IDictionary<string, object>>().SelectMany(c => Records(c, "templateHulls").Select(x => S(x, "hull")))).Distinct().ToList();
        var upgradePieces = catalog["ShipUpgradePieces"].DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        var siegeEngines = L(O(naval, "provenance"), "siegeEngines");
        Check(allHulls.Count > 0 && allHulls.All(chain) && policyHulls.Keys.OrderBy(x => x).SequenceEqual(L(O(startup, "starts"), "creationCultures").OrderBy(x => x)) &&
              policyHulls.Values.Cast<IDictionary<string, object>>().SelectMany(c => c.Values.Cast<IDictionary<string, object>>()).All(h => !String.IsNullOrEmpty(S(h, "prefab")) && !String.IsNullOrEmpty(S(h, "deckNavmesh")) &&
                  missions.DocumentElement.ChildNodes.OfType<XmlElement>().Any(m => m.GetAttribute("id") == S(h, "missionShip") && m.GetAttribute("prefab") == S(h, "prefab"))) &&
              upgradePieces.All(p => p.SelectNodes("TargetSlots/TargetSlot").OfType<XmlElement>().All(s => slots.DocumentElement.SelectSingleNode("ShipSlot[@id='" + s.GetAttribute("id") + "']") != null)) &&
              upgradePieces.Where(p => p.HasAttribute("siege_engine")).All(p => defaults.Contains(p.GetAttribute("siege_engine")) && siegeEngines.Contains(p.GetAttribute("siege_engine").Split('.').Last())),
              "every one of the " + allHulls.Count + " hulls the creation cultures, their naval templates and the pirate templates use resolves its mission ship and ship slots; each creation-culture hull's chain in the policy names its mission prefab and deck navmesh (verified against the installed files by make_naval_policy.py); all " +
              upgradePieces.Count + " upgrade pieces target installed slots and their " + siegeEngines.Count + " siege engines are verified live defaults");
        var roles = O(O(naval, "hulls"), "roles");
        var templatesDoc = catalog["partyTemplates"]; var npcs = catalog["NPCCharacters"];
        Func<string, bool> template = id => templatesDoc.DocumentElement.ChildNodes.OfType<XmlElement>().Any(e => e.GetAttribute("id") == id && e.SelectNodes("ship_hulls/ShipTemplateStack").Count > 0);
        Check(roles.All(c => { var x = (IDictionary<string, object>)c.Value; return template(S(x, "fishing_party_template")) && template(S(x, "settlement_patrol_template_coastal")) &&
                                                                                 new[] { "shipwright", "shipyard_worker" }.All(k => npcs.DocumentElement.ChildNodes.OfType<XmlElement>().Any(e => e.GetAttribute("id") == S(x, k))); }),
              "every creation culture's naval roles resolve: its fishing and coastal patrol templates (with ship hulls) and its shipwright and shipyard worker characters");
        // Port facilities: port towns on open basins with shipyards and their buildable hulls; landlocked towns without.
        var shares = O(O(naval, "shipyards"), "shares");
        var counts = r.Facilities.GroupBy(f => f.ShipyardLevel).ToDictionary(g => g.Key, g => g.Count());
        Func<int, int> share = l => (int)Math.Round(D(shares, l.ToString()) * levels.Count, MidpointRounding.AwayFromZero);
        var ordered = r.Facilities.OrderByDescending(f => f.Quality).ThenBy(f => f.Town, StringComparer.Ordinal).ToList();
        Check(r.Facilities.Count == world.Settlements.Count(s => s.Kind == "town" && s.Port) && r.Facilities.Count == levels.Count &&
              counts.GetValueOrDefault(3) == share(3) && counts.GetValueOrDefault(2) == share(2) && counts.GetValueOrDefault(1) == levels.Count - share(3) - share(2) &&
              ordered.Select((f, i) => f.ShipyardLevel == (i < share(3) ? 3 : i < share(3) + share(2) ? 2 : 1)).All(x => x) &&
              r.Facilities.All(f => element(f.Town).SelectSingleNode("Components/Town/Buildings/Building[@id='building_shipyard']") is XmlElement &&
                                    ((XmlElement)element(f.Town).SelectSingleNode("Components/Town/Buildings/Building[@id='building_shipyard']")).GetAttribute("level") == f.ShipyardLevel.ToString()) &&
              world.Settlements.Where(s => s.Kind == "town" && !s.Port).All(s => element(s.Id).SelectSingleNode("Components/Town/Buildings/Building[@id='building_shipyard']") == null && !element(s.Id).HasAttribute("port_posX")),
              "the " + r.Facilities.Count + " port towns get building_shipyard at the installed shares by harbour quality (level 3: " + counts.GetValueOrDefault(3) + ", 2: " + counts.GetValueOrDefault(2) + ", 1: " + counts.GetValueOrDefault(1) +
              ", written into the Settlements document); the " + world.Settlements.Count(s => s.Kind == "town" && !s.Port) + " landlocked towns have no port and no shipyard");
        var tiers = O(O(naval, "shipyards"), "hullTiers");
        Check(r.Facilities.All(f => f.Open && basins.ContainsKey(f.Basin) && (bool)basins[f.Basin]["open"] && L(basins[f.Basin], "ports").Contains(f.Town) && waterMap.Passable(f.PortX, f.PortY) &&
                                    f.Hulls.Count > 0 && f.Hulls.All(h => Convert.ToInt32(tiers[hull(h).GetAttribute("ship_type")]) <= f.ShipyardLevel) &&
                                    catalog["SPCultures"].DocumentElement.SelectSingleNode("Culture[@id='" + f.Culture + "']/available_ship_hulls").ChildNodes.OfType<XmlElement>()
                                        .Where(e => Convert.ToInt32(tiers[hull(e.GetAttribute("id").Split('.').Last()).GetAttribute("ship_type")]) <= f.ShipyardLevel).Count() == f.Hulls.Count),
              "every port is water of its open MMR-28 basin, and each shipyard builds exactly its culture's hulls up to its tier (light 1, medium 2, heavy 3: " + String.Join("; ", r.Facilities.GroupBy(f => f.ShipyardLevel).OrderBy(g => g.Key).Select(g => "level " + g.Key + " " + g.Average(f => f.Hulls.Count).ToString("0.0") + " hulls")) + ")");
        // Blockade arcs at the installed offsets in the port frame; port markers at the port.
        var arc = O(O(naval, "blockades"), "arc");
        var start = L(arc, "start").Select(Convert.ToDouble).ToArray(); var end = L(arc, "end").Select(Convert.ToDouble).ToArray();
        Func<NavalContentFactory.Facility, double[], double[], bool> at = (f, p, o) => {
            double ux = Delta(land, f.X, f.PortX), uy = f.PortY - f.Y, n = Math.Sqrt(ux * ux + uy * uy); ux /= n; uy /= n;
            double ax = Delta(land, f.PortX, p[0]), ay = p[1] - f.PortY;
            return Math.Abs(ax * ux + ay * uy - o[0]) < 1e-6 && Math.Abs(-ax * uy + ay * ux - o[1]) < 1e-6; };
        // The arc keeps the installed geometry (each end's distance from the port and the angle between them) in the facility's turned frame,
        // and both ends are water of the port's basin, or the facility carries a named blockade diagnostic.
        Func<NavalContentFactory.Facility, double[], double> reach = (f, p) => land.Distance(f.PortX, f.PortY, p[0], p[1]);
        Func<double[], double> norm = o => Math.Sqrt(o[0] * o[0] + o[1] * o[1]);
        Func<NavalContentFactory.Facility, bool> wetArc = f => new[] { f.ArcStart, f.ArcEnd }.All(p => waterMap.Passable(p[0], p[1]) && waterMap.Component(p[0], p[1]) == waterMap.Component(f.PortX, f.PortY));
        double installedAngle = Math.Abs(Math.Atan2(start[1], start[0]) - Math.Atan2(end[1], end[0]));
        Func<NavalContentFactory.Facility, double> arcAngle = f => {
            double ax = Delta(land, f.PortX, f.ArcStart[0]), ay = f.ArcStart[1] - f.PortY, bx = Delta(land, f.PortX, f.ArcEnd[0]), by = f.ArcEnd[1] - f.PortY;
            return Math.Acos(Math.Max(-1, Math.Min(1, (ax * bx + ay * by) / (Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by))))); };
        double expectedAngle = Math.Acos(Math.Max(-1, Math.Min(1, (start[0] * end[0] + start[1] * end[1]) / (norm(start) * norm(end)))));
        var blocked = r.Facilities.Where(f => f.ArcProblem != null).ToList();
        Check(r.Facilities.All(f => Math.Abs(reach(f, f.ArcStart) - norm(start)) < 1e-6 && Math.Abs(reach(f, f.ArcEnd) - norm(end)) < 1e-6 && Math.Abs(arcAngle(f) - expectedAngle) < 1e-6 && !double.IsNaN(f.Orientation)) &&
              r.Facilities.All(f => f.ArcProblem == null ? wetArc(f) : r.Diagnostics.Any(d => d.Kind == "blockade" && d.Subject == f.Town)),
              "every port town's blockade arc keeps the installed geometry (ends " + Math.Round(norm(start), 2) + " and " + Math.Round(norm(end), 2) + " from the port, " + Math.Round(expectedAngle * 180 / Math.PI) + " degrees apart) in its seaward frame: " +
              (r.Facilities.Count - blocked.Count) + " of " + r.Facilities.Count + " have both ends on water of the port's basin (" + r.Facilities.Count(f => f.ArcProblem == null && f.ArcRotation == 0 && !f.ArcMirrored) + " unturned, " +
              r.Facilities.Count(f => f.ArcProblem == null && (f.ArcRotation != 0 || f.ArcMirrored)) + " turned or mirrored), " + blocked.Count + " are named blockade diagnostics for MMR-40");
        // Landings.
        double envelope = D(O(O(naval, "landings"), "fromCentre"), "maximum") + land.Spacing;
        var harbours = world.Settlements.Where(s => s.Kind == "village" && s.Harbour).ToList();
        var landed = r.Landings.Where(l => l.Problem == null).ToList();
        Check(r.Landings.Count == harbours.Count && landed.Count == harbours.Count && landed.All(l => l.Open && waterMap.Passable(l.PortX, l.PortY) && (bool)basins[l.Basin]["open"] && l.FromCentre <= envelope + 1e-9 &&
                                                                                                 l.PortX == byId[l.Village].PortX && l.PortY == byId[l.Village].PortY),
              "all " + harbours.Count + " harbour villages get a drop-off intent at their port, water of an open basin within the installed envelope (" + Math.Round(envelope, 2) + "); a harbour village whose port fails that refuses the world (below), since its XML port would still give it HasPort");
        // Roles: coastal versus landlocked kingdoms and clans.
        var kingdoms = r.Roles.Where(x => x.Kind == "kingdom").ToList(); var clans = r.Roles.Where(x => x.Kind == "clan").ToList();
        Check(kingdoms.Count == factions.Kingdoms.Count && clans.Count == factions.AllClans.Count() &&
              kingdoms.All(k => k.Naval == r.Facilities.Any(f => f.Open && factions.Kingdoms.First(x => x.Id == k.Id).Towns.Contains(f.Town)) && !String.IsNullOrEmpty(k.Reason)) &&
              clans.All(c => c.Naval == !r.Plan.Landlocked.Contains(c.Id) && (c.Naval == r.Facilities.Any(f => f.Open && f.LandGroup == c.LandGroup && c.LandGroup >= 0))),
              "naval roles: " + kingdoms.Count(k => k.Naval) + " coastal and " + kingdoms.Count(k => !k.Naval) + " landlocked kingdoms (" + String.Join("; ", kingdoms.Where(k => !k.Naval).GroupBy(k => Regex.Replace(k.Reason, "[0-9]+", "N")).Select(g => g.Count() + " " + g.Key)) +
              "); " + clans.Count(c => c.Naval) + " clans may hold ships and " + r.Plan.Landlocked.Count + " may not (no port town on an open basin in their home's land group)");

        // c2: islands and impossible ports.
        int main = Convert.ToInt32(travel["mainGroup"]);
        var settledGroups = new HashSet<int>(Records(travel, "settlements").Where(s => s.ContainsKey("group")).Select(s => Convert.ToInt32(s["group"])));   // unplaceable (excluded) settlements have no group
        var islands = Records(travel, "groups").Where(g => Convert.ToInt32(g["towns"]) > 0 && Convert.ToInt32(g["id"]) != main).ToList();
        var settledIslands = Records(travel, "groups").Where(g => settledGroups.Contains(Convert.ToInt32(g["id"])) && Convert.ToInt32(g["id"]) != main).ToList();
        var openIds = new HashSet<int>(basins.Where(b => (bool)b.Value["open"]).Select(b => b.Key));
        Check(islands.All(g => Records(travel, "transitions").Any(t => Convert.ToInt32(t["group"]) == Convert.ToInt32(g["id"]) && openIds.Contains(Convert.ToInt32(t["basin"])))) &&
              r.Diagnostics.Count(d => d.Kind == "island") == settledIslands.Count,
              "every one of the " + islands.Count + " island land groups with towns has a candidate naval transition to an open basin, and all " + settledIslands.Count + " groups with a settlement are diagnosed (" +
              String.Join("; ", r.Diagnostics.Where(d => d.Kind == "island").Select(d => d.Subject + ": " + d.Reason.Split(';')[1].Trim())) + ")");
        var impossible = Records(ports, "ports").Where(p => !p.ContainsKey("chosen") || p["chosen"] == null).Select(p => S(p, "town")).Concat(L(ports, "coastalTownsWithoutHarbourRecord")).ToList();
        Check(impossible.Count > 0 && impossible.All(t => r.Diagnostics.Any(d => d.Kind == "impossible_port" && d.Subject == t && !String.IsNullOrEmpty(d.Reason))) && impossible.All(t => !byId[t].Port),
              "every one of the " + impossible.Count + " towns the port compiler could not give a port is a named diagnostic and a landlocked town (" +
              String.Join("; ", r.Diagnostics.Where(d => d.Kind == "impossible_port").GroupBy(d => Regex.Replace(d.Reason, @"\([0-9.]+ cells\)", "(N cells)")).Select(g => g.Count() + " " + g.Key)) + ")");

        // Pirates.
        var pp = O(naval, "pirates");
        double spacing = world.Settlements.Where(s => s.Kind == "town").Average(t => world.Settlements.Where(u => u.Kind == "town" && u != t).Min(u => land.Distance(t.X, t.Y, u.X, u.Y)));
        var zones = r.Pirates.SelectMany(p => p.SpawnPoints).ToList();
        var spawns = zones.Where(z => z.Zone == "outer").ToList(); var innerZones = zones.Where(z => z.Zone == "inner").ToList();
        int openSea = Convert.ToInt32(O(naval, "water")["openSeaCells"]);
        Func<double, double, bool> openSeaAt = (x, y) => { for (int dr = -openSea; dr <= openSea; dr++) for (int dc = -openSea; dc <= openSea; dc++) if (land.ClassOf(x + dc * land.Spacing, y + dr * land.Spacing) != "sea") return false; return true; };
        int target = (int)Math.Round(D(pp, "perPortTown") * r.Facilities.Count, MidpointRounding.AwayFromZero);
        Check(r.Pirates.Select(p => p.Clan).OrderBy(x => x).SequenceEqual(new[] { "northern_pirates", "southern_pirates" }) && spawns.Count > 0 && spawns.Count <= target &&
              spawns.All(s => openSeaAt(s.X, s.Y) && r.Facilities.All(f => land.Distance(s.X, s.Y, f.PortX, f.PortY) >= D(pp, "minPortDistance") * spacing - 1e-9) &&
                              spawns.Where(o => o != s).All(o => land.Distance(s.X, s.Y, o.X, o.Y) >= D(pp, "spawnSpacing") * D(pp, "radius") * spacing - 1e-9) &&
                              Math.Abs(s.Radius - D(pp, "radius") * spacing) < 1e-9) &&
              innerZones.All(z => spawns.Any(s => s.Uid + ":inner" == z.Uid && s.X == z.X && s.Y == z.Y && s.Clan == z.Clan) && Math.Abs(z.Radius - D(pp, "innerRadius") * spacing) < 1e-9) &&
              r.Pirates.All(p => startupResult.Hideouts.Any(h => h.Id == p.Home)),
              spawns.Count + " pirate spawn positions (the installed " + D(pp, "perPortTown") + " per port town gives " + target + ") with " + zones.Count + " zones (" + innerZones.Count + " stacked inner zones of radius " + Math.Round(D(pp, "innerRadius") * spacing, 1) +
              ", installed share " + D(pp, "innerShare") + "; " + String.Join(", ", r.Pirates.Select(p => p.Clan + " " + p.SpawnPoints.Count)) + ") sit on open sea at least " + Math.Round(D(pp, "minPortDistance") * spacing, 1) +
              " from every port and one outer radius (" + Math.Round(D(pp, "radius") * spacing, 1) + ") apart; each pirate clan is homed at a generated hideout" + (r.Notes.Count == 0 ? "" : "; " + String.Join("; ", r.Notes)));
        var factionsDoc = r.Documents.Single(d => d.Key == "Factions").Value.DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        Check(factionsDoc.Count == 2 && factionsDoc.All(e => CampaignIdentity.IsAlias(e.GetAttribute("id")) && e.GetAttribute("is_bandit") == "true" &&
                                                         r.Pirates.Any(p => "Settlement." + p.Home == e.GetAttribute("initial_home_settlement")) &&
                                                         ((XmlElement)catalog["Factions"].DocumentElement.SelectSingleNode("Faction[@id='" + e.GetAttribute("id") + "']")).Attributes.Cast<XmlAttribute>().Where(a => a.Name != "initial_home_settlement").All(a => e.GetAttribute(a.Name) == a.Value)),
              "the installed pirate clans are re-created as world aliases with their installed records and a generated hideout home (PiratesCampaignBehavior finds them structurally: a bandit clan, a culture without settlements, a template with ship hulls)");
        // The whole world, with the startup and naval records, plans through the MMR-30 registry.
        Func<string, XmlDocument> empty = name => { var x = new XmlDocument(); x.AppendChild(x.CreateElement(name)); return x; };
        Func<string, IEnumerable<XmlDocument>, XmlDocument> merge = (name, parts) => { var x = empty(name); foreach (var p in parts.Where(p => p != null)) foreach (XmlNode n in p.DocumentElement.ChildNodes) x.DocumentElement.AppendChild(x.ImportNode(n, true)); return x; };
        var startupDocs = startupResult.Documents.ToDictionary(d => d.Key, d => d.Value);
        var worldDocs = new Dictionary<string, XmlDocument>(StringComparer.Ordinal) {
            ["SPCultures"] = startupDocs["SPCultures"], ["WorkshopTypes"] = empty("WorkshopTypes"), ["LocationComplexTemplates"] = empty("LocationComplexTemplates"),
            ["NPCCharacters"] = heroes.Documents.Single(d => d.Key == "NPCCharacters").Value, ["Heroes"] = heroes.Documents.Single(d => d.Key == "Heroes").Value,
            ["Kingdoms"] = factions.Documents.Single(d => d.Key == "Kingdoms").Value,
            ["Factions"] = merge("Factions", new[] { factions.Documents.Single(d => d.Key == "Factions").Value, startupDocs["Factions"], r.Documents.Single(d => d.Key == "Factions").Value }),
            ["Settlements"] = merge("Settlements", new[] { settlementsDoc, startupDocs["Settlements"] }) };
        var plan = RegistrationPlan.FromDocuments(r.WorldPrefix, RegistrationPlan.NewGameOrder.Select(id => new KeyValuePair<string, XmlDocument>(id, worldDocs[id])).ToList(), false,
                                                  factions.DeclaredKeys.Concat(heroes.DeclaredKeys).Concat(world.DeclaredKeys).Concat(startupResult.DeclaredKeys).Concat(r.DeclaredKeys));
        var planProblems = plan.Problems((t, i) => false, (t, i) => resolver.Exists(t + "." + i)).ToList();
        Check(planProblems.Count == 0 && plan.GeneratedObjects.Count(o => o.Type == "Faction" && (o.Id == "northern_pirates" || o.Id == "southern_pirates")) == 2,
              "the world with its startup and naval records plans through the MMR-30 registry with no problem, the hero family forward references included (two-pass since MMR-45) (" + planProblems.Count + (planProblems.Count == 0 ? "" : ": " + String.Join("; ", planProblems.Take(3))) + ")");
        // c3 (offline part) and the compiler slots.
        var owned = Records(campaign, "ports").SelectMany(p => Records(p, "slots").Where(s => S(s, "owner") == "MMR-35").Select(s => S(p, "town") + ":" + S(s, "name"))).ToList();
        Check(owned.Count == 2 * Records(campaign, "ports").Count() && owned.All(k => r.Slots.Any(s => s.Town + ":" + s.Name == k && !String.IsNullOrEmpty(s.Value))) && r.Slots.Count == owned.Count,
              "all " + owned.Count + " MMR-35 compiler slots (navalFacility and shipyardAndFleet of each port) are resolved");

        // Determinism and refusals.
        var again = build(campaign, travel, naval);
        var shuffled = Copy(campaign); foreach (var key in new[] { "towns", "castles", "villages", "heroes", "kingdoms", "houses", "ports" }) shuffled[key] = ((IEnumerable)shuffled[key]).Cast<object>().Reverse().ToArray();
        Check(again.IntentsSha256 == r.IntentsSha256 && build(shuffled, travel, naval).IntentsSha256 == r.IntentsSha256 && again.Documents.Select(d => d.Value.OuterXml).SequenceEqual(r.Documents.Select(d => d.Value.OuterXml)),
              "building twice, or from reordered records, gives the same naval intents (" + r.IntentsSha256.Substring(0, 12) + ") and documents");
        var badPolicy = Copy(naval); badPolicy["version"] = 2;
        var noHull = (XmlDocument)catalog["ShipHulls"].CloneNode(true);
        var firstUsed = r.Facilities.SelectMany(f => f.Hulls).First();
        ((XmlElement)noHull.DocumentElement.SelectSingleNode("ShipHull[@id='" + firstUsed + "']")).SetAttribute("id", firstUsed + "_missing");
        // An island group without transitions (only where an island group has towns: the factory refuses only those), and a port moved
        // onto land (the chosen candidate's sea point on its land point).
        IDictionary<string, object> cutIsland = null;
        if (islands.Count > 0)
        {
            cutIsland = Copy(travel); var island = Convert.ToInt32(islands.First()["id"]);
            cutIsland["transitions"] = Records(cutIsland, "transitions").Where(t => Convert.ToInt32(t["group"]) != island).ToArray();
        }
        var landPort = Copy(ports); var moved = Records(landPort, "ports").First(p => p.ContainsKey("chosen") && p["chosen"] != null);
        O(moved, "chosen")["seaPoint"] = O(moved, "chosen")["landPoint"];
        var disagree = Copy(naval); var empireHulls = O(O(O(disagree, "hulls"), "cultures"), "empire"); empireHulls.Remove(empireHulls.Keys.First());
        var dryHarbour = Copy(ports);
        var harbourCandidate = Records(dryHarbour, "ports").SelectMany(p => Records(p, "candidates")).First(c => S(c, "village") != null && c.ContainsKey("harbourCell") && c.ContainsKey("landCell"));
        harbourCandidate["harbourCell"] = harbourCandidate["landCell"];
        var refusals = new List<NavalContentFactory.NavalException>(); var expectedCodes = new List<string>(); var expectedRetry = new List<bool>(); var refusedCases = new List<string>();
        Action<string, Func<object>, string, bool> refuse = (what, make, code, retry) => { refusedCases.Add(what); refusals.Add(Refused(make)); expectedCodes.Add(code); expectedRetry.Add(retry); };
        refuse("a wrong policy version", () => build(campaign, travel, badPolicy), "quality.naval_policy", false);
        refuse("a hull the shipyards need missing from the catalog", () => buildWith(campaign, travel, ports, naval, noHull), "quality.naval_catalog", false);
        if (cutIsland != null) refuse("an island cut off from every open basin", () => build(campaign, cutIsland, naval), "quality.naval_unreachable_island", true);
        refuse("a port moved onto land", () => buildWith(campaign, travel, landPort, naval, catalog["ShipHulls"]), "quality.naval_port", true);
        refuse("a policy whose hull list disagrees with the catalog", () => build(campaign, travel, disagree), "quality.naval_policy", false);
        refuse("a harbour village whose harbour cell is land", () => buildWith(campaign, travel, dryHarbour, naval, catalog["ShipHulls"]), "quality.naval_port", true);
        Check(refusals.Select((e, i) => e != null && e.Codes.Contains(expectedCodes[i]) && (e.Codes.Count == 1 ? e.Retryable == expectedRetry[i] : true)).All(x => x),
              "each is refused with its quality code: " + String.Join(", ", refusedCases.Take(refusedCases.Count - 1)) + ", and " + refusedCases.Last() +
              (cutIsland != null ? "" : " (no island land group has towns, so there is no island to cut off)") + " (" +
              String.Join(" | ", refusals.Select((e, i) => expectedCodes[i] + ": " + (e == null ? "NOT refused" : String.Join(",", e.Codes) + (e.Retryable ? " retryable" : "") + " - " + e.Problems.First()))) + ")");

        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) {
            ["schema"] = "mnbl.evidence.naval", ["version"] = 1, ["ticket"] = "MMR-35", ["worldPrefix"] = r.WorldPrefix, ["intentsSha256"] = r.IntentsSha256,
            ["facilities"] = r.Facilities.Select(f => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["town"] = f.Town, ["culture"] = f.Culture, ["basin"] = f.Basin, ["shipyardLevel"] = f.ShipyardLevel, ["hulls"] = f.Hulls.Count, ["landGroup"] = f.LandGroup }).ToList(),
            ["landings"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["landed"] = landed.Count, ["harbourVillages"] = harbours.Count },
            ["roles"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["coastalKingdoms"] = kingdoms.Count(k => k.Naval), ["landlockedKingdoms"] = kingdoms.Count(k => !k.Naval), ["navalClans"] = clans.Count(c => c.Naval), ["landlockedClans"] = r.Plan.Landlocked.Count },
            ["pirates"] = r.Pirates.ToDictionary(p => p.Clan, p => (object)new SortedDictionary<string, object>(StringComparer.Ordinal) { ["home"] = p.Home, ["spawnPoints"] = p.SpawnPoints.Count }),
            ["diagnostics"] = r.Diagnostics.Select(d => d.Kind + " " + d.Subject + ": " + d.Reason).ToList(), ["notes"] = r.Notes,
            ["documents"] = r.Documents.ToDictionary(d => d.Key, d => Sha(Encoding.UTF8.GetBytes(d.Value.OuterXml))), ["checks"] = checks, ["failures"] = failures };
        File.WriteAllText(outJson, Json.Serialize(evidence), new UTF8Encoding(false));
    }

    private static int GetValueOrDefault(this Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

    private static double Delta(GeneratedSpawnModel.CellMap map, double from, double to)
    {
        double d = to - from;
        if (d > map.Width / 2) d -= map.Width; else if (d < -map.Width / 2) d += map.Width;
        return d;
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
