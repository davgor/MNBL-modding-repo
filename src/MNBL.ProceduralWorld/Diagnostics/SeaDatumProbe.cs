using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-25: where does the engine put the sea on a generated campaign map? The terrain side is fixed by the receipts
    // (sea faces where source height < sea level, sea triangles clamped to z = 0), but the generated scenes have no
    // medit_water_plane and water_exists=false, yet water renders. This samples the native water level and the terrain
    // height over a grid of navmesh faces (seaDatumProbe=1). Fail-soft: every sample records its own error.
    internal static class SeaDatumProbe
    {
        private static readonly HashSet<TerrainType> Water = new HashSet<TerrainType> { TerrainType.Water, TerrainType.Lake, TerrainType.River,
            TerrainType.Fording, TerrainType.CoastalSea, TerrainType.OpenSea };

        internal static Dictionary<string, object> Capture(ProbeContext context, GeneratedMapScene map)
        {
            var r = new Dictionary<string, object>(); var failures = new List<string>(); r["failures"] = failures;
            var scene = map.Scene; var size = map.GetTerrainSize();
            r["terrainSize"] = new[] { size.X, size.Y };
            Try(r, failures, "hasWaterPlaneEntity", () => scene.GetFirstEntityWithName("medit_water_plane") != null);
            context.Checkpoint();
            Try(r, failures, "sceneWaterLevel", () => scene.GetWaterLevel());
            context.Checkpoint();
            int dim = context.Int("seaDatumGrid", 24);
            var samples = new List<Dictionary<string, object>>(); r["samples"] = samples;
            for (int i = 0; i < dim; i++)
                for (int j = 0; j < dim; j++)
                {
                    var p = new Vec2((i + .5f) / dim * size.X, (j + .5f) / dim * size.Y);
                    var s = new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y }; samples.Add(s);
                    Try(s, failures, "terrain", () => {
                        var face = map.GetFaceIndex(new CampaignVec2(p, false));
                        return face.IsValid() ? map.GetFaceTerrainType(face).ToString() : "none";
                    });
                    Try(s, failures, "terrainHeight", () => scene.GetTerrainHeight(p));
                    Try(s, failures, "waterRenderer", () => scene.GetWaterLevelAtPosition(p, true, false));
                    Try(s, failures, "waterBodies", () => scene.GetWaterLevelAtPosition(p, false, true));
                }
            context.Checkpoint();
            var summary = new Dictionary<string, object>(); r["summary"] = summary;
            foreach (var group in samples.Where(s => s.ContainsKey("terrain") && s.ContainsKey("terrainHeight")).GroupBy(s => Classify((string)s["terrain"])))
            {
                var g = new Dictionary<string, object> { ["samples"] = group.Count() }; summary[group.Key] = g;
                Range(g, "terrainHeight", group.Select(s => (float)s["terrainHeight"]));
                foreach (string key in new[] { "waterRenderer", "waterBodies" })
                {
                    var both = group.Where(s => s.ContainsKey(key)).ToList();
                    Range(g, key, both.Select(s => (float)s[key]));
                    Range(g, key + "MinusTerrain", both.Select(s => (float)s[key] - (float)s["terrainHeight"]));
                }
            }
            return r;
        }

        private static string Classify(string terrain)
        {
            if (terrain == "none") return "noFace";
            TerrainType type;
            if (!Enum.TryParse(terrain, out type)) return "unknown";
            return type == TerrainType.OpenSea ? "openSea" : type == TerrainType.CoastalSea ? "coastalSea" : Water.Contains(type) ? "otherWater" : "land";
        }

        private static void Range(Dictionary<string, object> row, string name, IEnumerable<float> values)
        {
            var list = values.Where(v => !float.IsNaN(v) && !float.IsInfinity(v)).ToList();
            row[name] = list.Count == 0 ? null : new Dictionary<string, object> { ["min"] = list.Min(), ["max"] = list.Max(), ["mean"] = list.Average(), ["count"] = list.Count };
        }

        private static void Try(Dictionary<string, object> row, List<string> failures, string name, Func<object> read)
        {
            try { row[name] = read(); }
            catch (Exception e) { row[name + "Error"] = e.GetType().Name + ": " + e.Message; if (failures.Count < 20) failures.Add(name + ": " + e.Message); }
        }
    }
}
