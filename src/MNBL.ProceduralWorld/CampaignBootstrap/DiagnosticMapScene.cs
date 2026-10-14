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
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    public sealed class DiagnosticMapSceneCreator : IMapSceneCreator
    {
        public const string ModuleId = "MNBL.ProceduralWorld";
        public const string Slot = "mnbl_campaign_42";
        private readonly Action<string> trace;
        private readonly string sceneName;
        private readonly string worldId;
        private readonly Dictionary<string, object> supportReport;
        private readonly PeriodicWorld periodic;
        private readonly GeneratedClimate climate;
        public DiagnosticMapSceneCreator(Action<string> trace) : this(Slot, trace) { }
        public DiagnosticMapSceneCreator(string sceneName, Action<string> trace)
            : this(sceneName, trace, null, null) { }
        internal DiagnosticMapSceneCreator(string sceneName, Action<string> trace,
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
        public IMapScene CreateMapScene() { return new DiagnosticMapScene(sceneName, trace, worldId, supportReport, periodic, climate); }
    }

    // IMapScene reimplementation is deliberate: installed MapScene.Load is virtual final.
    // Inherited methods and SandBox/Naval castclass sites continue using populated base fields.
    public sealed class DiagnosticMapScene : MapScene, IMapScene
    {
        private readonly Action<string> trace;
        private readonly string sceneName;
        private readonly string worldId;
        private readonly Dictionary<string, object> supportReport;
        private bool loaded;
        private bool afterLoaded;
        private readonly PeriodicWorld periodic;
        private readonly GeneratedClimate climate;
        internal PeriodicWorld Periodic { get { return periodic; } }
        public DiagnosticMapScene(Action<string> trace) : this(DiagnosticMapSceneCreator.Slot, trace) { }
        public DiagnosticMapScene(string sceneName, Action<string> trace)
            : this(sceneName, trace, null, null) { }
        internal DiagnosticMapScene(string sceneName, Action<string> trace,
            string worldId, Dictionary<string, object> supportReport, PeriodicWorld periodic = null, GeneratedClimate climate = null)
        {
            this.periodic = periodic;
            this.climate = climate;
            if (trace == null) throw new ArgumentNullException("trace");
            DiagnosticMapSceneCreator.ValidateSceneName(sceneName);
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
            DiagnosticMapSceneCreator.ValidateFixture(sceneName);
            Utilities.PairSceneNameToModuleName(sceneName, DiagnosticMapSceneCreator.ModuleId);
            PeriodicWorld.Current = periodic;
            PeriodicSeam.Reset();
            PeriodicQueries = PeriodicImageWins = PeriodicNativeQueries = PeriodicPruned = 0;  // per world, not per process
            if (periodic != null)
            {
                trace("periodic_world:" + periodic.Width + "@" + periodic.OriginX);
                // Bannerlord.Harmony; removed again in Destroy. An exception escaping this engine callback ends the process
                // without a managed record, so a failed patch is traced and the world runs unpatched (the probe reports it).
                try { PeriodicPatches.Apply(); trace("periodic_patches:" + PeriodicPatches.Applied.Count); }
                catch (Exception error) { PeriodicPatches.LastError = error.ToString(); trace("periodic_patches_failed:" + error.ToString().Replace("\r", "").Replace("\n", " | ")); }
            }
            trace("diagnostic_scene_read_begin:" + sceneName);
            // SandBox.MapScene.Load IL_0016..02bb; only Read target and diagnostics differ.
            var scene = TaleWorlds.Engine.Scene.CreateNewScene(false, true, (DecalAtlasGroup)1, "MapScene");
            Set("_scene", scene);
            scene.SetClothSimulationState(true);
            var renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
            Set("_agentRendererSceneController", renderer);
            renderer.SetDoTimerBasedForcedSkeletonUpdates(false);
            scene.SetOcclusionMode(true);
            var init = new SceneInitializationData(true);
            init.UsePhysicsMaterials = false;
            init.EnableFloraPhysics = false;
            init.UseTerrainMeshBlending = false;
            init.CreateOros = false;
            scene.SetFetchCrcInfoOfScene(true);
            DisableUnwalkableNavigationMeshes();
            scene.SetNavMeshRegionMap(SandBoxHelpers.MapSceneHelper.GetRegionMapping(Campaign.Current.Models.PartyNavigationModel));
            scene.Read(sceneName, DiagnosticMapSceneCreator.ModuleId, ref init, "");
            if (!String.Equals(scene.GetName(), sceneName, StringComparison.Ordinal))
                throw new InvalidDataException("Loaded campaign scene identity differs from the requested owned scene.");
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
            loaded = true;
            trace("diagnostic_scene_loaded:" + scene.GetName());
        }

        // ---- MMR-63: periodic path and distance queries ----
        // The target's image (as given, east +Width, west -Width; inside the terrain) whose native path from the start
        // is shortest; ties within 1e-3 keep the earlier image. The strips copy the canonical faces' groups, so an image's
        // face is the land or naval face at the image with the target face's group.
        public static int PeriodicQueries, PeriodicImageWins, PeriodicNativeQueries, PeriodicPruned;

        public new bool GetPathBetweenAIFaces(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, NavigationPath path, int[] excludedFaceIds, float extraCostMultiplier, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            if (periodic != null)
            {
                PathFaceRecord face; Vec2 image; float distance;
                if (NearestTargetImage(startingFace, endingFace, startingPosition, endingPosition, agentRadius, Campaign.PathFindingMaxCostLimit, excludedFaceIds,
                        regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand, out face, out image, out distance))
                { endingFace = face; endingPosition = image; }
            }
            return base.GetPathBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, path, excludedFaceIds,
                extraCostMultiplier, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
        }

        public new bool GetPathDistanceBetweenAIFaces(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, out float distance, int[] excludedFaceIds, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            if (periodic == null)
                return base.GetPathDistanceBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, out distance,
                    excludedFaceIds, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
            PathFaceRecord face; Vec2 image;
            return NearestTargetImage(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, excludedFaceIds,
                regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand, out face, out image, out distance);
        }

        // The unmodified native query (no images), for evidence comparisons.
        internal bool PlanarPathDistance(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, out float distance, int[] excludedFaceIds, int regionSwitchCostFromLandToSea, int regionSwitchCostFromSeaToLand)
        {
            return base.GetPathDistanceBetweenAIFaces(startingFace, endingFace, startingPosition, endingPosition, agentRadius, distanceLimit, out distance,
                excludedFaceIds, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand);
        }

        internal bool NearestTargetImage(PathFaceRecord startingFace, PathFaceRecord endingFace, Vec2 startingPosition, Vec2 endingPosition,
            float agentRadius, float distanceLimit, int[] excludedFaceIds, int toSea, int toLand, out PathFaceRecord face, out Vec2 image, out float distance)
        {
            PeriodicQueries++;
            bool found = false; int chosen = -1;
            face = endingFace; image = endingPosition; distance = float.MaxValue;
            if (!startingFace.IsValid()) return false;
            // Exact pruning: a path is never shorter than the straight line to its end, so images are tried nearest-first
            // and an image whose straight-line distance cannot beat (or tie) the best path is never queried; the native
            // limit is tightened to the best so far. Ties within 1e-3 keep the earlier Images order (as given, east, west).
            var candidates = new List<KeyValuePair<int, Vec2>>();
            int order = 0;
            foreach (var candidate in periodic.Images(endingPosition)) candidates.Add(new KeyValuePair<int, Vec2>(order++, candidate));
            candidates.Sort((a, b) => { int c = startingPosition.DistanceSquared(a.Value).CompareTo(startingPosition.DistanceSquared(b.Value)); return c != 0 ? c : a.Key.CompareTo(b.Key); });
            foreach (var candidate in candidates)
            {
                float straight = startingPosition.Distance(candidate.Value);
                if (found && straight > distance + 1e-3f) { PeriodicPruned++; continue; }
                var candidateFace = candidate.Key == 0 ? endingFace : ImageFace(candidate.Value, endingFace);
                if (!candidateFace.IsValid()) continue;
                float d;
                float limit = found ? Math.Min(distanceLimit, distance + 1e-3f) : distanceLimit;
                PeriodicNativeQueries++;
                if (!base.GetPathDistanceBetweenAIFaces(startingFace, candidateFace, startingPosition, candidate.Value, agentRadius, limit, out d,
                        excludedFaceIds, toSea, toLand)) continue;
                bool better = !found || d < distance - 1e-3f || (Math.Abs(d - distance) <= 1e-3f && candidate.Key < chosen);
                if (better) { found = true; distance = d; face = candidateFace; image = candidate.Value; chosen = candidate.Key; }
            }
            if (chosen > 0) PeriodicImageWins++;
            return found;
        }

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
                if (periodic != null && ReferenceEquals(PeriodicWorld.Current, periodic))
                {
                    PeriodicPatches.Remove();
                    PeriodicWorld.Current = null;
                    trace("periodic_world_released");
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
