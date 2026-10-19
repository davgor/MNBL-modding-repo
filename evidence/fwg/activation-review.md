# MMR-45 independent review (world activation)

**Scope.** One independent review of:
- the transaction;
- the game host;
- the compiled world dataset and its registration;
- the probe;
- the refactor of the fixture dataset's closure;
- the registry's two-pass entries;
- the catalog pin;
- the harnesses, the summarizer and the doc.

**How it was done.** The reviewer:
- read the installed 1.4.8 IL where the claims rest on it: `Hero.Deserialize`, `set_Father`, `set_Mother`, `set_Spouse`, `Hero..ctor`,
  `MBGameManager.EndGame`, and `GetApplicationVersionWithBuildNumber`;
- reproduced `tools/Test-Activation.ps1` (212/212 at the time) and `tools/Test-CompiledWorld.ps1` (8/8);
- compiled the whole module into a scratch folder;
- neither edited the repository nor started the game.

**Moving target.** Two files changed while the review ran: `ActivationCommitStage.cs` and `WorldActivationProbe.cs`. The findings are against
their 03:05 state.

**Verdict before the fixes:**
- c1 met with fixes;
- c2 met with fixes;
- c3 not met in that snapshot, because finding 1 blocked every commit.

All findings were fixed in one batch, except the one decided otherwise (13).

