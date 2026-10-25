# AI Influence: recovered constants, political calls and long-campaign design

Follow-up to [the initial investigation](ai-influence-design.md), 2026-10-25. Target remains v6.0.2, original DLL SHA-256 `472658f15a0ee00b6b05904830fda257593653f74fdf3c8fec1345267a73862d`. This is static analysis, not observed game execution. Tokens/IL offsets below belong to that original artifact.

## Recovery scope

The five module decoders (`06000018`–`0600001C`) have **18,701 direct generic string uses**, yielding **11,086 distinct plaintext values**. Every incoming predecessor of each call resolves to one literal argument through only allowed no-op/unconditional-branch steps. There were zero predecessor, string-tag, pool-range or UTF-8 failures. An independent rerun reproduced the result.

Original field `04000002` at RVA 8264/file offset 584 holds 164,608 encrypted bytes. A standalone implementation of the initializer's xorshift/XOR transform followed by raw LZMA1 recovers 625,952 bytes; pool SHA-256 `008151b2e3da5e037e7f8825dfc90d154d471fa24bc8f6b44576ad344a1b7f46`. No original assembly, initializer or decrypted target method ran.

All **1,420 encoded delegate-field bindings** resolve to metadata targets with matching delegate/target signatures, accounting explicitly for instance receivers and constructor returns. The opcodes include call, callvirt and newobj. Exact pass-through wrappers can be followed; nontrivial helper control flow stays explicit. The political dispatcher was an ordinary obfuscated-name switch in an omitted namespace, not an irreducible dynamic call.

The expanded static scope starts from diplomacy, dynamic events, response actions and AIInfluenceBehavior, then follows local method operands. This is broad reachability, not proof every branch executes. All 931 selected top-level types now have reconstructed output. Combined readable output is `build/research-ai-influence/calls/recovered/`; the dispatcher is `RecoveredType_0200053F.cs`, retaining original method token `06002A60` in its alias. Source aliases and decoded proxy field names are analyst presentation, not original source names.

Reproduction utilities are in `evidence/ai-influence/recovery/String*` and `Call*`. Compact metrics, hash manifests and political-route index are under `evidence/ai-influence/`; full plaintext constants, raw IL, bindings and reconstructed code remain in ignored `build/research-ai-influence/`.

**Limits:** this exhausts the identified static string/binder mechanisms, not hypothetical computed/reflection-only uses. Static call/reference resolution does not establish branch feasibility, external runtime dispatch, current game-version compatibility or whole-program equivalence. Control-flow flattening and explicit async state machines may remain in readable output. Analysis DLLs are not executable replacement mods.

## Newly recovered political behavior

`DiplomacyManager.ExecuteDiplomaticAction` (`060023AA`, IL_0137) reaches `06002A60`. Its enum switch maps 27 non-None actions to concrete handler tokens: war; peace proposal/accept/reject; alliance proposal/accept/reject/break; trade proposal/accept/reject/end; territory demand/transfer/reject; tribute demand/accept/reject/end; reparations demand/accept/reject; clan expulsion; fief grant/receive; settlement quarantine; kingdom tax policy. The compact route index retains switch offset, case entry, handler call and handler token for each.

- **Clan expulsion** (`06002A47`) invokes BanClan and the decoded ChangeKingdomAction.ApplyByLeaveKingdom route. It refuses ruling-clan expulsion and the player clan when the player leads the kingdom. Conversion must handle actual membership, holdings and political consequences, not only text announcing exile.
- **Quarantine** (`06002A4E`) checks ownership and active disease, then calls DiseaseManager.SetQuarantine at IL_02AA with kingdom-leader authority. A rewritten capability needs actual disease/policy state, restrictions, expiry and consistent context; unsupported effects must not be invented.
- **Tax policy** (`06002A58`) parses scope at IL_0133, applies policy at IL_01E0 and persists at IL_01E5. Tax collection and UI finance effects need reconciliation to prevent double collection or purely narrative policies.
- **Native/player control is broader than war/peace.** Decoded patch targets include ConsiderTradeAgreement; AllianceCampaignBehavior.StartAlliance/EndAlliance/AddAllianceDecision; KingdomDiplomacyVM.GetAreProposalActionsEnabledWithReason; decision OnShowDecision; clan finance tribute tooltips and kingdom-tax hooks. The rewrite must deliberately preserve usable player controls and decide ownership of each AI/native path.
- **Peace is a package.** Recovered defaults link peace acceptance to territory/tribute/reparations conditions. Implement explicit terms, consent, validation, ordered commit and compensation/recovery where APIs cannot be atomic. A proposal, acceptance and completed side effect are different graph events.

Existing war/peace/territory/payment endpoint observations in the first report remain valid. No dedicated blackmail/conspiracy executor was established merely by finding betrayal/deception language; the new multi-NPC intrigue system remains an explicit implementation requirement.

## World and decision defaults recovered from encrypted strings

