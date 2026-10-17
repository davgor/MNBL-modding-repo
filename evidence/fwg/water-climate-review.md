# MMR-39 independent review and disposition

**Reviewer.** One independent agent. It made no tracked-file edits and launched no game.
- It re-ran `tools/Test-WaterClimate.ps1`: 29/29 at the time, after the lake planes were added during the review.
- It read the installed IL: `MapColorGradeManager`, `MapStormModel` and `NavalDLCStormModel`, `Storm`, `INavalMapSceneWrapper`, `PiratesCampaignBehavior` and `DefaultMapWeatherModel`.
- It inspected Run A's world packages on disk.

**Verdict.** 2 high, 7 medium and 5 low findings.

**Confirmed correct:**
- `MapColorGradeManager.Init` fills a 262144-byte grid, and `ApplyColorGrade` indexes row·512 + col with col = floor(x / T · 512), matching the synthesis and the binding.
- All 16 `MapStormModel` members are delegated. War Sails' spawn chance is a constant 0.0025, and the War Sails Main_map is 16 × 65 = 1040 wide, so the 32.5 spacing is correct.
- The base wind strength returns the first active storm containing the point, so taking the maximum over copies is equivalent.
- `GetWindForPosition` clamps to [0.0667, 0.46], and a zero vector gives (0, 1).
- The decorator covers the wrapper's three members. `_patrolZones` is a `Dictionary<Clan, List<PatrolZone>>` and is counted correctly.
- The boxing of the `PreviousData` struct is handled.
- The seasons formula matched the engine with 0 error.
- Temperatures are °C, and pole rows are uniform.
- The source-grid mapping matches `SourceUid`.
- v1 climates still load.
- No C# 7.3 problems.

All findings were fixed in one batch, followed by confirming runs (bottom). Each finding below is the reviewer's text, condensed, followed by what was done.

## High

**H1. A saved synthesized-climate world could not be loaded: `GeneratedWorldPackage.Write` copied only the wind and snow/rain files, so `FromInstalledPackage` threw on the missing colour-grade file. There was no load evidence for c1.**

Fixed:
- The package copies every climate file the placement names (wind, snow/rain, colour grade).
- Run 27432 saved the periodic v2 world, and its package holds `climate-colorgrade.u8`.
- Run 25508 loaded that save with no failures:
  - the package verified;
  - after load the weather binding is the same synthesis (wind `e70e04c4…`, snow/rain `5e7f598c…`, colour grade `dd65fdb1…` generated);
  - the campaign ran 0.92 hours.

**H2. Production scenes now carried a `MapColorGradeManager` that read Calradia's grid, and nothing replaced it (`FromSynthesis` was used only by tests; the patch began only for placement climates). Production weather also stays dry/calm. c1 and c3 were evidenced on fixtures only.**

Fixed:
- **Production scenes carry no manager.**
  - Its `Init` reads the texture natively, and the confirming `world_surface` run hung reading a production scene that carried one (run 15744: not responding, no CPU, for 11 minutes).
  - With the manager removed, run 8832 read both worlds.
  - A production campaign map gets its manager with the generated grid at MMR-45's activation (handoff).
- **Every generated map scene is patched.** `GeneratedColorGrade.Begin` binds the world's generated grid, or with none bound a neutral grid. The neutral grid is every cell 0, which the manager maps to its default grade. Calradia's regions never apply on a generated map.
  - The w42 fixture (diagnostic climate) binds the neutral grid in run 30900, where the manager applied 0 at noon.
  - The startup audit checks the patch.
- **The doc now states the coverage:** campaign behaviour is evidenced on the two fixtures, production scenes' water bodies by `world_surface`. Production climate binding (`FromSynthesis`, tested offline) is MMR-45's.

## Medium

**M3. Production pirate points are logical travel-grid positions but were served as physical; a production load would serve none.**

Fixed:
- `NavalPlan.AddPirates` marks the factory's points logical (copies; the factory records are unchanged).
- The decorator serves a logical point at its canonical physical position (`PeriodicWorldCoordinates.ToPhysical`) once the map scene is loaded. Without a periodic world it serves none and records `FrameError`.
- **Remaining:** the production package must carry the plan's points. That is MMR-45's activation package; the doc says so.

**M4. `GeneratedNavalMapSceneWrapper.Current` was never cleared, keeping the ended map scene alive through `Inner`.**

Fixed: `GeneratedGameManager`'s teardown releases it (`Release`, behind the War Sails presence guard).

**M5. Lake bounding-box planes could draw water over land or sea. The inserted content changed without a version change.**

Fixed:
- A lake's plane is at most the lowest other sample within one sample of its box, less 0.001. Past the terrain's east or west edge that rim continues in the copy one width away. A lake that would not clear its bed is left out.
- Bundle f596b58c shows 8 planes and leaves out 15 groups. The offline test checks that every plane clears its bed and never rises above a rim sample.
- The inserted XML (placeholder names) is hashed into the scene name, and `GeneratedWaterSystem.Version` is `mmr39/2`.
- The sea plane is exactly the terrain's size. Run 8832 showed the 2× plane widening a production scene's borders to [−800, 2400], because a production scene has no border entities.

**M6. Seam storm checks proved less than claimed: the duplicate check could never fire, double damage was untested, damage could pass vacuously, the spawn gating was tautological, rebase events were not filtered.**

