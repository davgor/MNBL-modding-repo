using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-290 magic water tests, without the game:
//   MagicWaterTests REPO BUNDLE_A BUNDLE_B OUT_JSON [DUMP_DIR]
// On two real compiled worlds (A' seed 42, B' seed 73), over MMR-288's vein graph and MMR-285's water (the scene's lake planes, the sea datum):
//   a2  every water stamp of the graph is planned once, deterministically, with its school's atlas row and its kind's column, over water only:
//       sea stamps where the ground lies below the datum (box centred on the sea plane), lake stamps inside a shown lake plane above its bed
//       (centred on the plane), bed stamps on other water travel cells (rivers; cells no plane shows), coast stamps (centre on land) only where
//       their footprint reaches water; the veins continue across the shore (every shore crossing has both ends' stamps drawn, the water side by
//       this plan); the shared decal budget with roads, river and lake water and the ground veins (whole world and the closest-camera windows);
//       MMR-261's recorded Nerdun fire cast's water veins appear with its land veins (A'); a synthetic coastal cast adds live water stamps and a
//       redraw touches only its zones;
// plus the installer over a recording target (the published material, else the look's ground material, else nothing, never a throw; the plane's
// z for sea and lake stamps; seam refresh; the toggle; removal and MMR-262's handle drop), the fallback modes F1 (bed) and F2 (shore glow), and
// the runtime wiring (the layer hook, no engine call on release after Scene.ClearAll). DUMP_DIR: per world the land and water stamps over a plain
// base map (DUMP_DIR/<A|B|A-nerdun>/water.json) for tools/render_magic_water.py.
internal static class MagicWaterTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }

    // Once Cleared is set (the engine ran Scene.ClearAll), every decal call fails and is counted: MMR-262's teardown must make none.
    private sealed class Recorder : IMagicWaterTarget
    {
        public sealed class Item { public RoadDecal Decal; public double X, Y, Z; public bool Visible = true; }
        public readonly Dictionary<int, Item> Live = new Dictionary<int, Item>();
        public int Next, AfterClear; public bool Cleared; public Func<string, bool> Resolves = m => true;
        private void Engine() { if (Cleared) { AfterClear++; throw new InvalidOperationException("an engine call on a cleared scene"); } }
        public object AddDecal(RoadDecal decal, double x, double y, double z) { Engine(); if (!Resolves(decal.Material)) return null; Live[++Next] = new Item { Decal = decal, X = x, Y = y, Z = z }; return Next; }
        public void MoveDecal(object handle, RoadDecal decal, double x, double y, double z) { Engine(); var i = Live[(int)handle]; i.X = x; i.Y = y; i.Z = z; }
        public void RemoveDecal(object handle) { Engine(); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public void SetVisible(object handle, bool visible) { Engine(); Live[(int)handle].Visible = visible; }
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.magic-water-offline", ["version"] = 1, ["ticket"] = "MMR-290" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // The runtime's reveal schedule (MagicGroundRuntime.Draw at t0, then Advance ticking only at the returned next reveal, each tick calling the
    // layers with that time): the first time each live water stamp is shown (absent: never in the session) and the number of ticks.
    private static Dictionary<MagicWaterStamp, double> Schedule(MagicWaterPlan water, double t0, Func<double, double> next, out int ticks)
    {
        var shown = new Dictionary<MagicWaterStamp, double>();
        ticks = 0;
        for (double t = t0; !Double.IsPositiveInfinity(t); t = next(t), ticks++)
            foreach (var s in water.Revealed(t).Stamps) if (s.Live && !shown.ContainsKey(s)) shown[s] = t;
        return shown;
    }

    // Live water stamps the schedule shows at their own reveal time (or at t0, when due by then), late, and never.
    private static int[] Timing(MagicWaterPlan water, double t0, Func<double, double> next)
    {
        int ticks; var shown = Schedule(water, t0, next, out ticks);
        int onTime = 0, late = 0, never = 0;
        foreach (var s in water.Stamps.Where(x => x.Live))
        {
            double at;
            if (!shown.TryGetValue(s, out at)) never++;
            else if (at == Math.Max(t0, s.RevealAt)) onTime++;
            else late++;
        }
        return new[] { onTime, late, never, ticks };
    }

    // The land stamps' next reveal alone (before MMR-290's fix: the schedule the runtime used), for the record.
    private static Func<double, double> LandOnly(MagicGroundPlan full)
    {
        return h => full.Stamps.Where(s => s.RevealAt > h).Select(s => s.RevealAt).DefaultIfEmpty(Double.PositiveInfinity).Min();
    }

    private static RoadsPolicy.MagicWaterPolicy WithMode(string repo, string mode)
    {
        var p = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath))).MagicWater;
        p.Mode = mode;
        return p;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0], dump = args.Length > 4 && args[4].Length > 0 ? args[4] : null;
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var magic = roads.Magic; var water = roads.MagicWater;
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var table = BiomeTable.FromBytes(File.ReadAllBytes(Path.Combine(repo, BiomeTable.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);

        Check(water.Material == "mnbl_magicwater" && water.Mode == "surface" && water.Columns == 4 && water.Rows == 8 && magic.Schools.All(s => water.SchoolRows.ContainsKey(s)) && water.SchoolRows["weave"] == 0
              && water.SchoolRows["air"] == 7 && water.FallbackPrefix == magic.MaterialPrefix && !magic.Schools.Any(s => magic.MaterialPrefix + s == water.Material) && water.HalfDepth > 0 && water.HalfDepth < MagicGround.DecalHalfDepth,
              "roads.json magicWater: one material " + water.Material + " for every school (not a ground material's name: mnbl_magic_water is the water school's ground decal), 4 columns by kind x 8 rows by look school, mode surface, plane half depth " + water.HalfDepth +
              ", fallback the look's ground material " + water.FallbackPrefix + "<look>");
        evidence["policy"] = new Dictionary<string, object> { ["material"] = water.Material, ["mode"] = water.Mode, ["atlas"] = new[] { water.Columns, water.Rows }, ["schoolRows"] = water.SchoolRows, ["widthScale"] = water.WidthScale,
            ["halfDepth"] = water.HalfDepth, ["opacity"] = water.Opacity, ["bedMaxDepth"] = water.BedMaxDepth, ["maxDecals"] = water.MaxDecals, ["fallback"] = water.FallbackPrefix + "<look>, row by core group else " + water.FallbackRow };

        var worlds = new List<object>(); evidence["worlds"] = worlds;
        foreach (var label in new[] { "A", "B" })
        {
            var world = new Dictionary<string, object> { ["label"] = label }; worlds.Add(world);
            var bundle = WorldDataBundle.Open(label == "A" ? args[1] : args[2], host);
            world["bundle"] = bundle.WorldContentId;
            var surface = WorldSurface.FromBundle(bundle);
            var cells = NavigationInput.FromBundle(bundle, surface, navigationPolicy).Cells;
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            worldJson = null;
            var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, cells, table));
            var src = overlay.Source;
            var dressing = MapDressing.Build(bundle, surface, null, source, roads, looks);
            var lakes = dressing.Lakes;
            // As the runtime plans them (integration of MMR-288/290): the layer declares IMagicWaterVeins in mode surface, so the ground plans
            // with the water veins drawn, and the water plan skips the water stamps of the components the ground drops (Plan.Dropped).
            bool drawsWater = water.Mode == RoadsPolicy.MagicWaterPolicy.Surface;
            var ground = MagicGround.Plan(overlay.Current, magic, drawsWater);
            var g = ground.Graph;
            var plan = MagicWater.Plan(g, lakes, water, magic, ground.Dropped);
            var againGround = MagicGround.Plan(overlay.Current, magic, drawsWater);
            var again = MagicWater.Plan(againGround.Graph, lakes, water, magic, againGround.Dropped);
            var graphWater = g.Stamps.Where(s => s.OnWater).ToList();
            var ids = new HashSet<string>(plan.Stamps.Select(s => s.Id));
            int droppedWater = graphWater.Count(s => ground.Dropped.Contains(s.Id)), droppedLand = ground.Dropped.Count - droppedWater, skippedDropped;
            plan.Skipped.TryGetValue("dropped", out skippedDropped);
            Check(plan.Stamps.Count > 0 && plan.Sha256() == again.Sha256() && plan.GraphWaterStamps == graphWater.Count && plan.Stamps.Count + plan.Skipped.Values.Sum() == graphWater.Count && ids.Count == plan.Stamps.Count
                  && graphWater.All(s => ids.Contains(s.Id) != ground.Dropped.Contains(s.Id)) && skippedDropped == droppedWater && plan.Thinned == 0 && ground.WaterDrawn == drawsWater,
                  label + ": every one of the graph's " + graphWater.Count + " water stamps is planned once, deterministically (" + plan.Sha256().Substring(0, 12) + "), except the " + droppedWater +
                  " of the components the ground overlay drops below minStamps (with " + droppedLand + " land stamps): " + String.Join(", ", plan.ByMode.Select(kv => kv.Key + " " + kv.Value)) +
                  "; " + String.Join(", ", plan.ByKind.Select(kv => kv.Key + " " + kv.Value)) + "; " + lakes.Count + " lake planes; the ground draws " + ground.Stamps.Count + " land stamps with the water layer");
            world["integration"] = new Dictionary<string, object> { ["drawsWaterStamps"] = drawsWater, ["groundStampsWithWaterLayer"] = ground.Stamps.Count, ["groundStampsWithoutWaterLayer"] = MagicGround.Plan(overlay.Current, magic).Stamps.Count,
                ["droppedWaterStamps"] = droppedWater, ["droppedLandStamps"] = droppedLand, ["groundComponents"] = ground.Components };

            // Box clipping (MMR-247's WaterPlanes): every stamp centred on the terrain keeps its box above the water planes its footprint shows
            // (its Floor over 7 x 7 samples, which the engine target clips with); sea and lake stamps, centred on their plane, stay whole.
            var waterPlanes = new WaterPlanes(lakes);
            int terrainCentred = 0, heldWater = 0, clipped = 0, submerged = 0, heldAfter = 0; bool floorsOk = true;
            foreach (var s in plan.Stamps)
            {
                foreach (var d in s.Fallback == null ? new[] { s.Decal } : new[] { s.Decal, s.Fallback })
                {
                    if (!Double.IsNaN(s.PlaneZ)) { floorsOk &= Double.IsNaN(d.Floor); continue; }
                    if (!ReferenceEquals(d, s.Decal)) { floorsOk &= !Double.IsNaN(d.Floor); continue; }
                    terrainCentred++;
                    floorsOk &= !Double.IsNaN(d.Floor) && d.Floor == waterPlanes.Floor(d.X, d.Y, d.Yaw, d.HalfLength, d.HalfWidth, src.Ground, 7);
                    double centre = src.Ground(d.X, d.Y) + d.BoxOffset, z, half;
                    if (d.Floor + WaterPlanes.Clearance > centre - d.HalfDepth) heldWater++;   // the bottom under the plane (or within its clearance)
                    bool whole = WaterPlanes.Clip(centre, d.HalfDepth, d.Floor, out z, out half);
                    if (!whole) submerged++; else if (half < d.HalfDepth - 1e-9) clipped++;
                    if (whole && d.Floor >= z - half) heldAfter++;
                }
            }
            world["boxClip"] = new Dictionary<string, object> { ["terrainCentredStamps"] = terrainCentred, ["holdingWaterUnclipped"] = heldWater, ["clipped"] = clipped, ["submerged"] = submerged, ["holdingWaterClipped"] = heldAfter,
                ["planeStampsWhole"] = plan.Stamps.Count(s => !Double.IsNaN(s.PlaneZ)) };
            Check(floorsOk && heldAfter == 0 && clipped + submerged == heldWater,
                  label + ": the " + terrainCentred + " terrain-centred magic water boxes (bed, coast, inland) stay above the water planes their footprint shows: " + heldWater + " would hold the water surface unclipped, " + clipped +
                  " are raised over it and " + submerged + " lie wholly under it (a sliver that paints nothing); 0 hold it after the clip; the " + plan.Stamps.Count(s => !Double.IsNaN(s.PlaneZ)) + " sea and lake boxes stay whole");

            // Looks: the owner node's look school; the magic water atlas row by school and column by kind; the fallback cell as the ground stamps'.
            bool looksOk = plan.Stamps.All(s =>
            {
                var node = g.Nodes[s.Node]; var st = g.Stamps.First(x => x.Id == s.Id);
                int row = water.SchoolRows[node.Look], groundRow = node.CoreRow >= 0 ? node.CoreRow : water.FallbackRow;
                bool groundCell = s.Decal.Material == magic.MaterialPrefix + node.Look && s.Decal.Atlas.SequenceEqual(new[] { .25f, .25f, st.AtlasColumn / 4f, groundRow / 4f }) && s.Decal.Tint == 0 && Math.Abs(s.Decal.HalfWidth - st.Width / 2) < 1e-9;
                if (s.Mode == MagicWaterMode.Inland) return s.Look == node.Look && groundCell && s.Fallback == null && s.Decal.Yaw == st.Yaw && s.Decal.X == st.X && s.Decal.Y == st.Y && s.RevealAt == st.RevealAt;
                return s.Look == node.Look && s.Decal.Material == water.Material && s.Decal.Atlas.SequenceEqual(new[] { .25f, .125f, st.AtlasColumn / 4f, row / 8f }) && s.Decal.Tint == MagicWater.Tint(water.Opacity)
                    && s.Fallback.Material == magic.MaterialPrefix + node.Look && s.Fallback.Atlas.SequenceEqual(new[] { .25f, .25f, st.AtlasColumn / 4f, groundRow / 4f }) && s.Fallback.Tint == 0
                    && Math.Abs(s.Decal.HalfWidth - water.WidthScale * st.Width / 2) < 1e-9 && Math.Abs(s.Fallback.HalfWidth - st.Width / 2) < 1e-9 && Math.Abs(s.Decal.HalfLength - st.Length / 2) < 1e-9 && s.Decal.Yaw == st.Yaw
                    && s.Decal.X == st.X && s.Decal.Y == st.Y && s.RevealAt == st.RevealAt;
            });
            Check(looksOk && plan.BySchool.Keys.All(magic.Schools.Contains),
                  label + ": each water stamp keeps its graph stamp's box, yaw and reveal time, takes the magic water cell (0.25, 0.125, column / 4, school row / 8) tinted white at opacity " + water.Opacity +
                  " and the look's ground cell as its fallback; an inland stamp takes the ground cell (" + String.Join(", ", plan.BySchool.Select(kv => kv.Key + " " + kv.Value)) + ")");
            // MMR-247 (D): neither module material has a rect in the decal atlas: the stamp and its ground fallback both carry the vein stamps'
            // vanilla fallback by kind (the magic water decal's with the policy's opacity).
            bool vanillaOk = plan.Stamps.All(s =>
            {
                var f = s.Kind == VeinKind.Burst ? magic.BurstFallback : magic.StrokeFallback;
                Func<RoadDecal, double, bool> carries = (d, alpha) => d.Fallback == f.Material && d.FallbackAtlas.SequenceEqual(f.Atlas) && d.FallbackTurn == f.Turn && d.FallbackAcross == f.Across
                    && d.FallbackTint == MagicGround.FallbackTint(magic.FallbackColours[s.Look], f.Core, alpha);
                return s.Mode == MagicWaterMode.Inland ? carries(s.Decal, 1) && s.Fallback == null : carries(s.Decal, water.Opacity) && carries(s.Fallback, 1);
            });
            Check(vanillaOk, label + ": every magic water stamp and its ground fallback carry the vanilla fallback by kind (" + magic.StrokeFallback.Material + " / " + magic.BurstFallback.Material + ") tinted toward the look's colour, so the engine target draws something the decal atlas holds");

            // Over water only.
            Func<double, double, string> coverAt = (x, y) => { double z, gr; return MagicWater.Cover(src, lakes, x, y, out z, out gr); };
            bool seaOk = true, lakeOk = true, bedOk = true, coastOk = true, inlandOk = true; double coastFar = 0; var depths = new List<double>();
            foreach (var s in plan.Stamps)
            {
                double gr = src.Ground(s.Decal.X, s.Decal.Y); int cell = src.CellAt(s.Decal.X, s.Decal.Y);
                if (s.Mode == MagicWaterMode.Sea) { seaOk &= gr < 0 && s.PlaneZ == 0 && s.Decal.HalfDepth == water.HalfDepth && Math.Abs(s.Depth + gr) < 1e-9; depths.Add(s.Depth); }
                else if (s.Mode == MagicWaterMode.Lake)
                {
                    lakeOk &= gr >= 0 && lakes.Any(l => s.Decal.X >= l.MinX && s.Decal.X <= l.MaxX && s.Decal.Y >= l.MinY && s.Decal.Y <= l.MaxY && l.Z > gr && l.Z == s.PlaneZ) && s.Decal.HalfDepth == water.HalfDepth;
                    depths.Add(s.Depth);
                }
                else if (s.Mode == MagicWaterMode.Bed) bedOk &= Double.IsNaN(s.PlaneZ) && cell >= 0 && TravelCells.Water(cells.Classes[cell]) && s.Decal.HalfDepth >= MagicGround.DecalHalfDepth;
                else if (s.Mode == MagicWaterMode.Coast)
                {
                    double reach = MagicWater.Reach(src, lakes, s.Decal.X, s.Decal.Y, s.Decal.Yaw, s.Fallback.HalfLength, s.Fallback.HalfWidth);
                    coastOk &= Double.IsNaN(s.PlaneZ) && coverAt(s.Decal.X, s.Decal.Y) == "land" && !Double.IsNaN(reach) && s.Decal.HalfDepth >= MagicGround.DecalHalfDepth;
                    if (!Double.IsNaN(reach)) coastFar = Math.Max(coastFar, reach);
                }
                else if (s.Mode == MagicWaterMode.Inland)
                    inlandOk &= coverAt(s.Decal.X, s.Decal.Y) == "land" && Double.IsNaN(MagicWater.Reach(src, lakes, s.Decal.X, s.Decal.Y, s.Decal.Yaw, s.Decal.HalfLength, s.Decal.HalfWidth)) && s.Decal.HalfDepth >= MagicGround.DecalHalfDepth;
                else coastOk = false;
            }
            // The travel cells under the stamps (rivers have no plane: their stamps are bed stamps on River cells).
            world["byTravelCell"] = plan.Stamps.GroupBy(s => { int k = src.CellAt(s.Decal.X, s.Decal.Y); return s.Mode + ":" + (k < 0 ? "pole" : TravelCells.ClassNames[cells.Classes[k]]); })
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Count());
            depths.Sort();
            Func<double, double> q = f => depths.Count == 0 ? 0 : Math.Round(depths[Math.Min(depths.Count - 1, (int)(f * depths.Count))], 3);
            world["depthUnderPlaneStamps"] = new Dictionary<string, object> { ["count"] = depths.Count, ["p10"] = q(0.1), ["median"] = q(0.5), ["p90"] = q(0.9), ["shallowerThanBedMaxDepth"] = depths.Count(d => d <= water.BedMaxDepth) };
            Check(seaOk && lakeOk && bedOk && coastOk && inlandOk,
                  label + ": magic water stamps only over water: sea stamps where the ground lies below the datum (box centred on z 0, half depth " + water.HalfDepth + "), lake stamps inside a shown lake plane above its bed (centred on its z), bed stamps on water travel cells no plane shows (slope depth), coast stamps' footprints reach water (their centres at most " +
                  coastFar.ToString("0.##", CultureInfo.InvariantCulture) + " units from it); the " + plan.Stamps.Count(s => s.Mode == MagicWaterMode.Inland) + " inland stamps (water-core samples on land, no water under their footprint) draw the ground material; median depth under the plane stamps " + q(0.5));

            // Continuity across the shore: at every crossing both ends' stamps are drawn (ground or water), and the crossing has a water stamp.
            var landIds = new HashSet<string>(ground.Stamps.Select(s => s.Id));
            Func<string, bool> drawn = id => landIds.Contains(id) || ids.Contains(id);
            var wetIds = new HashSet<string>(plan.Stamps.Where(s => !MagicWaterMode.Ground(s.Mode)).Select(s => s.Id));
            var byNode = g.Stamps.GroupBy(s => s.Node).ToDictionary(x => x.Key, x => x.ToList());
            int shore = 0, joined = 0, waterSide = 0, dryEnds = 0, inDropped = 0;
            foreach (var e in g.ShoreEdges())
            {
                var ends = new[] { e.Parent, e.Child }.SelectMany(n => byNode.ContainsKey(n) ? byNode[n] : new List<VeinStamp>()).ToList();
                // A crossing inside a component the ground overlay drops (below minStamps) draws on neither side.
                if (ends.Any(s => ground.Dropped.Contains(s.Id))) { inDropped++; continue; }
                shore++;
                var landEnd = g.Nodes[e.Parent].OnWater ? e.Child : e.Parent;
                bool both = ends.All(s => drawn(s.Id)) && ends.Any(s => s.Node == landEnd);
                bool wet = ends.Any(s => wetIds.Contains(s.Id));
                // A water end that is a water-core sample on land (its stamps inland) is no water to cross into.
                bool dryEnd = !wet && ends.Where(s => s.Node != landEnd).All(s => ids.Contains(s.Id) && !wetIds.Contains(s.Id));
                if (both) joined++; if (wet) waterSide++; if (dryEnd) dryEnds++;
            }
            int union = g.Stamps.Count(s => drawn(s.Id)), notDrawn = g.Stamps.Count - union;
            world["shore"] = new Dictionary<string, object> { ["crossings"] = shore, ["inDroppedComponents"] = inDropped, ["bothEndsDrawn"] = joined, ["withWaterStamp"] = waterSide, ["waterEndInland"] = dryEnds, ["graphStamps"] = g.Stamps.Count, ["drawnGroundOrWater"] = union, ["notDrawn"] = notDrawn,
                ["groundSkipped"] = ground.Skipped };
            int noRow; ground.Skipped.TryGetValue("noAtlasRow", out noRow);
            Check(shore > 0 && joined == shore && waterSide + dryEnds == shore && notDrawn == noRow + ground.Thinned + droppedLand + plan.Skipped.Values.Sum() + plan.Thinned,
                  label + ": the veins continue across the shore: at all " + shore + " crossings both ends' stamps are drawn, and " + waterSide + " carry magic water (" + dryEnds + " end on a water-core sample on land, drawn inland; " + inDropped +
                  " more lie in components too small to draw); ground and water stamps together draw " + union + " of the graph's " + g.Stamps.Count + " stamps");

            // The shared decal budget: ground veins + roads + river and lake water + magic water, whole world and in the closest-camera windows.
            var all = ground.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y }).Concat(dressing.Decals.Select(d => new[] { d.X, d.Y })).Concat(dressing.Water.Select(d => new[] { d.X, d.Y }))
                .Concat(plan.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y })).ToList();
            var waterOnly = plan.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y }).ToList();
            int near = WindowMax(all, surface, 58, 37), far = WindowMax(all, surface, 116, 74), waterNear = WindowMax(waterOnly, surface, 58, 37);
            world["decalBudget"] = new Dictionary<string, object> { ["magicGround"] = ground.Stamps.Count, ["roads"] = dressing.Decals.Count, ["water"] = dressing.Water.Count, ["magicWater"] = plan.Stamps.Count, ["wholeWorld"] = all.Count,
                ["closestViewWindow"] = new[] { 58, 37 }, ["closestViewMax"] = near, ["doubleViewWindow"] = new[] { 116, 74 }, ["doubleViewMax"] = far, ["magicWaterClosestViewMax"] = waterNear, ["budgetInView"] = 5000, ["magicWaterMaxDecals"] = water.MaxDecals };
            Check(plan.Stamps.Count <= water.MaxDecals && near < 5000 && far < 5000,
                  label + ": decal budget shared with the ground veins, roads and water: " + ground.Stamps.Count + " + " + dressing.Decals.Count + " + " + dressing.Water.Count + " + magic water " + plan.Stamps.Count + " = " + all.Count +
                  " whole world (magic water cap " + water.MaxDecals + "); at most " + near + " in a 58 x 37 window (" + waterNear + " of them magic water) and " + far + " in a 116 x 74 one (in-view budget about 5,000)");
            world["generated"] = plan.Record();

            // ---- installer over a recording target ----
            var groundNames = new HashSet<string>(magic.Schools.Select(s => magic.MaterialPrefix + s));
            var none = new Recorder { Resolves = m => false };
            var i0 = new MagicWaterInstaller(none); i0.Sync(plan, x => x);
            var interim = new Recorder { Resolves = groundNames.Contains };
            var i1 = new MagicWaterInstaller(interim); i1.Sync(plan, x => x);
            var published = new Recorder();
            var i2 = new MagicWaterInstaller(published); i2.Sync(plan, x => x);
            bool planeZ = true;
            int seaLake = 0;
            foreach (var kv in i2.DrawnStamps())
            {
                var item = published.Live.Values.First(it => ReferenceEquals(it.Decal, kv.Value));
                if (kv.Key.Mode == MagicWaterMode.Sea || kv.Key.Mode == MagicWaterMode.Lake) { seaLake++; planeZ &= item.Z == kv.Key.PlaneZ && !Double.IsNaN(item.Z); }
                else planeZ &= Double.IsNaN(item.Z);
            }
            Check(i0.Live == 0 && i0.NotDrawn == plan.Stamps.Count && i0.Errors.Count == 0 && i0.MissingMaterials.Contains(water.Material) && i0.MissingMaterials.Count > 1
                  && i1.Live == plan.Stamps.Count && i1.FallbackDrawn == plan.Stamps.Count(s => !MagicWaterMode.Ground(s.Mode)) && i1.MissingMaterials.SequenceEqual(new[] { water.Material }) && interim.Live.Values.All(it => groundNames.Contains(it.Decal.Material)) && i1.Errors.Count == 0
                  && i2.Live == plan.Stamps.Count && i2.FallbackDrawn == 0 && published.Live.Values.All(it => it.Decal.Material == water.Material || groundNames.Contains(it.Decal.Material)) && published.Live.Values.Count(it => it.Decal.Material == water.Material) == plan.Stamps.Count(s => !MagicWaterMode.Ground(s.Mode)) && planeZ && seaLake == plan.Stamps.Count(s => !Double.IsNaN(s.PlaneZ)),
                  label + ": the installer draws every stamp with " + water.Material + " when it resolves (sea and lake boxes centred on their plane's z, the rest on the terrain), with the look's ground material while it is unpublished (" + i1.FallbackDrawn +
                  " fallback decals), and nothing, with no error, when neither resolves (" + i0.NotDrawn + " not drawn)");
            // Seam images, the toggle, removal; MMR-262's drop after Scene.ClearAll.
            i2.Refresh(x => x + surface.LogicalWidth);
            bool moved = published.Live.Values.All(it => it.X >= surface.Strip + surface.LogicalWidth) && i2.Moved == plan.Stamps.Count;
            i2.SetVisible(false); bool hidden = published.Live.Values.All(it => !it.Visible);
            i2.SetVisible(true); bool shown = published.Live.Values.All(it => it.Visible);
            i2.Remove();
            var cleared = new Recorder(); var dropping = new MagicWaterInstaller(cleared); dropping.Sync(plan, x => x);
            cleared.Cleared = true; int dropped = dropping.Drop();
            world["teardown"] = new Dictionary<string, object> { ["dropped"] = dropped, ["engineCallsAfterClear"] = cleared.AfterClear };
            Check(moved && hidden && shown && published.Live.Count == 0 && i2.Removed == plan.Stamps.Count && i2.Errors.Count == 0 && dropped == plan.Stamps.Count && cleared.AfterClear == 0 && dropping.Live == 0,
                  label + ": a seam refresh moves every stamp to its image, the toggle hides and shows them all (MMR-247's c31 check), Remove removes each once, and after Scene.ClearAll the installer drops all " + dropped + " handles with 0 engine calls");

            // ---- fallback modes ----
            // In bed or shore mode the layer does not declare IMagicWaterVeins, so the ground plans with no water layer and drops no water stamp:
            // compared with the surface plan of every water stamp.
            var allWater = MagicWater.Plan(g, lakes, water, magic);
            var f1 = MagicWater.Plan(g, lakes, WithMode(repo, RoadsPolicy.MagicWaterPolicy.Bed), magic);
            var f2 = MagicWater.Plan(g, lakes, WithMode(repo, RoadsPolicy.MagicWaterPolicy.Shore), magic);
            int planeStamps = allWater.Stamps.Count(s => !Double.IsNaN(s.PlaneZ)), shallow = allWater.Stamps.Count(s => !Double.IsNaN(s.PlaneZ) && s.Depth <= water.BedMaxDepth);
            var glow = f2.Stamps.Where(s => s.Mode == MagicWaterMode.Glow).ToList();
            bool glowOk = glow.All(s =>
            {
                double ex = s.Decal.X + Math.Cos(s.Decal.Yaw) * (MagicVeins.TipEnd - 0.5) * magic.StampLength, ey = s.Decal.Y + Math.Sin(s.Decal.Yaw) * (MagicVeins.TipEnd - 0.5) * magic.StampLength;
                var land = g.Nodes[s.Node];
                return !land.OnWater && s.Decal.Material == magic.MaterialPrefix + land.Look && s.Fallback == null && s.Kind == VeinKind.Tip && s.Decal.Atlas[3] == land.CoreRow / 4f
                    && Near(coverAt, src, ex, ey, g.Source.Cells.Spacing);
            });
            world["fallbackModes"] = new Dictionary<string, object> { ["bed"] = f1.Record(), ["shore"] = f2.Record(), ["shoreGlowStamps"] = glow.Count };
            Check(f1.Stamps.All(s => Double.IsNaN(s.PlaneZ) && s.Mode != MagicWaterMode.Glow) && f1.Skipped.ContainsKey("deep") && f1.Skipped["deep"] == planeStamps - shallow && f1.Stamps.Count == allWater.Stamps.Count - (planeStamps - shallow)
                  && f2.Stamps.Count(s => s.Mode != MagicWaterMode.Glow) == f1.Stamps.Count && glow.Count > 0 && glow.Count + f2.Skipped.Where(kv => kv.Key.StartsWith("glow", StringComparison.Ordinal)).Sum(kv => kv.Value) == g.ShoreEdges().Count() && glowOk,
                  label + ": fallback F1 (mode bed) keeps the " + shallow + " of " + planeStamps + " plane stamps over a bed at most " + water.BedMaxDepth + " deep, centred on the bed; F2 (mode shore) adds " + glow.Count +
                  " ground-material tips on the land side of the shore crossings, ending at the shoreline");

            // ---- a live mutation: a coastal fire cast adds live water stamps; a redraw touches only the zones it changes ----
            var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
            Func<int, string> naturalCore = i => { BiomeTable.Row row; return table.Rows.TryGetValue((int)source.Biome[i], out row) ? row.Core : null; };
            Func<string, string, int> variantOf = (core, school) => { var v = src.Catalogue.FirstOrDefault(x => x.Core == core && x.School == school); return v == null ? -1 : v.Index; };
            List<MutationCell> best = null; double[] bestAt = null;
            foreach (var town in Json.A(graph, "settlements").Cast<IDictionary<string, object>>().Where(s => Json.S(s, "kind") == "town" && s.ContainsKey("point")))
            {
                var tp = Json.Point(town, "point");
                int centre = src.SampleAt(tp[0] + surface.Strip, tp[1] + surface.PoleMargin), cr = centre / src.Side, cc = centre % src.Side;
                var cast = new List<MutationCell>();
                for (int dr = -5; dr <= 5; dr++) for (int dc = -5; dc <= 5; dc++)
                {
                    if (dr * dr + dc * dc > 25 || src.PoleRow(cr + dr)) continue;
                    int i = src.Index(cr + dr, cc + dc); string core = naturalCore(i); int v = core == null ? -1 : variantOf(core, "fire");
                    if (v >= 0) cast.Add(new MutationCell(cr + dr, src.Wrap(cc + dc), v, 0.7f));
                }
                int wet = cast.Count(m => table.Rows.Values.Any(r => r.Water && r.Core == src.Catalogue[m.Variant].Core));
                int bestWet = best == null ? -1 : best.Count(m => table.Rows.Values.Any(r => r.Water && r.Core == src.Catalogue[m.Variant].Core));
                if (wet > bestWet && wet < cast.Count) { best = cast; bestAt = new[] { tp[0] + surface.Strip, tp[1] + surface.PoleMargin }; }
            }
            overlay.Apply(best, new[] { new MagicNode("fire", bestAt[0], bestAt[1]) });
            var liveGround = MagicGround.Plan(overlay.Current, magic, drawsWater);
            var livePlan = MagicWater.Plan(liveGround.Graph, lakes, water, magic, liveGround.Dropped);
            var liveWater = livePlan.Stamps.Where(s => s.Live).ToList();
            var rec = new Recorder(); var inst = new MagicWaterInstaller(rec);
            inst.Sync(plan, x => x);
            int removedBefore = inst.Removed, createdBefore = inst.Created;
            inst.Sync(livePlan, x => x);
            int touched = (inst.Removed - removedBefore) + (inst.Created - createdBefore);
            var baseKeys = new HashSet<string>(plan.Stamps.Select(s => s.Key)); var liveKeys = new HashSet<string>(livePlan.Stamps.Select(s => s.Key));
            int expectedTouched = baseKeys.Count(k => !liveKeys.Contains(k)) + liveKeys.Count(k => !baseKeys.Contains(k));
            var castSamples = new HashSet<int>(best.Select(m => src.Index(m.Row, m.Column)));
            var untouched = new HashSet<int>(g.Zones.Where(z => !z.Samples.Any(castSamples.Contains)).Select(z => z.Index));
            bool kept = plan.Stamps.Where(s => untouched.Contains(s.Zone)).All(s => liveKeys.Contains(s.Key));
            world["liveCast"] = new Dictionary<string, object> { ["cells"] = best.Count, ["liveWaterStamps"] = liveWater.Count, ["byMode"] = liveWater.GroupBy(s => s.Mode).ToDictionary(x => x.Key, x => (object)x.Count()), ["redrawn"] = touched };
            Check(liveWater.Count > 0 && inst.Live == livePlan.Stamps.Count && touched == expectedTouched && touched > 0 && kept && inst.Errors.Count == 0,
                  label + ": a coastal fire cast (" + best.Count + " cells) adds " + liveWater.Count + " live water stamps (" + String.Join(", ", liveWater.GroupBy(s => s.Mode).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + " " + x.Count())) +
                  "); the installer redraws only the " + touched + " decals of the zones the cast touched");
            overlay.Replace(null, null);

            // ---- the same cast with a cast day: the runtime's reveal schedule shows every live water stamp at its own RevealAt ----
            {
                double castAt = 5 * 24.0;
                var dayOverlay = new WorldSurfaceOverlay(src);
                dayOverlay.Replace(best, new[] { new MagicNode("fire", bestAt[0], bestAt[1]) { Since = castAt } });
                var dayGround = MagicGround.Plan(dayOverlay.Current, magic, drawsWater);
                var dayWater = MagicWater.Plan(dayGround.Graph, lakes, water, magic, dayGround.Dropped);
                var live = dayWater.Stamps.Where(s => s.Live).ToList();
                int landTop = dayGround.Stamps.Where(s => s.Live).Select(s => s.Order).DefaultIfEmpty(-1).Max();
                int above = live.Count(s => s.Order > landTop), ownTime = live.Count(s => dayGround.Stamps.All(x => !x.Live || x.RevealAt != s.RevealAt));
                var fixedTiming = Timing(dayWater, castAt, dayGround.NextReveal); var landTiming = Timing(dayWater, castAt, LandOnly(dayGround));
                world["liveCastSchedule"] = new Dictionary<string, object> { ["castHours"] = castAt, ["liveWaterStamps"] = live.Count, ["aboveHighestLandOrder"] = above, ["atTimesNoLandStampHas"] = ownTime,
                    ["onTime"] = fixedTiming[0], ["late"] = fixedTiming[1], ["never"] = fixedTiming[2], ["ticks"] = fixedTiming[3],
                    ["landOnlySchedule"] = new Dictionary<string, object> { ["onTime"] = landTiming[0], ["late"] = landTiming[1], ["never"] = landTiming[2], ["ticks"] = landTiming[3] } };
                Check(live.Count > 0 && live.All(s => s.RevealAt >= castAt) && fixedTiming[0] == live.Count && fixedTiming[1] == 0 && fixedTiming[2] == 0,
                      label + ": with a cast day the runtime's schedule (Draw, then Advance at each NextReveal) shows all " + live.Count + " live water stamps of the coastal cast at their own RevealAt in " + fixedTiming[3] +
                      " ticks (" + above + " above the cast's highest land Order, " + ownTime + " at times no live land stamp has; the land stamps' schedule alone showed " + landTiming[1] + " late and " + landTiming[2] + " never)");
            }

            // ---- MMR-261's recorded Nerdun fire cast (A'): its water veins appear with its land veins ----
            if (label == "A")
            {
                var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
                var sites = MutationSites.From(campaign, graph, source.Realms, surface.Strip, surface.PoleMargin);
                const string NerdunUid = "surface-city-2-13033-human_cold";
                string nerdun = sites.CityTown[NerdunUid]; var at = sites.Point[nerdun];
                var delta = MutationDelta.Read(File.ReadAllText(Path.Combine(repo, "tests/World/fixtures/mutation/nerdun-fire.delta.json"), new UTF8Encoding(false)), src, source.Seed);
                var edit = new MutationEdit { Day = 10, School = "fire", Site = nerdun, At = nerdun, Rule = "capital", Node = "mnbl-cast-fire-" + nerdun, Intensity = 4, City = NerdunUid, X = at[0], Y = at[1] };
                var castNodes = MutationOperations.Nodes(new[] { edit }).Select(kv => new MagicNode(kv.Key.School, kv.Key.X, kv.Key.Y)).ToList();
                var state = new MutationState { Producer = "p", World = source.Sha256, Request = "r", Day = 10, Edits = new List<MutationEdit> { edit }, Cells = delta.Cells, Nodes = castNodes };
                var model = new MutationModel(new MutationModel.Context { Source = src, WorldSha256 = source.Sha256, RequestSha256 = "r" });
                List<MutationCell> loadedCells; List<MagicNode> loadedNodes;
                bool loaded = model.Load(state.ToJson(), out loadedCells, out loadedNodes);
                var castOverlay = new WorldSurfaceOverlay(src);
                castOverlay.Replace(loadedCells, loadedNodes);
                var castGround = MagicGround.Plan(castOverlay.Current, magic, drawsWater);
                var castPlan = MagicWater.Plan(castGround.Graph, lakes, water, magic, castGround.Dropped);
                double since = 10 * 24.0, grownAt = since + magic.RevealHours;
                var castWater = castPlan.Stamps.Where(s => s.Live).ToList();
                var reveal = new SortedDictionary<string, object>(StringComparer.Ordinal);
                var steps = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(f => castPlan.Revealed(since + f * magic.RevealHours)).ToList();
                for (int k = 0; k < steps.Count; k++) reveal[(k * 12).ToString("00", CultureInfo.InvariantCulture) + "h"] = steps[k].Stamps.Count(s => s.Live);
                var counts = steps.Select(pl => pl.Stamps.Count(s => s.Live)).ToList();
                bool grows = counts.Zip(counts.Skip(1), (a, b) => a <= b).All(x => x) && counts[4] == castWater.Count && castPlan.Revealed(since - 1).Stamps.Count(s => s.Live) == 0
                    && steps.Zip(steps.Skip(1), (a, b) => a.Stamps.All(b.Stamps.Contains)).All(x => x) && castPlan.Revealed(grownAt + 720).Sha256() == castPlan.Sha256();
                var castLand = castGround.Stamps.Where(s => s.Live).ToList();
                bool shared = castWater.All(s => castGround.Graph.Stamps.Any(x => x.Id == s.Id && x.OnWater && x.RevealAt == s.RevealAt)) && castLand.All(s => !castWater.Any(w => w.Id == s.Id));
                var castCells = loadedCells.Where(c => c.Variant >= 0).Select(c => src.CellOfSample(c.Row, c.Column)).Where(c => c >= 0).Distinct().ToList();
                world["nerdunCast"] = new Dictionary<string, object> { ["liveCells"] = loadedCells.Count, ["waterTravelCells"] = castCells.Count(c => TravelCells.Water(cells.Classes[c])),
                    ["liveWaterNodes"] = castGround.Graph.Nodes.Count(n => n.Live && n.OnWater), ["liveWaterStamps"] = castWater.Count, ["liveLandStamps"] = castLand.Count,
                    ["byMode"] = castWater.GroupBy(s => s.Mode).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Count()), ["byKind"] = castWater.GroupBy(s => s.Kind).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => (object)x.Count()),
                    ["revealedLiveWaterStamps"] = reveal, ["since"] = since, ["revealHours"] = magic.RevealHours };
                Check(loaded && castWater.Count > 0 && grows && shared,
                      label + ": the recorded Nerdun fire cast (" + castCells.Count(c => TravelCells.Water(cells.Classes[c])) + " water travel cells) draws " + castWater.Count + " live water stamps (" + String.Join(", ", castWater.GroupBy(s => s.Mode).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + " " + x.Count())) +
                      ") beside its " + castLand.Count + " land stamps, with the graph's ids and reveal times: " + String.Join(", ", reveal.Select(kv => kv.Key + " " + kv.Value)) + " over " + magic.RevealHours + " h, none before the cast, each step a superset, fully grown on a later Continue");
                var nerdunTiming = Timing(castPlan, since, castGround.NextReveal); var nerdunLand = Timing(castPlan, since, LandOnly(castGround));
                ((Dictionary<string, object>)world["nerdunCast"])["schedule"] = new Dictionary<string, object> { ["onTime"] = nerdunTiming[0], ["late"] = nerdunTiming[1], ["never"] = nerdunTiming[2], ["ticks"] = nerdunTiming[3],
                    ["landOnlySchedule"] = new Dictionary<string, object> { ["onTime"] = nerdunLand[0], ["late"] = nerdunLand[1], ["never"] = nerdunLand[2], ["ticks"] = nerdunLand[3] } };
                Check(nerdunTiming[0] == castWater.Count && nerdunTiming[1] == 0 && nerdunTiming[2] == 0,
                      label + ": the Nerdun cast's runtime schedule (Draw at the cast, then Advance at each NextReveal) shows all " + castWater.Count + " live water stamps at their own RevealAt in " + nerdunTiming[3] +
                      " ticks (the land stamps' schedule alone showed " + nerdunLand[1] + " late, " + nerdunLand[2] + " never)");
                if (dump != null) Dump(Path.Combine(dump, "A-nerdun"), castGround, castPlan, src, source, table, magic, water);
            }
            if (dump != null) Dump(Path.Combine(dump, label), ground, plan, src, source, table, magic, water);
        }

        // ---- the runtime's wiring (source checks: the engine side cannot run offline) ----
        string runtime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MagicWaterRuntime.cs"));
        string groundRuntime = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/World/MagicGroundRuntime.cs"));
        string release = Body(runtime, "public void Release(bool native)"), install = Body(runtime, "public void Install(");
        // The layer is registered in MagicGroundRuntime's one Layers initializer, after MMR-289's features layer (and before MMR-291's overlay
        // meshes); it declares IMagicWaterVeins
        // (the ground plans with the water veins drawn) and plans without the ground's dropped components.
        bool registered = System.Text.RegularExpressions.Regex.IsMatch(groundRuntime, @"Layers\s*=\s*new\s+List<IMagicVeinLayer>\s*\{\s*MagicFeaturesRuntime\.Layer\s*,\s*MagicWaterRuntime\.Instance\s*(,\s*OverlayMeshRuntime\.Instance\s*)?\}") && !groundRuntime.Contains("Layers.Add(");
        string frame = Body(runtime, "private MatrixFrame Frame(");
        Check(registered && runtime.Contains("class MagicWaterRuntime : IMagicVeinLayer, IMagicWaterVeins") && Body(runtime, "public bool DrawsWaterStamps").Contains("RoadsPolicy.MagicWaterPolicy.Surface")
              && Body(runtime, "private static ICollection<string> Dropped()").Contains("MagicGroundRuntime.Plan") && install.Contains("MagicWater.Plan(veins, lakes, policy.MagicWater, policy.Magic, Dropped())")
              && Body(runtime, "public void Changed(").Contains("Dropped()") && release.Contains("drawn.Drop()") && release.Contains("drawn.Remove()")
              && release.IndexOf("installer = null", StringComparison.Ordinal) < release.IndexOf("drawn.Remove()", StringComparison.Ordinal) && install.Contains("MapDressingRuntime.Current") && install.Contains("new EngineTarget(scene, new WaterPlanes(lakes))")
              && frame.Contains("if (Double.IsNaN(z))") && frame.Contains("planes.Box(d, x, y, Ground, out h, out half)"),
              "runtime: MagicGroundRuntime's one Layers initializer holds the features and then the magic water layer, which declares IMagicWaterVeins in mode surface and skips the ground's dropped components; install plans over the installed dressing's lake planes; a sea or lake box is centred on its plane's z and stays whole, any other is centred on the terrain and clipped over the water planes (WaterPlanes.Box); Release(true) removes natively, Release(false) (after Scene.ClearAll) only drops handles, and a second Release does nothing");
    }

    // Whether the shoreline is within one travel cell of a point: some point within r is water and some is land.
    private static bool Near(Func<double, double, string> cover, MagicGroundSource src, double x, double y, double r)
    {
        bool wet = false, dry = false;
        for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++) { if (cover(x + i * r / 2, y + j * r / 2) == "land") dry = true; else wet = true; }
        return wet && dry;
    }

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++) { if (source[i] == '{') depth++; else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1); }
        return "";
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

    // The land and water stamps over a plain base map, as tools/render_magic_veins.py's dump (frame, base, nodes, sources), with each water stamp's mode.
    private static void Dump(string dir, MagicGroundPlan ground, MagicWaterPlan water, MagicGroundSource src, GeneratedClimateSystem.Source source, BiomeTable table, RoadsPolicy.MagicPolicy magic, RoadsPolicy.MagicWaterPolicy policy)
    {
        Directory.CreateDirectory(dir);
        var g = ground.Graph;
        var cores = table.Rows.Values.Select(r => r.Core).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        var core = new int[src.Side * src.Side]; var wet = new int[src.Side * src.Side];
        for (int r = 0; r < src.Side; r++) for (int c = 0; c < src.Side; c++)
        {
            int i = r * src.Side + c; BiomeTable.Row row;
            core[i] = table.Rows.TryGetValue((int)source.Biome[i], out row) ? cores.IndexOf(row.Core) : -1;
            var p = src.Logical(r, Math.Min(c, src.Columns - 1));
            wet[i] = (row != null && row.Water) || src.PoleRow(r) || MagicVeins.OnWaterCell(src, src.Canonical(p[0]), p[1] + src.PoleMargin) ? 1 : 0;
        }
        Func<double, double> f = v => Double.IsInfinity(v) || Double.IsNaN(v) ? (Double.IsPositiveInfinity(v) ? 1e300 : -1e300) : Math.Round(v, 5);
        var stamps = ground.Stamps.Select(s => (object)new Dictionary<string, object> { ["id"] = s.Id, ["kind"] = s.Kind, ["look"] = s.Look, ["row"] = s.AtlasRow, ["col"] = s.AtlasColumn, ["x"] = f(s.Decal.X), ["y"] = f(s.Decal.Y),
            ["yaw"] = f(s.Decal.Yaw), ["length"] = f(2 * s.Decal.HalfLength), ["width"] = f(2 * s.Decal.HalfWidth), ["onWater"] = false, ["drawn"] = true, ["live"] = s.Live, ["revealAt"] = f(s.RevealAt), ["layer"] = "ground" })
            .Concat(water.Stamps.Select(s => (object)new Dictionary<string, object> { ["id"] = s.Id, ["kind"] = s.Kind, ["look"] = s.Look, ["row"] = MagicWaterMode.Ground(s.Mode) ? (int)Math.Round(s.Decal.Atlas[3] * 4) : s.AtlasRow, ["col"] = s.AtlasColumn,
                ["x"] = f(s.Decal.X), ["y"] = f(s.Decal.Y), ["yaw"] = f(s.Decal.Yaw), ["length"] = f(2 * s.Decal.HalfLength), ["width"] = f(2 * s.Decal.HalfWidth), ["onWater"] = true, ["drawn"] = true, ["live"] = s.Live,
                ["revealAt"] = f(s.RevealAt), ["layer"] = MagicWaterMode.Ground(s.Mode) ? "ground" : "water", ["mode"] = s.Mode, ["fallbackRow"] = s.Fallback == null ? -1 : (int)Math.Round(s.Fallback.Atlas[3] * 4) })).ToList();
        var doc = new Dictionary<string, object>
        {
            ["schema"] = "mnbl.magic-water-dump", ["version"] = 1,
            ["frame"] = new Dictionary<string, object> { ["width"] = src.Width, ["height"] = src.Height, ["strip"] = src.Strip, ["poleMargin"] = src.PoleMargin, ["side"] = src.Side, ["columns"] = src.Columns, ["meridian"] = src.Meridian },
            ["base"] = new Dictionary<string, object> { ["cores"] = cores, ["core"] = core, ["water"] = wet },
            ["policy"] = new Dictionary<string, object> { ["material"] = policy.Material, ["atlas"] = new[] { policy.Columns, policy.Rows }, ["widthScale"] = policy.WidthScale },
            ["stamps"] = stamps,
            ["nodes"] = g.Nodes.Select(n => (object)new object[] { f(n.X), f(n.Y), n.Parent, f(n.Width), n.Order, n.OnWater ? 1 : 0, n.Live ? 1 : 0, f(n.RevealAt) }).ToList(),
            ["sources"] = g.Sources.Select(s => (object)s.Node).ToList()
        };
        File.WriteAllText(Path.Combine(dir, "water.json"), Serializer.Serialize(doc), new UTF8Encoding(false));
    }
}
