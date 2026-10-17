using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-48: binds every later save of a generated campaign (Save As, quick save, autosave, Ironman save) to its exact world through the
    // shared journal (SaveJournal, MMR-62's initial-save journal reused), and records the build that wrote it in the header.
    //   The binding: the header's mnbl.world.* keys (WorldIdentityBehavior, every save mode) name the world package, its manifest hash, the
    //     campaign instance and the data bundle's content id; the mnbl.save.* keys (SaveBindingContract) the contract, module, game, War Sails,
    //     backend, navigation cache and save id versions. Both are header strings read at the main menu before anything deserializes, so a
    //     save is found and identified by its contents, never by its file name: an autosave slot, a Save As copy, a quick save over an older
    //     file and a file the player renamed or copied all carry their binding (no sidecar to name or keep in step).
    //   The journal: the installed SaveHandler.SaveTick runs every save mode through MBSaveLoad.OverwriteSaveAux with its final name
    //     (SaveAsCurrentGame: the given name or, in Ironman, IronmanModSaveName; QuickSaveCurrentGame: ActiveSaveSlotName or the next free
    //     "saveNNN"; AutoSaveCurrentGame: "saveauto1..3"), before Game.Save writes the file (installed 1.4.8 IL). No event carries the name of a
    //     quick save or an autosave before the write (OnBeforeSave and the metadata event run before MBSaveLoad picks it), so an observe-only
    //     Harmony prefix there writes the intent (kind from SaveHandler's queued SaveArgs.Mode), then the save runs unchanged. OnSaveOver
    //     (CampaignEvents.OnSaveOverEvent, after the AsyncFileSaveDriver's write, or with an empty name when the game refused the save)
    //     completes it by readback. The initial save is skipped here: its publication (InitialSavePublisher) owns its journal.
    //   A save that cannot be journaled (no store lock in this process, a write error) still runs and still carries its binding; the failure is
    //     recorded in Problems. A crash during its write then leaves at worst an unreadable or torn file, which the reference index reports as a
    //     gap (it fails closed) until the player removes it; a journaled save's torn write is removed at the next start.
    // Applied for a generated campaign's lifetime (GeneratedGameManager, with the world identity), removed when it ends.
    internal static class WorldSaveBinding
    {
        public const string HarmonyId = "mnbl.proceduralworld.saves";
        private const int ProblemsKept = 20;
        private static Harmony harmony;
        private static WorldIdentityBehavior identity;
        private static SaveOperation pending;
        private static Action<string> trace = s => { };
        private static Dictionary<string, string> compatibility;
        private static FieldInfo saveArgsQueue, saveArgsMode;

        public static SaveOperation Last { get; private set; }
        public static double LastReadbackSeconds { get; private set; }
        public static int Journaled { get; private set; }
        public static readonly List<string> Problems = new List<string>();
        public static bool Attached { get { return harmony != null && identity != null; } }

        public static void Attach(WorldIdentityBehavior behavior, Action<string> traceSink)
        {
            if (behavior == null) throw new ArgumentNullException("behavior");
            GeneratedPatches.RequireInstalledVersion("save binding");
            foreach (var entry in Compatibility()) behavior.SaveEntries[entry.Key] = entry.Value;
            if (harmony == null)
            {
                var target = AccessTools.Method(typeof(MBSaveLoad), "OverwriteSaveAux");
                saveArgsQueue = AccessTools.Field(typeof(SaveHandler), "SaveArgsQueue");
                if (target == null || saveArgsQueue == null) throw new InvalidOperationException("The save binding targets are not the installed " + GeneratedPatches.GameVersion + " members.");
                harmony = new Harmony(HarmonyId);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(WorldSaveBinding), nameof(OverwritePrefix)));
            }
            identity = behavior; pending = null; trace = traceSink ?? (s => { });
            trace("save_binding_attached");
        }

        // An intent left outstanding stays on disk for the next start's recovery.
        public static void Detach()
        {
            identity = null; pending = null;
            if (harmony != null) { harmony.UnpatchAll(HarmonyId); harmony = null; trace("save_binding_detached"); }
            trace = s => { };
        }

        // The build's mnbl.save.* entries, computed once per process.
        public static Dictionary<string, string> Compatibility()
        {
            if (compatibility != null) return compatibility;
            var module = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo(MNBL.ProceduralWorld.Surface.RuntimeWorldBackend.ModuleId);
            var naval = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo("NavalDLC");
            string assembly = typeof(WorldSaveBinding).Assembly.Location;
            string producerLock;
            try { producerLock = UI.WorldSetupContract.Installed().ProducerLockSha256; } catch (Exception) { producerLock = "unknown"; }
            compatibility = SaveBindingContract.Compatibility(
                module == null ? "unknown" : module.Version.ToString(),
                File.Exists(assembly) ? WorldSaveTransfer.Sha256File(assembly) : "unknown",
                TaleWorlds.Engine.Utilities.GetApplicationVersionWithBuildNumber().ToString(),
                naval == null ? "absent" : naval.Version.ToString(),
                "mnbl.runtime-world-backend/R07; producerLock " + (producerLock.Length > 12 ? producerLock.Substring(0, 12) : producerLock),
                MNBL.ProceduralWorld.Surface.NavigationCacheKey.Schema + "/" + MNBL.ProceduralWorld.Surface.NavigationCacheKey.Version.ToString(CultureInfo.InvariantCulture) + "; adapter " + MNBL.ProceduralWorld.Surface.WorldNavigationCache.Adapter,
                SaveIds.Base.ToString(CultureInfo.InvariantCulture) + "/v" + SaveIds.Version.ToString(CultureInfo.InvariantCulture),
                WorldIdentity.Schema);
            return compatibility;
        }

        // Harmony prefix on MBSaveLoad.OverwriteSaveAux(CampaignSaveMetaDataArgs, string saveName, Action<(SaveResult, string)>). Never throws.
        public static void OverwritePrefix(string saveName)
        {
            var behavior = identity;
            if (behavior == null || Campaign.Current == null) return;
            try
            {
                // Only the campaign it was attached to: a start or load that failed before OnGameEnd must not journal the next campaign's saves
                // under the old world (review finding).
                if (Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>() != behavior) return;
                var binding = BindingOf(behavior);
                if (binding == null || binding.Legacy) return;
                var initial = SaveBindingRuntime.Store.Journal(binding.Instance);
                if (initial != null && initial.State == EarlyWorldBindingStore.Intent && initial.SaveName == saveName) return;   // the initial save's own publication
                if (pending != null) Complete(false, null, "a new save began before the engine reported " + pending.SaveName);
                pending = SaveBindingRuntime.Later.Begin(binding, KindOf(Campaign.Current), saveName);
                Journaled++;
                trace("save_journal_intent:" + pending.Kind + ":" + saveName);
            }
            catch (Exception error) { Problem("the save " + saveName + " was not journaled: " + error.GetType().Name + ": " + error.Message); }
        }

        // CampaignEvents.OnSaveOverEvent (WorldIdentityBehavior listens). Never throws.
        public static void OnSaveOver(bool success, string name)
        {
            if (pending == null) return;
            Complete(success, name, null);
        }

        private static void Complete(bool success, string name, string why)
        {
            var operation = pending;
            pending = null;
            var watch = Stopwatch.StartNew();
            try
            {
                Last = SaveBindingRuntime.Later.Complete(operation.Id, success, name ?? "");
                trace("save_journal_" + Last.State + ":" + Last.Kind + ":" + Last.SaveName + (why == null ? "" : ":" + why));
            }
            catch (Exception error)
            {
                try { Last = SaveBindingRuntime.Later.Journal(operation.Id); } catch (Exception) { Last = operation; }
                Problem("the save " + operation.SaveName + " was not resolved (retried at the next start): " + error.GetType().Name + ": " + error.Message);
            }
            LastReadbackSeconds = watch.Elapsed.TotalSeconds;
        }

        public static WorldBinding BindingOf(WorldIdentityBehavior behavior)
        {
            if (behavior.Entries.Count == 0) return null;
            return WorldBinding.FromHeader(behavior.Entries.ToDictionary(e => WorldIdentityBehavior.Prefix + e.Key, e => e.Value, StringComparer.Ordinal));
        }

        // The kind of the save SaveTick is running: Ironman, else its queued SaveArgs.Mode (SaveAs 0, QuickSave 1, AutoSave 2; installed IL).
        private static string KindOf(Campaign campaign)
        {
            if (CampaignOptions.IsIronmanMode) return SaveJournal.Ironman;
            var queue = saveArgsQueue.GetValue(campaign.SaveHandler) as IEnumerable;
            object head = queue == null ? null : queue.Cast<object>().FirstOrDefault();
            if (head == null) throw new InvalidOperationException("No save is queued.");
            if (saveArgsMode == null) saveArgsMode = AccessTools.Field(head.GetType(), "Mode");
            switch (Convert.ToInt32(saveArgsMode.GetValue(head), CultureInfo.InvariantCulture))
            {
                case 0: return SaveJournal.SaveAs;
                case 1: return SaveJournal.QuickSave;
                case 2: return SaveJournal.AutoSave;
                default: throw new InvalidOperationException("Unknown save mode " + saveArgsMode.GetValue(head) + ".");
            }
        }

        private static void Problem(string text)
        {
            lock (Problems) { Problems.Add(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + text); while (Problems.Count > ProblemsKept) Problems.RemoveAt(0); }
            trace("save_binding_problem:" + text);
        }
    }
}
