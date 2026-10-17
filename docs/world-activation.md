# World activation (MMR-45, U03)

A generation job's world, waiting for review, becomes one campaign instance in one transaction. The transaction joins the pieces earlier tickets
proved:
- the compiled world's campaign graph, closed and validated (C07, MMR-36);
- its navigation and playability (W02, W05; MMR-38/41);
- its settlement placement (W04, MMR-40) and mission scene contracts (W06, MMR-42);
- its climate and water (W03, MMR-39);
- the early save binding (MMR-62).

Success means two things: one immutable activation receipt, and a playable initial save. Any failure leaves diagnostic evidence, never a
playable half-world.

| Part | Where |
|---|---|
| Transaction, journal, receipt, recovery (pure managed) | `src/MNBL.ProceduralWorld/Generation/WorldActivationTransaction.cs` |
| The game's boundaries (the commit stage of the generation job) | `Generation/ActivationCommitStage.cs` (`ActivationCommitStage`, `GameActivationHost`) |
| The compiled world's campaign graph (pure managed) | `Campaign/CompiledWorldDataset.cs`, with the fixture world's re-emission closure (`GeneratedCampaignDataset.CompiledWorldDocuments`) |
| The compiled world's registration in the new game | `CampaignBootstrap/CompiledWorldRegistration.cs` (`IGeneratedWorld`) |
| The installed catalog the world is built against | `worldgen/policies/campaign-catalog.json`, written by `tools/make_campaign_catalog_pin.py` |
| Startup recovery | `Persistence/SaveBindingRuntime.RunStartup` (after MMR-62's save recovery) |
| Component harness (offline) | `tests/Generation/ActivationFailureTests.cs`, run by `tools/Test-Activation.ps1` |
| Compiled world tests (offline) | `tests/Campaign/CompiledWorldTests.cs`, run by `tools/Test-CompiledWorld.ps1` |
| In-game probe | `world_activation` (`Automation/WorldActivationProbe.cs`) |
| Evidence | `evidence/generation/activation-matrix.json` (`tools/summarize_activation.py`), `activation-tests.json`/`.txt`, `compiled-world-tests.json`/`.txt`, `evidence/fwg/activation-review.md` |

## The transaction

**Order.** `WorldActivationTransaction.Run` takes the boundaries in this order. Each completed boundary is journaled durably, with its
evidence hashes and detail, in `<store>/activations/<instance>.json` (`mnbl.activation-journal.v1`, written through `DurableFile` at every
transition).

| Boundary | What it does | Thread |
|---|---|---|
| Admit | The store's writer lock (MMR-62), checked before the journal is begun. A job in Committing with passing runtime validation. The main menu, with no game running. One activation at a time: an unrecovered active journal, or an unreadable one, refuses the next. | worker, one engine item |
| VerifyData | Re-opens the DATA_READY bundle: manifest, every file hash, host compatibility. This happens before any native resource of the world opens. Its content id and manifest must be the job's. Records the generator, request and policy hashes (the job's inputs) and the host's game and War Sails versions. | worker |
| VerifyRuntime | The runtime reports belong to this world and scene and still pass. `NavigationGate.Require` (W02) and `RuntimePlayabilityValidator.Require` (MMR-41, made against this navigation and layout). The persisted navigation and playability reports are the ones this job made (their recorded hashes equal the job's). The settlement layout report (`layoutSha256` equals the prepared layout's) and the job's entity check. The installed scene files are unchanged since the job installed them. Copies of the three reports are kept with the activation. | worker |
| BuildWorld | The campaign graph from the bundle (below), validated whole (C07), against the pinned installed catalog. Also the world's climate, synthesized from its source in its coordinate frame, and its terrain context for the battle map. | worker, engine items for the catalog |
| PromotePackage | The world package (`GeneratedWorldPackage`, moved into place whole, manifest last), from a compiled-world placement. The bundle is taken over from the job, because the save binding names it. | worker |
| StartCampaign | The new game on the world's scene. Registration runs inside the new game's `RegisterSubModuleObjects` (R07, `CompiledWorldRegistration`). Then the map scene, the navigation caches (MMR-41) and the battle map (MMR-42). | engine items; the worker waits for the campaign map |
| PostBuild | The live campaign against everything it was built from. Closure is settled. Every settlement stands on its layout position (centre, gate, port). No baked entity was made at run time. The navigation caches are registered under their key. The battle map is installed with the world's terrain context, and every land cell and every sea terrain check gives one scene. Every settlement's live location scenes meet the mission contracts. The world's climate is on the live map, and its sea and lake planes are present. The route set passes through the live campaign (MMR-41). The save guard holds. | one engine item |
| BindIdentity | The save header's world binding (`WorldIdentity`) with this instance. | one engine item |
| PublishSave | The initial save (MMR-62's `InitialSavePublisher`), read back bound to this instance and whole. | engine items |
| WriteReceipt | The receipt and its runtime-derived record. The transaction writes them itself. | worker |

**Failure.** A failure at a boundary undoes in reverse ownership order through the host's `Undo`. First comes what the failing boundary itself
left, when its work had begun: a game that started or failed to start, registered objects, a package already moved into place. `Undo` is safe
whatever part of the work happened. Then every completed boundary is undone:
- ending the new game through the installed menu-exit lifecycle. `MBGameManager.EndGame` waits for a game still loading, and the host waits up
  to 300 s;
- rolling back any object of the world still registered;
- removing the world's terrain context;
- deleting a package this activation created;
- giving the bundle back to the job, which releases it as before.

A boundary counts as completed as soon as its work returns, even if the journal write after it fails, so its undo is never skipped. The journal
then says failed. It records where the failure came from and every undo's outcome. An undo that throws is recorded, and the remaining undos
still run. If even the failed state cannot be journaled, the journal stays active and recovery abandons it.

**Failures left for recovery.** These leave the journal active instead:
- **Any failure once the initial save was published.** This covers the receipt write, or a journal write after PublishSave. The world is
  published whole and bound, so nothing is undone and recovery writes the receipt. A rollback never leaves a bound save whose package or bundle
  it deleted.
- **An unresolved initial save** (MMR-62 left its intent, or the save did not finish within 600 s). The save may exist, bound and whole. The live
  campaign is undone, the package is kept because the pending publication references it, and recovery decides once MMR-62's recovery has resolved
  the save.

**No partial world becomes selectable as a save.**
- The only save written is the initial save, after every check has passed.
- From the moment the campaign map runs until the commit, the host's own save manager (`ActivationSaveGuard`) holds the campaign: autosave
  off, the sandbox's own manager told of every completed save. PostBuild refuses a campaign whose save manager is not the guard.
- After the commit, the starter's own manager is back.
- Ironman is recorded, not refused: MMR-62's publisher saves an Ironman campaign under its Ironman name.
- Manual saves through the escape menu are the player-facing starter's to block (MMR-46). The probe's map is paused with no player input.

**The campaign instance.** It is the one the setup chose: the generation request carries it, in `WorldChoice.CampaignInstanceId`. It is written
as the save binding's 32 hex digits, so the receipt, the save binding, the journal and the request name the same campaign. A request id that is
not a version-4 UUID falls back to a new instance, and the record shows both.

## The receipt

**The receipt.** `<store>/activations/<instance>/receipt.json` is exactly the v1 contract (`worldgen/contracts/activation-receipt.schema.json`;
no additional properties):
- the world's content id and manifest;
- the campaign instance (the save binding's 32-hex instance, written as the dashed UUID it is);
- the runtime-derived record's hash;
- W02, W03, W04, W05 and C07, each passed with its evidence hash;
- `activatedBy: U03`.

**The runtime-derived record.** Beside the receipt, `runtime-derived.json` holds everything derived at activation: the evidence and detail of
every boundary up to BindIdentity. That covers:
- the generator, request, policy and input hashes;
- the host (game and War Sails versions);
- the backend: navmesh, navigation policy, coordinate frame and policy, encoder;
- the installed scene files;
- the catalog pin;
- the dataset record (documents, graph, layout);
- the climate identity;
- the package;
- the navigation cache key;
- the live checks.

**Write-once.** Both files are built only from the journal as read back from its durable file, so a recovery after a stop writes the same bytes.
Both are written once: identical bytes are the same receipt, and anything else is refused (permanently: recovery quarantines such a journal).
`ActivationReceipt.Read` also checks the receipt names the instance it is read for.

**Evidence kept with the receipt.** The reports the evidence hashes name are copied into `<store>/activations/<instance>/evidence/` at
VerifyRuntime: the navigation record, the playability record and the settlement layout report. A later generation job for the same scene may
sweep or rewrite the scene's own reports, and they would then be gone. The navigation caches named by their key in W05 are re-made on demand
under the same key; they are not evidence copies.

**Engine and DLC.** VerifyData hashes the installed binaries this process runs, by file name, never by path: TaleWorlds.Engine, TaleWorlds.Core,
TaleWorlds.CampaignSystem, TaleWorlds.MountAndBlade, SandBox and NavalDLC. It records them with the game and War Sails versions.

| Validation | Evidence |
|---|---|
| W02 | the navigation report record (`world-navigation/<scene>.json`) |
| W03 | the climate identity, the live map's weather (status and wind and snow/rain hashes) and the scene's water |
| W04 | the settlement layout report, the live settlements on it, no baked entity made at run time |
| W05 | the playability record (inspection world), the live campaign's route set, the navigation cache key, the battle map, the mission scenes |
| C07 | the graph report |

## Recovery

`ActivationRecovery.Recover` runs at every start, at the main menu, after MMR-62 has recovered the initial saves. It is idempotent. For each
active journal:

| The process stopped | Outcome |
|---|---|
| before the package was promoted, or after it with no published initial save | abandoned. The package is kept, unbound: no save names it, so it is never selectable (MMR-47's cleanup). |
| with the initial save published (journaled, or found published by MMR-62's journal) but no receipt | the receipt is written from the journal, then committed |
| with the receipt written but not committed | committed |
| with MMR-62's journal still unresolved | left active and reported; retried at the next start |

**Unreadable and unrecoverable journals.**
- An unreadable journal refuses the next activation (fail closed) until recovery has run. Recovery moves it aside as evidence
  (`<file>.quarantined`) and reports it.
- An active journal whose recovery fails on its data is marked `quarantined`, reported and kept. Examples: its evidence does not hold, or a
  receipt with other bytes exists.
- Neither stops later activations. An I/O failure during recovery is retried at the next start.

## The compiled world

`CompiledWorldDataset.Build` runs at BuildWorld, at the main menu. The offline tests run the same builder.

**Steps:**
1. **The accepted factories, in order.** Factions (MMR-31), heroes (MMR-32), and settlements (MMR-33, with the mission scene contracts of
   MMR-42). The settlement records then take the placement the scene's entities were built from: the settlement layout report the scene
   stage persisted (MMR-40), moved into the records' logical frame. Startup (MMR-34) and naval content (MMR-35) run last, on those positions.
2. **The player's records**, aliases installed code finds by literal id: the installed `main_hero` character and hero, and the installed
   `player_faction`, homed at the world's start town of the player character's culture.
3. **The merged documents.** The records are merged into the new game's eight documents (`RegistrationPlan.NewGameOrder`). The installed records
   they need are re-emitted by the fixture world's closure rule (`GeneratedCampaignDataset.CompiledWorldDocuments`):
   - the stock categories' NPC templates;
   - the engine's literal NPC lookups;
   - the startup plan's templates;
   - every generated record's references;
   - the installed WorkshopTypes and LocationComplexTemplates, whole.
   The fixture world's own documents are byte-identical before and after this refactor (`Test-GeneratedCampaignDataset`, the same 8 document
   hashes).
4. **Validation.** The whole graph is validated (MMR-36) in the logical frame, where the travel cells are.
5. **The registration copies.** Every position moves into the scene's physical frame: physical = logical + (originX, originY), MMR-64.
   Settlement centres, gates and ports move, and culture start points. Only positions change.

**Two checks on the result:**
- **Positions.** The layout's x is wrapped into the canonical logical range [0, width) before the records take it, so a gate or port across
  the seam stands in its canonical travel cell. The registration Settlements document is then compared with the layout report: every centre,
  fortification gate and town port equals its physical placement within 1e-6, or the world is refused.
- **Spouses.** A hero's spouse must name the hero back, or name none. Installed IL: `Hero.Deserialize` reads `spouse` only while the hero has
  none, so another spouse on the later hero would be silently ignored. A mismatch refuses the world.

**Hero bodies.** Every XML hero's explicit body is drawn at registration, inside the new game (`CompiledWorldRegistration.Register`), with the
engine's face generator (`HeroBodies`, MMR-32) and the hero's seed. They are not drawn at BuildWorld:
- the body ranges live in the game's object manager, which the main menu does not have;
- the first in-game run, 26432, failed BuildWorld with a NullReferenceException when it drew them there, and was rolled back.

The same seed gives the same body. So the package keeps the documents as validated, and a load draws the same bodies again. The registration
report records the count and the bodies' hash.

**The catalog pin.** The factories' policies were made from one capture of the installed merged XML. The activation builds a world only from
live documents (`GetMergedXmlForManaged` at the main menu) whose SHA-256 equals the capture's. `CompiledWorldRegistration` registers only when
the live engine defaults are exactly the pinned inventory. Another game version or another mod's XML refuses the world instead of building it
from documents its policies were not made from.

**Why every captured document is pinned.** The review suggested pinning only the documents the policies read. That was not taken: every
captured document is an input of C07. The graph validator and the re-emission closure follow references through the installed loaders'
schema, into items, crafting pieces, monsters and ship pieces, so an item mod changes what the re-emitted templates reference. Mod
compatibility is stated as a limit. BuildWorld as a whole, the 24 merges included, took 6.8 s in run 22472. The merges were not timed apart.

**The registry's two-pass list.** It now includes a hero's family (`Hero.father`, `Hero.mother`, `Hero.spouse` to a later hero), as the installed
`Heroes.xml` has. Installed 1.4.8 IL:
- `Hero.Deserialize` reads each family reference through `ReadObjectReferenceFromXml`, which gives a presumed Hero when the referenced hero is
  not loaded yet;
- `set_Father` and `set_Mother` store the reference and add the hero to the referenced hero's `_children`;
- `set_Spouse` links both sides;
- nothing else of the referenced hero is read, and its own record later deserializes the same object.

A family member that never gets a record stays presumed, which the closure check refuses.

## The package

A compiled world's package (`<store>/worlds/<worldId>-<manifest12>`) holds:
- a compiled-world placement (`mnbl.compiled-world-placement.v1`): world id, scene, extent, the five installed scene files' hashes, the periodic
  frame, the content id and bundle manifest, the layout, climate and graph hashes;
- the eight registered documents;
- `world.json`.

**The periodic frame.** `PeriodicWorldCoordinates.ToPlacement` writes it, and `FromPlacement` reads back the same frame: rows, height and
central meridian stated exactly. The activation checks that the frame identity round-trips before the package is written.

**Identical content.** It gives the same package, which a second campaign of the world shares. A failed activation deletes only a package it
created itself.

## Offline tests

**`tools/Test-Activation.ps1`** (`tests/Generation/ActivationFailureTests.cs`): 240 checks (`evidence/generation/activation-tests.json` and
`.txt`). The component harness: a scripted host whose boundaries have the side effects that matter to a failure:
- a world package moved into the store;
- a running campaign with registered objects;
- the identity binding;
- the initial save, published through MMR-62's real journal over a scripted save list.

An earlier world is activated first. Then come:
- a failure injected at each of the ten boundaries;
- failures inside the work of the boundaries with partial side effects, by a host that cleans its own partial work and by one that leaves it
  to the transaction's undo;
- a process stop after each boundary;
- stops inside the publication, before and after the engine save;
- a save list that cannot be read back;
- a torn save;
- a reserved name another campaign holds;
- a journal write failing after the published save;
- an undo that throws;
- a second campaign of the same world content, failing and then committing;
- admission: one at a time, and an unreadable journal;
- quarantine.

**After every case:**
- every earlier committed world's package, receipt, save and journal are byte for byte unchanged;
- a save is bound to an instance exactly when its activation committed with a receipt;
- recovery is idempotent;
- the receipt is the v1 contract and is rebuilt byte for byte from the journal.

**`tools/Test-CompiledWorld.ps1`** (`tests/Campaign/CompiledWorldTests.cs`): 8 checks on world A (`evidence/generation/compiled-world-tests.json`
and `.txt`):
- the graph validates;
- all 387 centres, gates and ports stand on the layout after the frame move;
- the frame move changes positions only;
- the documents plan through the registry, with 150 hero family references to a later hero;
- the creation cultures and their start points;
- determinism;
- a missing or unactivatable layout is refused, and a moved record is found.

**Regressions.** The harnesses the change touches all pass: GraphValidation 72, CampaignRegistry 59, Settlements 22, NavalContent 15, SpawnPolicy
18, GeneratedCampaignDataset 60 (the fixture world's documents byte-identical), EarlyBinding 58, PeriodicCoordinates 85, EncounterResolver 35,
SettlementPlacement 52, Playability 24.

## In game

**The probe.** `world_activation` (`seed`, `culture`, `fault`, `stopInside`, `secondFault`, `gameplayDays`, `campaignTimeoutSeconds`):
1. a generation job (MMR-44) to review;
2. its commit through `ActivationCommitStage`, with the diagnostic bootstrap driving character creation;
3. the receipt verified, and the initial save read back;
4. campaign time run, with the player sheltered;
5. then, optionally, the same world activated again with a failure.

`tools/summarize_activation.py` writes `evidence/generation/activation-matrix.json` (15/15 checks).

| Run | What | Outcome |
|---|---|---|
| 26432 | first activation | BuildWorld failed (hero bodies drawn at the main menu: NullReferenceException) and rolled back: main menu, failed journal, no save, no object |
| 22472 | commit, 1 day, a second activation failing at PostBuild | **committed** in 407 s (StartCampaign 398 s, of which the first navigation caches took 389 s); receipt `e0fd12b0...`; the save published, whole and bound; 1.0 day played; the second activation (caches reused, 15 s) rolled back and the first world's 16 hashes were unchanged |
| 3352 | stop inside PublishSave | PostBuild refused the map: the determinism check compared counts with `true` (review finding 1). Rolled back cleanly |
| 9404 | the review's fixes: commit, 1 day, a second activation failing at PostBuild | **committed** (instance from the request, the save guard, the live water, 5,548/5,548 land cells and 16,076/16,076 sea checks, evidence copies, engine and DLC hashes); receipt `f34781a5...`; the day stalled at 0.83 on an unplayed bandit encounter (the probe now shelters the player); the second activation rolled back and the first world was unchanged |
| 49176 | stop inside PublishSave | the process was terminated after the initial save was published and read back, before the journal recorded it: active journal through BindIdentity, MMR-62's journal published, no receipt |
| 26876 | stop inside PromotePackage; its startup recovers 49176 | the startup **committed** 49176: PublishSave found published by MMR-62's journal, receipt `2e73defa...` written at recovery. Then this run's own process was terminated inside PromotePackage |
| 43676 | commit, 1 day, a second activation failing at BindIdentity; its startup recovers 26876 | the startup **abandoned** 26876 (its package was the identical content 49176 had promoted, reused, so nothing unbound was left). **Committed** in 21 s (caches reused: StartCampaign 9.6 s); receipt `ea690359...`; the save published, whole and bound; 1.0 day of campaign time, no stall; the second activation failed at BindIdentity with the campaign running and rolled back all seven boundaries (main menu, no save bound, no object), and the first world's 16 hashes were unchanged |

**Every commit:**
- all eight registered documents and the graph report (`a8275a2f...`) are byte for byte the offline ones (`Test-CompiledWorld`), so the live
  catalog is the pinned one and the game builds exactly the world the tests validate;
- the registry plans 1,344 objects and closes, and the 241 hero bodies are drawn;
- PostBuild sees 0 closure problems, 387 positions on the layout and 0 baked entities made at run time. The 72 hideouts get their run-time
  entities. All 358 settlements meet the mission contracts, and the live route set passes;
- the runtime-derived record holds no profile path.

**Timings.** Generation took 32-33 s per job. BuildWorld took 6.4-8.0 s and PublishSave 1.5-2.0 s.

## Limits

- **The player-facing starter is MMR-46's.**
  - The probe's starter is the diagnostic bootstrap: it drives character creation and holds the map paused.
  - The Start Campaign action supplies `Start` and `Progress` to `ActivationCommitStage`, with real character creation.
  - It must block manual saves until the commit; the host's save guard already holds autosave.
- **Loading a compiled world's save is MMR-49's.** `CompiledWorldRegistration.Replay` refuses. The package holds the documents as validated, the
  compiled-world placement and the bundle's identity. A load must re-install the scene from the bundle (a later job may sweep it), draw the
  same hero bodies and replay.
  - "Playable" here means the activated campaign plays: a day of campaign time with parties, trade and recruitment.
  - The initial save is whole and bound; a reload is not shown.
- **Navigation cache cost.** The first campaign of a world in a module build generates its caches: 389-391 s for world A. The key includes this
  module's SHA-256, so every build regenerates them once. MMR-56 measures the profile.
- **The catalog pin is strict.** Every captured document must equal the live one, so another mod's campaign XML refuses activation. Finding 13
  of the review gives the reasons.
- **One world was activated in game: world A (seed 42).** World B is covered offline by the dataset's factories (earlier tickets), not by this
  activation.
- **A game that fails while loading** is ended once it has loaded: `MBGameManager.EndGame` waits for `IsLoaded`, and the host waits up to
  300 s. An exception inside the engine's new-game callbacks can end the process. The journal then stays active, and the next start abandons
  it.
- **Abandoned packages stay unbound in the store.** No save names them. MMR-47's staging cleanup and world inspection remove them.
- **The probe records a stopped run's instance only in the journal.** The next start's single recovery row names it.

## Handoffs

- **MMR-46:** call `GenerationCoordinator.Commit(job, new ActivationCommitStage { Start = ..., Progress = ... })` from Start Campaign, with a
  player-driven character creation, and block manual saves until the commit.
- **MMR-47:** sweep abandoned unbound packages, and list quarantined journals and failed activations' evidence in the world inspection.
- **MMR-48/49:** restore a compiled world from its package, its bundle and its receipt before deserialization. Re-install the scene from the
  bundle; the activation's evidence copies keep the receipt checkable.
- **MMR-56:** world A's cache generation (Default 48.6 s, Naval 33.8 s, All 306.1 s in run 22472) and the per-build regeneration.
