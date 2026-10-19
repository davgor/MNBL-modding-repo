using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-48: moves generated campaigns between stores without regenerating them. An export is a folder holding the chosen saves with the
    // immutable world content they need (each world package, worlds/<id>-<manifest12>, and each data bundle, bundles/<worldContentId>,
    // once however many saves use it), each compiled campaign's activation evidence (evidence/<instance>/: the navigation, playability and
    // settlement layout reports its activation kept, MMR-49: a load restores them when the store lacks them, and the navigation report is
    // the engine's validation, which cannot be redone offline) and export.json, which lists every file with its length and SHA-256 and each
    // save's binding. Derived artifacts are not exported: the map scene and the navigation caches are rebuilt from the bundle by the runtime
    // backend (MMR-45/MMR-49 install the scene and verify it against the package's sceneFiles hashes; the caches are keyed by NavigationCacheKey).
    //   Export: each save must be bound to a world (its header's mnbl.world.* keys) and whole (SaveFileCheck); each package must verify
    //     (PackageProblems) and each bundle too (the bundle verifier, WorldDataBundle.Problems in game and in the tool). The files are copied
    //     into a staging folder beside the destination (the same volume), each copy hashed against its source, flushed, then the folder is
    //     renamed to its name. A rename never crosses volumes.
    //   Import: the export is verified first (Verify: the manifest, every file's length and hash, no unlisted file or link, safe paths, each
    //     save's header against its manifest row, the packages and bundles), then the store lock is taken (a running game holds it), then
    //     every conflict is checked before anything is written: a save name already in the save folder is refused (an import never replaces
    //     a save; a new name can be given), and a package, bundle or activation evidence file already in the store must be identical (it is
    //     then reused: identical immutable content is stored once). Content is copied first, each through staging the startup cleanup removes
    //     (bundles/.staging-*, worlds/*.tmp-*, activations/<instance>/evidence/*.tmp-*) and renamed into place; the save last, through a temporary file in the save folder renamed to "<name>.sav" (a rename
    //     that never replaces), then read back bound and whole. A crash leaves staging, unreferenced complete content (harmless), or a
    //     temporary save file the next import removes; never a save without its world.
    // Pure managed; the bundle verifier is passed in.
    internal static class WorldSaveTransfer
    {
        public const string Schema = "mnbl.save-export.v2";   // v2 (MMR-49): with each compiled campaign's activation evidence
        public const string ManifestFile = "export.json";
        public const string PackageSchema = "mnbl.generated-world-package.v1";   // GeneratedWorldPackage.Schema (the probe checks they agree)
        public const string ImportTemporaryPrefix = ".mnbl-import-";
        public const int MaxPath = 259;   // the Windows path limit the game and this module's .NET 4.8 file calls run under
        public const string DerivedNote = "The map scene and the navigation caches are derived from the bundle by the runtime backend and are not exported; they are rebuilt and verified against the package's sceneFiles hashes and NavigationCacheKey.";
        private static readonly Regex Hex64 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex Instance32 = new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        // The reports a compiled campaign's activation keeps in activations/<instance>/evidence (ActivationCommitStage) and a load restores.
        public static readonly string[] EvidenceFiles = { "navigation.json", "playability.json", "settlement-layout.json" };

        internal sealed class Refused : IOException
        {
            public readonly List<string> Problems;
            public Refused(string message, IEnumerable<string> problems) : base(message + (problems == null || !problems.Any() ? "" : ": " + String.Join("; ", problems.Take(8)) + (problems.Count() > 8 ? "; ..." : ""))) { Problems = (problems ?? Enumerable.Empty<string>()).ToList(); }
        }

        internal sealed class Options
        {
            public readonly string StoreRoot, SaveFolder;
            public readonly Func<string, bool, IList<string>> BundleProblems;   // (bundle directory, its name must be its content id) -> problems
            private readonly Func<DateTime> clock;
            public Options(string storeRoot, string saveFolder, Func<string, bool, IList<string>> bundleProblems, Func<DateTime> clock = null)
            {
                StoreRoot = storeRoot; SaveFolder = saveFolder; BundleProblems = bundleProblems; this.clock = clock ?? (() => DateTime.UtcNow);
            }
            public DateTime Now { get { return clock(); } }
        }

        internal sealed class SaveRow { public string Name, File, UniqueGameId; public WorldBinding Binding; public Dictionary<string, string> Compatibility; }
        internal sealed class FileRow { public string Path, Sha256; public long Length; }
        internal sealed class Manifest
        {
            public string Directory;
            public List<SaveRow> Saves = new List<SaveRow>();
            public List<string> Packages = new List<string>(), Bundles = new List<string>(), Evidence = new List<string>();
            public List<FileRow> Files = new List<FileRow>();
        }

        // ---------------------------------------------------------------- export

        public static Dictionary<string, object> Export(Options options, IList<string> saveNames, string destinationParent, string exportName)
        {
            if (options == null || options.StoreRoot == null || options.SaveFolder == null || options.BundleProblems == null) throw new ArgumentNullException("options");
            if (saveNames == null || saveNames.Count == 0) throw new ArgumentException("Name at least one save to export.");
            if (!SaveJournal.ValidName(exportName) || exportName.Trim() != exportName) throw new ArgumentException("Not a valid export name: '" + exportName + "'.");
            string store = Path.GetFullPath(options.StoreRoot), destination = Path.GetFullPath(destinationParent);
            var catalog = new FolderSaveCatalog(options.SaveFolder);
            var problems = new List<string>();
            var rows = new List<SaveRow>();
            foreach (var name in saveNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SaveHeader header;
                try { header = catalog.Read(name); }
                catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException) { problems.Add("save " + name + ": " + error.Message); continue; }
                if (header == null) { problems.Add("save " + name + ": not in the save folder"); continue; }
                if (!header.Readable) { problems.Add("save " + name + ": its header cannot be read"); continue; }
                WorldBinding binding;
                try { binding = WorldBinding.FromHeader(header.Metadata); }
                catch (InvalidDataException error) { problems.Add("save " + name + ": malformed world binding (" + error.Message + ")"); continue; }
                if (binding == null) { problems.Add("save " + name + ": not a save of a generated world"); continue; }
                if (header.BodyWhole != true) { problems.Add("save " + name + ": not whole (" + (header.BodyProblem ?? "unchecked") + ")"); continue; }
                string unique; header.Metadata.TryGetValue("UniqueGameId", out unique);
                rows.Add(new SaveRow { Name = name, File = "saves/" + name + ".sav", UniqueGameId = unique, Binding = binding, Compatibility = SaveBindingContract.CompatibilityFrom(header.Metadata) });
            }
            var packages = rows.Select(r => r.Binding).GroupBy(b => b.PackageName).Select(g => g.First()).ToList();
            foreach (var b in packages) problems.AddRange(PackageProblems(Path.Combine(store, "worlds", b.PackageName), b.ManifestSha256, b.WorldId).Select(p => "package " + b.PackageName + ": " + p));
            var bundles = rows.Select(r => r.Binding.ContentId).Where(c => c != WorldBinding.NoContent).Distinct(StringComparer.Ordinal).ToList();
            foreach (var c in bundles) problems.AddRange(options.BundleProblems(Path.Combine(store, "bundles", c), true).Select(p => "bundle " + c.Substring(0, 12) + ": " + p));
            var evidence = rows.Where(r => r.Binding.ContentId != WorldBinding.NoContent).Select(r => r.Binding.Instance).Distinct(StringComparer.Ordinal).ToList();
            foreach (var i in evidence)
            {
                if (i == null || !Instance32.IsMatch(i)) { problems.Add("campaign " + i + ": not a campaign instance id"); continue; }
                foreach (var f in EvidenceFiles)
                    if (!File.Exists(EvidencePath(store, i, f))) problems.Add("campaign " + i + ": its activation evidence " + f + " is missing (a load on another computer needs it)");
            }
            if (problems.Count > 0) throw new Refused("Nothing was exported", problems);
            string target = Path.Combine(destination, exportName);
            if (Directory.Exists(target) || File.Exists(target)) throw new Refused("Nothing was exported: " + target + " exists", null);
            string staging = Path.Combine(destination, ".mnbl-export-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var relatives = rows.Select(r => r.File).Concat(packages.SelectMany(b => Relative(Path.Combine(store, "worlds", b.PackageName), "worlds/" + b.PackageName)))
                .Concat(bundles.SelectMany(c => Relative(Path.Combine(store, "bundles", c), "bundles/" + c))).Concat(evidence.SelectMany(i => EvidenceFiles.Select(f => "evidence/" + i + "/" + f)))
                .Concat(new[] { ManifestFile }).ToList();
            RequireFits("Nothing was exported", relatives.SelectMany(r => new[] { Path.Combine(staging, r), Path.Combine(target, r) }));
            Directory.CreateDirectory(destination);
            var files = new List<FileRow>();
            try
            {
                foreach (var r in rows) files.Add(CopyVerified(catalog.PathOf(r.Name), staging, r.File));
                foreach (var b in packages) files.AddRange(CopyTree(Path.Combine(store, "worlds", b.PackageName), staging, "worlds/" + b.PackageName));
                foreach (var c in bundles) files.AddRange(CopyTree(Path.Combine(store, "bundles", c), staging, "bundles/" + c));
                foreach (var i in evidence) foreach (var f in EvidenceFiles) files.Add(CopyVerified(EvidencePath(store, i, f), staging, "evidence/" + i + "/" + f));
                var manifest = new Dictionary<string, object> {
                    ["schema"] = Schema, ["createdUtc"] = options.Now.ToString("o", CultureInfo.InvariantCulture),
                    ["saves"] = rows.Select(r => (object)new Dictionary<string, object> { ["name"] = r.Name, ["file"] = r.File, ["worldId"] = r.Binding.WorldId, ["manifestSha256"] = r.Binding.ManifestSha256,
                        ["instance"] = r.Binding.Instance, ["contentId"] = r.Binding.ContentId, ["package"] = r.Binding.PackageName, ["uniqueGameId"] = r.UniqueGameId,
                        ["compatibility"] = r.Compatibility.ToDictionary(k => k.Key, k => (object)k.Value) }).ToList(),
                    ["worlds"] = packages.Select(b => (object)new Dictionary<string, object> { ["package"] = b.PackageName, ["worldId"] = b.WorldId, ["manifestSha256"] = b.ManifestSha256 }).ToList(),
                    ["bundles"] = bundles.Select(c => (object)new Dictionary<string, object> { ["contentId"] = c }).ToList(),
                    ["evidence"] = evidence.Select(i => (object)new Dictionary<string, object> { ["instance"] = i, ["files"] = EvidenceFiles.Cast<object>().ToList() }).ToList(),
                    ["files"] = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => (object)new Dictionary<string, object> { ["path"] = f.Path, ["length"] = f.Length, ["sha256"] = f.Sha256 }).ToList(),
                    ["derived"] = DerivedNote };
                WriteFlushed(Path.Combine(staging, ManifestFile), new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(manifest)));
                // Verified before it takes its name: a copy torn by a save rewritten meanwhile never stands under the export's name (review finding).
                Verify(staging, options.BundleProblems);
                Directory.Move(staging, target);
            }
            catch (Exception)
            {
                try { if (Directory.Exists(staging)) StagingCleanup.DeleteTree(staging); } catch (Exception) { }
                throw;
            }
            var verified = Verify(target, options.BundleProblems);
            return new Dictionary<string, object> { ["directory"] = target, ["saves"] = rows.Select(r => r.Name).ToList(), ["worlds"] = packages.Select(b => b.PackageName).ToList(), ["bundles"] = bundles,
                ["evidence"] = evidence, ["files"] = verified.Files.Count, ["bytes"] = verified.Files.Sum(f => f.Length), ["verified"] = true };
        }

        // ---------------------------------------------------------------- verify

        // The export, verified whole; throws Refused with every problem found.
        public static Manifest Verify(string exportDirectory, Func<string, bool, IList<string>> bundleProblems)
        {
            string directory = Path.GetFullPath(exportDirectory);
            var problems = new List<string>();
            if (!Directory.Exists(directory)) throw new Refused("Not an export", new[] { directory + " does not exist" });
            if (IsLink(directory)) throw new Refused("Not an export", new[] { "the export folder is a link" });
            Dictionary<string, object> document;
            try { document = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(directory, ManifestFile))) as Dictionary<string, object>; }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException || error is UnauthorizedAccessException) { throw new Refused("Not an export", new[] { ManifestFile + ": " + error.Message }); }
            if (document == null || Text(document, "schema") != Schema) throw new Refused("Not an export", new[] { ManifestFile + " is not " + Schema });
            var manifest = new Manifest { Directory = directory };
            foreach (var row in Rows(document, "worlds")) { string p = Text(row, "package"); if (p != null) manifest.Packages.Add(p); }
            foreach (var row in Rows(document, "bundles")) { string c = Text(row, "contentId"); if (c != null) manifest.Bundles.Add(c); }
            foreach (var row in Rows(document, "evidence")) { string i = Text(row, "instance"); if (i != null && Instance32.IsMatch(i)) manifest.Evidence.Add(i); else problems.Add("an evidence row names no campaign instance"); }
            foreach (var row in Rows(document, "files"))
            {
                object length; string path = Text(row, "path"), sha = Text(row, "sha256");
                if (path == null || sha == null || !Hex64.IsMatch(sha) || !row.TryGetValue("length", out length) || !(length is int || length is long)) { problems.Add("a file row lacks its path, length or hash"); continue; }
                string why = PathProblem(path, manifest);
                if (why != null) { problems.Add(path + ": " + why); continue; }
                manifest.Files.Add(new FileRow { Path = path, Sha256 = sha, Length = Convert.ToInt64(length, CultureInfo.InvariantCulture) });
            }
            if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count) problems.Add("a file is listed twice");
            // Every listed file, and nothing else.
            var listed = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var found in Tree(directory, problems))
                if (!found.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase) && !listed.Contains(found)) problems.Add(found + ": not listed in " + ManifestFile);
            foreach (var file in manifest.Files)
            {
                string full = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { problems.Add(file.Path + ": missing"); continue; }
                long actual = new FileInfo(full).Length;
                if (actual != file.Length) { problems.Add(file.Path + ": " + actual + " bytes, listed " + file.Length); continue; }
                if (Sha256File(full) != file.Sha256) problems.Add(file.Path + ": its SHA-256 is not the listed one");
            }
            foreach (var row in Rows(document, "saves"))
            {
                var save = new SaveRow { Name = Text(row, "name"), File = Text(row, "file"), UniqueGameId = Text(row, "uniqueGameId"),
                    Binding = new WorldBinding { WorldId = Text(row, "worldId"), ManifestSha256 = Text(row, "manifestSha256"), Instance = Text(row, "instance"), ContentId = Text(row, "contentId") } };
                try { save.Binding.Validate(); }
                catch (InvalidDataException error) { problems.Add("save " + save.Name + ": " + error.Message); continue; }
                if (!SaveJournal.ValidName(save.Name) || save.File != "saves/" + save.Name + ".sav" || !listed.Contains(save.File)) { problems.Add("save " + save.Name + ": its file is not saves/<name>.sav or not listed"); continue; }
                if (Text(row, "package") != save.Binding.PackageName || !manifest.Packages.Contains(save.Binding.PackageName)) problems.Add("save " + save.Name + ": its package is not in the export");
                if (save.Binding.ContentId != WorldBinding.NoContent && !manifest.Bundles.Contains(save.Binding.ContentId)) problems.Add("save " + save.Name + ": its bundle is not in the export");
                if (save.Binding.ContentId != WorldBinding.NoContent && (!manifest.Evidence.Contains(save.Binding.Instance) || EvidenceFiles.Any(f => !listed.Contains("evidence/" + save.Binding.Instance + "/" + f))))
                    problems.Add("save " + save.Name + ": its campaign's activation evidence is not in the export");
                string full = Path.Combine(directory, "saves", save.Name + ".sav");
                if (File.Exists(full))
                {
                    try
                    {
                        var found = WorldBinding.FromHeader(SaveFolder.ReadMetadata(full));
                        if (found == null || found.Instance != save.Binding.Instance || !found.SameWorld(save.Binding)) problems.Add("save " + save.Name + ": its header is not bound to the world and campaign its row names");
                        string why; if (SaveFileCheck.Check(full, out why) != true) problems.Add("save " + save.Name + ": not whole (" + why + ")");
                    }
                    catch (InvalidDataException error) { problems.Add("save " + save.Name + ": " + error.Message); }
                }
                manifest.Saves.Add(save);
            }
            if (manifest.Saves.Count == 0) problems.Add("the export holds no save");
            foreach (var file in manifest.Files.Where(f => f.Path.StartsWith("saves/", StringComparison.Ordinal)))
                if (manifest.Saves.Count(s => String.Equals(s.File, file.Path, StringComparison.OrdinalIgnoreCase)) != 1) problems.Add(file.Path + ": not named by exactly one save row");
            foreach (var package in manifest.Packages)
            {
                var binding = manifest.Saves.Select(s => s.Binding).FirstOrDefault(b => b.PackageName == package);
                if (binding == null) { problems.Add("package " + package + ": no save uses it"); continue; }
                problems.AddRange(PackageProblems(Path.Combine(directory, "worlds", package), binding.ManifestSha256, binding.WorldId).Select(p => "package " + package + ": " + p));
            }
            foreach (var instance in manifest.Evidence)
                if (!manifest.Saves.Any(s => s.Binding.Instance == instance && s.Binding.ContentId != WorldBinding.NoContent)) problems.Add("evidence " + instance + ": no save of a compiled world is of that campaign");
            foreach (var content in manifest.Bundles)
            {
                if (!manifest.Saves.Any(s => s.Binding.ContentId == content)) { problems.Add("bundle " + content + ": no save uses it"); continue; }
                problems.AddRange(bundleProblems(Path.Combine(directory, "bundles", content), true).Select(p => "bundle " + content.Substring(0, Math.Min(12, content.Length)) + ": " + p));
            }
            if (problems.Count > 0) throw new Refused("The export does not verify", problems);
            return manifest;
        }

        // ---------------------------------------------------------------- import

        // renames: export save name -> name in the save folder (others keep their name).
        public static Dictionary<string, object> Import(Options options, string exportDirectory, IDictionary<string, string> renames = null)
        {
            if (options == null || options.StoreRoot == null || options.SaveFolder == null || options.BundleProblems == null) throw new ArgumentNullException("options");
            var manifest = Verify(exportDirectory, options.BundleProblems);
            string store = Path.GetFullPath(options.StoreRoot), saveFolder = Path.GetFullPath(options.SaveFolder);
            Directory.CreateDirectory(store);
            using (var storeLock = StoreLock.TryAcquire(store))
            {
                if (storeLock == null) throw new Refused("Nothing was imported: another process (a running game) holds " + Path.Combine(store, "store.lock") + "; close it first", null);
                Directory.CreateDirectory(saveFolder);
                var catalog = new FolderSaveCatalog(saveFolder);
                var removedTemporaries = new List<string>();
                foreach (var stale in Directory.GetFiles(saveFolder, ImportTemporaryPrefix + "*.tmp")) { File.Delete(stale); removedTemporaries.Add(Path.GetFileName(stale)); }
                // Every conflict before anything is written.
                var problems = new List<string>();
                var targets = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var save in manifest.Saves)
                {
                    string target; if (renames == null || !renames.TryGetValue(save.Name, out target)) target = save.Name;
                    if (!SaveJournal.ValidName(target)) { problems.Add("save " + save.Name + ": '" + target + "' is not a valid save name"); continue; }
                    if (SaveFolder.Contains(saveFolder, target)) problems.Add("save " + target + ": a save of that name exists (an import never replaces a save; give it another name)");
                    if (targets.Values.Contains(target, StringComparer.OrdinalIgnoreCase)) problems.Add("save " + target + ": two saves would get that name");
                    targets[save.Name] = target;
                }
                var packageAction = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var package in manifest.Packages)
                {
                    string existing = Path.Combine(store, "worlds", package);
                    if (!Directory.Exists(existing)) { packageAction[package] = "copied"; continue; }
                    string why = SameTree(manifest, "worlds/" + package, existing);
                    if (why == null) packageAction[package] = "reused"; else problems.Add("package " + package + ": the store holds a different package under that name (" + why + ")");
                }
                var bundleAction = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var content in manifest.Bundles)
                {
                    string existing = Path.Combine(store, "bundles", content);
                    if (!Directory.Exists(existing)) { bundleAction[content] = "copied"; continue; }
                    string why = SameTree(manifest, "bundles/" + content, existing);
                    if (why == null) bundleAction[content] = "reused"; else problems.Add("bundle " + content.Substring(0, 12) + ": the store holds a different bundle under that content id (" + why + ")");
                }
                var evidenceAction = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var instance in manifest.Evidence)
                {
                    var copies = EvidenceFiles.Where(f => !File.Exists(EvidencePath(store, instance, f))).ToList();
                    foreach (var f in EvidenceFiles.Except(copies))
                        if (Sha256File(EvidencePath(store, instance, f)) != manifest.Files.First(r => r.Path == "evidence/" + instance + "/" + f).Sha256)
                            problems.Add("campaign " + instance + ": the store holds different activation evidence (" + f + ")");
                    evidenceAction[instance] = copies.Count == 0 ? "reused" : copies.Count == EvidenceFiles.Length ? "copied" : "completed";
                }
                if (problems.Count > 0) throw new Refused("Nothing was imported", problems);
                string stagingName = Guid.NewGuid().ToString("N").Substring(0, 12);
                RequireFits("Nothing was imported", manifest.Files.SelectMany(f =>
                {
                    var parts = f.Path.Split(new[] { '/' }, 3);
                    if (parts[0] == "saves") return new[] { Path.Combine(saveFolder, ImportTemporaryPrefix + stagingName + ".tmp"), Path.Combine(saveFolder, targets[Path.GetFileNameWithoutExtension(parts[1])] + ".sav") };
                    if (parts[0] == "evidence") { string kept = EvidencePath(store, parts[1], parts[2]); return new[] { kept + DurableFile.TemporaryMarker + stagingName, kept }; }
                    string final = Path.Combine(store, parts[0], parts[1]);
                    string staging = parts[0] == "bundles" ? Path.Combine(store, "bundles", ".staging-" + stagingName) : final + DurableFile.TemporaryMarker + stagingName;
                    return new[] { Path.Combine(staging, parts[2]), Path.Combine(final, parts[2]) };
                }));
                var initialJournals = new EarlyWorldBindingStore(store, catalog, b => true, () => true);
                var laterJournals = new SaveJournal(store, catalog, catalog.Stat, () => true);
                var before = WorldReferenceIndex.Build(catalog, initialJournals, null, WorldReferenceIndex.SaveKindsBound, laterJournals);
                // Immutable content first, each through staging renamed into place.
                foreach (var content in manifest.Bundles.Where(c => bundleAction[c] == "copied"))
                {
                    string staging = Path.Combine(store, "bundles", ".staging-" + stagingName);
                    CopyListed(manifest, "bundles/" + content, staging);
                    var stagingProblems = options.BundleProblems(staging, false);
                    if (stagingProblems.Count > 0) { StagingCleanup.DeleteTree(staging); throw new Refused("The copied bundle does not verify", stagingProblems); }
                    Directory.Move(staging, Path.Combine(store, "bundles", content));
                }
                foreach (var package in manifest.Packages.Where(p => packageAction[p] == "copied"))
                {
                    string final = Path.Combine(store, "worlds", package), staging = final + DurableFile.TemporaryMarker + stagingName;
                    CopyListed(manifest, "worlds/" + package, staging);
                    Directory.Move(staging, final);
                }
                foreach (var package in manifest.Packages)
                {
                    var binding = manifest.Saves.First(s => s.Binding.PackageName == package).Binding;
                    var after = PackageProblems(Path.Combine(store, "worlds", package), binding.ManifestSha256, binding.WorldId);
                    if (after.Count > 0) throw new Refused("The package in the store does not verify", after);
                }
                foreach (var content in manifest.Bundles)
                {
                    var after = options.BundleProblems(Path.Combine(store, "bundles", content), true);
                    if (after.Count > 0) throw new Refused("The bundle in the store does not verify", after);
                }
                // The campaigns' activation evidence, each missing file through a temporary file renamed into place (never replacing one).
                foreach (var instance in manifest.Evidence)
                    foreach (var f in EvidenceFiles.Where(f => !File.Exists(EvidencePath(store, instance, f))))
                    {
                        string final = EvidencePath(store, instance, f), temporary = final + DurableFile.TemporaryMarker + stagingName;
                        CopyFile(Path.Combine(manifest.Directory, "evidence", instance, f), temporary, manifest.Files.First(r => r.Path == "evidence/" + instance + "/" + f));
                        File.Move(temporary, final);
                    }
                // The saves last: a temporary file renamed to its name (never replacing one), then read back.
                var imported = new List<Dictionary<string, object>>();
                foreach (var save in manifest.Saves)
                {
                    string target = targets[save.Name];
                    var row = manifest.Files.First(f => f.Path == save.File);
                    string temporary = Path.Combine(saveFolder, ImportTemporaryPrefix + stagingName + ".tmp");
                    CopyFile(Path.Combine(manifest.Directory, "saves", save.Name + ".sav"), temporary, row);
                    File.Move(temporary, catalog.PathOf(target));
                    var header = catalog.Read(target);
                    WorldBinding found = header == null || !header.Readable ? null : WorldBinding.FromHeader(header.Metadata);
                    if (found == null || found.Instance != save.Binding.Instance || !found.SameWorld(save.Binding) || header.BodyWhole != true)
                    {
                        catalog.Delete(target);
                        throw new Refused("The imported save " + target + " did not read back bound and whole; it was removed", null);
                    }
                    imported.Add(new Dictionary<string, object> { ["from"] = save.Name, ["name"] = target, ["instance"] = save.Binding.Instance, ["package"] = save.Binding.PackageName, ["contentId"] = save.Binding.ContentId });
                }
                var afterIndex = WorldReferenceIndex.Build(catalog, initialJournals, null, WorldReferenceIndex.SaveKindsBound, laterJournals);
                return new Dictionary<string, object> { ["export"] = manifest.Directory, ["saves"] = imported,
                    ["packages"] = packageAction.ToDictionary(k => k.Key, k => (object)k.Value), ["bundles"] = bundleAction.ToDictionary(k => k.Key, k => (object)k.Value),
                    ["evidence"] = evidenceAction.ToDictionary(k => k.Key, k => (object)k.Value),
                    ["removedTemporaries"] = removedTemporaries, ["countsBefore"] = before.Counts(), ["countsAfter"] = afterIndex.Counts(),
                    ["indexComplete"] = afterIndex.DiscoveryComplete, ["regenerated"] = false };
            }
        }

        // ---------------------------------------------------------------- package verification

        // Why a world package directory is not the package a binding names (empty when it is): world.json hashes to the binding's manifest hash,
        // is a package manifest for the binding's world, and every file it lists is inside the package with its listed hash; no link.
        public static List<string> PackageProblems(string directory, string manifestSha256, string worldId)
        {
            var problems = new List<string>();
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory)) { problems.Add("not a package directory"); return problems; }
            if (IsLink(directory)) { problems.Add("the package directory is a link"); return problems; }
            string manifestPath = Path.Combine(directory, "world.json");
            if (!File.Exists(manifestPath)) { problems.Add("world.json is missing"); return problems; }
            if (Sha256File(manifestPath) != manifestSha256) { problems.Add("world.json does not hash to the binding's manifest hash"); return problems; }
            Dictionary<string, object> manifest;
            try { manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(manifestPath)) as Dictionary<string, object>; }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { problems.Add("world.json is not JSON: " + error.Message); return problems; }
            if (manifest == null || Text(manifest, "schema") != PackageSchema) { problems.Add("world.json is not " + PackageSchema); return problems; }
            if (Text(manifest, "worldId") != worldId) problems.Add("world.json names world " + Text(manifest, "worldId") + ", not " + worldId);
            object files;
            if (!manifest.TryGetValue("files", out files) || !(files is IDictionary<string, object>)) { problems.Add("world.json lists no files"); return problems; }
            foreach (var file in (IDictionary<string, object>)files)
            {
                string path = Path.GetFullPath(Path.Combine(directory, file.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { problems.Add(file.Key + " escapes the package"); continue; }
                if (!File.Exists(path)) { problems.Add(file.Key + " is missing"); continue; }
                if (IsLink(path)) { problems.Add(file.Key + " is a link"); continue; }
                if (Sha256File(path) != Convert.ToString(file.Value, CultureInfo.InvariantCulture)) problems.Add(file.Key + " does not have its listed hash");
            }
            return problems;
        }

        // ---------------------------------------------------------------- helpers

        private static string PathProblem(string path, Manifest manifest)
        {
            var parts = path.Split('/');
            if (path.Length > 400 || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return "not a safe relative path";
            if (parts[0] == "saves")
                return parts.Length == 2 && parts[1].EndsWith(".sav", StringComparison.Ordinal) && SaveJournal.ValidName(parts[1].Substring(0, parts[1].Length - 4)) ? null : "a save must be saves/<name>.sav";
            if (parts.Any(p => p.Trim() != p)) return "not a safe relative path";
            if (parts[0] == "worlds") return parts.Length >= 3 && manifest.Packages.Contains(parts[1]) ? null : "not inside a listed world package";
            if (parts[0] == "bundles") return parts.Length >= 3 && manifest.Bundles.Contains(parts[1]) && Hex64.IsMatch(parts[1]) ? null : "not inside a listed bundle";
            if (parts[0] == "evidence") return parts.Length == 3 && manifest.Evidence.Contains(parts[1]) && EvidenceFiles.Contains(parts[2]) ? null : "not a listed campaign's activation evidence";
            return "outside saves/, worlds/, bundles/ and evidence/";
        }

        // A campaign's kept activation report in a store (ActivationJournalStore.InstanceDirectory(instance)/evidence/<file>).
        public static string EvidencePath(string store, string instance, string file) { return Path.Combine(store, "activations", instance, "evidence", file); }

        // Refuses before anything is written when a path the operation would create is longer than the Windows limit.
        private static void RequireFits(string refusal, IEnumerable<string> paths)
        {
            var longest = paths.OrderByDescending(p => p.Length).FirstOrDefault();
            if (longest != null && longest.Length > MaxPath)
                throw new Refused(refusal + ": a path would be " + longest.Length + " characters, over the Windows limit of " + MaxPath + " (" + longest + "); use a shorter folder", null);
        }

        private static IEnumerable<string> Relative(string directory, string prefix)
        {
            var problems = new List<string>();
            return Tree(directory, problems).Select(r => prefix + "/" + r).ToList();
        }

        // Whether the store's directory holds exactly the export's files under prefix (the same relative paths and hashes); null when it does.
        private static string SameTree(Manifest manifest, string prefix, string existing)
        {
            var problems = new List<string>();
            var expected = manifest.Files.Where(f => f.Path.StartsWith(prefix + "/", StringComparison.Ordinal)).ToDictionary(f => f.Path.Substring(prefix.Length + 1), f => f, StringComparer.OrdinalIgnoreCase);
            var found = Tree(existing, problems);
            if (problems.Count > 0) return problems[0];
            if (found.Count != expected.Count || found.Any(f => !expected.ContainsKey(f))) return "its files differ from the export's";
            foreach (var f in found)
                if (Sha256File(Path.Combine(existing, f.Replace('/', Path.DirectorySeparatorChar))) != expected[f].Sha256) return f + " differs";
            return null;
        }

        // Relative paths ('/') of every file under a directory; a link is a problem and is not followed.
        private static List<string> Tree(string root, List<string> problems)
        {
            var files = new List<string>();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (var entry in new DirectoryInfo(directory).GetFileSystemInfos())
                {
                    string relative = entry.FullName.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { problems.Add(relative + ": a link"); continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(entry.FullName); else files.Add(relative);
                }
            }
            return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        private static List<FileRow> CopyTree(string source, string stagingRoot, string prefix)
        {
            var problems = new List<string>();
            var rows = Tree(source, problems).Select(relative => CopyVerified(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)), stagingRoot, prefix + "/" + relative)).ToList();
            if (problems.Count > 0) throw new Refused("Nothing was exported", problems);
            return rows;
        }

        // Copies one file to <stagingRoot>/<relative>, hashing the source as it is read and the copy after it is flushed.
        private static FileRow CopyVerified(string source, string stagingRoot, string relative)
        {
            string target = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string sha; long length;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[1 << 16]; int read; length = 0;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { hash.TransformBlock(buffer, 0, read, null, 0); output.Write(buffer, 0, read); length += read; }
                hash.TransformFinalBlock(buffer, 0, 0);
                output.Flush(true);
                sha = Hex(hash.Hash);
            }
            if (Sha256File(target) != sha) throw new IOException("The copy of " + relative + " does not hash to its source.");
            return new FileRow { Path = relative, Sha256 = sha, Length = length };
        }

        // Copies the export's files under prefix into a new directory, each verified against the manifest.
        private static void CopyListed(Manifest manifest, string prefix, string target)
        {
            foreach (var file in manifest.Files.Where(f => f.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
                CopyFile(Path.Combine(manifest.Directory, file.Path.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(target, file.Path.Substring(prefix.Length + 1).Replace('/', Path.DirectorySeparatorChar)), file);
        }

        private static void CopyFile(string source, string target, FileRow expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                input.CopyTo(output, 1 << 16);
                output.Flush(true);
            }
            if (new FileInfo(target).Length != expected.Length || Sha256File(target) != expected.Sha256) throw new IOException("The copy of " + expected.Path + " does not match the export.");
        }

        private static void WriteFlushed(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
        }

        public static string Sha256Bytes(byte[] bytes) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }

        public static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes) { var text = new StringBuilder(bytes.Length * 2); foreach (var b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture)); return text.ToString(); }
        private static bool IsLink(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
        private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> document, string key)
        {
            object value;
            if (!document.TryGetValue(key, out value) || !(value is IEnumerable)) return Enumerable.Empty<Dictionary<string, object>>();
            return ((IEnumerable)value).OfType<Dictionary<string, object>>();
        }
    }
}
