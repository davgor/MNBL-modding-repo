# Bannerlord navigation mesh files: `navmesh.bin` (RNM1) and NMG nav prefabs

Target: Bannerlord v1.4.8.119303 and War Sails (`Modules/NavalDLC`) v1.2.8.119303. Codec and writer:
`tools/scenebin/navmesh_bin.py`. Tests: `tests/test_navmesh_bin.py`. Working data and logs:
`build/navmeshbin/` (git-ignored).

Labels used below:

- **verified**: checked by code that ran against every relevant installed file, or against the IL.
- **inferred**: consistent with every installed file, but nothing proves the engine depends on it.
- **unknown**: not determined.

Nothing here has been tested in the game during this work; the game was not launched. The only
in-game evidence cited is from earlier sessions (`docs/runtime-capabilities.md`).

## 1. Inventory (verified)

Files under `<game>/Modules/*/SceneObj/*/navmesh*.bin` (all 485 are named `navmesh.bin`, and all use the RNM1 container):

| Module | files | NMG9 | NMG8 | NMG7 |
|---|---:|---:|---:|---:|
| Native | 65 | 46 | 11 | 8 |
| NavalDLC | 58 | 58 | | |
| SandBox | 157 | 96 | 55 | 6 |
| SandBoxCore | 202 | 145 | 55 | 2 |
| StoryMode | 3 | 2 | 1 | |
| **total** | **485** | **347** | **122** | **16** |

Nav prefabs under `<game>/Modules/*/NavMeshPrefabs/*.bin` (127 files), all in the prefab container:

| Module | NMG9 | NMG8 | NMG7 | NMG5 | NMG4 |
|---|---:|---:|---:|---:|---:|
| Native (69) | 5 | 12 | 40 | 7 | 5 |
| NavalDLC (51) | 50 | 1 | | | |
| MNBL.ProceduralWorld (deployed probe files from `NavigationFixture.cs`) | 7 | | | | |

All 7 deployed `mnbl_probe_*` files are byte-identical to `navigation_fixture_prefab(42)` or `(43)`.
Two files named `_42_` contain seed-43 bytes, which matches the earlier in-game replacement
experiment that rewrote an indexed name.

## 2. Containers

### 2.1 Scene `navmesh.bin`: RNM1 (verified on all 485 files)

All values are little endian.

| off | type | value in installed files | meaning |
|---:|---|---|---|
| 0 | char[4] | `RNM1` | magic |
| 4 | u32 | file length − 8 | size of everything after this field. **verified**: 485/485 |
| 8 | u32 | `3` in 485/485 | compression type. Inferred to mean "LZ4 block": every file decodes as raw LZ4. Other values are unknown |
| 12 | u32 | `0` in 485/485 | reserved |
| 16 | u64 | decompressed NMG payload size | **verified**: our LZ4 decode returns exactly this length for 485/485 |
| 24 | u64 | the same value as offset 4 | repeated size. **verified** 485/485 |
| 32 | u64 | `0` in 210 files, otherwise heap-pointer-like (e.g. `0x23cde4b9100`) | uninitialised engine memory |
| 40 | u64 | never 0; pointer-like (e.g. `0x7ff964cea460`), different in each file | uninitialised engine memory |
| 48 | … | raw LZ4 block (no frame, no checksum), running to EOF | NMG payload |

The two values at offsets 32 and 40 look like stack, heap or code addresses, and they change from file to file. **Inferred**: the loader ignores them, because no meaningful reader could depend on values like these. **Unknown**: whether a file with both set to 0 loads. The codec writes 0 by default and keeps the original values on round trip. CSC's in-game-proven writer
copies the original 48-byte header rather than zeroing it, so its success does not answer this question.

**LZ4 block.** The engine's encoder is stronger than greedy LZ4. Its parse picks the nearest of
several candidate matches, and the stream is about 25% smaller than reference `LZ4_compress_default`.
Re-encoding a decoded payload with a reimplementation of reference LZ4-fast differs from the
engine's stream at the first divergent sequence (`build/navmeshbin/lz4try.py`, `lz4seq.py`).
The engine likely uses LZ4HC (**inferred**). `navmesh_bin.lz4_block_compress` provides `fast` (greedy) and
`store` (one literal run). Both produce valid LZ4 blocks that decode back to the same payload,
but neither matches the engine's compressed bytes.
**Hypothesis (not tested here):** the engine accepts any valid LZ4 block. Supporting evidence: Custom
Scene Creator (`build/research/custom-scene-editor/NAVMESH_HANDOFF.md`) documents in-engine acceptance
of navmeshes written with its own simple greedy LZ4 encoder.

