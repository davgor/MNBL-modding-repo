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
    // MMR-34: what a generated world must supply before the installed campaign behaviours start on it (docs/campaign-behavior-
    // compatibility.md). Pure managed. From the MMR-27 records, the MMR-28 travel graph, cells and ports, the MMR-31 factions, the MMR-32
    // heroes, the MMR-33 settlements and the startup policy (worldgen/policies/startup.json) it builds:
    //   * a start per creation culture (every culture character creation can offer, startup.json starts.creationCultures), re-emitted
    //     as CultureObject.StartingPoint so no installed Calradia start point survives: a port town with food, notables and a sea
    //     destination in the same open basin, else the fallback rule;
    //   * hideouts across the world (the installed BanditSpawnCampaignBehavior spawns bandits only around hideouts), each with a bandit
    //     culture weighted by its nearest town's culture and by its cell's ground (biome, coast; MMR-287, RT-28);
    //   * the installed land bandit clans, re-created with their installed ids as world aliases (installed code finds them by id) and a
    //     generated hideout as home;
    //   * the retirement retreat, re-created with its installed ids as a world alias (RetirementCampaignBehavior finds it by id and
    //     dereferences it every hour);
    //   * the spawn anchors the installed behaviours spawn parties at, each checked to be passable land connected to a town
    //     (GeneratedSpawnModel);
    //   * the runtime plan: the MMR-32 notable and companion intents and the MMR-31 ruler relations, for GeneratedStartupBehavior.
    // Construction randomness comes from named, versioned streams; the campaign keeps MBRandom for mutable gameplay. A world that cannot
    // supply the startup is refused with StartupException, whose quality codes (startup.json quality) say whether another seed may pass.
    // The record builders (bandit clan, retreat, start culture, the bandit home rule) are shared with the in-game fixture world
    // (GeneratedCampaignDataset), so the fixture registers the records this class emits.
    internal sealed class GeneratedCampaignInitializer
    {
        public sealed class StartIntent
        {
            public string Culture, Town, Basin, SeaDestination, ShipSource, Embarkation, Reason;
            public double X, Y, PortX, PortY, DestinationPortX, DestinationPortY;
            public int Food, Notables;
            public bool Fallback;
        }

        public sealed class HeroPlan { public string Id, Uid, Template, Settlement, Name, Occupation; public int Age; }
        // MMR-287: what a hideout's bandit culture was drawn from: its cell's natural biome, whether the cell is on the coast, its nearest town's
        // culture and the mixed weights (normalized, summing to 1).
        public sealed class HideoutGround { public string Biome, TownCulture; public bool Coast; public SortedDictionary<string, double> Weights; }
        public sealed class RelationPlan { public string A, B; public int Value; }

        // What GeneratedStartupBehavior instantiates at new game.
        public sealed class StartupPlan
        {
            public List<HeroPlan> Notables = new List<HeroPlan>(), Companions = new List<HeroPlan>();
            public List<RelationPlan> Relations = new List<RelationPlan>();
        }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<StartIntent> Starts = new List<StartIntent>();
            public List<SettlementFactory.SettlementIntent> Hideouts = new List<SettlementFactory.SettlementIntent>();
            public SortedDictionary<string, string> HideoutTowns = new SortedDictionary<string, string>(StringComparer.Ordinal);   // hideout -> nearest town
            public SortedDictionary<string, string> BanditHomes = new SortedDictionary<string, string>(StringComparer.Ordinal);    // clan -> hideout
            public SortedDictionary<string, HideoutGround> HideoutGrounds = new SortedDictionary<string, HideoutGround>(StringComparer.Ordinal);   // hideout -> its draw (MMR-287)
            public double RetreatX, RetreatY;
            public string RetreatCulture;
            public List<GeneratedSpawnModel.Anchor> Anchors = new List<GeneratedSpawnModel.Anchor>();
            public StartupPlan Plan = new StartupPlan();
            public List<string> Notes = new List<string>();
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public string IntentsJson, IntentsSha256;
        }

        // A bounded quality rejection: the problems and their quality codes (startup.json quality.codes; retryable codes may pass on another seed).
        public sealed class StartupException : InvalidOperationException
        {
            public StartupException(IList<string> problems, IList<string> codes)
                : base("Startup refused [" + String.Join(", ", codes) + "]: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; Codes = codes; }
            public IList<string> Problems { get; private set; }
            public IList<string> Codes { get; private set; }
            public bool Retryable { get; internal set; }
        }

        public const string CodePolicy = "quality.startup_policy", CodeCatalog = "quality.startup_catalog", CodeMap = "quality.startup_map",
                            CodeHideouts = "quality.startup_hideouts", CodeRetreat = "quality.startup_retreat", CodeNoStart = "quality.startup_no_start",
                            CodeSpawn = "quality.startup_unplaceable_spawn", CodePlan = "quality.startup_plan";

        private readonly IDictionary<string, object> policy;
        private readonly XmlDocument settlementsCatalog, factionsCatalog, culturesCatalog;
        private readonly SettlementFactory settlements;
        private readonly Dictionary<string, int> streams = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> problems = new List<string>();
        private readonly SortedSet<string> codes = new SortedSet<string>(StringComparer.Ordinal);
        private string prefix;

        private void Problem(string code, string text)
        {
            var known = Obj(Obj(policy, "quality"), "codes");
            if (known == null || !known.ContainsKey(code)) { codes.Add(CodePolicy); problems.Add("startup.json has no quality code " + code); }
            codes.Add(code); problems.Add(text);
        }

        private void ThrowIfRefused()
        {
            if (problems.Count == 0) return;
            var known = Obj(Obj(policy, "quality"), "codes");
            bool retryable = codes.All(c => { var entry = Obj(known, c); object v; return entry != null && entry.TryGetValue("retryable", out v) && v is bool && (bool)v; });
            throw new StartupException(problems.Distinct().ToList(), codes.ToList()) { Retryable = retryable };
        }

        // startup: worldgen/policies/startup.json. settlements: the factory that built the world's settlements (its hideout builder and
        // food supply). The installed Settlements, Factions and SPCultures documents supply the retreat, bandit clan and culture records.
        public GeneratedCampaignInitializer(IDictionary<string, object> startup, SettlementFactory settlements, XmlDocument settlementsCatalog,
                                            XmlDocument factionsCatalog, XmlDocument culturesCatalog)
        {
            policy = startup; this.settlements = settlements;
            this.settlementsCatalog = settlementsCatalog; this.factionsCatalog = factionsCatalog; this.culturesCatalog = culturesCatalog;
            foreach (var name in new[] { "hideouts", "starts" }) streams[name] = Int(Obj(policy, "streams"), name);
        }

        // biomeAt: the natural biome (terrain-materials.json core name) at a logical campaign position, or null (CellBiomes reads it from the bundle).
        public Result Build(IDictionary<string, object> travel, IDictionary<string, object> ports, byte[] cellsBin, FactionFactory.Result factions,
                            HeroFactory.Result heroes, SettlementFactory.Result world, Func<double, double, string> biomeAt)
        {
            if (biomeAt == null) throw new ArgumentNullException("biomeAt");
            var result = new Result { WorldPrefix = world.WorldPrefix };
            prefix = world.WorldPrefix;
            if (Text(policy, "schema") != "mnbl.startup" || Int(policy, "version") != 1) Problem(CodePolicy, "not mnbl.startup version 1");
            var map = new GeneratedSpawnModel.CellMap(Obj(travel, "grid"), cellsBin, List(Obj(policy, "passable"), "classes").Select(x => (string)x), Records(travel, "settlements"));
            foreach (var conflict in map.Conflicts) Problem(CodeMap, conflict);
            var towns = world.Settlements.Where(s => s.Kind == "town").OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            var fixed_ = world.Settlements.Where(s => s.Kind != "hideout").ToList();
            if (towns.Count < 2) Problem(CodeMap, "fewer than two towns");
            double spacing = towns.Count < 2 ? 0 : towns.Average(t => towns.Where(u => u != t).Min(u => map.Distance(t.X, t.Y, u.X, u.Y)));
            var townGroups = new HashSet<int>(towns.Select(t => map.Group(t.GateX, t.GateY)).Where(g => g >= 0));

            Hideouts(result, map, towns, fixed_, spacing, biomeAt);
            BanditClans(result);
            Retreat(result, map, towns, fixed_);
            Starts(result, travel, ports, map, towns, heroes, world);
            result.Anchors = GeneratedSpawnModel.Anchors(map, result, world, factions, townGroups);
            foreach (var a in result.Anchors.Where(a => a.Problem != null)) Problem(CodeSpawn, a.Problem);
            foreach (var h in heroes.RuntimeHeroes.OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                var plan = new HeroPlan { Id = h.Id, Uid = h.Uid, Template = h.Template, Settlement = h.Settlement, Name = h.Name, Occupation = h.Occupation, Age = h.Age };
                if (h.Template == null || h.Settlement == null || !world.Settlements.Any(s => s.Id == h.Settlement)) Problem(CodePlan, "runtime hero " + h.Id + " has no template or generated home settlement");
                if (h.Occupation == "Wanderer") result.Plan.Companions.Add(plan); else result.Plan.Notables.Add(plan);
            }
            foreach (var r in heroes.Relations.OrderBy(r => r.A, StringComparer.Ordinal).ThenBy(r => r.B, StringComparer.Ordinal))
                result.Plan.Relations.Add(new RelationPlan { A = r.A, B = r.B, Value = r.Value });
            ThrowIfRefused();
            Documents(result);
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        // Hideouts: the installed density per town, shared over the land groups with towns; passable cells away from settlements and
        // other hideouts in stream order; the bandit culture drawn from GroundWeights: the installed weights of the nearest town's culture
        // mixed with the cell's biome and coast weights (hideouts.biome, MMR-287).
        private void Hideouts(Result result, GeneratedSpawnModel.CellMap map, List<SettlementFactory.SettlementIntent> towns,
                              List<SettlementFactory.SettlementIntent> fixed_, double spacing, Func<double, double, string> biomeAt)
        {
            var p = Obj(policy, "hideouts");
            int total = (int)Math.Round(Double(p, "perTown") * towns.Count, MidpointRounding.AwayFromZero);
            double away = Double(p, "settlementDistance") * spacing, apart = Double(p, "hideoutSpacing") * spacing;
            var byComponent = towns.GroupBy(t => map.Group(t.GateX, t.GateY)).Where(g => g.Key >= 0).OrderBy(g => g.Key).ToList();
            var share = new Dictionary<int, int>();
            var remainders = new List<KeyValuePair<int, double>>();
            foreach (var g in byComponent)
            {
                double exact = total * g.Count() / (double)towns.Count;
                share[g.Key] = (int)Math.Floor(exact); remainders.Add(new KeyValuePair<int, double>(g.Key, exact - Math.Floor(exact)));
            }
            int left = total - share.Values.Sum();
            foreach (var r in remainders.OrderByDescending(r => r.Value).ThenBy(r => r.Key).Take(Math.Max(0, left))) share[r.Key]++;
            foreach (var key in share.Keys.ToList()) if (share[key] == 0) share[key] = 1;
            var placed = new List<KeyValuePair<double, double>>();
            var weights = Obj(p, "cultureWeights");
            var pooled = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var culture in weights) foreach (var w in (IDictionary<string, object>)culture.Value) pooled[w.Key] = Convert.ToInt32(w.Value, CultureInfo.InvariantCulture) + (pooled.ContainsKey(w.Key) ? (int)pooled[w.Key] : 0);
            foreach (var g in byComponent)
            {
                var candidates = map.Cells(g.Key).Where(c => fixed_.All(s => map.Distance(c.X, c.Y, s.X, s.Y) >= away))
                    .OrderBy(c => Stream("hideouts", "cell:" + c.Row + ":" + c.Col, 0)).ThenBy(c => c.Row).ThenBy(c => c.Col).ToList();
                int made = 0;
                foreach (var c in candidates)
                {
                    if (made >= share[g.Key]) break;
                    if (placed.Any(q => map.Distance(c.X, c.Y, q.Key, q.Value) < apart)) continue;
                    var town = g.OrderBy(t => map.Distance(c.X, c.Y, t.X, t.Y)).ThenBy(t => t.Id, StringComparer.Ordinal).First();
                    string uid = "hideout:cell:" + c.Row.ToString(CultureInfo.InvariantCulture) + ":" + c.Col.ToString(CultureInfo.InvariantCulture);
                    string id = prefix + "o_" + Sha256("mnbl.stable-id/1\nhideout\n" + uid).Substring(0, 10);
                    var ground = new HideoutGround { Biome = biomeAt(c.X, c.Y), TownCulture = town.Culture, Coast = map.Near(c.Row, c.Col, (int)Double(Obj(p, "biome"), "coastCells"), "sea") };
                    ground.Weights = GroundWeights(Obj(weights, town.Culture ?? "") ?? pooled, Obj(p, "biome"), ground.Biome, ground.Coast);
                    string bandit = WeightedReal("hideouts", "culture:" + uid, ground.Weights);
                    var h = settlements.Hideout(id, "Hideout", bandit, c.X, c.Y);
                    h.Uid = uid; h.LandGroup = g.Key;
                    h.Provenance = "MMR-34 hideout: cell (" + c.Row + ", " + c.Col + "), nearest town " + town.Id + " (" + town.Culture + "), ground " + (ground.Biome ?? "unknown") + (ground.Coast ? " on the coast" : "");
                    result.Hideouts.Add(h); result.HideoutTowns[id] = town.Id; result.HideoutGrounds[id] = ground;
                    placed.Add(new KeyValuePair<double, double>(c.X, c.Y)); made++;
                }
                if (made < share[g.Key]) result.Notes.Add("land group " + g.Key + " (" + g.Count() + " towns) holds " + made + " of its " + share[g.Key] + " hideouts: no " + (made == 0 ? "" : "other ") +
                                                          "passable cell is far enough from settlements" + (made == 0 ? "" : " and hideouts"));
            }
            if (result.Hideouts.Count == 0) Problem(CodeHideouts, "no hideout could be placed");
            // Every bandit culture BanditSpawnCampaignBehavior spawns for needs a hideout (it reads its hideouts-by-culture map unchecked).
            foreach (var missing in List(p, "requiredCultures").Cast<string>().Where(c => !result.Hideouts.Any(h => h.Culture == c)).ToList())
            {
                var donor = result.Hideouts.Where(h => result.Hideouts.Count(o => o.Culture == h.Culture) > 1)
                    .OrderByDescending(h => { double v; return result.HideoutGrounds[h.Id].Weights.TryGetValue(missing, out v) ? v : 0; })
                    .ThenBy(h => Stream("hideouts", "cover:" + missing + ":" + h.Uid, 0)).FirstOrDefault();
                if (donor == null) { Problem(CodeHideouts, "no hideout can take the required bandit culture " + missing + ": fewer hideouts than required cultures"); continue; }
                var replaced = settlements.Hideout(donor.Id, "Hideout", missing, donor.X, donor.Y);
                replaced.Uid = donor.Uid; replaced.LandGroup = donor.LandGroup;
                replaced.Provenance = donor.Provenance + "; bandit culture " + donor.Culture + " replaced by " + missing + " so every bandit culture has a hideout";
                result.Hideouts[result.Hideouts.IndexOf(donor)] = replaced;
                result.Notes.Add("hideout " + donor.Id + " takes " + missing + " (was " + donor.Culture + ") so every bandit culture has a hideout");
            }
        }

        private void BanditClans(Result result)
        {
            foreach (var clan in List(Obj(policy, "banditClans"), "clans").Cast<string>())
            {
                var record = Record(factionsCatalog, clan);
                if (record == null) { Problem(CodeCatalog, "bandit clan " + clan + " is not installed"); continue; }
                var home = BanditHome(record.GetAttribute("culture").Split('.').Last(), result.Hideouts);
                if (home != null) result.BanditHomes[clan] = home;
            }
        }

        // The bandit home rule: the first hideout of the clan's culture, else the first hideout.
        internal static string BanditHome(string clanCulture, IList<SettlementFactory.SettlementIntent> hideouts)
        {
            var home = hideouts.FirstOrDefault(h => h.Culture == clanCulture) ?? hideouts.FirstOrDefault();
            return home == null ? null : home.Id;
        }

        // An installed bandit clan re-emitted with its installed id (a world alias) and a generated hideout as its home.
        internal static XmlElement BanditClanRecord(XmlDocument doc, XmlElement installed, string hideout)
        {
            var e = (XmlElement)doc.ImportNode(installed, true);
            e.SetAttribute("initial_home_settlement", "Settlement." + hideout);
            return e;
        }

        // The installed retirement retreat re-emitted with its installed ids (a world alias) at a generated land point, with a world culture.
        internal static XmlElement RetreatRecord(XmlDocument doc, XmlElement installed, double x, double y, string culture)
        {
            var e = (XmlElement)doc.ImportNode(installed, true);
            e.SetAttribute("posX", Number(x)); e.SetAttribute("posY", Number(y));
            e.SetAttribute("culture", "Culture." + culture);
            return e;
        }

        // An installed creation culture re-emitted with its start point at a generated start gate.
        internal static XmlElement StartCultureRecord(XmlDocument doc, XmlElement installed, double x, double y)
        {
            var e = (XmlElement)doc.ImportNode(installed, true);
            e.SetAttribute("start_point_position_x", Number(x)); e.SetAttribute("start_point_position_y", Number(y));
            return e;
        }

        // The retreat's culture rule: the culture of the nearest town (ties by id).
        internal static string NearestTownCulture(double x, double y, IEnumerable<SettlementFactory.SettlementIntent> towns, Func<double, double, double, double, double> distance)
        {
            var town = towns.OrderBy(t => distance(x, y, t.X, t.Y)).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
            return town == null ? null : town.Culture;
        }

        // The retreat: the passable cell of the land group with the most towns farthest from every settlement and hideout.
        private void Retreat(Result result, GeneratedSpawnModel.CellMap map, List<SettlementFactory.SettlementIntent> towns, List<SettlementFactory.SettlementIntent> fixed_)
        {
            if (Record(settlementsCatalog, Text(Obj(policy, "retreat"), "settlement")) == null) { Problem(CodeCatalog, "the installed retirement retreat is missing"); return; }
            var main = towns.GroupBy(t => map.Group(t.GateX, t.GateY)).Where(g => g.Key >= 0).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
            if (main == null) { Problem(CodeMap, "no land group holds a town"); return; }
            var all = fixed_.Concat(result.Hideouts).ToList();
            var best = map.Cells(main.Key).OrderByDescending(c => all.Min(s => map.Distance(c.X, c.Y, s.X, s.Y))).ThenBy(c => c.Row).ThenBy(c => c.Col).FirstOrDefault();
            if (best == null) { Problem(CodeRetreat, "no passable cell for the retreat"); return; }
            result.RetreatX = best.X; result.RetreatY = best.Y;
            result.RetreatCulture = NearestTownCulture(best.X, best.Y, towns, map.Distance);
        }

        // The cultures character creation can offer: the installed main cultures with a start point (CharacterCreationCampaignBehavior lists
        // the main cultures; War Sails adds nord, a main culture in the merged catalog).
        internal static List<string> CreationCultures(XmlDocument cultures)
        {
            return cultures.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.GetAttribute("is_main_culture") == "true" && e.HasAttribute("start_point_position_x"))
                .Select(e => e.GetAttribute("id")).OrderBy(c => c, StringComparer.Ordinal).ToList();
        }

        private void Starts(Result result, IDictionary<string, object> travel, IDictionary<string, object> ports, GeneratedSpawnModel.CellMap map, List<SettlementFactory.SettlementIntent> towns,
                            HeroFactory.Result heroes, SettlementFactory.Result world)
        {
            var p = Obj(policy, "starts");
            int foodMinimum = Int(p, "foodMinimum"), notablesMinimum = Int(p, "notablesMinimum");
            var creation = List(p, "creationCultures").Cast<string>().ToList();
            var installedCreation = CreationCultures(culturesCatalog);
            if (creation.Count == 0 || !creation.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(installedCreation))
                Problem(CodePolicy, "startup.json creationCultures (" + String.Join(", ", creation) + ") are not the installed creation cultures (" + String.Join(", ", installedCreation) + ")");
            var open = Records(travel, "basins").ToDictionary(b => Convert.ToString(b["id"], CultureInfo.InvariantCulture), b => { object v; return b.TryGetValue("open", out v) && v is bool && (bool)v; }, StringComparer.Ordinal);
            var basinOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var port in Records(ports, "ports")) { var chosen = Obj(port, "chosen"); if (chosen != null && Text(port, "town") != null && chosen.ContainsKey("basin")) basinOf[Text(port, "town")] = Convert.ToString(chosen["basin"], CultureInfo.InvariantCulture); }
            var notables = heroes.RuntimeHeroes.Where(h => h.Occupation != "Wanderer").GroupBy(h => h.Settlement).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Func<SettlementFactory.SettlementIntent, StartIntent> evaluate = t => {
                var s = new StartIntent { Culture = t.Culture, Town = t.Id, X = t.GateX, Y = t.GateY, Food = settlements.TradeAreaFood(world, t.Id), Notables = notables.ContainsKey(t.Id) ? notables[t.Id] : 0 };
                if (!t.Port || !t.PortX.HasValue) { s.Reason = "not a port town"; return s; }
                s.PortX = t.PortX.Value; s.PortY = t.PortY.Value;
                string basin; basinOf.TryGetValue(t.Id, out basin); s.Basin = basin;
                var destination = basin == null ? null : towns.Where(o => o != t && o.Port && o.PortX.HasValue && basinOf.ContainsKey(o.Id) && basinOf[o.Id] == basin)
                    .OrderBy(o => map.Distance(t.PortX.Value, t.PortY.Value, o.PortX.Value, o.PortY.Value)).ThenBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
                if (destination != null) { s.SeaDestination = destination.Id; s.DestinationPortX = destination.PortX.Value; s.DestinationPortY = destination.PortY.Value; }
                int shipyard; s.ShipSource = t.Buildings.TryGetValue("building_shipyard", out shipyard) && shipyard > 0 ? "building_shipyard level " + shipyard + " of " + t.Id : null;
                s.Embarkation = "the player embarks only at a town port (MMR-17): " + t.Id + "'s port";
                bool basinOpen = basin != null && open.ContainsKey(basin) && open[basin];
                if (s.Food < foodMinimum) s.Reason = "trade-area food " + s.Food + " below " + foodMinimum;
                else if (s.Notables < notablesMinimum) s.Reason = "no notable to recruit from";
                else if (!basinOpen) s.Reason = "its port's sea basin is not open";
                else if (destination == null) s.Reason = "no other port town in its sea basin";
                else if (s.ShipSource == null) s.Reason = "no shipyard";
                else if (!map.Passable(t.GateX, t.GateY)) s.Reason = "gate is not passable land";
                return s; };
            var evaluated = towns.Select(evaluate).ToList();
            var qualifying = evaluated.Where(s => s.Reason == null).ToList();
            if (qualifying.Count == 0) { Problem(CodeNoStart, "no town qualifies as a start: " + String.Join("; ", evaluated.GroupBy(s => s.Reason).Select(g => g.Count() + " " + g.Key))); return; }
            // Every creation culture, whether or not the world presents through it: a culture without a qualifying town of its own takes the
            // qualifying town nearest its towns, and a culture without towns the qualifying town the starts stream draws for it.
            foreach (var culture in creation.OrderBy(c => c, StringComparer.Ordinal))
            {
                var own = qualifying.Where(s => s.Culture == culture).OrderBy(s => Stream("starts", culture + ":" + s.Town, 0)).ThenBy(s => s.Town, StringComparer.Ordinal).FirstOrDefault();
                StartIntent chosen = own;
                if (chosen == null)
                {
                    var cultureTowns = towns.Where(t => t.Culture == culture).ToList();
                    var near = cultureTowns.Count > 0
                        ? qualifying.OrderBy(s => cultureTowns.Min(t => map.Distance(t.X, t.Y, s.X, s.Y))).ThenBy(s => s.Town, StringComparer.Ordinal).First()
                        : qualifying.OrderBy(s => Stream("starts", culture + ":fallback:" + s.Town, 0)).ThenBy(s => s.Town, StringComparer.Ordinal).First();
                    chosen = new StartIntent { Culture = culture, Town = near.Town, X = near.X, Y = near.Y, PortX = near.PortX, PortY = near.PortY, Basin = near.Basin, SeaDestination = near.SeaDestination,
                                               DestinationPortX = near.DestinationPortX, DestinationPortY = near.DestinationPortY, ShipSource = near.ShipSource, Embarkation = near.Embarkation,
                                               Food = near.Food, Notables = near.Notables, Fallback = true,
                                               Reason = cultureTowns.Count == 0 ? "the world has no " + culture + " town"
                                                                                : "no " + culture + " town qualifies (" + String.Join("; ", evaluated.Where(s => s.Culture == culture).Select(s => s.Town + ": " + s.Reason)) + ")" };
                }
                else chosen = new StartIntent { Culture = culture, Town = own.Town, X = own.X, Y = own.Y, PortX = own.PortX, PortY = own.PortY, Basin = own.Basin, SeaDestination = own.SeaDestination,
                                                DestinationPortX = own.DestinationPortX, DestinationPortY = own.DestinationPortY, ShipSource = own.ShipSource, Embarkation = own.Embarkation,
                                                Food = own.Food, Notables = own.Notables };
                result.Starts.Add(chosen);
            }
        }

        private void Documents(Result result)
        {
            var settlementsDoc = new XmlDocument(); settlementsDoc.AppendChild(settlementsDoc.CreateElement("Settlements"));
            foreach (var h in result.Hideouts) settlementsDoc.DocumentElement.AppendChild(SettlementFactory.Element(settlementsDoc, h, settlements.Prototype(h.Prototype)));
            settlementsDoc.DocumentElement.AppendChild(RetreatRecord(settlementsDoc, Record(settlementsCatalog, Text(Obj(policy, "retreat"), "settlement")), result.RetreatX, result.RetreatY, result.RetreatCulture));
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Settlements", settlementsDoc));
            var factionsDoc = new XmlDocument(); factionsDoc.AppendChild(factionsDoc.CreateElement("Factions"));
            foreach (var clan in result.BanditHomes) factionsDoc.DocumentElement.AppendChild(BanditClanRecord(factionsDoc, Record(factionsCatalog, clan.Key), clan.Value));
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Factions", factionsDoc));
            var culturesDoc = new XmlDocument(); culturesDoc.AppendChild(culturesDoc.CreateElement("SPCultures"));
            foreach (var s in result.Starts)
            {
                var record = Record(culturesCatalog, s.Culture);
                if (record == null) { Problem(CodeCatalog, "creation culture " + s.Culture + " is not installed"); continue; }
                culturesDoc.DocumentElement.AppendChild(StartCultureRecord(culturesDoc, record, s.X, s.Y));
            }
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("SPCultures", culturesDoc));
            foreach (var h in result.Hideouts) { result.DeclaredKeys.Add("Settlement." + h.Id); result.DeclaredKeys.Add("Hideout." + h.Id + "_component"); }
            result.DeclaredKeys.Add("Settlement." + Text(Obj(policy, "retreat"), "settlement"));
            result.DeclaredKeys.Add("RetirementSettlementComponent." + Text(Obj(policy, "retreat"), "component"));
            foreach (var clan in result.BanditHomes.Keys) result.DeclaredKeys.Add("Faction." + clan);
            ThrowIfRefused();
        }

        private static XmlElement Record(XmlDocument doc, string id)
        {
            return doc == null || id == null ? null : doc.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("id") == id);
        }

        private ulong Stream(string stream, string entity, int draw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.startup-stream/1\n" + stream + "\n" + streams[stream].ToString(CultureInfo.InvariantCulture) + "\n" +
                                                                               prefix + "\n" + entity + "\n" + draw.ToString(CultureInfo.InvariantCulture)));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        // MMR-287 (RT-28): a hideout's bandit culture weights, (1 - share) x its nearest town culture's cultureWeights + share x the ground's:
        // biomeWeights of its cell's natural biome, plus coastWeight for coastCulture on a coast cell, each part normalized to 1. A ground
        // with no weights (a biome the policy does not list, off the coast) leaves the town's weights alone.
        internal static SortedDictionary<string, double> GroundWeights(IDictionary<string, object> town, IDictionary<string, object> biome, string biomeName, bool coast)
        {
            Func<IDictionary<string, double>, SortedDictionary<string, double>> normalized = w =>
            {
                double sum = w.Values.Where(v => v > 0).Sum();
                var n = new SortedDictionary<string, double>(StringComparer.Ordinal);
                if (sum > 0) foreach (var kv in w.Where(kv => kv.Value > 0)) n[kv.Key] = kv.Value / sum;
                return n;
            };
            var townPart = normalized((town ?? new Dictionary<string, object>()).ToDictionary(kv => kv.Key, kv => Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture)));
            var groundRaw = new Dictionary<string, double>(StringComparer.Ordinal);
            var listed = Obj(Obj(biome, "biomeWeights"), biomeName ?? "");
            if (listed != null) foreach (var kv in listed) groundRaw[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
            if (coast)
            {
                string culture = Text(biome, "coastCulture");
                double extra = Double(biome, "coastWeight");
                double have; groundRaw[culture] = (groundRaw.TryGetValue(culture, out have) ? have : 0) + extra;
            }
            var groundPart = normalized(groundRaw);
            if (groundPart.Count == 0) return townPart;
            if (townPart.Count == 0) return groundPart;
            double share = Double(biome, "share");
            var mixed = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var key in townPart.Keys.Union(groundPart.Keys))
            {
                double t, g;
                double v = (1 - share) * (townPart.TryGetValue(key, out t) ? t : 0) + share * (groundPart.TryGetValue(key, out g) ? g : 0);
                if (v > 0) mixed[key] = v;
            }
            return mixed;
        }

        // A draw over real weights (ordinal key order): the stream value's top 53 bits as a unit fraction of the total.
        private string WeightedReal(string stream, string entity, IDictionary<string, double> weights)
        {
            var items = weights.Where(w => w.Value > 0).OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
            double total = items.Sum(w => w.Value);
            if (items.Count == 0 || !(total > 0)) { Problem(CodePolicy, "no weights for " + entity); return null; }
            double roll = (Stream(stream, entity, 0) >> 11) / (double)(1UL << 53) * total;
            foreach (var w in items) { if (roll < w.Value) return w.Key; roll -= w.Value; }
            return items.Last().Key;
        }

        // MMR-287: the natural biome of a logical campaign position, read from the bundle's masks.bin biome plane (MNBLMK01, plane 0, the
        // terrain-materials.json biome ids of the bundle's own policy) at the sample nearest its physical position (physical = logical +
        // (strip, poleMargin), projection.json periodicLayout), clamped to the domain like WorldSurface.BiomeAt.
        public static Func<double, double, string> CellBiomes(byte[] masks, string projectionJson, string materialsJson)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var projection = (IDictionary<string, object>)json.DeserializeObject(projectionJson.TrimStart('\uFEFF'));
            var materials = (IDictionary<string, object>)json.DeserializeObject(materialsJson.TrimStart('\uFEFF'));
            if (masks == null || masks.Length < 12 || Encoding.ASCII.GetString(masks, 0, 8) != "MNBLMK01") throw new ArgumentException("masks.bin is not MNBLMK01");
            int side = (int)BitConverter.ToUInt32(masks, 8);
            if (side < 2 || masks.Length != 12 + 4L * side * side) throw new ArgumentException("masks.bin does not hold 4 planes of " + side + " x " + side + " samples");
            var layout = Obj(projection, "periodicLayout");
            double width = Double(layout, "physicalWidth"), strip = Double(layout, "strip"), poleMargin = Double(layout, "poleMargin");
            if (!(width > 0)) throw new ArgumentException("projection.json has no periodicLayout.physicalWidth");
            double spacing = width / (side - 1);
            var names = new Dictionary<int, string>();
            foreach (var b in List(materials, "biomes").OfType<IDictionary<string, object>>()) names[Convert.ToInt32(b["id"], CultureInfo.InvariantCulture)] = Text(b, "core");
            return (x, y) =>
            {
                int c = (int)Math.Max(0, Math.Min(side - 1, Math.Round((x + strip) / spacing))), r = (int)Math.Max(0, Math.Min(side - 1, Math.Round((y + poleMargin) / spacing)));
                string name;
                return names.TryGetValue(masks[12 + r * side + c], out name) ? name : null;
            };
        }

        private string Weighted(string stream, string entity, IDictionary<string, object> weights)
        {
            var items = weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new KeyValuePair<string, int>(w.Key, Convert.ToInt32(w.Value, CultureInfo.InvariantCulture))).Where(w => w.Value > 0).ToList();
            int total = items.Sum(w => w.Value);
            if (total == 0) { Problem(CodePolicy, "no weights for " + entity); return null; }
            int roll = (int)(Stream(stream, entity, 0) % (ulong)total);
            foreach (var w in items) { if (roll < w.Value) return w.Key; roll -= w.Value; }
            return items.Last().Key;
        }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.startup-intents", ["version"] = 1, ["worldPrefix"] = r.WorldPrefix,
                ["starts"] = r.Starts.Select(s => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["culture"] = s.Culture, ["town"] = s.Town, ["gate"] = new[] { s.X, s.Y }, ["port"] = new[] { s.PortX, s.PortY }, ["basin"] = s.Basin,
                    ["seaDestination"] = s.SeaDestination, ["destinationPort"] = new[] { s.DestinationPortX, s.DestinationPortY }, ["shipSource"] = s.ShipSource,
                    ["embarkation"] = s.Embarkation, ["food"] = s.Food, ["notables"] = s.Notables, ["fallback"] = s.Fallback, ["reason"] = s.Reason }).ToList(),
                ["hideouts"] = r.Hideouts.Select(h => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["id"] = h.Id, ["uid"] = h.Uid, ["culture"] = h.Culture, ["position"] = new[] { h.X, h.Y }, ["component"] = h.LandGroup, ["prototype"] = h.Prototype,
                    ["mapPrefab"] = h.MapPrefab, ["nearestTown"] = r.HideoutTowns[h.Id] }).ToList(),
                ["banditHomes"] = r.BanditHomes, ["retreat"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["position"] = new[] { r.RetreatX, r.RetreatY }, ["culture"] = r.RetreatCulture },
                ["anchors"] = r.Anchors.GroupBy(a => a.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                ["plan"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["notables"] = r.Plan.Notables.Count, ["companions"] = r.Plan.Companions.Count, ["relations"] = r.Plan.Relations.Count },
                ["notes"] = r.Notes };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        private static string Number(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }
        private static string Sha256(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> Obj(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as IDictionary<string, object> : null; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static int Int(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; }
        private static double Double(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; }
        private static IEnumerable<object> List(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? ((IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }
        private static IEnumerable<IDictionary<string, object>> Records(IDictionary<string, object> o, string key) { return List(o, key).OfType<IDictionary<string, object>>(); }
    }
}
