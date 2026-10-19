# Water, flow and climate on generated geography (MMR-39)

A generated world gets its own water bodies, wind field, snow and rain, colour grade, storms and pirate spawn origins. FWGC++ exports none of the engine's flow or weather fields, so the host synthesizes them deterministically from what the source world carries. Policy versions: `mmr39/1` (climate and storms) and `mmr39/2` (water).

| Piece | File |
|---|---|
| Climate synthesis (wind, snow/rain, colour grade) | `src/MNBL.ProceduralWorld/World/GeneratedClimateSystem.cs` |
| Water bodies and water classes | `src/MNBL.ProceduralWorld/World/GeneratedWaterSystem.cs` |
| Storm rules | `src/MNBL.ProceduralWorld/World/GeneratedStormPolicy.cs`, `Campaign/GeneratedStormModel.cs`, `CampaignBootstrap/PeriodicStorms.cs` |
| Climate binding | `CampaignBootstrap/GeneratedClimate.cs` (schema v2, `FromSynthesis`), `GeneratedMapSupport.PrepareWeather`, `CampaignBootstrap/GeneratedColorGrade.cs` |
| Pirate spawn origins | `Campaign/GeneratedNavalMapSceneWrapper.cs`, `Campaign/NavalContentFactory.cs` (`NavalPlan.PirateSpawnPoints`) |
| Production scene entities | `World/RuntimeWorldBackend.cs` (the sea plane and the lake planes) |
| Recorded policy | `worldgen/policies/climate-flow.json` |
| Offline tests | `tests/World/WaterClimateTests.cs`, `tools/Test-WaterClimate.ps1` |
| Fixture tools | `tools/Synthesize-Climate.ps1` (`tools/climate/SynthesizeClimate.cs`), `tools/scenebin/build_periodic_world.py --open-sea --pirates`, `tools/scenebin/add_fixture_pirates.py` |
| In-game checks | `Diagnostics/WaterClimateProbe.cs` (`generated_campaign` with `waterClimateProbe=1`); the `world_surface` probe's `water` rows and water-aware surface checks |
| Evidence | `evidence/world/water-climate.json` (`tools/summarize_water_climate.py`), `evidence/world/water-climate-tests.json`, `evidence/fwg/water-climate-review.md` |

**Ownership.**
- This ticket owns wakes, wind-sensitive movement, storms and pirate spawn origins on production geography. MMR-59 proved only that the weather fields can be rebound; MMR-53 cites this evidence.
- MMR-45 activates production worlds. At activation it binds a world's synthesized climate (`GeneratedClimate.FromSynthesis` over the bundle's `source/world.json`), checks it in its preflight, and gives the campaign map its colour-grade manager (below).
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
  - **Clamp.** The plane never rises above a sample that is not the lake's: at most the lowest other sample within one sample of its box, less 0.001. Past the terrain's east or west edge, that rim continues in the copy one width away.
  - **Left out.** A lake whose plane would not clear its own bed is left out. The 257 sample grid resamples the 129 source grid, so many small lakes are shallower than a sample's error.
  - **Edge copies** (2026-10-19). A strip copy cut by the terrain's east or west edge holds only part of its lake. It is shown, or left out, at its whole twin's level one width away: the group holding the copy of its first sample. Before this, a partial copy could clear a bed its twin did not, and the terrain's far edge showed a lake the canonical map left out (world B's west edge; 8 of the 19 accepted worlds of that day's seed sweep had an edge copy that differed).
  - **Bundle f596b58c:** 8 lake planes are shown, and 15 lake groups are left out (67 lake samples in all). The 4 strip lakes are copies of canonical lakes one width away, at the same level.
  - **Bundle f63c6841:** 50 planes are shown and 63 groups left out (51 and 62 before the edge-copy rule).
  - Lake boxes stay inside the terrain. A box half a sample past the edge widened world B's borders to −3.125 in run 6540.
- **Water classes stay apart.** The bundle's intent mask (0 land, 1 ocean, 2 lake, 3 river) is carried unchanged.
  - Navigation is MMR-38's: sea faces are OpenSea or CoastalSea, lakes are typed Lake, rivers River, and an enclosed lake is refused from the sea.
  - Storms spawn only over OpenSea.
  - Bundle f596b58c has 60918 ocean, 67 lake and 46 river samples out of 66049. Every lake and river sample lies above the datum, and no ocean sample does.
