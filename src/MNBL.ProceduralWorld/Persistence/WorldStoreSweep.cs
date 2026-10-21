using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-282: which generated scenes the world store keeps. Before it, generation swept every content-addressed scene folder this process
    // had not used, with its settlement report and navigation caches: the next load of such a campaign re-encoded its scene (about 15 s) and
    // rebuilt its caches (about 6.4 min), and after any WorldBridge change the re-encoding had another name and the load was refused for
    // good. Now a scene is referenced, and kept with everything derived from it, while any of these names it:
    //   * a world package in the package store (<store>/worlds/<id>-<manifest12>/world.json sceneName): every started campaign has one;
    //   * a bound save, a pending initial save or a save in flight (WorldReferenceIndex: the header's mnbl.world.scene, and its package's);
    //   * this process (the world being reviewed, the campaign being played or loaded).
    // Discovery fails closed: a save header, a journal, the save list or a package manifest that cannot be read might name any scene, so
    // while there is a gap nothing referenced-or-not is deleted (only staging debris goes). Pure managed: tested offline (Test-WorldStore).
    internal sealed class WorldSceneReferences
    {
        public readonly SortedDictionary<string, List<string>> Scenes = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly List<string> Gaps = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int Packages { get; private set; }
        public bool Complete { get { return Gaps.Count == 0; } }
        public bool Referenced(string scene) { return scene != null && Scenes.ContainsKey(scene); }
        public List<string> Why(string scene) { List<string> why; return scene != null && Scenes.TryGetValue(scene, out why) ? why : new List<string>(); }

        // index: the saves and journals (null: a gap); packageRoot: <store>/worlds; inUse: the scenes this process uses.
        public static WorldSceneReferences Discover(WorldReferenceIndex index, string packageRoot, IEnumerable<string> inUse)
        {
            var r = new WorldSceneReferences();
            var packageScenes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (packageRoot != null && Directory.Exists(packageRoot))
                    foreach (var dir in Directory.GetDirectories(packageRoot).OrderBy(d => d, StringComparer.Ordinal))
                    {
                        string name = Path.GetFileName(dir);
                        if (DurableFile.IsTemporary(name)) continue;   // a package write that never finished (GeneratedWorldPackage.Write's staging)
                        string scene;
                        try { scene = PackageScene(Path.Combine(dir, "world.json")); }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException || error is InvalidDataException)
                        {
                            r.Gaps.Add("package " + name + ": its world.json cannot be read (" + error.GetType().Name + ": " + error.Message + "), so the scene it names is unknown");
                            continue;
                        }
                        r.Packages++;
                        packageScenes[name] = scene;
                        r.Add(scene, "world package " + name);
                    }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                r.Gaps.Add("the package store cannot be listed (" + error.GetType().Name + ": " + error.Message + ")");
            }
            if (index == null) r.Gaps.Add("the saves were not read, so a save may name any scene");
            else
            {
                r.Gaps.AddRange(index.Gaps);
                foreach (var reference in index.References)
                {
                    string label = reference.Kind + " " + reference.Name;
                    if (reference.Scene != null) r.Add(reference.Scene, label);
                    string scene;
                    if (reference.Package != null && packageScenes.TryGetValue(reference.Package, out scene)) r.Add(scene, label + " (through its package)");
                    else if (reference.Scene == null) r.Notes.Add(label + " names package " + reference.Package + ", which is not in the package store, and no scene");
                }
            }
            foreach (var scene in inUse ?? Enumerable.Empty<string>()) r.Add(scene, "in use in this process");
            return r;
        }

        private static string PackageScene(string worldJson)
        {
            var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(worldJson)) as IDictionary<string, object>;
            object scene;
            if (manifest == null || !manifest.TryGetValue("sceneName", out scene) || !(scene is string) || ((string)scene).Length == 0) throw new InvalidDataException("no sceneName");
            return (string)scene;
        }

        private void Add(string scene, string why)
        {
            if (String.IsNullOrEmpty(scene)) return;
            List<string> list;
            if (!Scenes.TryGetValue(scene, out list)) Scenes[scene] = list = new List<string>();
            if (!list.Contains(why)) list.Add(why);
        }

        public Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { ["complete"] = Complete, ["packages"] = Packages, ["gaps"] = Gaps.ToList(), ["notes"] = Notes.ToList(),
                ["scenes"] = Scenes.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToList()) };
        }
    }

    // Where a world's derived resources live. SceneStore is the module's SceneObj folder: the only place the engine reads scenes from, and a
    // module update (Workshop or manual) replaces the module folder with everything in it. SceneArchive (<store>/world-scenes) holds a copy of
    // every installed world scene outside the module, so a module update costs a copy instead of a re-encoding (WorldSceneArchive).
    // SettlementReports and NavigationCaches are already under the per-user store (%LOCALAPPDATA%\MNBL.ProceduralWorld).
    internal sealed class WorldStoreLayout
    {
        public string SceneStore, SceneArchive, SettlementReports, NavigationCaches, ReportPath;
    }

    internal static class WorldStoreSweep
    {
        // Deletes the world scenes (installed and archived), settlement reports and navigation caches no reference names, and keeps the rest,
        // recording what it kept and why. With an incomplete discovery it deletes only staging debris (never a reference).
        public static Dictionary<string, object> Run(WorldStoreLayout layout, WorldSceneReferences references, Func<string, bool> isWorldScene)
        {
            var swept = new List<string>(); var archived = new List<string>(); var reports = new List<string>(); var caches = new List<string>();
            var retained = new List<string>(); var kept = new SortedDictionary<string, object>(StringComparer.Ordinal); var debris = new List<string>();
            Action<string, List<string>, string> removeDir = (path, into, label) =>
            {
                try { Directory.Delete(path, true); into.Add(label); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { retained.Add(label + ": " + error.GetType().Name); }
            };
            // Staging debris is never a reference: the caches' staging, discard and verify folders and the archive's staging.
            foreach (var dir in Dirs(layout.NavigationCaches))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("staging-", StringComparison.Ordinal) || name.StartsWith("discard-", StringComparison.Ordinal) || name.StartsWith("verify-", StringComparison.Ordinal))
                    removeDir(dir, debris, "navigation-caches/" + name);
            }
            foreach (var dir in Dirs(layout.SceneArchive))
                if (Path.GetFileName(dir).StartsWith(WorldSceneArchive.StagingPrefix, StringComparison.Ordinal)) removeDir(dir, debris, "world-scenes/" + Path.GetFileName(dir));
            // Refused layouts are evidence of a world that never got a scene.
            foreach (var file in Files(layout.SettlementReports))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith("refused-", StringComparison.Ordinal)) continue;
                try { File.Delete(file); reports.Add(name); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { retained.Add("world-settlements/" + name + ": " + error.GetType().Name); }
            }
            var record = new Dictionary<string, object> { ["references"] = references.Report(), ["sweptStaging"] = debris };
            if (!references.Complete)
            {
                record["sweep"] = "skipped: reference discovery is incomplete (" + references.Gaps.Count + " gaps: " + references.Gaps[0] + (references.Gaps.Count > 1 ? "; ..." : "") + "); every world scene and cache is kept";
            }
            else
            {
                Action<string> keep = name => { if (!kept.ContainsKey(name)) kept[name] = references.Why(name); };
                foreach (var dir in Dirs(layout.SceneStore))
                {
                    string name = Path.GetFileName(dir);
                    if (!isWorldScene(name)) continue;
                    if (references.Referenced(name)) keep(name); else removeDir(dir, swept, name);
                }
                foreach (var dir in Dirs(layout.SceneArchive))
                {
                    string name = Path.GetFileName(dir);
                    if (!isWorldScene(name)) continue;
                    if (references.Referenced(name)) keep(name); else removeDir(dir, archived, name);
                }
                foreach (var file in Files(layout.SettlementReports))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!isWorldScene(name) || references.Referenced(name)) continue;
                    try { File.Delete(file); reports.Add(name); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { retained.Add("world-settlements/" + name + ": " + error.GetType().Name); }
                }
                foreach (var dir in Dirs(layout.NavigationCaches))
                {
                    string name = Path.GetFileName(dir), scene;
                    if (name.IndexOf('-') >= 0) continue;   // debris, handled above
                    try
                    {
                        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(dir, "manifest.json"))) as IDictionary<string, object>;
                        object value; scene = manifest != null && manifest.TryGetValue("scene", out value) ? value as string : null;
                    }
                    catch (Exception error) { retained.Add("navigation-caches/" + name + ": its manifest cannot be read (" + error.GetType().Name + "), kept"); continue; }
                    if (scene == null || !isWorldScene(scene)) continue;   // a fixture scene's caches, never swept
                    if (references.Referenced(scene)) keep(scene); else removeDir(dir, caches, name);
                }
                record["sweep"] = "complete";
            }
            record["sweptUnusedScenes"] = swept; record["sweptArchivedScenes"] = archived; record["sweptSettlementReports"] = reports; record["sweptNavigationCaches"] = caches;
            record["retainedUnusedScenes"] = retained; record["keptScenes"] = kept;
            if (layout.ReportPath != null)
                try { DurableFile.WriteAllText(layout.ReportPath, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(record)); record["reportPath"] = layout.ReportPath; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { record["reportError"] = error.GetType().Name + ": " + error.Message; }
            return record;
        }

        private static string[] Dirs(string root)
        {
            try { return root != null && Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToArray() : new string[0]; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return new string[0]; }
        }
        private static string[] Files(string root)
        {
            try { return root != null && Directory.Exists(root) ? Directory.GetFiles(root, "*.json").OrderBy(d => d, StringComparer.Ordinal).ToArray() : new string[0]; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return new string[0]; }
        }
    }

    // MMR-282: a copy of each installed world scene under <store>/world-scenes/<scene name>, outside the module folder a module update
    // replaces. Content-addressed like the scene store: written once through a staging folder moved into place whole, never rewritten; a
    // load whose scene is missing from the module copies it back (verified file by file against the package's scene files) instead of
    // re-encoding it, so a campaign survives a module update even when this build's encoder would name the world differently.
    internal static class WorldSceneArchive
    {
        public const string StagingPrefix = "staging-";

        // Each file's SHA-256 in the folder, null for a missing file (the form LoadPolicy.Scene compares).
        public static Dictionary<string, string> Hashes(string folder, IEnumerable<string> files)
        {
            return files.ToDictionary(f => f, f => folder != null && File.Exists(Path.Combine(folder, f)) ? Sha256File(Path.Combine(folder, f)) : null, StringComparer.Ordinal);
        }

        // Archives the scene in source (whose files must have the expected hashes) unless the identical archive exists. Returns "kept" or
        // "archived"; throws when an archive of that name differs (archives are never rewritten) or a copy does not verify.
        public static string Keep(string archive, string source, IDictionary<string, string> expected)
        {
            if (Directory.Exists(archive))
            {
                var have = Hashes(archive, expected.Keys);
                var differs = expected.Where(kv => have[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
                if (differs.Count == 0) return "kept";
                throw new IOException("The archived scene " + Path.GetFileName(archive) + " differs from the installed one (" + String.Join(", ", differs) + "); archives are never rewritten.");
            }
            string root = Path.GetDirectoryName(Path.GetFullPath(archive)), staging = Path.Combine(root, StagingPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var kv in expected)
                {
                    string to = Path.Combine(staging, kv.Key);
                    File.Copy(Path.Combine(source, kv.Key), to, false);
                    if (Sha256File(to) != kv.Value) throw new IOException("The archived copy of " + kv.Key + " differs from the scene's file.");
                }
                Directory.Move(staging, archive);
                return "archived";
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { } }
        }

        // Copies an archived scene into target (which holds none of its files), each file verified against expected; a partial copy is removed.
        public static void Restore(string archive, string target, IDictionary<string, string> expected)
        {
            bool existed = Directory.Exists(target);
            if (existed && expected.Keys.Any(f => File.Exists(Path.Combine(target, f)))) throw new IOException("The scene folder " + target + " already holds scene files; an installed scene is never rewritten.");
            Directory.CreateDirectory(target);
            try
            {
                foreach (var kv in expected)
                {
                    string to = Path.Combine(target, kv.Key);
                    File.Copy(Path.Combine(archive, kv.Key), to, false);
                    if (Sha256File(to) != kv.Value) throw new IOException("The restored " + kv.Key + " differs from the package's scene file.");
                }
            }
            catch
            {
                try { if (existed) foreach (var f in expected.Keys) File.Delete(Path.Combine(target, f)); else Directory.Delete(target, true); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
                throw;
            }
        }

        public static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
