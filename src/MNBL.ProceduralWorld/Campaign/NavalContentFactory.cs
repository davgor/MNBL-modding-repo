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
    // MMR-35: a generated world's ports, fleets and naval faction records (docs/naval-content.md). Pure managed. From the MMR-27 records,
    // the MMR-28 travel graph, cells and ports, the MMR-31 factions, the MMR-33 settlements, the MMR-34 hideouts and the naval policy
    // (worldgen/policies/naval-content.json) it builds:
    //   * a naval facility per port town: its basin (open), shipyard level and the hulls the shipyard can build (each in the policy's
    //     verified hull chain: mission ship, nested prefab, deck navmesh, slots), its seaward orientation (from the port toward its basin's
    //     water nearby) and its intents for the map scene (a port marker and a blockade arc whose ends are water of its basin, else a named
    //     diagnostic; MMR-40 places the entities);
    //   * a landing per harbour village at its port, which must be water of an open basin within the installed drop-off envelope (else the
    //     world is refused: the village would keep a port the engine uses);
    //   * naval roles: a kingdom is coastal when one of its towns is a port town on an open basin, a clan may hold ships when its home's land
    //     group holds one; everything else is landlocked, with a reason, and the runtime plan keeps those clans' ideal fleet at zero;
    //   * the installed pirate clans re-created as world aliases, homed at a generated hideout, and their spawn point intents on open sea;
    //   * diagnostics: every town the MMR-28 port compiler could not give a port, with its reasons, and the naval transitions of every land
    //     group other than the main one that holds a settlement (a group with a town and none is refused).
    // A world whose naval content cannot be built is refused with NavalException and quality codes from the policy. Construction
    // randomness comes from a named, versioned stream; the campaign keeps MBRandom.
    internal sealed class NavalContentFactory
    {
        public sealed class Facility
        {
            public string Town, Culture, Kingdom, Owner, ArcProblem;
            public int Basin, ShipyardLevel, LandGroup;
            public bool Open, ArcMirrored;
            public double Quality, X, Y, GateX, GateY, PortX, PortY, Orientation, ArcRotation;
            public double[] ArcStart, ArcEnd;
            public List<string> Hulls = new List<string>();
        }
        public sealed class Landing { public string Village, Town, Problem; public double X, Y, PortX, PortY, FromCentre; public int Basin; public bool Open; }
        public sealed class Role { public string Id, Kind, Reason; public bool Naval; public int LandGroup; public List<string> Ports = new List<string>(); }
        // X, Y: logical travel-grid coordinates for the factory's points (Logical); physical scene coordinates for a fixture placement's.
        public sealed class SpawnPoint { public string Uid, Clan, NearestPort, Zone; public double X, Y, Radius; public int Basin; public bool Logical; }
        public sealed class Pirate { public string Clan, Culture, Template, Home; public List<SpawnPoint> SpawnPoints = new List<SpawnPoint>(); }
        public sealed class Diagnostic { public string Kind, Subject, Reason; }
        public sealed class SlotValue { public string Town, Name, Value; }

        // What the runtime needs: the clans that may not hold ships (GeneratedClanShipOwnershipModel keeps their ideal fleet at zero).
        public sealed class NavalPlan
        {
            public SortedSet<string> Landlocked = new SortedSet<string>(StringComparer.Ordinal);
            // MMR-39: the pirate spawn points War Sails' pirates patrol (GeneratedNavalMapSceneWrapper serves them in place of PirateSpawnPoint
            // entities): a production world's are NavalContentFactory's (AddPirates over Result.Pirates), a fixture's come from its placement
            // (placement.naval.pirateSpawnPoints). The same world gives the same points every session, so saved patrol zones keep matching.
            public List<SpawnPoint> PirateSpawnPoints = new List<SpawnPoint>();

            public void AddPirates(IEnumerable<Pirate> pirates)
            {
                foreach (var pirate in pirates)
                    foreach (var s in pirate.SpawnPoints)
                        PirateSpawnPoints.Add(new SpawnPoint { Uid = s.Uid, Clan = s.Clan, NearestPort = s.NearestPort, Zone = s.Zone, X = s.X, Y = s.Y, Radius = s.Radius, Basin = s.Basin, Logical = true });
            }

            public void AddFixturePirates(IDictionary<string, object> placement)
            {
                object naval, points;
                if (placement == null || !placement.TryGetValue("naval", out naval) || !(naval is IDictionary<string, object>) ||
                    !((IDictionary<string, object>)naval).TryGetValue("pirateSpawnPoints", out points) || !(points is System.Collections.IEnumerable)) return;
                foreach (var item in ((System.Collections.IEnumerable)points).Cast<object>())
                {
                    var p = (IDictionary<string, object>)item;
                    PirateSpawnPoints.Add(new SpawnPoint { Uid = Convert.ToString(p["uid"], CultureInfo.InvariantCulture), Clan = Convert.ToString(p["clan"], CultureInfo.InvariantCulture),
                        X = Convert.ToDouble(p["x"], CultureInfo.InvariantCulture), Y = Convert.ToDouble(p["y"], CultureInfo.InvariantCulture),
                        Radius = Convert.ToDouble(p["radius"], CultureInfo.InvariantCulture), Zone = "fixture" });
                }
            }
        }

        public sealed class Result
        {
            public string WorldPrefix;
            public List<Facility> Facilities = new List<Facility>();
            public List<Landing> Landings = new List<Landing>();
            public List<Role> Roles = new List<Role>();
            public List<Pirate> Pirates = new List<Pirate>();
            public List<Diagnostic> Diagnostics = new List<Diagnostic>();
            public List<SlotValue> Slots = new List<SlotValue>();
            public List<string> Notes = new List<string>();
            public NavalPlan Plan = new NavalPlan();
            public List<KeyValuePair<string, XmlDocument>> Documents = new List<KeyValuePair<string, XmlDocument>>();
            public List<string> DeclaredKeys = new List<string>();
            public string IntentsJson, IntentsSha256;
        }

        public sealed class NavalException : InvalidOperationException
        {
            public NavalException(IList<string> problems, IList<string> codes)
                : base("Naval content refused [" + String.Join(", ", codes) + "]: " + String.Join("; ", problems.Take(20)) + (problems.Count > 20 ? "; ..." : "")) { Problems = problems; Codes = codes; }
            public IList<string> Problems { get; private set; }
            public IList<string> Codes { get; private set; }
            public bool Retryable { get; internal set; }
        }

        public const string CodePolicy = "quality.naval_policy", CodeCatalog = "quality.naval_catalog", CodeIsland = "quality.naval_unreachable_island", CodePort = "quality.naval_port";
        private static readonly string[] WaterClasses = { "sea", "lake", "river" };   // travel cell classes 2, 3, 4 (GeneratedSpawnModel)
        private const byte Sea = 2;

        private readonly IDictionary<string, object> policy;
        private readonly XmlDocument factionsCatalog, culturesCatalog, hullsCatalog, missionsCatalog, slotsCatalog, templatesCatalog;
        private readonly List<string> problems = new List<string>();
        private readonly SortedSet<string> codes = new SortedSet<string>(StringComparer.Ordinal);
        private string prefix;

        public NavalContentFactory(IDictionary<string, object> naval, XmlDocument factionsCatalog, XmlDocument culturesCatalog, XmlDocument hullsCatalog,
                                   XmlDocument missionsCatalog, XmlDocument slotsCatalog, XmlDocument templatesCatalog)
        {
            policy = naval; this.factionsCatalog = factionsCatalog; this.culturesCatalog = culturesCatalog; this.hullsCatalog = hullsCatalog;
            this.missionsCatalog = missionsCatalog; this.slotsCatalog = slotsCatalog; this.templatesCatalog = templatesCatalog;
        }

        // The shipyard level of every MMR-28 port town (one with a chosen candidate): the installed shares (policy shipyards.shares), assigned
        // in order of the chosen candidate's harbour quality, best first. SettlementFactory.Build takes it before the settlements are written.
        public static SortedDictionary<string, int> ShipyardLevels(IDictionary<string, object> naval, IDictionary<string, object> ports)
        {
            var shares = Obj(Obj(naval, "shipyards"), "shares");
            var towns = Records(ports, "ports").Where(p => Obj(p, "chosen") != null && Text(p, "town") != null)
                .OrderByDescending(p => Double(Obj(p, "chosen"), "quality")).ThenBy(p => Text(p, "town"), StringComparer.Ordinal).Select(p => Text(p, "town")).ToList();
            var levels = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int n = towns.Count, assigned = 0;
            foreach (var level in new[] { 3, 2 })
            {
                int count = (int)Math.Round(Double(shares, level.ToString(CultureInfo.InvariantCulture)) * n, MidpointRounding.AwayFromZero);
                foreach (var t in towns.Skip(assigned).Take(count)) levels[t] = level;
                assigned += count;
            }
            foreach (var t in towns.Skip(assigned)) levels[t] = 1;
            return levels;
        }

        public Result Build(IDictionary<string, object> campaign, IDictionary<string, object> travel, IDictionary<string, object> ports, byte[] cellsBin,
                            FactionFactory.Result factions, SettlementFactory.Result world, GeneratedCampaignInitializer.Result startup)
        {
            var result = new Result { WorldPrefix = world.WorldPrefix };
            prefix = world.WorldPrefix;
            if (Text(policy, "schema") != "mnbl.naval-content" || Int(policy, "version") != 1) Problem(CodePolicy, "not mnbl.naval-content version 1");
            var land = new GeneratedSpawnModel.CellMap(Obj(travel, "grid"), cellsBin, new[] { "land" }, Records(travel, "settlements"));
            var water = new GeneratedSpawnModel.CellMap(Obj(travel, "grid"), cellsBin, WaterClasses, new IDictionary<string, object>[0]);
            byte[] classes = new byte[land.Cols * land.Rows]; Array.Copy(cellsBin, 16, classes, 0, classes.Length);
            // Water components take the MMR-28 basin of the transitions on them; a component without any is judged by the MMR-28 open rule.
            var basinOf = new Dictionary<int, int>(); var openBasin = new Dictionary<int, bool>();
            foreach (var b in Records(travel, "basins")) openBasin[Int(b, "id")] = Bool(b, "open");
            foreach (var t in Records(travel, "transitions"))
            {
                var cell = List(t, "seaCell").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
                if (cell.Length != 2) continue;
                int comp = water.Component((cell[1] + 0.5) * water.Spacing, (cell[0] + 0.5) * water.Spacing), known;
                if (comp < 0) { Problem(CodePolicy, "transition sea cell (" + cell[0] + ", " + cell[1] + ") is not water"); continue; }
                if (basinOf.TryGetValue(comp, out known) && known != Int(t, "basin")) Problem(CodePolicy, "water component " + comp + " carries basins " + known + " and " + Int(t, "basin"));
                basinOf[comp] = Int(t, "basin");
            }
            Func<double, double, int> basinAt = (x, y) => { int comp = water.Component(x, y), b; return comp >= 0 && basinOf.TryGetValue(comp, out b) ? b : -1; };
            Func<double, double, bool> openAt = (x, y) => { int b = basinAt(x, y); bool o; return b >= 0 && openBasin.TryGetValue(b, out o) && o; };
            var towns = world.Settlements.Where(s => s.Kind == "town").OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            double spacing = towns.Count < 2 ? 0 : towns.Average(t => towns.Where(u => u != t).Min(u => land.Distance(t.X, t.Y, u.X, u.Y)));
            var levels = ShipyardLevels(policy, ports);
            var quality = Records(ports, "ports").Where(p => Obj(p, "chosen") != null).ToDictionary(p => Text(p, "town"), p => Double(Obj(p, "chosen"), "quality"), StringComparer.Ordinal);

            Agreement();
            Facilities(result, towns, land, water, basinAt, openAt, levels, quality);
            Landings(result, world, land, basinAt, openAt);
            Roles(result, factions, world, land);
            Diagnostics(result, ports, travel, land);
            Pirates(result, travel, classes, land, water, basinAt, openAt, spacing, startup);
            Slots(result, campaign);
            ThrowIfRefused();
            Documents(result);
            result.IntentsJson = Canonical(result);
            result.IntentsSha256 = Sha256(result.IntentsJson);
            return result;
        }

        // The policy must agree with the catalog it is given: the creation cultures' hull lists and naval roles, and the pirate templates.
        private void Agreement()
        {
            var cultures = Obj(Obj(policy, "hulls"), "cultures"); var roles = Obj(Obj(policy, "hulls"), "roles");
            foreach (var c in cultures ?? new Dictionary<string, object>())
            {
                var record = Record(culturesCatalog, c.Key);
                var installed = record == null ? new List<string>() : Children(record, "available_ship_hulls").Select(e => e.GetAttribute("id").Split('.').Last()).ToList();
                var listed = ((IDictionary<string, object>)c.Value).Keys.ToList();
                if (!installed.SequenceEqual(listed)) Problem(CodePolicy, "naval-content.json lists culture " + c.Key + "'s hulls as " + String.Join(", ", listed) + "; the catalog has " + String.Join(", ", installed));
                foreach (var h in (IDictionary<string, object>)c.Value)
                {
                    var chain = (IDictionary<string, object>)h.Value;
                    if (String.IsNullOrEmpty(Text(chain, "prefab")) || String.IsNullOrEmpty(Text(chain, "deckNavmesh"))) Problem(CodePolicy, "naval-content.json has no verified prefab and deck navmesh for hull " + h.Key);
                }
                var role = Obj(roles, c.Key);
                foreach (var key in new[] { "fishing_party_template", "settlement_patrol_template_coastal", "shipwright", "shipyard_worker" })
                    if (record != null && (record.GetAttribute(key).Split('.').Last() != (Text(role, key) ?? ""))) Problem(CodePolicy, "naval-content.json's " + key + " of culture " + c.Key + " is not the catalog's");
            }
            foreach (var p in Obj(Obj(policy, "pirates"), "clans") ?? new Dictionary<string, object>())
            {
                var record = Record(factionsCatalog, p.Key);
                if (record != null && record.GetAttribute("default_party_template").Split('.').Last() != Text((IDictionary<string, object>)p.Value, "template"))
                    Problem(CodePolicy, "naval-content.json's template of pirate clan " + p.Key + " is not the catalog's");
            }
        }

        private void Facilities(Result result, List<SettlementFactory.SettlementIntent> towns, GeneratedSpawnModel.CellMap land, GeneratedSpawnModel.CellMap water,
                                Func<double, double, int> basinAt, Func<double, double, bool> openAt, IDictionary<string, int> levels, IDictionary<string, double> quality)
        {
            var chains = Obj(Obj(policy, "hulls"), "cultures");
            var tiers = Obj(Obj(policy, "shipyards"), "hullTiers");
            var arc = Obj(Obj(policy, "blockades"), "arc");
            var start = List(arc, "start").Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            var end = List(arc, "end").Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            foreach (var t in towns.Where(t => t.Port && t.PortX.HasValue))
            {
                var f = new Facility { Town = t.Id, Culture = t.Culture, Kingdom = t.Kingdom, Owner = t.Owner, X = t.X, Y = t.Y, GateX = t.GateX, GateY = t.GateY,
                                       PortX = t.PortX.Value, PortY = t.PortY.Value, Basin = basinAt(t.PortX.Value, t.PortY.Value), Open = openAt(t.PortX.Value, t.PortY.Value),
                                       LandGroup = land.Group(t.GateX, t.GateY) };
                double q; quality.TryGetValue(t.Id, out q); f.Quality = q;
                int level; if (!levels.TryGetValue(t.Id, out level)) { Problem(CodePort, "port town " + t.Id + " has no MMR-28 chosen port candidate"); level = 0; }
                f.ShipyardLevel = level;
                int built; t.Buildings.TryGetValue("building_shipyard", out built);
                if (built != level) Problem(CodePolicy, "port town " + t.Id + " was written with shipyard level " + built + ", not its naval level " + level + " (pass ShipyardLevels to SettlementFactory.Build)");
                if (!f.Open) Problem(CodePort, "port town " + t.Id + "'s port (" + Number(f.PortX) + ", " + Number(f.PortY) + ") is " + (f.Basin < 0 ? "not water of any basin" : "on closed basin " + f.Basin));
                // The hulls its shipyard builds: its culture's available hulls up to the shipyard's tier.
                var culture = Record(culturesCatalog, t.Culture);
                if (culture == null) { Problem(CodeCatalog, "culture " + t.Culture + " of port town " + t.Id + " is not installed"); continue; }
                foreach (var h in Children(culture, "available_ship_hulls").Select(e => e.GetAttribute("id").Split('.').Last()))
                {
                    var hull = Record(hullsCatalog, h);
                    if (!HullResolves(h)) continue;
                    if (Obj(Obj(chains, t.Culture), h) == null) { Problem(CodePolicy, "hull " + h + " of culture " + t.Culture + " has no verified chain in naval-content.json"); continue; }
                    int tier = Int(tiers, hull.GetAttribute("ship_type"));
                    if (tier >= 1 && tier <= level) f.Hulls.Add(h);
                }
                if (f.Hulls.Count == 0) Problem(CodeCatalog, "port town " + t.Id + "'s shipyard (level " + level + ") can build no hull of culture " + t.Culture);
                // Orientation: seaward, the mean direction from the port to its basin's water cells within three cells (the coastline normal),
                // else the gate-to-port direction. The blockade arc: the installed offsets (measured in the installed scene's centre-to-port
                // frame, its only orientation) applied in that seaward frame; if an end is not water of the port's basin, the frame is turned
                // by 15-degree steps either way up to 90, then mirrored, and the first placement with both ends on that water wins.
                double sx = 0, sy = 0; int seen = 0;
                for (int dr = -3; dr <= 3; dr++)
                    for (int dc = -3; dc <= 3; dc++)
                    {
                        double cx = f.PortX + dc * land.Spacing, cy = f.PortY + dr * land.Spacing;
                        if ((dr != 0 || dc != 0) && water.Passable(cx, cy) && basinAt(cx, cy) == f.Basin) { double dx0 = dc * land.Spacing, dy0 = dr * land.Spacing, n0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0); sx += dx0 / n0; sy += dy0 / n0; seen++; }
                    }
                f.Orientation = seen > 0 && Math.Abs(sx) + Math.Abs(sy) > 1e-9 ? Math.Atan2(sy, sx) : Math.Atan2(f.PortY - t.GateY, Delta(land, t.GateX, f.PortX));
                Func<double, bool, double[][]> place = (angle, mirror) => {
                    double ux = Math.Cos(angle), uy = Math.Sin(angle), side = mirror ? -1 : 1;
                    Func<double[], double[]> at = o => new[] { Wrap(land, f.PortX + o[0] * ux - side * o[1] * uy), f.PortY + o[0] * uy + side * o[1] * ux };
                    return new[] { at(start), at(end) }; };
                Func<double[], bool> wet = p => water.Passable(p[0], p[1]) && basinAt(p[0], p[1]) == f.Basin;
                var tries = new List<KeyValuePair<double, bool>>();
                foreach (var mirror in new[] { false, true })
                    foreach (var step in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6 }) tries.Add(new KeyValuePair<double, bool>(step * Math.PI / 12, mirror));
                var fit = tries.Select(x => new { x.Key, x.Value, Ends = place(f.Orientation + x.Key, x.Value) }).FirstOrDefault(x => wet(x.Ends[0]) && wet(x.Ends[1]));
                if (fit != null) { f.ArcStart = fit.Ends[0]; f.ArcEnd = fit.Ends[1]; f.ArcRotation = fit.Key; f.ArcMirrored = fit.Value; }
                else
                {
                    var ends = place(f.Orientation, false); f.ArcStart = ends[0]; f.ArcEnd = ends[1];
                    f.ArcProblem = "no turn of the installed arc keeps both ends on water of basin " + f.Basin;
                    result.Diagnostics.Add(new Diagnostic { Kind = "blockade", Subject = t.Id, Reason = f.ArcProblem + "; the arc is staged seaward and MMR-40 must fit it to the placed coast" });
                }
                result.Facilities.Add(f);
            }
        }

        private bool HullResolves(string id)
        {
            var hull = Record(hullsCatalog, id);
            if (hull == null) { Problem(CodeCatalog, "ship hull " + id + " is not installed"); return false; }
            bool ok = true;
            if (Record(missionsCatalog, hull.GetAttribute("mission_ship")) == null) { Problem(CodeCatalog, "ship hull " + id + "'s mission ship " + hull.GetAttribute("mission_ship") + " is not installed"); ok = false; }
            foreach (var slot in hull.SelectNodes("AvailableSlots/ShipSlot").OfType<XmlElement>())
                if (Record(slotsCatalog, slot.GetAttribute("id")) == null) { Problem(CodeCatalog, "ship hull " + id + "'s slot " + slot.GetAttribute("id") + " is not installed"); ok = false; }
            return ok;
        }

        // Harbour villages: a landing at the village's port when it is water of an open basin within the installed drop-off envelope.
        private void Landings(Result result, SettlementFactory.Result world, GeneratedSpawnModel.CellMap land, Func<double, double, int> basinAt, Func<double, double, bool> openAt)
        {
            double envelope = Double(Obj(Obj(policy, "landings"), "fromCentre"), "maximum") + land.Spacing;
            foreach (var v in world.Settlements.Where(s => s.Kind == "village" && s.Harbour).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var l = new Landing { Village = v.Id, Town = v.Bound, X = v.X, Y = v.Y };
                if (!v.Port || !v.PortX.HasValue) { l.Problem = "the harbour village has no port"; Problem(CodePort, "harbour village " + v.Id + " has no port"); result.Landings.Add(l); continue; }
                l.PortX = v.PortX.Value; l.PortY = v.PortY.Value; l.Basin = basinAt(l.PortX, l.PortY); l.Open = openAt(l.PortX, l.PortY);
                l.FromCentre = land.Distance(v.X, v.Y, l.PortX, l.PortY);
                if (l.Basin < 0) l.Problem = "its harbour cell is not water of any basin";
                else if (!l.Open) l.Problem = "its harbour cell is on closed basin " + l.Basin;
                else if (l.FromCentre > envelope) l.Problem = "its port is " + Math.Round(l.FromCentre, 2) + " from the centre, beyond the installed drop-off envelope " + Math.Round(envelope, 2);
                if (l.Problem != null) Problem(CodePort, "harbour village " + v.Id + ": " + l.Problem + " (the village's XML port would still give it HasPort)");
                result.Landings.Add(l);
            }
        }

        private void Roles(Result result, FactionFactory.Result factions, SettlementFactory.Result world, GeneratedSpawnModel.CellMap land)
        {
            var open = result.Facilities.Where(f => f.Open).ToList();
            var byId = world.Settlements.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var k in factions.Kingdoms.OrderBy(k => k.Id, StringComparer.Ordinal))
            {
                var r = new Role { Id = k.Id, Kind = "kingdom" };
                r.Ports = open.Where(f => k.Towns.Contains(f.Town)).Select(f => f.Town).ToList();
                r.Naval = r.Ports.Count > 0;
                r.Reason = r.Naval ? r.Ports.Count + " port towns on open basins" : k.Towns.Count == 0 ? "the kingdom holds no town" : "none of its " + k.Towns.Count + " towns is a port town";
                result.Roles.Add(r);
            }
            foreach (var c in factions.Clans.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var r = new Role { Id = c.Id, Kind = "clan" };
                SettlementFactory.SettlementIntent home;
                if (c.HomeId == null || !byId.TryGetValue(c.HomeId, out home)) { r.Naval = false; r.LandGroup = -1; r.Reason = "the clan has no generated home settlement"; }
                else
                {
                    r.LandGroup = land.Group(home.GateX, home.GateY);
                    r.Ports = open.Where(f => f.LandGroup == r.LandGroup && r.LandGroup >= 0).Select(f => f.Town).ToList();
                    r.Naval = r.Ports.Count > 0;
                    r.Reason = r.Naval ? "its home " + home.Id + " shares land group " + r.LandGroup + " with " + r.Ports.Count + " port towns on open basins"
                                       : "no port town on an open basin in its home " + home.Id + "'s land group " + r.LandGroup;
                }
                if (!r.Naval) result.Plan.Landlocked.Add(c.Id);
                result.Roles.Add(r);
            }
        }

        // The towns the MMR-28 port compiler could not give a port, and every island's naval transitions (c2).
        private void Diagnostics(Result result, IDictionary<string, object> ports, IDictionary<string, object> travel, GeneratedSpawnModel.CellMap land)
        {
            foreach (var p in Records(ports, "ports").Where(p => Obj(p, "chosen") == null).OrderBy(p => Text(p, "town"), StringComparer.Ordinal))
            {
                var reasons = Records(p, "candidates").Select(c => Text(c, "reason") ?? Text(c, "status")).Where(x => x != null).Distinct().ToList();
                result.Diagnostics.Add(new Diagnostic { Kind = "impossible_port", Subject = Text(p, "town"), Reason = reasons.Count == 0 ? "no port candidate" : String.Join("; ", reasons) });
            }
            foreach (var t in List(ports, "coastalTownsWithoutHarbourRecord").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal))
                result.Diagnostics.Add(new Diagnostic { Kind = "impossible_port", Subject = t, Reason = "a coastal town without a producer harbour record (the MMR-28 compiler makes ports only from harbour records)" });
            int main = Int(travel, "mainGroup");
            var open = new HashSet<int>(Records(travel, "basins").Where(b => Bool(b, "open")).Select(b => Int(b, "id")));
            // Every land group other than the main one that holds a settlement (a town, a castle or a village).
            var settled = Records(travel, "settlements").GroupBy(s => Int(s, "group")).ToDictionary(g => g.Key, g => g.Count());
            foreach (var g in Records(travel, "groups").Where(g => settled.ContainsKey(Int(g, "id"))).OrderBy(g => Int(g, "id")))
            {
                int id = Int(g, "id");
                if (id == main) continue;
                var transitions = Records(travel, "transitions").Where(t => Int(t, "group") == id && open.Contains(Int(t, "basin"))).ToList();
                int portCount = transitions.Count(t => Text(t, "kind") == "port"), coastCount = transitions.Count(t => Text(t, "kind") == "coast");
                if (transitions.Count == 0 && Int(g, "towns") > 0) Problem(CodeIsland, "island land group " + id + " (" + Int(g, "towns") + " towns) has no port or coast transition to an open basin");
                result.Diagnostics.Add(new Diagnostic { Kind = "island", Subject = "land group " + id, Reason = Int(g, "towns") + " towns, " + settled[id] + " settlements; " + portCount + " port and " + coastCount + " coast transitions to open basins" +
                                                                                                                 (transitions.Count == 0 ? " (no settlement there can be reached by sea)" : "") + (Bool(g, "playerSafe") ? "; player-safe" : "; not player-safe") });
            }
        }

        // The installed pirate clans as world aliases, and their spawn point intents on open sea.
        private void Pirates(Result result, IDictionary<string, object> travel, byte[] classes, GeneratedSpawnModel.CellMap land, GeneratedSpawnModel.CellMap water,
                             Func<double, double, int> basinAt, Func<double, double, bool> openAt, double spacing, GeneratedCampaignInitializer.Result startup)
        {
            var p = Obj(policy, "pirates");
            var w = Obj(policy, "water");
            int openSea = Int(w, "openSeaCells"), minOpen = Int(w, "minOpenCells");
            double radius = Double(p, "radius") * spacing, inner = Double(p, "innerRadius") * spacing, innerShare = Double(p, "innerShare");
            double away = Double(p, "minPortDistance") * spacing, apart = Double(p, "spawnSpacing") * radius;
            var ports = result.Facilities.Where(f => f.Open).ToList();
            int total = (int)Math.Round(Double(p, "perPortTown") * ports.Count, MidpointRounding.AwayFromZero);
            var byBasin = ports.GroupBy(f => f.Basin).OrderBy(g => g.Key).ToList();
            var share = new Dictionary<int, int>(); var remainders = new List<KeyValuePair<int, double>>();
            foreach (var g in byBasin) { double exact = ports.Count == 0 ? 0 : total * g.Count() / (double)ports.Count; share[g.Key] = (int)Math.Floor(exact); remainders.Add(new KeyValuePair<int, double>(g.Key, exact - Math.Floor(exact))); }
            foreach (var r in remainders.OrderByDescending(r => r.Value).ThenBy(r => r.Key).Take(Math.Max(0, total - share.Values.Sum()))) share[r.Key]++;
            var weights = Obj(p, "cultureWeights");
            var pooled = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var c in weights) foreach (var x in (IDictionary<string, object>)c.Value) pooled[x.Key] = Convert.ToInt32(x.Value, CultureInfo.InvariantCulture) + (pooled.ContainsKey(x.Key) ? (int)pooled[x.Key] : 0);
            int cols = land.Cols, rows = land.Rows;
            Func<int, bool> isOpenSea = i => {
                int r0 = i / cols, c0 = i % cols;
                for (int dr = -openSea; dr <= openSea; dr++)
                {
                    int r = r0 + dr; if (r < 0 || r >= rows) return false;
                    for (int dc = -openSea; dc <= openSea; dc++) if (classes[r * cols + ((c0 + dc) % cols + cols) % cols] != Sea) return false;
                }
                return true; };
            var clans = Obj(p, "clans");
            var pirates = clans.Keys.OrderBy(k => k, StringComparer.Ordinal).ToDictionary(k => k, k => new Pirate { Clan = k, Culture = Text(Obj(clans, k), "culture"), Template = Text(Obj(clans, k), "template") }, StringComparer.Ordinal);
            var placed = new List<SpawnPoint>();
            foreach (var g in byBasin)
            {
                var candidates = Enumerable.Range(0, classes.Length).Where(i => classes[i] == Sea)
                    .Select(i => new { I = i, X = (i % cols + 0.5) * land.Spacing, Y = (i / cols + 0.5) * land.Spacing })
                    .Where(c => basinAt(c.X, c.Y) == g.Key && isOpenSea(c.I) && ports.All(f => land.Distance(c.X, c.Y, f.PortX, f.PortY) >= away))
                    .OrderBy(c => Stream("pirates", "cell:" + c.I / cols + ":" + c.I % cols)).ThenBy(c => c.I).ToList();
                int made = 0;
                foreach (var c in candidates)
                {
                    if (made >= share[g.Key]) break;
                    if (placed.Any(q => land.Distance(c.X, c.Y, q.X, q.Y) < apart)) continue;
                    var nearest = g.OrderBy(f => land.Distance(c.X, c.Y, f.PortX, f.PortY)).ThenBy(f => f.Town, StringComparer.Ordinal).First();
                    string uid = "pirates:cell:" + (c.I / cols).ToString(CultureInfo.InvariantCulture) + ":" + (c.I % cols).ToString(CultureInfo.InvariantCulture);
                    var cw = Obj(weights, nearest.Culture ?? "") ?? pooled;
                    string clan = Weighted("pirates", "clan:" + uid, cw.Where(x => pirates.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value));
                    if (clan == null) continue;
                    var s = new SpawnPoint { Uid = uid, Clan = clan, X = c.X, Y = c.Y, Radius = radius, Basin = g.Key, NearestPort = nearest.Town, Zone = "outer" };
                    placed.Add(s); pirates[clan].SpawnPoints.Add(s); made++;
                    // The installed scene stacks a second, smaller zone of the same clan at most positions; PiratesCampaignBehavior keeps both.
                    if (Stream("pirates", "inner:" + uid) % 10000UL < (ulong)Math.Round(innerShare * 10000))
                        pirates[clan].SpawnPoints.Add(new SpawnPoint { Uid = uid + ":inner", Clan = clan, X = c.X, Y = c.Y, Radius = inner, Basin = g.Key, NearestPort = nearest.Town, Zone = "inner" });
                }
                if (made < share[g.Key]) result.Notes.Add("basin " + g.Key + " (" + g.Count() + " port towns) holds " + made + " of its " + share[g.Key] + " pirate spawn positions: no " + (made == 0 ? "" : "other ") + "open-sea cell is far enough from ports" + (made == 0 ? "" : " and spawn positions"));
            }
            var hideouts = startup == null ? new List<SettlementFactory.SettlementIntent>() : startup.Hideouts;
            foreach (var pirate in pirates.Values)
            {
                var record = Record(factionsCatalog, pirate.Clan);
                if (record == null) { Problem(CodeCatalog, "pirate clan " + pirate.Clan + " is not installed"); continue; }
                var template = Record(templatesCatalog, pirate.Template);
                if (template == null) { Problem(CodeCatalog, "pirate template " + pirate.Template + " is not installed"); continue; }
                var stacks = template.SelectNodes("ship_hulls/ShipTemplateStack").OfType<XmlElement>().Select(x => x.GetAttribute("id").Split('.').Last()).ToList();
                if (stacks.Count == 0) Problem(CodeCatalog, "pirate template " + pirate.Template + " has no ship hulls (PiratesCampaignBehavior would not treat the clan as pirates)");
                foreach (var h in stacks) HullResolves(h);
                var anchor = pirate.SpawnPoints.FirstOrDefault();
                var home = anchor == null ? hideouts.FirstOrDefault() : hideouts.OrderBy(h => land.Distance(anchor.X, anchor.Y, h.X, h.Y)).ThenBy(h => h.Id, StringComparer.Ordinal).FirstOrDefault();
                if (home == null) { Problem(CodeCatalog, "no generated hideout can be pirate clan " + pirate.Clan + "'s home"); continue; }
                pirate.Home = home.Id;
                if (pirate.SpawnPoints.Count == 0) result.Notes.Add("pirate clan " + pirate.Clan + " has no spawn point: it spawns nothing (NavalDLCBanditDensityModel caps it at its spawn points)");
                result.Pirates.Add(pirate);
            }
            // MMR-39: the runtime plan serves these points to War Sails (GeneratedNavalMapSceneWrapper) in place of PirateSpawnPoint entities.
            result.Plan.AddPirates(result.Pirates);
        }

        // The MMR-35 slots of the compiler's port records.
        private void Slots(Result result, IDictionary<string, object> campaign)
        {
            var byTown = result.Facilities.ToDictionary(f => f.Town, StringComparer.Ordinal);
            var landlocked = result.Plan.Landlocked;
            foreach (var p in Records(campaign, "ports"))
            {
                string town = Text(p, "town");
                foreach (var slot in Records(p, "slots").Where(s => Text(s, "owner") == "MMR-35"))
                {
                    Facility f;
                    if (!byTown.TryGetValue(town, out f)) { Problem(CodePort, "compiler port " + town + " has no naval facility (slot " + Text(slot, "name") + ")"); continue; }
                    string name = Text(slot, "name");
                    if (name == "navalFacility")
                        result.Slots.Add(new SlotValue { Town = town, Name = name, Value = "port " + Number(f.PortX) + "," + Number(f.PortY) + " on open basin " + f.Basin + ", facing " + Number(Math.Round(f.Orientation, 4)) +
                                                                                           " rad; port marker at the port; blockade arc " + Number(Math.Round(f.ArcStart[0], 3)) + "," + Number(Math.Round(f.ArcStart[1], 3)) + " to " + Number(Math.Round(f.ArcEnd[0], 3)) + "," + Number(Math.Round(f.ArcEnd[1], 3)) });
                    else if (name == "shipyardAndFleet")
                        result.Slots.Add(new SlotValue { Town = town, Name = name, Value = "building_shipyard level " + f.ShipyardLevel + " building " + String.Join(", ", f.Hulls) + "; fleets from the installed production and trade, for the clans of land group " + f.LandGroup });
                    else Problem(CodePolicy, "unknown MMR-35 slot " + name + " on " + town);
                }
            }
        }

        private void Documents(Result result)
        {
            var factionsDoc = new XmlDocument(); factionsDoc.AppendChild(factionsDoc.CreateElement("Factions"));
            foreach (var p in result.Pirates)
            {
                factionsDoc.DocumentElement.AppendChild(GeneratedCampaignInitializer.BanditClanRecord(factionsDoc, Record(factionsCatalog, p.Clan), p.Home));
                result.DeclaredKeys.Add("Faction." + p.Clan);
            }
            result.Documents.Add(new KeyValuePair<string, XmlDocument>("Factions", factionsDoc));
        }

        private void Problem(string code, string text)
        {
            var known = Obj(Obj(policy, "quality"), "codes");
            if (known == null || !known.ContainsKey(code)) { codes.Add(CodePolicy); problems.Add("naval-content.json has no quality code " + code); }
            codes.Add(code); problems.Add(text);
        }

        private void ThrowIfRefused()
        {
            if (problems.Count == 0) return;
            var known = Obj(Obj(policy, "quality"), "codes");
            bool retryable = codes.All(c => { var entry = Obj(known, c); object v; return entry != null && entry.TryGetValue("retryable", out v) && v is bool && (bool)v; });
            throw new NavalException(problems.Distinct().ToList(), codes.ToList()) { Retryable = retryable };
        }

        private ulong Stream(string stream, string entity)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.naval-stream/1\n" + stream + "\n" + Int(Obj(policy, "streams"), stream).ToString(CultureInfo.InvariantCulture) + "\n" + prefix + "\n" + entity + "\n0"));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        private string Weighted(string stream, string entity, IDictionary<string, object> weights)
        {
            var items = weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new KeyValuePair<string, int>(w.Key, Convert.ToInt32(w.Value, CultureInfo.InvariantCulture))).Where(w => w.Value > 0).ToList();
            int total = items.Sum(w => w.Value);
            if (total == 0) return null;
            int roll = (int)(Stream(stream, entity) % (ulong)total);
            foreach (var w in items) { if (roll < w.Value) return w.Key; roll -= w.Value; }
            return items.Last().Key;
        }

        private static string Canonical(Result r)
        {
            var o = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = "mnbl.naval-intents", ["version"] = 1, ["worldPrefix"] = r.WorldPrefix,
                ["facilities"] = r.Facilities.Select(f => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["town"] = f.Town, ["culture"] = f.Culture, ["basin"] = f.Basin, ["open"] = f.Open, ["landGroup"] = f.LandGroup, ["shipyardLevel"] = f.ShipyardLevel, ["quality"] = f.Quality,
                    ["port"] = new[] { f.PortX, f.PortY }, ["orientation"] = f.Orientation, ["arcStart"] = f.ArcStart, ["arcEnd"] = f.ArcEnd, ["arcRotation"] = f.ArcRotation, ["arcMirrored"] = f.ArcMirrored, ["arcProblem"] = f.ArcProblem, ["hulls"] = f.Hulls }).ToList(),
                ["landings"] = r.Landings.Select(l => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["village"] = l.Village, ["town"] = l.Town, ["port"] = new[] { l.PortX, l.PortY }, ["basin"] = l.Basin, ["open"] = l.Open, ["problem"] = l.Problem }).ToList(),
                ["roles"] = r.Roles.Select(x => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["id"] = x.Id, ["kind"] = x.Kind, ["naval"] = x.Naval, ["reason"] = x.Reason }).ToList(),
                ["pirates"] = r.Pirates.Select(p => new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["clan"] = p.Clan, ["home"] = p.Home, ["template"] = p.Template,
                    ["spawnPoints"] = p.SpawnPoints.Select(s => new SortedDictionary<string, object>(StringComparer.Ordinal) { ["uid"] = s.Uid, ["position"] = new[] { s.X, s.Y }, ["radius"] = s.Radius, ["zone"] = s.Zone, ["basin"] = s.Basin }).ToList() }).ToList(),
                ["diagnostics"] = r.Diagnostics.Select(d => d.Kind + " " + d.Subject + ": " + d.Reason).ToList(),
                ["landlocked"] = r.Plan.Landlocked.ToList(), ["notes"] = r.Notes };
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(o);
        }

        private static double Delta(GeneratedSpawnModel.CellMap map, double from, double to)
        {
            double d = to - from;
            if (d > map.Width / 2) d -= map.Width; else if (d < -map.Width / 2) d += map.Width;
            return d;
        }
        private static double Wrap(GeneratedSpawnModel.CellMap map, double x) { double w = map.Width; return ((x % w) + w) % w; }
        private static IEnumerable<XmlElement> Children(XmlElement e, string name) { var c = e.SelectSingleNode(name); return c == null ? Enumerable.Empty<XmlElement>() : c.ChildNodes.OfType<XmlElement>(); }
        private static XmlElement Record(XmlDocument doc, string id)
        {
            return doc == null || id == null ? null : doc.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("id") == id);
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
