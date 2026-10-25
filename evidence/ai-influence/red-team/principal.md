# Principal lens: FWGC++ integration contracts

Reviewed saved epic revision 2 (`rv_01m5rtw43cadsyx88e11x6wxtk`), snapshot `build/research-ai-influence/red-team-plan.json`, against clean FWGC++ HEAD `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`. This reviews the plan, not a completed implementation. No model, build or campaign test was run.

## P1 — Choose the FWGC++ reuse boundary before implementing a second director

The plan defines generic core APIs (MMR-310) and versioned projected facts (MMR-312), but never decides whether it imports, ports or only learns from FWGC++'s director, communicator and inference components. That choice matters: `orchestrator/include/orchestrator/fwgc_director.h:3` says the native director owns a complete world; lines 13–19 specify blocking calls and reconstruction-based saves tied to compatible builds. Lines 46–63 define API version 10, while `orchestrator/include/orchestrator/contract.hpp:76` defines packet contract 31. These are different versions, not one compatibility number. The native clock is days since founding with sunrise at hour zero (`fwgc_director.h:92`), not an implicit Bannerlord timestamp. The plan's world projection instead combines compiled genesis data and live TaleWorlds state (snapshot lines 517 onward, MMR-312).

A blind DLL reuse could create two simulation owners; a managed rewrite could silently lose already-tested semantics. Neither is an observed defect yet: the missing piece is a concrete decision and compatibility gate.

**Patch MMR-310/312:** inventory FWGC++ director, packet/brain schema, communicator and inference as reuse/adapt/reimplement/reference-only, with rationale. Explicitly select one owner for each world mutation, knowledge projection, NPC clock and dialogue outcome. Specify source-to-campaign ID and time conversion, API/packet/brain/save version negotiation and rejection/migration policy. If binding the native director, test UTF-8, allocator ownership, handle lifetime, status/poison recovery and off-thread long calls against the pinned ABI; if managed, use pinned cross-implementation contract fixtures instead. No parallel uncoordinated world simulation.

**Patch MMR-323:** persist accepted nondeterministic decisions and validated compaction outcomes as inputs; replay/restore with every inference provider unavailable must reproduce authoritative state and commitments without generating replacement decisions. FWGC++ explicitly serializes chosen reaction and compaction outcomes (`orchestrator/src/c_save.cpp:106`, `:130`, `:248`, `:263`) and rejects incompatible save/contract versions (`:300–307`). This sharpens existing deterministic replay acceptance; it does not replace it.

**Falsification:** an existing explicit integration decision and exercised version/identity/time contract would close P1 without adding another implementation subsystem.

## P2 — A negotiated tool format is weaker than an inference capability contract

MMR-314 requires one local completion using a negotiated tool format; MMR-328 later integrates factorized LittleBit serving. FWGC++'s existing runtime is a C++ GGUF-loading interface (`inference/include/inference/runtime.hpp:64–70`), with GBNF grammar (`:48–54`), prompt-plus-reply context budgets (`:28`, `:80–83`), per-call context clearing and caller-driven streaming cancellation (`:79–83`). It is not evidence that a new local endpoint or LittleBit server preserves these semantics. FWGC++ explicitly runs without network connections (`:4–5`), whereas this plan intentionally uses a local endpoint. Local service use is reasonable, but requires an explicit boundary rather than an implied drop-in compatibility claim.

**Patch MMR-314 and MMR-328:** publish and negotiate capabilities for constrained decoding/schema support, chat/tool templates, tokenizer/context accounting, maximum output and stop reasons, cancellation, concurrent context isolation and embedding identity. Run identical conformance fixtures against the S1 baseline adapter and each actual LittleBit serving adapter. Unsupported required capabilities fail closed or use an explicitly measured validator/fallback route; silently dropping grammar is not conformance. State local IPC/loopback binding and no remote inference policy. Preserve the current S1 independence from LittleBit experiments.

**Patch MMR-325/329:** report runtime capability differences and actual decoding constraints with model comparisons so a grammar-constrained baseline and unconstrained derivative are not misrepresented as compression-only differences.

**Falsification:** an exercised adapter conformance profile covering these behaviors closes P2. Generic successful completion or valid JSON alone does not.

## Verification performed

Executed bounded source reads and searches (no predicate behavior is claimed from these):

- `git -C C:/Users/davgo/Documents/GitHub/FWGC++ status --porcelain` returned no changes.
- Parsed the saved plan with PowerShell `ConvertFrom-Json`; selected MMR-310/312/313/314/315/323/327/328 and read their full bodies and acceptance criteria.
- Executed a full body/acceptance search for `fwgc_director|FWGC\+\+|\bGBNF\b|\bloopback\b|\bABI\b`: no matching tickets. Positive control `$p.bundle.tickets.key -contains 'MMR-314'` returned True. This only verifies literal absence; manual reading establishes the narrower contract gaps above.
- `rg -n 'FWGC_API_VERSION|api_version|schema_version|version|replay|save|restore|query' orchestrator/src/c_save.cpp orchestrator/src/c_api.cpp orchestrator/include/orchestrator/contract.hpp orchestrator/include/orchestrator/fwgc_director.h` located the cited contract boundaries.
- `rg -n 'class Model|Loads a GGUF|grammar|context is cleared|thread-safe|context_tokens|max_tokens' inference/include/inference/runtime.hpp` located the inference contract.
- Read existing native tests: `orchestrator/tests/test_c_api.cpp:752–753` checks API/packet versions; `:858` declares byte-exact restore; `:930` incompatible-save rejection. These tests were NOT executed; no native build directory exists at `orchestrator/build` in this checkout.

## Adversarial review and limitations

These are acceptance-definition gaps, not demonstrated failures in the future rewrite. Native source tests were inspected but not run; no C ABI binding, model serving, save corruption or live Bannerlord behavior was measured. Source line anchors were re-read before filing. No claim that FWGC++'s no-network/C++ rules automatically prohibit MNBL's managed local service. No mandate to embed the native director or to introduce a second world owner.

## Dropped/refuted claims and corrections

- Dropped generic missing replay/migration finding: MMR-315 and MMR-323 already require deterministic, versioned replay, rollback, corruption handling and save consistency. P1 only makes model-free replay inputs and the cross-project boundary explicit.
- Dropped generic LittleBit infeasibility finding: MMR-311/327/328 already separate training/deployment resources, require real factorized checkpoints and leave required infeasible lanes unaccepted.
- Dropped generic local-runtime failure finding: MMR-314 already covers unavailable provider, cancellation and no silent cloud fallback. P2 addresses capability equivalence, not those already-owned behaviors.
- Initial search used unbounded `ABI` and falsely matched words such as capability. Corrected to `\bABI\b`; no numerical claim uses that first search.
- An initial MNBL source search ran from the FWGC++ directory and returned missing-path errors; reran in MNBL. Only ordinary system DLL imports were returned for the targeted source tree, which is not proof of repository-wide absence of bindings.

Index line: Principal — P1 explicit FWGC++ ownership/version/clock/replay boundary; P2 baseline-to-LittleBit adapter capability conformance. Append to MMR-310/312/314/323/325/328/329; preserve two sprints and existing no-go gates.
