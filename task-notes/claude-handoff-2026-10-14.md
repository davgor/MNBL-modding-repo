# Claude handoff — 2026-10-14 (supersedes the 2026-10-13 checkpoint for current state)

Run `rn_01m4h4jk5wssr7x79xjmrp8ypg`, epic `ep_01m4fpnf646t254mnh69jhz107`, plan revision 3 (thinned; lean verification: one independent review per ticket, batched fixes, targeted runs). Claim tokens are never stored in the repo; reconcile claims through Dark Mechanicus on resume.

## Accepted this session
- MMR-24 (C++ worldbridge identity), MMR-60 (generated-world mission transitions; `evidence/runtime/mission-transition-probe.json`, run 22868).

## MMR-63 periodic east/west seam — ACCEPTED 2026-10-14 17:47 UTC (follow-ups commented on MMR-49/64/65)
- Evidence: `evidence/runtime/world-wrap-probe.json` (run `generated_campaign-12348`, module `82406613…`, all 23 checks passed), summarizer `tools/summarize_wrap_probe.py`, docs top section of `docs/runtime-capabilities.md`, research `task-notes/mmr63-periodic-seam-research.md`.
- Runtime layer: `CampaignBootstrap/PeriodicWorld.cs`, `PeriodicSeam.cs` (rebase pass + order-target canonicalization), `PeriodicPatches.cs` (11 Harmony patches), periodic queries in `DiagnosticMapScene.cs`; probe `Diagnostics/WorldWrapProbe.cs` (`wrapProbe=1`, `wrapTravel=0` skips travel legs).
- Fixture `build/research-wrap63/fixture-c` (scene `mnbl_generated_00063042`, seed 405570) must be installed with `tools/Deploy-SceneSlots.ps1` before a rerun. A full run takes ~14 min (All navigation cache ~419 s).
- Declared limits: labels/selection/nameplates (MMR-66), resume from save (MMR-49), naval encounter at the seam not run, tracks/weather not exercised, 121 planar heuristics unverified.

## MMR-59 environment rebind and MMR-19 save identity — ACCEPTED 2026-10-14 19:06 UTC (final evidence on module 174fcb0a, one shared build)
- MMR-59: `evidence/runtime/environment-rebind-probe.json` (run 24212, 23/23), fixtures `build/research-env59/w42z|w43z` (two-zone climate variants of w42/w43; same installed scenes).
- MMR-19: `evidence/runtime/two-world-load-matrix.json` (process 1 run 31624, process 2 run 8256, 17/17), worlds w42/w43; packages under `%LOCALAPPDATA%/MNBL.ProceduralWorld/worlds`; saves `mnbl_mmr19_*` kept in Game Saves.
- Both reviewed (accept with should-fix); fixes applied and verified in the final runs, then accepted.
- Every generated world now writes its package and save identity at creation (GeneratedCampaignBootProbe).

## MMR-21 backend decision — ACCEPTED 2026-10-14 19:46 UTC
- Deliverables: `docs/runtime-backend-decision.md` (backend = owned native scene bundle + `SandBox.MapScene` subclass; contract matrix, frozen contracts, workarounds, native APIs, unsupported operations with owners, provisional limits, promotion map) and `evidence/runtime/independent-review.md` (reviewer report).
- Telemetry extraction: `task-notes/mmr21-telemetry.md` (no benchmark run). Gaps: game-process RAM, per-probe hardware/settings, offline scene construction time, MMR-19 save duration, in-memory sizes.
- Main provisional limit: navigation cache build (35–75 s per 32,768-face world; 567 s for the 131,072-face periodic world; rebuilt on every load) → MMR-41/MMR-56.

- Review: accept with fixes (M1-M6, S1-S17), all applied; dispositions in the review file. Key correction: navmesh/entity loading from a scene registered after launch is UNOBSERVED (MMR-45; fallback = startup-indexed slot pool).

