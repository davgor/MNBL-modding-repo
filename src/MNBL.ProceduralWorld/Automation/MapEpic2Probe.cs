using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-249: the map epic 2 probe. Reads the fixture slots tools/scenebin/build_probe_slots.py writes (deployed under
    // SceneObj before launch) one scene per screen, in the order probe-args.json gives, and records load and shader-compile
    // time, memory, frame times and engine screenshots per view, then runs the native experiments: per-season masks and
    // season switches, entity-less / entity / mesh decals with a runtime texture, 200/1,000/3,000 decals with removal and
    // re-creation, and the dynamic snow texture, terrain dynamic params and a runtime MapColorGradeManager. Every step is
    // fail-soft (a managed exception is recorded and the next step runs); outputs are checkpointed after each step, so a
    // native crash leaves everything before it on disk. Pixel answers are computed offline from the screenshots
    // (tools/summarize_map2_probe.py). All native calls run on the application thread (OnFrameTick).
    internal static class MapEpic2Probe
    {
        internal const string ModuleId = "MNBL.ProceduralWorld";

        internal static IEnumerator Run(ProbeContext context)
        {
            var root = new Dictionary<string, object>();
            var scenes = new List<object>();
            context.Outputs["map2"] = root;
            root["scenes"] = scenes;
            root["threadId"] = Thread.CurrentThread.ManagedThreadId;
            root["resolution"] = new[] { Screen.RealScreenResolutionWidth, Screen.RealScreenResolutionHeight };
            root["applicationVersion"] = Utilities.GetApplicationVersionWithBuildNumber().ToString();
            root["frameLimiterWithSleep"] = Utilities.GetFrameLimiterWithSleep();
            // Run 2: try to lift the 60 fps cap every earlier profile hit (docs/first-test-profile.md); the frame rows show whether it lifted.
            if (context.Bool("forceVsyncOff", false)) { Utilities.SetForceVsync(false); root["forceVsyncSetFalse"] = true; }
            var skip = new HashSet<string>(Strings(context.Args, "skip"));
            root["skip"] = skip.ToList();
            int index = 0;
            foreach (var entry in Items(context.Args, "scenes"))
            {
                var spec = entry as Dictionary<string, object>;
                if (spec == null) continue;
                string name = Convert.ToString(spec["name"], CultureInfo.InvariantCulture), kind = Convert.ToString(spec["kind"], CultureInfo.InvariantCulture);
                string label = "s" + (index++).ToString("00", CultureInfo.InvariantCulture) + "_" + name.Replace("mnbl_probe_", "") + (kind == "decals" ? "_decals" : kind == "decals2" || kind == "costonly" || kind == "snow2" || kind == "snow3" ? "_" + kind : "");
                var report = new Dictionary<string, object> { { "label", label }, { "scene", name }, { "kind", kind } };
                scenes.Add(report);
                if (skip.Contains(label) || skip.Contains(kind) || skip.Contains(name)) { report["status"] = "skipped"; context.Checkpoint(); continue; }
                if (!name.StartsWith("mnbl_probe_", StringComparison.Ordinal)) { report["status"] = "refused: not a probe slot"; continue; }
                context.Stage(label + "_begin");
                var screen = new ProbeSceneScreen(context, spec, report, label, skip);
                try
                {
                    ScreenManager.PushScreen(screen);
                    while (!screen.Complete) yield return null;
                }
                finally
                {
                    if (ScreenManager.TopScreen == screen) ScreenManager.PopScreen();
                    if (screen.Finalized) Utilities.ClearOldResourcesAndObjects();
                }
                report["status"] = screen.Failure == null ? "ok" : "failed";
                if (screen.Failure != null) report["failure"] = screen.Failure.ToString();
                context.Stage(label + "_end " + report["status"]);
                context.Checkpoint();
                double released = context.Seconds;
                while (context.Seconds - released < 2) yield return null;
            }
            root["completed"] = true;
            context.Checkpoint();
        }

        internal static IEnumerable<object> Items(Dictionary<string, object> args, string key)
        {
            object value;
            if (args == null || !args.TryGetValue(key, out value) || value == null || value is string) yield break;
            var list = value as IEnumerable;
            if (list == null) yield break;
            foreach (var item in list) yield return item;
        }

        internal static IEnumerable<string> Strings(Dictionary<string, object> args, string key)
        {
            foreach (var item in Items(args, key)) yield return Convert.ToString(item, CultureInfo.InvariantCulture);
        }

        internal static float[] Floats(object value)
        {
            var list = new List<float>();
            foreach (var item in (IEnumerable)value) list.Add(Convert.ToSingle(item, CultureInfo.InvariantCulture));
            return list.ToArray();
        }
    }

    internal sealed class ProbeSceneScreen : ScreenBase
    {
        private const string DecalSet = "editor_set";
        private readonly ProbeContext context;
        private readonly Dictionary<string, object> spec, report;
        private readonly string label, kind, sceneName;
        private readonly HashSet<string> skip;
        private readonly List<object> steps = new List<object>(), shots = new List<object>(), frames = new List<object>();
        private readonly Dictionary<string, Dictionary<string, object>> views = new Dictionary<string, Dictionary<string, object>>();
        private readonly List<KeyValuePair<string, Func<IEnumerator>>> plan = new List<KeyValuePair<string, Func<IEnumerator>>>();
        private readonly List<Decal> decals = new List<Decal>();
        private readonly List<GameEntity> entities = new List<GameEntity>();
        private readonly List<Texture> textures = new List<Texture>();
        private Scene scene;
        private SceneLayer layer;
        private Camera camera;
        private MBAgentRendererSceneController renderer;
        private IEnumerator current;
        private Dictionary<string, object> currentStep;
        private int planIndex = -1, frameCount;
        private double stepStart, lastFrame = -1;
        private int maxCompilations;
        internal bool Complete, Finalized;
        internal Exception Failure;

        internal ProbeSceneScreen(ProbeContext context, Dictionary<string, object> spec, Dictionary<string, object> report, string label, HashSet<string> skip)
        {
            this.context = context; this.spec = spec; this.report = report; this.label = label; this.skip = skip;
            kind = Convert.ToString(spec["kind"], CultureInfo.InvariantCulture);
            sceneName = Convert.ToString(spec["name"], CultureInfo.InvariantCulture);
            foreach (var item in MapEpic2Probe.Items(spec, "views"))
            {
                var view = item as Dictionary<string, object>;
                if (view != null) views[Convert.ToString(view["name"], CultureInfo.InvariantCulture)] = view;
            }
        }

        private int Frames { get { return context.Int("frames", 240); } }
        private int Settle { get { return context.Int("settleFrames", 40); } }
        private double StepTimeout { get { return context.Num("stepTimeoutSeconds", 600); } }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            report["steps"] = steps; report["shots"] = shots; report["frames"] = frames;
            var load = new Dictionary<string, object>();
            report["load"] = load;
            try
            {
                load["cpuMemoryMbBefore"] = Utilities.GetCurrentCpuMemoryUsageMB();
                load["gpuMemoryMbBefore"] = Utilities.GetGPUMemoryMB();
                context.Stage(label + "_create");
                var watch = Stopwatch.StartNew();
                // As SandBox.MapScene.Load and GeneratedMapScene: no physics, decals on, the world-map decal atlas.
                scene = Scene.CreateNewScene(false, true, DecalAtlasGroup.Worldmap, "mnbl_map2_probe");
                renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
                var init = new SceneInitializationData(true);
                init.UsePhysicsMaterials = false; init.EnableFloraPhysics = false; init.UseTerrainMeshBlending = false; init.CreateOros = false;
                // Run 3: snow texture routes that must exist before the terrain is read.
                string preRead = spec.ContainsKey("preRead") ? Convert.ToString(spec["preRead"], CultureInfo.InvariantCulture) : null;
                load["preRead"] = preRead;
                if (preRead == "bind") scene.SetDynamicSnowTexture(SnowTexture(0, false));
                if (preRead == "loadPath")
                {
                    string folder = Path.Combine(new FileInfo(typeof(MapEpic2Probe).Assembly.Location).Directory.Parent.Parent.FullName, "SceneObj", sceneName);
                    var mask = Texture.LoadTextureFromPath("mnbl_probe_snow_mask.png", folder);
                    load["maskLoaded"] = mask != null;
                    if (mask != null) { mask.SetTextureAsAlwaysValid(); mask.PreloadTexture(true); load["maskName"] = mask.Name; textures.Add(mask); }
                }
                context.Stage(label + "_read");
                scene.Read(sceneName, MapEpic2Probe.ModuleId, ref init, "");
                load["readSeconds"] = watch.Elapsed.TotalSeconds;
                if (scene.GetName() != sceneName) throw new InvalidDataException("Read selected scene " + scene.GetName());
                Vec2i nodes; float size; int layerCount, version;
                scene.GetTerrainData(out nodes, out size, out layerCount, out version);
                load["terrainNodes"] = new[] { nodes.X, nodes.Y }; load["nodeSize"] = size; load["terrainLayers"] = layerCount; load["terrainVersion"] = version;
                scene.SetUseConstantTime(true);
                scene.SetOcclusionMode(true);
                scene.TimeOfDay = 12f;
                watch.Restart();
                scene.OptimizeScene(true, true);
                load["optimizeSeconds"] = watch.Elapsed.TotalSeconds;
                camera = Camera.CreateCamera();
                layer = new SceneLayer();
                layer.SetScene(scene);
                layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                layer.SetSceneUsesShadows(true);
                layer.SetSceneUsesSkybox(true);
                layer.SceneView.SetForceShaderCompilation(true);
                layer.SceneView.SetClearGbuffer(true);
                var first = views.Values.FirstOrDefault();
                Aim(first);
                layer.SetCamera(camera);
                AddLayer(layer);
                scene.Tick(0);
                Plan();
                load["created"] = true;
            }
            catch (Exception error) { Failure = error; Complete = true; load["error"] = error.ToString(); }
            context.Checkpoint();
        }

        // ------------------------------------------------------------------ plan
        private void Plan()
        {
            Add("load", LoadMetrics);
            if (kind == "cost" || kind == "layers" || kind == "gallery" || kind == "forest" || kind == "world")
            {
                foreach (var name in new[] { "overview", "map38_flat", "map38_hill", "map38_mountain", "map38_coast" })
                    if (views.ContainsKey(name)) { string n = name; Add("frames_" + n, () => FrameStep(n)); }
                foreach (var name in views.Keys.ToList()) { string n = name; Add("shot_" + n, () => ShotStep(n)); }
            }
            if (kind == "layers") { Add("season_masks", SeasonSwitches); }
            if (kind == "decals")
            {
                Add("decal_techniques", DecalTechniques);
                Add("decal_counts", DecalCounts);
            }
            if (kind == "costonly")
                foreach (var name in views.Keys.ToList()) { string n = name; Add("frames_" + n, () => FrameStep(n)); }
            if (kind == "decals2")
            {
                Add("decal_variants", DecalVariants);
                Add("decal_counts2", DecalCounts2);
            }
            if (kind == "snow3") Add("snow3_" + Convert.ToString(spec["preRead"], CultureInfo.InvariantCulture), () => SnowParams("pre_" + Convert.ToString(spec["preRead"], CultureInfo.InvariantCulture), null));
            if (kind == "snow2")
            {
                Add("snow2_inherent", () => SnowParams("inherent", null));
                Add("snow2_runtime", () => SnowParams("runtime", "red"));
                Add("snow2_channels", SnowChannels);
                Add("snow2_vanilla_texture", () => SnowParams("vanillaTexture", "vanilla"));
            }
            if (kind == "snow")
            {
                Add("snow_baseline", () => ShotsOf("baseline", "snow_topdown", "map38_nw", "map38_hill"));
                Add("season_switches", SeasonSwitches);
                Add("dynamic_snow", DynamicSnow);
                Add("forced_snow", ForcedSnow);
                Add("color_grade_manager", ColorGradeManager);
            }
        }

        private void Add(string name, Func<IEnumerator> body)
        {
            if (skip.Contains(label + ":" + name) || skip.Contains(kind + ":" + name)) { steps.Add(new Dictionary<string, object> { { "name", name }, { "status", "skipped" } }); return; }
            plan.Add(new KeyValuePair<string, Func<IEnumerator>>(name, body));
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            if (Complete) return;
            LoadingWindow.DisableGlobalLoadingWindow();
            double now = context.Seconds;
            lastFrameDelta = lastFrame < 0 ? 0 : now - lastFrame;
            lastFrame = now;
            frameCount++;
            int compiling = Utilities.GetNumberOfShaderCompilationsInProgress();
            if (compiling > maxCompilations) maxCompilations = compiling;
            try { scene.Tick(dt); }
            catch (Exception error) { Failure = error; Complete = true; return; }
            while (true)
            {
                if (current == null)
                {
                    if (++planIndex >= plan.Count) { Complete = true; return; }
                    currentStep = new Dictionary<string, object> { { "name", plan[planIndex].Key }, { "startSeconds", now } };
                    steps.Add(currentStep);
                    context.Stage(label + "_" + plan[planIndex].Key);
                    stepStart = now;
                    try { current = plan[planIndex].Value(); }
                    catch (Exception error) { EndStep(error); continue; }
                }
                bool more;
                try
                {
                    if (now - stepStart > StepTimeout) throw new TimeoutException("Step exceeded " + StepTimeout + " s (loading state " + scene.GetLoadingStateName() + ").");
                    more = current.MoveNext();
                }
                catch (Exception error) { EndStep(error); continue; }
                if (!more) { EndStep(null); continue; }
                return;
            }
        }

        private double lastFrameDelta;

        private void EndStep(Exception error)
        {
            currentStep["status"] = error == null ? "ok" : "failed";
            if (error != null) currentStep["error"] = error.GetType().Name + ": " + error.Message;
            currentStep["seconds"] = context.Seconds - stepStart;
            current = null;
            context.Checkpoint();
        }

        // ------------------------------------------------------------------ helpers
        private bool Ready { get { return layer.ReadyToRender() && Utilities.GetNumberOfShaderCompilationsInProgress() == 0 && scene.IsLoadingFinished(); } }

        private IEnumerator WaitReady(int stable)
        {
            int count = 0;
            while (count < stable)
            {
                count = Ready ? count + 1 : 0;
                yield return null;
            }
        }

        private IEnumerator Wait(int framesToWait) { for (int i = 0; i < framesToWait; i++) yield return null; }

        private void Aim(Dictionary<string, object> view)
        {
            if (view == null) return;
            var eye = MapEpic2Probe.Floats(view["eye"]); var target = MapEpic2Probe.Floats(view["target"]);
            float fov = Convert.ToSingle(view["fov"], CultureInfo.InvariantCulture);
            bool down = Convert.ToString(view["kind"], CultureInfo.InvariantCulture) == "topdown";
            camera.LookAt(new Vec3(eye[0], eye[1], eye[2]), new Vec3(target[0], target[1], target[2]), down ? new Vec3(0, 1, 0) : Vec3.Up);
            camera.SetFovVertical(fov, (float)Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, 6000f);
            if (layer != null) layer.SetCamera(camera);
        }

        private IEnumerator Shot(string id, string viewName)
        {
            Dictionary<string, object> view;
            if (!views.TryGetValue(viewName, out view)) throw new KeyNotFoundException("view " + viewName);
            Aim(view);
            var e = WaitReady(Settle); while (e.MoveNext()) yield return e.Current;
            string file = Path.Combine(context.Directory, "map2-" + context.ProcessId + "-" + label + "-" + id + ".bmp");
            Utilities.TakeScreenshot(file);
            var record = new Dictionary<string, object> { { "id", id }, { "view", viewName }, { "path", file }, { "eye", view["eye"] }, { "target", view["target"] },
                { "fov", view["fov"] }, { "viewKind", view["kind"] }, { "seconds", context.Seconds }, { "frame", frameCount } };
            shots.Add(record);
            context.Outputs[label + "_" + id + "Screenshot"] = file;
            e = Wait(4); while (e.MoveNext()) yield return e.Current;
            record["fileExists"] = File.Exists(file);
            context.Checkpoint();
        }

        private IEnumerator Measure(string id, string viewName, Dictionary<string, object> extra)
        {
            Dictionary<string, object> view;
            if (!views.TryGetValue(viewName, out view)) throw new KeyNotFoundException("view " + viewName);
            Aim(view);
            var e = WaitReady(Settle); while (e.MoveNext()) yield return e.Current;
            var deltas = new List<double>();
            double mainFps = 0, rendererFps = 0;
            int notReady = 0;
            for (int i = 0; i < Frames; i++)
            {
                yield return null;
                deltas.Add(lastFrameDelta * 1000.0);
                mainFps += Utilities.GetMainFps(); rendererFps += Utilities.GetRendererFps();
                if (!Ready) notReady++;
            }
            deltas.Sort();
            var record = new Dictionary<string, object> { { "id", id }, { "view", viewName }, { "frames", deltas.Count },
                { "meanMs", deltas.Average() }, { "p50Ms", deltas[deltas.Count / 2] }, { "p95Ms", deltas[(int)(deltas.Count * .95)] }, { "maxMs", deltas[deltas.Count - 1] },
                { "rendererFps", Utilities.GetRendererFps() }, { "mainFps", Utilities.GetMainFps() }, { "framesNotReady", notReady },
                { "meanMainFps", mainFps / Frames }, { "meanRendererFps", rendererFps / Frames }, { "meanMainMs", 1000.0 * Frames / Math.Max(mainFps, 1e-6) },
                { "cpuMemoryMb", Utilities.GetCurrentCpuMemoryUsageMB() }, { "gpuMemoryMb", Utilities.GetGPUMemoryMB() } };
            if (extra != null) foreach (var pair in extra) record[pair.Key] = pair.Value;
            frames.Add(record);
            context.Checkpoint();
        }

        private IEnumerator FrameStep(string viewName) { return Measure(viewName, viewName, null); }
        private IEnumerator ShotStep(string viewName) { return Shot(viewName, viewName); }

        private IEnumerator ShotsOf(string prefix, params string[] names)
        {
            foreach (var name in names)
            {
                if (!views.ContainsKey(name)) continue;
                var e = Shot(prefix + "_" + name, name); while (e.MoveNext()) yield return e.Current;
            }
        }

        private IEnumerator LoadMetrics()
        {
            var load = (Dictionary<string, object>)report["load"];
            double start = context.Seconds;
            var e = WaitReady(Settle); while (e.MoveNext()) yield return e.Current;
            load["readyAfterSeconds"] = context.Seconds - start;
            load["maxShaderCompilationsInProgress"] = maxCompilations;
            load["loadingState"] = scene.GetLoadingStateName();
            load["terrainMemory"] = scene.GetTerrainMemoryUsage();
            load["floraInstances"] = scene.GetFloraInstanceCount();
            load["floraRendererTextureUsage"] = scene.GetFloraRendererTextureUsage();
            load["navMeshFaces"] = scene.GetNavMeshFaceCount();
            load["cpuMemoryMbReady"] = Utilities.GetCurrentCpuMemoryUsageMB();
            load["gpuMemoryMbReady"] = Utilities.GetGPUMemoryMB();
            load["centerHeight"] = scene.GetTerrainHeight(new Vec2(800, 800));
            load["hillHeight"] = scene.GetTerrainHeight(new Vec2(1400, 200));
            context.Checkpoint();
        }

        private string FirstView(params string[] names) { foreach (var n in names) if (views.ContainsKey(n)) return n; return views.Keys.First(); }

        // ------------------------------------------------------------------ seasons
        private IEnumerator SeasonSwitches()
        {
            var record = new Dictionary<string, object>();
            report["seasons"] = record;
            record["seasonTimeFactorBefore"] = MBMapScene.GetSeasonTimeFactor(scene);
            record["winterTimeFactorBefore"] = scene.GetWinterTimeFactor();
            string[] names = kind == "snow" ? new[] { "snow_topdown", "map38_nw" } : new[] { "node11_topdown", "overview" };
            var e = ShotsOf("season0", names); while (e.MoveNext()) yield return e.Current;
            MBMapScene.SetSeasonTimeFactor(scene, 1f);
            record["seasonTimeFactorSet"] = MBMapScene.GetSeasonTimeFactor(scene);
            e = Wait(10); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("seasonfactor1", names); while (e.MoveNext()) yield return e.Current;
            MBMapScene.SetSeasonTimeFactor(scene, 0f);
            scene.SetWinterTimeFactor(1f);
            record["winterTimeFactorSet"] = scene.GetWinterTimeFactor();
            e = Wait(10); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("wintertime1", names); while (e.MoveNext()) yield return e.Current;
            scene.SetWinterTimeFactor(0f);
            e = Wait(10); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("reset", names[0]); while (e.MoveNext()) yield return e.Current;
            record["seasonTimeFactorAfter"] = MBMapScene.GetSeasonTimeFactor(scene);
            record["winterTimeFactorAfter"] = scene.GetWinterTimeFactor();
        }

        // ------------------------------------------------------------------ decals
        private static readonly string[] PathMaterials = { "decal_empire_path_a", "decal_empire_path_b", "decal_khuzait_path_a", "decal_khuzait_path_b", "decal_battania_path_a", "decal_battania_path_b" };
        private static readonly float[][] PathArguments = { new[] { .53f, .53f, .48f, .01f }, new[] { .53f, .53f, 0f, .01f }, new[] { .53f, .53f, .5f, .01f },
            new[] { .53f, .53f, 0f, .01f }, new[] { .53f, .53f, .48f, .01f }, new[] { .53f, .53f, -.01f, .01f } };

        private MatrixFrame DecalFrame(float x, float y, float yaw, float halfLength, float halfWidth, float halfDepth)
        {
            float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw);
            var rot = new Mat3(new Vec3(c * halfLength, s * halfLength, 0), new Vec3(-s * halfWidth, c * halfWidth, 0), new Vec3(0, 0, halfDepth));
            return new MatrixFrame(rot, new Vec3(x, y, scene.GetTerrainHeight(new Vec2(x, y))));
        }

        private Decal SceneDecal(Material material, float[] argument, MatrixFrame frame)
        {
            var decal = Decal.CreateDecal(null);
            decal.SetMaterial(material);
            scene.AddDecalInstance(decal, DecalSet, true);
            decal.SetIsVisible(true);
            decal.SetVectorArgument(argument[0], argument[1], argument[2], argument[3]);
            decal.Frame = frame;
            decals.Add(decal);
            return decal;
        }

        private IEnumerator DecalTechniques()
        {
            var record = new Dictionary<string, object>();
            report["decalTechniques"] = record;
            string view = FirstView("decal_hill");
            var e = Shot("d0_baseline", view); while (e.MoveNext()) yield return e.Current;
            var path = Material.GetFromResource(PathMaterials[0]);
            record["pathMaterialFound"] = path != null;
            record["pathMaterialShader"] = path == null || path.GetShader() == null ? null : path.GetShader().Name;
            // D1: entity-less scene decals (NavalDLC foam pattern) in a line over the hill flank at y = 160.
            var watch = Stopwatch.StartNew();
            var line = new List<object>();
            for (int i = 0; i < 16; i++)
            {
                float x = 1381f + 2.5f * i, y = 160f;
                SceneDecal(path, PathArguments[0], DecalFrame(x, y, .15f * i, 1.5f, 1.5f, 3f));
                line.Add(new[] { x, y, scene.GetTerrainHeight(new Vec2(x, y)) });
            }
            record["d1"] = new Dictionary<string, object> { { "technique", "Decal.CreateDecal(null) + SetMaterial + Scene.AddDecalInstance('editor_set') + Frame" },
                { "count", 16 }, { "halfSize", 1.5 }, { "centers", line }, { "createMs", watch.Elapsed.TotalMilliseconds } };
            e = Shot("d1_sceneDecals", view); while (e.MoveNext()) yield return e.Current;
            // D2: Material.CreateCopy + SetTexture(DiffuseMap, a runtime CreateFromByteArray checker), y = 170.
            var pixels = new byte[64 * 64 * 4];
            for (int py = 0; py < 64; py++)
                for (int px = 0; px < 64; px++)
                {
                    bool a = ((px / 8) + (py / 8)) % 2 == 0;
                    int o = 4 * (py * 64 + px);
                    pixels[o] = (byte)(a ? 255 : 0); pixels[o + 1] = (byte)(a ? 0 : 255); pixels[o + 2] = 255; pixels[o + 3] = 255;
                }
            var texture = Texture.CreateFromByteArray(pixels, 64, 64);
            textures.Add(texture);
            var copy = path.CreateCopy();
            copy.SetTexture(Material.MBTextureType.DiffuseMap, texture);
            var copied = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                float x = 1388f + 6f * i, y = 171f;
                SceneDecal(copy, new[] { 1f, 1f, 0f, 0f }, DecalFrame(x, y, 0, 2f, 2f, 3f));
                copied.Add(new[] { x, y });
            }
            record["d2"] = new Dictionary<string, object> { { "technique", "Material.CreateCopy + SetTexture(DiffuseMap, Texture.CreateFromByteArray RGBA 64x64 magenta/cyan checker)" },
                { "count", 4 }, { "halfSize", 2.0 }, { "centers", copied }, { "texture", new[] { texture.Width, texture.Height } }, { "pixelOrder", "R,G,B,A bytes: magenta (255,0,255) / cyan (0,255,255)" } };
            e = Shot("d2_runtimeTexture", view); while (e.MoveNext()) yield return e.Current;
            // D3: an entity carrying the decal component (the scene-file decal_component path), y = 150.
            var entityRecord = new List<object>();
            for (int i = 0; i < 6; i++)
            {
                float x = 1385f + 5f * i, y = 150f;
                var entity = GameEntity.CreateEmpty(scene, false);
                var decal = Decal.CreateDecal(null);
                decal.SetMaterial(path);
                decal.SetVectorArgument(PathArguments[0][0], PathArguments[0][1], PathArguments[0][2], PathArguments[0][3]);
                entity.AddComponent(decal);
                var frame = DecalFrame(x, y, 0, 1.5f, 1.5f, 3f);
                entity.SetGlobalFrame(in frame);
                entities.Add(entity);
                entityRecord.Add(new[] { x, y });
            }
            record["d3"] = new Dictionary<string, object> { { "technique", "GameEntity.CreateEmpty + AddComponent(Decal)" }, { "count", 6 }, { "centers", entityRecord } };
            e = Shot("d3_entityDecals", view); while (e.MoveNext()) yield return e.Current;
            // D4: mesh fallback: a [-1,1]^3 box mesh with the decal material (the deferred decal shader projects inside the box), y = 140.
            var meshRecord = new List<object>();
            for (int i = 0; i < 6; i++)
            {
                float x = 1385f + 5f * i, y = 140f;
                var mesh = Mesh.CreateMeshWithMaterial(path);
                var handle = mesh.LockEditDataWrite();
                try { AddBox(mesh, handle); }
                finally { mesh.UnlockEditDataWrite(handle); }
                mesh.CullingMode = MBMeshCullingMode.None;
                mesh.SetVectorArgument(PathArguments[0][0], PathArguments[0][1], PathArguments[0][2], PathArguments[0][3]);
                mesh.ComputeNormals(); mesh.UpdateBoundingBox();
                var entity = GameEntity.CreateEmpty(scene, false);
                entity.AddMesh(mesh);
                var frame = DecalFrame(x, y, 0, 1.5f, 1.5f, 3f);
                entity.SetGlobalFrame(in frame);
                entities.Add(entity);
                meshRecord.Add(new[] { x, y });
            }
            record["d4"] = new Dictionary<string, object> { { "technique", "Mesh.CreateMeshWithMaterial(decal material) box [-1,1]^3 on an entity" }, { "count", 6 }, { "centers", meshRecord } };
            e = Shot("d4_meshDecals", view); while (e.MoveNext()) yield return e.Current;
        }

        private static void AddBox(Mesh mesh, UIntPtr handle)
        {
            var p = new Vec3[8];
            for (int i = 0; i < 8; i++) p[i] = new Vec3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (var f in faces)
            {
                mesh.AddTriangle(p[f[0]], p[f[1]], p[f[2]], new Vec2(0, 0), new Vec2(1, 0), new Vec2(1, 1), 0xffffffff, handle);
                mesh.AddTriangle(p[f[0]], p[f[2]], p[f[3]], new Vec2(0, 0), new Vec2(1, 1), new Vec2(0, 1), 0xffffffff, handle);
            }
        }

        private IEnumerator DecalCounts()
        {
            var record = new Dictionary<string, object>();
            report["decalCounts"] = record;
            string view = FirstView("decal_flat");
            var rows = new List<object>();
            record["rows"] = rows;
            int before = decals.Count;
            var e = Measure("count0", view, new Dictionary<string, object> { { "decals", 0 } }); while (e.MoveNext()) yield return e.Current;
            e = Shot("count0", view); while (e.MoveNext()) yield return e.Current;
            var fallback = Material.GetFromResource(PathMaterials[0]);
            var materials = PathMaterials.Select(n => Material.GetFromResource(n) ?? fallback).ToArray();
            record["materialsFound"] = PathMaterials.Where(n => Material.GetFromResource(n) != null).ToList();
            var rng = new System.Random(249);
            var placed = new List<float[]>();
            int made = 0;
            foreach (int target in new[] { 200, 1000, 3000 })
            {
                var watch = Stopwatch.StartNew();
                for (; made < target; made++)
                {
                    float x = 570f + (float)rng.NextDouble() * 60f, y = 585f + (float)rng.NextDouble() * 60f, yaw = (float)(rng.NextDouble() * Math.PI * 2);
                    int m = made % materials.Length;
                    SceneDecal(materials[m], PathArguments[m], DecalFrame(x, y, yaw, .45f, .45f, 2f));
                    placed.Add(new[] { x, y, yaw, m });
                }
                double ms = watch.Elapsed.TotalMilliseconds;
                rows.Add(new Dictionary<string, object> { { "decals", target }, { "createMs", ms } });
                e = Measure("count" + target, view, new Dictionary<string, object> { { "decals", target }, { "createMs", ms } }); while (e.MoveNext()) yield return e.Current;
                e = Shot("count" + target, view); while (e.MoveNext()) yield return e.Current;
            }
            // Removal of the counted decals, then re-creation of the first 200 at the same frames.
            var removeWatch = Stopwatch.StartNew();
            for (int i = decals.Count - 1; i >= before; i--) { scene.RemoveDecalInstance(decals[i], DecalSet); decals.RemoveAt(i); }
            record["removeMs"] = removeWatch.Elapsed.TotalMilliseconds;
            e = Measure("removed", view, new Dictionary<string, object> { { "decals", 0 }, { "afterRemoval", true } }); while (e.MoveNext()) yield return e.Current;
            e = Shot("removed", view); while (e.MoveNext()) yield return e.Current;
            var again = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                var p = placed[i]; int m = (int)p[3];
                SceneDecal(materials[m], PathArguments[m], DecalFrame(p[0], p[1], p[2], .45f, .45f, 2f));
            }
            record["recreate200Ms"] = again.Elapsed.TotalMilliseconds;
            e = Shot("recreated200", view); while (e.MoveNext()) yield return e.Current;
        }

        // ------------------------------------------------------------------ snow
        private Texture SnowTexture(int channel, bool waterMask)
        {
            const int n = 256;
            var bytes = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int o = 4 * (y * n + x);
                    byte v = 0;
                    if (y < 128 && x < 128) v = 255;                       // memory rows 0-127, columns 0-127
                    else if (y >= 200 && y < 216 && x < 64) v = 255;      // a bar in the lower-left memory quadrant (transpose check)
                    else if (y >= 128 && x >= 128) v = (byte)((x - 128) * 2); // a ramp along x in the lower-right memory quadrant
                    bytes[o + 3] = 255;
                    if (waterMask) { bytes[o] = 255; if (y < 128 && x < 128) bytes[o + 2] = 255; }
                    else bytes[o + channel] = v;
                }
            var texture = Texture.CreateFromByteArray(bytes, n, n);
            textures.Add(texture);
            return texture;
        }

        private IEnumerator DynamicSnow()
        {
            var record = new Dictionary<string, object>();
            report["dynamicSnow"] = record;
            record["threadId"] = Thread.CurrentThread.ManagedThreadId;
            record["pattern"] = "256x256 RGBA bytes, rows first: rows 0-127 x columns 0-127 = 255; rows 200-215 x columns 0-63 = 255; rows 128-255 x columns 128-255 = ramp 2(x-128)";
            var red = SnowTexture(0, false);
            scene.SetDynamicSnowTexture(red);
            record["boundRed"] = true;
            var steps = new List<object>();
            record["steps"] = steps;
            foreach (var p in new[] { new[] { .55f, .65f, 0f }, new[] { .225f, .65f, 0f }, new[] { -.1f, .65f, 0f } })
            {
                MBMapScene.SetTerrainDynamicParams(scene, new Vec3(p[0], p[1], p[2]));
                string id = "red_x" + p[0].ToString("0.000", CultureInfo.InvariantCulture).Replace("-", "m").Replace(".", "p");
                steps.Add(new Dictionary<string, object> { { "id", id }, { "params", p } });
                var e = Wait(6); while (e.MoveNext()) yield return e.Current;
                e = ShotsOf(id, "snow_topdown"); while (e.MoveNext()) yield return e.Current;
            }
            var w = ShotsOf("red_winter", "map38_nw", "map38_se", "map38_hill"); while (w.MoveNext()) yield return w.Current;
            foreach (var test in new[] { new object[] { "green", 1, false }, new object[] { "alpha", 3, false }, new object[] { "bluewater", 0, true } })
            {
                var texture = SnowTexture((int)test[1], (bool)test[2]);
                scene.SetDynamicSnowTexture(texture);
                MBMapScene.SetTerrainDynamicParams(scene, new Vec3(-.1f, .65f, 0f));
                var e = Wait(6); while (e.MoveNext()) yield return e.Current;
                e = ShotsOf((string)test[0], "snow_topdown"); while (e.MoveNext()) yield return e.Current;
            }
            // Leave the red pattern bound at mid season for the colour-grade manager step.
            scene.SetDynamicSnowTexture(red);
            MBMapScene.SetTerrainDynamicParams(scene, new Vec3(.55f, .65f, 0f));
        }

        // ------------------------------------------------------------------ run 2
        private IEnumerator DecalRow(string id, string view, Dictionary<string, object> record, Func<float, float, Decal> make, float y, int count, float step)
        {
            var row = new List<object>();
            for (int i = 0; i < count; i++)
            {
                float x = 600f - step * (count - 1) / 2f + step * i;
                var made = make(x, y);
                row.Add(new object[] { x, y, made != null });
            }
            record[id] = row;
            var e = Shot(id, view); while (e.MoveNext()) yield return e.Current;
        }

        private IEnumerator DecalVariants()
        {
            var record = new Dictionary<string, object>();
            report["decalVariants"] = record;
            string view = FirstView("decal_rows");
            var e = Shot("v0_baseline", view); while (e.MoveNext()) yield return e.Current;
            var path = Material.GetFromResource(PathMaterials[0]);
            var pixels = new byte[64 * 64 * 4];
            for (int py = 0; py < 64; py++)
                for (int px = 0; px < 64; px++)
                {
                    bool a = ((px / 8) + (py / 8)) % 2 == 0;
                    int o = 4 * (py * 64 + px);
                    pixels[o] = (byte)(a ? 255 : 0); pixels[o + 1] = (byte)(a ? 0 : 255); pixels[o + 2] = 255; pixels[o + 3] = 255;
                }
            var texture = Texture.CreateFromByteArray(pixels, 64, 64);
            textures.Add(texture);
            var copyTextured = path.CreateCopy();
            copyTextured.SetTexture(Material.MBTextureType.DiffuseMap, texture);
            var copyPlain = path.CreateCopy();
            // Rows 6 units apart in front of the map38 camera at (600, 600).
            e = DecalRow("v1_copyTextureRegistered", view, record, (x, y) => { var d = SceneDecal(copyTextured, new[] { 1f, 1f, 0f, 0f }, DecalFrame(x, y, 0, 2f, 2f, 3f)); d.CheckAndRegisterToDecalSet(); return d; }, 592f, 4, 7f);
            while (e.MoveNext()) yield return e.Current;
            e = DecalRow("v2_copyPlain", view, record, (x, y) => SceneDecal(copyPlain, PathArguments[0], DecalFrame(x, y, 0, 2f, 2f, 3f)), 598f, 4, 7f);
            while (e.MoveNext()) yield return e.Current;
            e = DecalRow("v3_factorTint", view, record, (x, y) => { var d = SceneDecal(path, PathArguments[0], DecalFrame(x, y, 0, 2f, 2f, 3f)); d.SetFactor1(0xFFFF00FF); return d; }, 604f, 4, 7f);
            while (e.MoveNext()) yield return e.Current;
            e = DecalRow("v4_entityRegistered", view, record, (x, y) =>
            {
                var entity = GameEntity.CreateEmpty(scene, false);
                var decal = Decal.CreateDecal(null);
                decal.SetMaterial(path);
                decal.SetVectorArgument(PathArguments[0][0], PathArguments[0][1], PathArguments[0][2], PathArguments[0][3]);
                entity.AddComponent(decal);
                var frame = DecalFrame(x, y, 0, 2f, 2f, 3f);
                entity.SetGlobalFrame(in frame);
                decal.CheckAndRegisterToDecalSet();
                entities.Add(entity);
                return decal;
            }, 610f, 4, 7f);
            while (e.MoveNext()) yield return e.Current;
            var overlays = new List<object>();
            record["v5_materials"] = overlays;
            string[] names = { "decal_city_ground", "mainmap_decal_undervillage_desert", "mainmap_decal_undervillage", "mainmap_cracked_mud_a" };
            int slot = 0;
            e = DecalRow("v5_groundOverlays", view, record, (x, y) =>
            {
                string name = names[slot++ % names.Length];
                var material = Material.GetFromResource(name);
                overlays.Add(new object[] { name, material != null });
                return material == null ? null : SceneDecal(material, new[] { 1f, 1f, 0f, 0f }, DecalFrame(x, y, 0, 3f, 3f, 3f));
            }, 618f, 4, 7f);
            while (e.MoveNext()) yield return e.Current;
        }

        private IEnumerator DecalCounts2()
        {
            var record = new Dictionary<string, object>();
            report["decalCounts2"] = record;
            string view = FirstView("decal_counts");
            var e = Measure("c0", view, new Dictionary<string, object> { { "decals", 0 } }); while (e.MoveNext()) yield return e.Current;
            var fallback = Material.GetFromResource(PathMaterials[0]);
            var materials = PathMaterials.Select(n => Material.GetFromResource(n) ?? fallback).ToArray();
            var rng = new System.Random(2490);
            int made = 0, before = decals.Count;
            var rows = new List<object>(); record["rows"] = rows;
            foreach (int target in new[] { 1000, 3000, 7500 })
            {
                var watch = Stopwatch.StartNew();
                for (; made < target; made++)
                {
                    float x = 570f + (float)rng.NextDouble() * 60f, y = 285f + (float)rng.NextDouble() * 60f, yaw = (float)(rng.NextDouble() * Math.PI * 2);
                    int m = made % materials.Length;
                    SceneDecal(materials[m], PathArguments[m], DecalFrame(x, y, yaw, .45f, .45f, 2f));
                }
                rows.Add(new Dictionary<string, object> { { "decals", target }, { "createMs", watch.Elapsed.TotalMilliseconds } });
                e = Measure("c" + target, view, new Dictionary<string, object> { { "decals", target } }); while (e.MoveNext()) yield return e.Current;
            }
            e = Shot("c7500", view); while (e.MoveNext()) yield return e.Current;
            var removeWatch = Stopwatch.StartNew();
            for (int i = decals.Count - 1; i >= before; i--) { scene.RemoveDecalInstance(decals[i], DecalSet); decals.RemoveAt(i); }
            record["remove7500Ms"] = removeWatch.Elapsed.TotalMilliseconds;
            e = Measure("c0_after", view, new Dictionary<string, object> { { "decals", 0 }, { "afterRemoval", true } }); while (e.MoveNext()) yield return e.Current;
        }

        private IEnumerator SnowParams(string prefix, string texture)
        {
            var record = new Dictionary<string, object>();
            report["snow2_" + prefix] = record;
            if (texture == "red") { scene.SetDynamicSnowTexture(SnowTexture(0, false)); record["bound"] = "runtime red pattern"; }
            if (texture == "vanilla")
            {
                var vanilla = Texture.GetFromResource("main_map_snow_flowmap");
                record["vanillaFound"] = vanilla != null;
                if (vanilla == null) yield break;
                record["vanillaSize"] = new[] { vanilla.Width, vanilla.Height };
                scene.SetDynamicSnowTexture(vanilla);
                record["bound"] = "main_map_snow_flowmap";
            }
            foreach (var p in new[] { new[] { .55f, .65f, 0f }, new[] { -.1f, .65f, 0f } })
            {
                MBMapScene.SetTerrainDynamicParams(scene, new Vec3(p[0], p[1], p[2]));
                string season = p[0] > 0 ? "summer" : "winter";
                var e = Wait(6); while (e.MoveNext()) yield return e.Current;
                foreach (var v in new[] { "q_nw", "q_ne", "q_sw", "q_se" })
                {
                    if (prefix == "vanillaTexture" && season == "summer" && v != "q_nw") continue;
                    e = Shot(prefix + "_" + season + "_" + v, v); while (e.MoveNext()) yield return e.Current;
                }
                if (season == "winter") { e = Shot(prefix + "_winter_map38_nw", "map38_nw"); while (e.MoveNext()) yield return e.Current; }
            }
        }

        private IEnumerator SnowChannels()
        {
            foreach (var test in new[] { new object[] { "green", 1, false }, new object[] { "alpha", 3, false }, new object[] { "bluewater", 0, true } })
            {
                scene.SetDynamicSnowTexture(SnowTexture((int)test[1], (bool)test[2]));
                MBMapScene.SetTerrainDynamicParams(scene, new Vec3(-.1f, .65f, 0f));
                var e = Wait(6); while (e.MoveNext()) yield return e.Current;
                e = Shot((string)test[0] + "_winter_q_nw", "q_nw"); while (e.MoveNext()) yield return e.Current;
            }
        }

        private IEnumerator ForcedSnow()
        {
            scene.SetForcedSnow(true);
            var e = Wait(10); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("forcedsnow", "snow_topdown", "map38_nw"); while (e.MoveNext()) yield return e.Current;
            scene.SetForcedSnow(false);
        }

        private IEnumerator ColorGradeManager()
        {
            var record = new Dictionary<string, object>();
            report["colorGradeManager"] = record;
            var entity = GameEntity.CreateEmpty(scene, false);
            entities.Add(entity);
            entity.CreateAndAddScriptComponent("MapColorGradeManager", true);
            object script = null;
            foreach (var component in entity.GetScriptComponents()) { if (component.GetType().Name == "MapColorGradeManager") script = component; }
            record["created"] = script != null;
            if (script == null) yield break;
            var type = script.GetType();
            record["type"] = type.AssemblyQualifiedName;
            Set(type, script, "SeasonTimeFactor", 1f);
            Set(type, script, "TimeOfDay", 12f);
            var apply = type.GetMethod("ApplyAtmosphere", BindingFlags.Public | BindingFlags.Instance);
            if (apply != null) apply.Invoke(script, new object[] { false });
            record["appliedWinter"] = apply != null;
            var e = Wait(30); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("cgm_winter", "snow_topdown"); while (e.MoveNext()) yield return e.Current;
            Set(type, script, "SeasonTimeFactor", 0f);
            if (apply != null) apply.Invoke(script, new object[] { false });
            e = Wait(30); while (e.MoveNext()) yield return e.Current;
            e = ShotsOf("cgm_summer", "snow_topdown"); while (e.MoveNext()) yield return e.Current;
        }

        private static void Set(Type type, object target, string field, object value)
        {
            var f = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(target, value);
        }

        protected override void OnFinalize()
        {
            base.OnFinalize();
            var cleanup = new List<string>();
            report["cleanup"] = cleanup;
            try
            {
                foreach (var decal in decals) scene.RemoveDecalInstance(decal, DecalSet);
                cleanup.Add("removed " + decals.Count + " scene decals");
                foreach (var entity in entities) entity.Remove(0);
                cleanup.Add("removed " + entities.Count + " entities");
            }
            catch (Exception error) { cleanup.Add("decal/entity cleanup: " + error.Message); }
            decals.Clear(); entities.Clear();
            layer?.ClearAll();
            if (renderer != null) MBAgentRendererSceneController.DestructAgentRendererSceneController(scene, renderer, false);
            scene?.ClearAll();
            camera?.ReleaseCamera();
            foreach (var texture in textures) { try { texture.Release(); } catch (Exception) { } }
            textures.Clear();
            layer = null; renderer = null; camera = null; scene = null;
            Finalized = true; report["finalized"] = true;
            context.Stage(label + "_finalized");
            context.Checkpoint();
        }
    }
}
