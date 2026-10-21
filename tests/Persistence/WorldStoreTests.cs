using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;

// MMR-282 world store tests, without the game:
//   WorldStoreTests REPO OUT_JSON
// The sweep over a real temporary store (scene store, scene archive, settlement reports, navigation caches): it keeps every scene a world
// package, a bound save (one written by an earlier MNBL build) or this process names, deletes only orphans and staging debris, and deletes
// nothing while the references cannot all be read; the scene archive (kept, restored after a module update, never rewritten, a tampered copy
// refused); a binding of an earlier MNBL build whose scene was kept still passes the load's build and scene checks; the cache forecast (reuse,
// another module build, another game, measured time); the wait model against the measured worlds in worldgen/policies/one-shot-budget.json.
internal static class WorldStoreTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: WorldStoreTests REPO OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.world-store-tests", ["version"] = 1, ["ticket"] = "MMR-282" };
        string scratch = Path.Combine(Path.GetTempPath(), "mnbl-mmr282-" + Guid.NewGuid().ToString("N"));
        try { Sweep(scratch); Archive(scratch); EarlierBuild(scratch); Forecast(); Model(args[0], evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception) { } }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[1], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private sealed class FakeCatalog : ISaveCatalog
    {
        public readonly Dictionary<string, SaveHeader> Saves = new Dictionary<string, SaveHeader>(StringComparer.Ordinal);
        public IList<SaveHeader> List() { return Saves.Values.ToList(); }
        public SaveHeader Read(string name) { SaveHeader h; return Saves.TryGetValue(name, out h) ? h : null; }
        public bool Delete(string name) { return Saves.Remove(name); }
        public void Flush(string name) { }
        public void Bound(string name, WorldBinding b, string scene)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["ApplicationVersion"] = "v1.4.8.119303", ["mnbl.world.id"] = b.WorldId, ["mnbl.world.manifestSha256"] = b.ManifestSha256,
                ["mnbl.world.contentId"] = b.ContentId, ["mnbl.world.instance"] = b.Instance };
            if (scene != null) metadata["mnbl.world.scene"] = scene;
            Saves[name] = new SaveHeader { Name = name, Metadata = metadata, BodyWhole = true };
        }
    }

    private static string Scene(char c) { return "mnbl_world_" + new string(c, 16) + "_" + new string(c, 12); }
    private static readonly string[] SceneFileNames = { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" };

    private sealed class Store
    {
        public string Root, Scenes, Archive, Reports, Caches, Packages;
        public FakeCatalog Catalog = new FakeCatalog();
        public WorldStoreLayout Layout { get { return new WorldStoreLayout { SceneStore = Scenes, SceneArchive = Archive, SettlementReports = Reports, NavigationCaches = Caches, ReportPath = Path.Combine(Archive, "sweep.json") }; } }
        public static Store Make(string scratch, string name)
        {
            var s = new Store { Root = Path.Combine(scratch, name) };
            s.Scenes = Path.Combine(s.Root, "SceneObj"); s.Archive = Path.Combine(s.Root, "store", "world-scenes"); s.Reports = Path.Combine(s.Root, "store", "world-settlements");
            s.Caches = Path.Combine(s.Root, "store", "navigation-caches"); s.Packages = Path.Combine(s.Root, "store", "worlds");
            foreach (var d in new[] { s.Scenes, s.Archive, s.Reports, s.Caches, s.Packages }) Directory.CreateDirectory(d);
            return s;
        }
        public Dictionary<string, string> WriteScene(string folder, string scene, string salt = "")
        {
            Directory.CreateDirectory(Path.Combine(folder, scene));
            foreach (var f in SceneFileNames) File.WriteAllText(Path.Combine(folder, scene, f), scene + " " + f + salt);
            return WorldSceneArchive.Hashes(Path.Combine(folder, scene), SceneFileNames);
        }
        // A scene with everything derived from it: installed, archived, its settlement report and a published cache.
        public void World(string scene)
        {
            WriteScene(Scenes, scene); WriteScene(Archive, scene);
            File.WriteAllText(Path.Combine(Reports, scene + ".json"), "{}");
            Cache(new string(scene[11], 64), scene);
        }
        public void Cache(string folder, string scene)
        {
            Directory.CreateDirectory(Path.Combine(Caches, folder));
            File.WriteAllText(Path.Combine(Caches, folder, "manifest.json"), Json.Serialize(new Dictionary<string, object> { ["schema"] = NavigationCacheStore.Schema, ["version"] = NavigationCacheStore.Version, ["scene"] = scene }));
        }
        public WorldBinding Package(string id, string scene)
        {
            string manifest = new string(id[id.Length - 1], 64);
            var binding = new WorldBinding { WorldId = id, ManifestSha256 = manifest, Instance = WorldBinding.NewInstance(), ContentId = new string('c', 64) };
            string dir = Path.Combine(Packages, binding.PackageName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "world.json"), Json.Serialize(new Dictionary<string, object> { ["schema"] = "mnbl.generated-world-package.v1", ["worldId"] = id, ["sceneName"] = scene }));
            return binding;
        }
        public WorldReferenceIndex Index()
        {
            var initial = new EarlyWorldBindingStore(Path.Combine(Root, "store"), Catalog, b => true);
            return WorldReferenceIndex.Build(Catalog, initial, null, WorldReferenceIndex.SaveKindsBound, new SaveJournal(Path.Combine(Root, "store"), Catalog, n => null));
        }
        public bool Has(string scene) { return Directory.Exists(Path.Combine(Scenes, scene)); }
        public bool Archived(string scene) { return Directory.Exists(Path.Combine(Archive, scene)); }
        public bool Report(string scene) { return File.Exists(Path.Combine(Reports, scene + ".json")); }
        public bool Cached(string scene) { return Directory.Exists(Path.Combine(Caches, new string(scene[11], 64))); }
        public bool Everything(string scene) { return Has(scene) && Archived(scene) && Report(scene) && Cached(scene); }
        public bool Nothing(string scene) { return !Has(scene) && !Archived(scene) && !Report(scene) && !Cached(scene); }
    }

    private static void Sweep(string scratch)
    {
        var s = Store.Make(scratch, "sweep");
        string packaged = Scene('a'), saved = Scene('b'), inUse = Scene('d'), orphan = Scene('e'), fixture = "mnbl_world_a";
        foreach (var scene in new[] { packaged, saved, inUse, orphan }) s.World(scene);
        s.WriteScene(s.Scenes, fixture);
        s.Cache(new string('f', 64), fixture);
        // A started campaign's package; a save of an earlier MNBL build whose package is gone (its header still names the scene); the world
        // under review in this process; an orphan (generated and previewed, never started).
        var started = s.Package("mnbl_00282001", packaged);
        s.Catalog.Bound("campaign A", started, packaged);
        s.Catalog.Bound("older campaign", new WorldBinding { WorldId = "mnbl_00282002", ManifestSha256 = new string('2', 64), Instance = WorldBinding.NewInstance(), ContentId = new string('c', 64) }, saved);
        s.Catalog.Saves["vanilla"] = new SaveHeader { Name = "vanilla", Metadata = new Dictionary<string, string> { ["ApplicationVersion"] = "v1.4.8.119303" }, BodyWhole = true };
        // Debris: a cache staging folder, an archive staging folder, a refused layout, a package write that never finished.
        Directory.CreateDirectory(Path.Combine(s.Caches, "staging-" + new string('1', 32)));
        Directory.CreateDirectory(Path.Combine(s.Archive, WorldSceneArchive.StagingPrefix + new string('1', 32)));
        File.WriteAllText(Path.Combine(s.Reports, "refused-" + new string('9', 64) + ".json"), "{}");
        Directory.CreateDirectory(Path.Combine(s.Packages, "mnbl_00282003-333333333333.tmp-" + new string('3', 32)));

        var refs = WorldSceneReferences.Discover(s.Index(), s.Packages, new[] { inUse });
        Check(refs.Complete && refs.Packages == 1 && refs.Referenced(packaged) && refs.Referenced(saved) && refs.Referenced(inUse) && !refs.Referenced(orphan),
            "references: a world package, a bound save's header and this process name their scenes; an orphan is named by nothing; an unfinished package write is no package");
        Check(refs.Why(packaged).Contains("world package " + started.PackageName) && refs.Why(packaged).Any(w => w.StartsWith("save campaign A")) && refs.Why(saved).Single() == "save older campaign"
              && refs.Why(inUse).Single() == "in use in this process", "references: each says why (" + String.Join("; ", refs.Scenes.Select(kv => kv.Key.Substring(11, 2) + ": " + String.Join(", ", kv.Value))) + ")");
        var record = WorldStoreSweep.Run(s.Layout, refs, SceneNames.IsWorldScene);
        Check((string)record["sweep"] == "complete" && s.Everything(packaged) && s.Everything(saved) && s.Everything(inUse),
            "sweep: every scene a package, a bound save or this process names is kept with its archive, settlement report and navigation caches");
        Check(s.Nothing(orphan) && ((List<string>)record["sweptUnusedScenes"]).SequenceEqual(new[] { orphan }) && ((List<string>)record["sweptArchivedScenes"]).SequenceEqual(new[] { orphan })
              && ((List<string>)record["sweptNavigationCaches"]).Count == 1, "sweep: the orphan's scene, archive, settlement report and caches are deleted, and nothing else");
        Check(s.Has(fixture) && Directory.Exists(Path.Combine(s.Caches, new string('f', 64))), "sweep: a fixture scene (not content-addressed) and its caches are never swept");
        Check(!Directory.Exists(Path.Combine(s.Caches, "staging-" + new string('1', 32))) && !Directory.Exists(Path.Combine(s.Archive, WorldSceneArchive.StagingPrefix + new string('1', 32)))
              && !File.Exists(Path.Combine(s.Reports, "refused-" + new string('9', 64) + ".json")), "sweep: staging debris and refused layouts go");
        var kept = (IDictionary<string, object>)record["keptScenes"];
        Check(kept.Count == 3 && kept.ContainsKey(packaged) && kept.ContainsKey(saved) && kept.ContainsKey(inUse) && File.Exists(Path.Combine(s.Archive, "sweep.json"))
              && ((IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(s.Archive, "sweep.json")))).ContainsKey("keptScenes"),
            "sweep: what was kept and why is recorded (the record and world-scenes/sweep.json)");

        // A second world generated in a later process: the first campaign's scene and caches stay (RT-35).
        var later = WorldStoreSweep.Run(s.Layout, WorldSceneReferences.Discover(s.Index(), s.Packages, new[] { Scene('g') }), SceneNames.IsWorldScene);
        Check(s.Everything(packaged) && s.Everything(saved) && !s.Has(inUse) && s.Nothing(inUse) == true,
            "sweep, later process: the campaigns' scenes stay; the earlier process's reviewed world, never started, is now an orphan and goes");

        // Fail closed: one unreadable save header, then one unreadable package manifest.
        s.World(orphan);
        s.Catalog.Saves["torn"] = new SaveHeader { Name = "torn" };
        var gap = WorldSceneReferences.Discover(s.Index(), s.Packages, null);
        var closed = WorldStoreSweep.Run(s.Layout, gap, SceneNames.IsWorldScene);
        Check(!gap.Complete && s.Everything(orphan) && ((string)closed["sweep"]).StartsWith("skipped", StringComparison.Ordinal),
            "sweep: a save header that cannot be read might name any scene: nothing is deleted (" + closed["sweep"] + ")");
        s.Catalog.Saves.Remove("torn");
        File.WriteAllText(Path.Combine(s.Packages, started.PackageName, "world.json"), "{ not json");
        var packageGap = WorldSceneReferences.Discover(s.Index(), s.Packages, null);
        WorldStoreSweep.Run(s.Layout, packageGap, SceneNames.IsWorldScene);
        Check(!packageGap.Complete && packageGap.Gaps.Single().Contains(started.PackageName) && s.Everything(orphan) && s.Everything(packaged),
            "sweep: a package manifest that cannot be read: nothing is deleted");
        Check(!WorldSceneReferences.Discover(null, s.Packages, null).Complete, "sweep: saves not read at all is a gap");
    }

    private static void Archive(string scratch)
    {
        var s = Store.Make(scratch, "archive");
        string scene = Scene('a');
        var hashes = s.WriteScene(s.Scenes, scene);
        string archive = Path.Combine(s.Archive, scene), installed = Path.Combine(s.Scenes, scene);
        Check(WorldSceneArchive.Keep(archive, installed, hashes) == "archived" && WorldSceneArchive.Keep(archive, installed, hashes) == "kept"
              && LoadPolicy.Scene(hashes, WorldSceneArchive.Hashes(archive, hashes.Keys)) == LoadPolicy.SceneState.Installed && !Directory.GetDirectories(s.Archive).Any(d => Path.GetFileName(d).StartsWith("staging-")),
            "archive: an installed scene is copied whole into the archive once, then kept (no staging left)");
        // A module update replaced the module folder: the load copies the archived scene back.
        Directory.Delete(installed, true);
        Check(LoadPolicy.Scene(hashes, WorldSceneArchive.Hashes(installed, hashes.Keys)) == LoadPolicy.SceneState.Missing, "archive: after a module update the scene is missing from the module");
        WorldSceneArchive.Restore(archive, installed, hashes);
        Check(LoadPolicy.Scene(hashes, WorldSceneArchive.Hashes(installed, hashes.Keys)) == LoadPolicy.SceneState.Installed, "archive: the archived copy is restored into the module, every file the package's");
        bool refused = false;
        try { WorldSceneArchive.Restore(archive, installed, hashes); } catch (IOException) { refused = true; }
        Check(refused && LoadPolicy.Scene(hashes, WorldSceneArchive.Hashes(installed, hashes.Keys)) == LoadPolicy.SceneState.Installed, "archive: an installed scene is never overwritten by a restore");
        // A tampered archive: refused at the load's check, and a restore that finds a wrong file removes its partial copy.
        File.AppendAllText(Path.Combine(archive, "terrain.bin"), "x");
        Check(LoadPolicy.Scene(hashes, WorldSceneArchive.Hashes(archive, hashes.Keys)) == LoadPolicy.SceneState.Different, "archive: a changed archived file is not the package's (the load re-encodes instead)");
        Directory.Delete(installed, true);
        refused = false;
        try { WorldSceneArchive.Restore(archive, installed, hashes); } catch (IOException) { refused = true; }
        Check(refused && !Directory.Exists(installed), "archive: a restore whose copy does not verify removes the partial folder");
        refused = false;
        string other = Path.Combine(s.Root, "other"); var otherHashes = s.WriteScene(other, scene, " other");
        try { WorldSceneArchive.Keep(archive, Path.Combine(other, scene), otherHashes); } catch (IOException) { refused = true; }
        Check(refused, "archive: an archive is never rewritten with other bytes");
    }

    // A binding written by an earlier MNBL build (another module version, assembly and backend) whose scene the sweep kept.
    private static void EarlierBuild(string scratch)
    {
        Func<string, string, string, Dictionary<string, string>> build = (module, assembly, backend) => new Dictionary<string, string> { ["contract"] = "mnbl.save-binding.v1", ["module"] = module, ["moduleAssemblySha256"] = assembly,
            ["game"] = "v1.4.8.119303", ["naval"] = "v1.2.8.119303", ["backend"] = backend, ["navigationCache"] = "mnbl.navigation-cache-key/1; adapter mmr41/1", ["saveIds"] = "7910000/v2", ["worldIdentity"] = "mnbl.world-identity.v1" };
        var saved = build("v0.0.1.0", new string('a', 64), "mnbl.runtime-world-backend/R07; producerLock 000000000000");
        var running = build("v0.0.2.0", new string('b', 64), "mnbl.runtime-world-backend/R07; producerLock 111111111111");
        var notes = new List<string>();
        var s = Store.Make(scratch, "earlier");
        string scene = Scene('b');
        s.World(scene);
        var package = s.Package("mnbl_00282004", scene);
        s.Catalog.Bound("earlier build", package, scene);
        // This build generates another world: the sweep runs in its process.
        WorldStoreSweep.Run(s.Layout, WorldSceneReferences.Discover(s.Index(), s.Packages, new[] { Scene('c') }), SceneNames.IsWorldScene);
        var expected = WorldSceneArchive.Hashes(Path.Combine(s.Archive, scene), SceneFileNames);
        Check(LoadPolicy.Compatibility(saved, running, notes) == null && notes.Count == 3 && LoadPolicy.Scene(expected, WorldSceneArchive.Hashes(Path.Combine(s.Scenes, scene), SceneFileNames)) == LoadPolicy.SceneState.Installed
              && s.Report(scene) && s.Cached(scene),
            "earlier build: its binding passes the build check (module, assembly and backend noted, verified by the rebuild) and its kept scene, report and caches are installed: the load goes on without re-encoding");
        Directory.Delete(Path.Combine(s.Scenes, scene), true);   // a module update since
        Check(LoadPolicy.Scene(expected, WorldSceneArchive.Hashes(Path.Combine(s.Archive, scene), SceneFileNames)) == LoadPolicy.SceneState.Installed,
            "earlier build, after a module update: the archived copy is the package's scene, restored with no encoder (a later encoder's other scene name does not matter)");
    }

    private static Dictionary<string, object> Manifest(string scene, string module, string game, double allMs, string created)
    {
        return new Dictionary<string, object> { ["schema"] = NavigationCacheStore.Schema, ["version"] = NavigationCacheStore.Version, ["scene"] = scene, ["moduleSha256"] = module,
            ["key"] = NavigationCacheKey.Schema + " 1\nadapter=mmr41/1\ngame=" + game + "\nscene=" + scene + "\n", ["createdUtc"] = created,
            ["durations"] = new Dictionary<string, object> { ["DefaultGenerateMilliseconds"] = 48000.0, ["NavalGenerateMilliseconds"] = 33000.0, ["AllGenerateMilliseconds"] = allMs } };
    }

    private static void Forecast()
    {
        string scene = Scene('a'), module = new string('m', 64), game = "v1.4.8.119303 / NavalDLC v1.2.8.119303";
        Func<IEnumerable<KeyValuePair<string, Dictionary<string, object>>>, CacheForecast> predict = rows =>
            CacheForecast.Predict(rows.Select(r => new KeyValuePair<string, IDictionary<string, object>>(r.Key, r.Value)), scene, module, game);
        var none = predict(new[] { new KeyValuePair<string, Dictionary<string, object>>("x", Manifest(Scene('z'), module, game, 300000, "2026-10-01")) });
        Check(!none.Reuse && none.Kind == CacheForecast.Missing && none.MeasuredSeconds == null, "forecast: no published cache of the scene: a rebuild, with no measured time");
        var update = predict(new[] { new KeyValuePair<string, Dictionary<string, object>>("old", Manifest(scene, new string('o', 64), game, 300000, "2026-10-01")) });
        Check(!update.Reuse && update.Kind == CacheForecast.OtherModule && Math.Abs(update.MeasuredSeconds.Value - 381) < 1e-6,
            "forecast: the scene's caches from another module build (a module update): a rebuild, its last build on this computer measured (381 s)");
        var patched = predict(new[] { new KeyValuePair<string, Dictionary<string, object>>("old", Manifest(scene, module, "v1.4.9.1 / NavalDLC v1.2.9", 300000, "2026-10-01")) });
        Check(!patched.Reuse && patched.Kind == CacheForecast.OtherGame, "forecast: the scene's caches under another game or War Sails version: a rebuild");
        var reuse = predict(new[] { new KeyValuePair<string, Dictionary<string, object>>("old", Manifest(scene, new string('o', 64), game, 300000, "2026-10-01")),
                                    new KeyValuePair<string, Dictionary<string, object>>("new", Manifest(scene, module, game, 400000, "2026-10-02")), new KeyValuePair<string, Dictionary<string, object>>("bad", null) });
        Check(reuse.Reuse && reuse.Kind == CacheForecast.Published && reuse.MeasuredFolder == "new" && Math.Abs(reuse.MeasuredSeconds.Value - 481) < 1e-6,
            "forecast: a published cache of the scene from this module build and game is reused (no warning); the newest manifest gives the measured time; an unreadable manifest is skipped");
        Check(CacheBuildText.Reason(CacheForecast.OtherModule) == CacheBuildText.ReasonModule && CacheBuildText.Reason(CacheForecast.Missing) == CacheBuildText.ReasonMissing
              && new[] { CacheBuildText.Preview, CacheBuildText.Load, CacheBuildText.LoadMeasured, CacheBuildText.LoadTitle, CacheBuildText.LoadContinue, CacheBuildText.LoadCancel, CacheBuildText.ReasonModule, CacheBuildText.ReasonMissing, CacheBuildText.ReasonGame }
                  .All(t => t.StartsWith("{=mnbl_", StringComparison.Ordinal) && t.IndexOf('}') > 7)
              && CacheBuildText.Load.Contains("{MINUTES}") && CacheBuildText.Load.Contains("{SETTLEMENTS}") && CacheBuildText.LoadMeasured.Contains("{MINUTES}"),
            "the warning texts: each with a stable string id, the wait in minutes and the settlement count");
    }

    private static void Model(string repo, IDictionary<string, object> evidence)
    {
        var budget = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen", "policies", "one-shot-budget.json")));
        var model = CacheBuildModel.FromPolicy((IDictionary<string, object>)budget["navigationCacheBuild"]);
        double a = model.Seconds(CacheBuildModel.Settlements(45, 26, 215)), b = model.Seconds(CacheBuildModel.Settlements(66, 39, 305));
        evidence["model"] = model.Report(); evidence["worldA"] = a; evidence["worldB"] = b;
        Check(model.Measurements.Count == 2 && Math.Abs(a - 383) / 383 <= 0.15, "model: world A (45 towns, 26 castles, 215 villages) is estimated at " + a.ToString("0.0", CultureInfo.InvariantCulture) + " s, within 15% of its measured 383 s");
        Check(Math.Abs(b - 829.1) / 829.1 <= 0.15 && model.Exponent > 1.5 && model.Exponent < 3,
            "model: world B (66, 39, 305) at " + b.ToString("0.0", CultureInfo.InvariantCulture) + " s against its measured 829 s; exponent " + model.Exponent.ToString("0.000", CultureInfo.InvariantCulture) + " (the square law of the distance pairs, near 2)");
        Check(model.Seconds(400) > model.Seconds(300) && model.Seconds(600) > 3 * model.Seconds(300) && CacheBuildModel.Minutes(a) == 7 && CacheBuildModel.Minutes(b) == 14 && CacheBuildModel.Minutes(10) == 1,
            "model: the wait grows faster than the settlement count; in whole minutes A is 7 and B 14 (never 0)");
        var one = CacheBuildModel.FromPolicy(new Dictionary<string, object> { ["worlds"] = new object[] { new Dictionary<string, object> { ["world"] = "A", ["settlements"] = 286, ["seconds"] = new object[] { 383.0 } } } });
        Check(Math.Abs(one.Exponent - 2) < 1e-9 && Math.Abs(one.Seconds(286) - 383) < 1e-6 && Math.Abs(one.Seconds(572) - 4 * 383) < 1e-6, "model: one measured world gives the square law through it");
        Check(CacheBuildModel.FromPolicy(null) == null, "model: no section, no model (the setup then promises no start time)");
    }
}
