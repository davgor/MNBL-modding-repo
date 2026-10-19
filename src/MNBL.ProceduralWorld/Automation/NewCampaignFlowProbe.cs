using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Generation;
using MNBL.ProceduralWorld.Persistence;
using MNBL.ProceduralWorld.UI;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-46: the New Campaign flow in the running game (new_campaign_flow; seed=<n>, culture=<id>), from the main menu, through the setup
    // screen's own controls (synthetic generic-panel keys through its mapping, or its view model's commands):
    //   1. Confirm generates the seed; the world ready for review shows its preview (the job's compiled world: content id, layout, scene), the
    //      raster as an engine texture (file and raw-bytes modes, screenshots), markers for every settlement and port, the pointed-at place in
    //      full; the panel's measured layout inside the page, also with every text 40 % longer (pseudo-localised);
    //   2. Exit returns to the seed with the world kept (same job, files kept); Confirm shows it again without a second job;
    //   3. Randomize in the preview (New Seed) draws a random seed and starts its job, releasing the reviewed world; committed worlds are
    //      untouched; Exit cancels it;
    //   4. the seed again: the same world (the same raster);
    //   5. Confirm in the preview (Start Campaign) hands the job to the activation (MMR-45): the screen closes, the installed new-game flow runs
    //      (the campaign intro is skipped as its skip does); in character creation the probe chooses a culture, goes on and back (the culture
    //      kept, the world's cultures only), then leaves through the culture stage's Back (the installed PopState): the activation is withdrawn
    //      and rolled back, the setup reopens on the same world (same job, nothing generated again);
    //   6. Start Campaign again; character creation is completed; the activation commits; every preview marker is checked against the live
    //      campaign (position, kind, name, kingdom) and the preview's world, layout and scene against the committed ones.
    // Any failed expectation fails the run. Physical keyboard and controller devices are not driven.
    internal static class NewCampaignFlowProbe
    {
        internal static IEnumerator Run(ProbeContext context)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Body(context));
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                var nested = stack.Peek().Current as IEnumerator;
                if (nested != null) stack.Push(nested); else yield return stack.Peek().Current;
            }
        }

        private static IEnumerator Body(ProbeContext context)
        {
            context.Outputs["scope"] = "MMR-46 New Campaign flow: generate, preview (texture modes, markers, layout, long texts), back keeping the world, reuse without regeneration, New Seed, the same seed again, Start Campaign with player-style character creation, withdrawal back to review, start and commit, preview against the live campaign. Synthetic generic-panel keys through the screen's mapping; physical devices are not driven.";
            uint seed = (uint)context.Int("seed", 42);
            string culture = context.Str("culture", "empire");
            var failures = new List<string>(); context.Outputs["failures"] = failures;
            Action<bool, string> expect = (ok, what) => { if (!ok) failures.Add(what); context.Stage((ok ? "ok: " : "FAIL: ") + what); };
            var coordinator = GenerationRuntime.Coordinator;
            var committedBefore = CommittedWorlds();
            context.Outputs["committedWorldFiles"] = committedBefore.Count;

            // 1. Generate and review.
            Module.CurrentModule.ExecuteInitialStateOptionWithId(WorldSetupScreen.MenuId);
            double start = context.Seconds;
            while ((WorldSetupScreen.Current == null || !WorldSetupScreen.Current.MovieLoaded) && context.Seconds - start < 20) yield return null;
            if (WorldSetupScreen.Current == null) throw new InvalidOperationException("The setup screen did not open.");
            var screen = WorldSetupScreen.Current; var vm = screen.ViewModel; var model = vm.Model;
            var runner = (CoordinatorRunner)typeof(WorldSetupModel).GetField("runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(model);
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            var generate = new Dictionary<string, object>(); context.Outputs["generate"] = generate;
            generate["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            context.Stage("new_campaign_flow:generate");
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
            generate["seconds"] = context.Seconds - start; generate["state"] = model.State.ToString(); generate["error"] = model.Error == null ? null : model.Error.ToString();
            if (model.State != SetupState.Generated) throw new InvalidOperationException("The world was not generated: " + model.State + " " + model.Error);
            var job = runner.LastJob; string jobRoot = model.JobRoot, jobId = job.Id;
            var preview = model.Preview;
            generate["jobId"] = jobId; generate["previewError"] = model.PreviewError;
            expect(preview != null && vm.IsPreviewShown && vm.CanStartCampaign && !vm.IsEditing, "the generated world is shown with its preview and offered for play");
            if (preview == null) throw new InvalidOperationException("No preview: " + model.PreviewError);
            var review = new Dictionary<string, object>(); context.Outputs["review"] = review;
            review["preview"] = preview.Record();
            review["job"] = new Dictionary<string, object> { ["worldContentId"] = job.Get<string>("worldContentId"), ["sceneName"] = job.Get<string>("sceneName"), ["phase"] = job.Phase.ToString() };
            var prepared = job.Get<MNBL.ProceduralWorld.Surface.PreparedSurface>("prepared");
            expect(preview.WorldContentId == job.Get<string>("worldContentId") && preview.SceneName == job.Get<string>("sceneName") && prepared != null && preview.LayoutSha256 == prepared.Settlements.Sha256,
                "the preview is the job's compiled world: its content id, scene and settlement layout");
            review["texture"] = new Dictionary<string, object> { ["mode"] = WorldPreviewTextures.Mode, ["created"] = WorldPreviewTextures.Created, ["error"] = WorldPreviewTextures.LastError, ["current"] = WorldPreviewTextures.Current != null };
            expect(WorldPreviewTextures.Current != null && WorldPreviewTextures.RasterSha256 == preview.RasterSha256 && WorldPreviewTextures.Mode == WorldPreviewTextures.ModeBytes && !vm.Preview.HasTextureError,
                "the preview raster is an engine texture (from its bytes, the default)");
            // North up, as the campaign map shows it: a marker further north (larger y) is higher on the picture.
            var north = preview.Markers.OrderByDescending(m => m.PhysicalY).First(); var south = preview.Markers.OrderBy(m => m.PhysicalY).First();
            review["orientation"] = new Dictionary<string, object> { ["northId"] = north.Id, ["northY"] = north.PhysicalY, ["northV"] = north.V, ["southId"] = south.Id, ["southY"] = south.PhysicalY, ["southV"] = south.V };
            expect(north.V < south.V && preview.Markers.All(m => Math.Abs(m.V - (1 - (m.PhysicalY - preview.OriginY) / preview.LogicalHeight)) < 1e-9), "north is up: the preview's vertical runs against the map's y");
            expect(vm.Preview.Markers.Count == preview.Markers.Count && vm.Preview.Markers.Count(m => m.ShowLabel) == preview.Markers.Count(m => m.Kind == "town"), "every settlement and port is a marker; every town is labelled");
            yield return Frames(40);
            review["bytesScreenshot"] = Shot(context, "preview-bytes");
            review["layout"] = Measure(screen);
            expect(Inside(review["layout"]), "the panel lies inside the page, the map and the buttons inside the panel, and the panel fits a 1080-unit page");
            // The PNG file mode (the fallback), for the record; the default is restored.
            bool fileOk = WorldPreviewTextures.Show(preview, WorldPreviewTextures.ModeFile);
            review["fileMode"] = new Dictionary<string, object> { ["ok"] = fileOk, ["error"] = WorldPreviewTextures.LastError, ["released"] = WorldPreviewTextures.Released };
            yield return Frames(40);
            review["fileScreenshot"] = Shot(context, "preview-file");
            WorldPreviewTextures.Show(preview, WorldPreviewTextures.ModeBytes);
            review["previewFiles"] = Directory.Exists(Path.Combine(SubModule.Store, "previews")) ? Directory.GetFiles(Path.Combine(SubModule.Store, "previews"), "preview-*.png").Length : 0;
            expect(fileOk && (int)review["previewFiles"] <= 1, "the PNG fallback renders too, and only the shown preview's file is kept");
            yield return Frames(10);
            var town = vm.Preview.Markers.First(m => m.IsTown);
            // The prefab binds the marker's hover to these commands (checked offline); here they are called as the widget calls them.
            town.ExecuteHover(); yield return Frames(5);
            review["hover"] = vm.Preview.HoverText; review["hoverName"] = town.FullName;
            expect(vm.Preview.HoverText.StartsWith(town.FullName, StringComparison.Ordinal), "pointing at a place names it in full");
            yield return Frames(20);
            review["hoverScreenshot"] = Shot(context, "preview-hover");
            town.ExecuteHoverEnd();
            // Keyboard and controller: the tab keys walk the towns and castles, each named in full; the kingdoms are listed in full.
            var walked = new List<string>();
            var nextKey = screen.HandleInput(k => k == SetupInput.Next, false).ToString(); yield return Frames(3);
            walked.Add(vm.Preview.HoverText);
            screen.HandleInput(k => k == SetupInput.Next, false); yield return Frames(3); walked.Add(vm.Preview.HoverText);
            var previousKey = screen.HandleInput(k => k == SetupInput.Previous, false).ToString(); yield return Frames(3); walked.Add(vm.Preview.HoverText);
            var selected = vm.Preview.Selected;
            review["places"] = new Dictionary<string, object> { ["next"] = nextKey, ["previous"] = previousKey, ["walked"] = walked, ["count"] = vm.Preview.Places.Count,
                ["selected"] = selected == null ? null : selected.FullName,
                ["previousGlyph"] = vm.Preview.PreviousPlaceKey == null ? null : vm.Preview.PreviousPlaceKey.KeyID, ["nextGlyph"] = vm.Preview.NextPlaceKey == null ? null : vm.Preview.NextPlaceKey.KeyID };
            expect(nextKey == "NextPlace" && previousKey == "PreviousPlace" && walked[0] == walked[2] && walked[0] != walked[1] && selected != null && selected.IsSelected && walked[0].StartsWith(selected.FullName, StringComparison.Ordinal)
                   && vm.Preview.Places.Count == preview.Markers.Count(m => m.Kind == "town" || m.Kind == "castle") && preview.KingdomNames.Keys.All(k => vm.Preview.Places.Any(p => p.Marker.Kingdom == k)),
                "the tab keys select the next and previous town or castle, each named in full with its kingdom, and every kingdom is reached that way");
            yield return Frames(20);
            review["placeScreenshot"] = Shot(context, "preview-place-selected");
            // Every text 40 % longer (a pseudo-localisation), the layout measured again.
            var render = WorldSetupViewModel.Render;
            WorldSetupViewModel.Render = (text, variables) => Longer(render(text, variables));
            vm.RefreshValues(); yield return Frames(40);
            review["longLayout"] = Measure(screen);
            review["longScreenshot"] = Shot(context, "preview-long-texts");
            review["longTexts"] = new Dictionary<string, object> { ["title"] = vm.PreviewTitleText, ["counts"] = vm.Preview.CountsText, ["start"] = vm.StartCampaignText };
            expect(Inside(review["longLayout"]), "with every text 40 % longer the panel still lies inside the page, the map and the buttons inside the panel, and the panel fits a 1080-unit page");
            WorldSetupViewModel.Render = render; vm.RefreshValues(); yield return Frames(10);
            context.Checkpoint();

            // 2. Back keeps the world; Confirm shows it again without a second job.
            var back = new Dictionary<string, object>(); context.Outputs["back"] = back;
            back["exit"] = screen.HandleInput(k => k == SetupInput.Exit, false).ToString();
            yield return Frames(10);
            back["state"] = model.State.ToString(); back["jobKept"] = model.JobRoot == jobRoot && Directory.Exists(jobRoot); back["notice"] = vm.NoticeText;
            back["jobPhase"] = job.Phase.ToString();
            expect(model.State == SetupState.Editing && (bool)back["jobKept"] && job.Phase == JobPhase.ReadyForReview && vm.HasNotice && WorldSetupScreen.Current == screen,
                "Exit in the preview returns to the seed with the world kept (the same job, its files, ready for review)");
            back["screenshot"] = Shot(context, "back-kept");
            int generations = model.Generations;
            back["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            yield return Frames(10);
            back["reused"] = model.Reused; back["generations"] = model.Generations; back["sameJob"] = runner.LastJob == job && coordinator.Current == job;
            expect(model.State == SetupState.Generated && model.Reused && model.Generations == generations && runner.LastJob == job && model.Preview == preview,
                "Confirm with the same seed shows the kept world again: no second job, the same preview");
            context.Checkpoint();

            // 3. New Seed: a random seed, its job started, the reviewed world released, committed worlds untouched; then cancelled.
            var newSeed = new Dictionary<string, object>(); context.Outputs["newSeed"] = newSeed;
            newSeed["randomize"] = screen.HandleInput(k => k == SetupInput.Randomize, false).ToString();
            start = context.Seconds;
            while ((model.JobRoot == null || model.JobRoot == jobRoot) && model.State == SetupState.Generating && context.Seconds - start < 90) yield return null;
            newSeed["seed"] = model.Choice.Seed; newSeed["source"] = model.Choice.SeedSource; newSeed["seedText"] = vm.SeedText;
            start = context.Seconds;
            while (Directory.Exists(jobRoot) && context.Seconds - start < 60) yield return null;
            newSeed["reviewedReleased"] = !Directory.Exists(jobRoot);
            newSeed["committedUnchanged"] = Same(committedBefore, CommittedWorlds());
            expect((string)newSeed["randomize"] == "NewSeed" && model.Choice.Seed != seed && model.Choice.SeedSource == "random" && vm.SeedText == model.Choice.Seed.ToString(CultureInfo.InvariantCulture)
                   && (bool)newSeed["reviewedReleased"] && (bool)newSeed["committedUnchanged"],
                "Randomize in the preview (New Seed) generates a random seed shown in the seed box, releases the reviewed world and touches no committed world");
            yield return Frames(30);
            newSeed["screenshot"] = Shot(context, "new-seed-generating");
            newSeed["exit"] = screen.HandleInput(k => k == SetupInput.Exit, false).ToString();
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 180) yield return null;
            newSeed["state"] = model.State.ToString(); newSeed["category"] = model.Error == null ? null : model.Error.Category.ToString();
            expect(model.State == SetupState.Failed && model.Error != null && model.Error.Category == GenerationErrorCategory.InterruptedGeneration, "Exit cancels the new seed's generation");
            context.Checkpoint();

            // 4. The seed again: the same world.
            var again = new Dictionary<string, object>(); context.Outputs["again"] = again;
            vm.SeedText = seed.ToString(CultureInfo.InvariantCulture); yield return null;
            screen.HandleInput(k => k == SetupInput.Confirm, false);
            start = context.Seconds;
            while (model.State == SetupState.Generating && context.Seconds - start < 1200) yield return null;
            again["seconds"] = context.Seconds - start; again["state"] = model.State.ToString();
            if (model.State != SetupState.Generated || model.Preview == null) throw new InvalidOperationException("The seed did not generate again: " + model.State + " " + model.Error + " " + model.PreviewError);
            again["rasterSha256"] = model.Preview.RasterSha256; again["worldContentId"] = model.Preview.WorldContentId;
            expect(model.Preview.RasterSha256 == preview.RasterSha256 && model.Preview.WorldContentId == preview.WorldContentId && model.Preview.Markers.Count == preview.Markers.Count,
                "the same seed makes the same world (the same content id and preview raster)");
            preview = model.Preview; job = runner.LastJob; jobRoot = model.JobRoot; generations = model.Generations;
            context.Checkpoint();

            // 5. Start Campaign, then leave character creation: withdrawn, back to review on the same world.
            var withdraw = new Dictionary<string, object>(); context.Outputs["withdraw"] = withdraw;
            withdraw["confirm"] = screen.HandleInput(k => k == SetupInput.Confirm, false).ToString();
            expect((string)withdraw["confirm"] == "StartCampaign" && model.State == SetupState.Starting, "Confirm in the preview starts the campaign");
            context.Stage("new_campaign_flow:start1");
            var creation = new CreationDriver(culture, "MNBL Flow");
            start = context.Seconds;
            bool left = false;
            while (context.Seconds - start < 900)
            {
                if (!left && Game.Current != null)
                {
                    string step = creation.StepToWithdraw();
                    if (step != null) context.Stage("new_campaign_flow:" + step);
                    if (creation.Left) { left = true; withdraw["creation"] = creation.Record(); }
                }
                if (model.State != SetupState.Starting && WorldSetupScreen.Current != null && WorldSetupScreen.Current.MovieLoaded) break;
                yield return null;
            }
            withdraw["seconds"] = context.Seconds - start;
            withdraw["handedOff"] = creation.SawHandOff;
            withdraw["state"] = model.State.ToString(); withdraw["withdrawn"] = model.Withdrawn; withdraw["reopened"] = WorldSetupScreen.Reopened;
            var stage1 = runner.LastStage;
            withdraw["instance"] = stage1 == null ? null : stage1.Instance;
            withdraw["error"] = stage1 == null || stage1.Error == null ? null : stage1.Error.GetType().Name + ": " + stage1.Error.Message;
            withdraw["jobPhase"] = job.Phase.ToString(); withdraw["cleanup"] = job.Cleanup.ToList();
            var journal1 = stage1 == null || stage1.Instance == null ? null : new ActivationJournalStore(SubModule.Store).Journal(stage1.Instance);
            withdraw["journal"] = journal1 == null ? null : new Dictionary<string, object> { ["state"] = journal1.State, ["failedAt"] = journal1.FailedAt, ["error"] = journal1.Error, ["rollback"] = journal1.Rollback };
            int bound1 = stage1 == null || stage1.Instance == null ? 0 : SaveBindingRuntime.Store.Saves.List().Select(h => { try { return h.Readable ? WorldBinding.FromHeader(h.Metadata) : null; } catch (Exception) { return null; } }).Count(b => b != null && b.Instance == stage1.Instance);
            withdraw["savesBound"] = bound1;
            withdraw["committedUnchanged"] = Same(committedBefore, CommittedWorlds());
            var creationRecord = creation.Record();
            expect(creation.CulturesAtFirst != null && creation.CulturesAtFirst.Count > 0 && stage1 != null && stage1.Host != null && stage1.Host.Dataset != null
                   && new HashSet<string>(creation.CulturesAtFirst).SetEquals(stage1.Host.Dataset.CreationCultures) && creation.CulturesAfterBack != null && creation.CulturesAfterBack.SequenceEqual(creation.CulturesAtFirst)
                   && creation.SelectedAfterBack == culture,
                "character creation offers only the world's cultures, and going on and back keeps the chosen culture and the same cultures");
            screen = WorldSetupScreen.Current; vm = screen == null ? null : screen.ViewModel;
            expect(left && creation.SawHandOff && WorldSetupScreen.Reopened >= 1 && model.State == SetupState.Generated && model.Withdrawn && screen != null && vm != null && vm.Model == model && WorldSetupScreen.Pending == null && job.Phase == JobPhase.ReadyForReview
                   && model.JobRoot == jobRoot && Directory.Exists(jobRoot) && model.Generations == generations && runner.LastJob == job,
                "Start Campaign closed the setup for the new game; leaving character creation withdraws the start and the setup reopens on the same world ready for review (same job and files, nothing generated again)");
            expect(journal1 != null && journal1.State == ActivationJournalStore.Failed && bound1 == 0 && (bool)withdraw["committedUnchanged"] && Game.Current == null,
                "the withdrawn activation is rolled back: failed journal, no save bound, committed worlds untouched, the main menu");
            if (vm == null) throw new InvalidOperationException("The setup did not reopen after the withdrawal.");
            withdraw["notice"] = vm.NoticeText;
            yield return Frames(30);
            withdraw["screenshot"] = Shot(context, "reopened-after-withdrawal");
            context.Checkpoint();

            // 6. Start again and play: the activation commits; the preview against the live campaign.
            var play = new Dictionary<string, object>(); context.Outputs["play"] = play;
            vm.ExecuteStartCampaign();
            context.Stage("new_campaign_flow:start2");
            var creation2 = new CreationDriver(culture, "MNBL Flow");
            start = context.Seconds;
            bool mapShot = false;
            while (model.State == SetupState.Starting && context.Seconds - start < 3300)
            {
                if (Game.Current != null)
                {
                    string step = creation2.StepToMap();
                    if (step != null) context.Stage("new_campaign_flow:" + step);
                    if (Campaign.Current != null && Game.Current.GameStateManager.ActiveState is MapState)
                    {
                        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                        if (!mapShot) { mapShot = true; play["mapSeconds"] = context.Seconds - start; }
                    }
                }
                yield return null;
            }
            play["seconds"] = context.Seconds - start; play["state"] = model.State.ToString(); play["creation"] = creation2.Record();
            var stage2 = runner.LastStage;
            play["instance"] = stage2 == null ? null : stage2.Instance;
            play["error"] = stage2 == null || stage2.Error == null ? null : stage2.Error.GetType().Name + ": " + stage2.Error.Message;
            var journal2 = stage2 == null || stage2.Instance == null ? null : new ActivationJournalStore(SubModule.Store).Journal(stage2.Instance);
            play["journal"] = journal2 == null ? null : journal2.State;
            play["receipt"] = stage2 != null && stage2.Instance != null && File.Exists(Path.Combine(new ActivationJournalStore(SubModule.Store).InstanceDirectory(stage2.Instance), "receipt.json"));
            play["jobReleased"] = !Directory.Exists(jobRoot);
            expect(model.State == SetupState.Started && stage2 != null && stage2 != stage1 && stage2.Instance != stage1.Instance && journal2 != null && journal2.State == ActivationJournalStore.Committed && (bool)play["receipt"]
                   && WorldSetupScreen.Pending == null && Game.Current != null && (bool)play["jobReleased"] && WorldPreviewTextures.Current == null,
                "Start Campaign again commits the world: a new activation instance, a committed journal with its receipt, the campaign running, the job's files released and the preview texture dropped");
            if (Game.Current != null && Campaign.Current != null)
            {
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
                yield return Frames(60);
                play["mapScreenshot"] = Shot(context, "campaign-map");
                // c1: every marker against the live campaign.
                var compare = Compare(preview);
                play["compare"] = compare;
                expect((int)compare["missing"] == 0 && (int)compare["kindMismatches"] == 0 && (int)compare["positionMismatches"] == 0 && (int)compare["nameMismatches"] == 0 && (int)compare["kingdomMismatches"] == 0 && (int)compare["regionNameMismatches"] == 0 && (int)compare["liveUnmarked"] == 0 && (int)compare["liveSettlementsChecked"] > 0,
                    "every preview marker is a live settlement or port of the committed campaign at the same position, of the same kind, name and kingdom, every live town, castle, village and port of the world is a marker (a harbour village's landing as its harbour marker), and every labelled kingdom has the live kingdom's name");
                var dataset = stage2.Host.Dataset;
                play["identity"] = new Dictionary<string, object> { ["datasetLayoutSha256"] = dataset.LayoutSha256, ["previewLayoutSha256"] = preview.LayoutSha256, ["requestWorldContentId"] = job.Get<string>("worldContentId"),
                    ["previewWorldContentId"] = preview.WorldContentId, ["sceneName"] = job.Get<string>("sceneName"), ["previewScene"] = preview.SceneName,
                    ["liveSettlements"] = Settlement.All.Count(s => s.StringId.StartsWith(dataset.WorldPrefix, StringComparison.Ordinal)) };
                expect(dataset.LayoutSha256 == preview.LayoutSha256 && job.Get<string>("worldContentId") == preview.WorldContentId && job.Get<string>("sceneName") == preview.SceneName,
                    "the committed campaign is the previewed world: the same content id, settlement layout and scene");
                context.Checkpoint();
                MBGameManager.EndGame();
                start = context.Seconds;
                while ((Game.Current != null || MBGameManager.Current != null) && context.Seconds - start < 120) yield return null;
            }
            yield return Frames(30);
            context.Outputs["finalTopScreen"] = ScreenManager.TopScreen == null ? null : ScreenManager.TopScreen.GetType().FullName;
            expect(Game.Current == null && WorldSetupScreen.Current == null && WorldSetupScreen.Pending == null, "back at the main menu with no setup left open or pending");
            context.Outputs["completed"] = true;
            context.Checkpoint();
            if (failures.Count > 0) throw new InvalidOperationException("New campaign flow expectations failed: " + String.Join("; ", failures));
        }

        // Character creation as a player drives it, one action per frame through the installed managers (MMR-48's save_binding drives it too).
        internal sealed class CreationDriver
        {
            private readonly string culture, name;
            private readonly HashSet<string> menus = new HashSet<string>(StringComparer.Ordinal);
            private bool narrativeStarted, wentOn, cameBack;
            private int steps;
            public List<string> CulturesAtFirst, CulturesAfterBack;
            public string SelectedAfterBack, StageAfterCulture;
            public bool Left, SawHandOff, SkippedVideo;
            public List<string> Stages = new List<string>();
            public CreationDriver(string culture, string name) { this.culture = culture; this.name = name; }

            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["cultures"] = CulturesAtFirst, ["culturesAfterBack"] = CulturesAfterBack, ["selectedAfterBack"] = SelectedAfterBack, ["stageAfterCulture"] = StageAfterCulture,
                    ["left"] = Left, ["skippedVideo"] = SkippedVideo, ["stages"] = Stages, ["menus"] = menus.ToList(), ["steps"] = steps };
            }

            private CharacterCreationState Creation()
            {
                if (WorldSetupScreen.Current == null && WorldSetupScreen.Pending != null) SawHandOff = true;
                var active = Game.Current.GameStateManager.ActiveState;
                var video = active as VideoPlaybackState;
                if (video != null) { SkippedVideo = true; video.OnVideoFinished(); return null; }   // as the intro's skip does
                return active as CharacterCreationState;
            }

            private static List<string> Cultures(CharacterCreationContent content) { return content.GetCultures().Select(c => c.StringId).OrderBy(c => c, StringComparer.Ordinal).ToList(); }

            // Choose the culture, go on one stage, come back, then leave through the culture stage's Back.
            public string StepToWithdraw()
            {
                var state = Creation();
                if (state == null || Left) return null;
                var manager = state.CharacterCreationManager; var content = manager.CharacterCreationContent; var stage = manager.CurrentStage;
                if (stage == null) return null;
                if (++steps > 64) throw new InvalidOperationException("Character creation step limit.");
                if (stage is CharacterCreationCultureStage && !wentOn)
                {
                    CulturesAtFirst = Cultures(content);
                    var chosen = content.GetCultures().FirstOrDefault(c => c.StringId == culture) ?? content.GetCultures().First();
                    content.SetSelectedCulture(chosen, manager); content.SetMainCharacterName(name);
                    wentOn = true; manager.NextStage();
                    return "creation_culture_chosen:" + chosen.StringId;
                }
                if (wentOn && !cameBack && !(stage is CharacterCreationCultureStage))
                {
                    StageAfterCulture = stage.GetType().Name;
                    cameBack = true; manager.PreviousStage();
                    return "creation_back_from:" + StageAfterCulture;
                }
                if (cameBack && stage is CharacterCreationCultureStage)
                {
                    CulturesAfterBack = Cultures(content);
                    SelectedAfterBack = content.SelectedCulture == null ? null : content.SelectedCulture.StringId;
                    Left = true;
                    Game.Current.GameStateManager.PopState(0);   // the culture stage's Back (CharacterCreationCultureStageView.PreviousStage)
                    return "creation_left";
                }
                return null;
            }

            // Through every stage to the campaign map.
            public string StepToMap()
            {
                var state = Creation();
                if (state == null) return null;
                var manager = state.CharacterCreationManager; var content = manager.CharacterCreationContent; var stage = manager.CurrentStage;
                if (stage == null) return null;
                if (++steps > 128) throw new InvalidOperationException("Character creation step limit.");
                string label = stage.GetType().Name;
                Stages.Add(label);
                if (stage is CharacterCreationCultureStage)
                {
                    if (CulturesAtFirst == null) CulturesAtFirst = Cultures(content);
                    var chosen = content.GetCultures().FirstOrDefault(c => c.StringId == culture) ?? content.GetCultures().First();
                    content.SetSelectedCulture(chosen, manager); content.SetMainCharacterName(name);
                }
                else if (stage is CharacterCreationNarrativeStage)
                {
                    if (!narrativeStarted) { manager.StartNarrativeStage(); narrativeStarted = true; }
                    var menu = manager.CurrentMenu;
                    if (menu == null || !menus.Add(menu.StringId)) throw new InvalidOperationException("Missing or repeated narrative menu.");
                    var option = manager.GetSuitableNarrativeMenuOptions().FirstOrDefault();
                    if (option == null) throw new InvalidOperationException("No suitable narrative option in " + menu.StringId);
                    manager.OnNarrativeMenuOptionSelected(option);
                    if (manager.TrySwitchToNextMenu()) return "creation_menu:" + menu.StringId;
                }
                else if (stage is CharacterCreationBannerEditorStage)
                {
                    var clan = Clan.PlayerClan; var banner = clan.Banner;
                    if (banner == null) throw new InvalidOperationException("Player banner missing.");
                    clan.Color = banner.GetPrimaryColor(); clan.Color2 = banner.GetFirstIconColor(); clan.UpdateBannerColor(clan.Color, clan.Color2);
                    content.SetMainClanBanner(banner);
                }
                else if (stage is CharacterCreationClanNamingStage) { var n = new TextObject("MNBL Flow Clan"); Clan.PlayerClan.ChangeClanName(n, n); }
                else if (stage is CharacterCreationReviewStage) { content.SetMainCharacterName(name); var n = new TextObject(name); Hero.MainHero.SetName(n, n); }
                manager.NextStage();
                return "creation_stage:" + label;
            }
        }

        // c1: each preview marker against the live campaign.
        private static Dictionary<string, object> Compare(WorldPreview preview)
        {
            int missing = 0, kind = 0, position = 0, name = 0, kingdom = 0, checkedMarkers = 0;
            var examples = new List<string>();
            Action<string> note = s => { if (examples.Count < 12) examples.Add(s); };
            foreach (var m in preview.Markers)
            {
                var s = Settlement.Find(m.Id);
                if (s == null) { missing++; note(m.Id + " missing"); continue; }
                checkedMarkers++;
                bool kindOk = m.Kind == "town" ? s.IsTown : m.Kind == "castle" ? s.IsCastle : m.Kind == "village" ? s.IsVillage : m.Kind == "port" && s.HasPort;
                if (!kindOk) { kind++; note(m.Id + " " + m.Kind); }
                var at = m.Kind == "port" ? s.PortPosition.ToVec2() : s.Position.ToVec2();
                if (Math.Abs(at.X - m.PhysicalX) > 1e-2 || Math.Abs(at.Y - m.PhysicalY) > 1e-2) { position++; note(m.Id + " " + m.Kind + " at " + at.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + at.Y.ToString("0.###", CultureInfo.InvariantCulture) + " not " + m.PhysicalX.ToString("0.###", CultureInfo.InvariantCulture) + "," + m.PhysicalY.ToString("0.###", CultureInfo.InvariantCulture)); }
                if (s.Name == null || s.Name.ToString() != m.Name) { name++; note(m.Id + " named " + (s.Name == null ? "null" : s.Name.ToString()) + " not " + m.Name); }
                // A marker of no kingdom must not stand in a live kingdom either.
                string live = s.MapFaction == null ? null : s.MapFaction.StringId;
                if (m.Kingdom != null ? live != m.Kingdom : s.MapFaction is Kingdom) { kingdom++; note(m.Id + " of " + live + " not " + (m.Kingdom ?? "no kingdom")); }
            }
            // The other way: every live town, castle and village of the world is a marker, and every live port too.
            string prefix = preview.Markers.Count == 0 ? null : preview.Markers[0].Id.Substring(0, preview.Markers[0].Id.IndexOf('_', 5) + 1);
            var ids = new HashSet<string>(preview.Markers.Where(m => m.Kind != "port").Select(m => m.Id), StringComparer.Ordinal);
            var ports = new HashSet<string>(preview.Markers.Where(m => m.Kind == "port").Select(m => m.Id), StringComparer.Ordinal);
            var harbours = new HashSet<string>(preview.Markers.Where(m => m.Kind == "village" && m.Harbour).Select(m => m.Id), StringComparer.Ordinal);
            int unmarked = 0, liveChecked = 0;
            foreach (var s in Settlement.All.Where(x => prefix != null && x.StringId.StartsWith(prefix, StringComparison.Ordinal) && (x.IsTown || x.IsCastle || x.IsVillage)))
            {
                liveChecked++;
                if (!ids.Contains(s.StringId)) { unmarked++; note(s.StringId + " has no marker"); }
                // A town's port is a port marker; a harbour village's (War Sails' landing) is its harbour marker.
                if (s.IsTown && s.HasPort != ports.Contains(s.StringId)) { unmarked++; note(s.StringId + (s.HasPort ? " has no port marker" : " has a port marker but no port")); }
                if (s.IsVillage && s.HasPort != harbours.Contains(s.StringId)) { unmarked++; note(s.StringId + (s.HasPort ? " is a harbour village not marked as one" : " is marked as a harbour village without a port")); }
            }
            // The kingdom names the preview labels, against the live kingdoms.
            int regionNames = 0;
            foreach (var r in preview.Regions)
            {
                var k = Kingdom.All.FirstOrDefault(x => x.StringId == r.Kingdom);
                if (k == null || k.Name == null || k.Name.ToString() != r.Name) { regionNames++; note("region " + r.Kingdom + " named " + (k == null || k.Name == null ? "null" : k.Name.ToString()) + " not " + r.Name); }
            }
            return new Dictionary<string, object> { ["markers"] = preview.Markers.Count, ["checked"] = checkedMarkers, ["missing"] = missing, ["kindMismatches"] = kind, ["positionMismatches"] = position,
                ["nameMismatches"] = name, ["kingdomMismatches"] = kingdom, ["regions"] = preview.Regions.Count, ["regionNameMismatches"] = regionNames, ["liveSettlementsChecked"] = liveChecked, ["liveUnmarked"] = unmarked, ["examples"] = examples };
        }

        // The committed worlds' files (packages and activation records), by path, size and time: a new seed or a withdrawal must not touch them.
        private static Dictionary<string, string> CommittedWorlds()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { GeneratedWorldPackage.Root, new ActivationJournalStore(SubModule.Store).Root })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(f);
                    files[f] = info.Length + "@" + info.LastWriteTimeUtc.Ticks;
                }
            }
            return files;
        }
        // Every earlier file is still there, unchanged (new activation records may appear beside them).
        private static bool Same(Dictionary<string, string> before, Dictionary<string, string> after) { return before.All(kv => after.ContainsKey(kv.Key) && after[kv.Key] == kv.Value); }

        private static string Longer(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            // Each word lengthened by 40 % (doubling vowels), numbers and {VARIABLES} kept.
            return Regex.Replace(text, "[A-Za-z]{3,}", w => { int extra = (int)Math.Ceiling(w.Value.Length * .4); var sb = new System.Text.StringBuilder(w.Value); for (int i = 0; i < extra; i++) sb.Append(w.Value[i % w.Value.Length] == ' ' ? 'e' : Char.ToLowerInvariant(w.Value[i % w.Value.Length])); return sb.ToString(); });
        }

        // The setup panel's measured widgets against the page (Gauntlet units scaled to pixels), by reflection on the widget tree.
        private static Dictionary<string, object> Measure(WorldSetupScreen screen)
        {
            var result = new Dictionary<string, object>();
            try
            {
                var context = screen.Layer.UIContext;
                var root = context.Root;
                result["page"] = Vec(context.EventManager.GetType().GetProperty("PageSize").GetValue(context.EventManager));
                result["scale"] = context.Scale;
                foreach (var id in new[] { "SetupParent", "SetupContent", "SetupButtons", "PreviewMap" })
                {
                    var w = Find(root, id);
                    if (w == null) { result[id] = null; continue; }
                    var pos = Vec(w.GetType().GetProperty("GlobalPosition").GetValue(w)); var size = Vec(w.GetType().GetProperty("Size").GetValue(w));
                    result[id] = new Dictionary<string, object> { ["x"] = pos[0], ["y"] = pos[1], ["width"] = size[0], ["height"] = size[1], ["visible"] = w.IsVisible };
                }
            }
            catch (Exception error) { result["error"] = error.GetType().Name + ": " + error.Message; }
            return result;
        }
        // Inside the page, the map and the buttons inside the panel, and the panel no taller than 1080 UI units: the smallest page a 16:9 or
        // narrower screen gives at UI scale 1 (Gauntlet scales by the smaller of width/1920 and height/1080; 4:3 pages are taller).
        private static bool Inside(object measured)
        {
            var m = measured as Dictionary<string, object>;
            if (m == null || m.ContainsKey("error") || !m.ContainsKey("page")) return false;
            var page = (double[])m["page"];
            Func<string, double[]> rect = id => { var r = m.ContainsKey(id) ? m[id] as Dictionary<string, object> : null; return r == null ? null : new[] { (double)r["x"], (double)r["y"], (double)r["width"], (double)r["height"] }; };
            var panel = rect("SetupParent");
            if (panel == null || panel[2] <= 0 || panel[3] <= 0 || panel[0] < -1 || panel[1] < -1 || panel[0] + panel[2] > page[0] + 1 || panel[1] + panel[3] > page[1] + 1) return false;
            foreach (var id in new[] { "SetupButtons", "PreviewMap" })
            {
                var r = rect(id);
                if (r == null || r[2] <= 0 || r[3] <= 0 || r[0] < panel[0] - 1 || r[1] < panel[1] - 1 || r[0] + r[2] > panel[0] + panel[2] + 1 || r[1] + r[3] > panel[1] + panel[3] + 1) return false;
            }
            double scale = Convert.ToDouble(m["scale"], CultureInfo.InvariantCulture);
            m["panelHeightUnits"] = panel[3] / scale;
            return panel[3] / scale <= 1080;
        }
        private static double[] Vec(object v)
        {
            var t = v.GetType();
            return new[] { Convert.ToDouble(t.GetField("X").GetValue(v), CultureInfo.InvariantCulture), Convert.ToDouble(t.GetField("Y").GetValue(v), CultureInfo.InvariantCulture) };
        }
        private static TaleWorlds.GauntletUI.BaseTypes.Widget Find(TaleWorlds.GauntletUI.BaseTypes.Widget w, string id)
        {
            if (w == null) return null;
            if (w.Id == id) return w;
            foreach (var c in w.Children) { var f = Find(c, id); if (f != null) return f; }
            return null;
        }

        private static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }

        private static string Shot(ProbeContext context, string label)
        {
            string path = Path.Combine(context.Directory, "new-campaign-flow-" + context.ProcessId + "-" + label + ".bmp");
            Utilities.TakeScreenshot(path);
            return path;
        }
    }
}
