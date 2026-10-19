#!/usr/bin/env python3
"""MMR-58: evidence/one-shot/acceptance-index.json, the one place that maps the epic's outcomes s1-s7 to their evidence.

The statuses, the aspects and their notes are written here by hand from the evidence (each one says why). This script adds what must not be
typed by hand:
  - every evidence file's SHA-256, whether git tracks it, and the verdict fields the file records itself;
  - a refusal when a cited file is missing, or when a criterion's status disagrees with its aspects.
A criterion is fail if any aspect fails, otherwise blocked if any aspect is blocked, otherwise not-run if any aspect has not run,
otherwise pass. MMR-1 adds the final scenario (s7) here and re-runs this script.
Usage: python tools/build_acceptance_index.py
"""
import hashlib
import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, 'evidence', 'one-shot', 'acceptance-index.json')
STATUSES = ('pass', 'fail', 'blocked', 'not-run')
VERDICT_KEYS = ('status', 'passed', 'total', 'failed', 'verdict', 'ok', 'accepted', 'result')
DATE_KEYS = ('generatedUtc', 'finishedUtc', 'utc', 'recordedUtc', 'startedUtc', 'date')


def A(name, status, evidence, note=''):
    return {'aspect': name, 'status': status, 'evidence': evidence, 'note': note}


