# Playing procedural worlds

How to install the MNBL Procedural World module, make a world from New Campaign, play it, keep its saves safe and recover when something goes wrong. Everything here is what the module does today, as tested on one PC (2026-10-18). The developer side is in [development.md](development.md).

> **This is a local test package, not a release.** It has not been certified across seeds, long campaigns, other mods or other machines.
> **Today only seed 42 makes a world you can play.** Most other seeds are refused by the generator's quality checks. Seed 73 is generated and previewed, but refused when you start its campaign. See [Seeds](#seeds).

## What you need

| | |
|---|---|
| Game | Mount & Blade II: Bannerlord **v1.4.8.119303** with War Sails (NavalDLC) **v1.2.8**. Other versions are refused before generating: "This version does not match" |
| Harmony | Bannerlord.Harmony v2.4.2, from the Steam Workshop (item 2859188632), enabled |
| Windows runtime | Microsoft Visual C++ 2015-2022 Redistributable (x64). The world generator needs it; the game does not install it |
| Game folder | Writable. Each world's map is installed in the module's own folder. A read-only installation cannot make or load worlds |
| Windows user name | Letters your system's ANSI code page can spell (on Western systems, the accented Latin letters work). A name in, for example, CJK letters cannot generate a world |
| Disk | About 200 MB per world kept (its data bundle is 137-185 MB, its navigation caches about 7 MB per module version), plus 136-184 MB of temporary space while generating. Navigation caches of earlier module versions are never removed (438 MB had built up on the test PC) |
| Time | About 60 s to generate. Then about **7 minutes** on the first start of each world's campaign, and again on the first start or load after a module update, while the game prepares the world's navigation. The game does not respond during that time; Windows may call it "not responding". Do not close it |

The measured times come from one PC (Core Ultra 9, RTX 5080, 32 GB, 60 fps cap). Yours may differ. The game also shows them on the setup screen.

## Install