Fixed:
- **Duplicate check.** Duplicates are counted by periodic distance.
- **Vacuous damage.** The model check requires damage and wind beside the storm to be positive.
- **Spawn gating.** It is checked against an inline canonical and pole-line test.
- **Rebase events.** They carry their storm and are filtered to the probe's storm.
- **Double damage.** War Sails' own `DamageNearbyParties` runs on the player's ship 3 units across the seam, then 3 units beside the storm. Run 27432: 187.24 hit points each, one application of the model's damage (ratio 1.000 and 1.000).

**M7. The day/night evidence could not fail (with no atmosphere probes the state is the default everywhere).**

Fixed:
- The probe runs to 01:00 and to 13:00, lets the map tick, and reads the manager's applied grade (`lastColorGrade`) against the installed rule: night 2, else the grid's grade under the camera, with the altitude and rain overrides.
- Run 27432: 2 at 00:37, and 140 (the generated grade under the camera) at 12:37, with night and day screenshots.
- Terrain snow cover remains a documented limit.

**M8. c2 was not yet evidenced on the synthesized periodic world (sailing failed in 6272; the screenshot showed no ship).**

Fixed:
- The second leg sails back from where the first ended. Setting sail again while at sea left the party stationary.
- The camera is moved to the ship before the screenshot.
- Run 27432: downwind boost 0.60 at speed 6.49, upwind boost 0 at speed 4.06. The screenshot shows the ship with its wake on the generated sea.

**M9. Storms at the poles: the calm polar padding turns into a (0, 1) drift, so southern storms wander through the padding holding storm slots.**

Fixed:
- `PeriodicStorms` reflects a storm past a pole line back across it, with its trail, and turns its move direction towards the equator (`GeneratedStormPolicy.PoleReflectY`, tested offline).
- Run 27432: a storm created at y 1202 (2 past the south line) was at 1198 on the next tick, heading north.

## Low

- **L10. The v2 identity check was not tied to the world.**
  - **Fixed:** loading checks the code's version, the placement's coordinate identity and the policy's `coordinates=`.
- **L11. `Synthesize` never checked whole samples per width.**
  - **Fixed:** it refuses a width or strip that is not a whole number of samples of every grid (tested offline).
- **L12. The wind direction maths was only tested with all-east arrows.**
  - **Fixed:** synthetic all-north arrows give (0, −0.4) at mid latitude, and alternating east/west columns interpolate through calm (offline).
- **L13. A leaked colour-grade patch would go unreported, and `End` had no ownership guard.**
  - **Fixed:** only the scene that began the patch ends it, and a new `Begin` removes any leftover. The startup audit checks the patch is applied on a generated map.
- **L14. One tick of missed damage for a storm in a strip between moving and its rebase.**
  - **Not fixed (documented limit):** the locator's image pass assumes a canonical search centre. A fix would need another patch, for at most one hourly tick in the frame a storm crosses.

## Confirming runs

| Run | What | Result |
|---|---|---|
| `tools/Test-WaterClimate.ps1` | offline, bundle f596b58c | 33/33 |
| 27432 | periodic fixture: water, climate, colour grade, seasons, wakes, sailing, day/night, storms (seam, damage pass, pole), save | ok, 0 failures |
| 25508 | load of 27432's save | ok, 0 failures, climate rebound |
| 30900 | w42 fixture: pirates, neutral colour grade, sailing, day/night | ok, 0 failures |
| 32884 | `world_surface`: production scenes A, B and warm A, with water bodies and the water-aware surface checks | ok. Every surface check passed; 8, 51 and 8 lake planes placed and hit by rays; borders [0, 1600]; worlds collected |
| 34508 | `world_navigation` (MMR-38 regression) | ok. 33 of 34 summary checks pass. The cross-process check compares against 6436, an older module; the only report differences are the content-addressed scene names, which change with the water bodies by design |
| 31900 | MMR-63/64 wrap regression on fixture-c | ok, no wrap failures; every MMR-63 check passes with the 16-patch set, as accepted run 6292 |

Superseded runs:
- 22056: before the probe fixes.
- 6272 and 27560: before the review.
- 15744: hung on the production scene's manager.
- 8832: the world_surface isolation run that showed the border widening and the physics change at sea.
- 6540: world B's borders widened to −3.125 by a lake box past the terrain edge; boxes now stay inside.
- 23912 and 32644: natural storms drifted onto the w42 ship during fast-forward sailing legs. The legs now run at play speed from a start 200 units clear of storms. In 23912 the rain grade's order was also read wrong, and is now the installed order.

**Module note.** 27432 and 25508 ran the module before two probe-only changes and the lake clip; the lake clip affects production scenes only. The probe-only changes were the sailing speed and start, and the order of the expected colour grade. The later runs (30900, 32884, 34508 and the wrap regression) ran the final module.

**New finding during the confirming runs.** A production scene carrying water bodies with plane physics changes physics answers at sea: ground heights and rays meet the water surface, and a ship sits on the water. The accepted MMR-37 `world_surface` checks expected the sea bed. They now take the water surface where the terrain is below it (`WorldSurfaceChecks.Surface`, `WaterHit`). Terrain-only queries are unchanged.
