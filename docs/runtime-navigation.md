# Campaign navigation of a compiled world (MMR-38, 2026-10-15)

A compiled world's campaign navmesh is built from its MMR-28 travel cells. It is checked against the travel graph before anything is installed, and checked again through the engine once it is loaded. A world that fails either check is never activated, and the refusal names the source ids involved.

| | |
|---|---|
| Builder | `src/MNBL.ProceduralWorld/World/RuntimeNavigationBuilder.cs` (pure managed): `NavigationInput`, `RuntimeNavigationBuilder`, `NavMesh` (NMG9 in RNM1; encode, decode, invariants) |
| Validator | `src/MNBL.ProceduralWorld/World/NavigationValidator.cs`: `NavigationModel` (the installed War Sails model), `NavigationPlan` (static), `NavigationValidator.Run` (engine), `NavigationGate` |
| Fixture and faults | `src/MNBL.ProceduralWorld/World/NavigationFixtures.cs` |
| Engine side | `RuntimeWorldBackend`: `Prepare` builds the navmesh, refuses the world or writes `navmesh.bin`; `Construct` does the installed navigation setup; `RuntimeWorld` answers the navmesh queries; `SceneNavigationEngine` binds the validator to the native scene. `GeneratedMapScene.Load` applies the gate |
| Policy | `worldgen/policies/navigation.json` (`mnbl.navigation` v1, embedded in the module). Face groups come from the bundle's `policies/playability.json` (`cells.faceGroups`) |
| Probe | `world_navigation` (`Automation/WorldNavigationProbe.cs`) |
| Tests | `tests/World/NavigationContractTests.cs` through `tools/Test-WorldNavigation.ps1` |
| Evidence | `evidence/world/navigation-routes.json` (`tools/summarize_world_navigation.py`; runs 42984 and 42956), `evidence/world/navigation-contract.json` and `evidence/world/navigation-tests.txt` (offline), `evidence/fwg/navigation-review.md` |

## Construction path

The navmesh follows R07's native path (`docs/runtime-backend-decision.md`): it is the `navmesh.bin` of the world's content-addressed scene folder, read by `Scene.Read`.

- **Where it is built.** `RuntimeWorldBackend.Prepare` builds it on the worker thread from the verified bundle, next to the surface encoding.
- **What it replaces.** It replaces the surface encoder's 33 × 33 diagnostic grid.
- **Scene identity.** Its SHA-256 is part of the scene name (`SceneNames.ForWorld`), so a different navmesh is a different scene.
- **No runtime assembly.** Nothing is stitched or imported at runtime. Prefabs created at runtime imported 0 faces in MMR-17.

## The mesh

**Layout**
- One quad per physical cell of the compiled grid. Cells are 6.25 units, the travel cells.
- The quads cover the rows between the pole lines and all 256 physical columns. The periodic strips repeat the opposite side (R10).
- No face exists beyond the pole lines.
- The mesh is one lattice. The terrain chunk lines and the seam columns are ordinary interior edges shared by the faces on both sides, and there is no tile seam to stitch.
- The fixture routes cross the interior chunk lines (x and y = 800) and the seam lines (x = 400 and 1200). Lines y = 400 and y = 1200 lie in the pole padding.

**Vertices, faces and container**
- Lattice vertices are grounded at max(collision reference, sea datum 0) + 0.2. The collision reference is the native height answer (MMR-37), on the shaped surface since MMR-278: the travel cells stay 6.25 units, two surface samples of 3.125, and the builder accepts a travel spacing that is a whole multiple of the surface spacing.
- Faces carry the campaign-map metadata: 2, 0, group, 0, −1, flag 0.
- The container is RNM1, compression 3. It holds one LZ4 block with a single literal run, the form the surface encoder's `terrain.bin` already proved in game.

**Size**
- World A has 16,422 faces and world B 16,704, both within MMR-21's provisional 32,768-face navigation-cache limit.
- The engine read every face of both (the face table below).

**Validity**
- The mesh satisfies the installed campaign maps' invariants (`docs/formats/navmesh-bin.md` §4).
- Our decoder and the independent Python codec (`navmesh_bin.py inspect`) both find no error and no warning.

## Face semantics

