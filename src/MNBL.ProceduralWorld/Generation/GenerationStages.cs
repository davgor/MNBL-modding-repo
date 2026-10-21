using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;

namespace MNBL.ProceduralWorld.Generation
{
    // MMR-44: the production stages of the generation job (docs/generation-job.md). Each promotes a proven component, in place:
    //   ValidatingRequest   worldbridge --new-request (MMR-24): the canonical request from the setup's choice and the bound policies.
    //   GeneratingSource    worldbridge --generate (MMR-22/24): the supervised producer in the bridge's own job, the quality attempts and the
    //                       static travel gate per attempt; progress from its persisted attempt state; cancellation through its cancel file,
    //                       then the host-owned process job.
    //   Compiling           worldbridge --bundle (MMR-29): the immutable bundle under SubModule.Store/bundles.
    //   ValidatingData      WorldDataBundle.Open (MMR-29): the host verifier, with this host's versions.
    //   BuildingScene       RuntimeWorldBackend.Prepare off the engine thread, Install on it (MMR-37/38): the content-addressed scene.
    //   RegisteringCampaign campaign objects register inside the new game's RegisterSubModuleObjects (R07, docs/runtime-backend-decision.md
    //                       "New game order"), which is MMR-45's commit; here the phase reports that and does nothing.
    //   ValidatingRuntime   RuntimeWorldBackend.Construct, NavigationValidator.Run and NavigationGate on the installed scene (MMR-38), each
    //                       a separate engine-thread item; the inspection world is disposed.
    // The worldbridge commands run as owned children of this host (GenerationProbe.RunBridge, a kill-on-close job); the bridge owns its
    // producer child in its own job.
    internal static class JobInputs
    {
        // Embedded resource (MNBL.ProceduralWorld.<name>) -> its place in the job's inputs. The repository's layout is kept: the projection and
        // playability policies name their calibration receipts relative to themselves (pinned by sha256), and the compiler resolves them there.
        public static readonly Dictionary<string, string> Layout = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["producer.lock.json"] = "worldgen/producer.lock.json",
            ["producer-seed-width.json"] = "worldgen/contracts/producer-seed-width.json",
            ["source-compatibility.json"] = "worldgen/contracts/source-compatibility.json",
            ["product-policy.json"] = "worldgen/policies/product-policy.json",
            ["world-quality.json"] = "worldgen/policies/world-quality.json",
            ["projection.json"] = "worldgen/policies/projection.json",
            ["terrain-materials.json"] = "worldgen/policies/terrain-materials.json",
            ["campaign-content.json"] = "worldgen/policies/campaign-content.json",
            ["culture-presentations.json"] = "worldgen/policies/culture-presentations.json",
            ["playability.json"] = "worldgen/policies/playability.json",
            ["terrain-shaping.json"] = "worldgen/policies/terrain-shaping.json",   // MMR-278: the bundle stage shapes the surface
            ["population.json"] = "worldgen/policies/population.json",
            ["terrain-import.json"] = "worldgen/calibration/terrain-import/terrain-import.json",
            ["axis-sea-datum-calibration.json"] = "worldgen/calibration/axis-sea-datum/calibration.json",
            ["naval-transitions.json"] = "worldgen/calibration/naval-transitions/naval-transitions.json",
            ["runtime-api-lock.json"] = "config/runtime-api-lock.json" };
        private static readonly string[] Policies = { "product-policy", "world-quality", "source-compatibility", "projection", "terrain-materials", "terrain-import", "campaign-content",
            "culture-presentations", "playability", "population" };