## MMR-25 projection — ACCEPTED 2026-10-14 20:45 UTC (review accept-with-fixes, all applied; `evidence/fwg/projection-review.md`)
- `docs/world-projection.md`: periodic-equirectangular v1 (W 800, H = W/2, central meridian 0, vertical 0.01/m above effective sea level, R10 strips/pole margin W/2). Code: `src/WorldBridge/src/{projection,source_nodes,projected_world}.cpp`, CLI `worldbridge --project`, reader now keeps radius/sea level/roads/rivers. 93 unit checks (CTest `projection`); real exports verified by `tools/summarize_projection.py` -> `evidence/fwg/projection-export.json`.
- Sea-datum calibration run `generated_campaign-4768` (`Diagnostics/SeaDatumProbe.cs`, `seaDatumProbe=1`): engine water level -100 everywhere on generated maps (no water entity) -> no native water surface at z=0; MMR-39 must add one (official naval Main_map uses `global_water`).
- worldbridge.exe hash is now 5181fa78… (deployed); no static pin needed it. terrain.bin carries z + below-datum + water_type rasters; policy calibration path is relative to the policy and verified at load.
- Open for MMR-26/37: physical domain 1600x1200 vs square-only surface encoder.

## MMR-26 terrain compiler — ACCEPTED 2026-10-14 21:36 UTC (review accept-with-fixes, all applied; `evidence/fwg/terrain-review.md`)
- `docs/terrain-compiler.md`; `worldbridge --compile-terrain` (seam-aware bilinear height, nearest categories, intent, coastline coherence ±0.05, rock = max(biome, slope), 16-bit global range). Encoder accepts `--surface-weights`. Policy `worldgen/policies/terrain-materials.json`, calibration `worldgen/calibration/terrain-import/`, schema `worldgen/contracts/terrain-metadata.schema.json`.
- Projection policy poleMarginRatio 0.5 -> 0.75 (square 1600x1600 physical domain); projection evidence regenerated.
- Evidence `evidence/fwg/terrain-compile.json` (independent re-derivation of size 129; sizes 129/257/513/1025 fidelity -> source size frozen at 129). Visual probe `native_render-4988` (5 views, slot `mnbl_terrain26_s129` deployed). Found: 75% polar padding; spikes from 2.5x vertical exaggeration.

## MMR-27 campaign semantics — ACCEPTED 2026-10-14 22:44 UTC (review accept-with-fixes, all applied; `evidence/fwg/campaign-review.md`)
- `docs/campaign-semantics.md`; `worldbridge --compile-campaign` -> id-map.json + campaign.json (towns/villages/castles/kingdoms/ruling clans/heroes/ports, typed pending slots, provenance, exclusions). Stable ids `<prefix>_<kind>_<hash10>`, prefix = hash of the source identity + world.json SHA-256 (distinct per world).
- Policies `worldgen/policies/campaign-content.json` (17 generator hero roles, hamlet roles, statuses, occupations with installed-template evidence and town/village settlement kind, name templates, minima) and `culture-presentations.json` (12 civilizations -> official cultures). Schema `worldgen/contracts/id-map.schema.json`. The `heroes` block (v6) is now pinned in `source-compatibility.json`; the reader fixture has an empty heroes block.
- Review fixes that later tickets inherit: harbourmaster -> Merchant (no ShipWright heroes are installed); reeves -> Headman at their hamlet (presence); every hero home, culture and civilization reference is checked; player-adopted (`founded_by`) rows are refused (MMR-30 owns the player faction); land hamlets per core are capped by hamlets_per_core.
- Finding: the ticket's '24-city ceiling' is only the explicit settlement_count cap; inferred-settings worlds place by habitat packing (45 cities at 129).
- Evidence `evidence/fwg/campaign-semantics.json`: independent re-derivation of all 5 sizes (19 checks each, incl. installed-template counts via `--game`), repeat identical. Unit 88/88, reader 139/139, CTest 7/7.
- Flaky: `process_contract` case 19 (grandchild termination) failed once under CPU load (beside the 1025 compile) and passed alone.

