using System;
using System.Collections.Generic;
using MNBL.ProceduralWorld.Generation;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.UI
{
    // MMR-47: one failure as the player sees it: its category's title, the message, the action to take (retry, settings, reinstall), and
    // "Reference <correlation id>". The detail (codes, paths, the retained diagnostics folder) stays behind Details, so the ordinary panel
    // never shows a path or a hash.
    internal sealed class GenerationErrorViewModel : ViewModel
    {
        private string titleText, messageText, actionText, referenceText, detailsText, detailsButtonText;
        private bool hasError, showDetails, hasReference;
        public GenerationError Error { get; private set; }
        public string DiagnosticsFolder { get; private set; }

        public void Show(GenerationError error, string diagnosticsFolder)
        {
            Error = error; DiagnosticsFolder = diagnosticsFolder;
            HasError = error != null;
            TitleText = error == null ? "" : WorldSetupViewModel.Text(error.Title);
            MessageText = error == null ? "" : WorldSetupViewModel.Text(error.Message, error.Variables);
            ActionText = error == null ? "" : WorldSetupViewModel.Text(error.Action);
            HasReference = error != null && error.CorrelationId != null;
            ReferenceText = HasReference ? WorldSetupViewModel.Text(WorldSetupText.ErrorReference, new Dictionary<string, string> { ["ID"] = error.CorrelationId }) : "";
            DetailsButtonText = WorldSetupViewModel.Text(WorldSetupText.ErrorDetails);
            Refresh();
        }

        public void ExecuteToggleDetails() { ShowDetails = !ShowDetails; Refresh(); }

        private void Refresh()
        {
            if (Error == null || !ShowDetails) { DetailsText = ""; return; }
            DetailsText = Error.Category + " " + Error.Code + (Error.Detail.Length > 0 ? " | " + Error.Detail : "")
                + (DiagnosticsFolder != null ? " | diagnostics " + DiagnosticsFolder : "") + (Error.RetainProblem != null ? " | not retained: " + Error.RetainProblem : "");
        }

        [DataSourceProperty] public bool HasError { get { return hasError; } set { if (value != hasError) { hasError = value; OnPropertyChangedWithValue(value, "HasError"); } } }
        [DataSourceProperty] public string TitleText { get { return titleText; } set { if (value != titleText) { titleText = value; OnPropertyChangedWithValue(value, "TitleText"); } } }
        [DataSourceProperty] public string MessageText { get { return messageText; } set { if (value != messageText) { messageText = value; OnPropertyChangedWithValue(value, "MessageText"); } } }
        [DataSourceProperty] public string ActionText { get { return actionText; } set { if (value != actionText) { actionText = value; OnPropertyChangedWithValue(value, "ActionText"); } } }
        [DataSourceProperty] public bool HasReference { get { return hasReference; } set { if (value != hasReference) { hasReference = value; OnPropertyChangedWithValue(value, "HasReference"); } } }
        [DataSourceProperty] public string ReferenceText { get { return referenceText; } set { if (value != referenceText) { referenceText = value; OnPropertyChangedWithValue(value, "ReferenceText"); } } }
        [DataSourceProperty] public bool ShowDetails { get { return showDetails; } set { if (value != showDetails) { showDetails = value; OnPropertyChangedWithValue(value, "ShowDetails"); } } }
        [DataSourceProperty] public string DetailsText { get { return detailsText; } set { if (value != detailsText) { detailsText = value; OnPropertyChangedWithValue(value, "DetailsText"); } } }
        [DataSourceProperty] public string DetailsButtonText { get { return detailsButtonText; } set { if (value != detailsButtonText) { detailsButtonText = value; OnPropertyChangedWithValue(value, "DetailsButtonText"); } } }
    }
}
