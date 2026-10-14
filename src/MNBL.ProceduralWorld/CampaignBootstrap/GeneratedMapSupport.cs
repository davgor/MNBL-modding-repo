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
        private static readonly ConditionalWeakTable<DiagnosticMapScene, State> States =
            new ConditionalWeakTable<DiagnosticMapScene, State>();

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
        private static string OwnedScene(DiagnosticMapScene map)
        {
            Require(map != null, "map is missing");
            Require(Campaign.Current != null && Object.ReferenceEquals(Campaign.Current.MapSceneWrapper, map),
                "map is not the active campaign wrapper");
            Require(map.Scene != null, "scene has not been read");
            string name = map.Scene.GetName();
            DiagnosticMapSceneCreator.ValidateSceneName(name);
            Require(name.StartsWith("mnbl_generated_", StringComparison.Ordinal), "not a generated bootstrap scene");
            string module = (map.Scene.GetModulePath() ?? "").Replace('\\', '/').TrimEnd('/');
            string expected = System.IO.Path.GetFullPath(ModuleHelper.GetModuleFullPath(DiagnosticMapSceneCreator.ModuleId))
                .Replace('\\', '/').TrimEnd('/');
            Require(String.Equals(module, "$BASE/Modules/" + DiagnosticMapSceneCreator.ModuleId, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(module, expected, StringComparison.OrdinalIgnoreCase), "scene is not in the owned module: " + module);
            return name;
        }

        public static void PrepareWeather(DiagnosticMapScene map, float extent, GeneratedClimate climate,
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

        public static void RegisterNavigationCaches(DiagnosticMapScene map, string worldId,
            Dictionary<string, object> report, Action<string> trace)
        {
            if (report == null || trace == null) throw new ArgumentNullException("report/trace");
            string name = OwnedScene(map);
            DiagnosticMapSceneCreator.ValidateSceneName(worldId);
            Require(name == "mnbl_generated_" + worldId.Substring("mnbl_".Length), "world/scene identity mismatch");
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
                ["scope"] = "Fresh caches for four generated settlements; native queries run synchronously. Not generated-route gameplay acceptance."
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
                var settlements = Settlement.All.OrderBy(s => s.StringId, StringComparer.Ordinal).ToArray();
                string[] expected = { worldId+"_town_1", worldId+"_town_2", worldId+"_village_1", worldId+"_village_2" };
                Require(settlements.Length == 4 && settlements.Select(s => s.StringId).SequenceEqual(expected),
                        "Settlement.All must contain exactly the four expected generated IDs");
                var placementRows = new List<Dictionary<string, object>>();
                row["settlements"] = placementRows;
                for (int i=0; i<settlements.Length; ++i)
                {
                    var s = settlements[i]; bool town = i < 2;
                    Require(town ? s.IsTown && s.HasPort : s.IsVillage && !s.HasPort, "settlement type/port contract changed: " + s.StringId);
                    if (!town) Require(s.Village.Bound != null && s.Village.Bound.StringId == worldId+"_town_"+(i-1), "village binding mismatch");
                    ValidatePoint(s.Position, state.Extent, "settlement " + s.StringId);
                    ValidatePoint(s.GatePosition, state.Extent, "gate " + s.StringId);
                    var placement = new Dictionary<string, object> {
                        ["id"] = s.StringId, ["kind"] = town ? "town" : "village",
                        ["position"] = new[] { s.Position.X, s.Position.Y },
                        ["gate"] = new[] { s.GatePosition.X, s.GatePosition.Y }, ["hasPort"] = s.HasPort
                    };
                    placementRows.Add(placement);
                    if (town)
                    {
                        ValidatePoint(s.PortPosition, state.Extent, "port " + s.StringId);
                        Require(s.PortPosition.X != s.GatePosition.X || s.PortPosition.Y != s.GatePosition.Y, "port equals land gate");
                        placement["port"] = new[] { s.PortPosition.X, s.PortPosition.Y };
                    }
                }
                foreach (var type in new[] { MobileParty.NavigationType.Default, MobileParty.NavigationType.Naval, MobileParty.NavigationType.All })
                {
                    var step = new Dictionary<string, object> { ["navigationType"] = type.ToString(), ["status"] = "constructing" };
                    rows.Add(step);
                    trace("generated_navigation_construct:" + worldId + ":" + type);
                    var cache = new SandBoxNavigationCache(type);
                    var timer = Stopwatch.StartNew();
                    step["status"] = "generating";
                    trace("generated_navigation_generate_begin:" + worldId + ":" + type);
                    cache.GenerateCacheData();
                    step["generationMilliseconds"] = timer.Elapsed.TotalMilliseconds;
                    step["status"] = "registering";
                    trace("generated_navigation_register_begin:" + worldId + ":" + type);
                    timer.Restart();
                    distance.RegisterDistanceCache(type, cache); // includes FinalizeInitialization
                    step["registrationMilliseconds"] = timer.Elapsed.TotalMilliseconds;
                    Require(Object.ReferenceEquals(cacheDictionary[type], cache), "registered cache identity mismatch");
                    step["status"] = "registered";
                    trace("generated_navigation_registered:" + worldId + ":" + type);
                }
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

        private static void ValidatePoint(CampaignVec2 point, float extent, string label)
        {
            Require(Finite(point.X) && Finite(point.Y) && point.X > 0 && point.Y > 0 &&
                    point.X < extent && point.Y < extent, label + " outside generated bounds");
        }
    }
}