CRITERIA = [
    {'id': 's1', 'text': 'A pinned FWGC++ build and version-matched Bannerlord/Modding Kit/War Sails setup are reproducibly identified and verified; the existing stale generator binary is not accepted as installation evidence.',
     'status': 'pass',
     'aspects': [
         A('the producer is pinned and its build verified', 'pass',
           ['tools.lock.json', 'worldgen/producer.lock.json', 'evidence/fwg/build.log', 'evidence/fwg/ctest.log', 'evidence/fwg/producer-verification.json', 'evidence/fwg/producer-process-contract.json'],
           'fwg.exe 564c2629... from FWGC++ commit ec5d1c10; CTest 5/5; MMR-22 22/22 lock and process checks. tests/check_producer_lock.py re-checks the lock, the binary and the FWGC++ checkout. Whether a fresh build reproduces the pinned bytes is not shown; tools/Build-Fwg.ps1 does not check out the pinned commit.'),
         A('the stale generator binary is not accepted', 'pass',
           ['evidence/fwg/supervisor-tests.log', 'evidence/fwg/producer-process-contract.json'],
           'Any binary other than the pin is refused before launch (pin_mismatch), by the deploy, the package and the module itself. The stale binary in the FWGC++ tree (generator/build/apps/Release/fwg.exe) hashes 5867006c40876b4c..., not the pin (hashed by this audit, 2026-10-18).'),
         A('the game and War Sails versions are identified and pinned', 'pass',
           ['evidence/baseline/environment.json', 'config/runtime-api-lock.json', 'evidence/runtime/build-api-inputs.json', 'evidence/baseline/doctor-tests.log'],
           'Bannerlord v1.4.8.119303 and War Sails v1.2.8.119303; 25 assemblies (18 game, 6 framework, Harmony) and 6 descriptors pinned by SHA-256; build and deploy refuse other inputs. The final one-shot run recorded the same versions and the package\'s module, fwg.exe and worldbridge.exe hashes.'),
         A('the Modding Kit is identified', 'pass',
           ['evidence/baseline/editor-inspection-19324.json', 'evidence/baseline/editor-prerequisite-gap.json'],
           'Identified by its Steam depot 4456490 and manifest 1015157637537524692 and one editor session that opened and rendered the War Sails map (2026-10-13; the record is an inspection, so it says ticketAccepted false). No build or player step uses it. Not re-checked since.'),
         A('generation is reproducible', 'pass',
           ['evidence/fwg/generation-identity.json', 'evidence/fwg/source-reader.json', 'evidence/world/seed-yield.json'],
           'MMR-24 75/75 identity and retry checks; MMR-23 120/120 source contract checks (re-run 2026-10-19). The shipped worldbridge (2bfdbb9d...) republished all 19 accepted bundles of the 2026-10-19 seed sweep byte for byte (seed-yield.json reproduced), worlds A and B with their content ids f596b58c... and f63c6841... unchanged.'),
         A('the producer\'s pinned runtime matches this installation', 'pass',
           ['evidence/fwg/producer-process-contract.json', 'evidence/one-shot/ucrt-drift.json', 'evidence/one-shot/offline-suites.json'],
           'A Windows update (KB5127647/KB5127648, 2026-10-13/14) replaced C:\\Windows\\System32\\ucrtbase.dll after the pin; the UCRT pin in worldgen/producer.lock.json was renewed to the installed dedb7611... on 2026-10-19 (fix the failures: the owner\'s instruction). The producer process contract\'s preflight passes again (Test-ProducerProcess in the offline suites). The producer lock is not a world input inside the bundle, so no world content id changed.'),
     ]},
    {'id': 's2', 'text': "Bannerlord's New Campaign flow generates a fresh chosen-seed world locally with progress/cancel/error handling, without a player running an external editor, publishing a module or restarting the game.",
     'status': 'pass',
     'aspects': [
         A('New Procedural Campaign entry and setup screen', 'pass',
           ['evidence/runtime/world-setup.json', 'evidence/runtime/world-setup-tests.json'], 'MMR-43 run 13032 19/19; 29 offline checks.'),
         A('the generation job: progress, cancel and errors, in the running game', 'pass',
           ['evidence/generation/generation-job.json', 'evidence/generation/job-state-machine-tests.json', 'evidence/one-shot/one-shot-run.json'],
           'MMR-44 run 21316 17/17: cancel in 4 phases, a killed producer gives generate.crashed, largest frame gap 0.407 s. The final one-shot run cancelled a generation through the setup screen (step 6) and generated seed 43\'s world (step 1). No external editor, module publishing or restart.'),
         A('preview, regeneration and Start Campaign', 'pass',
           ['evidence/ui/new-campaign-flow/new-campaign-flow.json', 'evidence/ui/new-campaign-flow/world-preview-tests.json', 'evidence/generation/activation-matrix.json', 'evidence/one-shot/one-shot-run.json'],
           'MMR-46 run 50304 16/16; MMR-45 run 43676 15/15. The final one-shot run (step 1, run 23852) generated A from the New Campaign screen in 35.0 s, finished character creation and entered the map after 395.3 s; its preview matched the live campaign (run 35592).'),
         A('an interrupted generation is recovered', 'pass',
           ['evidence/generation/recovery/world-recovery.json', 'evidence/generation/recovery/recovery-tests.json'], 'MMR-47 crash run 28280 and resume run 47264, 8/8.'),
         A('the measured first-test profile', 'pass',
           ['worldgen/validation/first-test-measurements.json', 'evidence/one-shot/one-shot-run.json'],
           'MMR-56 run 35468 20/20, re-measured by the final one-shot run 23852. The first Start Campaign blocks the engine thread about 6.5 minutes while the navigation caches are built (the game does not respond); the wait text says so.'),
         A('the clean package generates and starts a world', 'pass',
           ['evidence/release/clean-install.json', 'evidence/one-shot/one-shot-run.json'], 'The final package 51abd1cc... installed alone generated and started world A (one-shot step 1) and passed the clean-install sequence (s7).'),
         A('a chosen seed gives a playable world', 'pass',
           ['evidence/world/seed-yield.json', 'evidence/one-shot/one-shot-run.json'],
           'Owner\'s judgement asked: chosen seeds now generally give a playable world, not every one. Offline, seeds 42-73 at one quality attempt went from 1 of 32 accepted to 19 of 32 (town repairs, port and village repairs, binding), and all 19 pass every managed activation stage offline: settlements, the runtime navigation build and validator over the stand-in engine, and the naval, spawn, graph, faction, character, registry and water stages (seed-yield.json). A request gets 3 quality attempts with derived seeds. In game, seeds 42 (A) and 73 (B) were played through the final one-shot run, and seed 43 generated its second attempt\'s world (8cb59e18..., all 792 playability routes passing; refused at runtime navigation on the earlier package e20f2667 until commit 6d34cef). Seeds 56 and 67 were refused in game at settlement placement (settlement.port_far) on that package; a later fix, not in it, turns a port town\'s gate toward its port, after which the offline sweep runs the placement on all 19 and both seeds generate and start in game (runs 51808, 25336). A runtime refusal still ends the job instead of trying the next derived seed (follow-up).'),
     ]},
    {'id': 's3', 'text': 'The entire generated globe is playable as a flat map with connected east/west edges. Terrain, player/AI land and naval routes, distances, encounters, camera/picking and saves use one consistent periodic topology; different seeds change geography and traversability.',
     'status': 'pass',
     'aspects': [
         A('periodic coordinates and distances', 'pass',
           ['evidence/runtime/periodic-coordinates.json', 'evidence/runtime/periodic-coordinates-tests.json', 'evidence/fwg/projection-export.json'],
           'MMR-64 run 6292 16/16; 85 offline checks on world A (re-run 2026-10-19 with the enlarged spatial table). East-west distances overstate sphere distance by 1/cos(latitude) (docs/periodic-coordinates.md).'),
         A('generated terrain renders and answers heights, rays and picking', 'pass',
           ['evidence/runtime/surface-probe.json', 'evidence/runtime/world-surface.json', 'evidence/world/surface-contract.json', 'evidence/fwg/terrain-compile.json'],
           'MMR-16 run 32896; MMR-37 32/32. Exact native bin-boundary rounding is unresolved (docs/runtime-backend-decision.md R02).'),
         A('land and naval routes agree with the engine on generated worlds', 'pass',
           ['evidence/world/navigation-routes.json', 'evidence/world/navigation-contract.json', 'evidence/runtime/navigation-probe.json', 'evidence/fwg/travel-graph.json', 'evidence/world/playability-report.json', 'evidence/one-shot/one-shot-run.json'],
           'MMR-38 run 42984: world A 826/826 routes, world B 1118/1118; MMR-41 21/21 with navigation caches cold, reused and faulted. The final one-shot run activated A and B on the shipped bridge (both navigation reports activatable).'),
         A('travel, camera and picking across the seam', 'pass',
           ['evidence/runtime/world-wrap-probe.json', 'evidence/world/periodic-movement.json', 'evidence/world/periodic-camera-picking.json', 'evidence/persistence/two-world-matrix.json', 'evidence/one-shot/one-shot-run.json'],
           'On world A the final one-shot run (two_world_matrix create 54052) crossed the seam on land, was pursued across it, called a fleet across it and kept it all through save and load. On world B (seam_presentation 54116, MMR-66\'s sections on a generated world): display copies, camera follow and pan, fast moves, picking (copy rays equal canonical rays), tracks, alignment, a town menu across the seam and the wrap budget, between two towns joined by land across the seam.'),
         A('different seeds change geography and traversability', 'pass',
           ['evidence/one-shot/one-shot-run.json', 'worldgen/validation/first-test-measurements.json'],
           'Worlds A (seed 42) and B (seed 73) differ in every geography hash (land cells 2389 vs 4192, coastlines differ) and in their engine routes (826 vs 1118), and both are played in the final one-shot run (land missions on B included).'),
         A('encounters and missions at the seam', 'pass',
           ['evidence/gameplay/naval-scenarios.json', 'evidence/world/mission-scenes-report.json'],
           'The final one-shot naval run 51924: route.seam (sailed across the seam, one rebase, on naval navigation, at sea) and pirate.seam (a naval encounter of the party and a pirate 1.6 apart across the seam, its mission, the retreat). One land mission at the seam ran (MMR-42 run 17596).'),
         A('one consistent topology for the audited consumers; named presentation limits', 'pass',
           ['evidence/world/periodic-consumers.tsv', 'evidence/fwg/water-climate-review.md', 'evidence/fwg/periodic-presentation-review.md'],
           'Owner\'s judgement asked: the topology is one for every audited consumer, with three named presentation limits left. Every planar campaign measure the audit found (evidence/world/periodic-consumers.tsv: 105 methods, 168 calls) is transpiled periodic, now with the views the player reads as distances (the clan screen\'s distance sorts, the army overlay, the game-menu party list, the nearby culture\'s music); the storm tick at the seam (L14) was fixed by MMR-65; strip lake copies cut by the terrain\'s edge now follow their twin. Left, presentation only: the hero behaviour text measures planar near the cut (its class initializer cannot run when the set is applied); siege machines and missiles are drawn only on the settlement\'s own side of the cut; a route whose path leaves the terrain cannot be represented (docs/periodic-presentation.md, Limits).'),
     ],
     'notExercised': ['mouse dragging across the seam (it pivots on the rebased click position; axis input was injected)', 'encyclopedia, clan and kingdom screen map navigation (FastMoveCameraToPosition called directly)',
                      'a siege, a raid, embarking and disembarking at the seam']},
    {'id': 's4', 'text': 'Generated towns, villages, castles, factions, clans and characters initialize a coherent Sandbox campaign with trading, recruitment, ownership, wars and missions, with no unintended Calradia campaign objects surviving.',
     'status': 'pass',
     'aspects': [
         A('generated factions, clans, heroes, settlements and economy register and validate', 'pass',
           ['evidence/fwg/factions.json', 'evidence/fwg/heroes.json', 'evidence/fwg/settlements.json', 'evidence/fwg/startup.json', 'evidence/campaign/graph-validation.json', 'evidence/runtime/graph-gate.json', 'evidence/runtime/registry-probe.json', 'evidence/runtime/character-creation.json', 'evidence/runtime/settlement-fixtures.json', 'evidence/runtime/startup-audit.json', 'evidence/world/settlement-entities.json', 'evidence/runtime/bootstrap-probe.json'],
           'World A: 358 settlements, 40 kingdoms, 92 clans. The MMR-30/33/34/35/36 in-game probes ran on 4-settlement fixtures; the whole world is covered by MMR-45, MMR-51 and the final one-shot run. One town starts with no gold and earns it within the day; 22 of 40 kingdoms hold a single fief.'),
         A('no unintended Calradia campaign objects survive', 'pass',
           ['evidence/campaign/graph-validation.json', 'evidence/gameplay/campaign-soak.json'],
           'The graph validator refuses official objects, references and coordinates; MMR-51 run 41920 found 0 settlements, clans or kingdoms outside the world.'),
         A('trading, recruitment, ownership and wars', 'pass',
           ['evidence/gameplay/campaign-soak.json', 'evidence/gameplay/campaign-soak-tests.txt', 'evidence/one-shot/one-shot-run.json'],
           'MMR-51 run 41920 55/55, 14 scenarios, 1 day 6 h on world A; the final one-shot run\'s soak (7156) travelled, traded and recruited in a generated town. Trade and recruitment drive the screens\' logic without the screens. Long play is deferred certification.'),
         A('land missions and mission transitions', 'pass',
           ['evidence/gameplay/land-missions.json', 'evidence/runtime/mission-transition-probe.json', 'evidence/world/mission-scenes-report.json', 'evidence/world/mission-scenes-tests.json'],
           'The final one-shot land run 52204: 163/163 checks, 14/14 cases (field battles of every class A has and B\'s wetland, town, castle, village, siege, capture, hideout, crossing, retreat), each return with the world identity unchanged; MMR-60 11/11; MMR-42 11/11.'),
         A('every generated world initializes', 'pass',
           ['evidence/world/seed-yield.json', 'evidence/one-shot/one-shot-run.json'],
           'Worlds A and B activate and play in the final one-shot run. Offline, every one of the 19 accepted worlds of seeds 42-73 passes the activation\'s managed stages (seed-yield.json); the sweep found and fixed the refusals of other worlds: harbours on closed basins, unplaceable villages (a crash in three runtime readers), a deck shared by two joins and port pairs the engine cannot route.'),
     ],
     'notExercised': ['village raids on land, lord\'s hall fights, port visits', 'a field battle fought to the end (all ended by retreat or the probe\'s kills), the hideout\'s night stealth mission',
                      'siege preparation beyond 1.5 h; captivity escape played rather than applied']},
    {'id': 's5', 'text': 'War Sails ports, fleets, embarkation, land/sea routes, navigation types and required distance caches operate on the generated world; naval travel and naval encounters are tested.',
     'status': 'pass',
     'aspects': [
         A('ports, fleets and naval faction records', 'pass',
           ['evidence/fwg/naval.json', 'evidence/runtime/naval-content.json'], 'MMR-35: 15 offline checks; in-game run 21180 on the fixture world; the final one-shot run sailed from and docked at generated ports on world A.'),
         A('naval navigation types, routes and distance caches', 'pass',
           ['evidence/world/navigation-routes.json', 'evidence/world/playability-report.json', 'evidence/one-shot/one-shot-run.json'],
           'Naval routes agree with the engine on worlds A and B. The final one-shot run built A\'s navigation caches once at its start and every later load of A and B reused them (keys dadc84ff, ebc5475a).'),
         A('water, flow and weather on the generated world', 'pass',
           ['evidence/runtime/environment-rebind-probe.json', 'evidence/world/water-climate.json', 'evidence/world/water-climate-tests.json'],
           'MMR-59 23/23; MMR-39 runs on fixtures. Rivers have no water surface; 15 of 23 lake groups on world A have no plane (too shallow at the sample grid).'),
         A('naval travel, embarkation and encounters', 'pass',
           ['evidence/gameplay/naval-scenarios.json'],
           'The final one-shot naval run 51924: 117/117 checks, 15/15 cases (shipyard purchase and repair, port and island voyages, drop-off and coastal landing, blockade and anchor, a pirate battle, a voyage and a pirate encounter across the seam, a seaborne raid won, defeat with the raft).'),
         A('fleets survive save and load', 'pass',
           ['evidence/persistence/two-world-matrix.json'], 'The final one-shot matrix (create 54052, cold 30068): an embarked fleet and an AI lord\'s fleet after a naval battle survive save, same-process load and cold load.'),
     ],
     'notExercised': ['boarding, crew reinforcement, ship damage in battle, a naval battle won by fighting, a blockade battle or an AI blockade',
                      'straits narrower than 8.5; pirate-initiated encounters (the player was ignored); port visits']},
    {'id': 's6', 'text': 'Two campaigns with different world identities can be saved, alternately loaded after a cold restart, and resumed with correct terrain, objects and navigation; missing or mismatched worlds fail safely before simulation.',
     'status': 'pass',
     'aspects': [
         A('two campaigns from production-generated worlds, saved and alternately loaded after a cold restart', 'pass',
           ['evidence/persistence/two-world-matrix.json', 'evidence/one-shot/one-shot-run.json', 'evidence/persistence/save-binding.json', 'evidence/persistence/save-binding-tests.json', 'evidence/persistence/loader-tests.json'],
           'The final one-shot matrix: create 54052 38/38 (A played into mutable land and naval state across the seam and saved; B, seed 73, generated, started, changed and saved; A then B loaded in the same process), cold 30068 20/20 (a new process loads A, its scene swept and re-encoded, then B; each equal to its save). No cross-world callbacks after any load. MMR-19 17/17; MMR-48 14/14.'),
         A('missing or mismatched worlds are refused before simulation', 'pass',
           ['evidence/persistence/two-world-matrix.json', 'evidence/persistence/compat-matrix.json', 'evidence/persistence/compatibility-tests.json'],
           'In the final one-shot run: the matrix refused torn, missing-world, other-game-version saves and a Continue into a missing world; the compatibility matrix 36200 13/13 refused incompatible schema and build, missing DLC, a corrupted manifest hash and an isolated damaged copy of the world data; each left the running campaign or the menu as it was, and the bundles were unchanged.'),
         A('a campaign is exported and imported with its world, offline', 'pass',
           ['evidence/persistence/save-transfer-real.json'],
           'tools/Transfer-WorldSave.ps1 (a repository tool, not in the package): a real export imported into an empty store on the same PC and checked by tools/Inspect-World.ps1 (the first export format; the current format is tested offline only).'),
         A('the packaged module reloads a save after a reinstall', 'pass',
           ['evidence/release/clean-install.json'], 'The clean-install sequence on the final package: no scene left after the reinstall, the save loaded and its scene was re-encoded.'),
     ],
     'notExercised': ['a transferred campaign loaded in game', 'a failure after a load has started, the deferred re-encode and War Sails absent, in game']},
    {'id': 's7', 'text': 'A clean local package completes the specified one-shot acceptance scenario and records exact build/request/world/save identities, observed results, timings and limitations. Broad seed certification, long simulation soaks and production performance targets are follow-up. Any observed correctness failure in the required scenario remains failed.',
     'status': 'pass',
     'aspects': [
         A('the current package passes its audits', 'pass',
           ['evidence/release/package-manifest.json'],
           'MNBL.ProceduralWorld-v0.0.1.zip 51abd1cc... (assembly bb6c6fd2..., worldbridge 2bfdbb9d..., fwg.exe 564c2629...), 11 files; 0 game files identical, the embedded resources the project lists, the module\'s own template files. A local test package, not release-certified.'),
         A('the current package installed alone, with the development module and store unavailable', 'pass',
           ['evidence/one-shot/one-shot-run.json'],
           'The one-shot sequence: the package extracted alone matches its manifest (11 files, no development receipt), the store started empty, the battle-scale lock held; the official files, the 123 earlier saves and the development module and store were unchanged or put back.'),
         A('the current package through the disable, remove, reinstall and read-only sequence', 'pass',
           ['evidence/release/clean-install.json'], 'tools/Test-CleanInstall.ps1 on 51abd1cc... (install, disabled, removed, reinstalled, read-only).'),
         A('the one-shot acceptance scenario on the current package', 'pass',
           ['evidence/one-shot/one-shot-run.json'],
           'tools/Test-OneShot.ps1 on 51abd1cc... (2026-10-19, docs/one-shot-acceptance.md): steps 1-6 and the sequence pass, every aspect. It re-ran the earlier pass on e20f2667 after the seed-43 fixes (commit 6d34cef). The three runs between found and fixed probe failures: returns measured planar across the seam, a native crash when the matrix finished an encounter under its opening map conversation, the soak\'s volunteers taken during its journey, the seam presentation without a shelter, and a stale path after a teleport. The two runs before e20f2667 found a type-initializer failure from patching CampaignUIHelper that broke character creation, the naval raid probe that needed the main agent to kill, and the AI fleet battle left to chance in the matrix (build/mmr1/one-shot-run-f91a42f4.json).'),
         A('the established offline suites on the integrated sources', 'pass',
           ['evidence/one-shot/offline-suites.json'],
           'tools/Test-OfflineSuites.ps1 on the final sources: every csc harness, the bridge and package path suites, the scenario checks and the Python tests. Test-GenerationIdentity not run (it rewrites an embedded build input).'),
     ]},
]

