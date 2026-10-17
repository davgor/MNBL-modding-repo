using System;
using System.IO;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.TwoDimension;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-46: the world preview's raster as a Gauntlet texture. Engine thread only. The engine texture is made from the preview's own pixels,
    // either from the raw RGBA bytes (Texture.CreateFromByteArray) or from the PNG the preview writes (Texture.LoadTextureFromPath), and
    // wrapped as the installed providers wrap theirs (SetTextureAsAlwaysValid, PreloadTexture, EngineTexture, TwoDimension.Texture). The
    // WorldSetup prefab's TextureWidget names WorldPreviewTextureProvider, which serves the current one.
    internal static class WorldPreviewTextures
    {
        public const string ModeBytes = "bytes", ModeFile = "file";
        public static Texture Current { get; private set; }
        public static string RasterSha256 { get; private set; }
        public static string Mode { get; private set; }
        public static string LastError { get; private set; }
        public static int Created { get; private set; }
        public static int Released { get; private set; }
        private static TaleWorlds.Engine.Texture engineTexture;

        // Shows the preview's raster (once per raster); returns false when the engine refused it (LastError says why). The raw bytes are the
        // default (run 43992 showed both modes render alike; no file is written); the PNG file is tried when the bytes are refused.
        public static bool Show(WorldPreview preview)
        {
            if (Show(preview, ModeBytes)) return true;
            string bytesError = LastError;
            if (Show(preview, ModeFile)) return true;
            LastError = "bytes: " + bytesError + "; file: " + LastError;
            return false;
        }

        public static bool Show(WorldPreview preview, string mode)
        {
            if (preview == null) { Clear(); return false; }
            if (Current != null && RasterSha256 == preview.RasterSha256 && Mode == mode) return true;
            try
            {
                TaleWorlds.Engine.Texture engine;
                if (mode == ModeBytes) engine = TaleWorlds.Engine.Texture.CreateFromByteArray(preview.Rgba, preview.Width, preview.Height);
                else
                {
                    string folder = Path.Combine(SubModule.Store, "previews");
                    Directory.CreateDirectory(folder);
                    string name = "preview-" + preview.RasterSha256.Substring(0, 16) + ".png";
                    string path = Path.Combine(folder, name);
                    if (!File.Exists(path)) MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllBytes(path, preview.Png());
                    // Only the shown preview's file is kept.
                    foreach (var old in Directory.GetFiles(folder, "preview-*.png"))
                        if (!String.Equals(Path.GetFileName(old), name, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    engine = TaleWorlds.Engine.Texture.LoadTextureFromPath(name, folder);
                }
                if (engine == null) throw new InvalidOperationException("the engine made no texture (" + mode + ")");
                engine.SetTextureAsAlwaysValid();
                engine.PreloadTexture(true);
                ReleaseEngineTexture();
                engineTexture = engine;
                Current = new Texture(new EngineTexture(engine));
                RasterSha256 = preview.RasterSha256; Mode = mode; LastError = null; Created++;
                return true;
            }
            catch (Exception error) { LastError = error.GetType().Name + ": " + error.Message; ReleaseEngineTexture(); Current = null; RasterSha256 = null; return false; }
        }

        // The engine texture is released a few frames later (a frame being drawn may still use it).
        public static void Clear() { ReleaseEngineTexture(); Current = null; RasterSha256 = null; }
        private static void ReleaseEngineTexture()
        {
            var old = engineTexture; engineTexture = null;
            if (old == null) return;
            try { old.ReleaseAfterNumberOfFrames(3); Released++; } catch (Exception) { }
        }
    }

    // Found by name by Gauntlet's TextureProviderFactory (RefreshProviderTypes scans the loaded assemblies; the setup screen refreshes it).
    public sealed class WorldPreviewTextureProvider : TaleWorlds.GauntletUI.TextureProvider
    {
        protected override Texture OnGetTextureForRender(TwoDimensionContext twoDimensionContext, string name) { return WorldPreviewTextures.Current; }
    }
}
