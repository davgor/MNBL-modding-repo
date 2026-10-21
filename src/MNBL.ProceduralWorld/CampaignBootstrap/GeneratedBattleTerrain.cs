using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SandBox;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.Missions;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-60: the generated campaign's battle-scene patch map (SandBox.MapScene._battleTerrainIndexMap*), which
    // SandBox.MapScene.GetMapPatchAtPosition samples for DefaultSceneModel.GetBattleSceneForMapPatch.
    // MMR-42: the cell choice is GeneratedEncounterResolver's (docs/mission-scenes.md): every party that can stand in a cell finds exactly one
    // scene (land; and naval per sampled sea terrain, through the engine's terrain tie-break), preferring the cell's geography (biome when the
    // world supplies one, else its land face terrain; each sea terrain's own naval scenes). MMR-283: the map is rebuilt against the loaded
    // battle table (scene packs add scenes); a table that serves less ground than the catalogue's (worldgen/policies/mission-scenes.json) is
    // refused before the navigation caches are built (Preflight; the setup screen and the loader check the active modules' tables earlier).
    //
    // Two bytes per cell, exactly as GetMapPatchAtPosition reads them: [0] an installed sp_battle_scenes map index and [1] the cell's place
    // inside its block as (x | y << 4), read back as (x/15, y/15) patch coordinates. Cells cover _terrainSize (the same extent the reader
    // divides by). Each cell is sampled at 3x3 points against the generated navmesh and classified by the PartyNavigationModel. Without this
    // map the reader returns scene index 0, which no installed scene lists; the scene model then silently picks a random scene.
    // MMR-64: on a periodic world every cell is computed at its canonical image: blocks are counted from the seam in canonical logical cells, so
    // a strip cell holds exactly its canonical cell's bytes and a block boundary falls on the seam. Needs whole cells per strip and period.
    public static class GeneratedBattleTerrain
    {
        public const float CellSize = 4f;

        // MMR-42: a world's terrain context at a physical position (compiled worlds: the bundle's masks.bin through WorldSurface.BiomeAt and
        // LandformAt, named by terrain-materials.json), registered for its scene before the campaign loads (MMR-45 registers it for the world
        // scene it activates); fixtures have none and use their face terrain.
        public sealed class TerrainContext { public string Biome, Landform; }
        public static readonly Dictionary<string, Func<double, double, TerrainContext>> ContextSources = new Dictionary<string, Func<double, double, TerrainContext>>(StringComparer.Ordinal);

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

        // The installed singleplayer battle-scene table, as the resolver reads it.
        internal static BattleTable InstalledTable()
        {
            var scenes = GameSceneDataManager.Instance == null ? null : GameSceneDataManager.Instance.SingleplayerBattleScenes;
            Require(scenes != null && scenes.Count > 0, "singleplayer battle scenes are not loaded");
            return new BattleTable(scenes.Select(s => new BattleTable.Row { Id = s.SceneID, Naval = s.IsNaval, Terrain = (int)s.Terrain, MapIndices = (s.MapIndices ?? new List<int>()).ToArray(),
                ForestDensity = s.ForestDensity.ToString(), TerrainTypes = (s.TerrainTypes ?? new List<TerrainType>()).Select(t => t.ToString()).ToArray() }), t => ((TerrainType)t).ToString());
        }

        private static GeneratedEncounterResolver resolver;
        // The world's resolver (the catalogue over the loaded table), built once per process: every campaign loads the same active modules'
        // tables (Campaign.InitializeScenes).
        internal static GeneratedEncounterResolver Resolver()
        {
            if (resolver == null) resolver = new GeneratedEncounterResolver(MissionScenes.Installed(), InstalledTable());
            return resolver;
        }

        // MMR-283: the table a campaign will load, read at the main menu, where no GameSceneDataManager exists yet (the campaign makes it and
        // reads every active module's ModuleData/sp_battle_scenes.xml in active-module order: Campaign.InitializeScenes, installed IL).
        internal static BattleTableCheck CheckModuleTables()
        {
            var problems = new List<KeyValuePair<string, string>>();
            var files = TaleWorlds.ModuleManager.ModuleHelper.GetActiveModules()
                .Select(m => new KeyValuePair<string, string>(m.Id, System.IO.Path.Combine(TaleWorlds.ModuleManager.ModuleHelper.GetModuleFullPath(m.Id), "ModuleData", "sp_battle_scenes.xml")))
                .Where(f => System.IO.File.Exists(f.Value)).ToList();
            var catalogue = MissionScenes.Installed();
            return BattleTableCheck.Of(catalogue, BattleTable.FromModuleFiles(files, catalogue, problems), problems);
        }

        // MMR-283: the loaded table, checked before a world's navigation caches are built (GeneratedMapScene.AfterLoad). Null when the battle
        // map can be built; else the failed record. Never throws.
        internal static Dictionary<string, object> Preflight()
        {
            try
            {
                var check = BattleTableCheck.Of(MissionScenes.Installed(), Resolver().Table);
                if (check.Ok) return null;
                var record = check.Record(); record["status"] = "failed";
                var culprit = check.Culprit(); record["error"] = "the loaded battle scenes leave " + check.Lost.Count + " kinds of ground without a deterministic scene (first " + check.Lost[0] + "; " + (culprit.Key ?? "?") + " of " + (culprit.Value ?? "an unknown module") + ")";
                return record;
            }
            catch (Exception error) { return new Dictionary<string, object> { ["status"] = "failed", ["error"] = error.GetType().Name + ": " + error.Message }; }
        }

        // MMR-283: patches a world registers for its scene before its campaign loads, applied to every cell's samples before its choice
        // (MMR-260/261: the stored mutation cells' classes through CellSamples.ClassOverride). Canonical (lx, y).
        internal static readonly Dictionary<string, List<Action<int, int, CellSamples>>> CellPatches = new Dictionary<string, List<Action<int, int, CellSamples>>>(StringComparer.Ordinal);

        // The battle map's cells over the map's terrain: the reader's own cell size (terrain size / map dimension), so sampled cells are the
        // cells it reads.
        internal static void Cells(GeneratedMapScene map, out int width, out int height, out float cellX, out float cellY, out Vec2 terrainSize)
        {
            terrainSize = (Vec2)MapField("_terrainSize").GetValue(map);
            Cells(terrainSize, out width, out height, out cellX, out cellY);
        }
        internal static void Cells(Vec2 terrainSize, out int width, out int height, out float cellX, out float cellY)
        {
            Require(terrainSize.X > 0 && terrainSize.Y > 0, "terrain size is not loaded");
            width = (int)Math.Ceiling(terrainSize.X / CellSize); height = (int)Math.Ceiling(terrainSize.Y / CellSize);
            Require(width > 0 && height > 0 && width <= 4096 && height <= 4096, "battle map dimensions out of range");
            cellX = terrainSize.X / width; cellY = terrainSize.Y / height;
        }
        // The periodic origin and period in battle-map cells (whole cells, as Install requires), or (0, width) on a planar world.
        internal static void Period(PeriodicWorldCoordinates coordinates, int width, float cellX, out int originCell, out int periodCells)
        {
            originCell = 0; periodCells = width;
            if (coordinates == null) return;
            double origin = coordinates.OriginX / cellX, period = coordinates.Width / cellX;
            Require(Math.Abs(origin - Math.Round(origin)) < 1e-6 && Math.Abs(period - Math.Round(period)) < 1e-6, "the periodic strip and width are not whole battle-map cells");
            originCell = (int)Math.Round(origin); periodCells = (int)Math.Round(period);
        }

        // MMR-260: the installed battle map, its request and its owner map, so a live mutation re-chooses the cells it changed (RebuildCells writes
        // the installed byte[] in place, in every strip image of each cell). Forgotten with the map.
        private static GeneratedEncounterResolver.MapRequest installedRequest;
        private static GeneratedEncounterResolver.Map installedMap;
        private static object installedOwner;
        internal static int Repatch(GeneratedMapScene map, IEnumerable<long> keys)
        {
            if (installedMap == null || !ReferenceEquals(installedOwner, map)) return -1;
            return Resolver().RebuildCells(installedMap, installedRequest, keys);
        }
        internal static void Forget(GeneratedMapScene map) { if (ReferenceEquals(installedOwner, map)) { installedMap = null; installedRequest = null; installedOwner = null; } }

        // The world's terrain context for its map scene (compiled worlds), or null (fixtures: their face terrain decides).
        internal static Func<double, double, TerrainContext> Context(GeneratedMapScene map)
        {
            Func<double, double, TerrainContext> context; string sceneName = map.Scene == null ? null : map.Scene.GetName();
            return sceneName != null && ContextSources.TryGetValue(sceneName, out context) ? context : null;
        }

        // A cell's 3x3 samples classified by the navigation model itself: terrain a land party may stand on, terrain a ship may stand on
        // (CoastalSea, OpenSea, Lake, River, UnderBridge, ...), and terrain neither may enter (Mountain); and its terrain context. The battle
        // map's cell choice and MMR-52's probe classify cells through this one sampling.
        internal static CellSamples SampleCell(int cx, int y, float cellX, float cellY, Func<double, double, TerrainContext> context, TaleWorlds.CampaignSystem.ComponentInterfaces.PartyNavigationModel navigation)
        {
            var s = new CellSamples();
            for (int sy = 0; sy < 3; ++sy)
                for (int sx = 0; sx < 3; ++sx)
                {
                    var p = new Vec2((cx + .05f + .45f * sx) * cellX, (y + .05f + .45f * sy) * cellY);
                    bool centre = sx == 1 && sy == 1;
                    foreach (var face in new[] { new CampaignVec2(p, true).Face, new CampaignVec2(p, false).Face })
                    {
                        if (!face.IsValid()) continue;
                        var t = (TerrainType)face.FaceGroupIndex;
                        bool onLand = navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Default);
                        bool atSea = navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Naval);
                        if (onLand) { s.Land.Add((int)t); if (centre) s.CentreLand = (int)t; }
                        if (atSea) s.Sea.Add((int)t); // terrain valid for both still allows a naval battle
                        if (!onLand && !atSea) { s.Blocked.Add((int)t); if (centre) s.CentreBlocked = (int)t; }
                    }
                }
            if (context != null) { var c = context((cx + .5) * cellX, (y + .5) * cellY); if (c != null) { s.Biome = c.Biome; s.Landform = c.Landform; } }
            return s;
        }

        public static Dictionary<string, object> Install(GeneratedMapScene map, string worldId)
        {
            Require(map != null && Campaign.Current != null && ReferenceEquals(Campaign.Current.MapSceneWrapper, map), "map is not the active campaign wrapper");
            int width, height; float cellX, cellY; Vec2 terrainSize;
            Cells(map, out width, out height, out cellX, out cellY, out terrainSize);
            var coordinates = map.Periodic == null ? null : map.Periodic.Coordinates;
            int originCell, periodCells;
            Period(coordinates, width, cellX, out originCell, out periodCells);
            var r = Resolver();
            var context = Context(map);
            var navigation = Campaign.Current.Models.PartyNavigationModel;
            // MMR-283: the request the resolver rebuilds the map from, against the loaded table.
            var request = new GeneratedEncounterResolver.MapRequest { Width = width, Height = height, OriginCell = originCell, PeriodCells = periodCells, Periodic = coordinates != null, WorldId = worldId,
                Sample = (cx, y) => SampleCell(cx, y, cellX, cellY, context, navigation) };
            // The cells around every port settlement's position, gate and port (a ring of one cell), in canonical columns: naval map events at a
            // port read the patch at the main party, which may stand on land there. MMR-254 (RT-24): only ports; every other settlement's cells
            // keep their class's battle scene.
            var settlementCells = request.SettlementCells;
            Func<int, int> canonical = cx => coordinates == null ? cx : MNBL.ProceduralWorld.Missions.GeneratedEncounterResolver.CanonicalColumn(cx, originCell, periodCells);
            foreach (var settlement in TaleWorlds.CampaignSystem.Settlements.Settlement.All.Where(x => x.HasPort && x.StringId.StartsWith(worldId + "_", StringComparison.Ordinal)))
            {
                var points = new List<Vec2> { settlement.Position.ToVec2(), settlement.GatePosition.ToVec2() };
                if (settlement.HasPort) points.Add(settlement.PortPosition.ToVec2());
                foreach (var point in points)
                {
                    int px = (int)Math.Floor(point.X / cellX), py = (int)Math.Floor(point.Y / cellY);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int cy = py + dy, cx = px + dx;
                        if (cy >= 0 && cy < height && cx >= 0 && cx < width) settlementCells.Add((long)cy * 1000003 + canonical(cx));
                    }
                }
            }
            List<Action<int, int, CellSamples>> patches; string sceneName = map.Scene == null ? null : map.Scene.GetName();
            if (sceneName != null && CellPatches.TryGetValue(sceneName, out patches)) request.Patches.AddRange(patches);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var built = r.Rebuild(request);
            MapField("_battleTerrainIndexMap").SetValue(map, built.Bytes);
            MapField("_battleTerrainIndexMapWidth").SetValue(map, width);
            MapField("_battleTerrainIndexMapHeight").SetValue(map, height);
            installedRequest = request; installedMap = built; installedOwner = map;
            var report = new Dictionary<string, object> {
                ["status"] = "installed",
                ["method"] = "SandBox.MapScene._battleTerrainIndexMap/_Width/_Height set by reflection after AfterLoad navigation registration; cells chosen by GeneratedEncounterResolver",
                ["layout"] = "2 bytes per cell: [0] sp_battle_scenes map index, [1] (x % 16) | (y % 16) << 4 -> patch coordinates (x/15, y/15)",
                ["terrainSize"] = new[] { terrainSize.X, terrainSize.Y }, ["cellSize"] = new[] { cellX, cellY }, ["blockCells"] = r.Catalogue.BlockSize,
                ["periodic"] = coordinates == null ? null : new Dictionary<string, object> { ["originCell"] = originCell, ["periodCells"] = periodCells,
                    ["rule"] = "every cell computed at its canonical image; blocks counted from the seam in canonical logical cells", ["coordinatePolicy"] = coordinates.Identity },
                ["terrainContextSource"] = context == null ? "none (the land face terrain names the scene class; no relief)" : "the world's masks (WorldSurface.BiomeAt, LandformAt)",
                ["navigationSetsMatchCatalogue"] =
                    Enum.GetValues(typeof(TerrainType)).Cast<TerrainType>().Where(t => navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Default)).Select(t => t.ToString()).OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(r.Catalogue.LandTerrains.OrderBy(x => x, StringComparer.Ordinal)) &&
                    Enum.GetValues(typeof(TerrainType)).Cast<TerrainType>().Where(t => navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Naval)).Select(t => t.ToString()).OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(r.Catalogue.NavalTerrains.OrderBy(x => x, StringComparer.Ordinal)),
                ["navigationSets"] = new Dictionary<string, object> {
                    ["land"] = Enum.GetValues(typeof(TerrainType)).Cast<TerrainType>().Where(t => navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Default)).Select(t => t.ToString()).ToArray(),
                    ["naval"] = Enum.GetValues(typeof(TerrainType)).Cast<TerrainType>().Where(t => navigation.IsTerrainTypeValidForNavigationType(t, MobileParty.NavigationType.Naval)).Select(t => t.ToString()).ToArray() },
                ["buildMilliseconds"] = watch.Elapsed.TotalMilliseconds, ["cellPatches"] = request.Patches.Count,
                ["samplingNote"] = "3x3 points per cell; slivers under about 0.1 of a cell along diagonal face edges can be missed" };
            foreach (var kv in r.Record(built)) report[kv.Key] = kv.Value;
            return report;
        }
    }
}
