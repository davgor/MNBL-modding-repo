# MMR-33 independent review (2026-10-15)

Reviewer: an independent read-only agent (no repository edits, no game launch). It re-ran `tools/Test-Settlements.ps1` (16/16) and `tools/Test-CampaignRegistry.ps1` (55/55), dumped the real 286-settlement XML and intents, regenerated both policies (byte-identical) and checked the installed IL.

Below is the report verbatim, followed by the disposition of every finding.

---

MMR-33 independent review. Read-only. I re-ran the two offline harnesses (Test-Settlements 16/16, Test-CampaignRegistry 55/55) and checked against the installed IL in %TEMP%\mmr32-il and the merged catalog (campaign-catalog-10212). I also built a scratch dump of the real 286-settlement XML and intents (%TEMP%\mmr33-review\dump) and regenerated both policies in %TEMP%\mmr33-review. Both came out byte-identical to the repo copies.

**No BLOCKER found.** The emitted XML satisfies every attribute that Settlement.Deserialize, Town.Deserialize, Village.Deserialize and Hideout.Deserialize require.

## MAJOR

**M1. The smaller-world balance is reported, not validated.**
- Where: `src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs:388-399` (`Balance()` only stores numbers) and `tests/Campaign/SettlementTests.cs` (no check reads `r.Balance`).
- Results outside the installed envelope pass silently. Installed figures from the catalog:
  - hearth per fief is 968.8 against an installed 740.4 (+31%);
  - villages per fief are 3.03 against 2.31;
  - 23 of 45 towns have 5 villages (installed maximum is 4);
  - 1 town has 1 village (installed minimum is 2);
  - 9 of 26 castles have 1 village (installed: 2 of 76).
- Nothing checks food or market viability, for example production coverage per land group. Group 12 has 1 town with only a whaler and a lumberjack village.
- The ticket explicitly says "validate the smaller-world balance".
- Fix: add policy bounds for these ratios, relative to the installed values, and refuse or flag. Add a per-land-group production-coverage check.

**M2. The village-to-bound distance is unbounded.**
- Where: `SettlementFactory.cs:362-363`. `Validate` checks only the land group.
- In the real world:
  - `v_4a8438c360` is 153.3 units from its town `t_7c3c7cce5c`;
  - `v_63f602ca44` is 141.3 from the same town;
  - three more exceed the installed maximum of 54.6.
- The world's mean nearest-town spacing is 43.0, so 153 is 3.6× the spacing. Installed: maximum 54.6 against a spacing of 66.9, i.e. 0.82×.
- Both outliers exceed the engine's own trade-bound limit for castle villages. `DefaultVillageTradeModel.TradeBoundDistanceLimitAsDays` is 3× `GetAverageDistanceBetweenClosestTwoTowns`, about 129 here.
- `Village.Deserialize` still trade-binds town villages at any distance, so villagers make 150-unit market trips.
- This conflicts with c2 ("bounded") and with the ticket ("valid reachable market").
- Fix: bound the distance relative to town spacing. Rebind outliers to the nearest same-kingdom fortification, or refuse.

**M3. The harbour-village role is silently dropped.**
- The compiler marks 80 villages `harbour:true` (74 fishing, 6 harbor), and `ports.json` links each port candidate to a harbour village.
- `SettlementFactory.cs:170-194` ignores `harbour`. `Element()` at line 225 emits `port_pos` only for towns.
- Result: 72 fishing-type villages (fisherman 40, whaler 28, walrus 4), none with a port.
- In the installed game, all 36 fishing-type villages have a port, and 72 of 307 villages are coastal.
- NavalDLC `FishingPartyCampaignBehavior.CanHaveFishingParties` requires a Fisherman village type, the culture's fishing party template, and `Settlement.HasPort`. So no fishing parties can exist anywhere.
- Village prototypes are not split into coastal and inland, unlike towns. The in-game castle-village fixture got `empire_village_r_navalraid`, a coastal prototype, with no port.
- MMR-35 owns "village landing metadata", but neither the docs nor the intents hand this off.
- Fix: carry the harbour flag and port candidate in the intents and document the handoff to MMR-35, or emit the village `port_pos` from the port candidate. Split village prototypes by coast.

**M4. The c3 gate-position verification cannot fail.**
- Every fixture, and the whole world, has gate == centre:
  - `SettlementFactory.cs:323`;
  - placement `gatePosition == position`;
  - `SettlementTests.cs:155` even asserts it.