### 2.2 Nav prefab `NavMeshPrefabs/<name>.bin` (verified on all 127 files)

`u32 payload_length` (= file length − 4) followed by the **uncompressed** NMG payload. Prefabs are
loaded by `Scene.ImportNavigationMeshPrefab(string name, int navMeshGroupShift)` and
`ImportNavigationMeshPrefabWithFrame(string name, MatrixFrame frame)`. The group shift is added to
face group ids (this follows from the parameter name, so it is **inferred**). Names are indexed when the game starts.

## 3. NMG payloads

### 3.1 NMG9 (verified: 347 scene files + 62 prefabs decode and re-encode byte-identically)

```
char[4]  "NMG9"
u32      vertex_count
         vertex_count × { f32 x, f32 y, f32 z }          (z up; game metres)
u32      edge_count
         edge_count × { i32 e0, i32 v0, i32 v1, i32 e3, i32 e4, i32 e5 }
u32      face_count
         face_count × {
             u32 k                                  degree: 3 or 4 in every installed file (max 4 over all 600)
             i32 vertex[k]
             i32 edge[k]                            edge[j] joins vertex[j] -> vertex[(j+1) % k]
             i32 meta0                              always 2
             i32 meta1                              0 (rare 1/2 in battle scenes)
             i32 group                              face group id = "navigation id" (§5)
             i32 meta3                              0, or the bit pattern of a small float (10.0, 20.0, 50.0, 100.0, …) in some battle scenes
             i32 meta4                              opaque (see below)
             u8  flag                               0 or 1
         }
u32      32                                         tail count
         32 × u64 0                                 tail: 256 zero bytes
```

Field facts checked on every NMG8/NMG9 file (409 NMG9 + 135 NMG8 payloads across scenes and prefabs):

- **Edges:** `e0 = e3 = e4 = -1` and `e5 = 0` in every one of 21,740,013 NMG9 edge records and
  4,249,366 NMG8 records. Only `v0, v1` carry data. Face adjacency is therefore not stored. The
  engine must derive it from two faces referencing the same edge index (**inferred**: the probe
  fixture writes -1 here, and it imported with paths working in game). The likely runtime meanings of
  e0/e3/e4 (per-side face links) are **unknown**.
- **meta0** = 2 in every face of all 600 decodable payloads (12,344,961 faces, NMG7/8/9). Meaning unknown; the value is constant.
- **meta1** ∈ {0, 1, 2}; 0 on 99.99% of faces and on every campaign-map face.
- **meta3**: 0 on 99.4% of faces. Non-zero values decode as round float32 values (1, 2, 3, 5, 10,
  15, 20, 30, 50, 100, 200). It might be a per-face cost or penalty (**hypothesis**). It is 0 on every campaign-map face.
- **meta4**: an opaque int32 that stays the same across neighbouring faces. Campaign maps use -109 (most faces), -101,
  -103, -69, and on War Sails also -1, -13, -111, -77, -5 and 17. Battle scenes mostly use 15 / -1 / -15.
  Castles and towns use six values in roughly equal shares (18, 20, 24, 34, 36, 40, or their
  complements). Each value covers the whole scene extent, so these are not spatial tiles. It does not
  separate land from sea or one group from another. Meaning **unknown**. Possibly an editor display
  colour or tag (**hypothesis**). The writer defaults to -1, the value the in-game-accepted fixture used.
- **flag** byte: 0 on every campaign-map face. It is 1 on about 5% of faces in battle and town scenes, mostly in groups 0
  and 1. Meaning **unknown**.
- **Tail:** exactly `u32 32` followed by 256 zero bytes in every NMG7/8/9 payload. Meaning unknown.
- **Number of NMG blocks:** exactly one per file. In every file the parse consumes the whole
  payload with nothing left over (**verified**). Separate walkable regions are expressed as face
  *groups*, not as separate blocks.

### 3.2 NMG8 and NMG7 (verified)

