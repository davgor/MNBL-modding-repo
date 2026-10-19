# REA red team: gameplay opportunities for Claude

Status: static review complete; no proposed feature was implemented or proved in a running game. These are scoped improvement proposals, not a claim that the current mod fails its declared contract.

Reviewed the frozen dirty-tree snapshot `C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795`, base HEAD `89247468cc3b5e2c006c6473282fa09da4833074`. All source anchors below refer to that snapshot. Seven principal source/policy files were hash-compared with the live tree before filing and were identical; hashes are in [gamer-evidence-sources.json](gamer-evidence-sources.json). Claude may subsequently change them.

## Recommendation

Start with local guides and biome expertise for the fastest visible gameplay result; preserve monthly climate next for the strongest campaign-wide identity; improve battle-scene matching third. None currently justifies waiting for a Harmony replacement. The installed game already exposes managed model seams and the mod already uses their chaining pattern. The interesting engineering is preserving game behavior and world identity across these seams.

### 1. Make a companion's origin change how an expedition survives

**Player impact: high; implementation reach: small-to-medium; first proof: one companion, two biomes.**

A desert expedition should make a desert guide worth hiring. A forest guide could improve spotting there; a cold-region guide could reduce a bounded winter travel penalty. Fantasy `raceId` can optionally contribute an explicit affinity policy, while personal origin/expertise remains useful independently. Surface the reason in the existing explained speed/spotting/food numbers so the choice is legible.

**Already implemented.** HeroFactory retains `RaceId`, source role, source UID and provenance. Companion selection is bounded and deterministic, but template and home are selected from presentation culture: `Campaign/HeroFactory.cs:263,274,277,280-282`. `worldgen/policies/rosters.json:65` explicitly makes race metadata-only and uses installed human bodies. Runtime creation uses the installed template and age, then assigns name; it saves the mapping from intent ID to actual hero (`Campaign/GeneratedStartupBehavior.cs:86-89,125-135`). Thus this proposal adds mechanics to already-retained identity; it is not a request to replace skeletons or invent a new hero registry.

**Verified installed seams and callers.** REA confirms public virtual, non-final methods (Cecil independently resolves the full signatures):
- `DefaultPartySpeedCalculatingModel.CalculateFinalSpeed(MobileParty, ExplainedNumber)`, token `0x0600196c`.
- `DefaultMapVisibilityModel.GetPartySpottingRange(MobileParty, bool)`, `0x0600189b`.
- `DefaultMobilePartyFoodConsumptionModel.CalculateDailyFoodConsumptionf(MobileParty, ExplainedNumber)`, `0x06001812`.

The installed visibility implementation already calls `MobileParty.EffectiveScout`, terrain queries, culture feats and `ExplainedNumber.AddFactor`; this is an existing gameplay path rather than an unused interface. Supplemental execution-free caller inspection finds `MobileParty.CalculateSpeedForPartyUnified` calls the speed seam at IL offsets 181 and 353, and `MobileParty.get_SeeingRange` calls the visibility seam at IL 17. Tooltip methods call those same seams.

**Concrete implementation slice.** Resolve the effective scout's saved intent ID to a versioned expertise entry. Sample the canonical world biome once per relevant location/update. Wrap the installed models, preserve their result, add one capped and named modifier, and keep land/sea effects separate. Prefer actual origin/role data; an affinity assigned from only `raceId` must be a declared design policy, not implied source lore. Do not stack the same expertise once per troop. Extend the identity treatment to newly spawned companions before claiming all companions support it.

**War Sails constraint.** `NavalDLCPartySpeedCalculationModel.CalculateFinalSpeed` first delegates to its BaseModel, then adds terrain, shallow-draft, skills/perks and wind effects and caps naval speed. Replacing it with DefaultPartySpeedCalculatingModel loses those mechanics. Register the additional model after official submodule initialization, as `GeneratedGameManager.cs:112-124` already does for fleet models, and delegate to the current installed chain. An outer modifier must deliberately preserve the naval cap; first prove the land-only version.

**First proof / falsification.** With the same party roster, leader, equipment and time, move between two declared biomes, assign/remove the same guide, and compare both actual travel/visibility and explanations. Save/reload and repeat using the same intent ID. If only the tooltip changes or the modifier persists outside its biome, the implementation is incomplete. Include an NPC party so this does not silently become a player-only advantage.

**Risks.** Guide availability in sparse worlds; loss of affinity on daily-spawn companions; repeated calculations; mod-order conflicts; accidentally overriding normal cultural/perk benefits; save migration for any new runtime state.

### 2. Turn the generated globe into a seasonal military and trading problem

**Player impact: very high; implementation reach: medium-to-large; first proof: seasonal snow at two source cells, then one campaign route.**

