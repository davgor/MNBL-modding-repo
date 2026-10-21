using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.Surface
{
    // A transform frame in a look's root-local coordinates: rows side, forward and up (TaleWorlds.Library.Mat3) and an origin. A child's frame
    // composes its transform as tools/make_settlement_placement_policy.py does: position, then rotation_euler as Mat3.ApplyEulerAngles (about up
    // by z, about side by x, about forward by y), then the scale.
    internal sealed class LookFrame
    {
        public double[] O = { 0, 0, 0 }, S = { 1, 0, 0 }, F = { 0, 1, 0 }, U = { 0, 0, 1 };

        public double[] Apply(double[] p) { return new[] { O[0] + S[0] * p[0] + F[0] * p[1] + U[0] * p[2], O[1] + S[1] * p[0] + F[1] * p[1] + U[1] * p[2], O[2] + S[2] * p[0] + F[2] * p[1] + U[2] * p[2] }; }
        private double[] Rotate(double[] v) { return new[] { S[0] * v[0] + F[0] * v[1] + U[0] * v[2], S[1] * v[0] + F[1] * v[1] + U[1] * v[2], S[2] * v[0] + F[2] * v[1] + U[2] * v[2] }; }
        // The z rotation of the forward axis about up, and the side axis's length (the uniform scale a hoisted entity keeps).
        public double Yaw { get { return Math.Atan2(-F[0], F[1]); } }
        public double Scale { get { return Math.Sqrt(S[0] * S[0] + S[1] * S[1] + S[2] * S[2]); } }
        public double Radius { get { return Math.Sqrt(O[0] * O[0] + O[1] * O[1]); } }

        public LookFrame Child(XmlElement e)
        {
            var t = e.SelectSingleNode("transform") as XmlElement;
            var p = Vec(t, "position", 0); var r = Vec(t, "rotation_euler", 0); var k = Vec(t, "scale", 1);
            double[] s = { 1, 0, 0 }, f = { 0, 1, 0 }, u = { 0, 0, 1 };
            double c = Math.Cos(r[2]), n = Math.Sin(r[2]); var s1 = Add(Mul(s, c), Mul(f, n)); f = Add(Mul(f, c), Mul(s, -n)); s = s1;   // about up
            c = Math.Cos(r[0]); n = Math.Sin(r[0]); var f1 = Add(Mul(f, c), Mul(u, n)); u = Add(Mul(u, c), Mul(f, -n)); f = f1;          // about side
            c = Math.Cos(r[1]); n = Math.Sin(r[1]); s1 = Add(Mul(s, c), Mul(u, -n)); u = Add(Mul(u, c), Mul(s, n)); s = s1;              // about forward
            return new LookFrame { O = Apply(p), S = Rotate(Mul(s, k[0])), F = Rotate(Mul(f, k[1])), U = Rotate(Mul(u, k[2])) };
        }

        public static double[] Vec(XmlElement transform, string attribute, double fill)
        {
            var text = transform == null ? null : transform.GetAttribute(attribute);
            if (String.IsNullOrEmpty(text)) return new[] { fill, fill, fill };
            var v = text.Split(',').Select(x => Double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (v.Length != 3) throw new InvalidDataException("A transform " + attribute + " is not three numbers: " + text);
            return v;
        }
        private static double[] Mul(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }
        private static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    }

    // One functional entity of a look (MMR-258): a gate-frame marker, a breachable wall section or a wall engine, at full scale in the look's
    // gate frame (forward toward its city gate, side to the right), with its look-local yaw and scale, and for a wall section its solid and
    // broken wall children (normalized XML, at most one of each).
    internal sealed class LookSlot
    {
        public string Tag, Level; public int Copy;
        public double Forward, Side, Yaw, Scale = 1;
        public string Children;
    }

    // A settlement look (MMR-258, findings section 10): one town, castle or village hierarchy of the installed War Sails campaign map, named by
    // its settlement id (the prototype whose location scenes the player enters), normalized (SettlementLookCatalog.Normalize) and kept as XML:
    //   <look id kind culture pool port [portAngle] [dropoffAngle] [gateAngle]> the root's normalized children </look>
    // Everything else is extracted from that XML, so a look frozen into a world package gives the same scene as the installed one did.
    //   towns, castles  the city gate (its direction is the look's gate frame), the gate-frame markers (outer gate, siege camps, siege engines,
    //                   ram, towers), per wall level its two breachable wall sections and four wall engines, the circle's scale, and whether its
    //                   banner placeholders keep one per wall level under a parent with that level's bit (SettlementVisual groups them so)
    //   villages        the icons per hearth level and the looted icon, and the production pieces (swapped for the village's type)
    //   all             the footprint radius (every visible entity) and the core radius (what culling keeps: level-bound pieces, functional
    //                   markers; a village's icons)
    // A look whose hierarchy breaks the contract (not exactly 2 wall sections and 4 wall engines per level, a wall with more than one solid or
    // broken child, no gate, no camp, ram or tower, no icon of a level) has Problems and is never selected.
    internal sealed class SettlementLook
    {
        public static readonly string[] MarkerTags = { "main_map_castle_gate", "map_camp_area_1", "map_camp_area_2", "map_siege_engine_0", "map_siege_engine_1", "map_siege_engine_2",
                                                       "map_siege_engine_3", "map_siege_ram", "map_siege_tower" };
        // Tags SettlementVisual (or War Sails) reads from a settlement's whole hierarchy: the scene builds these itself, so a look's visual tree
        // never carries them (map_banner_placeholder stays when the look's banners conform).
        public static readonly HashSet<string> FunctionalTags = new HashSet<string>(MarkerTags.Concat(new[] { "bo_town", "main_map_city_gate", "main_map_city_port", "map_settlement_circle",
            "map_breachable_wall", "map_solid_wall", "map_broken_wall", "map_defensive_engine_0", "map_defensive_engine_1", "map_defensive_engine_2", "map_defensive_engine_3",
            "main_map_village_dropoff", "Blockade_Arc_Start", "Blockade_Arc_End" }), StringComparer.Ordinal);
        public const string BannerTag = "map_banner_placeholder";
        private static readonly string[] Levels = { "level_1", "level_2", "level_3" };

        public string Id, Kind, Culture, Pool, Xml, Sha256;
        public bool Port;
        public double? PortAngle, DropoffAngle;
        public double GateAngle, GateDistance, CircleScale = 1, Radius, CoreRadius;
        public bool OwnBanners, HasPier;
        public int ProductionPieces;
        public readonly List<LookSlot> Slots = new List<LookSlot>();
        public readonly List<string> Problems = new List<string>();
        public bool Conforms { get { return Problems.Count == 0; } }
        public XmlElement Root { get; private set; }

        public static SettlementLook FromXml(string xml, SettlementLooksPolicy policy)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            if (root.Name != "look") throw new InvalidDataException("A settlement look must be a <look> element.");
            Func<string, double?> angle = a => root.HasAttribute(a) ? (double?)Double.Parse(root.GetAttribute(a), CultureInfo.InvariantCulture) : null;
            var look = new SettlementLook { Id = root.GetAttribute("id"), Kind = root.GetAttribute("kind"), Culture = root.GetAttribute("culture"), Pool = root.GetAttribute("pool"),
                                            Port = root.GetAttribute("port") == "true", PortAngle = angle("portAngle"), DropoffAngle = angle("dropoffAngle"), Xml = xml, Sha256 = Sha(xml), Root = root };
            if (String.IsNullOrEmpty(look.Id) || (look.Kind != "town" && look.Kind != "castle" && look.Kind != "village") || String.IsNullOrEmpty(look.Culture))
                throw new InvalidDataException("A settlement look needs an id, a town, castle or village kind and a culture.");
            look.Extract(policy);
            return look;
        }

        // The entities under the look root with their frames, in document order (each entity before its children).
        public static IEnumerable<KeyValuePair<XmlElement, LookFrame>> Walk(XmlElement parent, LookFrame frame)
        {
            foreach (var child in Children(parent))
            {
                var f = frame.Child(child);
                yield return new KeyValuePair<XmlElement, LookFrame>(child, f);
                foreach (var d in Walk(child, f)) yield return d;
            }
        }
        public static IEnumerable<XmlElement> Children(XmlElement e)
        {
            var c = e.Name == "look" ? e : e.SelectSingleNode("children") as XmlElement;
            return c == null ? Enumerable.Empty<XmlElement>() : c.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "game_entity");
        }
        public static List<string> Tags(XmlElement e) { return e.SelectNodes("tags/tag").Cast<XmlElement>().Select(t => t.GetAttribute("name")).ToList(); }
        public static List<string> LevelNames(XmlElement e) { return e.SelectNodes("levels/level").Cast<XmlElement>().Select(t => t.GetAttribute("name")).ToList(); }
        public static string MeshOf(XmlElement e) { var m = e.SelectSingleNode("components/meta_mesh_component") as XmlElement; return m == null ? null : m.GetAttribute("name"); }
        public static bool Visible(XmlElement e) { return e.SelectSingleNode("components/meta_mesh_component|components/decal_component") != null; }
        public static bool Functional(XmlElement e) { return Tags(e).Any(FunctionalTags.Contains); }
        // Whether an entity or one of its look ancestors carries a wall level bit (it shows with a wall or hearth level: never culled).
        public static bool LevelBound(XmlElement e)
        {
            for (var x = e; x != null && x.Name == "game_entity"; x = x.ParentNode == null ? null : x.ParentNode.ParentNode as XmlElement)
                if (LevelNames(x).Any(l => Levels.Contains(l))) return true;
            return false;
        }
        public bool IsProduction(XmlElement e, SettlementLooksPolicy policy)
        {
            var mesh = MeshOf(e);
            return e.GetAttribute("name").StartsWith("map_icons_production_", StringComparison.Ordinal) || (mesh != null && policy.Production.Values.Any(v => v.Contains(mesh)));
        }

        private void Extract(SettlementLooksPolicy policy)
        {
            var all = Walk(Root, new LookFrame()).ToList();
            foreach (var kv in all)
            {
                if (!Visible(kv.Key) || Functional(kv.Key)) continue;
                double r = kv.Value.Radius;
                Radius = Math.Max(Radius, r);
                bool core = Kind == "village" ? IsIcon(kv.Key) : LevelBound(kv.Key);
                if (core) CoreRadius = Math.Max(CoreRadius, r);
                if (Kind == "village" && IsProduction(kv.Key, policy)) ProductionPieces++;
                var mesh = MeshOf(kv.Key);
                if (mesh != null && (mesh.Contains("pier") || mesh.Contains("dock"))) HasPier = true;
            }
            if (Kind == "village")
            {
                foreach (var level in Levels.Select((l, i) => new { l, i }))
                    if (all.Count(kv => IconLevel(kv.Key) == level.i + 1) != 1) Problems.Add("not one icon of " + level.l);
                if (all.Count(kv => IconLevel(kv.Key) == 0) != 1) Problems.Add("not one looted icon");
                return;
            }
            var gates = all.Where(kv => Tags(kv.Key).Contains("main_map_city_gate")).ToList();
            if (gates.Count != 1) { Problems.Add(gates.Count + " city gates"); return; }
            var g = gates[0].Value.O;
            GateAngle = Math.Atan2(g[1], g[0]); GateDistance = Math.Sqrt(g[0] * g[0] + g[1] * g[1]);
            if (GateDistance < 1e-6) { Problems.Add("the city gate stands at the centre"); return; }
            CoreRadius = Math.Max(CoreRadius, GateDistance);
            Func<LookFrame, double[]> gateFrame = f => new[] { f.O[0] * Math.Cos(GateAngle) + f.O[1] * Math.Sin(GateAngle), f.O[0] * Math.Sin(GateAngle) - f.O[1] * Math.Cos(GateAngle) };
            var copies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in all)
            {
                var tags = Tags(kv.Key);
                string tag = tags.FirstOrDefault(t => MarkerTags.Contains(t) || t == "map_breachable_wall" || t.StartsWith("map_defensive_engine_", StringComparison.Ordinal));
                if (tag == null) continue;
                string level = null;
                if (tag == "map_breachable_wall" || tag.StartsWith("map_defensive_engine_", StringComparison.Ordinal))
                {
                    var bits = LevelNames(kv.Key).Where(l => Levels.Contains(l)).ToList();
                    if (bits.Count != 1) { Problems.Add(tag + " " + kv.Key.GetAttribute("name") + " has wall levels [" + String.Join(",", bits) + "], not one"); continue; }
                    level = bits[0];
                }
                string key = tag + "/" + level; int copy; copies.TryGetValue(key, out copy); copies[key] = copy + 1;
                var fg = gateFrame(kv.Value);
                var slot = new LookSlot { Tag = tag, Level = level, Copy = copy, Forward = fg[0], Side = fg[1], Yaw = kv.Value.Yaw, Scale = kv.Value.Scale };
                if (tag == "map_breachable_wall")
                {
                    var kids = Children(kv.Key).ToList();
                    int solid = kids.Count(k => Tags(k).Contains("map_solid_wall")), broken = kids.Count(k => Tags(k).Contains("map_broken_wall"));
                    if (solid > 1 || broken > 1) Problems.Add("wall " + kv.Key.GetAttribute("name") + " has " + solid + " solid and " + broken + " broken children");
                    var text = new StringBuilder();
                    foreach (var k in kids.Where(k => Tags(k).Contains("map_solid_wall") || Tags(k).Contains("map_broken_wall"))) text.Append(k.OuterXml);
                    slot.Children = text.ToString();
                }
                CoreRadius = Math.Max(CoreRadius, kv.Value.Radius);
                Slots.Add(slot);
            }
            foreach (var level in Levels)
            {
                if (Slots.Count(s => s.Tag == "map_breachable_wall" && s.Level == level) != 2) Problems.Add(Slots.Count(s => s.Tag == "map_breachable_wall" && s.Level == level) + " wall sections at " + level + ", not 2");
                // Four wall engines per level (8 installed Battanian castles have two map_defensive_engine_3 and no _0 at level 3).
                int engines = Slots.Count(s => s.Tag.StartsWith("map_defensive_engine_", StringComparison.Ordinal) && s.Level == level);
                if (engines != 4) Problems.Add(engines + " wall engines at " + level + ", not 4");
            }
            foreach (var t in new[] { "map_camp_area_1", "map_camp_area_2", "map_siege_ram", "map_siege_tower" })
                if (!Slots.Any(s => s.Tag == t)) Problems.Add("no " + t);
            var circle = all.FirstOrDefault(kv => Tags(kv.Key).Contains("map_settlement_circle"));
            if (circle.Key != null) CircleScale = circle.Value.Scale;
            // Banners: SettlementVisual groups the placeholders by their parent's wall level and asserts one at each level.
            var banners = all.Where(kv => Tags(kv.Key).Contains(BannerTag)).Select(kv => kv.Key.ParentNode.ParentNode as XmlElement).ToList();
            OwnBanners = banners.Count > 0 && banners.All(p => p != null && p.Name == "game_entity" && LevelNames(p).Any(l => Levels.Contains(l)))
                && Levels.All(l => banners.Any(p => LevelNames(p).Contains(l)))
                && all.Where(kv => Tags(kv.Key).Contains(BannerTag)).All(kv => Children(kv.Key).Count() == 1);
        }

        // A village icon's hearth level (1..3: [level_N, civilian] with a village_ mesh; 0: the looted icon), else -1.
        public static int IconLevel(XmlElement e)
        {
            var mesh = MeshOf(e);
            if (mesh == null || !mesh.StartsWith("village_", StringComparison.Ordinal)) return -1;
            var levels = LevelNames(e);
            if (levels.Contains("looted") && Levels.All(levels.Contains)) return 0;
            // [level_N, civilian], in castle villages [level_N, civilian, siege].
            var wall = levels.Where(l => Levels.Contains(l)).ToList();
            if (wall.Count == 1 && levels.Contains("civilian") && !levels.Contains("looted")) return Array.IndexOf(Levels, wall[0]) + 1;
            return -1;
        }
        private static bool IsIcon(XmlElement e) { return IconLevel(e) >= 0; }

        public static string Sha(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
    }

    // The look catalog (MMR-258): every town, castle and village hierarchy of the player's installed War Sails campaign map whose id
    // worldgen/templates/settlements/prototypes.json lists for a presentation culture (the prototype pools SettlementFactory picks from), each
    // normalized. Built at generation from the installed files; a world package keeps the selected looks (FrozenLooks), so no TaleWorlds XML
    // ships with the module and a world's rebuild survives a game patch. Normalization (settlement-looks.json normalize):
    //   inlinePrefabs          a nested prefab instance (prefab=, _index_) becomes an entity holding the installed prefab (Modules/*/Prefabs),
    //                          with the instance's transform, levels and tags: the nesting is what broke Player Settlement's templates;
    //                          the instance's component overrides move onto the prefab's components (MergeOverrides, MMR-247)
    //   stripEditorData        old_prefab_name, _index_ and edit_mode_data go, and every editor-only entity except a banner placeholder's
    //                          volume (SettlementVisual reads its child 0)
    //   stripSceneLeveler      SceneLeveler scripts go; so do Town Entity Manager scripts below the root (the scene's root carries it)
    //   collidersRaycastOnly   every physics shape answers rays only (a solid look collider answers ground queries over its neighbours)
    //   dropSourcePorts, dropArcs, dropDropoffs   the source's port, blockade arc ends and drop-offs go (their directions are kept for the
    //                          yaw); the scene adds the world's own
    internal sealed class SettlementLookCatalog
    {
        public readonly Dictionary<string, SettlementLook> Looks = new Dictionary<string, SettlementLook>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Normalized = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Missing = new List<string>();   // prototypes the installed map has no root for
        public string SourceSha256;

        private static readonly object cacheLock = new object();
        private static SettlementLookCatalog cached; private static string cachedKey;

        // The installed catalog, read once per process (Prepare reads it for every world).
        public static SettlementLookCatalog Installed(string mainMapPath, IEnumerable<string> prefabDirectories, byte[] prototypes, SettlementLooksPolicy policy)
        {
            lock (cacheLock)
            {
                var bytes = File.ReadAllBytes(mainMapPath);
                string key = SettlementLook.Sha(Convert.ToBase64String(bytes.Take(4096).ToArray())) + bytes.Length + "|" + policy.Sha256 + "|" + SettlementLook.Sha(Convert.ToBase64String(prototypes));
                if (cached == null || cachedKey != key) { cached = FromInstalled(bytes, PrefabResolver(prefabDirectories), prototypes, policy); cachedKey = key; }
                return cached;
            }
        }

        public static SettlementLookCatalog FromInstalled(byte[] mainMapSceneXml, Func<string, XmlElement> prefab, byte[] prototypes, SettlementLooksPolicy policy)
        {
            var catalog = new SettlementLookCatalog();
            using (var sha = SHA256.Create()) catalog.SourceSha256 = BitConverter.ToString(sha.ComputeHash(mainMapSceneXml)).Replace("-", "").ToLowerInvariant();
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(new UTF8Encoding(false).GetString(mainMapSceneXml).TrimStart('﻿'));
            var roots = doc.SelectNodes("/scene/entities/game_entity").Cast<XmlElement>().GroupBy(e => e.GetAttribute("name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var p in Pools(prototypes))
            {
                if (!policy.Kinds.Contains(p.Kind)) continue;
                XmlElement root;
                if (!roots.TryGetValue(p.Id, out root)) { catalog.Missing.Add(p.Id); continue; }
                var look = SettlementLook.FromXml(Normalize(root, p, prefab, policy, catalog.Normalized), policy);
                catalog.Looks[look.Id] = look;
            }
            return catalog;
        }

        public sealed class PoolEntry { public string Id, Culture, Kind, Pool; public bool Port; }
        // prototypes.json's pools of the presentation cultures: town_port, town_inland, castle, village_coastal, village_inland.
        public static List<PoolEntry> Pools(byte[] prototypes)
        {
            var doc = StrictJson.Parse(prototypes);
            var list = new List<PoolEntry>();
            foreach (var culture in StrictJson.Obj(doc, "cultures", "prototypes.json").Where(c => PopulationPolicy.LordCultures.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal))
                foreach (var pool in ((IDictionary<string, object>)culture.Value).OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    string kind = pool.Key.StartsWith("town", StringComparison.Ordinal) ? "town" : pool.Key == "castle" ? "castle" : pool.Key.StartsWith("village", StringComparison.Ordinal) ? "village" : null;
                    if (kind == null) continue;
                    foreach (IDictionary<string, object> e in (object[])pool.Value)
                        list.Add(new PoolEntry { Id = (string)e["id"], Culture = culture.Key, Kind = kind, Pool = pool.Key, Port = pool.Key == "town_port" || pool.Key == "village_coastal" });
                }
            return list;
        }

        // The installed prefabs by name, read on first use from Modules/<module>/Prefabs/*.xml in the given order (the first definition wins).
        public static Func<string, XmlElement> PrefabResolver(IEnumerable<string> directories)
        {
            Dictionary<string, XmlElement> prefabs = null;
            var dirs = directories.ToList();
            return name =>
            {
                if (prefabs == null)
                {
                    prefabs = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
                    foreach (var dir in dirs.Where(Directory.Exists))
                        foreach (var file in Directory.GetFiles(dir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        {
                            var d = new XmlDocument { XmlResolver = null };
                            try { d.Load(file); } catch (XmlException) { continue; }
                            foreach (var e in d.DocumentElement.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "game_entity"))
                                if (!prefabs.ContainsKey(e.GetAttribute("name"))) prefabs[e.GetAttribute("name")] = e;
                        }
                }
                XmlElement found; return prefabs.TryGetValue(name, out found) ? found : null;
            };
        }

        // One installed root as a normalized <look> (the steps above, counted in `counts`).
        public static string Normalize(XmlElement source, PoolEntry entry, Func<string, XmlElement> prefab, SettlementLooksPolicy policy, IDictionary<string, int> counts)
        {
            Action<string> count = k => { int n; counts.TryGetValue(k, out n); counts[k] = n + 1; };
            var doc = new XmlDocument { XmlResolver = null };
            var look = doc.CreateElement("look"); doc.AppendChild(look);
            look.SetAttribute("id", entry.Id); look.SetAttribute("kind", entry.Kind); look.SetAttribute("culture", entry.Culture); look.SetAttribute("pool", entry.Pool);
            look.SetAttribute("port", entry.Port ? "true" : "false");
            var children = source.SelectSingleNode("children") as XmlElement;
            if (children != null) foreach (var c in children.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "game_entity")) look.AppendChild(doc.ImportNode(c, true));
            // Prefab instances, innermost definitions included.
            if (policy.Normalize["inlinePrefabs"])
                for (int pass = 0; pass < 8; pass++)
                {
                    var instances = look.SelectNodes(".//game_entity[@prefab]").Cast<XmlElement>().ToList();
                    if (instances.Count == 0) break;
                    foreach (var i in instances)
                    {
                        string name = i.GetAttribute("prefab");
                        var definition = prefab(name);
                        if (definition == null) throw new InvalidDataException("Look " + entry.Id + " instances prefab " + name + ", which no installed module defines.");
                        i.RemoveAttribute("prefab"); i.SetAttribute("name", name);
                        var holder = i.SelectSingleNode("children") as XmlElement;
                        if (holder == null) { holder = doc.CreateElement("children"); i.AppendChild(holder); }
                        var inlined = (XmlElement)holder.AppendChild(doc.ImportNode(definition, true));
                        MergeOverrides(i, inlined, count);
                        count("prefabsInlined");
                    }
                }
            // The source's port, arcs and drop-offs: their directions first (the yaw aligns the world's).
            Func<string, double?> direction = tag => {
                var hit = SettlementLook.Walk(look, new LookFrame()).FirstOrDefault(kv => SettlementLook.Tags(kv.Key).Contains(tag));
                return hit.Key == null ? (double?)null : Math.Atan2(hit.Value.O[1], hit.Value.O[0]); };
            var port = direction("main_map_city_port"); var dropoff = direction("main_map_village_dropoff");
            if (port.HasValue) look.SetAttribute("portAngle", port.Value.ToString("R", CultureInfo.InvariantCulture));
            if (dropoff.HasValue) look.SetAttribute("dropoffAngle", dropoff.Value.ToString("R", CultureInfo.InvariantCulture));
            foreach (var e in look.SelectNodes(".//game_entity").Cast<XmlElement>().ToList())
            {
                var tags = SettlementLook.Tags(e);
                bool drop = (policy.Normalize["dropSourcePorts"] && tags.Contains("main_map_city_port")) || (policy.Normalize["dropArcs"] && (tags.Contains("Blockade_Arc_Start") || tags.Contains("Blockade_Arc_End")))
                    || (policy.Normalize["dropDropoffs"] && tags.Contains("main_map_village_dropoff"));
                if (drop && e.ParentNode != null) { e.ParentNode.RemoveChild(e); count(tags.Contains("main_map_city_port") ? "sourcePortsDropped" : tags.Contains("main_map_village_dropoff") ? "dropoffsDropped" : "arcsDropped"); }
            }
            foreach (var e in look.SelectNodes(".//game_entity").Cast<XmlElement>().ToList())
            {
                if (e.OwnerDocument == null || !IsAttached(e, look)) continue;
                if (policy.Normalize["stripEditorData"])
                {
                    var parent = e.ParentNode.Name == "children" ? e.ParentNode.ParentNode as XmlElement : null;
                    bool bannerVolume = parent != null && SettlementLook.Tags(parent).Contains(SettlementLook.BannerTag);
                    // An editor-only entity goes unless it (or something below it) is tagged: the siege markers, wall engines and camp volumes
                    // are editor-only arrows the campaign reads by tag.
                    if (!bannerVolume && e.SelectSingleNode("visibility_masks/visibility_mask[@name='visible_only_when_editing' and @value='true']") != null && e.SelectSingleNode(".//tag") == null)
                    { e.ParentNode.RemoveChild(e); count("editorEntitiesStripped"); continue; }
                    foreach (var a in new[] { "old_prefab_name", "_index_" }) if (e.HasAttribute(a)) e.RemoveAttribute(a);
                    foreach (var m in e.SelectNodes("edit_mode_data").Cast<XmlNode>().ToList()) { e.RemoveChild(m); count("editModeDataStripped"); }
                }
                var scripts = e.SelectSingleNode("scripts") as XmlElement;
                if (scripts != null)
                {
                    foreach (var s in scripts.ChildNodes.OfType<XmlElement>().ToList())
                    {
                        string name = s.GetAttribute("name");
                        if ((policy.Normalize["stripSceneLeveler"] && name == "SceneLeveler") || name == "Town Entity Manager" || name == "BlockadePositionScript") { scripts.RemoveChild(s); count(name == "SceneLeveler" ? "sceneLevelersStripped" : "nestedManagersStripped"); }
                    }
                    if (!scripts.HasChildNodes) e.RemoveChild(scripts);
                }
                var physics = e.SelectSingleNode("physics") as XmlElement;
                if (policy.Normalize["collidersRaycastOnly"] && physics != null && physics.HasAttribute("shape") && physics.SelectSingleNode("body_flags/body_flag[@name='only_collide_with_raycast']") == null)
                {
                    var flags = physics.SelectSingleNode("body_flags") as XmlElement;
                    if (flags == null) { flags = doc.CreateElement("body_flags"); physics.AppendChild(flags); }
                    var flag = doc.CreateElement("body_flag"); flag.SetAttribute("name", "only_collide_with_raycast"); flags.AppendChild(flag);
                    count("collidersMadeRaycastOnly");
                }
            }
            if (look.SelectSingleNode(".//game_entity[@prefab]") != null) throw new InvalidDataException("Look " + entry.Id + " still nests a prefab instance after normalization.");
            return look.OuterXml;
        }

        private static bool IsAttached(XmlNode e, XmlNode root) { for (var x = e; x != null; x = x.ParentNode) if (x == root) return true; return false; }

        // MMR-247 (acc4 V2): a prefab instance's components are overrides of its prefab's components, not components of their own: the n-th
        // meta_mesh_component (decal_component) of the instance edits the n-th of the prefab's root, and names no mesh (every installed
        // instance's override is nameless; it lists the prefab's meshes with new arguments, factors or materials). Inlined, an override left
        // on the holder is a meta mesh component that names nothing, which the engine draws as its magenta "REPLACE ME" box (acc4: beside
        // Hakndum, the iron mine village Sulkkas' map_icons_productions_saltmine_c holder). So each override moves onto the inlined
        // definition's component: its attributes, and each of its meshes onto the definition's mesh of that name (appended when the
        // definition lists none). An override with no component to edit, or naming another mesh, goes (counted).
        private static void MergeOverrides(XmlElement instance, XmlElement definition, Action<string> count)
        {
            var overrides = instance.SelectSingleNode("components") as XmlElement;
            if (overrides == null) return;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in overrides.ChildNodes.OfType<XmlElement>().ToList())
            {
                int n; seen.TryGetValue(o.Name, out n); seen[o.Name] = n + 1;
                var target = definition.SelectNodes("components/" + o.Name).Cast<XmlElement>().ElementAtOrDefault(n);
                if (target != null && (!o.HasAttribute("name") || o.GetAttribute("name") == target.GetAttribute("name")))
                {
                    foreach (XmlAttribute a in o.Attributes) if (a.Name != "name") target.SetAttribute(a.Name, a.Value);
                    foreach (var m in o.ChildNodes.OfType<XmlElement>())
                    {
                        var own = m.Name == "mesh" ? target.SelectNodes("mesh").Cast<XmlElement>().FirstOrDefault(x => x.GetAttribute("name") == m.GetAttribute("name")) : null;
                        if (own == null) target.AppendChild(target.OwnerDocument.ImportNode(m, true));
                        else foreach (XmlAttribute a in m.Attributes) own.SetAttribute(a.Name, a.Value);
                    }
                    count("prefabOverridesMerged");
                }
                else count("prefabOverridesDropped");
                overrides.RemoveChild(o);
            }
            if (!overrides.HasChildNodes) instance.RemoveChild(overrides);
        }
    }

    // What the selector chose for one settlement (MMR-258): a look of the catalog at a scale, or phase 0 (culture pieces) with the reason.
    // Prototype is the settlement record's prototype (the look's own id, so the map look matches the scene the player enters; null for a variant
    // whose look is another kind's or pool's, where SettlementFactory picks); Radius is the clearance the look was fitted to.
    internal sealed class LookChoice
    {
        public string Id, Kind, Culture, Look, Variant, Prototype, VillageType, Reason, Tier;
        public double Scale = 1, Radius;
        public int Repeats;             // neighbours (both ways) already holding the look when it was chosen: no fitting look free of them existed
        public bool Phase0 { get { return Look == null; } }
        public SettlementLook Source;   // set when the look is resolved against a catalog (not serialized)

        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["look"] = Look, ["variant"] = Variant, ["prototype"] = Prototype, ["villageType"] = VillageType, ["reason"] = Reason, ["tier"] = Tier,
                ["culture"] = Culture, ["scale"] = Math.Round(Scale, 6), ["radius"] = Math.Round(Radius, 6), ["lookSha256"] = Source == null ? null : Source.Sha256 };
        }
    }

    // A settlement as the selector sees it: kind, presentation culture, port, size tier (a town's class, a village's type), centre (logical).
    internal sealed class LookSite
    {
        public SettlementSite Site; public string Culture, Tier, VillageType; public double[] Centre, Port;
        public string Kind { get { return Site.Kind == SiteKind.Town ? "town" : Site.Kind == SiteKind.Castle ? "castle" : "village"; } }
        public bool HasPort { get { return Site.Kind == SiteKind.Town ? Site.HasPort : Site.Harbour && Site.HarbourCell != null; } }
    }

    // MMR-258: one selector, deterministic per world, picks every town's, castle's and village's look (settlement-looks.json selector, fit,
    // variants). Settlements go towns first, then castles, then villages, each by id.
    //   pool        the culture's looks of the settlement's prototype pool (town_port or town_inland, castle, village_coastal or village_inland:
    //               the pools SettlementFactory picks prototypes from), conforming to the siege contract and, for towns and castles, to the
    //               mission scene contracts (the look's id becomes the record's prototype); a village falls back to the other pool, as the
    //               factory does
    //   variants    an inland town of a portStripped culture (Nord) without inland looks takes its culture's port looks (their port is dropped
    //               anyway; harbour pieces go); a port town of a portGrafted culture (Battania, Khuzait) may take an inland look, grafted with
    //               a pier at its port; a small town may take one of its culture's castle looks (castleAsSmallTown). A variant's prototype is
    //               the factory's pick, so the map look and the entered scene differ there (counted in the report)
    //   score       repeatPenalty x the settlement's neighbourhood (its `neighbours` nearest of its kind and those whose nearest it is) already
    //               holding the look, + usageBalance x the look's uses so far, + 0.25 for a variant, + 0.25 for a town look off its class's size
    //               (small below the culture's median core radius, capital above), + a tie-break from the world's stream. The first that fits
    //               wins, in the order: fewest neighbourhood repeats (strict while repeatPenalty > 0: neighbours differ wherever a fitting look is
    //               free), then a look whose prototype the record takes (own pool, port-stripped, a village's other pool) before a grafted or
    //               castle variant (whose entered scene differs), then the score
    //   fit         the clearance: from each other settlement, its fitted radius (already chosen) or a share of the distance by kind weight
    //               (town 3, castle 2, village 1), less clearanceMargin. A look's core radius at the kind's scale (towns and castles fit.scale,
    //               villages 1: owner decision 5 scales town and castle layouts) must fit; else it shrinks down to minScale/scale of it; a look
    //               that still does not fit is skipped. Phase 0 (culture pieces) when no look fits or the pool is empty.
    internal static class SettlementLookSelector
    {
        public const double Phase0Radius = 1.0;
        private static readonly Dictionary<string, double> Weights = new Dictionary<string, double> { ["town"] = 3, ["castle"] = 2, ["village"] = 1 };

        public static double KindScale(string kind, SettlementLooksPolicy policy) { return kind == "village" ? 1.0 : policy.Scale; }

        // conforms(prototype, kind, port): the mission scene contracts (MissionScenes.PrototypeProblems is empty); null skips the check.
        public static Dictionary<string, LookChoice> Select(IList<LookSite> sites, SettlementLookCatalog catalog, SettlementLooksPolicy policy, Func<string, string, bool, bool> conforms,
                                                           IPlacementGround ground, string worldPrefix)
        {
            var choices = new Dictionary<string, LookChoice>(StringComparer.Ordinal);
            var usage = new Dictionary<string, int>(StringComparer.Ordinal);
            var looks = catalog.Looks.Values.Where(l => l.Conforms).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
            var order = sites.OrderBy(s => s.Kind == "town" ? 0 : s.Kind == "castle" ? 1 : 2).ThenBy(s => s.Site.Id, StringComparer.Ordinal).ToList();
            var nearestOf = Nearest(sites, policy.Neighbours, ground);
            var medianCore = looks.Where(l => l.Kind == "town").GroupBy(l => l.Culture).ToDictionary(g => g.Key, g => Median(g.Select(l => l.CoreRadius)), StringComparer.Ordinal);
            foreach (var site in order)
            {
                var choice = new LookChoice { Id = site.Site.Id, Kind = site.Kind, Culture = site.Culture, VillageType = site.VillageType, Tier = site.Tier };
                double avail = Available(site, sites, choices, ground) - policy.ClearanceMargin;
                choice.Radius = avail;
                var candidates = Candidates(site, looks, policy, conforms);
                if (candidates.Count == 0) { choice.Reason = "no " + site.Culture + " " + site.Kind + " look in the pool"; choices[site.Site.Id] = choice; continue; }
                // The neighbourhood both ways: the site's nearest of its kind, and those whose nearest it is (the pair is judged by the later one).
                var neighbourhood = nearestOf[site].Concat(sites.Where(o => o != site && nearestOf[o].Contains(site))).Distinct().ToList();
                double kindScale = KindScale(site.Kind, policy), minScale = kindScale * policy.MinScale / policy.Scale;
                var scored = candidates.Select(c => {
                    int repeats = neighbourhood.Count(n => { LookChoice o; return choices.TryGetValue(n.Site.Id, out o) && o.Look == c.Key.Id; });
                    double score = policy.RepeatPenalty * repeats + policy.UsageBalance * (usage.ContainsKey(c.Key.Id) ? usage[c.Key.Id] : 0) + (c.Value != null ? 0.25 : 0);
                    double median;
                    if (site.Kind == "town" && c.Key.Kind == "town" && medianCore.TryGetValue(site.Culture, out median))
                        score += (site.Tier == "small" && c.Key.CoreRadius > median) || (site.Tier == "capital" && c.Key.CoreRadius < median) ? 0.25 : 0;
                    score += TieBreak(policy.Stream, worldPrefix, site.Site.Id, c.Key.Id) * 0.01;
                    // A look a neighbour holds never beats one none does while repeats are penalized (where the pool allows, neighbours differ).
                    return new { Look = c.Key, Variant = c.Value, Score = score, Repeats = policy.RepeatPenalty > 0 ? repeats : 0 }; })
                    .OrderBy(x => x.Repeats).ThenBy(x => x.Variant == null || x.Variant == "portStripped" || x.Variant == "otherPool" ? 0 : 1).ThenBy(x => x.Score).ThenBy(x => x.Look.Id, StringComparer.Ordinal).ToList();
                string why = null;
                foreach (var c in scored)
                {
                    double scale = kindScale, core = Math.Max(c.Look.CoreRadius, 1e-6);
                    if (core * scale > avail) scale = avail / core;
                    if (scale < minScale - 1e-12) { why = why ?? c.Look.Id + " needs " + Round(core * minScale) + " at " + Round(minScale) + ", the clearance is " + Round(avail); continue; }
                    choice.Look = c.Look.Id; choice.Variant = c.Variant; choice.Scale = scale; choice.Source = c.Look; choice.Repeats = c.Repeats;
                    choice.Prototype = c.Variant == null || c.Variant == "portStripped" || c.Variant == "otherPool" ? c.Look.Id : null;
                    break;
                }
                if (choice.Look == null) choice.Reason = "no look fits the clearance (" + why + ")";
                else { int n; usage.TryGetValue(choice.Look, out n); usage[choice.Look] = n + 1; }
                choices[site.Site.Id] = choice;
            }
            return choices;
        }

        // Every site's `k` nearest settlements of its kind (by distance, then id).
        public static Dictionary<LookSite, List<LookSite>> Nearest(IList<LookSite> sites, int k, IPlacementGround ground)
        {
            return sites.ToDictionary(s => s, s => sites.Where(o => o != s && o.Kind == s.Kind).OrderBy(o => SettlementPositionResolver.Distance(ground, s.Centre, o.Centre)).ThenBy(o => o.Site.Id, StringComparer.Ordinal).Take(k).ToList());
        }

        // The looks a site may take, with their variant (null: the site's own pool).
        private static List<KeyValuePair<SettlementLook, string>> Candidates(LookSite site, List<SettlementLook> looks, SettlementLooksPolicy policy, Func<string, string, bool, bool> conforms)
        {
            Func<SettlementLook, bool> scenes = l => conforms == null || conforms(l.Id, site.Kind, site.HasPort);
            var mine = looks.Where(l => l.Culture == site.Culture && l.Kind == site.Kind).ToList();
            var list = new List<KeyValuePair<SettlementLook, string>>();
            if (site.Kind == "town")
            {
                list.AddRange(mine.Where(l => l.Port == site.HasPort && scenes(l)).Select(l => new KeyValuePair<SettlementLook, string>(l, null)));
                if (!site.HasPort && list.Count == 0 && policy.PortStripped.Contains(site.Culture))
                    list.AddRange(mine.Where(l => l.Port && scenes(l)).Select(l => new KeyValuePair<SettlementLook, string>(l, "portStripped")));
                if (site.HasPort && policy.PortGrafted.Contains(site.Culture))
                    list.AddRange(mine.Where(l => !l.Port).Select(l => new KeyValuePair<SettlementLook, string>(l, "portGrafted")));
                if (policy.CastleAsSmallTown && site.Tier == "small")
                    list.AddRange(looks.Where(l => l.Culture == site.Culture && l.Kind == "castle").Select(l => new KeyValuePair<SettlementLook, string>(l, "castleAsSmallTown")));
            }
            else if (site.Kind == "castle") list.AddRange(mine.Where(scenes).Select(l => new KeyValuePair<SettlementLook, string>(l, null)));
            else
            {
                list.AddRange(mine.Where(l => l.Port == site.HasPort && scenes(l)).Select(l => new KeyValuePair<SettlementLook, string>(l, null)));
                if (list.Count == 0) list.AddRange(mine.Where(l => l.Port != site.HasPort && scenes(l)).Select(l => new KeyValuePair<SettlementLook, string>(l, "otherPool")));
            }
            return list;
        }

        // The clearance radius a site's look may take before the margin.
        private static double Available(LookSite site, IList<LookSite> sites, Dictionary<string, LookChoice> choices, IPlacementGround ground)
        {
            double best = Double.MaxValue;
            foreach (var o in sites)
            {
                if (o == site) continue;
                double d = SettlementPositionResolver.Distance(ground, site.Centre, o.Centre);
                if (d > 60) continue;
                LookChoice c;
                double room = choices.TryGetValue(o.Site.Id, out c) ? d - Claimed(c) : d * Weights[site.Kind] / (Weights[site.Kind] + Weights[o.Kind]);
                best = Math.Min(best, room);
            }
            return best == Double.MaxValue ? 60 : Math.Max(0, best);
        }

        // The radius a chosen look takes: its footprint at its scale, clipped to its clearance (culled beyond), or the phase-0 pieces'.
        public static double Claimed(LookChoice c)
        {
            if (c.Phase0 || c.Source == null) return Phase0Radius;
            return Math.Max(Math.Min(c.Source.Radius * c.Scale, Math.Max(c.Radius, 0)), c.Source.CoreRadius * c.Scale);
        }

        private static double TieBreak(int stream, string prefix, string site, string look)
        {
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(new UTF8Encoding(false).GetBytes("mnbl.settlement-looks/1\n" + stream.ToString(CultureInfo.InvariantCulture) + "\n" + prefix + "\n" + site + "\n" + look));
                ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
                return (v >> 11) / (double)(1UL << 53);
            }
        }

        private static double Median(IEnumerable<double> values) { var l = values.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2; }
        private static string Round(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // The look-usage and reuse report (RT-15): per kind the looks used and how often, phase 0 and its reasons, variants, scales, and the
        // nearest-neighbour repeats: pairs of a settlement and one of its `neighbours` nearest of its kind with the same look, and how many of
        // those were avoidable (the settlement's pool held more looks than its neighbourhood).
        public static Dictionary<string, object> Report(IList<LookSite> sites, Dictionary<string, LookChoice> choices, SettlementLookCatalog catalog, SettlementLooksPolicy policy, IPlacementGround ground)
        {
            var report = new Dictionary<string, object> { ["catalogLooks"] = catalog.Looks.Count, ["catalogConforming"] = catalog.Looks.Values.Count(l => l.Conforms),
                ["catalogByKind"] = catalog.Looks.Values.GroupBy(l => l.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["nonConforming"] = catalog.Looks.Values.Where(l => !l.Conforms).OrderBy(l => l.Id, StringComparer.Ordinal).ToDictionary(l => l.Id, l => (object)l.Problems.First()),
                ["normalized"] = catalog.Normalized, ["missingPrototypes"] = catalog.Missing };
            foreach (var kind in new[] { "town", "castle", "village" })
            {
                var mine = sites.Where(s => s.Kind == kind).ToList();
                var chosen = mine.Select(s => choices[s.Site.Id]).ToList();
                // Avoidable: a repeated pair whose later-chosen settlement held no neighbour repeat when it was chosen (it could have taken a
                // look none of them held: a selector fault). A repeat chosen with Repeats > 0 had no fitting free look in its pool.
                int pairs = 0, repeats = 0, avoidable = 0;
                var order = sites.OrderBy(s => s.Kind == "town" ? 0 : s.Kind == "castle" ? 1 : 2).ThenBy(s => s.Site.Id, StringComparer.Ordinal).Select((s, i) => new { s, i }).ToDictionary(x => x.s, x => x.i);
                var nearestOf = Nearest(sites, policy.Neighbours, ground);
                foreach (var s in mine)
                {
                    var c = choices[s.Site.Id];
                    foreach (var n in nearestOf[s])
                    {
                        pairs++;
                        if (c.Phase0 || choices[n.Site.Id].Look != c.Look) continue;
                        repeats++;
                        var later = order[s] > order[n] ? c : choices[n.Site.Id];
                        if (later.Repeats == 0) avoidable++;
                    }
                }
                report[kind] = new Dictionary<string, object> {
                    ["settlements"] = mine.Count, ["looks"] = chosen.Count(c => !c.Phase0), ["phase0"] = chosen.Count(c => c.Phase0),
                    ["phase0Reasons"] = chosen.Where(c => c.Phase0).GroupBy(c => c.Reason.StartsWith("no look fits", StringComparison.Ordinal) ? "no look fits the clearance" : c.Reason).ToDictionary(g => g.Key, g => (object)g.Count()),
                    ["distinctLooks"] = chosen.Where(c => !c.Phase0).Select(c => c.Look).Distinct().Count(),
                    ["usage"] = chosen.Where(c => !c.Phase0).GroupBy(c => c.Look).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count()),
                    ["maxUses"] = chosen.Where(c => !c.Phase0).GroupBy(c => c.Look).Select(g => g.Count()).DefaultIfEmpty(0).Max(),
                    ["variants"] = chosen.Where(c => c.Variant != null).GroupBy(c => c.Variant).ToDictionary(g => g.Key, g => (object)g.Count()),
                    ["prototypeIsLook"] = chosen.Count(c => !c.Phase0 && c.Prototype == c.Look),
                    ["scale"] = new Dictionary<string, object> { ["min"] = chosen.Where(c => !c.Phase0).Select(c => Math.Round(c.Scale, 3)).DefaultIfEmpty(0).Min(),
                        ["median"] = Math.Round(Median(chosen.Where(c => !c.Phase0).Select(c => c.Scale)), 3), ["shrunk"] = chosen.Count(c => !c.Phase0 && c.Scale < KindScale(kind, policy) - 1e-9) },
                    ["neighbourPairs"] = pairs, ["neighbourRepeats"] = repeats, ["avoidableRepeats"] = avoidable,
                    ["byCulture"] = chosen.GroupBy(c => c.Culture).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object> {
                        ["settlements"] = g.Count(), ["distinctLooks"] = g.Where(c => !c.Phase0).Select(c => c.Look).Distinct().Count(),
                        ["pool"] = catalog.Looks.Values.Count(l => l.Conforms && l.Kind == kind && l.Culture == g.Key) }) };
            }
            var villages = sites.Where(s => s.Kind == "village").Select(s => choices[s.Site.Id]).ToList();
            report["villageProduction"] = villages.GroupBy(c => c.VillageType ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            return report;
        }
    }

    // The looks a world was built with, frozen into its package (MMR-258): looks/selection.json (every settlement's choice and each used look's
    // hash) and looks/catalog/<look id>.xml (the normalized look). A load that re-encodes the world's scene reads these instead of the installed
    // campaign map, so a game patch that changes Main_map leaves the world's scene as it was built.
    internal sealed class FrozenLooks
    {
        public const string SelectionPath = "looks/selection.json", CatalogFolder = "looks/catalog/";
        public SettlementLookCatalog Catalog;
        public Dictionary<string, LookChoice> Choices;
        public string SourceSha256, PolicySha256;

        public Dictionary<string, byte[]> Files()
        {
            var used = Choices.Values.Where(c => !c.Phase0).Select(c => c.Look).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var files = used.ToDictionary(id => CatalogFolder + id + ".xml", id => new UTF8Encoding(false).GetBytes(Catalog.Looks[id].Xml), StringComparer.Ordinal);
            var selection = new Dictionary<string, object> { ["schema"] = "mnbl.settlement-looks-selection", ["version"] = 1, ["sourceSha256"] = SourceSha256, ["policySha256"] = PolicySha256,
                ["looks"] = used.ToDictionary(id => id, id => (object)Catalog.Looks[id].Sha256),
                ["settlements"] = Choices.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)new Dictionary<string, object> {
                    ["kind"] = c.Value.Kind, ["culture"] = c.Value.Culture, ["tier"] = c.Value.Tier, ["look"] = c.Value.Look, ["variant"] = c.Value.Variant, ["prototype"] = c.Value.Prototype,
                    ["villageType"] = c.Value.VillageType, ["reason"] = c.Value.Reason, ["repeats"] = c.Value.Repeats, ["scale"] = c.Value.Scale.ToString("R", CultureInfo.InvariantCulture),
                    ["radius"] = c.Value.Radius.ToString("R", CultureInfo.InvariantCulture) }) };
            files[SelectionPath] = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(selection));
            return files;
        }

        // A package's frozen looks (the files Files wrote), each look verified against its recorded hash.
        public static FrozenLooks Read(Func<string, byte[]> file, SettlementLooksPolicy policy)
        {
            var doc = StrictJson.Parse(file(SelectionPath));
            StrictJson.Header(doc, "mnbl.settlement-looks-selection", 1, SelectionPath);
            var frozen = new FrozenLooks { Catalog = new SettlementLookCatalog(), Choices = new Dictionary<string, LookChoice>(StringComparer.Ordinal),
                                           SourceSha256 = StrictJson.Str(doc, "sourceSha256", SelectionPath), PolicySha256 = StrictJson.Str(doc, "policySha256", SelectionPath) };
            frozen.Catalog.SourceSha256 = frozen.SourceSha256;
            foreach (var kv in StrictJson.Obj(doc, "looks", SelectionPath))
            {
                var xml = new UTF8Encoding(false).GetString(file(CatalogFolder + kv.Key + ".xml"));
                var look = SettlementLook.FromXml(xml, policy);
                if (look.Sha256 != (string)kv.Value || look.Id != kv.Key) throw new InvalidDataException("The frozen look " + kv.Key + " differs from its recorded hash.");
                frozen.Catalog.Looks[look.Id] = look;
            }
            Func<IDictionary<string, object>, string, string> text = (o, k) => { object v; return o.TryGetValue(k, out v) ? v as string : null; };
            foreach (var kv in StrictJson.Obj(doc, "settlements", SelectionPath))
            {
                var o = (IDictionary<string, object>)kv.Value;
                var c = new LookChoice { Id = kv.Key, Kind = text(o, "kind"), Culture = text(o, "culture"), Tier = text(o, "tier"), Look = text(o, "look"), Variant = text(o, "variant"), Prototype = text(o, "prototype"),
                                         VillageType = text(o, "villageType"), Reason = text(o, "reason"), Repeats = StrictJson.Int(o, "repeats", SelectionPath), Scale = Double.Parse(text(o, "scale"), CultureInfo.InvariantCulture),
                                         Radius = Double.Parse(text(o, "radius"), CultureInfo.InvariantCulture) };
                if (c.Look != null && !frozen.Catalog.Looks.TryGetValue(c.Look, out c.Source)) throw new InvalidDataException("The frozen selection names look " + c.Look + ", which the package does not keep.");
                frozen.Choices[kv.Key] = c;
            }
            return frozen;
        }
    }

    // MMR-258: one settlement's look as scene entities (SettlementEntityFactory.Settlement): the look's normalized tree, cleaned of what the
    // scene builds itself, fitted and re-snapped, under the <id>_look container (turned by `turn`, at the choice's scale).
    //   clean     an entity carrying a functional tag (SettlementLook.FunctionalTags; banner placeholders too unless the look's banners
    //             conform) goes with its subtree, unless it also holds plain visible pieces: then it stays as an untagged holder of those; the
    //             look's own selection sphere (an untagged collider at the root) goes; a village's icons are taken out (Icons); a visual
    //             component that does not resolve goes (SettlementEntityRules.FilterUnresolvedVisuals: a nameless meta mesh, an editor
    //             helper mesh drawn with its editor material outside an editor-only entity)
    //   harbour   a look with a port on a settlement without one loses its pier and dock pieces (port-stripped)
    //   cull      towns and castles (fit.cull): a visible piece no wall level holds, beyond the clearance at the look's scale
    //   villages  production pieces take the village type's meshes in turn (phase0.production), and are drawn in to the clearance; a look
    //             without production pieces gets three
    //   re-snap   (fit.resnapChildren) every topmost visible piece is moved along its parent's up axis onto the terrain under it
    //   names     every entity <id>_l<n> in document order
    internal sealed class LookBuilder
    {
        private readonly ResolvedSettlement s; private readonly SettlementLook look; private readonly LookChoice choice; private readonly IPlacementGround ground;
        private readonly SettlementLooksPolicy policy; private readonly double rootZ, turn, scale;
        private readonly XmlDocument doc = new XmlDocument { XmlResolver = null };
        private readonly XmlElement tree;
        private readonly List<KeyValuePair<int, KeyValuePair<XmlElement, LookFrame>>> icons = new List<KeyValuePair<int, KeyValuePair<XmlElement, LookFrame>>>();
        public int Culled, Snapped, Swapped, Moved, Filtered;

        public LookBuilder(ResolvedSettlement settlement, SettlementLook look, LookChoice choice, IPlacementGround ground, SettlementLooksPolicy policy, double rootZ, double turn)
        {
            s = settlement; this.look = look; this.choice = choice; this.ground = ground; this.policy = policy; this.rootZ = rootZ; this.turn = turn; scale = choice.Scale;
            tree = (XmlElement)doc.ImportNode(look.Root, true); doc.AppendChild(tree);
            bool village = look.Kind == "village";
            if (village)
                foreach (var kv in SettlementLook.Walk(tree, new LookFrame()).ToList())
                {
                    int level = SettlementLook.IconLevel(kv.Key);
                    if (level < 0) continue;
                    icons.Add(new KeyValuePair<int, KeyValuePair<XmlElement, LookFrame>>(level, kv));
                    kv.Key.ParentNode.RemoveChild(kv.Key);
                }
            Clean(tree, village || !look.OwnBanners);
            Filtered = SettlementEntityRules.FilterUnresolvedVisuals(tree);
            foreach (var e in SettlementLook.Children(tree).ToList())
                if (e.SelectSingleNode("physics[@shape]") != null && !SettlementLook.Visible(e) && !SettlementLook.Children(e).Any()) tree.RemoveChild(e);
            bool portless = village ? !(s.Site.Harbour && s.Dropoff != null) : !s.Site.HasPort;
            if (look.Port && portless)
                foreach (var e in tree.SelectNodes(".//game_entity").Cast<XmlElement>().ToList())
                {
                    var mesh = SettlementLook.MeshOf(e);
                    if (mesh != null && (mesh.Contains("pier") || mesh.Contains("dock")) && Attached(e)) e.ParentNode.RemoveChild(e);
                }
            if (!village && policy.Cull)
                foreach (var kv in SettlementLook.Walk(tree, new LookFrame()).ToList())
                    if (Attached(kv.Key) && SettlementLook.Visible(kv.Key) && !SettlementLook.LevelBound(kv.Key) && kv.Value.Radius * scale > choice.Radius) { kv.Key.ParentNode.RemoveChild(kv.Key); Culled++; }
            if (village) Production();
            if (policy.Resnap) Snap(tree, new LookFrame(), false);
            int n = 0;
            foreach (var e in tree.SelectNodes(".//game_entity").Cast<XmlElement>()) e.SetAttribute("name", s.Site.Id + "_l" + (n++).ToString(CultureInfo.InvariantCulture));
        }

        private bool Attached(XmlNode e) { for (var x = e; x != null; x = x.ParentNode) if (x == tree) return true; return false; }

        private void Clean(XmlElement parent, bool dropBanners)
        {
            foreach (var child in SettlementLook.Children(parent).ToList())
            {
                var tags = SettlementLook.Tags(child);
                bool functional = tags.Any(SettlementLook.FunctionalTags.Contains) || (dropBanners && tags.Contains(SettlementLook.BannerTag));
                if (!functional) { Clean(child, dropBanners); continue; }
                if (!HasPlain(child, dropBanners)) { child.ParentNode.RemoveChild(child); continue; }
                foreach (var t in child.SelectNodes("tags/tag").Cast<XmlElement>().ToList())
                    if (SettlementLook.FunctionalTags.Contains(t.GetAttribute("name")) || (dropBanners && t.GetAttribute("name") == SettlementLook.BannerTag)) t.ParentNode.RemoveChild(t);
                var holder = child.SelectSingleNode("tags") as XmlElement;
                if (holder != null && !holder.HasChildNodes) child.RemoveChild(holder);
                Clean(child, dropBanners);
            }
        }

        // Whether an entity holds a visible piece below it that no functional entity holds.
        private static bool HasPlain(XmlElement e, bool dropBanners)
        {
            foreach (var c in SettlementLook.Children(e))
            {
                var tags = SettlementLook.Tags(c);
                if (tags.Any(SettlementLook.FunctionalTags.Contains) || (dropBanners && tags.Contains(SettlementLook.BannerTag))) continue;
                if (SettlementLook.Visible(c) || HasPlain(c, dropBanners)) return true;
            }
            return false;
        }

        private void Production()
        {
            List<string> pieces = null;
            if (choice.VillageType != null) policy.Production.TryGetValue(choice.VillageType, out pieces);
            if (pieces == null || pieces.Count == 0) return;
            var found = new List<XmlElement>();
            foreach (var e in tree.SelectNodes(".//game_entity").Cast<XmlElement>())
            {
                var component = e.SelectSingleNode("components/meta_mesh_component") as XmlElement;
                if (component == null || !look.IsProduction(e, policy)) continue;
                component.SetAttribute("name", pieces[Swapped++ % pieces.Count]);
                foreach (var m in component.ChildNodes.OfType<XmlElement>().ToList()) component.RemoveChild(m);   // the source mesh's factors
                found.Add(e);
            }
            if (found.Count == 0)
                for (int n = 0; n < 3; n++)
                {
                    double a = 2 * Math.PI * n / 3, r = 1.6 / scale;
                    var e = doc.CreateElement("game_entity"); e.SetAttribute("name", "production"); e.SetAttribute("mobility", "1");
                    var t = doc.CreateElement("transform"); t.SetAttribute("position", N(r * Math.Cos(a)) + ", " + N(r * Math.Sin(a)) + ", 0"); t.SetAttribute("rotation_euler", "0, 0, " + N(a)); e.AppendChild(t);
                    var comps = doc.CreateElement("components"); var mm = doc.CreateElement("meta_mesh_component"); mm.SetAttribute("name", pieces[n % pieces.Count]); comps.AppendChild(mm); e.AppendChild(comps);
                    var levels = doc.CreateElement("levels");
                    foreach (var l in SettlementPlacementPolicy.WallLevels.Skip(n).Concat(new[] { "civilian" })) { var le = doc.CreateElement("level"); le.SetAttribute("name", l); levels.AppendChild(le); }
                    e.AppendChild(levels); tree.AppendChild(e); Swapped++;
                }
            // Drawn in to the clearance (the topmost production pieces, along their direction from the centre).
            double room = Math.Max(choice.Radius, 0.8);
            foreach (var e in SettlementLook.Children(tree).ToList())
            {
                if (!found.Contains(e) && !(e.SelectNodes(".//game_entity").Cast<XmlElement>().Any(found.Contains))) continue;
                var f = new LookFrame().Child(e);
                if (f.Radius * scale <= room) continue;
                var t = e.SelectSingleNode("transform") as XmlElement;
                var p = LookFrame.Vec(t, "position", 0); double k = room / (f.Radius * scale);
                t.SetAttribute("position", N(p[0] * k) + ", " + N(p[1] * k) + ", " + N(p[2]));
            }
        }

        // The look-local point q as a logical point of the world.
        private double[] Logical(double[] q)
        {
            double c = Math.Cos(turn), n = Math.Sin(turn);
            return new[] { s.Centre[0] + scale * (c * q[0] - n * q[1]), s.Centre[1] + scale * (n * q[0] + c * q[1]) };
        }

        private void Snap(XmlElement parent, LookFrame frame, bool above)
        {
            foreach (var child in SettlementLook.Children(parent).ToList())
            {
                var f = frame.Child(child);
                bool snap = !above && SettlementLook.Visible(child) && Math.Abs(frame.U[2]) > 0.2;
                if (snap)
                {
                    // The local move whose image under the parent's axes is straight up by the height difference (Cramer's rule on [S F U]).
                    var at = Logical(f.O);
                    double w = (ground.Elevation(at[0], at[1]) - rootZ - scale * f.O[2]) / scale;
                    double[] S = frame.S, F = frame.F, U = frame.U;
                    double det = S[0] * (F[1] * U[2] - F[2] * U[1]) - F[0] * (S[1] * U[2] - S[2] * U[1]) + U[0] * (S[1] * F[2] - S[2] * F[1]);
                    if (Math.Abs(det) > 1e-12)
                    {
                        double d0 = w * (F[0] * U[1] - F[1] * U[0]) / det, d1 = -w * (S[0] * U[1] - S[1] * U[0]) / det, d2 = w * (S[0] * F[1] - S[1] * F[0]) / det;
                        var t = child.SelectSingleNode("transform") as XmlElement;
                        if (t == null) { t = doc.CreateElement("transform"); child.InsertBefore(t, child.FirstChild); }
                        var p = LookFrame.Vec(t, "position", 0);
                        t.SetAttribute("position", N(p[0] + d0) + ", " + N(p[1] + d1) + ", " + N(p[2] + d2));
                        f = frame.Child(child); Snapped++;
                    }
                }
                Snap(child, f, above || snap);
            }
        }

        // The village's icons, named as the contract requires (<id>_icon, _icon_2, _icon_3, _icon_looted), placed by the look's turn and scale on
        // the terrain, with the contract's levels. MMR-247: a piece of the look's tree with an icon's level set (most looks' level-3 production
        // piece, some looks' level-2 or looted ones) moves under that icon without its levels, at the same place: the engine's inspection read
        // of acceptance run acc1 lost every icon such a piece shared a level set with (SettlementEntityRules' same-level-visuals model), so no
        // entity of a village but its icons has an icon's level set. The icon's levels gate the piece as its own did. Call it before Container.
        public List<XmlElement> Icons()
        {
            var result = new List<XmlElement>();
            foreach (var level in new[] { 1, 2, 3, 0 })
            {
                var kv = icons.First(i => i.Key == level).Value;
                var e = (XmlElement)kv.Key.CloneNode(true);
                var kids = e.SelectSingleNode("children"); if (kids != null) e.RemoveChild(kids);
                string id = s.Site.Id;
                e.SetAttribute("name", level == 1 ? id + "_icon" : level == 0 ? id + "_icon_looted" : id + "_icon_" + level);
                var at = Logical(kv.Value.O);
                double c = Math.Cos(turn), n = Math.Sin(turn), k = kv.Value.Scale * scale;
                var t = e.SelectSingleNode("transform") as XmlElement;
                if (t == null) { t = doc.CreateElement("transform"); e.InsertBefore(t, e.FirstChild); }
                t.SetAttribute("position", N(scale * (c * kv.Value.O[0] - n * kv.Value.O[1])) + ", " + N(scale * (n * kv.Value.O[0] + c * kv.Value.O[1])) + ", " + N(ground.Elevation(at[0], at[1]) - rootZ));
                t.SetAttribute("rotation_euler", "0, 0, " + N(kv.Value.Yaw + turn));
                t.SetAttribute("scale", N(k) + ", " + N(k) + ", " + N(k));
                var levels = e.SelectSingleNode("levels") as XmlElement;
                if (levels != null) e.RemoveChild(levels);
                levels = doc.CreateElement("levels");
                var set = level == 0 ? SettlementPlacementPolicy.WallLevels.Concat(new[] { "looted" }).ToList() : new List<string> { "level_" + level, "civilian" };
                foreach (var l in set) { var le = doc.CreateElement("level"); le.SetAttribute("name", l); levels.AppendChild(le); }
                e.AppendChild(levels);
                // The twins: the tree's direct children with this level set (in any order), re-framed into the icon's frame (both frames
                // are a yaw, a uniform scale and an offset in the root's; a twin keeps its tilt, and its yaw turns by the frames' difference).
                double iconYaw = kv.Value.Yaw + turn;
                double[] iconAt = { scale * (c * kv.Value.O[0] - n * kv.Value.O[1]), scale * (n * kv.Value.O[0] + c * kv.Value.O[1]), ground.Elevation(at[0], at[1]) - rootZ };
                XmlElement holder = null;
                foreach (var twin in SettlementLook.Children(tree).Where(x => new HashSet<string>(SettlementLook.LevelNames(x), StringComparer.Ordinal).SetEquals(set)).ToList())
                {
                    var tt = twin.SelectSingleNode("transform") as XmlElement;
                    if (tt == null) { tt = doc.CreateElement("transform"); twin.InsertBefore(tt, twin.FirstChild); }
                    var p = LookFrame.Vec(tt, "position", 0); var r = LookFrame.Vec(tt, "rotation_euler", 0); var ks = LookFrame.Vec(tt, "scale", 1);
                    double qx = scale * (c * p[0] - n * p[1]) - iconAt[0], qy = scale * (n * p[0] + c * p[1]) - iconAt[1], qz = scale * p[2] - iconAt[2];
                    double ci = Math.Cos(-iconYaw), ni = Math.Sin(-iconYaw);
                    tt.SetAttribute("position", N((ci * qx - ni * qy) / k) + ", " + N((ni * qx + ci * qy) / k) + ", " + N(qz / k));
                    tt.SetAttribute("rotation_euler", N(r[0]) + ", " + N(r[1]) + ", " + N(r[2] + turn - iconYaw));
                    tt.SetAttribute("scale", N(ks[0] * scale / k) + ", " + N(ks[1] * scale / k) + ", " + N(ks[2] * scale / k));
                    var own = twin.SelectSingleNode("levels"); if (own != null) twin.RemoveChild(own);
                    tree.RemoveChild(twin);
                    if (holder == null) { holder = doc.CreateElement("children"); e.InsertBefore(holder, levels); }
                    holder.AppendChild(twin); Moved++;
                }
                result.Add(e);
            }
            return result;
        }

        public EntitySpec Container(string name)
        {
            var spec = new EntitySpec { Name = name, Yaw = turn };
            if (Math.Abs(scale - 1) > 1e-12) spec.Scale = new[] { scale, scale, scale };
            spec.Raw.AddRange(SettlementLook.Children(tree));
            return spec;
        }

        // A look wall section's solid and broken pieces under the scene's wall entity: <wall>_solid, <wall>_broken, their descendants numbered.
        public static IEnumerable<XmlElement> WallPieces(string wall, string childrenXml)
        {
            if (String.IsNullOrEmpty(childrenXml)) yield break;
            var d = new XmlDocument { XmlResolver = null }; d.LoadXml("<pieces>" + childrenXml + "</pieces>");
            foreach (var e in d.DocumentElement.ChildNodes.OfType<XmlElement>())
            {
                string name = wall + (SettlementLook.Tags(e).Contains("map_solid_wall") ? "_solid" : "_broken");
                e.SetAttribute("name", name);
                int n = 0;
                foreach (var x in e.SelectNodes(".//game_entity").Cast<XmlElement>()) x.SetAttribute("name", name + "_" + (n++).ToString(CultureInfo.InvariantCulture));
                yield return e;
            }
        }

        private static string N(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }

    // MMR-258: the one look plan of a world, used by the production scene (RuntimeWorldBackend.Prepare) and the offline harnesses: every site's
    // culture, size tier and village type, the selection (or a package's frozen one), each fortification's frame from its look, then the
    // placement resolver. Returns the layout with its look report and the frozen looks to keep in the world package.
    internal static class SettlementLookPlan
    {
        public sealed class Inputs
        {
            public SettlementLookCatalog Catalog { get; set; }
            public SettlementLooksPolicy Policy { get; set; }
            public FrozenLooks Frozen { get; set; }                                       // a package's frozen looks (no selection runs)
            public Func<string, string, bool, bool> Conforms { get; set; }                 // the mission scene contracts (null: unchecked)
            public IDictionary<string, string> Cultures { get; set; }
            public IDictionary<string, string> TownClasses { get; set; }
            public IDictionary<string, string> VillageTypes { get; set; }
            public string WorldPrefix { get; set; }
        }

        // The plan's inputs from a bundle's campaign records, travel graph and ports: the static settlement cultures and world prefix, the towns'
        // classes and SettlementFactory's village types (PlanVillageTypes; defaults: the registered engine defaults, the activation's).
        public static Inputs ForCampaign(byte[] campaignJson, byte[] travelJson, byte[] portsJson, byte[] economyJson, byte[] prototypesJson, Func<string, bool> defaults, SettlementLooksPolicy policy)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<byte[], IDictionary<string, object>> parse = b => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(b).TrimStart('﻿'));
            var campaign = parse(campaignJson);
            var inputs = new Inputs { Policy = policy, Cultures = CampaignRegistry.HeroFactory.SettlementCulturesOf(campaign), WorldPrefix = CampaignRegistry.FactionFactory.WorldPrefixOf(campaign),
                                      TownClasses = new Dictionary<string, string>(StringComparer.Ordinal) };
            foreach (IDictionary<string, object> t in (object[])campaign["towns"]) { object id, cls; t.TryGetValue("id", out id); t.TryGetValue("cityClass", out cls); inputs.TownClasses[(string)id] = cls as string; }
            var empty = new XmlDocument { XmlResolver = null }; empty.LoadXml("<Settlements/>");
            inputs.VillageTypes = new CampaignRegistry.SettlementFactory(parse(economyJson), parse(prototypesJson), empty, defaults).PlanVillageTypes(campaign, parse(travelJson), parse(portsJson));
            return inputs;
        }

        public static SettlementLayout Resolve(IList<SettlementSite> sites, NavigationPlan plan, IPlacementGround ground, SettlementPlacementPolicy placement, Inputs inputs, out FrozenLooks frozen)
        {
            var lookSites = new List<LookSite>();
            foreach (var site in sites)
            {
                var point = plan == null ? null : plan.Point(site.Id);
                var centre = point != null && point.Resolved != null ? point.Resolved : site.Centre;
                if (centre == null || site.Kind == SiteKind.Hideout) continue;
                var port = plan == null ? null : plan.Point("port:" + site.Id);
                string culture, tier = null, type = null;
                if (!inputs.Cultures.TryGetValue(site.Id, out culture) || culture == null) throw new InvalidDataException("Settlement " + site.Id + " has no presentation culture for its look.");
                if (site.Kind == SiteKind.Town) inputs.TownClasses.TryGetValue(site.Id, out tier);
                if (site.Kind == SiteKind.Village && inputs.VillageTypes != null) inputs.VillageTypes.TryGetValue(site.Id, out type);
                lookSites.Add(new LookSite { Site = site, Culture = culture, Tier = site.Kind == SiteKind.Village ? type : site.Kind == SiteKind.Castle ? "castle" : tier, VillageType = type, Centre = centre,
                                             Port = port == null ? site.Port : port.Resolved });
            }
            Dictionary<string, LookChoice> choices;
            SettlementLookCatalog catalog;
            if (inputs.Frozen != null)
            {
                catalog = inputs.Frozen.Catalog; choices = inputs.Frozen.Choices;
                var missing = lookSites.Where(x => !choices.ContainsKey(x.Site.Id)).Select(x => x.Site.Id).ToList();
                if (missing.Count > 0 || choices.Count != lookSites.Count) throw new InvalidDataException("The frozen look selection is not this world's (" + missing.Count + " settlements without a look).");
            }
            else
            {
                catalog = inputs.Catalog;
                choices = SettlementLookSelector.Select(lookSites, catalog, inputs.Policy, inputs.Conforms, ground, inputs.WorldPrefix);
            }
            foreach (var x in lookSites)
            {
                var c = choices[x.Site.Id];
                x.Site.Look = c; x.Site.Frame = null;
                if (!x.Site.Fortification || c.Source == null) continue;
                var frame = SettlementFrame.FromLook(c.Source, c.Scale);
                // A port town's harbour look: the gate turned so the look's harbour faces the world's port.
                if (x.Site.HasPort && c.Variant == null && c.Source.PortAngle.HasValue && x.Port != null)
                {
                    double toPort = Math.Atan2(x.Port[1] - x.Centre[1], SettlementPositionResolver.Delta(ground, x.Centre[0], x.Port[0]));
                    frame.GateAngle = toPort - (c.Source.PortAngle.Value - c.Source.GateAngle);
                    frame.GateRule = "look " + c.Look + " harbour toward the port";
                }
                x.Site.Frame = frame;
            }
            var layout = SettlementPositionResolver.Resolve(sites, plan, ground, placement);
            layout.Looks = SettlementLookSelector.Report(lookSites, choices, catalog, inputs.Policy, ground);
            frozen = new FrozenLooks { Catalog = catalog, Choices = choices, SourceSha256 = catalog.SourceSha256, PolicySha256 = inputs.Policy.Sha256 };
            return layout;
        }
    }
}
