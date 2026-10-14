# MMR-59 research: wind/flow, storms, weather and season binding (2026-10-14)

Started as a read-only IL survey of the installed 1.4.8 + War Sails assemblies. The runtime result is in docs/runtime-capabilities.md ("Generated wind, storms and weather rebind") and evidence/runtime/environment-rebind-probe.json. The binding actually used is the private-field write in GeneratedMapSupport.PrepareWeather, not the IMapScene re-implementation suggested below. Sources: `build/scenetrace/il/*.tsv`, plus method dumps made with `build/research-baseline/ApiTrace.ps1 -Mode methods`. The dumps were written to session scratch and not committed. To regenerate them, use the type needles named in each section (`Type::*`). File facts come from the installed `Main_map` folders. Items marked UNVERIFIED are inferences. Offsets are IL offsets in the named method.

## Summary: recommended binding

All weather, wind and storm state is per instance: the `MapScene`, the `Campaign`, the models, the behaviours, the `StormManager` custom manager, the per-game `NavalDLCManager` and the per-campaign `SandBoxViewVisualManager`. A world switch replaces it automatically, provided each world binds its data during its own `MapScene.Load`. No managed static weather arrays exist (stsfld scan below).

| Data | Single store every consumer reads | Recommended binding |
|---|---|---|
| Wind/flow | `SandBox.MapScene._windFlowMapData` (private `float[524288]`, fixed 512×512×2) | Fill it in `DiagnosticMapScene.Load` before `MapWeatherModel.InitializeCaches`. Today this is a reflective write; keep it. Production option: author `flowmap.dds` (512², G16R16F) in the generated SceneObj folder and let the public `MapScene.SetSnowAndRainDataWithDimension` pull it through native `Scene.GetWindFlowMapData` (UNVERIFIED that it decodes an authored file). Seam-only fallback, with no private fields: replace `NavalDLCManager.Instance.NavalMapSceneWrapper` (public setter) with a decorator that overrides `GetWindAtPosition`. |
| Storm spawn area | `StormCampaignBehavior._allOpenSeaWeatherNodePositions`, rebuilt every session | Nothing Calradia-specific: the candidates are the weather-node grid ∩ naval faces of TerrainType `OpenSea` (19). The generated navmesh must emit OpenSea face groups. Without them no storms spawn and nothing reports it. Tune scale through a `MapStormModel` subclass (all virtual). For periodic worlds, return 0 from `GetHourlyStormSpawnChanceForPosition` in the strips. |
| Weather/snow/rain | `MapScene._snowAndRainData` + width/height (private), read only through `IMapScene.GetSnowAmountAtPosition` / `GetRainAmountAtPosition` | Prefer re-implementing those two `IMapScene` methods in `DiagnosticMapScene` (`public new float GetSnowAmountAtPosition(Vec2)`, range 0..255). This uses the same interface re-implementation as the MMR-63 path methods, so no reflection is needed (UNVERIFIED at runtime for these two). Also set the public field `Campaign.Current.DefaultWeatherNodeDimension` and call `Scene.CreateDynamicRainTexture(dim, dim)` before `InitializeCaches`, as `GeneratedMapSupport.PrepareWeather` does now. |
| Season | Global calendar: `CampaignTime.GetSeasonOfYear` plus `MapWeatherModel.GetSeasonTimeFactorOfCampaignTime` | No position input exists. Change the curves by overriding the virtual `GetSeasonTimeFactorOfCampaignTime` in a chained `MapWeatherModel`. Climate zones come only from the snow/rain grid, and optionally from atmosphere probes (`IMapScene.GetAtmosphereStates`). Terrain snow visuals also need `Scene.SetDynamicSnowTexture(Texture)`. A generated texture for it is unproven (see the Texture note). |

Model chaining is a supported seam (verified). `CampaignGameStarter.AddModel<T>` calls `GetModel<T>()` and `MBGameModel<T>.Initialize(previous)` at IL_0001/0009, so `BaseModel` becomes the previous model. `GameModelsManager.GetGameModel<T>` scans from the end (IL_000b–0037), so the model added last wins. NavalDLC adds `NavalDLCMapWeatherModel` and `NavalDLCStormModel` in `NavalDLCSubModule.AddModels` (IL_02ee, IL_022d). A model of ours added later wraps them. `NavalDLC.GameModels` is built from `starter.Models` in `NavalDLCManager.OnGameStart` IL_0008, so a `MapStormModel` of ours must be added before that. The relative order is UNVERIFIED.

