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
     'status': 'fail',
     'aspects': [
         A('the producer is pinned and its build verified', 'pass',
           ['tools.lock.json', 'worldgen/producer.lock.json', 'evidence/fwg/build.log', 'evidence/fwg/ctest.log', 'evidence/fwg/producer-verification.json', 'evidence/fwg/producer-process-contract.json'],
           'fwg.exe 564c2629... from FWGC++ commit ec5d1c10; CTest 5/5; MMR-22 22/22 lock and process checks. tests/check_producer_lock.py re-checks the lock, the binary and the FWGC++ checkout. Whether a fresh build reproduces the pinned bytes is not shown; tools/Build-Fwg.ps1 does not check out the pinned commit.'),
         A('the stale generator binary is not accepted', 'pass',
           ['evidence/fwg/supervisor-tests.log', 'evidence/fwg/producer-process-contract.json'],
           'Any binary other than the pin is refused before launch (pin_mismatch), by the deploy, the package and the module itself. The stale binary in the FWGC++ tree (generator/build/apps/Release/fwg.exe) hashes 5867006c40876b4c..., not the pin (hashed by this audit, 2026-10-18).'),
         A('the game and War Sails versions are identified and pinned', 'pass',
           ['evidence/baseline/environment.json', 'config/runtime-api-lock.json', 'evidence/runtime/build-api-inputs.json', 'evidence/baseline/doctor-tests.log'],
           'Bannerlord v1.4.8.119303 and War Sails v1.2.8.119303; 25 assemblies (18 game, 6 framework, Harmony) and 6 descriptors pinned by SHA-256; build and deploy refuse other inputs.'),
         A('the Modding Kit is identified', 'pass',
           ['evidence/baseline/editor-inspection-19324.json', 'evidence/baseline/editor-prerequisite-gap.json'],
           'Identified by its Steam depot 4456490 and manifest 1015157637537524692 and one editor session that opened and rendered the War Sails map (2026-10-13; the record is an inspection, so it says ticketAccepted false). No build or player step uses it. Not re-checked since.'),
         A('generation is reproducible', 'pass',
           ['evidence/fwg/generation-identity.json', 'evidence/fwg/source-reader.json'],
           'MMR-24 75/75 identity and retry checks; MMR-23 120/120 source contract checks (re-run 2026-10-19).'),
         A('the producer\'s pinned runtime matches this installation', 'fail',
           ['evidence/one-shot/producer-process-contract-20261019.json', 'evidence/one-shot/ucrt-drift.json'],
           'Re-run 2026-10-19 (MMR-1): the producer process contract\'s preflight fails ucrtbase_matches_lock. A Windows update (KB5127647/KB5127648, 2026-10-13/14) replaced C:\\Windows\\System32\\ucrtbase.dll (now 10.0.26100.9711) after the pin, so the pinned UCRT hash 5c52e3a3... no longer matches the system\'s dedb7611.... fwg.exe still matches its pin, the contract\'s CTest passes 9/9 and every generation outcome behaves, and in-game generation ran. Re-pinning changes worldgen/producer.lock.json, a world input: an owner decision. MMR-22\'s accepted run (evidence/fwg/producer-process-contract.json) passed before the update.'),
     ]},
    {'id': 's2', 'text': "Bannerlord's New Campaign flow generates a fresh chosen-seed world locally with progress/cancel/error handling, without a player running an external editor, publishing a module or restarting the game.",
     'status': 'fail',
     'aspects': [
         A('New Procedural Campaign entry and setup screen', 'pass',
           ['evidence/runtime/world-setup.json', 'evidence/runtime/world-setup-tests.json'], 'MMR-43 run 13032 19/19; 29 offline checks.'),
         A('the generation job: progress, cancel and errors, in the running game', 'pass',
           ['evidence/generation/generation-job.json', 'evidence/generation/job-state-machine-tests.json', 'evidence/runtime/generator-probe.json'],
           'MMR-44 run 21316 17/17: cancel in 4 phases, a killed producer gives generate.crashed, largest frame gap 0.407 s. No external editor, module publishing or restart. (generator-probe.json is MMR-15\'s developer-menu run; that menu and its generation_menu probe were removed on 2026-10-18, and the setup screen covers the same flow.)'),
         A('preview, regeneration and Start Campaign', 'pass',
           ['evidence/ui/new-campaign-flow/new-campaign-flow.json', 'evidence/ui/new-campaign-flow/world-preview-tests.json', 'evidence/generation/activation-matrix.json'],
           'MMR-46 run 50304 16/16; MMR-45 run 43676 15/15 (world A committed and played).'),
         A('an interrupted generation is recovered', 'pass',
           ['evidence/generation/recovery/world-recovery.json', 'evidence/generation/recovery/recovery-tests.json'], 'MMR-47 crash run 28280 and resume run 47264, 8/8.'),
         A('the measured first-test profile', 'pass',
           ['worldgen/validation/first-test-measurements.json'],
           'MMR-56 run 35468 20/20. The first Start Campaign blocks the engine thread about 383 s (the game does not respond); the wait text says so.'),
         A('the clean package generates and starts a world', 'pass',
           ['evidence/release/clean-install.json'], 'MMR-57 run 9276: seed 42 in 37.3 s, the map after 427.7 s, 358 settlements.'),
         A('a chosen seed gives a playable world', 'fail',
           ['worldgen/fixtures/acceptance-seeds.json', 'evidence/world/second-bundle-search.jsonl', 'worldgen/validation/first-test-measurements.json'],
           'Only seed 42 starts a campaign. Seed 43 exhausts its 3 quality attempts (generate.exhausted); seeds 44-72 were refused by the quality gate on an earlier build and not re-tried; seed 73 generates and is offered with Start Campaign, but its activation is refused at the village binding (s4). docs/research/map-epic-2-findings.md estimates a random seed fails all 3 attempts about 90 % of the time. The refusals are clean, but the player cannot generally choose a seed and play it.'),
     ]},
    {'id': 's3', 'text': 'The entire generated globe is playable as a flat map with connected east/west edges. Terrain, player/AI land and naval routes, distances, encounters, camera/picking and saves use one consistent periodic topology; different seeds change geography and traversability.',
     'status': 'fail',
     'aspects': [
         A('periodic coordinates and distances', 'pass',
           ['evidence/runtime/periodic-coordinates.json', 'evidence/runtime/periodic-coordinates-tests.json', 'evidence/fwg/projection-export.json'],
           'MMR-64 run 6292 16/16; 82 offline checks on world A. East-west distances overstate sphere distance by 1/cos(latitude) (docs/periodic-coordinates.md).'),
         A('generated terrain renders and answers heights, rays and picking', 'pass',
           ['evidence/runtime/surface-probe.json', 'evidence/runtime/world-surface.json', 'evidence/world/surface-contract.json', 'evidence/fwg/terrain-compile.json'],
           'MMR-16 run 32896; MMR-37 32/32. Exact native bin-boundary rounding is unresolved (docs/runtime-backend-decision.md R02).'),
         A('land and naval routes agree with the engine on generated worlds', 'pass',
           ['evidence/world/navigation-routes.json', 'evidence/world/navigation-contract.json', 'evidence/runtime/navigation-probe.json', 'evidence/fwg/travel-graph.json', 'evidence/world/playability-report.json'],
           'MMR-38 run 42984: world A 826/826 routes, world B 1118/1118; MMR-41 21/21 with navigation caches cold, reused and faulted.'),
         A('travel, camera and picking across the seam, on the periodic fixture; travel across it on world A', 'pass',
           ['evidence/runtime/world-wrap-probe.json', 'evidence/world/periodic-movement.json', 'evidence/world/periodic-camera-picking.json', 'evidence/persistence/two-world-matrix.json'],
           'MMR-63/65/66 ran on the 4-settlement periodic fixture (mnbl_00063042). On world A, MMR-49 run 48048 crossed the seam, was pursued across it and called a fleet across it, and all of it survived save and load.'),
         A('camera and picking across the seam on a generated world; mouse dragging across it; encyclopedia map navigation', 'not-run', [],
           'Implemented; observed only on the fixture (camera, picking) or not at all (dragging, encyclopedia).'),
         A('different seeds change geography and traversability', 'pass',
           ['worldgen/validation/first-test-measurements.json'],
           'Worlds A (seed 42) and B (seed 73) differ in every geography hash (36.8 % of cells share a class) and in their engine routes (826 vs 1118). B is not playable (s4), so the difference is shown on compiled worlds and engine routes, not in play.'),
         A('encounters and missions at the seam', 'not-run',
           ['evidence/world/mission-scenes-report.json'],
           'One land mission at the seam ran (MMR-42 run 17596). A naval encounter, a siege, a raid, embarking and disembarking at the seam were not run in game (docs/runtime-backend-decision.md, docs/mission-scenes.md).'),
         A('one consistent topology everywhere', 'fail',
           ['evidence/fwg/water-climate-review.md', 'evidence/fwg/periodic-presentation-review.md'],
           'Known defects, found by review and not fixed (minor): a storm that crosses the seam can miss one hourly damage tick (water-climate-review L14); distance-sorted interface lists, menu texts and map step sounds still measure planar positions near the cut (docs/periodic-presentation.md); siege machines draw only on the settlement\'s own side of the cut. A route whose path leaves the terrain cannot be represented.'),
     ]},
    {'id': 's4', 'text': 'Generated towns, villages, castles, factions, clans and characters initialize a coherent Sandbox campaign with trading, recruitment, ownership, wars and missions, with no unintended Calradia campaign objects surviving.',
     'status': 'fail',
     'aspects': [
         A('generated factions, clans, heroes, settlements and economy register and validate', 'pass',
           ['evidence/fwg/factions.json', 'evidence/fwg/heroes.json', 'evidence/fwg/settlements.json', 'evidence/fwg/startup.json', 'evidence/campaign/graph-validation.json', 'evidence/runtime/graph-gate.json', 'evidence/runtime/registry-probe.json', 'evidence/runtime/character-creation.json', 'evidence/runtime/settlement-fixtures.json', 'evidence/runtime/startup-audit.json', 'evidence/world/settlement-entities.json', 'evidence/runtime/bootstrap-probe.json'],
           'World A: 358 settlements, 40 kingdoms, 92 clans. The MMR-30/33/34/35/36 in-game probes ran on 4-settlement fixtures; the whole world is covered by MMR-45 and MMR-51. One town starts with no gold and earns it within the day; 22 of 40 kingdoms hold a single fief.'),
         A('no unintended Calradia campaign objects survive', 'pass',
           ['evidence/campaign/graph-validation.json', 'evidence/gameplay/campaign-soak.json'],
           'The graph validator refuses official objects, references and coordinates; MMR-51 run 41920 found 0 settlements, clans or kingdoms outside the world.'),
         A('trading, recruitment, ownership and wars', 'pass',
           ['evidence/gameplay/campaign-soak.json', 'evidence/gameplay/campaign-soak-tests.txt'],
           'MMR-51 run 41920 55/55, 14 scenarios, 1 day 6 h on world A and 12 h on the fixture world. Trade and recruitment drive the screens\' logic without the screens. Long play is deferred certification.'),
         A('land missions and mission transitions', 'pass',
           ['evidence/gameplay/land-missions.json', 'evidence/runtime/mission-transition-probe.json', 'evidence/world/mission-scenes-report.json', 'evidence/world/mission-scenes-tests.json'],
           'MMR-52 run 33816 163/163 (its 14 cases); MMR-60 11/11; MMR-42 11/11.'),
         A('the remaining land missions', 'not-run', [],
           'Not opened: village raids, lord\'s hall fights, port visits. Not played through: a wetland battle (neither world has wetland), a field battle fought to the end (all ended by retreat), the hideout\'s night stealth mission; siege preparation was watched for 1.5 h only; captivity escape was applied instantly.'),
         A('every generated world initializes', 'fail',
           ['worldgen/fixtures/acceptance-seeds.json', 'evidence/fwg/world-loading-review.md'],
           'Seed 73 generates and previews, but its campaign is refused at activation: the village binding leaves towns with 6-8 villages and towns and castles with none. Recorded in reviewed evidence; no run artifact of the refused start is in the repository.'),
         A('land missions on the current package (MMR-1)', 'fail',
           ['evidence/one-shot/one-shot-run.json'],
           'One-shot run 29368: 12 of 13 world-A cases passed; field.rock failed its own check that the battle has exactly the player\'s and the enemy\'s parties (an AI lord\'s party joined; the mission opened and returned). Rerun 11472 on the same build, from another designated save, passed every case; intermittent or state-dependent is not established.'),
     ]},
    {'id': 's5', 'text': 'War Sails ports, fleets, embarkation, land/sea routes, navigation types and required distance caches operate on the generated world; naval travel and naval encounters are tested.',
     'status': 'fail',
     'aspects': [
         A('ports, fleets and naval faction records', 'pass',
           ['evidence/fwg/naval.json', 'evidence/runtime/naval-content.json'], 'MMR-35: 15 offline checks; in-game run 21180 on the fixture world.'),
         A('naval navigation types, routes and distance caches', 'pass',
           ['evidence/world/navigation-routes.json', 'evidence/world/playability-report.json'], 'Naval routes agree with the engine on world A; the Naval caches and drop-off ports are built and reused.'),
         A('water, flow and weather on the generated world', 'pass',
           ['evidence/runtime/environment-rebind-probe.json', 'evidence/world/water-climate.json', 'evidence/world/water-climate-tests.json'],
           'MMR-59 23/23; MMR-39 runs on fixtures. Rivers have no water surface; 15 of 23 lake groups on world A have no plane.'),
         A('naval travel, embarkation and encounters on world A', 'pass',
           ['evidence/gameplay/naval-scenarios.json'],
           'MMR-53 run 46036 104/104: shipyard, port and island voyages, drop-off, coastal landing, blockade, pirate battle (retreat), seaborne raid, defeat with the raft; AI 54 port and 36 island arrivals. A naval encounter at the seam is s3\'s.'),
         A('naval combat and the rest of War Sails play', 'not-run', [],
           'Not exercised: boarding, crew reinforcement, ship damage in battle, a naval battle won by fighting, a blockade battle or an AI blockade, straits narrower than 8.5, pirate-initiated encounters (the player was ignored), port visits.'),
         A('fleets survive save and load', 'pass',
           ['evidence/persistence/two-world-matrix.json'], 'Embarked and post-battle fleets survive save and load (MMR-49).'),
         A('naval play on the current package (MMR-1)', 'fail',
           ['evidence/one-shot/one-shot-run.json'],
           'One-shot run 48692: the shipyard purchase, port voyage and repair passed; the island voyage (Korpor to Frosrin) landed after about 9 h and walked back to Korpor, so the blockade, drop-off, pirate battle, raid and defeat cases (each starting where the previous one ends) failed after it. Rerun 7368 on the same build, from another designated save, passed all 9 cases; intermittent or state-dependent is not established.'),
         A('the navigation caches reused across loads on the current package (MMR-1)', 'fail',
           ['evidence/one-shot/one-shot-run.json'],
           'The one-shot run built the caches at A\'s first start (key 2a603033...), then built them again in the matrix\'s same-process load (397 s) although they existed; the cold load after its scene was re-encoded read another key (175479b6..., 34.9 s). Cause not identified.'),
     ]},
    {'id': 's6', 'text': 'Two campaigns with different world identities can be saved, alternately loaded after a cold restart, and resumed with correct terrain, objects and navigation; missing or mismatched worlds fail safely before simulation.',
     'status': 'blocked',
     'aspects': [
         A('saves bind to their world; alternate loads after a cold restart', 'pass',
           ['evidence/persistence/two-world-matrix.json', 'evidence/runtime/two-world-load-matrix.json', 'evidence/persistence/save-binding.json', 'evidence/persistence/save-binding-tests.json', 'evidence/persistence/initial-save-recovery.json', 'evidence/persistence/loader-tests.json'],
           'MMR-49: create run 48048 37/37, cold run 39860 20/20, load check 28224 3/3; MMR-19 17/17; MMR-48 14/14. MMR-49 was not re-run after MMR-51\'s refactor; the MMR-57 clean-install reload is the latest load. 30 handled first-chance NullReferenceExceptions per load, source unknown, are recorded in docs/runtime-capabilities.md and not resolved.'),
         A('missing or mismatched worlds are refused before simulation', 'pass',
           ['evidence/persistence/two-world-matrix.json', 'evidence/persistence/compat-matrix.json', 'evidence/persistence/compatibility-tests.json'],
           'Refusals observed in game: damaged-save, missing-world, damaged-world and incompatible-build (4: one in the two-world matrix, three in the compatibility matrix). Each left the running campaign as it was. A failure after a load has started, the deferred re-encode and War Sails absent were not run in game.'),
         A('a campaign is exported and imported with its world, offline', 'pass',
           ['evidence/persistence/save-transfer-real.json'],
           'tools/Transfer-WorldSave.ps1 (a repository tool, not in the package): a real export imported into an empty store on the same PC and checked by tools/Inspect-World.ps1. That run used the first export format, which docs later found could not load without its activation evidence; the current format is tested offline only.'),
         A('a transferred campaign loads in the game', 'not-run', [], 'No imported save was loaded in game.'),
         A('the packaged module reloads a save after a reinstall', 'pass',
           ['evidence/release/clean-install.json'], 'MMR-57 run 40996: no scene left, the save loaded and its scene was re-encoded.'),
         A('two campaigns from production-generated worlds', 'blocked',
           ['worldgen/fixtures/acceptance-seeds.json'],
           'The second campaign of every two-world test is the MMR-19 fixture world (mnbl_00059042, seed 364610, no bundle), because only seed 42 activates (s2, s4). Blocked by those failures, not by an unsupported capability.'),
     ]},
    {'id': 's7', 'text': 'A clean local package completes the specified one-shot acceptance scenario and records exact build/request/world/save identities, observed results, timings and limitations. Broad seed certification, long simulation soaks and production performance targets are follow-up. Any observed correctness failure in the required scenario remains failed.',
     'status': 'fail',
     'aspects': [
         A('the clean local package (MMR-57)', 'pass',
           ['evidence/release/package-manifest-f70b44c0.json', 'evidence/release/clean-install.json'],
           'MNBL.ProceduralWorld-v0.0.1.zip f70b44c0... (assembly a61d6dc7...), 11 files; clean install 27/27 (install, disable, remove, reinstall, read-only). It is a local test package, not release-certified.'),
         A('the current package passes its audits', 'pass',
           ['evidence/release/package-manifest.json'],
           'ed63d689... (assembly bc323534...), rebuilt after the developer menu entries were removed and the probes gained fixtureB (2026-10-19); worldbridge and fwg unchanged; 0 of 30,170 game files identical, 36 embedded resources, template files are the module\'s own. Built from commit 55576ca plus the MMR-1 changes, which the MMR-1 commit records.'),
         A('the current package installed alone, with the development module and store unavailable', 'pass',
           ['evidence/one-shot/one-shot-run.json'],
           'MMR-1\'s sequence: the package extracted alone matches its manifest (11 files, no development receipt), the store started empty, the battle-scale lock held; the official files, the 107 earlier saves and the development module and store were unchanged or put back. (The cited run file records the scenario\'s overall fail; this aspect is its "sequence" step, which passed.)'),
         A('the current package through the disable, remove, reinstall and read-only sequence', 'not-run', [],
           'MMR-57\'s sequence ran on f70b44c0; it was not repeated on ed63d689 (the changes since are the menu entries and probe options).'),
         A('the one-shot acceptance scenario on the current package (MMR-1)', 'fail',
           ['evidence/one-shot/one-shot-run.json'],
           'Run 2026-10-19, tools/Test-OneShot.ps1 (docs/one-shot-acceptance.md): step 1 fail (everything passed except the producer\'s pinned UCRT, which a Windows update replaced); step 2 fail (land_missions field.rock: a third AI party joined the battle); step 3 fail (naval_missions: the island voyage turned back to its start town, the later cases cascaded); step 4 fail (the navigation caches were rebuilt on a load although they existed; camera, picking, sea crossing and a naval encounter at the seam on world A not run); step 5 blocked (no second activatable world; A\'s save, same-process and cold loads passed); step 6 blocked (cross-world callbacks need B; the refusals, hashes and orphan checks passed). The land and naval failures did not reproduce in reruns on the same build from another designated save (11472, 7368); whether they are intermittent or depend on the campaign state is not established.'),
         A('the established offline suites on the integrated sources', 'fail',
           ['evidence/one-shot/offline-suites.json'],
           'tools/Test-OfflineSuites.ps1: 45 of 46 pass (every csc harness, the bridge and package path suites, the scenario checks, the Python tests). Test-ProducerProcess fails one preflight check, ucrtbase_matches_lock: a Windows update (KB5127647/KB5127648, 2026-10-13/14) replaced C:\\Windows\\System32\\ucrtbase.dll after the producer\'s runtime was pinned, so the pinned UCRT hash no longer matches the system\'s. Its CTest (9/9) and every generation outcome pass, and in-game generation ran. Re-pinning changes worldgen/producer.lock.json, a world input: an owner decision. Test-GenerationIdentity not run (it rewrites an embedded build input).'),
     ]},
]

