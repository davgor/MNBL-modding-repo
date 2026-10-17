using System;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-46: the game manager of a generated campaign the player starts (the setup screen's Start Campaign, through MMR-45's activation). It
    // is GeneratedGameManager's seams with the installed new-game flow: SandBoxGameManager.OnLoadFinished plays the campaign intro and opens
    // character creation (LaunchSandboxCharacterCreation), which the player drives; the world's cultures are the only ones offered
    // (GeneratedCharacterCreation, MMR-32), on every stage and after going back. Leaving character creation through its first stage's Back
    // ends the game before the campaign map: the activation reads that as the player withdrawing (Progress "withdrawn"), rolls back, and the
    // generation job returns to review with its world.
    internal sealed class GeneratedCampaignManager : GeneratedGameManager
    {
        private Game ownedGame;
        private bool mapReached;
        private DateTime startedUtc;

        private GeneratedCampaignManager(TaleWorlds.CampaignSystem.Map.IMapSceneCreator creator, Action<string> trace) : base(creator, trace) { }

        // Engine thread, at the main menu.
        public static GeneratedCampaignManager Start(ActivationStart start)
        {
            if (Game.Current != null) throw new InvalidOperationException("Return to the main menu first.");
            if (start == null || start.World == null || start.SupportReport == null || start.Trace == null) throw new ArgumentNullException("start");
            GeneratedMapSceneCreator.ValidateFixture(start.SceneName);
            if (start.Periodic != null) PeriodicPatches.Preflight(start.Periodic);
            var manager = new GeneratedCampaignManager(new GeneratedMapSceneCreator(start.SceneName, start.Trace, start.World.WorldId, start.SupportReport, start.Periodic, start.Climate), start.Trace) {
                World = start.World, Periodic = start.Periodic, CreationCultures = start.CreationCultures, startedUtc = DateTime.UtcNow };
            MBGameManager.StartNewGame(manager);
            return manager;
        }

        // Engine thread: null while the game loads or character creation runs, "map" once the campaign map runs, "withdrawn" when the game
        // ended before the map (the player left character creation), else why it failed.
        public static string Progress(GeneratedGameManager manager)
        {
            var m = manager as GeneratedCampaignManager;
            if (m == null) return "no game manager";
            if (m.ownedGame != null && Game.Current == m.ownedGame && Game.Current.GameStateManager.ActiveState is MapState) { m.mapReached = true; return "map"; }
            if (m.ownedGame != null && Game.Current != m.ownedGame && TaleWorlds.MountAndBlade.MBGameManager.Current == null) return m.mapReached ? "failed: the game ended after the campaign map" : "withdrawn";
            // No game of this manager initialized and none is loading: the start ended before the game existed (review finding: it waited out
            // the whole campaign timeout).
            if (m.ownedGame == null && Game.Current == null && TaleWorlds.MountAndBlade.MBGameManager.Current != m && (DateTime.UtcNow - m.startedUtc).TotalSeconds > 30)
                return "failed: the new game ended before it initialized";
            return null;
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game);   // SandBox's own, then the generated map-scene creator
            ownedGame = game;
        }

        // The installed new-game branch: the campaign intro, then character creation the player drives.
        public override void OnLoadFinished()
        {
            Traced("OnLoadFinished", () => base.OnLoadFinished());
        }
    }
}
