using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: the binding store for this game process, the store lock, and the startup pass that runs once at the main menu before the player
    // can open the save list (SubModule.OnBeforeInitialModuleScreenSetAsRoot, which runs before the initial screen handles /continuegame):
    // take the store lock, recover interrupted initial-save publications, then (MMR-45) the activations they belong to, remove abandoned
    // staging, and build the reference index. The lock
    // is held for the process's lifetime. A process without it recovers and removes nothing, and may not write to the store either:
    // RequireWriter guards the generation coordinator (its Admission), the encoder staging (RuntimeWorldBackend.Prepare), the package writer
    // (GeneratedWorldPackage.Write) and the initial save (EarlyWorldBindingStore.Begin). The pass never throws into the game; its report
    // (with the index summarized) is kept in Startup and written to <store>/saves/startup-<pid>.json, keeping the newest ten.
    internal static class SaveBindingRuntime
    {
        private const int StartupReportsKept = 10;
        private static EarlyWorldBindingStore store;
        private static SaveJournal later;
        private static StoreLock storeLock;
        private static readonly object gate = new object();

        public static Dictionary<string, object> Startup { get; private set; }

        public static EarlyWorldBindingStore Store
        {
            get
            {
                if (store == null) store = new EarlyWorldBindingStore(SubModule.Store, new BannerlordSaveCatalog(), PackagePublished, EnsureLock);
                return store;
            }
        }

        // MMR-48: the later saves' journal (Save As, quick save, autosave, Ironman), the initial-save journal's rules reused.
        public static SaveJournal Later
        {
            get
            {
                if (later == null) later = new SaveJournal(SubModule.Store, Store.Saves, BannerlordSaveCatalog.Stat, EnsureLock);
                return later;
            }
        }

        // Takes the store lock if no process holds it; true while this process holds it.
        public static bool EnsureLock()
        {
            lock (gate)
            {
                if (storeLock == null) storeLock = StoreLock.TryAcquire(SubModule.Store);
                return storeLock != null && storeLock.Held;
            }
        }

        // MMR-47: why the store is not the verified per-user data root (null when it is): %LOCALAPPDATA%\MNBL.ProceduralWorld, outside the game
        // installation and Steam libraries, with no link on its path. Nothing is cleaned or discarded under a root that does not verify.
        public static string RootProblem()
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Generation.WorldRecoveryService.StoreFolder);
            string bin = Path.GetDirectoryName(typeof(TaleWorlds.Library.Debug).Assembly.Location);
            string game = Path.GetFullPath(Path.Combine(bin, "..", ".."));
            return Generation.WorldRecoveryService.VerifyRoot(SubModule.Store, expected, new[] { game, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) });
        }

        public static string WriterRefusal()
        {
            return EnsureLock() ? null : "Another game process is using the world store (" + Path.Combine(SubModule.Store, "store.lock") + "); close it before generating or starting a world.";
        }

        public static void RequireWriter()
        {
            string refusal = WriterRefusal();
            if (refusal != null) throw new InvalidOperationException(refusal);
        }

        // The binding's world package resolves and verifies; a binding that names a data bundle also needs that bundle to verify.
        public static bool PackagePublished(WorldBinding binding)
        {
            try
            {
                if (GeneratedWorldPackage.Resolve(binding) == null) return false;
                if (binding.ContentId == WorldBinding.NoContent) return true;
                var identity = new Dictionary<string, string>(StringComparer.Ordinal) { ["schema"] = WorldDataBundle.SaveIdentitySchema, ["worldContentId"] = binding.ContentId };
                return WorldDataBundle.OpenForSave(Generation.CompileStage.BundleRoot(), identity, UI.WorldSetupHost.BundleHost()) != null;
            }
            catch (Exception) { return false; }
        }

        public static Dictionary<string, object> RunStartup()
        {
            var report = new Dictionary<string, object> { ["schema"] = "mnbl.save-binding-startup.v1", ["processId"] = Process.GetCurrentProcess().Id, ["utc"] = DateTime.UtcNow.ToString("o") };
            var watch = Stopwatch.StartNew();
            try
            {
                report["lockHeld"] = EnsureLock();
                if ((bool)report["lockHeld"])
                {
                    report["recovery"] = Store.Recover().ToDictionary();
                    // MMR-45: activations a stopped process left active, decided by their initial saves' journals just recovered.
                    report["activationRecovery"] = Generation.ActivationRecovery.Recover(new Generation.ActivationJournalStore(SubModule.Store),
                        instance => { var journal = Store.Journal(instance); return journal == null ? null : journal.State; });
                    // MMR-48: later saves a crash interrupted (their intents), by the same rules.
                    report["laterRecovery"] = Later.Recover().ToDictionary();
                    // At the main menu no generation job of this process exists yet, so no staging is live. MMR-47: the cleanup service, only
                    // under the verified root: abandoned staging and finished job directories; unfinished jobs stay for the setup to offer.
                    string rootProblem = RootProblem();
                    report["root"] = rootProblem ?? "verified";
                    string currentLock = null;
                    try { currentLock = UI.WorldSetupContract.Installed().ProducerLockSha256; } catch (Exception error) { report["lockError"] = error.Message; }
                    var cleanup = Generation.WorldRecoveryService.Cleanup(SubModule.Store, rootProblem, true, path => false, currentLock);
                    report["cleanup"] = cleanup;
                    if (cleanup.ContainsKey("staging")) report["staging"] = cleanup["staging"];
                }
                else report["skipped"] = "another game process holds " + Path.Combine(SubModule.Store, "store.lock") + "; nothing recovered or removed";
                var index = WorldReferenceIndex.Build(Store.Saves, Store, null, WorldReferenceIndex.SaveKindsBound, Later);
                report["index"] = new Dictionary<string, object> { ["savesListed"] = index.SavesListed, ["savesBound"] = index.SavesBound, ["references"] = index.References.Count,
                    ["discoveryComplete"] = index.DiscoveryComplete, ["saveKindsBound"] = WorldReferenceIndex.SaveKindsBound, ["gaps"] = index.Gaps.ToList(), ["counts"] = index.Counts() };
            }
            catch (Exception error) { report["error"] = error.ToString(); }
            report["seconds"] = watch.Elapsed.TotalSeconds;
            Startup = report;
            try
            {
                string directory = Path.Combine(SubModule.Store, "saves");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "startup-" + report["processId"] + ".json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(report));
                foreach (var old in new DirectoryInfo(directory).GetFiles("startup-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(StartupReportsKept)) old.Delete();
            }
            catch (Exception) { }
            return report;
        }
    }
}
