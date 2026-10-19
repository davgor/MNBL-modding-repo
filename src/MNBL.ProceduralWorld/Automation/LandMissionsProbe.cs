using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Helpers;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using SandBox.Conversation.MissionLogics;
using SandBox.Missions.MissionLogics.Hideout;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-52: land missions and settlement interactions on the generated world (land_missions; scenarios=<repo>/tests/Runtime/LandMissionScenarios.json,
    // phase=prepare|run). prepare: world A (seed 42) is generated from the world setup screen and saved as the designated test save. run: the
    // designated save is loaded through the normal load entry (the world loader), then each case runs from it, through the installed paths:
    //   field battles: one per battle class present (the resolver's classes of the compiled biomes and face terrain), a crossing and a retreat:
    //     the player at a cell of the class, a hostile party beside it, PlayerEncounter.StartBattle and the encounter menu's attack option;
    //     the opened scene against the generated patch's (MMR-42 guard, this battle's own observation), deployment finished, every agent's
    //     spawn and gear checked, the mission ended (Mission.OnEndMissionResult; RetreatMission for the retreat), the return checked;
    //   settlements: a town's center with a notable or merchant conversation and its tavern interior; a castle's courtyard; a village's center;
    //   a siege assault with deployment; a defeat leading to capture and the escape; world B for the classes A lacks; a hideout cleared last.
    // A case that starts from anything but an idle map is preceded by a reload of the designated save. Diagnostic setup (relocations, wars,
    // troops, the castle given to the player, the siege camp's preparation advanced, the troop selection answered) is recorded with each case.
    // Every expectation is recomputed by tools/summarize_land_missions.py from the outputs; that summary is the verdict.
    // The harness members marked internal (State, Case, the mission, menu and return helpers) are shared with NavalMissionsProbe (MMR-53).
    internal static class LandMissionsProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        internal sealed class State
        {
            public ProbeContext Context;
            public Dictionary<string, object> Rules;
            public string Prefix, WorldId, Save;
            public List<string> Failures = new List<string>();
            public Dictionary<string, object> Cases = new Dictionary<string, object>();
            public Dictionary<string, Dictionary<string, object>> Cultures = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<string, object>> Tally; // the current world's culture tally: A's Cultures, or world B's own
            public List<string> MenusSeen = new List<string>();
            public Dictionary<string, object> MenuOptions = new Dictionary<string, object>(StringComparer.Ordinal);
            public string LogPath;
            public int Reloads;
            public bool NoReload; // world B: no designated save to reload
            public string Tag = "land"; // the probe's stage prefix and screenshot tag (MMR-53's naval_missions: "naval")
            public double Rule(string key) { return Convert.ToDouble(Rules[key], CultureInfo.InvariantCulture); }
            public List<string> RuleList(string key) { return ((IEnumerable)Rules[key]).Cast<object>().Select(o => (string)o).ToList(); }
        }

        private static IEnumerator Body(ProbeContext context)
        {
            string path = context.Str("scenarios", null);
            if (path == null || !File.Exists(path)) throw new InvalidOperationException("scenarios=<tests/Runtime/LandMissionScenarios.json> is required.");
            var plan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
            var world = (Dictionary<string, object>)plan["world"];
            var st = new State { Context = context, Rules = (Dictionary<string, object>)plan["rules"], Save = (string)world["designatedSave"] };
            st.Tally = st.Cultures;
            st.LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs", "rgl_log_" + context.ProcessId + ".txt");
            string phase = context.Str("phase", "run");
            context.Outputs["scope"] = "MMR-52: land missions and settlement interactions on generated world A (" + phase + ").";
            context.Outputs["phase"] = phase;
            context.Outputs["scenarioFile"] = new Dictionary<string, object> { ["schema"] = plan["schema"], ["sha256"] = GeneratedWorldPackage.Sha256(File.ReadAllBytes(path)) };
            context.Outputs["rules"] = st.Rules;
            context.Outputs["failures"] = st.Failures;
            context.Outputs["cases"] = st.Cases;
            context.Outputs["cultures"] = st.Cultures;
            context.Outputs["menus"] = st.MenuOptions;
            var baselines = new List<object>(); context.Outputs["baselines"] = baselines;
            Action<string> baseline = label =>
            {
                var held = WorldLifetimeScope.Held();
                baselines.Add(new Dictionary<string, object> { ["at"] = label, ["game"] = Game.Current != null, ["held"] = held.ToDictionary(k => k.Key, k => (object)k.Value), ["tracked"] = WorldLifetimeScope.Names.Count });
                if (Game.Current != null || held.Count > 0) st.Failures.Add("baseline " + label + ": a game or a tracked static remains");
            };
            baseline("start");

            // ---- the designated test save of world A
            bool exists = MBSaveLoad.GetSaveFileWithName(st.Save) != null;
            context.Outputs["designatedSaveExisted"] = exists;
            if (phase == "prepare" || !exists)
            {
                var prepare = new Dictionary<string, object>(); context.Outputs["prepare"] = prepare;
                var seeds = ((IEnumerable)world["seeds"]).Cast<object>().Select(o => Convert.ToUInt32(o, CultureInfo.InvariantCulture)).ToArray();
                yield return TwoWorldMatrixProbe.StartGenerated(context, prepare, "A", seeds, (string)world["culture"], "land_missions");
                var before = WorldSaveBinding.Last;
                Campaign.Current.SaveHandler.SaveAs(st.Save);
                double start = context.Seconds;
                yield return null;
                while (context.Seconds - start < 120 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
                var journal = WorldSaveBinding.Last;
                prepare["saveJournal"] = journal == null || journal == before ? null : journal.Report();
                prepare["saved"] = journal != null && journal != before && journal.SaveName == st.Save && journal.State == EarlyWorldBindingStore.Published;
                if (!(bool)prepare["saved"]) st.Failures.Add("prepare: the designated save " + st.Save + " was not published");
                yield return TwoWorldMatrixProbe.EndGame(context);
                baseline("after prepare");
                if (phase == "prepare") { context.Outputs["completed"] = true; context.Checkpoint(); if (st.Failures.Count > 0) throw new InvalidOperationException(String.Join("; ", st.Failures)); yield break; }
            }

            // ---- run: load the designated save, then the cases
            var load = new Dictionary<string, object>(); context.Outputs["load"] = load;
            yield return LoadDesignated(st, load);
            if (!Equals(load["loaded"], true)) { st.Failures.Add("the designated save did not load"); }
            else
            {
                var setup = Setup(st); context.Outputs["setup"] = setup;
                context.Outputs["locationAudit"] = Locations(st);
                context.Outputs["names"] = Names(st);
                context.Checkpoint();
                var candidates = Candidates(st); context.Outputs["terrain"] = candidates.Record;
                var cultures = Kingdom.All.Where(k => k.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && k.Culture != null).Select(k => k.Culture.StringId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                context.Outputs["culturesPresent"] = cultures;
                int cultureIndex = 0;
                Func<string> nextCulture = () => cultures.Count == 0 ? null : cultures[cultureIndex++ % cultures.Count];
                foreach (var cls in st.RuleList("battleClasses"))
                {
                    var candidate = candidates.ByClass.ContainsKey(cls) ? candidates.ByClass[cls] : null;
                    if (candidate == null) { st.Cases["field." + cls] = new Dictionary<string, object> { ["class"] = cls, ["absent"] = true }; continue; }
                    string culture = nextCulture();
                    yield return Case(st, "field." + cls, row => Field(st, row, candidate, culture, false));
                }
                if (candidates.Crossing != null) { string culture = nextCulture(); yield return Case(st, "crossing", row => Field(st, row, candidates.Crossing, culture, false)); }
                else st.Cases["crossing"] = new Dictionary<string, object> { ["absent"] = true };
                var open = candidates.ByClass.ContainsKey("open") ? candidates.ByClass["open"] : candidates.ByClass.Values.FirstOrDefault();
                if (open != null) yield return Case(st, "retreat", row => Field(st, row, open, nextCulture(), true));
                // Cultures not yet seen with a hero in a battle: one more battle each, at the open candidate.
                foreach (var culture in cultures)
                {
                    Dictionary<string, object> seen;
                    if (open == null || (st.Cultures.TryGetValue(culture, out seen) && (int)seen["heroes"] > 0 && (int)seen["troops"] > 0)) continue;
                    yield return Case(st, "gear." + culture, row => Field(st, row, open, culture, false));
                }
                yield return Case(st, "town", row => Town(st, row));
                yield return Case(st, "castle", row => Castle(st, row));
                yield return Case(st, "village", row => Village(st, row));
                yield return Case(st, "siege", row => Siege(st, row));
                yield return Case(st, "capture", row => Capture(st, row));
                // ---- world B for the battle classes and the crossing world A lacks
                var lacking = st.RuleList("battleClasses").Where(c => !candidates.ByClass.ContainsKey(c)).ToList();
                if (candidates.Crossing == null) lacking.Add("crossing");
                context.Outputs["lackingInA"] = lacking;
                // seedB= starts a second production world for them; else MMR-19's fixture world. fixtureB=false without seedB (a package-only
                // installation, which holds no fixture world): B's cases are recorded as blocked, and world A stays loaded for the hideout.
                bool runB = lacking.Count > 0 && (context.Int("seedB", 0) != 0 || context.Bool("fixtureB", true));
                if (runB) yield return WorldB(st, plan, path, lacking);
                else if (lacking.Count > 0) context.Outputs["worldB"] = new Dictionary<string, object> { ["for"] = lacking, ["blocked"] = TwoWorldMatrixProbe.FixtureBlockedReason };
                // The hideout last: the game hung at its boss conversation twice (runs 25280 and 17928), and a hang loses only what follows.
                // After world B, world A's designated save is loaded again for it.
                bool hideoutReady = true;
                if (runB)
                {
                    var reload = new Dictionary<string, object>(); context.Outputs["reloadForHideout"] = reload;
                    yield return LoadDesignated(st, reload);
                    st.Reloads++;
                    if (Equals(reload["loaded"], true)) { st.NoReload = false; st.Tally = st.Cultures; Setup(st); }
                    else { hideoutReady = false; st.Failures.Add("hideout: not run (world A's designated save did not load after world B)"); }
                }
                if (hideoutReady) yield return Case(st, "hideout", row => HideoutCase(st, row));
                context.Outputs["reloads"] = st.Reloads;
                context.Outputs["menusSeen"] = st.MenusSeen;
            }
            context.Checkpoint();
            if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
            baseline("end");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (st.Failures.Count > 0) throw new InvalidOperationException("Land mission expectations failed: " + String.Join("; ", st.Failures.Take(12)));
        }

        // ---------------------------------------------------------------- world B

        // World B for what world A lacks, started fresh after A ends: a second production world from the setup screen (seedB=), else MMR-19's
        // fixture world, its placement and catalog receipt named by the scenario file (relative to the repository); its cells sampled as A's;
        // one field battle per battle class A lacks and the crossing if A has none. Its cases are B.<case>; a case B lacks too is absent.
        private static IEnumerator WorldB(State st, Dictionary<string, object> plan, string scenarioPath, List<string> lacking)
        {
            var context = st.Context;
            var planB = (Dictionary<string, object>)plan["worldB"];
            var b = new Dictionary<string, object> { ["for"] = lacking }; context.Outputs["worldB"] = b;
            uint seedB = (uint)context.Int("seedB", 0);
            if (seedB != 0)
            {
                yield return TwoWorldMatrixProbe.EndGame(context);
                yield return TwoWorldMatrixProbe.StartGenerated(context, b, "B", new[] { seedB }, context.Str("culture", "empire"), "land_missions");
            }
            else
            {
                string repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(scenarioPath))));
                string placement = Path.Combine(repo, (string)planB["fixturePlacement"]), receipt = Path.Combine(repo, (string)planB["catalogReceipt"]);
                var missing = new[] { placement, receipt }.Where(f => !File.Exists(f)).Select(f => Path.GetFileName(f)).ToList();
                if (missing.Count > 0) { b["error"] = "missing input " + String.Join(", ", missing); st.Failures.Add("world B: its input is missing: " + String.Join(", ", missing)); yield break; }
                yield return TwoWorldMatrixProbe.EndGame(context);
                context.Args["fixtureSeed"] = planB["fixtureSeed"]; context.Args["fixturePlacement"] = placement; context.Args["catalogReceipt"] = receipt;
                yield return TwoWorldMatrixProbe.StartFixture(context, b, "B", "land_missions");
            }
            st.WorldId = (string)b["worldId"]; st.Prefix = (string)b["prefix"]; st.NoReload = true;
            st.Tally = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal); context.Outputs["culturesB"] = st.Tally;
            b["setup"] = Setup(st);
            var candidates = Candidates(st); context.Outputs["terrainB"] = candidates.Record;
            var cultures = Kingdom.All.Where(k => k.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && k.Culture != null).Select(k => k.Culture.StringId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            b["culturesPresent"] = cultures;
            int index = 0;
            foreach (var cls in lacking.Where(c => c != "crossing"))
            {
                Candidate candidate;
                if (!candidates.ByClass.TryGetValue(cls, out candidate)) { st.Cases["B.field." + cls] = new Dictionary<string, object> { ["class"] = cls, ["absent"] = true }; continue; }
                string culture = cultures.Count == 0 ? null : cultures[index++ % cultures.Count];
                yield return Case(st, "B.field." + cls, row => Field(st, row, candidate, culture, false));
            }
            if (lacking.Contains("crossing"))
            {
                if (candidates.Crossing == null) st.Cases["B.crossing"] = new Dictionary<string, object> { ["absent"] = true };
                else { string culture = cultures.Count == 0 ? null : cultures[index++ % cultures.Count]; yield return Case(st, "B.crossing", row => Field(st, row, candidates.Crossing, culture, false)); }
            }
        }

        // ---------------------------------------------------------------- the designated save and the cases

        internal static IEnumerator LoadDesignated(State st, Dictionary<string, object> row)
        {
            var context = st.Context;
            var info = MBSaveLoad.GetSaveFileWithName(st.Save);
            row["save"] = st.Save;
            if (info == null) { row["loaded"] = false; row["error"] = "no save " + st.Save; yield break; }
            var previous = GeneratedWorldLoader.Current;
            bool vanilla = false;
            double start = context.Seconds;
            SandBox.SandBoxSaveHelper.TryLoadSave(info, r => vanilla = true, () => { });
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 3600) yield return null;
            row["seconds"] = context.Seconds - start; row["vanillaStarted"] = vanilla;
            row["loader"] = loader == null ? null : loader.Report;
            bool loaded = loader != null && loader.State == GeneratedWorldLoader.Phase.Loaded && !vanilla;
            if (loaded)
            {
                while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 3700) yield return null;
                RuntimeMissionProbe.Pause();
                yield return TwoWorldMatrixProbe.Frames(30);
                var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
                st.WorldId = binding.WorldId; st.Prefix = binding.WorldId + "_";
                row["worldId"] = binding.WorldId; row["instance"] = binding.Instance;
            }
            row["loaded"] = loaded;
        }

        // The diagnostic setup every case starts from: no autosave (an autosave mid-case would write a save), the player ignored by other parties.
        internal static Dictionary<string, object> Setup(State st)
        {
            var campaign = Campaign.Current;
            var previous = campaign.SandBoxManager.SandBoxSaveManager;
            campaign.SandBoxManager.SandBoxSaveManager = new NoAutoSave();
            var main = MobileParty.MainParty;
            var shelter = CampaignTime.Now + CampaignTime.Days(60);
            main.IgnoreByOtherPartiesTill(shelter);
            RuntimeMissionProbe.Pause();
            return new Dictionary<string, object> { ["saveManagerReplaced"] = previous == null ? null : previous.GetType().FullName, ["playerIgnoredUntilDays"] = shelter.ToDays,
                ["playerCulture"] = Hero.MainHero.Culture == null ? null : Hero.MainHero.Culture.StringId };
        }

        private sealed class NoAutoSave : ISaveManager
        {
            public int GetAutoSaveInterval() { return -1; }
            public bool IsAutoSaveDisabled() { return true; }
            public void OnSaveOver(bool success, string saveName) { }
        }

        // One case: its own row and failures, the engine's asserts and its log window captured; a case that leaves the campaign anywhere but idle
        // on the map (or ended it) is followed by a reload of the designated save.
        internal static IEnumerator Case(State st, string name, Func<Dictionary<string, object>, IEnumerator> body)
        {
            var context = st.Context;
            var row = new Dictionary<string, object> { ["caseName"] = name }; st.Cases[name] = row;
            var failures = new List<string>(); row["failures"] = failures;
            string notIdle = NotIdle();
            if (notIdle != null)
            {
                row["notIdleBefore"] = notIdle;
                if (st.NoReload) { failures.Add("the campaign was not idle on the map before the case (world B has no save to reload)"); st.Failures.Add(name + ": not run (not idle)"); yield break; }
                var reload = new Dictionary<string, object>(); row["reloadBefore"] = reload;
                yield return LoadDesignated(st, reload);
                st.Reloads++;
                if (!Equals(reload["loaded"], true)) { failures.Add("the designated save did not load before the case"); st.Failures.Add(name + ": not run (reload failed)"); yield break; }
                Setup(st);
            }
            RuntimeMissionProbe.Pause();
            context.Stage(st.Tag + "_missions:" + name);
            long logStart = LogLength(st);
            var asserts = new AssertCapture();
            asserts.Install();
            double start = context.Seconds;
            Exception error = null;
            var steps = TwoWorldMatrixProbe.Nested(body(row));
            try
            {
                while (true)
                {
                    try { if (!steps.MoveNext()) break; }
                    catch (Exception e) { error = e; break; }
                    yield return steps.Current;
                }
            }
            finally { asserts.Remove(); (steps as IDisposable)?.Dispose(); }
            row["seconds"] = context.Seconds - start;
            if (error != null) { row["error"] = error.ToString(); failures.Add(error.GetType().Name + ": " + error.Message); row["timeState"] = SafeTimeState(context); }
            row["asserts"] = asserts.Asserts; row["assertCount"] = asserts.Count; row["assertCanarySeen"] = asserts.CanarySeen;
            row["log"] = LogWindow(st, logStart, row);
            foreach (var f in failures) st.Failures.Add(name + ": " + f);
            context.Checkpoint();
        }

        // Idle on the map: no mission, encounter, map event, settlement, siege camp, captivity or open menu (a case's finally can leave a menu
        // open, run 17928: hostile_action_end_by_peace after the siege's peace).
        internal static string NotIdle()
        {
            if (Game.Current == null) return "no game";
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            if (map == null) return "state " + (Game.Current.GameStateManager.ActiveState == null ? "none" : Game.Current.GameStateManager.ActiveState.GetType().Name);
            var main = MobileParty.MainParty;
            if (Mission.Current != null) return "a mission";
            if (PlayerEncounter.Current != null) return "an encounter";
            if (Hero.MainHero.IsPrisoner) return "the player is a prisoner";
            if (main.MapEvent != null) return "a map event";
            if (main.CurrentSettlement != null) return "in " + main.CurrentSettlement.StringId;
            if (main.BesiegerCamp != null) return "a siege camp";
            if (map.MenuContext != null && map.MenuContext.GameMenu != null) return "the menu " + map.MenuContext.GameMenu.StringId;
            return null;
        }

        private static object SafeTimeState(ProbeContext context)
        {
            try { return Game.Current == null || Campaign.Current == null || !(Game.Current.GameStateManager.ActiveState is MapState) ? (object)new Dictionary<string, object> { ["state"] = Game.Current == null ? null : Game.Current.GameStateManager.ActiveState == null ? null : Game.Current.GameStateManager.ActiveState.GetType().Name, ["mission"] = Mission.Current != null } : TwoWorldMatrixProbe.TimeState(context.Seconds); }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        // ---------------------------------------------------------------- field battles

        private sealed class Candidate { public string Class, Face, Biome, Landform; public int X, Y; public Vec2 Position; }
        private sealed class Candidates_ { public Dictionary<string, Candidate> ByClass = new Dictionary<string, Candidate>(StringComparer.Ordinal); public Candidate Crossing; public Dictionary<string, object> Record; }

        // Cells of every battle class present, sampled as the battle map samples them (GeneratedBattleTerrain.SampleCell) and classified by the
        // resolver: every cell with a land sample counts, as the battle map classes every such cell (a marsh cell with water at its centre is
        // still wetland). Each class's first cell (scan order) with a sample point (the centre first) of valid land, away from settlements, whose
        // patch names exactly one land scene. A crossing cell: a cell with a Bridge or Fording sample; the crossing candidate is a point on one.
        private static Candidates_ Candidates(State st)
        {
            var result = new Candidates_();
            var map = (GeneratedMapScene)Campaign.Current.MapSceneWrapper;
            int width, height; float cellX, cellY; Vec2 size;
            GeneratedBattleTerrain.Cells(map, out width, out height, out cellX, out cellY, out size);
            var context = GeneratedBattleTerrain.Context(map); var navigation = Campaign.Current.Models.PartyNavigationModel; var resolver = GeneratedBattleTerrain.Resolver();
            var periodic = PeriodicWorld.Current;
            var settlementPoints = Settlement.All.Where(s => s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal)).SelectMany(s => new[] { s.Position.ToVec2(), s.GatePosition.ToVec2() }).ToList();
            var crossings = new HashSet<string>(st.RuleList("crossingTerrains"), StringComparer.Ordinal);
            int stride = (int)st.Rule("cellStride"); double clearance = st.Rule("settlementClearance");
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal); int sampled = 0, centreLand = 0, crossingCells = 0;
            for (int y = stride / 2; y < height; y += stride)
                for (int x = stride / 2; x < width; x += stride)
                {
                    var p = new Vec2((x + .5f) * cellX, (y + .5f) * cellY);
                    if (periodic != null && !periodic.IsCanonical(p.X)) continue;
                    var s = GeneratedBattleTerrain.SampleCell(x, y, cellX, cellY, context, navigation);
                    if (s.Land.Count == 0) continue;
                    sampled++;
                    if (s.CentreLand != null) centreLand++;
                    string cls = resolver.ClassOf(s);
                    int n; counts.TryGetValue(cls, out n); counts[cls] = n + 1;
                    bool crossingCell = s.Land.Any(t => crossings.Contains(((TerrainType)t).ToString()));
                    if (crossingCell) crossingCells++;
                    bool needClass = !result.ByClass.ContainsKey(cls), needCrossing = result.Crossing == null && crossingCell;
                    if (!needClass && !needCrossing) continue;
                    foreach (var q in SamplePoints(x, y, cellX, cellY))
                    {
                        var position = new CampaignVec2(q, true);
                        if (!position.IsValid() || GeneratedSpawnAssertions.Problem(position, MobileParty.NavigationType.Default) != null) continue;
                        if (settlementPoints.Any(v => v.Distance(q) < clearance)) continue;
                        var patch = map.GetMapPatchAtPosition(position);
                        if (!resolver.Table.ExpectedScene(patch.sceneIndex, false, (int)position.Face.FaceGroupIndex).Deterministic) continue;
                        string face = ((TerrainType)position.Face.FaceGroupIndex).ToString();
                        var candidate = new Candidate { Class = cls, Face = face, Biome = s.Biome, Landform = s.Landform, X = x, Y = y, Position = q };
                        if (needClass) { result.ByClass[cls] = candidate; needClass = false; }
                        if (needCrossing && crossings.Contains(face)) { result.Crossing = candidate; needCrossing = false; }
                        if (!needClass && !needCrossing) break;
                    }
                }
            result.Record = new Dictionary<string, object> { ["cells"] = new[] { width, height }, ["cellSize"] = new[] { cellX, cellY }, ["stride"] = stride, ["landCellsSampled"] = sampled, ["landCentreCells"] = centreLand,
                ["classesPresent"] = counts.ToDictionary(k => k.Key, k => (object)k.Value), ["chosen"] = result.ByClass.ToDictionary(k => k.Key, k => (object)Describe(k.Value)),
                ["crossingCells"] = crossingCells, ["crossing"] = result.Crossing == null ? null : Describe(result.Crossing) };
            return result;
        }

        // A cell's sample points as SampleCell takes them (3 x 3 at .05, .5, .95 of the cell), the centre first.
        private static IEnumerable<Vec2> SamplePoints(int x, int y, float cellX, float cellY)
        {
            yield return new Vec2((x + .5f) * cellX, (y + .5f) * cellY);
            for (int sy = 0; sy < 3; ++sy)
                for (int sx = 0; sx < 3; ++sx)
                    if (sx != 1 || sy != 1) yield return new Vec2((x + .05f + .45f * sx) * cellX, (y + .05f + .45f * sy) * cellY);
        }

        private static Dictionary<string, object> Describe(Candidate c)
        {
            return new Dictionary<string, object> { ["class"] = c.Class, ["cell"] = new[] { c.X, c.Y }, ["position"] = new[] { c.Position.X, c.Position.Y }, ["face"] = c.Face, ["biome"] = c.Biome, ["landform"] = c.Landform };
        }

        // A field battle at the candidate: the enemy a lord party of the given culture (war declared on its kingdom for the battle, peace made after,
        // recorded), else a bandit party; the player with a crew of its culture. retreat: left by Mission.RetreatMission.
        private static IEnumerator Field(State st, Dictionary<string, object> row, Candidate c, string culture, bool retreat)
        {
            var context = st.Context; var failures = (List<string>)row["failures"];
            var main = MobileParty.MainParty;
            row["candidate"] = Describe(c); row["retreat"] = retreat;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            Heal();
            setup["crewAdded"] = Crew(main, (int)st.Rule("playerTroops"));
            main.Position = new CampaignVec2(c.Position, true); main.SetMoveModeHold();
            IFaction war = null; MobileParty enemyParty = null; CampaignVec2 enemyHome = default(CampaignVec2);
            var aside = new List<KeyValuePair<MobileParty, CampaignVec2>>();
            try
            {
                string left;
                var enemy = Enemy(st, culture, out war, out left);
                if (enemy == null) throw new InvalidOperationException("no hostile land party for the battle");
                setup["enemyLeftSettlement"] = left;
                if (culture != null && !enemy.IsLordParty) setup["lordCensus"] = LordCensus(st, culture);
                setup["enemy"] = enemy.StringId; setup["enemyKind"] = GeneratedSpawnAssertions.KindOf(enemy); setup["enemyCulture"] = enemy.LeaderHero != null && enemy.LeaderHero.Culture != null ? enemy.LeaderHero.Culture.StringId : enemy.MapFaction.Culture == null ? null : enemy.MapFaction.Culture.StringId;
                setup["enemyTroops"] = enemy.MemberRoster.TotalManCount; setup["warDeclared"] = war == null ? null : war.StringId; setup["enemyShips"] = enemy.Ships.Count;
                var point = RuntimeMissionProbe.FindPoint(main.Position, MobileParty.NavigationType.Default, 2, 1);
                enemyHome = enemy.Position; enemyParty = enemy;
                enemy.Position = point; enemy.SetMoveModeHold();
                aside = Isolate(point, main, enemy);
                setup["movedAside"] = aside.Select(kv => kv.Key.StringId).ToArray();
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["before"] = RuntimeMissionProbe.Snapshot(false);
                int observedBefore = MNBL.ProceduralWorld.Missions.MissionScenePatches.Observed;
                PlayerEncounter.RestartPlayerEncounter(enemy.Party, main.Party, true, false);
                var encounter = PlayerEncounter.Current;
                var battle = PlayerEncounter.StartBattle();
                if (battle != null && battle.IsNavalMapEvent) failures.Add("the land battle became a naval map event");
                var involved = battle == null ? new List<PartyBase>() : battle.InvolvedParties.ToList();
                row["mapEvent"] = new Dictionary<string, object> { ["naval"] = battle != null && battle.IsNavalMapEvent,
                    ["involved"] = involved.Select(p => p.MobileParty == null ? "settlement" : p.MobileParty.StringId).ToArray() };
                // Only the two parties of the case fight (run 17928: parties parked by earlier cases joined as the player's allies).
                if (involved.Count != 2 || !involved.Contains(enemy.Party) || !involved.Contains(main.Party)) failures.Add("the battle's parties are not exactly the player's and the enemy's: " + String.Join(", ", (string[])((Dictionary<string, object>)row["mapEvent"])["involved"]));
                GameMenu.ActivateGameMenu("encounter");
                yield return Menu(st, "encounter");
                RuntimeMissionProbe.RunOption("attack");
                yield return RunMission(st, row, true, m => GearAndSample(st, m, row),
                    m => { if (retreat) m.RetreatMission(); else m.OnEndMissionResult(); });
                // The outcome on the map before the encounter is finished: the installed OnEndMissionResult also ends by retreating, so a field
                // battle and the retreat both leave the battle to the enemy; for the retreat it is required.
                var mapEvent = PlayerEncounter.Battle ?? battle;
                bool playerWon = mapEvent != null && mapEvent.HasWinner && mapEvent.WinningSide == mapEvent.PlayerSide;
                row["outcome"] = new Dictionary<string, object> { ["menu"] = CurrentMenu(), ["battleState"] = mapEvent == null ? null : mapEvent.BattleState.ToString(),
                    ["playerWon"] = playerWon, ["enemyActive"] = enemy.IsActive, ["enemyHealthy"] = enemy.MemberRoster.TotalHealthyCount,
                    ["encounterState"] = PlayerEncounter.Current == null ? null : PlayerEncounter.Current.EncounterState.ToString() };
                if (retreat && (playerWon || !enemy.IsActive || enemy.MemberRoster.TotalHealthyCount == 0)) failures.Add("the retreat did not leave the battle to the enemy");
                if (PlayerEncounter.Current != null && PlayerEncounter.Current == encounter) PlayerEncounter.Finish(true);
                yield return Idle(st, 30);
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["after"] = RuntimeMissionProbe.Snapshot(false);
                RuntimeMissionProbe.CompareReturn(row, failures, false);
                // MMR-42's guard: this battle's own observation (CompareReturn reads the last one of the scene's name, which an earlier battle
                // on the same cell also wrote).
                var opened = row.ContainsKey("mission") ? ((Dictionary<string, object>)row["mission"])["scene"] as string : null;
                int fresh = MNBL.ProceduralWorld.Missions.MissionScenePatches.Observed - observedBefore;
                var ring = MNBL.ProceduralWorld.Missions.MissionScenePatches.Snapshot();
                var own = fresh <= 0 ? null : ring.Skip(Math.Max(0, ring.Count - fresh)).LastOrDefault(o => Equals(o["opened"], opened));
                row["guardObservations"] = fresh; row["mismatchGuard"] = own;
                if (own == null) failures.Add("the battle scene guard made no observation of this battle's scene choice");
                else if (!Equals(own["agrees"], true)) failures.Add("the battle scene guard flagged this battle's choice: " + own["problem"]);
                row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                if (row["afterNavigation"] != null) failures.Add("the party returned off navigation: " + row["afterNavigation"]);
            }
            finally
            {
                setup["putBack"] = Restore(aside);
                if (war != null && war.IsAtWarWith(Clan.PlayerClan)) { MakePeaceAction.Apply(Clan.PlayerClan, war); setup["peaceMade"] = !war.IsAtWarWith(Clan.PlayerClan); }
                // The enemy back where it was (into its settlement if it left one), so no later case meets it.
                if (enemyParty != null && enemyParty.IsActive && enemyParty.MapEvent == null)
                {
                    var home = setup["enemyLeftSettlement"] == null ? null : Settlement.Find((string)setup["enemyLeftSettlement"]);
                    enemyParty.Position = enemyHome;
                    if (home != null && enemyParty.CurrentSettlement == null) EnterSettlementAction.ApplyForParty(enemyParty, home);
                    setup["enemyRestored"] = home != null ? (object)home.StringId : new[] { enemyHome.X, enemyHome.Y };
                }
            }
        }

        // One-shot run 29368: an AI lord's party standing near the battle point joined the case's battle (normal joining, not a defect). Every
        // other free party within IsolationRadius of the point (periodic distance) is moved aside for the case, to a point 20-30 units from where
        // it stood on its own navigation, and put back after it (Restore: the ids put back).
        internal const float IsolationRadius = 12f;
        internal static List<KeyValuePair<MobileParty, CampaignVec2>> Isolate(CampaignVec2 point, params MobileParty[] keep)
        {
            var periodic = PeriodicWorld.Current;
            Func<CampaignVec2, float> distance = p => periodic != null ? periodic.Distance(p.ToVec2(), point.ToVec2()) : p.ToVec2().Distance(point.ToVec2());
            var moved = new List<KeyValuePair<MobileParty, CampaignVec2>>();
            foreach (var p in MobileParty.All.Where(p => !keep.Contains(p) && p.IsActive && p.CurrentSettlement == null && p.MapEvent == null && p.AttachedTo == null
                                                      && distance(p.Position) < IsolationRadius).OrderBy(p => p.StringId, StringComparer.Ordinal).ToList())
            {
                var navigation = p.IsCurrentlyAtSea ? MobileParty.NavigationType.Naval : MobileParty.NavigationType.Default;
                var away = NavigationHelper.FindPointAroundPosition(p.Position, navigation, 30, 20, true, false);
                if (!away.IsValid() || !away.Face.IsValid() || away.IsOnLand != !p.IsCurrentlyAtSea) continue;
                moved.Add(new KeyValuePair<MobileParty, CampaignVec2>(p, p.Position));
                p.Position = away;
            }
            return moved;
        }

        internal static string[] Restore(List<KeyValuePair<MobileParty, CampaignVec2>> moved)
        {
            var back = new List<string>();
            foreach (var kv in moved)
                if (kv.Key.IsActive && kv.Key.MapEvent == null && kv.Key.CurrentSettlement == null) { kv.Key.Position = kv.Value; back.Add(kv.Key.StringId); }
            return back.ToArray();
        }

        // A lord party of the culture (its kingdom put at war with the player; a lord in a settlement not under siege leaves it first, recorded as
        // setup), else a bandit party: active, on land, free. A party owning ships (War Sails gives most northern lords a fleet) fights on land
        // like any other when it is not at sea; the battle must still be a land map event (Field checks it).
        internal static MobileParty Enemy(State st, string culture, out IFaction war, out string left)
        {
            war = null; left = null;
            var main = MobileParty.MainParty;
            Func<MobileParty, bool> free = p => p != main && p.IsActive && p.MapEvent == null && p.Army == null && p.AttachedTo == null && p.BesiegerCamp == null
                && !p.IsCurrentlyAtSea && p.MemberRoster.TotalHealthyCount >= 5;
            if (culture != null)
            {
                var lord = MobileParty.All.Where(p => free(p) && p.IsLordParty && p.LeaderHero != null && p.LeaderHero.IsActive && !p.LeaderHero.IsWounded && p.LeaderHero.Culture != null
                        && p.LeaderHero.Culture.StringId == culture && p.MapFaction is Kingdom && p.StringId.StartsWith(st.Prefix, StringComparison.Ordinal)
                        && (p.CurrentSettlement == null ? p.Position.IsOnLand : p.CurrentSettlement.SiegeEvent == null))
                    .OrderBy(p => p.CurrentSettlement == null ? 0 : 1).ThenBy(p => p.MemberRoster.TotalManCount).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
                if (lord != null)
                {
                    if (lord.CurrentSettlement != null) { left = lord.CurrentSettlement.StringId; LeaveSettlementAction.ApplyForParty(lord); }
                    if (!lord.MapFaction.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, lord.MapFaction); war = lord.MapFaction; }
                    return lord;
                }
            }
            return MobileParty.All.Where(p => free(p) && p.CurrentSettlement == null && p.Position.IsOnLand && p.IsBandit).OrderBy(p => p.MemberRoster.TotalManCount).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
        }

        // Why no lord party of the culture could fight: the culture's lord parties of the world counted by the first filter each fails.
        private static Dictionary<string, object> LordCensus(State st, string culture)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            Action<string> count = k => { int n; counts.TryGetValue(k, out n); counts[k] = n + 1; };
            var heroes = Hero.AllAliveHeroes.Where(h => h.IsLord && h.Culture != null && h.Culture.StringId == culture && h.StringId.StartsWith(st.Prefix, StringComparison.Ordinal)).ToList();
            foreach (var h in heroes)
            {
                var p = h.PartyBelongedTo;
                count(p == null ? (h.IsPrisoner ? "prisoner" : h.CurrentSettlement != null ? "inSettlementWithoutParty" : "noParty")
                    : p.LeaderHero != h ? "notLeader" : !p.IsActive ? "inactive" : p.MapEvent != null ? "mapEvent" : p.Army != null ? "army" : p.AttachedTo != null ? "attached"
                    : p.BesiegerCamp != null ? "besieging" : p.Ships.Count > 0 ? "ships" : p.IsCurrentlyAtSea ? "atSea" : p.MemberRoster.TotalHealthyCount < 5 ? "small"
                    : !h.IsActive ? "heroInactive" : h.IsWounded ? "wounded" : !(p.MapFaction is Kingdom) ? "noKingdom" : p.CurrentSettlement != null && p.CurrentSettlement.SiegeEvent != null ? "inBesiegedSettlement"
                    : p.CurrentSettlement == null && !p.Position.IsOnLand ? "offLand" : "eligible");
            }
            return new Dictionary<string, object> { ["lords"] = heroes.Count, ["byReason"] = counts.ToDictionary(k => k.Key, k => (object)k.Value) };
        }

        internal static void Heal()
        {
            var hero = Hero.MainHero;
            if (hero.HitPoints < hero.MaxHitPoints) hero.HitPoints = hero.MaxHitPoints;
        }

        internal static int Crew(MobileParty party, int wanted)
        {
            int count = Math.Max(0, wanted - party.MemberRoster.TotalHealthyCount);
            var troop = Hero.MainHero.Culture != null && Hero.MainHero.Culture.BasicTroop != null ? Hero.MainHero.Culture.BasicTroop : MBObjectManager.Instance.GetObject<CharacterObject>("imperial_recruit");
            if (count > 0) party.MemberRoster.AddToCounts(troop, count, false, 0, 0, true, -1);
            return count;
        }

        // In a battle: the gear of every human agent (complete armour and weapons, every item's mesh), then the sample window.
        internal static IEnumerator GearAndSample(State st, Mission m, Dictionary<string, object> row)
        {
            row["gear"] = Gear(st, m);
            double t0 = m.CurrentTime, start = st.Context.Seconds;
            while (st.Context.Seconds - start < st.Rule("sampleSeconds")) yield return null;
            ((Dictionary<string, object>)row["mission"])["missionTimeAdvanced"] = m.CurrentTime - t0;
        }

        private static readonly Dictionary<string, bool> MeshCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Complete gear: body armour and at least one weapon; every equipped item names a mesh the engine finds. Every agent counts for the
        // battle's own rows; the culture tally of the current world (troops and heroes seen) counts only the agents of other parties than the
        // player's (the player's hero and the crew the probe adds would otherwise stand for the player's culture).
        internal static Dictionary<string, object> Gear(State st, Mission m)
        {
            int agents = 0, incomplete = 0, player = 0; var problems = new List<string>(); var byCulture = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var none = new Dictionary<string, object> { ["troops"] = 0, ["heroes"] = 0, ["incomplete"] = 0, ["samples"] = new List<string>() };
            foreach (var agent in m.Agents.ToList())
            {
                if (agent == null || !agent.IsHuman || agent.Character == null) continue;
                agents++;
                var character = agent.Character as CharacterObject;
                string culture = character != null && character.Culture != null ? character.Culture.StringId : "none";
                bool hero = character != null && character.HeroObject != null;
                bool own = agent.Origin != null && ReferenceEquals(agent.Origin.BattleCombatant, PartyBase.MainParty);
                Dictionary<string, object> c;
                if (own) { player++; c = none; }
                else if (!st.Tally.TryGetValue(culture, out c)) st.Tally[culture] = c = new Dictionary<string, object> { ["troops"] = 0, ["heroes"] = 0, ["incomplete"] = 0, ["samples"] = new List<string>() };
                c[hero ? "heroes" : "troops"] = (int)c[hero ? "heroes" : "troops"] + 1;
                int n; byCulture.TryGetValue(culture, out n); byCulture[culture] = n + 1;
                var equipment = agent.SpawnEquipment;
                bool body = !equipment[EquipmentIndex.Body].IsEmpty;
                int weapons = 0; var missing = new List<string>();
                for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
                {
                    var element = equipment[i];
                    if (element.IsEmpty || element.Item == null) continue;
                    if (i < (int)EquipmentIndex.NumAllWeaponSlots) weapons++;
                    foreach (var mesh in Meshes(element.Item, missing))
                    {
                        bool found;
                        if (!MeshCache.TryGetValue(mesh, out found)) { try { found = TaleWorlds.Engine.MetaMesh.GetCopy(mesh, false, true) != null; } catch (Exception) { found = false; } MeshCache[mesh] = found; }
                        if (!found) missing.Add(element.Item.StringId + " (" + mesh + ")");
                    }
                }
                bool complete = body && weapons > 0 && missing.Count == 0;
                if (!complete)
                {
                    incomplete++; c["incomplete"] = (int)c["incomplete"] + 1;
                    string text = (character == null ? agent.Character.StringId : character.StringId) + (hero ? " (hero)" : "") + ": " + (body ? "" : "no body armour; ") + (weapons > 0 ? "" : "no weapon; ") + String.Join(", ", missing);
                    if (problems.Count < 20) problems.Add(text);
                    var samples = (List<string>)c["samples"]; if (samples.Count < 5) samples.Add(text);
                }
            }
            return new Dictionary<string, object> { ["agents"] = agents, ["playerPartyAgents"] = player, ["incomplete"] = incomplete, ["byCulture"] = byCulture.ToDictionary(k => k.Key, k => (object)k.Value), ["problems"] = problems };
        }

        // The meshes an item is drawn with: a crafted weapon's pieces (WeaponDesign.UsedPieces, each valid non-empty piece's MeshName; such an
        // item names no MultiMeshName), else its MultiMeshName. An item with neither is recorded as missing (a horse excepted: its mesh comes
        // with its horse component).
        private static List<string> Meshes(ItemObject item, List<string> missing)
        {
            var names = new List<string>();
            if (item.WeaponDesign != null && item.WeaponDesign.UsedPieces != null)
            {
                foreach (var e in item.WeaponDesign.UsedPieces)
                    if (e != null && e.IsValid && e.CraftingPiece != null && !e.CraftingPiece.IsEmptyPiece && !String.IsNullOrEmpty(e.CraftingPiece.MeshName)) names.Add(e.CraftingPiece.MeshName);
                if (names.Count == 0) missing.Add(item.StringId + " (a crafted weapon without piece meshes)");
            }
            else if (!String.IsNullOrEmpty(item.MultiMeshName)) names.Add(item.MultiMeshName);
            else if (item.HorseComponent == null) missing.Add(item.StringId + " (no mesh name)");
            return names;
        }

        // ---------------------------------------------------------------- missions

        // Waits for the mission to be ready (MMR-60's readiness: its state and screen, the scene loaded, the main agent, shaders compiled; teams
        // for a battle), finishes deployment, runs the case's in-mission steps, takes the screenshot (outputs.<case>Screenshot), ends it unless it
        // ended itself, and waits for the map.
        internal static IEnumerator RunMission(State st, Dictionary<string, object> row, bool combat, Func<Mission, IEnumerator> inMission, Action<Mission> end, string key = "mission", bool naval = false)
        {
            var context = st.Context;
            Mission owned = null; bool requested = false;
            double start = context.Seconds;
            try
            {
                while (true)
                {
                    var state = Game.Current.GameStateManager.ActiveState as MissionState;
                    if (state != null && Mission.Current != null && state.CurrentMission == Mission.Current)
                    {
                        owned = Mission.Current;
                        if (owned.Scene != null && owned.Scene.IsLoadingFinished() && owned.MainAgent != null && RuntimeMissionProbe.IsMissionScreen()
                            && TaleWorlds.Engine.Utilities.GetNumberOfShaderCompilationsInProgress() == 0
                            && (!combat || (owned.PlayerTeam != null && owned.PlayerEnemyTeam != null))
                            && (!naval || RuntimeMissionProbe.ShipCount(owned) >= 2)) break;
                    }
                    if (context.Seconds - start > st.Rule("missionReadySeconds")) throw new TimeoutException("the mission was not ready in time (state " + (Game.Current.GameStateManager.ActiveState == null ? "none" : Game.Current.GameStateManager.ActiveState.GetType().Name) + ")");
                    yield return null;
                }
                var info = RuntimeMissionProbe.MissionInfo(owned, Game.Current.GameStateManager.ActiveState);
                info["combat"] = combat; info["loadSeconds"] = context.Seconds - start;
                row[key] = info;
                if (combat && !owned.IsDeploymentFinished)
                {
                    yield return null;
                    var handler = owned.GetMissionBehavior<DeploymentHandler>();
                    if (handler != null) handler.FinishDeployment();
                    start = context.Seconds;
                    while (!owned.IsDeploymentFinished) { if (context.Seconds - start > 30) throw new TimeoutException("deployment did not finish"); yield return null; }
                    info["deployed"] = true;
                }
                yield return TwoWorldMatrixProbe.Frames(2);
                var spawns = Spawns(owned); info["spawns"] = spawns;
                if ((int)spawns["offNavmesh"] > 0 || (int)spawns["outsideBoundaries"] > 0 || !Equals(spawns["mainOnNavmesh"], true))
                    ((List<string>)row["failures"]).Add("the " + key + " has agents spawned off its navigation mesh or outside its boundaries: " + String.Join("; ", (List<string>)spawns["samples"]));
                if (inMission != null) yield return inMission(owned);
                if (Mission.Current == owned)
                {
                    string shot = Path.Combine(context.Directory, st.Tag + "-" + context.ProcessId + "-" + Key(row, key) + ".bmp");
                    TaleWorlds.Engine.Utilities.TakeScreenshot(shot);
                    context.Outputs[Key(row, key) + "Screenshot"] = shot;
                    start = context.Seconds;
                    do { yield return null; if (context.Seconds - start > 30) break; } while (Mission.Current == owned && (!File.Exists(shot) || new FileInfo(shot).Length == 0));
                    info["screenshot"] = File.Exists(shot);
                }
                // A mission whose logic reported its end (a defeat, a won hideout) still waits for the player's leave: the case's end is called
                // either way, as the leave button calls it.
                if (Mission.Current == owned) { info["endedItself"] = owned.MissionEnded; end(owned); requested = true; }
                info["missionResult"] = owned.MissionResult == null ? null : new Dictionary<string, object> { ["victory"] = owned.MissionResult.PlayerVictory, ["defeated"] = owned.MissionResult.PlayerDefeated, ["enemyRetreated"] = owned.MissionResult.EnemyRetreated };
                start = context.Seconds;
                while (Mission.Current != null || Game.Current.GameStateManager.ActiveState is MissionState || RuntimeMissionProbe.IsMissionScreen())
                {
                    if (context.Seconds - start > st.Rule("teardownSeconds")) throw new TimeoutException("the mission did not tear down");
                    yield return null;
                }
                info["teardownSeconds"] = context.Seconds - start;
            }
            finally { if (!requested && owned != null && Mission.Current == owned) owned.EndMission(); }
        }

        // Every active human agent's position: on a face of the scene's navigation mesh, looked up as mission code looks it up (the Vec3 form of
        // Scene.GetNavMeshFaceIndex; the Vec2 form is the campaign map's, its flag the map's land region, and it hung a tavern mission in run
        // 3940), at its height or half a metre below or above it (a mounted agent stands off its face); and inside the mission's boundaries
        // when it has any. An agent off the mesh while it uses a scene object (a seat, an instrument: the stock scene's own usable points, run
        // 37204's town) or sits, or while it stands on a machine or other scene geometry (Agent.GetSteppedMachine, GetSteppedRootEntity: run
        // 35564's siege had one troop at 49.9 m off the mesh), or on the terrain itself within half a metre (a gap in a stock scene's mesh), is
        // recorded apart with what it uses or stands on; any other agent off the mesh (nothing under it) fails.
        private static readonly float[] FaceHeights = { 0f, -0.5f, 0.5f };

        internal static Dictionary<string, object> Spawns(Mission m)
        {
            int agents = 0, onFace = 0, heightAdjusted = 0, off = 0, onObject = 0, outside = 0; var samples = new List<string>();
            var objects = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var boundaries = m.Boundaries == null ? 0 : m.Boundaries.Count;
            bool mainOn = false;
            foreach (var a in m.Agents.ToList())
            {
                if (a == null || !a.IsHuman || !a.IsActive()) continue;
                agents++;
                var position = a.Position;
                int found = -1;
                for (int i = 0; i < FaceHeights.Length && found < 0; i++)
                {
                    var record = new PathFaceRecord(-1, -1, -1);
                    m.Scene.GetNavMeshFaceIndex(ref record, new Vec3(position.X, position.Y, position.Z + FaceHeights[i]), false);
                    if (record.IsValid()) found = i;
                }
                bool exact = found == 0, horizontal = found >= 0;
                bool inside = boundaries == 0 || m.IsPositionInsideBoundaries(position.AsVec2);
                string support = null;
                if (!horizontal && a != m.MainAgent)
                {
                    if (a.IsUsingGameObject || a.IsSitting()) support = a.CurrentlyUsedGameObject == null ? "sitting" : a.CurrentlyUsedGameObject.GetType().Name;
                    else if (a.GetSteppedMachine() != null) support = "on " + a.GetSteppedMachine().GetType().Name;
                    else { var root = a.GetSteppedRootEntity(); if (root.IsValid) support = "on entity " + root.Name; }
                    if (support == null) { float ground = m.Scene.GetTerrainHeight(position.AsVec2, true); if (Math.Abs(position.Z - ground) <= 0.5f) support = "on the terrain, off the mesh"; }
                }
                bool usingObject = support != null;
                if (usingObject)
                {
                    string kind = support;
                    int k; objects.TryGetValue(kind, out k); objects[kind] = k + 1;
                    horizontal = true;
                }
                if (exact) onFace++; else if (usingObject) onObject++; else if (horizontal) heightAdjusted++; else off++;
                if (!inside) outside++;
                if (a == m.MainAgent) mainOn = horizontal && inside;
                if ((!horizontal || !inside) && samples.Count < 5) samples.Add((a.Character == null ? "?" : a.Character.StringId) + " at " + position.X.ToString("0.0", CultureInfo.InvariantCulture) + "," + position.Y.ToString("0.0", CultureInfo.InvariantCulture) + "," + position.Z.ToString("0.0", CultureInfo.InvariantCulture)
                    + " (terrain " + m.Scene.GetTerrainHeight(position.AsVec2, true).ToString("0.0", CultureInfo.InvariantCulture) + ")" + (horizontal ? "" : " off the navmesh") + (inside ? "" : " outside the boundaries"));
            }
            return new Dictionary<string, object> { ["agents"] = agents, ["onNavmesh"] = onFace, ["heightAdjusted"] = heightAdjusted, ["offNavmesh"] = off,
                ["offNavmeshUsingObject"] = onObject, ["usedObjects"] = objects.ToDictionary(k => k.Key, k => (object)k.Value), ["boundaries"] = boundaries,
                ["outsideBoundaries"] = outside, ["mainOnNavmesh"] = mainOn, ["samples"] = samples };
        }

        // M1: the party's return to where the case's installed path puts it, within the bound (the vanilla land bound unless given), in its
        // domain: on land, or at sea for a return at sea (MMR-53's seaborne raid, within the naval bound).
        internal static void ReturnTo(State st, Dictionary<string, object> row, List<string> failures, Vec2 expected, string reason, double bound = -1, bool atSea = false)
        {
            var main = MobileParty.MainParty;
            // Across the periodic seam, as RuntimeMissionProbe.CompareReturn measures it.
            var periodic = PeriodicWorld.Current;
            double moved = periodic == null ? main.Position.ToVec2().Distance(expected) : periodic.Distance(main.Position.ToVec2(), expected);
            if (bound < 0) bound = st.Rule("returnBoundLand");
            row["returnExpected"] = new Dictionary<string, object> { ["position"] = new[] { expected.X, expected.Y }, ["reason"] = reason };
            row["returnDisplacement"] = moved; row["returnBound"] = bound;
            if (moved > bound) failures.Add("returned " + moved.ToString("0.###", CultureInfo.InvariantCulture) + " map units from " + reason + " (bound " + bound + ")");
            // atSea (MMR-53): a return at sea (a seaborne raid's) is checked on the sea, a land return on land.
            if (atSea) { if (!main.IsCurrentlyAtSea) failures.Add("the party returned off the sea"); }
            else if (!main.Position.IsOnLand || main.IsCurrentlyAtSea) failures.Add("the party returned off land");
        }

        internal static Vec2 PositionOf(Dictionary<string, object> snapshot) { var p = (float[])snapshot["position"]; return new Vec2(p[0], p[1]); }

        private static string Key(Dictionary<string, object> row, string key)
        {
            // The case's name as recorded by Case (the row's key in State.Cases), camel-cased, plus the mission key.
            string name = row.ContainsKey("caseName") ? (string)row["caseName"] : "case";
            var parts = (name + "." + key).Split('.', '-', '_').Where(p => p.Length > 0).ToArray();
            return parts[0] + String.Concat(parts.Skip(1).Select(p => Char.ToUpperInvariant(p[0]) + p.Substring(1)));
        }

        // Waits for the official menu, recording its options (ids and whether they hold).
        internal static IEnumerator Menu(State st, string id)
        {
            double start = st.Context.Seconds;
            while (true)
            {
                var map = Game.Current.GameStateManager.ActiveState as MapState;
                if (map != null && map.MenuContext != null && map.MenuContext.GameMenu != null && map.MenuContext.GameMenu.StringId == id) break;
                if (st.Context.Seconds - start > 30) throw new TimeoutException("expected the official menu " + id + " (at " + CurrentMenu() + ")");
                yield return null;
            }
            RecordMenu(st);
        }

        internal static string CurrentMenu()
        {
            var map = Game.Current == null ? null : Game.Current.GameStateManager.ActiveState as MapState;
            return map == null ? (Game.Current == null ? "no game" : Game.Current.GameStateManager.ActiveState == null ? "no state" : Game.Current.GameStateManager.ActiveState.GetType().Name)
                : map.MenuContext == null || map.MenuContext.GameMenu == null ? "the map" : map.MenuContext.GameMenu.StringId;
        }

        internal static void RecordMenu(State st)
        {
            var map = Game.Current.GameStateManager.ActiveState as MapState;
            if (map == null || map.MenuContext == null || map.MenuContext.GameMenu == null) return;
            var menu = map.MenuContext.GameMenu;
            st.MenusSeen.Add(menu.StringId);
            if (st.MenuOptions.ContainsKey(menu.StringId)) return;
            var options = new List<object>();
            foreach (var o in menu.MenuOptions)
            {
                bool holds = false; string text = null, tooltip = null;
                try { holds = o.GetConditionsHold(Game.Current, map.MenuContext); } catch (Exception) { holds = false; }
                try { text = o.Text == null ? null : o.Text.ToString(); } catch (Exception e) { text = "(" + e.GetType().Name + ")"; }
                try { tooltip = o.Tooltip == null ? null : o.Tooltip.ToString(); } catch (Exception e) { tooltip = "(" + e.GetType().Name + ")"; }
                options.Add(new Dictionary<string, object> { ["id"] = o.IdString, ["holds"] = holds, ["enabled"] = holds && o.IsEnabled, ["text"] = text, ["tooltip"] = String.IsNullOrEmpty(tooltip) ? null : tooltip });
            }
            st.MenuOptions[menu.StringId] = options;
        }

        // Idle on the map: no mission, encounter, map event or settlement; a victory's loot or party screen closed as the player would close it.
        internal static IEnumerator Idle(State st, double seconds)
        {
            double start = st.Context.Seconds;
            var main = MobileParty.MainParty;
            while (true)
            {
                var active = Game.Current.GameStateManager.ActiveState;
                if (active is InventoryState) { try { InventoryScreenHelper.CloseScreen(true); } catch (Exception) { } }
                else if (active is PartyState) { try { PartyScreenHelper.CloseScreen(true, true); } catch (Exception) { } }
                if (active is MapState && Mission.Current == null && PlayerEncounter.Current == null && main.MapEvent == null && main.CurrentSettlement == null && !Hero.MainHero.IsPrisoner)
                {
                    var map = (MapState)active;
                    if (map.MenuContext == null || map.MenuContext.GameMenu == null) break;
                }
                if (st.Context.Seconds - start > seconds) throw new TimeoutException("the party did not come back idle to the map (at " + CurrentMenu() + ")");
                yield return null;
            }
            RuntimeMissionProbe.Pause();
        }

        // ---------------------------------------------------------------- settlements

        private static IEnumerator Town(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var town = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && !s.MapFaction.IsAtWarWith(main.MapFaction) && s.SiegeEvent == null && s.Notables.Count > 0
                    && s.LocationComplex != null && s.LocationComplex.GetLocationWithId("tavern") != null)
                .OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (town == null) throw new InvalidOperationException("no peaceful town with notables and a tavern");
            int wall = town.Town.GetWallLevel();
            var expected = new Dictionary<string, object> { ["center"] = town.LocationComplex.GetLocationWithId("center").GetSceneName(wall), ["tavern"] = town.LocationComplex.GetLocationWithId("tavern").GetSceneName(wall) };
            row["settlement"] = town.StringId; row["name"] = town.Name.ToString(); row["expectedScenes"] = expected; row["wallLevel"] = wall;
            yield return Enter(st, row, town, "town");
            RuntimeMissionProbe.RunOption("town_streets");
            yield return RunMission(st, row, false, m => Converse(st, m, row, "centerConversation",
                a => { var h = HeroOf(a); return h != null && h.IsNotable; },
                a => { var c = a.Character as CharacterObject; return c != null && (c.Occupation == Occupation.Merchant || c.Occupation == Occupation.ShopWorker || c.Occupation == Occupation.GoodsTrader || c.Occupation == Occupation.Weaponsmith || c.Occupation == Occupation.Armorer || c.Occupation == Occupation.HorseTrader || c.Occupation == Occupation.Blacksmith); }),
                m => m.EndMission(), "center");
            yield return Menu(st, "town");
            // The tavern: the location encounter opens it as the center's tavern door does (the passage's location change).
            var tavern = town.LocationComplex.GetLocationWithId("tavern");
            PlayerEncounter.LocationEncounter.CreateAndOpenMissionController(tavern, null, null, null);
            yield return RunMission(st, row, false, m => Converse(st, m, row, "tavernConversation",
                a => { var c = a.Character as CharacterObject; return c != null && c.Occupation == Occupation.Tavernkeeper; }, null),
                m => m.EndMission(), "tavern");
            yield return Menu(st, "town");
            yield return Leave(st, row, "town_leave");
            foreach (var place in new[] { "center", "tavern" })
            {
                var mission = row.ContainsKey(place) ? (Dictionary<string, object>)row[place] : null;
                if (mission == null) { failures.Add("the " + place + " mission did not open"); continue; }
                if (!String.Equals((string)mission["scene"], (string)expected[place], StringComparison.Ordinal)) failures.Add("the " + place + " opened " + mission["scene"] + ", not the mapped " + expected[place]);
            }
            var conversation = row.ContainsKey("centerConversation") ? (Dictionary<string, object>)row["centerConversation"] : null;
            if (conversation == null || !Equals(conversation["started"], true)) failures.Add("no notable or merchant conversation in the town center");
        }

        private static IEnumerator Castle(State st, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var castle = Settlement.All.Where(s => s.IsCastle && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && !s.MapFaction.IsAtWarWith(main.MapFaction) && s.SiegeEvent == null && s.LocationComplex != null)
                .OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (castle == null) throw new InvalidOperationException("no peaceful castle");
            var owner = castle.Owner;
            int wall = castle.Town.GetWallLevel();
            string expected = castle.LocationComplex.GetLocationWithId("center").GetSceneName(wall);
            row["settlement"] = castle.StringId; row["name"] = castle.Name.ToString(); row["expectedScene"] = expected; row["wallLevel"] = wall;
            // Diagnostic setup: the castle is given to the player's clan, so its gates open without a guard's dialogue; given back after.
            ChangeOwnerOfSettlementAction.ApplyByDefault(Hero.MainHero, castle);
            row["setup"] = new Dictionary<string, object> { ["givenToPlayer"] = castle.OwnerClan == Clan.PlayerClan, ["previousOwner"] = owner == null ? null : owner.StringId };
            try
            {
                yield return Enter(st, row, castle, "castle");
                RuntimeMissionProbe.RunOption("take_a_walk_around_the_castle");
                yield return RunMission(st, row, false, null, m => m.EndMission(), "center");
                yield return Menu(st, "castle");
                yield return Leave(st, row, "leave");
            }
            finally
            {
                if (owner != null && owner.IsAlive && castle.OwnerClan == Clan.PlayerClan) { ChangeOwnerOfSettlementAction.ApplyByDefault(owner, castle); ((Dictionary<string, object>)row["setup"])["givenBack"] = castle.Owner == owner; }
            }
            var mission = row.ContainsKey("center") ? (Dictionary<string, object>)row["center"] : null;
            if (mission == null) failures.Add("the castle courtyard did not open");
            else if (!String.Equals((string)mission["scene"], expected, StringComparison.Ordinal)) failures.Add("the castle opened " + mission["scene"] + ", not the mapped " + expected);
        }

        private static IEnumerator Village(State st, Dictionary<string, object> row)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var village = Settlement.All.Where(s => s.IsVillage && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && !s.MapFaction.IsAtWarWith(main.MapFaction) && !s.IsUnderRaid && s.LocationComplex != null
                    && s.LocationComplex.GetLocationWithId("village_center") != null)
                .OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (village == null) throw new InvalidOperationException("no peaceful village");
            string expected = village.LocationComplex.GetLocationWithId("village_center").GetSceneName(0);
            row["settlement"] = village.StringId; row["name"] = village.Name.ToString(); row["expectedScene"] = expected;
            yield return Enter(st, row, village, "village");
            RuntimeMissionProbe.RunOption("village_center");
            yield return RunMission(st, row, false, null, m => m.EndMission(), "center");
            yield return Menu(st, "village");
            yield return Leave(st, row, "leave");
            var mission = row.ContainsKey("center") ? (Dictionary<string, object>)row["center"] : null;
            if (mission == null) failures.Add("the village center did not open");
            else if (!String.Equals((string)mission["scene"], expected, StringComparison.Ordinal)) failures.Add("the village opened " + mission["scene"] + ", not the mapped " + expected);
        }

        // The player at the settlement's gate, the official settlement encounter, its menu.
        internal static IEnumerator Enter(State st, Dictionary<string, object> row, Settlement settlement, string menu)
        {
            var main = MobileParty.MainParty;
            main.SetMoveModeHold();
            // The party is placed at the gate; every placement is recorded with where it came from and how far (MMR-53: a voyage's next step
            // starts where the leave path put the party, a few hundredths away).
            var from = main.Position.ToVec2();
            main.Position = settlement.GatePosition;
            var placed = row.ContainsKey("relocations") ? row["relocations"] as List<object> : null;
            if (placed == null) row["relocations"] = placed = new List<object>();
            placed.Add(new Dictionary<string, object> { ["to"] = settlement.StringId, ["from"] = new[] { from.X, from.Y }, ["distance"] = from.Distance(settlement.GatePosition.ToVec2()) });
            yield return RuntimeMissionProbe.SettleCamera(st.Context);
            row["before"] = RuntimeMissionProbe.Snapshot(false);
            EncounterManager.StartSettlementEncounter(main, settlement);
            yield return Menu(st, menu);
            row["entered"] = main.CurrentSettlement == settlement && PlayerEncounter.EncounterSettlement == settlement;
        }

        private static IEnumerator Leave(State st, Dictionary<string, object> row, string option)
        {
            var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            RuntimeMissionProbe.RunOption(option);
            yield return Idle(st, 30);
            row["after"] = RuntimeMissionProbe.Snapshot(false);
            row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
            if (row["afterNavigation"] != null) failures.Add("the party left off navigation: " + row["afterNavigation"]);
            ReturnTo(st, row, failures, PositionOf((Dictionary<string, object>)row["before"]), "the settlement's gate, where the party entered");
        }

        // In a location mission: the first agent the preferred rule picks (else the fallback rule), the installed mission conversation with it,
        // its agent and first line recorded, then ended.
        private static IEnumerator Converse(State st, Mission m, Dictionary<string, object> row, string key, Func<Agent, bool> prefer, Func<Agent, bool> fallback)
        {
            var record = new Dictionary<string, object> { ["started"] = false }; row[key] = record;
            double start = st.Context.Seconds;
            Agent target = null;
            while (st.Context.Seconds - start < 8 && target == null)
            {
                var people = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != m.MainAgent && a.Character != null).ToList();
                target = people.Where(prefer).OrderBy(a => a.Character.StringId, StringComparer.Ordinal).FirstOrDefault()
                      ?? (fallback == null ? null : people.Where(fallback).OrderBy(a => a.Character.StringId, StringComparer.Ordinal).FirstOrDefault());
                if (target == null) yield return null;
            }
            record["agents"] = m.Agents.Count(a => a != null && a.IsHuman);
            if (target == null) { record["error"] = "no agent to speak to"; yield break; }
            var hero = HeroOf(target); var character = target.Character as CharacterObject;
            record["agent"] = target.Name; record["character"] = target.Character.StringId; record["hero"] = hero == null ? null : hero.StringId;
            record["notable"] = hero != null && hero.IsNotable; record["occupation"] = character == null ? null : character.Occupation.ToString();
            var logic = m.GetMissionBehavior<MissionConversationLogic>();
            if (logic == null) { record["error"] = "no MissionConversationLogic in the mission"; yield break; }
            logic.StartConversation(target, true, false);
            var conversations = Campaign.Current.ConversationManager;
            start = st.Context.Seconds;
            while (!conversations.IsConversationInProgress && st.Context.Seconds - start < 8) yield return null;
            record["started"] = conversations.IsConversationInProgress;
            if (!conversations.IsConversationInProgress) yield break;
            start = st.Context.Seconds;
            while (st.Context.Seconds - start < 1.5) yield return null;
            record["withAgent"] = ReferenceEquals(conversations.OneToOneConversationAgent, target);
            record["conversationHero"] = conversations.OneToOneConversationHero == null ? null : conversations.OneToOneConversationHero.StringId;
            record["firstLine"] = conversations.CurrentSentenceText;
            conversations.EndConversation();
            start = st.Context.Seconds;
            while (conversations.IsConversationInProgress && st.Context.Seconds - start < 8) yield return null;
            record["ended"] = !conversations.IsConversationInProgress;
        }

        internal static object Field(object o, string name)
        {
            var info = o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            try { return info == null ? null : info.GetValue(o); } catch (Exception e) { return e.GetType().Name; }
        }

        internal static object Property(object o, string name)
        {
            var info = o.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            try { return info == null ? null : info.GetValue(o); } catch (Exception e) { return e.GetType().Name; }
        }

        private static Hero HeroOf(Agent a) { var c = a.Character as CharacterObject; return c == null ? null : c.HeroObject; }

        // ---------------------------------------------------------------- hideout, siege, capture

        private static IEnumerator HideoutCase(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var hideout = Settlement.All.Where(s => s.IsHideout && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && (s.SettlementComponent as Hideout) != null && ((Hideout)s.SettlementComponent).IsInfested && s.Position.IsOnLand
                    && GeneratedSpawnAssertions.Problem(s.GatePosition, MobileParty.NavigationType.Default) == null)
                .OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (hideout == null) throw new InvalidOperationException("no infested land hideout");
            row["settlement"] = hideout.StringId; row["parties"] = hideout.Parties.Count(p => p.IsBandit);
            // The hideout mission opens its hideout_center location's level-0 scene (HideoutCampaignBehavior.OnTroopRosterManageDone).
            var center = hideout.LocationComplex == null ? null : hideout.LocationComplex.GetLocationWithId("hideout_center");
            row["expectedScene"] = center == null ? null : center.GetSceneName(0);
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            Heal(); setup["crewAdded"] = Crew(main, (int)st.Rule("playerTroops"));
            // The installed options: sneaking in (attack) in the hideout model's night hours opens the stealth ambush mission
            // (HideoutAmbushMissionController); the assault outside them opens HideoutMissionController, the mission this case plays. Inside the
            // night hours, campaign time runs (the player sheltered) until they end.
            int from = Campaign.Current.Models.HideoutModel.CanAttackHideoutStartTime, to = Campaign.Current.Models.HideoutModel.CanAttackHideoutEndTime;
            setup["attackHours"] = new[] { from, to };
            Func<bool> night = () => { float h = CampaignTime.Now.CurrentHourInDay; return from <= to ? h >= from && h < to : h >= from || h < to; };
            double hours = CampaignTime.Now.ToHours;
            if (night())
            {
                DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                double wait = context.Seconds;
                while (night() && context.Seconds - wait < 120) yield return null;
                RuntimeMissionProbe.Pause();
            }
            setup["hoursRunPastNight"] = CampaignTime.Now.ToHours - hours;
            yield return Enter(st, row, hideout, "hideout_place");
            setup["hourOfDay"] = CampaignTime.Now.CurrentHourInDay;
            string option = FirstEnabled("assault");
            setup["option"] = option;
            if (option == null) throw new InvalidOperationException("the hideout's assault is not enabled (see menus.hideout_place)");
            // The option opens the troop selection (MenuContext.OpenTroopSelection); it is answered with the selection it offers.
            var answer = TroopSelectionAnswer.Install(setup);
            try { RuntimeMissionProbe.RunOption(option); }
            finally { answer.Remove(); }
            if (!setup.ContainsKey("troopSelection")) throw new InvalidOperationException("the hideout option opened no troop selection");
            var fight = new Dictionary<string, object>(); row["fight"] = fight;
            yield return RunMission(st, row, true, m => ClearHideout(st, m, fight, row), m => m.EndMission(), "mission");
            yield return Idle(st, 60);
            row["after"] = RuntimeMissionProbe.Snapshot(false);
            row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
            var component = (Hideout)hideout.SettlementComponent;
            row["infestedAfter"] = component.IsInfested; row["banditPartiesAfter"] = hideout.Parties.Count(p => p.IsBandit);
            if (row["afterNavigation"] != null) failures.Add("the party returned off navigation: " + row["afterNavigation"]);
            ReturnTo(st, row, failures, PositionOf((Dictionary<string, object>)row["before"]), "the hideout's gate, where the party entered");
            if (component.IsInfested) failures.Add("the hideout is still infested after the fight");
            var mission = row.ContainsKey("mission") ? (Dictionary<string, object>)row["mission"] : null;
            if (mission == null || !String.Equals((string)mission["scene"], (string)row["expectedScene"], StringComparison.Ordinal))
                failures.Add("the hideout mission opened " + (mission == null ? "nothing" : mission["scene"]) + ", not the mapped " + row["expectedScene"]);
        }

        // The boss's dialogue (HideoutConversationsCampaignBehavior): once its line is shown, the player's option is chosen as a click chooses it
        // (ConversationManager.DoOption): "I don't fight duels with brigands" (the battle) when clickable, else the duel; the option's consequence
        // queues the fight mode for the conversation's end, and the conversation closes (its close_window, continued as a click continues it).
        // Each step is checkpointed: run 25280 hung when the probe ended this conversation on its first frame and called the battle mode itself.
        private static IEnumerator BossDialogue(State st, TaleWorlds.CampaignSystem.Conversation.ConversationManager conversations, Dictionary<string, object> fight)
        {
            var steps = new List<string>(); fight["bossDialogueSteps"] = steps;
            Action<string> step = s => { steps.Add(s); st.Context.Checkpoint(); };
            double start = st.Context.Seconds;
            step("conversation open");
            while (conversations.IsConversationInProgress && st.Context.Seconds - start < 3) yield return null;
            fight["bossConversationLine"] = conversations.CurrentSentenceText;
            var options = conversations.CurOptions == null ? new List<TaleWorlds.CampaignSystem.Conversation.ConversationSentenceOption>() : conversations.CurOptions.ToList();
            fight["bossOptions"] = options.Select(o => (object)new Dictionary<string, object> { ["id"] = o.Id, ["clickable"] = o.IsClickable, ["text"] = o.Text == null ? null : o.Text.ToString() }).ToList();
            int index = options.FindIndex(o => o.Id == "bandit_hideout_start_defender_2" && o.IsClickable);
            if (index < 0) index = options.FindIndex(o => o.Id == "bandit_hideout_start_defender_1" && o.IsClickable);
            if (index < 0) { fight["bossDialogueError"] = "no clickable option of the boss's dialogue"; step("no option"); yield break; }
            fight["bossOption"] = options[index].Id;
            step("choosing " + options[index].Id);
            conversations.DoOption(index);
            step("chosen");
            start = st.Context.Seconds; double lastContinue = start;
            while (conversations.IsConversationInProgress && st.Context.Seconds - start < 12)
            {
                if (st.Context.Seconds - lastContinue > 1.5) { lastContinue = st.Context.Seconds; step("continue"); conversations.ContinueConversation(); }
                yield return null;
            }
            fight["bossConversationEnded"] = !conversations.IsConversationInProgress;
            step(conversations.IsConversationInProgress ? "still open" : "closed");
        }

        internal static string FirstEnabled(params string[] ids)
        {
            var map = (MapState)Game.Current.GameStateManager.ActiveState; var menu = map.MenuContext.GameMenu;
            foreach (var id in ids)
            {
                var o = menu.MenuOptions.FirstOrDefault(x => x.IdString == id);
                if (o != null && o.GetConditionsHold(Game.Current, map.MenuContext) && o.IsEnabled) return id;
            }
            return null;
        }

        // The troop selection a menu option opens, answered with the selection it offers (as the player pressing Done without changes); every
        // other call passes to the menu's own handler, which is put back once the selection is answered or the option has run.
        internal sealed class TroopSelectionAnswer : IMenuContextHandler
        {
            private readonly MenuContext _context; private readonly IMenuContextHandler _inner; private readonly Dictionary<string, object> _record; private readonly bool _naval;

            private TroopSelectionAnswer(MenuContext context, Dictionary<string, object> record, bool naval) { _context = context; _inner = context.Handler; _record = record; _naval = naval; }

            // naval: the naval troop selection (troops and ships, a seaborne raid's) is answered too, with the troops and ships it offers.
            internal static TroopSelectionAnswer Install(Dictionary<string, object> record, bool naval = false)
            {
                var context = ((MapState)Game.Current.GameStateManager.ActiveState).MenuContext;
                var answer = new TroopSelectionAnswer(context, record, naval);
                context.Handler = answer;
                return answer;
            }

            internal void Remove() { if (ReferenceEquals(_context.Handler, this)) _context.Handler = _inner; }

            public void OnOpenTroopSelection(TroopRoster fullRoster, TroopRoster initialSelections, Func<CharacterObject, bool> canChangeStatusOfTroop, Action<TroopRoster> onDone, int maxSelectableTroopCount, int minSelectableTroopCount)
            {
                _record["troopSelection"] = new Dictionary<string, object> { ["offered"] = fullRoster.TotalManCount, ["selected"] = initialSelections.TotalManCount,
                    ["max"] = maxSelectableTroopCount, ["min"] = minSelectableTroopCount };
                Remove();
                onDone(initialSelections);
            }

            public void OnOpenNavalTroopSelection(TroopRoster fullRoster, TroopRoster initialTroopSelections, List<TaleWorlds.CampaignSystem.Naval.Ship> eligibleShips, List<TaleWorlds.CampaignSystem.Naval.Ship> initialShipSelections,
                Func<CharacterObject, bool> canChangeStatusOfTroop, Action<TroopRoster, List<TaleWorlds.CampaignSystem.Naval.Ship>> onDone, int minSelectableTroopCount, int minSelectableShipCount, int maxSelectableShipCount, bool anyOtherPartiesOnPlayerSide)
            {
                if (!_naval) { if (_inner != null) _inner.OnOpenNavalTroopSelection(fullRoster, initialTroopSelections, eligibleShips, initialShipSelections, canChangeStatusOfTroop, onDone, minSelectableTroopCount, minSelectableShipCount, maxSelectableShipCount, anyOtherPartiesOnPlayerSide); return; }
                _record["navalTroopSelection"] = new Dictionary<string, object> { ["offered"] = fullRoster.TotalManCount, ["selected"] = initialTroopSelections.TotalManCount, ["minTroops"] = minSelectableTroopCount,
                    ["eligibleShips"] = eligibleShips.Count, ["ships"] = initialShipSelections.Select(s => (object)((TaleWorlds.Core.IShipOrigin)s).Hull.StringId).ToList(), ["minShips"] = minSelectableShipCount, ["maxShips"] = maxSelectableShipCount };
                Remove();
                onDone(initialTroopSelections, initialShipSelections);
            }

            public void OnBackgroundMeshNameSet(string name) { if (_inner != null) _inner.OnBackgroundMeshNameSet(name); }
            public void OnOpenTownManagement() { if (_inner != null) _inner.OnOpenTownManagement(); }
            public void OnOpenRecruitVolunteers() { if (_inner != null) _inner.OnOpenRecruitVolunteers(); }
            public void OnOpenTournamentLeaderboard() { if (_inner != null) _inner.OnOpenTournamentLeaderboard(); }
            public void OnMenuCreate() { if (_inner != null) _inner.OnMenuCreate(); }
            public void OnMenuActivate() { if (_inner != null) _inner.OnMenuActivate(); }
            public void OnMenuRefresh() { if (_inner != null) _inner.OnMenuRefresh(); }
            public void OnHourlyTick() { if (_inner != null) _inner.OnHourlyTick(); }
            public void OnPanelSoundIDSet(string panelSoundID) { if (_inner != null) _inner.OnPanelSoundIDSet(panelSoundID); }
            public void OnAmbientSoundIDSet(string ambientSoundID) { if (_inner != null) _inner.OnAmbientSoundIDSet(ambientSoundID); }
        }

        // The bandits fall wave after wave (Agent.Die by the player's agent); the boss's dialogue, when it opens, is answered as the player
        // answers it (BossDialogue); until the mission is won.
        // The controller's phases (HideoutMissionController._hideoutMissionState): the first fight, the cutscene before the boss fight, the
        // leaders' conversation, the boss fight (duel or all). Bandits fall only in a fight phase, as a player can strike only then; the boss
        // (_bossAgent) only in the boss fight. Each phase change is recorded and checkpointed.
        private static readonly HashSet<string> FightPhases = new HashSet<string>(new[] { "InitialFightBeforeBossFight", "WithoutBossFight", "BossFightWithDuel", "BossFightWithAll" }, StringComparer.Ordinal);

        private static IEnumerator ClearHideout(State st, Mission m, Dictionary<string, object> fight, Dictionary<string, object> row)
        {
            row["gear"] = Gear(st, m);
            int killed = 0, bossConversations = 0; double start = st.Context.Seconds, last = start;
            var conversations = Campaign.Current.ConversationManager;
            var controller = m.GetMissionBehavior<HideoutMissionController>();
            fight["controller"] = controller != null;
            var phases = new List<object>(); fight["phases"] = phases;
            string phase = null;
            while (st.Context.Seconds - start < 240 && Mission.Current == m && !m.MissionEnded)
            {
                if (m.MissionResult != null && m.MissionResult.PlayerVictory) break;
                object state = controller == null ? null : Field(controller, "_hideoutMissionState");
                string now = state == null ? null : state.ToString();
                if (now != phase) { phase = now; phases.Add(new Dictionary<string, object> { ["phase"] = phase, ["seconds"] = Math.Round(st.Context.Seconds - start, 2), ["killed"] = killed }); st.Context.Checkpoint(); }
                if (conversations.IsConversationInProgress)
                {
                    bossConversations++;
                    yield return BossDialogue(st, conversations, fight);
                }
                else if (st.Context.Seconds - last > 0.5 && m.MainAgent != null && m.PlayerEnemyTeam != null && (controller == null || FightPhases.Contains(phase)))
                {
                    last = st.Context.Seconds;
                    var boss = controller == null ? null : Field(controller, "_bossAgent") as Agent;
                    bool bossFight = phase == "BossFightWithDuel" || phase == "BossFightWithAll";
                    foreach (var a in m.PlayerEnemyTeam.ActiveAgents.ToList())
                        if (a.IsHuman && a.IsActive() && (bossFight || a != boss)) { Kill(a, m.MainAgent); killed++; }
                }
                yield return null;
            }
            fight["killed"] = killed; fight["bossConversations"] = bossConversations; fight["seconds"] = st.Context.Seconds - start;
            fight["victory"] = m.MissionResult != null && m.MissionResult.PlayerVictory;
            start = st.Context.Seconds;
            while (Mission.Current == m && !m.MissionEnded && st.Context.Seconds - start < 10) yield return null;
        }

        internal static void Kill(Agent victim, Agent by)
        {
            var blow = new Blow(by == null ? -1 : by.Index) { DamageType = DamageTypes.Blunt, BaseMagnitude = 2000f, InflictedDamage = 2000, GlobalPosition = victim.Position, Direction = Vec3.Up, VictimBodyPart = BoneBodyPartType.Chest };
            victim.Die(blow, Agent.KillInfo.Invalid);
        }

        private static IEnumerator Siege(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            // The castle farthest from its own kingdom's lord parties (they come to relieve a siege; an encounter with one stops the camp's
            // preparation), its distance recorded.
            var lords = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.MapFaction is Kingdom).ToList();
            Func<Settlement, float> nearestLord = s => lords.Where(p => p.MapFaction == s.MapFaction).Select(p => p.Position.ToVec2().Distance(s.Position.ToVec2())).DefaultIfEmpty(float.MaxValue).Min();
            var target = Settlement.All.Where(s => s.IsCastle && s.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && s.SiegeEvent == null && s.MapFaction is Kingdom && s.LocationComplex != null
                    && GeneratedSpawnAssertions.Problem(s.GatePosition, MobileParty.NavigationType.Default) == null)
                .OrderByDescending(nearestLord).ThenBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (target == null) throw new InvalidOperationException("no castle to besiege");
            row["nearestDefendingLord"] = nearestLord(target); row["garrison"] = target.Town.GarrisonParty == null ? 0 : target.Town.GarrisonParty.MemberRoster.TotalManCount;
            int wall = target.Town.GetWallLevel();
            string expected = target.LocationComplex.GetLocationWithId("center").GetSceneName(wall);
            row["settlement"] = target.StringId; row["expectedScene"] = expected; row["wallLevel"] = wall;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            var war = target.MapFaction;
            if (!war.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, war); setup["warDeclared"] = war.StringId; }
            Heal(); setup["crewAdded"] = Crew(main, (int)st.Rule("siegeTroops"));
            try
            {
                var point = RuntimeMissionProbe.FindPoint(target.GatePosition, MobileParty.NavigationType.Default, 2, 1);
                main.SetMoveModeHold(); main.Position = point;
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["before"] = RuntimeMissionProbe.Snapshot(false);
                EncounterManager.StartSettlementEncounter(main, target);
                double start = context.Seconds;
                while (CurrentMenu() != "castle_outside" && CurrentMenu() != "town_outside" && context.Seconds - start < 30) yield return null;
                RecordMenu(st);
                setup["outsideMenu"] = CurrentMenu();
                RuntimeMissionProbe.RunOption("town_besiege");
                yield return Menu(st, "menu_siege_strategies");
                var camped = main.Position.ToVec2(); setup["campPosition"] = new[] { camped.X, camped.Y };
                // The camp prepares as campaign time runs (BesiegerCamp.PreparationProgress); the player sheltered, the menu open.
                double hours = CampaignTime.Now.ToHours;
                var siege = target.SiegeEvent;
                var camp = siege == null ? null : siege.BesiegerCamp;
                if (camp != null && camp.SiegeEngines != null && camp.SiegeEngines.SiegePreparations != null)
                    setup["preparationPerHour"] = Campaign.Current.Models.SiegeEventModel.GetConstructionProgressPerHour(camp.SiegeEngines.SiegePreparations.SiegeEngine, siege, camp);
                // Every half campaign hour: the menu, the siege, the camp's progress, the player's map event; an encounter (a sally-out or a relief)
                // stops the preparation and the case, recorded with its parties. The camp's own preparation is observed for siegeObserveHours;
                // then it is brought to siegeCompletionFrom (diagnostic setup, recorded: at the installed rate the camp needs about four days, in
                // which the garrison sallies out, run 17928 - a stock mechanic MMR-52 does not retest) and the engine's tick completes it.
                var series = new List<object>(); setup["series"] = series;
                DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                start = context.Seconds;
                double lastHours = hours, lastProgress = start, nextSample = hours;
                bool advanced = false;
                while (camp != null && !camp.IsPreparationComplete && CampaignTime.Now.ToHours - hours < st.Rule("siegePreparationHours") && context.Seconds - start < 240)
                {
                    double now = CampaignTime.Now.ToHours;
                    if (now >= nextSample && series.Count < 60) { nextSample = now + 0.5; series.Add(SiegeSample(target, siege, camp, now - hours)); }
                    if (!advanced && now - hours >= st.Rule("siegeObserveHours") && main.MapEvent == null)
                    {
                        advanced = true;
                        object observed = Property(camp, "PreparationProgress");
                        float to = (float)st.Rule("siegeCompletionFrom");
                        camp.SiegeEngines.SiegePreparations.SetProgress(to);
                        setup["preparationAdvanced"] = new Dictionary<string, object> { ["atHours"] = now - hours, ["observedProgress"] = observed, ["to"] = to,
                            ["naturalHoursNeeded"] = setup.ContainsKey("preparationPerHour") && Convert.ToDouble(setup["preparationPerHour"], CultureInfo.InvariantCulture) > 0 ? (object)(1.0 / Convert.ToDouble(setup["preparationPerHour"], CultureInfo.InvariantCulture)) : null };
                    }
                    if (main.MapEvent != null) { setup["interruption"] = Interruption(main.MapEvent, now - hours); break; }
                    if (target.SiegeEvent != siege) { setup["interruption"] = new Dictionary<string, object> { ["hours"] = now - hours, ["siegeEnded"] = true, ["menu"] = CurrentMenu() }; break; }
                    if (now > lastHours) { lastHours = now; lastProgress = context.Seconds; }
                    else if (context.Seconds - lastProgress > 15) { setup["timeStalled"] = TwoWorldMatrixProbe.TimeState(context.Seconds); break; }
                    if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppableFastForward);
                    yield return null;
                }
                RuntimeMissionProbe.Pause();
                series.Add(SiegeSample(target, siege, camp, CampaignTime.Now.ToHours - hours));
                setup["preparationHours"] = CampaignTime.Now.ToHours - hours; setup["preparationProgress"] = camp == null ? null : Property(camp, "PreparationProgress");
                setup["prepared"] = camp != null && camp.IsPreparationComplete; setup["menuAfterPreparation"] = CurrentMenu();
                if (camp == null || !camp.IsPreparationComplete)
                    throw new InvalidOperationException("the siege camp was not prepared" + (setup.ContainsKey("interruption") ? " (interrupted: see setup.interruption)" : setup.ContainsKey("timeStalled") ? " (campaign time stalled)" : ""));
                if (CurrentMenu() != "menu_siege_strategies") yield return Menu(st, "menu_siege_strategies");
                RuntimeMissionProbe.RunOption("menu_siege_strategies_lead_assault");
                yield return RunMission(st, row, true, m => GearAndSample(st, m, row), m => m.OnEndMissionResult(), "mission");
                // Back at the siege: the mission returned the party to its camp; then the siege is lifted through the menu.
                start = context.Seconds;
                while (CurrentMenu() == "the map" && context.Seconds - start < 10) yield return null;
                setup["menuAfterAssault"] = CurrentMenu();
                ReturnTo(st, row, failures, camped, "the siege camp, where the assault began");
                if (CurrentMenu() == "encounter" && PlayerEncounter.Current != null) { PlayerEncounter.Finish(true); yield return TwoWorldMatrixProbe.Frames(10); }
                if (CurrentMenu() == "menu_siege_strategies")
                {
                    RecordMenu(st);
                    RuntimeMissionProbe.RunOption("menu_siege_strategies_leave");
                    start = context.Seconds;
                    while (CurrentMenu() != "menu_siege_strategies_break_siege" && CurrentMenu() != "the map" && context.Seconds - start < 10) yield return null;
                    if (CurrentMenu() == "menu_siege_strategies_break_siege") { RecordMenu(st); RuntimeMissionProbe.RunOption("menu_siege_strategies_break_siege_go_on"); }
                }
                yield return Idle(st, 30);
                row["after"] = RuntimeMissionProbe.Snapshot(false);
                row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                if (row["afterNavigation"] != null) failures.Add("the party returned off navigation: " + row["afterNavigation"]);
                var mission = row.ContainsKey("mission") ? (Dictionary<string, object>)row["mission"] : null;
                if (mission == null) failures.Add("the assault did not open");
                else
                {
                    if (!String.Equals((string)mission["scene"], expected, StringComparison.Ordinal)) failures.Add("the assault opened " + mission["scene"] + ", not the mapped " + expected);
                    if (!Equals(mission.ContainsKey("deployed") ? mission["deployed"] : null, true)) failures.Add("the assault had no deployment to finish");
                }
                row["siegeAfter"] = target.SiegeEvent != null;
            }
            finally { if (war.IsAtWarWith(Clan.PlayerClan) && setup.ContainsKey("warDeclared")) { MakePeaceAction.Apply(Clan.PlayerClan, war); setup["peaceMade"] = !war.IsAtWarWith(Clan.PlayerClan); } }
        }

        private static Dictionary<string, object> SiegeSample(Settlement target, TaleWorlds.CampaignSystem.Siege.SiegeEvent siege, TaleWorlds.CampaignSystem.Siege.BesiegerCamp camp, double hours)
        {
            var main = MobileParty.MainParty;
            return new Dictionary<string, object> { ["hours"] = Math.Round(hours, 2), ["menu"] = CurrentMenu(), ["siegeSame"] = target.SiegeEvent == siege && siege != null,
                ["progress"] = camp == null ? null : Property(camp, "PreparationProgress"), ["playerBesieging"] = main.BesiegerCamp == camp && camp != null, ["playerMapEvent"] = main.MapEvent != null,
                ["mode"] = Campaign.Current.TimeControlMode.ToString() };
        }

        internal static Dictionary<string, object> Interruption(TaleWorlds.CampaignSystem.MapEvents.MapEvent e, double hours)
        {
            Func<TaleWorlds.CampaignSystem.MapEvents.MapEventSide, object> side = s => s == null ? null : s.Parties.Select(x => x.Party).Select(p => (object)new Dictionary<string, object> {
                ["party"] = p.MobileParty != null ? p.MobileParty.StringId : p.Settlement != null ? p.Settlement.StringId : "?", ["kind"] = p.MobileParty == null ? "settlement" : GeneratedSpawnAssertions.KindOf(p.MobileParty),
                ["faction"] = p.MapFaction == null ? null : p.MapFaction.StringId, ["healthy"] = p.NumberOfHealthyMembers }).ToList();
            return new Dictionary<string, object> { ["hours"] = hours, ["menu"] = CurrentMenu(), ["eventType"] = e.EventType.ToString(), ["attackers"] = side(e.AttackerSide), ["defenders"] = side(e.DefenderSide) };
        }

        private static IEnumerator Capture(State st, Dictionary<string, object> row)
        {
            var context = st.Context; var failures = (List<string>)row["failures"]; var main = MobileParty.MainParty;
            var setup = new Dictionary<string, object>(); row["setup"] = setup;
            // The player's party small, the enemy a stronger lord party (its kingdom put at war with the player).
            foreach (var e in main.MemberRoster.GetTroopRoster().ToList()) if (!e.Character.IsHero && e.Number > 3) main.MemberRoster.AddToCounts(e.Character, -(e.Number - 3));
            Heal();
            setup["playerTroops"] = main.MemberRoster.TotalManCount;
            IFaction war = null;
            var enemy = MobileParty.All.Where(p => p != main && p.IsActive && p.IsLordParty && p.MapEvent == null && p.CurrentSettlement == null && p.Army == null && p.BesiegerCamp == null && p.Ships.Count == 0
                    && !p.IsCurrentlyAtSea && p.Position.IsOnLand && p.MapFaction is Kingdom && p.MemberRoster.TotalHealthyCount >= 3 * main.MemberRoster.TotalManCount && p.StringId.StartsWith(st.Prefix, StringComparison.Ordinal))
                .OrderByDescending(p => p.MemberRoster.TotalHealthyCount).ThenBy(p => p.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (enemy == null) throw new InvalidOperationException("no stronger hostile lord party");
            if (!enemy.MapFaction.IsAtWarWith(main.MapFaction)) { DeclareWarAction.ApplyByDefault(Clan.PlayerClan, enemy.MapFaction); war = enemy.MapFaction; }
            setup["enemy"] = enemy.StringId; setup["enemyTroops"] = enemy.MemberRoster.TotalManCount; setup["warDeclared"] = war == null ? null : war.StringId;
            try
            {
                var point = RuntimeMissionProbe.FindPoint(main.Position.IsOnLand ? main.Position : enemy.Position, MobileParty.NavigationType.Default, 2, 1);
                enemy.Position = point; enemy.SetMoveModeHold();
                yield return RuntimeMissionProbe.SettleCamera(context);
                row["before"] = RuntimeMissionProbe.Snapshot(false);
                PlayerEncounter.RestartPlayerEncounter(enemy.Party, main.Party, true, false);
                PlayerEncounter.StartBattle();
                GameMenu.ActivateGameMenu("encounter");
                yield return Menu(st, "encounter");
                RuntimeMissionProbe.RunOption("attack");
                var fight = new Dictionary<string, object>(); row["fight"] = fight;
                yield return RunMission(st, row, true, m => Lose(st, m, fight, row), m => m.EndMission(), "mission");
                // The campaign takes the player prisoner (its encounter menus pass as the player would pass them).
                double start = context.Seconds; var menus = new List<string>();
                while (!Hero.MainHero.IsPrisoner && context.Seconds - start < 60)
                {
                    string menu = CurrentMenu();
                    if (menus.Count == 0 || menus[menus.Count - 1] != menu) menus.Add(menu);
                    if (Game.Current.GameStateManager.ActiveState is InventoryState) { try { InventoryScreenHelper.CloseScreen(true); } catch (Exception) { } }
                    yield return null;
                }
                row["menusAfterDefeat"] = menus;
                row["captured"] = Hero.MainHero.IsPrisoner;
                row["captor"] = Hero.MainHero.PartyBelongedToAsPrisoner == null ? null : Hero.MainHero.PartyBelongedToAsPrisoner.MobileParty != null ? Hero.MainHero.PartyBelongedToAsPrisoner.MobileParty.StringId : Hero.MainHero.PartyBelongedToAsPrisoner.Settlement != null ? Hero.MainHero.PartyBelongedToAsPrisoner.Settlement.StringId : "?";
                row["captivityMenu"] = CurrentMenu();
                if (!Hero.MainHero.IsPrisoner) throw new InvalidOperationException("the defeat did not lead to capture (menus " + String.Join(" > ", menus) + ")");
                RecordMenu(st);
                yield return TwoWorldMatrixProbe.Frames(10);
                // The escape: the installed action, as the captivity's hourly escape applies it (no notification); PlayerCaptivity.EndCaptivityInternal
                // puts the party at its captor party's position (a settlement's gate or port when held there) and out of the encounter radius
                // (MobileParty.TeleportPartyToOutSideOfEncounterRadius: within 1.25 x EncounterModel.NeededMaximumLandDistanceForEncounteringMobileParty);
                // the release's OnHeroPrisonerReleased listener then protects the player (PlayerEncounter.ProtectPlayerSide), which teleports it
                // once more: within 2 x 1.25 x that radius of the captor (run 35564: 1.188 with a radius of 0.5).
                // Campaign time runs in captivity; it is stopped first, so the release is measured before the party moves on (run 37204).
                row["timeModeInCaptivity"] = Campaign.Current.TimeControlMode.ToString();
                RuntimeMissionProbe.Pause();
                var captor = Hero.MainHero.PartyBelongedToAsPrisoner;
                var released = captor == null ? main.Position.ToVec2() : captor.IsMobile ? captor.MobileParty.Position.ToVec2() : captor.Settlement.GatePosition.ToVec2();
                row["encounterRadius"] = Campaign.Current.Models.EncounterModel.NeededMaximumLandDistanceForEncounteringMobileParty;
                EndCaptivityAction.ApplyByEscape(Hero.MainHero, null, false);
                RuntimeMissionProbe.Pause();
                row["releasedDisplacement"] = main.Position.ToVec2().Distance(released);
                start = context.Seconds; menus = new List<string>();
                while (context.Seconds - start < 30)
                {
                    string menu = CurrentMenu();
                    if (menus.Count == 0 || menus[menus.Count - 1] != menu) menus.Add(menu);
                    if (menu == "menu_captivity_end_wilderness_escape" || menu == "menu_captivity_end_prison_escape") { RecordMenu(st); RuntimeMissionProbe.RunOption("mno_continue"); break; }
                    if (menu == "the map" && !Hero.MainHero.IsPrisoner && main.IsActive) break;
                    yield return null;
                }
                row["menusAfterEscape"] = menus;
                yield return Idle(st, 30);
                row["escaped"] = !Hero.MainHero.IsPrisoner && main.IsActive;
                row["after"] = RuntimeMissionProbe.Snapshot(false);
                row["afterNavigation"] = GeneratedSpawnAssertions.Problem(main);
                if (row["afterNavigation"] != null) failures.Add("the party was freed off navigation: " + row["afterNavigation"]);
                ReturnTo(st, row, failures, released, "the captor's position, where the escape releases the party", 2 * 1.25 * Convert.ToDouble(row["encounterRadius"], CultureInfo.InvariantCulture) + 0.01);
            }
            finally { if (war != null && war.IsAtWarWith(Clan.PlayerClan)) { MakePeaceAction.Apply(Clan.PlayerClan, war); setup["peaceMade"] = !war.IsAtWarWith(Clan.PlayerClan); } }
        }

        // Every agent of the player's team falls (the main agent knocked out) until the mission's result is the player's defeat.
        internal static IEnumerator Lose(State st, Mission m, Dictionary<string, object> fight, Dictionary<string, object> row)
        {
            row["gear"] = Gear(st, m);
            int fallen = 0; double start = st.Context.Seconds, last = 0;
            while (st.Context.Seconds - start < 60 && Mission.Current == m && !m.MissionEnded)
            {
                if (m.MissionResult != null && m.MissionResult.PlayerDefeated) break;
                if (st.Context.Seconds - last > 0.5 && m.PlayerTeam != null)
                {
                    last = st.Context.Seconds;
                    var by = m.PlayerEnemyTeam == null ? null : m.PlayerEnemyTeam.ActiveAgents.FirstOrDefault(a => a.IsHuman);
                    foreach (var a in m.PlayerTeam.ActiveAgents.ToList()) if (a.IsHuman && a.IsActive()) { Kill(a, by); fallen++; }
                }
                yield return null;
            }
            fight["fallen"] = fallen; fight["seconds"] = st.Context.Seconds - start;
            fight["defeated"] = m.MissionResult != null && m.MissionResult.PlayerDefeated;
        }

        // ---------------------------------------------------------------- audits

        internal static Dictionary<string, object> Locations(State st)
        {
            var audit = MNBL.ProceduralWorld.Missions.LocationSceneAudit.Run(st.WorldId);
            return new Dictionary<string, object> { ["catalogueSha256"] = audit.CatalogueSha256, ["settlements"] = audit.Settlements.Count, ["sceneReferences"] = audit.References,
                ["problems"] = audit.Problems.Take(30).ToList(), ["problemCount"] = audit.Problems.Count, ["engineMissing"] = audit.EngineMissing.ToList() };
        }

        private static readonly Regex Unresolved = new Regex(@"\{=[^}]*\}|\{[A-Z_][A-Z0-9_.]*\}", RegexOptions.CultureInvariant);

        // Every generated settlement, clan, kingdom and lord: a resolved name, a resolved encyclopedia link, a page that accepts it.
        internal static Dictionary<string, object> Names(State st)
        {
            var problems = new List<string>(); int checkedCount = 0;
            var manager = Campaign.Current.EncyclopediaManager;
            Action<string, object, TaleWorlds.Localization.TextObject, TaleWorlds.Localization.TextObject> check = (kind, obj, name, link) =>
            {
                checkedCount++;
                string n = null, l = null;
                try { n = name == null ? null : name.ToString(); l = link == null ? null : link.ToString(); } catch (Exception e) { problems.Add(kind + " " + obj + ": " + e.GetType().Name); return; }
                if (String.IsNullOrWhiteSpace(n) || Unresolved.IsMatch(n)) problems.Add(kind + " " + obj + ": name '" + n + "'");
                if (String.IsNullOrWhiteSpace(l) || Unresolved.IsMatch(l)) problems.Add(kind + " " + obj + ": encyclopedia link '" + l + "'");
                var page = manager.GetPageOf(obj.GetType());
                if (page == null || !page.IsValidEncyclopediaItem(obj)) problems.Add(kind + " " + obj + ": no encyclopedia page accepts it");
            };
            foreach (var s in Settlement.All.Where(x => x.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && (x.IsTown || x.IsCastle || x.IsVillage))) check("settlement", s, s.Name, s.EncyclopediaLinkWithName);
            foreach (var c in Clan.All.Where(x => x.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && !x.IsEliminated)) check("clan", c, c.Name, c.EncyclopediaLinkWithName);
            foreach (var k in Kingdom.All.Where(x => x.StringId.StartsWith(st.Prefix, StringComparison.Ordinal) && !x.IsEliminated)) check("kingdom", k, k.Name, k.EncyclopediaLinkWithName);
            foreach (var h in Hero.AllAliveHeroes.Where(x => x.IsLord && x.StringId.StartsWith(st.Prefix, StringComparison.Ordinal))) check("lord", h, h.Name, h.EncyclopediaLinkWithName);
            return new Dictionary<string, object> { ["checked"] = checkedCount, ["problems"] = problems.Take(30).ToList(), ["problemCount"] = problems.Count };
        }

        private static long LogLength(State st)
        {
            try { return File.Exists(st.LogPath) ? new FileInfo(st.LogPath).Length : -1; } catch (Exception) { return -1; }
        }

        private static readonly Regex MissingLine = new Regex(@"missing|not found|unable to find|could not find|cannot find|can't find|failed to load|couldn't load", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The engine log lines written during the case that report something missing: their kinds, and any naming a generated object or a scene
        // the case opened (a missing asset of this world). The engine's stock warnings (particles, sounds, shaders) are counted, not failed.
        private static Dictionary<string, object> LogWindow(State st, long from, Dictionary<string, object> row)
        {
            var record = new Dictionary<string, object> { ["path"] = "rgl_log_<pid>.txt" };
            if (from < 0 || !File.Exists(st.LogPath)) { record["read"] = false; return record; }
            string text;
            try
            {
                using (var stream = new FileStream(st.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(from, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
                }
            }
            catch (Exception e) { record["read"] = false; record["error"] = e.GetType().Name; return record; }
            var scenes = new List<string>();
            foreach (var kv in row) { var m = kv.Value as Dictionary<string, object>; if (m != null && m.ContainsKey("scene") && m["scene"] is string) scenes.Add((string)m["scene"]); }
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => MissingLine.IsMatch(l)).ToList();
            var stock = st.RuleList("stockMissingPatterns").Select(x => new Regex(x, RegexOptions.CultureInvariant)).ToList();
            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var world = new List<string>(); var other = new List<string>(); int worldCount = 0, otherCount = 0;
            foreach (var line in lines)
            {
                string body = Regex.Replace(line, @"^\[[^\]]*\]\s*", "");
                string kind = Regex.Replace(body, @"[\w./\\-]*\d[\w./\\-]*|""[^""]*""", "#");
                kind = kind.Length > 70 ? kind.Substring(0, 70) : kind;
                int n; kinds.TryGetValue(kind, out n); kinds[kind] = n + 1;
                // A line naming a generated object or a scene the case opened; else any line of a kind not known as stock (the scenario file's
                // stockMissingPatterns: shader variants compiled on first use, unvoiced dialogue lines).
                if (line.IndexOf(st.Prefix, StringComparison.Ordinal) >= 0 || scenes.Any(s => line.IndexOf(s, StringComparison.Ordinal) >= 0)) { worldCount++; if (world.Count < 20) world.Add(body); }
                else if (!stock.Any(r => r.IsMatch(body))) { otherCount++; if (other.Count < 20) other.Add(body); }
            }
            record["read"] = true; record["missingLines"] = lines.Count; record["kinds"] = kinds.OrderByDescending(k => k.Value).Take(15).ToDictionary(k => k.Key, k => (object)k.Value);
            record["worldMissing"] = world; record["worldMissingCount"] = worldCount; record["otherMissing"] = other; record["otherMissingCount"] = otherCount;
            return record;
        }
    }
}
