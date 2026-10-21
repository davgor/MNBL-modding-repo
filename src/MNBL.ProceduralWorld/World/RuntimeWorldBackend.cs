using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-37: the runtime surface R07 selected (docs/runtime-backend-decision.md, docs/runtime-surface.md): an owned native scene bundle
    // per world, read into an owned scene, behind an owned SandBox.MapScene subclass (GeneratedMapScene). No other surface API.
    //   * Cold construction, from a verified MMR-29 world bundle: Prepare (worker thread: models the surface, encodes the compiled terrain
    //     with the pinned surface encoder into a staging job, and refuses an encoding whose published heights differ from the model), then
    //     Install (engine thread: places the five native files in the scene store under the encoding's content-addressed name, reusing an
    //     identical installed scene, never rewriting one, and registers it), then Construct (engine thread: reads it into an owned scene).
    //   * Warm construction: an installed owned scene (a reused store entry, or a fixture slot installed before launch) is read the same
    //     way; the campaign map profile (ReadCampaignMap) is the installed SandBox MapScene.Load sequence with the owned read target.
    //   * Threading: native calls only on the engine thread, the application tick thread (EngineThread, bound on the first tick); CPU
    //     work (modelling, encoding, hashing) off it; EngineWork carries a worker's continuation onto the engine thread (pumped every tick).
    //   * Ownership: each world's resources are in its ResourceLedger with who releases them: the scene and agent renderer (the world, or
    //     for a campaign map the installed engine objects), screen objects, the scene store entry and the engine's scene-name pairing.
    //     The staging job is deleted once installed or refused. The scene store keeps one folder per world encoding, and (MMR-282) a copy of
    //     each in the per-user scene archive (WorldSceneArchive), outside the module folder a module update replaces. A generation's sweep
    //     removes only the scenes, archives, settlement reports and navigation caches no world package, bound save or this process names
    //     (WorldStoreSweep), and keeps everything when it cannot tell.
    //   * Generations: every world carries a generation token (WorldGenerations); Prepare, Install and Construct refuse a token a later
    //     world superseded, so a late callback cannot attach old resources; queries through a superseded world are refused.
    //   * Navigation (MMR-38, docs/runtime-navigation.md): Prepare builds the campaign navmesh from the bundle's travel cells
    //     (RuntimeNavigationBuilder), validates it statically (NavigationPlan) and refuses the world, before anything is encoded or
    //     installed, when a required point cannot be snapped or a required route is not realized; the navmesh replaces the encoder's
    //     diagnostic grid and its hash is part of the scene name. Construct repeats the installed MapScene.Load navigation setup before the
    //     read: the region map, and the disable of the faces invalid for every navigation type (observed in MMR-38 to have no effect on faces
    //     read afterwards; path queries exclude those types explicitly). RuntimeWorld answers the native navmesh queries the engine-side
    //     validation (NavigationValidator, SceneNavigationEngine) makes.
    internal static class EngineThread
    {
        private static int id, nativeId;
        public static int Id { get { return id; } }
        // MMR-49: the operating system's id of the engine thread (a stalled load is told from a busy one by that thread's processor time).
        public static int NativeId { get { return nativeId; } }
        // Called on every application tick (SubModule.OnApplicationTick): the first call binds the application tick thread, where the
        // engine's native calls and campaign map loading run (the module's OnSubModuleLoad runs on another thread).
        public static void Bind()
        {
            if (Interlocked.CompareExchange(ref id, Thread.CurrentThread.ManagedThreadId, 0) != 0) return;
#pragma warning disable 618   // the operating system thread id, which AppDomain.GetCurrentThreadId is (System.Diagnostics.ProcessThread.Id)
            Interlocked.Exchange(ref nativeId, AppDomain.GetCurrentThreadId());
#pragma warning restore 618
        }
        public static bool IsCurrent { get { return id != 0 && id == Thread.CurrentThread.ManagedThreadId; } }
        public static void Require(string operation)
        {
            if (id == 0) throw new InvalidOperationException(operation + ": the engine thread is not bound yet (the first application tick binds it).");
            int me = Thread.CurrentThread.ManagedThreadId;
            if (id != me) throw new InvalidOperationException(operation + " is a native call and runs only on the engine thread " + id + ", not thread " + me + ".");
        }
    }

    // Work for the engine thread, posted from any thread and run by Pump on the engine thread (SubModule.OnApplicationTick).
    internal static class EngineWork
    {
        private static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        public static Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>();
            queue.Enqueue(() => { try { completion.SetResult(work()); } catch (Exception error) { completion.SetException(error); } });
            return completion.Task;
        }
        public static int Pump()
        {
            if (!EngineThread.IsCurrent) return 0;
            int ran = 0; Action next;
            while (queue.TryDequeue(out next)) { next(); ran++; }
            return ran;
        }
    }

    internal sealed class PreparedSurface
    {
        public string SceneName, JobRoot, Output, WorldContentId;
        public WorldSurface Surface;
        public GenerationToken Token;
        public Dictionary<string, object> Encoder, Manifest;
        public SortedDictionary<string, string> Files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public double EncodeSeconds;
        public int ThreadId;
        public bool InstalledAlready;   // the scene store already holds this encoding (same bytes)
        public NavigationBuild Navigation;
        public NavigationPlan NavigationPlan;
        public List<GeneratedWaterSystem.Lake> Lakes;   // MMR-39: the lake planes in the encoded scene
        public int UnmatchedLakes, HiddenLakes;
        public SettlementLayout Settlements;            // MMR-40: the resolved settlement placement the scene's entities were built from
        public FrozenLooks Looks;                       // MMR-258: the looks the scene's entities were built from (frozen into the world package)
        public IPlacementGround SettlementGround;
        public string SettlementReportPath;
        public string Archive;                          // MMR-282: the scene archive's copy ("archived", "kept" or why it failed)
        public string SnowTextureName;                  // MMR-256: the snow texture the scene's terrain names (null on the permanent-white route)
        public bool JobDeleted;
        public string JobDeleteError;
        // Deletes the staging job (the encoder's output and inputs). Idempotent.
        public void Discard()
        {
            if (JobDeleted || JobRoot == null) return;
            try { if (Directory.Exists(JobRoot)) Directory.Delete(JobRoot, true); JobDeleted = !Directory.Exists(JobRoot); }
            catch (Exception error) { JobDeleteError = error.GetType().Name + ": " + error.Message; }
        }
    }

    internal static class RuntimeWorldBackend
    {
        public const string ModuleId = "MNBL.ProceduralWorld";
        public const string HelperPrefix = "mnbl_world";   // MMR-40: the helper entities' name prefix in a world's scene
        public static string SettlementReportPath(string sceneName) { return Path.Combine(SubModule.Store, "world-settlements", sceneName + ".json"); }
        public static string RefusedSettlementReportPath(string worldContentId) { return Path.Combine(SubModule.Store, "world-settlements", "refused-" + worldContentId + ".json"); }
        // The installed War Sails campaign map, read for the helper scripts and icon meshes the settlement entities reference (MMR-40).
        public static string InstalledCampaignMap() { return Path.Combine(TaleWorlds.ModuleManager.ModuleHelper.GetModuleFullPath("NavalDLC"), "SceneObj", "Main_map", "scene.xscene"); }
        // MMR-258: the installed prefab folders a look's nested prefab instances resolve in (the first definition wins).
        public static IEnumerable<string> InstalledPrefabFolders() { return new[] { "NavalDLC", "SandBox", "Native" }.Select(m => Path.Combine(TaleWorlds.ModuleManager.ModuleHelper.GetModuleFullPath(m), "Prefabs")); }
        public const string TemplateSceneName = "mnbl_runtime_surface";
        public static readonly string[] NativeFiles = { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" };
        private static readonly HashSet<string> usedThisProcess = new HashSet<string>(StringComparer.Ordinal);

        public static string ModuleRoot() { return new FileInfo(typeof(RuntimeWorldBackend).Assembly.Location).Directory.Parent.Parent.FullName; }
        public static string SceneStore() { return Path.Combine(ModuleRoot(), "SceneObj"); }
        public static string SceneFolder(string sceneName) { return Path.Combine(SceneStore(), sceneName); }
        // MMR-282: the per-user copy of every installed world scene (<store>/world-scenes/<name>).
        public static string SceneArchive() { return Path.Combine(SubModule.Store, "world-scenes"); }
        public static string SceneArchiveFolder(string sceneName) { return Path.Combine(SceneArchive(), sceneName); }
        private static void MarkUsed(string sceneName) { lock (usedThisProcess) usedThisProcess.Add(sceneName); }
        // MMR-49: a loaded world's installed scene is in use in this process: a later generation job's sweep keeps it.
        internal static void Use(string sceneName) { MarkUsed(sceneName); }
        // MMR-49 (engine thread): an installed scene a load uses: kept from this process's sweep, paired with this module, and resolved to its
        // installed folder (IOException when it does not resolve there). Returns the engine's path of it.
        internal static string UseInstalled(string sceneName)
        {
            EngineThread.Require("scene registration");
            MarkUsed(sceneName);
            Utilities.PairSceneNameToModuleName(sceneName, ModuleId);
            return Resolve(sceneName);
        }
        private static bool Used(string sceneName) { lock (usedThisProcess) return usedThisProcess.Contains(sceneName); }

        // Worker thread. No native call. `navigationVariant` selects the production navigation (null), the navigation fixture or a fault
        // (NavigationVariants); a world whose navigation is refused throws NavigationRefusedException before anything is encoded.
        // MMR-258: frozenLooks, a world package's frozen looks (a load re-encoding its swept scene); null selects them from the installed
        // campaign map (generation), and the prepared surface keeps them for the package (PreparedSurface.Looks).
        public static PreparedSurface Prepare(WorldDataBundle bundle, GenerationToken token, CancellationToken cancel, string navigationVariant = null, FrozenLooks frozenLooks = null)
        {
            if (bundle == null || token == null) throw new ArgumentNullException();
            MNBL.ProceduralWorld.Persistence.SaveBindingRuntime.RequireWriter();   // MMR-62: only the store lock holder writes staging
            token.Require("preparing world " + bundle.WorldContentId);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var surface = WorldSurface.FromBundle(bundle);
            var policy = NavigationPolicy.Installed();
            var navigationInput = NavigationVariants.Apply(navigationVariant, NavigationInput.FromBundle(bundle, surface, policy), policy);
            var navigation = RuntimeNavigationBuilder.Build(navigationInput, policy);
            var plan = NavigationPlan.Create(navigation, NavigationModel.Installed(NavigationModel.InstalledCalibration()));
            if (!plan.Activatable) throw new NavigationRefusedException("World " + bundle.WorldContentId.Substring(0, 12) + " (" + navigationInput.Variant + ")", plan.Diagnostics);
            // MMR-40: every campaign settlement's markers, resolved against the navigation (binding points) and the surface; a placement that
            // cannot be made valid refuses the world here, before anything is encoded.
            var placementPolicy = SettlementPlacementPolicy.Installed();
            var contract = SettlementEntityContract.Installed(InstalledCampaignMap());
            placementPolicy.RequireSource(contract.SourceSha256);
            var settlementGround = new NavigationGround(navigation);
            // MMR-258: every town's, castle's and village's look (the installed catalog, or the package's frozen looks), then the placement.
            var looksPolicy = MNBL.ProceduralWorld.World.SettlementLooksPolicy.Installed();
            byte[] campaignJson = bundle.ReadVerified("compiled/campaign/campaign.json"), portsJson = bundle.ReadVerified("compiled/travel/ports.json");
            var lookInputs = SettlementLookPlan.ForCampaign(campaignJson, bundle.ReadVerified("compiled/travel/travel-graph.json"), portsJson, MNBL.ProceduralWorld.World.StrictJson.Embedded("economy.json"),
                MNBL.ProceduralWorld.World.StrictJson.Embedded("settlement-prototypes.json"), new HashSet<string>(Generation.CatalogPin.Pin.FromBytes(MNBL.ProceduralWorld.World.StrictJson.Embedded("campaign-catalog.json")).Defaults, StringComparer.Ordinal).Contains, looksPolicy);
            lookInputs.Frozen = frozenLooks;
            if (frozenLooks == null) lookInputs.Catalog = SettlementLookCatalog.Installed(InstalledCampaignMap(), InstalledPrefabFolders(), MNBL.ProceduralWorld.World.StrictJson.Embedded("settlement-prototypes.json"), looksPolicy);
            var missionScenes = MNBL.ProceduralWorld.Missions.MissionScenes.Installed();
            lookInputs.Conforms = (prototype, kind, port) => missionScenes.PrototypeProblems(prototype, kind, port).Count == 0;
            FrozenLooks looks;
            var settlements = SettlementLookPlan.Resolve(SettlementPositionResolver.SitesFromBundle(navigationInput, campaignJson, portsJson), plan, settlementGround, placementPolicy, lookInputs, out looks);
            if (!settlements.Activatable)
            {
                // The refused layout is kept as evidence under the world's content id (no scene name exists yet); Sweep removes it with the rest.
                MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllText(RefusedSettlementReportPath(bundle.WorldContentId), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(settlements.Report(settlementGround)));
                throw new SettlementPlacementRefusedException("World " + bundle.WorldContentId.Substring(0, 12), settlements.Problems);
            }
            string settlementXml = SettlementEntityFactory.ToXml(SettlementEntityFactory.Build(settlements, settlementGround, contract, placementPolicy, looksPolicy, HelperPrefix, surface.Extent, SettlementEntityFactory.MapMaximumHeight(surface.High)));
            string module = ModuleRoot(), template = MNBL.ProceduralWorld.World.SurfaceTemplateSource.Ensure(), encoderPath = Path.Combine(Path.GetDirectoryName(typeof(RuntimeWorldBackend).Assembly.Location), "worldbridge.exe");
            PlainPath(template);
            // MMR-39: the water bodies inserted after encoding (the sea plane and the lake planes, GeneratedWaterSystem) are part of the
            // encoding's identity: their XML (with placeholder names) is hashed into the scene name.
            byte[] worldJson = bundle.ReadVerified("source/world.json");
            GeneratedClimateSystem.Source source;
            using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
            worldJson = null;
            // MMR-256: the world's snow texture (GeneratedSnowTexture): its name goes into the terrain, so it is part of the scene name.
            var climate = GeneratedClimateSystem.Synthesize(source, PeriodicWorldCoordinates.FromSurface(surface), GeneratedClimateSystem.Policy.Installed());
            string snowName = climate.SnowTextureName;
            int unmatchedLakes, hiddenLakes;
            var lakes = GeneratedWaterSystem.Lakes(surface, source, PeriodicWorldCoordinates.FromSurface(surface), out unmatchedLakes, out hiddenLakes);
            string entitiesXml = GeneratedWaterSystem.WithWaterBodies("<scene><entities>\n</entities></scene>", "scene_water", surface.Extent, lakes);
            string templateSha = String.Join("\n", NativeFiles.Select(f => f + " " + GenerationProbe.Hash(Path.Combine(template, f)))) + "\nwater " + GeneratedWaterSystem.Version
                + " " + GeneratedClimateSystem.Sha(new UTF8Encoding(false).GetBytes(entitiesXml))
                + "\nsettlements " + SettlementEntityFactory.Version + " " + GeneratedClimateSystem.Sha(new UTF8Encoding(false).GetBytes(settlementXml)) + " " + contract.Sha256
                + "\nsnow " + GeneratedClimateSystem.Version + " " + climate.SnowRoute + " " + (snowName ?? "none");
            string sceneName = SceneNames.ForWorld(bundle.WorldContentId, GenerationProbe.Hash(encoderPath), templateSha, surface.Extent, navigation.Sha256);
            string root = Path.Combine(SubModule.Store, "world-surfaces", Guid.NewGuid().ToString("N"));
            PlainPath(root);
            if (Directory.Exists(root)) throw new IOException("Fresh surface job already exists: " + root);
            var prepared = new PreparedSurface { SceneName = sceneName, JobRoot = root, Output = Path.Combine(root, "surface"), WorldContentId = bundle.WorldContentId, Surface = surface, Token = token, ThreadId = Thread.CurrentThread.ManagedThreadId,
                Navigation = navigation, NavigationPlan = plan, Lakes = lakes, UnmatchedLakes = unmatchedLakes, HiddenLakes = hiddenLakes,
                Settlements = settlements, SettlementGround = settlementGround, Looks = looks, SnowTextureName = snowName };
            try
            {
                string input = Path.Combine(root, "input"), localTemplate = Path.Combine(root, "template");
                Directory.CreateDirectory(input); Directory.CreateDirectory(localTemplate);
                File.WriteAllBytes(Path.Combine(input, "heightfield.bin"), bundle.ReadVerified("compiled/terrain/heightfield.bin"));
                File.WriteAllBytes(Path.Combine(input, "weights.bin"), bundle.ReadVerified("compiled/terrain/weights.bin"));
                File.WriteAllBytes(Path.Combine(input, "flora.bin"), bundle.ReadVerified("compiled/terrain/flora.bin"));   // MMR-255: map trees
                foreach (string file in NativeFiles) File.Copy(Path.Combine(template, file), Path.Combine(localTemplate, file), false);
                string xmlPath = Path.Combine(localTemplate, "scene.xscene"), xml = File.ReadAllText(xmlPath), original = "<scene name=\"" + TemplateSceneName + "\"";
                int at = xml.IndexOf(original, StringComparison.Ordinal);
                if (at < 0 || xml.IndexOf(original, at + original.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("The surface template must name the template scene exactly once.");
                xml = xml.Replace(original, "<scene name=\"" + sceneName + "\"");
                if (snowName != null) xml = GeneratedClimateSystem.WithSnowTexture(xml, snowName);   // MMR-256: the encoder keeps terrain attributes
                File.WriteAllText(xmlPath, xml, new UTF8Encoding(false));
                cancel.ThrowIfCancellationRequested();
                token.Require("encoding world " + bundle.WorldContentId);
                string[] arguments = { "--surface-heightfield", Path.Combine(input, "heightfield.bin"), "--surface-template", localTemplate, "--surface-output", prepared.Output,
                    "--surface-extent", surface.Extent.ToString("R", CultureInfo.InvariantCulture), "--surface-scale", "1", "--surface-weights", Path.Combine(input, "weights.bin"), "--surface-normals", "smoothed", "--surface-flora", Path.Combine(input, "flora.bin") };   // MMR-278: normals from a smoothed copy
                prepared.Encoder = GenerationProbe.RunSurfaceEncoder(arguments, root, cancel);
                if (Convert.ToInt32(prepared.Encoder["exitCode"], CultureInfo.InvariantCulture) != 0) throw new InvalidDataException("The surface encoder failed: " + root);
                foreach (string file in NativeFiles) if (!File.Exists(Path.Combine(prepared.Output, file))) throw new FileNotFoundException("The surface encoder omitted " + file, Path.Combine(prepared.Output, file));
                // The encoding must be the model: the published quantized heights byte for byte, and the published bounds.
                if (!File.ReadAllBytes(Path.Combine(prepared.Output, "quantized-heightfield.bin")).SequenceEqual(surface.QuantizedHeightfieldBytes()))
                    throw new InvalidDataException("The encoded heights differ from the surface model.");
                var manifest = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(prepared.Output, "surface.json")));
                Func<string, double> number = key => Convert.ToDouble(manifest[key], CultureInfo.InvariantCulture);
                if (Convert.ToInt32(manifest["gridSide"]) != surface.Side || Convert.ToInt32(manifest["heightmapLevel"]) != surface.HeightmapLevel || number("nodeSize") != surface.NodeSize
                    || number("minHeight") != surface.Low || number("maxHeight") != surface.High || Math.Abs(number("quantizationStep") - surface.Step) > 1e-15 || (string)manifest["sceneName"] != sceneName)
                    throw new InvalidDataException("The encoder's surface manifest differs from the surface model.");
                prepared.Manifest = manifest;
                // MMR-39: the water body on the datum (GeneratedWaterSystem): the encoder keeps template entities empty.
                string scenePath = Path.Combine(prepared.Output, "scene.xscene");
                // Each lake keeps its own level (GeneratedWaterSystem.Lakes, from the source's water layers through the canonical location).
                // No colour-grade manager: its Init reads Calradia's grid natively (and an inspection scene's read of it hung, run 15744); the
                // campaign map's grade and season threshold are GeneratedMapAtmosphere's (MMR-256).
                // MMR-40: the settlement entities and the installed campaign map's scene levels they name (SettlementEntityFactory.DeclareSceneLevels).
                File.WriteAllText(scenePath, SettlementEntityFactory.DeclareSceneLevels(SettlementEntityFactory.Splice(GeneratedWaterSystem.WithWaterBodies(File.ReadAllText(scenePath), sceneName + "_water", surface.Extent, prepared.Lakes), settlementXml)), new UTF8Encoding(false));
                var entityProblems = SettlementEntityFactory.Validate(File.ReadAllText(scenePath), settlements, settlementGround, placementPolicy, HelperPrefix);
                if (entityProblems.Count > 0) throw new InvalidDataException("The world's settlement entities break their contract: " + String.Join("; ", entityProblems.Take(10)));
                // MMR-40: the resolved positions are a derived world artifact beside the navigation report (named by the scene).
                prepared.SettlementReportPath = SettlementReportPath(sceneName);
                MNBL.ProceduralWorld.Persistence.DurableFile.WriteAllText(prepared.SettlementReportPath, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(settlements.Report(settlementGround)));
                // The campaign navmesh replaces the encoder's diagnostic 33 x 33 grid.
                File.WriteAllBytes(Path.Combine(prepared.Output, "navmesh.bin"), navigation.Bytes);
                if (GenerationProbe.Hash(Path.Combine(prepared.Output, "navmesh.bin")) != navigation.Sha256) throw new IOException("The written navmesh differs from the built one.");
                foreach (string file in NativeFiles) prepared.Files[file] = GenerationProbe.Hash(Path.Combine(prepared.Output, file));
                string folder = SceneFolder(sceneName);
                prepared.InstalledAlready = Directory.Exists(folder) && NativeFiles.All(f => File.Exists(Path.Combine(folder, f)) && GenerationProbe.Hash(Path.Combine(folder, f)) == prepared.Files[f]);
                if (GeneratedClimateSystem.SnowTextureOf(File.ReadAllText(scenePath)) != snowName) throw new InvalidDataException("The encoded scene does not name the world's snow texture " + (snowName ?? "(none)") + ".");
                if (snowName != null) GeneratedSnowTexture.Ensure(snowName, climate.SnowTexture);
                prepared.EncodeSeconds = watch.Elapsed.TotalSeconds;
                return prepared;
            }
            catch { prepared.Discard(); throw; }
        }

        // Engine thread: installs a prepared world's scene in the scene store (or reuses the identical installed one), registers its name,
        // removes the store's folders this process has not used, and deletes the staging job. A superseded token is refused before any
        // file is installed or name registered (the job is still deleted).
        public static Dictionary<string, object> Install(PreparedSurface prepared)
        {
            try
            {
                EngineThread.Require("scene registration");
                bool reused = InstallFiles(prepared);
                var record = Register(prepared, reused);
                foreach (var kv in Sweep(References())) record[kv.Key] = kv.Value;
                return record;
            }
            finally { prepared.Discard(); }
        }

        // MMR-44: Install in three parts, so only the name registration holds the engine thread. InstallFiles (any thread, no engine call):
        // places the encoding's files under its content-addressed name in the scene store, or verifies the identical installed ones; returns
        // whether they were already installed. A superseded token is refused before anything is written.
        public static bool InstallFiles(PreparedSurface prepared)
        {
            prepared.Token.Require("installing scene " + prepared.SceneName);
            string target = SceneFolder(prepared.SceneName);
            PlainPath(target);
            if (Directory.Exists(target))
            {
                var differs = NativeFiles.Where(f => !File.Exists(Path.Combine(target, f)) || GenerationProbe.Hash(Path.Combine(target, f)) != prepared.Files[f]).ToList();
                if (differs.Count > 0) throw new IOException("The installed scene " + prepared.SceneName + " differs from its encoding (" + String.Join(", ", differs) + "); scenes are never rewritten.");
                prepared.Archive = KeepArchive(prepared.SceneName, prepared.Files);
                return true;
            }
            Directory.CreateDirectory(target);
            foreach (string file in NativeFiles)
            {
                string to = Path.Combine(target, file);
                File.Copy(Path.Combine(prepared.Output, file), to, false);
                if (GenerationProbe.Hash(to) != prepared.Files[file]) throw new IOException("Installed scene file differs from the encoding: " + file);
            }
            prepared.Archive = KeepArchive(prepared.SceneName, prepared.Files);
            return false;
        }

        // MMR-282 (any thread, no engine call): the installed scene's copy in the scene archive, made unless the identical one is there.
        // Returns "archived", "kept" or why it failed: the installed world works without it; it is what a module update would cost.
        internal static string KeepArchive(string sceneName, IDictionary<string, string> files)
        {
            try
            {
                PlainPath(SceneArchive());
                Directory.CreateDirectory(SceneArchive());
                return Persistence.WorldSceneArchive.Keep(SceneArchiveFolder(sceneName), SceneFolder(sceneName), files);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return "failed: " + error.GetType().Name + ": " + error.Message; }
        }

        // MMR-282 (any thread, no engine call): copies an archived scene back into the scene store (each file verified against the package's
        // scene files; a partial copy is removed). The caller registers it (UseInstalled).
        internal static void RestoreArchived(string sceneName, IDictionary<string, string> files)
        {
            string target = SceneFolder(sceneName);
            PlainPath(target); PlainPath(SceneArchiveFolder(sceneName));
            Persistence.WorldSceneArchive.Restore(SceneArchiveFolder(sceneName), target, files);
        }

        // Engine thread: registers the installed scene's name and checks the engine resolves it to its folder.
        public static Dictionary<string, object> Register(PreparedSurface prepared, bool reused)
        {
            EngineThread.Require("scene registration");
            prepared.Token.Require("registering scene " + prepared.SceneName);
            MarkUsed(prepared.SceneName);
            Utilities.PairSceneNameToModuleName(prepared.SceneName, ModuleId);
            string resolved = Resolve(prepared.SceneName);
            return new Dictionary<string, object> { ["sceneName"] = prepared.SceneName, ["folder"] = SceneFolder(prepared.SceneName), ["resolved"] = resolved, ["reused"] = reused,
                ["files"] = prepared.Files, ["threadId"] = Thread.CurrentThread.ManagedThreadId };
        }

        // MMR-282 (engine thread: the game's save list): every scene a world package, a bound or pending save, or this process names. A
        // failure to read any of them is a gap, and a sweep with a gap deletes no scene or cache.
        public static Persistence.WorldSceneReferences References()
        {
            Persistence.WorldReferenceIndex index = null;
            string failure = null;
            try
            {
                var store = Persistence.SaveBindingRuntime.Store;
                index = Persistence.WorldReferenceIndex.Build(store.Saves, store, null, Persistence.WorldReferenceIndex.SaveKindsBound, Persistence.SaveBindingRuntime.Later);
            }
            catch (Exception error) { failure = "the saves could not be indexed: " + error.GetType().Name + ": " + error.Message; }
            List<string> used; lock (usedThisProcess) used = usedThisProcess.ToList();
            var references = Persistence.WorldSceneReferences.Discover(index, GeneratedWorldPackage.Root, used);
            if (failure != null) references.Gaps.Add(failure);
            return references;
        }

        // Any thread (file work only): removes the world scenes (installed and archived), settlement reports and navigation caches no reference
        // names, and staging an earlier process left; keeps the rest, recorded with why (<store>/world-scenes/sweep.json). MMR-282: before
        // it, every scene this process had not used went, with its caches, so generating a world cost every other campaign a rebuild.
        public static Dictionary<string, object> Sweep(Persistence.WorldSceneReferences references)
        {
            var layout = new Persistence.WorldStoreLayout { SceneStore = SceneStore(), SceneArchive = SceneArchive(),
                SettlementReports = Path.Combine(SubModule.Store, "world-settlements"), NavigationCaches = WorldNavigationCache.Root(), ReportPath = Path.Combine(SceneArchive(), "sweep.json") };
            return Persistence.WorldStoreSweep.Run(layout, references, SceneNames.IsWorldScene);
        }

        // The installed scene path the engine resolves for a name; throws unless it is the name's scene store folder and that folder holds
        // the scene (the engine's startup index keeps the names of folders an install later removed).
        private static string Resolve(string sceneName)
        {
            if (!File.Exists(Path.Combine(SceneFolder(sceneName), "scene.xscene"))) throw new IOException("The scene " + sceneName + " is not installed (install it before constructing it).");
            string resolved;
            bool registered = Utilities.TryGetFullFilePathOfScene(sceneName, out resolved);
            string absolute = registered && !String.IsNullOrEmpty(resolved) ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Scene).Assembly.Location), resolved)) : null;
            if (!registered || !String.Equals(absolute, Path.GetFullPath(Path.Combine(SceneFolder(sceneName), "scene.xscene")), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The scene " + sceneName + " does not resolve to its installed folder (" + resolved + ").");
            return resolved;
        }

        // Engine thread: reads an installed owned scene into an inspection scene (physics world on) owned by the returned world. On any
        // failure what it created is released and the generation retired before the exception propagates.
        public static RuntimeWorld Construct(string sceneName, GenerationToken token, WorldSurface surface, NavigationModel navigation = null)
        {
            EngineThread.Require("scene construction");
            token.Require("constructing scene " + sceneName);
            GeneratedMapSceneCreator.ValidateSceneName(sceneName);
            string resolved = Resolve(sceneName);
            var world = new RuntimeWorld(sceneName, token, surface, "inspection");
            try
            {
                if (SceneNames.IsWorldScene(sceneName))
                {
                    world.Ledger.Acquire("scene store entry SceneObj/" + sceneName, "RuntimeWorldBackend scene store (one folder per world encoding, reused; a sweep removes only folders nothing references)", null);
                    world.Ledger.Acquire("scene name pairing", "engine scene index (no unregister API; the name always pairs to the same folder)", null);
                }
                var scene = Scene.CreateNewScene(true, true, DecalAtlasGroup.All, "mono_renderscene");
                world.Ledger.Acquire("scene", "RuntimeWorld (inspection)", () => scene.ClearAll());
                var renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
                world.Ledger.Acquire("agent renderer", "RuntimeWorld (inspection)", () => MBAgentRendererSceneController.DestructAgentRendererSceneController(scene, renderer, false));
                // A content-addressed world carries a campaign navmesh: the installed MapScene.Load navigation setup precedes the read.
                if (SceneNames.IsWorldScene(sceneName)) world.NavigationSetup = RuntimeNavigation.PrepareScene(scene, navigation ?? NavigationModel.Installed(NavigationModel.InstalledCalibration()));
                var init = new SceneInitializationData(true);
                init.InitPhysicsWorld = true;
                world.SnowTexture = GeneratedSnowTexture.Acquire(sceneName, world.Ledger);   // MMR-256: the texture the terrain names, before the read
                scene.Read(sceneName, ModuleId, ref init, "");
                if (!String.Equals(scene.GetName(), sceneName, StringComparison.Ordinal)) throw new InvalidDataException("The scene read is not the owned scene " + sceneName + ".");
                scene.SetUseConstantTime(true); scene.SetOcclusionMode(true);
                scene.OptimizeScene(true, true);
                world.Bind(scene, resolved);
                MarkUsed(sceneName);
                return world;
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }

        // Engine thread, campaign loading: the installed SandBox MapScene.Load scene creation and read (IL_0016..), with the owned read
        // target. The installed engine objects keep their release duties: the agent renderer is released by the installed
        // MapScene.Destroy and the scene cleared by the installed MapScreen.OnFinalize; the ledger hands both to them.
        public static RuntimeWorld ReadCampaignMap(string sceneName, GenerationToken token, Action<Scene, MBAgentRendererSceneController> created, Action<Scene> beforeRead)
        {
            EngineThread.Require("campaign map read");
            token.Require("reading campaign map " + sceneName);
            var world = new RuntimeWorld(sceneName, token, null, "campaign map");
            var scene = Scene.CreateNewScene(false, true, (DecalAtlasGroup)1, "MapScene");
            world.Ledger.Acquire("scene", "installed MapScreen.OnFinalize (Scene.ClearAll)", null);
            scene.SetClothSimulationState(true);
            var renderer = MBAgentRendererSceneController.CreateNewAgentRendererSceneController(scene);
            world.Ledger.Acquire("agent renderer", "installed MapScene.Destroy (DestructAgentRendererSceneController)", null);
            renderer.SetDoTimerBasedForcedSkeletonUpdates(false);
            if (created != null) created(scene, renderer);
            scene.SetOcclusionMode(true);
            var init = new SceneInitializationData(true);
            init.UsePhysicsMaterials = false; init.EnableFloraPhysics = false; init.UseTerrainMeshBlending = false; init.CreateOros = false;
            scene.SetFetchCrcInfoOfScene(true);
            if (beforeRead != null) beforeRead(scene);
            world.SnowTexture = GeneratedSnowTexture.Acquire(sceneName, world.Ledger);   // MMR-256: the texture the terrain names, before the read
            scene.Read(sceneName, ModuleId, ref init, "");
            if (!String.Equals(scene.GetName(), sceneName, StringComparison.Ordinal)) throw new InvalidDataException("Loaded campaign scene identity differs from the requested owned scene.");
            world.Bind(scene, null);
            return world;
        }

        internal static void PlainPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Redirected surface path: " + current);
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
    }

    // One constructed world's native scene, owned resources and generation. Every query checks, before any native call, that it runs
    // on the engine thread, that the world is not disposed and that no later world superseded it.
    internal sealed class RuntimeWorld : IDisposable
    {
        private Scene scene;
        private bool disposed;
        public string SceneName { get; private set; }
        public string ResolvedPath { get; private set; }
        public string Profile { get; private set; }
        public GenerationToken Token { get; private set; }
        public WorldSurface Surface { get; private set; }
        public ResourceLedger Ledger { get; private set; }
        public List<string> DisposeFailures { get; private set; }
        public Dictionary<string, object> NavigationSetup { get; internal set; }
        public Dictionary<string, object> SnowTexture { get; internal set; }   // MMR-256: GeneratedSnowTexture.Acquire's record (before the read)
        public int ConstructedOnThread { get; private set; }
        public bool IsDisposed { get { return disposed; } }

        internal RuntimeWorld(string sceneName, GenerationToken token, WorldSurface surface, string profile)
        {
            SceneName = sceneName; Token = token; Surface = surface; Profile = profile; Ledger = new ResourceLedger(); DisposeFailures = new List<string>();
            ConstructedOnThread = Thread.CurrentThread.ManagedThreadId;
        }
        internal void Bind(Scene value, string resolved) { scene = value; ResolvedPath = resolved; }

        private void Live(string operation)
        {
            if (disposed) throw new ObjectDisposedException("RuntimeWorld " + SceneName, operation + " after the world was disposed.");
            EngineThread.Require(operation);
            Token.RequireNotSuperseded(operation + " on " + SceneName);
        }

        public Scene Scene { get { Live("scene access"); return scene; } }
        public float Height(Vec2 point, bool checkHoles) { Live("height"); return scene.GetTerrainHeight(point, checkHoles); }
        public void HeightAndNormal(Vec2 point, out float height, out Vec3 normal) { Live("height and normal"); scene.GetTerrainHeightAndNormal(point, out height, out normal); }
        public Vec3 Normal(Vec2 point) { Live("normal"); return scene.GetNormalAt(point); }
        public bool Ground(Vec2 point, BodyFlags excludeMask, out float height) { Live("ground height"); height = float.NaN; return scene.GetHeightAtPoint(point, excludeMask, ref height); }
        public bool Ray(Vec3 from, Vec3 to, BodyFlags excludeMask, out float distance, out Vec3 closest)
        {
            Live("terrain ray"); return scene.RayCastForClosestEntityOrTerrain(from, to, out distance, out closest, .01f, excludeMask);
        }
        // Navmesh queries (MMR-38). Face indices are the engine's, valid for this scene only.
        public bool NavFace(double x, double y, bool land, out int face, out int group, out int island)
        {
            Live("navmesh face");
            var record = new PathFaceRecord(-1, -1, -1);
            scene.GetNavMeshFaceIndex(ref record, new Vec2((float)x, (float)y), land, false, true);
            face = record.FaceIndex; group = record.FaceGroupIndex; island = record.FaceIslandIndex;
            return record.IsValid();
        }
        public bool NavPath(int from, int to, Vec2 a, Vec2 b, float radius, int[] excluded, float extraCost, int costToSea, int costToLand, NavigationPath path)
        {
            Live("navmesh path"); return scene.GetPathBetweenAIFaces(from, to, a, b, radius, path, excluded, extraCost, costToSea, costToLand);
        }
        public bool NavDistance(int from, int to, Vec2 a, Vec2 b, float radius, float limit, int[] excluded, int costToSea, int costToLand, out float distance)
        {
            Live("navmesh distance"); return scene.GetPathDistanceBetweenAIFaces(from, to, a, b, radius, limit, out distance, excluded, costToSea, costToLand);
        }
        public int NavFaceCount() { Live("navmesh face count"); return scene.GetNavMeshFaceCount(); }
        public int NavFaceGroup(int face) { Live("navmesh face group"); return scene.GetIdOfNavMeshFace(face); }
        public Vec3 NavFaceCenter(int face) { Live("navmesh face centre"); var centre = Vec3.Zero; scene.GetNavMeshCenterPosition(face, ref centre); return centre; }
        public uint NavCrc() { Live("navmesh crc"); return scene.GetNavigationMeshCRC(); }
        // Fault injection on an inspection scene only (the probe's engine fault); production never toggles faces (R03).
        public int DisableNavFaces(int group)
        {
            Live("navmesh face toggle");
            if (Profile != "inspection") throw new InvalidOperationException("Faces are toggled only on an inspection scene.");
            return scene.SetAbilityOfFacesWithId(group, false);
        }

        public Dictionary<string, object> Bounds()
        {
            Live("bounds");
            Vec2i nodes; float nodeSize; int layers, version;
            scene.GetTerrainData(out nodes, out nodeSize, out layers, out version);
            Vec3 min, max; scene.GetBoundingBox(out min, out max);
            float low, high; bool range = scene.GetTerrainMinMaxHeight(out low, out high);
            return new Dictionary<string, object> { ["nodesX"] = nodes.X, ["nodesY"] = nodes.Y, ["nodeSize"] = nodeSize, ["layers"] = layers, ["version"] = version,
                ["boxMin"] = new[] { min.x, min.y, min.z }, ["boxMax"] = new[] { max.x, max.y, max.z }, ["rangeAvailable"] = range, ["minHeight"] = low, ["maxHeight"] = high,
                ["hasHeightmap"] = scene.HasTerrainHeightmap, ["containsTerrain"] = scene.ContainsTerrain };
        }

        // Engine thread. Releases what this world owns, latest first (resources another owner releases are handed to it), retires the
        // generation and drops the scene reference. Idempotent.
        public void Dispose()
        {
            if (disposed) return;
            EngineThread.Require("world disposal");
            DisposeFailures = Ledger.ReleaseAll("RuntimeWorld.Dispose (" + Profile + ")");
            scene = null; disposed = true;
            WorldGenerations.Retire(Token);
        }

        public Dictionary<string, object> Record()
        {
            return new Dictionary<string, object> { ["sceneName"] = SceneName, ["resolved"] = ResolvedPath, ["profile"] = Profile, ["generation"] = Token.Id, ["constructedOnThread"] = ConstructedOnThread,
                ["navigationSetup"] = NavigationSetup, ["snowTexture"] = SnowTexture, ["disposed"] = disposed, ["ledger"] = Ledger.Record(), ["outstanding"] = Ledger.Outstanding, ["disposeFailures"] = DisposeFailures };
        }
    }
    // MMR-38: the engine side of navigation for constructed worlds.
    internal static class RuntimeNavigation
    {
        // The installed SandBox.MapScene.Load navigation setup, before the read: the faces invalid for every navigation type disabled
        // (DisableUnwalkableNavigationMeshes) and the region map from the navigation model (MapSceneHelper.GetRegionMapping).
        public static Dictionary<string, object> PrepareScene(Scene scene, NavigationModel model)
        {
            var installed = (PartyNavigationModel)model.Instance;
            var disabled = new List<string>();
            // The call's return value is recorded: before the read no face exists yet (MMR-38 observed that the disable does not reach faces
            // read afterwards; every path query excludes the invalid types explicitly, as the campaign does).
            foreach (int id in installed.GetInvalidTerrainTypesForNavigationType(MobileParty.NavigationType.All)) disabled.Add(TerrainTypes.Name(id) + " " + id + " (" + scene.SetAbilityOfFacesWithId(id, false) + ")");
            bool[] regions = SandBox.SandBoxHelpers.MapSceneHelper.GetRegionMapping(installed);
            scene.SetNavMeshRegionMap(regions);
            return new Dictionary<string, object> { ["model"] = model.Source, ["disabledBeforeRead"] = disabled,
                ["landRegion"] = Enumerable.Range(0, regions.Length).Where(t => regions[t]).Select(t => (TerrainTypes.Name(t) ?? "?") + " " + t).ToArray(), ["regionMapEqualsModel"] = model.SameRegions(regions) };
        }
    }

    // The validator's engine calls on a constructed world (guarded: engine thread, live, not superseded).
    internal sealed class SceneNavigationEngine : INavigationEngine
    {
        private readonly RuntimeWorld world;
        public SceneNavigationEngine(RuntimeWorld world) { this.world = world; }
        public string Name { get { return "TaleWorlds.Engine.Scene navmesh of " + world.SceneName; } }
        public bool Face(double x, double y, bool land, out int face, out int group, out int island) { return world.NavFace(x, y, land, out face, out group, out island); }
        public bool Path(int fromFace, int toFace, double[] from, double[] to, double radius, int[] excluded, double extraCost, int costToSea, int costToLand, out List<double[]> points)
        {
            var path = new NavigationPath();
            bool found = world.NavPath(fromFace, toFace, new Vec2((float)from[0], (float)from[1]), new Vec2((float)to[0], (float)to[1]), (float)radius, excluded, (float)extraCost, costToSea, costToLand, path);
            points = found ? Enumerable.Range(0, path.Size).Select(i => new double[] { path.PathPoints[i].x, path.PathPoints[i].y }).ToList() : null;
            return found;
        }
        public bool Distance(int fromFace, int toFace, double[] from, double[] to, double radius, double limit, int[] excluded, int costToSea, int costToLand, out double distance)
        {
            float d; bool ok = world.NavDistance(fromFace, toFace, new Vec2((float)from[0], (float)from[1]), new Vec2((float)to[0], (float)to[1]), (float)radius, (float)limit, excluded, costToSea, costToLand, out d);
            distance = d; return ok;
        }
        public int FaceCount { get { return world.NavFaceCount(); } }
        public int FaceGroup(int face) { return world.NavFaceGroup(face); }
        public double[] FaceCenter(int face) { var c = world.NavFaceCenter(face); return new double[] { c.x, c.y, c.z }; }
    }
}
