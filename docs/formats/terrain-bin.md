# Bannerlord `terrain.bin` format (ZGR4 / ZGR5 / ZGR6RTRN)

Status: reverse-engineered from the installed files of Bannerlord 1.4.8.119303 and War Sails
(NavalDLC) 1.2.8.119303. Reference implementation: `tools/scenebin/terrain_bin.py`.
Tests: `tests/test_terrain_bin.py`. Generated ZGR6 scene fixtures have since rendered
in shipping PID 24860 and reached a paused Sandbox in PID 23152; see
`docs/runtime-capabilities.md` and their retained runtime results. This narrow
evidence does not validate every format variant, native query or in-process
replacement (see [Open questions](#open-questions)).

Legend used below:

- **Verified** – checked by code over every installed file (487 files) unless a smaller sample
  is stated.
- **Inferred** – consistent with the data but not proven, or proven only on a sample.
- **Unknown** – meaning not established; the writer uses the value stated.

All integers are little-endian unless stated otherwise. PNG content is big-endian, as the
PNG standard requires.

## 1. Inventory

| | |
|---|---|
| Files | 487 `Modules/*/SceneObj/*/terrain.bin` (Native 68, NavalDLC 81, SandBox 132, SandBoxCore 202, StoryMode 4), 3.70 GB total |
| Container versions | `ZGR6RTRN` 428, `ZGR5` 35, `ZGR4` 24 |
| Grid sizes | 1x1 up to 16x16 nodes (no file exceeds 16 per side; 33,194 nodes in total) |
| Node map resolutions | R = 2^L + 1 with L = 2..10 (R = 5, 9, 17, 33, 65, 129, 257, 513, 1025); 185 files use only 129, 185 only 257, the rest are mixed |
| Paired `scene.xscene` | every folder has one. `<terrain node_dimension_x/y node_size min_height max_height ...>` + `<layers version="1">` + `<nodes>` |

The `<nodes>` children of `scene.xscene` (one per terrain node) carry `idx`, `idy`,
`layer_is_used_mask_{summer,fall,winter,spring}`, `height_complexity`, `min_height`,
`max_height` and `<texture_levels>` (`heightmap_level`, `min_heightmap_level`,
`normalmap_level`, `weightmap_levels` for layers 0..15).

Cross-checks with `scene.xscene` (all verified on every file):

- The node grid recovered from `terrain.bin` equals `node_dimension_x` x `node_dimension_y`
  (487/487).
- Every node's height PNG width equals `2^heightmap_level + 1` (33,194/33,194 nodes).
- A node's set of present weight slots equals the OR of its four `layer_is_used_mask_*`
  values in every node of the 485 files that have weights.
- terrain.bin does **not** contain `min_height`/`max_height`, `node_size`, or the grid
  dimensions (except implicitly, see below). A writer must keep `scene.xscene` consistent.

## 2. Compressed blob (shared by all versions)

Every compressed stream is a *blob*: a 40-byte header followed by one raw **LZ4 block**
(LZ4 block format, without the frame format).

| Off | Type | Field | Notes |
|---|---|---|---|
| 0 | u32 | codec | 3 in every blob (verified). Decodes as an LZ4 block. |
| 4 | u32 | pad | 0 in every blob (verified). |
| 8 | u64 | usize | decompressed size (verified: our decoder output length matches in every blob) |
| 16 | u64 | csize | stored size **including** this 40-byte header (verified) |
| 24 | u64 | reserved | **uninitialised memory**: 454 distinct values across the install, all-zero in 144 files (verified). Ignored by the writer (writes 0). |
| 32 | u64 | stamp | **uninitialised memory**, usually a stack-like address `0x00007ffX...` (431 distinct values; `0x8` in `flora_editor_scene`). Writer writes 0. |
| 40 | u8[csize-40] | payload | LZ4 block |

Because `reserved`/`stamp` vary arbitrarily between files the engine loads, the engine cannot be
validating them (inferred). The reader keeps them so the round trip is byte-exact.

The "unusual IHDR" bytes seen in a hex dump are not a TaleWorlds PNG variant: the PNGs
sit inside LZ4-compressed data, so LZ4 tokens and match offsets are interleaved with PNG
bytes on disk. After LZ4 decompression every embedded stream is a standard PNG (decoded by
both Pillow and our own stdlib decoder).

## 3. ZGR6RTRN (current format, 428 files)

### 3.1 Header (0x70 bytes)

| Off | Type | Field | Notes |
|---|---|---|---|
| 0x00 | char[4] | `ZGR6` | magic |
| 0x04 | char[4] | `RTRN` | second magic (verified constant) |
| 0x08 | u32 | 2 | constant in all 428 files. Meaning unknown (header/format version?). Writer: 2 |
| 0x0C | entry[5] | section directory | 20 bytes each, in this fixed order: `MIDX`, `HGHT`, `NRML`, `WGHT`, `PHYM` (verified) |

Directory entry (20 bytes):

| Off | Type | Field | Notes |
|---|---|---|---|
| +0 | char[4] | tag | |
| +4 | u32 | version | 1 in every entry (verified). Writer: 1 |
| +8 | u32 | offset | absolute file offset of the section |
| +12 | u32 | csize | blob size (= blob header `csize`; excludes the node table) |
| +16 | u32 | usize | decompressed size (= blob header `usize`) |

### 3.2 Section layout

Sections are stored contiguously in directory order, starting at 0x70 (verified; the
serializer recomputes every offset and reproduces all 428 files byte-for-byte).

- `MIDX`: just one blob (`csize` bytes).
- `HGHT`, `NRML`, `WGHT`, `PHYM`: `u32 node_offset[N]` followed by one blob.
  `N = node_dimension_x * node_dimension_y`. `node_offset[i]` is the byte offset of node
  `i`'s chunk inside the **decompressed** data. Chunk `i` runs to `node_offset[i+1]`, or to
  `usize` for the last node. `node_offset[0] = 0`.
- Empty section: `csize = usize = 0`, offset = current file position. There is no table and
  no blob. Used for `NRML` and `WGHT` in both campaign `Main_map` files only (SandBox and
  NavalDLC). Those scenes texture through a Granite tileset (`vista_tileset="WorldMap"`,
  `terrain_render_version="1"`).
- The file ends right after the last section. No trailer, padding or checksum (verified).

N is not stored explicitly. A reader can derive it as `(next_offset - offset - csize) / 4`.

### 3.3 Node order and grid geometry

- Node index `i = idy * node_dimension_x + idx` (x fastest). **Verified**: 57,505 of 57,542
  same-resolution shared edges are bit-identical under this order. The 37 exceptions are
  editing artefacts in 7 files, mostly 1–12 codes on a few samples. The grid also matches
  the `MIDX` image size in 428/428 files, and node extremes match the xscene per-node values.
- Every node map is R x R samples, R = 2^L + 1, row-major (row 0 first), **column = +X,
  row = +Y**. Adjacent nodes share their border row/column, so the stitched grid is
  `(nx*(R-1)+1) x (ny*(R-1)+1)`.
- World position of node `(idx, idy)` sample `(row r, col c)`:
  `x = (idx*(R-1) + c) * node_size/(R-1)`, `y = (idy*(R-1) + r) * node_size/(R-1)`, origin at
  the terrain corner (0, 0). **Verified** with scene entities: in `battle_terrain_001`, 59 of
  387 top-level entities sit within 5 cm of the decoded surface under this mapping, versus 3
  with Y flipped and 1 with X/Y swapped. `Main_map`: 27 vs 5 vs 4.
- Nodes in one file may have different R (mixed LOD levels). 1,554 adjacent pairs differ in
  resolution. The reference decoder bilinearly upsamples lower-resolution nodes when it
  stitches them (an approximation; the engine's own handling is unknown).

### 3.4 Section contents (after LZ4 decompression)

`lenpng` below means `u32 length` followed by `length` bytes of a PNG file.

**HGHT node chunk** = `lenpng` with an R x R, **16-bit greyscale** PNG (colour type 0, bit depth 16).

**Height encoding (verified):**

```
height_m = min_height + v * (max_height - min_height) / 65535
```

Here `v` is the 16-bit sample and `min_height`/`max_height` are the `scene.xscene`
`<terrain>` attributes; the encoding is global, not per node. Evidence:

- In both `Main_map` files and `mp_skirmish_map_008_skin` the decoded data spans exactly
  `[min_height, max_height]`: the raw samples reach 65535 (and 0). The divisor is therefore
  65535, not 65536.
- 327 files reach v = 0 and 231 reach v = 65535. The editor sets min/max to the data range,
  except in 45 files that keep the default -100/500.
- Per-node decoded extremes match the xscene node `min_height`/`max_height` with a median
  error of 0.05 m. Those xscene statistics are sometimes stale (outliers up to hundreds of
  metres), so they are not authoritative.
- Writer quantisation: `v = round((h - min)/(max - min) * 65535)`, clamped to 0..65535.
  Maximum error = half a step = (max - min)/131070.

**NRML node chunk** = `lenpng` with an R x R, **8-bit RGBA** PNG (colour type 6).

- R = x, G = y components of the world-space unit normal. Each channel is
  `floor(n * 127.5 + 127.5)`: flat ground stores (127, 127), which rounding would store
  as 128. z is implied: `sqrt(1 - x² - y²)`.
- B = 63 and A = 0 in every texel of every file (verified on 485 files). Their meaning is
  unknown; the writer writes 63/0.
- Normals are computed on the stitched grid: 98–99% of shared-edge normals are identical
  across nodes in the files checked.
- **Inferred, not exact**: the gradient kernel. Sobel with node_size/(R-1) spacing and
  floor encoding reproduces 63.4% of `battle_terrain_001` texels exactly and 90% within one
  code. The decoded slope agrees with the height gradient (steep-sample slope ratio 0.98).
  The editor probably derives normals from higher-precision data. The writer uses Sobel on
  the quantised stitched grid, with correctly scaled one-sided differences at the outer border.

**WGHT node chunk** = 16 slots, one per terrain layer index 0..15 (= xscene `<layers>` order):

```
repeat 16: u32 present (0 or 1)       # only 0/1 observed
           if present: lenpng         # R x R, 8-bit greyscale (colour type 0)
```

- Slot 0 (the first layer) is present and all 255 in every node checked: it is the base layer.
- Present slots == OR of the node's four `layer_is_used_mask_*` (verified, see section 1).
- Blending semantics (inferred from PHYM, below): layers are composited bottom-up with the
  weight as opacity (layer k over layers < k).

**PHYM node chunk** = 4 seasonal maps:

```
repeat 4: u32 season (0 summer, 1 fall, 2 winter, 3 spring)   # always 0,1,2,3 in order
          u32 count (= R*R)
          u8  layer[count]                                    # X-MAJOR: index = col*R + row
```

- Values are **terrain layer indices**, not physics-material ids. Every file's maximum
  value is below its layer count (487/487), and the values follow the weight maps. The
  engine presumably maps the index to the layer's seasonal `physics_material`
  (inferred).
- **Orientation verified**: PHYM is transposed relative to the PNG maps (x-major). The
  correlation test against the weight maps picks x-major in 32/32 sampled files that had
  non-base weights (8 more had none).
- Rule (inferred): per sample, the layer with the largest visible contribution
  `w_k * prod_{j>k}(1 - w_j)` wins; ties go to the higher layer. Agreement with official
  data: 99.8% (`battle_terrain_001`), 98.6% (`mp_siege_map_010`), 96.8%
  (`vlandia_village_b`). Agreement is 99.2–100% for `battle_terrain_030` and
  `battle_terrain_coastal_04` when `is_flora_layer` layers and disabled seasonal layers are
  excluded. Excluding flora layers breaks `battle_terrain_001`, so the exact eligibility rule
  is **unresolved**. The writer uses all supplied layers unless `physics_layers=` is given.
- Seasons differ when a layer is disabled for a season (e.g. `battle_terrain_001` winter has
  no layer 1).

**MIDX** (global, not per node) = 4 × `lenpng`, each an 8-bit greyscale PNG of
`(nx*256) x (ny*256)` pixels (256 px per node regardless of R; verified 428/428 ZGR6 and
35/35 ZGR5).

- **Meaning unknown.** The four images look seasonal (summer, fall, winter, spring); in
  `Main_map` the summer and winter images are identical. 238 of 463 files are all 255
  everywhere. In 398 of 428 ZGR6 files the first image is all 255. Values seen: up to 109
  (not layer indices, not physics-material ids, not `terrain_materials.xml` indices). In
  `Main_map` they form large regional classes (values 0, 3, 17, 22, 34, 52, 53).
- Writer: four all-255 images, the most common pattern; `battle_terrain_001` uses it with
  full NRML/WGHT.

### 3.5 Embedded PNG details

Official PNGs (sampled: 388 PNGs from 5 files of all three versions, all chunk CRCs valid):

- Chunk order: `IHDR`, `pHYs` (2835, 2835, 1 = 72.009 dpi), one or more `IDAT`, `IEND`.
- Non-interlaced, libpng-style adaptive filters (all five filter types occur).
- zlib headers vary (`789c`, `5885`, `388d`, `6881`, `4889`): window size is reduced for
  small images, as libpng does.

The writer emits `IHDR`, `pHYs` (2835, 2835, 1), a single `IDAT` (zlib level 9; filter Up for
greyscale, None for RGBA) and `IEND`, with correct CRCs. This is a standard PNG; whether the
engine's decoder needs anything more is untested in game.

## 4. ZGR4 and ZGR5 (legacy, read-only support)

| Off | Type | Field |
|---|---|---|
| 0x000 | char[4] | `ZGR4` or `ZGR5` |
| 0x004 | u64[256] | node table, **x-major**: entry `idx*16 + idy` = offset of that node's record inside the decompressed blob. Unused entries are 0; node (0,0) can also be 0 |
| 0x804 | u32 | blob size (= blob `csize`) |
| 0x808 | blob | one blob for the whole terrain |

- The 16x16 table caps these versions at 16 nodes per side (inferred from the layout).
  Records are stored with idy fastest (x-major), verified on all 59 files. The grid is the
  bounding box of the used entries and equals the xscene grid in 59/59 files.
- ZGR5 only: the decompressed data starts with `u32 midx_header` (2 in 25 files, 1 in 10
  files; meaning unknown), then the same 4 MIDX `lenpng` images. Node records follow at
  the table offsets.
- Node record (both versions):

  ```
  PHYM   4 x (u32 season, u32 count=R*R, u8[count])     # x-major
  AUX8   u32 count=R*R, u8[count]                        # unknown; dropped in ZGR6
  HGHT   lenpng (16-bit grey)
  WGHT   16 slots as in ZGR6
  NRML   lenpng (RGBA, B=63, A=0)
  ```

  AUX8 is mostly 255; it is all 0 in 7 files, and others have minima of 28–161.
  Possibly baked occlusion. Unknown.
- There is no trailing data after the blob (59/59).

## 5. Engine-side observations (native DLL, read-only)

`bin/Win64_Shipping_Client/TaleWorlds.Native.dll` contains:

- The magics `ZGR6`, `ZGR5` and `ZGR2` as code immediates.
- The strings `/terrain.bin`, "Unable to decompressed, corrupted terrain file!" and
  "Terrain node count: %d".
- `terrain_node_heightmap_`, `terrain_node_normalmap_` and
  `#define NodeHeightMap (Texture2D_table[indices.t_terrain_%d])`. The per-node maps
  probably become GPU textures.

The section tags are not present as literal strings in either byte order. No checksum or CRC
over the container exists in the format: every header field is explained except the two
garbage u64s. The only integrity checks we can infer are LZ4 decode failure and the PNG
chunk CRCs, which the writer computes correctly.

## 6. Writer (`build_terrain`) – what is written and what is guessed

| Item | Value written | Basis |
|---|---|---|
| Container | `ZGR6RTRN`, sections MIDX/HGHT/NRML/WGHT/PHYM, contiguous | verified layout |
| `u32` after `RTRN` | 2 | constant in all files (meaning unknown) |
| section version | 1 | constant |
| blob codec/pad | 3 / 0 | constant |
| blob reserved/stamp | 0 / 0 | **guess** (official values are garbage) |
| LZ4 | own greedy compressor, standard block rules (last 5 bytes literal, last match ≥ 12 bytes from end); `compress=False` writes a literal-only block | verified by own decoder only |
| Heights | 16-bit grey PNG, global min/max encoding, shared node edges identical | verified encoding |
| Normals | Sobel on the stitched grid, floor encoding, B=63, A=0 | approximate kernel (**inferred**) |
| Weights | layer 0 = 255 everywhere unless given; slots that are all zero in a node are omitted | verified structure |
| Physics | dominant visible layer, x-major, same map for all 4 seasons unless given | **inferred** rule |
| MIDX | 4 × all-255 L8 images of (nx·256)×(ny·256) | **guess** (most common official pattern) |
| xscene `<nodes>` (`nodes_xml`) | heightmap/normalmap/weightmap levels = L, masks = present slots, min/max from data, `height_complexity` = mean slope clamped to 1 | `height_complexity` is a **guess** (its official meaning is unknown; correlation with simple roughness metrics ≤ 0.71) |

Constraints enforced by the builder: square nodes, cells per node a power of two ≥ 4, finite
heights. `min_height`/`max_height` default to the data range snapped outward to multiples of
0.001 (official xscene files print 3 decimals). The returned `info` values must be copied
verbatim into `scene.xscene` `<terrain>`, together with matching `node_dimension_x/y` and
`node_size`; otherwise every decoded height is rescaled.

## 7. Validation performed (all executed, see `build/terrainbin/` for scratch outputs)

- (a) parse → serialize is **byte-identical for 487/487 installed files** (0 failed). Every
  blob decompresses to exactly `usize`, and every node chunk parses with no leftover bytes.
- (b) Decoded heights lie within `[min_height, max_height]` for 487/487 files (worst
  excursion 5.7e-14 m, float rounding). This is guaranteed by the formula. The stronger
  checks are the exact-endpoint test on `Main_map` and the per-node xscene agreement
  (median 0.05 m).
- (c) Previews: `build/terrainbin/main_map_h16.png` (4097x4097, 16-bit),
  `main_map_h8_1024.png`, `battle_terrain_001_h16.png` / `_h8.png` (321x513), drawn with
  north up. `Main_map` shows the recognisable Calradia relief with carved river channels and
  no seams across the 16x16 node grid. `battle_terrain_001` shows a smooth central basin
  with higher rims and no seams across 5x8 nodes.
- (d) Analytic synthesis: 4x3 nodes x 64 cells, node_size 100, range -10..110.
  - Maximum height error 0.00091554 m against a half-step bound of 0.00091554 m.
  - Normal component error ≤ 0.0048 (0.41°) in the interior and ≤ 0.024 (1.41°) on the
    outer border.
  - Round trip byte-identical.
  - A Main_map-scale build (16x16 nodes x 256 cells, 4097² samples) takes 28 s and
    produces an 8.9 MB file that round-trips.
- (e) `python tests/test_terrain_bin.py`: 18 tests, all pass in about 13 s (installed-file
  tests skip if the game is absent).

## Open questions

1. **In-game acceptance is untested.** Unproven: that the engine loads a synthesized file
   through an owned, pre-indexed scene slot, and that `GetTerrainHeight` returns the
   encoded heights. Also unproven: that rewriting an indexed scene's terrain.bin is picked
   up without restart (this is proven only for nav prefabs).
2. MIDX meaning, and whether non-255 values are needed for anything we care about
   (rendering, seasons, the tileset path).
3. Whether a non-tileset scene may leave NRML/WGHT empty. Only the tileset-based `Main_map`
   files do this.
4. Exact normal kernel and PHYM layer-eligibility rule (both only affect shading and
   footstep/physics materials, not geometry).
5. Engine limit on grid size above 16x16 nodes for ZGR6. No installed file exceeds 16, and
   the legacy tables are 16x16.
6. Whether xscene per-node `min_height`/`max_height`/`height_complexity` matter at runtime
   (culling/LOD). Official files contain stale values, which suggests tolerance.
7. Meaning of the ZGR6 header `u32 2`, the ZGR5 `midx_header`, the AUX8 map, and NRML B/A.
8. Mixed-resolution neighbours: how the engine stitches nodes of different R (we upsample
   bilinearly for previews only).