The playability policy names each cell class's TerrainType; a land cell's is its `cells.bin` v2 land type (MMR-254), the shared biome table's face compiled by the travel compiler (`docs/travel-graph.md` "Land face types"). Names resolve through the installed `TaleWorlds.Core.TerrainType`; a numeral is refused.

Each cell carries four attributes besides its TerrainType: domain, water class, terrain cost and passage. They are kept separately from the type, and the report records their combinations (`build.cellSemantics`).

| Cell (MMR-28 class after repairs) | Domain (region) | Water class | Terrain cost | Passage | TerrainType |
|---|---|---|---|---|---|
| land | land (1) | | plain | open | its land type: Plain, Desert, Forest, Steppe, Snow or Swamp |
| mountain | none | | mountain | impassable (invalid for All) | Mountain |
| sea, at least 4 cells from another class | sea (0) | open sea | | open | OpenSea |
| other sea | sea (0) | coastal sea | | open | CoastalSea |
| lake | sea (0) | lake | | open | Lake |
| river | sea (0) | river | | open | River |
| joining crossing cell | sea (0) under a land deck | its own | | under bridge | UnderBridge, with Bridge deck faces above |

**Land types (MMR-254).** `TravelCells.Read` takes `cells.bin` v2 only (`MNBLTC02`; a v1 file is refused): a land cell must carry an installed TerrainType, every other cell none, and the flags only the road bit. The builder gives each land cell's faces (its quad, or the fan beside a deck) the cell's land type. The validator checks:
- every land type the cells carry is a land face (Default-valid, in the land region, Naval-invalid), once per type;
- the cells per type equal the travel graph's `grid.landTypes`;
- a `gateFace` or `settlementFace` point may resolve to any land type of the cells touching it (an edge or corner point touches two or four), a `pass` or `footprint` cell centre to the cell's own.

The fixture world keeps one land face (the policy's `faceGroups.land`). The road bit is not navigation: flipping every one leaves the navmesh byte for byte (`NavigationContractTests`).

**Seam images.** `GeneratedMapScene` answers a periodic path query from the target's image across the seam; the image's face must have the original's group. With biome faces an image point on a cell edge may resolve to the neighbouring cell of another type, so `ImageFace` then takes the image's face of the original's region (land or sea, by the installed navigation model's Default validity).

**Validity per domain.** Every type is checked against the installed navigation model for its domain:
- land and bridge: Default-valid and in the land region;
- water: Naval-valid and in the sea region;
- mountain: invalid for All.

## Land face types and speed (MMR-254)

Findings sections 3 and 8. Party speed follows the face type through one model, `GeneratedPartySpeedModel` (`Campaign/GeneratedPartySpeedModel.cs`, terms in `World/RoadSpeed.cs`):
- **Registration.** `GeneratedGameManager.InitializeGameStarter` adds it with `CampaignGameStarter.AddModel<PartySpeedModel>` after War Sails' own (its AddModels ran in the base call), so the chain is GeneratedPartySpeedModel → NavalDLCPartySpeedCalculationModel → DefaultPartySpeedCalculatingModel (`roads.json speed.chain`). No Harmony.
- **Terms.** On the installed chain's final speed, then the installed minimum again, for the party's current face (its TerrainType, read as every installed consumer reads it) and its cell's road bit; every factor is the module's `roads.json speed`:

  | Term | When | Factor |
  |---|---|---|
  | terrain | on land, face Snow / Swamp (the engine has no term of its own) | `terrain.Snow` −0.15, `terrain.Swamp` −0.2 |
  | road | on a road cell, not at sea (`roadAtSea` false) | `road` +0.2 |
  | ford | on a road deck (a Bridge face on a road cell), with `cancelFordOnDecks` | the installed ford factor back (+0.3: `DefaultPartySpeedCalculatingModel.MovingAtWaterEffect`) |

  Desert (−0.1) and Forest (−0.3) keep the engine's own terms, which now apply because the faces carry those types.
