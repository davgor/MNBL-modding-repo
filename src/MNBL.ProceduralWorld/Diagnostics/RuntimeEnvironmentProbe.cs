using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-59: can generated wind/flow and climate data be bound to, and read by, the installed native and War Sails
    // consumers, and be replaced on a world switch? Runs once per generated world (environmentProbe=1) on fixtures from
    // tools/scenebin/build_climate_world.py, whose placement declares a generated climate pattern. Every section is
    // fail-soft: it records its failures and the probe continues, so one run reports everything.
    internal static class RuntimeEnvironmentProbe
    {
        // World-switch evidence from the previous world: weak references and values only, never the objects themselves.
        private static WeakReference previousMap, previousWind, previousPrecipitation, previousCampaign, previousNaval, previousStormManager, previousStorm, pendingStorm,
            previousClimate, previousBootstrap;
        private static string previousWorld, previousWindSha, previousPrecipitationSha;
        private static List<float[]> previousCandidates;

        private static readonly FieldInfo WindField = MapField("_windFlowMapData"), PrecipitationField = MapField("_snowAndRainData"),
            WidthField = MapField("_snowAndRainDataTextureWidth"), HeightField = MapField("_snowAndRainDataTextureHeight");

        private static FieldInfo MapField(string name)
        {
            var f = typeof(MapScene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(typeof(MapScene).FullName, name);
            return f;
        }

        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, string worldId, int worldIndex,
            GeneratedClimate climate, Dictionary<string, object> row)
        {
            var failures = new List<string>(); row["failures"] = failures;
            row["worldId"] = worldId; row["worldIndex"] = worldIndex;
            if (worldIndex == 0)
            {
                previousMap = previousWind = previousPrecipitation = previousCampaign = previousNaval = previousStormManager = previousStorm = pendingStorm = null;
                previousClimate = previousBootstrap = null; previousCandidates = null;
            }
            var campaign = Campaign.Current;
            var map = campaign.MapSceneWrapper as DiagnosticMapScene;
            if (climate == null || map == null) { failures.Add("The world has no generated climate (placement 'climate') or no owned map scene."); yield break; }
            row["pattern"] = climate.Pattern; row["windVector"] = new[] { climate.WindVector.X, climate.WindVector.Y };
            var scene = (IMapScene)map;
            float extent = climate.Extent;
            var town1 = Settlement.Find(worldId + "_town_1"); var town2 = Settlement.Find(worldId + "_town_2");
            Vec2 port1 = town1.PortPosition.ToVec2(), port2 = town2.PortPosition.ToVec2();
            var weather = campaign.Models.MapWeatherModel;

            Section(row, failures, "contract", r => Contract(r, map, climate, extent));
            Section(row, failures, "wind", r => Wind(r, map, weather, climate, new[] { port1, port2, (port1 + port2) * .5f, town1.GatePosition.ToVec2(),
                new Vec2(extent * .25f, extent * .5f), new Vec2(extent * .75f, extent * .5f) }));
            Section(row, failures, "precipitation", r => Precipitation(r, scene, weather, climate, extent));
            Section(row, failures, "stormArea", r => StormArea(r, scene, map, extent));
            // Before time runs (no natural storms yet) and before this world spawns its own storm.
            Section(row, failures, "worldSwitch", r => Switch(r, map, campaign, climate, extent, worldId));

            // Movement effect: the player's ship sails port 1 -> port 2 and back; the wind boost is read from the War Sails
            // speed model's own CalculateWindBoostForParty, and the speed is measured.
            var sailing = new Dictionary<string, object>(); row["sailing"] = sailing;
            var steps = Sailing(context, boot, campaign, town1, town2, sailing);
            Exception sailError = null;
            while (true)
            {
                try { if (!steps.MoveNext()) break; } catch (Exception e) { sailError = e; break; }
                yield return steps.Current;
            }
            if (sailError != null) sailing["error"] = sailError.ToString();
            Judge(sailing, failures, "sailing", climate.Pattern);

            Section(row, failures, "storm", r => SpawnStorm(r, weather, climate, extent));
            if (previousMap != null && worldIndex > 0)
            {
                // Give the engine a few frames, then collect: a retained previous-world object survives three full collections.
                for (int round = 0; round < 3; round++)
                {
                    for (int frame = 0; frame < 10; frame++) { boot.Tick(); yield return null; }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                }
                Section(row, failures, "previousWorldReleased", r => Released(r));
            }
            try { Remember(map, campaign, climate, worldId, row, boot); }
            catch (Exception e) { row["rememberError"] = e.ToString(); failures.Add("remember: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Section(Dictionary<string, object> row, List<string> failures, string name, Func<Dictionary<string, object>, IEnumerable<string>> body)
        {
            var section = new Dictionary<string, object>(); row[name] = section;
            try { foreach (var f in body(section)) failures.Add(name + ": " + f); }
            catch (Exception e) { section["error"] = e.ToString(); failures.Add(name + ": " + e.GetType().Name + ": " + e.Message); }
        }

        private static IEnumerable<string> Contract(Dictionary<string, object> r, DiagnosticMapScene map, GeneratedClimate climate, float extent)
        {
            var wind = (float[])WindField.GetValue(map); var precipitation = (byte[])PrecipitationField.GetValue(map);
            int width = (int)WidthField.GetValue(map), height = (int)HeightField.GetValue(map);
            string windSha = GeneratedClimate.Sha256(wind), precipitationSha = GeneratedClimate.Sha256(precipitation);
            r["windFloats"] = wind.Length; r["precipitationBytes"] = precipitation.Length; r["precipitationWidth"] = width; r["precipitationHeight"] = height;
            r["sceneWindSha256"] = windSha; r["scenePrecipitationSha256"] = precipitationSha;
            r["generatedWindSha256"] = climate.WindSha256; r["generatedPrecipitationSha256"] = climate.PrecipitationSha256;
            r["weatherNodeDimension"] = Campaign.Current.DefaultWeatherNodeDimension;
            r["terrainSize"] = new[] { ((IMapScene)map).GetTerrainSize().X, ((IMapScene)map).GetTerrainSize().Y };
            r["bounds"] = new[] { Campaign.MapMinimumPosition.X, Campaign.MapMinimumPosition.Y, Campaign.MapMaximumPosition.X, Campaign.MapMaximumPosition.Y };
            r["weatherModel"] = Campaign.Current.Models.MapWeatherModel.GetType().FullName;
            r["binding"] = "SandBox.MapScene private fields written in DiagnosticMapScene.Load before MapWeatherModel.InitializeCaches (GeneratedMapSupport.PrepareWeather)";
            if (windSha != climate.WindSha256) yield return "The scene's wind field is not the generated field.";
            if (precipitationSha != climate.PrecipitationSha256) yield return "The scene's snow/rain field is not the generated field.";
            if (width != GeneratedClimate.WeatherDimension || height != GeneratedClimate.WeatherDimension) yield return "Snow/rain dimensions differ.";
            if (Campaign.Current.DefaultWeatherNodeDimension != GeneratedClimate.WeatherDimension) yield return "Weather node dimension was not set.";
            if (Math.Abs(Campaign.MapMaximumPosition.X - extent) > .01f || Math.Abs(Campaign.MapMaximumPosition.Y - extent) > .01f) yield return "Campaign bounds are not this world's extent.";
        }

        private static IEnumerable<string> Wind(Dictionary<string, object> r, DiagnosticMapScene map, MapWeatherModel weather, GeneratedClimate climate, Vec2[] points)
        {
            var samples = new List<object>(); r["samples"] = samples;
            var problems = new List<string>();
            foreach (var p in points)
            {
                Vec2 raw = map.GetWindAtPosition(p), expected = climate.WindAt(p), model = weather.GetWindForPosition(new CampaignVec2(p, false));
                // War Sails clamps the length to [0.0667, 0.46] outside storms (NavalDLCMapWeatherModel.GetWindForPosition IL_0028).
                float expectedLength = MathF.Clamp(expected.Length, 0.0667f, 0.46f);
                float cos = expected.Length > 0 && model.Length > 0 ? Vec2.DotProduct(model.Normalized(), expected.Normalized()) : 0;
                samples.Add(new Dictionary<string, object> { ["at"] = new[] { p.X, p.Y }, ["raw"] = new[] { raw.X, raw.Y }, ["generated"] = new[] { expected.X, expected.Y },
                    ["model"] = new[] { model.X, model.Y }, ["modelDirectionCos"] = cos, ["expectedModelLength"] = expectedLength });
                if (raw.X != expected.X || raw.Y != expected.Y) problems.Add("raw wind at " + p + " is " + raw + ", generated " + expected);
                if (cos < 0.999f || Math.Abs(model.Length - expectedLength) > 1e-3f) problems.Add("model wind at " + p + " is " + model + ", expected direction of " + expected + " with length " + expectedLength);
            }
            return problems;
        }

        private static IEnumerable<string> Precipitation(Dictionary<string, object> r, IMapScene scene, MapWeatherModel weather, GeneratedClimate climate, float extent)
        {
            var problems = new List<string>();
            float snowFactor, rainFactor;
            weather.GetSeasonTimeFactorOfCampaignTime(CampaignTime.Now, out snowFactor, out rainFactor, true);
            r["season"] = CampaignTime.Now.GetSeasonOfYear.ToString(); r["seasonSnowFactor"] = snowFactor; r["seasonRainFactor"] = rainFactor;
            r["seasonNote"] = "season is global and time-only (MapWeatherModel.GetSeasonTimeFactorOfCampaignTime); position-dependent climate comes from the snow/rain field";
            var samples = new List<object>(); r["samples"] = samples;
            float cell = extent / GeneratedClimate.WeatherDimension;
            foreach (var named in new[] { Tuple.Create("west", 8, 16), Tuple.Create("east", 24, 16), Tuple.Create("north", 16, 24), Tuple.Create("south", 16, 8) })
            {
                var p = new Vec2((named.Item2 + .5f) * cell, (named.Item3 + .5f) * cell);
                float rawSnow = scene.GetSnowAmountAtPosition(p), rawRain = scene.GetRainAmountAtPosition(p);
                int snow = climate.SnowAt(p), rain = climate.RainAt(p);
                float scaledSnow, scaledRain;
                weather.GetSnowAndRainDataForPosition(p, CampaignTime.Now, out scaledSnow, out scaledRain);
                var weatherEvent = weather.GetWeatherEventInPosition(p);
                samples.Add(new Dictionary<string, object> { ["name"] = named.Item1, ["at"] = new[] { p.X, p.Y }, ["terrain"] = scene.GetTerrainTypeAtPosition(new CampaignVec2(p, false)).ToString(),
                    ["rawSnow"] = rawSnow, ["rawRain"] = rawRain,
                    ["generatedSnow"] = snow, ["generatedRain"] = rain, ["seasonScaledSnow"] = scaledSnow, ["seasonScaledRain"] = scaledRain, ["weatherEvent"] = weatherEvent.ToString() });
                if (rawSnow != snow || rawRain != rain) problems.Add(named.Item1 + ": raw snow/rain " + rawSnow + "/" + rawRain + ", generated " + snow + "/" + rain);
            }
            // DefaultMapWeatherModel.GetSnowAndRainDataForPosition (IL_0056-00cf) maps raw/255 through a SmoothStep whose lower edge
            // can be negative, so a dry cell may score above 0. It is non-decreasing in the raw value and, for raw 230 against 0,
            // strictly larger in every season (both SmoothStep edge ranges checked), so the wet half must score higher.
            var byName = samples.Cast<Dictionary<string, object>>().ToDictionary(s => (string)s["name"]);
            if (climate.Pattern == "along" && !((float)byName["west"]["seasonScaledRain"] > (float)byName["east"]["seasonScaledRain"]))
                problems.Add("season-scaled rain is not higher in the generated wet west than in the dry east");
            if (climate.Pattern == "across" && !((float)byName["north"]["seasonScaledSnow"] > (float)byName["south"]["seasonScaledSnow"]))
                problems.Add("season-scaled snow is not higher in the generated snowy north than in the dry south");
            return problems;
        }

        // The open-sea storm spawn area: NavalDLC keeps the weather-node positions that lie on OpenSea faces.
        private static IEnumerable<string> StormArea(Dictionary<string, object> r, IMapScene scene, DiagnosticMapScene map, float extent)
        {
            var problems = new List<string>();
            var behaviorType = HarmonyLib.AccessTools.TypeByName("NavalDLC.CampaignBehaviors.StormCampaignBehavior");
            object behavior = CampaignBehavior(behaviorType);
            if (behavior == null) { problems.Add("No StormCampaignBehavior."); return problems; }
            var candidates = ((IEnumerable)HarmonyLib.AccessTools.Field(behaviorType, "_allOpenSeaWeatherNodePositions").GetValue(behavior)).Cast<Vec2>().ToList();
            int dim = Campaign.Current.DefaultWeatherNodeDimension; var size = scene.GetTerrainSize();
            int recount = 0, offOpenSea = 0, outside = 0;
            for (int i = 0; i < dim; i++)
                for (int j = 0; j < dim; j++)
                {
                    var p = new Vec2((float)i / dim * size.X, (float)j / dim * size.Y);
                    var face = scene.GetFaceIndex(new CampaignVec2(p, false));  // the same calls as InitializeStormNodes IL_003a-00a8
                    if (face.IsValid() && scene.GetTerrainTypeAtPosition(new CampaignVec2(p, false)) == TerrainType.OpenSea) recount++;
                }
            foreach (var p in candidates)
            {
                var face = scene.GetFaceIndex(new CampaignVec2(p, false));
                if (!face.IsValid() || scene.GetFaceTerrainType(face) != TerrainType.OpenSea) offOpenSea++;
                if (p.X < 0 || p.Y < 0 || p.X >= extent || p.Y >= extent) outside++;
            }
            r["candidates"] = candidates.Count; r["independentRecount"] = recount; r["offOpenSea"] = offOpenSea; r["outsideWorld"] = outside;
            r["first"] = candidates.Take(5).Select(p => new[] { p.X, p.Y }).ToList();
            r["candidatePositions"] = candidates.Select(p => new[] { p.X, p.Y }).ToList();
            r["source"] = "StormCampaignBehavior._allOpenSeaWeatherNodePositions (InitializeStormNodes at session launch)";
            if (candidates.Count == 0) problems.Add("No open-sea storm spawn candidates.");
            if (candidates.Count != recount) problems.Add("Candidate count " + candidates.Count + " differs from the OpenSea node recount " + recount + ".");
            if (offOpenSea != 0 || outside != 0) problems.Add(offOpenSea + " candidates are not on generated OpenSea faces; " + outside + " lie outside the world.");
            return problems;
        }

        private static object CampaignBehavior(Type type)
        {
            if (type == null) return null;
            var method = typeof(Campaign).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                .First(m => m.Name == "GetCampaignBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(type);
            return method.Invoke(method.IsStatic ? null : Campaign.Current, null);
        }

        private static IEnumerator Sailing(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, Settlement town1, Settlement town2,
            Dictionary<string, object> row)
        {
            var main = MobileParty.MainParty;
            RuntimeMissionProbe.Pause();
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (main.Ships.Count == 0)
            {
                var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { town1.Culture }).Where(c => c != null).SelectMany(c => c.AvailableShipHulls).First();
                int crew = Math.Max(0, new Ship(hull).SkeletalCrewCapacity - main.MemberRoster.TotalHealthyCount);
                if (crew > 0) main.MemberRoster.AddToCounts(Hero.MainHero.Culture.BasicTroop, crew, false, 0, 0, true, -1);
                ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, new Ship(hull));
                row["synthetic"] = "one culture ship hull and a skeletal crew of basic troops granted to the player";
            }
            main.SetSailAtPosition(town1.PortPosition); main.SetMoveModeHold();
            var boost = WindBoost();
            row["windBoostSource"] = boost == null ? null : boost.Item1.GetType().FullName + ".CalculateWindBoostForParty";
            foreach (var leg in new[] { Tuple.Create("forward", town2), Tuple.Create("back", town1) })
            {
                var record = new Dictionary<string, object> { ["leg"] = leg.Item1 + " (toward " + leg.Item2.StringId + " port)" }; row[leg.Item1] = record;
                var samples = new List<object>(); record["samples"] = samples;
                main.SetMoveGoToPoint(leg.Item2.PortPosition, MobileParty.NavigationType.Naval);
                var start = main.Position.ToVec2(); double startHours = CampaignTime.Now.ToHours, begin = context.Seconds, lastSample = 0;
                Vec2? previousSample = null;
                boot.HoldPaused = false;
                DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                while (context.Seconds - begin < 8)
                {
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                    if (context.Seconds - lastSample >= .25 && context.Seconds - begin > 1)
                    {
                        lastSample = context.Seconds;
                        var bearing = main.Bearing; var wind = campaign.Models.MapWeatherModel.GetWindForPosition(main.Position);
                        float angle = bearing.Length > 0 && wind.Length > 0 ? MathF.Acos(MathF.Clamp(Vec2.DotProduct(bearing.Normalized(), wind.Normalized()), -1, 1)) * 57.29578f : float.NaN;
                        float measured = boost == null ? float.NaN : (float)boost.Item2.Invoke(boost.Item1, new object[] { main });
                        // NavalDLCPartySpeedCalculationModel.CalculateWindBoostForParty without perks (IL_001c-007f): the angle is
                        // |wind.RotationInRadians - bearing.RotationInRadians| in degrees, not wrapped to [0, 180]; below 120 the boost is
                        // Map(angle, 0, 120, |w|, 0) * 1.5 clamped to [0, 1.5], else 0.
                        float engineAngle = MathF.Abs(wind.RotationInRadians - bearing.RotationInRadians) * 57.29578f;
                        float predicted = wind.Length > 0 && engineAngle < 120 ? MathF.Clamp(wind.Length * (1 - engineAngle / 120) * 1.5f, 0, 1.5f) : 0;
                        bool nearStorm = NearStorm(main.Position.ToVec2());  // natural storms spawn while time runs and add to the wind
                        var here = main.Position.ToVec2(); bool moved = previousSample.HasValue && previousSample.Value.Distance(here) > 1e-4f; previousSample = here;
                        string terrain = campaign.MapSceneWrapper.GetFaceTerrainType(main.CurrentNavigationFace).ToString();
                        samples.Add(new Dictionary<string, object> { ["at"] = new[] { main.Position.X, main.Position.Y }, ["atSea"] = main.IsCurrentlyAtSea, ["nearStorm"] = nearStorm,
                            ["bearing"] = new[] { bearing.X, bearing.Y }, ["wind"] = new[] { wind.X, wind.Y }, ["angleDegrees"] = angle, ["engineAngleDegrees"] = engineAngle,
                            ["windBoost"] = measured, ["predictedWindBoost"] = predicted, ["speed"] = main.Speed, ["terrain"] = terrain, ["moved"] = moved,
                            ["speedWithoutWind"] = main.Speed / (1 + measured) });
                    }
                    yield return null;
                }
                DiagnosticSandboxBootstrap.Pause(campaign);
                boot.HoldPaused = true;
                var end = main.Position.ToVec2(); double hours = CampaignTime.Now.ToHours - startHours;
                var moving = samples.Cast<Dictionary<string, object>>().Where(s => (bool)s["atSea"] && (bool)s["moved"] && !(bool)s["nearStorm"] && !float.IsNaN((float)s["angleDegrees"])).ToList();
                record["samplesStationary"] = samples.Cast<Dictionary<string, object>>().Count(s => !(bool)s["moved"]);
                record["samplesNearStorm"] = samples.Cast<Dictionary<string, object>>().Count(s => (bool)s["nearStorm"]);
                record["start"] = new[] { start.X, start.Y }; record["end"] = new[] { end.X, end.Y };
                record["campaignHours"] = hours; record["displacement"] = start.Distance(end);
                record["displacementPerHour"] = hours > 0 ? start.Distance(end) / hours : double.NaN;
                record["samplesUsed"] = moving.Count;
                record["meanSpeed"] = moving.Count == 0 ? float.NaN : moving.Average(s => (float)s["speed"]);
                record["meanWindBoost"] = moving.Count == 0 ? float.NaN : moving.Average(s => (float)s["windBoost"]);
                record["meanAngleDegrees"] = moving.Count == 0 ? float.NaN : moving.Average(s => (float)s["angleDegrees"]);
                record["maxBoostFormulaError"] = moving.Count == 0 ? float.NaN : moving.Max(s => Math.Abs((float)s["windBoost"] - (float)s["predictedWindBoost"]));
            }
            main.SetMoveModeHold();
        }

        // NavalDLCManager.Instance is a static field (NavalDLCMapWeatherModel reads it with ldsfld).
        private static object NavalManager()
        {
            var managerType = HarmonyLib.AccessTools.TypeByName("NavalDLC.NavalDLCManager");
            if (managerType == null) return null;
            var field = HarmonyLib.AccessTools.Field(managerType, "Instance");
            if (field != null && field.IsStatic) return field.GetValue(null);
            var property = HarmonyLib.AccessTools.Property(managerType, "Instance");
            return property == null ? null : property.GetValue(null);
        }

        // Within 1.5 x the effect radius of any spawned storm (NavalDLC adds storm wind and rain inside it).
        private static bool NearStorm(Vec2 p)
        {
            var managerType = HarmonyLib.AccessTools.TypeByName("NavalDLC.NavalDLCManager");
            object naval = managerType == null ? null : NavalManager();
            object storms = naval == null ? null : HarmonyLib.AccessTools.Property(managerType, "StormManager").GetValue(naval);
            if (storms == null) return false;
            foreach (var storm in (IEnumerable)HarmonyLib.AccessTools.Property(storms.GetType(), "SpawnedStorms").GetValue(storms))
            {
                var at = (Vec2)HarmonyLib.AccessTools.Property(storm.GetType(), "CurrentPosition").GetValue(storm);
                float radius = (float)HarmonyLib.AccessTools.Property(storm.GetType(), "EffectRadius").GetValue(storm);
                if (at.Distance(p) < 1.5f * radius) return true;
            }
            return false;
        }

        // The War Sails speed model in the active chain and its private CalculateWindBoostForParty(MobileParty).
        private static Tuple<object, MethodInfo> WindBoost()
        {
            object model = Campaign.Current.Models.PartySpeedCalculatingModel;
            for (int depth = 0; model != null && depth < 8; depth++)
            {
                var method = model.GetType().GetMethod("CalculateWindBoostForParty", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (method != null) return Tuple.Create(model, method);
                var baseModel = model.GetType().GetProperty("BaseModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                model = baseModel == null ? null : baseModel.GetValue(model);
            }
            return null;
        }

        private static void Judge(Dictionary<string, object> sailing, List<string> failures, string name, string pattern)
        {
            if (sailing.ContainsKey("error")) { failures.Add(name + ": " + ((string)sailing["error"]).Split('\n')[0]); return; }
            if (sailing["windBoostSource"] == null) failures.Add(name + ": no War Sails wind boost in the speed model chain.");
            var forward = (Dictionary<string, object>)sailing["forward"]; var back = (Dictionary<string, object>)sailing["back"];
            foreach (var leg in new[] { forward, back })
                if ((int)leg["samplesUsed"] < 5) failures.Add(name + ": " + leg["leg"] + " has too few moving samples at sea (" + leg["samplesUsed"] + ").");
            // NavalDLCPartySpeedCalculationModel.CalculateFinalSpeed multiplies by (1 + wind boost) and adds a terrain factor
            // (OpenSea x1.448), so speed / (1 + boost) must be one value per terrain across both legs.
            var used = new[] { forward, back }.SelectMany(l => ((List<object>)l["samples"]).Cast<Dictionary<string, object>>())
                .Where(s => (bool)s["atSea"] && (bool)s["moved"] && !(bool)s["nearStorm"]).ToList();
            var spread = used.GroupBy(s => (string)s["terrain"]).ToDictionary(g => g.Key, g => (object)(g.Max(s => (float)s["speedWithoutWind"]) / g.Min(s => (float)s["speedWithoutWind"])));
            sailing["speedWithoutWindSpreadByTerrain"] = spread;
            if (spread.Count == 0 || spread.Values.Any(v => (float)v > 1.001f)) failures.Add(name + ": speed / (1 + wind boost) is not constant per terrain.");
            if (pattern == "along")
            {
                // The wind blows from port 1 toward port 2: the forward leg sails with it, the way back against it.
                if (!((float)forward["meanWindBoost"] > (float)back["meanWindBoost"] + 0.02f)) failures.Add(name + ": the wind boost sailing with the wind is not larger than against it.");
                if (!((double)forward["displacementPerHour"] > (double)back["displacementPerHour"])) failures.Add(name + ": the ship did not cover more ground per hour sailing with the wind.");
            }
        }

        private static IEnumerable<string> SpawnStorm(Dictionary<string, object> r, MapWeatherModel weather, GeneratedClimate climate, float extent)
        {
            var problems = new List<string>();
            var managerType = HarmonyLib.AccessTools.TypeByName("NavalDLC.NavalDLCManager");
            object naval = managerType == null ? null : NavalManager();
            object storms = naval == null ? null : HarmonyLib.AccessTools.Property(managerType, "StormManager").GetValue(naval);
            if (storms == null) { problems.Add("No NavalDLC StormManager."); return problems; }
            var behaviorType = HarmonyLib.AccessTools.TypeByName("NavalDLC.CampaignBehaviors.StormCampaignBehavior");
            var candidates = ((IEnumerable)HarmonyLib.AccessTools.Field(behaviorType, "_allOpenSeaWeatherNodePositions").GetValue(CampaignBehavior(behaviorType))).Cast<Vec2>().ToList();
            if (candidates.Count == 0) { problems.Add("No candidate to spawn at."); return problems; }
            // Prefer a candidate where the generated field is wet, so the storm type reads a non-zero rain value.
            // Prefer the wettest generated cell, then the candidate nearest the world centre (away from the border).
            var centre = new Vec2(extent / 2, extent / 2);
            var p = candidates.OrderByDescending(c => climate.RainAt(c)).ThenBy(c => c.Distance(centre)).ThenBy(c => c.X).ThenBy(c => c.Y).First();
            // Read the storm model's type and the rain first: a spawned storm forces rain 1 around itself (NavalDLCMapWeatherModel).
            object modelType = null;
            var gameModels = HarmonyLib.AccessTools.Property(managerType, "GameModels").GetValue(naval);
            var stormModel = gameModels == null ? null : HarmonyLib.AccessTools.Property(gameModels.GetType(), "MapStormModel").GetValue(gameModels);
            if (stormModel != null) modelType = HarmonyLib.AccessTools.Method(stormModel.GetType(), "GetSpawnedStormTypeForPosition").Invoke(stormModel, new object[] { p });
            float scaledSnow, scaledRain;
            weather.GetSnowAndRainDataForPosition(p, CampaignTime.Now, out scaledSnow, out scaledRain);
            bool nearExistingStorm = NearStorm(p);
            r["naturalStormsBeforeSpawn"] = ((IEnumerable)HarmonyLib.AccessTools.Property(storms.GetType(), "SpawnedStorms").GetValue(storms)).Cast<object>()
                .Select(s => { var at = (Vec2)HarmonyLib.AccessTools.Property(s.GetType(), "CurrentPosition").GetValue(s); var t = StormType(s);
                    return (object)new Dictionary<string, object> { ["at"] = new[] { at.X, at.Y }, ["type"] = t == null ? null : t.ToString() }; }).ToList();
            // NavalDLCStormModel.GetSpawnedStormTypeForPosition: season-scaled rain <= 0.2 -> type 0, <= 0.6 -> 1, else 2.
            int expectedType = scaledRain <= 0.2f ? 0 : scaledRain <= 0.6f ? 1 : 2;
            var spawned = (IEnumerable)HarmonyLib.AccessTools.Property(storms.GetType(), "SpawnedStorms").GetValue(storms);
            int before = spawned.Cast<object>().Count();
            HarmonyLib.AccessTools.Method(storms.GetType(), "CreateStormAtPosition", new[] { typeof(Vec2) }).Invoke(storms, new object[] { p });
            var list = ((IEnumerable)HarmonyLib.AccessTools.Property(storms.GetType(), "SpawnedStorms").GetValue(storms)).Cast<object>().ToList();
            var storm = list.LastOrDefault();
            object stormType = storm == null ? null : StormType(storm);
            var position = storm == null ? Vec2.Zero : (Vec2)HarmonyLib.AccessTools.Property(storm.GetType(), "CurrentPosition").GetValue(storm);
            r["at"] = new[] { p.X, p.Y }; r["generatedRain"] = climate.RainAt(p); r["seasonScaledRainBeforeSpawn"] = scaledRain; r["nearExistingStorm"] = nearExistingStorm;
            r["stormsBefore"] = before; r["stormsAfter"] = list.Count; r["stormPosition"] = new[] { position.X, position.Y };
            r["stormType"] = stormType == null ? null : stormType.ToString(); r["modelStormTypeAtPosition"] = modelType == null ? null : modelType.ToString();
            r["weatherEventAtPosition"] = weather.GetWeatherEventInPosition(p).ToString();
            r["method"] = "NavalDLC StormManager.CreateStormAtPosition(Vec2) at an open-sea spawn candidate (the call TrySpawningNewStorm makes)";
            if (list.Count != before + 1 || storm == null) problems.Add("CreateStormAtPosition did not add a storm.");
            else if (position.Distance(p) > 1f) problems.Add("The storm is not at the candidate.");
            r["expectedTypeIndex"] = expectedType; r["stormTypeIndex"] = stormType == null ? -1 : Convert.ToInt32(stormType);
            if (nearExistingStorm) problems.Add("The spawn candidate lies inside an existing storm, whose forced rain would decide the type.");
            else if (stormType == null || Convert.ToInt32(stormType) != expectedType)
                problems.Add("Storm type " + stormType + " is not the type the threshold rule gives for season-scaled rain " + scaledRain + ".");
            pendingStorm = new WeakReference(storm);
            return problems;
        }

        private static object StormType(object storm)
        {
            foreach (var p in storm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (p.PropertyType.IsEnum && p.PropertyType.Name == "StormTypes" && p.GetIndexParameters().Length == 0) return p.GetValue(storm);
            foreach (var f in storm.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType.IsEnum && f.FieldType.Name == "StormTypes") return f.GetValue(storm);
            return null;
        }

        private static IEnumerable<string> Switch(Dictionary<string, object> r, DiagnosticMapScene map, Campaign campaign, GeneratedClimate climate, float extent, string worldId)
        {
            var problems = new List<string>();
            if (previousMap == null) { r["first"] = true; return problems; }
            r["previousWorld"] = previousWorld;
            var wind = (float[])WindField.GetValue(map); var precipitation = (byte[])PrecipitationField.GetValue(map);
            string windSha = GeneratedClimate.Sha256(wind), precipitationSha = GeneratedClimate.Sha256(precipitation);
            r["sameMapObject"] = ReferenceEquals(previousMap.Target, map); r["sameCampaignObject"] = ReferenceEquals(previousCampaign.Target, campaign);
            r["sameWindBuffer"] = ReferenceEquals(previousWind.Target, wind); r["samePrecipitationBuffer"] = ReferenceEquals(previousPrecipitation.Target, precipitation);
            r["windDiffersFromPrevious"] = windSha != previousWindSha; r["precipitationDiffersFromPrevious"] = precipitationSha != previousPrecipitationSha;
            var behaviorType = HarmonyLib.AccessTools.TypeByName("NavalDLC.CampaignBehaviors.StormCampaignBehavior");
            var candidates = ((IEnumerable)HarmonyLib.AccessTools.Field(behaviorType, "_allOpenSeaWeatherNodePositions").GetValue(CampaignBehavior(behaviorType))).Cast<Vec2>()
                .Select(p => new[] { p.X, p.Y }).ToList();
            var previousSet = new HashSet<string>(previousCandidates.Select(p => p[0] + "," + p[1]));
            r["candidatesShared"] = candidates.Count(p => previousSet.Contains(p[0] + "," + p[1]));
            r["candidatesNow"] = candidates.Count; r["candidatesBefore"] = previousCandidates.Count;
            var managerType = HarmonyLib.AccessTools.TypeByName("NavalDLC.NavalDLCManager");
            object naval = NavalManager();
            object storms = HarmonyLib.AccessTools.Property(managerType, "StormManager").GetValue(naval);
            r["sameNavalManager"] = ReferenceEquals(previousNaval.Target, naval); r["sameStormManager"] = ReferenceEquals(previousStormManager.Target, storms);
            var current = ((IEnumerable)HarmonyLib.AccessTools.Property(storms.GetType(), "SpawnedStorms").GetValue(storms)).Cast<object>().ToList();
            r["stormsAtSwitch"] = current.Count;
            r["previousStormRetained"] = previousStorm != null && previousStorm.Target != null && current.Any(s => ReferenceEquals(s, previousStorm.Target));
            if ((bool)r["sameMapObject"] || (bool)r["sameCampaignObject"] || (bool)r["sameWindBuffer"] || (bool)r["samePrecipitationBuffer"])
                problems.Add("The new world reuses the previous world's map scene, campaign or buffers.");
            if (!(bool)r["windDiffersFromPrevious"] || !(bool)r["precipitationDiffersFromPrevious"]) problems.Add("The new world's fields equal the previous world's.");
            if (windSha != climate.WindSha256 || precipitationSha != climate.PrecipitationSha256) problems.Add("The new world's fields are not its generated fields.");
            if ((bool)r["sameNavalManager"] || (bool)r["sameStormManager"]) problems.Add("The War Sails manager or storm manager was retained.");
            if ((bool)r["previousStormRetained"]) problems.Add("The previous world's storm is still spawned.");
            if ((int)r["candidatesShared"] == candidates.Count && candidates.Count == previousCandidates.Count) problems.Add("The storm spawn area did not change with the world.");
            return problems;
        }

        private static IEnumerable<string> Released(Dictionary<string, object> r)
        {
            r["previousMapAlive"] = previousMap.IsAlive; r["previousCampaignAlive"] = previousCampaign.IsAlive;
            r["previousWindAlive"] = previousWind.IsAlive; r["previousPrecipitationAlive"] = previousPrecipitation.IsAlive;
            r["previousNavalManagerAlive"] = previousNaval.IsAlive; r["previousStormManagerAlive"] = previousStormManager.IsAlive;
            r["previousStormAlive"] = previousStorm != null && previousStorm.IsAlive;
            r["previousClimateAlive"] = previousClimate != null && previousClimate.IsAlive;
            r["previousBootstrapAlive"] = previousBootstrap != null && previousBootstrap.IsAlive;
            r["note"] = "managed objects only; native textures owned by the previous engine Scene are not observable from managed code";
            var alive = r.Where(kv => kv.Value is bool && (bool)kv.Value).Select(kv => kv.Key).ToList();
            if (alive.Count != 0) yield return "Still alive after three full collections: " + String.Join(", ", alive) + ".";
        }

        private static void Remember(DiagnosticMapScene map, Campaign campaign, GeneratedClimate climate, string worldId, Dictionary<string, object> row,
            DiagnosticSandboxBootstrap boot)
        {
            previousClimate = new WeakReference(climate); previousBootstrap = new WeakReference(boot);
            var wind = (float[])WindField.GetValue(map); var precipitation = (byte[])PrecipitationField.GetValue(map);
            previousMap = new WeakReference(map); previousCampaign = new WeakReference(campaign);
            previousWind = new WeakReference(wind); previousPrecipitation = new WeakReference(precipitation);
            previousWindSha = GeneratedClimate.Sha256(wind); previousPrecipitationSha = GeneratedClimate.Sha256(precipitation);
            previousWorld = worldId;
            previousStorm = pendingStorm; pendingStorm = null;
            var managerType = HarmonyLib.AccessTools.TypeByName("NavalDLC.NavalDLCManager");
            object naval = managerType == null ? null : NavalManager();
            previousNaval = new WeakReference(naval);
            previousStormManager = new WeakReference(naval == null ? null : HarmonyLib.AccessTools.Property(managerType, "StormManager").GetValue(naval));
            var area = row.ContainsKey("stormArea") ? (Dictionary<string, object>)row["stormArea"] : null;
            previousCandidates = area != null && area.ContainsKey("candidatePositions") ? (List<float[]>)area["candidatePositions"] : new List<float[]>();
        }
    }
}
