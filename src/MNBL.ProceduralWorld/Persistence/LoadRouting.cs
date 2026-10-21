using System;
using System.Linq;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;
using TaleWorlds.SaveSystem.Load;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-49: the normal Load and Continue entries route a generated campaign's save to GeneratedWorldLoader. The installed entries (the load
    // screen's SavedGameVM.ExecuteSaveLoad, SandBoxViewSubModule.ContinueCampaign, PreloadScreen.OnFrameTick behind the main menu's Continue)
    // all call SandBoxSaveHelper.TryLoadSave(saveInfo, onStartGame, onCancel). It runs the installed checks and prompts (module mismatches, a
    // save from a newer version), then LoadGameAction deserializes the save and onStartGame starts the vanilla SandBoxGameManager(LoadResult),
    // which would read the official world's XML onto a generated save (installed 1.4.8 IL). So:
    //   * a prefix on LoadGameAction takes every save whose header carries a world binding (mnbl.world.id) to the loader instead and skips the
    //     installed method; the installed prompts before it are unchanged (their Yes calls LoadGameAction too);
    //   * a prefix on TryLoadSave ignores the repeated calls for a generated save (LoadPolicy.Routing): the preload screen calls TryLoadSave on
    //     every frame until a game starts, also while the installed prompt or a refusal is up;
    //   * a prefix on MBSaveLoad.LoadSaveGameData, applied first and version-agnostic, refuses a generated save to every caller but the
    //     loader (Deserialize): if the routing cannot be applied (another game version) or another path reaches the deserializer, a generated
    //     save is refused with a message instead of being read onto the official world.
    // Applied once per process at the main menu (SubModule), never removed.
    internal static class LoadRouting
    {
        public const string HarmonyId = "mnbl.proceduralworld.load", GuardId = "mnbl.proceduralworld.load-guard";
        private static Harmony harmony, guard;
        private static readonly System.Reflection.FieldInfo InquiryActive = AccessTools.Field(typeof(SandBox.SandBoxSaveHelper), "_isInquiryActive");
        private static int deserializing;
        public static int Routed { get; private set; }
        public static int Ignored { get; private set; }
        public static int Guarded { get; private set; }
        public static bool Applied { get { return harmony != null; } }
        public static bool GuardApplied { get { return guard != null; } }
        public static string ApplyError { get; private set; }
        public static string GuardError { get; private set; }
        // The preload state behind Continue this routing took a save from (a refusal's OK returns from it to the main menu).
        public static GameState RoutedFrom { get; private set; }

        // A Harmony entry (GeneratedPatches.Patched) of a process-lifetime patch of this class: never a campaign's leftover.
        public static bool IsProcessLifetime(string patched) { return patched.StartsWith(HarmonyId + ":", StringComparison.Ordinal) || patched.StartsWith(GuardId + ":", StringComparison.Ordinal); }

        public static void Apply()
        {
            if (guard == null)
                try
                {
                    var deserialize = CampaignRegistry.PatchSignatures.Method(typeof(MBSaveLoad).FullName, "LoadSaveGameData");   // MMR-284: by signature
                    var g = new Harmony(GuardId);
                    g.Patch(deserialize, prefix: new HarmonyMethod(typeof(LoadRouting), nameof(LoadSaveGameDataPrefix)));
                    guard = g;
                }
                catch (Exception error) { GuardError = error.Message; }   // the routing is still applied; the module log records both
            if (harmony != null) return;
            try
            {
                CampaignRegistry.GeneratedPatches.RequireSupportedBuild("load routing");
                // MMR-284: by signature (PatchSignatureException names the patch); the guard above refuses generated saves meanwhile.
                var helper = typeof(SandBox.SandBoxSaveHelper).FullName;
                var tryLoad = CampaignRegistry.PatchSignatures.Method(helper, "TryLoadSave");
                var loadAction = CampaignRegistry.PatchSignatures.Method(helper, "LoadGameAction");
                CampaignRegistry.PatchSignatures.Field(helper, "_isInquiryActive");
                var h = new Harmony(HarmonyId);
                h.Patch(tryLoad, prefix: new HarmonyMethod(typeof(LoadRouting), nameof(TryLoadSavePrefix)));
                h.Patch(loadAction, prefix: new HarmonyMethod(typeof(LoadRouting), nameof(LoadGameActionPrefix)));
                harmony = h;
            }
            catch (Exception error) { ApplyError = error.Message; throw; }
        }

        private static bool Generated(SaveGameFileInfo saveInfo) { return saveInfo != null && saveInfo.MetaData != null && saveInfo.MetaData.Keys.Contains(WorldBinding.Prefix + "id"); }

        // The installed checks and prompts run for a generated save once; its repeated calls are ignored.
        public static bool TryLoadSavePrefix(SaveGameFileInfo saveInfo)
        {
            bool generated = Generated(saveInfo);
            if (!generated) return true;
            var states = GameStateManager.Current;
            var active = states == null ? null : states.ActiveState;
            bool repeated = active is Sandbox.View.GameStates.PreloadState && ReferenceEquals(active, RoutedFrom);
            bool inquiry = InquiryActive != null && (bool)InquiryActive.GetValue(null);
            if (LoadPolicy.Routing(generated, inquiry, repeated) == LoadPolicy.Route.Ignore) { Ignored++; return false; }
            return true;
        }

        // A save of a generated world: never the installed deserialization and start.
        public static bool LoadGameActionPrefix(SaveGameFileInfo saveInfo, Action onCancel)
        {
            if (!Generated(saveInfo)) return true;
            var states = GameStateManager.Current;
            RoutedFrom = states == null ? null : states.ActiveState as Sandbox.View.GameStates.PreloadState;
            Routed++;
            GeneratedWorldLoader.Begin(saveInfo.Name, onCancel, s => { });
            return false;
        }

        // The loader's deserialization (and a diagnostic probe's), the only one a generated save is open to.
        public static T Deserialize<T>(Func<T> read)
        {
            deserializing++;
            try { return read(); }
            finally { deserializing--; }
        }

        public static bool LoadSaveGameDataPrefix(string __0, ref LoadResult __result)
        {
            if (deserializing > 0) return true;
            bool generated;
            try { var header = SaveFolder.ReadMetadata(BannerlordSaveCatalog.SavePath(__0)); generated = header != null && header.ContainsKey(WorldBinding.Prefix + "id"); }
            catch (Exception) { return true; }   // a header this reader cannot read is the installed load's to judge
            if (!generated) return true;
            Guarded++;
            __result = null;
            var text = new TaleWorlds.Localization.TextObject(Applied ? LoadPolicy.Texts.Routed : LoadPolicy.Texts.RoutedUnapplied);
            text.SetTextVariable("SAVE", __0 ?? "?"); text.SetTextVariable("REASON", ApplyError ?? "unknown");
            InformationManager.DisplayMessage(new InformationMessage(text.ToString()));
            return false;
        }
    }
}