- `Settlement.Deserialize` (IL_0073-007b) sets `GatePosition = Position` before reading `gate_posX`. A missing or ignored gate attribute therefore looks identical to a correct one.
- What the checks actually cover:
  - `SettlementFixturesProbe.cs:56` checks only `IsValid()`;
  - `tools/summarize_settlement_fixtures.py:37-40` compares only the castle, hideout and village_3 centres;
  - the town centre, every gate and the port value are never compared (port is only checked for non-null);
  - the offline fixture check at `SettlementTests.cs:171-174` inspects no gate or port values.
- The run 6748 values do match the placement (town 609.375,165.625; port 609.375,171.875), but no check enforces it.
- `docs/settlements.md:107` ("all are the specified ones") overclaims.
- Fix: give the fixture town a distinct gate, for example the MMR-28 port `landPoint`, which is also a sensible staged gate for port towns. Compare exact centre, gate and port values in the summarizer and the offline check.

**M5. The market-price check cannot fail, and the evidence shows an unsupplied market.**
- `DefaultTradeItemPriceFactorModel.GetBasePriceFactor` clamps the factor to [0.1, 10] (IL). The probe bound `0 < p < 100000` (`SettlementFixturesProbe.cs:82`, summarizer:50) can never trip.
- Run 6748 prices against base values from the DefaultItems IL:

  | Item | Price | Base | Multiple |
  |---|---|---|---|
  | tools | 2530 | 250 | 10.1× (at the clamp) |
  | wine | 793 | 90 | 8.8× |
  | iron | 346 | 50 | 6.9× |
  | grain | 20 | 10 | 2× |

- This is a scarcity market, which is expected for the 2-town fixture world, but it is reported as "bounded" (`docs:113`).
- The ticket asks for trade prices to be checked with fixtures, and c2 covers markets.
- Fix: assert a band relative to the base value (for example ≤3×) for goods the fixture region produces, such as grain from wheat_farm, and record clamp hits. Pair this with the production-coverage check from M1.

## MINOR

1. **The port-location claims are inaccurate.**
   - `LocationComplex.Initialize` (IL) creates every template Location for every town. The merged `town_complex` includes `port` and a center→port passage.
   - Removing `<Location id="port">` at `SettlementFactory.cs:251` only drops the scene override; the template scene `empire_interior_tavern_a` remains.
   - Vanilla inland towns behave the same way, so the nord adaptation is engine-valid. The wording is still wrong in `SettlementTests.cs:113-115`, `docs:44` and `docs:110`.
   - The in-game check at `SettlementFixturesProbe.cs:48` compares the town against a castle, whose `castle_complex` has no port, so it cannot fail on the inland/port distinction.
   - The adapted nord towns keep a harbour centre scene (`nord_town_*`). That is an MMR-42 matter.
2. **Siege positions are not mentioned anywhere**, although the ticket requires keeping them separate. `Town.Deserialize` sets `BesiegerCampPositions1/2` to identity, and `SettlementVisual.OnStartup` fills siege-engine frames from the map entity's children, so this belongs to MMR-40. The deferral should be documented.
3. **The ownership-transfer check is weaker than its label.**
   - "moved" relies on `castleVillage.OwnerClan`, which is derived from the castle and cannot fail independently (`SettlementFixturesProbe.cs:86`).
   - "restored" does not check the village.
   - Village membership in the clan lists and the Kingdom inverse lists are never checked.
   - `docs:114` overclaims "its village between the two clans' lists".
