using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-40: a world's read scene's settlement entities against the layout they were built from (PreparedSurface.Settlements), on the engine
    // thread over the inspection world. The generation job's runtime stage (RuntimeStage) refuses a world that fails it
    // (runtime.settlement_entities); the world_surface probe records it. Checked:
    //   helpers   the borders and the baked Town Scene Manager helper; no baked siege prefab cache (GeneratedMapScene.Load adds it)
    //   roots     one root per settlement found by Scene.GetCampaignEntityWithName (SettlementVisual.OnStartup's lookup), named its id, with its
    //             kind's tag and the Town Entity Manager script, at the layout's centre
    //   children  each required direct child by tag count; every marker, wall section and wall engine by name at its layout position (its
    //             image beside the settlement's centre), its upgrade level mask's wall level bits those of its levels (SettlementVisual keeps
    //             the current level's copies by them); each wall section with one map_solid_wall and one map_broken_wall child; a village's
    //             four icons with their level bits
    //   faces     gates, markers, walls and wall engines over a land navigation face; ports, arc ends, every blockade ship position
    //             (SettlementPositionResolver.BlockadeShips) and drop-offs over a sea face
    //   scripts   BlockadePositionScript bound on a port town with an arc, on no other
    //   census    the root entities: borders, the helper, settlements, water bodies and the engine's own roots (listed), nothing else
    //   report    the derived layout report persisted under the scene name is this layout
    internal static class SettlementEntityChecks
    {
        private const double PositionTolerance = 2e-3;
        private const uint WallLevelBits = 2 | 4 | 8;

        public static Dictionary<string, object> Run(RuntimeWorld world, PreparedSurface prepared)
        {
            var failures = new List<string>();
            var report = new Dictionary<string, object> { ["failures"] = failures };
            var scene = world.Scene; var layout = prepared.Settlements; var ground = prepared.SettlementGround; var model = prepared.NavigationPlan.Model;
            var policy = SettlementPlacementPolicy.Installed();
            Action<string> fail = text => { if (failures.Count < 200) failures.Add(text); else if (failures.Count == 200) failures.Add("(further failures omitted)"); };
            foreach (var name in new[] { "border_min", "border_max", RuntimeWorldBackend.HelperPrefix + "_helper_town_scene_manager" })
                if (scene.GetFirstEntityWithName(name) == null) fail("No entity " + name + ".");
            if (scene.GetFirstEntityWithScriptComponent(SettlementEntityContract.SiegeCacheScript) != null) fail("The world's scene carries the campaign-only siege prefab cache.");
            int found = 0, children = 0, blockadeScripts = 0, blockadeShips = 0, landChecks = 0, seaChecks = 0, positionChecks = 0, levelChecks = 0, walls = 0, wallEngines = 0;
            double worstOffset = 0;
            var levelSamples = new List<object>();
            Func<double[], bool, Vec3> at = (logical, water) => { var q = ground.Physical(logical[0], logical[1]); return new Vec3((float)q[0], (float)q[1], water ? 0f : (float)ground.Elevation(logical[0], logical[1])); };
            // A child stands at its image beside its settlement's centre (a gate across the cut from a town at the seam stands in the strip).
            Func<double[], double[], Vec3> beside = (centre, logical) => {
                var c = ground.Physical(centre[0], centre[1]);
                return new Vec3((float)(c[0] + SettlementPositionResolver.Delta(ground, centre[0], logical[0])), (float)(c[1] + logical[1] - centre[1]), 0f); };
            Action<string, Vec3, Vec3> close = (what, actual, expected) => {
                positionChecks++;
                double d = Math.Sqrt((actual.x - expected.x) * (double)(actual.x - expected.x) + (actual.y - expected.y) * (double)(actual.y - expected.y));
                worstOffset = Math.Max(worstOffset, d);
                if (d > PositionTolerance) fail(what + " stands " + d.ToString("0.####", CultureInfo.InvariantCulture) + " from its layout position.");
            };
            Action<string, double[], bool> face = (what, logical, land) => {
                var q = ground.Physical(logical[0], logical[1]); int f, g, i;
                bool ok = world.NavFace(q[0], q[1], land, out f, out g, out i) && model.IsLand(g) == land;
                if (land) landChecks++; else seaChecks++;
                if (!ok) fail(what + " is not over a " + (land ? "land" : "sea") + " navigation face (face " + f + ", group " + g + ").");
            };
            Action<string, GameEntity, IEnumerable<string>> levels = (what, entity, names) => {
                levelChecks++;
                uint expected = SettlementEntityFactory.LevelMask(names), actual = (uint)entity.GetUpgradeLevelMask();
                if (levelSamples.Count < 8) levelSamples.Add(new Dictionary<string, object> { ["entity"] = entity.Name, ["levels"] = String.Join(",", names), ["expected"] = expected, ["mask"] = actual });
                if ((actual & WallLevelBits) != (expected & WallLevelBits)) fail(what + " has upgrade level mask " + actual + ", its levels [" + String.Join(",", names) + "] are " + expected + ".");
            };
            foreach (var r in layout.Settlements)
            {
                string id = r.Site.Id;
                var e = scene.GetCampaignEntityWithName(id);
                if (e == null) { fail(id + ": no campaign entity of that name."); continue; }
                found++;
                string tag = r.Site.Kind == SiteKind.Town ? "town" : r.Site.Kind == SiteKind.Castle ? "castle" : r.Site.Kind == SiteKind.Village ? "village" : "wm_hideout";
                if (e.Name != id || !e.HasTag(tag)) fail(id + ": the entity is " + e.Name + " without tag " + tag + ".");
                if (!e.HasScriptComponent("Town Entity Manager")) fail(id + ": no Town Entity Manager script.");
                close(id + " root", e.GlobalPosition, at(r.Centre, false));
                var direct = new Dictionary<string, List<GameEntity>>(StringComparer.Ordinal);
                var named = new Dictionary<string, GameEntity>(StringComparer.Ordinal);
                for (int c = 0; c < e.ChildCount; c++)
                {
                    var child = e.GetChild(c); children++;
                    named[child.Name] = child;
                    foreach (var t in child.Tags) { List<GameEntity> list; if (!direct.TryGetValue(t, out list)) direct[t] = list = new List<GameEntity>(); list.Add(child); }
                }
                Func<string, List<GameEntity>> tagged = t => { List<GameEntity> list; return direct.TryGetValue(t, out list) ? list : new List<GameEntity>(); };
                Action<string, int> count = (t, n) => { if (tagged(t).Count != n) fail(id + ": " + tagged(t).Count + " children tagged " + t + ", expected " + n + "."); };
                count("map_settlement_circle", r.Site.Kind == SiteKind.Hideout ? 0 : 1);
                if (r.Site.Kind == SiteKind.Village)
                {
                    count("main_map_village_dropoff", r.Site.Harbour ? 1 : 0);
                    if (r.Dropoff != null && tagged("main_map_village_dropoff").Count == 1) { close(id + " drop-off", tagged("main_map_village_dropoff")[0].GlobalPosition, beside(r.Centre, r.Dropoff)); face(id + " drop-off", r.Dropoff, false); }
                    for (int n = 0; n < 3; n++)
                    {
                        GameEntity icon; string name = id + (n == 0 ? "_icon" : "_icon_" + (n + 1));
                        if (!named.TryGetValue(name, out icon)) fail(id + ": no icon " + name + ".");
                        else levels(name, icon, new[] { SettlementPlacementPolicy.WallLevels[n], "civilian" });
                    }
                    GameEntity looted;
                    if (!named.TryGetValue(id + "_icon_looted", out looted)) fail(id + ": no looted icon.");
                    else levels(id + "_icon_looted", looted, SettlementPlacementPolicy.WallLevels.Concat(new[] { "looted" }));
                }
                if (r.Site.Fortification && r.Gate != null)
                {
                    count("main_map_city_gate", 1);
                    if (tagged("main_map_city_gate").Count == 1) close(id + " gate", tagged("main_map_city_gate")[0].GlobalPosition, beside(r.Centre, r.Gate));
                    face(id + " gate", r.Gate, true);
                    foreach (var g in r.Markers.Concat(r.Walls).GroupBy(m => m.Tag)) count(g.Key, g.Count());
                    foreach (var m in r.Markers.Concat(r.Walls))
                    {
                        GameEntity child;
                        if (!named.TryGetValue(id + "_" + m.Suffix, out child)) { fail(id + ": no child " + id + "_" + m.Suffix + "."); continue; }
                        if (!child.HasTag(m.Tag)) fail(id + "_" + m.Suffix + " lacks tag " + m.Tag + ".");
                        close(id + " " + m.Suffix, child.GlobalPosition, beside(r.Centre, m.Logical));
                        face(id + " " + m.Suffix, m.Logical, true);
                        if (m.Level == null) continue;
                        bool wall = m.Tag == "map_breachable_wall";
                        levels(id + "_" + m.Suffix, child, wall ? policy.WallLevelSets[m.Level] : policy.WallEngineLevelSets[m.Level]);
                        if (!wall) { wallEngines++; continue; }
                        walls++;
                        int solid = 0, broken = 0;
                        for (int c = 0; c < child.ChildCount; c++) { if (child.GetChild(c).HasTag("map_solid_wall")) solid++; if (child.GetChild(c).HasTag("map_broken_wall")) broken++; }
                        if (solid != 1 || broken != 1) fail(id + "_" + m.Suffix + " has " + solid + " solid and " + broken + " broken wall children, not one each.");
                    }
                }
                if (r.Site.HasPort && r.Port != null)
                {
                    count("main_map_city_port", 1);
                    if (tagged("main_map_city_port").Count == 1) close(id + " port", tagged("main_map_city_port")[0].GlobalPosition, beside(r.Centre, r.Port));
                    face(id + " port", r.Port, false);
                }
                bool arc = r.Site.HasPort && r.ArcStart != null;
                count("Blockade_Arc_Start", arc ? 1 : 0); count("Blockade_Arc_End", arc ? 1 : 0);
                if (arc != e.HasScriptComponent("BlockadePositionScript")) fail(id + ": BlockadePositionScript " + (arc ? "missing" : "on a settlement without a blockade arc") + ".");
                else if (arc) blockadeScripts++;
                if (arc)
                {
                    face(id + " arc start", r.ArcStart, false); face(id + " arc end", r.ArcEnd, false);
                    var ships = SettlementPositionResolver.BlockadeShips(r.ArcStart, r.ArcEnd, ground, policy);
                    if (ships.Count != Int32.Parse(policy.Blockade["MaximumNumberOfShips"], CultureInfo.InvariantCulture)) fail(id + ": the blockade arc places " + ships.Count + " ships.");
                    for (int n = 0; n < ships.Count; n++) { face(id + " blockade ship " + n, ships[n], false); blockadeShips++; }
                }
            }
            int expectedRoots = 3 + layout.Settlements.Count + 1 + prepared.Lakes.Count;
            // The roots the scene file does not author (the engine adds its own on read): listed by name, the same in every world.
            var authored = new HashSet<string>(layout.Settlements.Select(r => r.Site.Id).Concat(new[] { "border_min", "border_max", RuntimeWorldBackend.HelperPrefix + "_helper_town_scene_manager" }), StringComparer.Ordinal);
            var all = new List<GameEntity>(); scene.GetEntities(ref all);
            var engineRoots = all.Where(x => x.Parent == null && !authored.Contains(x.Name) && !x.HasTag(GeneratedWaterSystem.Tag)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            report["engineRootEntities"] = engineRoots;
            report["settlements"] = layout.Settlements.Count; report["found"] = found; report["children"] = children; report["blockadeScripts"] = blockadeScripts; report["blockadeShips"] = blockadeShips;
            report["walls"] = walls; report["wallEngines"] = wallEngines; report["levelChecks"] = levelChecks; report["levelSamples"] = levelSamples;
            report["landFaceChecks"] = landChecks; report["seaFaceChecks"] = seaChecks; report["positionChecks"] = positionChecks; report["worstOffset"] = worstOffset;
            report["rootEntities"] = scene.RootEntityCount; report["expectedRootEntities"] = expectedRoots; report["layoutSha256"] = layout.Sha256;
            report["counts"] = layout.Settlements.GroupBy(r => r.Site.Kind.ToString()).ToDictionary(g => g.Key, g => (object)g.Count());
            report["problems"] = layout.Problems.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => (object)g.Count());
            if (scene.RootEntityCount != expectedRoots + engineRoots.Count) fail(scene.RootEntityCount + " root entities, expected " + expectedRoots + " authored (borders, the helper, settlements, water bodies) and " + engineRoots.Count + " the engine adds.");
            if (engineRoots.Any(n => n.StartsWith("mnbl_", StringComparison.Ordinal))) fail("Unexpected generated root entities: " + String.Join(", ", engineRoots.Where(n => n.StartsWith("mnbl_", StringComparison.Ordinal))) + ".");
            try
            {
                var persisted = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(prepared.SettlementReportPath));
                report["reportPath"] = prepared.SettlementReportPath;
                if ((string)persisted["layoutSha256"] != layout.Sha256) fail("The persisted settlement layout report is another layout.");
            }
            catch (Exception error) { fail("The settlement layout report cannot be read: " + error.Message); }
            report["passed"] = failures.Count == 0;
            return report;
        }
    }
}
