using System;
using System.Collections.Generic;
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
    // Isolated raw-grid projection. This does not bind a campaign or replace native terrain-height queries.
    public sealed class HeightfieldProbeScreen : ScreenBase
    {
        private const int ChunkCells = 4;
        private readonly int seed;
        private readonly DiagnosticHeightfield field;
        private readonly string fieldHash;
        private readonly float spacing, heightScale, heightOffset;
        private readonly List<GCHandle> pins = new List<GCHandle>();
        private readonly List<object> picks = new List<object>();
        private Scene scene;
        private SceneLayer layer;
        private Camera camera;
        private int ticks;
        private float elapsed;
        private object[] measurements;
        private bool ready;
        private bool collisionEnabled;
        private readonly List<Action> registerCollision = new List<Action>();
        private readonly string run = Guid.NewGuid().ToString("N");

        public HeightfieldProbeScreen(int seed)
        {
            this.seed = seed;
            if (seed == 0)
            {
                field = DiagnosticHeightfield.Calibration();
                fieldHash = "calibration-v1";
                heightScale = 1; heightOffset = 0;
            }
            else
            {
                var markerPath = Path.Combine(SubModule.Store, "latest-heightfield-" + seed + ".json");
                if (!File.Exists(markerPath)) throw new InvalidOperationException("Generate this seed first.");
                var marker = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(markerPath));
                Guid job;
                if (!Guid.TryParseExact((string)marker["job"], "N", out job) || Convert.ToInt32(marker["seed"]) != seed)
                    throw new InvalidDataException("Invalid diagnostic job reference.");
                var path = Path.Combine(SubModule.Store, "jobs", job.ToString("N"), "diagnostic", "heightfield.bin");
                fieldHash = GenerationProbe.Hash(path);
                if (fieldHash != (string)marker["heightfieldHash"]) throw new InvalidDataException("Heightfield hash mismatch.");
                field = DiagnosticHeightfield.Read(path);
                heightScale = .002f; heightOffset = 20;
            }
            spacing = 128f / (field.Side - 1);
        }

        private string ReportPath { get { return Path.Combine(SubModule.Store, "heightfield-probe-" + seed + "-" + run + ".json"); } }
        private void Trace(string text)
        {
            File.AppendAllText(Path.Combine(SubModule.Store, "heightfield-progress.log"),
                DateTime.UtcNow.ToString("O") + " run=" + run + " seed=" + seed + " " + text + "\n");
        }
        private Vec3 Vertex(int x, int y) { return new Vec3(x * spacing, y * spacing, (float)field.At(x,y) * heightScale + heightOffset); }
        private uint Color(Vec3 point)
        {
            // Diagnostic height colors with corner variation; no production material claim.
            byte red = (byte)Math.Max(20, Math.Min(230, point.z * 5));
            byte green = (byte)Math.Max(40, Math.Min(220, 150 + point.y * .4 - point.z));
            byte blue = (byte)Math.Max(30, Math.Min(220, 190 - point.z * 4));
            return 0xff000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Trace("initialize");
            scene = Scene.CreateNewScene(true, true, DecalAtlasGroup.All, "mono_renderscene");
            var initialization = new SceneInitializationData(true) { InitPhysicsWorld = true };
            scene.Read("scn_item_tableau", ref initialization);
            scene.SetName("mnbl_heightfield_probe_" + run);
            scene.DisableStaticShadows(true);
            scene.SetDoNotWaitForLoadingStatesToRender(true);
            scene.SetDefaultLighting();
            var material = PhysicsMaterial.GetFromName("default");
            if (!material.IsValid) throw new InvalidOperationException("Default physics material unavailable.");
            for (int by = 0; by < field.Side - 1; by += ChunkCells)
                for (int bx = 0; bx < field.Side - 1; bx += ChunkCells) AddChunk(bx, by, material);
            var sunlight = new Vec3(1,1,1);
            scene.SetSun(ref sunlight, 1, 0, 10);
            camera = Camera.CreateCamera();
            camera.LookAt(new Vec3(170,-100,180), new Vec3(64,64,15), Vec3.Up);
            camera.SetFovVertical(.9f, Screen.RealScreenResolutionWidth / Screen.RealScreenResolutionHeight, .1f, 1000);
            layer = new SceneLayer();
            layer.SetScene(scene); layer.SetCamera(camera);
            layer.SetSceneUsesSkybox(false); layer.SetSceneUsesShadows(true); layer.SetRenderWithPostfx(false);
            layer.SceneView.SetForceShaderCompilation(false); layer.SceneView.SetClearGbuffer(true);
            layer.SetCleanScreenUntilLoadingDone(false);
            AddLayer(layer); ScreenManager.TrySetFocus(layer);
            Trace("initialized");
            InformationManager.DisplayMessage(new InformationMessage("Terrain diagnostic " + seed + ": click to compare picking; Escape returns. Auto return in 60 seconds."));
        }

        private void AddChunk(int bx, int by, PhysicsMaterial material)
        {
            var vertices = new List<Vec3>(); var indices = new List<int>();
            var mesh = Mesh.CreateMesh(true);
            mesh.SetMaterial("vertex_color_lighting_nav_mesh"); mesh.Color = 0xffffffff; mesh.CullingMode = MBMeshCullingMode.None;
            var editHandle = mesh.LockEditDataWrite();
            try
            {
            for (int y = by; y < Math.Min(by + ChunkCells, field.Side - 1); y++)
                for (int x = bx; x < Math.Min(bx + ChunkCells, field.Side - 1); x++)
                {
                    var a = Vertex(x,y); var b = Vertex(x+1,y); var c = Vertex(x,y+1); var d = Vertex(x+1,y+1);
                    int i = vertices.Count; vertices.AddRange(new[] { a,b,d,c });
                    indices.AddRange(new[] { i,i+1,i+3,i+2,i+3,i+1 });
                    mesh.AddTriangleWithVertexColors(a,b,c,new Vec2(0,0),new Vec2(1,0),new Vec2(0,1),Color(a),Color(b),Color(c),editHandle);
                    mesh.AddTriangleWithVertexColors(d,c,b,new Vec2(1,1),new Vec2(0,1),new Vec2(1,0),Color(d),Color(c),Color(b),editHandle);
                }
            }
            finally { mesh.UnlockEditDataWrite(editHandle); }
            mesh.ComputeNormals(); mesh.UpdateBoundingBox(); mesh.PreloadForRendering();
            var entity = GameEntity.CreateEmpty(scene); entity.AddMesh(mesh); entity.SetReadyToRender(true);
            var vertexPin = GCHandle.Alloc(vertices.ToArray(), GCHandleType.Pinned); pins.Add(vertexPin);
            var indexPin = GCHandle.Alloc(indices.ToArray(), GCHandleType.Pinned); pins.Add(indexPin);
            registerCollision.Add(() => {
            entity.ReplacePhysicsBodyWithQuadPhysicsBody(new UIntPtr(unchecked((ulong)vertexPin.AddrOfPinnedObject().ToInt64())), vertices.Count,
                material, BodyFlags.Moveable | BodyFlags.TwoSided,
                new UIntPtr(unchecked((ulong)indexPin.AddrOfPinnedObject().ToInt64())), indices.Count);
            var frame = MatrixFrame.Identity; entity.SetGlobalFrame(in frame); entity.SetPhysicsState(true,true);
            });
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt); LoadingWindow.DisableGlobalLoadingWindow();
            if (ticks == 0) Trace("before_first_tick");
            scene.Tick(dt);
            if (ticks == 0) Trace("after_first_tick");
            ticks++; elapsed += dt;
            if (!collisionEnabled && elapsed >= 5)
            {
                Trace("before_deferred_collision");
                foreach (var register in registerCollision) register();
                registerCollision.Clear(); collisionEnabled = true;
                Trace("after_deferred_collision");
            }
            if (ticks == 60)
            {
                Trace("visual_ready=" + layer.ReadyToRender());
            }
            if (collisionEnabled && measurements == null && elapsed >= 7)
            {
                var result = new List<object>();
                float n = field.Side - 1;
                foreach (var p in new[] { new Vec2(.2f,.2f), new Vec2(n-.2f,.2f), new Vec2(.2f,n-.2f),
                    new Vec2(n-.2f,n-.2f), new Vec2(n*.26f,n*.69f), new Vec2(n*.38f,n*.38f), new Vec2(n*.63f,n*.57f) })
                {
                    float x = p.x * spacing, y = p.y * spacing;
                    double expected = field.Sample(p.x,p.y) * heightScale + heightOffset;
                    float distance; Vec3 point;
                    Trace("before_landmark_ray " + p);
                    bool hit = scene.RayCastForClosestEntityOrTerrain(new Vec3(x,y,100),new Vec3(x,y,-100),out distance,out point,0);
                    Trace("after_landmark_ray " + hit);
                    result.Add(new { x,y,expected,nativeHit=hit,nativeHeight=hit ? (float?)point.z : null,
                        error=hit ? (double?)Math.Abs(point.z-expected) : null,
                        nativeTerrainHeight=scene.GetTerrainHeight(new Vec2(x,y)) });
                }
                measurements = result.ToArray(); ready = layer.ReadyToRender(); WriteReport(false); Trace("measurements_written");
            }
            if (measurements != null && layer.Input.IsKeyPressed(InputKey.LeftMouseButton))
            {
                var mouse = layer.Input.GetMousePositionRanged();
                var begin = Vec3.Zero; var end = Vec3.Zero;
                camera.ViewportPointToWorldRay(ref begin,ref end,layer.SceneView.ScreenPointToViewportPoint(mouse));
                float distance; Vec3 point;
                bool hit = scene.RayCastForClosestEntityOrTerrain(begin,end,out distance,out point,0);
                double fraction;
                bool mathematical = field.Intersect(begin.x/spacing,begin.y/spacing,(begin.z-heightOffset)/heightScale,
                    end.x/spacing,end.y/spacing,(end.z-heightOffset)/heightScale,out fraction);
                var expected = mathematical ? begin + (end-begin)*(float)fraction : Vec3.Zero;
                picks.Add(new { mouseX=mouse.x,mouseY=mouse.y,nativeHit=hit,mathematicalHit=mathematical,
                    x=hit?(float?)point.x:null,y=hit?(float?)point.y:null,z=hit?(float?)point.z:null,
                    error=hit&&mathematical?(float?)(point-expected).Length:null });
                WriteReport(false);
                InformationManager.DisplayMessage(new InformationMessage("Pick: native="+hit+", grid="+mathematical+
                    (hit&&mathematical ? ", error="+(point-expected).Length.ToString("F5") : "")));
            }
            if (ticks == 1) Trace("before_escape_input");
            if (elapsed >= 60 || layer.Input.IsKeyPressed(InputKey.Escape)) ScreenManager.PopScreen();
            if (ticks == 1) Trace("after_escape_input");
        }

        private void WriteReport(bool finalized)
        {
            File.WriteAllText(ReportPath,new JavaScriptSerializer().Serialize(new {schema="mnbl.heightfield-probe",version=1,
                processId=System.Diagnostics.Process.GetCurrentProcess().Id,run,seed,fieldHash,side=field.Side,spacing,heightScale,heightOffset,chunkCells=ChunkCells,
                triangles=2*(field.Side-1)*(field.Side-1),meshEditLock=true,ready,measurements,picks,finalized,
                tolerance=.02,campaignBackendAccepted=false,nativeTerrainHeightReplacementProven=false}));
        }
        protected override void OnFinalize()
        {
            base.OnFinalize();
            foreach (var pin in pins) if (pin.IsAllocated) pin.Free();
            pins.Clear(); camera?.ReleaseCamera(); WriteReport(true); Trace("finalized");
        }
    }
}
