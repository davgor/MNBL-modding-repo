# Combat overhaul: engine contracts and implementation plan

2026-10-25. Dark Mechanicus epic `ep_01m5rsw4xfjkvkk6t30j7bm6ds`.

**The bulk-implementation gate remains closed; the live probes now establish concrete working routes.** Original custom animation cold-load/playback/restoration passed (30840). Native unguarded cleave hit three agents once per swing in both directions (39720, repeated in later runs). Earlier native crush-through policy passed a left swing through three guarding shields with reduced body damage (46400); the close-range right swing still bypassed that policy and stopped. A real Abilities destination renders and supports assignment/apply/back/reopen; a physical Back crash in40336 was followed by corrected teardown and successful automated plus repeated physical transitions in11956. Keyboard Escape delivery, controller coverage, mounted/interruption/custom-animation damage, close-range shield handling and broader production criteria remain open. These are diagnostic proofs, not acceptance of the whole combat epic.

## Evidence inspected

- [Native and managed combat findings](combat-native-findings.md): fresh REA managed inspection, earlier Ghidra callback/job analysis matched to the current native file, and a fresh byte comparison of all 500 retained collision-job instructions.
- [Animation findings](combat-animation-findings.md): installed action/clip resources, supplied skeletons, authoring tools, native-wrapper contracts and the official import workflow.
- [Navigation, HUD, input and camera findings](combat-ui-findings.md): current managed methods and actual shipped Gauntlet prefabs/brush bindings.
- [Selected combat method receipt](../../evidence/combat-overhaul/combat-methods.json): current assembly SHA/MVID, exact build-local tokens, CIL hashes and anchors. Parent REA `ev_75287ed09fa9c7510802a288da5a12e0ea7d2e63a6c4c7ab1ddb57c3c17beb36`.
- Prior local research in `evidence/engine-extension/`, `evidence/character-overhaul/`, and `build/research-baseline/` was consulted. The deleted historical DD2 combat plan remains deleted. Its claims of native attack reuse were explicitly unverified; its stamina, control scheme and vanilla-AI choices do not carry into this ESO-like epic.

Reference build: Bannerlord v1.4.8.119303 with War Sails v1.2.8.119303. MountAndBlade DLL SHA `19387f31557ff840d14f378f6bbdf1d58fcff406ab9ff2294dfbb3e49a50b87e`; native DLL SHA `2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a`. These are evidence identities, not a guarantee for other builds. Revalidate patch contracts after updates.

## What comes with the engine

**Combat contact callback.** The native job calls a typed managed callback with collision data, remaining momentum and collision reaction by reference. Existing managed models choose collision reaction and momentum. This is a credible narrow interception route; a native detour is not currently demonstrated to be mandatory.

**Damage and attribution.** `Agent.RegisterBlow` reaches managed health/death/reporting and native reaction handling. Retaining that pipeline can preserve more game behavior than directly subtracting health. However, it is not identical to the private mission registration path; event and particle parity must be deliberately accounted for.

**Animation assets and playback.** Supplied human/horse FBX skeletons, the editor, action types, inherited action sets, clip queries and action-channel playback already exist. Current action identity/progress and bone-transform queries provide instrumentation. They do not prove that a newly authored clip generates valid weapon contacts.

**Campaign navigation.** The map navigation handler returns `INavigationElement` objects, and the VM/prefab enumerate them. A custom destination does not require rewriting every stock panel. A first-class Abilities state/screen and its navigation/permission/focus lifecycle still need implementation.

**Camera and save hooks.** Existing camera offsets/fixed-distance properties and `CampaignBehaviorBase.SyncData` are useful foundations. Camera input clamps one stock distance property, while native cached action IDs and Agent indices are unsuitable persistent save identities.

## What must be overridden or added

**Continuous attacks and shields require separate policies.** Incoming `ContinueChecking` skips the managed damage body; setting that value at entry is not cleave. Working native cleave preserves ordinary damage, then changes outgoing reaction and momentum together. Late shield-flag conversion alone failed: native collision records still scheduled BlockedMelee. `Mission.GetDefendCollisionResults` exposes earlier `crushedThrough`/attacker-stun outputs; the scoped override allowed native shield-to-body continuation in the left-swing probe. Retain the defense fact for mitigation on the later real body contact. Preliminary crush contacts do not consume the damaging-hit ledger. Do not claim the progress-zero blocked path is covered by this seam.

**Animation playback is not attack execution.** Controller.None plus explicit stock release actions animated but produced zero contacts in42180. Adding AgentHumanAILogic and an owned AgentComponent.OnAIInputSet callback produced real native release/contact in39720, excluding shared hardware input. Production player controls still need selective ownership; this deterministic AI fixture does not prove the player-controller replacement.

**UI destruction must be tested through real widget input.** Automated navigation alone passed while physical Back later crashed. The revised probe queues Close for frame dispatch, detaches the layer on deactivation and finalizes the VM with the state, without explicitly releasing the movie inside the button callback. This combined change survived repeated physical Back/reopen in11956; no crash stack isolated a single cause. NavalDLC replaces the character screen with a registered derived type, so lifecycle checks must validate actual state/listener/registration rather than one simple class name.

**Duplicate suppression belongs to the mod.** The inspected native continuation branch can remove a victim index from a history array. Its full runtime semantics are unknown. Use attack-instance + phase + victim-generation identity; retain only intended multi-hit phases. Prove actor-index reuse, interruption, shield/body repeated contacts and mount transitions.

