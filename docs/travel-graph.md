# Static travel graph and port candidates (MMR-28, 2026-10-15)

`worldbridge --compile-travel` computes the campaign's static travel candidates on the compiled terrain (MMR-26), under the War Sails contract MMR-17 observed:
- land components and water basins;
- roads, with their passes and bridge candidates, and the road network with feeder roads (MMR-253);
- every land cell's face type from the shared biome table (MMR-254);
- ports and transitions;
- player reachability.

It also resolves the slots MMR-27 left to MMR-28: settlement positions and port sites.

**Nothing here proves engine movement.** Every result carries `pendingEngineValidation`. The compiler emits the host checks that MMR-38 runs once it has built the faces (W02–W05): see `docs/runtime-navigation.md` for what each check runs and `evidence/world/navigation-routes.json` for the engine results.

| | |
|---|---|
| Code | `src/WorldBridge/src/travel_graph.cpp`, `src/port_compiler.cpp`. The reader now keeps `roads.routes[].river_crossings` |
| Policy | `worldgen/policies/playability.json` (`mnbl.playability` v1). It pins the calibration by SHA-256, resolved relative to the policy file |
| Engine contract | `worldgen/calibration/naval-transitions/naval-transitions.json`. From the accepted MMR-17 run 2248:<br>• the invalid-terrain sets for Default, Naval and All;<br>• the TerrainType ids;<br>• region switch costs 50/50 and the 0.5 embark threshold;<br>• the observed embark and disembark rules.<br>Plus the installed War Sails port-gate envelope (`settlements.xml`, 29 port towns, at most 10.97 units) and what MMR-17 left open |
| Outputs | • `travel-graph.json` (`worldgen/contracts/travel-graph.schema.json`);<br>• `ports.json` (`ports.schema.json`);<br>• `cells.bin` v2 (`MNBLTC02`), the cell classes after repairs, the OpenSea flags, the land face types and the road bit (below);<br>• `campaign.json`, the MMR-27 records after the exclusions, with positions and port sites resolved.<br>All are deterministic, and every input hash is recorded |
| Tests | `src/WorldBridge/tests/travel_graph_tests.cpp` (CTest `travel`, 157 checks), plus 3 road-crossing cases in `source_reader_tests` (142 checks). Logs: `evidence/fwg/travel-tests.txt`, `evidence/fwg/travel-ctest.log` (8/8 suites) |
| Evidence | `evidence/fwg/travel-graph.json` (`tools/summarize_travel.py`): every research size, from 33 to 1025, re-derived independently from the MMR-26 terrain artifact and the MMR-27 records, 22/22 checks each. It includes schema validation, the recomputed reasons and bounds, the completeness of the repair records, and the calibration evidence hashes. A repeat compile is byte-identical. `evidence/fwg/travel-gate.json` (`tools/Test-TravelGate.ps1`) records the gate inside `--generate`. Review and disposition: `evidence/fwg/travel-review.md` |

```
worldbridge --compile-travel SPLIT --contract worldgen/contracts/source-compatibility.json \
  --projection-policy worldgen/policies/projection.json --materials worldgen/policies/terrain-materials.json \
  --import worldgen/calibration/terrain-import/terrain-import.json --content worldgen/policies/campaign-content.json \
  --cultures worldgen/policies/culture-presentations.json --playability worldgen/policies/playability.json \
  --population worldgen/policies/population.json --out NEW_DIR
```

The terrain (MMR-26) and the campaign records (MMR-27) are compiled in memory from the same policies. The recorded `terrainMasksSha256`, `terrainHeightfieldSha256` and `idMapSha256` equal those compilers' artifacts, and the verifier checks this. Exit 0 means status `ok`. Exit 3 means `rejected`; the outputs are still written, with the reasons.

## Navigation cells

Cells are the compiled grid's cells: 6.25 world units at 257 vertices.
- **Extent:** one seam period of columns (they wrap) between the pole lines. Padding rows are not part of the graph.
- **Size:** 128 × 64 cells at the pinned settings. `cells.bin` holds each cell's class after repairs, its OpenSea flag, its land face type and its road bit ("Land face types" below). Cell (r, c) sits at physical sample (rowOffset + r, columnOffset + c).

| Class | Rule (in order) | Proposed TerrainType for MMR-38 |
|---|---|---|
| sea | the main-diagonal centre height is below the datum (the MMR-17/18 bootstrap fixture rule; the diagonal is the MMR-16 triangle split) | CoastalSea 18; OpenSea 19 when at least 4 cells (Chebyshev) from any other class (MMR-59: storms need OpenSea) |
| lake | a main-diagonal corner carries compiled lake intent | Lake 8 |
| river | a main-diagonal corner carries river intent | River 11 |
| land | otherwise | its land face type (MMR-254, "Land face types" below): the shared biome table's TerrainType |
| mountain | land whose (max − min corner height) / spacing exceeds 1 (the fixture rule) | Mountain 7 |

**Face-group checks.** When the policy loads, its proposed face groups are checked against the calibration's invalid sets:
- land is Default-valid and Naval-invalid;
- mountain is invalid for All;
- sea, lake and river are Naval-valid and Default-invalid;
- bridge is Default-valid;
- under-bridge is Naval-valid.

**Connectivity** is through shared edges only, wrapping across the seam. Cells that touch only at a corner are not connected (MMR-17: two triangles per cell). Engine islands span land and sea (All), so a static land component is narrower than an engine island. The `islands` host check covers the difference.

## Settlements and repairs

**Placement.** A settlement sits at its source sample, and its candidate cells are the cells touching that point (NW, NE, SW, SE). Its emitted point is the source point clamped into its cell, a quarter cell inside the edges. Settlements that still share a point are listed in `positionCollisions`, each with a `distinctPositions` host check (1 at size 129).

**Repairs.** Every repair is deterministic, bounded and recorded with stable UIDs. Every cell whose class changed has its record; the verifier checks both directions. Exceeding a bound rejects the world, and the reason names the UIDs.

