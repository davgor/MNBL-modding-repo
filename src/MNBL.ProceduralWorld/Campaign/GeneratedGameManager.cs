using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using SandBox;
using SandBox.CampaignBehaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.Core;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // The generated world a GeneratedGameManager registers (new game) or replays (saved game) through its
    // CampaignObjectRegistry.
    internal interface IGeneratedWorld
    {
        string WorldId { get; }
        void Register(Action<string> trace);   // RegisterSubModuleObjects(false), in place of SandBox's own call
        void Replay(Action<string> trace);     // RegisterSubModuleObjects(true): after BeforeLoad, before UnregisterNonReadyObjects
        IList<string> ClosureProblems(ClosureMode mode);
        // MMR-36: unregisters what a failed new-world registration staged (CampaignObjectRegistry.RollBack); what could not be removed.
        IList<string> RollBack();
        Dictionary<string, object> RegistryCensus(string phase, ClosureMode mode);
        // MMR-34: the world supplies the startup (bandit clans, hideouts, the retirement retreat and a runtime plan).
        bool SuppliesStartup { get; }
        GeneratedCampaignInitializer.StartupPlan StartupPlan { get; }   // available once the world has registered
        // MMR-35: the world's fleet rule (the clans that may not hold ships), or null for a world without naval content; known before
        // OnGameStart (new game: the registration's dataset plan; load: the package).
        NavalContentFactory.NavalPlan NavalPlan { get; }
    }

    // MMR-30: the game manager of a generated campaign, promoted from the MMR-18/19 diagnostic manager (runs 31308, 23192,
    // 24736, 24212, 31624 new game; 8256 load). The seams, in call order (docs/runtime-backend-decision.md):
    //   OnGameStart                  removes BackstoryCampaignBehavior (it dereferences the vanilla lords) and, unless the world
    //                                supplies the retirement retreat (MMR-34), RetirementCampaignBehavior; applies the world-lifetime
    //                                patches (GeneratedPatches.ApplyWorld, MMR-34); adds the periodic seam and the world identity
    //                                behaviours, and for a world with a startup the startup behaviour (MMR-34);
    //   RegisterSubModuleObjects     dispatches every submodule and replaces SandBox's own XML with the generated world's
    //                                registry (new game) or replay (saved game); the registry asserts closure before
    //                                returning;
    //   AfterRegisterSubModuleObjects  asserts closure again after every submodule registered, before CampaignObjectManager
    //                                publishes its default lists (InitializeOnNewGame);
    //   OnGameInitializationFinished installs the generated map-scene creator before the map loads;
    //   OnGameEnd                    removes this module's patches and its behaviours' listeners from the ending campaign's events, and
    //                                records what is left (LastTeardown, read by the next world's startup audit).
    // Every registry census (MBObjectManager and CampaignObjectManager) is kept in RegistryCensuses for MMR-36. A census is
    // diagnostic and fail-soft (a failure is recorded); the closure assertion is not.
    internal abstract class GeneratedGameManager : SandBoxGameManager
    {
        protected readonly IMapSceneCreator Creator;
        protected readonly Action<string> Trace;
        protected readonly bool LoadMode;
        private bool registeredWorld;
        internal IGeneratedWorld World { get; set; }
        internal PeriodicWorld Periodic { get; set; }
        internal Dictionary<string, string> IdentityEntries { get; set; }
        // MMR-32: the cultures character creation offers (the cultures the world presents through); null keeps the installed list.
        internal IList<string> CreationCultures { get; set; }
        public GeneratedCharacterCreation CreationFilter { get; private set; }
        public WorldIdentityBehavior Identity { get; private set; }
        // MMR-34: the runtime plan's instantiation (worlds that supply a startup) and the diagnostic spawn assertions (SpawnAssertions).
        public GeneratedStartupBehavior Startup { get; private set; }
        internal bool SpawnAssertions { get; set; }
        public GeneratedSpawnAssertions SpawnChecks { get; private set; }
        // MMR-35: the fleet rule on War Sails' model seams (worlds with naval content): no ships for a landlocked clan's parties, and an ideal
        // fleet of zero so any it holds is sold.
        public GeneratedClanShipOwnershipModel ShipOwnership { get; private set; }
        public GeneratedShipDistributionModel ShipDistribution { get; private set; }
        // MMR-39: War Sails' storms on generated geography (every generated world with War Sails).
        public GeneratedStormModel Storms { get; private set; }
        internal static bool NavalPresent { get { return TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo("NavalDLC") != null; } }
        public List<Dictionary<string, object>> RegistryCensuses { get; private set; }

        protected GeneratedGameManager(IMapSceneCreator creator, Action<string> trace)
            : base(() => new Campaign(CampaignGameMode.Campaign))
        {
            Creator = creator; Trace = trace; RegistryCensuses = new List<Dictionary<string, object>>();
        }

        protected GeneratedGameManager(IMapSceneCreator creator, TaleWorlds.SaveSystem.Load.LoadResult saved, Action<string> trace)
            : base(saved)
        {
            Creator = creator; Trace = trace; LoadMode = true; RegistryCensuses = new List<Dictionary<string, object>>();
        }

        // Diagnostics record their own census at each phase.
        protected virtual void OnPhase(string phase) { }

        // Engine callbacks run outside any probe coroutine, so an exception here ends the process without a managed record.
        // Record it, then let it propagate unchanged.
        protected void Traced(string callback, Action body)
        {
            try { body(); }
            catch (Exception error)
            {
                Trace("generated_callback_failed:" + callback + ":" + error);
                throw;
            }
        }

        // MMR-35: the fleet rule's model must be in the starter before the submodules' OnGameStart, where War Sails builds its model set
        // (NavalDLCManager.OnGameStart: Game.AddGameModelsManager<NavalDLC.GameModels>(starter.Models), last model of a type wins); every
        // submodule's InitializeGameStarter, War Sails' AddModels included, has run when the base returns, so War Sails' model becomes its
        // BaseModel (CampaignGameStarter.AddModel).
        public override void InitializeGameStarter(Game game, IGameStarter starterObject)
        {
            base.InitializeGameStarter(game, starterObject);
            if (World == null) return;
            if (NavalPresent) Traced("InitializeGameStarter:storms", () => RegisterStormModel(starterObject));
            if (World.NavalPlan == null) return;
            Traced("InitializeGameStarter", () => {
                var campaignStarter = starterObject as CampaignGameStarter;
                if (campaignStarter == null) throw new InvalidOperationException("Generated world requires CampaignGameStarter.");
                ShipOwnership = new GeneratedClanShipOwnershipModel(World.NavalPlan.Landlocked);
                campaignStarter.AddModel<NavalDLC.ComponentInterfaces.ClanShipOwnershipModel>(ShipOwnership);
                ShipDistribution = new GeneratedShipDistributionModel(ShipOwnership.Landlocked);
                campaignStarter.AddModel<NavalDLC.ComponentInterfaces.ShipDistributionModel>(ShipDistribution);
                Trace("naval_fleet_rule_registered:landlocked=" + String.Join(",", World.NavalPlan.Landlocked) + ":ownership_base=" + (ShipOwnership.Installed == null ? "none" : ShipOwnership.Installed.GetType().FullName) +
                      ":distribution_base=" + (ShipDistribution.Installed == null ? "none" : ShipDistribution.Installed.GetType().FullName));
            });
        }

        // MMR-39: chained over War Sails' storm model (its AddModels ran in the base InitializeGameStarter), as the fleet rule's models are.
        private void RegisterStormModel(IGameStarter starterObject)
        {
            var campaignStarter = starterObject as CampaignGameStarter;
            if (campaignStarter == null) throw new InvalidOperationException("Generated world requires CampaignGameStarter.");
            Storms = new GeneratedStormModel();
            GeneratedStormModel.ResetCounters();
            campaignStarter.AddModel<NavalDLC.ComponentInterfaces.MapStormModel>(Storms);
            Trace("storm_model_registered:base=" + (Storms.Installed == null ? "none" : Storms.Installed.GetType().FullName));
        }

        private static void ReleaseNavalWrapper() { GeneratedNavalMapSceneWrapper.Release(); }

        // MMR-39: the naval map-scene wrapper for the world's generated pirate spawn points (after War Sails' view built its own this
        // session; called from the submodule's OnNewGameCreated and OnAfterGameLoaded).
        internal void InstallNavalSceneWrapper()
        {
            if (World == null || World.NavalPlan == null || !NavalPresent) return;
            Traced("naval_scene_wrapper", () => GeneratedNavalMapSceneWrapper.Install(World.NavalPlan, Trace));
        }

        public override void OnGameStart(Game game, IGameStarter starter)
        {
            base.OnGameStart(game, starter);
            if (World == null) return;
            Traced("OnGameStart", () => RemoveOfficialWorldBehaviors(starter));
        }

        private void RemoveOfficialWorldBehaviors(IGameStarter starter)
        {
            var campaignStarter = starter as CampaignGameStarter;
            if (campaignStarter == null) throw new InvalidOperationException("Generated world requires CampaignGameStarter.");
            // SandBoxManager.Initialize added this earlier. Its new-game callback immediately dereferences named Calradian
            // lords and town_V6.
            RemoveExactlyOne<BackstoryCampaignBehavior>(campaignStarter);
            Trace("generated_backstory_dependency_removed_before_event_registration");
            // OnSessionLaunched caches Settlement.Find("retirement_retreat"); the hourly CheckRetirementSettlementVisibility
            // then dereferences it (PID34724 first-chance NRE). A world that supplies the retreat (MMR-34) keeps the behaviour.
            if (World.SuppliesStartup) Trace("generated_retirement_behavior_kept:the_world_supplies_retirement_retreat");
            else
            {
                RemoveExactlyOne<RetirementCampaignBehavior>(campaignStarter);
                Trace("generated_retirement_dependency_removed_before_event_registration");
            }
            // The wanderer introduction dereferences town_ES4 (LordConversationsCampaignBehavior); every generated world, new or loaded.
            GeneratedPatches.ApplyWorld(Trace);

            if (Periodic != null)
            {
                campaignStarter.AddBehavior(new PeriodicSeamBehavior());
                Trace("periodic_seam_behavior_registered");
            }
            Identity = new WorldIdentityBehavior();
            if (IdentityEntries != null) foreach (var entry in IdentityEntries) Identity.Entries[entry.Key] = entry.Value;
            campaignStarter.AddBehavior(Identity);
            Trace("world_identity_behavior_registered");
            MNBL.ProceduralWorld.Persistence.WorldSaveBinding.Attach(Identity, Trace);   // MMR-48: every later save journaled, the build recorded
            if (CreationCultures != null && !LoadMode)
            {
                CreationFilter = new GeneratedCharacterCreation(CreationCultures, Trace);
                campaignStarter.AddBehavior(CreationFilter);
                Trace("character_creation_culture_filter_registered:" + String.Join(",", CreationCultures));
            }
            if (World.SuppliesStartup)
            {
                var world = World;
                Startup = new GeneratedStartupBehavior(() => world.StartupPlan, Trace);
                campaignStarter.AddBehavior(Startup);   // a load needs it too: it saves the intent-id map
                if (!LoadMode) Startup.Activate();
                Trace("startup_behavior_registered:" + (LoadMode ? "load" : "new_game"));
            }
            if (SpawnAssertions)
            {
                SpawnChecks = new GeneratedSpawnAssertions();
                campaignStarter.AddBehavior(SpawnChecks);
                Trace("spawn_assertions_registered");
            }
        }

        // What a generated campaign leaves when it ends: the startup, world and creation patches are removed and this module's behaviours
        // stop listening to the ending campaign's events (CampaignEvents belongs to the campaign; GameManagerBase.OnGameEnd runs before
        // Campaign.OnDestroy, so Campaign.Current is still the ending campaign). The periodic patches end with the map scene
        // (GeneratedMapScene.Destroy, after this callback); the next world's audit checks them. Recorded, never thrown.
        internal sealed class TeardownRecord
        {
            public string WorldId, Error;
            public WeakReference Events;
            public List<WeakReference> Behaviors = new List<WeakReference>();
            public int OwnBehaviors, OwnListenersBefore, OwnListenersLeft;
            public List<string> PatchesLeft = new List<string>();
            public bool StartupActiveLeft, StartupAppliedLeft, WorldAppliedLeft;
        }
        internal static TeardownRecord LastTeardown { get; private set; }

        public override void OnGameEnd(Game game)
        {
            MNBL.ProceduralWorld.Generation.ActivationSaveBlock.End();   // MMR-46: never outlives the campaign it guarded
            MNBL.ProceduralWorld.Persistence.WorldSaveBinding.Detach();   // MMR-48: neither does the save binding
            MNBL.ProceduralWorld.Persistence.GeneratedWorldLoader.ReleaseEnded(this);   // MMR-49: nor the loader's reference to its game
            if (World != null) Teardown();
            else GeneratedStartupBehavior.Deactivate(Trace);
            base.OnGameEnd(game);
        }

        private void Teardown()
        {
            var record = new TeardownRecord { WorldId = World.WorldId };
            try
            {
                GeneratedStartupBehavior.Deactivate(Trace);
                GeneratedPatches.RemoveWorld(Trace);
                GeneratedCharacterCreation.Release(Trace);
                var campaign = Campaign.Current;
                var events = CampaignListeners.EventsOf(campaign);
                if (events != null)
                {
                    record.Events = new WeakReference(events);
                    var behaviors = campaign.GetCampaignBehaviors<CampaignBehaviorBase>().ToList();
                    var own = behaviors.Where(b => b.GetType().Assembly == typeof(GeneratedGameManager).Assembly).ToList();
                    foreach (var b in behaviors) record.Behaviors.Add(new WeakReference(b));
                    record.OwnBehaviors = own.Count;
                    record.OwnListenersBefore = CampaignListeners.Of(events).Where(c => own.Contains(c.Key.Value)).Sum(c => c.Value);
                    foreach (var b in own) events.RemoveListeners(b);
                    record.OwnListenersLeft = CampaignListeners.Of(events).Where(c => own.Contains(c.Key.Value)).Sum(c => c.Value);
                }
                if (NavalPresent) ReleaseNavalWrapper();   // MMR-39: the decorator must not keep the ended map scene alive
                // The periodic and colour-grade patches live as long as the map scene (removed in GeneratedMapScene.Destroy, after this).
                record.PatchesLeft = GeneratedPatches.Patched().Where(p => !p.StartsWith(PeriodicPatches.HarmonyId + ":", StringComparison.Ordinal)
                    && !p.StartsWith(GeneratedColorGrade.HarmonyId + ":", StringComparison.Ordinal)
                    && !MNBL.ProceduralWorld.Persistence.LoadRouting.IsProcessLifetime(p)).ToList();   // MMR-49: process lifetime
                GeneratedMapScene.FixturePrefabs.Clear();   // the fixture settlements' prefabs belong to this world
                GeneratedMapScene.RuntimeSettlementEntities.Clear();   // MMR-40: so do the settlement entities made at run time
                GeneratedMapScene.MissingBakedEntities.Clear(); GeneratedMapScene.SiegePrefabCacheSource = null;
                // MMR-49: the battle terrain context and the battle scene observations belong to this world too (they outlived a committed
                // campaign before: WorldLifetimeScope).
                GeneratedBattleTerrain.ContextSources.Clear(); MNBL.ProceduralWorld.Missions.MissionScenePatches.Clear();
                record.StartupActiveLeft = GeneratedStartupBehavior.Active != null;
                record.StartupAppliedLeft = GeneratedPatches.StartupApplied; record.WorldAppliedLeft = GeneratedPatches.WorldApplied;
                Trace("generated_teardown:own_listeners=" + record.OwnListenersBefore + "->" + record.OwnListenersLeft + ":patches_left=" + record.PatchesLeft.Count);
            }
            catch (Exception error) { record.Error = error.ToString(); Trace("generated_teardown_failed:" + error); }
            LastTeardown = record;
        }

        private static void RemoveExactlyOne<T>(CampaignGameStarter starter) where T : CampaignBehaviorBase
        {
            var found = starter.CampaignBehaviors.OfType<T>().ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Unexpected installed behavior inventory: " + typeof(T).FullName);
            if (!starter.RemoveBehavior(found[0])) throw new InvalidOperationException("Official-world behavior was not removed: " + typeof(T).FullName);
        }

        public override void RegisterSubModuleObjects(bool isSavedGame)
        {
            if (World == null) { base.RegisterSubModuleObjects(isSavedGame); return; }
            Traced("RegisterSubModuleObjects", () => RegisterGeneratedSubModuleObjects(isSavedGame));
        }

        private void RegisterGeneratedSubModuleObjects(bool isSavedGame)
        {
            // Save reconstruction must happen before deserialization. Reusing this new-game callback for a saved world would
            // silently rebuild identity.
            if (isSavedGame != LoadMode) throw new NotSupportedException(LoadMode ? "A generated load must take the saved-game path." : "A new generated world cannot take the saved-game path; load it through a saved-game manager.");
            if (registeredWorld) throw new InvalidOperationException("Generated object registration was invoked twice.");
            var modules = TaleWorlds.MountAndBlade.Module.CurrentModule.CollectSubModules().ToArray();
            if (modules.Count(module => module.GetType() == typeof(SandBoxSubModule)) != 1)
                throw new InvalidOperationException("Expected exactly one installed SandBoxSubModule.");
            if (modules.Count(module => module.GetType().FullName == "NavalDLC.NavalDLCSubModule") != 1)
                throw new InvalidOperationException("Expected exactly one installed NavalDLCSubModule.");
            OnPhase("before_generated_registration");
            foreach (var module in modules)
            {
                Trace("generated_registration_dispatch:" + module.GetType().FullName);
                if (module.GetType() == typeof(SandBoxSubModule))
                {
                    if (LoadMode) World.Replay(Trace); else World.Register(Trace);
                    registeredWorld = true;
                }
                else module.RegisterSubModuleObjects(isSavedGame);
            }
            OnPhase("after_all_submodule_registration");
        }

        public override void AfterRegisterSubModuleObjects(bool isSavedGame)
        {
            base.AfterRegisterSubModuleObjects(isSavedGame);
            if (World != null)
                Traced("AfterRegisterSubModuleObjects", () => {
                    var closure = World.ClosureProblems(LoadMode ? ClosureMode.Replay : ClosureMode.Registration);
                    RecordCensus("after_submodule_post_registration");
                    if (closure.Count > 0)
                    {
                        if (!LoadMode) Trace("generated_registration_rolled_back:" + World.RollBack().Count + " remaining");
                        throw new RegistrationException("Generated world not closed after every submodule registered, before the default lists publish", closure);
                    }
                    Trace("generated_registry_closed");
                });
            OnPhase("after_submodule_post_registration");
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game); // includes SandBox's default creator assignment
            var campaign = game.GameType as Campaign;
            if (campaign == null) throw new InvalidOperationException("Expected a Campaign game type.");
            if (Creator != null) campaign.MapSceneCreator = Creator; // instance-only; before Campaign loading stage 2
            if (World != null) RecordCensus("game_initialization_finished");
        }

        private void RecordCensus(string phase)
        {
            try { RegistryCensuses.Add(World.RegistryCensus(phase, LoadMode ? ClosureMode.Replay : ClosureMode.Registration)); }
            catch (Exception error)
            {
                Trace("registry_census_failed:" + phase + ":" + error.GetType().Name + ":" + error.Message);
                RegistryCensuses.Add(new Dictionary<string, object> { ["phase"] = phase, ["censusFailed"] = error.ToString() });
            }
        }
    }
}
