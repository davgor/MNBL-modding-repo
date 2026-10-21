using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Web.Script.Serialization;
using HarmonyLib;
using Helpers;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.CampaignRegistry;
using MNBL.ProceduralWorld.Diagnostics;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.Surface;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using C = MNBL.ProceduralWorld.Automation.Map2AcceptanceChecks;
using Path = System.IO.Path;
using Utilities = TaleWorlds.Engine.Utilities;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-247: the map epic 2 acceptance run in the game (map2_acceptance; phase=baseline|world1|world2|mods, repo=<checkout>, tag=<run id>;
    // tools/Run-Map2Acceptance.ps1 runs the phases, tools/summarize_map2_acceptance.py grades them; docs/map-epic-2-acceptance.md):
    //   baseline  the official War Sails sandbox (Calradia): frames at the fixed cameras, memory after one campaign day, the economy over
    //             baselineDays (RT-23, RT-42);
    //   world1    seed 42 through the New Campaign screen (the preview's wait estimate, the timed start and its cache build), then every check of
    //             the ticket's step 2 (biomes, relief, water, faces and speed, battles, a conversation, the seam, roads and ruins, looks,
    //             villages, names, the day-0 census, vanilla parity, frames and memory, magic ground, the seasons, the fire cast at Nerdun),
    //             a save and its Continue, the soak (soakDays) and its census;
    //   world2    seed 73 through the same screen (Town count, its census, its cache build), then world 1 continued from its save (scene and
    //             caches kept, reused);
    //   mods      with the synthetic content module enabled (tools/Build-AcceptanceContentModule.ps1): the setup screen and world 1's load,
    //             each working or refusing naming the module before any cache build.
    // Every step is fail-soft: its row records done, error, skipped (skip=, only=) or blocked (with the reason), with what it measured;
    // the verdicts (pass / fail per step and criterion) are the summarizer's. A step that changes the campaign leaves it idle on the map.
    //   decallab  MMR-247 (D): a world 1 save loaded (fromSave=), else world 1 started (seed=) and saved (w1.saveLab), then w1.decalLab
    //             (Automation/Map2AcceptanceDecalLab.cs): labelled decal grids at a flat dry spot and a sea spot, screenshotted at three
    //             distances, and the fix's look at the acc5 places; then the hang repro (w1.hangRepro.war, w1.hangRepro.castle: MMR-247 H).
    internal static partial class Map2AcceptanceProbe
    {
        internal static IEnumerator Run(ProbeContext context) { return TwoWorldMatrixProbe.Nested(Body(context)); }

        internal sealed class Acc
        {
            public ProbeContext Context;
            public string Repo, Tag, Phase, Culture;
            public HashSet<string> Skip = new HashSet<string>(StringComparer.Ordinal), Only = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, object> Steps = new Dictionary<string, object>(StringComparer.Ordinal);
            public List<string> Order = new List<string>();
            public List<object> Shots = new List<object>();
            public LandMissionsProbe.State Land;
            public Live World;
            public Dictionary<string, List<C.FacePoint>> Faces;
            public Dictionary<string, Map2AcceptancePlan.Spot> Spots;
            public Func<double, double, string> BiomeAt;   // logical
            public Dictionary<string, Dictionary<string, object>> Table;   // terrain-materials.json biomes by core
            public TravelCells Cells;   // the bundle's travel cells (cells.bin v2), physical = (c + 0.5 + ColumnOffset, r + 0.5 + RowOffset) * Spacing at a centre
            public string Step;
            public double BudgetScale = 1;    // budgetScale=: every step budget times this (0: none)
        }

        // The running generated world: its identity, scene, bundle and frame.
        internal sealed class Live
        {
            public string Key, WorldId, Prefix, ContentId, Scene, Package, Bundle, Instance;
            public double OriginX, OriginY, Width, Height;
            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["key"] = Key, ["worldId"] = WorldId, ["prefix"] = Prefix, ["contentId"] = ContentId, ["scene"] = Scene, ["package"] = Package,
                    ["bundle"] = Bundle, ["instance"] = Instance, ["frame"] = new[] { OriginX, OriginY, Width, Height } };
            }
            public Vec2 Physical(double lx, double ly) { return new Vec2((float)(OriginX + lx), (float)(OriginY + ly)); }
        }

        private static IEnumerator Body(ProbeContext context)
        {
            var a = new Acc { Context = context, Repo = context.Str("repo", null), Tag = context.Str("tag", "acc"), Phase = context.Str("phase", null), Culture = context.Str("culture", "empire"),
                BudgetScale = context.Num("budgetScale", 1) };
            // skip= / only=: a list of step names (a trailing * matches a prefix), or one comma-separated string.
            foreach (var key in new[] { "skip", "only" })
            {
                var target = key == "skip" ? a.Skip : a.Only;
                foreach (var s in MapEpic2Probe.Strings(context.Args, key)) target.Add(s.Trim());
                var text = context.Args.ContainsKey(key) ? context.Args[key] as string : null;
                if (text != null) foreach (var s in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) target.Add(s.Trim());
            }
            context.Outputs["skip"] = a.Skip.ToList(); context.Outputs["only"] = a.Only.ToList();
            context.Outputs["schema"] = "mnbl.map2-acceptance-probe"; context.Outputs["version"] = 1; context.Outputs["phase"] = a.Phase; context.Outputs["tag"] = a.Tag;
            context.Outputs["steps"] = a.Steps; context.Outputs["order"] = a.Order; context.Outputs["shots"] = a.Shots;
            context.Outputs["build"] = C.Build();
            if (a.Repo == null || !File.Exists(Path.Combine(a.Repo, "tests/Runtime/LandMissionScenarios.json"))) throw new InvalidOperationException("repo=<the checkout that built this module> is required (tests/Runtime/LandMissionScenarios.json).");
            if (context.Bool("forceVsyncOff", false)) { Utilities.SetForceVsync(false); ((Dictionary<string, object>)context.Outputs["build"])["forceVsyncSetFalse"] = true; }
            var plan = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(a.Repo, "tests/Runtime/LandMissionScenarios.json")));
            // MMR-247 (results/MMR-247-H.md): with freshMapAfterMission=true each land case after a mission on the loaded map starts from a fresh
            // load of the reload save. Off by default since MMR-247 X (c32): the acc4/acc5 freezes were the map dressing's moved ruin markers
            // writing through stale occluder records, so the run shows the battles, castle and siege after missions without the reload.
            bool freshMap = context.Bool("freshMapAfterMission", false);
            a.Land = new LandMissionsProbe.State { Context = context, Rules = (Dictionary<string, object>)plan["rules"], SiegeSetup = LandMissionsProbe.State.SiegeSetupOf(plan), Tag = "acc", NoReload = true, FreshMapAfterMission = freshMap };
            context.Outputs["freshMapAfterMission"] = freshMap;
            a.Land.Tally = a.Land.Cultures;
            a.Land.LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs", "rgl_log_" + context.ProcessId + ".txt");
            context.Outputs["landCases"] = a.Land.Cases;
            context.Checkpoint();
            switch (a.Phase)
            {
                case "baseline": yield return Baseline(a); break;
                case "world1": yield return World1(a); break;
                case "world2": yield return World2(a); break;
                case "mods": yield return Mods(a); break;
                case "decallab": yield return DecalLabPhase(a); break;
                default: throw new ArgumentException("phase must be baseline, world1, world2, mods or decallab.");
            }
            if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
            context.Outputs["landFailures"] = a.Land.Failures;
            context.Outputs["completed"] = true;
            context.Checkpoint();
        }

        // ---------------------------------------------------------------- steps

        private static bool Wanted(Acc a, string name)
        {
            if (a.Skip.Contains(name) || a.Skip.Any(s => s.EndsWith("*", StringComparison.Ordinal) && name.StartsWith(s.TrimEnd('*'), StringComparison.Ordinal))) return false;
            return a.Only.Count == 0 || a.Only.Contains(name) || a.Only.Any(s => s.EndsWith("*", StringComparison.Ordinal) && name.StartsWith(s.TrimEnd('*'), StringComparison.Ordinal));
        }

        // One step: its row (status done / error / skipped / blocked / timeout, seconds), the stage traced at its start ("map2_acceptance:<step>")
        // and at its end ("map2_acceptance:<step>:end <status>": the progress log is flushed per line, so the orchestrator names a step the
        // game hung in: a start line with no end), a checkpoint after it. needsMap: the step runs on the campaign map; the map is made idle first
        // (an encounter cleared, menus left, an open map incident confirmed, time stopped), else the campaign is recovered (Recover: the last
        // good save reloaded), else the step is blocked. Past its budget (Map2AcceptancePlan.Budget) the step ends as timeout: its iterator is
        // disposed (the finally blocks end its mission, clear the encounter, put parties back, make peace) and the campaign recovered.
        private static IEnumerator Step(Acc a, string name, bool needsMap, Func<Dictionary<string, object>, IEnumerator> body)
        {
            var row = new Dictionary<string, object> { ["step"] = name };
            a.Steps[name] = row; a.Order.Add(name); a.Step = name;
            if (!Wanted(a, name)) { row["status"] = "skipped"; row["reason"] = "skip/only argument"; End(a, name, row); yield break; }
            if (needsMap)
            {
                if (Game.Current == null || Campaign.Current == null) { row["status"] = "blocked"; row["reason"] = "no campaign is running"; End(a, name, row); yield break; }
                yield return Idle(a, row);
                if (NotIdle() != null && a.Land.Save != null && !a.Land.NoReload) { var before = new Dictionary<string, object>(); row["recoverBefore"] = before; yield return Recover(a, before); }
                string notIdle = NotIdle();
                if (notIdle != null) { row["status"] = "blocked"; row["reason"] = "the campaign is not idle on the map: " + notIdle; End(a, name, row); yield break; }
            }
            a.Context.Stage("map2_acceptance:" + name);
            double start = a.Context.Seconds, budget = Map2AcceptancePlan.Budget(name) * a.BudgetScale;
            Exception error = null; bool timedOut = false;
            var steps = TwoWorldMatrixProbe.Nested(body(row));
            try
            {
                while (true)
                {
                    try { if (!steps.MoveNext()) break; }
                    catch (Exception e) { error = e; break; }
                    if (budget > 0 && a.Context.Seconds - start > budget) { timedOut = true; break; }
                    yield return steps.Current;
                }
            }
            finally { try { (steps as IDisposable)?.Dispose(); } catch (Exception e) { row["disposeError"] = e.GetType().Name + ": " + e.Message; } }
            row["seconds"] = Math.Round(a.Context.Seconds - start, 2);
            if (timedOut)
            {
                row["status"] = "timeout"; row["reason"] = "past its budget of " + budget + " s"; row["budgetSeconds"] = budget;
                a.Context.Stage("map2_acceptance:" + name + ":timeout after " + budget + " s");
                try { if (Campaign.Current != null) row["timeState"] = C.SafeTimeState(a.Context); } catch (Exception) { }
                var recover = new Dictionary<string, object>(); row["recover"] = recover;
                yield return Recover(a, recover);
            }
            else if (error != null)
            {
                row["status"] = "error"; row["error"] = error.ToString();
                a.Context.Stage("map2_acceptance:" + name + ":error " + error.GetType().Name + ": " + error.Message);
                try { if (Campaign.Current != null) row["timeState"] = C.SafeTimeState(a.Context); } catch (Exception) { }
            }
            else if (!row.ContainsKey("status")) row["status"] = "done";
            End(a, name, row);
        }

        private static void End(Acc a, string name, Dictionary<string, object> row)
        {
            a.Context.Stage("map2_acceptance:" + name + ":end " + (row.ContainsKey("status") ? row["status"] : "done"));
            a.Context.Checkpoint();
        }

        // Back to an idle map after a step that timed out or left the campaign unusable: an open mission ended, the encounter and its
        // conversation cleared, menus left, the player's settlement left, then the map made idle (Idle); if it is still not idle, the land
        // cases' save (the last good save of this attempt) is loaded again, as a land case's own reload does. Recorded in `r`.
        private static IEnumerator Recover(Acc a, Dictionary<string, object> r)
        {
            double start = a.Context.Seconds;
            a.Context.Stage("map2_acceptance:" + a.Step + ":recover");
            if (Mission.Current != null)
            {
                try { if (!Mission.Current.MissionEnded) { Mission.Current.EndMission(); r["missionEnded"] = true; } } catch (Exception e) { r["missionEndError"] = e.GetType().Name + ": " + e.Message; }
                while (Mission.Current != null && a.Context.Seconds - start < 60) yield return null;
            }
            if (Game.Current != null && Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState)
            {
                var cleared = new Dictionary<string, object>(); r["cleared"] = cleared;
                try { if (PlayerEncounter.Current != null || Campaign.Current.ConversationManager.IsConversationInProgress) TwoWorldMatrixProbe.ClearEncounter(cleared); } catch (Exception e) { r["encounterError"] = e.GetType().Name + ": " + e.Message; }
                try { TwoWorldMatrixProbe.ExitMenu(cleared); } catch (Exception e) { r["menuError"] = e.GetType().Name + ": " + e.Message; }
                try { var main = MobileParty.MainParty; if (main.CurrentSettlement != null && main.CurrentSettlement.SiegeEvent == null && PlayerEncounter.Current == null) { r["left"] = main.CurrentSettlement.StringId; LeaveSettlementAction.ApplyForParty(main); } }
                catch (Exception e) { r["leaveError"] = e.GetType().Name + ": " + e.Message; }
            }
            if (Game.Current != null && Campaign.Current != null) yield return Idle(a, r);
            r["notIdleBeforeReload"] = NotIdle();
            if (NotIdle() != null && a.Land.Save != null && !a.Land.NoReload && a.World != null)
            {
                var reload = new Dictionary<string, object>(); r["reload"] = reload;
                a.Context.Stage("map2_acceptance:" + a.Step + ":reload " + a.Land.Save);
                yield return LandMissionsProbe.LoadDesignated(a.Land, reload);
                a.Land.Reloads++;
                if (Equals(reload["loaded"], true)) { LandMissionsProbe.Setup(a.Land); Bind(a, a.World.Key); }
            }
            r["notIdleAfter"] = NotIdle(); r["seconds"] = Math.Round(a.Context.Seconds - start, 2);
        }

        private static void Blocked(Dictionary<string, object> row, string reason) { row["status"] = "blocked"; row["reason"] = reason; }

        // LandMissionsProbe's idle rule (a map incident open or due included), and no encyclopedia open over the map (it keeps the map state
        // disabled).
        private static string NotIdle() { return LandMissionsProbe.NotIdle() ?? (C.EncyclopediaOpen() ? "the encyclopedia is open" : null); }

        // Idle on the map: a conversation, the map conversation and an encounter ended in that order (TwoWorldMatrixProbe.ClearEncounter),
        // inventory or party screens closed, a map incident confirmed as a player confirms it (C.ResolveIncident), an open encyclopedia closed,
        // menus left, inquiries hidden, time stopped.
        private static IEnumerator Idle(Acc a, Dictionary<string, object> row)
        {
            var cleared = new Dictionary<string, object>();
            double start = a.Context.Seconds;
            while (a.Context.Seconds - start < 30)
            {
                if (InformationManager.IsAnyInquiryActive()) InformationManager.HideInquiry();
                if (Game.Current == null) break;
                var state = Game.Current.GameStateManager.ActiveState;
                if (Mission.Current != null)
                {
                    // A step that failed inside a mission left it open: ended as its case would end it.
                    try { if (!Mission.Current.MissionEnded) { Mission.Current.EndMission(); Count(cleared, "missionsEnded"); } } catch (Exception e) { cleared["missionEndError"] = e.GetType().Name + ": " + e.Message; }
                }
                else if (state is MapState)
                {
                    C.ResolveIncident(cleared);
                    string encyclopedia = C.CloseEncyclopediaIfOpen();
                    if (encyclopedia != null) cleared["encyclopedia"] = encyclopedia;
                    if (PlayerEncounter.Current != null || Campaign.Current.ConversationManager.IsConversationInProgress) TwoWorldMatrixProbe.ClearEncounter(cleared);
                    else TwoWorldMatrixProbe.ExitMenu(cleared);
                    if (NotIdle() == null) break;
                }
                else if (state is InventoryState) { try { InventoryScreenHelper.CloseScreen(true); } catch (Exception) { } }
                else if (state is PartyState) { try { PartyScreenHelper.CloseScreen(true, true); } catch (Exception) { } }
                yield return null;
            }
            if (Campaign.Current != null) RuntimeMissionProbe.Pause();
            if (cleared.Count > 0) row["clearedBefore"] = cleared;
        }

        private static void Count(Dictionary<string, object> row, string key) { row[key] = (row.ContainsKey(key) ? (int)row[key] : 0) + 1; }

        // A screenshot of the current view, recorded with the step, a label and what it shows.
        private static string Shot(Acc a, Dictionary<string, object> row, string label, Dictionary<string, object> what)
        {
            string safe = new string(label.Select(ch => Char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray());
            string path = Path.Combine(a.Context.Directory, "map2acc-" + a.Context.ProcessId + "-" + a.Tag + "-" + a.Phase + "-" + safe + ".bmp");
            Utilities.TakeScreenshot(path);
            var record = new Dictionary<string, object> { ["step"] = a.Step, ["label"] = label, ["path"] = path, ["campaignDay"] = Campaign.Current == null ? null : (object)Math.Round(CampaignTime.Now.ToDays, 3),
                ["hour"] = Campaign.Current == null ? null : (object)Math.Round(CampaignTime.Now.CurrentHourInDay, 2) };
            if (what != null) foreach (var kv in what) record[kv.Key] = kv.Value;
            a.Shots.Add(record);
            var shots = row.ContainsKey("shots") ? (List<object>)row["shots"] : (List<object>)(row["shots"] = new List<object>());
            shots.Add(label);
            return path;
        }

        // The free camera over a physical point, settled (at most `settle` seconds: the frame still, the scene loaded, no shader compiling), a
        // screenshot; the camera given back to the party after. If the free camera did not reach the point (within 2 units) and place is true,
        // the party is placed on valid land beside it and followed instead (recorded as mode "party"), then put back.
        // atShot (MMR-247, c29/c31/c33) adds to the shot's record just before it is taken (an overlay mesh layer's state in the view: MeshesAt).
        private static IEnumerator Look(Acc a, Dictionary<string, object> row, string label, Vec2 at, float distance, float bearing, Dictionary<string, object> what, bool place = true, double settle = 8,
            Action<Dictionary<string, object>> atShot = null)
        {
            var record = new Dictionary<string, object> { ["at"] = C.P(at), ["distance"] = distance, ["bearing"] = bearing };
            if (what != null) foreach (var kv in what) record[kv.Key] = kv.Value;
            C.AimFree(at.X, at.Y, distance, bearing);
            yield return C.Settle(a.Context, record, settle, 6);
            var camera = (Dictionary<string, object>)record["camera"];
            double[] target = camera.ContainsKey("target") ? (double[])camera["target"] : null;
            double off = target == null ? double.NaN : C.Distance(new Vec2((float)target[0], (float)target[1]), at);
            record["targetOff"] = C.F(off); record["mode"] = "free";
            CampaignVec2 back = default(CampaignVec2); bool placed = false;
            if (!(off < 2) && place)
            {
                var point = NavigationHelper.FindReachablePointAroundPosition(new CampaignVec2(at, true), MobileParty.NavigationType.Default, 4f, 0f, false);
                if (point.IsValid())
                {
                    back = C.Place(point.ToVec2()); placed = true; record["mode"] = "party"; record["partyAt"] = C.P(point.ToVec2());
                    C.FollowParty(distance);
                    yield return C.Settle(a.Context, record, settle, 6);
                }
            }
            if (atShot != null) { try { atShot(record); } catch (Exception e) { record["atShotError"] = e.GetType().Name + ": " + e.Message; } }
            Shot(a, row, label, record);
            yield return C.Frames(4);
            if (placed) MobileParty.MainParty.Position = back;
            C.FollowParty(30);
        }

        // ---------------------------------------------------------------- baseline

        private static IEnumerator Baseline(Acc a)
        {
            var context = a.Context;
            yield return Step(a, "base.start", false, row => BaselineStart(a, row));
            yield return Step(a, "base.frames", true, row => Frames(a, row));
            yield return Step(a, "base.memoryDay", true, row => MemoryDay(a, row, null));
            yield return Step(a, "base.soak", true, row => BaselineSoak(a, row));
            yield return TwoWorldMatrixProbe.EndGame(context);
        }

        // The official sandbox (Calradia with War Sails) through the diagnostic bootstrap, as baseline_scenario starts it: official content, this
        // module loaded (it hosts the probe), no generated terrain or records.
        private static IEnumerator BaselineStart(Acc a, Dictionary<string, object> row)
        {
            var context = a.Context; double start = context.Seconds;
            var boot = DiagnosticSandboxBootstrap.Start(true, new SandBox.MapSceneCreator(), a.Culture, "MNBL Acceptance Baseline", context.Stage);
            while (!boot.Completed)
            {
                boot.Tick();
                if (boot.Failed) throw new InvalidOperationException(boot.Error);
                if (context.Seconds - start > 600) throw new TimeoutException("The official sandbox did not reach its map.");
                yield return null;
            }
            row["bootSeconds"] = context.Seconds - start;
            var scene = C.MapScene();
            row["scene"] = scene == null ? null : scene.GetName(); row["sceneModule"] = scene == null ? null : scene.GetModulePath();
            start = context.Seconds;
            while (scene != null && (!scene.IsLoadingFinished() || Utilities.GetNumberOfShaderCompilationsInProgress() > 0) && context.Seconds - start < 180) yield return null;
            InformationManager.HideInquiry();
            LandMissionsProbe.Setup(a.Land);
            yield return C.Frames(60);
            row["vanillaRun"] = false; row["scope"] = "official content and the official map; this module is loaded (it hosts the probe) and the bootstrap is the diagnostic one";
            row["modules"] = Utilities.GetModulesNames();
            row["economy"] = C.Economy(null);
            row["memory"] = C.Memory();
        }

        private static IEnumerator BaselineSoak(Acc a, Dictionary<string, object> row)
        {
            double days = a.Context.Num("baselineDays", 30);
            row["economyBefore"] = C.Economy(null);
            var advance = new Dictionary<string, object>(); row["advance"] = advance;
            yield return C.AdvanceDays(a.Context, advance, days, null, null);
            row["economyAfter"] = C.Economy(null);
            row["memoryAfter"] = C.Memory();
        }

        // The fixed cameras (the same in every world): following the party, time stopped, at distance 30 and at 100.
        private static IEnumerator Frames(Acc a, Dictionary<string, object> row)
        {
            int frames = a.Context.Int("frames", 600);
            foreach (var view in new[] { new KeyValuePair<string, float>("near", 30f), new KeyValuePair<string, float>("far", 100f) })
            {
                var r = new Dictionary<string, object> { ["distance"] = view.Value }; row[view.Key] = r;
                C.FollowParty(view.Value);
                var cameraView = C.CameraView(); if (cameraView != null) C.SetDistance(cameraView, view.Value);
                yield return C.Settle(a.Context, r, 10, 30);
                yield return C.MeasureFrames(a.Context, r, frames);
                Shot(a, row, "frames-" + view.Key, new Dictionary<string, object> { ["distance"] = view.Value });
                yield return C.Frames(4);
            }
            C.FollowParty(30);
            row["vsync"] = a.Context.Bool("forceVsyncOff", false) ? "SetForceVsync(false) called" : "not changed";
        }

        // Memory before and after one campaign day in fast forward (the player sheltered).
        private static IEnumerator MemoryDay(Acc a, Dictionary<string, object> row, string prefix)
        {
            row["before"] = C.Memory();
            var advance = new Dictionary<string, object>(); row["advance"] = advance;
            yield return C.AdvanceDays(a.Context, advance, 1, null, null);
            yield return C.Frames(30);
            GC.Collect(); GC.WaitForPendingFinalizers();
            row["after"] = C.Memory();
        }

        // ---------------------------------------------------------------- world 1

        private static IEnumerator World1(Acc a)
        {
            var context = a.Context;
            string fromSave = context.Str("fromSave", null);
            if (fromSave == null)
            {
                yield return Step(a, "w1.start", false, row => StartWorld(a, row, "A", (uint)context.Int("seed", 42)));
            }
            else
            {
                yield return Step(a, "w1.load", false, row => LoadSave(a, row, "A", fromSave, false));
                // A resumed attempt's land cases reload the day-0 save it loaded (w1.save0 is not run again).
                if (a.World != null) { a.Land.Save = fromSave; a.Land.NoReload = false; }
            }
            if (a.World == null) { context.Outputs["stopped"] = "world 1 did not start or load"; yield break; }
            // acc4 triage hang-2: the core first (no mission, no encounter), each step in Map2AcceptancePlan's order; then the risky tail (the
            // map conversation, the field battles, the settlement cases, the siege last), each fail-soft with its own budget, so one hang in a
            // mission can no longer lose the core. The tail starts from the soak's save (SaveStep makes it the land cases' reload save).
            var steps = new Dictionary<string, KeyValuePair<bool, Func<Dictionary<string, object>, IEnumerator>>>(StringComparer.Ordinal);
            Action<string, bool, Func<Dictionary<string, object>, IEnumerator>> add = (n, map, body) => steps[n] = new KeyValuePair<bool, Func<Dictionary<string, object>, IEnumerator>>(map, body);
            add("w1.identity", true, row => Identity(a, row));
            add("w1.census0", true, row => CensusStep(a, row));
            add("w1.save0", true, row => SaveStep(a, row, "w1_day0"));
            add("w1.frames", true, row => Frames(a, row));
            add("w1.memoryDay", true, row => MemoryDay(a, row, a.World.Prefix));
            add("w1.faces", true, row => FacesStep(a, row));
            add("w1.biomes", true, row => Biomes(a, row));
            add("w1.relief", true, row => Relief(a, row));
            add("w1.water", true, row => Water(a, row));
            add("w1.dressing", true, row => Dressing(a, row));
            add("w1.magic", true, row => MagicGround(a, row));
            add("w1.magicWater", true, row => MagicWaterStep(a, row));
            add("w1.looks", true, row => Looks(a, row));
            add("w1.speed", true, row => SpeedStep(a, row));
            add("w1.seam", true, row => Seam(a, row));
            add("w1.incidents", true, row => Incidents(a, row));
            add("w1.ruinsPage", true, row => RuinsPage(a, row));
            add("w1.season", true, row => Season(a, row));
            add("w1.cast", true, row => Cast(a, row));
            add("w1.saveCast", true, row => SaveStep(a, row, "w1_cast"));
            add("w1.continue", false, row => ContinueStep(a, row));
            add("w1.soak", true, row => Soak(a, row));
            add("w1.saveSoak", true, row => SaveStep(a, row, "w1_soak"));
            add("w1.conversation", true, row => Conversation(a, row));
            add("w1.battle.snow", true, row => FaceBattle(a, row, "Snow"));
            add("w1.battle.forest", true, row => FaceBattle(a, row, "Forest"));
            add("w1.battle.settlement", true, row => SettlementBattle(a, row));
            add("w1.battle.mutated", true, row => MutatedBattle(a, row));
            add("w1.town", true, row => LandCase(a, row, "town", r => LandMissionsProbe.Town(a.Land, r)));
            add("w1.castle", true, row => LandCase(a, row, "castle", r => LandMissionsProbe.Castle(a.Land, r)));
            add("w1.village", true, row => LandCase(a, row, "village", r => LandMissionsProbe.Village(a.Land, r)));
            add("w1.siege", true, row => Siege(a, row));
            foreach (var name in Map2AcceptancePlan.World1Core.Concat(Map2AcceptancePlan.World1Tail))
                yield return Step(a, name, steps[name].Key, steps[name].Value);
            yield return TwoWorldMatrixProbe.EndGame(context);
        }

        // ---------------------------------------------------------------- world 2

        private static IEnumerator World2(Acc a)
        {
            var context = a.Context;
            string continueSave = context.Str("continueSave", null);
            // World 1's scene and caches as they are before world 2 is generated (the check after is in w2.continue1).
            yield return Step(a, "w2.world1Before", false, row => WorldFiles(a, row, continueSave));
            yield return Step(a, "w2.start", false, row => StartWorld(a, row, "B", (uint)context.Int("seed", 73)));
            if (a.World != null)
            {
                yield return Step(a, "w2.identity", true, row => Identity(a, row));
                yield return Step(a, "w2.census0", true, row => CensusStep(a, row));
                yield return Step(a, "w2.water", true, row => Water(a, row));
                yield return Step(a, "w2.save0", true, row => SaveStep(a, row, "w2_day0"));
                yield return TwoWorldMatrixProbe.EndGame(context);
            }
            yield return Step(a, "w2.world1After", false, row => WorldFiles(a, row, continueSave));
            a.World = null;
            yield return Step(a, "w2.continue1", false, row => continueSave == null ? Blocked1(row, "continueSave=<world 1's save> was not given") : LoadSave(a, row, "A", continueSave, true));
            if (a.World != null) yield return Step(a, "w2.after1", true, row => AfterContinue(a, row));
            yield return TwoWorldMatrixProbe.EndGame(context);
        }

        private static IEnumerator Blocked1(Dictionary<string, object> row, string reason) { Blocked(row, reason); yield break; }

        // A saved world's installed scene folder, its archived copy and its published caches (the forecast the loader would make).
        private static IEnumerator WorldFiles(Acc a, Dictionary<string, object> row, string save)
        {
            if (save == null) { Blocked(row, "continueSave=<world 1's save> was not given"); yield break; }
            var info = MBSaveLoad.GetSaveFileWithName(save);
            if (info == null) { Blocked(row, "no save " + save); yield break; }
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (info.MetaData != null) foreach (var k in info.MetaData.Keys) metadata[k] = info.MetaData[k];
            var binding = WorldBinding.FromHeader(metadata);
            row["binding"] = new Dictionary<string, object> { ["worldId"] = binding.WorldId, ["scene"] = binding.Scene, ["contentId"] = binding.ContentId, ["package"] = binding.PackageName };
            string module = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(SubModule).Assembly.Location)));
            string folder = binding.Scene == null ? null : Path.Combine(module, "SceneObj", binding.Scene);
            row["sceneFolder"] = folder; row["sceneInstalled"] = folder != null && Directory.Exists(folder);
            row["sceneTree"] = folder != null && Directory.Exists(folder) ? TwoWorldMatrixProbe.TreeHash(folder) : "absent";
            string archive = binding.Scene == null ? null : Path.Combine(SubModule.Store, "world-scenes", binding.Scene);
            row["archived"] = archive != null && Directory.Exists(archive);
            row["forecast"] = binding.Scene == null ? null : C.Forecast(binding.Scene, false);
            row["package"] = Directory.Exists(Path.Combine(GeneratedWorldPackage.Root, binding.PackageName));
            yield break;
        }

        // ---------------------------------------------------------------- mods

        private static IEnumerator Mods(Acc a)
        {
            var context = a.Context;
            string module = context.Str("module", "MNBL.AcceptanceContent"), continueSave = context.Str("continueSave", null);
            yield return Step(a, "mods.modules", false, row => ModsModules(a, row, module));
            yield return Step(a, "mods.setup", false, row => ModsSetup(a, row, module));
            yield return Step(a, "mods.load", false, row => continueSave == null ? Blocked1(row, "continueSave=<world 1's save> was not given") : ModsLoad(a, row, continueSave, module));
            if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
        }

        private static IEnumerator ModsModules(Acc a, Dictionary<string, object> row, string module)
        {
            var names = Utilities.GetModulesNames();
            row["modules"] = names; row["enabled"] = names.Contains(module);
            var info = TaleWorlds.ModuleManager.ModuleHelper.GetModuleInfo(module);
            row["info"] = info == null ? null : new Dictionary<string, object> { ["id"] = info.Id, ["folder"] = info.FolderPath, ["version"] = info.Version.ToString() };
            if (!names.Contains(module)) Blocked(row, "the synthetic module " + module + " is not enabled in this game");
            yield break;
        }

        // The setup screen with the module enabled: its host problems (the content check names the module), and, when it lets the player
        // generate, the seed's generation to review (then Exit; nothing is started). Timed out waits are recorded, never left hanging.
        private static IEnumerator ModsSetup(Acc a, Dictionary<string, object> row, string module)
        {
            var context = a.Context;
            TaleWorlds.MountAndBlade.Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 60) yield return null;
            row["openSeconds"] = context.Seconds - start;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            yield return C.Frames(20);
            var vm = WorldSetupScreen.Current.ViewModel; var model = vm.Model;
            row["host"] = SetupTexts(vm);
            if (vm.HasUnfinished && vm.CanDiscard) { row["discardedUnfinished"] = vm.UnfinishedText; vm.ExecuteDiscard(); yield return C.Frames(10); row["host"] = SetupTexts(vm); }
            Shot(a, row, "mods-setup", null);
            if (vm.HasHostProblems || !vm.CanGenerate) { row["outcome"] = "refused at setup"; row["namesModule"] = (vm.HostProblemText ?? "").Contains(module) || (vm.HostProblemTitle ?? "").Contains(module) || AnyHostView(vm, module); }
            else
            {
                vm.SeedText = a.Context.Int("seed", 42).ToString(CultureInfo.InvariantCulture); yield return null;
                row["confirm"] = WorldSetupScreen.Current.HandleInput(k => k == SetupInput.Confirm, false).ToString();
                start = context.Seconds;
                while (model.State == SetupState.Generating && context.Seconds - start < 1500) yield return null;
                row["generateSeconds"] = context.Seconds - start; row["state"] = model.State.ToString(); row["timedOut"] = model.State == SetupState.Generating;
                row["error"] = model.Error == null ? null : new Dictionary<string, object> { ["category"] = model.Error.Category.ToString(), ["code"] = model.Error.Code, ["message"] = model.Error.Message, ["detail"] = model.Error.Detail,
                    ["variables"] = model.Error.Variables == null ? null : model.Error.Variables.ToDictionary(k => k.Key, k => (object)k.Value) };
                row["after"] = SetupTexts(vm);
                row["outcome"] = model.State == SetupState.Generated ? "generated" : model.Error != null ? "refused at generation" : model.State.ToString();
                row["namesModule"] = model.Error != null && ((model.Error.Variables != null && model.Error.Variables.Values.Any(v => v != null && v.Contains(module))) || (model.Error.Detail ?? "").Contains(module) || (vm.ErrorText ?? "").Contains(module));
                yield return C.Frames(20);
                Shot(a, row, "mods-setup-after", null);
                if (model.State == SetupState.Generating) { WorldSetupScreen.Current.HandleInput(k => k == SetupInput.Exit, false); start = context.Seconds; while (model.State == SetupState.Generating && context.Seconds - start < 180) yield return null; }
            }
            for (int i = 0; i < 4 && WorldSetupScreen.Current != null; i++) { WorldSetupScreen.Current.ViewModel.ExecuteBack(); yield return C.Frames(20); }
            row["closed"] = WorldSetupScreen.Current == null;
        }

        private static bool AnyHostView(WorldSetupViewModel vm, string module)
        {
            try { return vm.HostProblemViews != null && vm.HostProblemViews.Any(v => (v.ToString() ?? "").Contains(module) || Texts(v).Any(t => t.Contains(module))); }
            catch (Exception) { return false; }
        }

        // Every string property of a view model (its rendered texts).
        private static IEnumerable<string> Texts(object vm)
        {
            foreach (var p in vm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0))
            {
                string v = null; try { v = p.GetValue(vm) as string; } catch (Exception) { }
                if (v != null) yield return v;
            }
        }

        private static Dictionary<string, object> SetupTexts(WorldSetupViewModel vm)
        {
            return new Dictionary<string, object> { ["hasHostProblems"] = vm.HasHostProblems, ["hostProblemTitle"] = vm.HostProblemTitle, ["hostProblemText"] = vm.HostProblemText,
                ["hostProblems"] = vm.HostProblemViews == null ? null : vm.HostProblemViews.Select(v => (object)String.Join(" | ", Texts(v))).ToList(), ["canGenerate"] = vm.CanGenerate,
                ["hasError"] = vm.HasError, ["errorTitle"] = vm.ErrorTitle, ["errorText"] = vm.ErrorText, ["notice"] = vm.NoticeText, ["status"] = vm.StatusText, ["waitText"] = vm.WaitText };
        }

        // The inquiries the game shows while a load starts, heard on InformationManager's public OnShowInquiry event (ShowInquiry only invokes
        // it, so a caller that inlined ShowInquiry still raises it): the installed load-with-other-modules question of
        // SandBoxSaveHelper.TryLoadSave is answered Yes, as the player who enabled the mod answers it.
        private static class Inquiries
        {
            public static readonly List<InquiryData> Shown = new List<InquiryData>();
            private static readonly Action<InquiryData, bool, bool> Handler = (data, pause, prioritize) => { if (data != null && Shown.Count < 20) Shown.Add(data); };
            public static void Install() { Shown.Clear(); InformationManager.OnShowInquiry -= Handler; InformationManager.OnShowInquiry += Handler; }
            public static void Remove() { InformationManager.OnShowInquiry -= Handler; }
        }

        // SandBoxSaveHelper's own inquiry flag: set while its module or version question is open, cleared by either answer.
        private static readonly FieldInfo SaveHelperInquiryActive = AccessTools.Field(typeof(SandBox.SandBoxSaveHelper), "_isInquiryActive");
        private static bool SaveHelperAsking() { try { return SaveHelperInquiryActive != null && (bool)SaveHelperInquiryActive.GetValue(null); } catch (Exception) { return false; } }

        // World 1's save loaded with the module enabled: loaded, or refused naming the module, and when (before any cache build: the loader's
        // phases and whether its map acquired caches).
        private static IEnumerator ModsLoad(Acc a, Dictionary<string, object> row, string save, string module)
        {
            Inquiries.Install();
            try { yield return LoadSave(a, row, "A", save, false, true); }
            finally { Inquiries.Remove(); }
            var loader = row.ContainsKey("loader") ? row["loader"] as Dictionary<string, object> : null;
            object refusal = loader == null ? null : loader.ContainsKey("refusal") ? loader["refusal"] : null;
            row["outcome"] = a.World != null ? "loaded" : refusal != null ? "refused" : "failed";
            row["refusalText"] = row.ContainsKey("refusalText") ? row["refusalText"] : null;
            row["namesModule"] = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 }.Serialize(new object[] { refusal, row.ContainsKey("refusalText") ? row["refusalText"] : null }).Contains(module);
            if (a.World != null) { Shot(a, row, "mods-loaded-map", null); }
        }

        // ---------------------------------------------------------------- starting and loading a generated world

        // Seed typed and confirmed through the screen's mapping; the wait text and the preview (its counts text carries the per-world cache
        // build estimate, MMR-282) recorded; Start Campaign; character creation driven as the player drives it; the map up with time stopped.
        // The cache build is the map's acquired navigation caches (generate times) and the whole start is timed.
        private static IEnumerator StartWorld(Acc a, Dictionary<string, object> row, string key, uint seed)
        {
            var context = a.Context;
            TaleWorlds.MountAndBlade.Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 60) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            yield return C.Frames(10);
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            row["seed"] = seed; row["setup"] = SetupTexts(vm);
            // An unfinished generation an earlier attempt of the run left (a crash mid-job, the mods phase's reviewed world) is discarded
            // through the screen's own Discard, as a player starting afresh does.
            if (vm.HasUnfinished && vm.CanDiscard) { row["discardedUnfinished"] = vm.UnfinishedText; vm.ExecuteDiscard(); yield return C.Frames(10); }
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            row["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            start = context.Seconds;
            yield return null;
            while (model.State == SetupState.Generating && context.Seconds - start < 1500) yield return null;
            row["generateSeconds"] = context.Seconds - start; row["state"] = model.State.ToString();
            if (model.State != SetupState.Generated) { row["error"] = model.Error == null ? null : model.Error.ToString(); throw new InvalidOperationException("seed " + seed + " was not generated: " + model.State + " " + model.Error); }
            var preview = model.Preview;
            var counts = preview == null ? null : preview.Markers.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
            int towns = counts == null || !counts.ContainsKey("town") ? 0 : counts["town"], castles = counts == null || !counts.ContainsKey("castle") ? 0 : counts["castle"], villages = counts == null || !counts.ContainsKey("village") ? 0 : counts["village"];
            int settlements = Surface.CacheBuildModel.Settlements(towns, castles, villages);
            var cacheModel = model.Contract.CacheBuild;
            row["preview"] = new Dictionary<string, object> { ["countsText"] = vm.Preview.CountsText, ["waitText"] = vm.WaitText, ["markers"] = counts == null ? null : counts.ToDictionary(k => k.Key, k => (object)k.Value),
                ["settlements"] = settlements, ["estimateSeconds"] = cacheModel == null ? null : (object)cacheModel.Seconds(settlements), ["estimateMinutes"] = cacheModel == null ? null : (object)Surface.CacheBuildModel.Minutes(cacheModel.Seconds(settlements)),
                ["model"] = cacheModel == null ? null : cacheModel.Report(), ["worldContentId"] = preview == null ? null : preview.WorldContentId, ["scene"] = preview == null ? null : preview.SceneName };
            yield return C.Frames(30);
            Shot(a, row, key + "-preview", null);
            vm.ExecuteStartCampaign();
            context.Stage("map2_acceptance:" + key + ":start");
            var creation = new NewCampaignFlowProbe.CreationDriver(a.Culture, "MNBL Acceptance " + key);
            start = context.Seconds; double mapAt = -1, longest = 0, last = context.Seconds;
            while (model.State == SetupState.Starting && context.Seconds - start < 4800)
            {
                longest = Math.Max(longest, context.Seconds - last); last = context.Seconds;
                if (Game.Current != null)
                {
                    string step = creation.StepToMap();
                    if (step != null) context.Stage("map2_acceptance:" + key + ":" + step);
                    if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState) { Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop; if (mapAt < 0) mapAt = context.Seconds - start; }
                }
                yield return null;
            }
            row["startSeconds"] = context.Seconds - start; row["mapAfterSeconds"] = mapAt; row["longestFrameSeconds"] = longest; row["startState"] = model.State.ToString(); row["creation"] = creation.Record();
            if (model.State != SetupState.Started || Campaign.Current == null) throw new InvalidOperationException("world " + key + " did not start: " + model.State + (model.Error == null ? "" : " (" + model.Error + ")"));
            while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 4900) yield return null;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            InformationManager.HideInquiry();
            yield return C.Frames(30);
            Bind(a, key);
            row["world"] = a.World.Record();
            var support = C.Support("generatedNavigationCaches");
            row["cacheBuild"] = C.CacheRecord(support);
            row["forecastAfter"] = C.Forecast(a.World.Scene, false);
            LandMissionsProbe.Setup(a.Land);
            Shot(a, row, key + "-first-map", null);
        }

        // The running world's identity (its save binding), scene, bundle and frame; the land-missions state's prefix.
        private static void Bind(Acc a, string key)
        {
            var binding = WorldSaveBinding.BindingOf(Campaign.Current.GetCampaignBehavior<WorldIdentityBehavior>());
            var map = Campaign.Current.MapSceneWrapper as GeneratedMapScene;
            var w = new Live { Key = key, WorldId = binding.WorldId, Prefix = binding.WorldId + "_", ContentId = binding.ContentId, Instance = binding.Instance, Package = binding.PackageName,
                Scene = map == null ? binding.Scene : map.SceneName, Bundle = Path.Combine(CompileStage.BundleRoot(), binding.ContentId) };
            var periodic = PeriodicWorld.Current;
            if (periodic != null) { var c = periodic.Coordinates; w.OriginX = c.OriginX; w.OriginY = c.OriginY; w.Width = c.Width; w.Height = c.Height; }
            a.World = w;
            a.Land.Prefix = w.Prefix; a.Land.WorldId = w.WorldId;
            a.Faces = null; a.Spots = null; a.BiomeAt = null; a.Cells = null;
        }

        // A save loaded through the normal entries: the main menu's Continue (the preload state for the save) or the load screen's
        // (SandBoxSaveHelper.TryLoadSave). The loader's report kept (cache forecast, generator processes, scene source, phases, refusal).
        private static IEnumerator LoadSave(Acc a, Dictionary<string, object> row, string key, string save, bool viaContinue, bool answerInquiries = false)
        {
            var context = a.Context;
            if (Game.Current != null) yield return TwoWorldMatrixProbe.EndGame(context);
            var info = MBSaveLoad.GetSaveFileWithName(save);
            row["save"] = save;
            if (info == null) { Blocked(row, "no save " + save); yield break; }
            var previous = GeneratedWorldLoader.Current; bool vanilla = false;
            double start = context.Seconds;
            if (viaContinue)
            {
                row["entry"] = "Continue (preload state)";
                var preload = GameStateManager.Current.CreateState<Sandbox.View.GameStates.PreloadState>(new object[] { save });
                GameStateManager.Current.PushState(preload, 0);
                while (GeneratedWorldLoader.Current == previous && context.Seconds - start < 60) yield return null;
            }
            else
            {
                row["entry"] = "load screen (SandBoxSaveHelper.TryLoadSave)";
                Action<TaleWorlds.SaveSystem.Load.LoadResult> onStart = r => vanilla = true; Action onCancel = () => { };
                SandBox.SandBoxSaveHelper.TryLoadSave(info, onStart, onCancel);
                // The installed checks may ask first (other modules than the save's): answered Yes when asked to, recorded either way. When
                // the helper's question is open and was not heard (5 s), its Yes is taken directly: the helper's LoadGameAction, as its Yes calls it.
                var asked = new List<object>(); row["inquiries"] = asked; int answered = 0; bool direct = false;
                while (GeneratedWorldLoader.Current == previous && context.Seconds - start < 60)
                {
                    if (answerInquiries && Inquiries.Shown.Count > answered)
                    {
                        var q = Inquiries.Shown[answered++];
                        asked.Add(new Dictionary<string, object> { ["title"] = q.TitleText, ["text"] = q.Text, ["affirmative"] = q.AffirmativeText, ["answered"] = "affirmative", ["heard"] = "OnShowInquiry" });
                        InformationManager.HideInquiry();
                        try { if (q.AffirmativeAction != null) q.AffirmativeAction(); } catch (Exception e) { asked.Add("the affirmative action threw " + e.GetType().Name + ": " + e.Message); }
                    }
                    else if (answerInquiries && !direct && answered == 0 && context.Seconds - start > 5 && SaveHelperAsking())
                    {
                        direct = true;
                        asked.Add(new Dictionary<string, object> { ["title"] = null, ["text"] = "SandBoxSaveHelper's question open and not heard", ["answered"] = "LoadGameAction called directly" });
                        InformationManager.HideInquiry();
                        try { SaveHelperInquiryActive.SetValue(null, false); AccessTools.Method(typeof(SandBox.SandBoxSaveHelper), "LoadGameAction").Invoke(null, new object[] { info, onStart, onCancel }); }
                        catch (Exception e) { var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; asked.Add("LoadGameAction threw " + inner.GetType().Name + ": " + inner.Message); }
                    }
                    else if (!answerInquiries && InformationManager.IsAnyInquiryActive()) { asked.Add("an inquiry is open and not answered"); break; }
                    yield return null;
                }
            }
            var loader = GeneratedWorldLoader.Current == previous ? null : GeneratedWorldLoader.Current;
            while (loader != null && !loader.Done && context.Seconds - start < 4800) yield return null;
            row["seconds"] = context.Seconds - start; row["vanillaStarted"] = vanilla; row["timedOut"] = loader != null && !loader.Done;
            row["loader"] = loader == null ? null : loader.Report;
            row["loaderState"] = loader == null ? null : loader.State.ToString();
            if (loader != null && loader.Refusal != null) row["refusalText"] = new Dictionary<string, object> { ["code"] = loader.Refusal.Code, ["title"] = loader.Refusal.Title, ["message"] = loader.Refusal.Message, ["detail"] = loader.Refusal.Detail,
                ["text"] = loader.Refusal.Text, ["variables"] = loader.Refusal.Variables == null ? null : loader.Refusal.Variables.ToDictionary(k => k.Key, k => (object)k.Value) };
            if (loader == null || loader.State != GeneratedWorldLoader.Phase.Loaded || vanilla)
            {
                if (loader == null) row["error"] = "the save was not routed to the world loader";
                yield return C.Frames(60);
                Shot(a, row, key + "-load-outcome", null);
                InformationManager.HideInquiry();
                yield break;
            }
            while (!(Game.Current.GameStateManager.ActiveState is MapState) && context.Seconds - start < 4900) yield return null;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            InformationManager.HideInquiry();
            yield return C.Frames(30);
            Bind(a, key);
            row["world"] = a.World.Record();
            row["cacheAcquired"] = C.CacheRecord(C.Support("generatedNavigationCaches"));
            row["generatorStarted"] = loader.Report.ContainsKey("generatorStarted") ? loader.Report["generatorStarted"] : null;
            LandMissionsProbe.Setup(a.Land);
            Shot(a, row, key + "-loaded-map", null);
        }

        // ---------------------------------------------------------------- identity and audit

        // What the build that runs does on this world (no game state changed): the patch signatures (MMR-284), the speed model chain (MMR-254),
        // the incident conditions' transpiler (MMR-287), the wanderer tick and the minor-faction marking (MMR-281), the map's records: climate
        // visuals and snow texture (MMR-256), map dressing (MMR-257/285), magic ground (MMR-260), the battle map, map trees (MMR-255), the
        // console commands of the live mutation (MMR-261).
        private static IEnumerator Identity(Acc a, Dictionary<string, object> row)
        {
            row["world"] = a.World.Record();
            row["support"] = C.Support("climateVisuals", "mapDressing", "magicGround", "battleSceneSelection", "afterLoadFailure", "generatedNavigationCaches", "climate", "water");
            var audit = new Dictionary<string, object>(); row["audit"] = audit;
            Try(audit, "patchSignatureFailures", () => GeneratedPatches.Verify().Select(f => (object)f.Message).ToList());
            Try(audit, "installedVersions", () => GeneratedPatches.InstalledVersions());
            Try(audit, "speedModelChain", () =>
            {
                var chain = new List<object>();
                for (object m = Campaign.Current.Models.PartySpeedCalculatingModel; m != null && chain.Count < 8;)
                {
                    chain.Add(m.GetType().FullName);
                    var b = m.GetType().GetProperty("BaseModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    m = b == null ? null : b.GetValue(m);
                }
                return chain;
            });
            Try(audit, "roadCells", () => GeneratedPartySpeedModel.Active == null ? 0 : GeneratedPartySpeedModel.Active.RoadCells);
            Try(audit, "incidentConditions", () =>
            {
                var result = new Dictionary<string, object>();
                foreach (var r in IncidentCulturePatch.Resolve(AccessTools.TypeByName(IncidentCulturePatch.BehaviourType), IncidentCulturePatch.Instructions))
                {
                    var info = r.Condition == null ? null : Harmony.GetPatchInfo(r.Condition);
                    bool patched = r.Resolved && info != null && info.Transpilers.Any(p => p.owner == GeneratedPatches.WorldId) &&
                                   PatchProcessor.GetCurrentInstructions(r.Condition).Count(i => i.Calls(IncidentCulturePatch.FactionCultureIdMethod)) == r.Incident.Literals.Length;
                    result[r.Incident.Id] = r.Resolved ? (patched ? "culture comparison" : "resolved, not patched") : "off: " + r.Problem;
                }
                return result;
            });
            Try(audit, "wandererTick", () =>
            {
                var tick = CompanionsTickPatch.Current;
                var info = tick == null ? null : Harmony.GetPatchInfo(tick.DailyTick);
                return tick == null ? "not patched" : tick.DailyTick.Name + (info != null && info.Prefixes.Any(p => p.owner == GeneratedPatches.WorldId) ? " prefixed" : " resolved, not prefixed");
            });
            Try(audit, "minorFactionMarks", () =>
            {
                var boot = Game.Current.GameManager as GeneratedGameManager;
                var population = boot == null ? null : boot.Population;
                return population == null ? null : new Dictionary<string, object> { ["markedAt"] = population.MinorFactionMarkedAt,
                    ["missing"] = population.MinorFactionMarks == null ? null : population.MinorFactionMarks.Missing.Cast<object>().ToList() };
            });
            Try(audit, "storylineBehavior", () => Campaign.Current.GetCampaignBehaviors<CampaignBehaviorBase>().Any(b => b.GetType().FullName.Contains("NavalStoryline")) ? "present" : "absent");
            Try(audit, "flora", () => { var s = C.MapScene(); return new Dictionary<string, object> { ["instances"] = s.GetFloraInstanceCount(), ["rendererTextureUsage"] = s.GetFloraRendererTextureUsage() }; });
            Try(audit, "atmosphere", () => new Dictionary<string, object> { ["active"] = GeneratedMapAtmosphere.Active, ["ticks"] = GeneratedMapAtmosphere.Ticks, ["lastSeason"] = C.F(GeneratedMapAtmosphere.LastSeason),
                ["lastThreshold"] = C.F(GeneratedMapAtmosphere.LastThreshold), ["lastError"] = GeneratedMapAtmosphere.LastError, ["snowTexturesHeld"] = GeneratedSnowTexture.Holding });
            Try(audit, "dressing", () => { var i = World.MapDressingRuntime.Current; return i == null ? null : i.Record(); });
            Try(audit, "magic", () => { var s = World.MagicGroundRuntime.Snapshot; return new Dictionary<string, object> { ["drawn"] = World.MagicGroundRuntime.Drawn, ["errors"] = World.MagicGroundRuntime.Errors.ToList(),
                ["liveCells"] = s == null ? null : (object)s.LiveCells.Count(), ["retypedCells"] = s == null ? null : (object)s.RetypedCells.Length, ["digest"] = s == null ? null : s.Digest, ["disabled"] = s == null ? null : (object)s.MutationsDisabled }; });
            Try(audit, "mutation", () => { var b = WorldMutationBehavior.Current(); return b == null ? null : new Dictionary<string, object> { ["producer"] = b.Model.State.Producer, ["castRefusal"] = b.Model.CastRefusal(),
                ["edits"] = b.Model.State.Edits.Count, ["cells"] = b.Model.State.Cells.Count, ["pending"] = b.Model.State.Pending.Count, ["disabled"] = b.Model.DisabledReason }; });
            Try(audit, "veins", () => Veins());
            Try(audit, "magicFeatures", () => { var t = MagicType("MagicFeaturesRuntime"); return t == null ? (object)"not in this build (MMR-289 not merged)" : Get(t, "Record"); });
            Try(audit, "magicWater", () => { var t = MagicType("MagicWaterRuntime"); return t == null ? "not in this build (MMR-290 not merged)" : Call(t, "Record"); });
            Try(audit, "overlayMeshes", () => World.OverlayMeshRuntime.Record());   // MMR-291: rivers, veins and magic water as meshes (c33)
            Try(audit, "consoleCommands", () => ConsoleCommands("mnbl_magic.cast", "mnbl_magic.list"));
            Try(audit, "ruinsPage", () => new Dictionary<string, object> { ["registered"] = RuinsEncyclopediaPage.Registered, ["lastError"] = RuinsEncyclopediaPage.LastError });
            Try(audit, "producerProcesses", () => GenerationProbe.ProcessStarts.ToDictionary(k => k.Key, k => (object)k.Value));
            yield break;
        }

        private static void Try(Dictionary<string, object> row, string key, Func<object> read)
        {
            try { row[key] = read(); }
            catch (Exception e) { row[key] = new Dictionary<string, object> { ["error"] = e.GetType().Name + ": " + e.Message }; }
        }

        // Whether the debug console knows the commands: CommandLineFunctionality's registered functions, read by reflection.
        private static Dictionary<string, object> ConsoleCommands(params string[] names)
        {
            var type = typeof(CommandLineFunctionality);
            var row = new Dictionary<string, object>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var dict = f.GetValue(null) as IDictionary;
                if (dict == null) continue;
                foreach (var k in dict.Keys) known.Add(Convert.ToString(k, CultureInfo.InvariantCulture));
                row["source"] = f.Name;
            }
            foreach (var n in names) row[n] = known.Contains(n);
            row["registered"] = known.Count;
            return row;
        }

        // ---------------------------------------------------------------- census and saves

        // The day-0 census (live objects, the population behaviour's census), the economy, the home terrain per culture (RT-27), the wanderers'
        // backstories (RT-32: each wanderer's template lines, as the conversation reads them), lord traits.
        private static IEnumerator CensusStep(Acc a, Dictionary<string, object> row)
        {
            row["census"] = C.Census(a.World.Prefix);
            row["economy"] = C.Economy(a.World.Prefix);
            row["homeTerrain"] = C.HomeTerrain(a.World.Prefix);
            var keys = new[] { "prebackstory", "backstory_a", "backstory_b", "backstory_c", "backstory_d", "response_1", "response_2", "generic_backstory" };
            var stories = new List<object>();
            foreach (var h in Hero.AllAliveHeroes.Where(h => h.IsWanderer && h.Template != null).OrderBy(h => h.StringId, StringComparer.Ordinal))
            {
                var lines = new Dictionary<string, object>();
                foreach (var k in keys)
                {
                    TextObject text;
                    try { lines[k] = GameTexts.TryGetText(k, out text, h.Template.StringId) && text != null ? text.ToString() : null; }
                    catch (Exception e) { lines[k] = "error: " + e.GetType().Name; }
                }
                stories.Add(new Dictionary<string, object> { ["hero"] = h.StringId, ["name"] = h.Name.ToString(), ["template"] = h.Template.StringId, ["culture"] = h.Culture == null ? null : h.Culture.StringId, ["lines"] = lines });
            }
            row["backstories"] = stories;
            yield break;
        }

        // A manual save under the run's tag (no save of an earlier run is overwritten), waited for until its world binding is published.
        private static IEnumerator SaveStep(Acc a, Dictionary<string, object> row, string label)
        {
            string name = "mnbl_acc_" + a.Tag + "_" + label;
            row["name"] = name;
            if (MBSaveLoad.GetSaveFileWithName(name) != null) { Blocked(row, "a save named " + name + " exists (an earlier run with this tag); a new tag is needed"); yield break; }
            var before = WorldSaveBinding.Last;
            double start = a.Context.Seconds;
            Campaign.Current.SaveHandler.SaveAs(name);
            yield return null;
            while (a.Context.Seconds - start < 180 && (WorldSaveBinding.Last == before || WorldSaveBinding.Last.State == EarlyWorldBindingStore.Intent || Campaign.Current.SaveHandler.IsSaving)) yield return null;
            var journal = WorldSaveBinding.Last;
            row["seconds"] = a.Context.Seconds - start;
            row["published"] = journal != null && journal != before && journal.SaveName == name && journal.State == EarlyWorldBindingStore.Published;
            row["listed"] = MBSaveLoad.GetSaveFileWithName(name) != null;
            row["day"] = CampaignTime.Now.ToDays;
            a.Context.Outputs["save_" + label] = name;
            if (!(bool)row["published"]) throw new InvalidOperationException("the save " + name + " was not published");
            if (a.World != null) { a.Land.Save = name; a.Land.NoReload = false; }
        }

        // ---------------------------------------------------------------- faces, biomes, relief

        private static IEnumerator FacesStep(Acc a, Dictionary<string, object> row)
        {
            var byType = new Dictionary<string, List<C.FacePoint>>(StringComparer.Ordinal);
            yield return C.FacePoints(a.Context, row, byType, FacesKept, Interior(a));
            a.Faces = byType;
            row["typesFound"] = byType.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            row["interior"] = a.Cells == null ? "same-type faces in 3 x 3 buckets of 3 units" : "same-type travel cells in the 5 x 5 cells around the face (cells.bin)";
            row["best"] = byType.ToDictionary(k => k.Key, k => (object)(k.Value.Count == 0 ? null : new Dictionary<string, object> { ["at"] = new[] { k.Value[0].X, k.Value[0].Y }, ["neighbours"] = k.Value[0].Neighbours }));
        }

        private const int FacesKept = 200;

        private static IEnumerator EnsureFaces(Acc a)
        {
            if (a.Faces != null) yield break;
            var row = new Dictionary<string, object>();
            var byType = new Dictionary<string, List<C.FacePoint>>(StringComparer.Ordinal);
            yield return C.FacePoints(a.Context, row, byType, FacesKept, Interior(a));
            a.Faces = byType;
        }

        // The world's travel cells: the magic overlay's source (the bundle's cells.bin, read at map load), else the bundle's cells.bin read with
        // its travel graph's grid. Null (and the reason recorded once) when neither can be read.
        private static TravelCells EnsureCells(Acc a)
        {
            if (a.Cells != null) return a.Cells;
            try
            {
                var s = World.MagicGroundRuntime.Snapshot;
                if (s != null && s.Source != null && s.Source.Cells != null) return a.Cells = s.Source.Cells;
                var graph = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(a.World.Bundle, "compiled/travel/travel-graph.json")).TrimStart('﻿'));
                var grid = (Dictionary<string, object>)graph["grid"];
                Func<string, int> i = k => Convert.ToInt32(grid[k], CultureInfo.InvariantCulture);
                return a.Cells = TravelCells.Read(File.ReadAllBytes(Path.Combine(a.World.Bundle, "compiled/travel/cells.bin")), i("cols"), i("rows"), i("columnOffset"), i("rowOffset"), Convert.ToDouble(grid["spacing"], CultureInfo.InvariantCulture));
            }
            catch (Exception e) { a.Context.Outputs["cellsError"] = e.GetType().Name + ": " + e.Message; return null; }
        }

        // A travel cell's centre (physical) and the cell holding a physical point (-1 outside the pole lines).
        private static Vec2 CellCentre(TravelCells t, int i) { int r = i / t.Cols, c = i % t.Cols; return new Vec2((float)((c + 0.5 + t.ColumnOffset) * t.Spacing), (float)((r + 0.5 + t.RowOffset) * t.Spacing)); }
        private static int CellAt(TravelCells t, double x, double y)
        {
            int r = (int)Math.Floor(y / t.Spacing) - t.RowOffset, c = (int)Math.Floor(x / t.Spacing) - t.ColumnOffset;
            return t.InRows(r) ? t.Index(r, c) : -1;
        }
        private static string CellType(TravelCells t, int i) { return i < 0 || t.Classes[i] != TravelCells.Land ? null : TerrainTypes.Name(t.LandTypes[i]); }
        private static bool WaterCell(TravelCells t, double x, double y) { int i = CellAt(t, x, y); return i < 0 || TravelCells.Water(t.Classes[i]); }

        // The interior score of a face point (acc4: faces are about one per travel cell, so same-type faces in 3-unit buckets scored 1 almost
        // everywhere and every "most interior" face fell in the dense cluster beside a town): the travel cells of its kind in the 5 x 5 cells
        // around it (land types by cells.bin's land type, Road by the road bit, the water and mountain classes by class).
        private static Func<C.FacePoint, int> Interior(Acc a)
        {
            var t = EnsureCells(a);
            if (t == null) return null;
            var classOf = new Dictionary<string, byte>(StringComparer.Ordinal) { ["Mountain"] = TravelCells.Mountain, ["OpenSea"] = TravelCells.Sea, ["CoastalSea"] = TravelCells.Sea, ["Lake"] = TravelCells.Lake, ["River"] = TravelCells.River };
            return p =>
            {
                int centre = CellAt(t, p.X, p.Y);
                if (centre < 0) return 0;
                int r0 = centre / t.Cols, c0 = centre % t.Cols, n = 0; byte k;
                bool cls = classOf.TryGetValue(p.Type, out k);
                for (int r = r0 - 2; r <= r0 + 2; r++)
                    for (int c = c0 - 2; c <= c0 + 2; c++)
                    {
                        if (!t.InRows(r)) continue;
                        int i = t.Index(r, c);
                        if (p.Type == "Road" ? t.Road(i) : cls ? t.Classes[i] == k : CellType(t, i) == p.Type) n++;
                    }
                return n;
            };
        }

        // The bundle's biome plane (masks.bin, the bundle's own terrain-materials.json), as GeneratedCampaignInitializer.CellBiomes reads it.
        private static void EnsureBiomes(Acc a)
        {
            if (a.BiomeAt != null) return;
            string b = a.World.Bundle;
            a.BiomeAt = GeneratedCampaignInitializer.CellBiomes(File.ReadAllBytes(Path.Combine(b, "compiled/terrain/masks.bin")), File.ReadAllText(Path.Combine(b, "compiled/terrain/projection.json")),
                File.ReadAllText(Path.Combine(b, "policies/terrain-materials.json")));
            var materials = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(File.ReadAllText(Path.Combine(b, "policies/terrain-materials.json")).TrimStart('\uFEFF'));
            a.Table = ((object[])materials["biomes"]).Cast<Dictionary<string, object>>().GroupBy(x => (string)x["core"]).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var grid = Map2AcceptancePlan.Sample(a.BiomeAt, a.World.Width, a.World.Height, 2);
            var land = a.Table.Where(kv => !Equals(kv.Value["water"], true)).Select(kv => kv.Key).ToList();
            a.Spots = Map2AcceptancePlan.Interior(grid, land, 6);
        }

        // One screenshot per land biome of the shared table present on the world, at its most interior spot; 40 face samples per biome (the
        // face type the game reads there, "invalid" off land: information, against the table's TerrainType); the face the game reads at the
        // centre of up to 50 land travel cells of each land type (acc4 triage fails-1: c1/c7 grade the face against the cell's own land type,
        // cells.bin, which the table's corner majority made); the map trees.
        private static IEnumerator Biomes(Acc a, Dictionary<string, object> row)
        {
            var t = EnsureCells(a);
            if (t == null) row["cellSamples"] = null;
            else
            {
                var picked = Map2AcceptancePlan.CellSamples(t.Classes, t.LandTypes, 50);
                var cellSamples = new List<object>(); row["cellSamples"] = cellSamples;
                int n = 0;
                foreach (var kv in picked)
                    foreach (int i in kv.Value)
                    {
                        var p = CellCentre(t, i);
                        var face = C.FaceAt(p);
                        cellSamples.Add(new object[] { p.X, p.Y, TravelCells.ClassNames[t.Classes[i]], CellType(t, i), face[0], face[1] });
                        if (++n % 200 == 0) yield return null;
                    }
                row["cellSampling"] = new Dictionary<string, object> { ["perType"] = 50, ["landCells"] = t.Classes.Count(k => k == TravelCells.Land),
                    ["types"] = picked.ToDictionary(kv => TerrainTypes.Name(kv.Key) ?? kv.Key.ToString(CultureInfo.InvariantCulture), kv => (object)kv.Value.Count) };
            }
            EnsureBiomes(a);
            var grid = Map2AcceptancePlan.Sample(a.BiomeAt, a.World.Width, a.World.Height, 2);
            row["census"] = Map2AcceptancePlan.Census(grid);
            row["spots"] = a.Spots.ToDictionary(k => k.Key, k => (object)k.Value.Record());
            var samples = new Dictionary<string, object>(); row["faceSamples"] = samples;
            foreach (var biome in a.Spots.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var all = Enumerable.Range(0, grid.Biomes.Length).Where(i => grid.Biomes[i] == biome).ToList();
                int stride = Math.Max(1, all.Count / 40);
                var list = new List<object>();
                for (int k = 0; k < all.Count && list.Count < 40; k += stride)
                {
                    int i = all[k] % grid.Cols, j = all[k] / grid.Cols;
                    var p = a.World.Physical(grid.X(i), grid.Y(j));
                    list.Add(new object[] { p.X, p.Y, C.TerrainAt(p) });
                }
                samples[biome] = list;
            }
            try { row["flora"] = C.MapScene().GetFloraInstanceCount(); } catch (Exception e) { row["flora"] = e.GetType().Name; }
            foreach (var kv in a.Spots.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var p = a.World.Physical(kv.Value.X, kv.Value.Y);
                yield return Look(a, row, "biome-" + kv.Key, p, 35, 0, new Dictionary<string, object> { ["biome"] = kv.Key, ["face"] = C.TerrainAt(p), ["table"] = a.Table.ContainsKey(kv.Key) ? a.Table[kv.Key]["terrainType"] : null });
            }
        }

        // Mountains and coasts (screenshots), the pads under fortifications (the slope from each settlement's point to a ring of 8 at 1.5
        // units, the terrain the game reads), the pole lines (the camera aimed past each; MMR-278's clamp holds it at the line).
        private static IEnumerator Relief(Acc a, Dictionary<string, object> row)
        {
            yield return EnsureFaces(a);
            var scene = C.MapScene();
            List<C.FacePoint> mountains;
            if (a.Faces.TryGetValue("Mountain", out mountains))
                for (int k = 0; k < Math.Min(2, mountains.Count); k++)
                {
                    var p = mountains[k * Math.Max(1, mountains.Count / 2) % mountains.Count];
                    yield return Look(a, row, "mountain-" + k, new Vec2((float)p.X, (float)p.Y), 45, 0.6f, new Dictionary<string, object> { ["face"] = "Mountain" });
                }
            else row["mountains"] = "no Mountain face on this world";
            var ports = Settlement.All.Where(s => s.IsTown && s.HasPort && s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).ToList();
            for (int k = 0; k < Math.Min(2, ports.Count); k++)
            {
                var s = ports[k * Math.Max(1, ports.Count / 2) % ports.Count];
                yield return Look(a, row, "coast-" + k, s.PortPosition.ToVec2(), 35, 1.2f, new Dictionary<string, object> { ["town"] = s.StringId });
            }
            var pads = new List<object>(); int over25 = 0;
            foreach (var s in Settlement.All.Where(s => (s.IsTown || s.IsCastle) && s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                var v = s.Position.ToVec2();
                var heights = Map2AcceptancePlan.Ring(v.X, v.Y, 1.5, 8).Select(q => (double)scene.GetTerrainHeight(new Vec2((float)q[0], (float)q[1]), true)).ToList();
                double slope = Map2AcceptancePlan.PadSlopeDegrees(heights, 1.5);
                if (slope > 25) over25++;
                pads.Add(new object[] { s.StringId, C.F(slope) });
            }
            row["pads"] = pads; row["padsOver25"] = over25;
            var poles = new Dictionary<string, object>(); row["poles"] = poles;
            if (PeriodicWorld.Current == null) { poles["error"] = "not a periodic world"; yield break; }
            var c = PeriodicWorld.Current.Coordinates;
            foreach (var pole in new[] { new KeyValuePair<string, double>("north", c.OriginY - 60), new KeyValuePair<string, double>("south", c.OriginY + c.Height + 60) })
            {
                var r = new Dictionary<string, object>(); poles[pole.Key] = r;
                int clampsBefore = PeriodicMapCameraView.PoleClamps;
                var at = new Vec2((float)(c.OriginX + c.Width / 2), (float)pole.Value);
                C.AimFree(at.X, at.Y, 60, 0);
                yield return C.Settle(a.Context, r, 8, 6);
                var camera = (Dictionary<string, object>)r["camera"];
                var target = camera.ContainsKey("target") ? (double[])camera["target"] : null;
                r["aimedY"] = pole.Value; r["poleLine"] = pole.Key == "north" ? c.OriginY : c.OriginY + c.Height;
                r["targetY"] = target == null ? null : (object)target[1]; r["clamps"] = PeriodicMapCameraView.PoleClamps - clampsBefore;
                r["held"] = target != null && Math.Abs(target[1] - (double)r["poleLine"]) < 0.75;
                Shot(a, row, "pole-" + pole.Key, r);
                yield return C.Frames(4);
            }
            C.FollowParty(30);
        }

        // MMR-285's in-game water check (rivers, lakes, impassable water never dry), then every road and magic decal's centre tested for water
        // analytically, as MapDressing's PlaneCover shows it (acc4 triage fails-5: the closest-entity ray also met bridge and settlement meshes):
        // ground below the datum (the sea plane) or inside a shown lake plane's box above the ground. A decal's ends (0.8 of its half length
        // along its heading) are counted apart.
        private static IEnumerator Water(Acc a, Dictionary<string, object> row)
        {
            var water = new Dictionary<string, object>(); row["visibility"] = water;
            yield return WaterVisibilityProbe.Run(a.Context, water);
            object shots;
            if (water.TryGetValue("shots", out shots) && shots is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)shots)
                {
                    var r = kv.Value as Dictionary<string, object>;
                    if (r != null && r.ContainsKey("path")) a.Shots.Add(new Dictionary<string, object> { ["step"] = a.Step, ["label"] = "water-" + kv.Key, ["path"] = r["path"], ["at"] = r.ContainsKey("at") ? r["at"] : null });
                }
            C.FollowParty(30);
            var installer = World.MapDressingRuntime.Current;
            if (installer == null) { row["decalsOnWater"] = "no map dressing installed"; yield break; }
            // MMR-291 (c24, c33): the river overlay meshes at the densest 10-unit cluster of river stamps, shown and hidden ("river-mesh").
            Try(row, "overlayMeshes", () => World.OverlayMeshRuntime.Record());
            var river = installer.Plan.Water.Where(d => d.Kind == "river").GroupBy(d => ((long)Math.Floor(d.X / 10) << 32) ^ (long)(uint)(int)Math.Floor(d.Y / 10)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
            if (river == null) row["toggle"] = "no river stamp on this world";
            else if (!World.OverlayMeshRuntime.DrawsRivers) row["toggle"] = "rivers drawn as decals (no river mesh layer to toggle)";
            else yield return MeshToggle(a, row, OverlayLayer.Rivers, "river-mesh", new Vec2((float)river.Average(d => d.X), (float)river.Average(d => d.Y)), 28, new Dictionary<string, object> { ["riverStamps"] = river.Count() });
            var scene = C.MapScene();
            var centres = installer.Plan.Decals.Select(d => new KeyValuePair<string, double[]>("road", new[] { d.X, d.Y })).ToList();
            var magic = MagicInstaller();
            var plan = magic == null ? null : AccessTools.Property(magic.GetType(), "Plan").GetValue(magic) as MagicGroundPlan;
            if (plan != null) centres.AddRange(plan.Stamps.Select(s => new KeyValuePair<string, double[]>("magic", new[] { s.Decal.X, s.Decal.Y })));
            var onWater = new Dictionary<string, int> { ["road"] = 0, ["magic"] = 0 }; var endsOnWater = new Dictionary<string, int> { ["road"] = 0, ["magic"] = 0 };
            var examples = new List<object>(); int n = 0;
            var lakes = installer.Plan.Lakes;
            row["waterBodies"] = scene.FindEntitiesWithTag(GeneratedWaterSystem.Tag).Count();
            Func<double, double, string> cover = (x, y) => PlaneCover(lakes, x, y, scene.GetTerrainHeight(new Vec2((float)x, (float)y), true));
            var ends = installer.Plan.Decals.Select(d => new[] { d.X + 0.8 * d.HalfLength * Math.Cos(d.Yaw), d.Y + 0.8 * d.HalfLength * Math.Sin(d.Yaw), d.X - 0.8 * d.HalfLength * Math.Cos(d.Yaw), d.Y - 0.8 * d.HalfLength * Math.Sin(d.Yaw) }).ToList();
            for (int k = 0; k < centres.Count; k++)
            {
                var c = centres[k];
                try
                {
                    string at = cover(c.Value[0], c.Value[1]);
                    if (at != null) { onWater[c.Key]++; if (examples.Count < 30) examples.Add(new object[] { c.Key, c.Value[0], c.Value[1], scene.GetTerrainHeight(new Vec2((float)c.Value[0], (float)c.Value[1]), true), at }); }
                    else if (c.Key == "road" && k < ends.Count && (cover(ends[k][0], ends[k][1]) != null || cover(ends[k][2], ends[k][3]) != null)) endsOnWater[c.Key]++;
                }
                catch (Exception e) { if (examples.Count < 30) examples.Add(new object[] { c.Key, "error: " + e.Message }); }
                if (++n % 250 == 0) yield return null;
            }
            row["decalsTested"] = centres.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => (object)g.Count());
            row["decalsOnWater"] = onWater.ToDictionary(k => k.Key, k => (object)k.Value); row["decalsOnWaterExamples"] = examples;
            row["decalsEndOnWater"] = endsOnWater.ToDictionary(k => k.Key, k => (object)k.Value);
            row["decalsOnWaterRule"] = "analytic at the decal centre: ground below the datum (sea) or inside a shown lake plane's box with the plane above the ground (lake)";
        }

        // What shows a point (physical) as water without decals (MapDressing.PlaneCover's rule, kept here so the probe reads the plan the scene
        // drew): "sea" below the datum, "lake" inside a shown lake box with its plane above the ground, else null.
        private static string PlaneCover(IList<GeneratedWaterSystem.Lake> lakes, double x, double y, double ground)
        {
            if (ground < GeneratedWaterSystem.DatumZ) return "sea";
            foreach (var l in lakes) if (x >= l.MinX && x <= l.MaxX && y >= l.MinY && y <= l.MaxY && l.Z > ground) return "lake";
            return null;
        }

        private static object MagicInstaller()
        {
            var f = typeof(World.MagicGroundRuntime).GetField("installer", BindingFlags.Static | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(null);
        }

        // Roads, bridges and ruin markers: what is drawn against the plan, each settlement's distance to its nearest drawn road decal, a
        // screenshot of a road at a town gate, of a bridge and of a ruin marker.
        private static IEnumerator Dressing(Acc a, Dictionary<string, object> row)
        {
            var installer = World.MapDressingRuntime.Current;
            if (installer == null) { Blocked(row, "no map dressing installed on this map"); yield break; }
            var plan = installer.Plan;
            row["drawn"] = new Dictionary<string, object> { ["roads"] = installer.DrawnCount(0), ["bridges"] = installer.DrawnCount(1), ["ruins"] = installer.DrawnCount(2), ["water"] = installer.DrawnCount(3) };
            row["planned"] = new Dictionary<string, object> { ["roads"] = plan.Decals.Count, ["bridges"] = plan.Bridges.Count, ["ruins"] = plan.Markers.Count, ["ruinsTotal"] = plan.Ruins.Count, ["water"] = plan.Water.Count };
            row["errors"] = installer.Errors.ToList();
            var hash = new Dictionary<long, List<RoadDecal>>();
            Func<double, double, long> key = (x, y) => ((long)Math.Floor(x / 4) << 32) ^ (long)(uint)(int)Math.Floor(y / 4);
            foreach (var d in plan.Decals) { long k = key(d.X, d.Y); List<RoadDecal> l; if (!hash.TryGetValue(k, out l)) hash[k] = l = new List<RoadDecal>(); l.Add(d); }
            var reach = new List<object>();
            foreach (var s in Settlement.All.Where(s => (s.IsTown || s.IsCastle || s.IsVillage) && s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal))
            {
                var g = s.GatePosition.ToVec2(); double best = double.PositiveInfinity;
                for (int r = 0; r <= 4 && double.IsInfinity(best); r++)
                    for (int dx = -r; dx <= r; dx++) for (int dy = -r; dy <= r; dy++)
                    {
                        List<RoadDecal> l;
                        if (!hash.TryGetValue(key(Wrap(a, g.X + dx * 4), g.Y + dy * 4), out l)) continue;
                        foreach (var d in l) best = Math.Min(best, C.Distance(g, new Vec2((float)d.X, (float)d.Y)));
                    }
                reach.Add(new object[] { s.StringId, s.IsTown ? "town" : s.IsCastle ? "castle" : "village", C.F(best) });
            }
            row["nearestRoad"] = reach;
            var town = Settlement.All.Where(s => s.IsTown && s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).OrderBy(s => s.StringId, StringComparer.Ordinal).FirstOrDefault();
            if (town != null) yield return Look(a, row, "road-at-town", town.GatePosition.ToVec2(), 22, 0.3f, new Dictionary<string, object> { ["town"] = town.StringId });
            if (plan.Bridges.Count > 0) yield return Look(a, row, "bridge", new Vec2((float)plan.Bridges[0].X, (float)plan.Bridges[0].Y), 14, 0.4f, new Dictionary<string, object> { ["prefab"] = plan.Bridges[0].Prefab, ["kind"] = plan.Bridges[0].Kind });
            else row["bridgeShot"] = "no bridge on this world";
            var marker = installer.DrawnMarkers().Select(m => m.Key).FirstOrDefault();
            if (marker != null) yield return Look(a, row, "ruin-marker", new Vec2((float)marker.X, (float)marker.Y), 12, 0.2f, new Dictionary<string, object> { ["ruin"] = marker.Ruin.Uid, ["name"] = marker.Ruin.Name, ["placement"] = marker.Placement });
            else row["ruinShot"] = "no drawn ruin marker";
        }

        private static double Wrap(Acc a, double x) { return PeriodicWorld.Current == null ? x : PeriodicWorld.Current.Coordinates.CanonicalX(x); }

        // Generated magic ground (visual only): the drawn stamps, no cell retyped before any cast, and screenshots of generated zones for the
        // owner's review of the vein look (rev5 c29): the densest cluster ("magic-ground") and the densest clusters of two other schools
        // ("magic-zone-1", "magic-zone-2"); MMR-288's vein graph record, and MMR-289's features in the generated zones (c30: drawn, none on
        // water, a road decal or a settlement's clearance) with a screenshot of one ("magic-features"). Both are read by reflection: a build
        // without them records "not in this build".
        private static IEnumerator MagicGround(Acc a, Dictionary<string, object> row)
        {
            var snapshot = World.MagicGroundRuntime.Snapshot;
            row["snapshot"] = snapshot == null ? null : new Dictionary<string, object> { ["liveCells"] = snapshot.LiveCells.Count(), ["retypedCells"] = snapshot.RetypedCells.Length, ["digest"] = snapshot.Digest, ["disabled"] = snapshot.MutationsDisabled };
            row["veins"] = Veins();
            row["features"] = Features(a);
            var magic = MagicInstaller();
            var plan = magic == null ? null : AccessTools.Property(magic.GetType(), "Plan").GetValue(magic) as MagicGroundPlan;
            if (plan == null) { Blocked(row, "no magic ground installer on this map (a magic-off world, or the install failed: see the identity's support report)"); yield break; }
            row["plan"] = plan.Record(); row["drawn"] = World.MagicGroundRuntime.Drawn;
            Try(row, "installer", () => magic.GetType().GetMethod("Record", BindingFlags.Instance | BindingFlags.Public).Invoke(magic, new object[0]));
            Try(row, "overlayMeshes", () => World.OverlayMeshRuntime.Record());   // MMR-291 (c29, c33)
            // MMR-291: while the overlay meshes draw the veins the decal installer's plan holds no land stamps; the zones come from the full plan.
            var full = World.MagicGroundRuntime.Plan ?? plan;
            row["veinsAsMeshes"] = World.OverlayMeshRuntime.DrawsVeins; row["fullPlanStamps"] = full.Stamps.Count;
            var generated = full.Stamps.Where(s => !s.Live).ToList();
            if (generated.Count == 0) { row["shot"] = "no generated variant stamps"; yield break; }
            var clusters = generated.GroupBy(s => ((long)Math.Floor(s.Decal.X / 8) << 32) ^ (long)(uint)(int)Math.Floor(s.Decal.Y / 8)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
            var zones = new List<object>(); row["zones"] = zones;
            var schools = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cluster in clusters)
            {
                string school = cluster.GroupBy(s => s.School).OrderByDescending(g => g.Count()).First().Key;
                if (zones.Count > 0 && schools.Contains(school)) continue;
                schools.Add(school);
                string label = zones.Count == 0 ? "magic-ground" : "magic-zone-" + zones.Count;
                var centre = new Vec2((float)cluster.Average(s => s.Decal.X), (float)cluster.Average(s => s.Decal.Y));
                var what = new Dictionary<string, object> { ["stamps"] = cluster.Count(), ["schools"] = cluster.Select(s => s.School).Distinct().ToList(), ["face"] = C.TerrainAt(centre) };
                zones.Add(new Dictionary<string, object> { ["label"] = label, ["at"] = C.P(centre), ["stamps"] = what["stamps"], ["schools"] = what["schools"] });
                yield return Look(a, row, label, centre, 28, 0, what, true, 8, MeshesAt(OverlayLayer.Veins, centre, 28));
                // MMR-291 (c29, c33): the densest zone's vein meshes shown and hidden from the free camera ("magic-veins").
                if (zones.Count == 1)
                {
                    if (World.OverlayMeshRuntime.DrawsVeins) yield return MeshToggle(a, row, OverlayLayer.Veins, "magic-veins", centre, 28, new Dictionary<string, object> { ["stamps"] = what["stamps"] });
                    else row["toggle"] = "veins drawn as decals (no vein mesh layer to toggle)";
                }
                if (zones.Count >= 3) break;
            }
            var feature = FeatureSpot(false);
            if (feature != null) yield return Look(a, row, "magic-features", feature.Value, 14, 0.5f, new Dictionary<string, object> { ["feature"] = "the first drawn generated source feature" });
        }

        // ---------------------------------------------------------------- MMR-288/289/290 by reflection (rev5: c29, c30, c31)

        // MMR-288's vein graph, MMR-289's features and MMR-290's magic water are read by reflection, so this probe builds and runs on a build
        // without them: a missing type is recorded as not in this build and the part (or step) is blocked, never thrown.
        private static Type MagicType(string name) { return typeof(World.MagicGroundRuntime).Assembly.GetType("MNBL.ProceduralWorld.World." + name, false); }

        // A public or private field or property (static when target is a Type), walking base types; null when absent.
        private static object Get(object target, string name)
        {
            if (target == null) return null;
            var type = target as Type ?? target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
            object self = target is Type ? null : target;
            for (var t = type; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, flags);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(self);
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(self);
            }
            return null;
        }

        private static object Call(object target, string name, params object[] args)
        {
            var type = target as Type ?? target.GetType();
            var m = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | (target is Type ? BindingFlags.Static : BindingFlags.Instance)).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(type.FullName, name);
            return m.Invoke(target is Type ? null : target, args);
        }

        private static double Number(object o, string name) { var v = Get(o, name); return v == null ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        private static bool Flag(object o, string name) { var v = Get(o, name); return v is bool && (bool)v; }
        private static List<object> Strings(object list) { return list is IEnumerable ? ((IEnumerable)list).Cast<object>().Select(x => (object)Convert.ToString(x, CultureInfo.InvariantCulture)).ToList() : null; }

        // MMR-288: the fully grown vein graph of the loaded map (MagicGroundRuntime.Veins) and the reveal clock (MagicGroundRuntime.Hours).
        private static Dictionary<string, object> Veins()
        {
            var runtime = typeof(World.MagicGroundRuntime);
            if (runtime.GetProperty("Veins", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null && runtime.GetField("Veins", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
                return new Dictionary<string, object> { ["available"] = false, ["reason"] = "MagicGroundRuntime.Veins is not in this build (MMR-288 not merged)" };
            var r = new Dictionary<string, object> { ["available"] = true };
            try
            {
                var graph = Get(runtime, "Veins");
                r["hours"] = Get(runtime, "Hours");
                r["record"] = graph == null ? null : Call(graph, "Record");
            }
            catch (Exception e) { r["error"] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            return r;
        }

        // MMR-289's features as drawn (MagicFeaturesRuntime: its Record, the installer's revealed plan): drawn counts, generated and live-cast
        // features, and how many stand on water (a water travel cell, the ground below the datum or under a shown lake plane), on a drawn road
        // decal, or within 5 units of a settlement (MMR-289's clearance).
        private static Dictionary<string, object> Features(Acc a)
        {
            var type = MagicType("MagicFeaturesRuntime");
            if (type == null) return new Dictionary<string, object> { ["available"] = false, ["reason"] = "MagicFeaturesRuntime is not in this build (MMR-289 not merged)" };
            var r = new Dictionary<string, object> { ["available"] = true };
            try
            {
                r["record"] = Get(type, "Record");
                r["errors"] = Strings(Get(type, "Errors"));
                var installer = Get(type, "Current");
                r["live"] = installer == null ? 0 : Convert.ToInt32(Get(installer, "Live"), CultureInfo.InvariantCulture);
                r["liveEntities"] = installer == null ? null : Get(installer, "LiveEntities");
                r["missingMeshes"] = installer == null ? null : Strings(Get(installer, "MissingMeshes"));
                var plan = installer == null ? null : Get(installer, "Plan");
                var features = plan == null ? new List<object>() : ((Get(plan, "Features") as IEnumerable) ?? new object[0]).Cast<object>().ToList();
                var dressing = World.MapDressingRuntime.Current;
                var scene = C.MapScene(); var t = EnsureCells(a);
                var settlements = Settlement.All.Where(x => x.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).Select(x => x.Position.ToVec2()).ToList();
                int generated = 0, live = 0, onWater = 0, onRoad = 0, near = 0; var examples = new List<object>();
                foreach (var f in features)
                {
                    double x = Number(f, "X"), y = Number(f, "Y");
                    if (Flag(f, "Live")) live++; else generated++;
                    bool water = (t != null && WaterCell(t, x, y)) || (scene != null && dressing != null && PlaneCover(dressing.Plan.Lakes, x, y, scene.GetTerrainHeight(new Vec2((float)x, (float)y), true)) != null);
                    bool road = dressing != null && dressing.Plan.Decals.Any(d => d.Covers(x, y, dressing.Plan.Width, 1));
                    bool close = settlements.Any(v => C.Distance(v, new Vec2((float)x, (float)y)) < 5);
                    if (water) onWater++; if (road) onRoad++; if (close) near++;
                    if ((water || road || close) && examples.Count < 10) examples.Add(new object[] { Get(f, "Id"), x, y, water, road, close });
                }
                r["drawnFeatures"] = features.Count; r["generated"] = generated; r["liveMutation"] = live; r["onWater"] = onWater; r["onRoad"] = onRoad; r["nearSettlement"] = near; r["examples"] = examples;
            }
            catch (Exception e) { r["error"] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            return r;
        }

        // A drawn feature's position (a source feature first), generated or of the live cast; null without MMR-289 or features.
        private static Vec2? FeatureSpot(bool live)
        {
            try
            {
                var type = MagicType("MagicFeaturesRuntime");
                var plan = type == null ? null : Get(Get(type, "Current"), "Plan");
                var features = plan == null ? null : (Get(plan, "Features") as IEnumerable);
                if (features == null) return null;
                var f = features.Cast<object>().Where(x => Flag(x, "Live") == live).OrderBy(x => Convert.ToString(Get(x, "Role"), CultureInfo.InvariantCulture) == "source" ? 0 : 1).FirstOrDefault();
                return f == null ? (Vec2?)null : new Vec2((float)Number(f, "X"), (float)Number(f, "Y"));
            }
            catch (Exception) { return null; }
        }

        // MMR-290's magic water (rev5 c31): MagicWaterRuntime.Record(), the stamps drawn, the layer's errors; the camera over the densest cluster
        // of generated water vein stamps (MMR-288's graph, OnWater) or of the drawn ones, a screenshot ("magic-water"), the stamps hidden
        // (MagicWaterRuntime.SetVisible(false)), the same view again ("magic-water-hidden"; the toggle diff settles route R1), shown again.
        private static IEnumerator MagicWaterStep(Acc a, Dictionary<string, object> row)
        {
            var type = MagicType("MagicWaterRuntime");
            if (type == null) { Blocked(row, "MagicWaterRuntime is not in this build (MMR-290 not merged): c31 cannot be checked"); yield break; }
            Try(row, "record", () => Call(type, "Record"));
            Try(row, "drawn", () => Get(type, "Drawn"));
            Try(row, "errors", () => Strings(Get(type, "Errors")));
            Vec2? at = null; int stamps = 0;
            try
            {
                var graph = Get(typeof(World.MagicGroundRuntime), "Veins");
                var water = graph == null ? new List<object>() : ((Get(graph, "Stamps") as IEnumerable) ?? new object[0]).Cast<object>().Where(x => Flag(x, "OnWater") && !Flag(x, "Live")).ToList();
                var best = water.GroupBy(x => ((long)Math.Floor(Number(x, "X") / 10) << 32) ^ (long)(uint)(int)Math.Floor(Number(x, "Y") / 10)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
                if (best != null) { at = new Vec2((float)best.Average(x => Number(x, "X")), (float)best.Average(x => Number(x, "Y"))); stamps = best.Count(); }
            }
            catch (Exception e) { row["spotError"] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            if (at == null) { Blocked(row, "no generated magic water stamp on this world's vein graph to look at"); yield break; }
            var toggle = new Dictionary<string, object> { ["label"] = "magic-water", ["hiddenLabel"] = "magic-water-hidden", ["at"] = C.P(at.Value), ["stamps"] = stamps, ["layer"] = OverlayLayer.MagicWater };
            row["toggle"] = toggle;
            Try(row, "overlayMeshes", () => World.OverlayMeshRuntime.Record());   // MMR-291 (c31, c33)
            yield return Look(a, row, "magic-water", at.Value, 38, 0, new Dictionary<string, object> { ["stamps"] = stamps, ["visible"] = true }, false, 12, MeshesAt(OverlayLayer.MagicWater, at.Value, 38));
            bool hidden = false;
            try { hidden = Equals(Call(type, "SetVisible", false), true); } catch (Exception e) { toggle["error"] = (e.InnerException ?? e).Message; }
            toggle["setVisibleFalse"] = hidden;
            yield return C.Frames(10);
            yield return Look(a, row, "magic-water-hidden", at.Value, 38, 0, new Dictionary<string, object> { ["stamps"] = stamps, ["visible"] = false }, false, 12, MeshesAt(OverlayLayer.MagicWater, at.Value, 38));
            bool shown = false;
            try { shown = Equals(Call(type, "SetVisible", true), true); } catch (Exception e) { toggle["errorShow"] = (e.InnerException ?? e).Message; }
            toggle["setVisibleTrue"] = shown;
        }

        // MMR-291 (c29, c31, c33): an overlay mesh layer at a shot (OverlayMeshRuntime.InView: drawn as meshes, live entities and pieces, hidden,
        // and the entities whose drawn rectangle meets the view; default taken: 116 x 74 units at distance 38, scaled with the distance).
        private static Action<Dictionary<string, object>> MeshesAt(string layer, Vec2 at, float distance)
        {
            return record => record["meshLayer"] = World.OverlayMeshRuntime.InView(layer, at.X, at.Y, 58.0 * distance / 38, 37.0 * distance / 38);
        }

        // MMR-291 (c24, c29, c33): an overlay mesh layer shown (label) and hidden (label-hidden) from the free camera at the same view, then shown
        // again; row["toggle"] names the pair (the summarizer's toggle diff) and whether each switch took (OverlayMeshRuntime.SetVisible).
        private static IEnumerator MeshToggle(Acc a, Dictionary<string, object> row, string layer, string label, Vec2 at, float distance, Dictionary<string, object> what)
        {
            var toggle = new Dictionary<string, object> { ["layer"] = layer, ["label"] = label, ["hiddenLabel"] = label + "-hidden", ["at"] = C.P(at) };
            row["toggle"] = toggle;
            yield return Look(a, row, label, at, distance, 0, new Dictionary<string, object>(what) { ["visible"] = true }, false, 12, MeshesAt(layer, at, distance));
            toggle["setVisibleFalse"] = World.OverlayMeshRuntime.SetVisible(layer, false);
            try
            {
                yield return C.Frames(10);
                yield return Look(a, row, label + "-hidden", at, distance, 0, new Dictionary<string, object>(what) { ["visible"] = false }, false, 12, MeshesAt(layer, at, distance));
            }
            finally { toggle["setVisibleTrue"] = World.OverlayMeshRuntime.SetVisible(layer, true); }   // shown again whatever happened
        }

        // A town, a castle and a village of each presentation culture (screenshots close up), and the initial wall levels.
        private static IEnumerator Looks(Acc a, Dictionary<string, object> row)
        {
            var mine = Settlement.All.Where(s => s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal) && s.Culture != null).ToList();
            row["wallLevels"] = mine.Where(s => s.IsTown || s.IsCastle).GroupBy(s => (s.IsTown ? "town:" : "castle:") + s.Town.GetWallLevel()).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            row["selection"] = Path.Combine(GeneratedWorldPackage.Root, a.World.Package, "looks", "selection.json");
            foreach (var culture in mine.Select(s => s.Culture.StringId).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (var kind in new[] { "town", "castle", "village" })
                {
                    var s = mine.Where(x => x.Culture.StringId == culture && (kind == "town" ? x.IsTown : kind == "castle" ? x.IsCastle : x.IsVillage)).OrderBy(x => x.StringId, StringComparer.Ordinal).FirstOrDefault();
                    if (s == null) continue;
                    yield return Look(a, row, "look-" + culture + "-" + kind, s.Position.ToVec2(), kind == "village" ? 9 : 12, 0.5f, new Dictionary<string, object> { ["settlement"] = s.StringId, ["kind"] = kind, ["culture"] = culture,
                        ["villageType"] = s.IsVillage && s.Village.VillageType != null ? s.Village.VillageType.StringId : null, ["wall"] = s.Town == null ? -1 : s.Town.GetWallLevel() });
                }
            }
        }

        // The speed tooltip's numbers on each face type the world has (the most interior free land face of the type) and on a road cell. acc4
        // triage fails-3: the face samples stand off road cells (the road term would join every type's lines), the Road sample on a Plain or
        // Steppe road cell (its only extra term is then the road's), a Bridge on its deck. The world's land types (cells.bin) are recorded
        // so a type the world has no cell of (Swamp on A' and B') reads as not applicable.
        private static IEnumerator SpeedStep(Acc a, Dictionary<string, object> row)
        {
            yield return EnsureFaces(a);
            var t = EnsureCells(a);
            if (t != null) row["cellTypes"] = Enumerable.Range(0, t.Classes.Length).Select(i => CellType(t, i)).Where(n => n != null).GroupBy(n => n).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            var roads = GeneratedPartySpeedModel.Active;
            Func<C.FacePoint, bool> offRoad = q => roads == null || !roads.At(q.X, q.Y);
            var main = MobileParty.MainParty; var home = main.Position;
            try
            {
                foreach (var type in new[] { "Plain", "Steppe", "Desert", "Forest", "Snow", "Swamp", "Dune", "Road", "Bridge", "Fording" })
                {
                    List<C.FacePoint> points;
                    C.FacePoint p = null; bool off = false;
                    if (a.Faces.TryGetValue(type, out points))
                    {
                        if (type == "Road") p = C.FreeLand(points, a.World.Prefix, 2.5, q => q.Under == "Plain" || q.Under == "Steppe") ?? C.FreeLand(points, a.World.Prefix, 2.5);
                        else if (type == "Bridge" || type == "Fording") p = C.FreeLand(points, a.World.Prefix, 2.5);
                        else { p = C.FreeLand(points, a.World.Prefix, 2.5, offRoad); off = p != null; if (p == null) p = C.FreeLand(points, a.World.Prefix, 2.5); }
                    }
                    if (p == null) { row[type] = new Dictionary<string, object> { ["absent"] = true, ["reason"] = points == null ? "no face of the type" : "no free face of the type (2.5 units from settlements, valid land)" }; continue; }
                    C.Place(new Vec2((float)p.X, (float)p.Y));
                    yield return C.Frames(3);
                    var r = C.Speed(main); r["wanted"] = type; r["offRoad"] = off; row[type] = r;
                }
            }
            finally { main.Position = home; main.SetMoveModeHold(); }
        }

        // A crossing of the seam between two different land biomes: the party placed west of the cut, sent to the point east of it at play
        // speed (sheltered), its track recorded until it arrives; the speed numbers and a screenshot on each side.
        private static IEnumerator Seam(Acc a, Dictionary<string, object> row)
        {
            EnsureBiomes(a);
            if (PeriodicWorld.Current == null) { Blocked(row, "not a periodic world"); yield break; }
            var land = new HashSet<string>(a.Table.Where(kv => !Equals(kv.Value["water"], true)).Select(kv => kv.Key), StringComparer.Ordinal);
            var candidates = Map2AcceptancePlan.Seam(a.BiomeAt, b => land.Contains(b), a.World.Width, a.World.Height, 1, Map2AcceptancePlan.SeamInsets, 12);
            row["candidates"] = candidates.Select(c => (object)new object[] { c.Y, c.West, c.East, c.Inset, c.Run }).ToList();
            Map2AcceptancePlan.SeamCrossing chosen = null; CampaignVec2 west = default(CampaignVec2), east = default(CampaignVec2);
            foreach (var c in candidates)
            {
                var w = new CampaignVec2(a.World.Physical(c.WestX, c.Y), true); var e = new CampaignVec2(a.World.Physical(c.EastX, c.Y), true);
                if (!w.IsValid() || !e.IsValid() || GeneratedSpawnAssertions.Problem(w, MobileParty.NavigationType.Default) != null || GeneratedSpawnAssertions.Problem(e, MobileParty.NavigationType.Default) != null) continue;
                chosen = c; west = w; east = e; break;
            }
            if (chosen == null) { Blocked(row, "no seam row with two different land biomes on valid land on both sides"); yield break; }
            row["chosen"] = new Dictionary<string, object> { ["y"] = chosen.Y, ["west"] = chosen.West, ["east"] = chosen.East, ["inset"] = chosen.Inset, ["westAt"] = C.P(west.ToVec2()), ["eastAt"] = C.P(east.ToVec2()) };
            var main = MobileParty.MainParty; var home = main.Position;
            try
            {
                C.Place(west.ToVec2()); C.FollowParty(25);
                yield return RuntimeMissionProbe.SettleCamera(a.Context);
                row["westSpeed"] = C.Speed(main); row["westFace"] = C.FaceTypeAt(main.Position);
                Shot(a, row, "seam-west", new Dictionary<string, object> { ["biome"] = chosen.West });
                main.SetMoveGoToPoint(east, MobileParty.NavigationType.Default);
                DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
                var track = new List<object>(); double start = a.Context.Seconds; float lastX = main.Position.ToVec2().X; bool jumped = false;
                while (a.Context.Seconds - start < 90 && C.Distance(main.Position.ToVec2(), east.ToVec2()) > 0.6f)
                {
                    if (PlayerEncounter.Current != null) { TwoWorldMatrixProbe.ClearEncounter(row); DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay); }
                    float x = main.Position.ToVec2().X;
                    if (Math.Abs(x - lastX) > a.World.Width / 2) jumped = true;
                    lastX = x;
                    if (track.Count < 400 && (track.Count == 0 || a.Context.Seconds - start > track.Count * 0.25)) track.Add(new object[] { Math.Round(a.Context.Seconds - start, 2), x, main.Position.ToVec2().Y, C.FaceTypeAt(main.Position) });
                    if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
                    yield return null;
                }
                RuntimeMissionProbe.Pause();
                row["track"] = track; row["arrived"] = C.Distance(main.Position.ToVec2(), east.ToVec2()) <= 0.6f; row["seconds"] = a.Context.Seconds - start; row["canonicalJump"] = jumped;
                row["eastSpeed"] = C.Speed(main); row["eastFace"] = C.FaceTypeAt(main.Position);
                yield return RuntimeMissionProbe.SettleCamera(a.Context);
                Shot(a, row, "seam-east", new Dictionary<string, object> { ["biome"] = chosen.East });
            }
            finally { RuntimeMissionProbe.Pause(); main.Position = home; main.SetMoveModeHold(); }
        }

        // ---------------------------------------------------------------- vanilla parity

        // MMR-287's incidents with debug calls: each culture incident's condition (the installed delegate, transpiled) run with the player's last
        // visited settlement one of a kingdom of its culture (villages first, then towns, castles), and the terrain incidents' face
        // predicates (every incident condition that reads IMapScene.GetFaceTerrainType, the face types it compares with) at a Desert, a
        // Forest and a Steppe place; each full condition is run there too. The player's party holds 60 troops; everything is put back.
        private static IEnumerator Incidents(Acc a, Dictionary<string, object> row)
        {
            yield return EnsureFaces(a);
            var main = MobileParty.MainParty; var home = main.Position;
            var lastVisited = AccessTools.Field(typeof(MobileParty), "<LastVisitedSettlement>k__BackingField");
            var wasVisited = lastVisited == null ? null : lastVisited.GetValue(main);
            row["crewAdded"] = LandMissionsProbe.Crew(main, 60);
            var behaviour = AccessTools.TypeByName(IncidentCulturePatch.BehaviourType);
            try
            {
                var culture = new Dictionary<string, object>(); row["culture"] = culture;
                foreach (var r in IncidentCulturePatch.Resolve(behaviour, IncidentCulturePatch.Instructions))
                {
                    var c = new Dictionary<string, object> { ["literals"] = r.Incident.Literals, ["resolved"] = r.Resolved, ["problem"] = r.Problem };
                    culture[r.Incident.Id] = c;
                    if (r.Condition == null) continue;
                    var sites = Settlement.All.Where(s => s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal) && s.MapFaction is Kingdom && r.Incident.Literals.Contains(IncidentCulturePatch.FactionCultureId(s.MapFaction)))
                        .OrderBy(s => s.IsVillage ? 0 : s.IsTown ? 1 : 2).ThenBy(s => s.StringId, StringComparer.Ordinal).ToList();
                    var tries = new List<object>(); c["tries"] = tries; c["candidates"] = sites.Count;
                    var cultures = Kingdom.All.Where(k => k.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal) && !k.IsEliminated).Select(k => IncidentCulturePatch.FactionCultureId(k)).Where(x => x != null).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                    c["kingdomCultures"] = cultures; c["kingdomOfCulture"] = r.Incident.Literals.Any(l => cultures.Contains(l));
                    var kinds = sites.GroupBy(s => s.IsVillage ? "village" : s.IsTown ? "town" : "castle").Select(g => g.First()).ToList();
                    foreach (var s in kinds)
                    {
                        if (lastVisited != null) lastVisited.SetValue(main, s);
                        object result = Invoke(r.Condition);
                        tries.Add(new object[] { s.StringId, s.IsVillage ? "village" : s.IsTown ? "town" : "castle", IncidentCulturePatch.FactionCultureId(s.MapFaction), result });
                        if (Equals(result, true)) { c["trueAt"] = s.StringId; break; }
                    }
                    if (sites.Count == 0) c["note"] = "no settlement of a kingdom of these cultures on this world";
                }
                var terrain = new Dictionary<string, object>(); row["terrain"] = terrain;
                var predicates = TerrainPredicates(behaviour);
                terrain["conditions"] = predicates.Select(p => (object)new Dictionary<string, object> { ["incident"] = p.Incident, ["method"] = p.Method.DeclaringType.Name + "::" + p.Method.Name,
                    ["faceTypes"] = p.Types.Select(t => (object)((TerrainType)t).ToString()).ToList() }).ToList();
                foreach (var type in new[] { "Desert", "Forest", "Steppe" })
                {
                    List<C.FacePoint> points;
                    var p = a.Faces.TryGetValue(type, out points) ? C.FreeLand(points, a.World.Prefix, 2.5) : null;
                    var at = new Dictionary<string, object>(); terrain[type] = at;
                    if (p == null) { at["absent"] = true; continue; }
                    C.Place(new Vec2((float)p.X, (float)p.Y));
                    yield return C.Frames(2);
                    int face = (int)Campaign.Current.MapSceneWrapper.GetFaceTerrainType(main.Position.Face);
                    at["at"] = new[] { p.X, p.Y }; at["face"] = ((TerrainType)face).ToString();
                    at["predicates"] = predicates.Select(q => (object)new object[] { q.Incident, q.Types.Contains(face), q.Types.Contains((int)Enum.Parse(typeof(TerrainType), type)), Invoke(q.Method) }).ToList();
                }
            }
            finally
            {
                if (lastVisited != null) lastVisited.SetValue(main, wasVisited);
                main.Position = home; main.SetMoveModeHold();
            }
        }

        private sealed class Predicate { public string Incident; public MethodInfo Method; public List<int> Types = new List<int>(); }

        // Every incident condition (lambda bodies of the behaviour and its nested types) that calls IMapScene.GetFaceTerrainType, with the
        // constants it compares the face type with (the ldc.i4 operands right after each call) and the incident id that registers it.
        private static List<Predicate> TerrainPredicates(Type behaviour)
        {
            var result = new List<Predicate>();
            if (behaviour == null) return result;
            var getFace = AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Map.IMapScene), "GetFaceTerrainType");
            var registered = new Dictionary<MethodInfo, string>();
            var types = new[] { behaviour }.Concat(behaviour.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)).ToList();
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(x => x.GetMethodBody() != null))
                {
                    List<CodeInstruction> code;
                    try { code = PatchProcessor.GetOriginalInstructions(m); } catch (Exception) { continue; }
                    string id = null;
                    for (int i = 0; i < code.Count; i++)
                    {
                        if (code[i].opcode == OpCodes.Ldstr && (code[i].operand as string ?? "").StartsWith("incident_", StringComparison.Ordinal)) id = (string)code[i].operand;
                        else if (code[i].opcode == OpCodes.Ldftn && id != null && code[i].operand is MethodInfo && !registered.ContainsKey((MethodInfo)code[i].operand)) registered[(MethodInfo)code[i].operand] = id;
                    }
                    var p = new Predicate { Method = m };
                    for (int i = 0; i < code.Count; i++)
                    {
                        if (!code[i].Calls(getFace)) continue;
                        // The face type is compared at once (ldc; ceq/beq/bne) or kept in a local and compared with several constants.
                        for (int k = i + 1; k < Math.Min(code.Count - 1, i + 9); k++)
                        {
                            int? v = Constant(code[k]);
                            if (v.HasValue && Compares(code[k + 1].opcode) && !p.Types.Contains(v.Value)) p.Types.Add(v.Value);
                        }
                    }
                    if (p.Types.Count > 0) result.Add(p);
                }
            foreach (var p in result) { string id; p.Incident = registered.TryGetValue(p.Method, out id) ? id : null; }
            return result;
        }

        private static bool Compares(OpCode op)
        {
            return op == OpCodes.Ceq || op == OpCodes.Beq || op == OpCodes.Beq_S || op == OpCodes.Bne_Un || op == OpCodes.Bne_Un_S;
        }

        private static int? Constant(CodeInstruction i)
        {
            var op = i.opcode;
            if (op == OpCodes.Ldc_I4_0) return 0; if (op == OpCodes.Ldc_I4_1) return 1; if (op == OpCodes.Ldc_I4_2) return 2; if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4; if (op == OpCodes.Ldc_I4_5) return 5; if (op == OpCodes.Ldc_I4_6) return 6; if (op == OpCodes.Ldc_I4_7) return 7; if (op == OpCodes.Ldc_I4_8) return 8;
            if (op == OpCodes.Ldc_I4_S) return Convert.ToInt32(i.operand, CultureInfo.InvariantCulture);
            if (op == OpCodes.Ldc_I4) return Convert.ToInt32(i.operand, CultureInfo.InvariantCulture);
            return null;
        }

        // An incident condition run as the incident system runs it: a bool(TextObject) lambda on its closure singleton (<>9), an instance lambda
        // of the behaviour itself on the live behaviour (acc4 triage fails-7: incident_solider_is_wanted_criminal and incident_hammer_of_the_sun
        // register ldarg.0; ldftn instance ...b__25_n), else a static bool(TextObject); anything else is not run (its signature recorded).
        private static object Invoke(MethodInfo m)
        {
            try
            {
                var ps = m.GetParameters();
                if (m.ReturnType != typeof(bool) || ps.Length != 1 || ps[0].ParameterType != typeof(TextObject)) return "not run: " + m.ReturnType.Name + "(" + String.Join(",", ps.Select(p => p.ParameterType.Name)) + ")";
                object target = null;
                if (!m.IsStatic)
                {
                    if (typeof(CampaignBehaviorBase).IsAssignableFrom(m.DeclaringType))
                    {
                        target = Campaign.Current == null ? null : Campaign.Current.GetCampaignBehaviors<CampaignBehaviorBase>().FirstOrDefault(b => m.DeclaringType.IsInstanceOfType(b));
                        if (target == null) return "not run: no live " + m.DeclaringType.Name + " behaviour";
                    }
                    else
                    {
                        var singleton = m.DeclaringType.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        target = singleton == null ? null : singleton.GetValue(null);
                        if (target == null) return "not run: no closure singleton";
                    }
                }
                return m.Invoke(target, new object[] { new TextObject("") });
            }
            catch (TargetInvocationException e) { return "threw " + e.InnerException.GetType().Name + ": " + e.InnerException.Message; }
            catch (Exception e) { return "threw " + e.GetType().Name + ": " + e.Message; }
        }

        // A map conversation: a lord party at peace with the player beside it, the installed party encounter (its meeting opens the map
        // conversation), a screenshot while it is open, then left as the player leaves it: the conversation ended, the map conversation over,
        // then the encounter (never PlayerEncounter.Finish alone).
        private static IEnumerator Conversation(Acc a, Dictionary<string, object> row)
        {
            var main = MobileParty.MainParty;
            var lord = MobileParty.All.Where(p => p.IsActive && p.IsLordParty && p.LeaderHero != null && p.LeaderHero.IsActive && p.MapEvent == null && p.Army == null && p.CurrentSettlement == null && p.AttachedTo == null
                    && !p.IsCurrentlyAtSea && p.MapFaction != null && !p.MapFaction.IsAtWarWith(main.MapFaction) && p.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal))
                .OrderBy(p => C.Distance(p.Position.ToVec2(), main.Position.ToVec2())).FirstOrDefault();
            if (lord == null) { Blocked(row, "no lord party at peace with the player on the map"); yield break; }
            var lordHome = lord.Position;
            row["lord"] = lord.StringId; row["hero"] = lord.LeaderHero.StringId;
            try
            {
                // acc4 triage hang-1 H2: the lord's point without the native path search (same navmesh island as the player).
                a.Context.Stage("map2_acceptance:w1.conversation:findPoint");
                var point = LandMissionsProbe.FindPointNear(main.Position, 1.5f, 0.5f);
                a.Context.Stage("map2_acceptance:w1.conversation:placeLord");
                lord.Position = point; lord.SetMoveModeHold();
                yield return C.Frames(5);
                a.Context.Stage("map2_acceptance:w1.conversation:startEncounter");
                EncounterManager.StartPartyEncounter(main.Party, lord.Party);
                double start = a.Context.Seconds;
                var states = new List<object>();
                while (a.Context.Seconds - start < 20)
                {
                    var map = Game.Current.GameStateManager.ActiveState as MapState;
                    bool open = Campaign.Current.ConversationManager.IsConversationInProgress && map != null && map.MapConversationActive;
                    if (states.Count < 40) states.Add(TwoWorldMatrixProbe.TimeState(a.Context.Seconds));
                    if (open) break;
                    yield return null;
                }
                row["states"] = states;
                var mapState = Game.Current.GameStateManager.ActiveState as MapState;
                row["conversationInProgress"] = Campaign.Current.ConversationManager.IsConversationInProgress; row["mapConversationActive"] = mapState != null && mapState.MapConversationActive;
                row["conversationHero"] = Hero.OneToOneConversationHero == null ? null : Hero.OneToOneConversationHero.StringId;
                yield return C.Frames(60);
                Shot(a, row, "map-conversation", new Dictionary<string, object> { ["lord"] = lord.StringId });
            }
            finally
            {
                TwoWorldMatrixProbe.ClearEncounter(row);
                if (lord.IsActive && lord.MapEvent == null && lord.CurrentSettlement == null) lord.Position = lordHome;
            }
            yield return LandMissionsProbe.Idle(a.Land, 30);
            row["afterIdle"] = LandMissionsProbe.NotIdle();
        }

        // ---------------------------------------------------------------- battles and settlements

        private static IEnumerator LandCase(Acc a, Dictionary<string, object> row, string name, Func<Dictionary<string, object>, IEnumerator> body)
        {
            yield return LandMissionsProbe.Case(a.Land, name, body);
            row["case"] = a.Land.Cases.ContainsKey(name) ? a.Land.Cases[name] : null;
            var c = row["case"] as Dictionary<string, object>;
            var failures = c == null ? null : c["failures"] as List<string>;
            row["caseFailures"] = failures;
            if (failures != null && failures.Count > 0) row["status"] = "done";   // the case's own failures are the verdict's (the summarizer reads them)
        }

        // A field battle on a face type: the most interior free face of the type at least 3.5 units from any settlement, with no water cell at it
        // or 3 units round it (acc4 triage hang-1: away from rivers, bridges and decks), preferring a point whose battle cell the resolver
        // classes as the type's own biomes' battle class (notrun-3: the cell's centre biome decides the scene; recorded when it falls back).
        private static IEnumerator FaceBattle(Acc a, Dictionary<string, object> row, string type)
        {
            yield return EnsureFaces(a);
            EnsureBiomes(a);
            var t = EnsureCells(a);
            var wanted = a.Table.Values.Where(b => Equals(b["terrainType"], type) && b.ContainsKey("battleClass")).Select(b => Convert.ToString(b["battleClass"], CultureInfo.InvariantCulture)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            row["preferredClasses"] = wanted;
            List<C.FacePoint> points;
            C.FacePoint p = null, first = null; string cls = null, biome = null; int tried = 0;
            if (a.Faces.TryGetValue(type, out points))
                foreach (var q in C.FreeLands(points, a.World.Prefix, 3.5, f => t == null || Map2AcceptancePlan.DryAround((x, y) => WaterCell(t, x, y), f.X, f.Y, 3)))
                {
                    if (first == null) first = q;
                    string at; string c = LandMissionsProbe.BattleClassAt(new Vec2((float)q.X, (float)q.Y), out at);
                    if (wanted.Contains(c)) { p = q; cls = c; biome = at; break; }
                    if (++tried >= 40) break;
                }
            row["classMatched"] = p != null;
            if (p == null && first != null) { p = first; cls = LandMissionsProbe.BattleClassAt(new Vec2((float)p.X, (float)p.Y), out biome); row["fallback"] = "no dry free " + type + " face whose battle cell is of class " + String.Join("/", wanted) + " among " + tried; }
            if (p == null) { Blocked(row, "no free " + type + " face away from water on this world (face types present: " + String.Join(", ", a.Faces.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ")"); yield break; }
            row["at"] = new[] { p.X, p.Y }; row["face"] = type; row["cellClass"] = cls; row["cellBiome"] = biome;
            var logical = PeriodicWorld.Current == null ? new[] { p.X, p.Y } : PeriodicWorld.Current.Coordinates.ToLogical(p.X, p.Y);
            row["biome"] = a.BiomeAt(logical[0], logical[1]);
            yield return LandCase(a, row, "field." + type.ToLowerInvariant(), r => LandMissionsProbe.FieldAt(a.Land, r, new Vec2((float)p.X, (float)p.Y), a.Culture));
        }

        // A field battle beside a non-port castle (else village): a free land point 2 to 3 units from it (MMR-254 narrowed the settlement ring
        // to ports, so the battle takes its ground's class).
        private static IEnumerator SettlementBattle(Acc a, Dictionary<string, object> row)
        {
            EnsureBiomes(a);
            var cells = EnsureCells(a);
            var all = Settlement.All.Where(s => s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)).ToList();
            foreach (var s in all.Where(s => (s.IsCastle || s.IsVillage) && !s.HasPort).OrderBy(s => s.IsCastle ? 0 : 1).ThenBy(s => s.StringId, StringComparer.Ordinal))
            {
                var centre = s.Position.ToVec2();
                for (int k = 0; k < 12; k++)
                {
                    double angle = 2 * Math.PI * k / 12;
                    var q = new Vec2((float)(centre.X + 2.5 * Math.Cos(angle)), (float)(centre.Y + 2.5 * Math.Sin(angle)));
                    var c = new CampaignVec2(q, true);
                    if (!c.IsValid() || GeneratedSpawnAssertions.Problem(c, MobileParty.NavigationType.Default) != null) continue;
                    if (cells != null && !Map2AcceptancePlan.DryAround((x, y) => WaterCell(cells, x, y), q.X, q.Y, 2)) continue;
                    if (all.Any(o => o != s && Math.Min(C.Distance(o.Position.ToVec2(), q), C.Distance(o.GatePosition.ToVec2(), q)) < 2)) continue;
                    if (C.Distance(s.GatePosition.ToVec2(), q) < 1) continue;
                    var logical = PeriodicWorld.Current == null ? new double[] { q.X, q.Y } : PeriodicWorld.Current.Coordinates.ToLogical(q.X, q.Y);
                    row["settlement"] = s.StringId; row["kind"] = s.IsCastle ? "castle" : "village"; row["at"] = C.P(q); row["biome"] = a.BiomeAt(logical[0], logical[1]); row["face"] = C.TerrainAt(q);
                    yield return LandCase(a, row, "field.beside", r => LandMissionsProbe.FieldAt(a.Land, r, q, a.Culture));
                    yield break;
                }
            }
            Blocked(row, "no free land point beside a non-port castle or village");
        }

        // MMR-52's siege case, with a screenshot of the besieged castle on the map (the siege state on the map): once the camp is prepared, else
        // as LandMissionsProbe.SiegeHook says (an unprepared attempt's standing siege, a retry's camp as soon as it is up).
        private static IEnumerator Siege(Acc a, Dictionary<string, object> row)
        {
            LandMissionsProbe.SiegeHook = (target, setup) => SiegeShot(a, row, target, setup);
            try { yield return LandCase(a, row, "siege", r => LandMissionsProbe.Siege(a.Land, r)); }
            finally { LandMissionsProbe.SiegeHook = null; }
        }

        private static IEnumerator SiegeShot(Acc a, Dictionary<string, object> row, Settlement target, Dictionary<string, object> setup)
        {
            setup["siegeShot"] = "aimed";
            yield return Look(a, row, "siege-map", target.Position.ToVec2(), 16, 0.5f, new Dictionary<string, object> { ["settlement"] = target.StringId, ["wall"] = target.Town == null ? -1 : target.Town.GetWallLevel(),
                ["siegeEvent"] = target.SiegeEvent != null, ["prepared"] = target.SiegeEvent != null && target.SiegeEvent.BesiegerCamp != null && target.SiegeEvent.BesiegerCamp.IsPreparationComplete }, false);
            setup["siegeShot"] = "taken";
        }

        // The ruins encyclopedia page: registered, its rows against the plan's ruins, opened through the encyclopedia's own link (a screenshot),
        // closed again.
        private static IEnumerator RuinsPage(Acc a, Dictionary<string, object> row)
        {
            row["registered"] = RuinsEncyclopediaPage.Registered; row["lastError"] = RuinsEncyclopediaPage.LastError;
            var manager = Campaign.Current.EncyclopediaManager;
            var page = manager.GetEncyclopediaPages().FirstOrDefault(p => p.GetStringID() == "EncyclopediaRuin");
            row["pageFound"] = page != null;
            var plan = World.MapDressingRuntime.CurrentPlan();
            row["planRuins"] = plan == null ? null : (object)plan.Ruins.Count;
            if (page != null)
            {
                Try(row, "rows", () => page.GetListItems().Count());
                Try(row, "rowNames", () => page.GetListItems().Select(i => (object)i.Name).Take(200).ToList());
            }
            Try(row, "open", () => { manager.GoToLink("ListPage", RuinsEncyclopediaPage.Identifier); return "GoToLink(ListPage, " + RuinsEncyclopediaPage.Identifier + ")"; });
            yield return C.Frames(90);
            row["topScreen"] = TaleWorlds.ScreenSystem.ScreenManager.TopScreen == null ? null : TaleWorlds.ScreenSystem.ScreenManager.TopScreen.GetType().FullName;
            row["encyclopediaOpen"] = C.EncyclopediaOpen();
            Shot(a, row, "ruins-page", null);
            Try(row, "closed", () => CloseEncyclopedia());
            yield return C.Frames(30);
            // An encyclopedia left open keeps the map state disabled (time frozen): closed again through MapScreen's view, and recorded.
            if (C.EncyclopediaOpen()) { row["closedAgain"] = C.CloseEncyclopediaIfOpen(); yield return C.Frames(30); }
            row["openAfterClose"] = C.EncyclopediaOpen();
        }

        // MapScreen's encyclopedia view closed through its own CloseEncyclopedia.
        private static object CloseEncyclopedia()
        {
            var screen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
            for (var t = screen == null ? null : screen.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.FieldType.Name.Contains("Encyclopedia")).Select(f => f.GetValue(screen))
                    .Concat(t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(p => p.PropertyType.Name.Contains("Encyclopedia") && p.GetIndexParameters().Length == 0).Select(p => { try { return p.GetValue(screen); } catch (Exception) { return null; } })))
                {
                    if (m == null) continue;
                    var close = AccessTools.Method(m.GetType(), "CloseEncyclopedia");
                    if (close != null && close.GetParameters().Length == 0) { close.Invoke(m, new object[0]); return m.GetType().FullName + ".CloseEncyclopedia"; }
                }
            return "no encyclopedia view found on " + (screen == null ? "no screen" : screen.GetType().FullName);
        }

        // ---------------------------------------------------------------- seasons

        // MMR-256's live check: the season run to its first extreme and then to the other (at most 90 days each, sheltered, grain); at each
        // extreme the same cameras over a cold region (cold tundra, else boreal forest, else tundra), its mirror across the equator, a warm
        // region (desert, else grassland) and land ice; the gameplay weather event at those places each day; the winds by latitude. acc4
        // triage notrun-2: once an extreme holds, time runs on to midday (Map2AcceptancePlan.Midday) while it still holds, and each shot waits
        // up to 60 s for the scene to settle (no shader compiling); hour and settled go with each shot. notrun-6: each place's weather-node
        // snow byte (the map's _snowAndRainData, as MMR-256 wrote it) and its yearly Snowy share, for the summarizer's expected share.
        private static IEnumerator Season(Acc a, Dictionary<string, object> row)
        {
            EnsureBiomes(a);
            var scene = C.MapScene();
            Func<double> season = () => { try { return TaleWorlds.MountAndBlade.MBMapScene.GetSeasonTimeFactor(scene); } catch (Exception) { return double.NaN; } };
            var places = new Dictionary<string, Vec2>(StringComparer.Ordinal);
            Map2AcceptancePlan.Spot spot;
            foreach (var b in new[] { "cold_tundra", "boreal_forest", "tundra" }) if (a.Spots.TryGetValue(b, out spot)) { places["cold"] = a.World.Physical(spot.X, spot.Y); places["coldMirror"] = a.World.Physical(spot.X, a.World.Height - spot.Y); row["coldBiome"] = b; break; }
            foreach (var b in new[] { "desert", "grassland" }) if (a.Spots.TryGetValue(b, out spot)) { places["warm"] = a.World.Physical(spot.X, spot.Y); row["warmBiome"] = b; break; }
            if (a.Spots.TryGetValue("land_ice", out spot)) places["ice"] = a.World.Physical(spot.X, spot.Y);
            row["places"] = places.ToDictionary(k => k.Key, k => (object)C.P(k.Value));
            if (!places.ContainsKey("cold") && !places.ContainsKey("ice")) row["note"] = "no cold region or land ice on this world";
            var bytes = new Dictionary<string, object>(); var shares = new Dictionary<string, object>();
            foreach (var place in places) { int b = SnowByteAt(place.Value); if (b >= 0) { bytes[place.Key] = b; shares[place.Key] = Math.Round(Map2AcceptancePlan.SnowyShare(b), 4); } }
            row["snowBytes"] = bytes; row["snowyShareExpected"] = shares;
            var weather = Campaign.Current.Models.MapWeatherModel;
            var days = new List<object>(); row["days"] = days;
            // Each place's weather event (Clear, LightRain, HeavyRain, Snowy, Blizzard, Storm: the gameplay state) under its name; the terrain
            // effect (1.4.8 has only Default and Wet: every rain or snow event is Wet) beside it under "effect".
            Action<int> perDay = day =>
            {
                var r = new Dictionary<string, object> { ["day"] = day, ["season"] = C.F(season()), ["threshold"] = C.F(GeneratedMapAtmosphere.LastThreshold) };
                var effects = new Dictionary<string, object>(); r["effect"] = effects;
                foreach (var p in places) { r[p.Key] = SafeEvent(weather, p.Value); effects[p.Key] = SafeEffect(weather, p.Value); }
                if (days.Count < 400) days.Add(r);
            };
            var winds = new List<object>();
            for (int k = 1; k < 10; k++)
            {
                var p = a.World.Physical(a.World.Width / 2, a.World.Height * k / 10.0);
                try { var w = weather.GetWindForPosition(new CampaignVec2(p, false)); winds.Add(new object[] { k / 10.0, w.X, w.Y }); } catch (Exception e) { winds.Add(new object[] { k / 10.0, e.GetType().Name }); }
            }
            row["winds"] = winds;
            row["start"] = new Dictionary<string, object> { ["season"] = C.F(season()), ["day"] = CampaignTime.Now.ToDays, ["support"] = C.Support("climateVisuals") };
            var extremes = new List<object>(); row["extremes"] = extremes;
            string done = null;
            for (int leg = 0; leg < 2; leg++)
            {
                var advance = new Dictionary<string, object>();
                string want = done;
                yield return C.AdvanceDays(a.Context, advance, 90, () => { var e = Map2AcceptancePlan.Extreme(season()); return e != null && e != want; }, perDay);
                string reached = Map2AcceptancePlan.Extreme(season());
                var r = new Dictionary<string, object> { ["leg"] = leg, ["extreme"] = reached, ["season"] = C.F(season()), ["day"] = CampaignTime.Now.ToDays, ["advance"] = advance,
                    ["atmosphere"] = new Dictionary<string, object> { ["active"] = GeneratedMapAtmosphere.Active, ["threshold"] = C.F(GeneratedMapAtmosphere.LastThreshold), ["ticks"] = GeneratedMapAtmosphere.Ticks, ["lastError"] = GeneratedMapAtmosphere.LastError } };
                extremes.Add(r);
                if (reached == null || reached == done) { r["note"] = "no new extreme within 90 days"; break; }
                // On to midday while the extreme holds (each window lasts about 5.9 days), at most 2 days.
                var midday = new Dictionary<string, object>(); r["middayAdvance"] = midday;
                if (!Map2AcceptancePlan.Midday(CampaignTime.Now.CurrentHourInDay))
                    yield return C.AdvanceDays(a.Context, midday, 2, () => Map2AcceptancePlan.Midday(CampaignTime.Now.CurrentHourInDay) || Map2AcceptancePlan.Extreme(season()) != reached, perDay);
                r["hour"] = Math.Round(CampaignTime.Now.CurrentHourInDay, 2); r["extremeAtShots"] = Map2AcceptancePlan.Extreme(season()); r["seasonAtShots"] = C.F(season());
                foreach (var p in places)
                    yield return Look(a, row, "season-" + reached + "-" + p.Key, p.Value, 60, 0, new Dictionary<string, object> { ["place"] = p.Key, ["extreme"] = reached, ["season"] = C.F(season()),
                        ["event"] = SafeEvent(weather, p.Value), ["effect"] = SafeEffect(weather, p.Value), ["snowByte"] = bytes.ContainsKey(p.Key) ? bytes[p.Key] : null }, true, 60);
                done = reached;
            }
            row["support"] = C.Support("climateVisuals");
        }

        // The weather node's snow byte at a physical point: SandBox.MapScene's _snowAndRainData (32 x 32 (snow, rain) pairs, col = floor(x /
        // extent * 32), row likewise, clamped; GeneratedClimate's layout), -1 when it cannot be read.
        private static int SnowByteAt(Vec2 p)
        {
            try
            {
                var map = Campaign.Current.MapSceneWrapper;
                var data = AccessTools.Field(map.GetType(), "_snowAndRainData").GetValue(map) as byte[];
                var size = (Vec2)AccessTools.Field(map.GetType(), "_terrainSize").GetValue(map);
                if (data == null || data.Length < 2 || !(size.X > 0)) return -1;
                int dim = (int)Math.Round(Math.Sqrt(data.Length / 2.0));
                int col = Math.Max(0, Math.Min(dim - 1, (int)Math.Floor(p.X / size.X * dim))), row = Math.Max(0, Math.Min(dim - 1, (int)Math.Floor(p.Y / size.Y * dim)));
                return data[2 * (row * dim + col)];
            }
            catch (Exception) { return -1; }
        }

        private static string SafeEvent(TaleWorlds.CampaignSystem.ComponentInterfaces.MapWeatherModel model, Vec2 p) { try { return model.GetWeatherEventInPosition(p).ToString(); } catch (Exception e) { return e.GetType().Name; } }
        private static string SafeEffect(TaleWorlds.CampaignSystem.ComponentInterfaces.MapWeatherModel model, Vec2 p) { try { return model.GetWeatherEffectOnTerrainForPosition(p).ToString(); } catch (Exception e) { return e.GetType().Name; } }

        // ---------------------------------------------------------------- the fire cast

        // MMR-261's debug cast at Nerdun (castAt=, castSchool=fire): the console command's own code, the background fwg apply (a counted
        // producer_apply process), the outcome on the overlay (live cells, retyped cells, the digest), the face types of the retyped cells
        // before and after, screenshots before ("cast-before"), right after the change ("cast-after"), mid-reveal ("cast-mid") and fully grown
        // ("cast-full"; rev5 c29: MMR-288's veins grow out from the capital over the reveal time, MagicGroundRuntime.Hours; without it the
        // policy default, 24 h and 48 h), MMR-289's features of the cast (c30). No battle here (acc4 triage hang-2): the zone's battle is
        // w1.battle.mutated, from the cast save, in the risky tail. "after" is the overlay at the step's end, what w1.saveCast saves.
        private static IEnumerator Cast(Acc a, Dictionary<string, object> row)
        {
            string at = a.Context.Str("castAt", "Nerdun"), school = a.Context.Str("castSchool", "fire");
            var b = WorldMutationBehavior.Current();
            if (b == null) { Blocked(row, "no live mutation on this world (WorldMutationBehavior not running)"); yield break; }
            var site = Settlement.All.FirstOrDefault(s => s.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal) && (s.StringId == at || String.Equals(s.Name.ToString(), at, StringComparison.OrdinalIgnoreCase)));
            if (site == null) { Blocked(row, "no settlement " + at + " on this world"); yield break; }
            row["site"] = site.StringId; row["siteName"] = site.Name.ToString(); row["school"] = school;
            var before = World.MagicGroundRuntime.Snapshot;
            row["before"] = before == null ? null : new Dictionary<string, object> { ["liveCells"] = before.LiveCells.Count(), ["retypedCells"] = before.RetypedCells.Length, ["digest"] = before.Digest };
            var starts = GenerationProbe.ProcessStarts.ToDictionary(k => k.Key, k => k.Value);
            yield return Look(a, row, "cast-before", site.Position.ToVec2(), 40, 0, new Dictionary<string, object> { ["settlement"] = site.StringId });
            var args = new List<string> { school, site.StringId };
            if (a.Context.Has("castStrength")) args.Add(a.Context.Num("castStrength", 10).ToString(CultureInfo.InvariantCulture));
            string reply = MutationCommands.Cast(args);
            row["reply"] = reply; row["producer"] = b.Model.State.Producer;
            if (reply == null || !reply.StartsWith("Edit:", StringComparison.Ordinal)) { row["note"] = "the cast made no edit: " + reply; yield break; }
            double start = a.Context.Seconds;
            DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
            MobileParty.MainParty.IgnoreByOtherPartiesTill(CampaignTime.Now + CampaignTime.Days(30));
            var pumps = new List<object>();
            while (a.Context.Seconds - start < 300)
            {
                if (b.LastChange != null) break;
                if (b.Model.State.Pending.Count == 0 && !b.Runner.Busy && a.Context.Seconds - start > 10 && b.Model.LastError != null) break;
                if (b.LastPump != null && (pumps.Count == 0 || !Equals(pumps[pumps.Count - 1], b.LastPump))) pumps.Add(b.LastPump);
                if (PlayerEncounter.Current != null) TwoWorldMatrixProbe.ClearEncounter(row);
                if (Campaign.Current.TimeControlMode == CampaignTimeControlMode.Stop) DiagnosticSandboxBootstrap.Run(Campaign.Current, CampaignTimeControlMode.UnstoppablePlay);
                yield return null;
            }
            RuntimeMissionProbe.Pause();
            row["waitSeconds"] = a.Context.Seconds - start; row["pumps"] = pumps; row["lastError"] = b.Model.LastError; row["lastChange"] = b.LastChange;
            row["processStarts"] = GenerationProbe.ProcessStarts.ToDictionary(k => k.Key, k => (object)(k.Value - (starts.ContainsKey(k.Key) ? starts[k.Key] : 0)));
            row["state"] = new Dictionary<string, object> { ["edits"] = b.Model.State.Edits.Count, ["cells"] = b.Model.State.Cells.Count, ["nodes"] = b.Model.State.Nodes.Count, ["pending"] = b.Model.State.Pending.Count };
            var after = World.MagicGroundRuntime.Snapshot;
            row["afterChange"] = row["after"] = after == null ? null : new Dictionary<string, object> { ["liveCells"] = after.LiveCells.Count(), ["retypedCells"] = after.RetypedCells.Length, ["digest"] = after.Digest };
            if (after == null || after.RetypedCells.Length == 0) { row["note"] = "no cell retyped"; yield return Look(a, row, "cast-after", site.Position.ToVec2(), 40, 0, null); yield break; }
            // The retyped cells: their centres, the type the overlay gives and the face type the game reads there now.
            var cells = after.Source == null ? null : after.Source.Cells; var faces = new List<object>();
            if (cells == null) { row["note"] = "the overlay has no travel cells"; yield break; }
            foreach (int idx in after.RetypedCells.Take(40))
            {
                int r = idx / cells.Cols, c = idx % cells.Cols;
                var p = new Vec2((float)((c + 0.5 + cells.ColumnOffset) * cells.Spacing), (float)((r + 0.5 + cells.RowOffset) * cells.Spacing));
                faces.Add(new object[] { idx, p.X, p.Y, ((TerrainType)after.CellTypes[idx]).ToString(), C.TerrainAt(p) });
            }
            row["retyped"] = faces;
            row["landSharesBefore"] = World.MagicGroundRuntime.LastChange == null ? null : World.MagicGroundRuntime.LastChange.SharesBefore;
            row["landSharesAfter"] = World.MagicGroundRuntime.LastChange == null ? null : World.MagicGroundRuntime.LastChange.SharesAfter;
            row["battleMap"] = C.Support("magicGround");
            yield return Look(a, row, "cast-after", site.Position.ToVec2(), 40, 0, null);
            row["battleCell"] = ZoneBattleCell(after);
            // The spread: the reveal clock and the live stamps' reveal times (MMR-288), then the mid-reveal and the fully grown shots.
            var reveal = new Dictionary<string, object>(); row["reveal"] = reveal;
            double now = CampaignTime.Now.ToHours, full = now + 48;
            var runtime = typeof(World.MagicGroundRuntime);
            bool clock = runtime.GetProperty("Hours", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null;
            reveal["available"] = clock;
            try
            {
                var graph = clock ? Get(runtime, "Veins") : null;
                var times = graph == null ? new List<double>() : ((Get(graph, "Stamps") as IEnumerable) ?? new object[0]).Cast<object>().Where(x => Flag(x, "Live")).Select(x => Number(x, "RevealAt")).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
                reveal["clockAtChange"] = clock ? Get(runtime, "Hours") : null; reveal["liveStamps"] = times.Count;
                if (times.Count > 0) { reveal["firstAt"] = times.Min(); reveal["lastAt"] = times.Max(); full = Math.Min(now + 96, Math.Max(now, times.Max())); reveal["capped"] = times.Max() > now + 96; }
            }
            catch (Exception e) { reveal["error"] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            if (!clock) reveal["note"] = "MagicGroundRuntime.Hours is not in this build (MMR-288 not merged): the policy default of 48 h is used";
            double mid = now + (full - now) / 2;
            reveal["changeAt"] = now; reveal["midAt"] = mid; reveal["fullAt"] = full;
            var hours = new List<object>(); reveal["hours"] = hours;
            foreach (var leg in new[] { new KeyValuePair<string, double>("cast-mid", mid), new KeyValuePair<string, double>("cast-full", full + 1) })
            {
                var advance = new Dictionary<string, object>(); reveal[leg.Key] = advance;
                double target = leg.Value;
                if (CampaignTime.Now.ToHours < target) yield return C.AdvanceDays(a.Context, advance, Math.Max(0.05, (target - CampaignTime.Now.ToHours) / 24 + 0.05), () => CampaignTime.Now.ToHours >= target, null);
                hours.Add(Math.Round(CampaignTime.Now.ToHours - now, 2));
                yield return Look(a, row, leg.Key, site.Position.ToVec2(), 40, 0, new Dictionary<string, object> { ["hoursSinceChange"] = Math.Round(CampaignTime.Now.ToHours - now, 2), ["clock"] = clock ? Get(runtime, "Hours") : null });
            }
            row["features"] = Features(a);
            var end = World.MagicGroundRuntime.Snapshot;
            row["after"] = end == null ? null : new Dictionary<string, object> { ["liveCells"] = end.LiveCells.Count(), ["retypedCells"] = end.RetypedCells.Length, ["digest"] = end.Digest };
        }

        // The first retyped cell whose centre is free land (valid for a party, 2.5 units from every settlement, dry 2 units round): the zone
        // battle's cell, as [cell, x, y, overlay type, face type]; null when none.
        private static object[] ZoneBattleCell(MagicSnapshot snapshot)
        {
            var cells = snapshot == null || snapshot.Source == null ? null : snapshot.Source.Cells;
            if (cells == null) return null;
            foreach (int idx in snapshot.RetypedCells)
            {
                var p = CellCentre(cells, idx);
                var cv = new CampaignVec2(p, true);
                if (!cv.IsValid() || GeneratedSpawnAssertions.Problem(cv, MobileParty.NavigationType.Default) != null || Settlement.All.Any(s => C.Distance(s.Position.ToVec2(), p) < 2.5f)) continue;
                if (!Map2AcceptancePlan.DryAround((x, y) => WaterCell(cells, x, y), p.X, p.Y, 2)) continue;
                return new object[] { idx, p.X, p.Y, ((TerrainType)snapshot.CellTypes[idx]).ToString(), C.TerrainAt(p) };
            }
            return null;
        }

        // The zone battle (rev5 hang-2 order: in the risky tail): the cast save loaded again (the soak's daily batch keeps mutating the world, so
        // not on the soak's state), the first free retyped cell of its overlay (as the cast found it), MMR-52's field battle there; its class
        // against the remapped face type is the summarizer's. The land cases keep reloading the soak's save after it.
        private static IEnumerator MutatedBattle(Acc a, Dictionary<string, object> row)
        {
            string save = a.Context.Outputs.ContainsKey("save_w1_cast") ? (string)a.Context.Outputs["save_w1_cast"] : a.Context.Str("castSave", null);
            if (save == null) { Blocked(row, "the cast save was not written (w1.saveCast) and castSave= was not given"); yield break; }
            row["save"] = save;
            string keep = a.Land.Save;
            var load = new Dictionary<string, object>(); row["load"] = load;
            a.Land.Save = save;
            try { yield return LandMissionsProbe.LoadDesignated(a.Land, load); }
            finally { a.Land.Save = keep; }
            a.Land.Reloads++;
            if (!Equals(load["loaded"], true)) { Blocked(row, "the cast save " + save + " did not load"); yield break; }
            LandMissionsProbe.Setup(a.Land); Bind(a, a.World == null ? "A" : a.World.Key);
            var snapshot = World.MagicGroundRuntime.Snapshot;
            row["overlay"] = snapshot == null ? null : new Dictionary<string, object> { ["liveCells"] = snapshot.LiveCells.Count(), ["retypedCells"] = snapshot.RetypedCells.Length, ["digest"] = snapshot.Digest };
            var cell = ZoneBattleCell(snapshot);
            if (cell == null) { Blocked(row, "no free land at a retyped cell centre of the cast save's overlay"); yield break; }
            row["battleCell"] = cell;
            var inside = new Vec2((float)cell[1], (float)cell[2]);
            yield return LandCase(a, row, "field.mutated", r => LandMissionsProbe.FieldAt(a.Land, r, inside, a.Culture));
        }

        // ---------------------------------------------------------------- save, continue, soak

        // The game ended, then the cast save continued from the main menu (Continue): no generator process at the load, the navigation caches
        // reused (the loader's forecast and the map's acquisition), the mutation persisted (the overlay's digest and retyped cells as saved).
        private static IEnumerator ContinueStep(Acc a, Dictionary<string, object> row)
        {
            var save = a.Context.Outputs.ContainsKey("save_w1_cast") ? (string)a.Context.Outputs["save_w1_cast"] : null;
            if (save == null) { Blocked(row, "the cast save was not written"); yield break; }
            var castRow = a.Steps.ContainsKey("w1.cast") ? (Dictionary<string, object>)a.Steps["w1.cast"] : null;
            var castAfter = castRow != null && castRow.ContainsKey("after") ? castRow["after"] as Dictionary<string, object> : null;
            row["expected"] = castAfter;
            yield return TwoWorldMatrixProbe.EndGame(a.Context);
            yield return LoadSave(a, row, "A", save, true);
            if (a.World == null) yield break;
            yield return AfterContinueChecks(a, row, castAfter);
        }

        private static IEnumerator AfterContinue(Acc a, Dictionary<string, object> row) { yield return AfterContinueChecks(a, row, null); }

        private static IEnumerator AfterContinueChecks(Acc a, Dictionary<string, object> row, Dictionary<string, object> expected)
        {
            var snapshot = World.MagicGroundRuntime.Snapshot;
            row["overlay"] = snapshot == null ? null : new Dictionary<string, object> { ["liveCells"] = snapshot.LiveCells.Count(), ["retypedCells"] = snapshot.RetypedCells.Length, ["digest"] = snapshot.Digest, ["disabled"] = snapshot.MutationsDisabled };
            if (expected != null && snapshot != null) row["mutationPersisted"] = Equals(expected["digest"], snapshot.Digest) && Equals(expected["retypedCells"], snapshot.RetypedCells.Length);
            var b = WorldMutationBehavior.Current();
            row["mutation"] = b == null ? null : new Dictionary<string, object> { ["edits"] = b.Model.State.Edits.Count, ["cells"] = b.Model.State.Cells.Count, ["disabled"] = b.Model.DisabledReason, ["castRefusal"] = b.Model.CastRefusal() };
            row["forecastNow"] = C.Forecast(a.World.Scene, false);
            // MMR-282's missing-cache warning, without deleting anything: the forecast with this scene's caches hidden from the list.
            row["forecastWithoutCaches"] = C.Forecast(a.World.Scene, true);
            // rev5 c30/c31: MMR-289's features and MMR-290's magic water re-created from the stored mutation state.
            row["features"] = Features(a);
            var water = MagicType("MagicWaterRuntime");
            if (water == null) row["magicWater"] = new Dictionary<string, object> { ["available"] = false, ["reason"] = "MagicWaterRuntime is not in this build (MMR-290 not merged)" };
            else { var w = new Dictionary<string, object> { ["available"] = true }; row["magicWater"] = w; Try(w, "record", () => Call(water, "Record")); }
            yield return Look(a, row, a.World.Key + "-after-continue", MobileParty.MainParty.Position.ToVec2(), 40, 0, null);
        }

        // MMR-247 step 3: soakDays of campaign (sheltered, grain), the kingdom and house changes and mercenary contracts heard as they happen,
        // the census, the economy and the home terrain again.
        private static IEnumerator Soak(Acc a, Dictionary<string, object> row)
        {
            double days = a.Context.Num("soakDays", 30);
            var owner = new object();
            var events = new List<object>(); row["events"] = events; var counts = new Dictionary<string, int>(StringComparer.Ordinal); row["eventCounts"] = counts;
            Action<string, object[]> add = (kind, data) => { int n; counts.TryGetValue(kind, out n); counts[kind] = n + 1; if (events.Count < 600) events.Add(new object[] { Math.Round(CampaignTime.Now.ToDays, 2), kind, data }); };
            Func<MBObjectBase, string> id = o => o == null ? null : o.StringId;
            Action<Action> safe = body => { try { body(); } catch (Exception) { } };
            row["economyBefore"] = C.Economy(a.World.Prefix);
            try
            {
                CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(owner, (clan, from, to, detail, notify) => safe(() => add("clanChangedKingdom", new object[] { id(clan), id(from), id(to), detail.ToString(), clan.IsMinorFaction })));
                CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(owner, k => safe(() => add("kingdomDestroyed", new object[] { id(k) })));
                CampaignEvents.OnClanDestroyedEvent.AddNonSerializedListener(owner, c => safe(() => add("clanDestroyed", new object[] { id(c), c.IsMinorFaction })));
                CampaignEvents.WarDeclared.AddNonSerializedListener(owner, (x, y, detail) => safe(() => add("warDeclared", new object[] { x.StringId, y.StringId, detail.ToString() })));
                CampaignEvents.MakePeace.AddNonSerializedListener(owner, (x, y, detail) => safe(() => add("peace", new object[] { x.StringId, y.StringId, detail.ToString() })));
                CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(owner, (s, open, newOwner, oldOwner, capturer, detail) => safe(() => add("settlementOwner", new object[] { id(s), id(oldOwner == null ? null : oldOwner.Clan), id(newOwner == null ? null : newOwner.Clan), detail.ToString() })));
                CampaignEvents.HeroKilledEvent.AddNonSerializedListener(owner, (victim, killer, detail, notify) => safe(() => { if (victim != null && victim.StringId.StartsWith(a.World.Prefix, StringComparison.Ordinal)) add("heroKilled", new object[] { id(victim), id(victim.Clan), detail.ToString() }); }));
                var advance = new Dictionary<string, object>(); row["advance"] = advance;
                yield return C.AdvanceDays(a.Context, advance, days, null, null);
            }
            finally
            {
                CampaignEvents.OnClanChangedKingdomEvent.ClearListeners(owner); CampaignEvents.KingdomDestroyedEvent.ClearListeners(owner); CampaignEvents.OnClanDestroyedEvent.ClearListeners(owner);
                CampaignEvents.WarDeclared.ClearListeners(owner); CampaignEvents.MakePeace.ClearListeners(owner); CampaignEvents.OnSettlementOwnerChangedEvent.ClearListeners(owner); CampaignEvents.HeroKilledEvent.ClearListeners(owner);
            }
            row["census"] = C.Census(a.World.Prefix);
            row["economyAfter"] = C.Economy(a.World.Prefix);
            row["homeTerrain"] = C.HomeTerrain(a.World.Prefix);
            row["memory"] = C.Memory();
            Shot(a, row, "soak-end", null);
        }
    }
}