| Repair | When | Bound |
|---|---|---|
| `footprint` | None of a settlement's touching cells is land, but one is mountain: that cell becomes its footprint | `maxFootprintFraction` (0.05) of settlements |
| `pass` | A mountain cell on a producer road's lattice path becomes land; footprint cells are never recorded again as passes | Road and access passes together: `maxPassFraction` (0.1) of the compiled mountain cells |
| `access` | No land cell shares an edge with a footprint: the shortest run of mountain cells to the nearest land becomes a pass | `maxAccessCells` (6) per footprint |
| `snap` | No touching cell is land or mountain: the settlement moves to the nearest land cell | Within `maxSnapCells` (2); `maxSnapFraction` (0.05) of settlements |
| `harbour_shift` | A producer sea node touches no navigable cell: the harbour resolves to the nearest one. `from` is the sea-node cell | Within `maxHarbourShiftCells` (2); `maxHarbourShiftFraction` (0.1) of harbours |
| `clearance` | The village quota moves a kept village clear of the fortifications (4 units) and of other settlements ("Village quota") | Outside the bounds; within the quota's reach |

## Roads, bridges and groups

- **Decoding.** Producer roads (`roads.routes`) are decoded to town UIDs by site index, which equals the site id (generator roads.cpp).
- **Rasterizing.** Each node takes its best touching cell: land, then mountain, river, lake, sea. Consecutive node cells are joined by the cheapest edge-connected path inside their bounding box. Entering a cell costs 1, plus 10 for mountain, 100 for river or lake, and 1000 for sea.
- **Poles.** A pole passage splits a road. Producer crossings on a step that touches a pole are counted in `producerPoleCrossings`.
- **Crossings.** A run of non-land cells between two land cells is a crossing:
  - `coast` when it holds sea;
  - `lake` or `river` otherwise;
  - `blocked` when it holds an uncarved mountain.
- **Bridges.** Crossings of the listed kinds (river, lake) that are at most `maxCells` (2) long join land components into **groups**. These are bridge candidates: each is a `bridge` host check, and there are at most `maxRequired` (32).
- **One deck per cell** (2026-10-19). A crossing on a cell that an earlier joining crossing already bridges between two other land components joins nothing: the runtime lays one passage per deck cell. In world ae2f24ef of that day's sweep, a river cell between three components carried two joins, and the second was never realized (`navigation.group_not_realized`).
- **Coast crossings join nothing.** Sea separation is crossed only through ports.
- **Producer crossings.** Every producer-reported river crossing gets an outcome: `matched` by a compiled crossing, or `no_compiled_water`. The generator records a bank step as a crossing.
- **Bridge faces are unproven.** No shipped campaign map authors Bridge 17 faces, and MMR-17 authored none.

## Road network (2026-10-19; MMR-253)

FWGC++ roads are a minimum spanning forest between cities only (findings section 8): no castles or villages, and on world A 7 of 45 cities have no road. `lay_roads` (`travel_graph.cpp`) builds the drawn and flagged network in `travel::run` after the village quota, on the final settlements. It never changes a cell class and never adds a water crossing (owner decision 6: no new bridges).

**Route pieces** come from the exact lattice the compiler already rasterizes ("Roads, bridges and groups"), in `analyze`:
- a pole passage splits a route (world B's route 58 crosses the south pole);
- every crossing without a deck cuts it: the sea legs of a road whose ends lie in different groups (`sameGroup` false), river and lake crossings that join nothing (both banks one land component, too long, or a deck cell already serving two other components) and blocked runs. The piece ends at the bank and the next one starts at the far bank (RT-17, owner default: roads end at the bank of a crossing with no deck);
- a joining crossing's deck cells stay on the piece and are listed in the road's `decks`;
- road passes are road land (they are land after the repairs).

**Feeder roads** (`roads.feeders`), each in input order, each on the network the earlier ones laid:
1. **Road-less towns** (a town whose cell is not a road cell; with `roadlessTown`): to the nearest road cell or other town of its land group.
2. **Castles**: to their garrison town (`campaign.json` `garrisonTown`).
3. **Villages**: to their bound town or castle (the MMR-252 binding).

A feeder is the cheapest edge path from the settlement's cell to its bound settlement's cell or any road cell of the bound settlement's road component, at most `maxCells` (48) cells. Entering a road cell costs 1 and any other cell 2, so feeders merge into existing roads; ties go to the lower predecessor cell. It runs over land cells after the repairs only (the carved passes included, `passes`; mountain cells stay impassable, so the travel classes are unchanged) and over existing deck cells only along the route that laid the deck (`water` `decksOnly`: it enters and leaves a deck only by that route's own steps, never turning on it). A castle or village that cannot reach its bound settlement's road within `maxCells` takes the nearest road cell or town of its land group instead, and is listed under `roadNetwork.boundNotReached`; one with no road or town within reach is `unconnected`.

**Gate spurs** (2026-10-20; MMR-247-R, acc4 c6). A castle or village whose cell already lies on its bound settlement's road gets no feeder above, nor does a town on a road cell. When no piece ends at its gate, the drawn road only passes its cell, and its decals are clipped at the gates around it: on A' a village sat 4.73 units from the nearest decal (acc4 `w1.dressing`), and 104 of the 258 settlements on A' (160 of 381 on B') were the end of no piece. The same holds when every piece that ends at a gate leaves its cell over a deck, since decals stop at the bank. After the feeders, each kept settlement on a road cell that no piece leaves over land from its gate gets a gate spur, in the same order (towns, castles, villages):
- a feeder from its gate (its placement point) through its cell to the land road cell beside it whose centre is nearest the gate (ties: the lower cell index). It ends at the gate of its bound settlement or of a town on that cell, else at a `junction` with the road that laid the cell;
- it adds no road cell. The second cell gives it a drawable length: the gate's own cell centre can be 2.2 units from the gate, less than `gateClip` plus one decal;
- with no land road cell beside it, the ordinary feeder search to the nearest other road or town runs, and a path whose first step is onto water is dropped. Such a settlement keeps only the road over its cell, a bridge in practice (11 on B', none on A').

Spurs are ordinary feeders in `travel-graph.json` (kind `feeder`, `settlement` the one served). They count in `roadNetwork.feeders` and `ends`. They change no class or connection; on A' and B' every spur ran into the cell beside its gate, so the road cells are unchanged.

**Road cells.** `Result::road` holds 1 per road cell (every cell of every piece, decks included): the `cells.bin` v2 road bit (bit 0 of the flags plane, MMR-254). `grid.roadCells` counts them.

