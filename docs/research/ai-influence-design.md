# AI Influence → FWG living-world AI

Research date: 2026-10-25. Epic: `ep_01m5rrrz6jwzzjedwwbm4m9xt5`.

## Coverage and limits

The follow-up recovered all 18,701 statically present direct encrypted-string uses of the five constant decoders and all 1,420 encoded delegate-field bindings, with exact target-signature checks. The 27 non-None diplomatic switch routes are mapped. See [the follow-up findings](ai-influence-deobfuscation.md) for current coverage, original-token references and the revised two-sprint plan. No target code was executed. Dynamic reflection outside the recovered mechanisms, branch feasibility, dependency/runtime compatibility and full semantic equivalence remain unproven. No model evaluation or compression was run; those are Sprint 2 work.

Downloaded target: Steam Workshop item 3584621421, `D:/SteamLibrary/steamapps/workshop/content/261550/3584621421`, descriptor version **v6.0.2**, assembly version 1.0.0.0, .NET Framework 4.7.2 x64.

- Original DLL SHA-256: `472658f15a0ee00b6b05904830fda257593653f74fdf3c8fec1345267a73862d`.
- REA rejected the original metadata: duplicate `#GUID` stream, offset 2616248. Original parent Evidence: `ev_4dc480cee3bfde3084bacf0595a78636b4a452e5a5427d90f386550bf6c1c363`; summary: `ev_21aa902ab8a50110c1e35975b60d815bb9de3743f43c91d661b4668b0f9e0add`.
- dnlib 4.5.0 read the original without CLR-loading/executing it and wrote a separate normalized analysis copy. Original files were not changed. The copy is **not a replacement mod DLL**.
- Analysis copy SHA-256: `225b1418f700307659a47a80c315e463f8b08755c1fc48f73e368fbd16c3e50d`; MVID: `a98963bd-a495-4b22-9e08-1af8dc7c58f8`.
- REA admitted the copy with complete metadata: 2,890 type rows including nested types, 17,091 methods. Derived-copy parent: `ev_daed1ae8e8aebeea22716765d909b1e27282db5592000f528600c83c4b7d8782`; summary: `ev_1925e24b2dbb2196178c6815df810c80984a0e95dd2e00785377b7eaf44ebfd2`. Although the tool labels its explicit input shipped-artifact, this input is our **derived artifact**, not the original download.
- ILSpy 9.1.0.7988 initially rejected the original compressed metadata. Whole-project decompilation of the copy failed in async reconstruction. With async/yield reconstruction disabled, all **588 top-level AIInfluence namespace types** emitted C# without per-type exceptions. Compiler-generated state machines remain explicit. This count excludes other namespaces and does not mean all statements are semantically recovered or compilable.
- A dnlib comparison of all 17,091 original/derived method signatures plus decoded instruction opcodes/operands matched. This is stronger than a count check, but does not establish complete assembly, exception-region, attribute or runtime equivalence. Political endpoints below are corroborated directly from the **original** IL, not solely from rewritten tokens.

## Local evidence and readable code

- `build/research-ai-influence/readable/`: recovered C# (local, ignored, not product source).
- `build/research-ai-influence/original-methods.json`: original method tokens, signatures and decoded IL.
- `build/research-ai-influence/rea-evidence-complete.json`: retained REA records.
- `evidence/ai-influence/bannerlord-hooks.json`: 105 original methods with selected TaleWorlds action/event/decision/model references; a bounded filter, not an exhaustive call graph.
- `evidence/ai-influence/normalization-audit.json`: method comparison result.
- `evidence/ai-influence/recovery/`: our recovery utilities, not recovered target code.
- `evidence/ai-influence/receipt.json`: artifact/tool/source hashes and coverage.

Recovery uses a local .NET 8 SDK already present in SummerEngine. Reproduction commands and file layout are in `evidence/ai-influence/recovery/README.md`. Raw recovered code and the derived DLL stay under ignored `build/`. The target was never installed into the game or launched.

## Political engine integration: observed static chains

All tokens and hexadecimal IL offsets below refer to the original SHA-256 above. See `original-methods.json` and the retained hook inventory. C# interpretation is qualified where dispatch is obfuscated.

1. **Vanilla political ownership.** `DiplomacyPatches.ConsiderWar_Prefix` (`060024C0`) checks `EnableDiplomacy` at `IL_0005`, checks kingdom-faction context and writes a false result at `IL_0045–0046`, returning false. Peace counterpart `060024C1` writes false at `IL_004F–0050`. Reconstructed C# shows disabled setting/exception paths allow original execution. The precise installation of every encrypted reflection target remains unverified. Lesson: explicitly decide which system owns each political behavior; avoid accidentally suppressing native decisions when AI is unavailable.

