using System;
using System.Linq;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace MNBL.CombatProbe
{
    // Explicitly opt-in, engine-thread only. Proves programmatic production entry points,
    // not physical input delivery, widget painting, or controller usability.
    public sealed class UiProbeScenario
    {
        private int stage;
        private float elapsed, stageElapsed;
        private bool subscribed, expectingInquiry;
        private InquiryData inquiry;
        private GameState ownedState;
        private AbilitiesProbeScreen firstScreen;
        private string initialAssignment, appliedAssignment;
        public bool Complete { get; private set; }
        public bool Passed { get; private set; }

        public void Tick(float dt)
        {
            if (Complete) return;
            elapsed += dt; stageElapsed += dt;
            try
            {
                if (elapsed > 180 || (stage > 0 && stageElapsed > 20))
                    throw new TimeoutException("UI scenario stage " + stage + " exceeded deadline");
                if (stageElapsed < 0.75f) return;
                switch (stage)
                {
                    case 0:
                        var map = ScreenManager.TopScreen as MapScreen;
                        if (!(Game.Current?.GameStateManager.ActiveState is MapState) || map == null ||
                            !map.IsReady || map.MapScene == null || AbilitiesProbe.LastHandler == null ||
                            InformationManager.IsAnyInquiryActive()) return;
                        InformationManager.OnShowInquiry += CaptureInquiry;
                        subscribed = true;
                        ProbeLog.Write("ui_scenario.begin route=programmatic physical_input_proven=false");
                        AbilitiesProbe.OpenFromNavigation();
                        ownedState = Game.Current.GameStateManager.ActiveState;
                        Next(); break;
                    case 1:
                        var screen = Screen();
                        firstScreen = screen;
                        ProbeLog.Capture("abilities-open");
                        initialAssignment = screen.ViewModel.AssignmentText;
                        Check(!screen.ViewModel.IsDirty, "initial_clean");
                        screen.ViewModel.ExecuteAssign();
                        Check(screen.ViewModel.IsDirty && screen.ViewModel.AssignmentText != initialAssignment, "assignment_binding_changed");
                        screen.ViewModel.ExecuteReset();
                        Check(!screen.ViewModel.IsDirty && screen.ViewModel.AssignmentText == initialAssignment, "reset_restored");
                        screen.ViewModel.ExecuteAssign();
                        screen.ViewModel.ExecuteApply();
                        appliedAssignment = screen.ViewModel.AssignmentText;
                        Check(!screen.ViewModel.IsDirty && appliedAssignment != initialAssignment, "apply_committed_session_assignment");
                        screen.Close(); Next(); break;
                    case 2:
                        Check(Game.Current.GameStateManager.ActiveState is MapState && ScreenManager.TopScreen is MapScreen,
                            "back_restored_map");
                        Check(AbilitiesProbeScreen.Current == null && !firstScreen.MovieLoaded, "old_screen_released");
                        AbilitiesProbe.OpenFromNavigation(); ownedState = Game.Current.GameStateManager.ActiveState;
                        Next(); break;
                    case 3:
                        ProbeLog.Capture("abilities-reopened");
                        Check(!ReferenceEquals(Screen(), firstScreen), "reopen_new_screen");
                        Check(Screen().ViewModel.AssignmentText == appliedAssignment, "reopen_retained_assignment");
                        RequestStockWithDirtyAssignment(); Next(); break;
                    case 4:
                        ProbeLog.Capture("abilities-unsaved-inquiry");
                        CheckInquiry();
                        Respond(true); Next(); break;
                    case 5:
                        CheckStock();
                        AbilitiesProbe.OpenFromNavigation(); ownedState = Game.Current.GameStateManager.ActiveState;
                        Next(); break;
                    case 6:
                        // Two toggles have now been applied, restoring the initial session value.
                        Check(Screen().ViewModel.AssignmentText == initialAssignment, "native_inquiry_yes_applied");
                        RequestStockWithDirtyAssignment(); Next(); break;
                    case 7:
                        CheckInquiry(); Respond(false); Next(); break;
                    case 8:
                        CheckStock();
                        AbilitiesProbe.OpenFromNavigation(); ownedState = Game.Current.GameStateManager.ActiveState;
                        Next(); break;
                    case 9:
                        Check(Screen().ViewModel.AssignmentText == initialAssignment, "native_inquiry_no_discarded");
                        Screen().Close(); Next(); break;
                    case 10:
                        Check(Game.Current.GameStateManager.ActiveState is MapState && ScreenManager.TopScreen is MapScreen &&
                            AbilitiesProbeScreen.Current == null, "final_map_and_screen_released");
                        Passed = true; Complete = true; Cleanup(false);
                        ProbeLog.Write("ui_scenario.pass route=programmatic physical_input_proven=false render_proven=false");
                        break;
                }
            }
            catch (Exception error)
            {
                Complete = true;
                ProbeLog.Write("ui_scenario.fail stage=" + stage + " error=" + error);
                Cleanup(true);
            }
        }

        public void Cancel()
        {
            if (Complete) return;
            Complete = true; ProbeLog.Write("ui_scenario.cancelled"); Cleanup(true);
        }
        private void Next() { stage++; stageElapsed = 0; }
        private void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            ProbeLog.Write("ui_scenario.check " + name);
        }
        private AbilitiesProbeScreen Screen()
        {
            var screen = AbilitiesProbeScreen.Current;
            if (!(Game.Current.GameStateManager.ActiveState is AbilitiesProbeState) || screen == null ||
                ScreenManager.TopScreen != screen || !screen.MovieLoaded || screen.ViewModel == null)
                throw new InvalidOperationException("Expected active registered Abilities screen and loaded movie");
            return screen;
        }
        private void RequestStockWithDirtyAssignment()
        {
            Screen().ViewModel.ExecuteAssign();
            Check(Screen().AnyUnsavedChanges(), "dirty_before_stock_navigation");
            var stock = AbilitiesProbe.LastHandler.GetElement("character_developer");
            Check(stock != null && stock.Permission.IsAuthorized, "stock_navigation_authorized");
            inquiry = null;
            expectingInquiry = true;
            try { stock.OpenView(); }
            finally { expectingInquiry = false; }
            // Capture only an inquiry synchronously emitted by this native navigation call.
            Check(inquiry != null, "native_unsaved_inquiry_emitted");
        }
        private void CaptureInquiry(InquiryData data, bool pause, bool prioritize)
        {
            if (!expectingInquiry) return;
            if (inquiry != null) throw new InvalidOperationException("Multiple unexpected navigation inquiries");
            inquiry = data;
        }
        private void CheckInquiry()
        {
            Screen();
            Check(inquiry != null && inquiry.AffirmativeAction != null && inquiry.NegativeAction != null &&
                InformationManager.IsAnyInquiryActive(), "native_inquiry_active_with_callbacks");
        }
        private void Respond(bool apply)
        {
            var captured = inquiry; inquiry = null;
            InformationManager.HideInquiry();
            (apply ? captured.AffirmativeAction : captured.NegativeAction)();
            ownedState = Game.Current.GameStateManager.ActiveState;
            ProbeLog.Write("ui_scenario.native_inquiry_callback apply=" + apply);
        }
        private void CheckStock()
        {
            var state = Game.Current.GameStateManager.ActiveState;
            var top = ScreenManager.TopScreen;
            var registered = state?.GetListenerOfType<ScreenBase>();
            var type = top?.GetType();
            var registrations = type == null ? new GameStateScreen[0] : type
                .GetCustomAttributes(typeof(GameStateScreen), false).Cast<GameStateScreen>().ToArray();
            // The shipped NavalDLC registers GauntletNavalCharacterDeveloperScreen for the
            // same state and derives it from the Sandbox screen. ScreenManager chooses the
            // last active-module registration, so an exact simple-name check is incorrect.
            bool stockFamily = false;
            for (var ancestor = type; ancestor != null; ancestor = ancestor.BaseType)
                if (ancestor.FullName == "SandBox.GauntletUI.GauntletCharacterDeveloperScreen") stockFamily = true;
            ProbeLog.Write("ui_scenario.stock_diagnostic state=" + state?.GetType().AssemblyQualifiedName +
                " top=" + type?.AssemblyQualifiedName + " mvid=" + type?.Assembly.ManifestModule.ModuleVersionId +
                " listener=" + registered?.GetType().AssemblyQualifiedName +
                " top_is_registered_listener=" + ReferenceEquals(top, registered) +
                " state_registrations=" + string.Join(",", registrations.Select(x => x.GameStateType.FullName)) +
                " stock_family=" + stockFamily + " abilities_released=" + (AbilitiesProbeScreen.Current == null));
            Check(state is CharacterDeveloperState && top != null && ReferenceEquals(top, registered) &&
                registrations.Any(x => x.GameStateType == state.GetType()) && stockFamily &&
                AbilitiesProbeScreen.Current == null, "stock_registered_screen_active");
        }
        private void Cleanup(bool closeOwned)
        {
            if (subscribed) { InformationManager.OnShowInquiry -= CaptureInquiry; subscribed = false; }
            try
            {
                if (inquiry != null) { InformationManager.HideInquiry(); inquiry = null; }
                if (closeOwned && Game.Current != null && ownedState != null &&
                    ReferenceEquals(Game.Current.GameStateManager.ActiveState, ownedState))
                {
                    var screen = AbilitiesProbeScreen.Current;
                    if (screen != null) { screen.ResetChanges(); screen.Close(); }
                    else if (ownedState is CharacterDeveloperState) Game.Current.GameStateManager.PopState(0);
                }
                ProbeLog.Write("ui_scenario.cleanup state=" + Game.Current?.GameStateManager.ActiveState?.GetType().Name);
            }
            catch (Exception error) { ProbeLog.Write("ui_scenario.cleanup_failed " + error); }
        }
    }
}