**travel-graph.json** (`travel-graph.schema.json`):
- `roads[]` gain `kind` (`route` or `feeder`), `pieces` and `decks`; a feeder also has `settlement` (the castle, village or road-less town it serves) and `to`, the settlement it leads to (its bound settlement, else the town it reaches or the one its junction road leads to). Route roads keep every earlier member, and `cells` still counts the lattice path before any cut. Feeders are numbered after the routes. `RuntimeNavigationBuilder` reads routes only: feeders carry no crossings and no route host checks.
- `pieces[]`: `path`, the edge-connected road cells in order (columns wrap), and `start` and `end`, each with `kind`, `cell` and `point`:
  - `gate`: a settlement's end; `settlement` names it and `point` is its placement point;
  - `pole`: a pole passage cuts the road; the end cell's centre;
  - `bank`: the road stops at a crossing without a deck; `point` is the middle of the bank edge and `water` the first water cell beyond it;
  - `junction`: a feeder meets an earlier road; `road` is that road's index; the end cell's centre.
- `roadNetwork`: `cells`, `routeCells`, `feederOnlyCells`, `deckCells`, `waterCellsWithoutDeck` (0 by construction), `feeders`, `maxFeederCells`, `ends` by kind, `connected` per settlement kind (`connected`, `total`), `unconnected` and `boundNotReached`. A kept settlement is connected when its cell is a road cell whose road component (edge-connected road cells) holds a town, or when it sits on a town's cell in a land group without road cells whose kept settlements all share that cell (an islet: no network to join).

For MMR-257's decals: draw each piece along its `path`, clip at `gate` ends, stop at `bank` ends (`water` is the crossing beyond), and leave `decks` cells to the bridge prefabs.

**Policy.** `splitAtPoles`, `skipSeaLegs`, `passesAreRoad`, `endAtUndeckedBank` and `cellFlag` record the compiler's fixed rules; `load_policy` refuses any of them false. `feeders.maxCells` (48) is a starting value and was not tuned.

**Seeds 42 and 73** (size 129, phase 16; `build/fixtures` from `refresh-fixtures.ps1`):

| | World A' (42) | World B' (73) |
|---|---|---|
| Routes / route pieces / decks | 34 / 43 / 2 | 65 / 85 / 18 deck cells (17 distinct) |
| Feeders (gate spurs) / longest | 212 (104) / 13 cells | 313 (153) / 27 cells |
| Road cells (route / feeder only) | 554 (358 / 196) | 1,040 (715 / 325) |
| Ends: gate / bank / pole / junction | 333 / 18 / 0 / 159 | 512 / 52 / 2 / 230 |
| Settlements on a road cell with a piece leaving their gate over land | 258 / 258 (154 before the spurs) | 370 / 381 (217 before) |
| Connected towns / castles / villages | 45/45, 26/26, 187/187 | 66/66, 39/39, 276/276 |
| Road cells on water without a deck | 0 | 0 |

One village of B' is connected to another town's road but not to its own town's: the town lies across a lake and a ridge, 94 land cells round (`boundNotReached`).

**Seeds 42–61 and 73** (`evidence/fwg/road-network.json`, `tools/summarize_roads.py` over `tools/Run-SeedSweep.ps1 -Stage generate`): all 21 worlds connect every town (1,124), castle (669) and village (4,710) to the network of their land group, with 0 road cells on water without a deck. 15,449 road cells, 5,352 feeders (2,772 of them gate spurs; the longest 47 cells, within `maxCells` 48), 113 deck cells, 619 bank ends, 10 pole ends. Of the 6,467 kept settlements on a road cell, 6,416 are the gate end of a piece that leaves their cell over land; the other 51 (21 towns, 28 villages, 2 castles) have no land road cell beside them, only decks or water (refreshed 2026-10-20 for MMR-247-R; the road cells and connections are unchanged). 14 castles and villages joined another town's road because their bound settlement lies beyond `maxCells` by land (`boundNotReached`). Islets whose town and villages all share one cell (seeds 47, 48, 50, 51, 54, 56 and 59) have no network to join and count as connected. A route can pass one river cell twice, once over its deck and once at a crossing that joins nothing; the second pass ends at the bank of the deck cell (seeds 53 and 58).

**Verification.** `travel_graph_tests` covers a route stopping at both banks of an undecked river and a village's feeder going round it by land; a deck kept on its route and feeders crossing water only there, a castle to its garrison town and its village to the castle; a feeder across the seam the short way and a road-less town to the nearest town; a pole passage cutting a route into pieces with pole ends and a village beyond `maxCells` recorded unconnected; gate spurs for a village, a castle and a town on a route cell and a village on its town's cell (the nearest land road cell, an off-centre gate, the lower-cell tie, no road cell added) and for a town whose only route leaves over a deck; every kept settlement on a road cell being the gate end of a piece that leaves its cell over land, on every test world; and determinism (laying the roads twice gives the same cells and document). `tools/summarize_roads.py` recounts every world from `travel-graph.json` and `cells.bin`: the road cells, the water cells without a deck, edge-connected pieces, bank ends beside undecked water, feeder steps onto decks only along their route, and who is connected.

**Defaults taken.** Sea legs of `sameGroup` false roads are cut, not the whole road: the land legs stay drawn to the coast. "Passes allowed" means the passes the repairs already carved; feeders carve none. Castles feed to their garrison town. A feeder that cannot reach its bound settlement within `maxCells` joins the nearest road or town. Connected means the settlement's road holds a town; road components of one land group are not joined to each other (FWGC++ routes are a forest; world A has 11 route components).

## Land face types (2026-10-20; MMR-254)

Findings section 3. Every land cell gets the face TerrainType of its biome, from the shared biome table (`docs/biome-presentation.md`), so party speed, battle scenes, perks and incidents follow the biome. The classes are not changed: the types are computed on the final classes (after the town repairs), which the village quota and the road network leave as they are.

**The rule** (`travel::land_types`):
- A cell's four corner samples vote: a corner's compiled biome (`masks.bin` biome plane, carried into the grid) gives its `cells.landFaceGroups` face, and a corner in the permanent-white mask gives the permanent-white face (Snow).
- Water biomes (ocean, lake: the ones the face table does not name) do not vote.
- The majority wins. Ties go to the main diagonal: the NW corner, then SE, then NE, then SW.
- A land cell whose corners are all water biomes (a carved town cell, a pass at a coast) takes `cells.landFaceGroups.default` (Plain).
- Every other class has no land type.

