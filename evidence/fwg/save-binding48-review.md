# MMR-48 independent review and fixes

One independent review of the MMR-48 working tree. The reviewer was an agent with read-only access. It did the following without editing
files or launching the game:
- ran the offline harnesses (`Test-SaveBinding` 64/64 and `Test-EarlyBinding` 58/58 at the time);
- compiled the module and every harness source set under C# 7.3 with warnings as errors;
- disassembled the installed engine to check the IL facts the design relies on;
- confirmed three findings with a throwaway program.

It reported no blocker, 2 major and 8 minor findings. All were fixed in one batch. The offline harnesses then ran: all 18 green, with
`Test-SaveBinding` now 71/71. One in-game confirming run followed on the fixed build: `save_binding` 15972, with 14/14 recomputed checks and status ok (see `docs/save-binding.md`, In game).

An in-game run made before the review, 43868, had already shown one probe bug, fixed in the same batch: the transfer check compared the
inspector's bundle summary (a `Dictionary<string, int>`) as a `Dictionary<string, object>` and always failed. Every recomputed check of
that run passed (14/14).

## Major

1. **A whole save could be deleted when its header could not be opened.** The game's header reader (`GetMetaDataContent`) opens with
   FileShare.Read and returns null on any error. The journal treated that as a torn write, and for a later save whose file had changed it
   deleted the file. The reviewer reproduced it: a sync client holding the newest quick save with share read/write/delete got it deleted.
   The same rule came from MMR-62's initial save. Fixed:
   - a header now has three outcomes, like the game data: readable, torn, or unknown (`SaveHeader.HeaderUnknown`);
   - when the game cannot read a header, `SaveFolder.ReadHeader` reads it again sharing read, write and delete. An `InvalidDataException`
     means torn, an I/O or permission error means unknown, and a successful read means readable;
   - `SaveReadback.Decide` never deletes on an unknown header, under any name, the initial save's included.

   Tested: a file held exclusively, and one held for writing with shared access as a sync client holds it. Each intent stays unresolved,
   the file is kept, and both are published once released.
2. **The probe could leave the player's autosave slot overwritten.** Restoring the slot and deleting the probe's saves were not in a
   `finally`, and the runner did not dispose suspended steps. Fixed:
   - cleanup is one idempotent object, run at the end and from a `finally`;
   - the runner disposes every suspended step when one throws or the run is abandoned;
   - the slot's earlier file is also kept beside the store with a record, and a later `save_binding` run restores one an interrupted run
     left (`RestoreLeftovers`);
   - the campaign's initial save is now removed too.

## Minor

3. **A listed save file without a save row passed verification, then the import crashed.** Fixed: every `saves/*` file must be named by
   exactly one row. The tool now reports any unexpected exception instead of crashing. Tested.
4. **A failed export could leave a folder under its final name.** Fixed: the staging folder is verified before it is renamed. Tested
   with a verifier that refuses the copy: no folder is left.
5. **The index's defaults failed open.** With every save kind bound, a caller that passed no journal sources got complete discovery.
   Fixed: each missing source (initial-save store, later-save journal) is a gap. The import builds the target's index with both journals,
   and the probes pass both. Tested.
6. **A stale intent could delete a torn file it did not write.** Fixed in two ways:
   - an intent with a newer journal under the same name is superseded (abandoned);
   - a file written more than ten minutes after an intent is never that save's: it is kept.

   Tested.
7. **The patch could outlive its campaign** after a start or load failed before `OnGameEnd`. Fixed: the prefix acts only for the
   campaign whose world identity behaviour it was attached to, and `Detach` also clears the trace.
8. **Some save names skipped the journal.** The initial save's reserved-name rule refused leading or trailing spaces and names over 200
   characters. Whether the game's Save As screen allows those was not checked. Fixed: later saves accept any plain file name the engine
   can write (`SaveJournal.ValidName`), and so do the transfer's save names. Tested with a spaced name and a 150-character name.
9. **The readback read every header in the folder after each save** (`MBSaveLoad.GetSaveFileWithName`). Fixed: the one header is read
   through the game's own driver (`ISaveDriver.LoadMetaData`, after the async driver's previous task). The whole-file integrity check
   remains. In game the readback took 0.06 s per save on an early-game save (run 43868); a late-game save takes longer in proportion to its
   size, and that was not measured.
10. **Checks that could not fail.**
    - The offline c2 checks are now labelled regression guards, and a control shows the tree hash catches one changed byte. The
      in-game hash comparison is the evidence for c2.
    - The probe's first check now reads the startup report's later-save recovery (nothing unresolved, no unreadable journal) and its
      counts, instead of a constant.
    - Tests were added for findings 1, 3 and 4.

## Found safe by the reviewer

- **Engine facts (IL):** `SaveTick` only peeks the queue, so the kind read is the current save's. Every MBSaveLoad entry point goes
  through the one `OverwriteSaveAux` overload, which is too large to inline. The refused callback runs synchronously. Completion runs
  on the main thread after the write, so the prefix and `OnSaveOver` cannot race. The probe's autosave slot formula and SaveFileCheck's
  format match.
- **The initial save** is not journaled twice.
- **MMR-62's resolution mapping** is unchanged.
- **The journal:** durable writes, validation, pruning (never intents), unreadable journals as gaps, temporary files cleaned.
- **The transfer:** path segments validated; links refused at the root, in the tree walk and in packages; unlisted and duplicate files
  refused; every copy re-hashed; a save is never replaced; the import is excluded by the store lock; content copied before saves;
  staging on the rename's volume.
- **The patch lifetime:** `Detach` runs before the teardown's patches-left check; `UnpatchAll(id)` removes only this owner.