2. **Statement → validation → live dispatch.** `KingdomStatementGenerator.ParseAIResponse` (`06002637`) deserializes and repairs model output, converts actions/targets and checks peace-package consistency. `ValidateActionsWithTargets` (`0600263B`) checks current war/proposals/trade state. `DiplomacyManager.HandleKingdomStatement` (`060023A8`) checks `EnableModification`, resolves `Kingdom.All`, calls `ExecuteDiplomaticAction` at `IL_03C6` and applies relations at `IL_03E5`. `ExecuteDiplomaticAction` (`060023AA`) re-resolves non-eliminated kingdoms by StringId. Follow-up: its call at IL_0137 reaches ordinary obfuscated-name method `06002A60`, a recovered switch with 27 action branches. The initial namespace-limited export had omitted that helper. Lesson: typed structured tools with named target fields and explicit rejection are preferable to repaired positional action/target arrays.

3. **War/peace are engine effects.** `DiplomacyManager.DeclareWar` (`060023AB`) checks existing war, breaks alliance (`IL_00B8`), ends trade (`IL_0153`), then calls `WithBypass` (`IL_0192`). Lambda `06002420`, `IL_000C`, invokes `DeclareWarAction.ApplyByDefault`. `MakePeace` (`060023AC`) uses bypass at `IL_0093`; lambda `06002422`, `IL_000C`, invokes `MakePeaceAction.Apply`. Lesson: diplomatic side effects and re-entry need a transaction/ownership contract, not only a good prompt.

4. **Fief transfers.** `TerritoryTransferSystem.TransferSettlement` (`0600281A`) checks town/castle (`IL_0035/0042`), current owner clan/kingdom (`IL_0073/007F`), selects a receiving clan, then uses bypass and persists. Lambda `06002837`, `IL_0011`, invokes `ChangeOwnerOfSettlementAction.ApplyByDefault`. Generated entities must be resolved through the current FWG campaign, and authority/ownership must be checked again at commit time.

5. **Economic consequences.** `ReparationsSystem.PayReparations` (`060027CB`) reads leader gold and calls `GiveGoldAction.ApplyBetweenCharacters` at `IL_00B9`. Reconstructed control flow caps payment at available gold, then marks/removes the demand even after partial payment. `TributeSystem.ProcessTributePayment` (`060028BB`) checks 24 campaign hours, caps payment to funds, transfers at `IL_0090`, and persists. These are design decisions about leader wealth, debt and shortfalls; do not inherit them silently as a state-treasury model.

6. **Relationships and event awareness.** `DiplomacyManager.ApplyRelationChange` (`060023DF`, offset 452 decimal) calls `ChangeRelationAction.ApplyRelationChangeBetweenHeroes`. `RegisterEvents` (`060023A5`) observes war, peace, hourly/daily ticks, battles, prisoners, hero deaths, settlement ownership and kingdom destruction. A new AI system needs these state changes in its facts and memories, not just dialogue history.

7. **Intrigue ingredients are present; a complete conspiracy subsystem is not established.** `ApplyCounterpartySocialPatchesData` (`060001B6`) has a modification gate, hero resolution and optional target whitelist. `IsHeroAllowedForSocialPatch` (`060001B7`) allows any resolved hero when that whitelist is null. `ApplyOneCounterpartySocialPatchData` (`060001B8`) records claims, lie penalties, trust and delayed directed relation effects (`IL_048C`). This supports learning from belief/deception design; it is not evidence that every desired intrigue mechanic already exists.

8. **Distance assumption.** `TryComputeKingdomPairBorderMetrics` (`06002823`) reads settlement gate positions and calls `CampaignVec2.Distance` at `IL_0065`. For FWG, border relevance must use wrapped/world travel semantics. This identifies a dependency to replace, not a demonstrated in-game seam bug in AI Influence.

## Prompt, knowledge and persistence design

The shipped `TECHNICAL_GUIDE_EN.md` documents campaign-specific contexts, a prompt router, per-NPC memories and knowledge, public information versus secrets, events/kingdom statements, and executable string actions. Useful reference sections: lines 9–69 (layout), 87–149 (knowledge), 182–315 (state), 317–337 (prompt modules), 339–475 (actions). These are package documentation claims; runtime fidelity was not tested.

The guide says prompt files are excluded from save snapshots (line 319). For MNBL, bind prompt/schema/config versions to evaluation and saved-state provenance so loading an older save cannot silently evaluate under an unidentified ruleset. Explicitly distinguish author-editable policy from campaign memory rollback.

