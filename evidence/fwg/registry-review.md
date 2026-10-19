# MMR-30 independent review and disposition (2026-10-15)

The review was done by an independent agent that was told not to modify repository files and not to launch the game; its scratch files are under `%TEMP%\mmr30-review-*`. The report is reproduced verbatim below, followed by the disposition of every finding. All fixes were applied in one batch, then verified:
- **Offline:** 54/54 checks (`evidence/fwg/registry-tests.txt`).
- **In game:** the new-game, save and cold-load pair was repeated (`evidence/runtime/registry-probe.json`; see docs/campaign-registry.md for the run history).

## Review (verbatim)

```
MMR-30 independent review. I made no repository edits. Scratch files: experiment harness `C:\Users\davgo\AppData\Local\Temp\mmr30-review-exp\Exp.cs`, IL dumper `C:\Users\davgo\AppData\Local\Temp\mmr30-review-x\il.ps1`, build output `C:\Users\davgo\AppData\Local\Temp\mmr30-review-build`. The mod builds clean (Release, scratch OutDir and obj). `tools/Test-CampaignRegistry.ps1` re-run: 36/36 pass.

The exact text of the E1–E14 results is load-bearing; each label is printed verbatim by Exp.cs.

## Findings

**1. MAJOR — `CampaignObjectRegistry.cs:128-134` and `:152`: closure during a load accepts objects that are not ready.**
- **Defect:** `state()` returns OK for anything found in a campaign list before it ever checks readiness. Line 152 also exempts not-ready Hero, Kingdom, Faction and MobileParty objects of this world during a replay. The docs (`docs/campaign-registry.md:58-62`) say closure requires "every reference … registered and ready".
- **In-game evidence (run 33092, registry census at `after_submodule_post_registration`):**
  - `closure` is `[]`.
  - The same census's own `stale` field lists all 3 clans, both kingdoms, all 21 alive heroes and all 17 parties.
  - The independent CampaignBootstrapProbe census agrees: those clans, kingdoms and heroes are `ready:false` until `saved_game_loaded`.
  - The "presumed hero" the docs and test model describe (`mnbl_00059042_ruler_2`), plus a Kingdom (`kingdom_2`) the docs omit, are the saved instances themselves. Their identityWitness values match the CampaignObjectManager entries. They are not stray copies.
- **Scenario:** a save whose clan or hero never becomes ready, or a replay that leaves a presumed clan copy, still closes. E12 shows presumed Faction and Kingdom copies → closure is `NO PROBLEMS`. ICampaignLists carries ids only, so readiness of list-held objects is never asserted at any point, including after new-game initialization.
- **Fix:** add readiness to ICampaignLists. Document that a replay checks only membership for temporary types. Add a closure at `saved_game_loaded`/map that requires readiness and is enforced as a failure. Stop reporting saved temporary objects as `stale` during a load (today that field is all false positives there).

**2. MAJOR — `tests/Campaign/RegistrationTests.cs:48`, `:76-79`, `:89-102`: the engine model differs from the installed engine and from the recorded runs.**
- **(a) The village-before-town throw does not happen.** Installed IL:
  - `Village.Deserialize` reads `bound` through `ReadObjectReferenceFromXml`.
  - That calls `GetPresumedObject`, which auto-creates the Settlement.
  - It then calls `Bound.IsTown`. `Settlement.get_IsTown` returns false when `Town` is null, and no Settlement constructor sets `Town`.
  - So nothing throws. The engine silently skips `Town.SetTradeBoundVillageInternal`. Closure cannot see this; only the ReadyAt plan rule prevents it. The model's NullReferenceException (line 78) and `docs/campaign-registry.md:54` ("In the engine that case throws") are wrong.
- **(b) Settlements do not move to CampaignObjectManager.** The model's `RemoveTemporaryTypes` drops Settlement. Run 416 at `visible_paused_map` shows Settlement, Town and Village still held in MBObjectManager (4/2/2), and only Hero, Faction, Kingdom and MobileParty dropped.
  - Consequence: the test "generated clan missing from the published Clans list fails closure" has no in-game equivalent for settlements.
  - E2: `village_2` absent from the published Settlements list → closure `NO PROBLEMS`, stale `[]`. The OM→list direction is never checked.
- **(c) `BeforeLoad` does not match run 33092.** It builds no Settlements list (in game, all 4 saved settlements are listed and not ready), models the stray as a presumed Hero only, and omits the Kingdom.
- **Fix:**
  - Model Village as a silent trade-bound loss, and add a check that each village is trade-bound to its bound town.
  - Keep Settlement held after initialization.
  - Have closure require every generated Settlement to be in the published Settlements list.

**3. MAJOR (latent) — `RegistrationPlan.cs` (no rule): a Hero without its NPCCharacter passes the plan.**
- Installed `Hero.Deserialize` (IL_0008-001a) does `GetObject<CharacterObject>(StringId)` then `.Age` with no null check.
- E14: Hero `ruler_2` with its NPCCharacter removed → `NO PROBLEMS`.
- In game: an NRE inside LoadXml(Heroes) after SPCultures and NPCCharacters have already merged, so a partial registration. Run 3428 shows a RegisterSubModuleObjects exception then hangs the game.
- No current producer emits an unpaired Hero.
- **Fix:** in `Problems`, require `NPCCharacter.<id>` planned earlier for every Hero; add a test.

**4. MAJOR (latent) — `RegistrationPlan.cs:203`, `:165`, `:173`: identity and collision rules never reach non-world-type records.**
- `Generated` is defined as "has the prefix, is an alias, or is a WorldType". So for Culture, NPCCharacter and the template types, "a generated id without the prefix" can never be detected.
- E4: an unprefixed `GeneratedCulture` and an uppercase `Ruler_X` NPCCharacter → `NO PROBLEMS`; both classified `Generated=False`.
- A generated culture reusing an official id merges silently; the collision check skips it.
- The ticket explicitly requires registering generated cultures.
- **Fix:** take an explicit list of generated ids from the dataset or compiler (`Dataset.Instances` / records) rather than inferring from the prefix, and apply the identity and collision checks to it.

**5. MAJOR — `CampaignObjectRegistry.cs:146-148`, `196-203`, `211-217`: the census counts campaign-created world objects as "official".**
- Run 416 world A, final census: 14 of 17 parties are `official`, including `garrison_party_mnbl_00059042_town_1_…` and `militias_of_militias_of_mnbl_00059042_…`. The 18 `CharacterObject_N` heroes are also `official`.
- E9: A-derived party ids in B's lists → `official=2 otherWorlds=0 closure=[]`.
- So the "nothing of A in B" check, and MMR-36's unexpected-Calradia census, are blind to A-derived objects.
- I checked the raw 416 data: world B contains no "00059042" substring anywhere, so c3 holds in fact, but the registry could not have detected a leak.
- **Fix:** separate "runtime-created" from "official", e.g. by snapshotting the official ids before registration. Also detect an embedded `mnbl_<8hex>_` anywhere in an id.

**6. MINOR — `CampaignObjectRegistry.cs:157`, `:227`: the census is not deterministic when closure is non-empty.**
- Closure order follows the engine's list order, which is registration order.
- E5: same objects, different insertion order → different `closure` arrays.
- The test covers determinism only when closure is empty.
- **Fix:** sort the closure problems.

**7. MINOR — `CampaignObjectRegistry.cs:233-244`: `RemoveWorldObjects` unregisters `NPCCharacter.main_hero`, which existed before the world registered.**
- E1 shows this. In game, main_hero exists at `before_generated_registration` as a presumed object that earlier content refers to, so unregistering it leaves stale referrers.
- The test's kept-templates list (`RegistrationTests.cs:291`) leaves main_hero out.
- After initialization the method cannot reach list-held heroes, clans or kingdoms, and still reports success (E10).
- `docs/campaign-registry.md:89` ("never touches an official") is false.
- **Fix:** exclude aliases, or restore their prior state. Clear or assert the campaign lists. Fail when objects remain.

**8. MINOR — `RegistrationPlan.cs:183`: the ReadyAt check is satisfied by the village's own parent settlement or by another village.**
- `Ordinal <=` treats the containing settlement as already deserialized.
- E3 (village bound to itself) and E3b (village bound to village_1) → `NO PROBLEMS`. In game both silently lose the trade-bound link.
- **Fix:** require the target ordinal to be below the parent Settlement's ordinal and the target to have a Town component.

**9. MINOR — `GeneratedCampaignRegistration.cs:115`, `:166`: the stage guards cannot fire in game.**
- The stage is a constant chosen by the caller, so "Replay refuses before BeforeLoad / after UnregisterNonReadyObjects" (`CampaignObjectRegistry.cs:92-95`, docs:64) only fires in tests.
- **Fix:** derive the stage from engine state (loading type, not-ready saved objects present), or document it as caller-asserted.

**10. MINOR (code reading only) — `RegistrationPlan.cs:195`: a reference to another world's template is accepted as shared.**
- A reference such as `Culture.mnbl_<other>_x` passes as a shared template if it is ready. Closure catches it only for census types; not for Item, EquipmentRoster and similar.
- **Fix:** refuse references whose prefix belongs to another world.

**11. MINOR — `GeneratedGameManager.cs:163`, `:277-283`: inconsistent failure handling for the registry census.**
- The census at `game_initialization_finished` is neither Traced nor fail-soft, in an engine callback. A census exception there leaves no managed record (run 7752 shows the census can throw).
- During a load, the registry census in AfterRegister is fail-hard, while the diagnostic census policy is fail-soft.
- Otherwise the DiagnosticSandboxBootstrap refactor (diff reviewed) is behaviour-preserving: seams, order, creator assignment and run-once guards are unchanged.

**12. MINOR — `WorldSaveProbe.cs:229`: load-time registry closure is recorded but never added to `failures`.** Only the offline summarizer enforces it, unlike GeneratedCampaignBootProbe.

**13. MINOR — documentation and comment inaccuracies:**
- docs:54 — the claimed throw (see 2a).
- docs:67 — says settlements move to CampaignObjectManager (see 2b).
- docs:71 and `CampaignObjectRegistry.cs:128-129` — "presumed copy" (see 1).
- `MBObjectStore.cs:108`, `CampaignObjectRegistry.cs:32-33` — say the lists are null before InitializeOnNewGame. In game, 5 of 6 are non-null and empty; only Settlements is null.
- docs:34 — the save-definer scan excludes Workshop dependencies. I checked by reflection-only IL: ButterLib's base is 2,002,018,000 + an offset, PlayerSettlement's is 300,711,204, so no collision. `SaveIds.Base` is justified.
- `CampaignIdentity.cs:14` — "can never share an id" is false: the same seed gives the same ids, and the prefix is only 32 bits.
- docs:58-62 — "every reference": only top-level and component attributes are checked; nested references such as `Locations/@complex_template` and equipment are not.
- The 48-character limit cites a 128-character bound with no source.

**14. NIT — test weaknesses:**
- The fresh-store A/B check compares two identical `Engine()` stores (`RegistrationTests.cs:282-286`), so it proves nothing.
- The summarizer's "same shared templates" check compares counts only.
- The "no mutable static state" check misses the public mutable `ISet` contents of `RegistrationPlan.WorldTypes` and `TemporaryTypes`.
- Campaign instance identity is documented only; nothing in the mod carries or checks it. StringIds do stay free of campaign identity.

**Overclaim check:** none on compiler worlds. Docs, `registry-probe.json` and the summarizer all state that only the two-town fixture was registered in game and the 1,306-object world was planned offline.

## Criteria
- **c1 — met as worded.** Tests detect village-before-town, out-of-pattern forward references, phase misorder, duplicates, cross-type ids and collisions. Weakened by 2a (wrong engine behaviour modelled), 3 and 8.
- **c2 — not met as written (narrowly).**
  - Proven for the new game: closure before default-list publication. Settlements list is null at the `after_submodule_post_registration` census, and `generated_registry_closed` is traced before `diagnostic_creator_installed`.
  - Not met:
    - the census is not deterministic once closure is non-empty (6);
    - load-time closure passes with unready objects (1);
    - origin attribution is wrong (5).
- **c3 — met for the engine's real path.** Run 416 world B raw data contains no world-A substring, and template counts are 17/886/12/6 for both worlds. The offline fixture is weak: the fresh-store case is tautological, and the reused-store case hides the main_hero removal (7).
```

