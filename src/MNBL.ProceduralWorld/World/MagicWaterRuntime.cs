using System;
using System.Collections.Generic;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.World
{
    // MMR-290: magic water on the campaign map (docs/magic-water.md; MagicWater is the pure model). An IMagicVeinLayer of MagicGroundRuntime
    // (in its Layers initializer, after MMR-289's features), so it follows the magic ground's lifetime on the engine thread:
    //   * IMagicWaterVeins (MMR-288): in mode surface it draws every water stamp, so the ground overlay plans with the water veins drawn (land
    //     branches hanging off water nodes continue them) and this layer skips the water stamps of the components the ground drops
    //     (MagicGroundRuntime.Plan.Dropped). The fallback modes (bed, shore) skip deep plane stamps, so the ground plans as with no water layer.
    //   * Install (GeneratedMapScene.AfterLoad, after the magic ground decals, so after the map dressing): plans the vein graph's water stamps over
    //     the scene's shown lake planes (MapDressingRuntime's installed plan) and draws what the campaign time shows.
    //   * Changed (a live cast at its safe point, or a reveal step): a new graph is re-planned; the installer redraws only the stamps that changed.
    //   * RefreshImages (a camera wrap): each stamp takes its image nearest the camera.
    //   * Release(true) (a new Begin on the same live scene) removes the decals natively; Release(false) (GeneratedMapScene.Destroy, after
    //     Scene.ClearAll took them, MMR-262) only lets go of the handles, with no engine call. Release(false) after Release(true) does nothing.
    // Decals are MMR-249's entity-less scene decals (MapDressingRuntime.EngineTarget's calls) with an explicit box centre: a sea or lake stamp is
    // centred on its water plane (the water shader draws decals whose box holds the surface) and stays whole; any other is centred on the terrain
    // with its box raised over the water planes its footprint shows (MMR-247's WaterPlanes, the planes of the lakes this layer planned over), so
    // a bed, coast, inland or glow stamp does not paint the water surface. MMR-247 (D): the target judges each material by the campaign map's
    // decal atlas (DecalAtlasChooser): neither module material's textures have a rect there, so each stamp draws its vanilla fallback, with
    // its own box (RoadDecal.Box). Nothing here throws into the engine: failures go to Errors.
    // MMR-291: while the overlay meshes draw the water stamps (OverlayMeshRuntime.DrawsMagicWater) this layer draws no decals; Drawn, SetVisible
    // and Record then speak for the meshes, so MMR-247's checks (c31) read the same API.
    internal sealed class MagicWaterRuntime : IMagicVeinLayer, IMagicWaterVeins
    {
        public static readonly MagicWaterRuntime Instance = new MagicWaterRuntime();
        public static readonly List<string> Errors = new List<string>();
        private MagicWaterInstaller installer;
        private EngineTarget target;
        private MagicWaterPlan full;
        private MagicVeinGraph graph;
        private RoadsPolicy policy;
        private IList<GeneratedWaterSystem.Lake> lakes;
        private Func<double, double> image;
        private double installMs;
        private bool replaced;        // MMR-291: the overlay meshes draw this map's water stamps
        private static string mode;   // roads.json magicWater mode, read once (MagicGroundRuntime asks before this layer's Install)

        // The live installer (null without a map), for the support audit and MMR-247's checks (c31).
        public static MagicWaterInstaller Installer { get { return Instance.installer; } }
        // Decals, or (MMR-291) the water stamps the overlay meshes draw instead.
        public static int Drawn { get { return (Instance.installer == null ? 0 : Instance.installer.Live) + OverlayMeshRuntime.Pieces(OverlayLayer.MagicWater); } }
        public static Dictionary<string, object> Record()
        {
            var i = Instance.installer;
            var r = i == null ? new Dictionary<string, object> { ["status"] = Instance.replaced ? "replaced by the overlay meshes (MMR-291)" : "none" } : i.Record();
            r["installMs"] = Instance.installMs; r["lakePlanes"] = Instance.lakes == null ? 0 : Instance.lakes.Count; r["layerErrors"] = new List<string>(Errors);
            r["drawsWaterStamps"] = Instance.DrawsWaterStamps;
            if (Instance.target != null) { r["clippedOverWater"] = Instance.target.Clipped; r["submerged"] = Instance.target.Submerged; r["target"] = Instance.target.Chooser.Record(); }
            if (Instance.replaced) { r["meshPieces"] = OverlayMeshRuntime.Pieces(OverlayLayer.MagicWater); r["overlayMeshes"] = OverlayMeshRuntime.Record(); }
            return r;
        }

        // MMR-288's declaration: mode surface draws every water stamp of the graph.
        public bool DrawsWaterStamps
        {
            get
            {
                if (mode == null) { try { mode = RoadsPolicy.Installed().MagicWater.Mode; } catch (Exception e) { Note("policy", e); mode = ""; } }
                return mode == RoadsPolicy.MagicWaterPolicy.Surface;
            }
        }
        // The water ids of the components the ground overlay does not draw (its plan was made with this layer's declaration).
        private static ICollection<string> Dropped() { var ground = MagicGroundRuntime.Plan; return ground == null ? null : ground.Dropped; }
        // MMR-247's toggle check: hide (false) or show (true) every drawn magic water stamp. False when nothing is drawn.
        public static bool SetVisible(bool visible)
        {
            if (Instance.replaced) return OverlayMeshRuntime.SetVisible(OverlayLayer.MagicWater, visible);   // MMR-291: the meshes
            var i = Instance.installer;
            if (i == null) return false;
            try { i.SetVisible(visible); return true; }
            catch (Exception e) { Note("visible", e); return false; }
        }

        public void Install(Scene scene, MagicVeinGraph veins, double hours, Func<double, double> imageOf)
        {
            Release(false);
            Errors.Clear();
            replaced = false;
            if (scene == null || veins == null) return;
            // MMR-291: the overlay meshes draw the water stamps (roads.json overlayMeshes.magicWater mode mesh, a material that resolves): no decals.
            if (OverlayMeshRuntime.DrawsMagicWater) { replaced = true; return; }
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                policy = RoadsPolicy.Installed();
                var dressing = MapDressingRuntime.Current;
                lakes = dressing == null ? (IList<GeneratedWaterSystem.Lake>)new GeneratedWaterSystem.Lake[0] : dressing.Plan.Lakes;
                image = imageOf;
                graph = veins;
                full = MagicWater.Plan(veins, lakes, policy.MagicWater, policy.Magic, Dropped());
                target = new EngineTarget(scene, new WaterPlanes(lakes));
                installer = new MagicWaterInstaller(target);
                installer.Sync(full.Revealed(hours), image);
                installMs = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception e) { Note("install", e); }
        }

        public void Changed(MagicVeinGraph veins, double hours)
        {
            if (installer == null || veins == null) return;
            try
            {
                if (!ReferenceEquals(veins, graph)) { full = MagicWater.Plan(veins, lakes, policy.MagicWater, policy.Magic, Dropped()); graph = veins; }
                installer.Sync(full.Revealed(hours), image);
            }
            catch (Exception e) { Note("change", e); }
        }

        public void RefreshImages(Func<double, double> imageOf)
        {
            image = imageOf;
            if (installer == null) return;
            try { installer.Refresh(imageOf); }
            catch (Exception e) { Note("refresh", e); }
        }

        public void Release(bool native)
        {
            var drawn = installer;
            installer = null; target = null; full = null; graph = null; lakes = null;
            if (drawn == null) return;
            try { if (native) drawn.Remove(); else drawn.Drop(); }
            catch (Exception e) { Note(native ? "remove" : "drop", e); }
        }

        private static void Note(string step, Exception e) { if (Errors.Count < 20) Errors.Add(step + ": " + e.GetType().Name + ": " + e.Message); }

        private sealed class EngineTarget : IMagicWaterTarget
        {
            private readonly Scene scene;
            private readonly WaterPlanes planes;
            // MMR-247 (D): the materials, judged by the campaign map's decal atlas (neither module material's textures have a rect: the stamp's
            // vanilla fallback draws, with its own box).
            public readonly DecalAtlasChooser Chooser = new DecalAtlasChooser();
            private readonly HashSet<Decal> fallbackDrawn = new HashSet<Decal>();
            public int Clipped, Submerged;   // terrain-centred boxes raised over a water plane, or lying wholly under one (a sliver that paints nothing)
            public EngineTarget(Scene scene, WaterPlanes planes) { this.scene = scene; this.planes = planes ?? new WaterPlanes(null); }
            private double Ground(double x, double y) { return scene.GetTerrainHeight(new Vec2((float)x, (float)y), true); }
            // The rotation columns scaled by the half extents (the box [-1, 1]^3 projected along local z; a fallback's own box, RoadDecal.Box), as
            // MapDressingRuntime's, centred at z (a water plane: the box stays whole) or on the terrain with its bottom raised over the water planes
            // its footprint shows (WaterPlanes.Box with the plan's Floor).
            private MatrixFrame Frame(RoadDecal d, bool fallback, double x, double y, double z, bool count)
            {
                double yaw, halfU, halfV;
                d.Box(fallback, out yaw, out halfU, out halfV);
                float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw), l = (float)halfU, w = (float)halfV;
                double h = z, half = d.HalfDepth;
                if (Double.IsNaN(z))
                {
                    bool whole = planes.Box(d, x, y, Ground, out h, out half);
                    if (count) { if (!whole) Submerged++; else if (half < d.HalfDepth - 1e-9) Clipped++; }
                }
                var rot = new Mat3(new Vec3(c * l, s * l, 0), new Vec3(-s * w, c * w, 0), new Vec3(0, 0, (float)half));
                return new MatrixFrame(rot, new Vec3((float)x, (float)y, (float)h));
            }
            public object AddDecal(RoadDecal d, double x, double y, double z)
            {
                bool fallback;
                var material = Chooser.Choose(d, out fallback);
                if (material == null) return null;
                var atlas = fallback ? d.FallbackAtlas : d.Atlas; uint tint = fallback ? d.FallbackTint : d.Tint;
                var decal = Decal.CreateDecal(null);
                decal.SetMaterial(material);
                scene.AddDecalInstance(decal, MapDressingRuntime.DecalSet, true);
                decal.SetIsVisible(true);
                decal.SetVectorArgument(atlas[0], atlas[1], atlas[2], atlas[3]);
                if (tint != 0) decal.SetFactor1(tint);
                decal.Frame = Frame(d, fallback, x, y, z, true);
                if (fallback) fallbackDrawn.Add(decal);
                return decal;
            }
            public void MoveDecal(object handle, RoadDecal d, double x, double y, double z) { var decal = (Decal)handle; decal.Frame = Frame(d, fallbackDrawn.Contains(decal), x, y, z, false); }
            public void RemoveDecal(object handle) { var decal = (Decal)handle; fallbackDrawn.Remove(decal); scene.RemoveDecalInstance(decal, MapDressingRuntime.DecalSet); }
            public void SetVisible(object handle, bool visible) { ((Decal)handle).SetIsVisible(visible); }
        }
    }
}
