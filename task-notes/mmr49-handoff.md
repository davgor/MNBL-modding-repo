# MMR-49 handoff (updated 2026-10-17 20:30 UTC)

MMR-49 is claimed: attempt `at_01m54zy8dmgs8xkmanykbpdy8x`. The claim token is in the session scratchpad's `claims.txt`, never in the repo. The work is uncommitted.

## Done

- All 17 review findings are fixed in one batch. The record is `evidence/fwg/world-loading-review.md`; its Results section is still to fill.
- The loader's order is now:
  1. discover;
  2. check everything: scene, reports through their gates, reconstruction, replay plan, map scene, periodic patches;
  3. end the running campaign;
  4. install, deserialize, start.
- Routing:
  - the prefix is now on `SandBoxSaveHelper.LoadGameAction`, so vanilla's prompts are kept;
  - a `TryLoadSave` prefix ignores the preload screen's per-frame repeats;
  - a version-agnostic guard on `MBSaveLoad.LoadSaveGameData` refuses generated saves to anyone but the loader.
- Transfer: the export format is `mnbl.save-export.v2` and carries `evidence/<instance>/`, the activation reports.
- Offline harnesses, all 19 green:
  - `Test-WorldLoader` 42/42, written to `evidence/persistence/loader-tests.json` and `.txt`;
  - `Test-SaveBinding` 77/77, written to `evidence/persistence/save-binding-tests.json`.
- Docs: `docs/world-loading.md` is rewritten (no run results yet); `docs/save-binding.md` covers the v2 export.

## Running now

- Shared game: the battle-scale Claude session ("Bannerlord growable agent storage") granted CLAIM for both runs. **Send it "RELEASE game" (SendMessage) after the cold run's process exits.**
- The create run, PID 11568, started at 20:24 UTC with args from `scratchpad/create49.json`. Collect it with `tools/Collect-OwnedProbe.ps1`.
- The module was built at 20:23. **Do not rebuild between create and cold**: world A's scene name includes the surface encoder's hash.

## Next

1. Run the cold phase: `{"phase":"cold","previous":"<abs path of evidence/runtime/in-game/two_world_matrix-11568.json>","deleteSaves":"mnbl_probe49_A_26872,mnbl_1609fb72_start_d82ae9f65741,mnbl_probe49_A_2092,mnbl_1609fb72_start_d3c18d014d8f,mnbl_probe49_A_37192,mnbl_1609fb72_start_f8e04f62bcd6,mnbl_1609fb72_start_344d742323ec"}`.
2. Run `python tools/summarize_two_world_matrix.py 11568 <cold pid> 28224`.
3. Fill the Results sections of the review record and `docs/world-loading.md`.
4. Submit, accept, and claim the next ticket.
