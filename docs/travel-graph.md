# Static travel graph and port candidates (MMR-28, 2026-10-15)

`worldbridge --compile-travel` computes the campaign's static travel candidates on the compiled terrain (MMR-26), under the War Sails contract MMR-17 observed:
- land components and water basins;
- roads, with their passes and bridge candidates;
- ports and transitions;
- player reachability.

It also resolves the slots MMR-27 left to MMR-28: settlement positions and port sites.

**Nothing here proves engine movement.** Every result carries `pendingEngineValidation`. The compiler emits the host checks that MMR-38 runs once it has built the faces (W02–W05): see `docs/runtime-navigation.md` for what each check runs and `evidence/world/navigation-routes.json` for the engine results.

| | |
|---|---|
| Code | `src/WorldBridge/src/travel_graph.cpp`, `src/port_compiler.cpp`. The reader now keeps `roads.routes[].river_crossings` |
| Policy | `worldgen/policies/playability.json` (`mnbl.playability` v1). It pins the calibration by SHA-256, resolved relative to the policy file |
| Engine contract | `worldgen/calibration/naval-transitions/naval-transitions.json`. From the accepted MMR-17 run 2248:<br>• the invalid-terrain sets for Default, Naval and All;<br>• the TerrainType ids;<br>• region switch costs 50/50 and the 0.5 embark threshold;<br>• the observed embark and disembark rules.<br>Plus the installed War Sails port-gate envelope (`settlements.xml`, 29 port towns, at most 10.97 units) and what MMR-17 left open |
| Outputs | • `travel-graph.json` (`worldgen/contracts/travel-graph.schema.json`);<br>• `ports.json` (`ports.schema.json`);<br>• `cells.bin`, the cell classes after repairs plus the OpenSea flags;<br>• `campaign.json`, the MMR-27 records after the exclusions, with positions and port sites resolved.<br>All are deterministic, and every input hash is recorded |
| Tests | `src/WorldBridge/tests/travel_graph_tests.cpp` (CTest `travel`, 83 checks), plus 3 road-crossing cases in `source_reader_tests` (142 checks). Logs: `evidence/fwg/travel-tests.txt`, `evidence/fwg/travel-ctest.log` (8/8 suites) |
| Evidence | `evidence/fwg/travel-graph.json` (`tools/summarize_travel.py`): every research size, from 33 to 1025, re-derived independently from the MMR-26 terrain artifact and the MMR-27 records, 22/22 checks each. It includes schema validation, the recomputed reasons and bounds, the completeness of the repair records, and the calibration evidence hashes. A repeat compile is byte-identical. `evidence/fwg/travel-gate.json` (`tools/Test-TravelGate.ps1`) records the gate inside `--generate`. Review and disposition: `evidence/fwg/travel-review.md` |

```
worldbridge --compile-travel SPLIT --contract worldgen/contracts/source-compatibility.json \
  --projection-policy worldgen/policies/projection.json --materials worldgen/policies/terrain-materials.json \
  --import worldgen/calibration/terrain-import/terrain-import.json --content worldgen/policies/campaign-content.json \
  --cultures worldgen/policies/culture-presentations.json --playability worldgen/policies/playability.json --out NEW_DIR
```

The terrain (MMR-26) and the campaign records (MMR-27) are compiled in memory from the same policies. The recorded `terrainMasksSha256`, `terrainHeightfieldSha256` and `idMapSha256` equal those compilers' artifacts, and the verifier checks this. Exit 0 means status `ok`. Exit 3 means `rejected`; the outputs are still written, with the reasons.

## Navigation cells

Cells are the compiled grid's cells: 6.25 world units at 257 vertices.
- **Extent:** one seam period of columns (they wrap) between the pole lines. Padding rows are not part of the graph.
- **Size:** 128 × 64 cells at the pinned settings. `cells.bin` holds each cell's class after repairs and its OpenSea flag. Cell (r, c) sits at physical sample (rowOffset + r, columnOffset + c).

