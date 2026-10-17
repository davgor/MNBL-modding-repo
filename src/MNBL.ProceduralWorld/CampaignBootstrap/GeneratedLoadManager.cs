using System;
using System.Collections.Generic;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-49: the game manager of a generated campaign the player loads (GeneratedWorldLoader, from the normal Load and Continue entries). It is
    // GeneratedGameManager's seams in load mode over the installed saved-game flow: the world's registration replays its documents in
    // RegisterSubModuleObjects(true) after the deserialized objects re-registered, the generated map-scene creator reads the world's scene, the
    // navigation caches and the battle terrain install after the map, then SandBoxGameManager.OnLoadFinished takes the installed saved branch
    // (the map state, the menu the save was in, OnGameLoadFinished). The SandBox save manager stays: autosave runs as in any campaign.
    internal sealed class GeneratedLoadManager : GeneratedGameManager
    {
        internal sealed class LoadStart
        {
            public IGeneratedWorld World;
            public string SceneName;
            public Dictionary<string, object> SupportReport;
            public Action<string> Trace;
            public PeriodicWorld Periodic;
            public GeneratedClimate Climate;
            public TaleWorlds.SaveSystem.Load.LoadResult Saved;
            public Dictionary<string, string> Identity;   // the header's mnbl.world.* entries, without the prefix
        }

        private Game ownedGame;
        private bool mapReached, gameEnded;
        private DateTime startedUtc;

        private GeneratedLoadManager(TaleWorlds.CampaignSystem.Map.IMapSceneCreator creator, TaleWorlds.SaveSystem.Load.LoadResult saved, Action<string> trace) : base(creator, saved, trace) { }

        // Engine thread, at the main menu.
        public static GeneratedLoadManager Start(LoadStart start)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the main menu first.");
            if (start == null || start.World == null || start.SupportReport == null || start.Trace == null || start.Identity == null) throw new ArgumentNullException("start");
            if (start.Saved == null || !start.Saved.Successful) throw new ArgumentException("A successfully deserialized save is required.");
            GeneratedMapSceneCreator.ValidateFixture(start.SceneName);
            if (start.Periodic != null) PeriodicPatches.Preflight(start.Periodic);   // refuse before the game starts when a periodic patch target refuses
            var manager = new GeneratedLoadManager(new GeneratedMapSceneCreator(start.SceneName, start.Trace, start.World.WorldId, start.SupportReport, start.Periodic, start.Climate), start.Saved, start.Trace) {
                World = start.World, Periodic = start.Periodic, IdentityEntries = start.Identity, startedUtc = DateTime.UtcNow };
            // As the installed load does (SavedGameVM.StartGame): the active save slot and the loaded game version.
            TaleWorlds.Core.MBSaveLoad.OnStartGame(start.Saved);
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        // Engine thread: null while the save loads, "map" once the campaign map runs, else why the load failed.
        public static string Progress(GeneratedLoadManager manager)
        {
            if (manager == null) return "no game manager";
            if (manager.ownedGame != null && Game.Current == manager.ownedGame && Game.Current.GameStateManager.ActiveState is MapState) { manager.mapReached = true; return "map"; }
            if (manager.gameEnded || (manager.ownedGame != null && Game.Current != manager.ownedGame && MBGameManager.Current == null))
                return manager.mapReached ? "failed: the game ended after the campaign map" : "failed: the game ended before the campaign map";
            if (manager.ownedGame == null && Game.Current == null && MBGameManager.Current != manager && (DateTime.UtcNow - manager.startedUtc).TotalSeconds > 30)
                return "failed: the load ended before the game initialized";
            return null;
        }

        // Engine thread: the running game is the one this manager started.
        public bool OwnsCurrentGame { get { return ownedGame != null && Game.Current == ownedGame; } }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game);   // SandBox's own, then the generated map-scene creator
            ownedGame = game;
        }

        // An ended game is not kept reachable through its manager (a loader that is no longer current may still hold the manager).
        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            if (ownedGame == game) { ownedGame = null; gameEnded = true; }
        }

        // The installed saved-game branch.
        public override void OnLoadFinished()
        {
            Traced("OnLoadFinished", () => base.OnLoadFinished());
        }
    }
}
