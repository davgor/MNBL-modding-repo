using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using C = MNBL.ProceduralWorld.Automation.Map2AcceptanceChecks;
using LabPlan = MNBL.ProceduralWorld.Automation.Map2AcceptancePlan;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-247 (D, acc5): the decal lab (map2_acceptance phase=decallab [fromSave=<a world 1 save>, else seed=<world 1's seed>]; tools/Run-Map2Acceptance.ps1
    // -Phases ...,decallab [-LabSave <save>]; C:\Users\davgo\m2\results\MMR-247-D.md). acc5 drew 2,826 decals with the newly published module materials and none showed.
    // The lab settles why in one launch: at the flattest dry spot clear of the world's own decals and at the nearest deep sea spot it draws a grid
    // (Map2AcceptancePlan.LabLayout: columns west to east, rows south to north, so the camera at bearing 0 sees the first row nearest):
    //   columns  vanilla (mainmap_decal_undervillage, listed in Native's decal atlas, W's blue tint), vanilla+waterTexture (a copy of it with
    //            mnbl_water_surface_d as its diffuse), water (mnbl_water_surface, its river cell), water+vanillaTexture (a copy of it with the
    //            vanilla stamp's diffuse), magicWater (mnbl_magicwater, a trunk cell), magic (mnbl_magic_<school>, a trunk cell), stroke and
    //            strokeTurned (the fix's vein fallback, decal_khuzait_path_a tinted, at yaw 0 and turned a quarter);
    //   rows     full atlas, the column's cell, with SetFactor1, W's fitted pool box (fit), the clipped box (clip), a box on the sea datum
    //            (plane: MMR-290's sea stamps), a large box.
    // Each grid is shot at 16, 38 (the map camera) and 60 units, then at 38 with the lab's decals hidden; every lab decal's viewport point is
    // recorded with each shot (tools/decal_lab_sheet.py labels them). The record names each material's shader, diffuse texture and whether the
    // atlas list holds it, each lab texture's size and load state, and the fix's own record (what the dressing, magic ground and magic water
    // targets drew). Then the acc5 places again with the fix (fix-river, fix-magic-ground, fix-magic-water).
    // Readings: decals drawn from the atlas (vanilla, water+vanillaTexture) show and module textures (vanilla+waterTexture, water, magicWater,
    // magic) do not: the atlas is the cause (the fix's fallbacks draw). Module textures show: the atlas is not, and the rows tell which frame,
    // factor or size hides them. fit missing where clip shows: W's tight boxes miss the rendered terrain (roads.json water.minHalfDepth).
    // MMR-291 (overlay meshes, results/MMR-291-route.md): after each spot's decal grid, a mesh grid on the same spot: columns mesh:vein (the vein
    // material: published, else the runtime copy of torch_flame_base with mnbl_magic_<school>), mesh:river (naval_worldmap_water on a ribbon;
    // its factor row worldmap_water), mesh:magicWater (the runtime copy with mnbl_magicwater), rows full, cell, factor, plane, large
    // (Map2AcceptancePlan.LabMeshRows). acc6lab: the copies draw on land and nothing on the sea (plane row included), the river ribbons draw over it.
    // MMR-291-W: the mesh rows are now the passes against the water (OverlayPass: blend, depth, under, forward, afterPostfx; LabMeshRowDefs), the
    // vein and magic water pieces on the spot's surface (the sea spot: the sea datum, and the camera aims there), the river column the control;
    // the record names each pass's material flags and alpha test (meshMaterials) and the vanilla materials the passes copy (passReferences).
    // Readings: a pass drawn at the sea spot = that pass shows magic water over our sea (blend: the A, the policy's magicWater.pass: the B); drawn
    // at the land spot = it still draws on land.
    // Then the hang repro (HangRepro), after the lab's grids and re-shots are checkpointed, so a block cannot lose them.
    internal static partial class Map2AcceptanceProbe
    {
        // The world: a world 1 save loaded (fromSave=), or with none world 1 started from the New Campaign screen as the world1 phase starts it
        // (seed=, 42) and saved (w1.saveLab: the repro's fresh map and a relaunch after a block load it). A save from before the snow route's
        // switch (acc5's) is refused by this build: its climate identity changed, and the loader requires the saved one.
        private static IEnumerator DecalLabPhase(Acc a)
        {
            var context = a.Context;
            string fromSave = context.Str("fromSave", null);
            if (fromSave != null)
            {
                yield return Step(a, "w1.load", false, row => LoadSave(a, row, "A", fromSave, false));
                if (a.World != null) { a.Land.Save = fromSave; a.Land.NoReload = false; }
            }
            else
            {
                yield return Step(a, "w1.start", false, row => StartWorld(a, row, "A", (uint)context.Int("seed", 42)));
                if (a.World != null) yield return Step(a, "w1.saveLab", true, row => SaveStep(a, row, "w1_lab"));
            }
            if (a.World == null) { context.Outputs["stopped"] = "world 1 did not start or load"; yield break; }
            yield return Step(a, "w1.decalLab", true, row => DecalLab(a, row));
            yield return HangRepro(a);
            yield return TwoWorldMatrixProbe.EndGame(context);
        }

        // MMR-247 (results/MMR-247-H.md): the deliberate repro of acc4/acc5's engine-thread blocks, on the loaded world, with the hang capture
        // on (AutorunHost's HangWatchdog) and the probe's fresh-map rule off. Each variant is its own step (its own budget and guard) from a map
        // with no mission since its load (Map2AcceptancePlan.HangReproVariants). A block stops the engine thread: the orchestrator's CPU-flat
        // kill ends the game, the variant's step has a start line and no end, and Run-Map2Acceptance.ps1 records that as the variant's outcome
        // (blocked, with the captures) and relaunches from the lab's save for the variants left.
        private static IEnumerator HangRepro(Acc a)
        {
            var context = a.Context;
            var record = new Dictionary<string, object> { ["variants"] = LabPlan.HangReproVariants, ["freshMapAfterMission"] = false, ["hangCapture"] = AutorunHost.HangCaptureOn,
                ["captureAfterSeconds"] = AutorunHost.HangSeconds, ["capturesBefore"] = AutorunHost.HangCaptures, ["save"] = a.Land.Save, ["frames"] = LabPlan.ReproFrames };
            context.Outputs["hangRepro"] = record;
            // The lab's shots on disk first (a screenshot is written at a frame's end; its record was checkpointed with the lab's step).
            var shots = a.Shots.OfType<Dictionary<string, object>>().Where(s => Equals(s["step"], "w1.decalLab")).Select(s => (string)s["path"]).ToList();
            double start = context.Seconds;
            while (shots.Any(p => !System.IO.File.Exists(p)) && context.Seconds - start < 10) yield return null;
            record["labShots"] = shots.Count; record["labShotsMissing"] = shots.Where(p => !System.IO.File.Exists(p)).ToList();
            a.Land.FreshMapAfterMission = false;
            record["variantSwitches"] = LabPlan.HangRepros.ToDictionary(v => v.Step, v => (object)v.Record());
            context.Checkpoint();
            foreach (var variant in LabPlan.HangRepros)
                yield return Step(a, variant.Step, true, row => ReproVariant(a, row, variant));
        }

        // One variant: its bisect switches set (MMR-247 X: the dressing entities' refresh, the magic layers' refresh, the display copies' sync;
        // put back after it), a fresh map when a mission ran on this one (the lab's save loaded again), one field battle (the land cases' field
        // case: war on the enemy's kingdom, the battle, peace after, one hour, as acc5's snow battle), the map rebuilt (as the forest case
        // waited), then war: a far teleport (ReproTeleport units) and war on a fresh kingdom (the forest case's order); castle: the first peaceful
        // castle given to the player and the teleport to its gate (the castle case's order); the camera settled and ReproFrames frames, a stage
        // every 30. Nothing is undone after (no peace, the castle not given back): the next variant reloads and the phase ends the campaign, so
        // the only diplomacy changes after the battle are the block's own. Progress lines for a crash's reading: the switches, the map's scene
        // view after the battle (MapScreen enables it again and calls Scene.CheckResources, which rebuilds the occlusion rasterizer, 5 frames
        // after it is active), and each dressing refresh that changed something (MapDressingRuntime.RefreshTrace).
        private static IEnumerator ReproVariant(Acc a, Dictionary<string, object> row, LabPlan.HangReproVariant variant)
        {
            var context = a.Context; string crumb = "map2_acceptance:" + a.Step + ":";
            var dressingBefore = MapDressingInstaller.EntityImages; bool magicBefore = MagicGroundRuntime.RefreshEnabled, syncBefore = PeriodicMapPresentation.CopySyncEnabled;
            MapDressingInstaller.EntityImages = (EntityImageMode)Enum.Parse(typeof(EntityImageMode), variant.DressingEntities);
            MagicGroundRuntime.RefreshEnabled = variant.MagicRefresh; PeriodicMapPresentation.CopySyncEnabled = variant.CopySync;
            MapDressingRuntime.RefreshTrace = s => context.Stage(crumb + s);
            row["switches"] = variant.Record();
            context.Stage(crumb + "switches dressing " + variant.DressingEntities + " magicRefresh " + variant.MagicRefresh + " copySync " + variant.CopySync);
            var body = ReproVariantBody(a, row, variant.Kind, crumb);
            try
            {
                while (body.MoveNext()) yield return body.Current;
            }
            finally
            {
                (body as IDisposable)?.Dispose();
                MapDressingRuntime.RefreshTrace = null;
                MapDressingInstaller.EntityImages = dressingBefore; MagicGroundRuntime.RefreshEnabled = magicBefore; PeriodicMapPresentation.CopySyncEnabled = syncBefore;
                try
                {
                    var d = MapDressingRuntime.Current;
                    row["dressing"] = d == null ? null : new Dictionary<string, object> { ["moved"] = d.Moved, ["movedEntities"] = d.MovedEntities, ["recreated"] = d.Recreated, ["frozen"] = d.Frozen, ["errors"] = d.Errors.ToList() };
                    row["magicRefreshesSkipped"] = MagicGroundRuntime.RefreshesSkipped; row["copySyncsSkipped"] = PeriodicMapPresentation.SyncsSkipped;
                    row["refreshesHeld"] = MNBL.ProceduralWorld.UI.PeriodicMapCameraView.RefreshesHeld;   // ticks a refresh waited for the scene view
                }
                catch (Exception e) { row["countersError"] = e.Message; }
            }
        }

        // MapScreen's scene view as HandleIfSceneIsReady left it: enabled (then Scene.CheckResources ran when it was enabled again) and the
        // frames since the screen was activated.
        private static string SceneViewState()
        {
            try
            {
                var type = AccessTools.TypeByName("SandBox.View.Map.MapScreen");
                var screen = type == null ? null : AccessTools.Property(type, "Instance").GetValue(null);
                if (screen == null) return "no map screen";
                bool enabled = (bool)AccessTools.Field(type, "_isSceneViewEnabled").GetValue(screen);
                int activated = (int)AccessTools.Field(type, "_activatedFrameNo").GetValue(screen);
                return "enabled " + enabled + " framesSinceActivated " + (TaleWorlds.Engine.Utilities.EngineFrameNo - activated);
            }
            catch (Exception e) { return "unread: " + e.GetType().Name; }
        }

        private static IEnumerator ReproVariantBody(Acc a, Dictionary<string, object> row, string kind, string crumb)
        {
            var context = a.Context;
            row["kind"] = kind; row["freshMapAfterMission"] = a.Land.FreshMapAfterMission; row["hangCapture"] = AutorunHost.HangCaptureOn; row["capturesBefore"] = AutorunHost.HangCaptures;
            if (a.Land.MissionRanOnThisMap)
            {
                if (a.Land.Save == null) { Blocked(row, "a mission ran on this map and there is no save to reload"); yield break; }
                var reload = new Dictionary<string, object>(); row["freshMap"] = reload;
                context.Stage(crumb + "freshMap");
                yield return LandMissionsProbe.LoadDesignated(a.Land, reload);
                if (!Equals(reload["loaded"], true)) { Blocked(row, "the save " + a.Land.Save + " did not load for a fresh map"); yield break; }
                LandMissionsProbe.Setup(a.Land); Bind(a, a.World.Key);
            }
            context.Stage(crumb + "battle");
            var battle = new Dictionary<string, object>(); row["battle"] = battle;
            foreach (var type in new[] { "Snow", "Plain", "Steppe", "Forest" })
            {
                battle.Clear(); battle["face"] = type;
                yield return FaceBattle(a, battle, type);
                object status; if (!battle.TryGetValue("status", out status) || !Equals(status, "blocked")) break;
            }
            if (!a.Land.MissionRanOnThisMap) { Blocked(row, "the field battle ran no mission (" + (battle.ContainsKey("reason") ? battle["reason"] : "see battle") + ")"); yield break; }
            string notIdle = NotIdle();
            if (notIdle != null) { Blocked(row, "not idle on the map after the battle: " + notIdle); yield break; }
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            context.Stage(crumb + "rebuildWait");
            yield return LandMissionsProbe.MapRebuilt(context, setup);
            string view = SceneViewState(); setup["sceneView"] = view;
            context.Stage(crumb + "sceneView " + view);
            var main = MobileParty.MainParty; var from = main.Position.ToVec2(); setup["from"] = C.P(from);
            if (kind == "war")
            {
                yield return EnsureFaces(a);
                var free = C.FreeLands(a.Faces.Values.SelectMany(l => l), a.World.Prefix, 3.5).Select(p => new[] { p.X, p.Y }).ToList();
                int k = LabPlan.FarPoint(free, C.P(from), LabPlan.ReproTeleport, LabPlan.ReproTeleportMin, (p, q) => C.Distance(new Vec2((float)p[0], (float)p[1]), new Vec2((float)q[0], (float)q[1])));
                if (k < 0) { Blocked(row, "no free land point " + LabPlan.ReproTeleportMin + " units or more from the player among " + free.Count); yield break; }
                var kingdom = Kingdom.All.Where(f => f != main.MapFaction && !f.IsEliminated && f.Settlements.Count > 0 && !f.IsAtWarWith(main.MapFaction) && f.StringId != a.Land.LastEnemyFaction)
                    .OrderByDescending(f => f.Settlements.Count).ThenBy(f => f.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (kingdom == null) { Blocked(row, "no kingdom at peace with the player other than the battle's"); yield break; }
                var to = new Vec2((float)free[k][0], (float)free[k][1]);
                context.Stage(crumb + "teleport");
                main.Position = new CampaignVec2(to, true); main.SetMoveModeHold();
                setup["to"] = C.P(to); setup["moved"] = C.Distance(from, to);
                context.Stage(crumb + "declareWar");
                DeclareWarAction.ApplyByDefault(Clan.PlayerClan, kingdom);
                setup["warDeclared"] = kingdom.StringId; setup["kingdomSettlements"] = kingdom.Settlements.Count; setup["atWar"] = kingdom.IsAtWarWith(main.MapFaction);
                setup["battleFaction"] = a.Land.LastEnemyFaction;
            }
            else
            {
                var castle = Settlement.All.Where(s => s.IsCastle && s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal) && s.OwnerClan != Clan.PlayerClan && !s.MapFaction.IsAtWarWith(main.MapFaction) && s.SiegeEvent == null)
                    .OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (castle == null) { Blocked(row, "no peaceful castle"); yield break; }
                setup["castle"] = castle.StringId; setup["previousOwner"] = castle.Owner == null ? null : castle.Owner.StringId;
                context.Stage(crumb + "giveCastle");
                ChangeOwnerOfSettlementAction.ApplyByDefault(Hero.MainHero, castle);
                setup["given"] = castle.OwnerClan == Clan.PlayerClan;
                context.Stage(crumb + "teleport");
                main.Position = castle.GatePosition; main.SetMoveModeHold();
                setup["to"] = C.P(castle.GatePosition.ToVec2()); setup["moved"] = C.Distance(from, castle.GatePosition.ToVec2());
            }
            C.FollowParty(30);
            context.Stage(crumb + "settle");
            yield return MNBL.ProceduralWorld.Diagnostics.RuntimeMissionProbe.SettleCamera(context);
            double framesFrom = context.Seconds; int frames = 0;
            for (; frames < LabPlan.ReproFrames; frames++)
            {
                if (frames % 30 == 0) context.Stage(crumb + "frames " + frames);
                yield return null;
            }
            context.Stage(crumb + "ran");
            row["frames"] = frames; row["framesSeconds"] = Math.Round(context.Seconds - framesFrom, 2); row["capturesAfter"] = AutorunHost.HangCaptures;
            row["blocked"] = false; row["outcome"] = "ran: " + frames + " frames after the battle, the " + (kind == "war" ? "far teleport and the war" : "castle given and the teleport") + ", no block";
        }

        private sealed class LabDrawn { public LabPlan.LabDecal Lab; public Decal Decal; public object Mesh; public Vec3 At; }

        // MMR-291: the mesh columns' grid at a spot, through OverlayMeshRuntime's own engine target (the materials the map's layers use: a
        // published material by name, else the template copy with the published textures; rivers the vanilla water material). MMR-291-W: rows are
        // the passes against the water (Map2AcceptancePlan.LabMeshRowDefs: the vein and magic water cell pieces in each pass, material keys
        // "<key>@<pass>", on the spot's surface: the ground on land, the sea datum at the sea spot, plus the layer's lift or the row's), the river
        // ribbon on the ground in every row (the control); at the sea spot the camera aims at the sea datum (the pieces lie there, not on the
        // seabed). Shots lab-<spot>-mesh-<close|map|far|hidden>; records per piece: pass, z, drawn, vertices, triangles; the target's material
        // records (meshMaterials: each pass's flags and alpha test before and after).
        private static IEnumerator LabMeshes(Acc a, Dictionary<string, object> row, string spot, double[] at, List<LabPlan.LabDecal> entries, Scene scene, Func<double, double, double> ground, string school)
        {
            if (entries.Count == 0) yield break;
            var p = OverlayMeshRuntime.Policy;
            var spotRows = (Dictionary<string, object>)row["spots"];
            var rows = new List<object>(); spotRows[spot + "Meshes"] = rows;
            if (p == null) { spotRows[spot + "MeshesMissing"] = "no overlayMeshes policy"; yield break; }
            bool sea = spot == "sea";
            Func<double, double, double> surface = sea ? (Func<double, double, double>)((px, py) => GeneratedWaterSystem.DatumZ) : ground;
            var target = new OverlayMeshRuntime.EngineTarget(scene, p);
            var drawn = new List<LabDrawn>();
            try
            {
                foreach (var d in entries)
                {
                    var rec = new Dictionary<string, object> { ["id"] = d.Id };
                    rows.Add(rec);
                    double x = at[0] + d.Dx, y = at[1] + d.Dy, h = d.Half, z;
                    var rect = OverlayMeshes.Rect(d.Atlas, p.FlipV);
                    OverlayMesh mesh;
                    if (d.Mesh == OverlayLayer.Rivers)
                    {
                        mesh = OverlayMeshes.LabRibbon("river", x, y, h, h, ground, p.RiverDepth, p.RiverDip, p.MaxSegment);
                        z = ground(x, y) + p.RiverDepth;
                    }
                    else
                    {
                        bool veins = d.Mesh == OverlayLayer.Veins;
                        double lift = !Double.IsNaN(d.Lift) ? d.Lift : veins ? p.VeinLift : p.WaterLift;
                        string key = OverlayPass.Key(veins ? "vein:" + school : OverlayLayer.MagicWater, d.Pass);
                        mesh = OverlayMeshes.LabQuad(d.Mesh, key, school, x, y, h, h, rect, 0xFFFFFFFFu, veins ? p.Glow(school) : p.WaterGlow, surface, lift, p.MaxSegment);
                        z = surface(x, y) + lift;
                        rec["pass"] = d.Pass;
                    }
                    rec["material"] = mesh.Material; rec["vertices"] = mesh.Vertices; rec["triangles"] = mesh.Triangles; rec["frame"] = d.Mesh == OverlayLayer.Rivers ? "ground" : sea ? "plane" : "ground"; rec["z"] = Math.Round(z, 3);
                    object handle = null;
                    try { handle = target.Add(mesh, x); }
                    catch (Exception e) { rec["error"] = e.GetType().Name + ": " + e.Message; }
                    if (handle == null) { rec["drawn"] = false; if (!rec.ContainsKey("error")) rec["reason"] = "material does not resolve"; continue; }
                    var point = new Vec3((float)x, (float)y, (float)z);
                    drawn.Add(new LabDrawn { Lab = d, Mesh = handle, At = point });
                    rec["drawn"] = true; rec["at"] = new double[] { x, y, point.z };
                }
                row["meshMaterials"] = target.Materials;
                var centre = new Vec2((float)at[0], (float)at[1]);
                float? aim = sea ? (float?)GeneratedWaterSystem.DatumZ : null;
                foreach (var view in new[] { new KeyValuePair<string, float>("close", 16f), new KeyValuePair<string, float>("map", 38f), new KeyValuePair<string, float>("far", 60f) })
                    yield return LabShot(a, row, "lab-" + spot + "-mesh-" + view.Key, centre, view.Value, drawn, true, aim);
                foreach (var d in drawn) { try { target.SetVisible(d.Mesh, false); } catch (Exception) { } }
                yield return LabShot(a, row, "lab-" + spot + "-mesh-hidden", centre, 38f, drawn, false, aim);
            }
            finally
            {
                // On the live map scene (MMR-262: never after a ClearAll).
                if (C.MapScene() == scene) foreach (var d in drawn) { try { target.Remove(d.Mesh); } catch (Exception) { } }
            }
        }

        private static IEnumerator DecalLab(Acc a, Dictionary<string, object> row)
        {
            var scene = C.MapScene();
            if (scene == null) { Blocked(row, "no map scene"); yield break; }
            var roads = RoadsPolicy.Installed();
            var magic = roads.Magic; var water = roads.MagicWater;
            string school = a.Context.Str("school", "fire");
            int[] colour;
            if (!magic.FallbackColours.TryGetValue(school, out colour)) { Blocked(row, "school " + school + " is not a visible school"); yield break; }
            const uint white = 0xFFFFFFFFu;
            float[] full = { 1, 1, 0, 0 };
            int waterRow = water.SchoolRows[school];
            var stroke = magic.StrokeFallback;
            uint strokeTint = MNBL.ProceduralWorld.Surface.MagicGround.FallbackTint(colour, stroke.Core, 1);
            var columns = new List<LabPlan.LabColumn> {
                new LabPlan.LabColumn { Name = "vanilla", Material = roads.WaterFallbackMaterial, Cell = roads.WaterFallbackAtlas, Factor = roads.WaterFallbackTint },
                new LabPlan.LabColumn { Name = "vanilla+waterTexture", Material = roads.WaterFallbackMaterial, Texture = "mnbl_water_surface_d", Cell = full, Factor = white },
                new LabPlan.LabColumn { Name = "water", Material = roads.WaterMaterial, Cell = roads.WaterCell(roads.RiverColumn, 0), Factor = white },
                new LabPlan.LabColumn { Name = "water+vanillaTexture", Material = roads.WaterMaterial, Texture = roads.WaterFallbackTexture, Cell = full, Factor = white },
                new LabPlan.LabColumn { Name = "magicWater", Material = water.Material, Cell = new[] { 1f / water.Columns, 1f / water.Rows, magic.TrunkColumn / (float)water.Columns, waterRow / (float)water.Rows }, Factor = white },
                new LabPlan.LabColumn { Name = "magic", Material = magic.MaterialPrefix + school, Cell = new[] { .25f, .25f, magic.TrunkColumn / 4f, 0f }, Factor = white },
                new LabPlan.LabColumn { Name = "stroke", Material = stroke.Material, Cell = stroke.Atlas, Factor = strokeTint },
                new LabPlan.LabColumn { Name = "strokeTurned", Material = stroke.Material, Cell = stroke.Atlas, Factor = strokeTint, Turn = true },
                // MMR-291: each overlay mesh layer's material on a test piece (its own grid, after the decals): the vein runtime copy (or published
                // material) with the school's trunk cell, the river water material on a ribbon, the magic water copy with the school's trunk cell.
                new LabPlan.LabColumn { Name = "mesh:vein", Mesh = OverlayLayer.Veins, Material = "vein:" + school, Cell = new[] { .25f, .25f, magic.TrunkColumn / 4f, 0f }, Factor = 0x80FFFFFFu },
                new LabPlan.LabColumn { Name = "mesh:river", Mesh = OverlayLayer.Rivers, Material = "river", Cell = full, Factor = 0x80FFFFFFu },
                new LabPlan.LabColumn { Name = "mesh:magicWater", Mesh = OverlayLayer.MagicWater, Material = OverlayLayer.MagicWater, Cell = new[] { 1f / water.Columns, 1f / water.Rows, magic.TrunkColumn / (float)water.Columns, waterRow / (float)water.Rows }, Factor = 0x80FFFFFFu } };
            var layout = LabPlan.LabLayout(columns);
            int decalColumns = columns.Count(c => c.Mesh == null);
            row["school"] = school; row["columns"] = columns.Select(c => c.Name).ToList(); row["rows"] = LabPlan.LabRows.Select(r => r.Name).ToList();
            row["meshRows"] = LabPlan.LabMeshRows.ToList();   // MMR-291-W: the mesh grid's rows (the passes against the water)
            row["passReferences"] = PassReferences();
            row["layout"] = layout.Select(d => (object)d.Record()).ToList();
            row["atlas"] = DecalAtlasRuntime.Record();
            // The materials (a column with Texture draws a copy of its material with that diffuse) and the textures, as the engine has them.
            var materials = new Dictionary<string, Material>(StringComparer.Ordinal); var keep = new List<object>();
            var materialRows = new Dictionary<string, object>(StringComparer.Ordinal); row["materials"] = materialRows;
            foreach (var c in columns)
            {
                if (c.Mesh != null) continue;   // MMR-291: the mesh columns' materials are the overlay engine target's (meshMaterials)
                string key = c.Material + (c.Texture == null ? "" : "+" + c.Texture);
                if (materials.ContainsKey(key)) continue;
                var r = new Dictionary<string, object>(); materialRows[key] = r;
                try
                {
                    var m = Material.GetFromResource(c.Material);
                    r["resolves"] = m != null;
                    if (m != null && c.Texture != null)
                    {
                        var t = Texture.GetFromResource(c.Texture);
                        r["swapTexture"] = t == null ? null : (object)t.Name;
                        if (t != null) { m = m.CreateCopy(); m.SetTexture(Material.MBTextureType.DiffuseMap, t); keep.Add(t); }
                        else m = null;
                    }
                    if (m != null)
                    {
                        keep.Add(m);
                        r["shader"] = m.GetShader() == null ? null : m.GetShader().Name;
                        string diffuse = DecalAtlasRuntime.Diffuse(m);
                        r["diffuse"] = diffuse; r["resident"] = DecalAtlas.Resident(DecalAtlasRuntime.Atlas, diffuse);
                        foreach (var slot in new[] { Material.MBTextureType.BumpMap, Material.MBTextureType.SpecularMap })
                            try { var t = m.GetTexture(slot); r[slot.ToString()] = t == null ? null : t.Name; } catch (Exception e) { r[slot.ToString()] = "error: " + e.Message; }
                    }
                    materials[key] = m;
                }
                catch (Exception e) { r["error"] = e.GetType().Name + ": " + e.Message; materials[key] = null; }
            }
            var textures = new Dictionary<string, object>(StringComparer.Ordinal); row["textures"] = textures;
            var loading = new List<Texture>();
            foreach (var name in new[] { "mnbl_water_surface_d", "mnbl_water_surface_n", "mnbl_magicwater_d", magic.MaterialPrefix + school + "_d", roads.WaterFallbackTexture, stroke.Texture, "decal_atlas_worldmap" })
            {
                var r = new Dictionary<string, object> { ["resident"] = DecalAtlas.Resident(DecalAtlasRuntime.Atlas, name) }; textures[name] = r;
                try
                {
                    var t = Texture.CheckAndGetFromResource(name);
                    r["found"] = t != null;
                    if (t != null) { r["width"] = t.Width; r["height"] = t.Height; r["loadedBefore"] = t.IsLoaded(); r["memorySize"] = t.MemorySize; t.PreloadTexture(false); loading.Add(t); keep.Add(t); }
                }
                catch (Exception e) { r["error"] = e.GetType().Name + ": " + e.Message; }
            }
            double start = a.Context.Seconds;
            while (loading.Any(t => !t.IsLoaded()) && a.Context.Seconds - start < 5) yield return null;
            foreach (var t in loading) ((Dictionary<string, object>)textures[t.Name])["loaded"] = t.IsLoaded();
            // The fix's own record on this build: what each engine target drew, and why.
            try { var d = MapDressingRuntime.Current == null ? null : MapDressingRuntime.Current.Record(); row["dressingTarget"] = d == null || !d.ContainsKey("target") ? null : d["target"]; } catch (Exception e) { row["dressingTargetError"] = e.Message; }
            try { var g = MagicGroundRuntime.DecalsRecord; row["magicGround"] = g == null ? null : g.ContainsKey("target") ? g["target"] : null; } catch (Exception e) { row["magicGroundError"] = e.Message; }
            try { row["magicWater"] = MagicWaterRuntime.Record(); } catch (Exception e) { row["magicWaterError"] = e.Message; }
            try { row["overlayMeshes"] = OverlayMeshRuntime.Record(); } catch (Exception e) { row["overlayMeshesError"] = e.Message; }   // MMR-291

            // The spots.
            var cells = EnsureCells(a);
            if (cells == null) { Blocked(row, "no travel cells for the spot search"); yield break; }
            Func<double, double, double> ground = (x, y) => scene.GetTerrainHeight(new Vec2((float)x, (float)y), true);
            double halfX = (decalColumns - 1) / 2.0 * LabPlan.LabColumnPitch + 3, halfY = (LabPlan.LabRows.Last().Y - LabPlan.LabRows.First().Y) / 2 + 3;
            var avoid = LabAvoid(a);
            var land = new List<double[]>(); var sea = new List<double[]>();
            for (int i = 0; i < cells.Classes.Length; i++)
            {
                int r = i / cells.Cols, c = i % cells.Cols;
                if (r % 3 != 0 || c % 3 != 0) continue;
                var p = CellCentre(cells, i);
                if (cells.Classes[i] == TravelCells.Land) land.Add(new double[] { p.X, p.Y });
                else if (cells.Classes[i] == TravelCells.Sea) sea.Add(new double[] { p.X, p.Y });
            }
            Func<double, double, bool> wet = (x, y) => WaterCell(cells, x, y);
            var spotRows = new Dictionary<string, object>(); row["spots"] = spotRows;
            var landSpot = LabPlan.LabLand(land, ground, wet, avoid, halfX, halfY, 3, 1.5, GeneratedWaterSystem.DatumZ + 0.5, 3);
            if (landSpot == null) { landSpot = LabPlan.LabLand(land, ground, wet, null, halfX, halfY, 3, 3, GeneratedWaterSystem.DatumZ + 0.5, 0); spotRows["landRelaxed"] = true; }
            var seaSpot = LabPlan.LabSea(sea, ground, landSpot, halfX, halfY, 3, 1);
            spotRows["land"] = landSpot; spotRows["sea"] = seaSpot; spotRows["avoided"] = avoid.Count; spotRows["footprint"] = new[] { 2 * halfX, 2 * halfY };
            var planes = WaterPlanes.Of(MapDressingRuntime.CurrentPlan());
            foreach (var spot in new[] { new KeyValuePair<string, double[]>("land", landSpot), new KeyValuePair<string, double[]>("sea", seaSpot) })
            {
                if (spot.Value == null) { spotRows[spot.Key + "Missing"] = "no spot qualifies"; continue; }
                var drawnRows = new List<object>(); spotRows[spot.Key + "Decals"] = drawnRows;
                var drawn = new List<LabDrawn>();
                try
                {
                    foreach (var d in layout)
                    {
                        if (d.Mesh != null) continue;   // MMR-291: the mesh grid comes after
                        string key = d.Material + (d.Texture == null ? "" : "+" + d.Texture);
                        var rec = new Dictionary<string, object> { ["id"] = d.Id };
                        drawnRows.Add(rec);
                        Material m; materials.TryGetValue(key, out m);
                        if (m == null) { rec["drawn"] = false; rec["reason"] = "material does not resolve"; continue; }
                        double x = spot.Value[0] + d.Dx, y = spot.Value[1] + d.Dy, z, half;
                        LabFrame(d, x, y, ground, planes, out z, out half);
                        float yaw = d.Turn ? (float)(Math.PI / 2) : 0f, cs = (float)Math.Cos(yaw), sn = (float)Math.Sin(yaw), h = (float)d.Half;
                        var decal = Decal.CreateDecal(null);
                        decal.SetMaterial(m);
                        scene.AddDecalInstance(decal, MapDressingRuntime.DecalSet, true);
                        decal.SetIsVisible(true);
                        decal.SetVectorArgument(d.Atlas[0], d.Atlas[1], d.Atlas[2], d.Atlas[3]);
                        if (d.Factor != 0) decal.SetFactor1(d.Factor);
                        decal.Frame = new MatrixFrame(new Mat3(new Vec3(cs * h, sn * h, 0), new Vec3(-sn * h, cs * h, 0), new Vec3(0, 0, (float)half)), new Vec3((float)x, (float)y, (float)z));
                        var at = new Vec3((float)x, (float)y, (float)ground(x, y));
                        drawn.Add(new LabDrawn { Lab = d, Decal = decal, At = at });
                        rec["drawn"] = true; rec["at"] = new double[] { x, y, at.z }; rec["boxZ"] = z; rec["boxHalfDepth"] = half;
                    }
                    var centre = new Vec2((float)spot.Value[0], (float)spot.Value[1]);
                    foreach (var view in new[] { new KeyValuePair<string, float>("close", 16f), new KeyValuePair<string, float>("map", 38f), new KeyValuePair<string, float>("far", 60f) })
                        yield return LabShot(a, row, "lab-" + spot.Key + "-" + view.Key, centre, view.Value, drawn, true);
                    foreach (var d in drawn) d.Decal.SetIsVisible(false);
                    yield return LabShot(a, row, "lab-" + spot.Key + "-hidden", centre, 38f, drawn, false);
                }
                finally
                {
                    // On the live map scene (MMR-262: never after a ClearAll).
                    if (C.MapScene() == scene) foreach (var d in drawn) { try { scene.RemoveDecalInstance(d.Decal, MapDressingRuntime.DecalSet); } catch (Exception) { } }
                }
                // MMR-291: the mesh grid on the same spot (the decals gone): each overlay layer's material on its test pieces, shot as the decals
                // were (lab-<spot>-mesh-<close|map|far|hidden>); drawn yes / no comes from the same shown-vs-hidden difference.
                yield return LabMeshes(a, row, spot.Key, spot.Value, layout.Where(d => d.Mesh != null).ToList(), scene, ground, school);
            }
            // The acc5 places again, with the fix: a river, the densest generated magic ground and the densest generated magic water.
            foreach (var place in FixPlaces())
                yield return Look(a, row, place.Key, place.Value.Key, place.Value.Value, 0, new Dictionary<string, object> { ["fix"] = true }, false, 12);
            GC.KeepAlive(keep);
            C.FollowParty(30);
        }

        // MMR-291-W: the vanilla materials the passes against the water copy or compare with, as the engine has them: the flags (MaterialFlags, to
        // read the bit names in game), blend, shader, shader flags and alpha test value (beards_h and thatched_roof_b_alpha test alpha: their values
        // show the alpha test's scale).
        private static Dictionary<string, object> PassReferences()
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in new[] { "torch_flame_base", "worldmap_cloud", "wm_clouds", "crosshair_material", "naval_worldmap_water", "worldmap_water", "beards_h", "thatched_roof_b_alpha" })
            {
                var rec = new Dictionary<string, object>(); r[name] = rec;
                try
                {
                    var m = Material.GetFromResource(name);
                    rec["resolves"] = m != null;
                    if (m == null) continue;
                    rec["flags"] = ((uint)m.Flags).ToString("x8"); rec["blend"] = m.GetAlphaBlendMode().ToString();
                    rec["shader"] = m.GetShader() == null ? null : m.GetShader().Name; rec["shaderFlags"] = m.GetShaderFlags().ToString("x"); rec["alphaTest"] = m.GetAlphaTestValue();
                }
                catch (Exception e) { rec["error"] = e.GetType().Name + ": " + e.Message; }
            }
            return r;
        }

        // A lab decal's box: ground (z the terrain, its half depth), fit (W's pool box: FitBox, clipped over the planes), clip (its half depth,
        // clipped over the planes), plane (z the sea datum, its half depth).
        private static void LabFrame(LabPlan.LabDecal d, double x, double y, Func<double, double, double> ground, WaterPlanes planes, out double z, out double half)
        {
            if (d.Frame == "plane") { z = GeneratedWaterSystem.DatumZ; half = d.HalfDepth; return; }
            if (d.Frame == "ground") { z = ground(x, y); half = d.HalfDepth; return; }
            var rd = new RoadDecal { Road = -1, Piece = -1, Family = "lab", Kind = "pool", X = x, Y = y, Yaw = d.Turn ? Math.PI / 2 : 0, HalfLength = d.Half, HalfWidth = d.Half, HalfDepth = d.HalfDepth };
            if (d.Frame == "fit") MapDressing.FitBox(ground, planes, rd);
            planes.Box(rd, x, y, ground, out z, out half);
        }

        // Every drawn object of the world near which a lab grid would mix with it: settlements, road, water and magic decals, features, bridges.
        private static List<double[]> LabAvoid(Acc a)
        {
            var list = new List<double[]>();
            try { foreach (var s in Settlement.All) if (s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)) { var p = s.Position.ToVec2(); list.Add(new double[] { p.X, p.Y }); } } catch (Exception) { }
            var plan = MapDressingRuntime.CurrentPlan();
            if (plan != null)
            {
                list.AddRange(plan.Decals.Select(d => new[] { d.X, d.Y })); list.AddRange(plan.Water.Select(d => new[] { d.X, d.Y }));
                list.AddRange(plan.Bridges.Select(b => new[] { b.X, b.Y })); list.AddRange(plan.Markers.Select(m => new[] { m.X, m.Y }));
            }
            var ground = MagicGroundRuntime.Plan;
            if (ground != null) list.AddRange(ground.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y }));
            var water = MagicWaterRuntime.Installer;
            if (water != null && water.Plan != null) list.AddRange(water.Plan.Stamps.Select(s => new[] { s.Decal.X, s.Decal.Y }));
            else if (MagicGroundRuntime.Veins != null) list.AddRange(MagicGroundRuntime.Veins.Stamps.Where(s => s.OnWater).Select(s => new[] { s.X, s.Y }));   // MMR-291: drawn as meshes
            var features = MagicFeaturesRuntime.Current;
            if (features != null && features.Plan != null) list.AddRange(features.Plan.Features.Select(f => new[] { f.X, f.Y }));
            return list;
        }

        // The acc5 places (w1.water's river, w1.magic's magic-ground, w1.magicWater's magic-water) by the same rules on the loaded world: the
        // densest 10-unit cluster of river stamps, of generated land vein stamps (8 units) and of generated water stamps (10 units).
        private static List<KeyValuePair<string, KeyValuePair<Vec2, float>>> FixPlaces()
        {
            var places = new List<KeyValuePair<string, KeyValuePair<Vec2, float>>>();
            Func<IEnumerable<double[]>, double, Vec2?> densest = (points, cell) =>
            {
                var best = points.GroupBy(p => ((long)Math.Floor(p[0] / cell) << 32) ^ (long)(uint)(int)Math.Floor(p[1] / cell)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
                return best == null ? (Vec2?)null : new Vec2((float)best.Average(p => p[0]), (float)best.Average(p => p[1]));
            };
            var plan = MapDressingRuntime.CurrentPlan();
            var river = plan == null ? null : densest(plan.Water.Where(d => d.Kind == "river").Select(d => new[] { d.X, d.Y }), 10);
            if (river != null) places.Add(new KeyValuePair<string, KeyValuePair<Vec2, float>>("fix-river", new KeyValuePair<Vec2, float>(river.Value, 28)));
            var ground = MagicGroundRuntime.Plan;
            var vein = ground == null ? null : densest(ground.Stamps.Where(s => !s.Live).Select(s => new[] { s.Decal.X, s.Decal.Y }), 8);
            if (vein != null) places.Add(new KeyValuePair<string, KeyValuePair<Vec2, float>>("fix-magic-ground", new KeyValuePair<Vec2, float>(vein.Value, 28)));
            var water = MagicWaterRuntime.Installer;
            var wv = water == null || water.Plan == null ? null : densest(water.Plan.Stamps.Where(s => !s.Live).Select(s => new[] { s.Decal.X, s.Decal.Y }), 10);
            // MMR-291: with the overlay meshes drawing the magic water there is no decal installer; the graph's generated water stamps instead.
            if (wv == null && MagicGroundRuntime.Veins != null) wv = densest(MagicGroundRuntime.Veins.Stamps.Where(s => s.OnWater && !s.Live).Select(s => new[] { s.X, s.Y }), 10);
            if (wv != null) places.Add(new KeyValuePair<string, KeyValuePair<Vec2, float>>("fix-magic-water", new KeyValuePair<Vec2, float>(wv.Value, 38)));
            return places;
        }

        // The free camera over the lab's centre, settled; the screenshot, with every lab decal's viewport point (MapCameraView.Camera's
        // WorldPointToViewPortPoint of the decal's centre on the ground) so the sheet can label it. MMR-291-W: aimZ, the camera target's height
        // (null: the terrain's; the sea mesh grid aims at the sea datum).
        private static IEnumerator LabShot(Acc a, Dictionary<string, object> row, string label, Vec2 at, float distance, List<LabDrawn> drawn, bool visible, float? aimZ = null)
        {
            var record = new Dictionary<string, object> { ["at"] = C.P(at), ["distance"] = distance, ["bearing"] = 0, ["labVisible"] = visible, ["aimZ"] = aimZ };
            C.AimFree(at.X, at.Y, distance, 0, aimZ);
            yield return C.Settle(a.Context, record, 12, 6);
            var points = new Dictionary<string, object>(StringComparer.Ordinal); record["viewport"] = points;
            try
            {
                var view = C.CameraView();
                var camera = view == null ? null : AccessTools.Property(view.GetType(), "Camera").GetValue(view) as Camera;
                if (camera == null) record["viewportError"] = "no map camera";
                else foreach (var d in drawn) { var p = d.At; var v = camera.WorldPointToViewPortPoint(ref p); points[d.Lab.Id] = new double[] { v.x, v.y, v.z }; }
            }
            catch (Exception e) { record["viewportError"] = e.GetType().Name + ": " + e.Message; }
            Shot(a, row, label, record);
            yield return C.Frames(4);
        }
    }
}