The authoring UI permits requested magic, but the follow-up decoded `PromptWorldDataStorage.GetDefaultWorld` (`06000D95`, IL_0007): its built-in default excludes magic and nonhuman peoples and names fixed Calradia kingdoms. Default event/memory/dialogue image prompts also restrict fantasy. These are editable shipped defaults, not an immutable engine prohibition. The rewrite replaces assumptions across every enabled template, not only world.txt, with generated FWG facts/capabilities. See the follow-up for memory/RAG and default political-rule findings.

## FWG integration decisions

Source-backed seams verified in this repository:

- `Generation/WorldReconstruction.cs` reads verified world/campaign documents; `Campaign/CompiledWorldDataset.cs` retains generated factions/heroes/settlements/population and provenance. Add a versioned fact projection, with live campaign values overriding initial historical state.
- `worldgen/policies/culture-presentations.json:5–19` maps elf to battania, dwarf to sturgia and tidekin to nord **for presentation**. `Campaign/FactionFactory.cs:198–199` preserves CultureUid/Civilization/CivilizationName. Do not infer a person's lore from `Hero.Culture` alone.
- `CampaignBootstrap/WorldIdentity.cs:16–38` separates world content from campaign instance. Scope memory, inference caches and action journals to instance + content + entity, not seed or StringId alone.
- `Campaign/WorldMutationBehavior.cs:155` exposes internal `WorldMutations.Report(MagicEvent)`. It queues a settlement event or returns a refusal. `World/WorldMutations.cs` checks school visibility, target, finite positive strength and capability; the behavior applies at a safe point and cancels work during load. This is **not a generic terrain editing or complete spell API**. An explicit bridge and gameplay costs/authority are necessary.
- `Campaign/GeneratedGameManager.cs` installs/clears campaign behaviors. AI needs the same load/unload lifecycle and cancellation guarantees. Current ownership/alive-state/party locations must come from live objects.

The adopted product direction is full FWG interaction: grounded conversation, memory and beliefs, intrigue, political/economic actions, generated-world travel and quests, NPC initiative, and real supported magic/world changes. Any advertised new effect must have an implemented validator/executor and observable state transition; generated prose cannot stand in for implementation.

## LittleBit evaluation contract

The user specifically selected SamsungLabs LittleBit, not generic low-bit quantization. The final sprint ranks tool-using parents, compresses candidates, and compares actual end-to-end performance.

The [official implementation](https://github.com/SamsungLabs/LittleBit) documents factorized low-bit layers, QAT, supported model families and optional LittleBit-2 initialization. Its provided evaluation examples cover perplexity and standard language benchmarks; our campaign tool tests are an additional requirement. No tool-use retention or serving compatibility is established by that README.

The saved epic requires:

- Early hardware/training/serving feasibility, including actual checkpoint load/generation; target resource budgets fixed before final results. No speculative model winner today.
- At least three feasible parent candidates; at least two compressed parents at two or more documented LittleBit settings each. Exact model/repository revisions, templates, tokenizers, data, seeds, layer format and checkpoints are recorded.
- At least 100 frozen held-out campaign scenarios, separated from calibration/training/tuning. Test queries/no-tool turns, target selection, typed arguments, dependent calls, failed tools, contradictions, authority, multi-turn intrigue, magic-on/off and save isolation.
- Paired parent/compressed scores for tool selection, schema/argument correctness, grounded targets, refusal, knowledge boundaries, multi-step completion and actual campaign effects. Critical invalid mutation or cross-campaign leakage is a hard failure. Report repetitions/uncertainty and all failed recipes.
- Real local serving with Bannerlord running: end-to-end p50/p95 latency, time to first token, throughput, peak RAM/VRAM, actual storage, context/KV overhead and frame impact. Parameter BPW alone does not establish runtime feasibility.
- The final selected derivative must pass live dialogue→intrigue→world-action→save/load scenarios. If none meets the frozen gates, record no-go and leave acceptance unpassed.

## Epic structure

The user-requested revision has exactly two sprints: (1) FWG conversion/rewrite, full world interaction and long-campaign knowledge-graph/decision improvements; (2) design evaluations across multiple inference models, including heavier LittleBit derivatives and longitudinal RAG/graph comparisons. MMR-305 and MMR-306 are the retained acceptance nodes; obsolete intermediate acceptance nodes were removed. Dependencies, current ticket counts and saved revision are recorded in `evidence/ai-influence/epic-validation.json`. No execution run was started.

## External provenance

- [AI Influence author Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3584621421): author-described features and distribution terms; downloaded guide/code takes precedence for this version's details. Design learning is implemented independently, with no recovered target source added to the product.
- [SamsungLabs LittleBit](https://github.com/SamsungLabs/LittleBit): method/runtime planning reference, accessed 2026-10-25; implementation ticket pins the exact revision before experiments.
