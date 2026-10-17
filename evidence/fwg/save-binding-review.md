# MMR-62 independent review (early save binding, world reference index, staging cleanup)

**The review.** One independent review of the implementation and of the first in-game pair (runs 27964 and 3868). It was done by an agent that:
- read the code;
- read the installed 1.4.8 save system through Mono.Cecil IL dumps;
- ran the offline tests;
- neither edited the repository nor started the game.

**Engine facts it confirmed in IL:**
- **The save file format that `SaveFileCheck` walks.** Saves are written with `File.WriteAllBytes`, with no flush.
- **Deletion.** `FileDriver.Delete` returns true even when the delete fails.
- **Corruption.** `IsCorrupted` is set when the metadata is null or has no `ApplicationVersion`.
- **Save sequence.** `SaveAs` only queues the save. `OnSaveOver` reads `SandBoxSaveManager` at callback time and fires after the background write.
- **Startup timing.** `OnBeforeInitialModuleScreenSetAsRoot` runs before `/continuegame` is handled.

All 15 findings were fixed in one batch. The confirming pair (runs 33696 and 19888) and the offline tests (58/58) are in `evidence/persistence/`.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | major | `SaveFileCheck` mapped an I/O or permission error to "torn", so a crash-recovered whole save whose file could not be read (a sharing violation, a cloud placeholder) would be deleted. | Three outcomes: whole, torn, unchecked. I/O and permission errors are unchecked, which leaves the intent unresolved. Test: a file held without sharing is unchecked. |
| 2 | major (latent) | The game's listing returns no files for a folder it cannot read, so the "save list unreadable" gap could never fire, and recovery would mark an intent abandoned. | `SaveFolder` reads the folder with System.IO, which throws, and the game's list must name the same saves. A lookup needs the folder and the game to agree. Tests: a non-folder throws, a missing folder is empty, mismatched lists throw. |
| 3 | major | The store lock guarded only cleanup and recovery. A process without it could still generate and publish, and the lock holder's cleanup or recovery could destroy its live staging or abandon its publication. | Only the lock holder may write. `SaveBindingRuntime.RequireWriter` guards the coordinator (`Admission`, before any job directory), `RuntimeWorldBackend.Prepare`, `GeneratedWorldPackage.Write` and `Begin`. Tests: `Begin` without the lock; coordinator admission (MMR-44 tests 18/18); the lock across processes and after a kill. Probes that run the worldbridge directly stay unguarded (documented). |
| 4 | major | Ironman campaigns save under `IronmanModSaveName` whatever name is asked for, so a publication never completed and left an unjournaled bound save. | In Ironman mode the publisher reserves and journals `IronmanModSaveName`. In game: published as `Ironmanwkz750n21Zfx` (run 33696) and left untouched by recovery (19888). |
| 5 | minor | A timeout could resolve while the save was still queued, so a bound save would land under an abandoned journal. | A timeout gives up only when `SaveHandler.IsSaving` is false. |
| 6 | minor | The save file was never flushed before "published". | The catalog flushes the save (`FlushFileBuffers`) before the journal says published. A flush failure leaves the intent unresolved (tested). |
| 7 | minor | The comments named `FileDriver` as the driver, but `Module.Initialize` installs `AsyncFileSaveDriver`. | Corrected in the code comments and the doc. Its wait-for-previous-task is what makes a read after `OnSaveOver` see the finished file. |
| 8 | minor | Cleanup followed a parent folder that was a reparse point, and treated any `world-surfaces/*` as staging. | A parent that is a reparse point is not scanned. Surface staging is limited to 32-hex names. Tests: a junction parent with a precious target, and a non-GUID surface folder kept. |
| 9 | minor | The content id was never bound or checked. | It is a required argument of `WorldIdentity.Build`: MMR-45 passes the bundle's id, and fixtures pass `none`. `Begin` requires a named bundle to verify (`WorldDataBundle.OpenForSave`). |
| 10 | minor | Hand-offs were left implicit: scene storage, resolving by id and hash, save type ids, active-campaign references. | Docs updated (`runtime-capabilities.md`, `save-binding.md`). Scene installation and reinstallation belong to MMR-45/49. Save type ids are reserved by MMR-30, and the binding needs none. The active binding is passed by MMR-45/47. |
| 11 | nit | The doc claimed the MMR-63 header check compares all 16 keys, and that the save manager is forwarded wholesale. | Corrected: only `OnSaveOver` is forwarded, and autosave is off while the manager is installed. |
| 12 | nit | A journal row without a note could abort the whole recovery. | `Read` validates every history row, and the state must equal the last row's. `LastNote` is tolerant. Test: a row without a note is unreadable and recovery goes on. |
| 13 | nit | The journal's save name was not validated on read, but is deleted by name. | `Read` applies `Begin`'s name rule. Test: a journal naming `..\outside` is unreadable and nothing is deleted. |
| 14 | nit | A startup report with the full index was written on every launch and never pruned. | The index is summarized, and the newest ten reports are kept. |
| 15 | minor | Coverage: a duplicate "outcome's write" test, a lock test within one process only, no Ironman, the probe's clean-up skipped on an early stop, and doc honesty about constructed torn saves. | The outcome test now uses a complete temporary journal. The lock is tested across processes. Ironman is published in game. The probe's clean-up runs in `finally`. The doc states that the torn saves were built from real bytes, which paths are offline-only, and which save manager was used. |
