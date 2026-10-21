# Water, flow and climate on generated geography (MMR-39)

A generated world gets its own water bodies, wind field, snow and rain, terrain snow texture, colour grade, storms and pirate spawn origins. FWGC++ exports none of the engine's flow or weather fields, so the host synthesizes them deterministically from what the source world carries. Policy versions: `mmr256/1` (climate: MMR-39's synthesis with MMR-256's snow season, snow texture, latitude winds and runtime atmosphere), `mmr39/1` (storms) and `mmr285/1` (water: `mmr39/2` until MMR-285 changed the lake rule).

| Piece | File |
|---|---|
| Climate synthesis (wind, snow/rain, snow texture, colour grade) and its policy reader (`GeneratedClimateSystem.Policy`, the embedded `climate-flow.json`) | `src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs` |
| Water bodies and water classes | `src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs` |
| Storm rules | `src/MNBL.ProceduralWorld/World/GeneratedStormPolicy.cs`, `Campaign/GeneratedStormModel.cs`, `CampaignBootstrap/PeriodicStorms.cs` |
| Climate binding | `CampaignBootstrap/GeneratedClimate.cs` (schema v2, `FromSynthesis`), `GeneratedMapSupport.PrepareWeather`, `CampaignBootstrap/GeneratedColorGrade.cs` (fixtures with a manager) |
| Terrain snow texture (MMR-256) | `CampaignBootstrap/GeneratedSnowTexture.cs`; the scene attribute in `World/RuntimeWorldBackend.cs` (`Prepare`, `Construct`, `ReadCampaignMap`) |
| Season threshold, atmosphere frame and colour grade on the campaign map (MMR-256) | `CampaignBootstrap/GeneratedMapAtmosphere.cs` (ticked from `SubModule.OnApplicationTick`) |
| Pirate spawn origins | `Campaign/GeneratedNavalMapSceneWrapper.cs`, `Campaign/NavalContentFactory.cs` (`NavalPlan.PirateSpawnPoints`) |
| Production scene entities | `World/RuntimeWorldBackend.cs` (the sea plane and the lake planes) |
| Recorded policy | `worldgen/policies/climate-flow.json` |
| Offline tests | `tests/World/WaterClimateTests.cs`, `tools/Test-WaterClimate.ps1` |
| Fixture tools | `tools/Synthesize-Climate.ps1` (`tools/climate/SynthesizeClimate.cs`), `tools/scenebin/build_periodic_world.py --open-sea --pirates`, `tools/scenebin/add_fixture_pirates.py` |
| In-game checks | `Diagnostics/WaterClimateProbe.cs` (`generated_campaign` with `waterClimateProbe=1`); the `world_surface` probe's `water` rows and water-aware surface checks; `Diagnostics/WaterVisibilityProbe.cs` (MMR-285, `world_activation` with `waterVisibility=1`) |
| Evidence | `evidence/world/water-climate.json` (`tools/summarize_water_climate.py`), `evidence/world/water-climate-tests.json`, `evidence/fwg/water-climate-review.md`; MMR-256: `evidence/map-epic-2/climate-a.json` and `climate-b.json` (the harness on A' and B') |

**Ownership.**
- This ticket owns wakes, wind-sensitive movement, storms and pirate spawn origins on production geography. MMR-59 proved only that the weather fields can be rebound; MMR-53 cites this evidence.
- MMR-45 activates production worlds. At activation it binds a world's synthesized climate (`GeneratedClimate.FromSynthesis` over the bundle's `source/world.json`) and checks it in its preflight. The campaign map's grade and season threshold are driven by `GeneratedMapAtmosphere` (MMR-256, below), not by a colour-grade manager.
- MMR-40 places the blockade entities (`docs/settlement-entities.md`); pirate spawn-point entities are handed to MMR-53; the spawn points themselves are served here.
- MMR-65 owns the remaining planar consumers at the seam (see "Limits").

**What the in-game evidence covers.**
- Campaign behaviour (climate read-back, seasons, colour grade, day and night, sailing, wakes, storms, pirates, save and load) was run on two fixtures:
  - the periodic open-sea fixture `build/research-water39/fixture-d`, with the synthesized seed-42 climate in `fixture-d39`;
  - the MMR-59 w42 world with generated pirate points, in `w42p`.
- Production worlds have no campaign until MMR-45. Their scenes' water bodies were checked by the `world_surface` probe on bundles f596b58c and f63c6841 (run 32884):
  - 8 and 51 lake planes, all placed, and every lake ray met its plane (f63c6841 shows 50 since the edge-copy rule below, 2026-10-19);
  - the borders stayed [0, 1600];
  - every MMR-37 surface check passed with the water-aware expectations.