        // MMR-47: the first policy a request binds with another hash than this module's (null when all match): an unfinished job made under
        // other policies is not resumed. A policy this module does not carry differs.
        public static string PolicyMismatch(IDictionary<string, string> requestPolicies)
        {
            foreach (var kv in requestPolicies.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                using (var stream = typeof(JobInputs).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + kv.Key + ".json"))
                {
                    if (stream == null) return kv.Key;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != kv.Value) return kv.Key;
                }
            }
            return null;
        }

        // The embedded documents, written into the job (the repository's layout under directory) and read back byte for byte.
        public static Dictionary<string, string> Write(string directory)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in Layout)
            {
                byte[] bytes;
                using (var stream = typeof(JobInputs).Assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + entry.Key))
                {
                    if (stream == null) throw new FileNotFoundException("The module has no embedded " + entry.Key + ".", entry.Key);
                    using (var copy = new MemoryStream()) { stream.CopyTo(copy); bytes = copy.ToArray(); }
                }
                string path = Path.GetFullPath(Path.Combine(directory, entry.Value));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("A job input differs from the module's: " + entry.Key);
                paths[entry.Key] = path;
            }
            return paths;
        }

        public static string PolicyList(Dictionary<string, string> paths)
        {
            return String.Join(",", Policies.Select(p => p + "=" + paths[p + ".json"]));
        }
    }

    internal sealed class BridgeRun
    {
        public int ExitCode;
        public string Stdout, Stderr;
        public Dictionary<string, object> Receipt;

        public static BridgeRun Execute(GenerationJob job, string role, IList<string> arguments, Action requestStop = null, int stopGraceMs = 0, Action tick = null)
        {
            var processes = new List<Dictionary<string, object>>();
            var receipt = GenerationProbe.RunBridge(arguments.ToArray(), job.Root, role, job.Token, requestStop, stopGraceMs, tick, processes);
            var all = job.Get<List<object>>("processes") ?? new List<object>();
            all.Add(receipt); job.Set("processes", all);
            Func<string, string> read = name => { string path = job.File(role + "-" + name + ".log"); return File.Exists(path) ? File.ReadAllText(path) : ""; };
            return new BridgeRun { ExitCode = Convert.ToInt32(receipt["exitCode"], CultureInfo.InvariantCulture), Stdout = read("stdout").Trim(), Stderr = read("stderr").Trim(), Receipt = receipt };
        }

        public IDictionary<string, object> StdoutJson()
        {
            string last = Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));
            if (last == null) return null;
            try { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(last) as IDictionary<string, object>; }
            catch (ArgumentException) { return null; }
        }

        public string Detail(string command) { return command + " exit " + ExitCode + (Stderr.Length > 0 ? ": " + (Stderr.Length > 600 ? Stderr.Substring(0, 600) : Stderr) : ""); }
    }

    internal static class JobJson
    {
        public static IDictionary<string, object> Read(string path) { return (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(path)); }
        public static void Write(string path, object value) { File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false)); }
    }

    internal sealed class RequestStage : IGenerationStage
    {
        public JobPhase Phase { get { return JobPhase.ValidatingRequest; } }
        public void Run(GenerationJob job)
        {
            var inputs = JobInputs.Write(job.File("inputs"));
            job.Set("inputs", inputs);
            var choice = job.Choice;
            JobJson.Write(job.File("overrides.json"), choice.Overrides.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
            string request = job.File("request.json");
            if (job.Resumed && File.Exists(request))
            {
                // MMR-47: the accepted request is never replaced (worldbridge --new-request refuses); a request made under another generator
                // (another producer lock) is not continued: an unfinished world is never regenerated under a new generator.
                var kept = JobJson.Read(request);
                if ((string)kept["producerLockSha256"] != choice.ProducerLockSha256)
                    throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.IncompatibleBuild, "resume.other_generator", "The unfinished job's request names producer lock " + kept["producerLockSha256"] + "; this module locks " + choice.ProducerLockSha256 + "."));
                Check(choice, kept);
                job.Set("request", request);
                job.Set("requestSeedSource", (string)kept["seedSource"]);
                job.Report(null, null, "request_resumed");
                return;
            }
            var run = BridgeRun.Execute(job, "request", new[] { "--new-request", "--out", request, "--instance", choice.CampaignInstanceId,
                "--seed", choice.Seed.ToString(CultureInfo.InvariantCulture), "--seed-source", choice.SeedSource == "random" ? "random" : "explicit",
                "--overrides", job.File("overrides.json"), "--lock", inputs["producer.lock.json"], "--policies", JobInputs.PolicyList(inputs),
                "--max-attempts", choice.MaxAttempts.ToString(CultureInfo.InvariantCulture) });
            job.Token.ThrowIfCancellationRequested();
            if (run.ExitCode != 0 || !File.Exists(request))
                throw new GenerationFailure(GenerationErrors.DiskFullText(run.Stderr) ? GenerationErrors.Job(GenerationErrorCategory.DiskFull, "request.disk_full", run.Detail("worldbridge --new-request"))
                    : GenerationErrors.Job(GenerationErrorCategory.RefusedRecipe, "request.refused", run.Detail("worldbridge --new-request")));
            // The request carries exactly the choice the setup showed.
            var r = JobJson.Read(request);
            Check(choice, r);
            job.Set("request", request);
            job.Set("requestSeedSource", (string)r["seedSource"]);
            job.Report(null, null, "request_accepted");
        }

        private static void Check(WorldChoice choice, IDictionary<string, object> r)
        {
            if (Convert.ToUInt32(r["masterSeed"], CultureInfo.InvariantCulture) != choice.Seed || (string)r["campaignInstanceId"] != choice.CampaignInstanceId
                || (string)r["producerLockSha256"] != choice.ProducerLockSha256 || Convert.ToInt32(r["maxAttempts"], CultureInfo.InvariantCulture) != choice.MaxAttempts)
                throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.IncompatibleBuild, "request.mismatch", "The written request differs from the setup's choice."));
        }
    }

    internal sealed class SourceStage : IGenerationStage
    {
        // The bridge stops within this time after its cancel file appears (it checks it between producer polls); then the owned job ends it.
        public const int StopGraceMs = 10000;
        public JobPhase Phase { get { return JobPhase.GeneratingSource; } }
        public void Run(GenerationJob job)
        {
            var inputs = job.Get<Dictionary<string, string>>("inputs");
            string state = job.File("state.json"), work = job.File("work"), cancel = job.File("cancel");
            string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            int reportedDone = -1;
            Action tick = () =>
            {
                try
                {
                    if (!File.Exists(state)) return;
                    var s = JobJson.Read(state);
                    // Rejected attempts (world-quality failures) are the ones done before the running attempt; an accepted one ends the stage.
                    var attempts = ((object[])s["attempts"]).Cast<IDictionary<string, object>>().ToList();
                    int rejected = attempts.Count(a => (string)a["state"] == "rejected");
                    if (rejected == reportedDone) return;
                    reportedDone = rejected;
                    job.Report(rejected, job.Choice.MaxAttempts, rejected == 0 ? "attempt_running" : "attempt_rejected");
                }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidOperationException || e is InvalidCastException || e is KeyNotFoundException) { }
            };
            job.Report(0, job.Choice.MaxAttempts, "attempt_running");
            var run = BridgeRun.Execute(job, "generate", new[] { "--generate", "--request", job.Get<string>("request"), "--state", state, "--work", work,
                "--lock", inputs["producer.lock.json"], "--producer", Path.Combine(bin, "fwg.exe"), "--seed-width", inputs["producer-seed-width.json"],
                "--policies", JobInputs.PolicyList(inputs), "--cancel-file", cancel }, () => File.WriteAllText(cancel, "cancel"), StopGraceMs, tick);
            job.Token.ThrowIfCancellationRequested();
            var summary = run.StdoutJson();
            var persisted = File.Exists(state) ? JobJson.Read(state) : null;
            job.Set("generateSummary", summary);
            if (run.ExitCode == 0 && summary != null && (string)summary["status"] == "accepted" && persisted != null)
            {
                var chosen = (IDictionary<string, object>)persisted["chosen"];
                int index = Convert.ToInt32(chosen["attempt"], CultureInfo.InvariantCulture);
                var attempt = ((object[])persisted["attempts"]).Cast<IDictionary<string, object>>().Single(a => Convert.ToInt32(a["index"], CultureInfo.InvariantCulture) == index);
                string attemptDir = Path.Combine(work, ((string)persisted["requestId"]).Substring(0, 16), "attempt-" + index + "-run-" + Convert.ToInt32(attempt["runs"], CultureInfo.InvariantCulture));
                string split = Path.Combine(attemptDir, "world");
                if (!File.Exists(Path.Combine(split, "world.json"))) throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.BadSource, "generate.split_missing", "The accepted attempt's world is missing: " + split));
                job.Set("split", split); job.Set("attemptRequest", Path.Combine(attemptDir, "request.json"));
                job.Set("attemptSeed", Convert.ToUInt32(chosen["seed"], CultureInfo.InvariantCulture)); job.Set("attempts", index + 1);
                job.Set("sourceWorldContentId", (string)chosen["worldContentId"]); job.Set("requestId", (string)persisted["requestId"]);
                job.Report(index, job.Choice.MaxAttempts, "source_accepted");   // completed: the attempts rejected before the accepted one
                return;
            }
            throw new GenerationFailure(Classify(run, summary, persisted));
        }

        // Exit 3: exhausted or failed (the last attempt's failure kind); 4: cancelled; 2: the request refused before any attempt. The mapping is
        // GenerationErrors.FromBridge (MMR-47: a full disk and changed inputs are told apart; tested offline).
        private static GenerationError Classify(BridgeRun run, IDictionary<string, object> summary, IDictionary<string, object> state)
        {
            string detail = run.Detail("worldbridge --generate");
            string status = null, failure = null, outcome = null;
            if (run.ExitCode == 3 && state != null)
            {
                status = state["status"] as string;
                var last = ((object[])state["attempts"]).Cast<IDictionary<string, object>>().LastOrDefault();
                failure = last == null ? null : last["failure"] as string; outcome = last == null ? null : last["producerOutcome"] as string;
                var reasons = last == null ? new List<string>() : ((object[])last["reasons"]).Cast<IDictionary<string, object>>().Select(r => r["code"] + ": " + r["message"]).ToList();
                detail += "; attempt failure " + failure + ", producer " + outcome + (reasons.Count > 0 ? "; " + String.Join("; ", reasons.Take(4)) : "");
            }
            return GenerationErrors.FromBridge(run.ExitCode, run.Stderr, detail, status, failure, outcome);
        }
    }

    internal sealed class CompileStage : IGenerationStage
    {
        public JobPhase Phase { get { return JobPhase.Compiling; } }
        public static string BundleRoot() { return Path.Combine(SubModule.Store, "bundles"); }
        public void Run(GenerationJob job)
        {
            var inputs = job.Get<Dictionary<string, string>>("inputs");
            string root = BundleRoot();
            Directory.CreateDirectory(root);
            // Only this job writes the bundle root while it runs (one job at a time). A killed publish leaves its staging directory, and a
            // cancellation can land after the publish's rename: every directory this run added is the job's own until it knows the outcome.
            var before = new HashSet<string>(Directory.GetDirectories(root), StringComparer.OrdinalIgnoreCase);
            job.Own("bundle staging", () => { foreach (var d in Directory.GetDirectories(root).Where(d => !before.Contains(d))) Directory.Delete(d, true); });
            job.Report(null, null, "compiling_world");
            var run = BridgeRun.Execute(job, "bundle", new[] { "--bundle", job.Get<string>("split"), "--contract", inputs["source-compatibility.json"],
                "--projection-policy", inputs["projection.json"], "--materials", inputs["terrain-materials.json"], "--import", inputs["terrain-import.json"],
                "--content", inputs["campaign-content.json"], "--cultures", inputs["culture-presentations.json"], "--playability", inputs["playability.json"], "--shaping", inputs["terrain-shaping.json"],
                "--population", inputs["population.json"],
                "--producer-lock", inputs["producer.lock.json"], "--runtime-lock", inputs["runtime-api-lock.json"], "--recipe", job.Get<string>("attemptRequest"),
                "--root", root, "--diagnostics", job.File("diagnostics") });
            job.Token.ThrowIfCancellationRequested();
            var result = run.StdoutJson();
            job.Set("bundleResult", result == null ? null : result.Where(kv => kv.Key != "inputs").ToDictionary(kv => kv.Key, kv => kv.Value));
            if (run.ExitCode != 0 || result == null || !result.ContainsKey("directory"))
            {
                var category = GenerationErrors.DiskFullText(run.Stderr) || GenerationErrors.DiskFullText(run.Stdout) ? GenerationErrorCategory.DiskFull : GenerationErrorCategory.BadSource;
                throw new GenerationFailure(GenerationErrors.Job(category, run.ExitCode == 3 ? "compile.travel_rejected" : "compile.failed", run.Detail("worldbridge --bundle") + " " + run.Stdout));
            }
            string directory = Path.GetFullPath((string)result["directory"]);
            bool existed = result.ContainsKey("existed") && result["existed"] is bool && (bool)result["existed"];
            // Reused content was already there before this run; published content is owned below; staging the bridge removed itself.
            job.Disown("bundle staging");
            // A bundle this job published is its own until committed; a reused one (identical content already published) is not.
            if (!existed) job.Own("bundle " + Path.GetFileName(directory), () => { if (Directory.Exists(directory)) Directory.Delete(directory, true); });
            job.Set("bundleDirectory", directory); job.Set("bundleExisted", existed);
            job.Report(null, null, existed ? "bundle_reused" : "bundle_published");
        }
    }

    internal sealed class DataStage : IGenerationStage
    {
        public JobPhase Phase { get { return JobPhase.ValidatingData; } }
        public void Run(GenerationJob job)
        {
            WorldDataBundle bundle;
            try { bundle = WorldDataBundle.Open(job.Get<string>("bundleDirectory"), WorldSetupHost.BundleHost()); }
            catch (WorldDataBundle.RejectedException rejected) { throw new GenerationFailure(GenerationErrors.FromBundleProblems(rejected.Problems), rejected); }
            job.Set("bundle", bundle); job.Set("worldContentId", bundle.WorldContentId); job.Set("manifestSha256", bundle.ManifestSha256);
            job.Report(null, null, "bundle_verified");
        }
    }

    internal sealed class SceneStage : IGenerationStage
    {
        public JobPhase Phase { get { return JobPhase.BuildingScene; } }
        public void Run(GenerationJob job)
        {
            var bundle = job.Get<WorldDataBundle>("bundle");
            var token = WorldGenerations.Begin("generation job " + job.Id.Substring(0, 8));
            job.Set("generation", token);
            job.Own("world generation " + token.Id, () => WorldGenerations.Retire(token));
            job.Report(null, null, "modelling_surface");
            var prepared = RuntimeWorldBackend.Prepare(bundle, token, job.Token);
            job.Own("surface staging", prepared.Discard);
            job.Set("prepared", prepared); job.Set("sceneName", prepared.SceneName); job.Set("navmeshSha256", prepared.Navigation.Sha256); job.Set("prepareSeconds", prepared.EncodeSeconds);
            job.Report(null, null, "installing_scene");
            // The files are copied and hashed here; only the name registration runs on the engine thread; the sweep runs here again.
            bool reused = RuntimeWorldBackend.InstallFiles(prepared);
            prepared.Discard();
            job.Disown("surface staging");
            var install = job.Engine("register scene " + prepared.SceneName, () => RuntimeWorldBackend.Register(prepared, reused));
            install["archive"] = prepared.Archive;
            // MMR-282: the sweep keeps every scene a world package, a save or this process names (the save list is read on the engine thread).
            var references = job.Engine("world references", () => RuntimeWorldBackend.References());
            foreach (var kv in RuntimeWorldBackend.Sweep(references)) install[kv.Key] = kv.Value;
            job.Set("install", install);
            job.Set("sceneStore", "SceneObj/" + prepared.SceneName + " belongs to the scene store (reused by name, archived under world-scenes; a sweep removes it only while no package, save or process names it)");
        }
    }

    internal sealed class CampaignStage : IGenerationStage
    {
        public JobPhase Phase { get { return JobPhase.RegisteringCampaign; } }
        public void Run(GenerationJob job) { job.Report(null, null, "registration_at_campaign_start"); }
    }

    internal sealed class RuntimeStage : IGenerationStage
    {
        public const int ValidationSliceMs = 25;
        public JobPhase Phase { get { return JobPhase.ValidatingRuntime; } }
        public void Run(GenerationJob job)
        {
            var prepared = job.Get<PreparedSurface>("prepared");
            var token = job.Get<GenerationToken>("generation");
            var model = NavigationModel.Installed(NavigationModel.InstalledCalibration());
            job.Report(null, null, "constructing_scene");
            var world = job.Engine("construct " + prepared.SceneName, () =>
            {
                var w = RuntimeWorldBackend.Construct(prepared.SceneName, token, prepared.Surface, model);
                try { w.Scene.Tick(0); } catch { w.Dispose(); throw; }
                return w;
            }, w => w.Dispose());
            job.Own("inspection world", () => job.Teardown("dispose inspection world", world.Dispose));
            // The validator in steps (face table, face checks, each route), each its own engine-thread item: the engine thread is given back
            // between them.
            var result = new NavigationValidator.Result();
            var steps = NavigationValidator.Steps(prepared.NavigationPlan, new SceneNavigationEngine(world), prepared.SceneName,
                s => job.Report(null, null, s.StartsWith("navigation_", StringComparison.Ordinal) ? s : "navigation_" + s), null, result).GetEnumerator();
            // Each engine item takes steps until about 25 ms have passed (one step at least): bounded holds, without one frame per route.
            int items = 0, taken = 0;
            while (job.Engine("validate navigation " + items, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool more;
                do { more = steps.MoveNext(); if (more) taken++; } while (more && watch.ElapsedMilliseconds < ValidationSliceMs);
                return more;
            })) items++;
            var report = result.Report;
            job.Set("navigationSteps", taken); job.Set("navigationItems", items + 1);
            // MMR-40: the read scene's settlement entities against the layout they were built from, before the world goes; MMR-258: in slices of
            // about 25 ms like the routes (a look's hierarchy is larger).
            job.Report(null, null, "checking_settlement_entities");
            var settlements = new Dictionary<string, object>();
            var entitySteps = SettlementEntityChecks.Steps(world, prepared, settlements).GetEnumerator();
            int entityItems = 0;
            while (job.Engine("check settlement entities " + entityItems, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool more;
                do { more = entitySteps.MoveNext(); } while (more && watch.ElapsedMilliseconds < ValidationSliceMs);
                return more;
            })) entityItems++;
            job.Set("settlementEntityItems", entityItems + 1);
            job.Set("settlementEntitiesPassed", settlements["passed"]); job.Set("settlementEntityPositionChecks", settlements["positionChecks"]);
            // MMR-41: the post-placement semantic route set through the engine, in slices like the navigation routes. The scene's previous report
            // goes first, so a run that stops before Persist leaves none.
            job.Report(null, null, "playability_routes");
            RuntimePlayabilityValidator.Delete(SubModule.Store, prepared.SceneName);
            var bundle = job.Get<WorldDataBundle>("bundle");
            var semantic = RuntimePlayabilityValidator.RouteSet(PlayabilityWorld.FromBundle(prepared.NavigationPlan.Build.Input, bundle.ReadVerified("compiled/campaign/campaign.json"), prepared.Settlements, prepared.SettlementGround));
            var playRows = new List<Dictionary<string, object>>();
            var playSteps = RuntimePlayabilityValidator.Steps(prepared.NavigationPlan, new SceneNavigationEngine(world), semantic, playRows).GetEnumerator();
            int playItems = 0;
            while (job.Engine("validate playability " + playItems, () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool more;
                do { more = playSteps.MoveNext(); } while (more && watch.ElapsedMilliseconds < ValidationSliceMs);
                return more;
            })) playItems++;
            job.Set("playabilityItems", playItems + 1);
            job.Engine("dispose inspection world", () => { world.Dispose(); return true; });
            job.Disown("inspection world");
            job.Set("navigationActivatable", report["activatable"]); job.Set("navigationReportSha256", report["reportSha256"]);
            job.Set("coordinatePolicy", report["coordinatePolicy"]); job.Set("coordinatePolicyVersion", report["coordinatePolicyVersion"]);   // MMR-64
            try
            {
                job.Set("navigationReport", NavigationGate.Persist(SubModule.Store, report));
                NavigationGate.Require(SubModule.Store, prepared.SceneName, Path.Combine(RuntimeWorldBackend.SceneFolder(prepared.SceneName), "navmesh.bin"), NavigationPolicy.Installed().Sha256);
            }
            catch (NavigationRefusedException refused)
            {
                throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime.navigation_refused", refused.Message), refused);
            }
            job.Report(null, null, "navigation_verified");
            if (!(bool)settlements["passed"])
            {
                var failures = (List<string>)settlements["failures"];
                throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime.settlement_entities", failures.Count + " settlement entity failures, first: " + failures[0]), null);
            }
            job.Report(null, null, "settlement_entities_verified");
            // MMR-41: every compiler pending-runtime flag resolved or failed, and the activation report MMR-45 requires.
            int playFailed = playRows.Count(r => !Equals(r["agrees"], true));
            var flags = RuntimePlayabilityValidator.ResolveFlags(bundle.ReadVerified("compiled/travel/travel-graph.json"), bundle.ReadVerified("compiled/travel/ports.json"),
                RuntimePlayabilityValidator.PendingRuntime(MNBL.ProceduralWorld.CampaignRegistry.CampaignGraphValidator.InstalledPolicy()), report, settlements, playFailed == 0, playFailed);
            var notFlags = RuntimePlayabilityValidator.NotFlags(bundle.ReadVerified("compiled/validation.json"), RuntimePlayabilityValidator.Embedded("product-policy.json"));
            var playability = RuntimePlayabilityValidator.Report(bundle.WorldContentId, prepared.SceneName, prepared.Settlements, report, settlements, semantic, playRows, flags, "scene (inspection world)", null, notFlags);
            job.Set("playabilityReport", RuntimePlayabilityValidator.Persist(SubModule.Store, playability)); job.Set("playabilityActivatable", playability["activatable"]);
            job.Set("playabilityRoutes", playRows.Count); job.Set("playabilityRoutesFailed", playFailed); job.Set("playabilityFlags", flags.Count);
            job.Set("playabilityFlagsFailed", flags.Count(f => (string)f["status"] == "failed")); job.Set("playabilityReportSha256", playability["reportSha256"]);
            if (!Equals(playability["activatable"], true))
            {
                var failedFlags = flags.Where(f => (string)f["status"] == "failed").Select(f => f["kind"] + " " + f["check"] + " " + f["subject"]).ToList();
                throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime.playability", playFailed + " semantic routes and " + failedFlags.Count + " pending-runtime flags failed"
                    + (playFailed > 0 ? ", first route: " + playRows.First(r => !Equals(r["agrees"], true))["id"] : ", first flag: " + failedFlags[0])), null);
            }
            try { RuntimePlayabilityValidator.Require(SubModule.Store, prepared.SceneName, NavigationGate.ReportPath(SubModule.Store, prepared.SceneName), RuntimeWorldBackend.SettlementReportPath(prepared.SceneName)); }
            catch (InvalidOperationException refused) { throw new GenerationFailure(GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime.playability", refused.Message), refused); }
            job.Report(null, null, "playability_verified");
        }
    }

    // The game's generation job: one coordinator, the engine-work scheduler pumped every application tick, jobs under SubModule.Store/jobs.
    internal static class GenerationRuntime
    {
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public static readonly EngineWorkScheduler Scheduler = new EngineWorkScheduler(() => EngineThread.IsCurrent, () => clock.Elapsed.TotalSeconds);
        private static GenerationCoordinator coordinator;
        public static GenerationCoordinator Coordinator
        {
            get
            {
                if (coordinator == null)
                    coordinator = new GenerationCoordinator(new IGenerationStage[] { new RequestStage(), new SourceStage(), new CompileStage(), new DataStage(), new SceneStage(), new CampaignStage(), new RuntimeStage() },
                        Scheduler, () => clock.Elapsed.TotalSeconds, Path.Combine(SubModule.Store, "jobs"), Classify) { Admission = MNBL.ProceduralWorld.Persistence.SaveBindingRuntime.WriterRefusal };
                return coordinator;
            }
        }

        // MMR-49: the phase of a generation job that is running or still being released, null when none (a load refuses meanwhile). Never
        // creates the coordinator.
        public static string ActiveJobPhase()
        {
            var c = coordinator;
            var job = c == null ? null : c.Current;
            if (job == null) return null;
            var released = job.Released;
            return !JobStateMachine.IsTerminal(job.Phase) || (released != null && !released.IsCompleted) ? job.Phase.ToString() : null;
        }

        public static GenerationError Classify(Exception error, GenerationJob job)
        {
            string detail = error.GetType().Name + ": " + error.Message;
            if (error is NavigationRefusedException || error is SettlementPlacementRefusedException || error is StaleWorldException) return GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime." + error.GetType().Name, detail);
            if (error is WorldDataBundle.RejectedException) return GenerationErrors.Job(GenerationErrorCategory.BadSource, "data.rejected", detail);
            return GenerationErrors.FromGeneration(null, error, null, detail);
        }
    }
}