## MMR-28 travel graph — ACCEPTED 2026-10-15 05:20 UTC (review accept-with-fixes, all applied; `evidence/fwg/travel-review.md`)
- `docs/travel-graph.md`; `worldbridge --compile-travel` -> travel-graph.json + ports.json + cells.bin + campaign.json (MMR-27 records after exclusions, with MMR-28 position/portSite slots resolved). The terrain and campaign are compiled in memory; the recorded hashes equal the MMR-26/27 artifacts.
- Policy `worldgen/policies/playability.json`; engine contract `worldgen/calibration/naval-transitions/naval-transitions.json` (MMR-17 run 2248 + the installed War Sails port-gate envelope, max 10.97 units); schemas travel-graph and ports.
- Ports serve the gate: the coast pair of the gate's land component nearest the gate, within 2 cells, never shared. Coast crossings never join land; bridges are river/lake runs of at most 2 cells.
- Generation driver: a request binding playability and the 5 compiler policies runs the gate per attempt. Each attempt keeps travel/ outputs, and its reasons carry UIDs. The 4 travel codes are retryable; compiler refusals fail at once (`evidence/fwg/travel-gate.json`, 6/6).
- Size 129 accepted: 30/44 ports, 6 village exclusions, 11 player-safe groups. The other sizes are rejected for resolution reasons (outside MMR-26's frozen size).
- Unit 77/77, reader 142/142, CTest 8/8; verifier 22/22 on all 5 sizes; repeat identical.

## MMR-29 world data bundle — ACCEPTED 2026-10-15 06:25 UTC (review: 0 blockers, 4 majors, all fixed; `evidence/fwg/bundle-review.md`)
- `docs/world-bundle.md`. `worldbridge --bundle` → `<root>/<WorldContentId>` (source/, compiled/ incl. `compatibility.json`, policies/, manifest.json, DATA_READY last). `--inspect-bundle` validates without the game.
- WorldContentId covers compatibility (game/War Sails versions, capabilities): a game update gives a new bundle. Reuse only when valid and equal apart from provenance (`source.recipe`, `source.producer`, `compiler`, `engine.runtimeLockSha256`); otherwise `bundle_conflict`. Roots deeper than 73 chars are refused (`path_too_long`); the staging dir is validated before the rename (`staging_invalid`).
- C# host: `CampaignBootstrap/WorldDataBundle.cs` (pure managed; Open / OpenForSave / ReadVerified; saves bind by WorldContentId). Not wired into any construction path yet (MMR-37/44).
- Three validators (C++ validate, C# Verify, Python `summarize_bundle.verify_bundle`) share rules; `tools/test_bundle_parity.py` (run by `tools/Test-WorldDataBundle.ps1`) must stay 35/35 when any of them changes.
- Real bundle: `build/research-bundle29/bundles/f596b58c…` (889 files, 143.7 MB); `build/research-bundle29/run.sh` republishes it.

## MMR-30 campaign registry — ACCEPTED 2026-10-15 08:31 UTC (review: 0 blockers, 5 majors, 9 minors, all fixed; `evidence/fwg/registry-review.md`)
- `docs/campaign-registry.md`. `src/MNBL.ProceduralWorld/Campaign/`: `CampaignIdentity`, `RegistrationPlan`, `CampaignObjectRegistry` (pure managed), `MBObjectStore`/`CampaignLists` (TaleWorlds adapters), `GeneratedGameManager` (promoted manager; `DiagnosticSandboxBootstrap` derives from it). Namespace is `MNBL.ProceduralWorld.CampaignRegistry` (a namespace named `Campaign` would shadow TaleWorlds' `Campaign`).
- Engine facts that later tickets must respect (installed IL + runs): an early village silently loses its trade-bound link (no throw); `Hero.Deserialize` needs its NPCCharacter; after init Hero/Faction/Kingdom/MobileParty leave MBObjectManager (settlements stay); during a load the saved clans/heroes/kingdoms are in the CampaignObjectManager lists, not ready until the load finishes; `MBObjectManager.GetObject(string,string)` asserts on unknown element names (use typed lookups; map in `evidence/runtime/object-type-registrations.txt`).
- Closure modes: Registration (before default lists), Replay (load), Settled (probes at map / after load). Census v2 origins: thisWorld, thisWorldDerived, otherWorlds (prefix or embedded), aliases, official, runtimeCreated.
- `SaveIds.Base` 7,910,000 (clear of 153 scanned definers incl. Workshop; `tools/Scan-SaveDefiners.ps1`). MMR-50 owns the definers.
- Offline `tools/Test-CampaignRegistry.ps1` 54/54; in game runs 21584 + 21872 (`tools/summarize_registry_probe.py` 16/16). A refused load hangs the game (MMR-49). Post-load trade bounds differ from new game: comment on MMR-33.
- In-game reproduce: Build.ps1, Deploy-Dev.ps1, Start-OwnedProbe generated_campaign (worldCount 2, saveProbe 1, args in `evidence/runtime/two-world-load-matrix.json` reproduce), Collect, generated_save_load with the oracle path exactly as recorded (backslashes), Collect.

## MMR-31 faction definitions — ACCEPTED 2026-10-15 09:16 UTC (review: 1 blocker, 1 major, 9 minors, 3 nits, all fixed; `evidence/fwg/factions-review.md`)
- `docs/factions.md`.
  - Code: `Campaign/FactionFactory.cs` (pure managed). `Build(campaign, idMap, factions policy, presentations, templates)` returns intents plus the Kingdoms and Factions XmlDocuments and DeclaredKeys for the MMR-30 registry.
  - Policy: `worldgen/policies/factions.json`, written by `tools/make_factions_policy.py`.
  - Culture contract: `worldgen/templates/cultures/` (contract v2), written by `tools/Scan-CultureContract.ps1`.
- Facts C03 (MMR-32) must use:
  - Write Heroes.xml `faction` from the intents' `affiliations`, not from the compiler's `hero.clan`: that still names the ruling clan for all 52 vassal founders. `Kingdom.Deserialize` sets RulingClan from the owner's clan, and `Clan.Deserialize→SetLeader` moves a leader into the clan it leads.
  - Ruler relations (`relations[].rulerRelation`, −20..20) are hero relation intents for C03 to apply once the heroes exist.
  - Never write a negative kingdom relationship value: `Kingdom.Deserialize` declares war on any value below 0.
- Size 129 (the real world in the bundle):
  - 40 kingdoms (8 regents), 40 ruling clans and 52 vassal clans, covering all 92 lords.
  - Every ruling clan holds only its ruler.
  - 132 banners, all unique; 2 realms renamed with the ordinal.
- Tests: `tools/Test-Factions.ps1 -Bundle build/research-bundle29/bundles/f596b58c…` gives 29/29. No in-game run, because nothing is registered until C03/C04 exist.

## MMR-32 heroes and rosters — ACCEPTED 2026-10-15 10:52 UTC (review: 0 blockers, 6 majors, 10 minors, nits, all fixed; `evidence/fwg/heroes-review.md`)
- `docs/heroes.md`.
  - Pure: `Campaign/HeroFactory.cs`, `CharacterTemplateResolver.cs`.
  - Game: `HeroBodies.cs` (explicit bodies), `GeneratedCharacterCreation.cs` (creation culture filter plus a guarded Harmony prefix on the culture screen's sort).
  - Probe: `Automation/HeroBodiesProbe.cs`.
  - Policy: `worldgen/policies/rosters.json`, written by `tools/make_rosters_policy.py`.
- What it produces:
  - Lords and synthesized spouses and heirs are XML. Women wear their culture's female lord rosters.
  - Notables and companions are runtime intents for MMR-34 (`CreateSpecialHero` + `SetName`, with an intent-id map).
  - All 765 MMR-32-owned compiler slots are resolved.
- Size 129:
  - 241 XML heroes (92 lords, 88 spouses, 61 heirs; 114 women);
  - 909 notables;
  - 28 companions, from the installed float loop.
- Tests: `tools/Test-Characters.ps1` gives 19/19.
- In game, `tools/summarize_character_creation.py` gives 43/43 over two runs:
  - run 17280: 7 cultures, plus 241 engine-drawn bodies;
  - run 27608: empire and sturgia only.
- Run 18560 crashed: the installed `SortCultureList` uses `Single` on the six vanilla cultures. That is why the sort prefix exists.
- Probe args: `creationAudit=1`, `restrictCreationCultures` and `creationCultures` (same list), `heroBundle`, `repo`.

## MMR-33 settlements and economy — ACCEPTED 2026-10-15 13:01 UTC (review: 0 blockers, 5 majors, 12 minors, nits, all fixed; `evidence/fwg/settlements-review.md`)
- `docs/settlements.md`.
  - Pure: `Campaign/SettlementFactory.cs`; `Element()` is shared by the world and the fixtures.
  - Probe: `Automation/SettlementFixturesProbe.cs`, with the fixture entity shim in `DiagnosticMapScene.AddNewEntityToMapScene`.
  - Policy: `worldgen/policies/economy.json` (v2), written by `tools/make_economy_policy.py`.
  - Prototypes: `worldgen/templates/settlements/prototypes.json`, written by `tools/make_settlement_templates.py`. Village prototypes are split into coastal and inland.
- Binding: compiler town, then a distance pass (1.02× the town spacing, else remote within the engine's 3× trade limit), then castles take villages from their garrison town, then from the fullest town of their kingdom.
- Balance, food and fishing rules:
  - The balance band (villages and hearth per fief within 0.75-1.5× installed) and a town food minimum (10 a day) are refused when violated.
  - The food table comes from the IL and is checked in game; grapes are food.
  - Harbour villages get ports at their MMR-28 harbour cells, and fishing types need a port.
- Size 129:
  - 45 towns, 26 castles, 215 villages;
  - 5 distance rebinds (3 to another kingdom), 2 remote villages, 39 castle rebinds;
  - 80 village ports, 1 food redraw.
- Tests: `tools/Test-Settlements.ps1` gives 22/22, including a full-world `RegistrationPlan.FromDocuments`.
  - Settlements are clean.
  - The 150 hero family forward references (MMR-32; vanilla Heroes.xml has 127) are outside MMR-30's two-pass list, for MMR-45.
- In game: `tools/summarize_settlement_fixtures.py` gives 13/13 on run 37100. Run 30552 found the grape food error.
- Probe arg: `settlementFixtures=<repo>`. The run requires the policies made from the catalog's Settlements.
- Earlier failed runs:
  - 35960: the closure refused castle-village trade bounds; the MMR-30 store was fixed;
  - 34416: hung in GeneratedMapSupport with four settlements;
  - 36452: `SettlementVisual` NRE, which led to the entity shim.

## MMR-34 startup, spawns and behaviour compatibility — ACCEPTED 2026-10-15 15:47 UTC
- `docs/campaign-behavior-compatibility.md`; review and disposition in `evidence/fwg/startup-review.md`.
- Scan and inventory:
  - `tools/Scan-CampaignBehaviors.ps1` and `tools/BehaviorScan/BehaviorScan.cs` read the IL of 515 behaviour and model types (TaleWorlds.Core and TaleWorlds.MountAndBlade models included) into `evidence/fwg/behavior-scan.json`. Predicate lookups record the world ids their lambda compares (`lambda:"town_ES4"`).
  - `tools/make_compatibility_inventory.py` writes `worldgen/policies/campaign-compatibility.json`: 446 retained, 23 adapted, 12 inactive, 34 excluded.
- Startup:
  - Pure: `Campaign/GeneratedCampaignInitializer.cs` with `worldgen/policies/startup.json` (`tools/make_startup_policy.py`).
  - Starts for all 7 creation cultures (with fallbacks); 72 hideouts at size 129.
  - Refusals carry `quality.startup_*` codes from startup.json. Not yet called by generation: MMR-45 wires it, MMR-44 records the codes.
  - The record builders are shared with the fixture world (`GeneratedCampaignDataset.AddStartupFixtures`).
- Runtime:
  - `GeneratedStartupBehavior.cs`: intents, relations and name restore; first names; stale-campaign guard; traced problems.
  - `GeneratedPatches.cs`: the manifest (creation, startup, world, periodic) and the version guard. The world transpiler `mnbl.proceduralworld.world` fixes the wanderer introduction's `town_ES4` NRE.
  - `GeneratedGameManager.OnGameEnd`: teardown record (`LastTeardown`).
  - The package records `suppliesStartup` and `fixturePrefabs`, so a load restores both.
- Tests: `tools/Test-SpawnPolicy.ps1` 18/18; registry 57/57; settlements 22/22.
- In game: runs 39816 (audit and saves) and 14820 (cold load). `tools/summarize_startup_audit.py RUN OUT LOAD` gives 36/36.
  - MMR-19's exact snapshot comparison flags 8 path-state values of moving AI parties after the 2-day window, re-derived on load. Reported, left to MMR-62.
- Crash causes found:
  - `BanditSpawnCampaignBehavior` needs a hideout of every bandit culture (run 37036);
  - the gameplay probe stalls on bandit encounters (39908), fixed with `IgnoreByOtherPartiesTill`; the save travel uses the same;
  - entering a town without an encounter crashes the town menu (31864);
  - loading a fixture world without its prefab map crashes `SettlementVisual` (35880).
- Environment note: the game's `C:\ProgramData\Mount and Blade II Bannerlord\crashes` is a symlink to a missing Workshop cache, so the watchdog writes no dumps. Use the probe's first-chance tracing instead.

## MMR-35 ports, fleets and naval faction records — ACCEPTED 2026-10-15 17:54 UTC
- `docs/naval-content.md`; review and disposition in `evidence/fwg/naval-review.md`.
- Pure: `Campaign/NavalContentFactory.cs` with `worldgen/policies/naval-content.json` (`tools/make_naval_policy.py`: catalog + the installed War Sails Main_map scene + nested prefab and navmesh files).
- What it produces:
  - facilities (shipyard levels 9/1/19 shares by harbour quality, passed into `SettlementFactory.Build`; buildable hulls by tier in verified chains; seaward blockade arcs on water; port markers);
  - landings (refusal if not open-basin water);
  - coastal and landlocked roles;
  - pirate aliases with clustered spawn zones;
  - diagnostics and `quality.naval_*` refusals;
  - all 60 slots resolved.
- Runtime: the fleet rule is `GeneratedShipDistributionModel` (refuses buys and transfers) plus `GeneratedClanShipOwnershipModel` (ideal 0, so ships are sold).
  - Both are added in `GeneratedGameManager.InitializeGameStarter`, before `NavalDLCManager.OnGameStart` builds its model set. Run 7108 showed `OnGameStart` is too late.
  - The package keeps `navalLandlocked`.
  - `NavalDLC` is a compile reference (lock compile: true).
- Tests: `tools/Test-NavalContent.ps1` 15/15.
- In game: runs 21180 + 10724, `tools/summarize_naval_content.py` 11/11. The fixture declares clan 2 landlocked.
- Gotchas:
  - `GameEntity.PrefabExists` is false for ship prefabs on the map;
  - naval visuals refresh only for visible parties;
  - a player ship purchase drains the gold the gameplay probe needs (use `ApplyByTransferring`);
  - the MMR-33 grain 3x check can flake (3.3x once).
- Inventory now 443/26/12/34.

## Next
- MMR-36 (validate the entire generated campaign graph before activation).

## Workflow (user, 2026-10-14)
- Big batches: all spec changes → one test run → all fixes → one confirming run → done. Fail-soft probes so one run reports everything.