- **NMG8:** identical to NMG9 except that each edge record is 5 int32 values (`e0, v0, v1, e3, e4`; no `e5`).
- **NMG7:** identical to NMG8 except that faces have **no** trailing `flag` byte. 16 scene files and 40 Native
  prefabs; all decode and re-encode byte-identically.
- **NMG4/NMG5** (12 legacy Native prefabs only): each vertex is 36 bytes (xyz plus 6 zero int32 values), and the
  face metadata and tail differ. These are recognised but **not decoded**; the codec reports them as
  `undecoded-legacy`.

## 4. Topology invariants

These were measured on `SandBox/Main_map` and `NavalDLC/Main_map` (**verified** on both). §6.1 gives the
counts for all 600 decodable installed files.

- **Winding:** every face is counter-clockwise in XY when viewed from +Z (signed area > 0): 17,153/17,153 and
  20,258/20,258 faces. Every face is also strictly convex in XY (0 non-convex).
- **Edge to face sides:** `edge[j]` of a face always joins `vertex[j]` and `vertex[j+1]`. The record's
  `(v0, v1)` follows the direction of one face that uses it. A shared edge is traversed in opposite
  directions by its two faces (32,649/32,649 and 37,060/37,060 shared edges).
- **Manifold:** every edge record is used by exactly 1 face (boundary) or exactly 2 faces (interior).
  No edge is used 0 times or more than twice. SandBox: 935 boundary and 32,649 interior edges. NavalDLC: 687 and 37,060.
- **One record per vertex pair:** SandBox has no duplicated `{v0, v1}` pairs. NavalDLC has one,
  which is a disconnected seam. The validator reports these as warnings.
- **Vertices** are not duplicated by position. Unreferenced vertices occur (13 and 3) and are
  harmless (warning).
- **Faces of different groups share edges.** Land/sea and land/mountain borders are ordinary shared
  edges, not separate meshes. NavalDLC has 1,841 land↔sea shared edges, which form the coastline used for
  embark/disembark.
- **Quads can be non-planar:** median 0.25–0.31 m and maximum 17.6 m of out-of-plane distance. Campaign
  edges have a median length of 3.7 m (SandBox) and 4.9 m (NavalDLC).
- **Faces can overlap in XY across regions.** On NavalDLC, 22 `UnderBridge` (25, sea region) faces lie
  under `Plain`/`Desert` land faces (bridges). `Scene.GetNavMeshFaceIndex(ref PathFaceRecord, Vec2,
  bool isRegion1, bool checkIfDisabled, bool ignoreHeight)` takes an `isRegion1` argument to pick between them.

`build_grid_navmesh` emits meshes that satisfy all of the above by construction. `validate()` checks
them, and the tests assert them.

## 5. Campaign semantics of face group ids (verified from IL)

### 5.1 Face group id = `TaleWorlds.Core.TerrainType`

- `SandBox.MapScene::GetFaceTerrainType(PathFaceRecord)` (SandBox.dll) returns
  `PathFaceRecord.FaceGroupIndex` as the `TerrainType`, with no mapping. If the face is invalid it asserts and returns 1.
  `MapScene::GetTerrainTypeAtPosition(ref CampaignVec2)` calls `CampaignVec2.get_Face()` and then
  `GetFaceTerrainType`.
- `TaleWorlds.Library.PathFaceRecord` (TaleWorlds.Library.dll) has the fields `FaceIndex`,
  `FaceGroupIndex` and `FaceIslandIndex`. `FaceGroupIndex` is the stored `group`. `FaceIslandIndex` is not a stored field,
  so the engine presumably computes it from connectivity (**inferred**).
- `TaleWorlds.Engine.Scene::GetIdOfNavMeshFace(int)` and `SetAbilityOfFacesWithId(int faceGroupId, bool)`
  use the same id.

`TaleWorlds.Core.TerrainType` (TaleWorlds.Core.dll):

