using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-45: a compiled world's campaign graph, built from its data bundle as the activation registers it (docs/world-activation.md). Pure
    // managed: the in-game activation (BuildWorld) and the offline tests (tests/Campaign/CompiledWorldTests.cs) run this same builder.
    //   1. The accepted factories in their order: factions (MMR-31), heroes (MMR-32), settlements (MMR-33; mission scene contracts, MMR-42),
    //      then the settlement records take the resolved placement the world's scene entities were built from (MMR-40's layout: the
    //      settlement report the scene stage persisted), then startup (MMR-34) and naval content (MMR-35), which read those positions.
    //   2. Their records merged into the new game's documents (RegistrationPlan.NewGameOrder) with the installed records they need re-emitted
    //      (GeneratedCampaignDataset.CompiledWorldDocuments: the fixture world's closure rule).
    //   3. The whole graph validated (MMR-36, CampaignGraphValidator) in the compiler's logical frame, where the travel cells are; a refused
    //      graph throws with every diagnostic.
    //   4. The registration copies: every position moved into the scene's physical frame (physical = logical + origin; MMR-64), so the
    //      campaign's settlements stand where the scene's entities are. Only positions change.
    internal sealed class CompiledWorldDataset
    {
        public sealed class Sources
        {
            public Func<string, byte[]> Bundle;                  // a verified read of a bundle file (compiled/campaign/campaign.json, ...)
            public Func<string, byte[]> Policy;                  // a repository policy path (graph-validation.json "policies") -> its bytes
            public IDictionary<string, XmlDocument> Catalog;      // the merged installed documents (the pinned capture)
            public string CatalogSha256;                         // what the graph report binds as the catalog (the pin policy, or offline the receipt)
            public IList<string> Defaults;                       // the pinned engine default inventory
            public MNBL.ProceduralWorld.Missions.MissionScenes MissionScenes;
            public IDictionary<string, object> Layout;           // the settlement layout report (mnbl.settlement-layout v2; physical positions)
            public double OriginX, OriginY;                      // the periodic frame's origin: physical = logical + origin
            public double Width;                                 // the period in x (logical x is canonical in [0, Width))
        }

        public FactionFactory.Result Factions;
        public HeroFactory.Result Heroes;
        public SettlementFactory.Result World;
        public GeneratedCampaignInitializer.Result Start;
        public NavalContentFactory.Result Naval;
        public NavalContentFactory.NavalPlan NavalPlan;
        public PopulationPlan Population;   // MMR-281: what the runtime keeps of the population pass
        public List<string> WandererTemplates = new List<string>();   // MMR-262: the backstory-safe wanderer templates the new game registers
        public CampaignGraphValidator.Report Graph;
        public string WorldPrefix, WorldId, LayoutSha256;
        public List<string> Declared = new List<string>();
        public List<string> Defaults;
        public List<string> CreationCultures = new List<string>();
        public List<KeyValuePair<string, XmlDocument>> LogicalDocuments;   // what the graph validated
        public List<KeyValuePair<string, XmlDocument>> Documents;          // what registers: the same records in the physical frame
        public List<GeneratedCampaignDataset.Provenance> Templates;
        public List<string> OptionalAbsent;
        public List<string> PositionMismatches = new List<string>();   // the registration documents against the layout, in the physical frame
        public List<string> FamilyProblems = new List<string>();
        public SortedDictionary<string, string> DocumentSha256 = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public int PositionsMoved;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        public static CompiledWorldDataset Build(Sources s)
        {
            if (s == null || s.Bundle == null || s.Policy == null || s.Catalog == null || s.Defaults == null || s.Layout == null) throw new ArgumentNullException("s");
            Func<string, IDictionary<string, object>> bundle = path => Parse(s.Bundle(path));
            Func<string, IDictionary<string, object>> policy = path => Parse(s.Policy(path));
            var campaign = bundle("compiled/campaign/campaign.json"); var idMap = bundle("compiled/campaign/id-map.json");
            var travel = bundle("compiled/travel/travel-graph.json"); var ports = bundle("compiled/travel/ports.json");
            var cells = s.Bundle("compiled/travel/cells.bin");
            var graphPolicy = policy(CampaignGraphValidator.PolicyPath);
            var rosters = policy("worldgen/policies/rosters.json"); var economy = policy("worldgen/policies/economy.json");
            var startup = policy("worldgen/policies/startup.json"); var naval = policy("worldgen/policies/naval-content.json");
            var prototypes = policy("worldgen/templates/settlements/prototypes.json");
            var templates = Strings(graphPolicy, "policies").Where(p => p.StartsWith("worldgen/templates/cultures/", StringComparison.Ordinal) && !p.EndsWith("/contract.json", StringComparison.Ordinal))
                .ToDictionary(p => p.Substring(p.LastIndexOf('/') + 1, p.Length - p.LastIndexOf('/') - 1 - ".json".Length), p => policy(p), StringComparer.Ordinal);
            foreach (var id in new[] { "Settlements", "Factions", "SPCultures", "ShipHulls", "MissionShips", "ShipSlots", "partyTemplates" })
                if (!s.Catalog.ContainsKey(id)) throw new InvalidOperationException("The installed catalog has no " + id + " document.");

            var d = new CompiledWorldDataset();
            var resolver = new CharacterTemplateResolver(s.Catalog, s.Defaults, (IDictionary<string, object>)rosters["troops"]);
            d.Factions = FactionFactory.Build(campaign, idMap, policy("worldgen/policies/factions.json"), policy("worldgen/policies/culture-presentations.json"), templates, policy(WandererPool.PolicyPath));
            d.Heroes = HeroFactory.Build(campaign, idMap, d.Factions, rosters, resolver, policy(WandererPool.PolicyPath));
            d.Population = PopulationPlan.From(campaign, d.Factions, policy(WandererPool.PolicyPath));
            var factory = new SettlementFactory(economy, prototypes, s.Catalog["Settlements"], resolver.Exists, s.MissionScenes);
            // MMR-258: each record takes the prototype its scene look was built from, and a village's type must be the one its production
            // pieces show.
            var looks = Looks(s.Layout);
            factory.LookPrototypes = looks.ToDictionary(kv => kv.Key, kv => kv.Value.Key, StringComparer.Ordinal);
            d.World = factory.Build(campaign, travel, ports, d.Factions, d.Heroes, NavalContentFactory.ShipyardLevels(naval, ports));
            var lookMismatches = d.World.Settlements.Where(x => x.Kind == "village" && looks.ContainsKey(x.Id) && looks[x.Id].Value != x.VillageType)
                .Select(x => x.Id + " is " + x.VillageType + ", its scene look shows " + looks[x.Id].Value).ToList();
            if (lookMismatches.Count > 0) throw new InvalidOperationException("The settlement layout's village looks are not the records' village types: " + String.Join("; ", lookMismatches.Take(5)));
            d.WorldPrefix = d.World.WorldPrefix; d.WorldId = d.WorldPrefix.TrimEnd('_');

            // MMR-40: the records take the placement the scene's entities were built from, before startup and naval content read them.
            d.LayoutSha256 = Text(s.Layout, "layoutSha256");
            if (!(s.Width > 0)) throw new InvalidOperationException("The periodic frame has no width.");
            var placed = Layout(s.Layout, s.OriginX, s.OriginY, s.Width);
            var missing = SettlementFactory.ApplyResolvedPositions(d.World.Settlements, id => { SettlementFactory.ResolvedPositions r; return placed.TryGetValue(id, out r) ? r : null; });
            if (missing.Count > 0) throw new InvalidOperationException("The settlement layout places no position for " + missing.Count + " settlements (first " + missing[0] + ").");
            SettlementFactory.RewritePositions(d.World.Documents.Single(x => x.Key == "Settlements").Value, d.World.Settlements);

            d.Start = new GeneratedCampaignInitializer(startup, factory, s.Catalog["Settlements"], s.Catalog["Factions"], s.Catalog["SPCultures"]).Build(travel, ports, cells, d.Factions, d.Heroes, d.World,
                GeneratedCampaignInitializer.CellBiomes(s.Bundle("compiled/terrain/masks.bin"), Utf8(s.Bundle("compiled/terrain/projection.json")), Utf8(s.Bundle("policies/terrain-materials.json"))));
            d.Naval = new NavalContentFactory(naval, s.Catalog["Factions"], s.Catalog["SPCultures"], s.Catalog["ShipHulls"], s.Catalog["MissionShips"], s.Catalog["ShipSlots"], s.Catalog["partyTemplates"])
                .Build(campaign, travel, ports, cells, d.Factions, d.World, d.Start);
            d.NavalPlan = d.Naval.Plan;   // the fleet rule and the pirate spawn points (logical: the naval scene wrapper places them)
            d.Declared = d.Factions.DeclaredKeys.Concat(d.Heroes.DeclaredKeys).Concat(d.World.DeclaredKeys).Concat(d.Start.DeclaredKeys).Concat(d.Naval.DeclaredKeys).Distinct().ToList();

            // The new game's documents: the factories' records merged per document, with the installed records they need.
            var parts = d.Factions.Documents.Concat(d.Heroes.Documents).Concat(d.World.Documents).Concat(d.Start.Documents).Concat(d.Naval.Documents).ToList();
            var merged = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
            foreach (var root in new[] { "SPCultures", "NPCCharacters", "Heroes", "Kingdoms", "Factions", "Settlements" })
            {
                var x = new XmlDocument { XmlResolver = null }; x.AppendChild(x.CreateElement(root));
                foreach (var part in parts.Where(p => p.Key == root))
                    foreach (XmlNode n in part.Value.DocumentElement.ChildNodes) x.DocumentElement.AppendChild(x.ImportNode(n, true));
                merged[root] = x;
            }
            var unknown = parts.Select(p => p.Key).Where(k => !merged.ContainsKey(k)).Distinct().ToList();
            if (unknown.Count > 0) throw new InvalidOperationException("A factory emitted a document the new game does not load: " + String.Join(", ", unknown));
            d.Declared.AddRange(Player(merged, s.Catalog, d.Start));
            // A spouse must name its hero back (or name none): Hero.Deserialize reads spouse only while the hero has none (installed IL), so a
            // later hero's other spouse would be silently ignored.
            var heroes = merged["Heroes"].DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("id")).ToDictionary(e => e.GetAttribute("id"), StringComparer.Ordinal);
            foreach (var h in heroes.Values.Where(e => e.HasAttribute("spouse")))
            {
                string id = h.GetAttribute("id"), spouse = h.GetAttribute("spouse").Replace("Hero.", "");
                XmlElement other;
                if (!heroes.TryGetValue(spouse, out other)) d.FamilyProblems.Add(id + " spouse " + spouse + " has no record");
                else if (other.HasAttribute("spouse") && other.GetAttribute("spouse").Replace("Hero.", "") != id) d.FamilyProblems.Add(id + " spouse " + spouse + " names " + other.GetAttribute("spouse"));
            }
            if (d.FamilyProblems.Count > 0) throw new InvalidOperationException("The heroes' spouses do not name each other: " + String.Join("; ", d.FamilyProblems.Take(5)));
            d.Defaults = s.Defaults.ToList();
            // The startup plan's templates, and the whole backstory-safe wanderer pool this catalog resolves (MMR-262 F05): a template registers
            // only when something roots it, and the runtime top-up and rotation pick only registered ones (GeneratedPopulationBehavior's
            // TemplateUsable), so rooting the day-0 wanderers' templates alone left them repeating those. The pool is filtered by HeroFactory's
            // own predicate (CharacterTemplateResolver.WandererTemplates: an is_template Wanderer whose references resolve).
            var resolvable = new HashSet<string>(WandererPool.Cultures.Concat(d.Population.Pool.Safe.Select(t => t.Culture)).Distinct(StringComparer.Ordinal)
                .SelectMany(c => resolver.WandererTemplates(c, new List<string>())), StringComparer.Ordinal);
            d.WandererTemplates = d.Population.Pool.Safe.Select(t => t.Id).Where(resolvable.Contains).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var planTemplates = d.Start.Plan.Notables.Concat(d.Start.Plan.Companions).Select(h => h.Template).Concat(d.WandererTemplates);
            List<GeneratedCampaignDataset.Provenance> reemitted; List<string> optionalAbsent;
            d.LogicalDocuments = GeneratedCampaignDataset.CompiledWorldDocuments(s.Catalog, s.Defaults, merged, d.Declared, planTemplates, out reemitted, out optionalAbsent);
            d.Templates = reemitted; d.OptionalAbsent = optionalAbsent;
            d.CreationCultures = merged["SPCultures"].DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.HasAttribute("start_point_position_x")).Select(e => e.GetAttribute("id")).OrderBy(x => x, StringComparer.Ordinal).ToList();

            // MMR-36: the whole staged graph, before anything registers.
            var policies = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Strings(graphPolicy, "policies")) policies[p] = Sha256(s.Policy(p));
            policies[CampaignGraphValidator.PolicyPath] = Sha256(s.Policy(CampaignGraphValidator.PolicyPath));
            policies[CampaignGraphValidator.CatalogReceiptKey] = s.CatalogSha256;
            policies[CampaignGraphValidator.LiveDefaultsKey] = CampaignGraphValidator.InventorySha256(s.Defaults);
            var input = new CampaignGraphValidator.Input { WorldPrefix = d.WorldPrefix, Campaign = campaign, IdMap = idMap, Plan = d.Start.Plan, MissionScenes = s.MissionScenes };
            foreach (var doc in d.LogicalDocuments) input.Documents.Add(new KeyValuePair<string, XmlDocument>(doc.Key, (XmlDocument)doc.Value.CloneNode(true)));
            input.Generated.UnionWith(d.Declared); input.Aliases.UnionWith(CampaignIdentity.Aliases);
            foreach (var c in s.Catalog) input.Catalog[c.Key] = c.Value;
            input.Defaults.UnionWith(s.Defaults);
            input.Resolutions.AddRange(CampaignGraphValidator.Resolutions(d.Factions, d.Heroes, d.World, d.Naval));
            input.Dispositions.AddRange(CampaignGraphValidator.Dispositions(d.Factions, d.Heroes));
            foreach (var p in policies) input.Policies[p.Key] = p.Value;
            input.SpawnPoints.AddRange(d.Start.Anchors.Select(a => new CampaignGraphValidator.SpawnPoint { Label = a.Kind + " anchor " + a.Subject, X = a.X, Y = a.Y })
                .Concat(d.Start.Starts.Select(x => new CampaignGraphValidator.SpawnPoint { Label = "start of " + x.Culture, X = x.X, Y = x.Y }))
                .Concat(d.Naval.Pirates.SelectMany(p => p.SpawnPoints).Select(p => new CampaignGraphValidator.SpawnPoint { Label = "pirate zone " + p.Uid, X = p.X, Y = p.Y })));
            foreach (var r in d.World.Rebound) input.Rebound[r.Key] = r.Value;
            foreach (var r in d.World.ReboundReason) input.ReboundReason[r.Key] = r.Value;
            d.Graph = CampaignGraphValidator.Validate(graphPolicy, input);

            // The registration copies in the physical frame.
            d.Documents = d.LogicalDocuments.Select(x => new KeyValuePair<string, XmlDocument>(x.Key, (XmlDocument)x.Value.CloneNode(true))).ToList();
            d.PositionsMoved = ToPhysical(d.Documents, s.OriginX, s.OriginY);
            d.PositionMismatches = PhysicalMismatches(d.Documents.Single(x => x.Key == "Settlements").Value, s.Layout);
            foreach (var doc in d.Documents) d.DocumentSha256[doc.Key] = Sha256(new UTF8Encoding(false).GetBytes(doc.Value.OuterXml));
            return d;
        }

        // The player's records, world aliases installed code finds by literal id (CampaignIdentity.Aliases): the installed main_hero character
        // and hero, and the installed player_faction homed at the world's start town of the player character's culture (else the first
        // creation culture's). Character creation then sets the player's culture, name and start as in any new game. Returns the declared keys.
        private static IEnumerable<string> Player(IDictionary<string, XmlDocument> merged, IDictionary<string, XmlDocument> catalog, GeneratedCampaignInitializer.Result start)
        {
            Func<string, string, string, XmlElement> installed = (document, element, id) =>
            {
                var e = catalog[document].DocumentElement.SelectSingleNode(element + "[@id='" + id + "']") as XmlElement;
                if (e == null) throw new InvalidOperationException("The installed catalog has no " + element + " " + id + ".");
                return e;
            };
            var character = installed("NPCCharacters", "NPCCharacter", "main_hero");
            var hero = installed("Heroes", "Hero", "main_hero");
            var clan = installed("Factions", "Faction", "player_faction");
            string culture = character.GetAttribute("culture").Replace("Culture.", "");
            var home = start.Starts.Where(x => x.Culture == culture).Concat(start.Starts.OrderBy(x => x.Culture, StringComparer.Ordinal)).FirstOrDefault();
            if (home == null || home.Town == null) throw new InvalidOperationException("The world has no start town for the player.");
            merged["NPCCharacters"].DocumentElement.AppendChild(merged["NPCCharacters"].ImportNode(character, true));
            merged["Heroes"].DocumentElement.AppendChild(merged["Heroes"].ImportNode(hero, true));
            var faction = (XmlElement)merged["Factions"].ImportNode(clan, true);
            faction.SetAttribute("initial_home_settlement", "Settlement." + home.Town);
            merged["Factions"].DocumentElement.AppendChild(faction);
            return new[] { "NPCCharacter.main_hero", "Hero.main_hero", "Faction.player_faction", "MobileParty.player_party" };
        }

        // The layout report's positions (physical, as the scene's entities stand) in the records' logical frame.
        // x is wrapped into the canonical logical range [0, width), so a gate or port across the seam stands in its canonical travel cell.
        public static Dictionary<string, SettlementFactory.ResolvedPositions> Layout(IDictionary<string, object> report, double originX, double originY, double width)
        {
            if (Text(report, "schema") != "mnbl.settlement-layout" || Convert.ToInt32(report["version"], CultureInfo.InvariantCulture) != 2)
                throw new InvalidOperationException("Not a version 2 settlement layout report.");
            if (!Equals(report["activatable"], true)) throw new InvalidOperationException("The settlement layout is not activatable.");
            Func<IDictionary<string, object>, string, double[]> logical = (row, key) =>
            {
                object v; if (!row.TryGetValue(key, out v) || v == null) return null;
                var p = ((IEnumerable)v).Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                double x0 = (p[0] - originX) % width; if (x0 < 0) x0 += width; if (x0 >= width) x0 = 0;
                return new[] { x0, p[1] - originY };
            };
            var result = new Dictionary<string, SettlementFactory.ResolvedPositions>(StringComparer.Ordinal);
            foreach (var row in ((IEnumerable)report["settlements"]).Cast<IDictionary<string, object>>())
                result[Text(row, "id")] = new SettlementFactory.ResolvedPositions { Centre = logical(row, "centre"), Gate = logical(row, "gate"), Port = logical(row, "port"), Dropoff = logical(row, "dropoff"),
                    Status = "resolved (layout " + Text(report, "layoutSha256").Substring(0, 12) + ")" };
            return result;
        }

        // MMR-258: the layout report's looks: settlement id -> (the record's prototype, null for phase 0 or a variant; the village type its
        // production pieces show). A report without looks (no selection ran) gives none.
        public static Dictionary<string, KeyValuePair<string, string>> Looks(IDictionary<string, object> report)
        {
            var result = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            foreach (var row in ((IEnumerable)report["settlements"]).Cast<IDictionary<string, object>>())
            {
                object v; var look = row.TryGetValue("look", out v) ? v as IDictionary<string, object> : null;
                if (look == null) continue;
                result[Text(row, "id")] = new KeyValuePair<string, string>(Text(look, "prototype"), Text(look, "villageType"));
            }
            return result;
        }

        // The registration Settlements document against the layout report (centre, a fortification's gate, a town's port), physical, within 1e-6.
        public static List<string> PhysicalMismatches(XmlDocument settlements, IDictionary<string, object> report)
        {
            var problems = new List<string>();
            foreach (var row in ((IEnumerable)report["settlements"]).Cast<IDictionary<string, object>>())
            {
                string id = Text(row, "id");
                var e = settlements.DocumentElement.SelectSingleNode("Settlement[@id='" + id + "']") as XmlElement;
                if (e == null) { problems.Add(id + " has no record"); continue; }
                foreach (var pair in new[] { new[] { "centre", "posX", "posY" }, new[] { "gate", "gate_posX", "gate_posY" }, new[] { "port", "port_posX", "port_posY" } })
                {
                    object v; if (!row.TryGetValue(pair[0], out v) || v == null) continue;
                    if (pair[0] == "gate" && !e.HasAttribute("gate_posX")) continue;   // a village's gate is its centre
                    var p = ((IEnumerable)v).Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                    if (!e.HasAttribute(pair[1])) { problems.Add(id + " " + pair[0] + " missing"); continue; }
                    if (Math.Abs(Number(e.GetAttribute(pair[1])) - p[0]) > 1e-6 || Math.Abs(Number(e.GetAttribute(pair[2])) - p[1]) > 1e-6) problems.Add(id + " " + pair[0] + " off its placement");
                }
            }
            return problems;
        }

        // Moves every position the documents hold (a settlement's centre, gate and port; a culture's start point) by the frame's origin.
        public static int ToPhysical(IEnumerable<KeyValuePair<string, XmlDocument>> documents, double originX, double originY)
        {
            int moved = 0;
            var pairs = new[] { new[] { "posX", "posY" }, new[] { "gate_posX", "gate_posY" }, new[] { "port_posX", "port_posY" }, new[] { "start_point_position_x", "start_point_position_y" } };
            foreach (var document in documents)
                foreach (var e in document.Value.DocumentElement.ChildNodes.OfType<XmlElement>())
                    foreach (var pair in pairs)
                    {
                        if (!e.HasAttribute(pair[0]) && !e.HasAttribute(pair[1])) continue;
                        if (!e.HasAttribute(pair[0]) || !e.HasAttribute(pair[1])) throw new InvalidOperationException(e.Name + " " + e.GetAttribute("id") + " has half a position (" + pair[0] + ", " + pair[1] + ").");
                        e.SetAttribute(pair[0], (Number(e.GetAttribute(pair[0])) + originX).ToString("R", CultureInfo.InvariantCulture));
                        e.SetAttribute(pair[1], (Number(e.GetAttribute(pair[1])) + originY).ToString("R", CultureInfo.InvariantCulture));
                        moved++;
                    }
            return moved;
        }

        // The report a probe or receipt keeps.
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> {
                ["worldId"] = WorldId, ["worldPrefix"] = WorldPrefix, ["layoutSha256"] = LayoutSha256, ["declared"] = Declared.Count,
                ["settlements"] = World.Settlements.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => (object)g.Count()), ["hideouts"] = Start.Hideouts.Count,
                ["heroes"] = Heroes.XmlHeroes.Count(), ["clans"] = Factions.Clans.Count, ["minorClans"] = Factions.MinorClans.Count, ["kingdoms"] = Factions.Kingdoms.Count, ["creationCultures"] = CreationCultures,
                ["reemittedTemplates"] = Templates.Count, ["wandererTemplates"] = WandererTemplates.Count, ["optionalAbsent"] = OptionalAbsent.Count, ["positionsMoved"] = PositionsMoved, ["positionMismatches"] = PositionMismatches, ["familyProblems"] = FamilyProblems,
                ["documents"] = DocumentSha256, ["landlocked"] = NavalPlan.Landlocked.Count, ["pirateSpawnPoints"] = NavalPlan.PirateSpawnPoints.Count,
                ["graph"] = Graph.Record(25) };
        }

        private static double Number(string text) { return Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static string Utf8(byte[] bytes) { return new UTF8Encoding(false).GetString(bytes); }
        private static IDictionary<string, object> Parse(byte[] bytes) { return (IDictionary<string, object>)Json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿')); }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v as string : null; }
        private static IEnumerable<string> Strings(IDictionary<string, object> o, string key) { return ((IEnumerable)o[key]).Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)); }
        public static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
