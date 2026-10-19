using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Persistence;

// MMR-50 compatibility decisions, without the game:
//   CompatibilityTests REPO OUT_JSON [BUNDLE_DIR]
// WorldCompatibilityPolicy's three classes (supported, rebuildable derived cache, unsupported world) and each refusal it gives, their
// agreement with the loader's own rules (LoadPolicy), the facts gathered from real temporary stores of synthetic saves, packages and scenes
// (reading changes no byte), the previous-save fixtures, and that no migration is advertised (the ledger lists no converter, no player
// message offers one, a real bundle built for another game version is refused without one: BUNDLE_DIR).
internal static class CompatibilityTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: CompatibilityTests REPO OUT_JSON [BUNDLE_DIR]"); return 2; }
        string scratch = Path.Combine(Path.GetTempPath(), "mnbl-compat-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        try { Run(args[0], scratch, args.Length > 2 ? args[2] : null); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { } }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.compatibility-tests", ["version"] = 1, ["ticket"] = "MMR-50", ["checks"] = checks, ["failures"] = failures, ["lines"] = lines };
        File.WriteAllText(args[1], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> Build(string game = "v1.4.8.119303", string naval = "v1.2.8.119303", string producer = "000000000000")
    {
        return new Dictionary<string, string> { ["contract"] = SaveBindingContract.Contract, ["module"] = "v0.0.1.0", ["moduleAssemblySha256"] = new string('a', 64), ["game"] = game, ["naval"] = naval,
            ["backend"] = "mnbl.runtime-world-backend/R07; producerLock " + producer, ["navigationCache"] = "mnbl.navigation-cache-key/1; adapter mmr41/1", ["saveIds"] = "7910000/v1", ["worldIdentity"] = WorldBinding.IdentitySchema };
    }

    private static readonly string Instance = new string('c', 32), Content = new string('b', 64), Manifest = new string('a', 64);

    // A compiled world's save whose every fact is usable as it is.
    private static WorldCompatibilityPolicy.Facts Facts()
    {
        return new WorldCompatibilityPolicy.Facts {
            Save = "test", Header = "readable", Generated = true, Binding = new WorldBinding { WorldId = "mnbl_00050001", ManifestSha256 = Manifest, ContentId = Content, Instance = Instance },
            BodyWhole = true, SavedBuild = Build(), RunningBuild = Build(), PackagePresent = true, PackageProblems = new List<string>(), BundlePresent = true, BundleProblems = new List<string>(),
            Scene = LoadPolicy.SceneState.Installed, SceneName = "mnbl_world_bbbbbbbbbbbbbbbb_000000000000",
            ReportState = WorldCompatibilityPolicy.Reports.ToDictionary(r => r, r => "installed") };
    }

    private static WorldCompatibilityPolicy.Decision Decide(Action<WorldCompatibilityPolicy.Facts> change) { var f = Facts(); change(f); return WorldCompatibilityPolicy.Evaluate(f); }
    private static bool Refused(WorldCompatibilityPolicy.Decision d, string code, string contains = null)
    {
        return d.Class == WorldCompatibilityPolicy.Unsupported && d.Refusal != null && d.Refusal.Code == code && (contains == null || d.Refusal.Detail.Contains(contains));
    }
    private static string Row(WorldCompatibilityPolicy.Decision d, string component) { var r = d.Rows.FirstOrDefault(x => x.Component == component); return r == null ? null : r.Verdict; }

    private static void Run(string repo, string scratch, string bundleDir)
    {
        // ---- the three classes
        var supported = WorldCompatibilityPolicy.Evaluate(Facts());
        Check(supported.Class == WorldCompatibilityPolicy.Supported && supported.Refusal == null && supported.Rows.All(r => r.Verdict == WorldCompatibilityPolicy.Same),
            "supported: the save, its build, its package and bundle, its scene and reports all usable as they are");
        var old = Decide(f => f.SavedBuild = new Dictionary<string, string>());
        Check(old.Class == WorldCompatibilityPolicy.Supported && Row(old, "build keys") == WorldCompatibilityPolicy.Verified, "supported: a save made before MMR-48 (no build keys) loads, its reconstruction verified at load");
        var updated = Decide(f => { f.RunningBuild["module"] = "v0.0.2.0"; f.RunningBuild["moduleAssemblySha256"] = new string('e', 64); });
        Check(updated.Class == WorldCompatibilityPolicy.Supported && Row(updated, "build module") == WorldCompatibilityPolicy.Verified && Row(updated, "build moduleAssemblySha256") == WorldCompatibilityPolicy.Verified,
            "supported: another module build loads; the rebuilt world is verified at load (documents, scene, frame, climate must be the saved world's)");
        var producer = Decide(f => f.RunningBuild["backend"] = "mnbl.runtime-world-backend/R07; producerLock 111111111111");
        Check(producer.Class == WorldCompatibilityPolicy.Supported && Row(producer, "producer") == WorldCompatibilityPolicy.Frozen && producer.Rows.First(r => r.Component == "producer").Detail.Contains("reads this world's bundle as it is"),
            "c3: a new generator build (another producer lock) makes new worlds and still reads the old world's frozen bundle as it is");
        var cache = Decide(f => f.RunningBuild["navigationCache"] = "mnbl.navigation-cache-key/2; adapter mmr41/2");
        Check(cache.Class == WorldCompatibilityPolicy.Rebuildable && Row(cache, "build navigationCache") == WorldCompatibilityPolicy.Rebuild,
            "rebuildable: another navigation cache key: the engine rebuilds its caches from the frozen navmesh");
        var swept = Decide(f => { f.Scene = LoadPolicy.SceneState.Missing; f.ReportState["navigation.json"] = "kept"; f.ReportState["playability.json"] = "kept"; f.ReportState["settlement-layout.json"] = "missing"; });
        var rebuilds = (IList)swept.Report("test")["rebuilds"];
        Check(swept.Class == WorldCompatibilityPolicy.Rebuildable && rebuilds.Count == 4 && rebuilds.Contains("scene") && rebuilds.Contains("report settlement-layout.json") && ((IList)swept.Report("test")["conditional"]).Count == 0,
            "rebuildable: a swept scene (re-encoded by the surface encoder of the build that encoded it), reports restored from the activation's copies, the layout re-derived with the scene");
        var otherBuild = Decide(f => { f.Scene = LoadPolicy.SceneState.Missing; f.RunningBuild["moduleAssemblySha256"] = new string('e', 64); });
        var conditional = (IList)otherBuild.Report("test")["conditional"];
        Check(otherBuild.Class == WorldCompatibilityPolicy.Rebuildable && conditional.Count == 1 && conditional.Contains("scene") && otherBuild.Rows.First(r => r.Component == "scene").Detail.Contains("refused, not-rebuildable, if it differs"),
            "rebuildable, conditional: a swept scene under another module build is re-encoded by this build's encoder and verified at load (refused if it differs); the report says so");
        var legacySwept = Decide(f => { f.Scene = LoadPolicy.SceneState.Missing; f.SavedBuild = new Dictionary<string, string>(); f.LegacyBuild = new Dictionary<string, string> { ["game"] = "v1.4.8.119303", ["naval"] = "v1.2.8.119303" }; });
        Check(((IList)legacySwept.Report("test")["conditional"]).Contains("scene"), "rebuildable, conditional: a swept scene of a save that recorded no build keys (its encoder unknown)");
        // ---- unsupported worlds, each with the loader's refusal
        Check(Refused(Decide(f => f.Scene = LoadPolicy.SceneState.Different), LoadPolicy.NotRebuildable, "never rewritten"), "unsupported: an installed scene that differs from the package's is never rewritten (not-rebuildable)");
        Check(Refused(Decide(f => f.ReportState["navigation.json"] = "missing"), LoadPolicy.NotRebuildable, "navigation.json"), "unsupported: a navigation report neither installed nor kept (not-rebuildable)");
        foreach (var key in LoadPolicy.StrictKeys)
        {
            var d = Decide(f => f.SavedBuild[key] = "other");
            var loader = LoadPolicy.Compatibility(new Dictionary<string, string>(Facts().SavedBuild) { [key] = "other" }, Build(), null);
            Check(Refused(d, LoadPolicy.IncompatibleBuild, key) && loader != null && loader.Code == LoadPolicy.IncompatibleBuild, "unsupported: another " + key + " (incompatible-build), as the loader decides");
        }
        var noDlc = Decide(f => f.RunningBuild["naval"] = "absent");
        var noDlcLoader = LoadPolicy.Compatibility(Build(), Build(naval: "absent"), null);
        Check(Refused(noDlc, LoadPolicy.IncompatibleBuild, "War Sails v1.2.8.119303, which is not installed") && noDlcLoader != null && noDlcLoader.Detail.Contains("which is not installed"),
            "missing DLC: a War Sails save on a game without War Sails is refused (incompatible-build) saying so, by the policy and the loader alike");
        Check(Refused(Decide(f => f.SavedBuild["naval"] = "v1.3.0.120000"), LoadPolicy.IncompatibleBuild, "naval: saved v1.3.0.120000"), "unsupported: a save needing a War Sails version that is not installed");
        Check(Refused(Decide(f => f.PackagePresent = false), LoadPolicy.MissingWorld), "unsupported: no world package (missing-world)");
        Check(Refused(Decide(f => f.PackageProblems = new List<string> { "world.json does not hash to the binding's manifest hash" }), LoadPolicy.DamagedWorld, "does not hash"), "unsupported: a package whose hash is not the binding's (damaged-world)");
        var noBundle = Decide(f => f.BundlePresent = false);
        Check(Refused(noBundle, LoadPolicy.MissingWorld, "never regenerated from its seed") && noBundle.Refusal.Detail.Contains("not a cache miss"),
            "unsupported: a missing data bundle is a missing world, not a cache miss, and the world is never regenerated from its seed");
        Check(Refused(Decide(f => f.BundleProblems = new List<string> { "built for Bannerlord v1.4.7.1, installed v1.4.8.119303" }), LoadPolicy.IncompatibleBuild), "unsupported: a bundle built for another game version (incompatible-build)");
        Check(Refused(Decide(f => f.BundleProblems = new List<string> { "compiled/terrain/heightfield.bin does not have its listed hash" }), LoadPolicy.DamagedWorld), "unsupported: a bundle whose bytes changed (damaged-world)");
        Check(Refused(Decide(f => f.BundleProblems = new List<string> { "missing bundle " + Content }), LoadPolicy.MissingWorld), "unsupported: OpenForSave's missing bundle is missing (MMR-49 run 11568)");
        Check(Refused(Decide(f => f.Header = "unknown"), LoadPolicy.Unavailable) && Refused(Decide(f => f.Header = "torn"), LoadPolicy.DamagedSave) && Refused(Decide(f => f.Header = "absent"), LoadPolicy.DamagedSave),
            "unsupported: a header another program holds (save-unavailable), a torn header or no save (damaged-save)");
        Check(Refused(Decide(f => f.BindingProblem = "mnbl.world.contentId is not a content id"), LoadPolicy.DamagedSave) && Refused(Decide(f => f.BodyWhole = false), LoadPolicy.DamagedSave)
              && Refused(Decide(f => f.Binding.Instance = "not-an-instance"), LoadPolicy.DamagedSave) && Refused(Decide(f => { f.Generated = false; f.Binding = null; }), LoadPolicy.NotGenerated),
            "unsupported: a malformed binding, torn game data or an invalid campaign instance (damaged-save); no binding (not-generated)");
        Check(Refused(Decide(f => { f.SavedBuild["game"] = "v1.4.7.1"; f.BundlePresent = false; }), LoadPolicy.IncompatibleBuild),
            "precedence: the build is checked before the world, as the loader does (another game version with a missing bundle refuses as incompatible-build)");
        Check(Refused(Decide(f => { f.SavedBuild["game"] = "v1.4.7.1"; f.Binding.Instance = null; }), LoadPolicy.IncompatibleBuild),
            "precedence: the build is checked before the campaign instance, as the loader does (review m1)");
        Check(Refused(Decide(f => f.BodyWhole = null), LoadPolicy.Unavailable), "unsupported: game data another program holds is not damage (save-unavailable), as the loader decides (review M4)");
        Check(Refused(Decide(f => { f.HeaderScene = "a"; f.HeaderSeed = "1"; f.PackageScene = "b"; f.PackageSeed = "1"; }), LoadPolicy.DamagedSave, "different worlds")
              && Decide(f => { f.HeaderScene = "a"; f.HeaderSeed = "1"; f.PackageScene = "a"; f.PackageSeed = "1"; }).Class == WorldCompatibilityPolicy.Supported,
            "unsupported: a header naming another world (scene or seed) than its package (damaged-save), as the loader decides");
        Check(Refused(Decide(f => f.ManifestProblem = "The given key was not present"), LoadPolicy.DamagedWorld, "not a package manifest"), "unsupported: a package manifest that hashes right but lacks its scene, seed, scene files or documents (damaged-world)");
        Check(Refused(Decide(f => f.LayoutProblem = "the settlement layout report is not this world's"), LoadPolicy.NotRebuildable), "unsupported: a settlement layout of another world (not-rebuildable)");
        Check(Refused(Decide(f => f.FilesProblem = "IOException: in use"), LoadPolicy.Failed, "cannot be read now"), "unsupported: a world file another program holds (load-failed, nothing changed), as the loader's unexpected-error refusal");
        // A save made before MMR-48: its header's game and War Sails versions.
        Func<string, string, WorldCompatibilityPolicy.Decision> legacy = (game, naval) => Decide(f => { f.SavedBuild = new Dictionary<string, string>(); f.LegacyBuild = new Dictionary<string, string> { ["game"] = game, ["naval"] = naval }; });
        var legacyLoader = LoadPolicy.LegacyCompatibility(new Dictionary<string, string> { ["game"] = "v1.4.7.1", ["naval"] = "v1.2.8.119303" }, Build(), null);
        Check(legacy("v1.4.8.119303", "v1.2.8.119303").Class == WorldCompatibilityPolicy.Supported && Refused(legacy("v1.4.7.1", "v1.2.8.119303"), LoadPolicy.IncompatibleBuild, "from the header")
              && legacyLoader != null && legacyLoader.Code == LoadPolicy.IncompatibleBuild && LoadPolicy.LegacyCompatibility(new Dictionary<string, string> { ["game"] = "v1.4.8.119303", ["naval"] = "v1.2.8.119303" }, Build(), null) == null,
            "a save made before MMR-48: its header's game version is compared (supported when equal, incompatible-build otherwise), by the policy and the loader alike (review m6)");
        Check(Refused(Decide(f => { f.SavedBuild = new Dictionary<string, string>(); f.LegacyBuild = new Dictionary<string, string> { ["game"] = "v1.4.8.119303", ["naval"] = "v1.2.8.119303" }; f.RunningBuild["naval"] = "absent"; }), LoadPolicy.IncompatibleBuild, "which is not installed")
              && LoadPolicy.LegacyBuild(new Dictionary<string, string> { ["ApplicationVersion"] = "v1.4.8.119303" })["naval"] == "absent",
            "a save made before MMR-48 with War Sails, on a game without it: refused saying War Sails is not installed; a header without Module_NavalDLC was made without it");
        var fixture = Decide(f => { f.Binding.ContentId = WorldBinding.NoContent; f.BundlePresent = null; f.BundleProblems = null; f.ReportState = null; });
        Check(fixture.Class == WorldCompatibilityPolicy.Supported && Refused(Decide(f => { f.Binding.ContentId = WorldBinding.NoContent; f.Scene = LoadPolicy.SceneState.Missing; }), LoadPolicy.NotRebuildable),
            "fixture worlds: supported with their installed scene; a missing fixture scene cannot be rebuilt (not-rebuildable)");
        var all = new[] { supported, old, cache, swept, noBundle, noDlc };
        Check(all.All(d => (string)d.Report("x")["migration"] == WorldCompatibilityPolicy.Migration && WorldCompatibilityPolicy.Migration.StartsWith("none", StringComparison.Ordinal))
              && all.Select(d => d.Class).Distinct().OrderBy(c => c).SequenceEqual(new[] { WorldCompatibilityPolicy.Rebuildable, WorldCompatibilityPolicy.Supported, WorldCompatibilityPolicy.Unsupported }),
            "c1/c3: every decision is one of the three classes, and every report says no migration is offered");
        // ---- the facts from files (real temporary stores)
        Files(scratch);
        // ---- previous-save fixtures
        Previous(repo);
        // ---- no migration advertised
        NoMigration(repo, bundleDir);
    }

    private static void Files(string scratch)
    {
        string store = Path.Combine(scratch, "store"), scenes = Path.Combine(scratch, "SceneObj"), saves = Path.Combine(scratch, "saves");
        Directory.CreateDirectory(store); Directory.CreateDirectory(scenes); Directory.CreateDirectory(saves);
        string scene = "mnbl_world_" + new string('b', 16) + "_0123456789ab";
        var sceneFiles = new Dictionary<string, byte[]> { ["scene.xscene"] = Encoding.UTF8.GetBytes("<scene/>"), ["navmesh.bin"] = new byte[] { 1, 2, 3 }, ["terrain.bin"] = new byte[] { 4, 5, 6, 7 } };
        Directory.CreateDirectory(Path.Combine(scenes, scene));
        foreach (var f in sceneFiles) File.WriteAllBytes(Path.Combine(scenes, scene, f.Key), f.Value);
        // The package: world.json lists its files with their hashes, its scene and the scene's files.
        var files = new Dictionary<string, byte[]> { ["placement.json"] = Encoding.UTF8.GetBytes("{}"), ["documents/Settlements.xml"] = Encoding.UTF8.GetBytes("<Settlements/>") };
        var manifest = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["schema"] = WorldSaveTransfer.PackageSchema, ["worldId"] = "mnbl_00050001", ["seed"] = 42,
            ["files"] = files.ToDictionary(k => k.Key, k => (object)Sha(k.Value)), ["sceneName"] = scene, ["sceneFiles"] = sceneFiles.ToDictionary(k => k.Key, k => (object)Sha(k.Value)), ["documents"] = new List<object>() }));
        var binding = new WorldBinding { WorldId = "mnbl_00050001", ManifestSha256 = Sha(manifest), ContentId = Content, Instance = Instance };
        string package = Path.Combine(store, "worlds", binding.PackageName);
        foreach (var f in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(package, f.Key))); File.WriteAllBytes(Path.Combine(package, f.Key), f.Value); }
        File.WriteAllBytes(Path.Combine(package, "world.json"), manifest);
        Directory.CreateDirectory(Path.Combine(store, "bundles", Content)); File.WriteAllText(Path.Combine(store, "bundles", Content, "manifest.json"), "{}");
        foreach (var r in new[] { "world-navigation", "world-settlements", "world-playability" }) { Directory.CreateDirectory(Path.Combine(store, r)); File.WriteAllText(Path.Combine(store, r, scene + ".json"), "{}"); }
        string layoutJson = Json.Serialize(new Dictionary<string, object> { ["worldContentId"] = Content, ["activatable"] = true });
        File.WriteAllText(Path.Combine(store, "world-settlements", scene + ".json"), layoutJson);
        foreach (var r in WorldCompatibilityPolicy.Reports) { string kept = WorldSaveTransfer.EvidencePath(store, Instance, r); Directory.CreateDirectory(Path.GetDirectoryName(kept)); File.WriteAllText(kept, "{}"); }
        var header = new Dictionary<string, string>(StringComparer.Ordinal) { ["ApplicationVersion"] = "v1.4.8.119303", ["mnbl.world.schema"] = WorldBinding.IdentitySchema, ["mnbl.world.id"] = binding.WorldId,
            ["mnbl.world.manifestSha256"] = binding.ManifestSha256, ["mnbl.world.package"] = binding.PackageName, ["mnbl.world.contentId"] = binding.ContentId, ["mnbl.world.instance"] = binding.Instance,
            ["mnbl.world.scene"] = scene, ["mnbl.world.seed"] = "42" };
        foreach (var e in Build()) header[SaveBindingContract.Prefix + e.Key] = e.Value;
        string save = Path.Combine(saves, "campaign.sav");
        File.WriteAllBytes(save, SaveBytes(header, 7));
        Func<string, IList<string>> fakeBundle = d => File.Exists(Path.Combine(d, "manifest.json")) ? new List<string>() : new List<string> { "not a bundle directory" };
        Func<WorldCompatibilityPolicy.Decision> decide = () => WorldCompatibilityPolicy.Evaluate(WorldCompatibilityPolicy.Gather(save, store, scenes, null, Build(), fakeBundle));
        string before = Tree(scratch);
        var d1 = decide();
        Check(d1.Class == WorldCompatibilityPolicy.Supported && Row(d1, "save data") == WorldCompatibilityPolicy.Same && Row(d1, "scene") == WorldCompatibilityPolicy.Same,
            "from files: a bound, whole save with its package, bundle, installed scene and reports is supported");
        Check(Tree(scratch) == before, "from files: gathering the facts reads only (every save, package, bundle, scene and report byte unchanged)");
        using (new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // a writer holds the save, as a sync tool or the game's writer may
            Check(Refused(decide(), LoadPolicy.Unavailable), "from files: a save another program holds open for writing: unsupported (save-unavailable), not damaged");
        string world = Path.Combine(package, "world.json");
        using (new FileStream(world, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Refused(decide(), LoadPolicy.Failed, "cannot be read now"), "from files: a world file another program holds: unsupported (load-failed), and the gathering does not throw");
        File.WriteAllText(Path.Combine(store, "world-settlements", scene + ".json"), Json.Serialize(new Dictionary<string, object> { ["worldContentId"] = new string('f', 64), ["activatable"] = true }));
        Check(Refused(decide(), LoadPolicy.NotRebuildable, "not this world's"), "from files: a settlement layout of another world: unsupported (not-rebuildable)");
        File.WriteAllText(Path.Combine(store, "world-settlements", scene + ".json"), layoutJson);
        foreach (var r in new[] { "world-navigation", "world-settlements", "world-playability" }) File.Delete(Path.Combine(store, r, scene + ".json"));
        Directory.Delete(Path.Combine(scenes, scene), true);
        var d2 = decide();
        Check(d2.Class == WorldCompatibilityPolicy.Rebuildable && Row(d2, "scene") == WorldCompatibilityPolicy.Rebuild && Row(d2, "report navigation.json") == WorldCompatibilityPolicy.Rebuild,
            "from files: the scene and reports swept, the activation's copies kept: rebuildable (re-encoded, restored)");
        File.Delete(WorldSaveTransfer.EvidencePath(store, Instance, "navigation.json"));
        Check(Refused(decide(), LoadPolicy.NotRebuildable, "navigation.json"), "from files: swept and the navigation report not kept: unsupported (not-rebuildable)");
        File.AppendAllText(Path.Combine(package, "placement.json"), " ");
        Check(Refused(decide(), LoadPolicy.DamagedWorld), "from files: a package file whose hash changed: unsupported (damaged-world)");
        Directory.Delete(Path.Combine(store, "bundles", Content), true);
        Directory.Delete(package, true);
        Check(Refused(decide(), LoadPolicy.MissingWorld), "from files: the package gone: unsupported (missing-world)");
        var whole = File.ReadAllBytes(save);
        File.WriteAllBytes(save, whole.Take(whole.Length - 600).ToArray());
        Check(Refused(decide(), LoadPolicy.DamagedSave), "from files: torn game data: unsupported (damaged-save)");
        File.WriteAllBytes(save, whole.Take(10).ToArray());
        Check(Refused(decide(), LoadPolicy.DamagedSave), "from files: a torn header: unsupported (damaged-save)");
        File.Delete(save);
        Check(Refused(decide(), LoadPolicy.DamagedSave, "not in the save folder"), "from files: no save: unsupported (damaged-save)");
    }

    // The checked-in previous-save headers: what this build decides for them.
    private static void Previous(string repo)
    {
        string folder = Path.Combine(repo, "tests", "Persistence", "fixtures", "previous-saves");
        var headers = Directory.GetFiles(folder, "*.header.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Check(headers.Count >= 1, "previous saves: " + headers.Count + " checked-in header fixtures");
        foreach (var path in headers)
        {
            var doc = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(path));
            var metadata = ((Dictionary<string, object>)doc["metadata"]).ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value), StringComparer.Ordinal);
            var binding = WorldBinding.FromHeader(metadata);
            var saved = SaveBindingContract.CompatibilityFrom(metadata);
            // The world present and usable as it is: only the save's own format decides.
            var f = Facts(); f.Binding = binding; f.SavedBuild = saved; f.LegacyBuild = LoadPolicy.LegacyBuild(metadata);
            string name = Path.GetFileName(path);
            var same = WorldCompatibilityPolicy.Evaluate(f);
            Check(binding != null && same.Class == WorldCompatibilityPolicy.Supported, "previous save " + name + ": readable by this build, supported (" + (saved.Count == 0 ? "no build keys: verified at load" : "its build keys") + ")");
            var f2 = Facts(); f2.Binding = binding; f2.SavedBuild = saved; f2.LegacyBuild = LoadPolicy.LegacyBuild(metadata);
            f2.RunningBuild = saved.Count > 0 ? new Dictionary<string, string>(saved) : Build(); f2.RunningBuild["game"] = "v1.4.9.999999";
            Check(Refused(WorldCompatibilityPolicy.Evaluate(f2), LoadPolicy.IncompatibleBuild), "previous save " + name + ": refused by a build for another game version" + (saved.Count == 0 ? " (compared from its header)" : ""));
        }
    }

    private static void NoMigration(string repo, string bundleDir)
    {
        var ledger = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen", "contracts", "save-type-ledger.json")));
        Check(((IList)ledger["converters"]).Count == 0 && ((string)ledger["converterRule"]).Contains("refused") && ((string)ledger["converterRule"]).Contains("never converted"),
            "c3: the save type ledger lists no converter, and its rule refuses (never converts) a save of another save format");
        var offers = new Regex("migration tool|convert it with|migrate (the|your) (save|world)|run the migration", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(repo, "src", "MNBL.ProceduralWorld"), "*.cs", SearchOption.AllDirectories))
            foreach (Match literal in Regex.Matches(File.ReadAllText(file), "\"(?:[^\"\\\\]|\\\\.)*\""))
                if (offers.IsMatch(literal.Value)) found.Add(Path.GetFileName(file) + ": " + literal.Value);
        Check(found.Count == 0, "c3: no message of the module offers a migration" + (found.Count == 0 ? "" : " (" + String.Join("; ", found.Take(3)) + ")"));
        if (bundleDir == null) { Check(false, "the real bundle for the refusal text was not given"); return; }
        string message = null;
        try
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(bundleDir).TrimEnd(Path.DirectorySeparatorChar)), id = Path.GetFileName(Path.GetFullPath(bundleDir).TrimEnd(Path.DirectorySeparatorChar));
            WorldDataBundle.OpenForSave(root, new Dictionary<string, string> { ["schema"] = WorldDataBundle.SaveIdentitySchema, ["worldContentId"] = id },
                new WorldDataBundle.HostProfile("v1.4.9.999999", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" }));
        }
        catch (WorldDataBundle.RejectedException error) { message = error.Message; }
        Check(message != null && message.Contains("built for") && message.Contains("there is no migration") && !offers.IsMatch(message),
            "c3: a real bundle built for another game version is refused saying there is no migration (no tool offered)");
    }

    // ---- helpers

    // A save in the installed format: Int32 header length, JSON {"List": metadata}, then raw deflated game data.
    private static byte[] SaveBytes(IDictionary<string, string> metadata, int seed)
    {
        var random = new Random(seed);
        Func<int, byte[]> bytes = n => { var b = new byte[n]; random.NextBytes(b); return b; };
        using (var file = new MemoryStream())
        {
            var json = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["List"] = metadata.ToDictionary(k => k.Key, k => (object)k.Value) }));
            file.Write(BitConverter.GetBytes(json.Length), 0, 4); file.Write(json, 0, json.Length);
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(deflate))
            {
                var h = bytes(200); writer.Write(h.Length); writer.Write(h);
                writer.Write(12); for (int i = 0; i < 12; i++) { var o = bytes(random.Next(10, 3000)); writer.Write(o.Length); writer.Write(o); }
                writer.Write(3); for (int i = 0; i < 3; i++) { var c = bytes(random.Next(10, 900)); writer.Write(c.Length); writer.Write(c); }
                var s = bytes(1500); writer.Write(s.Length); writer.Write(s);
            }
            return file.ToArray();
        }
    }

    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private static string Tree(string root)
    {
        var text = new StringBuilder();
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) text.Append(f.Substring(root.Length)).Append('=').Append(Sha(File.ReadAllBytes(f))).Append('\n');
        return Sha(Encoding.UTF8.GetBytes(text.ToString()));
    }
}
