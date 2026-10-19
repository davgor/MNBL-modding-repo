# MMR-28 independent review (2026-10-15)

Reviewer: an independent Claude subagent with no write access to the repo. Scratch files are in %TEMP%\mmr28-review: a mini schema validator, a closure script and a C++ harness linked against the Release libs. The report below is verbatim; the disposition follows it.

---

ACCEPT WITH FIXES

Nothing in the repo was edited. Scratch: C:\Users\davgo\AppData\Local\Temp\mmr28-review (minischema.py, closure.py, harness\harness.cpp, verify.json).

## Criteria

**c1 — met.** I ran my own closure script over all 5 real outputs (s33/129/257/513/1025):
- groups partition the land components;
- land and group back-links agree;
- basin `ports` equal the chosen ports;
- port transitions match ports.json cells and groups;
- road ends, UIDs and crossing land ids all resolve;
- exclusion ids, UIDs and kinds resolve;
- repair subjects resolve;
- the cells.bin header and size are correct;
- ports.json `inputs` equals travel-graph.json `inputs`;
- settlement ids and per-town candidate villages equal MMR-27 campaign.json.

All references resolve, and `campaign::exclude` re-checks closure. `engineValidation:"pending"`, transition `pendingEngineValidation` and host-check `pending` are present throughout. All 12 real outputs validate against both schemas; jsonschema is not installed, so I used a hand-written validator covering every keyword the schemas use.

**c2 — partially met.** Every gate and port coordinate is deterministic and traceable: source point, method, shift, sea node, village UID and harbour cell. A repeat run is byte-identical. But the port candidates contradict the documented rule: they are not tied to the gate (M1). Many settlements also share the same gate cell, so they emit the same point (S2).

**c3 — met with gaps.** Each named outcome has at least one real test:
- river bridge candidates, producer-reported and not, plus an unlisted kind;
- closed basins, both lake-only and a 2-cell sea pocket;
- isolated islands: an island without a port is rejected, two islands with ports are accepted, and a castle on a portless island is excluded;
- invalid ports.

Only 2 of the 6 invalid-port reasons are tested, `closed_basin` and `sea_node_not_navigable`; see S7.

**c4 — partially met.** Repairs carry stable UIDs. The `unreachable_town`, `unplaceable_town` and `too_many_exclusions` reasons carry UIDs. But `quality.repair_bound_exceeded` has `uids: []` and none in its message. In the FWG-03 attempt state every code loses its `uids`, so in generation, exclusion and bound rejections are UID-less (M2).

## Checks performed
- **Build and tests:** rebuilt and ran CTest, 8/8 pass (travel 57/57, source_reader 142/142). Re-ran the Python verifier on s129, 19/19, with a byte-identical repeat.
- **Calibration vs MMR-17 and navmesh-bin.md §5:**
  - The invalid sets for Default, Naval and All, the 50/50 region switch costs and the 0.5 embark threshold match both worlds in navigation-probe.json exactly.
  - The TerrainType ids match the §5 table, and the 3 pinned SHA-256s match the files.
  - The proposed face groups are consistent with the invalid sets, checked by hand and in `load_policy`.
- **Classification vs the fixtures:** it matches the bootstrap world builder that MMR-17's accepted run used: main-diagonal centre below 0, and (max − min)/cell > 1 for mountain (build_bootstrap_world.py:121-124).
  - navmesh_bin.py `campaign_face_id_rule` differs: it uses the 4-corner mean and OpenSea by depth.
  - The doc correctly cites the bootstrap rule.
  - Edge-only connectivity matches the two-triangle main-diagonal split, and seam wrap is consistent with R10.
- **Generator source ec5d1c1:** `road.from/to` are indices into `world.settlements.sites` (roads.cpp:150). In the real worlds site id equals index, so decoding by site id is correct. The generator records a crossing when either step end is a river node, so many "crossings" are bank steps.
- **generation_driver:** traced the policy binding, the parser vs quality failure kinds, and retry only when every code is declared (attempt_schedule.cpp:297-303). Read travel-gate.json and its state files.
- **Scratch harness against the Release libs** (results quoted below).
- **Real-output analysis:** groups, basins, ports, gate distances, crossings, shared cells, exclusions, and producer crossings against compiled crossings.

## Must-fix

**M1 — ports are not tied to their town's gate, contrary to the doc.**
- **Where:** port_compiler.cpp:60-73 and :81-86; docs/travel-graph.md:83-84.
- **What happens:**
  - The doc says the port follows "the MMR-17 layout: a CoastalSea face one cell off the Plain gate face".
  - The code takes as land entrance any land cell of the town's *group* that is nearest the *harbour*.
  - The town's port is chosen by harbour quality before gate distance.
  - Gate distance is unbounded and has no host check.