| id | TerrainType | `IsTerrainTypeValidForDefault` (land) | `IsTerrainTypeValidForNaval` (sea) | faces on SandBox Main_map | faces on NavalDLC Main_map |
|---:|---|:---:|:---:|---:|---:|
| 1 | Plain | yes | | 4909 | 6436 |
| 2 | Desert | yes | | 1291 | 830 |
| 3 | Snow | yes | | | |
| 4 | Forest | yes | | 2954 | 2859 |
| 5 | Steppe | yes | | 1192 | 766 |
| 6 | Fording | yes | | 62 | |
| 7 | Mountain | | | 3315 | 4742 |
| 8 | Lake | | yes | 180 | |
| 10 | Water | | yes | 898 | |
| 11 | River | | yes | 1376 | 898 |
| 13 | Canyon | | | 563 | |
| 14 | RuralArea | | | 413 | 481 |
| 15 | Swamp | yes | | | |
| 16 | Dune | yes | | | |
| 17 | Bridge | yes | | | |
| 18 | CoastalSea | | yes | | 1969 |
| 19 | OpenSea | | yes | | 880 |
| 20 | Beach | yes | | | |
| 21 | Cliff | | | | |
| 22 | NonNavigableRiver | | | | 322 |
| 23 | LandRestriction | | yes | | |
| 24 | SeaRestriction | | yes | | |
| 25 | UnderBridge | | yes | | 75 |

Ids 0, 9 and 12 are not defined by the enum, and no campaign face uses them. Battle and town scenes use
other ids (0, 1, 2, …), which have mission meanings and are outside the scope of this table.

### 5.2 How the campaign uses the ids (IL call chain, verified)

1. **Load order.** `SandBox.MapScene::Load()` (SandBox.dll) creates the scene and then:
   - calls `DisableUnwalkableNavigationMeshes()`, which runs
     `Scene.SetAbilityOfFacesWithId(id, false)` for every id in
     `PartyNavigationModel.GetInvalidTerrainTypesForNavigationType(NavigationType.All = 3)`;
   - calls `Scene.SetNavMeshRegionMap(SandBoxHelpers.MapSceneHelper.GetRegionMapping(model))`;
   - then `Scene.Read("Main_map", …)` loads `navmesh.bin` with the scene.
2. **Region map.** `MapSceneHelper::GetRegionMapping(PartyNavigationModel)` builds a `bool[max(TerrainType)+1]`
   (length 26) where `map[t] = model.IsTerrainTypeValidForNavigationType(t, NavigationType.Default = 1)`.
   Index = face group id; `true` = region 1 = land, `false` = region 0 = sea or other.
   `MapScene::GetFaceIndex(ref CampaignVec2)` calls
   `Scene.GetNavMeshFaceIndex(ref rec, pos, isRegion1: IsOnLand, checkIfDisabled: false, ignoreHeight: true)`.
   `MapScene::GetPathBetweenAIFaces(…, regionSwitchCostFromLandToSea, regionSwitchCostFromSeaToLand)`
   forwards to `Scene.GetPathBetweenAIFaces(…, regionSwitchCostTo0, regionSwitchCostTo1)`, so region 0 = sea
   and region 1 = land.
3. **Disabled ids.**
   - Without War Sails, `DefaultPartyNavigationModel` (TaleWorlds.CampaignSystem.dll) treats every
     enum value not in {1, 2, 3, 4, 5, 6, 15, 16, 17, 20} as invalid for `All`. On SandBox Main_map
     these are 7, 8, 10, 11, 13 and 14 (6,745 faces), which remain in the mesh but are disabled.
   - With War Sails, `NavalDLC.GameComponents.NavalPartyNavigationModel` (NavalDLC.dll) builds
     `_invalidTypesIntegerCache[All]` from types that are valid for neither naval nor the base model:
     **7 Mountain, 13 Canyon, 14 RuralArea, 21 Cliff, 22 NonNavigableRiver**.
4. **Per-party validity.** `Helpers.NavigationHelper::IsPositionValidForNavigationType(PathFaceRecord,
   NavigationType)` and `CampaignVec2::IsValid()` call `IMapScene.GetFaceTerrainType(face)` and then
   `PartyNavigationModel.IsTerrainTypeValidForNavigationType(type, IsOnLand ? Default : Naval)`.
   `MobileParty::ComputePath(CampaignVec2, NavigationType, bool)` passes
   `GetInvalidTerrainTypesForNavigationType(navType)` as `excludedFaceIds` to `GetPathBetweenAIFaces`,
   together with `MobileParty::GetRegionSwitchCostFromSeaToLand/LandToSea()`. The settlement distance
   cache (`TaleWorlds.CampaignSystem.Map.DistanceCache.SandBoxNavigationCache::
   GetRealDistanceAndLandRatioBetweenSettlements`) uses the same excluded ids and region costs.
