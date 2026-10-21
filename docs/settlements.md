# Settlements and the starting economy (MMR-33, 2026-10-15)

`SettlementFactory` turns the MMR-27 compiler towns, castles and villages into the Settlements XML that the MMR-30 registry loads. It writes one intent per settlement and resolves the compiler slots MMR-33 owns.

**Inputs:**
- positions: the MMR-28 travel graph and ports, all staged;
- owners: the MMR-31 clans;
- castle and village cultures: MMR-32;
- the economy policy;
- installed settlement prototypes.

| | |
|---|---|
| Code | `src/MNBL.ProceduralWorld/Campaign/SettlementFactory.cs`. Pure managed; `Element()` is the one builder the world and the fixtures share. |
| Policy | `worldgen/policies/economy.json` (`mnbl.economy` v2), written by `tools/make_economy_policy.py` from the installed settlements (hash-pinned) and the installed IL (food table). |
| Prototypes | `worldgen/templates/settlements/prototypes.json`, written by `tools/make_settlement_templates.py`. Per culture and kind (`town_port`, `town_inland`, `castle`, `village_coastal`, `village_inland`, `hideout`): the installed prototypes' assets and their vanilla map prefab. |
| Tests | `tests/Campaign/SettlementTests.cs` via `tools/Test-Settlements.ps1`. The real size-129 world goes through the accepted MMR-31/32 factories; 22 checks (`evidence/fwg/settlements-tests.txt`). Every bound is recomputed from the policy and inputs. |
| In game | the `generated_campaign` probe with `settlementFixtures=<repo>`, summarized by `tools/summarize_settlement_fixtures.py` (`evidence/runtime/settlement-fixtures.json`) |
| Evidence | `evidence/fwg/settlements.json`; the review and its disposition are in `evidence/fwg/settlements-review.md` |

## The lifecycle path

Settlements are made through the installed XML path, never by property assignment. `Settlement` is sealed, with important get-only state.