- **Real s129:**
  - Only 9 of 44 land entrances are the gate cell.
  - Three ports sit 10 or more cells away; surface-city-2-1338-human_cold is at 25.0 cells, about 156 units.
  - surface-city-2-11626-hill_dwarf (gate [45,104]) and surface-city-1-11099-human_heartland share the identical port: land [45,90], sea [45,89]. 11626's port is 14 cells from its gate and 2 from the other town's.
- **Harness:** H1 reproduces the shared port pair. H4 shows a valid port whose land entrance is 8.2 cells away, across an unproven river bridge candidate.
- **Why it matters:** War Sails puts the party at the port when leaving by sea and enters the town from it. MMR-17 observed the port at 4.6–6.7 units from the gate and never tested a distant port.
- **Fix:**
  - Add a policy bound (for example `maxPortGateCells`, with invalid reason `port_too_far_from_gate`), or rank by gate distance first.
  - Prefer an entrance in the gate's own land component.
  - Reject or resolve two towns sharing a port pair.
  - Add a host check on `GetPortToGateDistanceForSettlement` (MMR-17 invoked it).
  - Correct doc lines 83-84.

**M2 — rejections lose UIDs, especially inside generation.**
- **Where:**
  - travel_graph.cpp:649-656: the bound reasons push `{}` uids.
  - generation_driver.cpp:270: pushes only `{r.code, r.message}`.
  - attempt_schedule.hpp:27: `Reason` has no uids.
- **Effect:** travel-gate.json's strict attempts record only "4 settlement snaps exceed maxSnapFraction" and "11 …", with no UIDs. Harness H2 confirms `uids=0` for the footprint and snap bound reasons.
- **Fix:**
  - Fill `uids` with the offending repair kind's subjects: snapped and footprinted settlement UIDs, and the road from/to UIDs for passes and bridges.
  - Carry the uids into the attempt state, by appending them to the message or extending `Reason`.
  - Consider writing each attempt's travel-graph.json into its attempt directory as rejection evidence.

## Should-fix

**S1 — bridge candidates are unbounded in length, and sea "coast" bridges grant player-safety.**
- **Where:** travel_graph.cpp:522-534; `bridges.kinds` in playability.json.
- **Real outputs:** coast bridges of up to 5 sea cells at s1025, and 4–5-cell crossings at s257 and s513, joining components that hold towns.
- **Harness:** H5 accepts a town on a portless island 3 sea cells away solely through a coast bridge; without the road (H6) the world is rejected. This conflicts with "preserve ocean-separated land if maritime travel proves it connected".
- **Fix:** add a `maxBridgeCells` bound. Either drop `coast` from the kinds that count for player-safety, or record coast joins as bounded repairs.

**S2 — settlements share gate cells, so their emitted points coincide.** In s129, 50 cells hold two or more settlements. Cell [44,13] holds a town plus 3 villages, one of them snapped onto the gate cell (harness H8). Fix: give each settlement a distinct cell or a sub-cell offset, or record the collisions with a host check.

**S3 — producer river crossings with no compiled crossing vanish without a record.**
- **Where:** travel_graph.cpp:783-786 and :526.
- **Effect:** at s33, 38 producer crossing steps produced only 4 producer-flagged crossings. Steps adjacent to a pole node are silently dropped.
- **Fix:** emit an outcome per producer bridge candidate: matched crossing, no compiled water, or dropped at a pole.

**S4 — footprint cells on a road are recorded twice.**
- **Where:** travel_graph.cpp:394-399.
- **Effect:** a footprint cell lying on a road is recorded again as a `pass` and counted against `maxPassFraction`. At s129 this happens at [25,32] and [26,32], so the doc's "17 road passes" includes 2 footprints (harness H3 reproduces it). Footprint cells not on a road get no host check.
- **Fix:** skip cells that are already footprints when carving passes, and add a host check for footprint cells.

**S5 — harbour shifts have no global cap.** Each shift is at most 2 cells, but there is no cap on the count; s513 has 23. The repair's `from` records the harbour cell, not the sea-node cell (port_compiler.cpp:92).