DEFERRED = [
    {'item': 'broad seed certification (seed corpus)', 'status': 'not-run', 'partial': ['evidence/world/seed-yield.json', 'worldgen/fixtures/acceptance-seeds.json'],
     'note': 'Seeds 42-73 offline at one quality attempt: 19 of 32 accepted, all 19 offline-activatable (stand-in engine). In game: 42 and 73 played, 43 generated and started on the final package, 56 and 67 generated and started after the later gate fix (refused at settlement.port_far on the final package). Size 129 only; the in-game activation of the other accepted seeds is not run.'},
    {'item': 'runtime-world sweep', 'status': 'not-run', 'partial': ['evidence/world/seed-yield.json'], 'note': 'The sweep runs the activation\'s managed stages and the stand-in navigation offline; no sweep of generated worlds through the game exists.'},
    {'item': 'long simulation soaks', 'status': 'not-run', 'partial': ['evidence/gameplay/campaign-soak.json'],
     'note': 'The longest play is 1 day 6 h on world A (MMR-51). Long-run economy, raids, sieges and naval attrition over many days are untested.'},
    {'item': 'endurance (repeated sessions, memory over time)', 'status': 'not-run', 'partial': ['worldgen/validation/first-test-measurements.json'],
     'note': 'One working-set measurement per phase in one profile run.'},
    {'item': 'all-season play', 'status': 'not-run', 'partial': ['evidence/world/water-climate.json'],
     'note': 'The weather model was checked against its formula for all four seasons on fixtures; no season-long play.'},
    {'item': 'resolution and performance certification', 'status': 'not-run', 'partial': ['worldgen/validation/first-test-measurements.json'],
     'note': 'One machine (Core Ultra 9, RTX 5080, 32 GB) at a 60 fps cap; narrow screens and UI scales measured, not run; no mission frame rate.'},
    {'item': 'production performance targets', 'status': 'not-run', 'partial': ['worldgen/policies/one-shot-budget.json'],
     'note': 'The budget is provisional and not enforced. The first start of every world, and the first start or load after a module update, blocks the engine thread for about 6.5 minutes.'},
]