4. **The castellan rule is never exercised in game**, because the fixture castle is owned by the ruling clan_1. The offline check at `SettlementTests.cs:91` only asserts `!Ruling`, not that the owner is the castellan's clan, so the label overclaims.
5. **`Distance()` ignores the periodic seam.** Columns wrap at 800 (`travel-graph.json` grid). Castle nearest-village selection near the seam could pick the wrong village (`SettlementFactory.cs:432-437`). It is latent: no seam pairs exist in this world.
6. **A missing population silently maps to the class minimum.** `Math.Max(1.0, NaN)` returns NaN, which gives a negative percentile that is clamped to 0 (`SettlementFactory.cs:415`, 167). A missing rural value gives hearth 100. Neither is refused.
7. **Robustness:** a town with 2 or fewer same-group villages and at least one castle leaves that castle with 0 villages, and the whole world is refused (`SettlementFactory.cs:357`). It only passes here because every town with castles has 4-5 villages.
8. **`RegistrationPlan.FromCompilerRecords` is stale against the factory output** (`RegistrationPlan.cs:122-162`). It still plans compiler castle owners and village bounds; 16 owners and 43 bounds now differ. No offline test runs `FromDocuments` on the factory's 286-settlement XML. This is a risk for MMR-45.
9. **The in-game fixture reads `economy.json` and `prototypes.json` from the repo without hash pinning** (`GeneratedCampaignRegistration.cs:89-90`).
10. **The castle-village trade bound is no longer guarded anywhere.** The closure now skips castle villages, which is correct per IL. But no settled-mode check confirms a non-null `TradeBound` after new game, and the engine can return null beyond the distance limit or when hostile. In this world the maximum Euclidean distance to a same-kingdom town is 38.3 against a limit of about 129, so it is fine here.
11. **The in-game probe records building levels but does not assert them** (c1 "required buildings"). The run 6748 levels do match the policy's medium tier.
12. **Doc accuracy:**
    - `docs/settlements.md:19` cites `evidence/fwg/settlements-review.md`, which does not exist.
    - `docs/campaign-registry.md:91` places `UpdateTradeBounds` next to the post-load town-village observation (run 2244). `UpdateTradeBounds` iterates `AllCastles` only, so it does not explain that.
    - `campaign-registry.md:158` still lists trade bounds after a load as an MMR-33 open question, and MMR-33 does not answer it.

## NIT

- Building medians are non-monotone across tiers: capital `guard_house` is 0 while small is 1, and medium `waterworks` is 0.
- The hideout text "A hideout of the forest_bandits." shows a culture id to the player.
- `evidence/fwg/settlements-tests.txt` is UTF-16, from Tee-Object.
- The port envelope check measures centre→port against the installed port→gate maximum plus 6.25 (17.2). 19 of 30 ports exceed the installed port→centre maximum of 6.96, and one exceeds 10.97 (11.05). This is acceptable as staged, but the reference should be stated correctly.

## Checked and found correct

- **Deserialize attribute sets match the docs.** Required component attributes are present: `background_crop_position`, `background_mesh` and `wait_mesh` from the prototype; `castle_background_mesh` for villages; `prosperity` and `hearth` set by the factory.
- **Building ids:** `Town.Deserialize` would silently drop unknown building ids, but every emitted id is validated as a registered `BuildingType`.
- **Port flag:** `HasPort` is true when `port_posX` is present and not (0,0). Castles and villages never emit port attributes.
- **The MMR-30 `TradeBound` change is correct and does not hide a defect.** `Village.Deserialize` trade-binds only when the bound settlement `IsTown`. `VillageTradeBoundCampaignBehavior.UpdateTradeBounds` assigns castle villages on new game, load, war, peace and owner change. It picks the nearest same-faction town within 3× the average distance between the two closest towns, else a non-hostile town, else null. `MBObjectStore:87-95` matches this, and the castle unit test exercises the closure logic.
- **XML structure:** towns and castles come before villages, component ids are unique (0 duplicates), `complex_template` matches kind (215 village, 45 town, 26 castle), and declared keys are present.
- **Names:** for all 286 settlements the localized name equals the compiler's `nameLocalizedXml`. Lore text is never copied; `Element` builds a fresh settlement.
- **Economy maths:**
  - percentile and quantile mapping, ranges and terciles are as documented (1700-3100, 3100-4000, 4000-5600; castles 420-1100; hearth 100-825);
  - means are 3277.8 / 847.3 / 319.9;
  - the shipyard rule holds;
  - village-type pools match the policy.
- **The population ×1000 test** is a real regression guard, even though the code makes it hold by construction.
- **Policies:** both policies regenerate byte-identically and are hash-pinned to the catalog in the harness.
- **Fixtures take the verified path.** They register through the MMR-30 registry: `FromDocuments`, phase loading, closure clean in Registration and Settled modes in run 6748.
- **The entity shim** mirrors `SandBox.MapScene.AddNewEntityToMapScene` (Instantiate plus SetLocalPosition) apart from the prefab name and `entity.Name`. `DiagnosticMapScene` re-implements `IMapScene`, so interface dispatch reaches it (log lines 73-75). Low risk and fixture-only.
- **Map prefabs:** the `mapPrefab` derivation is confirmed against Main_map; towns are `__empty_object`.
- **Earlier failures are confirmed in their logs:** 35960 was the closure trade-bound refusal and 36452 was the `SettlementVisual.OnStartup` NRE.
- **Counts in the docs all check out:** 45 towns (30 port), 26 castles (16 castellan-owned), 215 villages, 43 rebound, 3 adapted towns, 475 slots, intents sha `1b0015b3202a`.
- **Run 6748 values match `docs:111-113`.**

