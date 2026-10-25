# Combat overhaul UI, navigation, input and camera investigation

Date: 2026-10-25. Static REA and supplemental decompilation followed by diagnostic campaign runs 66056, 40004, 40336 and 11956. **PASS for the revised basic Abilities destination, rendering, automated lifecycle and repeated mouse Back/reopen in run 11956. Keyboard Escape failed its attempted check; keyboard/controller acceptance remains open.** Earlier run 40336 crashed after physical Back; the revised teardown survived that sequence, but its original cause was not isolated. Eleven slots, production persistence, HUD and camera remain untested.

## Initial live navigation result

`evidence/combat-overhaul/runtime/ui/run-66056.jsonl` records the actual campaign handler creating `escape_menu,character_developer,inventory,mnbl_abilities,party,quest,clan,kingdom`, the additional item VM, and a completed paused campaign load. A narrow early OnCreateElements postfix thus inserted the distinct navigation identity in the installed build with NavalDLC loaded. The diagnostic aliases native character icon artwork; it does not prove custom icon loading or the final top-strip design.

The custom screen was not opened during run 66056. Later runs below supersede that limitation. All 195 save hashes remained unchanged at the earlier recorded check in `evidence/combat-overhaul/runtime/save-integrity.json`; that earlier receipt alone does not attest later runs.

## Live Abilities screen result — run 40336, post-Back crash

After the physical Back sequence described below, the game stopped consuming ticks/`quit.once` and displayed the application-error/file-collection dialog. The log ended at `abilities.finalized` at `07:59:35Z`. Seeing the map and finalization log was insufficient to establish successful return. The root agent is collecting crash evidence; the precise cause is unresolved. A reentrant teardown during Gauntlet click dispatch is a hypothesis under investigation, not a diagnosed cause. Basic navigation/rendering is established, but the lifecycle acceptance gate remains open until the physical close path survives and continues ticking in a fresh run.

### Revised teardown retest — run 11956

The revised screen queues Back for its frame callback, detaches the layer on deactivate, finalizes the VM on finalize, and omits explicit `ReleaseMovie` during teardown, matching that aspect of the shipped character screen. This combines two changes; the original crash cause cannot be attributed to either change independently.

The fresh run's log (`run-11956.jsonl`, initially recorded under `%LOCALAPPDATA%/MNBL.CombatProbe`, retained with the UI runtime evidence) records the entire automated scenario passing at `2026-10-25T08:08:36.5426601Z`. Root then physically opened and clicked Back, reopened, changed slot, applied, clicked Back again, and reopened a third time. The UI stayed responsive and slot 2 remained applied. Logged mouse-path closes at `08:09:37Z` and `08:10:06Z` show `close_queued`, `close_frame_dispatch`, `layer_detached explicit_movie_release=false`, deactivate and finalize; another successful open at `08:10:28Z` establishes activity more than twenty seconds after the second close. This supersedes the failed physical Back result for the revised implementation, without proving indefinite leak freedom or isolating the earlier crash.

The root agent's attempted keyboard Escape did not close the screen and produced no close log. Static inspection confirms the spelling is correct: `GenericPanelGameKeyCategory` registers `Exit` for `InputKey.Escape` and `ControllerRRight`; the shipped character screen also polls `IsHotKeyReleased("Exit")`. No speculative key-name change was made. Input delivery/focus and controller behavior still require a real successful test; mouse success does not close those requirements.

`evidence/combat-overhaul/runtime/ui/run-40336.jsonl` records the full opt-in `UiProbeScenario` passing at `2026-10-25T07:58:05.3529109Z`. It used the actual navigation element's `OpenView`, registered `AbilitiesProbeState` / `AbilitiesProbeScreen`, module-owned `CombatProbeAbilities.xml` and the game's state manager. It did not replace navigation with a debug modal.

Programmatic checks passed for initial clean assignment, changed binding text, reset, apply, Back to MapState, old movie/screen release, reopening into a new screen with the session assignment retained, and switching through the stock Character destination. Both real native unsaved-change inquiries were observed active and their captured affirmative/negative callbacks executed; reopening verified Yes applied and No discarded. The scenario finished back on MapState with the custom screen released. These checks do not prove a physical click on either inquiry option or exhaustive leak freedom.

