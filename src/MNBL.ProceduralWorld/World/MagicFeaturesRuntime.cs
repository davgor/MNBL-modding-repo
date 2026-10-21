using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.World
{
    // MMR-289: the magic features on the campaign map (docs/magic-features.md; MagicFeatures is the pure model). A layer of the magic ground
    // overlay (MagicGroundRuntime.Layers), so it follows the overlay's lifetime on the engine thread:
    //   * Install (GeneratedMapScene.AfterLoad, after the magic ground decals, so after the map dressing): the plan of the loaded map's vein graph
    //     (generated zones and the live mutation state MMR-261 restored, so a Continue re-creates a cast's features from its stored document),
    //     kept clear of the world's settlement points (MMR-261's mutation context, WorldMutationBehavior.Contexts, of this overlay's source: the
    //     travel graph's settlements, and since MMR-247 the hideouts and the retirement retreat the campaign start places) and of
    //     the drawn map dressing's roads, bridges, ruin markers, water decals and lake planes; what the campaign time shows is drawn. A world with
    //     no settlement table or no drawn map dressing draws none.
    //   * Changed (a live change at MMR-261's safe point, or a reveal step of MMR-288's campaign tick): a new graph is planned again; the
    //     installer adds and removes only the features whose key changed, so a cast's features appear as its veins grow.
    //   * RefreshImages (a camera wrap): every entity takes its image nearest the camera again.
    //   * Release(true) removes every entity natively (a new Begin on the same live scene); Release(false) (GeneratedMapScene.Destroy, after
    //     MapScreen.OnFinalize's Scene.ClearAll took them, MMR-262) lets go of the handles with no engine call; the second after the first is a no-op.
    // Entities: GameEntity.CreateEmpty roots with one meta mesh copy each (AddMultiMesh(MetaMesh.GetCopy)), a multiply factor (SetFactorColor) and
    // a global frame; a source feature's effects are one more root with a point light and vanilla particle systems. Never inside a baked
    // settlement; no prefab (the candidate prefabs carry physics bodies). Nothing here throws into an engine callback.
    internal sealed class MagicFeaturesRuntime : IMagicVeinLayer
    {
        public static readonly MagicFeaturesRuntime Layer = new MagicFeaturesRuntime();
        public static readonly List<string> Errors = new List<string>();
        // The last install's record (status, plan counts, installed counts, timing), for the support report and the in-game checks.
        public static Dictionary<string, object> Record { get; private set; }
        public static MagicFeatureInstaller Current { get { return Layer.installer; } }

        private RoadsPolicy.FeaturesPolicy policy;
        private MagicFeatureInstaller installer;
        private MagicFeaturePlan plan;
        private MagicVeinGraph graph;
        private MagicFeatureSites sites;
        private Func<double, double> image;
        private double nextReveal = Double.PositiveInfinity;

        public void Install(Scene scene, MagicVeinGraph veins, double hours, Func<double, double> imageOf)
        {
            Release(false);
            Errors.Clear();
            var record = new Dictionary<string, object> { ["status"] = "begun" };
            Record = record;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (scene == null || veins == null) { record["status"] = "none: no map or vein graph"; return; }
                var context = WorldMutationBehavior.Contexts.Values.FirstOrDefault(c => c != null && ReferenceEquals(c.Source, veins.Source));
                if (context == null || context.Sites == null) { record["status"] = "none: the world has no settlement table (no mutation context for this overlay), so no feature is placed"; return; }
                policy = RoadsPolicy.Installed().Features;
                // The roads, bridges, ruin markers, water decals and lake planes come from the drawn map dressing; without it no clearance from
                // them holds, so nothing is placed (as with no settlement table).
                var dressing = MapDressingRuntime.Current == null ? null : MapDressingRuntime.Current.Plan;
                if (dressing == null) { record["status"] = "none: no map dressing plan (its install failed or the world has none), so no feature is placed"; return; }
                // MMR-247 (acc5 c30): the hideouts and the retreat too (11 features stood within 5 units of a hideout).
                sites = MagicFeatureSites.From(context.Sites.Point.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).Concat(context.Sites.Placed), dressing);
                record["settlements"] = sites.Settlements.Count; record["roads"] = sites.Roads.Count; record["bridges"] = sites.Bridges.Count; record["ruins"] = sites.Ruins.Count; record["waterDecals"] = sites.Water.Count;
                image = imageOf;
                installer = new MagicFeatureInstaller(new EngineTarget(scene));
                Draw(veins, hours);
                record["status"] = "installed"; record["installMs"] = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception error) { Fail(record, "install", error); }
        }

        public void Changed(MagicVeinGraph veins, double hours)
        {
            if (installer == null || veins == null) return;
            if (ReferenceEquals(veins, graph) && hours < nextReveal) return;
            try { Draw(veins, hours); }
            catch (Exception error) { Fail(Record, "change", error); }
        }

        private void Draw(MagicVeinGraph veins, double hours)
        {
            if (!ReferenceEquals(veins, graph)) { plan = MagicFeatures.Plan(veins, policy, sites); graph = veins; }
            installer.Sync(plan.Revealed(hours), image);
            nextReveal = plan.NextReveal(hours);
            if (Record != null) { Record["features"] = installer.Record(); Record["hours"] = hours; }
        }

        public void RefreshImages(Func<double, double> imageOf)
        {
            image = imageOf;
            try { if (installer != null) installer.Refresh(imageOf); }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("refresh: " + error.GetType().Name + ": " + error.Message); }
        }

        public void Release(bool native)
        {
            var drawn = installer;
            installer = null;
            try
            {
                if (drawn != null)
                {
                    if (native) drawn.Remove(); else drawn.Drop();
                    if (Record != null) Record["released"] = new Dictionary<string, object> { ["native"] = native, ["removed"] = drawn.Removed, ["dropped"] = drawn.Dropped };
                }
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("release: " + error.GetType().Name + ": " + error.Message); }
            plan = null; graph = null; sites = null; image = null; nextReveal = Double.PositiveInfinity;
        }

        private static void Fail(Dictionary<string, object> record, string step, Exception error)
        {
            if (Errors.Count < 20) Errors.Add(step + ": " + error);
            if (record != null) record["error"] = step + ": " + error.GetType().Name + ": " + error.Message;
        }

        // The engine adapter: every entity a scene root.
        internal sealed class EngineTarget : IMagicFeatureTarget
        {
            private readonly Scene scene;
            public EngineTarget(Scene scene) { this.scene = scene; }
            private double Ground(double x, double y) { return scene.GetTerrainHeight(new Vec2((float)x, (float)y), true); }
            private MatrixFrame Frame(FeaturePart p, double x, double y)
            {
                var b = p.Basis;
                var rot = new Mat3(new Vec3((float)b[0], (float)b[1], (float)b[2]), new Vec3((float)b[3], (float)b[4], (float)b[5]), new Vec3((float)b[6], (float)b[7], (float)b[8]));
                return new MatrixFrame(rot, new Vec3((float)x, (float)y, (float)(Ground(x, y) + p.Lift)));
            }
            public object AddPart(FeaturePart p, double x, double y)
            {
                var mesh = MetaMesh.GetCopy(p.Mesh, false, true);
                if (mesh == null) return null;
                var entity = GameEntity.CreateEmpty(scene, false, true, true);
                entity.Name = "mnbl_magic_feature";
                entity.AddMultiMesh(mesh, true);
                if (p.Tint != 0xFFFFFFFFu) entity.SetFactorColor(p.Tint);
                var frame = Frame(p, x, y);
                entity.SetGlobalFrame(in frame);
                entity.SetReadyToRender(true);
                return entity;
            }
            public void MovePart(object handle, FeaturePart p, double x, double y) { var frame = Frame(p, x, y); ((GameEntity)handle).SetGlobalFrame(in frame); }
            public object AddEffect(MagicFeature f, double x, double y)
            {
                var entity = GameEntity.CreateEmpty(scene, false, true, true);
                entity.Name = "mnbl_magic_feature_fx_" + f.Look;
                var frame = MatrixFrame.Identity;
                frame.origin = new Vec3((float)x, (float)y, (float)(Ground(x, y) + f.EffectLift));
                entity.SetGlobalFrame(in frame);
                if (f.Light != null)
                {
                    var light = Light.CreatePointLight((float)f.Light.Radius);
                    light.LightColor = new Vec3((float)f.Light.R, (float)f.Light.G, (float)f.Light.B);
                    light.Intensity = (float)f.Light.Intensity;
                    if (f.Light.Flicker) light.SetLightFlicker(0.6f, 1.0f);   // map_icon_fx_village_plunder's flicker (Native/Prefabs/map_icon_parts.xml)
                    entity.AddLight(light);
                }
                foreach (var name in f.Particles) entity.AddParticleSystemComponent(name);   // SandBox.View SettlementVisual's campaign-map call
                return entity;
            }
            public void MoveEffect(object handle, MagicFeature f, double x, double y)
            {
                var entity = (GameEntity)handle; var frame = entity.GetGlobalFrame();
                frame.origin = new Vec3((float)x, (float)y, (float)(Ground(x, y) + f.EffectLift));
                entity.SetGlobalFrame(in frame);
            }
            public void Remove(object handle) { var entity = (GameEntity)handle; if (entity.Scene != null) entity.Scene.RemoveEntity(entity, 0); }
        }
    }
}
