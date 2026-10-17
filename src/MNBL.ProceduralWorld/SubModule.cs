using System;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using MNBL.ProceduralWorld.Automation;
using MNBL.ProceduralWorld.Diagnostics;

namespace MNBL.ProceduralWorld
{
    public sealed class SubModule : MBSubModuleBase
    {
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private CancellationTokenSource cancellation;
        private Task generation;
        private volatile string generationJobRoot;
        private volatile string generationOutcome;
        private volatile string generationFailure;
        private volatile string generationStage;
        internal static SubModule Instance { get; private set; }
        internal Task GenerationTask { get { return generation; } }
        internal string GenerationJobRoot { get { return generationJobRoot; } }
        internal string GenerationOutcome { get { return generationOutcome; } }
        internal string GenerationFailure { get { return generationFailure; } }
        internal string GenerationStage { get { return generationStage; } }
        private bool registered;
        internal static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNBL.ProceduralWorld");

        protected override void OnSubModuleLoad()
        {
            Instance = this;
            Directory.CreateDirectory(Store);
            LogLifecycle("loaded");
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            // Harness-requested probe (tools/Invoke-InGameProbe.ps1); the request is claimed before anything runs.
            try { AutorunHost.TryClaim(Store); }
            catch (Exception error) { File.AppendAllText(Path.Combine(Store, "module.log"), "autorun: " + error + "\n"); }
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            if (registered) return;
            registered = true;
            // MMR-62: recover interrupted initial-save publications and remove abandoned staging before the player can open the save list.
            MNBL.ProceduralWorld.Persistence.SaveBindingRuntime.RunStartup();
            // MMR-49: what a generated campaign holds in this process, counted against the main menu's baseline.
            MNBL.ProceduralWorld.CampaignRegistry.WorldLifetimeTrackers.Register();
            // MMR-49: a generated campaign's save loads through the world loader, never the installed load.
            try { MNBL.ProceduralWorld.Persistence.LoadRouting.Apply(); }
            catch (Exception error) { File.AppendAllText(Path.Combine(Store, "module.log"), "load routing: " + error + "\n"); }
            if (MNBL.ProceduralWorld.Persistence.LoadRouting.GuardError != null) File.AppendAllText(Path.Combine(Store, "module.log"), "load guard: " + MNBL.ProceduralWorld.Persistence.LoadRouting.GuardError + "\n");
            // MMR-43: the player's entry, beside Sandbox's New Campaign.
            MNBL.ProceduralWorld.UI.WorldSetupScreen.Register();
            // The developer diagnostics stay out of the player's main menu: they are registered for a harness probe (GenerationMenuProbe
            // drives them) or when the store holds a "developer" marker file.
            if (!AutorunHost.Busy && !File.Exists(Path.Combine(Store, "developer"))) return;
            Add("fwgc_probe_42", "FWGC: Generate probe world (seed 42)", 42, () => Start(42));
            Add("fwgc_probe_43", "FWGC: Generate probe world (seed 43)", 43, () => Start(43));
            Add("fwgc_cancel", "FWGC: Cancel generation", 44, () => cancellation?.Cancel());
            Add("fwgc_native_preview", "FWGC: Preview generated terrain", 45, () => InteractiveDiagnostics.Start("native_render"));
            Add("fwgc_sandbox_boot", "FWGC: Sandbox terrain test (paused)", 46, () => InteractiveDiagnostics.Start("sandbox_boot"));
            Add("fwgc_reject_probe", "FWGC: Test generator rejection", 47, () => Start(42, "invalid_settings"));
            Add("fwgc_abort_probe", "FWGC: Test generator abort", 48, () => Start(42, "producer_terminated"));
        }

        private void OpenHeightfield(int seed)
        {
            try { TaleWorlds.ScreenSystem.ScreenManager.PushScreen(new HeightfieldProbeScreen(seed)); }
            catch (Exception error) { File.AppendAllText(Path.Combine(Store,"module.log"),error+"\n"); messages.Enqueue("Terrain diagnostic failed: "+error.Message); }
        }

        private static void Add(string id, string text, int order, Action action)
        {
            // Disabled while a harness probe runs so a stray click cannot start a second diagnostic mid-measurement.
            Module.CurrentModule.AddInitialStateOption(new InitialStateOption(id, new TextObject(text), order, action,
                () => AutorunHost.Busy || InteractiveDiagnostics.Busy ||
                    (id != "fwgc_cancel" && Instance?.GenerationTask != null && !Instance.GenerationTask.IsCompleted)
                    ? new ValueTuple<bool, TextObject>(true, new TextObject("Another diagnostic is running. Cancel generation or wait for it to finish."))
                    : new ValueTuple<bool, TextObject>(false, null), null, null));
        }

