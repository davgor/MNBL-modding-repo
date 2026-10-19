using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // Reads an installed (or owned, pre-indexed) scene by name into an isolated native scene and records what the
    // engine's own terrain, navmesh and physics consumers report. Read-only with respect to game files.
    //
    // Args: sceneName (required), module (optional: uses the module-qualified Read overload), preset ("battle"|"map"),
    // points [[x,y],...], doRaycast, rayThickness, nodes [[nx,ny],...], allNodes, dumpHeightData, waitLoadingSeconds,
    // ticksBeforeRaycast, tickWhileWaiting, nodeData, heightData, continueAfterReadException, createPhysics, enableDecals, atlasGroup, initPhysicsWorld, loadNavMesh, initFloraNodes,
    // usePhysicsMaterials, enableFloraPhysics, useTerrainMeshBlending, doNotUseLoadingScreen.
    internal static class SceneQueryProbe
    {
        public static IEnumerator Run(ProbeContext context)
        {
            var sceneName = context.Str("sceneName", null);
            if (string.IsNullOrEmpty(sceneName)) throw new ArgumentException("scene_query requires args.sceneName.");
            var module = context.Str("module", null);
            bool map = sceneName.Equals("Main_map", StringComparison.OrdinalIgnoreCase);
            var preset = context.Str("preset", map ? "map" : "battle");
            if (preset != "map" && preset != "battle") throw new ArgumentException("preset must be 'map' or 'battle'.");
            var points = context.Pairs("points");
            bool doRaycast = context.Bool("doRaycast", false);

            // Official precedents: SandBox.MapScene.Load uses CreateNewScene(false, true, Worldmap) and
            // SceneInitializationData(true) minus physics materials, flora physics, mesh blending and oros;
            // MultiThreadedStressTestsScreen/BenchmarkScreen use CreateNewScene(true, true, All) + Read(name).
            bool createPhysics = context.Bool("createPhysics", preset == "battle");
            bool enableDecals = context.Bool("enableDecals", true);
            var atlas = (DecalAtlasGroup)context.Int("atlasGroup", preset == "map" ? (int)DecalAtlasGroup.Worldmap : (int)DecalAtlasGroup.All);
            var initialization = new SceneInitializationData(true);
            if (preset == "map")
            {
                initialization.UsePhysicsMaterials = false;
                initialization.EnableFloraPhysics = false;
                initialization.UseTerrainMeshBlending = false;
                initialization.CreateOros = false;
                // MapScene installs a campaign region map before loading navigation; this probe has no campaign.
                initialization.LoadNavMesh = false;
            }
            initialization.InitPhysicsWorld = context.Bool("initPhysicsWorld", initialization.InitPhysicsWorld);
            initialization.LoadNavMesh = context.Bool("loadNavMesh", initialization.LoadNavMesh);
            initialization.InitFloraNodes = context.Bool("initFloraNodes", initialization.InitFloraNodes);
            initialization.UsePhysicsMaterials = context.Bool("usePhysicsMaterials", initialization.UsePhysicsMaterials);
            initialization.EnableFloraPhysics = context.Bool("enableFloraPhysics", initialization.EnableFloraPhysics);
            initialization.UseTerrainMeshBlending = context.Bool("useTerrainMeshBlending", initialization.UseTerrainMeshBlending);
            initialization.DoNotUseLoadingScreen = context.Bool("doNotUseLoadingScreen", true);
            context.Outputs["sceneName"] = sceneName;
            context.Outputs["module"] = module;
            context.Outputs["preset"] = preset;
            context.Outputs["createNewScene"] = new Dictionary<string, object> {
                { "initializePhysics", createPhysics }, { "enableDecals", enableDecals }, { "atlasGroup", atlas.ToString() } };
            context.Outputs["initialization"] = new Dictionary<string, object> {
                { "InitPhysicsWorld", initialization.InitPhysicsWorld }, { "LoadNavMesh", initialization.LoadNavMesh },
                { "InitFloraNodes", initialization.InitFloraNodes }, { "UsePhysicsMaterials", initialization.UsePhysicsMaterials },
                { "EnableFloraPhysics", initialization.EnableFloraPhysics }, { "UseTerrainMeshBlending", initialization.UseTerrainMeshBlending },
                { "DoNotUseLoadingScreen", initialization.DoNotUseLoadingScreen }, { "CreateOros", initialization.CreateOros },
                { "InitSkyboxFromStart", initialization.InitSkyboxFromStart } };

            context.Stage("create_scene");
            var scene = Scene.CreateNewScene(createPhysics, enableDecals, atlas, "mnbl_scene_query");
            context.Stage("create_scene_done");
            double readStart = context.Seconds;
            context.Stage("read_scene name=" + sceneName + (module == null ? "" : " module=" + module));
            bool readCompleted = true;
            try
            {
                if (module == null) scene.Read(sceneName, ref initialization, "");
                else scene.Read(sceneName, module, ref initialization, "");
            }
            catch (Exception failure)
            {
                // Mission-object scripts (e.g. FleePosition.OnInit -> Mission.Current.AddFleePosition) throw while the
                // native Read instantiates entity scripts outside a Mission. The read is then incomplete; later
                // measurements are kept only as a labelled partial-read experiment.
                if (!context.Bool("continueAfterReadException", true)) throw;
                readCompleted = false;
                context.Outputs["readException"] = failure.ToString();
                context.Stage("read_scene_exception " + failure.GetType().Name + " at " + Frame(failure));
            }
            double readSeconds = context.Seconds - readStart;
            context.Stage("read_scene_done completed=" + readCompleted + " seconds=" + readSeconds.ToString("F3"));
            context.Outputs["readCompleted"] = readCompleted;
            context.Outputs["readSeconds"] = Math.Round(readSeconds, 3);
            context.Outputs["afterRead"] = State(scene);
            context.Checkpoint();

            context.Stage("heights_after_read");
            context.Outputs["heightsAfterRead"] = Heights(context, scene, points, "h0");
            context.Checkpoint();

            double waitLimit = context.Num("waitLoadingSeconds", 15);
            double waitStart = context.Seconds;
            if (!scene.IsLoadingFinished() && waitLimit > 0)
            {
                context.Stage("wait_loading");
                bool tick = context.Bool("tickWhileWaiting", false);
                while (!scene.IsLoadingFinished() && context.Seconds - waitStart < waitLimit)
                {
                    if (tick) scene.Tick(0.016f);
                    yield return null;
                }
                context.Stage("wait_loading_done finished=" + scene.IsLoadingFinished());
                context.Outputs["afterWait"] = State(scene);
                context.Stage("heights_after_wait");
                context.Outputs["heightsAfterWait"] = Heights(context, scene, points, "h1");
            }
            context.Outputs["waitedSeconds"] = Math.Round(context.Seconds - waitStart, 3);

            context.Stage("terrain_info");
            Vec2i nodeDimension; float nodeSize; int layerCount, layerVersion;
            scene.GetTerrainData(out nodeDimension, out nodeSize, out layerCount, out layerVersion);
            float minHeight, maxHeight;
            bool minMax = scene.GetTerrainMinMaxHeight(out minHeight, out maxHeight);
            context.Stage("navmesh_face_count");
            int navFaces = scene.GetNavMeshFaceCount();
            context.Stage("bounds");
            Vec3 boxMin, boxMax, limitMin, limitMax;
            scene.GetBoundingBox(out boxMin, out boxMax);
            scene.GetSceneLimits(out limitMin, out limitMax);
            context.Outputs["terrain"] = new Dictionary<string, object> {
                { "nodeDimensionX", nodeDimension.X }, { "nodeDimensionY", nodeDimension.Y }, { "nodeSize", ProbeContext.F(nodeSize) },
                { "extentX", ProbeContext.F(nodeDimension.X * nodeSize) }, { "extentY", ProbeContext.F(nodeDimension.Y * nodeSize) },
                { "layerCount", layerCount }, { "layerVersion", layerVersion },
                { "minMaxReturned", minMax }, { "minHeight", ProbeContext.F(minHeight) }, { "maxHeight", ProbeContext.F(maxHeight) },
                { "terrainMemoryUsage", scene.GetTerrainMemoryUsage() },
                { "navMeshFaceCount", navFaces },
                { "boundingBoxMin", V(boxMin) }, { "boundingBoxMax", V(boxMax) },
                { "sceneLimitsMin", V(limitMin) }, { "sceneLimitsMax", V(limitMax) },
                { "waterLevel", ProbeContext.F(scene.GetWaterLevel()) } };
            context.Checkpoint();

            if (navFaces > 0 && context.Bool("navFaces", true))
            {
                context.Stage("navmesh_groups");
                var groups = new SortedDictionary<int, int>();
                for (int i = 0; i < navFaces; i++)
                {
                    int group = scene.GetNavMeshPathFaceRecord(i).FaceGroupIndex;
                    int seen;
                    groups[group] = groups.TryGetValue(group, out seen) ? seen + 1 : 1;
                }
                var histogram = new Dictionary<string, object>();
                foreach (var pair in groups) histogram[pair.Key.ToString()] = pair.Value;
                context.Outputs["navGroupHistogram"] = histogram;
                context.Checkpoint();
            }

            // Routes: [[x0,y0,x1,y1],...] resolved to navmesh faces at terrain height, then native path distance.
            if (navFaces > 0 && context.Has("routes"))
            {
                var routes = new List<object>();
                context.Outputs["routes"] = routes;
                var list = context.Args["routes"] as IEnumerable;
                int index = 0;
                foreach (var item in list)
                {
                    var values = new List<float>();
                    foreach (var number in (IEnumerable)item) values.Add(Convert.ToSingle(number, System.Globalization.CultureInfo.InvariantCulture));
                    if (values.Count != 4) throw new ArgumentException("routes entries must be [x0,y0,x1,y1].");
                    context.Stage("route i=" + index);
                    var a = new Vec2(values[0], values[1]);
                    var b = new Vec2(values[2], values[3]);
                    var faceA = PathFaceRecord.NullFaceRecord;
                    var faceB = PathFaceRecord.NullFaceRecord;
                    scene.GetNavMeshFaceIndex(ref faceA, new Vec3(a.x, a.y, scene.GetTerrainHeight(a, true) + 0.5f), false);
                    scene.GetNavMeshFaceIndex(ref faceB, new Vec3(b.x, b.y, scene.GetTerrainHeight(b, true) + 0.5f), false);
                    float distance = -1;
                    bool found = false;
                    if (faceA.FaceIndex >= 0 && faceB.FaceIndex >= 0)
                        found = scene.GetPathDistanceBetweenAIFaces(faceA.FaceIndex, faceB.FaceIndex, a, b, 0, 100000, out distance, new int[0], 0, 0);
                    context.Stage("route_done i=" + index + " found=" + found);
                    routes.Add(new Dictionary<string, object> { { "from", new[] { a.x, a.y } }, { "to", new[] { b.x, b.y } },
                        { "faceA", faceA.FaceIndex }, { "groupA", faceA.FaceGroupIndex }, { "faceB", faceB.FaceIndex }, { "groupB", faceB.FaceGroupIndex },
                        { "found", found }, { "distance", ProbeContext.F(distance) }, { "straight", ProbeContext.F((b - a).Length) } });
                    context.Checkpoint();
                    index++;
                }
            }

            if (doRaycast)
            {
                int ticks = context.Int("ticksBeforeRaycast", 0);
                for (int i = 0; i < ticks; i++)
                {
                    context.Stage("scene_tick " + i);
                    scene.Tick(0.1f);
                    yield return null;
                }
                float top = (minMax ? maxHeight : 500f) + 100f, bottom = (minMax ? minHeight : -100f) - 100f;
                float thickness = (float)context.Num("rayThickness", 0.01);
                var rays = new List<object>();
                context.Outputs["raycasts"] = rays;
                context.Outputs["raycastSettings"] = new Dictionary<string, object> {
                    { "fromZ", top }, { "toZ", bottom }, { "rayThickness", thickness }, { "excludeBodyFlags", "default (79617)" }, { "sceneTicks", ticks } };
                context.Checkpoint();
                for (int i = 0; i < points.Count; i++)
                {
                    float x = (float)points[i][0], y = (float)points[i][1];
                    context.Stage("raycast_before i=" + i + " x=" + x + " y=" + y);
                    float distance; Vec3 closest;
                    bool hit = scene.RayCastForClosestEntityOrTerrain(new Vec3(x, y, top), new Vec3(x, y, bottom), out distance, out closest, thickness);
                    context.Stage("raycast_after i=" + i + " hit=" + hit);
                    rays.Add(new Dictionary<string, object> { { "x", x }, { "y", y }, { "hit", hit }, { "distance", ProbeContext.F(distance) },
                        { "hitZ", hit ? ProbeContext.F(top - distance) : null }, { "closestPoint", hit ? V(closest) : null } });
                    context.Checkpoint();
                }
            }

            // Node data last: GetTerrainHeightData crashed natively on a partially read battle scene (PID 32160).
            if (context.Bool("nodeData", true))
            {
                context.Stage("node_data");
                Nodes(context, scene, nodeDimension, nodeSize);
                context.Checkpoint();
            }

            // Results are on disk before teardown, so a teardown failure cannot lose measurements.
            context.Stage("cleanup_clear_all");
            scene.ClearAll();
            context.Stage("cleanup_done");
            context.Outputs["cleanup"] = "ClearAll";
        }

        private static Dictionary<string, object> State(Scene scene)
        {
            return new Dictionary<string, object> {
                { "name", scene.GetName() }, { "isLoadingFinished", scene.IsLoadingFinished() }, { "loadingState", scene.GetLoadingStateName() },
                { "hasTerrainHeightmap", scene.HasTerrainHeightmap }, { "containsTerrain", scene.ContainsTerrain } };
        }

        private static List<object> Heights(ProbeContext context, Scene scene, List<double[]> points, string tag)
        {
            var result = new List<object>();
            for (int i = 0; i < points.Count; i++)
            {
                var p = new Vec2((float)points[i][0], (float)points[i][1]);
                context.Stage(tag + " i=" + i + " x=" + p.x + " y=" + p.y);
                float withHoles = scene.GetTerrainHeight(p, true);
                float noHoles = scene.GetTerrainHeight(p, false);
                float height; Vec3 normal;
                scene.GetTerrainHeightAndNormal(p, out height, out normal);
                result.Add(new Dictionary<string, object> { { "x", p.x }, { "y", p.y }, { "height", ProbeContext.F(withHoles) },
                    { "heightNoHoleCheck", ProbeContext.F(noHoles) }, { "heightAndNormalHeight", ProbeContext.F(height) }, { "normal", V(normal) } });
            }
            return result;
        }

        private static void Nodes(ProbeContext context, Scene scene, Vec2i dimension, float nodeSize)
        {
            var wanted = new List<int[]>();
            if (context.Bool("allNodes", false))
            {
                for (int y = 0; y < dimension.Y; y++) for (int x = 0; x < dimension.X; x++) wanted.Add(new[] { x, y });
            }
            else if (context.Has("nodes"))
            {
                foreach (var pair in context.Pairs("nodes")) wanted.Add(new[] { (int)pair[0], (int)pair[1] });
            }
            else if (dimension.X > 0 && dimension.Y > 0)
            {
                wanted.Add(new[] { 0, 0 });
                if (dimension.X > 1 || dimension.Y > 1) wanted.Add(new[] { dimension.X - 1, dimension.Y - 1 });
            }
            bool heightData = context.Bool("heightData", true);
            bool dump = context.Bool("dumpHeightData", false);
            string dumpDirectory = Path.Combine(context.Directory, "terrain-" + context.ProcessId);
            if (dump) Directory.CreateDirectory(dumpDirectory);
            var result = new List<object>();
            context.Outputs["nodes"] = result;
            foreach (var node in wanted)
            {
                int nx = node[0], ny = node[1];
                if (nx < 0 || ny < 0 || nx >= dimension.X || ny >= dimension.Y)
                {
                    result.Add(new Dictionary<string, object> { { "x", nx }, { "y", ny }, { "skipped", "outside node dimension" } });
                    continue;
                }
                context.Stage("node_info x=" + nx + " y=" + ny);
                int vertexCount; float quadLength, nodeMin, nodeMax;
                scene.GetTerrainNodeData(nx, ny, out vertexCount, out quadLength, out nodeMin, out nodeMax);
                var entry = new Dictionary<string, object> { { "x", nx }, { "y", ny }, { "vertexCountAlongAxis", vertexCount },
                    { "quadLength", ProbeContext.F(quadLength) }, { "minHeight", ProbeContext.F(nodeMin) }, { "maxHeight", ProbeContext.F(nodeMax) } };
                result.Add(entry);
                context.Checkpoint();
                if (!heightData) continue;
                context.Stage("node_heights x=" + nx + " y=" + ny);
                float[] data = scene.GetTerrainHeightData(nx, ny);
                entry["heightDataCount"] = data == null ? -1 : data.Length;
                if (data != null && data.Length > 0)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var v in data) { if (v < lo) lo = v; if (v > hi) hi = v; }
                    var bytes = new byte[data.Length * 4];
                    Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                    using (var sha = SHA256.Create()) entry["sha256"] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    entry["dataMin"] = ProbeContext.F(lo);
                    entry["dataMax"] = ProbeContext.F(hi);
                    var head = new List<object>();
                    for (int i = 0; i < Math.Min(8, data.Length); i++) head.Add(ProbeContext.F(data[i]));
                    entry["first"] = head;
                    if (dump)
                    {
                        var file = Path.Combine(dumpDirectory, "node_" + nx + "_" + ny + ".f32");
                        File.WriteAllBytes(file, bytes);
                        entry["dumpFile"] = Path.GetFileName(file);
                    }
                    // Layout probe: compare engine point queries with the array under row-major and column-major guesses.
                    int side = (int)Math.Round(Math.Sqrt(data.Length));
                    if (side * side == data.Length && side >= 4 && quadLength > 0)
                    {
                        var checks = new List<object>();
                        foreach (var ij in new[] { new[] { 1, 2 }, new[] { side - 2, 3 }, new[] { 2, side - 3 } })
                        {
                            int i = ij[0], j = ij[1];
                            var position = new Vec2(nx * nodeSize + i * quadLength, ny * nodeSize + j * quadLength);
                            float query = scene.GetTerrainHeight(position, false);
                            checks.Add(new Dictionary<string, object> { { "i", i }, { "j", j }, { "worldX", position.x }, { "worldY", position.y },
                                { "pointQuery", ProbeContext.F(query) }, { "rowMajor_j_side_plus_i", ProbeContext.F(data[j * side + i]) },
                                { "columnMajor_i_side_plus_j", ProbeContext.F(data[i * side + j]) } });
                        }
                        entry["layoutChecks"] = checks;
                    }
                }
                context.Checkpoint();
            }
        }

        private static string Frame(Exception failure)
        {
            var trace = new System.Diagnostics.StackTrace(failure, false);
            var frame = trace.FrameCount > 0 ? trace.GetFrame(0).GetMethod() : null;
            return frame == null ? "unknown" : frame.DeclaringType + "." + frame.Name;
        }

        private static object V(Vec3 v)
        {
            return new Dictionary<string, object> { { "x", ProbeContext.F(v.x) }, { "y", ProbeContext.F(v.y) }, { "z", ProbeContext.F(v.z) } };
        }
    }
}
