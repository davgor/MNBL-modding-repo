using System;
using System.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-49: the installed statics a generated campaign holds, registered with WorldLifetimeScope once per process (SubModule). Each counts
    // what is held; the baseline, at the main menu with no campaign, is zero for all of them.
    internal static class WorldLifetimeTrackers
    {
        private static bool registered;

        public static void Register()
        {
            if (registered) return;
            registered = true;
            WorldLifetimeScope.Track("patches: campaign and map Harmony patches", () => GeneratedPatches.Patched().Count(p => !MNBL.ProceduralWorld.Persistence.LoadRouting.IsProcessLifetime(p)));
            WorldLifetimeScope.Track("map: fixture settlement prefabs", () => GeneratedMapScene.FixturePrefabs.Count);
            WorldLifetimeScope.Track("map: runtime settlement entities", () => GeneratedMapScene.RuntimeSettlementEntities.Count);
            WorldLifetimeScope.Track("map: missing baked entities", () => GeneratedMapScene.MissingBakedEntities.Count);
            WorldLifetimeScope.Track("map: siege prefab cache source", () => GeneratedMapScene.SiegePrefabCacheSource == null ? 0 : 1);
            WorldLifetimeScope.Track("map: live campaign map generation", () => WorldGenerations.LiveCampaignMap == 0 ? 0 : 1);
            WorldLifetimeScope.Track("world generation: current token", () => WorldGenerations.Current == 0 ? 0 : 1);
            WorldLifetimeScope.Track("battle: terrain contexts", () => GeneratedBattleTerrain.ContextSources.Count);
            WorldLifetimeScope.Track("battle: mission scene observations", () => MNBL.ProceduralWorld.Missions.MissionScenePatches.Observed);
            WorldLifetimeScope.Track("periodic: current frame", () => PeriodicWorld.Current == null ? 0 : 1);
            WorldLifetimeScope.Track("periodic: patches applied", () => PeriodicPatches.IsApplied ? 1 : 0);
            WorldLifetimeScope.Track("climate: colour grade applied", () => GeneratedColorGrade.Applied ? 1 : 0);
            WorldLifetimeScope.Track("naval: map scene wrapper", () => GeneratedNavalMapSceneWrapper.Current == null ? 0 : 1);
            WorldLifetimeScope.Track("saves: binding attached", () => MNBL.ProceduralWorld.Persistence.WorldSaveBinding.Attached ? 1 : 0);
            WorldLifetimeScope.Track("saves: activation save block", () => MNBL.ProceduralWorld.Generation.ActivationSaveBlock.Active ? 1 : 0);
            WorldLifetimeScope.Track("startup: behaviour active", () => GeneratedStartupBehavior.Active == null ? 0 : 1);
            // The last teardown's own measurement: the module's behaviours' listeners still on the ended campaign's events after it removed
            // them (non-zero when a removal failed), and whether the teardown itself failed.
            WorldLifetimeScope.Track("campaign events: module listeners left", () => GeneratedGameManager.LastTeardown == null ? 0 : GeneratedGameManager.LastTeardown.OwnListenersLeft);
            WorldLifetimeScope.Track("campaign events: teardown failed", () => GeneratedGameManager.LastTeardown == null || GeneratedGameManager.LastTeardown.Error == null ? 0 : 1);
            WorldLifetimeScope.Track("load: in progress", () => MNBL.ProceduralWorld.Persistence.GeneratedWorldLoader.Busy ? 1 : 0);
            WorldLifetimeScope.Track("load: loader holds a game", () => MNBL.ProceduralWorld.Persistence.GeneratedWorldLoader.HoldsGame ? 1 : 0);
        }
    }
}