- **Road cells.** `WorldReconstruction.Build` reads the world's road bits (`RoadSpeed.Cells`, from `cells.bin` v2 and `grid`); activation and the loader register them by scene with the battle terrain context (`GeneratedPartySpeedModel.RoadSources`), the map scene activates its own on `Load` and clears them on `Destroy`, and the game manager's teardown clears the sources (`WorldLifetimeScope` tracks them). A fixture slot has none.
- **The hot path allocates nothing**: the terms are written into per-thread scratch arrays (`[ThreadStatic]`), since the engine computes party speeds on TWParallel workers (the parallel movement ticks of `CampaignTickCacheDataStore`).
- **AI pathing ignores roads** (the engine has no per-face cost): the bonus helps the player more than the AI (findings section 8).
- **Checks.** `tools/Test-PartySpeed.ps1` builds the chain as AddModel builds it and checks the terms against the policy (also a tuned one) and a bundle's road cells; the startup audit checks the live chain (`speedModelChain`).

**Navigation caches.** Nothing here enters the navigation-cache key (`NavigationCacheKey.Sources`: every keyed input and its source, all fixed for a world; an unclassified input is refused). The land types are part of the navmesh, which the key hashes; the road bits, the speed terms and later live face overrides (MMR-260/261) are not.

## Rivers, bridges and coasts

**Rivers.** A river or lake cell is passable by land only as a bridge deck. There is no fording, and a road never implies passability.

**Bridges.** Every joining crossing in the travel graph becomes a deck: a run of at most `maxCells` (2) river or lake cells from a bank of land component A to a bank of land component B.

- **Deck shape.** A deck cell connects its entry and exit sides through inset points a quarter of the side in from each corner.
  - Opposite sides give a rectangle.
  - Adjacent sides give a strictly convex quad (an L-shaped crossing; world A has one).
- **Banks.** A bank cell beside a deck is a fan of triangles, so it shares exactly the deck's edge.
- **Under the deck.** The water cell keeps a lattice UnderBridge face connected to the neighbouring water. That face has no coast edge toward the banks.
- **Layers.** The two layers overlap in XY and are told apart by region (`isRegion1`), as on the installed War Sails map.
  - In game, with Bridge faces disabled in the fixture, every land crossing failed and every sea route under the bridges still passed (run 42984).
- **Refusals.** A crossing is refused when:
  - it is not edge-connected;
  - it enters and leaves a cell through the same side;
  - it crosses an already-decked cell another way.

**Coasts.**
- Land and sea faces share every coast edge; embark and disembark need shared edges (MMR-17).
- A port is the travel-graph port cell's sea face. That is CoastalSea, or the River or Lake face where the port cell is a river or lake cell.

**Corner contacts.**
- Where land cells of two groups touch only diagonally, an All route can embark and disembark at one vertex: two region switches and a zero-length sea leg.
- This is accepted as All connectivity, because both groups touch the same water there, as MMR-28's basins model it.
- The validator reports any such route (`engine.cornerHops`). The confirming run found none.
- Whether a moving party executes such a leg is observed only once compiled worlds are activated (MMR-45).

## Face lifetime (R03)

- **No toggling.** Faces are fixed for the scene's lifetime and production never toggles them. The probe's engine faults toggle faces only on throwaway inspection scenes; their reports record the fault and are never persisted.
- **Navigation setup.** `Construct` repeats the installed `SandBox.MapScene.Load` setup before the read:
  - it sets the region map from the installed model;
  - it calls the disable of the faces invalid for All. Every call returned 0, and the disable has no effect on faces read afterwards (below).
  - The campaign map profile (`ReadCampaignMap`) is the installed sequence itself.
- **No face indices.** Native face indices are never reported or persisted. Reports name source ids, TerrainType names and rounded lengths.
- **Cache order.** Settlement navigation caches are built in `AfterLoad`, after the navmesh and the settlement positions are final (MMR-21; MMR-41 owns them). The positions come from this report.

## Required points

