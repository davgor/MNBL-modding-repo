using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.Engine;
using MNBL.ProceduralWorld.Surface;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-256: the terrain's seasonal snow on a generated world (worldgen/policies/climate-flow.json precipitation.snowTexture,
    // docs/water-climate.md). MMR-249 found that the terrain snows only from the texture its scene names (terrain attribute
    // dynamic_flowmap_texture_name, with use_dynamic_weather_effects="true"), resolved when the scene is read; Scene.SetDynamicSnowTexture has
    // no effect after Read and hangs the engine before it, so it is never called. So on the namedTexture route:
    //   * the world's snow raster (GeneratedClimateSystem: R snow byte, G rain byte; 512 x 512, row 0 at physical y = extent) is named by its
    //     hash, as its PNG's file name ("mnbl_snow_" + 16 hex digits + ".png"); RuntimeWorldBackend.Prepare writes that name into the scene's
    //     terrain (part of the scene name) and the raster as <store>/world-snow/<name> (Ensure; the campaign map's load writes it again from
    //     its climate if it is gone);
    //   * before every Read of a scene that names one (the campaign map and inspection worlds), Acquire loads the PNG
    //     (Texture.LoadTextureFromPath, as the world preview's file mode), keeps it valid and preloaded, and records the engine's name for it
    //     and whether the engine's resource lookup resolves the scene's name (Texture.CheckAndGetFromResource). The record is the map load's
    //     log row (GeneratedMapScene traces it). MMR-247: the engine names a path-loaded texture by its file name with the extension (acc4:
    //     engineName "mnbl_snow_6842edaae0521518.png"), and renaming it (Texture.Name, MMR-256's first route) did not make the scene's
    //     extension-less name resolve ("Cannot find texture" at every map read), so the scene names the file and the texture is never renamed;
    //   * a texture is shared by name and counted: the last world that holds it releases it (ReleaseAfterNumberOfFrames), when that world
    //     is disposed. The engine may keep one texture per name, so releasing one world's copy while another reads it is avoided.
    // Whether the terrain draws a texture loaded this way is unproven in game (MMR-247 checks it). The policy's permanentWhite route is the
    // documented fallback: no name in the scene, nothing loaded; GeneratedMapAtmosphere still drives the season threshold, and only the
    // permanent-white layer (land ice, 12-month snow) is white. Engine thread only (Acquire, Release); Ensure is file work, any thread.
    internal static class GeneratedSnowTexture
    {
        private sealed class Held { public Texture Texture; public int Count; public string EngineName; public bool Resolves; }
        private static readonly Dictionary<string, Held> held = new Dictionary<string, Held>(StringComparer.Ordinal);
        public static int Loaded { get; private set; }
        public static int Released { get; private set; }
        public static int Holding { get { lock (held) return held.Count; } }

        public static string Folder() { return Path.Combine(SubModule.Store, "world-snow"); }
        public static string PathOf(string name)
        {
            if (!GeneratedClimateSystem.IsSnowTextureName(name)) throw new ArgumentException("Not a snow texture name: " + name);
            return Path.Combine(Folder(), name);
        }

        // The raster's PNG under its name, unless already there (the name is the raster's hash). Returns the path.
        public static string Ensure(string name, byte[] rgba)
        {
            if (String.IsNullOrEmpty(name) || rgba == null) throw new ArgumentNullException();
            if (GeneratedClimateSystem.SnowTextureName(GeneratedClimateSystem.Sha(rgba)) != name) throw new InvalidDataException("The snow texture " + name + " is not named by its pixels.");
            string path = PathOf(name);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            Directory.CreateDirectory(Folder());
            MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllBytes(path, MNBL.ProceduralWorld.UI.WorldPreview.EncodePng(rgba, GeneratedClimateSystem.SnowTextureDimension, GeneratedClimateSystem.SnowTextureDimension));
            return path;
        }

        // The snow texture an installed scene's terrain names (null when it names none: the permanentWhite route, or a fixture).
        public static string NamedBy(string sceneName)
        {
            string xml = Path.Combine(RuntimeWorldBackend.SceneFolder(sceneName), "scene.xscene");
            return File.Exists(xml) ? GeneratedClimateSystem.SnowTextureOf(File.ReadAllText(xml)) : null;
        }

        // Engine thread, before the scene's Read: loads and names the texture the scene names, held by the world's ledger. Never throws for
        // a texture that cannot be loaded (the record says why; the scene then reads without it).
        public static Dictionary<string, object> Acquire(string sceneName, ResourceLedger ledger)
        {
            EngineThread.Require("snow texture load");
            var record = new Dictionary<string, object> { ["scene"] = sceneName };
            string name;
            try { name = NamedBy(sceneName); }
            catch (Exception error) { record["status"] = "unreadable scene: " + error.GetType().Name + ": " + error.Message; return record; }
            record["name"] = name;
            if (name == null) { record["status"] = "none: the scene names no snow texture (" + GeneratedClimateSystem.Policy.PermanentWhite + " route or a fixture)"; return record; }
            if (!GeneratedClimateSystem.IsSnowTextureName(name)) { record["status"] = "refused: the scene names " + name + ", not a snow texture's file name (a scene from before MMR-247)"; return record; }
            Held h;
            lock (held)
            {
                if (!held.TryGetValue(name, out h))
                {
                    string path = PathOf(name);
                    if (!File.Exists(path)) { record["status"] = "missing: " + path; return record; }
                    try
                    {
                        var texture = Texture.LoadTextureFromPath(name, Folder());
                        if (texture == null) { record["status"] = "refused: the engine made no texture from " + path; return record; }
                        string engineName = texture.Name;   // never renamed: the scene names the file (MMR-247)
                        texture.SetTextureAsAlwaysValid();
                        texture.PreloadTexture(true);
                        h = new Held { Texture = texture, EngineName = engineName, Resolves = Texture.CheckAndGetFromResource(name) != null };
                        held[name] = h; Loaded++;
                    }
                    catch (Exception error) { record["status"] = "failed: " + error.GetType().Name + ": " + error.Message; return record; }
                }
                h.Count++;
            }
            record["status"] = "loaded"; record["engineName"] = h.EngineName; record["named"] = h.Texture.Name; record["engineNameIsScenes"] = h.EngineName == name; record["resolvesByName"] = h.Resolves;
            record["width"] = h.Texture.Width; record["height"] = h.Texture.Height; record["holders"] = h.Count;
            ledger.Acquire("snow texture " + name, "GeneratedSnowTexture (shared by name, counted)", () => Release(name));
            return record;
        }

        private static void Release(string name)
        {
            Held h;
            lock (held)
            {
                if (!held.TryGetValue(name, out h)) return;
                if (--h.Count > 0) return;
                held.Remove(name);
            }
            h.Texture.ReleaseAfterNumberOfFrames(3);
            Released++;
        }
    }
}