DEFERRED = [
    {'item': 'broad seed certification (seed corpus)', 'status': 'not-run', 'partial': ['evidence/world/second-bundle-search.jsonl', 'worldgen/fixtures/acceptance-seeds.json'],
     'note': '31 seeds were tried on an earlier build (1 published) and seeds 42, 43 and 73 on the current one. Size 129 only.'},
    {'item': 'runtime-world sweep', 'status': 'not-run', 'partial': [], 'note': 'No sweep of generated worlds through the game exists.'},
    {'item': 'long simulation soaks', 'status': 'not-run', 'partial': ['evidence/gameplay/campaign-soak.json'],
     'note': 'The longest play is 1 day 6 h on world A (MMR-51). Long-run economy, raids, sieges and naval attrition over many days are untested.'},
    {'item': 'endurance (repeated sessions, memory over time)', 'status': 'not-run', 'partial': ['worldgen/validation/first-test-measurements.json'],
     'note': 'One working-set measurement per phase in one profile run.'},
    {'item': 'all-season play', 'status': 'not-run', 'partial': ['evidence/world/water-climate.json'],
     'note': 'The weather model was checked against its formula for all four seasons on fixtures; no season-long play.'},
    {'item': 'resolution and performance certification', 'status': 'not-run', 'partial': ['worldgen/validation/first-test-measurements.json'],
     'note': 'One machine (Core Ultra 9, RTX 5080, 32 GB) at a 60 fps cap; narrow screens and UI scales measured, not run; no mission frame rate.'},
    {'item': 'production performance targets', 'status': 'not-run', 'partial': ['worldgen/policies/one-shot-budget.json'],
     'note': 'The budget is provisional and not enforced. The first start of every world, and the first start or load after a module update, blocks the engine thread for about 6.4 minutes.'},
]

