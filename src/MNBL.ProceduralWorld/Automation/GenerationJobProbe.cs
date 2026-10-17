using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-44: the generation job in the running game (generation_job; seed=<n>, otherSeed=<n>), from the main menu, through the game's
    // GenerationCoordinator and engine-work scheduler:
    //   1. the whole pipeline to review (request, source with quality attempts, bundle, verification, scene, runtime navigation), the
    //      engine thread's frame gaps meanwhile, the job's events and timings; released (its bundle, scene staging and directory);
    //   2. cancellation in each phase group: while the producer runs (source), while the bundle is written (compilation), with the scene
    //      install provably queued (engine), and after the inspection world was constructed (engine: it is torn down);
    //   3. a producer crash (its fwg process killed): a failed job in its category;
    //   4. a late callback of a cancelled job while a later job runs: dropped unrun;
    //   after each: no generator process left, the job's own files released, the menu usable. Any failed expectation fails the run.
    internal static class GenerationJobProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Body(context));
            // The scheduler hold is lifted however the probe ends (an exception in a step runs this finally).
            try
            {
                while (stack.Count > 0)
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    var nested = stack.Peek().Current as IEnumerator;
                    if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
                }
            }
            finally { GenerationRuntime.Scheduler.Held = false; }
        }

        private static int GeneratorProcesses() { return Process.GetProcessesByName("fwg").Length + Process.GetProcessesByName("worldbridge").Length; }
        // The consumer queue of the job RunJob is following (a probe step injects another job's late event into it).
        private static ConcurrentQueue<ProgressEvent> currentQueue;


        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "The generation job (MMR-44) in game through GenerationRuntime.Coordinator: the full pipeline to review with frame gaps, cancellation in the source, compilation and engine phase groups (a queued install; a constructed inspection world), a killed producer, a cancelled job's late callback. Campaign registration, commit and play are MMR-45; the setup screen's use of the job is the world_setup probe.";
            uint seed = (uint)context.Int("seed", 42), other = (uint)context.Int("otherSeed", 43);
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); };
            var coordinator = GenerationRuntime.Coordinator; var scheduler = GenerationRuntime.Scheduler;
            var contract = WorldSetupContract.Installed();
            Func<uint, string, WorldChoice> choice = (s, seedSource) => new WorldChoice { CampaignInstanceId = Guid.NewGuid().ToString("D"), Seed = s, SeedSource = seedSource, RecipeVersion = contract.RecipeVersion,
                Overrides = new SortedDictionary<string, long>(contract.Overrides, StringComparer.Ordinal), MaxAttempts = contract.QualityAttempts, ProducerLockSha256 = contract.ProducerLockSha256, Profile = "one-shot" };
            string bundles = CompileStage.BundleRoot();
            context.Outputs["engineThread"] = MNBL.ProceduralWorld.Surface.EngineThread.Id;
            context.Outputs["generatorProcessesBefore"] = GeneratorProcesses();
            var jobs = new List<object>(); context.Outputs["jobs"] = jobs;

            // 1. The whole pipeline to review.
            var full = new Dictionary<string, object>(); jobs.Add(full);
            GenerationJob fullJob = null;
            yield return RunJob(context, coordinator, "full pipeline", choice(seed, "explicit"), null, full, j => fullJob = j);
            expect((string)full["phase"] == "ReadyForReview", "the full pipeline reaches review");
            expect(((List<string>)full["phases"]).SequenceEqual(JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview).Select(p => p.ToString()).Concat(new[] { "ReadyForReview" })),
                "progress covers every phase in order: " + String.Join(" > ", (List<string>)full["phases"]));
            expect((double)full["maxFrameGapSeconds"] < 1.0, "the game kept responding during generation and build (largest frame gap " + ((double)full["maxFrameGapSeconds"]).ToString("0.000") + " s)");
            if (fullJob != null)
            {
                var a = fullJob.Artifacts;
                full["bundleDirectory"] = a.ContainsKey("bundleDirectory") ? a["bundleDirectory"] : null; full["sceneName"] = a.ContainsKey("sceneName") ? a["sceneName"] : null;
                full["navigationActivatable"] = a.ContainsKey("navigationActivatable") ? a["navigationActivatable"] : null; full["requestSeedSource"] = a.ContainsKey("requestSeedSource") ? a["requestSeedSource"] : null;
                full["coordinatePolicy"] = a.ContainsKey("coordinatePolicy") ? a["coordinatePolicy"] : null; full["coordinatePolicyVersion"] = a.ContainsKey("coordinatePolicyVersion") ? a["coordinatePolicyVersion"] : null;   // MMR-64
                // MMR-40/41: the runtime stage's settlement entity checks and playability report.
                foreach (var name in new[] { "settlementEntitiesPassed", "playabilityReport", "playabilityActivatable", "playabilityRoutes", "playabilityRoutesFailed", "playabilityFlags", "playabilityFlagsFailed", "playabilityReportSha256", "playabilityItems" })
                    full[name] = a.ContainsKey(name) ? a[name] : null;
                string gate;
                try { MNBL.ProceduralWorld.Surface.RuntimePlayabilityValidator.Require(SubModule.Store, (string)a["sceneName"], MNBL.ProceduralWorld.Surface.NavigationGate.ReportPath(SubModule.Store, (string)a["sceneName"]), MNBL.ProceduralWorld.Surface.RuntimeWorldBackend.SettlementReportPath((string)a["sceneName"])); gate = "activation allowed"; }
                catch (InvalidOperationException refused) { gate = "refused: " + refused.Message; }
                full["playabilityGate"] = gate;
                expect(gate == "activation allowed" && Equals(full["playabilityActivatable"], true), "the runtime stage persisted an activatable playability report bound to the scene's navigation and settlement reports (MMR-41): " + gate);
                expect(a.ContainsKey("coordinatePolicyVersion") && (string)a["coordinatePolicyVersion"] == MNBL.ProceduralWorld.Surface.PeriodicWorldCoordinates.PolicyVersion && a["coordinatePolicy"] is string,
                    "the job record names the periodic coordinate policy its navigation report was made under (MMR-64)");
                expect(a.ContainsKey("bundleDirectory") && Directory.Exists((string)a["bundleDirectory"]) && a.ContainsKey("navigationActivatable") && a["navigationActivatable"] is bool && (bool)a["navigationActivatable"]
                       && File.Exists(Path.Combine(MNBL.ProceduralWorld.Surface.RuntimeWorldBackend.SceneFolder((string)a["sceneName"]), "scene.xscene")),
                    "at review the bundle is published and verified, the scene installed, the navigation validated on the engine");
            }
            yield return Release(context, coordinator, fullJob, full);
            expect((bool)full["jobDirectoryReleased"] && (!full.ContainsKey("bundleReleased") || (bool)full["bundleReleased"]) && (int)full["generatorProcessesAfter"] == 0, "releasing the reviewed job removes its bundle and directory; no generator process left");
            context.Checkpoint();

            // 2. Cancellation in each phase group.
            double since = -1;
            var source = new Dictionary<string, object>(); jobs.Add(source);
            GenerationJob sourceJob = null;
            yield return RunJob(context, coordinator, "cancel during source generation", choice(other, "random"), (j, r) =>
            {
                if (j.Phase != JobPhase.GeneratingSource || Process.GetProcessesByName("fwg").Length == 0) return false;
                if (since < 0) since = context.Seconds;
                if (context.Seconds - since < 2) return false;
                r["producerRunningSeconds"] = context.Seconds - since; r["cancelAccepted"] = coordinator.Cancel(j); return true;
            }, source, j => sourceJob = j);
            source["requestSeedSource"] = sourceJob == null ? null : sourceJob.Get<string>("requestSeedSource");
            var generateRun = sourceJob == null ? null : (sourceJob.Get<List<object>>("processes") ?? new List<object>()).Cast<Dictionary<string, object>>().LastOrDefault(p => (string)p["role"] == "generate");
            source["bridgeStop"] = generateRun == null ? null : new Dictionary<string, object> { ["exitCode"] = generateRun.ContainsKey("exitCode") ? generateRun["exitCode"] : null,
                ["stopRequestedAtMs"] = generateRun.ContainsKey("stopRequestedAtMs") ? generateRun["stopRequestedAtMs"] : null, ["terminatedAfterGraceMs"] = generateRun.ContainsKey("terminatedAfterGraceMs") ? generateRun["terminatedAfterGraceMs"] : null };
            yield return Release(context, coordinator, sourceJob, source);
            Cancelled(expect, source, "source generation");
            expect(source["requestSeedSource"] as string == "random", "a Random World seed is recorded in the request as random (--seed-source random)");
            expect(generateRun != null && generateRun.ContainsKey("stopRequestedAtMs") && !generateRun.ContainsKey("terminatedAfterGraceMs") && Convert.ToInt32(generateRun["exitCode"]) == 4,
                "the bridge stopped through its cancel file (exit 4, cancelled), without the owned job having to end it");
            context.Checkpoint();

            var compile = new Dictionary<string, object>(); jobs.Add(compile);
            GenerationJob compileJob = null;
            Directory.CreateDirectory(bundles);
            var rootBefore = new HashSet<string>(Directory.GetDirectories(bundles), StringComparer.OrdinalIgnoreCase);
            // Cancelled while the bridge writes the bundle's staging directory (after its 3-8 s compile), so the cleanup has staging to remove.
            yield return RunJob(context, coordinator, "cancel during compilation", choice(seed, "explicit"), (j, r) =>
            {
                if (j.Phase != JobPhase.Compiling) return false;
                var staging = Directory.GetDirectories(bundles, ".staging-*").Where(d => !rootBefore.Contains(d)).ToList();
                if (staging.Count == 0) return false;
                r["stagingSeen"] = staging.Select(Path.GetFileName).ToList(); r["cancelAccepted"] = coordinator.Cancel(j); return true;
            }, compile, j => compileJob = j);
            yield return Release(context, coordinator, compileJob, compile);
            var rootAfter = new HashSet<string>(Directory.GetDirectories(bundles), StringComparer.OrdinalIgnoreCase);
            compile["rootAdded"] = rootAfter.Where(d => !rootBefore.Contains(d)).Select(Path.GetFileName).ToList();
            compile["rootRemoved"] = rootBefore.Where(d => !rootAfter.Contains(d)).Select(Path.GetFileName).ToList();
            Cancelled(expect, compile, "compilation");
            expect(compile.ContainsKey("stagingSeen") && ((List<string>)compile["rootAdded"]).Count == 0 && ((List<string>)compile["rootRemoved"]).Count == 0,
                "cancelled while the bundle was being staged: its staging (and any publish) removed, nothing else in the bundle root touched"); since = -1;
            context.Checkpoint();

            var queued = new Dictionary<string, object>(); jobs.Add(queued);
            GenerationJob queuedJob = null;
            long withdrawnBefore = scheduler.Withdrawn, droppedBefore = scheduler.DroppedStale;
            yield return RunJob(context, coordinator, "cancel with the scene install queued", choice(seed, "explicit"), (j, r) =>
            {
                if (j.Phase != JobPhase.BuildingScene) return false;
                if (!scheduler.Held) { scheduler.Held = true; return false; }
                if (scheduler.Pending == 0) return false;
                r["queuedItems"] = scheduler.Pending; r["cancelAccepted"] = coordinator.Cancel(j); scheduler.Held = false; return true;
            }, queued, j => queuedJob = j);
            scheduler.Held = false;
            queued["installNeverRan"] = queuedJob != null && queuedJob.Artifacts.ContainsKey("install") == false;
            queued["withdrawnOrDropped"] = (scheduler.Withdrawn - withdrawnBefore) + (scheduler.DroppedStale - droppedBefore);
            yield return Release(context, coordinator, queuedJob, queued);
            Cancelled(expect, queued, "engine construction (install queued)");
            expect((bool)queued["installNeverRan"] && (long)queued["withdrawnOrDropped"] >= 1, "the queued scene install was withdrawn or dropped, never run");
            context.Checkpoint();

            var constructed = new Dictionary<string, object>(); jobs.Add(constructed);
            GenerationJob constructedJob = null;
            yield return RunJob(context, coordinator, "cancel after the inspection world was constructed", choice(seed, "explicit"), (j, r) =>
            {
                if (j.Phase != JobPhase.ValidatingRuntime || !j.OwnedResources.Contains("inspection world")) return false;
                if (!scheduler.Held) { scheduler.Held = true; return false; }
                if (scheduler.Pending == 0) return false;
                r["cancelAccepted"] = coordinator.Cancel(j); scheduler.Held = false; return true;
            }, constructed, j => constructedJob = j);
            scheduler.Held = false;
            yield return Release(context, coordinator, constructedJob, constructed);
            Cancelled(expect, constructed, "engine construction (world constructed)");
            var cleanup = constructedJob == null ? new List<string>() : constructedJob.Cleanup.ToList();
            expect(cleanup.Contains("released inspection world") && cleanup.Any(c => c.StartsWith("released world generation", StringComparison.Ordinal)),
                "the constructed inspection world was torn down on the engine thread and its world generation retired: " + String.Join("; ", cleanup));
            context.Checkpoint();

            // 3. A producer crash, and 4. a cancelled job's late callback while it runs.
            since = -1; bool lateRan = false; EngineWorkScheduler.Handle<int> late = null;
            var crash = new Dictionary<string, object>(); jobs.Add(crash);
            GenerationJob crashJob = null;
            DateTime crashStart = DateTime.Now;
            yield return RunJob(context, coordinator, "producer crash", choice(other, "explicit"), (j, r) =>
            {
                if (late == null && queuedJob != null)
                {
                    late = scheduler.Post(queuedJob.Ticket, "late callback of a cancelled job", () => { lateRan = true; return 0; });
                    queuedJob.Report(null, null, "late_progress");
                    var lateEvent = queuedJob.Events.LastOrDefault();
                    if (lateEvent != null && currentQueue != null) { currentQueue.Enqueue(lateEvent); r["lateEventInjected"] = true; }
                }
                if (j.Phase != JobPhase.GeneratingSource) return false;
                // Only the producer this job's bridge started (none other runs: the job's are the only generator processes).
                var producers = Process.GetProcessesByName("fwg").Where(p => { try { return p.StartTime >= crashStart; } catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { return false; } }).ToArray();
                if (producers.Length == 0) return false;
                if (since < 0) since = context.Seconds;
                if (context.Seconds - since < 2) return false;
                var killed = new List<int>();
                foreach (var p in producers) { try { p.Kill(); killed.Add(p.Id); } catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { } }
                r["killedProducers"] = killed; return true;
            }, crash, j => crashJob = j);
            yield return Release(context, coordinator, crashJob, crash);
            expect((string)crash["phase"] == "Failed" && crash["errorCategory"] as string == "BadSource" && (int)crash["generatorProcessesAfter"] == 0 && (bool)crash["jobDirectoryReleased"],
                "a crashed producer fails the job as a bad source (" + crash["errorCode"] + "), with no generator process left and its files released");
            context.Outputs["lateCallback"] = new Dictionary<string, object> { ["posted"] = late != null, ["ran"] = lateRan, ["dropped"] = late != null && late.Task.IsFaulted && late.Task.Exception.GetBaseException() is StaleWorkException };
            expect(late != null && !lateRan && late.Task.IsFaulted, "a cancelled job's late engine callback, posted while a later job ran, was dropped unrun");
            expect(crash.ContainsKey("lateEventInjected") && (int)crash["droppedStale"] >= 1, "a cancelled job's late progress event, delivered to the later job's consumer, was dropped as stale");
            context.Checkpoint();

            // The menu, processes, staging and thread affinity at the end.
            context.Outputs["scheduler"] = new Dictionary<string, object> { ["ran"] = scheduler.Ran, ["droppedStale"] = scheduler.DroppedStale, ["withdrawn"] = scheduler.Withdrawn,
                ["affinityViolations"] = scheduler.AffinityViolations, ["longestItemSeconds"] = scheduler.LongestItemSeconds, ["longestItem"] = scheduler.LongestItemLabel };
            context.Outputs["finalTopScreen"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName;
            context.Outputs["generatorProcessesAfter"] = GeneratorProcesses();
            context.Outputs["bundleStagingLeft"] = Directory.Exists(bundles) ? Directory.GetDirectories(bundles, ".staging-*").Length : 0;
            expect(ScreenManager.TopScreen is MBInitialScreenBase, "the main menu is usable at the end");
            expect(scheduler.AffinityViolations == 0 && jobs.Cast<Dictionary<string, object>>().All(r => (int)r["stageOnEngineThread"] == 0 && (int)r["engineItemOffEngineThread"] == 0),
                "thread affinity: no stage ran on the engine thread and every engine item ran on it");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("Generation job expectations failed: " + String.Join("; ", failures));
        }

        private static void Cancelled(Action<bool, string> expect, Dictionary<string, object> r, string group)
        {
            expect((string)r["phase"] == "Cancelled" && r.ContainsKey("cancelAccepted") && (bool)r["cancelAccepted"] && (int)r["generatorProcessesAfter"] == 0 && (bool)r["jobDirectoryReleased"]
                   && ((List<object>)r["owned"]).Count == 0 && (!r.ContainsKey("bundleReleased") || (bool)r["bundleReleased"]),
                "cancelled during " + group + " (at " + r["triggeredAt"] + "): Cancelled, its own resources released (" + String.Join("; ", (List<string>)r["cleanup"]) + "), no generator process left, its files gone");
        }

        // Runs a job to review or a final state, calling drive each frame until it returns true, and records what happened.
        private static IEnumerator RunJob(ProbeContext context, GenerationCoordinator coordinator, string label, WorldChoice choice, Func<GenerationJob, Dictionary<string, object>, bool> drive,
            Dictionary<string, object> record, Action<GenerationJob> started)
        {
            var queue = new ConcurrentQueue<ProgressEvent>(); var sink = new ProgressSink();
            currentQueue = queue;
            var job = coordinator.Start(choice, queue.Enqueue, j => sink.Follow(j.Id));
            started(job);
            record["label"] = label; record["jobId"] = job.Id; record["seed"] = choice.Seed;
            context.Stage("generation_job:" + label);
            var phases = new List<string>(); var codes = new List<string>();
            double start = context.Seconds, last = start, maxGap = 0; bool driven = drive == null;
            Func<bool> settled = () => { var p = job.Phase; return (p == JobPhase.ReadyForReview || JobStateMachine.IsTerminal(p)) && job.Completion.IsCompleted; };
            while (!settled() && context.Seconds - start < 600)
            {
                ProgressEvent e;
                while (queue.TryDequeue(out e)) if (sink.Accept(e)) { if (phases.Count == 0 || phases[phases.Count - 1] != e.Phase.ToString()) phases.Add(e.Phase.ToString()); codes.Add(e.MessageCode); }
                if (!driven && drive(job, record)) { driven = true; record["triggeredAt"] = job.Phase.ToString(); }
                yield return null;
                maxGap = Math.Max(maxGap, context.Seconds - last); last = context.Seconds;
            }
            ProgressEvent tail;
            while (queue.TryDequeue(out tail)) if (sink.Accept(tail)) { if (phases.Count == 0 || phases[phases.Count - 1] != tail.Phase.ToString()) phases.Add(tail.Phase.ToString()); codes.Add(tail.MessageCode); }
            var r = coordinator.Record(job);
            record["phase"] = job.Phase.ToString(); record["seconds"] = context.Seconds - start; record["maxFrameGapSeconds"] = maxGap; record["driven"] = driven;
            record["phases"] = phases; record["messageCodes"] = codes; record["events"] = sink.Accepted; record["droppedStale"] = sink.DroppedStale; record["droppedOutOfOrder"] = sink.DroppedOutOfOrder;
            record["timings"] = r["timings"]; record["cleanup"] = job.Cleanup.ToList(); record["owned"] = job.OwnedResources.Cast<object>().ToList();
            record["errorCategory"] = job.Error == null ? null : job.Error.Category.ToString(); record["errorCode"] = job.Error == null ? null : job.Error.Code; record["failureDetail"] = job.FailureDetail;
            var affinity = (Dictionary<string, object>)r["affinity"];
            record["stageOnEngineThread"] = affinity["stageOnEngineThread"]; record["engineItemOffEngineThread"] = affinity["engineItemOffEngineThread"];
            record["eventsLog"] = File.Exists(job.File("events.jsonl")) ? File.ReadAllLines(job.File("events.jsonl")).Length : 0;
            context.Checkpoint();
        }

        private static IEnumerator Release(ProbeContext context, GenerationCoordinator coordinator, GenerationJob job, Dictionary<string, object> record)
        {
            if (job == null) yield break;
            string bundle = job.Artifacts.ContainsKey("bundleDirectory") ? (string)job.Artifacts["bundleDirectory"] : null;
            bool owned = job.Artifacts.ContainsKey("bundleExisted") && !(bool)job.Artifacts["bundleExisted"];
            var releasing = coordinator.Release(job);
            double start = context.Seconds;
            while (!releasing.IsCompleted && context.Seconds - start < 120) yield return null;
            start = context.Seconds;
            while (GeneratorProcesses() > 0 && context.Seconds - start < 15) yield return null;
            record["phaseAfterRelease"] = job.Phase.ToString();
            record["jobDirectoryReleased"] = !Directory.Exists(job.Root);
            if (bundle != null && owned) record["bundleReleased"] = !Directory.Exists(bundle);
            record["generatorProcessesAfter"] = GeneratorProcesses();
            record["cleanup"] = job.Cleanup.ToList(); record["owned"] = job.OwnedResources.Cast<object>().ToList();
        }
    }
}