1. Extract `MNBL.ProceduralWorld-v0.0.1.zip` into `<Bannerlord>\Modules\`, so that `Modules\MNBL.ProceduralWorld\SubModule.xml` exists.
2. In the launcher, enable **FWGC Procedural World - Capability Probe** together with Harmony and War Sails.

The tests start the game with a module list directly, so the launcher itself was not exercised. A zip downloaded from the internet may need Windows' **Unblock** (file Properties) before extracting; that was not tested either.

**Disabling or removing the module** leaves your saves and worlds alone. Tested: with it disabled or removed the game starts normally, every earlier save and every world file is unchanged, and after reinstalling, a generated campaign loads again (`docs/packaging.md`).

**Do not load a generated campaign's save while the module is disabled or removed.** The check that keeps such a save away from the game's normal loader is part of the module. Without it, nothing stops the load, and what happens then has not been tested.

## Make a world

1. Main menu: **New Procedural Campaign**. It is greyed out ("A world is being generated. Wait for it to finish.") while an earlier world is still being finished.
2. **Seed**: a whole number from 0 to 4294967295, digits only, no leading zeros. The default is 42. **Random World** (R) picks one for you.
   World size (129) and generation stages (16 of 16) are fixed for this world type. There is no region to choose: the whole globe is generated.
3. **Generate** (Enter). The stages are shown as it runs: shaping the globe, building the campaign data, verifying it, building the world map, checking travel by land and sea. **Cancel** (Escape) stops it and leaves nothing running.
4. **Preview**: the world map, the counts of towns, castles, villages, ports, kingdoms and clans, and the cultures you can pick in character creation. Previous/Next (Q/E) step through places.
   - **Back** keeps the world: Generate shows it again without making it twice.
   - **New Seed** (R) draws a new seed and generates it. The reviewed world is released.
   - **Start Campaign** (Enter) starts the campaign in this world.
5. The campaign intro and character creation follow. Character creation offers only this world's cultures. Leaving it with Back returns you to the same world.
6. **The first start takes about 7 minutes** while navigation is prepared (see above). The campaign's first save is then written automatically. You cannot save before that point ("The campaign can be saved once it has started.").

## Seeds

- **The same seed makes the same world** with the same game, War Sails and module version. Each world is identified by a content id that covers its source, its compiled data and the rules it was made with. The same seed under another game or War Sails version therefore makes a new world.
- **Different seeds make different worlds**: different coasts, climates, peoples and routes.
- **Only seed 42 starts a campaign today.**
  - Most seeds fail the generator's quality checks, which reject a whole world when one town cannot be placed or reached. The game then shows "No playable world for this seed", with an offer to try another. An earlier build's one-attempt search rejected 30 of 31 seeds; from that, a random seed is estimated to fail about 9 times in 10. On this build only seeds 42, 43 and 73 were tried.
  - **Seed 73 passes generation and preview, but its campaign is refused when you press Start Campaign.** Its villages cannot be bound to their towns. By the code, the message is "The campaign could not be started in this world. The attempt was undone". That message was not observed in a run.
  - Making more seeds playable is follow-up work.

## The world you get

**One generated globe, shown as a flat map that wraps from east to west.**
- Sailing or riding off the east edge brings you in from the west. The camera, the map labels and clicking work across that edge, and so do saves.
  - Travel, pursuit and fleets across the edge, with a save and load, were tested on seed 42's world.
  - The camera and clicking were tested on a small test world only.
  - Dragging the map across the edge was not tested.
- North and south do not connect. The polar bands are ocean, and east-west distances grow toward the poles (twice the true distance at 60° latitude).
- Near the east-west edge a few things still use flat positions:
  - distance-sorted lists, some menu texts and map step sounds;
  - siege machines, which are drawn only on their settlement's side of the edge;
  - a storm crossing the edge can miss one hour of damage.
- A naval battle, a siege or a raid exactly at the edge has not been tested.

**Peoples and cultures.** The world's peoples are generated. Each is shown through one of the game's seven cultures:

| Generated people | Shown as |
|---|---|
| maritime humans, gnomes | Empire |
| desert humans | Aserai |
| cold-climate humans, tidekin | Nord |
| large-island humans | Khuzait |
| rainforest humans, elves | Battania |
| heartland humans, hill dwarves | Vlandia |
| dwarves, frosthold dwarves | Sturgia |

**Kingdoms and clans.**
- Every generated realm that holds a town becomes a kingdom, ruled by "House of {realm}", with vassal clans named after their leaders.
- All kingdoms start at peace.
- Banners, policies and titles come from the game's own sets.

**Settlements.**
- Town and kingdom names come from the generator. Village and castle names are made from their town's name.
- Seed 42's world has 45 towns, 26 castles, 215 villages, 30 port towns, 80 harbour villages, 72 hideouts, 40 kingdoms, 92 clans and 327 heroes.
- There is no 24-town limit. The generator caps only an explicit settlement-count request, which this module never makes. The number of towns follows the land: 45 for seed 42 and 66 for seed 73 at this world size. The generator's packing allows roughly 15 to 103.

**What is reused from the game.**
- Characters, troops, equipment and buildings, and mission scenes: 370 installed battle, town and naval scenes, chosen per place.
- On the campaign map every town and castle uses the same keep model, and villages use the empire village models.
- Snowy land uses steppe battle scenes, because the game has no snow battle scene. Snow weather works; snow on the terrain does not.

**Nothing from Calradia survives.** No official settlement, clan, kingdom or position is left in a generated campaign. The story mode and the War Sails storyline are not active.

**Tested so far.**
- Trade, recruitment, wars, ownership, successions, siege preparation, land battles and hideouts.
- Ships: buying, voyages, landing, a blockade, a pirate battle and a raid on a coastal village from the sea.
- Saving and loading, including across the east-west edge.
- Not yet tested:
  - long campaigns, beyond about a day and a half of game time;
  - village raids by land, fights in a lord's hall and port visits;
  - boarding, ship damage, and naval or field battles fought to the end (every battle in the tests ended by retreat).

## Where your worlds and saves are

| What | Where |
|---|---|
| Saves | `Documents\Mount and Blade II Bannerlord\Game Saves\*.sav`, as for any campaign |
| Worlds (immutable) | `%LOCALAPPDATA%\MNBL.ProceduralWorld\worlds\` (world packages) and `bundles\` (each world's data) |
| Activation records | `%LOCALAPPDATA%\MNBL.ProceduralWorld\activations\` |
| Navigation caches | `%LOCALAPPDATA%\MNBL.ProceduralWorld\navigation-caches\` (rebuilt when missing, about 7 minutes) |
| Failure reports | `%LOCALAPPDATA%\MNBL.ProceduralWorld\diagnostics\<reference>\` (the newest 100) |
| Temporary jobs | `%LOCALAPPDATA%\MNBL.ProceduralWorld\jobs\` (removed when finished) |
| Each world's map | `<Bannerlord>\Modules\MNBL.ProceduralWorld\SceneObj\mnbl_world_<id>\` (rebuilt from the world's data in about 15 s when missing) |
| Module log | `%LOCALAPPDATA%\MNBL.ProceduralWorld\module.log` |

**A save knows its world.** The world's identity is written into the save itself (its header), not into a file beside it. Renamed and copied saves, autosaves, quick saves and Ironman saves all keep it.

**Back up a campaign.**
- Keep the save **and** its world: the `worlds`, `bundles` and `activations` folders of the store.
- The repository's `tools/Transfer-WorldSave.ps1` (`-Action export`, `verify`, `import`) packs and unpacks exactly that. It is a developer tool and is **not in the player package**, although a load refusal names it.
  - Its export and import were tested into an empty store on the same PC and checked by inspection.
  - **A transferred campaign was not loaded in the game.**
- Copying those folders by hand is the same content, but that was not tested as a procedure either.

**Deleting worlds** is not supported. Nothing in the module deletes a world. A world deleted by hand cannot be regenerated for its saves: they are refused as "This campaign's world is missing".

## Compatibility

- **Vanilla saves** are passed to the game's own loader unchanged. Loading one with the module enabled was not tested.
- **While the module is enabled**, a generated save can only be opened through its loader. Any other path refuses it, and nothing is loaded.
- **Before anything is loaded**, a generated save is checked against its world, the game and War Sails versions, the save contract and the module:
  - A different world, game or War Sails version, or save contract is refused, and the game is not simulated.
  - A different module build is not refused outright. The world is rebuilt and compared with what was saved.
- **A module update** may load an older campaign, but only when it rebuilds that campaign's world exactly as saved. Otherwise the campaign is refused as "This campaign's map cannot be rebuilt". An update that changes the save contract gives "This campaign needs another version". Either way you need the module version the campaign was made with. There is no save converter.
- **Rolling back a module update.**
  1. Remove the `Modules\MNBL.ProceduralWorld` folder.
  2. Extract the older version's zip into `Modules\`.
  3. Leave the store and the saves alone.

  Campaigns made with the newer version then load only if they rebuild exactly and share the save contract; otherwise they are refused as above. The first start or load after the change rebuilds the navigation caches (about 7 minutes). Rolling back to an older version has not itself been tested: only removing and reinstalling the same version has.
- **Other mods.**
  - A mod that changes the game's campaign XML stops a generated campaign from starting.
  - A mod that changes the battle-scene table stops one from loading.
  - Both are refused before play (fail-closed). Other combinations were not tested.

### When a load is refused

| Title shown | Code | What happened | What to do |
|---|---|---|---|
| This campaign's world is missing | `missing-world` | The save's world package or bundle is not in the store | Restore the store folders from a backup, or import the export the campaign came with |
| This campaign's world is damaged | `damaged-world` | The world's files fail their checks | Restore them from a backup or an export |
| This save is damaged | `damaged-save` | The save file is torn or unreadable | Load an earlier save of the campaign |
| This campaign needs another version | `incompatible-build` | Another game or War Sails version, another save contract, or War Sails missing | Load it with the versions it was made with |
| This campaign's map cannot be rebuilt | `not-rebuildable` | This module version rebuilds the world differently, or the map cannot be written (read-only game folder) | Use the module version it was made with; make the game folder writable |
| Not a generated campaign | `not-generated` | The save is a normal campaign | Load it as usual |
| Not now | `busy` | Another load, generation or campaign start is in progress, or the campaign is not on its map | Wait, or return to the campaign map |
| This save is in use | `save-unavailable` | Another program holds the save file | Close it and load again |
| The campaign could not be loaded | `load-failed` | Something unexpected; saves and worlds were not changed | Note the details and report it |

Most refusals come before anything changes, so a campaign you were playing keeps running on its map. That is how every refusal in the tests behaved. Only a failure after the running campaign has already been closed for the load leaves you at the main menu, and the message then says so. The load messages are in English only.

## Troubleshooting

Every failure on the setup screen shows a **Reference** (for example `MNBL-20261018-163012-ab12cd`). Its details are in `%LOCALAPPDATA%\MNBL.ProceduralWorld\diagnostics\<reference>\error.json`. **Details** shows the category, the code and the folder. Generate is always available again after a failure.

| What you see | Codes | Cause | What to do |
|---|---|---|---|
| Something required is missing | `host.war_sails_missing`, `host.tool`, `generation.tool`, `generate.producer_changed` | War Sails is not enabled, or the module's generator files are missing or changed | Enable War Sails; reinstall the module from its zip |
| This version does not match | `host.game_version`, `host.war_sails_version`, `generate.changed_inputs`, `request.mismatch`, `resume.other_generator`, `data.rejected` (version problems) | The game or War Sails is not v1.4.8.119303 / v1.2.8, or an unfinished world came from another module version | Install the versions the module was made for, or a module made for your game; discard the unfinished world |
| No playable world for this seed | `generate.exhausted`, `generation.quality_exhausted` | Every attempt failed the quality checks: common today (see Seeds) | Try another seed; seed 42 works |
| The generator refused these settings | `generate.refused`, `generate.request_refused`, `generation.refused`, `request.refused` | The generator refused the request. **By the code, a store path your system's code page cannot spell also lands here** (`unrepresentable_path`), for example a Windows user name in CJK letters; this was observed in the path test, not on the screen | Try another seed. For the user-name case there is no fix in this version |
| Not enough disk space | `generate.disk_full`, `generation.disk_full`, `request.disk_full`, `compile.failed` or `compile.travel_rejected` (disk) | The disk filled while writing the world | Free space and generate again. The budget's provisional figure is 768 MB free before generating; it is not checked in advance |
| The generated world could not be read | `generate.crashed`, `generate.exit_<n>`, `generation.producer_*`, `generation.reader_failed`, `generation.failed`, `generate.split_missing`, `compile.failed`, `compile.travel_rejected`, `data.rejected`, `job.failed` | The generator or the data step stopped, or the world's files could not be written. **A read-only game folder also lands here**, with "Access to the path …\SceneObj\… is denied" in the details | Generate again. If it repeats, reinstall the module, and make sure the game folder is writable |
| The game could not build this world | `runtime.navigation_refused`, `runtime.settlement_entities`, `runtime.playability`, `runtime.<error>`, `generate.engine`, `activation.failed` | The game could not build the map, travel or the campaign for this world. By the code, seed 73's refused start gives `activation.failed` (not observed on the screen) | Generate again, or try another seed; check that the game and War Sails are up to date |
| Generation was interrupted | `generate.cancelled`, `generation.cancelled`, `generation.busy` | You cancelled; or an earlier world is still being finished; or another running copy of the game holds the store | Generate again; close any other running copy of the game |
| A world from an earlier session was not finished | | The game closed during a generation | **Continue it** or **Discard it**. Only discard is offered when it came from another module version, stopped while its campaign was starting, or cannot be read |
| The campaign started, but its first save could not be confirmed | `activation.unresolved` | The game stopped while writing the first save | Restart the game: it checks the campaign at start-up and keeps or undoes it |
| The game does not respond for minutes after Start Campaign, or on a load | | The first start of each world, and the first start or load after a module update, prepares navigation for about 7 minutes | Wait. Later loads of the same world take about 15 s |
| New Procedural Campaign is greyed out | | A world is being generated or released | Wait a moment |

## Known limits

All of these are listed with their evidence in `evidence/one-shot/acceptance-index.json` and `docs/packaging.md`.
- **Seeds:** only seed 42 makes a playable world today; world size 129 only.
- **First start:** about 7 minutes without response, for each new world and after each module update.
- **Folders:** a read-only game folder cannot make or load worlds, and a Windows user name outside the ANSI code page cannot generate.
- **Untested:** the launcher, other mods, long campaigns and many seeds. Performance was measured on one PC only.
- **Edge of the map:** the few flat-position leftovers described under The world you get.
- **Backups:** the backup and transfer tool is a developer tool, not part of the package, and a transferred campaign has not been loaded in game.
- **Without the module:** do not load a generated campaign's save while the module is disabled or removed.