Use source monthly snow to create geographically meaningful campaign seasons: winter campaigning and forage costs in one region while another becomes a useful alternative. Subsequent economy work can make supply expeditions, provisioning and relief trade matter. This is a stronger payoff than a purely visual climate map.

**Already implemented and deliberately simplified.** Climate synthesis preserves wind, rain, snow coverage and biome color grades, and War Sails storms are integrated. However `World/GeneratedClimateSystem.cs:95,104,113-114` reads twelve monthly snow grids and immediately collapses them to the per-cell maximum. `:194` then synthesizes weather from that maximum/ice cap. `worldgen/policies/climate-flow.json:41` explicitly leaves season to the engine's global time-only cycle. The source's monthly phase cannot survive this reduction. This is an accepted policy simplification, not an unreported bug.

**Verified installed seams and gameplay path.** REA finds virtual `NavalDLCMapWeatherModel.GetSnowAndRainDataForPosition(Vec2, CampaignTime, out float, out float)` (`0x06001448`) and `UpdateWeatherForPosition(CampaignVec2, CampaignTime)` (`0x0600144d`). `MapWeatherCampaignBehavior.UpdateWeatherNodeWithIndex` calls the model's UpdateWeatherForPosition at IL 42. The installed default speed model reads weather/terrain effects, and `DefaultSettlementFoodModel.CalculateTownFoodStocksChange` (`0x060019eb`) provides a separate virtual seam for a later explicit harvest/supply rule. Weather alone does not establish crop or trade effects.

**Important chain trap.** An outer weather wrapper overriding only GetSnowAndRainDataForPosition is insufficient evidence of success. Installed Naval.UpdateWeatherForPosition delegates land updates to BaseModel.UpdateWeatherForPosition; Default.Update then calls its own virtual snow/rain getter. A call delegated to the inner object need not return to an outer wrapper. Naval's snow/rain getter additionally applies storm rain. Preserve this orchestration deliberately: verify which object runs each update, retain storm contribution and caches, and test direct queries and weather-node updates. Do not merely add a getter and assume all weather consumers changed.

**Concrete implementation slice.** Persist/package the monthly snow planes with versioned identity instead of only SnowMax, map campaign year phase to those twelve source months, and sample canonical coordinates. Keep existing rain/wind behavior initially. Let source data determine hemisphere differences; do not hard-code opposite seasons until source cells verify that property. Follow with a small, explicit food/supply rule through chained food models if desired. Retain immutable inputs in the world bundle so loading a save does not regenerate climate.

**First proof / falsification.** Find actual source cells with different monthly snow profiles; compare their engine weather at several campaign phases, both direct getter and weather-node state, with and without a storm. Include equivalent coordinates across the periodic seam. If the two getter outputs change but the node state/mission weather does not, reject the proof. Only after that, demonstrate one bounded travel/provisioning consequence. Verify save/reload preserves phase and world identity.

**Risks.** The game calendar is not the source twelve-month calendar; source profiles may be similar; smoothing and weather caches can hide discontinuities; global season helpers and mission atmosphere may still need explicit reconciliation. This review did not prove the actual source world's hemispheres have opposite snow profiles. No seasonal wind data was established, so a monsoon system would need another explicit input/design policy.

### 3. Let choosing the battlefield on the campaign map matter more

**Player impact: high; implementation reach: medium for scene matching, large for new mission geometry; first proof: one river crossing and one ridge.**

A crossing should reliably lead to a battle with a usable crossing; a ridge chosen to oppose cavalry should produce an appropriate tactical arena. Begin by matching existing official scenes to generated local topography, then consider true generated battle geometry only after a separate terrain/navmesh proof.

**Already implemented.** `CampaignBootstrap/GeneratedBattleTerrain.cs` builds a deterministic patch map and ensures land/naval scene compatibility. It samples navigation terrain, maps unsupported types (Forest/Fording/Bridge to Plain, Mountain/Cliff/Canyon to Steppe in `:35-40`), and chooses within a candidate list using the world/block hash (`:171-197`). This is real scene selection, not missing functionality. Its selection mechanism does not score local elevation profile, crossing geometry or direction of approach; do not describe it as generated tactical terrain.

**Verified installed seam and callers.** `DefaultSceneModel.GetBattleSceneForMapPatch(MapPatchData, bool)` (`0x060019c4`) is public virtual and non-final. REA shows it consults installed SingleplayerBattleSceneData and terrain and returns a scene ID. Supplemental installed caller inspection finds `Helpers.MenuHelper.EncounterAttackCondition` (IL 457) and `EncounterAttackConsequence` (IL 843 and 1243) call SceneModel.GetBattleSceneForMapPatch. Thus a deterministic scene choice reaches both attack validation and execution; both must agree.

