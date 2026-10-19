# Campaign-scoped identities and ordered registration (MMR-30, 2026-10-15)

Every generated campaign object is registered, replayed after a load, and removed through one class: `CampaignObjectRegistry`.
- **Plan:** it comes from the generated XML documents, with the producer's own list of the objects it generated, or from the compiler's campaign records. It is checked before anything loads.
- **Registration:** loads phase by phase, in the order the R04/R05 runs proved.
- **Closure:** asserted before CampaignObjectManager publishes its default lists, and checked again once the game is settled.
- **Census:** a deterministic census of both object managers, which tells apart where each object came from. MMR-36 consumes it.

| | |
|---|---|
| Code (`src/MNBL.ProceduralWorld/Campaign/`) | Pure managed: `CampaignIdentity.cs` (namespaces, `SaveIds`), `RegistrationPlan.cs` (plan, order, reference rules), `CampaignObjectRegistry.cs` (register, replay, closure, census, removal; `IObjectStore`, `ICampaignLists`). TaleWorlds types: `MBObjectStore.cs` (live MBObjectManager and CampaignObjectManager adapters) and `GeneratedGameManager.cs` (the promoted game manager) |
| Wired into | `CampaignBootstrap/GeneratedCampaignRegistration.cs`: new-game `Register` and saved-game `ReapplyForSavedGame` go through the registry. `DiagnosticSandboxBootstrap` derives from `GeneratedGameManager` and keeps only the probe driver |
| Tests | `tests/Campaign/RegistrationTests.cs` via `tools/Test-CampaignRegistry.ps1` (54 checks, `evidence/fwg/registry-tests.txt`), against an independent model of the object manager built from installed IL and the in-game runs |
| In-game | The MMR-18/19 sequence through the registry: new world A, new world B, saves (run 21584), then a cold-restart load of both saves (run 21872). `evidence/runtime/registry-probe.json` (`tools/summarize_registry_probe.py`, 16 checks), including the MMR-19 two-world matrix on this build. Earlier runs are kept as history (see below) |
| Evidence scans | `evidence/runtime/save-definer-scan.json` (`tools/Scan-SaveDefiners.ps1`): 153 `SaveableTypeDefiner` base ids, 73 installed with the game and 80 from the Workshop modules on this machine. `evidence/runtime/object-type-registrations.txt` (`tools/Scan-ObjectTypes.ps1`): the installed `RegisterType<T>(elementName, listName)` calls |
| Review | `evidence/fwg/registry-review.md` (verbatim, with the disposition of every finding) |

## Identity namespaces

| Namespace | Mutable | Form | Source |
|---|---|---|---|
| World definition ids (engine StringIds) | no | `mnbl_<8 hex>_…`; compiler ids `mnbl_<8 hex>_<kind>_<10 hex>` (26 characters); settlement components `<settlement>_component` | MMR-27 compiler (source identity + world.json, never a display name), or the seed for the diagnostic fixture (`mnbl_<seed:x8>_<role>_<n>`) |
| Aliases | no | `main_hero`, `player_faction`, `player_party` | Installed code finds the player by these literal ids, so every world re-creates them. They are the only unprefixed generated ids, and they replace the official player definition |
| Campaign instance | yes | `campaignInstanceId`, a UUID | MMR-24 request; bound to a world by the activation receipt (MMR-29). The registry records it in the census and refuses a generated id that embeds it. Two campaigns of one world register the same ids |
| Save definition ids | no | `SaveIds.Base` = 7,910,000, `SaveIds.Version` = 1 | Module-fixed and versioned; never derived from a world, seed or campaign. MMR-50 owns the definitions and migration |

