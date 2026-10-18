using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-50: the read-only compatibility report of one save in the running game (engine thread): WorldCompatibilityPolicy over the game's
    // save folder, this module's store and scene store, this host's bundle profile and the running build (WorldSaveBinding.Compatibility).
    // Reads only; the load itself (GeneratedWorldLoader) decides by the same rules.
    internal static class WorldCompatibilityReport
    {
        public static WorldCompatibilityPolicy.Decision ForSave(string saveName)
        {
            return WorldCompatibilityPolicy.Evaluate(WorldCompatibilityPolicy.Gather(BannerlordSaveCatalog.SavePath(saveName), SubModule.Store, RuntimeWorldBackend.SceneStore(),
                UI.WorldSetupHost.BundleHost(), WorldSaveBinding.Compatibility()));
        }
    }
}
