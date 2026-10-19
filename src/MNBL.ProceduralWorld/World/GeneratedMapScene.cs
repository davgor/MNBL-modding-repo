using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.DotNet;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using MNBL.ProceduralWorld.CampaignBootstrap;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Surface
{
    public sealed class GeneratedMapSceneCreator : IMapSceneCreator
    {
        public const string ModuleId = "MNBL.ProceduralWorld";
        public const string Slot = "mnbl_campaign_42";
        private readonly Action<string> trace;
        private readonly string sceneName;
        private readonly string worldId;
        private readonly Dictionary<string, object> supportReport;
        private readonly PeriodicWorld periodic;
        private readonly GeneratedClimate climate;
        public GeneratedMapSceneCreator(Action<string> trace) : this(Slot, trace) { }
        public GeneratedMapSceneCreator(string sceneName, Action<string> trace)
            : this(sceneName, trace, null, null) { }
        internal GeneratedMapSceneCreator(string sceneName, Action<string> trace,
            string worldId, Dictionary<string, object> supportReport, PeriodicWorld periodic = null, GeneratedClimate climate = null)
        {
            this.periodic = periodic;
            this.climate = climate;
            if (trace == null) throw new ArgumentNullException("trace");
            ValidateSceneName(sceneName);
            this.sceneName = sceneName;
            this.trace = trace;
            this.worldId = worldId;
            this.supportReport = supportReport;
            if ((worldId == null) != (supportReport == null)) throw new ArgumentException("Generated world identity and support report must be supplied together.");
        }
        internal static void ValidateSceneName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 96 || !name.StartsWith("mnbl_", StringComparison.Ordinal))
                throw new ArgumentException("An owned mnbl_ scene name is required.", "name");
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_')
                    throw new ArgumentException("Scene name must contain only lowercase ASCII letters, digits and underscores.", "name");
        }
        public static void ValidateFixture() { ValidateFixture(Slot); }
        public static void ValidateFixture(string name)
        {
            ValidateSceneName(name);
            var folder = Path.Combine(ModuleHelper.GetModuleFullPath(ModuleId), "SceneObj", name);
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Owned scene directory may not be a reparse point.");
            foreach (var file in new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" })
                if (!File.Exists(Path.Combine(folder, file))) throw new FileNotFoundException("Diagnostic fixture missing", Path.Combine(folder, file));
        }
        public IMapScene CreateMapScene() { return new GeneratedMapScene(sceneName, trace, worldId, supportReport, periodic, climate); }
    }

    // MMR-37: the owned SandBox.MapScene subclass of the runtime surface R07 selected (promoted from the MMR-18 diagnostic map scene;
    // docs/runtime-surface.md). IMapScene is re-implemented deliberately: the installed MapScene.Load is virtual final, and the installed
    // GetHeightAtPoint/GetMapPatchAtPosition are not virtual. Inherited members and the SandBox/War Sails castclass sites keep using the
    // populated base fields.
    //   * The scene is read through RuntimeWorldBackend (the campaign map profile, or an inspection world it binds to), under a world
    //     generation token (WorldGenerations) begun in Load and retired in Destroy.
    //   * Every height, normal, bounds, terrain size, map patch and face height query (the F06 height/normal/bounds/map-patch members)
    //     first refuses a scene that is not loaded, a world a later world superseded (StaleWorldException) and a disposed inspection world,
    //     so no query reaches another world's native data; then it is the installed query on the owned scene. No query falls back to
    //     Calradia's terrain or fixed native faces. Face and path queries are MMR-38's; seam picking and camera are MMR-66's.
    public sealed class GeneratedMapScene : MapScene, IMapScene
    {
        private readonly Action<string> trace;
        private readonly string sceneName;
        private readonly string worldId;
        private readonly Dictionary<string, object> supportReport;
        private bool loaded;
        private bool loading;
        private bool afterLoaded;
        private GenerationToken token;
        private RuntimeWorld world;
        internal GenerationToken Token { get { return token; } }
        internal RuntimeWorld World { get { return world; } }
        private readonly PeriodicWorld periodic;
        private readonly GeneratedClimate climate;
        internal PeriodicWorld Periodic { get { return periodic; } }

        // MMR-33 fixtures: a settlement the generated scene was not authored with has no entity. SettlementVisual.OnStartup then calls
        // IMapScene.AddNewEntityToMapScene(settlement id, position), whose installed MapScene implementation instantiates a prefab named
        // after the settlement id, which a generated id never has (run 36452 failed there). For a fixture settlement this makes its
        // entity from its prototype's vanilla map prefab, named with the settlement id; every other id takes the installed path.
        // Placing the world's settlement entities is MMR-40's.
        internal static readonly Dictionary<string, string> FixturePrefabs = new Dictionary<string, string>(StringComparer.Ordinal);
        // MMR-40: every other generated settlement without a scene entity (an MMR-34 hideout: hideouts are placed from the campaign build,
        // after the scene is encoded) gets its kind's full hierarchy from its record (SettlementEntityFactory.Unresolved: the record's centre,
        // gate and port, the siege markers and walls at their installed offsets; SettlementEntityRuntime). On a world's scene every town,
        // castle and village is baked, so one built here is a missing baked entity: it is listed (MissingBakedEntities, refused by
        // SettlementEntityChecks and MMR-45's activation) rather than hidden.
        internal static readonly List<string> RuntimeSettlementEntities = new List<string>();
        internal static readonly List<string> MissingBakedEntities = new List<string>();
        public new void AddNewEntityToMapScene(string entityId, in CampaignVec2 position)
        {
            string prefab;
            if (entityId != null && !FixturePrefabs.ContainsKey(entityId))
            {
                var settlement = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(entityId);
                if (settlement != null && entityId.StartsWith("mnbl_", StringComparison.Ordinal))
                {
                    var kind = settlement.IsHideout ? SiteKind.Hideout : settlement.IsVillage ? SiteKind.Village : settlement.IsCastle ? SiteKind.Castle : SiteKind.Town;
                    var notes = new List<string>();
                    double[] gate = null, port = null;
                    if (settlement.IsFortification) gate = new double[] { settlement.GatePosition.X, settlement.GatePosition.Y };
                    if (settlement.IsTown && settlement.HasPort) port = new double[] { settlement.PortPosition.X, settlement.PortPosition.Y };
                    var spec = SettlementEntityFactory.Unresolved(entityId, kind, new double[] { position.X, position.Y, Scene.GetTerrainHeight(position.ToVec2(), true) }, gate, port, SettlementPlacementPolicy.Installed());
                    SettlementEntityRuntime.Create(Scene, spec, notes);
                    RuntimeSettlementEntities.Add(entityId);
                    if (kind != SiteKind.Hideout && SceneNames.IsWorldScene(sceneName)) MissingBakedEntities.Add(entityId);
                    trace("runtime_settlement_entity:" + entityId + ":" + kind + (notes.Count > 0 ? ":" + String.Join("; ", notes) : ""));
                    return;
                }
            }
            if (entityId == null || !FixturePrefabs.TryGetValue(entityId, out prefab)) { base.AddNewEntityToMapScene(entityId, position); return; }
            var entity = GameEntity.Instantiate(Scene, prefab, true, true, "");
            if (entity == null) { trace("fixture_entity_prefab_missing:" + entityId + ":" + prefab); return; }
            entity.Name = entityId;
            entity.SetLocalPosition(position.AsVec3());
            trace("fixture_entity_created:" + entityId + ":" + prefab);
        }
        public GeneratedMapScene(Action<string> trace) : this(GeneratedMapSceneCreator.Slot, trace) { }
        public GeneratedMapScene(string sceneName, Action<string> trace)
            : this(sceneName, trace, null, null) { }
        internal GeneratedMapScene(string sceneName, Action<string> trace,
            string worldId, Dictionary<string, object> supportReport, PeriodicWorld periodic = null, GeneratedClimate climate = null)
        {
            this.periodic = periodic;
            this.climate = climate;
            if (trace == null) throw new ArgumentNullException("trace");
            GeneratedMapSceneCreator.ValidateSceneName(sceneName);
            this.sceneName = sceneName;
            this.trace = trace;
            this.worldId = worldId;
            this.supportReport = supportReport;
            if ((worldId == null) != (supportReport == null)) throw new ArgumentException("Generated world identity and support report must be supplied together.");
            // Fail before creating native resources if the installed field contract changed.
            foreach (var name in new[] { "_scene", "_agentRendererSceneController", "_minimumPositionCache",
                "_maximumPositionCache", "_maximumHeightCache", "_terrainSize", "_battleTerrainIndexMap",
                "_battleTerrainIndexMapWidth", "_battleTerrainIndexMapHeight" }) Field(name);
        }
        private static FieldInfo Field(string name)
        {
            var field = typeof(MapScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(typeof(MapScene).FullName, name);
            return field;
        }
        private void Set(string name, object value) { Field(name).SetValue(this, value); }

        public new void Load()
        {
            if (loaded) throw new InvalidOperationException("Diagnostic scene already loaded.");
            GeneratedMapSceneCreator.ValidateFixture(sceneName);
            // MMR-38: a compiled world (content-addressed scene) becomes a campaign map only with an activatable navigation report for its
            // installed navmesh (NavigationGate; MMR-45's activation transaction checks the same report before it gets here).
            // An exception escaping this engine callback ends the process without a managed record, so the refusal's diagnostics are traced first.
            if (SceneNames.IsWorldScene(sceneName))
            {
                try { NavigationGate.Require(SubModule.Store, sceneName, System.IO.Path.Combine(RuntimeWorldBackend.SceneFolder(sceneName), "navmesh.bin"), NavigationPolicy.Installed().Sha256); }
                catch (NavigationRefusedException refusal)
                {
                    foreach (var d in refusal.Diagnostics) trace("navigation_refused:" + d.ToString().Replace("\r", "").Replace("\n", " "));
                    throw;
                }
            }
            Utilities.PairSceneNameToModuleName(sceneName, GeneratedMapSceneCreator.ModuleId);
            PeriodicWorld.Current = periodic;
            PeriodicSeam.Reset();
            PeriodicQueries = PeriodicImageWins = PeriodicNativeQueries = PeriodicPruned = PeriodicStartMoves = 0;  // per world, not per process
            if (periodic != null)
            {
                trace("periodic_world:" + periodic.Width + "@" + periodic.OriginX);
                // Bannerlord.Harmony; removed again in Destroy. An exception escaping this engine callback ends the process
                // without a managed record, so a failed patch is traced and the world runs unpatched (the probe reports it).
                try { PeriodicPatches.Apply(); trace("periodic_patches:" + PeriodicPatches.Applied.Count); }
                catch (Exception error) { PeriodicPatches.LastError = error.ToString(); trace("periodic_patches_failed:" + error.ToString().Replace("\r", "").Replace("\n", " | ")); }
            }
            // MMR-39: the world's colour grade (its generated grid, else the neutral grid) is patched in before the read (the manager's Init
            // may run while the scene loads); Calradia's region grid never applies on a generated map.
            try
            {
                GeneratedColorGrade.Begin(climate == null ? null : climate.CopyColorGrade(), this);
                trace("colorgrade_patch_applied:" + (GeneratedColorGrade.Neutral ? "neutral" : climate.ColorGradeSha256.Substring(0, 12)));
            }
            catch (Exception error) { GeneratedColorGrade.LastError = error.ToString(); trace("colorgrade_patch_failed:" + error.ToString().Replace("\r", "").Replace("\n", " | ")); }
            trace("diagnostic_scene_read_begin:" + sceneName);
            // SandBox.MapScene.Load IL_0016..02bb through the runtime backend's campaign map profile; only the read target and diagnostics
            // differ. A new generation begins here: every earlier world's queries and late callbacks are refused from now on.
            token = WorldGenerations.Begin("campaign map " + sceneName);
            world = RuntimeWorldBackend.ReadCampaignMap(sceneName, token,
                (created, renderer) => { Set("_scene", created); Set("_agentRendererSceneController", renderer); },
                created => {
                    DisableUnwalkableNavigationMeshes();
                    created.SetNavMeshRegionMap(SandBoxHelpers.MapSceneHelper.GetRegionMapping(Campaign.Current.Models.PartyNavigationModel));
                });
            var scene = world.Scene;
            loading = true;
            Utilities.SetAllocationAlwaysValidScene(scene);
            var min = scene.GetFirstEntityWithName("border_min");
            var max = scene.GetFirstEntityWithName("border_max");
            if (min == null || max == null) throw new InvalidDataException("Campaign borders missing.");
            Set("_minimumPositionCache", min.GetGlobalFrame().origin.AsVec2);
            Set("_maximumPositionCache", max.GetGlobalFrame().origin.AsVec2);
            Set("_maximumHeightCache", max.GetGlobalFrame().origin.z);
            scene.DisableStaticShadows(true);
            scene.InvalidateTerrainPhysicsMaterials();
            scene.SetDontLoadInvisibleEntities(true);
            var water = scene.GetFirstEntityWithName("medit_water_plane");
            if (water != null) water.SetDoNotCheckVisibility(true);
            if (scene.GetFirstEntityWithScriptComponent("Town Scene Manager") == null)
                trace("diagnostic_warning:Town Scene Manager missing");
            AddSiegePrefabCache(scene);
            MBMapScene.LoadAtmosphereData(scene);
            Vec2i nodes;
            float nodeSize;
            int layerCount, layerVersion;
            scene.GetTerrainData(out nodes, out nodeSize, out layerCount, out layerVersion);
            Set("_terrainSize", new Vec2(nodes.X * nodeSize, nodes.Y * nodeSize));
            if (supportReport != null)
                GeneratedMapSupport.PrepareWeather(this, nodes.X * nodeSize, climate, supportReport, trace);
            Campaign.Current.Models.MapWeatherModel.InitializeCaches();
            MBMapScene.ValidateTerrainSoundIds();
            scene.OptimizeScene(true, false);
            byte[] battleMap = null;
            int width = 0, height = 0;
            if (supportReport == null) MBMapScene.GetBattleSceneIndexMap(scene, ref battleMap, ref width, ref height);
            else supportReport["battleSceneSelection"] = "pending: the generated battle-index map is installed in AfterLoad (MMR-60)";
            Set("_battleTerrainIndexMap", battleMap);
            Set("_battleTerrainIndexMapWidth", width);
            Set("_battleTerrainIndexMapHeight", height);
            scene.Tick(0.1f);
            var callback = new ManagedDelegate();
            callback.Instance = Campaign.LateAITick;
            Campaign.Current.CampaignLateAITickTask = AsyncTask.CreateWithDelegate(callback, false);
            loaded = true; loading = false;
            WorldGenerations.CampaignMapLoaded(token);
            trace("diagnostic_scene_loaded:" + scene.GetName() + ":generation " + token.Id);
        }

        // MMR-40: MapScreen.OnInitialize dereferences the scene's CampaignMapSiegePrefabEntityCache unguarded, and a generated scene carries
        // none (its OnInit needs the campaign's map scene, and every generated scene is also read without a campaign). The campaign map gets one
        // here, after the read (MapSceneWrapper.Scene is this scene) and before anything asks for it: a helper entity with the script, its
        // prefab variables set to the installed campaign map's (SettlementEntityContract.SiegeCacheVariables), then its callbacks (OnInit
        // instantiates the prefabs and caches their frames). A scene that already has one (the MMR-33 fixture slot) keeps it.
        internal static string SiegePrefabCacheSource;   // "scene", "added" (for the probes)
        private void AddSiegePrefabCache(Scene scene)
        {
            if (scene.GetFirstEntityWithScriptComponent<CampaignMapSiegePrefabEntityCache>() != null) { SiegePrefabCacheSource = "scene"; trace("siege_prefab_cache:scene"); return; }
            var contract = SettlementEntityContract.Installed(RuntimeWorldBackend.InstalledCampaignMap());
            var entity = GameEntity.CreateEmpty(scene, false, true, true);
            entity.Name = RuntimeWorldBackend.HelperPrefix + "_helper_campaignmapsiegeprefabentitycache";
            entity.CreateAndAddScriptComponent(SettlementEntityContract.SiegeCacheScript, false);
            var cache = entity.GetFirstScriptOfType<CampaignMapSiegePrefabEntityCache>();
            if (cache == null) throw new InvalidOperationException("The " + SettlementEntityContract.SiegeCacheScript + " script did not bind.");
            foreach (var kv in contract.SiegeCacheVariables)
            {
                var field = typeof(CampaignMapSiegePrefabEntityCache).GetField(kv.Key, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null || field.FieldType != typeof(string)) throw new MissingFieldException(typeof(CampaignMapSiegePrefabEntityCache).FullName, kv.Key);
                field.SetValue(cache, kv.Value);
            }
            entity.CallScriptCallbacks(true);
            SiegePrefabCacheSource = "added";
            trace("siege_prefab_cache:added:" + contract.SiegeCacheVariables.Count);
        }

        // MMR-37: binds a production map scene to an inspection world (no campaign): the installed query members then read that world's
        // scene. Borders, maximum height and terrain size come from the native scene (terrain nodes x node size, the scene bounding box and
        // the terrain height range), as Load takes them from the native scene. The binding is one of the world's owned resources:
        // disposing the world clears it first.
        internal static GeneratedMapScene Inspect(RuntimeWorld inspected, Action<string> trace)
        {
            if (inspected == null || inspected.Profile != "inspection") throw new ArgumentException("An inspection world is required.");
            var map = new GeneratedMapScene(inspected.SceneName, trace);
            var bounds = inspected.Bounds();
            float[] boxMin = (float[])bounds["boxMin"], boxMax = (float[])bounds["boxMax"];
            float nodeSize = (float)bounds["nodeSize"];
            map.Set("_scene", inspected.Scene);
            map.Set("_minimumPositionCache", new Vec2(boxMin[0], boxMin[1]));
            map.Set("_maximumPositionCache", new Vec2(boxMax[0], boxMax[1]));
            map.Set("_maximumHeightCache", (float)bounds["maxHeight"]);
            map.Set("_terrainSize", new Vec2((int)bounds["nodesX"] * nodeSize, (int)bounds["nodesY"] * nodeSize));
            map.token = inspected.Token; map.world = inspected; map.loaded = true;
            inspected.Ledger.Acquire("map scene binding", "RuntimeWorld (inspection)", () => { map.Set("_scene", null); map.loaded = false; });
            return map;
        }

        // ---- MMR-37: the F06 height, normal, bounds, map patch and face height members, guarded ----
        private void Live(string operation, bool duringLoad = false)
        {
            if (!loaded && !(duringLoad && loading)) throw new InvalidOperationException(operation + " on map scene " + sceneName + " before it loaded or after its world released it.");
            token.RequireNotSuperseded(operation + " on map scene " + sceneName);
            if (world != null && world.Profile == "inspection" && world.IsDisposed) throw new ObjectDisposedException("GeneratedMapScene " + sceneName, operation + " after its world was disposed.");
        }
        // The owned native scene, guarded like the queries (also while Load prepares it); installed callers typed MapScene reach the base.
        public new Scene Scene { get { Live("Scene", true); return base.Scene; } }
        public new bool GetHeightAtPoint(in CampaignVec2 point, ref float height) { Live("GetHeightAtPoint"); return base.GetHeightAtPoint(point, ref height); }
        public new void GetTerrainHeightAndNormal(Vec2 position, out float height, out Vec3 normal) { Live("GetTerrainHeightAndNormal"); base.GetTerrainHeightAndNormal(position, out height, out normal); }
        public new Vec3 GetGroundNormal(Vec2 position) { Live("GetGroundNormal"); return base.GetGroundNormal(position); }
        public new void GetMapBorders(out Vec2 minimumPosition, out Vec2 maximumPosition, out float maximumHeight) { Live("GetMapBorders"); base.GetMapBorders(out minimumPosition, out maximumPosition, out maximumHeight); }
        public new Vec2 GetTerrainSize() { Live("GetTerrainSize"); return base.GetTerrainSize(); }
        public new MapPatchData GetMapPatchAtPosition(in CampaignVec2 position) { Live("GetMapPatchAtPosition"); return base.GetMapPatchAtPosition(position); }
        public new float GetFaceVertexZ(PathFaceRecord navMeshFace) { Live("GetFaceVertexZ"); return base.GetFaceVertexZ(navMeshFace); }

        // ---- MMR-63: periodic path and distance queries ----
        // MMR-64: the target's placement whose native path is cheapest, by PeriodicDistanceModel.ShortestOverImages (nearest displacement
        // first, ties within 1e-3 to the earlier, an exact half width east), so the answer is the same whichever copy of the target is passed.
        // When the target's copy for a displacement lies outside the terrain the query starts from the start's copy a whole number of widths
        // away instead, so a route the long way around is not dropped. The strips copy the canonical faces' groups, so a copy's face is the
        // land or naval face there with the original face's group; the copies the caller passed keep their own faces. A path found from a
        // moved start is shifted back by the same widths, so it begins at the caller's start and runs on continuously (PeriodicSeam rebases a
        // party that follows it across the seam). Copies are chosen by path cost without the path query's extra cost multiplier, which the
        // installed distance query does not take.
        public static int PeriodicQueries, PeriodicImageWins, PeriodicNativeQueries, PeriodicPruned, PeriodicStartMoves;

        public new bool GetPathBetweenAIFaces(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, NavigationPath path, int[] excludedFaceIds, float extraCostMultiplier, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            float shift = 0;
            if (periodic != null)
            {
                PathFaceRecord fromFace, face; Vec2 from, image; float distance;
                if (NearestTargetImage(startingFace, endingFace, startingPosition, endingPosition, agentRadius, Campaign.PathFindingMaxCostLimit, excludedFaceIds,
                        regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand, out fromFace, out from, out face, out image, out distance))
                { shift = startingPosition.X - from.X; startingFace = fromFace; startingPosition = from; endingFace = face; endingPosition = image; }
            }
            bool found = base.GetPathBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, path, excludedFaceIds,
                extraCostMultiplier, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
            if (shift != 0 && path != null)
                for (int i = 0; i < path.Size; i++) { var point = path.PathPoints[i]; point.x += shift; path.OverridePathPointAtIndex(i, in point); }
            return found;
        }

        public new bool GetPathDistanceBetweenAIFaces(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, out float distance, int[] excludedFaceIds, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            if (periodic == null)
                return base.GetPathDistanceBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, out distance,
                    excludedFaceIds, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
            PathFaceRecord fromFace, face; Vec2 from, image;
            return NearestTargetImage(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, excludedFaceIds,
                regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand, out fromFace, out from, out face, out image, out distance);
        }

        // The unmodified native query (no images), for evidence comparisons.
        internal bool PlanarPathDistance(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, out float distance, int[] excludedFaceIds, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            return base.GetPathDistanceBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, out distance,
                excludedFaceIds, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
        }

        // The cheapest placement: the start (face and position) and target (face and position) the native query used, and its cost.
        internal bool NearestTargetImage(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, int[] excludedFaceIds, int toSea, int toLand,
            out PathFaceRecord fromFace, out Vec2 from, out PathFaceRecord face, out Vec2 image, out float distance)
        {
            PeriodicQueries++;
            fromFace = startingFace; from = startingPosition; face = endingFace; image = endingPosition; distance = float.MaxValue;
            if (!startingFace.IsValid()) return false;
            var used = new Dictionary<int, PathFaceRecord[]>();
            var points = new Dictionary<int, Vec2[]>();
            float startY = startingPosition.Y, targetY = endingPosition.Y;
            var result = PeriodicDistanceModel.ShortestOverImages(periodic.Coordinates, startingPosition.X, startY, endingPosition.X, targetY, distanceLimit,
                (PeriodicWorldCoordinates.Placement placement, double limit, out double cost) => {
                    cost = Double.MaxValue;
                    bool givenStart = placement.StartShift == 0, givenTarget = Math.Abs(placement.TargetX - endingPosition.X) < 1e-3;
                    var a = givenStart ? startingPosition : new Vec2((float)placement.FromX, startY);
                    var b = givenTarget ? endingPosition : new Vec2((float)placement.TargetX, targetY);
                    var fa = givenStart ? startingFace : ImageFace(a, startingFace);
                    var fb = givenTarget ? endingFace : ImageFace(b, endingFace);
                    if (!fa.IsValid() || !fb.IsValid()) return false;
                    int id = PlacementKey(placement);
                    used[id] = new[] { fa, fb }; points[id] = new[] { a, b };
                    PeriodicNativeQueries++;
                    float d;
                    if (!base.GetPathDistanceBetweenAIFaces(fa, fb, a, b, agentRadius, (float)Math.Min(limit, float.MaxValue), out d, excludedFaceIds, toSea, toLand)) return false;
                    cost = d;
                    return true;
                });
            PeriodicPruned += result.Pruned;
            if (!result.Found) return false;
            int key = PlacementKey(result.Placement);
            fromFace = used[key][0]; face = used[key][1]; from = points[key][0]; image = points[key][1]; distance = (float)result.Cost;
            if (result.Placement.StartShift != 0) PeriodicStartMoves++;
            if (result.Placement.StartShift != 0 || Math.Abs(result.Placement.TargetX - endingPosition.X) >= 1e-3) PeriodicImageWins++;
            return true;
        }

        private static int PlacementKey(PeriodicWorldCoordinates.Placement placement) { return placement.StartShift * 16 + placement.TargetShift; }

        private static PathFaceRecord ImageFace(Vec2 image, PathFaceRecord original)
        {
            foreach (bool land in new[] { true, false })
            {
                var face = new CampaignVec2(image, land).Face;
                if (face.IsValid() && face.FaceGroupIndex == original.FaceGroupIndex) return face;
            }
            return PathFaceRecord.NullFaceRecord;
        }

        // The periodic world, its patches and caches end with this map scene, so no later campaign inherits them.
        public new void Destroy()
        {
            try { base.Destroy(); }
            finally
            {
                // MMR-37: the map's world ends here: its generation is retired and the scene and agent renderer are handed to the installed
                // engine objects that release them (MapScreen.OnFinalize clears the scene). A failure here never skips the periodic cleanup.
                try
                {
                    if (world != null && world.Profile == "campaign map") { world.Dispose(); trace("generated_map_world_released:generation " + token.Id); }
                }
                finally
                {
                    WorldGenerations.CampaignMapDestroyed(token);
                    if (GeneratedColorGrade.End(this)) trace("colorgrade_patch_removed");
                    if (periodic != null && ReferenceEquals(PeriodicWorld.Current, periodic))
                    {
                        PeriodicPatches.Remove();
                        PeriodicWorld.Current = null;
                        trace("periodic_world_released");
                    }
                }
            }
        }

        public new void AfterLoad()
        {
            if (!loaded || afterLoaded) throw new InvalidOperationException("Invalid generated scene AfterLoad sequence.");
            base.AfterLoad(); // Installed MapScene implementation is empty.
            if (supportReport != null)
            {
                GeneratedMapSupport.RegisterNavigationCaches(this, worldId, supportReport, trace);
                // Needs the registered generated navmesh faces; replaces the null map Load left (no stock index imported).
                supportReport["battleSceneSelection"] = GeneratedBattleTerrain.Install(this, worldId);
                trace("generated_battle_terrain_installed:" + worldId);
            }
            afterLoaded = true;
            trace("diagnostic_scene_after_load_complete");
        }
    }
}
