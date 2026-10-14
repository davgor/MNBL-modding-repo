using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.Diagnostics;
using SandBox;
using SandBox.CampaignBehaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // Opt-in diagnostic only. Root calls Tick on the game thread; no automatic entry point.
    public sealed class DiagnosticSandboxBootstrap : SandBoxGameManager
    {
        private readonly IMapSceneCreator creator;
        private readonly Action<string> trace;
        private readonly string cultureId;
        private readonly string characterName;
        private Game ownedGame;
        private CharacterCreationState creationState;
        private readonly HashSet<string> menus = new HashSet<string>();
        private bool failed;
        private bool finished;
        private bool narrativeStarted;
        private int steps;
        private Action registerWorldObjects;
        private Action<Dictionary<string, object>> recordCensus;
        private string generatedWorldId;
        private PeriodicWorld periodic;
        private bool registeredWorld;
        // MMR-19 load mode: a saved generated world, re-applied from its package instead of registered from a dataset.
        private readonly bool loadMode;
        private Action reapplyWorld;
        private Dictionary<string, string> identityEntries;
        public WorldIdentityBehavior Identity { get; private set; }

        // Tick re-pauses the map every frame while this is set; a gameplay probe clears it to run time.
        public bool HoldPaused { get; set; } = true;
        public bool Completed { get { return finished; } }
        public bool Failed { get { return failed; } }
        public string Error { get; private set; }

        public static DiagnosticSandboxBootstrap Start(bool explicitDiagnosticFlag, Action<string> trace)
        {
            if (!explicitDiagnosticFlag) throw new InvalidOperationException("Diagnostic opt-in required.");
            DiagnosticMapSceneCreator.ValidateFixture();
            return Start(true, new DiagnosticMapSceneCreator(trace), "empire", "MNBL Diagnostic", trace);
        }

        private DiagnosticSandboxBootstrap(IMapSceneCreator creator, string cultureId,
            string characterName, Action<string> trace)
            : base(() => new Campaign(CampaignGameMode.Campaign))
        {
            this.creator = creator;
            this.cultureId = cultureId;
            this.characterName = characterName;
            this.trace = trace;
        }

        private DiagnosticSandboxBootstrap(IMapSceneCreator creator, TaleWorlds.SaveSystem.Load.LoadResult saved, Action<string> trace)
            : base(saved)
        {
            this.creator = creator;
            this.trace = trace;
            loadMode = true;
        }

        // MMR-19: loads a generated save. The caller has already read the world identity from the save's metadata and verified
        // the world package, before deserializing; reapply re-merges the package's generated XML in RegisterSubModuleObjects(true).
        internal static DiagnosticSandboxBootstrap StartGeneratedLoad(TaleWorlds.SaveSystem.Load.LoadResult saved, string worldId, string sceneName,
            Action reapply, Action<Dictionary<string, object>> census, Dictionary<string, object> supportReport, Action<string> trace,
            PeriodicWorld periodic, GeneratedClimate climate, Dictionary<string, string> identity)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the initial menu first.");
            if (saved == null || !saved.Successful) throw new ArgumentException("A successfully deserialized save is required.");
            if (reapply == null || census == null || trace == null || supportReport == null) throw new ArgumentNullException("Generated load callbacks/report");
            DiagnosticMapSceneCreator.ValidateFixture(sceneName);
            var manager = new DiagnosticSandboxBootstrap(new DiagnosticMapSceneCreator(sceneName, trace, worldId, supportReport, periodic, climate), saved, trace);
            manager.periodic = periodic;
            manager.generatedWorldId = worldId;
            manager.reapplyWorld = reapply;
            manager.recordCensus = census;
            manager.identityEntries = identity;
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        public static DiagnosticSandboxBootstrap Start(bool explicitDiagnosticFlag,
            IMapSceneCreator creator, string cultureId, string characterName, Action<string> trace)
        {
            if (!explicitDiagnosticFlag) throw new InvalidOperationException("Diagnostic opt-in required.");
            if (Game.Current != null) throw new InvalidOperationException("Return to the initial menu first.");
            if (creator == null || trace == null) throw new ArgumentNullException("creator/trace");
            if (String.IsNullOrWhiteSpace(cultureId) || String.IsNullOrWhiteSpace(characterName))
                throw new ArgumentException("Explicit culture id and character name required.");
            var manager = new DiagnosticSandboxBootstrap(creator, cultureId, characterName, trace);
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        internal static DiagnosticSandboxBootstrap StartGenerated(string worldId, string sceneName,
            string cultureId, Action registerObjects, Action<Dictionary<string, object>> census,
            Dictionary<string, object> supportReport, Action<string> trace, PeriodicWorld periodic = null, GeneratedClimate climate = null)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the initial menu first.");
            if (String.IsNullOrWhiteSpace(worldId) || String.IsNullOrWhiteSpace(cultureId))
                throw new ArgumentException("Generated world and culture identities are required.");
            if (registerObjects == null || census == null || trace == null || supportReport == null) throw new ArgumentNullException("Generated bootstrap callbacks/report");
            DiagnosticMapSceneCreator.ValidateFixture(sceneName);
            var manager = new DiagnosticSandboxBootstrap(new DiagnosticMapSceneCreator(sceneName, trace, worldId, supportReport, periodic, climate),
                cultureId, "Traveler " + worldId, trace);
            manager.periodic = periodic;
            manager.generatedWorldId = worldId;
            manager.registerWorldObjects = registerObjects;
            manager.recordCensus = census;
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        private void Census(string phase)
        {
            if (recordCensus == null) return;
            if (!loadMode) { Traced("census:" + phase, () => recordCensus(CampaignBootstrapProbe.Capture(generatedWorldId, phase))); return; }
            // While a save loads, saved objects are not ready until the generated XML is re-applied; a census failure is
            // recorded and must not abort the load.
            try { recordCensus(CampaignBootstrapProbe.Capture(generatedWorldId, phase)); }
            catch (Exception error) { trace("census_failed:" + phase + ":" + error.GetType().Name + ":" + error.Message); }
        }

        // Engine callbacks run outside the probe coroutine, so an exception here ends the
        // process without a managed record. Record it, then let it propagate unchanged.
        private void Traced(string callback, Action body)
        {
            try { body(); }
            catch (Exception error)
            {
                trace("generated_callback_failed:" + callback + ":" + error);
                throw;
            }
        }

        public override void OnGameStart(Game game, IGameStarter starter)
        {
            base.OnGameStart(game, starter);
            if (registerWorldObjects == null && reapplyWorld == null) return;
            Traced("OnGameStart", () => RemoveOfficialWorldBehaviors(starter));
        }

        private void RemoveOfficialWorldBehaviors(IGameStarter starter)
        {
            var campaignStarter = starter as CampaignGameStarter;
            if (campaignStarter == null) throw new InvalidOperationException("Generated world requires CampaignGameStarter.");
            // SandBoxManager.Initialize added this earlier. Its new-game callback
            // immediately dereferences named Calradian lords and town_V6.
            RemoveExactlyOne<BackstoryCampaignBehavior>(campaignStarter);
            trace("generated_backstory_dependency_removed_before_event_registration");
            // OnSessionLaunched caches Settlement.Find("retirement_retreat"); the hourly
            // CheckRetirementSettlementVisibility then dereferences it (PID34724 first-chance NRE).
            RemoveExactlyOne<RetirementCampaignBehavior>(campaignStarter);
            trace("generated_retirement_dependency_removed_before_event_registration");
            if (periodic != null)
            {
                campaignStarter.AddBehavior(new PeriodicSeamBehavior());
                trace("periodic_seam_behavior_registered");
            }
            Identity = new WorldIdentityBehavior();
            if (identityEntries != null) foreach (var entry in identityEntries) Identity.Entries[entry.Key] = entry.Value;
            campaignStarter.AddBehavior(Identity);
            trace("world_identity_behavior_registered");
        }

        private static void RemoveExactlyOne<T>(CampaignGameStarter starter) where T : CampaignBehaviorBase
        {
            var found = starter.CampaignBehaviors.OfType<T>().ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Unexpected installed behavior inventory: " + typeof(T).FullName);
            if (!starter.RemoveBehavior(found[0])) throw new InvalidOperationException("Official-world behavior was not removed: " + typeof(T).FullName);
        }

        public override void RegisterSubModuleObjects(bool isSavedGame)
        {
            if (registerWorldObjects == null && reapplyWorld == null) { base.RegisterSubModuleObjects(isSavedGame); return; }
            Traced("RegisterSubModuleObjects", () => RegisterGeneratedSubModuleObjects(isSavedGame));
        }

        private void RegisterGeneratedSubModuleObjects(bool isSavedGame)
        {
            // Save reconstruction must happen before deserialization. Reusing this
            // new-game callback for a saved world would silently rebuild identity.
            if (isSavedGame != loadMode) throw new NotSupportedException(loadMode ? "A generated load must take the saved-game path." : "A new generated world cannot take the saved-game path; use StartGeneratedLoad.");
            if (registeredWorld) throw new InvalidOperationException("Generated object registration was invoked twice.");
            var modules = TaleWorlds.MountAndBlade.Module.CurrentModule.CollectSubModules().ToArray();
            if (modules.Count(module => module.GetType() == typeof(SandBoxSubModule)) != 1)
                throw new InvalidOperationException("Expected exactly one installed SandBoxSubModule.");
            if (modules.Count(module => module.GetType().FullName == "NavalDLC.NavalDLCSubModule") != 1)
                throw new InvalidOperationException("Expected exactly one installed NavalDLCSubModule.");
            Census("before_generated_registration");
            foreach (var module in modules)
            {
                trace("generated_registration_dispatch:" + module.GetType().FullName);
                if (module.GetType() == typeof(SandBoxSubModule))
                {
                    if (loadMode) reapplyWorld(); else registerWorldObjects();
                    registeredWorld = true;
                }
                else module.RegisterSubModuleObjects(isSavedGame);
            }
            Census("after_all_submodule_registration");
        }

        public override void AfterRegisterSubModuleObjects(bool isSavedGame)
        {
            base.AfterRegisterSubModuleObjects(isSavedGame);
            Census("after_submodule_post_registration");
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game); // includes SandBox's default creator assignment
            ownedGame = game;
            var campaign = game.GameType as Campaign;
            if (campaign == null) throw new InvalidOperationException("Expected diagnostic Campaign.");
            campaign.MapSceneCreator = creator; // instance-only; before Campaign loading stage 2
            campaign.SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(trace);
            Pause(campaign);
            trace("diagnostic_creator_installed");
            Census("game_initialization_finished");
        }

        public override void OnLoadFinished()
        {
            if (loadMode)
            {
                if (!LoadingSavedGame || !Object.ReferenceEquals(Game.Current, ownedGame)) throw new InvalidOperationException("Diagnostic load ownership mismatch.");
                Traced("OnLoadFinished", () => base.OnLoadFinished()); // saved branch: MapState, menu restore, OnGameLoadFinished
                ((Campaign)ownedGame.GameType).SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(trace);
                Pause((Campaign)ownedGame.GameType);
                trace("generated_save_loaded");
                Census("saved_game_loaded");
                return;
            }
            // Exact new-game branch from LaunchSandboxCharacterCreation, omitting only intro video.
            if (LoadingSavedGame || !Object.ReferenceEquals(Game.Current, ownedGame))
                throw new InvalidOperationException("Diagnostic ownership mismatch.");
            // SandBox.OnCampaignStart installs another save manager after early initialization.
            ((Campaign)ownedGame.GameType).SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(trace);
            creationState = ownedGame.GameStateManager.CreateState<CharacterCreationState>();
            ownedGame.GameStateManager.CleanAndPushState(creationState, 0);
            IsLoaded = true;
            trace("character_creation_active");
            Census("character_creation_active");
        }

        // One stage/menu per application tick. Never call from a worker thread or inside a creation event.
        public void Tick()
        {
            if (failed || (!loadMode && creationState == null) || ownedGame == null) return;
            try
            {
                if (!Object.ReferenceEquals(Game.Current, ownedGame))
                    throw new InvalidOperationException("Diagnostic game ended or changed.");
                var active = ownedGame.GameStateManager.ActiveState;
                if (active is MapState)
                {
                    if (HoldPaused) Pause((Campaign)ownedGame.GameType);
                    if (finished) return;
                    finished = true;
                    trace("diagnostic_map_state_active");
                    Census("map_state_active");
                    return;
                }
                if (loadMode || !Object.ReferenceEquals(active, creationState)) return;
                if (++steps > 128) throw new InvalidOperationException("Character creation step limit.");
                var manager = creationState.CharacterCreationManager;
                var content = manager.CharacterCreationContent;
                var stage = manager.CurrentStage;
                if (stage == null) throw new InvalidOperationException("Character creation not activated.");
                trace("character_stage:" + stage.GetType().FullName);
                if (stage is CharacterCreationCultureStage)
                {
                    var culture = content.GetCultures().SingleOrDefault(c => c.StringId == cultureId);
                    if (culture == null) throw new InvalidOperationException("Culture not registered: " + cultureId);
                    content.SetSelectedCulture(culture, manager);
                    content.SetMainCharacterName(characterName);
                }
                else if (stage is CharacterCreationNarrativeStage)
                {
                    if (!narrativeStarted) { manager.StartNarrativeStage(); narrativeStarted = true; }
                    var menu = manager.CurrentMenu;
                    if (menu == null || !menus.Add(menu.StringId))
                        throw new InvalidOperationException("Missing or repeated narrative menu.");
                    var option = manager.GetSuitableNarrativeMenuOptions().FirstOrDefault();
                    if (option == null) throw new InvalidOperationException("No suitable narrative option.");
                    manager.OnNarrativeMenuOptionSelected(option); // records choice and OnSelect
                    trace("narrative_menu:" + menu.StringId);
                    if (manager.TrySwitchToNextMenu()) return; // executes OnConsequence, including last menu
                }
                else if (stage is CharacterCreationBannerEditorStage)
                {
                    var clan = Clan.PlayerClan;
                    var banner = clan.Banner;
                    if (banner == null) throw new InvalidOperationException("Player banner missing.");
                    clan.Color = banner.GetPrimaryColor();
                    clan.Color2 = banner.GetFirstIconColor();
                    clan.UpdateBannerColor(clan.Color, clan.Color2);
                    content.SetMainClanBanner(banner);
                }
                else if (stage is CharacterCreationClanNamingStage)
                {
                    var name = new TextObject("MNBL Diagnostic", null);
                    Clan.PlayerClan.ChangeClanName(name, name);
                }
                else if (stage is CharacterCreationReviewStage)
                {
                    content.SetMainCharacterName(characterName);
                    var name = new TextObject(characterName, null);
                    Hero.MainHero.SetName(name, name);
                }
                else if (!(stage is CharacterCreationFaceGeneratorStage) && !(stage is CharacterCreationOptionsStage))
                    throw new InvalidOperationException("Untraced character creation stage: " + stage.GetType());
                // Executes stage finalization + all OnStageCompleted callbacks. The last call applies
                // final effects, then FinalizeCharacterCreationState, then OnCharacterCreationIsOver.
                manager.NextStage();
                // FinalizeCharacterCreationState pushes MapState synchronously; pause before returning.
                if (ownedGame.GameStateManager.ActiveState is MapState) Pause((Campaign)ownedGame.GameType);
            }
            catch (Exception error)
            {
                failed = true; // no automatic retries, save creation, or fallback to vanilla scene
                Error = error.ToString();
                trace("diagnostic_boot_failed:" + error);
            }
        }

        internal static void Run(Campaign campaign, CampaignTimeControlMode mode)
        {
            campaign.SetTimeControlModeLock(false);
            campaign.TimeControlMode = mode;
        }

        internal static void Pause(Campaign campaign)
        {
            campaign.SetTimeControlModeLock(false);
            campaign.TimeControlMode = CampaignTimeControlMode.Stop;
            campaign.SetTimeControlModeLock(true);
        }

        private sealed class DiagnosticNoAutoSaveManager : ISaveManager
        {
            private readonly Action<string> trace;
            public DiagnosticNoAutoSaveManager(Action<string> trace) { this.trace = trace; }
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string saveName)
            {
                trace("unexpected_diagnostic_save:" + success + ":" + saveName);
            }
        }
    }
}
