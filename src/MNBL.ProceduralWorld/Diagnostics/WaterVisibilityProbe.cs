using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using MNBL.ProceduralWorld.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-285: water visibility on the loaded campaign map of a generated world, for MMR-247's live test (world_activation with
    // waterVisibility=1 runs it on the committed world after its gameplay; any probe holding a loaded generated map can call Run). It reads the
    // map dressing the scene drew (MapDressingRuntime.Current: its plan's lake planes, water decals and water cells) and checks the live scene:
    //   bodies   the scene carries the sea plane and one plane per lake of the plan (tag snapped_water_body), and a ray down at each lake's
    //            first sample meets its plane above the terrain (every lake shown);
    //   decals   every water decal of the plan is drawn, tinted, with no installer error, and its stamp material (or, while unpublished, its
    //            fallback) drew;
    //   cells    every water cell of the plan that land parties cannot cross (Sea, Lake, River without a road deck) shows water at its centre in
    //            the live scene: a ray down meets a water body above the terrain (the sea or a lake plane), or a drawn water decal's opaque core
    //            covers it; every River cell's centre lies in a drawn river stamp;
    //   shots    screenshots with the camera over the largest lake, a river and a filled coast cell (the free camera, then back to the party).
    // Fail-soft: each part records its error and the next one runs; failures go to report["failures"].
    // MMR-291: with the river overlay meshes drawn, a river ribbon or disc (OverlayMeshRuntime.RiverCovers) counts as a drawn river stamp, and the
    // water decals they replace count as drawn when the meshes drew.
    internal static class WaterVisibilityProbe
    {
        private const int RaysPerFrame = 250;

        internal static IEnumerator Run(ProbeContext context, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var installer = MapDressingRuntime.Current;
            var map = Campaign.Current == null ? null : Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            if (installer == null || map == null) { failures.Add("no generated map with its map dressing installed (" + (map == null ? "no generated map" : "no dressing") + ")"); yield break; }
            var plan = installer.Plan;
            Scene scene = null;
            try { scene = map.Scene; } catch (Exception e) { failures.Add("the map scene is not live: " + e.Message); yield break; }
            report["world"] = plan.WorldContentId; report["sceneName"] = map.SceneName; report["plan"] = plan.WaterRecord();
            Part(report, failures, "bodies", r => Bodies(r, scene, map.SceneName, plan));
            Part(report, failures, "decals", r => Decals(r, installer));
            context.Checkpoint();
            // Cells: rays in batches (a few thousand per world).
            var cells = new Dictionary<string, object>(); report["cells"] = cells;
            var drawn = new HashSet<int>(installer.DrawnWater().Select(kv => kv.Key));
            int checkedCells = 0, byBody = 0, byDecal = 0, dry = 0, rivers = 0, riversDrawn = 0, riversUnderPlane = 0, n = 0;
            var dryRows = new List<object>(); cells["dry"] = dryRows;
            foreach (var cell in plan.WaterCells)
            {
                bool covered = false, stamped = false, body = false;
                try
                {
                    // Drawn at its image nearest the camera; the scene's terrain and planes are physical, so test the canonical centre (the
                    // planes and terrain repeat in the strips).
                    for (int i = 0; i < plan.Water.Count && !stamped; i++) stamped = drawn.Contains(i) && plan.Water[i].Covers(cell.X, cell.Y, plan.Width, plan.WaterCore);
                    if (!stamped) stamped = OverlayMeshRuntime.RiverCovers(cell.X, cell.Y);   // MMR-291: a river ribbon or disc drawn as a mesh
                    float terrain = scene.GetTerrainHeight(new Vec2((float)cell.X, (float)cell.Y), true), distance; Vec3 closest;
                    var from = new Vec3((float)cell.X, (float)cell.Y, terrain + 30); var to = new Vec3((float)cell.X, (float)cell.Y, terrain - 30);
                    body = scene.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f) && closest.z > terrain + 1e-3f;
                    covered = body || stamped;
                    // MMR-247 (acc5 c24): a river cell whose centre a shown plane covers (the ray met the sea or a lake plane) shows that water; the
                    // map dressing clips its strips over the plane and patches no such centre (Test-WaterVisibility's rule).
                    if (cell.Class == TravelCells.River)
                    {
                        rivers++; bool river = false; for (int i = 0; i < plan.RiverStamps && !river; i++) river = drawn.Contains(i) && plan.Water[i].Covers(cell.X, cell.Y, plan.Width, plan.WaterCore);
                        if (!river) river = OverlayMeshRuntime.RiverCovers(cell.X, cell.Y);   // MMR-291
                        if (river) riversDrawn++; else if (body) { riversDrawn++; riversUnderPlane++; }
                    }
                    if (!cell.Deck)
                    {
                        checkedCells++;
                        if (body) byBody++; else if (stamped) byDecal++;
                        if (!covered) { dry++; if (dryRows.Count < 30) dryRows.Add(new object[] { cell.Row, cell.Column, TravelCells.ClassNames[cell.Class], cell.Cover, cell.X, cell.Y, terrain, cell.Ground }); }
                    }
                }
                catch (Exception e) { if (dryRows.Count < 30) dryRows.Add(new object[] { cell.Row, cell.Column, "error: " + e.Message }); dry++; }
                if (++n % RaysPerFrame == 0) yield return null;
            }
            cells["impassable"] = checkedCells; cells["byWaterBody"] = byBody; cells["byDecal"] = byDecal; cells["dryCount"] = dry; cells["rivers"] = rivers; cells["riversDrawn"] = riversDrawn; cells["riversUnderPlane"] = riversUnderPlane;
            if (dry > 0) failures.Add(dry + " of " + checkedCells + " impassable water cells show dry land at their centre");
            if (riversDrawn != rivers) failures.Add((rivers - riversDrawn) + " of " + rivers + " river cells have no drawn river stamp or shown water plane at their centre");
            context.Checkpoint();
            // Screenshots over the largest lake, a river and a filled coast cell.
            var shots = new Dictionary<string, object>(); report["shots"] = shots;
            var places = new List<KeyValuePair<string, Vec2>>();
            var lake = plan.Lakes.Where(l => l.CenterX >= plan.Strip && l.CenterX < plan.Strip + plan.Width).OrderByDescending(l => l.Samples).FirstOrDefault();
            if (lake != null) places.Add(new KeyValuePair<string, Vec2>("lake", new Vec2((float)lake.CenterX, (float)lake.CenterY)));
            var rivered = plan.WaterCells.Where(w => w.Class == TravelCells.River).ToList();
            if (rivered.Count > 0) { var w = rivered[rivered.Count / 2]; places.Add(new KeyValuePair<string, Vec2>("river", new Vec2((float)w.X, (float)w.Y))); }
            var filled = plan.WaterCells.FirstOrDefault(w => w.Cover == "fill" && w.Class == TravelCells.Sea);
            if (filled != null) places.Add(new KeyValuePair<string, Vec2>("coastFill", new Vec2((float)filled.X, (float)filled.Y)));
            bool moved = false;
            foreach (var place in places)
            {
                IEnumerator steps = null;
                try { PeriodicMapCameraView.SetFollowMode("Free"); moved = true; steps = CameraTo(context, new Vec3(place.Value.x, place.Value.y, PeriodicMapCameraView.Target().z)); }
                catch (Exception e) { shots[place.Key] = "camera: " + e.Message; continue; }
                while (true)
                {
                    try { if (!steps.MoveNext()) break; } catch (Exception e) { shots[place.Key] = "camera: " + e.Message; break; }
                    yield return steps.Current;
                }
                string shot = System.IO.Path.Combine(context.Directory, "water-visibility-" + context.ProcessId + "-" + plan.WorldContentId.Substring(0, 8) + "-" + place.Key + ".bmp");
                try { Utilities.TakeScreenshot(shot); shots[place.Key] = new Dictionary<string, object> { ["path"] = shot, ["at"] = new[] { place.Value.x, place.Value.y } }; }
                catch (Exception e) { shots[place.Key] = "screenshot: " + e.Message; }
                yield return null;
            }
            if (moved) { try { PeriodicMapCameraView.Call("TeleportCameraToMainParty"); } catch (Exception) { } }
            report["passed"] = failures.Count == 0;
        }

        private static void Part(Dictionary<string, object> report, List<string> failures, string name, Action<Dictionary<string, object>> body)
        {
            var row = new Dictionary<string, object>(); report[name] = row;
            var rowFailures = new List<string>(); row["failures"] = rowFailures;
            try { body(row); } catch (Exception e) { row["error"] = e.ToString(); rowFailures.Add("error: " + e.Message); }
            failures.AddRange(rowFailures.Select(f => name + ": " + f));
        }

        // The sea plane and the plan's lake planes, each lake's ray at its first sample meeting a shown water plane above the terrain. MMR-247
        // (acc4 triage fails-10): a lake is shown when the ray meets any shown plane there, not only its own: the sea plane when the lake lies
        // below the datum (strip copies of lakes clamped to the shore under the sea plane), or another plan lake's plane whose box holds the
        // sample. lakeRayHits counts the lakes met by their own plane; every other lake is listed in missed with the plane met (own, sea,
        // lake:<index> or none), and only "none" fails.
        private static void Bodies(Dictionary<string, object> row, Scene scene, string sceneName, MapDressingPlan plan)
        {
            var failures = (List<string>)row["failures"];
            var bodies = scene.FindEntitiesWithTag(GeneratedWaterSystem.Tag).ToList();
            row["bodies"] = bodies.Count; row["lakes"] = plan.Lakes.Count; row["lakesHidden"] = plan.LakesHidden; row["lakesUnmatched"] = plan.LakesUnmatched;
            if (bodies.Count != 1 + plan.Lakes.Count) failures.Add(bodies.Count + " water bodies, expected the sea plane and " + plan.Lakes.Count + " lake planes");
            if (plan.LakesHidden + plan.LakesUnmatched > 0) failures.Add(plan.LakesHidden + " lake groups hidden and " + plan.LakesUnmatched + " unmatched (every lake must show)");
            int hits = 0, none = 0, through = 0; var missed = new List<object>(); row["missed"] = missed;
            var planes = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["own"] = 0, ["sea"] = 0, ["lake"] = 0 };
            foreach (var lake in plan.Lakes)
            {
                float distance; Vec3 closest;
                double x = lake.FirstSampleX, y = lake.FirstSampleY;
                double top = Math.Max(Math.Max(lake.Z, GeneratedWaterSystem.DatumZ), plan.Lakes.Where(l => Holds(l, x, y)).Select(l => l.Z).DefaultIfEmpty(lake.Z).Max());
                var from = new Vec3((float)x, (float)y, (float)top + 20); var to = new Vec3((float)x, (float)y, (float)Math.Min(lake.Z, GeneratedWaterSystem.DatumZ) - 20);
                bool hit = scene.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f);
                float terrain = scene.GetTerrainHeight(new Vec2((float)x, (float)y), true);
                // MMR-247 (acc5, B' lake 94): a mesh standing on the sample (there a ruin marker) stops the ray above the plane; cast on from just
                // under such a hit (at most 3 times) until it meets a plan plane, the sea or the terrain.
                Func<float, bool> atPlane = z => Math.Abs(z - GeneratedWaterSystem.DatumZ) < 2e-3 || plan.Lakes.Any(l => Holds(l, x, y) && Math.Abs(z - l.Z) < 2e-3);
                for (int k = 0; k < 3 && hit && closest.z > terrain + 1e-3f && !atPlane(closest.z); k++)
                {
                    through++;
                    hit = scene.RayCastForClosestEntityOrTerrain(new Vec3((float)x, (float)y, closest.z - .02f), to, out distance, out closest, .01f);
                }
                string plane = "none";
                if (hit && closest.z > terrain)
                {
                    if (Math.Abs(closest.z - lake.Z) < 2e-3) plane = "own";
                    else if (lake.Z < GeneratedWaterSystem.DatumZ && Math.Abs(closest.z - GeneratedWaterSystem.DatumZ) < 2e-3) plane = "sea";
                    else { var other = plan.Lakes.FirstOrDefault(l => l != lake && Holds(l, x, y) && Math.Abs(closest.z - l.Z) < 2e-3); if (other != null) plane = "lake:" + other.Index; }
                }
                if (plane == "own") { hits++; planes["own"]++; continue; }
                if (plane == "none") none++; else planes[plane == "sea" ? "sea" : "lake"]++;
                if (missed.Count < 200) missed.Add(new object[] { lake.Index, x, y, lake.Z, hit ? (object)closest.z : null, terrain, plane });
            }
            row["lakeRayHits"] = hits; row["lakeRayPlanes"] = planes; row["lakesNotShown"] = none; row["raysThroughMeshes"] = through;
            if (none > 0) failures.Add(none + " of " + plan.Lakes.Count + " lake rays met no shown water plane above the terrain");
        }

        private static bool Holds(GeneratedWaterSystem.Lake l, double x, double y) { return x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY; }

        private static void Decals(Dictionary<string, object> row, MapDressingInstaller installer)
        {
            var failures = (List<string>)row["failures"];
            var plan = installer.Plan;
            int drawn = installer.DrawnCount(3);
            string material = plan.Water.Count == 0 ? null : plan.Water[0].Material, fallback = plan.Water.Count == 0 ? null : plan.Water[0].Fallback;
            // MMR-247 (W): the stamp material that actually drew counts, mnbl_water_surface or, while it is unpublished, its fallback (the engine
            // target's record of the materials it drew); without that record, the material or its fallback resolving by name.
            var target = installer.Describe == null ? null : installer.Describe();
            var drew = target != null && target.ContainsKey("materials") ? target["materials"] as IDictionary<string, int> : null;
            Func<string, bool> used = m => m != null && (drew != null ? drew.ContainsKey(m) && drew[m] > 0 : Material.GetFromResource(m) != null);
            string drawnWith = material == null ? null : used(material) ? material : used(fallback) ? fallback : null;
            // MMR-291: water decals the river overlay meshes replace count as drawn when the meshes drew (their pieces); with every water decal
            // replaced, the meshes' drawing is the material check.
            int replaced = installer.ReplacedWater, meshPieces = OverlayMeshRuntime.Pieces(OverlayLayer.Rivers);
            if (drawnWith == null && replaced > 0 && meshPieces > 0) drawnWith = "overlay meshes";
            bool resolves = material == null || drawnWith != null;
            row["planned"] = plan.Water.Count; row["drawn"] = drawn; row["material"] = material; row["fallback"] = fallback; row["drawnWith"] = drawnWith; row["materialResolves"] = resolves;
            row["replacedByMeshes"] = replaced; row["riverMeshPieces"] = meshPieces;
            row["fallbacks"] = target != null && target.ContainsKey("fallbacks") ? target["fallbacks"] : null; row["installerErrors"] = installer.Errors.ToList();
            row["roadDecalsDrawn"] = installer.DrawnCount(0); row["decalsDrawn"] = installer.DrawnCount(0) + drawn;
            if (!resolves) failures.Add("neither the water stamp material " + material + " nor its fallback " + fallback + " drew");
            if (drawn + (meshPieces > 0 ? replaced : 0) != plan.Water.Count) failures.Add(drawn + " of " + plan.Water.Count + " water decals drawn (" + replaced + " replaced by river meshes, " + meshPieces + " mesh pieces drawn)");
        }

        // The free camera to a point (as PeriodicPresentationProbe): its ideal target set, then until the target is there and the frame still.
        private static IEnumerator CameraTo(ProbeContext context, Vec3 point)
        {
            point.x = PeriodicMapCameraView.Nearest(point.x);
            PeriodicMapCameraView.SetIdeal(point);
            double t0 = context.Seconds;
            Vec3 lastFrame = PeriodicMapCameraView.FrameOrigin(); int still = 0;
            while (context.Seconds - t0 < 6 && still < 10)
            {
                yield return null;
                var t = PeriodicMapCameraView.Target(); var f = PeriodicMapCameraView.FrameOrigin();
                bool frameStill = (f.AsVec2 - lastFrame.AsVec2).Length < 0.05f; lastFrame = f;
                still = (t.AsVec2 - point.AsVec2).Length < 0.5f && frameStill ? still + 1 : 0;
            }
        }
    }
}
