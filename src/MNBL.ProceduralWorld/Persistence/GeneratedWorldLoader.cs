using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Surface;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MNBL.ProceduralWorld.Persistence
{
    // MMR-49: the production load of a generated campaign's save, from the normal Load and Continue entries (LoadRouting takes a save with a
    // world binding to it after the installed load's own checks and prompts). The world is restored before anything deserializes, and every
    // check runs before anything changes, in this order:
    //   1. discover the binding: the save's header read from the game's save list (BannerlordSaveCatalog: readable, torn, or held by another
    //      program), its mnbl.world.* binding, the whole file (SaveFileCheck), the recorded build against the running one
    //      (LoadPolicy.Compatibility); the immutable world: the package the binding names (every file against its manifest, the header's scene
    //      and seed) and, for a compiled world, the data bundle by its content id (WorldDataBundle.OpenForSave);
    //   2. check the derived resources: the installed scene (paired with this module and resolved to its folder) or, when it is gone from
    //      the module (MMR-282: a module update replaced the folder), its copy in the per-user scene archive, or else its re-encoding from the
    //      bundle by the surface encoder (never the generator) into staging; either must be the package's byte for byte; the navigation,
    //      playability and settlement layout reports, installed or kept with the campaign at its activation, through their gates; then the
    //      campaign rebuilt from the bundle (WorldReconstruction), whose documents, coordinate frame and climate must be the saved world's,
    //      its replay plan, the map scene and the periodic patches;
    //   2b. MMR-282 (RT-38): when the navigation caches will be rebuilt (no published cache of the scene from this module build and game,
    //      CacheForecast), the player is told how long first (CacheBuildModel, or this world's last build on this computer) and may cancel;
    //   3. end the running campaign, if any, through the installed exit (only one on its map; its teardown releases the old world);
    //   4. install what the checks proved: the archived or re-encoded scene (an installed one is archived if it was not) and the reports
    //      restored from the campaign's copies;
    //   5. deserialize the save (MBSaveLoad.LoadSaveGameData, which LoadRouting lets only this loader open for a generated save);
    //   6. start the load (GeneratedLoadManager): the documents replay onto the deserialized objects in RegisterSubModuleObjects(true), the map
    //      scene loads with its periodic frame, climate and colour grade, the navigation caches and the battle terrain install, and the
    //      installed saved branch resumes the campaign (positions and caches resolve as the engine loads them; time runs once the map is up).
    // A refusal in 1-2 changes nothing: no file is written (the encoder's staging is deleted, and a settlement report the encoder wrote beside
    // it is removed again) and a running campaign keeps running; the player sees why and what to do. One exception: a swept scene cannot be
    // re-encoded while another campaign's map is live (WorldGenerations), so its encoding and the checks that read it run after step 3. A
    // refusal after step 3 leaves the player at the main menu with every save and world unchanged; a failure after the start ends the game
    // through the installed exit. A load runs on a worker; every engine call is posted to the engine thread (EngineWork). It records the
    // processes this module started meanwhile (GenerationProbe.ProcessStarts): a load never starts the generator; it starts the surface
    // encoder only to re-encode a swept scene.
    internal sealed class GeneratedWorldLoader
    {
        public enum Phase { Planning, Reconstructing, EndingGame, Installing, Deserializing, Starting, Loading, Loaded, Refused, Failed, Declined }
        private const int ReportsKept = 20;
        private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(20);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private static int sequence;

        public static GeneratedWorldLoader Current { get; private set; }
        public static bool Busy { get { var c = Current; return c != null && !c.Done; } }
        // A loader that still references a game manager (and through it a game): none once its game ended (ReleaseEnded).
        public static bool HoldsGame { get { var c = Current; return c != null && c.Manager != null; } }

        public string SaveName { get; private set; }
        public Phase State { get { return state; } }
        // Set last, after the report is written: a reader of Report sees it complete.
        public bool Done { get { return finished; } }
        public LoadPolicy.Refusal Refusal { get; private set; }
        public string Error { get; private set; }
        public GeneratedLoadManager Manager { get; private set; }
        public WorldBinding Binding { get; private set; }
        public readonly Dictionary<string, object> Report = new Dictionary<string, object>();
        public readonly List<string> Notes = new List<string>();

        private volatile Phase state = Phase.Planning;
        private volatile bool finished;
        private bool started;
        private readonly Action onCancel;
        private readonly Action<string> trace;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<Dictionary<string, object>> phases = new List<Dictionary<string, object>>();

        private sealed class Refused : Exception
        {
            public readonly LoadPolicy.Refusal Refusal;
            public Refused(LoadPolicy.Refusal refusal) : base(refusal.ToString()) { Refusal = refusal; }
        }
        private static Refused Refuse(string code, string detail) { return new Refused(LoadPolicy.Refuse(code, detail)); }

        // Every trace line (the loader's and the load's game manager's) is appended at once to <store>/loads/trace-<pid>.log: a load that hangs
        // the engine still leaves where it stopped.
        private GeneratedWorldLoader(string saveName, Action onCancel, Action<string> trace)
        {
            SaveName = saveName; this.onCancel = onCancel;
            var sink = trace ?? (s => { });
            string path = Path.Combine(SubModule.Store, "loads", "trace-" + Process.GetCurrentProcess().Id + ".log");
            this.trace = line =>
            {
                try { lock (TraceGate) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.AppendAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine); } }
                catch (Exception) { }
                sink(line);
            };
        }
        private static readonly object TraceGate = new object();

        // Engine thread. Starts loading the save; null when a load may not start now (another load: a message; a generation job, an
        // activation, a campaign off its map: the busy refusal), after which onCancel runs as the installed load's cancel does.
        public static GeneratedWorldLoader Begin(string saveName, Action onCancel, Action<string> trace)
        {
            if (Busy)
            {
                InformationManager.DisplayMessage(new InformationMessage(Localized(LoadPolicy.Texts.AlreadyLoading, null)));
                if (onCancel != null) onCancel();
                return null;
            }
            string busy = BusyNow();
            if (busy != null)
            {
                var refusal = LoadPolicy.Refuse(LoadPolicy.Busy, busy);
                ShowRefusal(Localized(refusal.Title, null), Localized(refusal.Message, refusal.Variables) + "\n\n(" + refusal + ")", onCancel);
                return null;
            }
            var loader = new GeneratedWorldLoader(saveName, onCancel, trace);
            Current = loader;
            WorldMutationBehavior.CancelForLoad();   // MMR-261: a running fwg apply never runs into a load
            loader.Report["saveName"] = saveName; loader.Report["processId"] = Process.GetCurrentProcess().Id; loader.Report["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            // As the installed load does (SandBoxSaveHelper's Start and LoadGame states): the loading window, which also keeps the player from
            // acting meanwhile; a running campaign's time stops while its successor is checked.
            TaleWorlds.Engine.LoadingWindow.EnableGlobalLoadingWindow();
            if (TaleWorlds.CampaignSystem.Campaign.Current != null) TaleWorlds.CampaignSystem.Campaign.Current.TimeControlMode = TaleWorlds.CampaignSystem.CampaignTimeControlMode.Stop;
            Task.Run(() => loader.Run());
            return loader;
        }

        // Engine thread: why a load may not start now (null when it may).
        private static string BusyNow()
        {
            var game = Game.Current;
            return LoadPolicy.BusyReason(Busy, GenerationRuntime.ActiveJobPhase(), ActivationSaveBlock.Active, game != null,
                game != null && game.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState);
        }

        // When the game this loader started ends (GeneratedGameManager.OnGameEnd): the loader lets go of it, and a finished loader is no longer
        // current, so nothing of an ended campaign stays reachable from here.
        internal static void ReleaseEnded(GeneratedGameManager ended)
        {
            var c = Current;
            if (c == null || ended == null || !ReferenceEquals(c.Manager, ended)) return;
            c.Manager = null;
            if (c.Done) Current = null;
        }

        private void Run()
        {
            var startsBefore = Starts();
            SceneWork work = null;
            try
            {
                Enter(Phase.Planning);
                var plan = Discover();
                Enter(Phase.Reconstructing);
                Restored restored;
                if (plan.Compiled) { work = new SceneWork(); restored = CheckCompiled(plan, work); }
                else restored = CheckFixture(plan);
                bool scenePresent = work == null || work.State == LoadPolicy.SceneState.Installed;
                if (scenePresent) CheckStart(plan, restored);
                Phases("checked");
                if (plan.Compiled) ConfirmCacheBuild(plan);
                EndRunningGame();
                if (work != null) { Enter(Phase.Installing); InstallCompiled(plan, work); }
                if (!scenePresent) CheckStart(plan, restored);
                Enter(Phase.Deserializing);
                var saved = Engine("deserialize the save", () => LoadRouting.Deserialize(() => MBSaveLoad.LoadSaveGameData(SaveName)));
                if (saved == null || !saved.Successful) throw Refuse(LoadPolicy.DamagedSave, "the game could not deserialize the save");
                Enter(Phase.Starting);
                var support = new Dictionary<string, object>();
                Report["support"] = support;
                started = true;
                var manager = Engine("start the load", () =>
                {
                    if (restored.Terrain != null) GeneratedBattleTerrain.ContextSources[plan.Scene] = restored.Terrain;
                    if (restored.Dressing != null) MNBL.ProceduralWorld.World.MapDressingRuntime.Plans[plan.Scene] = restored.Dressing;
                    if (restored.Overlay != null) MNBL.ProceduralWorld.World.MagicGroundRuntime.Overlays[plan.Scene] = restored.Overlay;
                    if (restored.Mutation != null) WorldMutationBehavior.Contexts[plan.Scene] = restored.Mutation;   // MMR-261
                    if (restored.Roads != null) GeneratedPartySpeedModel.RoadSources[plan.Scene] = restored.Roads;
                    return GeneratedLoadManager.Start(new GeneratedLoadManager.LoadStart { World = restored.World, SceneName = plan.Scene, SupportReport = support, Trace = trace,
                        Periodic = restored.Periodic, Climate = restored.Climate, Saved = saved, Identity = plan.Identity });
                });
                Manager = manager;
                Enter(Phase.Loading);
                var loading = Stopwatch.StartNew();
                string progress;
                while ((progress = EngineProgress(manager)) == null)
                {
                    if (loading.Elapsed.TotalSeconds > 3600) throw new TimeoutException("The campaign did not reach its map within an hour.");
                    Thread.Sleep(250);
                }
                if (progress != "map") throw new InvalidOperationException(progress);
                // MMR-283: the map's AfterLoad never throws; what failed there (the navigation caches, the battle map) ends the load here.
                object afterLoad; support.TryGetValue("afterLoadFailure", out afterLoad);
                var battleMap = support.ContainsKey("battleSceneSelection") ? support["battleSceneSelection"] as Dictionary<string, object> : null;
                if (afterLoad == null && battleMap != null && Equals(battleMap["status"], "failed"))
                {
                    afterLoad = "the battle map: " + battleMap["error"];
                    // A battle table the map refused is named as a content conflict (module and scene), as CheckStart would have.
                    if (battleMap.ContainsKey("lost")) Refusal = ContentRefusal(new List<CatalogPin.Conflict> { ContentCompatibility.BattleConflict(Engine("check the battle table", () => GeneratedBattleTerrain.CheckModuleTables())) });
                }
                if (afterLoad != null) throw new InvalidOperationException("the campaign map could not be completed: " + afterLoad);
                Report["loadSeconds"] = loading.Elapsed.TotalSeconds;
                Enter(Phase.Loaded);
                Engine("loaded message", () => { InformationManager.DisplayMessage(new InformationMessage(Localized(LoadPolicy.Texts.Restored, null))); return true; });
            }
            catch (Exception raw)
            {
                var error = raw is AggregateException && raw.InnerException != null ? raw.InnerException : raw;
                var refused = error as Refused;
                if (error is Declined) Enter(Phase.Declined);
                else if (refused != null) { Refusal = refused.Refusal; Enter(Phase.Refused); }
                else if (!started && !(error is EngineStopped))
                {
                    // Before the start an unexpected error is a refusal too: nothing started, and nothing of the world was written.
                    Report["exception"] = error.ToString();
                    Refusal = LoadPolicy.Refuse(LoadPolicy.Failed, error.GetType().Name + ": " + error.Message);
                    Enter(Phase.Refused);
                }
                else { Error = error.GetType().Name + ": " + error.Message; Report["exception"] = error.ToString(); Enter(Phase.Failed); }
                // A stopped engine answers nothing more; anything else shows why and, for a game that reached its map, returns to the menu. A
                // player who cancelled at the navigation warning goes back to where the load was started, with nothing changed.
                if (state == Phase.Declined) { try { Engine("leave the declined load", () => { Dismissed(onCancel); return true; }); } catch (Exception) { } }
                else if (!(error is EngineStopped))
                {
                    if (state == Phase.Failed) ReturnToMenu();
                    try { Engine("show the outcome", () => { ShowOutcome(); return true; }); } catch (Exception) { }
                }
            }
            finally
            {
                if (work != null) work.Release(state != Phase.Loaded && !work.Installed, Report);
                if (state != Phase.Loaded) Manager = null;
                var after = Starts();
                var processes = after.Keys.Union(startsBefore.Keys).ToDictionary(k => k, k => (object)((after.ContainsKey(k) ? after[k] : 0) - (startsBefore.ContainsKey(k) ? startsBefore[k] : 0)));
                Report["processesStarted"] = processes.Where(kv => (int)kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                // Any process but the surface encoder is the generator's (request, generate, bundle, producer): a load starts none of them.
                Report["generatorStarted"] = processes.Any(kv => kv.Key != "surface_encoder" && (int)kv.Value > 0);
                Report["state"] = state.ToString(); Report["refusal"] = Refusal == null ? null : new Dictionary<string, object> { ["code"] = Refusal.Code, ["detail"] = Refusal.Detail };
                Report["error"] = Error; Report["notes"] = Notes.ToList(); Report["phases"] = phases; Report["seconds"] = watch.Elapsed.TotalSeconds;
                WriteReport();
                finished = true;
                trace("generated_load_finished:" + state);
            }
        }

        // ---------------------------------------------------------------- 1. the binding and the immutable world

        private sealed class LoadPlan
        {
            public bool Compiled;
            public string Scene, PackageDirectory, Instance;
            public Dictionary<string, object> Manifest;
            public Dictionary<string, string> SceneFiles = new Dictionary<string, string>(StringComparer.Ordinal), Documents = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Identity, Header;
            public WorldDataBundle Bundle;
        }

        private LoadPlan Discover()
        {
            var header = Engine("read the save's header", () => SaveBindingRuntime.Store.Saves.Read(SaveName));
            if (header == null) throw Refuse(LoadPolicy.DamagedSave, "the save is not in the save folder");
            if (header.HeaderUnknown) throw Refuse(LoadPolicy.Unavailable, "its header cannot be read now (" + header.HeaderProblem + ")");
            if (!header.Readable) throw Refuse(LoadPolicy.DamagedSave, "its header cannot be read");
            WorldBinding binding;
            try { binding = WorldBinding.FromHeader(header.Metadata); }
            catch (InvalidDataException error) { throw Refuse(LoadPolicy.DamagedSave, "its world binding is malformed: " + error.Message); }
            if (binding == null) throw Refuse(LoadPolicy.NotGenerated, "the header names no generated world");
            Binding = binding;
            // MMR-50: game data another program holds is not damage (WorldCompatibilityPolicy reports it the same).
            if (header.BodyWhole == null) throw Refuse(LoadPolicy.Unavailable, "its game data cannot be read now" + (header.BodyProblem == null ? "" : " (" + header.BodyProblem + ")"));
            if (header.BodyWhole != true) throw Refuse(LoadPolicy.DamagedSave, "its game data is not whole: " + (header.BodyProblem ?? "torn"));
            Report["binding"] = new Dictionary<string, object> { ["worldId"] = binding.WorldId, ["package"] = binding.PackageName, ["instance"] = binding.Instance, ["contentId"] = binding.ContentId };
            var running = Engine("the running build", () => WorldSaveBinding.Compatibility());
            var savedBuild = SaveBindingContract.CompatibilityFrom(header.Metadata);
            var refusal = savedBuild.Count == 0 ? LoadPolicy.LegacyCompatibility(LoadPolicy.LegacyBuild(header.Metadata), running, Notes) : LoadPolicy.Compatibility(savedBuild, running, Notes);
            if (refusal != null) throw new Refused(refusal);
            // MMR-284: a supported build loads only when every patch the campaign needs still finds its target (by signature and, for the
            // wanderer introduction, IL content); a game update that changed one refuses here, naming the patch, before anything changes.
            var patch = Engine("check the patch signatures", () => GeneratedPatches.Verify()).FirstOrDefault();
            if (patch != null) throw new Refused(LoadPolicy.Patch(patch.Patch, patch.Target, patch.Message));
            var plan = new LoadPlan { Compiled = binding.ContentId != WorldBinding.NoContent, Instance = binding.Instance, PackageDirectory = Path.Combine(GeneratedWorldPackage.Root, binding.PackageName) };
            if (plan.Compiled && !ActivationRequest.ValidInstance(binding.Instance)) throw Refuse(LoadPolicy.DamagedSave, "its campaign instance '" + binding.Instance + "' is not a campaign instance id");
            plan.Header = header.Metadata.Where(kv => kv.Key.StartsWith(WorldBinding.Prefix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key.Substring(WorldBinding.Prefix.Length), kv => kv.Value, StringComparer.Ordinal);
            plan.Identity = new Dictionary<string, string>(plan.Header, StringComparer.Ordinal);
            if (!Directory.Exists(plan.PackageDirectory)) throw Refuse(LoadPolicy.MissingWorld, "no world package " + binding.PackageName);
            var problems = WorldSaveTransfer.PackageProblems(plan.PackageDirectory, binding.ManifestSha256, binding.WorldId);
            if (problems.Count > 0) throw Refuse(LoadPolicy.DamagedWorld, "package " + binding.PackageName + ": " + String.Join("; ", problems.Take(5)));
            try
            {
                plan.Manifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(plan.PackageDirectory, "world.json")));
                plan.Scene = Convert.ToString(plan.Manifest["sceneName"], CultureInfo.InvariantCulture);
                foreach (var kv in (Dictionary<string, object>)plan.Manifest["sceneFiles"]) plan.SceneFiles[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                foreach (Dictionary<string, object> row in (IEnumerable)plan.Manifest["documents"]) plan.Documents[Convert.ToString(row["id"], CultureInfo.InvariantCulture)] = Convert.ToString(row["sha256"], CultureInfo.InvariantCulture);
            }
            catch (Exception error) when (error is InvalidCastException || error is KeyNotFoundException || error is ArgumentException || error is InvalidOperationException)
            {
                throw Refuse(LoadPolicy.DamagedWorld, "package " + binding.PackageName + ": its world.json is not a package manifest (" + error.Message + ")");
            }
            string savedScene, savedSeed;
            if (!plan.Header.TryGetValue("scene", out savedScene) || savedScene != plan.Scene || !plan.Header.TryGetValue("seed", out savedSeed) || savedSeed != Convert.ToString(plan.Manifest["seed"], CultureInfo.InvariantCulture))
                throw Refuse(LoadPolicy.DamagedSave, "the save's header and its world package name different worlds");
            Report["scene"] = plan.Scene;
            if (plan.Compiled)
            {
                var identity = new Dictionary<string, string>(StringComparer.Ordinal) { ["schema"] = WorldDataBundle.SaveIdentitySchema, ["worldContentId"] = binding.ContentId };
                try { plan.Bundle = WorldDataBundle.OpenForSave(CompileStage.BundleRoot(), identity, UI.WorldSetupHost.BundleHost()); }
                catch (WorldDataBundle.RejectedException error)
                {
                    string kind = WorldInspector.Classify(error.Problems);
                    throw Refuse(kind == WorldInspector.Missing ? LoadPolicy.MissingWorld : kind == WorldInspector.Incompatible ? LoadPolicy.IncompatibleBuild : LoadPolicy.DamagedWorld,
                        "bundle " + binding.ContentId.Substring(0, 12) + ": " + String.Join("; ", error.Problems.Take(5)));
                }
            }
            Phases("planned");
            return plan;
        }

        // ---------------------------------------------------------------- 2. the derived resources and the campaign, checked

        private sealed class Restored
        {
            public IGeneratedWorld World;
            public PeriodicWorld Periodic;
            public GeneratedClimate Climate;
            public Func<double, double, GeneratedBattleTerrain.TerrainContext> Terrain;
            public MapDressingPlan Dressing;   // MMR-257
            public WorldSurfaceOverlay Overlay;   // MMR-260
            public MutationModel.Context Mutation;   // MMR-261
            public MNBL.ProceduralWorld.World.RoadSpeed.Cells Roads;
        }

        // What a compiled world's scene and reports need, from the checks to the install.
        private sealed class SceneWork
        {
            public LoadPolicy.SceneState State;
            public bool Deferred, Installed, SettlementWritten, FromArchive;
            public GenerationToken Token;
            public PreparedSurface Prepared;
            public string LayoutSha256;
            public string NavigationReportPath;   // MMR-257: the report CheckReports read (the scene's, or the copy kept with the campaign)
            public readonly List<KeyValuePair<string, byte[]>> Restore = new List<KeyValuePair<string, byte[]>>();
            public readonly List<string> RestoredNames = new List<string>(), Rederived = new List<string>();

            // The encoder's staging always goes; on a refusal before the install, so does the settlement report the encoder wrote.
            public void Release(bool refusedBeforeInstall, Dictionary<string, object> report)
            {
                if (Prepared != null)
                {
                    if (refusedBeforeInstall && SettlementWritten && Prepared.SettlementReportPath != null)
                        try { File.Delete(Prepared.SettlementReportPath); report["settlementReportRemoved"] = !File.Exists(Prepared.SettlementReportPath); }
                        catch (Exception error) { report["settlementReportRemoved"] = error.Message; }
                    Prepared.Discard();
                    report["encoderStagingDeleted"] = Prepared.JobDeleted;
                }
                if (Token != null) WorldGenerations.Retire(Token);
            }
        }

        private Restored CheckCompiled(LoadPlan plan, SceneWork work)
        {
            // The scene.
            string folder = RuntimeWorldBackend.SceneFolder(plan.Scene);
            var installed = plan.SceneFiles.Keys.ToDictionary(f => f, f => File.Exists(Path.Combine(folder, f)) ? GenerationProbe.Hash(Path.Combine(folder, f)) : null, StringComparer.Ordinal);
            work.State = LoadPolicy.Scene(plan.SceneFiles, installed);
            Report["sceneState"] = work.State.ToString();
            if (work.State == LoadPolicy.SceneState.Different) throw Refuse(LoadPolicy.NotRebuildable, "the installed scene " + plan.Scene + " differs from the package's");
            if (work.State == LoadPolicy.SceneState.Installed)
            {
                try { Report["sceneResolved"] = Engine("pair the installed scene", () => RuntimeWorldBackend.UseInstalled(plan.Scene)); }
                catch (IOException error) { throw Refuse(LoadPolicy.NotRebuildable, "the installed scene: " + error.Message); }
            }
            // MMR-282: the archived copy, when it is the package's, is copied back at the install (no encoder, so a later encoder that would
            // name the world differently does not matter).
            else if (LoadPolicy.Scene(plan.SceneFiles, WorldSceneArchive.Hashes(RuntimeWorldBackend.SceneArchiveFolder(plan.Scene), plan.SceneFiles.Keys)) == LoadPolicy.SceneState.Installed)
            { work.FromArchive = true; Report["sceneSource"] = "archive"; }
            else if (WorldGenerations.LiveCampaignMap == 0) Encode(plan, work);
            else { work.Deferred = true; Report["encodeDeferred"] = "another campaign's map is live: the swept scene is re-encoded after it ends"; }
            // The derived reports.
            var layout = CheckReports(plan, work);
            Phases("scene and reports");
            // The campaign, rebuilt from the bundle and required to be the saved world's.
            WorldSurface surface; PeriodicWorldCoordinates coordinates;
            try { surface = WorldSurface.FromBundle(plan.Bundle); coordinates = PeriodicWorldCoordinates.FromSurface(surface); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the world's surface: " + error.Message); }
            string savedFrame; plan.Header.TryGetValue("coordinatePolicy", out savedFrame);
            var refusal = LoadPolicy.Rebuilt("coordinate frame", savedFrame, coordinates.FrameIdentity);
            if (refusal != null) throw new Refused(refusal);
            // MMR-283: rebuilt from the catalog frozen in the package at activation, so later stat changes never change the world; the live
            // catalog is then checked only for the installed objects the world references (and stock objects added since).
            WorldReconstruction.Frozen frozen;
            try { frozen = WorldReconstruction.PackageCatalog(plan.PackageDirectory); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.DamagedWorld, "package " + Binding.PackageName + ": " + error.Message); }
            // MMR-247: the report inside the navigation record CheckReports chose (the scene's, or the copy kept with the campaign: both records).
            IDictionary<string, object> navigationReport;
            try { navigationReport = NavigationGate.ReadAt(work.NavigationReportPath, plan.Scene); }
            catch (NavigationRefusedException error) { throw Refuse(LoadPolicy.NotRebuildable, "navigation: " + error.Message); }
            WorldReconstruction.World world;
            try { world = WorldReconstruction.Build(plan.Bundle, layout, coordinates, surface, frozen, navigationReport); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the campaign rebuild failed: " + error.Message); }
            refusal = LoadPolicy.RebuiltDocuments(plan.Documents, world.Dataset.DocumentSha256);
            if (refusal != null) throw new Refused(refusal);
            List<CatalogPin.Conflict> conflicts;
            try { conflicts = WorldReconstruction.LoadConflicts((label, step) => Engine(label, step), world, frozen); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the installed content could not be checked: " + error.Message); }
            Report["contentConflicts"] = conflicts.Select(c => c.ToString()).Take(20).ToList();
            if (conflicts.Count > 0) throw new Refused(ContentRefusal(conflicts));
            if (world.Dataset.WorldId != Binding.WorldId) throw Refuse(LoadPolicy.NotRebuildable, "the rebuilt world is " + world.Dataset.WorldId);
            string savedClimate; plan.Header.TryGetValue("climate", out savedClimate);
            refusal = LoadPolicy.Rebuilt("climate", savedClimate, world.Climate == null ? "none" : world.Climate.Pattern);
            if (refusal != null) throw new Refused(refusal);
            Report["rebuilt"] = new Dictionary<string, object> { ["documents"] = world.Dataset.DocumentSha256.Count, ["graphSha256"] = world.Dataset.Graph.Sha256, ["frame"] = coordinates.FrameIdentity,
                ["climate"] = world.Climate == null ? "none" : world.Climate.Pattern };
            Phases("campaign rebuilt");
            CompiledWorldRegistration registration;
            IList<string> preflight;
            try
            {
                registration = CompiledWorldRegistration.ForLoad(world.Dataset, plan.Scene, ActivationRequest.Uuid(plan.Instance));
                preflight = registration.ReplayPreflight();
            }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the replay plan: " + error.Message); }
            if (preflight.Count > 0) throw Refuse(LoadPolicy.NotRebuildable, "the replay plan: " + String.Join("; ", preflight.Take(5)));
            Phases("replay plan checked");
            return new Restored { World = registration, Periodic = new PeriodicWorld(coordinates), Climate = world.Climate, Terrain = world.TerrainContext, Dressing = world.Dressing,
                Roads = world.Roads, Overlay = world.Overlay, Mutation = world.Mutation };
        }

        // A swept scene, re-encoded from the bundle by the surface encoder into staging; it must be the package's, byte for byte.
        private void Encode(LoadPlan plan, SceneWork work)
        {
            bool settlementExisted = File.Exists(RuntimeWorldBackend.SettlementReportPath(plan.Scene));
            work.Token = WorldGenerations.Begin("load " + plan.Scene);
            using (var cancel = new CancellationTokenSource(EncodeTimeout))
            {
                // MMR-258: the package's frozen looks, not the installed campaign map (a patch since generation leaves the scene as built).
                FrozenLooks looks;
                try { looks = FrozenLooks.Read(file => File.ReadAllBytes(Path.Combine(plan.PackageDirectory, file.Replace('/', Path.DirectorySeparatorChar))), MNBL.ProceduralWorld.World.SettlementLooksPolicy.Installed()); }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException) { throw Refuse(LoadPolicy.DamagedWorld, "package " + Binding.PackageName + ": its settlement looks: " + error.Message); }
                try { work.Prepared = RuntimeWorldBackend.Prepare(plan.Bundle, work.Token, cancel.Token, null, looks); }
                catch (OperationCanceledException) { throw Refuse(LoadPolicy.NotRebuildable, "the surface encoder did not finish within " + EncodeTimeout.TotalMinutes + " min"); }
                catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the scene could not be re-encoded: " + error.GetType().Name + ": " + error.Message); }
                finally { work.SettlementWritten = !settlementExisted && File.Exists(RuntimeWorldBackend.SettlementReportPath(plan.Scene)); }
            }
            var prepared = work.Prepared;
            if (prepared.SceneName != plan.Scene) throw Refuse(LoadPolicy.NotRebuildable, "this build encodes the world as " + prepared.SceneName + ", the save's scene is " + plan.Scene);
            var differs = plan.SceneFiles.Where(kv => { string h; return !prepared.Files.TryGetValue(kv.Key, out h) || h != kv.Value; }).Select(kv => kv.Key).ToList();
            if (differs.Count > 0) throw Refuse(LoadPolicy.NotRebuildable, "the re-encoded scene differs from the package's: " + String.Join(", ", differs));
            Report["encodeSeconds"] = prepared.EncodeSeconds;
            if (work.SettlementWritten) work.Rederived.Add("settlement-layout.json");
        }

        // The navigation, settlement layout and playability reports a load needs: each installed, re-derived by the encoder (the layout), or
        // the copy the activation kept with the campaign (activations/<instance>/evidence), read now and written only at the install. Each
        // passes its gate against the scene's navmesh (installed or re-encoded), and a layout that is not the kept one refuses. Returns the
        // layout. While the encoding is deferred only the copies' presence and the layout are checked; the gates run after the encoding.
        private IDictionary<string, object> CheckReports(LoadPlan plan, SceneWork work)
        {
            string evidence = Path.Combine(new ActivationJournalStore(SubModule.Store).InstanceDirectory(plan.Instance), "evidence");
            var rows = new[] { new { Kept = "navigation.json", Path = NavigationGate.ReportPath(SubModule.Store, plan.Scene) },
                               new { Kept = "settlement-layout.json", Path = RuntimeWorldBackend.SettlementReportPath(plan.Scene) },
                               new { Kept = "playability.json", Path = RuntimePlayabilityValidator.ReportPath(SubModule.Store, plan.Scene) } };
            work.Restore.Clear(); work.RestoredNames.Clear();
            var source = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (File.Exists(row.Path)) { source[row.Kept] = row.Path; continue; }
                string kept = Path.Combine(evidence, row.Kept);
                if (!File.Exists(kept)) throw Refuse(LoadPolicy.NotRebuildable, "the world's " + row.Kept + " is missing and was not kept with the campaign");
                source[row.Kept] = kept;
                try { work.Restore.Add(new KeyValuePair<string, byte[]>(row.Path, File.ReadAllBytes(kept))); }
                catch (IOException error) { throw Refuse(LoadPolicy.NotRebuildable, "the campaign's kept " + row.Kept + " cannot be read: " + error.Message); }
                work.RestoredNames.Add(row.Kept);
            }
            work.NavigationReportPath = source["navigation.json"];   // MMR-257: the map dressing's bridges
            IDictionary<string, object> layout = ReadReport(source["settlement-layout.json"], "settlement layout report");
            if (!Equals(Value(layout, "worldContentId"), Binding.ContentId) || !Equals(Value(layout, "activatable"), true)) throw Refuse(LoadPolicy.NotRebuildable, "the settlement layout report is not this world's");
            string keptLayout = Path.Combine(evidence, "settlement-layout.json");
            if (File.Exists(keptLayout) && !String.Equals(source["settlement-layout.json"], keptLayout, StringComparison.OrdinalIgnoreCase)
                && !Equals(Value(ReadReport(keptLayout, "kept settlement layout report"), "layoutSha256"), Value(layout, "layoutSha256")))
                throw Refuse(LoadPolicy.NotRebuildable, "the scene's settlement layout is not the one the campaign was activated with");
            string layoutSha = Convert.ToString(Value(layout, "layoutSha256"), CultureInfo.InvariantCulture);
            if (work.LayoutSha256 != null && work.LayoutSha256 != layoutSha) throw Refuse(LoadPolicy.NotRebuildable, "the re-encoded scene's settlement layout is not the one the campaign was rebuilt with");
            work.LayoutSha256 = layoutSha;
            string navmesh = work.Prepared != null ? Path.Combine(work.Prepared.Output, "navmesh.bin") : work.FromArchive ? Path.Combine(RuntimeWorldBackend.SceneArchiveFolder(plan.Scene), "navmesh.bin")
                : work.Deferred ? null : Path.Combine(RuntimeWorldBackend.SceneFolder(plan.Scene), "navmesh.bin");
            if (navmesh != null)
            {
                try { NavigationGate.RequireAt(source["navigation.json"], plan.Scene, navmesh, NavigationPolicy.Installed().Sha256); }
                catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "navigation: " + error.Message); }
                try { RuntimePlayabilityValidator.RequireAt(source["playability.json"], plan.Scene, source["navigation.json"], source["settlement-layout.json"]); }
                catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "playability: " + error.Message); }
            }
            return layout;
        }

        private static IDictionary<string, object> ReadReport(string path, string what)
        {
            try
            {
                var report = Json.DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
                if (report == null) throw new InvalidDataException("not a JSON object");
                return report;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                throw Refuse(LoadPolicy.NotRebuildable, "the " + what + " cannot be read: " + error.Message);
            }
        }

        private static object Value(IDictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v : null; }

        private Restored CheckFixture(LoadPlan plan)
        {
            GeneratedWorldPackage package;
            try { package = Engine("verify the fixture package", () => GeneratedWorldPackage.Load(plan.PackageDirectory, Binding.ManifestSha256)); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the fixture world's package or installed scene: " + error.Message); }
            try { WorldIdentity.RequireMatch(plan.Header, package); }
            catch (InvalidDataException error) { throw Refuse(LoadPolicy.DamagedSave, error.Message); }
            var registration = GeneratedCampaignRegistration.FromInstalledPackage(package);
            Phases("fixture package");
            return new Restored { World = registration, Periodic = registration.Periodic, Climate = registration.Climate };
        }

        // MMR-283: the first conflict as the player's refusal (its module and object), the rest in the detail.
        private static LoadPolicy.Refusal ContentRefusal(List<CatalogPin.Conflict> conflicts)
        {
            var c = conflicts[0];
            return LoadPolicy.Content(c.Kind, c.Key, c.Module, c.Official, c.Target, String.Join("; ", conflicts.Take(10)) + (conflicts.Count > 10 ? " (" + (conflicts.Count - 10) + " more)" : ""));
        }

        // What GeneratedLoadManager.Start would refuse on, checked first: the map scene's files and the periodic patch targets; MMR-283: and
        // the battle table the campaign will load, before its navigation caches are built.
        private void CheckStart(LoadPlan plan, Restored restored)
        {
            var battle = Engine("check the battle table", () => GeneratedBattleTerrain.CheckModuleTables());
            Report["battleTable"] = battle.Record();
            if (!battle.Ok) throw new Refused(ContentRefusal(new List<CatalogPin.Conflict> { ContentCompatibility.BattleConflict(battle) }));
            try
            {
                Engine("check the map scene and the periodic patches", () =>
                {
                    GeneratedMapSceneCreator.ValidateFixture(plan.Scene);
                    if (restored.Periodic != null) PeriodicPatches.Preflight(restored.Periodic);
                    return true;
                });
            }
            catch (PatchSignatureException error) { throw new Refused(LoadPolicy.Patch(error.Patch, error.Target, error.Message)); }   // MMR-284
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "the map scene or the periodic patches: " + error.Message); }
            Phases("start checked");
        }

        // ---------------------------------------------------------------- 2b. the navigation caches (MMR-282, RT-38)

        private sealed class Declined : Exception { public Declined() : base("The player cancelled the load at the navigation warning.") { } }

        // Before anything changes: a load whose caches will be rebuilt (CacheForecast) says how long first and waits for the player's answer;
        // Cancel ends the load with nothing changed. A probe run (AutorunHost) is answered for it and recorded. The forecast is advice: when it
        // cannot be made, the load goes on and the report says why.
        private void ConfirmCacheBuild(LoadPlan plan)
        {
            CacheForecast forecast; int settlements; double seconds;
            try
            {
                string game = Engine("the running game and War Sails versions", () => GeneratedPatches.InstalledVersions());
                forecast = CacheForecast.Predict(WorldNavigationCache.PublishedManifests(), plan.Scene, WorldNavigationCache.ModuleSha256(), game);
                settlements = Settlements(plan.Bundle);
                var model = UI.WorldSetupContract.Installed().CacheBuild;
                seconds = forecast.MeasuredSeconds ?? (model == null ? 0 : model.Seconds(settlements));
            }
            catch (Exception error) when (!(error is Refused))
            {
                Report["cacheForecast"] = new Dictionary<string, object> { ["error"] = error.GetType().Name + ": " + error.Message };
                return;
            }
            var record = forecast.Report();
            record["settlements"] = settlements; record["estimateSeconds"] = Math.Round(seconds, 1);
            Report["cacheForecast"] = record;
            if (forecast.Reuse || seconds <= 0) return;
            if (Automation.AutorunHost.Busy) { record["warning"] = "answered by the running probe: load"; trace("generated_load_cache_warning:probe"); return; }
            var vars = new Dictionary<string, string> { ["SAVE"] = SaveName, ["MINUTES"] = CacheBuildModel.Minutes(seconds).ToString(CultureInfo.InvariantCulture),
                ["SETTLEMENTS"] = settlements.ToString(CultureInfo.InvariantCulture) };
            var answer = new TaskCompletionSource<bool>();
            Engine("warn of the navigation rebuild", () =>
            {
                vars["REASON"] = Localized(CacheBuildText.Reason(forecast.Kind), null);
                string text = Localized(forecast.MeasuredSeconds.HasValue ? CacheBuildText.LoadMeasured : CacheBuildText.Load, vars);
                TaleWorlds.Engine.LoadingWindow.DisableGlobalLoadingWindow();
                InformationManager.ShowInquiry(new InquiryData(Localized(CacheBuildText.LoadTitle, null), text, true, true, Localized(CacheBuildText.LoadContinue, null), Localized(CacheBuildText.LoadCancel, null),
                    () => { TaleWorlds.Engine.LoadingWindow.EnableGlobalLoadingWindow(); answer.TrySetResult(true); }, () => answer.TrySetResult(false)), false, false);
                return true;
            });
            trace("generated_load_cache_warning:" + forecast.Kind);
            bool go = answer.Task.Result;
            record["warning"] = go ? "shown: load" : "shown: cancelled";
            if (!go) throw new Declined();
        }

        // The world's towns, castles and villages (the compiled campaign's counts), the model's settlement count.
        private static int Settlements(WorldDataBundle bundle)
        {
            var campaign = (IDictionary<string, object>)Json.DeserializeObject(new System.Text.UTF8Encoding(false).GetString(bundle.ReadVerified("compiled/campaign/campaign.json")).TrimStart('\uFEFF'));
            var counts = (IDictionary<string, object>)campaign["counts"];
            Func<string, int> n = key => Convert.ToInt32(counts[key], CultureInfo.InvariantCulture);
            return CacheBuildModel.Settlements(n("towns"), n("castles"), n("villages"));
        }

        private static string Localized(string text, Dictionary<string, string> variables)
        {
            var t = new TaleWorlds.Localization.TextObject(text);
            if (variables != null) foreach (var kv in variables) t.SetTextVariable(kv.Key, kv.Value);
            return t.ToString();
        }

        // ---------------------------------------------------------------- 3. the running campaign

        private void EndRunningGame()
        {
            if (!Engine("check for a running game", () => Game.Current != null)) return;
            // Ending a game that is not on its map stops the engine (this ticket's runs): the load refuses instead.
            if (!Engine("the running game is on its map", () => Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState))
                throw Refuse(LoadPolicy.Busy, "the running campaign is not on its map");
            Enter(Phase.EndingGame);
            Engine("end the running game", () => { MBGameManager.EndGame(); return true; });
            Report["endedRunningGame"] = true;
            var waited = Stopwatch.StartNew();
            while (!Engine("game ended", () => Game.Current == null && MBGameManager.Current == null))
            {
                if (waited.Elapsed.TotalSeconds > 120) throw new TimeoutException("The running campaign did not end within 120 s.");
                Thread.Sleep(100);
            }
            Engine("the loading window", () => { TaleWorlds.Engine.LoadingWindow.EnableGlobalLoadingWindow(); return true; });
        }

        // ---------------------------------------------------------------- 4. the install

        private void InstallCompiled(LoadPlan plan, SceneWork work)
        {
            if (work.Deferred)
            {
                Encode(plan, work);
                CheckReports(plan, work);
            }
            string folder = RuntimeWorldBackend.SceneFolder(plan.Scene);
            if (work.FromArchive)
            {
                // MMR-282: the archived copy back into the module (each file verified; a partial copy is removed), then registered.
                try { RuntimeWorldBackend.RestoreArchived(plan.Scene, plan.SceneFiles); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { throw Refuse(LoadPolicy.NotRebuildable, "the archived scene could not be restored: " + error.Message); }
                work.Installed = true;
                try { Report["sceneResolved"] = Engine("register the restored scene", () => RuntimeWorldBackend.UseInstalled(plan.Scene)); }
                catch (IOException error) { throw Refuse(LoadPolicy.NotRebuildable, "the restored scene: " + error.Message); }
                Report["sceneRestoredFromArchive"] = true;
            }
            // An installed scene not archived yet (installed before MMR-282, or its copy failed) is archived now.
            else if (work.State == LoadPolicy.SceneState.Installed) Report["sceneArchive"] = RuntimeWorldBackend.KeepArchive(plan.Scene, plan.SceneFiles);
            if (work.Prepared != null)
            {
                bool existed = Directory.Exists(folder), reused;
                try { reused = RuntimeWorldBackend.InstallFiles(work.Prepared); }
                catch (Exception error)
                {
                    // A partial copy would read as a different scene to every later load: it goes.
                    if (!existed) try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception) { }
                    throw Refuse(LoadPolicy.NotRebuildable, "the re-encoded scene could not be installed: " + error.Message);
                }
                work.Installed = true;
                Report["sceneResolved"] = Engine("register the re-installed scene", () => RuntimeWorldBackend.Register(work.Prepared, reused))["resolved"];
                Report["sceneReinstalled"] = true;
            }
            foreach (var report in work.Restore) DurableFile.WriteAllBytes(report.Key, report.Value);
            Report["reportsRestored"] = work.RestoredNames.ToList();
            Report["reportsRederived"] = work.Rederived.ToList();
            try { NavigationGate.Require(SubModule.Store, plan.Scene, Path.Combine(folder, "navmesh.bin"), NavigationPolicy.Installed().Sha256); }
            catch (Exception error) when (!(error is Refused)) { throw Refuse(LoadPolicy.NotRebuildable, "navigation after the install: " + error.Message); }
            Phases("installed");
        }

        // ---------------------------------------------------------------- outcome

        // Engine thread.
        private void ShowOutcome()
        {
            var shown = Refusal ?? LoadPolicy.Refuse(LoadPolicy.Failed, Error);
            string title = Localized(shown.Title, null);
            string message = Localized(shown.Message, shown.Variables);
            if (Report.ContainsKey("endedRunningGame")) message += " " + Localized(LoadPolicy.Texts.EndedRunning, null);
            if (Equals(Report.ContainsKey("returnedToMenu") ? Report["returnedToMenu"] : null, true)) message += " " + Localized(LoadPolicy.Texts.BackAtMenu, null);
            ShowRefusal(title, message + "\n\n(" + (Refusal != null ? Refusal.Code + ": " + Refusal.Detail : Error) + ")", onCancel);
        }

        // MMR-283: a "{=id}text" refusal through TextObject with its variables; plain text as it is (MMR-286: every load message has an id).
        private static string Localized(string text, IDictionary<string, string> variables)
        {
            if (text == null || !text.StartsWith("{=", StringComparison.Ordinal)) return text;
            var t = new TaleWorlds.Localization.TextObject(text);
            if (variables != null) foreach (var kv in variables) t.SetTextVariable(kv.Key, kv.Value);
            return t.ToString();
        }

        // Engine thread: a refusal's message over a hidden loading window. OK runs Dismissed.
        private static void ShowRefusal(string title, string text, Action onCancel)
        {
            TaleWorlds.Engine.LoadingWindow.DisableGlobalLoadingWindow();
            InformationManager.ShowInquiry(new InquiryData(title, text, true, false, Localized(LoadPolicy.Texts.Ok, null), null, () => Dismissed(onCancel), null), false, false);
        }

        // The player read the refusal: behind Continue, the preload state that waits for a game that will not start goes (back to the main
        // menu); then the caller's cancel runs, as the installed load's does.
        private static void Dismissed(Action onCancel)
        {
            var states = GameStateManager.Current;
            if (states != null && states.ActiveState != null && ReferenceEquals(states.ActiveState, LoadRouting.RoutedFrom)) states.PopState(0);
            if (onCancel != null) onCancel();
        }

        // The probe's OK (engine thread): closes the message and does what its OK does.
        internal void Dismiss() { InformationManager.HideInquiry(); Dismissed(onCancel); }

        // After a failed start: the installed exit for this loader's own game, on its map (ending a game while it loads stops the engine,
        // so a load that failed earlier is left to the engine, which returns to the menu itself when its loading fails).
        private void ReturnToMenu()
        {
            try
            {
                var manager = Manager;
                if (manager == null || !Engine("check for the failed game", () => manager.OwnsCurrentGame && Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState)) return;
                Engine("end the failed game", () => { MBGameManager.EndGame(); return true; });
                var waited = Stopwatch.StartNew();
                while (!Engine("failed game ended", () => Game.Current == null && MBGameManager.Current == null) && waited.Elapsed.TotalSeconds < 120) Thread.Sleep(100);
                Report["returnedToMenu"] = Engine("at the menu", () => Game.Current == null);
            }
            catch (Exception error) { Report["returnError"] = error.Message; }
        }

        private void Enter(Phase next)
        {
            state = next;
            phases.Add(new Dictionary<string, object> { ["phase"] = next.ToString(), ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3) });
            trace("generated_load:" + next);
        }

        private void Phases(string label) { phases.Add(new Dictionary<string, object> { ["step"] = label, ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3) }); trace("generated_load_step:" + label); }

        private static Dictionary<string, int> Starts() { return GenerationProbe.ProcessStarts.ToDictionary(kv => kv.Key, kv => kv.Value); }

        // A progress poll the engine does not answer: LoadPolicy.Stall decides between a busy engine (the first load of a world in a module
        // build generates its navigation caches on the engine thread for minutes) and a stopped one (an exception escaped one of its
        // callbacks), from the engine thread's processor time per window. A stopped engine ends the load as failed with its report and trace
        // written, though nothing can return it to the menu.
        private string EngineProgress(GeneratedLoadManager manager)
        {
            var posted = EngineWork.Run(() => GeneratedLoadManager.Progress(manager));
            var stall = new LoadPolicy.Stall();
            var waited = Stopwatch.StartNew();
            while (!posted.Wait(TimeSpan.FromSeconds(LoadPolicy.Stall.WindowSeconds)))
                if (stall.Sample(waited.Elapsed.TotalSeconds, EngineCpuSeconds()))
                    throw new EngineStopped("The engine stopped during the load: " + stall.Reason + " (see the load's trace).");
            try { return posted.Result; }
            catch (AggregateException error) when (error.InnerException != null) { throw error.InnerException; }
        }

        // The engine thread's processor time (EngineThread.NativeId); the whole process's when that thread cannot be read.
        private static double EngineCpuSeconds()
        {
            var process = Process.GetCurrentProcess();
            int native = EngineThread.NativeId;
            if (native != 0)
                try { foreach (ProcessThread thread in process.Threads) if (thread.Id == native) return thread.TotalProcessorTime.TotalSeconds; }
                catch (Exception) { }
            return process.TotalProcessorTime.TotalSeconds;
        }

        private sealed class EngineStopped : Exception { public EngineStopped(string message) : base(message) { } }

        private static T Engine<T>(string label, Func<T> work)
        {
            if (EngineThread.IsCurrent) return work();
            var posted = EngineWork.Run(work);
            try { return posted.Result; }
            catch (AggregateException error) when (error.InnerException != null) { throw error.InnerException; }
        }

        private void WriteReport()
        {
            try
            {
                string directory = Path.Combine(SubModule.Store, "loads");
                Directory.CreateDirectory(directory);
                string name = "load-" + Process.GetCurrentProcess().Id + "-" + Interlocked.Increment(ref sequence) + ".json";
                Report["reportFile"] = name;
                File.WriteAllText(Path.Combine(directory, name), Json.Serialize(Report));
                foreach (var old in new DirectoryInfo(directory).GetFiles("load-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(ReportsKept)) old.Delete();
            }
            catch (Exception error) { Report["reportError"] = error.Message; }
        }
    }
}