### Mandatory bypasses (Harmony; non-virtual)

- **Non-periodic generated world: none.** Use either the private-field writes we already make (`_windFlowMapData`, `_snowAndRainData`, `_snowAndRainDataTextureWidth/Height`, `_terrainSize`; these are a private contract, not Harmony) or the seams above.
- **Periodic world:**
  - `NavalDLC.Map.Storm::set_CurrentPosition(Vec2)` (private) must be patched if storms should wrap: IL_000e `IsPositionOutOfMapBoundary` → IL_0016 `ForceDeactivate`. Unpatched, a storm dies at the border and nothing throws.
- **Optional, only if needed:**
  - `SandBox.MapScene::GetTextureDataIndexForPosition(Vec2,int,int)` (private): wind is fixed at 512×512, the terrain origin at 0, and the index is clamped.
  - `DefaultMapWeatherModel::GetNodePositionForWeather` (private, no clamp): only if a queried position can leave [0, terrainSize). Otherwise canonicalize the position in an overriding model before calling base.
  - `NavalDLCPartySpeedCalculationModel::CalculateWindBoostForParty` (private): only to change the wind→speed formula itself. Its input is virtual.

## (a) Naval speed wind query and single-position sample

- **Gameplay value:** `Campaign.Current.Models.MapWeatherModel.GetWindForPosition(new CampaignVec2(pos, false))`.
  - With NavalDLC this goes through `NavalDLCMapWeatherModel.GetWindForPosition`, which calls `NavalDLCManager.Instance.NavalMapSceneWrapper.GetWindAtPosition(pos)` at IL_0011 → `NavalMapSceneWrapper` IL_0007 → `SandBox.MapScene.GetWindAtPosition` (public, non-virtual) → `_windFlowMapData[2i], [2i+1]` with `i = GetTextureDataIndexForPosition(pos, 512, 512)`.
  - The length is clamped to [0.0667, 0.46] at IL_0028. Inside a storm it gains `Map(n, 0, 1, 0.1, 0.6)` (IL_0075), and the total is clamped to [1e-5, 1] at IL_0089.
- **Raw texel:** `((MapScene)Campaign.Current.MapSceneWrapper).GetWindAtPosition(pos)`.
- **Speed:**
  - `NavalDLCPartySpeedCalculationModel.CalculateFinalSpeed` calls `CalculateWindBoostForParty` at IL_01a2. That method calls `GetWindForPosition(party.Position)` at IL_0015 and computes the angle between bearing and wind. Below 120° the factor is `Map(angle, 0, 120, |w|, 0) × 1.5`, clamped to [0, 1.5] (IL_006a–007f), plus the FairWinds perk. Otherwise the factor is 0, or the ShockAndAwe secondary bonus.
  - `MobileParty.CalculateSpeedForPartyUnified` IL_0124 stores `_lastWind`, and `IsLastSpeedCacheInvalid` IL_004e compares against it. Changing wind data therefore invalidates speed caches.
- **Other readers of the same virtual:**
  - `Storm.ChangeMoveDirection` IL_001c
  - `MapScreen.TickCircles` IL_04cd
  - `NavalMobilePartyVisual.UpdateEntityPosition` IL_035a
  - `DefaultMapWeatherModel.GetWaveStrengthForPosition` IL_001e and `GetAtmosphereModel` IL_02ce (mission `NauticalInfo.WindVector`)
  - `NavalDLCMapWeatherModel.GetAtmosphereModel` IL_00eb
- **Direct wrapper reader that bypasses the model:** `NavalDLCStormModel.GetHourlyIntensityChangeForStorm` IL_003a.
- **Base model:** `DefaultMapWeatherModel.GetWindForPosition` is the constant `Vec2.Side × 0.26` and reads no data.

## (b) Open-sea storm spawn area