IDENTITIES = {
    'game': 'Mount & Blade II: Bannerlord v1.4.8.119303 (Steam build 24573425)',
    'warSails': 'NavalDLC v1.2.8.119303',
    'moddingKit': 'Steam app 1393600 build 24575232; depot 4456490 manifest 1015157637537524692 (development only)',
    'harmony': 'Bannerlord.Harmony v2.4.2 (Steam Workshop 2859188632)',
    'producer': {'fwg.exe': '564c2629b5b92d49e52cd466a5a1aaa9339380464207ddcae669dd487d2f695e', 'fwgcppCommit': 'ec5d1c10f5ae4c32fdcef092d23ad7d51e2a30de'},
    'worldbridge': '2bfdbb9d0e2ae55dc356e9a8873bd70a5cded3b647faed28d261e5abc442f53a (package 51abd1cc, the 2026-10-19 seed sweep)',
    'package': {'archive': 'MNBL.ProceduralWorld-v0.0.1.zip', 'sha256': '51abd1cc9b88dc3d2a7e3d0d8f5b15f9301a54d9288fbde23864a5d542789a66',
                'moduleAssemblySha256': 'bb6c6fd2da1a5f47ee71fc0394702a80005b4d537b1ab5db09cd9548349fd03e', 'releaseCertified': False,
                'oneShotTested': True, 'cleanInstallTested': True},
    'worlds': {
        'A': {'seed': 42, 'worldContentId': 'f596b58cdcfc18798542fdef5a4d9c9f6d84abddca1425af8d0b87f521191d2d', 'campaignId': 'mnbl_1609fb72',
              'scene': 'mnbl_world_f596b58cdcfc1879_019bf8ec927e', 'playable': True},
        'B': {'seed': 73, 'worldContentId': 'f63c6841085725e2c437b7d5d087a52cf4dc7d5d8fe3e2d583d46cba6a775550', 'campaignId': 'mnbl_214f6557',
              'scene': 'mnbl_world_f63c6841085725e2_c36448e016ee', 'playable': True},
        'fixtures': {'mnbl_00059042': 'MMR-19 world w42, seed 364610: the earlier second world of the two-world tests (no longer needed: B is production seed 73)',
                     'mnbl_00063042': 'MMR-63 periodic fixture, seed 405570: the seam travel, camera and picking runs before the generated-world seam probe'},
    },
}

