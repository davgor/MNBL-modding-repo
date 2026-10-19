using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MNBL.ProceduralWorld.Surface
{
    // One scene entity to author: its name, position (global for a root, local to its parent for a child), yaw, scale, tags, scripts, mesh,
    // physics and scene levels. Pure data: SettlementEntityFactory emits it as scene XML (baked into a world's scene before it is encoded) and
    // SettlementEntityRuntime builds it as live entities (a settlement without a scene entity, such as an MMR-34 hideout).
    internal sealed class EntitySpec
    {
        public string Name, Mesh, Physics;
        public double[] Position = { 0, 0, 0 };
        public double Yaw;
        public double[] Scale;   // x, y, z; null is 1, 1, 1
        public readonly List<string> Tags = new List<string>(), Levels = new List<string>(), BodyFlags = new List<string>(), Flags = new List<string>();
        public readonly List<KeyValuePair<string, List<KeyValuePair<string, string>>>> Scripts = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
        public string RawScripts;   // an installed <script> element copied verbatim (helper entities)
        public bool EditorOnly;
        public readonly List<EntitySpec> Children = new List<EntitySpec>();

        public EntitySpec Child(string name, double[] local, params string[] tags)
        {
            var c = new EntitySpec { Name = name, Position = local ?? new double[] { 0, 0, 0 } };
            c.Tags.AddRange(tags); Children.Add(c); return c;
        }
        public EntitySpec Script(string name, params string[] variables)
        {
            var vars = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < variables.Length; i += 2) vars.Add(new KeyValuePair<string, string>(variables[i], variables[i + 1]));
            Scripts.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(name, vars)); return this;
        }
        public IEnumerable<EntitySpec> SelfAndDescendants() { yield return this; foreach (var c in Children) foreach (var d in c.SelfAndDescendants()) yield return d; }
    }

    // What MMR-40 takes from the installed War Sails campaign map (NavalDLC Main_map): the helper scripts the campaign map dereferences, copied
    // verbatim as variable and resource definitions only, and the reusable meshes the icons and walls reference (never copied). Its hash binds
    // a world's scene to the installed contract it was built against.
    internal sealed class SettlementEntityContract
    {
        public static readonly string[] HelperScripts = { "Town Scene Manager", "CampaignMapSiegePrefabEntityCache" };
        // The helpers baked into a scene. CampaignMapSiegePrefabEntityCache needs a running campaign (its OnInit reads the campaign's map
        // scene: an inspection read of a scene carrying one throws, run 25412), and every generated scene is also read without one (the
        // generation job, the inspection probes), so GeneratedMapScene.Load adds it to the campaign map with the installed variables
        // (SiegeCacheVariables) when the scene has none. MapScreen.OnInitialize dereferences it unguarded.
        public static readonly string[] BakedHelperScripts = { "Town Scene Manager" };
        public const string SiegeCacheScript = "CampaignMapSiegePrefabEntityCache";
        public static readonly Dictionary<SiteKind, string> IconMeshes = new Dictionary<SiteKind, string> {
            [SiteKind.Town] = "mi_emp_keep_1", [SiteKind.Castle] = "mi_emp_keep_1", [SiteKind.Village] = "village_empire_1", [SiteKind.Hideout] = "worldmap_hideout_a" };
        // A village's icon per wall level, shown with the civilian level (the installed village's map_icons_*_village_l1..l3), and its looted
        // icon (the level-1 mesh with the looted level), so exactly one icon shows for any village state.
        public static readonly string[] VillageLevelIcons = { "village_empire_1", "village_empire_2", "village_empire_3" };
        public readonly Dictionary<string, string> Helpers = new Dictionary<string, string>(StringComparer.Ordinal);   // script name -> <script> outer XML
        public readonly Dictionary<string, string> SiegeCacheVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Sha256, SourceSha256;
        private HashSet<string> meshes;

        private static SettlementEntityContract installed;
        private static readonly object installedLock = new object();
        // The installed contract, read once per process (the campaign map is large; Prepare and GeneratedMapScene.Load both need it).
        public static SettlementEntityContract Installed(string mainMapPath)
        {
            lock (installedLock)
            {
                if (installed == null) installed = FromInstalled(File.ReadAllBytes(mainMapPath));
                return installed;
            }
        }

        public static SettlementEntityContract FromInstalled(byte[] mainMapSceneXml)
        {
            var doc = new XmlDocument(); doc.LoadXml(new UTF8Encoding(false).GetString(mainMapSceneXml).TrimStart('\uFEFF'));
            var contract = new SettlementEntityContract();
            foreach (var name in HelperScripts)
            {
                var matches = doc.SelectNodes("/scene/entities/game_entity/scripts/script[@name='" + name + "']");
                if (matches == null || matches.Count != 1) throw new InvalidDataException("The installed campaign map has " + (matches == null ? 0 : matches.Count) + " '" + name + "' helper scripts, not one.");
                contract.Helpers[name] = matches[0].OuterXml;
                if (name == SiegeCacheScript)
                    foreach (XmlElement v in matches[0].SelectNodes("variables/variable")) contract.SiegeCacheVariables[v.GetAttribute("name")] = v.GetAttribute("value");
            }
            if (contract.SiegeCacheVariables.Count == 0) throw new InvalidDataException("The installed " + SiegeCacheScript + " sets no prefab variables.");
            contract.meshes = new HashSet<string>(doc.SelectNodes("//meta_mesh_component").Cast<XmlElement>().Select(e => e.GetAttribute("name")), StringComparer.Ordinal);
            foreach (var kv in IconMeshes) if (!contract.meshes.Contains(kv.Value)) throw new InvalidDataException("The installed campaign map references no mesh " + kv.Value + " (the " + kv.Key + " icon).");
            contract.RequireMeshes(VillageLevelIcons);
            using (var sha = SHA256.Create())
            {
                contract.SourceSha256 = Hex(sha.ComputeHash(mainMapSceneXml));
                contract.Sha256 = Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(String.Join("\n", contract.Helpers.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value))
                    + "\n" + String.Join("\n", IconMeshes.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)) + "\nvillage " + String.Join(",", VillageLevelIcons))));
            }
            return contract;
        }

        // Refuses a mesh the installed campaign map does not reference (the policy's wall meshes).
        public void RequireMeshes(params string[] names)
        {
            foreach (var n in names) if (!meshes.Contains(n)) throw new InvalidDataException("The installed campaign map references no mesh " + n + ".");
        }

        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
    }

    // MMR-40: the settlement hierarchy per kind, from the installed campaign map's (counted in tools/make_settlement_placement_policy.py and
    // docs/settlement-entities.md), every child named <settlement id>_<suffix>:
    //   town     root tagged "town", Town Entity Manager, BlockadePositionScript (port towns with a fitted arc, MMR-35's installed median
    //            parameters); children: icon (mesh), selection (bo_town, raycast-only sphere), circle (map_settlement_circle, the installed
    //            scale), banner placeholders (map_banner_placeholder, each with its one editor-only volume_box child), gate (main_map_city_gate),
    //            outer gate (main_map_castle_gate), siege camps (map_camp_area_1/2, the installed scales), siege engines (map_siege_engine_0..3),
    //            ram, towers, per wall level: two breachable wall sections (map_breachable_wall, each with a raycast-only collider, a map_solid_wall
    //            and a map_broken_wall child: SettlementVisual.RefreshWallState shows one of them by the section's hit points) and four wall
    //            engines (map_defensive_engine_0..3), each with its level's scene levels (SettlementVisual keeps those of the current wall
    //            level); port (main_map_city_port, a direct child), blockade arc ends (Blockade_Arc_Start/End)
    //   castle   the same without port and blockade, root tagged "castle"
    //   village  root tagged "village"; icons per level (civilian) and looted, selection (an untagged raycast sphere, as installed), circle,
    //            one banner placeholder, drop-off (main_map_village_dropoff) for a harbour village
    //   hideout  root tagged "wm_hideout" with a sphere collider and the icon
    // plus the campaign borders (border_min, border_max: the scene's box) and the helper entities (<prefix>_helper_*: Town Scene Manager; see
    // BakedHelperScripts). Every marker faces the gate direction (an entity's forward is +Y, so its yaw is the gate angle less a quarter turn).
    // Positions come from the SettlementLayout only; a root stands at its physical centre, every child at its offset from it. The scene declares
    // the installed campaign map's scene levels (DeclareSceneLevels). The XML is deterministic (the encoding's identity hashes it).
    internal static class SettlementEntityFactory
    {
        public const string Version = "mmr40/2";
        // The installed campaign map's scene levels (NavalDLC Main_map <levels>); the generated template declares only base and level_1..3.
        public static readonly KeyValuePair<string, uint>[] SceneLevels = {
            new KeyValuePair<string, uint>("base", 1), new KeyValuePair<string, uint>("level_1", 2), new KeyValuePair<string, uint>("level_2", 4), new KeyValuePair<string, uint>("level_3", 8),
            new KeyValuePair<string, uint>("civilian", 16), new KeyValuePair<string, uint>("siege", 32), new KeyValuePair<string, uint>("looted", 64) };

        public static uint LevelMask(IEnumerable<string> names)
        {
            uint mask = 0;
            foreach (var n in names)
            {
                var level = SceneLevels.FirstOrDefault(l => l.Key == n);
                if (level.Key == null) throw new ArgumentException("No scene level " + n);
                mask |= level.Value;
            }
            return mask;
        }

        public static List<EntitySpec> Build(SettlementLayout layout, IPlacementGround ground, SettlementEntityContract contract, SettlementPlacementPolicy policy,
                                             string helperPrefix, double extent, double maxHeight)
        {
            contract.RequireMeshes(policy.SolidWallMesh, policy.BrokenWallMesh);
            var roots = new List<EntitySpec> {
                new EntitySpec { Name = "border_min", Position = new double[] { 0, 0, 0 } },
                new EntitySpec { Name = "border_max", Position = new[] { extent, extent, maxHeight } } };
            foreach (var name in SettlementEntityContract.BakedHelperScripts)
                roots.Add(new EntitySpec { Name = helperPrefix + "_helper_" + new string(name.ToLowerInvariant().Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_').ToArray()), RawScripts = contract.Helpers[name] });
            foreach (var s in layout.Settlements) roots.Add(Settlement(s, ground, policy));
            return roots;
        }

        public static EntitySpec Settlement(ResolvedSettlement s, IPlacementGround ground, SettlementPlacementPolicy policy)
        {
            var site = s.Site; string id = site.Id;
            var c = ground.Physical(s.Centre[0], s.Centre[1]);
            var root = new EntitySpec { Name = id, Position = new[] { c[0], c[1], ground.Elevation(s.Centre[0], s.Centre[1]) } };
            Func<double[], bool, double[]> local = (logical, water) => {
                if (logical == null) return null;
                double dx = SettlementPositionResolver.Delta(ground, s.Centre[0], logical[0]), dy = logical[1] - s.Centre[1];
                double z = water ? 0 : ground.Elevation(logical[0], logical[1]);
                return new[] { dx, dy, z - root.Position[2] };
            };
            if (site.Kind == SiteKind.Hideout)
            {
                root.Tags.Add("wm_hideout"); root.Physics = "bo_sphere_collider"; root.Flags.Add("align_to_terrain");
                root.Script("Town Entity Manager", "Override Factor Color", "false", "Factor Color", "1, 1, 1, 1");
                root.Child(id + "_icon", null).Mesh = SettlementEntityContract.IconMeshes[SiteKind.Hideout];
                return root;
            }
            root.Tags.Add(site.Kind == SiteKind.Town ? "town" : site.Kind == SiteKind.Castle ? "castle" : "village");
            root.Script("Town Entity Manager", "Override Factor Color", "false", "Factor Color", "1, 1, 1, 1");
            if (site.Kind == SiteKind.Village)
            {
                for (int n = 0; n < 3; n++)
                {
                    var icon = root.Child(id + (n == 0 ? "_icon" : "_icon_" + (n + 1)), null);
                    icon.Mesh = SettlementEntityContract.VillageLevelIcons[n]; icon.Levels.AddRange(new[] { SettlementPlacementPolicy.WallLevels[n], "civilian" });
                }
                var looted = root.Child(id + "_icon_looted", null);
                looted.Mesh = SettlementEntityContract.VillageLevelIcons[0]; looted.Levels.AddRange(SettlementPlacementPolicy.WallLevels.Concat(new[] { "looted" }));
            }
            else root.Child(id + "_icon", null).Mesh = SettlementEntityContract.IconMeshes[site.Kind];
            var selection = site.Kind == SiteKind.Village ? root.Child(id + "_selection", new double[] { 0, 0, 1 }) : root.Child(id + "_selection", new double[] { 0, 0, 1 }, "bo_town");
            selection.Physics = "bo_sphere_collider"; selection.BodyFlags.Add("only_collide_with_raycast");
            var circle = root.Child(id + "_circle", null, "map_settlement_circle");
            double circleScale;
            if (policy.CircleScale.TryGetValue(site.Kind == SiteKind.Town ? "town" : "castle", out circleScale) && site.Fortification) circle.Scale = new[] { circleScale, circleScale, circleScale };
            int banners = site.Kind == SiteKind.Town ? policy.TownBanners : site.Kind == SiteKind.Castle ? policy.CastleBanners : policy.VillageBanners;
            for (int n = 0; n < banners; n++)
            {
                // SettlementVisual.GetBannerPositionForParty calls GetChild(0) on a placeholder; a childless one faults natively (MMR-33).
                var banner = root.Child(id + "_banner_" + n, new[] { n - (banners - 1) / 2.0, 0, 2 }, "map_banner_placeholder");
                var volume = banner.Child(id + "_banner_" + n + "_volume", new[] { 0.5, 0.5, -0.5 });
                volume.EditorOnly = true; volume.Mesh = "volume_box"; volume.Script("VolumeBox", "NavMeshPrefabName", "");
            }
            if (site.Harbour && s.Dropoff != null) root.Child(id + "_dropoff", local(s.Dropoff, true), "main_map_village_dropoff");
            if (!site.Fortification || s.Gate == null) return root;
            double yaw = s.GateAngle - Math.PI / 2;
            root.Child(id + "_gate", local(s.Gate, false), "main_map_city_gate").Yaw = yaw;
            foreach (var m in s.Markers)
            {
                var marker = root.Child(id + "_" + m.Suffix, local(m.Logical, false), m.Tag);
                marker.Yaw = yaw;
                double[] camp; if (policy.CampScale.TryGetValue(m.Tag, out camp)) marker.Scale = new[] { camp[0], camp[1], 1.0 };
                if (m.Level != null) marker.Levels.AddRange(policy.WallEngineLevelSets[m.Level]);
            }
            foreach (var w in s.Walls)
            {
                string name = id + "_" + w.Suffix;
                var wall = root.Child(name, local(w.Logical, false), "map_breachable_wall");
                wall.Yaw = yaw; wall.Levels.AddRange(policy.WallLevelSets[w.Level]);
                // Raycast-only, like the selection spheres: a plain sphere answers ground and ray queries over a neighbouring village (run 34096).
                var collider = wall.Child(name + "_collider", null); collider.Physics = "bo_sphere_collider"; collider.BodyFlags.Add("only_collide_with_raycast");
                wall.Child(name + "_broken", null, "map_broken_wall").Mesh = policy.BrokenWallMesh;
                wall.Child(name + "_solid", null, "map_solid_wall").Mesh = policy.SolidWallMesh;
            }
            if (site.HasPort && s.Port != null)
            {
                root.Child(id + "_port", local(s.Port, true), "main_map_city_port");   // must be a direct child
                var vars = new List<string>();
                foreach (var kv in new[] { "MaximumNumberOfShips", "NumberOfArcs", "DistanceBetweenShips" }) { vars.Add(kv); vars.Add(policy.Blockade[kv]); }
                vars.AddRange(new[] { "DistanceRandomizationOnArcs", "0.100", "DistanceRandomizationBetweenArcs", "0.100", "Angle", policy.Blockade["Angle"], "MissionShipId", policy.Blockade["MissionShipId"],
                    "ShipScaleFactor", policy.Blockade["ShipScaleFactor"], "IsVisualizationEnabled", "false", "IsRandomizationEnabled", "false", "IsShipVisualizationEnabled", "false" });
                if (s.ArcStart != null && s.ArcEnd != null)
                {
                    root.Script("BlockadePositionScript", vars.ToArray());
                    root.Child(id + "_blockade_start", local(s.ArcStart, true), "Blockade_Arc_Start");
                    root.Child(id + "_blockade_end", local(s.ArcEnd, true), "Blockade_Arc_End");
                }
            }
            return root;
        }

        // A settlement's full hierarchy without a resolved layout (SettlementEntityRuntime, for a settlement the scene lacks), from its record:
        // the centre, the record's gate (a fortification's siege markers and walls at their installed offsets in that gate's frame, with no
        // ground checks) and the record's port (no blockade arc: fitting one needs the world's water). Physical positions.
        public static EntitySpec Unresolved(string id, SiteKind kind, double[] physical, double[] gate, double[] port, SettlementPlacementPolicy policy)
        {
            var site = new SettlementSite { Id = id, Kind = kind, Source = "runtime (no scene entity)", Centre = new[] { physical[0], physical[1] }, HasPort = kind == SiteKind.Town && port != null };
            var flat = new FlatGround(physical[2]);
            var resolved = SettlementPositionResolver.Unadjusted(site, site.Centre, gate, port, flat, policy);
            return Settlement(resolved, flat, policy);
        }

        // A ground at one height where logical is physical (Unresolved: positions are already physical).
        internal sealed class FlatGround : IPlacementGround
        {
            private readonly double z;
            public FlatGround(double z) { this.z = z; }
            public double Width { get { return 1e9; } }
            public double Height { get { return 1e9; } }
            public double Spacing { get { return 1; } }
            public byte Class(double x, double y) { return TravelCells.Land; }
            public int Land(double x, double y) { return 0; }
            public int Basin(double x, double y) { return -1; }
            public double Elevation(double x, double y) { return z; }
            public double Slope(double x, double y) { return 0; }
            public double[] Physical(double x, double y) { return new[] { x, y }; }
        }

        // The specs as <game_entity> elements (no XML declaration), in order, for a scene's <entities>.
        public static string ToXml(IEnumerable<EntitySpec> roots)
        {
            var doc = new XmlDocument();
            var container = doc.CreateElement("entities"); doc.AppendChild(container);
            foreach (var r in roots) container.AppendChild(Element(doc, r));
            var text = new StringBuilder();
            using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "\t", NewLineChars = "\n", ConformanceLevel = ConformanceLevel.Fragment }))
                foreach (XmlNode child in container.ChildNodes) child.WriteTo(writer);
            return text.ToString();
        }

        private static XmlElement Element(XmlDocument doc, EntitySpec e)
        {
            var x = doc.CreateElement("game_entity");
            x.SetAttribute("name", e.Name); x.SetAttribute("mobility", "1");
            if (e.Flags.Count > 0) { var flags = Sub(doc, x, "flags"); foreach (var f in e.Flags) { var fl = Sub(doc, flags, "flag"); fl.SetAttribute("name", f); fl.SetAttribute("value", "true"); } }
            if (e.EditorOnly) { var masks = Sub(doc, x, "visibility_masks"); var m = Sub(doc, masks, "visibility_mask"); m.SetAttribute("name", "visible_only_when_editing"); m.SetAttribute("value", "true"); }
            if (e.Tags.Count > 0) { var tags = Sub(doc, x, "tags"); foreach (var t in e.Tags) Sub(doc, tags, "tag").SetAttribute("name", t); }
            var transform = Sub(doc, x, "transform");
            transform.SetAttribute("position", String.Join(", ", e.Position.Select(N)));
            transform.SetAttribute("rotation_euler", "0, 0, " + N(e.Yaw));
            if (e.Scale != null) transform.SetAttribute("scale", String.Join(", ", e.Scale.Select(N)));
            if (e.Physics != null)
            {
                var physics = Sub(doc, x, "physics"); physics.SetAttribute("shape", e.Physics);
                if (e.BodyFlags.Count > 0) { var flags = Sub(doc, physics, "body_flags"); foreach (var f in e.BodyFlags) Sub(doc, flags, "body_flag").SetAttribute("name", f); }
            }
            if (e.Mesh != null) Sub(doc, Sub(doc, x, "components"), "meta_mesh_component").SetAttribute("name", e.Mesh);
            if (e.Scripts.Count > 0 || e.RawScripts != null)
            {
                var scripts = Sub(doc, x, "scripts");
                if (e.RawScripts != null) { var raw = new XmlDocument(); raw.LoadXml(e.RawScripts); scripts.AppendChild(doc.ImportNode(raw.DocumentElement, true)); }
                foreach (var s in e.Scripts)
                {
                    var script = Sub(doc, scripts, "script"); script.SetAttribute("name", s.Key);
                    if (s.Value.Count > 0) { var vars = Sub(doc, script, "variables"); foreach (var v in s.Value) { var variable = Sub(doc, vars, "variable"); variable.SetAttribute("name", v.Key); variable.SetAttribute("value", v.Value); } }
                }
            }
            if (e.Levels.Count > 0) { var levels = Sub(doc, x, "levels"); foreach (var l in e.Levels) Sub(doc, levels, "level").SetAttribute("name", l); }
            if (e.Children.Count > 0) { var children = Sub(doc, x, "children"); foreach (var c in e.Children) children.AppendChild(Element(doc, c)); }
            return x;
        }

        // Splices entity XML into a scene's <entities> (after what it holds). The scene text must hold exactly one <entities> element.
        public static string Splice(string sceneXml, string entitiesXml)
        {
            const string close = "</entities>";
            int at = sceneXml.IndexOf(close, StringComparison.Ordinal);
            if (at < 0 || sceneXml.IndexOf(close, at + close.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("The scene must hold exactly one </entities>.");
            return sceneXml.Substring(0, at) + entitiesXml + "\n" + sceneXml.Substring(at);
        }

        private static readonly Regex LevelDeclaration = new Regex("<level\\s+name=\"([^\"]+)\"\\s+mask=\"(\\d+)\"\\s*/>", RegexOptions.CultureInvariant);

        // Declares the installed campaign map's scene levels in a scene's own <levels> (the one before <entities>), adding the missing ones:
        // the settlement entities' levels (village icons, walls, wall engines) are resolved against it, and SettlementVisual's level mask
        // asks the scene for civilian, siege and looted. A declared level with another mask, or a mask taken by another name, is refused.
        public static string DeclareSceneLevels(string sceneXml)
        {
            int entities = sceneXml.IndexOf("<entities", StringComparison.Ordinal);
            int open = sceneXml.IndexOf("<levels>", StringComparison.Ordinal), close = open < 0 ? -1 : sceneXml.IndexOf("</levels>", open, StringComparison.Ordinal);
            if (open < 0 || close < 0 || (entities >= 0 && open > entities))
            {
                int sceneOpen = sceneXml.IndexOf("<scene", StringComparison.Ordinal), sceneEnd = sceneOpen < 0 ? -1 : sceneXml.IndexOf('>', sceneOpen);
                if (sceneEnd < 0) throw new InvalidDataException("No <scene> element to declare levels in.");
                return sceneXml.Substring(0, sceneEnd + 1) + "\n\t<levels>\n" + String.Concat(SceneLevels.Select(l => "\t\t<level name=\"" + l.Key + "\" mask=\"" + l.Value + "\" />\n")) + "\t</levels>" + sceneXml.Substring(sceneEnd + 1);
            }
            string block = sceneXml.Substring(open, close - open);
            var declared = LevelDeclaration.Matches(block).Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => UInt32.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            var added = new StringBuilder();
            foreach (var level in SceneLevels)
            {
                uint mask;
                if (declared.TryGetValue(level.Key, out mask)) { if (mask != level.Value) throw new InvalidDataException("The scene declares level " + level.Key + " with mask " + mask + ", not " + level.Value + "."); continue; }
                var other = declared.FirstOrDefault(d => d.Value == level.Value);
                if (other.Key != null) throw new InvalidDataException("The scene's level " + other.Key + " takes mask " + level.Value + " (" + level.Key + ").");
                added.Append("\t\t<level name=\"").Append(level.Key).Append("\" mask=\"").Append(level.Value).Append("\" />\n");
            }
            if (added.Length == 0) return sceneXml;
            int lineStart = sceneXml.LastIndexOf('\n', close) + 1;
            return sceneXml.Substring(0, lineStart) + added + sceneXml.Substring(lineStart);
        }

        // The structural contract of a scene's settlement entities against its layout (c1/c2): the scene levels, one root per settlement named
        // its id, with the kind's tag and scripts, the required children directly under it, every marker, wall section and wall engine by name
        // with its tag, offset and levels, positions within tolerance of the layout, unique entity names, borders and helpers. Returns the
        // problems (empty when the scene is right).
        public static List<string> Validate(string sceneXml, SettlementLayout layout, IPlacementGround ground, SettlementPlacementPolicy policy, string helperPrefix, double tolerance = 1e-3)
        {
            var problems = new List<string>();
            var doc = new XmlDocument(); doc.LoadXml(sceneXml.TrimStart('\uFEFF'));
            foreach (var level in SceneLevels)
                if (doc.SelectSingleNode("/scene/levels/level[@name='" + level.Key + "' and @mask='" + level.Value + "']") == null) problems.Add("scene level " + level.Key + " (" + level.Value + ") is not declared");
            var all = doc.SelectNodes("//game_entity").Cast<XmlElement>().ToList();
            foreach (var dup in all.GroupBy(e => e.GetAttribute("name")).Where(g => g.Count() > 1)) problems.Add("entity name " + dup.Key + " appears " + dup.Count() + " times");
            var roots = doc.SelectNodes("/scene/entities/game_entity").Cast<XmlElement>().GroupBy(e => e.GetAttribute("name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var b in new[] { "border_min", "border_max" }) if (!roots.ContainsKey(b)) problems.Add("no " + b);
            foreach (var h in SettlementEntityContract.HelperScripts)
            {
                bool baked = SettlementEntityContract.BakedHelperScripts.Contains(h);
                if (doc.SelectNodes("/scene/entities/game_entity[starts-with(@name,'" + helperPrefix + "_helper_')]/scripts/script[@name='" + h + "']").Count != (baked ? 1 : 0)) problems.Add("helper " + h + " is not " + (baked ? "present once" : "absent"));
            }
            foreach (var s in layout.Settlements)
            {
                string id = s.Site.Id; XmlElement e;
                if (!roots.TryGetValue(id, out e)) { problems.Add(id + ": no root entity"); continue; }
                string tag = s.Site.Kind == SiteKind.Town ? "town" : s.Site.Kind == SiteKind.Castle ? "castle" : s.Site.Kind == SiteKind.Village ? "village" : "wm_hideout";
                if (e.SelectSingleNode("tags/tag[@name='" + tag + "']") == null) problems.Add(id + ": root lacks tag " + tag);
                if (e.SelectSingleNode("scripts/script[@name='Town Entity Manager']") == null) problems.Add(id + ": root lacks Town Entity Manager");
                var c = ground.Physical(s.Centre[0], s.Centre[1]); var at = Position(e);
                if (Math.Abs(at[0] - c[0]) > tolerance || Math.Abs(at[1] - c[1]) > tolerance) problems.Add(id + ": root at " + at[0] + "," + at[1] + ", layout centre " + c[0] + "," + c[1]);
                Func<string, List<XmlElement>> direct = t => e.SelectNodes("children/game_entity[tags/tag/@name='" + t + "']").Cast<XmlElement>().ToList();
                Action<XmlElement, string, double[]> offset = (child, what, logical) => {
                    var p = Position(child);
                    double dx = SettlementPositionResolver.Delta(ground, s.Centre[0], logical[0]), dy = logical[1] - s.Centre[1];
                    if (Math.Abs(p[0] - dx) > tolerance || Math.Abs(p[1] - dy) > tolerance) problems.Add(id + ": " + what + " at offset " + p[0] + "," + p[1] + ", layout " + dx + "," + dy);
                };
                Action<string, int, double[]> expect = (t, count, logical) => {
                    var found = direct(t);
                    if (found.Count != count) { problems.Add(id + ": " + found.Count + " direct children tagged " + t + ", expected " + count); return; }
                    if (logical != null && count == 1) offset(found[0], t, logical);
                };
                Func<XmlElement, string> levelsOf = x => String.Join(",", x.SelectNodes("levels/level").Cast<XmlElement>().Select(l => l.GetAttribute("name")));
                if (s.Site.Kind == SiteKind.Hideout) continue;
                expect("map_settlement_circle", 1, null);
                int banners = s.Site.Kind == SiteKind.Town ? policy.TownBanners : s.Site.Kind == SiteKind.Castle ? policy.CastleBanners : policy.VillageBanners;
                var placeholders = direct("map_banner_placeholder");
                if (placeholders.Count != banners || placeholders.Any(b => b.SelectNodes("children/game_entity").Count != 1 || b.SelectSingleNode("children/game_entity/components/meta_mesh_component[@name='volume_box']") == null))
                    problems.Add(id + ": banner placeholders are not " + banners + " each with one volume_box child");
                if (s.Site.Kind == SiteKind.Village)
                {
                    var icons = e.SelectNodes("children/game_entity[starts-with(@name,'" + id + "_icon')]").Cast<XmlElement>().Select(levelsOf).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var expected = new[] { "level_1,civilian", "level_2,civilian", "level_3,civilian", "level_1,level_2,level_3,looted" }.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (!icons.SequenceEqual(expected)) problems.Add(id + ": village icons' levels are [" + String.Join("; ", icons) + "], not one per level and a looted one");
                    if (e.SelectSingleNode("children/game_entity[physics/@shape='bo_sphere_collider']") == null) problems.Add(id + ": no selection collider");
                    expect("main_map_village_dropoff", s.Site.Harbour ? 1 : 0, s.Dropoff);
                    continue;
                }
                expect("bo_town", 1, null);
                expect("main_map_city_gate", 1, s.Gate);
                foreach (var m in s.Markers.Concat(s.Walls))
                {
                    var child = e.SelectSingleNode("children/game_entity[@name='" + id + "_" + m.Suffix + "']") as XmlElement;
                    if (child == null) { problems.Add(id + ": no " + m.Suffix + " child"); continue; }
                    if (child.SelectSingleNode("tags/tag[@name='" + m.Tag + "']") == null) problems.Add(id + ": " + m.Suffix + " lacks tag " + m.Tag);
                    offset(child, m.Suffix, m.Logical);
                    if (m.Level != null)
                    {
                        var want = String.Join(",", m.Tag == "map_breachable_wall" ? policy.WallLevelSets[m.Level] : policy.WallEngineLevelSets[m.Level]);
                        if (levelsOf(child) != want) problems.Add(id + ": " + m.Suffix + " has levels [" + levelsOf(child) + "], not [" + want + "]");
                    }
                    if (m.Tag == "map_breachable_wall" && (child.SelectNodes("children/game_entity[tags/tag/@name='map_solid_wall']").Count != 1 || child.SelectNodes("children/game_entity[tags/tag/@name='map_broken_wall']").Count != 1))
                        problems.Add(id + ": wall " + m.Suffix + " lacks its one solid and one broken wall child");
                }
                foreach (var g in s.Markers.Concat(s.Walls).GroupBy(m => m.Tag))
                    if (direct(g.Key).Count != g.Count()) problems.Add(id + ": " + direct(g.Key).Count + " " + g.Key + " children, expected " + g.Count());
                foreach (var level in SettlementPlacementPolicy.WallLevels)
                {
                    if (s.Walls.Count(w => w.Level == level) != 2) problems.Add(id + ": " + s.Walls.Count(w => w.Level == level) + " breachable wall sections at " + level + ", expected 2");
                    if (s.Markers.Count(m => m.Level == level) != 4) problems.Add(id + ": " + s.Markers.Count(m => m.Level == level) + " wall engines at " + level + ", expected 4");
                }
                expect("main_map_city_port", s.Site.HasPort ? 1 : 0, s.Port);
                bool blockade = s.Site.HasPort && s.ArcStart != null;
                expect("Blockade_Arc_Start", blockade ? 1 : 0, s.ArcStart);
                expect("Blockade_Arc_End", blockade ? 1 : 0, s.ArcEnd);
                if ((e.SelectSingleNode("scripts/script[@name='BlockadePositionScript']") != null) != blockade) problems.Add(id + ": BlockadePositionScript " + (blockade ? "missing" : "on a town without a blockade arc"));
            }
            return problems;
        }

        private static double[] Position(XmlElement e)
        {
            var t = e.SelectSingleNode("transform") as XmlElement;
            return t == null ? new double[] { 0, 0, 0 } : t.GetAttribute("position").Split(',').Select(v => Double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }

        private static XmlElement Sub(XmlDocument doc, XmlElement parent, string name) { var e = doc.CreateElement(name); parent.AppendChild(e); return e; }
        private static string N(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }
}
