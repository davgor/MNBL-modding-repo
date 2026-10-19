using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Screens;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // An isolated rendering/query experiment. It never starts or modifies a campaign.
    public sealed class SurfaceProbeScreen : ScreenBase
    {
        private Scene scene;
        private SceneLayer layer;
        private Camera camera;
        private readonly int seed;
        private readonly bool testNativeRay;
        private int ticks;
        private float elapsed;
        private GCHandle collisionVertices;
        private GCHandle collisionIndices;

        private void Trace(string stage)
        {
            File.AppendAllText(Path.Combine(SubModule.Store, "render-progress.log"),
                DateTime.UtcNow.ToString("O") + " seed=" + seed + " " + stage + "\n");
        }

        public SurfaceProbeScreen(int seed, bool testNativeRay = false) { this.seed = seed; this.testNativeRay = testNativeRay; }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Trace("initialize");
            scene = Scene.CreateNewScene(true, true, DecalAtlasGroup.All, "mono_renderscene");
            scene.SetName("mnbl_render_probe");
            if (testNativeRay)
            {
                var initialization = new SceneInitializationData(true);
                initialization.InitPhysicsWorld = true;
                Trace("before_physics_template");
                scene.Read("scn_item_tableau", ref initialization);
                Trace("after_physics_template");
            }
            // Tableau-style loading: preload our mesh without compiling an empty terrain.
            scene.DisableStaticShadows(true);
            scene.SetDoNotWaitForLoadingStatesToRender(true);
            scene.SetDefaultLighting();
            var mesh = Mesh.CreateMesh(true);
            mesh.SetMaterial("vertex_color_lighting_nav_mesh");
            if (mesh.GetMaterial() == null) throw new InvalidOperationException("Probe material was not loaded.");
            mesh.Color = 0xffffffff;
            mesh.CullingMode = MBMeshCullingMode.None;
            var a = new Vec3(0, 0, 1);
            var b = new Vec3(10, 0, 2);
            var c = new Vec3(0, 10, seed - 35);
            mesh.AddTriangleWithVertexColors(a, b, c, new Vec2(0,0), new Vec2(1,0), new Vec2(0,1),
                0xff3060ff, 0xff30ff60, 0xffff6030, UIntPtr.Zero);
            var d = new Vec3(10, 10, 3);
            if (testNativeRay)
                mesh.AddTriangleWithVertexColors(d, c, b, new Vec2(1,1), new Vec2(0,1), new Vec2(1,0),
                    0xffffffff, 0xffff6030, 0xff30ff60, UIntPtr.Zero);
            mesh.ComputeNormals();
            mesh.UpdateBoundingBox();
            mesh.PreloadForRendering();
            var entity = GameEntity.CreateEmpty(scene);
            entity.AddMesh(mesh);
            entity.SetReadyToRender(true);
            if (testNativeRay)
            {
                // Installed War Sails ShipAttachment uses pinned Vec3[] and int[] and
                // retains the pins until its entities are removed. Keep ours until scene teardown.
                collisionVertices = GCHandle.Alloc(new[] { a, b, d, c }, GCHandleType.Pinned);
                collisionIndices = GCHandle.Alloc(new[] { 0, 1, 3, 2, 3, 1 }, GCHandleType.Pinned);
                var material = PhysicsMaterial.GetFromName("default");
                if (!material.IsValid) throw new InvalidOperationException("Default physics material unavailable.");
                Trace("before_quad_collision");
                entity.ReplacePhysicsBodyWithQuadPhysicsBody(
                    new UIntPtr(unchecked((ulong)collisionVertices.AddrOfPinnedObject().ToInt64())), 4,
                    material, BodyFlags.TwoSided | BodyFlags.Moveable,
                    new UIntPtr(unchecked((ulong)collisionIndices.AddrOfPinnedObject().ToInt64())), 6);
                var identity = MatrixFrame.Identity;
                entity.SetGlobalFrame(in identity);
                entity.SetPhysicsState(true, true);
                var control = GameEntity.CreateEmpty(scene);
                control.AddSphereAsBody(new Vec3(20, 2, 3), 1, BodyFlags.Moveable | BodyFlags.OnlyCollideWithRaycast);
                control.SetGlobalFrame(in identity);
                Trace("after_quad_collision");
            }
            var sunlight = new Vec3(1, 1, 1);
            scene.SetSun(ref sunlight, 1, 0, 10);
            camera = Camera.CreateCamera();
            camera.LookAt(new Vec3(15,-18,20), new Vec3(3,3,3), Vec3.Up);
            camera.SetFovVertical(0.9f, Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, 1000);
            layer = new SceneLayer();
            layer.SetScene(scene);
            layer.SetCamera(camera);
            layer.SetSceneUsesSkybox(false);
            layer.SetSceneUsesShadows(true);
            layer.SetRenderWithPostfx(false);
            layer.SceneView.SetForceShaderCompilation(false);
            layer.SceneView.SetClearGbuffer(true);
            layer.SetCleanScreenUntilLoadingDone(false);
            AddLayer(layer);
            ScreenManager.TrySetFocus(layer);
            Trace("initialized");
            InformationManager.DisplayMessage(new InformationMessage("Surface diagnostic seed " + seed + ". Returns to menu after 30 seconds."));
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            LoadingWindow.DisableGlobalLoadingWindow();
            if (ticks == 0) Trace("before_first_tick");
            scene.Tick(dt);
            if (ticks == 0) Trace("after_first_tick");
            if (++ticks == 60 || ticks == 1200)
            {
                bool? hit = null;
                float? distance = null;
                bool? sphereHit = null;
                float? sphereDistance = null;
                if (testNativeRay)
                {
                    Trace("before_raycast");
                    float nativeDistance;
                    hit = scene.RayCastForClosestEntityOrTerrain(new Vec3(2,2,30), new Vec3(2,2,-10), out nativeDistance);
                    Trace("after_raycast hit=" + hit);
                    if (!float.IsNaN(nativeDistance) && !float.IsInfinity(nativeDistance)) distance = nativeDistance;
                    float controlDistance;
                    sphereHit = scene.RayCastForClosestEntityOrTerrain(new Vec3(20,2,30), new Vec3(20,2,-10), out controlDistance);
                    if (!float.IsNaN(controlDistance) && !float.IsInfinity(controlDistance)) sphereDistance = controlDistance;
                    Trace("sphere_raycast hit=" + sphereHit);
                }
                Trace("before_ready_query");
                bool ready = layer.ReadyToRender();
                string loadingState = scene.GetLoadingStateName();
                Trace("after_ready_query=" + ready + " loading=" + loadingState);
                File.WriteAllText(Path.Combine(SubModule.Store, "render-probe-" + seed + ".json"),
                    new JavaScriptSerializer().Serialize(new { schema = "mnbl.render-probe", version = 2, seed,
                        processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        variant = testNativeRay ? "moveable-quad-and-sphere-control" : "mesh-preload-no-terrain-check",
                        ticks, readyToRender = ready, loadingState, nativeRayExecuted = testNativeRay, nativeRayHit = hit, distance,
                        sphereHit, sphereDistance,
                        expectedTriangleHeight = 1f + .2f + .2f * (seed - 36),
                        visualInspectionRequired = true, campaignBackendAccepted = false }));
                Trace("measurement_written");
            }
            elapsed += dt;
            if (elapsed >= 30 || layer.Input.IsKeyPressed(InputKey.Escape)) ScreenManager.PopScreen();
        }

        protected override void OnFinalize()
        {
            base.OnFinalize(); // SceneLayer clears its owned native scene.
            if (collisionVertices.IsAllocated) collisionVertices.Free();
            if (collisionIndices.IsAllocated) collisionIndices.Free();
            camera?.ReleaseCamera();
            Trace("finalized");
        }
    }
}