BUILD_CURRENCY = ('No single module build ran every piece of evidence: each ticket\'s in-game runs used the module assembly of its own build '
                  '(for example MMR-49 4ed3b966..., MMR-51 fcae6eea..., MMR-53 3003e293..., MMR-56 e10a2e69...). The final one-shot scenario '
                  '(2026-10-19) re-ran the player flow, land and naval play, the seam on A and B, the two-world save matrix and the refusals on one '
                  'build, the package assembly bb6c6fd2..., the clean-install sequence ran on the same package, and the offline suites on its '
                  'sources; the remaining in-game evidence is from earlier builds.')


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def recorded(path):
    """The verdict fields a JSON evidence file records about itself: top-level and in nested objects up to two levels deep (for
    example accepted.passed, create.status), with lists of checks or failures counted. Lists of runs are not entered."""
    if not path.endswith('.json'):
        return None
    try:
        with open(path, encoding='utf-8-sig') as f:
            doc = json.load(f)
    except Exception as error:
        return {'unreadable': type(error).__name__}
    if not isinstance(doc, dict):
        return None
    out = {}

    def walk(node, prefix, depth):
        for k, v in node.items():
            name = prefix + k
            if k in VERDICT_KEYS + ('checks', 'failures') and (isinstance(v, (str, int, float, bool)) or v is None):
                out[name] = v
            elif k in ('checks', 'failures') and isinstance(v, list):
                out[name] = len(v)
            elif isinstance(v, dict) and depth < 2:
                walk(v, name + '.', depth + 1)
    walk(doc, '', 0)
    for k in DATE_KEYS:
        if isinstance(doc.get(k), str):
            out[k] = doc[k]
            break
    return out or None


