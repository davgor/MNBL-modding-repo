using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-56: the first-test profile measured through the player's own path (first_test_profile; seedA, seedRejected, seedB, culture):
    //   A. the New Campaign setup screen with seed A: Confirm, the generation job to review (its phase timings, the producer and bridge
    //      receipts, the bundle's own timings), Start Campaign with character creation driven as a player drives it, the activation's steps
    //      (object construction, the navigation caches, the initial save), the campaign map's frames paused, at play speed and in fast
    //      forward (frame intervals, simulation rate), a manual save and its load through the normal load entry, each timed;
    //   R. the profile's next seed (seedRejected): Confirm, and how the generation refuses it (its attempts and codes);
    //   B. seed B to review only (its activation is refused by the village binding, docs/world-loading.md): the same job record;
    // and for A and B the bundle's geography (height, mask and cell hashes, land and sea cells, the coastline, land components) and the
    // in-engine route matrix the job's runtime validation measured. The game process's memory is sampled four times a second and kept per
    // phase; the store's directories are measured after each step. Every number is recorded; tools/summarize_first_test.py is the verdict.
    internal static class FirstTestProfileProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-56: the F03 first-test profile measured end to end through the New Campaign path, and A/B seed differentiation from the same build.";
            uint seedA = (uint)context.Int("seedA", 42), seedRejected = (uint)context.Int("seedRejected", 43), seedB = (uint)context.Int("seedB", 73);
            string culture = context.Str("culture", "empire");
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            var memory = new MemorySampler();
            Action checkpoint = () => { context.Outputs["memory"] = memory.Snapshot(); context.Checkpoint(); };
            context.Outputs["machine"] = Machine();
            var disk = new List<object>(); context.Outputs["disk"] = disk;
            Action<string> measureDisk = at => disk.Add(Disk(at));
            measureDisk("start");
            try
            {
                // ---------------------------------------------------------------- A: request to play
                var a = new Dictionary<string, object> { ["seed"] = seedA }; context.Outputs["A"] = a;
                memory.Phase = "setup";
                var screen = OpenSetup(context);
                yield return screen.Wait;
                var vm = WorldSetupScreen.Current.ViewModel; var model = vm.Model;
                var runner = Runner(model);
                context.Outputs["build"] = Build(model);
                a["waitText"] = vm.WaitText;
                memory.Phase = "A.generate";
                var generate = new Dictionary<string, object>(); a["generate"] = generate;
                a["bundlesBefore"] = Directory.Exists(Path.Combine(SubModule.Store, "bundles")) ? Directory.GetDirectories(Path.Combine(SubModule.Store, "bundles")).Select(d => (object)Path.GetFileName(d)).ToList() : new List<object>();
                yield return Generate(context, vm, seedA, generate);
                if (model.State != SetupState.Generated) throw new InvalidOperationException("seed " + seedA + " did not generate: " + model.State + " " + model.Error);
                var job = runner.LastJob; string jobRoot = model.JobRoot;
                a["job"] = GenerationRuntime.Coordinator.Record(job);
                a["jobBytes"] = Bytes(jobRoot);
                a["geography"] = Geography(job); a["routes"] = Routes(job);
                measureDisk("A generated");
                checkpoint();

                memory.Phase = "A.start";
                var start = new Dictionary<string, object>(); a["start"] = start;
                vm.ExecuteStartCampaign();
                context.Stage("first_test_profile:start");
                var creation = new NewCampaignFlowProbe.CreationDriver(culture, "MNBL Profile");
                double t0 = context.Seconds, gameAt = -1, creationDoneAt = -1, mapAt = -1, creationSeconds = 0, last = context.Seconds;
                var startFrames = new FrameStats(); var longGaps = new List<object>();
                while (model.State == SetupState.Starting && context.Seconds - t0 < 3300)
                {
                    // Every frame's length: the engine thread's responsiveness through the start (a gap is time the game drew nothing and took
                    // no input), each gap over a second recorded with what was on screen; the time the creation screen was up is the player's.
                    double gap = context.Seconds - last; last = context.Seconds; startFrames.Add(gap);
                    string screenNow = TaleWorlds.ScreenSystem.ScreenManager.TopScreen == null ? null : TaleWorlds.ScreenSystem.ScreenManager.TopScreen.GetType().Name;
                    string stateNow = Game.Current == null ? null : Game.Current.GameStateManager.ActiveState == null ? null : Game.Current.GameStateManager.ActiveState.GetType().Name;
                    if (gap > 1 && longGaps.Count < 50) longGaps.Add(new Dictionary<string, object> { ["after"] = context.Seconds - t0 - gap, ["seconds"] = gap, ["screen"] = screenNow, ["state"] = stateNow });
                    if (stateNow == "CharacterCreationState") creationSeconds += gap;
                    if (Game.Current != null)
                    {
                        if (gameAt < 0) gameAt = context.Seconds - t0;
                        string step = creation.StepToMap();
                        if (step != null) context.Stage("first_test_profile:" + step);
                        if (creationDoneAt < 0 && !(Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState) && creation.Stages.Count > 0) creationDoneAt = context.Seconds - t0;
                        if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState)
                        {
                            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                            if (mapAt < 0) mapAt = context.Seconds - t0;
                        }
                    }
                    yield return null;
                }
                start["seconds"] = context.Seconds - t0; start["gameAfter"] = gameAt; start["creationLeftAfter"] = creationDoneAt; start["mapAfter"] = mapAt;
                start["frames"] = startFrames.Record(); start["longGaps"] = longGaps; start["creationSeconds"] = creationSeconds;
                start["state"] = model.State.ToString(); start["creation"] = creation.Record();
                var stage = runner.LastStage;
                start["error"] = stage == null || stage.Error == null ? null : stage.Error.GetType().Name + ": " + stage.Error.Message;
                start["host"] = stage == null || stage.Host == null ? null : stage.Host.Record;
                if (model.State != SetupState.Started || Campaign.Current == null) throw new InvalidOperationException("the campaign did not start: " + model.State + " " + start["error"]);
                measureDisk("A started");
                checkpoint();

                memory.Phase = "A.map";
                var map = new Dictionary<string, object>(); a["map"] = map;
                yield return Map(context, map);
                measureDisk("A played");
                checkpoint();

                memory.Phase = "A.save";
                var save = new Dictionary<string, object>(); a["save"] = save;
                string saveName = "mnbl_probe56_A_" + context.ProcessId;
                yield return Save(context, saveName, save);
                measureDisk("A saved");
                checkpoint();
                memory.Phase = "A.end";
                yield return TwoWorldMatrixProbe.EndGame(context);

                memory.Phase = "A.load";
                var load = new Dictionary<string, object>(); a["load"] = load;
                yield return Load(context, saveName, load);
                if (Game.Current != null) { memory.Phase = "A.end"; yield return TwoWorldMatrixProbe.EndGame(context); }
                checkpoint();

                // ---------------------------------------------------------------- R and B: generation only
                memory.Phase = "setup";
                screen = OpenSetup(context);
                yield return screen.Wait;
                vm = WorldSetupScreen.Current.ViewModel; model = vm.Model; runner = Runner(model);
                var r = new Dictionary<string, object> { ["seed"] = seedRejected }; context.Outputs["R"] = r;
                memory.Phase = "R.generate";
                var rg = new Dictionary<string, object>(); r["generate"] = rg;
                yield return Generate(context, vm, seedRejected, rg);
                r["job"] = runner.LastJob == null ? null : GenerationRuntime.Coordinator.Record(runner.LastJob);
                if (model.State == SetupState.Generated)
                {
                    r["geography"] = Geography(runner.LastJob); r["routes"] = Routes(runner.LastJob);
                    vm.ExecuteBack(); yield return TwoWorldMatrixProbe.Frames(10);
                }
                checkpoint();

                var b = new Dictionary<string, object> { ["seed"] = seedB }; context.Outputs["B"] = b;
                memory.Phase = "B.generate";
                var bg = new Dictionary<string, object>(); b["generate"] = bg;
                yield return Generate(context, vm, seedB, bg);
                b["job"] = runner.LastJob == null ? null : GenerationRuntime.Coordinator.Record(runner.LastJob);
                if (model.State == SetupState.Generated)
                {
                    b["jobBytes"] = Bytes(model.JobRoot);
                    b["geography"] = Geography(runner.LastJob); b["routes"] = Routes(runner.LastJob);
                    measureDisk("B generated");
                }
                else failures.Add("seed " + seedB + " did not generate: " + model.State + " " + model.Error);
                checkpoint();

                memory.Phase = "close";
                for (int i = 0; i < 4 && WorldSetupScreen.Current != null; i++) { WorldSetupScreen.Current.ViewModel.ExecuteBack(); yield return TwoWorldMatrixProbe.Frames(20); }
                context.Outputs["setupClosed"] = WorldSetupScreen.Current == null;
                measureDisk("end");
            }
            finally
            {
                memory.Stop();
                context.Outputs["memory"] = memory.Snapshot();
                using (var p = Process.GetCurrentProcess()) { context.Outputs["peakWorkingSetBytes"] = p.PeakWorkingSet64; context.Outputs["peakPagedBytes"] = p.PeakPagedMemorySize64; context.Outputs["peakVirtualBytes"] = p.PeakVirtualMemorySize64; }
            }
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("First-test profile expectations failed: " + String.Join("; ", failures));
        }

        // ---------------------------------------------------------------- the setup screen

        private sealed class Opened { public IEnumerator Wait; }

        private static Opened OpenSetup(ProbeContext context)
        {
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            return new Opened { Wait = WaitOpen(context) };
        }

        private static IEnumerator WaitOpen(ProbeContext context)
        {
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            yield return TwoWorldMatrixProbe.Frames(10);
        }

        private static CoordinatorRunner Runner(WorldSetupModel model)
        {
            return (CoordinatorRunner)typeof(WorldSetupModel).GetField("runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(model);
        }

        // The seed typed, Confirm through the screen's mapping, then the wait the player sees: the job's seconds and the longest frame (the
        // setup screen stays responsive while the job runs off the engine thread).
        private static IEnumerator Generate(ProbeContext context, WorldSetupViewModel vm, uint seed, Dictionary<string, object> record)
        {
            var model = vm.Model;
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            record["confirm"] = WorldSetupScreen.Current.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            context.Stage("first_test_profile:generate " + seed);
            var frames = new FrameStats();
            double start = context.Seconds, last = context.Seconds, sized = context.Seconds; long peakJob = 0;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200)
            {
                yield return null; frames.Add(context.Seconds - last); last = context.Seconds;
                // The job directory's size every two seconds (its largest is the generation's temporary disk peak).
                if (context.Seconds - sized > 2 && model.JobRoot != null) { sized = context.Seconds; peakJob = Math.Max(peakJob, Bytes(model.JobRoot)); }
            }
            if (model.JobRoot != null) peakJob = Math.Max(peakJob, Bytes(model.JobRoot));
            record["seconds"] = context.Seconds - start; record["state"] = model.State.ToString(); record["frames"] = frames.Record(); record["peakJobBytes"] = peakJob;
            record["producerRuns"] = ProducerRuns(model.JobRoot);
            record["error"] = model.Error == null ? null : new Dictionary<string, object> { ["category"] = model.Error.Category.ToString(), ["code"] = model.Error.Code, ["detail"] = model.Error.Detail };
            record["previewError"] = model.PreviewError;
        }

        // The producer's own runs, as worldbridge --generate records them (run-record.json per attempt: fwg.exe in its job object, its elapsed
        // time, the job's peak memory, its process count); worldbridge's receipt is only the supervising process.
        private static List<object> ProducerRuns(string jobRoot)
        {
            var rows = new List<object>();
            try
            {
                if (jobRoot == null || !Directory.Exists(jobRoot)) return rows;
                var json = new JavaScriptSerializer();
                foreach (var file in Directory.EnumerateFiles(jobRoot, "run-record.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var r = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(file));
                    Func<string, object> get = k => r.ContainsKey(k) ? r[k] : null;
                    rows.Add(new Dictionary<string, object> { ["run"] = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))), ["elapsedMilliseconds"] = get("elapsedMilliseconds"),
                        ["peakJobMemoryBytes"] = get("peakJobMemoryBytes"), ["totalProcesses"] = get("totalProcesses"), ["exitCode"] = get("exitCode"), ["outcome"] = get("outcome") });
                }
            }
            catch (Exception e) { rows.Add(new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }); }
            return rows;
        }

        // ---------------------------------------------------------------- the campaign map

        // Frames paused, at play speed and in fast forward, with the simulation's rate; the player sheltered for the day (recorded). Play speed is
        // UnstoppablePlay: StoppablePlay holds time while the player's party stands idle (run 17872: 0 campaign hours in 20 s).
        private static IEnumerator Map(ProbeContext context, Dictionary<string, object> record)
        {
            var campaign = Campaign.Current; var main = MobileParty.MainParty;
            main.IgnoreByOtherPartiesTill(CampaignTime.Now + CampaignTime.Days(2)); record["playerIgnoredDays"] = 2;
            record["parties"] = MobileParty.All.Count; record["settlements"] = TaleWorlds.CampaignSystem.Settlements.Settlement.All.Count;
            campaign.TimeControlMode = CampaignTimeControlMode.Stop;
            yield return TwoWorldMatrixProbe.Frames(60);
            foreach (var mode in new[] { CampaignTimeControlMode.Stop, CampaignTimeControlMode.UnstoppablePlay, CampaignTimeControlMode.UnstoppableFastForward })
            {
                var frames = new FrameStats(); double hours = CampaignTime.Now.ToHours, start = context.Seconds, last = context.Seconds;
                double seconds = mode == CampaignTimeControlMode.UnstoppableFastForward ? 60 : 20;
                while (context.Seconds - start < seconds)
                {
                    if (mode != CampaignTimeControlMode.Stop) MNBL.ProceduralWorld.CampaignBootstrap.DiagnosticSandboxBootstrap.Run(campaign, mode);
                    else campaign.TimeControlMode = CampaignTimeControlMode.Stop;
                    yield return null; frames.Add(context.Seconds - last); last = context.Seconds;
                    if (mode == CampaignTimeControlMode.UnstoppableFastForward && CampaignTime.Now.ToHours - hours >= 24) break;
                }
                double real = context.Seconds - start, advanced = CampaignTime.Now.ToHours - hours;
                var row = frames.Record(); row["realSeconds"] = real; row["campaignHours"] = advanced; row["hoursPerSecond"] = real > 0 ? advanced / real : 0;
                row["menu"] = campaign.CurrentMenuContext == null || campaign.CurrentMenuContext.GameMenu == null ? null : campaign.CurrentMenuContext.GameMenu.StringId;
                record[mode.ToString()] = row;
                Diagnostics.RuntimeMissionProbe.Pause();
                yield return TwoWorldMatrixProbe.Frames(10);
            }
            record["partiesAfter"] = MobileParty.All.Count;
        }

        // ---------------------------------------------------------------- save and load

        private static IEnumerator Save(ProbeContext context, string name, Dictionary<string, object> record)
        {
            record["name"] = name;
            var before = WorldSaveBinding.Last;
            double start = context.Seconds;
            Campaign.Current.SaveHandler.SaveAs(name);
            yield return null;
            while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            record["seconds"] = context.Seconds - start;
            var journal = WorldSaveBinding.Last;
            record["published"] = journal != null && journal != before && journal.SaveName == name && journal.State == EarlyWorldBindingStore.Published;
            var info = MBSaveLoad.GetSaveFileWithName(name);
            record["listed"] = info != null;
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Game Saves", name + ".sav");
            record["bytes"] = File.Exists(file) ? (object)new FileInfo(file).Length : null;
        }

        private static IEnumerator Load(ProbeContext context, string name, Dictionary<string, object> record)
        {
            var info = MBSaveLoad.GetSaveFileWithName(name);
            record["save"] = name;
            if (info == null) { record["loaded"] = false; record["error"] = "no save " + name; yield break; }
            var previous = GeneratedWorldLoader.Current;
            bool vanilla = false;
            double start = context.Seconds;
            SandBox.SandBoxSaveHelper.TryLoadSave(info, x => vanilla = true, () => { });
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            record["loaderSeconds"] = context.Seconds - start; record["vanillaStarted"] = vanilla;
            record["loader"] = loader == null ? null : loader.Report;
            bool loaded = loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla;
            if (loaded)
            {
                while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 3700) yield return null;
                record["mapSeconds"] = context.Seconds - start;
                Diagnostics.RuntimeMissionProbe.Pause();
                var frames = new FrameStats(); double t = context.Seconds, last = context.Seconds;
                while (context.Seconds - t < 10) { yield return null; frames.Add(context.Seconds - last); last = context.Seconds; }
                record["mapFrames"] = frames.Record();
            }
            record["loaded"] = loaded;
        }

        // ---------------------------------------------------------------- the world's geography and routes

        // The bundle's compiled terrain and travel cells: the files' hashes; the cell classes (MNBLTC01: cols, rows, class bytes, OpenSea flags)
        // counted; the coastline as the land and mountain cells with a sea, lake or river 4-neighbour (columns wrap), its mask hashed; land
        // components of land and mountain cells (4-neighbour, wrapping); the compiler's own land components; the class grid itself (base64) so
        // A and B can be compared cell by cell.
        private static Dictionary<string, object> Geography(GenerationJob job)
        {
            var record = new Dictionary<string, object>();
            try
            {
                string dir = job.Get<string>("bundleDirectory");
                record["worldContentId"] = job.Get<string>("worldContentId");
                Func<string, string> sha = rel => { string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(p) ? Diagnostics.GenerationProbe.Hash(p) : null; };
                record["files"] = new Dictionary<string, object> { ["height-q16.bin"] = sha("compiled/terrain/height-q16.bin"), ["heightfield.bin"] = sha("compiled/terrain/heightfield.bin"),
                    ["masks.bin"] = sha("compiled/terrain/masks.bin"), ["cells.bin"] = sha("compiled/travel/cells.bin"), ["travel-graph.json"] = sha("compiled/travel/travel-graph.json") };
                var bytes = File.ReadAllBytes(Path.Combine(dir, "compiled", "travel", "cells.bin"));
                if (System.Text.Encoding.ASCII.GetString(bytes, 0, 8) != "MNBLTC01") throw new InvalidDataException("cells.bin is not MNBLTC01");
                int cols = BitConverter.ToInt32(bytes, 8), rows = BitConverter.ToInt32(bytes, 12), n = cols * rows;
                var cls = new byte[n]; Array.Copy(bytes, 16, cls, 0, n);
                int openSea = 0; for (int i = 0; i < n && 16 + n + i < bytes.Length; i++) if (bytes[16 + n + i] != 0) openSea++;
                string[] names = { "land", "mountain", "sea", "lake", "river" };
                var counts = names.ToDictionary(x => x, x => (object)0);
                foreach (var c in cls) { string k = c < names.Length ? names[c] : "other" + c; counts[k] = (counts.ContainsKey(k) ? (int)counts[k] : 0) + 1; }
                Func<int, bool> landish = i => cls[i] == 0 || cls[i] == 1;
                Func<int, IEnumerable<int>> neighbours = i => { int r = i / cols, c = i % cols; var list = new List<int> { r * cols + (c + cols - 1) % cols, r * cols + (c + 1) % cols }; if (r > 0) list.Add(i - cols); if (r < rows - 1) list.Add(i + cols); return list; };
                var coast = new byte[n]; int coastCells = 0;
                for (int i = 0; i < n; i++) if (landish(i) && neighbours(i).Any(j => !landish(j))) { coast[i] = 1; coastCells++; }
                var component = Enumerable.Repeat(-1, n).ToArray(); var sizes = new List<int>();
                for (int s = 0; s < n; s++)
                {
                    if (!landish(s) || component[s] >= 0) continue;
                    var queue = new Queue<int>(); queue.Enqueue(s); component[s] = sizes.Count; int size = 0;
                    while (queue.Count > 0) { int i = queue.Dequeue(); size++; foreach (int j in neighbours(i)) if (landish(j) && component[j] < 0) { component[j] = sizes.Count; queue.Enqueue(j); } }
                    sizes.Add(size);
                }
                record["grid"] = new Dictionary<string, object> { ["cols"] = cols, ["rows"] = rows };
                record["cells"] = counts; record["openSeaCells"] = openSea;
                record["landCells"] = cls.Count(c => c == 0 || c == 1); record["coastCells"] = coastCells;
                using (var h = SHA256.Create()) record["coastlineSha256"] = BitConverter.ToString(h.ComputeHash(coast)).Replace("-", "").ToLowerInvariant();
                record["landComponents"] = sizes.Count; record["largestLandComponent"] = sizes.Count == 0 ? 0 : sizes.Max();
                record["classes"] = Convert.ToBase64String(cls);
                var manifest = (IDictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(dir, "manifest.json")));
                var totals = manifest.ContainsKey("totals") ? manifest["totals"] as IDictionary<string, object> : null;
                record["bundleBytes"] = totals == null ? null : totals["bytes"]; record["bundleFiles"] = totals == null ? null : totals["files"];
                var validation = (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(dir, "compiled", "validation.json")));
                var vc = validation.ContainsKey("counts") ? validation["counts"] as IDictionary<string, object> : null;
                record["compilerCounts"] = vc == null ? null : vc.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        // The in-engine route matrix the job's runtime validation measured on the world's navigation mesh (the persisted navigation report):
        // its routes by type (reachable, found, path length), a hash of the matrix (every route's type, endpoints and length), the mesh's
        // faces by terrain type, and the playability routes.
        private static Dictionary<string, object> Routes(GenerationJob job)
        {
            var record = new Dictionary<string, object>();
            try
            {
                string scene = job.Get<string>("sceneName");
                string path = Path.Combine(SubModule.Store, "world-navigation", scene + ".json");
                record["sceneName"] = scene; record["navmeshSha256"] = job.Get<string>("navmeshSha256"); record["reportSha256"] = job.Get<string>("navigationReportSha256");
                record["playabilityRoutes"] = job.Artifacts.ContainsKey("playabilityRoutes") ? job.Artifacts["playabilityRoutes"] : null;
                record["playabilityRoutesFailed"] = job.Artifacts.ContainsKey("playabilityRoutesFailed") ? job.Artifacts["playabilityRoutesFailed"] : null;
                if (!File.Exists(path)) { record["error"] = "no persisted navigation report"; return record; }
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var wrapper = (IDictionary<string, object>)json.DeserializeObject(File.ReadAllText(path));
                var report = (IDictionary<string, object>)json.DeserializeObject((string)wrapper["report"]);
                record["reportIsTheJobs"] = Equals(wrapper.ContainsKey("reportSha256") ? wrapper["reportSha256"] : null, job.Get<string>("navigationReportSha256"));
                record["summary"] = report["summary"];
                var build = (IDictionary<string, object>)report["build"];
                record["facesByTerrainType"] = build["facesByTerrainType"]; record["faces"] = build["faces"];
                var routes = ((object[])((IDictionary<string, object>)report["engine"])["routes"]).Cast<IDictionary<string, object>>().ToList();
                var lines = new List<string>(); var byType = new SortedDictionary<string, object>(StringComparer.Ordinal); var lengths = new List<double>();
                foreach (var row in routes)
                {
                    string type = Convert.ToString(row["type"], CultureInfo.InvariantCulture);
                    bool reachable = Equals(row.ContainsKey("reachable") ? row["reachable"] : null, true);
                    double length = row.ContainsKey("pathLength") && row["pathLength"] != null ? Convert.ToDouble(row["pathLength"], CultureInfo.InvariantCulture) : -1;
                    Func<object, string> xy = o => { var p = (object[])o; return Convert.ToDouble(p[0], CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture) + "," + Convert.ToDouble(p[1], CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture); };
                    lines.Add(type + "|" + xy(row["from"]) + "|" + xy(row["to"]) + "|" + reachable + "|" + length.ToString("0.###", CultureInfo.InvariantCulture));
                    var t = byType.ContainsKey(type) ? (Dictionary<string, object>)byType[type] : null;
                    if (t == null) byType[type] = t = new Dictionary<string, object> { ["routes"] = 0, ["reachable"] = 0, ["lengthSum"] = 0.0 };
                    t["routes"] = (int)t["routes"] + 1;
                    if (reachable && length >= 0) { t["reachable"] = (int)t["reachable"] + 1; t["lengthSum"] = (double)t["lengthSum"] + length; lengths.Add(length); }
                }
                lines.Sort(StringComparer.Ordinal);
                using (var h = SHA256.Create()) record["matrixSha256"] = BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(String.Join("\n", lines)))).Replace("-", "").ToLowerInvariant();
                record["routes"] = routes.Count; record["byType"] = byType;
                lengths.Sort();
                record["reachableLength"] = lengths.Count == 0 ? null : new Dictionary<string, object> { ["count"] = lengths.Count, ["mean"] = lengths.Average(), ["median"] = lengths[lengths.Count / 2], ["max"] = lengths[lengths.Count - 1] };
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        // ---------------------------------------------------------------- machine, build, disk

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private class SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus([In, Out] SystemPowerStatus status);

        // The machine as Windows describes it (CPU name, logical processors, physical memory, display adapters, OS) and the game's own settings
        // files (engine_config.txt and BannerlordConfig.txt, as key = value).
        private static Dictionary<string, object> Machine()
        {
            var record = new Dictionary<string, object>();
            try
            {
                using (var cpu = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    record["cpu"] = cpu == null ? null : Convert.ToString(cpu.GetValue("ProcessorNameString"), CultureInfo.InvariantCulture).Trim();
                record["logicalProcessors"] = Environment.ProcessorCount;
                var status = new MemoryStatusEx();
                record["physicalMemoryBytes"] = GlobalMemoryStatusEx(status) ? (object)status.ullTotalPhys : null;
                var adapters = new List<object>();
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(Char.IsDigit)))
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null || k.GetValue("DriverDesc") == null) continue;
                                adapters.Add(new Dictionary<string, object> { ["name"] = Convert.ToString(k.GetValue("DriverDesc"), CultureInfo.InvariantCulture), ["driver"] = Convert.ToString(k.GetValue("DriverVersion"), CultureInfo.InvariantCulture) });
                            }
                record["displayAdapters"] = adapters;
                record["os"] = Environment.OSVersion.VersionString; record["is64Bit"] = Environment.Is64BitProcess;
                using (var nt = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (nt != null) record["windows"] = new Dictionary<string, object> { ["product"] = Convert.ToString(nt.GetValue("ProductName"), CultureInfo.InvariantCulture),
                        ["displayVersion"] = Convert.ToString(nt.GetValue("DisplayVersion"), CultureInfo.InvariantCulture), ["build"] = Convert.ToString(nt.GetValue("CurrentBuild"), CultureInfo.InvariantCulture) };
                var power = new SystemPowerStatus();
                if (GetSystemPowerStatus(power)) record["power"] = new Dictionary<string, object> { ["acLine"] = power.ACLineStatus == 1 ? "online" : power.ACLineStatus == 0 ? "battery" : "unknown", ["batteryPercent"] = power.BatteryLifePercent };
                using (var scheme = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
                    record["powerScheme"] = scheme == null ? null : Convert.ToString(scheme.GetValue("ActivePowerScheme"), CultureInfo.InvariantCulture);
                try { record["engineGpuMemoryMB"] = TaleWorlds.Engine.Utilities.GetGPUMemoryMB(); } catch (Exception e) { record["engineGpuMemoryMB"] = e.GetType().Name; }
                string configs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Configs");
                foreach (var file in new[] { "engine_config.txt", "BannerlordConfig.txt" })
                {
                    string p = Path.Combine(configs, file);
                    if (!File.Exists(p)) { record[file] = null; continue; }
                    var settings = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    foreach (var line in File.ReadAllLines(p)) { int eq = line.IndexOf('='); if (eq > 0) settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim(); }
                    record[file] = settings;
                }
                var store = new DriveInfo(Path.GetPathRoot(SubModule.Store));
                record["storeDrive"] = new Dictionary<string, object> { ["name"] = store.Name, ["format"] = store.DriveFormat, ["totalBytes"] = store.TotalSize, ["freeBytes"] = store.AvailableFreeSpace };
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        // The build that ran: the module, the producer the setup's contract pins, the game and War Sails, and the engine binaries (by name).
        private static Dictionary<string, object> Build(WorldSetupModel model)
        {
            var record = new Dictionary<string, object>();
            try
            {
                record["moduleAssemblySha256"] = Diagnostics.GenerationProbe.Hash(typeof(SubModule).Assembly.Location);
                record["producerSha256"] = model.Contract.ProducerSha256; record["producerToolsLockSha256"] = model.Contract.ProfileToolsLockSha256;
                record["recipeVersion"] = model.Contract.RecipeVersion; record["defaultSeed"] = model.Contract.DefaultSeed; record["qualityAttempts"] = model.Contract.QualityAttempts;
                record["overrides"] = model.Contract.Overrides.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                var host = WorldSetupHost.BundleHost();
                record["game"] = host.GameVersion; record["navalDlc"] = host.NavalVersion;
                string bridge = Path.Combine(Path.GetDirectoryName(typeof(SubModule).Assembly.Location), "worldbridge.exe");
                record["worldbridgeSha256"] = File.Exists(bridge) ? Diagnostics.GenerationProbe.Hash(bridge) : null;
                record["binaries"] = new SortedDictionary<string, object>(new[] { typeof(TaleWorlds.Engine.Scene), typeof(Game), typeof(Campaign), typeof(TaleWorlds.MountAndBlade.Module), typeof(SandBox.SandBoxSubModule), typeof(NavalDLC.NavalDLCSubModule) }
                    .Select(t => t.Assembly.Location).Distinct().ToDictionary(p => Path.GetFileName(p), p => (object)Diagnostics.GenerationProbe.Hash(p)), StringComparer.Ordinal);
                // The native engine (what the shared install's other deploys can replace), next to the managed engine.
                string bin = Path.GetDirectoryName(typeof(TaleWorlds.Engine.Scene).Assembly.Location);
                record["nativeBinaries"] = new SortedDictionary<string, object>(new[] { "Bannerlord.exe", "TaleWorlds.Native.dll" }.ToDictionary(n => n, n => { string p = Path.Combine(bin, n); return (object)(File.Exists(p) ? Diagnostics.GenerationProbe.Hash(p) : null); }), StringComparer.Ordinal);
                // The setup documents the module embeds (the profile, its budget, the producer lock), as the setup reads them.
                var assembly = typeof(SubModule).Assembly; var docs = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var name in new[] { "one-shot.json", "one-shot-budget.json", "producer.lock.json", "product-policy.json" })
                    using (var stream = assembly.GetManifestResourceStream("MNBL.ProceduralWorld." + name))
                    using (var sha = SHA256.Create())
                        docs[name] = stream == null ? null : BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                record["embeddedDocuments"] = docs;
            }
            catch (Exception e) { record["error"] = e.GetType().Name + ": " + e.Message; }
            return record;
        }

        // The store's directories and the saves, in bytes, at a step.
        private static Dictionary<string, object> Disk(string at)
        {
            var record = new Dictionary<string, object> { ["at"] = at };
            foreach (var dir in new[] { "bundles", "jobs", "worlds", "activations", "navigation-caches", "world-navigation", "world-playability", "world-surfaces", "world-settlements", "worldsaves", "saves", "diagnostics", "previews" })
                record[dir] = Bytes(Path.Combine(SubModule.Store, dir));
            record["store"] = Bytes(SubModule.Store);
            record["gameSaves"] = Bytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "Game Saves"));
            try { var d = new DriveInfo(Path.GetPathRoot(SubModule.Store)); record["freeBytes"] = d.AvailableFreeSpace; } catch (Exception) { }
            return record;
        }

        private static long Bytes(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return 0;
            long total = 0;
            try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch (IOException) { } } }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return total;
        }

        // ---------------------------------------------------------------- samplers

        // Frame intervals: count, mean, median, 95th and 99th percentile, the longest, and frames per second from the mean.
        private sealed class FrameStats
        {
            private readonly List<double> gaps = new List<double>();
            public void Add(double seconds) { if (seconds > 0) gaps.Add(seconds); }
            public Dictionary<string, object> Record()
            {
                var s = gaps.OrderBy(x => x).ToList();
                if (s.Count == 0) return new Dictionary<string, object> { ["frames"] = 0 };
                Func<double, double> p = q => s[Math.Min(s.Count - 1, (int)Math.Floor(q * s.Count))];
                double mean = s.Average();
                return new Dictionary<string, object> { ["frames"] = s.Count, ["meanSeconds"] = mean, ["medianSeconds"] = p(0.5), ["p95Seconds"] = p(0.95), ["p99Seconds"] = p(0.99), ["maxSeconds"] = s[s.Count - 1], ["fps"] = 1 / mean };
            }
        }

        // The game process's working set and private bytes, four times a second off the engine thread, kept as the largest per phase (with
        // the GC heap at the largest working set).
        private sealed class MemorySampler
        {
            private readonly Dictionary<string, object> Phases = new Dictionary<string, object>();
            private readonly System.Threading.Timer timer;
            private readonly object sync = new object();
            public volatile string Phase = "start";

            public MemorySampler() { timer = new System.Threading.Timer(_ => Sample(), null, 0, 250); }

            private void Sample()
            {
                try
                {
                    long ws, priv;
                    using (var p = Process.GetCurrentProcess()) { ws = p.WorkingSet64; priv = p.PrivateMemorySize64; }
                    string phase = Phase;
                    lock (sync)
                    {
                        var row = Phases.ContainsKey(phase) ? (Dictionary<string, object>)Phases[phase] : null;
                        if (row == null) Phases[phase] = row = new Dictionary<string, object> { ["maxWorkingSetBytes"] = 0L, ["maxPrivateBytes"] = 0L, ["samples"] = 0 };
                        row["samples"] = (int)row["samples"] + 1;
                        if (ws > (long)row["maxWorkingSetBytes"]) { row["maxWorkingSetBytes"] = ws; row["gcHeapBytesAtMax"] = GC.GetTotalMemory(false); }
                        if (priv > (long)row["maxPrivateBytes"]) row["maxPrivateBytes"] = priv;
                    }
                }
                catch (Exception) { }
            }

            public void Stop() { timer.Dispose(); }

            // A copy for the result, taken under the lock (the timer keeps writing the live rows).
            public Dictionary<string, object> Snapshot() { lock (sync) return Phases.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>((Dictionary<string, object>)kv.Value)); }
        }
    }
}
