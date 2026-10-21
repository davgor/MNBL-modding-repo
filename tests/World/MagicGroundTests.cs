using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Missions;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-260 magic ground overlay and MMR-288 vein tests, without the game:
//   MagicGroundTests REPO BUNDLE_A BUNDLE_B OUT_JSON [DUMP_DIR]
// On two real compiled worlds (A' seed 42, B' seed 73):
//   a1  the generated magic variants are drawn as vein graphs (MMR-288): two builds are equal; the zones partition the mutated samples; each
//       source has one burst and bursts are only at sources; every other stamp lies on a graph edge (no isolated splotch) with its yaw along
//       the edge; widths never grow outward; land stamps never lie on a water node or cell; each stamp has the shared table's look, atlas row
//       by core and column by kind; the variants retype no face (visual only, RT-20); MMR-261's recorded Nerdun fire cast grows from the
//       capital and is revealed outward over revealHours (A');
//   a2  a synthetic live mutation (a fire disc at a town, a hidden school, water on cold ground, a water core) retypes only cells holding live
//       samples, only within the land-valid set (no Dune); the offline navmesh's faces of those cells, in every strip image, take the type;
//       the battle map gives those cells faceClass[remapped type] in every strip image, and a second change re-chooses exactly its cells;
//   a3  the navmesh, cells.bin and the navigation-cache key do not depend on the overlay;
//   r1  the per-type land shares before and after the mutation are recorded;
// plus the seam and pole mapping, the snapshot swap, the RT-37 disable, a magic-off world, the installer over a recording target (a missing
// material draws nothing; a live change redraws only the zones it touches) and the shared decal budget with the map dressing (whole-world and
// closest-camera window counts, and with the graph's water stamps MMR-290 will draw). DUMP_DIR: each world's stamps, graph and a plain base map
// (DUMP_DIR/<A|B|A-nerdun>/veins.json) for tools/render_magic_veins.py.
internal static class MagicGroundTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static bool Throws(Action a) { try { a(); return false; } catch (Exception) { return true; } }

    // Once Cleared is set (the engine ran Scene.ClearAll), every decal call fails and is counted: MMR-262's teardown must make none.
    private sealed class Recorder : IMapDressingTarget
    {
        public readonly Dictionary<int, double[]> Live = new Dictionary<int, double[]>();
        public int Next, AfterClear; public bool Cleared; public Func<string, bool> Resolves = m => true;
        private void Engine() { if (Cleared) { AfterClear++; throw new InvalidOperationException("an engine call on a cleared scene"); } }
        public object AddDecal(RoadDecal decal, double x, double y) { Engine(); if (!Resolves(decal.Material)) return null; Live[++Next] = new[] { x, y }; return Next; }
        public void MoveDecal(object handle, RoadDecal decal, double x, double y) { Engine(); Live[(int)handle] = new[] { x, y }; }
        public void RemoveDecal(object handle) { Engine(); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public object AddEntity(string prefab, double x, double y, double z, double yaw, double scale) { throw new NotSupportedException(); }
        public void MoveEntity(object handle, double x, double y, double z, double yaw, double scale) { throw new NotSupportedException(); }
        public void RemoveEntity(object handle) { throw new NotSupportedException(); }
        public double Ground(double x, double y) { return 1; }
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.magic-ground-offline", ["version"] = 1 };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0], dump = args.Length > 4 && args[4].Length > 0 ? args[4] : null;
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var magic = roads.Magic; RoadsPolicyMinStamps = magic.MinStamps;
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var table = BiomeTable.FromBytes(File.ReadAllBytes(Path.Combine(repo, BiomeTable.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var catalogue = MissionScenes.FromBytes(File.ReadAllBytes(Path.Combine(repo, MissionScenes.PolicyPath)));
        var resolver = new GeneratedEncounterResolver(catalogue, BattleTable.FromCatalogue(catalogue));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var landValid = new HashSet<int>(table.LandValid.Select(TerrainTypes.Id));
        var landFaces = new HashSet<int>(BiomeTable.LandFaceTypes.Select(TerrainTypes.Id));

        Check(magic.Schools.Count == 8 && magic.Rows.Count == 4 && magic.RowOf("desert") == 0 && magic.RowOf("grassland") == 1 && magic.RowOf("exposed_rock") == 2 && magic.RowOf("cold_tundra") == 3
              && magic.RowOf("ocean") < 0 && magic.MaterialPrefix == "mnbl_magic_" && !table.LandValid.Contains("Dune"),
              "roads.json magic: 8 published school materials mnbl_magic_<school>, atlas rows arid/verdant/rock/cold by core (docs/magic-ground-textures.md), water cores have none; landValid has no Dune");

        // The source reader's magic layers on a magic-off world: a source without biome_variant loads with no magic ground.
        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var world = new Dictionary<string, object> { ["label"] = label }; worlds.Add(world);
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            world["bundle"] = bundle.WorldContentId;
            var surface = WorldSurface.FromBundle(bundle);
            var navigationInput = NavigationInput.FromBundle(bundle, surface, navigationPolicy);
            var cellsBin = bundle.ReadVerified("compiled/travel/cells.bin");
            var cellsBefore = (byte[])cellsBin.Clone();
            var navigation = RuntimeNavigationBuilder.Build(navigationInput, navigationPolicy);
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            worldJson = null;
            var cells = navigationInput.Cells;
            var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, cells, table));
            var src = overlay.Source;
            var baseSnapshot = overlay.Current;
            Check(src.Enabled && src.Catalogue.Length == source.MagicalBiomes.Count && src.Catalogue.Length > 0 && Math.Abs(src.Threshold - source.MagicThreshold) < 1e-12 && src.Columns == surface.SourceGridSide - 1,
                  label + ": the source's magic layers read (" + src.Catalogue.Length + " magical biomes, threshold " + src.Threshold.ToString("R", CultureInfo.InvariantCulture) + ", " + src.Variant.Count(v => v >= 0) + " mutated samples)");

            // ---- mapping: seam, strip images and poles ----
            int n1 = src.Columns; bool seam = true, poles = true, partition = true;
            var rnd = new Random(7);
            for (int k = 0; k < 400; k++)
            {
                double x = surface.Strip + rnd.NextDouble() * surface.LogicalWidth, y = surface.PoleMargin + rnd.NextDouble() * surface.LogicalHeight;
                if (src.SampleAt(x, y) != src.SampleAt(x + surface.LogicalWidth, y) || src.SampleAt(x, y) != src.SampleAt(x - surface.LogicalWidth, y)) seam = false;
                if (src.CellAt(x, y) != src.CellAt(x + surface.LogicalWidth, y) || src.CellAt(x, y) < 0) seam = false;
            }
            poles = src.SampleAt(surface.Strip + 3, surface.PoleMargin - 5) == src.Index(0, 0) && src.SampleAt(surface.Strip + 400, 0) == src.Index(0, 0)
                && src.SampleAt(surface.Strip + 3, surface.PoleMargin + surface.LogicalHeight + 9) == src.Index(n1, 0) && src.Index(5, n1) == src.Index(5, 0) && src.CellOfSample(0, 3) < 0 && src.CellOfSample(n1, 3) < 0;
            for (int r = 1; r < n1 && partition; r++) for (int c = 0; c < n1; c++)
            {
                int cell = src.CellOfSample(r, c);
                var p = src.Logical(r, c);
                if (cell < 0 || src.SampleAt(p[0] + surface.Strip, p[1] + surface.PoleMargin) != src.Index(r, c)) { partition = false; break; }
            }
            Check(seam && poles && partition, label + ": a physical point and its strip images reach the same source sample and travel cell; past a pole line the pole node; column n-1 is column 0; pole rows hold no cell; every other sample lies in one travel cell and maps back to itself");

            // ---- a1 (MMR-288): the generated variants drawn as vein graphs, visual only ----
            var plan = MagicGround.Plan(baseSnapshot, magic);
            var again = MagicGround.Plan(overlay.Current, magic);
            var veins = plan.Graph;
            Check(plan.Stamps.Count > 0 && veins.Sha256() == again.Graph.Sha256() && plan.Sha256() == again.Sha256() && plan.Thinned == 0,
                  label + ": two builds of the vein graph and its plan are equal (graph " + veins.Sha256().Substring(0, 12) + ", plan " + plan.Sha256().Substring(0, 12) + "): " + veins.Zones.Count + " zones, " + veins.Sources.Count +
                  " sources, " + veins.Branches + " branches, " + veins.Nodes.Count + " nodes (" + veins.Nodes.Count(v => v.OnWater) + " on water), " + plan.Stamps.Count + " land stamps (" + String.Join(", ", plan.ByKind.Select(kv => kv.Key + " " + kv.Value)) + ")");
            // Zones partition the drawable mutated samples (pole rows and unknown looks aside), one look and origin each.
            int mutated = src.Variant.Count(v => v >= 0) - CountColumn(src);
            var seen = new HashSet<int>(); bool zonesOk = true;
            foreach (var z in veins.Zones)
                foreach (int i in z.Samples)
                {
                    var v = src.Catalogue[baseSnapshot.VariantAt(i)]; string look; if (!table.HiddenLooks.TryGetValue(v.School, out look)) look = v.School;
                    if (!seen.Add(i) || look != z.Look || z.Live || src.PoleRow(i / src.Side)) zonesOk = false;
                }
            int skippedSamples = veins.Skipped.Where(kv => kv.Key == "poleRow" || kv.Key == "noLook").Sum(kv => kv.Value);
            Check(zonesOk && seen.Count + skippedSamples == mutated && veins.MutatedSamples == mutated && veins.Zones.All(z => z.Sources.Count > 0 && z.NodeCount >= z.Sources.Count),
                  label + ": the " + veins.Zones.Count + " zones partition the " + seen.Count + " mutated non-pole samples (water samples included; " + skippedSamples + " pole-row samples aside), one look school each, each with a source");
            // Each source has one burst; bursts are only at sources.
            var bursts = veins.Stamps.Where(st => st.Kind == VeinKind.Burst).ToList();
            bool burstOk = veins.Sources.All(so => bursts.Count(b => b.Node == so.Node && b.X == veins.Nodes[so.Node].X && b.Y == veins.Nodes[so.Node].Y) == 1) && bursts.All(b => veins.Nodes[b.Node].IsSource)
                && veins.Zones.Where(z => !z.Live).All(z => Math.Abs(baseSnapshot.PotencyAt(veins.Nodes[veins.Sources[z.Sources[0]].Node].Sample) - z.MaxPotency) < 1e-9);
            Check(burstOk && bursts.Count == veins.Sources.Count,
                  label + ": every source has exactly one burst stamp at its node and no burst lies elsewhere; each generated zone's first source is its potency maximum");
            // Every other stamp lies on a graph edge and follows it: trunk centred on its edge, tip on its edge, fork within 0.1 L of a child edge.
            double eps = 1e-6; int trunks = 0, tips = 0, forks = 0; bool onEdges = true, yawOk = true;
            Func<double, double, double> angle = (a, b) => { double d = (b - a) % (2 * Math.PI); if (d > Math.PI) d -= 2 * Math.PI; if (d < -Math.PI) d += 2 * Math.PI; return Math.Abs(d); };
            foreach (var st in veins.Stamps.Where(st => st.Kind != VeinKind.Burst))
            {
                var node = veins.Nodes[st.Node];
                if (st.Kind == VeinKind.Fork)
                {
                    forks++;
                    var near = node.Children.Min(ch => SegmentDistance(veins, node.Index, ch, st.X, st.Y));
                    var two = node.Children.OrderByDescending(ch => veins.Nodes[ch].Width).ThenBy(ch => ch).Take(2).Select(ch => EdgeOf(veins, ch).Yaw).ToArray();
                    if (node.Children.Count < 2 || near > 0.1 * st.Length + eps) onEdges = false;
                    if (Math.Abs(angle(two[0], st.Yaw) + angle(st.Yaw, two[1]) - angle(two[0], two[1])) > 1e-6) yawOk = false;
                    continue;
                }
                var e = veins.Edges[st.Edge];
                if (e.Child != st.Node) onEdges = false;
                double distance = SegmentDistance(veins, e.Parent, e.Child, st.X, st.Y);
                if (st.Kind == VeinKind.Trunk) { trunks++; if (node.Children.Count == 0 || distance > eps || Math.Abs(veins.DeltaX(veins.Nodes[e.Parent].X, st.X) * 2 - veins.DeltaX(veins.Nodes[e.Parent].X, node.X)) > eps) onEdges = false; }
                else { tips++; if (node.Children.Count != 0 || distance > eps) onEdges = false; }
                if (angle(st.Yaw, e.Yaw) > 1e-9) yawOk = false;
            }
            var graphIds = new HashSet<string>(veins.Stamps.Select(v => v.Id), StringComparer.Ordinal);
            bool decalYaw = plan.Stamps.Where(st => graphIds.Contains(st.Id)).All(st => angle(st.Decal.Yaw, veins.Stamps.First(v => v.Id == st.Id).Yaw + (magic.FlipU ? Math.PI : 0)) < 1e-9);
            var edgeIndex = new Dictionary<int, int>(); for (int k = 0; k < veins.Edges.Count; k++) edgeIndex[veins.Edges[k].Child] = k;
            double fitLength; int branchNodes = veins.Nodes.Count(v => v.Children.Count >= 2);
            bool forksFit = veins.Stamps.Where(st => st.Kind == VeinKind.Fork).All(st => MagicVeins.ForkFits(veins, veins.Nodes[st.Node], edgeIndex, magic, out fitLength) && Math.Abs(st.Length - fitLength) < 1e-9 && st.Length <= magic.StampLength + 1e-9)
                && forks == veins.Nodes.Count(v => MagicVeins.ForkFits(veins, v, edgeIndex, magic, out fitLength));
            Check(onEdges && forksFit && trunks + tips == veins.Edges.Count,
                  label + ": no isolated splotch: every non-burst stamp lies on a graph edge (" + trunks + " trunks centred on their edge, " + tips + " tips on their edge, " + forks + " forks within 0.1 L of a child edge at the " + branchNodes +
                  " branching nodes their children fit, each fork's length fitted to its children's angle)");
            Check(yawOk && decalYaw, label + ": every trunk and tip stamp's yaw is its edge's direction (growth toward +u), every fork's lies between its two widest children; the decals carry them (flipU " + magic.FlipU + ")");
            // Veins thin and fade toward the zone edge (gaps fix): by the owner sample's depth inside its zone (8-connected sample steps from the
            // nearest sample outside it, computed here on its own: 1 on the rim, 4 standing for 4 or more) the drawn trunks' and tips' mean width
            // is lower on the rim than inside it and the mean alpha is lowest on the rim; node widths fall with order (orders 0-2, 3-5, 6-8, 9+:
            // with distance from the source), and fewer than half the land nodes sit at minWidth. (The deepest band need not be the widest: a
            // zone's deep interior can lie far from its sources.)
            bool widths = veins.Edges.All(e => veins.Nodes[e.Child].Width <= veins.Nodes[e.Parent].Width + 1e-12)
                && veins.Nodes.All(v => v.Width >= magic.MinWidth - 1e-12 && v.Width <= magic.Width + 1e-12) && veins.Nodes.Where(v => v.Children.Count == 0).All(v => Math.Abs(v.Width - magic.MinWidth) < 1e-12);
            var byOrder = veins.Nodes.GroupBy(v => Math.Min(v.Order, 12)).OrderBy(gr => gr.Key).Select(gr => Math.Round(gr.Average(v => v.Width), 2)).ToList();
            var orderBands = veins.Nodes.Where(v => !v.OnWater).GroupBy(v => Math.Min(v.Order / 3, 3)).OrderBy(gr => gr.Key).Select(gr => Math.Round(gr.Average(v => v.Width), 3)).ToList();
            var depthOf = ZoneDepths(veins, src);
            var lines = plan.Stamps.Where(st => st.Kind == VeinKind.Trunk || st.Kind == VeinKind.Tip).ToList();
            var depthBands = Enumerable.Range(1, 4).Select(d => lines.Where(st => Math.Min(4, depthOf[st.Node]) == d).ToList()).ToList();
            var widthByDepth = depthBands.Select(b => b.Count == 0 ? Double.NaN : Math.Round(b.Average(st => st.Decal.HalfWidth * 2), 3)).ToList();
            var alphaByDepth = depthBands.Select(b => b.Count == 0 ? Double.NaN : Math.Round(b.Average(st => st.Alpha), 3)).ToList();
            Func<List<double>, bool> rising = l => l.All(v => !Double.IsNaN(v)) && l.Skip(1).All(v => l[0] < v);
            double atMin = (double)veins.Nodes.Count(v => !v.OnWater && Math.Abs(v.Width - magic.MinWidth) < 1e-12) / Math.Max(1, veins.Nodes.Count(v => !v.OnWater));
            bool falling = orderBands.Zip(orderBands.Skip(1), (a, b) => a >= b - 1e-9).All(x => x) && orderBands[0] > orderBands[orderBands.Count - 1];
            double rimWidth = depthBands[0].Average(st => st.Decal.HalfWidth * 2), innerWidth = depthBands.Skip(1).SelectMany(b => b).Average(st => st.Decal.HalfWidth * 2);
            Check(widths && rimWidth < innerWidth && rising(alphaByDepth) && falling && atMin < 0.5,
                  label + ": veins thin and fade toward the zone edge: drawn trunk and tip mean width by depth in the zone (rim 1 .. 4+) " + String.Join(" ", widthByDepth) + " (rim " + rimWidth.ToString("0.###", CultureInfo.InvariantCulture) + " < inside " + innerWidth.ToString("0.###", CultureInfo.InvariantCulture) + "), mean alpha " + String.Join(" ", alphaByDepth) +
                  "; land node mean width by order band (0-2, 3-5, 6-8, 9+) " + String.Join(" ", orderBands) + "; " + (100 * atMin).ToString("0.0", CultureInfo.InvariantCulture) + "% of land nodes at minWidth " + magic.MinWidth +
                  " (never wider outward, at most " + magic.Width + "); mean node width by order " + String.Join(" ", byOrder));
            // Land stamps: never on a water node or a water cell; the water side of the graph is left to MMR-290.
            bool landOnly = plan.Stamps.All(st => !veins.Nodes[st.Node].OnWater && !MagicVeins.OnWaterCell(src, st.Decal.X, st.Decal.Y));
            int waterStamps = veins.Stamps.Count(st => st.OnWater), waterNodes = veins.Nodes.Count(v => v.OnWater);
            var shoreBursts = plan.Stamps.Where(st => !graphIds.Contains(st.Id)).ToList();
            int droppedStamps = plan.Components.Where(kv => kv.Key.EndsWith("DroppedStamps", StringComparison.Ordinal)).Sum(kv => kv.Value);
            Check(landOnly && plan.Stamps.Count - shoreBursts.Count + droppedStamps + waterStamps + plan.Skipped.Where(kv => kv.Key == "noAtlasRow").Sum(kv => kv.Value) == veins.Stamps.Count && veins.ShoreEdges().Any()
                  && shoreBursts.All(st => st.Kind == VeinKind.Burst && st.Id.EndsWith(".shore", StringComparison.Ordinal) && st.AtlasColumn == magic.BurstColumn)
                  && shoreBursts.Where(sb => !sb.Live).All(sb => plan.Stamps.Where(o => o.Kind == VeinKind.Burst && o != sb).All(o => Math.Sqrt(Math.Pow(veins.DeltaX(o.Decal.X, sb.Decal.X), 2) + Math.Pow(o.Decal.Y - sb.Decal.Y, 2)) >= magic.SourceSpacing / 2)),
                  label + ": no land stamp lies on a water node or a water travel cell; " + waterNodes + " water nodes and " + waterStamps + " water stamps are left to MMR-290 (" + veins.ShoreEdges().Count() + " edges cross the shore); " +
                  "every drawn stamp is a graph land stamp or one of " + shoreBursts.Count + " shore bursts (none within sourceSpacing / 2 of another burst); " + droppedStamps + " land stamps of components too small to draw");
            // No isolated splotch (gaps fix): the drawn stamps joined where their bounding circles touch (columns wrapping); every group holds a
            // burst and at least minStamps stamps. With no water layer (this branch) the land stamps alone; with one (MMR-290) the land stamps of
            // that plan plus the graph's water stamps.
            var planWater = MagicGround.Plan(baseSnapshot, magic, true);
            var alone = Groups(src, plan.Stamps.Select(st => Box(st.Decal.X, st.Decal.Y, st.Decal.HalfLength * 2, st.Decal.HalfWidth * 2, st.Kind == VeinKind.Burst)).ToList());
            var withWater = Groups(src, planWater.Stamps.Select(st => Box(st.Decal.X, st.Decal.Y, st.Decal.HalfLength * 2, st.Decal.HalfWidth * 2, st.Kind == VeinKind.Burst))
                .Concat(veins.Stamps.Where(st => st.OnWater && !planWater.Dropped.Contains(st.Id)).Select(st => Box(st.X, st.Y, st.Length, st.Width, st.Kind == VeinKind.Burst))).ToList());
            var zonesAlone = plan.ZonesByStamps();
            // Thinning past maxDecals (here half the plan) takes generated stamps outermost first, so what stays still reaches its bursts.
            int cap = magic.MaxDecals; magic.MaxDecals = plan.Stamps.Count / 2;
            var thin = MagicGround.Plan(baseSnapshot, magic); magic.MaxDecals = cap;
            var thinGroups = Groups(src, thin.Stamps.Select(st => Box(st.Decal.X, st.Decal.Y, st.Decal.HalfLength * 2, st.Decal.HalfWidth * 2, st.Kind == VeinKind.Burst)).ToList());
            Check(thin.Thinned > 0 && thin.Stamps.Count == Math.Max(plan.Stamps.Count / 2, plan.Stamps.Count(st => st.Kind == VeinKind.Burst || st.Live)) && thinGroups[1] == 0 && thinGroups[2] == 0 && plan.Thinned == 0 && plan.Stamps.Count <= magic.MaxDecals,
                  label + ": thinning to " + plan.Stamps.Count / 2 + " (maxDecals " + magic.MaxDecals + " leaves this world unthinned) drops " + thin.Thinned + " generated stamps outermost first; every kept stamp still reaches a burst (" + thinGroups[0] + " groups, " + thinGroups[1] + " stamps without one)");
            int smallZones = Enumerable.Range(1, magic.MinStamps - 1).Sum(c => plan.Stamps.GroupBy(st => st.Zone).Count(gr => gr.Count() == c));
            Check(alone[1] == 0 && alone[2] == 0 && alone[3] == 0 && withWater[1] == 0 && withWater[3] == 0 && smallZones == 0,
                  label + ": no isolated splotch: of " + alone[0] + " drawn land stamp groups (stamps joined where their circles touch) none lacks a burst (" + alone[1] + " stamps), none is smaller than minStamps " + magic.MinStamps +
                  " (" + alone[3] + "), no stamp touches no other (" + alone[2] + "); zones drawing 0 / 1 / 2 / 3+ stamps " + String.Join(" / ", zonesAlone.Values) + " (" + smallZones + " zones draw 1 to " + (magic.MinStamps - 1) + "); " +
                  "components " + String.Join(", ", plan.Components.Select(kv => kv.Key + " " + kv.Value)) + "; with MMR-290's water stamps drawn (less the " + planWater.Dropped.Count(id => veins.Stamps.Any(v => v.Id == id && v.OnWater)) + " of components too small to draw): " + planWater.Stamps.Count + " land stamps, " + withWater[0] + " groups, " + withWater[1] + " stamps with no burst, " + withWater[3] + " groups below minStamps");
            // MMR-247 (acc5 c13: a tip centred on sea ground in game, A' (976.83, 958.72); on B' two on the sea and three in lakes): no drawn land
            // stamp is centred where the scene shows water, recomputed here from the lake planes and the lower of the encoded and the collision
            // height (what the acceptance probe reads), without the water layer and with it; the graph sends those centres to the water layer.
            int lakesUnmatched, lakesHidden;
            var sceneLakes = GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out lakesUnmatched, out lakesHidden);
            var wetAlone = Wet(surface, sceneLakes, plan); var wetWithWater = Wet(surface, sceneLakes, planWater);
            int byPlane = veins.Stamps.Count(st => st.OnWater && !veins.Nodes[st.Node].OnWater && !MagicVeins.OnWaterCell(src, st.X, st.Y) && MagicVeins.ShowsWater(src, st.X, st.Y)
                && !(src.Variant[src.SampleAt(st.X, st.Y)] >= 0 && MagicVeins.WaterCore(src, src.Catalogue[src.Variant[src.SampleAt(st.X, st.Y)]].Core)));
            Check(wetAlone.Count == 0 && wetWithWater.Count == 0 && src.Lakes.Count == sceneLakes.Count,
                  label + ": no drawn land stamp is centred on shown water (the sea below the datum or a shown lake plane, by the lower of the encoded and the collision height): " + wetAlone.Count + " of " + plan.Stamps.Count +
                  " without the water layer, " + wetWithWater.Count + " of " + planWater.Stamps.Count + " with it" + (wetWithWater.Count > 0 ? " (" + String.Join("; ", wetWithWater.Take(4).Select(st => st.Id + " at " + st.Decal.X.ToString("0.##", CultureInfo.InvariantCulture) + ", " + st.Decal.Y.ToString("0.##", CultureInfo.InvariantCulture))) + ")" : "") +
                  "; " + byPlane + " graph stamps on land cells and land nodes are water stamps by the plane test alone (" + sceneLakes.Count + " lake planes)");
            world["wetCentres"] = new Dictionary<string, object> { ["landStampsAlone"] = wetAlone.Count, ["landStampsWithWater"] = wetWithWater.Count, ["waterStampsByPlaneTest"] = byPlane };
            bool looksAgree = plan.Stamps.All(s =>
            {
                var node = veins.Nodes[s.Node]; var v = src.Catalogue[baseSnapshot.VariantAt(node.Sample)]; string look; if (!table.HiddenLooks.TryGetValue(v.School, out look)) look = v.School;
                int column = s.Kind == VeinKind.Trunk ? magic.TrunkColumn : s.Kind == VeinKind.Fork ? magic.ForkColumn : s.Kind == VeinKind.Tip ? magic.TipColumn : magic.BurstColumn;
                return s.Core == v.Core && s.Look == look && s.Decal.Material == "mnbl_magic_" + look && s.AtlasRow == magic.RowOf(v.Core) && s.AtlasColumn == column && !s.Live
                    && s.Decal.Atlas[0] == .25f && s.Decal.Atlas[1] == .25f && s.Decal.Atlas[2] == column / 4f && s.Decal.Atlas[3] == s.AtlasRow / 4f
                    && s.Decal.X >= surface.Strip && s.Decal.X < surface.Strip + surface.LogicalWidth && s.Decal.HalfWidth * 2 >= magic.MinWidth - 1e-9;
            });
            Check(looksAgree, label + ": each stamp has its owner node's look (hidden schools by hiddenLooks), material, atlas row by core group and column by kind (" + String.Join(", ", plan.BySchool.Select(kv => kv.Key + " " + kv.Value)) + "; groups " + String.Join(", ", plan.ByGroup.Select(kv => kv.Key + " " + kv.Value)) + ")");
            // MMR-247 (D): no module texture has a rect in the campaign map's decal atlas, so every stamp carries its vanilla fallback: bursts the
            // village stamp, every other kind the khuzait stroke turned a quarter, tinted toward the look's colour with the stamp's alpha.
            bool fallbacksOk = plan.Stamps.All(s =>
            {
                var f = s.Kind == VeinKind.Burst ? magic.BurstFallback : magic.StrokeFallback; var d = s.Decal;
                return d.Fallback == f.Material && d.FallbackAtlas.SequenceEqual(f.Atlas) && d.FallbackTurn == f.Turn && d.FallbackAlong == f.Along && d.FallbackAcross == f.Across
                    && d.FallbackTint == MagicGround.FallbackTint(magic.FallbackColours[s.Look], f.Core, s.Alpha) && (d.FallbackTint >> 24) == (uint)Math.Round(255 * s.Alpha);
            });
            uint fireStroke = MagicGround.FallbackTint(new[] { 240, 120, 50 }, new[] { 191, 168, 146 }, 1), faded = MagicGround.FallbackTint(new[] { 255, 255, 255 }, new[] { 128, 128, 128 }, .35);
            Check(fallbacksOk && fireStroke == 0xFFFF7A1Cu && faded == 0x59FFFFFFu,
                  label + ": every stamp carries its vanilla fallback (bursts " + magic.BurstFallback.Material + ", the rest " + magic.StrokeFallback.Material + " turned a quarter), its tint 255 x the linear look colour / the linear stamp core, clamped, alpha the stamp's (fire on the stroke " + fireStroke.ToString("x8", CultureInfo.InvariantCulture) + ")");
            // Coverage: the share of land zone samples a vein node passes within spacing of (recorded; the art's halo covers the rest).
            var landSamples = veins.Zones.SelectMany(z => z.Samples).Where(i => !MagicVeins.WaterCore(src, src.Catalogue[baseSnapshot.VariantAt(i)].Core) && !MagicVeins.OnWaterCell(src, src.Canonical(src.Logical(i / src.Side, i % src.Side)[0]), src.Logical(i / src.Side, i % src.Side)[1] + src.PoleMargin)).ToList();
            double covered = Coverage(veins, src, landSamples, magic.Spacing);
            Check(covered >= 0.8, label + ": " + (100 * covered).ToString("0.0", CultureInfo.InvariantCulture) + "% of the " + landSamples.Count + " land zone samples have a vein node within spacing (" + magic.Spacing + " units)");
            world["veins"] = veins.Record(); ((Dictionary<string, object>)world["veins"])["landCoverage"] = Math.Round(covered, 4); ((Dictionary<string, object>)world["veins"])["meanWidthByOrder"] = byOrder;
            ((Dictionary<string, object>)world["veins"])["graphSha256"] = veins.Sha256();
            ((Dictionary<string, object>)world["veins"])["landNodesAtMinWidth"] = Math.Round(atMin, 4); ((Dictionary<string, object>)world["veins"])["meanLandWidthByOrderBand"] = orderBands;
            ((Dictionary<string, object>)world["veins"])["meanDrawnWidthByZoneDepth"] = widthByDepth; ((Dictionary<string, object>)world["veins"])["meanDrawnAlphaByZoneDepth"] = alphaByDepth;
            world["fragments"] = new Dictionary<string, object> { ["minStamps"] = magic.MinStamps, ["components"] = plan.Components, ["zonesByStamps"] = zonesAlone, ["shoreBursts"] = shoreBursts.Count, ["droppedStamps"] = droppedStamps,
                ["groups"] = alone[0], ["withWaterDrawn"] = new Dictionary<string, object> { ["landStamps"] = planWater.Stamps.Count, ["components"] = planWater.Components, ["zonesByStamps"] = planWater.ZonesByStamps(), ["groups"] = withWater[0] } };
            var navFaces = Enumerable.Range(0, navigation.Mesh.FaceCount).Select(f => { var c = navigation.Mesh.Centroid(f); return new FaceSample(f, c[0], c[1], navigation.Mesh.Groups[f]); }).ToArray();
            var baseShares = WorldSurfaceOverlay.LandShares(baseSnapshot);
            var graphTypes = navigationInput.LandTypeCounts;
            bool sharesAreCells = graphTypes.All(kv => ((Dictionary<string, object>)baseShares[kv.Key])["cells"].Equals(kv.Value)) && (int)baseShares["landCells"] == graphTypes.Values.Sum();
            Check(baseSnapshot.RetypedCells.Length == 0 && MagicFaces.Build(baseSnapshot, navFaces.Length, i => navFaces[i]) == null && sharesAreCells,
                  label + ": the generated variants retype no cell and no face (RT-20): the land shares are cells.bin's own (" + String.Join(", ", graphTypes.Select(kv => kv.Key + " " + kv.Value)) + ")");
            world["generated"] = plan.Record();
            if (dump != null) Dump(Path.Combine(dump, label), plan, src, source, table, cells, magic, null);
            // MMR-262: each magic decal reaches its whole footprint on the shaped relief (the frame's z stays the centre's height).
            var reaches = plan.Stamps.Select(st => FootprintReach(surface, st.Decal)).ToList();
            bool depthOk = plan.Stamps.Select((st, k) => st.Decal.HalfDepth >= MagicGround.DecalHalfDepth && reaches[k] <= MapDressing.DepthReach * st.Decal.HalfDepth + 1e-9
                && Math.Abs(st.Decal.HalfDepth - Math.Max(MagicGround.DecalHalfDepth, reaches[k] / MapDressing.DepthReach)) < 1e-9).All(ok => ok);
            int deepened = plan.Stamps.Count(st => st.Decal.HalfDepth > MagicGround.DecalHalfDepth), beyond = reaches.Count(rr => rr > MagicGround.DecalHalfDepth);
            double deepest = plan.Stamps.Count == 0 ? 0 : plan.Stamps.Max(st => st.Decal.HalfDepth);
            var deepestAt = plan.Stamps.OrderByDescending(st => st.Decal.HalfDepth).Select(st => new[] { Math.Round(st.Decal.X, 3), Math.Round(st.Decal.Y, 3) }).FirstOrDefault();
            world["decalDepth"] = new Dictionary<string, object> { ["stamps"] = plan.Stamps.Count, ["deepened"] = deepened, ["over10"] = plan.Stamps.Count(st => st.Decal.HalfDepth > 10), ["max"] = Math.Round(deepest, 3),
                ["maxAt"] = deepestAt, ["beyondFixedDepth"] = beyond, ["reachShare"] = MapDressing.DepthReach };
            Check(depthOk && plan.Stamps.Count > 0,
                  label + ": every magic decal's half depth is max(3, its footprint's reach / 0.85): " + deepened + " of " + plan.Stamps.Count + " deepened (up to " + deepest.ToString("0.##", CultureInfo.InvariantCulture)
                  + "), " + beyond + " of which a fixed 3 would have clipped; the plan's digest leaves the depth out");

            // The shared decal budget with the map dressing (roads MMR-257, water MMR-285): whole world (every decal is drawn once, at its image
            // nearest the camera) and the most in any closest-camera window (58 x 37 units at distance 38, MMR-249). The graph's water stamps
            // (MMR-290's, not drawn yet) are counted on top as the projected total.
            var dressing = MapDressing.Build(bundle, surface, null, source, roads, looks);
            var all = plan.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y }).Concat(dressing.Decals.Select(d => new[] { d.X, d.Y })).Concat(dressing.Water.Select(d => new[] { d.X, d.Y })).ToList();
            var projected = all.Concat(veins.Stamps.Where(st => st.OnWater).Select(st => new[] { st.X, st.Y })).ToList();
            int window = WindowMax(all, surface, 58, 37), windowFar = WindowMax(all, surface, 116, 74), windowProjected = WindowMax(projected, surface, 58, 37), windowProjectedFar = WindowMax(projected, surface, 116, 74);
            world["decalBudget"] = new Dictionary<string, object> { ["magic"] = plan.Stamps.Count, ["roads"] = dressing.Decals.Count, ["water"] = dressing.Water.Count, ["wholeWorld"] = all.Count,
                ["closestViewWindow"] = new[] { 58, 37 }, ["closestViewMax"] = window, ["doubleViewWindow"] = new[] { 116, 74 }, ["doubleViewMax"] = windowFar, ["budgetInView"] = 5000,
                ["magicWaterStamps"] = waterStamps, ["projectedWholeWorld"] = projected.Count, ["projectedClosestViewMax"] = windowProjected, ["projectedDoubleViewMax"] = windowProjectedFar };
            Check(window < 5000 && windowFar < 5000 && windowProjectedFar < 5000 && plan.Stamps.Count <= magic.MaxDecals,
                  label + ": decal budget shared with roads and water: magic " + plan.Stamps.Count + " + roads " + dressing.Decals.Count + " + water " + dressing.Water.Count + " = " + all.Count + " whole world; at most " + window +
                  " in a closest-camera 58 x 37 window and " + windowFar + " in a 116 x 74 one (in-view budget about 5,000); with the graph's " + waterStamps + " water stamps " + projected.Count + " whole world, " + windowProjected + " / " + windowProjectedFar + " in view");

            // ---- a2: a live mutation ----
            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            var town = Json.A(graph, "settlements").Cast<IDictionary<string, object>>().First(s => Json.S(s, "kind") == "town" && s.ContainsKey("point"));
            var tp = Json.Point(town, "point");
            int centre = src.SampleAt(tp[0] + surface.Strip, tp[1] + surface.PoleMargin);
            int cr = centre / src.Side, cc = centre % src.Side;
            Func<int, string> naturalCore = i => { BiomeTable.Row row; return table.Rows.TryGetValue((int)source.Biome[i], out row) ? row.Core : null; };
            Func<string, string, int> variantOf = (core, school) => { var v = src.Catalogue.FirstOrDefault(x => x.Core == core && x.School == school); return v == null ? -1 : v.Index; };
            var cast = new List<MutationCell>();
            for (int dr = -4; dr <= 4; dr++) for (int dc = -4; dc <= 4; dc++)
            {
                if (dr * dr + dc * dc > 16 || src.PoleRow(cr + dr)) continue;
                int i = src.Index(cr + dr, cc + dc); string core = naturalCore(i); int v = core == null ? -1 : variantOf(core, "fire");
                if (v >= 0) cast.Add(new MutationCell(cr + dr, src.Wrap(cc + dc), v, 0.6f));
            }
            // A hidden school (blood looks and retypes as infernal), water on cold ground (Snow), and an ocean-core sample (never retyped).
            Func<Func<int, bool>, int> find = pred => { for (int r = 2; r < n1 - 1; r++) for (int c = 0; c < n1; c++) { int i = src.Index(r, c); if (Math.Abs(r - cr) > 6 && pred(i)) return i; } return -1; };
            Func<int, bool> landSample = i => { int cell = src.CellOfSample(i / src.Side, i % src.Side); return cell >= 0 && cells.Classes[cell] == TravelCells.Land; };
            int blood = find(i => naturalCore(i) == "desert" && landSample(i)), frost = find(i => naturalCore(i) == "cold_tundra" && landSample(i)), sea = find(i => naturalCore(i) == "ocean");
            if (blood >= 0) cast.Add(new MutationCell(blood / src.Side, blood % src.Side, variantOf("desert", "blood"), 0.5f));
            if (frost >= 0) cast.Add(new MutationCell(frost / src.Side, frost % src.Side, variantOf("cold_tundra", "water"), 0.5f));
            if (sea >= 0) cast.Add(new MutationCell(sea / src.Side, sea % src.Side, variantOf("ocean", "fire"), 0.6f));
            cast.RemoveAll(m => m.Variant < 0);
            var change = overlay.Apply(cast, new[] { new MagicNode("fire", tp[0] + surface.Strip, tp[1] + surface.PoleMargin) });
            var live = overlay.Current;
            var liveCells = new HashSet<int>(cast.Select(m => src.CellOfSample(m.Row, m.Column)).Where(c => c >= 0));
            bool onlyLive = live.RetypedCells.All(liveCells.Contains);
            bool withinLand = live.RetypedCells.All(c => landValid.Contains(live.CellTypes[c]) && cells.Classes[c] == TravelCells.Land);
            bool expected = live.RetypedCells.All(c =>
            {
                var types = cast.Where(m => src.CellOfSample(m.Row, m.Column) == c).Select(m => src.MutatedTypeId(m.Variant)).Where(t => t != 0).ToList();
                return types.Count > 0 && types.GroupBy(t => t).Max(g => g.Count()) == types.Count(t => t == live.CellTypes[c]);
            });
            bool hidden = blood < 0 || live.CellTypes[src.CellOfSample(blood / src.Side, blood % src.Side)] == TerrainTypes.Id(table.MutatedType("infernal", "desert"));
            bool frozen = frost < 0 || live.CellTypes[src.CellOfSample(frost / src.Side, frost % src.Side)] == TerrainTypes.Id("Snow");
            int seaCell = sea < 0 ? -1 : src.CellOfSample(sea / src.Side, sea % src.Side);
            bool waterCore = sea < 0 || seaCell < 0 || live.CellTypes[seaCell] == 0 || cast.Any(m => src.CellOfSample(m.Row, m.Column) == seaCell && m.Variant != variantOf("ocean", "fire"));
            bool generatedStay = Enumerable.Range(0, cells.Classes.Length).Where(c => !liveCells.Contains(c)).All(c => live.CellTypes[c] == 0);
            Check(live.RetypedCells.Length > 0 && onlyLive && withinLand && expected && hidden && frozen && waterCore && generatedStay && change.ChangedCells.SequenceEqual(live.RetypedCells),
                  label + ": a live fire disc of " + cast.Count + " samples at " + Json.S(town, "id") + " (plus blood on desert, water on cold tundra, fire on an ocean core) retypes " + live.RetypedCells.Length +
                  " land cells, only cells holding live samples, each to its samples' MutatedType within landValid (no Dune): blood as infernal, water on cold tundra Snow, the ocean core nothing; generated-only cells keep their type");
            // Faces: every land face of a retyped cell, in every strip image, takes the cell's type; no other face changes.
            var faces = MagicFaces.Build(live, navFaces.Length, i => navFaces[i]);
            var mesh = navigation.Mesh;
            bool facesOk = faces != null && navFaces.All(f =>
            {
                int cell = src.CellAt(f.X, f.Y); bool retyped = cell >= 0 && live.CellTypes[cell] != 0 && landFaces.Contains(f.Group);
                return retyped ? faces[f.Index] == live.CellTypes[cell] : faces[f.Index] == 0;
            });
            int stripFaces = navFaces.Count(f => faces[f.Index] != 0 && (mesh.PhysicalColumns[f.Index] < cells.ColumnOffset || mesh.PhysicalColumns[f.Index] >= cells.ColumnOffset + cells.Cols));
            var imagesPerCell = navFaces.Where(f => faces[f.Index] != 0).GroupBy(f => src.CellAt(f.X, f.Y)).Select(g => g.Select(f => mesh.PhysicalColumns[f.Index]).Distinct().Count()).ToList();
            bool deckUntouched = navFaces.Where(f => f.Group == TerrainTypes.Id("Bridge") || !landFaces.Contains(f.Group)).All(f => faces[f.Index] == 0);
            Check(facesOk && deckUntouched && faces.Count(t => t != 0) >= live.RetypedCells.Length,
                  label + ": " + faces.Count(t => t != 0) + " navmesh faces take their cell's live type (" + stripFaces + " of them in strip images; up to " + (imagesPerCell.Count == 0 ? 0 : imagesPerCell.Max()) +
                  " images of one cell); bridge, mountain and water faces never change, so passability is unchanged");
            // Battle map: the retyped cells' class is faceClass[remapped type] in every strip image (RT-26).
            int width, height, originCell, periodCells; double cellX, cellY;
            width = (int)Math.Ceiling(surface.Extent / GeneratedBattleTerrainCellSize); height = width; cellX = surface.Extent / width; cellY = surface.Extent / height;
            var coordinates = PeriodicWorldCoordinates.FromSurface(surface);
            originCell = (int)Math.Round(coordinates.OriginX / cellX); periodCells = (int)Math.Round(coordinates.Width / cellX);
            var battle = new MagicBattleCells { Width = width, Height = height, OriginCell = originCell, PeriodCells = periodCells, CellX = cellX, CellY = cellY };
            Func<int, int, CellSamples> sample = (cx, y) =>
            {
                var s = new CellSamples();
                for (int sy = 0; sy < 3; sy++) for (int sx = 0; sx < 3; sx++)
                {
                    double px = (cx + .05 + .45 * sx) * cellX, py = (y + .05 + .45 * sy) * cellY; int cell = src.CellAt(px, py); bool middle = sx == 1 && sy == 1;
                    if (cell < 0) continue;
                    var nav = navigation.Cells[cell];
                    if (nav.Domain == "land") { s.Land.Add(nav.TerrainType); if (middle) s.CentreLand = nav.TerrainType; }
                    else if (nav.Domain == "sea") s.Sea.Add(nav.TerrainType);
                    else { s.Blocked.Add(nav.TerrainType); if (middle) s.CentreBlocked = nav.TerrainType; }
                }
                BiomeTable.Row biome; if (table.Rows.TryGetValue(surface.BiomeAt((cx + .5) * cellX, (y + .5) * cellY), out biome)) s.Biome = biome.Core;
                return s;
            };
            var request = new GeneratedEncounterResolver.MapRequest { Width = width, Height = height, OriginCell = originCell, PeriodCells = periodCells, Periodic = true, WorldId = "mnbl_magic_" + label, Sample = sample };
            WorldSurfaceOverlay current = overlay;
            request.Patches.Add((lx, y, s) => { string cls = battle.ClassAt(current.Current, catalogue.FaceClass, lx, y); if (cls != null) s.ClassOverride = cls; });
            var map = resolver.Rebuild(request);
            var keys = battle.Keys(src, live.RetypedCells);
            Func<long, string> expectedClass = key => { int y = (int)(key / 1000003), lx = (int)(key % 1000003); int cell = src.CellAt((originCell + lx + .5) * cellX, (y + .5) * cellY); return catalogue.FaceClass[TerrainTypes.Name(live.CellTypes[cell])]; };
            bool classes = keys.Count > 0 && keys.All(k => map.Choices[k].Class == expectedClass(k));
            int images = 0; bool imagesAgree = true;
            foreach (var k in keys)
            {
                int y = (int)(k / 1000003), lx = (int)(k % 1000003);
                var copies = Enumerable.Range(0, map.Width).Where(x => GeneratedEncounterResolver.CanonicalColumn(x, originCell, periodCells) == lx).ToList();
                images += copies.Count;
                if (copies.Any(x => map.Bytes[2 * (y * map.Width + x)] != map.Choices[k].Index)) imagesAgree = false;
            }
            // MMR-247: the class overrides keep the activation's PostBuild invariant: every land party finds one scene, every ship one naval scene.
            bool determined = map.LandDeterministic == map.LandCells && map.SeaDeterministic == map.SeaChecks;
            Check(classes && imagesAgree && images > keys.Count && determined,
                  label + ": the battle map gives the " + keys.Count + " battle cells of retyped cells faceClass[remapped type] (" + String.Join(", ", keys.Select(k => map.Choices[k].Class).GroupBy(c => c).Select(g => g.Key + " " + g.Count())) + "), the same index in all " + images + " strip images (RT-26); " +
                  "with the overrides land " + map.LandDeterministic + " of " + map.LandCells + " and sea " + map.SeaDeterministic + " of " + map.SeaChecks + " resolve one scene (MMR-247)");
            // A second change: the disc's inner samples turn weave (Forest) and its rim clears; RebuildCells re-chooses exactly the changed cells in every image.
            var second = new List<MutationCell>();
            foreach (var m in cast.Where(m => Math.Abs(m.Row - cr) <= 4 && Math.Abs(src.Wrap(m.Column - cc + n1 / 2) - n1 / 2) <= 4))
            {
                bool inner = (m.Row - cr) * (m.Row - cr) + Math.Pow(src.Wrap(m.Column - cc + n1 / 2) - n1 / 2, 2) <= 4;
                string core = naturalCore(src.Index(m.Row, m.Column));
                second.Add(new MutationCell(m.Row, m.Column, inner ? variantOf(core, "weave") : -1, 0.7f));
            }
            var change2 = overlay.Apply(second, null);
            var after = overlay.Current;
            var keys2 = battle.Keys(src, change2.ChangedCells);
            var bytesBefore = (byte[])map.Bytes.Clone();
            int written = resolver.RebuildCells(map, request, keys2);
            int expectedWritten = keys2.Sum(k => Enumerable.Range(0, map.Width).Count(x => GeneratedEncounterResolver.CanonicalColumn(x, originCell, periodCells) == (int)(k % 1000003)));
            var keySet = new HashSet<long>(keys2);
            bool untouched = true;
            for (int y = 0; y < map.Height && untouched; y++) for (int x = 0; x < map.Width; x++)
                if (!keySet.Contains((long)y * 1000003 + GeneratedEncounterResolver.CanonicalColumn(x, originCell, periodCells)) && map.Bytes[2 * (y * map.Width + x)] != bytesBefore[2 * (y * map.Width + x)]) { untouched = false; break; }
            bool reclassed = keys2.All(k =>
            {
                int y = (int)(k / 1000003), lx = (int)(k % 1000003); int cell = src.CellAt((originCell + lx + .5) * cellX, (y + .5) * cellY);
                var fresh = resolver.Choose(sample(originCell + lx, y), request.WorldId, lx / catalogue.BlockSize, y / catalogue.BlockSize);
                return after.CellTypes[cell] != 0 ? map.Choices[k].Class == catalogue.FaceClass[TerrainTypes.Name(after.CellTypes[cell])] : map.Choices[k].Class == fresh.Class;
            });
            Check(change2.ChangedCells.Length > 0 && written == expectedWritten && untouched && reclassed && after.Version == live.Version + 1 && live.LiveCount == cast.Count,
                  label + ": a second change (weave core, cleared rim) changes " + change2.ChangedCells.Length + " cells; RebuildCells rewrites their " + keys2.Count + " battle cells in all " + written +
                  " images (woodland where weave, the natural class where cleared) and nothing else; the earlier snapshot is unchanged (immutable)");

            // ---- r1: the land shares before and after (RT-20) ----
            world["mutation"] = new Dictionary<string, object> { ["town"] = Json.S(town, "id"), ["samples"] = cast.Count, ["retypedCells"] = live.RetypedCells.Length, ["faces"] = faces.Count(t => t != 0), ["battleCells"] = keys.Count,
                ["battleImages"] = images, ["landSharesBefore"] = change.SharesBefore, ["landSharesAfter"] = change.SharesAfter, ["second"] = change2.Record() };
            int retypedFromShares = (int)change.SharesAfter["landCells"] == (int)change.SharesBefore["landCells"] ? live.RetypedCells.Count(c => live.CellTypes[c] != cells.LandTypes[c]) : -1;
            int moved = change.SharesBefore.Keys.Union(change.SharesAfter.Keys).Where(k => k != "landCells").Sum(k => Math.Abs(CellsOf(change.SharesAfter, k) - CellsOf(change.SharesBefore, k)));
            Check(moved <= 2 * retypedFromShares && moved > 0 && (int)change.SharesAfter["landCells"] == (int)change.SharesBefore["landCells"],
                  label + ": land shares by type recorded before and after the mutation (r1): " + Shares(change.SharesBefore) + " -> " + Shares(change.SharesAfter) + "; the land cell count is unchanged");

            // ---- a3: the navigation cache key is unaffected ----
            var rebuilt = RuntimeNavigationBuilder.Build(NavigationInput.FromBundle(bundle, surface, navigationPolicy), navigationPolicy);
            Check(rebuilt.Sha256 == navigation.Sha256 && cellsBin.SequenceEqual(cellsBefore) && bundle.ReadVerified("compiled/travel/cells.bin").SequenceEqual(cellsBefore) && cells.LandTypes.SequenceEqual(TravelCells.Read(cellsBefore, cells.Cols, cells.Rows, cells.ColumnOffset, cells.RowOffset, cells.Spacing).LandTypes),
                  label + ": with live mutations applied the navmesh builds byte-identical (" + navigation.Sha256.Substring(0, 12) + ") and cells.bin and its land types are untouched (a3)");

            // ---- RT-37: disable keeps the generated ground ----
            var disabled = overlay.Disable();
            var planOff = MagicGround.Plan(overlay.Current, magic);
            Check(overlay.Current.RetypedCells.Length == 0 && overlay.Current.LiveCount == 0 && overlay.Current.MutationsDisabled && planOff.Sha256() == plan.Sha256() && Throws(() => overlay.Apply(cast, null)) && disabled.ChangedCells.Length > 0,
                  label + ": disabling mutations (RT-37) drops every live cell and retyped face, refuses later casts, and keeps the generated magic ground drawn exactly as before (" + planOff.Stamps.Count + " decals)");
            overlay.Replace(cast, null);
            Check(overlay.Current.LiveCount == cast.Count && !overlay.Current.MutationsDisabled && overlay.Current.RetypedCells.SequenceEqual(new MagicSnapshot(src, 0, cast.ToDictionary(m => src.Index(m.Row, m.Column)), null, false).RetypedCells),
                  label + ": a load's replay (Replace) restores the stored cells to the same retyped cells");

            // ---- installer over a recording target ----
            overlay.Replace(null, null);
            var recorder = new Recorder();
            var installer = new MagicGroundInstaller(recorder);
            var basePlan = MagicGround.Plan(overlay.Current, magic);
            installer.Sync(basePlan, x => x);
            int drawnBase = recorder.Live.Count;
            overlay.Apply(cast, null);
            var livePlan = MagicGround.Plan(overlay.Current, magic);
            int removedBefore = installer.Removed, createdBefore = installer.Created;
            installer.Sync(livePlan, x => x);
            int touched = (installer.Removed - removedBefore) + (installer.Created - createdBefore);
            var baseKeys = new HashSet<string>(basePlan.Stamps.Select(s => s.Key)); var liveKeys = new HashSet<string>(livePlan.Stamps.Select(s => s.Key));
            int expectedTouched = baseKeys.Count(k => !liveKeys.Contains(k)) + liveKeys.Count(k => !baseKeys.Contains(k));
            var castSamples = new HashSet<int>(cast.Select(m => src.Index(m.Row, m.Column)));
            var untouchedZones = new HashSet<int>(basePlan.Graph.Zones.Where(z => !z.Samples.Any(castSamples.Contains)).Select(z => z.Index));
            bool othersKept = basePlan.Stamps.Where(s => untouchedZones.Contains(s.Zone)).All(s => liveKeys.Contains(s.Key));
            bool liveDrawn = livePlan.Stamps.Where(s => s.Live).All(s => installer.DrawnStamps().Any(d => d.Key.Id == s.Id && d.Key.Live)) && livePlan.Stamps.Any(s => s.Live);
            // The generated graph is fixed per world (gaps fix): a generated stamp farther than (minStamps + 1) stamp lengths from every node the
            // cast hides keeps its decal; nearer ones may go (hidden, or a cut piece below minStamps).
            var hiddenNodes = livePlan.Graph.Nodes.Where(v => v.Hidden).ToList();
            double reach = (magic.MinStamps + 1) * magic.StampLength;
            Func<MagicStamp, double> fromCast = st => hiddenNodes.Count == 0 ? Double.PositiveInfinity : hiddenNodes.Min(v => Math.Sqrt(Math.Pow(livePlan.Graph.DeltaX(v.X, st.Decal.X), 2) + Math.Pow(v.Y - st.Decal.Y, 2)));
            var changedGenerated = basePlan.Stamps.Where(st => !liveKeys.Contains(st.Key)).ToList();
            bool farKept = changedGenerated.All(st => fromCast(st) <= reach) && livePlan.Stamps.Where(st => !st.Live).All(st => baseKeys.Contains(st.Key) || st.Id.EndsWith(".shore", StringComparison.Ordinal));
            installer.Refresh(x => x + surface.LogicalWidth);
            bool moved2 = recorder.Live.Values.All(p => p[0] >= surface.Strip + surface.LogicalWidth);
            installer.Remove();
            var missing = new Recorder { Resolves = m => m != "mnbl_magic_fire" };
            var partial = new MagicGroundInstaller(missing);
            partial.Sync(livePlan, x => x);
            world["liveRedraw"] = new Dictionary<string, object> { ["decals"] = touched, ["hiddenNodes"] = hiddenNodes.Count, ["generatedRemoved"] = changedGenerated.Count, ["reach"] = reach,
                ["liveAdded"] = livePlan.Stamps.Count(s => s.Live) };
            Check(farKept && hiddenNodes.Count > 0,
                  label + ": the generated veins are fixed per world: the cast hides " + hiddenNodes.Count + " generated nodes and removes " + changedGenerated.Count + " generated stamps, all within " + reach + " units of them; every other generated stamp keeps its decal (" +
                  touched + " decals redrawn, " + livePlan.Stamps.Count(s => s.Live) + " of them live)");
            Check(drawnBase == plan.Stamps.Count && liveDrawn && touched == expectedTouched && touched > 0 && othersKept && moved2 && recorder.Live.Count == 0 && partial.MissingMaterials.SequenceEqual(new[] { "mnbl_magic_fire" })
                  && partial.Errors.Count == 0 && partial.Live == livePlan.Stamps.Count(s => s.Decal.Material != "mnbl_magic_fire") && partial.NotDrawn == livePlan.Stamps.Count(s => s.Decal.Material == "mnbl_magic_fire"),
                  label + ": the installer draws every stamp, redraws only the " + touched + " decals of the zones a live change touched (" + untouchedZones.Count + " of " + basePlan.Graph.Zones.Count + " zones keep every stamp), moves them all on a seam refresh and removes them all; a material that does not resolve draws nothing, with no error");
            // MMR-262: after Scene.ClearAll (the map's teardown) the installer drops its handles with no target call; a Remove there is the control.
            var cleared = new Recorder();
            var dropping = new MagicGroundInstaller(cleared);
            dropping.Sync(livePlan, x => x);
            int drawnLive = dropping.Live;
            cleared.Cleared = true;
            int dropped = dropping.Drop();
            var control = new Recorder();
            var removing = new MagicGroundInstaller(control);
            removing.Sync(livePlan, x => x);
            control.Cleared = true;
            removing.Remove();
            world["teardown"] = new Dictionary<string, object> { ["dropped"] = dropped, ["engineCallsAfterClear"] = cleared.AfterClear, ["controlRemoveCallsAfterClear"] = control.AfterClear };
            Check(drawnLive == livePlan.Stamps.Count && dropped == drawnLive && cleared.AfterClear == 0 && dropping.Live == 0 && dropping.Removed == 0 && dropping.Dropped == drawnLive && dropping.Errors.Count == 0
                  && cleared.Live.Count == drawnLive && control.AfterClear == drawnLive,
                  label + ": on a cleared scene the installer drops all " + dropped + " magic decal handles with 0 target calls (a Remove there would make " + control.AfterClear + ")");
            // ---- a2 (MMR-288): MMR-261's recorded Nerdun fire cast grows veins from the capital, revealed outward over revealHours ----
            if (label == "A")
            {
                var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
                var sites = MutationSites.From(campaign, graph, source.Realms, surface.Strip, surface.PoleMargin);
                const string NerdunUid = "surface-city-2-13033-human_cold";
                string nerdun = sites.CityTown[NerdunUid]; var at = sites.Point[nerdun];
                var delta = MutationDelta.Read(File.ReadAllText(Path.Combine(repo, "tests/World/fixtures/mutation/nerdun-fire.delta.json"), new UTF8Encoding(false)), src, source.Seed);
                var edit = new MutationEdit { Day = 10, School = "fire", Site = nerdun, At = nerdun, Rule = "capital", Node = "mnbl-cast-fire-" + nerdun, Intensity = 4, City = NerdunUid, X = at[0], Y = at[1] };
                // The stored document (MMR-261's SyncData string) rebuilds the cast node and its growth start (MutationModel.Load -> MutationOperations.Grown).
                var castNodes = MutationOperations.Nodes(new[] { edit }).Select(kv => new MagicNode(kv.Key.School, kv.Key.X, kv.Key.Y)).ToList();
                var state = new MutationState { Producer = "p", World = source.Sha256, Request = "r", Day = 10, Edits = new List<MutationEdit> { edit }, Cells = delta.Cells, Nodes = castNodes };
                var model = new MutationModel(new MutationModel.Context { Source = src, WorldSha256 = source.Sha256, RequestSha256 = "r" });
                List<MutationCell> loadedCells; List<MagicNode> loadedNodes;
                bool loaded = model.Load(state.ToJson(), out loadedCells, out loadedNodes);
                var castOverlay = new WorldSurfaceOverlay(src);
                castOverlay.Replace(loadedCells, loadedNodes);
                var castPlan = MagicGround.Plan(castOverlay.Current, magic);
                var castAgain = MagicGround.Plan(castOverlay.Current, magic);
                var castVeins = castPlan.Graph;
                double since = 10 * 24.0, grownAt = since + magic.RevealHours;
                var liveZones = castVeins.Zones.Where(z => z.Live).ToList();
                var liveSources = castVeins.Sources.Where(so => so.Cast != null).ToList();
                var capital = liveSources.FirstOrDefault(so => castVeins.Nodes[so.Node].X == at[0] && castVeins.Nodes[so.Node].Y == at[1]);
                var liveStamps = castPlan.Stamps.Where(st => st.Live).ToList();
                var reveal = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var steps = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(f => castPlan.Revealed(since + f * magic.RevealHours)).ToList();
                for (int k = 0; k < steps.Count; k++) reveal[(k * 12).ToString("00", CultureInfo.InvariantCulture) + "h"] = steps[k].Stamps.Count(st => st.Live);
                var counts = steps.Select(pl => pl.Stamps.Count(st => st.Live)).ToList();
                bool growing = counts.Zip(counts.Skip(1), (a, b) => a <= b).All(x => x) && counts[0] > 0 && counts[0] < counts[2] && counts[2] < counts[4] && counts[4] == liveStamps.Count;
                bool nested = steps.Zip(steps.Skip(1), (a, b) => a.Stamps.All(b.Stamps.Contains)).All(x => x) && steps.All(pl => pl.Stamps.Count(st => !st.Live) == castPlan.Stamps.Count(st => !st.Live));
                bool outward = castVeins.Nodes.Where(v => v.Live && v.Parent >= 0).All(v => castVeins.Nodes[v.Parent].RevealAt <= v.RevealAt && v.RevealAt >= since && v.RevealAt <= grownAt)
                    && castVeins.Nodes.Where(v => v.Live).All(v => Math.Abs(v.RevealAt - (since + magic.RevealHours * v.Order / (double)castVeins.Nodes.Where(x => x.Live).Max(x => x.Order))) < 1e-9);
                bool before = castPlan.Revealed(since - 1).Stamps.Count(st => st.Live) == 0, continued = castPlan.Revealed(grownAt + 24 * 30).Sha256() == castPlan.Sha256();
                var capitalZone = capital == null ? null : castVeins.Zones[capital.Zone];
                Check(loaded && loadedNodes.Count == 1 && loadedNodes[0].Since == since && castPlan.Sha256() == castAgain.Sha256() && liveZones.Count > 0 && liveZones.All(z => z.Look == "fire")
                      && capital != null && liveSources.All(so => so.Cast == loadedNodes[0] && so.Since == since) && liveZones.All(z => z.Sources.Count > 0) && capitalZone.NodeCount > 1,
                      label + ": the recorded Nerdun fire cast (" + delta.Changed + " cells, " + loadedCells.Count(c => c.Variant >= 0) + " fire) loaded from its stored document grows " + liveZones.Count + " live zone(s) from the cast node at the capital " + nerdun +
                      " (its zone: " + capitalZone.NodeCount + " nodes; " + liveSources.Count + " live sources), deterministically; the node's growth starts at its batch day (" + since + " h)");
                Check(growing && nested && outward && before && continued,
                      label + ": the cast's " + liveStamps.Count + " live stamps appear outward by graph order over " + magic.RevealHours + " campaign hours (" + String.Join(", ", reveal.Select(kv => kv.Key + " " + kv.Value)) +
                      "): none before the cast, each later step a superset, parents before children; a Continue after the time has passed draws them fully grown");
                var castWater = MagicGround.Plan(castOverlay.Current, magic, true);
                int castWet = Wet(surface, sceneLakes, castPlan).Count + Wet(surface, sceneLakes, castWater).Count;
                Check(castWet == 0, label + ": with the cast, no drawn land stamp (live or generated) is centred on shown water: " + castWet + " of " + castPlan.Stamps.Count + " + " + castWater.Stamps.Count + " (without and with the water layer)");
                // The reveal clock (MagicGroundRuntime's, pure): install at the cast's start, tick to each reveal time (each step adds only the
                // newly revealed stamps and removes none), a tick before a step does nothing; a Continue 48 h later draws all; a start with no
                // campaign clock draws all, and its first real tick re-hides what that time does not show yet.
                var clockRec = new Recorder(); var clockInst = new MagicGroundInstaller(clockRec); var clock = new MagicRevealClock();
                clockInst.Sync(clock.Start(castPlan, since), x => x);
                bool clockOk = clockRec.Live.Count == castPlan.Revealed(since).Stamps.Count && !clock.Unknown;
                var times = castPlan.Stamps.Select(st => st.RevealAt).Where(t => t > since && !Double.IsInfinity(t)).Distinct().OrderBy(t => t).ToList();
                double last = since;
                foreach (var t in times)
                {
                    if (t - 1e-3 > last && clock.Tick(castPlan, t - 1e-3) != null) clockOk = false;
                    var stepPlan = clock.Tick(castPlan, t);
                    int removed0 = clockInst.Removed, created0 = clockInst.Created, before0 = clockRec.Live.Count;
                    if (stepPlan == null) { clockOk = false; continue; }
                    clockInst.Sync(stepPlan, x => x);
                    if (clockRec.Live.Count != castPlan.Revealed(t).Stamps.Count || clockInst.Removed != removed0 || clockInst.Created - created0 != clockRec.Live.Count - before0) clockOk = false;
                    last = t;
                }
                clockOk = clockOk && times.Count > 0 && clock.Tick(castPlan, grownAt + 100) == null && Double.IsPositiveInfinity(clock.Next) && clockRec.Live.Count == castPlan.Stamps.Count;
                var continueRec = new Recorder(); var continueInst = new MagicGroundInstaller(continueRec); var continued2 = new MagicRevealClock();
                continueInst.Sync(continued2.Start(castPlan, grownAt), x => x);
                var noClockRec = new Recorder(); var noClockInst = new MagicGroundInstaller(noClockRec); var noClock = new MagicRevealClock();
                noClockInst.Sync(noClock.Start(castPlan, Double.PositiveInfinity), x => x);
                int grownCount = noClockRec.Live.Count; bool unknown = noClock.Unknown;
                var rehide = noClock.Tick(castPlan, since + 12);
                if (rehide != null) noClockInst.Sync(rehide, x => x);
                bool noClockOk = unknown && grownCount == castPlan.Stamps.Count && rehide != null && noClockRec.Live.Count == castPlan.Revealed(since + 12).Stamps.Count && noClockInst.Removed > 0 && !noClock.Unknown
                    && MagicRevealClock.Timed(castPlan) == castPlan.Stamps.Count(st => st.Live);
                Check(clockOk && continueRec.Live.Count == castPlan.Stamps.Count && noClockOk,
                      label + ": the reveal clock: installed at the cast's start it draws " + castPlan.Revealed(since).Stamps.Count(st => st.Live) + " live stamps, each of " + times.Count + " reveal steps adds only its new stamps (none before it is due), a Continue at +" + magic.RevealHours +
                      " h draws all " + castPlan.Stamps.Count + "; a start with no campaign clock draws all " + grownCount + " and its first tick (+12 h) re-hides " + noClockInst.Removed);
                world["nerdunCast"] = new Dictionary<string, object> { ["deltaCells"] = delta.Changed, ["liveCells"] = loadedCells.Count, ["liveZones"] = liveZones.Count, ["liveSources"] = liveSources.Count,
                    ["liveNodes"] = new Dictionary<string, object> { ["land"] = castVeins.Nodes.Count(v => v.Live && !v.OnWater), ["water"] = castVeins.Nodes.Count(v => v.Live && v.OnWater) },
                    ["liveStamps"] = liveStamps.GroupBy(st => st.Kind).OrderBy(gr => gr.Key, StringComparer.Ordinal).ToDictionary(gr => gr.Key, gr => (object)gr.Count()),
                    ["liveWaterStamps"] = castVeins.Stamps.Count(st => st.Live && st.OnWater), ["revealHours"] = magic.RevealHours, ["since"] = since, ["revealedLiveStamps"] = reveal, ["capital"] = nerdun, ["maxOrder"] = castVeins.Nodes.Where(v => v.Live).Max(v => v.Order) };
                if (dump != null) Dump(Path.Combine(dump, label + "-nerdun"), castPlan, src, source, table, cells, magic, new Dictionary<string, object> { ["since"] = since, ["revealHours"] = magic.RevealHours, ["capital"] = at });
            }
            overlay.Replace(null, null);
        }

        // A magic-off world: no biome_variant layer.
        {
            var bundle = WorldDataBundle.Open(args[1], host);
            var surface = WorldSurface.FromBundle(bundle);
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            source.BiomeVariant = null;
            var input = NavigationInput.FromBundle(bundle, surface, navigationPolicy);
            var off = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, input.Cells, table));
            var plan = MagicGround.Plan(off.Current, magic);
            Check(!off.Source.Enabled && plan.Stamps.Count == 0 && plan.Skipped.ContainsKey("magicOff") && Throws(() => off.Apply(new[] { new MutationCell(10, 10, 0, 1f) }, null)) && off.Current.RetypedCells.Length == 0,
                  "a magic-off world (no biome_variant) loads with no magic ground and refuses a live mutation");
        }

        // The hot path (GeneratedMapScene.GetFaceTerrainType) allocates nothing, and nothing of the overlay reaches the navigation cache key.
        string scene = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/GeneratedMapScene.cs")).Replace("\r\n", "\n");
        int begin = scene.IndexOf("public new TerrainType GetFaceTerrainType(", StringComparison.Ordinal), end = begin < 0 ? -1 : scene.IndexOf("\n        }\n", begin, StringComparison.Ordinal);
        string body = begin < 0 || end < 0 ? "" : scene.Substring(begin, end - begin);
        Check(body.Length > 0 && body.IndexOf("new ", body.IndexOf('{'), StringComparison.Ordinal) < 0 && body.IndexOf("faceTypes", StringComparison.Ordinal) > 0 && !body.Contains("Linq") && !body.Contains("=>"),
              "GeneratedMapScene.GetFaceTerrainType answers from the face overlay with no allocation (no new, no lambda, no LINQ)");
        // Load order: the face overlay is built in GeneratedMapScene.Load (after the read, before the map counts as loaded), which
        // Campaign.LoadMapScene runs before OnGameLoaded's MobileParty.PreAfterLoad / ComputePathAfterLoad (installed IL, docs/magic-ground.md);
        // the battle patch is registered there, before AfterLoad installs the battle map, and the decals follow the battle map and the dressing.
        int load = scene.IndexOf("public new void Load()", StringComparison.Ordinal), begun = scene.IndexOf("MagicGroundRuntime.Begin(", load, StringComparison.Ordinal),
            read = scene.IndexOf("RuntimeWorldBackend.ReadCampaignMap(", load, StringComparison.Ordinal), loadedAt = scene.IndexOf("loaded = true; loading = false;", load, StringComparison.Ordinal);
        int afterLoad = scene.IndexOf("public new void AfterLoad()", StringComparison.Ordinal), battleAt = scene.IndexOf("GeneratedBattleTerrain.Install(this, worldId)", afterLoad, StringComparison.Ordinal),
            dressingAt = scene.IndexOf("MapDressingRuntime.Install(", afterLoad, StringComparison.Ordinal), installAt = scene.IndexOf("MagicGroundRuntime.Install(", afterLoad, StringComparison.Ordinal);
        Check(load >= 0 && read > load && begun > read && loadedAt > begun && afterLoad > loadedAt && battleAt > afterLoad && dressingAt > battleAt && installAt > dressingAt,
              "load order: the overlay's faces and battle patch are made in Load after the scene's read and before the map is loaded (so before MobileParty.PreAfterLoad and ComputePathAfterLoad); AfterLoad draws the decals after the battle map and the dressing");
        // MMR-262: the teardown (GeneratedMapScene.Destroy, after MapScreen.OnFinalize's Scene.ClearAll) lets go of the decals and nodes with no
        // engine call; Begin removes natively only what an earlier Begin drew on the same live scene.
        string runtime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MagicGroundRuntime.cs")).Replace("\r\n", "\n");
        string handles = Body(runtime, "internal static void ReleaseHandles("), beginBody = Body(runtime, "internal static void Begin(");
        Check(handles.Length > 0 && handles.Contains(".Drop()") && handles.Contains("SetFaceTypes(null)") && handles.Contains("ClearGradeCell") && handles.Contains("nodes.Clear()")
              && new[] { ".Remove()", "RemoveNode(", "RemoveDecal", "RemoveEntity", "Release(null)", "Release(log)" }.All(n => !handles.Contains(n))
              && beginBody.Contains("if (scene != null && scene == loaded) Release(null); else ReleaseHandles(null);")
              && Body(scene, "public new void Destroy()").Contains("MagicGroundRuntime.ReleaseHandles(trace)"),
              "the magic ground's teardown lets go of its decal and node handles with no engine call (the grade cells, battle patch and face types still go); Begin removes natively only on the same live scene");
        // MMR-288: the layers run after the decals: Install after Draw, a live change's Changed after its Draw, a reveal step's Changed after its
        // Sync (the clock decides the step); Draw plans with the water layers' declaration.
        string install = Body(runtime, "internal static void Install("), advance = Body(runtime, "internal static void Advance("), changed = Body(runtime, "private static MagicChange Changed("), draw = Body(runtime, "private static void Draw(");
        Func<string, string, string, bool> precedes = (text, a, b) => text.IndexOf(a, StringComparison.Ordinal) >= 0 && text.IndexOf(a, StringComparison.Ordinal) < text.IndexOf(b, StringComparison.Ordinal);
        Check(precedes(install, "Draw(overlay.Current", "layer.Install(") && precedes(changed, "Draw(change.After", "layer.Changed(") && precedes(advance, "clock.Tick(", "installer.Sync(") && precedes(advance, "installer.Sync(", "layer.Changed(")
              && draw.Contains("IMagicWaterVeins") && draw.Contains("clock.Start(") && install.Contains("magic_ground_warning:no campaign clock"),
              "the vein layers are called after the decals (install, live change, reveal step); the reveal runs on MagicRevealClock; a missing campaign clock at install is logged");
        string cache = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/WorldNavigationCache.cs")).Replace("\r\n", "\n");
        begin = cache.IndexOf("public static NavigationCacheKey BuildKey(", StringComparison.Ordinal); end = begin < 0 ? -1 : cache.IndexOf("\n        }\n", begin, StringComparison.Ordinal);
        string keyBody = begin < 0 || end < 0 ? "" : cache.Substring(begin, end - begin);
        var named = new[] { "Magic", "Overlay", "FaceTypes", "Mutation", "Snapshot" }.Where(w => keyBody.IndexOf(w, StringComparison.Ordinal) >= 0).ToList();
        Check(keyBody.Length > 0 && named.Count == 0 && NavigationCacheKey.Sources.Keys.All(k => k.IndexOf("magic", StringComparison.OrdinalIgnoreCase) < 0 && k.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) < 0)
              && Throws(() => new NavigationCacheKey().Set("magicOverlay", "x")),
              "the navigation-cache key (a3): BuildKey reads nothing of the overlay" + (named.Count > 0 ? " (found " + String.Join(", ", named) + ")" : "") + ", no classified input names it and an overlay input is refused");
    }

    // MMR-262: the farthest footprint sample (4 corners, 4 edge midpoints) from the centre's ground, recomputed from the surface as DecalFrame
    // turns the half extents (as MapDressingTests does).
    private static double FootprintReach(WorldSurface surface, RoadDecal d)
    {
        Func<double, double, double> ground = (x, y) => MapDressing.Ground(surface, Math.Max(0, Math.Min(surface.Extent, x)), Math.Max(0, Math.Min(surface.Extent, y)));
        double c = Math.Cos(d.Yaw), s = Math.Sin(d.Yaw), centre = ground(d.X, d.Y), reach = 0;
        foreach (var k in new[] { new[] { -1, -1 }, new[] { -1, 0 }, new[] { -1, 1 }, new[] { 0, -1 }, new[] { 0, 1 }, new[] { 1, -1 }, new[] { 1, 0 }, new[] { 1, 1 } })
        {
            double ox = k[0] * d.HalfLength * c - k[1] * d.HalfWidth * s, oy = k[0] * d.HalfLength * s + k[1] * d.HalfWidth * c;
            reach = Math.Max(reach, Math.Abs(ground(d.X + ox, d.Y + oy) - centre));
        }
        return reach;
    }
    // A method's body in a source file ("" when not found).
    private static string Body(string source, string signature)
    {
        int begin = source.IndexOf(signature, StringComparison.Ordinal), end = begin < 0 ? -1 : source.IndexOf("\n        }\n", begin, StringComparison.Ordinal);
        return begin < 0 || end < 0 ? "" : source.Substring(begin, end - begin);
    }

    // MMR-288: the distance from a point to the edge a -> b (canonical x, the period wrapping).
    private static double SegmentDistance(MagicVeinGraph g, int a, int b, double x, double y)
    {
        var pa = g.Nodes[a]; var pb = g.Nodes[b];
        double bx = g.DeltaX(pa.X, pb.X), by = pb.Y - pa.Y, px = g.DeltaX(pa.X, x), py = y - pa.Y, len2 = bx * bx + by * by;
        double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, (px * bx + py * by) / len2));
        double dx = px - t * bx, dy = py - t * by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private static VeinEdge EdgeOf(MagicVeinGraph g, int child) { return g.Edges.First(e => e.Child == child); }
    // Each node's sample depth inside its zone: 8-connected steps from the nearest sample outside the zone (1 on the rim).
    private static Dictionary<int, int> ZoneDepths(MagicVeinGraph g, MagicGroundSource src)
    {
        var r = new Dictionary<int, int>();
        foreach (var z in g.Zones)
        {
            var member = new HashSet<int>(z.Samples); var depth = new Dictionary<int, int>(); var queue = new Queue<int>();
            foreach (int i in z.Samples)
            {
                int ir = i / src.Side, ic = i % src.Side; bool rim = false;
                for (int dr = -1; dr <= 1 && !rim; dr++) for (int dc = -1; dc <= 1; dc++) if ((dr != 0 || dc != 0) && (src.PoleRow(ir + dr) || !member.Contains(src.Index(ir + dr, ic + dc)))) { rim = true; break; }
                if (rim) { depth[i] = 1; queue.Enqueue(i); }
            }
            while (queue.Count > 0)
            {
                int j = queue.Dequeue(), jr = j / src.Side, jc = j % src.Side;
                for (int dr = -1; dr <= 1; dr++) for (int dc = -1; dc <= 1; dc++)
                {
                    if ((dr == 0 && dc == 0) || src.PoleRow(jr + dr)) continue;
                    int k = src.Index(jr + dr, jc + dc);
                    if (member.Contains(k) && !depth.ContainsKey(k)) { depth[k] = depth[j] + 1; queue.Enqueue(k); }
                }
            }
            for (int k = z.FirstNode; k < z.FirstNode + z.NodeCount; k++) { int d; r[k] = depth.TryGetValue(g.Nodes[k].Sample, out d) ? d : 1; }
        }
        return r;
    }
    // A stamp's centre, bounding-circle radius and whether it is a burst.
    private static double[] Box(double x, double y, double length, double width, bool burst) { return new[] { x, y, Math.Sqrt(length * length + width * width) / 2, burst ? 1 : 0 }; }
    // The stamps joined where their bounding circles touch (columns wrapping): {groups, stamps in groups with no burst, stamps touching no other,
    // groups smaller than roads.json magic veins.minStamps}.
    private static int[] Groups(MagicGroundSource src, List<double[]> boxes)
    {
        int n = boxes.Count; var parent = Enumerable.Range(0, n).ToArray(); var touches = new bool[n];
        Func<int, int> find = null; find = k => parent[k] == k ? k : (parent[k] = find(parent[k]));
        double cell = 16; var grid = new Dictionary<long, List<int>>();
        Func<double, double, long> key = (x, y) => ((long)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(y / cell);
        for (int k = 0; k < n; k++) { List<int> l; long kk = key(src.Canonical(boxes[k][0] - src.Strip), boxes[k][1]); if (!grid.TryGetValue(kk, out l)) grid[kk] = l = new List<int>(); l.Add(k); }
        double maxR = boxes.Count == 0 ? 0 : boxes.Max(b => b[2]);
        int span = (int)Math.Ceiling(2 * maxR / cell);
        for (int a = 0; a < n; a++)
        {
            double ax = src.Canonical(boxes[a][0] - src.Strip), ay = boxes[a][1];
            for (int dx = -span; dx <= span; dx++) for (int dy = -span; dy <= span; dy++)
                foreach (var shift in new[] { 0.0, -src.Width, src.Width })
                {
                    List<int> l;
                    if (!grid.TryGetValue(key(ax + shift + dx * cell, ay + dy * cell), out l)) continue;
                    foreach (int b in l)
                    {
                        if (b <= a) continue;
                        double ddx = src.Canonical(boxes[b][0] - src.Strip) - ax; ddx -= Math.Round(ddx / src.Width) * src.Width;
                        double ddy = boxes[b][1] - ay;
                        if (Math.Sqrt(ddx * ddx + ddy * ddy) <= boxes[a][2] + boxes[b][2]) { touches[a] = touches[b] = true; parent[find(a)] = find(b); }
                    }
                }
        }
        var groups = Enumerable.Range(0, n).GroupBy(k => find(k)).ToList();
        int minStamps = RoadsPolicyMinStamps;
        return new[] { groups.Count, groups.Where(gr => gr.All(k => boxes[k][3] == 0)).Sum(gr => gr.Count()), Enumerable.Range(0, n).Count(k => !touches[k] && boxes[k][3] == 0), groups.Count(gr => gr.Count() < minStamps) };
    }
    private static int RoadsPolicyMinStamps;
    // The share of the given samples with a vein node of their zone's graph within r units.
    private static double Coverage(MagicVeinGraph g, MagicGroundSource src, List<int> samples, double r)
    {
        if (samples.Count == 0) return 1;
        var cell = new Dictionary<long, List<VeinNode>>();
        Func<double, double, long> key = (x, y) => ((long)Math.Floor(x / r) << 32) ^ (uint)(int)Math.Floor(y / r);
        foreach (var n in g.Nodes) { List<VeinNode> l; long k = key(n.X, n.Y); if (!cell.TryGetValue(k, out l)) cell[k] = l = new List<VeinNode>(); l.Add(n); }
        int near = 0;
        foreach (int i in samples)
        {
            var p = src.Logical(i / src.Side, i % src.Side); double x = src.Canonical(p[0]), y = p[1] + src.PoleMargin; bool hit = false;
            for (int dx = -1; dx <= 1 && !hit; dx++) for (int dy = -1; dy <= 1 && !hit; dy++)
                foreach (var shift in new[] { 0.0, -src.Width, src.Width })
                {
                    List<VeinNode> l;
                    if (!cell.TryGetValue(key(x + shift + dx * r, y + dy * r), out l)) continue;
                    if (l.Any(n => Math.Abs(g.DeltaX(n.X, x)) <= r && Math.Sqrt(Math.Pow(g.DeltaX(n.X, x), 2) + Math.Pow(n.Y - y, 2)) <= r)) { hit = true; break; }
                }
            if (hit) near++;
        }
        return (double)near / samples.Count;
    }

    // MMR-288: the stamps, graph and a plain base map of a plan for tools/render_magic_veins.py (DIR/veins.json).
    private static void Dump(string dir, MagicGroundPlan plan, MagicGroundSource src, GeneratedClimateSystem.Source source, BiomeTable table, TravelCells cells, RoadsPolicy.MagicPolicy magic, Dictionary<string, object> cast)
    {
        Directory.CreateDirectory(dir);
        var g = plan.Graph;
        var drawn = plan.Stamps.ToDictionary(s => s.Id, s => s);
        var cores = table.Rows.Values.Select(r => r.Core).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        var core = new int[src.Side * src.Side]; var water = new int[src.Side * src.Side];
        for (int r = 0; r < src.Side; r++) for (int c = 0; c < src.Side; c++)
        {
            int i = r * src.Side + c; BiomeTable.Row row;
            core[i] = table.Rows.TryGetValue((int)source.Biome[i], out row) ? cores.IndexOf(row.Core) : -1;
            var p = src.Logical(r, Math.Min(c, src.Columns - 1));
            water[i] = (row != null && row.Water) || src.PoleRow(r) || MagicVeins.OnWaterCell(src, src.Canonical(p[0]), p[1] + src.PoleMargin) ? 1 : 0;
        }
        Func<double, double> f = v => Double.IsInfinity(v) || Double.IsNaN(v) ? (Double.IsPositiveInfinity(v) ? 1e300 : -1e300) : Math.Round(v, 5);
        var doc = new Dictionary<string, object>
        {
            ["schema"] = "mnbl.magic-veins-dump", ["version"] = 1,
            ["frame"] = new Dictionary<string, object> { ["width"] = src.Width, ["height"] = src.Height, ["strip"] = src.Strip, ["poleMargin"] = src.PoleMargin, ["side"] = src.Side, ["columns"] = src.Columns, ["meridian"] = src.Meridian },
            ["base"] = new Dictionary<string, object> { ["cores"] = cores, ["core"] = core, ["water"] = water },
            ["policy"] = new Dictionary<string, object> { ["flipU"] = magic.FlipU, ["flipV"] = magic.FlipV, ["stampLength"] = magic.StampLength, ["spacing"] = magic.Spacing },
            ["cast"] = cast,
            ["stamps"] = g.Stamps.Select(s =>
            {
                MagicStamp d; bool land = drawn.TryGetValue(s.Id, out d);
                return (object)new Dictionary<string, object> { ["id"] = s.Id, ["kind"] = s.Kind, ["look"] = g.Nodes[s.Node].Look, ["school"] = g.Nodes[s.Node].School, ["row"] = s.AtlasRow, ["col"] = s.AtlasColumn,
                    ["x"] = f(s.X), ["y"] = f(s.Y), ["yaw"] = f(land ? d.Decal.Yaw : s.Yaw), ["length"] = f(s.Length), ["width"] = f(s.Width), ["onWater"] = s.OnWater, ["drawn"] = land, ["live"] = s.Live,
                    ["order"] = g.Nodes[s.Node].Order, ["revealAt"] = f(s.RevealAt), ["alpha"] = f(s.Alpha) };
            }).Concat(plan.Stamps.Where(s => s.Id.EndsWith(".shore", StringComparison.Ordinal)).Select(s => (object)new Dictionary<string, object> { ["id"] = s.Id, ["kind"] = s.Kind, ["look"] = s.Look, ["school"] = s.School,
                    ["row"] = s.AtlasRow, ["col"] = s.AtlasColumn, ["x"] = f(s.Decal.X), ["y"] = f(s.Decal.Y), ["yaw"] = f(s.Decal.Yaw), ["length"] = f(s.Decal.HalfLength * 2), ["width"] = f(s.Decal.HalfWidth * 2), ["onWater"] = false,
                    ["drawn"] = true, ["live"] = s.Live, ["order"] = s.Order, ["revealAt"] = f(s.RevealAt), ["alpha"] = f(s.Alpha) })).ToList(),
            ["nodes"] = g.Nodes.Select(n => (object)new object[] { f(n.X), f(n.Y), n.Parent, f(n.Width), n.Order, n.OnWater ? 1 : 0, n.Live ? 1 : 0, f(n.RevealAt) }).ToList(),
            ["sources"] = g.Sources.Select(s => (object)s.Node).ToList()
        };
        File.WriteAllText(Path.Combine(dir, "veins.json"), Serializer.Serialize(doc), new UTF8Encoding(false));
    }

    private const double GeneratedBattleTerrainCellSize = 4;   // GeneratedBattleTerrain.CellSize
    private static int CountColumn(MagicGroundSource s) { int n = 0; for (int r = 0; r < s.Side; r++) if (s.Variant[r * s.Side + s.Columns] >= 0) n++; return n; }
    // MMR-247: a plan's drawn stamps whose centre the scene shows as water: the lower of the encoded and the collision height under the datum, or
    // inside a lake plane's box under its plane.
    private static List<MagicStamp> Wet(WorldSurface surface, IList<GeneratedWaterSystem.Lake> lakes, MagicGroundPlan plan)
    {
        return plan.Stamps.Where(st =>
        {
            double x = st.Decal.X, y = st.Decal.Y, ground = Math.Min(surface.Height(x, y), surface.CollisionHeight(x, y));
            return ground < GeneratedWaterSystem.DatumZ || lakes.Any(l => x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && l.Z > ground);
        }).ToList();
    }
    private static int CellsOf(SortedDictionary<string, object> shares, string type) { object v; return shares.TryGetValue(type, out v) ? (int)((Dictionary<string, object>)v)["cells"] : 0; }
    private static string Shares(SortedDictionary<string, object> shares)
    {
        return String.Join(", ", shares.Where(kv => kv.Key != "landCells").Select(kv => kv.Key + " " + ((double)((Dictionary<string, object>)kv.Value)["share"]).ToString("0.000", CultureInfo.InvariantCulture)));
    }
    // The most decals in any window of w x h units (stepped by a quarter window, columns wrapping).
    private static int WindowMax(List<double[]> points, WorldSurface surface, double w, double h)
    {
        int best = 0;
        for (double x0 = surface.Strip; x0 < surface.Strip + surface.LogicalWidth; x0 += w / 4)
            for (double y0 = surface.PoleMargin - h / 2; y0 < surface.PoleMargin + surface.LogicalHeight; y0 += h / 4)
            {
                int n = 0;
                foreach (var p in points)
                {
                    double dx = p[0] - x0; dx -= Math.Floor(dx / surface.LogicalWidth) * surface.LogicalWidth;
                    if (dx < w && p[1] >= y0 && p[1] < y0 + h) n++;
                }
                best = Math.Max(best, n);
            }
        return best;
    }
}
