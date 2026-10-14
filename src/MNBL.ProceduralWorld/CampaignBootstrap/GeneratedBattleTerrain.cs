using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SandBox;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-60: the generated campaign's battle-scene patch map (SandBox.MapScene._battleTerrainIndexMap*), which
    // SandBox.MapScene.GetMapPatchAtPosition samples for DefaultSceneModel.GetBattleSceneForMapPatch.
    //
    // Two bytes per cell, exactly as GetMapPatchAtPosition reads them: [0] an installed sp_battle_scenes map index and
    // [1] the cell's place inside its block as (x | y << 4), read back as (x/15, y/15) patch coordinates. Cells cover
    // _terrainSize (the same extent the reader divides by). Each cell is sampled at 3x3 points against the generated
    // navmesh and classified by the PartyNavigationModel so the scene model finds exactly one candidate wherever a party
    // can stand in it: a land-only cell uses an index naming exactly one land scene of its terrain; a cell a ship can
    // enter (CoastalSea, OpenSea, Lake, River...) uses an index naming exactly one land AND exactly one naval scene.
    // Without this map the reader returns scene index 0, which no installed scene lists; the scene model then silently
    // picks a random scene of the party's terrain (FailedAssert only when no scene of that terrain exists).
    public static class GeneratedBattleTerrain
    {
        public const float CellSize = 4f;
        public const int BlockCells = 16;
        // Generated land terrain with no installed land battle scene of its own: the scene terrain it borrows.
        private static readonly Dictionary<TerrainType, TerrainType> LandSubstitute = new Dictionary<TerrainType, TerrainType> {
            [TerrainType.Mountain] = TerrainType.Steppe, [TerrainType.Lake] = TerrainType.Plain, [TerrainType.Forest] = TerrainType.Plain,
            [TerrainType.Snow] = TerrainType.Steppe, [TerrainType.RuralArea] = TerrainType.Plain, [TerrainType.Beach] = TerrainType.Plain,
            [TerrainType.Cliff] = TerrainType.Steppe, [TerrainType.Canyon] = TerrainType.Steppe, [TerrainType.Dune] = TerrainType.Desert,
            [TerrainType.Fording] = TerrainType.Plain, [TerrainType.Bridge] = TerrainType.Plain, [TerrainType.CoastalSea] = TerrainType.Plain,
            [TerrainType.OpenSea] = TerrainType.Plain, [TerrainType.Water] = TerrainType.Plain, [TerrainType.River] = TerrainType.Plain };

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Generated battle terrain: " + message);
        }
        private static FieldInfo MapField(string name)
        {
            var f = typeof(MapScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Require(f != null, "installed field contract changed: " + name);
            return f;
        }
        // Stable across processes and platforms (string.GetHashCode is not).
        private static uint Mix(string worldId, int bx, int by)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(worldId + ":" + bx + ":" + by)) { h ^= b; h *= 16777619; }
            return h;
        }

        // Candidate map indices per scene terrain, ascending: land indices named by exactly one land scene, sea indices
        // named by exactly one naval scene.
        internal static void Candidates(out Dictionary<TerrainType, List<int>> land, out Dictionary<TerrainType, List<int>> sea,
            out Dictionary<int, string> landScene, out Dictionary<int, string> navalScene, out List<int> both,
            out Dictionary<int, TerrainType> landTerrainOf, out Dictionary<int, TerrainType> navalTerrainOf)
        {
            var scenes = GameSceneDataManager.Instance == null ? null : GameSceneDataManager.Instance.SingleplayerBattleScenes;
            Require(scenes != null && scenes.Count > 0, "singleplayer battle scenes are not loaded");
            var landBy = new Dictionary<int, List<SingleplayerBattleSceneData>>();
            var navalBy = new Dictionary<int, List<SingleplayerBattleSceneData>>();
            foreach (var scene in scenes)
                foreach (int index in scene.MapIndices ?? new List<int>())
                {
                    var into = scene.IsNaval ? navalBy : landBy;
                    List<SingleplayerBattleSceneData> list;
                    if (!into.TryGetValue(index, out list)) into[index] = list = new List<SingleplayerBattleSceneData>();
                    list.Add(scene);
                }
            land = new Dictionary<TerrainType, List<int>>(); sea = new Dictionary<TerrainType, List<int>>();
            landScene = new Dictionary<int, string>(); navalScene = new Dictionary<int, string>();
            landTerrainOf = new Dictionary<int, TerrainType>(); navalTerrainOf = new Dictionary<int, TerrainType>();
            foreach (var pair in landBy.OrderBy(p => p.Key))
                if (pair.Value.Count == 1 && pair.Key >= 1 && pair.Key <= 255)
                {
                    List<int> list;
                    if (!land.TryGetValue(pair.Value[0].Terrain, out list)) land[pair.Value[0].Terrain] = list = new List<int>();
                    list.Add(pair.Key); landScene[pair.Key] = pair.Value[0].SceneID; landTerrainOf[pair.Key] = pair.Value[0].Terrain;
                }
            foreach (var pair in navalBy.OrderBy(p => p.Key))
                if (pair.Value.Count == 1 && pair.Key >= 1 && pair.Key <= 255)
                {
                    List<int> list;
                    if (!sea.TryGetValue(pair.Value[0].Terrain, out list)) sea[pair.Value[0].Terrain] = list = new List<int>();
                    list.Add(pair.Key); navalScene[pair.Key] = pair.Value[0].SceneID; navalTerrainOf[pair.Key] = pair.Value[0].Terrain;
                }
            both = landScene.Keys.Where(navalScene.ContainsKey).OrderBy(i => i).ToList();
        }

        // The installed singleplayer battle-scene table the map's indices refer to (part of the map's identity).
        internal static string SceneTableSha()
        {
            var text = new StringBuilder();
            foreach (var s in GameSceneDataManager.Instance.SingleplayerBattleScenes.OrderBy(s => s.SceneID, StringComparer.Ordinal).ThenBy(s => s.IsNaval).ThenBy(s => s.Terrain))
                text.Append(s.SceneID).Append('|').Append(s.IsNaval).Append('|').Append(s.Terrain).Append('|')
                    .Append(String.Join(",", (s.MapIndices ?? new List<int>()).OrderBy(i => i))).Append('\n');
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        public static Dictionary<string, object> Install(DiagnosticMapScene map, string worldId)
        {
            Require(map != null && Campaign.Current != null && ReferenceEquals(Campaign.Current.MapSceneWrapper, map), "map is not the active campaign wrapper");
            var terrainSize = (Vec2)MapField("_terrainSize").GetValue(map);
            Require(terrainSize.X > 0 && terrainSize.Y > 0, "terrain size is not loaded");
            int width = (int)Math.Ceiling(terrainSize.X / CellSize), height = (int)Math.Ceiling(terrainSize.Y / CellSize);
            Require(width > 0 && height > 0 && width <= 4096 && height <= 4096, "battle map dimensions out of range");
            // The reader's own cell size (terrain size / map dimension), so sampled cells are the cells it reads.
            float cellX = terrainSize.X / width, cellY = terrainSize.Y / height;
            Dictionary<TerrainType, List<int>> land, sea;
            Dictionary<int, string> landScene, navalScene;
            List<int> both;
            Dictionary<int, TerrainType> landTerrainOf, navalTerrainOf;
            Candidates(out land, out sea, out landScene, out navalScene, out both, out landTerrainOf, out navalTerrainOf);
            Require(land.ContainsKey(TerrainType.Plain), "no installed unique Plain land index");
            Require(both.Count > 0, "no installed index names exactly one land and one naval scene");
            var navigation = Campaign.Current.Models.PartyNavigationModel;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int coastal = 0, unreachable = 0, noFace = 0;
            var bytes = new byte[width * height * 2];
            var generatedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var sceneCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var substitutions = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    // 3x3 samples classified by the navigation model itself: terrain a land party may stand on, terrain a
                    // ship may stand on (CoastalSea, OpenSea, Lake, River, ...), and terrain neither may enter (Mountain).
                    TerrainType? landTerrain = null, seaTerrain = null, blockedTerrain = null;
                    for (int sy = 0; sy < 3; ++sy)
                        for (int sx = 0; sx < 3; ++sx)
                        {
                            var p = new Vec2((x + .05f + .45f * sx) * cellX, (y + .05f + .45f * sy) * cellY);
                            bool centre = sx == 1 && sy == 1;
                            foreach (var face in new[] { new CampaignVec2(p, true).Face, new CampaignVec2(p, false).Face })
                            {
                                if (!face.IsValid()) continue;
                                var t = (TerrainType)face.FaceGroupIndex;
                                bool onLand = navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Default);
                                bool atSea = navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Naval);
                                if (onLand && (landTerrain == null || centre)) landTerrain = t;
                                if (atSea && seaTerrain == null) seaTerrain = t; // terrain valid for both still allows a naval battle
                                if (!onLand && !atSea && (blockedTerrain == null || centre)) blockedTerrain = t;
                            }
                        }
                    string key = (landTerrain == null ? "" : "land:" + landTerrain) + (landTerrain != null && seaTerrain != null ? "+" : "") + (seaTerrain == null ? "" : "sea:" + seaTerrain);
                    if (landTerrain == null && seaTerrain == null)
                    {
                        // No party can stand here; the index only has to be valid. It borrows the blocking terrain's scene.
                        if (blockedTerrain != null) { unreachable++; key = "unreachable:" + blockedTerrain; landTerrain = blockedTerrain; }
                        else { noFace++; key = "noFace"; landTerrain = TerrainType.Plain; }
                    }
                    int n; generatedCounts.TryGetValue(key, out n); generatedCounts[key] = n + 1;
                    TerrainType sceneLand = TerrainType.Plain;
                    if (landTerrain != null)
                    {
                        sceneLand = landTerrain.Value;
                        if (!land.ContainsKey(sceneLand))
                        {
                            TerrainType substitute;
                            sceneLand = LandSubstitute.TryGetValue(landTerrain.Value, out substitute) && land.ContainsKey(substitute) ? substitute : TerrainType.Plain;
                            string sub = "land:" + landTerrain + "->" + sceneLand;
                            substitutions.TryGetValue(sub, out n); substitutions[sub] = n + 1;
                        }
                    }
                    List<int> list;
                    uint pick = Mix(worldId, x / BlockCells, y / BlockCells);
                    if (seaTerrain != null)
                    {
                        // A party here may fight either kind of battle: one land and one naval scene, preferring both terrains;
                        // sea terrain without naval scenes of its own (Lake) borrows CoastalSea.
                        coastal++;
                        var seaScene = both.Any(i => navalTerrainOf[i] == seaTerrain.Value) ? seaTerrain.Value : TerrainType.CoastalSea;
                        if (seaScene != seaTerrain.Value) { string sub = "sea:" + seaTerrain + "->" + seaScene; substitutions.TryGetValue(sub, out n); substitutions[sub] = n + 1; }
                        list = both.Where(i => navalTerrainOf[i] == seaScene && landTerrainOf[i] == sceneLand).ToList();
                        if (list.Count == 0) list = both.Where(i => navalTerrainOf[i] == seaScene).ToList();
                        if (list.Count == 0) list = both;
                    }
                    else list = land[sceneLand];
                    int index = list[(int)(pick % (uint)list.Count)];
                    string scene = "land:" + (landScene.ContainsKey(index) ? landScene[index] : "-") + " naval:" + (navalScene.ContainsKey(index) ? navalScene[index] : "-");
                    int at = 2 * (y * width + x);
                    bytes[at] = (byte)index;
                    bytes[at + 1] = (byte)((x % BlockCells) | ((y % BlockCells) << 4));
                    sceneCounts.TryGetValue(scene, out n); sceneCounts[scene] = n + 1;
                }
            MapField("_battleTerrainIndexMap").SetValue(map, bytes);
            MapField("_battleTerrainIndexMapWidth").SetValue(map, width);
            MapField("_battleTerrainIndexMapHeight").SetValue(map, height);
            string sha;
            using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            return new Dictionary<string, object> {
                ["status"] = "installed",
                ["method"] = "SandBox.MapScene._battleTerrainIndexMap/_Width/_Height set by reflection after AfterLoad navigation registration",
                ["layout"] = "2 bytes per cell: [0] sp_battle_scenes map index, [1] (x % 16) | (y % 16) << 4 -> patch coordinates (x/15, y/15)",
                ["terrainSize"] = new[] { terrainSize.X, terrainSize.Y }, ["cellSize"] = new[] { cellX, cellY }, ["blockCells"] = BlockCells,
                ["sceneTableSha256"] = SceneTableSha(),
                ["width"] = width, ["height"] = height, ["sha256"] = sha,
                ["landCandidates"] = land.ToDictionary(p => p.Key.ToString(), p => (object)p.Value.Count),
                ["landAndNavalCandidates"] = both.Select(i => (object)(i + ":" + landScene[i] + "/" + navalScene[i])).ToArray(),
                ["seaTouchingCells"] = coastal, ["buildMilliseconds"] = watch.Elapsed.TotalMilliseconds,
                ["seaCandidates"] = sea.ToDictionary(p => p.Key.ToString(), p => (object)p.Value.ToArray()),
                ["generatedTerrainCells"] = generatedCounts, ["substitutions"] = substitutions,
                ["unreachableCells"] = unreachable, ["cellsWithoutFaces"] = noFace, ["scenesByCells"] = sceneCounts,
                ["samplingNote"] = "3x3 points per cell; slivers under about 0.1 of a cell along diagonal face edges can be missed"
            };
        }
    }
}
