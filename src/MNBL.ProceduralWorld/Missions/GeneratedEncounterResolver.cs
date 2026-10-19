using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MNBL.ProceduralWorld.Missions
{
    // MMR-42: field, naval and settlement mission selection on a generated map (docs/mission-scenes.md). Pure managed: the in-game battle map
    // (GeneratedBattleTerrain), the mismatch guard (MissionScenePatches), the probes and the offline tests share it.
    //
    // The engine's chain (MMR-60, IL): MenuHelper.EncounterAttackConsequence -> IMapScene.GetMapPatchAtPosition(main party) ->
    // DefaultSceneModel.GetBattleSceneForMapPatch(patch, naval). Scenes listing the patch's index with the encounter's IsNaval are taken; one
    // scene is used as is; several naval scenes are filtered by Terrain == the terrain under the main party (SandBox.MapScene.
    // GetTerrainTypeAtPosition), then a random one, else a random one of them all; several land scenes are random with an assert; none is a
    // random pick among the scenes of that terrain (and naval kind), silently, else an assert and any scene. A choice is deterministic when the
    // final list names one distinct scene (ExpectedScene; the multi-land and empty cases are always treated as random).
    //
    // Cell choice (Choose): each battle-map cell is sampled (3x3 points, land and sea faces classified by the navigation model) and given an
    // index the engine resolves deterministically for every sampled party: a land party (when land was sampled) finds exactly one land scene;
    // a ship on each sampled sea terrain finds exactly one naval scene. Among those, the index prefers the cell's geography:
    //   land   the scene class of the cell's biome (compiled worlds: masks.bin) or, without one, of its land face terrain (MissionScenes classes:
    //          open, woodland, steppe, rock, cold, desert, wetland -> Plain, Steppe, Desert, Swamp scenes, with a forest-density or TerrainTypes
    //          preference and fallback terrains); Forest, Snow and Mountain have no battle scenes of their own and borrow by these rules;
    //   relief the cell's landform (Mountain / ridge, Valley) prefers, within the class, scenes whose ground shows it (Mountain/Canyon, River/Water);
    //   sea    a naval scene of each sampled sea terrain itself (CoastalSea, OpenSea, River, UnderBridge); Lake and Water, which have none, take an
    //          index naming one naval scene only (preferring CoastalSea), which the engine then uses whatever the terrain.
    // Cells around a settlement's gate or port (Robust) take, where installed, an index every naval-valid terrain resolves deterministically,
    // since naval map events at a settlement (blockades, sally-outs) read the patch at the main party, which may stand on land there.
    // The pick among the preferred indices is a stable hash of the world and the cell's 16-cell block (blocks counted in canonical columns on
    // a periodic world, so a strip cell equals its canonical cell and a block boundary falls on the seam).
    internal sealed class BattleTable
    {
        public sealed class Row { public string Id; public bool Naval; public int Terrain; public int[] MapIndices; public string ForestDensity; public string[] TerrainTypes; }
        public sealed class Expected { public string Scene, Rule; public bool Deterministic; public string[] Candidates; }

        private readonly Dictionary<int, List<Row>> land = new Dictionary<int, List<Row>>(), naval = new Dictionary<int, List<Row>>();
        public readonly List<Row> Rows;
        public string Sha256 { get; private set; }
        public string InventorySha256 { get; private set; }

        // forest density "None" (the installed enum's absent value) is null, as the catalogue records an absent attribute.
        public BattleTable(IEnumerable<Row> rows, Func<int, string> terrainName)
        {
            Rows = rows.ToList();
            foreach (var r in Rows)
            {
                if (r.ForestDensity == "None" || r.ForestDensity == "") r.ForestDensity = null;
                foreach (int i in r.MapIndices ?? new int[0])
                {
                    var into = r.Naval ? naval : land; List<Row> list;
                    if (!into.TryGetValue(i, out list)) into[i] = list = new List<Row>();
                    list.Add(r);
                }
            }
            // GeneratedBattleTerrain.SceneTableSha: SceneID (ordinal), IsNaval, Terrain (enum value), sorted MapIndices with duplicates; the
            // inventory hash adds forest density and sorted TerrainTypes (what the class preferences read).
            var text = new StringBuilder(); var inventory = new StringBuilder();
            foreach (var r in Rows.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Naval).ThenBy(r => r.Terrain))
            {
                string indices = String.Join(",", (r.MapIndices ?? new int[0]).OrderBy(i => i));
                text.Append(r.Id).Append('|').Append(r.Naval ? "True" : "False").Append('|').Append(terrainName(r.Terrain)).Append('|').Append(indices).Append('\n');
                inventory.Append(r.Id).Append('|').Append(r.Naval ? "True" : "False").Append('|').Append(terrainName(r.Terrain)).Append('|').Append(r.ForestDensity ?? "").Append('|')
                    .Append(String.Join(",", (r.TerrainTypes ?? new string[0]).OrderBy(t => t, StringComparer.Ordinal))).Append('|').Append(indices).Append('\n');
            }
            Sha256 = MissionScenes.Sha(Encoding.UTF8.GetBytes(text.ToString()));
            InventorySha256 = MissionScenes.Sha(Encoding.UTF8.GetBytes(inventory.ToString()));
        }

        public static BattleTable FromCatalogue(MissionScenes m)
        {
            return new BattleTable(m.Battle.Select(b => new Row { Id = b.Id, Naval = b.Naval, Terrain = m.TerrainIds[b.Terrain], MapIndices = b.MapIndices, ForestDensity = b.ForestDensity, TerrainTypes = b.TerrainTypes }),
                                   t => m.TerrainNames[t]);
        }

        public IEnumerable<int> Indices { get { return land.Keys.Concat(naval.Keys).Distinct().Where(i => i >= 1 && i <= 255).OrderBy(i => i); } }

        // DefaultSceneModel.GetBattleSceneForMapPatch's choice for an index, an encounter kind and the terrain under the main party.
        public Expected ExpectedScene(int index, bool isNaval, int terrainAtParty)
        {
            List<Row> matching;
            (isNaval ? naval : land).TryGetValue(index, out matching);
            matching = matching ?? new List<Row>();
            var e = new Expected { Candidates = matching.Select(r => r.Id).ToArray() };
            if (matching.Count == 0) { e.Rule = "no scene lists index " + index + " (random pick among the scenes of the party's terrain)"; return e; }
            if (matching.Count == 1) { e.Scene = matching[0].Id; e.Deterministic = true; e.Rule = "a single scene lists the index"; return e; }
            if (!isNaval) { e.Rule = "several land scenes list the index (random, FailedAssert)"; return e; }
            var filtered = matching.Where(r => r.Terrain == terrainAtParty).Select(r => r.Id).Distinct().ToList();
            if (filtered.Count > 0)
            {
                e.Deterministic = filtered.Count == 1; e.Scene = e.Deterministic ? filtered[0] : null;
                e.Rule = e.Deterministic ? "the one naval scene of the terrain under the party" : "random among " + filtered.Count + " naval scenes of the terrain under the party";
                return e;
            }
            var all = matching.Select(r => r.Id).Distinct().ToList();
            e.Deterministic = all.Count == 1; e.Scene = e.Deterministic ? all[0] : null;
            e.Rule = e.Deterministic ? "no naval scene of the terrain under the party; the index names one naval scene" : "no naval scene of the terrain under the party (random among " + all.Count + ")";
            return e;
        }

        public Row UniqueLand(int index) { List<Row> l; return land.TryGetValue(index, out l) && l.Count == 1 ? l[0] : null; }

        // The naval scene a ship on this terrain gets deterministically, or null; exact: a scene of that terrain itself.
        public string NavalScene(int index, int terrain, out bool exact)
        {
            exact = false;
            List<Row> l; if (!naval.TryGetValue(index, out l)) return null;
            var e = ExpectedScene(index, true, terrain);
            if (!e.Deterministic) return null;
            exact = l.Any(r => r.Terrain == terrain && r.Id == e.Scene);
            return e.Scene;
        }
        public Row NavalRow(int index, string scene) { List<Row> l; return naval.TryGetValue(index, out l) ? l.FirstOrDefault(r => r.Id == scene) : null; }
    }

    // One battle-map cell's samples: the terrain of each land-valid, naval-valid and blocked face found (centre first), the biome and landform at
    // its centre (null without them), and whether it lies at a settlement's gate or port (Robust).
    internal sealed class CellSamples
    {
        public readonly List<int> Land = new List<int>(), Sea = new List<int>(), Blocked = new List<int>();
        public int? CentreLand, CentreBlocked;
        public string Biome, Landform;
        public bool Robust;
    }

    internal sealed class CellChoice
    {
        public int Index;
        public string Key, Class, Tier, LandScene;
        public Dictionary<int, string> Naval = new Dictionary<int, string>();
        public bool Preferred, Unreachable, NoFace, Relief, Robust;
    }

    internal sealed class GeneratedEncounterResolver
    {
        public readonly MissionScenes Catalogue;
        public readonly BattleTable Table;
        private readonly Dictionary<string, List<int>> landByClass = new Dictionary<string, List<int>>(StringComparer.Ordinal), preferredByClass = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<KeyValuePair<string, List<int>>>> fallbackByClass = new Dictionary<string, List<KeyValuePair<string, List<int>>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<int>> reliefIndices = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        private readonly HashSet<int> robust;
        private readonly int[] navalTerrains;
        private readonly List<int> indices;
        private readonly Dictionary<string, List<int>> seaCache = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> tierCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly int coastal;
        private const string PreferredSeaTier = "class land + own sea";

        // table: the battle table the game has loaded (in game, from GameSceneDataManager); it must be the catalogue's, inventory included.
        public GeneratedEncounterResolver(MissionScenes catalogue, BattleTable table)
        {
            Catalogue = catalogue; Table = table;
            if (table.Sha256 != catalogue.TableSha256) throw new InvalidOperationException("The loaded battle scene table (" + table.Sha256 + ") is not the catalogue's (" + catalogue.TableSha256 + "): refusing to select scenes from another table.");
            if (table.InventorySha256 != catalogue.InventorySha256) throw new InvalidOperationException("The loaded battle scenes' forest density or ground (" + table.InventorySha256 + ") differ from the catalogue's (" + catalogue.InventorySha256 + ").");
            indices = table.Indices.ToList();
            coastal = catalogue.TerrainIds["CoastalSea"];
            navalTerrains = catalogue.NavalTerrains.Select(t => catalogue.TerrainIds[t]).ToArray();
            robust = new HashSet<int>(indices.Where(i => navalTerrains.All(t => { bool exact; return table.NavalScene(i, t, out exact) != null; })));
            Func<string, List<int>> ofTerrain = name => { int terrain = catalogue.TerrainIds[name]; return indices.Where(i => { var r = table.UniqueLand(i); return r != null && r.Terrain == terrain; }).ToList(); };
            Func<int, BattleTable.Row> scene = i => table.UniqueLand(i);
            foreach (var c in catalogue.Classes.Values)
            {
                var all = ofTerrain(c.Terrain);
                List<int> preferred;
                if (c.ForestDensity != null)
                {
                    // Forest density in priority order: the first value any of the terrain's scenes has (High before Low).
                    preferred = new List<int>();
                    foreach (var density in c.ForestDensity)
                    {
                        preferred = all.Where(i => String.Equals(scene(i).ForestDensity, density, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (preferred.Count > 0) break;
                    }
                }
                else if (c.TerrainTypes != null) preferred = all.Where(i => (scene(i).TerrainTypes ?? new string[0]).Any(x => c.TerrainTypes.Contains(x, StringComparer.Ordinal))).ToList();
                else preferred = all;
                landByClass[c.Name] = all; preferredByClass[c.Name] = preferred;
                fallbackByClass[c.Name] = c.Fallback.Select(f => new KeyValuePair<string, List<int>>(f, ofTerrain(f))).ToList();
            }
            foreach (var r in catalogue.Relief)
                reliefIndices[r.Key] = new HashSet<int>(indices.Where(i => { var row = table.UniqueLand(i); return row != null && (row.TerrainTypes ?? new string[0]).Any(t => r.Value.Contains(t, StringComparer.Ordinal)); }));
        }

        public List<int> LandCandidates(string cls, bool preferred) { return (preferred ? preferredByClass : landByClass)[cls]; }
        public IEnumerable<int> RobustIndices { get { return robust.OrderBy(i => i); } }

        public string ClassOf(CellSamples s)
        {
            string cls;
            if (s.Biome != null && Catalogue.BiomeClass.TryGetValue(s.Biome, out cls)) return cls;
            int? t = s.CentreLand ?? (s.Land.Count > 0 ? (int?)s.Land[0] : null) ?? s.CentreBlocked ?? (s.Blocked.Count > 0 ? (int?)s.Blocked[0] : null);
            string name;
            if (t.HasValue && Catalogue.TerrainNames.TryGetValue(t.Value, out name) && Catalogue.FaceClass.TryGetValue(name, out cls)) return cls;
            return "open";
        }

        // Stable across processes and platforms (string.GetHashCode is not): FNV-1a over "worldId:bx:by" (MMR-60's rule).
        public static uint Mix(string worldId, int bx, int by)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(worldId + ":" + bx.ToString(CultureInfo.InvariantCulture) + ":" + by.ToString(CultureInfo.InvariantCulture))) { h ^= b; h *= 16777619; }
            return h;
        }

        public CellChoice Choose(CellSamples s, string worldId, int bx, int by)
        {
            var c = new CellChoice { Class = ClassOf(s) };
            bool hasLand = s.Land.Count > 0;
            var sea = s.Sea.Distinct().OrderBy(t => t).ToList();
            c.Key = (hasLand ? "land:" + Name(s.CentreLand ?? s.Land[0]) : "") + (hasLand && sea.Count > 0 ? "+" : "") + (sea.Count > 0 ? "sea:" + String.Join("/", sea.Select(Name)) : "");
            if (!hasLand && sea.Count == 0)
            {
                if (s.Blocked.Count > 0) { c.Unreachable = true; c.Key = "unreachable:" + Name(s.CentreBlocked ?? s.Blocked[0]); }
                else { c.NoFace = true; c.Key = "noFace"; }
            }
            List<int> list; uint pick = Mix(worldId, bx, by);
            if (sea.Count == 0)
            {
                var preferred = preferredByClass[c.Class]; var all = landByClass[c.Class];
                var fallback = fallbackByClass[c.Class].FirstOrDefault(f => f.Value.Count > 0);
                if (preferred.Count > 0) { list = preferred; c.Tier = "class"; c.Preferred = true; }
                else if (all.Count > 0) { list = all; c.Tier = "class terrain (no preferred scene)"; }
                else if (fallback.Value != null) { list = fallback.Value; c.Tier = "fallback " + fallback.Key; }
                else { list = landByClass["open"]; c.Tier = "open (class has no scene)"; }
            }
            else list = SeaCandidates(c.Class, hasLand, sea, out c.Tier, out c.Preferred);
            // Relief: within the chosen candidates, the scenes whose ground shows the cell's landform.
            HashSet<int> relief;
            if (s.Landform != null && reliefIndices.TryGetValue(s.Landform, out relief))
            {
                var shown = list.Where(relief.Contains).ToList();
                if (shown.Count > 0) { list = shown; c.Relief = true; c.Tier += " + relief"; }
            }
            // A settlement's gate or port: an index every naval-valid terrain resolves, among the candidates, else among all of the cell's
            // land guarantee (the class's terrain first).
            if (s.Robust)
            {
                var r = list.Where(robust.Contains).ToList();
                if (r.Count == 0)
                {
                    Func<int, bool> landOk = i => !hasLand || Table.UniqueLand(i) != null;
                    r = landByClass[c.Class].Where(robust.Contains).ToList();
                    if (r.Count == 0) r = fallbackByClass[c.Class].SelectMany(f => f.Value).Where(robust.Contains).ToList();
                    if (r.Count == 0) r = indices.Where(i => robust.Contains(i) && landOk(i) && Table.UniqueLand(i) != null).ToList();
                }
                if (r.Count > 0) { list = r; c.Robust = true; c.Tier += " (settlement: robust naval)"; }
            }
            c.Index = list[(int)(pick % (uint)list.Count)];
            var lr = Table.UniqueLand(c.Index); c.LandScene = lr == null ? null : lr.Id;
            foreach (int t in sea) { bool exact; c.Naval[t] = Table.NavalScene(c.Index, t, out exact); }
            return c;
        }

        // Indices a cell touching the sea may take, best tier first: every sampled sea terrain resolves to one naval scene (exactly its own
        // terrain where it has scenes; Lake/Water to an index naming one naval scene, CoastalSea preferred) and a land party (when land was
        // sampled; preferred even without) finds one land scene, of the cell's class where possible, then its fallback terrains.
        private List<int> SeaCandidates(string cls, bool hasLand, List<int> sea, out string tier, out bool preferred)
        {
            string key = cls + "|" + hasLand + "|" + String.Join(",", sea);
            preferred = false;
            List<int> cached;
            if (seaCache.TryGetValue(key, out cached)) { tier = tierCache[key]; preferred = tier == PreferredSeaTier; return cached; }
            var classPreferred = new HashSet<int>(preferredByClass[cls]); var classAll = new HashSet<int>(landByClass[cls]);
            Func<int, bool> exactSea = i => sea.All(t => { bool exact; var scene = Table.NavalScene(i, t, out exact); return scene != null && (exact || !HasOwnNaval(t)); });
            Func<int, bool> coastalSubstitute = i => sea.Where(t => !HasOwnNaval(t)).All(t => { bool exact; var scene = Table.NavalScene(i, t, out exact); var row = scene == null ? null : Table.NavalRow(i, scene); return row != null && row.Terrain == coastal; });
            Func<int, bool> anySea = i => sea.All(t => { bool exact; return Table.NavalScene(i, t, out exact) != null; });
            var tiers = new List<KeyValuePair<string, Func<int, bool>>> {
                new KeyValuePair<string, Func<int, bool>>(PreferredSeaTier, i => classPreferred.Contains(i) && exactSea(i) && coastalSubstitute(i)),
                new KeyValuePair<string, Func<int, bool>>("class terrain land + own sea", i => classAll.Contains(i) && exactSea(i) && coastalSubstitute(i)) };
            foreach (var f in fallbackByClass[cls]) { var set = new HashSet<int>(f.Value); tiers.Add(new KeyValuePair<string, Func<int, bool>>("fallback " + f.Key + " land + own sea", i => set.Contains(i) && exactSea(i) && coastalSubstitute(i))); }
            tiers.Add(new KeyValuePair<string, Func<int, bool>>("any land + own sea", i => Table.UniqueLand(i) != null && exactSea(i) && coastalSubstitute(i)));
            tiers.Add(new KeyValuePair<string, Func<int, bool>>("class terrain land + any deterministic sea", i => classAll.Contains(i) && anySea(i)));
            foreach (var f in fallbackByClass[cls]) { var set = new HashSet<int>(f.Value); tiers.Add(new KeyValuePair<string, Func<int, bool>>("fallback " + f.Key + " land + any deterministic sea", i => set.Contains(i) && anySea(i))); }
            tiers.Add(new KeyValuePair<string, Func<int, bool>>("any land + any deterministic sea", i => Table.UniqueLand(i) != null && anySea(i)));
            if (!hasLand) tiers.Add(new KeyValuePair<string, Func<int, bool>>("naval only (no land sampled)", anySea));
            foreach (var t in tiers)
            {
                var list = indices.Where(t.Value).ToList();
                if (list.Count == 0) continue;
                tier = t.Key; seaCache[key] = list; tierCache[key] = tier; preferred = tier == PreferredSeaTier;
                return list;
            }
            throw new InvalidOperationException("No installed index resolves a cell with sea terrain " + String.Join("/", sea.Select(Name)) + (hasLand ? " and land" : "") + " deterministically.");
        }

        private bool HasOwnNaval(int terrain) { return Table.Rows.Any(r => r.Naval && r.Terrain == terrain); }
        public string Name(int terrain) { string n; return Catalogue.TerrainNames.TryGetValue(terrain, out n) ? n : terrain.ToString(CultureInfo.InvariantCulture); }

        // The battle map: two bytes per cell, [0] the index and [1] (x % 16) | (y % 16) << 4 in canonical columns (GetMapPatchAtPosition reads the
        // patch coordinates as (b & 15, b >> 4) / 15). sample(cx, y): the samples of physical column cx (canonical on a periodic world), row y.
        public sealed class Map
        {
            public byte[] Bytes; public int Width, Height, OriginCell, PeriodCells; public string Sha256;
            public SortedDictionary<string, int> Keys = new SortedDictionary<string, int>(StringComparer.Ordinal), Classes = new SortedDictionary<string, int>(StringComparer.Ordinal),
                                                 Tiers = new SortedDictionary<string, int>(StringComparer.Ordinal), Scenes = new SortedDictionary<string, int>(StringComparer.Ordinal),
                                                 Biomes = new SortedDictionary<string, int>(StringComparer.Ordinal), Landforms = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public int SeaTouching, Unreachable, NoFace, Preferred, Relief, Robust, RobustCells, LandCells, LandDeterministic, SeaChecks, SeaDeterministic, SeaExact;
            public Dictionary<long, CellChoice> Choices = new Dictionary<long, CellChoice>();   // canonical cell (y * 1000003 + lx) -> choice
        }

        public Map Build(int width, int height, int originCell, int periodCells, bool periodic, string worldId, Func<int, int, CellSamples> sample)
        {
            var map = new Map { Bytes = new byte[width * height * 2], Width = width, Height = height, OriginCell = originCell, PeriodCells = periodCells };
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int lx = periodic ? CanonicalColumn(x, originCell, periodCells) : x, cx = periodic ? originCell + lx : x;
                    long key = (long)y * 1000003 + lx;
                    CellChoice c;
                    if (!map.Choices.TryGetValue(key, out c))
                    {
                        var s = sample(cx, y);
                        c = Choose(s, worldId, lx / Catalogue.BlockSize, y / Catalogue.BlockSize);
                        map.Choices[key] = c;
                        Count(map.Keys, c.Key); Count(map.Classes, c.Class); Count(map.Tiers, c.Tier);
                        if (s.Biome != null) Count(map.Biomes, s.Biome);
                        if (s.Landform != null) Count(map.Landforms, s.Landform);
                        Count(map.Scenes, "land:" + (c.LandScene ?? "-") + " naval:" + (c.Naval.Count == 0 ? "-" : String.Join("/", c.Naval.OrderBy(n => n.Key).Select(n => Name(n.Key) + "=" + (n.Value ?? "RANDOM")))));
                        if (c.Naval.Count > 0) map.SeaTouching++;
                        if (c.Unreachable) map.Unreachable++;
                        if (c.NoFace) map.NoFace++;
                        if (c.Preferred) map.Preferred++;
                        if (c.Relief) map.Relief++;
                        if (s.Robust) { map.RobustCells++; if (c.Robust) map.Robust++; }
                        if (s.Land.Count > 0) { map.LandCells++; if (c.LandScene != null) map.LandDeterministic++; }
                        foreach (var n in c.Naval)
                        {
                            map.SeaChecks++; if (n.Value != null) map.SeaDeterministic++;
                            bool exact; Table.NavalScene(c.Index, n.Key, out exact); if (exact) map.SeaExact++;
                        }
                    }
                    int at = 2 * (y * width + x);
                    map.Bytes[at] = (byte)c.Index;
                    map.Bytes[at + 1] = (byte)((lx % Catalogue.BlockSize) | ((y % Catalogue.BlockSize) << 4));
                }
            map.Sha256 = MissionScenes.Sha(map.Bytes);
            return map;
        }
        private static void Count(IDictionary<string, int> d, string key) { int n; d.TryGetValue(key ?? "", out n); d[key ?? ""] = n + 1; }

        public Dictionary<string, object> Record(Map map)
        {
            return new Dictionary<string, object> {
                ["catalogueSha256"] = Catalogue.Sha256, ["sceneTableSha256"] = Table.Sha256, ["inventorySha256"] = Table.InventorySha256, ["width"] = map.Width, ["height"] = map.Height, ["sha256"] = map.Sha256,
                ["cellsByGeography"] = map.Keys, ["cellsByClass"] = map.Classes, ["cellsByTier"] = map.Tiers, ["cellsByBiome"] = map.Biomes, ["cellsByLandform"] = map.Landforms, ["scenesByCells"] = map.Scenes,
                ["seaTouchingCells"] = map.SeaTouching, ["unreachableCells"] = map.Unreachable, ["cellsWithoutFaces"] = map.NoFace, ["preferredCells"] = map.Preferred, ["reliefCells"] = map.Relief,
                ["settlementCells"] = map.RobustCells, ["settlementCellsRobust"] = map.Robust, ["robustIndices"] = robust.Count,
                ["landCells"] = map.LandCells, ["landDeterministic"] = map.LandDeterministic, ["seaTerrainChecks"] = map.SeaChecks, ["seaDeterministic"] = map.SeaDeterministic, ["seaOwnTerrain"] = map.SeaExact,
                ["distinctCells"] = map.Keys.Values.Sum() };
        }

        // Periodic seam (MMR-64): the canonical column of a physical cell column, and SandBox.MapScene.GetMapPatchAtPosition's read (cell =
        // floor(position / terrainSize * dimension), clamped; patch coordinates (b & 15, b >> 4) / 15; the engine computes in float32).
        public static int CanonicalColumn(int x, int originCell, int periodCells) { return ((x - originCell) % periodCells + periodCells) % periodCells; }
        public static int[] ReadPatch(byte[] map, int width, int height, double terrainX, double terrainY, double x, double y, out double px, out double py)
        {
            int cx = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(x / terrainX * width))), cy = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(y / terrainY * height)));
            int at = 2 * (cy * width + cx); byte b = map[at + 1];
            px = (b & 15) / 15.0; py = (b >> 4) / 15.0;
            return new[] { map[at], cx, cy };
        }
        // The battle's encounter direction (PatchEncounterDir) on a periodic world: MMR-63's transpiled attacker - defender, the x difference the
        // short way through the seam (PeriodicWorldCoordinates.DeltaX); planar on other worlds (width 0). Used by the offline tests.
        public static double[] EncounterDirection(double[] attacker, double[] defender, double width)
        {
            double dx = attacker[0] - defender[0], dy = attacker[1] - defender[1];
            if (width > 0) { dx = ((dx % width) + width) % width; if (dx > width / 2) dx -= width; }
            double length = Math.Sqrt(dx * dx + dy * dy);
            return length == 0 ? new[] { 0.0, 0.0 } : new[] { dx / length, dy / length };
        }

        // Geographic mismatch: what the scene model opened against what the generated map names. index 0 means no generated battle map (the
        // reader's null-map value, which no installed scene lists: Calradia indexing would have been silent).
        public Dictionary<string, object> Check(int index, bool isNaval, int terrainAtParty, string opened)
        {
            var e = Table.ExpectedScene(index, isNaval, terrainAtParty);
            string problem = index == 0 ? "no generated battle map (patch index 0)" : !e.Deterministic ? "the generated patch does not resolve deterministically: " + e.Rule
                           : opened != null && opened != e.Scene ? "the opened scene " + opened + " is not the generated patch's " + e.Scene : null;
            return new Dictionary<string, object> { ["index"] = index, ["naval"] = isNaval, ["terrainAtParty"] = Name(terrainAtParty), ["opened"] = opened, ["expected"] = e.Scene,
                ["candidates"] = e.Candidates, ["rule"] = e.Rule, ["deterministic"] = e.Deterministic, ["agrees"] = problem == null, ["problem"] = problem };
        }

        // Conversation scenes (DefaultSceneModel.GetConversationSceneForMapPosition, IL): the most frequent of 57 samples around the party among
        // the conversation terrains (a Snow first sample counts as Plain; other terrains are not counted; none counted: Plain), then a random
        // conversation scene of that terrain, else a random one. Per generated terrain: the terrain it counts as and its scenes.
        public Dictionary<string, object> ConversationCoverage(IEnumerable<int> terrains)
        {
            var rows = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (int t in terrains.Distinct().OrderBy(t => t))
            {
                string name = Name(t), counted = name == "Snow" ? "Plain" : Catalogue.ConversationTerrains.Contains(name) ? name : null;
                var scenes = counted == null ? new List<string>() : Catalogue.Conversations.Where(c => c.Terrain == counted).Select(c => c.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                rows[name] = new Dictionary<string, object> { ["countsAs"] = counted, ["scenes"] = scenes,
                    ["rule"] = counted == null ? "not counted: the dominant counted terrain around it decides (Plain when none)" : scenes.Count > 0 ? "a random scene of " + counted : "no scene of " + counted + ": a random conversation scene" };
            }
            return new Dictionary<string, object> { ["terrains"] = rows };
        }
    }
}