Rendering is separately evidenced by the reviewed native screenshots `run-40336-abilities-open.png`, `run-40336-abilities-reopened.png` and `run-40336-abilities-unsaved-inquiry.png` in the same directory. The open screenshot shows the custom full-screen title, description, assignment text, four actionable buttons and the added active navigation item alongside the retained stock bar. The session-only diagnostic assignment is explicitly labeled; it does not grant a combat ability.

After automation finished, the root agent physically clicked the added navigation item, Change slot, Apply and Back, reviewing the desktop after each step. The visible assignment changed to slot 2 unapplied, then slot 2 applied, then returned to the map. The retained log corroborates this separate interaction sequence: open at `07:58:34Z`, assignment change at `07:59:00Z`, apply at `07:59:14Z`, and Back/deactivate/finalize at `07:59:35Z`. Native screenshots above cover the automated stages; the physical mouse observations were reviewed in root computer-use tool output. Thus the runner's deliberately conservative `physical_input_proven=false render_proven=false` log describes its own assertion scope, not the combined evidence.

**Installed variant discovered:** the active Character screen is `NavalDLC.GauntletUI.CharacterDeveloper.GauntletNavalCharacterDeveloperScreen` from `NavalDLC.GauntletUI`, MVID `f486b6a7-2e1b-4888-b640-0b3f60d12b68`. It derives from the Sandbox character screen and declares `[GameStateScreen(typeof(CharacterDeveloperState))]`. Run 40004 reached this transition but failed the harness's incorrect exact simple-name assertion. The corrected check in run 40336 verifies the active state, top screen identity against `state.GetListenerOfType<ScreenBase>()`, declared state registration, Sandbox base ancestry and custom-screen release; actual type details are logged. This was a test assumption failure, not an observed navigation failure.

**Production limits:** native character art is reused for the extra icon. Custom icon assets, mockup top navigation strip, character tableau/soul orb, eleven-slot ring, twelve abilities, multiclass/fork data, persistence across save/load, keyboard/controller focus/input, scaling, mission/save restriction cases, and compatibility with other UI mods are not proven by this diagnostic. Basic route and screen creation can proceed on observed evidence; those distinct requirements retain their own acceptance work.

## Evidence and identity

All paths below are relative to `D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord`. REA summary receipts and compact method signatures/IL hashes are retained in `evidence/combat-overhaul/ui/`; `method-index.json` records build-local tokens. Supplemental decompilation is in its `decompiled/` directory. It contains unresolved-reference annotations and is analytical text, not compilable source or permission to redistribute game implementation.

- `Modules/SandBox/bin/Win64_Shipping_Client/SandBox.View.dll`: SHA-256 `c7360e71da06799a0cb6c00c4a0cfab5877621197ba25101433ac28654d169e2`; navigation REA parent `ev_52b3378fced952a03daa82d175a69faa8d97e6fb0bf281f82b1be794179fabb9`.
- `bin/Win64_Shipping_Client/TaleWorlds.CampaignSystem.ViewModelCollection.dll`: SHA-256 `5d817a46456d07b5e4382a870795fd9f8d6f11b4707a91d8a8b5d5cd50e762a5`; navigation VM REA `ev_57f4f181639407abd064df97166abf2e2095c1a609a45bcfce5e4a0a1a6d43dc`.
- `Modules/SandBox/bin/Win64_Shipping_Client/SandBox.GauntletUI.dll`: SHA-256 `69f2a87c3c3883d04ece46200c5cddf39c188f0ee7c20e26c47fcf64c0f97ed3`; character/global-layer REA `ev_4beb0f40166f469885de44799dfd8580fce0d8105582aa6e9d73c5decb29f259`; map-bar factory REA `ev_d0153e7ceda3bca7fd02765cbfdf2b374cd36335980a0d83afbcdcaf6d6d635f`.
- `Modules/Native/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.View.dll`: SHA-256 `ffaac5ed867cd299f8c14f58ffee64f86e50d8b197db5f966759524c3e574ba1`; mission controller/camera REA `ev_ed3cdae671429e19c5d6bf1d5faea81990e155ae34b0bd813d672a1ba8ec97d9`.
- `Modules/Native/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.GauntletUI.dll`: SHA-256 `75b070e0b1347c3f6c7e4526edc5370bba18b4d4b4747c115669e23477ad44e2`; HUD REA `ev_d5379cd57cc84952fcb90f62aed383ea34e73845930bf8c29ac4f3e893b13aae`.
- `resource-hashes.json` pins the inspected MapBar prefab, MapBar brushes and CharacterDeveloper prefab. The REA summaries retain MVID, selected type names, counts and coverage limitations.

