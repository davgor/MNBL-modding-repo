using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.Automation
{
    // Executes the registered developer-menu callbacks, with failures isolated to owned child jobs.
    internal static class GenerationMenuProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            RequireMenu();
            var module = SubModule.Instance;
            if (module == null || (module.GenerationTask != null && !module.GenerationTask.IsCompleted))
                throw new InvalidOperationException("Generation suite requires an idle module.");
            var cases = new[] { "seed42", "seed43", "invalid", "abort", "cancel", "recovery" };
            var actions = new[] { "fwgc_probe_42", "fwgc_probe_43", "fwgc_reject_probe", "fwgc_abort_probe", "fwgc_probe_42", "fwgc_probe_42" };
            var expected = new[] { "source_generated", "source_generated", "producer_refused", "controlled_abort", "cancelled", "source_generated" };
            var reports = new List<object>();
            var markers = new Dictionary<string, string>();
            context.Outputs["cases"] = reports;
            context.Outputs["scope"] = "Registered in-game developer menu callbacks invoked through Module.ExecuteInitialStateOptionWithId; not physical mouse input. Tick timing measures application responsiveness, not isolated render or CPU cost.";
            context.Outputs["controlledAbort"] = "The producer abort case deliberately terminates the owned child job; it is not a naturally occurring generator crash.";
            try
            {
                for (int i = 0; i < cases.Length; i++)
                {
                    RequireMenu();
                    string marker = Path.Combine(SubModule.Store, "latest-heightfield-" + (i == 1 ? "43" : "42") + ".json");
                    string markerBefore = File.Exists(marker) ? GenerationProbe.Hash(marker) : null;
                    Task previous = module.GenerationTask;
                    Invoke(actions[i]);
                    Task task = module.GenerationTask;
                    if (task == null || ReferenceEquals(task, previous)) throw new InvalidOperationException("Menu action did not start a fresh job: " + actions[i]);
                    context.TrackOwnedWork(() => Invoke("fwgc_cancel"), () => task.IsCompleted);
                    context.Stage("generation_" + cases[i] + "_started");
                    double start = context.Seconds, last = start, maxGap = 0;
                    int ticks = 0;
                    bool cancelled = false;
                    var stages = new List<string>();
                    while (!task.IsCompleted)
                    {
                        RequireMenu();
                        double now = context.Seconds;
                        maxGap = Math.Max(maxGap, now - last);
                        last = now;
                        ticks++;
                        string stage = module.GenerationStage;
                        if (stage != null && (stages.Count == 0 || stages[stages.Count - 1] != stage)) stages.Add(stage);
                        if (cases[i] == "cancel" && !cancelled && now - start >= 0.3 && module.GenerationJobRoot != null)
                        {
                            Invoke("fwgc_cancel");
                            cancelled = true;
                            context.Stage("generation_cancel_requested");
                        }
                        if (now - start > 90)
                        {
                            Invoke("fwgc_cancel");
                            double cleanup = context.Seconds;
                            while (!task.IsCompleted && context.Seconds - cleanup < 10) yield return null;
                            throw new TimeoutException("Generation case exceeded 90 seconds: " + cases[i]);
                        }
                        yield return null;
                    }
                    maxGap = Math.Max(maxGap, context.Seconds - last);
                    if (task.IsFaulted) throw new InvalidOperationException("Generation UI handler escaped its error boundary.", task.Exception);
                    if (module.GenerationOutcome != expected[i])
                        throw new InvalidOperationException(cases[i] + " outcome " + module.GenerationOutcome + ", expected " + expected[i]);
                    RequireMenu();
                    string jobRoot = module.GenerationJobRoot;
                    if (String.IsNullOrEmpty(jobRoot)) throw new InvalidDataException("Generation case has no job provenance.");
                    string resultPath = Path.Combine(jobRoot, "result.json");
                    if (!File.Exists(resultPath)) throw new InvalidDataException("Generation case has no result document.");
                    string markerAfter = File.Exists(marker) ? GenerationProbe.Hash(marker) : null;
                    if (expected[i] != "source_generated" && markerBefore != markerAfter)
                        throw new InvalidDataException("Failed or cancelled job changed the accepted heightfield marker.");
                    if (expected[i] == "source_generated")
                    {
                        var published = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(marker));
                        string fieldPath = Path.Combine(jobRoot, "diagnostic", "heightfield.bin");
                        string heightHash = GenerationProbe.Hash(fieldPath);
                        if ((string)published["job"] != Path.GetFileName(jobRoot) || (string)published["heightfieldHash"] != heightHash)
                            throw new InvalidDataException("Menu action published an unbound heightfield.");
                        markers[cases[i]] = heightHash;
                    }
                    reports.Add(new
                    {
                        name = cases[i], action = actions[i], outcome = module.GenerationOutcome,
                        failure = module.GenerationFailure, elapsedSeconds = context.Seconds - start,
                        applicationTicks = ticks, maxApplicationTickGapSeconds = maxGap, stages,
                        menuScreen = ScreenManager.TopScreen.GetType().FullName,
                        jobRoot, resultPath, resultSha256 = GenerationProbe.Hash(resultPath),
                        markerBefore, markerAfter, cancellationRequested = cancelled
                    });
                    context.Stage("generation_" + cases[i] + "_finished outcome=" + module.GenerationOutcome);
                    context.Checkpoint();
                    // Capture both successful publication and every failure return on the real menu.
                    string capture = "generation-" + cases[i];
                    BaselineScenarioProbe.Capture(context, capture);
                    string imagePath = (string)context.Outputs[capture + "Screenshot"];
                    double captureStart = context.Seconds;
                    do
                    {
                        yield return null;
                        RequireMenu();
                        if (context.Seconds - captureStart > 30) throw new TimeoutException("Menu screenshot not written.");
                    } while (!File.Exists(imagePath) || new FileInfo(imagePath).Length == 0);
                }
                if (markers["seed42"] == markers["seed43"] || markers["seed42"] != markers["recovery"])
                    throw new InvalidDataException("Seed-specific output or same-seed recovery replay mismatch.");
                context.Outputs["heightfieldHashes"] = markers;
                context.Outputs["menuRecoveryPassed"] = true;
            }
            finally
            {
                if (module.GenerationTask != null && !module.GenerationTask.IsCompleted) Invoke("fwgc_cancel");
            }
        }

        private static void Invoke(string id)
        {
            if (Module.CurrentModule.GetInitialStateOptionWithId(id) == null)
                throw new InvalidOperationException("Required registered developer action missing: " + id);
            Module.CurrentModule.ExecuteInitialStateOptionWithId(id);
        }

        private static void RequireMenu()
        {
            if (!(ScreenManager.TopScreen is MBInitialScreenBase))
                throw new InvalidOperationException("Generator diagnostic left the initial menu unexpectedly.");
        }
    }
}
