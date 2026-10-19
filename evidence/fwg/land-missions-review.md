# MMR-52 independent review and fixes

One independent review of the MMR-52 working tree. The reviewer was an agent with read-only access. It did the following without editing files or launching the game:
- read the probe, the shared pieces it introduced (`AssertCapture`, `LocationSceneAudit`, `GeneratedBattleTerrain.SampleCell`), the summarizer, the scenario file and the offline checks;
- checked engine claims against the installed IL (TaleWorlds.CampaignSystem, SandBox, TaleWorlds.MountAndBlade);
- ran the scenario check and the regression replay;
- read the raw outputs of development runs 29768, 25280, 17928 and 48860.

The tree changed during the review: run 48860 and the hideout scene check landed while it read. It reported 1 blocker, 7 major and 8 minor findings. All were fixed in one batch, then one confirming run followed (below). Eight of the fixes are guarded by a mutation of the committed fixture (the scenario file's regressions marked `foundBy: review`).

## Before the review

Four development runs found the probe's own faults, each fixed and guarded in the scenario file:
- **29768:**
  - crafted weapons have no multimesh;
  - lords resting in settlements were skipped;
  - the hideout's attack option was disabled;
  - the siege camp never prepared;
  - a defeated mission waited for the player's leave;
  - every fifth cell missed the crossing.
- **25280 and 17928:**
  - the engine hung opening the hideout boss's conversation, after the probe had killed agents through the cutscene before it;
  - every battania, khuzait and sturgia lord party owns ships and was filtered out;
  - the garrison sallies out against the siege camp, which needs about 94 campaign hours at the installed rate.
- **48860:** passed 120/120 before the review.

## Blocker

1. **The evidence was stale against the summarizer.** Run 48860 came before the probe recorded the hideout's expected scene, so the new hideout scene row failed on it, and `evidence/gameplay/land-missions.json` had been written with an older scenario file. Fixed: a fresh run after the whole batch, with the evidence and the fixture written from it. The summary also now requires that the run's rules equal the scenario file's, which makes a stale run fail on its own.

## Major

2. **Returns were checked only for field battles.** The settlement, hideout, siege and capture rows kept the navigation row alone. Fixed: each case records where its installed path puts the party, and is checked there with the vanilla land bound, on land, on valid navigation:
   - **town, castle, village and hideout:** the gate where the party entered;
   - **siege:** the camp, for the assault's return; lifting the siege then moves the party, which is recorded;
   - **capture:** the captor's position, where `PlayerCaptivity.EndCaptivityInternal` releases the party (a settlement's gate or port when held there).

   Guarded by `siege-returned-far`.
