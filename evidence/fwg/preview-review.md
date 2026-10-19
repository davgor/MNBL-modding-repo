# MMR-46 independent review and fixes

One independent review (a read-only reviewer agent; no file edits, no game launch) of the MMR-46 working tree after the first in-game run
(43992). It reported 4 major and 7 minor findings. All were fixed in one batch, then the offline harnesses and the in-game probes ran
again.

Before the review, the author's own first run had already found and fixed five things:
- the fixed settings listed in the preview state (a list's `IsVisible` bound against its items);
- kingdom labels showing the source name rather than the registered name;
- a button row wider than the panel;
- a long-text panel taller than a 1080-unit page;
- 3 villages' kingdoms differing from the live campaign. The first attempt (take the bound settlement's kingdom from the records) did
  not fix it; see run 13920 below.

## Major

1. **The preview was drawn upside down against the campaign map.** In game, +y is north and up on the map. The preview's rows and
   marker heights ran with y, so south was at the top. Fixed in `WorldPreview.cs`:
   - raster row 0 is the north edge;
   - `V = 1 - (y - originY) / height`;
   - the hillshade light stays north-west (+y).

   New checks:
   - `Test-WorldPreview` checks that the northernmost place is drawn above the southernmost, with every marker's V running against its y;
   - the probe checks the same on the live world.
2. **The MMR-43 `world_setup` probe would have started a campaign.** After a generation it typed a seed under the preview and pressed
   Confirm, which is now Start Campaign. Fixed in two places:
   - the probe presses Exit first (back to the seed, the world kept);
   - the seed cannot be edited under the preview (`CanEditSeed` is false while the preview is shown). New Seed returns to editing before it
     draws.

   `world_setup` reran on this build (see the evidence below).
3. **A failed start showed the generation's message** ("The world could not be generated"), because the coordinator classifies an
   `ActivationException` as a bad source. Fixed with `StartOutcome.FromCommit`, which builds the setup's report of a finished commit:
   - `activation.failed`: undone, generate again;
   - `activation.unresolved`: the failure came after the initial save and is checked at the next start;
   - a full disk keeps its category.

   `CoordinatorRunner` passes it the activation's own recoverable flag. Tested offline.
4. **Full names were readable only with the mouse.** Fixed:
   - **Previous/Next place:** the tab keys (Q/E, the controller's shoulder buttons) and two buttons with their glyphs, in their own
     navigation scope. They walk the 71 towns and castles west to east, each named in full below the map, with a gold ring on the selected
     marker.
   - **Kingdoms:** every kingdom holds a town, so walking the places names every kingdom in full; pointing at a kingdom's gold label does
     too. A full kingdom list under the counts was tried first, but with long texts it pushed the panel past 1080 units (run 13920), so it
     was dropped.
   - **Hint claims:** the code comment and test label claiming a hint were corrected (no hint widget exists).

   Tested offline and by the probe (keys through the screen's mapping).

## Minor

5. **A recoverable failure after the initial save blocked every save for the session.** `ActivationCommitStage.Recover` now releases the
   save guard and the save block when the failure is recoverable.
6. **Enter did nothing for a world without a preview.** `SetupInput.ForState` takes whether the preview is shown: without one, Confirm
   generates and Randomize draws a seed.
7. **Back stayed enabled while the campaign started.** It is bound to `CanGoBack` (false while starting).
8. **Checks that could pass vacuously.**
   - The probe now asserts:
     - the hand-off (the screen closed for the new game);
     - the reopen count;
     - the committed job's files released;
     - the preview texture dropped;
     - for markers of no kingdom, that the live settlement is in no kingdom either.
   - Hover is still triggered through the marker view model's commands. The prefab binding to them is checked offline, and
     `docs/world-preview.md` says so.
   - `Test-WorldPreview` now checks each marker's kind and name against the campaign records themselves.
9. **Texture and PNG leaks.** On replacement or clear, the engine texture is released after 3 frames (`ReleaseAfterNumberOfFrames`). The
   texture is made from the raster's bytes by default (run 43992 showed both modes render alike), so no file is written. The PNG file is
   the fallback, and only the shown preview's file is kept. The probe checks both.
10. **A translated noun spliced into a sentence.** There is now one whole sentence per kind, with and without a kingdom (`PlaceTown` …
    `PlacePortFree`).
11. **A start could be orphaned.**
    - `GeneratedCampaignManager.Progress` reports a failure when no game of the manager initialized and none is loading 30 s after the
      start, instead of waiting out the campaign timeout.
    - A setup screen that closes while its world is starting hands its model over (`Pending`) in `OnFinalize`.

    The reviewer marked the likelihood uncertain. Both paths are defensive and are not exercised by the probe.

## After the fix batch: run 13920

The confirming run passed everything else of the review's fixes, but failed three things:

- **Long texts:** the panel came to 1085 units, 5 over the limit. The full kingdom list added for finding 4 took about ten lines with 40
  kingdoms, so it was dropped. Every kingdom holds a town, so Previous/Next place still names each kingdom in full, and pointing at a
  kingdom's gold label names it. Offline checks confirm that every kingdom of the world is reached.
- **The same 3 villages' kingdoms.** The cause is MMR-33's village binding, which rebinds villages away from the compiler's town (44 in
  world A, 3 of them into another kingdom). The preview used the records. The binding was extracted unchanged into
  `Campaign/VillageBinding.cs`, which `SettlementFactory` and the preview now both call. All ten harnesses that compile `SettlementFactory`
  still pass. `Test-WorldPreview` reports the binding's counts and checks every village against it.
- **80 harbour villages had a live port but no preview marker for it.** War Sails gives a harbour village a port (its fleet landing). The
  preview now draws harbour villages as a distinct blue-edged dot with their own sentence. The live check matches each village's `HasPort`
  to that flag, and each town's to its port marker. The layout's harbour flags are checked offline.

## Checked and found correct by the reviewer

- **Reuse rule:** a failed or released job is never reused.
- **Job ownership:** no double release.
- **Withdrawn, then New Seed or Back:** both release the job correctly.
- **Ordering:** Tick, HandOff and TickPending are ordered correctly on the engine thread.
- **The commit continuation** is never cancelled.
- **The Harmony save-block targets** exist on the installed `SaveHandler`.
- **The texture** renders after the game round trip.
