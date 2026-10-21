using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;

// MMR-47 recovery tests, without the game:
//   RecoveryTests REPO BUNDLE_A OUT_JSON
// Every expected failure category (from the real classification paths) yields its own recovery message and action, and a retained
// diagnostic record under a unique correlation id with the job's records copied (and pruning keeps the newest); the data root is the
// per-user store and nothing else (not another folder, not the game or a Steam library, not through a junction); a fixture store's jobs are
// sorted into resumable / not resumable (another generator, no choice, committing) / finished, links and live jobs skipped; Discard removes
// only a job directory under the verified root; the cleanup removes abandoned staging and finished job directories but never a bundle,
// a package, an activation or a save's world; the inspector tells a valid bundle from a missing, an incompatible (versions, schema) and a
// corrupt one (a changed byte, an unsafe path, a link), reads save bindings from save headers, and relates saves, bundles, packages and
// activations in a store.
internal static class RecoveryTests
{
    private static int checks, failures;
    private static readonly List<string> lines = new List<string>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; string line = (condition ? "ok    " : "FAIL  ") + label; lines.Add(line); Console.WriteLine(line); }

    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: RecoveryTests REPO BUNDLE_A OUT_JSON"); return 2; }
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.recovery-tests", ["version"] = 1, ["ticket"] = "MMR-47" };
        string temp = Path.Combine(Path.GetTempPath(), "mnbl-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try { Run(args[0], args[1], temp, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        finally { try { RemoveJunctions(temp); Directory.Delete(temp, true); } catch (Exception) { } }
        evidence["checks"] = checks; evidence["failures"] = failures; evidence["lines"] = lines;
        File.WriteAllText(args[2], Json.Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string repo, string bundleA, string temp, SortedDictionary<string, object> evidence)
    {
        // ---- c1: the failure matrix ----
        var store = Path.Combine(temp, "store");
        Directory.CreateDirectory(store);
        var jobRoot = Path.Combine(store, "jobs", new string('c', 32));
        Directory.CreateDirectory(jobRoot);
        File.WriteAllText(Path.Combine(jobRoot, "timings.json"), "{\"phase\":\"Failed\"}");
        // As production leaves a failed job: its events end with the final one (appended after the record).
        File.WriteAllLines(Path.Combine(jobRoot, "events.jsonl"), Enumerable.Range(0, 200).Select(i => "{\"sequence\":" + i + ",\"phase\":\"GeneratingSource\"}").Concat(new[] { "{\"sequence\":200,\"phase\":\"Failed\"}" }));
        File.WriteAllText(Path.Combine(jobRoot, "generate-stderr.log"), new string('x', GenerationDiagnostics.MaxLogBytes + 500));
        var diskFull = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var matrix = new List<Tuple<string, GenerationError>> {
            Tuple.Create("missing War Sails", GenerationErrors.Host("v1.4.8.119303", "v1.4.8", null, "v1.2.8", false, null)[0]),
            Tuple.Create("missing generator", GenerationErrors.FromGeneration("reader_failed", new FileNotFoundException("missing", "C:\\\\x\\\\fwg.exe"), null, "fwg.exe missing")),
            Tuple.Create("game build below the minimum", GenerationErrors.Host("v1.4.7.1", "v1.4.8", "v1.2.8", "v1.2.8", true, null)[0]),
            Tuple.Create("incompatible bundle or schema", GenerationErrors.Job(GenerationErrorCategory.IncompatibleBuild, "bundle.schema", "manifest is not mnbl.world-bundle version 1")),
            Tuple.Create("refused recipe", GenerationErrors.FromGeneration("producer_refused", null, "INVALID_INPUT", "detail")),
            Tuple.Create("exhausted quality attempts", GenerationErrors.FromGeneration("quality_exhausted", null, null, "3 attempts")),
            Tuple.Create("disk full", GenerationErrors.FromGeneration("reader_failed", diskFull, null, "write failed")),
            Tuple.Create("bad source", GenerationErrors.FromGeneration("producer_exit_failure", null, null, "exit 3")),
            Tuple.Create("runtime capability failure", GenerationErrors.Job(GenerationErrorCategory.RuntimeCapabilityFailure, "runtime.navigation", "the navmesh did not load")),
            Tuple.Create("campaign start failed", GenerationErrors.Activation("PostBuild: closure")),
            Tuple.Create("interrupted generation", GenerationErrors.FromGeneration("cancelled", new OperationCanceledException(), null, "cancelled by the player")),
            Tuple.Create("disk full in the worldbridge", GenerationErrors.FromBridge(2, "error: There is not enough space on the disk.", "worldbridge --generate exit 2", null, null, null)),
            Tuple.Create("policies changed by an update", GenerationErrors.FromBridge(2, "policy_changed: Policy 'playability' is 1a2b, the request was accepted for 3c4d", "worldbridge --generate exit 2", null, null, null)),
            Tuple.Create("bundle of another schema", GenerationErrors.FromBundleProblems(new List<string> { "manifest is not mnbl.world-bundle version 1" })) };
        var tick = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        var diagnostics = new GenerationDiagnostics(store, () => tick);
        var rows = new List<object>();
        bool matrixOk = true;
        var categories = new HashSet<GenerationErrorCategory>();
        foreach (var m in matrix)
        {
            string id = diagnostics.Retain(m.Item2, "job-c", jobRoot, new Dictionary<string, object> { ["case"] = m.Item1 });
            var record = diagnostics.Read(id);
            string folder = diagnostics.Folder(id);
            categories.Add(m.Item2.Category);
            bool ok = id != null && id.StartsWith("MNBL-20261017-100000-", StringComparison.Ordinal) && record != null && (string)record["category"] == m.Item2.Category.ToString()
                && (string)record["detail"] == m.Item2.Detail && m.Item2.Message.StartsWith("{=mnbl_", StringComparison.Ordinal) && m.Item2.Title.StartsWith("{=mnbl_", StringComparison.Ordinal)
                && m.Item2.Action.StartsWith("{=mnbl_", StringComparison.Ordinal) && (m.Item2.Detail.Length == 0 || !m.Item2.Message.Contains(m.Item2.Detail))
                && File.Exists(Path.Combine(folder, "timings.json")) && File.ReadAllLines(Path.Combine(folder, "events.jsonl")).Length == GenerationDiagnostics.EventLines
                && new FileInfo(Path.Combine(folder, "generate-stderr.log")).Length < GenerationDiagnostics.MaxLogBytes + 200 && m.Item2.RetainProblem == null
                && diagnostics.Retain(m.Item2) == id;   // retaining again keeps the same reference
            matrixOk &= ok;
            rows.Add(new Dictionary<string, object> { ["case"] = m.Item1, ["category"] = m.Item2.Category.ToString(), ["code"] = m.Item2.Code, ["title"] = m.Item2.Title, ["action"] = m.Item2.Action, ["correlationId"] = id, ["ok"] = ok });
        }
        evidence["failureMatrix"] = rows;
        var ids = diagnostics.Ids();
        var actions = matrix.GroupBy(m => m.Item2.Category).Select(g => g.First().Item2.Action).ToList();
        Check(matrixOk && categories.Count == 8 && ids.Count == matrix.Count && ids.Distinct().Count() == ids.Count && actions.Distinct().Count() == 8,
              "every expected failure (" + matrix.Count + " cases over all 8 categories: missing tool or DLC, incompatible build or schema, refused recipe, exhausted quality attempts, disk full, bad source, runtime capability failure, interrupted generation) has its category's own message and action (8 distinct actions), never its detail, and a unique correlation id under which the detail and the job's records are kept (timings, the last " + GenerationDiagnostics.EventLines + " events, logs cut at " + GenerationDiagnostics.MaxLogBytes + " bytes)");
        // The worldbridge's failures and the bundle verifier's refusals, classified as the stages classify them.
        var bridge = new Dictionary<string, GenerationErrorCategory> {
            ["exit 2 disk full"] = GenerationErrors.FromBridge(2, "There is not enough space on the disk", "d", null, null, null).Category,
            ["exit 3 disk full"] = GenerationErrors.FromBridge(3, "fs error 112", "d", "failed", "mechanical", "crashed").Category,
            ["policy_changed"] = GenerationErrors.FromBridge(2, "policy_changed: x", "d", null, null, null).Category,
            ["state_request_mismatch"] = GenerationErrors.FromBridge(2, "state_request_mismatch: x", "d", null, null, null).Category,
            ["producer_changed"] = GenerationErrors.FromBridge(2, "producer_changed: x", "d", null, null, null).Category,
            ["refused"] = GenerationErrors.FromBridge(2, "invalid_input: x", "d", null, null, null).Category,
            ["exhausted"] = GenerationErrors.FromBridge(3, "", "d", "exhausted", "quality", "succeeded").Category,
            ["engine"] = GenerationErrors.FromBridge(3, "", "d", "failed", "engine", "crashed").Category,
            ["cancelled"] = GenerationErrors.FromBridge(4, "", "d", null, null, null).Category,
            ["bundle versions"] = GenerationErrors.FromBundleProblems(new List<string> { "built for Bannerlord v1.4.8 or later, installed v1.4.7.1" }).Category,
            ["bundle capability"] = GenerationErrors.FromBundleProblems(new List<string> { "requires runtime capability R11 (x), which this host does not provide" }).Category,
            ["bundle hash"] = GenerationErrors.FromBundleProblems(new List<string> { "hash differs: source/world.json" }).Category };
        evidence["bridgeClassification"] = bridge.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToString());
        Check(bridge["exit 2 disk full"] == GenerationErrorCategory.DiskFull && bridge["exit 3 disk full"] == GenerationErrorCategory.DiskFull && bridge["policy_changed"] == GenerationErrorCategory.IncompatibleBuild
              && bridge["state_request_mismatch"] == GenerationErrorCategory.IncompatibleBuild && bridge["producer_changed"] == GenerationErrorCategory.MissingToolOrDlc && bridge["refused"] == GenerationErrorCategory.RefusedRecipe
              && bridge["exhausted"] == GenerationErrorCategory.ExhaustedQualityAttempts && bridge["engine"] == GenerationErrorCategory.RuntimeCapabilityFailure && bridge["cancelled"] == GenerationErrorCategory.InterruptedGeneration
              && bridge["bundle versions"] == GenerationErrorCategory.IncompatibleBuild && bridge["bundle capability"] == GenerationErrorCategory.IncompatibleBuild && bridge["bundle hash"] == GenerationErrorCategory.BadSource,
              "the worldbridge's failures reach the player in their own category: a full disk (whatever the exit), changed policies or attempt state (an update: incompatible build, never 'try another seed'), a changed producer, a refusal, exhausted attempts, an engine failure, a cancel; a bundle built for other versions, capabilities or schema is an incompatible build, a damaged one a bad source");

        // Pruning: the newest Keep records stay.
        var many = new GenerationDiagnostics(Path.Combine(temp, "prune"), () => tick = tick.AddSeconds(1));
        var first = new List<string>();
        for (int i = 0; i < GenerationDiagnostics.Keep + 5; i++) first.Add(many.Retain(GenerationErrors.Busy("n" + i)));
        var kept = many.Ids();
        // An old record that cannot be removed (a file held open) never fails a new record; it is retried later.
        string oldest = kept[0];
        GenerationError held;
        using (new FileStream(Path.Combine(many.Folder(oldest), "error.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            many.Retain(held = GenerationErrors.Busy("while one record is held"));
        bool heldOk = held.RetainProblem == null && many.Read(held.CorrelationId) != null && many.PruneFailures > 0;
        // An unverified root: the reference is given, nothing is written; retained again under a verified root, the problem clears.
        var unverifiedDiagnostics = new GenerationDiagnostics(Path.Combine(temp, "unverified"), () => tick, () => "not the per-user store");
        var orphan = GenerationErrors.Busy("x");
        string orphanId = unverifiedDiagnostics.Retain(orphan);
        bool notWritten = orphanId != null && orphan.RetainProblem != null && !Directory.Exists(unverifiedDiagnostics.Root);
        var verified = new GenerationDiagnostics(Path.Combine(temp, "verified"), () => tick);
        verified.Retain(orphan);
        bool cleared = orphan.RetainProblem == null && verified.Read(orphanId) != null;
        Check(kept.Count == GenerationDiagnostics.Keep && kept.SequenceEqual(first.Skip(5)) && heldOk && notWritten && cleared,
              "the diagnostics keep the newest " + GenerationDiagnostics.Keep + " records; a record that cannot be removed is skipped without failing the new one; under an unverified root a reference is given but nothing is written, and retaining it later under a verified root writes it and clears the problem");

        // ---- c2: the data root ----
        string game = Path.Combine(temp, "Mount & Blade II Bannerlord"); Directory.CreateDirectory(game);
        string inGame = Path.Combine(game, "Modules", "x"); Directory.CreateDirectory(inGame);
        string steam = Path.Combine(temp, "steamapps", "common", "store"); Directory.CreateDirectory(steam);
        string linkParent = Path.Combine(temp, "linked"); string linkTarget = Path.Combine(temp, "target"); Directory.CreateDirectory(linkTarget);
        bool junction = Junction(linkParent, linkTarget);
        string throughLink = Path.Combine(linkParent, "store");
        Directory.CreateDirectory(Path.Combine(linkTarget, "store"));
        var forbidden = new[] { game };
        string okRoot = WorldRecoveryService.VerifyRoot(store, store, forbidden), otherRoot = WorldRecoveryService.VerifyRoot(Path.Combine(temp, "elsewhere"), store, forbidden);
        string gameRoot = WorldRecoveryService.VerifyRoot(inGame, inGame, forbidden), steamRoot = WorldRecoveryService.VerifyRoot(steam, steam, forbidden);
        string linkRoot = WorldRecoveryService.VerifyRoot(throughLink, throughLink, forbidden);
        evidence["dataRoot"] = new Dictionary<string, object> { ["store"] = okRoot ?? "verified", ["other"] = otherRoot, ["insideGame"] = gameRoot, ["steam"] = steamRoot, ["throughJunction"] = linkRoot, ["junctionMade"] = junction };
        Check(okRoot == null && otherRoot != null && gameRoot != null && steamRoot != null && (!junction || linkRoot != null) && junction,
              "the data root verifies only as the per-user store itself: another folder, a folder inside the game installation, a Steam library and a path through a junction are refused (" + (linkRoot ?? "?") + ")");

        // ---- c2: unfinished jobs ----
        string lockNow = new string('1', 64), lockOld = new string('2', 64);
        string jobs = Path.Combine(store, "jobs");
        Func<string, string> job = n => { string d = Path.Combine(jobs, n.PadRight(32, n[0])); Directory.CreateDirectory(d); return d; };
        Action<string, string, uint> choice = (d, lk, seed) => File.WriteAllText(Path.Combine(d, "choice.json"), Json.Serialize(new Dictionary<string, object> {
            ["seed"] = seed, ["seedSource"] = "explicit", ["campaignInstanceId"] = Guid.NewGuid().ToString("D"), ["recipeVersion"] = 3, ["maxAttempts"] = 3, ["producerLockSha256"] = lk, ["profile"] = "one-shot",
            ["overrides"] = new Dictionary<string, object> { ["size"] = 129, ["phase"] = 16 } }));
        string running = job("d"); choice(running, lockNow, 42); File.WriteAllText(Path.Combine(running, "events.jsonl"), "{\"phase\":\"ValidatingRequest\"}\n{\"phase\":\"GeneratingSource\"}\n");
        string review = job("e"); choice(review, lockNow, 43); File.WriteAllText(Path.Combine(review, "timings.json"), "{\"phase\":\"ReadyForReview\"}");
        string finished = jobRoot;   // Failed (above)
        string other = job("f"); choice(other, lockOld, 44);
        string noChoice = job("0");
        // As production leaves it: the record written at review, the commit only in the events.
        string committing = job("1"); choice(committing, lockNow, 45); File.WriteAllText(Path.Combine(committing, "timings.json"), "{\"phase\":\"ReadyForReview\"}");
        File.WriteAllText(Path.Combine(committing, "events.jsonl"), "{\"phase\":\"ReadyForReview\"}\n{\"phase\":\"Committing\"}\n");
        string empty = job("4"); File.WriteAllText(Path.Combine(empty, "choice.json"), ""); File.WriteAllText(Path.Combine(empty, "timings.json"), ""); File.WriteAllText(Path.Combine(empty, "events.jsonl"), "{\"phase\":\"Compiling\"}\n");
        string torn = job("5"); File.WriteAllText(Path.Combine(torn, "choice.json"), "{\"seed\":4"); File.WriteAllText(Path.Combine(torn, "events.jsonl"), "{\"phase\":\"GeneratingSource\"}\n");
        File.WriteAllText(Path.Combine(torn, "request.json"), Json.Serialize(new Dictionary<string, object> { ["masterSeed"] = 48, ["seedSource"] = "random", ["campaignInstanceId"] = Guid.NewGuid().ToString("D"),
            ["recipeVersion"] = 3, ["maxAttempts"] = 3, ["producerLockSha256"] = lockNow, ["overrides"] = new Dictionary<string, object>(), ["policies"] = new Dictionary<string, object> { ["playability"] = "aa" } }));
        string otherPolicies = job("6"); choice(otherPolicies, lockNow, 49); File.WriteAllText(Path.Combine(otherPolicies, "events.jsonl"), "{\"phase\":\"GeneratingSource\"}\n");
        File.WriteAllText(Path.Combine(otherPolicies, "request.json"), Json.Serialize(new Dictionary<string, object> { ["masterSeed"] = 49, ["campaignInstanceId"] = Guid.NewGuid().ToString("D"), ["producerLockSha256"] = lockNow,
            ["policies"] = new Dictionary<string, object> { ["playability"] = "bb" } }));
        string unreadable = job("7"); choice(unreadable, lockNow, 50); File.WriteAllText(Path.Combine(unreadable, "events.jsonl"), "{\"phase\":\"Compiling\"}\n");
        string live = job("2"); choice(live, lockNow, 46);
        string linkedJob = Path.Combine(jobs, new string('3', 32)); bool linkedMade = Junction(linkedJob, linkTarget);
        Directory.CreateDirectory(Path.Combine(jobs, "not-a-job"));
        string committingInstance = null;
        { var c = (IDictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(committing, "choice.json"))); committingInstance = Guid.Parse((string)c["campaignInstanceId"]).ToString("N"); }
        File.SetLastWriteTimeUtc(Path.Combine(running, "events.jsonl"), DateTime.UtcNow.AddMinutes(5));
        Func<IDictionary<string, string>, string> policyMismatch = p => p.Where(kv => kv.Value != "aa").Select(kv => kv.Key).FirstOrDefault();
        List<WorldRecoveryService.UnfinishedJob> found = null; Exception listing = null;
        // One job's events held open by another process while listing: that job cannot be read.
        using (new FileStream(Path.Combine(unreadable, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            try { found = WorldRecoveryService.Jobs(jobs, p => p == live, lockNow, instance => instance == committingInstance, policyMismatch); } catch (Exception e) { listing = e; found = new List<WorldRecoveryService.UnfinishedJob>(); }
        Func<string, WorldRecoveryService.UnfinishedJob> byRoot = r => found.FirstOrDefault(j => j.Root == r);
        evidence["unfinishedJobs"] = found.Select(j => j.Report()).ToList();
        Check(listing == null && found.Count == 10 && found[0].Root == running && byRoot(running).Resumable && byRoot(running).Phase == "GeneratingSource" && byRoot(running).Seed == 42
              && byRoot(review).Resumable && byRoot(review).Phase == "ReadyForReview" && byRoot(finished).Finished && !byRoot(finished).Resumable
              && !byRoot(other).Resumable && byRoot(other).Reason.Contains("another version") && !byRoot(noChoice).Resumable && !byRoot(noChoice).HasChoice && byRoot(noChoice).Finished && byRoot(noChoice).Phase == "Leftover"
              && !byRoot(committing).Resumable && byRoot(committing).Kind == WorldRecoveryService.Committing && byRoot(committing).Phase == "Committing" && byRoot(committing).Reason.Contains("activation")
              && byRoot(empty).Kind == WorldRecoveryService.Unrecorded && byRoot(empty).Phase == "Compiling"
              && byRoot(torn).Resumable && byRoot(torn).Seed == 48 && byRoot(torn).SeedSource == "random"
              && byRoot(otherPolicies).Kind == WorldRecoveryService.OtherPolicies && !byRoot(otherPolicies).Resumable
              && byRoot(unreadable).Kind == WorldRecoveryService.Unreadable && !byRoot(unreadable).Finished
              && byRoot(live) == null && byRoot(linkedJob) == null && linkedMade,
              "a stopped process's jobs are found newest first with their latest phase (the last event, else the record: a job stopped while committing is caught though its record says review) and choice (choice.json, else its request when the choice is empty or torn): resumable while generating or awaiting review; not resumable when made by another generator or under other policies, stopped while committing (left to the activation's recovery), or with nothing readable (one unreadable job never stops the listing); a finished one, and a directory with nothing recorded (an earlier version's), are marked for cleanup; this process's live job, a link and a non-job folder are skipped");

        // Discard.
        Directory.CreateDirectory(Path.Combine(store, "bundles", new string('a', 64)));
        File.WriteAllText(Path.Combine(store, "bundles", new string('a', 64), "manifest.json"), "{}");
        string discardOther = WorldRecoveryService.Discard(store, other, p => p == live, null);
        string discardLive = WorldRecoveryService.Discard(store, live, p => p == live, null);
        string discardOutside = WorldRecoveryService.Discard(store, Path.Combine(store, "bundles", new string('a', 64)), null, null);
        string discardLink = WorldRecoveryService.Discard(store, linkedJob, null, null);
        string discardUnverified = WorldRecoveryService.Discard(store, noChoice, null, "not the per-user store");
        evidence["discard"] = new Dictionary<string, object> { ["other"] = discardOther ?? "removed", ["live"] = discardLive, ["outside"] = discardOutside, ["link"] = discardLink, ["unverified"] = discardUnverified };
        Check(discardOther == null && !Directory.Exists(other) && discardLive != null && Directory.Exists(live) && discardOutside != null && Directory.Exists(Path.Combine(store, "bundles", new string('a', 64)))
              && discardLink != null && Directory.Exists(linkTarget) && discardUnverified != null && Directory.Exists(noChoice),
              "Discard removes one job directory under the verified root's jobs/ and nothing else; it refuses this process's live job, any path outside jobs/ (a bundle), a link (its target untouched) and an unverified root");

        // ---- c2: cleanup ----
        string bundles = Path.Combine(store, "bundles"), worlds = Path.Combine(store, "worlds"), activations = Path.Combine(store, "activations");
        Directory.CreateDirectory(Path.Combine(bundles, ".staging-123"));
        Directory.CreateDirectory(Path.Combine(worlds, "mnbl_x-000000000000"));
        Directory.CreateDirectory(Path.Combine(worlds, "mnbl_y" + DurableFile.TemporaryMarker + "9"));
        var journalStore = new ActivationJournalStore(store);
        string activeInstance = Guid.NewGuid().ToString("N");
        journalStore.Begin(new ActivationRequest { Instance = activeInstance, WorldContentId = new string('a', 64), ManifestSha256 = new string('b', 64) });
        Directory.CreateDirectory(Path.Combine(store, "saves", "journal"));
        File.WriteAllText(Path.Combine(store, "saves", "journal", "x.json" + DurableFile.TemporaryMarker + "1"), "{}");
        var unverified = WorldRecoveryService.Cleanup(store, "not the per-user store", true, null, lockNow);
        bool untouched = Directory.Exists(Path.Combine(bundles, ".staging-123")) && Directory.Exists(finished);
        var cleanup = WorldRecoveryService.Cleanup(store, null, true, p => p == live, lockNow);
        var removedJobs = (List<string>)cleanup["finishedJobsRemoved"];
        var staging = (Dictionary<string, object>)cleanup["staging"];
        evidence["cleanup"] = cleanup;
        Check(untouched && unverified.ContainsKey("skipped") && !Directory.Exists(Path.Combine(bundles, ".staging-123")) && !Directory.Exists(Path.Combine(worlds, "mnbl_y" + DurableFile.TemporaryMarker + "9"))
              && Directory.Exists(Path.Combine(bundles, new string('a', 64))) && Directory.Exists(Path.Combine(worlds, "mnbl_x-000000000000")) && File.Exists(journalStore.JournalPath(activeInstance))
              && new HashSet<string>(removedJobs).SetEquals(new[] { Path.GetFileName(finished), Path.GetFileName(noChoice) }) && !Directory.Exists(finished) && !Directory.Exists(noChoice) && Directory.Exists(unreadable) && Directory.Exists(empty) && Directory.Exists(running) && Directory.Exists(review) && Directory.Exists(committing) && Directory.Exists(live)
              && ((List<string>)staging["removed"]).Count == 3,
              "the cleanup under an unverified root removes nothing; under the verified root it removes the abandoned staging (bundle staging, a package and a journal temporary) and the finished and nothing-recorded job directories, and keeps every bundle, package, activation, unfinished job and live job");

        // ---- c3: inspection ----
        var host = new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", WorldDataBundle.HostCapabilities);
        var otherHost = new WorldDataBundle.HostProfile("v1.4.7.1", "v1.2.8.119303", host.Capabilities);   // MMR-284: below the minimum (a newer host is valid)
        var valid = WorldInspector.Bundle(bundleA, host);
        var missing = WorldInspector.Bundle(Path.Combine(temp, "no-such-bundle"), host);
        var incompatible = WorldInspector.Bundle(bundleA, otherHost);
        string contentA = (string)valid["worldContentId"];
        // A copy to damage.
        string copy = Path.Combine(store, "bundles", contentA);
        CopyTree(bundleA, copy);
        var copiedValid = WorldInspector.Bundle(copy, host);
        string victim = Path.Combine(copy, "compiled", "validation.json");
        var original = File.ReadAllBytes(victim); var damaged = (byte[])original.Clone(); damaged[damaged.Length / 2] ^= 0x20; File.WriteAllBytes(victim, damaged);
        var changedByte = WorldInspector.Bundle(copy, host);
        File.WriteAllBytes(victim, original);
        string manifestPath = Path.Combine(copy, "manifest.json"); string manifestText = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifestText.Replace("\"version\": 1", "\"version\": 2").Replace("\"version\":1", "\"version\":2"));
        var schema = WorldInspector.Bundle(copy, host);
        // An inventory row naming a path outside the bundle.
        var parsed = (Dictionary<string, object>)Json.DeserializeObject(manifestText);
        var inventory = ((object[])parsed["inventory"]).ToList();
        inventory.Insert(0, new Dictionary<string, object> { ["bytes"] = 1, ["path"] = "../escape.json", ["sha256"] = new string('0', 64) });
        parsed["inventory"] = inventory;
        File.WriteAllText(manifestPath, Json.Serialize(parsed));
        var unsafePath = WorldInspector.Bundle(copy, host);
        File.WriteAllText(manifestPath, manifestText);
        bool linkMade = Junction(Path.Combine(copy, "compiled", "linked"), linkTarget);
        var linked = WorldInspector.Bundle(copy, host);
        if (linkMade) Directory.Delete(Path.Combine(copy, "compiled", "linked"), false);
        var restored = WorldInspector.Bundle(copy, host);
        Func<Dictionary<string, object>, string> status = r => (string)r["status"];
        Func<Dictionary<string, object>, string> problems = r => String.Join("; ", ((IEnumerable)r["problems"]).Cast<object>().Take(2));
        evidence["bundles"] = new Dictionary<string, object> { ["valid"] = status(valid), ["missing"] = status(missing) + ": " + problems(missing), ["incompatible"] = status(incompatible) + ": " + problems(incompatible),
            ["changedByte"] = status(changedByte) + ": " + problems(changedByte), ["schema"] = status(schema) + ": " + problems(schema), ["unsafePath"] = status(unsafePath) + ": " + problems(unsafePath),
            ["link"] = status(linked) + ": " + problems(linked), ["restored"] = status(restored), ["gameVersion"] = valid["gameVersion"], ["navalVersion"] = valid["navalVersion"] };
        Check(status(valid) == WorldInspector.Valid && valid["gameVersion"] as string == "v1.4.8.119303" && status(missing) == WorldInspector.Missing && status(incompatible) == WorldInspector.Incompatible
              && problems(incompatible).StartsWith("built for Bannerlord v1.4.8 or later, installed v1.4.7.1", StringComparison.Ordinal) && status(copiedValid) == WorldInspector.Valid
              && status(changedByte) == WorldInspector.Corrupt && problems(changedByte).Contains("hash differs") && status(schema) == WorldInspector.Incompatible
              && status(unsafePath) == WorldInspector.Corrupt && ((IEnumerable)unsafePath["problems"]).Cast<string>().Any(p => p.Contains("unsafe inventory path ../escape.json"))
              && linkMade && status(linked) == WorldInspector.Corrupt && ((IEnumerable)linked["problems"]).Cast<string>().Any(p => p.Contains("linked")) && status(restored) == WorldInspector.Valid,
              "bundle inspection without the game tells a valid bundle (with its versions) from a missing one, an incompatible one (a host below the minimum Bannerlord; another manifest schema version) and a corrupt one (one changed byte, an unsafe '../' inventory path, a junction inside), and the repaired copy is valid again");

        // Saves: bindings from the headers; the store's relationships.
        string saves = Path.Combine(temp, "Game Saves"); Directory.CreateDirectory(saves);
        // MMR-50: a package that verifies (world.json hashes to the binding's manifest hash), as a save is loadable only with its world whole.
        var packageManifest = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["schema"] = WorldSaveTransfer.PackageSchema, ["worldId"] = "mnbl_1609fb72", ["seed"] = 42,
            ["files"] = new Dictionary<string, object>(), ["sceneName"] = "mnbl_world_x", ["sceneFiles"] = new Dictionary<string, object>(), ["documents"] = new List<object>() }));
        string packageSha = WorldSaveTransfer.Sha256Bytes(packageManifest);
        string package = "mnbl_1609fb72-" + packageSha.Substring(0, 12);
        // A committed activation of bundle A, its initial save the bound one.
        journalStore.Abandon(journalStore.Journal(activeInstance), "test: one activation at a time");
        string committedInstance = Guid.NewGuid().ToString("N");
        var committedJournal = journalStore.Begin(new ActivationRequest { Instance = committedInstance, WorldContentId = contentA, ManifestSha256 = "0123456789ab" + new string('0', 52) });
        journalStore.Commit(committedJournal, "test");
        Directory.CreateDirectory(Path.Combine(worlds, package)); File.WriteAllBytes(Path.Combine(worlds, package, "world.json"), packageManifest);
        Save(Path.Combine(saves, "bound.sav"), new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_1609fb72", ["mnbl.world.manifestSha256"] = packageSha,
            ["mnbl.world.instance"] = committedInstance, ["mnbl.world.contentId"] = contentA, ["mnbl.world.scene"] = "mnbl_world_x", ["mnbl.world.seed"] = "42", ["UniqueGameId"] = "a", ["ApplicationVersion"] = "v1.4.8.119303" });
        Save(Path.Combine(saves, "orphan.sav"), new Dictionary<string, string> { ["mnbl.world.id"] = "mnbl_deadbeef", ["mnbl.world.manifestSha256"] = new string('9', 64),
            ["mnbl.world.instance"] = Guid.NewGuid().ToString("N"), ["mnbl.world.contentId"] = new string('d', 64), ["mnbl.world.scene"] = "mnbl_world_y", ["UniqueGameId"] = "b", ["ApplicationVersion"] = "v1.4.8.119303" });
        Save(Path.Combine(saves, "vanilla.sav"), new Dictionary<string, string> { ["UniqueGameId"] = "c" });
        File.WriteAllBytes(Path.Combine(saves, "torn.sav"), new byte[] { 1, 2 });
        var report = WorldInspector.Store(store, saves, host, lockNow, null);
        var saveRows = ((List<Dictionary<string, object>>)report["saves"]).ToDictionary(r => (string)r["name"]);
        var bundleRow = ((List<Dictionary<string, object>>)report["bundles"]).FirstOrDefault(b => Equals(b["directory"], contentA));
        var activationRow = ((List<Dictionary<string, object>>)report["activations"]).FirstOrDefault(a => Equals(a["instance"], committedInstance));
        evidence["store"] = new Dictionary<string, object> { ["summary"] = report["summary"], ["saves"] = report["saves"], ["jobs"] = ((IList)report["jobs"]).Count, ["staging"] = report["staging"] };
        Check(saveRows.ContainsKey("bound") && Equals(saveRows["bound"]["loadable"], true) && Equals(saveRows["bound"]["bundle"], WorldInspector.Valid) && Equals(saveRows["bound"]["packagePresent"], true)
              && saveRows.ContainsKey("orphan") && Equals(saveRows["orphan"]["loadable"], false) && Equals(saveRows["orphan"]["bundle"], WorldInspector.Missing) && !saveRows.ContainsKey("vanilla")
              && saveRows.ContainsKey("torn") && saveRows["torn"].ContainsKey("error") && bundleRow != null && ((List<string>)bundleRow["referencedBySaves"]).SequenceEqual(new[] { "bound" })
              && activationRow != null && Equals(activationRow["state"], ActivationJournalStore.Committed) && Equals(activationRow["bundle"], WorldInspector.Valid) && ((IList)bundleRow["activations"]).Count == 1,
              "store inspection reads each save's world binding from its header (no game): a save whose package and bundle are present is loadable, one whose bundle is missing is not, a save of no generated world is left out and a torn header is reported; each bundle lists the saves and activations that reference it");
        // The same bundle damaged: the save and the activation name it corrupt (not missing), and the save is not loadable.
        var victim2 = Path.Combine(copy, "compiled", "validation.json"); var bytes2 = File.ReadAllBytes(victim2); bytes2[bytes2.Length / 3] ^= 0x10; File.WriteAllBytes(victim2, bytes2);
        var damagedReport = WorldInspector.Store(store, saves, host, lockNow, null);
        var damagedSave = ((List<Dictionary<string, object>>)damagedReport["saves"]).First(r => Equals(r["name"], "bound"));
        var damagedActivation = ((List<Dictionary<string, object>>)damagedReport["activations"]).First(a => Equals(a["instance"], committedInstance));
        var damagedBundle = ((List<Dictionary<string, object>>)damagedReport["bundles"]).First(b => Equals(b["directory"], contentA));
        Check(Equals(damagedSave["bundle"], WorldInspector.Corrupt) && Equals(damagedSave["loadable"], false) && Equals(damagedActivation["bundle"], WorldInspector.Corrupt)
              && Equals(damagedBundle["status"], WorldInspector.Corrupt) && ((List<string>)damagedBundle["referencedBySaves"]).SequenceEqual(new[] { "bound" }),
              "a present but damaged bundle is reported corrupt on the saves and activations that use it (not missing), its save is not loadable, and the bundle still lists the save that references it");
    }

    // The binding store's save header type (compiled in with the activation journal; the inspector reads headers itself).
    internal static SaveHeader Header(string name) { return new SaveHeader { Name = name, Metadata = null, BodyWhole = null, BodyProblem = null }; }

    // A save in the installed format: Int32 header length, JSON {"List": metadata}, then deflated game data whole for SaveFileCheck (MMR-50:
    // the inspector's verdict is the compatibility policy's, which reads the game data too).
    private static void Save(string path, Dictionary<string, string> metadata)
    {
        var json = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["List"] = metadata }));
        var random = new Random(json.Length);
        Func<int, byte[]> bytes = n => { var b = new byte[n]; random.NextBytes(b); return b; };
        using (var file = File.Create(path))
        {
            file.Write(BitConverter.GetBytes(json.Length), 0, 4); file.Write(json, 0, json.Length);
            using (var deflate = new DeflateStream(file, CompressionLevel.Fastest, true))
            using (var writer = new BinaryWriter(deflate))
            {
                var h = bytes(200); writer.Write(h.Length); writer.Write(h);
                writer.Write(2); for (int i = 0; i < 2; i++) { var o = bytes(300); writer.Write(o.Length); writer.Write(o); }
                writer.Write(1); { var c = bytes(100); writer.Write(c.Length); writer.Write(c); }
                var s = bytes(500); writer.Write(s.Length); writer.Write(s);
            }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(to + dir.Substring(from.Length));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, to + file.Substring(from.Length));
    }

    private static bool Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
    }

    // Junctions are removed as links before the temporary tree goes (so no target is followed).
    private static void RemoveJunctions(string root)
    {
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(dir, false); continue; }
            RemoveJunctions(dir);
        }
    }
}
