using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;
using Module = TaleWorlds.MountAndBlade.Module;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-43: the New Campaign setup screen. The main menu's "New Procedural Campaign" entry (the R01 extension point,
    // Module.AddInitialStateOption, next to Sandbox's own) pushes it; Back pops it to the main menu. A GauntletLayer shows the WorldSetup
    // movie over WorldSetupViewModel; the generic panel keys drive it (SetupInput) for keyboard and controller, and the prefab's navigation
    // scopes give controller focus.
    // MMR-46: Start Campaign closes the screen as the new game starts (HandOff) and keeps its model (Pending) while the activation runs; when
    // the player leaves character creation (the activation withdrawn) or the start fails, the main menu reopens the screen on the same model
    // and world, without generating it again.
    internal sealed class WorldSetupScreen : ScreenBase
    {
        public const string MenuId = "MNBLProceduralCampaign";
        public const int MenuOrder = 4;   // after Sandbox's "SandBoxNewGame" (3)
        public const string Movie = "WorldSetup";
        private GauntletLayer layer;
        private GauntletMovieIdentifier movie;
        private WorldSetupViewModel viewModel;
        private readonly IWorldGenerationRunner runner;
        private readonly WorldSetupModel resumed;
        internal static WorldSetupScreen Current { get; private set; }
        // The model of a world handed to the activation, while the screen is closed (MMR-46).
        internal static WorldSetupModel Pending { get; private set; }
        internal static int Reopened { get; private set; }

        private WorldSetupScreen(IWorldGenerationRunner runner) { this.runner = runner; }
        private WorldSetupScreen(WorldSetupModel model) { resumed = model; }

        internal WorldSetupViewModel ViewModel { get { return viewModel; } }
        internal GauntletLayer Layer { get { return layer; } }
        internal bool MovieLoaded { get { return movie != null; } }

        public static void Register()
        {
            Module.CurrentModule.AddInitialStateOption(new InitialStateOption(MenuId, new TextObject(WorldSetupText.Menu), MenuOrder, () => Open(null), DisabledReason, null, null));
        }

        public static void Open(IWorldGenerationRunner runner)
        {
            if (Current != null) return;
            if (Pending != null && Pending.State != SetupState.Starting) { Resume(); return; }
            ScreenManager.PushScreen(new WorldSetupScreen(runner ?? new CoordinatorRunner(GenerationRuntime.Coordinator)));
        }

        // Engine thread, from the activation's game starter just before the new game starts: the screen closes, its model stays pending.
        internal static void HandOff()
        {
            var screen = Current;
            if (screen == null || screen.viewModel == null) return;
            Pending = screen.viewModel.Model;
            Pending.HandOff();
            screen.Close();
        }

        // Engine thread, every application tick (SubModule): follows a pending start; once it was withdrawn or failed and the game has ended,
        // reopens the screen at the main menu on the same model. A committed campaign drops the model.
        internal static void TickPending()
        {
            var model = Pending;
            if (model == null) return;
            model.Tick();
            if (model.State == SetupState.Started) { Pending = null; WorldPreviewTextures.Clear(); return; }   // the campaign plays: the preview is done
            if (model.State == SetupState.Starting) return;
            if (TaleWorlds.Core.Game.Current != null || MBGameManager.Current != null || Current != null) return;
            var states = TaleWorlds.Core.GameStateManager.Current;
            if (states == null || !(states.ActiveState is TaleWorlds.MountAndBlade.InitialState) || !(ScreenManager.TopScreen is MBInitialScreenBase)) return;   // the main menu, not its loading
            Resume();
        }

        private static void Resume()
        {
            var model = Pending;
            Pending = null;
            model.Resume();
            Reopened++;
            ScreenManager.PushScreen(new WorldSetupScreen(model));
        }

        // The menu entry is disabled, with the reason, while a generation job is unfinished or being released, or while a harness probe
        // other than world_setup, new_campaign_flow or world_recovery (which drive this entry) is measuring.
        private static ValueTuple<bool, TextObject> DisabledReason()
        {
            // A world handed to the activation and back (withdrawn or failed) reopens on its model.
            if (Pending != null && Pending.State != SetupState.Starting && Current == null) return new ValueTuple<bool, TextObject>(false, null);
            // MMR-47: a host problem no longer disables the entry: the setup opens refusing to generate and shows each problem with its action
            // and reference (review finding: a disabled entry showed the message as a tooltip only, with no action, reference or diagnostics).
            string probe = MNBL.ProceduralWorld.Automation.AutorunHost.RunningProbe;
            var job = GenerationRuntime.Coordinator.Current;
            bool jobBusy = job != null && (!JobStateMachine.IsTerminal(job.Phase) || (job.Released != null && !job.Released.IsCompleted)) && Current == null;
            if ((probe != null && probe != "world_setup" && probe != "new_campaign_flow" && probe != "world_recovery") || jobBusy)
                return new ValueTuple<bool, TextObject>(true, new TextObject(WorldSetupText.Busy));
            return new ValueTuple<bool, TextObject>(false, null);
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            // The preview's texture provider is found by name; the factory may have scanned the assemblies before this module's loaded.
            try { TaleWorlds.GauntletUI.TextureProviderFactory.RefreshProviderTypes(); }
            catch (Exception error) { try { File.AppendAllText(Path.Combine(SubModule.Store, "module.log"), "world setup: texture providers: " + error.Message + Environment.NewLine); } catch (IOException) { } }
            var model = resumed ?? new WorldSetupModel(WorldSetupContract.Installed(), runner, WorldSetupHost.Problems(), null, new GenerationDiagnostics(SubModule.Store, null, SaveBindingRuntime.RootProblem));
            viewModel = new WorldSetupViewModel(model, Close);
            layer = new GauntletLayer("MNBLWorldSetup", 100, false) { IsFocusLayer = true };
            movie = layer.LoadMovie(Movie, viewModel);
            layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
            var panel = HotKeyManager.GetCategory("GenericPanelGameKeyCategory");
            if (panel != null) layer.Input.RegisterHotKeyCategory(panel);   // Confirm, Exit, Randomize and the tab keys
            AddLayer(layer);
            ScreenManager.TrySetFocus(layer);
            Current = this;
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            if (viewModel == null) return;
            viewModel.Model.Tick();
            viewModel.Refresh();
            HandleInput(layer.Input.IsHotKeyReleased, layer.IsFocusedOnInput());
        }

        // The generic panel keys through SetupInput; a probe feeds synthetic key states here. typing: the seed box has keyboard focus. With the
        // preview shown, Confirm starts the campaign and Randomize draws a new seed (SetupInput.ForState).
        internal SetupCommand HandleInput(Func<string, bool> released, bool typing)
        {
            if (viewModel == null) return SetupCommand.None;   // the screen has closed
            var command = SetupInput.ForState(SetupInput.Map(released, typing), viewModel.Model.State, viewModel.Model.PreviewShown);
            if (command == SetupCommand.Generate) viewModel.ExecuteGenerate();
            else if (command == SetupCommand.Back) viewModel.ExecuteBack();
            else if (command == SetupCommand.RandomWorld) viewModel.ExecuteRandomWorld();
            else if (command == SetupCommand.StartCampaign) viewModel.ExecuteStartCampaign();
            else if (command == SetupCommand.NewSeed) viewModel.ExecuteNewSeed();
            else if (command == SetupCommand.PreviousPlace) viewModel.Preview.ExecutePreviousPlace();
            else if (command == SetupCommand.NextPlace) viewModel.Preview.ExecuteNextPlace();
            return command;
        }

        private void Close()
        {
            if (ScreenManager.TopScreen == this) ScreenManager.PopScreen();
        }

        protected override void OnFinalize()
        {
            if (viewModel != null)
            {
                // A running generation is cancelled (its job removes its own processes); the job's files are released. A world handed to the
                // activation stays with it (Pending).
                // A screen that closes while its world is being started (before the hand-off) still hands the model over (review finding).
                if (viewModel.Model.State == SetupState.Starting && !viewModel.Model.HandedOff) { Pending = viewModel.Model; Pending.HandOff(); }
                if (!viewModel.Model.HandedOff) viewModel.Model.Close();
                viewModel.OnFinalize();
            }
            if (layer != null && movie != null) layer.ReleaseMovie(movie);
            layer = null; movie = null; viewModel = null;
            if (Current == this) Current = null;
            base.OnFinalize();
        }
    }

    // The host the setup screen runs on: War Sails installed and enabled, the exact game and War Sails builds the mod was made for
    // (GeneratedPatches), and the pinned generator binaries beside the module DLL. Evaluated once per process (the binaries are hashed once).
    internal static class WorldSetupHost
    {
        private static List<GenerationError> problems;
        public static List<GenerationError> Problems()
        {
            if (problems != null) return problems;
            string game = Utilities.GetApplicationVersionWithBuildNumber().ToString();
            var naval = ModuleHelper.GetModuleInfo("NavalDLC");
            string navalVersion = naval == null ? null : "v" + naval.Version.Major + "." + naval.Version.Minor + "." + naval.Version.Revision;
            bool enabled = naval != null && Utilities.GetModulesNames().Contains("NavalDLC");
            var tools = new List<KeyValuePair<string, string>>();
            string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var tool in new[] { "fwg", "worldbridge" })
            {
                string exe = Path.Combine(bin, tool + ".exe"), pin = Path.Combine(bin, tool + ".sha256");
                try
                {
                    if (!File.Exists(exe) || !File.Exists(pin)) { tools.Add(new KeyValuePair<string, string>(tool, "missing " + (File.Exists(exe) ? pin : exe))); continue; }
                    string actual = GenerationProbe.Hash(exe), expected = File.ReadAllText(pin).Trim();
                    if (actual != expected) tools.Add(new KeyValuePair<string, string>(tool, "hash " + actual + " differs from pin " + expected));
                }
                // Called from the main menu's entry callback: an unreadable file is a host problem, never an exception there.
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { tools.Add(new KeyValuePair<string, string>(tool, "unreadable: " + error.Message)); }
            }
            problems = GenerationErrors.Host(game, GeneratedPatchesVersions.Game, navalVersion, GeneratedPatchesVersions.Naval, enabled, tools);
            return problems;
        }
        // Probes evaluate a host they describe (offline the same function is tested directly).
        internal static void Override(List<GenerationError> value) { problems = value; }

        // MMR-44: the host a world bundle must match (WorldDataBundle.HostProfile): this game's version with its build number; War Sails's
        // installed version, completed with the runtime lock's build number when its release matches (SubModule.xml carries no build); the
        // runtime capabilities R01-R10 the decision record proves.
        public static WorldDataBundle.HostProfile BundleHost()
        {
            string game = Utilities.GetApplicationVersionWithBuildNumber().ToString();
            var naval = ModuleHelper.GetModuleInfo("NavalDLC");
            string release = naval == null ? null : "v" + naval.Version.Major + "." + naval.Version.Minor + "." + naval.Version.Revision;
            string locked = null;
            using (var stream = typeof(WorldSetupHost).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld.runtime-api-lock.json"))
            using (var reader = new StreamReader(stream))
                locked = ((IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()))["navalVersion"] as string;
            string navalVersion = release != null && locked != null && locked.StartsWith(release + ".", StringComparison.Ordinal) ? locked : release;
            return new WorldDataBundle.HostProfile(game, navalVersion, new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" });
        }
    }

    internal static class GeneratedPatchesVersions
    {
        public const string Game = MNBL.ProceduralWorld.CampaignRegistry.GeneratedPatches.GameVersion;
        public const string Naval = MNBL.ProceduralWorld.CampaignRegistry.GeneratedPatches.NavalVersion;
    }

    // The setup's generation job: MMR-44's GenerationCoordinator (GenerationRuntime.Coordinator). Run starts a job for the choice, forwards
    // its progress events, cancels it with the setup's token, and returns once the world is ready for review (the bundle's source split).
    // Release hands the job back: a job awaiting review is cancelled (its bundle and staging released), then its directory is deleted.
    internal sealed class CoordinatorRunner : IWorldGenerationRunner
    {
        private readonly GenerationCoordinator coordinator;
        private readonly ConcurrentDictionary<string, GenerationJob> jobs = new ConcurrentDictionary<string, GenerationJob>(StringComparer.OrdinalIgnoreCase);
        public CoordinatorRunner(GenerationCoordinator coordinator) { this.coordinator = coordinator; }
        public GenerationJob LastJob { get; private set; }
        public GenerationCoordinator Coordinator { get { return coordinator; } }

        public string Run(WorldChoice choice, Action<ProgressEvent> onProgress, Action<string, string> onJobCreated, CancellationToken token)
        {
            // One job at a time: an earlier world still being released (a quick reopen) is waited for, not refused.
            coordinator.AwaitIdle(TimeSpan.FromSeconds(120));
            GenerationJob job;
            try { job = coordinator.Start(choice, onProgress, j => { jobs[j.Root] = j; LastJob = j; onJobCreated(j.Id, j.Root); }); }
            catch (GenerationBusyException busy) { throw new GenerationFailure(GenerationErrors.Busy(busy.Message), busy); }
            using (token.Register(() => coordinator.Cancel(job)))
            {
                while (true)
                {
                    var completion = job.Completion;
                    completion.Wait();
                    var phase = job.Phase;
                    if (completion == job.Completion && (phase == JobPhase.ReadyForReview || JobStateMachine.IsTerminal(phase))) break;
                }
            }
            if (job.Phase == JobPhase.ReadyForReview) return Path.Combine(job.Get<string>("bundleDirectory"), "source");
            if (job.Phase == JobPhase.Cancelled) throw new OperationCanceledException(token);
            throw new GenerationFailure(job.Error ?? GenerationErrors.Job(GenerationErrorCategory.BadSource, "job.failed", job.FailureDetail));
        }

        public GenerationError Classify(Exception error, string jobRoot)
        {
            var failure = error as GenerationFailure;
            if (failure != null) return failure.Error;
            if (error is OperationCanceledException) return GenerationErrors.Job(GenerationErrorCategory.InterruptedGeneration, "generation.cancelled", jobRoot);
            return GenerationRuntime.Classify(error, null);
        }

        public void Release(string jobRoot)
        {
            GenerationJob job;
            if (jobRoot == null || !jobs.TryRemove(jobRoot, out job)) return;
            if (!coordinator.Release(job).Wait(TimeSpan.FromSeconds(120)))
                try { File.AppendAllText(Path.Combine(SubModule.Store, "module.log"), "world setup: job " + job.Id + " was not released within 120 s" + Environment.NewLine); } catch (IOException) { }
        }

        // MMR-47: the jobs a stopped process left (this process's current job is live), and discarding one under the verified root.
        public IList<WorldRecoveryService.UnfinishedJob> Unfinished()
        {
            // Another game process holding the store lock may be running these jobs: nothing is offered then.
            if (!SaveBindingRuntime.EnsureLock()) return new List<WorldRecoveryService.UnfinishedJob>();
            var current = coordinator.Current;
            return WorldRecoveryService.Jobs(Path.Combine(SubModule.Store, "jobs"), path => current != null && String.Equals(Path.GetFullPath(current.Root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
                WorldSetupContract.Installed().ProducerLockSha256, instance => { var j = new ActivationJournalStore(SubModule.Store).Journal(instance); return j != null && j.State == ActivationJournalStore.Active; },
                JobInputs.PolicyMismatch);
        }

        public string Discard(WorldRecoveryService.UnfinishedJob job)
        {
            if (!SaveBindingRuntime.EnsureLock()) return "another game process is using the world store";
            var current = coordinator.Current;
            return WorldRecoveryService.Discard(SubModule.Store, job.Root, path => current != null && String.Equals(Path.GetFullPath(current.Root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase),
                SaveBindingRuntime.RootProblem());
        }

        // MMR-46: the preview of the world the job holds for review, from the inputs the activation compiles (the bundle's campaign records, the
        // job's surface and settlement layout, FactionFactory over the module's policies).
        public WorldPreview Preview(string jobRoot)
        {
            GenerationJob job;
            if (jobRoot == null || !jobs.TryGetValue(jobRoot, out job)) throw new InvalidOperationException("The setup holds no job at " + jobRoot + ".");
            if (job.Phase != JobPhase.ReadyForReview) throw new InvalidOperationException("The job is " + job.Phase + ", not ready for review.");
            var prepared = job.Get<PreparedSurface>("prepared");
            if (prepared == null || prepared.Surface == null) throw new InvalidOperationException("The job holds no prepared surface.");
            string scene = job.Get<string>("sceneName");
            var bundle = WorldDataBundle.Open(job.Get<string>("bundleDirectory"), WorldSetupHost.BundleHost());
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            Func<byte[], IDictionary<string, object>> parse = bytes => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'));
            Func<string, IDictionary<string, object>> policy = path => parse(GameActivationHost.PolicyBytes(path));
            var layout = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(RuntimeWorldBackend.SettlementReportPath(scene)));
            if ((string)layout["worldContentId"] != bundle.WorldContentId || (string)layout["layoutSha256"] != prepared.Settlements.Sha256)
                throw new InvalidDataException("The settlement layout report is not this world's layout.");
            var campaign = parse(bundle.ReadVerified("compiled/campaign/campaign.json"));
            var idMap = parse(bundle.ReadVerified("compiled/campaign/id-map.json"));
            var templates = ((IEnumerable)policy(CampaignGraphValidator.PolicyPath)["policies"]).Cast<object>().Select(o => (string)o)
                .Where(path => path.StartsWith("worldgen/templates/cultures/", StringComparison.Ordinal) && !path.EndsWith("/contract.json", StringComparison.Ordinal))
                .ToDictionary(path => path.Substring(path.LastIndexOf('/') + 1, path.Length - path.LastIndexOf('/') - 1 - ".json".Length), path => policy(path), StringComparer.Ordinal);
            var factions = FactionFactory.Build(campaign, idMap, policy("worldgen/policies/factions.json"), policy("worldgen/policies/culture-presentations.json"), templates);
            // The villages bound as the campaign binds them (SettlementFactory runs the same VillageBinding).
            var bindingProblems = new List<string>();
            var binding = VillageBinding.FromRecords(policy("worldgen/policies/economy.json"), campaign, parse(bundle.ReadVerified("compiled/travel/travel-graph.json")), bindingProblems);
            if (bindingProblems.Count > 0) throw new InvalidDataException("The village binding failed: " + String.Join("; ", bindingProblems.Take(3)));
            return WorldPreview.Build(prepared.Surface, layout, campaign, factions, scene, binding.BoundOf);
        }

        // MMR-46: the reviewed job's commit (MMR-45's activation) with the player's game starter: the setup screen closes and the installed
        // new-game flow (campaign intro, character creation) runs in the generated world. A committed job is released (its package, bundle and
        // scene stay with the world); a withdrawn one is ready for review again.
        public ActivationCommitStage LastStage { get; private set; }
        public Task<StartOutcome> StartCampaign(string jobRoot)
        {
            GenerationJob job;
            if (jobRoot == null || !jobs.TryGetValue(jobRoot, out job))
                return Task.FromResult(new StartOutcome { Kind = "failed", Error = GenerationErrors.Activation("no job at " + jobRoot) });
            var stage = new ActivationCommitStage {
                Start = a => { WorldSetupScreen.HandOff(); return GeneratedCampaignManager.Start(a); },
                Progress = GeneratedCampaignManager.Progress };
            LastStage = stage;
            Task committing;
            try { committing = coordinator.Commit(job, stage); }
            catch (InvalidOperationException refused) { return Task.FromResult(new StartOutcome { Kind = "failed", Error = GenerationErrors.Activation(refused.Message) }); }
            return committing.ContinueWith(_ =>
            {
                var activation = stage.Error as ActivationException;
                var outcome = StartOutcome.FromCommit(job.Phase, job.Error, job.FailureDetail, activation != null && activation.Recoverable);
                if (outcome.Kind == "playing") Release(jobRoot);
                return outcome;
            }, TaskScheduler.Default);
        }
    }
}
