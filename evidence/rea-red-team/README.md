# REA review of the procedural Bannerlord mod

2026-10-16, America/Chicago. Prepared for Claude and the engine-extension agent.

**The proposed upgrades are not implemented or runtime-proven.** This is a focused adversarial review of the current mod against installed Bannerlord/War Sails contracts, with two isolated reproductions. It is not a full campaign acceptance test, a native-engine audit or a claim that Claude has finished integration. No product source, installed module or save was changed.

## What to improve first

1. **Make companions useful as local guides.** The mod already preserves source hero identity and race metadata. Add explicit, bounded biome expertise to scouting, travel and provisioning through the installed speed/visibility/food model chain. A desert guide or forest scout should change an expedition's options, with a named explanation in the existing UI. Start with one guide and two biomes; prove actual movement and spotting, NPC behavior and save/reload. Preserve War Sails' model chain and speed cap. This is the smallest compelling gameplay slice.
2. **Give the globe source-driven seasons.** The source has twelve monthly snow grids; the current synthesis collapses them to a per-cell maximum and delegates seasonal timing to the global engine clock. Preserve the monthly phase and test real source cells with different profiles. This could make seasonal campaigning, relief trade and provisioning meaningful. Weather hooks are verified; harvest/logistics rules are additional work, and opposite hemispheric profiles must be measured rather than assumed. Override the effective weather update path as well as direct queries: an outer getter alone is bypassed by inner BaseModel dispatch.
3. **Make choosing a battlefield matter.** Current battle patches map terrain into compatible scene pools, then choose deterministically by hash. Add a reviewed catalog of official scenes and match generated crossings, ridges and forest cover to suitable arenas. Prove deployment/navmesh correctness from both approaches, including reload and seam-equivalent coordinates. Selecting existing scenes is supported; creating arbitrary new tactical terrain is a separate unproven native problem.

Exact installed methods, callers, tokens, source anchors, risks and falsification steps: [gameplay review](gamer.md). These three proposals can use existing managed model extension points; a replacement for Harmony is not their prerequisite.

## Red-team findings to address before expanding the patch surface

### Search ownership is inferred from coordinates instead of the search instance

The periodic locator continuation uses a thread-local 32-slot ring keyed by position and squared radius. Executing the verbatim snapshot predicates established both a later identical query being classified as an old image pass and an existing image pass losing that classification after 32 subsequent marks. Positive controls confirmed that an unmarked strip search continues and a marked image pass stops.

**Boundary:** this proves the predicate's ambiguity under controlled calls. It does not establish that an installed gameplay caller reaches those sequences, or that an in-game hang/missed encounter occurred. The first regression should exercise real nested party/settlement/track searches before choosing a replacement. Prefer explicit search-scoped ownership and lifetime, rather than a larger ring or another floating-point sentinel. See [code review](code.md) and [reproduction output](code-repro-results.txt).

### Long campaigns retain diagnostic history without a bound

Periodic seam and storm event lists append records until reset; storm records include object references. Bound or gate diagnostic history, retaining aggregate counters and a small recent-event window. This is a source-level retention finding, not a measured memory leak rate or observed crash. See [code review](code.md) for exact ownership and reset paths.

### Compatibility receipts should measure the binaries they certify

The existing development lock is strong: the five checked installed game assemblies match its hashes. However the runtime version predicates check release metadata, and BundleHost substitutes the embedded full NavalDLC build when the three-component release matches, while advertising R01–R10 as constants.

The isolated actual-method harness accepted the matching release, refused a different release, and showed identical metadata remains indistinguishable. **No DLL was altered:** this does not prove a modified game would pass every later patch preflight. Reuse the lock in a measured runtime receipt, then add assembly/MVID, targeted signatures/IL, exception regions and active patch-owner evidence. Separate declared capabilities from checks actually observed. See [contract review](principal.md) and [reproduction output](principal-repro-results.txt).

## How REA should make development better

- **Patch-contract inventory:** retain exact shipped artifact identities, targeted methods and their callers; produce a focused difference report after a game update. Token identity is build-local, and a normalized IL hash is not proof of semantic equivalence.
- **Save identity oracle:** augment existing StringId/readiness checks with actual game IDs and ownership edges at load checkpoints. Alternate worlds A/B/A, cold-load, and use a deliberate same-name/wrong-instance fixture. No current save corruption was established; this supports future dynasties, factions and persistent events.
- **Route-cost instrumentation:** before adding asymmetric embark/disembark penalties, tides or port policies, verify the actual parameter flow and route-selection metric. Installed MobileParty path construction supplies transition-cost getters in an order opposite the interface parameter labels; the current equal costs hide that distinction. Native solver behavior and metric equivalence remain unproven. Details are in the code review.

## Coordination with the large-battle work

The user-authorized exchange with **Build an extensible Bannerlord mod** (`01a1478c-7459-7bb0-b01b-755374e02acc`) established separate ownership: that chat owns the bounded active-agent pool and larger virtual-force prototype. This review owns campaign integration and these evidence files. It received the compatibility-receipt recommendation and the weather dispatch trap.

Before combining that prototype with generated campaigns, define an exact-once result transaction for hero/troop identities, casualties, wounds, prisoners, XP and party rosters. Large logical troop counts alone do not prove campaign integration. This review neither launched the game nor duplicated its battle tests.

## Evidence and verification

- Snapshot base HEAD: `89247468cc3b5e2c006c6473282fa09da4833074`; selected dirty-tree copy at `C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795`.
- [Snapshot manifest](snapshot-manifest.json): 619 selected files. This is a file-level attribution record, not an atomic build. Binaries, large files and scene payloads were excluded.
- [Installed inputs](installed-inputs.json): CampaignSystem, Core, MountAndBlade, NavalDLC and SandBox match the captured lock.
- [Final source check](final-source-check.json): 14 central source/policy anchors matched the snapshot at handoff.
- [Campaign artifact](campaign-artifact.json), [code evidence](code-evidence.json), [contract evidence](principal-evidence.json), [gameplay evidence](gamer-evidence.json): REA identities, selected metadata/IL relationships and stated coverage. The lane reports also record supplemental execution-free Cecil inspection.
- [Code reproduction](code-repro.ps1) executes the actual extracted ring predicates with the installed Vec2 type and an explicit world-context scaffold. Use Windows PowerShell in a fresh process. [Contract reproduction](principal-repro.ps1) executes three extracted methods with controlled version providers and the real embedded lock. Neither runs Bannerlord. Both default to the captured snapshot path; preserve that copy or pass an equivalent source root after checking hashes.
- Independent principal/gamer cross-check confirmed monthly snow phase loss and the weather-model delegation trap. Initial harness compilation/scaffold errors were corrected before accepting results; the lane reports disclose them.

## Scope exclusions and dropped claims

Normal load-menu integration, production activation and migration work already tracked as unfinished were not relabeled as newly discovered defects. No corrupted save, currently mismatched game binary, unlimited native-agent capacity, arbitrary generated tactical terrain or broken seasonal weather was established. Existing storms, wind, generated identities and deterministic scene mapping are acknowledged as implemented work.

Full end-to-end gameplay, performance profiling, native disassembly, complete test-suite mutation testing and statistical world-quality review were outside this pass. The first-proof steps above define the next implementation work; they are not completed tests.
