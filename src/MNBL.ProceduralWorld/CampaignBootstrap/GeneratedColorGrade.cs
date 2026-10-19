using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using TaleWorlds.Engine;
using MNBL.ProceduralWorld.CampaignRegistry;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-39: the colour grade of a generated campaign map (GeneratedClimateSystem, docs/water-climate.md). The installed MapColorGradeManager
    // (TaleWorlds.MountAndBlade.View, a script on a scene entity) reads the worldmap_colorgrade_grid texture into its private 512 x 512 grid
    // in Init and every tick applies the grade under the camera (col = floor(x / extent * 512)); ColorGradeEnabled is honoured only in the
    // editor. That texture is Calradia's regions. While a generated map scene is loaded, a postfix on Init copies the world's grid over it:
    // its generated grid, or with none bound (a diagnostic climate, a world whose climate is not yet bound) a neutral grid, every cell 0,
    // which the manager maps to its default grade. Calradia's regions never apply. Patched before the scene read (GeneratedMapScene.Load),
    // removed when that scene is destroyed; managers that initialized before the patch are replaced in PrepareWeather. The manager type is
    // resolved by name: this module does not reference the view assembly.
    internal static class GeneratedColorGrade
    {
        public const string HarmonyId = "mnbl.proceduralworld.colorgrade";
        public const string ManagerType = "TaleWorlds.MountAndBlade.View.Scripts.MapColorGradeManager";
        public const int Cells = 512 * 512;
        private static Harmony harmony;
        private static byte[] grid;
        private static object owner;
        public static int Replaced { get; private set; }
        public static string LastError { get; internal set; }
        public static bool Applied { get { return harmony != null; } }
        public static bool Neutral { get; private set; }
        public static string GridSha256 { get { return grid == null ? null : GeneratedClimate.Sha256(grid); } }

        // generated: the world's grid (Cells bytes), or null for the neutral grid. A previous patch (an owner that never reached its End) is
        // removed first.
        internal static void Begin(byte[] generated, object scene)
        {
            if (scene == null) throw new ArgumentNullException("scene");
            if (generated != null && generated.Length != Cells) throw new InvalidDataException("A colour-grade grid is " + Cells + " bytes, not " + generated.Length + ".");
            Remove();
            LastError = null; Replaced = 0;
            GeneratedPatches.RequireInstalledVersion("colour-grade");
            var type = AccessTools.TypeByName(ManagerType);
            var init = type == null ? null : AccessTools.Method(type, "Init");
            if (init == null || AccessTools.Field(type, "colorGradeGrid") == null) throw new InvalidOperationException("The colour-grade patch target is not the installed " + GeneratedPatches.GameVersion + " MapColorGradeManager.");
            Neutral = generated == null;
            grid = generated == null ? new byte[Cells] : (byte[])generated.Clone();
            harmony = new Harmony(HarmonyId);
            harmony.Patch(init, postfix: new HarmonyMethod(typeof(GeneratedColorGrade), nameof(InitPostfix)));
            owner = scene;
        }

        // Only the scene that began the patch ends it.
        internal static bool End(object scene)
        {
            if (scene == null || !ReferenceEquals(scene, owner)) return false;
            Remove();
            return true;
        }

        private static void Remove()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null; grid = null; owner = null;
        }

        // An exception escaping a script's Init (an engine callback) ends the process without a managed record: recorded instead.
        private static void InitPostfix(object __instance)
        {
            try { Replace(__instance); }
            catch (Exception error) { LastError = error.ToString(); }
        }

        internal static bool Replace(object manager)
        {
            if (grid == null || manager == null) return false;
            var target = Grid(manager);
            if (target == null || target.Length != grid.Length) throw new InvalidDataException("The colour-grade manager's grid is not " + grid.Length + " bytes.");
            Buffer.BlockCopy(grid, 0, target, 0, grid.Length);
            Replaced++;
            return true;
        }

        internal static byte[] Grid(object manager)
        {
            var field = manager == null ? null : AccessTools.Field(manager.GetType(), "colorGradeGrid");
            return field == null ? null : (byte[])field.GetValue(manager);
        }

        internal static object Field(object manager, string name)
        {
            var field = manager == null ? null : AccessTools.Field(manager.GetType(), name);
            return field == null ? null : field.GetValue(manager);
        }

        // The scene's colour-grade managers (scripts of the installed type on its entities).
        internal static List<object> Managers(Scene scene)
        {
            var entities = new List<GameEntity>();
            scene.GetEntities(ref entities);
            return entities.SelectMany(e => e.GetScriptComponents()).Where(s => s != null && s.GetType().FullName == ManagerType).Cast<object>().ToList();
        }

        internal static int ReplaceInScene(Scene scene)
        {
            int count = 0;
            foreach (var manager in Managers(scene)) if (Replace(manager)) count++;
            return count;
        }
    }
}
