# AI Influence investigation and FWG AI epic

Owner: current Codex chat. Requested 2026-10-25. Scope: research and create a new Dark Mechanicus epic; do not execute the implementation epic.

User decisions: full FWG world interaction, including fantasy peoples, magic and world changes. Final evaluations must compare strong tool-using parent models with models compressed specifically using SamsungLabs LittleBit. Inspect the DLL and Bannerlord political/intrigue hooks, not only documentation.

## Acceptance and work

- A1 (accepted): identified v6.0.2 and retained static evidence. dnlib 4.5.0 recovered original IL and produced a separate analysis copy; ILSpy 9.1.0.7988 with async/yield reconstruction disabled emitted 588 AIInfluence types. All 17,091 method signatures/decoded instruction fingerprints match original and derived inputs. Political endpoints corroborated in original IL. Encrypted strings/delegates and runtime behavior remain explicitly unverified; no full-source-equivalence claim.
- A2 (accepted): root checked FWG identity/presentation and mutation seams; worker /root/fwg_design independently checked political chains and all eight receipt artifact hashes. Findings retained in docs/research/ai-influence-design.md.
- A3 (accepted): new epic ep_01m5rrrz6jwzzjedwwbm4m9xt5 saved as revision 1, rv_01m5rs5vzhcsgd7hbg9x6hhm62. Four sprints, 21 work tickets plus four acceptance tickets (MMR-305–329), full FWG world interaction and genuine LittleBit evaluation gates. No implementation run started.
- A4 (accepted): saved plan read back and validated with no errors; ticket-size advisories retained. Evidence: evidence/ai-influence/epic-validation.json. Recovery utilities build with zero warnings/errors; git diff --check passed. Research and epic creation complete; gameplay implementation, runtime validation and model evaluations are future epic work, not completed results.

## Sources and boundaries

## Follow-up: deeper recovery and two-sprint revision

User request: finish encrypted strings and obfuscated political/intrigue calls, integrate missed behavior, and condense to exactly two sprints. Sprint 1 is the FWG conversion/rewrite and improvements, including a persistent knowledge graph to limit long-campaign decision drift. Sprint 2 evaluates the design across multiple models, including heavier LittleBit-compressed parents.

- B1 (accepted): /root/decode_strings recovered 18,701 static direct decoder uses/11,086 values with zero input-flow/tag/range/UTF-8 failures. Root independently reran exporter/decoder in strings-verify and matched outputs. Original target was not executed or modified.
- B2 (accepted): /root/decode_calls recovered 1,420 encoded delegate bindings with 1,420 exact signature/input-flow checks and 27 political enum routes. Expanded combined readable scope: 931 types. Root independently replayed export/decode/signatures/routes in calls-verify; outputs match exactly. Runtime reflection/branch semantics remain explicitly outside static proof.
- B3 (accepted): two-sprint revision 2 saved as rv_01m5rtw43cadsyx88e11x6wxtk. 27 tickets: Sprint 1 conversion/rewrite, graph/history/retrieval/drift controls and recovered governance; Sprint 2 multi-model/heavier LittleBit and long-horizon design evaluations. Knowledge graph benefit is tested against actual recent-tail-plus-RAG baseline. No implementation/evaluation run started.
- B4 (accepted): docs/research/ai-influence-deobfuscation.md and receipt/hash/coverage/route records updated; saved plan read back and validated without errors (size advisories only). Independent review corrected the semantic ordering to political adapters MMR-318 before intrigue MMR-317; saved edge verified. Original DLL hash unchanged; git diff --check passed. All workers finished their assigned scopes.

## Original sources and boundaries

## Follow-up: adversarial comparison with FWGC++

- C1 (accepted): six-lens review of immutable epic revision 2 against clean FWGC++ HEAD `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`; ten confirmed gaps, duplicates/refutations recorded. Cards and coordinator index: `evidence/ai-influence/red-team/index.md`.
- C2 (accepted): revision 3 saved as `rv_01m5rvjpfbk8rx73zrfh2t4mxe`; fourteen existing tickets strengthened, exactly two sprints/27 tickets/47 edges preserved. Corpus reader independently reproduced 6,524/423 split and all 6,947 source-record joins. Historical blind judgement raw files were not located; scores remain reported rather than reproduced.
- C3 (accepted): saved plan read back and validated with zero errors/24 existing size advisories. FWGC++ stayed clean. No model inference, training, gameplay or native suite run; implementation remains future epic work. Verification receipts and reproducible corpus audit are in the review directory.

- Download: D:/SteamLibrary/steamapps/workshop/content/261550/3584621421.
- REA parent evidence ev_4dc480cee3bfde3084bacf0595a78636b4a452e5a5427d90f386550bf6c1c363; summary ev_21aa902ab8a50110c1e35975b60d815bb9de3743f43c91d661b4668b0f9e0add.
- Packaged guide describes prompt routing, memory, campaign snapshots, action strings, diplomacy. These are documentation claims pending code corroboration.
- Official LittleBit source: https://github.com/SamsungLabs/LittleBit (read 2026-10-25). Exact revision, model choice and hardware feasibility are implementation gates, not measured results today.
- Existing map epic has an active run and pre-existing working changes. No mutation of that work.