| Class | Rule (in order) | Proposed TerrainType for MMR-38 |
|---|---|---|
| sea | the main-diagonal centre height is below the datum (the MMR-17/18 bootstrap fixture rule; the diagonal is the MMR-16 triangle split) | CoastalSea 18; OpenSea 19 when at least 4 cells (Chebyshev) from any other class (MMR-59: storms need OpenSea) |
| lake | a main-diagonal corner carries compiled lake intent | Lake 8 |
| river | a main-diagonal corner carries river intent | River 11 |
| land | otherwise | Plain 1 |
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

## Town repairs (2026-10-19)

No generated town is dropped (owner decision, `docs/research/map-epic-2-findings.md` section 14). Before this change a single unplaceable or unreachable town rejected the whole world: 30 of 31 seeds (43–73) at the one-shot profile, mostly islet towns without a port and towns whose source point lies in the sea at size 129. Now the compiler repairs such towns and runs the analysis again:

1. **Coast port**, inside each round. A town whose group is not player-safe and that has no valid harbour port takes a coast pair of its own land component as its port when:
   - its group holds a kept village for each of its towns and castles, so the binding can give every fortification a village;
   - the pair's basin is open and a group that returns to the main group touches it;
   - the pair is not held by another port, its sea cell is not a deck cell and lies within `maxPortGateCells` of the gate, and its path bound fits the envelope (steps 3–5 above).

   The pair chosen is the one whose sea cell is nearest the gate, then whose land cell is, then by cell order. Towns are taken in input order. The port has no harbour village: `ports.json` marks it `"kind": "coast"`, and `campaign.json` gets a port record with no villages, so the town is a port town.
2. **Join**, between rounds. A group that is still not player-safe joins a player-safe group through the shortest run of mountain cells, at most `maxAccessCells` (6). The search is breadth-first from the group's land cells. Each carved cell is a `join` repair with a `pass` host check.
3. **Move**, between rounds. A town whose group cannot join, or a town with no cell, moves to the nearest land cell of a player-safe group, measured from its source point. The cell must have been land before any repair and must hold no settlement. The settlement method is `move`. Its own villages outside the new group are then excluded as usual, within `maxExcludedVillageFraction`.

Each round starts again from the compiled classes with the joins and moves found so far. There are at most four rounds. A town that no repair can make player-safe still rejects the world, for example when no free land cell is left.

**What stays the same.** The town repairs run only for towns that would otherwise reject the world, and they sit outside the repair bounds. All other bounds still apply: passes, footprints, snaps, bridges, harbour shifts and exclusions. A world whose towns are all player-safe in the first round is analysed once and unchanged. World A's travel outputs (`travel-graph.json`, `ports.json`, `cells.bin`, `campaign.json`) and its world content id are byte-identical.

`playability.json` is unchanged, because it is a world input in the bundle's `policies/`. Its notes still say that ports come only from harbour records and that unplaceable or unreachable towns reject the world; this section supersedes them.

**Yield** (`evidence/world/seed-yield.json`, `tools/summarize_seed_yield.py`). This is an offline sweep (`build/seed-sweep/after5`, 2026-10-19) at the one-shot profile (size 129, phase 16), with one quality attempt per seed:
- seeds 42–73 went from 1 of 32 accepted to 19 of 32, world A (42) and world B (73) among them, both with their content ids unchanged;
- each of the 19 passes the activation's managed stages offline:
  - it builds its settlements through SettlementFactory and the village binding (the harness of `tools/Test-Settlements.ps1`);
  - it passes the runtime navigation build, plan and validator over the stand-in engine (`tools/Test-NavigationSweep.ps1`);
  - its naval content, spawn policy, graph validation, factions, characters, registry and water stages raise no refusal (their harnesses);
