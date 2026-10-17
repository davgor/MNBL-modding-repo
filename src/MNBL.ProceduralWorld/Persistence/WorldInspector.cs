using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-47: the world store as the player's campaigns see it, without the game (tools/Inspect-World.ps1 runs it): each data bundle's
    // status for this host (valid, missing, incompatible: built for other versions, needing other capabilities or another schema; corrupt:
    // anything else WorldDataBundle refuses, unsafe paths and links included), its versions and generator, the world packages, the
    // activations (campaign instance -> content -> package -> initial save), the saves (read from their headers' JSON metadata: the
    // mnbl.world.* binding) and whether each save's world can be loaded, the unfinished jobs, and the staging cleanup would remove. Reads only.
    internal static class WorldInspector
    {
        public const string Valid = "valid", Missing = "missing", Incompatible = "incompatible", Corrupt = "corrupt";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        // One bundle directory's status for this host, with every problem WorldDataBundle found.
        public static Dictionary<string, object> Bundle(string directory, WorldDataBundle.HostProfile host, bool nameMustMatch = true)
        {
            var problems = WorldDataBundle.Problems(directory, host, nameMustMatch);
            var row = new Dictionary<string, object> { ["directory"] = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), ["status"] = Classify(problems), ["problems"] = problems };
            if (problems.Count == 0 || problems.All(p => Kind(p) == Incompatible))
            {
                // Versions and generator from the manifest (read only when it parsed).
                try
                {
                    var manifest = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                    row["worldContentId"] = Text(manifest, "worldContentId");
                    var versions = manifest.ContainsKey("engine") ? manifest["engine"] as IDictionary<string, object> : null;
                    if (versions != null) { row["gameVersion"] = Text(versions, "gameVersion"); row["navalVersion"] = Text(versions, "navalVersion"); }
                    if (manifest.ContainsKey("requiredCapabilities")) row["requiredCapabilities"] = manifest["requiredCapabilities"];
                    if (manifest.ContainsKey("compiler")) row["generator"] = manifest["compiler"];
                }
                catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException || error is InvalidCastException) { }
            }
            return row;
        }

        public static string Classify(IList<string> problems)
        {
            if (problems == null || problems.Count == 0) return Valid;
            if (problems.Any(p => Kind(p) == Missing)) return Missing;
            if (problems.All(p => Kind(p) == Incompatible)) return Incompatible;
            return Corrupt;
        }

        private static string Kind(string problem)
        {
            // A directory that is not there: the verifier's own words, or OpenForSave's for a save naming a bundle the store lacks (MMR-49 run 11568).
            if (problem.StartsWith("not a bundle directory", StringComparison.Ordinal) || problem.StartsWith("missing bundle ", StringComparison.Ordinal)) return Missing;
            if (problem.StartsWith("built for ", StringComparison.Ordinal) || problem.StartsWith("requires runtime capability", StringComparison.Ordinal)
                || problem.StartsWith("manifest is not " + WorldDataBundle.Schema, StringComparison.Ordinal) || problem.StartsWith("DATA_READY is not " + WorldDataBundle.ReadySchema, StringComparison.Ordinal))
                return Incompatible;
            return Corrupt;
        }

        // A save's binding from its file (the header's JSON metadata after its length; Bannerlord's MetaData), or null for a save of no
        // generated world. Throws InvalidDataException when the header cannot be read.
        public static IDictionary<string, string> SaveMetadata(string path) { return SaveFolder.ReadMetadata(path); }

        // The whole store. savesFolder: the game's "Game Saves" folder (null: saves are not read); currentLock: this module's producer lock.
        public static Dictionary<string, object> Store(string store, string savesFolder, WorldDataBundle.HostProfile host, string currentLock, string rootProblem)
        {
            store = Path.GetFullPath(store);
            var report = new Dictionary<string, object> { ["schema"] = "mnbl.world-inspection.v1", ["root"] = rootProblem == null ? "verified" : rootProblem,
                ["host"] = new Dictionary<string, object> { ["gameVersion"] = host.GameVersion, ["navalVersion"] = host.NavalVersion, ["capabilities"] = host.Capabilities.OrderBy(c => c, StringComparer.Ordinal).ToList() } };
            // Bundles.
            var bundles = new List<Dictionary<string, object>>();
            string bundleRoot = Path.Combine(store, "bundles");
            if (Directory.Exists(bundleRoot))
                foreach (var dir in Directory.GetDirectories(bundleRoot).OrderBy(d => d, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".staging-", StringComparison.Ordinal)) continue;
                    bundles.Add(Bundle(dir, host));
                }
            // By its content id, else by its directory name (bundles are stored as bundles/<worldContentId>): a corrupt bundle whose manifest was
            // not read is still found, and reported corrupt rather than missing (review finding).
            var byContent = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var b in bundles)
            {
                string key = b.ContainsKey("worldContentId") && b["worldContentId"] is string ? (string)b["worldContentId"] : (string)b["directory"];
                if (key != null && !byContent.ContainsKey(key)) byContent[key] = b;
            }
            // Packages.
            var packages = new List<Dictionary<string, object>>();
            string packageRoot = Path.Combine(store, "worlds");
            if (Directory.Exists(packageRoot))
                foreach (var dir in Directory.GetDirectories(packageRoot).OrderBy(d => d, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Contains(DurableFile.TemporaryMarker)) continue;
                    var row = new Dictionary<string, object> { ["name"] = name, ["manifest"] = File.Exists(Path.Combine(dir, "world.json")) };
                    packages.Add(row);
                }
            var packageNames = new HashSet<string>(packages.Select(p => (string)p["name"]), StringComparer.OrdinalIgnoreCase);
            // Activations: journal and receipt per instance.
            var activations = new List<Dictionary<string, object>>();
            var journals = new ActivationJournalStore(store);
            if (Directory.Exists(journals.Root))
                foreach (var file in Directory.GetFiles(journals.Root, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string instance = Path.GetFileNameWithoutExtension(file);
                    if (!ActivationRequest.ValidInstance(instance)) continue;
                    var row = new Dictionary<string, object> { ["instance"] = instance };
                    try
                    {
                        var j = journals.Journal(instance);
                        row["state"] = j.State; row["worldContentId"] = j.WorldContentId; row["failedAt"] = j.FailedAt;
                        row["receipt"] = File.Exists(Path.Combine(journals.InstanceDirectory(instance), "receipt.json"));
                        Dictionary<string, object> b; row["bundle"] = j.WorldContentId != null && byContent.TryGetValue(j.WorldContentId, out b) ? b["status"] : Missing;
                    }
                    catch (Exception error) { row["state"] = "unreadable"; row["error"] = error.GetType().Name + ": " + error.Message; }
                    activations.Add(row);
                }
            // Saves: each save's binding, and whether its world can be loaded (package present, bundle valid).
            var saves = new List<Dictionary<string, object>>();
            var referenced = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (savesFolder != null && Directory.Exists(savesFolder))
                foreach (var file in Directory.GetFiles(savesFolder, "*.sav").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var row = new Dictionary<string, object> { ["name"] = Path.GetFileNameWithoutExtension(file) };
                    try
                    {
                        var metadata = SaveMetadata(file);
                        if (!metadata.Keys.Any(k => k.StartsWith(WorldBinding.Prefix, StringComparison.Ordinal))) continue;   // not a generated world's save
                        var binding = WorldBinding.FromHeader(metadata);
                        if (binding == null) { row["error"] = "the header's world binding names no world"; saves.Add(row); continue; }
                        row["instance"] = binding.Instance; row["worldId"] = binding.WorldId; row["contentId"] = binding.ContentId; row["package"] = binding.PackageName;
                        row["packagePresent"] = packageNames.Contains(binding.PackageName);
                        string bundleStatus = binding.ContentId == WorldBinding.NoContent ? "none (a fixture world)" : null;
                        Dictionary<string, object> b;
                        if (bundleStatus == null) bundleStatus = byContent.TryGetValue(binding.ContentId, out b) ? (string)b["status"] : Missing;
                        row["bundle"] = bundleStatus;
                        row["loadable"] = (bool)row["packagePresent"] && (bundleStatus == Valid || binding.ContentId == WorldBinding.NoContent);
                        if (binding.ContentId != WorldBinding.NoContent) { List<string> l; if (!referenced.TryGetValue(binding.ContentId, out l)) referenced[binding.ContentId] = l = new List<string>(); l.Add((string)row["name"]); }
                    }
                    catch (Exception error) { row["error"] = error.GetType().Name + ": " + error.Message; }
                    saves.Add(row);
                }
            foreach (var b in bundles)
            {
                List<string> by; string id = b.ContainsKey("worldContentId") && b["worldContentId"] is string ? (string)b["worldContentId"] : (string)b["directory"];
                b["referencedBySaves"] = id != null && referenced.TryGetValue(id, out by) ? by : new List<string>();
                b["activations"] = activations.Where(a => id != null && Equals(a.ContainsKey("worldContentId") ? a["worldContentId"] : null, id)).Select(a => a["instance"]).ToList();
            }
            report["bundles"] = bundles; report["packages"] = packages; report["activations"] = activations; report["saves"] = saves;
            report["jobs"] = WorldRecoveryService.Jobs(Path.Combine(store, "jobs"), null, currentLock).Select(j => j.Report()).ToList();
            report["staging"] = StagingCleanup.Candidates(store).Select(p => p.Substring(store.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/')).ToList();
            report["summary"] = new Dictionary<string, object> {
                ["bundles"] = bundles.GroupBy(b => (string)b["status"]).ToDictionary(g => g.Key, g => g.Count()),
                ["savesBound"] = saves.Count(s => s.ContainsKey("instance")), ["savesLoadable"] = saves.Count(s => Equals(s.ContainsKey("loadable") ? s["loadable"] : null, true)),
                ["unfinishedJobs"] = ((List<Dictionary<string, object>>)report["jobs"]).Count(j => !(bool)j["finished"]) };
            return report;
        }

        private static string Text(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string : null; }
    }
}
