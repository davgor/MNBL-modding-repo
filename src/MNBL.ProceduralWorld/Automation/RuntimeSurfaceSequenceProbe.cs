using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // All native calls stay on the application thread. Only generation/encoding and owned file IO
    // run in a worker. Each generated world owns an immutable, runtime-registered scene directory.
    internal static class RuntimeSurfaceSequenceProbe
    {
        internal const string Slot = "mnbl_runtime_surface";
        private const string ModuleId = "MNBL.ProceduralWorld";
        private static readonly string[] NativeFiles = { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" };

        internal static IEnumerator Run(ProbeContext context)
        {
            string module = new FileInfo(typeof(RuntimeSurfaceSequenceProbe).Assembly.Location).Directory.Parent.Parent.FullName;
            string sceneRoot = Path.Combine(module, "SceneObj");
            string template = Path.Combine(module, "SurfaceTemplate");
            PlainPath(sceneRoot); PlainPath(template);
            if (!Directory.Exists(sceneRoot) || !Directory.Exists(template)) throw new DirectoryNotFoundException("Module scene directory and generic SurfaceTemplate must be deployed before launch.");
            var reports = new List<object>();
            var queryFailures = new List<string>();
            context.Outputs["stages"] = reports;
            context.Outputs["queryFailureStages"] = queryFailures;
            context.Outputs["measureQueries"] = context.Bool("measureQueries", true);
            context.Outputs["scope"] = "Fresh calibration and FWGC outputs encoded after process launch, installed as immutable scenes and registered through the installed map-download API. Isolated native surface experiment, not campaign activation or routing acceptance.";
            context.Outputs["sceneStorage"] = "Unique directories retained as evidence; native file-cache eviction is not proven.";
            context.Outputs["tolerances"] = new { heightMeters = .05, rayMeters = .1, normalDegrees = 1, projectionPixels = 2 };
            string[] labels = { "calibration", "seed42", "seed43" };
            float[] extents = { 256, 800, 640 };
            int count = context.Int("stageCount", 3);
            if (count < 1 || count > 3) throw new ArgumentOutOfRangeException("stageCount");
            using (var cancellation = new CancellationTokenSource())
            {
                Action cancel = () => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
                Task<Bundle> work = null;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        int index = i;
                        context.Stage(labels[i] + "_generation_begin");
                        var token = cancellation.Token;
                        work = Task.Run(() => GenerateBundle(labels[index], index == 0 ? 0u : (uint)(41 + index), extents[index], template, token));
                        Task<Bundle> tracked = work;
                        context.TrackOwnedWork(cancel, () => tracked.IsCompleted);
                        while (!work.IsCompleted) yield return null;
                        if (work.IsFaulted) throw new InvalidOperationException("Runtime bundle generation failed.", work.Exception.GetBaseException());
                        if (work.IsCanceled) throw new OperationCanceledException();
                        Bundle bundle = work.Result;
                        var result = new Dictionary<string, object> {
                            { "label", labels[i] }, { "jobRoot", bundle.JobRoot }, { "extent", extents[i] },
                            { "sourceHash", GenerationProbe.Hash(bundle.Source) }, { "encoder", bundle.Encoder },
                            { "manifest", Json(File.ReadAllText(Path.Combine(bundle.Output, "surface.json"))) },
                            { "sourceGeneratedAfterLaunch", true }
                        };
                        reports.Add(result);
                        string target = Path.Combine(sceneRoot, bundle.SceneName);
                        context.Stage(labels[i] + "_install_fresh_scene");
                        result["installedFiles"] = Install(bundle.Output, target);
                        context.Stage(labels[i] + "_register_scene");
                        Utilities.PairSceneNameToModuleName(bundle.SceneName, ModuleId);
                        string resolved;
                        bool registered = Utilities.TryGetFullFilePathOfScene(bundle.SceneName, out resolved);
                        result["registeredSceneName"] = bundle.SceneName;
                        result["resolvedScenePath"] = resolved;
                        result["registrationResolved"] = registered;
                        if (!registered || String.IsNullOrEmpty(resolved)) throw new IOException("Runtime scene registration did not resolve.");
                        // Installed resolver returns a path relative to the shipping engine directory.
                        string resolvedAbsolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Scene).Assembly.Location), resolved));
                        string expectedAbsolute = Path.GetFullPath(Path.Combine(target, "scene.xscene"));
                        result["resolvedPathMatchesInstalled"] = String.Equals(resolvedAbsolute, expectedAbsolute, StringComparison.OrdinalIgnoreCase);
                        if (!(bool)result["resolvedPathMatchesInstalled"]) throw new IOException("Runtime scene resolver selected a different directory.");
                        context.Checkpoint();
                        var screen = new RuntimeSurfaceScreen(context, labels[i], bundle, result);
                        try
                        {
                            ScreenManager.PushScreen(screen);
                            while (!screen.Complete) yield return null;
                        }
                        finally
                        {
                            if (ScreenManager.TopScreen == screen) ScreenManager.PopScreen();
                            if (screen.Finalized)
                            {
                                context.Stage(labels[i] + "_clear_old_resources_after_pop");
                                // Installed GameStateScreenManager.OnPopState uses this after non-menu screens.
                                Utilities.ClearOldResourcesAndObjects();
                                result["clearOldResourcesAfterPop"] = true;
                            }
                        }
                        if (!screen.Finalized) throw new InvalidOperationException("Previous runtime surface screen did not finalize; replacement withheld.");
                        context.Checkpoint();
                        if (screen.Failure != null) throw screen.Failure;
                        if (!screen.QueriesPassed) queryFailures.Add(labels[i]);
                        // Allow native render commands from screen cleanup to retire before opening the next scene.
                        double released = context.Seconds;
                        while (context.Seconds - released < 2) yield return null;
                    }
                    context.Outputs["sequenceCompleted"] = true;
                    context.Outputs["queryChecksPassed"] = context.Bool("measureQueries", true) && queryFailures.Count == 0;
                    context.Outputs["nativeAllocationReclamationTiming"] = "Not directly observable; records native terrain-removal call, owned reference release and replacement query results.";
                    context.Checkpoint();
                    if (queryFailures.Count != 0) throw new InvalidOperationException("Native surface query mismatches: " + String.Join(", ", queryFailures) + ". Inspect persisted measurements.");
                }
                finally { if (work != null && !work.IsCompleted) cancel(); }
            }
        }

        internal sealed class Bundle
        {
            internal string JobRoot, Source, Output, SceneName;
            internal float Extent;
            internal Dictionary<string, object> Encoder;
        }

        private static Bundle GenerateBundle(string label, uint seed, float extent, string template, CancellationToken token)
        {
            string root = null;
            string source;
            if (seed == 0)
            {
                root = Path.Combine(SubModule.Store, "surface-jobs", Guid.NewGuid().ToString("N"));
                PlainPath(root);
                if (Directory.Exists(root)) throw new IOException("Fresh job already exists.");
                Directory.CreateDirectory(root);
                source = Path.Combine(root, "calibration.bin");
                using (var writer = new System.IO.BinaryWriter(File.Create(source)))
                {
                    writer.Write(Encoding.ASCII.GetBytes("MNBLHF01")); writer.Write(129);
                    for (int y = 0; y < 129; y++) for (int x = 0; x < 129; x++)
                    {
                        token.ThrowIfCancellationRequested();
                        double px = x * extent / 128, py = y * extent / 128;
                        double peak = 30 * Math.Exp(-((px - 160) * (px - 160) + (py - 90) * (py - 90)) / (2 * 25 * 25));
                        double d = py - .6 * px - 20;
                        double channel = -6 * Math.Exp(-d * d / (2 * 6 * 6));
                        double ridge = py >= 30 && py <= 220 ? 15 * Math.Exp(-(px - 200) * (px - 200) / (2 * 4 * 4)) : 0;
                        writer.Write(2 + .02 * px + .05 * py + peak + channel + ridge);
                    }
                }
            }
            else
            {
                GenerationProbe.RunDiagnostic(seed, SubModule.Store, token, "normal", null, value => root = value);
                source = Path.Combine(root, "diagnostic", "heightfield.bin");
            }
            token.ThrowIfCancellationRequested();
            string sceneName = "mnbl_generated_" + Guid.NewGuid().ToString("N");
            string localTemplate = Path.Combine(root, "surface-template");
            PlainPath(localTemplate);
            Directory.CreateDirectory(localTemplate);
            foreach (string file in NativeFiles) File.Copy(Path.Combine(template, file), Path.Combine(localTemplate, file), false);
            string sceneXml = Path.Combine(localTemplate, "scene.xscene");
            string xml = File.ReadAllText(sceneXml);
            string originalName = "<scene name=\"" + Slot + "\"";
            int nameAt = xml.IndexOf(originalName, StringComparison.Ordinal);
            if (nameAt < 0 || xml.IndexOf(originalName, nameAt + originalName.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Generic template must have exactly one expected scene name.");
            File.WriteAllText(sceneXml, xml.Replace(originalName, "<scene name=\"" + sceneName + "\""), new UTF8Encoding(false));
            string output = Path.Combine(root, "surface");
            string[] args = { "--surface-heightfield", source, "--surface-template", localTemplate, "--surface-output", output,
                "--surface-extent", extent.ToString(CultureInfo.InvariantCulture), "--surface-scale", seed == 0 ? "1" : "0.01" };
            var encoder = GenerationProbe.RunSurfaceEncoder(args, root, token);
            token.ThrowIfCancellationRequested();
            if (Convert.ToInt32(encoder["exitCode"]) != 0) throw new InvalidDataException("Surface encoder failed: " + root);
            foreach (string file in NativeFiles) if (!File.Exists(Path.Combine(output, file))) throw new FileNotFoundException("Surface encoder omitted " + file);
            DiagnosticHeightfield.Read(Path.Combine(output, "quantized-heightfield.bin"));
            return new Bundle { JobRoot = root, Source = source, Output = output, Extent = extent, Encoder = encoder, SceneName = sceneName };
        }

        private static object Json(string value) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(value); }

        private static List<object> Install(string source, string target)
        {
            PlainPath(source); PlainPath(target);
            var receipts = new List<object>();
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Immutable scene destination already exists.");
            Directory.CreateDirectory(target);
            foreach (string name in NativeFiles)
            {
                string input = Path.Combine(source, name), output = Path.Combine(target, name);
                PlainPath(input); PlainPath(output);
                string expected = GenerationProbe.Hash(input);
                File.Copy(input, output, false);
                string actual = GenerationProbe.Hash(output);
                if (actual != expected) throw new IOException("Surface slot copy hash mismatch: " + name);
                receipts.Add(new { file = name, sha256 = actual });
            }
            return receipts;
        }

        private static void PlainPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Redirected surface path: " + current);
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        private sealed class RuntimeSurfaceScreen : ScreenBase
        {
            private readonly ProbeContext context;
            private readonly string label;
            private readonly Bundle bundle;
            private readonly Dictionary<string, object> result;
            private readonly List<object> measurements = new List<object>();
            private readonly List<object> cursorPicks = new List<object>();
            private Scene scene;
            private SceneLayer layer;
            private Camera camera;
            private MBAgentRendererSceneController renderer;
            private DiagnosticHeightfield heights;
            private int readyFrames, zoom;
            private double start, capture = -1;
            internal bool Complete, Finalized;
            internal bool QueriesPassed = true;
            internal Exception Failure;

            internal RuntimeSurfaceScreen(ProbeContext context, string label, Bundle bundle, Dictionary<string, object> result)
            { this.context = context; this.label = label; this.bundle = bundle; this.result = result; }

            protected override void OnInitialize()
            {
                base.OnInitialize();
                start = context.Seconds;
                result["measurements"] = measurements;
                result["cursorPicks"] = cursorPicks;
                heights = DiagnosticHeightfield.Read(Path.Combine(bundle.Output, "quantized-heightfield.bin"));
                Stage("create");
                scene = Scene.CreateNewScene(true, true, DecalAtlasGroup.All, "mono_renderscene");
                renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
                var init = new SceneInitializationData(true);
                init.InitPhysicsWorld = true;
                result["initPhysicsWorld"] = true;
                Stage("read");
                scene.Read(bundle.SceneName, ModuleId, ref init, "");
                result["sceneName"] = scene.GetName();
                if (scene.GetName() != bundle.SceneName) throw new InvalidDataException("Runtime read selected a different scene.");
                scene.SetUseConstantTime(true); scene.SetOcclusionMode(true);
                Stage("optimize"); scene.OptimizeScene(true, true);
                camera = Camera.CreateCamera(); SetCamera(0);
                layer = new SceneLayer(); layer.SetScene(scene); layer.SetCamera(camera);
                layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                layer.SetSceneUsesShadows(true); layer.SetSceneUsesSkybox(true);
                layer.SceneView.SetForceShaderCompilation(true); layer.SceneView.SetClearGbuffer(true);
                AddLayer(layer); ScreenManager.TrySetFocus(layer);
                scene.Tick(0); context.Checkpoint();
            }

            private void Stage(string name) { context.Stage(label + "_" + name); }
            private void SetCamera(int level)
            {
                float e = bundle.Extent, scale = level == 0 ? 1 : .65f;
                camera.LookAt(new Vec3(e * .5f, e * (.5f - .85f * scale), e * .85f * scale), new Vec3(e * .5f, e * .5f, 0), Vec3.Up);
                camera.SetFovVertical(.9f, Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, e * 5);
                if (layer != null) layer.SetCamera(camera);
            }

            protected override void OnFrameTick(float dt)
            {
                base.OnFrameTick(dt);
                if (Complete) return;
                try
                {
                    LoadingWindow.DisableGlobalLoadingWindow();
                    if (context.Seconds - start > 180) throw new TimeoutException("Surface readiness: " + scene.GetLoadingStateName());
                    if (!layer.ReadyToRender() || Utilities.GetNumberOfShaderCompilationsInProgress() > 0 || !scene.IsLoadingFinished()) { readyFrames = 0; return; }
                    scene.Tick(0);
                    readyFrames++;
                    if (readyFrames == 30)
                    {
                        result["loadingState"] = scene.GetLoadingStateName(); result["readyToRender"] = true;
                        result["isLoadingFinished"] = true; result["stableReadyFrames"] = readyFrames;
                        Stage("queries_zoom" + zoom);
                        if (context.Bool("measureQueries", true))
                        {
                            var report = RuntimeSurfaceQueries.Measurements(scene, layer.SceneView, camera, heights, bundle.Extent,
                                name => { Stage(name); context.Checkpoint(); },
                                pending => { pending["zoom"] = zoom; measurements.Add(pending); context.Checkpoint(); });
                            if (!(bool)report["passed"]) QueriesPassed = false;
                            context.Checkpoint();
                            if (report.ContainsKey("exception")) throw new InvalidOperationException("Native query sequence stopped after a managed exception; inspect persisted report.");
                        }
                        string path = Path.Combine(context.Directory, "surface-" + context.ProcessId + "-" + label + "-zoom" + zoom + ".bmp");
                        Utilities.TakeScreenshot(path); result["screenshotZoom" + zoom] = path;
                        context.Outputs[label + "Zoom" + zoom + "Screenshot"] = path;
                        capture = context.Seconds;
                        context.Checkpoint();
                    }
                    if (readyFrames >= 30 && context.Bool("measureQueries", true) && layer.Input.IsKeyPressed(InputKey.LeftMouseButton))
                    {
                        var mouse = layer.Input.GetMousePositionRanged();
                        var pick = RuntimeSurfaceQueries.ObserveCursor(scene, layer.SceneView, heights, bundle.Extent, Stage);
                        pick["zoom"] = zoom; pick["mouseRanged"] = new[] { mouse.x, mouse.y };
                        pick["seconds"] = context.Seconds;
                        if (!(bool)pick["passed"]) QueriesPassed = false;
                        cursorPicks.Add(pick); Stage("cursor_click_recorded"); context.Checkpoint();
                    }
                    if (capture >= 0 && context.Seconds - capture >= context.Num("holdSeconds", 10))
                    {
                        if (zoom == 0) { zoom = 1; SetCamera(zoom); readyFrames = 0; capture = -1; }
                        else Complete = true;
                    }
                }
                catch (Exception error) { Failure = error; result["failure"] = error.ToString(); Complete = true; context.Checkpoint(); }
            }

            protected override void OnFinalize()
            {
                base.OnFinalize();
                var cleanup = new List<string>(); result["cleanup"] = cleanup;
                Stage("layer_clear_terrain");
                if (layer != null) { layer.ClearAll(); cleanup.Add("SceneLayer.ClearAll -> SceneView.ClearAll(true,true)"); }
                Stage("renderer_destruct");
                if (renderer != null) { MBAgentRendererSceneController.DestructAgentRendererSceneController(scene, renderer, false); cleanup.Add("DestructAgentRendererSceneController(false)"); }
                Stage("scene_clear_all");
                if (scene != null) { scene.ClearAll(); cleanup.Add("Scene.ClearAll (installed MapScreen teardown precedent)"); }
                if (camera != null) { camera.ReleaseCamera(); cleanup.Add("ReleaseCamera"); }
                layer = null; renderer = null; camera = null; scene = null; heights = null;
                cleanup.Add("owned scene/layer/renderer/camera/heightfield references cleared");
                Finalized = true; result["finalized"] = true;
                Stage("finalized"); context.Checkpoint();
            }
        }
    }
}
