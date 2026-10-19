using System;
using System.Collections.Generic;
using System.Linq;
using MNBL.ProceduralWorld.CampaignRegistry;
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
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // What a diagnostic drives a campaign through while it runs time: the fixture's owned game loop (DiagnosticSandboxBootstrap), or a
    // campaign the game runs itself, such as a loaded save (LoadedCampaignTicker: Tick does nothing and it never fails).
    internal interface ICampaignTicker
    {
        bool HoldPaused { get; set; }
        void Tick();
        bool Failed { get; }
        string Error { get; }
    }

    internal sealed class LoadedCampaignTicker : ICampaignTicker
    {
        public bool HoldPaused { get; set; } = true;
        public void Tick() { }
        public bool Failed { get { return false; } }
        public string Error { get { return null; } }
    }

    // Opt-in diagnostic only. Root calls Tick on the game thread; no automatic entry point. The generated-campaign seams
    // (behaviours, registration dispatch, closure, map-scene creator) are GeneratedGameManager's (MMR-30); this class adds
    // the probe driver: character creation, pausing, no autosave and a census at every phase.
    internal sealed class DiagnosticSandboxBootstrap : GeneratedGameManager, ICampaignTicker
    {
        private readonly string cultureId;
        private readonly string characterName;
        private Game ownedGame;
        private CharacterCreationState creationState;
        private readonly HashSet<string> menus = new HashSet<string>();
        private bool failed;
        private bool finished;
        private bool narrativeStarted;
        private int steps;
        private Action<Dictionary<string, object>> recordCensus;
        private string generatedWorldId;

        // Tick re-pauses the map every frame while this is set; a gameplay probe clears it to run time.
        public bool HoldPaused { get; set; } = true;
        // MMR-32: when set before character creation, every listed culture's origins and creation assets are audited at the start of the
        // narrative stage.
        public bool AuditCreation { get; set; }
        public Dictionary<string, object> CreationAudit { get; private set; }
        public bool Completed { get { return finished; } }
        public bool Failed { get { return failed; } }
        public string Error { get; private set; }

        public static DiagnosticSandboxBootstrap Start(bool explicitDiagnosticFlag, Action<string> trace)
        {
            if (!explicitDiagnosticFlag) throw new InvalidOperationException("Diagnostic opt-in required.");
            GeneratedMapSceneCreator.ValidateFixture();
            return Start(true, new GeneratedMapSceneCreator(trace), "empire", "MNBL Diagnostic", trace);
        }

        private DiagnosticSandboxBootstrap(IMapSceneCreator creator, string cultureId, string characterName, Action<string> trace)
            : base(creator, trace)
        {
            this.cultureId = cultureId;
            this.characterName = characterName;
        }

        private DiagnosticSandboxBootstrap(IMapSceneCreator creator, TaleWorlds.SaveSystem.Load.LoadResult saved, Action<string> trace)
            : base(creator, saved, trace)
        {
        }

        // MMR-19: loads a generated save. The caller has already read the world identity from the save's metadata and verified
        // the world package, before deserializing; the world's registry replays the package's generated XML in
        // RegisterSubModuleObjects(true).
        internal static DiagnosticSandboxBootstrap StartGeneratedLoad(TaleWorlds.SaveSystem.Load.LoadResult saved, IGeneratedWorld world, string sceneName,
            Action<Dictionary<string, object>> census, Dictionary<string, object> supportReport, Action<string> trace,
            PeriodicWorld periodic, GeneratedClimate climate, Dictionary<string, string> identity)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the initial menu first.");
            if (saved == null || !saved.Successful) throw new ArgumentException("A successfully deserialized save is required.");
            if (world == null || census == null || trace == null || supportReport == null) throw new ArgumentNullException("Generated load world/callbacks/report");
            GeneratedMapSceneCreator.ValidateFixture(sceneName);
            if (periodic != null) PeriodicPatches.Preflight(periodic);   // MMR-64: refuse before the game starts when a periodic patch target refuses
            var manager = new DiagnosticSandboxBootstrap(new GeneratedMapSceneCreator(sceneName, trace, world.WorldId, supportReport, periodic, climate), saved, trace);
            manager.Periodic = periodic;
            manager.World = world;
            manager.generatedWorldId = world.WorldId;
            manager.recordCensus = census;
            manager.IdentityEntries = identity;
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

        internal static DiagnosticSandboxBootstrap StartGenerated(IGeneratedWorld world, string sceneName, string cultureId,
            Action<Dictionary<string, object>> census, Dictionary<string, object> supportReport, Action<string> trace,
            PeriodicWorld periodic = null, GeneratedClimate climate = null, IList<string> creationCultures = null, bool spawnAssertions = false)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the initial menu first.");
            if (world == null || String.IsNullOrWhiteSpace(world.WorldId) || String.IsNullOrWhiteSpace(cultureId))
                throw new ArgumentException("Generated world and culture identities are required.");
            if (census == null || trace == null || supportReport == null) throw new ArgumentNullException("Generated bootstrap callbacks/report");
            GeneratedMapSceneCreator.ValidateFixture(sceneName);
            if (periodic != null) PeriodicPatches.Preflight(periodic);   // MMR-64: refuse before the game starts when a periodic patch target refuses
            var manager = new DiagnosticSandboxBootstrap(new GeneratedMapSceneCreator(sceneName, trace, world.WorldId, supportReport, periodic, climate),
                cultureId, "Traveler " + world.WorldId, trace);
            manager.Periodic = periodic;
            manager.World = world;
            manager.generatedWorldId = world.WorldId;
            manager.recordCensus = census;
            manager.CreationCultures = creationCultures;
            manager.SpawnAssertions = spawnAssertions;   // MMR-34: diagnostic out-of-bounds and off-navmesh assertions
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        protected override void OnPhase(string phase) { Census(phase); }

        private void Census(string phase)
        {
            if (recordCensus == null) return;
            if (!LoadMode) { Traced("census:" + phase, () => recordCensus(CampaignBootstrapProbe.Capture(generatedWorldId, phase))); return; }
            // While a save loads, saved objects are not ready until the generated XML is re-applied; a census failure is
            // recorded and must not abort the load.
            try { recordCensus(CampaignBootstrapProbe.Capture(generatedWorldId, phase)); }
            catch (Exception error) { Trace("census_failed:" + phase + ":" + error.GetType().Name + ":" + error.Message); }
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game); // SandBox's own, then the generated map-scene creator
            ownedGame = game;
            var campaign = (Campaign)game.GameType;
            campaign.SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(Trace);
            Pause(campaign);
            Trace("diagnostic_creator_installed");
            Census("game_initialization_finished");
        }

        public override void OnLoadFinished()
        {
            if (LoadMode)
            {
                if (!LoadingSavedGame || !Object.ReferenceEquals(Game.Current, ownedGame)) throw new InvalidOperationException("Diagnostic load ownership mismatch.");
                Traced("OnLoadFinished", () => base.OnLoadFinished()); // saved branch: MapState, menu restore, OnGameLoadFinished
                ((Campaign)ownedGame.GameType).SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(Trace);
                Pause((Campaign)ownedGame.GameType);
                Trace("generated_save_loaded");
                Census("saved_game_loaded");
                return;
            }
            // Exact new-game branch from LaunchSandboxCharacterCreation, omitting only intro video.
            if (LoadingSavedGame || !Object.ReferenceEquals(Game.Current, ownedGame))
                throw new InvalidOperationException("Diagnostic ownership mismatch.");
            // SandBox.OnCampaignStart installs another save manager after early initialization.
            ((Campaign)ownedGame.GameType).SandBoxManager.SandBoxSaveManager = new DiagnosticNoAutoSaveManager(Trace);
            creationState = ownedGame.GameStateManager.CreateState<CharacterCreationState>();
            ownedGame.GameStateManager.CleanAndPushState(creationState, 0);
            IsLoaded = true;
            Trace("character_creation_active");
            Census("character_creation_active");
        }

        // One stage/menu per application tick. Never call from a worker thread or inside a creation event.
        public void Tick()
        {
            if (failed || (!LoadMode && creationState == null) || ownedGame == null) return;
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
                    Trace("diagnostic_map_state_active");
                    Census("map_state_active");
                    return;
                }
                if (LoadMode || !Object.ReferenceEquals(active, creationState)) return;
                if (++steps > 128) throw new InvalidOperationException("Character creation step limit.");
                var manager = creationState.CharacterCreationManager;
                var content = manager.CharacterCreationContent;
                var stage = manager.CurrentStage;
                if (stage == null) throw new InvalidOperationException("Character creation not activated.");
                Trace("character_stage:" + stage.GetType().FullName);
                if (stage is CharacterCreationCultureStage)
                {
                    var culture = content.GetCultures().SingleOrDefault(c => c.StringId == cultureId);
                    if (culture == null) throw new InvalidOperationException("Culture not registered: " + cultureId);
                    content.SetSelectedCulture(culture, manager);
                    content.SetMainCharacterName(characterName);
                }
                else if (stage is CharacterCreationNarrativeStage)
                {
                    if (!narrativeStarted)
                    {
                        manager.StartNarrativeStage(); narrativeStarted = true;
                        if (AuditCreation && CreationAudit == null) CreationAudit = AuditCharacterCreation(manager, content);
                    }
                    var menu = manager.CurrentMenu;
                    if (menu == null || !menus.Add(menu.StringId))
                        throw new InvalidOperationException("Missing or repeated narrative menu.");
                    var option = manager.GetSuitableNarrativeMenuOptions().FirstOrDefault();
                    if (option == null) throw new InvalidOperationException("No suitable narrative option.");
                    manager.OnNarrativeMenuOptionSelected(option); // records choice and OnSelect
                    Trace("narrative_menu:" + menu.StringId);
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
                Trace("diagnostic_boot_failed:" + error);
            }
        }

        // MMR-32: every culture character creation lists, with its origins and creation assets, audited once the narrative stage has
        // started (NarrativeMenuOption.OnSelect dereferences CurrentMenu, installed IL). Parent options check the culture; later menus check
        // the parent occupation the parent option set (education: urban or rural); youth options set the title type that names the
        // player's creation rosters. For each culture: every suitable parent origin is selected through the engine's own OnSelect; for that
        // origin every later menu must offer a suitable option, and each of those options is selected (with CurrentMenu set to its menu)
        // to collect the title types it sets; then, for every origin and title type, the characters every menu shows
        // (GetNarrativeMenuCharacterArgs) must have resolvable equipment rosters and hand items. Fail-soft: every exception is a recorded
        // problem. The probe's culture is restored afterwards (and read back), and its own path replays OnSelect for every option it takes.
        private static Dictionary<string, object> AuditCharacterCreation(CharacterCreationManager manager, CharacterCreationContent content)
        {
            var objects = Game.Current.ObjectManager;
            var problems = new List<string>();
            var cultures = new List<object>();
            var selected = content.SelectedCulture;
            var first = manager.CurrentMenu;
            var setMenu = typeof(CharacterCreationManager).GetProperty("CurrentMenu").GetSetMethod(true);
            Action<NarrativeMenu> current = menu => setMenu.Invoke(manager, new object[] { menu });
            var later = manager.NarrativeMenus.Where(m => m != first).ToList();
            try
            {
                foreach (var culture in content.GetCultures().OrderBy(c => c.StringId, StringComparer.Ordinal).ToList())
                {
                    var origins = new List<object>();
                    var titles = new SortedSet<string>(StringComparer.Ordinal);
                    var parents = new List<NarrativeMenuOption>();
                    int characters = 0, rosters = 0, items = 0, selections = 0;
                    try
                    {
                        content.SetSelectedCulture(culture, manager);
                        if (first != null) parents = first.CharacterCreationMenuOptions.Where(o => o.OnCondition(manager)).ToList();
                        if (parents.Count == 0) problems.Add(culture.StringId + ": the first narrative menu offers no origin");
                        foreach (var origin in parents)
                        {
                            current(first);
                            try { origin.OnSelect(manager); selections++; }
                            catch (Exception error) { problems.Add(culture.StringId + ": origin " + origin.StringId + " OnSelect threw " + error.GetType().Name + ": " + error.Message); continue; }
                            string parent = content.SelectedParentOccupation;
                            var menus = new Dictionary<string, object>();
                            foreach (var menu in later)
                            {
                                var suitable = menu.CharacterCreationMenuOptions.Where(o => o.OnCondition(manager)).ToList();
                                menus[menu.StringId] = suitable.Select(o => o.StringId).ToList();
                                if (suitable.Count == 0) problems.Add(culture.StringId + ": origin " + origin.StringId + " (" + parent + ") leaves narrative menu " + menu.StringId + " without an option");
                                foreach (var option in suitable)
                                {
                                    current(menu);
                                    try { option.OnSelect(manager); selections++; titles.Add(content.SelectedTitleType ?? ""); }
                                    catch (Exception error) { problems.Add(culture.StringId + ": " + menu.StringId + " option " + option.StringId + " OnSelect threw " + error.GetType().Name + ": " + error.Message); }
                                }
                                current(first);
                                // Selecting later options may change the parent occupation; reselect the origin before the next menu.
                                try { origin.OnSelect(manager); } catch (Exception) { }
                            }
                            origins.Add(new Dictionary<string, object> { ["option"] = origin.StringId, ["parentOccupation"] = parent, ["menus"] = menus });
                        }
                        foreach (var origin in parents)
                        {
                            current(first);
                            try { origin.OnSelect(manager); } catch (Exception) { continue; }
                            foreach (var title in titles)
                                foreach (var menu in manager.NarrativeMenus.Where(m => m.GetNarrativeMenuCharacterArgs != null))
                                {
                                    List<NarrativeMenuCharacterArgs> args;
                                    try { args = menu.GetNarrativeMenuCharacterArgs(culture, title, manager); }
                                    catch (Exception error) { problems.Add(culture.StringId + ": " + menu.StringId + " (" + origin.StringId + ", " + title + ") characters threw " + error.GetType().Name + ": " + error.Message); continue; }
                                    foreach (var a in args ?? new List<NarrativeMenuCharacterArgs>())
                                    {
                                        characters++;
                                        if (!String.IsNullOrEmpty(a.EquipmentId))
                                        {
                                            rosters++;
                                            if (objects.GetObject<MBEquipmentRoster>(a.EquipmentId) == null) problems.Add(culture.StringId + ": " + menu.StringId + " (" + origin.StringId + ", " + title + ") equipment roster " + a.EquipmentId + " is missing");
                                        }
                                        foreach (var item in new[] { a.LeftHandItemId, a.RightHandItemId }.Where(i => !String.IsNullOrEmpty(i)))
                                        {
                                            items++;
                                            string id = item.StartsWith("Item.", StringComparison.Ordinal) ? item.Substring(5) : item;
                                            if (objects.GetObject<ItemObject>(id) == null) problems.Add(culture.StringId + ": " + menu.StringId + " (" + origin.StringId + ", " + title + ") item " + item + " is missing");
                                        }
                                    }
                                }
                        }
                        if (titles.Count == 0) problems.Add(culture.StringId + ": no option sets a title type");
                        if (characters == 0) problems.Add(culture.StringId + ": no narrative menu shows a character");
                        if (culture.DefaultCharacterCreationBodyProperty == null) problems.Add(culture.StringId + ": no default character creation body property");
                    }
                    catch (Exception error) { problems.Add(culture.StringId + ": audit threw " + error.GetType().Name + ": " + error.Message); }
                    finally { try { current(first); } catch (Exception) { } }
                    cultures.Add(new Dictionary<string, object> {
                        ["culture"] = culture.StringId, ["origins"] = origins, ["titleTypes"] = titles.ToList(), ["selections"] = selections, ["characters"] = characters,
                        ["equipmentRosters"] = rosters, ["handItems"] = items,
                        ["bodyProperty"] = culture.DefaultCharacterCreationBodyProperty == null ? null : culture.DefaultCharacterCreationBodyProperty.StringId });
                }
            }
            catch (Exception error) { problems.Add("audit threw " + error.GetType().Name + ": " + error.Message); }
            string restored = null;
            try { if (selected != null) content.SetSelectedCulture(selected, manager); restored = content.SelectedCulture == null ? null : content.SelectedCulture.StringId; }
            catch (Exception error) { problems.Add("restoring the culture threw " + error.GetType().Name + ": " + error.Message); }
            if (selected != null && restored != selected.StringId) problems.Add("the probe culture " + selected.StringId + " was not restored (" + restored + ")");
            return new Dictionary<string, object> { ["cultures"] = cultures, ["problems"] = problems, ["firstMenu"] = first == null ? null : first.StringId,
                                                    ["laterMenus"] = later.Select(m => m.StringId).ToList(), ["restoredCulture"] = restored };
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