# Recorded failures inside evidence that an aspect cites as passing, each one by design (the file says why).
DESIGNED = {
    ('evidence/generation/recovery/world-recovery.json', 'accepted.crash.status'): 'the crash run is killed on purpose and stays "running"',
    ('evidence/world/playability-report.json', 'sources.startsRefused.status'): 'run 9908 is kept to show that surviving installed start points are refused (check installedStartsRefused)',
    ('evidence/world/playability-report.json', 'startsRefused.failures'): 'the refused routes of that same deliberate run 9908',
    ('evidence/persistence/initial-save-recovery.json', 'sources.publish.status'): 'process 1 of the recovery pair is left mid-run on purpose; process 2 (run 19888) recovers it, and all 15 checks pass',
    ('evidence/one-shot/ucrt-drift.json', 'preflight.passed'): 'the record of the drift itself, from before the UCRT pin was renewed; evidence/fwg/producer-process-contract.json is the re-run after the renewal, and it passes',
}


# A passing aspect that cites one part of a file whose overall verdict is graded by another aspect (criterion, aspect, path).
DESIGNED_ASPECTS = {}


def failing_fields(rec):
    bad = []
    for k, v in (rec or {}).items():
        leaf = k.rsplit('.', 1)[-1]
        if leaf == 'passed' and v is False or leaf == 'failures' and isinstance(v, int) and not isinstance(v, bool) and v > 0 \
                or leaf in ('status', 'verdict', 'result') and isinstance(v, str) and v.lower() in ('failed', 'fail', 'error', 'refused', 'running'):
            bad.append(k)
    return bad


