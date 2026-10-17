# Reference: the Player Settlement mod (research note, 2026-10-17)

**Status.** This is a research note, not an implementation. Player Settlement (Steam Workshop 3720376888, v7.5.0) lets the player build towns, castles and villages in a running campaign. It is a **reference only** (decision 002): we do not bundle it, depend on it or copy its code.
- **Method:** its assemblies were read with ildasm (scratch dumps, not in the repo) and its data files read in place. Nothing was run in game.
- **Version:** it declares Bannerlord v1.3.9; it is untested on our v1.4.8. Every vanilla API it calls exists in 1.4.8.

## How it builds a settlement

- **Data, not code.** Each culture has a template file (`ModuleData/Player_Settlement_Templates/*_default.xml`, War Sails variants under `*_War_Sails/`). Each template is ordinary settlement XML with placeholders (`{{POS_X}}`, `{{G_POS_X}}`, `{{PLAYER_CLAN}}`, `{{VILLAGE_TYPE}}`...).
  - It carries full `Components` (with `Buildings` levels), `Locations` that reuse installed scenes (e.g. `empire_town_g`, `arena_empire_a`, keep interiors, `empire_shipyard`) and `CommonAreas`.
  - **The same model as MMR-33's prototypes:** installed assets, generated identity.
- **Map icon: a prefab per template.** `Prefabs/*.xml` (about 39 MB) holds one prefab per template, copied from the vanilla `Main_map` hierarchies. They carry the tags vanilla `SettlementVisual` reads:
  - `map_settlement_circle`, `main_map_city_gate`, `main_map_castle_gate`, `map_banner_placeholder`;
  - walls, `map_camp_area_*`, `map_siege_engine_*`, `map_defensive_engine_*`;
  - War Sails: `main_map_city_port` and the blockade arc.

  Children keep their scene `<levels>` (level_1..3, civilian, siege, looted). There are no new meshes or scenes.
- **Registration in a running campaign.** It clones the template, fills the id, positions, owner and culture, then calls `MBObjectManager.LoadXml` with `Campaign._gameLoadingType` temporarily set to NewCampaign.
  - The loading type matters in 1.4.8: `Settlement`/`Town`/`Village.Deserialize` build the location complex, buildings, prosperity, hearth and village binding only when loading as a new campaign.
  - Then it runs the creation hooks: `OnGameCreated`, `AfterInitialized`, `OnFinishLoadState`, and `SettlementVisualManager.AddNewPartyVisualForParty`, which instantiates the prefab through `AddNewEntityToMapScene`.
  - It fills in the town state by hand: buildings and their default project, gold, garrison, militia, notables, market, workshops, crafting orders, recruits.
- **The forced reload.** It forces a save and reload after every build. `Campaign._towns/_castles/_villages`, nameplates and the navigation distance caches are built only at load (`Campaign.OnDataLoadFinished`). The mod never builds distance caches; a Harmony finalizer swallows the resulting exceptions and returns `float.MaxValue`.
- **Persistence.** The settlement XML is stored inside the save (behaviour `SyncData`, custom saveable types). On load it is re-registered early (`RegisterSubModuleObjects`), before the save fills in the state. A settlement built after the loaded save is skipped, which gives "ghosting".

## What we do differently already

- Generated settlements register at campaign creation from generated XML, and replay from the world package on load (`GeneratedCampaignRegistration`, `GeneratedGameManager`). No reload is needed.
- Map entities are built by `SettlementEntityFactory` (MMR-40) and spawned through our own `GeneratedMapScene.AddNewEntityToMapScene`.
- Real navigation caches are built per world (MMR-41); exceptions are not swallowed.

## What we could take from it

- **Authored prefabs for map entities.** Export each prototype's `Main_map` hierarchy (tags, levels, scripts) into module prefabs named by prototype, and instantiate them (`GameEntity.Instantiate`, then rename to the settlement id). Vanilla `SettlementVisual.OnStartup` then needs no patching. This could replace or cross-check MMR-40's code-built hierarchy.
- **Settlements added after campaign start** (later work: player founding, late hideouts). Use the loading-type switch around `LoadXml`, plus their checklist:
  - the creation hooks and the visual;
  - `Campaign._towns` and siblings;
  - the clan and kingdom fief caches;
  - garrison, workshops, crafting orders and recruitment;
  - village production and trade binding;
  - the building default;
  - navigation caches and nameplates.
- **Pitfalls they guard against** (useful invariants for our checks):
  - distance-model exceptions for settlements missing from the caches;
  - empty `map_camp_area_*`, which leaves besieger camp positions empty;
  - siege engine slot and wall-section counts that disagree with the entity tags;
  - tier children without level masks;
  - some prefabs crashing `GameEntity.Instantiate` (they keep a blacklist);
  - building level 0 or no daily default;
  - **reused mission scenes whose workshop and notable markers do not match the settlement's data** (`MissionAgentHandler.GetAllProps`, `NotableSpawnPointHandler`, `InitShopSigns`). Our settlements copy their prototype's whole component and locations, so markers and data come from the same installed settlement. MMR-42's catalogue inventories the markers but does not compare counts.