3. **Spawn positions were never evaluated.** Fixed: after deployment (or readiness), every active human agent must stand on a face of the scene's navigation mesh and inside the mission's boundaries. Guarded by `agent-off-navmesh`.
   - The face is looked up with the Vec3 form of `Scene.GetNavMeshFaceIndex`, as mission code calls it: at the agent's height, or half a metre below or above it for a seated or mounted agent.
   - The first version fell back to the Vec2 form, which installed code calls only on the campaign map (its flag is the map's land region). Confirming run 3940 hung as the tavern mission opened, after the field battles had passed every new check. That run is the regression `campaign-map-lookup-in-a-mission`.
4. **The missing-asset check was a filter.** Lines naming neither the world nor an opened scene were only tallied. Fixed: every missing line must be of a stock kind the scenario file names (`stockMissingPatterns`: shader variants compiled on first use, unvoiced dialogue lines), or the case fails. Guarded by `unknown-missing-line`.
5. **The culture tally counted the player's party and world B.** The player's hero and the probe's crew stood for `empire`, and B's battles counted for A. Fixed: the tally counts only the agents of other parties (by their origin's party), and B keeps its own tally (`culturesB`). Guarded by `culture-without-hero`.
6. **The idle check before a case was too weak.** In run 17928 the capture case started at a castle with the menu `hostile_action_end_by_peace` still open, without a reload. Fixed: a case reloads the designated save unless the map is idle, meaning no mission, encounter, map event, settlement, siege camp, captivity or open menu; the reason is recorded (`notIdleBefore`). Campaign time is stopped before every case.
7. **The absence proof skipped water-centre cells.** The battle map classes every cell with a land sample, and a marsh cell with a river at its centre is wetland. Fixed:
   - every cell with a land sample counts;
   - a crossing cell is one with any Bridge or Fording sample;
   - a battle stands on any usable sample point of its cell, the centre first.
8. **Enemies were left parked, and the battle's parties were not checked.** In run 17928 two parked lord parties joined the gear battles as the player's allies. Fixed: only the player's and the enemy's parties may fight, and the enemy is put back where it was after its battle (into its settlement if it left one). Guarded by `parked-party-joined`.

## Minor

9. **The retreat restated its input.** `Mission.OnEndMissionResult` itself ends by retreating, so the field battles and the retreat leave the same way. Fixed: every battle records its outcome before the encounter is finished, and the retreat requires that the player's side did not win and that the enemy remains with healthy troops. The documents say plainly that both paths retreat. Guarded by `retreat-won`.
10. **The siege row hid the shortcut.** Fixed: the rows state the camp's own progress, its rate and the hours a natural preparation needs; they require that progress was observed; and they name the advance to 0.99 as diagnostic setup. Guarded by `siege-camp-never-built`.
11. **The guard row could use an older battle's observation.** Fixed: each battle takes the guard's observations made since its encounter began (`MissionScenePatches.Observed` before and after). Guarded by `guard-from-older-battle`.
12. **The capture claimed a captivity menu it never saw.** The escape is the installed action applied without a notification, which opens no menu. Fixed: the claim is removed; the release position is checked (finding 2); and the war's peace moved into a `finally`.
13. **The hideout preferred an untested mission.** Sneaking in by night opens the stealth ambush mission (`HideoutAmbushMissionController`), which the phase-aware sweep does not cover. Fixed: the case takes the assault, running campaign time past the hideout model's night hours when needed.
14. **World B cannot run from a clean checkout.** Its placement and catalog receipt are git-ignored research outputs, as for MMR-51. The scenario check now reports them as notes, as MMR-51's does, and the limit is documented.
15. **The summary judged a run by that run's own rules.** Fixed: the verdict uses the scenario file's rules, and a row requires the run's rules to equal them.
16. **Stale header comments.** Fixed.

## Confirming runs

The batch's first confirming runs found three more problems, each fixed and guarded in the scenario file:
- **3940:** the game hung opening the town's tavern. The spawn check's fallback used the campaign map's navmesh query (finding 3). Fixed: the Vec3 form only.
- **37204 and 35564:**
  - **Spawns:** the town's seated agents, the tavern's musician and a siege troop stood off the navigation mesh at the stock scenes' own usable points. Fixed: an agent off the mesh is accepted only when something supports it, and that support is recorded: a scene object it uses (`ChairUsePoint`, `PlayMusicPoint`), a machine or scene entity it stands on, or the terrain within half a metre. Any other agent off the mesh, and the main agent off it, still fail.
  - **The escape** released the party about 1.18 from its captor. The release teleports twice: `EndCaptivityInternal`, then `ProtectPlayerSide` from the `OnHeroPrisonerReleased` listener. Fixed: time is stopped before the escape, and the bound is the installed 2 × 1.25 × the encounter radius.
- **43696:** one siege troop stood on the terrain, off the stock scene's mesh, with nothing else under it. The terrain support was added.

**Accepted run: `land_missions` 33816.** 163/163 recomputed checks, 14/14 cases, status ok. The only case absent is the wetland battle, absent from both worlds at full resolution. `evidence/gameplay/land-missions.json` records it, `docs/land-missions.md` describes it, and the fixture is `tests/Runtime/fixtures/land-missions-33816.json`.
