using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace MNBL.ProceduralWorld.Generation
{
    // What the activation hands the game starter (the new game's world, scene, periodic frame, climate and creation cultures).
    internal sealed class ActivationStart
    {
        public IGeneratedWorld World;
        public string SceneName;
        public Dictionary<string, object> SupportReport;
        public Action<string> Trace;
        public PeriodicWorld Periodic;
        public GeneratedClimate Climate;
        public IList<string> CreationCultures;
    }

    // MMR-45: the generation job's commit (ICommitStage; GenerationCoordinator.Commit runs it from ReadyForReview on the job's worker). It runs
    // WorldActivationTransaction with the game host below; the journal, the rollback and the receipt are the transaction's.
    // The new game is started through Start and followed through Progress (both on the engine thread): the probe's starter drives character
    // creation (DiagnosticSandboxBootstrap); the player-facing starter is MMR-46's Start Campaign action.
    internal sealed class ActivationCommitStage : ICommitStage
    {
        public ActivationBoundary? Fault;                 // a failure injected before that boundary's work (probes)
        public ActivationBoundary? StopInside;            // probes: the process is terminated after that boundary's work, before it is journaled
        public Action OnStop;                             // probes: records the run, then terminates the process (engine thread)
        public Action<CompiledWorldRegistration> Configure { get; set; }   // probes: sees the registration before the new game starts (e.g. a registration fault)
        public Func<ActivationStart, GeneratedGameManager> Start;
        public Func<GeneratedGameManager, string> Progress;   // null while starting, "map" once the campaign map runs, else why it failed
        public double CampaignTimeoutSeconds = 3600;
        public Action<string> Trace = _ => { };

        private GeneratedGameManager manager;
        public GeneratedGameManager Manager { get { return Volatile.Read(ref manager); } internal set { Volatile.Write(ref manager, value); } }
        public string Instance { get; private set; }
        public ActivationJournal Journal { get; private set; }
        public Exception Error { get; private set; }
        public GameActivationHost Host { get; private set; }

        public void Commit(GenerationJob job)
        {
            if (Start == null || Progress == null) throw new InvalidOperationException("The activation needs a game starter.");
            SaveBindingRuntime.RequireWriter();   // before the journal is begun
            // The campaign instance the setup chose (the generation request carries it), as the save binding's 32 hex digits.
            Guid chosen;
            string fromChoice = job.Choice != null && Guid.TryParse(job.Choice.CampaignInstanceId, out chosen) ? chosen.ToString("N") : null;
            // A start after a withdrawn one (MMR-46) is a new attempt: the first attempt's journal keeps the request's instance.
            if (fromChoice != null && ActivationRequest.ValidInstance(fromChoice) && new ActivationJournalStore(SubModule.Store).Journal(fromChoice) != null) fromChoice = null;
            Instance = fromChoice != null && ActivationRequest.ValidInstance(fromChoice) ? fromChoice : WorldBinding.NewInstance();
            var request = new ActivationRequest { Instance = Instance, WorldContentId = job.Get<string>("worldContentId"), ManifestSha256 = job.Get<string>("manifestSha256") };
            Host = new GameActivationHost(this, job);
            Host.Record["requestCampaignInstanceId"] = job.Choice == null ? null : job.Choice.CampaignInstanceId;
            Host.Record["instanceFromRequest"] = Instance == fromChoice;
            try { Journal = new WorldActivationTransaction(Host, new ActivationJournalStore(SubModule.Store)).Run(request, Fault); }
            catch (ActivationException failed) when (failed.InnerException is CommitWithdrawn && !failed.Recoverable && failed.Rollback.All(r => Equals(r["undone"], true)))
            {
                // MMR-46: the player left before the campaign started and everything was undone: the job goes back to review.
                Trace("activation_withdrawn:" + Instance);
                throw new CommitWithdrawn(failed.InnerException.Message, failed);
            }
            Host.ReleaseSaveGuard();
            Trace("activation_committed:" + Instance);
        }

        public void Recover(GenerationJob job, Exception error)
        {
            Error = error;
            var activation = error as ActivationException;
            // A failure after the initial save leaves the campaign running (MMR-45): its saves are not blocked for the rest of the session.
            if (activation != null && activation.Recoverable && Host != null)
                try { Host.ReleaseSaveGuard(); } catch (Exception release) { lock (job.Cleanup) job.Cleanup.Add("save guard release failed: " + release.Message); }
            lock (job.Cleanup) job.Cleanup.Add("activation " + (Instance ?? "(none)") + (activation == null ? " failed before the transaction: " + error.Message
                : (activation.Recoverable ? " left for recovery at " : " rolled back at ") + activation.Boundary + ": " + error.Message));
        }
    }

    // The campaign's save manager while an activation runs: autosave off, the sandbox's own manager told of every save that completes.
    internal sealed class ActivationSaveGuard : ISaveManager
    {
        public readonly ISaveManager Previous;
        public ActivationSaveGuard(ISaveManager previous) { Previous = previous; }
        public int GetAutoSaveInterval() { return -1; }
        public bool IsAutoSaveDisabled() { return true; }
        public void OnSaveOver(bool success, string saveName) { if (Previous != null) Previous.OnSaveOver(success, saveName); }
    }

    // MMR-46: no save of a campaign whose activation has not committed, whatever asks for it: SaveHandler's manual, quick and auto save entry
    // points refuse every save but the activation's reserved initial save (MMR-62's publisher) from the moment the campaign map runs until the
    // commit, a rollback or the game's end. The save manager guard turns autosave off; this covers the escape menu and quick save.
    internal static class ActivationSaveBlock
    {
        public const string HarmonyId = "mnbl.proceduralworld.activation-saves";
        private static Harmony harmony;
        private static string allowed;
        public static int Refused { get; private set; }
        public static bool Active { get { return harmony != null; } }

        public static void Begin(string reservedSaveName)
        {
            allowed = reservedSaveName;
            if (harmony != null) return;
            harmony = new Harmony(HarmonyId);
            var handler = typeof(SaveHandler);
            harmony.Patch(handler.GetMethod("SaveAs"), prefix: new HarmonyMethod(typeof(ActivationSaveBlock), nameof(SaveAsPrefix)));
            foreach (var name in new[] { "QuickSaveCurrentGame", "SignalAutoSave", "ForceAutoSave" })
                harmony.Patch(handler.GetMethod(name), prefix: new HarmonyMethod(typeof(ActivationSaveBlock), nameof(RefusePrefix)));
        }

        public static void End()
        {
            if (harmony == null) return;
            harmony.UnpatchAll(HarmonyId);
            harmony = null; allowed = null;
        }

        public static bool SaveAsPrefix(string saveName)
        {
            if (saveName != null && saveName == allowed) return true;
            Refuse();
            return false;
        }
        public static bool RefusePrefix() { Refuse(); return false; }
        private static void Refuse()
        {
            Refused++;
            try { TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(new TaleWorlds.Localization.TextObject("{=mnbl_activation_no_save}The campaign can be saved once it has started.").ToString())); }
            catch (Exception) { }
        }
    }

    // The production boundaries (docs/world-activation.md). Every native call goes through job.Engine (the engine thread); file and managed
    // work runs on the job's worker. Evidence the receipt binds: W02 the navigation report (VerifyRuntime), C07 the graph report (BuildWorld),
    // W03 the climate and water attached to the live map, W04 the settlement layout and the live settlements on it, W05 the playability
    // reports with the live campaign's route set and navigation caches (PostBuild).
    internal sealed class GameActivationHost : IActivationHost
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private readonly ActivationCommitStage stage;
        private readonly GenerationJob job;
        private PreparedSurface prepared;
        private WorldDataBundle bundle;
        private IDictionary<string, object> layout;
        private string layoutReportSha, navigationReportSha, playabilityReportSha;
        private PeriodicWorldCoordinates coordinates;
        private GeneratedClimateSystem.Result synthesis;
        private GeneratedClimate climate;
        private GeneratedWorldPackage package;
        private bool packageCreated, bundleDisowned;
        private Dictionary<string, object> supportReport;
        private Dictionary<string, string> identity;
        public CompiledWorldDataset Dataset { get; private set; }
        public CompiledWorldRegistration Registration { get; private set; }
        public Dictionary<string, object> Record { get; private set; }

        public GameActivationHost(ActivationCommitStage stage, GenerationJob job)
        {
            this.stage = stage; this.job = job;
            Record = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private void Note(string text) { stage.Trace(text); lock (Record) { object o; if (!Record.TryGetValue("trace", out o)) Record["trace"] = o = new List<string>(); ((List<string>)o).Add(text); } }

        public ActivationStep Run(ActivationBoundary boundary, ActivationRequest request)
        {
            var step = new ActivationStep();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Note("activation_boundary:" + boundary);
            try
            {
                switch (boundary)
                {
                    case ActivationBoundary.Admit: Admit(step); break;
                    case ActivationBoundary.VerifyData: VerifyData(step, request); break;
                    case ActivationBoundary.VerifyRuntime: VerifyRuntime(step, request); break;
                    case ActivationBoundary.BuildWorld: BuildWorld(step); break;
                    case ActivationBoundary.PromotePackage: PromotePackage(step, request); break;
                    case ActivationBoundary.StartCampaign: StartCampaign(step, request); break;
                    case ActivationBoundary.PostBuild: PostBuild(step); break;
                    case ActivationBoundary.BindIdentity: BindIdentity(step, request); break;
                    case ActivationBoundary.PublishSave: PublishSave(step, request); break;
                    default: throw new InvalidOperationException("The host does not run " + boundary);
                }
            }
            catch (Exception error)
            {
                lock (Record) Record[boundary + "Failure"] = error.ToString();   // the diagnostic record (the journal keeps the type and message)
                throw;
            }
            step.Detail["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3);
            lock (Record) Record[boundary.ToString()] = step.Detail;
            if (stage.StopInside == boundary && stage.OnStop != null)
            {
                Note("activation_stop_inside:" + boundary);
                job.Engine("stop the process inside " + boundary, () => { stage.OnStop(); return true; });
                throw new InvalidOperationException("The process was to stop inside " + boundary + ".");
            }
            return step;
        }

        // ---- Admit: the store's writer, a job awaiting this commit, the main menu ----
        private void Admit(ActivationStep step)
        {
            SaveBindingRuntime.RequireWriter();
            if (job.Phase != JobPhase.Committing) throw new InvalidOperationException("The job is " + job.Phase + ", not committing.");
            if (!job.Engine("activation: at the main menu", () => Game.Current == null && TaleWorlds.MountAndBlade.MBGameManager.Current == null))
                throw new InvalidOperationException("A world activates from the main menu; a game is running.");
            prepared = job.Get<PreparedSurface>("prepared");
            if (prepared == null || prepared.SceneName != job.Get<string>("sceneName")) throw new InvalidOperationException("The job holds no prepared scene.");
            if (!Equals(job.Artifacts.ContainsKey("navigationActivatable") ? job.Artifacts["navigationActivatable"] : null, true) ||
                !Equals(job.Artifacts.ContainsKey("playabilityActivatable") ? job.Artifacts["playabilityActivatable"] : null, true))
                throw new InvalidOperationException("The job's runtime validation did not pass.");
            step.Note = "job " + job.Id + ", scene " + prepared.SceneName;
            step.Detail["jobId"] = job.Id; step.Detail["sceneName"] = prepared.SceneName; step.Detail["attemptSeed"] = job.Get<uint>("attemptSeed");
            step.Detail["module"] = new Dictionary<string, object> { ["assemblySha256"] = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(typeof(SubModule).Assembly.Location) };
        }

        // ---- VerifyData: the DATA_READY bundle, before any native resource of the world opens ----
        private void VerifyData(ActivationStep step, ActivationRequest request)
        {
            var hostProfile = MNBL.ProceduralWorld.UI.WorldSetupHost.BundleHost();
            bundle = WorldDataBundle.Open(job.Get<string>("bundleDirectory"), hostProfile);   // DATA_READY, the manifest, every file hash, compatibility
            if (bundle.WorldContentId != request.WorldContentId || bundle.ManifestSha256 != request.ManifestSha256)
                throw new InvalidDataException("The bundle is not the world the job validated (" + bundle.WorldContentId + ").");
            var inputs = job.Get<Dictionary<string, string>>("inputs") ?? new Dictionary<string, string>();
            Func<string, string> file = path => path != null && File.Exists(path) ? MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(path) : null;
            step.Note = "bundle " + bundle.WorldContentId.Substring(0, 12);
            step.Detail["worldContentId"] = bundle.WorldContentId; step.Detail["manifestSha256"] = bundle.ManifestSha256;
            step.Detail["generator"] = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["requestSha256"] = file(job.Get<string>("request")), ["attemptRequestSha256"] = file(job.Get<string>("attemptRequest")), ["requestId"] = job.Get<string>("requestId"),
                ["sourceWorldContentId"] = job.Get<string>("sourceWorldContentId"), ["attempts"] = job.Artifacts.ContainsKey("attempts") ? job.Artifacts["attempts"] : null,
                ["seedSource"] = job.Get<string>("requestSeedSource"),
                ["inputs"] = new SortedDictionary<string, object>(inputs.ToDictionary(kv => kv.Key, kv => (object)file(kv.Value)), StringComparer.Ordinal) };
            step.Detail["host"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["game"] = hostProfile.GameVersion, ["navalDlc"] = hostProfile.NavalVersion, ["capabilities"] = hostProfile.Capabilities.ToList(),
                // The installed engine, campaign, sandbox and War Sails binaries this process runs (by file name, never by path).
                ["binaries"] = new SortedDictionary<string, object>(new[] { typeof(TaleWorlds.Engine.Scene), typeof(Game), typeof(Campaign), typeof(TaleWorlds.MountAndBlade.Module), typeof(SandBox.SandBoxSubModule), typeof(NavalDLC.NavalDLCSubModule) }
                    .Select(t => t.Assembly.Location).Distinct().ToDictionary(path => Path.GetFileName(path), path => (object)MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(path)), StringComparer.Ordinal) };
        }

        // ---- VerifyRuntime: the derived runtime reports belong to this world and scene and still pass ----
        private void VerifyRuntime(ActivationStep step, ActivationRequest request)
        {
            string scene = prepared.SceneName, folder = RuntimeWorldBackend.SceneFolder(scene);
            var sceneFiles = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" })
            {
                string path = Path.Combine(folder, name);
                if (!File.Exists(path)) throw new FileNotFoundException("The installed world scene lacks " + name, path);
                sceneFiles[name] = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(path);
            }
            foreach (var kv in prepared.Files)
            {
                string path = Path.Combine(folder, kv.Key);
                if (File.Exists(path) && MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(path) != kv.Value) throw new InvalidDataException("The installed scene file " + kv.Key + " changed since the job installed it.");
            }
            var navigation = NavigationGate.Require(SubModule.Store, scene, Path.Combine(folder, "navmesh.bin"), NavigationPolicy.Installed().Sha256);   // W02
            if ((string)navigation["worldContentId"] != request.WorldContentId) throw new InvalidDataException("The navigation report names world " + navigation["worldContentId"] + ".");
            string navigationPath = NavigationGate.ReportPath(SubModule.Store, scene), settlementPath = RuntimeWorldBackend.SettlementReportPath(scene);
            RuntimePlayabilityValidator.Require(SubModule.Store, scene, navigationPath, settlementPath);   // MMR-41's report, made against this navigation and layout
            layout = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(settlementPath));
            if ((string)layout["worldContentId"] != request.WorldContentId || (string)layout["layoutSha256"] != prepared.Settlements.Sha256 || !Equals(layout["activatable"], true))
                throw new InvalidDataException("The settlement layout report is not this world's activatable layout.");
            if (!Equals(job.Artifacts.ContainsKey("settlementEntitiesPassed") ? job.Artifacts["settlementEntitiesPassed"] : null, true))
                throw new InvalidDataException("The scene's settlement entities did not pass the job's check.");
            // The persisted reports are the ones this job made (not a later job's for the same scene).
            string playabilityPath = RuntimePlayabilityValidator.ReportPath(SubModule.Store, scene);
            Func<string, string> recorded = path => (string)((IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(path)))["reportSha256"];
            if (recorded(navigationPath) != job.Get<string>("navigationReportSha256")) throw new InvalidDataException("The persisted navigation report is not the one this job made.");
            if (recorded(playabilityPath) != job.Get<string>("playabilityReportSha256")) throw new InvalidDataException("The persisted playability report is not the one this job made.");
            navigationReportSha = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(navigationPath);
            layoutReportSha = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(settlementPath);
            playabilityReportSha = MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(playabilityPath);
            // The evidence the receipt names, kept with the activation: a later job for the same scene may sweep or rewrite the scene's reports.
            string evidence = Path.Combine(new ActivationJournalStore(SubModule.Store).InstanceDirectory(request.Instance), "evidence");
            Directory.CreateDirectory(evidence);
            foreach (var kv in new[] { new { Name = "navigation.json", Path = navigationPath }, new { Name = "playability.json", Path = playabilityPath }, new { Name = "settlement-layout.json", Path = settlementPath } })
                DurableFile.WriteAllBytes(Path.Combine(evidence, kv.Name), File.ReadAllBytes(kv.Path));
            coordinates = PeriodicWorldCoordinates.FromSurface(prepared.Surface);
            step.Evidence["W02"] = navigationReportSha;
            step.Note = "navigation, playability and layout reports of " + scene;
            step.Detail["sceneFiles"] = sceneFiles; step.Detail["navigationReportSha256"] = job.Get<string>("navigationReportSha256");
            step.Detail["navigationRecordSha256"] = navigationReportSha; step.Detail["playabilityRecordSha256"] = playabilityReportSha;
            step.Detail["layoutSha256"] = layout["layoutSha256"]; step.Detail["layoutReportSha256"] = layoutReportSha;
            step.Detail["backend"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["navmeshSha256"] = job.Get<string>("navmeshSha256"), ["navigationPolicySha256"] = NavigationPolicy.Installed().Sha256,
                ["coordinatePolicy"] = coordinates.Identity, ["coordinateFrame"] = coordinates.FrameIdentity,
                // The encoder by its hash and outcome only: its arguments and paths name the user's profile.
                ["encoder"] = prepared.Encoder == null ? null : prepared.Encoder.Where(kv => kv.Key == "role" || kv.Key == "executableHash" || kv.Key == "exitCode").ToDictionary(kv => kv.Key, kv => kv.Value) };
        }

        // ---- BuildWorld: the campaign graph from the bundle, against the pinned installed catalog, validated whole ----
        // MMR-49: WorldReconstruction, shared with the load (GeneratedWorldLoader), so a world is rebuilt the same way it was activated.
        private void BuildWorld(ActivationStep step)
        {
            byte[] pinBytes;
            var catalog = WorldReconstruction.Catalog((label, work) => job.Engine(label, work), out pinBytes);
            var world = WorldReconstruction.Build(bundle, layout, coordinates, prepared.Surface, catalog, pinBytes);
            Dataset = world.Dataset; synthesis = world.Synthesis; climate = world.Climate; terrainContext = world.TerrainContext;
            step.Evidence["C07"] = Dataset.Graph.Sha256;
            step.Note = Dataset.WorldId + ": " + Dataset.Declared.Count + " declared objects, graph " + Dataset.Graph.Sha256.Substring(0, 12);
            step.Detail["dataset"] = Dataset.Record(); step.Detail["catalogPinSha256"] = world.CatalogSha256; step.Detail["catalogDocuments"] = world.CatalogDocuments;
            step.Detail["climate"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["identity"] = synthesis.Identity, ["windSha256"] = synthesis.WindSha256,
                ["precipitationSha256"] = synthesis.PrecipitationSha256, ["colorGradeSha256"] = synthesis.ColorGradeSha256 };
            step.Detail["policies"] = Dataset.Graph.PolicyIdentity;
        }
        private Func<double, double, GeneratedBattleTerrain.TerrainContext> terrainContext;
        private const double PublishLimitSeconds = 600;
        private ActivationSaveGuard saveGuard;

        // After the commit: the save manager the campaign had before the guard (the starter's own) is back.
        public void ReleaseSaveGuard()
        {
            if (saveGuard == null) { job.Engine("end the save block", () => { ActivationSaveBlock.End(); return true; }); return; }
            job.Engine("release the save guard", () =>
            {
                var c = Campaign.Current;
                if (c != null && c.SandBoxManager.SandBoxSaveManager == saveGuard) c.SandBoxManager.SandBoxSaveManager = saveGuard.Previous;
                ActivationSaveBlock.End();
                return true;
            });
            saveGuard = null;
        }

        // A repository policy path as the module carries it (worldgen/policies/x.json -> x.json; culture templates and prototypes renamed).
        internal static string PolicyResource(string path)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (path.StartsWith("worldgen/templates/cultures/", StringComparison.Ordinal)) return "culture-" + name;
            if (path == "worldgen/templates/settlements/prototypes.json") return "settlement-prototypes.json";
            return name;
        }
        internal static byte[] PolicyBytes(string path)
        {
            var bytes = RuntimePlayabilityValidator.Embedded(PolicyResource(path));
            if (bytes == null) throw new InvalidDataException("The module does not carry the policy " + path + ".");
            return bytes;
        }

        // ---- PromotePackage: the world package, moved into place whole (manifest last) ----
        private void PromotePackage(ActivationStep step, ActivationRequest request)
        {
            string scene = prepared.SceneName, folder = RuntimeWorldBackend.SceneFolder(scene);
            var placement = new Dictionary<string, object> {
                ["schema"] = "mnbl.compiled-world-placement.v1", ["worldId"] = Dataset.WorldId, ["sceneName"] = scene, ["extent"] = prepared.Surface.Extent,
                ["sceneFiles"] = new[] { "scene.xscene", "terrain.bin", "navmesh.bin", "flora.bin", "atmosphere.xml" }.ToDictionary(f => f, f => (object)MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(Path.Combine(folder, f))),
                ["periodic"] = coordinates.ToPlacement(), ["worldContentId"] = request.WorldContentId, ["bundleManifestSha256"] = request.ManifestSha256,
                ["layoutSha256"] = layout["layoutSha256"], ["climateIdentity"] = synthesis.Identity, ["graphSha256"] = Dataset.Graph.Sha256 };
            string placementPath = job.File("compiled-placement.json");
            File.WriteAllText(placementPath, Json.Serialize(placement), new UTF8Encoding(false));
            var check = PeriodicWorldCoordinates.FromPlacement((IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(placementPath)));
            if (check == null || check.FrameIdentity != coordinates.FrameIdentity) throw new InvalidDataException("The package's periodic frame does not read back as the scene's.");
            var before = new HashSet<string>(Directory.Exists(GeneratedWorldPackage.Root) ? Directory.GetDirectories(GeneratedWorldPackage.Root) : new string[0], StringComparer.OrdinalIgnoreCase);
            var documents = Dataset.Documents.Select(d => new GeneratedCampaignDataset.Document(d.Key, d.Value)).ToList();
            var fingerprint = new Dictionary<string, object> { ["builder"] = "CompiledWorldDataset.Build (MMR-45 activation)", ["graphSha256"] = Dataset.Graph.Sha256,
                ["defaultReferencesSha256"] = CampaignGraphValidator.InventorySha256(Dataset.Defaults), ["layoutSha256"] = layout["layoutSha256"], ["declared"] = Dataset.Declared.Count,
                ["suppliesStartup"] = true, ["navalLandlocked"] = Dataset.NavalPlan.Landlocked.Cast<object>().ToList(), ["creationCultures"] = Dataset.CreationCultures.Cast<object>().ToList(),
                ["worldContentId"] = request.WorldContentId };
            package = GeneratedWorldPackage.Write(Dataset.WorldId, scene, job.Get<uint>("attemptSeed"), placementPath, documents,
                MNBL.ProceduralWorld.Diagnostics.GenerationProbe.Hash(typeof(SubModule).Assembly.Location), fingerprint);
            packageCreated = !before.Contains(package.Directory);
            // The save binding names the bundle (its contentId): it stays published with the world.
            bundleDisowned = job.Disown("bundle " + request.WorldContentId);
            step.Note = "package " + Path.GetFileName(package.Directory) + (packageCreated ? " promoted" : " reused (identical content)");
            step.Detail["package"] = Path.GetFileName(package.Directory); step.Detail["packageManifestSha256"] = package.ManifestSha256; step.Detail["packageCreated"] = packageCreated;
            step.Detail["bundleKept"] = bundleDisowned ? "taken over from the job" : "already published";
        }

        // ---- StartCampaign: the new game on the world's scene; registration inside RegisterSubModuleObjects (R07) ----
        private void StartCampaign(ActivationStep step, ActivationRequest request)
        {
            string scene = prepared.SceneName;
            Registration = new CompiledWorldRegistration(Dataset, scene, request.CampaignInstanceId);
            if (stage.Configure != null) stage.Configure(Registration);
            supportReport = new Dictionary<string, object>();
            var periodic = new PeriodicWorld(coordinates);
            var start = new ActivationStart { World = Registration, SceneName = scene, SupportReport = supportReport, Trace = stage.Trace, Periodic = periodic, Climate = climate, CreationCultures = Dataset.CreationCultures };
            var started = DateTime.UtcNow;
            stage.Manager = job.Engine("start the new game", () =>
            {
                GeneratedBattleTerrain.ContextSources[scene] = terrainContext;
                return stage.Start(start);
            });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string progress;
            while ((progress = job.Engine("new game progress", () => stage.Progress(stage.Manager))) == null)
            {
                if (watch.Elapsed.TotalSeconds > stage.CampaignTimeoutSeconds) throw new TimeoutException("The new game did not reach the campaign map within " + stage.CampaignTimeoutSeconds + " s.");
                Thread.Sleep(250);
            }
            if (progress == "withdrawn") throw new CommitWithdrawn("The player left before the campaign started.");
            if (progress != "map") throw new InvalidOperationException("The new game failed: " + progress);
            // No save of the campaign until the activation commits: the host's guard replaces the save manager (autosave off) until then.
            job.Engine("install the save guard", () =>
            {
                var c = Campaign.Current;
                saveGuard = new ActivationSaveGuard(c.SandBoxManager.SandBoxSaveManager); c.SandBoxManager.SandBoxSaveManager = saveGuard;
                // Manual and quick saves too: only the activation's reserved initial save may be written before the commit (MMR-46).
                ActivationSaveBlock.Begin(CampaignOptions.IsIronmanMode ? c.SaveHandler.IronmanModSaveName : EarlyWorldBindingStore.InitialSaveName(new WorldBinding { WorldId = Dataset.WorldId, Instance = request.Instance }));
                return true;
            });
            step.Note = "campaign map of " + scene + " after " + Math.Round(watch.Elapsed.TotalSeconds, 1) + " s";
            step.Detail["campaignSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2); step.Detail["registration"] = Registration.Report;
            step.Detail["navigationCaches"] = CacheRecord(supportReport);
        }

        // The navigation caches as the map support recorded them, without paths: status, key, per-type source and timings.
        private static Dictionary<string, object> CacheRecord(Dictionary<string, object> support)
        {
            var caches = support != null && support.ContainsKey("generatedNavigationCaches") ? support["generatedNavigationCaches"] as Dictionary<string, object> : null;
            if (caches == null) return null;
            var cache = caches.ContainsKey("navigationCache") ? caches["navigationCache"] as Dictionary<string, object> : null;
            Func<Dictionary<string, object>, string, object> get = (d, k) => d != null && d.ContainsKey(k) ? d[k] : null;
            return new Dictionary<string, object> { ["status"] = get(caches, "status"), ["elapsedMilliseconds"] = get(caches, "elapsedMilliseconds"),
                ["cacheStatus"] = get(cache, "status"), ["keySha256"] = get(cache, "keySha256"), ["caches"] = get(caches, "caches") };
        }

        // ---- PostBuild: the live campaign against everything it was built from ----
        private void PostBuild(ActivationStep step)
        {
            var problems = new List<string>();
            var live = job.Engine("post-build checks", () => LiveChecks(problems));
            if (problems.Count > 0) throw new InvalidDataException("The live campaign failed " + problems.Count + " post-build checks: " + String.Join("; ", problems.Take(8)));
            Func<object, string> sha = o => CompiledWorldDataset.Sha256(new UTF8Encoding(false).GetBytes(Json.Serialize(o)));
            step.Evidence["W03"] = sha(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["climateIdentity"] = synthesis.Identity, ["weather"] = live["weather"], ["sceneName"] = prepared.SceneName,
                ["water"] = live["water"] });
            step.Evidence["W04"] = sha(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["layoutReportSha256"] = layoutReportSha, ["settlements"] = live["settlements"], ["missingBakedEntities"] = live["missingBakedEntities"] });
            step.Evidence["W05"] = sha(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["playabilityRecordSha256"] = playabilityReportSha, ["campaignRoutes"] = live["playabilitySha256"],
                ["navigationCacheKey"] = live["navigationCacheKey"], ["battleMap"] = live["battleMap"], ["missionScenes"] = live["missionScenes"] });
            step.Note = "live campaign checks passed";
            foreach (var kv in live) step.Detail[kv.Key] = kv.Value;
        }

        private SortedDictionary<string, object> LiveChecks(List<string> problems)
        {
            var live = new SortedDictionary<string, object>(StringComparer.Ordinal);
            string worldId = Dataset.WorldId;
            // Nothing saves the campaign before the commit (the host's guard is the save manager; Ironman is recorded).
            var manager = Campaign.Current.SandBoxManager.SandBoxSaveManager;
            if (!(manager is ActivationSaveGuard) || !manager.IsAutoSaveDisabled() || !ActivationSaveBlock.Active) problems.Add("the campaign's saves are not held until the commit");
            live["saveGuard"] = manager == null ? null : manager.GetType().Name; live["ironman"] = CampaignOptions.IsIronmanMode;
            // W03: the scene's water on the live map (the sea plane and every lake plane the encoding inserted).
            var mapScene = ((GeneratedMapScene)Campaign.Current.MapSceneWrapper).Scene;
            string seaName = prepared.SceneName + "_water";
            int lakesFound = prepared.Lakes == null ? 0 : prepared.Lakes.Count(l => mapScene.GetFirstEntityWithName(GeneratedWaterSystem.LakeName(seaName, l)) != null);
            bool sea = mapScene.GetFirstEntityWithName(seaName) != null;
            if (!sea || lakesFound != (prepared.Lakes == null ? 0 : prepared.Lakes.Count)) problems.Add("the live map's water is not the scene's (sea plane " + sea + ", lakes " + lakesFound + " of " + (prepared.Lakes == null ? 0 : prepared.Lakes.Count) + ")");
            live["water"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["seaPlane"] = sea, ["lakes"] = prepared.Lakes == null ? 0 : prepared.Lakes.Count, ["lakesFound"] = lakesFound };
            // Closure: everything the world registered is ready where the settled campaign keeps it.
            var closure = Registration.ClosureProblems(ClosureMode.Settled);
            problems.AddRange(closure.Take(5).Select(c => "closure: " + c));
            live["closureProblems"] = closure.Count;
            // W04: every settlement where the layout (and the scene's entity) stands; no baked entity made at run time.
            int checkedPositions = 0; var off = new List<string>();
            foreach (var row in ((IEnumerable)layout["settlements"]).Cast<IDictionary<string, object>>())
            {
                var s = Settlement.Find((string)row["id"]);
                if (s == null) { off.Add(row["id"] + " missing"); continue; }
                Action<string, TaleWorlds.Library.Vec2> at = (key, p) =>
                {
                    object v; if (!row.TryGetValue(key, out v) || v == null) return;
                    var e = ((IEnumerable)v).Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                    checkedPositions++;
                    if (Math.Abs(p.X - e[0]) > 1e-2 || Math.Abs(p.Y - e[1]) > 1e-2) off.Add(row["id"] + " " + key + " " + p.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.###", CultureInfo.InvariantCulture));
                };
                at("centre", s.Position.ToVec2());
                if (s.IsFortification) at("gate", s.GatePosition.ToVec2());
                if (s.IsTown && s.HasPort) at("port", s.PortPosition.ToVec2());
            }
            problems.AddRange(off.Take(5).Select(o => "settlement off its placement: " + o));
            live["settlements"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["positionsChecked"] = checkedPositions, ["off"] = off.Count, ["layoutSha256"] = layout["layoutSha256"] };
            var missingBaked = GeneratedMapScene.MissingBakedEntities.ToList();
            problems.AddRange(missingBaked.Take(5).Select(m => "baked settlement entity missing: " + m));
            live["missingBakedEntities"] = missingBaked.Count; live["runtimeSettlementEntities"] = GeneratedMapScene.RuntimeSettlementEntities.Count;
            // W05: the navigation caches under their key, the battle map, the mission scenes, and the route set through the live campaign.
            var caches = supportReport.ContainsKey("generatedNavigationCaches") ? supportReport["generatedNavigationCaches"] as Dictionary<string, object> : null;
            var cacheRecord = caches != null && caches.ContainsKey("navigationCache") ? caches["navigationCache"] as Dictionary<string, object> : null;
            if (caches == null || (string)caches["status"] != "registered" || cacheRecord == null) problems.Add("the navigation caches are not registered");
            live["navigationCacheKey"] = cacheRecord == null ? null : cacheRecord.ContainsKey("keySha256") ? cacheRecord["keySha256"] : null;
            live["navigationCacheStatus"] = cacheRecord == null ? null : cacheRecord.ContainsKey("status") ? cacheRecord["status"] : null;
            var battle = supportReport.ContainsKey("battleSceneSelection") ? supportReport["battleSceneSelection"] as Dictionary<string, object> : null;
            if (battle == null || (string)battle["status"] != "installed" || !Equals(battle["navigationSetsMatchCatalogue"], true)) problems.Add("the battle map is not installed against the catalogue");
            if (battle != null && !Convert.ToString(battle["terrainContextSource"], CultureInfo.InvariantCulture).Contains("masks")) problems.Add("the battle map has no terrain context from the world's masks");
            Func<string, object> b = key => battle != null && battle.ContainsKey(key) ? battle[key] : null;
            // GeneratedEncounterResolver's record counts the cells where every land party finds one scene (landDeterministic of landCells) and
            // the sea terrain checks that find one naval scene (seaDeterministic of seaTerrainChecks).
            Func<string, long> n = key => b(key) == null ? -1 : Convert.ToInt64(b(key), CultureInfo.InvariantCulture);
            if (battle != null && (n("landDeterministic") != n("landCells") || n("seaDeterministic") != n("seaTerrainChecks") || n("landCells") <= 0))
                problems.Add("the battle map does not give every party one scene (land " + n("landDeterministic") + " of " + n("landCells") + ", sea " + n("seaDeterministic") + " of " + n("seaTerrainChecks") + ")");
            live["battleMap"] = battle == null ? null : new SortedDictionary<string, object>(StringComparer.Ordinal) { ["status"] = battle["status"], ["terrainContextSource"] = battle["terrainContextSource"],
                ["sceneTableSha256"] = b("sceneTableSha256"), ["inventorySha256"] = b("inventorySha256"), ["catalogueSha256"] = b("catalogueSha256"), ["mapSha256"] = b("sha256"),
                ["landCells"] = b("landCells"), ["landDeterministic"] = b("landDeterministic"), ["seaTerrainChecks"] = b("seaTerrainChecks"), ["seaDeterministic"] = b("seaDeterministic") };
            var weather = supportReport.ContainsKey("generatedWeather") ? supportReport["generatedWeather"] as Dictionary<string, object> : null;
            if (weather == null || (string)weather["status"] != "prepared" || (string)weather["windSha256"] != synthesis.WindSha256 || (string)weather["precipitationSha256"] != synthesis.PrecipitationSha256)
                problems.Add("the live map's weather is not the world's climate");
            live["weather"] = weather == null ? null : new SortedDictionary<string, object>(StringComparer.Ordinal) { ["status"] = weather["status"], ["windSha256"] = weather["windSha256"],
                ["precipitationSha256"] = weather["precipitationSha256"], ["colorGrade"] = weather["colorGrade"] };
            var catalogue = MNBL.ProceduralWorld.Missions.MissionScenes.Installed();
            var contractLocations = new HashSet<string>(new[] { "center", "lordshall", "port", "village_center", "hideout_center" }, StringComparer.Ordinal);
            int sceneSettlements = 0, sceneProblems = 0;
            foreach (var s in Settlement.All.Where(x => x.StringId.StartsWith(worldId + "_", StringComparison.Ordinal) && (x.IsTown || x.IsCastle || x.IsVillage || x.IsHideout)))
            {
                string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : "hideout";
                bool port = (s.IsTown || s.IsVillage) && s.HasPort;
                var locations = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (s.LocationComplex != null)
                    foreach (var l in s.LocationComplex.GetListOfLocations())
                    {
                        var names = new string[4];
                        for (int level = 0; level < 4; level++) { string scene = null; try { scene = l.GetSceneName(level); } catch (Exception) { } names[level] = String.IsNullOrEmpty(scene) ? null : scene; }
                        locations[l.StringId] = names;
                    }
                var contract = locations.Where(l => contractLocations.Contains(l.Key) && (l.Key != "port" || (kind == "town" && port))).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
                var failed = catalogue.LocationProblems(kind, port, contract);
                sceneSettlements++;
                if (failed.Count > 0) { sceneProblems++; if (sceneProblems <= 3) problems.Add("mission scenes: " + s.StringId + ": " + failed[0]); }
            }
            live["missionScenes"] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["settlements"] = sceneSettlements, ["failed"] = sceneProblems, ["catalogueSha256"] = catalogue.Sha256 };
            var playability = CampaignPlayability.Run(worldId, stage.Trace, Dataset.CreationCultures);
            if (!Equals(playability["activatable"], true)) problems.Add("playability: " + ((IList)playability["failures"]).Count + " semantic routes failed in the live campaign");
            live["playabilitySha256"] = playability.ContainsKey("reportSha256") ? playability["reportSha256"] : CompiledWorldDataset.Sha256(new UTF8Encoding(false).GetBytes(Json.Serialize(playability)));
            live["playabilityRoutes"] = playability.ContainsKey("routes") ? playability["routes"] : null;
            live["playabilityActivatable"] = playability["activatable"];
            return live;
        }

        // ---- BindIdentity: the save header's world binding with this campaign instance ----
        private void BindIdentity(ActivationStep step, ActivationRequest request)
        {
            identity = job.Engine("bind the world identity", () =>
            {
                var map = (TaleWorlds.CampaignSystem.Map.IMapScene)Campaign.Current.MapSceneWrapper;
                var entries = WorldIdentity.Build(Dataset.WorldId, prepared.SceneName, new PeriodicWorld(coordinates), climate, package, map, request.WorldContentId, request.Instance);
                var behavior = stage.Manager.Identity;
                if (behavior == null) throw new InvalidOperationException("The new game has no world identity behaviour.");
                foreach (var kv in entries) behavior.Entries[kv.Key] = kv.Value;
                return entries;
            });
            step.Note = "instance " + request.Instance + " bound to " + identity["package"];
            step.Detail["identity"] = identity.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }

        // ---- PublishSave: the initial save, read back bound to this instance and whole (MMR-62) ----
        private void PublishSave(ActivationStep step, ActivationRequest request)
        {
            var binding = new WorldBinding { WorldId = Dataset.WorldId, ManifestSha256 = package.ManifestSha256, Instance = request.Instance, ContentId = request.WorldContentId };
            var publisher = job.Engine("publish the initial save", () => InitialSavePublisher.Start(SaveBindingRuntime.Store, Campaign.Current, binding));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!job.Engine("initial save progress", () => { publisher.Tick(watch.Elapsed.TotalSeconds > 120); return publisher.Done; }))
            {
                // The engine keeps a queued save while it may still land (Tick waits on IsSaving); past the hard limit the outcome is left to
                // MMR-62's recovery.
                if (watch.Elapsed.TotalSeconds > PublishLimitSeconds) throw new UnresolvedPublication("The initial save did not finish within " + PublishLimitSeconds + " s.", null);
                Thread.Sleep(100);
            }
            step.Detail["saveName"] = publisher.SaveName; step.Detail["ironman"] = publisher.Ironman; step.Detail["saveSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
            if (publisher.Published)
            {
                step.Note = "initial save " + publisher.SaveName + " published";
                step.Detail["uniqueGameId"] = publisher.Record.UniqueGameId;
                return;
            }
            if (publisher.Record == null) throw new IOException("The initial save was not begun: " + publisher.Error);
            if (publisher.Record.State == EarlyWorldBindingStore.Intent) throw new UnresolvedPublication("The initial save's outcome is unresolved: " + publisher.Error, null);
            throw new IOException("The initial save was not published: " + publisher.Record.State + " (" + publisher.Record.LastNote + ")" + (publisher.Error == null ? "" : "; " + publisher.Error));
        }

        public void Undo(ActivationBoundary boundary, ActivationRequest request)
        {
            Note("activation_undo:" + boundary);
            switch (boundary)
            {
                case ActivationBoundary.PromotePackage:
                    // Only a package this activation created: nothing else can reference content first promoted here.
                    if (packageCreated && package != null && Directory.Exists(package.Directory)) Directory.Delete(package.Directory, true);
                    if (bundleDisowned)
                    {
                        string directory = job.Get<string>("bundleDirectory");
                        job.Own("bundle " + request.WorldContentId, () => { if (Directory.Exists(directory)) Directory.Delete(directory, true); });   // released with the failed job, as before
                        bundleDisowned = false;
                    }
                    break;
                case ActivationBoundary.StartCampaign:
                    // Also the failing boundary's own leftovers: a game that started (or failed to start), registered objects, the terrain context.
                    try { EndGame(); }
                    finally { job.Engine("remove the terrain context", () => { ActivationSaveBlock.End(); return GeneratedBattleTerrain.ContextSources.Remove(prepared.SceneName); }); saveGuard = null; }
                    break;
                case ActivationBoundary.BindIdentity:
                    if (identity != null) job.Engine("unbind the world identity", () => { if (stage.Manager != null && stage.Manager.Identity != null) stage.Manager.Identity.Entries.Clear(); return true; });
                    break;
            }
        }

        // Ends the new game through the installed menu-exit lifecycle and checks that none of the world's objects stays registered (else the
        // registry rolls them back).
        private void EndGame()
        {
            job.Engine("end the new game", () => { if (Game.Current != null) TaleWorlds.MountAndBlade.MBGameManager.EndGame(); return true; });
            // MBGameManager.EndGame waits until the game has loaded (installed IL: async, Task.Delay while !IsLoaded), so a game still loading
            // ends once it is loaded.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!job.Engine("game ended", () => Game.Current == null && TaleWorlds.MountAndBlade.MBGameManager.Current == null))
            {
                if (watch.Elapsed.TotalSeconds > 300) throw new TimeoutException("The new game did not end within 300 s.");
                Thread.Sleep(250);
            }
            if (Dataset == null) { stage.Manager = null; return; }
            var left = job.Engine("world objects left", () =>
            {
                if (MBObjectManager.Instance == null) return 0;
                var store = new MBObjectStore(StoreStage.NewGame);
                return CampaignObjectRegistry.CensusTypes.Sum(type => store.Objects(type).Count(o => o.Key.StartsWith(Dataset.WorldPrefix, StringComparison.Ordinal)));
            });
            lock (Record) Record["objectsLeftAfterEnd"] = left;
            if (left > 0)
            {
                var remaining = Registration == null ? new List<string> { left + " objects of the world and no registry to roll them back" } : job.Engine("roll back the world's objects", () => Registration.RollBack());
                lock (Record) Record["rollbackRemaining"] = remaining;
                if (remaining.Count > 0) throw new InvalidOperationException(remaining.Count + " objects of the world could not be removed after the game ended.");
            }
            stage.Manager = null;
        }
    }
}