What the engine reads (installed IL):
- `Settlement.Deserialize` reads:
  - `name`, `posX`/`posY`, `gate_posX`/`gate_posY` (else the gate is the centre), `port_posX`/`port_posY` (`HasPort`), `culture`, `owner` and `text`;
  - its component;
  - `Locations` (`complex_template`, and each `Location`'s scene overrides);
  - `CommonAreas`.
- **Components:**
  - `Town.Deserialize`: `is_castle`, `prosperity`, `Buildings`;
  - `Village.Deserialize`: `village_type`, `hearth`, `bound`;
  - `Hideout.Deserialize`: its meshes.
- **Village binding:** `Village.Deserialize` trade-binds a village only when its bound settlement `IsTown`. A castle's village gets its trade town later, from `VillageTradeBoundCampaignBehavior.UpdateTradeBounds`: the nearest town of its faction within 3× the average distance between the two closest towns.
- **Effect on MMR-30:** the registration closure now asks only a town's villages for a trade bound, and the registry tests (55/55) include the castle case. Hideout is a registered world type.

**Locations.** `LocationComplex.Initialize` creates every location of the complex template, so every town has a port location. A prototype's `Location` elements only override scenes. An inland town keeps no port scene override, like the installed inland towns, so the nord towns that adapt a port prototype drop it.

**Prototypes are assets only.** A prototype supplies:
- the component's meshes;
- the location complex template;
- the location scenes;
- the common areas.

The world supplies everything else: id, name, culture, owner, positions, prosperity, hearth, village type, bound settlement and text. The prototype's Calradian lore text is never copied; each settlement gets a policy text such as "The castle of {name}, in the {realm}". The prototypes stream draws each prototype from the settlement culture's installed list:
- a port town takes a port prototype;
- an inland town takes an inland prototype; nord has none, so 3 nord towns adapt a port prototype;
- a harbour village takes a coastal village prototype (installed villages with a port), and any other village an inland one.

The XML lists every town and castle before the villages bound to them (the MMR-30 ReadyAt rule).

## What becomes what (size 129)

| | Rule | Count |
|---|---|---|
| Towns | compiler owner (a clan intent of its kingdom), MMR-27 presentation, the buildings of its class tier, a shipyard exactly when it has a port | 45 (30 port towns) |
| Castles | owned by the vassal clan its castellan leads (compiler hero `hero-castellan-<castle uid>`, MMR-31), else by the compiler owner; MMR-32 settlement culture; castle buildings | 26 (16 castellan-owned) |
| Villages | bound by the binding rules below; MMR-32 settlement culture; village type from the production class; harbour villages get a port | 215 (80 with a port) |

## Binding

Distances are on the periodic world (columns wrap at 800). The **spacing** is the mean distance from each town to its nearest town: 42.5 on world A (seed 42, size 129), against 66.9 for the installed map.

**The village quota decides the binding** (map epic 2, MMR-252; [travel-graph.md](travel-graph.md), "Village quota"). The travel compiler binds exactly 3 villages to every town and 2 to every castle, castle villages included, and writes it into `campaign.json` version 2 (`bound`, `boundKind`, `sourceTown`, `origin`). `Campaign/VillageBinding.cs` passes it through and rebinds nothing; `SettlementFactory` calls it, and so does the world preview (`docs/world-preview.md`), which shows each village in its bound settlement's kingdom as the campaign has it. The four-step rebinding and the evening-out of MMR-33 and MMR-46 are gone, and so are `economy.json`'s `townMinimum` and `townMaximum` (clean break).

- **Limits.** A village farther from its bound settlement than the preferred limit, 1.02× the spacing (43.3 on world A; the installed maximum ratio 0.82 plus 25%), is *remote*. It must stay within the engine limit, 3× the spacing (127.5), the engine's trade limit. World A: 185 villages within the limit, 2 remote, the farthest at 1.57×.
- **Counts.** `SettlementFactory.Validate` refuses a town without exactly `townVillages` (3) and a castle without exactly `castleVillages` (2) bound villages. A fortification with none is refused with its crash paths named: `SnareTheWealthyIssueQuest`, `incident_the_quiet_life` and `incident_wages_of_war_and_diseases` throw on it (RT-31). The graph validator refuses one too (`graph.village_binding`).
- **Campaign records of version 1 are refused** ("not mnbl.campaign-records version 2").

| | Generated (world A, size 129) | Installed | Bound |
|---|---|---|---|
| Villages per town | 3: 45 | 2-4 | exactly 3 |
| Villages per castle | 2: 26 | 1-2 (74 of 76 have 2) | exactly 2 |
| Villages per fief | 2.63 (1.14×) | 2.31 | 0.75-1.5× installed |

The exact 3/2 rule is the owner's; it cuts a town's trade area from vanilla's median of 5 villages to 3, and MMR-247 records the economy effect (RT-42). acc6's 30-day soak (world 1 against the vanilla War Sails sandbox): mean town prosperity 3,186 → 3,213 (+0.9%) against vanilla's 3,483 → 3,448 (−1.0%), town food rising (+25%) where vanilla's fell (−31%), village hearths growing at vanilla's pace (+7.6% against +9.3%). The cost is a prosperity level 7-9% under vanilla's that does not erode; the balance pass changed nothing (`evidence/map-epic-2/balance-pass.json`).

**Castle villages.** Each castle village has a town of its castle's kingdom within the engine's trade limit. The intents record it as `tradeTown`. A castle village without one is refused.

## Economy

Every value is a quantile of the installed settlements' own distribution, taken at the settlement's percentile among the generated ones. It therefore stays inside the installed range whatever the world's size. Population only orders settlements: multiplying every population by 1000 changes nothing.

| Value | Installed distribution | Generated percentile |
|---|---|---|
| Town prosperity | the installed town values split into terciles (1700-3100, 3100-4000, 4000-5600) for the compiler classes small, medium and capital | log population within the class |
| Castle prosperity | installed castles, 420-1100 | the garrison town's log population among all towns |
| Village hearth | installed villages, 100-825 | the village's share of its source town's rural population (`sourceTown`: the compiler town, or the garrison town for a castle's synthesized village; RT-10), shared among the villages of that source town |
| Village type | the culture's installed village types of the source production class (farming, fishing, resource, harbor), weighted by installed counts, from the villageTypes stream. A fishing type needs a port (all 36 installed fishing villages have one), so a village without a port draws from its class without them, else from its culture's farming types. A coastal village (one with a port) draws a fishing type of its culture with probability `villageMix.coastalFishingShare` (0.5, the installed share among villages with a port; the stream's draw 1), else a non-fishing type of its class (RT-08). | — |
| Buildings | median installed level per building, by prosperity tercile (towns) and for castles. A larger tier is never lower than a smaller one. Port towns add `building_shipyard`. | — |

A missing population estimate or rural population is refused. World A: castle villages' hearth median 253 (17 values, none at the minimum 100) against town villages' quartiles 233/311/427; before the source-town share every castle village would have taken the minimum (RT-10).

**Village mix floors (RT-08, RT-09).** Every culture keeps a world-level floor of horse-ranch villages (its installed `*_horse_ranch` type) and wheat farms: `max(1 when its installed pool has the type, floor(share × the culture's generated villages))`, with the shares of `villageMix.floors`. After the draw, a culture below a floor redraws villages to the floor type: villages without a port first, then of the farming class, then the type with the least food, then id; never one whose own type is at its floor. The food pass never redraws a village whose type is at its culture's floor, and `Validate` refuses a culture below one. World A: 8 floor redraws, every culture at or above both floors (nord has no horse ranch); its 61 coastal villages of cultures with fishing types drew a fishing type at 0.459.

**Food.** Town food stocks change with consumption, the bound villages' hearth levels and the food items sold in the market (`DefaultSettlementFoodModel`). Those items come from the villages' production.

From the installed IL (`DefaultVillageTypes`, `NavalVillageTypes`):
- every village type produces grain: wheat farms 50 a day, every other type 3;
- fishers add fish 28;
- cattle farms add butter and cheese 8, sheep and swine farms 4;
- date farms add dates 8, olive groves olives 12, vineyards grapes 11 (the engine counts grapes as food; fixture run 30552 caught this);
- whalers and walrus hunters produce fish 5 and no grain.

A town's trade-area supply is the daily food production of its own villages plus the castle villages that trade with it. The smallest installed supply is 10; the median is 86.
- Every generated town must reach 10. One below it has its least-food village redrawn to the most food-producing type its culture allows.
- World A (seed 42, after the village quota): no village needed a redraw; the generated minimum is 11, and the median 64.
- The fixture probe checks this food table in game.

**Starting values the engine derives at new game.** These follow from prosperity, hearth, buildings and bound villages through the installed behaviours. The fixtures show them in game:
- food stocks (FoodConsumptionBehavior);
- militia (MilitiasCampaignBehavior);
- town and village gold (Town.OnInit, Village.OnInit);
- workshops (WorkshopsCampaignBehavior.InitializeWorkshops);
- garrisons (`FillGarrisonPartyOnNewGame`, from the culture's `default_party_template`, MMR-32).

**Slots.** All 475 MMR-33-owned compiler slots are resolved in the intents: each town's `prosperityAndEconomy`, and each village's `villageType` and `hearth`.

## Positions

Positions are staged (`pendingEngineValidation`).
- **Centre:** the MMR-28 settlement point.
- **Gate:** the centre in this factory; `ApplyResolvedPositions` writes MMR-40's resolved gate (`docs/settlement-entities.md`). Town and castle gates are written explicitly.
- **Port town:** its port is the MMR-28 candidate's sea point, within the installed port-gate maximum (10.97) plus one cell of the staged gate.
  - The farthest is 11.05.
  - 19 of the 30 ports are farther from the centre than the installed port-centre maximum (6.96). That holds only until the gates are placed.
- **Harbour village:** each of the 80 MMR-27 harbour villages gets a port at the centre of its MMR-28 harbour cell, a navigable sea cell. All 80 are within the installed village port-centre maximum (13.09) plus one cell; the farthest is 12.6.
  - This gives all 69 fishing-type villages the port that NavalDLC fishing parties need (`FishingPartyCampaignBehavior.CanHaveFishingParties`).
  - MMR-35 compiles the village landing metadata.
- **Siege positions:** not written. `Town.Deserialize` leaves the besieger camp positions at identity, and `SettlementVisual.OnStartup` fills siege-engine frames from the map entity's children. MMR-40 bakes the siege geometry into the settlement entities (`docs/settlement-entities.md`).

MMR-40 places gates, ports and settlement entities (`docs/settlement-entities.md`); its factory builds every hierarchy, so the intents' vanilla map prefab (`mapPrefab`) is used only by the MMR-33 fixture prefabs. Towns have none, because the vanilla map does not place them as prefab instances.

## The whole world through the registry

The offline tests plan the whole generated world through the MMR-30 registry's `FromDocuments`: the factions, the heroes and all 286 settlements, in the proven order.
- **Settlements, factions and references:** no problem.
- **Hero family references:** 150 of them (spouse, father, mother) point forward to a hero later in the document. That is outside MMR-30's two-pass list. They are MMR-32 records, and the installed Heroes.xml has 127 of the same kind, which the engine resolves through presumed objects. Handed to MMR-45.
- **`FromCompilerRecords`:** it still plans the compiler's castle owners (16 differ) and village bounds (44 differ). MMR-45 registers from the documents.

## In-game fixtures (c3)

With `settlementFixtures=<repo>`, `SettlementFactory.Element()` rebuilds the sandbox world's first town and village, keeping the same ids, owner and positions. Three fixtures are added through the same builder and the same registration path:
- a castle of the same clan;
- a village bound to the castle;
- a forest-bandit hideout.

**Positions and gates.** The fixtures sit at land points the placement builder verified on that map: the obstruction detour's end and the lake's two shores.
- The town and castle gates sit 1.875 inside their centre's own land cell: the town's toward its port, the castle's east. A gate the engine ignored would read as the centre.
- The registration requires the economy policy and prototypes to be the ones made from the catalog's Settlements, and records their hashes.

**Entities.** The generated scene has no entity for the three added fixtures. The installed `MapScene.AddNewEntityToMapScene` instantiates a prefab named after the settlement id, which a generated id never has; run 36452 crashed in `SettlementVisual.OnStartup`.
- So the generated map scene makes a fixture's entity from its prototype's vanilla map prefab (castle `map_icon_castle_empire`, village `map_village_emp_all_40`, hideout `hideout_forest_1`).
- Every other id keeps the installed path.

**Run 37100 (13/13)**, after new game:
- **Components and locations:** every fixture has its component and a location complex with scenes. The coastal town's port location carries its XML port scene (`empire_shipyard`).
- **Positions:**
  - Every centre, gate and port is exactly the specified one and the one the XML gave the engine.
  - The town's gate is (609.375, 167.5) and its port (609.375, 171.875); the castle's gate is (511.25, 134.375).
- **Buildings:** the town is at the policy's medium-tier levels with a level-1 shipyard, the castle at the castle tier.
- **Ownership:**
  - The clan owns the town and castle.
  - Its settlement, fief and village lists, and its kingdom's fief and village lists, hold all four.
  - Each village is bound and listed, and both villages trade with the town. The castle's village gets this through the engine's trade-bound assignment.
- **Starting values:**
  - town: food 400, militia 254, gold 23,050, 4 workshops, garrison 264;
  - castle: food 550, garrison 108.
- **Prices**, as multiples of each item's base value:
  - grain 1.7, within the 3× band, because the fixture's wheat farm supplies it;
  - fish 2, butter 5.3, cheese 5.6, iron 6.9, wine 8.8;
  - tools 10.1: at the engine's clamp (`DefaultTradeItemPriceFactorModel` limits the factor to 0.1-10), because the two-town fixture world has no supply of it.
- **Food table:** each of the 24 village types in the policy's food table produces exactly that much food in game. Run 30552 (12/13) had found the one mismatch, grapes, before the table was corrected.
- **Ownership transfer:** transferring the castle to the other clan, of another kingdom, moved it and its village between both clans' and both kingdoms' settlement, fief and village lists. The village's faction followed, and moving it back restored everything.
- **Policies:** the run read the repository's current economy policy and prototypes (hashes recorded).

## What this does not prove

- **No full-world boot.** MMR-45 registers the 286 settlements with the full world. MMR-40 places entities and final positions (MMR-45 binds the records with `ApplyResolvedPositions` and checks them with `CheckResolvedPositions`), and MMR-36 checks closure.
- **Live trade, recruitment and village deliveries** are exercised once, in MMR-51. The fixture market is a two-town world without supply for most goods (see the clamp hits above).
- **The castellan rule in game:** the fixture castle belongs to the ruling clan. The rule is only an owner attribute, which the in-game owner checks cover generically. Its offline check is exact.
- **Hideouts across a world** are MMR-34's (bandits and hideouts). SettlementFactory builds a hideout from a placed specification.
- **Scenes:** the location scenes are the prototypes'. Resolving mission scenes against the generated terrain is MMR-42's (`prototypeAndScenes`). The adapted nord towns keep a harbour centre scene (`nord_town_*`).
