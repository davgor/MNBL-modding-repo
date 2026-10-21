using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.World
{
    // MMR-291: veins, rivers and magic water as overlay meshes on the campaign map (docs/overlay-meshes.md; OverlayMeshes is the pure model; the
    // route with its IL and shader evidence: C:\Users\davgo\m2\results\MMR-291-route.md). Engine thread only; nothing here throws into the engine.
    //   * Rivers follow the map dressing's lifetime: MapDressingRuntime.Install leaves the water decals the meshes replace out of its installer
    //     (RiverDecalsReplaced: rivers, pools and, by policy, fills) and then calls InstallRivers; RefreshImages and Release go along.
    //   * Veins and magic water are a vein layer of MagicGroundRuntime (Layers, after the features and the magic water decals): Install at the
    //     map's AfterLoad, Changed at a live cast's safe point and at each reveal step (a new graph is planned again, the installer recreates only
    //     the chunk meshes whose geometry changed, a reveal step adds its band's meshes), RefreshImages after a camera wrap, Release(true) on the
    //     live scene and Release(false) after Scene.ClearAll (MMR-262: no engine call). It declares IMagicWaterVeins, so the ground overlay plans
    //     with the water veins drawn. While a layer is drawn as meshes its decals are not drawn (MagicGroundRuntime, MagicWaterRuntime); a layer
    //     whose materials cannot resolve keeps its decals (Ready).
    //   * A mesh: Mesh.CreateMeshWithMaterial, LockEditDataWrite, AddFaceCorner per vertex (position in the chunk's frame, uv, vertex colour),
    //     AddFace per triangle, ComputeNormals, ComputeTangents, RecomputeBoundingBox, UnlockEditDataWrite (TaleWorlds' BoundaryWallView order);
    //     the factor colour (Mesh.Color) and the vector argument (0, 0, 0, glow); one GameEntity.CreateEmpty root per mesh with AddMesh, framed at
    //     the chunk's image (x) and origin y, SetReadyToRender. Removal: Scene.RemoveEntity.
    //   * Materials: a published material by name when it resolves, else a copy of the vanilla template (roads.json overlayMeshes) with the
    //     layer's published textures (SetTexture DiffuseMap, DiffuseMap2 for the self-illumination, BumpMap, SpecularMap), as SandBox.View's
    //     MobilePartyVisual makes its campaign-map banner materials; rivers: the first vanilla water material that resolves. MMR-291-W: then the
    //     layer's pass against the water (OverlayPass: magic water writes depth behind an alpha test, so the water drawn after it keeps off).
    internal sealed class OverlayMeshRuntime : IMagicVeinLayer, IMagicWaterVeins
    {
        public static readonly OverlayMeshRuntime Instance = new OverlayMeshRuntime();
        public static readonly List<string> Errors = new List<string>();
        private static RoadsPolicy.OverlayPolicy policy;
        private static readonly Dictionary<string, bool> ready = new Dictionary<string, bool>(StringComparer.Ordinal);

        private OverlayMeshInstaller rivers, magic;
        private EngineTarget riverTarget, magicTarget;
        private OverlayMeshPlan magicFull;
        private MagicVeinGraph graph;
        private Func<double, double> image, riverImage;
        private double riverMs, magicMs;
        private string riverStatus = "none", magicStatus = "none";
        // The river surfaces drawn (null without river meshes): the in-game water check's cover.
        public static OverlayRiverSurface RiverSurface { get; private set; }
        public static OverlayMeshPlan RiverPlan { get; private set; }

        public static RoadsPolicy.OverlayPolicy Policy
        {
            get
            {
                if (policy == null) { try { policy = RoadsPolicy.Installed().Overlay; } catch (Exception e) { Note("policy", e); } }
                return policy;
            }
        }

        // A layer is drawn as meshes when the policy says so and its materials can resolve (checked once per process: materials are resources).
        public static bool Ready(string layer)
        {
            var p = Policy;
            if (p == null) return false;
            bool on = layer == OverlayLayer.Veins ? p.DrawsVeins : layer == OverlayLayer.MagicWater ? p.DrawsMagicWater : p.DrawsRivers;
            if (!on) return false;
            bool r;
            if (ready.TryGetValue(layer, out r)) return r;
            try
            {
                if (layer == OverlayLayer.Rivers) r = p.RiverMaterials.Any(m => Material.GetFromResource(m) != null);
                else
                {
                    bool veins = layer == OverlayLayer.Veins;
                    string published = veins ? RoadsPolicy.OverlayPolicy.Named(p.VeinMaterial, "fire") : p.WaterMaterial;
                    string textures = veins ? RoadsPolicy.OverlayPolicy.Named(p.VeinTextures, "fire") : p.WaterTextures;
                    r = Material.GetFromResource(published) != null || (Material.GetFromResource(veins ? p.VeinTemplate : p.WaterTemplate) != null && Texture.CheckAndGetFromResource(textures + "_d") != null);
                }
            }
            catch (Exception e) { Note("ready " + layer, e); r = false; }
            ready[layer] = r;
            return r;
        }
        public static bool DrawsVeins { get { return Ready(OverlayLayer.Veins); } }
        public static bool DrawsMagicWater { get { return Ready(OverlayLayer.MagicWater); } }
        public static bool DrawsRivers { get { return Ready(OverlayLayer.Rivers); } }
        public bool DrawsWaterStamps { get { return DrawsMagicWater; } }

        // The water decals the river meshes replace (MapDressingInstaller.SkipWater), or null while the rivers stay decals.
        public static Func<RoadDecal, bool> RiverDecalsReplaced()
        {
            if (!DrawsRivers) return null;
            bool fills = Policy.RiverFills;
            return d => d.Kind == "river" || d.Kind == "pool" || (fills && d.Kind == "fill");
        }

        // Pieces (stamps, chains, discs) a layer draws now; a layer's meshes hidden or shown (MMR-247's toggle check); a point's river cover.
        public static int Pieces(string layer)
        {
            var i = layer == OverlayLayer.Rivers ? Instance.rivers : Instance.magic;
            return i == null ? 0 : i.Pieces(layer);
        }
        public static bool SetVisible(string layer, bool visible)
        {
            var i = layer == OverlayLayer.Rivers ? Instance.rivers : Instance.magic;
            if (i == null) return false;
            try { i.SetVisible(layer, visible); return true; }
            catch (Exception e) { Note("visible", e); return false; }
        }
        public static bool RiverCovers(double x, double y) { var s = RiverSurface; return s != null && Instance.rivers != null && s.Covers(x, y); }

        // MMR-247 (c29, c31, c33): a layer at a screenshot: drawn as meshes, its live entities and pieces, hidden or not, and the entities (and
        // their pieces) whose drawn rectangle (the image x, the origin y, the mesh bounds) meets the window of half extents (hx, hy) round the
        // physical point (x, y).
        public static Dictionary<string, object> InView(string layer, double x, double y, double hx, double hy)
        {
            var i = layer == OverlayLayer.Rivers ? Instance.rivers : Instance.magic;
            var r = new Dictionary<string, object> { ["layer"] = layer, ["meshes"] = Ready(layer), ["window"] = new[] { x - hx, y - hy, x + hx, y + hy } };
            int entities = 0, pieces = 0, inView = 0, inViewPieces = 0;
            if (i != null)
                foreach (var kv in i.DrawnMeshes())
                {
                    var m = kv.Key; double ix = kv.Value;
                    if (m.Layer != layer) continue;
                    entities++; pieces += m.PieceIds.Count;
                    if (ix + m.MaxX >= x - hx && ix + m.MinX <= x + hx && m.OriginY + m.MaxY >= y - hy && m.OriginY + m.MinY <= y + hy) { inView++; inViewPieces += m.PieceIds.Count; }
                }
            r["entities"] = entities; r["pieces"] = pieces; r["inView"] = inView; r["inViewPieces"] = inViewPieces; r["hidden"] = i != null && i.HiddenLayers.Contains(layer);
            return r;
        }

        public static Dictionary<string, object> Record()
        {
            var p = Policy;
            var r = new Dictionary<string, object> { ["policy"] = p == null ? null : new Dictionary<string, object> { ["enabled"] = p.Enabled, ["veins"] = p.VeinMode, ["magicWater"] = p.WaterMode, ["rivers"] = p.RiverMode,
                    ["passes"] = new Dictionary<string, object> { ["veins"] = p.VeinPass, ["magicWater"] = p.WaterPass, ["alphaTest"] = p.AlphaTest } },
                ["ready"] = new Dictionary<string, object>(ready.ToDictionary(kv => kv.Key, kv => (object)kv.Value)), ["errors"] = Errors.ToList() };
            r["rivers"] = Part(Instance.rivers, Instance.riverTarget, Instance.riverStatus, Instance.riverMs);
            r["magic"] = Part(Instance.magic, Instance.magicTarget, Instance.magicStatus, Instance.magicMs);
            return r;
        }
        private static Dictionary<string, object> Part(OverlayMeshInstaller i, EngineTarget t, string status, double ms)
        {
            var r = i == null ? new Dictionary<string, object>() : i.Record();
            r["status"] = status; r["installMs"] = ms;
            if (t != null) r["materials"] = t.Materials;
            return r;
        }

        // ---- rivers: the map dressing's lifetime ----
        internal static void InstallRivers(Scene scene, MapDressingPlan plan, Func<double, double> imageOf)
        {
            var self = Instance;
            ReleaseRivers(false);
            if (scene == null || plan == null || !DrawsRivers) { self.riverStatus = "none: " + (scene == null || plan == null ? "no map dressing" : "rivers stay decals (policy or no river material)"); return; }
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                OverlayRiverSurface surface;
                var built = OverlayMeshes.Rivers(plan, plan.Ground, plan.Extent, Policy, out surface);
                self.riverTarget = new EngineTarget(scene, Policy);
                self.rivers = new OverlayMeshInstaller(self.riverTarget);
                self.riverImage = imageOf;
                self.rivers.Sync(built, imageOf);
                RiverPlan = built; RiverSurface = surface;
                self.riverMs = watch.Elapsed.TotalMilliseconds; self.riverStatus = "installed";
            }
            catch (Exception e) { Note("install rivers", e); self.riverStatus = "failed: " + e.GetType().Name + ": " + e.Message; }
        }
        internal static void RefreshRivers(Func<double, double> imageOf)
        {
            var self = Instance;
            self.riverImage = imageOf;
            if (self.rivers == null) return;
            try { self.rivers.Refresh(imageOf); } catch (Exception e) { Note("refresh rivers", e); }
        }
        internal static void ReleaseRivers(bool native)
        {
            var self = Instance;
            var drawn = self.rivers;
            self.rivers = null; self.riverTarget = null; RiverPlan = null; RiverSurface = null;
            if (drawn == null) return;
            try { if (native) drawn.Remove(); else drawn.Drop(); }
            catch (Exception e) { Note(native ? "remove rivers" : "drop rivers", e); }
        }

        // ---- veins and magic water: a vein layer of the magic ground overlay ----
        public void Install(Scene scene, MagicVeinGraph veins, double hours, Func<double, double> imageOf)
        {
            Release(false);
            if (scene == null || veins == null) { magicStatus = "none: no map or vein graph"; return; }
            if (!DrawsVeins && !DrawsMagicWater) { magicStatus = "none: veins and magic water stay decals (policy or no material)"; return; }
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                image = imageOf;
                magicTarget = new EngineTarget(scene, Policy);
                magic = new OverlayMeshInstaller(magicTarget);
                Plan(veins);
                magic.Sync(magicFull.Revealed(hours), image);
                magicMs = watch.Elapsed.TotalMilliseconds; magicStatus = "installed";
            }
            catch (Exception e) { Note("install", e); magicStatus = "failed: " + e.GetType().Name + ": " + e.Message; }
        }

        // The full plan of a graph: MagicGroundRuntime's ground plan (made before its layers run) and the magic water plan over the shown lakes.
        private void Plan(MagicVeinGraph veins)
        {
            var roads = RoadsPolicy.Installed();
            var ground = MagicGroundRuntime.Plan;
            if (ground == null || !ReferenceEquals(ground.Graph, veins)) throw new InvalidOperationException("the magic ground plan is not the vein graph's");
            var dressing = MapDressingRuntime.Current;
            var lakes = dressing == null ? (IList<GeneratedWaterSystem.Lake>)new GeneratedWaterSystem.Lake[0] : dressing.Plan.Lakes;
            var water = DrawsMagicWater ? MagicWater.Plan(veins, lakes, roads.MagicWater, roads.Magic, ground.Dropped) : null;
            // A layer whose materials cannot resolve stays decals: plan only what is drawn as meshes.
            magicFull = OverlayMeshes.Magic(ground, water, lakes, Policy, DrawsVeins, DrawsMagicWater);
            graph = veins;
        }

        public void Changed(MagicVeinGraph veins, double hours)
        {
            if (magic == null || veins == null) return;
            try
            {
                if (!ReferenceEquals(veins, graph)) Plan(veins);
                magic.Sync(magicFull.Revealed(hours), image);
            }
            catch (Exception e) { Note("change", e); }
        }

        public void RefreshImages(Func<double, double> imageOf)
        {
            image = imageOf;
            if (magic == null) return;
            try { magic.Refresh(imageOf); } catch (Exception e) { Note("refresh", e); }
        }

        public void Release(bool native)
        {
            var drawn = magic;
            magic = null; magicTarget = null; magicFull = null; graph = null;
            if (drawn == null) return;
            try { if (native) drawn.Remove(); else drawn.Drop(); }
            catch (Exception e) { Note(native ? "remove" : "drop", e); }
        }

        private static void Note(string step, Exception e) { if (Errors.Count < 20) Errors.Add(step + ": " + e.GetType().Name + ": " + e.Message); }

        // The engine adapter: one root entity per mesh.
        internal sealed class EngineTarget : IOverlayMeshTarget
        {
            private sealed class Drawn { public GameEntity Entity; public Mesh Mesh; }
            private readonly Scene scene;
            private readonly RoadsPolicy.OverlayPolicy p;
            private readonly Dictionary<string, Material> cache = new Dictionary<string, Material>(StringComparer.Ordinal);
            private readonly List<object> keep = new List<object>();   // the textures and copies the materials use
            public readonly SortedDictionary<string, object> Materials = new SortedDictionary<string, object>(StringComparer.Ordinal);
            public EngineTarget(Scene scene, RoadsPolicy.OverlayPolicy p) { this.scene = scene; this.p = p; }

            private Material MaterialOf(OverlayMesh m)
            {
                Material mat;
                if (cache.TryGetValue(m.Material, out mat)) return mat;
                var rec = new Dictionary<string, object>(); Materials[m.Material] = rec;
                try
                {
                    if (m.Layer == OverlayLayer.Rivers)
                    {
                        // "river": the policy's materials in order; "river:<name>": that one (the decal lab's fallback row)
                        var names = m.Material.StartsWith("river:", StringComparison.Ordinal) ? new List<string> { m.Material.Substring(6) } : p.RiverMaterials;
                        foreach (var name in names) { mat = Material.GetFromResource(name); if (mat != null) { rec["material"] = name; break; } }
                        if (mat == null) rec["missing"] = String.Join(", ", names);
                    }
                    else
                    {
                        bool veins = m.Layer == OverlayLayer.Veins;
                        string pass;
                        OverlayPass.Split(m.Material, out pass);
                        pass = pass ?? p.Pass(m.Layer);
                        string published = veins ? RoadsPolicy.OverlayPolicy.Named(p.VeinMaterial, m.Look) : p.WaterMaterial;
                        mat = Material.GetFromResource(published);
                        if (mat != null) { rec["published"] = published; mat = Pass(mat, pass, true, rec); }
                        else
                        {
                            string templateName = veins ? p.VeinTemplate : p.WaterTemplate, textures = veins ? RoadsPolicy.OverlayPolicy.Named(p.VeinTextures, m.Look) : p.WaterTextures;
                            var template = Material.GetFromResource(templateName);
                            Texture d = Texture.CheckAndGetFromResource(textures + "_d"), n = Texture.CheckAndGetFromResource(textures + "_n"), s = Texture.CheckAndGetFromResource(textures + "_s");
                            rec["template"] = template == null ? null : templateName; rec["diffuse"] = d == null ? null : d.Name; rec["normal"] = n == null ? null : n.Name; rec["specular"] = s == null ? null : s.Name;
                            if (template != null && d != null)
                            {
                                mat = template.CreateCopy();
                                mat.SetTexture(Material.MBTextureType.DiffuseMap, d);
                                mat.SetTexture(Material.MBTextureType.DiffuseMap2, d);   // the self-illumination's colour (standart.rsh apply_self_illumination)
                                if (n != null) mat.SetTexture(Material.MBTextureType.BumpMap, n);
                                if (s != null) mat.SetTexture(Material.MBTextureType.SpecularMap, s);
                                keep.Add(d); if (n != null) keep.Add(n); if (s != null) keep.Add(s); keep.Add(mat);
                                rec["copy"] = true;
                                try { rec["shader"] = mat.GetShader() == null ? null : mat.GetShader().Name; rec["blend"] = mat.GetAlphaBlendMode().ToString(); } catch (Exception) { }
                                mat = Pass(mat, pass, false, rec);
                            }
                        }
                    }
                }
                catch (Exception e) { rec["error"] = e.GetType().Name + ": " + e.Message; mat = null; }
                cache[m.Material] = mat;
                return mat;
            }

            // MMR-291-W: the layer's pass against the water (OverlayPass) on its material: the flags and, for depth, pbr_shading's alpha_test
            // (a runtime shader flag on a material copy, as TaleWorlds.MountAndBlade.View adds use_double_colormap_with_mask_texture) at
            // overlayMeshes.alphaTest. A published material is a shared resource: a copy takes the change. The alpha test goes first, so a
            // failure leaves no depth write without it (a failed pass draws as blend and says why). The record holds the flags and the alpha
            // test before and after (the decal lab reads them).
            private Material Pass(Material mat, string pass, bool shared, Dictionary<string, object> rec)
            {
                rec["pass"] = pass;
                try
                {
                    uint before = (uint)mat.Flags;
                    rec["flags"] = before.ToString("x8");
                    if (pass == RoadsPolicy.OverlayPolicy.Blend) return mat;
                    var target = shared ? mat.CreateCopy() : mat;
                    if (shared) { keep.Add(target); rec["passCopy"] = true; }
                    if (OverlayPass.AlphaTest(pass))
                    {
                        rec["shaderFlags"] = target.GetShaderFlags().ToString("x");
                        rec["alphaTestBefore"] = target.GetAlphaTestValue();
                        target.AddMaterialShaderFlag("alpha_test", false);
                        target.SetAlphaTestValue((float)p.AlphaTest);
                        rec["passShaderFlags"] = target.GetShaderFlags().ToString("x"); rec["alphaTest"] = target.GetAlphaTestValue();
                    }
                    target.Flags = (MaterialFlags)OverlayPass.Flags(pass, before);
                    rec["passFlags"] = ((uint)target.Flags).ToString("x8");
                    return target;
                }
                catch (Exception e) { rec["passError"] = e.GetType().Name + ": " + e.Message; return mat; }
            }

            public object Add(OverlayMesh m, double x)
            {
                var material = MaterialOf(m);
                if (material == null) return null;
                var mesh = Mesh.CreateMeshWithMaterial(material);
                var handle = mesh.LockEditDataWrite();
                try
                {
                    var corner = new int[m.Vertices];
                    var up = new Vec3(0, 0, 1);
                    for (int i = 0; i < m.Vertices; i++)
                        corner[i] = mesh.AddFaceCorner(new Vec3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), up, new Vec2(m.Uvs[2 * i], m.Uvs[2 * i + 1]), m.Colours[i], handle);
                    for (int t = 0; t + 2 < m.Indices.Count; t += 3) mesh.AddFace(corner[m.Indices[t]], corner[m.Indices[t + 1]], corner[m.Indices[t + 2]], handle);
                    mesh.ComputeNormals();
                    mesh.ComputeTangents();
                    mesh.RecomputeBoundingBox();
                }
                finally { mesh.UnlockEditDataWrite(handle); }
                mesh.CullingMode = MBMeshCullingMode.None;
                mesh.VisibilityMask = VisibilityMaskFlags.Final;   // drawn in the final view only: an overlay casts no shadow
                mesh.Color = 0xFFFFFFFFu;
                mesh.SetVectorArgument(0f, 0f, 0f, (float)m.Glow);
                var entity = GameEntity.CreateEmpty(scene, false, false, true);   // no physics: a visual over the map, never a ray or click target
                entity.Name = "mnbl_overlay_" + m.Layer;
                entity.AddMesh(mesh, true);
                var frame = MatrixFrame.Identity;
                frame.origin = new Vec3((float)x, (float)m.OriginY, 0f);
                entity.SetGlobalFrame(in frame);
                entity.SetReadyToRender(true);
                return new Drawn { Entity = entity, Mesh = mesh };
            }
            public void Move(object handle, OverlayMesh m, double x)
            {
                var e = ((Drawn)handle).Entity; var frame = e.GetGlobalFrame();
                frame.origin.x = (float)x; e.SetGlobalFrame(in frame);
            }
            public void Remove(object handle) { var e = ((Drawn)handle).Entity; if (e.Scene != null) e.Scene.RemoveEntity(e, 0); }
            public void SetVisible(object handle, bool visible) { ((Drawn)handle).Entity.SetVisibilityExcludeParents(visible); }
        }
    }
}