5. **Coast transitions.** `Helpers.NavigationHelper::GetEmbarkAndDisembarkDataForPlayer` and
   `CalculateTransitionStartAndEndPosition` (TaleWorlds.CampaignSystem.dll) classify the faces on both
   sides with `GetFaceTerrainType` and `IsTerrainTypeValidForNavigationType(…, Default)`. They then walk the
   navmesh with `GetLastPositionOnNavMeshFaceForPointAndDirection` /
   `GetLastPointOnNavigationMeshFromPositionToDestination`, using a 0.05 step and a 10-unit probe.
   Embarking and disembarking therefore require land-region faces that share an edge with sea-region faces.
   On NavalDLC Main_map, land and sea form 5 and 3 connected components; 1,841 edges are shared between them.
6. **Other consumers of the id:** `MapScene::GetEnvironmentTerrainTypes(Count)` (battle-scene and
   conversation-scene choice through `DefaultSceneModel`), `NavalDLCCampaignShipDamageModel`,
   `SeaDamageCampaignBehavior`, `StormCampaignBehavior`, `FishingPartyCampaignBehavior`, and the skill
   levelling for terrain. All of these are listed in `build/navmeshbin/callers.txt`.
7. Each `scene.xscene` carries a `SettlementPositionScript` whose variables name the navigation and distance
   models used when the editor baked the settlement distance cache: `DefaultPartyNavigationModel`, or
   `NavalPartyNavigationModel` + `NavalDLCMapDistanceModel`. A new navmesh invalidates that cache, so
   another stream has to rebuild it.

**Consequences for a generated campaign navmesh** (derived from the facts above; not tested in game):

- Paint every face with a TerrainType id.
- Walkable land uses {1, 2, 3, 4, 5, 6, 15, 16, 17, 20}. Sailable sea uses {8, 10, 11, 18, 19, 23, 24, 25}.
  Use 7/13/14/21/22 for impassable cells that should still exist in the mesh.
- Keep land and sea faces edge-connected along coasts.
- Overlapping bridge and under-bridge faces are allowed.

## 6. Validation executed

| check | command | result |
|---|---|---|
| Byte-identical round trip of every installed navmesh and prefab | `python tools/scenebin/navmesh_bin.py roundtrip --recompress "<game>/Modules" --json build/navmeshbin/roundtrip-all.json` | see §6.1 |
| Structural and geometric validation of every decodable file | `python build/navmeshbin/geometry_sweep.py` → `build/navmeshbin/geometry-sweep.json` | see §6.1 |
| Fixture equivalence | `tests/test_navmesh_bin.py::FixtureEquivalenceTests` | the builder output equals both a hand-written restatement of `NavigationFixture.cs` and the 7 C#-written deployed files |
| Synthesized grids | `tests/test_navmesh_bin.py::GridBuilderTests` + `synth` runs | see §7 |
| Unit tests | `python -m unittest tests/test_navmesh_bin.py -v` | 18 tests, all pass |

### 6.1 Sweep results

The sweep results are written to `build/navmeshbin/roundtrip-all.json` and `build/navmeshbin/geometry-sweep.json`.

"Round trip" has three levels:

- **Payload:** decompress, decode NMG, re-encode, and compare to the decompressed bytes. This is the real format check.
- **File:** re-wrap the identical payload with the original 48-byte header fields and the
  original compressed block, and compare to the file on disk.
- **Recompression:** compress with our LZ4 and decode back to the identical payload. This output is never identical to
  the engine's block (§2.1).

Results (executed 2026-10-09; full listing `build/navmeshbin/roundtrip-all.json`):

| set | files | payload identical | file identical | our LZ4 decodes identical | our LZ4 = engine block |
|---|---:|---:|---:|---:|---:|
| RNM1 scene NMG9 | 347 | 347 | 347 | 347 | 0 |
| RNM1 scene NMG8 | 122 | 122 | 122 | 122 | 0 |
| RNM1 scene NMG7 | 16 | 16 | 16 | 16 | 0 |
| prefab NMG9 / NMG8 / NMG7 | 62 / 13 / 40 | all | all | n/a | n/a |
| prefab NMG4 / NMG5 | 5 / 7 | not decoded | | | |