- **Physics.** The planes carry plane physics, so ground heights and rays meet the water surface where the terrain lies below it. A ship at sea sits on the water. The MMR-37 surface checks now expect this (`WorldSurfaceChecks.Surface`, `WaterHit`); terrain-only queries still answer the terrain.
- **Picking.** A ray straight down at open sea stops on the sea plane at z = 0, above the sea bed (−19.14 at the probe point). On land it reaches the terrain. At every shown lake's sample it meets the lake plane.
- **Reads.** No managed code reads `water_exists`, `water_level` or `global_water`; War Sails decides sea and land from navigation faces.

## Wind (flow)

- **Source.** `climate.wind_arrows`: fixed compass-bearing transport directions on an 8 × 6 lattice of the 129 source grid. Each arrow's 3D tangent is turned into local (east, north) at its position. The seed-42 world's 48 arrows all point due east, so the direction maths is also tested offline on synthetic arrows:
  - arrows all pointing north give (0, −0.4) at mid latitude;
  - east and west arrows in alternate lattice columns interpolate through calm.
- **Field.** `SandBox.MapScene._windFlowMapData`, 512 × 512 (x, y) float pairs over the physical terrain, index row × 512 + col with col = floor(x / extent × 512). This is the MMR-59 layout, read back through the engine and War Sails. Nothing is decoded from texture art.
  - Each sample's centre is mapped to its canonical logical location (`PeriodicWorldCoordinates`) and then to the source grid.
  - The arrows are interpolated bilinearly on their lattice (columns periodic, rows clamped).
  - The wind fades to zero by smoothstep over 16 source rows towards each pole line. A pole is one location, so it has one value: calm.
  - Scale 0.4. War Sails clamps the wind's length to [0.0667, 0.46] before its boost uses it, so 0.4 reaches ships unchanged.
  - The map vector is (east, −north), because physical y runs south.