**Permanent white** is the material policy's `permanentWhite`: a terrain sample whose biome it lists (`land_ice`, the tombstoned `snow`), or a land or river sample whose nearest source sample has `snowMonths` (12) months of `seasonal_environment.months[*].snow` at or above `snowThreshold` (0.5). The terrain compiler computes it (`terrain::Compiled::white`, not written to `masks.bin`); MMR-255's permanent_snow layer reads the same mask. On A' and B' only land ice has a year of snow, so cold tundra is Steppe everywhere.

**cells.bin v2** (`MNBLTC02`, capability R11): `u32 cols`, `u32 rows`, then four `cols × rows` byte planes, row-major from the north pole line:
1. the class after repairs;
2. the OpenSea flag;
3. the land type: the installed TerrainType id of a land cell (the calibration's `faceGroups`), 0 on every other class;
4. the flags: bit 0 the road cell (deck cells included); the other bits are 0.

Version 1 is refused by every reader (clean break: `TravelCells.Read`, `GeneratedSpawnModel.CellMap`, `RoadSpeed.Cells`, the first-test probe). `grid.cellsSha256` hashes the v2 file.

**Counts.** `grid.landTypes` holds the land cells per TerrainType name; they sum to `classes.land`, and the runtime validator checks them cell for cell.

**Host checks** name the types: `gateFace` and `settlementFace` expect any land type of the cells touching the placement point (15–16% of land-land edges change biome, so one expected name would fail at random), `pass` and `footprint` the cell's own type.

| World | Land cells | Desert | Forest | Plain | Snow | Steppe | Road cells |
|---|---:|---:|---:|---:|---:|---:|---:|
| A' (seed 42) | 1,898 | 498 | 301 | 45 | 182 | 872 | 554 |
| B' (seed 73) | 3,736 | 1,131 | 363 | 120 | 671 | 1,451 | 1,040 |

No Swamp cell: marsh is 2 of world A's and 12 of world B's source samples and never wins a corner majority. `tools/summarize_land_faces.py` recounts every land type from the bundle's own `masks.bin` and the table (0 mismatches on A' and B'), checks the road bit against the road pieces and records where the terrain incidents sample (`evidence/world/land-faces.json`).

**Verification.** `travel_graph_tests` covers the table's faces, the majority, water corners not voting, the main-diagonal and NE tie-breaks, the default, cold tundra as Steppe and as Snow under permanent white, land ice, the seam column, a non-land cell, an unknown biome and a grid without biome samples; per-type counts and `grid.landTypes`; the host checks naming the touching cells' types; and the four `cells.bin` v2 planes with the road bit. `terrain_resampling_tests` covers the permanent-white mask.

## Basins, ports and transitions

A basin is a set of water cells joined by edges. It is **open** when it holds at least one sea cell and at least `minOpenCells` (16) cells. Lake-only basins and small sea pockets are closed.

**Ports come only from producer harbour records** of kept harbour villages (MMR-27 ports), never from a shore, and **they serve the gate**. A candidate is invalid, with the reason given, as soon as a step fails:

1. **Harbour cell.** The first navigable cell touching the sea node, else a recorded harbour shift. When neither exists: `sea_node_not_navigable`.
2. **Basin.** The harbour cell's basin must be open; otherwise `closed_basin`.
3. **Port pair.** A land cell of the gate's own land component, sharing an edge with a cell of that basin. The pair is chosen in this order:
   - its sea cell is nearest the gate;
   - then its land cell is nearest the gate;
   - then it is nearest the harbour cell;
   - then cell order.

   Pairs another town already holds are skipped (towns in id order). So are pairs whose sea cell is a deck cell, a cell of a joining crossing: the runtime lays a bridge deck over it, and a port's sea point cannot snap there (2026-10-19, seed 43's world, refused at activation with `navigation.snap_refused`). When no pair exists: `basin_not_reachable_from_gate`. When only held pairs exist: `port_pair_taken`. When only held and deck pairs exist: `port_pair_under_bridge`.
4. **Gate distance.** The port cell must lie within `maxPortGateCells` (2) of the gate cell, or the candidate is `port_too_far_from_gate`. Two cells is 12.5 units, the smallest whole-cell bound covering the installed War Sails maximum of 10.97 units. MMR-17's fixture ports sat 5–6.25 units from the gate.
5. **Engine path** (2026-10-19). Of the pairs within `maxPortGateCells`, in the order above, the candidate takes the first whose path bound is at most `maxPortGateCells` × spacing (12.5 units). When none fits: `port_path_too_long`, with the first pair's bound. The bound is the shortest plane path from the port cell's point nearest the gate point to the gate point that enters no mountain cell, no row off the grid and no water of another basin (deck cells excepted). The engine's port-to-gate path avoids the same cells and starts inside the port cell, so it is never shorter; the runtime validator checks it against the same envelope (`portGate`). Seed 43's world had a port two cells from its gate with a mountain cell between them: an engine path of 15.02 units, refused at activation.

**Choosing the port.** A town's port is its best valid candidate: nearest the gate, then highest harbour quality, then village UID. A town whose harbours all fail has no port. Its record is dropped from the travel output's `campaign.json`, and `ports.json` keeps the reasons.

**Towns without harbour records.** Towns on an open coast without one are listed in `coastalTownsWithoutHarbourRecord`.

**Transitions:**
- `port`: embark and disembark at a town port;
- `coast`: one representative disembark pair per group and open basin, with its pair count.

The `engine` block carries the region switch costs and embark threshold the transitions assume.

## Reachability, exclusions and rejection

This is the MMR-17 contract:
- the player embarks only at a town port (`SetSailAtPosition`, the leave-by-sea menu);
- the player and the AI disembark on any coast;
- the AI also embarks anywhere on a coast (not used for the player check).

A group is **player-safe** when the player can reach it from the main group and sail back. The main group is the group with the most towns, ties going to the smallest town UID. Reaching a group means taking a port of a reached group to a basin, then landing on a coast of that basin. Returning means taking a port of the group to a basin whose coast belongs to a group that can itself return.

