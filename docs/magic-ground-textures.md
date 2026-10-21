# Magic-ground textures (MMR-259, vein stamps MMR-288; 2026-10-20)

Magic-mutated ground is drawn by one runtime decal overlay (MMR-249 delivery A; consumers MMR-260 and MMR-288's vein placement). This file covers what the overlay draws: one published decal material per visible magic school, each with a stamp atlas whose falloff is baked into its alpha. Every colour and surface texel is derived from installed vanilla textures (owner decision 4), with the colour direction from FWGC++. Research: `docs/research/map-epic-2-findings.md` sections 6 and 13.

**MMR-288 (vein stamps).** The first in-game run (acc4) showed magic ground as large splotches. The owner asked for veins fanning out, corruption spreading through the land. The atlases are re-derived as vein stamp kits (contract C1 in the epic's revision 5): same materials, names, sizes, layout and import settings, so the publish is a re-import of the 24 textures with no material change. Evidence: `evidence/art/magic-veins.json` (gates, lineage, per-cell numbers); `evidence/art/magic-textures.json` is now the slim record (contract, vanilla inputs, output hashes).

**Status.** The re-derived sources pass the gates. **The re-import is not done yet**: the installed `AssetPackages/pack0.tpac` still holds the MMR-259 splotch atlases until one Modding Kit session re-imports the 24 textures (see "Publishing").

## What the overlay uses

**Materials.** `mnbl_magic_weave`, `mnbl_magic_umbral`, `mnbl_magic_infernal`, `mnbl_magic_radiant`, `mnbl_magic_fire`, `mnbl_magic_water`, `mnbl_magic_earth`, `mnbl_magic_air`. Each is a clone of vanilla `mainmap_decal_undervillage` (shader `deferred_decal`; flags `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain`, `use_normalmap`, plus `use_specular`).

**Hidden schools** reuse a visible material, per `terrain-materials.json` `mutation.hiddenLooks`: blood uses infernal, void umbral, rot water, eldritch weave. No new atlases.

**Textures** (all 2048 x 2048; sources in `Modules/MNBL.ProceduralWorld/AssetSources/magic_ground/`):

| Texture | Content | Import as (as published) |
|---|---|---|
| `mnbl_magic_<school>_d` | RGBA: colour, and the stamp's falloff in alpha. Colour runs to every cell edge, so mips never fringe dark | Albedo_HQ (BC7), `has_alpha`, `dont_resize_in_atlas` |
| `mnbl_magic_<school>_n` | RGB tangent normal, DirectX style like the vanilla terrain normals (B holds z) | Normal (BC5), `is_bumpmap` |
| `mnbl_magic_<school>_s` | R specular, G gloss, B occlusion (the vanilla terrain convention) | Specular (DXT1), `is_specularmap` |

**Atlas layout.** 4 x 4 cells of 512 px. Each cell has a 16 px margin where alpha is 0, so no cell bleeds into another down to mip 4.

| | Column 0 | Column 1 | Column 2 | Column 3 |
|---|---|---|---|---|
| Row 0 **arid**: desert | trunk | fork | tip | source burst |
| Row 1 **verdant**: grassland, forest, rainforest, marsh | trunk | fork | tip | source burst |
| Row 2 **rock**: exposed_rock | trunk | fork | tip | source burst |
| Row 3 **cold**: boreal_forest, cold_tundra, land_ice | trunk | fork | tip | source burst |

- **Row** from the cell's FWGC++ core: `terrain.magical_biomes[biome_variant].core` (MMR-260).
- **The decal argument** is `SetVectorArgument(0.25, 0.25, column / 4, row / 4)`, with row 0 the top of the image (MMR-260's `flipV` policy; MMR-288 adds `flipU`). MMR-247's screenshots settle both directions.

## The vein stamps (contract C1)

**Frame.** Inside the margin u runs left to right (`u = (x - 16) / 480`), v top to bottom. Veins grow toward +u (the source side is u = 0). Placement scales a stamp non-uniformly: box length L along the branch (u), width W across (v). The box spans the **whole** 512 px cell, margin included, so u 0..1 is 0.9375 of L.

| Kind | Shape |
|---|---|
| Trunk (column 0) | One vein along v = 0.5 from u = 0 to 1. Core about 1/12 of the inner height (40 texels, never thinner; up to +10 % wider), a broken rim, sparse hairline capillaries, the vanilla crack network around it, and a low-alpha corruption halo (alpha ≤ 0.32) reaching about 1/4 of the height. Meander ≤ 1/16 of the height, pinned to v = 0.5 at u = 0 and u = 1 |
| Fork (column 1) | The trunk's own line enters at (0, 0.5) and splits at u = 0.4 into two mirrored children, 0.7 x the parent's width, leaving at (1, 0.5 ± 0.3). Each child runs straight over u 0.75..1 with heading dv/du = ±0.4, so in the world a child leaves at yaw ±atan(0.4 W / L). At u = 0.9 the children are at v = 0.5 ± 0.26 |
| Tip (column 2) | Enters on the trunk's line at trunk width, tapers to a hairline at u = 0.85 and ends in a mirrored capillary splay |
| Source burst (column 3) | A stained, brighter core of radius 0.12 at the centre (cracked by the vanilla crack network) and 8 rays at about 0, ±42, ±94, ±140 and 180 degrees from +u, tapering from 1.05 to 0.3 of the core width, ending at radius 0.47 |

Every stamp is (near) symmetric about v = 0.5: the fork's children and the burst's rays are exact mirrors; the trunk's meander, the capillaries and the material detail are not.

**Chaining.** Stamps chained along a branch sit 0.75 L apart (384 texels):
- **Joint rule.** A stamp's head anchor (u 0.1, v 0.5) sits on the previous stamp's tail anchor (u 0.9): centres 0.375 L + 0.375 L' apart, each along its own heading. A turn pivots at the shared anchor.
- **Overlap.** The next stamp's head (u 0..0.2) lies over the previous stamp's tail (u 0.8..1).
- **Trunks repeat.** A trunk's texels at u 0.8..1 are its texels at u 0..0.2 (the content is periodic over 384 texels), and fork and tip stamps start with the trunk's head. So a chain trunk, trunk, ..., fork or tip is texel-identical across every joint.
- **Feather.** Alpha is feathered in optical depth, `alpha' = 1 - (1 - alpha)^f`, with f a smoothstep from 0 to 1 over the head (u 0..0.2) and from 1 to 0 over the tail (u 0.8..1; trunk and fork children). Across a joint the two weights sum to 1, so the two stamps composite to exactly one stamp's alpha: no seam, no doubled halo. (The contract said "about the last 12 %"; the feather spans the whole 20 % overlap because that is what makes the sum exact. Default taken.)
- **Width.** The vein's world width scales with W. A fork's children are 0.7 x its core: the next stamp on a child should have W' = 0.7 W.

**Readability** at 37 screen px per unit (camera distance 38): the trunk core is at least 40 texels, 3.5 screen px in a box 1.2 units wide (14.5 px at 5 units). A fork child is 28 texels: 2.4 px in a 1.2-unit fork box, 3 px from W 1.5 up. Tips taper to hairlines by design. Hairline capillaries are 2-4 texels: about 1 screen px or less in all but the widest boxes, so they are surface detail, not structure. In a box 1.2 units wide the engine samples about mip 3 (480 texels over about 44 screen px).

## Looks

Each stamp has two layers of look:
- **The halo** (corrupted ground) is MMR-259's recipe for the school and row (`RECIPES`), with a per-school stain layer over it. It shows only at the halo's low alpha.
- **The vein** is painted by `VEINS[school]`: rim, capillaries, core, axis highlight, glints, the source node and the vanilla crack detail.

| School | Vein |
|---|---|
| fire | Glowing ember cracks in scorched ground: hot orange-yellow core over a red ember edge, char rim, ember capillaries, scorched cracks |
| infernal | Molten crimson cracks with ash: crimson core, black ash rim with pale ash flecks, crimson capillaries, ash-grey cracks |
| weave | Pearl and opal glass threads: opal core with pearl patches and a white highlight, pale pearl rim, thin-film glass threads (palette from the approved weave studies) |
| radiant | White-gold crystalline light seams: gold crystal core, white-gold axis and sparkles, cream rim, straight angular capillaries |
| umbral | Ink-black veins with violet rims; smooth ink tendrils; a violet-lit source |
| earth | Deep fissures with amber and geode glints: near-black fissure core with amber and violet flecks, raised loam lips, root-like cracks, an amber geode source |
| water | Brine-teal crystal frost veins: teal ice core, aqua axis, white frost rim, angular frost capillaries |
| air | Pale wind-scoured streaks with silver filaments: pale cyan-grey scoured core, silver axis, broken silver hairlines alongside, near-parallel filaments, wind streaks around |

**What derives from where.**
- **Colour and surface.** Vanilla textures recoloured to the recipe's colour, their luminance structure kept: ashes, ground soil, ice, snow, desert floor, plus the row's parent layer (structure, height, normals, specular). Flat paints (`color`) and glows (`glow`) are modulated by the parent's own detail, as in MMR-259.
- **Crack detail.** The crack network of the vanilla cracked-mud decal (`mainmap_cracked_mud_a`, texture `decal_b_d`), placed within the halo's reach and inside the burst's node.
- **Skeleton.** Procedural (`vein_skeleton`): periodic sine noise for the trunk line, cubic Hermite fork children, mirrored burst rays, random-walk capillaries, seeded by `sha256(school/row)`. It only places the vanilla-derived materials.
- **Codex was not used.** No generated imagery fed any texture or skeleton.
- Weave's palette match against the approved concept studies is recorded (`conceptMatch`), not gated.

## Gates

`python tools/art/magic_ground.py --modules "<game>/Modules" --ext build/art/ext --out Modules/MNBL.ProceduralWorld/AssetSources/magic_ground --review build/art/review --evidence evidence/art/magic-veins.json --inputs-evidence evidence/art/magic-textures.json` decodes the vanilla inputs, derives every atlas, gates it, and writes the sources, the evidence and review images. It takes about 4 minutes. The output is deterministic: two runs are byte-identical.

| Gate | Rule (MMR-288 adaptation of MMR-259's gates) | Result (2026-10-20) |
|---|---|---|
| Macro ΔE | CIEDE2000 of the vein core (core and source-node masks ≥ 0.5; linear-light mean of the stamp over its parent) against the parent, every layer of the row's biomes and its slope layer: ≥ 10 | All 128 cells; minimum 10.47 (radiant arid tip) |
| Vanilla envelope | Footprint (stamp over parent, weighted by the halo's reach): luminance mean, saturation. Vein core: surface detail (luminance minus a 3 px blur), specular, gloss mean and p99, normal xy RMS. Bounds: the shared layer set, the recipe sources and, for the colour keys, the vanilla world-map decals (alpha-weighted; the burnt-village decal sets the dark end, 69.5). The core's raw luminance spread is recorded, not gated: an ink or ember line is meant to stand out | All cells inside |
| Seams | Alpha 0 in each cell's 16 px margin. Chains trunk ×3 → fork and trunk ×3 → tip at 384 texels against the ideal (the same stamps unfeathered, each over the last): axis coverage ≥ 0.95; per joint, alpha MAE ≤ 0.01 and axis colour ΔE ≤ 2 into a trunk, ≤ 0.03 and ≤ 6 into a fork or tip (their heads fade from the trunk's own) | All 64 chains: coverage ≥ 0.992; into a trunk alpha MAE ≤ 0.0007, ΔE ≤ 0.43; into a fork or tip ≤ 0.017, ≤ 4.64 |
| V symmetry | Mean \|A − flipV(A)\| / mean A of the alpha blurred by 6 px ≤ 0.25; trunk meander ≤ 1/16 of the height (30 texels) | Maximum 0.237 (trunks; forks 0.136, bursts 0.122); meander ≤ 16.9 texels |
| Readability | Trunk core ≥ 3 screen px in a 1.2-unit box at 37 px per unit | Trunk core ≥ 40 texels (3.5 px); fork children 28-35 texels (2.4 px at W 1.2) and tips are recorded, not gated |
| BCn round-trip | DXT5 diffuse error, weighted by alpha (where the decal shows), ≤ the worst vanilla world-map decal's own round-trip weighted the same way (5.78). Alpha ≤ 3. BC5 normal and DXT1 specular ≤ 1.25 × the parents in the same layout. Hairlines (capillary texels outside core and rim) keep ≥ 0.85 of their alpha and ≥ 0.7 of their local contrast | All 8 schools: diffuse 3.88-5.72 (unweighted 3.66-5.13, recorded); hairline alpha ≥ 0.869, contrast ≥ 1.01. Pillow has no BC7 encoder, so DXT5 stands in, a stricter test than the BC7 the editor writes |

`tests/test_magic_ground.py` checks:
- the pack reader: LZ4, raw chunks, the two-chunk large packs, and the installed `core.tpac`;
- CIEDE2000 against the Sharma reference pairs;
- the margin and the minimum accent size;
- the atlas contract against the shared table (columns trunk, fork, tip, burst);
- the vein geometry: complementary feathers across a joint, the trunk line's period and its on-axis ends, the fork and burst mirrors, the fork starting on the trunk's line, determinism;
- the committed sources: their hashes against `magic-veins.json` and `magic-textures.json`, every cell's transparent margin, each trunk's tail block equal to its head block in all three textures, and the feathered trunk ends;
- the published-pack checker against a vanilla decal pack.

## Reading vanilla packs

`tools/art/tpac_textures.py` (`list`, `find`, `extract`) reads texture records from installed `.tpac` packs and decodes the top mip with Pillow. It fixes the two faults of the earlier scratch decoder:
- **Raw chunks.** A chunk whose stored size equals its size is stored raw, not LZ4.
- **Large packs.** `core.tpac` and `core_game.tpac` (snow, ice, stone_desert) carry two chunks per texture: a raw low-resolution streaming chain, then the full chain. The full chain is found by its byte size, and packs are read by seeking. `core_game.tpac` is 3 GB, with a 5.5 MB header.

## Publishing (one owner editor session)

Publishing is blocked headless. The editor's `publish` command packages only what is already in the module's editor asset database (`Modules/<module>/Assets/`). Importing textures and creating materials happen in the Resource Browser, and the editor has no command-line publish. Do these steps in one session, under the shared-install rules: CLAIM/OK/RELEASE with the battle-scale session, no Bannerlord, Bannerlord.Native or Watchdog process running, `build/battle-scale/native-test.lock` opened with FileShare.None, and the native DLL hashes recorded before and after.

1. Copy `Modules/MNBL.ProceduralWorld/AssetSources/magic_ground/` from the repository (after `git lfs pull`) into the installed `Modules/MNBL.ProceduralWorld/AssetSources/magic_ground/`.
2. Start the Modding Kit editor (`bin/Win64_Shipping_wEditor/Bannerlord.exe`, Editor from the launcher) with MNBL.ProceduralWorld enabled.
3. **Import the textures.** In the Resource Browser, open MNBL.ProceduralWorld and import the 24 files into a new folder `magic_ground`, or let the editor pick up new AssetSources files. Set the flags:
   - `_d`: has alpha, don't resize in atlas, BC7;
   - `_n`: normal map, BC5;
   - `_s`: specular map.
   - If PNG import is refused, regenerate with `--format tga`.
4. **Create the eight materials.** Right-click vanilla `mainmap_decal_undervillage` and clone it into MNBL.ProceduralWorld's `magic_ground` folder as `mnbl_magic_<school>`. On each clone:
   - set diffuse to `mnbl_magic_<school>_d`, normal to `_n` and specular to `_s`;
   - add the `use_specular` shader flag;
   - keep `gbuffer_alpha_blend`, `use_albedo`, `render_on_terrain` and `use_normalmap`.
5. **Publish.** File > Publish Module, MNBL.ProceduralWorld, to a folder outside the module (the editor refuses the module folder itself). Then exit the editor.
6. **Bring the packs into the repository.**
   - Copy the published `AssetPackages/*.tpac` into `Modules/MNBL.ProceduralWorld/AssetPackages/`.
   - Copy the editor's new `Modules/MNBL.ProceduralWorld/Assets/` package files into the repository's same path, so a later re-publish starts from them.
   - Both paths are in Git LFS (`.gitattributes`).
7. **Verify.** `python tools/art/check_magic_packs.py --out evidence/art/magic-packs.json` must pass: 24 textures with their formats, flags and sources, and 8 materials with their flags.
8. **Leave the install as found.** Remove the copied AssetSources and the editor's Assets from the installed module, unless MMR-247's deploy will replace them. Record the hashes and RELEASE.

**Done 2026-10-20** by Astra (the Codex agent) at the owner's request, under CLAIM/RELEASE. Its report and session evidence stay outside the repository (`C:\Users\davgo\m2\publish\REPORT.md`). What differed from the steps above:
- **Starting the editor.** Running `Bannerlord.exe` directly failed ("There is no target file!") until the owner validated the Modding Kit in Steam. After that, Steam Library > Modding Kit > Launch > Play > Editor worked (host `bin/Win64_Shipping_wEditor/TaleWorlds.MountAndBlade.Launcher.exe`).
- **Dependencies.** The editor resolves neither the Workshop Harmony module nor a module DLL outside `bin/Win64_Shipping_wEditor`. A temporary `Modules/Bannerlord.Harmony` copy and editor-bin copies of the Harmony and MNBL binaries were staged for the session and then removed. `SubModule.xml` was never changed.
- **Materials.** Vanilla materials have no Duplicate command. Create Override > Material copied `mainmap_decal_undervillage` into the module; the copy was renamed `mnbl_magic_weave` and duplicated for the other seven.
- **Flags as the editor names them.** `_d` Albedo_HQ (BC7) with has_alpha and dont_resize_in_atlas; `_n` Normal (BC5) with `is_bumpmap` (this build's name for the normal-map flag); `_s` Specular (DXT1) with is_specularmap. PNG import worked.
- **Output.** One pack, `AssetPackages/pack0.tpac` (94,687,675 bytes, SHA-256 `1a44ee55cf5eb1f39a970f4347b17e7dcc785c2b9778b5d178d2a6ba60773615`), and the editor database `Assets/magic_ground/` (24 texture and 8 material records), both in the repository. The editor's whole-module export also holds existing module content; only these two paths were taken.
- **Checker fix.** `check_magic_packs.py` now reads a material's name by its length prefix: the editor writes a letter byte right after the name, which the old regex swallowed. It passes on the pack: 24 textures and 8 materials, 0 problems.
- **Install.** Restored and verified: the 132 module files are unchanged, `SubModule.xml` is byte-identical, and the native DLL hashes did not change. Outside the module the editor rewrote only `bin/Win64_Shipping_wEditor/imgui.ini` and `steam_appid.txt`.

**Re-import for MMR-288: done 2026-10-20** by a Codex Modding Kit session run by the orchestrator (report outside the repository:
`C:\Users\davgo\m2\publish2\REPORT.md`). The editor picked up the changed sources on its own; every vein texture's decoded top mip differs
from the previous pack and matches its new source within 2.3/255 mean error. The same session added MMR-290's `mnbl_magicwater` and
cluster W's `mnbl_water_surface`. One pack, `AssetPackages/pack0.tpac` (70,834,304 bytes, SHA-256
`36ce4d89baa4b30cd6ff6548e96b5348124084da9ecf4d353693b4c0b951af96`): 29 texture and 10 material records; the editor database is
`Assets/{magic_ground,magic_water,water_surface}/`. `check_magic_packs.py` passes with 0 problems; the install was restored and verified
as before. The steps it followed:

The vein atlases keep every name, size, format and flag, so publishing them is a re-import of the 24 textures with no material change. In one editor session under the rules above:
1. Copy the 24 re-derived files from the repository's `AssetSources/magic_ground/` over the installed copies (after `git lfs pull`).
2. In the Resource Browser, re-import each texture (or let the editor pick up the changed sources) with the settings the first publish used: `_d` Albedo_HQ (BC7) with `has_alpha` and `dont_resize_in_atlas`; `_n` Normal (BC5) with `is_bumpmap`; `_s` Specular (DXT1) with `is_specularmap`. Don't touch the eight `mnbl_magic_<school>` materials.
3. Publish Module to an outside folder, bring `AssetPackages/*.tpac` and the changed `Assets/magic_ground/` texture records into the repository, run `python tools/art/check_magic_packs.py --out evidence/art/magic-packs.json`, and leave the install as found.

## Packaging

- The module ships its published packs: `AssetPackages/*.tpac` (`config/mods.json` package files and provenance; `tools/ci/CiBuild.ps1` stages them; `tools/Deploy-Dev.ps1` deploys them).
- It never ships the sources (`AssetSources`) or the editor packages (`Assets`).
- `mods.py package` refuses a package without `AssetPackages/*.tpac`: a release without the materials would draw no magic ground.
- The packs carry the engine's `.tpac` extension, which the package's official-name rule refuses everywhere else. `mods.py package` exempts only the module's own packs, the `AssetPackages/*.tpac` its package block lists with a provenance; a `.tpac` at the module root or in any other folder is still refused. `tools/Package.ps1` still refuses a pack byte-identical to any installed game file.
- The textures are derived from TaleWorlds textures (recoloured and recomposed, never byte-identical), and the package notes say so.
