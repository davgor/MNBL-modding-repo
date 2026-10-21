using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Missions;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.World
{
    // MMR-260: the magic ground overlay on the campaign map (docs/magic-ground.md; MagicGround is the pure model). The world's overlay (built
    // with its campaign by WorldReconstruction at activation and at load) waits in Overlays under the scene name.
    //   * GeneratedMapScene.Load (after the scene's read, so before the campaign's OnGameLoaded runs MobileParty.PreAfterLoad and
    //     ComputePathAfterLoad): the stored live cells MMR-261 restored (Restore, from its SyncData, which LoadBehaviorData reads in the load's
    //     first stage) become the snapshot; the faces of retyped cells get their type (GeneratedMapScene.FaceTypes, read by its
    //     GetFaceTerrainType); the battle map's patch is registered (GeneratedBattleTerrain.CellPatches, before AfterLoad installs the map).
    //   * GeneratedMapScene.AfterLoad: the decals (MapDressingRuntime's engine target: MMR-249's entity-less scene decals of MMR-259's
    //     published mnbl_magic_<school> materials along MMR-288's vein graph; a material that does not resolve draws nothing), the colour-grade
    //     cells (MMR-256's managed atmosphere tick, never a MapColorGradeManager) and the node effects (separate root entities: a point light
    //     and a vanilla particle).
    //   * MMR-288 reveal: a live cast's veins appear outward over roads.json magic revealHours of campaign time. WorldMutationBehavior's
    //     campaign tick (engine thread, never during a load) calls Advance(hours); a step redraws only the newly revealed stamps. A load
    //     draws what its campaign time shows, so a Continue after the time has passed shows them fully grown.
    //   * Layers (MMR-289 features, MMR-290 magic water): IMagicVeinLayer hooks, called with the vein graph at install, at each change or
    //     reveal step, at a seam refresh and at release.
    //   * During play (MMR-261, engine thread, a safe point): Set (a cast's whole live state) or Apply swaps the snapshot, rebuilds the face types, re-chooses the changed battle
    //     cells in every strip image, redraws the changed decals and grade cells and adds the nodes; each change records the land shares by
    //     type before and after (RT-20).
    //   * GeneratedMapScene.Destroy (after MapScreen.OnFinalize's Scene.ClearAll took every decal and entity drawn, MMR-262): only the handles
    //     go, with no engine call (ReleaseHandles); the patch, the face types and the grade cells go too. Native removal (Release) is for a
    //     new Begin on the same live scene. Nothing here throws into an engine callback.
    internal static class MagicGroundRuntime
    {
        public static readonly Dictionary<string, WorldSurfaceOverlay> Overlays = new Dictionary<string, WorldSurfaceOverlay>(StringComparer.Ordinal);
        private sealed class Stored { public List<MutationCell> Cells; public List<MagicNode> Nodes; public bool Disabled; }
        private static readonly Dictionary<string, Stored> stored = new Dictionary<string, Stored>(StringComparer.Ordinal);
        private sealed class NodeEntity { public MagicNode Node; public GameEntity Entity; public double X; }

        private static WorldSurfaceOverlay overlay;
        private static GeneratedMapScene map;
        private static Scene scene;
        private static string sceneName;
        private static RoadsPolicy.MagicPolicy policy;
        private static MagicBattleCells battle;
        private static Action<int, int, CellSamples> patch;
        private static MagicGroundInstaller installer;
        private static MagicGroundPlan full;                     // every stamp of the current snapshot, with its reveal time
        private static MagicRevealClock clock = new MagicRevealClock();
        // MMR-289/290: further layers drawn from the vein graph, in install order: the magic features (World/MagicFeaturesRuntime.cs), then the
        // magic water (World/MagicWaterRuntime.cs, which declares IMagicWaterVeins), then MMR-291's overlay meshes (World/OverlayMeshRuntime.cs:
        // the veins and the magic water as meshes; while it draws the veins this overlay draws no land decals). Registered once (module start);
        // never throw out of them.
        public static readonly List<IMagicVeinLayer> Layers = new List<IMagicVeinLayer> { MagicFeaturesRuntime.Layer, MagicWaterRuntime.Instance, OverlayMeshRuntime.Instance };
        private static readonly List<NodeEntity> nodes = new List<NodeEntity>();
        private static Dictionary<int, byte> grades = new Dictionary<int, byte>();
        private static Vec2 terrainSize;
        private static Action<string> trace;
        private static Dictionary<string, object> record;
        public static readonly List<string> Errors = new List<string>();
        public static MagicChange LastChange { get; private set; }

        public static int PendingCount { get { return stored.Count; } }
        // Decals, node effects and (MMR-291) the vein stamps the overlay meshes draw.
        public static int Drawn { get { return (installer == null ? 0 : installer.Live) + nodes.Count + OverlayMeshRuntime.Pieces(OverlayLayer.Veins); } }
        public static WorldSurfaceOverlay Current { get { return overlay; } }
        // MMR-288: the loaded map's vein graph (fully grown; VeinNode.RevealAt against Hours says what shows) and the reveal clock.
        public static MagicVeinGraph Veins { get { return full == null ? null : full.Graph; } }
        public static double Hours { get { return clock.Hours; } }
        // The fully grown plan of the loaded map (its Dropped names the graph stamps of components too small to draw, water ones included).
        public static MagicGroundPlan Plan { get { return full; } }
        // The live snapshot of the loaded map (null without one).
        public static MagicSnapshot Snapshot { get { return overlay == null ? null : overlay.Current; } }
        // MMR-247 (D): the decals' record (the installer's, with its engine target's: materials drawn, fallbacks, the decal atlas); null without one.
        public static Dictionary<string, object> DecalsRecord { get { return installer == null ? null : installer.Record(); } }

        // ---- MMR-261's API ----
        // A load's replay of the stored live cells and nodes for the world of sceneName (its SyncData, read before the map loads). Replaces any
        // earlier replay. When that world's map is already up, the cells apply now, as Apply would.
        public static void Restore(string worldScene, IEnumerable<MutationCell> cells, IEnumerable<MagicNode> nodeList)
        {
            if (String.IsNullOrEmpty(worldScene)) throw new ArgumentException("A scene name is required.");
            stored[worldScene] = new Stored { Cells = (cells ?? new MutationCell[0]).ToList(), Nodes = (nodeList ?? new MagicNode[0]).ToList() };
            if (overlay != null && sceneName == worldScene) Changed(overlay.Replace(stored[worldScene].Cells, stored[worldScene].Nodes), "restore");
        }
        // RT-37: a digest mismatch on load. The live cells are dropped and casts refused; generated magic ground stays drawn.
        public static void RestoreDisabled(string worldScene)
        {
            if (String.IsNullOrEmpty(worldScene)) throw new ArgumentException("A scene name is required.");
            stored[worldScene] = new Stored { Cells = new List<MutationCell>(), Nodes = new List<MagicNode>(), Disabled = true };
            if (overlay != null && sceneName == worldScene) Changed(overlay.Disable(), "disable");
        }
        // A cast's changed cells and its node, during play on the engine thread at a safe point (the map loaded). Throws when no generated map is
        // loaded, the world is magic-off or mutations are disabled; the caller refuses the cast then.
        public static MagicChange Apply(IEnumerable<MutationCell> cells, IEnumerable<MagicNode> nodeList)
        {
            if (overlay == null || map == null) throw new InvalidOperationException("No generated campaign map with a magic overlay is loaded.");
            return Changed(overlay.Apply(cells, nodeList), "apply");
        }
        // MMR-261: a cast's whole live state during play, on the engine thread at a safe point. fwg apply's delta is always against the freshly
        // generated world, so it replaces the last state (cells that went back to their generated variant drop out). Throws as Apply does,
        // and when mutations are disabled; the stored replay follows, so a later map load of this world draws the same.
        public static MagicChange Set(IEnumerable<MutationCell> cells, IEnumerable<MagicNode> nodeList)
        {
            if (overlay == null || map == null) throw new InvalidOperationException("No generated campaign map with a magic overlay is loaded.");
            if (overlay.Current.MutationsDisabled) throw new InvalidOperationException("Live mutations are disabled for this world (a digest mismatch, RT-37); no cast applies.");
            var cellList = (cells ?? new MutationCell[0]).ToList(); var nodeCopy = (nodeList ?? new MagicNode[0]).ToList();
            var change = Changed(overlay.Replace(cellList, nodeCopy), "apply");
            stored[sceneName] = new Stored { Cells = cellList, Nodes = nodeCopy };
            return change;
        }
        public static MagicChange DisableMutations(string reason)
        {
            if (overlay == null) throw new InvalidOperationException("No generated campaign map with a magic overlay is loaded.");
            var change = Changed(overlay.Disable(), "disable");
            if (record != null) record["mutationsDisabled"] = reason;
            return change;
        }

        // ---- the map's lifetime ----
        // GeneratedMapScene.Load, after the read. Never throws.
        internal static void Begin(GeneratedMapScene mapScene, string worldScene, Scene loaded, Vec2 terrain, PeriodicWorldCoordinates coordinates, Dictionary<string, object> supportReport, Action<string> log)
        {
            try
            {
                // What an earlier Begin drew is removed natively only on this same live scene; on another scene (a map whose Destroy did not
                // run) it went with that scene's ClearAll, so only its handles go.
                if (scene != null && scene == loaded) Release(null); else ReleaseHandles(null);
                Errors.Clear(); LastChange = null;
                WorldSurfaceOverlay world;
                if (worldScene == null || !Overlays.TryGetValue(worldScene, out world) || world == null) { if (supportReport != null) supportReport["magicGround"] = "none: the world has no magic overlay"; return; }
                overlay = world; map = mapScene; scene = loaded; sceneName = worldScene; trace = log; terrainSize = terrain;
                policy = RoadsPolicy.Installed().Magic;
                record = new Dictionary<string, object> { ["status"] = "begun", ["enabled"] = world.Source.Enabled };
                if (supportReport != null) supportReport["magicGround"] = record;
                Stored s;
                if (stored.TryGetValue(worldScene, out s)) { if (s.Disabled) world.Disable(); else world.Replace(s.Cells, s.Nodes); }
                // The battle map's cells (its own cell size over the terrain) and the patch it reads every cell's class from.
                int width, height, originCell, periodCells; float cellX, cellY;
                GeneratedBattleTerrain.Cells(terrain, out width, out height, out cellX, out cellY);
                GeneratedBattleTerrain.Period(coordinates, width, cellX, out originCell, out periodCells);
                battle = new MagicBattleCells { Width = width, Height = height, OriginCell = originCell, PeriodCells = periodCells, CellX = cellX, CellY = cellY };
                var faceClass = MissionScenes.Installed().FaceClass;
                patch = (lx, y, samples) => { var snap = overlay == null ? null : overlay.Current; if (snap == null) return; string cls = battle.ClassAt(snap, faceClass, lx, y); if (cls != null) samples.ClassOverride = cls; };
                List<Action<int, int, CellSamples>> patches;
                if (!GeneratedBattleTerrain.CellPatches.TryGetValue(worldScene, out patches)) GeneratedBattleTerrain.CellPatches[worldScene] = patches = new List<Action<int, int, CellSamples>>();
                patches.Add(patch);
                var snapshot = world.Current;
                SetFaces(snapshot);
                record["liveCells"] = snapshot.LiveCount; record["retypedCells"] = snapshot.RetypedCells.Length; record["digest"] = snapshot.Digest;
                record["landShares"] = WorldSurfaceOverlay.LandShares(snapshot);
                record["status"] = "faces ready";
                if (log != null) log("magic_ground_begun:enabled " + world.Source.Enabled + ":live " + snapshot.LiveCount + ":retyped " + snapshot.RetypedCells.Length + ":faces " + (map.FaceTypes == null ? 0 : map.FaceTypes.Count(t => t != 0)));
            }
            catch (Exception error) { Fail("begin", error, supportReport); }
        }

        // GeneratedMapScene.AfterLoad, after the battle map and the map dressing. Never throws.
        internal static void Install(Dictionary<string, object> supportReport)
        {
            if (overlay == null || scene == null) return;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var target = new MapDressingRuntime.EngineTarget(scene);
                installer = new MagicGroundInstaller(target) { Describe = target.Record };   // MMR-247 (D): the atlas judgement and fallbacks in the record
                clock = new MagicRevealClock();
                double now = Clock();
                Draw(overlay.Current, now);
                // No campaign clock at AfterLoad: the live veins show grown until the first campaign tick re-hides what its time does not show.
                if (clock.Unknown && MagicRevealClock.Timed(full) > 0 && trace != null) trace("magic_ground_warning:no campaign clock at install:" + MagicRevealClock.Timed(full) + " live stamps shown grown until the first tick");
                foreach (var layer in Layers) Guard("layer install", () => layer.Install(scene, full.Graph, clock.Hours, Image));
                record["status"] = "installed"; record["installMs"] = watch.Elapsed.TotalMilliseconds;
                if (trace != null) trace("magic_ground_installed:decals " + installer.Live + ":notDrawn " + installer.NotDrawn + ":missing " + String.Join("/", installer.MissingMaterials) + ":grades " + grades.Count + ":nodes " + nodes.Count + ":errors " + installer.Errors.Count);
            }
            catch (Exception error) { Fail("install", error, supportReport); }
        }

        // The decals, grade cells and nodes of a snapshot (the installer redraws only the stamps that changed); now: the install's campaign
        // time, NaN for a live change (the clock's own time).
        private static void Draw(MagicSnapshot snapshot, double now)
        {
            full = MagicGround.Plan(snapshot, policy, Layers.OfType<IMagicWaterVeins>().Any(l => l.DrawsWaterStamps));
            var plan = Double.IsNaN(now) ? clock.Restart(full) : clock.Start(full, now);
            installer.Sync(Decals(plan), Image);
            record["decals"] = installer.Record();
            record["veinsAsMeshes"] = OverlayMeshRuntime.DrawsVeins;   // MMR-291: the land stamps drawn by the overlay meshes, not as decals
            int dressing = MapDressingRuntime.Current == null ? 0 : MapDressingRuntime.Current.Live;
            record["sceneDecals"] = new Dictionary<string, object> { ["magic"] = installer.Live, ["dressing"] = dressing, ["total"] = installer.Live + dressing, ["budgetInView"] = 5000 };
            // Colour grade: every grid cell over a retyped cell takes its type's grade; cells no longer retyped give back the world's grade.
            var next = MagicGrades.Cells(snapshot, terrainSize.X, terrainSize.Y, GeneratedMapAtmosphere.Dimension);
            foreach (var kv in grades) if (!next.ContainsKey(kv.Key)) GeneratedMapAtmosphere.ClearGradeCell(kv.Key);
            foreach (var kv in next) GeneratedMapAtmosphere.SetGradeCell(kv.Key, kv.Value);
            grades = next;
            record["gradeCells"] = grades.Count; record["gradeDriver"] = GeneratedMapAtmosphere.Active ? "GeneratedMapAtmosphere" : "inactive (no managed atmosphere tick on this scene)";
            // Nodes: separate root entities, never inside a baked settlement.
            var wanted = snapshot.Nodes;
            for (int i = nodes.Count - 1; i >= 0; i--) if (!wanted.Contains(nodes[i].Node)) { RemoveNode(nodes[i]); nodes.RemoveAt(i); }
            foreach (var n in wanted) if (nodes.All(x => !ReferenceEquals(x.Node, n))) AddNode(snapshot, n);
            record["nodes"] = nodes.Count;
        }

        private static MagicChange Changed(MagicChange change, string kind)
        {
            LastChange = change;
            try
            {
                if (map != null) SetFaces(change.After);
                if (battle != null && change.ChangedCells.Length > 0)
                {
                    var keys = battle.Keys(overlay.Source, change.ChangedCells);
                    int written = GeneratedBattleTerrain.Repatch(map, keys);
                    if (record != null) record["lastBattleRepatch"] = new Dictionary<string, object> { ["cells"] = keys.Count, ["imagesWritten"] = written };
                }
                if (installer != null) { Draw(change.After, Double.NaN); foreach (var layer in Layers) Guard("layer change", () => layer.Changed(full.Graph, clock.Hours)); }
                if (record != null) { record["lastChange"] = change.Record(); record["lastChange.kind"] = kind; }
                if (trace != null) trace("magic_ground_" + kind + ":version " + change.After.Version + ":live " + change.After.LiveCount + ":changed cells " + change.ChangedCells.Length + ":samples " + change.ChangedSamples.Length);
            }
            catch (Exception error) { Fail(kind, error, null); }
            return change;
        }

        // MMR-288: the campaign tick (WorldMutationBehavior, engine thread, outside loads): live veins revealed by now are drawn. Cheap when
        // nothing is due (one comparison).
        internal static void Advance(double now)
        {
            if (installer == null || full == null) return;
            var plan = clock.Tick(full, now);
            if (plan == null) return;
            try
            {
                installer.Sync(Decals(plan), Image);
                if (record != null) { record["decals"] = installer.Record(); record["revealHours"] = now; }
                foreach (var layer in Layers) Guard("layer reveal", () => layer.Changed(full.Graph, now));
                if (trace != null) trace("magic_ground_revealed:hours " + now.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ":decals " + installer.Live + ":hidden " + plan.Hidden);
            }
            catch (Exception error) { Fail("reveal", error, null); }
        }

        // MMR-291: the land decals to draw: none while the overlay meshes draw the veins (roads.json overlayMeshes.veins mode mesh and a material
        // that resolves), else the plan's.
        private static MagicGroundPlan Decals(MagicGroundPlan plan) { return OverlayMeshRuntime.DrawsVeins ? plan.WithoutStamps() : plan; }

        // The campaign time in hours (+infinity with no campaign clock yet: every vein shows grown until the first tick, MagicRevealClock).
        private static double Clock()
        {
            try { return Campaign.Current == null ? Double.PositiveInfinity : CampaignTime.Now.ToHours; }
            catch (Exception) { return Double.PositiveInfinity; }
        }

        private static void Guard(string step, Action action)
        {
            try { action(); }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add(step + ": " + error.GetType().Name + ": " + error.Message); }
        }

        // The faces of the loaded scene's navmesh (every strip image) that a retyped cell retypes, swapped whole into the map scene.
        private static void SetFaces(MagicSnapshot snapshot)
        {
            if (map == null || scene == null) return;
            var types = MagicFaces.Build(snapshot, snapshot.RetypedCells.Length == 0 ? 0 : scene.GetNavMeshFaceCount(), i =>
            {
                var face = scene.GetNavMeshPathFaceRecord(i);
                var centre = Vec3.Zero;
                scene.GetNavMeshCenterPosition(i, ref centre);
                return new FaceSample(face.FaceIndex, centre.x, centre.y, face.FaceGroupIndex);
            });
            map.SetFaceTypes(types);
            if (record != null) record["retypedFaces"] = types == null ? 0 : types.Count(t => t != 0);
        }

        private static void AddNode(MagicSnapshot snapshot, MagicNode n)
        {
            try
            {
                var entity = GameEntity.CreateEmpty(scene, false, true, true);
                entity.Name = "mnbl_magic_node_" + n.School;
                double x = Image(n.X);
                var frame = MatrixFrame.Identity;
                frame.origin = new Vec3((float)x, (float)n.Y, scene.GetTerrainHeight(new Vec2((float)x, (float)n.Y), true) + (float)policy.NodeHeight);
                entity.SetGlobalFrame(in frame);
                int sample = snapshot.Source.SampleAt(n.X, n.Y), variant = snapshot.VariantAt(sample);
                uint colour = variant >= 0 && variant < snapshot.Source.Catalogue.Length ? snapshot.Source.Catalogue[variant].Colour : 0xFFFFFFFFu;
                var light = Light.CreatePointLight((float)policy.NodeLightRadius);
                light.LightColor = new Vec3(((colour >> 16) & 255) / 255f, ((colour >> 8) & 255) / 255f, (colour & 255) / 255f);
                light.Intensity = (float)policy.NodeLightIntensity;
                entity.AddLight(light);
                var local = MatrixFrame.Identity;
                ParticleSystem.CreateParticleSystemAttachedToEntity(policy.Particle(n.School), entity, ref local);
                nodes.Add(new NodeEntity { Node = n, Entity = entity, X = x });
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("node " + n.School + ": " + error.GetType().Name + ": " + error.Message); }
        }
        private static void RemoveNode(NodeEntity n)
        {
            try { if (n.Entity != null && n.Entity.Scene != null) n.Entity.Scene.RemoveEntity(n.Entity, 0); }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("remove node: " + error.GetType().Name + ": " + error.Message); }
        }

        private static double Image(double x) { return MNBL.ProceduralWorld.UI.PeriodicMapCameraView.Nearest((float)x); }

        // MMR-247 X: the hang repro's bisect switch (Map2AcceptanceDecalLab): false leaves the magic ground, its nodes, features and water where
        // they are on a refresh (counted in RefreshesSkipped). The product never changes it.
        internal static bool RefreshEnabled = true;
        internal static int RefreshesSkipped;

        // PeriodicMapCameraView, after a camera wrap or a long move: the decals and nodes take their image nearest the camera again.
        internal static void RefreshImages()
        {
            if (!RefreshEnabled) { RefreshesSkipped++; return; }
            try
            {
                if (installer != null) installer.Refresh(Image);
                foreach (var layer in Layers) Guard("layer refresh", () => layer.RefreshImages(Image));
                foreach (var n in nodes)
                {
                    double x = Image(n.Node.X);
                    if (Math.Abs(x - n.X) < 1e-6 || n.Entity == null) continue;
                    var frame = n.Entity.GetGlobalFrame(); frame.origin.x = (float)x; n.Entity.SetGlobalFrame(in frame); n.X = x;
                }
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("refresh: " + error.GetType().Name + ": " + error.Message); }
        }

        // A new Begin on the same live scene: removes the decals and node entities natively (each handle once), then lets go of the rest.
        internal static void Release(Action<string> log)
        {
            var drawn = installer;
            installer = null;
            try
            {
                if (drawn != null) drawn.Remove();
                foreach (var n in nodes) RemoveNode(n);
                foreach (var layer in Layers) Guard("layer release", () => layer.Release(true));
                if (log != null && (drawn != null || nodes.Count > 0)) log("magic_ground_removed:" + (drawn == null ? 0 : drawn.Removed) + ":nodes " + nodes.Count);
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("remove: " + error.GetType().Name + ": " + error.Message); }
            nodes.Clear();
            ReleaseHandles(null);
        }

        // MMR-262: GeneratedMapScene.Destroy, and a new Begin on another scene. MapScreen.OnFinalize has run Scene.ClearAll by then, which took
        // the decals and node entities and freed the scene's decal container (RemoveDecalInstance reads it unchecked: a native access
        // violation), so the handles go with no engine call. The grade cells, the battle patch and the face types (all managed) go too.
        internal static void ReleaseHandles(Action<string> log)
        {
            var drawn = installer;
            installer = null;
            int decals = drawn == null ? 0 : drawn.Drop(), dropped = nodes.Count;
            foreach (var layer in Layers) Guard("layer release handles", () => layer.Release(false));
            try
            {
                foreach (var kv in grades) GeneratedMapAtmosphere.ClearGradeCell(kv.Key);
                List<Action<int, int, CellSamples>> patches;
                if (sceneName != null && patch != null && GeneratedBattleTerrain.CellPatches.TryGetValue(sceneName, out patches)) { patches.Remove(patch); if (patches.Count == 0) GeneratedBattleTerrain.CellPatches.Remove(sceneName); }
                if (map != null) map.SetFaceTypes(null);
                if (log != null && (drawn != null || dropped > 0)) log("magic_ground_released:handles " + decals + ":nodes " + dropped + ":engine calls 0");
            }
            catch (Exception error) { if (Errors.Count < 20) Errors.Add("release: " + error.GetType().Name + ": " + error.Message); }
            nodes.Clear(); grades = new Dictionary<int, byte>();
            overlay = null; map = null; scene = null; sceneName = null; patch = null; battle = null; record = null; trace = null;
            full = null; clock = new MagicRevealClock();
        }

        // The game manager's teardown: the world's overlays and the stored live cells go with it.
        internal static void Forget() { Overlays.Clear(); stored.Clear(); LastChange = null; }

        private static void Fail(string step, Exception error, Dictionary<string, object> supportReport)
        {
            if (Errors.Count < 20) Errors.Add(step + ": " + error);
            if (record != null) record["error"] = step + ": " + error.GetType().Name + ": " + error.Message;
            else if (supportReport != null) supportReport["magicGround"] = "failed: " + step + ": " + error.GetType().Name + ": " + error.Message;
            try { if (trace != null) trace("magic_ground_failed:" + step + ":" + error.ToString().Replace("\r", "").Replace("\n", " | ")); } catch (Exception) { }
        }
    }

    // MMR-289/290: a layer drawn from the vein graph beside the magic ground decals (engine thread). Install after the decals at the map's
    // AfterLoad; Changed after a live change or a reveal step (hours: the campaign time shown; VeinNode.RevealAt <= hours is grown); RefreshImages
    // after a camera wrap (image: a canonical x to the image nearest the camera); Release(true) removes natively on the live scene,
    // Release(false) only lets go of handles after Scene.ClearAll (MMR-262); Release(false) also follows Release(true), so it must be a no-op then.
    internal interface IMagicVeinLayer
    {
        void Install(Scene scene, MagicVeinGraph graph, double hours, Func<double, double> image);
        void Changed(MagicVeinGraph graph, double hours);
        void RefreshImages(Func<double, double> image);
        void Release(bool native);
    }

    // MMR-288 gaps: a vein layer that draws the graph's water stamps (MMR-290's magic water) says so; the ground overlay then leaves the land
    // branches that hang off water nodes as they are (they continue the water veins). With no such layer, a land fragment rooted on a water
    // node is re-rooted at a shore burst or, below veins.minStamps, not drawn (MagicGround.Components). Read once per plan, before the
    // layers' Install, so it is a declaration (the layer's mode draws water stamps), not a material check.
    internal interface IMagicWaterVeins
    {
        bool DrawsWaterStamps { get; }
    }
}
