# MMR-36 independent review and disposition (2026-10-15)

Reviewer: an independent read-only agent. It did not edit the repo or launch the game. It re-ran `tools/Test-GraphValidation.ps1` (42/42, the same report hashes as the evidence) and ran extra fault injections through a scratch copy of the harness (P1–P24). It reviewed the first version: offline 42 checks and in-game run 36200, 9/9.

## Review (verbatim)

**Verdicts**
- **c1 – not met as written.** The 28 offline faults and 3 in-game faults are meaningful: each must produce exactly its expected code set. But several broken-reference and vanilla-instance classes pass with no diagnostic (findings 1, 2, 5, 6).
- **c2 – met, but narrowly, for the one bundle.** The staged census matches counts derived from the factories exactly, and all 711 source UIDs get an outcome. However:
  - any non-"instantiated" disposition string is accepted, including null (finding 3);
  - harbour records are outside the source check (finding 4);
  - the reused-template census is never compared to an expected value (finding 13).
- **c3 – met for invalid graphs the validator actually detects.**
  - The gate at `GeneratedCampaignRegistration.cs:155-163` runs before the fixture prefabs, the registry plan and any document load.
  - The documents it validates are the same objects later passed to `registry.Register`.
  - Run 36200 shows the stages `mnbl_00059042:generated_graph_validation:valid` (20) → `generated_registry_plan:44` (21) → `generated_xml_load:SPCultures` (22). Each fault row records "generated_dataset_preflight > generated_graph_validation:1 diagnostics", no registry and unchanged prefabs.
  - Caveats: nothing rolls back through C01 (finding 12), and the false passes below do proceed to simulation.

**Findings**

1. **MAJOR – a reference to an object of the wrong type passes closure.**
   - Where: `CampaignGraphValidator.cs:246-250`; `ReferenceSchema.cs:41-42` takes the type from the value itself.
   - Results, all "PASSED (no diagnostic)":
     - P1: a non-leader hero with `faction="Kingdom.<k>"`;
     - P2: a non-capital castle with `owner="Hero.<lord>"`;
     - P15: a village with `village_type="Faction.<clan>"`.
   - The MMR-30 registry plan doesn't check types either (`RegistrationPlan.cs:216`, `byKey.TryGetValue`), so these reach the engine with a null clan, owner or village type.
   - Fix: a map from attribute to expected type (`faction`→Faction, `owner`→Hero for Kingdom/Faction and Faction for Settlement, `super_faction`→Kingdom, `bound`→Settlement, `village_type`→VillageType, `culture`→Culture, `troop`/`upgrade_target`→NPCCharacter); report a mismatch as `graph.reference_type`.

2. **MAJOR – a Calradia hero's character is not flagged as an unexpected vanilla instance.**
   - Where: `CampaignGraphValidator.cs:214-222`; `worldTypes` (`make_graph_policy.py:44`) omits NPCCharacter.
   - P3: staging the installed `NPCCharacter.dead_lord_2_1` (`is_hero=true`, with an installed Hero record) is classed as a re-emitted template → PASSED.
   - The old dataset rule ("Named/world hero cannot be retained as a reusable NPC template", `GeneratedCampaignDataset.cs:332-334`) was not carried into the validator, which claims to own this census.
   - Fix: flag an NPCCharacter that is staged or referenced when it has an installed Hero, or has `is_hero` set without `is_template` and is not a culture role.

3. **MAJOR – dispositions are not tied to policy, so a silent discard can pass.**
   - Where: `CampaignGraphValidator.cs:435-438`.
   - `outcome = d.Outcome + …` treats a null outcome as "" (non-null) and accepts it. P5 (null outcome and reason) and P6 (`"dropped"`) both pass.
   - Fix: add an allowed-outcome list (`dissolved`, `bounded`, `notInstantiated`, `excluded`) to `graph-validation.json`, and require a reason.
   - Also at line 432: a record counts as "staged" by type alone. A compiler town staged as a castle passes (P22), and so does a record staged despite an exclusion or a dissolved/bounded disposition (no contradiction check).

