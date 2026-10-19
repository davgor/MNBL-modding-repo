using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-38: validation of a built campaign navmesh (docs/runtime-navigation.md). Pure managed except through INavigationEngine.
    //   * NavigationModel: the installed War Sails navigation model (NavalPartyNavigationModel over DefaultPartyNavigationModel, built the way
    //     the campaign builds it): invalid terrain types per navigation type (the AI party capabilities Default, Naval and All), the region
    //     map, region switch costs, the embark threshold.
    //   * NavigationPlan (static, before anything is installed): every required point snapped within the policy radius in its intended domain
    //     and component, or refused; the mesh's land and water components against the travel graph's groups and basins; the route matrix
    //     (every MMR-28 host check, plus forbidden routes between groups and basins, plus All routes between groups) evaluated on the mesh.
    //     Any diagnostic refuses activation, naming the source ids.
    //   * NavigationValidator.Run (engine): the same matrix through actual engine calls in both directions (each periodic image of the
    //     target tried, as GeneratedMapScene's periodic queries do), with path lengths bounded by the mesh's own shortest path, exact region
    //     switch counts and, for bridges, the deck or under-bridge face the path must cross; the engine's face table against the mesh; the
    //     MMR-28 island members. The report is semantic (source ids, TerrainType names, rounded lengths) and bound to the world content id,
    //     the navmesh hash, the policy hash and the validator version. Native face indices are session-local and never reported.
    //   * NavigationGate: a world's report is persisted beside the store with its hash; activation of a content-addressed scene requires a
    //     report that is intact, activatable, of this validator version and policy, and names the scene and its installed navmesh hash.
    internal sealed class NavigationModel
    {
        public static readonly string[] Types = { "Default", "Naval", "All" };
        public string Source;
        public object Instance;   // the installed model object (in game), for the installed region-map helper
        public Dictionary<string, int[]> Invalid = new Dictionary<string, int[]>(StringComparer.Ordinal);
        public bool[] RegionMap;
        public int CostToSea, CostToLand;
        public double EmbarkRadius, CostLimit;

        public bool Valid(int group, string type) { return group >= 0 && Array.IndexOf(Invalid[type], group) < 0; }
        public bool IsLand(int group) { return group >= 0 && group < RegionMap.Length && RegionMap[group]; }

        // The installed types, constructed as NavalDLC registers them over the base model. Reflection only: no campaign is needed.
        public static NavigationModel Installed(IDictionary<string, object> calibration)
        {
            Func<string, string, Type> type = (assembly, name) =>
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly) ?? Assembly.Load(assembly);
                var t = loaded.GetType(name, false); if (t == null) throw new TypeLoadException(name + " is not in the installed " + assembly + ".");
                return t;
            };
            var baseType = type("TaleWorlds.CampaignSystem", "TaleWorlds.CampaignSystem.GameComponents.DefaultPartyNavigationModel");
            var navalType = type("NavalDLC", "NavalDLC.GameComponents.NavalPartyNavigationModel");
            var navigationType = type("TaleWorlds.CampaignSystem", "TaleWorlds.CampaignSystem.Party.MobileParty+NavigationType");
            var instance = Activator.CreateInstance(navalType, Activator.CreateInstance(baseType));
            var model = new NavigationModel { Source = navalType.FullName + " over " + baseType.FullName, Instance = instance };
            var invalid = navalType.GetMethod("GetInvalidTerrainTypesForNavigationType");
            var valid = navalType.GetMethod("IsTerrainTypeValidForNavigationType");
            foreach (string name in Types) model.Invalid[name] = ((int[])invalid.Invoke(instance, new[] { Enum.Parse(navigationType, name) })).OrderBy(v => v).ToArray();
            var terrain = typeof(TaleWorlds.Core.TerrainType);
            model.RegionMap = Enumerable.Range(0, TerrainTypes.Max + 1).Select(t => (bool)valid.Invoke(instance, new[] { Enum.ToObject(terrain, t), Enum.Parse(navigationType, "Default") })).ToArray();
            var embark = navalType.GetMethod("GetEmbarkDisembarkThresholdDistance", Type.EmptyTypes);
            model.EmbarkRadius = embark != null ? Convert.ToDouble(embark.Invoke(instance, null), CultureInfo.InvariantCulture) : Json.D(calibration, "embarkDisembarkThreshold");
            var costs = Json.O(calibration, "regionSwitchCost");
            model.CostToSea = Json.I(costs, "landToSea"); model.CostToLand = Json.I(costs, "seaToLand");
            model.CostLimit = 1e9;
            return model;
        }

        // The MMR-17 observed campaign values (worldgen/calibration/naval-transitions; embedded in the module).
        public static IDictionary<string, object> InstalledCalibration()
        {
            using (var stream = typeof(NavigationModel).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld.naval-transitions.json"))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded naval-transitions calibration.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false))) return (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd().TrimStart('﻿'));
            }
        }
        public static NavigationModel FromCalibration(IDictionary<string, object> calibration)
        {
            var model = new NavigationModel { Source = "calibration " + Json.S(calibration, "schema") };
            var sets = Json.O(calibration, "invalidTerrain");
            foreach (string name in Types) model.Invalid[name] = Json.A(sets, name).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).OrderBy(v => v).ToArray();
            model.RegionMap = Enumerable.Range(0, TerrainTypes.Max + 1).Select(t => model.Valid(t, "Default") && TerrainTypes.Name(t) != null).ToArray();
            var costs = Json.O(calibration, "regionSwitchCost");
            model.CostToSea = Json.I(costs, "landToSea"); model.CostToLand = Json.I(costs, "seaToLand");
            model.EmbarkRadius = Json.D(calibration, "embarkDisembarkThreshold"); model.CostLimit = 1e9;
            return model;
        }

        public bool SameAs(NavigationModel other)
        {
            return Types.All(t => Invalid[t].SequenceEqual(other.Invalid[t])) && CostToSea == other.CostToSea && CostToLand == other.CostToLand && Math.Abs(EmbarkRadius - other.EmbarkRadius) < 1e-6;
        }
        // Region map entries for defined TerrainType values only (undefined ids are never painted).
        public bool SameRegions(bool[] other)
        {
            return Enumerable.Range(0, Math.Max(RegionMap.Length, other.Length)).Where(t => TerrainTypes.Name(t) != null).All(t => (t < RegionMap.Length && RegionMap[t]) == (t < other.Length && other[t]));
        }
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["source"] = Source, ["invalid"] = Types.ToDictionary(t => t, t => (object)Invalid[t].Select(v => TerrainTypes.Name(v) + " " + v).ToArray()),
                ["landRegion"] = Enumerable.Range(0, RegionMap.Length).Where(t => RegionMap[t]).Select(t => TerrainTypes.Name(t) + " " + t).ToArray(),
                ["regionSwitchCostToSea"] = CostToSea, ["regionSwitchCostToLand"] = CostToLand, ["embarkRadius"] = EmbarkRadius };
        }
    }

    internal sealed class MeshTopology
    {
        public readonly NavMesh Mesh;
        public readonly List<int>[] Adjacent;
        public MeshTopology(NavMesh mesh)
        {
            Mesh = mesh; Adjacent = Enumerable.Range(0, mesh.FaceCount).Select(_ => new List<int>()).ToArray();
            var users = new Dictionary<int, int>();
            for (int f = 0; f < mesh.FaceCount; f++)
                foreach (int e in mesh.FaceEdges[f])
                {
                    int other; if (users.TryGetValue(e, out other)) { Adjacent[f].Add(other); Adjacent[other].Add(f); } else users[e] = f;
                }
        }
        public int[] Components(Func<int, bool> member)
        {
            var label = Enumerable.Repeat(-1, Mesh.FaceCount).ToArray(); int next = 0;
            for (int f = 0; f < Mesh.FaceCount; f++)
            {
                if (label[f] >= 0 || !member(f)) continue;
                var queue = new Queue<int>(); queue.Enqueue(f); label[f] = next;
                while (queue.Count > 0) { int g = queue.Dequeue(); foreach (int h in Adjacent[g]) if (label[h] < 0 && member(h)) { label[h] = next; queue.Enqueue(h); } }
                next++;
            }
            return label;
        }
        // The face of the given region at a physical point (region-strict, as Scene.GetNavMeshFaceIndex answers), or -1.
        public int Locate(double x, double y, bool land, NavigationModel model)
        {
            foreach (int f in Mesh.FacesAt(x, y, g => model.IsLand(g) == land)) return f;
            return -1;
        }
    }

    internal sealed class NavPoint
    {
        public string Id, Kind;
        public string[] SourceIds;
        public bool Land;
        public int Component;          // land component (land points) or basin (sea points)
        public double[] Requested, Resolved;
        public string Snap;
        public double SnapDistance;
        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["id"] = Id, ["kind"] = Kind, ["sourceIds"] = SourceIds, ["domain"] = Land ? "land" : "sea", ["component"] = Component,
                ["requested"] = Round(Requested), ["resolved"] = Resolved == null ? null : Round(Resolved), ["snap"] = Snap, ["snapDistance"] = Math.Round(SnapDistance, 4) };
        }
        internal static double[] Round(double[] p) { return p == null ? null : p.Select(v => Math.Round(v, 4)).ToArray(); }
    }

    internal sealed class FaceCheck
    {
        public string Id, Check, Subject;
        public string[] SourceIds = new string[0], Expect = new string[0];   // TerrainType names; empty = no face of that region
        public double[] Point;   // logical
        public bool Land, Gating = true, OnIsland, PhysicalPoint;
        public int StaticFace = -1;
        public string StaticType;
    }

    internal sealed class RouteCheck
    {
        public string Id, Check, Subject, Type;
        public string[] SourceIds = new string[0], Cross = new string[0];
        public double[] From, To;   // logical
        public bool FromLand, ToLand, Expected, Gating = true;
        public int MinTransitions = -1, MaxTransitions = -1;
        public double MaxDistance = -1, MaxFactor = -1, MinFactor = -1;
        public double[] DetourCells;
        public int[] Excluded;   // null: the navigation type's invalid terrain types
        public List<int[]> Through;   // logical cells the path must cross on a face of ThroughGroup in the ThroughLand region (bridges)
        public int ThroughGroup = -1;
        public bool ThroughLand;
        public bool StaticReachable;
        public double StaticLength = double.NaN;   // the mesh's own shortest path (MeshNavigationEngine), the bound for the engine's
    }

    internal sealed class NavigationPlan
    {
        public NavigationBuild Build;
        public NavigationModel Model;
        public MeshTopology Topology;
        public Dictionary<string, int[]> Components = new Dictionary<string, int[]>(StringComparer.Ordinal);
        public List<NavPoint> Points = new List<NavPoint>();
        public List<FaceCheck> Faces = new List<FaceCheck>();
        public List<RouteCheck> Routes = new List<RouteCheck>();
        public List<string> IslandMembers = new List<string>();   // point ids that must share one engine island (MMR-28 "islands")
        public List<NavigationDiagnostic> Diagnostics = new List<NavigationDiagnostic>();
        public List<string> Notes = new List<string>();
        public bool Activatable { get { return Diagnostics.Count == 0; } }
        private Dictionary<string, NavPoint> byId = new Dictionary<string, NavPoint>(StringComparer.Ordinal);

        private TravelCells T { get { return Build.Input.Cells; } }
        private WorldSurface S { get { return Build.Input.Surface; } }
        private double Width { get { return T.Cols * T.Spacing; } }
        // Logical (x wraps) to the canonical physical position (MMR-64: PeriodicWorldCoordinates.ToPhysical).
        public double[] Physical(double[] logical) { return Coordinates.ToPhysical(logical[0], logical[1]); }
        // MMR-64: a route's placements inside the terrain, as (start, target) physical pairs in PeriodicWorldCoordinates.Placements order (nearest
        // displacement first, ties east; the start moved by whole widths when the target's copy lies outside). Routes try every placement,
        // without pruning, and keep the shortest found path (MMR-38's rule); GeneratedMapScene keeps the cheapest path cost
        // (PeriodicDistanceModel.ShortestOverImages), so the two pick the same placement unless path length and cost disagree.
        public List<double[][]> Placements(double[] fromPhysical, double[] toLogical)
        {
            var p = Physical(toLogical);
            return Coordinates.Placements(fromPhysical[0], p[0]).Select(q => new[] { new[] { q.FromX, fromPhysical[1] }, new[] { q.TargetX, p[1] } }).ToList();
        }
        private PeriodicWorldCoordinates coordinates;
        // The bundle's periodic coordinate contract (MMR-64), from its projection layout.
        public PeriodicWorldCoordinates Coordinates { get { return coordinates ?? (coordinates = PeriodicWorldCoordinates.FromSurface(S)); } }
        // The logical cell of a physical position (x wraps).
        public int[] LogicalCell(double x, double y) { return T.CellOf(x - S.Strip, y - S.PoleMargin); }
        public double[] CellCentre(int r, int c) { return new[] { (T.Wrap(c) + .5) * T.Spacing, (r + .5) * T.Spacing }; }
        public NavPoint Point(string id) { NavPoint p; return byId.TryGetValue(id, out p) ? p : null; }

        public static NavigationPlan Create(NavigationBuild build, NavigationModel model)
        {
            var plan = new NavigationPlan { Build = build, Model = model, Topology = new MeshTopology(build.Mesh) };
            plan.Diagnostics.AddRange(build.Diagnostics);
            foreach (string type in NavigationModel.Types) { string t = type; plan.Components[t] = plan.Topology.Components(f => model.Valid(build.Mesh.Groups[f], t)); }
            // The mesh's face connectivity, with which engine islands (FaceIslandIndex) are compared: in game a pocket closed by mountain faces
            // shared its surroundings' island (MMR-38), so engine islands are not All connectivity; reachability uses the per-type components.
            plan.Components["mesh"] = plan.Topology.Components(f => true);
            foreach (var kv in build.FaceGroups)
                if (!(kv.Key == "mountain" ? !model.Valid(kv.Value, "All") : kv.Key == "land" || kv.Key == "bridge" ? model.Valid(kv.Value, "Default") && model.IsLand(kv.Value) : model.Valid(kv.Value, "Naval") && !model.IsLand(kv.Value)))
                    plan.Diagnostics.Add(new NavigationDiagnostic("navigation.face_group_invalid", kv.Key, TerrainTypes.Name(kv.Value) + " is not valid for its domain under the installed navigation model"));
            if (build.Input.FixtureChecks != null) plan.AddFixture();
            else { plan.CheckTopology(); plan.AddPoints(); plan.AddHostChecks(); plan.AddForbidden(); }
            plan.EvaluateStatically();
            return plan;
        }

        // ---- topology against the travel graph ----
        private int CellFace(int r, int c, bool land)
        {
            var p = Physical(CellCentre(r, c));
            return Topology.Locate(p[0], p[1], land, Model);
        }
        // Mesh components joined across their periodic images: a landmass or basin across the seam is one component in two images (the
        // canonical cells on either side of the seam lie in different images); every cell's two images are the same place.
        private Func<int, int> PeriodicRoot(string type, bool land)
        {
            var parent = new Dictionary<int, int>(); var comp = Components[type];
            Func<int, int> find = null;
            find = x => { int p; if (!parent.TryGetValue(x, out p)) { parent[x] = x; return x; } if (p == x) return x; int root = find(p); parent[x] = root; return root; };
            for (int i = 0; i < T.Classes.Length; i++)
            {
                var p = Physical(CellCentre(i / T.Cols, i % T.Cols)); double other = p[0] + (p[0] - S.Strip < Width / 2 ? Width : -Width);
                if (other < 0 || other > S.Extent) continue;
                int fa = Topology.Locate(p[0], p[1], land, Model), fb = Topology.Locate(other, p[1], land, Model);
                if (fa < 0 || fb < 0 || comp[fa] < 0 || comp[fb] < 0) continue;
                int ra = find(comp[fa]), rb = find(comp[fb]); if (ra != rb) parent[ra] = rb;
            }
            return x => x < 0 ? -1 : find(x);
        }
        private void CheckTopology()
        {
            var input = Build.Input; var t = T;
            Action<string, string, string[]> fail = (subject, detail, ids) => Diagnostics.Add(new NavigationDiagnostic("navigation.topology_mismatch", subject, detail, ids));
            for (int k = 0; k < TravelCells.ClassNames.Length; k++)
            {
                int expected; input.ClassCounts.TryGetValue(TravelCells.ClassNames[k], out expected);
                int actual = t.Classes.Count(v => v == k);
                if (actual != expected) fail("grid", TravelCells.ClassNames[k] + " cells " + actual + " differ from the travel graph's " + expected, null);
            }
            if (!Build.OpenSeaComputed.SequenceEqual(t.OpenSea)) fail("grid", "the OpenSea flags differ from the MMR-28 rule recomputed (" + Build.OpenSeaComputed.Zip(t.OpenSea, (a, b) => a != b ? 1 : 0).Sum() + " cells)", null);
            int lands = Build.LandOf.Max() + 1;
            if (lands != input.LandComponentCells.Count) fail("landComponents", lands + " land components, the travel graph has " + input.LandComponentCells.Count, null);
            for (int id = 0; id < lands; id++)
            {
                int expected; int actual = Build.LandOf.Count(l => l == id);
                if (!input.LandComponentCells.TryGetValue(id, out expected) || expected != actual) fail("land component " + id, actual + " cells, the travel graph has " + expected, null);
            }
            int basins = Build.BasinOf.Max() + 1;
            if (basins != input.Basins.Count) fail("basins", basins + " basins, the travel graph has " + input.Basins.Count, null);
            foreach (var b in input.Basins) { int actual = Build.BasinOf.Count(x => x == b.Id); if (actual != b.Cells) fail("basin " + b.Id, actual + " cells, the travel graph has " + b.Cells, null); }
            if (!Diagnostics.Any(d => d.Code == "navigation.topology_mismatch" && d.Subject.StartsWith("land", StringComparison.Ordinal)))
            {
                // Default components realize the groups (land components joined by bridges): one per group, never shared.
                var componentOfGroup = new Dictionary<int, HashSet<int>>(); var groupOfComponent = new Dictionary<int, HashSet<int>>();
                var groupOfLand = new Dictionary<int, int>(); var landRoot = PeriodicRoot("Default", true); var seaRoot = PeriodicRoot("Naval", false);
                foreach (var g in input.Groups) foreach (int l in g.Lands) groupOfLand[l] = g.Id;
                for (int i = 0; i < t.Classes.Length; i++)
                {
                    if (t.Classes[i] != TravelCells.Land) continue;
                    int group; if (!groupOfLand.TryGetValue(Build.LandOf[i], out group)) { fail("land component " + Build.LandOf[i], "belongs to no travel group", null); continue; }
                    int face = CellFace(i / t.Cols, i % t.Cols, true);
                    int comp = face < 0 ? -1 : landRoot(Components["Default"][face]);
                    if (comp < 0) { fail("cell " + (i / t.Cols) + "," + (i % t.Cols), "land cell without a Default-valid land face", null); continue; }
                    Add(componentOfGroup, group, comp); Add(groupOfComponent, comp, group);
                }
                foreach (var kv in componentOfGroup.Where(kv => kv.Value.Count != 1))
                {
                    // Source ids: the group's joining crossings and the settlements cut off from the group's largest component.
                    var groupLands = input.Groups.First(g => g.Id == kv.Key).Lands;
                    Func<int, int, int> compOf = (r, c) => { int face = CellFace(r, c, true); return face < 0 ? -1 : landRoot(Components["Default"][face]); };
                    var sizes = new Dictionary<int, int>();
                    for (int i = 0; i < t.Classes.Length; i++) if (t.Classes[i] == TravelCells.Land && groupLands.Contains(Build.LandOf[i])) { int comp = compOf(i / t.Cols, i % t.Cols); int n; sizes.TryGetValue(comp, out n); sizes[comp] = n + 1; }
                    int largest = sizes.OrderByDescending(x => x.Value).First().Key;
                    var cut = input.Settlements.Where(x => x.Group == kv.Key && !input.Excluded.Contains(x.Id) && compOf(x.Row, x.Col) != largest).Select(x => x.Id);
                    var subjects = input.Crossings.Where(x => groupLands.Contains(x.LandA) || groupLands.Contains(x.LandB)).Select(x => x.Subject)
                        .Concat(input.HostChecks.Where(h => h.Check == "bridge").Select(h => h.Subject).Where(x => !Build.Bridges.Any(b => b.Subjects.Contains(x))));
                    Diagnostics.Add(new NavigationDiagnostic("navigation.group_not_realized", "group " + kv.Key, "the group's land is " + kv.Value.Count + " separate Default components (a joining bridge is not realized)",
                        subjects.Concat(cut).ToArray()));
                }
                foreach (var kv in groupOfComponent.Where(kv => kv.Value.Count != 1))
                    Diagnostics.Add(new NavigationDiagnostic("navigation.groups_merged", "groups " + String.Join(", ", kv.Value), "separate travel groups share one Default component (an unintended land connection)"));
                // Naval components realize the basins.
                var componentOfBasin = new Dictionary<int, HashSet<int>>(); var basinOfComponent = new Dictionary<int, HashSet<int>>();
                for (int i = 0; i < t.Classes.Length; i++)
                {
                    if (!TravelCells.Water(t.Classes[i])) continue;
                    int face = CellFace(i / t.Cols, i % t.Cols, false);
                    int comp = face < 0 ? -1 : seaRoot(Components["Naval"][face]);
                    if (comp < 0) { fail("cell " + (i / t.Cols) + "," + (i % t.Cols), "water cell without a Naval-valid sea face", null); continue; }
                    Add(componentOfBasin, Build.BasinOf[i], comp); Add(basinOfComponent, comp, Build.BasinOf[i]);
                }
                foreach (var kv in componentOfBasin.Where(kv => kv.Value.Count != 1)) fail("basin " + kv.Key, "the basin is " + kv.Value.Count + " separate Naval components", null);
                foreach (var kv in basinOfComponent.Where(kv => kv.Value.Count != 1)) fail("basins " + String.Join(", ", kv.Value), "separate basins share one Naval component (an unintended water connection)", null);
            }
        }
        private static void Add(Dictionary<int, HashSet<int>> map, int key, int value) { HashSet<int> set; if (!map.TryGetValue(key, out set)) map[key] = set = new HashSet<int>(); set.Add(value); }

        // ---- required points ----
        private void AddPoints()
        {
            var input = Build.Input; var t = T; var policy = Build.Policy;
            foreach (var s in input.Settlements.Where(s => !input.Excluded.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var cell = t.CellOf(s.X, s.Y);
                if (cell[0] != s.Row || cell[1] != t.Wrap(s.Col))
                    Diagnostics.Add(new NavigationDiagnostic("navigation.point_inconsistent", "settlement " + s.Id, "the emitted point lies in cell " + cell[0] + "," + cell[1] + ", its record names cell " + s.Row + "," + s.Col, s.Id, s.Uid));
                AddPoint(new NavPoint { Id = s.Id, Kind = s.Kind, SourceIds = new[] { s.Id, s.Uid }, Land = true, Component = s.Land, Requested = new[] { s.X, s.Y } });
            }
            // Settlements sharing one emitted point are separated along x inside their cell (MMR-28 distinctPositions).
            foreach (var shared in input.PositionCollisions)
            {
                var members = shared.Select(Point).Where(p => p != null).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
                for (int i = 0; i < members.Count; i++)
                {
                    var p = members[i]; double offset = (i - (members.Count - 1) / 2.0) * policy.Separation * t.Spacing;
                    p.Requested = new[] { p.Requested[0] + offset, p.Requested[1] }; p.Kind += " (separated)";
                }
            }
            foreach (var x in input.Transitions)
            {
                string key = x.Kind == "port" ? x.Town : "group " + x.Group + " cell " + x.LandCell[0] + "," + x.LandCell[1];
                int land = t.InRows(x.LandCell[0]) ? Build.LandOf[t.Index(x.LandCell[0], x.LandCell[1])] : -1;
                var group = input.Groups.FirstOrDefault(g => g.Id == x.Group);
                if (group == null || !group.Lands.Contains(land))
                    Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", x.Kind + " transition " + key, "the land cell " + x.LandCell[0] + "," + x.LandCell[1] + " (land component " + land + ") is not in group " + x.Group, x.Town));
                AddPoint(new NavPoint { Id = (x.Kind == "port" ? "portEntrance:" : "coastLand:") + key, Kind = x.Kind + " land entrance", SourceIds = new[] { x.Town }, Land = true, Component = land, Requested = x.LandPoint });
                AddPoint(new NavPoint { Id = (x.Kind == "port" ? "port:" : "coastSea:") + key, Kind = x.Kind + " sea point", SourceIds = new[] { x.Town }, Land = false, Component = x.Basin, Requested = x.SeaPoint });
            }
            foreach (var p in Points) Snap(p);
            // A port point is placed at the interior point of its port cell nearest its town's gate: the travel graph bounds the port and gate
            // cells (maxPortGateCells), not the points, and the port-to-gate distance is checked against the envelope (portGate).
            foreach (var x in input.Transitions.Where(x => x.Kind == "port"))
            {
                var port = Point("port:" + x.Town); var gate = Point(x.Town);
                if (port == null || gate == null || port.Resolved == null || gate.Resolved == null) continue;
                double s = t.Spacing, margin = policy.Interior * s;
                var cell = t.CellOf(port.Resolved[0], port.Resolved[1]);
                double x0 = Math.Floor(port.Resolved[0] / s) * s, y0 = cell[0] * s;
                double gx = gate.Resolved[0]; if (gx - port.Resolved[0] > Width / 2) gx -= Width; else if (port.Resolved[0] - gx > Width / 2) gx += Width;
                var placed = new[] { Math.Max(x0 + margin, Math.Min(x0 + s - margin, gx)), Math.Max(y0 + margin, Math.Min(y0 + s - margin, gate.Resolved[1])) };
                port.SnapDistance = Distance(placed, port.Requested); port.Resolved = new[] { ((placed[0] % Width) + Width) % Width, placed[1] }; port.Snap = "placed toward its gate";
            }
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Points.Where(p => p.Resolved != null && input.Settlements.Any(s => s.Id == p.Id)))
            {
                string key = p.Resolved[0].ToString("R", CultureInfo.InvariantCulture) + "," + p.Resolved[1].ToString("R", CultureInfo.InvariantCulture); string other;
                if (resolved.TryGetValue(key, out other)) Diagnostics.Add(new NavigationDiagnostic("navigation.point_shared", "settlements " + other + ", " + p.Id, "two settlements resolve to one point", other, p.Id));
                else resolved[key] = p.Id;
            }
        }
        private void AddPoint(NavPoint p) { if (!byId.ContainsKey(p.Id)) { Points.Add(p); byId[p.Id] = p; } }

        // Kept when the point's cell is in its intended domain and component; otherwise moved to the nearest interior point of such a cell
        // within the policy radius; otherwise refused.
        private void Snap(NavPoint p)
        {
            var t = T; var policy = Build.Policy; double s = t.Spacing, radius = policy.SnapRadius * s, margin = policy.Interior * s;
            Func<int, int, bool> intended = (r, c) =>
            {
                if (!t.InRows(r)) return false;
                int i = t.Index(r, c); var cell = Build.Cells[i];
                return p.Land ? cell.Class == TravelCells.Land && Build.LandOf[i] == p.Component : TravelCells.Water(cell.Class) && cell.Passage != "underBridge" && Build.BasinOf[i] == p.Component;
            };
            var home = t.CellOf(p.Requested[0], p.Requested[1]);
            double[] best = null; double bestDistance = double.MaxValue;
            int reach = (int)Math.Ceiling(radius / s) + 1;
            for (int dr = -reach; dr <= reach; dr++) for (int dc = -reach; dc <= reach; dc++)
            {
                int r = home[0] + dr, c = home[1] + dc;
                if (!intended(r, c)) continue;
                double x0 = (Math.Floor(p.Requested[0] / s) + dc) * s, y0 = r * s;   // unwrapped beside the request
                double x =Math.Max(x0 + margin, Math.Min(x0 + s - margin, p.Requested[0])), y = Math.Max(y0 + margin, Math.Min(y0 + s - margin, p.Requested[1]));
                double d = Math.Sqrt((x - p.Requested[0]) * (x - p.Requested[0]) + (y - p.Requested[1]) * (y - p.Requested[1]));
                if (d < bestDistance - 1e-12) { bestDistance = d; best = new[] { x, y }; }
            }
            if (best != null && bestDistance <= radius + 1e-9)
            {
                p.Resolved = new[] { ((best[0] % Width) + Width) % Width, best[1] }; p.SnapDistance = bestDistance;
                p.Snap = p.Kind.EndsWith("(separated)", StringComparison.Ordinal) ? "separated" : bestDistance == 0 ? "kept" : "moved";
                return;
            }
            p.Snap = "refused";
            string where = !t.InRows(home[0]) ? "beyond the pole lines" : p.Land
                ? (Build.Cells[t.Index(home[0], home[1])].Class == TravelCells.Land ? "in land component " + Build.LandOf[t.Index(home[0], home[1])] + ", not its own " + p.Component : "in a " + TravelCells.ClassNames[Build.Cells[t.Index(home[0], home[1])].Class] + " cell")
                : (TravelCells.Water(Build.Cells[t.Index(home[0], home[1])].Class) ? "in basin " + Build.BasinOf[t.Index(home[0], home[1])] + ", not its own " + p.Component : "in a " + TravelCells.ClassNames[Build.Cells[t.Index(home[0], home[1])].Class] + " cell");
            Diagnostics.Add(new NavigationDiagnostic("navigation.snap_refused", p.Kind + " " + p.Id, "the requested point lies " + where + "; no " + (p.Land ? "land of component " : "water of basin ") + p.Component
                + " within " + radius.ToString("0.###", CultureInfo.InvariantCulture) + " units (a snap ends only in its own domain and component)", p.SourceIds));
        }

        // ---- the route matrix from the travel graph ----
        private string LandName { get { return TerrainTypes.Name(Build.FaceGroups["land"]); } }
        private void Face(string check, string subject, string id, double[] point, bool land, string[] expect, string[] sources, bool island = false, bool gating = true, bool physical = false)
        {
            if (point == null) return;
            Faces.Add(new FaceCheck { Id = id, Check = check, Subject = subject, Point = point, Land = land, Expect = expect, SourceIds = sources ?? new string[0], OnIsland = island, Gating = gating, PhysicalPoint = physical });
        }
        private void Route(string check, string subject, string id, NavPoint from, NavPoint to, string type, bool expected, bool bothWays, Action<RouteCheck> configure = null)
        {
            if (from == null || to == null) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", id, "a required route names a point the travel graph does not define", subject)); return; }
            if (from.Resolved == null || to.Resolved == null) return;   // navigation.snap_refused names it
            Route(check, subject, id, from.Resolved, from.Land, to.Resolved, to.Land, type, expected, bothWays, from.SourceIds.Concat(to.SourceIds).ToArray(), configure);
        }
        private void Route(string check, string subject, string id, double[] from, bool fromLand, double[] to, bool toLand, string type, bool expected, bool bothWays, string[] sources, Action<RouteCheck> configure = null)
        {
            var r = new RouteCheck { Id = id, Check = check, Subject = subject, Type = type, From = from, FromLand = fromLand, To = to, ToLand = toLand, Expected = expected, SourceIds = sources };
            if (configure != null) configure(r);
            Routes.Add(r);
            if (bothWays)
            {
                var back = new RouteCheck { Id = id + " (back)", Check = check, Subject = subject, Type = type, From = to, FromLand = toLand, To = from, ToLand = fromLand, Expected = expected, SourceIds = sources };
                if (configure != null) configure(back);
                Routes.Add(back);
            }
        }
        private void AddHostChecks()
        {
            var input = Build.Input; var t = T; string land = LandName;
            var towns = input.Settlements.Where(s => s.Kind == "town" && !input.Excluded.Contains(s.Id)).ToDictionary(s => s.Id, StringComparer.Ordinal);
            var byUid = input.Settlements.ToDictionary(s => s.Uid ?? s.Id, StringComparer.Ordinal);
            var playerSafe = new HashSet<int>(input.Groups.Where(g => g.PlayerSafe).Select(g => g.Id));
            foreach (var h in input.HostChecks)
            {
                switch (h.Check)
                {
                    case "gateFace":
                    case "settlementFace":
                    {
                        var p = Point(h.Subject);
                        if (p == null) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Check + " " + h.Subject, "the host check names a settlement with no required point", h.Subject)); break; }
                        var record = input.Settlements.First(s => s.Id == h.Subject);
                        Face(h.Check, h.Subject, h.Check + " " + h.Subject, p.Resolved, true, new[] { land }, p.SourceIds, record.Kind != "village" && playerSafe.Contains(record.Group));
                        if (record.Kind != "village" && playerSafe.Contains(record.Group)) IslandMembers.Add(p.Id);
                        break;
                    }
                    case "villageRoute":
                    {
                        string town; if (!input.VillageTown.TryGetValue(h.Subject, out town)) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", "villageRoute " + h.Subject, "the village has no bound town", h.Subject)); break; }
                        Route(h.Check, h.Subject, "villageRoute " + h.Subject, Point(h.Subject), Point(town), "Default", true, true);
                        break;
                    }
                    case "landRoute":
                    {
                        var road = input.Roads.FirstOrDefault(r => "road " + r.Index == h.Subject);
                        if (road == null) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Check + " " + h.Subject, "the host check names a road the travel graph does not define")); break; }
                        Route(h.Check, h.Subject, "landRoute " + h.Subject, Point(road.From), Point(road.To), "Default", true, true);
                        break;
                    }
                    case "portFace":
                    case "seamTransition":
                    {
                        // A coast transition on a seam column ("coast group <g>"): every coast transition is routed below (coastTransition),
                        // this one with them. Only a port's seam transition is routed here (seed 43's world: input_inconsistent before).
                        if (h.Check == "seamTransition" && h.Subject.StartsWith("coast ", StringComparison.Ordinal)) break;
                        string town = h.Check == "portFace" ? h.Subject : h.Subject.Replace("port ", "");
                        var port = Point("port:" + town); var entrance = Point("portEntrance:" + town);
                        if (port == null || entrance == null) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Check + " " + town, "the host check names a port with no transition", town)); break; }
                        if (h.Check == "portFace" && port.Resolved != null)
                        {
                            // The port cell's sea face from the travel cells themselves (MMR-28 proposes CoastalSea; a port cell of the open basin that is
                            // river or lake has that water's face), not from the builder's typing.
                            var cell = t.CellOf(port.Resolved[0], port.Resolved[1]); int i = t.Index(cell[0], cell[1]); byte k = t.Classes[i];
                            string expected = input.FaceGroupNames[k == TravelCells.Lake ? "lake" : k == TravelCells.River ? "river" : t.OpenSea[i] == 1 ? "openSea" : "coastalSea"];
                            Face(h.Check, town, "portFace " + town, port.Resolved, false, new[] { expected }, port.SourceIds, true); IslandMembers.Add(port.Id);
                        }
                        // The port shares an edge with its land entrance: exactly one embark/disembark between them, no detour.
                        Route(h.Check, town, h.Check + " transition " + town, entrance, port, "All", true, true, r => { r.MinTransitions = 1; r.MaxTransitions = 1; if (entrance.Resolved != null && port.Resolved != null) r.MaxDistance = Distance(entrance.Resolved, port.Resolved) + t.Spacing; });
                        break;
                    }
                    case "portGate":
                    {
                        var port = Point("port:" + h.Subject); var entrance = Point("portEntrance:" + h.Subject); var gate = Point(h.Subject);
                        Route(h.Check, h.Subject, "portGate land " + h.Subject, entrance, gate, "Default", true, true);
                        // GetPortToGateDistanceForSettlement reads the All navigation cache between the port and the gate (NavalDLCMapDistanceModel),
                        // a cost that includes the region switch cost (MMR-17: 55-56 for ports 5-6 units away). The envelope is checked on the
                        // path's length, with exactly one region switch.
                        Route(h.Check, h.Subject, "portGate distance " + h.Subject, port, gate, "All", true, false, r => { r.MaxDistance = input.MaxPortGateUnits; r.MinTransitions = 1; r.MaxTransitions = 1; });
                        break;
                    }
                    case "bridge":
                    {
                        var bridge = Build.Bridges.FirstOrDefault(b => b.Subjects.Contains(h.Subject));
                        if (bridge == null) { Diagnostics.Add(new NavigationDiagnostic("navigation.bridge_missing", h.Subject, "the travel graph requires a bridge here and the navmesh has no deck for it", h.Subject)); break; }
                        foreach (var c in bridge.Cells)
                        {
                            var at = DeckPoint(c[0], c[1]);
                            Face(h.Check, h.Subject, "bridge deck " + h.Subject + " cell " + c[0] + "," + c[1], at, true, new[] { TerrainTypes.Name(Build.FaceGroups["bridge"]) }, new[] { h.Subject });
                            Face(h.Check, h.Subject, "bridge under " + h.Subject + " cell " + c[0] + "," + c[1], at, false, new[] { TerrainTypes.Name(Build.FaceGroups["underBridge"]) }, new[] { h.Subject });
                        }
                        int deckGroup = Build.FaceGroups["bridge"];
                        Route(h.Check, h.Subject, "bridge crossing " + h.Subject, CellCentre(bridge.BankA[0], bridge.BankA[1]), true, CellCentre(bridge.BankB[0], bridge.BankB[1]), true, "Default", true, true, new[] { h.Subject },
                            r => { r.Through = bridge.Cells; r.ThroughGroup = deckGroup; r.ThroughLand = true; });
                        AddUnderBridgeRoute(bridge, h.Subject);
                        break;
                    }
                    case "pass":
                    case "footprint":
                    {
                        var parts = h.Subject.Replace("cell ", "").Split(','); int r = Int32.Parse(parts[0], CultureInfo.InvariantCulture), c = Int32.Parse(parts[1], CultureInfo.InvariantCulture);
                        Face(h.Check, h.Subject, h.Check + " " + h.Subject, CellCentre(r, c), true, new[] { land }, new[] { h.Subject });
                        break;
                    }
                    case "seaRoute":
                    {
                        int group = Int32.Parse(h.Subject.Replace("group ", ""), CultureInfo.InvariantCulture);
                        var ports = input.Transitions.Where(x => x.Kind == "port" && x.Group == group).OrderBy(x => x.Town, StringComparer.Ordinal).ToList();
                        if (ports.Count == 0) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Subject, "a sea route is required but the group has no port", h.Subject)); break; }
                        var from = Point("port:" + ports[0].Town);
                        var targets = input.Transitions.Where(x => x.Group == input.MainGroup && x.Basin == ports[0].Basin)
                            .Select(x => Point(x.Kind == "port" ? "port:" + x.Town : "coastSea:group " + x.Group + " cell " + x.LandCell[0] + "," + x.LandCell[1])).Where(p => p != null && p.Resolved != null)
                            .OrderBy(p => from == null || from.Resolved == null ? 0 : Distance(from.Resolved, p.Resolved)).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
                        if (targets.Count == 0) { Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Subject, "no coast of the main group on the port's basin " + ports[0].Basin)); break; }
                        Route(h.Check, h.Subject, "seaRoute " + h.Subject, from, targets[0], "Naval", true, true);
                        break;
                    }
                    case "distinctPositions":
                        foreach (var id in h.Subject.Split(',').Select(x => x.Trim())) { var p = Point(id); if (p != null && !p.Kind.EndsWith("(separated)", StringComparison.Ordinal)) Diagnostics.Add(new NavigationDiagnostic("navigation.input_inconsistent", h.Check, "a listed settlement was not separated", id)); }
                        break;
                    case "openSea":
                    {
                        int basin = Int32.Parse(h.Subject.Replace("basin ", ""), CultureInfo.InvariantCulture);
                        var cells = Enumerable.Range(0, t.Classes.Length).Where(i => Build.BasinOf[i] == basin && t.OpenSea[i] == 1).ToList();
                        int step = Math.Max(1, cells.Count / 16);
                        for (int k = 0; k < cells.Count; k += step)
                            Face(h.Check, h.Subject, "openSea " + h.Subject + " cell " + (cells[k] / t.Cols) + "," + (cells[k] % t.Cols), CellCentre(cells[k] / t.Cols, cells[k] % t.Cols), false, new[] { TerrainTypes.Name(Build.FaceGroups["openSea"]) }, new[] { h.Subject });
                        if (cells.Count == 0)
                        {
                            // A basin with no OpenSea cell ("storms cannot spawn"): its water cells carry their own faces, none OpenSea (2026-10-19: 10 of
                            // 19 swept worlds have one; with no row the flag failed as unchecked).
                            var water = Enumerable.Range(0, t.Classes.Length).Where(i => Build.BasinOf[i] == basin && TravelCells.Water(t.Classes[i])).ToList();
                            int waterStep = Math.Max(1, water.Count / 16);
                            for (int k = 0; k < water.Count; k += waterStep)
                            {
                                byte c = t.Classes[water[k]];
                                Face(h.Check, h.Subject, "openSea " + h.Subject + " water cell " + (water[k] / t.Cols) + "," + (water[k] % t.Cols), CellCentre(water[k] / t.Cols, water[k] % t.Cols), false,
                                     new[] { input.FaceGroupNames[c == TravelCells.Lake ? "lake" : c == TravelCells.River ? "river" : "coastalSea"] }, new[] { h.Subject });
                            }
                        }
                        break;
                    }
                    case "islands":
                        foreach (var castle in input.Settlements.Where(s => s.Kind == "castle" && !input.Excluded.Contains(s.Id))) if (Point(castle.Id) != null) IslandMembers.Add(castle.Id);
                        break;
                    case "padding":
                        for (int k = 0; k < 8; k++)
                        {
                            double x = (k + .5) * Width / 8;
                            foreach (var y in new[] { -0.5 * t.Spacing, t.Rows * t.Spacing + 0.5 * t.Spacing })
                                foreach (bool l in new[] { true, false })
                                    Face(h.Check, h.Subject, "padding " + x.ToString("0.###", CultureInfo.InvariantCulture) + "," + y.ToString("0.###", CultureInfo.InvariantCulture) + (l ? " land" : " sea"), new[] { x, y }, l, new string[0], new[] { "grid" });
                        }
                        break;
                    default:
                        Diagnostics.Add(new NavigationDiagnostic("navigation.unknown_host_check", h.Check, "the travel graph emits a host check this validator does not run: " + h.Expected));
                        break;
                }
            }
            // Coast transitions (disembark candidates): the land entrance and the sea point share an edge, so exactly one region switch.
            foreach (var x in input.Transitions.Where(x => x.Kind == "coast"))
            {
                string key = "group " + x.Group + " cell " + x.LandCell[0] + "," + x.LandCell[1];
                var landPoint = Point("coastLand:" + key); var seaPoint = Point("coastSea:" + key);
                Route("coastTransition", key, "coastTransition " + key, landPoint, seaPoint, "All", true, true, r => { r.MinTransitions = 1; r.MaxTransitions = 1; if (landPoint != null && seaPoint != null && landPoint.Resolved != null && seaPoint.Resolved != null) r.MaxDistance = Distance(landPoint.Resolved, seaPoint.Resolved) + t.Spacing; });
            }
            Notes.Add("portFace: SetSailAtPosition is a campaign action (MMR-40 binds port entities; MMR-45 activates). portGate: GetPortToGateDistanceForSettlement reads the campaign's All navigation cache (MMR-41) and includes the region switch cost; the envelope is checked here on the engine path's length with one switch, and the placed port point (static.points) is the position the port entity must use. seamTransition: the transition across the seam column is routed here; the embark action at the seam is MMR-65's.");
        }
        // A logical point on the deck of a bridge cell: its canonical deck face's centroid (a deck entered and left through adjacent sides does
        // not cover the cell centre).
        private double[] DeckPoint(int r, int c)
        {
            int pr = T.RowOffset + r, pc = T.ColumnOffset + T.Wrap(c);
            List<int> faces; if (!Build.Mesh.CellFaces.TryGetValue(NavMesh.Key(pr, pc), out faces)) return CellCentre(r, c);
            int deck = faces.FirstOrDefault(f => Build.Mesh.Roles[f] == NavMesh.RoleDeck) ; if (Build.Mesh.Roles[deck] != NavMesh.RoleDeck) return CellCentre(r, c);
            var centroid = Build.Mesh.Centroid(deck);
            return new[] { centroid[0] - S.Strip, centroid[1] - S.PoleMargin };
        }
        // A Naval route under a deck cell whose water continues on two of its free sides, crossing the cell's UnderBridge face.
        private void AddUnderBridgeRoute(BridgeSpec bridge, string subject)
        {
            var t = T; int[] dr = { -1, 0, 1, 0 }, dc = { 0, 1, 0, -1 };
            for (int i = 0; i < bridge.Cells.Count; i++)
            {
                var cell = bridge.Cells[i]; var sides = bridge.Sides[i];
                var water = Enumerable.Range(0, 4).Where(k => k != sides[0] && k != sides[1] && t.InRows(cell[0] + dr[k]) && TravelCells.Water(t.At(cell[0] + dr[k], cell[1] + dc[k]))
                    && Build.Cells[t.Index(cell[0] + dr[k], cell[1] + dc[k])].Passage != "underBridge").ToList();
                // Opposite sides only: the straight line between their centres crosses the cell, so the shortest path must pass under the deck
                // (between adjacent sides it may round the corner through the diagonal cell).
                if (water.Count < 2 || (water[0] + 2) % 4 != water[1]) continue;
                int under = Build.FaceGroups["underBridge"];
                Route("bridge", subject, "bridge under route " + subject + " cell " + cell[0] + "," + cell[1], CellCentre(cell[0] + dr[water[0]], cell[1] + dc[water[0]]), false, CellCentre(cell[0] + dr[water[1]], cell[1] + dc[water[1]]), false, "Naval", true, true, new[] { subject },
                    r => { r.Through = new List<int[]> { cell }; r.ThroughGroup = under; r.ThroughLand = false; });
                return;
            }
            Notes.Add(subject + ": no deck cell has water on two opposite free sides; no under-bridge route");
        }

        // Forbidden routes between groups and basins, and the All routes between groups that the cell topology intends.
        private void AddForbidden()
        {
            var input = Build.Input; var t = T;
            Func<GroupRecord, double[]> rep = g =>
            {
                var town = input.Settlements.Where(s => s.Group == g.Id && !input.Excluded.Contains(s.Id)).OrderBy(s => s.Kind == "town" ? 0 : 1).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s => Point(s.Id)).FirstOrDefault(p => p != null && p.Resolved != null);
                if (town != null) return town.Resolved;
                for (int i = 0; i < t.Classes.Length; i++) if (t.Classes[i] == TravelCells.Land && g.Lands.Contains(Build.LandOf[i])) return CellCentre(i / t.Cols, i % t.Cols);
                return null;
            };
            var main = input.Groups.FirstOrDefault(g => g.Id == input.MainGroup);
            if (main == null) return;
            var mainRep = rep(main);
            // Intended All connectivity: groups joined through the water basins their land touches (cells only, independent of the mesh).
            var touch = new Dictionary<int, HashSet<int>>();
            var groupOfLand = new Dictionary<int, int>(); foreach (var g in input.Groups) foreach (int l in g.Lands) groupOfLand[l] = g.Id;
            for (int i = 0; i < t.Classes.Length; i++)
            {
                int group; if (t.Classes[i] != TravelCells.Land || !groupOfLand.TryGetValue(Build.LandOf[i], out group)) continue;
                foreach (int n in t.Neighbours(i / t.Cols, i % t.Cols)) if (TravelCells.Water(t.Classes[n])) Add(touch, group, Build.BasinOf[n]);
            }
            var reached = new HashSet<int> { main.Id }; var basinsReached = new HashSet<int>(); bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var g in input.Groups) { HashSet<int> b; if (reached.Contains(g.Id) && touch.TryGetValue(g.Id, out b)) foreach (int x in b) changed |= basinsReached.Add(x); }
                foreach (var g in input.Groups) { HashSet<int> b; if (!reached.Contains(g.Id) && touch.TryGetValue(g.Id, out b) && b.Overlaps(basinsReached)) { reached.Add(g.Id); changed = true; } }
            }
            foreach (var g in input.Groups.Where(g => g.Id != main.Id))
            {
                var r = rep(g); if (r == null || mainRep == null) continue;
                Route("forbidden", "group " + g.Id, "land to group " + g.Id + " (Default)", mainRep, true, r, true, "Default", false, true, new[] { "group " + main.Id, "group " + g.Id });
                bool all = reached.Contains(g.Id);
                // The connection needs the sea: at least one embark and one disembark (region switches, counted exactly from the engine's
                // cost). Where land cells of the two groups touch only diagonally, both switches happen at one vertex (a corner hop; reported).
                Route(all ? "transition" : "forbidden", "group " + g.Id, "land to group " + g.Id + " (All)", mainRep, true, r, true, "All", all, false, new[] { "group " + main.Id, "group " + g.Id }, x => { if (all) x.MinTransitions = 2; });
            }
            var mainPort = input.Transitions.Where(x => x.Kind == "port" && x.Group == main.Id).OrderBy(x => x.Town, StringComparer.Ordinal).Select(x => Point("port:" + x.Town)).FirstOrDefault(p => p != null && p.Resolved != null);
            if (mainPort == null) return;
            foreach (var b in input.Basins.Where(b => b.Id != mainPort.Component))
            {
                int i = Array.IndexOf(Build.BasinOf, b.Id); if (i < 0) continue;
                Route("forbidden", "basin " + b.Id, "sea to basin " + b.Id + " (Naval)", mainPort.Resolved, false, CellCentre(i / t.Cols, i % t.Cols), false, "Naval", false, false, new[] { mainPort.Id, "basin " + b.Id });
            }
            int open = Array.IndexOf(t.OpenSea, (byte)1);
            // Sanity only (MMR-17): each destination face is itself invalid for the navigation type.
            if (open >= 0 && mainRep != null) Route("sanity", "sanity", "land to open sea (Default)", mainRep, true, CellCentre(open / t.Cols, open % t.Cols), false, "Default", false, false, new[] { "group " + main.Id });
            if (mainRep != null) Route("sanity", "sanity", "sea to inland (Naval)", mainPort.Resolved, false, mainRep, true, "Naval", false, false, new[] { mainPort.Id });
        }

        private void AddFixture()
        {
            foreach (var c in Build.Input.FixtureChecks)
            {
                var from = c.Point ?? (c.From == null ? null : CellCentre(c.From[0], c.From[1]));
                if (c.Kind == "face") { Face(c.Group, c.Id, c.Id, c.Point ?? CellCentre(c.Cell[0], c.Cell[1]), c.Land, c.Expect, new[] { "fixture " + c.Id }, false, c.Gating, c.PhysicalPoint); continue; }
                Route(c.Group, c.Id, c.Id, from, c.FromLand, CellCentre(c.To[0], c.To[1]), c.ToLand, c.Type, c.Expected, false, new[] { "fixture " + c.Id }, r =>
                {
                    r.MinTransitions = c.MinTransitions; r.MaxTransitions = c.MaxTransitions; r.MaxFactor = c.MaxFactor; r.MinFactor = c.MinFactor; r.DetourCells = c.Detour; r.Cross = c.Cross ?? new string[0]; r.Gating = c.Gating; r.Excluded = c.Excluded;
                    if (c.Through != null) { r.Through = c.Through; r.ThroughLand = c.ThroughLand; r.ThroughGroup = Build.FaceGroups[c.ThroughLand ? "bridge" : "underBridge"]; }
                });
            }
        }

        public static double Distance(double[] a, double[] b) { return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }

        // ---- static evaluation on the mesh ----
        private void EvaluateStatically()
        {
            foreach (var f in Faces)
            {
                var p = f.PhysicalPoint ? f.Point : Physical(f.Point);
                f.StaticFace = Topology.Locate(p[0], p[1], f.Land, Model);
                f.StaticType = f.StaticFace < 0 ? null : TerrainTypes.Name(Build.Mesh.Groups[f.StaticFace]);
                bool ok = f.Expect.Length == 0 ? f.StaticFace < 0 : f.StaticType != null && f.Expect.Contains(f.StaticType);
                if (!ok && f.Gating) Diagnostics.Add(new NavigationDiagnostic("navigation.face_not_realized", f.Id, "the mesh has " + (f.StaticType ?? "no face") + " here, expected " + (f.Expect.Length == 0 ? "no face" : String.Join(" or ", f.Expect)), f.SourceIds));
            }
            var reference = new MeshNavigationEngine(Build.Mesh, Model);
            foreach (var r in Routes)
            {
                var a = Physical(r.From);
                int fa = Topology.Locate(a[0], a[1], r.FromLand, Model);
                var comp = r.Excluded == null ? Components[r.Type] : Topology.Components(f => Array.IndexOf(r.Excluded, Build.Mesh.Groups[f]) < 0);
                bool endpoints = fa >= 0;
                foreach (var pair in Placements(a, r.To))
                {
                    var s = pair[0]; var b = pair[1];
                    int fs = s[0] == a[0] ? fa : Topology.Locate(s[0], s[1], r.FromLand, Model);
                    int fb = Topology.Locate(b[0], b[1], r.ToLand, Model);
                    if (fb < 0) { endpoints = false; continue; }
                    if (fs < 0 || comp[fs] < 0 || comp[fs] != comp[fb]) continue;
                    r.StaticReachable = true;
                    // The mesh's own shortest path for this placement (string-pulled through its face corridor): the engine's path is bounded by it.
                    List<double[]> path;
                    if (r.Expected && r.Gating && reference.Path(fs, fb, s, b, 0, r.Excluded ?? Model.Invalid[r.Type], 0, Model.CostToSea, Model.CostToLand, out path))
                    {
                        double length = 0; for (int i = 1; i < path.Count; i++) length += Distance(path[i - 1], path[i]);
                        if (double.IsNaN(r.StaticLength) || length < r.StaticLength) r.StaticLength = length;
                    }
                }
                if (r.StaticReachable != r.Expected && r.Gating)
                    Diagnostics.Add(new NavigationDiagnostic(r.Expected ? "navigation.route_not_realized" : "navigation.unintended_route", r.Id,
                        r.Type + " navigation " + (r.Expected ? "cannot reach" : "reaches") + " the target on the built mesh" + (!endpoints ? " (an endpoint has no face of its domain)" : ""), r.SourceIds));
            }
            var all = Components["All"];
            var members = IslandMembers.Select(Point).Where(p => p != null && p.Resolved != null).ToList();
            var islands = members.Select(p => { var q = Physical(p.Resolved); int f = Topology.Locate(q[0], q[1], p.Land, Model); return f < 0 ? -1 : all[f]; }).ToList();
            if (islands.Distinct().Count() > 1)
            {
                int common = islands.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                Diagnostics.Add(new NavigationDiagnostic("navigation.islands", "world", "town gates, ports and kept castles of player-safe groups are not on one All-navigation island",
                    members.Where((p, i) => islands[i] != common).SelectMany(p => p.SourceIds).ToArray()));
            }
        }

        public Dictionary<string, object> StaticRecord()
        {
            return new Dictionary<string, object> {
                ["model"] = Model.Record(), ["components"] = NavigationModel.Types.ToDictionary(t => t, t => (object)(Components[t].Max() + 1)),
                ["points"] = Points.Select(p => p.Record()).ToList(),
                ["snaps"] = new Dictionary<string, object> { ["kept"] = Points.Count(p => p.Snap == "kept"), ["moved"] = Points.Count(p => p.Snap == "moved"), ["refused"] = Points.Count(p => p.Snap == "refused"),
                    ["separated"] = Points.Count(p => p.Snap == "separated"), ["portsPlaced"] = Points.Count(p => p.Snap == "placed toward its gate"),
                    ["maxDistance"] = Points.Where(p => p.Resolved != null && p.Snap != "placed toward its gate").Select(p => Math.Round(p.SnapDistance, 4)).DefaultIfEmpty(0).Max(),
                    ["maxPortPlacement"] = Points.Where(p => p.Snap == "placed toward its gate").Select(p => Math.Round(p.SnapDistance, 4)).DefaultIfEmpty(0).Max() },
                ["faceChecks"] = Faces.Count, ["routeChecks"] = Routes.Count, ["routesExpectedReachable"] = Routes.Count(r => r.Expected), ["routesExpectedForbidden"] = Routes.Count(r => !r.Expected),
                ["islandMembers"] = IslandMembers.Count, ["diagnostics"] = Diagnostics.Select(d => d.Record()).ToList(), ["notes"] = Notes, ["activatable"] = Activatable };
        }
    }

    // The engine calls the validator makes (Scene in game; MeshNavigationEngine offline). Positions are physical.
    internal interface INavigationEngine
    {
        string Name { get; }
        bool Face(double x, double y, bool land, out int face, out int group, out int island);
        bool Path(int fromFace, int toFace, double[] from, double[] to, double radius, int[] excluded, double extraCost, int costToSea, int costToLand, out List<double[]> points);
        bool Distance(int fromFace, int toFace, double[] from, double[] to, double radius, double limit, int[] excluded, int costToSea, int costToLand, out double distance);
        int FaceCount { get; }
        int FaceGroup(int face);
        double[] FaceCenter(int face);
    }

    // A managed stand-in for the engine over the built mesh: region-strict face lookup, a shortest face corridor (shared-edge midpoints, region
    // switch costs) string-pulled by the funnel algorithm. It is the static reference path the engine's lengths are bounded by, and the
    // offline engine for the validator's logic; it is never evidence of engine behaviour.
    internal sealed class MeshNavigationEngine : INavigationEngine
    {
        private readonly NavMesh mesh; private readonly MeshTopology topology; private readonly NavigationModel model;
        private readonly HashSet<int> disabled = new HashSet<int>();
        private int[] islands;
        public MeshNavigationEngine(NavMesh mesh, NavigationModel model) { this.mesh = mesh; this.model = model; topology = new MeshTopology(mesh); Recompute(); }
        public string Name { get { return "managed mesh stand-in (offline)"; } }
        public void Disable(int group) { disabled.Add(group); Recompute(); }
        private void Recompute() { islands = topology.Components(f => !disabled.Contains(mesh.Groups[f])); }
        public bool Face(double x, double y, bool land, out int face, out int group, out int island)
        {
            face = topology.Locate(x, y, land, model); group = face < 0 ? -1 : mesh.Groups[face]; island = face < 0 ? -1 : islands[face];
            return face >= 0;
        }
        public bool Path(int fromFace, int toFace, double[] from, double[] to, double radius, int[] excluded, double extraCost, int costToSea, int costToLand, out List<double[]> points)
        {
            points = Search(fromFace, toFace, from, to, excluded, costToSea, costToLand);
            return points != null;
        }
        public bool Distance(int fromFace, int toFace, double[] from, double[] to, double radius, double limit, int[] excluded, int costToSea, int costToLand, out double distance)
        {
            var points = Search(fromFace, toFace, from, to, excluded, costToSea, costToLand);
            distance = 0; if (points == null) return false;
            for (int i = 1; i < points.Count; i++) distance += NavigationPlan.Distance(points[i - 1], points[i]);
            distance += lastSwitchCost;   // the engine's distance is a cost including region switches
            return true;
        }
        public int FaceCount { get { return mesh.FaceCount; } }
        public int FaceGroup(int face) { return mesh.Groups[face]; }
        public double[] FaceCenter(int face) { return mesh.Centroid(face); }
        private double lastSwitchCost;
        private List<double[]> Search(int a, int b, double[] from, double[] to, int[] excluded, int costToSea, int costToLand)
        {
            lastSwitchCost = 0;
            Func<int, bool> open = f => Array.IndexOf(excluded, mesh.Groups[f]) < 0 && !disabled.Contains(mesh.Groups[f]);
            if (!open(a) || !open(b)) return null;
            var dist = new Dictionary<int, double> { [a] = 0 }; var via = new Dictionary<int, int>(); var at = new Dictionary<int, double[]> { [a] = from };
            var queue = new SortedSet<Tuple<double, int>>(); queue.Add(Tuple.Create(0.0, a));
            while (queue.Count > 0)
            {
                var top = queue.Min; queue.Remove(top); int f = top.Item2;
                if (f == b) break;
                if (top.Item1 > dist[f]) continue;
                foreach (int g in topology.Adjacent[f])
                {
                    if (!open(g)) continue;
                    var mid = Midpoint(f, g);
                    double cost = dist[f] + NavigationPlan.Distance(at[f], mid) + (model.IsLand(mesh.Groups[f]) != model.IsLand(mesh.Groups[g]) ? (model.IsLand(mesh.Groups[g]) ? costToLand : costToSea) : 0);
                    double known; if (dist.TryGetValue(g, out known) && known <= cost) continue;
                    dist[g] = cost; via[g] = f; at[g] = mid; queue.Add(Tuple.Create(cost, g));
                }
            }
            if (!dist.ContainsKey(b)) return null;
            var corridor = new List<int> { b }; int c = b;
            while (c != a) { c = via[c]; corridor.Add(c); }
            corridor.Reverse();
            for (int i = 1; i < corridor.Count; i++)
                if (model.IsLand(mesh.Groups[corridor[i - 1]]) != model.IsLand(mesh.Groups[corridor[i]])) lastSwitchCost += model.IsLand(mesh.Groups[corridor[i]]) ? costToLand : costToSea;
            // Portals: the shared edge between consecutive faces, as (left, right) facing along the corridor. A face is counter-clockwise, so
            // leaving it through its edge u -> v puts v on the left.
            var portals = new List<double[][]> { new[] { from, from } };
            for (int i = 0; i + 1 < corridor.Count; i++)
            {
                var loop = mesh.FaceVertices[corridor[i]]; var edges = mesh.FaceEdges[corridor[i]];
                int j = Array.IndexOf(edges, mesh.FaceEdges[corridor[i]].Intersect(mesh.FaceEdges[corridor[i + 1]]).First());
                var u = mesh.Vertices[loop[j]]; var v = mesh.Vertices[loop[(j + 1) % loop.Length]];
                portals.Add(new[] { new[] { v[0], v[1] }, new[] { u[0], u[1] } });
            }
            portals.Add(new[] { to, to });
            return Funnel(portals);
        }
        private static double Cross(double[] o, double[] a, double[] b) { return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]); }
        private static bool Same(double[] a, double[] b) { return Math.Abs(a[0] - b[0]) < 1e-12 && Math.Abs(a[1] - b[1]) < 1e-12; }
        // The simple stupid funnel algorithm over (left, right) portals; returns the corner points from start to end.
        private static List<double[]> Funnel(List<double[][]> portals)
        {
            var path = new List<double[]> { portals[0][0] };
            double[] apex = portals[0][0], left = portals[0][0], right = portals[0][1];
            int apexIndex = 0, leftIndex = 0, rightIndex = 0;
            for (int i = 1; i < portals.Count; i++)
            {
                var l = portals[i][0]; var r = portals[i][1];
                if (Cross(apex, right, r) >= 0)
                {
                    if (Same(apex, right) || Cross(apex, left, r) < 0) { right = r; rightIndex = i; }
                    else { path.Add(left); apex = left; apexIndex = leftIndex; left = apex; right = apex; leftIndex = rightIndex = apexIndex; i = apexIndex; continue; }
                }
                if (Cross(apex, left, l) <= 0)
                {
                    if (Same(apex, left) || Cross(apex, right, l) > 0) { left = l; leftIndex = i; }
                    else { path.Add(right); apex = right; apexIndex = rightIndex; left = apex; right = apex; leftIndex = rightIndex = apexIndex; i = apexIndex; continue; }
                }
            }
            var end = portals[portals.Count - 1][0];
            if (!Same(path[path.Count - 1], end)) path.Add(end);
            return path;
        }
        private double[] Midpoint(int f, int g)
        {
            var shared = mesh.FaceEdges[f].Intersect(mesh.FaceEdges[g]).First(); var e = mesh.Edges[shared];
            var p = mesh.Vertices[e[0]]; var q = mesh.Vertices[e[1]];
            return new[] { (p[0] + q[0]) / 2, (p[1] + q[1]) / 2 };
        }
    }

    internal static class NavigationValidator
    {
        public const string ReportSchema = "mnbl.navigation-report";
        public const string Version = "mmr38/3";   // 3 (2026-10-19): All routes take routes.maxStaticFactorAll; a basin with no OpenSea cell is checked by its water faces

        // The route matrix, face checks, face table and islands through an engine. Returns the report; its "activatable" is false when the
        // static plan or any gating engine result disagrees with the intended topology. engineFaults names any fault injected into the engine
        // (the probe's), recorded in the report.
        public static Dictionary<string, object> Run(NavigationPlan plan, INavigationEngine engine, string sceneName, Action<string> stage, IList<string> engineFaults = null)
        {
            var result = new Result();
            foreach (var step in Steps(plan, engine, sceneName, stage, engineFaults, result)) { }
            return result.Report;
        }

        public sealed class Result { public Dictionary<string, object> Report; }

        // MMR-44: Run in steps, for a caller that gives the engine thread back between them (the generation job runs each MoveNext as its own
        // engine-thread item): after the face table, after the face checks, and after each route. Every step queries the engine, so each runs on
        // the engine thread; the report, identical to Run's, is in result once the steps end.
        public static IEnumerable<string> Steps(NavigationPlan plan, INavigationEngine engine, string sceneName, Action<string> stage, IList<string> engineFaults, Result result)
        {
            var mesh = plan.Build.Mesh; var model = plan.Model; var policy = plan.Build.Policy; double spacing = plan.Build.Input.Cells.Spacing;
            var diagnostics = new List<NavigationDiagnostic>();
            if (stage != null) stage("navigation_face_table");
            // The engine's face table against the built mesh: count, TerrainType histogram, and every engine face centre located in the mesh.
            int count = engine.FaceCount, centreMismatches = 0;
            var engineHistogram = new SortedDictionary<string, int>(StringComparer.Ordinal); var meshHistogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (int g in mesh.Groups) Bump(meshHistogram, TerrainTypes.Name(g) ?? g.ToString(CultureInfo.InvariantCulture));
            var mismatchSamples = new List<string>();
            for (int f = 0; f < count; f++)
            {
                int g = engine.FaceGroup(f); Bump(engineHistogram, TerrainTypes.Name(g) ?? g.ToString(CultureInfo.InvariantCulture));
                var c = engine.FaceCenter(f);
                bool found = mesh.FacesAt(c[0], c[1], x => x == g).Any();
                if (!found) { centreMismatches++; if (mismatchSamples.Count < 8) mismatchSamples.Add((TerrainTypes.Name(g) ?? "?") + " at " + Math.Round(c[0], 3) + "," + Math.Round(c[1], 3)); }
            }
            bool histogramEqual = engineHistogram.Count == meshHistogram.Count && engineHistogram.All(kv => { int n; return meshHistogram.TryGetValue(kv.Key, out n) && n == kv.Value; });
            if (count != mesh.FaceCount || !histogramEqual || centreMismatches > 0)
                diagnostics.Add(new NavigationDiagnostic("navigation.engine_faces", "navmesh", "the engine read " + count + " faces (mesh " + mesh.FaceCount + "), TerrainType histogram " + (histogramEqual ? "equal" : "different") + ", " + centreMismatches + " face centres outside a mesh face of their type"));
            var faceTable = new Dictionary<string, object> { ["engineFaces"] = count, ["meshFaces"] = mesh.FaceCount, ["engineByTerrainType"] = engineHistogram, ["histogramEqual"] = histogramEqual,
                ["centresOutsideTheirMeshFace"] = centreMismatches, ["centreMismatchSamples"] = mismatchSamples };
            yield return "navigation_face_table";

            // Face checks.
            if (stage != null) stage("navigation_faces");
            var islandOfStatic = new Dictionary<int, HashSet<int>>(); var staticOfIsland = new Dictionary<int, HashSet<int>>();
            var all = plan.Components["mesh"];
            Action<int, int> pairIsland = (staticFace, island) =>
            {
                if (staticFace < 0 || island < 0 || all[staticFace] < 0) return;
                Add(islandOfStatic, all[staticFace], island); Add(staticOfIsland, island, all[staticFace]);
            };
            var faces = new List<object>();
            foreach (var f in plan.Faces)
            {
                var p = f.PhysicalPoint ? f.Point : plan.Physical(f.Point); int face, group, island;
                bool valid = engine.Face(p[0], p[1], f.Land, out face, out group, out island);
                string type = valid ? TerrainTypes.Name(group) ?? group.ToString(CultureInfo.InvariantCulture) : null;
                bool ok = f.Expect.Length == 0 ? !valid : valid && f.Expect.Contains(type);
                if (valid) pairIsland(f.StaticFace, island);
                if (!ok && f.Gating) diagnostics.Add(new NavigationDiagnostic("navigation.engine_face", f.Id, "the engine answers " + (type ?? "no face") + ", expected " + (f.Expect.Length == 0 ? "no face" : String.Join(" or ", f.Expect)), f.SourceIds));
                faces.Add(new Dictionary<string, object> { ["id"] = f.Id, ["check"] = f.Check, ["subject"] = f.Subject, ["domain"] = f.Land ? "land" : "sea", ["point"] = NavPoint.Round(f.Point), ["expected"] = f.Expect, ["static"] = f.StaticType,
                    ["engine"] = type, ["agrees"] = ok, ["gating"] = f.Gating });
            }
            yield return "navigation_faces";

            // Routes.
            if (stage != null) stage("navigation_routes");
            var routes = new List<object>(); int sampled = 0;
            var crossed = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var cornerHops = new List<string>();
            foreach (var r in plan.Routes)
            {
                var e = EvaluateRoute(plan, engine, r, pairIsland);
                if (e.Sampled) sampled++;
                foreach (var line in e.Lines) Bump(crossed, line);
                if (e.CornerHop) cornerHops.Add(r.Id);
                if (!e.Agrees && r.Gating) diagnostics.Add(new NavigationDiagnostic(r.Expected ? "navigation.engine_route_failed" : "navigation.engine_unintended_route", r.Id, r.Type + ": " + String.Join("; ", e.Problems), r.SourceIds));
                routes.Add(e.Row);
                yield return "navigation_route";
            }

            // Islands: the MMR-28 members share one engine island; engine islands are compared with the mesh's face connectivity (a weak check
            // where the mesh is one connected piece: All connectivity of the members is the static "navigation.islands" check and the All
            // routes).
            var islandProblems = islandOfStatic.Where(kv => kv.Value.Count > 1).Select(kv => "mesh component " + kv.Key + " spans engine islands " + String.Join("/", kv.Value))
                .Concat(staticOfIsland.Where(kv => kv.Value.Count > 1).Select(kv => "one engine island spans mesh components " + String.Join("/", kv.Value))).ToList();
            var members = plan.IslandMembers.Select(plan.Point).Where(p => p != null && p.Resolved != null).ToList();
            var memberIslands = new HashSet<int>();
            foreach (var p in members) { var q = plan.Physical(p.Resolved); int face, group, island; if (engine.Face(q[0], q[1], p.Land, out face, out group, out island)) memberIslands.Add(island); else memberIslands.Add(-1); }
            if (memberIslands.Count > 1) islandProblems.Add("town gates, ports and kept castles of player-safe groups lie on " + memberIslands.Count + " engine islands");
            foreach (var problem in islandProblems) diagnostics.Add(new NavigationDiagnostic("navigation.engine_islands", "world", problem));

            var all3 = plan.Diagnostics.Concat(diagnostics).ToList();
            var stable = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["schema"] = ReportSchema, ["version"] = 1, ["validatorVersion"] = Version, ["engineFaults"] = (engineFaults ?? new string[0]).ToArray(),
                ["sceneName"] = sceneName, ["worldContentId"] = plan.Build.Input.WorldContentId, ["variant"] = plan.Build.Input.Variant,
                ["navigationSha256"] = plan.Build.Sha256, ["inputsSha256"] = plan.Build.Input.InputsSha256, ["policySha256"] = policy.Sha256, ["build"] = plan.Build.Record(),
                ["coordinatePolicyVersion"] = PeriodicWorldCoordinates.PolicyVersion, ["coordinatePolicy"] = plan.Coordinates.Identity,
                ["static"] = plan.StaticRecord(), ["engine"] = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["name"] = engine.Name, ["faceTable"] = faceTable, ["faces"] = faces, ["routes"] = routes, ["sampledRoutes"] = sampled, ["tileLinesCrossed"] = crossed, ["cornerHops"] = cornerHops,
                    ["islands"] = new Dictionary<string, object> { ["meshComponentsSeen"] = islandOfStatic.Count, ["engineIslandsSeen"] = staticOfIsland.Count, ["memberIslands"] = memberIslands.Count, ["problems"] = islandProblems },
                    ["diagnostics"] = diagnostics.Select(d => d.Record()).ToList() },
                ["summary"] = new Dictionary<string, object> { ["faceChecks"] = faces.Count, ["facesAgree"] = faces.Cast<Dictionary<string, object>>().Count(x => (bool)x["agrees"]),
                    ["routeChecks"] = routes.Count, ["routesAgree"] = routes.Cast<Dictionary<string, object>>().Count(x => (bool)x["agrees"]),
                    ["reachableRoutes"] = plan.Routes.Count(r => r.Expected), ["forbiddenRoutes"] = plan.Routes.Count(r => !r.Expected), ["diagnostics"] = all3.Count },
                ["failures"] = all3.Select(d => d.Record()).ToList(), ["activatable"] = all3.Count == 0 };
            var report = new Dictionary<string, object>(stable);
            report["reportSha256"] = Json.Sha(new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(stable));
            result.Report = report;
        }

        public sealed class RouteEvaluation
        {
            public Dictionary<string, object> Row;
            public List<string> Problems, Lines = new List<string>();
            public bool Agrees, Found, Reachable, Sampled, CornerHop;
            public double Distance = double.NaN, Length = double.NaN, Straight = double.NaN;
            public int Switches = -1;
            public List<double[]> Points;   // the found path's polyline, start to end (diagnostics: route_trace)
        }

        // One route through the engine (Steps' route matrix and MMR-41's semantic route set): every placement of the route (MMR-64); the shortest
        // found path is the route's. pairIsland, when given, records the engine islands of the faces it touches (the islands check).
        public static RouteEvaluation EvaluateRoute(NavigationPlan plan, INavigationEngine engine, RouteCheck r, Action<int, int> pairIsland)
        {
            var model = plan.Model; var policy = plan.Build.Policy; double spacing = plan.Build.Input.Cells.Spacing;
            var e = new RouteEvaluation();
            var a = plan.Physical(r.From);
            int fa, ga, ia;
            bool va = engine.Face(a[0], a[1], r.FromLand, out fa, out ga, out ia);
            if (va && pairIsland != null) pairIsland(plan.Topology.Locate(a[0], a[1], r.FromLand, model), ia);
            var invalid = r.Excluded ?? model.Invalid[r.Type];
            double radius = r.FromLand ? policy.LandRadius : model.EmbarkRadius;
            // Every placement of the route (MMR-64); the shortest found path is the route's, and `a` becomes that placement's start.
            List<double[]> points = null; double distance = double.NaN, straight = double.NaN; double[] b = null, chosenStart = null;
            bool found = false, reachable = false; double best = double.MaxValue;
            foreach (var pair in plan.Placements(a, r.To))
            {
                var s = pair[0]; var image = pair[1];
                int fs = fa, gs, isl;
                bool vs = va;
                if (s[0] != a[0]) { vs = engine.Face(s[0], s[1], r.FromLand, out fs, out gs, out isl); if (vs && pairIsland != null) pairIsland(plan.Topology.Locate(s[0], s[1], r.FromLand, model), isl); }
                int fb, gb, ib;
                if (!engine.Face(image[0], image[1], r.ToLand, out fb, out gb, out ib)) continue;
                if (pairIsland != null) pairIsland(plan.Topology.Locate(image[0], image[1], r.ToLand, model), ib);
                if (!vs) continue;
                List<double[]> candidate; double cost;
                bool pathFound = engine.Path(fs, fb, s, image, radius, invalid, policy.ExtraCost, model.CostToSea, model.CostToLand, out candidate);
                bool costFound = engine.Distance(fs, fb, s, image, radius, model.CostLimit, invalid, model.CostToSea, model.CostToLand, out cost);
                reachable |= costFound;
                if (!pathFound || candidate == null) { if (b == null) { b = image; chosenStart = s; if (costFound) distance = cost; } continue; }
                double l = PolylineLength(s, candidate, image);
                if (l < best) { best = l; found = true; points = candidate; b = image; chosenStart = s; distance = costFound ? cost : double.NaN; }
            }
            if (b == null) { var first = plan.Placements(a, r.To)[0]; chosenStart = first[0]; b = first[1]; }
            a = chosenStart;
            straight = NavigationPlan.Distance(a, b);
            var problems = new List<string>();
            if (found != r.Expected) problems.Add(r.Expected ? "no path" : "a path was found");
            if (reachable != r.Expected) problems.Add(r.Expected ? "no path distance" : "a path distance was found");
            Dictionary<string, object> samples = null; double length = double.NaN; int switches = -1;
            var lines = new List<string>();
            if (found && points != null)
            {
                var polyline = new List<double[]> { a }; polyline.AddRange(points.Where(q => q != null)); polyline.Add(b);
                e.Points = polyline;
                length = PolylineLength(a, points, b);
                samples = Sample(engine, model, polyline, policy.SampleStep); e.Sampled = true;
                lines = Crossings(polyline, plan.Build.Input.Surface.NodeSize, plan.Build.Input.Surface.Extent);
                int offLand = (int)samples["offLand"], offSea = (int)samples["offSea"], transitions = (int)samples["transitions"];
                if (r.Type == "Default" && offLand > 0) problems.Add(offLand + " samples off land (a shortcut)");
                if (r.Type == "Naval" && offSea > 0) problems.Add(offSea + " samples off sea (a shortcut)");
                // The engine's path distance is a cost: the path's length plus the region switch cost of every embark and disembark.
                if (!double.IsNaN(distance) && model.CostToSea == model.CostToLand && model.CostToSea > 0) switches = (int)Math.Round((distance - length) / model.CostToSea);
                if (switches >= 0 && r.MinTransitions >= 0 && switches < r.MinTransitions) problems.Add(switches + " region switches, at least " + r.MinTransitions + " expected");
                if (switches >= 0 && r.MaxTransitions >= 0 && switches > r.MaxTransitions) problems.Add(switches + " region switches, at most " + r.MaxTransitions + " expected");
                if (switches < 0 && (r.MinTransitions >= 0 || r.MaxTransitions >= 0)) problems.Add("the region switches could not be counted (no engine cost)");
                if (switches >= 2 && transitions < switches) e.CornerHop = true;
                foreach (var line in r.Cross) if (!lines.Contains(line)) problems.Add("does not cross the tile line " + line);
                if (r.Through != null && !PassesThrough(engine, plan, polyline, policy.SampleStep, r)) problems.Add("does not cross " + (TerrainTypes.Name(r.ThroughGroup) ?? "?") + " faces of cell(s) " + String.Join(" ", r.Through.Select(c => c[0] + "," + c[1])));
            }
            if (reachable && !double.IsNaN(distance) && distance < straight - policy.DistanceSlack) problems.Add("distance " + Math.Round(distance, 3) + " is shorter than the straight " + Math.Round(straight, 3));
            // An All route (land and sea, with region switches) takes its own bound: the engine's route over the square sea faces can run longer than the
            // shortest corridor with its faces and switches right, up to a staircase's sqrt 2 at 45 degrees (seed 43's world: one route across the seam, 1.29 x).
            double factor = r.Type == "All" ? policy.MaxStaticFactorAll : policy.MaxStaticFactor;
            if (found && !double.IsNaN(length) && !double.IsNaN(r.StaticLength) && length > factor * r.StaticLength + policy.StaticSlack * spacing)
                problems.Add("path length " + Math.Round(length, 3) + " exceeds " + factor + " x the mesh's shortest path " + Math.Round(r.StaticLength, 3) + " + " + policy.StaticSlack + " cell (a detour)");
            if (found && !double.IsNaN(length))
            {
                if (length < straight - policy.DistanceSlack) problems.Add("path length " + Math.Round(length, 3) + " is shorter than the straight " + Math.Round(straight, 3));
                if (r.MaxDistance >= 0 && length > r.MaxDistance) problems.Add("path length " + Math.Round(length, 3) + " exceeds " + Math.Round(r.MaxDistance, 3));
                if (r.MaxFactor > 0 && length > r.MaxFactor * straight) problems.Add("path length " + Math.Round(length, 3) + " exceeds " + r.MaxFactor + " x straight " + Math.Round(straight, 3));
                if (r.MinFactor > 0 && length < r.MinFactor * straight) problems.Add("path length " + Math.Round(length, 3) + " is under " + r.MinFactor + " x straight " + Math.Round(straight, 3) + " (no detour)");
                if (r.DetourCells != null && (length / spacing < r.DetourCells[0] || length / spacing > r.DetourCells[1])) problems.Add("detour " + Math.Round(length / spacing, 3) + " cells outside [" + r.DetourCells[0] + ", " + r.DetourCells[1] + "]");
            }
            bool agrees = problems.Count == 0;
            e.Problems = problems; e.Agrees = agrees; e.Found = found; e.Reachable = reachable; e.Distance = distance; e.Length = length; e.Straight = straight; e.Switches = switches; e.Lines = lines;
            e.Row = new Dictionary<string, object> { ["id"] = r.Id, ["check"] = r.Check, ["subject"] = r.Subject, ["type"] = r.Type, ["from"] = NavPoint.Round(r.From), ["to"] = NavPoint.Round(r.To), ["expected"] = r.Expected ? "reachable" : "forbidden",
                ["static"] = r.StaticReachable ? "reachable" : "forbidden", ["found"] = found, ["reachable"] = reachable, ["distance"] = reachable ? (object)Math.Round(distance, 3) : null,
                ["pathLength"] = found ? (object)Math.Round(length, 3) : null, ["meshShortestPath"] = double.IsNaN(r.StaticLength) ? null : (object)Math.Round(r.StaticLength, 3), ["regionSwitches"] = switches,
                ["straight"] = Math.Round(straight, 3), ["excluded"] = r.Excluded == null ? r.Type + " invalid terrain types" : "[" + String.Join(",", r.Excluded) + "]",
                ["waypoints"] = points == null ? 0 : points.Count, ["samples"] = samples, ["crosses"] = lines, ["agrees"] = agrees, ["problems"] = problems, ["gating"] = r.Gating, ["sourceIds"] = r.SourceIds };
            return e;
        }

        private static double PolylineLength(double[] a, List<double[]> points, double[] b)
        {
            var all = new List<double[]> { a }; all.AddRange(points.Where(q => q != null)); all.Add(b);
            double length = 0; for (int i = 1; i < all.Count; i++) length += NavigationPlan.Distance(all[i - 1], all[i]);
            return length;
        }
        // Whether a path crosses one of the route's cells on a face of the required group (a bridge deck in the land region, an under-bridge
        // face in the sea region).
        private static bool PassesThrough(INavigationEngine engine, NavigationPlan plan, List<double[]> polyline, double step, RouteCheck r)
        {
            for (int i = 1; i < polyline.Count; i++)
            {
                var a = polyline[i - 1]; var b = polyline[i];
                int steps = Math.Max(1, (int)Math.Ceiling(NavigationPlan.Distance(a, b) / step));
                for (int s = 0; s <= steps; s++)
                {
                    double x = a[0] + (b[0] - a[0]) * s / steps, y = a[1] + (b[1] - a[1]) * s / steps;
                    var cell = plan.LogicalCell(x, y);
                    if (!r.Through.Any(c => c[0] == cell[0] && plan.Build.Input.Cells.Wrap(c[1]) == cell[1])) continue;
                    int f, g, island;
                    if (engine.Face(x, y, r.ThroughLand, out f, out g, out island) && g == r.ThroughGroup) return true;
                }
            }
            return false;
        }

        // The MMR-17 sampler, restated: each segment every `step` units, the land and sea region faces underneath, domain changes. A sample
        // whose own lookup fails counts as in a domain when a point 0.05 units away (four directions) is (MMR-17 had no such tolerance): a
        // path along an edge between a sea face and a mountain or land face (a boundary tie the lookup may answer with either face) is not a
        // shortcut, which crosses whole cells.
        private static readonly double[][] Probes = { new[] { .05, 0.0 }, new[] { -.05, 0.0 }, new[] { 0.0, .05 }, new[] { 0.0, -.05 } };
        private static bool InDomain(INavigationEngine engine, NavigationModel model, double x, double y, bool land)
        {
            int f, g, island; string type = land ? "Default" : "Naval";
            if (engine.Face(x, y, land, out f, out g, out island) && model.Valid(g, type)) return true;
            foreach (var d in Probes) if (engine.Face(x + d[0], y + d[1], land, out f, out g, out island) && model.Valid(g, type)) return true;
            return false;
        }
        private static Dictionary<string, object> Sample(INavigationEngine engine, NavigationModel model, List<double[]> points, double step)
        {
            int total = 0, offLand = 0, offSea = 0, transitions = 0; bool? wasLand = null;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                int steps = Math.Max(1, (int)Math.Ceiling(NavigationPlan.Distance(a, b) / step));
                for (int s = 0; s <= steps; s++)
                {
                    double x = a[0] + (b[0] - a[0]) * s / steps, y = a[1] + (b[1] - a[1]) * s / steps;
                    bool onLand = InDomain(engine, model, x, y, true), onSea = InDomain(engine, model, x, y, false);
                    total++; if (!onLand) offLand++; if (!onSea) offSea++;
                    bool land = onLand && !onSea ? true : !onLand && onSea ? false : wasLand ?? onLand;
                    if (wasLand.HasValue && land != wasLand.Value) transitions++;
                    wasLand = land;
                }
            }
            return new Dictionary<string, object> { ["samples"] = total, ["offLand"] = offLand, ["offSea"] = offSea, ["transitions"] = transitions };
        }

        // The terrain chunk (tile) lines a polyline crosses: x = k * nodeSize and y = k * nodeSize inside the terrain.
        private static List<string> Crossings(List<double[]> points, double node, double extent)
        {
            var lines = new SortedSet<string>(StringComparer.Ordinal);
            for (int k = 1; k * node < extent - 1e-9; k++)
                for (int axis = 0; axis < 2; axis++)
                {
                    double v = k * node; int last = 0;
                    foreach (var p in points)
                    {
                        int side = p[axis] > v + 1e-9 ? 1 : p[axis] < v - 1e-9 ? -1 : 0;
                        if (side == 0) continue;
                        if (last != 0 && side != last) lines.Add((axis == 0 ? "x=" : "y=") + v.ToString("0.###", CultureInfo.InvariantCulture));
                        last = side;
                    }
                }
            return lines.ToList();
        }
        private static void Bump(SortedDictionary<string, int> map, string key) { int n; map.TryGetValue(key, out n); map[key] = n + 1; }
        private static void Add(Dictionary<int, HashSet<int>> map, int key, int value) { HashSet<int> set; if (!map.TryGetValue(key, out set)) map[key] = set = new HashSet<int>(); set.Add(value); }
    }

    // Activation requires the world's navigation report: intact (its hash), of this validator version and navigation policy, activatable, and
    // naming the scene and its installed navmesh's hash. The production activation (MMR-45) runs NavigationValidator.Run on the constructed
    // world and persists its report before activating it, or finds a persisted report that still passes this gate.
    internal static class NavigationGate
    {
        public const string RecordSchema = "mnbl.navigation-gate-record";
        public static string ReportPath(string storeRoot, string sceneName) { return Path.Combine(storeRoot, "world-navigation", sceneName + ".json"); }

        // The exact text the report's hash covers (its fields without reportSha256, in ordinal key order).
        public static string StableText(IDictionary<string, object> report)
        {
            var stable = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in report) if (kv.Key != "reportSha256") stable[kv.Key] = kv.Value;
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(stable);
        }

        public static string Persist(string storeRoot, IDictionary<string, object> report)
        {
            string text = StableText(report);
            if (Json.Sha(text) != (string)report["reportSha256"]) throw new InvalidOperationException("The navigation report's hash does not match its content.");
            string path = ReportPath(storeRoot, (string)report["sceneName"]);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var record = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = RecordSchema, ["version"] = 1, ["reportSha256"] = report["reportSha256"], ["report"] = text };
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
            return path;
        }

        // Throws NavigationRefusedException unless the persisted report allows activating the scene whose navmesh is at navmeshPath.
        public static IDictionary<string, object> Require(string storeRoot, string sceneName, string navmeshPath, string policySha256) { return RequireAt(ReportPath(storeRoot, sceneName), sceneName, navmeshPath, policySha256); }

        // MMR-49: the same for a record at another path (a load checks a copy kept with the campaign before it restores it).
        public static IDictionary<string, object> RequireAt(string path, string sceneName, string navmeshPath, string policySha256)
        {
            Func<string, string, NavigationRefusedException> refuse = (code, detail) => new NavigationRefusedException("Activating " + sceneName, new[] { new NavigationDiagnostic(code, sceneName, detail) });
            if (!File.Exists(path)) throw refuse("navigation.no_report", "no navigation report was recorded for this scene (" + path + ")");
            IDictionary<string, object> report;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var record = (IDictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(path));
                if (Json.S(record, "schema") != RecordSchema) throw refuse("navigation.report_invalid", "the file is not an " + RecordSchema);
                string text = Json.S(record, "report");
                if (text == null || Json.Sha(text) != Json.S(record, "reportSha256")) throw refuse("navigation.report_invalid", "the report does not match its recorded hash");
                report = (IDictionary<string, object>)serializer.DeserializeObject(text);
            }
            catch (Exception error) when (!(error is NavigationRefusedException))
            {
                throw refuse("navigation.report_invalid", "the report cannot be read: " + error.GetType().Name + ": " + error.Message);
            }
            Check(report, sceneName, navmeshPath, policySha256);
            return report;
        }

        // Throws NavigationRefusedException unless the report allows activating the scene (also used for a report that is not persisted).
        public static void Check(IDictionary<string, object> report, string sceneName, string navmeshPath, string policySha256)
        {
            var problems = new List<NavigationDiagnostic>();
            Action<string, string> fail = (code, detail) => problems.Add(new NavigationDiagnostic(code, sceneName, detail));
            if (Json.S(report, "schema") != NavigationValidator.ReportSchema) fail("navigation.report_invalid", "the report is not an " + NavigationValidator.ReportSchema);
            if (Json.S(report, "validatorVersion") != NavigationValidator.Version) fail("navigation.report_stale", "the report is from validator " + Json.S(report, "validatorVersion") + ", not " + NavigationValidator.Version);
            if (Json.S(report, "policySha256") != policySha256) fail("navigation.report_stale", "the report was made under navigation policy " + Json.S(report, "policySha256") + ", not the installed " + policySha256);
            if (Json.S(report, "coordinatePolicyVersion") != PeriodicWorldCoordinates.PolicyVersion)
                fail("navigation.report_stale", "the report was made under periodic coordinate policy " + (Json.S(report, "coordinatePolicyVersion") ?? "(none)") + ", not " + PeriodicWorldCoordinates.PolicyVersion);
            if (Json.S(report, "sceneName") != sceneName) fail("navigation.report_mismatch", "the report names scene " + Json.S(report, "sceneName"));
            string content = Json.S(report, "worldContentId");
            if (SceneNames.IsWorldScene(sceneName) && (content == null || content.Length < 16 || !sceneName.StartsWith("mnbl_world_" + content.Substring(0, 16).ToLowerInvariant() + "_", StringComparison.Ordinal)))
                fail("navigation.report_mismatch", "the report's world " + content + " is not the scene's");
            string installed = File.Exists(navmeshPath) ? Json.Sha(File.ReadAllBytes(navmeshPath)) : null;
            if (installed == null || Json.S(report, "navigationSha256") != installed) fail("navigation.report_mismatch", "the report's navmesh " + Json.S(report, "navigationSha256") + " is not the installed " + (installed ?? "(missing)"));
            if (Json.A(report, "engineFaults").Length > 0) fail("navigation.report_faulted", "the report was made with engine faults injected: " + String.Join(", ", Json.A(report, "engineFaults").Select(x => (string)x)));
            if (!Json.B(report, "activatable"))
                foreach (IDictionary<string, object> f in Json.A(report, "failures"))
                    problems.Add(new NavigationDiagnostic(Json.S(f, "code"), Json.S(f, "subject"), Json.S(f, "detail"), Json.A(f, "sourceIds").Select(x => (string)x).ToArray()));
            if (!Json.B(report, "activatable") && problems.Count == 0) fail("navigation.report_refused", "the report is not activatable");
            if (problems.Count > 0) throw new NavigationRefusedException("Activating " + sceneName, problems);
        }
    }
}