| Outcome | Rule | Code |
|---|---|---|
| Repaired | A town is unplaceable, or its group is not player-safe | a town repair (below); rejected only when none applies |
| Rejected | A town is still unplaceable after the town repairs | `quality.unplaceable_town` |
| Rejected | A town's group is still not player-safe after the town repairs | `quality.unreachable_town` (names the UID, the group and why) |
| Excluded | A village outside its town's land group, or unplaceable | reason recorded; at most `maxExcludedVillageFraction` (0.1) |
| Excluded | A castle that is not player-safe, or unplaceable | reason recorded; at most `maxExcludedCastleFraction` (0.25) |
| Rejected | Too many exclusions | `quality.too_many_exclusions` (names the UIDs) |
| Rejected | A repair or bridge bound exceeded | `quality.repair_bound_exceeded` (names the UIDs) |

## Town repairs (2026-10-19; MMR-251)

No generated town is dropped (owner decision 9, `docs/research/map-epic-2-findings.md` sections 11 and 14). Before the repairs a single unplaceable or unreachable town rejected the whole world: 30 of 31 seeds (43–73) at the one-shot profile, mostly islet towns without a port and towns whose source point lies in the sea at size 129. Now the compiler repairs such towns and runs the analysis again. The repairs keep the town where FWGC++ put it whenever they can, and they come in this order:

1. **Town snap**, inside each round. A town with no touching land or mountain cell snaps to the nearest land cell within `maxTownSnapCells` (6), where other settlements stop at `maxSnapCells` (2). Beyond `maxSnapCells` it is a `town_snap` repair; within it, it keeps the kind `snap`. Either way it does not count toward `maxSnapFraction`.
2. **Carve**, inside each round (`townCarve`). A town with no land cell within `maxTownSnapCells` gets the river or lake cell under it (the touching cell of the best rank) carved to land: a `carve` repair, settlement method `carve`, with a `footprint` host check. A sea cell is never carved, because its ground lies below the datum (default taken: such a town moves, step 5).
3. **Join**, between rounds. A group that is not player-safe joins a player-safe group through the shortest run of mountain cells, at most `maxAccessCells` (6). The search is breadth-first from the group's land cells. Each carved cell is a `join` repair with a `pass` host check. Decks are not synthesized for a join: a group across a short river or lake is already joined by its producer road's deck (`bridges`), and owner decision 6 rules out new bridges (default taken).
4. **Forced port**, in the round after a join failed (`forcedPorts`). A town of such a group that has no valid harbour port takes a coast pair of its own land component as its port when:
   - its group holds a kept village for each of its towns and castles, so the binding can give every fortification a village;
   - the pair's basin is open and a group that returns to the main group touches it;
   - the pair is not held by another port, its sea cell is not a deck cell and lies within `maxPortGateCells` of the gate, and its path bound fits the envelope (steps 3–5 above).

   The pair chosen is the one whose sea cell is nearest the gate, then whose land cell is, then by cell order. Towns are taken in input order, and one forced port serves a group in a round, so a group gets as few as possible (RT-43). The repair is a `coast_port`. `ports.json` marks the port `"forced": true` (its chosen candidate `"kind": "coast"`), and `campaign.json` gets a port record marked `"forced": true`, so the town is a port town. The village quota synthesizes and pins its harbour village (RT-12; "Village quota" below).
5. **Move**, between rounds, the last resort. A town whose forced port did not make it player-safe or could not be laid, a town of a group that cannot join while `forcedPorts` is off, and a town with no cell move to the nearest land cell of a player-safe group, measured from the source point. The cell must have been land before any repair and must hold no settlement. The settlement method is `move`. The town's own villages outside the new group are then excluded as usual, within `maxExcludedVillageFraction`.

Each round starts again from the compiled classes with the joins, forced-port permissions and moves found so far. There are at most six rounds. A town that no repair can make player-safe still rejects the world, for example when no free land cell is left. Every repair names the town's UID as its subject in `travel-graph.json` `repairs`.

**Bounds.** The town repairs sit outside the repair bounds, and so does placing a town: a town's snap, its footprint and its footprint's access passes do not count toward `maxSnapFraction`, `maxFootprintFraction` or `maxPassFraction` (MMR-251). The other bounds still apply: road passes, villages' and castles' footprints, access passes and snaps, bridges, harbour shifts and exclusions. A world whose towns are all placed directly and player-safe in the first round is analysed once.

**City preservation.** `travel::run`, which the generation driver's travel gate, `--compile-travel` and `--bundle` all run, asserts that every FWGC++ city is one town at every stage: the source sites, `settlements.founding.placed_cities`, the compiled towns, the id-map's town entries and the travel graph's towns are the same count with the same uid set, and every town of an accepted world has a cell. A failure refuses `city_not_preserved`, a compiler refusal that fails the generation at once. The bundle validator (`bundle_validator.cpp`) checks the same on the bundle's files, streaming `source/world.json`. Every generation attempt records its world's city count (`cities`) in the attempt state (`worldgen/contracts/generation-attempts.schema.json`), accepted or not, so a world that a derived seed replaced still shows what it held.

**City preservation sweep (MMR-251)** (`evidence/fwg/city-preservation.json`, `tools/Run-SeedSweep.ps1`, `tools/summarize_city_preservation.py`). Seeds 42–61 at the one-shot profile (size 129, phase 16) with the module's policy list and the request's three quality attempts, then the offline BuildWorld pass on each accepted world (bundle, settlement harness, navigation sweep with placement and flags, activation harnesses):
- all 20 requests are accepted, 17 on attempt 0 (the requested seed). No attempt of the 23 is rejected for `unplaceable_town` or `unreachable_town`. The three rejected attempts fail non-town bounds: road passes above `maxPassFraction` (seeds 44 and 55) and harbour shifts above `maxHarbourShiftFraction` (61); their derived seeds generate;
- every accepted world keeps every FWGC++ city: sites = `placed_cities` = compiled = id-map = travel towns, one uid set (1,058 towns over the 20 worlds);
- 135 towns are repaired: 69 snaps and 13 town snaps, 30 footprints with 13 access-pass cells, joins (20 carved cells), 17 forced ports and 10 moves; no town needed a carve. 8 of the 10 moved towns had none of their own villages on their island (the move excluded none), so the forced port's kept-village condition failed there; once MMR-252 synthesizes harbour villages, that condition is what it can relax (not broken down further);
- 18 of 20 reach the campaign offline. Seed 59 builds a town with no bound village (the settlement harness's 1–5 villages per town line) and seed 61's SettlementFactory refuses a castle village with no town of its kingdom within the engine trade limit. Both are village-binding shapes that MMR-252's 3/2 quota replaces. All 20 pass the runtime navigation, its flags (forced ports' portFace and portGate included) and the settlement placement.

