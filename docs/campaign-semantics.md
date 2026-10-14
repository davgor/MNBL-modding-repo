# Campaign semantics and stable ids (MMR-27, 2026-10-14)

`worldbridge --compile-campaign` turns a generated world into source-derived intermediate records with stable Bannerlord ids. It resolves source identities, categories and ownership joins. It synthesizes only what the engine graph needs and the source lacks, always with provenance. Every engine value that a later ticket decides is left as a typed pending slot.

**It does not create Bannerlord objects.** The registry and production adapters are MMR-30 to MMR-36. Final engine-graph validation is MMR-36.

| | |
|---|---|
| Code | `src/WorldBridge/src/campaign_compiler.cpp`, `src/stable_ids.cpp` |
| Reader additions | `heroes.realms`, `heroes.people` (identity fields only), `humans.cultures`, `settlements.founding`, `effective_config` community limits, city population fields |
| Policies | `worldgen/policies/campaign-content.json` (kinds, roles, statuses, occupations, names, minima); `worldgen/policies/culture-presentations.json` (civilization to official culture) |
| Contract | `worldgen/contracts/id-map.schema.json` (`id-map.json`) |
| Tests | `src/WorldBridge/tests/campaign_semantics_tests.cpp` (CTest `campaign`, 59 checks), plus campaign-input reader cases in `source_reader_tests` (136 checks). Logs: `evidence/fwg/campaign-tests.txt`, `evidence/fwg/campaign-ctest.log` (7/7 suites) |
| Evidence | `evidence/fwg/campaign-semantics.json` (`tools/summarize_campaign.py`). For sizes 33, 129 and 257 a Python verifier independently recomputed every id and join, 18/18 checks each. Sizes 513 and 1025 were compiled and summarized. A repeat compile is byte-identical |

```
worldbridge --compile-campaign SPLIT --contract worldgen/contracts/source-compatibility.json \
  --content worldgen/policies/campaign-content.json --cultures worldgen/policies/culture-presentations.json --out NEW_DIR
```

It writes `id-map.json` and `campaign.json`; both are canonical JSON, and `campaign.json` names the id map's hash and both policy hashes.

## Records and joins

All joins use stable source UIDs. Integer site and hamlet ids are used only to reach a UID.