IDENTITIES = {
    'game': 'Mount & Blade II: Bannerlord v1.4.8.119303 (Steam build 24573425)',
    'warSails': 'NavalDLC v1.2.8.119303',
    'moddingKit': 'Steam app 1393600 build 24575232; depot 4456490 manifest 1015157637537524692 (development only)',
    'harmony': 'Bannerlord.Harmony v2.4.2 (Steam Workshop 2859188632)',
    'producer': {'fwg.exe': '564c2629b5b92d49e52cd466a5a1aaa9339380464207ddcae669dd487d2f695e', 'fwgcppCommit': 'ec5d1c10f5ae4c32fdcef092d23ad7d51e2a30de'},
    'worldbridge': '776c160626d029e6c9b911472db1a1e7d7992c5f51188259b81fd864e654fe34 (package, MMR-49, MMR-56)',
    'package': {'archive': 'MNBL.ProceduralWorld-v0.0.1.zip', 'sha256': 'ed63d689da10e7c58369bdc6ed1938d8484d0cc5abb4633b06177601132e52aa',
                'moduleAssemblySha256': 'bc3235345ea22aebd5eb1bc72e78620f863753816a68cf9b3c520b4f76ee2249', 'releaseCertified': False,
                'oneShotTested': True,
                'cleanInstallTested': {'sha256': 'f70b44c0879d4389fced8c16e61ea5fb331995ae862acdd8bfe1ddf8d86b314f',
                                       'moduleAssemblySha256': 'a61d6dc78ed37c0198191e728b2711af866875706ab744a61acf1b8290a1a4e0'}},
    'worlds': {
        'A': {'seed': 42, 'worldContentId': 'f596b58cdcfc18798542fdef5a4d9c9f6d84abddca1425af8d0b87f521191d2d', 'campaignId': 'mnbl_1609fb72',
              'scene': 'mnbl_world_f596b58cdcfc1879_171df43a3a14', 'playable': True},
        'B': {'seed': 73, 'worldContentId': 'f63c6841085725e2c437b7d5d087a52cf4dc7d5d8fe3e2d583d46cba6a775550', 'playable': False,
              'note': 'refused at activation (village binding)'},
        'fixtures': {'mnbl_00059042': 'MMR-19 world w42, seed 364610: the second world of the two-world tests, started through the diagnostic bootstrap (its playability record says activatable false: no player start of its own)',
                     'mnbl_00063042': 'MMR-63 periodic fixture, seed 405570: the seam travel, camera and picking runs'},
    },
}

