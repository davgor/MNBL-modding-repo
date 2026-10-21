using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using TaleWorlds.MountAndBlade;
using MNBL.ProceduralWorld.Automation;

namespace MNBL.ProceduralWorld
{
    public sealed class SubModule : MBSubModuleBase
    {
        private bool registered;
        internal static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNBL.ProceduralWorld");

        protected override void OnSubModuleLoad()
        {
            Directory.CreateDirectory(Store);
            LogLifecycle("loaded");
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            // Harness-requested probe (tools/Start-OwnedProbe.ps1); the request is claimed before anything runs.
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
            // MMR-43: the player's entry, beside Sandbox's New Campaign. It is the module's only main-menu entry: the developer
            // "FWGC: ..." diagnostics were removed once the setup screen covered generation, cancellation and errors.
            MNBL.ProceduralWorld.UI.WorldSetupScreen.Register();
        }

        protected override void OnApplicationTick(float dt)
        {
            var autorun = AutorunHost.Current;
            if (autorun != null) autorun.Tick(dt);
            // MMR-37: native calls run on the application tick thread (campaign map loading runs on it too; module load runs on another,
            // run 27772); it is bound on the first tick, before any world loads. Workers' continuations for it are run here.
            MNBL.ProceduralWorld.Surface.EngineThread.Bind();
            MNBL.ProceduralWorld.Surface.EngineWork.Pump();
            // MMR-44: the generation job's engine-thread items, within their per-tick budget.
            MNBL.ProceduralWorld.Generation.GenerationRuntime.Scheduler.Pump();
            // MMR-46: a world handed from the setup screen to the activation; the screen reopens if the player withdraws.
            MNBL.ProceduralWorld.UI.WorldSetupScreen.TickPending();
            // MMR-257: a ruin marker's name under the cursor on the campaign map.
            EngineMarks.Enter("MapDressingRuntime.Tick");
            MNBL.ProceduralWorld.World.MapDressingRuntime.Tick();
            // MMR-256: the generated campaign map's season threshold, atmosphere frame and colour grade (the manager's tick).
            EngineMarks.Enter("GeneratedMapAtmosphere.Tick");
            MNBL.ProceduralWorld.CampaignBootstrap.GeneratedMapAtmosphere.Tick(dt);
            EngineMarks.Leave();   // MMR-247: the hang capture's section marks (HangWatchdog)
        }

        // MMR-257: SandBox has built the campaign's encyclopedia pages (its module loads first); a generated campaign adds its ruins page.
        public override void OnGameInitializationFinished(TaleWorlds.Core.Game game)
        {
            base.OnGameInitializationFinished(game);
            MNBL.ProceduralWorld.CampaignRegistry.RuinsEncyclopediaPage.Register(game);
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
            AutorunHost.Current?.OnUnloaded();
            LogLifecycle("unloaded");
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        private void OnProcessExit(object sender, EventArgs args)
        {
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