- **Candidates:** `StormCampaignBehavior.OnSessionLaunchedEvent` IL_001b calls `InitializeStormNodes`.
  - For i, j < `Campaign.DefaultWeatherNodeDimension`, it takes the position `(i/dim·T.x, j/dim·T.y)`, with `T = IMapScene.GetTerrainSize()` (IL_0015, IL_003a–0060).
  - The position is kept if `IMapScene.GetFaceIndex(new CampaignVec2(p, false))` is valid (IL_007b) and `GetTerrainTypeAtPosition == 19` (OpenSea) (IL_00a1–00a8).
  - The list is shuffled with `Campaign.UniqueGameId`. `SyncData` is empty, so the list is rebuilt every session from the live navmesh.
  - It uses no settlement lists, no hard-coded coordinates and no map borders.
- **Spawn:** `HourlyTick` → `TrySpawningNewStorm`.
  - For each candidate, `RandomFloat < GetHourlyStormSpawnChanceForPosition(p)` (IL_0045; 0.0025).
  - The live count must be below `MaximumNumberOfStorms` (6, IL_0065), and no live storm may be within the squared distance `_spawnDistanceSquaredThreshold` (40000 = 200 m, IL_009e).
  - Then `StormManager.CreateStormAtPosition(p)` (IL_00d2). The type comes from `GetSpawnedStormTypeForPosition`, by rain at p: ≤0.2 / ≤0.6 / else.
- **Scale constants (Calradia: terrain 1040 m, dim 32 → 32.5 m node spacing):**
  - Radii 20/30/40 m ±20% with intensity.
  - Speeds 3/2/1.
  - Life: `GetStormLifeSpan` gives min 10 d and max 21 d; the `Storm` constructor then rolls `min + RandomInt(0, 21)` days, so 10–30 d. Developing and finalizing phases add 8 h each.
  - The spawn-separation threshold above.
- **Density:** the spawn rate scales with the number of OpenSea nodes (always dim² samples), not with sea area. Override any of these in a `MapStormModel` subclass.

## (c) Snow / rain / season for a position

- **`GetWeatherEventInPosition(Vec2)`** returns a cached `WeatherEvent`: Clear 0, LightRain 1, HeavyRain 2, Snowy 3, Blizzard 4, Storm 5.
  - The base reads `_weatherDataCache[y·dim + x]`, which `MapWeatherCampaignBehavior` updates node by node.
  - NavalDLC returns Storm when the base value is Clear and the position is within 1.25 × EffectRadius of an active storm.
- **`GetSnowAndRainDataForPosition(Vec2, CampaignTime, out snow, out rain)`** computes live:
  - `IMapScene.GetSnowAmountAtPosition(rawPos)/255` (IL_0017)
  - `GetRainAmountAtPosition(nodeSnappedPos)/255` (IL_0028)
  - the season factors (IL_0051), passed through SmoothStep
  - NavalDLC forces rain = 1 inside a storm's EffectRadius.
- **`GetWeatherEffectOnTerrainForPosition(Vec2)`** returns Wet for any non-Clear event. NavalDLC also returns Wet where `d² < 1.2·R²` from a storm or from one of its last 6 recorded positions (`Storm.HasWetWeatherEffectAtPosition`).
- **Season** is time-only. Use `CampaignTime.Now.GetSeasonOfYear` and `GetSeasonTimeFactorOfCampaignTime(ct, out snowF, out rainF, snap)`.

## (d) Static state that survives a world switch

