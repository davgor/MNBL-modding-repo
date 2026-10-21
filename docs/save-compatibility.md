# Save compatibility and migration boundaries (MMR-50)

Before anything loads, a generated campaign's save gets one of three decisions:
- **supported**;
- **rebuildable derived cache**;
- **unsupported world**.

The same rules decide in three places:
- the read-only report, without the game (`tools/Inspect-World.ps1`) or in it (`WorldCompatibilityReport`);
- the load itself (`GeneratedWorldLoader`, MMR-49);
- the loader's pure rules (`LoadPolicy`).

There is no migration. No converter is implemented, so a save or world this build cannot read is refused, never converted, regenerated or re-identified.

| Piece | File |
|---|---|
| The policy (pure: facts from files, then the decision) | `Persistence/WorldCompatibilityPolicy.cs` |
| The report in game | `Persistence/WorldCompatibilityReport.cs` |
| The report without the game | `Persistence/WorldInspector.cs` (`Store`, each save's `compatibility`), `tools/Inspect-World.ps1` |
| The save type ledger and its converter rule | `worldgen/contracts/save-type-ledger.json` |
| Previous-save fixtures | `tests/Persistence/fixtures/previous-saves/*.header.json` |
| Offline tests | `tests/Persistence/CompatibilityTests.cs`, `tools/Test-Compatibility.ps1` → `evidence/persistence/compatibility-tests.json`, `.txt` |
| In-game matrix | `Automation/CompatibilityProbe.cs` (`compat_matrix`) → `evidence/persistence/compat-matrix.json` (`tools/summarize_compat_matrix.py`) |
| Installed save definitions | `evidence/persistence/save-definer-scan-mmr50.json` (`tools/Scan-SaveDefiners.ps1`) |

## The three classes

**Supported.** The following are all usable as they are:
- the save: header readable, binding well formed, game data whole;
- its build keys (MMR-48's `mnbl.save.*`);
- its world package and data bundle (every file with its hash);
- its installed scene, and the navigation, playability and settlement layout reports.

Some differences are reported but not refused; the load verifies them by rebuilding:
- **Another module build** (module version, assembly hash): the campaign is rebuilt from the frozen bundle and, since MMR-283, the catalog frozen in its package, and its documents, coordinate frame and climate must be the saved world's (MMR-49).
- **Other content mods (MMR-283):** only the installed objects the world references are checked, by key, element and the fields the world reads; a refusal (`content-conflict`) names the mod and the object (`docs/world-activation.md`, "Content mods").
- **Another backend or producer.** A new generator build makes new worlds with its own producer lock. It reads an old world's bundle as it is, and must re-encode its scene to the package's bytes.
- **Another game or War Sails build (MMR-284, RT-41).** The save's `game` and `naval` keys are diagnostics. A build at or above this release's minimum (`SupportedBuilds`: Bannerlord v1.4.8, War Sails v1.2.8, any change set) loads, the saved one and the running one alike, and there is no maximum: a build newer than any known at release loads. What keeps that safe is the world's catalog check (MMR-283) and every patch's signature check (`PatchSignatures`, `GeneratedPatches.Verify`), which refuse naming the object or the patch.
- **A save made before MMR-48** has no build keys. Its header's game version (`ApplicationVersion`) and War Sails version (`Module_NavalDLC`, absent when the save was made without it) are checked by the same minimum rule instead (`LoadPolicy.LegacyBuild`, `LoadPolicy.BuildCheck`), and its reconstruction is verified the same way.

**Rebuildable derived cache.** As supported, but a derived resource must be rebuilt first from the frozen world, and only by a tested deterministic adapter:
- **A scene missing from the module with its archived copy** (MMR-282: `<store>/world-scenes/<scene>`, kept for every world a package or save names) is copied back when every file equals the package's scene files. No encoder runs, so this works under any later build. Not conditional.
- **A swept scene** with no archived copy is re-encoded from the bundle by the surface encoder. It is installed only when every file equals the package's scene files: MMR-49's cold run, 14 s.

  This is tested when the build is the one that encoded the world: the save's module, assembly and backend equal the running ones. Under another build, or for a save that recorded none, the encoder may encode differently, so the row is marked **conditional**: the re-encoding is verified at load and refused (`not-rebuildable`) if it differs. Confirming run 24328 shows a conditional rebuild of a pre-MMR-48 save verified and loaded.
- **The navigation and playability reports** are restored from the copies the campaign's activation kept (`activations/<instance>/evidence/`).
- **The settlement layout** is re-derived by the encoder with a re-encoded scene, and checked against the kept one. Since MMR-282 the sweep keeps the layout report of every referenced scene, and an archived scene uses that report or the kept copy.

**What the sweep keeps (MMR-282, RT-35).** A generation's sweep (`RuntimeWorldBackend.Sweep`, `Persistence/WorldStoreSweep.cs`) used to delete every content-addressed scene this process had not used, with its settlement report and navigation caches. The next load of such a campaign then re-encoded its scene and rebuilt its caches (about 6.4 minutes), and after a WorldBridge change the re-encoding got another name and the load was refused for good.
- **Now it deletes** only the scenes, archived scenes, settlement reports and caches that nothing names. The names come from three places: every world package in the package store (`world.json` `sceneName`); every bound save, pending initial save and save in flight (`WorldReferenceIndex`: the header's `mnbl.world.scene`, and its package's); and this process.
- **It fails closed.** An unreadable save header, journal, save list or package manifest is a gap, and with a gap it deletes nothing but staging debris.
- **What it kept, and why,** is in the job's install record and `<store>/world-scenes/sweep.json`.
- **A new navigation cache key** means the engine rebuilds its caches from the frozen navmesh.

**Unsupported world.** Refused, with the refusal the loader gives:

| Case | Code |
|---|---|
| no save, a torn header, torn game data, a malformed binding or campaign instance, a header naming another world (scene, seed) than its package | `damaged-save` |
| a header or game data another program holds | `save-unavailable` |
| another binding contract, save id version or identity schema | `incompatible-build` |
| a save made with, or a game running, a Bannerlord or War Sails build below the minimum (`mnbl_load_build_saved_old`, `mnbl_load_build_running_old`; for a save made before MMR-48, the versions its header records) | `incompatible-build` |
| **a War Sails save on a game without War Sails active** | `incompatible-build` |
| a patch target whose installed signature changed (`mnbl_load_patch_signature`, naming the patch and the member) | `incompatible-build` |
| no world package, no data bundle | `missing-world` |
| a package or bundle whose bytes are not their hashes; a package manifest without its scene, seed, scene files or documents | `damaged-world` |
| a bundle built for a game or War Sails below the minimum, opened on one below it, or needing other capabilities | `incompatible-build` |
| an installed scene that differs from the package's; a report neither installed nor kept; a settlement layout of another world; a fixture world's missing scene | `not-rebuildable` |
| a world file another program holds | `load-failed` (nothing changed) |

The order is the loader's: the save, its build, the campaign instance, the package, the bundle, then the derived resources. Every report says `migration: none`.

The loader also runs the navigation and playability gates and rebuilds the campaign. The report lists those as verified at load; it does not predict them.

**Source against native cache.**
- **A missing data bundle is a missing world, never a cache miss.** The bundle holds the world's definitions, and a world is never regenerated from its seed.
- **Derived caches are rebuilt only from the frozen bundle**, through the adapters above.
- **Native data is never carried over.** Nothing is read from another build: no scene, navmesh or cache of another encoding. A scene name carries its encoder, template and navmesh identity (`SceneNames.ForWorld`).

## Map epic 2: clean break (owner decision 1, 2026-10-18)

Worlds generated, and campaigns saved, before map epic 2 **stop loading**. Nothing converts them, and no code path keeps reading their formats (findings section 12). Keeping them would need dual paths in five subsystems:
- cells.bin v1/v2 and the validator version;
- `MNBLWT01` and the 4-layer template (MMR-255 replaced both: `MNBLWT02` and the generated 15-layer template; the encoder and
  `WorldSurface` read only `MNBLWT02`);
- climate `mmr39/1`;
- the legacy `VillageBinding` and `economy.json` binding;
- the settlement builder `mmr40/2`.

Even then, a swept scene could not be rescued. So every map epic 2 change replaces its format or identity outright. The epic lands as one build with one scene rename before its live test.

**What refuses an old world**, in the order a load reaches it:
- **Generation-bound policies.** `playability.json`, `terrain-materials.json` (now version 2) and `projection.json` changed in MMR-248. A generation request bound to the old policies is refused as `policy_changed` (MMR-47), never resumed.
- **The bundle capability.** Every bundle written from MMR-248 on requires `R11`, the one capability for the epic's three format bumps:
  - cells.bin v2 `MNBLTC02` (MMR-253/254; landed in MMR-254: classes, OpenSea, land face types, the road bit. `TravelCells.Read`, `GeneratedSpawnModel.CellMap` and `RoadSpeed.Cells` refuse `MNBLTC01`);
  - weights.bin v2 `MNBLWT02` (MMR-255);
  - campaign.json v2 (MMR-252/279).

  The constant is `worldbridge::capabilities::kFormatsV2` and `WorldDataBundle.FormatsV2Capability` (`docs/biome-presentation.md`). A host without R11, an older module, refuses a new bundle at `WorldDataBundle.Open` with `incompatible-build`, before activation.
- **The formats.** A pre-epic bundle does not require R11, so the capability alone does not refuse it on a new host. The readers of the three formats do: each format ticket replaces its reader with a version-2-only one (`TravelCells.Read`, `WorldSurface`, the campaign factories), which refuses a version-1 file (`damaged-world` or `incompatible-build`). None falls back to a version-1 path.
- **The producer (MMR-261).** The pinned FWGC++ build moved to `0ae1f3e` (`fwg apply`, and the generation baseline fix that changes the magic layers, key locations and the founding definition of every world), so every world's source, ids and bundle change; every bundle also requires `source/request.json`, which a pre-epic bundle lacks (`WorldDataBundle.Open` refuses it as missing a required file).
- **Save ids v2 (MMR-261).** `SaveIds.Version` is 2: the save type ledger's entry 3 adds the live mutation's `SyncData` string (`_mnblWorldMutations`) and the header key `mnbl.campaign.mutationProducer`. A save that records `7910000/v1` is refused as `incompatible-build` (strict key `saveIds`; no converter).
- **Scene and identity.** The encoder, template, policies, climate identity (MMR-256) and `SettlementEntityFactory.Version` (MMR-258, `mmr258/1`) all change. An old campaign's installed scene no longer matches what this build encodes, so it is `not-rebuildable`. A package written before MMR-258 also has no frozen settlement looks (`looks/`); a re-encode of its swept scene refuses it as `damaged-world`.

**Live mutation and later producer updates (MMR-261, RT-37).** A campaign's live mutations are one `SyncData` string: the canonical `mnbl.world-mutations` v1 document (`docs/magic-ground.md` "Live mutation"). A load replays its stored cells only; no producer runs during a load. The campaign casts only with the producer build that generated its world (the bundle's `source.producer.binarySha256`, recorded in the document and in the header key `mnbl.campaign.mutationProducer`). The package keeps every such build by hash (`Producers/<sha256>/fwg.exe`, `worldgen/producer-archive.json`), so a later producer update leaves old campaigns casting with their own build. A new cast is refused only when that build is missing; the stored mutations always load. A document whose digest does not verify, or one of another world or request, disables only the live cells and the casts (the generated magic ground stays drawn) and is saved back unchanged.

**What this costs players.** A pre-epic campaign cannot be continued on a map epic 2 build. To finish one, keep the older module build. The store keeps old bundles and packages untouched, because bundles are never rewritten (below). The first map epic 2 release notes must say this.

**Between MMR-248 and the format tickets.** MMR-248 freezes the policies and the capability; the three formats arrive later. Until then a pre-epic bundle still opens in the offline harnesses. That keeps worlds A and B usable as test inputs until the tickets that change the compiler regenerate them. Once a version-2 format lands, its version-1 reader is gone.

## The report

**Without the game.** `tools/Inspect-World.ps1` reports every save of a generated world: its class, each component's verdict (same, verified at load, rebuilt from the frozen world, frozen, refused, not checked), the refusal and the rebuilds. It compares against the running build known without the game:
- the binding contract;
- the save id version, from the ledger;
- the identity schema;
- the game and War Sails versions;
- the backend with this producer lock.

When the module is installed under `-GameRoot`, it also checks the scene and the reports. It reads only.

**In game.** `WorldCompatibilityReport.ForSave(name)` compares against the running build itself (`WorldSaveBinding.Compatibility`).

## Migration boundaries

- **No converter is implemented.** The save type ledger lists none (`converters: []`). Its `converterRule` says that a save made under another `saveIdsVersion`, binding contract or identity schema loads only through a listed converter with a tested fixture, and that it is refused until one exists.
- **What a converter would have to do:** copy the save (never writing the original), convert the copy, verify it, then offer it.
- **No message offers a migration.** MMR-50 removed the bundle refusal's advice to "convert it with the explicit migration tool", since no such tool existed. The tests scan every message literal of the module for such an offer.
- **Bundles are never rewritten.** They are content-addressed (`bundles/<worldContentId>`), and the producer publishes a new bundle by renaming its staging into place. It reuses an existing bundle of the same content unchanged, even under new provenance, and refuses to republish over one that describes the content differently or is damaged, leaving it untouched (`src/WorldBridge/tests/bundle_transaction_tests.cpp`, run by `tools/Test-WorldDataBundle.ps1`). A new generator build therefore creates new worlds beside old ones and never alters them.

## Save type ids

The engine's rules (installed 1.4.8 IL, `TaleWorlds.SaveSystem.Definition.DefinitionContext`):
- **Discovery.** `GetSaveableAssemblies` takes the SaveSystem assembly and every loaded assembly that references it. `CollectTypes` instantiates every non-abstract `SaveableTypeDefiner` with its parameterless constructor. `FillWithCurrentTypes` runs their `Define*` methods in a fixed order: basic, class, struct, interface, enum, root class, generic struct, and so on.
- **Numbering.** A definer passes its base id to `SaveableTypeDefiner(int saveBaseId)`, and each definition its local id.
- **Collisions.** `AddClassDefinition` registers each `SaveId` with `Dictionary.Add`, so two loaded mods defining the same id throw at definition time.

The module's base is `7910000`, with `7910000`–`7919999` reserved (MMR-30), and it defines no saveable type: a world binding is header strings. The MMR-50 rescan of the installed game and Workshop modules found 153 definers and no errors, and the nearest installed base is 2,830,000 away.

The scan records base ids, not each definer's span of local ids. So before a first id is assigned, the scan must also record every definer's highest local id, to show the module's range overlaps none. A future saveable type takes ids inside the reserved range, appends a ledger entry with a new `saveIdsVersion`, and needs a converter (or refusal) for older saves.

## Evidence

**In game: `compat_matrix` 24328, 13/13 recomputed checks, status ok** (`evidence/persistence/compat-matrix.json`, `tools/summarize_compat_matrix.py`).
- **The source save** (made before MMR-48) is reported supported.
- **With its world's scene and reports swept,** it is reported rebuildable: the scene conditional, navigation and playability restored, the layout re-derived.
- **The load agrees with the report.** It went through the world loader, with only the surface encoder started. It restored exactly the reports the report named and re-derived the layout, and the bundle and package bytes were unchanged.
- **A save in this build's format** (every build key) is reported supported. Its header is checked in as `tests/Persistence/fixtures/previous-saves/mmr50-current.header.json`.
- **Four fixtures** made from that save each changed one header entry: another binding contract, another save id version, a War Sails version not installed, a corrupted package hash. Each was reported unsupported and refused by the load with the same code (`incompatible-build` ×3, `damaged-world`) before any game started. The installed load never ran, the caller's cancel ran, and every byte of the fixtures, the source saves, the bundle and the package was preserved.
- **Every baseline held.**

**Offline:**
- **`tools/Test-Compatibility.ps1`: 56/56** (`evidence/persistence/compatibility-tests.json`, `.txt`). It covers:
  - the three classes and the conditional rebuild;
  - every refusal, with its agreement with the loader (`LoadPolicy`), legacy saves included;
  - the facts from real temporary stores, including a save held open for writing, a world file held, a layout of another world, and reading only;
  - both previous-save fixtures;
  - no converter, no message offering a migration, and a real bundle built for another game version refused without one.
- **`tools/Test-WorldDataBundle.ps1`:** 41/41, and the C++ `bundle_transaction_tests` 38/38 (bundles never rewritten).
- **`tools/Test-Recovery.ps1`:** 10/10, the inspector's verdict now the policy's.
- **`evidence/persistence/save-definer-scan-mmr50.json`:** the installed save definitions.

**The real store** (`tools/Inspect-World.ps1`): 46 bound saves, all supported.

**Review:** `evidence/fwg/save-compatibility-review.md`.