4. **MAJOR – harbour records and pending slots are checked against factory claims, not the graph.**
   - Where: lines 450-460 and 490-513.
   - A slot counts as resolved when any resolution with the same `record|slot` exists, whatever its value. `tier`, `vassalClans` and `policies` are emitted unconditionally (lines 496, 497, 502), so those checks can never fail. P16: a `policies` value of "" → PASSED.
   - P21: a compiler port town staged with no `port_posX`/`port_posY` and no `building_shipyard` → PASSED, while MMR-35's `navalFacility` and `shipyardAndFleet` slots still read "resolved".
   - This conflicts with the ticket's "never silently discard an unsupported race or harbour record" and "reject every required typed pending slot".
   - Fix: check each slot against the staged XML (each campaign.json `ports[].town` must have a port and a shipyard, `villageType` must equal the component's `village_type`, a kingdom with an empty policies value is pending), and check that staged kind and ownership match the manifest. P23 (one village's `bound` swapped to another town) also passes.

5. **MAJOR – several validation classes in the ticket are only nominally checked.**
   - **Troop upgrades** (lines 355-358): the cycle search starts only from staged characters. In the size-129 world, "staged NPCCharacter records 241, with upgrade targets 0", so it checks no tree at all. P24: a cycle inside the installed troop tree → PASSED. The docs claim "staged and installed".
   - **Mission locations** (line 343): checked only for Town components. A village with no `Locations` passes (P9).
   - **Hero roles** (lines 320-327): only `is_hero` and a clan. A clan leader whose character has `occupation=Wanderer` passes (P10).
   - **Initial-party intent** (lines 375-382): a runtime notable homed at a hideout passes (P13); a relation to a nonexistent hero passes (P14). Pirate spawn points and startup anchors are not inputs at all.
   - **Fixed-position spawn records** (lines 403-412): only settlements are checked; a culture's start point is compared only with that culture's own installed start point.
   - Fix: add each check, plus a fault fixture for each.

6. **MINOR – an unstaged Calradia instance with an alias id resolves as a template.**
   - Where: line 254 (`!input.Aliases.Contains(depId)`).
   - P4b: with the staged retreat removed, a hero's `born_settlement="Settlement.retirement_retreat"` → PASSED.
   - P4 and P4c were caught only incidentally, by walking the installed record, and the diagnostic is pinned on the template: "graph.official_reference Faction.player_faction Settlement.town_B1".
   - The MMR-30 plan backstops this in game (`RegistrationPlan.cs:236`), but the validator is meant to own closure. Fix: exempt an alias only when it is staged.

7. **MINOR – malformed input crashes the validator instead of producing diagnostics.**
   - P11: a document with no root → NullReferenceException (line 172).
   - P12: a disposition with neither uid nor id → ArgumentNullException (line 419).
   - P17: a slot with no owner → ArgumentNullException (line 459, `deferred.ContainsKey(null)`).
   - P19: an exclusion listed twice → ArgumentException (line 421).
   - An id-map entry with a null uid would also throw at line 443.
   - Registration is still refused, but with no aggregated report.

8. **MINOR – elements without an `id` skip every check** (line 172). P7 (`<Settlement name=…>`) and P8 (`<Starport/>`) → PASSED. The registry plan also skips them (`RegistrationPlan.cs:103`).

9. **MINOR – diagnostics don't carry a real property path.**
   - The path field holds the document name for identity codes, the missing target for closure codes, and null for malformed references (line 241).
   - Errors found inside an installed template have no generated id or source UID of the record that pulled it in.

10. **MINOR – the report hash doesn't bind the staged content** (lines 480-487: counts, outcomes, optional-absent list, policy hash, diagnostics). So the claim at `summarize_graph_gate.py:7-9`, "the gate validated exactly the graph the offline tests validate", is overstated. Fix: include each document's sha256.