- **`Campaign.MapDiagonal`, `MapDiagonalSquared`, `MapMinimumPosition`, `MapMaximumPosition`, `MapMaximumHeight`** are static backing fields (checked with Cecil). Each `Campaign.LoadMapScene` rewrites them (IL_0080–00b3) after `IMapScene.Load` and before `AfterLoad`. The weather seed reads `MapDiagonal` (`GetCurrentWeatherInAdjustedPosition` IL_0004).
- **`NavalDLCManager.Instance`** is static: set in `NavalDLCSubModule.OnGameStart` IL_0011 and nulled in `OnGameEnd` IL_0008, which also nulls `NavalMapSceneWrapper` (IL_0002).
- **`MBMapScene.ApplyRainColorGrade`** is a static bool that `MapWeatherVisualManager.WeatherAudioAndVisualTick` rewrites every visual tick. Only the first frame can be stale.
- **No ldsfld/stsfld** occurs on any field of `DefaultMapWeatherModel`, `NavalDLCMapWeatherModel`, `NavalDLCStormModel`, `MapWeatherCampaignBehavior`, `StormCampaignBehavior`, `Storm`, `StormManager`, `MapScene`, `MapWeatherVisualManager` or `NavalMapSceneWrapper`.
- **`Campaign.DefaultWeatherNodeDimension`** is a public *instance* field. Its default is 0, and only `MapScene.SetSnowAndRainDataWithDimension` (IL_005d) and the editor set it. A new campaign therefore starts at 0 until bound.
- **Native resources owned by the old `Scene`** (dynamic rain texture, snow texture, wake renderer) are UNVERIFIED to be released with it.
- **Save/load risk:**
  - `MapWeatherCampaignBehavior.SyncData` saves `_lastUpdatedNodeIndex`. Loading it with a smaller dim gives IndexOutOfRange in `WeatherUpdateTick`.
  - `StormManager` is a saved custom manager, so saved storms keep their absolute positions. The saved storm fields are UNVERIFIED.

## Per-consumer details

### SandBox.MapScene (data owner)

- **`Load`** (virtual final; `DiagnosticMapScene` re-implements it):
  - `Scene.Read` IL_00db/00f4
  - `LoadAtmosphereData` IL_01e3
  - `MapWeatherModel.InitializeCaches` IL_01f7
  - `GetTerrainData` → `_terrainSize = nodes × nodeSize` IL_021c–0248 (after InitializeCaches)
  - No weather fields are written in Load itself.
- **`SetSnowAndRainDataWithDimension(Texture, int dim)`** (public, non-virtual):
  - `Scene.CreateDynamicRainTexture(dim, dim)` IL_0008
  - width and height from the texture IL_000f/001b
  - `_snowAndRainData = new byte[w·h·2]` IL_0035
  - `Texture.GetPixelData` into it IL_0046
  - `Scene.SetDynamicSnowTexture(tex)` IL_0052
  - `Campaign.DefaultWeatherNodeDimension = dim` IL_005d
  - `_windFlowMapData = new float[524288]` IL_0068
  - `Scene.GetWindFlowMapData` IL_007e
  - Calling it overwrites any wind we wrote earlier, so order matters.
- **`GetWindAtPosition`** (public, non-virtual, not on `IMapScene`) hard-codes 512 (IL_0002/0007).
- **`GetSnowAmountAtPosition` / `GetRainAmountAtPosition`** (`IMapScene`, virtual final) read byte `2i` / `2i+1` (IL_001d / IL_001f). The grid is the texture's w×h and is independent of dim.
- **`GetTextureDataIndexForPosition`** (private):
  - `floor(x/T.x·w)`, `floor(y/T.y·h)` (IL_0018/002f)
  - `ClampIndex` (IL_0037/003f)
  - index `= y·w + x`
  - It assumes the terrain origin is (0,0) and ignores `border_min/max`. It calls `GetTerrainSize` non-virtually, so re-implementing `GetTerrainSize` does not affect it.
- **`SetupWaterWake(size, offset)`** calls `EnsureWaterWakeRenderer` (IL_0006), `SetWaterWakeWorldSize(size, 0.994)` (IL_0017) and `SetWaterWakeCameraOffset` (IL_0023).
  - Its only caller is `NavalMapSceneWrapper.InitializeMapWaterWake` IL_0010, with (128, 8).
  - These are constants, not map bounds. They are most likely a wake window around the camera (UNVERIFIED native semantics).
- **`GetAtmosphereStates`** (`IMapScene`) reads entities tagged `atmosphere_probe` with `MapAtmosphereProbe` (IL_000c/0025). Neither installed `Main_map` xscene contains `atmosphere_probe` (grep), so the vanilla atmosphere grid is empty.
- **`Destroy`** does not clear the weather arrays. They die with the instance.

### SandBox.View.Map.SnowAndRainTextureDefiner (vanilla binder; scene script)

