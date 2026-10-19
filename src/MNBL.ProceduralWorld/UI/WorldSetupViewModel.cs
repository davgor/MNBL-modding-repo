using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MNBL.ProceduralWorld.Generation;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.ViewModelCollection.Input;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-43: the Gauntlet view model of the New Campaign setup screen (GUI/Prefabs/WorldSetup.xml). It shows WorldSetupModel: every label
    // is a localisation-ready TextObject ({=id}); hashes, paths and job details appear only in the diagnostics view. The bound values are
    // rebuilt when the model's revision changes (or diagnostics are toggled), not every frame.
    // MMR-46: a world ready for review shows its preview (WorldPreviewViewModel, GUI/Prefabs/WorldPreview.xml) in place of the seed and
    // settings, with Start Campaign, New Seed and Back; Back returns to the seed with the world kept.
    internal sealed class WorldSetupViewModel : ViewModel
    {
        private readonly WorldSetupModel model;
        private readonly Action close;
        private int shownRevision = -1;
        private string titleText, profileText, coverageText, seedLabelText, seedHintText, seedText, seedErrorText, randomWorldText, generateText, backText, settingsTitleText, waitText,
            statusText, hostProblemTitle, hostProblemText, hostProblemAction, resultText, peoplesText, creationText, errorTitle, errorText, errorAction, diagnosticsText, diagnosticsButtonText,
            startCampaignText, newSeedText, previewTitleText, noticeText, unfinishedText, resumeText, discardText;
        private bool hasSeedError, isGenerating, canGenerate, isSeedEditable, hasHostProblems, hasResult, hasError, showDiagnostics, hasStatus, hasWaitText,
            canStartCampaign, isPreviewShown, isEditing, isStarting, hasNotice, canGoBack, hasUnfinished, canResume, canDiscard;
        // MMR-47: the failure (and each host problem) with its reference and details.
        private readonly GenerationErrorViewModel errorView = new GenerationErrorViewModel();
        private readonly MBBindingList<GenerationErrorViewModel> hostProblemViews = new MBBindingList<GenerationErrorViewModel>();
        private readonly WorldPreviewViewModel preview = new WorldPreviewViewModel();
        private WorldPreview shownPreview;
        private MBBindingList<WorldSettingItemVM> settings;
        private InputKeyItemVM doneInputKey, cancelInputKey, randomInputKey;

        public WorldSetupViewModel(WorldSetupModel model, Action close)
        {
            this.model = model; this.close = close;
            Settings = new MBBindingList<WorldSettingItemVM>();
            foreach (var s in model.Settings) Settings.Add(new WorldSettingItemVM(s));
            var panel = KeyCategory("GenericPanelGameKeyCategory");
            if (panel != null)
            {
                DoneInputKey = InputKeyItemVM.CreateFromHotKey(panel.GetHotKey(SetupInput.Confirm), true);
                CancelInputKey = InputKeyItemVM.CreateFromHotKey(panel.GetHotKey(SetupInput.Exit), true);
                var randomize = panel.GetHotKey(SetupInput.Randomize);
                if (randomize != null) RandomInputKey = InputKeyItemVM.CreateFromHotKey(randomize, true);
                var previous = panel.GetHotKey(SetupInput.Previous); var next = panel.GetHotKey(SetupInput.Next);
                if (previous != null) preview.PreviousPlaceKey = InputKeyItemVM.CreateFromHotKey(previous, true);
                if (next != null) preview.NextPlaceKey = InputKeyItemVM.CreateFromHotKey(next, true);
            }
            RefreshValues();
        }

        internal WorldSetupModel Model { get { return model; } }

        public static string Text(string text, IDictionary<string, string> variables = null)
        {
            if (String.IsNullOrEmpty(text)) return "";
            return Render(text, variables);
        }

        // The game's localisation and key categories; the offline harness (no game) supplies its own.
        internal static Func<string, IDictionary<string, string>, string> Render = (text, variables) =>
        {
            var t = new TextObject(text);
            if (variables != null) foreach (var kv in variables) t.SetTextVariable(kv.Key, kv.Value);
            return t.ToString();
        };
        internal static Func<string, GameKeyContext> KeyCategory = id => HotKeyManager.GetCategory(id);

        public override void RefreshValues()
        {
            base.RefreshValues();
            var max = new Dictionary<string, string> { ["MAX"] = model.Contract.MaxSeed.ToString(CultureInfo.InvariantCulture) };
            TitleText = Text(WorldSetupText.Title);
            ProfileText = Text(WorldSetupText.Profile);
            CoverageText = Text(WorldSetupText.Coverage);
            SeedLabelText = Text(WorldSetupText.SeedLabel);
            SeedHintText = Text(WorldSetupText.SeedHint, max);
            RandomWorldText = Text(WorldSetupText.RandomWorld);
            GenerateText = Text(WorldSetupText.Generate);
            SettingsTitleText = Text(WorldSetupText.Settings);
            HasWaitText = WaitTime.Available(model.Contract);
            WaitText = HasWaitText ? WaitTime.Text(model.Contract, Text) : "";
            DiagnosticsButtonText = Text(WorldSetupText.Diagnostics);
            StartCampaignText = Text(WorldSetupText.StartCampaign);
            ResumeText = Text(WorldSetupText.Resume); DiscardText = Text(WorldSetupText.Discard);
            HostProblemViews.Clear();
            foreach (var p in model.HostProblems) { var v = new GenerationErrorViewModel(); v.Show(p, Folder(p)); HostProblemViews.Add(v); }
            NewSeedText = Text(WorldSetupText.NewSeed);
            shownPreview = null;   // the preview's texts in the current language
            foreach (var s in Settings) s.RefreshValues();
            Refresh(true);
        }

        // The model's state onto the bound properties: each frame the screen asks, and this rebuilds only when the model changed.
        public void Refresh(bool force = false)
        {
            if (!force && shownRevision == model.Revision) return;
            shownRevision = model.Revision;
            if (seedText != model.SeedTextValue) { seedText = model.SeedTextValue; OnPropertyChangedWithValue(seedText, "SeedText"); }
            HasSeedError = model.SeedProblem != SeedProblem.None;
            SeedErrorText = HasSeedError ? Text(UI.SeedText.Reason(model.SeedProblem), new Dictionary<string, string> { ["MAX"] = model.Contract.MaxSeed.ToString(CultureInfo.InvariantCulture) }) : "";
            IsGenerating = model.State == SetupState.Generating;
            IsStarting = model.State == SetupState.Starting || model.State == SetupState.Started;
            IsPreviewShown = model.State == SetupState.Generated && model.Preview != null;
            IsEditing = !IsPreviewShown && !IsStarting;
            CanGoBack = !IsStarting;
            CanGenerate = model.CanGenerate;
            CanStartCampaign = model.CanStartCampaign;
            IsSeedEditable = model.CanEditSeed;
            BackText = Text(IsGenerating ? WorldSetupText.Cancel : WorldSetupText.Back);
            HasStatus = (IsGenerating || IsStarting) && model.StageText != null;
            var stageVariables = new Dictionary<string, string>(model.StageVariables, StringComparer.Ordinal) { ["SEED"] = model.Seed.ToString(CultureInfo.InvariantCulture) };
            StatusText = HasStatus ? Text(model.StageText, stageVariables) : "";
            // Every host problem's message, and each distinct action once.
            HasHostProblems = model.HostProblems.Count > 0;
            HostProblemTitle = HasHostProblems ? Text(model.HostProblems[0].Title) : "";
            HostProblemText = String.Join(" ", model.HostProblems.Select(p => Text(p.Message, p.Variables)));
            HostProblemAction = String.Join(" ", model.HostProblems.Select(p => p.Action).Distinct().Select(a => Text(a)));
            HasResult = model.State == SetupState.Generated && model.Result != null;
            if (HasResult)
            {
                var r = model.Result;
                ResultText = Text(WorldSetupText.Generated, new Dictionary<string, string> { ["SEED"] = model.Choice.Seed.ToString(CultureInfo.InvariantCulture),
                    ["CITIES"] = r.Cities.ToString(CultureInfo.InvariantCulture), ["PEOPLE_COUNT"] = r.Peoples.Count.ToString(CultureInfo.InvariantCulture) });
                PeoplesText = Text(WorldSetupText.Peoples, new Dictionary<string, string> { ["PEOPLES"] = String.Join(", ", r.Peoples.Select(p =>
                    Text(WorldSetupText.People, new Dictionary<string, string> { ["NAME"] = p.Name, ["CULTURE"] = Text(WorldSetupText.CultureNames[p.Culture]) }))) });
                CreationText = Text(WorldSetupText.Creation, new Dictionary<string, string> { ["CULTURES"] = String.Join(", ", r.Cultures.Select(c => Text(WorldSetupText.CultureNames[c]))) });
            }
            else { ResultText = ""; PeoplesText = ""; CreationText = ""; }
            // The preview (made once per world; its texture on this, the engine, thread) and why the world is or is not offered.
            if (shownPreview != model.Preview)
            {
                shownPreview = model.Preview;
                var counts = new Dictionary<string, string>(StringComparer.Ordinal);
                if (shownPreview != null && model.Result != null)
                {
                    Func<string, string> n = key => { int v; return shownPreview.Counts.TryGetValue(key, out v) ? v.ToString(CultureInfo.InvariantCulture) : "0"; };
                    counts["compiled"] = Text(WorldSetupText.CompiledCounts, new Dictionary<string, string> { ["TOWNS"] = n("towns"), ["CASTLES"] = n("castles"), ["VILLAGES"] = n("villages"),
                        ["PORTS"] = n("ports"), ["KINGDOMS"] = n("kingdoms"), ["CLANS"] = n("clans"), ["CITIES"] = model.Result.Cities.ToString(CultureInfo.InvariantCulture),
                        ["PEOPLE_COUNT"] = model.Result.Peoples.Count.ToString(CultureInfo.InvariantCulture) });
                }
                Preview.Show(shownPreview, counts, Text(WorldSetupText.Legend));
            }
            PreviewTitleText = model.Choice != null ? Text(WorldSetupText.PreviewTitle, new Dictionary<string, string> { ["SEED"] = model.Choice.Seed.ToString(CultureInfo.InvariantCulture) }) : "";
            string notice = null; Dictionary<string, string> noticeVars = null;
            if (model.State == SetupState.Generated && model.Preview == null) notice = WorldSetupText.PreviewFailed;
            else if (IsPreviewShown && model.Withdrawn) notice = WorldSetupText.Withdrawn;
            else if (model.State == SetupState.Editing && model.Choice != null && model.Preview != null && model.Choice.Seed == model.Seed)
            { notice = WorldSetupText.ReuseWorld; noticeVars = new Dictionary<string, string> { ["SEED"] = model.Seed.ToString(CultureInfo.InvariantCulture) }; }
            HasNotice = notice != null;
            NoticeText = HasNotice ? Text(notice, noticeVars) : "";
            HasError = model.State == SetupState.Failed && model.Error != null;
            if (errorView.Error != (HasError ? model.Error : null)) errorView.Show(HasError ? model.Error : null, HasError ? Folder(model.Error) : null);
            // An unfinished world from an earlier session (MMR-47).
            var offered = model.Offered;
            HasUnfinished = offered != null && !IsStarting && !IsPreviewShown && !IsGenerating;
            CanResume = model.CanResume; CanDiscard = model.CanDiscard;
            string offeredText = !HasUnfinished ? null : offered.Resumable ? WorldSetupText.Unfinished : offered.Kind == WorldRecoveryService.Committing ? WorldSetupText.UnfinishedCommitting
                : (offered.Kind == WorldRecoveryService.OtherGenerator || offered.Kind == WorldRecoveryService.OtherPolicies) && offered.HasChoice ? WorldSetupText.UnfinishedOther : WorldSetupText.UnfinishedUnreadable;
            UnfinishedText = !HasUnfinished ? "" : Text(offeredText, new Dictionary<string, string> { ["SEED"] = offered.Seed.ToString(CultureInfo.InvariantCulture) })
                + (model.DiscardProblem != null ? " " + Text(WorldSetupText.DiscardRefused, new Dictionary<string, string> { ["REASON"] = model.DiscardProblem }) : "")
                + (model.Unfinished.Count(j => !j.Finished) > 1 ? " " + Text(WorldSetupText.UnfinishedMore, new Dictionary<string, string> { ["COUNT"] = (model.Unfinished.Count(j => !j.Finished) - 1).ToString(CultureInfo.InvariantCulture) }) : "");
            ErrorTitle = HasError ? Text(model.Error.Title) : "";
            ErrorText = HasError ? Text(model.Error.Message, model.Error.Variables) : "";
            ErrorAction = HasError ? Text(model.Error.Action) : "";
            DiagnosticsText = ShowDiagnostics ? Diagnostics() : "";
        }

        private string Diagnostics()
        {
            var c = model.Contract;
            var lines = new List<string> {
                "profile one-shot (" + c.ProfileSchema + "), recipe " + c.RecipeVersion + ", overrides " + String.Join(", ", c.Overrides.Select(kv => kv.Key + "=" + kv.Value)) + ", quality attempts " + c.QualityAttempts,
                "producer lock " + c.ProducerLockSha256 + " (worldgen/producer.lock.json), producer " + c.ProducerSha256 + ", max seed " + c.MaxSeed + ", profile tools lock " + c.ProfileToolsLockSha256,
                "wait-time budget " + c.BudgetStatus + ": " + String.Join("; ", c.Wait.Select(w => w.Id + " <= " + w.MaxSeconds.ToString(CultureInfo.InvariantCulture) + " s (" + w.Source + ")")),
                "documents " + String.Join(", ", c.Sources.Select(kv => kv.Key + " " + kv.Value.Substring(0, 12))),
                "host " + (model.HostProblems.Count == 0 ? "compatible" : String.Join("; ", model.HostProblems.Select(p => p.ToString()))) };
            if (model.Choice != null) lines.Add("request fields " + String.Join(", ", model.Choice.RequestFields().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                kv.Key + "=" + (kv.Value is IDictionary<string, object> ? "{" + String.Join(",", ((IDictionary<string, object>)kv.Value).Select(o => o.Key + ":" + o.Value)) + "}" : Convert.ToString(kv.Value, CultureInfo.InvariantCulture)))));
            if (model.JobRoot != null) lines.Add("job " + model.JobRoot);
            if (model.ResultSplit != null) lines.Add("source split " + model.ResultSplit);
            if (model.Result != null) lines.Add("peoples " + String.Join(", ", model.Result.Peoples.Select(p => p.Civilization + "->" + p.Culture + " " + p.Cities)));
            if (model.Error != null) lines.Add("error " + model.Error + (model.Diagnostics != null && model.Error.CorrelationId != null ? " diagnostics " + model.Diagnostics.Folder(model.Error.CorrelationId) : ""));
            if (model.Unfinished.Count > 0) lines.Add("unfinished jobs " + String.Join(", ", model.Unfinished.Select(j => j.Id.Substring(0, 8) + " " + j.Phase + (j.Resumable ? "" : " (" + j.Reason + ")"))));
            if (model.DiscardProblem != null) lines.Add("discard kept the job: " + model.DiscardProblem);
            if (model.Preview != null) lines.Add("preview content " + model.Preview.WorldContentId.Substring(0, 12) + ", layout " + model.Preview.LayoutSha256.Substring(0, 12) + ", scene " + model.Preview.SceneName +
                ", raster " + model.Preview.RasterSha256.Substring(0, 12) + " " + model.Preview.Width + "x" + model.Preview.Height + ", " + model.Preview.Markers.Count + " markers; texture " +
                (WorldPreviewTextures.Current != null ? WorldPreviewTextures.Mode + " #" + WorldPreviewTextures.Created : "none") + (WorldPreviewTextures.LastError != null ? " (" + WorldPreviewTextures.LastError + ")" : ""));
            if (model.PreviewError != null) lines.Add("preview failed " + model.PreviewError);
            lines.Add("generations " + model.Generations + ", starts " + model.Starts + (model.Reused ? ", world reused" : "") + (model.Withdrawn ? ", last start withdrawn" : "") + ", state " + model.State);
            return String.Join("  |  ", lines);   // the rich text widget draws a line feed as a box glyph
        }

        public void ExecuteRandomWorld() { model.RandomWorldSeed(); Refresh(); }
        public void ExecuteGenerate() { model.Generate(); Refresh(); }
        public void ExecuteStartCampaign() { model.StartCampaign(); Refresh(); }
        public void ExecuteResume() { model.ResumeUnfinished(); Refresh(); }
        public void ExecuteDiscard() { model.DiscardUnfinished(); Refresh(); }

        private string Folder(GenerationError error) { return model.Diagnostics == null || error == null || error.CorrelationId == null ? null : model.Diagnostics.Folder(error.CorrelationId); }
        public void ExecuteNewSeed() { model.NewSeed(); Refresh(); }
        public void ExecuteToggleDiagnostics() { ShowDiagnostics = !ShowDiagnostics; Refresh(true); }
        public void ExecuteBack()
        {
            if (model.Back()) close();
            else Refresh();
        }

        public override void OnFinalize()
        {
            base.OnFinalize();
            preview.OnFinalize(); errorView.OnFinalize(); foreach (var v in HostProblemViews) v.OnFinalize();
            if (DoneInputKey != null) DoneInputKey.OnFinalize();
            if (CancelInputKey != null) CancelInputKey.OnFinalize();
            if (RandomInputKey != null) RandomInputKey.OnFinalize();
        }

        [DataSourceProperty] public string TitleText { get { return titleText; } set { if (value != titleText) { titleText = value; OnPropertyChangedWithValue(value, "TitleText"); } } }
        [DataSourceProperty] public string ProfileText { get { return profileText; } set { if (value != profileText) { profileText = value; OnPropertyChangedWithValue(value, "ProfileText"); } } }
        [DataSourceProperty] public string CoverageText { get { return coverageText; } set { if (value != coverageText) { coverageText = value; OnPropertyChangedWithValue(value, "CoverageText"); } } }
        [DataSourceProperty] public string SeedLabelText { get { return seedLabelText; } set { if (value != seedLabelText) { seedLabelText = value; OnPropertyChangedWithValue(value, "SeedLabelText"); } } }
        [DataSourceProperty] public string SeedHintText { get { return seedHintText; } set { if (value != seedHintText) { seedHintText = value; OnPropertyChangedWithValue(value, "SeedHintText"); } } }
        // Two-way: the edit box writes the typed text; the model validates it exactly.
        [DataSourceProperty] public string SeedText
        {
            get { return seedText; }
            set { if (value != seedText) { model.SetSeedText(value); seedText = model.SeedTextValue; OnPropertyChangedWithValue(seedText, "SeedText"); Refresh(); } }
        }
        [DataSourceProperty] public string SeedErrorText { get { return seedErrorText; } set { if (value != seedErrorText) { seedErrorText = value; OnPropertyChangedWithValue(value, "SeedErrorText"); } } }
        [DataSourceProperty] public bool HasSeedError { get { return hasSeedError; } set { if (value != hasSeedError) { hasSeedError = value; OnPropertyChangedWithValue(value, "HasSeedError"); } } }
        [DataSourceProperty] public string RandomWorldText { get { return randomWorldText; } set { if (value != randomWorldText) { randomWorldText = value; OnPropertyChangedWithValue(value, "RandomWorldText"); } } }
        [DataSourceProperty] public string GenerateText { get { return generateText; } set { if (value != generateText) { generateText = value; OnPropertyChangedWithValue(value, "GenerateText"); } } }
        [DataSourceProperty] public string BackText { get { return backText; } set { if (value != backText) { backText = value; OnPropertyChangedWithValue(value, "BackText"); } } }
        [DataSourceProperty] public string SettingsTitleText { get { return settingsTitleText; } set { if (value != settingsTitleText) { settingsTitleText = value; OnPropertyChangedWithValue(value, "SettingsTitleText"); } } }
        [DataSourceProperty] public MBBindingList<WorldSettingItemVM> Settings { get { return settings; } set { if (value != settings) { settings = value; OnPropertyChangedWithValue(value, "Settings"); } } }
        [DataSourceProperty] public string WaitText { get { return waitText; } set { if (value != waitText) { waitText = value; OnPropertyChangedWithValue(value, "WaitText"); } } }
        [DataSourceProperty] public bool HasWaitText { get { return hasWaitText; } set { if (value != hasWaitText) { hasWaitText = value; OnPropertyChangedWithValue(value, "HasWaitText"); } } }
        [DataSourceProperty] public string StatusText { get { return statusText; } set { if (value != statusText) { statusText = value; OnPropertyChangedWithValue(value, "StatusText"); } } }
        [DataSourceProperty] public bool HasStatus { get { return hasStatus; } set { if (value != hasStatus) { hasStatus = value; OnPropertyChangedWithValue(value, "HasStatus"); } } }
        [DataSourceProperty] public bool IsGenerating { get { return isGenerating; } set { if (value != isGenerating) { isGenerating = value; OnPropertyChangedWithValue(value, "IsGenerating"); } } }
        [DataSourceProperty] public bool CanGenerate { get { return canGenerate; } set { if (value != canGenerate) { canGenerate = value; OnPropertyChangedWithValue(value, "CanGenerate"); } } }
        [DataSourceProperty] public bool IsSeedEditable { get { return isSeedEditable; } set { if (value != isSeedEditable) { isSeedEditable = value; OnPropertyChangedWithValue(value, "IsSeedEditable"); } } }
        [DataSourceProperty] public bool HasHostProblems { get { return hasHostProblems; } set { if (value != hasHostProblems) { hasHostProblems = value; OnPropertyChangedWithValue(value, "HasHostProblems"); } } }
        [DataSourceProperty] public string HostProblemTitle { get { return hostProblemTitle; } set { if (value != hostProblemTitle) { hostProblemTitle = value; OnPropertyChangedWithValue(value, "HostProblemTitle"); } } }
        [DataSourceProperty] public string HostProblemText { get { return hostProblemText; } set { if (value != hostProblemText) { hostProblemText = value; OnPropertyChangedWithValue(value, "HostProblemText"); } } }
        [DataSourceProperty] public string HostProblemAction { get { return hostProblemAction; } set { if (value != hostProblemAction) { hostProblemAction = value; OnPropertyChangedWithValue(value, "HostProblemAction"); } } }
        [DataSourceProperty] public bool HasResult { get { return hasResult; } set { if (value != hasResult) { hasResult = value; OnPropertyChangedWithValue(value, "HasResult"); } } }
        [DataSourceProperty] public string ResultText { get { return resultText; } set { if (value != resultText) { resultText = value; OnPropertyChangedWithValue(value, "ResultText"); } } }
        [DataSourceProperty] public string PeoplesText { get { return peoplesText; } set { if (value != peoplesText) { peoplesText = value; OnPropertyChangedWithValue(value, "PeoplesText"); } } }
        [DataSourceProperty] public string CreationText { get { return creationText; } set { if (value != creationText) { creationText = value; OnPropertyChangedWithValue(value, "CreationText"); } } }
        [DataSourceProperty] public WorldPreviewViewModel Preview { get { return preview; } set { } }
        [DataSourceProperty] public string StartCampaignText { get { return startCampaignText; } set { if (value != startCampaignText) { startCampaignText = value; OnPropertyChangedWithValue(value, "StartCampaignText"); } } }
        [DataSourceProperty] public string NewSeedText { get { return newSeedText; } set { if (value != newSeedText) { newSeedText = value; OnPropertyChangedWithValue(value, "NewSeedText"); } } }
        [DataSourceProperty] public string PreviewTitleText { get { return previewTitleText; } set { if (value != previewTitleText) { previewTitleText = value; OnPropertyChangedWithValue(value, "PreviewTitleText"); } } }
        [DataSourceProperty] public string NoticeText { get { return noticeText; } set { if (value != noticeText) { noticeText = value; OnPropertyChangedWithValue(value, "NoticeText"); } } }
        [DataSourceProperty] public bool HasNotice { get { return hasNotice; } set { if (value != hasNotice) { hasNotice = value; OnPropertyChangedWithValue(value, "HasNotice"); } } }
        [DataSourceProperty] public bool CanStartCampaign { get { return canStartCampaign; } set { if (value != canStartCampaign) { canStartCampaign = value; OnPropertyChangedWithValue(value, "CanStartCampaign"); } } }
        [DataSourceProperty] public bool IsPreviewShown { get { return isPreviewShown; } set { if (value != isPreviewShown) { isPreviewShown = value; OnPropertyChangedWithValue(value, "IsPreviewShown"); } } }
        [DataSourceProperty] public bool IsEditing { get { return isEditing; } set { if (value != isEditing) { isEditing = value; OnPropertyChangedWithValue(value, "IsEditing"); } } }
        [DataSourceProperty] public bool IsStarting { get { return isStarting; } set { if (value != isStarting) { isStarting = value; OnPropertyChangedWithValue(value, "IsStarting"); } } }
        [DataSourceProperty] public GenerationErrorViewModel ErrorView { get { return errorView; } set { } }
        [DataSourceProperty] public MBBindingList<GenerationErrorViewModel> HostProblemViews { get { return hostProblemViews; } set { } }
        [DataSourceProperty] public bool HasUnfinished { get { return hasUnfinished; } set { if (value != hasUnfinished) { hasUnfinished = value; OnPropertyChangedWithValue(value, "HasUnfinished"); } } }
        [DataSourceProperty] public bool CanResume { get { return canResume; } set { if (value != canResume) { canResume = value; OnPropertyChangedWithValue(value, "CanResume"); } } }
        [DataSourceProperty] public bool CanDiscard { get { return canDiscard; } set { if (value != canDiscard) { canDiscard = value; OnPropertyChangedWithValue(value, "CanDiscard"); } } }
        [DataSourceProperty] public string UnfinishedText { get { return unfinishedText; } set { if (value != unfinishedText) { unfinishedText = value; OnPropertyChangedWithValue(value, "UnfinishedText"); } } }
        [DataSourceProperty] public string ResumeText { get { return resumeText; } set { if (value != resumeText) { resumeText = value; OnPropertyChangedWithValue(value, "ResumeText"); } } }
        [DataSourceProperty] public string DiscardText { get { return discardText; } set { if (value != discardText) { discardText = value; OnPropertyChangedWithValue(value, "DiscardText"); } } }
        [DataSourceProperty] public bool CanGoBack { get { return canGoBack; } set { if (value != canGoBack) { canGoBack = value; OnPropertyChangedWithValue(value, "CanGoBack"); } } }
        [DataSourceProperty] public bool HasError { get { return hasError; } set { if (value != hasError) { hasError = value; OnPropertyChangedWithValue(value, "HasError"); } } }
        [DataSourceProperty] public string ErrorTitle { get { return errorTitle; } set { if (value != errorTitle) { errorTitle = value; OnPropertyChangedWithValue(value, "ErrorTitle"); } } }
        [DataSourceProperty] public string ErrorText { get { return errorText; } set { if (value != errorText) { errorText = value; OnPropertyChangedWithValue(value, "ErrorText"); } } }
        [DataSourceProperty] public string ErrorAction { get { return errorAction; } set { if (value != errorAction) { errorAction = value; OnPropertyChangedWithValue(value, "ErrorAction"); } } }
        [DataSourceProperty] public bool ShowDiagnostics { get { return showDiagnostics; } set { if (value != showDiagnostics) { showDiagnostics = value; OnPropertyChangedWithValue(value, "ShowDiagnostics"); } } }
        [DataSourceProperty] public string DiagnosticsText { get { return diagnosticsText; } set { if (value != diagnosticsText) { diagnosticsText = value; OnPropertyChangedWithValue(value, "DiagnosticsText"); } } }
        [DataSourceProperty] public string DiagnosticsButtonText { get { return diagnosticsButtonText; } set { if (value != diagnosticsButtonText) { diagnosticsButtonText = value; OnPropertyChangedWithValue(value, "DiagnosticsButtonText"); } } }
        [DataSourceProperty] public InputKeyItemVM DoneInputKey { get { return doneInputKey; } set { if (value != doneInputKey) { doneInputKey = value; OnPropertyChangedWithValue(value, "DoneInputKey"); } } }
        [DataSourceProperty] public InputKeyItemVM CancelInputKey { get { return cancelInputKey; } set { if (value != cancelInputKey) { cancelInputKey = value; OnPropertyChangedWithValue(value, "CancelInputKey"); } } }
        [DataSourceProperty] public InputKeyItemVM RandomInputKey { get { return randomInputKey; } set { if (value != randomInputKey) { randomInputKey = value; OnPropertyChangedWithValue(value, "RandomInputKey"); } } }
    }

    internal sealed class WorldSettingItemVM : ViewModel
    {
        private readonly WorldSetting setting;
        private string nameText, valueText, reasonText;
        private bool isLocked;
        public WorldSettingItemVM(WorldSetting setting) { this.setting = setting; RefreshValues(); }
        public override void RefreshValues()
        {
            base.RefreshValues();
            NameText = WorldSetupViewModel.Text(setting.Name);
            ValueText = WorldSetupViewModel.Text(setting.Value, setting.Variables);
            ReasonText = WorldSetupViewModel.Text(setting.Reason, setting.Variables);
            IsLocked = setting.Locked;
        }
        [DataSourceProperty] public string NameText { get { return nameText; } set { if (value != nameText) { nameText = value; OnPropertyChangedWithValue(value, "NameText"); } } }
        [DataSourceProperty] public string ValueText { get { return valueText; } set { if (value != valueText) { valueText = value; OnPropertyChangedWithValue(value, "ValueText"); } } }
        [DataSourceProperty] public string ReasonText { get { return reasonText; } set { if (value != reasonText) { reasonText = value; OnPropertyChangedWithValue(value, "ReasonText"); } } }
        [DataSourceProperty] public bool IsLocked { get { return isLocked; } set { if (value != isLocked) { isLocked = value; OnPropertyChangedWithValue(value, "IsLocked"); } } }
    }
}