## U1: A new campaign destination has an actual extension seam

**Observed:** `SandBox.View.Map.Navigation.MapNavigationHandler.OnCreateElements` (token `0x06000472`, IL hash in method-index) is protected virtual and constructs seven `INavigationElement` entries. Its constructor calls this method and stores the resulting array. `GetElement(string id)` searches by `StringId`; it is not restricted to an enum for this route.

`MapNavigationVM` constructs its `MBBindingList<MapNavigationItemVM> NavigationItems` by iterating `navigationHandler.GetElements()`. Each item copies `NavigationElement.StringId` into `ItemId`, derives enabled/active/alert states from the element, and its `ExecuteOpen()` calls `NavigationElement.OpenView()`.

`Modules/SandBox/GUI/Prefabs/Map/MapBar.xml` lines 64–70 enumerate `{MapNavigation\NavigationItems}`. The button binds `IconID="@ItemId"`, `Command.Click="ExecuteOpen"`, `IsEnabled="@IsEnabled"`, `IsSelected="@IsActive"`. A second mock-navigation list also enumerates the items. This is stronger evidence than a guessed hardcoded button patch.

**Constraint:** `GauntletMapBarView.CreateLayout()` directly constructs `new MapNavigationHandler()` and passes it to `new GauntletMapBarGlobalLayer(...)`; there is no separate overridable handler factory in that inspected class. `MapScreen.NavigationHandler` rejects replacement of a non-null handler with a different non-null handler with the diagnostic "Navigation handler should not be changed after map bar initialization". Replacing only the screen property late would also leave the global layer/VM holding the original handler.

**Proven diagnostic implementation:** a narrowly scoped postfix on `MapNavigationHandler.OnCreateElements` inserts one custom `AbilitiesNavigationElement` after inventory while retaining all existing entries and a unique ID. This happens during construction before the VM snapshots the array. Runs 66056 and 40336 validate this route with the recorded module list. A registered `MapBarView` override remains an untested alternative; it is not required by this result and has a larger compatibility surface because the shipped Gauntlet class already bears `[OverrideView(typeof(MapBarView))]`.

**Still unknown:** coexistence with UIExtenderEx and other mods modifying the same handler, duplicate override precedence, custom icon style resolution and a production code-generated prefab route. The runtime pass establishes the recorded Harmony/module combination and module-owned XML load, not arbitrary mod load orders.

## U2: A real screen and navigation lifecycle are separate from a button

`CharacterDeveloperNavigationElement` is the reference contract: `StringId`, `IsActive`, `IsLockingNavigation`, `HasAlert`, `Permission`, tooltip and `OpenView`. It checks `MapNavigationHelper.IsNavigationBarEnabled` and `Mission.IsCharacterWindowAccessAllowed`. Before switching, it honors `IChangeableScreen.AnyUnsavedChanges()` / `CanChangesBeApplied()` and routes through the game's apply/discard inquiries.

`MapNavigationHelper.SwitchToANewScreen` (token `0x06000479`) pops the current game state when not on MapScreen, then calls the open action. `IsNavigationBarEnabled` checks saving, dead player, battle simulation, escape menu, army/marriage/heir/cheat/incident/encyclopedia UI and every element's `IsLockingNavigation`. An Abilities screen that just pushes a screen from an arbitrary button would bypass this behavior.

`GauntletMapBarGlobalLayer.OnTick()` keeps the global bar enabled when its handler reports any element active, including an added element; otherwise it disables it outside MapScreen. `HandlePanelSwitching` has explicit existing panel hotkeys. Therefore a new destination can participate through `IsActive`, but its own hotkey/controller behavior is additional work, not automatic enum registration. The existing `MapNavigationItemType` / extension methods represent stock destinations; do not append a fictional enum value as an API extension.

