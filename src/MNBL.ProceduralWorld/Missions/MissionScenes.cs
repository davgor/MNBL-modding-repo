using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Missions
{
    // MMR-42: the versioned catalogue of installed mission scenes (worldgen/policies/mission-scenes.json, written by tools/make_mission_scenes.py
    // from the installed game and embedded in the module) and the mission contracts a scene must meet. Pure managed: the offline tests, the
    // settlement factory, the graph validator and the in-game resolver share it (docs/mission-scenes.md).
    //   * Battle scenes: the installed sp_battle_scenes table, with its hash (GeneratedBattleTerrain.SceneTableSha) and an inventory hash that
    //     also covers forest density and TerrainTypes; a game whose loaded table differs refuses the catalogue.
    //   * Scenes: every battle, conversation and settlement location scene, inventoried from its scene.xscene (module, SHA-256, scene levels,
    //     contract tags, scripts and spawn paths, each with the scene levels it is visible in; hierarchy and capacity). A scene is usable when
    //     it is installed in an allowed module and meets its contract.
    //   * Prototypes: every settlement prototype's locations with their scene per wall level (0..3), as the catalog the prototypes come from.
    //   * Contracts (from the installed code that reads them): a fortification's center opens at wall level N with level_N (a visit) and
    //     level_N siege (a siege), its lord's hall with siege; a port town has a port scene; a harbour village's village_center has the
    //     naval_raid level with landing and jumping points visible in it (War Sails' seaborne raid); a hideout_center has level_1/level_2,
    //     spawnpoint_player and CommonAreaMarker areas. Location.GetSceneName(N) falls back to the level 0 scene when level N has none, so the
    //     wall-level checks read that effective scene.
    internal sealed class MissionScenes
    {
        public const string PolicyPath = "worldgen/policies/mission-scenes.json";
        private const string Resource = "MNBL.ProceduralWorld.mission-scenes.json";

        public sealed class BattleScene { public string Id, Module, Terrain, ForestDensity; public bool Naval; public string[] TerrainTypes; public int[] MapIndices; public int SpawnPaths; }
        public sealed class ConversationScene { public string Id, Module, Terrain, ForestDensity; }
        public sealed class Scene
        {
            public string Id, Module, Sha256; public int SpawnPaths, Layers, WinterLayers, Entities;
            public Dictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.Ordinal), Tags = new Dictionary<string, int>(StringComparer.Ordinal),
                                           TagPrefixes = new Dictionary<string, int>(StringComparer.Ordinal), Scripts = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, string[]> ByLevel = new Dictionary<string, string[]>(StringComparer.Ordinal);   // contract item -> levels ("*": every level)
            public bool VisibleIn(string item, string level) { string[] l; return ByLevel.TryGetValue(item, out l) && (l.Contains("*") || l.Contains(level)); }
        }
        public sealed class Prototype { public string Id, Kind, Culture; public Dictionary<string, string[]> Locations = new Dictionary<string, string[]>(StringComparer.Ordinal); }
        public sealed class SceneClass { public string Name, Terrain; public string[] ForestDensity, TerrainTypes, Fallback; }
        public sealed class Reference { public string Location, Scene; public int Level; }

        public string Sha256 { get; private set; }
        public string TableSha256 { get; private set; }
        public string InventorySha256 { get; private set; }
        public int BlockSize { get; private set; }
        public List<BattleScene> Battle = new List<BattleScene>();
        public List<ConversationScene> Conversations = new List<ConversationScene>();
        public Dictionary<string, Scene> Scenes = new Dictionary<string, Scene>(StringComparer.Ordinal);
        public Dictionary<string, Prototype> Prototypes = new Dictionary<string, Prototype>(StringComparer.Ordinal);
        public HashSet<string> AllowedModules = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, int> TerrainIds = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<int, string> TerrainNames = new Dictionary<int, string>();
        public Dictionary<string, SceneClass> Classes = new Dictionary<string, SceneClass>(StringComparer.Ordinal);
        public Dictionary<string, string> BiomeClass = new Dictionary<string, string>(StringComparer.Ordinal), FaceClass = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string[]> Relief = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public List<string> ConversationTerrains = new List<string>(), LandTerrains = new List<string>(), NavalTerrains = new List<string>();
        public IDictionary<string, object> Contracts;

        public static MissionScenes Installed()
        {
            using (var stream = typeof(MissionScenes).Assembly.GetManifestResourceStream(Resource))
            {
                if (stream == null) throw new InvalidOperationException("The module has no embedded mission scene catalogue (" + Resource + ").");
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); return FromBytes(copy.ToArray()); }
            }
        }

        public static MissionScenes FromBytes(byte[] bytes)
        {
            var doc = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'));
            if (S(doc, "schema") != "mnbl.mission-scenes" || I(doc, "version") != 1) throw new InvalidDataException("Not an mnbl.mission-scenes version 1 catalogue.");
            var table = O(doc, "battleTable");
            var m = new MissionScenes { Sha256 = Sha(bytes), TableSha256 = S(table, "sha256"), InventorySha256 = S(table, "inventorySha256"), BlockSize = I(doc, "blockSize"), Contracts = O(doc, "contracts") };
            foreach (var a in A(doc, "allowedModules")) m.AllowedModules.Add((string)a);
            foreach (var t in O(doc, "terrainTypes")) { int id = Convert.ToInt32(t.Value, CultureInfo.InvariantCulture); m.TerrainIds[t.Key] = id; m.TerrainNames[id] = t.Key; }
            foreach (var b in A(doc, "battleScenes").Cast<IDictionary<string, object>>())
                m.Battle.Add(new BattleScene { Id = S(b, "id"), Module = S(b, "module"), Naval = Bl(b, "naval"), Terrain = S(b, "terrain"), ForestDensity = S(b, "forestDensity"),
                    TerrainTypes = A(b, "terrainTypes").Cast<string>().ToArray(), MapIndices = A(b, "mapIndices").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray(),
                    SpawnPaths = b.ContainsKey("spawnPaths") && b["spawnPaths"] != null ? Convert.ToInt32(b["spawnPaths"], CultureInfo.InvariantCulture) : 0 });
            foreach (var c in A(doc, "conversationScenes").Cast<IDictionary<string, object>>())
                m.Conversations.Add(new ConversationScene { Id = S(c, "id"), Module = S(c, "module"), Terrain = S(c, "terrain"), ForestDensity = S(c, "forestDensity") });
            foreach (var e in O(doc, "scenes"))
            {
                var o = (IDictionary<string, object>)e.Value;
                var s = new Scene { Id = e.Key, Module = S(o, "module"), Sha256 = S(o, "sha256"), SpawnPaths = I(o, "spawnPaths"), Layers = I(o, "layers"), WinterLayers = I(o, "winterLayers"), Entities = I(o, "entities") };
                foreach (var name in new[] { "levels", "tags", "tagPrefixes", "scripts" })
                {
                    var into = name == "levels" ? s.Levels : name == "tags" ? s.Tags : name == "tagPrefixes" ? s.TagPrefixes : s.Scripts;
                    foreach (var kv in O(o, name)) into[kv.Key] = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture);
                }
                if (o.ContainsKey("byLevel")) foreach (var kv in O(o, "byLevel")) s.ByLevel[kv.Key] = ((object[])kv.Value).Cast<string>().ToArray();
                m.Scenes[e.Key] = s;
            }
            foreach (var e in O(doc, "prototypes"))
            {
                var o = (IDictionary<string, object>)e.Value;
                var p = new Prototype { Id = e.Key, Kind = S(o, "kind"), Culture = S(o, "culture") };
                foreach (var l in O(o, "locations")) p.Locations[l.Key] = ((object[])l.Value).Select(x => x as string).ToArray();
                m.Prototypes[e.Key] = p;
            }
            foreach (var e in O(doc, "classes"))
            {
                var o = (IDictionary<string, object>)e.Value; var prefer = o.ContainsKey("prefer") ? o["prefer"] as IDictionary<string, object> : null;
                m.Classes[e.Key] = new SceneClass { Name = e.Key, Terrain = S(o, "terrain"),
                    ForestDensity = prefer != null && prefer.ContainsKey("forestDensity") ? A(prefer, "forestDensity").Cast<string>().ToArray() : null,
                    TerrainTypes = prefer != null && prefer.ContainsKey("terrainTypes") ? A(prefer, "terrainTypes").Cast<string>().ToArray() : null,
                    Fallback = A(o, "fallback").Cast<string>().ToArray() };
            }
            foreach (var e in O(doc, "biomeClass")) m.BiomeClass[e.Key] = (string)e.Value;
            foreach (var e in O(doc, "faceClass")) m.FaceClass[e.Key] = (string)e.Value;
            foreach (var e in O(doc, "relief")) m.Relief[e.Key] = ((object[])e.Value).Cast<string>().ToArray();
            m.ConversationTerrains.AddRange(A(doc, "conversationTerrains").Cast<string>());
            m.LandTerrains.AddRange(A(doc, "landTerrains").Cast<string>());
            m.NavalTerrains.AddRange(A(doc, "navalTerrains").Cast<string>());
            foreach (var c in m.Classes.Values)
                foreach (var t in new[] { c.Terrain }.Concat(c.Fallback))
                    if (!m.TerrainIds.ContainsKey(t ?? "")) throw new InvalidDataException("Scene class " + c.Name + " names no installed terrain " + t + ".");
            foreach (var v in m.BiomeClass.Values.Concat(m.FaceClass.Values)) if (!m.Classes.ContainsKey(v)) throw new InvalidDataException("Unknown scene class " + v + ".");
            foreach (var t in m.ConversationTerrains.Concat(m.LandTerrains).Concat(m.NavalTerrains)) if (!m.TerrainIds.ContainsKey(t)) throw new InvalidDataException("Unknown terrain " + t + ".");
            if (m.BlockSize != 16) throw new InvalidDataException("The battle map block is 16 cells (the reader packs 4-bit patch coordinates).");
            if (String.IsNullOrEmpty(m.TableSha256) || String.IsNullOrEmpty(m.InventorySha256)) throw new InvalidDataException("The catalogue has no battle table hashes.");
            return m;
        }

        // The scene's problems for a contract (empty when usable): installed, in an allowed module, and the contract's levels, tags, tag
        // prefixes (visible in the contract's level when it names one), scripts and spawn paths.
        // levels: the scene levels to require instead of the contract's (a fortification's center is checked per wall level).
        public List<string> SceneProblems(string scene, string contract, IEnumerable<string> levels = null)
        {
            var problems = new List<string>();
            Scene s;
            if (String.IsNullOrEmpty(scene)) { problems.Add(contract + ": no scene"); return problems; }
            if (!Scenes.TryGetValue(scene, out s)) { problems.Add(contract + ": " + scene + " is not an installed scene"); return problems; }
            if (!AllowedModules.Contains(s.Module)) problems.Add(contract + ": " + scene + " is in module " + s.Module + ", which is not allowed");
            var c = O(Contracts, contract);
            string inLevel = S(c, "inLevel");
            foreach (var level in levels ?? A(c, "levels").Cast<string>()) if (!s.Levels.ContainsKey(level)) problems.Add(contract + ": " + scene + " has no scene level " + level);
            foreach (var tag in A(c, "tags").Cast<string>())
                if (!s.Tags.ContainsKey(tag)) problems.Add(contract + ": " + scene + " has no " + tag + " entity");
                else if (inLevel != null && !s.VisibleIn(tag, inLevel)) problems.Add(contract + ": " + scene + "'s " + tag + " entity is not visible in level " + inLevel);
            foreach (var prefix in A(c, "tagPrefixes").Cast<string>())
                if (!s.TagPrefixes.ContainsKey(prefix)) problems.Add(contract + ": " + scene + " has no " + prefix + "* entity");
                else if (inLevel != null && !s.VisibleIn(prefix, inLevel)) problems.Add(contract + ": " + scene + "'s " + prefix + "* entities are not visible in level " + inLevel);
            foreach (var script in A(c, "scripts").Cast<string>()) if (!s.Scripts.ContainsKey(script)) problems.Add(contract + ": " + scene + " has no " + script + " script");
            if (c.ContainsKey("minimumPaths") && s.SpawnPaths < Convert.ToInt32(c["minimumPaths"], CultureInfo.InvariantCulture)) problems.Add(contract + ": " + scene + " has " + s.SpawnPaths + " spawn paths");
            return problems;
        }

        // Every scene a location complex names, per location and wall level (0 = scene_name, 1..3 = scene_name_1..3).
        public static List<Reference> References(IDictionary<string, string[]> locations)
        {
            var list = new List<Reference>();
            foreach (var l in locations.OrderBy(x => x.Key, StringComparer.Ordinal))
                for (int level = 0; level < l.Value.Length; level++) if (!String.IsNullOrEmpty(l.Value[level])) list.Add(new Reference { Location = l.Key, Level = level, Scene = l.Value[level] });
            return list;
        }

        // The scene Location.GetSceneName(level) opens: the level's own scene, else the level 0 scene.
        public static string Effective(string[] scenes, int level)
        {
            if (scenes == null) return null;
            string own = level < scenes.Length ? scenes[level] : null;
            return !String.IsNullOrEmpty(own) ? own : scenes.Length > 0 && !String.IsNullOrEmpty(scenes[0]) ? scenes[0] : null;
        }

        // The problems of a settlement's locations for its kind (town, castle, village, hideout): every named scene usable, and the kind's
        // contracts. port: a town with a port location (a port town), or a village with a port (a harbour village, raided from the sea).
        public List<string> LocationProblems(string kind, bool port, IDictionary<string, string[]> locations)
        {
            var problems = new List<string>();
            Func<string, string[]> at = id => { string[] v; return locations.TryGetValue(id, out v) ? v : new string[4]; };
            foreach (var r in References(locations))
            {
                Scene s;
                if (!Scenes.TryGetValue(r.Scene, out s)) problems.Add(r.Location + " level " + r.Level + ": " + r.Scene + " is not an installed scene");
                else if (!AllowedModules.Contains(s.Module)) problems.Add(r.Location + " level " + r.Level + ": " + r.Scene + " is in module " + s.Module + ", which is not allowed");
            }
            if (kind == "town" || kind == "castle")
            {
                // Wall level n opens the center's effective scene n with level_n (a visit) and level_n siege (a siege), and the lord's hall's
                // effective scene n with siege (the siege lord's hall fight).
                var center = at("center"); var hall = at("lordshall");
                for (int n = 1; n <= 3; n++)
                {
                    string tag = "level_" + n.ToString(CultureInfo.InvariantCulture);
                    problems.AddRange(SceneProblems(Effective(center, n), "settlementCenter", new[] { tag }).Select(p => p + " (wall level " + n + ")"));
                    problems.AddRange(SceneProblems(Effective(center, n), "siege", new[] { tag, "siege" }).Select(p => p + " (wall level " + n + ")"));
                    problems.AddRange(SceneProblems(Effective(hall, n), "siegeLordsHall").Select(p => p + " (wall level " + n + ")"));
                }
                if (kind == "town" && port) { var p = at("port").FirstOrDefault(x => !String.IsNullOrEmpty(x)); problems.AddRange(SceneProblems(p, "port")); }
                if (kind == "town" && !port && at("port").Any(x => !String.IsNullOrEmpty(x))) problems.Add("port: an inland town keeps a port scene");
            }
            else if (kind == "village")
            {
                var center = at("village_center").Where(x => !String.IsNullOrEmpty(x)).Distinct().ToList();
                if (center.Count == 0) problems.Add("villageCenter: no village_center scene");
                foreach (var c in center) { problems.AddRange(SceneProblems(c, "villageCenter")); if (port) problems.AddRange(SceneProblems(c, "navalRaid")); }
            }
            else if (kind == "hideout")
            {
                var center = at("hideout_center").Where(x => !String.IsNullOrEmpty(x)).Distinct().ToList();
                if (center.Count == 0) problems.Add("hideout: no hideout_center scene");
                foreach (var c in center) problems.AddRange(SceneProblems(c, "hideout"));
            }
            else problems.Add("no mission scene contract for settlement kind " + kind);
            return problems.Distinct().ToList();
        }

        // A prototype's problems as a settlement of a kind (the factory drops an inland town's port location).
        public List<string> PrototypeProblems(string prototype, string kind, bool port)
        {
            Prototype p;
            if (prototype == null || !Prototypes.TryGetValue(prototype, out p)) return new List<string> { "prototype " + (prototype ?? "(none)") + " is not in the mission scene catalogue" };
            var locations = new Dictionary<string, string[]>(p.Locations, StringComparer.Ordinal);
            if (kind == "town" && !port) locations.Remove("port");
            return LocationProblems(kind, port, locations);
        }

        // The location references a settlement stores (C04/C06): location, level and scene, with the scenes' installed hashes.
        public SortedDictionary<string, object> ReferenceRecord(IDictionary<string, string[]> locations)
        {
            var refs = References(locations);
            var text = new StringBuilder();
            foreach (var r in refs) { Scene s; Scenes.TryGetValue(r.Scene, out s); text.Append(r.Location).Append('|').Append(r.Level).Append('|').Append(r.Scene).Append('|').Append(s == null ? "-" : s.Module + ":" + s.Sha256).Append('\n'); }
            return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["references"] = refs.Count, ["scenes"] = refs.Select(r => r.Scene).Distinct().Count(), ["referencesSha256"] = Sha(new UTF8Encoding(false).GetBytes(text.ToString())),
                ["catalogue"] = Sha256 };
        }

        public static IDictionary<string, string[]> LocationsOf(System.Xml.XmlElement settlement)
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var locations = settlement == null ? null : settlement.SelectSingleNode("Locations") as System.Xml.XmlElement;
            if (locations == null) return result;
            foreach (var l in locations.ChildNodes.OfType<System.Xml.XmlElement>().Where(e => e.Name == "Location"))
                result[l.GetAttribute("id")] = new[] { Attr(l, "scene_name"), Attr(l, "scene_name_1"), Attr(l, "scene_name_2"), Attr(l, "scene_name_3") };
            return result;
        }
        private static string Attr(System.Xml.XmlElement e, string name) { var v = e.GetAttribute(name); return String.IsNullOrEmpty(v) ? null : v; }

        internal static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static IDictionary<string, object> O(IDictionary<string, object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || !(v is IDictionary<string, object>)) throw new InvalidDataException("Missing object " + key + ".");
            return (IDictionary<string, object>)v;
        }
        private static object[] A(IDictionary<string, object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return new object[0];
            var array = v as object[]; if (array != null) return array;
            var sequence = v as IEnumerable; if (sequence != null && !(v is string)) return sequence.Cast<object>().ToArray();
            throw new InvalidDataException(key + " is not an array.");
        }
        private static string S(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v as string : null; }
        private static int I(IDictionary<string, object> d, string key) { object v; if (d == null || !d.TryGetValue(key, out v) || v == null) throw new InvalidDataException("Missing integer " + key + "."); return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        private static bool Bl(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v; }
    }
}
