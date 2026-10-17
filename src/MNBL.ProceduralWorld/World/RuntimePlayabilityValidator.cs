using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Surface
{
    // What the semantic route set is built from: the world's settlements at their final positions (MMR-40's resolved gates and town ports,
    // logical or physical), their kingdoms, owners and bindings, the kingdoms' capitals, every clan's naval capability, and the player starts.
    internal sealed class PlayabilityWorld
    {
        public sealed class Site { public string Id, Kind, Kingdom, Bound, Owner; public bool HasPort; public double[] Gate, Port; public int Group = -1, Basin = -1; }
        public sealed class Faction { public string Id, Capital, RulingClan; }
        public sealed class Capability { public bool Naval; public string Reason; }
        public sealed class Start { public string Culture, Town; public double[] Point; }
        public readonly SortedDictionary<string, Site> Sites = new SortedDictionary<string, Site>(StringComparer.Ordinal);
        public readonly List<Faction> Kingdoms = new List<Faction>();
        public readonly Dictionary<string, Capability> Clans = new Dictionary<string, Capability>(StringComparer.Ordinal);
        public readonly List<Start> Starts = new List<Start>();
        public bool StartCandidates;
        public Func<double[], double[], double> Distance;

        public Capability Of(string clan)
        {
            Capability c;
            return clan != null && Clans.TryGetValue(clan, out c) ? c : new Capability { Naval = false, Reason = clan == null ? "no clan" : "clan " + clan + " is unknown" };
        }

        // Before activation: the bundle's records (compiled/campaign/campaign.json kingdoms, towns, castles, villages, clans) and travel graph
        // (groups, basins, port transitions), and the settlement layout (gates, town ports). A clan may hold ships when its home shares a land
        // group with a port town on an open basin (MMR-35's clan rule; the runtime fleet rule refuses ships to every other clan). The player
        // starts are every town MMR-34's start rule can choose on the map: a port town on an open basin with another port town on that basin
        // (its sea destination, the nearest such port). MMR-34 picks every start, fallbacks included, among the towns that also pass its
        // economy conditions (food, notables, shipyard), so the candidates hold every start it can pick.
        public static PlayabilityWorld FromBundle(NavigationInput input, byte[] campaignJson, SettlementLayout layout, IPlacementGround ground)
        {
            var w = new PlayabilityWorld { StartCandidates = true, Distance = (a, b) => SettlementPositionResolver.Distance(ground, a, b) };
            var campaign = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(new UTF8Encoding(false).GetString(campaignJson).TrimStart('﻿'));
            var kingdomOf = new Dictionary<string, string>(StringComparer.Ordinal); var ownerOf = new Dictionary<string, string>(StringComparer.Ordinal); var boundOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IDictionary<string, object> t in Json.A(campaign, "towns")) { kingdomOf[Json.S(t, "id")] = Json.S(t, "kingdom"); ownerOf[Json.S(t, "id")] = Json.S(t, "owner"); }
            foreach (IDictionary<string, object> c in Json.A(campaign, "castles")) { kingdomOf[Json.S(c, "id")] = Json.S(c, "kingdom"); ownerOf[Json.S(c, "id")] = Json.S(c, "owner"); }
            foreach (IDictionary<string, object> v in Json.A(campaign, "villages")) boundOf[Json.S(v, "id")] = Json.S(v, "bound");
            var openBasin = input.Basins.ToDictionary(b => b.Id, b => b.Open);
            var transitionBasin = input.Transitions.Where(x => x.Kind == "port").GroupBy(x => x.Town).ToDictionary(g => g.Key, g => g.First().Basin, StringComparer.Ordinal);
            foreach (var s in input.Settlements.Where(s => !input.Excluded.Contains(s.Id)))
            {
                if (s.Kind != "town" && s.Kind != "castle" && s.Kind != "village") continue;
                var r = layout.Find(s.Id);
                if (r == null) continue;
                string kingdom, bound, owner; kingdomOf.TryGetValue(s.Id, out kingdom); boundOf.TryGetValue(s.Id, out bound); ownerOf.TryGetValue(s.Id, out owner);
                bool port = s.Kind == "town" && r.Port != null && transitionBasin.ContainsKey(s.Id);
                w.Sites[s.Id] = new Site { Id = s.Id, Kind = s.Kind, Kingdom = kingdom, Bound = bound, Owner = owner, HasPort = port, Gate = r.Gate ?? r.Centre, Port = port ? r.Port : null, Group = s.Group,
                    Basin = port ? transitionBasin[s.Id] : -1 };
            }
            Func<Site, bool> openPort = x => x.HasPort && openBasin.ContainsKey(x.Basin) && openBasin[x.Basin];
            foreach (IDictionary<string, object> c in Json.A(campaign, "clans"))
            {
                string id = Json.S(c, "id"), home = Json.S(c, "home"); Site homeSite;
                if (home == null || !w.Sites.TryGetValue(home, out homeSite)) { w.Clans[id] = new Capability { Naval = false, Reason = "clan " + id + " has no placed home" }; continue; }
                int ports = w.Sites.Values.Count(x => openPort(x) && x.Group == homeSite.Group);
                w.Clans[id] = new Capability { Naval = ports > 0, Reason = ports > 0 ? "clan " + id + "'s home " + home + " shares land group " + homeSite.Group + " with " + ports + " port towns on open basins"
                                                                                    : "no port town on an open basin in clan " + id + "'s home " + home + "'s land group " + homeSite.Group };
            }
            foreach (IDictionary<string, object> k in Json.A(campaign, "kingdoms")) w.Kingdoms.Add(new Faction { Id = Json.S(k, "id"), Capital = Json.S(k, "capital"), RulingClan = Json.S(k, "rulingClan") });
            foreach (var t in w.Sites.Values.Where(x => x.Kind == "town" && openPort(x)))
                if (w.Sites.Values.Any(o => o.Id != t.Id && o.HasPort && o.Basin == t.Basin)) w.Starts.Add(new Start { Culture = "(any creation culture)", Town = t.Id, Point = null });
            return w;
        }
    }

    // One directed route of the semantic set: from and to positions and domains, the navigation type the mover's capability gives (Default
    // for a land party, Naval for a fleet, All for a party that can embark), and a refusal when the mover cannot make it at all (a fief
    // across water for a clan that holds no ships).
    internal sealed class SemanticRoute
    {
        public string Id, Category, Subject, Mover, Type, FromId, ToId, Refused;
        public double[] From, To;
        public bool FromLand, ToLand;
    }

    // MMR-41: the post-placement semantic route set, each route in both directions under the mover's capability, through engine queries:
    //   village-market   every village to its bound settlement (its market) and back, villagers on land (Default)
    //   capital-fief     every kingdom's capital to each of its fiefs, the ruling clan's lords, and back, the fief owner's lords: on land within
    //                    a land group, else embarking (All) when the moving clan may hold ships; otherwise refused
    //   player-start     each start town (once, whatever cultures share it): its gate to its port and back (All, embarking), its port to the
    //                    nearest other port of its basin and back (Naval, its sea destination), and its gate to the nearest other town and back
    //                    (Default within a land group, else All); in a campaign also each distinct starting point that is not the town's gate
    //                    to the gate and back; no start at all is a refusal (MMR-34 refuses such a world)
    //   port-to-port     every basin's port towns joined by a spanning tree of nearest ports, each edge both ways by fleet (Naval)
    //   faction-access   every kingdom's capital to the nearest fief of another kingdom that its ruling clan can reach and whose owner can
    //                    come back, both directions; none is a refusal
    // Inspection worlds run the routes through NavigationValidator.EvaluateRoute (the same path, distance, shortcut and straight-line checks
    // as MMR-38's matrix); a campaign runs them through its engine path queries and checks its registered navigation caches (CampaignPlayability).
    // Then every compiler pending-runtime flag is resolved or failed (ResolveFlags) and an activation report is persisted (Persist) for
    // MMR-45's activation (Require).
    internal static class RuntimePlayabilityValidator
    {
        public const string Version = "mmr41/2";
        public const string ReportSchema = "mnbl.playability-report";
        public const string RecordSchema = "mnbl.playability-gate-record";
        public static readonly string[] Categories = { "village-market", "capital-fief", "player-start", "port-to-port", "faction-access" };

        public static List<SemanticRoute> RouteSet(PlayabilityWorld w)
        {
            var routes = new List<SemanticRoute>();
            var sites = w.Sites;
            Action<string, string, string, string, double[], bool, string, double[], bool, string, string> add = (category, subject, mover, fromId, from, fromLand, toId, to, toLand, type, refused) =>
                routes.Add(new SemanticRoute { Id = category + " " + fromId + (fromLand ? "" : " port") + " -> " + toId + (toLand ? "" : " port"), Category = category, Subject = subject, Mover = mover,
                    FromId = fromId, From = from, FromLand = fromLand, ToId = toId, To = to, ToLand = toLand, Type = type, Refused = refused });
            // A clan's lords between two gates: Default within one land group, else All when the clan may hold ships, else refused.
            Func<PlayabilityWorld.Site, PlayabilityWorld.Site, string, string[]> lords = (a, b, clan) => {
                if (a.Group == b.Group) return new[] { "Default", null };
                var c = w.Of(clan);
                return c.Naval ? new[] { "All", null } : new[] { "All", "the route crosses water and " + c.Reason };
            };
            // village-market
            foreach (var v in sites.Values.Where(s => s.Kind == "village"))
            {
                PlayabilityWorld.Site market;
                if (v.Bound == null || !sites.TryGetValue(v.Bound, out market)) { add("village-market", v.Id, "villagers", v.Id, v.Gate, true, v.Bound ?? "(none)", v.Gate, true, "Default", "the village's bound settlement is not placed"); continue; }
                string refused = v.Group == market.Group ? null : "the village and its market are on different land groups";
                add("village-market", v.Id, "villagers", v.Id, v.Gate, true, market.Id, market.Gate, true, "Default", refused);
                add("village-market", v.Id, "villagers", market.Id, market.Gate, true, v.Id, v.Gate, true, "Default", refused);
            }
            // capital-fief
            foreach (var k in w.Kingdoms)
            {
                PlayabilityWorld.Site capital;
                if (k.Capital == null || !sites.TryGetValue(k.Capital, out capital)) { add("capital-fief", k.Id, "lords of " + k.Id, k.Capital ?? "(none)", new double[] { 0, 0 }, true, "(fiefs)", new double[] { 0, 0 }, true, "Default", "the kingdom's capital is not placed"); continue; }
                foreach (var f in sites.Values.Where(s => (s.Kind == "town" || s.Kind == "castle") && s.Kingdom == k.Id && s.Id != capital.Id))
                {
                    var there = lords(capital, f, k.RulingClan); var back = lords(f, capital, f.Owner);
                    add("capital-fief", k.Id, "lords of " + k.RulingClan, capital.Id, capital.Gate, true, f.Id, f.Gate, true, there[0], there[1]);
                    add("capital-fief", k.Id, "lords of " + f.Owner, f.Id, f.Gate, true, capital.Id, capital.Gate, true, back[0], back[1]);
                }
            }
            // port-to-port: per basin, a spanning tree of nearest ports (Prim's).
            foreach (var basin in sites.Values.Where(s => s.HasPort && s.Port != null).GroupBy(s => s.Basin).OrderBy(g => g.Key))
            {
                var ports = basin.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
                if (ports.Count < 2) continue;
                var inTree = new List<PlayabilityWorld.Site> { ports[0] }; var rest = ports.Skip(1).ToList();
                while (rest.Count > 0)
                {
                    PlayabilityWorld.Site bestFrom = null, bestTo = null; double best = double.MaxValue;
                    foreach (var a in inTree) foreach (var b in rest) { double d = w.Distance(a.Port, b.Port); if (d < best - 1e-12) { best = d; bestFrom = a; bestTo = b; } }
                    add("port-to-port", "basin " + basin.Key, "fleets", bestFrom.Id, bestFrom.Port, false, bestTo.Id, bestTo.Port, false, "Naval", null);
                    add("port-to-port", "basin " + basin.Key, "fleets", bestTo.Id, bestTo.Port, false, bestFrom.Id, bestFrom.Port, false, "Naval", null);
                    inTree.Add(bestTo); rest.Remove(bestTo);
                }
            }
            // player-start: once per start town.
            if (w.Starts.Count == 0) add("player-start", "world", "player", "(no start)", new double[] { 0, 0 }, true, "(no start)", new double[] { 0, 0 }, true, "Default", "no town can be a player start (MMR-34 refuses such a world)");
            foreach (var group in w.Starts.GroupBy(s => s.Town).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                PlayabilityWorld.Site town;
                string subject = "start town " + group.Key + " (" + String.Join(", ", group.Select(s => s.Culture).Distinct().OrderBy(c => c, StringComparer.Ordinal)) + ")";
                if (!sites.TryGetValue(group.Key, out town)) { add("player-start", subject, "player", group.Key, new double[] { 0, 0 }, true, group.Key, new double[] { 0, 0 }, true, "Default", "the start town is not placed"); continue; }
                foreach (var point in group.Where(s => s.Point != null && w.Distance(s.Point, town.Gate) > 1e-3).Select(s => s.Point).GroupBy(p => Math.Round(p[0], 3) + "," + Math.Round(p[1], 3)).Select(g => g.First()))
                {
                    string label = "start point " + Math.Round(point[0], 3).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(point[1], 3).ToString(CultureInfo.InvariantCulture);
                    add("player-start", subject, "player", label, point, true, town.Id, town.Gate, true, "Default", null);
                    add("player-start", subject, "player", town.Id, town.Gate, true, label, point, true, "Default", null);
                }
                if (town.HasPort && town.Port != null)
                {
                    add("player-start", subject, "player", town.Id, town.Gate, true, town.Id, town.Port, false, "All", null);
                    add("player-start", subject, "player", town.Id, town.Port, false, town.Id, town.Gate, true, "All", null);
                    var sea = sites.Values.Where(o => o.Id != town.Id && o.HasPort && o.Port != null && o.Basin == town.Basin).OrderBy(o => w.Distance(town.Port, o.Port)).ThenBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (sea != null)
                    {
                        add("player-start", subject, "player fleet", town.Id, town.Port, false, sea.Id, sea.Port, false, "Naval", null);
                        add("player-start", subject, "player fleet", sea.Id, sea.Port, false, town.Id, town.Port, false, "Naval", null);
                    }
                    else add("player-start", subject, "player fleet", town.Id, town.Port, false, "(sea destination)", town.Port, false, "Naval", "no other port town on the start's basin");
                }
                var near = sites.Values.Where(o => o.Id != town.Id && o.Kind == "town").OrderBy(o => w.Distance(town.Gate, o.Gate)).ThenBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
                if (near != null)
                {
                    string type = near.Group == town.Group ? "Default" : "All";
                    add("player-start", subject, "player", town.Id, town.Gate, true, near.Id, near.Gate, true, type, null);
                    add("player-start", subject, "player", near.Id, near.Gate, true, town.Id, town.Gate, true, type, null);
                }
            }
            // faction-access: the nearest foreign fief both directions can make.
            foreach (var k in w.Kingdoms)
            {
                PlayabilityWorld.Site capital;
                if (k.Capital == null || !sites.TryGetValue(k.Capital, out capital)) continue;
                var foreign = sites.Values.Where(s => (s.Kind == "town" || s.Kind == "castle") && s.Kingdom != null && s.Kingdom != k.Id)
                    .OrderBy(s => w.Distance(capital.Gate, s.Gate)).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
                if (foreign.Count == 0) continue;
                var reachable = foreign.FirstOrDefault(f => lords(capital, f, k.RulingClan)[1] == null && lords(f, capital, f.Owner)[1] == null);
                if (reachable == null)
                {
                    var nearest = foreign[0];
                    var there = lords(capital, nearest, k.RulingClan); var back = lords(nearest, capital, nearest.Owner);
                    add("faction-access", k.Id, "lords of " + k.RulingClan, capital.Id, capital.Gate, true, nearest.Id, nearest.Gate, true, there[0], there[1] ?? "no foreign fief is reachable both ways: " + back[1]);
                    add("faction-access", k.Id, "lords of " + nearest.Owner, nearest.Id, nearest.Gate, true, capital.Id, capital.Gate, true, back[0], back[1] ?? "no foreign fief is reachable both ways: " + there[1]);
                    continue;
                }
                var go = lords(capital, reachable, k.RulingClan); var come = lords(reachable, capital, reachable.Owner);
                add("faction-access", k.Id, "lords of " + k.RulingClan, capital.Id, capital.Gate, true, reachable.Id, reachable.Gate, true, go[0], null);
                add("faction-access", k.Id, "lords of " + reachable.Owner, reachable.Id, reachable.Gate, true, capital.Id, capital.Gate, true, come[0], null);
            }
            // One route per id within a category and subject.
            return routes.GroupBy(r => r.Id + "|" + r.Category + "|" + r.Subject).Select(g => g.First()).ToList();
        }

        public static RouteCheck Check(SemanticRoute r)
        {
            return new RouteCheck { Id = r.Id, Check = r.Category, Subject = r.Subject, Type = r.Type, From = r.From, FromLand = r.FromLand, To = r.To, ToLand = r.ToLand, Expected = true, Gating = true,
                SourceIds = new[] { r.FromId, r.ToId } };
        }

        // Inspection worlds: each route through the engine (one step per route, for a caller that gives the engine thread back between steps).
        public static IEnumerable<string> Steps(NavigationPlan plan, INavigationEngine engine, IList<SemanticRoute> routes, List<Dictionary<string, object>> rows)
        {
            foreach (var r in routes)
            {
                Dictionary<string, object> row;
                if (r.Refused != null)
                    row = new Dictionary<string, object> { ["id"] = r.Id, ["check"] = r.Category, ["subject"] = r.Subject, ["type"] = r.Type, ["agrees"] = false, ["problems"] = new List<string> { "refused: " + r.Refused } };
                else row = NavigationValidator.EvaluateRoute(plan, engine, Check(r), null).Row;
                row["category"] = r.Category; row["mover"] = r.Mover; row["fromId"] = r.FromId; row["toId"] = r.ToId;
                rows.Add(row);
                yield return "playability_route";
            }
        }

        // Every compiler pending-runtime flag, resolved or failed, with what decided it:
        //   travel graph       engineValidation: the navigation report (MMR-38) is activatable
        //   host checks        each (check, subject): every engine face and route MMR-38 ran for it agrees; distinctPositions: no static
        //                      diagnostic names it; islands: the engine islands check found no problem
        //   transitions        each port transition: its portFace or seamTransition rows agree; each coast transition: its coastTransition rows
        //   ports              ports.json engineValidation: every portFace and portGate host check resolved and the settlement entity checks
        //                      passed (MMR-40: ports, arcs and drop-offs over sea faces)
        //   campaign graph     each pendingRuntime entry of the graph policy (worldgen/policies/graph-validation.json): W05 (runtime settlement,
        //                      gate and port positions) by the settlement entity checks, U03 (runtime route reachability) by the semantic route
        //                      set; an entry with no resolver here fails
        public static List<Dictionary<string, object>> ResolveFlags(byte[] travelGraphJson, byte[] portsJson, IEnumerable<string> graphPendingRuntime, IDictionary<string, object> navigationReport,
                                                                    IDictionary<string, object> settlementChecks, bool routesPassed, int routesFailed)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<byte[], IDictionary<string, object>> parse = b => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(b).TrimStart('﻿'));
            var graph = parse(travelGraphJson); var ports = parse(portsJson);
            var engine = navigationReport == null ? null : navigationReport["engine"] as IDictionary<string, object>;
            Func<string, IEnumerable<IDictionary<string, object>>> engineRows = name => engine == null ? Enumerable.Empty<IDictionary<string, object>>() : ((System.Collections.IEnumerable)engine[name]).Cast<IDictionary<string, object>>();
            var rowsByCheck = engineRows("faces").Concat(engineRows("routes")).GroupBy(r => (r.ContainsKey("check") ? r["check"] as string : null) + "|" + (r.ContainsKey("subject") ? r["subject"] as string : null))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var failures = navigationReport == null ? new List<IDictionary<string, object>>() : ((System.Collections.IEnumerable)navigationReport["failures"]).Cast<IDictionary<string, object>>().ToList();
            bool activatable = navigationReport != null && Equals(navigationReport["activatable"], true);
            var islandProblems = engine == null ? null : ((System.Collections.IEnumerable)((IDictionary<string, object>)engine["islands"])["problems"]).Cast<object>().ToList();
            bool settlementsPassed = settlementChecks != null && Equals(settlementChecks["passed"], true);
            var flags = new List<Dictionary<string, object>>();
            Action<string, string, string, bool, string> flag = (kind, check, subject, ok, by) => flags.Add(new Dictionary<string, object> { ["kind"] = kind, ["check"] = check, ["subject"] = subject, ["status"] = ok ? "resolved" : "failed", ["by"] = by });
            flag("travelGraph", "engineValidation", "travel graph", activatable && Json.S(graph, "engineValidation") == "pending", activatable ? "the navigation report is activatable" : "the navigation report is not activatable");
            var hostStatus = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (IDictionary<string, object> h in Json.A(graph, "hostChecks"))
            {
                string check = Json.S(h, "check"), subject = Json.S(h, "subject");
                string engineSubject = check == "seamTransition" ? subject.Replace("port ", "") : subject;
                bool ok; string by;
                if (check == "distinctPositions") { ok = !failures.Any(f => (f.ContainsKey("subject") ? f["subject"] as string : null) == "distinctPositions"); by = ok ? "every listed settlement was separated (no static diagnostic)" : "a listed settlement was not separated"; }
                else if (check == "islands") { ok = islandProblems != null && islandProblems.Count == 0; by = ok ? "the engine islands check found no problem" : "engine islands problems: " + (islandProblems == null ? "no report" : islandProblems.Count.ToString(CultureInfo.InvariantCulture)); }
                else
                {
                    List<IDictionary<string, object>> rows; rowsByCheck.TryGetValue(check + "|" + engineSubject, out rows);
                    int agree = rows == null ? 0 : rows.Count(r => Equals(r["agrees"], true));
                    ok = rows != null && rows.Count > 0 && agree == rows.Count;
                    by = rows == null || rows.Count == 0 ? "no engine face or route ran for it" : agree + " of " + rows.Count + " engine faces and routes agree";
                }
                hostStatus[check + "|" + subject] = ok;
                flag("hostCheck", check, subject, ok, by);
            }
            foreach (IDictionary<string, object> t in Json.A(graph, "transitions"))
            {
                string kind = Json.S(t, "kind"), subject; string[] checksFor;
                if (kind == "port") { subject = Json.S(t, "town"); checksFor = new[] { "portFace", "seamTransition" }; }
                else { var cell = Json.Cell(t["landCell"]); subject = "group " + Json.I(t, "group") + " cell " + cell[0] + "," + cell[1]; checksFor = new[] { "coastTransition" }; }
                var rows = checksFor.SelectMany(c => { List<IDictionary<string, object>> list; return rowsByCheck.TryGetValue(c + "|" + subject, out list) ? list : new List<IDictionary<string, object>>(); }).ToList();
                int agree = rows.Count(r => Equals(r["agrees"], true));
                bool ok = Json.S(t, "status") == "pendingEngineValidation" && rows.Count > 0 && agree == rows.Count;
                flag("transition", kind, subject, ok, rows.Count == 0 ? "no engine route ran for it" : agree + " of " + rows.Count + " " + String.Join("/", checksFor) + " rows agree");
            }
            bool portChecks = hostStatus.Where(kv => kv.Key.StartsWith("portFace|", StringComparison.Ordinal) || kv.Key.StartsWith("portGate|", StringComparison.Ordinal)).All(kv => kv.Value);
            flag("ports", "engineValidation", "ports.json", Json.S(ports, "engineValidation") == "pending" && portChecks && settlementsPassed,
                (portChecks ? "every portFace and portGate host check resolved" : "a portFace or portGate host check failed") + "; " + (settlementsPassed ? "the settlement entity checks passed" : "the settlement entity checks did not pass"));
            foreach (var entry in graphPendingRuntime ?? Enumerable.Empty<string>())
            {
                if (entry.StartsWith("W05:", StringComparison.Ordinal)) flag("campaignGraph", "pendingRuntime", entry, settlementsPassed, settlementsPassed ? "the settlement entity checks passed (MMR-40)" : "the settlement entity checks did not pass");
                else if (entry.StartsWith("U03:", StringComparison.Ordinal)) flag("campaignGraph", "pendingRuntime", entry, routesPassed, routesPassed ? "the semantic route set passed in both directions" : routesFailed + " semantic routes failed");
                else flag("campaignGraph", "pendingRuntime", entry, false, "no resolver for this pending-runtime entry");
            }
            return flags;
        }

        // The pending items that are not compiler pending-runtime flags, listed so the report is not read as "nothing pending": the campaign
        // records' content slots (compiled/validation.json pendingSlots, owned by the factory tickets and MMR-42) and the product policy's
        // pendingRuntimePolicies (runtime policies owned by their tickets).
        public static Dictionary<string, object> NotFlags(byte[] validationJson, byte[] productPolicyJson)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var result = new Dictionary<string, object>();
            if (validationJson != null)
            {
                var v = (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(validationJson).TrimStart('﻿'));
                object slots; result["pendingSlots"] = v.TryGetValue("pendingSlots", out slots) ? slots : null;
            }
            if (productPolicyJson != null)
            {
                var p = (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(productPolicyJson).TrimStart('﻿'));
                object pending; result["pendingRuntimePolicies"] = p.TryGetValue("pendingRuntimePolicies", out pending) ? pending : null;
            }
            result["note"] = "content slots and runtime policies owned by other tickets, not compiler pending-runtime flags";
            return result;
        }

        public static Dictionary<string, object> Report(string worldContentId, string sceneName, SettlementLayout layout, IDictionary<string, object> navigationReport, IDictionary<string, object> settlementChecks,
                                                        IList<SemanticRoute> routes, List<Dictionary<string, object>> rows, List<Dictionary<string, object>> flags, string engineName, IList<string> engineFaults,
                                                        Dictionary<string, object> notFlags)
        {
            var failed = rows.Where(r => !Equals(r["agrees"], true)).ToList();
            var byCategory = Categories.ToDictionary(c => c, c => (object)new Dictionary<string, object> { ["routes"] = rows.Count(r => (string)r["category"] == c),
                ["passed"] = rows.Count(r => (string)r["category"] == c && Equals(r["agrees"], true)), ["types"] = routes.Where(r => r.Category == c).GroupBy(r => r.Type).ToDictionary(g => g.Key, g => (object)g.Count()) });
            var flagSummary = flags.GroupBy(f => (string)f["kind"]).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object> { ["flags"] = g.Count(), ["resolved"] = g.Count(f => (string)f["status"] == "resolved") });
            int steepPads = layout == null ? 0 : layout.Problems.Count(p => p.Code == "settlement.steep_pad"), steepMarkers = layout == null ? 0 : layout.Problems.Count(p => p.Code == "settlement.steep_marker");
            var faults = (engineFaults ?? new string[0]).ToList();
            var stable = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = ReportSchema, ["version"] = 1, ["validatorVersion"] = Version, ["engine"] = engineName, ["engineFaults"] = faults, ["worldContentId"] = worldContentId, ["sceneName"] = sceneName,
                ["layoutSha256"] = layout == null ? null : layout.Sha256, ["navigationReportSha256"] = navigationReport == null ? null : navigationReport["reportSha256"],
                ["settlementChecksPassed"] = settlementChecks != null && Equals(settlementChecks["passed"], true),
                ["routes"] = new Dictionary<string, object> { ["total"] = rows.Count, ["passed"] = rows.Count - failed.Count, ["failed"] = failed.Count, ["byCategory"] = byCategory },
                ["routeRows"] = rows,
                ["failures"] = failed.Select(r => new Dictionary<string, object> { ["id"] = r["id"], ["category"] = r["category"], ["problems"] = r["problems"] }).ToList(),
                ["flags"] = new Dictionary<string, object> { ["total"] = flags.Count, ["resolved"] = flags.Count(f => (string)f["status"] == "resolved"), ["failed"] = flags.Count(f => (string)f["status"] == "failed"),
                    ["byKind"] = flagSummary, ["failedFlags"] = flags.Where(f => (string)f["status"] == "failed").ToList(), ["rows"] = flags },
                ["notFlags"] = notFlags,
                ["steepPads"] = new Dictionary<string, object> { ["pads"] = steepPads, ["leastSteepMarkersAndGates"] = steepMarkers, ["gating"] = false,
                    ["decision"] = "accepted: campaign movement and settlement interaction run on navigation faces, and every gate, marker and village is over a Default-valid land face (the settlement entity checks, MMR-40) with its routes passing here; a steep pad only shapes the icon's footing" },
                ["activatable"] = failed.Count == 0 && flags.All(f => (string)f["status"] == "resolved") && faults.Count == 0 };
            var report = new Dictionary<string, object>(stable);
            report["reportSha256"] = Json.Sha(new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(stable));
            return report;
        }

        public static string ReportPath(string store, string sceneName) { return Path.Combine(store, "world-playability", sceneName + ".json"); }

        // The graph policy's pending-runtime entries (worldgen/policies/graph-validation.json "pendingRuntime").
        public static List<string> PendingRuntime(IDictionary<string, object> graphPolicy) { return Json.A(graphPolicy, "pendingRuntime").Cast<string>().ToList(); }

        // An embedded policy document of this module (MNBL.ProceduralWorld.<name>), or null.
        public static byte[] Embedded(string name)
        {
            using (var stream = typeof(RuntimePlayabilityValidator).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + name))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
            }
        }

        // Validation of a scene begins by removing its previous report: a later run that stops before Persist leaves no stale activatable report.
        public static void Delete(string store, string sceneName) { string path = ReportPath(store, sceneName); if (File.Exists(path)) File.Delete(path); }

        // The record holds the report's exact text and its hash (as NavigationGate's), so Require checks the text the hash covers.
        public static string Persist(string store, Dictionary<string, object> report)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var stable = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in report) if (kv.Key != "reportSha256") stable[kv.Key] = kv.Value;
            string text = serializer.Serialize(stable);
            if (Json.Sha(text) != (string)report["reportSha256"]) throw new InvalidOperationException("The playability report's hash does not match its content.");
            string path = ReportPath(store, (string)report["sceneName"]);
            var record = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = RecordSchema, ["version"] = 1, ["reportSha256"] = report["reportSha256"], ["report"] = text };
            Persistence.DurableFile.WriteAllText(path, serializer.Serialize(record));
            return path;
        }

        // MMR-45's activation: a world's scene becomes a campaign map only with this scene's persisted playability report, unedited (its text
        // matches its hash), from this validator, made without injected engine faults, activatable, of the scene's world (a world scene's name
        // carries its content id), and bound to the scene's current navigation report (NavigationGate's record) and settlement layout report
        // (RuntimeWorldBackend.SettlementReportPath). Throws InvalidOperationException naming every reason.
        public static IDictionary<string, object> Require(string store, string sceneName, string navigationRecordPath, string settlementReportPath) { return RequireAt(ReportPath(store, sceneName), sceneName, navigationRecordPath, settlementReportPath); }

        // MMR-49: the same for a record at another path (a load checks a copy kept with the campaign before it restores it).
        public static IDictionary<string, object> RequireAt(string path, string sceneName, string navigationRecordPath, string settlementReportPath)
        {
            if (!File.Exists(path)) throw new InvalidOperationException("World " + sceneName + " has no playability report (MMR-41).");
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            IDictionary<string, object> report;
            var problems = new List<string>();
            try
            {
                var record = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(path));
                if (Json.S(record, "schema") != RecordSchema) throw new InvalidDataException("the file is not an " + RecordSchema);
                string text = Json.S(record, "report");
                if (text == null || Json.Sha(text) != Json.S(record, "reportSha256")) throw new InvalidDataException("the report does not match its recorded hash");
                report = (IDictionary<string, object>)serializer.DeserializeObject(text);
            }
            catch (Exception error) { throw new InvalidOperationException("World " + sceneName + "'s playability report cannot be used: " + error.Message); }
            if (Json.S(report, "schema") != ReportSchema) problems.Add("not an " + ReportSchema);
            if (Json.S(report, "validatorVersion") != Version) problems.Add("made by validator " + Json.S(report, "validatorVersion") + ", not " + Version);
            if (Json.S(report, "sceneName") != sceneName) problems.Add("names scene " + Json.S(report, "sceneName"));
            string content = Json.S(report, "worldContentId");
            if (SceneNames.IsWorldScene(sceneName) && (content == null || content.Length < 16 || !sceneName.StartsWith("mnbl_world_" + content.Substring(0, 16).ToLowerInvariant() + "_", StringComparison.Ordinal)))
                problems.Add("its world " + content + " is not the scene's");
            if (Json.A(report, "engineFaults").Length > 0) problems.Add("made with engine faults injected");
            try
            {
                var navigation = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(navigationRecordPath));
                if (Json.S(navigation, "reportSha256") != Json.S(report, "navigationReportSha256")) problems.Add("made against navigation report " + Json.S(report, "navigationReportSha256") + ", the scene's is " + Json.S(navigation, "reportSha256"));
            }
            catch (Exception error) { problems.Add("the scene's navigation report cannot be read (" + error.GetType().Name + ")"); }
            try
            {
                var layout = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(settlementReportPath));
                if (Json.S(layout, "layoutSha256") != Json.S(report, "layoutSha256")) problems.Add("made against settlement layout " + Json.S(report, "layoutSha256") + ", the scene's is " + Json.S(layout, "layoutSha256"));
            }
            catch (Exception error) { problems.Add("the scene's settlement layout report cannot be read (" + error.GetType().Name + ")"); }
            if (!Json.B(report, "activatable"))
            {
                var routes = (IDictionary<string, object>)report["routes"]; var flags = (IDictionary<string, object>)report["flags"];
                problems.Add("not activatable: " + Convert.ToString(routes["failed"], CultureInfo.InvariantCulture) + " routes and " + Convert.ToString(flags["failed"], CultureInfo.InvariantCulture) + " flags failed");
            }
            if (problems.Count > 0) throw new InvalidOperationException("World " + sceneName + "'s playability report refuses activation: " + String.Join("; ", problems));
            return report;
        }
    }
}
