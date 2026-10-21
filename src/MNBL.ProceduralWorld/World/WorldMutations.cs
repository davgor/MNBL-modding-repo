using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-261: the runtime landscape mutation, pure managed (docs/magic-ground.md "Live mutation"; findings section 7; owner decisions 2 and 7;
    // RT-37). The engine side is Campaign/WorldMutationBehavior.cs; MagicGroundRuntime (MMR-260) draws and retypes what this computes.
    //   * Trigger: MagicEvent {school, settlement, strength} (WorldMutations.Report, the console's mnbl_magic.cast). The daily batch adds each
    //     event's strength to its (settlement, school) residue; a residue at or above mutation.json residueThreshold emits one LeylineEdit and
    //     drops by the threshold. The edit's site (MutationSites, the capital rule): the FWGC++ capital of the event settlement's member realm
    //     (heroes.realms[].capital_uid, the realm in its kingdom's campaign.json memberRealms, MMR-279) while that kingdom still holds it, else the
    //     kingdom's ruling house's home, else the event settlement. A city site plants its key point by city_uid; any other by the direction of
    //     its source sample.
    //   * Compute: the producer regenerates the world from the bundle's source/request.json and replays the whole edit log (fwg apply,
    //     fwg.operations v1 in, fwg.ley_delta v1 out, MMR-250), on a background thread (MutationRunner), never while a load runs; the
    //     delta's cells whose biome_variant changed become the live cells, applied on the engine thread at a safe point (the whole set replaces
    //     the last: the delta is always against the freshly generated world). The operations are compacted: one shift with one new_node per
    //     key point at its current intensity (a node raised twice is planted once at the higher intensity, which is the same field).
    //   * Persist: MutationState, one canonical JSON string (one SyncData key): the producer build, the world, the request, the applied and
    //     pending edits, the live cells and nodes, the residue and a digest over all of it. A load replays the stored cells only (no producer).
    //     A digest that does not verify, or a document of another world, disables the live cells and every cast and keeps the document
    //     unchanged (the log is kept); the generated magic ground stays drawn (RT-37).
    //   * Producer updates (RT-37): the campaign casts with the build that generated its world (the bundle's source.producer.binarySha256),
    //     found in ProducerArchive by its hash; a cast is refused only when that build is missing; the stored cells load whatever is installed.
    internal struct MagicEvent
    {
        public string School, Settlement;   // a visible school (mutation.json schools); a settlement's StringId
        public double Strength;
        public MagicEvent(string school, string settlement, double strength) { School = school; Settlement = settlement; Strength = strength; }
    }

    // worldgen/policies/mutation.json (module-embedded; not a compiler input).
    internal sealed class MutationPolicy
    {
        public const string PolicyPath = "worldgen/policies/mutation.json";
        public List<string> Schools;
        public double ResidueThreshold, CastStrength;
        public int FirstIntensity, IntensityStep, MaxIntensity, MaxNodes, MaxPending, ApplyTimeoutSeconds;
        public string NodeIdPrefix;
        public string Sha256 { get; private set; }

        public static MutationPolicy Installed() { return FromBytes(StrictJson.Embedded("mutation.json")); }
        public static MutationPolicy FromBytes(byte[] bytes)
        {
            const string what = "mutation.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "notes", "schools", "residueThreshold", "castStrength", "firstIntensity", "intensityStep", "maxIntensity", "maxNodes", "maxPending",
                "nodeIdPrefix", "applyTimeoutSeconds", "cells");
            StrictJson.Header(doc, "mnbl.mutation", 1, what);
            var p = new MutationPolicy { Schools = StrictJson.Strings(doc, "schools", what), ResidueThreshold = StrictJson.Num(doc, "residueThreshold", what), CastStrength = StrictJson.Num(doc, "castStrength", what),
                FirstIntensity = StrictJson.Int(doc, "firstIntensity", what), IntensityStep = StrictJson.Int(doc, "intensityStep", what), MaxIntensity = StrictJson.Int(doc, "maxIntensity", what),
                MaxNodes = StrictJson.Int(doc, "maxNodes", what), MaxPending = StrictJson.Int(doc, "maxPending", what), NodeIdPrefix = StrictJson.Str(doc, "nodeIdPrefix", what),
                ApplyTimeoutSeconds = StrictJson.Int(doc, "applyTimeoutSeconds", what) };
            // FWGC++ (MMR-250): intensity 0..4, at most 128 edits in one shift; only the eight visible schools are cast (hidden ones are refused).
            StrictJson.Range(p.Schools.Count > 0 && p.Schools.All(MutationOperations.VisibleSchools.Contains) && p.Schools.Distinct().Count() == p.Schools.Count
                             && p.ResidueThreshold > 0 && p.CastStrength >= p.ResidueThreshold && p.FirstIntensity >= 1 && p.FirstIntensity <= 4 && p.IntensityStep >= 0
                             && p.MaxIntensity >= p.FirstIntensity && p.MaxIntensity <= 4 && p.MaxNodes >= 1 && p.MaxNodes <= 128 && p.MaxPending >= 1 && p.ApplyTimeoutSeconds > 0
                             && StrictJson.Str(doc, "cells", what) == "variantChanged", what);
            p.Sha256 = StrictJson.Sha(bytes);
            return p;
        }
    }

    // One LeylineEdit the campaign emitted: the event (day, school, site) and where it went (at, its rule, the city uid or the direction).
    internal sealed class MutationEdit
    {
        public int Day, Intensity;
        public string School, Site, At, Rule, City, Node;   // Rule: capital | rulingHouseHome | eventSite; City: a settlements.sites[] uid or null
        public double[] Direction;                          // null for a city site
        public double X, Y;                                 // the site's canonical physical point (the node's entity)
    }

    // The capital rule's tables (campaign.json, heroes.realms[], the travel graph's settlement points). Immutable.
    internal sealed class MutationSites
    {
        public readonly Dictionary<string, string> Town = new Dictionary<string, string>(StringComparer.Ordinal);          // settlement -> the town whose realm it belongs to
        public readonly Dictionary<string, string> Uid = new Dictionary<string, string>(StringComparer.Ordinal);           // settlement -> its source uid
        public readonly Dictionary<string, string> CityTown = new Dictionary<string, string>(StringComparer.Ordinal);      // FWGC++ city uid -> town
        public readonly Dictionary<string, string> RealmOfCity = new Dictionary<string, string>(StringComparer.Ordinal);   // city uid -> realm uid
        public readonly Dictionary<string, string> Capital = new Dictionary<string, string>(StringComparer.Ordinal);       // realm uid -> capital city uid
        public readonly Dictionary<string, string> Kingdom = new Dictionary<string, string>(StringComparer.Ordinal);       // realm uid -> campaign kingdom (memberRealms)
        public readonly Dictionary<string, double[]> Point = new Dictionary<string, double[]>(StringComparer.Ordinal);     // settlement -> canonical physical point
        public readonly HashSet<string> Cities = new HashSet<string>(StringComparer.Ordinal);                              // settlements.sites[] uids
        // MMR-247: settlements the campaign start places beside the travel graph's (hideouts, the retirement retreat), canonical physical. No
        // edit resolves to them; MMR-289's features keep clear of them as of Point's (WorldReconstruction fills it).
        public readonly List<double[]> Placed = new List<double[]>();

        public sealed class Target { public string At, Rule, City, Realm, Kingdom; public double X, Y; }

        // campaign: campaign.json; graph: travel-graph.json; realms: world.json heroes.realms[]; strip, poleMargin: the surface frame.
        public static MutationSites From(IDictionary<string, object> campaign, IDictionary<string, object> graph, IEnumerable<IDictionary<string, object>> realms, double strip, double poleMargin)
        {
            var s = new MutationSites();
            foreach (var t in Json.A(campaign, "towns").Cast<IDictionary<string, object>>()) { string id = Json.S(t, "id"), uid = Json.S(t, "uid"); s.Town[id] = id; s.Uid[id] = uid; if (uid != null) s.CityTown[uid] = id; }
            foreach (var c in Json.A(campaign, "castles").Cast<IDictionary<string, object>>()) { string id = Json.S(c, "id"); s.Uid[id] = Json.S(c, "uid"); string g = Json.S(c, "garrisonTown"); if (g != null) s.Town[id] = g; }
            foreach (var v in Json.A(campaign, "villages").Cast<IDictionary<string, object>>())
            {
                string id = Json.S(v, "id"), bound = Json.S(v, "bound"); s.Uid[id] = Json.S(v, "uid");
                string town; if (bound != null && s.Town.TryGetValue(bound, out town)) s.Town[id] = town;
            }
            foreach (var r in realms)
            {
                string uid = Json.S(r, "uid"), capital = Json.S(r, "capital_uid");
                if (uid == null || capital == null) continue;
                s.Capital[uid] = capital;
                foreach (var city in Json.A(r, "city_uids").OfType<string>()) s.RealmOfCity[city] = uid;
            }
            foreach (var k in Json.A(campaign, "kingdoms").Cast<IDictionary<string, object>>())
                foreach (var realm in Json.A(k, "memberRealms").OfType<string>()) s.Kingdom[realm] = Json.S(k, "id");
            foreach (var st in Json.A(graph, "settlements").Cast<IDictionary<string, object>>())
            {
                if (!st.ContainsKey("point")) continue;
                var p = Json.Point(st, "point"); s.Point[Json.S(st, "id")] = new[] { p[0] + strip, p[1] + poleMargin };
            }
            foreach (var uid in s.CityTown.Keys) s.Cities.Add(uid);
            return s;
        }

        // The site of an edit for an event at a settlement. ownerKingdom: a settlement -> the kingdom that holds it now (null: none);
        // rulingHome: a kingdom -> its ruling clan's home settlement now (null: none). Null when the settlement is unknown.
        public Target Resolve(string settlement, Func<string, string> ownerKingdom, Func<string, string> rulingHome)
        {
            if (settlement == null || !Point.ContainsKey(settlement)) return null;
            string town, uid, realm = null, capital = null, kingdom = null, capitalTown = null;
            if (Town.TryGetValue(settlement, out town) && Uid.TryGetValue(town, out uid) && uid != null && RealmOfCity.TryGetValue(uid, out realm))
            {
                Capital.TryGetValue(realm, out capital); Kingdom.TryGetValue(realm, out kingdom);
                if (capital != null) CityTown.TryGetValue(capital, out capitalTown);
            }
            if (capitalTown != null && kingdom != null && Point.ContainsKey(capitalTown) && ownerKingdom(capitalTown) == kingdom)
                return Make(capitalTown, "capital", realm, kingdom);
            string home = kingdom == null ? null : rulingHome(kingdom);
            if (home != null && Point.ContainsKey(home)) return Make(home, "rulingHouseHome", realm, kingdom);
            return Make(settlement, "eventSite", realm, kingdom);
        }

        private Target Make(string at, string rule, string realm, string kingdom)
        {
            string uid; Uid.TryGetValue(at, out uid);
            var p = Point[at];
            return new Target { At = at, Rule = rule, City = uid != null && Cities.Contains(uid) ? uid : null, Realm = realm, Kingdom = kingdom, X = p[0], Y = p[1] };
        }
    }

    // fwg.operations v1 from the edit log, and the direction of a source sample on FWGC++'s sphere.
    internal static class MutationOperations
    {
        public static readonly string[] VisibleSchools = { "weave", "umbral", "infernal", "radiant", "fire", "water", "earth", "air" };

        // world.json topology (FWGC++ sphere): row z from latitude +90 to -90, column x from longitude -180 to +180 (duplicate seam).
        public static double[] Direction(int row, int column, int side)
        {
            double lat = (90.0 - 180.0 * row / (side - 1)) * Math.PI / 180.0, lon = (-180.0 + 360.0 * column / (side - 1)) * Math.PI / 180.0;
            return new[] { Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), Math.Cos(lat) * Math.Sin(lon) };
        }

        // The key points the log plants (node id -> its first edit and its last intensity), in order of first planting.
        public static List<KeyValuePair<MutationEdit, int>> Nodes(IEnumerable<MutationEdit> edits)
        {
            var order = new List<string>(); var first = new Dictionary<string, MutationEdit>(StringComparer.Ordinal); var level = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in edits) { if (!first.ContainsKey(e.Node)) { first[e.Node] = e; order.Add(e.Node); } level[e.Node] = e.Intensity; }
            return order.Select(n => new KeyValuePair<MutationEdit, int>(first[n], level[n])).ToList();
        }

        // MMR-288: each node's veins grow from the batch day of its key point's first edit (campaign hours), so the stored edit log rebuilds the
        // reveal on a load. Nodes are matched to the key points in order (Applied makes them in that order).
        public static void Grown(IList<MagicNode> nodes, IEnumerable<MutationEdit> edits)
        {
            var points = Nodes(edits);
            for (int i = 0; i < nodes.Count && i < points.Count; i++)
            {
                var e = points[i].Key;
                if (nodes[i].School == e.School && nodes[i].X == e.X && nodes[i].Y == e.Y) nodes[i].Since = e.Day * 24.0;
            }
        }

        // One shift operation planting every key point at its current intensity (FWGC++ replays it on the freshly generated world).
        public static string Document(IEnumerable<MutationEdit> edits)
        {
            var b = new StringBuilder("{\"schema\":\"fwg.operations\",\"version\":1,\"operations\":[{\"kind\":\"shift leylines\",\"api_version\":1,\"leyline_edits\":[");
            bool firstEdit = true;
            foreach (var kv in Nodes(edits))
            {
                var e = kv.Key;
                if (!firstEdit) b.Append(','); firstEdit = false;
                b.Append("{\"school\":"); MutationJson.Str(b, e.School); b.Append(",\"new_node\":{\"id\":"); MutationJson.Str(b, e.Node);
                b.Append(",\"intensity\":").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                if (e.City != null) { b.Append(",\"city_uid\":"); MutationJson.Str(b, e.City); }
                else b.Append(",\"direction\":[").Append(String.Join(",", e.Direction.Select(MutationJson.F))).Append(']');
                b.Append("}}");
            }
            return b.Append("]}]}").ToString();
        }
    }

    // fwg.ley_delta v1 (MMR-250) -> the live cells: every listed cell whose biome_variant changed, with the new variant's school's potency.
    internal static class MutationDelta
    {
        public sealed class Result { public List<MutationCell> Cells = new List<MutationCell>(); public int Listed, Changed, Cleared; public string Sha256; }

        // Throws InvalidDataException when the delta is not this world's (seed/size/schools, or a cell's before differs from the generated layer).
        public static Result Read(string text, MagicGroundSource source, long seed)
        {
            var doc = MutationJson.Parse(text) as Dictionary<string, object>;
            if (doc == null || MutationJson.Text(doc, "schema") != "fwg.ley_delta" || MutationJson.Int(doc, "version") != 1) throw new InvalidDataException("not an fwg.ley_delta v1 document");
            if (MutationJson.Int(doc, "size") != source.Side) throw new InvalidDataException("the delta is for size " + MutationJson.Int(doc, "size") + ", the world has " + source.Side + " samples a side");
            if (MutationJson.Long(doc, "seed") != seed) throw new InvalidDataException("the delta is for seed " + MutationJson.Long(doc, "seed") + ", not the world's " + seed);
            var schools = ((List<object>)doc["schools"]).Cast<string>().ToList();
            var r = new Result { Sha256 = Json.Sha(text) };
            foreach (Dictionary<string, object> cell in (List<object>)doc["cells"])
            {
                r.Listed++;
                int x = MutationJson.Int(cell, "x"), z = MutationJson.Int(cell, "z");
                if (x < 0 || z < 0 || x >= source.Side || z >= source.Side) throw new InvalidDataException("delta cell " + x + "," + z + " is outside the grid");
                var before = (Dictionary<string, object>)cell["before"]; var after = (Dictionary<string, object>)cell["after"];
                int was = MutationJson.Int(before, "biome_variant"), now = MutationJson.Int(after, "biome_variant");
                int generated = source.Enabled ? source.Variant[z * source.Side + x] : -1;
                if (was != generated) throw new InvalidDataException("delta cell " + x + "," + z + " starts from variant " + was + ", the world's is " + generated + " (another producer build or world)");
                if (now == was) continue;
                if (now >= source.Catalogue.Length) throw new InvalidDataException("delta cell " + x + "," + z + " names variant " + now + ", not a magical biome");
                float potency = Single.NaN;
                if (now >= 0)
                {
                    int school = schools.IndexOf(source.Catalogue[now].School);
                    var p = (List<object>)after["potency"];
                    if (school >= 0 && school < p.Count) potency = (float)MutationJson.Num(p[school]);
                }
                else r.Cleared++;
                r.Changed++;
                r.Cells.Add(new MutationCell(z, x, now, potency));
            }
            return r;
        }
    }

    // The campaign's mutation document (one SyncData string). Canonical: fixed member order, no whitespace, integers plainly, doubles G17 and
    // floats G9 (both round-trip), NaN as null; the digest is the SHA-256 of the document without its digest member.
    internal sealed class MutationState
    {
        public const string Schema = "mnbl.world-mutations";
        public const int Version = 1;
        public string Producer, World, Request;   // the producer build, world.json and request.json SHA-256
        public List<MutationEdit> Edits = new List<MutationEdit>(), Pending = new List<MutationEdit>();
        public List<MutationCell> Cells = new List<MutationCell>();
        public List<MagicNode> Nodes = new List<MagicNode>();
        public SortedDictionary<string, double> Residue = new SortedDictionary<string, double>(StringComparer.Ordinal);   // "school|settlement"
        public int Day = -1;   // the last batch's day

        public string Body()
        {
            var b = new StringBuilder("{\"schema\":"); MutationJson.Str(b, Schema); b.Append(",\"version\":").Append(Version);
            b.Append(",\"producer\":"); MutationJson.Str(b, Producer); b.Append(",\"world\":"); MutationJson.Str(b, World); b.Append(",\"request\":"); MutationJson.Str(b, Request);
            b.Append(",\"day\":").Append(Day.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"edits\":["); Write(b, Edits); b.Append("],\"pending\":["); Write(b, Pending);
            b.Append("],\"cells\":[").Append(String.Join(",", Cells.Select(c => "[" + c.Row.ToString(CultureInfo.InvariantCulture) + "," + c.Column.ToString(CultureInfo.InvariantCulture) + "," +
                c.Variant.ToString(CultureInfo.InvariantCulture) + "," + MutationJson.F(c.Potency) + "]")));
            b.Append("],\"nodes\":[");
            for (int i = 0; i < Nodes.Count; i++) { if (i > 0) b.Append(','); b.Append("{\"school\":"); MutationJson.Str(b, Nodes[i].School); b.Append(",\"x\":").Append(MutationJson.F(Nodes[i].X)).Append(",\"y\":").Append(MutationJson.F(Nodes[i].Y)).Append('}'); }
            b.Append("],\"residue\":[");
            int n = 0;
            foreach (var kv in Residue) { if (n++ > 0) b.Append(','); b.Append("{\"key\":"); MutationJson.Str(b, kv.Key); b.Append(",\"value\":").Append(MutationJson.F(kv.Value)).Append('}'); }
            return b.Append("]}").ToString();
        }
        public string Digest() { return Json.Sha(Body()); }
        public string ToJson() { string body = Body(); return body.Substring(0, body.Length - 1) + ",\"digest\":\"" + Json.Sha(body) + "\"}"; }

        private static void Write(StringBuilder b, List<MutationEdit> edits)
        {
            for (int i = 0; i < edits.Count; i++)
            {
                var e = edits[i];
                if (i > 0) b.Append(',');
                b.Append("{\"day\":").Append(e.Day.ToString(CultureInfo.InvariantCulture)).Append(",\"school\":"); MutationJson.Str(b, e.School);
                b.Append(",\"site\":"); MutationJson.Str(b, e.Site); b.Append(",\"at\":"); MutationJson.Str(b, e.At); b.Append(",\"rule\":"); MutationJson.Str(b, e.Rule);
                b.Append(",\"node\":"); MutationJson.Str(b, e.Node); b.Append(",\"intensity\":").Append(e.Intensity.ToString(CultureInfo.InvariantCulture));
                b.Append(",\"city\":"); MutationJson.Str(b, e.City);
                b.Append(",\"direction\":"); if (e.Direction == null) b.Append("null"); else b.Append('[').Append(String.Join(",", e.Direction.Select(MutationJson.F))).Append(']');
                b.Append(",\"x\":").Append(MutationJson.F(e.X)).Append(",\"y\":").Append(MutationJson.F(e.Y)).Append('}');
            }
        }

        // A stored document. digestOk: the stored digest is the document's own. Throws InvalidDataException on a malformed document.
        public static MutationState Parse(string text, out bool digestOk)
        {
            var d = MutationJson.Parse(text) as Dictionary<string, object>;
            if (d == null || MutationJson.Text(d, "schema") != Schema || MutationJson.Int(d, "version") != Version) throw new InvalidDataException("not an " + Schema + " v" + Version + " document");
            var s = new MutationState { Producer = MutationJson.Text(d, "producer"), World = MutationJson.Text(d, "world"), Request = MutationJson.Text(d, "request"), Day = MutationJson.Int(d, "day") };
            s.Edits = ReadEdits(d["edits"]); s.Pending = ReadEdits(d["pending"]);
            foreach (List<object> c in (List<object>)d["cells"])
                s.Cells.Add(new MutationCell(MutationJson.Int(c[0]), MutationJson.Int(c[1]), MutationJson.Int(c[2]), c[3] == null ? Single.NaN : (float)MutationJson.Num(c[3])));
            foreach (Dictionary<string, object> n in (List<object>)d["nodes"]) s.Nodes.Add(new MagicNode(MutationJson.Text(n, "school"), MutationJson.Num(n["x"]), MutationJson.Num(n["y"])));
            foreach (Dictionary<string, object> r in (List<object>)d["residue"]) s.Residue[MutationJson.Text(r, "key")] = MutationJson.Num(r["value"]);
            digestOk = MutationJson.Text(d, "digest") == s.Digest();
            return s;
        }

        private static List<MutationEdit> ReadEdits(object list)
        {
            return ((List<object>)list).Cast<Dictionary<string, object>>().Select(e => new MutationEdit {
                Day = MutationJson.Int(e, "day"), School = MutationJson.Text(e, "school"), Site = MutationJson.Text(e, "site"), At = MutationJson.Text(e, "at"), Rule = MutationJson.Text(e, "rule"),
                Node = MutationJson.Text(e, "node"), Intensity = MutationJson.Int(e, "intensity"), City = MutationJson.Text(e, "city"),
                Direction = e["direction"] == null ? null : ((List<object>)e["direction"]).Select(MutationJson.Num).ToArray(), X = MutationJson.Num(e["x"]), Y = MutationJson.Num(e["y"]) }).ToList();
        }
    }

    // RT-37: the producer builds the package keeps (worldgen/producer-archive.json; Producers/<sha256>/fwg.exe under the module folder).
    internal sealed class ProducerArchive
    {
        public const string PolicyPath = "worldgen/producer-archive.json";
        public sealed class Build { public string Sha256, SourceCommit, Path; public List<string> Apply; }
        public readonly List<Build> Builds = new List<Build>();

        public static ProducerArchive Installed() { return FromBytes(StrictJson.Embedded("producer-archive.json")); }
        public static ProducerArchive FromBytes(byte[] bytes)
        {
            const string what = "producer-archive.json";
            var doc = StrictJson.Parse(bytes);
            StrictJson.Only(doc, what, "schema", "version", "rule", "builds");
            StrictJson.Header(doc, "mnbl.producer-archive", 1, what);
            var a = new ProducerArchive();
            foreach (var o in StrictJson.Arr(doc, "builds", what))
            {
                var e = o as IDictionary<string, object>;
                if (e == null) throw new InvalidDataException(what + " builds[] holds a non-object.");
                StrictJson.Only(e, what + " build", "sha256", "sourceCommit", "sourceCommitDate", "since", "path", "apply");
                var b = new Build { Sha256 = StrictJson.Str(e, "sha256", what), SourceCommit = StrictJson.Str(e, "sourceCommit", what), Path = StrictJson.Str(e, "path", what), Apply = StrictJson.Strings(e, "apply", what) };
                StrictJson.Range(b.Sha256.Length == 64 && b.Sha256.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) && b.Path == "Producers/" + b.Sha256 + "/fwg.exe"
                                 && new[] { "{request}", "{operations}", "{delta}" }.All(b.Apply.Contains) && a.Builds.All(x => x.Sha256 != b.Sha256), what + " build " + b.Sha256);
                a.Builds.Add(b);
            }
            return a;
        }

        // The build of a campaign: its executable under moduleRoot when it is listed, present and its bytes hash to sha256; else null and why.
        public Build Resolve(string sha256, string moduleRoot, Func<string, bool> exists, Func<string, string> hash, out string path, out string refusal)
        {
            path = null; refusal = null;
            var b = Builds.FirstOrDefault(x => x.Sha256 == sha256);
            if (b == null) { refusal = "the producer build " + Short(sha256) + " this campaign's world was generated with is not in this version's archive"; return null; }
            string candidate = System.IO.Path.Combine(moduleRoot, b.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!exists(candidate)) { refusal = "the producer build " + Short(sha256) + " is missing from the module (" + b.Path + ")"; return null; }
            if (hash(candidate) != sha256) { refusal = "the archived producer " + b.Path + " has other bytes"; return null; }
            path = candidate;
            return b;
        }
        private static string Short(string sha) { return sha == null ? "(none)" : sha.Length > 12 ? sha.Substring(0, 12) : sha; }
    }

    // One fwg apply job and its outcome.
    internal sealed class ApplyJob { public string Producer, ProducerSha256, Request, Operations, Directory; public List<string> Arguments; public int TimeoutSeconds; public int Edits; }
    internal sealed class ApplyOutcome
    {
        public ApplyJob Job; public bool Succeeded, Cancelled; public string Delta, Error; public int ExitCode; public double Seconds;

        // MMR-262: how a finished fwg apply reads (the launcher adds the delta, or the failure's detail to Error). Only the caller's token
        // cancels (the world's release, a load beginning); the job's own timeout is a failure like a non-zero exit, so Applied holds its
        // edits until the next daily batch instead of relaunching at the next tick.
        public static ApplyOutcome Finished(ApplyJob job, int exit, bool cancelled, bool timedOut, bool wroteDelta)
        {
            var o = new ApplyOutcome { Job = job, ExitCode = exit, Cancelled = cancelled, Succeeded = !cancelled && exit == 0 && wroteDelta };
            if (!o.Succeeded) o.Error = cancelled ? "cancelled" : timedOut ? "timed out after " + job.TimeoutSeconds + " s" : "exit " + exit;
            return o;
        }
    }

    // fwg apply on a background thread. Never starts while a load runs (the loading predicate: the in-game one is GeneratedWorldLoader.Busy
    // or a load manager's stage); one job at a time; the outcome waits for the engine thread (Take); Cancel kills the owned process and
    // drops the outcome (world release, a load beginning).
    internal sealed class MutationRunner
    {
        private readonly Func<bool> loading;
        private readonly Func<ApplyJob, CancellationToken, ApplyOutcome> launch;
        private readonly object gate = new object();
        private CancellationTokenSource running;
        private ApplyOutcome done;
        private int generation;
        public int Started { get; private set; }
        public int RefusedDuringLoad { get; private set; }
        public bool Busy { get { lock (gate) return running != null; } }

        public MutationRunner(Func<bool> loading, Func<ApplyJob, CancellationToken, ApplyOutcome> launch) { this.loading = loading; this.launch = launch; }

        public bool TryStart(ApplyJob job, out string refusal)
        {
            lock (gate)
            {
                refusal = null;
                if (loading()) { RefusedDuringLoad++; refusal = "a load is running: no generator process starts during a load"; return false; }
                if (running != null) { refusal = "an fwg apply is already running"; return false; }
                if (done != null) { refusal = "an outcome waits for the engine thread"; return false; }
                var cts = new CancellationTokenSource(); running = cts; int mine = ++generation; Started++;
                Task.Run(() =>
                {
                    ApplyOutcome outcome;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        // Checked again on the worker, just before the process starts.
                        if (loading()) outcome = new ApplyOutcome { Job = job, Cancelled = true, Error = "a load began before the producer started" };
                        else outcome = launch(job, cts.Token) ?? new ApplyOutcome { Job = job, Error = "the launcher returned nothing" };
                    }
                    catch (OperationCanceledException) { outcome = new ApplyOutcome { Job = job, Cancelled = true, Error = "cancelled" }; }
                    catch (Exception error) { outcome = new ApplyOutcome { Job = job, Error = error.GetType().Name + ": " + error.Message }; }
                    outcome.Seconds = watch.Elapsed.TotalSeconds;
                    bool dropped;
                    lock (gate)
                    {
                        dropped = mine != generation;   // cancelled meanwhile: dropped
                        if (!dropped) { running = null; done = outcome; }
                    }
                    if (dropped) Remove(job);   // MMR-262: it never reaches Applied; launch has returned, so its process has exited
                });
                return true;
            }
        }

        // The finished outcome, once (the engine thread's safe point).
        public ApplyOutcome Take() { lock (gate) { var o = done; done = null; return o; } }

        // The running job's folder goes when its worker sees the cancel (after its process exits); a finished outcome's folder goes here.
        public void Cancel()
        {
            ApplyOutcome finished;
            lock (gate)
            {
                generation++;
                if (running != null) { try { running.Cancel(); } catch (ObjectDisposedException) { } running = null; }
                finished = done; done = null;
            }
            if (finished != null) Remove(finished.Job);
        }

        // A job's work folder (<store>/mutations/apply-*): request, operations, delta, logs and the process receipt.
        public static void Remove(ApplyJob job)
        {
            try { if (job != null && job.Directory != null && Directory.Exists(job.Directory)) Directory.Delete(job.Directory, true); } catch (Exception) { }
        }
    }

    // The campaign's mutation model: residue, edits, the producer job and the state document. The engine side calls Report, DailyBatch and
    // Pump (casts) and Applied (an outcome at a safe point); everything here is deterministic over its inputs.
    internal sealed class MutationModel
    {
        public sealed class Context
        {
            public MagicGroundSource Source; public MutationSites Sites; public MutationPolicy Policy; public ProducerArchive Archive;
            public byte[] Request; public string RequestSha256, WorldSha256, ProducerSha256, ModuleRoot, WorkRoot; public long Seed;
            public Func<string, bool> Exists = File.Exists; public Func<string, string> Hash;
        }
        public readonly Context World;
        public MutationState State { get; private set; }
        public string Stored { get; private set; }            // the loaded document, kept verbatim while mutations are disabled
        public string DisabledReason { get; private set; }    // RT-37: a digest mismatch or another world's document; casts blocked
        public string LastError { get; private set; }
        public MutationDelta.Result LastDelta { get; private set; }
        public readonly List<MagicEvent> Today = new List<MagicEvent>();
        public readonly List<string> Log = new List<string>();
        private ApplyJob inFlight;
        private bool heldUntilBatch;   // a failed fwg apply is retried at the next daily batch, not at every tick

        public MutationModel(Context world)
        {
            World = world;
            State = new MutationState { Producer = world.ProducerSha256, World = world.WorldSha256, Request = world.RequestSha256 };
        }

        // ---- persistence
        public string Save() { return DisabledReason != null && Stored != null ? Stored : State.ToJson(); }

        // A load's document (null or empty: a campaign without mutations). Returns the cells and nodes to replay, or null when disabled.
        public bool Load(string text, out List<MutationCell> cells, out List<MagicNode> nodes)
        {
            cells = null; nodes = null; Stored = null; DisabledReason = null;
            if (String.IsNullOrEmpty(text)) return true;
            Stored = text;
            bool digestOk; MutationState s;
            try { s = MutationState.Parse(text, out digestOk); }
            catch (Exception error) { DisabledReason = "the stored mutation document does not read (" + error.Message + ")"; return false; }
            if (!digestOk) DisabledReason = "the stored mutation digest does not match its cells";
            else if (s.World != World.WorldSha256) DisabledReason = "the stored mutations belong to another world (" + s.World + ")";
            else if (s.Request != World.RequestSha256) DisabledReason = "the stored mutations were computed from another request";
            if (DisabledReason != null) return false;
            State = s; Stored = null;
            cells = s.Cells; nodes = s.Nodes;
            MutationOperations.Grown(nodes, s.Edits);
            return true;
        }

        // ---- casts
        public string CastRefusal()
        {
            if (DisabledReason != null) return "live mutations are disabled for this campaign: " + DisabledReason;
            if (!World.Source.Enabled) return "the world has no magic layers (a magic-off world)";
            string path;
            return Producer(out path);
        }

        // The campaign's recorded build, resolved once per recorded hash (hashing the executable is not free).
        private string resolvedFor, resolvedPath, resolvedRefusal;
        private string Producer(out string path)
        {
            if (resolvedFor != State.Producer)
            {
                World.Archive.Resolve(State.Producer, World.ModuleRoot, World.Exists, World.Hash, out resolvedPath, out resolvedRefusal);
                resolvedFor = State.Producer;
            }
            path = resolvedPath;
            return resolvedRefusal;
        }

        // A magic event, counted at the next daily batch. Returns null, or why it is refused.
        public string Report(MagicEvent e)
        {
            if (e.School == null || !World.Policy.Schools.Contains(e.School)) return "school " + (e.School ?? "(none)") + " is not one of " + String.Join(", ", World.Policy.Schools);
            if (e.Settlement == null || !World.Sites.Point.ContainsKey(e.Settlement)) return "settlement " + (e.Settlement ?? "(none)") + " is not one of this world's";
            if (!(e.Strength > 0) || Double.IsInfinity(e.Strength)) return "strength must be a positive number";
            string refusal = CastRefusal();
            if (refusal != null) return refusal;
            Today.Add(e);
            return null;
        }

        // The daily batch: residue per (school, settlement); each crossing emits one edit (pending until its delta is applied).
        public List<MutationEdit> DailyBatch(int day, Func<string, string> ownerKingdom, Func<string, string> rulingHome)
        {
            var emitted = new List<MutationEdit>();
            var events = Today.ToList(); Today.Clear();
            heldUntilBatch = false;
            State.Day = day;
            if (CastRefusal() != null) return emitted;
            foreach (var e in events)
            {
                string key = e.School + "|" + e.Settlement; double r; State.Residue.TryGetValue(key, out r); State.Residue[key] = r + e.Strength;
            }
            foreach (var key in State.Residue.Keys.ToList())
            {
                while (State.Residue[key] >= World.Policy.ResidueThreshold)
                {
                    var split = key.Split('|');
                    var edit = Edit(day, split[0], split[1], ownerKingdom, rulingHome);
                    if (edit == null) break;
                    State.Residue[key] -= World.Policy.ResidueThreshold;
                    State.Pending.Add(edit); emitted.Add(edit);
                    Log.Add("edit " + edit.School + " at " + edit.At + " (" + edit.Rule + ", from " + edit.Site + ") node " + edit.Node + " intensity " + edit.Intensity);
                }
                if (State.Residue[key] <= 0) State.Residue.Remove(key);
            }
            return emitted;
        }

        private MutationEdit Edit(int day, string school, string site, Func<string, string> ownerKingdom, Func<string, string> rulingHome)
        {
            var target = World.Sites.Resolve(site, ownerKingdom, rulingHome);
            if (target == null) return null;
            string node = World.Policy.NodeIdPrefix + "-" + school + "-" + target.At;
            var all = State.Edits.Concat(State.Pending).ToList();
            var prior = all.LastOrDefault(e => e.Node == node);
            if (prior == null && MutationOperations.Nodes(all).Count >= World.Policy.MaxNodes) { LastError = "the key point cap (" + World.Policy.MaxNodes + ") is reached"; return null; }
            if (State.Pending.Count >= World.Policy.MaxPending) { LastError = "the pending queue is full (" + World.Policy.MaxPending + ")"; return null; }
            int intensity = prior == null ? World.Policy.FirstIntensity : Math.Min(World.Policy.MaxIntensity, prior.Intensity + World.Policy.IntensityStep);
            double[] direction = null;
            if (target.City == null)
            {
                int sample = World.Source.SampleAt(target.X, target.Y);
                direction = MutationOperations.Direction(sample / World.Source.Side, sample % World.Source.Side, World.Source.Side);
            }
            return new MutationEdit { Day = day, School = school, Site = site, At = target.At, Rule = target.Rule, City = target.City, Direction = direction, Node = node, Intensity = intensity, X = target.X, Y = target.Y };
        }

        // Starts fwg apply for the whole log (applied + pending) when something is pending and nothing runs. Returns null, or why not.
        public string Pump(MutationRunner runner)
        {
            if (State.Pending.Count == 0) return "nothing pending";
            if (heldUntilBatch) return "the last fwg apply failed; it is retried after the next daily batch";
            string refusal = CastRefusal();
            if (refusal != null) return refusal;
            if (inFlight != null || runner.Busy) return "an fwg apply is already running";
            string path; refusal = Producer(out path);
            if (refusal != null) return refusal;
            var build = World.Archive.Builds.First(b => b.Sha256 == State.Producer);
            var all = State.Edits.Concat(State.Pending).ToList();
            string dir = Path.Combine(World.WorkRoot, "apply-" + State.Day.ToString(CultureInfo.InvariantCulture) + "-" + all.Count.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var job = new ApplyJob { Producer = path, ProducerSha256 = State.Producer, Directory = dir, Request = Path.Combine(dir, "request.json"), Operations = Path.Combine(dir, "operations.json"),
                TimeoutSeconds = World.Policy.ApplyTimeoutSeconds, Edits = all.Count };
            job.Arguments = build.Apply.Select(a => a.Replace("{request}", job.Request).Replace("{operations}", job.Operations).Replace("{delta}", Path.Combine(dir, "delta.json"))).ToList();
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(job.Request, World.Request);
            File.WriteAllText(job.Operations, MutationOperations.Document(all), new UTF8Encoding(false));
            string why;
            if (!runner.TryStart(job, out why)) { try { Directory.Delete(dir, true); } catch (Exception) { } return why; }
            inFlight = job;
            Log.Add("fwg apply started: " + all.Count + " edits, " + MutationOperations.Nodes(all).Count + " key points");
            return null;
        }

        // An outcome at the engine thread's safe point. apply: the overlay's replace of the whole live state (MagicGroundRuntime.Set), which
        // throws to refuse. Returns the cells applied, or null (the pending edits stay pending after a failure, and drop after a refusal).
        public List<MutationCell> Applied(ApplyOutcome outcome, Action<List<MutationCell>, List<MagicNode>> apply)
        {
            if (outcome == null) return null;
            MutationRunner.Remove(outcome.Job);
            if (!ReferenceEquals(outcome.Job, inFlight)) return null;   // a job of before a cancel
            inFlight = null;
            if (!outcome.Succeeded) { LastError = outcome.Cancelled ? "cancelled: " + outcome.Error : "fwg apply failed: " + outcome.Error; Log.Add(LastError); heldUntilBatch = !outcome.Cancelled; return null; }
            int applying = outcome.Job.Edits - State.Edits.Count;
            if (applying <= 0 || applying > State.Pending.Count) { LastError = "the outcome does not match the edit log"; return null; }
            MutationDelta.Result delta;
            try { delta = MutationDelta.Read(outcome.Delta, World.Source, World.Seed); }
            catch (Exception error) { LastError = "the delta was refused: " + error.Message; Log.Add(LastError); State.Pending.RemoveRange(0, applying); return null; }
            var taken = State.Pending.Take(applying).ToList();
            var edits = State.Edits.Concat(taken).ToList();
            var nodes = MutationOperations.Nodes(edits).Select(kv => new MagicNode(kv.Key.School, kv.Key.X, kv.Key.Y)).ToList();
            MutationOperations.Grown(nodes, edits);
            try { apply(delta.Cells, nodes); }
            catch (Exception error) { LastError = "the overlay refused the cells: " + error.Message; Log.Add(LastError); State.Pending.RemoveRange(0, applying); return null; }
            State.Pending.RemoveRange(0, applying);
            State.Edits = edits; State.Cells = delta.Cells; State.Nodes = nodes; LastDelta = delta;
            Log.Add("applied: " + delta.Changed + " cells (" + delta.Listed + " listed) after " + outcome.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            return delta.Cells;
        }

        // A world release or a load beginning: the running job is dropped (its edits stay pending in the document).
        public void Cancel(MutationRunner runner) { runner.Cancel(); inFlight = null; }
    }

    // A small JSON reader that keeps numbers as their text (so a stored document's doubles read back bit for bit) and the writer helpers.
    internal static class MutationJson
    {
        public sealed class Number { public readonly string Text; public Number(string text) { Text = text; } }

        public static string F(double v) { return Double.IsNaN(v) || Double.IsInfinity(v) ? "null" : v.ToString("G17", CultureInfo.InvariantCulture); }
        public static string F(float v) { return Single.IsNaN(v) || Single.IsInfinity(v) ? "null" : v.ToString("G9", CultureInfo.InvariantCulture); }
        public static void Str(StringBuilder b, string s)
        {
            if (s == null) { b.Append("null"); return; }
            b.Append('"');
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\""); else if (c == '\\') b.Append("\\\\");
                else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            b.Append('"');
        }

        public static string Text(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v as string : null; }
        public static int Int(Dictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v)) throw new InvalidDataException("lacks " + key); return Int(v); }
        public static long Long(Dictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v) || !(v is Number)) throw new InvalidDataException("lacks " + key); return Int64.Parse(((Number)v).Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }
        public static int Int(object v) { var n = v as Number; if (n == null) throw new InvalidDataException("not a number"); return Int32.Parse(n.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }
        public static double Num(object v) { var n = v as Number; if (n == null) throw new InvalidDataException("not a number"); return System.Double.Parse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture); }

        public static object Parse(string text)
        {
            int i = 0;
            var value = Value(text, ref i);
            Space(text, ref i);
            if (i != text.Length) throw new InvalidDataException("content after the JSON value");
            return value;
        }
        private static void Space(string t, ref int i) { while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++; }
        private static object Value(string t, ref int i)
        {
            Space(t, ref i);
            if (i >= t.Length) throw new InvalidDataException("unexpected end of JSON");
            char c = t[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal); i++; Space(t, ref i);
                if (i < t.Length && t[i] == '}') { i++; return d; }
                while (true)
                {
                    Space(t, ref i); string key = Quoted(t, ref i); Space(t, ref i);
                    if (i >= t.Length || t[i] != ':') throw new InvalidDataException("expected ':'");
                    i++; d[key] = Value(t, ref i); Space(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == '}') { i++; return d; }
                    throw new InvalidDataException("expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Space(t, ref i);
                if (i < t.Length && t[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(t, ref i)); Space(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == ']') { i++; return l; }
                    throw new InvalidDataException("expected ',' or ']'");
                }
            }
            if (c == '"') return Quoted(t, ref i);
            if (t.Length - i >= 4 && String.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (t.Length - i >= 5 && String.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (t.Length - i >= 4 && String.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (i == start) throw new InvalidDataException("unexpected character '" + c + "'");
            return new Number(t.Substring(start, i - start));
        }
        private static string Quoted(string t, ref int i)
        {
            if (i >= t.Length || t[i] != '"') throw new InvalidDataException("expected a string");
            var b = new StringBuilder(); i++;
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\')
                {
                    i++;
                    if (i >= t.Length) break;
                    char e = t[i];
                    if (e == 'u') { b.Append((char)Convert.ToInt32(t.Substring(i + 1, 4), 16)); i += 4; }
                    else b.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : e);
                }
                else b.Append(t[i]);
                i++;
            }
            if (i >= t.Length) throw new InvalidDataException("unterminated string");
            i++;
            return b.ToString();
        }
    }
}
