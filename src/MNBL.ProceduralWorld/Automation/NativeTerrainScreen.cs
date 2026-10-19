using System;
using System.Collections;
using System.IO;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // Follows installed VisualTestsScreen: real terrain Read, agent renderer,
    // OptimizeScene, SceneLayer, readiness gate, then Tick and screenshot.
    internal sealed class NativeTerrainScreen : ScreenBase
    {
        private readonly ProbeContext context;
        private Scene scene;
        private SceneLayer layer;
        private Camera camera;
        private MBAgentRendererSceneController renderer;
        private int readyFrames, view;
        private double started, captured = -1;
        // Camera views "eyeX,eyeY,eyeZ,targetX,targetY;..." in world units (MMR-26); empty keeps the single overview.
        private readonly System.Collections.Generic.List<float[]> views = new System.Collections.Generic.List<float[]>();
        private readonly System.Collections.Generic.List<string> captures = new System.Collections.Generic.List<string>();
        internal bool Complete;
        internal Exception Failure;
        internal NativeTerrainScreen(ProbeContext context) { this.context = context; }

        internal static IEnumerator Run(ProbeContext context)
        {
            var screen = new NativeTerrainScreen(context);
            ScreenManager.PushScreen(screen);
            while (!screen.Complete) yield return null;
            if (ScreenManager.TopScreen == screen) ScreenManager.PopScreen();
            if (screen.Failure != null) throw screen.Failure;
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            started = context.Seconds;
            string name = context.Str("sceneName", "mnbl_world_b");
            if (!name.StartsWith("mnbl_", StringComparison.Ordinal)) throw new ArgumentException("Only owned scenes allowed.");
            context.Stage("native_render_create");
            scene = Scene.CreateNewScene(true, true, DecalAtlasGroup.All, "mono_renderscene");
            renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
            var init = new SceneInitializationData(true);
            context.Stage("native_render_read " + name);
            scene.Read(name, ref init);
            context.Outputs["sceneName"] = scene.GetName();
            Vec2i nodes; float size; int layers, version;
            scene.GetTerrainData(out nodes, out size, out layers, out version);
            float extent = nodes.X * size;
            context.Outputs["extent"] = extent;
            context.Outputs["navFaces"] = scene.GetNavMeshFaceCount();
            context.Outputs["centerHeight"] = scene.GetTerrainHeight(new Vec2(extent / 2, extent / 2));
            context.Stage("native_render_optimize");
            scene.SetUseConstantTime(true);
            scene.SetOcclusionMode(true);
            scene.OptimizeScene(true, true);
            camera = Camera.CreateCamera();
            foreach (string spec in context.Str("views", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = spec.Split(',');
                if (parts.Length != 5) throw new ArgumentException("views entries are eyeX,eyeY,eyeZ,targetX,targetY");
                views.Add(Array.ConvertAll(parts, v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (views.Count == 0) views.Add(new[] { extent * .5f, -extent * .35f, extent * .85f, extent * .5f, extent * .5f });
            Aim(0, scene);
            camera.SetFovVertical(.9f, Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, extent * 5);
            layer = new SceneLayer();
            layer.SetScene(scene);
            layer.SetCamera(camera);
            layer.SetSceneUsesShadows(true);
            layer.SetSceneUsesSkybox(true);
            layer.SceneView.SetForceShaderCompilation(true);
            layer.SceneView.SetClearGbuffer(true);
            AddLayer(layer);
            context.Stage("native_render_initial_tick");
            scene.Tick(0);
            context.Checkpoint();
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            try
            {
                LoadingWindow.DisableGlobalLoadingWindow();
                if (context.Seconds - started > 150) throw new TimeoutException("Native terrain renderer: " + scene.GetLoadingStateName());
                if (!layer.ReadyToRender()) return;
                if (Utilities.GetNumberOfShaderCompilationsInProgress() > 0) return;
                scene.Tick(0);
                readyFrames++;
                if (readyFrames == 30 && captured < 0)
                {
                    string path = Path.Combine(context.Directory, "terrain-" + context.ProcessId + (view == 0 ? "" : "-" + view) + ".bmp");
                    context.Stage("native_render_capture " + view);
                    Utilities.TakeScreenshot(path);
                    captures.Add(path);
                    if (view == 0) context.Outputs["screenshot"] = path;
                    context.Outputs["screenshots"] = captures;
                    context.Outputs["readyToRender"] = true;
                    context.Outputs["loadingState"] = scene.GetLoadingStateName();
                    context.Outputs["readyFrames"] = readyFrames;
                    context.Checkpoint();
                    if (view + 1 < views.Count) { Aim(++view, scene); readyFrames = 0; }
                    else captured = context.Seconds;
                }
                if (captured >= 0 && context.Seconds - captured > 10) Complete = true;
            }
            catch (Exception error) { Failure = error; Complete = true; }
        }

        private void Aim(int index, Scene target)
        {
            var v = views[index];
            camera.LookAt(new Vec3(v[0], v[1], v[2]), new Vec3(v[3], v[4], target.GetTerrainHeight(new Vec2(v[3], v[4]))), Vec3.Up);
            if (layer != null) layer.SetCamera(camera);
        }

        protected override void OnFinalize()
        {
            base.OnFinalize();
            layer?.ClearAll();
            if (renderer != null) MBAgentRendererSceneController.DestructAgentRendererSceneController(scene, renderer, false);
            camera?.ReleaseCamera();
            context.Stage("native_render_finalized");
        }
    }
}
