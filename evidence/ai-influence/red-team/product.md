# Product lens — player intent at the game boundary

Reviewed 2026-10-25. Target: epic revision 2, `build/research-ai-influence/red-team-plan.json`, hash `59cddddc2183487bb66984f720f3470d80fa611ae4b677c20e2c9c68948a3280`; FWGC++ HEAD `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`. Read-only source review; no game or model run. Filing mode: file; coordinator owns epic/index.

## P1 — Actor authority is not evidence of the player's intended action

The plan validates actor authority and state in MMR-313, shows pending/rejected actions in MMR-316, and requires consent for linked peace/territory/payment packages in MMR-318. It does not assign a general player-intent confirmation contract to the UI/execution boundary. A legal quest, order, transfer or magic action can still be the wrong interpretation of an ambiguous player sentence. This blocks confidence that an otherwise valid model proposal expresses the player's choice.

FWGC++ explicitly puts this responsibility on its consumer: `communicator/README.md:54` says the game asks the player and passes the act to the director only on yes; `communicator/AGENTS.md:178` states the binding must honor `Interpretation::confirm`. The public field is documented in `communicator/include/communicator/interpreter.hpp:30`; `communicator/src/interpreter.cpp:202` emits questions for world-changing acts. `training/README.md:81` reports a model interpreting “ok”, with work held and no new offer, as taking work; the confirmation step was the protection. That reported experiment was not rerun here.

### Suggested plan changes

- MMR-310: define an action-specific player-intent policy covering interpreted player commands, negotiated commitments and autonomous NPC acts. Specify which consequential player actions need a concrete confirmation and which are covered by an explicit bounded delegation. Do not impose player confirmation on ordinary autonomous NPC behavior.
- MMR-313/MMR-316: show exact actor, target, terms/cost and consequence before committing confirmation-required proposals. Bind approval to the proposal ID, campaign and validated terms/state; changed terms or expired proposals invalidate it. Reject/cancel leaves no effects. A model-generated “yes” or inferred assent from unrelated dialogue is not approval. Keep actor capability checks independent of player intent.
- MMR-324/MMR-325: exercise ambiguous “ok”, negation, quoted instructions, changed targets/terms while the panel is open, cancel/retry and permitted NPC autonomy. Assert zero unconfirmed player-side effects for confirmation-required cases; score unnecessary confirmation and failure to execute authorized actions separately.

### Executed verification

Read the cited README/header/source ranges and ran `rg -n 'confirm|world-changing|binding|Taking|giving up' communicator/README.md communicator/AGENTS.md communicator/include/communicator/interpreter.hpp communicator/src/interpreter.cpp training/README.md` in FWGC++.

Parsed the immutable JSON with PowerShell `ConvertFrom-Json`, searched ticket bodies plus acceptance text for `confirm|confirmation`, and separately for `consent`. Confirmation had no matches; consent matched MMR-318. Positive control: the MMR-318 text matched `explicit consent` as true. This is a bounded text/ownership audit, not a claim that a keyword search proves runtime behavior. Read all relevant authority/UI/quest/initiative/save criteria before narrowing the finding.

### Adversarial review and limitations

No current MNBL implementation fault or observed unwanted in-game action is established. The missing piece is an explicit, testable plan requirement. “Consent is absent everywhere” is false: MMR-318 already owns linked-package consent. FWGC++'s world-changing classification covers its own quest contract; that classification must not be blindly copied to Bannerlord's wider action surface. This review does not prescribe a prompt for every game action.

## Dropped/refuted candidates

- Missing fallback owner: MMR-314, MMR-316 and MMR-319 already require offline/disabled/failure playability. Source lens separately sharpens pre-display claim validation; not duplicated here.
- Missing model-switch persistence: MMR-355 already prevents silent commitment/personality resets; MMR-323 binds configuration to saves and MMR-329 owns installation/manifests. No independent product gap established.
- Missing full consent: narrowed to player-intent interpretation outside the existing linked-package requirement.

Index line: `product — P1 player-intent confirmation/delegation contract at UI→execution boundary; retain independent NPC autonomy and existing linked-package consent.`