Across the 485 RNM1 blocks, our `fast` encoder output totals 724.8 MB against the engine's 604.6 MB (1.20×).

Structural sweep (`validate(check_geometry=True)` on all 600 decodable payloads, 12,344,961 faces,
`build/navmeshbin/geometry-sweep.json`):

- **600/600:** every face side matches its edge record. No edge is used by more than 2 faces. No
  edge is used by 0 faces. Every face has degree 3 or 4. All reserved edge fields are (-1, -1, -1[, 0]). Meta0 is 2.
  The tail is standard.
- **571/600** have no errors at all.
- **28 files** (castles, towns, villages, hideouts, some multiplayer maps) contain 1–12 shared edges
  traversed in the same direction by both faces: 76 edges in total.
- **1 file** (`Native/SceneObj/mp_siege_map_011_b`) has 115 clockwise faces and 1 non-convex face.

The engine therefore tolerates a few such defects in mission scenes. Both campaign maps have none, and
generated meshes must have none (the validator treats them as errors).

- **Warnings:** unreferenced vertices in 109 files; duplicated vertex pairs (unconnected seams) in 19 files.
- **Largest installed navmesh:** `SandBoxCore/SceneObj/empire_castle_b`, with 188,606 faces.
- **Most islands:** 246, in `mp_siege_map_010`.

## 7. Writer

`build_grid_navmesh(heights, cell_size, face_id_fn, origin=(0, 0), triangulate=False|True|"alternate"|"auto",
version=b"NMG9", meta4=-1, …)`:

- **Vertices:** sample `heights[j][i]` becomes vertex `(origin_x + i·cell, origin_y + j·cell, h)`. Only vertices
  used by some face are emitted, numbered row-major.
- **Faces:** cell `(i, j)` becomes one CCW quad `(v00, v10, v11, v01)`, or two CCW triangles. The face
  gets `meta = (2, 0, face_id, 0, meta4)` and flag 0. `face_id_fn(i, j)` returning `None` leaves a hole.
- **Edges:** edges are created in face order. A new edge takes the direction of the face that creates it. The
  neighbouring face reuses the same index in the opposite direction. Edge records are `(-1, v0, v1, -1, -1, 0)`.
- **Fixture match:** for a 3×2 grid, the face order, edge order and bytes are exactly those of `NavigationFixture.cs`.
  `navigation_fixture_prefab(seed)` reproduces that file (SHA-256 for seed 42
  `d0da0fd6…83a6`, seed 43 `b860e1f9…29f5f`).

Writers: `write_scene_navmesh(mesh, path)` produces RNM1 + LZ4 with scratch fields 0, and
`write_prefab(mesh, path)` produces a u32 length + NMG9. Both write atomically, re-decode before
publishing, and refuse paths inside official game modules or `bin`.

`synth` (CLI) builds from synthetic relief or from an `MNBLHF01` heightfield with a simple id rule:
below sea level is CoastalSea/OpenSea (Water with `--no-naval`), slope above the threshold is Mountain, and everything else is Plain.
Example outputs are under `build/navmeshbin/synth/`.

## 8. Open questions (unknown / untested)

1. Whether the engine loads a `navmesh.bin` with zeroed scratch fields (offsets 32/40). If it does not,
   copy those 16 bytes from an installed file (`Rnm1Container.scratch0/1`).
2. Whether the engine accepts our `fast` or `store` LZ4 blocks. Third-party evidence says a greedy encoder works,
   but this has not been tested with our bytes.
3. The meanings of meta1, meta3, meta4 and the flag byte, and of the 32-entry zero tail. The writer uses the
   campaign-map values (0, 0, -1, 0).
4. Limits on face, vertex and edge counts. The largest installed file has 188,606 faces. The largest synthesized and
   validated file has 250,000 faces (500×500 cells, 14.1 MB RNM1, built in about 8.5 s), but it has not been loaded in game.
5. Whether a regenerated `Main_map`-style navmesh needs anything beyond the scene's `navmesh.bin`. The distance
   cache and the region map come from the models, and battle-scene index data comes from `terrain.bin`.
   Port and settlement placement must sit on the right region.
6. NMG4/NMG5 layout (legacy prefabs only).