**S6 — the verifier is a line-by-line Python port, not an independent derivation.**
- Its inputs are independent (the MMR-26 artifacts on disk, world.json, campaign.json), so it does catch drift between the on-disk and in-memory compiles and C++ slips.
- It uses the same rank and penalty tables and tie rules, so it cannot catch spec flaws such as M1, S1 or S4.
- `statusRecomputed` trusts `graph['reasons']`, and no bound fraction is ever recomputed.
- It does not check:
  - that the repairs array is complete;
  - host checks or coast-pair counts;
  - harbour_shift records;
  - the schemas;
  - any size but 129.
- **Fix:** recompute the bounds, require a repair record for every class-changed cell, and validate against the schemas.

**S7 — test gaps.**
- Untested invalid-port reasons: `basin_not_reachable_from_town`, `coast_too_far_from_harbour`, `harbour_village_excluded`, `town_unplaceable`.
- Untested: the lake and coast bridge kinds and the `maxRequired` bound.
- Untested: the gate refusal path (`compiler.*` failing the generation).
- The pole-passage test is weak: its two pieces would not join even without the split.

**S8 — the post-exclusion campaign records are discarded.**
- **Where:** travel_graph.cpp:954.
- **Effect:** they are computed and closure-checked, then thrown away. `landComponents` still lists excluded settlements.
- **Fix:** emit them, or document how MMR-38 and the campaign export must apply exclusions.

**S9 — host-check gaps.**
- No gate-face check for villages and castles.
- No check for village-to-town land routes.
- No check for seam transitions; R10 lists seam embark and disembark as unresolved.
- The `bridge` check names Bridge 17, but neither shipped map authors any Bridge 17 face (navmesh-bin.md §5). Phrase it as Default-valid faces over Naval-valid ones.

## Minor
- **Calibration:**
  - `portOffsetFromGate: 6.25` holds for world 42 only; world 43 is 5.0, yet the note says the values are identical in both.
  - The `open` list should note that MMR-17 never authored OpenSea 19 (its open-sea fixture point was group 18) or River 11.
  - `load_calibration` checks only the format of `evidenceSha256`, not the evidence file; the 3 pinned hashes were verified by hand.
- **Doc:** it references evidence/fwg/travel-review.md, which does not exist. It gives s1025 snaps as "31 %"; the actual figure is 113/387, 29 %.
- **Schema looseness:**
  - ports.json `inputs` is just `{type: object}`;
  - no if/then requires `seaCell` or `landCell` on valid candidates, or `town` on port transitions;
  - host-check subjects are free-form strings.
- **Unused:** `regionSwitchCost` and the embark threshold are loaded but never used.
- **Anti-diagonal rivers:** they rasterize into cells that touch only at corners, leaving 1-cell land pockets (harness H7). At s129, 34 of the 35 basins are lake or river fragments. Land 9 (1 cell, 1 village) exists only through two 1-cell "bridges" on road 8. This is valid under the edge model but inflates the bridge and exclusion counts.
- **Real worlds:**
  - s129 is accepted soundly: one open basin, and every one of the 10 town-bearing groups has a valid port into it.
  - The 16 groups that are not player-safe are all 1–8-cell islets or pockets with no towns or castles.
  - The 3 village exclusions are all harbour villages on 1-cell fragments (corner-only contact, or cut off by river or sea); each of their towns keeps another valid port.
  - The attribution of the rejections at s33, s257, s513 and s1025 matches the outputs.

---

## Disposition

All findings were applied in one batch, then verified once: unit tests, CTest, real compiles, the verifier on all five sizes and the generation gate.

**Results after the batch:**
- **Tests:** `travel_graph_tests` 77/77 (20 new), `source_reader_tests` 142/142, CTest 8/8, `campaign_semantics_tests` 88/88.
- **Campaign output unchanged:** after the serializer refactor, `--compile-campaign` output is byte-identical to the accepted MMR-27 artifact.
- **Verifier:** 22/22 checks on every research size (33, 129, 257, 513, 1025), accepted and rejected alike.
- **Repeat compile:** byte-identical.
- **Generation gate:** 6/6 checks.
- **Size 129** is still accepted; the other sizes are still rejected, now with UIDs in every reason.

