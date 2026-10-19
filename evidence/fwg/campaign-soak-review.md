# MMR-51 independent review and fixes

One independent review of the MMR-51 working tree. The reviewer was an agent with read-only access. It did the following without editing files or launching the game:
- read the probe, the summarizer, the scenario file and the refactored two-world probe;
- checked engine claims against the installed IL (TaleWorlds.CampaignSystem, SandBox, and its own disassembly of TaleWorlds.CampaignSystem.ViewModelCollection);
- compiled the module (0 warnings, 0 errors) and ran the scenario check;
- read the raw outputs of development runs 47176 and 21164.

It reported 2 blockers, 3 major and 8 minor findings. All were fixed in one batch. The confirming runs then found five more problems, each fixed and each added to the regression fixture (below). Accepted run: `campaign_soak` 41920, 55/55 recomputed checks, 14/14 scenarios, status ok.

## Blockers

1. **The ruler-death expectation contradicted the installed rule.** `Kingdom.AddDecision` holds the election of a kingdom other than the player's at once (`KingdomElection.StartElection`), so a king selection is never pending there. With one candidate clan there is no election at all (run 47176). Fixed: the expectation follows `KillCharacterAction.ApplyInternal` and `Kingdom.AddDecision` branch by branch:
   - two or more candidates: a king selection is added, the elected ruling clan is applied, it is one of the candidates, and without an heir in the ruling clan another clan rules;
   - one candidate: that clan rules;
   - none: the kingdom is destroyed.

   A kingdom with two or more candidates is preferred, and a decision-concluded listener records the election's end. Run 41920: 2 candidate clans; the election was held at once, electing the heir's clan, and concluded.
2. **Villager sales always measured 0.** `MbEvent.AddNonSerializedListener` puts a listener at the head of the list and `InvokeList` walks from the head, so the probe's `SettlementEntered` listener ran before `VillagerCampaignBehavior`'s sale. Fixed:
   - the goods, trade gold and town gold are taken on entry, before the sale, and compared at the next hour;
   - pack animals, which the sale keeps, are not counted as goods.

   Run 41920: 29 entries; 27 sold (1,602 items); the other 2 carried only pack animals.

## Major

3. **The ownership step dissolved the war's target.** On seed 42 most kingdoms hold a single fief. Taking the target's only town made `FactionDiscontinuationCampaignBehavior` dissolve it, which voided the war, and the follow-up read the elimination as peace. Fixed:
   - the war targets a kingdom of two or more fiefs;
   - the ownership row requires the target to survive, still at war;
   - the follow-up tells elimination from peace;
   - the audit counts kingdoms by fief count (22 of 40 hold one fief).
4. **Weak coverage of required unreachable destinations, and villagers hidden by the Hold exclusion.** Fixed:
   - unfound paths are read every frame, not hourly. A party whose path to a settlement is not found fails the scenario; point targets are recorded;
   - every party holding still for the stuck window is recorded with its behaviours, target, home, raid state, trade town, sea state and navigation, and with the installed reason for the hold. A villager holding without one fails;
   - every village's trade town is audited;
   - MMR-41's `CampaignPlayability` runs against the live campaign of both worlds.

   The confirming runs showed that the 25 to 34 holding "villagers" were War Sails' fishing parties, fishing at sea (finding 18).
5. **Motion thresholds passed trivially.** Fixed: at least half of each kind of party must move (`movedFraction` per world in the scenario file). The replenishment check is labelled operability, not a rate.

## Minor

6. **The visit's travel was vacuous** (distance 0: the player starts at that town's gate). Fixed: the player travels to another town by land, and the check requires travel time and no encounter before the move. Run 41920: 8.67 hours.
7. **The trade did not go through the trade screen's path.** `SellItemsAction` is an AI trade action. Fixed: the trade goes through an `InventoryLogic` built as `InventoryScreenHelper.OpenScreenAsTrade` builds it, committed by `DoneLogic`.

   The recruitment remains the recruitment screen's domain sequence (`RecruitmentVM.OnDone`), as the screen writes the roster and slot. The scenario says what the game decides there: the volunteer on offer, its availability and its cost.
