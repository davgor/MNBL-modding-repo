# Save binding: every save kind, the world reference index, moving saves (MMR-62, MMR-48)

A generated campaign's saves name their world before anything deserializes. MMR-62 promoted MMR-19's proof (R05) into production:
- every save header binds the campaign instance to its immutable world package and data bundle;
- the initial save is published through a journal, so an interrupted publication recovers without an ambiguous or dangling save;
- an index of save references fails closed;
- the only cleanup is abandoned staging.

MMR-48 extends it to every later save and separates the immutable world from the mutable campaign:
- Save As, quick saves, autosaves and Ironman saves are journaled through the same journal, read back bound to the exact world, and
  recovered after a crash (see Later saves);
- every save header also records the build that wrote it: contract, module, game, War Sails, backend, navigation cache and save id
  versions (see What a save records);
- immutable world content is stored once and counted by every save and campaign that uses it; mutable state lives only in the save (see
  Immutable worlds and mutable campaigns);
- a save can be exported with its world and imported into another store without regeneration (see Moving saves with their world);
- the module's fixed save definitions are kept in a versioned ledger with a regression fixture (see The save definition ledger).

| Piece | File |
|---|---|
| Binding in a save header | `Persistence/WorldBinding.cs` (written by `CampaignBootstrap/WorldIdentity.cs` through `WorldIdentityBehavior`) |
| Initial-save journal and recovery | `Persistence/EarlyWorldBindingStore.cs`, `Persistence/DurableFile.cs`, `Persistence/SaveFileCheck.cs` |
| Engine adapters | `Persistence/BannerlordSaveCatalog.cs` and `Persistence/SaveFolder.cs` (the game's save list, cross-checked), `Persistence/InitialSavePublisher.cs` (the engine save), `Persistence/SaveBindingRuntime.cs` (store lock, writer admission, startup pass) |
| Reference index | `Persistence/WorldReferenceIndex.cs` |
| Staging cleanup and store lock | `Persistence/StagingCleanup.cs`, `Persistence/StoreLock.cs` |
| Offline tests | `tests/Persistence/EarlyBindingTests.cs`, `tools/Test-EarlyBinding.ps1` → `evidence/persistence/early-binding-tests.json`, `.txt` |
| In-game check | `Diagnostics/InitialSaveProbe.cs`: `generated_campaign` with `initialSaveProbe=1`, then `save_recovery` in a new process → `evidence/persistence/initial-save-recovery.json` (`tools/summarize_initial_save.py`) |
| Review | `evidence/fwg/save-binding-review.md` |
| MMR-48: later saves | `Persistence/SaveJournal.cs` (journal and the shared readback rules), `Persistence/WorldSaveBinding.cs` (the engine hook, the compatibility keys) |
| MMR-48: header contract | `Persistence/SaveBindingContract.cs`, `worldgen/contracts/save-binding.schema.json` |
| MMR-48: moving saves | `Persistence/WorldSaveTransfer.cs`, `Persistence/FolderSaveCatalog.cs`, `tools/Transfer-WorldSave.ps1` (`tools/WorldSaveTransfer/Program.cs`) |
| MMR-48: save definition ledger | `worldgen/contracts/save-type-ledger.json`, fixture `tests/Persistence/fixtures/save-type-ledger.v1.json` |
| MMR-48: tests | `tests/Persistence/SaveBindingTests.cs`, `tools/Test-SaveBinding.ps1` → `evidence/persistence/save-binding-tests.json`, `.txt` |
| MMR-48: in-game check | `Automation/SaveBindingProbe.cs` (`save_binding`) → `evidence/persistence/save-binding.json` (`tools/summarize_save_binding.py`) |
| MMR-48: review | `evidence/fwg/save-binding48-review.md` |

The store's pure parts use no game types. The game's save list is an `ISaveCatalog`, so the boundary matrix runs offline against a scripted list and in game against the real one.

## The binding

**The hook is R05's, unchanged.**
- `WorldIdentityBehavior` adds `mnbl.world.*` entries through `CampaignEvents.CollectMetadataEntriesEvent`.
- `SaveHandler.GetSaveMetaData` dispatches that event for every save mode, so every save carries them (autosaves and Ironman saves included).
- At the main menu, with no game, `MBSaveLoad.GetSaveFiles` reads every header without deserializing.
- No sidecar is used (MMR-48 decision, below). MMR-48 adds a journal around later saves, not a second binding.

**The binding needs no saveable types.** It is header strings only. The module's save type id base is reserved by MMR-30 (`Campaign/CampaignIdentity.cs`, `SaveIds`).

**MMR-62 adds two keys and changes one:**
- `instance`: a campaign instance id (32 hex digits), drawn when the world is created.
- `contentId`: the data bundle's `worldContentId`, or `none` for a fixture world. `WorldIdentity.Build` takes it as a required argument: MMR-45 passes the bundle's id, and the probes pass `none`.
- `package`: now the package directory's name. MMR-19 wrote the absolute path, which held the user name.

**Resolving a package.** A package is resolved by world id and manifest hash: `<store>/worlds/<id>-<manifest12>`, through `GeneratedWorldPackage.Resolve`. That function verifies every hash and fails closed.
- A path in an older header is never used.
- An MMR-19 header (no instance) still names its package. It reads as a legacy binding.
- A malformed binding refuses: a bad id, manifest hash, instance or content id.

The world identity now has 16 keys. MMR-19's save probe compares all of them with its oracle. MMR-63's wrap probe resolves the package from the header and checks the coordinate policy. A generated save is never regenerated from its seed on load: the package and the scene it pins are the inputs. MMR-49 owns the load.

## Publishing the initial save

`EarlyWorldBindingStore`, driven in game by `InitialSavePublisher`, works in three steps.

1. **Begin.** Nothing is written unless every check passes:
   - The binding has an instance.
   - This process holds the store lock.
   - The world package is published and verifies. A binding that names a data bundle also needs that bundle to verify (`WorldDataBundle.OpenForSave`).
   - The reserved name is a valid file name and no save has it. The reserved name is `<worldId>_start_<instance12>`. In an Ironman campaign it is `SaveHandler.IronmanModSaveName` (`Ironman` + `UniqueGameId`), because `SaveHandler.SaveTick` saves an Ironman campaign under that name whatever name `SaveAs` was given (IL_005f).

   When they pass, Begin writes the intent durably to `<store>/saves/journal/<instance>.json`: instance, world, manifest hash, content id, package and save name. One journal per instance.
2. **The engine saves.** `SaveHandler.SaveAs` queues the save. `SaveTick` runs it on the engine's `AsyncFileSaveDriver`, the driver `Module.Initialize` installs, which writes the file with `FileDriver.Save` on a background task. `OnSaveOver` is called after the write.
   - A temporary `ISaveManager` waits for `OnSaveOver` and forwards it to the sandbox's own.
   - While it is installed, autosave is off.
   - A timeout gives up only when nothing is queued (`SaveHandler.IsSaving`), so a queued save never lands under an abandoned journal.
3. **Complete.** The save is read back from the game's save list, and the store trusts that read-back, not the engine's report.
   - The header must carry this instance's binding.
   - The file must be whole: `SaveFileCheck` walks the installed save format to its end.
   - The file is flushed to the disk (`FlushFileBuffers`).
   - Then the journal says `published`, recording the game's `UniqueGameId`.

   MMR-45's activation may report success only on `published`. This is criterion c1: a durable, discoverable binding exists before activation returns.

**Durable writes.** `DurableFile` writes a journal through to the disk, flushes it, then renames it over the old one in a single same-volume rename. A crash leaves the old journal or the new one, plus at most a temporary file, which is staging.

**Why the file is checked whole.** `FileDriver.Save` writes the file once with `File.WriteAllBytes`: an Int32-prefixed JSON header, then raw deflated `GameData`. A write cut short can leave a readable header in front of torn data. `SaveFileCheck` inflates the data and walks it to its end:
- header bytes;
- the objects;
- the containers;
- the strings;
- with nothing after them.

The check has three outcomes:
- whole;
- torn: the bytes are there and wrong;
- unchecked: the file could not be read, for example because of a sharing violation, a permission error, or a cloud placeholder.

Only a torn file counts as a torn write.

## Recovery

Recovery is the same after a crash as after a reported failure. `SaveBindingRuntime.RunStartup` runs once at the main menu, before the player can open the save list and before the initial screen handles `/continuegame`, from `SubModule.OnBeforeInitialModuleScreenSetAsRoot`. It does four things:
- takes the store lock;
- resolves every `intent` journal;
- removes abandoned staging;
- summarizes the index.

Its report goes to `<store>/saves/startup-<pid>.json`, and the newest ten are kept.

| Found under the reserved name | Journal | Action |
|---|---|---|
| no save | `abandoned` | none; the world package stays |
| a header that cannot be read (torn write) | `removed` | deleted through `MBSaveLoad.DeleteSaveGame` |
| a readable header with no binding, or a malformed one | `removed` | deleted (an unbound save is one the vanilla loader would take) |
| a header bound to this instance and world, with torn game data | `removed` | deleted |
| a whole save bound to this instance and world | `published` | flushed (the crash came after the engine wrote it) |
| a save bound to another instance or world | `conflict` | kept: a bound save is never deleted |

**Why deletion is safe.** A save under the reserved name is this publication's own. The name was free when the intent was written, and Begin refuses a taken name. `FileDriver.Delete` reports success even when the file stays, so the store reads the list again before it journals `removed`.

**When an intent stays unresolved.** The intent stays, and is retried at the next start, when any of these happens:
- the save list cannot be read;
- a deletion does not take;
- the game data could not be read;
- the flush fails.

Meanwhile nothing is deleted, and the index counts the pending publication as a reference. Recovery is idempotent.

**Validating journals.** A journal is read only if all of these hold:
- its instance is its file name;
- its save name is a plain file name (so it never addresses a file outside the save folder);
- every history row has a state, a time and a note;
- its state is its last row's.

Otherwise it is unreadable. That is reported, it is a gap in the index, and it is never acted on.

## The save list, read so that a failure shows

The game's own listing hides a folder it cannot read. In 1.4.8, `PlatformFileHelperPC.GetFiles` catches the error and returns no files.

`BannerlordSaveCatalog` therefore reads the `Game Saves` folder again with System.IO (`SaveFolder`), which throws on an error, using the game's own `*.sav` pattern:
- **Listing:** the game's list must name exactly the saves on disk. A folder that cannot be read, or a difference between the two lists, throws: a gap for the index, an unresolved intent for recovery.
- **Lookup:** looking up one save requires the folder and the game to agree on whether it exists.
- **No folder:** a missing folder (no save made yet) is empty.

## The reference index

`WorldReferenceIndex` lists which saves, pending initial saves and active campaigns name which world package, and which data bundle through the content id.

**It fails closed.** Each of these is a gap:
- a save whose header cannot be read, since it might name any world;
- a malformed binding;
- an unreadable journal;
- a save list that cannot be read, or that differs from the folder.

While there is a gap, no world is reported deletable (criterion c3).

**Every save kind is bound (MMR-48).** Autosaves, quick saves, Save As and Ironman saves are now journaled (see Later saves), and a renamed or copied save carries its header, so `SaveKindsBound` is true: a complete index decides retention by its references alone. A later save in flight (its journal's intent) is a `pending save` reference, and an unreadable later-save journal is a gap. Nothing deletes a world in MMR-48; `Decide` is what a deletion would have to ask. Under MMR-62 the flag was false and `Decide` always retained.

**Reference counts.** `Counts` gives each package and each data bundle (content id) its references by kind (saves, pending, active) and its distinct campaign instances. The startup report carries them.

**Both journals are required.** With every save kind bound, an index built without the initial-save store or without the later-save journal records a gap for each, so its decisions fail closed (MMR-48 review).

**The active campaign is supplied by the caller.** The startup pass has no active campaign. MMR-45 (activation) and MMR-47 (inspection and cleanup) pass the active binding to `Build`.

## The store lock and who may write

`<store>/store.lock` is opened without sharing by `SaveBindingRuntime`. The system releases it when the process ends, including on a crash. Only the process holding it may write to the store:
- `RequireWriter` guards the generation coordinator (`GenerationCoordinator.Admission`, which refuses a job before its directory exists).
- It also guards the encoder staging (`RuntimeWorldBackend.Prepare`) and the package writer (`GeneratedWorldPackage.Write`).
- `EarlyWorldBindingStore.Begin` checks it too.

A second game process therefore cannot generate or publish while the first holds the store. So the lock holder's recovery and cleanup never meet another process's live staging or in-flight publication.

Probes that run the worldbridge directly (the developer menu entries and harness probes) are not guarded. The harness runs one game at a time.

## Staging cleanup

`StagingCleanup` removes only what a writer abandons by crashing:

| Staging | Writer |
|---|---|
| `bundles/.staging-*` | worldbridge bundle publication (`docs/world-bundle.md`) |
| `worlds/*.tmp-*` | `GeneratedWorldPackage.Write` |
| `world-surfaces/<32 hex>` | `RuntimeWorldBackend` encoder staging (`Guid` names) |
| `saves/journal/*.tmp-*` | `DurableFile` |
| `saves/journal/later/*.tmp-*` | `DurableFile` (MMR-48's later-save journals) |

**Never touched:**
- published bundles;
- world packages;
- job records under `jobs/` (MMR-47 resumes or discards an unfinished job from them);
- installed scenes;
- saves;
- any other store data.

**Safety rules:**
- It runs only while this process holds the store lock.
- It skips anything a live job owns.
- It never follows a reparse point:
  - a staging parent that is one is not scanned;
  - a candidate that is one is kept;
  - a link inside a staging directory is removed as a link, and its target is untouched.

General cleanup and its safety tests belong to MMR-47.

## Offline tests

58/58 pass (`evidence/persistence/early-binding-tests.json`, `.txt`, `.receipt.json`):
- **The binding:**
  - keys, package name and legacy headers;
  - malformed ids, manifest hashes, instances and content ids are refused.
- **Begin's refusals write nothing:**
  - an unpublished package;
  - a taken name;
  - no instance;
  - an invalid name;
  - no store lock;
  - a second journal for one instance.
- **Publication interrupted at each boundary, against a scripted save list:**
  - **Interruptions:**
    - before the intent is durable (a torn temporary journal);
    - after the intent, before the engine save;
    - a reported engine failure with no file;
    - a torn header;
    - a whole header before torn game data, either recovered or after a reported success;
    - an unbound header and a malformed binding;
    - after the engine save (published, flushed, `UniqueGameId` recorded);
    - a normal publication (intent then published, idempotent);
    - before the outcome's rename (a complete temporary journal is ignored).
  - **Fail-closed cases:**
    - another instance's save under the name, and another world's (conflict, kept);
    - the save list unreadable, a deletion that does not take, a flush that fails, an integrity check that could not run (all unresolved, nothing deleted);
    - corrupt and renamed journals, a journal history row without a note, and a journal naming a save outside the folder (all unreadable, never acted on).
- **The index:**
  - bound, legacy and vanilla saves, pending initial saves, the active campaign, and retention through the content id;
  - a complete index still retains;
  - with every save kind bound, retention follows the references;
  - unreadable saves, malformed bindings and an unreadable list are each a gap.
- **Staging:**
  - without the lock nothing is removed;
  - every kind is removed;
  - live staging is kept until the next run;
  - sentinels are kept (a bundle, a package, a job record, journals, other data, a surface folder not named like staging);
  - a junction inside staging is removed as a link;
  - a junction candidate is kept;
  - a junction parent is not scanned.
- **The durable file.**
- **The store lock:**
  - in one process, with release on dispose;
  - across processes: held by a child process it cannot be taken, and when the child is killed the system releases it.
- **The save folder:** names; a missing folder is empty; a non-folder throws; the lists must match.
- **The integrity check:**
  - whole, torn data and a torn header, trailing data, and data that does not inflate, all on synthetic saves in the installed format;
  - a file held without sharing is unchecked, never torn.

MMR-44's job tests were rerun for the coordinator's admission: 18/18 (`evidence/persistence/generation-job-tests-mmr62.txt`).

## In game

Two processes on module `c1ef0e0d…`, using the MMR-19 fixture (seed 364610, `w42`):
- **Process 1:** `generated_campaign` with `initialSaveProbe=1`, run 33696.
- **Process 2:** `save_recovery`, run 19888, a new process.

`evidence/persistence/initial-save-recovery.json` records all 15 checks passing. The pre-review pair, runs 27964 and 3868, is kept as history.

**Process 1:**
- **Initial save.** It was published in 0.34 s. The journal went intent then published, and the save read back whole and bound to the instance. The index (83 saves listed, 41 bound, complete) found it naming the package, and the package is retained.
- **Ironman.** With Ironman mode on, the publication reserved and published `Ironmanwkz750n21Zfx` (`Ironman` + `UniqueGameId`).
- **Refusals.** An unpublished package and a taken name were refused, with no journal.
- **Planted boundaries:**
  - C: intent, no save.
  - D: a torn header.
  - T: a whole header before torn data.
  - E: a foreign unreadable save with no journal.
  - Staging of every kind, plus sentinels.
- **Crash.** Save B was saved by the engine, and the process was killed before the outcome was journaled.

D and T were built from the published save's bytes: D is the length prefix and half the header, and T is the header rebound to T, followed by half the data. The game listed D and E as unreadable, and read T as a whole header with torn data.

**Process 2.** The game reported the previous session's unexpected shutdown (its Safe Mode dialog, dismissed by the harness). The startup pass, holding the lock in 0.08 s, resolved:
- B: `published`, flushed, recorded as "recovered at startup";
- C: `abandoned`;
- D: `removed`, the save deleted;
- T: `removed`, the save deleted.

It left the published initial and Ironman journals untouched. It removed the four planted staging items and kept the bundle, package and job sentinels.

The probe then confirmed:
- the store lock cannot be taken a second time;
- the foreign save was kept;
- the index reported it as a gap, with an unreferenced package retained because discovery was incomplete;
- a second recovery changed nothing.

After deleting the foreign save:
- the index was complete;
- the unreferenced package was still retained (MMR-48);
- with every save kind bound, it would not be retained, while the world (three bound saves) still would.

The probe then removed its own saves, journals, staging and sentinels.

## Later saves (MMR-48)

**The hook.** The installed `SaveHandler.SaveTick` runs every save mode through `MBSaveLoad.OverwriteSaveAux` with its final name, before `Game.Save` writes the file (1.4.8 IL):

| Mode | Name |
|---|---|
| Save As (`SaveAs`) | the given name; in Ironman, `IronmanModSaveName` |
| Quick save (`QuickSaveCurrentGame`, the escape menu's Save) | `MBSaveLoad.ActiveSaveSlotName`, else the next free `saveNNN` |
| Autosave (`TryAutoSave`, `ForceAutoSave`) | `saveauto1`..`saveauto3`, in turn |

No event carries a quick save's or an autosave's name before the write: `OnBeforeSave` and the metadata event run before `MBSaveLoad` picks it. So `WorldSaveBinding` patches `OverwriteSaveAux` with an observe-only Harmony prefix (`mnbl.proceduralworld.saves`, registered in `GeneratedPatches`). The prefix writes the journal's intent and the save runs unchanged. The kind comes from `SaveHandler`'s queued `SaveArgs.Mode`, or Ironman. The prefix is applied with the world identity behaviour when a generated campaign starts or loads, and removed when it ends.

**The journal.** `SaveJournal` reuses MMR-62's journal: the same states, durable writes (`DurableFile`), readback rules (`SaveReadback`, now shared by both), startup pass and validation. It keeps one record per save under `<store>/saves/journal/later/<id>.json`.
1. **Begin**, in the prefix. Nothing is written unless every check passes: the binding has an instance, this process holds the store lock, and the name is a plain file name. The intent records the kind, instance, world, content id, name, and the size and write time of the file that had the name.
2. **The engine saves.** `OnSaveOver` follows the write. It also comes when the game refused the save, with an empty name: `OverwriteSaveAux` refuses when the maximum number of saves is reached.
3. **Complete**, on `CampaignEvents.OnSaveOverEvent` (the world identity behaviour listens). The save is read back from the game's save list and the outcome follows `SaveReadback`, whatever the engine reported.

The initial save is skipped by the prefix: its publication owns its journal.

**What is different from the initial save.** A later save's name is not reserved: a quick save or an autosave overwrites whatever has the name. So the intent records the file that had it, and only a file changed since then can be this save's write:

| Found under the name | Journal | Action |
|---|---|---|
| no save | `abandoned` | none |
| the file unchanged since the intent (same size and write time) | `abandoned` | kept: the engine never wrote it |
| a changed file whose header cannot be read | `removed` | deleted: this save's torn write |
| a changed file with this campaign's header before torn game data | `removed` | deleted |
| a whole save bound to this campaign and world | `published` | flushed |
| a changed file that is unbound, malformed or bound to another campaign | `conflict` | kept: never provably this save's |
| a file whose header could not be opened (another process holds it), or whose change or game data cannot be told | intent stays | nothing deleted; retried at the next start |
| a file written more than ten minutes after the intent, or a newer journal under the same name | `abandoned` | kept: another save's (superseded) |

A save the game overwrote in place (`File.WriteAllBytes`) and that a crash tore is lost either way: the engine writes in place. The journal makes sure the torn remains are removed, instead of staying a permanent gap in the index.

**A header has three outcomes (MMR-48 review).** The game's header reader opens a save sharing only read and returns null on any error, so a file another process holds (a sync or backup client) looked torn. `BannerlordSaveCatalog.Read` reads the one header through the game's driver (`ISaveDriver.LoadMetaData`). When the game cannot read it, the file is read again sharing read, write and delete (`SaveFolder.ReadHeader`):
- bytes that do not parse, or a header without `ApplicationVersion` (which the game lists as corrupted), are torn;
- a file that cannot be opened is unknown (`SaveHeader.HeaderUnknown`), and nothing is deleted;
- a successful read is readable.

This also applies to the initial save under its reserved name.

**Recovery.** `SaveBindingRuntime.RunStartup` resolves every later-save intent after the initial saves and the activations (`laterRecovery` in the startup report). The same conditions as MMR-62 leave an intent unresolved. An intent with a newer journal under the same name is superseded: only the newest resolves the file. Resolved journals are pruned beyond the newest 64; intents never are.

**Names.** A later save's name is any plain file name the engine can write (`SaveJournal.ValidName`), wider than the initial save's reserved names. Spaces or a long name chosen in the Save As screen never leave a save unjournaled.

**A save that cannot be journaled still runs.** For example, a second game process has no store lock. The save keeps its binding, and the failure is recorded in `WorldSaveBinding.Problems`. A crash during its write leaves at worst an unreadable or torn file, which the index reports as a gap: it fails closed until the file is removed.

**The readback costs a whole-file read.** `SaveFileCheck` inflates and walks the game data after every save; see the in-game timings below.

## What a save records

Every save of a generated campaign carries two groups of header keys, written by the same metadata hook in every save mode and read at the main menu before anything deserializes.
- **`mnbl.world.*`: the world binding** (MMR-19/62, `WorldIdentity`, 16 keys): world id, seed, scene, package and manifest hash, the placement, scene file and document hashes, the periodic coordinate policy, the climate, the engine's scene and navigation mesh CRCs, the campaign instance and the content id. The package manifest hashes every package file, and the content id hashes the bundle: these are the required file hashes.
- **`mnbl.save.*`: the build that wrote the save** (MMR-48, `SaveBindingContract`, 9 keys):

| Key | Value |
|---|---|
| `contract` | `mnbl.save-binding.v1` (`worldgen/contracts/save-binding.schema.json`) |
| `module`, `moduleAssemblySha256` | the module's version and assembly hash |
| `game`, `naval` | the game version with its build number; War Sails' version or `absent` |
| `backend` | the runtime world backend and the producer lock it runs (worldbridge and encoder hashes) |
| `navigationCache` | the navigation cache key's schema and adapter |
| `saveIds` | the module's fixed save id base and version (`7910000/v1`) |
| `worldIdentity` | the schema of the `mnbl.world.*` keys |

`SaveBindingContract.Differences` names how a save's build differs from the running one. MMR-49 decides what a difference means at load; MMR-48 records and reports.

**No sidecar (decision).** A save is found and identified by its own contents, never by its file name or a file beside it. Autosave slots, Save As copies, a quick save over an older file and a file the player renamed or copied all keep their binding. There is no second file to name, discover or keep in step, and no ordering between two files to recover. The journal covers the one thing a single file cannot: a write the engine left torn.

## Immutable worlds and mutable campaigns

| Data | Where | Mutable? |
|---|---|---|
| Frozen source and compiler outputs, reconstruction recipes | `<store>/bundles/<worldContentId>` (the data bundle) | never: content-addressed and verified |
| The world package (placement, documents, climate, generator fingerprint) | `<store>/worlds/<worldId>-<manifest12>` | never: an existing package must be byte-identical |
| The installed map scene | the module's `SceneObj/<content-addressed name>` | derived from the bundle (MMR-37/45), checked against the package's `sceneFiles` hashes |
| Navigation caches | keyed by `NavigationCacheKey` (MMR-41) | derived, versioned apart from the world |
| Campaign state: owners, parties, heroes, time | the game's save | yes |

**Identical content is stored once.** Two campaigns of the same world have different instances but share the bundle and the package. The index counts both, so neither is ever reported unreferenced while a save of either exists. An import reuses a bundle or package the store already holds, after checking it is identical.

**Mutable changes never touch the world.** Saving reads the binding, never the bundle or the package for writing. The offline tests (several saves of two campaigns) and the in-game run (an ownership change, a party move and four saves) check that the package and bundle trees are byte-identical afterwards, and that the content id is unchanged.

## Moving saves with their world

`WorldSaveTransfer` exports chosen saves with the world content they need, and imports such an export into a store and save folder. `tools/Transfer-WorldSave.ps1` runs it without the game:

```
tools/Transfer-WorldSave.ps1 -Action export -Save 'My campaign','saveauto1' -To D:\exports -Name my-campaign
tools/Transfer-WorldSave.ps1 -Action verify -From D:\exports\my-campaign
tools/Transfer-WorldSave.ps1 -Action import -From D:\exports\my-campaign [-Rename 'My campaign=My campaign (moved)']
```

**An export** is a folder holding:
- the saves, `saves/<name>.sav`;
- each world package, `worlds/<id>-<manifest12>/`, once however many saves use it;
- each data bundle, `bundles/<worldContentId>/`, also once;
- each compiled campaign's activation evidence, `evidence/<instance>/`. This holds the navigation, playability and settlement layout reports its activation kept (MMR-49, export format `mnbl.save-export.v2`). A load on another computer restores them when it must re-encode the scene, and the navigation report is the engine's validation, which cannot be redone offline;
- `export.json`, which lists every file with its length and SHA-256, and each save's binding and build keys (schema `saveExport`).

Before anything is written, each save must be bound and whole, each package must verify (`PackageProblems`) and each bundle too (`WorldDataBundle.Problems`). The files are copied into a staging folder beside the destination, on the same volume. Each copy is hashed against its source and flushed. The staging folder is verified whole, then renamed to its name: an export that does not verify never stands under its name. Derived artifacts (the scene, the caches) are not exported. A compiled campaign whose activation evidence is missing is refused.

**An import** goes in this order:
1. **Verify the export:** the manifest, every file's length and hash, no unlisted file and no link, safe relative paths, every save file named by exactly one save row, each save's header against its row, the packages, the bundles, and each compiled campaign's activation evidence.
2. **Take the store lock.** A running game holds it, so the import is refused: close the game first.
3. **Check every conflict before writing anything.**
   - A save name already in the save folder is refused. An import never replaces a save; `-Rename` gives it another name.
   - A package, bundle or activation evidence file already in the store must be identical; it is then reused.
4. **Copy the content first.** Each package and bundle goes through staging the startup cleanup removes (`bundles/.staging-*`, `worlds/*.tmp-*`) and is renamed into place on the store's volume. Then each missing activation evidence file goes through a temporary file (`activations/<instance>/evidence/*.tmp-*`, also removed by the cleanup) renamed into place.
5. **Copy the saves last.** Each goes through a temporary file in the save folder, renamed to `<name>.sav` (a rename that never replaces), then read back bound and whole.

**Recoverable ordering.** A crash leaves staging, unreferenced complete content (harmless), or a temporary save file the next import removes; never a save without its world. No rename crosses volumes: data from another volume is copied into staging on the target volume first. Paths over the Windows limit (259 characters) are refused before anything is written.

**Without regeneration.** An import never runs the worldbridge or the generator. The imported save's world is the exported package and bundle, verified by hash. The inspector then reports the save loadable. Loading it is MMR-49's.

## The save definition ledger

`worldgen/contracts/save-type-ledger.json` lists the module's fixed save definitions:
- the `SaveableTypeDefiner` base and ids: base `7910000`, reserved to `7919999`, version 1; the module defines no saveable types, since a binding is header strings;
- the `SyncData` keys: `_mnblStartupIntentIds` and `_mnblStartupIntentHeroes` (MMR-34);
- the header keys (`mnbl.world.*`, `mnbl.save.*`).

They are module constants, never derived from a world, seed or campaign. Per-world generated object ids (`mnbl_<8 hex>_...`) are StringIds inside a campaign's game data and never appear in the ledger.

**Entries are append-only.** A change of a saved shape gets a new id or key, a new entry and a migration (MMR-50).

**The regression fixture**, `tests/Persistence/fixtures/save-type-ledger.v1.json`, is entry 1 frozen. `SaveBindingTests` checks five things:
- the ledger still begins with the fixture;
- `SaveIds` matches the last entry;
- no `SaveableTypeDefiner` exists that the ledger does not list;
- every `SyncData` key in the module source is a string literal and is listed;
- the header keys are the ledger's, and no world id appears in it.

## Offline tests (MMR-48)

71/71 pass (`evidence/persistence/save-binding-tests.json`, `.txt`). They run against real temporary save folders of synthetic saves in the installed format, so file sizes, write times, sharing and deletion are real.
- **Later saves.**
  - Each kind (Save As, quick save, autosave, Ironman) is journaled before its write and published after its readback, its journal valid against the schema.
  - A quick save over the campaign's own earlier save records the file it replaces.
  - A reported failure with no file, and a save the game refused before writing (empty name, the earlier file untouched), are abandoned.
  - The refusals write nothing.
  - Corrupt, renamed, unknown-kind and prior-less journals are unreadable gaps, never acted on.
  - Pruning keeps the newest 64 and every intent, and nothing without the lock.
- **The interrupted-save recovery test** (the one this ticket owns; the initial-save boundary matrix stays MMR-62's).
  - **Crash points:** before the write, over another campaign's save (abandoned, kept); a torn header, new or over another campaign's save (removed); this campaign's header before torn game data (removed); after the write, new or over another campaign's save (published); the file changed to another campaign's save or an unbound one (conflict, kept).
  - **In flight:** every intent is a pending reference.
  - **Unreadable list:** with the save list unreadable, nothing is resolved or deleted.
  - **Temporary journal:** a torn one is ignored and then removed by the cleanup.
  - **Idempotent:** a second recovery changes nothing, and the index is complete afterwards.
  - **Unknown files:** unknown change, unknown game data and an unopenable header are never deleted.
  - **Held files:** a whole save held exclusively, or held for writing as a sync client holds it, stays unresolved and is published once released.
  - **Stale intents:** they are superseded by a newer journal of the same name or by a write long after.
  - **Names:** a spaced name and a 150-character name are journaled.
- **The index.** One world referenced by two campaigns counts 3 references and 2 instances, and a save in flight keeps its world. An unreferenced world may go only with discovery complete. A renamed save is still found by its header. An unreadable save, and a missing journal source, are gaps.
- **The contract.** All 9 `mnbl.save.*` keys are present. Empty or multi-line values are refused. Differences from the running build are named. The headers and the journals, initial and later, are valid against the schema, and a bad journal is not.
- **The ledger.** Entry 1 equals its frozen fixture, and entries are numbered. `SaveIds` matches the last entry. No `SaveableTypeDefiner` exists. Every `SyncData` key is a listed literal. The header keys are the ledger's, and no world id appears in it.
- **Moving saves.**
  - **Export:** two campaigns' saves with their one shared world, against the schema, reading the store only. Refused, writing nothing: a save of no generated world, a missing save, an existing destination, a destination past the path limit, and a copy that does not verify.
  - **Verify refuses:** a changed byte, an unlisted file, a missing file, a path leaving the export, a row naming another campaign, and a save file no row names.
  - **Import:** into an empty store (copied, read back, verified, no staging left); again under the same names (refused, nothing written); under new names (reused, 4 references from 2 campaigns).
  - **Refused, writing nothing:** a store holding a different bundle under the content id, and a held store lock.
  - **Crash leftovers:** they are removed by the cleanup and the next import.
- **Immutable content (c2, regression guards).** Journaling saves of changing game states writes nothing under `worlds/` or `bundles/`, and a control shows one changed byte changes the tree hash.

MMR-62's tests still pass, 58/58 (`evidence/persistence/early-binding-tests-mmr48.json`, `.txt`), with the index expectation updated for every save kind bound. The other 16 offline harnesses pass unchanged.

`tools/Transfer-WorldSave.ps1` was also run on a real save of the per-user store (`evidence/persistence/save-transfer-real.json`).
- **Export:** the initial save of a committed seed-42 campaign, with its package and its 137 MB bundle: 902 files, 149.6 MB, in 6.0 s.
- **Imports:** into an empty temporary store in 5.0 s; again under a new name, the world reused (2 references, 1 campaign); a third time under the same name, refused.
- **Inspection:** `Inspect-World.ps1` found the bundle valid and both imported saves loadable.

## In game (MMR-48)

`save_binding` run 15972, on the fixed build: 14/14 recomputed checks passed, with status ok (`evidence/persistence/save-binding.json`, `tools/summarize_save_binding.py`).
- **The campaign.** The setup generated seed 42. Start Campaign went through character creation, and the activation committed instance `f97b15f5…`. From Start Campaign to the committed campaign took 398 s, most of it the navigation cache rebuild that follows a module rebuild. The startup pass had run the later-save recovery: nothing resolved, unresolved or unreadable.
- **Mutable changes.** A town of the world went to the player's clan, and the main party moved about 570 units.
- **Every save kind.**

| Kind | Name | Earlier file | Save | Readback |
|---|---|---|---|---|
| Save As | `mnbl_probe48_saveas_15972` | none | 0.95 s | 0.050 s |
| Quick save | the same (the active slot) | the Save As | 1.28 s | 0.049 s |
| Autosave | `saveauto1` | the player's (copied, restored after) | 1.19 s | 0.047 s |
| Ironman | `IronmanLW9DEoGlPqLF` | none | 1.20 s | 0.048 s |

  Each save behaved the same way:
  - its intent was written before the file;
  - its header named the campaign instance, the package `mnbl_1609fb72-38cfe59a…` and the content id `f596b58c…`;
  - the file was whole and the journal published;
  - its `mnbl.save.*` keys equalled the running build's: game `v1.4.8.119303`, War Sails `v1.2.8.119303`, module `v0.0.1.0`, save ids `7910000/v1`.
- **c2.** The package and the 137 MB bundle hashed the same before and after. The bundle still verified with the same `worldContentId`, and every save named it.
- **The index.** It was complete and found the campaign's four saves and the active campaign. The package had 5 references from 1 campaign. The bundle had 13 references from 9 campaigns: earlier campaigns of the same world share it.
- **Recovery with the game's save list.** A planted intent whose file held this campaign's header before torn game data was removed, and the file deleted through the game. An intent whose file was never touched after it was abandoned, and the file kept.
- **c3.**
  - The Save As and Ironman saves were exported with their package and bundle: 903 files, 153.2 MB, in 5.3 s.
  - They were imported into an empty store in 4.4 s, the world copied.
  - They were imported again under new names: the world was reused, giving 4 references from 1 campaign, and the target's index was complete.
  - The inspector found all four saves loadable.
  - Nothing was regenerated, and the temporary store was removed.
- **Main menu.** With no game loaded, before any scene or object is reconstructed, all four saves were found bound to the exact world from their headers, with all 9 build keys. The save binding had detached.
- **Cleanup.** The probe's saves and the campaign's initial save were deleted, and `saveauto1` was restored. The harness found no existing save changed and no new save left.

Run 43868, on the build before the review, is kept for the record: 13/14 under today's checks. Its transfer expectation failed only on a probe bug: a dictionary type in the check. Its cleanup output predates the review's format. It left its campaign's initial save, `mnbl_1609fb72_start_344d742323ec`, in the save folder.

## Limits

- **Initial save only (MMR-62; lifted by MMR-48).** MMR-48 journals every later save kind with the same journal.
- **No loading.** The normal menu's load routing and refusal, and the restore before deserialization, are MMR-49's. The index and the binding are what MMR-49 reads.
- **Path state after a load.** MMR-34's handoff (8 path-state values of moving AI parties re-derived on load) is a load-fidelity question, forwarded to MMR-49.
- **No world is ever deleted here.** MMR-48 makes the index's retention decision final (every save kind bound), but nothing deletes a world yet.
- **A torn in-place overwrite is lost (MMR-48).** The engine overwrites a save in place, so a crash during a quick save or an autosave loses the file it was replacing. The journal removes the torn remains; it cannot bring back the older save.
- **Compatibility keys are recorded, not enforced (MMR-48).** What a different game, module or backend version means for a load is MMR-49's decision.
- **Readback cost on late-game saves is unmeasured (MMR-48).** The whole-file check took about 0.05 s per early-game save in game. It grows with the save's size, and a large late-game save was not measured.
- **What the in-game run did not interrupt (MMR-48).** No engine write was killed midway. The torn file was planted from real save bytes in the running process, and the crash cases are offline. The Save As screen's name rules were not checked; the journal accepts any plain file name.
- **The integrity check reads the whole file.** It runs once per initial save, at publication or recovery, not for the save list.
- **Directory entries are not flushed.** The save and the journal are flushed, but after a power loss a journal's rename may be undone. That leaves the older journal, which recovery handles as the earlier boundary. Game-crash recovery is what was run.
- **What the in-game run did not interrupt.**
  - It killed no engine write midway: the torn saves were built from real save bytes.
  - The conflict, failed-deletion, unreadable-folder and flush-failure paths are covered offline only.
  - Both processes ran under the diagnostic bootstrap's save manager, not SandBox's.
- **Unguarded writers.** Probes and developer entries that run the worldbridge directly are not admission-checked; the harness runs one game.
- **Scene storage.**
  - A fixture world's scene slot still lives outside its package, named from the seed (MMR-19).
  - Production worlds use content-addressed scene names (MMR-37), and the binding records the bundle's content id.
  - Installing the scene at activation and reinstalling it at load belong to MMR-45 and MMR-49.
