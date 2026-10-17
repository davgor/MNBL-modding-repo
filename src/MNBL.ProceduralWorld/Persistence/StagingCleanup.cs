using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-62: removes staging that a generation job or a durable write abandons only by crashing. Each kind is never referenced by a save, a
    // package or a bundle, because its writer renames it into place or deletes it when it finishes:
    //   bundles/.staging-*       the worldbridge's bundle staging, renamed to bundles/<worldContentId> when it validates (docs/world-bundle.md)
    //   worlds/*.tmp-*           a world package being written, renamed to worlds/<id>-<manifest12> (GeneratedWorldPackage)
    //   world-surfaces/<32 hex>  a world's encoder staging, deleted after the scene is installed (RuntimeWorldBackend.PreparedSurface.Discard)
    //   saves/journal/*.tmp-*    a journal being replaced (DurableFile)
    //   activations/*.tmp-*      an activation journal being replaced (MMR-45; DurableFile)
    //   activations/<instance>/*.tmp-*, activations/<instance>/evidence/*.tmp-*   a receipt, runtime-derived record or evidence copy being
    //                            written (MMR-45; DurableFile)
    // Nothing else is touched: published bundles, world packages, the job records under jobs/ (MMR-47 resumes or discards an unfinished job
    // from them), installed scenes and saves. It runs only while this process holds the store lock; a process without it writes no staging
    // (SaveBindingRuntime.RequireWriter guards the generation coordinator, the encoder staging and the package writer), so no other process's
    // staging can be live. Probes that run the worldbridge directly (developer and harness paths) are not guarded: the harness runs one game.
    // It skips anything isLive reports (a job of this process). Reparse points are never followed: a staging folder under a parent that is
    // one is not scanned, a candidate that is one is kept, and a link inside a staging directory is removed as a link without touching its
    // target. Every candidate lies under the store root.
    internal static class StagingCleanup
    {
        public sealed class Report
        {
            public bool Ran;
            public string Skipped;
            public readonly List<string> Removed = new List<string>();
            public readonly List<Dictionary<string, object>> Retained = new List<Dictionary<string, object>>();

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object> { ["ran"] = Ran, ["skipped"] = Skipped, ["removed"] = Removed.ToList(), ["retained"] = Retained.ToList() };
            }
        }

        private static readonly System.Text.RegularExpressions.Regex Hex32 = new System.Text.RegularExpressions.Regex("^[0-9a-f]{32}$");

        // The staging kinds: the parent folder (relative to the store), the pattern, directories or files, and a name filter.
        private static IEnumerable<Tuple<string, string, bool, Func<string, bool>>> Kinds()
        {
            yield return Tuple.Create("bundles", ".staging-*", true, (Func<string, bool>)(n => true));
            yield return Tuple.Create("worlds", "*" + DurableFile.TemporaryMarker + "*", true, (Func<string, bool>)(n => true));
            yield return Tuple.Create("world-surfaces", "*", true, (Func<string, bool>)(n => Hex32.IsMatch(n)));
            yield return Tuple.Create(Path.Combine("saves", "journal"), "*" + DurableFile.TemporaryMarker + "*", false, (Func<string, bool>)(n => true));
            yield return Tuple.Create(Path.Combine("saves", "journal", "later"), "*" + DurableFile.TemporaryMarker + "*", false, (Func<string, bool>)(n => true));   // MMR-48
            yield return Tuple.Create("activations", "*" + DurableFile.TemporaryMarker + "*", false, (Func<string, bool>)(n => true));
        }

        public static IEnumerable<string> Candidates(string store)
        {
            store = Path.GetFullPath(store);
            foreach (var kind in Kinds())
            {
                if (ParentLink(store, kind.Item1) != null) continue;
                foreach (var path in Entries(Path.Combine(store, kind.Item1), kind.Item2, kind.Item3)) if (kind.Item4(Path.GetFileName(path))) yield return path;
            }
            // MMR-45: inside each activation's directory and its evidence folder (directories that are links are not scanned).
            if (ParentLink(store, "activations") != null) yield break;
            foreach (var instance in Entries(Path.Combine(store, "activations"), "*", true))
            {
                if ((File.GetAttributes(instance) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var folder in new[] { instance, Path.Combine(instance, "evidence") })
                {
                    if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var path in Entries(folder, "*" + DurableFile.TemporaryMarker + "*", false)) yield return path;
                }
            }
        }

        // The first folder from the store down to a staging parent that is a reparse point, or null.
        private static string ParentLink(string store, string parent)
        {
            string path = store;
            foreach (var part in parent.Split(Path.DirectorySeparatorChar))
            {
                path = Path.Combine(path, part);
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return path;
            }
            return null;
        }

        public static Report Run(string store, bool lockHeld, Func<string, bool> isLive)
        {
            var report = new Report();
            if (!lockHeld) { report.Skipped = "another game process holds the store lock, or it could not be taken: nothing is removed"; return report; }
            store = Path.GetFullPath(store);
            string prefix = store.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            report.Ran = true;
            foreach (var kind in Kinds())
            {
                var link = ParentLink(store, kind.Item1);
                if (link != null) Keep(report, link.Substring(prefix.Length).Replace('\\', '/'), "a reparse point: the staging under it is not scanned");
            }
            foreach (var path in Candidates(store).ToList())
            {
                bool inside = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                string relative = inside ? path.Substring(prefix.Length).Replace('\\', '/') : path;
                try
                {
                    if (!inside) { Keep(report, relative, "outside the store root"); continue; }
                    if (isLive != null && isLive(path)) { Keep(report, relative, "owned by a live job"); continue; }
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { Keep(report, relative, "a reparse point (never followed)"); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) DeleteTree(path);
                    else DeleteFile(path, attributes);
                    report.Removed.Add(relative);
                }
                catch (Exception error) { Keep(report, relative, error.GetType().Name + ": " + error.Message); }
            }
            return report;
        }

        private static IEnumerable<string> Entries(string directory, string pattern, bool directories)
        {
            if (!Directory.Exists(directory)) return Enumerable.Empty<string>();
            return (directories ? Directory.GetDirectories(directory, pattern) : Directory.GetFiles(directory, pattern)).OrderBy(p => p, StringComparer.Ordinal);
        }

        // Deletes a directory tree without following links: a junction or symbolic link inside is removed as a link.
        internal static void DeleteTree(string directory)
        {
            foreach (var entry in Directory.GetFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0) { if (isDirectory) Directory.Delete(entry, false); else DeleteFile(entry, attributes); }
                else if (isDirectory) DeleteTree(entry);
                else DeleteFile(entry, attributes);
            }
            Directory.Delete(directory, false);
        }

        private static void DeleteFile(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        private static void Keep(Report report, string path, string reason) { report.Retained.Add(new Dictionary<string, object> { ["path"] = path, ["reason"] = reason }); }
    }
}
