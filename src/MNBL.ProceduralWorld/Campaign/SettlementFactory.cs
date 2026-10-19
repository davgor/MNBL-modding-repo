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
    // MMR-33: generated settlements and their starting economy (docs/settlements.md). Pure managed. From the MMR-27 compiler towns,
    // castles and villages, the MMR-28 travel graph and ports (staged positions), the MMR-31 clans (owners), the MMR-32 settlement
    // cultures, the economy policy (worldgen/policies/economy.json) and installed settlement prototypes (worldgen/templates/settlements)
    // it builds the Settlements XML the MMR-30 registry loads (towns and castles before the villages bound to them), one intent per
    // settlement, and the resolution of the MMR-33-owned compiler slots (town prosperityAndEconomy, village villageType and hearth).
    //   * The XML path is the installed lifecycle: Settlement.Deserialize reads name, positions (centre, gate, port), culture, owner,
    //     text, its component (Town: is_castle, prosperity, Buildings; Village: village_type, hearth, bound; Hideout) and the Locations
    //     and CommonAreas (installed IL). A prototype supplies only assets (component meshes, location complex template, scenes, common
    //     areas); identity, geography, ownership, economy and text are the world's. The prototype's lore text is never copied.
    //   * Prosperity and hearth are quantiles of the installed settlements' own distributions at the settlement's percentile among the
    //     generated ones, so they stay in the installed range whatever the world's size; population only orders settlements.
    //   * Binding (economy.json binding): a village farther from its bound settlement than the policy limit (relative to the world's
    //     town spacing, periodic) is rebound to the nearest town or castle of its land group within it; castles take villages of their
    //     garrison town, then of over-full towns of their kingdom, as the installed castles have them. Balance (villages and hearth per
    //     fief), the engine's castle-village trade limit and each town's trade-area food supply are validated against the policy.
    //   * Harbour villages (MMR-27 harbour) get a port at their MMR-28 harbour cell and a coastal prototype; fishing types need a port.
    //   * Positions are staged (pendingEngineValidation): MMR-40 places gates and ports on the engine navmesh.
    // Element() is the one builder the world and the in-game fixtures share. Determinism: records in id order, named versioned streams.
    internal sealed class SettlementFactory
    {
        public sealed class SettlementIntent
        {
            public string Id, Uid, Kind, Name, SourceName, Culture, Owner, Kingdom, Prototype, Bound, VillageType, Production, CityClass, Provenance, Text;
            public string MapPrefab;   // the prototype's vanilla map prefab (null for towns, which the vanilla map does not place as prefabs); MMR-40 places entities
            public string TradeTown;   // a castle's village: the nearest town of its kingdom within the engine's trade limit (the engine assigns it in game)
            public string FoodRedraw;  // a village whose type the food pass changed: the type drawn before
            public string SourceBound; // a village's compiler town (before any rebinding)
            public double X, Y, GateX, GateY, BoundDistance;
            public double? PortX, PortY;
            public int[] HarbourCell;  // a harbour village's MMR-28 harbour cell (row, column)
            public string PositionStatus = "pendingEngineValidation";   // MMR-40: "resolved ..." once ApplyResolvedPositions has run
            public int Prosperity, Hearth, LandGroup;
            public bool Port, AdaptedPrototype, Harbour, Remote;
            public string SceneRepick;   // MMR-42: the factory's pick failed the mission scene contracts and was re-picked (old -> new: reason)
            public SortedDictionary<string, int> Buildings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        }

        public sealed class Slot { public string Owner, Kind, Name; public SortedDictionary<string, object> Value; }

        // MMR-40: a settlement's resolved placement (World/SettlementPositionResolver's layout, logical positions): the centre is its navigation
        // point, a fortification's gate the resolved gate, a port town's port the navigation port point and a harbour village's port its
        // drop-off. The records and the scene entities are then built from the same placement.
        // Properties, not fields: only the MMR-40 resolver assigns them, and harnesses that compile the factory without it would fail CS0649.
        public sealed class ResolvedPositions { public double[] Centre { get; set; } public double[] Gate { get; set; } public double[] Port { get; set; } public double[] Dropoff { get; set; } public string Status { get; set; } }

        // Writes the resolved placement into the intents; returns the ids with no resolved placement (left staged).
        public static List<string> ApplyResolvedPositions(IEnumerable<SettlementIntent> intents, Func<string, ResolvedPositions> resolved)
        {
            var missing = new List<string>();
            foreach (var s in intents)
            {
                var r = resolved(s.Id);
                if (r == null || r.Centre == null) { missing.Add(s.Id); continue; }
                s.X = r.Centre[0]; s.Y = r.Centre[1];
                var gate = r.Gate ?? r.Centre; s.GateX = gate[0]; s.GateY = gate[1];
                var port = r.Port ?? r.Dropoff;
                if (port != null) { s.PortX = port[0]; s.PortY = port[1]; }
                s.PositionStatus = r.Status ?? "resolved";
            }
            return missing;
        }

        // MMR-45: the Settlements document Build wrote, given the intents' positions after ApplyResolvedPositions (the attributes Element writes:
        // the centre, a fortification's gate, a port settlement's port). Every intent must have its record.
        public static void RewritePositions(XmlDocument settlements, IEnumerable<SettlementIntent> intents)
        {
            foreach (var s in intents)
            {
                var e = settlements.DocumentElement.SelectSingleNode("Settlement[@id='" + s.Id + "']") as XmlElement;
                if (e == null) throw new InvalidOperationException("Settlement " + s.Id + " has no record to place.");
                e.SetAttribute("posX", Number(s.X)); e.SetAttribute("posY", Number(s.Y));
                if (s.Kind == "town" || s.Kind == "castle") { e.SetAttribute("gate_posX", Number(s.GateX)); e.SetAttribute("gate_posY", Number(s.GateY)); }
                if (s.Port && s.PortX.HasValue) { e.SetAttribute("port_posX", Number(s.PortX.Value)); e.SetAttribute("port_posY", Number(s.PortY.Value)); }
            }
        }

        // The records that differ from the placement the scene entities were built from (centre, gate, port), within `tolerance` world
        // units, as "id field record placement" lines; a record with no placement is listed too. A world whose records differ from its scene
        // is refused (MMR-45 binds the records with ApplyResolvedPositions and checks them with this before activation).
        public static List<string> CheckResolvedPositions(IEnumerable<SettlementIntent> intents, Func<string, ResolvedPositions> resolved, double tolerance = 1e-3)
        {
            var mismatches = new List<string>();
            Func<double, double, double, double, bool> differs = (ax, ay, bx, by) => Math.Abs(ax - bx) > tolerance || Math.Abs(ay - by) > tolerance;
            Func<double, double, string> p = (x, y) => x.ToString("0.####", CultureInfo.InvariantCulture) + "," + y.ToString("0.####", CultureInfo.InvariantCulture);
            foreach (var s in intents)
            {
                var r = resolved(s.Id);
                if (r == null || r.Centre == null) { mismatches.Add(s.Id + " placement missing"); continue; }
                if (differs(s.X, s.Y, r.Centre[0], r.Centre[1])) mismatches.Add(s.Id + " centre " + p(s.X, s.Y) + " " + p(r.Centre[0], r.Centre[1]));
                var gate = r.Gate ?? r.Centre;
                if (differs(s.GateX, s.GateY, gate[0], gate[1])) mismatches.Add(s.Id + " gate " + p(s.GateX, s.GateY) + " " + p(gate[0], gate[1]));
                var port = r.Port ?? r.Dropoff;
                if ((port == null) != (s.PortX == null)) mismatches.Add(s.Id + " port " + (s.PortX == null ? "none" : p(s.PortX.Value, s.PortY.Value)) + " " + (port == null ? "none" : p(port[0], port[1])));
                else if (port != null && differs(s.PortX.Value, s.PortY.Value, port[0], port[1])) mismatches.Add(s.Id + " port " + p(s.PortX.Value, s.PortY.Value) + " " + p(port[0], port[1]));
            }
            return mismatches;
        }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<SettlementIntent> Settlements = new List<SettlementIntent>();
            public List<Slot> Slots = new List<Slot>();
            public SortedDictionary<string, string> Rebound = new SortedDictionary<string, string>(StringComparer.Ordinal);   // village -> its new bound settlement
            public SortedDictionary<string, string> ReboundReason = new SortedDictionary<string, string>(StringComparer.Ordinal);   // village -> distance, castle or balance
            public double Spacing, PreferredLimit, EngineLimit;
            public SortedDictionary<string, object> Balance = new SortedDictionary<string, object>(StringComparer.Ordinal);
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public string IntentsJson, IntentsSha256;
        }

        public sealed class SettlementException : InvalidOperationException
        {
            public SettlementException(IList<string> problems) : base("Settlements refused: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; }
            public IList<string> Problems { get; private set; }
        }

        private readonly IDictionary<string, object> policy, prototypes;
        private readonly Dictionary<string, XmlElement> catalog = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        private readonly Func<string, bool> defaults;
        private readonly MNBL.ProceduralWorld.Missions.MissionScenes scenes;   // MMR-42 (null: no mission scene contracts, no prototypeAndScenes slots)
        private readonly Dictionary<string, int> streams = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> problems = new List<string>();
        private string prefix;
        private double width;   // the periodic world's east-west extent (0: not periodic)

        // economy: worldgen/policies/economy.json. prototypes: worldgen/templates/settlements/prototypes.json. settlements: the merged
        // catalog's Settlements document (the prototypes' assets). defaults: whether a typed engine-default reference (VillageType.x,
        // BuildingType.x) is registered (CharacterTemplateResolver.Exists).
        // missionScenes: the MMR-42 mission scene catalogue (worldgen/policies/mission-scenes.json); a pick whose locations fail its contracts is
        // re-picked among the pool's conforming prototypes, and towns and castles claim their prototypeAndScenes slot.
        public SettlementFactory(IDictionary<string, object> economy, IDictionary<string, object> prototypes, XmlDocument settlements, Func<string, bool> defaults,
                                 MNBL.ProceduralWorld.Missions.MissionScenes missionScenes = null)
        {
            policy = economy; this.prototypes = prototypes; this.defaults = defaults; scenes = missionScenes;
            foreach (var e in settlements.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "Settlement")) catalog[e.GetAttribute("id")] = e;
            foreach (var name in new[] { "prototypes", "villageTypes" }) streams[name] = Int(Obj(policy, "streams"), name);
        }

        // shipyardLevels (MMR-35, NavalContentFactory.ShipyardLevels): port town -> its shipyard level; without it every port town takes the
        // economy policy's level.
        public Result Build(IDictionary<string, object> campaign, IDictionary<string, object> travel, IDictionary<string, object> ports,
                            FactionFactory.Result factions, HeroFactory.Result heroes, IDictionary<string, int> shipyardLevels = null)
        {
            var result = new Result { WorldPrefix = factions.WorldPrefix };
            prefix = factions.WorldPrefix;
            if (Text(policy, "schema") != "mnbl.economy" || Int(policy, "version") != 2) problems.Add("not mnbl.economy version 2");
            var grid = Obj(travel, "grid");
            width = Int(grid, "cols") * Double(grid, "spacing");
            if (double.IsNaN(width) || width <= 0) problems.Add("the travel graph has no grid extent");
            if (Text(prototypes, "schema") != "mnbl.settlement-prototypes") problems.Add("not mnbl.settlement-prototypes");
            var towns = Records(campaign, "towns").OrderBy(t => Text(t, "id"), StringComparer.Ordinal).ToList();
            var castles = Records(campaign, "castles").OrderBy(c => Text(c, "id"), StringComparer.Ordinal).ToList();
            var villages = Records(campaign, "villages").OrderBy(v => Text(v, "id"), StringComparer.Ordinal).ToList();
            var points = Records(travel, "settlements").ToDictionary(s => Text(s, "id"), StringComparer.Ordinal);
            var portOf = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
            var harbourOf = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);   // harbour village -> its candidate
            foreach (var p in Records(ports, "ports"))
            {
                var chosen = Obj(p, "chosen"); if (chosen != null && Text(p, "town") != null) portOf[Text(p, "town")] = chosen;
                foreach (var c in Records(p, "candidates")) if (Text(c, "village") != null && List(c, "harbourCell").Count() == 2) harbourOf[Text(c, "village")] = c;
            }
            var clans = factions.Clans.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var kingdoms = factions.Kingdoms.ToDictionary(k => k.Id, StringComparer.Ordinal);
            var heroesById = Records(campaign, "heroes").ToDictionary(h => Text(h, "id"), StringComparer.Ordinal);

            // Towns: class tercile prosperity at the town's population percentile within its class.
            var townTiers = Obj(Obj(policy, "prosperity"), "towns");
            foreach (var t in towns)
            {
                string id = Text(t, "id"), cls = Text(t, "cityClass");
                if (double.IsNaN(Double(Obj(t, "population"), "estimate"))) problems.Add("town " + id + " has no population estimate");
                var peers = towns.Where(x => Text(x, "cityClass") == cls).Select(x => Population(x)).ToList();
                var tier = List(townTiers, cls).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
                if (tier.Count == 0) { problems.Add("town " + id + " class " + cls + " has no prosperity tercile"); continue; }
                var s = Base(t, "town", points, kingdoms);
                s.Culture = Text(t, "presentation");
                s.CityClass = cls;
                s.Prosperity = Quantile(tier, Percentile(Population(t), peers));
                s.Port = Bool(t, "port") && portOf.ContainsKey(id);
                if (Bool(t, "port") && !portOf.ContainsKey(id)) problems.Add("port town " + id + " has no MMR-28 port candidate");
                if (s.Port) { var sea = Obj(portOf[id], "seaPoint"); s.PortX = Double(sea, "x"); s.PortY = Double(sea, "y"); }
                foreach (var b in Obj(Obj(Obj(policy, "buildings"), "towns"), cls)) if (b.Key != "building_shipyard") s.Buildings[b.Key] = Convert.ToInt32(b.Value, CultureInfo.InvariantCulture);
                int naval;
                if (s.Port) s.Buildings["building_shipyard"] = shipyardLevels != null && shipyardLevels.TryGetValue(id, out naval) ? naval : Int(Obj(policy, "buildings"), "shipyard");
                string kind = s.Port ? "town_port" : "town_inland";
                var pool = Prototypes(s.Culture, kind);
                if (pool.Count == 0 && !s.Port) { pool = Prototypes(s.Culture, "town_port"); s.AdaptedPrototype = true; }
                s.Prototype = Pick("prototypes", id, pool);
                s.Prototype = Conform(s, pool);
                s.Text = Fill(Text(Obj(policy, "texts"), "town"), new Dictionary<string, string> { ["kind"] = cls == "capital" ? "capital" : "town", ["name"] = s.SourceName, ["realm"] = Realm(s.Kingdom, kingdoms) });
                result.Settlements.Add(s);
            }
            var townPopulations = towns.Select(Population).ToList();
            var townById = towns.ToDictionary(t => Text(t, "id"), StringComparer.Ordinal);
            // Castles: owned by their castellan's vassal clan when the castle has one (hero uid "hero-castellan-<castle uid>"), else by the
            // compiler's owner; prosperity at their garrison town's population percentile.
            var castleProsperity = List(Obj(Obj(policy, "prosperity"), "castles"), "values").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
            foreach (var c in castles)
            {
                string id = Text(c, "id");
                var s = Base(c, "castle", points, kingdoms);
                s.Culture = CultureOf(heroes, id);
                var castellan = heroesById.Values.FirstOrDefault(h => Text(h, "uid") == "hero-castellan-" + Text(c, "uid"));
                var clan = castellan == null ? null : factions.Clans.FirstOrDefault(x => !x.Ruling && x.LeaderId == Text(castellan, "id"));
                if (clan != null) { s.Owner = clan.Id; s.Provenance += "; owned by the vassal clan of castellan " + Text(castellan, "id"); }
                IDictionary<string, object> garrison;
                s.Prosperity = townById.TryGetValue(Text(c, "garrisonTown") ?? "", out garrison) ? Quantile(castleProsperity, Percentile(Population(garrison), townPopulations)) : Quantile(castleProsperity, 0.5);
                foreach (var b in Obj(Obj(policy, "buildings"), "castles")) s.Buildings[b.Key] = Convert.ToInt32(b.Value, CultureInfo.InvariantCulture);
                var castlePool = Prototypes(s.Culture, "castle");
                s.Prototype = Pick("prototypes", id, castlePool);
                s.Prototype = Conform(s, castlePool);
                s.MapPrefab = MapPrefab(s.Prototype);
                s.Text = Fill(Text(Obj(policy, "texts"), "castle"), new Dictionary<string, string> { ["name"] = s.SourceName, ["realm"] = Realm(s.Kingdom, kingdoms) });
                result.Settlements.Add(s);
            }
            // Villages. Binding (economy.json binding, VillageBinding: the world preview binds with the same function): (1) the compiler's town;
            // (2) distance; (3) castles from their garrison town; (4) castles from over-full towns of their kingdom. Hearth at the village's share
            // percentile of its compiler town's rural population.
            var byId = result.Settlements.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var binding = VillageBinding.Bind(policy, towns, castles, villages, points,
                result.Settlements.Where(x => x.Kind == "town" || x.Kind == "castle").Select(x => new VillageBinding.Fortification { Id = x.Id, Kind = x.Kind, Kingdom = x.Kingdom, X = x.X, Y = x.Y, LandGroup = x.LandGroup }).ToList(),
                width, problems);
            var boundOf = binding.BoundOf; var remote = binding.Remote;
            result.Spacing = binding.Spacing; result.PreferredLimit = binding.PreferredLimit; result.EngineLimit = binding.EngineLimit;
            foreach (var kv in binding.Rebound) result.Rebound[kv.Key] = kv.Value;
            foreach (var kv in binding.ReboundReason) result.ReboundReason[kv.Key] = kv.Value;
            var share = villages.ToDictionary(v => Text(v, "id"), v => {
                IDictionary<string, object> town; townById.TryGetValue(Text(v, "bound") ?? "", out town);
                int count = villages.Count(x => Text(x, "bound") == Text(v, "bound"));
                return town == null || count == 0 ? 0.0 : Double(Obj(town, "population"), "rural") / count; }, StringComparer.Ordinal);
            foreach (var v in villages) if (double.IsNaN(share[Text(v, "id")])) problems.Add("village " + Text(v, "id") + " has no rural population share (its town has no rural population)");
            var hearths = List(Obj(policy, "hearth"), "values").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
            var cell = Double(grid, "spacing");
            foreach (var v in villages)
            {
                string id = Text(v, "id");
                var s = Base(v, "village", points, kingdoms);
                s.Culture = CultureOf(heroes, id);
                s.Bound = boundOf[id]; s.SourceBound = Text(v, "bound");
                if (result.Rebound.ContainsKey(id)) s.Provenance += "; rebound (" + result.ReboundReason[id] + ") from town " + Text(v, "bound") + " to " + s.Bound;
                SettlementIntent bound;
                if (!byId.TryGetValue(s.Bound ?? "", out bound)) { problems.Add("village " + id + " is bound to " + s.Bound + ", which is not a generated town or castle"); continue; }
                s.Owner = null; s.Kingdom = bound.Kingdom;
                s.BoundDistance = Dist(s.X, s.Y, bound.X, bound.Y); s.Remote = remote.Contains(id);
                s.Hearth = Quantile(hearths, Percentile(share[id], share.Values.ToList()));
                s.Production = Text(v, "production");
                // A harbour village's port: the centre of its MMR-28 harbour cell (row, column), a navigable sea cell.
                s.Harbour = Bool(v, "harbour");
                IDictionary<string, object> harbour;
                if (s.Harbour && harbourOf.TryGetValue(id, out harbour))
                {
                    var hc = List(harbour, "harbourCell").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
                    s.HarbourCell = hc; s.Port = true; s.PortX = (hc[1] + 0.5) * cell; s.PortY = (hc[0] + 0.5) * cell;
                }
                else if (s.Harbour) problems.Add("harbour village " + id + " has no MMR-28 harbour cell");
                s.VillageType = Weighted("villageTypes", id, VillagePool(s.Culture, s.Production, s.Port));
                var pool = Prototypes(s.Culture, s.Port ? "village_coastal" : "village_inland");
                if (pool.Count == 0) { pool = Prototypes(s.Culture, s.Port ? "village_inland" : "village_coastal"); s.AdaptedPrototype = true; }
                s.Prototype = Pick("prototypes", id, pool);
                s.Prototype = Conform(s, pool);
                s.MapPrefab = MapPrefab(s.Prototype);
                s.Text = Fill(Text(Obj(policy, "texts"), "village"), new Dictionary<string, string> { ["name"] = s.SourceName, ["bound"] = bound.SourceName });
                result.Settlements.Add(s);
            }
            // A castle's villages trade with the nearest town of the castle's kingdom within the engine limit (VillageTradeBoundCampaignBehavior).
            var allTowns = result.Settlements.Where(x => x.Kind == "town").OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            foreach (var s in result.Settlements.Where(x => x.Kind == "village" && byId.ContainsKey(x.Bound ?? "") && byId[x.Bound].Kind == "castle"))
            {
                var trade = allTowns.Where(t => t.Kingdom == s.Kingdom && Dist(s.X, s.Y, t.X, t.Y) <= result.EngineLimit).OrderBy(t => Dist(s.X, s.Y, t.X, t.Y)).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
                s.TradeTown = trade == null ? null : trade.Id;
            }
            FoodPass(result);
            Validate(result, clans, villages, towns.Count, castles.Count);
            Slots(result);
            Balance(result);
            if (problems.Count > 0) throw new SettlementException(problems.Distinct().ToList());
            var doc = new XmlDocument(); doc.AppendChild(doc.CreateElement("Settlements"));
            foreach (var s in result.Settlements.OrderBy(x => x.Kind == "village" ? 1 : 0).ThenBy(x => x.Id, StringComparer.Ordinal))
                doc.DocumentElement.AppendChild(Element(doc, s, catalog[s.Prototype]));
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Settlements", doc));
            foreach (var s in result.Settlements)
            {
                result.DeclaredKeys.Add("Settlement." + s.Id);
                result.DeclaredKeys.Add((s.Kind == "village" ? "Village." : "Town.") + s.Id + "_component");
            }
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        // The one settlement builder (world and fixtures): the prototype's component, location complex and common areas as assets; the
        // intent's identity, positions, owner, culture, economy and text.
        public static XmlElement Element(XmlDocument doc, SettlementIntent s, XmlElement prototype)
        {
            var e = doc.CreateElement("Settlement");
            e.SetAttribute("id", s.Id);
            e.SetAttribute("name", FactionFactory.Localized(s.Id, s.SourceName));
            if (s.Kind == "hideout") e.SetAttribute("type", "Hideout");
            if (s.Owner != null) e.SetAttribute("owner", "Faction." + s.Owner);
            e.SetAttribute("posX", Number(s.X)); e.SetAttribute("posY", Number(s.Y));
            e.SetAttribute("culture", "Culture." + s.Culture);
            if (s.Kind == "town" || s.Kind == "castle") { e.SetAttribute("gate_posX", Number(s.GateX)); e.SetAttribute("gate_posY", Number(s.GateY)); }
            if (s.Port && s.PortX.HasValue) { e.SetAttribute("port_posX", Number(s.PortX.Value)); e.SetAttribute("port_posY", Number(s.PortY.Value)); }
            if (s.Text != null) e.SetAttribute("text", FactionFactory.Localized(s.Id + "_text", s.Text));
            var components = doc.CreateElement("Components"); e.AppendChild(components);
            var source = prototype.SelectSingleNode("Components/*") as XmlElement;
            var c = (XmlElement)doc.ImportNode(source, true);
            c.SetAttribute("id", s.Id + "_component");
            if (s.Kind == "town" || s.Kind == "castle")
            {
                c.SetAttribute("is_castle", s.Kind == "castle" ? "true" : "false");
                c.SetAttribute("prosperity", s.Prosperity.ToString(CultureInfo.InvariantCulture));
                var buildings = c.SelectSingleNode("Buildings") as XmlElement;
                if (buildings != null) c.RemoveChild(buildings);
                buildings = doc.CreateElement("Buildings"); c.AppendChild(buildings);
                foreach (var b in s.Buildings) { var be = doc.CreateElement("Building"); be.SetAttribute("id", b.Key); be.SetAttribute("level", b.Value.ToString(CultureInfo.InvariantCulture)); buildings.AppendChild(be); }
            }
            else if (s.Kind == "village")
            {
                c.SetAttribute("village_type", "VillageType." + s.VillageType);
                c.SetAttribute("hearth", s.Hearth.ToString(CultureInfo.InvariantCulture));
                c.SetAttribute("bound", "Settlement." + s.Bound);
            }
            components.AppendChild(c);
            var locations = prototype.SelectSingleNode("Locations") as XmlElement;
            if (locations != null)
            {
                var l = (XmlElement)doc.ImportNode(locations, true);
                // The location complex template defines a port location for every town (LocationComplex.Initialize); an inland town keeps
                // no port scene override, as the installed inland towns.
                if (s.Kind == "town" && !s.Port) foreach (var port in l.SelectNodes("Location[@id='port']").OfType<XmlElement>().ToList()) l.RemoveChild(port);
                e.AppendChild(l);
            }
            var areas = prototype.SelectSingleNode("CommonAreas") as XmlElement;
            if (areas != null) e.AppendChild(doc.ImportNode(areas, true));
            return e;
        }

        // A hideout from a placed specification (MMR-34 places hideouts across a world; the fixtures place one): a prototype of the
        // bandit culture, the specification's id, name and position.
        public SettlementIntent Hideout(string id, string name, string banditCulture, double x, double y)
        {
            var s = new SettlementIntent { Id = id, Uid = "hideout:" + id, Kind = "hideout", Name = FactionFactory.Localized(id, name), SourceName = name, Culture = banditCulture,
                                           X = x, Y = y, GateX = x, GateY = y, Provenance = "placed hideout specification" };
            prefix = prefix ?? "";
            var hideoutPool = Prototypes(banditCulture, "hideout", true);
            s.Prototype = Pick("prototypes", id, hideoutPool);
            s.Prototype = Conform(s, hideoutPool);
            s.MapPrefab = MapPrefab(s.Prototype);
            s.Text = Fill(Text(Obj(policy, "texts"), "hideout"), new Dictionary<string, string>());
            if (problems.Count > 0) throw new SettlementException(problems.ToList());
            return s;
        }

        // A settlement of the in-game fixture world from a specification (kind town/castle/village), through the same rules as the world:
        // installed prototype of its culture and kind, policy buildings, a prosperity/hearth/village type given by the specification.
        public SettlementIntent Fixture(string id, string kind, string name, string culture, string owner, string bound, double x, double y, double gateX, double gateY,
                                        double? portX, double? portY, int prosperity, int hearth, string villageType)
        {
            prefix = prefix ?? "";
            var s = new SettlementIntent { Id = id, Uid = "fixture:" + id, Kind = kind, Name = FactionFactory.Localized(id, name), SourceName = name, Culture = culture,
                                           Owner = kind == "village" ? null : owner, Bound = bound, X = x, Y = y, GateX = gateX, GateY = gateY, PortX = portX, PortY = portY,
                                           Port = portX.HasValue, Prosperity = prosperity, Hearth = hearth, VillageType = villageType, Provenance = "MMR-33 in-game fixture" };
            if (kind == "town")
            {
                foreach (var b in Obj(Obj(Obj(policy, "buildings"), "towns"), "medium")) if (b.Key != "building_shipyard") s.Buildings[b.Key] = Convert.ToInt32(b.Value, CultureInfo.InvariantCulture);
                if (s.Port) s.Buildings["building_shipyard"] = Int(Obj(policy, "buildings"), "shipyard");
                var pool = Prototypes(culture, s.Port ? "town_port" : "town_inland");
                if (pool.Count == 0 && !s.Port) { pool = Prototypes(culture, "town_port"); s.AdaptedPrototype = true; }
                s.Prototype = Pick("prototypes", id, pool);
                s.Prototype = Conform(s, pool);
            }
            else if (kind == "castle")
            {
                foreach (var b in Obj(Obj(policy, "buildings"), "castles")) s.Buildings[b.Key] = Convert.ToInt32(b.Value, CultureInfo.InvariantCulture);
                var castlePool = Prototypes(culture, "castle", true);
                s.Prototype = Pick("prototypes", id, castlePool);
                s.Prototype = Conform(s, castlePool);
            }
            else
            {
                var villagePool = Prototypes(culture, s.Port ? "village_coastal" : "village_inland", true);
                s.Prototype = Pick("prototypes", id, villagePool);
                s.Prototype = Conform(s, villagePool);
            }
            s.MapPrefab = MapPrefab(s.Prototype);
            s.Text = Fill(Text(Obj(policy, "texts"), kind == "town" ? "town" : kind), new Dictionary<string, string> { ["kind"] = "town", ["name"] = name, ["realm"] = "fixture realm", ["bound"] = bound ?? "" });
            if (problems.Count > 0) throw new SettlementException(problems.ToList());
            return s;
        }

        public XmlElement Prototype(string id) { XmlElement e; return catalog.TryGetValue(id ?? "", out e) ? e : null; }

        // The vanilla map prefab of a prototype (prototypes.json mapPrefab), or null.
        public string MapPrefab(string prototypeId)
        {
            foreach (var culture in Obj(prototypes, "cultures") ?? new Dictionary<string, object>())
                foreach (var kind in (IDictionary<string, object>)culture.Value)
                    foreach (var p in List((IDictionary<string, object>)culture.Value, kind.Key).OfType<IDictionary<string, object>>())
                        if (Text(p, "id") == prototypeId) return Text(p, "mapPrefab");
            return null;
        }

        private SettlementIntent Base(IDictionary<string, object> r, string kind, Dictionary<string, IDictionary<string, object>> points, Dictionary<string, FactionFactory.KingdomIntent> kingdoms)
        {
            string id = Text(r, "id");
            var s = new SettlementIntent { Id = id, Uid = Text(r, "uid"), Kind = kind, SourceName = FactionFactory.Clean(Text(r, "name")), Owner = Text(r, "owner"), Kingdom = Text(r, "kingdom"),
                                           Provenance = "MMR-27 " + kind + " (" + Text(r, "nameProvenance") + ")" };
            s.Name = FactionFactory.Localized(id, s.SourceName);
            IDictionary<string, object> point;
            if (!points.TryGetValue(id, out point)) { problems.Add(kind + " " + id + " has no MMR-28 position"); return s; }
            var p = Obj(point, "point");
            s.X = Double(p, "x"); s.Y = Double(p, "y"); s.GateX = s.X; s.GateY = s.Y; s.LandGroup = Int(point, "group");
            return s;
        }

        // Culture of a castle or village: the MMR-32 settlement culture (a town takes its MMR-27 presentation).
        private string CultureOf(HeroFactory.Result heroes, string id)
        {
            string culture;
            if (!heroes.SettlementCultures.TryGetValue(id, out culture) || culture == null) { problems.Add("settlement " + id + " has no MMR-32 settlement culture"); return null; }
            return culture;
        }

        private void Validate(Result r, Dictionary<string, FactionFactory.ClanIntent> clans, IList<IDictionary<string, object>> villages, int townCount, int castleCount)
        {
            var byId = r.Settlements.ToDictionary(s => s.Id, StringComparer.Ordinal);
            if (r.Settlements.Count(s => s.Kind == "village") != villages.Count || r.Settlements.Count(s => s.Kind == "town") != townCount || r.Settlements.Count(s => s.Kind == "castle") != castleCount)
                problems.Add("not every compiler settlement became a settlement");
            var townRange = List(Obj(Obj(policy, "prosperity"), "towns"), "small").Concat(List(Obj(Obj(policy, "prosperity"), "towns"), "capital")).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
            var castleRange = List(Obj(Obj(policy, "prosperity"), "castles"), "values").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
            var hearthRange = List(Obj(policy, "hearth"), "values").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
            var binding = Obj(policy, "binding");
            int castleVillages = Int(binding, "castleVillages"), townMinimum = Int(binding, "townMinimum"), townMaximum = Int(binding, "townMaximum");
            var fishing = new HashSet<string>(List(Obj(policy, "villageTypes"), "fishingTypes").Select(x => (string)x), StringComparer.Ordinal);
            var villagePorts = Obj(Obj(policy, "positions"), "villagePorts");
            double portEnvelope = Double(villagePorts, "installedPortCentreMax") + Double(villagePorts, "allowance");
            foreach (var s in r.Settlements)
            {
                string p = CampaignIdentity.Problem(s.Id, prefix);
                if (p != null) problems.Add("settlement " + s.Id + ": " + p);
                if (s.Prototype == null || !catalog.ContainsKey(s.Prototype)) problems.Add(s.Kind + " " + s.Id + " culture " + s.Culture + " has no installed " + s.Kind + " prototype");
                if (double.IsNaN(s.X) || double.IsNaN(s.Y) || double.IsInfinity(s.X) || double.IsInfinity(s.Y)) problems.Add(s.Kind + " " + s.Id + " has no finite position");
                if (s.Kind == "town" || s.Kind == "castle")
                {
                    if (s.Owner == null || !clans.ContainsKey(s.Owner)) problems.Add(s.Kind + " " + s.Id + " owner " + s.Owner + " is not a clan intent");
                    else if (clans[s.Owner].KingdomId != s.Kingdom) problems.Add(s.Kind + " " + s.Id + " owner " + s.Owner + " is not of its kingdom " + s.Kingdom);
                    var range = s.Kind == "town" ? townRange : castleRange;
                    if (s.Prosperity < range.Min() || s.Prosperity > range.Max()) problems.Add(s.Kind + " " + s.Id + " prosperity " + s.Prosperity + " leaves the installed range");
                    foreach (var b in s.Buildings.Keys) if (!defaults("BuildingType." + b)) problems.Add(s.Kind + " " + s.Id + " building " + b + " is not a registered building type");
                    int bound = r.Settlements.Count(v => v.Kind == "village" && v.Bound == s.Id);
                    if (bound == 0) problems.Add(s.Kind + " " + s.Id + " has no bound village");
                    else if (s.Kind == "castle" && bound > castleVillages) problems.Add("castle " + s.Id + " has " + bound + " villages, more than " + castleVillages);
                    else if (s.Kind == "town" && bound > townMaximum) problems.Add("town " + s.Id + " has " + bound + " villages, more than " + townMaximum);
                    else if (s.Kind == "town" && bound < townMinimum && villages.Count(v => Text(v, "bound") == s.Id) >= townMinimum)
                        problems.Add("town " + s.Id + " was left " + bound + " villages of the source's " + villages.Count(v => Text(v, "bound") == s.Id));
                }
                else if (s.Kind == "village")
                {
                    SettlementIntent b;
                    if (!byId.TryGetValue(s.Bound ?? "", out b) || (b.Kind != "town" && b.Kind != "castle")) problems.Add("village " + s.Id + " is not bound to a town or castle");
                    else
                    {
                        if (b.LandGroup != s.LandGroup) problems.Add("village " + s.Id + " is bound across land groups");
                        if (!s.Remote && s.BoundDistance > r.PreferredLimit) problems.Add("village " + s.Id + " is " + Math.Round(s.BoundDistance, 1) + " from " + s.Bound + ", beyond the binding limit " + Math.Round(r.PreferredLimit, 1));
                        if (s.BoundDistance > r.EngineLimit) problems.Add("village " + s.Id + " is " + Math.Round(s.BoundDistance, 1) + " from " + s.Bound + ", beyond the engine trade limit " + Math.Round(r.EngineLimit, 1));
                        if (b.Kind == "castle" && s.TradeTown == null) problems.Add("castle village " + s.Id + " has no town of its kingdom within the engine trade limit " + Math.Round(r.EngineLimit, 1));
                    }
                    if (s.Hearth < hearthRange.Min() || s.Hearth > hearthRange.Max()) problems.Add("village " + s.Id + " hearth " + s.Hearth + " leaves the installed range");
                    if (s.VillageType == null || !defaults("VillageType." + s.VillageType)) problems.Add("village " + s.Id + " village type " + s.VillageType + " is not registered");
                    if (fishing.Contains(s.VillageType ?? "") && !s.Port) problems.Add("village " + s.Id + " is a " + s.VillageType + " without a port");
                    if (s.Port && Dist(s.X, s.Y, s.PortX.Value, s.PortY.Value) > portEnvelope) problems.Add("village " + s.Id + " port is " + Math.Round(Dist(s.X, s.Y, s.PortX.Value, s.PortY.Value), 1) + " from its centre, beyond " + portEnvelope);
                }
                if (s.Culture == null || !Obj(prototypes, "cultures").ContainsKey(s.Culture)) problems.Add(s.Kind + " " + s.Id + " culture " + s.Culture + " has no prototypes");
            }
            // Smaller-world balance: villages and hearth per fief within the policy band of the installed values.
            var balance = Obj(policy, "balance"); var band = List(balance, "band").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToList();
            int fiefs = townCount + castleCount;
            var villageIntents = r.Settlements.Where(s => s.Kind == "village").ToList();
            foreach (var measure in new[] { new KeyValuePair<string, double>("villagesPerFief", fiefs == 0 ? 0 : villageIntents.Count / (double)fiefs),
                                            new KeyValuePair<string, double>("hearthPerFief", fiefs == 0 ? 0 : villageIntents.Sum(v => v.Hearth) / (double)fiefs) })
            {
                double installed = Double(Obj(balance, "installed"), measure.Key), ratio = measure.Value / installed;
                if (band.Count != 2 || double.IsNaN(ratio) || ratio < band[0] || ratio > band[1]) problems.Add(measure.Key + " " + Math.Round(measure.Value, 2) + " is " + Math.Round(ratio, 2) + " x the installed " + installed + ", outside the band");
            }
            // Food: every town's trade-area supply reaches the policy minimum (after the food pass).
            int minimum = Int(Obj(policy, "food"), "townMinimum");
            foreach (var t in r.Settlements.Where(s => s.Kind == "town"))
            {
                int supply = TradeArea(r, t.Id).Sum(v => FoodOf(v.VillageType));
                if (supply < minimum) problems.Add("town " + t.Id + " trade-area food supply " + supply + " is below " + minimum);
            }
        }

        // The villages that bring their goods to a town: its own, and those of the castles whose trade town it is.
        private static IEnumerable<SettlementIntent> TradeArea(Result r, string town)
        {
            return r.Settlements.Where(v => v.Kind == "village" && (v.Bound == town || v.TradeTown == town));
        }

        private int FoodOf(string villageType) { return Int(Obj(Obj(policy, "food"), "villageTypes"), villageType ?? ""); }

        // A town's trade-area food supply: the daily food production of its own villages and of the castle villages trading with it.
        public int TradeAreaFood(Result r, string town) { return TradeArea(r, town).Sum(v => FoodOf(v.VillageType)); }

        // The village types a village may take: its culture's installed types of its production class (all its types when the culture
        // has none of that class); without a port, the class without the fishing types, else the culture's farming types.
        private IDictionary<string, object> VillagePool(string culture, string production, bool port)
        {
            var pools = Obj(Obj(Obj(policy, "villageTypes"), "pools"), culture ?? "") ?? new Dictionary<string, object>();
            var fishing = new HashSet<string>(List(Obj(policy, "villageTypes"), "fishingTypes").Select(x => (string)x), StringComparer.Ordinal);
            Func<IDictionary<string, object>, IDictionary<string, object>> allowed = p => {
                var o = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var w in p ?? new Dictionary<string, object>()) if (port || !fishing.Contains(w.Key)) o[w.Key] = Convert.ToInt32(w.Value, CultureInfo.InvariantCulture) + (o.ContainsKey(w.Key) ? (int)o[w.Key] : 0);
                return o; };
            var all = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in pools) foreach (var w in (IDictionary<string, object>)p.Value) all[w.Key] = Convert.ToInt32(w.Value, CultureInfo.InvariantCulture) + (all.ContainsKey(w.Key) ? (int)all[w.Key] : 0);
            var classPool = Obj(pools, production ?? "");
            var pick = allowed(classPool != null && classPool.Count > 0 ? classPool : all);
            if (pick.Count == 0) pick = allowed(Obj(pools, "farming"));
            if (pick.Count == 0) pick = allowed(all);
            return pick;
        }

        // A town whose trade-area food supply is below the policy minimum: its trade-area village with the least food (nearest the town
        // first) takes the most food-producing type its culture allows for its class (else among its culture's farming types), until the
        // town reaches the minimum.
        private void FoodPass(Result r)
        {
            int minimum = Int(Obj(policy, "food"), "townMinimum");
            foreach (var t in r.Settlements.Where(s => s.Kind == "town").OrderBy(s => s.Id, StringComparer.Ordinal).ToList())
            {
                while (TradeArea(r, t.Id).Sum(v => FoodOf(v.VillageType)) < minimum)
                {
                    bool changed = false;
                    foreach (var v in TradeArea(r, t.Id).OrderBy(v => FoodOf(v.VillageType)).ThenBy(v => Dist(v.X, v.Y, t.X, t.Y)).ThenBy(v => v.Id, StringComparer.Ordinal))
                    {
                        var options = VillagePool(v.Culture, v.Production, v.Port).Keys.Concat(VillagePool(v.Culture, "farming", v.Port).Keys);
                        var best = options.Where(x => defaults("VillageType." + x)).OrderByDescending(FoodOf).ThenBy(x => x, StringComparer.Ordinal).FirstOrDefault();
                        if (best == null || FoodOf(best) <= FoodOf(v.VillageType)) continue;
                        v.FoodRedraw = v.FoodRedraw ?? v.VillageType; v.VillageType = best; changed = true;
                        v.Provenance += "; village type " + v.FoodRedraw + " redrawn to " + best + " for town " + t.Id + "'s food supply";
                        break;
                    }
                    if (!changed) break;   // Validate refuses the town
                }
            }
        }

        private void Slots(Result r)
        {
            foreach (var s in r.Settlements)
            {
                // MMR-42: a town's or castle's prototype and its location scenes, checked against the mission scene contracts (C04/C06 references).
                if (scenes != null && (s.Kind == "town" || s.Kind == "castle") && s.Prototype != null)
                {
                    var value = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["prototype"] = s.Prototype,
                        ["contracts"] = s.Kind == "town" && s.Port ? "settlementCenter, siege, siegeLordsHall, port" : "settlementCenter, siege, siegeLordsHall" };
                    foreach (var kv in scenes.ReferenceRecord(PrototypeLocations(s))) value[kv.Key] = kv.Value;
                    if (s.SceneRepick != null) value["repicked"] = s.SceneRepick;
                    r.Slots.Add(new Slot { Owner = s.Id, Kind = s.Kind, Name = "prototypeAndScenes", Value = value });
                }
                if (s.Kind == "town")
                    r.Slots.Add(new Slot { Owner = s.Id, Kind = "town", Name = "prosperityAndEconomy", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                        ["prosperity"] = s.Prosperity, ["class"] = s.CityClass, ["buildings"] = s.Buildings, ["boundVillages"] = r.Settlements.Count(v => v.Bound == s.Id),
                        ["startingValues"] = "food stocks, militia, gold, workshops and garrison follow from prosperity and bound villages through the installed new-game behaviours" } });
                if (s.Kind == "village")
                {
                    r.Slots.Add(new Slot { Owner = s.Id, Kind = "village", Name = "villageType", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["villageType"] = s.VillageType, ["production"] = s.Production } });
                    r.Slots.Add(new Slot { Owner = s.Id, Kind = "village", Name = "hearth", Value = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["hearth"] = s.Hearth } });
                }
            }
        }

        // Smaller-world balance: the generated world's distributions beside the installed ones (Validate enforces the policy bounds).
        private void Balance(Result r)
        {
            var towns = r.Settlements.Where(s => s.Kind == "town").ToList(); var castles = r.Settlements.Where(s => s.Kind == "castle").ToList();
            var villages = r.Settlements.Where(s => s.Kind == "village").ToList();
            var installed = Obj(Obj(policy, "balance"), "installed");
            int fiefs = towns.Count + castles.Count;
            r.Balance["towns"] = towns.Count; r.Balance["castles"] = castles.Count; r.Balance["villages"] = villages.Count;
            r.Balance["villagesPerTown"] = towns.GroupBy(t => villages.Count(v => v.Bound == t.Id)).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count());
            r.Balance["villagesPerCastle"] = castles.GroupBy(c => villages.Count(v => v.Bound == c.Id)).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count());
            r.Balance["meanTownProsperity"] = towns.Count == 0 ? 0 : Math.Round(towns.Average(t => t.Prosperity), 1);
            r.Balance["meanCastleProsperity"] = castles.Count == 0 ? 0 : Math.Round(castles.Average(t => t.Prosperity), 1);
            r.Balance["meanHearth"] = villages.Count == 0 ? 0 : Math.Round(villages.Average(v => v.Hearth), 1);
            double perFief = fiefs == 0 ? 0 : villages.Count / (double)fiefs, hearthPerFief = fiefs == 0 ? 0 : villages.Sum(v => v.Hearth) / (double)fiefs;
            r.Balance["villagesPerFief"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["value"] = Math.Round(perFief, 3), ["installed"] = Double(installed, "villagesPerFief"), ["ratio"] = Math.Round(perFief / Double(installed, "villagesPerFief"), 3) };
            r.Balance["hearthPerFief"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["value"] = Math.Round(hearthPerFief, 1), ["installed"] = Double(installed, "hearthPerFief"), ["ratio"] = Math.Round(hearthPerFief / Double(installed, "hearthPerFief"), 3) };
            r.Balance["binding"] = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["spacing"] = Math.Round(r.Spacing, 2), ["preferredLimit"] = Math.Round(r.PreferredLimit, 2), ["engineLimit"] = Math.Round(r.EngineLimit, 2),
                ["rebound"] = r.ReboundReason.GroupBy(x => x.Value).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                ["crossKingdom"] = villages.Count(v => r.Rebound.ContainsKey(v.Id) && towns.Any(t => t.Id == v.SourceBound && t.Kingdom != v.Kingdom)),
                ["remote"] = villages.Where(v => v.Remote).OrderBy(v => v.Id, StringComparer.Ordinal).ToDictionary(v => v.Id, v => (object)new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["bound"] = v.Bound, ["distance"] = Math.Round(v.BoundDistance, 1), ["ratio"] = Math.Round(v.BoundDistance / r.Spacing, 2) }),
                ["maxDistance"] = villages.Count == 0 ? 0 : Math.Round(villages.Max(v => v.BoundDistance), 1),
                ["maxDistanceNotRemote"] = villages.Count(v => !v.Remote) == 0 ? 0 : Math.Round(villages.Where(v => !v.Remote).Max(v => v.BoundDistance), 1),
                ["townsBelowMinimumBySource"] = towns.Where(t => villages.Count(v => v.Bound == t.Id) < Int(Obj(policy, "binding"), "townMinimum")).Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList() };
            var supply = towns.Select(t => TradeArea(r, t.Id).Sum(v => FoodOf(v.VillageType))).OrderBy(x => x).ToList();
            r.Balance["food"] = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["townMinimum"] = Int(Obj(policy, "food"), "townMinimum"), ["minimum"] = supply.Count == 0 ? 0 : supply[0], ["p10"] = supply.Count == 0 ? 0 : supply[supply.Count / 10],
                ["median"] = supply.Count == 0 ? 0 : supply[supply.Count / 2], ["redrawn"] = villages.Where(v => v.FoodRedraw != null).OrderBy(v => v.Id, StringComparer.Ordinal).ToDictionary(v => v.Id, v => v.FoodRedraw + ">" + v.VillageType),
                ["landGroups"] = towns.GroupBy(t => t.LandGroup).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (object)new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["towns"] = g.Count(), ["supply"] = g.Sum(t => TradeArea(r, t.Id).Sum(v => FoodOf(v.VillageType))) }) };
            r.Balance["harbourVillages"] = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["harbour"] = villages.Count(v => v.Harbour), ["ports"] = villages.Count(v => v.Port), ["coastalPrototypes"] = villages.Count(v => v.Port && !v.AdaptedPrototype),
                ["fishingTypes"] = villages.Count(v => List(Obj(policy, "villageTypes"), "fishingTypes").Contains(v.VillageType)),
                ["maxPortCentre"] = villages.Count(v => v.Port) == 0 ? 0 : Math.Round(villages.Where(v => v.Port).Max(v => Dist(v.X, v.Y, v.PortX.Value, v.PortY.Value)), 2) };
        }

        // MMR-42: the picked prototype if its locations meet the mission scene contracts for this settlement, else a deterministic re-pick among the
        // pool's conforming prototypes (none: a refusal naming the first problem).
        private string Conform(SettlementIntent s, List<string> pool)
        {
            if (scenes == null || s.Prototype == null) return s.Prototype;
            var failed = scenes.PrototypeProblems(s.Prototype, s.Kind, s.Port);
            if (failed.Count == 0) return s.Prototype;
            var conforming = pool.Where(p => scenes.PrototypeProblems(p, s.Kind, s.Port).Count == 0).ToList();
            if (conforming.Count == 0) { problems.Add(s.Kind + " " + s.Id + ": no " + s.Culture + " prototype meets the mission scene contracts (" + s.Prototype + ": " + failed[0] + ")"); return s.Prototype; }
            string picked = Pick("prototypes", s.Id + "|missionScenes", conforming);
            s.SceneRepick = s.Prototype + " -> " + picked + ": " + failed[0];
            return picked;
        }

        // A settlement's location scenes as Element() writes them (an inland town drops its prototype's port location).
        public IDictionary<string, string[]> PrototypeLocations(SettlementIntent s)
        {
            XmlElement element;
            var locations = new Dictionary<string, string[]>(MNBL.ProceduralWorld.Missions.MissionScenes.LocationsOf(catalog.TryGetValue(s.Prototype ?? "", out element) ? element : null), StringComparer.Ordinal);
            if (s.Kind == "town" && !s.Port) locations.Remove("port");
            return locations;
        }

        // A culture's installed prototypes of a kind; withPrefab: only those the vanilla map places as a prefab (fixtures need an entity).
        private List<string> Prototypes(string culture, string kind, bool withPrefab = false)
        {
            var c = Obj(Obj(prototypes, "cultures"), culture ?? "");
            return List(c, kind).OfType<IDictionary<string, object>>().Where(p => !withPrefab || Text(p, "mapPrefab") != null)
                .Select(p => Text(p, "id")).Where(id => id != null && catalog.ContainsKey(id)).ToList();
        }

        private string Realm(string kingdom, Dictionary<string, FactionFactory.KingdomIntent> kingdoms)
        {
            FactionFactory.KingdomIntent k;
            return kingdom != null && kingdoms.TryGetValue(kingdom, out k) ? k.Name : "world";
        }

        private static double Population(IDictionary<string, object> town) { return Math.Log(Math.Max(1.0, Double(Obj(town, "population"), "estimate"))); }

        // Rank of value among values in [0, 1] (ties share the mean rank); one value is the median.
        private static double Percentile(double value, IList<double> values)
        {
            if (values.Count <= 1) return 0.5;
            int below = values.Count(v => v < value), equal = values.Count(v => v == value);
            return (below + (equal - 1) / 2.0) / (values.Count - 1);
        }

        private static int Quantile(IList<int> sorted, double p)
        {
            var list = sorted.OrderBy(v => v).ToList();
            int index = (int)Math.Round(Math.Max(0, Math.Min(1, p)) * (list.Count - 1), MidpointRounding.AwayFromZero);
            return list[index];
        }

        // Distance on the periodic world (columns wrap at width; rows do not); not periodic when width is 0 (fixtures).
        private double Dist(double x1, double y1, double x2, double y2)
        {
            double dx = Math.Abs(x1 - x2), dy = y1 - y2;
            if (width > 0) dx = Math.Min(dx, width - dx);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private ulong Stream(string stream, string entity, int draw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.settlement-stream/1\n" + stream + "\n" + streams[stream].ToString(CultureInfo.InvariantCulture) + "\n" +
                                                                               prefix + "\n" + entity + "\n" + draw.ToString(CultureInfo.InvariantCulture)));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        private string Pick(string stream, string entity, List<string> pool)
        {
            if (pool.Count == 0) { problems.Add("no installed prototype for " + entity); return null; }
            return pool[(int)(Stream(stream, entity, 0) % (ulong)pool.Count)];
        }

        private string Weighted(string stream, string entity, IDictionary<string, object> weights)
        {
            var items = weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new KeyValuePair<string, int>(w.Key, Convert.ToInt32(w.Value, CultureInfo.InvariantCulture))).Where(w => w.Value > 0).ToList();
            int total = items.Sum(w => w.Value);
            if (total == 0) { problems.Add("no village type for " + entity); return null; }
            int roll = (int)(Stream(stream, entity, 0) % (ulong)total);
            foreach (var w in items) { if (roll < w.Value) return w.Key; roll -= w.Value; }
            return items.Last().Key;
        }

        private static string Fill(string template, IDictionary<string, string> values) { return template == null ? null : FactionFactory.Fill(template, values); }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.settlement-intents", ["version"] = 2, ["worldPrefix"] = r.WorldPrefix,
                ["settlements"] = r.Settlements.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = s.Id, ["uid"] = s.Uid, ["kind"] = s.Kind, ["name"] = s.Name, ["culture"] = s.Culture, ["owner"] = s.Owner, ["kingdom"] = s.Kingdom, ["prototype"] = s.Prototype,
                    ["adaptedPrototype"] = s.AdaptedPrototype, ["mapPrefab"] = s.MapPrefab, ["bound"] = s.Bound, ["villageType"] = s.VillageType, ["production"] = s.Production, ["class"] = s.CityClass,
                    ["position"] = new[] { s.X, s.Y }, ["gate"] = new[] { s.GateX, s.GateY }, ["port"] = s.PortX.HasValue ? new[] { s.PortX.Value, s.PortY.Value } : null,
                    ["harbour"] = s.Harbour, ["harbourCell"] = s.HarbourCell, ["boundDistance"] = Math.Round(s.BoundDistance, 2), ["remote"] = s.Remote, ["tradeTown"] = s.TradeTown,
                    ["foodRedraw"] = s.FoodRedraw, ["sourceBound"] = s.SourceBound,
                    ["positionStatus"] = s.PositionStatus, ["prosperity"] = s.Prosperity, ["hearth"] = s.Hearth, ["buildings"] = s.Buildings, ["landGroup"] = s.LandGroup,
                    ["provenance"] = s.Provenance }).ToList(),
                ["rebound"] = r.Rebound, ["reboundReason"] = r.ReboundReason,
                ["slots"] = r.Slots.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["owner"] = x.Owner, ["kind"] = x.Kind, ["name"] = x.Name, ["value"] = x.Value }).ToList(),
                ["balance"] = r.Balance
            };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        private static string Number(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }
        private static string Sha256(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static bool Bool(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is bool && (bool)v; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static double Double(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return List(o, key).OfType<IDictionary<string, object>>(); }
    }
}
