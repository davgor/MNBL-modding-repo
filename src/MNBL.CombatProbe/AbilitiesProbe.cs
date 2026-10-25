using System;
using System.Linq;
using HarmonyLib;
using SandBox.View;
using SandBox.View.Map.Navigation;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace MNBL.CombatProbe
{
    // Disposable feasibility module: no campaign data, abilities or progression are persisted.
    public static class AbilitiesProbe
    {
        public const string ElementId = "mnbl_abilities";
        public static MapNavigationHandler LastHandler { get; private set; }
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MapNavigationHandler), "OnCreateElements"),
                postfix: new HarmonyMethod(typeof(AbilitiesProbe), nameof(AddElement)));
            harmony.Patch(AccessTools.Constructor(typeof(MapNavigationItemVM), new[] { typeof(INavigationElement) }),
                postfix: new HarmonyMethod(typeof(AbilitiesProbe), nameof(SetProbeIcon)));
            ProbeLog.Write("abilities.patches_installed");
        }
        private static void AddElement(MapNavigationHandler __instance, ref INavigationElement[] __result)
        {
            LastHandler = __instance;
            if (__result.Any(x => x.StringId == ElementId)) return;
            var list = __result.ToList();
            int index = list.FindIndex(x => x.StringId == "inventory");
            list.Insert(index >= 0 ? index + 1 : list.Count, new AbilitiesNavigationElement(__instance));
            __result = list.ToArray();
            ProbeLog.Write("abilities.navigation_created ids=" + string.Join(",", __result.Select(x => x.StringId)));
        }
        private static void SetProbeIcon(MapNavigationItemVM __instance)
        {
            if (__instance.NavigationElement.StringId != ElementId) return;
            // Only the art key is aliased; navigation identity and activation remain independent.
            // Custom art/brush loading is intentionally not claimed by this lifecycle probe.
            __instance.ItemId = "character_developer";
            ProbeLog.Write("abilities.navigation_vm_created native_icon_alias=character_developer");
        }
        public static void OpenFromNavigation()
        {
            var element = LastHandler == null ? null : LastHandler.GetElement(ElementId);
            if (element == null) throw new InvalidOperationException("Abilities navigation element has not been created.");
            element.OpenView();
        }
    }

    public sealed class AbilitiesNavigationElement : MapNavigationElementBase
    {
        public AbilitiesNavigationElement(MapNavigationHandler handler) : base(handler) { }
        public override string StringId => AbilitiesProbe.ElementId;
        public override bool IsActive => Game.Current.GameStateManager.ActiveState is AbilitiesProbeState;
        public override bool IsLockingNavigation => false;
        public override bool HasAlert => false;
        protected override TextObject GetTooltip() => new TextObject("Abilities (feasibility probe)");
        protected override TextObject GetAlertTooltip() => TextObject.GetEmpty();
        protected override NavigationPermissionItem GetPermission() => new NavigationPermissionItem(
            MapNavigationHelper.IsNavigationBarEnabled(_handler) && !IsActive &&
            (Mission.Current == null || Mission.Current.IsCharacterWindowAccessAllowed), null);
        public override void GoToLink() { }
        public override void OpenView(params object[] parameters) => OpenView();
        public override void OpenView()
        {
            ProbeLog.Write("abilities.open_requested authorized=" + Permission.IsAuthorized);
            if (!Permission.IsAuthorized) return;
            Action open = () => Game.Current.GameStateManager.PushState(
                Game.Current.GameStateManager.CreateState<AbilitiesProbeState>(), 0);
            var previous = ScreenManager.TopScreen as IChangeableScreen;
            if (previous != null && previous.AnyUnsavedChanges())
                InformationManager.ShowInquiry(previous.CanChangesBeApplied()
                    ? MapNavigationHelper.GetUnsavedChangedInquiry(open)
                    : MapNavigationHelper.GetUnapplicableChangedInquiry(), false, false);
            else MapNavigationHelper.SwitchToANewScreen(open);
        }
    }

    public sealed class AbilitiesProbeState : GameState
    {
        public override bool IsMenuState => true;
    }

    [GameStateScreen(typeof(AbilitiesProbeState))]
    public sealed class AbilitiesProbeScreen : ScreenBase, IGameStateListener, IChangeableScreen
    {
        private GauntletLayer layer;
        private GauntletMovieIdentifier movie;
        private AbilitiesProbeVM vm;
        private bool layerAttached, closeRequested, popRequested;
        public static AbilitiesProbeScreen Current { get; private set; }
        public bool MovieLoaded => movie != null;
        public AbilitiesProbeVM ViewModel => vm;
        public AbilitiesProbeScreen(AbilitiesProbeState state) { ProbeLog.Write("abilities.screen_constructed"); }
        void IGameStateListener.OnInitialize() { ProbeLog.Write("abilities.state_initialized"); }
        void IGameStateListener.OnActivate()
        {
            base.OnActivate();
            if (layer != null)
            {
                if (!layerAttached) { AddLayer(layer); layerAttached = true; }
                layer.IsFocusLayer = true;
                ScreenManager.TrySetFocus(layer);
                Current = this;
                ProbeLog.Write("abilities.layer_reactivated");
                return;
            }
            vm = new AbilitiesProbeVM(Close);
            layer = new GauntletLayer("MNBLAbilitiesProbe", 1, true);
            layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
            layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
            layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericCampaignPanelsGameKeyCategory"));
            movie = layer.LoadMovie("CombatProbeAbilities", vm);
            AddLayer(layer);
            layerAttached = true;
            layer.IsFocusLayer = true;
            ScreenManager.TrySetFocus(layer);
            layer.GamepadNavigationContext.GainNavigationAfterFrames(2, null);
            Current = this;
            ProbeLog.Write("abilities.movie_loaded movie=" + (movie != null));
        }
        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            LoadingWindow.DisableGlobalLoadingWindow();
            if (layer == null) return;
            // A Gauntlet button/inquiry callback must finish dispatch before its movie
            // can be detached. The frame callback owns the destructive transition.
            if (popRequested) { popRequested = false; Pop(); return; }
            if (closeRequested) { closeRequested = false; ProcessClose(); return; }
            if (layer.Input.IsHotKeyReleased("Exit")) Close();
            else if (layer.Input.IsHotKeyReleased("Confirm")) vm.ExecuteApply();
            else if (layer.Input.IsHotKeyReleased("Reset")) vm.ExecuteReset();
        }
        public void Close()
        {
            if (ScreenManager.TopScreen != this) return;
            closeRequested = true;
            ProbeLog.Write("abilities.close_queued");
        }
        private void ProcessClose()
        {
            if (ScreenManager.TopScreen != this) return;
            ProbeLog.Write("abilities.close_frame_dispatch");
            if (AnyUnsavedChanges())
                InformationManager.ShowInquiry(new InquiryData("Unsaved probe assignment", "Apply the assignment before returning?", true, true,
                    "Apply", "Discard", () => { ApplyChanges(); popRequested = true; }, () => { ResetChanges(); popRequested = true; }), false, false);
            else Pop();
        }
        private void Pop() { ProbeLog.Write("abilities.back"); Game.Current.GameStateManager.PopState(0); }
        void IGameStateListener.OnDeactivate()
        {
            base.OnDeactivate();
            DetachLayer();
            ProbeLog.Write("abilities.deactivated");
        }
        void IGameStateListener.OnFinalize()
        {
            // Match the shipped character screen: detach on deactivate, finalize the
            // VM on finalize. It does not explicitly ReleaseMovie during click dispatch.
            // Explicit release here was an unproven deviation from that lifecycle.
            DetachLayer();
            vm?.OnFinalize();
            vm = null; layer = null; movie = null;
            closeRequested = false; popRequested = false;
            if (Current == this) Current = null;
            ProbeLog.Write("abilities.finalized");
        }
        private void DetachLayer()
        {
            if (layer != null && layerAttached)
            {
                layer.IsFocusLayer = false;
                ScreenManager.TryLoseFocus(layer);
                RemoveLayer(layer);
                layerAttached = false;
                ProbeLog.Write("abilities.layer_detached explicit_movie_release=false");
            }
        }
        public bool AnyUnsavedChanges() => vm != null && vm.IsDirty;
        public bool CanChangesBeApplied() => true;
        public void ApplyChanges() => vm?.ExecuteApply();
        public void ResetChanges() => vm?.ExecuteReset();
    }

    public sealed class AbilitiesProbeVM : ViewModel
    {
        private readonly Action close;
        private int selected = 1;
        private static int applied = 1;
        public AbilitiesProbeVM(Action close) { this.close = close; selected = applied; }
        [DataSourceProperty] public string TitleText => "ABILITIES — ENGINE FEASIBILITY PROBE";
        [DataSourceProperty] public string DescriptionText => "Fighter / Whirlwind\nThis is an isolated navigation and binding test. It does not grant a combat ability.";
        [DataSourceProperty] public string AssignmentText => "Whirlwind: slot " + selected + (IsDirty ? " — unapplied" : " — applied (session only)");
        [DataSourceProperty] public string AssignText => "Change slot (1 / 2)";
        [DataSourceProperty] public string ApplyText => "Apply";
        [DataSourceProperty] public string ResetText => "Reset";
        [DataSourceProperty] public string BackText => "Back";
        public bool IsDirty => selected != applied;
        public void ExecuteAssign() { selected = selected == 1 ? 2 : 1; Refresh(); ProbeLog.Write("abilities.assignment_changed slot=" + selected); }
        public void ExecuteApply() { applied = selected; Refresh(); ProbeLog.Write("abilities.assignment_applied slot=" + applied); }
        public void ExecuteReset() { selected = applied; Refresh(); ProbeLog.Write("abilities.assignment_reset slot=" + selected); }
        public void ExecuteBack() => close();
        private void Refresh() => OnPropertyChanged(nameof(AssignmentText));
    }
}
