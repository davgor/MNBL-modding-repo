using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.World;

// MMR-261 runtime landscape mutation tests, without the game:
//   WorldMutationTests REPO BUNDLE_A OUT_JSON [PRODUCER]
// On the real compiled world A' (seed 42):
//   a1  a recorded fwg apply delta for a fire cast at Nerdun (tests/World/fixtures/mutation, made by the pinned producer from the bundle's
//       source/request.json and the operations the capital rule emits) applies to MMR-260's overlay (retyped cells within landValid, land
//       shares before and after) and the campaign document round-trips byte for byte; its replay gives the same overlay snapshot;
//       with PRODUCER, the real fwg apply runs on the background runner and writes the recorded delta byte for byte;
//   a2  no generator process starts during a load: the runner refuses while the load predicate holds and its launcher is never called; a
//       load beginning after the start drops the job; the in-game launcher is the counted "producer_apply" role and refuses during a load;
//   a3  after a producer update the campaign keeps casting with its recorded build, refuses new casts only when that build is missing, and
//       always loads its stored mutations (RT-37);
//   r1  a digest mismatch disables only the mutation cells and casts; the generated magic ground stays drawn and the document is kept (RT-37);
// plus the policy and archive readers, the capital rule's three branches and the direction of a source sample, and (MMR-262) a failed or
// timed-out fwg apply held until the next daily batch and a cancelled job's folder removed.
internal static class WorldMutationTests
{
    private static int checks, failures;
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static bool Throws(Action a) { try { a(); return false; } catch (Exception) { return true; } }
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private static int Main(string[] args)
    {
        var evidence = new Dictionary<string, object> { ["schema"] = "mnbl.world-mutation-offline", ["version"] = 1 };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[2], Serializer.Serialize(evidence), Utf8);
        Console.WriteLine(checks - failures + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string[] args, Dictionary<string, object> evidence)
    {
        string repo = args[0], producerExe = args.Length > 3 ? args[3] : null;
        var policy = MutationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, MutationPolicy.PolicyPath)));
        var archive = ProducerArchive.FromBytes(File.ReadAllBytes(Path.Combine(repo, ProducerArchive.PolicyPath)));
        var lockDoc = (IDictionary<string, object>)Serializer.DeserializeObject(File.ReadAllText(Path.Combine(repo, "worldgen/producer.lock.json")));
        var pinned = (IDictionary<string, object>)lockDoc["producer"];
        string pin = (string)pinned["sha256"];
        var lockApply = ((object[])((IDictionary<string, object>)pinned["commands"])["apply"]).Cast<string>().ToList();
        Check(policy.Schools.SequenceEqual(MutationOperations.VisibleSchools) && policy.FirstIntensity == 4 && policy.ResidueThreshold == 10 && archive.Builds.Any(b => b.Sha256 == pin && b.Apply.SequenceEqual(lockApply))
              && Throws(() => MutationPolicy.FromBytes(Utf8.GetBytes(File.ReadAllText(Path.Combine(repo, MutationPolicy.PolicyPath)).Replace("\"maxIntensity\": 4", "\"maxIntensity\": 5"))))
              && Throws(() => MutationPolicy.FromBytes(Utf8.GetBytes(File.ReadAllText(Path.Combine(repo, MutationPolicy.PolicyPath)).Replace("\"air\"]", "\"blood\"]"))))
              && Throws(() => ProducerArchive.FromBytes(Utf8.GetBytes(File.ReadAllText(Path.Combine(repo, ProducerArchive.PolicyPath)).Replace("\"Producers/7", "\"bin/7")))),
              "mutation.json and producer-archive.json read strictly: the eight visible schools, intensity 4 at a residue of 10; the pinned producer " + pin.Substring(0, 12) + " is archived with the lock's apply command; an intensity above 4, a hidden school and an archive path off Producers/<sha256> are refused");

        // ---- the world (A', seed 42) ----
        var table = BiomeTable.FromBytes(File.ReadAllBytes(Path.Combine(repo, BiomeTable.PolicyPath)));
        var navigationPolicy = NavigationPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, NavigationPolicy.PolicyPath)));
        var magic = RoadsPolicy.FromBytes(File.ReadAllBytes(Path.Combine(repo, RoadsPolicy.PolicyPath))).Magic;
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var bundle = WorldDataBundle.Open(args[1], host);
        evidence["bundle"] = bundle.WorldContentId;
        var surface = WorldSurface.FromBundle(bundle);
        var cells = NavigationInput.FromBundle(bundle, surface, navigationPolicy).Cells;
        byte[] worldJson = bundle.ReadVerified("source/world.json");
        GeneratedClimateSystem.Source source;
        using (var stream = new MemoryStream(worldJson)) source = GeneratedClimateSystem.ReadSource(stream, GeneratedClimateSystem.Sha(worldJson));
        worldJson = null;
        byte[] request = bundle.ReadVerified("source/request.json");
        var manifestSource = (IDictionary<string, object>)bundle.Manifest["source"];
        string bundleProducer = (string)((IDictionary<string, object>)manifestSource["producer"])["binarySha256"];
        string recipeSha = (string)((IDictionary<string, object>)manifestSource["recipe"])["sha256"];
        Check(Json.Sha(request) == recipeSha && bundleProducer == pin && Utf8.GetString(request).Contains("\"seed\":42"),
              "the bundle stores its accepted request.json (source/request.json, the manifest's recipe " + recipeSha.Substring(0, 12) + ") and names the pinned producer that generated it");
        var overlay = new WorldSurfaceOverlay(MagicGroundSource.From(source, surface, cells, table));
        var src = overlay.Source;
        var campaign = (IDictionary<string, object>)Serializer.DeserializeObject(Utf8.GetString(bundle.ReadVerified("compiled/campaign/campaign.json")));
        var graph = (IDictionary<string, object>)Serializer.DeserializeObject(Utf8.GetString(bundle.ReadVerified("compiled/travel/travel-graph.json")));
        var sites = MutationSites.From(campaign, graph, source.Realms, surface.Strip, surface.PoleMargin);

        // ---- the capital rule ----
        const string NerdunUid = "surface-city-2-13033-human_cold";
        string nerdun; sites.CityTown.TryGetValue(NerdunUid, out nerdun);
        string realm = null, kingdom = null; if (sites.RealmOfCity.TryGetValue(NerdunUid, out realm)) sites.Kingdom.TryGetValue(realm, out kingdom);
        var village = Json.A(campaign, "villages").Cast<IDictionary<string, object>>().FirstOrDefault(v => Json.S(v, "bound") == nerdun);
        string villageId = village == null ? null : Json.S(village, "id");
        var other = Json.A(campaign, "kingdoms").Cast<IDictionary<string, object>>().First(k => Json.S(k, "id") != kingdom);
        string home = Json.A(campaign, "houses").Cast<IDictionary<string, object>>().Where(h => Json.S(h, "kingdom") == kingdom).Select(h => Json.S(h, "home")).First(h => h != nerdun && sites.Point.ContainsKey(h));
        var held = sites.Resolve(villageId, s => kingdom, k => home);
        var lost = sites.Resolve(villageId, s => s == nerdun ? Json.S(other, "id") : kingdom, k => home);
        var gone = sites.Resolve(villageId, s => null, k => null);
        Check(nerdun != null && kingdom != null && villageId != null && sites.Capital[realm] == NerdunUid && held != null && held.At == nerdun && held.Rule == "capital" && held.City == NerdunUid
              && lost != null && lost.At == home && lost.Rule == "rulingHouseHome" && gone != null && gone.At == villageId && gone.Rule == "eventSite" && gone.City == null
              && sites.Resolve("no-such-settlement", s => kingdom, k => home) == null,
              "capital rule (MMR-279's memberRealms): an event at Nerdun's village " + villageId + " goes to the FWGC++ capital of its member realm " + realm + " (Nerdun " + nerdun + ", city_uid) while kingdom " + kingdom +
              " holds it, else to the ruling house's home " + home + ", else to the event settlement (a direction); an unknown settlement is refused");

        // ---- the model over a synthetic archive (the bundle's producer is the archived build) ----
        string scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2])), "mutation-work");
        Directory.CreateDirectory(scratch);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // path -> the bytes' hash a fake file system answers
        Func<ProducerArchive, MutationModel.Context> context = a => new MutationModel.Context { Source = src, Sites = sites, Policy = policy, Archive = a, Request = request, RequestSha256 = Json.Sha(request),
            WorldSha256 = source.Sha256, ProducerSha256 = bundleProducer, Seed = source.Seed, ModuleRoot = Path.Combine(scratch, "module"), WorkRoot = Path.Combine(scratch, "work"),
            Exists = p => files.ContainsKey(p), Hash = p => files[p] };
        string archivedPin = Path.Combine(scratch, "module", "Producers", pin, "fwg.exe");
        files[archivedPin] = pin;
        var model = new MutationModel(context(archive));
        Check(model.CastRefusal() == null && model.Report(new MagicEvent("blood", villageId, 5)) != null && model.Report(new MagicEvent("fire", "nowhere", 5)) != null && model.Report(new MagicEvent("fire", villageId, -1)) != null,
              "casts are allowed with the recorded build archived; a hidden school, an unknown settlement and a non-positive strength are refused");
        foreach (var s in new[] { 4.0, 4.0, 4.0 }) model.Report(new MagicEvent("fire", villageId, s));
        Func<string, string> owner = s => kingdom; Func<string, string> ruling = k => home;
        var emitted = model.DailyBatch(10, owner, ruling);
        var edit = emitted.FirstOrDefault();
        double left; model.State.Residue.TryGetValue("fire|" + villageId, out left);
        var none = model.DailyBatch(11, owner, ruling);
        Check(emitted.Count == 1 && edit.At == nerdun && edit.City == NerdunUid && edit.Intensity == 4 && edit.Node == "mnbl-cast-fire-" + nerdun && Math.Abs(left - 2) < 1e-12 && none.Count == 0 && model.State.Pending.Count == 1,
              "daily batch: three fire events of 4 at the village cross the residue threshold 10 once: one LeylineEdit, a key point of intensity 4 at Nerdun (city_uid), residue 2 left; the next day emits nothing");
        string operations = MutationOperations.Document(model.State.Edits.Concat(model.State.Pending));
        Check(operations == "{\"schema\":\"fwg.operations\",\"version\":1,\"operations\":[{\"kind\":\"shift leylines\",\"api_version\":1,\"leyline_edits\":[{\"school\":\"fire\",\"new_node\":{\"id\":\"mnbl-cast-fire-" + nerdun + "\",\"intensity\":4,\"city_uid\":\"" + NerdunUid + "\"}}]}]}",
              "the operations document is fwg.operations v1 with one shift planting the key point");

        // ---- a1: the recorded delta ----
        string fixtures = Path.Combine(repo, "tests", "World", "fixtures", "mutation");
        string recordedDelta = Path.Combine(fixtures, "nerdun-fire.delta.json"), recordedOps = Path.Combine(fixtures, "nerdun-fire.operations.json"), recordedRequest = Path.Combine(fixtures, "request-a.json");
        Check(File.Exists(recordedDelta) && File.ReadAllText(recordedOps) == operations && File.ReadAllBytes(recordedRequest).SequenceEqual(request),
              "the recorded fwg apply files are this cast's: the operations the model emits and the bundle's request.json (tests/World/fixtures/mutation)");
        string deltaText = File.ReadAllText(recordedDelta, Utf8);
        int launches = 0; int workerThread = -1;
        var runner = new MutationRunner(() => false, (job, token) =>
        {
            Interlocked.Increment(ref launches); workerThread = Thread.CurrentThread.ManagedThreadId;
            bool inputs = File.ReadAllBytes(job.Request).SequenceEqual(request) && File.ReadAllText(job.Operations) == operations && job.Producer == archivedPin && job.ProducerSha256 == pin;
            return new ApplyOutcome { Job = job, Succeeded = inputs, Delta = deltaText, Error = inputs ? null : "the job's inputs differ" };
        });
        Check(model.Pump(runner) == null, "Pump starts fwg apply for the pending edit on the runner");
        var outcome = Wait(runner);
        MagicChange change = null;
        var applied = model.Applied(outcome, (c, n) => { change = overlay.Replace(c, n); });
        var delta = model.LastDelta;
        var landValid = new HashSet<int>(table.LandValid.Select(TerrainTypes.Id));
        var snap = overlay.Current;
        Check(applied != null && launches == 1 && workerThread != Thread.CurrentThread.ManagedThreadId && delta.Listed > applied.Count && applied.Count > 0 && model.State.Pending.Count == 0 && model.State.Edits.Count == 1
              && snap.LiveCount == applied.Count && snap.RetypedCells.Length > 0 && snap.RetypedCells.All(c => landValid.Contains(snap.CellTypes[c])) && snap.Nodes.Count == 1 && snap.Nodes[0].School == "fire"
              && change.SharesBefore != null && change.SharesAfter != null,
              "a1: the recorded delta (" + delta.Listed + " cells listed, " + delta.Changed + " with a new biome_variant, every before the generated layer's) applies on the engine thread's turn after the background job: " +
              snap.LiveCount + " live cells, " + snap.RetypedCells.Length + " travel cells retyped within landValid, the fire node at Nerdun; land shares " + Shares(change.SharesBefore) + " -> " + Shares(change.SharesAfter));
        string saved = model.Save();
        bool digestOk; var parsed = MutationState.Parse(saved, out digestOk);
        string again = parsed.ToJson();
        var reload = new MutationModel(context(archive));
        List<MutationCell> restoredCells; List<MagicNode> restoredNodes;
        bool loaded = reload.Load(saved, out restoredCells, out restoredNodes);
        var replayOverlay = new WorldSurfaceOverlay(src);
        replayOverlay.Replace(restoredCells, restoredNodes);
        Check(digestOk && again == saved && loaded && reload.Save() == saved && replayOverlay.Current.Digest == overlay.Current.Digest && replayOverlay.Current.CellTypes.SequenceEqual(overlay.Current.CellTypes),
              "a1: the campaign's SyncData string (" + saved.Length + " bytes, digest " + parsed.Digest().Substring(0, 12) + ") round-trips byte for byte (parse -> write, load -> save), and its replay gives the same overlay snapshot (digest " + overlay.Current.Digest.Substring(0, 12) + ")");
        evidence["a1"] = new Dictionary<string, object> { ["deltaSha256"] = Json.Sha(deltaText), ["listed"] = delta.Listed, ["variantChanged"] = delta.Changed, ["cleared"] = delta.Cleared, ["liveCells"] = snap.LiveCount,
            ["retypedCells"] = snap.RetypedCells.Length, ["landSharesBefore"] = change.SharesBefore, ["landSharesAfter"] = change.SharesAfter, ["documentBytes"] = saved.Length, ["documentSha256"] = Json.Sha(saved),
            ["overlayDigest"] = overlay.Current.Digest, ["operations"] = operations };
        // The direction rule: Nerdun's source sample gives the direction FWGC++ resolved for its city_uid.
        var recorded = (Dictionary<string, object>)MutationJson.Parse(deltaText);
        var firstOp = (Dictionary<string, object>)((List<object>)recorded["operations"])[0];
        var firstEdit = (Dictionary<string, object>)((List<object>)firstOp["leyline_edits"])[0];
        var resolved = ((List<object>)((Dictionary<string, object>)firstEdit["new_node"])["direction"]).Select(MutationJson.Num).ToArray();
        int nerdunSample = src.SampleAt(sites.Point[nerdun][0], sites.Point[nerdun][1]);
        var mine = MutationOperations.Direction(nerdunSample / src.Side, nerdunSample % src.Side, src.Side);
        Check(Enumerable.Range(0, 3).All(i => Math.Abs(mine[i] - resolved[i]) < 1e-9),
              "a non-city site's direction (row, column on FWGC++'s sphere) is the one FWGC++ resolves for Nerdun's city_uid (" + String.Join(", ", resolved.Select(v => v.ToString("0.000000", CultureInfo.InvariantCulture))) + ")");

        if (producerExe != null)
        {
            // The real fwg apply on the background runner (the pinned build), byte for byte the recorded delta.
            files.Clear(); files[archivedPin] = pin;
            var real = new MutationModel(context(archive));
            real.Report(new MagicEvent("fire", villageId, 12)); real.DailyBatch(10, owner, ruling);
            var realRunner = new MutationRunner(() => false, (job, token) => RunProcess(producerExe, job, token));
            var watch = Stopwatch.StartNew();
            string started = real.Pump(realRunner);
            var realOutcome = Wait(realRunner);
            Check(started == null && realOutcome.Succeeded && realOutcome.Delta == deltaText && real.Applied(realOutcome, (c, n) => { }) != null,
                  "a1 (live): the pinned fwg.exe's apply on the background runner wrote the recorded delta byte for byte in " + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" + (realOutcome.Error == null ? "" : " (" + realOutcome.Error + ")"));
            evidence["liveApplySeconds"] = realOutcome.Seconds;
        }

        // ---- a2: no generator process during a load ----
        bool loading = true; int started2 = 0;
        var gated = new MutationRunner(() => loading, (job, token) => { Interlocked.Increment(ref started2); return new ApplyOutcome { Job = job, Succeeded = true, Delta = deltaText }; });
        var during = new MutationModel(context(archive));
        during.Report(new MagicEvent("fire", villageId, 10)); during.DailyBatch(3, owner, ruling);
        string refused = during.Pump(gated);
        Thread.Sleep(50);
        bool noneDuringLoad = refused != null && refused.Contains("load") && started2 == 0 && gated.RefusedDuringLoad == 1 && !gated.Busy && during.State.Pending.Count == 1;
        loading = false;
        string afterLoad = during.Pump(gated); var o2 = Wait(gated);
        // A load beginning between the start and the producer's launch: the worker sees it and drops the job without launching.
        var gate = new ManualResetEventSlim(false); int launchedLate = 0; bool lateLoading = false;
        var late = new MutationRunner(() => lateLoading, (job, token) => { Interlocked.Increment(ref launchedLate); return new ApplyOutcome { Job = job, Succeeded = true, Delta = deltaText }; });
        var lateModel = new MutationModel(context(archive));
        lateModel.Report(new MagicEvent("fire", villageId, 10)); lateModel.DailyBatch(3, owner, ruling);
        lateLoading = false;
        var blocking = new MutationRunner(() => lateLoading, (job, token) => { gate.Wait(); if (lateLoading) return new ApplyOutcome { Job = job, Cancelled = true, Error = "load" }; Interlocked.Increment(ref launchedLate); return new ApplyOutcome { Job = job, Succeeded = true, Delta = deltaText }; });
        lateModel.Pump(blocking); lateLoading = true; lateModel.Cancel(blocking); gate.Set(); Thread.Sleep(100);
        bool dropped = blocking.Take() == null && !blocking.Busy && launchedLate == 0 && lateModel.State.Pending.Count == 1;
        string probe = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Diagnostics/GenerationProbe.cs"));
        string loader = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Persistence/GeneratedWorldLoader.cs"));
        string behavior = File.ReadAllText(Path.Combine(repo, "src/MNBL.ProceduralWorld/Campaign/WorldMutationBehavior.cs"));
        int apply = probe.IndexOf("internal static Surface.ApplyOutcome RunProducerApply(", StringComparison.Ordinal);
        string applyBody = apply < 0 ? "" : probe.Substring(apply, probe.IndexOf("internal static void RequireEmptyOutput", apply, StringComparison.Ordinal) - apply);
        bool audited = applyBody.IndexOf("GeneratedWorldLoader.Busy", StringComparison.Ordinal) > 0 && applyBody.IndexOf("\"producer_apply\"", StringComparison.Ordinal) > 0 && applyBody.IndexOf("Execute(", StringComparison.Ordinal) > 0
            && loader.IndexOf("kv.Key != \"surface_encoder\" && (int)kv.Value > 0", StringComparison.Ordinal) > 0 && loader.IndexOf("WorldMutationBehavior.CancelForLoad();", StringComparison.Ordinal) > 0
            && behavior.IndexOf("GeneratedWorldLoader.Busy || loadPending || MNBL.ProceduralWorld.World.MagicGroundRuntime.Snapshot == null", StringComparison.Ordinal) > 0
            && behavior.IndexOf("Process.Start", StringComparison.Ordinal) < 0;
        Check(noneDuringLoad && afterLoad == null && o2 != null && started2 == 1 && dropped && audited,
              "a2: while a load runs the runner refuses (\"" + refused + "\"), its launcher is never called and the edit stays pending; after the load it starts once; a load beginning before the launch drops the job unlaunched; " +
              "in game the only launcher is GenerationProbe.RunProducerApply, which refuses while GeneratedWorldLoader.Busy and starts the producer as the counted \"producer_apply\" role (the loader's generatorStarted audit counts every role but the surface encoder), and a load's Begin cancels a running job");

        // A failed fwg apply keeps its edit pending and is retried after the next daily batch, not at every tick.
        var failing = new MutationRunner(() => false, (job, token) => new ApplyOutcome { Job = job, Error = "exit 2" });
        var retry = new MutationModel(context(archive));
        retry.Report(new MagicEvent("fire", villageId, 10)); retry.DailyBatch(4, owner, ruling);
        retry.Pump(failing); retry.Applied(Wait(failing), (c, n) => { });
        string heldBack = retry.Pump(failing);
        retry.DailyBatch(5, owner, ruling);
        string retried = retry.Pump(failing); Wait(failing);
        Check(heldBack != null && heldBack.Contains("retried") && retried == null && retry.State.Pending.Count == 1 && retry.LastError.Contains("exit 2"),
              "a failed fwg apply keeps its edit pending (\"" + retry.LastError + "\") and starts again only after the next daily batch");

        // MMR-262: fwg apply's own timeout is a failure, not a cancellation, so it is held until the next daily batch like one, not relaunched at
        // the next tick. The in-game launcher reads its outcome with ApplyOutcome.Finished, cancelled by the caller's token only.
        var probeJob = new ApplyJob { TimeoutSeconds = policy.ApplyTimeoutSeconds };
        var expired = ApplyOutcome.Finished(probeJob, 1223, false, true, false);
        var released = ApplyOutcome.Finished(probeJob, 1223, true, false, false);
        var finishedAsItExpired = ApplyOutcome.Finished(probeJob, 0, false, true, true);
        int slowLaunches = 0;
        var slow = new MutationRunner(() => false, (job, token) => { Interlocked.Increment(ref slowLaunches); return ApplyOutcome.Finished(job, 1223, token.IsCancellationRequested, true, false); });
        var slowModel = new MutationModel(context(archive));
        slowModel.Report(new MagicEvent("fire", villageId, 10)); slowModel.DailyBatch(6, owner, ruling);
        slowModel.Pump(slow); slowModel.Applied(Wait(slow), (c, n) => { });
        string slowHeld = slowModel.Pump(slow); int launchesHeld = slowLaunches;
        slowModel.DailyBatch(7, owner, ruling);
        string slowRetried = slowModel.Pump(slow); Wait(slow);
        bool readsTimeout = applyBody.IndexOf("ApplyOutcome.Finished(job, exit, token.IsCancellationRequested, timedOut,", StringComparison.Ordinal) > 0
            && applyBody.IndexOf("timeout.IsCancellationRequested && !token.IsCancellationRequested", StringComparison.Ordinal) > 0;
        Check(!expired.Cancelled && !expired.Succeeded && expired.Error == "timed out after " + policy.ApplyTimeoutSeconds + " s" && released.Cancelled && released.Error == "cancelled"
              && finishedAsItExpired.Succeeded && finishedAsItExpired.Error == null && readsTimeout
              && slowHeld != null && slowHeld.Contains("retried") && launchesHeld == 1 && slowRetried == null && slowLaunches == 2 && slowModel.State.Pending.Count == 1
              && slowModel.LastError.StartsWith("fwg apply failed: timed out", StringComparison.Ordinal),
              "a timed-out fwg apply is a failure (\"" + slowModel.LastError + "\"), not a cancellation: its edit stays pending, the next tick's Pump is held (\"" + slowHeld +
              "\") and it relaunches only after the next daily batch; only the caller's token cancels (RunProducerApply)");

        // MMR-262: a cancelled job's folder under <store>/mutations goes too: a running job's when its worker sees the cancel (its process has
        // exited), and a finished outcome's when the cancel drops it before the engine thread takes it.
        var hold = new ManualResetEventSlim(false); string runningDir = null;
        var cancelling = new MutationRunner(() => false, (job, token) => { runningDir = job.Directory; hold.Wait(); return ApplyOutcome.Finished(job, 1223, token.IsCancellationRequested, false, false); });
        var cancelModel = new MutationModel(context(archive));
        cancelModel.Report(new MagicEvent("fire", villageId, 10)); cancelModel.DailyBatch(8, owner, ruling);
        string cancelStarted = cancelModel.Pump(cancelling);
        bool runningExisted = Until(() => runningDir != null) && Directory.Exists(runningDir);
        cancelModel.Cancel(cancelling); hold.Set();
        bool runningGone = runningDir != null && Until(() => !Directory.Exists(runningDir));
        string finishedDir = null;
        var quick = new MutationRunner(() => false, (job, token) => { finishedDir = job.Directory; return new ApplyOutcome { Job = job, Succeeded = true, Delta = deltaText }; });
        var quickModel = new MutationModel(context(archive));
        quickModel.Report(new MagicEvent("fire", villageId, 10)); quickModel.DailyBatch(9, owner, ruling);
        string quickStarted = quickModel.Pump(quick);
        bool finishedExisted = Until(() => !quick.Busy) && finishedDir != null && Directory.Exists(finishedDir);
        quickModel.Cancel(quick);
        bool finishedGone = finishedDir != null && !Directory.Exists(finishedDir) && quick.Take() == null;
        Check(cancelStarted == null && runningExisted && runningGone && cancelling.Take() == null && !cancelling.Busy && cancelModel.State.Pending.Count == 1
              && quickStarted == null && finishedExisted && finishedGone && quickModel.State.Pending.Count == 1,
              "a cancelled fwg apply leaves no job folder: a running job's (" + Path.GetFileName(runningDir ?? "?") + ") is removed once its worker returns, a finished outcome's (" +
              Path.GetFileName(finishedDir ?? "?") + ") when the cancel drops it; their edits stay pending");

        // ---- a3: producer updates (RT-37) ----
        // The campaign's world was generated by build OLD; the module now pins NEW and keeps OLD in its archive.
        string oldSha = new string('a', 64), newSha = new string('b', 64);
        var updated = ProducerArchive.FromBytes(Utf8.GetBytes("{\"schema\":\"mnbl.producer-archive\",\"version\":1,\"rule\":\"test\",\"builds\":[" + Build(oldSha) + "," + Build(newSha) + "]}"));
        string oldPath = Path.Combine(scratch, "module", "Producers", oldSha, "fwg.exe"), newPath = Path.Combine(scratch, "module", "Producers", newSha, "fwg.exe");
        files.Clear(); files[oldPath] = oldSha; files[newPath] = newSha;
        var oldCtx = context(updated); oldCtx.ProducerSha256 = oldSha;
        var campaignModel = new MutationModel(oldCtx);
        campaignModel.Report(new MagicEvent("fire", villageId, 10)); campaignModel.DailyBatch(5, owner, ruling);
        string usedProducer = null;
        var updatedRunner = new MutationRunner(() => false, (job, token) => { usedProducer = job.Producer; return new ApplyOutcome { Job = job, Succeeded = true, Delta = deltaText }; });
        string castOld = campaignModel.Pump(updatedRunner);
        var o3 = Wait(updatedRunner);
        bool appliedOld = campaignModel.Applied(o3, (c, n) => { }) != null;
        string stored = campaignModel.Save();
        // After loading in a version that pins NEW: the document's recorded build (OLD) is what casts use.
        var newCtx = context(updated); newCtx.ProducerSha256 = newSha;
        var loadedNew = new MutationModel(newCtx);
        List<MutationCell> c3; List<MagicNode> n3;
        bool loads = loadedNew.Load(stored, out c3, out n3);
        string keepsCasting = loadedNew.CastRefusal();
        // OLD missing from the module: stored mutations still load; new casts are refused.
        files.Remove(oldPath);
        var missing = new MutationModel(newCtx);
        bool loadsMissing = missing.Load(stored, out c3, out n3);
        string refusalMissing = missing.CastRefusal(), reportMissing = missing.Report(new MagicEvent("fire", villageId, 10));
        // OLD not listed at all (an archive that dropped it): the same.
        var dropped2 = ProducerArchive.FromBytes(Utf8.GetBytes("{\"schema\":\"mnbl.producer-archive\",\"version\":1,\"rule\":\"test\",\"builds\":[" + Build(newSha) + "]}"));
        files[oldPath] = oldSha;
        var unlisted = new MutationModel(context(dropped2)); unlisted.World.ProducerSha256 = newSha;
        bool loadsUnlisted = unlisted.Load(stored, out c3, out n3);
        // A file whose bytes differ from its name is not the build.
        files[oldPath] = newSha;
        var tampered = new MutationModel(newCtx); tampered.Load(stored, out c3, out n3);
        Check(castOld == null && usedProducer == oldPath && appliedOld && loads && loadedNew.State.Producer == oldSha && keepsCasting == null && loadsMissing && c3 != null && c3.Count == applied.Count
              && refusalMissing != null && reportMissing != null && loadsUnlisted && unlisted.CastRefusal() != null && tampered.CastRefusal() != null && loadedNew.DisabledReason == null,
              "a3: a campaign keeps casting with the build that generated its world (" + oldSha.Substring(0, 6) + "..., archived) after the pin moves to " + newSha.Substring(0, 6) + "...; with that build missing (\"" + refusalMissing +
              "\"), unlisted or with other bytes, new casts are refused while the stored mutations still load (" + c3.Count + " cells)");

        // ---- r1: a digest mismatch (RT-37) ----
        files.Clear(); files[archivedPin] = pin;
        var cell0 = model.State.Cells[0];
        string forged = saved.Replace("[" + cell0.Row + "," + cell0.Column + "," + cell0.Variant + ",", "[" + cell0.Row + "," + cell0.Column + "," + (cell0.Variant == 0 ? 1 : 0) + ",");
        var mismatch = new MutationModel(context(archive));
        List<MutationCell> cm; List<MagicNode> nm;
        bool verified = mismatch.Load(forged, out cm, out nm);
        var otherWorld = new MutationModel(context(archive)); otherWorld.World.WorldSha256 = new string('c', 64);
        bool otherVerified = otherWorld.Load(saved, out cm, out nm);
        otherWorld.World.WorldSha256 = source.Sha256;
        var basePlan = MagicGround.Plan(new WorldSurfaceOverlay(src).Current, magic);
        var shown = new WorldSurfaceOverlay(src); shown.Replace(applied, model.State.Nodes);
        shown.Disable();
        var offPlan = MagicGround.Plan(shown.Current, magic);
        var castRunner = new MutationRunner(() => false, (job, token) => { throw new InvalidOperationException("a disabled campaign must not launch"); });
        Check(forged != saved && !verified && mismatch.DisabledReason != null && cm == null && mismatch.CastRefusal() != null && mismatch.Report(new MagicEvent("fire", villageId, 10)) != null
              && mismatch.Pump(castRunner) != null && !castRunner.Busy && mismatch.Save() == forged && !otherVerified && otherWorld.DisabledReason != null
              && shown.Current.LiveCount == 0 && shown.Current.RetypedCells.Length == 0 && shown.Current.MutationsDisabled && offPlan.Sha256() == basePlan.Sha256() && offPlan.Stamps.Count > 0 && Throws(() => shown.Apply(applied, null)),
              "r1: a document whose digest does not match (one cell's variant changed) or of another world disables the live cells and every cast (\"" + mismatch.DisabledReason + "\") and is saved back unchanged; the overlay keeps the " +
              offPlan.Stamps.Count + " generated magic ground decals (the same plan as the world without mutations) and retypes nothing");
        evidence["r1"] = new Dictionary<string, object> { ["reason"] = mismatch.DisabledReason, ["generatedDecals"] = offPlan.Stamps.Count };
        try { Directory.Delete(scratch, true); } catch (Exception) { }
    }

    private static string Build(string sha) { return "{\"sha256\":\"" + sha + "\",\"sourceCommit\":\"" + new string('0', 40) + "\",\"path\":\"Producers/" + sha + "/fwg.exe\",\"apply\":[\"apply\",\"--request\",\"{request}\",\"--operations\",\"{operations}\",\"--delta\",\"{delta}\"]}"; }

    private static ApplyOutcome Wait(MutationRunner runner)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 300) { var o = runner.Take(); if (o != null) return o; Thread.Sleep(20); }
        return null;
    }

    private static bool Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 10) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }

    // The test's launcher: the producer as a plain child process (the game's is GenerationProbe.RunProducerApply, an owned job).
    private static ApplyOutcome RunProcess(string exe, ApplyJob job, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe, String.Join(" ", job.Arguments.Select(a => "\"" + a + "\""))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = job.Directory };
        using (var p = Process.Start(start))
        {
            string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
            string delta = Path.Combine(job.Directory, "delta.json");
            return new ApplyOutcome { Job = job, ExitCode = p.ExitCode, Succeeded = p.ExitCode == 0 && File.Exists(delta), Delta = File.Exists(delta) ? File.ReadAllText(delta, Utf8) : null, Error = p.ExitCode == 0 ? null : err };
        }
    }

    private static string Shares(SortedDictionary<string, object> shares)
    {
        return String.Join(", ", shares.Where(kv => kv.Key != "landCells").Select(kv => kv.Key + " " + ((double)((Dictionary<string, object>)kv.Value)["share"]).ToString("0.0000", CultureInfo.InvariantCulture)));
    }
}