The settlement harness's port check measured the port-to-gate distance without the seam wrap and failed seeds 43 and 44 on a port across the seam column (792 units for one cell); it now uses the periodic distance (`tests/Campaign/SettlementTests.cs`).

**Yield** (`evidence/world/seed-yield.json`, `tools/summarize_seed_yield.py`). This is an offline sweep (`build/seed-sweep/after5`, 2026-10-19) at the one-shot profile (size 129, phase 16), with one quality attempt per seed:
- seeds 42–73 went from 1 of 32 accepted to 19 of 32, world A (42) and world B (73) among them, both with their content ids unchanged;
- each of the 19 passes the activation's managed stages offline:
  - it builds its settlements through SettlementFactory and the village binding (the harness of `tools/Test-Settlements.ps1`);
  - it passes the runtime navigation build, plan and validator over the stand-in engine, and every host-check and transition flag the playability validator resolves from that report (`tools/Test-NavigationSweep.ps1`). The flags were added on 2026-10-19: seed 43's world had passed the sweep and was refused in game on an unresolved seam flag. The first flagged sweep failed 10 of the 19 on basins with no OpenSea cell, which the validator now checks (`docs/runtime-navigation.md`); the re-sweep passes all 19 (`build/seed-sweep/after5/navigation`; the earlier run is kept beside it);
  - it passes the runtime settlement placement (`SettlementPositionResolver`, as activation runs it after the plan; in the sweep since 2026-10-19). Before then, seeds 56 and 67 were refused in game at `settlement.port_far`: a port 13.813 and 13.321 units from the town's placed gate, against 12.5. The sweep then found 10 such towns in 8 of the 19 worlds. The bridge bounds a port from the town's point, and the runtime gate stands 2.597 further out, facing its road. Port towns' gates now turn to keep their ports within the limit (`docs/settlement-entities.md`, Gate). All 19 pass, and seeds 56 and 67 generate and start in game;
  - its naval content, spawn policy, graph validation, factions, characters, registry and water stages raise no refusal (their harnesses);
- 12 of the 13 rejected seeds fail on a repair bound (snaps, passes, footprints or harbour shifts), and seed 71 on too many excluded villages;
- the built worldbridge reproduces every one of the 19 bundles byte for byte (`build/seed-sweep/after6`, `after6-73`).

The harnesses also carry world A's fixture counts (ruler and slot counts) and their negative self-tests are written against A; on other worlds those lines fail without a refusal and are listed as `fixtureLines`. One describes the world rather than the fixture, and is follow-up for broad seed certification: B and seed 57 present 6 of the 7 character-creation cultures (no Khuzait realm); a player who picks the missing culture starts through the spawn policy's culture fallback. A third line, lake copies cut by the terrain's edge on 8 worlds (B among them), was a presentation defect, fixed the same day (`docs/water-climate.md`, Edge copies).

A request gets three quality attempts, so a seed whose first attempt is rejected usually still generates.

