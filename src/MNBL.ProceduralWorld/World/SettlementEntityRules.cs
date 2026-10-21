using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-247: the scene a settlement entity check reads, without the engine: SettlementEntityChecks wraps the read inspection scene
    // (GameEntity), the offline harness (tools/Test-SettlementEntityRules.ps1) the baked scene XML (XmlEntityScene), so both run one set of
    // rules (SettlementEntityRules).
    internal interface ICheckedEntity
    {
        string Name { get; }
        IEnumerable<string> Tags { get; }
        bool HasTag(string tag);
        bool HasScript(string name);
        int ChildCount { get; }
        ICheckedEntity GetChild(int index);
        double[] GlobalPosition { get; }   // x, y, z
        uint UpgradeLevelMask { get; }
    }

    internal interface ICheckedScene
    {
        bool HasEntityNamed(string name);              // Scene.GetFirstEntityWithName
        bool HasScript(string name);                   // Scene.GetFirstEntityWithScriptComponent
        ICheckedEntity CampaignEntity(string name);    // Scene.GetCampaignEntityWithName
        IEnumerable<ICheckedEntity> RootEntities();    // every entity without a parent
        int RootEntityCount { get; }
        // The navigation face of the given region at a physical point (Scene.GetNavMeshFaceIndex, region-strict), its face group.
        bool NavFace(double x, double y, bool land, out int face, out int group);
    }

    // MMR-40 (moved here by MMR-247 from SettlementEntityChecks, unchanged): a world's scene's settlement entities against the layout they were
    // built from. The generation job's runtime stage (RuntimeStage) refuses a world that fails it (runtime.settlement_entities); the
    // world_surface probe records it. Checked:
    //   helpers   the borders and the baked Town Scene Manager helper; no baked siege prefab cache (GeneratedMapScene.Load adds it)
    //   roots     one root per settlement found by Scene.GetCampaignEntityWithName (SettlementVisual.OnStartup's lookup), named its id, with its
    //             kind's tag and the Town Entity Manager script, at the layout's centre
    //   children  each required direct child by tag count; every marker, wall section and wall engine by name at its layout position (its
    //             image beside the settlement's centre), its upgrade level mask's wall level bits those of its levels (SettlementVisual keeps
    //             the current level's copies by them); each wall section with one map_solid_wall and one map_broken_wall child (MMR-258: a
    //             look's wall section keeps its own pieces, at most one of each); a village's four icons with their level bits
    //   faces     gates, markers, walls and wall engines over a land navigation face; ports, arc ends, every blockade ship position
    //             (SettlementPositionResolver.BlockadeShips) and drop-offs over a sea face
    //   scripts   BlockadePositionScript bound on a port town with an arc, on no other
    //   census    the root entities: borders, the helper, settlements, water bodies and the engine's own roots (listed), nothing else
    //   report    the derived layout report persisted under the scene name is this layout (skipped when no report path is given: offline)
    internal static class SettlementEntityRules
    {
        private const double PositionTolerance = 2e-3;
        private const uint WallLevelBits = 2 | 4 | 8;

        // The check in steps, one settlement each (the runtime stage gives the engine thread back between slices of them). `report` is
        // complete when the steps end. `lakes`: the scene's lake bodies (PreparedSurface.Lakes); `waterTag`: GeneratedWaterSystem.Tag.
        public static IEnumerable<bool> Steps(ICheckedScene scene, SettlementLayout layout, IPlacementGround ground, Func<int, bool> isLand, SettlementPlacementPolicy policy,
                                              int lakes, string helperPrefix, string waterTag, string siegeCacheScript, string reportPath, Dictionary<string, object> report)
        {
            var failures = new List<string>();
            report["failures"] = failures;
            int failureCount = 0;
            Action<string> fail = text => { failureCount++; if (failures.Count < 200) failures.Add(text); else if (failures.Count == 200) failures.Add("(further failures omitted)"); };
            foreach (var name in new[] { "border_min", "border_max", helperPrefix + "_helper_town_scene_manager" })
                if (!scene.HasEntityNamed(name)) fail("No entity " + name + ".");
            if (scene.HasScript(siegeCacheScript)) fail("The world's scene carries the campaign-only siege prefab cache.");
            int found = 0, children = 0, blockadeScripts = 0, blockadeShips = 0, landChecks = 0, seaChecks = 0, positionChecks = 0, levelChecks = 0, walls = 0, wallEngines = 0;
            double worstOffset = 0;
            var levelSamples = new List<object>();
            Func<double[], double[]> at = logical => ground.Physical(logical[0], logical[1]);
            // A child stands at its image beside its settlement's centre (a gate across the cut from a town at the seam stands in the strip).
            Func<double[], double[], double[]> beside = (centre, logical) => {
                var c = ground.Physical(centre[0], centre[1]);
                return new[] { c[0] + SettlementPositionResolver.Delta(ground, centre[0], logical[0]), c[1] + logical[1] - centre[1] }; };
            Action<string, double[], double[]> close = (what, actual, expected) => {
                positionChecks++;
                // Single precision, as the engine's Vec3 holds them.
                double dx = (float)actual[0] - (float)expected[0], dy = (float)actual[1] - (float)expected[1], d = Math.Sqrt(dx * dx + dy * dy);
                worstOffset = Math.Max(worstOffset, d);
                if (d > PositionTolerance) fail(what + " stands " + d.ToString("0.####", CultureInfo.InvariantCulture) + " from its layout position.");
            };
            Action<string, double[], bool> face = (what, logical, land) => {
                var q = ground.Physical(logical[0], logical[1]); int f, g;
                bool ok = scene.NavFace(q[0], q[1], land, out f, out g) && isLand(g) == land;
                if (land) landChecks++; else seaChecks++;
                if (!ok) fail(what + " is not over a " + (land ? "land" : "sea") + " navigation face (face " + f + ", group " + g + ").");
            };
            Action<string, ICheckedEntity, IEnumerable<string>> levels = (what, entity, names) => {
                levelChecks++;
                uint expected = SettlementEntityFactory.LevelMask(names), actual = entity.UpgradeLevelMask;
                if (levelSamples.Count < 8) levelSamples.Add(new Dictionary<string, object> { ["entity"] = entity.Name, ["levels"] = String.Join(",", names), ["expected"] = expected, ["mask"] = actual });
                if ((actual & WallLevelBits) != (expected & WallLevelBits)) fail(what + " has upgrade level mask " + actual + ", its levels [" + String.Join(",", names) + "] are " + expected + ".");
            };
            foreach (var r in layout.Settlements)
            {
                string id = r.Site.Id;
                var e = scene.CampaignEntity(id);
                if (e == null) { fail(id + ": no campaign entity of that name."); continue; }
                found++;
                string tag = r.Site.Kind == SiteKind.Town ? "town" : r.Site.Kind == SiteKind.Castle ? "castle" : r.Site.Kind == SiteKind.Village ? "village" : "wm_hideout";
                if (e.Name != id || !e.HasTag(tag)) fail(id + ": the entity is " + e.Name + " without tag " + tag + ".");
                if (!e.HasScript("Town Entity Manager")) fail(id + ": no Town Entity Manager script.");
                close(id + " root", e.GlobalPosition, at(r.Centre));
                var direct = new Dictionary<string, List<ICheckedEntity>>(StringComparer.Ordinal);
                var named = new Dictionary<string, ICheckedEntity>(StringComparer.Ordinal);
                for (int c = 0; c < e.ChildCount; c++)
                {
                    var child = e.GetChild(c); children++;
                    named[child.Name] = child;
                    foreach (var t in child.Tags) { List<ICheckedEntity> list; if (!direct.TryGetValue(t, out list)) direct[t] = list = new List<ICheckedEntity>(); list.Add(child); }
                }
                Func<string, List<ICheckedEntity>> tagged = t => { List<ICheckedEntity> list; return direct.TryGetValue(t, out list) ? list : new List<ICheckedEntity>(); };
                Action<string, int> count = (t, n) => { if (tagged(t).Count != n) fail(id + ": " + tagged(t).Count + " children tagged " + t + ", expected " + n + "."); };
                count("map_settlement_circle", r.Site.Kind == SiteKind.Hideout ? 0 : 1);
                if (r.Site.Kind == SiteKind.Village)
                {
                    count("main_map_village_dropoff", r.Site.Harbour ? 1 : 0);
                    if (r.Dropoff != null && tagged("main_map_village_dropoff").Count == 1) { close(id + " drop-off", tagged("main_map_village_dropoff")[0].GlobalPosition, beside(r.Centre, r.Dropoff)); face(id + " drop-off", r.Dropoff, false); }
                    for (int n = 0; n < 3; n++)
                    {
                        ICheckedEntity icon; string name = id + (n == 0 ? "_icon" : "_icon_" + (n + 1));
                        if (!named.TryGetValue(name, out icon)) fail(id + ": no icon " + name + ".");
                        else levels(name, icon, new[] { SettlementPlacementPolicy.WallLevels[n], "civilian" });
                    }
                    ICheckedEntity looted;
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
                        ICheckedEntity child;
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
                        // MMR-258's relaxed contract: a look's wall keeps its own pieces, at most one of each; the culture pieces have one each.
                        bool fromLook = r.Look != null && r.Look.Source != null;
                        if (solid > 1 || broken > 1 || (!fromLook && (solid != 1 || broken != 1))) fail(id + "_" + m.Suffix + " has " + solid + " solid and " + broken + " broken wall children" + (fromLook ? ", more than one" : ", not one each") + ".");
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
                if (arc != e.HasScript("BlockadePositionScript")) fail(id + ": BlockadePositionScript " + (arc ? "missing" : "on a settlement without a blockade arc") + ".");
                else if (arc) blockadeScripts++;
                if (arc)
                {
                    face(id + " arc start", r.ArcStart, false); face(id + " arc end", r.ArcEnd, false);
                    var ships = SettlementPositionResolver.BlockadeShips(r.ArcStart, r.ArcEnd, ground, policy);
                    if (ships.Count != Int32.Parse(policy.Blockade["MaximumNumberOfShips"], CultureInfo.InvariantCulture)) fail(id + ": the blockade arc places " + ships.Count + " ships.");
                    for (int n = 0; n < ships.Count; n++) { face(id + " blockade ship " + n, ships[n], false); blockadeShips++; }
                }
                yield return true;
            }
            int expectedRoots = 3 + layout.Settlements.Count + 1 + lakes;
            // The roots the scene file does not author (the engine adds its own on read): listed by name, the same in every world.
            var authored = new HashSet<string>(layout.Settlements.Select(r => r.Site.Id).Concat(new[] { "border_min", "border_max", helperPrefix + "_helper_town_scene_manager" }), StringComparer.Ordinal);
            var engineRoots = scene.RootEntities().Where(x => !authored.Contains(x.Name) && !x.HasTag(waterTag)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            report["engineRootEntities"] = engineRoots;
            report["settlements"] = layout.Settlements.Count; report["found"] = found; report["children"] = children; report["blockadeScripts"] = blockadeScripts; report["blockadeShips"] = blockadeShips;
            report["walls"] = walls; report["wallEngines"] = wallEngines; report["levelChecks"] = levelChecks; report["levelSamples"] = levelSamples;
            report["landFaceChecks"] = landChecks; report["seaFaceChecks"] = seaChecks; report["positionChecks"] = positionChecks; report["worstOffset"] = worstOffset;
            report["rootEntities"] = scene.RootEntityCount; report["expectedRootEntities"] = expectedRoots; report["layoutSha256"] = layout.Sha256;
            report["counts"] = layout.Settlements.GroupBy(r => r.Site.Kind.ToString()).ToDictionary(g => g.Key, g => (object)g.Count());
            report["problems"] = layout.Problems.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => (object)g.Count());
            if (scene.RootEntityCount != expectedRoots + engineRoots.Count) fail(scene.RootEntityCount + " root entities, expected " + expectedRoots + " authored (borders, the helper, settlements, water bodies) and " + engineRoots.Count + " the engine adds.");
            if (engineRoots.Any(n => n.StartsWith("mnbl_", StringComparison.Ordinal))) fail("Unexpected generated root entities: " + String.Join(", ", engineRoots.Where(n => n.StartsWith("mnbl_", StringComparison.Ordinal))) + ".");
            if (reportPath != null)
                try
                {
                    var persisted = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(reportPath));
                    report["reportPath"] = reportPath;
                    if ((string)persisted["layoutSha256"] != layout.Sha256) fail("The persisted settlement layout report is another layout.");
                }
                catch (Exception error) { fail("The settlement layout report cannot be read: " + error.Message); }
            report["failureCount"] = failureCount;
            report["passed"] = failures.Count == 0;
        }

        // MMR-247 (acc4 V2): every visual piece of a settlement resolves. A meta mesh component names a mesh: a nameless one is a prefab
        // instance's override left without its prefab, which the engine draws as its magenta "REPLACE ME" box. No editor helper mesh (the
        // installed editor prefabs' volume boxes, cubes, planes, barriers, occluders, spawn, camera and animation-stand icons, debug meshes)
        // draws with its editor material outside an editor-only entity (visible_only_when_editing on it or an ancestor: the banner
        // placeholders' volume_box children are; a primitive given a material of its own is deliberate geometry). UnresolvedVisual says why a component fails (null: it resolves); FilterUnresolvedVisuals removes the failing components below
        // a root (LookBuilder runs it on every look tree); SettlementEntityFactory.Validate refuses a scene that still has one.
        public static readonly HashSet<string> EditorHelperMeshes = new HashSet<string>(new[] { "volume_box", "editor_cube", "editor_cube_centered", "editor_plane_low",
            "editor_plane_high", "editor_cylinder", "sp_cube", "occ_box", "barrier", "arrow_new_icon", "debug_lod_mesh", "icon_camera", "icon_spawn_point", "icon_man",
            "soft_border_column" }, StringComparer.Ordinal);

        public static string UnresolvedVisual(XmlElement component)
        {
            if (component == null || component.Name != "meta_mesh_component") return null;
            string mesh = component.GetAttribute("name");
            if (String.IsNullOrEmpty(mesh)) return "a meta mesh component names no mesh (an unresolved prefab override)";
            if (!EditorHelperMeshes.Contains(mesh) && !mesh.StartsWith("animation_stand_", StringComparison.Ordinal)) return null;
            // A helper primitive drawn with a material of its own is deliberate geometry (installed town_EN1's editor_cube in
            // desert_border_mat_c, town_S3's editor planes and cylinder in ground_soil_b, the water bodies' editor_plane_high).
            if (component.SelectNodes("mesh").Cast<XmlElement>().Any(m => !String.IsNullOrEmpty(m.GetAttribute("material")))) return null;
            for (XmlNode x = component.ParentNode; x != null; x = x.ParentNode)
                if (x.Name == "game_entity" && x.SelectSingleNode("visibility_masks/visibility_mask[@name='visible_only_when_editing' and @value='true']") != null) return null;
            return "the editor helper mesh " + mesh + " draws outside an editor-only entity";
        }

        public static int FilterUnresolvedVisuals(XmlElement root, List<string> removed = null)
        {
            int n = 0;
            foreach (var c in root.SelectNodes(".//components/meta_mesh_component").Cast<XmlElement>().ToList())
            {
                string why = UnresolvedVisual(c);
                if (why == null) continue;
                var entity = c.ParentNode.ParentNode as XmlElement;
                if (removed != null) removed.Add((entity == null ? "?" : entity.GetAttribute("name")) + ": " + why);
                var components = c.ParentNode; components.RemoveChild(c); n++;
                if (!components.HasChildNodes && components.ParentNode != null) components.ParentNode.RemoveChild(components);
            }
            return n;
        }

        // The runtime stage's failure detail (RuntimeStage): "<n> settlement entity failures, first: <first>".
        public static string Detail(Dictionary<string, object> report)
        {
            var failures = (List<string>)report["failures"];
            return failures.Count + " settlement entity failures, first: " + (failures.Count == 0 ? "(none)" : failures[0]);
        }
    }

    // MMR-247: a baked scene's XML as the checked scene (the offline harness). Global positions compose the transforms as LookFrame does
    // (position, rotation_euler, scale); upgrade level masks are the <levels> names' scene level bits (none: 0); navigation faces come from the
    // given lookup (the offline navigation mesh: MeshTopology.Locate).
    //   readModel  "authored" reads the XML as written. "same-level-visuals" applies a model of what acceptance run acc1 (MMR-247) saw on
    //              the engine's inspection read: in each root's hierarchy, in document order, an entity with a visual component (meta mesh or
    //              decal) and an explicit level set that an earlier such entity of the same root already has is gone, with its subtree. The
    //              engine's code is not inspected (no native decompiler here); the model is inferred from the run (every village lost the
    //              icons whose level set a look piece of its own had, and nothing else: castles, towns and the pre-MMR-258 scenes, which have
    //              no such pair, passed).
    internal sealed class XmlEntityScene : ICheckedScene
    {
        public const string Authored = "authored", SameLevelVisuals = "same-level-visuals";
        private readonly List<XmlEntity> roots = new List<XmlEntity>();
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal), scripts = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, XmlEntity> byName = new Dictionary<string, XmlEntity>(StringComparer.Ordinal);
        private readonly Func<double, double, bool, int[]> faces;
        public int Dropped;   // entities the read model removed (subtrees counted once, at their top)
        public readonly List<string> DroppedNames = new List<string>();

        public XmlEntityScene(string sceneXml, Func<double, double, bool, int[]> faces, string readModel)
        {
            if (readModel != Authored && readModel != SameLevelVisuals) throw new ArgumentException("No read model " + readModel);
            this.faces = faces;
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(sceneXml.TrimStart('﻿'));
            foreach (XmlElement e in doc.SelectNodes("/scene/entities/game_entity"))
            {
                if (readModel == SameLevelVisuals) DropSameLevelVisuals(e);
                var root = new XmlEntity(e, new LookFrame());
                roots.Add(root);
                if (!byName.ContainsKey(root.Name)) byName[root.Name] = root;
            }
            foreach (XmlElement e in doc.SelectNodes("/scene/entities//game_entity")) names.Add(e.GetAttribute("name"));
            foreach (XmlElement s in doc.SelectNodes("/scene/entities//scripts/script")) scripts.Add(s.GetAttribute("name"));
        }

        private void DropSameLevelVisuals(XmlElement root)
        {
            var seen = new HashSet<uint>();
            foreach (var e in root.SelectNodes(".//game_entity").Cast<XmlElement>().ToList())
            {
                if (!Attached(e, root)) continue;
                var levels = e.SelectNodes("levels/level").Cast<XmlElement>().Select(l => l.GetAttribute("name")).ToList();
                if (levels.Count == 0 || e.SelectSingleNode("components/meta_mesh_component|components/decal_component") == null) continue;
                uint mask = SettlementEntityFactory.LevelMask(levels);
                if (seen.Add(mask)) continue;
                e.ParentNode.RemoveChild(e); Dropped++; DroppedNames.Add(e.GetAttribute("name"));
            }
        }

        private static bool Attached(XmlNode e, XmlNode root) { for (var x = e; x != null; x = x.ParentNode) if (x == root) return true; return false; }

        public bool HasEntityNamed(string name) { return names.Contains(name); }
        public bool HasScript(string name) { return scripts.Contains(name); }
        public ICheckedEntity CampaignEntity(string name) { XmlEntity e; return byName.TryGetValue(name, out e) ? e : null; }
        public IEnumerable<ICheckedEntity> RootEntities() { return roots; }
        public int RootEntityCount { get { return roots.Count; } }
        public bool NavFace(double x, double y, bool land, out int face, out int group)
        {
            var r = faces(x, y, land); face = r[0]; group = r[1]; return face >= 0;
        }

        private sealed class XmlEntity : ICheckedEntity
        {
            private readonly XmlElement e; private readonly List<XmlEntity> children;
            private readonly double[] position; private readonly uint mask;
            public XmlEntity(XmlElement element, LookFrame parent)
            {
                e = element;
                var frame = parent.Child(element);
                position = frame.O;
                var levels = element.SelectNodes("levels/level").Cast<XmlElement>().Select(l => l.GetAttribute("name")).ToList();
                mask = levels.Count == 0 ? 0 : SettlementEntityFactory.LevelMask(levels);
                children = element.SelectNodes("children/game_entity").Cast<XmlElement>().Select(c => new XmlEntity(c, frame)).ToList();
            }
            public string Name { get { return e.GetAttribute("name"); } }
            public IEnumerable<string> Tags { get { return e.SelectNodes("tags/tag").Cast<XmlElement>().Select(t => t.GetAttribute("name")); } }
            public bool HasTag(string tag) { return Tags.Contains(tag); }
            public bool HasScript(string name) { return e.SelectSingleNode("scripts/script[@name='" + name + "']") != null; }
            public int ChildCount { get { return children.Count; } }
            public ICheckedEntity GetChild(int index) { return children[index]; }
            public double[] GlobalPosition { get { return position; } }
            public uint UpgradeLevelMask { get { return mask; } }
        }
    }
}