| Record | From | Joins |
|---|---|---|
| Town | `settlements.sites[]` | **Kingdom and owner:** the realm holding the city, and its ruling clan. **Capital:** when the realm's `capital_uid` is this city. **Culture:** from the core's `culture_id`, presented through its civilization. **Population:** metadata only |
| Village | `humans.hamlets[]` | **Bound:** to the town of its core city (`core_id` → site → uid). **Production and harbour:** by role. **Name:** synthesized |
| Castle | `humans.fortresses[]` | **Owner:** the realm of its garrison city. **Fortress without a garrison** (the writer's −1/"" sentinel): owned by the realm whose `culture_id` equals the fortress's. **Name:** synthesized |
| Kingdom | `heroes.realms[]` | **Ruler:** the sovereign, or a synthesized regent when `sovereign_uid` is null. **Capital:** must be one of the realm's cities |
| Clan | synthesized, one ruling house per realm (`clan:<realm uid>`) | **Leader:** the ruler. **Home:** the capital. **Members:** the realm's lords |
| Hero | `heroes.people[]`, living | **Occupation:** by role. **Affiliation:** Lord → clan; notable → home town, or the realm capital; Wanderer → none |
| Port | harbour villages, gathered per town | sea nodes and best harbour quality |

**Refusals.** Every city must be held by exactly one realm. A realm's culture must exist and match its civilization. A sovereign must be an instantiated lord of that realm. Anything else is refused with a reason: `faction_distribution`, `broken_join` or `sovereign_mismatch`.

**Closure.** `closure_problems()` checks that every reference resolves:
- every town and castle is owned and every village bound;
- every kingdom has a capital among its towns and a ruler leading its ruling clan.

**Exclusions.** `exclude()` lets MMR-28 drop a village or castle after a navigation decision, re-checking closure. Removing a harbour village drops the port when it was the last one. Towns are never removed (`preserved_settlement`).

## Stable ids

- **Form:** `id = mnbl_<8 hex of sha256("mnbl.world-prefix/1\n" + layoutSeed)>_<kind code>_<10 hex of sha256("mnbl.stable-id/1\n" + kind + "\n" + uid)>`. Kind codes: t town, v village, c castle, k kingdom, l clan, h hero.
- **Shape:** ids use only `[a-z0-9_]`, are 26 characters, and never depend on names.
- **Synthesized records** use derived UIDs (`clan:<realm>`, `regent:<realm>`).
- **Collisions:** a collision is refused (`id_collision`), never renamed silently, and so is a repeated (kind, uid).
- **The world prefix** keeps worlds A and B apart in one process. Campaign scoping proper is MMR-30.

## Names and escaping

- **Source names are kept.** A repeated name is disambiguated with the policy ordinal ("Alpha 2"), recorded as such.
- **Synthesized names.** Hamlets and fortresses carry no names (0 of 221 hamlets at size 129), so they get names from the policy:
  - village: "{core} {label}", where the label comes from the role (Fields, Harbour, Fishery, Works, Landing);
  - castle with a garrison: "{core} Watch";
  - castle without a garrison: "{culture} Hold";
  - ruling clan: "House of {realm}";
  - regent: "Regent of {realm}".
- **Uniqueness.** Settlement and clan names are unique across the world.
- **Escaping.** Every record carries `name`, `nameXml` (XML attribute escaped, control characters dropped) and `nameLocalized` (`{=id}name`). Braces become parentheses because TextObject reads them as variables.

## Category policy (complete, unknown fails)

| Category | Mapping |
|---|---|
| Source kinds | city → town, fortress → castle, hamlet → village |
| Hamlet roles | farming, fishing, resource: villages. harbor, "harbor + fishing", landing: harbour villages, which give their town a port. A `fishing` hamlet keeps its sea node but is not a port |
| Hero roles | all 17 roles in the generator's catalogue (`generated/content_heroes.cpp` kEpithets) map to installed Occupation values (NPCCharacters.xml): sovereign, domain_holder, castellan, pretender and founder → Lord; council → Merchant; reeve → Headman; harbourmaster → ShipWright; champion, magister, exile and legend → Wanderer; warlord and dread → GangLeader; keeper, heresiarch and prophet → Preacher. A domain holder, pretender or founder without a realm becomes a Wanderer (the realm-less domain holders are ruin wardens with no home). Any other Lord role without a realm is refused |
| Hero statuses | living: instantiated. legend: recorded in `notInstantiated` |
| Civilizations | all 12 map to an official main culture, as presentation only (`culture-presentations.json`). Generated culture ids and labels are kept. An unlisted civilization is refused, because it is an unresolved mandatory reference. Missing source-specific art never blocks a world |

## Typed slots

Each record lists slots that are either `resolved` here or `pending` with an owner ticket:

| Slot | Status and owner |
|---|---|
| Kingdom culture | resolved |
| Kingdom banner and policies | pending MMR-31 |
| Clan tier, banner and vassal clans | pending MMR-31 |
| Hero occupation | resolved |
| Hero character template, age, traits and family | pending MMR-32 |
| Town position | pending MMR-28 |
| Town prototype and scenes | pending MMR-42 |
| Town economy | pending MMR-33 |
| Town garrison | pending MMR-32 |
| Village type and hearth | pending MMR-33 |
| Port site | pending MMR-28 |
| Naval facility, shipyard and fleet | pending MMR-35 |

## Producer limits and minima

- **The "24-city ceiling" applies only to an explicit request.** It is the cap on an explicit `settlement_count` request (generator `config.cpp`, 0..24). Worlds that infer their own settings place cities by habitat packing (`settlement.cpp city_limit`). The pinned size-129 world has 45 cities against a founding target of 64; the size-33 world has 24 against 51.
- **The compiler checks the producer's own report:**
  - `founding.placed_cities` must equal the site count and stay within `target_cities`;
  - fortresses must stay within `fortress_count`;
  - it never adds or drops settlements.
- **`hamlets_per_core` is not a hard cap** in the output: cores carry up to 5 hamlets with the setting at 3, harbours included. So it is reported, not enforced.
- **Minima come from installed-game evidence** (`campaign-content.json`, each rule with its evidence):
  - at least one town;
  - every kingdom with a town;
  - every village bound (307 of 307 in the installed Settlements.xml);
  - every town and castle owned (133 of 133);
  - every clan with a leader and home;
  - every kingdom with a ruler.
- **The player aliases** (`main_hero`, `player_faction`) are created by the registry: deferred to MMR-30.

## Real worlds (pinned producer, seed 42)

| Size | Towns | Villages | Castles | Kingdoms (regents) | Heroes (legends not instantiated) | Ports | Compile |
|---|---|---|---|---|---|---|---|
| 33 | 24 | 81 | 22 | 20 (2) | 153 (6) | 15 | 0.7 s |
| 129 | 45 | 221 | 26 | 40 (8) | 327 (12) | 44 | 2.1 s |
| 257 | 49 | 235 | 28 | 42 (5) | 365 (12) | 47 | 3.6 s |
| 513 | 56 | 273 | 37 | 46 (3) | 394 (11) | 51 | 9.2 s |
| 1025 | 58 | 288 | 41 | 52 (7) | 478 (12) | 51 | 30.8 s |

At size 129, kingdoms are presented as vlandia 11, empire 7, battania 6, aserai 6, khuzait 4, nord 4 and sturgia 2. Heroes break down as 92 Lords, 62 Headmen, 57 Wanderers, 54 ShipWrights, 45 Merchants, 13 Preachers and 4 GangLeaders.

## Limits

- **One ruling clan per realm.** Lords join it. More noble clans and the faction balance are MMR-31.
- **Hero templates, ages, families and the hero count** an engine campaign can carry are pending (MMR-32).
- **Positions** come from the projection (MMR-25), and settlement placement and navigation reachability belong to MMR-28.
- **Policy-based content.** The culture mapping and the occupation choices are policy, and MMR-31 to MMR-33 may refine them. Every value is versioned by the policy hash.