## Verdicts

- **c1: Met.** Components, owners (in-kingdom clan intents), cultures, buildings and template-resolvable locations with scenes are all present. The port-location wording is inaccurate (MINOR 1), and building levels are not asserted in game (MINOR 11).
- **c2: Not met as claimed.**
  - Bindings and production are valid by land group and by a versioned policy.
  - Binding distance is unbounded (M2), the smaller-world balance is not validated (M1), the harbour role is dropped (M3), and the market check cannot fail while showing clamp-level prices (M5).
  - Starting food and gold come from the engine and are shown for only one fixture, with loose bounds.
- **c3: Partially met.**
  - Met: the lifecycle path, LocationComplex, bound villages, the clan settlement list for the town and castle, and the transfer evidence.
  - Gate verification cannot fail (M4), the port value is unchecked, village and kingdom inverse lists after transfer are unchecked (MINOR 3), and the castellan rule is untested in game (MINOR 4).

---

## Disposition

Every finding was fixed in one batch, then confirmed:
- offline: `tools/Test-Settlements.ps1`, 22/22 (`evidence/fwg/settlements-tests.txt`, now UTF-8);
- in game: run 37100, 13/13 (`evidence/runtime/settlement-fixtures.json`).

Run 30552 (12/13) came first. It caught the one error in the new food table: the engine counts grapes as food. The table was corrected and 37100 confirmed it; no code changed between the two runs.

### MAJOR

**M1. Balance validated.** Economy policy v2 adds bounds, and the factory refuses a world outside them:
- `binding.townMaximum` 5 (the installed maximum plus one, because there are fewer castles per town) and the existing castle range of 1-2;
- `balance.band` 0.75-1.5× the installed villages per fief (2.31) and hearth per fief (740.4);
- `food`, described below.

