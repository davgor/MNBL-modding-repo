using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;

// MMR-43 setup screen tests, without the game:
//   WorldSetupTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON
// The setup contract from the repository's documents (and its refusals), exact seed parsing, Random World, the fixed settings and their
// reasons, the measured waiting time, the host and generation error categories, the player's request fields against the generation request
// schema, the setup state machine over a scripted generation job (generate, cancel, fail, back, edits locked while generating, job files
// released), the streaming world.json reader against a whole-document reading of two real generated worlds, their peoples' cultures
// against the cultures those worlds' compiled campaigns present through, the key mapping, localisation ids, and every binding and command
// of GUI/Prefabs/WorldSetup.xml against the view models.
// MMR-46: the review flow over the same scripted job: the preview of a real compiled world, Back keeping the world (the same seed shows it
// again without a second job), Start Campaign handing the job over (withdrawn, failed, committed), New Seed, a world without a preview, the
// keys per state, the preview view model (markers, regions, labels, the pointed-at place) and GUI/Prefabs/WorldPreview.xml's bindings.
internal static class WorldSetupTests
{
    private static int checks, failures;
    private static void Check(bool condition, string label) { checks++; if (!condition) failures++; Console.WriteLine((condition ? "ok    " : "FAIL  ") + label); }
    private static string gameRoot;
    private static readonly Dictionary<string, string> DocumentPaths = new Dictionary<string, string> {
        ["one-shot.json"] = "worldgen/profiles/one-shot.json", ["producer-controls.json"] = "worldgen/contracts/producer-controls.json",
        ["producer-seed-width.json"] = "worldgen/contracts/producer-seed-width.json", ["producer.lock.json"] = "worldgen/producer.lock.json",
        ["product-policy.json"] = "worldgen/policies/product-policy.json", ["culture-presentations.json"] = "worldgen/policies/culture-presentations.json",
        ["one-shot-budget.json"] = "worldgen/policies/one-shot-budget.json" };

    private static int Main(string[] args)
    {
        if (args.Length != 5) { Console.Error.WriteLine("usage: WorldSetupTests REPO BUNDLE_A BUNDLE_B GAME_ROOT OUT_JSON"); return 2; }
        gameRoot = args[3];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { "bin/Win64_Shipping_Client", "Modules/Native/bin/Win64_Shipping_Client" })
            {
                string path = Path.Combine(gameRoot, dir, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var evidence = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["schema"] = "mnbl.evidence.world-setup", ["version"] = 3, ["ticket"] = "MMR-43, MMR-46" };
        try { Run(args, evidence); }
        catch (Exception e) { Check(false, "unexpected exception: " + e); }
        evidence["checks"] = checks; evidence["failures"] = failures;
        File.WriteAllText(args[4], new JavaScriptSerializer().Serialize(evidence), new UTF8Encoding(false));
        Console.WriteLine((checks - failures) + "/" + checks + " checks passed");
        return failures == 0 ? 0 : 1;
    }