## Disposition

| # | Disposition | What changed | Verified by |
|---|---|---|---|
| 1 | **Fixed** | • `ICampaignLists` now carries each entry's readiness.<br>• Closure has three modes:<br>&nbsp;&nbsp;– Registration: everything registered and ready.<br>&nbsp;&nbsp;– Replay: the permanent types ready; saved clans, heroes, kingdoms and parties need only be listed, documented as such.<br>&nbsp;&nbsp;– Settled: everything ready wherever it lives.<br>• An object both registered and listed is decided by the object manager (one instance); a listed-only object by its list entry.<br>• Settled closure runs at the visible map and at `saved_game_loaded`, and both probes enforce it as failures.<br>• The misleading `stale` field is replaced by `notReady` and, for Settlements, `notInObjectManager`.<br>• The docs now say the not-ready heroes and kingdoms are the saved instances. | Tests:<br>• a listed clan that is not ready fails settled closure;<br>• during a load settled closure reports the saved clans not ready, while replay closure passes;<br>• after `FinishLoad` settled closure is clean.<br>In game: final settled closure clean after the new games and after the load. |
| 2 | **Fixed** | The engine model is rebuilt from the installed IL and the runs:<br>• (a) an early village silently loses its trade-bound link (no throw), and registration closure checks `Town.TradeBoundVillages` (`IObjectStore.TradeBound`; in game, `MBObjectStore` reads the town's list);<br>• (b) Settlement, Town and Village stay held after initialization; Hero, Faction, Kingdom and MobileParty move to the lists, and the Settlements list and object manager must agree both ways;<br>• (c) `BeforeLoad` builds a Settlements list and holds a saved hero and a saved kingdom not ready in the object manager.<br>Docs corrected. | Tests:<br>• a village before its town: the model silently drops the link, closure detects it, and the plan refuses first;<br>• Settlements both ways;<br>• a trade-bound loss fails registration closure. |
| 3 | **Fixed** | `Problems` requires `NPCCharacter.<id>` planned before every Hero. | Test: a hero without its character is refused, and the model's `Hero.Deserialize` throws. |
| 4 | **Fixed (as suggested)** | `FromDocuments` takes the producer's declared generated keys: `GeneratedCampaignDataset.Instances` plus aliases, in game. Compiler plans declare every record. Identity and collision rules apply to every declared object, and a declared object that is in no document is refused. | Tests: an unprefixed generated culture, an uppercase character, a generated culture reusing `sturgia`, and an undeclared document object are all refused. |
| 5 | **Fixed (as suggested)** | • Census v2 takes a snapshot of the objects present before registration and classifies every object as this world, derived from this world, another world (prefix or embedded), alias, official (present before, or an installed record the plan re-emits) or runtime-created.<br>• Closure flags another world's object even when its prefix is embedded.<br>• `officialIdsSha256` per type. | Tests:<br>• `garrison_party_<A>…` counts as this world's derived, `CharacterObject_5` as runtime-created;<br>• `militias_of_<B>…` is another world's and fails closure;<br>• an A object in B's object manager fails B's registration;<br>• an installed record first loaded by the registration counts as official.<br>In game: B's official template ids hash the same as A's. |
| 6 | **Fixed** | Closure problems are sorted. | Test: identical census with non-empty closure across insertion orders. |
| 7 | **Fixed** | `RemoveWorldObjects` never removes an alias that existed before registration, and reports what remains (registered or listed). The docs are corrected. | Tests:<br>• `main_hero` survives removal in a reused store;<br>• after initialization, removal reports the list-held clans and heroes. |
| 8 | **Fixed (as suggested)** | A `ReadyAt` target must come before the village's own settlement and must be a town or castle (it has a Town component). | Tests: a village bound to itself or to another village is refused. |
| 9 | **Fixed (engine-derived)** | `Replay` also refuses when no saved permanent object of the world is registered and waiting for its XML. That is the engine state BeforeLoad leaves, absent before it and gone after `UnregisterNonReadyObjects`. The caller's stage remains as a second guard. | Test: a store claiming `SavedObjectsRegistered` with nothing waiting is refused. In game: run 9280 replayed (the guard passed). |
| 10 | **Fixed** | A reference whose world prefix is another world's is refused, whatever its type. | Test: `Culture.<B>_culture` referenced from world A is refused although registered. |
| 11 | **Fixed** | `GeneratedGameManager.RecordCensus` is fail-soft and traced at both phases (a failure is recorded as `censusFailed`). The closure assertion still throws. | Code reading; in-game censuses recorded at both phases. |
| 12 | **Fixed** | `WorldSaveProbe` adds the settled census's closure problems and other-world prefixes to its failures. | Run 2244 failed on exactly such a closure problem (the trade-bound finding below); run 9280 passed. |
| 13 | **Fixed** | Docs and comments corrected: the throw, the settlement move, the saved instances, the list publication, the prefix collision wording, the nested-reference scope and the 48-character rationale. The save-definer scan now includes the Workshop modules (153 definers, 80 from Workshop; the nearest base is still 2.83 million away). | `evidence/runtime/save-definer-scan.json`; the identity test asserts clearance over all scanned bases. |
| 14 | **Fixed** | • The fresh-store A/B test now compares official-id hashes, and a leak-detection test injects A objects into B.<br>• The summarizer compares official-id hashes.<br>• `WorldTypes`/`TemporaryTypes` are private (exposed as `IsWorldType`/`IsTemporaryType`), and the static-state test also checks that public static collections are read-only.<br>• The campaign instance id is carried by the registry, recorded in the census, and refused inside a generated id. | Tests. |

**Found while confirming the fixes in game (run 2244).** After a cold load, the towns' `TradeBoundVillages` did not list their villages, although they did after the new game. The campaign re-derives trade bounds, so the trade-bound check is a registration invariant only. The post-load state is an open question recorded on MMR-33.

Criteria after the fixes:
- **c1:** met. The tests detect early references, including the silent village loss, the hero/character pair, self- and village-bound villages, out-of-pattern forward references and phase misorder, as well as duplicate and invalid identities over every declared generated object.
- **c2:** met. The census is deterministic, including with problems. Closure is asserted before the default lists publish, and settled closure checks readiness everywhere afterwards. Origin attribution is correct.
- **c3:** met. The in-game A-then-B run has no object of A in B, at origin level, and identical official-template hashes. Offline, a fresh store compares hashes and detects injected leaks, and a reused store keeps the templates and `main_hero`.