Size 129 (both per-fief values are fixed by the source's village and fief counts):

| | Generated | Installed |
|---|---|---|
| Villages per fief | 3.03 (1.31×) | 2.31 |
| Hearth per fief | 968.8 (1.31×) | 740.4 |
| Villages per town | 1: 1, 2: 9, 3: 9, 4: 5, 5: 21 | 2-4 |
| Villages per castle | 1: 8, 2: 18 | 1-2 |

The town with 1 village was given only 1 by the source. It is reported as `townsBelowMinimumBySource`; any other town below 2 is refused.

Food coverage:
- Each village type's daily food production comes from the installed IL and is checked in game.
- A town's trade-area supply (its own villages plus the castle villages trading with it) must reach the smallest installed supply, 10. The installed median is 86.
- One village was redrawn, lumberjack to wheat farm. The generated minimum is 19 and the median 78.
- The supply per land group is recorded.
- Refusal tests cover the band and the food minimum.

**M2. Village distance bounded.** All distances are periodic.
- The spacing is the mean nearest-town distance, 42.5.
- The preferred limit is the installed maximum ratio (0.8158) × 1.25 = 1.02× (43.3).
- A village beyond the limit is rebound to the nearest town or castle of its land group within it: one with room first, then one of its own kingdom.
- With none within the limit, the village is remote. It must stay within the engine's own trade limit, 3× (127.5, `DefaultVillageTradeModel.TradeBoundDistanceLimitAsDays`).

Size 129:
- The compiler's binding reached 3.61×.
- 5 villages were rebound: `v_4a8438c360` and `v_63f602ca44` among them, and 3 of the 5 to another kingdom's fortification.
- 2 villages are remote, at most 2.32×. Each has no fortification of its land group within the limit, which the tests check independently.

Every castle village has a town of its kingdom within the engine limit, recorded as the intent's `tradeTown` (farthest 91). A castle village without one is refused. Refusal test: with a 1.2× engine limit, the remote villages are refused.

**M3. Harbour role kept.**
- Each of the 80 harbour villages gets a staged port at the centre of its MMR-28 harbour cell, a navigable sea cell. All are within the installed village port-centre maximum (13.09) plus one cell; the farthest is 12.6.
- Village prototypes are split into `village_coastal` (installed villages with a port) and `village_inland`.
- A fishing type needs a port. The 5 non-harbour fishing-class villages draw non-fishing types, and all 69 fishing-type villages have ports.
- The intents carry `harbour` and `harbourCell`. The castle-village fixture is now inland.
- Handoff: MMR-35 owns the village landing metadata, and its comment says so.

**M4. Gates and ports falsifiable.**
- The fixture town's gate is 1.875 toward its port and the castle's 1.875 east, both inside the centre's verified land cell.
- The probe compares every fixture's centre, gate and port with the XML the engine read.
- The summarizer compares them with fixed values: the placement points and the gate rule.
- The offline fixture check asserts the exact `posX`, `gate_posX` and `port_posX` attributes.

Run 37100 shows a town gate of (609.375, 167.5), a castle gate of (511.25, 134.375) and a town port of (609.375, 171.875). The docs overclaim is removed.

**M5. Prices against base value.**
- The probe records price, item value and multiple for grain, butter, cheese, fish, iron, tools and wine.
- It asserts grain, which the fixture's wheat farm supplies, at most 3× its base value: run 37100 has 1.7×.
- It records clamp hits (≥ 9.5×): tools at 10.1×.
- The docs describe the two-town fixture world as unsupplied for most goods.

### MINOR

1. **Port-location wording** corrected in the docs and the tests. The template creates a port location for every town; only the scene override is the prototype's. Only inland towns drop it, matching the installed inland towns, and `Element()` now drops it only for towns. The in-game check now compares the town's live port scene with its XML override (`empire_shipyard`).
2. **Siege positions:** the deferral to MMR-40 is documented, with the reason from the IL (`Town.Deserialize` and `SettlementVisual.OnStartup`).
3. **Transfer check.** Moved and restored now each require all of these, for both clans and both kingdoms (the fixture clans are in different kingdoms):
   - the castle in the new owner's settlement and fief lists, and out of the old owner's;
   - its village in the new owner's village list, and out of the old owner's;
   - the village's map faction following the owner;
   - the kingdom fief and village lists.
4. **Castellan:** the offline check now asserts each castellan castle's owner is exactly the clan that castellan leads, and the other castles the compiler's owner. The in-game fixture castle is still ruling-clan owned; this is documented under what the evidence does not prove.
5. **Periodic distance:** used for all binding, spacing and trade-town distances (columns wrap at 800).
6. **A missing population estimate is refused**, and so is a town without a rural population. Refusal test: a town without an estimate.
7. **Robustness:** castles short of villages now take from the fullest town of their kingdom with more than 2 (step 4). A castle still without one is refused, as before.
8. **Full-world registration plan:** the offline tests run `RegistrationPlan.FromDocuments` over the factions, heroes and all 286 settlements in the proven order.
   - Settlements, factions and references have no problem.
   - It found 150 hero family forward references (spouse, father, mother). They are MMR-32 records, of the kind the installed Heroes.xml has 127 of. They fall outside MMR-30's two-pass list; handed to MMR-45.
   - `FromCompilerRecords` staleness (16 castle owners, 44 village bounds) is recorded for MMR-45.
9. **Policy pinning:** in-game registration now requires the economy policy and prototypes to have been made from the catalog's Settlements, and records their sha256. The summarizer checks them against the repository's current files.
10. **Castle-village trade bound:** guarded offline (every castle village has `tradeTown` within the engine limit) and in game (the castle village's trade bound is the town).
11. **Building levels asserted in game:** the probe compares live levels with the XML, and the summarizer compares them with the policy tiers.
12. **Docs:**
    - `evidence/fwg/settlements-review.md` exists (this file).
    - `docs/campaign-registry.md`: `UpdateTradeBounds` is now described as castles-only and not as the explanation for run 2244. The open question is reassigned to MMR-49 (loading).

### NIT

- **Building tiers** are monotone: each tier is at least the one below. The shipyard is left out of the tiers, because the port rule owns it.
- **Hideout text** is "An outlaw hideout in the wilds.", with no culture id.
- **The test log** is written as UTF-8 by `Test-Settlements.ps1`.
- **Port envelope:** the reference is stated correctly. It measures from the staged gate against the installed port-gate maximum 10.97 plus one cell (farthest 11.05). 19 of the 30 ports are farther than the installed port-centre maximum, 6.96, until gates are placed.

### Verdicts after the fixes

- **c1: met.**
- **c2: met.** Bounded binding, validated balance, food coverage, harbour ports, fishing types only with ports, and base-relative prices in game.
- **c3: met.** Falsifiable gates and ports, building levels, port scene, and clan and kingdom inverse lists through a transfer. The castellan rule is checked offline only.