BUILD_CURRENCY = ('No single module build ran every piece of evidence: each ticket\'s in-game runs used the module assembly of its own '
                  'build (for example MMR-49 4ed3b966..., MMR-51 fcae6eea..., MMR-53 3003e293..., MMR-56 e10a2e69...), and only the MMR-57 '
                  'clean-install runs used the packaged assembly a61d6dc7.... The one-shot scenario (MMR-1, 2026-10-19) re-ran the player flow, '
                  'land and naval play, the seam and save matrix and the refusals on one build, the package assembly bc323534..., and the '
                  'offline suites on its sources; the remaining in-game evidence is from earlier builds.')


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
}


# A passing aspect that cites one part of a file whose overall verdict is graded by another aspect (criterion, aspect, path).
DESIGNED_ASPECTS = {
    ('s7', 'the current package installed alone, with the development module and store unavailable', 'evidence/one-shot/one-shot-run.json'):
        'this aspect is the run\'s "sequence" step (the installation, the snapshots, the restore), which passed; the run\'s overall fail is the one-shot aspect\'s',
}


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
        criteria.append({'id': c['id'], 'text': c['text'], 'status': c['status'], 'aspects': aspects})
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
                 'any has not run, otherwise pass. Deferred certification is listed once, under deferredCertification, and is not part of '
                 'any criterion\'s status. Built by tools/build_acceptance_index.py; MMR-1 adds the final scenario.'),
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
