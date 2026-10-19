using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using NavalDLC;
using NavalDLC.CampaignBehaviors;
using NavalDLC.ComponentInterfaces;
using NavalDLC.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-39: water, flow and climate on a generated world (docs/water-climate.md), in the running game. Each section is fail-soft (its
    // error is recorded and the next one runs):
    //   water     the water body on the datum, and a ray pick at sea (the water plane) and on land (the terrain);
    //   climate   the bound fields are the synthesis (the scene's private buffers byte for byte), read back through SandBox and War Sails at
    //             sample points, with strip copies equal to their canonical points; no Calradia weather resource is bound;
    //   colour    the scene's colour-grade manager holds the generated grade grid, or the neutral grid for a diagnostic climate (never
    //             Calradia's worldmap_colorgrade_grid);
    //   dayNight  at 01:00 and 13:00 the manager applies the night grade, then the grid's grade under the camera, with a screenshot each;
    //   seasons   snow and rain of a snowy, a rainy and a dry node in each season (MapWeatherModel with explicit times), and the atmosphere
    //             at night and noon at two distant points (no position grid: a generated scene has no atmosphere probes);
    //   sailing   a ship on generated open sea sailing downwind and upwind (War Sails' wind boost), with a screenshot of the ship on the water;
    //   wakes     War Sails' naval map-scene wrapper (which sets up the wake renderer on our scene) is in place;
    //   storms    (periodic worlds) the storm model in War Sails' set, spawn gating by canonical position, a storm crossing the seam (moved,
    //             not ended, never doubled), its damage and wind reaching across the seam (the model, and War Sails' own damage pass on the
    //             player's ship across the seam and beside the storm), and a storm past the south pole line reflected back;
    //   pirates   (worlds with generated pirate spawn points) War Sails' patrol zones built from them and pirate parties spawned at them.
    internal static class WaterClimateProbe
    {
        internal static IEnumerator Run(ProbeContext context, DiagnosticSandboxBootstrap boot, GeneratedCampaignRegistration registration, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var campaign = Campaign.Current;
            var map = (GeneratedMapScene)campaign.MapSceneWrapper;
            var world = PeriodicWorld.Current;
            var climate = registration.Climate;
            float extent = map.GetTerrainSize().X;
            report["world"] = registration.WorldId; report["extent"] = extent; report["periodic"] = world != null;
            report["climate"] = climate == null ? null : new Dictionary<string, object> { ["schema"] = climate.Schema, ["pattern"] = climate.Pattern, ["identity"] = climate.Identity,
                ["windSha256"] = climate.WindSha256, ["precipitationSha256"] = climate.PrecipitationSha256 };
            var town1 = Settlement.Find(registration.WorldId + "_town_1"); var town2 = Settlement.Find(registration.WorldId + "_town_2");
            Sync(report, failures, "water", r => Water(r, map, registration.WorldId, extent, town1));
            Sync(report, failures, "climate", r => Climate(r, map, climate, world, extent));
            Sync(report, failures, "colorGrade", r => ColorGrade(r, map, climate, world, extent));
            Sync(report, failures, "seasons", r => Seasons(r, map, climate, world, extent));
            Sync(report, failures, "wakes", r => Wakes(r));
            context.Checkpoint();
            foreach (var part in new[] { "sailing", "dayNight", "storms", "pirates" })
            {
                if (part == "storms" && world == null) { report[part] = new Dictionary<string, object> { ["skipped"] = "not a periodic world" }; continue; }
                var plan = ((IGeneratedWorld)registration).NavalPlan;
                if (part == "pirates" && (plan == null || plan.PirateSpawnPoints.Count == 0)) { report[part] = new Dictionary<string, object> { ["skipped"] = "no generated pirate spawn points" }; continue; }
                var row = new Dictionary<string, object>(); report[part] = row;
                var rowFailures = new List<string>(); row["failures"] = rowFailures;
                IEnumerator steps = part == "sailing" ? Sailing(context, boot, campaign, map, world, climate, extent, row)
                    : part == "dayNight" ? DayNight(context, boot, campaign, map, row)
                    : part == "storms" ? Storms(context, boot, campaign, map, world, row) : Pirates(context, boot, campaign, plan, row);
                Exception error = null;
                while (true)
                {
                    try { if (!steps.MoveNext()) break; } catch (Exception e) { error = e; break; }
                    yield return steps.Current;
                }
                if (error != null) { row["error"] = error.ToString(); failures.Add(part + ": " + error.Message); }
                failures.AddRange(rowFailures.Select(f => part + ": " + f));
                context.Checkpoint();
            }
            report["counters"] = new Dictionary<string, object> { ["stormRebases"] = PeriodicStorms.Events.Count, ["suppressedSpawnChecks"] = GeneratedStormModel.SuppressedSpawnChecks,
                ["copiedDamageQueries"] = GeneratedStormModel.CopiedDamageQueries, ["spawnPointQueries"] = GeneratedNavalMapSceneWrapper.SpawnPointQueries };
        }

        private static void Sync(Dictionary<string, object> report, List<string> failures, string name, Func<Dictionary<string, object>, IEnumerable<string>> section)
        {
            var row = new Dictionary<string, object>(); report[name] = row;
            try { failures.AddRange(section(row).Select(p => name + ": " + p)); }
            catch (Exception e) { row["error"] = e.ToString(); failures.Add(name + ": " + e.Message); }
        }

        // ---- water ----
        private static IEnumerable<string> Water(Dictionary<string, object> r, GeneratedMapScene map, string worldId, float extent, Settlement town)
        {
            var problems = new List<string>();
            var scene = map.Scene;
            var bodies = scene.FindEntitiesWithTag(GeneratedWaterSystem.Tag).ToList();
            r["waterBodies"] = bodies.Select(b => b.Name).ToList();
            GameEntity water = bodies.FirstOrDefault(b => b.Name.EndsWith("_water", StringComparison.Ordinal));
            if (water == null) { problems.Add("No generated water body in the scene."); return problems; }
            if (bodies.Count != 1) problems.Add(bodies.Count + " water bodies in the scene (one expected).");
            var frame = water.GetGlobalFrame();
            float scaleX = frame.rotation.s.Length, scaleY = frame.rotation.f.Length;
            r["entity"] = water.Name; r["origin"] = new[] { frame.origin.x, frame.origin.y, frame.origin.z }; r["scale"] = new[] { scaleX, scaleY };
            if (Math.Abs(frame.origin.z - (float)GeneratedWaterSystem.DatumZ) > 1e-3f) problems.Add("The water body is not on the datum (z " + frame.origin.z + ").");
            if (scaleX < extent || scaleY < extent || Math.Abs(frame.origin.x - extent / 2) > 1 || Math.Abs(frame.origin.y - extent / 2) > 1) problems.Add("The water body does not cover the terrain.");
            // A ray straight down at open sea stops on the water plane at the datum; on land it reaches the terrain above it.
            var sea = SeaPoint(map, extent);
            var land = LandPoint(map, extent);
            if (!sea.IsValid || !land.IsValid) problems.Add("No " + (sea.IsValid ? "land point away from settlements" : "open-sea point") + " to pick.");
            var picks = new List<object>(); r["picks"] = picks;
            foreach (var at in new[] { sea, land })
            {
                if (!at.IsValid) continue;
                float distance; Vec3 closest;
                bool hit = scene.RayCastForClosestEntityOrTerrain(new Vec3(at.X, at.Y, 60), new Vec3(at.X, at.Y, -60), out distance, out closest, 0.01f);
                float terrain = 0; map.GetHeightAtPoint(new CampaignVec2(at, true), ref terrain);
                float bed = scene.GetTerrainHeight(at, true);
                picks.Add(new Dictionary<string, object> { ["at"] = new[] { at.X, at.Y }, ["hit"] = hit, ["hitZ"] = hit ? (object)closest.z : null, ["terrainZ"] = bed, ["mapHeight"] = terrain });
                if (!hit) problems.Add("A ray at " + at + " hit nothing.");
                else if (at == sea && (Math.Abs(closest.z - (float)GeneratedWaterSystem.DatumZ) > 0.05f || bed >= 0)) problems.Add("The sea pick did not stop on the water plane (z " + closest.z + ", sea bed " + bed + ").");
                else if (at != sea && Math.Abs(closest.z - bed) > 0.1f) problems.Add("The land pick did not reach the terrain (z " + closest.z + ", terrain " + bed + ").");
            }
            return problems;
        }

        // A land point above the datum, at least 40 units from every settlement (whose meshes a ray would hit first), in the canonical range.
        private static Vec2 LandPoint(GeneratedMapScene map, float extent)
        {
            var world = PeriodicWorld.Current;
            float x0 = world == null ? 0 : world.OriginX, x1 = world == null ? extent : world.MaxX;
            var settlements = Settlement.All.Select(s => s.GatePosition.ToVec2()).ToList();
            for (float y = extent * .3f; y < extent * .7f; y += 6.25f)
                for (float x = x0 + 3.125f; x < x1; x += 6.25f)
                {
                    var p = new CampaignVec2(new Vec2(x, y), true);
                    if (!p.Face.IsValid() || map.Scene.GetTerrainHeight(p.ToVec2(), true) < 1f) continue;
                    var type = map.GetFaceTerrainType(p.Face);
                    if (type == TerrainType.Water || type == TerrainType.River || type == TerrainType.Lake || type == TerrainType.CoastalSea || type == TerrainType.OpenSea) continue;
                    if (settlements.All(s => s.Distance(p.ToVec2()) > 40f)) return p.ToVec2();
                }
            return Vec2.Invalid;
        }

        // An open-sea point in the middle rows of the canonical range (or of the terrain).
        private static Vec2 SeaPoint(GeneratedMapScene map, float extent) { return SeaPoint(map, extent, 0); }

        // ... with open sea along its row for `clear` units east and west of it (a sailing leg each way).
        private static Vec2 SeaPoint(GeneratedMapScene map, float extent, float clear) { return SeaPoint(map, extent, clear, 0f); }

        // ... and at least `stormClearance` from every storm.
        private static Vec2 SeaPoint(GeneratedMapScene map, float extent, float clear, float stormClearance)
        {
            var manager = stormClearance > 0 && NavalDLCManager.Instance != null ? NavalDLCManager.Instance.StormManager : null;
            var storms = manager == null ? new List<Vec2>() : manager.SpawnedStorms.Select(s => s.CurrentPosition).ToList();
            var world = PeriodicWorld.Current;
            float x0 = world == null ? extent * 0.25f : world.OriginX + world.Width * 0.25f, x1 = world == null ? extent * 0.75f : world.MaxX - world.Width * 0.25f;
            float y0 = world == null ? extent * 0.25f : (float)(world.Coordinates.OriginY + world.Coordinates.Height * 0.25), y1 = world == null ? extent * 0.75f : (float)(world.Coordinates.OriginY + world.Coordinates.Height * 0.75);
            for (float y = (y0 + y1) / 2, dy = 0; dy < (y1 - y0) / 2; dy += 3.125f)
                foreach (float yy in new[] { y + dy, y - dy })
                    for (float x = x0; x < x1; x += 6.25f)
                    {
                        bool open = true;
                        for (float dx = -clear; dx <= clear && open; dx += 3.125f)
                        {
                            var p = new CampaignVec2(new Vec2(x + dx, yy), false);
                            open = p.Face.IsValid() && map.GetFaceTerrainType(p.Face) == TerrainType.OpenSea;
                        }
                        if (open && storms.All(s => s.Distance(new Vec2(x, yy)) > stormClearance)) return new Vec2(x, yy);
                    }
            return Vec2.Invalid;
        }

        // ---- climate ----
        private static IEnumerable<string> Climate(Dictionary<string, object> r, GeneratedMapScene map, GeneratedClimate climate, PeriodicWorld world, float extent)
        {
            var problems = new List<string>();
            if (climate == null) { problems.Add("The world has no generated climate."); return problems; }
            var windField = (float[])Field(typeof(SandBox.MapScene), "_windFlowMapData").GetValue(map);
            var rainField = (byte[])Field(typeof(SandBox.MapScene), "_snowAndRainData").GetValue(map);
            string windSha = GeneratedClimate.Sha256(windField), rainSha = GeneratedClimate.Sha256(rainField);
            r["boundWindSha256"] = windSha; r["boundPrecipitationSha256"] = rainSha; r["weatherNodeDimension"] = Campaign.Current.DefaultWeatherNodeDimension;
            if (windSha != climate.WindSha256 || rainSha != climate.PrecipitationSha256) problems.Add("The scene's weather buffers are not the generated climate's.");
            if (Campaign.Current.DefaultWeatherNodeDimension != GeneratedClimate.WeatherDimension) problems.Add("The weather node dimension is not the generated one.");
            if (map.Scene.GetFirstEntityWithName("MapSnowAndRainDefiner") != null || map.Scene.GetFirstEntityWithScriptComponent("MapSnowAndRainDefiner") != null)
                problems.Add("A Calradia weather definer is in the scene.");
            var samples = new List<object>(); r["samples"] = samples;
            int mismatches = 0, copyMismatches = 0;
            var weather = Campaign.Current.Models.MapWeatherModel;
            for (int i = 0; i < 24; i++)
            {
                float x = world == null ? extent * (i + .5f) / 24 : world.OriginX + world.Width * (i + .5f) / 24;
                float y = world == null ? extent * .5f : (float)(world.Coordinates.OriginY + world.Coordinates.Height * (.2 + .6 * (i % 6) / 5.0));
                var p = new Vec2(x, y);
                var engineWind = map.GetWindAtPosition(p);
                float snow = ((IMapScene)map).GetSnowAmountAtPosition(p), rain = ((IMapScene)map).GetRainAmountAtPosition(p);
                var modelWind = weather.GetWindForPosition(new CampaignVec2(p, false));
                var expected = climate.WindAt(p);
                bool ok = engineWind.X == expected.X && engineWind.Y == expected.Y && Math.Abs(snow - climate.SnowAt(p)) < 0.5f && Math.Abs(rain - climate.RainAt(p)) < 0.5f;
                if (!ok) mismatches++;
                var row = new Dictionary<string, object> { ["at"] = new[] { x, y }, ["sceneWind"] = new[] { engineWind.X, engineWind.Y }, ["generatedWind"] = new[] { expected.X, expected.Y },
                    ["warSailsWind"] = new[] { modelWind.X, modelWind.Y }, ["snow"] = snow, ["rain"] = rain, ["generatedSnow"] = climate.SnowAt(p), ["generatedRain"] = climate.RainAt(p) };
                if (world != null)
                    foreach (var k in new[] { 1, -1 })
                    {
                        float cx = x + k * world.Width;
                        if (cx < 0 || cx > extent) continue;
                        var copy = new Vec2(cx, y);
                        var copyWind = map.GetWindAtPosition(copy);
                        bool same = copyWind.X == engineWind.X && copyWind.Y == engineWind.Y && ((IMapScene)map).GetSnowAmountAtPosition(copy) == snow && ((IMapScene)map).GetRainAmountAtPosition(copy) == rain;
                        row["copy" + (k > 0 ? "East" : "West")] = same;
                        if (!same) copyMismatches++;
                    }
                samples.Add(row);
            }
            r["mismatches"] = mismatches; r["copyMismatches"] = copyMismatches;
            if (mismatches > 0) problems.Add(mismatches + " sample points read back other values than the generated climate.");
            if (copyMismatches > 0) problems.Add(copyMismatches + " strip copies read back other values than their canonical points.");
            return problems;
        }

        // ---- colour grade ----
        private static IEnumerable<string> ColorGrade(Dictionary<string, object> r, GeneratedMapScene map, GeneratedClimate climate, PeriodicWorld world, float extent)
        {
            var problems = new List<string>();
            // A synthesized climate binds its grid; without one (an MMR-59 diagnostic pattern) the neutral grid (every cell 0, the default grade).
            bool generated = climate != null && climate.HasColorGrade;
            if (climate != null && climate.Schema == GeneratedClimate.SchemaV2 && !generated) { problems.Add("The synthesized climate has no colour grade."); return problems; }
            string expectedSha = generated ? climate.ColorGradeSha256 : GeneratedClimate.Sha256(new byte[GeneratedColorGrade.Cells]);
            var managers = GeneratedColorGrade.Managers(map.Scene);
            r["managers"] = managers.Count; r["patchApplied"] = GeneratedColorGrade.Applied; r["neutral"] = GeneratedColorGrade.Neutral; r["replaced"] = GeneratedColorGrade.Replaced;
            r["lastError"] = GeneratedColorGrade.LastError; r["expected"] = generated ? "generated" : "neutral"; r["expectedSha256"] = expectedSha;
            if (!GeneratedColorGrade.Applied || GeneratedColorGrade.Neutral == generated) problems.Add("The colour-grade patch is " + (GeneratedColorGrade.Applied ? (GeneratedColorGrade.Neutral ? "neutral" : "generated") : "not applied") + "; " + (generated ? "generated" : "neutral") + " expected.");
            if (managers.Count != 1) { problems.Add(managers.Count + " colour-grade managers in the scene (one expected)."); if (managers.Count == 0) return problems; }
            var grid = GeneratedColorGrade.Grid(managers[0]);
            string sha = grid == null ? null : GeneratedClimate.Sha256(grid);
            r["boundSha256"] = sha;
            if (sha != expectedSha) problems.Add("The colour-grade manager's grid is not the " + (generated ? "generated" : "neutral") + " grid.");
            if (GeneratedColorGrade.LastError != null) problems.Add("The colour-grade postfix failed: " + GeneratedColorGrade.LastError);
            var counts = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (grid != null) foreach (var g in grid.GroupBy(b => b).OrderBy(g => g.Key)) counts[g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = g.Count();
            r["grades"] = counts;
            if (world != null && grid != null)
            {
                // Strip cells are copies of their canonical cells.
                int period = (int)Math.Round(world.Width / (extent / GeneratedClimate.ColorGradeDimension)), origin = (int)Math.Round(world.OriginX / (extent / GeneratedClimate.ColorGradeDimension)), diff = 0;
                for (int row = 0; row < GeneratedClimate.ColorGradeDimension; row++)
                    for (int col = 0; col < GeneratedClimate.ColorGradeDimension; col++)
                    {
                        if (col >= origin && col < origin + period) continue;
                        int canonical = origin + (((col - origin) % period) + period) % period;
                        if (grid[row * GeneratedClimate.ColorGradeDimension + col] != grid[row * GeneratedClimate.ColorGradeDimension + canonical]) diff++;
                    }
                r["stripCellsDifferingFromCanonical"] = diff;
                if (diff != 0) problems.Add(diff + " strip colour-grade cells differ from their canonical cells.");
            }
            return problems;
        }

        // ---- seasons and day/night ----
        private static IEnumerable<string> Seasons(Dictionary<string, object> r, GeneratedMapScene map, GeneratedClimate climate, PeriodicWorld world, float extent)
        {
            var problems = new List<string>();
            if (climate == null) { problems.Add("The world has no generated climate."); return problems; }
            var weather = Campaign.Current.Models.MapWeatherModel;
            // Weather nodes: the snowiest, the rainiest and a dry one (no snow, least rain), inside the canonical range between the poles.
            var nodes = new List<Vec2>();
            float step = extent / GeneratedClimate.WeatherDimension;
            for (int row = 0; row < GeneratedClimate.WeatherDimension; row++)
                for (int col = 0; col < GeneratedClimate.WeatherDimension; col++)
                {
                    var p = new Vec2((col + .5f) * step, (row + .5f) * step);
                    if (world != null && (!world.IsCanonical(p.X) || world.Coordinates.PoleOf(p.Y) != PeriodicWorldCoordinates.Pole.None)) continue;
                    nodes.Add(p);
                }
            var snowy = nodes.OrderByDescending(p => climate.SnowAt(p)).ThenBy(p => p.X).First();
            var rainy = nodes.OrderByDescending(p => climate.RainAt(p)).ThenBy(p => p.X).First();
            var dry = nodes.Where(p => climate.SnowAt(p) == 0).OrderBy(p => climate.RainAt(p)).ThenBy(p => p.X).FirstOrDefault();
            // DefaultMapWeatherModel.GetSnowAndRainDataForPosition (installed IL): with the season factors (snapped to the weather period),
            // c = Lerp(0.55, -0.1, snowFactor), v = SmoothStep(c - 0.65, c + 0.65, snowByte / 255), snow = Lerp(0, v, v);
            // d = Lerp(0.7, 0.3, rainFactor), rain = SmoothStep(d - 0.45, d + 0.45, rainByte / 255). The engine's own season scaling: a zero
            // byte still gives a little snow in winter, as on Calradia; what the world supplies is the byte.
            var rows = new List<object>(); r["samples"] = rows;
            r["formula"] = "c = Lerp(0.55, -0.1, snowFactor); v = SmoothStep(c - 0.65, c + 0.65, snow / 255); snow = Lerp(0, v, v); d = Lerp(0.7, 0.3, rainFactor); rain = SmoothStep(d - 0.45, d + 0.45, rain / 255)";
            float maxError = 0; bool ordered = true;
            var snowBySeason = new Dictionary<string, float>(); var rainBySeason = new Dictionary<string, float>();
            for (int season = 0; season < 4; season++)
            {
                var time = CampaignTime.Now + CampaignTime.Days(21f * season);
                float snowFactor, rainFactor;
                weather.GetSeasonTimeFactorOfCampaignTime(time, out snowFactor, out rainFactor, true);
                string name = time.GetSeasonOfYear.ToString();
                var entry = new Dictionary<string, object> { ["season"] = name, ["snowFactor"] = snowFactor, ["rainFactor"] = rainFactor };
                var values = new Dictionary<string, float[]>();
                foreach (var named in new[] { Tuple.Create("snowy", snowy), Tuple.Create("rainy", rainy), Tuple.Create("dry", dry) })
                {
                    if (!named.Item2.IsValid) continue;
                    float s, rn; weather.GetSnowAndRainDataForPosition(named.Item2, time, out s, out rn);
                    float c = MBMath.Lerp(0.55f, -0.1f, snowFactor, 1e-05f), d = MBMath.Lerp(0.7f, 0.3f, rainFactor, 1e-05f);
                    float v = MBMath.SmoothStep(c - 0.65f, c + 0.65f, climate.SnowAt(named.Item2) / 255f);
                    float expectSnow = MBMath.Lerp(0f, v, v, 1e-05f), expectRain = MBMath.SmoothStep(d - 0.45f, d + 0.45f, climate.RainAt(named.Item2) / 255f);
                    maxError = Math.Max(maxError, Math.Max(Math.Abs(s - expectSnow), Math.Abs(rn - expectRain)));
                    values[named.Item1] = new[] { s, rn };
                    entry[named.Item1] = new Dictionary<string, object> { ["at"] = new[] { named.Item2.X, named.Item2.Y }, ["generatedSnow"] = climate.SnowAt(named.Item2),
                        ["generatedRain"] = climate.RainAt(named.Item2), ["snow"] = s, ["rain"] = rn, ["formulaSnow"] = expectSnow, ["formulaRain"] = expectRain };
                }
                if (values.ContainsKey("dry") && (values["snowy"][0] < values["dry"][0] || values["rainy"][1] < values["dry"][1])) ordered = false;
                snowBySeason[name] = values["snowy"][0]; rainBySeason[name] = values["rainy"][1];
                rows.Add(entry);
            }
            r["maxFormulaError"] = maxError; r["ordered"] = ordered; r["snowyNodeSnowBySeason"] = snowBySeason; r["rainyNodeRainBySeason"] = rainBySeason;
            if (maxError > 1e-4f) problems.Add("The weather model's snow or rain is not the installed formula over the generated bytes (error " + maxError + ").");
            if (!ordered) problems.Add("A drier generated node gets more snow or rain than the snowy or rainy node.");
            if (snowBySeason.Values.Max() - snowBySeason.Values.Min() < 0.05f || rainBySeason.Values.Max() - rainBySeason.Values.Min() < 0.05f) problems.Add("Snow or rain does not vary with the season.");
            // Day and night: the atmosphere at 01:00 and 13:00 at two distant points (no position-dependent Calradia grid).
            var day = CampaignTime.Now - CampaignTime.Hours(CampaignTime.Now.CurrentHourInDay);
            var a = world == null ? new Vec3(extent * .3f, extent * .5f, 0) : new Vec3(world.OriginX + world.Width * .2f, (float)(world.Coordinates.OriginY + world.Coordinates.Height * .5), 0);
            var b = world == null ? new Vec3(extent * .7f, extent * .3f, 0) : new Vec3(world.OriginX + world.Width * .8f, (float)(world.Coordinates.OriginY + world.Coordinates.Height * .3), 0);
            var states = new Dictionary<string, object>(); r["atmosphere"] = states;
            r["atmosphereProbes"] = map.Scene.FindEntitiesWithTag("atmosphere_probe").Count();
            r["atmosphereStates"] = ((IMapScene)map).GetAtmosphereStates().Count;
            string nightA = null, nightB = null, noonA = null;
            foreach (var hour in new[] { 1, 13 })
                foreach (var named in new[] { Tuple.Create("a", a), Tuple.Create("b", b) })
                {
                    var state = weather.GetInterpolatedAtmosphereState(day + CampaignTime.Hours(hour), named.Item2);
                    string text = Describe(state);
                    states[named.Item1 + "@" + hour] = text;
                    if (hour == 1 && named.Item1 == "a") nightA = text; if (hour == 1 && named.Item1 == "b") nightB = text; if (hour == 13 && named.Item1 == "a") noonA = text;
                }
            r["nightEqualsAcrossPoints"] = nightA == nightB; r["noonEqualsNight"] = nightA == noonA;
            r["dayNight"] = "the engine's time of day (MapScene/MapColorGradeManager: scene TimeOfDay, the night grade 22:00-02:00); the weather model's atmosphere state takes no time";
            if (nightA != nightB) problems.Add("The atmosphere differs between two points at the same hour (a position grid is bound).");
            if ((int)r["atmosphereProbes"] != 0) problems.Add("The scene has atmosphere probes (a position grid).");
            return problems;
        }

        private static string Describe(object state)
        {
            if (state == null) return "null";
            var type = state.GetType();
            var parts = type.GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name + "=" + Convert.ToString(f.GetValue(state), System.Globalization.CultureInfo.InvariantCulture))
                .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name + "=" + Convert.ToString(p.GetValue(state), System.Globalization.CultureInfo.InvariantCulture)));
            return String.Join(";", parts);
        }

        // ---- wakes ----
        private static IEnumerable<string> Wakes(Dictionary<string, object> r)
        {
            var problems = new List<string>();
            var installed = NavalDLCManager.Instance == null ? null : NavalDLCManager.Instance.NavalMapSceneWrapper;
            var ours = installed as GeneratedNavalMapSceneWrapper;
            var view = ours != null ? ours.Inner : installed;
            r["installed"] = installed == null ? null : installed.GetType().FullName;
            r["warSailsWrapper"] = view == null ? null : view.GetType().FullName;
            r["setup"] = "NavalDLC.View.NavalMapSceneWrapper's constructor calls InitializeMapWaterWake -> Scene.SetupWaterWake(128, 8) on the map scene";
            if (view == null || view.GetType().FullName != "NavalDLC.View.NavalMapSceneWrapper") problems.Add("War Sails' naval map-scene wrapper (the wake setup) is not in place.");
            return problems;
        }

        // ---- sailing ----
        private static IEnumerator Sailing(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, GeneratedMapScene map, PeriodicWorld world,
            GeneratedClimate climate, float extent, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            RuntimeMissionProbe.Pause();
            if (main.CurrentSettlement != null) LeaveSettlementAction.ApplyForParty(main);
            if (main.Ships.Count == 0)
            {
                var hull = Kingdom.All.Select(k => k.Culture).Concat(new[] { Hero.MainHero.Culture }).Where(c => c != null).SelectMany(c => c.AvailableShipHulls).First();
                int crew = Math.Max(0, new Ship(hull).SkeletalCrewCapacity - main.MemberRoster.TotalHealthyCount);
                if (crew > 0) main.MemberRoster.AddToCounts(Hero.MainHero.Culture.BasicTroop, crew, false, 0, 0, true, -1);
                ChangeShipOwnerAction.ApplyByLooting(PartyBase.MainParty, new Ship(hull));
                row["synthetic"] = "one culture ship hull and a skeletal crew of basic troops granted to the player";
            }
            // Natural storms add their own wind (samples near one are dropped): start at least 200 from every storm, else anywhere. The legs run
            // at normal play speed (SailingSpeed), about 1.5 campaign hours, so storms rarely reach the ship meanwhile.
            var start = SeaPoint(map, extent, 70f, 200f);
            row["startClearOfStorms"] = start.IsValid;
            if (!start.IsValid) start = SeaPoint(map, extent, 70f);
            if (!start.IsValid) { failures.Add("No open-sea point with open water 70 units each way to sail from."); yield break; }
            var boost = WindBoost();
            row["windBoostSource"] = boost == null ? null : boost.Item1.GetType().FullName + ".CalculateWindBoostForParty";
            string screenshot = null;
            // Downwind is along the x sign of the generated wind at the start (east on the uniform-east fixture), upwind the opposite way.
            float downX = map.GetWindAtPosition(start).X >= 0 ? 1f : -1f;
            row["windAtStart"] = new[] { map.GetWindAtPosition(start).X, map.GetWindAtPosition(start).Y };
            foreach (var leg in new[] { Tuple.Create(downX > 0 ? "downwind (east)" : "downwind (west)", downX), Tuple.Create(downX > 0 ? "upwind (west)" : "upwind (east)", -downX) })
            {
                bool first = leg.Item2 == downX;
                // The first leg sets sail at the start; the second sails back from where the first ended (setting sail again while at sea
                // leaves the party stationary). The row is open sea 70 units each way of the start.
                if (first) { main.SetSailAtPosition(new CampaignVec2(start, false)); main.SetMoveModeHold(); row["camera"] = CameraToMainParty(); }
                var target = new CampaignVec2(new Vec2(start.X + leg.Item2 * 60f, start.Y), false);
                var legStart = main.Position.ToVec2();
                var record = new Dictionary<string, object> { ["leg"] = leg.Item1, ["from"] = new[] { legStart.X, legStart.Y }, ["to"] = new[] { target.X, target.Y } }; row[first ? "downwind" : "upwind"] = record;
                main.SetMoveGoToPoint(target, MobileParty.NavigationType.Naval);
                var samples = new List<float[]>(); double begin = context.Seconds, lastSample = 0, startHours = CampaignTime.Now.ToHours; var from = main.Position.ToVec2();
                var previous = main.Position.ToVec2(); int stationary = 0, nearStorm = 0;
                boot.HoldPaused = false;
                DiagnosticSandboxBootstrap.Run(campaign, SailingSpeed);
                while (context.Seconds - begin < 6)
                {
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, SailingSpeed);
                    if (context.Seconds - lastSample >= .25 && context.Seconds - begin > 1 && main.IsCurrentlyAtSea)
                    {
                        lastSample = context.Seconds;
                        var here = main.Position.ToVec2(); bool moved = here.Distance(previous) > 1e-3f; previous = here;
                        if (!moved) stationary++;
                        else if (NearStorm(here)) nearStorm++;   // a storm adds its own wind
                        else
                        {
                            float measured = boost == null ? float.NaN : (float)boost.Item2.Invoke(boost.Item1, new object[] { main });
                            var wind = campaign.Models.MapWeatherModel.GetWindForPosition(main.Position);
                            samples.Add(new[] { main.Position.X, main.Position.Y, main.Speed, measured, wind.X, wind.Y, main.Bearing.X, main.Bearing.Y });
                        }
                    }
                    if (screenshot == null && first && context.Seconds - begin > 2.5 && context.Seconds - begin < 3) CameraToMainParty();   // the wake behind the ship
                    if (screenshot == null && first && context.Seconds - begin > 3)
                    {
                        screenshot = System.IO.Path.Combine(context.Directory, "water-climate-" + context.ProcessId + "-sailing.bmp");
                        Utilities.TakeScreenshot(screenshot); context.Outputs["waterClimateSailingScreenshot"] = screenshot;
                    }
                    yield return null;
                }
                DiagnosticSandboxBootstrap.Pause(campaign);
                boot.HoldPaused = true;
                double hours = CampaignTime.Now.ToHours - startHours;
                record["samples"] = samples.Take(40).ToList(); record["samplesTaken"] = samples.Count; record["samplesStationary"] = stationary; record["samplesNearStorm"] = nearStorm;
                record["meanSpeed"] = samples.Count == 0 ? float.NaN : samples.Average(s => s[2]);
                record["meanWindBoost"] = samples.Count == 0 ? float.NaN : samples.Average(s => s[3]);
                record["displacementPerHour"] = hours > 0 ? from.Distance(main.Position.ToVec2()) / hours : double.NaN;
                if (samples.Count < 5) failures.Add(leg.Item1 + ": too few samples at sea (" + samples.Count + ").");
            }
            main.SetMoveModeHold();
            if (screenshot != null) row["screenshot"] = screenshot;
            var down = (Dictionary<string, object>)row["downwind"]; var up = (Dictionary<string, object>)row["upwind"];
            if (!((float)down["meanWindBoost"] > (float)up["meanWindBoost"] + 0.02f)) failures.Add("The wind boost sailing downwind is not larger than upwind.");
            if (!((float)down["meanSpeed"] > (float)up["meanSpeed"])) failures.Add("The ship is not faster downwind than upwind.");
        }

        // MapScreen (SandBox.View, not referenced by this module).TeleportCameraToMainParty, so the screenshot shows the ship and its wake.
        private static string CameraToMainParty()
        {
            var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
            var method = screen == null ? null : screen.GetType().GetMethod("TeleportCameraToMainParty", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null) return screen == null ? "no screen" : "no TeleportCameraToMainParty on " + screen.GetType().FullName;
            method.Invoke(screen, null);
            return screen.GetType().FullName + ".TeleportCameraToMainParty";
        }

        private static bool NearStorm(Vec2 p)
        {
            var manager = NavalDLCManager.Instance == null ? null : NavalDLCManager.Instance.StormManager;
            return manager != null && manager.SpawnedStorms.Any(s => s.CurrentPosition.Distance(p) < 1.5f * s.EffectRadius);
        }

        private const CampaignTimeControlMode SailingSpeed = CampaignTimeControlMode.StoppablePlay;

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

        // ---- day and night ----
        // MapColorGradeManager.ApplyColorGrade (installed IL): the grade is the grid's under the camera (col = floor(x / terrain x * 512)), then
        // 1 above 400 units, then 2 (night) when TimeOfDay > 22 or < 2, then 160 (rain) when MBMapScene.GetApplyRainColorGrade() and the camera
        // is below 50, the last override winning; it is stored in lastColorGrade. Run to 01:00 and to 13:00, let the map tick, read it and take
        // a screenshot. Rain over the camera masks the night grade; the night check then records that instead.
        private static IEnumerator DayNight(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, GeneratedMapScene map, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var managers = GeneratedColorGrade.Managers(map.Scene);
            if (managers.Count != 1) { failures.Add(managers.Count + " colour-grade managers (one expected)."); yield break; }
            var manager = managers[0];
            row["camera"] = CameraToMainParty();
            foreach (var target in new[] { Tuple.Create("night", 1.0), Tuple.Create("day", 13.0) })
            {
                double begin = context.Seconds;
                boot.HoldPaused = false;
                DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                while (context.Seconds - begin < 60 && Math.Abs(CampaignTime.Now.CurrentHourInDay - target.Item2) > 0.4)
                {
                    boot.Tick();
                    if (boot.Failed) throw new InvalidOperationException(boot.Error);
                    if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                    yield return null;
                }
                DiagnosticSandboxBootstrap.Pause(campaign);
                boot.HoldPaused = true;
                CameraToMainParty();
                for (int frame = 0; frame < 30; frame++) yield return null;   // the map screen sets the scene's time; the manager ticks
                var camera = map.Scene.LastFinalRenderCameraFrame.origin;
                var size = (Vec2)GeneratedColorGrade.Field(manager, "terrainSize");
                float timeOfDay = (float)GeneratedColorGrade.Field(manager, "TimeOfDay");
                byte applied = (byte)GeneratedColorGrade.Field(manager, "lastColorGrade");
                var grid = GeneratedColorGrade.Grid(manager);
                int col = Math.Max(0, Math.Min(511, (int)Math.Floor(camera.x / size.x * 512))), gridRow = Math.Max(0, Math.Min(511, (int)Math.Floor(camera.y / size.y * 512)));
                bool rain = TaleWorlds.MountAndBlade.MBMapScene.GetApplyRainColorGrade();
                // ApplyColorGrade IL_007f..00cd: the grid's grade; 1 above 400; 2 at night; 160 in rain below 50 (the last override wins).
                int expected = grid[gridRow * 512 + col];
                if (camera.z > 400) expected = 1;
                if (timeOfDay > 22 || timeOfDay < 2) expected = 2;
                if (rain && camera.z < 50) expected = 160;
                string shot = System.IO.Path.Combine(context.Directory, "water-climate-" + context.ProcessId + "-" + target.Item1 + ".bmp");
                Utilities.TakeScreenshot(shot); context.Outputs["waterClimate" + (target.Item1 == "night" ? "Night" : "Day") + "Screenshot"] = shot;
                row[target.Item1] = new Dictionary<string, object> { ["campaignHour"] = CampaignTime.Now.CurrentHourInDay, ["managerTimeOfDay"] = timeOfDay, ["sceneTimeOfDay"] = map.Scene.TimeOfDay,
                    ["camera"] = new[] { camera.x, camera.y, camera.z }, ["gridGradeUnderCamera"] = grid[gridRow * 512 + col], ["rainGrade"] = rain, ["appliedGrade"] = applied, ["expectedGrade"] = expected, ["screenshot"] = shot };
                if (applied != expected) failures.Add(target.Item1 + ": the manager applied grade " + applied + ", not " + expected + ".");
            }
            var night = (Dictionary<string, object>)row["night"]; var day = (Dictionary<string, object>)row["day"];
            bool nightMasked = (bool)night["rainGrade"] && ((float[])night["camera"])[2] < 50;
            row["nightGradeMaskedByRain"] = nightMasked;
            if (!nightMasked && (byte)night["appliedGrade"] != 2) failures.Add("The night grade (2) is not applied at 01:00.");
            if ((byte)day["appliedGrade"] == 2) failures.Add("The night grade is still applied at 13:00.");
        }

        // ---- storms ----
        private static IEnumerator Storms(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, GeneratedMapScene map, PeriodicWorld world, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var c = world.Coordinates;
            var models = Property(typeof(NavalDLCManager), "GameModels").GetValue(NavalDLCManager.Instance);
            var model = Property(models.GetType(), "MapStormModel").GetValue(models) as MapStormModel;
            var ours = model as GeneratedStormModel;
            row["model"] = model == null ? null : model.GetType().FullName;
            row["baseModel"] = ours == null || ours.Installed == null ? null : ours.Installed.GetType().FullName;
            if (ours == null || ours.Installed == null) { failures.Add("The generated storm model is not War Sails' active storm model."); yield break; }
            row["policy"] = GeneratedStormPolicy.Record(c, c.Extent, campaign.DefaultWeatherNodeDimension);
            // Spawn gating over War Sails' own candidates.
            var behavior = campaign.GetCampaignBehavior<StormCampaignBehavior>();
            var candidates = ((IEnumerable)Field(typeof(StormCampaignBehavior), "_allOpenSeaWeatherNodePositions").GetValue(behavior)).Cast<Vec2>().ToList();
            int canonical = 0, gatedWrong = 0, scaledWrong = 0;
            foreach (var p in candidates)
            {
                bool can = p.X >= world.OriginX && p.X < world.MaxX && p.Y > c.OriginY && p.Y < c.OriginY + c.Height;   // canonical, between the pole lines
                float chance = ours.GetHourlyStormSpawnChanceForPosition(p), basic = ours.Installed.GetHourlyStormSpawnChanceForPosition(p);
                if (can) { canonical++; if (Math.Abs(chance - basic * ours.SpawnChanceScale) > 1e-6) scaledWrong++; }
                else if (chance != 0) gatedWrong++;
            }
            row["candidates"] = candidates.Count; row["canonicalCandidates"] = canonical; row["spawnChanceScale"] = ours.SpawnChanceScale;
            if (candidates.Count == 0 || canonical == 0) failures.Add("No open-sea storm candidates in the canonical range.");
            if (gatedWrong > 0 || scaledWrong > 0) failures.Add(gatedWrong + " strip or polar candidates may spawn storms; " + scaledWrong + " canonical chances are not the scaled War Sails chance.");
            // A storm next to the east seam: the generated wind blows east, so it drifts across.
            var manager = NavalDLCManager.Instance.StormManager;
            float y = float.NaN;
            for (float dy = 0; dy < c.Height / 2 && float.IsNaN(y); dy += 3.125f)
                foreach (float yy in new[] { (float)(c.OriginY + c.Height / 2 + dy), (float)(c.OriginY + c.Height / 2 - dy) })
                {
                    var p = new CampaignVec2(new Vec2(world.MaxX - 3f, yy), false);
                    if (p.Face.IsValid() && map.GetFaceTerrainType(p.Face) == TerrainType.OpenSea) { y = yy; break; }
                }
            if (float.IsNaN(y)) { failures.Add("No open sea at the east seam for the crossing storm."); yield break; }
            var spawnAt = new Vec2(world.MaxX - 3f, y);
            int before = manager.SpawnedStorms.Count, rebasesBefore = PeriodicStorms.Events.Count;
            manager.CreateStormAtPosition(spawnAt);
            var storm = manager.SpawnedStorms.LastOrDefault();
            if (storm == null || manager.SpawnedStorms.Count != before + 1) { failures.Add("CreateStormAtPosition did not add a storm."); yield break; }
            row["spawnedAt"] = new[] { spawnAt.X, spawnAt.Y };
            // A new storm develops in place (Storm.IsActive is false while developing; only an active storm moves) and then drifts with the
            // wind (desired direction the wind's, +-30 degrees).
            var track = new List<float[]>(); double begin = context.Seconds, lastSample = 0, createdHours = CampaignTime.Now.ToHours, activeAtHours = -1;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            Func<int> crossings = () => PeriodicStorms.Events.Skip(rebasesBefore).Count(e => ReferenceEquals(e.Storm, storm) && e.Shift != 0);
            while (context.Seconds - begin < 120 && crossings() == 0 && manager.SpawnedStorms.Contains(storm) && !storm.IsInFinalizingState)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                if (activeAtHours < 0 && storm.IsActive) activeAtHours = CampaignTime.Now.ToHours;
                if (context.Seconds - lastSample >= .2) { lastSample = context.Seconds; track.Add(new[] { storm.CurrentPosition.X, storm.CurrentPosition.Y, storm.IsActive ? 1f : 0f }); }
                yield return null;
            }
            row["developingHours"] = activeAtHours < 0 ? (object)null : activeAtHours - createdHours;
            row["waitedHours"] = CampaignTime.Now.ToHours - createdHours; row["waitedSeconds"] = context.Seconds - begin;
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            var events = PeriodicStorms.Events.Skip(rebasesBefore).Where(e => ReferenceEquals(e.Storm, storm) && e.Shift != 0).ToList();
            var position = storm.CurrentPosition;
            row["track"] = track.Take(200).ToList();
            row["rebases"] = events.Select(e => (object)new Dictionary<string, object> { ["kind"] = e.Kind, ["fromX"] = e.FromX, ["toX"] = e.ToX, ["y"] = e.Y, ["shift"] = e.Shift, ["trailPoints"] = e.TrailPointsShifted }).ToList();
            row["otherStormRebases"] = PeriodicStorms.Events.Skip(rebasesBefore).Count(e => !ReferenceEquals(e.Storm, storm));
            row["after"] = new Dictionary<string, object> { ["position"] = new[] { position.X, position.Y }, ["active"] = storm.IsActive, ["stillListed"] = manager.SpawnedStorms.Contains(storm),
                ["storms"] = manager.SpawnedStorms.Count, ["canonical"] = world.IsCanonical(position.X), ["effectRadius"] = storm.EffectRadius };
            if (events.Count == 0) { failures.Add("The storm did not cross the seam within 120 s (" + (storm.IsActive ? "active" : storm.IsInFinalizingState ? "finalizing" : "developing") + ")."); yield break; }
            if (!storm.IsActive || !manager.SpawnedStorms.Contains(storm) || !world.IsCanonical(position.X)) failures.Add("The storm that crossed the seam is ended, gone or not canonical.");
            // A duplicate would sit a whole width away: the periodic distance finds it.
            if (manager.SpawnedStorms.Count(s => world.Distance(s.CurrentPosition, position) < 1e-3f) != 1) failures.Add("The crossing storm exists more than once.");
            // Effects through the seam: a point just across the seam from the storm (planar far, periodic near) is damaged as its copy beside the storm.
            float offset = (position.X - world.OriginX) + 1f;
            var across = new Vec2(world.Canonical(position.X - offset), position.Y);
            var copy = new Vec2(position.X - offset, position.Y);
            var ship = MobileParty.MainParty.Ships.FirstOrDefault();
            float damageAcross = ours.GetPositionDamageForStorm(storm, across, ship), damageCopy = ours.Installed.GetPositionDamageForStorm(storm, copy, ship);
            float plainAcross = ours.Installed.GetPositionDamageForStorm(storm, across, ship);
            float windAcross = ours.GetNormalizedWindStrengthOfStormForPosition(across), windCopy = ours.Installed.GetNormalizedWindStrengthOfStormForPosition(copy);
            row["acrossSeam"] = new Dictionary<string, object> { ["point"] = new[] { across.X, across.Y }, ["copyBesideStorm"] = new[] { copy.X, copy.Y },
                ["periodicDistance"] = world.Distance(across, position), ["planarDistance"] = across.Distance(position),
                ["damage"] = damageAcross, ["damageAtCopy"] = damageCopy, ["planarDamage"] = plainAcross, ["windStrength"] = windAcross, ["windStrengthAtCopy"] = windCopy };
            if (Math.Abs(damageAcross - damageCopy) > 1e-5f || Math.Abs(windAcross - windCopy) > 1e-5f) failures.Add("The storm's damage or wind across the seam is not its value at the copy beside it.");
            if (!(windCopy > 0) || !(damageCopy > 0)) failures.Add("The point beside the storm takes no storm damage or wind (the check proves nothing).");
            var damagePass = DamagePass(campaign, world, storm, ours, failures);
            row["damagePass"] = damagePass;
            var reflected = PoleReflection(context, boot, campaign, world, manager, failures);
            while (reflected.MoveNext()) yield return reflected.Current;
            row["poleReflection"] = PoleRow;
        }

        // War Sails' own damage pass (StormCampaignBehavior.DamageNearbyParties: the locator search around the storm with the seam image pass,
        // then TryDamagingParty per ship) on the player's ship placed just across the seam from the storm, then beside it at the same distance.
        // OnShipDamaged may scale the model's damage, so each pass's hit-point loss is compared with the model's damage at the ship's position:
        // one application gives the same ratio on both sides (a doubled pass twice it, a missed pass 0).
        private static Dictionary<string, object> DamagePass(Campaign campaign, PeriodicWorld world, Storm storm, GeneratedStormModel model, List<string> failures)
        {
            var row = new Dictionary<string, object>();
            var main = MobileParty.MainParty; var ship = main.Ships.FirstOrDefault();
            var pass = typeof(StormCampaignBehavior).GetMethod("DamageNearbyParties", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (ship == null || pass == null) { failures.Add("The damage pass cannot run (" + (ship == null ? "no ship" : "no DamageNearbyParties") + ")."); return row; }
            var behavior = campaign.GetCampaignBehavior<StormCampaignBehavior>();
            float full = ship.HitPoints, delta = 3f;
            var storm0 = storm.CurrentPosition;
            foreach (var side in new[] { Tuple.Create("acrossSeam", world.Canonical(storm0.X - delta)), Tuple.Create("besideStorm", world.Canonical(storm0.X + delta)) })
            {
                ship.HitPoints = full;
                main.SetSailAtPosition(new CampaignVec2(new Vec2(side.Item2, storm0.Y), false)); main.SetMoveModeHold();
                var at = main.Position.ToVec2();
                float expected = model.GetPositionDamageForStorm(storm, at, ship);
                bool damageable = model.CanPartyGetDamagedByStorm(main);
                pass.Invoke(pass.IsStatic ? null : behavior, new object[] { storm });
                float loss = full - ship.HitPoints;
                row[side.Item1] = new Dictionary<string, object> { ["at"] = new[] { at.X, at.Y }, ["periodicDistance"] = world.Distance(at, storm.CurrentPosition), ["planarDistance"] = at.Distance(storm.CurrentPosition),
                    ["modelDamage"] = expected, ["hitPointLoss"] = loss, ["ratio"] = expected > 0 ? loss / expected : float.NaN, ["damageable"] = damageable };
            }
            ship.HitPoints = full;
            var a = (Dictionary<string, object>)row["acrossSeam"]; var b = (Dictionary<string, object>)row["besideStorm"];
            float ra = (float)a["ratio"], rb = (float)b["ratio"];
            if (!(bool)a["damageable"] || !((float)a["modelDamage"] > 0) || !((float)b["modelDamage"] > 0)) failures.Add("The damage pass proves nothing: the ship takes no storm damage.");
            else if (!(Math.Abs(ra - rb) <= 0.01f * Math.Max(1f, Math.Abs(rb))) || !((float)a["hitPointLoss"] > 0)) failures.Add("War Sails' damage pass across the seam is not one application (ratio " + ra + " against " + rb + " beside the storm).");
            return row;
        }

        private static Dictionary<string, object> PoleRow;

        // A storm placed 2 units past the south pole line (in the calm padding) is reflected back by the seam pass on the next campaign tick.
        private static IEnumerator PoleReflection(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, PeriodicWorld world, NavalDLC.Map.StormManager manager, List<string> failures)
        {
            PoleRow = new Dictionary<string, object>();
            var c = world.Coordinates;
            float south = (float)(c.OriginY + c.Height);
            var at = new Vec2(world.OriginX + world.Width / 2, south + 2f);
            int before = manager.SpawnedStorms.Count, eventsBefore = PeriodicStorms.Events.Count;
            manager.CreateStormAtPosition(at);
            var storm = manager.SpawnedStorms.Count == before + 1 ? manager.SpawnedStorms.Last() : null;
            if (storm == null) { failures.Add("CreateStormAtPosition did not add the polar storm."); yield break; }
            double begin = context.Seconds;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            while (context.Seconds - begin < 3 && !PeriodicStorms.Events.Skip(eventsBefore).Any(e => ReferenceEquals(e.Storm, storm)))
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            var reflection = PeriodicStorms.Events.Skip(eventsBefore).FirstOrDefault(e => ReferenceEquals(e.Storm, storm));
            var direction = (Vec2)typeof(Storm).GetField("_desiredMoveDirection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(storm);
            PoleRow["createdAt"] = new[] { at.X, at.Y }; PoleRow["southPoleLine"] = south;
            PoleRow["event"] = reflection == null ? null : new Dictionary<string, object> { ["kind"] = reflection.Kind, ["fromY"] = reflection.Y, ["toY"] = reflection.ToY };
            PoleRow["position"] = new[] { storm.CurrentPosition.X, storm.CurrentPosition.Y }; PoleRow["desiredDirection"] = new[] { direction.X, direction.Y };
            if (reflection == null || reflection.Kind != "pole" || Math.Abs(storm.CurrentPosition.Y - (south - 2f)) > 0.05f || direction.Y > 0)
                failures.Add("The storm past the south pole line was not reflected back with a northward direction.");
        }

        // ---- pirates ----
        private static IEnumerator Pirates(ProbeContext context, DiagnosticSandboxBootstrap boot, Campaign campaign, NavalContentFactory.NavalPlan plan, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"];
            var wrapper = GeneratedNavalMapSceneWrapper.Current;
            row["wrapperInstalled"] = wrapper != null && ReferenceEquals(NavalDLCManager.Instance.NavalMapSceneWrapper, wrapper);
            if (wrapper == null || !ReferenceEquals(NavalDLCManager.Instance.NavalMapSceneWrapper, wrapper)) { failures.Add("The generated naval map-scene wrapper is not War Sails' wrapper."); yield break; }
            var byClan = plan.PirateSpawnPoints.GroupBy(p => p.Clan).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            row["planPoints"] = byClan.ToDictionary(kv => kv.Key, kv => (object)kv.Value.Count);
            row["servedPoints"] = byClan.Keys.ToDictionary(k => k, k => (object)wrapper.GetSpawnPoints(k).Count);
            if (byClan.Any(kv => wrapper.GetSpawnPoints(kv.Key).Count != kv.Value.Count)) failures.Add("The wrapper does not serve the plan's points.");
            var behavior = campaign.GetCampaignBehavior<PiratesCampaignBehavior>();
            var zones = Field(typeof(PiratesCampaignBehavior), "_patrolZones").GetValue(behavior);
            int zoneCount = 0; foreach (var entry in (IEnumerable)zones) { var value = entry.GetType().GetProperty("Value"); zoneCount += value == null ? 1 : ((ICollection)value.GetValue(entry)).Count; }
            row["patrolZones"] = zoneCount;
            if (zoneCount != plan.PirateSpawnPoints.Count) failures.Add("War Sails built " + zoneCount + " patrol zones from " + plan.PirateSpawnPoints.Count + " generated spawn points.");
            // Let the daily spawn run.
            double begin = context.Seconds, startDays = CampaignTime.Now.ToDays;
            boot.HoldPaused = false;
            DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
            while (context.Seconds - begin < 40 && CampaignTime.Now.ToDays - startDays < 3)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (campaign.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(campaign, CampaignTimeControlMode.UnstoppableFastForward);
                yield return null;
            }
            DiagnosticSandboxBootstrap.Pause(campaign);
            boot.HoldPaused = true;
            row["campaignDays"] = CampaignTime.Now.ToDays - startDays;
            var pirates = MobileParty.All.Where(p => p.IsActive && p.ActualClan != null && byClan.ContainsKey(p.ActualClan.StringId)).ToList();
            var parties = new List<object>(); row["pirateParties"] = parties;
            int atGenerated = 0;
            foreach (var party in pirates)
            {
                var points = byClan[party.ActualClan.StringId];
                var target = party.TargetPosition.ToVec2();
                var nearest = points.OrderBy(p => new Vec2((float)p.X, (float)p.Y).Distance(target)).First();
                float d = new Vec2((float)nearest.X, (float)nearest.Y).Distance(target);
                bool patrolsGenerated = d < 1f;
                if (patrolsGenerated) atGenerated++;
                parties.Add(new Dictionary<string, object> { ["id"] = party.StringId, ["clan"] = party.ActualClan.StringId, ["position"] = new[] { party.Position.X, party.Position.Y },
                    ["patrolTarget"] = new[] { target.X, target.Y }, ["nearestGeneratedPoint"] = nearest.Uid, ["distanceToIt"] = d, ["atSea"] = party.IsCurrentlyAtSea });
            }
            row["partiesPatrollingGeneratedPoints"] = atGenerated;
            if (pirates.Count == 0) failures.Add("No pirate party spawned in " + row["campaignDays"] + " days.");
            else if (atGenerated != pirates.Count) failures.Add((pirates.Count - atGenerated) + " pirate parties do not patrol a generated spawn point.");
        }

        private static FieldInfo Field(Type owner, string name)
        {
            var f = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new MissingFieldException(owner.FullName, name);
            return f;
        }

        private static PropertyInfo Property(Type owner, string name)
        {
            var p = owner.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p == null) throw new MissingMemberException(owner.FullName, name);
            return p;
        }
    }
}