| Item | Disposition |
|---|---|
| M1 | **Fixed.** A port now serves its gate: the coast pair of the gate's own land component nearest the gate, never a pair another town holds.<br>• **Bound.** `ports.maxPortGateCells` is 2 cells (12.5 units). It is calibrated from the installed War Sails map: 29 port towns with gate-to-port distances of 4.86–10.97 units, now in `naval-transitions.json` `installedPortGateEnvelope` with the source file's SHA-256.<br>• **Invalid outcomes:** `port_too_far_from_gate`, `port_pair_taken`, `basin_not_reachable_from_gate`.<br>• **Choice:** the town's port is the valid candidate nearest the gate.<br>• **Host check:** `portGate` (`GetPortToGateDistanceForSettlement`).<br>• **Size 129:** 30 of 44 harbour towns keep a port, all within 2 cells; the 11 player-safe groups still hold every town. The shared pair (11626 / 11099) is gone.<br>• Doc corrected |
| M2 | **Fixed.**<br>• Every bound reason carries its UIDs: road ends for passes and bridges, settlements for access passes, footprints and snaps, villages for harbour shifts.<br>• The driver appends `[uids: …]` (the first 20) to each attempt reason, and writes each attempt's travel outputs into `attempt/travel/`.<br>• `travel-gate.json` shows the 4 snapped hamlets named |
| S1 | **Fixed.** `bridges.maxCells` is 2. `coast` is dropped from the joining kinds; coast crossings are recorded but join nothing, because sea is crossed only through ports. Tests cover the lake join, a coast crossing that does not join, the length bound and the `maxRequired` bound |
| S2 | **Fixed.** The emitted point is the source point clamped into its cell, a quarter cell inside the edges, so shared cells no longer give identical points. Points that still coincide are listed in `positionCollisions`, each with a `distinctPositions` host check (1 at size 129) |
| S3 | **Fixed.** Every producer river crossing gets an outcome (`matched` or `no_compiled_water`); crossings on pole steps are counted in `producerPoleCrossings` |
| S4 | **Fixed.** Footprint cells are never recorded again as passes, and footprint cells get a `footprint` host check. Size 129 now has 14 road passes (the 2 double-counted footprints are gone) |
| S5 | **Fixed.** `maxHarbourShiftFraction` (0.1) caps the count, with UIDs. A harbour-shift repair records the sea-node cell as `from` |
| S6 | **Fixed.** The verifier now:<br>• recomputes every reason with its code and UIDs, and so every bound;<br>• requires a repair record for every changed cell, and that every carving record changed its cell;<br>• validates both schemas with a small validator covering the keywords used, including if/then;<br>• checks the calibration's pinned evidence hashes and the installed envelope source hash;<br>• checks the post-exclusion campaign records;<br>• verifies all five sizes.<br>It still shares the compiler's rules for the core derivations. The policy-independent checks (port adjacency, distance and uniqueness; gates on land; transitions; ids; schemas; hashes; repair completeness) do not share them |
| S7 | **Fixed.** New tests cover:<br>• the invalid-port reasons `basin_not_reachable_from_gate`, `port_pair_taken`, `port_too_far_from_gate`, `harbour_village_excluded` and `town_unplaceable`;<br>• the lake and coast kinds, `maxCells` and `maxRequired`;<br>• UIDs in the pass, footprint, snap, bridge and harbour-shift bound reasons;<br>• a footprint on a road, producer crossing outcomes, emitted points and collisions, and a seam transition.<br>The pole test now splits a road that would otherwise bridge a river. The gate refusal path (`compiler.out_of_range` fails at once) runs in `Test-TravelGate.ps1` request C |
| S8 | **Fixed.**<br>• The travel output writes `campaign.json`: the MMR-27 records after the exclusions, with the `position` slots (towns, villages, castles) and `portSite` slots resolved by MMR-28.<br>• Towns whose port is invalid lose their port record.<br>• `campaign::closure_problems` is re-checked.<br>• `landComponents` lists every placed settlement, and `exclusions` names the dropped ones (documented) |
| S9 | **Fixed.**<br>• New host checks: `settlementFace` (villages and castles), `villageRoute`, `seamTransition`.<br>• The bridge check is rephrased as Default-valid faces over Naval-valid ones, noting that no shipped map authors Bridge 17 |
| Minor | **Fixed:**<br>• the calibration's port offset is per world (6.25 and 5.0);<br>• its `open` list notes that MMR-17 authored no OpenSea 19 or River 11;<br>• the review file exists;<br>• the doc percentages are corrected (1025: 29.2 %);<br>• the schemas are tightened: a full `inputs` block in ports.json, if/then for valid and invalid candidates and for port transitions, and the new fields;<br>• the region switch costs and embark threshold appear in the graph's `engine` block.<br>**Kept as designed:**<br>• `load_calibration` checks only the hash format, because the evidence files do not ship with the mod; the verifier checks the hashes;<br>• host-check subjects stay free-form;<br>• anti-diagonal river pockets are documented as a limit |