`GauntletCharacterDeveloperScreen` uses `[GameStateScreen(typeof(CharacterDeveloperState))]`, implements `IGameStateListener` and `IChangeableScreen`, creates a GauntletLayer on activate, loads the movie, registers generic panel/campaign key categories, adds layer and sets focus. It removes layer and focus on deactivate, finalizes VM and unloads sprite category on finalize, and returns through `GameStateManager.PopState`.

**Recommended architecture:** custom `AbilitiesState`, registered `AbilitiesScreen`, explicit `AbilitiesNavigationElement`, one VM/service boundary for draft changes and persistence, and a module-owned movie/brush/sprite set. The actual supplied mockup has a top navigation strip; merely adding the map-bar icon is not visual completion. The Abilities movie needs that strip and wiring to existing destinations; whether it is also required on every vanilla panel remains a scope decision, not proven by a map-bar extension.

**Probe:** new destination opens from campaign and returns to it; switch Character -> Abilities -> Inventory with both valid and invalid pending changes; gamepad focus, Escape/Back, save lock, mission restrictions and repeated opens; check exactly one state/layer and one navigation item after reload. Confirm game discovers custom GameStateScreen attributes and custom XML in the actual deployment without substituting debug-only launch.

## U3: HUD is a multi-purpose existing layer

`MissionGauntletAgentStatus` overrides `MissionAgentStatusUIHandler`. `EarlyStart()` constructs `MissionAgentStatusVM`, creates `GauntletLayer("MainAgentHUD", ViewOrderPriority, false)`, loads `MainAgentHUD`, adds it to MissionScreen, registers interaction events, combat log and managed-option subscriptions. It handles interaction messages and damage visuals as well as health. Its tick hides interaction text when radial/order UI is active. Finalize removes layer/subscriptions.

**Implication:** replacing the whole status view to move the health bar may remove interactions, damage feedback, deployment/suspension behavior or weapon-related updates. First prove a module-owned combat layer that owns the ring and health presentation while selectively hiding only superseded stock widgets for eligible player mode. Record exact hidden widgets and restore them on disable; preserve interaction/equipment/damage functions. An override route needs explicit feature parity, not just visual screenshots.

**Ring work:** eleven wedge positions and mouse hit regions, focused slot navigation, cooldown overlay, default/RT/LT group highlight, device-specific prompts and held-heavy center treatment are distinct implementation requirements. A circular picture with eleven buttons nearby does not meet the mockup. Author original sprites/brushes with stable module names; test actual Gauntlet clipping, DPI/scaling, ultrawide and gamepad selection rather than assuming a web prototype transfers.

## U4: New input competes with a broad stock controller

`MissionMainAgentController.OnPreMissionTick` (token `0x06000494`) calls `InteractionComponent.FocusTick`, `FocusedItemHealthTick`, `ControlTick`, `FocusStateCheckTick` and `LookTick` when active. `IsDisabled` gates all these, not only directional combat. Blindly disabling this controller loses more than attacks.

`ControlTick` (token `0x0600049d`) sets `MovementFlags` from game keys; observed attack key ID 9 calls `AttackDirectionToMovementFlag(GetAttackDirection())`, defend key ID 10 sets defend direction flags, while movement, jump, weapon selection, dismount, kick and interaction/lock behavior coexist in the method. It checks `Mission.IsOrderMenuOpen`, `MissionScreen.IsRadialMenuActive`, mouse visibility and a cached `_isPlayerOrderOpen` from `MissionPlayerToggledOrderViewEvent`.

**Required spike:** determine a narrowly scoped input arbitration seam that suppresses stock attack/defense flags for opted-in melee while retaining movement/look/use/mount behavior. A postfix that clears flags may race native consumption or other views; instrument order before choosing it. Controller RT/LT are analog and have existing weapon semantics; modifier press/release and held-heavy thresholds need hysteresis and no action leakage. Suppress ability activations while order menu is open from both current mission state and event transition; discard queued edges on reopening gameplay.

**Acceptance:** record vanilla movement flags and custom commands over tap, hold, release, both triggers, order open/close, menu focus, gamepad disconnect, mount/dismount and death. Exactly one cast per press and zero vanilla attacks during custom action are required. Verify jump survives unmodified A and modified A never also jumps. Default keyboard ability keys clash with formation commands, so order suppression must be verified in the real troop UI.