- 12 of the 13 rejected seeds fail on a repair bound (snaps, passes, footprints or harbour shifts), and seed 71 on too many excluded villages;
- the built worldbridge reproduces every one of the 19 bundles byte for byte (`build/seed-sweep/after6`, `after6-73`).

The harnesses also carry world A's fixture counts (ruler and slot counts) and their negative self-tests are written against A; on other worlds those lines fail without a refusal and are listed as `fixtureLines`. One describes the world rather than the fixture, and is follow-up for broad seed certification: B and seed 57 present 6 of the 7 character-creation cultures (no Khuzait realm); a player who picks the missing culture starts through the spawn policy's culture fallback. A third line, lake copies cut by the terrain's edge on 8 worlds (B among them), was a presentation defect, fixed the same day (`docs/water-climate.md`, Edge copies).

A request gets three quality attempts, so a seed whose first attempt is rejected usually still generates.

**Village repairs found by the sweep** (worlds the gate used to reject, which reached activation for the first time):
- a kept harbour village whose producer sea node resolves to no navigable cell (`sea_node_not_navigable`) loses its harbour role in `campaign.json`, since activation refuses a harbour village without a harbour cell (seed 60);
- so does a kept harbour village whose harbour cell lies on a closed basin (`closed_basin`), since activation lands only on open basins and refuses its port otherwise (`quality.naval_port`; 11 of the 17 other accepted worlds of the 2026-10-19 sweep);
- a village farther than the engine trade distance (3 × the world's town spacing) from every town and castle of its land group is excluded, since no binding could keep it (seed 62).

**Verification.** `travel_graph_tests` (88 checks) covers each repair: coast port, join, move, a move with no free cell (rejected), no repair on player-safe worlds, the coast-port marking in `ports.json`, and the trade-distance exclusion. It also covers the port rules: a river-mouth harbour skipping a deck cell, `port_pair_under_bridge`, and `port_path_too_long` around a mountain cell, with the bound's value checked; and one deck per cell. `tools/summarize_travel.py` re-derives worlds without town repairs, including the deck, one-deck-per-cell and path-bound rules; it does not re-derive the town repairs.

**Applying the results to the campaign records.** The exclusions are applied to the MMR-27 records with `campaign::exclude`, which re-checks closure, and the travel output writes the result as `campaign.json`. Its `position` slots (towns, villages, castles) and `portSite` slots are resolved with owner MMR-28. `landComponents` lists every placed settlement, excluded ones included; `exclusions` names the dropped ones.

**In generation (FWG-03).** A request can bind `playability` together with `projection`, `terrain-materials`, `terrain-import`, `campaign-content` and `culture-presentations`. When it does, `worldbridge --generate` runs this compiler on every attempt's world and writes its outputs into the attempt's `travel/` directory.
- **Rejection:** a quality failure whose reasons carry their UIDs (`[uids: …]`, the first 20). `worldgen/policies/world-quality.json` declares the four codes retryable, so the next attempt uses a derived seed.
- **Compiler refusal:** `compiler.<code>` fails the generation at once.
- **Other requests:** requests that do not bind these policies behave as before.

## Host checks (run by MMR-38: `docs/runtime-navigation.md`)

| Check | Subject |
|---|---|
| `gateFace` / `settlementFace` | Every kept town gate, village and castle cell is a Default-valid Plain face on its group's island |
| `villageRoute` | Every kept village has a Default route to its bound town |
| `portFace` | Every valid port cell is a Naval-valid CoastalSea face sharing an edge with its land entrance, and `SetSailAtPosition` succeeds there |
| `portGate` | `GetPortToGateDistanceForSettlement` is finite and within the installed envelope, and a Default route joins the land entrance and the gate |
| `bridge` | Every joining crossing carries Default-valid faces over Naval-valid ones (Bridge over UnderBridge proposed; MMR-38 chooses), and a Default route crosses it |
| `pass` / `footprint` | Every pass, access and footprint cell is Default-valid, not Mountain |
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