        private void Start(uint seed)
        {
            Start(seed, "normal");
        }

        private void Start(uint seed, string scenario)
        {
            if (generation != null && !generation.IsCompleted) { messages.Enqueue("A world is already generating."); return; }
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            generationJobRoot = null;
            generationOutcome = "running";
            generationFailure = null;
            generationStage = "starting";
            generation = Task.Run(() =>
            {
                try
                {
                    messages.Enqueue(GenerationProbe.RunDiagnostic(seed, Store, token, scenario,
                        stage => { generationStage = stage; messages.Enqueue("World generation: " + stage); },
                        jobRoot => generationJobRoot = jobRoot));
                    var resultPath = Path.Combine(generationJobRoot, "result.json");
                    var result = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(resultPath));
                    generationOutcome = Convert.ToString(result["status"]);
                }
                catch (OperationCanceledException) { generationOutcome = "cancelled"; messages.Enqueue("World generation cancelled."); }
                catch (Exception error)
                {
                    generationFailure = error.ToString();
                    generationOutcome = "failed";
                    File.AppendAllText(Path.Combine(Store, "module.log"), error + "\n");
                    messages.Enqueue("Generation failed: " + error.Message);
                }
            });
            messages.Enqueue("Generating seed " + seed + ". The menu remains available; Cancel stops this job.");
        }

        protected override void OnApplicationTick(float dt)
        {
            string message;
            while (messages.TryDequeue(out message)) InformationManager.DisplayMessage(new InformationMessage(message));
            var autorun = AutorunHost.Current;
            if (autorun != null) autorun.Tick(dt);
            // MMR-37: native calls run on the application tick thread (campaign map loading runs on it too; module load runs on another,
            // run 27772); it is bound on the first tick, before any world loads. Workers' continuations for it are run here.
            MNBL.ProceduralWorld.Surface.EngineThread.Bind();
            MNBL.ProceduralWorld.Surface.EngineWork.Pump();
            // MMR-44: the generation job's engine-thread items, within their per-tick budget.
            MNBL.ProceduralWorld.Generation.GenerationRuntime.Scheduler.Pump();
            InteractiveDiagnostics.Tick();
            // MMR-46: a world handed from the setup screen to the activation; the screen reopens if the player withdraws.
            MNBL.ProceduralWorld.UI.WorldSetupScreen.TickPending();
        }

        // MMR-39: War Sails' view has built its naval map-scene wrapper by now (its module loads first); a generated world wraps it with its
        // pirate spawn points before PiratesCampaignBehavior reads them.
        public override void OnNewGameCreated(TaleWorlds.Core.Game game, object initializerObject)
        {
            base.OnNewGameCreated(game, initializerObject);
            var manager = game == null ? null : game.GameManager as MNBL.ProceduralWorld.CampaignRegistry.GeneratedGameManager;
            if (manager != null) manager.InstallNavalSceneWrapper();
        }

        public override void OnAfterGameLoaded(TaleWorlds.Core.Game game)
        {
            base.OnAfterGameLoaded(game);
            var manager = game == null ? null : game.GameManager as MNBL.ProceduralWorld.CampaignRegistry.GeneratedGameManager;
            if (manager != null) manager.InstallNavalSceneWrapper();
        }

        protected override void OnSubModuleUnloaded()
        {
            cancellation?.Cancel();
            Instance = null;
            AutorunHost.Current?.OnUnloaded();
            LogLifecycle("unloaded");
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        private void OnProcessExit(object sender, EventArgs args)
        {
            cancellation?.Cancel();
            AutorunHost.Current?.OnProcessExit();
            LogLifecycle("process_exit");
        }

        private static void LogLifecycle(string state)
        {
            var assembly = typeof(SubModule).Assembly;
            string digest;
            using (var stream = File.OpenRead(assembly.Location))
            using (var sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            var producerPin = Path.Combine(Path.GetDirectoryName(assembly.Location), "fwg.sha256");
            var record = new { utc = DateTime.UtcNow.ToString("O"), state, processId = Process.GetCurrentProcess().Id,
                moduleSha256 = digest, assembly = assembly.FullName, modulePath = assembly.Location,
                engineAssembly = typeof(MBSubModuleBase).Assembly.FullName,
                producerSha256 = File.Exists(producerPin) ? File.ReadAllText(producerPin).Trim() : null };
            File.AppendAllText(Path.Combine(Store, "module.log"), new JavaScriptSerializer().Serialize(record) + "\n");
        }
    }
}