- `PromptWorldDataStorage.GetDefaultWorld` (`06000D95`, IL_0007) assumes an all-human, magic-free Calradia, fixed kingdoms and source calendar/social conventions. The default is installed into editable campaign world.txt through `06000D99`/`06000D9A`. This reconciles the user's observation with the author's separate UI option to request custom magic lore.
- Image defaults (`06000CE0`, `060013C5`, `06002AE9`, IL_0007) independently restrict high fantasy. Audit every enabled dialogue/event/memory/image template; replacing one world file is insufficient.
- Political/default event templates (`06000CD2`, `06000D59`, `06000CFC`, IL_0007) already say current technical state overrides history and distinguish proposed from successful actions. Preserve that principle as validators and typed state, rather than relying on instructions alone.
- The diplomatic defaults also pressure each response to add something new, and one rule foregrounds player agreements less than 15 days old (`06000D59`, IL_0007). This is a prompt horizon, not proven deletion. Remove forced novelty: a stable, justified position is valid. Commitments remain active until actual fulfillment, revocation or expiry.
- Confidential dialogue should not create public events (`06000CFD`, IL_0007), and one conversation should not repeatedly drive diplomatic engagement. Enforce graph visibility and public-event deduplication independently of model compliance.
- Social claim schemas cover name/clan/age/gold, while conversation and letter prompts use different lie-evidence standards (`06000CB2`, IL_14E6/174B). Preserve speaker, recipient, evidence and claim status; suspicion is not authoritative contradiction.

These are editable shipped defaults, not unavoidable engine rules. Templates can be edited or ignored by a model; authority, knowledge boundaries and action validity belong in code.

## What the existing memory system actually does

Avoid the wrong baseline: this version already has conversation RAG. `ConversationRagService.BuildHistorySection` (`060012C0`) combines a recent tail (`PromptMaxHistory` fallback 15) with retrieved chunks (`RagTopK` fallback 5, chunk-size fallback 6). Retrieval supports lexical/semantic/hybrid modes. Chunk records include line ranges, source ID, kind, campaign day and embeddings. Embeddings are invalidated when configuration changes.

`MemoryHistoryPolicy.ApplyConsolidation` (`0600136A`) is obsolete and forwards to ApplyMemoryFormation (`06001369`). The current flow advances the processing index, removes distorted overheard records and saves visual MemoryBook entries; it does **not** recursively replace the conversation with MEMORY summaries. NPC forgetting and expiry exist, but should be distinct from retaining an auditable source archive.

A specific political context weakness is confirmed: `GetRecentStatements` (`06002582`) applies a lifespan/newest-first limit (default 15; Take at IL_006F); `GetStatementsForNPC` (`06002583`) filters by kingdom afterward. Unrelated recent diplomacy can crowd out older relevant statements in that path. Separate pending-proposal logic searches a larger statement universe, so this does not prove all obligations vanish after 15 entries.

## Revised design and acceptance

**Sprint 1 — conversion/rewrite and improvements.** Port the useful behaviors into FWG with source-to-feature coverage, current generated-world facts, explicit political authority and all recovered governance routes. Implement a temporal knowledge graph backed by an append-only event archive. Keep authoritative facts, NPC beliefs, rumours, proposals, commitments and committed effects distinct. Preserve provenance, valid/recorded time, confidence/status, secrecy, campaign/world identity and save watermark.

Bound prompt size, not historical truth. Retrieve graph relationships, current state, relevant causal history and active obligations regardless of recency/similarity. Summaries and indexes are derived, versioned and rebuildable. NPC forgetting limits recollection without deleting the source event. Decision receipts retain fact IDs/versions, concise justification, tool proposal and observed outcome, not hidden chain-of-thought. Contradiction checks must permit justified changes after new evidence and must not freeze personalities.

**Sprint 2 — evaluations against the design.** Compare at least three inference models across size tiers, then genuine LittleBit derivatives of at least two parents including a heavy 30B+ lane. Assess a 70B-class lane against real authorized resources; do not invent a fitting result. Initial host evidence is a 16,303 MiB RTX 5080 Laptop GPU; actual game headroom, RAM, training needs and factorized serving costs must be measured at execution.

Test at least 10 simulated campaign years/100,000 dated events per stress world, plus separately labeled live sessions. Measure temporal contradictions, missed obligations, information leakage, invalid actions and justified decision changes over history length, not only one-turn tool JSON. Include succession, corrections, expiry, wars, mutations, model/index switches and rollback. Compare graph grounding with a faithful recent-tail-plus-RAG baseline under matched access/context/resource budgets; show regressions and no benefit honestly. Paired heavy-parent/LittleBit trials must preserve held-out data and record actual artifacts/settings, latency, RAM/VRAM, storage, game impact and critical failures.

Current work delivers recovered evidence and the saved plan. The rewritten mod, graph, long-campaign runs and model experiments have not been implemented or executed by this investigation.