**Village repairs found by the sweep** (worlds the gate used to reject, which reached activation for the first time):
- a kept harbour village whose producer sea node resolves to no navigable cell (`sea_node_not_navigable`) loses its harbour role in `campaign.json`, since activation refuses a harbour village without a harbour cell (seed 60);
- so does a kept harbour village whose harbour cell lies on a closed basin (`closed_basin`), since activation lands only on open basins and refuses its port otherwise (`quality.naval_port`; 11 of the 17 other accepted worlds of the 2026-10-19 sweep);
- a village farther than the engine trade distance (3 × the world's town spacing) from every town and castle of its land group is excluded, since no binding could keep it (seed 62).

**Verification.** `travel_graph_tests` (115 checks) covers each repair: town snap (outside the snap bound), carve (and no carve of a sea cell), join before a forced port, a forced port only when the join fails, one forced port per group, the `"forced": true` marking in `ports.json`, move, a move with no free cell (rejected), town footprints and access passes outside the bounds, no repair on player-safe worlds, the city preservation refusals, and the trade-distance exclusion. It also covers the port rules: a river-mouth harbour skipping a deck cell, `port_pair_under_bridge`, and `port_path_too_long` around a mountain cell, with the bound's value checked; and one deck per cell. `tools/summarize_travel.py` re-derives worlds without town repairs, including the deck, one-deck-per-cell and path-bound rules; it does not re-derive the town repairs.

**Applying the results to the campaign records.** The exclusions are applied to the MMR-27 records with `campaign::exclude`, which re-checks closure, then the village quota binds, drops and synthesizes villages ("Village quota"), then the population pass plans kingdoms, houses, companies, outlaw clans and wanderers on the final binding ([population.md](population.md); `--population`, the `population` policy of a request), and the travel output writes the result as `campaign.json` version 2. Its `position` slots (towns, villages, castles) and `portSite` slots are resolved with owner MMR-28. `landComponents` lists every placed settlement, excluded ones included; `exclusions` names the dropped ones.

**In generation (FWG-03).** A request can bind `playability` together with `projection`, `terrain-materials`, `terrain-import`, `campaign-content` and `culture-presentations`. When it does, `worldbridge --generate` runs this compiler on every attempt's world and writes its outputs into the attempt's `travel/` directory.
- **Rejection:** a quality failure whose reasons carry their UIDs (`[uids: …]`, the first 20). `worldgen/policies/world-quality.json` declares the four codes retryable, so the next attempt uses a derived seed.
- **Compiler refusal:** `compiler.<code>` fails the generation at once.
- **Other requests:** requests that do not bind these policies behave as before.

## Village quota (2026-10-19; MMR-252)

Every town has exactly `villages.townVillages` (3) villages and every castle `castleVillages` (2) (owner rule; `docs/research/map-epic-2-findings.md` sections 9 and 17). The source binds every hamlet to its core city's town (world A: towns 1–5 villages, castles none), and the installed game has only roughly 3 and 2. `village_quota.cpp` runs inside `travel::run` on an accepted pass-1 result, after the exclusions and the harbour-role fixes and before the position slots; `campaign::compile` stays faithful to the source. Distances are periodic; the **town spacing S** is the mean distance from each town to its nearest town (42.5 on world A, 43.6 on B), and the **reach** is the widest tier's `maxSpacing` × S.

1. **Pin ports.** Each valid port's chosen harbour village stays with its town (`pinned`). A forced port (step 4 of the town repairs) gets a synthesized harbour village instead (step 6), pinned too, so every port survives (RT-12).
2. **Towns keep their own villages** in the `preference` order: the pinned one; at most `maxExtraHarbours` (1) more harbour villages; land villages of a production class the town does not hold yet, nearest first; then the nearest. No town or castle holds more harbour villages than its pinned one plus `maxExtraHarbours` (default taken: the cap holds in every step, so a harbour-heavy town drops harbour villages and gets land villages instead, RT-08).
3. **Castles** take, in turn and in id order, the nearest remaining village of their garrison town within the reach.
4. **Surplus** goes to short towns and castles of its land group within the reach, its own kingdom first, then the nearest.
5. **Drop.** The rest is excluded with a `quota:` reason. These exclusions come after the analysis, so they do not count toward `maxExcludedVillageFraction`. A presentation culture's last village of a `protectedClasses` class (`farming`: the horse ranches and wheat farms, RT-09) is never dropped: it takes its town's place of the farthest village that is neither pinned nor itself protected.
6. **Clearance, then synthesis.**
   - **Clearance (RT-13).** A kept village closer than `clearance` (4 units) to any town or castle, or within `minSeparation` (1 unit) of another settlement, moves to the nearest clear point of its own land component within the reach (a `clearance` repair: subject the village UID, `from` its old cell, distance in cells).
   - **Synthesis.** A short fortification gets villages on land cells of its own land component, tier by tier (`tiers`: preferred 0.12–0.35 S, wide 0.08–0.5 S, tight 0–0.65 S, distances from the fortification), every one clear of every fortification. In a tier the point taken is the one whose distance to every other settlement is largest, up to twice the clearance, then the one nearest the fortification. Candidate points are the 3 × 3 points a quarter cell apart inside each cell, which the emitted-point clamp keeps as they are.
   - A synthesized village has uid `<uidPrefix><fortification uid>:<n>` (`mnbl-quota-village:`), an id from the world's id registry (an id-map entry), the fortification's culture, a coined name (MMR-286: two lexicon roots of the fortification's civilization, the second of the role's `concepts`, unique among every settlement name, never a numeral), settlement method `synthesized`, its `tier`, and the land production class with the largest deficit of its presentation culture against that culture's source land villages (`synthesizedClass` largestDeficit; ties go to a protected class, RT-09). Its `sourceTown` is the fortification, or a castle's garrison town (else the nearest town of its kingdom).
   - **A forced port's harbour village** is synthesized first, within 2 cells of the port cell; its role is the content's harbour role (`harbor`), its harbour cell the port cell, and `ports.json` names it on the forced port's candidate (`village`, `villageUid`, `harbourCell`; still no sea node).
   - **Islet exceptions** (`isletExceptions`). Where no clear point exists within the reach, the clearest point is used (separated from other settlements, then farthest from every fortification) and recorded in `villageQuota.exceptions` (village, nearest fortification, distance, reason); a synthesized one gets the tier `islet`. A castle with no land point at all is excluded with its villages (the last resort); a town always has its own cell.
7. **Notables** of dropped villages, reeves included, move to the least-settled final village of the dropped village's town, recorded in `villageQuota.movedNotables` (RT-11).
8. **Second analysis.** The analysis runs again on the frozen pass-1 classes with the final settlements at their final points and the kept harbour villages. Every settlement must keep its cell and point, every port its pair and its forced flag, and nothing may be excluded or rejected; otherwise the compile is refused with `quota_unstable`, an internal error. The quota never rejects a world. Every town and castle ends with exactly its count, or the compile is refused the same way.

**Records.** `campaign.json` version 2 writes each village's `bound`, `boundKind`, `sourceTown`, `origin` (source, reassigned or synthesized), `tier` and `pinned`, each castle's `villages`, each port record's harbour villages bound to its town (the pinned one first) and the `villageQuota` block (counts, `protectedKept`, `clearanceMoves`, `spacing`, `synthesizedByTier`, `exceptions`, `movedNotables`, `passes` 2, `stable`). `travel-graph.json` lists the synthesized villages as settlements and in their land component, the dropped ones under `exclusions`, and the `clearance` repairs; the settlement host checks are rebuilt (`villageRoute` now names the bound town or castle), and `positionCollisions` covers the kept settlements only. `VillageBinding` passes the binding through ([settlements.md](settlements.md), Binding).

**Seeds 42 and 73** (size 129, phase 16, both accepted on attempt 0):

| | World A (42) | World B (73) |
|---|---|---|
| Towns / castles / villages | 45 / 26 / 187 (= 45·3 + 26·2) | 66 / 39 / 276 (= 66·3 + 39·2) |
| Kept at their town / reassigned / dropped / synthesized | 132 / 37 / 46 / 18 | 196 / 46 / 63 / 34 |
| Synthesized by tier | preferred 18 | preferred 34 |
| Clearance moves / islet exceptions | 32 / 0 | 37 / 2 (3.49 units) |
| Ports kept (forced) | 30 of 30 (0) | 25 of 25 (0) |
| Notables moved | 19 | 14 |

No fortification has 0 villages, no village is within 4 units of a fortification except the recorded exceptions, and no two settlements share a point. Both pass the settlement harness (`tools/Test-Settlements.ps1`) and the navigation sweep with placement and flags (`tools/Test-NavigationSweep.ps1`).

**Seeds 42–61 and 73** (`evidence/fwg/village-quota.json`, `tools/summarize_village_quota.py`; generation with 3 attempts, then bundle and the settlement harness): all 21 accepted as before, every town exactly 3 and every castle exactly 2 villages, none with 0. All 17 forced ports have their pinned, synthesized harbour village. 577 villages were synthesized (566 preferred, 1 wide, 10 islet) and 1,064 dropped; 33 villages are recorded islet exceptions (the only villages within 4 units of a fortification), no two settlements share a point, no castle had to be excluded, two protected farming villages were swapped in, and every world's second analysis is stable. 20 of the 21 pass the settlement harness; seed 61 is refused for two castle villages with no town of their castle's kingdom within the engine trade limit, the refusal MMR-251 recorded before the quota (a kingdom-geometry shape, left to the kingdom merge of MMR-279). A town keeps its own villages only within the engine trade distance (3 × S): seeds 45 and 52 had kept a source village 131.5 and 240.7 units from its town.

**Verification.** `travel_graph_tests` covers the quota on small worlds: the pin, the harbour cap, a new-class land village, the castle takes from its garrison town, a `quota:` drop and its notable's move, a village on its town's point moving clear, two synthesized villages in the preferred tier with id-map entries and the largest-deficit class, a culture's last farming village kept, a forced port's synthesized harbour village, the campaign v2 fields, and two runs giving byte-identical records and travel graphs. `campaign_semantics_tests` covers castle-bound villages in closure and `exclude()` erasing only the ports it empties.

**Defaults taken.** The reach (castle takes, surplus, clearance search) is the widest tier's `maxSpacing` × S; the harbour cap holds in every step; the deficit compares land classes only (harbour villages are pinned or capped); a harbour village moves or is synthesized within 2 cells of its harbour or port cell (the managed village port envelope is 19.3 units); MMR-251's kept-village condition for a forced port stays, so the 10 moved towns of the sweep still move (MMR-251 suggested relaxing it once villages could be synthesized; not done here: it would add forced ports, RT-43).

## Host checks (run by MMR-38: `docs/runtime-navigation.md`)

| Check | Subject |
|---|---|
| `gateFace` / `settlementFace` | Every kept town gate, village and castle cell is a Default-valid land face, of one of the land types of the cells touching its point (MMR-254), on its group's island |
| `villageRoute` | Every kept village has a Default route to its bound town or castle |
| `portFace` | Every valid port cell is a Naval-valid CoastalSea face sharing an edge with its land entrance, and `SetSailAtPosition` succeeds there |
| `portGate` | `GetPortToGateDistanceForSettlement` is finite and within the installed envelope, and a Default route joins the land entrance and the gate |
| `bridge` | Every joining crossing carries Default-valid faces over Naval-valid ones (Bridge over UnderBridge proposed; MMR-38 chooses), and a Default route crosses it |
| `pass` / `footprint` | Every pass, access and footprint cell is Default-valid, of the cell's land type (MMR-254), not Mountain |
| `landRoute` | Every road whose ends share a group has a Default route between the gates |
| `seaRoute` | Every player-safe group other than the main one has a sail-out and sail-back route |
| `seamTransition` | Transitions on a seam column work across the periodic seam (R10 left these open) |
| `distinctPositions` | Settlements sharing an emitted point |
| `openSea` | Every open basin either carries OpenSea faces or is reported to have no storm area |
| `islands` | Every gate, port and kept castle lies on one engine island |
| `padding` | Faces beyond the pole lines are not navigable |

## Real worlds (pinned producer, seed 42)

**Size 129 is accepted.** It is the source size MMR-26 froze for the 257-vertex terrain.
- **Components:** 28 land components in 27 groups, joined by 1 required bridge (river or lake).
- **Basins:** 35 basins, of which 1 is open, with 2,963 OpenSea cells.
- **Ports:** 30 of the 44 harbour towns have a valid port, every one within 2 cells of its gate. The other 14 harbours lie farther than the installed envelope.
- **Reachability:** the 11 player-safe groups still hold every town; the main group has 17.
- **Exclusions:** 6 villages (2.7 %) lie outside their town's land group.
- **Repairs:** 14 road passes, 4 access cells (one town walled in on a coastal massif), 6 footprints, 4 snaps and 1 harbour shift.
- **Shared points:** 1 position collision.
- **Compile time:** 2.6 s, including the in-memory terrain and campaign compiles.

The other research sizes are rejected, each with the reasons and UIDs that come from resolution, as MMR-26's fidelity table predicted. They are outside the frozen configuration.

| Size | Why rejected |
|---|---|
| 33 | 3 towns unplaceable: islets of a single sample vanish when bilinearly upsampled 4×. 3 more towns are on portless islands |
| 257 | 40 snaps (12.8 % of 312 settlements) exceed the 5 % bound: settlement points fall inside one cell, and coastal hamlets land on sea cells. 1 town group is unreachable and 1 town is unplaceable |
| 513 | 73 snaps (19.9 %) and 36 harbour shifts exceed their bounds; 1 town is unplaceable |
| 1025 | 113 snaps (29.2 %) and 31 harbour shifts exceed their bounds; 2 town groups are unreachable and 1 town is unplaceable |

**Generation gate (`travel-gate.json`, real generations at size 129, seed 42):**
- **Real policies:** attempt 0 is accepted, and the attempt directory keeps its travel outputs.
- **Strict variant** (`maxSnapFraction` 0): attempt 0 is rejected with `quality.repair_bound_exceeded`. The reason names the 4 snapped hamlets. The world is retried on derived seed 2675596451, which fails only the same zero-snap bound.
- **Compiler refusal** (grid side 300): the generation fails at once with `compiler.out_of_range`.

## Limits

- **Coastline clearance is not measured.** Ports follow MMR-17's one-cell layout within the installed envelope, and every port is a host check.
- **Proposals for MMR-38.** Bridges and the face-group mapping were proposals. MMR-38 realized them (Bridge decks over UnderBridge faces) and kept every land cell Plain: biome land types would not change validity, and Forest and Snow battle scenes need MMR-42's substitution rules.
- **Fixture conventions.** The mountain threshold and the 0.01 vertical scale come from the fixtures; the 2.5× exaggeration is recorded in MMR-25/26. About 21 % of the size-129 land is mountain.
- **Anti-diagonal rivers.** They rasterize into cells that touch only at corners, which can leave 1-cell land pockets. This is valid under the edge model, but it raises the bridge and exclusion counts.
- **Villages.** Whether War Sails villager parties sail is not established, so a village needs a land path to its town.
- **AI reachability.** Coast embarking makes AI reachability broader than the player's. The AI case is the `islands` host check, not a static rejection.
- **Unused harbour records.** Harbours beyond the port-gate envelope remain villages with harbour roles. Their towns have no port unless another harbour qualifies.
- **Other sizes** are rejected under this policy (see above).
