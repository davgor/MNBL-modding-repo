# MMR-49 handoff (paused 2026-10-17 ~22:25 UTC at the user's request)

MMR-49 is claimed: attempt `at_01m54zy8dmgs8xkmanykbpdy8x`. The token is in the session scratchpad's `claims.txt`. The lease was extended 16 h at the pause. Nothing is committed.

## Done

- **Review findings.** All 17 are fixed in one batch; see `evidence/fwg/world-loading-review.md`. Its "Found by the confirming runs" section is written; the Results section is still to fill.
- **Defects found by the confirming runs, both fixed:**
  - `WorldInspector.Kind` did not count OpenForSave's "missing bundle …" problem as missing, so the refusal said `damaged-world`.
  - The save pass and `NonCanonicalSaved` required `CampaignVec2.IsValid`, which also checks the navmesh face. They now use `PeriodicSeam.Movable` (set and not canonical), and the save pass also rebases such party positions.
- **Probe fixes, all in place:**
  - planting: strip images on the map, never the pointer track;
  - the seam row is now searched across the whole map;
  - B's lord fallbacks;
  - AI retargets reported apart, with a no-load control;
  - the fleet is captured at the load;
  - after the pursuit, the map conversation is ended (`ClearEncounter`) and the menu exited;
  - the probe waits for the anchor's hourly position update.
- **Offline harnesses:** all 19 green. The evidence is written: `evidence/persistence/loader-tests.json`/`.txt` (42/42) and `save-binding-tests.json`/`.txt` (77/77).
- **Docs.** `docs/world-loading.md` is rewritten except its run results; `docs/save-binding.md` covers the v2 export.

## In-game runs (raw results in `evidence/runtime/in-game/`, git-ignored)

| Run | Kind | Result |
|---|---|---|
| 11568 | create | Failed. Found the classifier defect, plus probe issues |
| 44940 | create | Failed: the planted values (off the map) and the fleet. Everything else passed: Continue (288 repeats ignored), A equal to its save, B from inside A, the four refusals, baselines |
| 35184 | create | Failed: the fleet only |
| 46320 | cold (diagnostic) | Failed: the fleet only. The cold path worked: surface encoder only, nav and playability restored, layout re-derived |
| 16324 | create | Failed: the fleet only, because the anchor position was read before the hourly tick set it |
| 5088 | create | **In flight at the pause, on the final build.** The game quits itself at the end. Check `progress-5088.log` and collect it with `tools/Collect-OwnedProbe.ps1 -ProcessId 5088` |

Builds are deterministic: worldbridge hashes `776C160626D029E6` on every build, so the scene names are stable. Rebuilding between create and cold is safe when only C# changes.

## Shared game

The battle-scale session ("Bannerlord growable agent storage") says it no longer needs a game slot. I told it that it may consider the game released once PID 5088 exits. **Before the next launch, send it a fresh CLAIM**, wait for OK (or 5 min with no reply), and check processes and the engine DLL hash `19387F31557F`.

## Next

1. If run 5088 passed (status ok): run the cold phase with `scratchpad/cold49.json`. It already points at `two_world_matrix-5088.json` and lists every leftover save. If 5088 failed, read its outputs, fix, and rerun create.
2. Run `python tools/summarize_two_world_matrix.py 5088 <cold pid> 28224`, which writes `evidence/persistence/two-world-matrix.json`.
3. Fill the Results sections of the review record and `docs/world-loading.md`.
4. Submit, accept, send RELEASE, and claim the next ticket.
