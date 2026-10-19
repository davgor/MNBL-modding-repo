# Navigation caches and runtime playability (MMR-41)

**Caches.** A generated world's navigation caches are built once, with the installed mechanism, and published under a key holding every input they depend on. A later campaign map of the same world reads them back, and anything else regenerates them.

**Route set.** After placement, the semantic route set runs in both directions through engine queries:
- village to market;
- capital to fief;
- player start;
- port to port;
- faction access.

**Flags and report.** Every pending-runtime flag the compiler emitted is resolved or failed. The activation report MMR-45 requires is written.

| Piece | File |
|---|---|
| Cache key, the installed cache format read without the engine, the published store's check and manifest | `World/NavigationCacheKey.cs` |
| Reuse, generation, publication, verification, sweep | `World/WorldNavigationCache.cs`, called by `CampaignBootstrap/GeneratedMapSupport.RegisterNavigationCaches` (`GeneratedMapScene.AfterLoad`) |
| Semantic route set, flag resolution, report, gate | `World/RuntimePlayabilityValidator.cs` |
| The route set in a campaign | `World/CampaignPlayability.cs` |
| One route through the engine (shared with MMR-38's matrix) | `World/NavigationValidator.cs` (`EvaluateRoute`) |
| Production integration | `Generation/GenerationStages.cs` (`RuntimeStage`), `World/RuntimeWorldBackend.cs` (`Sweep`) |
| Probes | `Automation/WorldNavigationProbe.cs` (worlds A and B), `Automation/GeneratedCampaignBootProbe.cs` (`playabilityProbe`, `playabilityStartCultures`, `navigationCacheVerify`, `navigationCacheFault`) |
| Offline tests | `tests/World/PlayabilityTests.cs`, `tools/Test-Playability.ps1` |
| Evidence | `evidence/world/playability-report.json` (in game), `evidence/world/playability-tests.json` and `.txt`, `evidence/world/playability-offline.json`, `evidence/fwg/playability-review.md` |

## Navigation caches

**What they are.** `MapDistanceModel` reads three installed `SandBoxNavigationCache` structures, Default, Naval and All:
- the distance and land ratio between every pair of settlement entrances (gate or port);
- each fortification's neighbours;
- each navmesh face's closest entrance.

`GenerateCacheData` builds them on the campaign map, which MMR-17 (R03) proved on generated worlds. It is the dominant load cost: 35–75 s at 32,768 faces and 567 s on the periodic fixture (`docs/runtime-backend-decision.md`). Before MMR-41 it ran on every new game and every load.

**The installed file format.** `NavigationCache.Serialize` writes:
- the scene XML CRC and the navigation mesh CRC, which it takes from `IMapScene`, so they are the engine's native values;
- every entrance pair, with the land ratio for All;
- the neighbour pairs;
- the closest entrance of every face.

`Deserialize` reads the CRCs and discards them. Vanilla reads these files from `ModuleData/DistanceCaches/settlements_distance_cache_<Type>.bin` through `SettlementPositionScript`. A generated scene has no such script, and this module never calls `GetSettlementsDistanceCacheFileForCapability`, so no installed cache is ever read.

**The key** (`NavigationCacheKey`, canonical text with one `name=value` line per input, then one line per settlement in id order; floats round-trip; SHA-256):

| Input | Values |
|---|---|
| game | the game and War Sails versions; the distance and party navigation models (type and assembly version); `Campaign.PathFindingMaxCostLimit`; per navigation type the cache's excluded faces, region switch costs, agent radius and neighbour cost multiplier |
| world | the world id, the scene name, `navmesh.bin`'s hash, the native scene XML and navigation mesh CRCs |
| positions | every settlement's id, kind (with port), position, gate and port as the campaign holds them: MMR-40's placement, with the harbour villages' drop-off ports applied (below) |
| periodic | `planar`, or the coordinate policy version (`mmr64/1`) and `PeriodicWorldCoordinates.Identity` (width, origin, extent: MMR-64 builds the caches through the periodic map scene, so their distances are periodic); `PeriodicDistanceModel.Version` |
| code | the adapter (`mmr41/1`) and this module's SHA-256, since the periodic path queries and the closest-settlement order are this module's code |
| schema | the key and cache schema |

**Drop-off ports.** War Sails gives harbour villages their ports when its `NavalMapSceneWrapper` starts, after `AfterLoad`: each `main_map_village_dropoff` entity's parent names a village, which gets the entity's global position as its port (`SetPortPosition`, which also sets `HasPort`). The caches are built in `AfterLoad`, so `RegisterNavigationCaches` applies the same ports first (`ApplyDropOffPorts`). It finds the village with `Settlement.Find`, because `Village.All` is still empty then (runs 17060 and 17064), and falls back to the entity's own `<village>_dropoff` name. With `playabilityProbe`, the campaign start rebuilds the key after War Sails' wrapper has run and requires it equal to the acquired key.

**Publication** (`WorldNavigationCache.Acquire`):
1. When no published cache passes the check, the caches are generated and registered.
2. They are serialized with the installed `Serialize` into `<store>/navigation-caches/staging-<guid>`, and each file is flushed.
3. The manifest is written last, durably. It holds the key text and hash, the scene, the CRCs, each file's hash, size and record counts, the durations and the module hash.
4. The staging folder is moved whole to `<store>/navigation-caches/<key sha>`. A refused cache in the way is moved aside and deleted first.

**Reuse.** The published folder of the computed key is checked (`NavigationCacheStore.Check`):
- the manifest's schema;
- the key text and hash, with the differing inputs named;
- each file's hash;
- each file parsed (truncated, trailing and implausible files are refused);
- the native CRCs in each file equal the scene's;
- every settlement it names is in the key;
- every face index is inside the navmesh.

Then all three files are deserialized before any is registered, and registered (`RegisterDistanceCache`, which runs `FinalizeInitialization`). A throw while deserializing or registering refuses the copy: the registered caches are cleared and generated.

**Fallbacks and sweep.**
- A refused or unreadable cache is regenerated and republished, with the reason recorded (`regenerated`). A missing cache is `generated`. Neither is ever counted as reused.
- The persisted copy is an optimization, so the layer fails soft. If the key cannot be built, the caches are generated unpublished. If publication fails, the error is recorded and the registered caches stay. An exception escaping `AfterLoad` would stop the campaign (run 30712 hung on one).
- `RuntimeWorldBackend.Sweep` removes staging, discard and verify folders left by an earlier process, and the caches of the world scenes it deletes (never those of retained scenes).
- A world scene (a compiled world's slot) skips MMR-33's fixture-identity contract and goes straight to the caches; fixture slots keep it.

**Verification** (`navigationCacheVerify=1`). After a reuse, each deserialized cache is serialized again, and fresh caches are generated. Both are compared in content with the published files: every distance exactly, every neighbour pair, every face's closest entrance, with entrance pairs in canonical order.

## The semantic route set

`RuntimePlayabilityValidator.RouteSet` builds, from the world's settlements at their final positions, each route in both directions under the mover's capability:

| Category | Routes | Mover and type |
|---|---|---|
| village-market | every village to its bound settlement and back | villagers on land (Default) |
| capital-fief | every kingdom's capital to each of its fiefs and back | going: the ruling clan; coming back: the fief's owner clan. Default within one land group; across water All when that clan fields ships, else refused |
| player-start | per start town: its gate to its port and back (All); its port to the nearest other port of its basin and back (Naval); its gate to the nearest other town and back (Default, or All across water); in a campaign also each distinct starting point of the cultures starting there to the gate and back, unless the point is the gate | the player |
| port-to-port | every basin's port towns joined by a spanning tree of nearest ports, each edge both ways | fleets (Naval) |
| faction-access | every kingdom's capital to the nearest fief of another kingdom that both directions can make under their own clans' capabilities, and back; none is a refused route | lords |

Only towns have ports in the route set; harbour villages' drop-off ports are in the caches but are not fleet destinations.

**Capability.** A clan fields ships when its home shares a land group with a port town on an open basin. This is MMR-35's clan rule. In a campaign the capability is read from the game's fleet rule (`GeneratedClanShipOwnershipModel`'s landlocked clans), else the same rule on the campaign's settlements.

**Player starts.** Before activation, the starts are every town that is a port town on an open basin with another port town on it. They hold every start MMR-34 can pick: it picks only among such towns, its fallbacks included, then also requires food, notables and a shipyard, and its sea destination is the nearest other port town of the basin. An empty set is a refused route. In a campaign, the starts are the creation cultures' starting points and their nearest towns.

**Where it runs.**
- **Inspection worlds (production).** Every route goes through `NavigationValidator.EvaluateRoute`. That is MMR-38's evaluation:
  - every periodic placement of the route;
  - an engine path and a path distance;
  - no shortcut off the route's domain;
  - no distance shorter than the straight line.

  The generation job's runtime stage runs it in engine-thread slices of about 25 ms.
- **Campaigns (`CampaignPlayability`).** Before any query, the registered caches are serialized with the installed `Serialize` and parsed (a snapshot). Every route is then a directed `IMapScene.GetPathDistanceBetweenAIFaces` query, and a zero cost between distinct points fails. Each settlement-to-settlement route's entrance pair must be in the snapshot of its navigation type. No `MapDistanceModel` query is made, because a missing pair is computed and stored by it, which would hide a miss and change the cache.

## Pending-runtime flags

`RuntimePlayabilityValidator.ResolveFlags` resolves or fails every flag the compiler emitted as pending runtime:

| Flag | Source | Resolved when |
|---|---|---|
| travel graph `engineValidation: pending` | `compiled/travel/travel-graph.json` | MMR-38's navigation report is activatable |
| each host check (`check`, `subject`, status pending) | travel graph `hostChecks` (634 on A, 905 on B) | every engine face and route MMR-38 ran for it agrees; `distinctPositions`: no static diagnostic names it; `islands`: the engine islands check found no problem |
| each transition `pendingEngineValidation` | travel graph `transitions` (51 on A, 34 on B) | its portFace or seamTransition rows (port), or its coastTransition rows (coast), agree |
| ports `engineValidation: pending` | `compiled/travel/ports.json` | every portFace and portGate host check resolved and the settlement entity checks passed |
| W05 | the campaign graph's `pendingRuntime` (runtime settlement, gate and port positions) | MMR-40's settlement entity checks passed |
| U03 | the campaign graph's `pendingRuntime` (runtime route reachability) | the semantic route set passed |
| any other `pendingRuntime` entry | the campaign graph policy | never: an entry without a resolver fails |

Not flags of this kind, and listed in the report's `notFlags`:
- the campaign records' per-record slots (`compiled/campaign/campaign.json` slots and `validation.json` `pendingSlots`, owned by MMR-31 to MMR-35 and MMR-42), which are content slots;
- the product policy's `pendingRuntimePolicies`, which are runtime policies owned by their tickets.

## The activation report

The report is `<store>/world-playability/<scene>.json` (`mnbl.playability-report`). It holds:
- the route totals per category with each row;
- the failures;
- the flag counts per kind with every flag;
- the steep-pad decision;
- `activatable`.

`activatable` is true only when every route passes, every flag resolves and no engine fault was injected. Validation deletes the scene's previous report first, so a failed run leaves none. The generation job refuses a world without it (`runtime.playability`).

**The gate.** `RuntimePlayabilityValidator.Persist` writes a record (`mnbl.playability-gate-record`) embedding the report text and its SHA-256, as MMR-38's `NavigationGate` does. `Require(store, scene, navigationRecord, settlementReport)` is the check MMR-45's activation makes. It refuses unless:
- the record's schema and the validator version are current;
- the embedded text matches its hash;
- the scene and world match;
- no engine fault was injected;
- the report is activatable;
- the report is bound to the scene's persisted navigation report hash and settlement layout hash.

The generation job and world_navigation call it after persisting.

**Steep pads (MMR-40's handoff): accepted, not gating.** The pads stay where they are:
- campaign movement and settlement interaction run on navigation faces;
- MMR-40's settlement entity checks found every gate, marker and village over a Default-valid land face;
- every route in this set passes.

A steep pad only shapes the icon's footing. The report counts them: 37 pads on A, 46 on B.

## Offline tests

`tools/Test-Playability.ps1` passes 24/24 (`evidence/world/playability-tests.json` and `.txt`; the offline report is `evidence/world/playability-offline.json`). It compiles the key, the cache file reader, the store, the validator and their dependencies against the pinned framework and `TaleWorlds.Core`, and runs on compiled worlds A and B. No game process.

| Area | What is checked |
|---|---|
| Key | the canonical text is independent of insertion order; each of the 19 keyed inputs changes the key and is the one difference named; a port moved by 0.001, a gate or centre moved, a port lost, a settlement added or removed each change it, named by settlement; malformed names, values and duplicate settlements are refused |
| Cache file | the installed All and Default formats read without the engine; content compares equal with pairs stored the other way round; one changed distance or face entrance is one difference; truncated, trailing, wrong-layout and negative-count files are refused |
| Store | a published cache passes for its key and scene; a moved port, a tampered file, other CRCs, a settlement the key lacks, a face beyond the navmesh, a missing file, another schema and no manifest are each refused |
| Route sets | A: 430 village-market, 62 capital-fief, 180 player-start, 58 port-to-port, 80 faction-access routes. B: 610, 102, 150, 48 and 108. No route refused; types follow capability |
| Dry run (managed mesh engine, never evidence of engine behaviour) | A: 810 routes agree and 689 flags resolve (634 host checks, 51 transitions). B: 1018 and 943 (905 and 34) |
| Faults | a failed portGate check fails its flag and the ports flag; failed settlement checks fail W05; failed routes fail U03; an unknown pending-runtime entry fails; engine faults make a report not activatable |
| Gate | `Require` passes an intact bound report and refuses 6 cases: no report, another navigation report, another layout, an edited record, not activatable, engine faults |
| Refusals | a landlocked kingdom's fief across water, a village off its market's land, a landlocked owner who cannot come back, a shipless kingdom with no reachable foreign fief, no player start |

MMR-38's harness (`tools/Test-WorldNavigation.ps1`) still passes 36/36 after `EvaluateRoute` was factored out of it.

## In game

`evidence/world/playability-report.json` (`tools/summarize_playability.py`) passes all 21 checks. Every value is copied or counted from the runs below.

**Production route set and flags** (world_navigation 29008, on the engine's navmesh of each compiled world):

| World | Routes (failed) | Flags (failed) | Activatable | Gate (`Require`) | Seconds |
|---|---|---|---|---|---|
| A | 810 (0) | 689 (0) | yes | activation allowed | 0.61 |
| B | 1018 (0) | 943 (0) | yes | activation allowed | 1.20 |
| A, warm (same report hash as cold) | 810 (0) | 689 (0) | yes | activation allowed | 0.53 |
| A with Bridge faces disabled (engine fault) | 810 (2) | 689 (5) | no | not activatable | 0.59 |

- Every category ran in both directions, and every route passed.
- The report lists the content slots and runtime policies as not flags.
- The generation job (25936) ran the playability step in the runtime stage: 810 routes and 689 flags in 18 engine-thread items, 0 failed, report persisted, and the gate allowed activation.

**Caches in a campaign** (32,768-face fixtures, two worlds per run):

| Run | World 0 caches | Default / Naval / All | World 1 |
|---|---|---|---|
| 2516 (cold) | generated and published, key `4c667d8fd962` (publication 0.11 s) | generated in 5.0 / 8.0 / 55.0 s | generated, key `21e98537d145` |
| 32740 (reuse, `navigationCacheVerify=1`) | reused | deserialized in 8.6 / 5.2 / 7.3 ms; the round trip and fresh caches equal the published files (pairs 66 / 3 / 105, faces 5,027 / 23,904 / 31,921) | reused |
| 21804 (`navigationCacheFault=corrupt`) | refused (`All: the file's hash differs from the manifest's`), regenerated and republished | generated in 5.1 / 8.3 / 56.1 s | reused |

- The harbour village's drop-off port was matched by its parent and applied at (606.875, 169.375). It is in the key (`village_1|village+port`) and in the Naval cache (3 pairs).
- In every run, the key rebuilt at campaign start, after War Sails' wrapper, equals the acquired key in both worlds.
- The campaign route set passed 20/20 in every run.

**Periodic fixture** (fixture-c, 131,072 faces):

| Run | Caches | Default / Naval / All |
|---|---|---|
| 6152 (cold) | generated and published, key `5e5ef63eb8a8`, keyed `periodic=mmr64/1 97e8b9f7…` (publication 0.23 s) | generated in 28.1 / 129.8 / 407.9 s (565.8 s in all) |
| 35828 (reuse, verify) | reused | deserialized in 8.7 / 45.1 / 40.9 ms (0.095 s in all); the round trip and fresh caches equal the published files |

- In both runs the key at campaign start equals the acquired key.
- The campaign route set passed 16/16, routing the selected culture's start only (see Limits).
- Run 9908 routed every creation culture on the same world. It refused the six installed Calradia start points that the fixture cannot replace (12 routes, no engine path), and the generated start passed. That run ends in error by design.

**Cost.** Reusing a published cache costs 21 ms on the fixtures, against 68 s to generate. On the periodic fixture it is 95 ms against 566 s. The cache build is no longer paid on a reload of an unchanged world.

**History.** Earlier runs, kept in the summary's sources:
- 33340, 18640, 24608, 32996, 11304 and 27968: the first implementation, which the review covered.
- 30712: hung on a `FormatException` in `AfterLoad`. Its result was copied from the autorun store, because it was never collected.
- 32892: `MissingFieldException` on the private constant cache parameters.
- 17060 and 17064: no drop-off applied, because `Village.All` was empty.
- 31352: fixture-c cannot take settlement fixtures.

## Limits

- **Cache cost at real scale (MMR-45).** World A's caches were generated in its first campaign: 389 s in run 22472 (Default 48.6 s, Naval 33.8 s, All 306.1 s) and 391 s in run 3352. They were reused later in the same build: the campaign started in 8.4 s in run 43676. The key includes this module's SHA-256, so every module build regenerates them once. File size and read time at scale are MMR-56's.
- **The production route set ran on inspection worlds**, through MMR-38's evaluation on the engine's navmesh of each compiled world. In a campaign of a compiled world it runs at MMR-45's PostBuild (world A, runs 22472, 9404, 43676: activatable).
- **Periodic campaign starts.** Fixture-c's placement has no navigation-test points, so it cannot take MMR-33/34's settlement and startup fixtures. Its six other creation cultures keep their installed Calradia start points. Run 9908 (every culture) refused all six, both ways, as it should. The periodic runs route only the selected culture's start (`playabilityStartCultures=empire`, recorded as `startCultures` and `excludedStarts`). In production MMR-34 re-emits every creation culture's start, and every one is routed.
- **Cache distances are two-way means** by construction, so the direction of a settlement pair is tested by the directed path queries only, not by the cache.
- **Nearest choices are by straight distance** in the canonical frame (the nearest port, town and foreign fief), not by path cost. On a periodic world the straight distance ignores the wrap. The routes are still run through the engine in both directions.
- **The start candidates are a superset** of MMR-34's starts before activation: towns that fail its food, notables or shipyard conditions are also routed. This makes the set stricter, not weaker.
- **Module builds differ across the evidence runs.**
  - world_navigation 29008 and generation_job 25936 ran on `a2b0a9a7`, before the drop-off village lookup changed to `Settlement.Find`. That code runs only in a campaign's `AfterLoad`, which those probes do not run.
  - The fixture campaigns (2516, 32740, 21804) and 9908 ran on `4b8a2460`, before `playabilityStartCultures` was added. Without the argument the start set is unchanged.
  - The periodic runs 6152 and 35828 ran on `608318e7`, the final build.
- **`Require` at activation.** It is called by the generation job and world_navigation after persisting, and by MMR-45's VerifyRuntime before a world activates.