## U5: Native-style camera pipeline has clamps and useful offsets

`MissionScreen.HandleUserInput(float)` (token `0x06000327`) processes stock camera distance keys 28/29 and unconditionally clamps `Mission.CameraAddedDistance` to `[0.7, 2.4]` in that path. Merely assigning a larger value each frame is not a reliable wide battlefield camera design.

`MissionScreen.UpdateCamera(float)` (token `0x06000321`) consumes `Mission.CustomCameraFixedDistance`, `CustomCameraTargetLocalOffset`, `CustomCameraLocalOffset`, `CustomCameraLocalOffset2`, `CustomCameraGlobalOffset`, rotational offsets and `CustomCameraIgnoreCollision`. It includes mounted/spectator/body/collision handling and smoothing. `CheckForUpdateCamera` (token `0x0600031f`) has a separate CustomCamera branch; `AllowInputWithCustomCamera` affects input processing. `ICameraModeLogic` is consulted for camera lock mode, not evidence of a complete replacement transform interface.

**Candidate:** first test fixed distance + local target/shoulder offsets through Mission's existing setters, retaining collision and stock look. Establish exact order relative to HandleUserInput and UpdateCamera. If that cannot produce the requested elevated far zoom, use a bounded camera override and explicitly own collision, aiming alignment, look input and mount/death transitions. Do not just bypass collision or add a per-frame magic camera position.

**Probe:** foot/mount near/far right-shoulder framing, walls/ceilings, dismount, first-person preference, spectator/death and campaign return; verify aim ray equals displayed reticle and Back+right-stick does not trigger native actions. Track distance/elevation and verify transient zoom bar visibility. Save/restore every changed camera setting on disable/teardown.

## Recommended ticket expansion (all sprint 1)

1. **Navigation insertion feasibility — basic route passed:** retain the observed early postfix and unique identity from run 40336. Production work now needs patch signature/IL guard and other-mod coexistence checks; do not repeat basic route exploration as if unresolved.
2. **Abilities state/screen lifecycle — revised basic route passed:** run 11956 passed automation and repeated physical Back/reopen with continued activity after teardown. Preserve the failed run and revised lifecycle evidence; the exact original crash cause remains unisolated. Production draft/persistence, dirty stock-screen-to-Abilities transitions, invalid pending changes, restrictions, sustained leak checks and keyboard/controller remain separate work. Escape specifically failed the attempted input check and needs diagnosis.
3. **Abilities navigation presentation:** module icon brushes plus mockup top strip, localization and controller route. Explicitly decide which stock screens receive the top strip; do not silently interpret a map icon as the requested tab.
4. **Abilities VM and character presentation:** authoritative loadout/progression adapter, twelve learned/eleven equipped, selection/assignment and test-only fork choices; CharacterTableauWidget binding to actual hero body/race/equipment and soul-orb layering. Test resize/focus and save/load separately from navigation.
5. **Ring rendering and interaction assets:** eleven wedges, cooldown masks, four mocked states, center icon animation, keyboard/controller hints; original atlas/brush pipeline and real-game screenshot acceptance.
6. **HUD coexistence/lifecycle:** selective stock-widget suppression, preserve interactions/damage/equipment, activation/death/deployment/teardown and mode restore.
7. **Input ownership feasibility:** frame-order instrumentation and stock-controller suppression; prove movement/look/use/mount preserved before production mapping. Gate attack/control wiring on this outcome.
8. **Production input mapping and conflict tests:** analog trigger handling, hold-heavy state machine, all eleven keys, troop UI suppression, focus/device transitions and no delayed casts.
9. **Camera feasibility:** establish collision-preserving shoulder/elevated-zoom route and measure stock clamp/order; gate production camera.
10. **Production camera and visual acceptance:** full foot/mount/death/focus restore, reticle agreement, transient zoom indicator and actual mockup comparison.

These should not become ten cosmetic checklist items under one ticket. Navigation insertion, input ownership and camera feasibility are separate engine-contract gates; UI assets, state lifecycle and gameplay data integration have different deliverables and tests. Runtime unknowns remain explicit until those probes run.
