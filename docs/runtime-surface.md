# The runtime surface and map-query backend (MMR-37, 2026-10-15)

R07 selected one runtime surface (`docs/runtime-backend-decision.md`): an owned native scene bundle per world, read into an owned scene, behind an owned `SandBox.MapScene` subclass. This ticket promotes that path into the product backend. It adds no other surface API.
- The MMR-18 diagnostic map scene becomes `GeneratedMapScene`.
- The R02 runtime encoding and installation sequence is reimplemented in `RuntimeWorldBackend`, which now builds worlds from MMR-29's compiled bundles. The R02 probe keeps its own copy as a regression harness.

| | |
|---|---|
| Backend | `src/MNBL.ProceduralWorld/World/RuntimeWorldBackend.cs`: `RuntimeWorldBackend` (Prepare, Install, Construct, ReadCampaignMap), `RuntimeWorld` (a constructed world: queries, ledger, disposal), `EngineThread`, `EngineWork` |
| Map scene | `World/GeneratedMapScene.cs`: `GeneratedMapScene` and `GeneratedMapSceneCreator` (promoted from `CampaignBootstrap/DiagnosticMapScene.cs`) |
| Model | `World/WorldSurface.cs` (pure managed): the surface a bundle defines (layout, scale record, encoder quantization, chunks, heights, normals, collision reference, rays, landmarks) |
| Lifetime | `World/WorldLifetime.cs` (pure managed): `WorldGenerations` / `GenerationToken`, `ResourceLedger`, `SceneNames`, `StaleWorldException` |
| Tests | `tests/World/SurfaceContractTests.cs` via `tools/Test-WorldSurface.ps1` (24 checks on two compiled bundles; `evidence/world/surface-tests.txt`, `evidence/world/surface-contract.json`); `tools/check_terrain_seams.py` (the encoded terrain.bin's chunk seams) |
| In game | the `world_surface` probe (`Automation/WorldSurfaceProbe.cs`: two compiled bundles, no campaign) and the `generated_campaign` probe's `mapSurface` rows; `tools/summarize_world_surface.py` (`evidence/runtime/world-surface.json`) |
| Second world | `f63c6841…`, from the pinned producer at seed 73. Seeds 43–72 were refused by the compiler's quality gate (`quality.unreachable_town`, `unplaceable_town`, `repair_bound_exceeded`); every attempt is recorded in `evidence/world/second-bundle-search.jsonl` |

## Construction

**Cold construction**, from a verified world bundle (`WorldDataBundle`), runs in three steps:
1. `Prepare` (worker thread, no native call):
   - models the surface (`WorldSurface.FromBundle`);
   - encodes `compiled/terrain/heightfield.bin`, `weights.bin` (v2, every material layer) and `flora.bin` (map trees) with the pinned surface encoder (`worldbridge.exe --surface-*`, hash-checked against `worldbridge.sha256`) into a staging job under the module store, using the module's `SurfaceTemplate` (MMR-255: generated from `terrain-materials.json` layerSet);
   - names the encoding by content (`SceneNames.ForWorld`: `mnbl_world_<content id 16>_<12 hex>` over the encoder binary, the template files and the extent);
   - refuses the encoding unless its published quantized heights equal the model byte for byte and its bounds, step, node size and level equal the model's;
   - refuses a superseded generation before starting and again before encoding.
2. `Install` (engine thread):
   - places the five native files in the scene store, `SceneObj/<name>`, checking every copy's hash. If the store already holds that name with identical files, it reuses them. If the files differ, it refuses: scenes are never rewritten.
   - registers the name (`PairSceneNameToModuleName`) and requires `TryGetFullFilePathOfScene` to resolve to that folder;
   - copies the scene into the per-user scene archive, `<store>/world-scenes/<name>` (MMR-282), unless the identical copy is there;
   - sweeps the `mnbl_world_*` folders, archived copies, settlement reports and navigation caches that no world package, save or this process names (MMR-282, `docs/save-compatibility.md`);
   - deletes the staging job, whether the install succeeded or was refused.
3. `Construct` (engine thread): checks that the name resolves to its store folder, reads the owned scene into an owned inspection scene with the physics world on, then optimizes it. On any failure it releases what it created and retires the generation before rethrowing.

**Warm construction** reads an installed owned scene: a reused store entry, or a fixture slot installed before launch.

**Campaign map profile.** `ReadCampaignMap` is the installed `SandBox.MapScene.Load` sequence with the owned read target: scene and agent renderer creation, initialization flags, region map, and `Read`. `GeneratedMapScene.Load` uses it.

**Threading.**
- Native calls run only on the engine thread, which is the application tick thread. Campaign map loading runs on it too, while the module's `OnSubModuleLoad` runs on another thread (run 27772). `EngineThread` binds the tick thread on the first tick, before any world loads, and the backend refuses native work from any other thread.
- CPU work (modelling, encoding, hashing) runs off it.
- `EngineWork` carries a worker's continuation onto the engine thread; it is pumped on every application tick.
- The backend records the thread each world was prepared, installed and constructed on.
- `GeneratedMapScene`'s guarded queries do not require the engine thread: the campaign's own callers may query from its threads, as the installed members allow.

## The surface

`WorldSurface` reads the bundle's compiled terrain and projection.

**Layout.** The native terrain is the projection's square periodic domain: 1600 × 1600 units, with 513 samples at 3.125 units: the shaped surface (MMR-278, `docs/terrain-compiler.md` "Shaping"). Navigation keeps the travel compile's 257 samples at 6.25 units (`navigation-heightfield.bin`), every second surface sample.
- It is 4 × 4 nodes (the chunks) of 128 cells and 400 units each, at heightmap level 7 (MMR-249's go; 64 cells at level 6 before map epic 2).
- Each chunk covers its own 129 × 129 samples, sharing its border samples with its neighbours.
- Chunk bounds, height ranges and material layers are deterministic (MMR-255):
  - the 15 material layers of `terrain-materials.json` layerSet, named in slot order by `weights.bin` v2 (`MNBLWT02`: u32 side, u32 N, N names, N alpha planes); the encoder refuses a template whose layers differ;
  - a chunk carries the base (slot 0, 255 everywhere) and every layer with a nonzero sample in it: its WGHT plane, its bit in `layer_is_used_mask` (the same in all four seasons) and its weightmap level;
  - PHYM is the dominant visible layer (alpha-over from the highest slot, `tools/scenebin/terrain_bin.py dominant_layers`), the same in all four seasons.

**UVs.** The encoder writes the per-node height, normal, weight and physics images, and the engine derives the terrain mesh, its UVs and its collision from the node grid. Nothing in the bundle or the backend supplies UVs. What can be checked is that the geometry, normals and materials the engine builds agree with the model, which the evidence shows.

**Scale record.** Campaign XY stays separate from both source metres and physical terrain coordinates, and the record states all of it:
- logical 800 × 400, wrapping every 800;
- physical = logical + (strip 400, pole margin 600);
- source metres: 0.004 units/m horizontal, 0.005 vertical (0.01 before map epic 2);
- vertical exaggeration 1.25 (2.5 before), sea datum 0.

**Encoding model.** Bounds are floor/ceil at 0.001, with 16-bit round-half-even quantization (`surface.cpp make_grid`), bit for bit. It also equals the compiler's own `height-q16.bin` codes.

**Height model.** The engine's terrain, ground and ray answers are the **collision reference**: the encoded samples truncated toward zero at 0.03 (MMR-16 calibration), interpolated on the main diagonal (x,y)–(x+1,y+1).
- In the MMR-37 runs they match it to 1e-5.
- They are checked against it at 0.001, and against the untruncated quantized height at 0.031 (the truncation bound).

**Normals.** Native normals are checked against the main-diagonal triangle normal of the quantized samples, within 1°. The summary records the worst angle observed. Since MMR-278 the backend encodes with `--surface-normals smoothed`: the normal map is the Sobel of the codes smoothed by [1,4,6,4,1]/16, so on the B-spline surface the 1° check is re-measured by the acceptance run (MMR-247).

A query outside the domain throws: no shortcut returns Calradia's heights or another world's.

**Budgets.** MMR-21 recorded no chunk, LOD, texture or memory budget, so this ticket uses the calibrated, natively validated limits:
- 4 × 4 nodes;
- grid side 513 at most (the encoder takes 129, 257 or 513);
- heightmap level 7 (level 6 at 257);
- 15 material layers (slot 15 free; MMR-249 loaded 15 per node at the frame cap with no layer-count trend in main-thread time);
- map trees: `flora.maxTrees` 88,000, the count MMR-249 measured at +0.24 ms of main thread (MMR-247; it was 200,000).

MMR-56 measures and refines them. 75% of the square is polar padding (MMR-26). A rectangular terrain would need encoder support and is not attempted here.

## Queries and picking

`GeneratedMapScene` re-implements `IMapScene` for the F06 height, normal, bounds, map patch and face height members:
- `GetHeightAtPoint`, `GetTerrainHeightAndNormal` and `GetGroundNormal`;
- `GetMapBorders` and `GetTerrainSize`;
- `GetMapPatchAtPosition` and `GetFaceVertexZ`.

It also hides the inherited `Scene` property behind the same guard. The probe asserts the interface map: all 13 re-implemented members dispatch to `GeneratedMapScene`.

Each member, and `Scene`, first refuses:
- a scene that is not loaded (`Scene` is also allowed while `Load` prepares it), or whose inspection world released its binding;
- a world a later world superseded (`StaleWorldException`);
- a disposed inspection world.

Then it runs the installed query on the owned scene. Installed callers typed `MapScene` reach the base members directly; they read only the owned scene's data.

`RuntimeWorld` exposes the native terrain height (with and without holes), height and normal, normal, ground height under the land and sea masks, bounds, and terrain rays. Each one checks the engine thread, disposal and generation before any native call.

The map screen's native picking bypasses `IMapScene` (`RayCastForClosestEntityOrTerrain`, `SelectEntitiesCollidedWith`). It reaches only the owned scene, which holds only this world's geometry. Seam picking and the camera belong to MMR-66, and battle-return camera fidelity to MMR-60. Face and path queries are `RuntimeWorld`'s navmesh members (MMR-38, `docs/runtime-navigation.md`).

**F06 members not exposed here**, with the reasons `evidence/runtime/surface-coverage-review.json` records for most of them:

| Member | Why it is not exposed |
|---|---|
| `Scene.GetTerrainNodeData` / `GetTerrainHeightData` | Unsupported bulk export: native crash PID 27392, and no shipping callers |
| `Scene.GetGroundHeightAtPosition` and the `RayCastForClosestEntityOrTerrainFixedPhysics` overloads | Mission-only users (spawn, items, siege); no map-scene callers |
| The `RayCastForClosestEntityOrTerrain` overloads that return a `WeakGameEntity` | Entity variants of the same ray; the map screen uses the closest-point variant exercised here |
| `GetSoftBoundaryVertex` / `GetHardBoundaryVertex` | Scene boundary polygon, not terrain; no campaign map-scene callers |
| Face and path members (`GetFaceIndex`, `DoesPathExistBetweenFaces`, `GetPathBetweenAIFaces`) | MMR-17/38's |

## Lifetime

**Generations.** The caller begins a generation (`WorldGenerations.Begin`) for every world it activates: each constructed world, and each campaign map load. Beginning one supersedes all earlier ones. Disposing a world retires its own.
- `Prepare`, `Install` and `Construct` require their token to be current. A world whose completion reaches the engine thread after a later world began is refused before anything of it is installed or registered.
- Queries refuse a superseded world.
- A retired world that is not superseded may still be queried by its own engine teardown, which happens before the next world begins.

**Ordering rule.** No generation may begin while a campaign map is live, from its `Load` to its `Destroy`, because a live map refuses every query once superseded. `Begin` enforces this. Preparing a world in the background does not begin a generation.

**Ownership.** Each `RuntimeWorld` keeps a `ResourceLedger` of what it holds:
- For an inspection world, it releases the scene, the agent renderer, the screen's camera and scene layer, and the map scene binding.
- It hands the scene store entry to the scene store and the name pairing to the engine's scene index, which has no unregister API; the name always pairs to the same folder.

`Dispose` releases latest first, each once. A failed release is recorded without skipping the rest. A resource another owner releases is recorded as handed to that owner, not as released. Disposal then retires the generation and drops the scene reference.

For a campaign map, the ledger hands the scene to the installed `MapScreen.OnFinalize` (`Scene.ClearAll`) and the agent renderer to the installed `MapScene.Destroy`. Neither release is observed by the backend.

**Disk.**
- Staging jobs are deleted once installed or refused.
- The scene store keeps one folder per world encoding, never rewritten.
- A sweep removes only the store folders that no world package, save or this process names, and nothing while a save or package cannot be read (MMR-282; before, it removed every folder this process had not used). It touches only the content-addressed form `mnbl_world_<16 hex>_<12 hex>`, never a fixture slot such as `mnbl_world_a` or `mnbl_generated_*`. A load whose folder is gone copies it back from the scene archive, or re-encodes it from its bundle under the same name.
- Loaded folders are file-locked and are not removed in the process that read them.

**World switch.** Disposing a world and constructing another in the same process is this ticket's; the evidence below covers it. The campaign keeps the installed sequence: `EndGame`, then the next world binds everything in its own `Load`. The previous map scene's queries and native scene access are then refused.

## Evidence

**Offline (24/24)** on the real size-129 world `f596b58c…` and the second compiled world `f63c6841…`. For each world:
- the model against the bundle and its projection;
- the pinned encoder's heights equal to the model for all 66,049 samples;
- the encoded scene's 16 node ranges and layer masks;
- the encoded terrain.bin: the 1,569 shared chunk-edge samples have equal height, normal and material weight in every layer, and every decoded height is the model's;
- the periodic seam columns are equal, and the compiler's own 16-bit heights carry the model's codes;
- 70 landmarks and the model queries.

Across both worlds:
- the two worlds differ at 69 of A's 70 landmark positions;
- generation tokens and late callbacks;
- the resource ledger;
- content-addressed scene names;
- the campaign map ordering rule.

**In game, `world_surface` (two compiled worlds, no campaign).** The sequence is A, a superseded late world C, B, then A again (warm, reusing its installed scene). For each world:
- bounds, and the map scene's borders and size read from the native scene;
- 70 landmarks: 24 compiler settlements, 25 chunk corners and centres, 9 grid points and 12 coast samples. At each one:
  - heights equal the collision reference;
  - normals are within 1° of the model's;
  - Scene and SceneView rays agree;
  - the `GeneratedMapScene` answers equal the native ones;
- 36 chunk-border points with no height or normal tear;
- 12 seam pairs;
- a native selection hit on the terrain.

Each later world answers at the previous world's landmark positions with its own heights. C's completion, delivered through `EngineWork` after B began, was refused at install and construct, with nothing installed or registered.

Every disposal leaves nothing outstanding, refuses all 14 queries through the disposed world and its binding, and the objects are collected. The staging jobs are deleted, and the scene store holds exactly the worlds' scenes.

**In a campaign** (the `generated_campaign` probe's `mapSurface` rows), for each fixture world:
- the generation is current;
- the guarded ground answers equal max(native terrain, sea datum 0): the water surface above underwater terrain;
- height-and-normal equals the native terrain.

After the in-process world switch, the first world's map scene refuses queries and native scene access. Its world was disposed, its scene and renderer were handed to the installed owners, and the probe holds the object on purpose, as a stale reference would be held.

## Limits and handoffs

- **Activation (MMR-45).** A cold-constructed compiled world is not booted as a campaign here. Since MMR-38 its `navmesh.bin` is the campaign navmesh built from the travel cells (`docs/runtime-navigation.md`), which replaces the encoder's 33 × 33 diagnostic grid. The scene has no settlement or border entities: the entities are MMR-40's, the caches MMR-41's, and activation MMR-45's.
- **Native release.** What `Scene.ClearAll` and the renderer release do natively, and when the engine reclaims the memory, is recorded as called, not measured. Cold-load restore is MMR-49's.
- **Seam behaviour.** Water at the sea datum belongs to MMR-39, and camera, labels and picking at the periodic seam to MMR-66.
- **Map patches.** The battle-index map is MMR-18's `GeneratedBattleTerrain`. Mission scene choice from the generated terrain belongs to MMR-42.
