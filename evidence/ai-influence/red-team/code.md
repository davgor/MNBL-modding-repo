# Code lens: FWGC++ source lessons missing from the AI Influence plan

Reviewed 2026-10-25. Scope: immutable epic revision 2 in `build/research-ai-influence/red-team-plan.json`, against FWGC++ commit `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`. FWGC++ was clean when inspected. This is a plan review, not a claim that the future rewrite has these implementation defects. No inference, training or build suite was run.

## C1 — Require a reply gate before text reaches the player or becomes memory

MMR-313 validates tool effects; MMR-315 separates truth from claims; MMR-316 requests grounded dialogue and honest action outcomes. Those are useful but do not assign a mandatory, executable check to the final prose or streaming display. A model can choose a legal tool and then falsely say a treaty was accepted, claim a shared past, or reveal knowledge its actor lacks. Correct retrieval does not establish that the response uses it correctly. Saving that prose as an utterance is legitimate; promoting its assertions into authoritative memory is not.

FWGC++ contains the missing mechanism explicitly: `communicator/src/truth.cpp:617-663` checks named claims against the packet and recent conversation; `communicator/src/authored.cpp:1558-1564` skips authored lines that fail this check. Crucially, `communicator/src/voice.cpp:674-683` merely returns generated text and can deliver pieces through a callback: it does not invoke the truth checker. `communicator/eval/main.cpp:950-954` describes the game-owned fallback and measures unsupported claims. Therefore reusing the voice API alone does not supply the integration gate. `communicator/README.md:65` assigns that gate to the caller.

Suggested Sprint 1 patch to MMR-316, with MMR-355/315 linkage: the game integration validates structured factual claims, actor knowledge, commitment/action status and displayed outcome against the request's fact packet plus current commit receipts before display; unsupported or stale claims trigger bounded regeneration or a deterministic fact-rendered fallback. Do not show unchecked partial prose as established facts. Persist the delivered utterance and validation outcome; its claims remain attributed claims unless independent game events establish them. Design an explicit representation of intended lies/rumours so an intrigue character may knowingly deceive without rewriting authoritative truth. Define what the guard can and cannot detect rather than promising a universal semantic truth oracle.

Acceptance: paired supported/unsupported replies for treaty acceptance, prior dealings, identity, secrets and failed actions; a streaming reply that later invents success cannot display that success; fallback itself is grounded; intentionally deceptive speech remains attributed and produces only configured belief effects. Sprint 2 reports raw factual error, post-gate error, fallback frequency and false rejections separately. A guard that silently substitutes prose must not make a poor model look reliable.

Falsification: an existing revision-2 ticket that explicitly owns the pre-display/post-generation claim gate and those failure semantics would close this gap; generic grounding or tool validation does not.

## C2 — Prune choices before generation, not only reject invalid actions afterward

MMR-313's capability registry and validation correctly reject unsupported actions. MMR-314 adds negotiated tool formats. Neither explicitly requires the model-visible candidate set and target choices to reflect current actor permissions, current state, and request-local knowledge. A legal schema containing every possible action still burdens a small or compressed model with irrelevant choices, even when post-validation protects the world.

FWGC++ implements this at `communicator/src/interpreter.cpp:122-137`: acts are offered only when context permits them, including a frightened speaker for calming and nameable targets for targeted actions. `interpreter.cpp:297-313` both constrains generation and rechecks the selected action. `communicator/src/reaction.cpp:51-52` constructs a grammar from `brief.open`; lines 108-122 refuse an empty menu, constrain the completion and recheck membership. `orchestrator/src/director.cpp:2347-2351` independently refuses a reaction that is no longer open. These are separate layers, not interchangeable promises.

Suggested Sprint 1 patch to MMR-313/314: generate a bounded, request-scoped menu of admissible tools and resolvable targets from capabilities, knowledge and current state; preserve an explicit no-op/clarify choice; use provider-supported constrained decoding or an explicit tested compatibility strategy, then revalidate at commit. Do not import FWGC++'s small fixed dialogue-act vocabulary as a permanent ceiling on full FWG world interaction. Menu construction must retain all required legal options and provide bounded discovery for large worlds.

Acceptance: magic-off worlds, unavailable diplomacy, absent targets and stale permissions alter the offered set; a change during inference still fails commit safely; unrelated capabilities do not crowd out a required action; empty menus abstain. Sprint 2 compares full versus admissible menus under matched task coverage and reports tool selection, rejection/retry rate, latency and raw versus constrained reliability for parent and LittleBit variants. MMR-326 already asks for raw versus constrained-decoding gains: retain that existing requirement and make its implementation path explicit.

Falsification: a revision-2 requirement that explicitly binds the model-visible schema/menu to current admissibility, rather than merely rejecting proposals afterward, would close this gap.

## Adversarial review and limitations

- These are source-to-plan comparisons. No live model output, current executable behavior, or universal effectiveness of a lexical truth guard was established. The checks in `truth.cpp` cover named claim classes; passing them does not prove all semantic claims true.
- Source comments report historical classification losses from overbroad action menus. Their numerical results were not reproduced here and are not used as quantitative evidence in these findings.
- Existing plan requirements already cover authoritative facts versus beliefs, scoped secrets, deterministic graph replay, stale commits, provider failures, bounded queues and constrained-decoding evaluation. Those are not reported as missing.
- A direct mandate to adopt FWGC++ wholesale was dropped: its source supplies tested design mechanisms, but its small dialogue contract and English phrasing restrictions are not automatically suitable for Bannerlord/FWG politics and magic.
- Player confirmation is a separate consumer-ownership issue (`interpreter.cpp:202-211`, `352`); routed to the coordinator for product/gamer review rather than filed twice here.
- No claim is made that `Voice::speak` violates its contract: FWGC++ explicitly assigns validation to its caller. The gap is ensuring the new integration also owns that step.

## Executed inspection

From FWGC++: `git status --porcelain` (empty), `git rev-parse HEAD` (pinned commit above); `rg -n 'untrue|fallback|speak\(|confirm|open_acts|available|calm|grammar|Interpretation' communicator/src/interpreter.cpp communicator/src/voice.cpp communicator/src/authored.cpp communicator/src/truth.cpp communicator/src/reaction.cpp`; bounded numbered reads of each cited source location; `rg -n 'untrue\(' communicator/eval/main.cpp communicator/include`.

From MNBL: parsed the frozen JSON with PowerShell `ConvertFrom-Json`, read full bodies and acceptance criteria of MMR-310, 312-317, 322, 325-326, 354-356; searched the full snapshot for truth/grammar/confirmation/admissibility requirements. One initial anchor search used the FWGC++ working directory for the MNBL snapshot and failed with file-not-found; it was rerun from MNBL. No conclusion relies on that failed search.

Index: code — C1 pre-display factual-claim gate and fallback; C2 request-scoped admissible tool/target menus; proposed Sprint 1 implementation and Sprint 2 measurement additions, pending coordinator integration.
