using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-38: the campaign navmesh of a compiled world (docs/runtime-navigation.md). Pure managed (no native call), tested offline.
    //   * NavigationInput: the world's MMR-28 travel cells and graph (cells.bin, travel-graph.json), the MMR-27 village bindings and the
    //     bundle's playability policy, whose cells.faceGroups name the installed TerrainType of each cell class.
    //   * RuntimeNavigationBuilder: one quad per physical cell between the pole lines, over every physical column (the periodic strips repeat
    //     the opposite side), grounded on the collision reference; typed faces (domain, water class, terrain cost and passage are kept per
    //     cell, separately); bridge decks over under-bridge faces for every joining crossing; encoded as the scene's navmesh.bin (RNM1,
    //     NMG9).
    // Face indices are this mesh's; the engine's are session-local and never persisted (NavigationValidator reports by source id).
    internal sealed class NavigationDiagnostic
    {
        public NavigationDiagnostic(string code, string subject, string detail, params string[] sourceIds)
        { Code = code; Subject = subject; Detail = detail; SourceIds = (sourceIds ?? new string[0]).Where(s => !String.IsNullOrEmpty(s)).Distinct().ToArray(); }
        public string Code { get; private set; }
        public string Subject { get; private set; }
        public string Detail { get; private set; }
        public string[] SourceIds { get; private set; }
        public Dictionary<string, object> Record() { return new Dictionary<string, object> { ["code"] = Code, ["subject"] = Subject, ["detail"] = Detail, ["sourceIds"] = SourceIds }; }
        public override string ToString() { return Code + " " + Subject + ": " + Detail + (SourceIds.Length > 0 ? " [" + String.Join(", ", SourceIds) + "]" : ""); }
    }

    // Activation is refused: the diagnostics name the source ids involved.
    internal sealed class NavigationRefusedException : InvalidOperationException
    {
        public NavigationRefusedException(string what, IEnumerable<NavigationDiagnostic> diagnostics)
            : base(Describe(what, diagnostics.ToList())) { Diagnostics = diagnostics.ToList().AsReadOnly(); }
        public IList<NavigationDiagnostic> Diagnostics { get; private set; }
        private static string Describe(string what, List<NavigationDiagnostic> d)
        {
            return what + " refused (" + d.Count + " navigation diagnostic" + (d.Count == 1 ? "" : "s") + "): " + String.Join("; ", d.Take(6).Select(x => x.ToString())) + (d.Count > 6 ? "; ..." : "");
        }
    }

    // The installed TaleWorlds.Core.TerrainType enum, by name. Navigation never uses a numeric literal for a terrain type.
    internal static class TerrainTypes
    {
        private static readonly Type Installed = typeof(TaleWorlds.Core.TerrainType);
        public static int Id(string name)
        {
            if (name == null || !Enum.GetNames(Installed).Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("The installed TerrainType enum defines no " + (name ?? "(null)") + ".");
            return Convert.ToInt32(Enum.Parse(Installed, name, false), CultureInfo.InvariantCulture);
        }
        public static string Name(int id)
        {
            foreach (var value in Enum.GetValues(Installed)) if (Convert.ToInt32(value, CultureInfo.InvariantCulture) == id) return Enum.GetName(Installed, value);
            return null;
        }
        public static int Max { get { return Enum.GetValues(Installed).Cast<object>().Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).Max(); } }
    }

    internal sealed class NavigationPolicy
    {
        public const string PolicyPath = "worldgen/policies/navigation.json";
        private const string Resource = "MNBL.ProceduralWorld.navigation.json";
        public double ZOffset, SeaDatum, DeckClearance, DeckInset, SnapRadius, Interior, Separation, LandRadius, ExtraCost, SampleStep, DistanceSlack, MaxStaticFactor, StaticSlack, MinDetourExtraCells, MaxDetourFactor;
        public string Sha256 { get; private set; }

        // The policy ships inside the module (an embedded copy of worldgen/policies/navigation.json).
        public static NavigationPolicy Installed()
        {
            using (var stream = typeof(NavigationPolicy).Assembly.GetManifestResourceStream(Resource))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded navigation policy (" + Resource + ").");
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return FromBytes(copy.ToArray()); }
            }
        }
        public static NavigationPolicy FromBytes(byte[] bytes)
        {
            var doc = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
            if (Json.S(doc, "schema") != "mnbl.navigation" || Json.I(doc, "version") != 1) throw new InvalidDataException("Not an mnbl.navigation version 1 policy.");
            Func<string, string, double> n = (section, key) => Json.D(Json.O(doc, section), key);
            var p = new NavigationPolicy {
                ZOffset = n("mesh", "zOffset"), SeaDatum = n("mesh", "seaDatum"), DeckClearance = n("mesh", "deckClearance"), DeckInset = n("mesh", "deckInsetFraction"),
                SnapRadius = n("snapping", "radiusFraction"), Interior = n("snapping", "interiorFraction"), Separation = n("snapping", "separationFraction"),
                LandRadius = n("routes", "landRadius"), ExtraCost = n("routes", "extraCostMultiplier"), SampleStep = n("routes", "sampleStep"), DistanceSlack = n("routes", "distanceSlack"),
                MaxStaticFactor = n("routes", "maxStaticFactor"), StaticSlack = n("routes", "staticSlackFraction"),
                MinDetourExtraCells = n("barrier", "minDetourExtraCells"), MaxDetourFactor = n("barrier", "maxDetourFactor") };
            if (!(p.DeckInset > 0 && p.DeckInset < .5)) throw new InvalidDataException("mesh.deckInsetFraction must lie in (0, 0.5).");
            if (!(p.SnapRadius >= 0 && p.SnapRadius <= 2)) throw new InvalidDataException("snapping.radiusFraction must lie in [0, 2].");
            if (!(p.Interior > 0 && p.Interior < .25) || !(p.Separation > 0 && p.Separation < .5)) throw new InvalidDataException("snapping fractions out of range.");
            if (!(p.SampleStep > 0) || !(p.LandRadius > 0) || !(p.ExtraCost > 0) || !(p.DeckClearance > 0) || !(p.MaxDetourFactor > 1) || !(p.MaxStaticFactor >= 1) || !(p.StaticSlack >= 0)) throw new InvalidDataException("route or mesh constants out of range.");
            p.Sha256 = Json.Sha(bytes);
            return p;
        }
    }

    internal static class Json
    {
        public static IDictionary<string, object> O(IDictionary<string, object> d, string key)
        {
            object v; if (!d.TryGetValue(key, out v) || !(v is IDictionary<string, object>)) throw new InvalidDataException("Missing object " + key + ".");
            return (IDictionary<string, object>)v;
        }
        public static object[] A(IDictionary<string, object> d, string key)
        {
            object v; if (!d.TryGetValue(key, out v) || v == null) return new object[0];
            var array = v as object[]; if (array != null) return array;
            // An in-memory report holds typed lists; a parsed one holds object[].
            var sequence = v as System.Collections.IEnumerable; if (sequence != null && !(v is string) && !(v is IDictionary<string, object>)) return sequence.Cast<object>().ToArray();
            throw new InvalidDataException(key + " is not an array.");
        }
        public static double D(IDictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v) || v == null) throw new InvalidDataException("Missing number " + key + "."); return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        public static int I(IDictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v) || v == null) throw new InvalidDataException("Missing integer " + key + "."); return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        public static string S(IDictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v as string : null; }
        public static bool B(IDictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v is bool && (bool)v; }
        public static int[] Cell(object o) { var a = (object[])o; return new[] { Convert.ToInt32(a[0], CultureInfo.InvariantCulture), Convert.ToInt32(a[1], CultureInfo.InvariantCulture) }; }
        public static double[] Point(IDictionary<string, object> d, string key) { var p = O(d, key); return new[] { D(p, "x"), D(p, "y") }; }
        public static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string Sha(string text) { return Sha(new UTF8Encoding(false).GetBytes(text)); }
    }

    // cells.bin (MMR-28): "MNBLTC01", int32 cols, int32 rows, cols*rows class bytes after repairs, cols*rows OpenSea flags; row-major from the
    // north pole line; columns wrap.
    internal sealed class TravelCells
    {
        public const byte Land = 0, Mountain = 1, Sea = 2, Lake = 3, River = 4;
        public static readonly string[] ClassNames = { "land", "mountain", "sea", "lake", "river" };
        public int Cols, Rows, ColumnOffset, RowOffset;
        public double Spacing;
        public byte[] Classes, OpenSea;
        public static TravelCells Read(byte[] bytes, int cols, int rows, int columnOffset, int rowOffset, double spacing)
        {
            if (bytes == null || bytes.Length < 16 || Encoding.ASCII.GetString(bytes, 0, 8) != "MNBLTC01") throw new InvalidDataException("cells.bin is not MNBLTC01.");
            int c = BitConverter.ToInt32(bytes, 8), r = BitConverter.ToInt32(bytes, 12);
            if (c != cols || r != rows || bytes.Length != 16 + 2 * cols * rows) throw new InvalidDataException("cells.bin does not match the travel grid (" + cols + " x " + rows + ").");
            var t = new TravelCells { Cols = cols, Rows = rows, ColumnOffset = columnOffset, RowOffset = rowOffset, Spacing = spacing, Classes = new byte[cols * rows], OpenSea = new byte[cols * rows] };
            Array.Copy(bytes, 16, t.Classes, 0, cols * rows); Array.Copy(bytes, 16 + cols * rows, t.OpenSea, 0, cols * rows);
            if (t.Classes.Any(k => k > River) || t.OpenSea.Any(f => f > 1)) throw new InvalidDataException("cells.bin holds an unknown class or flag.");
            return t;
        }
        public TravelCells Clone() { return new TravelCells { Cols = Cols, Rows = Rows, ColumnOffset = ColumnOffset, RowOffset = RowOffset, Spacing = Spacing, Classes = (byte[])Classes.Clone(), OpenSea = (byte[])OpenSea.Clone() }; }
        public int Wrap(int c) { return ((c % Cols) + Cols) % Cols; }
        public int Index(int r, int c) { return r * Cols + Wrap(c); }
        public bool InRows(int r) { return r >= 0 && r < Rows; }
        public byte At(int r, int c) { return Classes[Index(r, c)]; }
        public static bool Water(byte k) { return k == Sea || k == Lake || k == River; }
        // The cell (r, c) holding a logical point (x wraps).
        public int[] CellOf(double x, double y) { return new[] { (int)Math.Floor(y / Spacing), Wrap((int)Math.Floor(x / Spacing)) }; }
        // The MMR-28 OpenSea rule: sea cells at least `cells` cells (Chebyshev, columns wrap) from any other class.
        public byte[] ComputeOpenSea(int cells)
        {
            var dist = Enumerable.Repeat(-1, Classes.Length).ToArray(); var queue = new Queue<int>();
            for (int i = 0; i < Classes.Length; i++) if (Classes[i] != Sea) { dist[i] = 0; queue.Enqueue(i); }
            while (queue.Count > 0)
            {
                int j = queue.Dequeue(), r = j / Cols, c = j % Cols;
                for (int dr = -1; dr <= 1; dr++) for (int dc = -1; dc <= 1; dc++)
                {
                    if ((dr == 0 && dc == 0) || !InRows(r + dr)) continue;
                    int k = Index(r + dr, c + dc); if (dist[k] < 0) { dist[k] = dist[j] + 1; queue.Enqueue(k); }
                }
            }
            return Classes.Select((k, i) => (byte)(k == Sea && (dist[i] < 0 || dist[i] >= cells) ? 1 : 0)).ToArray();
        }
        // The MMR-28 flood fill: 4-connected (columns wrap), labelled in row-major order of each component's first cell.
        public int[] Label(Func<byte, bool> member)
        {
            var label = Enumerable.Repeat(-1, Classes.Length).ToArray(); int next = 0;
            for (int start = 0; start < Classes.Length; start++)
            {
                if (label[start] >= 0 || !member(Classes[start])) continue;
                var queue = new Queue<int>(); queue.Enqueue(start); label[start] = next;
                while (queue.Count > 0)
                {
                    int j = queue.Dequeue(), r = j / Cols, c = j % Cols;
                    foreach (var n in Neighbours(r, c)) if (label[n] < 0 && member(Classes[n])) { label[n] = next; queue.Enqueue(n); }
                }
                next++;
            }
            return label;
        }
        // Edge neighbours in the MMR-28 order (r+1, r-1, c+1, c-1); rows do not wrap.
        public IEnumerable<int> Neighbours(int r, int c)
        {
            if (r + 1 < Rows) yield return Index(r + 1, c);
            if (r > 0) yield return Index(r - 1, c);
            yield return Index(r, c + 1);
            yield return Index(r, c - 1);
        }
    }

    internal sealed class SettlementRecord { public string Id, Uid, Kind, Method; public int Row, Col, Land, Group; public double X, Y; }
    internal sealed class TransitionRecord { public string Kind, Town; public int Group, Basin; public int[] LandCell, SeaCell; public double[] LandPoint, SeaPoint; }
    internal sealed class CrossingRecord { public string Subject, Kind; public List<int[]> Cells; public int LandA, LandB; }
    internal sealed class RoadRecord { public int Index; public string From, To, FromUid, ToUid; public bool SameGroup; }
    internal sealed class HostCheck { public string Check, Subject, Expected; }
    internal sealed class GroupRecord { public int Id; public int[] Lands; public bool PlayerSafe; public int Towns; }
    internal sealed class BasinRecord { public int Id, Cells, OpenSeaCells; public bool Open; }

    internal sealed class NavigationInput
    {
        public string WorldContentId, Variant = "production", InputsSha256;
        public WorldSurface Surface;
        public TravelCells Cells;
        public Dictionary<string, string> FaceGroupNames = new Dictionary<string, string>(StringComparer.Ordinal);  // cell class -> TerrainType name
        public int OpenSeaCells, MaxBridgeCells;
        public double MaxPortGateUnits;
        public int MainGroup;
        public List<SettlementRecord> Settlements = new List<SettlementRecord>();
        public HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, string> VillageTown = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<RoadRecord> Roads = new List<RoadRecord>();
        public List<CrossingRecord> Crossings = new List<CrossingRecord>();
        public List<TransitionRecord> Transitions = new List<TransitionRecord>();
        public List<HostCheck> HostChecks = new List<HostCheck>();
        public List<GroupRecord> Groups = new List<GroupRecord>();
        public List<BasinRecord> Basins = new List<BasinRecord>();
        public Dictionary<int, int> LandComponentCells = new Dictionary<int, int>();
        public Dictionary<string, int> ClassCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string[]> PositionCollisions = new List<string[]>();
        public List<NavigationFixtures.Check> FixtureChecks;   // fixture worlds only
        public NavigationInput Shallow() { return (NavigationInput)MemberwiseClone(); }

        public static NavigationInput FromBundle(WorldDataBundle bundle, WorldSurface surface, NavigationPolicy policy)
        {
            return FromParts(bundle.WorldContentId, surface, bundle.ReadVerified("compiled/travel/cells.bin"), bundle.ReadVerified("compiled/travel/travel-graph.json"),
                bundle.ReadVerified("compiled/campaign/campaign.json"), bundle.ReadVerified("policies/playability.json"), policy);
        }

        public static NavigationInput FromParts(string contentId, WorldSurface surface, byte[] cellsBin, byte[] graphJson, byte[] campaignJson, byte[] playabilityJson, NavigationPolicy policy)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<byte[], IDictionary<string, object>> parse = b => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(b).TrimStart('﻿'));
            var graph = parse(graphJson); var campaign = parse(campaignJson); var playability = parse(playabilityJson);
            if (Json.S(graph, "schema") != "mnbl.travel-graph") throw new InvalidDataException("Not an mnbl.travel-graph.");
            if (Json.S(graph, "status") != "ok") throw new InvalidDataException("The travel graph was rejected by its compiler (status " + Json.S(graph, "status") + ").");
            var grid = Json.O(graph, "grid");
            var input = new NavigationInput { WorldContentId = contentId, Surface = surface };
            input.Cells = TravelCells.Read(cellsBin, Json.I(grid, "cols"), Json.I(grid, "rows"), Json.I(grid, "columnOffset"), Json.I(grid, "rowOffset"), Json.D(grid, "spacing"));
            if (Json.S(grid, "cellsSha256") != Json.Sha(cellsBin)) throw new InvalidDataException("cells.bin is not the travel graph's cells (sha256 differs).");
            var t = input.Cells;
            if (Math.Abs(t.Spacing - surface.Spacing) > 1e-9 || Math.Abs(t.ColumnOffset * t.Spacing - surface.Strip) > 1e-9 || Math.Abs(t.RowOffset * t.Spacing - surface.PoleMargin) > 1e-9
                || Math.Abs(t.Cols * t.Spacing - surface.LogicalWidth) > 1e-9 || Math.Abs(t.Rows * t.Spacing - surface.LogicalHeight) > 1e-9)
                throw new InvalidDataException("The travel grid does not cover the surface's logical globe (spacing, strip, pole margin, width or height differ).");
            foreach (var kv in Json.O(grid, "classes")) input.ClassCounts[kv.Key] = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture);
            var cellPolicy = Json.O(playability, "cells");
            foreach (var kv in Json.O(cellPolicy, "faceGroups")) input.FaceGroupNames[kv.Key] = (string)kv.Value;
            input.OpenSeaCells = Json.I(cellPolicy, "openSeaCells");
            input.MaxBridgeCells = Json.I(Json.O(playability, "bridges"), "maxCells");
            input.MaxPortGateUnits = Json.D(Json.O(graph, "engine"), "maxPortGateUnits");
            input.MainGroup = Json.I(graph, "mainGroup");
            foreach (IDictionary<string, object> x in Json.A(graph, "exclusions")) input.Excluded.Add(Json.S(x, "id"));
            foreach (IDictionary<string, object> s in Json.A(graph, "settlements"))
            {
                var cell = Json.Cell(s["cell"]); var p = Json.Point(s, "point");
                input.Settlements.Add(new SettlementRecord { Id = Json.S(s, "id"), Uid = Json.S(s, "uid"), Kind = Json.S(s, "kind"), Method = Json.S(s, "method"), Row = cell[0], Col = cell[1],
                    Land = Json.I(s, "land"), Group = Json.I(s, "group"), X = p[0], Y = p[1] });
            }
            foreach (IDictionary<string, object> v in Json.A(campaign, "villages")) input.VillageTown[Json.S(v, "id")] = Json.S(v, "bound");
            foreach (IDictionary<string, object> r in Json.A(graph, "roads"))
            {
                int index = Json.I(r, "index");
                input.Roads.Add(new RoadRecord { Index = index, From = Json.S(r, "from"), To = Json.S(r, "to"), FromUid = Json.S(r, "fromUid"), ToUid = Json.S(r, "toUid"), SameGroup = Json.B(r, "sameGroup") });
                int j = 0;
                foreach (IDictionary<string, object> x in Json.A(r, "crossings"))
                {
                    if (Json.B(x, "joins"))
                    {
                        var lands = Json.A(x, "lands");
                        input.Crossings.Add(new CrossingRecord { Subject = "road " + index + " crossing " + j, Kind = Json.S(x, "kind"), Cells = Json.A(x, "cells").Select(Json.Cell).ToList(),
                            LandA = Convert.ToInt32(lands[0], CultureInfo.InvariantCulture), LandB = Convert.ToInt32(lands[1], CultureInfo.InvariantCulture) });
                    }
                    j++;
                }
            }
            foreach (IDictionary<string, object> x in Json.A(graph, "transitions"))
                input.Transitions.Add(new TransitionRecord { Kind = Json.S(x, "kind"), Town = Json.S(x, "town"), Group = Json.I(x, "group"), Basin = Json.I(x, "basin"),
                    LandCell = Json.Cell(x["landCell"]), SeaCell = Json.Cell(x["seaCell"]), LandPoint = Json.Point(x, "landPoint"), SeaPoint = Json.Point(x, "seaPoint") });
            foreach (IDictionary<string, object> h in Json.A(graph, "hostChecks"))
                input.HostChecks.Add(new HostCheck { Check = Json.S(h, "check"), Subject = Json.S(h, "subject"), Expected = Json.S(h, "expected") });
            foreach (IDictionary<string, object> g in Json.A(graph, "groups"))
                input.Groups.Add(new GroupRecord { Id = Json.I(g, "id"), Lands = Json.A(g, "lands").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray(), PlayerSafe = Json.B(g, "playerSafe"), Towns = Json.I(g, "towns") });
            foreach (IDictionary<string, object> b in Json.A(graph, "basins"))
                input.Basins.Add(new BasinRecord { Id = Json.I(b, "id"), Cells = Json.I(b, "cells"), OpenSeaCells = Json.I(b, "openSea"), Open = Json.B(b, "open") });
            foreach (IDictionary<string, object> l in Json.A(graph, "landComponents")) input.LandComponentCells[Json.I(l, "id")] = Json.I(l, "cells");
            foreach (var c in Json.A(graph, "positionCollisions")) input.PositionCollisions.Add(((object[])c).Select(v => (string)v).ToArray());
            input.InputsSha256 = Json.Sha(String.Join("\n", new[] { Json.Sha(cellsBin), Json.Sha(graphJson), Json.Sha(campaignJson), Json.Sha(playabilityJson), policy.Sha256, surface.InputsSha256 }));
            return input;
        }
    }

    // Per logical cell: the MMR-28 class, its domain, water class, terrain cost and passage (kept separately), and the installed TerrainType.
    internal sealed class NavCell
    {
        public byte Class;
        public string Domain, WaterClass, Cost, Passage;
        public int TerrainType;
        public string Key { get { return "domain " + Domain + ", water " + (WaterClass ?? "-") + ", cost " + (Cost ?? "-") + ", passage " + Passage + " -> " + TerrainTypes.Name(TerrainType); } }
    }

    // Sides of a cell, counter-clockwise from +Z: 0 = low y (row - 1), 1 = high x (column + 1), 2 = high y (row + 1), 3 = low x (column - 1).
    internal sealed class BridgeSpec
    {
        public int Index;
        public List<string> Subjects = new List<string>();
        public string Kind;
        public int LandA, LandB;
        public List<int[]> Cells = new List<int[]>();   // logical (r, c), from bank A to bank B
        public List<int[]> Sides = new List<int[]>();   // per cell: entry side, exit side
        public int[] BankA, BankB;
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["subjects"] = Subjects, ["kind"] = Kind, ["lands"] = new[] { LandA, LandB }, ["cells"] = Cells, ["sides"] = Sides, ["bankA"] = BankA, ["bankB"] = BankB };
        }
    }

    internal sealed class NavMesh
    {
        public const byte RoleCell = 0, RoleBank = 1, RoleUnderBridge = 2, RoleDeck = 3;
        public readonly List<double[]> Vertices = new List<double[]>();
        public readonly List<int[]> Edges = new List<int[]>();           // (v0, v1) in the creating face's direction
        public readonly List<int[]> FaceVertices = new List<int[]>();
        public readonly List<int[]> FaceEdges = new List<int[]>();
        public readonly List<int> Groups = new List<int>();
        public readonly List<byte> Roles = new List<byte>();
        public readonly List<int> PhysicalRows = new List<int>(), PhysicalColumns = new List<int>(), Bridges = new List<int>();
        public readonly Dictionary<long, List<int>> CellFaces = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, int> edgeIndex = new Dictionary<long, int>();
        public int FaceCount { get { return Groups.Count; } }
        public double CellSize;

        public static long Key(int row, int column) { return ((long)row << 32) | (uint)column; }
        public int AddVertex(double x, double y, double z) { Vertices.Add(new[] { x, y, z }); return Vertices.Count - 1; }
        public int AddFace(int[] loop, int group, byte role, int row, int column, int bridge)
        {
            var edges = new int[loop.Length];
            for (int j = 0; j < loop.Length; j++)
            {
                int a = loop[j], b = loop[(j + 1) % loop.Length];
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                int e; if (!edgeIndex.TryGetValue(key, out e)) { e = Edges.Count; edgeIndex[key] = e; Edges.Add(new[] { a, b }); }
                edges[j] = e;
            }
            FaceVertices.Add(loop); FaceEdges.Add(edges); Groups.Add(group); Roles.Add(role); PhysicalRows.Add(row); PhysicalColumns.Add(column); Bridges.Add(bridge);
            List<int> list; long cell = Key(row, column); if (!CellFaces.TryGetValue(cell, out list)) CellFaces[cell] = list = new List<int>();
            list.Add(FaceCount - 1);
            return FaceCount - 1;
        }

        // The faces containing a point (x, y) whose group passes `accept`, in face order (inclusive of edges within tolerance).
        public IEnumerable<int> FacesAt(double x, double y, Func<int, bool> accept)
        {
            int row = (int)Math.Floor(y / CellSize), column = (int)Math.Floor(x / CellSize);
            for (int dr = -1; dr <= 1; dr++) for (int dc = -1; dc <= 1; dc++)
            {
                List<int> list; if (!CellFaces.TryGetValue(Key(row + dr, column + dc), out list)) continue;
                foreach (int f in list) if (accept(Groups[f]) && Contains(f, x, y)) yield return f;
            }
        }
        public bool Contains(int face, double x, double y)
        {
            var loop = FaceVertices[face];
            for (int j = 0; j < loop.Length; j++)
            {
                var a = Vertices[loop[j]]; var b = Vertices[loop[(j + 1) % loop.Length]];
                if ((b[0] - a[0]) * (y - a[1]) - (b[1] - a[1]) * (x - a[0]) < -1e-9) return false;
            }
            return true;
        }
        public double[] Centroid(int face)
        {
            var loop = FaceVertices[face]; double x = 0, y = 0;
            foreach (int v in loop) { x += Vertices[v][0]; y += Vertices[v][1]; }
            return new[] { x / loop.Length, y / loop.Length };
        }

        // NMG9 in an RNM1 container: compression type 3 (one LZ4 block; a single literal run, as the surface encoder writes), scratch
        // fields 0 (docs/formats/navmesh-bin.md).
        public byte[] Encode()
        {
            var payload = new MemoryStream(); var w = new BinaryWriter(payload);
            w.Write(Encoding.ASCII.GetBytes("NMG9"));
            w.Write((uint)Vertices.Count); foreach (var v in Vertices) { w.Write((float)v[0]); w.Write((float)v[1]); w.Write((float)v[2]); }
            w.Write((uint)Edges.Count); foreach (var e in Edges) { w.Write(-1); w.Write(e[0]); w.Write(e[1]); w.Write(-1); w.Write(-1); w.Write(0); }
            w.Write((uint)FaceCount);
            for (int f = 0; f < FaceCount; f++)
            {
                w.Write((uint)FaceVertices[f].Length);
                foreach (int v in FaceVertices[f]) w.Write(v);
                foreach (int e in FaceEdges[f]) w.Write(e);
                w.Write(2); w.Write(0); w.Write(Groups[f]); w.Write(0); w.Write(-1); w.Write((byte)0);
            }
            w.Write((uint)32); w.Write(new byte[256]); w.Flush();
            var raw = payload.ToArray();
            var block = new MemoryStream();
            block.WriteByte((byte)(Math.Min(raw.Length, 15) << 4));
            if (raw.Length >= 15) { long k = raw.Length - 15; while (k >= 255) { block.WriteByte(255); k -= 255; } block.WriteByte((byte)k); }
            block.Write(raw, 0, raw.Length);
            var compressed = block.ToArray();
            var file = new MemoryStream(); var o = new BinaryWriter(file);
            o.Write(Encoding.ASCII.GetBytes("RNM1")); o.Write((uint)(40 + compressed.Length)); o.Write((uint)3); o.Write((uint)0);
            o.Write((ulong)raw.Length); o.Write((ulong)(40 + compressed.Length)); o.Write((ulong)0); o.Write((ulong)0); o.Write(compressed); o.Flush();
            return file.ToArray();
        }

        // Decodes an RNM1 NMG9 navmesh (any LZ4 block) back to vertices, edges, faces and groups.
        public static NavMesh Decode(byte[] file)
        {
            if (file.Length < 48 || Encoding.ASCII.GetString(file, 0, 4) != "RNM1" || BitConverter.ToUInt32(file, 4) != file.Length - 8 || BitConverter.ToUInt32(file, 8) != 3) throw new InvalidDataException("Not an RNM1 LZ4 navmesh.");
            long size = (long)BitConverter.ToUInt64(file, 16);
            var raw = new byte[size]; int ip = 48, op = 0;
            while (ip < file.Length)
            {
                int token = file[ip++]; long literal = token >> 4;
                if (literal == 15) { int b; do { b = file[ip++]; literal += b; } while (b == 255); }
                Array.Copy(file, ip, raw, op, literal); ip += (int)literal; op += (int)literal;
                if (ip >= file.Length) break;
                int offset = file[ip] | (file[ip + 1] << 8); ip += 2; long match = token & 15;
                if (match == 15) { int b; do { b = file[ip++]; match += b; } while (b == 255); }
                match += 4; for (long i = 0; i < match; i++, op++) raw[op] = raw[op - offset];
            }
            if (op != size) throw new InvalidDataException("The LZ4 block does not decode to the recorded size.");
            var r = new BinaryReader(new MemoryStream(raw));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "NMG9") throw new InvalidDataException("Not NMG9.");
            var mesh = new NavMesh();
            uint vertices = r.ReadUInt32(); for (uint i = 0; i < vertices; i++) mesh.Vertices.Add(new double[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() });
            uint edges = r.ReadUInt32();
            for (uint i = 0; i < edges; i++)
            {
                int e0 = r.ReadInt32(), v0 = r.ReadInt32(), v1 = r.ReadInt32(), e3 = r.ReadInt32(), e4 = r.ReadInt32(), e5 = r.ReadInt32();
                if (e0 != -1 || e3 != -1 || e4 != -1 || e5 != 0) throw new InvalidDataException("Edge " + i + " reserved fields are not (-1, -1, -1, 0).");
                mesh.Edges.Add(new[] { v0, v1 });
            }
            uint faces = r.ReadUInt32();
            for (uint f = 0; f < faces; f++)
            {
                int k = (int)r.ReadUInt32(); var loop = new int[k]; var es = new int[k];
                for (int j = 0; j < k; j++) loop[j] = r.ReadInt32();
                for (int j = 0; j < k; j++) es[j] = r.ReadInt32();
                int m0 = r.ReadInt32(), m1 = r.ReadInt32(), group = r.ReadInt32(), m3 = r.ReadInt32(), m4 = r.ReadInt32(); byte flag = r.ReadByte();
                if (m0 != 2 || m1 != 0 || m3 != 0 || m4 != -1 || flag != 0) throw new InvalidDataException("Face " + f + " metadata is not the campaign-map values.");
                mesh.FaceVertices.Add(loop); mesh.FaceEdges.Add(es); mesh.Groups.Add(group); mesh.Roles.Add(RoleCell); mesh.PhysicalRows.Add(-1); mesh.PhysicalColumns.Add(-1); mesh.Bridges.Add(-1);
            }
            if (r.ReadUInt32() != 32 || r.ReadBytes(256).Any(b => b != 0) || r.BaseStream.Position != raw.Length) throw new InvalidDataException("The NMG9 tail is not 32 + 256 zero bytes.");
            return mesh;
        }

        // The topology invariants of the installed campaign maps (docs/formats/navmesh-bin.md section 4): every face side is its edge record,
        // every face counter-clockwise and strictly convex in XY, degree 3 or 4, every edge used by one or two faces (twice in opposite
        // directions), one edge record per vertex pair, every vertex referenced.
        public List<string> Validate()
        {
            var problems = new List<string>();
            var uses = new int[Edges.Count]; var forward = new int[Edges.Count];
            var pairs = new HashSet<long>();
            for (int e = 0; e < Edges.Count; e++) { long key = ((long)Math.Min(Edges[e][0], Edges[e][1]) << 32) | (uint)Math.Max(Edges[e][0], Edges[e][1]); if (!pairs.Add(key)) problems.Add("duplicate vertex pair in edge " + e); }
            var referenced = new bool[Vertices.Count];
            for (int f = 0; f < FaceCount; f++)
            {
                var loop = FaceVertices[f]; int k = loop.Length;
                if (k != 3 && k != 4) problems.Add("face " + f + " has degree " + k);
                double area = 0;
                for (int j = 0; j < k; j++)
                {
                    int a = loop[j], b = loop[(j + 1) % k], c = loop[(j + 2) % k]; referenced[a] = true;
                    var edge = Edges[FaceEdges[f][j]];
                    if (edge[0] == a && edge[1] == b) forward[FaceEdges[f][j]]++;
                    else if (!(edge[0] == b && edge[1] == a)) problems.Add("face " + f + " side " + j + " is not its edge record");
                    uses[FaceEdges[f][j]]++;
                    var pa = Vertices[a]; var pb = Vertices[b]; var pc = Vertices[c];
                    area += pa[0] * pb[1] - pb[0] * pa[1];
                    if ((pb[0] - pa[0]) * (pc[1] - pb[1]) - (pb[1] - pa[1]) * (pc[0] - pb[0]) <= 1e-9) problems.Add("face " + f + " is not strictly convex at vertex " + b);
                }
                if (area <= 0) problems.Add("face " + f + " is not counter-clockwise");
            }
            for (int e = 0; e < Edges.Count; e++)
            {
                if (uses[e] < 1 || uses[e] > 2) problems.Add("edge " + e + " is used by " + uses[e] + " faces");
                else if (uses[e] == 2 && forward[e] != 1) problems.Add("edge " + e + " is traversed twice in one direction");
            }
            for (int v = 0; v < Vertices.Count; v++) if (!referenced[v]) problems.Add("vertex " + v + " is not referenced");
            return problems;
        }
    }

    internal sealed class NavigationBuild
    {
        public NavigationInput Input;
        public NavigationPolicy Policy;
        public NavCell[] Cells;
        public int[] LandOf, BasinOf;
        public byte[] OpenSeaComputed;
        public Dictionary<string, int> FaceGroups = new Dictionary<string, int>(StringComparer.Ordinal);   // cell class -> installed TerrainType id
        public List<BridgeSpec> Bridges = new List<BridgeSpec>();
        public NavMesh Mesh;
        public byte[] Bytes;
        public string Sha256;
        public int PhysicalColumns;
        public List<NavigationDiagnostic> Diagnostics = new List<NavigationDiagnostic>();
        public NavCell CellAt(int r, int c) { return Cells[Input.Cells.Index(r, c)]; }

        public Dictionary<string, object> Record()
        {
            var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (int g in Mesh.Groups) { string name = TerrainTypes.Name(g) ?? g.ToString(CultureInfo.InvariantCulture); int n; histogram.TryGetValue(name, out n); histogram[name] = n + 1; }
            var roles = new SortedDictionary<string, int>(StringComparer.Ordinal);
            string[] roleNames = { "cell", "bank", "underBridge", "deck" };
            foreach (byte role in Mesh.Roles) { int n; roles.TryGetValue(roleNames[role], out n); roles[roleNames[role]] = n + 1; }
            return new Dictionary<string, object> {
                ["variant"] = Input.Variant, ["worldContentId"] = Input.WorldContentId, ["inputsSha256"] = Input.InputsSha256, ["policySha256"] = Policy.Sha256, ["navmeshSha256"] = Sha256,
                ["bytes"] = Bytes.Length, ["vertices"] = Mesh.Vertices.Count, ["edges"] = Mesh.Edges.Count, ["faces"] = Mesh.FaceCount, ["physicalColumns"] = PhysicalColumns,
                ["rows"] = Input.Cells.Rows, ["faceGroups"] = FaceGroups.ToDictionary(k => k.Key, k => (object)(TerrainTypes.Name(k.Value) + " " + k.Value)),
                ["facesByTerrainType"] = histogram, ["facesByRole"] = roles,
                ["cellSemantics"] = Cells.GroupBy(c => c.Key).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["bridges"] = Bridges.Select(b => b.Record()).ToList(), ["diagnostics"] = Diagnostics.Select(d => d.Record()).ToList() };
        }
    }

    internal static class RuntimeNavigationBuilder
    {
        private static readonly int[] DR = { -1, 0, 1, 0 }, DC = { 0, 1, 0, -1 };

        public static NavigationBuild Build(NavigationInput input, NavigationPolicy policy)
        {
            var t = input.Cells; var s = input.Surface;
            var build = new NavigationBuild { Input = input, Policy = policy };
            foreach (var kv in input.FaceGroupNames) build.FaceGroups[kv.Key] = TerrainTypes.Id(kv.Value);
            foreach (var key in new[] { "land", "mountain", "coastalSea", "openSea", "lake", "river", "bridge", "underBridge" })
                if (!build.FaceGroups.ContainsKey(key)) throw new InvalidDataException("The playability policy names no face group for " + key + ".");
            build.LandOf = t.Label(k => k == TravelCells.Land);
            build.BasinOf = t.Label(TravelCells.Water);
            build.OpenSeaComputed = t.ComputeOpenSea(input.OpenSeaCells);
            build.PhysicalColumns = (int)Math.Round(s.Extent / t.Spacing);
            if (Math.Abs(build.PhysicalColumns * t.Spacing - s.Extent) > 1e-9 || build.PhysicalColumns < t.Cols) throw new InvalidDataException("The surface extent is not a whole number of travel cells.");

            // Cell semantics.
            build.Cells = new NavCell[t.Classes.Length];
            for (int i = 0; i < t.Classes.Length; i++)
            {
                byte k = t.Classes[i]; var cell = new NavCell { Class = k };
                if (k == TravelCells.Land) { cell.Domain = "land"; cell.Cost = "plain"; cell.Passage = "open"; cell.TerrainType = build.FaceGroups["land"]; }
                else if (k == TravelCells.Mountain) { cell.Domain = "none"; cell.Cost = "mountain"; cell.Passage = "impassable"; cell.TerrainType = build.FaceGroups["mountain"]; }
                else
                {
                    cell.Domain = "sea"; cell.Passage = "open";
                    cell.WaterClass = k == TravelCells.Lake ? "lake" : k == TravelCells.River ? "river" : t.OpenSea[i] == 1 ? "openSea" : "coastalSea";
                    cell.TerrainType = build.FaceGroups[cell.WaterClass];
                }
                build.Cells[i] = cell;
            }

            // Bridges: every joining crossing (deduplicated by its cells).
            var deckSides = new Dictionary<int, int[]>();     // logical cell -> (entry, exit)
            var deckBridge = new Dictionary<int, int>();
            var bankSides = new Dictionary<int, HashSet<int>>(); // logical bank cell -> its sides facing a deck
            foreach (var crossing in input.Crossings)
            {
                var spec = ResolveBridge(input, build, crossing);
                if (spec == null) continue;
                var same = build.Bridges.FirstOrDefault(b => b.Cells.Count == spec.Cells.Count && b.Cells.Select(c => t.Index(c[0], c[1])).OrderBy(x => x).SequenceEqual(spec.Cells.Select(c => t.Index(c[0], c[1])).OrderBy(x => x)));
                if (same != null) { same.Subjects.Add(crossing.Subject); continue; }
                bool conflict = false;
                for (int i = 0; i < spec.Cells.Count; i++)
                {
                    int idx = t.Index(spec.Cells[i][0], spec.Cells[i][1]); int[] known;
                    if (deckSides.TryGetValue(idx, out known) && !(known.Min() == spec.Sides[i].Min() && known.Max() == spec.Sides[i].Max())) conflict = true;
                }
                if (conflict) { build.Diagnostics.Add(new NavigationDiagnostic("navigation.bridge_conflict", crossing.Subject, "a crossing cell is already a deck crossed through other sides")); continue; }
                spec.Index = build.Bridges.Count; build.Bridges.Add(spec);
                for (int i = 0; i < spec.Cells.Count; i++)
                {
                    int idx = t.Index(spec.Cells[i][0], spec.Cells[i][1]); deckSides[idx] = spec.Sides[i]; deckBridge[idx] = spec.Index;
                    build.Cells[idx].Passage = "underBridge"; build.Cells[idx].TerrainType = build.FaceGroups["underBridge"];
                }
                foreach (var bank in new[] { new { Cell = spec.BankA, Side = (spec.Sides[0][0] + 2) % 4 }, new { Cell = spec.BankB, Side = (spec.Sides[spec.Sides.Count - 1][1] + 2) % 4 } })
                {
                    int idx = t.Index(bank.Cell[0], bank.Cell[1]); HashSet<int> set;
                    if (!bankSides.TryGetValue(idx, out set)) bankSides[idx] = set = new HashSet<int>();
                    set.Add(bank.Side);
                }
            }

            // Geometry: every physical cell between the pole lines.
            var mesh = new NavMesh { CellSize = t.Spacing }; build.Mesh = mesh;
            double sp = t.Spacing, inset = policy.DeckInset;
            var lattice = new Dictionary<long, int>(); var insets = new Dictionary<long, int>();
            Func<int, int, int> logical = (pr, pc) => t.Index(pr - t.RowOffset, pc - t.ColumnOffset);
            Func<int, int, bool> inBand = (pr, pc) => pr >= t.RowOffset && pr < t.RowOffset + t.Rows && pc >= 0 && pc < build.PhysicalColumns;
            Func<int, int, bool> isDeck = (pr, pc) => inBand(pr, pc) && deckSides.ContainsKey(logical(pr, pc));
            Func<double, double, double> ground = (x, y) => Math.Max(s.CollisionHeight(x, y), policy.SeaDatum);
            Func<int, int, int> corner = (row, column) =>
            {
                int v; long key = NavMesh.Key(row, column);
                if (!lattice.TryGetValue(key, out v)) { double x = column * sp, y = row * sp; lattice[key] = v = mesh.AddVertex(x, y, ground(x, y) + policy.ZOffset); }
                return v;
            };
            // Inset point `which` (0 = nearer the lower lattice end) on a canonical lattice edge: horizontal (row line, column segment) or vertical.
            Func<bool, int, int, int, double, int> insetPoint = (horizontal, line, segment, which, z) =>
            {
                long key = ((long)(horizontal ? 0 : 1) << 62) | ((long)line << 40) | ((long)segment << 8) | (uint)which;
                int v;
                if (!insets.TryGetValue(key, out v))
                {
                    double f = which == 0 ? inset : 1 - inset;
                    double x = horizontal ? (segment + f) * sp : line * sp, y = horizontal ? line * sp : (segment + f) * sp;
                    insets[key] = v = mesh.AddVertex(x, y, (double.IsNaN(z) ? ground(x, y) + policy.ZOffset : ground(x, y) + z));
                }
                return v;
            };
            // The two inset points of side k of physical cell (pr, pc), in the cell's counter-clockwise order.
            Func<int, int, int, int[]> sidePoints = (pr, pc, k) =>
            {
                int nr = pr + DR[k], nc = pc + DC[k];
                double z = isDeck(pr, pc) && isDeck(nr, nc) ? policy.DeckClearance : double.NaN;
                switch (k)
                {
                    case 0: return new[] { insetPoint(true, pr, pc, 0, z), insetPoint(true, pr, pc, 1, z) };
                    case 1: return new[] { insetPoint(false, pc + 1, pr, 0, z), insetPoint(false, pc + 1, pr, 1, z) };
                    case 2: return new[] { insetPoint(true, pr + 1, pc, 1, z), insetPoint(true, pr + 1, pc, 0, z) };
                    default: return new[] { insetPoint(false, pc, pr, 1, z), insetPoint(false, pc, pr, 0, z) };
                }
            };
            for (int pr = t.RowOffset; pr < t.RowOffset + t.Rows; pr++)
                for (int pc = 0; pc < build.PhysicalColumns; pc++)
                {
                    int idx = logical(pr, pc); var cell = build.Cells[idx];
                    int[] corners = { corner(pr, pc), corner(pr, pc + 1), corner(pr + 1, pc + 1), corner(pr + 1, pc) };
                    HashSet<int> banks;
                    var split = new bool[4];
                    if (bankSides.TryGetValue(idx, out banks)) for (int k = 0; k < 4; k++) split[k] = banks.Contains(k) && isDeck(pr + DR[k], pc + DC[k]);
                    if (split.Any(b => b))
                    {
                        var loop = new List<int>();
                        for (int k = 0; k < 4; k++) { loop.Add(corners[k]); if (split[k]) loop.AddRange(sidePoints(pr, pc, k)); }
                        double cx = (pc + .5) * sp, cy = (pr + .5) * sp;
                        int centre = mesh.AddVertex(cx, cy, ground(cx, cy) + policy.ZOffset);
                        for (int j = 0; j < loop.Count; j++) mesh.AddFace(new[] { centre, loop[j], loop[(j + 1) % loop.Count] }, cell.TerrainType, NavMesh.RoleBank, pr, pc, -1);
                    }
                    else mesh.AddFace(corners, cell.TerrainType, deckSides.ContainsKey(idx) ? NavMesh.RoleUnderBridge : NavMesh.RoleCell, pr, pc, -1);
                    int[] sides;
                    if (deckSides.TryGetValue(idx, out sides))
                    {
                        var deck = new List<int>();
                        for (int k = 0; k < 4; k++) if (k == sides[0] || k == sides[1]) deck.AddRange(sidePoints(pr, pc, k));
                        mesh.AddFace(deck.ToArray(), build.FaceGroups["bridge"], NavMesh.RoleDeck, pr, pc, deckBridge[idx]);
                    }
                }
            foreach (var problem in mesh.Validate().Take(20)) build.Diagnostics.Add(new NavigationDiagnostic("navigation.mesh_invalid", "navmesh", problem));
            build.Bytes = mesh.Encode();
            build.Sha256 = Json.Sha(build.Bytes);
            return build;
        }

        // A joining crossing as a deck: an edge-connected run of river or lake cells from a bank of land component A to one of B, each cell
        // entered and left through two different sides. Opposite sides are preferred; ties take the lowest side numbers.
        private static BridgeSpec ResolveBridge(NavigationInput input, NavigationBuild build, CrossingRecord x)
        {
            var t = input.Cells;
            Func<string, NavigationDiagnostic> refuse = why => { var d = new NavigationDiagnostic("navigation.bridge_unrealizable", x.Subject, why); build.Diagnostics.Add(d); return d; };
            if (x.Cells.Count < 1 || x.Cells.Count > input.MaxBridgeCells) { refuse("a bridge spans 1 to " + input.MaxBridgeCells + " cells, not " + x.Cells.Count); return null; }
            foreach (var c in x.Cells)
                if (!t.InRows(c[0]) || (t.At(c[0], c[1]) != TravelCells.River && t.At(c[0], c[1]) != TravelCells.Lake)) { refuse("crossing cell " + c[0] + "," + t.Wrap(c[1]) + " is not river or lake"); return null; }
            Func<int[], int[], int> sideTo = (a, b) =>
            {
                for (int k = 0; k < 4; k++) if (a[0] + DR[k] == b[0] && t.Wrap(a[1] + DC[k]) == t.Wrap(b[1])) return k;
                return -1;
            };
            for (int i = 1; i < x.Cells.Count; i++) if (sideTo(x.Cells[i - 1], x.Cells[i]) < 0) { refuse("crossing cells are not edge-connected"); return null; }
            Func<int[], int, List<int>> banksOf = (c, land) => Enumerable.Range(0, 4).Where(k => t.InRows(c[0] + DR[k]) && t.At(c[0] + DR[k], c[1] + DC[k]) == TravelCells.Land
                && build.LandOf[t.Index(c[0] + DR[k], c[1] + DC[k])] == land).ToList();
            int last = x.Cells.Count - 1;
            var entries = banksOf(x.Cells[0], x.LandA); var exits = banksOf(x.Cells[last], x.LandB);
            if (last > 0) { entries.Remove(sideTo(x.Cells[0], x.Cells[1])); exits.Remove(sideTo(x.Cells[last], x.Cells[last - 1])); }
            if (entries.Count == 0 || exits.Count == 0) { refuse("no bank of land component " + (entries.Count == 0 ? x.LandA : x.LandB) + " beside the crossing"); return null; }
            var spec = new BridgeSpec { Kind = x.Kind, LandA = x.LandA, LandB = x.LandB, Cells = x.Cells.Select(c => new[] { c[0], t.Wrap(c[1]) }).ToList() };
            spec.Subjects.Add(x.Subject);
            int bestIn = -1, bestOut = -1, bestScore = int.MaxValue;
            foreach (int a in entries) foreach (int b in exits)
            {
                int lastIn = last == 0 ? a : (sideTo(x.Cells[last], x.Cells[last - 1]));
                if (b == lastIn) continue;
                if (last == 0 && a == b) continue;
                // Prefer straight passages in the end cells.
                int firstOut = last == 0 ? b : sideTo(x.Cells[0], x.Cells[1]);
                int score = ((a + 2) % 4 == firstOut ? 0 : 10) + ((lastIn + 2) % 4 == b ? 0 : 10) + a * 4 + b;
                if (score < bestScore) { bestScore = score; bestIn = a; bestOut = b; }
            }
            if (bestIn < 0) { refuse("the crossing cannot enter and leave a cell through different sides"); return null; }
            for (int i = 0; i <= last; i++)
            {
                int sIn = i == 0 ? bestIn : sideTo(x.Cells[i], x.Cells[i - 1]);
                int sOut = i == last ? bestOut : sideTo(x.Cells[i], x.Cells[i + 1]);
                if (sIn == sOut) { refuse("cell " + i + " of the crossing is entered and left through one side"); return null; }
                spec.Sides.Add(new[] { sIn, sOut });
            }
            spec.BankA = new[] { x.Cells[0][0] + DR[bestIn], t.Wrap(x.Cells[0][1] + DC[bestIn]) };
            spec.BankB = new[] { x.Cells[last][0] + DR[bestOut], t.Wrap(x.Cells[last][1] + DC[bestOut]) };
            return spec;
        }
    }
}
