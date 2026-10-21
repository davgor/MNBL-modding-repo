using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.World
{
    // MMR-257: the map dressing on the campaign map (docs/map-dressing.md). The world's plan (MapDressing, built with its campaign by
    // WorldReconstruction at activation and at load) waits in Plans under the scene name; GeneratedMapScene.AfterLoad installs it on the
    // loaded scene. GeneratedMapScene.Destroy runs after MapScreen.OnFinalize's Scene.ClearAll, which already took every decal and entity
    // drawn (and freed the scene's decal container), so it only lets go of the handles (ReleaseHandles: no engine call, MMR-262); native
    // removal (Release) is for a re-install on the same live scene. Each object is drawn at its periodic image
    // nearest the camera and moved when the camera wraps or has moved far (PeriodicMapCameraView calls RefreshImages). A ruin marker shows
    // its name while the cursor's ground point is within MarkerHoverRadius of it. Nothing here may throw into an engine callback: failures are
    // recorded on the support report and in Errors.
    //   * Decals: entity-less scene decals, MMR-249's technique (Decal.CreateDecal(null), SetMaterial(published material),
    //     Scene.AddDecalInstance(d, "editor_set", true), SetIsVisible, SetVectorArgument(atlas), Frame: the rotation columns scaled by the
    //     half extents, the box [-1, 1]^3 projected along local z); removed with RemoveDecalInstance(d, "editor_set").
    //   * Water decals (MMR-285, MMR-247): the same technique with the published water material (mnbl_water_surface), each a cell of its
    //     atlas; while it does not resolve, the decal's fallback (the vanilla stamp tinted per instance with SetFactor1).
    //   * MMR-247 (D, acc5): the campaign map draws scene decals from Native's prebaked decal atlas only, so a material whose diffuse texture
    //     has no rect there (every module texture) draws nothing: the target draws the decal's fallback instead (roads.json decalAtlas;
    //     World/DecalAtlas.cs), and records per material its diffuse texture, whether the atlas holds it and the decals passed over.
    //   * Every decal box stays above the water planes under it (MMR-247, MapDressing's WaterPlanes): the campaign map's water shader adds
    //     every decal whose box holds the water surface. The target clips each frame it sets; the target MMR-260's magic ground makes with
    //     the scene alone takes the installed dressing's planes.
    //   * Bridges and ruin markers: installed prefabs (GameEntity.Instantiate), framed with yaw and scale; removed with Scene.RemoveEntity.
    //     A refresh instantiates them again at a new image instead of moving them (MMR-247 X, EntityImageMode.Recreate: a prefab's
    //     occlusion body gives the entity an occluder record that Scene.CheckResources leaves stale, and moving the entity writes through it).
    //     Each is instantiated at its frame (no later SetGlobalFrame), and PeriodicMapCameraView refreshes only while the map's scene view is
    //     enabled, so every creation follows the frame's CheckResources.
    //   * MMR-291: rivers, pools and (by policy) fills as overlay meshes (OverlayMeshRuntime.InstallRivers; their decals are left out of the
    //     installer, MapDressingInstaller.SkipWater), refreshed and released with the dressing.
    internal static class MapDressingRuntime
    {
        public const string DecalSet = "editor_set";
        public static readonly Dictionary<string, MapDressingPlan> Plans = new Dictionary<string, MapDressingPlan>(StringComparer.Ordinal);
        public static MapDressingInstaller Current { get; private set; }
        private static string hovered;
        public static readonly List<string> Errors = new List<string>();

        // The ruins of the world whose plan waits or is drawn (the encyclopedia page lists them).
        public static IList<RuinRecord> Ruins()
        {
            if (Current != null) return Current.Plan.Ruins;
            foreach (var plan in Plans.Values) return plan.Ruins;
            return new RuinRecord[0];
        }
        public static MapDressingPlan CurrentPlan()
        {
            if (Current != null) return Current.Plan;
            foreach (var plan in Plans.Values) return plan;
            return null;
        }

        internal sealed class EngineTarget : IMapDressingTarget   // also MMR-260's magic ground decals (MagicGroundRuntime)
        {
            private readonly Scene scene;
            private readonly WaterPlanes planes;
            // MMR-247 (D): the materials, each judged by whether the campaign map's decal atlas holds its diffuse texture (World/DecalAtlas.cs).
            private readonly DecalAtlasChooser chooser = new DecalAtlasChooser();
            private readonly HashSet<Decal> fallbackDrawn = new HashSet<Decal>();   // the decals drawn with their fallback (its own box)
            // MMR-247: decals created per material drawn, how many drew their fallback, had their box raised over a water plane, or lay wholly
            // under one (drawn as a sliver over the plane, which paints nothing).
            public readonly SortedDictionary<string, int> Drawn = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public int Clipped, Submerged;
            public int Fallbacks { get { return chooser.Fallbacks; } }
            // A target with the scene alone (MMR-260's magic ground) keeps its boxes above the planes of the dressing drawn on the map (the
            // sea plane alone without one).
            public EngineTarget(Scene scene) : this(scene, WaterPlanes.Of(CurrentPlan())) { }
            public EngineTarget(Scene scene, WaterPlanes planes) { this.scene = scene; this.planes = planes ?? new WaterPlanes(null); }
            public Scene Scene { get { return scene; } }   // the scene everything this target drew is on
            public WaterPlanes Planes { get { return planes; } }
            // The rotation columns scaled by the half extents (the box [-1, 1]^3 projected along local z; a fallback's own box, RoadDecal.Box),
            // centred BoxOffset over the terrain under the decal, the box's bottom raised over the water planes its footprint shows (WaterPlanes.Box).
            private MatrixFrame DecalFrame(RoadDecal d, bool fallback, double x, double y, out bool clipped, out bool submerged)
            {
                double yaw, halfU, halfV;
                d.Box(fallback, out yaw, out halfU, out halfV);
                float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw), l = (float)halfU, w = (float)halfV;
                double z, half, depth = d.Kind != null ? Math.Max(d.HalfDepth, DecalAtlasRuntime.WaterMinHalfDepth) : d.HalfDepth;   // water stamps: roads.json water.minHalfDepth
                submerged = !planes.Box(d, x, y, Ground, out z, out half, depth);
                clipped = half < depth - 1e-9;
                var rot = new Mat3(new Vec3(c * l, s * l, 0), new Vec3(-s * w, c * w, 0), new Vec3(0, 0, (float)half));
                return new MatrixFrame(rot, new Vec3((float)x, (float)y, (float)z));
            }
            public object AddDecal(RoadDecal d, double x, double y)
            {
                // MMR-247: the decal's fallback draws while its material does not resolve; MMR-247 (D): or while its diffuse texture has no rect in
                // the decal atlas (acc5: the published module materials resolve and draw nothing). Nothing draws when neither can (null).
                bool fallback;
                var material = chooser.Choose(d, out fallback);
                if (material == null) return null;
                string name = fallback ? d.Fallback : d.Material; var atlas = fallback ? d.FallbackAtlas : d.Atlas; uint tint = fallback ? d.FallbackTint : d.Tint;
                var decal = Decal.CreateDecal(null);
                decal.SetMaterial(material);
                scene.AddDecalInstance(decal, DecalSet, true);
                decal.SetIsVisible(true);
                decal.SetVectorArgument(atlas[0], atlas[1], atlas[2], atlas[3]);
                if (tint != 0) decal.SetFactor1(tint);   // MMR-285: water decals tint their stamp (MMR-249: SetFactor1 works on vanilla stamps)
                bool clipped, submerged;
                decal.Frame = DecalFrame(d, fallback, x, y, out clipped, out submerged);
                if (fallback) fallbackDrawn.Add(decal);
                if (clipped) Clipped++;
                if (submerged) Submerged++;
                int n; Drawn.TryGetValue(name, out n); Drawn[name] = n + 1;
                return decal;
            }
            public void MoveDecal(object handle, RoadDecal d, double x, double y) { var decal = (Decal)handle; bool clipped, submerged; decal.Frame = DecalFrame(d, fallbackDrawn.Contains(decal), x, y, out clipped, out submerged); }
            public Dictionary<string, object> Record()
            {
                var r = new Dictionary<string, object> { ["materials"] = new SortedDictionary<string, int>(Drawn, StringComparer.Ordinal), ["fallbacks"] = Fallbacks, ["clippedOverWater"] = Clipped,
                    ["submerged"] = Submerged, ["lakePlanes"] = planes.Lakes.Count, ["clearance"] = WaterPlanes.Clearance };
                foreach (var kv in chooser.Record()) if (kv.Key != "fallbacks") r[kv.Key] = kv.Value;
                return r;
            }
            public void RemoveDecal(object handle) { var decal = (Decal)handle; fallbackDrawn.Remove(decal); scene.RemoveDecalInstance(decal, DecalSet); }
            private static MatrixFrame EntityFrame(double x, double y, double z, double yaw, double scale)
            {
                float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw), k = (float)scale;
                return new MatrixFrame(new Mat3(new Vec3(c * k, s * k, 0), new Vec3(-s * k, c * k, 0), new Vec3(0, 0, k)), new Vec3((float)x, (float)y, (float)z));
            }
            // MMR-247 X: instantiated at its frame (CreateFromPrefabWithInitialFrameAndRestOffset writes the frame, and an occluder record's,
            // while it creates the entity), so no frame change waits for a later scene tick, after which a Scene.CheckResources could have
            // left the record stale.
            public object AddEntity(string prefab, double x, double y, double z, double yaw, double scale)
            {
                var frame = EntityFrame(x, y, z, yaw, scale);
                return GameEntity.Instantiate(scene, prefab, frame, true);
            }
            public void MoveEntity(object handle, double x, double y, double z, double yaw, double scale) { var frame = EntityFrame(x, y, z, yaw, scale); ((GameEntity)handle).SetGlobalFrame(in frame); }
            public void RemoveEntity(object handle) { var entity = (GameEntity)handle; if (entity.Scene != null) entity.Scene.RemoveEntity(entity, 0); }
            public double Ground(double x, double y) { return scene.GetTerrainHeight(new Vec2((float)x, (float)y), true); }
        }

        private static double Image(double x) { return MNBL.ProceduralWorld.UI.PeriodicMapCameraView.Nearest((float)x); }

        // GeneratedMapScene.AfterLoad: draws the scene's plan, if the world has one. Never throws.
        internal static void Install(Scene mapScene, string sceneName, Dictionary<string, object> supportReport, Action<string> trace)
        {
            try
            {
                // An install still held is removed natively only when it is on this same live scene; one on another scene (an earlier map
                // whose Destroy did not run) went with that scene's ClearAll, so only its handles go.
                var held = Current == null ? null : Current.Target as EngineTarget;
                if (held != null && mapScene != null && held.Scene == mapScene) Release(trace); else ReleaseHandles(trace);
                Errors.Clear();
                MapDressingPlan plan;
                if (mapScene == null || sceneName == null || !Plans.TryGetValue(sceneName, out plan) || plan == null) { if (supportReport != null) supportReport["mapDressing"] = "none: the world has no map dressing plan"; return; }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var target = new EngineTarget(mapScene, WaterPlanes.Of(plan));
                // MMR-247: the record names the materials drawn (the water fallback while unpublished) and the boxes clipped over water.
                // MMR-291: the water decals the river overlay meshes replace are left out, then the meshes are drawn.
                var installer = new MapDressingInstaller(plan, target) { Describe = target.Record, SkipWater = OverlayMeshRuntime.RiverDecalsReplaced() };
                installer.Install(Image);
                Current = installer;
                OverlayMeshRuntime.InstallRivers(mapScene, plan, Image);
                var record = installer.Record();
                record["installMs"] = watch.Elapsed.TotalMilliseconds;
                record["riverMeshes"] = OverlayMeshRuntime.Record()["rivers"];
                if (supportReport != null) supportReport["mapDressing"] = record;
                trace("map_dressing_installed:decals " + plan.Decals.Count + ":bridges " + plan.Bridges.Count + ":ruins " + plan.Markers.Count + ":water " + plan.Water.Count + ":created " + installer.Created + ":errors " + installer.Errors.Count
                      + ":materials " + String.Join("/", target.Drawn.Select(kv => kv.Key + " " + kv.Value)) + ":fallbacks " + target.Fallbacks + ":clipped " + target.Clipped + ":submerged " + target.Submerged);
            }
            catch (Exception error)
            {
                if (Errors.Count < 20) Errors.Add("install: " + error);
                if (supportReport != null) supportReport["mapDressing"] = "failed: " + error.GetType().Name + ": " + error.Message;
                try { trace("map_dressing_failed:" + error.ToString().Replace("\r", "").Replace("\n", " | ")); } catch (Exception) { }
            }
        }

        // PeriodicMapCameraView, after a camera wrap or a long move: each object takes its image nearest the camera again.
        // RefreshTrace (the hang repro's progress line, MMR-247 X): what a refresh that changed something did, before the next engine tick.
        internal static Action<string> RefreshTrace;
        internal static void RefreshImages()
        {
            try
            {
                var installer = Current;
                if (installer == null) return;
                int moved = installer.Moved, recreated = installer.Recreated, frozen = installer.Frozen, entities = installer.MovedEntities;
                installer.Refresh(Image);
                var trace = RefreshTrace;
                if (trace != null && (installer.Moved != moved || installer.Recreated != recreated || installer.Frozen != frozen))
                    trace("dressingRefresh moved " + (installer.Moved - moved) + " (entities " + (installer.MovedEntities - entities) + ") recreated " + (installer.Recreated - recreated)
                          + " frozen " + (installer.Frozen - frozen) + " mode " + MapDressingInstaller.EntityImages + " errors " + installer.Errors.Count);
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("refresh: " + error.GetType().Name + ": " + error.Message); }
            OverlayMeshRuntime.RefreshRivers(Image);   // MMR-291
        }

        // A re-install on the same live scene: removes everything drawn (RemoveDecalInstance, RemoveEntity), each handle once.
        internal static void Release(Action<string> trace)
        {
            var installer = Current;
            Current = null;
            Hide();
            OverlayMeshRuntime.ReleaseRivers(true);   // MMR-291: on the live scene
            if (installer == null) return;
            try { installer.Remove(); if (trace != null) trace("map_dressing_removed:" + installer.Removed + ":errors " + installer.Errors.Count); }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("remove: " + error.GetType().Name + ": " + error.Message); }
        }

        // MMR-262: GeneratedMapScene.Destroy, and an install held on another scene. MapScreen.OnFinalize has run Scene.ClearAll by then
        // (GameStateManager notifies the screens before OnStateStackEmpty reaches Game.Destroy and Campaign.OnDestroy); it took every decal
        // and entity drawn and freed the scene's decal container, which RemoveDecalInstance reads unchecked (a native access violation). So
        // only the handles go: no engine call. The managed wrappers release their references when collected, as vanilla's do after ClearAll.
        internal static void ReleaseHandles(Action<string> trace)
        {
            var installer = Current;
            Current = null;
            Hide();
            OverlayMeshRuntime.ReleaseRivers(false);   // MMR-291: handles only, no engine call
            if (installer == null) return;
            int dropped = installer.Drop();
            try { if (trace != null) trace("map_dressing_released:handles " + dropped + ":engine calls 0"); } catch (Exception) { }
        }

        // The application tick: a ruin marker's name while the cursor's ground point is near it on the campaign map.
        internal static void Tick()
        {
            if (Current == null) return;
            try
            {
                var screen = ScreenManager.TopScreen;
                var layer = CampaignMapScreen.Is(screen) ? screen.FindLayer<SceneLayer>() : null;   // MapScreen or War Sails' NavalMapScreen (MMR-247)
                Vec3 ground, normal;
                if (layer == null || !layer.ProjectedMousePositionOnGround(out ground, out normal, false, BodyFlags.None, false)) { Hide(); return; }
                string name = null; double best = MapDressing.MarkerHoverRadius;
                foreach (var kv in Current.DrawnMarkers())
                {
                    double dx = kv.Value - ground.x, dy = kv.Key.Y - ground.y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) { best = d; name = kv.Key.Ruin.Name; }
                }
                if (name == null) { Hide(); return; }
                if (name == hovered) return;
                hovered = name;
                MBInformationManager.ShowHint(name);
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("hover: " + error.GetType().Name + ": " + error.Message); Hide(); }
        }

        private static void Hide()
        {
            if (hovered == null) return;
            hovered = null;
            try { MBInformationManager.HideInformations(); } catch (Exception) { }
        }
    }
}
