# REA red-team review — 2026-10-16

User requested an adversarial review of Claude's ongoing procedural Bannerlord mod against the installed base game, with concrete REA-enabled improvements and coordination with the Harmony/engine-extension chat. User explicitly approved saving findings for Claude.

## Scope and acceptance

- A1: Compare selected high-impact mod contracts to shipped game metadata/IL; preserve REA identities and limitations.
- A2: Produce actionable, evidence-backed review findings and rank concrete gameplay improvements with first proof steps.
- A3: Exchange relevant findings with `Build an extensible Bannerlord mod` (01a1478c-7459-7bb0-b01b-755374e02acc).
- A4: Verify findings against current source before handoff, record stale or unverified claims, save review files for Claude.

Review is read-only for product code. No game launch, deploy, native patch, full suite or build is part of this review. Existing dirty edits belong to ongoing work. A copied source snapshot is an attribution aid, not an atomically consistent build or a claim that Claude has finished.

## Inputs

- Repo HEAD at capture: `89247468cc3b5e2c006c6473282fa09da4833074`.
- Snapshot: `C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795`; 619 selected files with SHA-256 manifest; excluded binaries, large files and scene payloads.
- Game: `D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord`.
- CampaignSystem SHA-256: `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`; MVID `886629fe-6e60-40d7-9a57-8d46017179d9`.
- REA artifact evidence: `ev_c863724fa1b7359c47ea40b0fd0feaeca23d770952313b76486edf1b6a2858c8`; static metadata, no execution.

## Ownership and work state

- `/root/code`: accepted — evidence/rea-red-team/code.md and code-evidence.json; ring collision/eviction reproduced centrally, runtime reachability explicitly unproven; uncapped diagnostic history independently cross-checked.
- `/root/principal`: accepted — evidence/rea-red-team/principal.md and principal-evidence.json; version predicates reproduced centrally with positive/negative controls; no installed mismatch or save corruption claimed.
- `/root/gamer`: accepted — evidence/rea-red-team/gamer.md and gamer-evidence.json; three concrete upgrades with verified installed methods/callers and required first proofs.
- Coordinator: accepted — synthesis in evidence/rea-red-team/README.md, installed-inputs.json and final-source-check.json; no product fixes in scope.

No additional full-repo lenses: this is a focused mod-versus-base-game investigation. Full SDET, generated-content statistical and published-surface audits are outside this pass.

## Coordination

Contacted the Harmony chat with user authorization. It is building a separate bounded active-agent/large virtual-force battle prototype. Review workers must not overlap its files or launch the game while it performs runtime checks.

## Completion

A1–A4 complete for the scoped review. All lane reports and primary evidence inspected; five game assembly hashes match the captured lock; fourteen central source/policy anchors rechecked unchanged. JSON artifacts parse and Markdown local links resolve. Weather-model virtual flags checked directly in saved REA records; principal independently verified monthly snow reduction and the inner weather-update dispatch. Gamer independently checked the search predicate proof boundary and diagnostic retention mechanism.

Executed two focused offline harnesses, not a mod build or full suite. Code harness uses verbatim extracted predicates, real Vec2 and explicit world constants; observed independent search suppression and marker eviction. Principal harness uses three verbatim methods, controlled version providers and embedded real lock; accepted matching release, rejected mismatched release. Initial compile/scaffold errors were corrected before accepting results and are disclosed in lane reports. Neither harness establishes gameplay success/failure.

User-authorized messages to the Harmony chat conveyed scope, measured-receipt recommendation, weather dispatch trap and exact-once campaign battle-result integration need. Its separate runtime battle work was not interrupted or treated as this review's acceptance evidence.

Saved results are ready for Claude at evidence/rea-red-team/README.md. Remaining runtime proofs and proposed features are implementation follow-ups, not unfinished review steps. No fixes, new game features, save corruption, native solver semantics or unlimited active-agent capacity are claimed.