- The MMR-38 navigation reports are unchanged apart from the content-addressed scene names (run 34508).

## Water

- **Sea plane.** One War Sails water plane on the projection's datum (z = 0, `projection.json` `vertical.datumM`), exactly over the terrain [0, extent]².
  - It uses the material `naval_worldmap_water` on `editor_plane_high`, the physics `bo_editor_plane` and the tag `snapped_water_body`, like the official Main_map's water planes.
  - A production scene has no border entities, so its campaign borders are the scene's box. A larger plane widened them to [−800, 2400] in run 8832.
  - The bootstrap fixtures' plane is twice the terrain, inside border entities.
  - The native encoder keeps template entities empty, so `RuntimeWorldBackend` inserts the planes into the encoded scene. Their XML (with placeholder names) is hashed into the content-addressed scene name.
- **Lakes keep their own level.** Each 4-connected group of lake samples (bundle intent 2) gets its own plane:
  - **Level.** The largest source `water_surface` (metres) over the source lake cells its samples map to (the nearest source cell of the sample's canonical location, else a lake cell among its 8 neighbours). It is projected as the terrain is: z = (level − datum) × `worldUnitsPerMetre`, which is 0.01 and already carries the 2.5× exaggeration (`docs/world-projection.md`).
  - **Box.** The plane covers the group's bounding box grown by half a sample.
  - **Clamp.** The plane never rises above a sample that is not lake: at most the lowest non-lake sample within one sample of its box, less 0.001. Past the terrain's east or west edge, that rim continues in the copy one width away.
  - **Other lakes are not shore** (MMR-285). Before, another group's samples inside the box counted as shore, so two groups in each other's boxes (diagonal neighbours, a lake split by a one-sample land bridge) could never both show. Their samples are water under one plane or the other.
  - **Left out.** A lake whose plane would not clear its own bed is left out. The sample grid resamples the 129 source grid, so many small lakes were shallower than a sample's error. Since MMR-278 the terrain shaping lowers every matched group's bed under the plane its shore allows (`docs/terrain-compiler.md` "Shaping"), and with the MMR-285 rule every lake group shows: A' 35 of 35 and B' 159 of 159 (`evidence/world/water-visibility.json`).
  - **Edge copies** (2026-10-19). A strip copy cut by the terrain's east or west edge holds only part of its lake. It is shown, or left out, at its whole twin's level one width away: the group holding the copy of its first sample. Before this, a partial copy could clear a bed its twin did not, and the terrain's far edge showed a lake the canonical map left out (world B's west edge; 8 of the 19 accepted worlds of that day's seed sweep had an edge copy that differed).
  - **Bundle f596b58c:** 8 lake planes are shown, and 15 lake groups are left out (67 lake samples in all). The 4 strip lakes are copies of canonical lakes one width away, at the same level.
  - **Bundle f63c6841:** 50 planes are shown and 63 groups left out (51 and 62 before the edge-copy rule).
  - Lake boxes stay inside the terrain. A box half a sample past the edge widened world B's borders to −3.125 in run 6540.
- **Water classes stay apart.** The bundle's intent mask (0 land, 1 ocean, 2 lake, 3 river) is carried unchanged.
  - Navigation is MMR-38's: sea faces are OpenSea or CoastalSea, lakes are typed Lake, rivers River, and an enclosed lake is refused from the sea.
  - **Rivers have no plane**: a river follows its slope. MMR-285 draws them, and every sea or lake travel cell no plane shows, with water decals (`docs/map-dressing.md` "Water"), so no cell land parties cannot cross renders as dry land.
  - Storms spawn only over OpenSea.
  - Bundle f596b58c has 60918 ocean, 67 lake and 46 river samples out of 66049. Every lake and river sample lies above the datum, and no ocean sample does.
- **Physics.** The planes carry plane physics, so ground heights and rays meet the water surface where the terrain lies below it. A ship at sea sits on the water. The MMR-37 surface checks now expect this (`WorldSurfaceChecks.Surface`, `WaterHit`); terrain-only queries still answer the terrain.
- **Picking.** A ray straight down at open sea stops on the sea plane at z = 0, above the sea bed (−19.14 at the probe point). On land it reaches the terrain. At every shown lake's sample it meets the lake plane.
- **Reads.** No managed code reads `water_exists`, `water_level` or `global_water`; War Sails decides sea and land from navigation faces.

## Wind (flow)

- **Source.** Since MMR-256 the wind is the policy's latitude bands, perturbed per world (RT-18). FWGC++'s `climate.wind_arrows` are one fixed compass bearing per world (seed 42's all point due east), so they are no longer read. The perturbation's seed is `world.json` `config.seed`.
- **Field.** `SandBox.MapScene._windFlowMapData`, 512 × 512 (x, y) float pairs over the physical terrain, index row × 512 + col with col = floor(x / extent × 512). This is the MMR-59 layout, read back through the engine and War Sails. Nothing is decoded from texture art.
  - Each sample's centre is mapped to its canonical logical location (`PeriodicWorldCoordinates`) and then to the source grid. Its row gives the latitude (90 − 180 × row / 128) and its column the longitude (360 × column / 128).
  - **Bands** (`wind.bands`): below 30° (−0.9, −0.44), 30–60° (0.9, 0.44), 60–90° (−0.9, 0.44), as (east, north) unit directions in the northern hemisphere; the southern hemisphere mirrors the north component. Neighbouring bands blend by smoothstep over 6° centred on their edge, and the north component flips across the equator the same way, so the wind turns through calm at each band edge. Starting values; the MMR-247 balance pass may tune them.
  - **Perturbation:** the band direction is turned by 0.35 × N radians, N a seeded value noise in [−1, 1] over (longitude, latitude): lattices of 40° and 20° (weights 1 and ½), periodic in longitude, smoothstep interpolation. Each lattice value is FNV-1a 64 of `wind/1:<seed>:<octave>:<i>:<j>` finished by the MurmurHash3 64-bit mixer.
  - The wind fades to zero by smoothstep over 16 source rows towards each pole line. A pole is one location, so it has one value: calm.
  - Scale 0.4. War Sails clamps the wind's length to [0.0667, 0.46] before its boost uses it, so 0.4 reaches ships unchanged.
  - The map vector is (east, −north), because physical y runs south.
- **Recorded per world.** The synthesis record's `windBands` holds each band's mean map vector per hemisphere (canonical rows inside the band, outside the blends and the pole taper), with `seed` and `windPerturbationRadians`. A' (seed 42): below 30° north (−0.36, 0.17), 30–60° north (0.35, −0.19), 60–90° north (−0.36, −0.16); the bands alternate east and west in both hemispheres; the turn ranges −0.23 to 0.29 rad. B' (seed 73): (−0.35, 0.20), (0.35, −0.19), (−0.36, −0.16); −0.23 to 0.31 rad. `evidence/map-epic-2/climate-a.json`, `climate-b.json`.
- **Smoothness.** The largest neighbour step is about 0.38, at a band edge (A' 0.379, B' 0.379; the official War Sails flowmap's mean step is 0.39). Elsewhere the field changes slowly.
- **Seam.** Every sample is taken at its canonical location, so a strip sample is an exact copy of its canonical sample. The synthesis refuses a layout whose width or strip is not a whole number of samples of every grid.
- **In game (MMR-39, on the arrow wind).** At 24 sample points the scene's wind, the generated field and War Sails' `GetWindForPosition` agreed exactly, and every strip copy read the same as its canonical point. The banded wind uses the same layout and binding; its in-game look is MMR-247's.

## Snow, rain and seasons

- **Field.** `_snowAndRainData`, 32 × 32 (snow, rain) byte pairs (`DefaultWeatherNodeDimension` 32). Each node averages 8 × 8 sub-samples of its footprint:
  - rain = rainfall / the layer's maximum (the source values are relative proxies), byte = round(255 × value);
  - snow (MMR-256): each sub-sample's **snow-season length** k is the number of months whose bilinear snow is at or above 0.5 (`precipitation.snowSeason.monthThreshold`); a sub-sample whose bilinear ice cap is at or above 0.5 is ice cap and counts 12. A node that is at least half ice cap takes 255 (`iceCap`). Any other node takes the byte of its mean season f = mean(k) / 12 through the inverse of the installed formula (below). Before MMR-256 the byte was max(monthly snow, ice cap), so many cold cells were Snowy at mid-summer.
- **The season is the engine's.** `DefaultMapWeatherModel.GetSnowAndRainDataForPosition` scales the bytes by the season factors:
  - c = Lerp(0.55, −0.1, snowFactor), v = SmoothStep(c − 0.65, c + 0.65, snow / 255), snow = Lerp(0, v, v) = v²; a position is Snowy above 0.55 and blizzard-eligible from 0.65;
  - d = Lerp(0.7, 0.3, rainFactor), rain = SmoothStep(d − 0.45, d + 0.45, rain / 255).
  - The snow factor (`CalculateTimeFactorForSnow`) is 0 at mid-summer and 1 at mid-winter, linear both ways, so it is uniform over the year.
- **The inverse.** A byte is Snowy for the share 1 − s* of the year, s* the factor where v² reaches 0.55. So the byte for a season f is ceil(255 × (c* − 0.65 + 1.3 t)), c* = 0.55 − 0.65 (1 − f), t = SmoothStep⁻¹(√0.55) = 0.66732, and 0 for f = 0: 196 = 12 months, 155 = 9, 113 = 6, 72 = 3, 30 = the peak only (`snowSeason.bytes`; the policy reader refuses a table that is not this rule). Each is the smallest byte Snowy for at least its share; the byte below is Snowy for less.
- **Checked offline (a1).** For every weather node of A' (seed 42) and B' (seed 73), under their real `PeriodicWorldCoordinates` layouts, the harness recomputes the footprint's mean season from the monthly snow and simulates its byte over a year with the installed `TaleWorlds.Library` maths (`MBMath.SmoothStep`, `Lerp`, `Map`) and the IL's season curve. The Snowy share equals the season to within 0.006 of a year (the byte step): A' 86 seasonal nodes and 2 ice-cap nodes, B' 114 and 788 (B's pole rows are ice). Ice-cap nodes are Snowy all year; snowless nodes never.
- A zero snow byte still gives a little snow in winter (v² = 0.38 at mid-winter), as on Calradia, but never the Snowy state. The world supplies where it snows and for how long; the engine supplies when.
- **In game (MMR-39).** For the snowiest, the rainiest and a dry node in each of the four seasons, the weather model's output equalled this formula over the generated bytes exactly (error 0).

## Terrain snow (MMR-256)

- **What the engine does** (MMR-249's probe, findings §13). The terrain snows only from the texture its scene names: terrain attribute `dynamic_flowmap_texture_name`, with `use_dynamic_weather_effects="true"` (the template has it). The shader whitens by SmoothStep(x − 0.65, x + 0.65, R), with x the threshold `MBMapScene.SetTerrainDynamicParams(scene, (x, 0.65, 0))` sets: 0.55 in summer, −0.1 in winter. `Scene.SetDynamicSnowTexture` has no effect after `Read` and hangs the engine before it; this module never calls it, nor the vanilla binder or `GetPixelData`.
- **The texture.** `GeneratedClimateSystem` writes a 512 × 512 RGBA raster: R the pixel's snow byte by the season rule above (one sub-sample at the pixel centre; ice cap 255), G its rain byte, B 0, A 255. Row 0 is at physical y = extent and column 0 at x = 0, because the shader samples v = 1 − y / extent (`terrain_pixel_functions.rsh`). Physical y runs south, so the source's north pole line is near the bottom rows. R uses the gameplay curve, so a cell turns white over the same season in which it is Snowy. Strip pixels copy canonical pixels.
- **The route** (`precipitation.snowTexture.route`):
  - `namedTexture` (the default until acc5; the code keeps it): the texture is named by its hash as its PNG's file name, `mnbl_snow_` + 16 hex digits + `.png`. `RuntimeWorldBackend.Prepare` adds `dynamic_flowmap_texture_name="<name>"` to the scene's terrain (so the name is part of the scene name) and writes the raster as `<store>/world-snow/<name>`. Before every `Read` of a scene that names one (the campaign map in `ReadCampaignMap`, inspection worlds in `Construct`), `GeneratedSnowTexture.Acquire` loads that file (`Texture.LoadTextureFromPath(name, folder)`, the world preview's file mode), keeps it valid and preloaded, never renames it, and records the engine's name for it (`engineName`, `engineNameIsScenes`) and whether `Texture.CheckAndGetFromResource(name)` finds it (`resolvesByName`). The campaign map's load writes the file again from its climate if the store lost it. Textures are shared by name and counted; the last world holding one releases it (`ReleaseAfterNumberOfFrames(3)`) when that world is disposed.
  - **The name (MMR-247, acceptance run acc4).** MMR-256 named the texture `mnbl_snow_<hex>` and renamed the loaded texture to it. In acc4 the engine named the loaded texture `mnbl_snow_6842edaae0521518.png` (the file name with its extension), the rename did not make the scene's name resolve (`resolvesByName` false), and every read of the map logged `Cannot find texture: mnbl_snow_6842edaae0521518` (rgl log lines 7113, 7485, 133124). The scene now names the file itself, so the scene attribute and the engine's own name for the texture are the same string; `WithSnowTexture` refuses an extension-less name (Test-WaterClimate). Whether the terrain's lookup then finds the path-loaded texture is still unproven in game: acc5 decides (below). `climate-flow.json` `snowTexture.name` describes the prefix only and was left as it is (documentary text).
  - `permanentWhite` (the documented fallback, **the policy's route since acc5**): the scene names no texture and nothing is loaded. Only the permanent-white layer (land ice and 12-month snow, `terrain-materials.json`) is white; the season threshold is still driven. The route is part of the climate and scene identity, so switching it renames the scenes (one build).
  - **The decision (MMR-247, acceptance run acc5).** With the file name in the scene, the texture loaded (`status` loaded, `engineName` `mnbl_snow_6842edaae0521518.png`, `engineNameIsScenes` true) but `resolvesByName` stayed false, and every map read logged `Cannot find texture: mnbl_snow_6842edaae0521518.png` (rgl logs of pids 15620, 15876, 16756, 43864, 56312; `mnbl_snow_487982f9fd189506.png` on world 2, pid 56816). The season shots agreed: the cold region's white share was 0.05 in winter and 0.03 in summer. So the terrain never sees a path-loaded texture by name, and step 4 below was taken: `route` is `permanentWhite`. Land ice read white in both seasons (white share 0.87 / 0.84), from the permanent-white layer. The next acceptance run confirms that nothing else whitens.
- **The season threshold.** `GeneratedMapAtmosphere` sets it every tick (below).
- **Log row (a2).** The map load traces `climate_snow_texture:<name>:<status>:resolvesByName <bool>` before the read, and the first map tick traces `climate_visuals:snow texture <name> <status> resolvesByName <bool>:season <s> threshold <x>:grade <g>` (again each quarter of the season factor). The support report's `climateVisuals` block holds both; the activation's post-build records it (`live.climateVisuals`).
- **Unproven in game.** Whether the terrain draws a texture loaded and named this way was not tested (the probe's run hung on the earlier `SetDynamicSnowTexture` route first). MMR-247 checks it and chooses the route:
  1. Generate seed 42 on the `namedTexture` route. In `module.log`/the trace, the load must show `climate_snow_texture:mnbl_snow_….png:loaded`; record `resolvesByName` and `engineNameIsScenes`, and search `rgl_log_<pid>.txt` for `Cannot find texture: mnbl_snow_`. If the engine still cannot find it (acc5), take step 4 without waiting for the season shots (the triage's default).
  2. On the campaign map, advance to mid-winter (season factor near 1: the trace's `threshold` near −0.10) and screenshot a cold region (cold tundra, boreal forest) and a warm one (desert, grassland), then mid-summer (threshold near 0.55) at the same camera.
  3. Pass: the cold region is white in winter and not in summer, and the warm region stays unwhitened in both. Fail: no change, or the whole map whitens.
  4. On a fail, set `precipitation.snowTexture.route` to `permanentWhite`, rebuild (the scenes rename) and confirm land ice is white all year and nothing else is.
  5. Either way, record the result and the orientation (a cold region near the north pole line must whiten, not its mirror) in findings §13.

## Colour grade

- **The manager reads Calradia's grid.** The installed `MapColorGradeManager` reads the `worldmap_colorgrade_grid` texture (512 × 512 grade bytes positioned by the terrain extent) into its private grid in `Init`. Every tick it applies the grade under the camera. `ColorGradeEnabled` is honoured only in the editor. On a generated map that texture is Calradia's regions.
- **Generated grid.** Each 512 × 512 cell takes the grade of the FWGC++ natural biome of its nearest source cell:

  | Biome | Grade |
  |---|---|
  | desert | desert |
  | grassland | steppe (dry) or central Europe (wet) |
  | forest | central Europe |
  | exposed rock | highlands |
  | rainforest | mediterranean |
  | marsh | normandy |
  | boreal forest, cold tundra, land ice, and the tombstoned tundra and snow labels | north |

  - Water and unlisted ids take a temperature band: below 2 °C north, below 18 °C normandy, else mediterranean.
  - MMR-256: grassland splits by the nearest source cell's moisture (`colorGrade.grassland`: below 0.5 steppe 40, else central Europe 60). Then the cells between the pole lines are mode-filtered over 3 × 3 cells (columns past the grid's edges continue one width away, rows clamp; a tie keeps the cell's own grade when it is among the most frequent, else the smallest), which removes single-cell specks at biome borders.
  - Strip cells copy their canonical cells, and the rows beyond each pole carry one grade.
- **Production scenes carry no manager.** Its `Init` reads the texture natively (`MBMapScene.GetColorGradeGridData`), an inspection scene's read of a production scene that carried one hung the game (run 15744), and a manager created at run time hung a non-map scene (MMR-249). So MMR-256 does the manager's tick itself.
- **`GeneratedMapAtmosphere`** (MMR-256). Begun when the campaign map loads (`GeneratedMapScene.Load`) and ended in `Destroy`; it runs on every application tick while the generated map is the top screen and the scene has no manager. The map screen test is `World/CampaignMapScreen.Is` (MMR-247): `SandBox.View.Map.MapScreen` or a subclass, since with War Sails the map's screen is `NavalDLC.View.Map.NavalMapScreen`. From the installed IL of `MapColorGradeManager.OnTick`:
  - **Atmosphere:** `MBMapScene.SetFrameForAtmosphere(scene, TimeOfDay × 10, camera z, false)`, then `SetTerrainDynamicParams(scene, (Lerp(0.55, −0.1, season), 0.65, 0))` with season = `MBMapScene.GetSeasonTimeFactor(scene)`, which the map screen sets each frame.
  - **Grade:** the world's grade under the camera (row × 512 + col, col = floor(x / terrain x × 512)); 1 (the default) above z 400; the night grade 2 from 22:00 to 02:00; the rain grade 160 below z 50 while `MBMapScene.GetApplyRainColorGrade()`. A change blends from the last grade with `Scene.SetColorGradeBlend` over 1 s (0.2 s for rain). The grade textures come from `worldmap_color_grades.xml` (`MBObjectManager.GetMergedXmlForNative("soln_worldmap_color_grades")`): 1 the default, 2 the night, each `color_grade` its value; a grade with no texture takes the default.
  - **Live cells (MMR-260):** `SetGradeCell(index, grade)` overrides a cell while a mutation holds it, `ClearGradeCell(index)` gives back the world's.
  - No exception escapes the tick: the first failure stops the driver and is traced (`climate_visuals_failed`).
- **Fixtures with a manager.** The bootstrap fixtures carry the manager as a helper. While a generated map scene is loaded, a Harmony postfix on `MapColorGradeManager.Init` copies the world's grid over the one the manager read: its generated grid, or, with none bound, a neutral grid (every cell 0, the default grade). The patch is applied before the scene read, removed when that scene is destroyed, listed in `GeneratedPatches.Manifest` and checked by the startup audit; managers that initialized earlier are replaced in `PrepareWeather`. `GeneratedMapAtmosphere` leaves such a scene to its manager. Calradia's regions never apply on a generated map.
- **In game.**
  - Periodic fixture: the manager's grid hashes to the generated grid, the postfix replaced it, and no strip cell differs from its canonical cell.
  - w42 fixture (diagnostic climate): the neutral grid is bound.

## Day, night and atmosphere

- **Day and night.** Time of day is the engine's. The map screen sets the scene's `TimeOfDay`, and the manager (on a production map, `GeneratedMapAtmosphere`) applies the night grade (2) from 22:00 to 02:00, else the grid's grade under the camera. The runs below are MMR-39's fixture runs with a manager.
  - Periodic fixture (run 27432): at 00:37 the manager applied 2, and at 12:37 it applied 140, the generated grid's grade under the camera.
  - w42 fixture (run 30900): 2 at night, and 0 (the neutral grid) at noon.
  - Rain over the camera applies 160 last and masks the night grade. The probe records that case (run 32644) instead of failing it.
  - Night and day screenshots are recorded.
- **Atmosphere.** A generated scene has no atmosphere probes, so `AtmosphereGrid` returns its default state at every position: no Calradia position grid is retained. The weather model's atmosphere state takes no time argument.
- Weather, colour grade and atmosphere therefore carry no Calradia grid.

## Storms

- **Model.** `GeneratedStormModel` is chained over War Sails' `NavalDLCStormModel` (`AddModel` after War Sails' own) for every generated world while War Sails is present. It delegates everything except three members:
  - **Hourly spawn chance.** Scaled by the area a weather node stands for, (spacing / 32.5)², so the storm frequency per unit of sea matches Calradia's (the War Sails Main_map has 32 × 32.5 = 1040). On a periodic world it is 0 outside the canonical range between the pole lines, so no storm is born in a strip copy or beyond a pole.
  - **Damage at a position.** Measured from the position's copy nearest the storm.
  - **Storm wind strength at a position.** Also measured from the nearest copy, so a storm reaches parties across the seam exactly as on its own side.
- **Seam and poles.** `PeriodicStorms` runs in `PeriodicSeam.RebaseAll` on every campaign tick.
  - A storm that drifts out of the canonical range is moved by whole widths together with its wet-weather trail, and its visual is marked dirty.
  - A storm that drifts past a pole line is reflected back across it, with its move direction turned towards the equator. The calm polar padding would otherwise carry it south for the rest of its life, because War Sails turns a calm wind into (0, 1).
  - The move writes the private position directly, because `Storm.set_CurrentPosition` would test the map borders. The storm is never ended at the terrain edge and never exists twice.
  - The storm list is saved, and positions are canonical whenever the game saves.
- **In game (periodic fixture, uniform east wind).**
  - War Sails' storm candidates: 748 open-sea weather nodes, 103 of them canonical. Every strip or polar candidate's chance is 0, and every canonical chance is War Sails' chance × 2.367 (checked independently of the policy code).
  - A storm created at (MaxX − 3, 800) developed in place for 8.0 campaign hours. `Storm.IsActive` is false while a storm develops, and only an active storm moves.
  - It then drifted east and was rebased once from x 1200.04 to 400.04 with its trail. It stayed active, canonical and listed once (by periodic distance).
  - A point just across the seam took the same damage (200.85) and storm wind (0.492) from the model as its copy beside the storm. The planar damage there is 0.
  - **War Sails' own damage pass** (`StormCampaignBehavior.DamageNearbyParties`: the locator search with the seam image pass, then `TryDamagingParty`) took 187.24 hit points from the player's ship 3 units across the seam, and 187.24 beside the storm at the same distance. Each is one application of the model's damage (ratio 1.000).
  - A storm created 2 units past the south pole line (y 1202) was reflected to y 1198 on the next tick, heading north.

## Wakes and wind-sensitive movement

- **Wakes.** War Sails' view builds `NavalDLC.View.NavalMapSceneWrapper` on the map scene each session. Its constructor sets up the water wake renderer (`Scene.SetupWaterWake(128, 8)`). On a generated world that wrapper is in place, wrapped only by the pirate-point decorator below. The sailing screenshot, with the camera moved to the ship, shows the ship on the generated sea with its wake behind it.
- **Movement.** A ship on generated open sea sails downwind and then back upwind along its row.
  - Downwind follows the x sign of the generated wind at the start.
  - Only samples in which the ship moved and no storm was near are used. Natural storms add their own wind.
  - The start is at least 200 units from every storm, and the legs run at normal play speed.
  - On the periodic fixture's uniform east wind (run 27432, fast-forward legs): downwind had a mean wind boost of 0.60 and speed 6.49; upwind had boost 0 and speed 4.06. The boost source is War Sails' `NavalDLCPartySpeedCalculationModel.CalculateWindBoostForParty`.
  - On the w42 world's MMR-59 pattern (run 30900): downwind (west) had boost 0.355 and speed 4.45; upwind had boost 0 and speed 3.28.

## Pirate spawn origins

- **The gap.** War Sails reads pirate spawn points from `PirateSpawnPoint` scene entities through `INavalMapSceneWrapper.GetSpawnPoints(clan)`. A generated scene has no such entities.
- **The decorator.** `GeneratedNavalMapSceneWrapper` wraps War Sails' own wrapper and keeps its wake setup and ports. It is installed from the submodule's `OnNewGameCreated` and `OnAfterGameLoaded`, after War Sails' view built its wrapper and before `PiratesCampaignBehavior` assigns patrol zones.
  - It serves the world's points: `NavalPlan.PirateSpawnPoints`.
  - It forwards the wind (at the canonical position) and `Tick`.
  - It is released at game end, so it does not keep the ended map scene alive.
- **Where the points come from, and their frame.**
  - A fixture's points come from its placement (`placement.naval.pirateSpawnPoints`) and are already physical.
  - A production world's points are `NavalContentFactory`'s (MMR-35; `Result.Plan.AddPirates`). They are logical travel-grid points, served at their canonical physical position (`PeriodicWorldCoordinates.ToPhysical`) once the map scene is loaded.
  - A saved fixture package carries its placement's points, so saved patrol zones keep matching.
  - A production package must carry the plan's points too. That is MMR-45's activation package; see "Limits".
- **In game (w42 fixture with 8 generated points).** The wrapper served 4 points to each pirate clan, and War Sails built 8 patrol zones from them. After 2 campaign days 8 pirate parties existed, all at sea, each patrolling one of the generated points (distance 0).

## Load and dispose

- **Per map scene.** The climate is bound from fresh copies (`GeneratedClimate.Copy*`). The colour-grade patch begins before the scene read and ends in `GeneratedMapScene.Destroy`; only the scene that began it ends it.
- **Per map scene (MMR-256).** The snow texture is held by the reading world's ledger (counted per name) and released when that world is disposed; `GeneratedMapAtmosphere` begins when the map loads and ends in `GeneratedMapScene.Destroy` (only the map that began it ends it). The lifetime trackers count both (`climate: atmosphere driver`, `climate: snow textures held`). The PNG files in `<store>/world-snow` stay (one 512 × 512 PNG per world, never swept; the name is the content, so a world's file is reused).
- **Per game.** The storm model is registered at each game start. `PeriodicStorms` events reset with `PeriodicSeam.Reset`.
- **Per session.** The naval decorator is installed over the wrapper War Sails built that session, and released at game end.
- **Save and load.**
  - The world package carries every climate file: wind, snow/rain and colour grade (and, for a fixture placement, the snow texture).
  - A v2 climate is checked on load against its policy text and file hashes, the code's version, and this placement's coordinate layout.
- The game-end teardown audit counts the colour-grade patch with the periodic patches (map-scene lifetime).

## Decisions

- **The weather binding keeps the private-field contract.** It writes the private `SandBox.MapScene` fields, behind the load-time name and type checks. Every consumer is proven to read them: the engine, the weather model and War Sails. There is no public write path. `Scene.GetWindFlowMapData` reads the authored flowmap, and a wind-only wrapper decorator would not reach the weather model's snow and rain.
- **A deterministic host synthesis policy, not a source export.** The identity is the SHA-256 of the policy text (version, source SHA-256, seed, coordinate policy identity, constants, biome table, and the policy values the synthesis uses: bands, perturbation, snow season, snow route, grassland split) together with the wind, snow/rain, colour-grade and snow-texture hashes. Documentary text in `climate-flow.json` is not part of it. It is recorded in the placement's `mnbl.generated-climate.v2` block and checked on load.
- **MMR-256 is one climate identity bump** (`mmr256/1`, clean break): earlier climates, scenes and fixture placements are refused. `tools/Synthesize-Climate.ps1` writes the snow texture (`climate-snow.rgba`) into fixture placements.
- **Defaults taken (MMR-256).** The ice-cap majority rule for a node; R uses the gameplay curve; B (the shader's water mask) is 0; the hash seed is `config.seed`; a 0.35 rad turn; our own atmosphere tick instead of a manager; textures kept per name with a count.

## Limits

- **In-game proof of MMR-256 is MMR-247's:** the named snow texture route (above), the banded wind on ships, and the grade and atmosphere tick on a production map.
- **Rivers have no water surface of their own.** A river follows its slope, and a flat plane cannot carry it. River faces stay navigable as MMR-38 types them, but a ship on a river above the datum is drawn over the river bed. Bundle f596b58c has 46 river samples.
- **Shallow lakes have no plane.** On bundle f596b58c 15 of 23 lake groups would not clear their beds at the sample grid's resolution.
- **One season for the whole world.** The engine's season is global; FWGC++'s southern months are inverted. Northern-phase seasons everywhere (map epic 2 default): the snow-season length is honoured, its timing is the north's.
- **The sea plane has no `water_flowmap` script.** The official planes carry it, so the sea surface's flow visuals use the material's defaults. The gameplay wind is bound.
- **The atmosphere state is the default everywhere.** It is position-free and Calradia-free, but not derived from the climate.
- **Non-virtual planar storm checks.** Storm-to-storm collisions, spawn separation, and the wet-weather and storm-rider tests are not reached by the model. Their error is limited to storms within one effect radius (about 20–48) of the seam (MMR-65).
- **One damage tick can be missed.** A storm that crossed the seam in the same frame as War Sails' hourly damage pass, before the rebase, misses parties across the seam for that tick: the locator's image pass assumes a canonical search centre.
- **Storm spawn frequency per area matches Calradia's, not a climate.** FWGC++ carries no storm field.