def tracked(rel):
    run = subprocess.run(['git', 'ls-files', '--error-unmatch', rel], cwd=REPO, capture_output=True, text=True)
    return run.returncode == 0


def final_scenario():
    """MMR-1's one-shot run, as tools/summarize_one_shot.py graded it."""
    path = os.path.join(REPO, 'evidence', 'one-shot', 'one-shot-run.json')
    if not os.path.exists(path):
        return {'status': 'not-run', 'owner': 'MMR-1'}
    with open(path, encoding='utf-8-sig') as f:
        run = json.load(f)
    return {'status': run.get('status'), 'owner': 'MMR-1', 'run': 'evidence/one-shot/one-shot-run.json', 'startedUtc': run.get('startedUtc'),
            'finishedUtc': run.get('finishedUtc'), 'steps': run.get('summary'), 'identities': run.get('identities'),
            'reruns': [{k: r.get(k) for k in ('of', 'result', 'ok')} for r in run.get('reruns') or []]}


def aggregate(aspects):
    states = [a['status'] for a in aspects]
    for s in ('fail', 'blocked', 'not-run'):
        if s in states:
            return s
    return 'pass'


def main():
    problems, cache = [], {}

    def entry(rel):
        if rel not in cache:
            full = os.path.join(REPO, rel.replace('/', os.sep))
            if not os.path.isfile(full):
                problems.append('missing evidence file: ' + rel)
                cache[rel] = {'path': rel, 'missing': True}
            else:
                cache[rel] = {'path': rel, 'sha256': sha(full), 'trackedByGit': tracked(rel), 'recorded': recorded(full)}
        return cache[rel]

    criteria = []
    for c in CRITERIA:
        for a in c['aspects']:
            if a['status'] not in STATUSES:
                problems.append('%s: unknown status %s' % (c['id'], a['status']))
        if aggregate(c['aspects']) != c['status']:
            problems.append('%s: status %s, but its aspects give %s' % (c['id'], c['status'], aggregate(c['aspects'])))
        aspects = [dict(a, evidence=[entry(p) for p in a['evidence']]) for a in c['aspects']]
        for a in aspects:
            if a['status'] != 'pass':
                continue
            for e in a['evidence']:
                if (c['id'], a['aspect'], e['path']) in DESIGNED_ASPECTS:
                    continue
                for field in failing_fields(e.get('recorded')):
                    if (e['path'], field) not in DESIGNED:
                        problems.append('%s "%s": %s records %s = %r' % (c['id'], a['aspect'], e['path'], field, e['recorded'][field]))
        criteria.append({'id': c['id'], 'text': c['text'], 'status': c['status'], 'aspects': aspects, 'notExercised': c.get('notExercised', [])})
    deferred = [dict(d, partial=[entry(p) for p in d['partial']]) for d in DEFERRED]
    if problems:
        print('acceptance index refused:\n  ' + '\n  '.join(problems))
        return 1
    files = sorted(cache.values(), key=lambda e: e['path'])
    doc = {
        'schema': 'mnbl.one-shot-acceptance-index', 'version': 1, 'ticket': 'MMR-58', 'epic': 'ep_01m4fpnf646t254mnh69jhz107',
        'note': ('The local test package is not release-certified. Statuses: pass (the aspect\'s stated scope is evidenced and passes; what '
                 'the criterion needs beyond that scope is its own not-run aspect), fail (an observed or '
                 'recorded correctness failure), blocked (cannot be exercised because of a recorded failure or an unsupported capability), '
                 'not-run (no evidence yet). A criterion is fail if any aspect fails, otherwise blocked if any is blocked, otherwise not-run if '
                 'any has not run, otherwise pass. An aspect is graded when the criterion\'s text or the one-shot scenario it names asks for '
                 'it; play the criterion does not name and that no run exercised is listed under the criterion\'s notExercised, ungraded. '
                 'Deferred certification is listed once, under deferredCertification, and is not part of any criterion\'s status. Built by '
                 'tools/build_acceptance_index.py; MMR-1 adds the final scenario.'),
        'summary': {c['id']: c['status'] for c in criteria},
        'criteria': criteria,
        'deferredCertification': deferred,
        'identities': IDENTITIES,
        'buildCurrency': BUILD_CURRENCY,
        'evidenceFiles': {'count': len(files), 'untracked': [e['path'] for e in files if not e.get('trackedByGit')]},
        'designedFailuresInPassingEvidence': [{'path': p, 'field': f, 'why': why} for (p, f), why in sorted(DESIGNED.items())]
                                             + [{'criterion': c, 'aspect': a, 'path': p, 'why': why} for (c, a, p), why in sorted(DESIGNED_ASPECTS.items())],
        'finalScenario': final_scenario(),
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('acceptance index: %s; %d evidence files (%d not tracked by git)' % (', '.join('%s %s' % kv for kv in doc['summary'].items()), len(files), len(doc['evidenceFiles']['untracked'])))
    return 0


if __name__ == '__main__':
    sys.exit(main())