**Concrete implementation slice.** Create a reviewed annotation catalog for a small set of installed scenes: crossing type, relief, forest density and usable approach. Derive corresponding descriptors from the world bundle and select a stable compatible scene, preserving naval compatibility and existing periodic keys. The reviewed scene annotations are new authoring work; names alone do not establish usable terrain. Prefer enriching current patch compilation first; use a SceneModel adapter only when its input/context actually suffices. Retain current deterministic fallback for cases the catalog cannot represent, with an explicit diagnostic.

**First proof / falsification.** Pick a real generated crossing and ridge, inspect the selected scene and actual spawn/navmesh behavior, and show the same decision after reload and from seam-equivalent positions. Approach from both sides. A matching label with impassable spawns or inverted deployment is a failed proof.

**Risks.** Existing map indices are one byte (`:19,197-202`), scenes may have more constraints than TerrainType exposes, and actual scene loading/navmesh is a native boundary. True procedural battlefield meshes are not verified here and must not be promised as a small SceneModel override. No new detour system is needed for the scene-selection slice.

## Why “super Harmony” is not the prerequisite

REA independently observed `CampaignGameStarter.AddModel<T>` (`0x060008c4`) calls GetModel<T>, initializes the new MBGameModel with that base, then appends it. The existing mod uses this pattern after official initialization (`GeneratedGameManager.cs:112-124,132-138`). That gives the proposals above a supported managed extension route. The calendar proposal is harder because the chain includes internal delegation, not because a method cannot be patched.

A new patch engine becomes relevant only after a concrete blocked/native seam is demonstrated. The separate large-battle/native-agent-pool investigation owns that problem; this review does not duplicate it.

## Evidence, execution and limitations

Primary evidence: [gamer-evidence.json](gamer-evidence.json) retains complete REA summary/method/call-edge view records and an execution-free supplemental Cecil caller scan command/output. No game assembly was executed; no game was launched, tests run, build made or deployment changed.

Installed identities:
- CampaignSystem SHA-256 `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`, MVID `886629fe-6e60-40d7-9a57-8d46017179d9`.
- NavalDLC SHA-256 `146e16de9ac242ef63047cc337c858ffaf24a7b153f7bd7e18f1ccca1d268a94`.

REA retained parents (exact type selections/paths and page parameters are saved):
- Campaign speed/food/visibility/weather/companion/trade: `ev_58f61af40e9e20e922275860fa78260e8f194262fb73a625f4cebfe237da92a5`.
- Naval weather/trade/storm: `ev_538d736406a9ce18afbaa1eb2be6750fbec0664602e2290646644144aa344eb1`.
- Scene model and starter: `ev_16694cb79d5440c9733021020b18faeaf6d3960737c076a2806e59770668ba8e`.
- Naval speed: `ev_7af4609032a774658b843d485db3be2c5aff04b48169776315b566fa2bcb54ea`.

Reproduction: invoke inspect_managed_members with each saved explicit path/type_names and detail summary, then inspect_analysis_view on its parent for methods and call_edges. Pages here were exhausted within the selected types; unrelated types remain outside that coverage. Cecil ReadAssembly only parses files. Its exact read-only scan command is retained under supplemental.command; it traverses types/nested types in CampaignSystem and SandBox and reports calls to the four selected seams. Source review used rg and focused Get-Content reads; hash comparisons use Get-FileHash.

**Adversarial review and limitations.** This is the scoped gameplay-opportunities lens, not a statistical generated-content audit. I read the existing playability and climate reports, including failure/refusal fields, before drawing conclusions. They mix runtime histories and intentional fault cases; their failures are not asserted as current gameplay defects. No counts or population ratios were newly measured; no alternate land/water predicate was invented. Static method existence and callers establish candidate reach, not successful integration, balance, player fun, runtime model ordering or mission correctness. The first proofs above are required before acceptance.

**Dropped/corrected claims.** An initial guessed type name NavalPartySpeedCalculatingModel was unmatched; the actual installed type is NavalDLCPartySpeedCalculationModel, independently inspected. “Just wrap the weather getter” was rejected after inspecting the inner update delegation. “Generate completely new tactical geometry with the scene model” was rejected: that seam selects a scene ID and does not establish a native terrain authoring pipeline. Creature ecology encounters were deferred because this review did not establish their exported data contract or required creature assets. Existing generated winds/storms, fleet eligibility, deterministic battle mapping and companion identities are not presented as new features.

**Independent cross-check.** The principal lens separately confirmed the twelve-month maximum reduction and synthesis consumer, then used Cecil to verify both Naval weather methods are virtual/non-final and delegate to BaseModel (getter IL_000b, update IL_0012), while Default.Update calls its own getter at IL_000d. This supports the data-loss and dispatch constraints; it does not establish runtime integration. Existing global seasons can still change snow, so the finding is loss of source monthly phase, not “snow never changes.”