**Confirming results:**
- **Offline:** `tools/Test-Activation.ps1` 240/240 (`evidence/generation/activation-tests.json` and `.txt`) and `tools/Test-CompiledWorld.ps1` 8/8
  (`evidence/generation/compiled-world-tests.json` and `.txt`). Regression harnesses: GraphValidation 72/72, CampaignRegistry 59/59,
  Settlements 22/22, NavalContent 15/15, SpawnPolicy 18/18, GeneratedCampaignDataset 60/60 (the fixture world's 8 documents byte-identical),
  EarlyBinding 58/58, PeriodicCoordinates 85/85, EncounterResolver 35/35, SettlementPlacement 52/52, Playability 24/24.
- **In game:** the runs summarized in `evidence/generation/activation-matrix.json`.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | blocker | PostBuild compared `landDeterministic` and `seaDeterministic`, which are cell counts, with `true`. Every activation rolled back. | Already corrected after run 3352, which had failed on it: the counts must equal `landCells` and `seaTerrainChecks` (the same rule as `tools/summarize_mission_scenes.py`). Run 22472 committed before the check existed. Run 9404 committed with it: 5,548 of 5,548 land cells and 16,076 of 16,076 sea checks. |
| 2 | major | A new game that failed after it started was never ended: undo ran only for completed boundaries. The game and its objects stayed, the package was deleted under it, and the next Admit refused. The harness hid this because its host cleaned up after itself. | The transaction now undoes the failing boundary's own leftovers first, whenever its work had begun. `Undo` must be safe whatever part of the work happened. `Undo(StartCampaign)` ends any game, rolls back any of the world's objects (or reports them when no registry exists yet) and removes the terrain context on the engine thread. It waits up to 300 s, because `MBGameManager.EndGame` waits for a game still loading. Harness: hosts that leave their partial work (PromotePackage after the move, StartCampaign with registered objects) are cleaned by the transaction. |
| 3 | major | A boundary joined `completed` only after its journal write. A journal write failing right after PublishSave rolled back a published world: game, package, bundle. That left a bound save whose package was gone, and recovery would then write a receipt for it. | A boundary is completed as soon as its work returns. Once PublishSave has returned, any failure is recoverable: nothing is undone, and recovery writes the receipt. A failure to journal the failed state leaves the journal active, and recovery abandons it. Harness: the journal write after the published save fails (`ActivationJournalStore.BeforeWrite`). The outcome is recoverable, nothing is undone, and recovery commits with the receipt. |
| 4 | minor | The PublishSave wait had no upper bound while the engine kept a save queued. | A 600 s hard limit, then `UnresolvedPublication`: MMR-62's recovery decides the save. |
| 5 | minor | The activation minted a new campaign instance. The generation request carries the setup's `CampaignInstanceId`, so the receipt named another instance than the request it binds. | The instance is the request's: `WorldChoice.CampaignInstanceId` written as 32 hex digits. A non-version-4 id falls back to a new instance, and the host record shows both. Run 9404: `instanceFromRequest` true. |
| 6 | minor | A later job sweeps or rewrites the scene's reports and caches (scene use is tracked per process), so the receipt's evidence could not be re-checked later. | The navigation record, playability record and settlement layout report are copied into `activations/<instance>/evidence/` at VerifyRuntime (run 9404: all three present). Stated in the doc. Caches are re-made on demand under their key. |
| 7 | minor | An unreadable journal, or an active one whose recovery keeps failing, blocked every later activation with no way out. | Recovery moves an unreadable journal aside (`<file>.quarantined`). An active journal whose recovery fails on its data is marked `quarantined`. Both are reported and kept, and neither blocks. The immutable receipt's refusal is now an `InvalidDataException` (permanent). Harness: both cases, then a committed activation. |
| 8 | minor | VerifyRuntime never compared the job's report hashes with the persisted reports, so "belongs to this job" relied on one job at a time. | Both the navigation and the playability records' `reportSha256` must equal the job's. |
| 9 | minor | The W03 water evidence was only the lake count. | PostBuild finds the sea plane and every lake plane on the live map, by the names the encoding gave them. Run 9404: sea plane present, 8 of 8 lakes. |
| 10 | minor | No engine or DLC binary was hashed. | VerifyData hashes TaleWorlds.Engine, Core, CampaignSystem, MountAndBlade, SandBox and NavalDLC by file name. |
| 11 | minor (latent) | `Layout` and `ToPhysical` did not wrap x into the canonical range. | `Layout` wraps the logical x into [0, width). The registration document is compared with the layout's physical positions (finding 15). |
| 12 | minor | Only the diagnostic starter turned autosave off, by convention. | The host installs `ActivationSaveGuard` (autosave off) once the map runs, and restores the starter's manager after the commit. PostBuild refuses a campaign without it. Ironman is recorded. Manual saves are MMR-46's starter's to block (stated). |
| 13 | minor | The catalog pin is over-strict: all 24 documents must match, so any item or troop mod refuses activation. | **Not taken.** Every captured document is an input of C07: the graph validator and the re-emission closure follow references into items, crafting pieces, monsters and ship pieces. Stated as a limit (mod compatibility). |
| 14 | minor | Paths in evidence: the summarizer kept absolute paths, and the host record's stack traces were included. | The summarizer keeps only file names outside the repository. The stack traces carry method names only, with no file paths (no line information in the release build). The receipt, the runtime-derived record and the pin were already clean, and the encoder record was reduced to role, executable hash and exit code. |
| 15 | nit | `PositionMismatches` compared the intents with the values just copied into them. | It now compares the registration Settlements document with the layout report's physical positions. |
| 16 | nit | The terrain context entry was removed on the worker, and the record was written without its lock. | Both fixed. |
| 17 | nit | Temporaries inside `activations/<instance>/` were never swept. `ActivationReceipt.Read` threw on missing keys and did not check the instance. | StagingCleanup sweeps each instance folder and its evidence folder. `Read` checks the keys and the instance. |
| 18 | nit | The journal was written before the writer check. | `ActivationCommitStage.Commit` checks the store's writer before the transaction begins the journal. |
| 19 | nit | The hero family claim holds, but `spouse` is read only while the hero has none, so a later hero's other spouse is silently ignored. | `CompiledWorldDataset` refuses heroes whose spouses do not name each other. World A has none. |

**Found by the in-game runs:**
- **Run 26432.** BuildWorld drew hero bodies at the main menu, where `MBObjectManager` holds no body ranges. It failed with a
  NullReferenceException and rolled back cleanly. Bodies are now drawn at registration, deterministic from each hero's seed.
- **Run 26432.** The encoder detail carried absolute profile paths, so it is now hashes only. Since that run, a failing boundary's full
  exception is kept in the host record.
- **Run 9404.** The day of gameplay stalled on a bandit encounter the probe does not play. The probe now shelters the player (as fixture run
  39908 needed).