8. **The summarizer was not tied to the run's inputs.** Fixed:
   - it grades with the rules, hours and motion fractions the run recorded (`outputs.rules`, `outputs.worldPlans`, each observation's `hours`);
   - it records the scenario file's hash at the run and now;
   - the docs say the summary is the verdict.
9. **Inputs were not validated up front.** Fixed: the probe checks every scenario key and B's files before any game starts, and the offline check validates each world's fields.
10. **c3 was not met, and the mechanism was metadata only.** Fixed:
    - **13 regressions** in the scenario file, each with the run or finding, cause, fix and guard;
    - **a committed fixture:** the accepted run's output, trimmed, with user paths replaced;
    - **a pure `recompute(result, plan)`** in the summarizer;
    - **an offline replay** (`tests/Runtime/test_campaign_soak_regressions.py`): the fixture passes every scenario, and each of the 12 mutation-guarded regressions, re-injected into a copy, fails its scenario (13/13);
    - **`tools/Test-CampaignSoak.ps1`** runs the scenario check and the replay.
11. **Robustness.** Fixed:
    - the observation's listeners and its exception handler are registered inside the `try` whose `finally` removes them;
    - the exception handler has a `[ThreadStatic]` re-entrancy guard;
    - an exception canary and an assert canary prove both hooks;
    - the engine's asserts are captured by wrapping `Debug.DebugManager`, through which every `Assert`, `FailedAssert` and `SilentAssert` passes, inlined calls included; this needs no Harmony patch, and the canary is not forwarded;
    - the diagnostic listeners are wrapped against throwing into the dispatch;
    - a party's first sample counts toward no streak;
    - the player's shelter is recorded as setup in each phase.
12. **A town starting without gold went unflagged.** Fixed: the audit lists towns without gold, villagers' sales record the town's gold on entry, and a town without gold for the whole day fails.

    The cause is recorded in the docs: towns earn their first gold from the new-game consumption passes, and this town's opening market earned none. It earns within the day.
13. **The two-world probe refactor.** The reviewer found the extraction of `StartGenerated` and `StartFixture` faithful. It also found that the file had been rewritten with LF line endings, against the repository's byte-exact `* -text`: CRLF was restored, and the diff is back to the refactor.

    The prisoner expectation change it noted (`prisonerHeldAtLoad`, `prisonerAfterRun`) belongs to MMR-49's accepted fix for run 2804, which is still uncommitted, not to this ticket.

## The confirming runs' findings

14. **The headless trade screen's callbacks** (runs 4652, 34968). `InventoryLogic` invokes four callbacks unchecked that the screen's view model hooks: the `AfterTransfer` and `AfterReset` events and the `TotalAmountChange` and `DonationXpChange` actions. The first transfer threw after moving the goods and before any gold settled. Fixed: no-op handlers stand in for the screen's refresh, and a failed trade records the exception's full text. Run 41920: bought for 25 and sold for 21, the player's and the town's gold moving by exactly those prices.
15. **No volunteer offered** (run 4652). The nearest town's notables offered the player nothing within its relation limit (`VolunteerModel.MaximumIndexHeroCanRecruitFromHero`). Fixed: the visit goes to the nearest town by land that offers a volunteer and an affordable trade good.
16. **The fixture world's player-start route** (run 4652). MMR-19's fixture world replaces no creation culture's start, so MMR-41's player-start route has nothing to hold there. Fixed: with no starts checked, the refused route is recorded as not applicable, and every other route must hold (10 of 10).
17. **A sold-out market counted as an invalid reference** (run 34968). One town sold its last trade-good stack 30 hours in. Fixed: towns without trade goods are recorded in the census, and every town must stock trade goods at initialization. World-wide, markets halve in the first hours after the new game and then fall about 7% a day while trade-good stacks restock.
18. **Fishing parties** (run 4652). The 34 villager parties holding still at sea were War Sails' fishing parties: `FishingPartyComponent` extends `VillagerPartyComponent` and sails only, and `FishingPartyCampaignBehavior` holds them at their fishing point. Fixed: they are a kind of their own, and a fishing party's hold is explained while it fishes or roams. Run 41920: 34 fishing holds, all fishing; no villager or fishing party held without an installed reason.

## Results

**In game: `campaign_soak` 41920, 55/55 recomputed checks, 14/14 scenarios, status ok** (`evidence/gameplay/campaign-soak.json`; figures in `docs/campaign-behavior-compatibility.md`).
- **A.init:** coherent; all 672 semantic routes hold in the live campaign.
- **The player's actions:**
  - **visit:** 8.67 hours of travel by land; the engine opened the town menu;
  - **trade:** through `DoneLogic`, exact;
  - **recruitment:** exact;
  - **leaving:** through the menu's leave path.
- **The natural day:**
  - 0 stalls, exceptions, asserts, positions off navigation, unfound paths and stuck parties;
  - at least half of every kind of party moved;
  - caravans traded, villagers sold their village's goods, lords recruited;
  - no town without food, and none without gold all day.
- **Replenishment:** 887 slots refilled.
- **The diagnostic actions:** war, ownership, clan succession and ruler succession (the election branch), each with its installed outcome. The six hours after them were clean.
- **B:** independent of A, coherent, its routes holding, twelve clean hours.
- **Baselines:** all three held.

**Offline:**
- `tools/Test-CampaignSoak.ps1`: the scenario check (14 scenarios, 13 regressions, 0 problems) and the replay (13/13);
- the module compiles with warnings as errors.

**Runs not accepted:**
- 47176: development run; B was refused for its missing inputs, and four expectations were wrong;
- 21164: two expectations wrong (blockers 1, 2);
- 4652: findings 14–16 and 18;
- 34968: findings 14 (a second callback) and 17.

## Limits

- **World B is MMR-19's fixture world**, not a second production world. The production generator activates only seed 42 today (MMR-49's limit).
- **A short run.** One day and six hours on A and twelve hours on B establish basic operability, not long-term balance:
  - kingdom consolidation from many single-fief kingdoms;
  - market levels;
  - raids, sieges and prisoner escape over many days.
- **The diagnostic actions call installed actions directly**, as their own entry points do. The player's trade and recruitment run the screens' logic without the screens.
- **The two-world matrix (MMR-49) was not re-run after the refactor.** The extracted helpers ran in all five soak runs (`StartGenerated`, `StartFixture`), and the review found the extraction faithful.
- **Each run's new campaign writes its initial save**, as the product does (`mnbl_1609fb72_start_*.sav`, one per run). These are kept, as in earlier tickets.
