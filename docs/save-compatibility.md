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
- **Another module build** (module version, assembly hash): the campaign is rebuilt from the frozen bundle, and its documents, coordinate frame and climate must be the saved world's (MMR-49).
- **Another backend or producer.** A new generator build makes new worlds with its own producer lock. It reads an old world's bundle as it is, and must re-encode its scene to the package's bytes.
- **A save made before MMR-48** has no build keys. Its header's game version (`ApplicationVersion`) and War Sails version (`Module_NavalDLC`, absent when the save was made without it) are compared instead (`LoadPolicy.LegacyBuild`), and its reconstruction is verified the same way.

**Rebuildable derived cache.** As supported, but a derived resource must be rebuilt first from the frozen world, and only by a tested deterministic adapter:
- **A swept scene** is re-encoded from the bundle by the surface encoder. It is installed only when every file equals the package's scene files: MMR-49's cold run, 14 s.

  This is tested when the build is the one that encoded the world: the save's module, assembly and backend equal the running ones. Under another build, or for a save that recorded none, the encoder may encode differently, so the row is marked **conditional**: the re-encoding is verified at load and refused (`not-rebuildable`) if it differs. Confirming run 24328 shows a conditional rebuild of a pre-MMR-48 save verified and loaded.
- **The navigation and playability reports** are restored from the copies the campaign's activation kept (`activations/<instance>/evidence/`).
- **The settlement layout** is re-derived by the encoder with the scene, and checked against the kept one.
- **A new navigation cache key** means the engine rebuilds its caches from the frozen navmesh.

**Unsupported world.** Refused, with the refusal the loader gives:

| Case | Code |
|---|---|
| no save, a torn header, torn game data, a malformed binding or campaign instance, a header naming another world (scene, seed) than its package | `damaged-save` |
| a header or game data another program holds | `save-unavailable` |
| another binding contract, save id version, identity schema or game version | `incompatible-build` |
| **a War Sails save on a game without War Sails active**, or one needing a War Sails version that is not installed (for a save made before MMR-48, the versions its header records) | `incompatible-build` |
| no world package, no data bundle | `missing-world` |
| a package or bundle whose bytes are not their hashes; a package manifest without its scene, seed, scene files or documents | `damaged-world` |
| a bundle built for another game or War Sails version, or needing other capabilities | `incompatible-build` |
| an installed scene that differs from the package's; a report neither installed nor kept; a settlement layout of another world; a fixture world's missing scene | `not-rebuildable` |
| a world file another program holds | `load-failed` (nothing changed) |

The order is the loader's: the save, its build, the campaign instance, the package, the bundle, then the derived resources. Every report says `migration: none`.

The loader also runs the navigation and playability gates and rebuilds the campaign. The report lists those as verified at load; it does not predict them.

**Source against native cache.**
- **A missing data bundle is a missing world, never a cache miss.** The bundle holds the world's definitions, and a world is never regenerated from its seed.
- **Derived caches are rebuilt only from the frozen bundle**, through the adapters above.
- **Native data is never carried over.** Nothing is read from another build: no scene, navmesh or cache of another encoding. A scene name carries its encoder, template and navmesh identity (`SceneNames.ForWorld`).

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