**StringIds:**
- Encoding: ASCII lowercase letters, digits and underscores.
- Length: at most `CampaignIdentity.MaxIdLength` = 48 characters. This is a module-chosen bound; no engine limit is known. Installed code derives ids that embed ours several levels deep (run 416: `garrison_party_<town>_<clan>_1`, `militias_of_militias_of_<village>…`), so ours are kept short.
- Prefixes: different worlds get different prefixes, except by a 32-bit hash collision. The same seed or source is the same world, with the same ids by design.

**Collisions:** the registry refuses a plan with any of:
- the same object twice;
- one id used by two world types (a hero's NPCCharacter and Hero share one id, by design);
- a generated id that is already registered, including a generated culture reusing an official id;
- an id without this world's prefix, or with another world's;
- an id that is too long, or not lowercase;
- a reference to an official world instance, or to another world's object;
- a declared generated object that is in no document.

Which objects are "generated" comes from the producer's own list (`GeneratedCampaignDataset.Instances` plus the aliases, or every compiler record). A generated record of any type is therefore checked, not only world-prefixed ones. A replay from a package has no such list and infers it from prefixes and world types.

**Save ids:**
- `SaveIds.Base` sits 2.83 million from the nearest of all 153 scanned bases. The highest installed base is 5,080,000.
- The scan constructs each definer out of the game process and reads its `_saveBaseId`.
- The scan does not check each definer's highest offset; MMR-50 confirms the range when it adds definitions.

## Order and references

| Phase | Document | Notes |
|---|---|---|
| 1 | SPCultures | the selected official culture, re-emitted with the generated start point |
| 2 | NPCCharacters | the shared templates the world references, then one character per hero, then `main_hero` |
| 3 | Heroes | each needs its NPCCharacter of the same id earlier (`Hero.Deserialize` reads it without a null check); `faction` points forward to the clan (two-pass) |
| 4 | Kingdoms | `owner` → Hero (earlier); `initial_home_settlement` → Settlement (two-pass) |
| 5 | Factions (clans) | `owner` → Hero, `super_faction` → Kingdom (earlier); `initial_home_settlement` → Settlement (two-pass) |
| 6 | WorkshopTypes | the installed records |
| 7 | LocationComplexTemplates | the installed records |
| 8 | Settlements | every town and castle (Town component) before any village (Village component). A village's `bound` must be a town or castle deserialized before the village's own settlement |
| (9) | Parties | created by the campaign at initialization, not registered from XML; counted by the census |

A saved game replays phases 1, 2, 6, 7 and 8. Heroes, Kingdoms and Factions are temporary types whose state lives in the save. Replayed records may reference their saved objects, which are in the deserialized CampaignObjectManager lists.

**The two-pass pattern** is the one the spike proved. `MBObjectManager.LoadXml` deserializes records in document order. A reference to an object not loaded yet creates a presumed, not-ready object, and that object's own record makes it ready later in the same registration. The registry allows the three forward references the proven registration relies on: `Hero.faction → Faction` and `Kingdom/Faction.initial_home_settlement → Settlement`. Since MMR-45 it also allows a hero's family to a later hero (`Hero.father`, `Hero.mother`, `Hero.spouse`), as the installed `Heroes.xml` has; the installed IL is cited in `RegistrationPlan.TwoPass` and `docs/world-activation.md`. Any other forward reference is an early reference and is refused.

**An early village fails silently.** `Village.Deserialize` (installed IL) reads `bound` through a presumed settlement and links the village to its town only if `Bound.IsTown`. A presumed settlement has no Town component, so an early village silently loses its trade-bound link; nothing throws. The plan rule prevents it, and registration closure also checks that every village is in its town's `TradeBoundVillages`.

**Reference checks** cover the top-level and component attributes of generated records. Two kinds of reference are left to the dataset builder's closure (`GeneratedCampaignDataset`) and to MMR-36:
- nested references (`Locations/@complex_template`, equipment);
- the references of shared templates, which are re-emitted with their official records and keep the engine's own, sometimes optional, references.

No broad XML removal and no XSLT is used. The registry never calls `ClearAllObjects`.

## Closure

Closure asserts five things:
1. Every planned object, and every reference of a generated object, is present and ready.
2. Nothing of this world is presumed or not deserialized.
3. Nothing of another world exists, by prefix or embedded in a derived id.
4. A published Settlements list and the object manager agree on this world's settlements, both ways.
5. In registration mode, every village bound to a town is trade-bound to it. `Settlement.IsTown` excludes castles, so a castle's village is never trade-bound at deserialization; `VillageTradeBoundCampaignBehavior.UpdateTradeBounds` gives it a town later. The closure therefore does not ask castle villages for one (MMR-33: run 35960 was wrongly refused; fixture run 6748 shows the castle village trading with a town).

The problems come back sorted. An object both registered and listed is one instance, so the object manager's readiness decides. A listed object the object manager does not hold is decided by its campaign-list entry, which carries readiness.

| Mode | Where | What it may expect |
|---|---|---|
| Registration | inside `Register`, and in `GeneratedGameManager.AfterRegisterSubModuleObjects`, both before `CampaignObjectManager.InitializeOnNewGame` publishes the default lists | everything registered and ready; villages trade-bound |
| Replay | inside `Replay`, and in `AfterRegisterSubModuleObjects` during a load | the permanent types ready. The saved clans, heroes, kingdoms and parties need only be listed: they are not ready until the load finishes (run 33092), and the object manager may hold a few of them, or a presumed copy, not ready until `UnregisterNonReadyObjects` |
| Settled | the probes' census at the visible paused map and after `OnLoadFinished`, enforced as failures | everything ready wherever it lives, including the campaign lists |

The trade-bound link is a registration invariant only, and only for a town's villages: `Village.Deserialize` trade-binds a village only when its bound settlement is a town. A castle's villages get theirs at runtime from `VillageTradeBoundCampaignBehavior.UpdateTradeBounds`, which iterates `Campaign.AllCastles` only (new game, load, war, peace and owner change; installed IL, found in MMR-33). It does not explain the town observation after a load (run 2244: the towns did not list their villages), which stays open. Hideout is a registered world type too (MMR-33 fixtures).

**Where objects live, observed in game:**
- **New game, after initialization:** Hero, Faction, Kingdom and MobileParty leave MBObjectManager for the campaign lists (`GetObjectTypeList<T>` returns null for them). Settlements, towns and villages stay in both (run 416).
- **Before initialization:** the Settlements list is null, and the other lists exist but are empty (run 7752).
- **During a load:**
  - BeforeLoad leaves the saved permanent objects registered and not ready;
  - the saved clans, heroes and kingdoms are in the campaign lists, not ready until the load finishes (runs 3428, 33092);
  - the object manager also holds some saved heroes and kingdoms, not ready.

**Replay timing (R05):** `Replay` refuses three situations:
- before `BeforeLoad` (it would orphan the saved objects);
- after `UnregisterNonReadyObjects` (the objects would already be gone);
- when no saved permanent object of the world is registered and waiting for its XML.

The third condition checks the engine's state, not only the stage the caller asserts. `GeneratedGameManager` calls `Replay` from `RegisterSubModuleObjects(true)`, which runs 8256 and 21872 showed is between the two.

## Census

`CampaignObjectRegistry.Census` (`mnbl.registry-census` v2) is identical for the same objects, whatever order they were registered in, and also when closure has problems. Every object has one origin:
- this world;
- derived from this world (an id the campaign built from one of ours, such as `garrison_party_mnbl_…`);
- another world (by prefix, or embedded in a derived id);
- an alias;
- official (present before this world registered, or an installed record the registration itself loaded: a generated game replaces SandBox's XML, so the stock workshop types and location templates first arrive with the registration);
- created at runtime (for example `CharacterObject_N` heroes).

It reports:
- **for each MBObjectManager type:** whether it is still held, totals, ready and not ready, counts by origin, and hashes of this world's ids and of the official ids;
- **for each CampaignObjectManager list:** publication, counts by origin, entries not ready, and, for Settlements, entries missing from the object manager;
- the other world prefixes present, the closure mode, the closure problems, and the campaign instance id.

`GeneratedGameManager` keeps a census after every submodule registered and another at `game_initialization_finished`. A census failure is recorded rather than thrown; the closure assertion still throws. The probes add a settled census at the map, or after the load, and fail on its closure. Full graph closure and the unexpected-Calradia census belong to MMR-36.

## A second campaign

There is no static state: one registry, and one plan, per campaign. The public static collections are read-only.
- **As the engine does it:** world B gets a fresh MBObjectManager, with the official templates reloaded. The in-game proof is from MMR-18, and runs 416 and 21584 repeat it through the registry. B holds nothing of A (no other-world origin in any census), and B's official template ids hash the same as A's.
- **If an object manager is ever reused:** `RemoveWorldObjects` unregisters this world's generated objects and any leftovers under its prefix. It never removes an official or shared template, or an alias that existed before this world registered: `main_hero` exists early, as a presumed object that installed content refers to. It reports what remains. After initialization, the campaign lists hold heroes, clans and kingdoms that it cannot unregister. MMR-36 rolls staged registration back through it.

## Compiler records

`RegistrationPlan.FromCompilerRecords` plans the MMR-27 records (`compiled/campaign/campaign.json`, read from the verified MMR-29 bundle) with their compiler ids. Every record becomes a declared generated object:
- heroes become NPCCharacters and Heroes, with each hero's clan from `clans[].members`;
- kingdoms link to their ruler and capital;
- clans link to their leader, kingdom and home;
- towns and castles become Settlements with a Town component;
- villages become Settlements with a Village component, planned after every town and castle.

The real size-129 world plans 1,306 objects with no problem. The XML those records become is MMR-31–35's work. Until then, the game registers the diagnostic two-town dataset through the same registry.

## In-game runs

| Run | Outcome | What it changed |
|---|---|---|
| 7752 (new game) | World A registered through the registry and reached the map, then the map census threw: `GetObjectTypeList<T>` is null for temporary types after initialization | `IObjectStore.HasType`; closure and census follow the campaign lists |
| 416, 33092 (new games, saves, cold load) | Passed on the first design | The review then showed the load-time closure accepted list-held objects without checking readiness, and the census counted campaign-created objects as official (both fixed) |
| 3428 (cold load) | The replay was refused before loading: the saved clans are not in MBObjectManager. The refusal is a `RegisterSubModuleObjects` exception, and the game then hung (failure UX is MMR-49's) | Campaign-list fallback for references and closure |
| 11432, 2244 (after the review) | New games passed. The load replayed and closed in replay mode, but the settled check found the towns not listing their villages as trade-bound after the load | The trade-bound check became a registration invariant; the observation went to MMR-33 |
| 25692, 9280 | Passed, but the census counted the installed workshop types and location templates the registration itself loads as runtime-created | Installed records the plan re-emits count as official |
| 21584, 21872 (final) | Passed: `evidence/runtime/registry-probe.json`, 16/16, including settled closure after the new games and after the load, origin-level "nothing of A in B", equal official-template hashes, and the literal MMR-19 matrix | — |

## What this does not prove

- **The registry has been exercised in game only on the diagnostic two-town fixture.** The 1,306-object compiler world is planned and checked offline, not registered in the engine (MMR-31–36).
- **Save definitions:** `SaveIds` reserves the namespace only. No `SaveableTypeDefiner` exists yet (MMR-50).
- **Loading:** normal-menu loading of a generated save, and the failure UX of a refused load, stay MMR-49's.
- **Town villages' trade bounds after a load** (run 2244) are still open, for MMR-49 (loading). MMR-33 established only the castle-village mechanism above, and its fixtures checked both villages' trade bounds after a new game.