- **Smoothness.** The largest neighbour step is 0.037 (the official War Sails flowmap's mean step is 0.39).
- **Seam.** Every sample is taken at its canonical location, so a strip sample is an exact copy of its canonical sample. The synthesis refuses a layout whose width or strip is not a whole number of samples of every grid.
- **In game.** At 24 sample points the scene's wind, the generated field and War Sails' `GetWindForPosition` agree exactly, and every strip copy reads the same as its canonical point. The scene's private buffers hash to the synthesis.

## Snow, rain and seasons

- **Field.** `_snowAndRainData`, 32 × 32 (snow, rain) byte pairs (`DefaultWeatherNodeDimension` 32). Each node averages 8 × 8 sub-samples of its footprint:
  - rain = rainfall / the layer's maximum (the source values are relative proxies);
  - snow = max(largest monthly snow cover, ice cap);
  - byte = round(255 × value).
- **The season is the engine's.** `DefaultMapWeatherModel.GetSnowAndRainDataForPosition` scales the bytes by the season factors:
  - c = Lerp(0.55, −0.1, snowFactor), v = SmoothStep(c − 0.65, c + 0.65, snow / 255), snow = Lerp(0, v, v);
  - d = Lerp(0.7, 0.3, rainFactor), rain = SmoothStep(d − 0.45, d + 0.45, rain / 255).
  - A zero snow byte still gives a little snow in winter, as on Calradia. The world supplies where it snows and rains, not when.
- **In game.** For the snowiest, the rainiest and a dry node in each of the four seasons, the weather model's output equals this formula over the generated bytes exactly (error 0). The snowy node's snow runs 0.64 (summer) to 0.99 (winter), and the rainy node's rain 0.03 to 0.57. A drier node never gets more snow or rain than the snowy or rainy node.

## Colour grade

- **The manager reads Calradia's grid.** The installed `MapColorGradeManager` reads the `worldmap_colorgrade_grid` texture (512 × 512 grade bytes positioned by the terrain extent) into its private grid in `Init`. Every tick it applies the grade under the camera. `ColorGradeEnabled` is honoured only in the editor. On a generated map that texture is Calradia's regions.
- **Generated grid.** Each 512 × 512 cell takes the grade of the FWGC++ natural biome of its nearest source cell:

  | Biome | Grade |
  |---|---|
  | desert | desert |
  | grassland | steppe |
  | forest | central Europe |
  | exposed rock | highlands |
  | rainforest | mediterranean |
  | marsh | normandy |
  | boreal forest, cold tundra, land ice, and the tombstoned tundra and snow labels | north |

  - Water and unlisted ids take a temperature band: below 2 °C north, below 18 °C normandy, else mediterranean.
  - Strip cells copy their canonical cells, and the rows beyond each pole carry one grade.
- **Binding.**
  - While a generated map scene is loaded, a Harmony postfix on `MapColorGradeManager.Init` copies the world's grid over the one the manager read: its generated grid, or, with none bound, a neutral grid. The neutral grid is every cell 0, which the manager maps to its default grade. Calradia's regions never apply on a generated map.
  - The patch is applied before the scene read and removed when that scene is destroyed. It is in `GeneratedPatches.Manifest`, and the startup audit checks it.
  - Managers that initialized earlier are replaced in `PrepareWeather`.
- **Production scenes carry no manager.** Its `Init` reads the texture natively, and an inspection scene's read of a production scene that carried one hung the game (run 15744). The fixtures carry it as a bootstrap helper. A production campaign map gets its manager, with the generated grid, at activation (MMR-45).
- **In game.**
  - Periodic fixture: the manager's grid hashes to the generated grid, the postfix replaced it, and no strip cell differs from its canonical cell.
  - w42 fixture (diagnostic climate): the neutral grid is bound.

## Day, night and atmosphere

- **Day and night.** Time of day is the engine's. The map screen sets the scene's `TimeOfDay`, and the manager applies the night grade (2) from 22:00 to 02:00, else the grid's grade under the camera.
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
- **Per game.** The storm model is registered at each game start. `PeriodicStorms` events reset with `PeriodicSeam.Reset`.
- **Per session.** The naval decorator is installed over the wrapper War Sails built that session, and released at game end.
- **Save and load.**
  - The world package carries every climate file: wind, snow/rain and colour grade.
  - A v2 climate is checked on load against its policy text and file hashes, the code's version, and this placement's coordinate layout.
- The game-end teardown audit counts the colour-grade patch with the periodic patches (map-scene lifetime).

## Decisions

- **The weather binding keeps the private-field contract.** It writes the private `SandBox.MapScene` fields, behind the load-time name and type checks. Every consumer is proven to read them: the engine, the weather model and War Sails. There is no public write path. `Scene.GetWindFlowMapData` reads the authored flowmap, and a wind-only wrapper decorator would not reach the weather model's snow and rain.
- **A deterministic host synthesis policy, not a source export.** The identity is the SHA-256 of the policy text (version, source SHA-256, coordinate policy identity, constants, biome table) together with the three field hashes. It is recorded in the placement's `mnbl.generated-climate.v2` block and checked on load.

## Limits

- **Production campaign binding is MMR-45's.**
  - A production world's campaign is not activated yet, so its weather stays the dry/calm bootstrap (War Sails then uses its minimum wind) until MMR-45 binds `FromSynthesis`.
  - Its campaign map also needs a colour-grade manager added at load with the generated grid. Until then the neutral grid applies.
  - Its saved package must carry the factory's pirate points.
- **Rivers have no water surface of their own.** A river follows its slope, and a flat plane cannot carry it. River faces stay navigable as MMR-38 types them, but a ship on a river above the datum is drawn over the river bed. Bundle f596b58c has 46 river samples.
- **Shallow lakes have no plane.** On bundle f596b58c 15 of 23 lake groups would not clear their beds at the sample grid's resolution.
- **Terrain snow cover is not drawn from the generated field.** The terrain snow texture has no safe creation path (MMR-59). Snow weather and the season scaling work.
- **The sea plane has no `water_flowmap` script.** The official planes carry it, so the sea surface's flow visuals use the material's defaults. The gameplay wind is bound.
- **The atmosphere state is the default everywhere.** It is position-free and Calradia-free, but not derived from the climate.
- **Non-virtual planar storm checks.** Storm-to-storm collisions, spawn separation, and the wet-weather and storm-rider tests are not reached by the model. Their error is limited to storms within one effect radius (about 20–48) of the seam (MMR-65).
- **One damage tick can be missed.** A storm that crossed the seam in the same frame as War Sails' hourly damage pass, before the rebase, misses parties across the seam for that tick: the locator's image pass assumes a canonical search centre.
- **Storm spawn frequency per area matches Calradia's, not a climate.** FWGC++ carries no storm field.
