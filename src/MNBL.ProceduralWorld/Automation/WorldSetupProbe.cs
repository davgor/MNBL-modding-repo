using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-43: the New Campaign setup screen in the running game (world_setup; seed=<n>, cancelSeed=<n>), from the main menu:
    //   1. the "New Procedural Campaign" entry beside Sandbox's, enabled on this host; executing it pushes the setup screen and its Gauntlet
    //      movie loads;
    //   2. the seed field as the edit box writes it: rejected texts with their reasons, an accepted seed; Random World (button and
    //      Randomize key, ignored while typing); the fixed settings with their reasons; the measured waiting time;
    //   3. synthetic generic-panel key states through the screen's own mapping (physical devices are not driven): Exit leaves to the main
    //      menu; reopened, Confirm generates the seed through the generation job (MMR-44), within the stated waiting time and without stalling the
    //      engine thread, to the generated world's peoples and the cultures character creation offers; diagnostics stay hidden until
    //      asked for;
    //   4. a generation cancelled with Exit: the interrupted-generation category, no generator process left; the superseded and the
    //      cancelled job's files are released;
    //   5. a host without War Sails (described to WorldSetupHost): the entry and Generate are refused with the missing-DLC category.
    // Any failed expectation fails the run.
    internal static class WorldSetupProbe
    {
        // The autorun host steps one enumerator; nested steps (open the screen, wait frames) are flattened here.
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

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "New Campaign setup screen (MMR-43) from the main menu: entry, Gauntlet movie, seed validation, Random World, fixed settings, waiting time, synthetic generic-panel keys, generation through MMR-44's generation job with the engine thread's frame gaps, the world's peoples and creation cultures, cancellation, job release, a host without War Sails. Physical keyboard and controller devices are not driven; the campaign start is MMR-45/46.";
            uint seed = (uint)context.Int("seed", 42), cancelSeed = (uint)context.Int("cancelSeed", 43);
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); };
            var setupJobs = new List<string>();
            string cancelBack = null;

            // 1. The entry.
            var options = Module.CurrentModule.GetInitialStateOptions().ToList();
            var entry = options.FirstOrDefault(o => o.Id == WorldSetupScreen.MenuId);
            var sandbox = options.FirstOrDefault(o => o.Id == "SandBoxNewGame");
            var disabled = entry == null ? new ValueTuple<bool, TaleWorlds.Localization.TextObject>(true, null) : entry.IsDisabledAndReason();
            context.Outputs["menu"] = new Dictionary<string, object> { ["entries"] = options.OrderBy(o => o.OrderIndex).Select(o => o.OrderIndex + " " + o.Id + " " + o.Name).ToList(),
                ["entry"] = entry == null ? null : entry.Name.ToString(), ["order"] = entry == null ? -1 : entry.OrderIndex, ["sandboxOrder"] = sandbox == null ? -1 : sandbox.OrderIndex, ["disabled"] = disabled.Item1,
                ["host"] = WorldSetupHost.Problems().Select(p => p.ToString()).ToList() };
            expect(entry != null && sandbox != null && entry.OrderIndex == sandbox.OrderIndex + 1 && !disabled.Item1, "the entry is beside Sandbox's and enabled on this host");
            context.Checkpoint();

            WorldSetupScreen screen = null;
            Func<IEnumerator> open = () => OpenScreen(context, s => screen = s);
            yield return open();
            var vm = screen.ViewModel;
            expect(ScreenManager.TopScreen == screen && screen.MovieLoaded && screen.Layer != null, "executing the entry pushes the setup screen and loads its movie");
            bool gameAtOpen = TaleWorlds.Core.Game.Current != null;
            context.Outputs["opened"] = new Dictionary<string, object> { ["gameExists"] = gameAtOpen, ["topScreen"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName, ["movieLoaded"] = screen.MovieLoaded,
                ["title"] = vm.TitleText, ["coverage"] = vm.CoverageText, ["profile"] = vm.ProfileText, ["seed"] = vm.SeedText, ["seedHint"] = vm.SeedHintText, ["canGenerate"] = vm.CanGenerate };
            yield return Frames(30);
            context.Outputs["setupScreenshot"] = Shot(context, "setup");

            // 2. The seed field, Random World, settings, waiting time.
            var seeds = new List<object>(); context.Outputs["seeds"] = seeds;
            foreach (var text in new[] { "", " 42", "042", "-1", "4294967296", "4,2", "abc", "4294967295", "0", seed.ToString() })
            {
                vm.SeedText = text; yield return null;
                seeds.Add(new Dictionary<string, object> { ["text"] = text, ["problem"] = vm.Model.SeedProblem.ToString(), ["error"] = vm.SeedErrorText, ["canGenerate"] = vm.CanGenerate });
            }
            var accepted = new HashSet<string> { "4294967295", "0", seed.ToString() };
            expect(seeds.Cast<Dictionary<string, object>>().All(s => accepted.Contains((string)s["text"]) == (bool)s["canGenerate"] && accepted.Contains((string)s["text"]) == (((string)s["error"]).Length == 0) && !((string)s["error"]).StartsWith("{=")),
                "rejected seeds show their (localised) reason and block Generate; accepted seeds generate");
            vm.ExecuteRandomWorld(); string random1 = vm.SeedText;
            var typingCommand = screen.HandleInput(k => k == SetupInput.Randomize, true); string whileTyping = vm.SeedText;
            var keyCommand = screen.HandleInput(k => k == SetupInput.Randomize, false); string random2 = vm.SeedText;
            context.Outputs["randomWorld"] = new Dictionary<string, object> { ["button"] = random1, ["typingKey"] = typingCommand.ToString(), ["key"] = keyCommand.ToString(), ["afterKey"] = random2,
                ["source"] = vm.Model.SeedSource, ["canGenerate"] = vm.CanGenerate, ["keyGlyph"] = vm.RandomInputKey == null ? null : vm.RandomInputKey.KeyID };
            expect(random1 != random2 && whileTyping == random1 && typingCommand == SetupCommand.None && keyCommand == SetupCommand.RandomWorld && vm.Model.SeedSource == "random" && vm.CanGenerate,
                "Random World (button, and the Randomize key unless the seed box is being typed in) draws a new valid seed each time");
            context.Outputs["settings"] = vm.Settings.Select(s => new Dictionary<string, object> { ["name"] = s.NameText, ["value"] = s.ValueText, ["locked"] = s.IsLocked, ["reason"] = s.ReasonText }).ToList();
            context.Outputs["waitText"] = vm.WaitText;
            var stated = WaitTime.Variables(vm.Model.Contract);
            expect(vm.Settings.Count == 2 && vm.Settings.All(s => s.IsLocked && s.ReasonText.Length > 0) && vm.HasWaitText && vm.WaitText.Contains(" " + stated["GENERATE"] + " "),
                "the fixed settings show why they are fixed, and the waiting time is the budget's measurement");
            context.Checkpoint();

            // 3. Keys: Exit leaves; reopened, Confirm generates.
            var keys = new Dictionary<string, object>(); context.Outputs["keys"] = keys;
            keys["exitIdle"] = screen.HandleInput(k => k == SetupInput.Exit, false).ToString();
            yield return Frames(10);
            keys["afterExit"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName;
            expect(ScreenManager.TopScreen is MBInitialScreenBase && WorldSetupScreen.Current == null, "Exit leaves the setup screen to the main menu");
            yield return open(); vm = screen.ViewModel;
            vm.SeedText = seed.ToString(); yield return null;
            keys["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            var stages = new List<string>(); keys["stages"] = stages;
            double start = context.Seconds, last = start, maxGap = 0;
            while (vm.IsGenerating && context.Seconds - start < 180)
            {
                if (stages.Count == 0 || stages[stages.Count - 1] != vm.StatusText) stages.Add(vm.StatusText);
                yield return null;
                maxGap = Math.Max(maxGap, context.Seconds - last); last = context.Seconds;
            }
            double generationSeconds = context.Seconds - start;
            keys["generationSeconds"] = generationSeconds; keys["statedGenerateSeconds"] = stated["GENERATE"]; keys["maxFrameGapSeconds"] = maxGap;
            expect(generationSeconds <= Double.Parse(stated["GENERATE"], System.Globalization.CultureInfo.InvariantCulture), "the generation finished within the waiting time the screen states");
            // The job's engine steps are single native calls (install, construct, navigation validation: up to about 0.7 s in MMR-44's runs).
            expect(maxGap < 1.0, "the engine thread kept ticking while generating (largest frame gap under 1 s)");
            var result = new Dictionary<string, object> { ["state"] = vm.Model.State.ToString(), ["resultText"] = vm.ResultText, ["peoples"] = vm.PeoplesText, ["creation"] = vm.CreationText,
                ["canStartCampaign"] = vm.CanStartCampaign, ["previewShown"] = vm.IsPreviewShown, ["requestFields"] = vm.Model.Choice == null ? null : vm.Model.Choice.RequestFields(), ["diagnosticsShown"] = vm.ShowDiagnostics, ["jobRoot"] = vm.Model.JobRoot };
            context.Outputs["generated"] = result;
            if (vm.Model.JobRoot != null) setupJobs.Add(vm.Model.JobRoot);
            if (vm.Model.State == SetupState.Generated)
            {
                // An independent whole-document reading of the same world.json, off the engine thread.
                var contract = vm.Model.Contract; string worldJson = Path.Combine(vm.Model.ResultSplit, "world.json");
                var reference = Task.Run(() => SourceWorldSummary.FromWorldJson(File.ReadAllText(worldJson), contract));
                while (!reference.IsCompleted) yield return null;
                if (reference.Status != TaskStatus.RanToCompletion) failures.Add("the reference reading of world.json failed: " + reference.Exception.GetBaseException().Message);
                else
                {
                    var again = reference.Result;
                    result["cities"] = again.Cities; result["peopleNames"] = again.Peoples.Select(p => p.Name + " " + p.Cities + " " + p.Culture).ToList(); result["cultures"] = again.Cultures;
                    expect(again.Cities == vm.Model.Result.Cities && again.Peoples.All(p => vm.PeoplesText.Contains(p.Name + " (" + WorldSetupViewModel.Text(WorldSetupText.CultureNames[p.Culture]) + ")"))
                           && again.Cultures.All(c => vm.CreationText.Contains(WorldSetupViewModel.Text(WorldSetupText.CultureNames[c]))) && vm.ResultText.Contains(seed.ToString())
                           && !(vm.ResultText + vm.PeoplesText + vm.CreationText).Contains(vm.Model.JobRoot ?? "\u0000"),
                        "the generated world's own peoples are shown with the culture each is shown as, and the cultures character creation offers; no path in the ordinary flow");
                }
            }
            else failures.Add("the generation did not complete: " + vm.Model.State + " " + (vm.Model.Error == null ? "" : vm.Model.Error.ToString()));
            // The lifecycle stage: the setup and its generation run on the main menu, before any game exists, so nothing is registered; the
            // campaign start (MMR-45/46) registers the chosen world's cultures ahead of character creation (MMR-32's ordering).
            result["gameExists"] = TaleWorlds.Core.Game.Current != null;
            expect(!gameAtOpen && TaleWorlds.Core.Game.Current == null, "setup and generation run at the main-menu stage: no game exists and nothing is registered");
            var fields = vm.Model.Choice == null ? null : vm.Model.Choice.RequestFields();
            expect(stages.Count >= 2 && fields != null && (uint)fields["masterSeed"] == seed && (string)fields["seedSource"] == "explicit" && (string)fields["producerLockSha256"] == vm.Model.Contract.Sources["producer.lock.json"],
                "Confirm generated the typed seed with progress stages; the request fields name the producer lock");
            yield return Frames(20);
            context.Outputs["generatedScreenshot"] = Shot(context, "generated");
            expect(!vm.ShowDiagnostics && vm.DiagnosticsText.Length == 0, "diagnostics are hidden in the ordinary flow");
            vm.ExecuteToggleDiagnostics(); yield return Frames(20);
            result["diagnosticsWhenShown"] = vm.DiagnosticsText;
            context.Outputs["diagnosticsScreenshot"] = Shot(context, "diagnostics");
            expect(vm.ShowDiagnostics && vm.DiagnosticsText.Contains("job ") && vm.DiagnosticsText.Contains("producer lock"), "the diagnostics view exposes the job and hashes on request");
            vm.ExecuteToggleDiagnostics();
            context.Checkpoint();

            // 4. Cancel with Exit while generating; the superseded job is released by the new Generate, the cancelled one when the screen closes.
            string firstJob = vm.Model.JobRoot;
            // MMR-46: the generated world shows its preview, where Confirm starts the campaign; Exit returns to the seed (the world kept).
            if (vm.Model.State == SetupState.Generated)
            {
                cancelBack = screen.HandleInput(k => k == SetupInput.Exit, false).ToString(); yield return null;
                expect(vm.Model.State == SetupState.Editing && vm.Model.CanEditSeed, "Exit in the preview returns to the seed");
            }
            vm.SeedText = cancelSeed.ToString(); yield return null;
            screen.HandleInput(k => k == SetupInput.Confirm, false);
            start = context.Seconds;
            while (vm.IsGenerating && vm.Model.JobRoot == null && context.Seconds - start < 30) yield return null;
            string cancelledJob = vm.Model.JobRoot; if (cancelledJob != null) setupJobs.Add(cancelledJob);
            double waitStage = context.Seconds; while (vm.IsGenerating && (context.Seconds - waitStage < 1.5 || !vm.Model.Releasing.IsCompleted) && context.Seconds - waitStage < 15) yield return null;
            var cancel = new Dictionary<string, object> { ["backFromPreview"] = cancelBack, ["firstJobReleased"] = firstJob != null && !Directory.Exists(firstJob), ["exitWhileGenerating"] = screen.HandleInput(k => k == SetupInput.Exit, false).ToString(),
                ["screenStayed"] = WorldSetupScreen.Current == screen, ["cancelText"] = vm.StatusText };
            context.Outputs["cancel"] = cancel;
            start = context.Seconds;
            while (vm.IsGenerating && context.Seconds - start < 60) yield return null;
            yield return Frames(30);
            cancel["state"] = vm.Model.State.ToString(); cancel["category"] = vm.Model.Error == null ? null : vm.Model.Error.Category.ToString();
            cancel["errorTitle"] = vm.ErrorTitle; cancel["errorAction"] = vm.ErrorAction;
            cancel["generatorProcesses"] = Process.GetProcessesByName("fwg").Length + Process.GetProcessesByName("worldbridge").Length;
            expect((bool)cancel["firstJobReleased"], "a new Generate releases the previous job's files");
            expect(cancel["screenStayed"] is bool && (bool)cancel["screenStayed"] && vm.Model.Error != null && vm.Model.Error.Category == GenerationErrorCategory.InterruptedGeneration && (int)cancel["generatorProcesses"] == 0,
                "Exit while generating cancels: the interrupted-generation category, no generator process left");
            context.Outputs["cancelScreenshot"] = Shot(context, "cancelled");
            var closing = vm.Model;
            screen.HandleInput(k => k == SetupInput.Exit, false); yield return Frames(10);
            double released = context.Seconds; while (!closing.Releasing.IsCompleted && context.Seconds - released < 15) yield return null;
            cancel["cancelledJobReleasedOnClose"] = cancelledJob != null && !Directory.Exists(cancelledJob);
            expect((bool)cancel["cancelledJobReleasedOnClose"], "closing the screen releases the last job's files");
            context.Checkpoint();

            // 5. A host without War Sails.
            var real = WorldSetupHost.Problems();
            WorldSetupHost.Override(GenerationErrors.Host(Utilities.GetApplicationVersionWithBuildNumber().ToString(), GeneratedPatchesVersions.Game, null, GeneratedPatchesVersions.Naval, false, null));
            var missing = new Dictionary<string, object>(); context.Outputs["missingWarSails"] = missing;
            var refused = entry.IsDisabledAndReason();
            missing["entryDisabled"] = refused.Item1; missing["entryReason"] = refused.Item2 == null ? null : refused.Item2.ToString();
            yield return open(); vm = screen.ViewModel;
            vm.SeedText = seed.ToString(); yield return null;
            missing["hostProblem"] = vm.HostProblemTitle + " | " + vm.HostProblemText + " | " + vm.HostProblemAction; missing["canGenerate"] = vm.CanGenerate;
            missing["reference"] = vm.HostProblemViews.Count > 0 ? vm.HostProblemViews[0].ReferenceText : null;
            missing["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString(); missing["generating"] = vm.IsGenerating;
            // MMR-47: the entry opens; the setup refuses to generate and shows the problem with its action and reference (retained diagnostics).
            expect(!refused.Item1 && vm.HasHostProblems && !vm.CanGenerate && !vm.IsGenerating && vm.Model.HostProblems[0].Category == GenerationErrorCategory.MissingToolOrDlc
                   && vm.HostProblemViews.Count > 0 && vm.HostProblemViews[0].HasReference && vm.HostProblemViews[0].ActionText.Length > 0
                   && vm.Model.Diagnostics != null && vm.Model.Diagnostics.Read(vm.Model.HostProblems[0].CorrelationId) != null,
                "without War Sails the setup opens refusing to generate, showing the missing-DLC problem with its action and a reference whose diagnostics are retained");
            yield return Frames(20);
            context.Outputs["missingWarSailsScreenshot"] = Shot(context, "missing-war-sails");
            screen.HandleInput(k => k == SetupInput.Exit, false); yield return Frames(10);
            WorldSetupHost.Override(real);
            context.Outputs["finalTopScreen"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName;
            context.Outputs["setupJobsLeft"] = setupJobs.Count(Directory.Exists);
            expect(ScreenManager.TopScreen is MBInitialScreenBase, "the main menu is usable at the end");
            expect(setupJobs.Count == 2 && setupJobs.All(j => !Directory.Exists(j)), "no job directory of the setup is left");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("World setup expectations failed: " + String.Join("; ", failures));
        }

        private static IEnumerator OpenScreen(ProbeContext context, Action<WorldSetupScreen> found)
        {
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            found(WorldSetupScreen.Current);
            yield return Frames(5);
        }

        private static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }

        private static string Shot(ProbeContext context, string label)
        {
            string path = Path.Combine(context.Directory, "world-setup-" + context.ProcessId + "-" + label + ".bmp");
            Utilities.TakeScreenshot(path);
            return path;
        }
    }
}
