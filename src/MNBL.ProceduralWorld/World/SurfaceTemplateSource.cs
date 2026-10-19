using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.World
{
    // MMR-57: the surface template the encoder starts from, made on the player's machine. Two of its five files are this module's own
    // (SurfaceTemplate/terrain.bin and navmesh.bin, written by tools/scenebin/build_slot.py's writers); flora.bin is the empty flora
    // file (its 12-byte header, written here: the same bytes as every official empty flora file, so it is not shipped). scene.xscene and
    // atmosphere.xml come from the installed official battle scene (SandBoxCore battle_terrain_001), so the module never ships them: they
    // are derived here from the player's own installation with the values SurfaceTemplate/template-recipe.json records (written and proved by
    // tools/scenebin/make_surface_template.py). Every file must have the recipe's SHA-256, so the template, and with it every scene name, is
    // the same on every installation of this game version; another official scene refuses with its reason. The template is kept in the
    // per-user store (writable even when the installation is not), one folder per recipe.
    internal static class SurfaceTemplateSource
    {
        private static readonly string[] Ours = { "terrain.bin", "navmesh.bin" };
        // The empty flora file as build_slot.py writes it: "FLR2", version 4, no entries (little-endian).
        private static readonly byte[] EmptyFlora = { (byte)'F', (byte)'L', (byte)'R', (byte)'2', 4, 0, 0, 0, 0, 0, 0, 0 };
        // Whether this process derived the template (rather than finding it complete in the store): MMR-57's clean install records it.
        public static bool CreatedThisProcess { get; private set; }
        private static readonly string[] TerrainAttributes = { "node_dimension_x", "node_dimension_y", "node_size", "min_height", "max_height", "vista_tileset" };

        public static string Ensure()
        {
            string module = MNBL.ProceduralWorld.Surface.RuntimeWorldBackend.ModuleRoot();
            string packaged = Path.Combine(module, "SurfaceTemplate"), recipePath = Path.Combine(packaged, "template-recipe.json");
            if (!File.Exists(recipePath)) throw new FileNotFoundException("The module's surface template recipe is missing: SurfaceTemplate/template-recipe.json.", recipePath);
            var recipe = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(recipePath, Encoding.UTF8));
            var expected = ((IDictionary<string, object>)recipe["expected"]).ToDictionary(kv => kv.Key, kv => (string)kv.Value, StringComparer.Ordinal);
            string store = Path.Combine(SubModule.Store, "surface-template"), target = Path.Combine(store, expected["scene.xscene"].Substring(0, 16));
            // The module's own files are checked on every call, also when the template is already in the store.
            var ours = Ours.ToDictionary(f => f, f => Read(Path.Combine(packaged, f)), StringComparer.Ordinal);
            foreach (var kv in ours) if (Sha(kv.Value) != expected[kv.Key]) throw new InvalidDataException("The module's SurfaceTemplate/" + kv.Key + " differs from its recipe.");
            if (Complete(target, expected)) return target;
            // Staging folders a stopped process left behind.
            if (Directory.Exists(store)) foreach (var stale in Directory.GetDirectories(store, "*.staging-*")) { try { Directory.Delete(stale, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            var official = (IDictionary<string, object>)recipe["official"];
            string officialDir = OfficialFolder((string)official["scene"]);
            byte[] scene = Read(Path.Combine(officialDir, "scene.xscene")), atmosphere = Read(Path.Combine(officialDir, "atmosphere.xml"));
            if (Sha(scene) != (string)official["sceneXsceneSha256"] || Sha(atmosphere) != (string)official["atmosphereXmlSha256"])
                throw new InvalidDataException("The installed " + official["scene"] + " is not the official scene this module's surface template derives from (another game version, or a modified installation).");
            byte[] derived = Derive(scene, (IDictionary<string, object>)recipe["substitutions"]);
            if (Sha(derived) != expected["scene.xscene"] || Sha(atmosphere) != expected["atmosphere.xml"] || Sha(EmptyFlora) != expected["flora.bin"])
                throw new InvalidDataException("The surface template derived from " + official["scene"] + " differs from the recipe's.");
            // Written beside the target and moved into place whole: a template is either complete or absent.
            string staging = target + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                File.WriteAllBytes(Path.Combine(staging, "scene.xscene"), derived);
                File.WriteAllBytes(Path.Combine(staging, "atmosphere.xml"), atmosphere);
                File.WriteAllBytes(Path.Combine(staging, "flora.bin"), EmptyFlora);
                foreach (var kv in ours) File.WriteAllBytes(Path.Combine(staging, kv.Key), kv.Value);
                if (Directory.Exists(target)) Directory.Delete(target, true);   // an incomplete or altered earlier one
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            if (!Complete(target, expected)) throw new IOException("The surface template could not be written to " + target + ".");
            CreatedThisProcess = true;
            return target;
        }

        // "Modules/<id>/<rest>" resolved through the game's own module lookup (an official module is in the installation's Modules folder,
        // wherever this module is installed: the game's Modules or a Workshop folder); any other path from the installation's root.
        private static string OfficialFolder(string path)
        {
            var parts = path.Split('/');
            if (parts.Length > 2 && parts[0] == "Modules")
            {
                string root = TaleWorlds.ModuleManager.ModuleHelper.GetModuleFullPath(parts[1]);
                if (!String.IsNullOrEmpty(root)) return Path.GetFullPath(Path.Combine(root, String.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(2))));
            }
            string game = Path.GetFullPath(Path.Combine(TaleWorlds.Library.BasePath.Name));
            return Path.GetFullPath(Path.Combine(game, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool Complete(string dir, IDictionary<string, string> expected)
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var kv in expected) { string p = Path.Combine(dir, kv.Key); if (!File.Exists(p) || Sha(Read(p)) != kv.Value) return false; }
            return true;
        }

        private static byte[] Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The surface template needs " + path + ".", path);
            return File.ReadAllBytes(path);
        }

        private static string Sha(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        // The official scene's text with this module's values, step by step as tools/scenebin/make_surface_template.py derives it (and
        // build_slot.py built it): line ends normalised to LF; the scene's name; its entities emptied; the flora and physics bounds; the
        // terrain's node grid and heights; the terrain nodes block. Each step must apply exactly once.
        internal static byte[] Derive(byte[] official, IDictionary<string, object> s)
        {
            string text = new UTF8Encoding(false).GetString(official).Replace("\r\n", "\n");
            text = Once(text, "<scene name=\"[^\"]*\"", RegexOptions.None, m => "<scene name=\"" + (string)s["sceneName"] + "\"");
            text = Once(text, "<entities>.*?</entities>", RegexOptions.Singleline, m => "<entities>\n\t</entities>");
            text = Once(text, "<flora_bounding_rect[^>]*/>", RegexOptions.None, m => (string)s["floraBoundingRect"]);
            text = Once(text, "(<property name=\"physics_world_min\" value=\")[^\"]*\"", RegexOptions.None, m => m.Groups[1].Value + (string)s["physicsWorldMin"] + "\"");
            text = Once(text, "(<property name=\"physics_world_max_\" value=\")[^\"]*\"", RegexOptions.None, m => m.Groups[1].Value + (string)s["physicsWorldMax"] + "\"");
            var terrain = (IDictionary<string, object>)s["terrain"];
            text = Once(text, "<terrain [^>]*>", RegexOptions.None, m =>
            {
                string tag = m.Value;
                foreach (var name in TerrainAttributes)
                {
                    var attribute = new Regex(Regex.Escape(name) + "=\"[^\"]*\"", RegexOptions.CultureInvariant);
                    if (attribute.Matches(tag).Count != 1) throw new InvalidDataException("The official <terrain> lacks " + name + ".");
                    tag = attribute.Replace(tag, x => name + "=\"" + (string)terrain[name] + "\"");
                }
                return tag;
            });
            text = Once(text, "\\t*<nodes>.*?</nodes>", RegexOptions.Singleline, m => (string)s["nodes"]);
            return new UTF8Encoding(false).GetBytes(text);
        }

        private static string Once(string text, string pattern, RegexOptions options, MatchEvaluator replace)
        {
            var regex = new Regex(pattern, options | RegexOptions.CultureInvariant);
            if (!regex.IsMatch(text)) throw new InvalidDataException("The official scene has no " + pattern + ".");
            return regex.Replace(text, replace, 1);
        }
    }
}