**Snapping.** A required point is a settlement, a port, a port entrance or a coast transition. An unplaceable village or castle is not one: the travel graph lists it with no cell or point, excludes it, and the campaign never builds it. A settlement without a cell that is not such an exclusion refuses activation (2026-10-19; 12 of the 19 accepted worlds of that day's seed sweep hold one).
- It stays where it is when its cell is in its intended domain and component (the travel graph's land component or basin).
- Otherwise it moves to the nearest interior point of a matching cell within 0.5 cell (3.125 units).
- Otherwise activation is refused, naming the point's ids.
- A snap may move a point out of a sea, mountain or other-component cell, but it ends only in its own domain and component.

**Shared points.** Settlements that share an emitted point (MMR-28 `distinctPositions`) are separated along x inside their own cell.

**Ports.**
- A port point is placed at the interior point of its port cell nearest its town's gate. It never leaves that cell; the move is at most 3.98 units.
- The travel graph bounds the port and gate *cells* (2 cells), not the points. Two of world B's ports had cell-centre points 14.1 and 12.6 units from their gates.
- **The placed points are a binding output.** The report's `static.points` carries every resolved position, and the port entities (MMR-40) and settlement positions (MMR-41/45) must use them.

**Results.**
- World A: no snap moved a point; 2 settlements separated; 30 ports placed.
- World B: no snap moved a point; 6 separated; 25 ports placed.

## Validation

### Static check

`NavigationPlan` runs in `Prepare`, before encoding. Any diagnostic refuses the world with `NavigationRefusedException`. It checks that:
- the cells reproduce the travel graph:
  - class counts;
  - the MMR-28 OpenSea rule, recomputed;
  - land components and basins, compared as per-label cell counts with the MMR-28 labelling;
- every group is one Default component of the mesh and every basin one Naval component, with images joined across the seam;
- every required point resolves;
- every route of the matrix holds on the mesh.

The static plan also computes each expected route's shortest path on the mesh. That path is a face corridor string-pulled by the funnel algorithm, and it bounds the engine's path.

### Engine check

`NavigationValidator.Run` runs on the loaded scene. It checks:
- **The face table:** count, TerrainType histogram, and every engine face centre inside a built face of its type.
- **Face checks:** region-strict `GetNavMeshFaceIndex`.
- **The route matrix, in both directions.** This restates the MMR-17 route function; the campaign probe is not reused as code.
  - The installed model's invalid types are the excluded faces, standing for the AI party capabilities Default, Naval and All.
  - Region switch costs are 50/50. The radius is 0.3 on land and the embark threshold 0.5 at sea. The extra cost is ×2.
  - Every placement of the route is tried (MMR-64, `PeriodicWorldCoordinates.Placements`: the three displacements by |D|, ties east, the start moved by whole widths when the target's copy lies outside the terrain), and the shortest found path is the route's. `GeneratedMapScene`'s periodic queries try the same placements in the same order but keep the cheapest path cost (`PeriodicDistanceModel.ShortestOverImages`), so the two can pick different copies when path length and cost disagree.
- **Per found path:**
  - It is sampled every 0.25 units, MMR-17's step, and must stay in its domain. A sample whose own lookup fails counts as in its domain when a point 0.05 units away does; MMR-17 had no such tolerance. This keeps a path along a sea/mountain edge, where the lookup may answer with either face, from counting as a shortcut, which crosses whole cells.
  - Its length must lie between the straight distance and 1.25 × the mesh's shortest path + half a cell. The worst ratio was 1.14.
  - **All routes** (land and sea, with region switches) take √2 instead of 1.25 (`routes.maxStaticFactorAll`, validator `mmr38/3`, 2026-10-19). The engine's own route over the square sea faces can leave the shortest corridor even when its faces and switches are right. The `route_trace` probe on seed 43's world showed one: a route across the seam that turned at coast corners, crossed the seam column at a lattice corner, overshot 28 units west and doubled back, 1.29 × the stand-in's straight diagonal. That one route refused an otherwise playable world. √2 is how much a staircase through square faces exceeds the straight line at 45°, the worst case for a path that moves steadily across the grid. A path that wanders more than that still fails. Default and Naval routes keep 1.25, since there a detour means a missing connection. Changing the bound makes every stored navigation report stale, so a campaign saved before it loads as not-rebuildable.
  - Its region switches are counted exactly, as (engine cost − path length) / 50.
  - A bridge route must cross its deck's Bridge face, or its UnderBridge face for a sea route under the deck.
- **Islands:** the MMR-28 members.

### What the matrix holds

For each world:
- every MMR-28 host check (next table);
- forbidden routes: Default between the main group and every other group, and Naval from the main port to every other basin;
- two sanity routes, to open sea and inland; each destination is invalid for its type;
- All routes between the main group and every other group, expected reachable exactly when their land touches a common water basin. This expectation is computed from the cells, independently of the mesh, and needs at least two region switches.

| MMR-28 host check | What runs here |
|---|---|
| `gateFace`, `settlementFace` | Land face at the resolved point is Plain; towns and castles of player-safe groups are island members |
| `villageRoute`, `landRoute` | Default route, both ways |
| `portFace` | The port's sea face, typed from `cells.bin` independently of the builder, and an All route from the land entrance with exactly one region switch and no detour. `SetSailAtPosition` is a campaign action (MMR-40 binds port entities, MMR-45 activates) |
| `portGate` | Default route from land entrance to gate, both ways. The All path from port to gate must have exactly one region switch and length ≤ the travel graph's 12.5 units. `GetPortToGateDistanceForSettlement` reads the campaign's All navigation cache, a cost that includes the region switch cost (MMR-17: 55–56 for ports 5–6 units away). The host check's envelope, read literally on that value, could never hold, so it is checked on the path's length (MMR-41 owns the cache) |
| `bridge` | Bridge face (land) and UnderBridge face (sea) on every deck cell; Default route bank to bank, both ways, across the deck. A Naval route under a deck cell whose water runs straight through it; neither A nor B has one, the fixture covers it |
| `pass`, `footprint` | Plain land face |
| `seaRoute` | Naval route from the group's port to the nearest main-group coast or port in the same basin, both ways |
| `distinctPositions` | Separated (above) |
| `openSea` | OpenSea faces on up to 16 of the basin's OpenSea cells. A basin with no OpenSea cell ("storms cannot spawn"): up to 16 of its water cells carry their own CoastalSea, Lake or River face (validator `mmr38/3`, 2026-10-19; before, no row ran and the flag failed, which the offline sweep showed on 10 of the 19 swept worlds) |
| `islands` | Static: gates, ports and kept castles on one All component. Engine: on one engine island. This is weak: each mesh is one connected piece, and the engine reports one island |
| `padding` | No face, land or sea, beyond either pole line |
| `seamTransition` | The port transition route across the seam column. A coast transition on a seam column (`coast group G`) is routed with every coast transition (below), and its flag resolves from those rows (`docs/playability.md`). The embark action at the seam is MMR-65's |
| (coast transitions) | All route from each coast transition's land point to its sea point, exactly one region switch |

**Results (run 42984, matched by run 42956)**
- World A: 826 routes (91 forbidden or sanity, 367 reverse) and 393 faces agree.
- World B: 1,118 routes (176 forbidden or sanity, 478 reverse) and 586 faces agree.
- No sampled path leaves its domain.
- Port-to-gate paths are at most 8.1 units (A) and 11.25 units (B).
- Warm world A reproduces cold A's report.
- A second process reproduces the reports of the fixture, A and B.

## Activation

`Prepare` refuses a world whose static plan has a diagnostic. Nothing is encoded, installed or registered.

**The report.**
- Production persists the engine report at `<store>/world-navigation/<scene>.json`, together with its hash. The file is a gate record (`mnbl.navigation-gate-record`: `schema`, `version`, `reportSha256`, and `report`, the report's hashed text), not the report; an activation keeps a byte copy as `activations/<instance>/evidence/navigation.json`.
- Code that reads the report's content reads it with `NavigationGate.ReadAt(path, scene)` (MMR-247): the record's schema and hash checked, the report returned as `Run` made it. The activation's BuildWorld and a load's rebuild hand that report to `WorldReconstruction.Build` (the map dressing reads `build.bridges`); handing it the record failed live acceptance run acc2 with "Missing object build".
- The production activation (MMR-45) must run `NavigationValidator.Run` on the constructed world and persist the report, or find a persisted report that still passes the gate. Until then, a compiled world is refused.

**The gate.** `NavigationGate.Require` (and `Check` for a report that is not persisted) allows activation only when:
- the report is intact (the hash recomputed over its exact text);
- it comes from this validator version and the installed navigation policy;
- it was made under this build's periodic coordinate policy version (`coordinatePolicyVersion`, MMR-64; the report also records the policy identity, `coordinatePolicy`);
- it names the scene, the scene's world and the installed `navmesh.bin` hash;
- it carries no injected engine fault;
- it is activatable.

An unreadable record is a refusal. `GeneratedMapScene.Load` checks the gate for every content-addressed scene and traces the diagnostics before the exception escapes. MMR-45's activation transaction checks the same report first.

**Refused (run 42984):**
- world A with a village moved into the open sea, or onto another land component: `navigation.snap_refused`, naming the village's id and uid;
- world A with its joining crossings dropped:
  - `navigation.bridge_missing` per crossing;
  - `navigation.group_not_realized`, naming the crossings and the settlements cut off;
  - the village routes that depended on the bridges;
- world A, and the fixture, with Bridge faces disabled in the loaded scene: the gate refused and named both crossings and the village beyond them. These reports were not persisted.

## Engine facts observed

- **The pre-read disable has no effect.** `SandBox.MapScene.Load` disables the faces invalid for All, then reads. Every disable call returns 0 (no face exists yet), and a path with no excluded faces entered a mountain-ringed pocket. Every campaign path query passes the invalid types explicitly, so routing is unaffected.
- **Distances are costs.** `GetPathDistanceBetweenAIFaces` returns the path length plus 50 per embark or disembark.
- **Islands.** Every world is one mesh island, and the engine reports one island. The fixture's mountain-closed pocket, a separate All component, shares it, so `FaceIslandIndex` is not All connectivity.
- **Region-strict lookups.** A land lookup on a lake returns no face. Mountain faces are in region 0, the sea region, because the region map is Default validity.
- **Container.** A store-mode LZ4 block with zeroed scratch fields loads (`docs/formats/navmesh-bin-loading.md`; the format itself is `docs/formats/navmesh-bin.md`).

## The fixture world

The fixture uses world A's terrain with designed travel cells (logical 64 × 128 cells), built and validated by the production code. Every expectation is designed in `NavigationFixtures.cs`, not derived from the mesh.

**MMR-17's cases, restated on production navigation:**
- a two-cell mountain wall with a detour within [direct + 4, 3 × direct] cells (17.4 here), from the policy's `barrier` constants. This replaces MMR-17's runtime face toggle, which R03 showed renumbers islands;
- a closed mountain ring, refused for Default and All;
- an enclosed 3 × 3 lake: refused from the sea, reached from its shore, walked around;
- an island behind a one-cell sea channel, the narrow passage the MMR-17 ticket named but never built:
  - ships take the channel (≤ 1.2 × straight);
  - land is refused;
  - All crosses with two region switches.

**What MMR-17 did not cover:**
- three bridges (one cell across x, two cells across x, one cell across y), each with a Naval route under it. Both the crossing and the sea route must cross their faces;
- a river crossed only at a bridge (≥ 2 × straight);
- land and sea routes across the chunk lines x = 800 and y = 800 and across the seam (x = 1200 eastbound, x = 400 westbound), at most 1.2 × straight;
- OpenSea and CoastalSea faces;
- no face beyond the poles, including in a strip;
- strip images.

## Reproduce

```
tools/Test-WorldNavigation.ps1 -BundleA build/research-bundle29/bundles/f596b58c… -BundleB build/research-bundle37/bundles/f63c6841…
tools/Start-OwnedProbe.ps1 -Probe world_navigation -ArgsJson '{"bundleA":"…f596b58c…","bundleB":"…f63c6841…"}'; tools/Collect-OwnedProbe.ps1
python tools/summarize_world_navigation.py --run evidence/runtime/in-game/world_navigation-<pid>.json --run2 evidence/runtime/in-game/world_navigation-<pid2>.json --offline evidence/world/navigation-contract.json --out evidence/world/navigation-routes.json
```

## Not done here

- **AI party movement on a compiled world.** It needs the world activated as a campaign map (MMR-45). Here the AI party capabilities are the installed navigation types' excluded faces. MMR-17 moved real parties (land travel, sailing, embarking and disembarking) on generated navmeshes of the same construction.
- **Campaign calls:** `SetSailAtPosition` (MMR-40 binds the port entities; MMR-53 sails from them) and `GetPortToGateDistanceForSettlement` through the navigation cache (MMR-41).
- **Seam actions:** the embark action and party movement at the seam (MMR-65).
- **Runtime obstruction:** there is none. Barriers are static Mountain faces.
