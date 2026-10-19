using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map.DistanceCache;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR18 diagnostic only. Call synchronously on the campaign loading thread.
    // No texture readback, native snow/wind import, file caches, or persistence.
    // MMR-59: a placement with generated climate fields supplies them instead of the dry/calm fixture.
    public static class GeneratedMapSupport
    {
        private const int WeatherDimension = 32;
        private const int WindDimension = 512;
        private const string NavalDistanceType = "NavalDLC.GameComponents.NavalDLCMapDistanceModel";
        private const string NavalNavigationType = "NavalDLC.GameComponents.NavalPartyNavigationModel";
        private sealed class State
        {
            internal bool WeatherAttempted, WeatherPrepared, NavigationAttempted;
            internal int ThreadId;
            internal float Extent;
            internal string SceneName;
        }
        // Weak scene keys: no static retention of a previous campaign/map/cache.
        private static readonly ConditionalWeakTable<GeneratedMapScene, State> States =
            new ConditionalWeakTable<GeneratedMapScene, State>();

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Generated map support: " + message);
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static FieldInfo Field(Type owner, string name, Type fieldType)
        {
            var f = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Require(f != null && f.IsPrivate && !f.IsStatic && f.FieldType == fieldType,
                "installed field contract changed: " + owner.FullName + "." + name);
            return f;
        }
        private static FieldInfo MapField(string name, Type type) { return Field(typeof(MapScene), name, type); }
        private static string OwnedScene(GeneratedMapScene map)
        {
            Require(map != null, "map is missing");
            Require(Campaign.Current != null && Object.ReferenceEquals(Campaign.Current.MapSceneWrapper, map),
                "map is not the active campaign wrapper");
            Require(map.Scene != null, "scene has not been read");
            string name = map.Scene.GetName();
            GeneratedMapSceneCreator.ValidateSceneName(name);
            // A fixture slot (mnbl_generated_*) or a compiled world's content-addressed scene (MMR-45's activation).
            Require(name.StartsWith("mnbl_generated_", StringComparison.Ordinal) || SceneNames.IsWorldScene(name), "not a generated bootstrap or world scene");
            string module = (map.Scene.GetModulePath() ?? "").Replace('\\', '/').TrimEnd('/');
            string expected = System.IO.Path.GetFullPath(ModuleHelper.GetModuleFullPath(GeneratedMapSceneCreator.ModuleId))
                .Replace('\\', '/').TrimEnd('/');
            Require(String.Equals(module, "$BASE/Modules/" + GeneratedMapSceneCreator.ModuleId, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(module, expected, StringComparison.OrdinalIgnoreCase), "scene is not in the owned module: " + module);
            return name;
        }

        public static void PrepareWeather(GeneratedMapScene map, float extent, GeneratedClimate climate,
            Dictionary<string, object> report, Action<string> trace)
        {
            if (report == null || trace == null) throw new ArgumentNullException("report/trace");
            string name = OwnedScene(map);
            Require(Finite(extent) && extent > 0, "invalid generated extent");
            var state = States.GetValue(map, ignored => new State());
            Require(!state.WeatherAttempted, "weather setup already attempted for this scene; do not retry partial native setup");
            state.WeatherAttempted = true;
            state.ThreadId = Thread.CurrentThread.ManagedThreadId;
            state.Extent = extent;
            state.SceneName = name;
            var row = new Dictionary<string, object> {
                ["status"] = "preparing", ["scene"] = name, ["extent"] = extent,
                ["threadId"] = state.ThreadId,
                ["policy"] = climate == null ? "MMR18 owned uniform dry/calm bootstrap fixture" : "MMR-59 generated climate pattern " + climate.Pattern,
                ["privateFieldDiagnostic"] = true, ["weatherDimension"] = WeatherDimension,
                ["windDimension"] = WindDimension, ["nativeRainTextureCreated"] = false,
                ["nativeSnowTextureSupported"] = false, ["nativeWakeSupported"] = false,
                ["productionWeatherSupported"] = false, ["stockGeographyImported"] = false,
                ["textureReadbackInvoked"] = false
            };
            report["generatedWeather"] = row;
            var watch = Stopwatch.StartNew();
            try
            {
                var width = MapField("_snowAndRainDataTextureWidth", typeof(int));
                var height = MapField("_snowAndRainDataTextureHeight", typeof(int));
                var precipitation = MapField("_snowAndRainData", typeof(byte[]));
                var wind = MapField("_windFlowMapData", typeof(float[]));
                var terrain = (Vec2)MapField("_terrainSize", typeof(Vec2)).GetValue(map);
                Require(Finite(terrain.X) && Finite(terrain.Y) && Math.Abs(terrain.X - extent) <= .001f &&
                        Math.Abs(terrain.Y - extent) <= .001f, "terrain size not initialized to the generated extent");
                // Each invocation owns distinct buffers; zero snow/rain and zero wind
                // are deliberate fixture values, not geographic or climate inference.
                if (climate != null) Require(Math.Abs(climate.Extent - extent) <= .001f, "generated climate extent differs from the terrain");
                var dry = climate != null ? climate.CopyPrecipitation() : new byte[WeatherDimension * WeatherDimension * 2];
                var calm = climate != null ? climate.CopyWind() : new float[WindDimension * WindDimension * 2];
                Require(dry.Length == WeatherDimension * WeatherDimension * 2 && calm.Length == WindDimension * WindDimension * 2, "climate buffer layout");
                width.SetValue(map, WeatherDimension); height.SetValue(map, WeatherDimension);
                precipitation.SetValue(map, dry); wind.SetValue(map, calm);
                Campaign.Current.DefaultWeatherNodeDimension = WeatherDimension;
                row["snowRainBytes"] = dry.Length; row["windFloats"] = calm.Length;
                if (climate != null) { row["climatePattern"] = climate.Pattern; row["windSha256"] = climate.WindSha256; row["precipitationSha256"] = climate.PrecipitationSha256; }
                // MMR-39: managers whose Init ran before the colour-grade patch get the generated grid here (the postfix covers later ones).
                row["colorGrade"] = !GeneratedColorGrade.Applied ? "unpatched" : GeneratedColorGrade.Neutral ? "neutral" : "generated";
                row["colorGradeSha256"] = GeneratedColorGrade.GridSha256; row["colorGradeManagers"] = GeneratedColorGrade.Managers(map.Scene).Count;
                row["colorGradeReplacedAtPrepare"] = GeneratedColorGrade.ReplaceInScene(map.Scene);
                trace("generated_weather_create_rain_texture_begin:" + name);
                // Exact public call made by installed MapScene weather initialization.
                // Do NOT use Texture.CreateFromByteArray/GetPixelData or import a flow map.
                map.Scene.CreateDynamicRainTexture(WeatherDimension, WeatherDimension);
                row["nativeRainTextureCreated"] = true;
                state.WeatherPrepared = true;
                row["status"] = "prepared";
                trace("generated_weather_prepared:" + name);
            }
            catch (Exception error)
            {
                row["status"] = "failed"; row["error"] = error.ToString();
                throw;
            }
            finally { row["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds; }
        }

        public static void RegisterNavigationCaches(GeneratedMapScene map, string worldId,
            Dictionary<string, object> report, Action<string> trace)
        {
            if (report == null || trace == null) throw new ArgumentNullException("report/trace");
            string name = OwnedScene(map);
            GeneratedMapSceneCreator.ValidateSceneName(worldId);
            // A fixture slot (mnbl_generated_<seed>) carries the MMR-33 fixture contract below; a compiled world's scene (MMR-45) does not.
            bool fixture = !SceneNames.IsWorldScene(name);
            Require(!fixture || name == "mnbl_generated_" + worldId.Substring("mnbl_".Length), "world/scene identity mismatch");
            State state;
            Require(States.TryGetValue(map, out state) && state.WeatherPrepared, "weather must be prepared before AfterLoad");
            Require(state.ThreadId == Thread.CurrentThread.ManagedThreadId && state.SceneName == name,
                "loading thread or scene changed between setup phases");
            Require(!state.NavigationAttempted, "navigation setup already attempted; partial registration is not retryable");
            state.NavigationAttempted = true;
            var rows = new List<Dictionary<string, object>>();
            var row = new Dictionary<string, object> {
                ["status"] = "validating", ["worldId"] = worldId, ["scene"] = name,
                ["threadId"] = state.ThreadId, ["caches"] = rows,
                ["serialized"] = false, ["deserialized"] = false, ["officialCachesRead"] = false,
                ["scope"] = "The world's caches for its generated settlements (MMR-41: reused from their published copy under the same key, else generated and published); native queries run synchronously."
            };
            report["generatedNavigationCaches"] = row;
            var watch = Stopwatch.StartNew();
            try
            {
                var campaign = Campaign.Current;
                var distance = campaign.Models.MapDistanceModel;
                var navigation = campaign.Models.PartyNavigationModel;
                Require(distance != null && distance.GetType().FullName == NavalDistanceType &&
                        distance.GetType().Assembly.GetName().Name == "NavalDLC", "final Naval distance model required");
                Require(navigation != null && navigation.GetType().FullName == NavalNavigationType &&
                        navigation.GetType().Assembly.GetName().Name == "NavalDLC", "final Naval party navigation model required");
                row["distanceModel"] = distance.GetType().AssemblyQualifiedName;
                row["navigationModel"] = navigation.GetType().AssemblyQualifiedName;
                row["pathFindingMaxCostLimit"] = Campaign.PathFindingMaxCostLimit;
                Require(Campaign.PathFindingMaxCostLimit > 0, "campaign path-cost limit not initialized");
                var minimum = Campaign.MapMinimumPosition;
                var maximum = Campaign.MapMaximumPosition;
                Require(Math.Abs(minimum.X) <= .001f && Math.Abs(minimum.Y) <= .001f &&
                        Math.Abs(maximum.X-state.Extent) <= .001f && Math.Abs(maximum.Y-state.Extent) <= .001f,
                        "campaign bounds not assigned for this generated extent");
                row["minimum"] = new[] { minimum.X, minimum.Y };
                row["maximum"] = new[] { maximum.X, maximum.Y };
                var cacheField = Field(distance.GetType(), "_navigationCaches",
                    typeof(Dictionary<MobileParty.NavigationType, MapDistanceModel.INavigationCache>));
                var cacheDictionary = (IDictionary)cacheField.GetValue(distance);
                Require(cacheDictionary != null && cacheDictionary.Count == 0, "distance model already has caches; refusing to replace retained world state");
                // The retirement retreat (an MMR-34 world alias) is checked by the startup audit, not as a placed settlement.
                var settlements = Settlement.All.Where(s => s.StringId != "retirement_retreat").OrderBy(s => s.StringId, StringComparer.Ordinal).ToArray();
                // The four generated settlements, plus (MMR-33 settlement fixtures, all three or none) the first world's castle, its
                // village and a hideout.
                var expectedIds = new List<string> { worldId+"_town_1", worldId+"_town_2", worldId+"_village_1", worldId+"_village_2" };
                bool fixtures = settlements.Any(s => s.StringId == worldId+"_castle_1");
                if (fixtures) expectedIds.AddRange(new[] { worldId+"_castle_1", worldId+"_village_3", worldId+"_hideout_1" });
                // MMR-34 startup fixtures: a hideout of each other bandit culture.
                if (settlements.Any(s => s.StringId == worldId+"_hideout_2")) expectedIds.AddRange(new[] { worldId+"_hideout_2", worldId+"_hideout_3", worldId+"_hideout_4", worldId+"_hideout_5" });
                string[] expected = expectedIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Require(!fixture || settlements.Select(s => s.StringId).SequenceEqual(expected),
                        "Settlement.All must contain exactly the expected generated IDs (four, or seven with the settlement fixtures)");
                var placementRows = new List<Dictionary<string, object>>();
                row["settlements"] = placementRows;
                for (int i=0; i<settlements.Length; ++i)
                {
                    var s = settlements[i];
                    string id = s.StringId.StartsWith(worldId + "_", StringComparison.Ordinal) ? s.StringId.Substring(worldId.Length + 1) : s.StringId;
                    string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsHideout ? "hideout" : "village";
                    bool town = s.IsTown;
                    if (fixture)
                    {
                        if (kind == "town") Require(id.StartsWith("town_", StringComparison.Ordinal) && s.HasPort, "settlement type/port contract changed: " + s.StringId);
                        else if (kind == "castle") Require(id.StartsWith("castle_", StringComparison.Ordinal) && !s.HasPort, "castle fixture contract changed: " + s.StringId);
                        else if (kind == "hideout") Require(id.StartsWith("hideout_", StringComparison.Ordinal) && !s.HasPort, "hideout fixture contract changed: " + s.StringId);
                        else
                        {
                            // MMR-42: a harbour village (its scene places a main_map_village_dropoff for it, MMR-40) is built with its port.
                            Require(id.StartsWith("village_", StringComparison.Ordinal) && (!s.HasPort || HasDropOff(map, s.StringId)), "settlement type/port contract changed: " + s.StringId);
                            string bound = id == "village_3" ? worldId+"_castle_1" : worldId+"_town_"+id.Substring("village_".Length);
                            Require(s.Village.Bound != null && s.Village.Bound.StringId == bound, "village binding mismatch");
                        }
                    }
                    ValidatePoint(s.Position, state.Extent, "settlement " + s.StringId);
                    ValidatePoint(s.GatePosition, state.Extent, "gate " + s.StringId);
                    var placement = new Dictionary<string, object> {
                        ["id"] = s.StringId, ["kind"] = kind,
                        ["position"] = new[] { s.Position.X, s.Position.Y },
                        ["gate"] = new[] { s.GatePosition.X, s.GatePosition.Y }, ["hasPort"] = s.HasPort
                    };
                    placementRows.Add(placement);
                    if (town && s.HasPort)
                    {
                        ValidatePoint(s.PortPosition, state.Extent, "port " + s.StringId);
                        Require(s.PortPosition.X != s.GatePosition.X || s.PortPosition.Y != s.GatePosition.Y, "port equals land gate");
                        placement["port"] = new[] { s.PortPosition.X, s.PortPosition.Y };
                    }
                }
                // MMR-41: the world's caches are read back from their published copy when the key matches, else generated and published
                // (WorldNavigationCache). options: the probe's ("navigationCacheOptions": verify, fault).
                // War Sails sets harbour villages' ports from the scene's drop-off entities when its map scene wrapper starts, after AfterLoad
                // (NavalMapSceneWrapper.InitializeDropOffLocations: each main_map_village_dropoff entity's parent names its village; the port is
                // the entity's global position, at sea). The same ports are set here first, so the caches and their key hold them.
                row["dropOffPorts"] = ApplyDropOffPorts(map);
                object options; report.TryGetValue("navigationCacheOptions", out options);
                var cache = WorldNavigationCache.Acquire(map, name, worldId, distance, cacheDictionary, trace, options as IDictionary<string, object>);
                row["navigationCache"] = cache;
                foreach (var step in (List<Dictionary<string, object>>)cache["caches"]) rows.Add(step);
                row["serialized"] = cache.ContainsKey("published"); row["deserialized"] = (string)cache["status"] == "reused";
                Require(cacheDictionary.Count == 3, "unexpected cache count after registration");
                row["status"] = "registered";
            }
            catch (Exception error)
            {
                row["status"] = "failed"; row["error"] = error.ToString();
                row["partialRegistrationMayRemain"] = rows.Count != 0;
                throw;
            }
            finally { row["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds; }
        }

        // Whether the scene places a drop-off entity for this village (parent named by the village, else the entity named <village>_dropoff).
        private static bool HasDropOff(GeneratedMapScene map, string village)
        {
            foreach (var entity in map.Scene.FindEntitiesWithTag("main_map_village_dropoff"))
            {
                var parent = entity.Parent;
                if ((parent != null && parent.Name == village) || entity.Name == village + "_dropoff") return true;
            }
            return false;
        }

        private static List<Dictionary<string, object>> ApplyDropOffPorts(GeneratedMapScene map)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var entity in map.Scene.FindEntitiesWithTag("main_map_village_dropoff"))
            {
                // War Sails matches the entity's parent name against Village.All, which is still empty at AfterLoad (runs 17060, 17064); the same
                // village is the settlement of that name (Settlement.All is populated), else the factory's child name (<village id>_dropoff). The
                // campaign-start key check confirms the result.
                var parent = entity.Parent;
                string parentName = parent == null ? null : parent.Name;
                Func<string, Settlement> village = id => { var s = id == null ? null : Settlement.Find(id); return s != null && s.IsVillage ? s : null; };
                var settlement = village(parentName);
                string matchedBy = settlement != null ? "parent" : null;
                if (settlement == null && entity.Name != null && entity.Name.EndsWith("_dropoff", StringComparison.Ordinal))
                {
                    settlement = village(entity.Name.Substring(0, entity.Name.Length - "_dropoff".Length));
                    if (settlement != null) matchedBy = "entity name";
                }
                var row = new Dictionary<string, object> { ["entity"] = entity.Name, ["parent"] = parentName, ["village"] = settlement == null ? null : settlement.StringId, ["matchedBy"] = matchedBy };
                rows.Add(row);
                if (settlement == null) continue;
                var at = entity.GlobalPosition.AsVec2;
                settlement.SetPortPosition(new CampaignVec2(at, false));
                row["port"] = new[] { at.X, at.Y };
            }
            return rows;
        }

        private static void ValidatePoint(CampaignVec2 point, float extent, string label)
        {
            Require(Finite(point.X) && Finite(point.Y) && point.X > 0 && point.Y > 0 &&
                    point.X < extent && point.Y < extent, label + " outside generated bounds");
        }
    }
}
