namespace MNBL.ProceduralWorld.UI
{
    // The offline setup harness (tools/Test-WorldSetup.ps1) has no engine: this stands in for UI/WorldPreviewTexture.cs's engine texture
    // (the module build compiles the real one; the world_setup and new_campaign_flow probes exercise it in the game). Same members the view
    // models use; Show succeeds unless Refuse is set.
    internal static class WorldPreviewTextures
    {
        public const string ModeBytes = "bytes", ModeFile = "file";
        public static bool Refuse;
        public static object Current { get; private set; }
        public static string RasterSha256 { get; private set; }
        public static string Mode { get; private set; }
        public static string LastError { get; private set; }
        public static int Created { get; private set; }

        public static bool Show(WorldPreview preview) { return Show(preview, ModeBytes); }
        public static bool Show(WorldPreview preview, string mode)
        {
            if (preview == null) { Clear(); return false; }
            if (Refuse) { Current = null; RasterSha256 = null; LastError = "refused (test)"; return false; }
            if (Current != null && RasterSha256 == preview.RasterSha256 && Mode == mode) return true;
            Current = new object(); RasterSha256 = preview.RasterSha256; Mode = mode; LastError = null; Created++;
            return true;
        }

        public static void Clear() { Current = null; RasterSha256 = null; }
    }
}