    // A test edit of a setup document; an edit that changes nothing is a broken test, not a refusal.
    private static Dictionary<string, byte[]> Edit(Dictionary<string, byte[]> docs, string name, string pattern, string replacement)
    {
        string text = new UTF8Encoding(false).GetString(docs[name]), edited = Regex.Replace(text, pattern, replacement);
        if (edited == text) throw new InvalidOperationException("test edit matched nothing: " + name + " " + pattern);
        var copy = new Dictionary<string, byte[]>(docs); copy[name] = new UTF8Encoding(false).GetBytes(edited);
        return copy;
    }
    private static string Sha(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }
    private static IDictionary<string, object> Json(string path) { return (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(path)); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args, SortedDictionary<string, object> evidence)
    {
        string repo = args[0];
        string[] bundles = { args[1], args[2] };
        var docs = DocumentPaths.ToDictionary(kv => kv.Key, kv => File.ReadAllBytes(Path.Combine(repo, kv.Value)));
        var contract = WorldSetupContract.FromDocuments(docs);
        evidence["contract"] = new Dictionary<string, object> { ["overrides"] = contract.Overrides, ["maxSeed"] = contract.MaxSeed, ["qualityAttempts"] = contract.QualityAttempts, ["sources"] = contract.Sources,
            ["producerLockSha256"] = contract.ProducerLockSha256, ["presentations"] = contract.Presentations };
        Check(contract.Overrides.Count == 2 && contract.Overrides["size"] == 129 && contract.Overrides["phase"] == 16 && contract.RecipeVersion == 3 && contract.MaxSeed == 4294967295UL && contract.DefaultSeed == 42
              && contract.QualityAttempts == 3 && contract.MaxQualityAttempts == 3 && contract.Coverage == "full-globe" && contract.WarSailsRequired && contract.Controls["size"].Min == 3 && contract.Controls["size"].Max == 1025
              && contract.ProducerLockSha256 == Sha(docs["producer.lock.json"]) && contract.ProducerLockSha256 != contract.ProfileToolsLockSha256 && contract.OfficialCultures.Count == 7,
              "the setup contract is the F03 one-shot profile (recipe 3, size 129, phase 16, seed 42), the producer's verified seed width 4294967295, its published size 3..1025 and phase 1..16, the product policy's 3 attempts and full-globe coverage, the request's producer lock (sha256 of worldgen/producer.lock.json, not the profile's tools lock) and the 7 official presentation cultures");
        Func<Action, bool> refused = a => { try { a(); return false; } catch (InvalidDataException) { return true; } };
        Check(refused(() => WorldSetupContract.FromDocuments(Edit(docs, "one-shot.json", "\"phase\":\\s*16", "\"phase\": 16, \"no_such_control\": 3")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "one-shot.json", "\"size\":\\s*129", "\"size\": 2")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "one-shot.json", "\"qualityAttempts\":\\s*3", "\"qualityAttempts\": 4")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "producer-seed-width.json", "\"verified\":\\s*true", "\"verified\": false")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "producer-seed-width.json", "\"maxSeed\":\\s*4294967295", "\"maxSeed\": 4294967296")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "producer.lock.json", "564c2629b5b92d49", "000c2629b5b92d49")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "product-policy.json", "\"coverage\":\\s*\"full-globe\"", "\"coverage\": \"region\"")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "culture-presentations.json", "\"officialCultures\":\\s*\\[", "\"officialCultures\": [\"rhodok\", ")))
              && refused(() => WorldSetupContract.FromDocuments(Edit(docs, "culture-presentations.json", "\"culture\":\\s*\"aserai\"", "\"culture\": \"rhodok\""))),
              "refused: a control the pinned producer does not publish, a value outside its range, more attempts than the product policy, an unverified or wider-than-32-bit seed width, a seed width verified for another producer than the locked one, coverage other than the policy's, an official culture without a name, a civilization shown through a non-official culture");

        // Seeds.
        var table = new[] { new { Text = "0", P = SeedProblem.None, Seed = 0u }, new { Text = "42", P = SeedProblem.None, Seed = 42u }, new { Text = "4294967295", P = SeedProblem.None, Seed = 4294967295u },
            new { Text = "4294967296", P = SeedProblem.TooLarge, Seed = 0u }, new { Text = "99999999999999999999999", P = SeedProblem.TooLarge, Seed = 0u }, new { Text = "", P = SeedProblem.Empty, Seed = 0u },
            new { Text = " 42", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "42 ", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "+42", P = SeedProblem.NotDigits, Seed = 0u },
            new { Text = "-1", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "042", P = SeedProblem.LeadingZero, Seed = 0u }, new { Text = "00", P = SeedProblem.LeadingZero, Seed = 0u },
            new { Text = "4,2", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "4.2", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "1e3", P = SeedProblem.NotDigits, Seed = 0u },
            new { Text = "\u0664\u0662", P = SeedProblem.NotDigits, Seed = 0u }, new { Text = "\uFF14\uFF12", P = SeedProblem.NotDigits, Seed = 0u } };
        var seedRows = new List<string>();
        bool seedsOk = table.All(row => { uint s; var p = SeedText.Parse(row.Text, contract.MaxSeed, out s); seedRows.Add("'" + row.Text + "' " + p); return p == row.P && (p != SeedProblem.None || s == row.Seed) && (p == SeedProblem.None) == (SeedText.Reason(p) == ""); });
        Check(seedsOk, "seeds are read exactly as worldbridge reads them (digits only, no sign, space, separator, exponent or non-ASCII digit, no leading zero, at most the producer's maximum), each refusal with its reason: " + String.Join("; ", seedRows));

        // Random World.
        var draws = Enumerable.Range(0, 2000).Select(_ => RandomWorld.Next(contract.MaxSeed, RandomNumberGenerator.Create())).ToList();
        var small = Enumerable.Range(0, 3000).Select(_ => RandomWorld.Next(2, RandomNumberGenerator.Create())).ToList();
        Check(draws.Distinct().Count() > 1990 && small.All(v => v <= 2) && new[] { 0u, 1u, 2u }.All(v => small.Count(x => x == v) > 800),
              "Random World draws uniformly from the operating system's cryptographic generator over the whole seed range (2000 draws, " + draws.Distinct().Count() + " distinct; [0, 2] covered evenly)");

        // Settings and waiting time.
        var settings = WorldSettings.For(contract);
        Check(settings.Count == 2 && settings.All(s => s.Locked && s.Reason.Length > 0) && settings[0].Id == "size" && settings[0].Value == "129" && settings[0].Variables["MIN"] == "3" && settings[0].Variables["MAX"] == "1025"
              && settings[0].Reason.Contains("different world") && settings[1].Id == "phase" && settings[1].Variables["VALUE"] == "16",
              "only the producer controls the profile fixes are shown, each locked with why; world size is explained as content (a different world), never as quality");
        var wait = WaitTime.Variables(contract);
        var noGenerate = WorldSetupContract.FromDocuments(Edit(docs, "one-shot-budget.json", "\"generateWorld\"", "\"otherPhase\""));
        var noStart = WorldSetupContract.FromDocuments(Edit(docs, "one-shot-budget.json", "\"startCampaign\"", "\"otherStartPhase\""));
        string waitText = WaitTime.Text(contract, (t, v) => v.Aggregate(t, (x, kv) => x.Replace("{" + kv.Key + "}", kv.Value)));
        // The expected values from the budget itself: its largest generation measurement rounded up to roundToSeconds, its largest first campaign
        // start in whole minutes rounded up.
        var budgetDoc = (IDictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(docs["one-shot-budget.json"]).TrimStart('﻿'));
        var budgetPhases = (IDictionary<string, object>)budgetDoc["phases"];
        Func<string, double> largest = id => ((object[])((IDictionary<string, object>)budgetPhases[id])["seconds"]).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).Max();
        int round = Convert.ToInt32(budgetDoc["roundToSeconds"], CultureInfo.InvariantCulture);
        string expectGenerate = (Math.Ceiling(largest("generateWorld") / round) * round).ToString("0", CultureInfo.InvariantCulture);
        string expectStart = Math.Max(1, Math.Ceiling(largest("startCampaign") / 60)).ToString("0", CultureInfo.InvariantCulture);
        Check(wait.Count == 2 && wait["GENERATE"] == expectGenerate && wait["START"] == expectStart && WaitTime.Available(contract) && !WaitTime.Available(noGenerate) && contract.BudgetStatus == "provisional"
              && !WaitTime.Variables(noStart).ContainsKey("START") && waitText.Contains("about " + expectGenerate + " seconds") && waitText.Contains("about " + expectStart + " minutes") && waitText.Contains("may stop responding")
              && !WaitTime.Text(noStart, (t, v) => t).Contains(WorldSetupText.WaitStart),
              "the waiting time is the budget's largest generation measurement rounded up to 5 s (" + expectGenerate + " s) and its largest first campaign start in whole minutes (" + expectStart + "), with the warning that the game may stop responding; nothing is promised without a measurement: " + waitText);

        // Host and generation error categories.
        var ok = GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", "v1.2.8", "v1.2.8", true, null);
        var noNaval = GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", null, "v1.2.8", false, null);
        var disabled = GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", "v1.2.8", "v1.2.8", false, null);
        var oldGame = GenerationErrors.Host("v1.3.0.1", "v1.4.8.119303", "v1.2.8", "v1.2.8", true, null);
        var oldNaval = GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", "v1.2.7", "v1.2.8", true, null);
        var tool = GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", "v1.2.8", "v1.2.8", true, new[] { new KeyValuePair<string, string>("fwg", "hash differs") });
        var both = GenerationErrors.Host("v1.3.0.1", "v1.4.8.119303", null, "v1.2.8", false, null);
        Check(ok.Count == 0 && noNaval.Single().Category == GenerationErrorCategory.MissingToolOrDlc && noNaval[0].Code == "host.war_sails_missing" && disabled.Single().Code == "host.war_sails_missing"
              && oldGame.Single().Category == GenerationErrorCategory.IncompatibleBuild && oldGame[0].Variables["EXPECTED"] == "v1.4.8.119303" && oldGame[0].Variables["ACTUAL"] == "v1.3.0.1"
              && oldNaval.Single().Code == "host.war_sails_version" && tool.Single().Category == GenerationErrorCategory.MissingToolOrDlc && tool[0].Detail.StartsWith("fwg:") && !tool[0].Message.Contains("fwg")
              && both.Count == 2 && both.Select(p => p.Action).Distinct().Count() == 2,
              "host refusals use the MMR-47 categories: War Sails missing or disabled (missing tool or DLC), another game or War Sails build (incompatible build, naming both), changed generator files (missing tool; the executable named only in the detail); several problems keep each action");
        var cancelled = GenerationErrors.FromGeneration("cancelled", new OperationCanceledException(), null, "d");
        var refusedRecipe = GenerationErrors.FromGeneration("producer_refused", new InvalidOperationException("x"), "INVALID_INPUT", "d");
        var reader = GenerationErrors.FromGeneration("reader_failed", new InvalidDataException("x"), null, "d");
        var missingTool = GenerationErrors.FromGeneration(null, new FileNotFoundException("missing", @"C:\m\bin\fwg.exe"), null, "d");
        var disk = GenerationErrors.FromGeneration("diagnostic_failed", new IOException("full", unchecked((int)0x80070070)), null, "d");
        var unknown = GenerationErrors.FromGeneration("something_new", new Exception("x"), null, "d");
        var errors = new[] { cancelled, refusedRecipe, reader, missingTool, disk, unknown };
        Check(cancelled.Category == GenerationErrorCategory.InterruptedGeneration && refusedRecipe.Category == GenerationErrorCategory.RefusedRecipe && refusedRecipe.Detail.Contains("INVALID_INPUT")
              && reader.Category == GenerationErrorCategory.BadSource && missingTool.Category == GenerationErrorCategory.MissingToolOrDlc && missingTool.Detail.StartsWith("fwg:")
              && disk.Category == GenerationErrorCategory.DiskFull && unknown.Category == GenerationErrorCategory.BadSource
              && errors.All(e => !e.Message.Contains("INVALID_INPUT") && !e.Message.Contains("fwg") && !e.Message.Contains("{CODE}") && !e.Message.Contains("{TOOL}"))
              && Enum.GetValues(typeof(GenerationErrorCategory)).Cast<GenerationErrorCategory>().All(c => GenerationErrors.Title(c).StartsWith("{=mnbl_err_") && GenerationErrors.Action(c).StartsWith("{=mnbl_err_")),
              "generation outcomes map to the categories (cancelled: interrupted; refused: refused recipe; reader or exit failure: bad source; missing generator: missing tool; disk full; anything unknown: bad source, never success), each with a localisable title and action; producer codes and executable names stay in the detail");

        // The setup state machine over a scripted job.
        string split = Path.Combine(Path.GetTempPath(), "mnbl-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(split); File.Copy(Path.Combine(bundles[0], "source", "world.json"), Path.Combine(split, "world.json"));
        var job = new ScriptedRunner { Split = split };
        var model = new WorldSetupModel(contract, job, null);
        int revision = model.Revision;
        bool initial = model.SeedTextValue == "42" && model.CanGenerate && model.State == SetupState.Editing;
        model.SetSeedText("042"); bool blocked = !model.CanGenerate && !model.Generate() && model.Revision > revision;
        model.SetSeedText("364610"); bool started = model.Generate() && model.State == SetupState.Generating && !model.CanEditSeed;
        model.SetSeedText("1"); bool locked = model.SeedTextValue == "364610"; model.RandomWorldSeed(); locked &= model.SeedTextValue == "364610";
        var attempt = job.Stage(JobPhase.GeneratingSource, 1, 3); model.Tick(); string stageText = model.StageText; var stageVars = model.StageVariables;
        // A late event of another (earlier) job, and an out-of-order event of this one, are dropped.
        job.Emit(new ProgressEvent { JobId = "an-earlier-job", Sequence = 99, Phase = JobPhase.ValidatingRuntime, MessageCode = "late" });
        job.Emit(new ProgressEvent { JobId = job.LastJob, Sequence = 0, Phase = JobPhase.Compiling, MessageCode = "stale" });
        model.Tick(); bool dropped = model.StageText == stageText && model.Sink.DroppedStale == 1 && model.Sink.DroppedOutOfOrder == 1;
        job.Go.Set(); Wait(model);
        var choice = model.Choice;
        Check(initial && blocked && started && locked && stageText == StageText.Describe(attempt) && stageVars["ATTEMPT"] == "2" && stageVars["ATTEMPTS"] == "3" && dropped && model.State == SetupState.Generated && model.Result.Cities == 45
              && choice.Seed == 364610 && choice.SeedSource == "explicit" && Guid.Parse(choice.CampaignInstanceId) != Guid.Empty && choice.RecipeVersion == 3 && choice.MaxAttempts == 3
              && choice.Overrides.SequenceEqual(contract.Overrides) && choice.ProducerLockSha256 == contract.ProducerLockSha256 && job.Choices[0] == choice,
              "Generate hands the job exactly the shown choice (a fresh campaign instance id, the typed seed, the profile's recipe and overrides, attempts and producer lock), locks seed edits while it runs, shows its phases (attempt 2 of 3) from its own job's events only, in order, and ends with the generated world (summarised off the engine thread)");
        evidence["requestFields"] = choice.RequestFields();
        // The request fields against worldgen/contracts/generation-request.schema.json.
        var schema = Json(Path.Combine(repo, "worldgen/contracts/generation-request.schema.json"));
        var properties = (IDictionary<string, object>)schema["properties"];
        var fields = choice.RequestFields();
        var schemaProblems = new List<string>();
        foreach (var kv in fields)
        {
            if (!properties.ContainsKey(kv.Key)) { schemaProblems.Add(kv.Key + " is not a request property"); continue; }
            var p = (IDictionary<string, object>)properties[kv.Key];
            string text = kv.Value is IDictionary<string, object> ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            if (p.ContainsKey("enum") && !((object[])p["enum"]).Contains(kv.Value)) schemaProblems.Add(kv.Key + " not in enum");
            if (p.ContainsKey("const") && Convert.ToString(p["const"], CultureInfo.InvariantCulture) != text) schemaProblems.Add(kv.Key + " differs from const");
            if (p.ContainsKey("pattern") && (text == null || !Regex.IsMatch(text, (string)p["pattern"]))) schemaProblems.Add(kv.Key + " fails its pattern");
            if (p.ContainsKey("minimum") && Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture) < Convert.ToDouble(p["minimum"], CultureInfo.InvariantCulture)) schemaProblems.Add(kv.Key + " below minimum");
            if (p.ContainsKey("maximum") && Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture) > Convert.ToDouble(p["maximum"], CultureInfo.InvariantCulture)) schemaProblems.Add(kv.Key + " above maximum");
        }
        var notSupplied = ((object[])schema["required"]).Cast<string>().Where(r => !fields.ContainsKey(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
        var randomModel = new WorldSetupModel(contract, new ScriptedRunner { Split = split }, null); randomModel.RandomWorldSeed();
        Check(schemaProblems.Count == 0 && notSupplied.SequenceEqual(new[] { "policies", "schema", "version" }) && randomModel.SeedSource == "random" && ((object[])((IDictionary<string, object>)properties["seedSource"])["enum"]).Contains("random"),
              "the choice's request fields are generation request properties with valid values (seedSource explicit/random, a v4-shaped campaign instance id, the producer lock's sha256); MMR-44 adds only schema, version and policies" + (schemaProblems.Count > 0 ? ": " + String.Join("; ", schemaProblems) : ""));

        var model2 = new WorldSetupModel(contract, job = new ScriptedRunner { Split = split }, null);
        model2.RandomWorldSeed(); string source = model2.SeedSource; model2.SetSeedText(model2.SeedTextValue + "1"); string edited = model2.SeedSource;
        model2.SetSeedText("7"); model2.Generate(); bool backCancels = !model2.Back() && model2.CancelRequested; job.Go.Set(); Wait(model2);
        Check(source == "random" && edited == "explicit" && backCancels && model2.State == SetupState.Failed && model2.Error.Category == GenerationErrorCategory.InterruptedGeneration && model2.Back(),
              "Random World marks its seed as drawn (typing makes it explicit again); Back while generating cancels and stays (interrupted generation), Back when idle leaves");
        var model3 = new WorldSetupModel(contract, job = new ScriptedRunner { Split = split, Fail = new InvalidDataException("reader") }, null);
        model3.Generate(); job.Go.Set(); Wait(model3);
        var model4 = new WorldSetupModel(contract, new ScriptedRunner { Split = split }, noNaval);
        var model5 = new WorldSetupModel(contract, job = new ScriptedRunner { Split = split, Fail = new TimeoutException("logs did not drain"), FailOnCancel = true }, null);
        model5.Generate(); model5.Cancel(); Wait(model5);
        Check(model3.State == SetupState.Failed && model3.Error.Category == GenerationErrorCategory.BadSource && !model4.CanGenerate && !model4.Generate()
              && model5.State == SetupState.Failed && model5.Error.Category == GenerationErrorCategory.InterruptedGeneration,
              "a failed job ends in its category; a cancel the player asked for is an interrupted generation whatever the job then threw; with a host problem nothing can be generated");
        // Job files: the previous job is released when a new one starts, the last when the screen closes (also while one is running).
        var releaser = new ScriptedRunner { Split = split, AutoRun = true };
        var model6 = new WorldSetupModel(contract, releaser, null);
        model6.Generate(); Wait(model6); string firstJob = model6.JobRoot;
        model6.Generate(); Wait(model6); string secondJob = model6.JobRoot;
        model6.Releasing.Wait(); bool firstReleased = releaser.Released.SequenceEqual(new[] { firstJob });
        model6.Close(); model6.Releasing.Wait();
        var runningRunner = new ScriptedRunner { Split = split };
        var model7 = new WorldSetupModel(contract, runningRunner, null);
        model7.Generate(); runningRunner.Stage(JobPhase.GeneratingSource, null, null); model7.Close(); model7.Releasing.Wait(5000);
        Check(firstJob != secondJob && firstReleased && releaser.Released.SequenceEqual(new[] { firstJob, secondJob }) && !model6.CanGenerate
              && runningRunner.Released.Count == 1 && runningRunner.Released[0] == runningRunner.LastJob,
              "a new Generate releases the previous job's files and closing the screen releases the last (a running job once it has stopped), off the engine thread");

        // MMR-46: the review flow, with the preview of bundle A (its own layout from evidence/world/settlement-position-report.json). The view
        // models render text without the game's localisation (the {=id} prefix dropped, {VARIABLES} substituted) and have no key categories.
        WorldSetupViewModel.KeyCategory = id => null;
        WorldSetupViewModel.Render = (text, variables) =>
        {
            string t = Regex.Replace(text, @"^\{=[a-z0-9_]+\}", "");
            if (variables != null) foreach (var kv in variables) t = t.Replace("{" + kv.Key + "}", kv.Value);
            return t;
        };
        var layoutA = (IDictionary<string, object>)((IDictionary<string, object>)Json(Path.Combine(repo, "evidence/world/settlement-position-report.json"))["worlds"])["A"];
        var previewA = BuildPreview(bundles[0], repo, layoutA);
        var flow = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        var m = new WorldSetupModel(contract, flow, null);
        m.SetSeedText("364610"); m.Generate(); Wait(m);
        bool reviewed = m.State == SetupState.Generated && m.Preview == previewA && m.CanStartCampaign && flow.Previews == 1 && flow.PreviewJobs[0] == m.JobRoot && m.Generations == 1;
        // The seed box is hidden under the preview: a typed seed or a Random World draw does not change the reviewed world's seed.
        m.SetSeedText("7"); m.RandomWorldSeed();
        reviewed &= !m.CanEditSeed && m.SeedTextValue == "364610" && m.Seed == 364610;
        string reviewedJob = m.JobRoot;
        bool backKeeps = !m.Back() && m.State == SetupState.Editing && m.CanEditSeed && !m.CanStartCampaign && flow.Released.Count == 0 && m.Preview == previewA;
        bool reused = m.Generate() && m.State == SetupState.Generated && m.Reused && m.Generations == 1 && flow.Choices.Count == 1 && m.JobRoot == reviewedJob && flow.Previews == 1;
        // A changed seed is a new world: the reviewed one is released first.
        m.Back(); m.SetSeedText("364611"); m.Generate(); Wait(m);
        m.Releasing.Wait();
        bool changed = m.State == SetupState.Generated && !m.Reused && m.Generations == 2 && flow.Choices.Count == 2 && flow.Choices[1].Seed == 364611 && flow.Released.SequenceEqual(new[] { reviewedJob });
        Check(reviewed && backKeeps && reused && changed,
              "a generated world is shown with its preview and offered for play, its seed locked while shown; Back returns to the seed with the world kept (nothing released, Start Campaign withdrawn), Generate with the same seed shows it again without a second job or preview, and a changed seed makes a new world after releasing the reviewed one");

        // Start Campaign: the job goes to the activation; the setup waits, locked; withdrawn -> review again (the same job); a second start;
        // committed -> started, the job's files left to the activation's runner.
        string playJob = m.JobRoot;
        bool starting = m.StartCampaign() && m.State == SetupState.Starting && flow.StartedJobs.SequenceEqual(new[] { playJob }) && !m.CanGenerate && !m.CanEditSeed && !m.CanStartCampaign && !m.Generate() && !m.Back() && !m.NewSeed() && !m.StartCampaign();
        m.HandOff(); bool handedOff = m.HandedOff;
        flow.Start.SetResult(new StartOutcome { Kind = "withdrawn" }); WaitStart(m);
        bool withdrawn = m.State == SetupState.Generated && m.Withdrawn && m.CanStartCampaign && m.JobRoot == playJob && flow.Released.Count == 1 && m.Starts == 1;
        m.Resume();
        bool again = m.StartCampaign() && flow.StartedJobs.SequenceEqual(new[] { playJob, playJob }) && !m.Withdrawn;
        flow.Start.SetResult(new StartOutcome { Kind = "playing" }); WaitStart(m);
        m.Close(); m.Releasing.Wait();
        bool played = m.State == SetupState.Started && m.Starts == 2 && flow.Released.Count == 1 && m.Closed && flow.Choices.Count == 2 && m.Generations == 2;
        Check(starting && handedOff && withdrawn && again && played,
              "Start Campaign hands the reviewed job to the activation and locks the setup (no generate, edit, back, new seed or second start); a withdrawn start returns to the same world ready for review; the next start commits; the committed job is not released by the setup and no world was generated twice");

        // A failed start shows its error and keeps the job until the next Generate; New Seed draws a seed and generates at once.
        var failing = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        var mf = new WorldSetupModel(contract, failing, null);
        mf.Generate(); Wait(mf); string failedJob = mf.JobRoot;
        mf.StartCampaign(); failing.Start.SetResult(new StartOutcome { Kind = "failed", Error = GenerationErrors.Activation("test") }); WaitStart(mf);
        bool failedShown = mf.State == SetupState.Failed && mf.Error.Code == "activation.failed" && failing.Released.Count == 0 && mf.CanGenerate;
        var throwing = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        var mt = new WorldSetupModel(contract, throwing, null);
        mt.Generate(); Wait(mt); mt.StartCampaign(); throwing.Start.SetException(new InvalidOperationException("boom")); WaitStart(mt);
        bool thrownShown = mt.State == SetupState.Failed && mt.Error != null;
        var seeding = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        var ms = new WorldSetupModel(contract, seeding, null, new FixedRng(0x01020304));
        ms.Generate(); Wait(ms); string firstSeedJob = ms.JobRoot; uint firstSeed = ms.Choice.Seed;
        bool newSeed = ms.NewSeed() && ms.State == SetupState.Generating; Wait(ms); ms.Releasing.Wait();
        newSeed &= ms.State == SetupState.Generated && ms.Choice.Seed != firstSeed && ms.Choice.Seed == 0x01020304u && ms.Choice.SeedSource == "random" && ms.SeedTextValue == ms.Choice.Seed.ToString(CultureInfo.InvariantCulture)
            && seeding.Released.SequenceEqual(new[] { firstSeedJob }) && ms.Generations == 2;
        var noPreview = new ScriptedRunner { Split = split, AutoRun = true, PreviewFail = new InvalidDataException("no layout") };
        var mp = new WorldSetupModel(contract, noPreview, null);
        mp.Generate(); Wait(mp);
        var vmp = new WorldSetupViewModel(mp, () => { });
        bool previewless = mp.State == SetupState.Generated && mp.Preview == null && mp.PreviewError.Contains("no layout") && !mp.CanStartCampaign && !mp.StartCampaign() && !vmp.IsPreviewShown && vmp.HasNotice
            && vmp.NoticeText == WorldSetupViewModel.Text(WorldSetupText.PreviewFailed) && vmp.IsEditing && vmp.HasResult && mp.CanEditSeed && !mp.PreviewShown;
        Check(failedShown && thrownShown && newSeed && previewless,
              "a failed start (reported or thrown) shows its error with the world kept for the next Generate; New Seed draws a random seed into the seed box and generates it at once, releasing the reviewed world; a world whose preview could not be made is not offered for play and says why");

        // MMR-47: every failure shown gets a reference (correlation id) with its diagnostics retained; the details stay behind a toggle.
        string diagRoot = Path.Combine(Path.GetTempPath(), "mnbl-diag-" + Guid.NewGuid().ToString("N"));
        var diagnostics = new GenerationDiagnostics(diagRoot);
        var hostModel = new WorldSetupModel(contract, new ScriptedRunner { Split = split }, GenerationErrors.Host("v1.4.8.119303", "v1.4.8.119303", null, "v1.2.8", false, null), null, diagnostics);
        var failingRun = new ScriptedRunner { Split = split, AutoRun = true, Fail = new InvalidDataException("reader detail C:\\path\\to\\job") };
        var failModel = new WorldSetupModel(contract, failingRun, null, null, diagnostics);
        failModel.Generate(); Wait(failModel);
        var failVm = new WorldSetupViewModel(failModel, () => { });
        string reference = failVm.ErrorView.ReferenceText, detailsHidden = failVm.ErrorView.DetailsText;
        failVm.ErrorView.ExecuteToggleDetails(); string detailsShown = failVm.ErrorView.DetailsText;
        var hostVm = new WorldSetupViewModel(hostModel, () => { });
        var failRecord = diagnostics.Read(failModel.Error.CorrelationId);
        bool referenced = failModel.Error.CorrelationId != null && failRecord != null && (string)failRecord["code"] == failModel.Error.Code && (string)failRecord["detail"] == failModel.Error.Detail && failModel.Error.Detail.Length > 0
            && reference.Contains(failModel.Error.CorrelationId) && !reference.Contains("path") && detailsHidden == "" && detailsShown.Contains(failModel.Error.Code) && detailsShown.Contains(diagnostics.Folder(failModel.Error.CorrelationId))
            && !failVm.ErrorView.MessageText.Contains("path") && hostModel.HostProblems.All(p => p.CorrelationId != null && diagnostics.Read(p.CorrelationId) != null)
            && hostVm.HostProblemViews.Count == hostModel.HostProblems.Count && hostVm.HostProblemViews.All(v => v.HasReference && v.ReferenceText.Contains(v.Error.CorrelationId))
            && diagnostics.Ids().Count == hostModel.HostProblems.Count + 1 && diagnostics.Ids().Distinct().Count() == diagnostics.Ids().Count;
        Check(referenced, "a failed generation and every host problem get a reference (" + failModel.Error.CorrelationId + ") the screen shows beside the message and action; the detail (codes, paths, the retained diagnostics folder) appears only under Details, and the record keeps it");
        // MMR-47: an unfinished job from an earlier session is offered: continue it (its own choice and directory) or discard it.
        var resumable = new WorldRecoveryService.UnfinishedJob { Id = new string('a', 32), Root = Path.Combine(Path.GetTempPath(), "jobs", new string('a', 32)), Phase = "GeneratingSource", Kind = WorldRecoveryService.Resumable, HasChoice = true,
            Seed = 77, SeedSource = "random", CampaignInstanceId = Guid.NewGuid().ToString("D"), ProducerLockSha256 = contract.ProducerLockSha256 };
        var stale = new WorldRecoveryService.UnfinishedJob { Id = new string('b', 32), Root = Path.Combine(Path.GetTempPath(), "jobs", new string('b', 32)), Phase = "Compiling", Kind = WorldRecoveryService.OtherGenerator, HasChoice = true, Seed = 5,
            Reason = "made by another version of the world generator" };
        var resumeRunner = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        resumeRunner.UnfinishedJobs.Add(resumable); resumeRunner.UnfinishedJobs.Add(stale);
        var mr2 = new WorldSetupModel(contract, resumeRunner, null);
        var vmr2 = new WorldSetupViewModel(mr2, () => { });
        bool offered = mr2.Offered == resumable && mr2.CanResume && vmr2.HasUnfinished && vmr2.UnfinishedText.Contains("77") && vmr2.UnfinishedText.Contains("1 more");
        bool resumedOk = mr2.ResumeUnfinished(); Wait(mr2);
        var resumedChoice = resumeRunner.Choices.Last();
        resumedOk &= resumedChoice.ResumeJobRoot == resumable.Root && resumedChoice.Seed == 77 && resumedChoice.SeedSource == "random" && resumedChoice.CampaignInstanceId == resumable.CampaignInstanceId
            && resumedChoice.ProducerLockSha256 == contract.ProducerLockSha256 && mr2.SeedTextValue == "77" && mr2.State == SetupState.Generated && mr2.Resumed == 1;
        mr2.Back(); vmr2.Refresh();
        bool staleOffered = mr2.Offered == stale && !mr2.CanResume && mr2.CanDiscard && vmr2.HasUnfinished && vmr2.UnfinishedText.Contains("another version");
        resumeRunner.DiscardRefusal = "a job of this process";
        bool refusedKept = !mr2.DiscardUnfinished() && mr2.Offered == stale && mr2.DiscardProblem == "a job of this process";
        resumeRunner.DiscardRefusal = null;
        vmr2.Refresh(); bool refusalShown = vmr2.UnfinishedText.Contains("a job of this process");
        resumeRunner.DiscardRefusal = null;
        bool discarded = mr2.DiscardUnfinished() && resumeRunner.Discarded.SequenceEqual(new[] { stale.Id }) && mr2.Offered == null && !mr2.CanDiscard;
        var kinds = new ScriptedRunner { Split = split };
        kinds.UnfinishedJobs.Add(new WorldRecoveryService.UnfinishedJob { Id = new string('c', 32), Root = "c", Phase = "Committing", Kind = WorldRecoveryService.Committing, HasChoice = true, Seed = 9 });
        kinds.UnfinishedJobs.Add(new WorldRecoveryService.UnfinishedJob { Id = new string('d', 32), Root = "d", Phase = "unknown", Kind = WorldRecoveryService.Unreadable });
        var mk = new WorldSetupModel(contract, kinds, null); var vmk = new WorldSetupViewModel(mk, () => { });
        string committingText = vmk.UnfinishedText; mk.DiscardUnfinished(); vmk.Refresh(); string unreadableText = vmk.UnfinishedText;
        bool kindsOk = committingText.Contains("campaign was starting") && !mk.CanResume && unreadableText.Contains("cannot be read") && !unreadableText.Contains("seed");
        // A job continued while the generation job is busy never started: it is offered again.
        var busyRunner = new ScriptedRunner { Split = split, AutoRun = true, Fail = new InvalidOperationException("busy") };
        var reoffer = new WorldRecoveryService.UnfinishedJob { Id = new string('e', 32), Root = "e", Phase = "GeneratingSource", Kind = WorldRecoveryService.Resumable, HasChoice = true, Seed = 11,
            CampaignInstanceId = Guid.NewGuid().ToString("D"), ProducerLockSha256 = contract.ProducerLockSha256 };
        busyRunner.UnfinishedJobs.Add(reoffer);
        var mb = new WorldSetupModel(contract, busyRunner, null);
        busyRunner.FailBeforeJob = true;
        mb.ResumeUnfinished(); Wait(mb);
        bool reoffered = mb.State == SetupState.Failed && mb.Offered == reoffer;
        vmr2.Refresh();
        Check(offered && resumedOk && staleOffered && refusedKept && refusalShown && discarded && !vmr2.HasUnfinished && kindsOk && reoffered,
              "an unfinished job from an earlier session is offered with its seed; Continue starts it again in its own directory with its own seed, seed source and campaign instance; a job made by another generator is offered only for Discard; one stopped while its campaign started, and one whose records cannot be read, get their own sentences (no seed when none was read); a refused discard keeps the job and says why on the screen; Discard removes only that job and the next is offered; a continued job that never started (the generation job busy) is offered again");
        try { Directory.Delete(diagRoot, true); } catch (IOException) { }

        // Keys per state.
        Func<string, bool> confirm = k => k == "Confirm", randomize = k => k == "Randomize", exit = k => k == "Exit";
        Func<string, bool> previous = k => k == "SwitchToPreviousTab", next = k => k == "SwitchToNextTab";
        Check(SetupInput.ForState(SetupInput.Map(confirm, false), SetupState.Generated, true) == SetupCommand.StartCampaign && SetupInput.ForState(SetupInput.Map(randomize, false), SetupState.Generated, true) == SetupCommand.NewSeed
              && SetupInput.ForState(SetupInput.Map(exit, false), SetupState.Generated, true) == SetupCommand.Back && SetupInput.ForState(SetupInput.Map(confirm, false), SetupState.Editing, false) == SetupCommand.Generate
              && SetupInput.ForState(SetupInput.Map(previous, false), SetupState.Generated, true) == SetupCommand.PreviousPlace && SetupInput.ForState(SetupInput.Map(next, false), SetupState.Generated, true) == SetupCommand.NextPlace
              && SetupInput.ForState(SetupInput.Map(next, false), SetupState.Editing, false) == SetupCommand.None && SetupInput.Map(next, true) == SetupCommand.None
              && SetupInput.ForState(SetupInput.Map(confirm, false), SetupState.Generated, false) == SetupCommand.Generate && SetupInput.ForState(SetupInput.Map(randomize, false), SetupState.Generated, false) == SetupCommand.RandomWorld
              && SetupInput.ForState(SetupInput.Map(confirm, false), SetupState.Starting, false) == SetupCommand.None && SetupInput.ForState(SetupInput.Map(exit, false), SetupState.Starting, false) == SetupCommand.None
              && SetupInput.ForState(SetupInput.Map(randomize, false), SetupState.Failed, false) == SetupCommand.RandomWorld,
              "with the preview shown Confirm starts the campaign, Randomize draws a new seed, the tab keys select places and Exit goes back to the seed; a world without a preview keeps Generate and Random World; while the campaign starts no key acts; elsewhere the keys keep their setup meaning and the tab keys do nothing");
        // The setup's report of a finished commit: never the generation's message.
        var diskFull = GenerationErrors.Job(GenerationErrorCategory.DiskFull, "x", "d");
        var fromPlay = StartOutcome.FromCommit(JobPhase.Playing, null, null, false); var fromReview = StartOutcome.FromCommit(JobPhase.ReadyForReview, null, null, false);
        var rolledBack = StartOutcome.FromCommit(JobPhase.Failed, GenerationErrors.FromGeneration("x", new InvalidOperationException("y"), null, "z"), "detail", false);
        var unresolved = StartOutcome.FromCommit(JobPhase.Failed, null, "detail", true); var full = StartOutcome.FromCommit(JobPhase.Failed, diskFull, "detail", false);
        Check(fromPlay.Kind == "playing" && fromReview.Kind == "withdrawn" && rolledBack.Kind == "failed" && rolledBack.Error.Code == "activation.failed" && rolledBack.Error.Detail == "detail"
              && unresolved.Error.Code == "activation.unresolved" && full.Error == diskFull,
              "a finished commit is reported as committed, withdrawn, or failed with the activation's own message (undone, or left for recovery after the initial save); a full disk keeps its category; never 'could not be generated'");

        // The view models over the preview: what the panel shows.
        var vmFlow = new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = previewA };
        var mv = new WorldSetupModel(contract, vmFlow, null);
        mv.SetSeedText("364610"); mv.Generate(); Wait(mv);
        var vm = new WorldSetupViewModel(mv, () => { });
        var pv = vm.Preview;
        var town = pv.Markers.First(x => x.IsTown);
        town.ExecuteHover(); string hover = pv.HoverText; town.ExecuteHoverEnd();
        pv.ExecuteNextPlace(); var first = pv.Selected; string firstText = pv.HoverText;
        pv.ExecutePreviousPlace(); var last = pv.Selected;
        town.ExecuteHover(); string hoverOverSelection = pv.HoverText; town.ExecuteHoverEnd(); string backToSelection = pv.HoverText;
        bool placesOk = pv.Places.Count == previewA.Markers.Count(x => x.Kind == "town" || x.Kind == "castle") && first == pv.Places[0] && last == pv.Places[pv.Places.Count - 1] && !first.IsSelected && last.IsSelected
            && firstText.StartsWith(first.FullName, StringComparison.Ordinal) && hoverOverSelection == hover && backToSelection.StartsWith(last.FullName, StringComparison.Ordinal)
            && pv.Places.Zip(pv.Places.Skip(1), (a, b) => a.Marker.U <= b.Marker.U).All(x => x)
            // Every kingdom of the world is named in full by some place the keys reach, and by its label when pointed at.
            && previewA.KingdomNames.Keys.All(k => pv.Places.Any(x => x.Marker.Kingdom == k)) && pv.Regions.Count > 0;
        var region = pv.Regions.First(); region.ExecuteHover(); string kingdomHover = pv.HoverText; region.ExecuteHoverEnd();
        placesOk &= kingdomHover.Contains(region.FullName) && pv.HoverText.StartsWith(last.FullName, StringComparison.Ordinal);
        var longMarker = new WorldPreviewMarkerVM(new WorldPreview.Marker { Name = "Aaaaaaaaaaaaaaaaaaaaaaaaaaaa of the Long Name", Kind = "town", U = .95, V = .5 }, null);
        bool panel = placesOk && vm.IsPreviewShown && !vm.IsEditing && vm.CanStartCampaign && vm.CanGoBack && pv.HasPreview && !pv.HasTextureError && pv.Markers.Count == previewA.Markers.Count
            && pv.Markers.Select(x => x.Marker.Id).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(previewA.Markers.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal))
            && pv.Markers.All(x => x.PositionX + x.MarkerSize / 2 >= 0 && x.PositionX + x.MarkerSize / 2 <= WorldPreviewViewModel.PreviewWidth && x.PositionY + x.MarkerSize / 2 >= 0 && x.PositionY + x.MarkerSize / 2 <= WorldPreviewViewModel.PreviewHeight)
            && pv.Markers.Count(x => x.ShowLabel) == previewA.Markers.Count(x => x.Kind == "town") && pv.Regions.All(r => r.Region.Towns + r.Region.Castles >= 2)
            && hover.StartsWith(town.FullName, StringComparison.Ordinal) && pv.HoverText == backToSelection && vm.PreviewTitleText.Contains("364610") && pv.CountsText.Contains(previewA.Counts["towns"].ToString(CultureInfo.InvariantCulture))
            && longMarker.Name.Length == WorldPreviewViewModel.LabelLength && longMarker.Name.EndsWith("\u2026", StringComparison.Ordinal) && longMarker.ShowLabelLeft && !longMarker.ShowLabelRight && longMarker.LabelX < 0;
        mv.Back(); vm.Refresh();
        bool reuseNotice = vm.IsEditing && !vm.IsPreviewShown && vm.HasNotice && vm.NoticeText.Contains("364610");
        WorldPreviewTextures.Refuse = true;
        var mr = new WorldSetupModel(contract, new ScriptedRunner { Split = split, AutoRun = true, PreviewResult = BuildPreview(bundles[1], repo, (IDictionary<string, object>)((IDictionary<string, object>)Json(Path.Combine(repo, "evidence/world/settlement-position-report.json"))["worlds"])["B"]) }, null);
        mr.Generate(); Wait(mr);
        var vmr = new WorldSetupViewModel(mr, () => { });
        bool refusedTexture = vmr.Preview.HasTextureError && vmr.Preview.TextureErrorText == WorldSetupViewModel.Text(WorldSetupText.PreviewTextureFailed) && vmr.Preview.Markers.Count > 0 && vmr.CanStartCampaign;
        WorldPreviewTextures.Refuse = false;
        evidence["preview"] = new Dictionary<string, object> { ["markers"] = pv.Markers.Count, ["labels"] = pv.Markers.Count(x => x.ShowLabel), ["regions"] = pv.Regions.Count, ["hover"] = hover, ["title"] = vm.PreviewTitleText, ["counts"] = pv.CountsText };
        Check(panel && reuseNotice && refusedTexture,
              "the preview panel shows every marker of the world inside the map (" + pv.Markers.Count + "), a label per town (" + pv.Markers.Count(x => x.ShowLabel) + "), the kingdoms holding two or more fortifications (" + pv.Regions.Count + "), the seed and the compiled counts; pointing at a place names it in full (" + hover + "), Previous/Next place walk the " + pv.Places.Count + " towns and castles west to east, each named in full with its kingdom (wrapping; every kingdom of the world is reached), and a kingdom's label names it in full when pointed at; a long name is shortened with an ellipsis and an east-edge label goes west; Back says the world is kept; a refused texture still shows the places and keeps the world playable");

        // Two generated worlds: the streaming reader against a whole-document reading, and the cultures against the compiled campaigns.
        var worlds = new List<object>();
        bool readerMatches = true, culturesMatch = true;
        foreach (var bundle in bundles)
        {
            string worldJson = Path.Combine(bundle, "source", "world.json");
            var fast = SourceWorldSummary.Read(worldJson, contract);
            var slow = SourceWorldSummary.FromWorldJson(File.ReadAllText(worldJson), contract);
            readerMatches &= fast.Cities == slow.Cities && fast.Peoples.Count == slow.Peoples.Count && fast.Peoples.Zip(slow.Peoples, (a, b) => a.Civilization == b.Civilization && a.Name == b.Name && a.Cities == b.Cities && a.Culture == b.Culture).All(x => x)
                && fast.Cultures.SequenceEqual(slow.Cultures);
            var campaign = Json(Path.Combine(bundle, "compiled", "campaign", "campaign.json"));
            var compiled = new SortedSet<string>(((object[])campaign["towns"]).Cast<IDictionary<string, object>>().Select(t => (string)t["presentation"]).Concat(((object[])campaign["kingdoms"]).Cast<IDictionary<string, object>>().Select(k => (string)k["presentation"])), StringComparer.Ordinal);
            culturesMatch &= compiled.SetEquals(fast.Cultures) && fast.Peoples.All(p => !p.Name.Contains("_"));
            worlds.Add(new Dictionary<string, object> { ["bundle"] = Path.GetFileName(bundle), ["cities"] = fast.Cities, ["peoples"] = fast.Peoples.Select(p => p.Name + " " + p.Cities + " -> " + p.Culture).ToList(),
                ["cultures"] = fast.Cultures, ["compiledPresentations"] = compiled.ToList() });
        }
        evidence["worlds"] = worlds;
        Check(readerMatches, "the setup's one-pass world.json reader gives exactly the whole-document reading's peoples, names, city counts and cultures on both generated worlds");
        Check(culturesMatch, "the cultures the setup says character creation offers are exactly those each world's compiled campaign presents through (towns and kingdoms): " + String.Join("; ", worlds.Cast<Dictionary<string, object>>().Select(w => w["cities"] + " cities: " + String.Join(",", (List<string>)w["cultures"]))));
        var synthetic = "{\"humans\":{\"cores\":[{\"site_id\":{\"population_profile\":\"nested\"},\"population_profile\":\"elf\"},{\"population_profile\":\"gnome\",\"x\":[1,2,{\"a\":\"}\"}]}],\"z\":null},"
            + "\"civilizations\":{\"entities\":[{\"id\":\"elf\",\"name\":\"Hi\\u0067h \\\"Elves\\\"\",\"inner\":{\"id\":\"wrong\",\"name\":\"wrong\"}},{\"name\":\"Gnomes\",\"id\":\"gnome\"}]},\"tail\":[true,false,-1.5e3]}";
        var syntheticPath = Path.Combine(split, "synthetic.json"); File.WriteAllText(syntheticPath, synthetic);
        var s1 = SourceWorldSummary.Read(syntheticPath, contract);
        File.WriteAllText(syntheticPath, synthetic.Substring(0, synthetic.Length - 20));
        Check(s1.Cities == 2 && s1.Peoples.Any(p => p.Name == "High \"Elves\"" && p.Culture == "battania") && s1.Peoples.Any(p => p.Name == "Gnomes" && p.Culture == "empire") && refused(() => SourceWorldSummary.Read(syntheticPath, contract)),
              "the reader takes only the members' own fields (not nested look-alikes), decodes escapes, skips every other value, and refuses a truncated document");

        // Keys and stage texts.
        Check(SetupInput.Map(k => k == "Exit", false) == SetupCommand.Back && SetupInput.Map(k => k == "Confirm", false) == SetupCommand.Generate && SetupInput.Map(k => true, false) == SetupCommand.Back
              && SetupInput.Map(k => false, false) == SetupCommand.None && SetupInput.Map(k => k == "Randomize", false) == SetupCommand.RandomWorld && SetupInput.Map(k => k == "Randomize", true) == SetupCommand.None,
              "the generic panel keys: Exit (Escape, the controller's right face button) goes back or cancels, Confirm (Enter, Numpad Enter, the left face button) generates, Randomize (R, the top face button) draws a Random World seed unless the seed box is being typed in; Exit wins");
        var phases = JobStateMachine.Pipeline.TakeWhile(p => p != JobPhase.ReadyForReview).ToList();
        var stages = phases.Select(p => new ProgressEvent { Phase = p }).Concat(new[] { new ProgressEvent { Phase = JobPhase.GeneratingSource, Completed = 0, Total = 3 } }).ToList();
        Check(stages.All(s => StageText.Describe(s) != null && StageText.Describe(s).StartsWith("{=mnbl_ws_stage_")) && new[] { JobPhase.ReadyForReview, JobPhase.Committing, JobPhase.Playing, JobPhase.Cancelled, JobPhase.Failed }.All(p => StageText.Describe(new ProgressEvent { Phase = p }) == null),
              "every running phase of the generation job has player text (" + phases.Count + " phases; the attempt count while the source is generated); review, commit and the final states keep the last text");

        // Localisation.
        var texts = WorldSetupText.All().Concat(Enum.GetValues(typeof(GenerationErrorCategory)).Cast<GenerationErrorCategory>().SelectMany(c => new[] { GenerationErrors.Title(c), GenerationErrors.Action(c) }))
            .Concat(stages.Select(StageText.Describe)).Concat(new[] { ok, noNaval, oldGame, oldNaval, tool }.SelectMany(x => x).Select(e => e.Message)).Concat(errors.Select(e => e.Message))
            .Concat(Enum.GetValues(typeof(GenerationErrorCategory)).Cast<GenerationErrorCategory>().Select(c => GenerationErrors.Job(c, "x", null).Message))
            .Concat(new[] { GenerationErrors.Busy("x").Message, GenerationErrors.Activation("x").Message, GenerationErrors.ActivationUnresolved("x").Message }).ToList();
        var ids = texts.Select(t => Regex.Match(t, "^\\{=([a-z0-9_]+)\\}").Groups[1].Value).ToList();
        var byId = texts.Distinct().GroupBy(t => Regex.Match(t, "^\\{=([a-z0-9_]+)\\}").Groups[1].Value).ToList();
        Check(ids.All(i => i.StartsWith("mnbl_")) && byId.All(g => g.Count() == 1) && contract.OfficialCultures.All(c => WorldSetupText.CultureNames[c].StartsWith("{=mnbl_ws_culture_")),
              "every player-facing string is localisation-ready: a {=mnbl_...} id with default text, each id naming one text (" + byId.Count + " ids, the official culture names included)");

        // The prefab against the view models.
        var prefab = XDocument.Load(Path.Combine(repo, "Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldSetup.xml"));
        var vmType = typeof(WorldSetupViewModel);
        var problems = new List<string>();
        // Every binding against the view model in scope (MMR-47: scope follows DataSource: a sub-view model, a list's item template, a key).
        Bindings(prefab.Root, vmType, problems);
        var idsInPrefab = new HashSet<string>(prefab.Descendants().Select(e => (string)e.Attribute("Id")).Where(i => i != null));
        foreach (var targeter in prefab.Descendants().Where(e => e.Name.LocalName == "NavigationScopeTargeter"))
            if (!idsInPrefab.Contains(((string)targeter.Attribute("ScopeParent")).Replace("..\\", ""))) problems.Add("scope parent " + (string)targeter.Attribute("ScopeParent"));
        int bindings = prefab.Descendants().SelectMany(e => e.Attributes()).Count(a => a.Value.StartsWith("@") || a.Name.LocalName.StartsWith("Command."));
        var seedBox = prefab.Descendants().FirstOrDefault(e => e.Name.LocalName == "EditableTextWidget" && (string)e.Attribute("RealText") == "@SeedText");
        Check(problems.Count == 0 && bindings > 30 && seedBox != null && (string)seedBox.Attribute("MaxLength") == "20" && prefab.Descendants().Any(e => (string)e.Attribute("DataSource") == "{RandomInputKey}"),
              "every binding and command of GUI/Prefabs/WorldSetup.xml (" + bindings + ") is a data-source property or method of its view model, the seed edit box is bound two-way with a length limit, Random World shows its key, and every navigation scope names a widget" + (problems.Count > 0 ? ": " + String.Join("; ", problems) : ""));
        // MMR-46: GUI/Prefabs/WorldPreview.xml against WorldPreviewViewModel (its lists' item templates against their item view models), used
        // by WorldSetup.xml with the setup's Preview as its data source; the setup's preview-state buttons.
        var previewPrefab = XDocument.Load(Path.Combine(repo, "Modules/MNBL.ProceduralWorld/GUI/Prefabs/WorldPreview.xml"));
        var previewProblems = new List<string>();
        Bindings(previewPrefab.Root, typeof(WorldPreviewViewModel), previewProblems);
        foreach (var element in previewPrefab.Descendants())
        {
            if (element.Name.LocalName == "NavigationScopeTargeter")
            {
                string parent = ((string)element.Attribute("ScopeParent") ?? "").Replace("..\\", "");
                if (!previewPrefab.Descendants().Any(e => (string)e.Attribute("Id") == parent)) previewProblems.Add("scope parent " + parent);
            }
        }
        var used = prefab.Descendants().Where(e => e.Name.LocalName == "WorldPreview").ToList();
        int previewBindings = previewPrefab.Descendants().SelectMany(e => e.Attributes()).Count(a => a.Value.StartsWith("@") || a.Name.LocalName.StartsWith("Command."));
        var texture = previewPrefab.Descendants().FirstOrDefault(e => e.Name.LocalName == "TextureWidget");
        Func<string, XElement> button = command => prefab.Descendants().FirstOrDefault(e => e.Name.LocalName == "ButtonWidget" && (string)e.Attribute("Command.Click") == command);
        Check(previewProblems.Count == 0 && used.Count == 1 && (string)used[0].Attribute("DataSource") == "{Preview}" && texture != null && (string)texture.Attribute("TextureProviderName") == "WorldPreviewTextureProvider"
              && (string)button("ExecuteStartCampaign").Attribute("IsEnabled") == "@CanStartCampaign" && (string)button("ExecuteStartCampaign").Attribute("IsVisible") == "@IsPreviewShown"
              && (string)button("ExecuteNewSeed").Attribute("IsVisible") == "@IsPreviewShown" && (string)button("ExecuteGenerate").Attribute("IsVisible") == "@IsEditing"
              && button("ExecuteStartCampaign").Descendants().Any(e => (string)e.Attribute("DataSource") == "{DoneInputKey}") && button("ExecuteNewSeed").Descendants().Any(e => (string)e.Attribute("DataSource") == "{RandomInputKey}"),
              "every binding and command of GUI/Prefabs/WorldPreview.xml (" + previewBindings + ") is a property or method of its view model or of its list's item view model; WorldSetup.xml uses it once with the setup's preview, draws the raster through WorldPreviewTextureProvider, and shows Start Campaign (Confirm's key, enabled only for a playable world) and New Seed (Randomize's key) in place of Generate while the preview is shown" + (previewProblems.Count > 0 ? ": " + String.Join("; ", previewProblems) : ""));
        try { Directory.Delete(split, true); } catch (IOException) { }
    }

    // A prefab's bindings against the view models in scope. An element with DataSource="{X}" binds its own attributes and its children against
    // X (a sub-view model, or an input key); a list's ItemTemplate binds against the list's item type; a list element binds nothing itself
    // (Gauntlet reads its other attributes against the list: run 43992's settings shown under the preview). Each @Property must be a
    // [DataSourceProperty] and each Command.* a method of the view model in scope.
    private static void Bindings(XElement element, Type context, List<string> problems)
    {
        Type own = context;
        string dataSource = (string)element.Attribute("DataSource");
        if (dataSource != null)
        {
            var p = context == null ? null : context.GetProperty(dataSource.Trim('{', '}'));
            if (p == null) { problems.Add("DataSource " + dataSource + " is not a property of " + (context == null ? "?" : context.Name)); own = null; }
            else own = p.PropertyType;
        }
        bool list = own != null && own.IsGenericType && own.GetGenericTypeDefinition().Name.StartsWith("MBBindingList", StringComparison.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            string v = attribute.Value, name = attribute.Name.LocalName;
            if (name == "DataSource") continue;
            if (!v.StartsWith("@") && !name.StartsWith("Command.")) continue;
            if (own == null || list) { problems.Add(element.Name.LocalName + " " + name + "=" + v + (list ? " on a list element (binds against the list)" : "")); continue; }
            if (v.StartsWith("@")) { var p = own.GetProperty(v.Substring(1)); if (p == null || !p.GetCustomAttributes().Any(a => a.GetType().Name == "DataSourceProperty")) problems.Add(element.Name.LocalName + " " + name + "=" + v + " (" + own.Name + ")"); }
            else if (own.GetMethod(v) == null) problems.Add(element.Name.LocalName + " " + name + "=" + v + " (" + own.Name + ")");
        }
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "ItemTemplate")
            {
                if (!list) { problems.Add("ItemTemplate under " + element.Name.LocalName + " without a list data source"); continue; }
                foreach (var item in child.Elements()) Bindings(item, own.GetGenericArguments()[0], problems);
            }
            else Bindings(child, own, problems);
        }
    }

    // As the setup's runner builds it in the game, from the bundle's campaign records, its surface, its layout and FactionFactory.
    private static WorldPreview BuildPreview(string bundleDir, string repo, IDictionary<string, object> layout)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        Func<byte[], IDictionary<string, object>> parse = bytes => (IDictionary<string, object>)json.DeserializeObject(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'));
        var bundle = WorldDataBundle.Open(bundleDir, new WorldDataBundle.HostProfile("v1.4.8.119303", "v1.2.8.119303", new[] { "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10" }));
        var campaign = parse(bundle.ReadVerified("compiled/campaign/campaign.json"));
        var idMap = parse(bundle.ReadVerified("compiled/campaign/id-map.json"));
        var templates = Directory.GetFiles(Path.Combine(repo, "worldgen/templates/cultures"), "*.json").Where(f => Path.GetFileName(f) != "contract.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => parse(File.ReadAllBytes(f)), StringComparer.Ordinal);
        var factions = FactionFactory.Build(campaign, idMap, parse(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/factions.json"))), parse(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/culture-presentations.json"))), templates);
        var binding = VillageBinding.FromRecords(parse(File.ReadAllBytes(Path.Combine(repo, "worldgen/policies/economy.json"))), campaign, parse(bundle.ReadVerified("compiled/travel/travel-graph.json")), new List<string>());
        return WorldPreview.Build(WorldSurface.FromBundle(bundle), layout, campaign, factions, "scene of " + bundle.WorldContentId.Substring(0, 12), binding.BoundOf);
    }

    private sealed class FixedRng : RandomNumberGenerator
    {
        private readonly uint value;
        public FixedRng(uint value) { this.value = value; }
        public override void GetBytes(byte[] data) { var b = BitConverter.GetBytes(value); for (int i = 0; i < data.Length; i++) data[i] = b[i % 4]; }
    }

    // The start's outcome reaches the model through a continuation (as the activation's does): tick until it has.
    private static void WaitStart(WorldSetupModel model)
    {
        for (int i = 0; i < 1000 && model.State == SetupState.Starting; i++) { Thread.Sleep(5); model.Tick(); }
    }

    private static void Wait(WorldSetupModel model)
    {
        for (int i = 0; i < 1000 && model.State == SetupState.Generating; i++) { Thread.Sleep(10); model.Tick(); }
    }

    private sealed class ScriptedRunner : IWorldGenerationRunner
    {
        public string Split; public Exception Fail; public bool AutoRun, FailOnCancel;
        public readonly ManualResetEventSlim Go = new ManualResetEventSlim(false);
        public readonly List<WorldChoice> Choices = new List<WorldChoice>();
        public readonly List<string> Released = new List<string>();
        public string LastJob;
        private Action<ProgressEvent> stage;
        private long sequence;
        public ProgressEvent Stage(JobPhase phase, int? completed, int? total)
        {
            for (int i = 0; i < 200 && stage == null; i++) Thread.Sleep(5);
            var e = new ProgressEvent { JobId = LastJob, Sequence = sequence++, Phase = phase, Completed = completed, Total = total, MessageCode = "progress" };
            stage(e);
            return e;
        }
        public void Emit(ProgressEvent e) { stage(e); }
        // Like the coordinator: one job at a time, a job holding its files until it is released.
        private string active;
        public string Run(WorldChoice choice, Action<ProgressEvent> onProgress, Action<string, string> onJobCreated, CancellationToken token)
        {
            if (FailBeforeJob && Fail != null) throw Fail;
            lock (Released) { if (active != null) throw new InvalidOperationException("busy: " + active + " is not released"); active = "scripted-job-" + (Choices.Count + 1); }
            Choices.Add(choice); stage = onProgress; LastJob = "scripted-job-" + Choices.Count; sequence = 0; onJobCreated(LastJob, LastJob);
            while (!AutoRun && !Go.IsSet)
            {
                if (token.IsCancellationRequested) { if (FailOnCancel) throw Fail; token.ThrowIfCancellationRequested(); }
                Thread.Sleep(5);
            }
            if (Fail != null) throw Fail;
            token.ThrowIfCancellationRequested();
            return Split;
        }
        public GenerationError Classify(Exception error, string jobRoot) { return GenerationErrors.FromGeneration(error is OperationCanceledException ? "cancelled" : "reader_failed", error, null, jobRoot); }
        public void Release(string jobRoot) { lock (Released) { Released.Add(jobRoot); if (active == jobRoot) active = null; } }
        // MMR-46: the preview of the job's world, and the start the test completes (TaskCompletionSource) as the activation would.
        public WorldPreview PreviewResult; public Exception PreviewFail; public int Previews;
        public readonly List<string> PreviewJobs = new List<string>();
        public WorldPreview Preview(string jobRoot) { Previews++; PreviewJobs.Add(jobRoot); if (PreviewFail != null) throw PreviewFail; return PreviewResult; }
        public readonly List<string> StartedJobs = new List<string>();
        public TaskCompletionSource<StartOutcome> Start;
        public Task<StartOutcome> StartCampaign(string jobRoot)
        {
            StartedJobs.Add(jobRoot); Start = new TaskCompletionSource<StartOutcome>();
            // Like the runner: a committed job is released by the activation's side, not the setup.
            var job = jobRoot;
            return Start.Task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion && t.Result.Kind == "playing") lock (Played) Played.Add(job); return t.Result; }, TaskScheduler.Default);
        }
        public readonly List<string> Played = new List<string>();
        // MMR-47: jobs a stopped process left, and discards.
        public List<WorldRecoveryService.UnfinishedJob> UnfinishedJobs = new List<WorldRecoveryService.UnfinishedJob>();
        public readonly List<string> Discarded = new List<string>();
        public string DiscardRefusal;
        public bool FailBeforeJob;   // Fail is thrown before any job is created (the coordinator busy)
        public IList<WorldRecoveryService.UnfinishedJob> Unfinished() { return UnfinishedJobs; }
        public string Discard(WorldRecoveryService.UnfinishedJob job) { if (DiscardRefusal != null) return DiscardRefusal; Discarded.Add(job.Id); return null; }
    }
}
