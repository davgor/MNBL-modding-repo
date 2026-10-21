using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-291 overlay mesh tests, without the game:
//   OverlayMeshTests REPO BUNDLE_A BUNDLE_B OUT_JSON [DUMP_DIR]
// On two real compiled worlds (A' seed 42, B' seed 73), from the plans the decals were drawn from (MMR-288's ground plan, MMR-290's water plan,
// MMR-247-W's river chains and pools), as the runtime makes them:
//   a2  the overlay meshes: every drawn land vein stamp, magic water stamp, river chain, pool and fill becomes exactly one piece of one chunk
//       mesh, deterministically; indices valid, every triangle inside one atlas cell; vertices in the chunk's frame (pieces across the periodic seam
//       whole); draped pieces follow their surface within segment.maxError (recorded: the largest rise and fall), never below the terrain by
//       their lift; river ribbons level over the centre's ground + depth with dipped edges, covering every river cell centre; budgets (vertices,
//       triangles, entities whole world and in the busiest view window, a frame-cost estimate); MMR-261's recorded Nerdun fire cast (A') revealed
//       outward by band, a reveal step adding only its band's meshes and a Continue drawing the full plan; the installer over a recording target
//       (seam images, the toggle, removal, MMR-262's handle drop with no engine call); the decal switch (mode decal draws no meshes; the dressing's
//       water decals left out only while the rivers are meshed); the runtime wiring (source checks).
// DUMP_DIR: per world the meshes, the base map and a chosen window (DUMP_DIR/<A|B|A-nerdun>/overlay.json) for tools/render_overlay_meshes.py.
internal static class OverlayMeshTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    // Once Cleared is set (the engine ran Scene.ClearAll), every call fails and is counted: MMR-262's teardown must make none.
    private sealed class Recorder : IOverlayMeshTarget
    {
        public sealed class Item { public OverlayMesh Mesh; public double X; public bool Visible = true; }
        public readonly Dictionary<int, Item> Live = new Dictionary<int, Item>();
        public int Next, Calls, AfterClear; public bool Cleared; public Func<string, bool> Resolves = m => true;
        private void Engine() { Calls++; if (Cleared) { AfterClear++; throw new InvalidOperationException("an engine call on a cleared scene"); } }
        public object Add(OverlayMesh mesh, double x) { Engine(); if (!Resolves(mesh.Material)) return null; Live[++Next] = new Item { Mesh = mesh, X = x }; return Next; }
        public void Move(object handle, OverlayMesh mesh, double x) { Engine(); Live[(int)handle].X = x; }
        public void Remove(object handle) { Engine(); if (!Live.Remove((int)handle)) throw new InvalidOperationException("removed twice"); }
        public void SetVisible(object handle, bool visible) { Engine(); Live[(int)handle].Visible = visible; }
    }

    // A recording map dressing target (the water decals the installer creates).
    private sealed class DressingRecorder : IMapDressingTarget
    {
        public int Decals, Water;
        public object AddDecal(RoadDecal decal, double x, double y) { Decals++; if (decal.Kind != null) Water++; return Decals; }
        public void MoveDecal(object handle, RoadDecal decal, double x, double y) { }
        public void RemoveDecal(object handle) { }
        public object AddEntity(string prefab, double x, double y, double z, double yaw, double scale) { return 1; }
        public void MoveEntity(object handle, double x, double y, double z, double yaw, double scale) { }
        public void RemoveEntity(object handle) { }
        public double Ground(double x, double y) { return 0; }
    }

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.overlay-meshes-offline", ["version"] = 1, ["ticket"] = "MMR-291" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[3], Serializer.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // PeriodicMapCameraView.Nearest with the camera at cx: the image of x nearest it, inside the terrain.
    private static Func<double, double> Camera(double cx, double width, double extent)
    {
        return x => { double image = cx + OverlayMeshes.DeltaX(cx, x, width); if (image < 0) image += width; else if (image > extent) image -= width; return image; };
    }

    private static RoadsPolicy.OverlayPolicy WithModes(string repo, string veins, string water, string rivers)
    {
        var p = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath))).Overlay;
        p.VeinMode = veins; p.WaterMode = water; p.RiverMode = rivers;
        return p;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0], dump = args.Length > 4 && args[4].Length > 0 ? args[4] : null;
        var roads = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath)));
        var magic = roads.Magic; var water = roads.MagicWater; var p = roads.Overlay;
        var looks = SettlementLooksPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, SettlementLooksPolicy.PolicyPath)));
        var table = BiomeTable.FromBytes(File.ReadAllBytes(Path.Combine(repo, BiomeTable.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);

        Check(p.Enabled && p.DrawsVeins && p.DrawsMagicWater && p.DrawsRivers && p.VeinTemplate == "torch_flame_base" && p.WaterTemplate == "torch_flame_base" && p.RiverMaterials[0] == "naval_worldmap_water"
              && RoadsPolicy.OverlayPolicy.Named(p.VeinTextures, "fire") == magic.MaterialPrefix + "fire" && p.WaterTextures == water.Material && !p.VeinMaterial.StartsWith(magic.MaterialPrefix, StringComparison.Ordinal)
              && magic.Schools.All(s => p.VeinGlow.ContainsKey(s)) && p.VeinLift > p.MaxError,
              "roads.json overlayMeshes: veins, magic water and rivers drawn as meshes; veins and magic water copy vanilla " + p.VeinTemplate + " with the published textures " + p.VeinTextures + "_d/_n/_s and " + p.WaterTextures +
              "_d/_n (a published " + p.VeinMaterial + " / " + p.WaterMaterial + " first), rivers " + String.Join(" then ", p.RiverMaterials) + "; chunks " + p.Chunk + " units; draping " + p.MaxSegment + " to " + p.MinSegment +
              " units a cell within " + p.MaxError + ", lifts " + p.VeinLift + " / " + p.WaterLift);
        // MMR-291-W: the passes against the water. torch_flame_base's recorded flags as MaterialFlags bits (dont_draw_to_gbuffer taken as
        // DontDrawToDepthRenderTarget 0x4, no_modify_depth_buffer 0x8, two_sided 0x20; the decal lab records the engine's value): depth clears only
        // the depth write bit, forward adds worldmap_cloud's two, afterPostfx the crosshair's; keys carry a pass.
        const uint torch = 0x4 | 0x8 | 0x20;
        string keyPass, keyBase = OverlayPass.Split(OverlayPass.Key("vein:fire", "depth"), out keyPass), plainPass;
        string plainBase = OverlayPass.Split("magicWater", out plainPass);
        bool unknownRefused = false;
        try { OverlayPass.Flags("above", torch); } catch (ArgumentException) { unknownRefused = true; }
        Check(OverlayPass.Flags("blend", torch) == torch && OverlayPass.Flags("depth", torch) == (0x4 | 0x20) && OverlayPass.Flags("forward", torch) == (torch | 0x100000 | 0x40) && OverlayPass.Flags("afterPostfx", torch) == (torch | 0x20000000)
              && OverlayPass.AlphaTest("depth") && !OverlayPass.AlphaTest("blend") && !OverlayPass.AlphaTest("forward") && unknownRefused && keyBase == "vein:fire" && keyPass == "depth" && plainBase == "magicWater" && plainPass == null
              && p.Pass(OverlayLayer.MagicWater) == "depth" && p.Pass(OverlayLayer.Veins) == "blend" && p.AlphaTest > 0.25 && p.AlphaTest < 0.5 && RoadsPolicy.OverlayPolicy.Passes.All(x => OverlayPass.Flags(x, torch) != 0),
              "MMR-291-W passes against the water: magic water " + p.WaterPass + " (depth written behind alpha test " + p.AlphaTest + ": torch_flame_base's flags " + torch.ToString("x") + " -> " + OverlayPass.Flags("depth", torch).ToString("x") +
              "), veins " + p.VeinPass + "; forward adds needs_forward_rendering + alpha_blend_sort, afterPostfx render_after_postfx; a lab key names its pass (vein:fire@depth)");
        evidence["policy"] = new Dictionary<string, object> { ["enabled"] = p.Enabled, ["chunk"] = p.Chunk, ["segment"] = new[] { p.MaxSegment, p.MinSegment, p.MaxError }, ["flipV"] = p.FlipV, ["alphaTest"] = p.AlphaTest,
            ["veins"] = new Dictionary<string, object> { ["mode"] = p.VeinMode, ["material"] = p.VeinMaterial, ["template"] = p.VeinTemplate, ["textures"] = p.VeinTextures, ["lift"] = p.VeinLift, ["pass"] = p.VeinPass, ["glow"] = p.VeinGlow },
            ["magicWater"] = new Dictionary<string, object> { ["mode"] = p.WaterMode, ["material"] = p.WaterMaterial, ["template"] = p.WaterTemplate, ["textures"] = p.WaterTextures, ["lift"] = p.WaterLift, ["pass"] = p.WaterPass, ["glow"] = p.WaterGlow },
            ["rivers"] = new Dictionary<string, object> { ["mode"] = p.RiverMode, ["materials"] = p.RiverMaterials, ["width"] = p.RiverWidth, ["depth"] = p.RiverDepth, ["dip"] = p.RiverDip, ["lift"] = p.RiverLift, ["poolScale"] = p.PoolScale, ["fills"] = p.RiverFills },
            ["budget"] = new Dictionary<string, object> { ["vertices"] = p.MaxVertices, ["triangles"] = p.MaxTriangles, ["entities"] = p.MaxEntities, ["view"] = new[] { p.ViewWidth, p.ViewHeight }, ["viewEntities"] = p.ViewEntities,
                ["viewTriangles"] = p.ViewTriangles, ["entityMicroseconds"] = p.EntityMicroseconds } };

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
            var dressing = MapDressing.Build(bundle, surface, null, source, roads, looks);
            var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, cells, table, dressing.Lakes));
            var src = overlay.Source;
            // As the runtime plans them: the overlay layer declares IMagicWaterVeins (it draws the water stamps), so the ground plans with the
            // water veins drawn and the water plan skips the dropped components' water stamps.
            var ground = MagicGround.Plan(overlay.Current, magic, true);
            var waterPlan = MagicWater.Plan(ground.Graph, dressing.Lakes, water, magic, ground.Dropped);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            OverlayRiverSurface riverSurface;
            var rivers = OverlayMeshes.Rivers(dressing, dressing.Ground, dressing.Extent, p, out riverSurface);
            double riverMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
            var meshes = OverlayMeshes.Magic(ground, waterPlan, dressing.Lakes, p);
            double magicMs = watch.Elapsed.TotalMilliseconds;
            OverlayRiverSurface surface2;
            var rivers2 = OverlayMeshes.Rivers(MapDressing.Build(bundle, surface, null, source, roads, looks), dressing.Ground, dressing.Extent, p, out surface2);
            var ground2 = MagicGround.Plan(overlay.Current, magic, true);
            var meshes2 = OverlayMeshes.Magic(ground2, MagicWater.Plan(ground2.Graph, dressing.Lakes, water, magic, ground2.Dropped), dressing.Lakes, p);
            world["planMs"] = new Dictionary<string, object> { ["rivers"] = Math.Round(riverMs, 1), ["veinsAndMagicWater"] = Math.Round(magicMs, 1) };
            world["extent"] = surface.Extent; world["period"] = surface.LogicalWidth; world["sampleSpacing"] = surface.Spacing;

            // ---- pieces: one per drawn stamp, chain, pool and fill ----
            var veinIds = ground.Stamps.Select(s => s.Id).Concat(waterPlan.Stamps.Where(s => MagicWaterMode.Ground(s.Mode)).Select(s => s.Id)).ToList();
            var waterIds = waterPlan.Stamps.Where(s => !MagicWaterMode.Ground(s.Mode)).Select(s => s.Id).ToList();
            Func<OverlayMeshPlan, string, List<string>> pieces = (plan, layer) => plan.Of(layer).SelectMany(m => m.PieceIds).ToList();
            var drawnVeins = pieces(meshes, OverlayLayer.Veins); var drawnWater = pieces(meshes, OverlayLayer.MagicWater); var drawnRivers = pieces(rivers, OverlayLayer.Rivers).Distinct().ToList();   // a chain across chunks is one piece in each
            int wholeWet; meshes.Skipped.TryGetValue(OverlayLayer.Veins + ":wholly over water", out wholeWet);
            int pools = dressing.Water.Count(d => d.Kind == "pool"), fills = dressing.Water.Count(d => d.Kind == "fill");
            Func<List<string>, bool> distinctKeys = list => list.Distinct().Count() == list.Count;
            Check(distinctKeys(drawnVeins) && distinctKeys(drawnWater) && new HashSet<string>(drawnVeins).SetEquals(veinIds.Except(veinIds.Where(id => !drawnVeins.Contains(id)))) && drawnVeins.Count + wholeWet == veinIds.Count
                  && new HashSet<string>(drawnWater).SetEquals(waterIds) && drawnRivers.Count(id => id.StartsWith("river.", StringComparison.Ordinal)) == dressing.RiverLines.Count && dressing.RiverLines.Count == dressing.RiverChains
                  && drawnRivers.Count(id => id.StartsWith("pool.", StringComparison.Ordinal)) == pools && drawnRivers.Count(id => id.StartsWith("fill.", StringComparison.Ordinal)) == (p.RiverFills ? fills : 0)
                  && meshes.Sha256() == meshes2.Sha256() && rivers.Sha256() == rivers2.Sha256(),
                  label + ": one piece per drawn stamp, chain and disc, deterministically (veins " + meshes.Sha256().Substring(0, 12) + ", rivers " + rivers.Sha256().Substring(0, 12) + "): " + drawnVeins.Count + " vein pieces of " + veinIds.Count +
                  " land stamps (" + ground.Stamps.Count + " ground + " + (veinIds.Count - ground.Stamps.Count) + " inland and glow; " + wholeWet + " wholly over shown water left out), " + drawnWater.Count + " magic water pieces, " +
                  dressing.RiverChains + " river chains, " + pools + " pools, " + fills + " fills");

            // ---- geometry: indices, atlas cells, the chunk frame ----
            double maxPiece = Math.Max(magic.StampLength, magic.Width * magic.BurstScale) * 0.75 + p.RiverWidth;
            bool indicesOk = true, cellsOk = true, frameOk = true, finite = true;
            foreach (var m in meshes.Meshes.Concat(rivers.Meshes))
            {
                indicesOk &= m.Indices.Count % 3 == 0 && m.Indices.All(i => i >= 0 && i < m.Vertices) && m.Positions.Count == 3 * m.Vertices && m.Uvs.Count == 2 * m.Vertices;
                finite &= m.Positions.All(v => !float.IsNaN(v) && !float.IsInfinity(v)) && m.Uvs.All(v => !float.IsNaN(v));
                frameOk &= m.MinX >= -p.Chunk / 2 - maxPiece && m.MaxX <= p.Chunk / 2 + maxPiece && m.MinY >= -p.Chunk / 2 - maxPiece && m.MaxY <= p.Chunk / 2 + maxPiece;
                if (m.Layer == OverlayLayer.Rivers) continue;
                double cu = 0.25, cv = m.Layer == OverlayLayer.Veins ? 0.25 : 1.0 / water.Rows;
                for (int t = 0; t < m.Indices.Count && cellsOk; t += 3)
                {
                    var us = Enumerable.Range(0, 3).Select(k => m.Uvs[2 * m.Indices[t + k]]).ToList(); var vs = Enumerable.Range(0, 3).Select(k => m.Uvs[2 * m.Indices[t + k] + 1]).ToList();
                    int cellU = (int)Math.Floor((us.Average() + 1e-6) / cu), cellV = (int)Math.Floor((vs.Average() + 1e-6) / cv);
                    cellsOk &= us.All(u => u >= cellU * cu - 1e-4 && u <= (cellU + 1) * cu + 1e-4) && vs.All(v => v >= cellV * cv - 1e-4 && v <= (cellV + 1) * cv + 1e-4);
                }
            }
            Check(indicesOk && finite && cellsOk && frameOk,
                  label + ": every mesh's triangles index its own vertices, every vein and magic water triangle lies inside one atlas cell (4 x 4, and 4 x " + water.Rows + "), and every vertex sits in its chunk's frame within one piece of the chunk (" + p.Chunk + " units; pieces across the seam whole)");

            // ---- terrain following ----
            FollowError ve, we; meshes.Errors.TryGetValue(OverlayLayer.Veins, out ve); meshes.Errors.TryGetValue(OverlayLayer.MagicWater, out we);
            world["follow"] = new Dictionary<string, object> { ["veins"] = ve == null ? null : ve.Record(), ["magicWater"] = we == null ? null : we.Record(), ["maxError"] = p.MaxError, ["veinLift"] = p.VeinLift, ["waterLift"] = p.WaterLift,
                ["note"] = "maxAbove: the most the followed surface (ground, raised to a shown water plane or a river surface) rises over the draped mesh (before its lift), sampled at 16 points per grid cell (4 per burst triangle); maxBelow: the most the mesh floats over it" };
            Check(ve != null && ve.Pieces > 0 && ve.Above <= p.MaxError + 1e-9 && we != null && we.Above <= p.MaxError + 1e-9 && ve.Below < 2 && we.Below < 2,
                  label + ": draped pieces follow the ground: it rises at most " + F(ve.Above) + " over a vein piece (tolerance " + p.MaxError + " < lift " + p.VeinLift + ": the mesh stays over the terrain; " + ve.Refined + " of " + ve.Pieces +
                  " pieces refined toward " + p.MinSegment + " units, " + ve.OverBound + " raised over a sharp crest by at most " + F(ve.MaxRaise) + ") and floats at most " + F(ve.Below) + "; magic water bed and coast pieces " + F(we.Above) + " / " + F(we.Below) +
                  " (" + we.Pieces + " draped, " + we.OverBound + " raised)");

            // ---- rivers: level over the centre's ground + depth, banks dipped, every river cell centre covered ----
            var planes = new WaterPlanes(dressing.Lakes);
            double minDepth = Double.MaxValue, maxEdge = Double.MinValue; int sections = 0;
            foreach (var c in riverSurface.Levels()) { minDepth = Math.Min(minDepth, c[2] - dressing.Ground(src.Canonical(c[0] - src.Strip), c[1])); sections++; }   // ribbon centres and disc centres
            foreach (var m in rivers.Of(OverlayLayer.Rivers))
                for (int v = 0; v < m.Vertices; v++)
                {
                    float u = m.Uvs[2 * v];
                    if (u != 0f && u != 1f) continue;   // a ribbon's edges (u 0, 1) and a disc's outer ring across its u axis
                    double x = m.OriginX + m.Positions[3 * v], y = m.OriginY + m.Positions[3 * v + 1], z = m.Positions[3 * v + 2], g = dressing.Ground(src.Canonical(x - src.Strip), y);
                    maxEdge = Math.Max(maxEdge, z - (g - p.RiverDip));
                }
            int riverCells = 0, covered = 0, inner = 0, underPlane = 0; var uncovered = new List<object>();
            foreach (var c in dressing.WaterCells.Where(w => w.Class == TravelCells.River))
            {
                riverCells++;
                if (MapDressing.PlaneCover(dressing.Lakes, c.X, c.Y, c.Ground) != null) { underPlane++; covered++; continue; }
                if (riverSurface.Covers(c.X, c.Y)) covered++; else if (uncovered.Count < 10) uncovered.Add(new[] { c.Row, c.Column });
                if (riverSurface.Covers(c.X, c.Y, OverlayMeshes.RiverInner)) inner++;
            }
            world["rivers"] = new Dictionary<string, object> { ["chains"] = dressing.RiverChains, ["pools"] = pools, ["fills"] = fills, ["sections"] = sections, ["minCentreDepth"] = Math.Round(minDepth, 4), ["maxEdgeOverGroundLessDip"] = Math.Round(maxEdge, 4),
                ["riverCells"] = riverCells, ["covered"] = covered, ["coveredOnTheLevelSurface"] = inner, ["underPlane"] = underPlane, ["uncovered"] = uncovered, ["surfaceSegments"] = riverSurface.Segments, ["surfaceDiscs"] = riverSurface.Discs };
            Check(sections > 0 && minDepth >= p.RiverDepth - 1e-4 && maxEdge <= 1e-4 && covered == riverCells,
                  label + ": river ribbons stand at least " + F(minDepth) + " over the centre's ground (depth " + p.RiverDepth + ", more where a shown plane lifts them), their edges at or under the ground less " + p.RiverDip + "; all " + riverCells +
                  " river cell centres are covered by a ribbon or disc (" + inner + " on its level surface) or a shown plane (" + underPlane + ")");

            // ---- budgets ----
            var all = new OverlayMeshPlan { Width = meshes.Width, Strip = meshes.Strip, Chunk = meshes.Chunk };
            all.Meshes.AddRange(rivers.Meshes); all.Meshes.AddRange(meshes.Meshes);
            var totals = all.Totals(all.Meshes);
            var view = all.InView(p.ViewWidth, p.ViewHeight, 8);
            var near = all.InView(58, 37, 8);
            var busiest = (Dictionary<string, object>)((IDictionary<string, object>)view["busiest"])["total"];
            int viewEntities = (int)busiest["entities"], viewTriangles = (int)busiest["triangles"];
            double frameMs = viewEntities * p.EntityMicroseconds / 1000.0;
            world["budget"] = new Dictionary<string, object> { ["wholeWorld"] = totals, ["byLayer"] = new Dictionary<string, object> { ["veins"] = all.Totals(meshes.Of(OverlayLayer.Veins)), ["magicWater"] = all.Totals(meshes.Of(OverlayLayer.MagicWater)), ["rivers"] = all.Totals(rivers.Meshes) },
                ["view"] = view, ["closestView"] = near, ["frameCostEstimateMs"] = Math.Round(frameMs, 4),
                ["frameCostNote"] = "busiest view's entities x entityMicroseconds (MMR-249's main-thread cost per decal, a per-entity stand-in); GPU cost of the triangles unmeasured above the 60 fps cap" };
            world["plans"] = new Dictionary<string, object> { ["veinsAndMagicWater"] = meshes.Record(), ["rivers"] = rivers.Record() };
            Check((int)totals["vertices"] <= p.MaxVertices && (int)totals["triangles"] <= p.MaxTriangles && (int)totals["entities"] <= p.MaxEntities && viewEntities <= p.ViewEntities && viewTriangles <= p.ViewTriangles,
                  label + ": budgets hold: whole world " + totals["entities"] + " entities, " + totals["vertices"] + " vertices, " + totals["triangles"] + " triangles (caps " + p.MaxEntities + ", " + p.MaxVertices + ", " + p.MaxTriangles +
                  "); the busiest " + p.ViewWidth + " x " + p.ViewHeight + " view " + viewEntities + " entities and " + viewTriangles + " triangles (caps " + p.ViewEntities + ", " + p.ViewTriangles + "); main-thread estimate " + F(frameMs) + " ms");

            // ---- installer: seam images, the toggle, removal, MMR-262's drop ----
            double width = surface.LogicalWidth, extent = surface.Extent;
            var target = new Recorder();
            var installer = new OverlayMeshInstaller(target);
            double cam0 = surface.Strip + width * 0.02;   // near the seam's west edge
            installer.Sync(all, Camera(cam0, width, extent));
            Func<double, bool> isImage = dx => Math.Abs(dx / width - Math.Round(dx / width)) < 1e-9;
            bool imagesOk = target.Live.Values.All(it => isImage(it.X - it.Mesh.OriginX) && it.X >= 0 && it.X <= extent);
            bool nearest = target.Live.Values.All(it => Math.Abs(it.X - cam0) <= width / 2 + 1e-6 || it.X - width < 0 || it.X + width > extent);
            double cam1 = surface.Strip + width * 0.98;   // across the seam
            var before = target.Live.Values.ToDictionary(it => it.Mesh.Key, it => it.X);
            installer.Refresh(Camera(cam1, width, extent));
            int expectMoved = target.Live.Values.Count(it => Math.Abs(Camera(cam1, width, extent)(it.Mesh.OriginX) - before[it.Mesh.Key]) > 1e-6);
            bool movedOk = installer.Moved == expectMoved && expectMoved > 0 && target.Live.Values.All(it => Math.Abs(it.X - Camera(cam1, width, extent)(it.Mesh.OriginX)) < 1e-6);
            installer.SetVisible(OverlayLayer.MagicWater, false);
            bool toggle = target.Live.Values.All(it => it.Visible == (it.Mesh.Layer != OverlayLayer.MagicWater)) && installer.Pieces(OverlayLayer.MagicWater) == drawnWater.Count;
            installer.SetVisible(OverlayLayer.MagicWater, true);
            toggle &= target.Live.Values.All(it => it.Visible);
            int live = installer.Live;
            installer.Remove();
            var cleared = new Recorder(); var dropping = new OverlayMeshInstaller(cleared); dropping.Sync(all, Camera(cam0, width, extent));
            int callsBefore = cleared.Calls; cleared.Cleared = true; int dropped = dropping.Drop(); dropping.Remove();
            var none = new Recorder { Resolves = m => false }; var i0 = new OverlayMeshInstaller(none); i0.Sync(all, x => x);
            world["installer"] = new Dictionary<string, object> { ["created"] = live, ["moved"] = installer.Moved, ["removed"] = installer.Removed, ["dropped"] = dropped, ["engineCallsAfterClear"] = cleared.AfterClear,
                ["notDrawnWithoutMaterials"] = i0.NotDrawn, ["missingMaterials"] = i0.MissingMaterials.ToList() };
            Check(live == all.Meshes.Count && imagesOk && nearest && movedOk && toggle && target.Live.Count == 0 && installer.Removed == live && installer.Errors.Count == 0 && dropped == all.Meshes.Count && cleared.AfterClear == 0
                  && callsBefore == all.Meshes.Count && i0.Live == 0 && i0.NotDrawn == all.Meshes.Count && i0.Errors.Count == 0,
                  label + ": the installer draws one entity per mesh (" + live + ") at the image nearest the camera, a camera move across the seam moves the " + expectMoved + " whose image changed, the toggle hides and shows the magic water layer alone, " +
                  "Remove removes each once, after Scene.ClearAll all " + dropped + " handles go with 0 engine calls, and with no material nothing is drawn and nothing throws");

            // ---- the decal switch ----
            var decalModes = WithModes(repo, RoadsPolicy.OverlayPolicy.Decal, RoadsPolicy.OverlayPolicy.Decal, RoadsPolicy.OverlayPolicy.Decal);
            OverlayRiverSurface noSurface;
            var offRivers = OverlayMeshes.Rivers(dressing, dressing.Ground, dressing.Extent, decalModes, out noSurface);
            var offMagic = OverlayMeshes.Magic(ground, waterPlan, dressing.Lakes, decalModes);
            var veinsOnly = OverlayMeshes.Magic(ground, waterPlan, dressing.Lakes, p, true, false);
            var kept = new DressingRecorder(); var keptInstaller = new MapDressingInstaller(dressing, kept); keptInstaller.Install(x => x);
            var skipping = new DressingRecorder(); var skipInstaller = new MapDressingInstaller(dressing, skipping) { SkipWater = d => d.Kind == "river" || d.Kind == "pool" || (p.RiverFills && d.Kind == "fill") }; skipInstaller.Install(x => x);
            int replacedExpect = dressing.Water.Count(d => d.Kind == "river" || d.Kind == "pool" || (p.RiverFills && d.Kind == "fill"));
            Check(offRivers.Meshes.Count == 0 && offMagic.Meshes.Count == 0 && veinsOnly.Of(OverlayLayer.MagicWater).Count() == 0 && veinsOnly.Of(OverlayLayer.Veins).Count() > 0
                  && kept.Water == dressing.Water.Count && skipping.Water == dressing.Water.Count - replacedExpect && skipInstaller.ReplacedWater == replacedExpect && kept.Decals - kept.Water == skipping.Decals - skipping.Water,
                  label + ": mode decal draws no mesh (the decals as before), a layer the runtime cannot resolve is planned out alone, and the map dressing leaves out exactly the " + replacedExpect + " river, pool and fill decals the river meshes replace (" +
                  (dressing.Water.Count - replacedExpect) + " water decals kept)");

            // ---- MMR-261's recorded Nerdun fire cast (A'): revealed outward by band; a Continue draws it grown ----
            if (label == "A")
            {
                var graph = (IDictionary<string, object>)Serializer.DeserializeObject(new UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
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
                var castGround = MagicGround.Plan(castOverlay.Current, magic, true);
                var castWater = MagicWater.Plan(castGround.Graph, dressing.Lakes, water, magic, castGround.Dropped);
                var castMeshes = OverlayMeshes.Magic(castGround, castWater, dressing.Lakes, p);
                double since = 10 * 24.0;
                var liveMeshes = castMeshes.Meshes.Where(m => m.Live).ToList();
                var bands = liveMeshes.Select(m => m.RevealAt).Distinct().OrderBy(v => v).ToList();
                var steps = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(f => castMeshes.Revealed(since + f * magic.RevealHours)).ToList();
                var counts = steps.Select(s => s.Meshes.Count(m => m.Live)).ToList();
                var pieceCounts = steps.Select(s => s.Meshes.Where(m => m.Live).Sum(m => m.PieceIds.Count)).ToList();
                // The runtime's schedule: draw at the cast time, then a sync at each reveal step adds only that step's band.
                var castTarget = new Recorder(); var castInstaller = new OverlayMeshInstaller(castTarget);
                castInstaller.Sync(castMeshes.Revealed(since), x => x);
                bool stepsOk = true; int stepCount = 0;
                for (double t = castMeshes.NextReveal(since); !Double.IsPositiveInfinity(t); t = castMeshes.NextReveal(t))
                {
                    int created = castInstaller.Created, removed = castInstaller.Removed;
                    castInstaller.Sync(castMeshes.Revealed(t), x => x);
                    stepsOk &= castInstaller.Removed == removed && castInstaller.Created - created == castMeshes.Meshes.Count(m => m.RevealAt == t);
                    stepCount++;
                }
                bool grown = castInstaller.Live == castMeshes.Meshes.Count && castMeshes.Revealed(since + magic.RevealHours + 720).Sha256() == castMeshes.Sha256();
                // A cast changes only the chunk meshes whose geometry it changes.
                var baseDigests = new HashSet<string>(meshes.Meshes.Select(m => m.Key + " " + m.Digest));
                int unchanged = castMeshes.Meshes.Count(m => baseDigests.Contains(m.Key + " " + m.Digest)), changed = castMeshes.Meshes.Count - unchanged;
                var livePieces = liveMeshes.SelectMany(m => m.PieceIds).ToList();
                var liveStampIds = castGround.Stamps.Where(s => s.Live).Select(s => s.Id).Concat(castWater.Stamps.Where(s => s.Live).Select(s => s.Id)).ToList();
                world["nerdunCast"] = new Dictionary<string, object> { ["liveMeshes"] = liveMeshes.Count, ["bands"] = bands.Count, ["livePieces"] = livePieces.Count, ["liveStamps"] = liveStampIds.Count,
                    ["liveMeshesShownAt"] = new[] { 0, 12, 24, 36, 48 }.Zip(counts, (h, c) => new KeyValuePair<string, int>(h + "h", c)).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["livePiecesShownAt"] = new[] { 0, 12, 24, 36, 48 }.Zip(pieceCounts, (h, c) => new KeyValuePair<string, int>(h + "h", c)).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["revealSteps"] = stepCount, ["chunkMeshesChangedByTheCast"] = changed, ["chunkMeshesKept"] = unchanged, ["since"] = since, ["revealHours"] = magic.RevealHours };
                Check(loaded && liveMeshes.Count > 0 && new HashSet<string>(livePieces).IsSubsetOf(liveStampIds) && counts.Zip(counts.Skip(1), (a, b) => a <= b).All(x => x) && counts[4] == liveMeshes.Count && castMeshes.Revealed(since - 1).Meshes.Count(m => m.Live) == 0
                      && stepsOk && grown && changed > 0 && unchanged > changed,
                      label + ": the recorded Nerdun fire cast draws " + livePieces.Count + " live pieces in " + liveMeshes.Count + " band meshes, shown " + String.Join("/", pieceCounts) + " at 0/12/24/36/48 h after its batch day and none before; each of the " + stepCount +
                      " reveal steps adds only its band's meshes; a Continue after " + magic.RevealHours + " h draws it grown; the cast recreates " + changed + " chunk meshes and keeps " + unchanged);
                if (dump != null) Dump(Path.Combine(dump, label + "-nerdun"), src, source, table, castMeshes, rivers, dressing, new[] { at[0], at[1] });
            }
            if (dump != null) Dump(Path.Combine(dump, label), src, source, table, meshes, rivers, dressing, null);
        }

        // ---- the runtime wiring (source checks: the engine files cannot compile offline) ----
        string Read(string path) { return File.ReadAllText(Path.Combine(repo, path)); }
        string runtime = Read("src/MNBL.ProceduralWorld/World/OverlayMeshRuntime.cs"), groundRt = Read("src/MNBL.ProceduralWorld/World/MagicGroundRuntime.cs"), waterRt = Read("src/MNBL.ProceduralWorld/World/MagicWaterRuntime.cs"),
               dressingRt = Read("src/MNBL.ProceduralWorld/World/MapDressingRuntime.cs"), probe = Read("src/MNBL.ProceduralWorld/Diagnostics/WaterVisibilityProbe.cs"), lab = Read("src/MNBL.ProceduralWorld/Automation/Map2AcceptanceDecalLab.cs");
        Func<string, string, string> body = (text, signature) => { int i = text.IndexOf(signature, StringComparison.Ordinal); if (i < 0) return ""; int open = text.IndexOf('{', i), depth = 0, k = open; for (; k < text.Length; k++) { if (text[k] == '{') depth++; else if (text[k] == '}' && --depth == 0) break; } return text.Substring(open, k - open + 1); };
        string release = body(runtime, "public void Release(bool native)"), releaseRivers = body(runtime, "internal static void ReleaseRivers(bool native)"), add = body(runtime, "public object Add(OverlayMesh m, double x)");
        bool wiring = Regex.IsMatch(groundRt, @"Layers = new List<IMagicVeinLayer> \{[^}]*OverlayMeshRuntime\.Instance") && groundRt.Contains("OverlayMeshRuntime.DrawsVeins ? plan.WithoutStamps() : plan")
            && body(waterRt, "public void Install(").Contains("if (OverlayMeshRuntime.DrawsMagicWater) { replaced = true; return; }")
            && dressingRt.Contains("SkipWater = OverlayMeshRuntime.RiverDecalsReplaced()") && dressingRt.Contains("OverlayMeshRuntime.InstallRivers(mapScene, plan, Image)") && dressingRt.Contains("OverlayMeshRuntime.RefreshRivers(Image)")
            && body(dressingRt, "internal static void Release(Action<string> trace)").Contains("OverlayMeshRuntime.ReleaseRivers(true)") && body(dressingRt, "internal static void ReleaseHandles(Action<string> trace)").Contains("OverlayMeshRuntime.ReleaseRivers(false)")
            && release.Contains("if (native) drawn.Remove(); else drawn.Drop();") && releaseRivers.Contains("if (native) drawn.Remove(); else drawn.Drop();")
            && new[] { "Mesh.CreateMeshWithMaterial(", "LockEditDataWrite()", "AddFaceCorner(", "AddFace(", "ComputeNormals()", "ComputeTangents()", "RecomputeBoundingBox()", "UnlockEditDataWrite(", "GameEntity.CreateEmpty(", "AddMesh(", "SetGlobalFrame(", "SetVectorArgument(" }.All(add.Contains)
            && runtime.Contains("CreateCopy()") && runtime.Contains("Material.MBTextureType.DiffuseMap2") && probe.Contains("OverlayMeshRuntime.RiverCovers(cell.X, cell.Y)") && lab.Contains("LabMeshes(")
            // MMR-291-W: both the template copy and a published material take the layer's pass; depth adds the alpha test.
            && Regex.Matches(runtime, @"mat = Pass\(mat, pass, (true|false), rec\)").Count == 2 && body(runtime, "private Material Pass(").Contains("Flags = (MaterialFlags)OverlayPass.Flags(pass, before)")
            && body(runtime, "private Material Pass(").IndexOf("AddMaterialShaderFlag(\"alpha_test\", false)", StringComparison.Ordinal) > 0
            && body(runtime, "private Material Pass(").IndexOf("AddMaterialShaderFlag(\"alpha_test\", false)", StringComparison.Ordinal) < body(runtime, "private Material Pass(").IndexOf("Flags = (MaterialFlags)", StringComparison.Ordinal)
            && body(runtime, "private Material Pass(").Contains("SetAlphaTestValue(") && lab.Contains("OverlayPass.Key(");
        evidence["wiring"] = wiring;
        Check(wiring, "the runtime wiring: the overlay layer runs after the features and the magic water decals in MagicGroundRuntime.Layers and the land decals give way while it draws the veins, MagicWaterRuntime draws no decals while the meshes draw the water, " +
              "the map dressing leaves out the replaced water decals and installs, refreshes and releases the river meshes (natively on the live scene, handles only after Scene.ClearAll), a mesh is built in TaleWorlds' order and the template copy takes the textures, " +
              "the water check counts a river mesh, and the decal lab draws the mesh grid; every vein and magic water material (copy or published) takes its layer's pass, depth with the alpha test (MMR-291-W), and the lab keys name their passes");
    }

    // The window (100 x 64 units) holding the most of the scarcest layer (veins, rivers, magic water), for the preview.
    private static double[] PreviewWindow(OverlayMeshPlan magic, OverlayMeshPlan rivers, double w, double h)
    {
        var all = magic.Meshes.Concat(rivers.Meshes).ToList();
        double[] best = null; int bestScore = -1;
        foreach (var anchor in all.Where(m => m.Layer == OverlayLayer.MagicWater || m.Layer == OverlayLayer.Rivers))
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    double x0 = anchor.OriginX - w / 2 + dx * w / 4, y0 = anchor.OriginY - h / 2 + dy * h / 4;
                    var counts = OverlayLayer.All.Select(layer => all.Where(m => m.Layer == layer && m.OriginX + m.MaxX > x0 && m.OriginX + m.MinX < x0 + w && m.OriginY + m.MaxY > y0 && m.OriginY + m.MinY < y0 + h).Sum(m => m.PieceIds.Count)).ToList();
                    int score = counts.Min() * 1000 + counts.Sum();
                    if (score > bestScore) { bestScore = score; best = new[] { x0, y0, w, h }; }
                }
        return best;
    }

    // The ground (MapDressing.GroundOf) every half unit over a window and 8 units around it, for the preview's water and shore cuts.
    private static Dictionary<string, object> Heights(MapDressingPlan dressing, MagicGroundSource src, double[] window)
    {
        if (window == null) return null;
        const double step = 0.5, pad = 8;
        double x0 = window[0] - pad, y0 = window[1] - pad;
        int nx = (int)Math.Ceiling((window[2] + 2 * pad) / step) + 1, ny = (int)Math.Ceiling((window[3] + 2 * pad) / step) + 1;
        var values = new List<double>(nx * ny);
        for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++) values.Add(Math.Round(dressing.Ground(src.Canonical(x0 + i * step - src.Strip), y0 + j * step), 3));
        return new Dictionary<string, object> { ["x0"] = x0, ["y0"] = y0, ["step"] = step, ["nx"] = nx, ["ny"] = ny, ["values"] = values };
    }

    private static void Dump(string dir, MagicGroundSource src, GeneratedClimateSystem.Source source, BiomeTable table, OverlayMeshPlan magic, OverlayMeshPlan rivers, MapDressingPlan dressing, double[] centre)
    {
        Directory.CreateDirectory(dir);
        if (centre == null)
        {
            // A second view: the middle of the longest river chain.
            var longest = dressing.RiverLines.OrderByDescending(l => MapDressing.Length(l)).FirstOrDefault();
            if (longest != null)
            {
                var mid = longest[longest.Count / 2];
                DumpWindow(Path.Combine(dir, "river.json"), src, source, table, magic, rivers, dressing, new[] { src.Strip + ((mid[0] - src.Strip) % src.Width + src.Width) % src.Width - 40, mid[1] - 25, 80.0, 50.0 }, null);
            }
        }
        DumpWindow(Path.Combine(dir, "overlay.json"), src, source, table, magic, rivers, dressing, centre != null ? new[] { centre[0] - 30, centre[1] - 20, 60.0, 40.0 } : PreviewWindow(magic, rivers, 100, 64), centre);
    }

    private static void DumpWindow(string file, MagicGroundSource src, GeneratedClimateSystem.Source source, BiomeTable table, OverlayMeshPlan magic, OverlayMeshPlan rivers, MapDressingPlan dressing, double[] window, double[] centre)
    {
        var cores = table.Rows.Values.Select(r => r.Core).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        var core = new int[src.Side * src.Side]; var wet = new int[src.Side * src.Side];
        for (int r = 0; r < src.Side; r++) for (int c = 0; c < src.Side; c++)
        {
            int i = r * src.Side + c; BiomeTable.Row row;
            core[i] = table.Rows.TryGetValue((int)source.Biome[i], out row) ? cores.IndexOf(row.Core) : -1;
            var q = src.Logical(r, Math.Min(c, src.Columns - 1));
            wet[i] = (row != null && row.Water) || src.PoleRow(r) || MagicVeins.OnWaterCell(src, src.Canonical(q[0]), q[1] + src.PoleMargin) ? 1 : 0;
        }
        Func<OverlayMesh, bool> inWindow = m => window == null || (m.OriginX + m.MaxX > window[0] - 64 && m.OriginX + m.MinX < window[0] + window[2] + 64 && m.OriginY + m.MaxY > window[1] - 64 && m.OriginY + m.MinY < window[1] + window[3] + 64);
        Func<float, double> r4 = v => Math.Round(v, 4);
        var list = new List<object>();
        foreach (var m in magic.Meshes.Concat(rivers.Meshes).Where(inWindow))
        {
            var groundAt = new List<double>();
            for (int v = 0; v < m.Vertices; v++) groundAt.Add(Math.Round(dressing.Ground(src.Canonical(m.OriginX + m.Positions[3 * v] - src.Strip), m.OriginY + m.Positions[3 * v + 1]), 4));
            list.Add(new Dictionary<string, object> { ["key"] = m.Key, ["layer"] = m.Layer, ["look"] = m.Look, ["live"] = m.Live, ["revealAt"] = Double.IsNegativeInfinity(m.RevealAt) ? null : (object)m.RevealAt, ["glow"] = m.Glow,
                ["origin"] = new[] { m.OriginX, m.OriginY }, ["positions"] = m.Positions.Select(r4).ToList(), ["uvs"] = m.Uvs.Select(r4).ToList(), ["alpha"] = m.Colours.Select(c => (int)(c >> 24)).ToList(),
                ["indices"] = m.Indices, ["ground"] = groundAt });
        }
        var doc = new Dictionary<string, object>
        {
            ["schema"] = "mnbl.overlay-meshes-dump", ["version"] = 1,
            ["frame"] = new Dictionary<string, object> { ["width"] = src.Width, ["height"] = src.Height, ["strip"] = src.Strip, ["poleMargin"] = src.PoleMargin, ["side"] = src.Side, ["columns"] = src.Columns, ["meridian"] = src.Meridian },
            ["base"] = new Dictionary<string, object> { ["cores"] = cores, ["core"] = core, ["water"] = wet },
            ["window"] = window, ["centre"] = centre, ["meshes"] = list, ["heights"] = Heights(dressing, src, window),
            ["lakes"] = dressing.Lakes.Select(l => (object)new[] { l.MinX, l.MinY, l.MaxX, l.MaxY, l.Z }).ToList()
        };
        File.WriteAllText(file, Serializer.Serialize(doc), new UTF8Encoding(false));
    }
}