**New controls need selective ownership.** `MissionMainAgentController` handles movement, look and interactions as well as directional combat. Disabling it wholesale is incorrect. Instrument frame order and suppress only owned attack/defense behavior. Preserve A jump, consume modified A, support analog-trigger hysteresis and discard queued ability presses when troop commands close.

**Custom action sets must remain opt-in.** Namespaced module actions/sets should preserve native inheritance and locomotion. `SetActionSet` requires full `AnimationSystemData`, including monster/bone configuration, not only a string. Store and restore the correct actor state. Do not edit all warrior mappings globally.

**Abilities is several systems.** Insert the navigation element before the handler's array is copied into the VM; late handler replacement is explicitly rejected. Add state/screen lifecycle, pending-change transactions, top navigation strip, hero preview, orb/ring layers, actual wedge interaction, sprites/brushes and progression/fork bindings. Merely showing a button or debug modal fails.

**HUD and camera need feature preservation.** Stock MainAgentHUD also owns interaction and damage feedback. Hide only superseded widgets or prove complete parity. The stock camera clamps added distance to 0.7–2.4; prove fixed-distance/offset control first, or explicitly own a bounded replacement with collision and reticle alignment.

**AI needs an authority handoff.** Setting every hero's Controller to Player has formation/player side effects. Sprint 2 begins with selective native combat-suppression proof while retaining navigation. The player command validators, animation clock, cooldowns and defense rules remain shared.

## Long sprint 1, with ordered internal gates

The player sprint remains the full requested delivery. The internal stages are dependency boundaries, not reductions of its scope or extra release sprints.

1. **Contracts and diagnostic infrastructure.** Pin actual binaries, patch surfaces, model composition and asset resources. Create an isolated, restorable runner with callback/action/HP/XP telemetry and vanilla controls.
2. **Engine feasibility.** Prove native continuation/momentum, shield conversion and pre-damage block interception; an original Blender-to-game clip; scoped action-set registration; foot/mounted channel ownership; actual new navigation insertion; selective input ownership; collision-safe camera. The technical gate accepts only an integrated original-animation/contact demonstrator and a documented implementation decision.
3. **Core production services.** Implement exactly-once damage/credit, validated hit geometry, action-progress event clock, mode lifecycle, ability definitions and fork-capable progression/save contracts. Implement basic/heavy/guard art and input ownership against those decisions.
4. **Content and UI production.** Three foot animation batches and three mounted batches account for all twelve abilities in each context. Six two-ability gameplay integrations bind actual art to effects and timing. Navigation/state, top strip, hero preview, ring widgets, HUD and assignment VM have separate owners.
5. **Full player acceptance.** Cold-installed resources, save/fork migration, MCM, complete mission scenarios, real controller/keyboard operation and final performance/regression evidence. Early diagnostic success does not substitute for playing the complete fighter.

Sprint 2 extends the accepted runtime to named characters, starting with AI ownership. Sprint 3 adds new elite troop kits and final mixed-army acceptance. Later multiclass content and actual Fighter forks are not required; their data contracts and a fork fixture are.

## Unknowns that block affected production

- **C1/C2:** Native continuation across all required attack contexts, shield/body ordering, repeat contacts and momentum. Trace callback inputs/outputs, contact order, health/shield changes and IDs; compare native baseline and each intervention separately.
- **C3:** Exactly-once downstream events, campaign casualties, skill/ability XP and reactions when using a synthetic blow. A health-change screenshot cannot pass this.
- **C4/A3/A4:** Custom animation collision eligibility, channel arbitration, thread affinity, action rejection, blended bone spaces and frame-independent contact windows. A `SetActionChannel=true` result cannot pass this.
- **A1/A2/A5/A7:** Blender exporter/rig compatibility, module XML merge, cold TPAC loading and mounted gait/IK. Editor/file presence cannot pass this.
- **U1/U2:** Actual early navigation extension and GameStateScreen discovery, load order, pending changes and controller focus. Static prefab enumeration cannot pass this.
- **U4/U5:** Input consumption timing and collision-safe elevated camera route. Clearing flags or repeatedly assigning zoom values without a trace cannot pass this.
- **C5:** Actual installed model/patch-owner chain and actor-scoped fallback, especially Naval momentum overrides. Static registration order alone cannot pass this.

Use the specific probe definitions in the three linked reports. Run bounded experiments together where their setup is compatible and reuse evidence for identical package hashes. Avoid redundant game launches and repeated broad scans. The shared-installation lock and process checks in `docs/runbooks/development.md` apply to every deployment/launch.

If supported managed seams fail a required behavior, record the failing native boundary and pursue a bounded native patch or an owned sweep/damage adapter, with exact ABI guards, rollback and the same acceptance matrix. Neither failure authorizes removing the behavior nor silently changing the project to a different engine. No unconditional detour or direct raw-offset patch is justified by the current evidence.

## Confidence and completion boundary

Confidence is high that these extension points and constraints exist in the inspected build, including the byte-checked native job. Confidence in the complete new combat system remains conditional on the early runtime gates. The plan is specific enough to expose failures before bulk content production; it is not evidence that custom clips, the new tab or mixed combat have already worked.

The authoritative ticket graph and per-ticket acceptance live in Dark Mechanicus. This report records research and architecture rationale; it is not a second implementation-status ledger.