11. **MINOR – the policy identity is inconsistent between world types.**
    - Fixture worlds key their policies by file name (`"economy.json"`, `GeneratedCampaignRegistration.cs:158`); the compiler world uses repo paths.
    - The compiler-world snapshot omits `graph-validation.json` itself, the catalog receipt and the live-default inventory (the "runtime-derived inventory" in the ticket).

12. **MINOR – there is no rollback through C01.**
    - `CampaignObjectRegistry.cs:275` and `docs/campaign-registry.md:129` both say "MMR-36 rolls staged registration back through it", but nothing calls `RemoveWorldObjects`.
    - A failure after the gate (registry plan or closure) relies on the process ending.

13. **MINOR – test weaknesses.**
    - `GraphFailureFixtures.cs:123-124`: the expected re-emitted Culture count is computed from the same staged SPCultures document, so it is circular.
    - Line 129: `templatesReferenced` and `defaultsReferenced` are only checked to be non-empty.
    - No fault fixture for a staged vanilla clan, a copied start point, a wrong-type reference or a hero character.
    - The dissolved-realm path is untested (the only bundle has 0 dissolved realms).

14. **NIT – documentation overclaims.**
    - `docs/campaign-graph-validation.md:112` says the gate runs "before any engine state changes". But `BuildCount++` (`GeneratedCampaignDataset.cs:179`) and the naval models installed in `GeneratedGameManager.cs:105-114` both happen before it.
    - Doc rows 32, 41 and 44 overstate findings 2, 5 and 3 respectively.

15. **NIT – the probe's object counts are weak evidence.**
    - `GraphGateProbe.cs:61-69` counts objects by world prefix, so it can't see re-merges of aliases or re-emitted templates (`main_hero`, `player_faction`, `Culture.empire`, the 886 NPCCharacters).
    - The real evidence is the stage check, which is sound. `StartsWith(prefix) || Contains(prefix)` is redundant.

16. **NIT – the ReferenceSchema extraction has a second, harmless behaviour change.** `GeneratedCampaignDataset.Dependencies` now calls `.ToList()`, which changes which preflight error surfaces first when an element has several problems. The `else if` change is harmless for the dataset, because `Fail` throws on the first message anyway.

**Checked with no problems found:**
- The docs' and evidence numbers all match the harness output: 42 checks; 711 UIDs; 2228 resolved / 71 deferred slots; 15 policies; the census table; 9/9 in-game checks.
- The embedded policy sha `846926a6…` equals the repo file.
- All sources were modified before run 36200 started (18:26:53Z).
- The report hash is deterministic across runs.

## Disposition (one fix batch)

Every finding was fixed except 16, which needs no change. The offline tests went from 42 checks to 70; each new fault must give exactly its expected codes. A confirming game run followed (see `evidence/runtime/graph-gate.json`).

