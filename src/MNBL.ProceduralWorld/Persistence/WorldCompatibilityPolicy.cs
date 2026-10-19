using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-50: whether this build can load a generated campaign's save, decided before anything loads, as one of three classes:
    //   supported                   the save, its build keys, its world package and bundle, and every derived resource are usable as they are
    //                               (differences a load verifies by rebuilding are listed, not refused);
    //   rebuildable-derived-cache   as supported, but a derived resource must be rebuilt from the frozen world first, by a deterministic
    //                               adapter whose output the load verifies: a swept scene re-encoded from the bundle by the surface encoder
    //                               and installed only when every file is the package's (tested by the build that encoded it: MMR-49's cold
    //                               run; under another module build or backend the re-encoding may differ and is then refused at load, so
    //                               the rebuild is reported conditional), the navigation and playability reports restored from the copies
    //                               the activation kept, the settlement layout re-derived by the encoder with the scene, the engine's
    //                               navigation caches rebuilt from the frozen navmesh under a new cache key;
    //   unsupported-world           refused, with the refusal the loader gives (LoadPolicy codes): a torn, unreadable or held save, another
    //                               binding contract, save id version, identity schema, game or War Sails version (a War Sails save on a
    //                               game without War Sails among them; for a save made before MMR-48, the game and War Sails versions its
    //                               header records), a missing or damaged world package, a header naming another world than its package, a
    //                               missing, damaged or incompatible data bundle, an installed scene that differs, a report neither
    //                               installed nor kept, a settlement layout of another world, a world file that cannot be read now.
    // A missing data bundle is a missing world, never a cache miss: a world is never regenerated from its seed, its ids are never rewritten
    // and no native data of another build is read. There is no migration: no converter is implemented, so a save or world this build cannot
    // read is refused, never converted (docs/save-compatibility.md; the save type ledger lists no converter). Pure managed: the facts are
    // gathered from files (Gather) and the decision taken from them (Evaluate), in the loader's order (GeneratedWorldLoader: the save, its
    // build, the package, the bundle, the derived resources); tools/Inspect-World.ps1 reports it without the game, the compat probe in game.
    // The loader also runs the navigation and playability gates and rebuilds the campaign; those are verified at load, not predicted here.
    internal static class WorldCompatibilityPolicy
    {
        public const string Schema = "mnbl.world-compatibility.v1";
        public const string Supported = "supported", Rebuildable = "rebuildable-derived-cache", Unsupported = "unsupported-world";
        public const string Same = "same", Verified = "verified-at-load", Rebuild = "rebuilt-from-frozen-world", Frozen = "frozen", Refused = "refused", NotChecked = "not-checked";
        public const string Migration = "none: no migration is implemented; a save or world this build cannot read is refused, never converted";
        public static readonly string[] Reports = { "navigation.json", "settlement-layout.json", "playability.json" };
        // The build keys whose equality makes a scene re-encoding the one the world was activated with (the encoder ships with the module).
        public static readonly string[] EncoderKeys = { "module", "moduleAssemblySha256", "backend" };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        // What a decision is taken from. A null fact was not checked (an offline report without the module's scene store, say).
        internal sealed class Facts
        {
            public string Save;
            public string Header;                 // "readable", "torn", "unknown" (another program holds it), "absent"
            public string HeaderProblem;
            public bool Generated;                // the header carries a world binding (mnbl.world.id)
            public string BindingProblem;         // a malformed binding; null when well formed
            public WorldBinding Binding;
            public bool? BodyWhole; public string BodyProblem;   // BodyWhole null: the game data could not be read now
            public IDictionary<string, string> SavedBuild, RunningBuild;   // mnbl.save.* without the prefix; RunningBuild null: not checked
            public IDictionary<string, string> LegacyBuild;   // a save made before MMR-48: game and naval from its header (LoadPolicy.LegacyBuild)
            public string HeaderScene, HeaderSeed;            // mnbl.world.scene, mnbl.world.seed
            public bool? PackagePresent; public IList<string> PackageProblems;
            public string ManifestProblem;                    // world.json verified but not a complete package manifest
            public string PackageScene, PackageSeed;
            public bool? BundlePresent; public IList<string> BundleProblems;
            public LoadPolicy.SceneState? Scene;   // the world's installed scene against its package's scene files
            public string SceneName;
            public IDictionary<string, string> ReportState;   // report -> "installed", "kept" (activation evidence) or "missing"
            public string LayoutProblem;                      // the settlement layout (installed or kept) is not this world's
            public string FilesProblem;                       // a world file could not be read now (held by another program)
        }

        internal sealed class Row
        {
            public string Component, Verdict, Detail, Code;
            public bool Conditional;   // a rebuild whose adapter is verified at load but not tested for this build
            public Dictionary<string, object> Report() { return new Dictionary<string, object> { ["component"] = Component, ["verdict"] = Verdict, ["detail"] = Detail, ["code"] = Code, ["conditional"] = Conditional }; }
        }

        internal sealed class Decision
        {
            public string Class;
            public LoadPolicy.Refusal Refusal;
            public readonly List<Row> Rows = new List<Row>();
            public Dictionary<string, object> Report(string save)
            {
                return new Dictionary<string, object> { ["schema"] = Schema, ["save"] = save, ["class"] = Class, ["refusal"] = Refusal == null ? null : new Dictionary<string, object> { ["code"] = Refusal.Code, ["detail"] = Refusal.Detail },
                    ["rebuilds"] = Rows.Where(r => r.Verdict == Rebuild).Select(r => (object)r.Component).ToList(),
                    ["conditional"] = Rows.Where(r => r.Conditional).Select(r => (object)r.Component).ToList(), ["migration"] = Migration, ["rows"] = Rows.Select(r => (object)r.Report()).ToList() };
            }
        }

        public static Decision Evaluate(Facts f)
        {
            var d = new Decision();
            Action<string, string, string, string> row = (component, verdict, detail, code) => d.Rows.Add(new Row { Component = component, Verdict = verdict, Detail = detail, Code = code });
            Action<string, string, string> refuse = (component, code, detail) => row(component, Refused, detail, code);
            // The save.
            switch (f.Header)
            {
                case "readable": row("save header", Same, "readable", null); break;
                case "unknown": refuse("save header", LoadPolicy.Unavailable, "its header cannot be read now (" + (f.HeaderProblem ?? "held by another program") + ")"); break;
                case "absent": refuse("save header", LoadPolicy.DamagedSave, "the save is not in the save folder"); break;
                default: refuse("save header", LoadPolicy.DamagedSave, "its header cannot be read" + (f.HeaderProblem == null ? "" : " (" + f.HeaderProblem + ")")); break;
            }
            if (f.Header == "readable")
            {
                if (f.BindingProblem != null) refuse("world binding", LoadPolicy.DamagedSave, "its world binding is malformed: " + f.BindingProblem);
                else if (!f.Generated || f.Binding == null) refuse("world binding", LoadPolicy.NotGenerated, "the header names no generated world");
                else row("world binding", Same, f.Binding.WorldId + (f.Binding.ContentId == WorldBinding.NoContent ? " (a fixture world)" : ", world " + Short(f.Binding.ContentId)), null);
                if (f.BodyWhole == false) refuse("save data", LoadPolicy.DamagedSave, "its game data is not whole: " + (f.BodyProblem ?? "torn"));
                else if (f.BodyWhole == null) refuse("save data", LoadPolicy.Unavailable, "its game data cannot be read now" + (f.BodyProblem == null ? "" : " (" + f.BodyProblem + ")"));
                else row("save data", Same, "whole", null);
                bool compiled = f.Binding != null && f.Binding.ContentId != WorldBinding.NoContent;
                // As the loader orders them: the build, then the campaign instance, then the world.
                Build(f, row, refuse);
                if (compiled && !ValidInstance(f.Binding.Instance)) refuse("campaign instance", LoadPolicy.DamagedSave, "'" + (f.Binding.Instance ?? "(none)") + "' is not a campaign instance id");
                if (f.Binding != null && f.BindingProblem == null) World(f, compiled, row, refuse, d);
            }
            var first = d.Rows.FirstOrDefault(r => r.Verdict == Refused);
            if (first != null) { d.Class = Unsupported; d.Refusal = LoadPolicy.Refuse(first.Code, first.Detail); }
            else d.Class = d.Rows.Any(r => r.Verdict == Rebuild) ? Rebuildable : Supported;
            return d;
        }

        // The recorded build against the running one: the strict keys refuse a difference, the keys a load verifies by rebuilding note it. A
        // save made before MMR-48 recorded no build keys: its header's game and War Sails versions are compared instead.
        private static void Build(Facts f, Action<string, string, string, string> row, Action<string, string, string> refuse)
        {
            var saved = f.SavedBuild ?? new Dictionary<string, string>();
            if (f.RunningBuild == null) { row("build keys", NotChecked, "the running build was not given", null); return; }
            if (saved.Count == 0)
            {
                row("build keys", Verified, "not recorded (a save made before MMR-48): its header's game and War Sails versions are compared, and the world's reconstruction is verified at load", null);
                // As LoadPolicy.LegacyCompatibility: a header that records neither version is not compared.
                if (f.LegacyBuild != null) foreach (var key in new[] { "game", "naval" }) Strict(key, f.LegacyBuild, f.RunningBuild, " (from the header)", row, refuse);
                return;
            }
            foreach (var key in LoadPolicy.StrictKeys) Strict(key, saved, f.RunningBuild, "", row, refuse);
            foreach (var key in LoadPolicy.RebuiltKeys)
            {
                string a, b; saved.TryGetValue(key, out a); f.RunningBuild.TryGetValue(key, out b);
                if (b == null) { row("build " + key, NotChecked, "saved " + (a ?? "(none)") + "; the running value was not given", null); continue; }
                if (a == b) { row("build " + key, Same, a, null); continue; }
                if (key == "navigationCache") row("build navigationCache", Rebuild, "saved under " + (a ?? "(none)") + ", running " + b + ": the engine rebuilds the navigation caches from the frozen navmesh", null);
                else if (key == "backend")
                {
                    string pa = Producer(a), pb = Producer(b);
                    row("build backend", Verified, "saved " + (a ?? "(none)") + ", running " + b + ": the scene and campaign are rebuilt from the frozen bundle and must equal the package's", null);
                    if (pa != null && pb != null && pa != pb) row("producer", Frozen, "the world was generated by producer " + pa + "; this build generates new worlds with " + pb + " and reads this world's bundle as it is", null);
                }
                else row("build " + key, Verified, "saved " + (a ?? "(none)") + ", running " + b + ": the rebuilt world is verified at load", null);
            }
        }

        private static void Strict(string key, IDictionary<string, string> saved, IDictionary<string, string> running, string source, Action<string, string, string, string> row, Action<string, string, string> refuse)
        {
            string a, b; saved.TryGetValue(key, out a); running.TryGetValue(key, out b);
            if (b == null) { row("build " + key, NotChecked, "saved " + (a ?? "(none)") + source + "; the running value was not given", null); return; }
            if (a == b) { row("build " + key, Same, a + source, null); return; }
            if (key == "naval" && (b == "absent" || b == "(none)") && a != null && a != "absent") refuse("build naval", LoadPolicy.IncompatibleBuild, "this campaign was saved with War Sails " + a + ", which is not installed");
            else refuse("build " + key, LoadPolicy.IncompatibleBuild, key + ": saved " + (a ?? "(none)") + source + ", running " + b);
        }

        // The immutable world (package, bundle) and the derived resources built from it.
        private static void World(Facts f, bool compiled, Action<string, string, string, string> row, Action<string, string, string> refuse, Decision d)
        {
            var b = f.Binding;
            if (f.FilesProblem != null) { refuse("world files", LoadPolicy.Failed, "a world file cannot be read now: " + f.FilesProblem); return; }
            if (f.PackagePresent == false) refuse("world package", LoadPolicy.MissingWorld, "no world package " + b.PackageName + " (copy the save with its world)");
            else if (f.PackageProblems != null && f.PackageProblems.Count > 0) refuse("world package", LoadPolicy.DamagedWorld, b.PackageName + ": " + String.Join("; ", f.PackageProblems.Take(5)));
            else if (f.ManifestProblem != null) refuse("world package", LoadPolicy.DamagedWorld, b.PackageName + ": its world.json is not a package manifest (" + f.ManifestProblem + ")");
            else row("world package", f.PackagePresent == true && f.PackageProblems != null ? Same : NotChecked, b.PackageName, null);
            if (f.PackageScene != null && (f.HeaderScene != f.PackageScene || f.HeaderSeed != f.PackageSeed))
                refuse("world identity", LoadPolicy.DamagedSave, "the save's header and its world package name different worlds");
            if (!compiled)
            {
                row("data bundle", Same, "none: a fixture world, whose scene is installed with the module (never rebuilt)", null);
                if (f.Scene == LoadPolicy.SceneState.Missing || f.Scene == LoadPolicy.SceneState.Different)
                    refuse("scene", LoadPolicy.NotRebuildable, "the fixture world's installed scene " + f.SceneName + " is " + (f.Scene == LoadPolicy.SceneState.Missing ? "missing" : "different") + " and cannot be rebuilt");
                return;
            }
            if (f.BundlePresent == false) refuse("data bundle", LoadPolicy.MissingWorld, "the world's data bundle " + Short(b.ContentId) + " is missing: a missing definition bundle is not a cache miss, and a world is never regenerated from its seed");
            else if (f.BundleProblems != null && f.BundleProblems.Count > 0)
            {
                string kind = WorldInspector.Classify(f.BundleProblems);
                refuse("data bundle", kind == WorldInspector.Missing ? LoadPolicy.MissingWorld : kind == WorldInspector.Incompatible ? LoadPolicy.IncompatibleBuild : LoadPolicy.DamagedWorld,
                    Short(b.ContentId) + ": " + String.Join("; ", f.BundleProblems.Take(5)));
            }
            else row("data bundle", f.BundlePresent == true && f.BundleProblems != null ? Same : NotChecked, Short(b.ContentId), null);
            // The derived resources.
            if (f.Scene == null) row("scene", NotChecked, f.SceneName ?? "unknown", null);
            else if (f.Scene == LoadPolicy.SceneState.Installed) row("scene", Same, f.SceneName + ": installed, every file the package's", null);
            else if (f.Scene == LoadPolicy.SceneState.Missing)
            {
                bool tested = SameEncoder(f);
                d.Rows.Add(new Row { Component = "scene", Verdict = Rebuild, Conditional = !tested,
                    Detail = f.SceneName + ": swept; re-encoded from the bundle by the surface encoder and installed only when every file is the package's" + (tested ? ""
                        : " (this build's module, assembly or backend differ from the one that encoded it, or were not recorded: the re-encoding is verified at load and refused, not-rebuildable, if it differs)") });
            }
            else refuse("scene", LoadPolicy.NotRebuildable, "the installed scene " + f.SceneName + " differs from the package's (an installed scene is never rewritten)");
            foreach (var report in Reports)
            {
                string state = null; if (f.ReportState != null) f.ReportState.TryGetValue(report, out state);
                if (report == "settlement-layout.json" && f.Scene == LoadPolicy.SceneState.Missing)
                    row("report " + report, Rebuild, "re-derived by the surface encoder with the scene" + (state == "kept" || state == "installed" ? ", and checked against the layout the campaign was activated with" : ""), null);
                else if (state == null) row("report " + report, NotChecked, "not checked", null);
                else if (state == "installed") row("report " + report, Same, "installed" + (report == "settlement-layout.json" ? "" : "; its gate runs at load"), null);
                else if (state == "kept") row("report " + report, Rebuild, "restored from the copy the campaign's activation kept" + (report == "settlement-layout.json" ? "" : "; its gate runs at load"), null);
                else refuse("report " + report, LoadPolicy.NotRebuildable, "the world's " + report + " is missing and was not kept with the campaign");
            }
            if (f.LayoutProblem != null) refuse("report settlement-layout.json", LoadPolicy.NotRebuildable, f.LayoutProblem);
        }

        // The scene's encoder is the one that encoded the world: the save recorded the module, its assembly and the backend, and they are the
        // running build's.
        private static bool SameEncoder(Facts f)
        {
            if (f.SavedBuild == null || f.RunningBuild == null || f.SavedBuild.Count == 0) return false;
            foreach (var key in EncoderKeys)
            {
                string a, b; f.SavedBuild.TryGetValue(key, out a); f.RunningBuild.TryGetValue(key, out b);
                if (a == null || a != b) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- facts from files

        // The facts of one save from files: savePath the .sav; store the module's store (worlds/, bundles/, activations/, world-navigation/,
        // world-playability/, world-settlements/); sceneStore the module's SceneObj folder (null: the scene and reports are not checked);
        // bundleProblems the bundle verifier (null: WorldDataBundle.Problems for host). Reads only; a world file another program holds is a fact
        // (FilesProblem), never an exception.
        public static Facts Gather(string savePath, string store, string sceneStore, WorldDataBundle.HostProfile host, IDictionary<string, string> runningBuild,
            Func<string, IList<string>> bundleProblems = null)
        {
            var f = new Facts { Save = Path.GetFileNameWithoutExtension(savePath), RunningBuild = runningBuild };
            if (!File.Exists(savePath)) { f.Header = "absent"; return f; }
            var header = SaveFolder.ReadHeader(f.Save, savePath);
            if (header.HeaderUnknown) { f.Header = "unknown"; f.HeaderProblem = header.HeaderProblem; return f; }
            if (!header.Readable) { f.Header = "torn"; f.HeaderProblem = header.HeaderProblem; return f; }
            f.Header = "readable"; f.BodyWhole = header.BodyWhole; f.BodyProblem = header.BodyProblem;
            if (f.BodyWhole == null) { string problem; f.BodyWhole = SaveFileCheck.Check(savePath, out problem); f.BodyProblem = problem; }
            f.Generated = header.Metadata.ContainsKey(WorldBinding.Prefix + "id");
            try { f.Binding = WorldBinding.FromHeader(header.Metadata); }
            catch (InvalidDataException error) { f.BindingProblem = error.Message; return f; }
            if (f.Binding == null) return f;
            f.SavedBuild = SaveBindingContract.CompatibilityFrom(header.Metadata);
            f.LegacyBuild = LoadPolicy.LegacyBuild(header.Metadata);
            string scene, seed; header.Metadata.TryGetValue(WorldBinding.Prefix + "scene", out scene); header.Metadata.TryGetValue(WorldBinding.Prefix + "seed", out seed);
            f.HeaderScene = scene; f.HeaderSeed = seed;
            try { GatherWorld(f, store, sceneStore, host, bundleProblems); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { f.FilesProblem = error.GetType().Name + ": " + error.Message; }
            return f;
        }

        private static void GatherWorld(Facts f, string store, string sceneStore, WorldDataBundle.HostProfile host, Func<string, IList<string>> bundleProblems)
        {
            string package = Path.Combine(store, "worlds", f.Binding.PackageName);
            f.PackagePresent = Directory.Exists(package);
            if (f.PackagePresent == true) f.PackageProblems = WorldSaveTransfer.PackageProblems(package, f.Binding.ManifestSha256, f.Binding.WorldId);
            bool compiled = f.Binding.ContentId != WorldBinding.NoContent;
            if (compiled)
            {
                string bundle = Path.Combine(store, "bundles", f.Binding.ContentId);
                f.BundlePresent = Directory.Exists(bundle);
                if (f.BundlePresent == true) f.BundleProblems = bundleProblems != null ? bundleProblems(bundle) : WorldDataBundle.Problems(bundle, host, true);
            }
            if (f.PackagePresent != true || f.PackageProblems == null || f.PackageProblems.Count > 0) return;
            // The manifest as the loader reads it: its scene, seed, scene files and documents must be there.
            Dictionary<string, object> manifest = null;
            IDictionary<string, object> sceneFiles = null;
            try
            {
                manifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(package, "world.json")));
                f.PackageScene = Convert.ToString(manifest["sceneName"], CultureInfo.InvariantCulture);
                f.PackageSeed = Convert.ToString(manifest["seed"], CultureInfo.InvariantCulture);
                sceneFiles = (IDictionary<string, object>)manifest["sceneFiles"];
                if (!(manifest["documents"] is IEnumerable)) throw new InvalidCastException("documents is not a list");
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is InvalidCastException || error is KeyNotFoundException || error is NullReferenceException)
            {
                f.ManifestProblem = error.Message; f.PackageScene = null; return;
            }
            f.SceneName = f.PackageScene;
            if (sceneStore == null) return;
            var expected = sceneFiles.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            string folder = Path.Combine(sceneStore, f.SceneName);
            var installed = expected.Keys.ToDictionary(k => k, k => File.Exists(Path.Combine(folder, k)) ? WorldSaveTransfer.Sha256File(Path.Combine(folder, k)) : null, StringComparer.Ordinal);
            f.Scene = LoadPolicy.Scene(expected, installed);
            if (!compiled) return;
            var paths = new Dictionary<string, string> { ["navigation.json"] = Path.Combine(store, "world-navigation", f.SceneName + ".json"),
                ["settlement-layout.json"] = Path.Combine(store, "world-settlements", f.SceneName + ".json"), ["playability.json"] = Path.Combine(store, "world-playability", f.SceneName + ".json") };
            f.ReportState = new Dictionary<string, string>(StringComparer.Ordinal);
            string layoutPath = null;
            foreach (var kv in paths)
            {
                string kept = ValidInstance(f.Binding.Instance) ? WorldSaveTransfer.EvidencePath(store, f.Binding.Instance, kv.Key) : null;
                f.ReportState[kv.Key] = File.Exists(kv.Value) ? "installed" : kept != null && File.Exists(kept) ? "kept" : "missing";
                if (kv.Key == "settlement-layout.json") layoutPath = File.Exists(kv.Value) ? kv.Value : kept != null && File.Exists(kept) ? kept : null;
            }
            // The layout the load reads when the scene is installed: this world's and activatable, as the loader requires.
            if (layoutPath != null && f.Scene == LoadPolicy.SceneState.Installed)
            {
                IDictionary<string, object> layout = null;
                try { layout = Json.DeserializeObject(File.ReadAllText(layoutPath)) as IDictionary<string, object>; }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { f.LayoutProblem = "the settlement layout report cannot be read: " + error.Message; return; }
                object content, activatable;
                if (layout == null || !layout.TryGetValue("worldContentId", out content) || !Equals(content, f.Binding.ContentId) || !layout.TryGetValue("activatable", out activatable) || !Equals(activatable, true))
                    f.LayoutProblem = "the settlement layout report is not this world's";
            }
        }

        // The producer lock a backend key names ("...; producerLock <12 hex>"), or null.
        private static string Producer(string backend)
        {
            const string marker = "producerLock ";
            int at = backend == null ? -1 : backend.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : backend.Substring(at + marker.Length).Trim();
        }

        private static bool ValidInstance(string instance) { return instance != null && instance.Length == 32 && instance.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        private static string Short(string id) { return id == null ? "(none)" : id.Length > 12 ? id.Substring(0, 12) : id; }
    }
}