- **Prefab:** `MapSnowAndRainDefiner` in `SandBox/Prefabs/map_icons.xml`, with `SnowAndRainTexture = main_map_snow_flowmap` (a global `.tpac` resource) and `WeatherNodeGridWidthAndHeight = 32`. Both Main_maps place one instance.
- **Binding path:** `OnInit` IL_0001 and `OnTerrainReload(1)` IL_0005 → `SetDataToScene`: `castclass SandBox.MapScene` on `Campaign.MapSceneWrapper` (IL_0018) → `SetSnowAndRainDataWithDimension` (IL_0029).
- **Ordering:** `Campaign.LoadMapScene` stores the wrapper (IL_000c) before `Load` (IL_0069). `InitializeCaches` (IL_01f7) needs a non-zero dim, and this is its only runtime setter. So `OnInit` evidently fires inside `Scene.Read` (inferred; UNVERIFIED in native code).
- **Generated worlds:** `tools/scenebin/build_bootstrap_world.py` rejects this prefab on purpose; `GeneratedMapSupport.PrepareWeather` replaces it. Reusing it would bind the Calradia texture.
- **Texture note:**
  - The only managed paths to a texture with generated content are `Texture.CreateFromByteArray(byte[], w, h)`, `CreateFromMemory(byte[])` and `LoadTextureFromPath(file, folder)`. All are thin native wrappers.
  - `GetPixelData` has no managed size check. Its output pixel format, and therefore the safety of the w·h·2 buffer, is UNVERIFIED.
  - This is why `GeneratedMapSupport` avoids `CreateFromByteArray/GetPixelData`.

### Wind flow file

- **NavalDLC:** `NavalDLC/SceneObj/Main_map/flowmap.dds` is 1,048,704 bytes: DDS header, 512×512, fourCC 0x70 = D3DFMT_G16R16F, pitch 2048. That equals 524288 floats = 512²×2, exactly the `GetWindFlowMapData` buffer.
- **SandBox:** `SandBox/SceneObj/Main_map` has no `flowmap.dds`. Native contains the string `/flowmap.dds` (`docs/formats/scene-bundle.md`).
- **Generated scene:** writing this file is the native-consistent wind source. That it loads for an owned-module scene is UNVERIFIED, so it needs one probe: compare `GetWindFlowMapData` with the authored values.

### DefaultMapWeatherModel (base `MapWeatherModel`; all public methods virtual)

- **`InitializeCaches`:** `_weatherDataCache = new WeatherEvent[dim²]` (IL_0016). Per model instance, so per campaign.
- **`GetNodePositionForWeather`** (private):
  - cell `= T/dim` (IL_002e/0042)
  - `x = (int)(p.x/cell)`, `y = (int)(p.y/cell)` (IL_004d/0058)
  - It does no clamping and no min offset. It returns 0 when there is no wrapper (IL_006c).
- **`UpdateWeatherForPosition`:**
  - Snow > 0.55 → `SetIsBlizzardOrSnowFromFunction` (IL_0027): ≥0.65 gives Blizzard or Snowy by a MapDiagonal-seeded roll; otherwise Snowy.
  - Otherwise → `SetIsRainingOrWetFromFunction` (IL_003a): rain ≥0.6 gives Heavy or Light rain with a 24 h look-back; otherwise Clear.
- **`WeatherUpdatePeriod`** is 4 h, and `WeatherUpdateFrequency = 4h / dim²`, so a full sweep takes 4 h at any dim.
- **`_atmosphereGrid`** is created lazily from `IMapScene.GetAtmosphereStates` (`AtmosphereGrid.Initialize` IL_000b). It is per instance.

### NavalDLC.GameComponents.NavalDLCMapWeatherModel (`MBGameModel` wrapper over Default)

- **Delegates to `BaseModel`:** `InitializeCaches`, the season, `UpdateWeatherForPosition` (sea → Clear at IL_0006) and the event/terrain/rain decorations listed in (a) and (c).
- **`IsPositionInsideStormForMission`** (private): active storm with `d² ≤ 0.64·R²`. Feeds `GetAtmosphereModel` IL_003b (mission weather).

### MapWeatherCampaignBehavior

