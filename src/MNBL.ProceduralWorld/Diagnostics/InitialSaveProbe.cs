using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Persistence;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace MNBL.ProceduralWorld.Diagnostics
{
    // MMR-62 in game, over two processes (docs/save-binding.md):
    //   Publish (generated_campaign with initialSaveProbe=1, after the world's identity is written): the initial save's journaled
    //   publication through the real engine save and read-back; an Ironman publication (the engine saves under IronmanModSaveName); the
    //   refusals that write nothing (an unpublished package, a taken name); the reference index over the game's save list. Then it leaves
    //   every interruption boundary for the next process and crashes (the torn saves are built from the published save's bytes; the
    //   crash after the engine save is a real process kill):
    //     C  intent written, the engine never asked to save (crash before the save);
    //     D  intent written, then a torn save file under the reserved name (the header cut short);
    //     T  intent written, then a save whose header is whole and whose game data is cut short;
    //     B  intent written, the engine save completed, the process killed before the outcome was journaled (crash after the save);
    //     E  a foreign save whose header cannot be read, with no journal (it must be kept, and it makes discovery incomplete);
    //   plus abandoned staging of every kind and sentinels that cleanup must keep. The process is killed after B's save is over.
    //   Recover (save_recovery, a new process at the main menu): what the startup pass (SaveBindingRuntime) did before the probe ran, the
    //   store lock's exclusivity, then the index and the retention decisions, idempotence, and the probe's own clean-up (its saves, journals,
    //   plants), which runs even when a check stops the probe early. Both processes run under the diagnostic bootstrap's save manager.
    internal static class InitialSaveProbe
    {
        private static string PlanPath { get { return Path.Combine(SubModule.Store, "saves", "probe62-plan.json"); } }

        internal static IEnumerator Publish(ProbeContext context, DiagnosticSandboxBootstrap boot, GeneratedCampaignRegistration registration, Dictionary<string, object> report)
        {
            var failures = new List<string>(); report["failures"] = failures;
            var store = SaveBindingRuntime.Store;
            var catalog = store.Saves;
            report["startup"] = Summary(SaveBindingRuntime.Startup);
            var entries = boot.Identity.Entries;
            var binding = WorldBinding.FromHeader(entries.ToDictionary(k => WorldBinding.Prefix + k.Key, k => k.Value));
            report["binding"] = new Dictionary<string, object> { ["worldId"] = binding.WorldId, ["package"] = binding.PackageName, ["instance"] = binding.Instance, ["contentId"] = binding.ContentId };
            if (binding.Legacy) { failures.Add("The world's identity has no campaign instance."); yield break; }

            // 1. Refusals that write nothing.
            var refusals = new Dictionary<string, object>(); report["refusals"] = refusals;
            var unpublished = new WorldBinding { WorldId = binding.WorldId, ManifestSha256 = new string('0', 64), Instance = WorldBinding.NewInstance(), ContentId = WorldBinding.NoContent };
            refusals["unpublishedPackage"] = Refusal(store, unpublished, EarlyWorldBindingStore.InitialSaveName(unpublished), failures);

            // 2. The initial save, published through the engine and read back.
            var publish = new Dictionary<string, object>(); report["publish"] = publish;
            var steps = Publication(context, boot, store, binding, publish, failures);
            while (steps.MoveNext()) yield return steps.Current;
            if (!(bool)publish["published"]) yield break;
            var publisher = (InitialSavePublisher)publish["publisher"]; publish.Remove("publisher");
            // An Ironman campaign saves under IronmanModSaveName whatever name is asked for; that name is reserved and journaled.
            var ironman = new Dictionary<string, object>(); report["ironman"] = ironman;
            var i = new WorldBinding { WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256, Instance = WorldBinding.NewInstance(), ContentId = binding.ContentId };
            bool wasIronman = CampaignOptions.IsIronmanMode;
            CampaignOptions.IsIronmanMode = true; entries["instance"] = i.Instance;
            try
            {
                steps = Publication(context, boot, store, i, ironman, failures);
                while (steps.MoveNext()) yield return steps.Current;
            }
            finally { CampaignOptions.IsIronmanMode = wasIronman; entries["instance"] = binding.Instance; }
            var ironmanPublisher = ironman.ContainsKey("publisher") ? (InitialSavePublisher)ironman["publisher"] : null; ironman.Remove("publisher");
            string expectedIronman = "Ironman" + Campaign.Current.UniqueGameId;
            ironman["expectedName"] = expectedIronman;
            if (ironmanPublisher == null || !ironmanPublisher.Ironman || ironmanPublisher.SaveName != expectedIronman || !(bool)ironman["published"])
                failures.Add("The Ironman publication did not reserve and publish " + expectedIronman + ".");
            var index = WorldReferenceIndex.Build(catalog, store, new[] { binding }, true, SaveBindingRuntime.Later);
            var decision = index.Decide(binding.PackageName, binding.ContentId);
            publish["index"] = new Dictionary<string, object> { ["savesListed"] = index.SavesListed, ["savesBound"] = index.SavesBound, ["discoveryComplete"] = index.DiscoveryComplete, ["gaps"] = index.Gaps,
                ["references"] = index.For(binding.PackageName).Select(r => r.Report()).ToList(), ["retain"] = decision.Retain, ["reason"] = decision.Reason };
            if (!index.For(binding.PackageName).Any(r => r.Kind == WorldReference.Save && r.Name == publisher.SaveName)) failures.Add("The reference index does not find the initial save naming the world package.");
            if (!decision.Retain) failures.Add("The index would let the published world be deleted.");
            refusals["takenName"] = Refusal(store, new WorldBinding { WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256, Instance = WorldBinding.NewInstance(), ContentId = binding.ContentId }, publisher.SaveName, failures);
            context.Checkpoint();

            if (context.Int("initialSaveCrash", 1) == 0) yield break;
            // 3. The interruption boundaries for the next process.
            var plan = new Dictionary<string, object> { ["schema"] = "mnbl.probe62-plan.v1", ["processId"] = context.ProcessId, ["worldId"] = binding.WorldId, ["package"] = binding.PackageName,
                ["contentId"] = binding.ContentId, ["published"] = Planned(binding.Instance, publisher.SaveName), ["ironman"] = Planned(i.Instance, expectedIronman) };
            report["plan"] = plan;
            string savePath = BannerlordSaveCatalog.SavePath(publisher.SaveName);
            if (!File.Exists(savePath)) { failures.Add("The published save's file is not at " + savePath + "."); yield break; }
            var saveBytes = File.ReadAllBytes(savePath);
            int metadataLength = BitConverter.ToInt32(saveBytes, 0);
            plan["publishedSaveSha256"] = Sha(saveBytes); plan["publishedSaveBytes"] = saveBytes.Length;
            Func<string, WorldBinding> other = label => new WorldBinding { WorldId = binding.WorldId, ManifestSha256 = binding.ManifestSha256, Instance = WorldBinding.NewInstance(), ContentId = binding.ContentId };
            // C: crash before the engine save.
            var c = other("C"); store.Begin(c, EarlyWorldBindingStore.InitialSaveName(c)); plan["beforeSave"] = Planned(c.Instance, EarlyWorldBindingStore.InitialSaveName(c));
            // D: a torn save: the header's length prefix and half its JSON.
            var d = other("D"); string dName = EarlyWorldBindingStore.InitialSaveName(d); store.Begin(d, dName);
            File.WriteAllBytes(BannerlordSaveCatalog.SavePath(dName), saveBytes.Take(4 + metadataLength / 2).ToArray());
            plan["tornHeader"] = Planned(d.Instance, dName);
            // T: a whole header (rewritten to bind T) before game data cut short.
            var t = other("T"); string tName = EarlyWorldBindingStore.InitialSaveName(t); store.Begin(t, tName);
            var tornBody = Rebind(saveBytes, metadataLength, binding.Instance, t.Instance);
            File.WriteAllBytes(BannerlordSaveCatalog.SavePath(tName), tornBody.Take(tornBody.Length - (saveBytes.Length - 4 - metadataLength) / 2).ToArray());
            plan["tornData"] = Planned(t.Instance, tName);
            // E: a foreign save whose header cannot be read; no journal names it.
            string eName = "mnbl_probe62_foreign_" + context.ProcessId;
            File.WriteAllBytes(BannerlordSaveCatalog.SavePath(eName), new byte[] { 0xff, 0xff, 0xff, 0x7f, 1, 2, 3 });
            plan["foreign"] = new Dictionary<string, object> { ["saveName"] = eName };
            var planted = new Dictionary<string, object>(); var unreadableNow = catalog.List().Where(h => !h.Readable).Select(h => h.Name).ToList();
            planted["listedUnreadable"] = unreadableNow;
            if (!unreadableNow.Contains(dName) || !unreadableNow.Contains(eName)) failures.Add("The game does not list the planted torn or foreign save as unreadable.");
            var tHeader = catalog.Read(tName);
            planted["tornDataHeader"] = new Dictionary<string, object> { ["readable"] = tHeader != null && tHeader.Readable, ["bodyWhole"] = tHeader == null ? null : tHeader.BodyWhole, ["problem"] = tHeader == null ? null : tHeader.BodyProblem };
            if (tHeader == null || !tHeader.Readable || tHeader.BodyWhole != false) failures.Add("The save with torn game data does not read as a whole header with torn data.");
            report["planted"] = planted;
            // Staging of every kind, and sentinels that must stay.
            string tag = "probe62-" + context.ProcessId, root = SubModule.Store;
            var staging = new[] { Path.Combine(root, "bundles", ".staging-" + tag), Path.Combine(root, "worlds", "mnbl_probe62_" + context.ProcessId + DurableFile.TemporaryMarker + Guid.NewGuid().ToString("N")),
                Path.Combine(root, "world-surfaces", Guid.NewGuid().ToString("N")) };
            foreach (var dir in staging) { Directory.CreateDirectory(Path.Combine(dir, "nested")); File.WriteAllText(Path.Combine(dir, "nested", "f.bin"), tag); }
            string journalTemp = Path.Combine(store.JournalDirectory, WorldBinding.NewInstance() + ".json" + DurableFile.TemporaryMarker + Guid.NewGuid().ToString("N"));
            File.WriteAllText(journalTemp, "{");
            var sentinels = new[] { Path.Combine(root, "bundles", Sha(Encoding.UTF8.GetBytes(tag))), Path.Combine(root, "worlds", "mnbl_probe62_" + context.ProcessId + "-000000000000"), Path.Combine(root, "jobs", tag) };
            foreach (var dir in sentinels) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "sentinel.txt"), tag); }
            plan["staging"] = staging.Concat(new[] { journalTemp }).ToList(); plan["sentinels"] = sentinels.ToList();
            // B: the engine save completes, then the process dies before the journal's outcome.
            var b = other("B"); string bName = EarlyWorldBindingStore.InitialSaveName(b);
            store.Begin(b, bName); plan["afterSave"] = Planned(b.Instance, bName);
            File.WriteAllText(PlanPath, new JavaScriptSerializer().Serialize(plan));
            entries["instance"] = b.Instance;
            var campaign = Campaign.Current;
            var previous = campaign.SandBoxManager.SandBoxSaveManager;
            var observer = new Observer(bName);
            campaign.SandBoxManager.SandBoxSaveManager = observer;
            double start = context.Seconds;
            campaign.SaveHandler.SaveAs(bName);
            while (!observer.Completed && context.Seconds - start < 120) { boot.Tick(); yield return null; }
            entries["instance"] = binding.Instance;
            campaign.SandBoxManager.SandBoxSaveManager = previous;
            report["afterSaveEngine"] = new Dictionary<string, object> { ["completed"] = observer.Completed, ["success"] = observer.Success, ["journalState"] = store.Journal(b.Instance).State };
            if (!observer.Completed || !observer.Success) { failures.Add("The engine did not complete save B."); yield break; }
            report["crash"] = "killed after save B was over, before its journal outcome";
            context.Stage("initial_save_crash");
            context.Checkpoint();
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            yield return null;
        }

        internal static IEnumerator Recover(ProbeContext context)
        {
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            var report = new Dictionary<string, object>(); context.Outputs["recovery"] = report;
            if (!File.Exists(PlanPath)) { failures.Add("No probe62 plan: run generated_campaign with initialSaveProbe=1 first."); yield break; }
            var plan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(PlanPath));
            report["plan"] = plan;
            try
            {
                // The startup pass runs when the main menu is first set up; the probe may tick before that.
                double start = context.Seconds;
                while (SaveBindingRuntime.Startup == null && context.Seconds - start < 120) yield return null;
                var startup = SaveBindingRuntime.Startup;
                report["startup"] = startup;
                if (startup == null || !(startup.ContainsKey("lockHeld") && (bool)startup["lockHeld"])) { failures.Add("The startup pass did not run with the store lock."); yield break; }
                if (startup.ContainsKey("error")) failures.Add("The startup pass failed: " + startup["error"]);
                var store = SaveBindingRuntime.Store; var catalog = store.Saves;
                context.Stage("save_recovery_begin");
                var other = StoreLock.TryAcquire(SubModule.Store);
                report["lockExclusive"] = other == null;
                if (other != null) { other.Dispose(); failures.Add("The store lock can be taken a second time while this process holds it."); }

                // 1. Each boundary as the startup pass left it.
                var outcomes = new Dictionary<string, object>(); report["outcomes"] = outcomes;
                Action<string, string, bool> expect = (key, state, saveExists) =>
                {
                    var planned = (Dictionary<string, object>)plan[key];
                    var journal = store.Journal((string)planned["instance"]);
                    var header = catalog.Read((string)planned["saveName"]);
                    var bound = header == null || !header.Readable ? null : WorldBinding.FromHeader(header.Metadata);
                    outcomes[key] = new Dictionary<string, object> { ["journal"] = journal == null ? null : journal.Report(), ["saveExists"] = header != null,
                        ["boundInstance"] = bound == null ? null : bound.Instance, ["bodyWhole"] = header == null ? null : header.BodyWhole };
                    if (journal == null || journal.State != state) failures.Add(key + ": the journal is " + (journal == null ? "missing" : journal.State) + ", not " + state + ".");
                    if ((header != null) != saveExists) failures.Add(key + ": the save " + (saveExists ? "is missing" : "still exists") + ".");
                    if (saveExists && state == EarlyWorldBindingStore.Published && (bound == null || bound.Instance != (string)planned["instance"] || header.BodyWhole != true))
                        failures.Add(key + ": the save does not read back whole and bound to its instance.");
                };
                expect("published", EarlyWorldBindingStore.Published, true);
                expect("ironman", EarlyWorldBindingStore.Published, true);
                expect("afterSave", EarlyWorldBindingStore.Published, true);
                expect("beforeSave", EarlyWorldBindingStore.Abandoned, false);
                expect("tornHeader", EarlyWorldBindingStore.Removed, false);
                expect("tornData", EarlyWorldBindingStore.Removed, false);
                var recovery = startup.ContainsKey("recovery") ? (Dictionary<string, object>)startup["recovery"] : null;
                var resolvedNames = recovery == null ? new List<string>() : ((IEnumerable)recovery["resolved"]).Cast<Dictionary<string, object>>().Select(r => (string)r["saveName"]).ToList();
                foreach (var key in new[] { "afterSave", "beforeSave", "tornHeader", "tornData" })
                    if (!resolvedNames.Contains((string)((Dictionary<string, object>)plan[key])["saveName"])) failures.Add(key + " was not resolved by the startup pass.");
                foreach (var key in new[] { "published", "ironman" })
                    if (resolvedNames.Contains((string)((Dictionary<string, object>)plan[key])["saveName"])) failures.Add("The startup pass touched the already published " + key + " journal.");
                string foreign = (string)((Dictionary<string, object>)plan["foreign"])["saveName"];
                var foreignHeader = catalog.Read(foreign);
                outcomes["foreign"] = new Dictionary<string, object> { ["saveExists"] = foreignHeader != null, ["readable"] = foreignHeader != null && foreignHeader.Readable };
                if (foreignHeader == null) failures.Add("The foreign unreadable save was deleted.");

                // 2. Staging: the abandoned kinds removed, the sentinels kept.
                var staging = ((IEnumerable)plan["staging"]).Cast<string>().ToList(); var sentinels = ((IEnumerable)plan["sentinels"]).Cast<string>().ToList();
                var stagingReport = startup.ContainsKey("staging") ? startup["staging"] : null;
                report["staging"] = new Dictionary<string, object> { ["startup"] = stagingReport, ["plantedLeft"] = staging.Where(p => Directory.Exists(p) || File.Exists(p)).ToList(),
                    ["sentinelsKept"] = sentinels.Where(Directory.Exists).ToList() };
                if (staging.Any(p => Directory.Exists(p) || File.Exists(p))) failures.Add("Planted staging was not removed.");
                if (!sentinels.All(p => File.Exists(Path.Combine(p, "sentinel.txt")))) failures.Add("A sentinel (bundle, package or job record) was removed.");

                // 3. The index fails closed while the foreign save is unreadable; retention never releases a complete world in this increment.
                string package = (string)plan["package"], sentinelPackage = Path.GetFileName(sentinels[1]), contentId = (string)plan["contentId"];
                var index = WorldReferenceIndex.Build(catalog, store, null, true, SaveBindingRuntime.Later);
                var incomplete = new Dictionary<string, object> { ["discoveryComplete"] = index.DiscoveryComplete, ["gaps"] = index.Gaps, ["world"] = Decision(index, package, contentId), ["unreferenced"] = Decision(index, sentinelPackage, null) };
                report["indexWithForeign"] = incomplete;
                if (index.DiscoveryComplete || !index.Gaps.Any(g => g.Contains(foreign))) failures.Add("The index does not report the unreadable foreign save as a gap.");
                if (index.For(package).Count(r => r.Kind == WorldReference.Save) != 3) failures.Add("The index does not find the world's three bound saves (initial, Ironman, after the crash).");
                if (!index.Decide(sentinelPackage).Retain || !index.Decide(sentinelPackage).Reason.StartsWith("reference discovery is incomplete", StringComparison.Ordinal)) failures.Add("An unreferenced package is not retained while discovery is incomplete.");
                // Recovery is idempotent.
                var again = store.Recover();
                report["recoverAgain"] = again.ToDictionary();
                if (again.Resolved.Count != 0 || again.Unresolved.Count != 0 || again.UnreadableJournals.Count != 0) failures.Add("A second recovery changed something.");
                // Without the foreign save the index is complete; under MMR-62's rule (save kinds not all journaled) it still retains every world.
                // MMR-48 binds every save kind (the default since then).
                catalog.Delete(foreign);
                var complete = WorldReferenceIndex.Build(catalog, store, null, false);
                var withKinds = WorldReferenceIndex.Build(catalog, store, null, true, SaveBindingRuntime.Later);
                report["indexComplete"] = new Dictionary<string, object> { ["discoveryComplete"] = complete.DiscoveryComplete, ["gaps"] = complete.Gaps, ["world"] = Decision(complete, package, contentId),
                    ["unreferenced"] = Decision(complete, sentinelPackage, null), ["unreferencedIfAllSaveKindsBound"] = Decision(withKinds, sentinelPackage, null), ["worldIfAllSaveKindsBound"] = Decision(withKinds, package, contentId) };
                if (!complete.DiscoveryComplete) failures.Add("The index is still incomplete without the foreign save: " + String.Join("; ", complete.Gaps));
                if (!complete.Decide(sentinelPackage).Retain) failures.Add("An unreferenced complete world would be deleted before MMR-48 binds every save kind.");
                if (withKinds.Decide(sentinelPackage).Retain || !withKinds.Decide(package, contentId).Retain) failures.Add("With every save kind bound, the decision does not follow the references.");
                context.Checkpoint();
            }
            finally { report["cleaned"] = CleanUp(plan, SaveBindingRuntime.Store); }
            context.Stage("save_recovery_end");
            yield return null;
        }

        // The probe's own clean-up: its saves (initial, Ironman, after the crash, the torn and foreign ones), journals, staging and sentinels.
        // The world package stays, as for every probe world. Never throws.
        private static List<string> CleanUp(Dictionary<string, object> plan, EarlyWorldBindingStore store)
        {
            var cleaned = new List<string>();
            var names = new List<string>(); var instances = new List<string>();
            foreach (var key in new[] { "published", "ironman", "afterSave", "beforeSave", "tornHeader", "tornData" })
            {
                object value;
                if (!plan.TryGetValue(key, out value) || !(value is Dictionary<string, object>)) continue;
                names.Add((string)((Dictionary<string, object>)value)["saveName"]); instances.Add((string)((Dictionary<string, object>)value)["instance"]);
            }
            if (plan.ContainsKey("foreign")) names.Add((string)((Dictionary<string, object>)plan["foreign"])["saveName"]);
            foreach (var name in names)
                try { if (store.Saves.Read(name) != null || File.Exists(BannerlordSaveCatalog.SavePath(name))) { store.Saves.Delete(name); if (File.Exists(BannerlordSaveCatalog.SavePath(name))) File.Delete(BannerlordSaveCatalog.SavePath(name)); cleaned.Add(name + ".sav"); } }
                catch (Exception error) { cleaned.Add("FAILED " + name + ": " + error.Message); }
            foreach (var instance in instances)
                try { var journal = Path.Combine(store.JournalDirectory, instance + ".json"); if (File.Exists(journal)) { File.Delete(journal); cleaned.Add(Path.GetFileName(journal)); } }
                catch (Exception error) { cleaned.Add("FAILED journal " + instance + ": " + error.Message); }
            foreach (var key in new[] { "staging", "sentinels" })
                if (plan.ContainsKey(key))
                    foreach (string path in (IEnumerable)plan[key])
                        try { if (Directory.Exists(path)) { Directory.Delete(path, true); cleaned.Add(path); } else if (File.Exists(path)) { File.Delete(path); cleaned.Add(path); } }
                        catch (Exception error) { cleaned.Add("FAILED " + path + ": " + error.Message); }
            try { File.Delete(PlanPath); } catch (Exception) { }
            return cleaned;
        }

        // One publication through InitialSavePublisher: waits for the engine (a timeout gives up only once nothing is queued), then reads the
        // save back. Fills record["published"] and leaves the publisher in record["publisher"].
        private static IEnumerator Publication(ProbeContext context, DiagnosticSandboxBootstrap boot, EarlyWorldBindingStore store, WorldBinding binding, Dictionary<string, object> record, List<string> failures)
        {
            double start = context.Seconds;
            var publisher = InitialSavePublisher.Start(store, Campaign.Current, binding);
            while (!publisher.Done && context.Seconds - start < 300) { publisher.Tick(context.Seconds - start > 180); boot.Tick(); yield return null; }
            record["publisher"] = publisher; record["published"] = publisher.Published;
            record["seconds"] = context.Seconds - start; record["saveName"] = publisher.SaveName; record["ironman"] = publisher.Ironman; record["error"] = publisher.Error; record["done"] = publisher.Done;
            record["journal"] = publisher.Record == null ? null : publisher.Record.Report();
            record["history"] = publisher.Record == null ? null : publisher.Record.History;
            if (!publisher.Published) { failures.Add("The save " + publisher.SaveName + " was not published: " + (publisher.Error ?? (publisher.Record == null ? "no journal" : publisher.Record.State + ": " + publisher.Record.LastNote))); yield break; }
            var readBack = store.Saves.Read(publisher.SaveName);
            var found = readBack == null || !readBack.Readable ? null : WorldBinding.FromHeader(readBack.Metadata);
            record["readBack"] = new Dictionary<string, object> { ["exists"] = readBack != null, ["readable"] = readBack != null && readBack.Readable, ["bodyWhole"] = readBack == null ? null : readBack.BodyWhole,
                ["instance"] = found == null ? null : found.Instance, ["package"] = found == null ? null : found.PackageName, ["contentId"] = found == null ? null : found.ContentId };
            if (found == null || found.Instance != binding.Instance || !found.SameWorld(binding) || readBack.BodyWhole != true) failures.Add("The save " + publisher.SaveName + " does not read back whole and bound to its campaign instance.");
            var history = publisher.Record.History.Select(h => (string)h["state"]).ToList();
            record["states"] = history;
            if (!history.SequenceEqual(new[] { EarlyWorldBindingStore.Intent, EarlyWorldBindingStore.Published })) failures.Add("The journal of " + publisher.SaveName + " did not go intent -> published.");
        }

        private static Dictionary<string, object> Refusal(EarlyWorldBindingStore store, WorldBinding binding, string saveName, List<string> failures)
        {
            string error = null;
            try { store.Begin(binding, saveName); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            bool journal = store.Journal(binding.Instance) != null;
            if (error == null || journal) failures.Add("Begin(" + saveName + ") was not refused without a journal.");
            return new Dictionary<string, object> { ["refused"] = error, ["journalWritten"] = journal };
        }

        private static Dictionary<string, object> Decision(WorldReferenceIndex index, string package, string contentId)
        {
            var decision = index.Decide(package, contentId);
            return new Dictionary<string, object> { ["package"] = package, ["retain"] = decision.Retain, ["reason"] = decision.Reason, ["references"] = decision.References.Count };
        }

        private static Dictionary<string, object> Planned(string instance, string saveName) { return new Dictionary<string, object> { ["instance"] = instance, ["saveName"] = saveName }; }

        private static Dictionary<string, object> Summary(Dictionary<string, object> startup)
        {
            if (startup == null) return null;
            return startup.Where(k => k.Key != "index").ToDictionary(k => k.Key, k => k.Value);
        }

        // The save file with its header's instance replaced (same length: instances are 32 hex digits).
        private static byte[] Rebind(byte[] save, int metadataLength, string from, string to)
        {
            var json = Encoding.UTF8.GetString(save, 4, metadataLength);
            if (json.IndexOf(from, StringComparison.Ordinal) < 0) throw new InvalidDataException("The save header does not hold the instance.");
            var rebound = Encoding.UTF8.GetBytes(json.Replace(from, to));
            if (rebound.Length != metadataLength) throw new InvalidDataException("Rebinding changed the header length.");
            var copy = (byte[])save.Clone();
            Buffer.BlockCopy(rebound, 0, copy, 4, rebound.Length);
            return copy;
        }

        private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private sealed class Observer : ISaveManager
        {
            private readonly string expected;
            internal bool Completed, Success;
            internal Observer(string expected) { this.expected = expected; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string name) { if (name == expected) { Success = success; Completed = true; } }
        }
    }
}