| # | Disposition |
|---|---|
| 1 | Fixed. New `ReferenceTypes` stage, code `graph.reference_type`, with the property→type map of the installed `Deserialize` reads. Faults: a hero whose clan is a kingdom, a castle owned by a hero, a village whose type is a clan |
| 2 | Fixed. The character of an installed hero is a world instance: staging it is `graph.unexpected_vanilla`, and referencing it unstaged is `graph.official_reference`. Faults: the character staged, and a generated character naming one |
| 3 | Fixed. Policy `dispositions` lists the allowed outcomes, and a reason is required (`graph.disposition`). Staged kind must match the compiler kind (`graph.source_mismatch`). A staged record or runtime intent with a contradicting disposition or exclusion is refused. Faults: a null outcome, "dropped", a staged town that is also excluded, a town staged as a castle |
| 4 | Fixed. `Unshown` verifies every graph-visible slot against the staged XML (the list is in the doc). A compiler port town must keep its port, and a village's binding must equal the compiler's or MMR-33's declared rebinding (the factory's `Rebound`/`ReboundReason`; 44 in the bundle). `Resolutions` no longer emits an empty `policies`. Slots of disposed records are moot (70 of the bounded heroes). Faults: a port town without its port (also a shipyard without a port, and the naval slot claim), a swapped binding, a kingdom without policies, a removed shipyard (also the slot claim) |
| 5 | Fixed. Upgrade cycles are searched from every character staged or reached (141 trees in the bundle). The location complex is required for every kind whose installed settlements all have one (towns, castles, villages, hideouts, the retreat). Rulers, leaders and noble clan members must be Lords. Runtime notables and companions need a template of their occupation (policy `runtimeHeroes`) and a home of their kind, and relations must join heroes of the world. Spawn positions are an input (anchors, starts, pirate zones: 491), and every position is compared with every installed settlement, gate, port and start point. One fault for each |
| 6 | Fixed. An alias is exempt only when staged. A failure inside a template is pinned on the generated record that reached it. Fault: the retreat alias referenced after it stops being staged |
| 7 | Fixed. Each malformed input is a `graph.malformed_input` diagnostic, and each stage is guarded, so nothing throws. One fault for each case the review named |
| 8 | Fixed. Record elements and components without an id are `graph.malformed_input`. Fault added |
| 9 | Fixed. `ReferenceSchema.References` yields each reference with its property path, which every closure and type diagnostic carries. Template failures carry `<template>:<path>` and the generated id of the record that reached it |
| 10 | Fixed. The report's canonical form binds every staged document's sha256 and the live default inventory sha (report schema version 2). The summarizer's claim now names what the hash binds |
| 11 | Fixed. Fixture keys are repo paths. Every world's snapshot must name the graph policy, `runtime:catalogReceipt` and `runtime:liveDefaults` (policy `requiredInventory`) |
| 12 | Fixed. `CampaignObjectRegistry` rolls a failed new-world load or closure back through `RemoveWorldObjects` (`RollBack`, `RollbackRemaining`), and `GeneratedGameManager` rolls back on a post-registration closure failure (`IGeneratedWorld.RollBack`). Two fake-engine tests: a closure failure, and a load that fails at its last document; nothing of the world remains, and templates and the pre-existing `main_hero` stay. Not exercised in game: rolling a world back in a live campaign would first re-merge its re-emitted templates into that campaign |
| 13 | Fixed. The expected culture count comes from the startup policy. The reached set is checked independently: every installed record the staged XML names directly (862) is reached, every reached key is installed or a default, none is a Calradia instance, and the census counts equal the set's. Faults added for a vanilla clan, a copied start point, wrong-type references and a hero character. The dissolved path is tested on a synthetic manifest |
| 14 | Fixed. The doc states the gate's real position: after the game manager and dataset build, before the fixture prefabs, registry plan and loads. The rows are rewritten |
| 15 | Fixed. The probe also records every census type's total in the object manager and campaign lists before and after, which must be unchanged. The doc and summarizer name the stage evidence as primary. The redundant prefix test is removed |
| 16 | No change needed (agreed: harmless) |

## Confirming runs

- Offline: `tools/Test-GraphValidation.ps1` 70/70 (`evidence/campaign/graph-tests.txt`); `tools/Test-CampaignRegistry.ps1` 59/59, including the two rollback checks (`evidence/campaign/registry-rollback-tests.txt`).
- In game: run 42076 (`evidence/runtime/in-game/generated_campaign-42076.json`), `tools/summarize_graph_gate.py` 9/9 (`evidence/runtime/graph-gate.json`).
  - Both worlds passed the gate with report hashes equal to the offline reports of the same fixture worlds (`162a854a89da…`, `6588d8cfa954…`); these hashes now bind the staged documents and the live defaults.
  - All three in-game faults were refused at the gate. Each stopped before any registry plan or document load, left no object of the world behind, and left every census type's count unchanged.
- Regression signals from the same run (earlier tickets' summarizers, without a cold load): startup audit 33/33, naval content 10/10, settlement fixtures 13/13.
  - In the first run (36200) the startup audit gave 32/33: no villager party spawned naturally in either world's 2-day window.
  - The same happened in run 17284, before this ticket. Natural villager spawning is chance-based.
