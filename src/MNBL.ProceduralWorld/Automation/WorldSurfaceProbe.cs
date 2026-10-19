using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-37: the runtime surface backend on real compiled worlds (world_surface; bundleA=<dir> bundleB=<dir>), no campaign:
    //   1. world A: Prepare (worker thread) -> Install -> Construct on an inspection screen (engine thread); GeneratedMapScene binds to it;
    //      every check below; then A is disposed (its ledger released latest first, its generation retired); queries through A's world
    //      and every guarded map scene member are refused; the objects are collected;
    //   2. a late callback: world C (bundle B) is prepared to completion, then world B begins, then C's completion is carried to the engine
    //      thread (EngineWork) and installs: refused, and nothing of C is installed or registered;
    //   3. world B is constructed and checked, including at A's landmark positions (where the worlds differ the native answers are B's);
    //      B is disposed;
    //   4. world A again (warm): its encoding is identical, so Install reuses the installed scene; it is constructed and checked,
    //      including at B's landmark positions; disposed. Nothing any world owned stays outstanding; staging jobs are deleted.
    // Checks (WorldSurfaceChecks): terrain bounds and the map scene's borders and size (taken from the native scene) against the model; at
    // every landmark (compiler settlements, chunk corners and centres, MMR-16 grid points, coast samples) native terrain, combined and
    // ground (land and sea masks) heights equal the collision reference within 0.001 (and the quantized model within 0.031), native
    // normals within 1 degree of the model's, Scene and SceneView vertical and oblique rays within 0.1 of the collision reference, and the
    // GeneratedMapScene (IMapScene) answers equal to the native ones; both sides of every interior chunk border (no height or normal tear);
    // periodic seam pairs; one native entity/terrain selection; the IMapScene interface map.
    internal static class WorldSurfaceProbe
    {
        private static readonly WorldDataBundle.HostProfile Host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });

        internal static IEnumerator Run(ProbeContext context)
        {
            context.Outputs["scope"] = "Runtime surface backend on compiled world bundles: cold and warm construction, queries, picking, chunk seams, disposal, world switch and late callbacks on inspection scenes. No campaign (MMR-45 activates; MMR-38 navmesh; MMR-40 entities; MMR-49 cold load).";
            context.Outputs["engineThread"] = EngineThread.Id;
            context.Outputs["probeThread"] = Thread.CurrentThread.ManagedThreadId;
            context.Outputs["sceneStoreBefore"] = Directory.GetDirectories(RuntimeWorldBackend.SceneStore()).Select(Path.GetFileName).Where(SceneNames.IsWorldScene).ToList();
            context.Outputs["interfaceMap"] = InterfaceMap();
            var bundleA = WorldDataBundle.Open(context.Str("bundleA", ""), Host);
            var bundleB = WorldDataBundle.Open(context.Str("bundleB", ""), Host);
            if (bundleA.WorldContentId == bundleB.WorldContentId) throw new ArgumentException("Two different compiled worlds are required.");
            var worlds = new List<object>(); context.Outputs["worlds"] = worlds;
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<WorldDataBundle, IDictionary<string, object>> travel = b => (IDictionary<string, object>)json.DeserializeObject(new System.Text.UTF8Encoding(false).GetString(b.ReadVerified("compiled/travel/travel-graph.json")));
            List<WorldSurface.Landmark> previousLandmarks = null; WorldSurface previousSurface = null;

            foreach (var step in new[] { "A", "late", "B", "A (warm)" })
            {
                if (step == "late")
                {
                    // 2. C finishes preparing; B begins; C's completion reaches the engine thread afterwards.
                    var late = new Dictionary<string, object>(); context.Outputs["lateCallback"] = late;
                    var tokenC = WorldGenerations.Begin("superseded " + bundleB.WorldContentId.Substring(0, 12));
                    PreparedSurface preparedC = null;
                    var prepareC = Task.Run(() => preparedC = RuntimeWorldBackend.Prepare(bundleB, tokenC, CancellationToken.None));
                    while (!prepareC.IsCompleted) yield return null;
                    if (prepareC.IsFaulted) throw new InvalidOperationException("Preparing world C failed.", prepareC.Exception.GetBaseException());
                    string folderC = RuntimeWorldBackend.SceneFolder(preparedC.SceneName);
                    late["sceneName"] = preparedC.SceneName; late["folderPreexisting"] = Directory.Exists(folderC);
                    string resolvedBefore; late["registeredBefore"] = Utilities.TryGetFullFilePathOfScene(preparedC.SceneName, out resolvedBefore) && !String.IsNullOrEmpty(resolvedBefore);
                    var tokenB = WorldGenerations.Begin("inspection " + bundleB.WorldContentId.Substring(0, 12) + " (B)");
                    currentB = tokenB;
                    late["generationC"] = tokenC.Id; late["generationB"] = tokenB.Id; late["preparedOnThread"] = preparedC.ThreadId;
                    var arrival = Task.Run(() => EngineWork.Run(() => RuntimeWorldBackend.Install(preparedC)));
                    while (!arrival.IsCompleted) yield return null;
                    var refusal = arrival.IsFaulted ? arrival.Exception.GetBaseException() : null;
                    late["installRefused"] = refusal is StaleWorldException; late["refusal"] = refusal == null ? "INSTALLED" : refusal.GetType().Name + ": " + refusal.Message;
                    late["deliveredThrough"] = "EngineWork (posted from a worker, run on the engine thread)";
                    late["folderInstalled"] = !(bool)late["folderPreexisting"] && Directory.Exists(folderC);
                    string resolvedAfter; late["registeredAfter"] = Utilities.TryGetFullFilePathOfScene(preparedC.SceneName, out resolvedAfter) && !String.IsNullOrEmpty(resolvedAfter);
                    late["jobDeleted"] = preparedC.JobDeleted && !Directory.Exists(preparedC.JobRoot);
                    try { RuntimeWorldBackend.Construct(preparedC.SceneName, tokenC, preparedC.Surface); late["constructRefused"] = false; }
                    catch (StaleWorldException error) { late["constructRefused"] = true; late["constructRefusal"] = error.Message; }
                    context.Checkpoint();
                    continue;
                }
                var bundle = step == "B" ? bundleB : bundleA;
                var token = step == "B" ? null : WorldGenerations.Begin("inspection " + bundle.WorldContentId.Substring(0, 12) + " (" + step + ")");
                if (token == null) token = CurrentToken(bundleB);
                PreparedSurface prepared = null;
                var prepare = Task.Run(() => prepared = RuntimeWorldBackend.Prepare(bundle, token, CancellationToken.None));
                while (!prepare.IsCompleted) yield return null;
                if (prepare.IsFaulted) throw new InvalidOperationException("Preparing world " + step + " failed.", prepare.Exception.GetBaseException());
                var record = new Dictionary<string, object> { ["label"] = step, ["worldContentId"] = bundle.WorldContentId, ["generation"] = token.Id, ["encodeSeconds"] = prepared.EncodeSeconds,
                    ["surface"] = SurfaceRecord(prepared.Surface), ["encoder"] = prepared.Encoder, ["preparedOnThread"] = prepared.ThreadId, ["installedAlready"] = prepared.InstalledAlready };
                worlds.Add(record); context.Checkpoint();
                record["install"] = RuntimeWorldBackend.Install(prepared);
                record["jobDeleted"] = prepared.JobDeleted && !Directory.Exists(prepared.JobRoot);
                context.Stage("world_" + step + "_installed");
                var screen = new InspectionScreen(context, step, prepared, travel(bundle),
                    previousSurface == null ? (KeyValuePair<WorldSurface, List<WorldSurface.Landmark>>?)null : new KeyValuePair<WorldSurface, List<WorldSurface.Landmark>>(previousSurface, previousLandmarks), record);
                ScreenManager.PushScreen(screen);
                while (!screen.Complete) yield return null;
                if (screen.Failure != null) throw new InvalidOperationException("World " + step + " inspection failed.", screen.Failure);
                var weakWorld = new WeakReference(screen.World); var weakMap = new WeakReference(screen.Map);
                var heldWorld = screen.World; var heldMap = screen.Map;
                previousLandmarks = screen.Landmarks; previousSurface = prepared.Surface;
                if (ScreenManager.TopScreen == screen) ScreenManager.PopScreen();
                if (!screen.Finalized) throw new InvalidOperationException("World " + step + "'s screen did not finalize.");
                Utilities.ClearOldResourcesAndObjects();
                record["afterDispose"] = heldWorld.Record();
                record["refusedAfterDispose"] = Refusals(heldWorld, heldMap);
                heldWorld = null; heldMap = null; screen = null;
                double released = context.Seconds; while (context.Seconds - released < 2) yield return null;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                record["collected"] = new Dictionary<string, object> { ["world"] = !weakWorld.IsAlive, ["mapScene"] = !weakMap.IsAlive };
                context.Checkpoint();
            }
            context.Outputs["sceneStoreAfter"] = Directory.GetDirectories(RuntimeWorldBackend.SceneStore()).Select(Path.GetFileName).Where(SceneNames.IsWorldScene).ToList();
            context.Outputs["jobsLeft"] = Directory.Exists(Path.Combine(SubModule.Store, "world-surfaces")) ? Directory.GetDirectories(Path.Combine(SubModule.Store, "world-surfaces")).Length : 0;
            context.Outputs["generations"] = WorldGenerations.Log;
            context.Outputs["currentGeneration"] = WorldGenerations.Current;
            context.Outputs["completed"] = true;
            context.Checkpoint();
        }

        // World B's token began in the late-callback step (it superseded C); it is still current.
        private static GenerationToken currentB;
        private static GenerationToken CurrentToken(WorldDataBundle b) { return currentB; }

        // Which method answers each IMapScene member on a GeneratedMapScene (the re-implemented ones must be GeneratedMapScene's).
        private static Dictionary<string, object> InterfaceMap()
        {
            var map = typeof(GeneratedMapScene).GetInterfaceMap(typeof(IMapScene));
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                string name = map.InterfaceMethods[i].Name;
                if (result.ContainsKey(name)) name += "#" + i;
                result[name] = map.TargetMethods[i].DeclaringType.Name;
            }
            var expected = new[] { "Load", "AfterLoad", "Destroy", "GetHeightAtPoint", "GetTerrainHeightAndNormal", "GetGroundNormal", "GetMapBorders", "GetTerrainSize",
                "GetMapPatchAtPosition", "GetFaceVertexZ", "GetPathBetweenAIFaces", "GetPathDistanceBetweenAIFaces", "AddNewEntityToMapScene" };
            return new Dictionary<string, object> { ["members"] = result, ["expectedGenerated"] = expected,
                ["missing"] = expected.Where(e => !result.Any(r => r.Key.Split('#')[0] == e && r.Value == "GeneratedMapScene")).ToList() };
        }

        private static Dictionary<string, object> SurfaceRecord(WorldSurface s)
        {
            return new Dictionary<string, object> { ["side"] = s.Side, ["extent"] = s.Extent, ["spacing"] = s.Spacing, ["nodes"] = WorldSurface.Nodes, ["cellsPerNode"] = s.Cells,
                ["heightmapLevel"] = s.HeightmapLevel, ["low"] = s.Low, ["high"] = s.High, ["step"] = s.Step, ["scale"] = s.Scale, ["inputsSha256"] = s.InputsSha256,
                ["seamMismatches"] = s.SeamMismatches(),
                ["chunks"] = s.Chunks.Select(c => new Dictionary<string, object> { ["x"] = c.X, ["y"] = c.Y, ["bounds"] = new[] { c.MinX, c.MinY, c.MaxX, c.MaxY }, ["minHeight"] = c.MinHeight, ["maxHeight"] = c.MaxHeight, ["layers"] = c.Mask }).ToList() };
        }

        // Every query through a disposed world, and every guarded member of its map scene binding, is refused before any native call.
        private static Dictionary<string, object> Refusals(RuntimeWorld world, GeneratedMapScene map)
        {
            var result = new Dictionary<string, object>();
            Func<Action, string> attempt = action => { try { action(); return "ANSWERED"; } catch (ObjectDisposedException) { return "ObjectDisposedException"; } catch (StaleWorldException) { return "StaleWorldException"; } catch (InvalidOperationException e) { return "InvalidOperationException: " + e.Message; } };
            result["worldHeight"] = attempt(() => world.Height(new Vec2(10, 10), true));
            result["worldNormal"] = attempt(() => world.Normal(new Vec2(10, 10)));
            result["worldGround"] = attempt(() => { float h; world.Ground(new Vec2(10, 10), BodyFlags.None, out h); });
            result["worldRay"] = attempt(() => { float d; Vec3 c; world.Ray(new Vec3(10, 10, 500), new Vec3(10, 10, -500), BodyFlags.None, out d, out c); });
            result["worldBounds"] = attempt(() => world.Bounds());
            result["worldScene"] = attempt(() => { var s = world.Scene; });
            if (map != null)
            {
                IMapScene wrapper = map;
                var p = new CampaignVec2(new Vec2(10, 10), true);
                result["mapScene"] = attempt(() => { var s = map.Scene; });
                result["mapTerrainSize"] = attempt(() => wrapper.GetTerrainSize());
                result["mapHeightAtPoint"] = attempt(() => { float h = 0; wrapper.GetHeightAtPoint(in p, ref h); });
                result["mapTerrainHeightAndNormal"] = attempt(() => { float h; Vec3 n; wrapper.GetTerrainHeightAndNormal(new Vec2(10, 10), out h, out n); });
                result["mapGroundNormal"] = attempt(() => wrapper.GetGroundNormal(new Vec2(10, 10)));
                result["mapBorders"] = attempt(() => { Vec2 a, b; float h; wrapper.GetMapBorders(out a, out b, out h); });
                result["mapPatch"] = attempt(() => wrapper.GetMapPatchAtPosition(in p));
                result["mapFaceVertexZ"] = attempt(() => wrapper.GetFaceVertexZ(PathFaceRecord.NullFaceRecord));
            }
            return result;
        }

        private sealed class InspectionScreen : ScreenBase
        {
            private readonly ProbeContext context;
            private readonly string label;
            private readonly PreparedSurface prepared;
            private readonly IDictionary<string, object> travelGraph;
            private readonly KeyValuePair<WorldSurface, List<WorldSurface.Landmark>>? previous;
            private readonly Dictionary<string, object> record;
            private SceneLayer layer;
            private Camera camera;
            private int readyFrames;
            private double start;
            internal RuntimeWorld World;
            internal GeneratedMapScene Map;
            internal List<WorldSurface.Landmark> Landmarks;
            internal bool Complete, Finalized;
            internal Exception Failure;

            internal InspectionScreen(ProbeContext context, string label, PreparedSurface prepared, IDictionary<string, object> travelGraph,
                KeyValuePair<WorldSurface, List<WorldSurface.Landmark>>? previous, Dictionary<string, object> record)
            { this.context = context; this.label = label; this.prepared = prepared; this.travelGraph = travelGraph; this.previous = previous; this.record = record; }

            protected override void OnInitialize()
            {
                base.OnInitialize();
                start = context.Seconds;
                try
                {
                    context.Stage("world_" + label + "_construct");
                    World = RuntimeWorldBackend.Construct(prepared.SceneName, prepared.Token, prepared.Surface);
                    var scene = World.Scene;
                    camera = Camera.CreateCamera();
                    var held = camera;
                    World.Ledger.Acquire("camera", "inspection screen", () => held.ReleaseCamera());
                    float e = (float)prepared.Surface.Extent;
                    camera.LookAt(new Vec3(e * .5f, e * -.35f, e * .85f), new Vec3(e * .5f, e * .5f, 0), Vec3.Up);
                    camera.SetFovVertical(.9f, Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, e * 5);
                    layer = new SceneLayer(); layer.SetScene(scene); layer.SetCamera(camera);
                    var heldLayer = layer;
                    World.Ledger.Acquire("scene layer", "inspection screen", () => heldLayer.ClearAll());
                    layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                    layer.SetSceneUsesShadows(true); layer.SetSceneUsesSkybox(true);
                    layer.SceneView.SetForceShaderCompilation(true); layer.SceneView.SetClearGbuffer(true);
                    AddLayer(layer); ScreenManager.TrySetFocus(layer);
                    Map = GeneratedMapScene.Inspect(World, s => context.Stage("world_" + label + ":" + s));
                    scene.Tick(0);
                    record["constructed"] = World.Record();
                    context.Checkpoint();
                }
                catch (Exception error) { Failure = error; record["failure"] = error.ToString(); Complete = true; }
            }

            protected override void OnFrameTick(float dt)
            {
                base.OnFrameTick(dt);
                if (Complete) return;
                try
                {
                    LoadingWindow.DisableGlobalLoadingWindow();
                    if (context.Seconds - start > 240) throw new TimeoutException("World " + label + " surface readiness timed out.");
                    var scene = World.Scene;
                    if (!layer.ReadyToRender() || Utilities.GetNumberOfShaderCompilationsInProgress() > 0 || !scene.IsLoadingFinished()) { readyFrames = 0; return; }
                    scene.Tick(0);
                    if (++readyFrames < 30) return;
                    context.Stage("world_" + label + "_checks");
                    Landmarks = World.Surface.Landmarks(travelGraph, 24, 12);
                    record["checks"] = WorldSurfaceChecks.Run(World, Map, layer.SceneView, Landmarks, previous, s => { context.Stage("world_" + label + ":" + s); }, prepared.Lakes);
                    context.Stage("world_" + label + ":water");
                    record["water"] = WorldSurfaceChecks.Water(World, prepared);   // MMR-39
                    context.Stage("world_" + label + ":settlements");
                    record["settlements"] = WorldSurfaceChecks.Settlements(World, prepared);   // MMR-40
                    string shot = Path.Combine(context.Directory, "world-surface-" + context.ProcessId + "-" + label + ".bmp");
                    Utilities.TakeScreenshot(shot); record["screenshot"] = shot;
                    Complete = true;
                    context.Checkpoint();
                }
                catch (Exception error) { Failure = error; record["failure"] = error.ToString(); Complete = true; context.Checkpoint(); }
            }

            protected override void OnFinalize()
            {
                base.OnFinalize();
                context.Stage("world_" + label + "_dispose");
                if (World != null) World.Dispose();
                layer = null; camera = null;
                Finalized = true;
                context.Checkpoint();
            }
        }
    }

    internal static class WorldSurfaceChecks
    {
        // Native heights are the collision reference (samples truncated toward zero at 0.03, main diagonal) to 1e-5 in the MMR-37 runs;
        // they are checked against it at 0.001, and against the quantized samples at the truncation bound.
        private const double HeightTolerance = .05, CollisionTolerance = .001, QuantizedTolerance = WorldSurface.CollisionStep + .001, RayTolerance = .1, NormalTolerance = 1;
        private const BodyFlags LandMask = BodyFlags.Moveable | BodyFlags.OnlyCollideWithRaycast;
        private const BodyFlags SeaMask = BodyFlags.Moveable | BodyFlags.CommonCollisionExcludeFlags;

        // MMR-39: a production scene carries water bodies with plane physics (the sea plane at the datum over the terrain, the lake planes over
        // their boxes), so a physics answer (ground height, ray) is the water surface where the terrain lies below it; terrain-only queries
        // (heights, normals) still answer the terrain.
        internal static Dictionary<string, object> Run(RuntimeWorld world, GeneratedMapScene map, SceneView view, List<WorldSurface.Landmark> landmarks,
            KeyValuePair<WorldSurface, List<WorldSurface.Landmark>>? previous, Action<string> stage, IList<GeneratedWaterSystem.Lake> lakes = null)
        {
            var s = world.Surface;
            Lakes = lakes;
            var failures = new List<string>();
            var rows = new List<object>();
            var report = new Dictionary<string, object> { ["tolerances"] = new { heightUnits = HeightTolerance, rayUnits = RayTolerance, normalDegrees = NormalTolerance }, ["rows"] = rows, ["failures"] = failures };
            Action<bool, string> check = (ok, name) => { if (!ok) failures.Add(name); };
            // Bounds.
            stage("bounds");
            var bounds = world.Bounds(); report["bounds"] = bounds;
            float[] boxMin = (float[])bounds["boxMin"], boxMax = (float[])bounds["boxMax"];
            check((int)bounds["nodesX"] == WorldSurface.Nodes && (int)bounds["nodesY"] == WorldSurface.Nodes && Close((float)bounds["nodeSize"], s.NodeSize, HeightTolerance), "bounds:nodes");
            check((bool)bounds["rangeAvailable"] && Close((float)bounds["minHeight"], s.Low, HeightTolerance) && Close((float)bounds["maxHeight"], s.High, HeightTolerance), "bounds:height_range");
            check((bool)bounds["hasHeightmap"] && (bool)bounds["containsTerrain"], "bounds:terrain_present");
            check(boxMin[0] <= HeightTolerance && boxMin[1] <= HeightTolerance && boxMax[0] >= s.Extent - HeightTolerance && boxMax[1] >= s.Extent - HeightTolerance && boxMin[2] <= s.Low + HeightTolerance && boxMax[2] >= s.High - HeightTolerance, "bounds:box_contains_terrain");
            IMapScene wrapper = map;
            Vec2 bmin, bmax; float bheight; wrapper.GetMapBorders(out bmin, out bmax, out bheight);
            Vec2 size = wrapper.GetTerrainSize();
            report["mapScene"] = new { borders = new[] { bmin.x, bmin.y, bmax.x, bmax.y }, maximumHeight = bheight, terrainSize = new[] { size.x, size.y } };
            // The map scene's borders, maximum height and terrain size were taken from the native scene; they must be the model's.
            check(Close(bmin.x, 0, 1e-3) && Close(bmin.y, 0, 1e-3) && Close(bmax.x, s.Extent, 1e-3) && Close(bmax.y, s.Extent, 1e-3) && Close(bheight, s.High, HeightTolerance) && Close(size.x, s.Extent, 1e-3) && Close(size.y, s.Extent, 1e-3), "map_scene:borders_and_size");
            // Landmarks.
            int landmarkRows = 0;
            foreach (var mark in landmarks)
            {
                stage("landmark_" + mark.Name);
                var xy = new Vec2((float)mark.X, (float)mark.Y);
                double expected = s.Height(xy.x, xy.y), collision = s.CollisionHeight(xy.x, xy.y);
                float terrain = world.Height(xy, true), noHoles = world.Height(xy, false), combined; Vec3 normal;
                world.HeightAndNormal(xy, out combined, out normal);
                Vec3 normalAt = world.Normal(xy);
                float land, sea; bool landHit = world.Ground(xy, LandMask, out land), seaHit = world.Ground(xy, SeaMask, out sea);
                float adapterLand = float.NaN, adapterSea = float.NaN;
                var landPoint = new CampaignVec2(xy, true); var seaPoint = new CampaignVec2(xy, false);
                bool adapterLandHit = wrapper.GetHeightAtPoint(in landPoint, ref adapterLand), adapterSeaHit = wrapper.GetHeightAtPoint(in seaPoint, ref adapterSea);
                float adapterHeight; Vec3 adapterNormal; wrapper.GetTerrainHeightAndNormal(xy, out adapterHeight, out adapterNormal);
                Vec3 ground = wrapper.GetGroundNormal(xy);
                double physical = Surface(s, xy.x, xy.y, collision);
                var vertical = Cast(world, view, s, new Vec3(xy.x, xy.y, (float)(s.High + 50)), new Vec3(xy.x, xy.y, (float)(s.Low - 50)));
                float reach = (float)(s.Extent * .04);
                var oblique = Cast(world, view, s, new Vec3(Math.Max(0, xy.x - reach), xy.y, (float)(s.High + 50)), new Vec3(Math.Min((float)s.Extent, xy.x + reach), xy.y, (float)(s.Low - 50)));
                double[] model = s.Normal(xy.x, xy.y);
                var row = new Dictionary<string, object> { ["landmark"] = mark.Name, ["kind"] = mark.Kind, ["position"] = new[] { mark.X, mark.Y }, ["expected"] = expected, ["collision"] = collision,
                    ["terrain"] = terrain, ["terrainNoHoles"] = noHoles, ["combined"] = combined, ["land"] = landHit ? (object)land : null, ["sea"] = seaHit ? (object)sea : null,
                    ["normal"] = V(normal), ["normalAt"] = V(normalAt), ["modelNormal"] = model, ["normalVsModelDegrees"] = Angle(normal, model), ["normalAtVsModelDegrees"] = Angle(normalAt, model),
                    ["collisionError"] = terrain - collision, ["physicalSurface"] = physical,
                    ["mapScene"] = new { land = adapterLandHit ? (object)adapterLand : null, sea = adapterSeaHit ? (object)adapterSea : null, height = adapterHeight, normal = V(adapterNormal), groundNormal = V(ground) },
                    ["vertical"] = vertical, ["oblique"] = oblique };
                rows.Add(row); landmarkRows++;
                string n = mark.Name;
                check(Close(terrain, collision, CollisionTolerance) && Close(noHoles, collision, CollisionTolerance) && Close(combined, collision, CollisionTolerance)
                      && Close(terrain, expected, QuantizedTolerance), n + ":height");
                check(landHit && Close(land, physical, CollisionTolerance) && seaHit && Close(sea, physical, CollisionTolerance), n + ":ground_masks");
                check(Unit(normal) && Unit(normalAt) && Angle(normal, normalAt) <= NormalTolerance && Angle(normal, model) <= NormalTolerance && Angle(normalAt, model) <= NormalTolerance, n + ":normals");
                check(adapterLandHit && adapterSeaHit && Close(adapterLand, land, HeightTolerance) && Close(adapterSea, sea, HeightTolerance) && Close(adapterHeight, combined, HeightTolerance)
                      && Angle(adapterNormal, normal) <= NormalTolerance && Angle(ground, normalAt) <= NormalTolerance, n + ":map_scene_agrees");
                check((bool)vertical["passed"], n + ":vertical_ray"); check((bool)oblique["passed"], n + ":oblique_ray");
                if (mark.Kind == "coast") check(terrain > 0, n + ":coast_sign");
            }
            report["landmarks"] = landmarkRows;
            // Chunk borders: no height or normal tear.
            stage("chunk_borders");
            var borders = new List<object>(); report["chunkBorders"] = borders;
            foreach (var pair in s.BorderSamples(6, .02))
            {
                var a = new Vec2((float)pair.Value[0], (float)pair.Value[1]); var b = new Vec2((float)pair.Value[2], (float)pair.Value[3]);
                float ha = world.Height(a, true), hb = world.Height(b, true);
                Vec3 na = world.Normal(a), nb = world.Normal(b);
                double ma = s.CollisionHeight(a.x, a.y), mb = s.CollisionHeight(b.x, b.y), modelAngle = Angle(s.Normal(a.x, a.y), s.Normal(b.x, b.y)), nativeAngle = Angle(na, nb);
                borders.Add(new { border = pair.Key, sides = pair.Value, native = new[] { ha, hb }, model = new[] { ma, mb }, nativeNormalAngle = nativeAngle, modelNormalAngle = modelAngle });
                check(Close(ha, ma, CollisionTolerance) && Close(hb, mb, CollisionTolerance) && Math.Abs(ha - hb) <= Math.Abs(ma - mb) + CollisionTolerance, pair.Key + ":height_tear");
                check(nativeAngle <= Math.Max(modelAngle, 0) + NormalTolerance && Angle(na, s.Normal(a.x, a.y)) <= NormalTolerance && Angle(nb, s.Normal(b.x, b.y)) <= NormalTolerance, pair.Key + ":normal_tear");
            }
            // Periodic seam pairs: x and x + logical width hold the same samples.
            stage("seam_pairs");
            var seams = new List<object>(); report["seamPairs"] = seams;
            foreach (var pair in s.SeamPairs(12))
            {
                var a = new Vec2((float)pair.Value[0], (float)pair.Value[1]); var b = new Vec2((float)pair.Value[2], (float)pair.Value[3]);
                float ha = world.Height(a, true), hb = world.Height(b, true);
                seams.Add(new { pair = pair.Key, positions = pair.Value, native = new[] { ha, hb }, model = s.Height(a.x, a.y) });
                check(Close(ha, hb, CollisionTolerance) && Close(ha, s.CollisionHeight(a.x, a.y), CollisionTolerance), pair.Key + ":seam_equal");
            }
            // A native selection along a vertical ray through the centre: its terrain hits agree with the collision reference.
            stage("selection");
            report["selection"] = Selection(world, s, failures);
            // The previous world's landmark positions: native answers are this world's, never the previous one's.
            if (previous.HasValue)
            {
                stage("previous_positions");
                int differing = 0, ours = 0; var stale = new List<object>();
                foreach (var mark in previous.Value.Value)
                {
                    var xy = new Vec2((float)mark.X, (float)mark.Y);
                    double mine = s.Height(xy.x, xy.y), theirs = previous.Value.Key.Height(xy.x, xy.y);
                    if (Math.Abs(mine - theirs) <= 4 * HeightTolerance) continue;
                    differing++;
                    float native = world.Height(xy, true);
                    if (Close(native, s.CollisionHeight(xy.x, xy.y), CollisionTolerance)) ours++;
                    else stale.Add(new { landmark = mark.Name, native, thisWorld = mine, previousWorld = theirs });
                }
                report["previousWorldPositions"] = new { checkedPositions = previous.Value.Value.Count, differing, answeredByThisWorld = ours, stale };
                check(differing > 0 && ours == differing, "previous_world_positions:answered_by_this_world");
            }
            report["passed"] = failures.Count == 0;
            return report;
        }

        // MMR-40: the production check (SettlementEntityChecks, which the generation job's runtime stage refuses a world by).
        internal static Dictionary<string, object> Settlements(RuntimeWorld world, PreparedSurface prepared) { return SettlementEntityChecks.Run(world, prepared); }

        // MMR-39: the encoded scene's water bodies (GeneratedWaterSystem): the sea plane on the datum over the whole terrain and one plane per
        // lake at its level; a ray down at a lake sample meets its lake's plane; and no colour-grade manager.
        internal static Dictionary<string, object> Water(RuntimeWorld world, PreparedSurface prepared)
        {
            var failures = new List<string>();
            var report = new Dictionary<string, object> { ["failures"] = failures };
            var scene = world.Scene; var s = world.Surface;
            var bodies = scene.FindEntitiesWithTag(GeneratedWaterSystem.Tag).ToList();
            string seaName = prepared.SceneName + "_water";
            report["bodies"] = bodies.Count; report["lakes"] = prepared.Lakes.Count; report["unmatchedLakes"] = prepared.UnmatchedLakes; report["hiddenLakes"] = prepared.HiddenLakes;
            if (bodies.Count != 1 + prepared.Lakes.Count) failures.Add(bodies.Count + " water bodies, expected the sea plane and " + prepared.Lakes.Count + " lake planes.");
            var sea = bodies.FirstOrDefault(b => b.Name == seaName);
            if (sea == null) failures.Add("No sea plane " + seaName + ".");
            else
            {
                var f = sea.GetGlobalFrame();
                report["sea"] = new { origin = V(f.origin), scale = new[] { f.rotation.s.Length, f.rotation.f.Length } };
                if (Math.Abs(f.origin.z - GeneratedWaterSystem.DatumZ) > 1e-3 || f.rotation.s.Length < s.Extent || f.rotation.f.Length < s.Extent) failures.Add("The sea plane is not on the datum over the terrain.");
            }
            var rows = new List<object>(); report["lakeRows"] = rows;
            int placed = 0, rayHits = 0;
            foreach (var lake in prepared.Lakes)
            {
                string name = GeneratedWaterSystem.LakeName(seaName, lake);
                var body = bodies.FirstOrDefault(b => b.Name == name);
                var row = new Dictionary<string, object> { ["name"] = name, ["expected"] = lake.Record(), ["present"] = body != null };
                rows.Add(row);
                if (body == null) continue;
                var f = body.GetGlobalFrame();
                bool where = Math.Abs(f.origin.x - lake.CenterX) < 1e-3 && Math.Abs(f.origin.y - lake.CenterY) < 1e-3 && Math.Abs(f.origin.z - lake.Z) < 1e-3
                    && Math.Abs(f.rotation.s.Length - (lake.MaxX - lake.MinX)) < 1e-3 && Math.Abs(f.rotation.f.Length - (lake.MaxY - lake.MinY)) < 1e-3;
                row["origin"] = V(f.origin); row["placed"] = where; if (where) placed++;
                float distance; Vec3 closest;
                var from = new Vec3((float)lake.FirstSampleX, (float)lake.FirstSampleY, (float)lake.Z + 20); var to = new Vec3((float)lake.FirstSampleX, (float)lake.FirstSampleY, (float)lake.Z - 20);
                bool hit = scene.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f);
                row["rayHit"] = hit; row["rayZ"] = hit ? (object)closest.z : null; row["terrainZ"] = s.CollisionHeight(lake.FirstSampleX, lake.FirstSampleY);
                if (hit && Math.Abs(closest.z - lake.Z) < 2e-3) rayHits++;
            }
            report["lakesPlaced"] = placed; report["lakeRayHits"] = rayHits;
            if (placed != prepared.Lakes.Count) failures.Add((prepared.Lakes.Count - placed) + " lake planes are missing or misplaced.");
            if (rayHits != prepared.Lakes.Count) failures.Add((prepared.Lakes.Count - rayHits) + " rays at lake samples did not meet the lake plane.");
            // A production scene carries no colour-grade manager (its Init would read Calradia's grid; MMR-45 adds it with the generated grid).
            report["colorGradeManager"] = scene.GetFirstEntityWithScriptComponent("MapColorGradeManager") != null;
            if ((bool)report["colorGradeManager"]) failures.Add("The production scene carries a colour-grade manager.");
            report["passed"] = failures.Count == 0;
            return report;
        }

        private static IList<GeneratedWaterSystem.Lake> Lakes;

        // The physical surface at a point: the terrain, or the water body above it.
        private static double Surface(WorldSurface s, double x, double y, double terrain)
        {
            double top = terrain;
            if (x >= 0 && x <= s.Extent && y >= 0 && y <= s.Extent) top = Math.Max(top, GeneratedWaterSystem.DatumZ);
            if (Lakes != null) foreach (var l in Lakes) if (x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY) top = Math.Max(top, l.Z);
            return top;
        }

        // The first water plane a downward segment meets above the terrain (fraction along it), or NaN.
        private static double WaterHit(WorldSurface s, Vec3 from, Vec3 to)
        {
            double best = double.NaN;
            var planes = new List<double[]> { new[] { GeneratedWaterSystem.DatumZ, 0, 0, s.Extent, s.Extent } };
            if (Lakes != null) planes.AddRange(Lakes.Select(l => new[] { l.Z, l.MinX, l.MinY, l.MaxX, l.MaxY }));
            foreach (var plane in planes)
            {
                double z = plane[0];
                if (!(from.z > z && to.z < z)) continue;
                double f = (from.z - z) / (from.z - to.z), x = from.x + (to.x - from.x) * f, y = from.y + (to.y - from.y) * f;
                if (x < plane[1] || x > plane[3] || y < plane[2] || y > plane[4] || !(s.CollisionHeight(x, y) < z)) continue;
                if (double.IsNaN(best) || f < best) best = f;
            }
            return best;
        }

        private static Dictionary<string, object> Cast(RuntimeWorld world, SceneView view, WorldSurface s, Vec3 from, Vec3 to)
        {
            double fraction; bool expected = s.Intersect(new double[] { from.x, from.y, from.z }, new double[] { to.x, to.y, to.z }, out fraction);
            double water = WaterHit(s, from, to);
            if (!double.IsNaN(water) && (!expected || water < fraction)) { expected = true; fraction = water; }
            Vec3 point = expected ? Lerp(from, to, fraction) : Vec3.Zero;
            float distance; Vec3 closest;
            bool hit = world.Ray(from, to, LandMask, out distance, out closest);
            double length = (to - from).Length; Vec3 reconstructed = Lerp(from, to, distance / length);
            float viewDistance; Vec3 viewClosest;
            bool viewHit = view.RayCastForClosestEntityOrTerrain(from, to, out viewDistance, out viewClosest, .01f, LandMask);
            Vec3 viewPoint = Lerp(from, to, viewDistance / length);
            bool passed = hit == expected && viewHit == expected && (!hit || ((reconstructed - point).Length <= RayTolerance && (viewPoint - reconstructed).Length <= RayTolerance && (closest - reconstructed).Length <= RayTolerance));
            return new Dictionary<string, object> { ["from"] = V(from), ["to"] = V(to), ["expectedHit"] = expected, ["expected"] = expected ? V(point) : null, ["hit"] = hit, ["point"] = hit ? V(reconstructed) : null,
                ["sceneViewHit"] = viewHit, ["sceneViewPoint"] = viewHit ? V(viewPoint) : null, ["error"] = hit && expected ? (object)(reconstructed - point).Length : null, ["passed"] = passed };
        }

        private static Dictionary<string, object> Selection(RuntimeWorld world, WorldSurface s, List<string> failures)
        {
            var scene = world.Scene;
            var from = new Vec3((float)(s.Extent / 2 + 3.1), (float)(s.Extent / 2 + 1.7), (float)(s.High + 50)); var to = new Vec3(from.x, from.y, (float)(s.Low - 50));
            Vec3 direction = to - from; float length = direction.Normalize();
            var ray = new Ray(from, direction, length);
            var intersections = new Intersection[128]; var ids = new UIntPtr[128];
            int count = scene.SelectEntitiesCollidedWith(ref ray, intersections, ids);
            if (count < 0 || count > 128) { failures.Add("selection:count_out_of_range"); return new Dictionary<string, object> { ["count"] = count }; }
            var hits = new List<object>(); int terrain = 0;
            for (int i = 0; i < count; i++)
            {
                var hit = intersections[i];
                bool isTerrain = hit.Type == IntersectionType.Terrain;
                double expected = isTerrain && s.Contains(hit.IntersectionPoint.x, hit.IntersectionPoint.y) ? s.CollisionHeight(hit.IntersectionPoint.x, hit.IntersectionPoint.y) : double.NaN;
                hits.Add(new { type = hit.Type.ToString(), point = V(hit.IntersectionPoint), expected });
                if (isTerrain) { terrain++; if (!Close(hit.IntersectionPoint.z, expected, HeightTolerance)) failures.Add("selection:terrain_height"); }
            }
            Array.Clear(intersections, 0, count); Array.Clear(ids, 0, count);
            if (terrain == 0) failures.Add("selection:no_terrain_hit");
            return new Dictionary<string, object> { ["count"] = count, ["terrainHits"] = terrain, ["hits"] = hits };
        }

        private static bool Close(double a, double b, double t) { return !double.IsNaN(a) && !double.IsNaN(b) && !double.IsInfinity(a) && !double.IsInfinity(b) && Math.Abs(a - b) <= t; }
        private static bool Unit(Vec3 v) { double l = Math.Sqrt(v.x * (double)v.x + v.y * (double)v.y + v.z * (double)v.z); return Math.Abs(l - 1) < .01; }
        private static double Angle(Vec3 a, Vec3 b) { return Angle(new double[] { a.x, a.y, a.z }, new double[] { b.x, b.y, b.z }); }
        private static double Angle(Vec3 a, double[] b) { return Angle(new double[] { a.x, a.y, a.z }, b); }
        private static double Angle(double[] a, double[] b)
        {
            double la = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]), lb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
            if (!(la > 1e-12) || !(lb > 1e-12)) return double.NaN;
            return Math.Acos(Math.Max(-1, Math.Min(1, (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (la * lb)))) * 180 / Math.PI;
        }
        private static float[] V(Vec3 v) { return new[] { v.x, v.y, v.z }; }
        private static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return new Vec3((float)(a.x + (b.x - a.x) * t), (float)(a.y + (b.y - a.y) * t), (float)(a.z + (b.z - a.z) * t)); }
    }
}
