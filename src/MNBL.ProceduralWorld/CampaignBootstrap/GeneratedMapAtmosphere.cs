using System;
using System.Collections.Generic;
using System.Xml;
using MNBL.ProceduralWorld.World;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-256: the campaign map's atmosphere, season threshold and colour grade on a generated world (worldgen/policies/climate-flow.json
    // colorGrade.runtime, docs/water-climate.md). The installed MapColorGradeManager does this on Calradia's map, but a production scene carries
    // none: its Init reads the worldmap_colorgrade_grid texture natively (MBMapScene.GetColorGradeGridData), an inspection read of a scene
    // carrying one hung (run 15744), and a runtime one hung a non-map scene (MMR-249). So this does the manager's OnTick itself, from the
    // installed IL (TaleWorlds.MountAndBlade.View 1.4.8), on every application tick while the generated campaign map is the top screen:
    //   * ApplyAtmosphere: MBMapScene.SetFrameForAtmosphere(scene, TimeOfDay x 10, camera z, false), then the terrain's dynamic snow threshold
    //     MBMapScene.SetTerrainDynamicParams(scene, (Lerp(0.55, -0.1, season), 0.65, 0)), season = MBMapScene.GetSeasonTimeFactor(scene) (the
    //     map screen sets it every frame). With the world's snow texture bound (GeneratedSnowTexture) this whitens the regions FWGC++ snows on
    //     as winter comes; without it (the permanent-white route) only the permanent-white layer is white;
    //   * ApplyColorGrade: the world's grade under the camera (row * 512 + col, col = floor(x / terrain x * 512)), 1 above z 400, the night
    //     grade 2 from 22:00 to 02:00, the rain grade 160 below z 50 while MBMapScene.GetApplyRainColorGrade; a change blends from the last
    //     grade's texture over 1 s (0.2 s for rain) with Scene.SetColorGradeBlend; the grade textures are worldmap_color_grades.xml's
    //     (MBObjectManager.GetMergedXmlForNative("soln_worldmap_color_grades")): 1 the default, 2 the night, each color_grade its value;
    //   * a scene that does carry a manager (a fixture slot) is left to it (GeneratedColorGrade replaces its grid).
    // The grid is the world's (GeneratedClimate) with live per-cell overrides (SetGradeCell: MMR-260's colour-grade cells of a mutation).
    // Begin in GeneratedMapScene.AfterLoad, End in GeneratedMapScene.Destroy. No exception escapes Tick (an engine callback's caller): the
    // first failure stops the driver and is recorded.
    internal static class GeneratedMapAtmosphere
    {
        public const int Dimension = 512;
        private static Scene scene;
        private static object owner;
        private static byte[] baseGrid, grid;
        private static Vec2 terrainSize;
        private static readonly Dictionary<byte, string> textures = new Dictionary<byte, string>();
        private static string defaultTexture;
        private static byte lastGrade;
        private static Blend primary, secondary;
        private static Action<string> trace;
        private static Dictionary<string, object> record;
        private static double loggedSeason = -1;
        private sealed class Blend { public string From, To; public float Alpha; }
        public static bool Active { get { return scene != null; } }
        public static int Ticks { get; private set; }
        public static string LastError { get; private set; }
        public static float LastThreshold { get; private set; }
        public static float LastSeason { get; private set; }
        public static int Overrides { get; private set; }

        // The map scene, its world grid (512 x 512 grade bytes, or null: the neutral grid, every cell the default grade) and the snow
        // texture record of its read. supportReport["climateVisuals"] is the record the map load logs (and the first tick completes).
        internal static void Begin(object map, Scene mapScene, byte[] worldGrid, Dictionary<string, object> snowTexture, Dictionary<string, object> supportReport, Action<string> log)
        {
            End(owner);
            record = new Dictionary<string, object> { ["snowTexture"] = snowTexture, ["driver"] = "GeneratedMapAtmosphere (MapColorGradeManager.OnTick from IL)" };
            if (supportReport != null) supportReport["climateVisuals"] = record;
            trace = log; LastError = null; Ticks = 0; Overrides = 0; loggedSeason = -1; primary = secondary = null; lastGrade = 0;
            if (mapScene == null) { record["status"] = "no scene"; return; }
            if (worldGrid != null && worldGrid.Length != Dimension * Dimension) throw new ArgumentException("A colour-grade grid is " + Dimension * Dimension + " bytes.");
            if (GeneratedColorGrade.Managers(mapScene).Count > 0) { record["status"] = "manager: the scene's MapColorGradeManager drives it (GeneratedColorGrade binds its grid)"; return; }
            baseGrid = worldGrid == null ? new byte[Dimension * Dimension] : (byte[])worldGrid.Clone();
            grid = (byte[])baseGrid.Clone();
            Vec2i nodes; float nodeSize; int layers, version;
            mapScene.GetTerrainData(out nodes, out nodeSize, out layers, out version);
            terrainSize = new Vec2(nodes.X * nodeSize, nodes.Y * nodeSize);
            ReadGrades();
            scene = mapScene; owner = map;
            record["status"] = "driving"; record["grid"] = worldGrid == null ? "neutral" : "generated"; record["gradeTextures"] = textures.Count;
            record["terrainSize"] = new[] { terrainSize.X, terrainSize.Y };
        }

        // Only the map that began it ends it.
        internal static bool End(object map)
        {
            if (map == null || !ReferenceEquals(map, owner)) return false;
            scene = null; owner = null; baseGrid = grid = null; primary = secondary = null; trace = null;
            return true;
        }

        // MMR-260: a cell's grade (index row * 512 + col over the terrain) while a mutation holds it; ClearGradeCell gives back the world's.
        internal static void SetGradeCell(int index, byte grade) { if (grid == null || index < 0 || index >= grid.Length) return; grid[index] = grade; Overrides++; }
        internal static void ClearGradeCell(int index) { if (grid == null || index < 0 || index >= grid.Length) return; grid[index] = baseGrid[index]; }
        internal static byte GradeCell(int index) { return grid == null || index < 0 || index >= grid.Length ? (byte)0 : grid[index]; }

        private static void ReadGrades()
        {
            textures.Clear(); defaultTexture = "worldmap_colorgrade_stratosphere";
            List<string> used;
            var doc = MBObjectManager.GetMergedXmlForNative("soln_worldmap_color_grades", out used);
            var root = doc == null ? null : doc.SelectSingleNode("worldmap_color_grades");
            if (root == null) { record["gradeError"] = "worldmap_color_grades.xml was not found"; return; }
            var def = root.SelectSingleNode("color_grade_default");
            if (def != null && def.Attributes["name"] != null) defaultTexture = def.Attributes["name"].Value;
            textures[1] = defaultTexture; textures[2] = "worldmap_colorgrade_night";
            var night = root.SelectSingleNode("color_grade_night");
            if (night != null && night.Attributes["name"] != null) textures[2] = night.Attributes["name"].Value;
            foreach (XmlNode node in root.SelectNodes("color_grade"))
            {
                byte value;
                if (node.Attributes["name"] != null && node.Attributes["value"] != null && Byte.TryParse(node.Attributes["value"].Value, out value)) textures[value] = node.Attributes["name"].Value;
            }
        }

        // The application tick (SubModule.OnApplicationTick).
        internal static void Tick(float dt)
        {
            if (scene == null) return;
            try
            {
                var screen = ScreenManager.TopScreen;
                if (!CampaignMapScreen.Is(screen)) return;   // MapScreen or War Sails' NavalMapScreen (MMR-247)
                float time = MBMath.ClampFloat(scene.TimeOfDay, 0, 23.99f), season = MBMath.ClampFloat(MBMapScene.GetSeasonTimeFactor(scene), 0, 1);
                var camera = scene.LastFinalRenderCameraFrame.origin;
                MBMapScene.SetFrameForAtmosphere(scene, time * 10, camera.z, false);
                float threshold = MBMath.Lerp(0.55f, -0.1f, season, 1E-05f);
                MBMapScene.SetTerrainDynamicParams(scene, new Vec3(threshold, 0.65f, 0f));
                LastThreshold = threshold; LastSeason = season;
                ApplyColorGrade(dt, camera, time);
                Ticks++;
                // The map load's log row (both bindings) on the first tick, then a row each quarter of the season factor.
                if (loggedSeason < 0 || Math.Abs(season - loggedSeason) >= .25)
                {
                    loggedSeason = season;
                    record["seasonThreshold"] = new Dictionary<string, object> { ["season"] = season, ["threshold"] = threshold, ["blizzard"] = 0.65f, ["ticks"] = Ticks };
                    var snow = record["snowTexture"] as Dictionary<string, object>;
                    object name = null, status = null, resolves = null;
                    if (snow != null) { snow.TryGetValue("name", out name); snow.TryGetValue("status", out status); snow.TryGetValue("resolvesByName", out resolves); }
                    if (trace != null) trace("climate_visuals:snow texture " + (name ?? "none") + " " + (status ?? "unknown") + " resolvesByName " + (resolves ?? "n/a") +
                        ":season " + season.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " threshold " + threshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ":grade " + lastGrade);
                }
            }
            catch (Exception error)
            {
                LastError = error.ToString();
                if (record != null) record["error"] = error.GetType().Name + ": " + error.Message;
                try { if (trace != null) trace("climate_visuals_failed:" + error.ToString().Replace("\r", "").Replace("\n", " | ")); } catch (Exception) { }
                scene = null;
            }
        }

        private static void ApplyColorGrade(float dt, Vec3 camera, float time)
        {
            float speed = 1;
            int col = MBMath.ClampIndex(MathF.Floor(camera.x / terrainSize.X * Dimension), 0, Dimension), row = MBMath.ClampIndex(MathF.Floor(camera.y / terrainSize.Y * Dimension), 0, Dimension);
            byte grade = grid[row * Dimension + col];
            if (camera.z > 400) grade = 1;
            if (time > 22 || time < 2) grade = 2;
            if (MBMapScene.GetApplyRainColorGrade() && camera.z < 50) { grade = 160; speed = .2f; }
            if (grade != lastGrade)
            {
                string from, to;
                if (!textures.TryGetValue(lastGrade, out from)) from = defaultTexture;
                if (!textures.TryGetValue(grade, out to)) to = defaultTexture;
                if (primary == null) primary = new Blend { From = from, To = to, Alpha = 0 };
                else secondary = new Blend { From = primary.To, To = to, Alpha = 0 };
                lastGrade = grade;
            }
            if (primary == null) return;
            if (primary.Alpha < 1)
            {
                primary.Alpha = Math.Min(1, primary.Alpha + dt * (1 / speed));
                scene.SetColorGradeBlend(primary.From, primary.To, primary.Alpha);
                return;
            }
            primary = secondary; secondary = null;
        }
    }
}