- **Node grid:** `OnSessionLaunchedEvent` → `InitializeTheBehavior` builds the dim² nodes at `(i/dim·T.x, j/dim·T.y)` (IL_0021–005d). Each node is a land CampaignVec2, or a sea one if invalid (IL_0070/0083).
- **Updates:** every node is updated once (IL_000c), then a periodic event fires each `WeatherUpdateFrequency` (IL_0063).
- **Bounds:** nodes cover the whole terrain from origin 0, not the border box. In periodic worlds, half the nodes lie in the strips.
- **Lifetime:** per campaign. `SyncData` saves only `_lastUpdatedNodeIndex`.

### NavalDLC Storm / StormManager / NavalDLCStormModel / StormCampaignBehavior

- **`Storm`:**
  - `Tick` advances the position along direction·dt·speed (IL_0054).
  - `set_CurrentPosition` kills the storm outside `IMapScene.GetMapBorders` (`IsPositionOutOfMapBoundary` IL_0010).
  - `ChangeMoveDirection` follows the wind ± N(0, 30°).
- **`StormManager`:** a `Campaign` custom manager. It is created in `NavalDLCManager.OnGameStart` (IL_0058) for new games and fetched with `OnAfterLoad` (IL_0081–0091) on load. Its event listeners are non-serialized and re-added each session.
- **`NavalDLCStormModel`:** every method is virtual (constants in (b)).
  - `GetHourlyIntensityChangeForStorm` uses `GetFaceIndex` (IL_001a) and the wrapper wind directly (IL_003a): ≥0.15 gives +0.01, otherwise −0.01; off the navmesh gives −0.05.
- **`StormCampaignBehavior`:** `HourlyTick` changes direction with 50% chance (IL_0034), applies the intensity change (IL_0049), damages parties (IL_005f) and tries to spawn (IL_007e).

### SandBox.View MapWeatherVisualManager / MapWeatherVisual (visual only)

- **Constructor:**
  - `_rainData` and `_rainDataTemporal` of `dim²·2` bytes (IL_0018/003a)
  - `castclass SandBox.MapScene` (IL_00a1)
  - one visual per `AllWeatherNodes` entry (IL_00ba). The behaviour must already be session-launched (UNVERIFIED order).
- **`OnVisualTick`:** `Scene.SetLandscapeRainMaskData` (IL_0061). The texture is the dynamic rain texture from `CreateDynamicRainTexture(dim, dim)`.
- **`WeatherAudioAndVisualTick`:** clamps the camera to [0, T] (IL_0071/008a) and calls `GetWeatherEventInPosition` (IL_00ad).
- **`MapWeatherVisual.MaskPixelIndex`:** `y·dim + x` from origin 0.
- **Lifetime:** the owner `SandBoxViewVisualManager` is created in `SandBoxViewSubModule.OnCampaignStart` IL_001b / `OnGameLoaded` IL_000c and nulled in `OnGameEnd` IL_0057.

### NavalMapSceneWrapper / NavalDLCManager

- **Construction:** `NavalDLCViewSubModule.OnNewGameCreated` / `OnAfterGameLoaded` (IL_0012/0017) construct it and pass it to `NavalDLCManager.set_NavalMapSceneWrapper` (public).
  - The constructor applies `castclass SandBox.MapScene` to the wrapper, initializes pirate spawn points and drop-offs from scene entities, and calls `SetupWaterWake(128, 8)`.
- **`INavalMapSceneWrapper`** (public) has `GetSpawnPoints(string)`, `GetWindAtPosition(Vec2)` and `Tick(float)`. A decorator can wrap the original and override only the wind. It must be set after NavalDLC.View's callback (submodule order UNVERIFIED).

### Season and climate visuals

- **`MapScreen.TickVisuals`:**
  - `GetSeasonTimeFactorOfCampaignTime(Now, …, snap: false)` (IL_005d)
  - `MBMapScene.SetSeasonTimeFactor(scene, snowFactor)` (IL_0069)
  - `MapColorGradeManager.OnTick` reads the native factor back (IL_0018).
- **`DefaultMapWeatherModel.GetSeasonTimeFactorOfCampaignTime`:** `ToSeasons % 4` (IL_0041) → `CalculateTimeFactorForSnow/Rain`. Mission weather (`GetSeasonRainAndSnowDataForOpeningMission`) combines the season of year with the campaign event at the position.
- **Static calendar constants** (`CampaignTime.Initialize`) are identical across worlds.
