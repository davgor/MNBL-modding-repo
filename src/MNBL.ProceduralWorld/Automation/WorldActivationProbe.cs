using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-45: a generated world activated in the running game (world_activation; seed=<n>, culture=<id>, fault=<boundary>, stopInside=<boundary>,
    // secondFault=<boundary>, gameplayDays=<d>, campaignTimeoutSeconds=<s>, waterVisibility=1 (MMR-285: Diagnostics/WaterVisibilityProbe on the committed world)):
    //   1. the startup pass's activation recovery as this process found it (a previous run's stopped activation);
    //   2. a generation job to review (MMR-44), then its commit through ActivationCommitStage: the world's campaign graph built and validated at
    //      the main menu, its package promoted, the new game started on the world's scene (character creation driven by the diagnostic
    //      bootstrap), the post-build checks on the live campaign, the identity bound, the initial save published, the receipt written;
    //      fault=<b> fails the activation at b (rolled back: the job fails, the menu returns), stopInside=<b> terminates this process at the end
    //      of b's work, before it is journaled (the next run's startup recovers it);
    //   3. after a committed activation: the receipt verified against its runtime-derived record, the initial save read back bound and whole,
    //      campaign time run for gameplayDays;
    //   4. secondFault=<b>: the game ended, the same world activated again with a failure at b; the first world's package, receipt, save and
    //      journal must be byte for byte unchanged and the failed instance must leave no save binding and no object.
    internal static class WorldActivationProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Body(context));
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                var nested = stack.Peek().Current as IEnumerator;
                if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
            }
        }

        private static ActivationBoundary? Boundary(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;
            return (ActivationBoundary)Enum.Parse(typeof(ActivationBoundary), name);
        }

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-45: a generation job's world activated into a playable campaign (WorldActivationTransaction through ActivationCommitStage), its receipt and initial save, failures injected at a boundary, a process stop inside a boundary, and the next start's recovery. Loading the saved world again is MMR-49; the player-facing Start Campaign action is MMR-46.";
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); };
            var startup = SaveBindingRuntime.Startup;
            context.Outputs["startupActivationRecovery"] = startup != null && startup.ContainsKey("activationRecovery") ? startup["activationRecovery"] : null;
            context.Outputs["startupSaveRecovery"] = startup != null && startup.ContainsKey("recovery") ? startup["recovery"] : null;
            uint seed = (uint)context.Int("seed", 42);
            var first = new Dictionary<string, object>(); context.Outputs["activation"] = first;
            ActivationCommitStage stage = null; GenerationJob job = null;
            yield return Activate(context, seed, Boundary(context.Str("fault", "")), Boundary(context.Str("stopInside", "")), first, (s, j) => { stage = s; job = j; });
            context.Checkpoint();
            if (stage == null || stage.Journal == null || stage.Journal.State != ActivationJournalStore.Committed)
            {
                if (context.Str("fault", "").Length == 0) failures.Add("the activation did not commit: " + first["error"]);
                else expect(Equals(first["jobPhase"], "Failed") && Equals(first["journalState"], ActivationJournalStore.Failed) && Equals(first["atMainMenu"], true) && Equals(first["savesBound"], 0),
                    "the activation with a failure at " + context.Str("fault", "") + " is rolled back to the main menu with a failed journal and no save binding");
                yield break;
            }
            // 3. The receipt and the initial save; the campaign plays.
            var journals = new ActivationJournalStore(SubModule.Store);
            Dictionary<string, object> receipt = null;
            try { receipt = ActivationReceipt.Read(journals, stage.Instance); } catch (Exception error) { failures.Add("the receipt does not verify: " + error.Message); }
            first["receipt"] = receipt;
            expect(receipt != null && Equals(receipt["campaignInstanceId"], ActivationRequest.Uuid(stage.Instance)) && Equals(receipt["worldContentId"], first["worldContentId"]), "one receipt binds the world's content id to this campaign instance");
            var save = SaveBindingRuntime.Store.Journal(stage.Instance);
            first["saveJournal"] = save == null ? null : save.Report();
            var header = save == null ? null : SaveBindingRuntime.Store.Saves.Read(save.SaveName);
            var bound = header == null || !header.Readable ? null : WorldBinding.FromHeader(header.Metadata);
            first["saveHeader"] = header == null ? null : new Dictionary<string, object> { ["readable"] = header.Readable, ["bodyWhole"] = header.BodyWhole, ["instance"] = bound == null ? null : bound.Instance,
                ["package"] = bound == null ? null : bound.PackageName, ["contentId"] = bound == null ? null : bound.ContentId };
            expect(save != null && save.State == EarlyWorldBindingStore.Published && header != null && header.BodyWhole == true && bound != null && bound.Instance == stage.Instance && bound.ContentId == (string)first["worldContentId"],
                "the initial save is published, whole, and bound to this instance and bundle");
            var boot = stage.Manager as DiagnosticSandboxBootstrap;
            double days = context.Num("gameplayDays", 1);
            if (boot != null && days > 0 && failures.Count == 0)
            {
                var gameplay = new Dictionary<string, object>(); first["gameplay"] = gameplay;
                var dataset = stage.Host.Dataset;
                var aliases = new HashSet<string>(dataset.Declared.Where(k => CampaignIdentity.IsAlias(k.Substring(k.IndexOf('.') + 1))).Select(k => k.Substring(k.IndexOf('.') + 1)), StringComparer.Ordinal);
                // The player is sheltered (other parties ignore the main party): the startup's bandits engage a player waiting at a gate and this
                // probe plays no encounter (run 9404 stalled on one at 0.83 days, as fixture run 39908 had).
                var steps = GeneratedCampaignGameplay.Run(context, boot, dataset.WorldId, aliases, gameplay, days, true, false, true);
                while (steps.MoveNext()) yield return steps.Current;
                failures.AddRange(((List<string>)gameplay["failures"]).Select(f => "gameplay: " + f));
                string shot = Path.Combine(context.Directory, "world-activation-" + context.ProcessId + "-map.bmp");
                try { TaleWorlds.Engine.Utilities.TakeScreenshot(shot); first["screenshot"] = shot; } catch (Exception) { }
                context.Checkpoint();
            }
            // MMR-285 (for MMR-247): every lake shown, every river drawn, no impassable water drawn as dry land, on the live map.
            if (context.Int("waterVisibility", 0) != 0 && failures.Count == 0)
            {
                var water = new Dictionary<string, object>(); first["waterVisibility"] = water;
                var steps = MNBL.ProceduralWorld.Diagnostics.WaterVisibilityProbe.Run(context, water);
                while (steps.MoveNext()) yield return steps.Current;
                failures.AddRange(((List<string>)water["failures"]).Select(f => "waterVisibility: " + f));
                context.Checkpoint();
            }
            // The committed job is final (Playing): released, its directory goes; the package, the bundle and the scene stay with the world.
            {
                var releasing = GenerationRuntime.Coordinator.Release(job);
                double released = context.Seconds;
                while (!releasing.IsCompleted && context.Seconds - released < 120) yield return null;
                first["jobReleased"] = releasing.IsCompleted && !Directory.Exists(job.Root);
                first["bundleKept"] = Directory.Exists(job.Get<string>("bundleDirectory"));
                expect((bool)first["bundleKept"], "the committed world's bundle stays published after its job is released (the save binding names it)");
            }
            // 4. The same world again, failing at a boundary, beside the committed one.
            var secondFault = Boundary(context.Str("secondFault", ""));
            if (secondFault != null)
            {
                var second = new Dictionary<string, object>(); context.Outputs["secondActivation"] = second;
                MBGameManager.EndGame();
                double start = context.Seconds;
                while (Game.Current != null || MBGameManager.Current != null)
                {
                    if (context.Seconds - start > 120) throw new TimeoutException("The committed campaign did not end.");
                    yield return null;
                }
                var packageDir = Path.Combine(GeneratedWorldPackage.Root, ((Dictionary<string, object>)first["saveHeader"])["package"] as string ?? "");
                var snapshot = Snapshot(packageDir, journals, stage.Instance, save.SaveName);
                second["firstWorldBefore"] = snapshot;
                ActivationCommitStage again = null;
                yield return Activate(context, seed, secondFault, null, second, (s, j) => again = s);
                var after = Snapshot(packageDir, journals, stage.Instance, save.SaveName);
                second["firstWorldAfter"] = after;
                expect(snapshot.All(kv => after.ContainsKey(kv.Key) && Equals(after[kv.Key], kv.Value)), "the committed world's package, receipt, save and journal are unchanged by the failed activation");
                expect(again != null && again.Journal == null && Equals(second["journalState"], ActivationJournalStore.Failed) && Equals(second["savesBound"], 0) && Equals(second["atMainMenu"], true) &&
                       Equals(second["worldObjectsLeft"], 0), "the second activation, failing at " + secondFault + ", is rolled back: failed journal, no save binding, no object left, the main menu");
            }
            context.Checkpoint();
        }

        // Generates the world (a job to review), commits it, follows the new game (driving character creation), and records the outcome.
        private static IEnumerator Activate(ProbeContext context, uint seed, ActivationBoundary? fault, ActivationBoundary? stopInside, Dictionary<string, object> record,
            Action<ActivationCommitStage, GenerationJob> done)
        {
            var coordinator = GenerationRuntime.Coordinator;
            var contract = WorldSetupContract.Installed();
            var choice = new WorldChoice { CampaignInstanceId = Guid.NewGuid().ToString("D"), Seed = seed, SeedSource = "explicit", RecipeVersion = contract.RecipeVersion,
                Overrides = new SortedDictionary<string, long>(contract.Overrides, StringComparer.Ordinal), MaxAttempts = contract.QualityAttempts, ProducerLockSha256 = contract.ProducerLockSha256, Profile = "one-shot" };
            var queue = new ConcurrentQueue<ProgressEvent>();
            var job = coordinator.Start(choice, queue.Enqueue);
            record["jobId"] = job.Id; record["seed"] = seed; record["fault"] = fault == null ? null : fault.ToString(); record["stopInside"] = stopInside == null ? null : stopInside.ToString();
            context.Stage("world_activation:generate");
            double start = context.Seconds;
            while (!((job.Phase == JobPhase.ReadyForReview || JobStateMachine.IsTerminal(job.Phase)) && job.Completion.IsCompleted))
            {
                if (context.Seconds - start > 900) throw new TimeoutException("The generation job did not reach review.");
                yield return null;
            }
            record["generateSeconds"] = context.Seconds - start; record["reviewPhase"] = job.Phase.ToString();
            if (job.Phase != JobPhase.ReadyForReview) { record["error"] = "the job ended " + job.Phase + ": " + job.FailureDetail; done(null, job); yield break; }
            record["worldContentId"] = job.Get<string>("worldContentId"); record["sceneName"] = job.Get<string>("sceneName");
            string culture = context.Str("culture", "empire");
            var traces = new ConcurrentQueue<string>();
            var stage = new ActivationCommitStage {
                Fault = fault, StopInside = stopInside, CampaignTimeoutSeconds = context.Num("campaignTimeoutSeconds", 3000),
                Trace = traces.Enqueue,   // the commit traces from the job's worker; the probe records them on the engine thread
                OnStop = () => { record["stoppedInside"] = stopInside.ToString(); record["stopUtc"] = DateTime.UtcNow.ToString("o"); context.Checkpoint(); Process.GetCurrentProcess().Kill(); },
                Start = a => DiagnosticSandboxBootstrap.StartGenerated(a.World, a.SceneName, culture, census => { }, a.SupportReport, a.Trace, a.Periodic, a.Climate, a.CreationCultures),
                Progress = m => { var b = m as DiagnosticSandboxBootstrap; return b == null ? "no game manager" : b.Failed ? "failed: " + b.Error : b.Completed ? "map" : null; } };
            context.Stage("world_activation:commit");
            start = context.Seconds;
            var committing = coordinator.Commit(job, stage);
            while (!committing.IsCompleted)
            {
                string line;
                while (traces.TryDequeue(out line)) context.Stage("world_activation:" + line);
                var boot = stage.Manager as DiagnosticSandboxBootstrap;
                if (boot != null && Game.Current != null) boot.Tick();   // character creation, one stage per frame; re-pauses the map (not once an undo ended the game)
                if (context.Seconds - start > context.Num("campaignTimeoutSeconds", 3000) + 600) throw new TimeoutException("The activation did not finish.");
                yield return null;
            }
            { string line; while (traces.TryDequeue(out line)) context.Stage("world_activation:" + line); }
            record["commitSeconds"] = context.Seconds - start;
            record["jobPhase"] = job.Phase.ToString(); record["instance"] = stage.Instance;
            record["error"] = stage.Error == null ? null : stage.Error.GetType().Name + ": " + stage.Error.Message;
            record["cleanup"] = job.Cleanup.ToList();
            var journal = stage.Instance == null ? null : new ActivationJournalStore(SubModule.Store).Journal(stage.Instance);
            record["journalState"] = journal == null ? null : journal.State;
            record["journal"] = journal == null ? null : new Dictionary<string, object> { ["state"] = journal.State, ["failedAt"] = journal.FailedAt, ["error"] = journal.Error,
                ["phases"] = journal.Phases.Select(p => p["boundary"]).ToList(), ["rollback"] = journal.Rollback, ["evidence"] = journal.Evidence.ToDictionary(kv => kv.Key, kv => (object)kv.Value) };
            record["host"] = stage.Host == null ? null : stage.Host.Record;
            if (stage.Error != null)
            {
                // A rolled-back activation: back at the main menu, nothing of the world registered, no save bound to the instance.
                start = context.Seconds;
                while ((Game.Current != null || MBGameManager.Current != null) && context.Seconds - start < 120) yield return null;
                record["atMainMenu"] = Game.Current == null && MBGameManager.Current == null;
                record["worldObjectsLeft"] = stage.Host != null && stage.Host.Dataset != null && MBObjectManagerReady() ? WorldObjects(stage.Host.Dataset.WorldPrefix) : 0;
                record["savesBound"] = stage.Instance == null ? 0 : SaveBindingRuntime.Store.Saves.List().Select(h => { try { return h.Readable ? WorldBinding.FromHeader(h.Metadata) : null; } catch (Exception) { return null; } })
                    .Count(b => b != null && b.Instance == stage.Instance);
                var releasing = coordinator.Release(job);
                start = context.Seconds;
                while (!releasing.IsCompleted && context.Seconds - start < 120) yield return null;
            }
            done(stage, job);
        }

        private static bool MBObjectManagerReady() { return TaleWorlds.ObjectSystem.MBObjectManager.Instance != null; }

        private static int WorldObjects(string prefix)
        {
            var store = new MBObjectStore(StoreStage.NewGame);
            return CampaignObjectRegistry.CensusTypes.Sum(type => store.Objects(type).Count(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)));
        }

        // A committed world's package files, receipt, save header and journals, as hashes.
        private static Dictionary<string, object> Snapshot(string packageDir, ActivationJournalStore journals, string instance, string saveName)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (Directory.Exists(packageDir))
                foreach (var f in Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                    result["package/" + f.Substring(packageDir.Length + 1).Replace('\\', '/')] = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(f);
            foreach (var name in new[] { "receipt.json", "runtime-derived.json" })
            {
                string path = Path.Combine(journals.InstanceDirectory(instance), name);
                result["activation/" + name] = File.Exists(path) ? MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(path) : null;
            }
            result["activation/journal"] = File.Exists(journals.JournalPath(instance)) ? MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(journals.JournalPath(instance)) : null;
            var header = SaveBindingRuntime.Store.Saves.Read(saveName);
            result["save/header"] = header == null || !header.Readable ? null : String.Join(";", header.Metadata.Where(kv => kv.Key.StartsWith(WorldBinding.Prefix, StringComparison.Ordinal) || kv.Key == "UniqueGameId")
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            result["save/bodyWhole"] = header == null ? null : header.BodyWhole;
            var journal = SaveBindingRuntime.Store.Journal(instance);
            result["save/journal"] = journal == null ? null : journal.State;
            return result;
        }
    }
}
