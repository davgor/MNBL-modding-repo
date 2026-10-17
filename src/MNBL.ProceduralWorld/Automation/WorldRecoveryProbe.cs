using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-47: crash recovery in the running game, over two launches (world_recovery; phase=crash|resume, seed=<n>).
    //   crash:  the store's worlds are recorded; abandoned staging and an earlier version's job directory are planted; the setup generates
    //           the seed and, while its job is generating the source, the process is terminated (Process.Kill: no cleanup runs).
    //   resume: the startup pass (SaveBindingRuntime.RunStartup) verified the data root and removed the planted staging and leftover, and
    //           kept the crashed job; no bundle, package, activation or save changed. The setup offers the crashed job (its seed); Continue
    //           finishes it in its own directory (the worldbridge resumes its attempt state; the same world as the seed's); a job planted as
    //           made by another generator is then offered only for Discard, which removes just it. A cancelled generation shows its reference,
    //           and its diagnostics are retained under it.
    internal static class WorldRecoveryProbe
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(context.Str("phase", "crash") == "resume" ? Resume(context) : Crash(context));
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                var nested = stack.Peek().Current as IEnumerator;
                if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
            }
        }

        private static string PlanPath(ProbeContext context) { return Path.Combine(context.Directory, "world-recovery-plan.json"); }

        private static IEnumerator Crash(ProbeContext context)
        {
            uint seed = (uint)context.Int("seed", 42);
            context.Outputs["scope"] = "MMR-47 crash phase: plant abandoned staging and a leftover job directory, generate a seed, terminate the process while the source is generated.";
            string store = SubModule.Store;
            var plan = new Dictionary<string, object> { ["seed"] = seed, ["worldsBefore"] = Worlds(store) };
            // Abandoned staging and an earlier version's job directory (the interim job's files).
            string staging = Path.Combine(store, "bundles", ".staging-mmr47-" + context.ProcessId);
            Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "partial.bin"), "partial");
            string leftover = Path.Combine(store, "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(leftover); File.WriteAllText(Path.Combine(leftover, "request.json"), "{\"seed\":1}"); File.WriteAllText(Path.Combine(leftover, "result.json"), "{}");
            plan["planted"] = new Dictionary<string, object> { ["staging"] = Path.GetFileName(staging), ["leftover"] = Path.GetFileName(leftover) };
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            vm.SeedText = seed.ToString(); yield return null;
            screen.HandleInput(k => k == SetupInput.Confirm, false);
            // Wait for the source's attempt to be running (the bridge's attempt state exists), then a little longer.
            start = context.Seconds;
            while (context.Seconds - start < 120)
            {
                if (model.JobRoot != null && File.Exists(Path.Combine(model.JobRoot, "state.json")) && model.Progress != null && model.Progress.Phase == JobPhase.GeneratingSource) break;
                if (model.State != SetupState.Generating) throw new InvalidOperationException("The generation ended before the crash point: " + model.State + " " + model.Error);
                yield return null;
            }
            double reached = context.Seconds; while (context.Seconds - reached < 3) yield return null;
            plan["jobRoot"] = model.JobRoot; plan["jobId"] = Path.GetFileName(model.JobRoot); plan["phaseAtCrash"] = model.Progress == null ? null : model.Progress.Phase.ToString();
            plan["stateAtCrash"] = File.Exists(Path.Combine(model.JobRoot, "state.json")) ? File.ReadAllText(Path.Combine(model.JobRoot, "state.json")) : null;
            plan["choiceRecorded"] = File.Exists(Path.Combine(model.JobRoot, "choice.json"));
            plan["generatorProcesses"] = Process.GetProcessesByName("fwg").Length + Process.GetProcessesByName("worldbridge").Length;
            File.WriteAllText(PlanPath(context), Json.Serialize(plan));
            context.Outputs["plan"] = plan;
            context.Outputs["crashUtc"] = DateTime.UtcNow.ToString("o");
            context.Stage("world_recovery:terminating");
            context.Checkpoint();
            Process.GetCurrentProcess().Kill();
            yield return null;
        }

        private static IEnumerator Resume(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-47 resume phase: the startup cleanup, the crashed job offered and continued in its own directory, a job of another generator discarded, a cancelled generation's reference and diagnostics.";
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); context.Stage((ok ? "ok: " : "FAIL: ") + what); };
            string store = SubModule.Store;
            var plan = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(PlanPath(context)));
            context.Outputs["plan"] = plan;
            string crashedRoot = (string)plan["jobRoot"], crashedId = (string)plan["jobId"];
            var planted = (IDictionary<string, object>)plan["planted"];
            // 1. The startup pass.
            var startup = SaveBindingRuntime.Startup;
            context.Outputs["startup"] = startup;
            var cleanup = startup != null && startup.ContainsKey("cleanup") ? (IDictionary<string, object>)startup["cleanup"] : null;
            var staging = cleanup != null && cleanup.ContainsKey("staging") ? cleanup["staging"] as IDictionary<string, object> : null;
            var removedStaging = staging == null ? new List<string>() : ((IEnumerable)staging["removed"]).Cast<object>().Select(o => (string)o).ToList();
            var removedJobs = cleanup == null || !cleanup.ContainsKey("finishedJobsRemoved") ? new List<string>() : ((IEnumerable)cleanup["finishedJobsRemoved"]).Cast<object>().Select(o => (string)o).ToList();
            var worldsNow = Worlds(store);
            var worldsBefore = (IDictionary<string, object>)plan["worldsBefore"];
            bool worldsKept = worldsBefore.All(kv => worldsNow.ContainsKey(kv.Key) && Equals(Convert.ToString(worldsNow[kv.Key]), Convert.ToString(kv.Value)));
            context.Outputs["worldsKept"] = worldsKept;
            expect(startup != null && Equals(startup["root"], "verified") && removedStaging.Any(r => r.EndsWith((string)planted["staging"], StringComparison.Ordinal))
                   && removedJobs.Contains((string)planted["leftover"]) && Directory.Exists(crashedRoot) && !removedJobs.Contains(crashedId) && worldsKept,
                "the startup pass verified the data root, removed the planted abandoned staging and the earlier version's job directory, kept the crashed job, and changed no bundle, package, activation or save");
            // A job of another generator, a minute older than the crashed one: offered right after it (the store may hold older leftovers).
            string otherRoot = Path.Combine(store, "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherRoot);
            File.WriteAllText(Path.Combine(otherRoot, "choice.json"), Json.Serialize(new Dictionary<string, object> { ["seed"] = 7, ["seedSource"] = "explicit", ["campaignInstanceId"] = Guid.NewGuid().ToString("D"),
                ["recipeVersion"] = 3, ["maxAttempts"] = 3, ["producerLockSha256"] = new string('b', 64), ["profile"] = "one-shot", ["overrides"] = new Dictionary<string, object>() }));
            File.WriteAllText(Path.Combine(otherRoot, "events.jsonl"), "{\"phase\":\"Compiling\"}\n");
            var crashedTime = new[] { Directory.GetLastWriteTimeUtc(crashedRoot) }.Concat(Directory.GetFiles(crashedRoot).Select(File.GetLastWriteTimeUtc)).Max().AddMinutes(-1);
            foreach (var f in Directory.GetFiles(otherRoot)) File.SetLastWriteTimeUtc(f, crashedTime);
            Directory.SetLastWriteTimeUtc(otherRoot, crashedTime);
            // 2. The setup offers the crashed job.
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            yield return Frames(20);
            var offered = model.Offered;
            var offer = new Dictionary<string, object> { ["offeredId"] = offered == null ? null : offered.Id, ["phase"] = offered == null ? null : offered.Phase, ["resumable"] = offered != null && offered.Resumable,
                ["seed"] = offered == null ? (object)null : offered.Seed, ["text"] = vm.UnfinishedText, ["unfinished"] = model.Unfinished.Select(j => j.Report()).ToList() };
            context.Outputs["offer"] = offer;
            context.Outputs["offerScreenshot"] = Shot(context, "unfinished-offered");
            expect(offered != null && offered.Id == crashedId && offered.Resumable && offered.Seed == Convert.ToUInt32(plan["seed"]) && vm.HasUnfinished && vm.CanResume && vm.UnfinishedText.Contains(plan["seed"].ToString()),
                "the setup offers the crashed job (its seed, stopped while " + (offered == null ? "?" : offered.Phase) + ") to continue or discard");
            // 3. Continue it.
            vm.ExecuteResume();
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
            var resumed = new Dictionary<string, object> { ["seconds"] = context.Seconds - start, ["state"] = model.State.ToString(), ["jobRoot"] = model.JobRoot, ["error"] = model.Error == null ? null : model.Error.ToString(),
                ["worldContentId"] = model.Preview == null ? null : model.Preview.WorldContentId };
            string events = Path.Combine(crashedRoot, "events.jsonl"), stateFile = Path.Combine(crashedRoot, "state.json");
            resumed["requestResumed"] = File.Exists(events) && File.ReadAllText(events).Contains("request_resumed");
            if (File.Exists(stateFile))
            {
                var state = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(stateFile));
                resumed["bridgeStatus"] = state["status"]; resumed["attempts"] = ((IEnumerable)state["attempts"]).Cast<IDictionary<string, object>>().Select(a => a["index"] + " runs " + a["runs"] + " " + a["state"]).ToList();
            }
            context.Outputs["resumed"] = resumed;
            // The interrupted attempt ran again (a second run of the same attempt, accepted), and the world is the seed's own: the bundle the
            // store already held for it before the crash.
            var attemptRows = resumed.ContainsKey("attempts") ? ((IEnumerable)resumed["attempts"]).Cast<string>().ToList() : new List<string>();
            bool rerun = attemptRows.Any(a => a.StartsWith("0 runs ", StringComparison.Ordinal) && a.EndsWith(" accepted", StringComparison.Ordinal) && !a.StartsWith("0 runs 1 ", StringComparison.Ordinal));
            bool sameWorld = model.Preview != null && worldsBefore.ContainsKey("bundles/" + model.Preview.WorldContentId);
            resumed["attemptRerun"] = rerun; resumed["sameWorldAsBefore"] = sameWorld;
            expect(model.State == SetupState.Generated && model.JobRoot != null && String.Equals(Path.GetFullPath(model.JobRoot), Path.GetFullPath(crashedRoot), StringComparison.OrdinalIgnoreCase)
                   && (bool)resumed["requestResumed"] && model.Choice.Seed == Convert.ToUInt32(plan["seed"]) && model.Preview != null && model.Resumed == 1 && rerun && sameWorld && Equals(resumed["bridgeStatus"], "accepted"),
                "Continue finishes the crashed job in its own directory with its own request (resumed, not replaced): the interrupted attempt runs again and gives the seed's own world, ready for review");
            yield return Frames(20);
            context.Outputs["resumedScreenshot"] = Shot(context, "resumed-preview");
            context.Checkpoint();
            // 4. The other generator's job: offered for Discard only; Discard removes only it.
            screen.HandleInput(k => k == SetupInput.Exit, false); yield return Frames(5);   // back to the seed (the world kept)
            var worldsBeforeDiscard = Worlds(store);
            var nextOffer = model.Offered;
            bool discardOffered = nextOffer != null && String.Equals(nextOffer.Root, otherRoot, StringComparison.OrdinalIgnoreCase) && !nextOffer.Resumable && model.CanDiscard && !model.CanResume;
            context.Outputs["otherOfferScreenshot"] = Shot(context, "other-generator-offered");
            vm.ExecuteDiscard(); yield return Frames(5);
            var afterDiscard = Worlds(store);
            context.Outputs["discard"] = new Dictionary<string, object> { ["offered"] = discardOffered, ["text"] = vm.UnfinishedText, ["removed"] = !Directory.Exists(otherRoot), ["problem"] = model.DiscardProblem,
                ["worldsKept"] = worldsBeforeDiscard.All(kv => afterDiscard.ContainsKey(kv.Key) && Equals(afterDiscard[kv.Key], kv.Value)), ["crashedKept"] = Directory.Exists(crashedRoot) };
            expect(discardOffered && !Directory.Exists(otherRoot) && Directory.Exists(crashedRoot) && model.DiscardProblem == null && (model.Offered == null || !String.Equals(model.Offered.Root, otherRoot, StringComparison.OrdinalIgnoreCase))
                   && worldsBeforeDiscard.All(kv => afterDiscard.ContainsKey(kv.Key) && Equals(afterDiscard[kv.Key], kv.Value)),
                "a job made by another generator is offered only for Discard, and Discard removes just its directory (no bundle, package, activation or save changes); the next leftover, if any, is offered after it");
            context.Checkpoint();
            // 5. A cancelled generation: its reference on the screen, its diagnostics retained.
            vm.SeedText = "43"; yield return null;
            screen.HandleInput(k => k == SetupInput.Confirm, false);
            // The reviewed (resumed) world is released by the new Generate: the setup keeps one job.
            start = context.Seconds;
            while (Directory.Exists(crashedRoot) && context.Seconds - start < 60) yield return null;
            context.Outputs["resumedReleasedByNextGenerate"] = !Directory.Exists(crashedRoot);
            start = context.Seconds;
            while (model.State == SetupState.Generating && (model.JobRoot == null || String.Equals(model.JobRoot, crashedRoot, StringComparison.OrdinalIgnoreCase)) && context.Seconds - start < 60) yield return null;
            string cancelledRoot = model.JobRoot;
            double began = context.Seconds; while (model.State == SetupState.Generating && context.Seconds - began < 2) yield return null;
            screen.HandleInput(k => k == SetupInput.Exit, false);
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 120) yield return null;
            yield return Frames(20);
            var error = model.Error;
            var record = error == null || error.CorrelationId == null || model.Diagnostics == null ? null : model.Diagnostics.Read(error.CorrelationId);
            string folder = error == null || error.CorrelationId == null || model.Diagnostics == null ? null : model.Diagnostics.Folder(error.CorrelationId);
            vm.ErrorView.ExecuteToggleDetails(); yield return Frames(10);
            context.Outputs["cancelled"] = new Dictionary<string, object> { ["category"] = error == null ? null : error.Category.ToString(), ["correlationId"] = error == null ? null : error.CorrelationId,
                ["reference"] = vm.ErrorView.ReferenceText, ["details"] = vm.ErrorView.DetailsText == null ? null : vm.ErrorView.DetailsText.Replace(store, "<store>"),
                ["recordFiles"] = folder != null && Directory.Exists(folder) ? Directory.GetFiles(folder).Select(Path.GetFileName).ToList() : null };
            context.Outputs["cancelledScreenshot"] = Shot(context, "cancelled-with-reference");
            expect(error != null && error.Category == GenerationErrorCategory.InterruptedGeneration && error.CorrelationId != null && record != null && vm.ErrorView.ReferenceText.Contains(error.CorrelationId)
                   && File.Exists(Path.Combine(folder, "events.jsonl")) && vm.ErrorView.DetailsText.Contains(error.CorrelationId),
                "a cancelled generation shows its recovery message with its reference; its diagnostics (the error and the job's records) are retained under it and named under Details");
            vm.ErrorView.ExecuteToggleDetails();
            screen.HandleInput(k => k == SetupInput.Exit, false); yield return Frames(10);   // close: the jobs are released
            var closing = model;
            start = context.Seconds; while (!closing.Releasing.IsCompleted && context.Seconds - start < 60) yield return null;
            context.Outputs["cancelledReleasedOnClose"] = cancelledRoot != null && !Directory.Exists(cancelledRoot);
            expect((bool)context.Outputs["resumedReleasedByNextGenerate"] && cancelledRoot != null && !Directory.Exists(cancelledRoot),
                "the resumed world, once reviewed, is released by the next Generate, and closing the setup releases the cancelled job's directory");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("World recovery expectations failed: " + String.Join("; ", failures));
        }

        // The store's worlds a player's campaigns depend on: bundles (not staging), packages, activation records and the game's saves, by
        // name and size.
        private static Dictionary<string, object> Worlds(string store)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in new[] { "bundles", "worlds", "activations" })
            {
                string root = Path.Combine(store, kind);
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal) && !Path.GetFileName(d).Contains(DurableFile.TemporaryMarker)))
                    result[kind + "/" + Path.GetFileName(dir)] = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                foreach (var file in Directory.GetFiles(root).Where(f => !Path.GetFileName(f).Contains(DurableFile.TemporaryMarker)))
                    result[kind + "/" + Path.GetFileName(file)] = new FileInfo(file).Length;
            }
            string saves = BannerlordSaveCatalog.Folder;
            if (Directory.Exists(saves)) foreach (var file in Directory.GetFiles(saves, "*.sav")) result["saves/" + Path.GetFileName(file)] = new FileInfo(file).Length;
            return result;
        }

        private static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }

        private static string Shot(ProbeContext context, string label)
        {
            string path = Path.Combine(context.Directory, "world-recovery-" + context.ProcessId + "-" + label + ".bmp");
            Utilities.TakeScreenshot(path);
            return path;
        }
    }
}
