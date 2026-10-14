# Campaign semantics and stable ids (MMR-27, 2026-10-14)

`worldbridge --compile-campaign` turns a generated world into source-derived intermediate records with stable Bannerlord ids. It resolves source identities, categories and ownership joins, and checks every reference it consumes. It synthesizes only what the engine graph needs and the source lacks, always with provenance. Every engine value that a later ticket decides is left as a typed pending slot.

**It does not create Bannerlord objects.** The registry and production adapters are MMR-30 to MMR-36. Final engine-graph validation is MMR-36.

| | |
|---|---|
| Code | `src/WorldBridge/src/campaign_compiler.cpp`, `src/stable_ids.cpp` |
| Reader additions | `heroes.realms` (with `civilization_name`, `parent_race_id`), `heroes.people` (identity, home and presence only), `humans.cultures`, `settlements.founding`, `effective_config` community limits, hamlet and fortress `founded_by`, city population fields. The `heroes` block is pinned in `source-compatibility.json` (version 6, required system; a `failed` status is refused) |
| Policies | `worldgen/policies/campaign-content.json` (kinds, roles, statuses, occupations with installed-game evidence, name templates, minima); `worldgen/policies/culture-presentations.json` (civilization to official culture) |
| Contract | `worldgen/contracts/id-map.schema.json` (`id-map.json`) |
| Tests | `src/WorldBridge/tests/campaign_semantics_tests.cpp` (CTest `campaign`, 88 checks), plus campaign-input and heroes-block cases in `source_reader_tests` (139 checks). Logs: `evidence/fwg/campaign-tests.txt`, `evidence/fwg/campaign-ctest.log` (7/7 suites) |
| Evidence | `evidence/fwg/campaign-semantics.json` (`tools/summarize_campaign.py`): for all five sizes, 33 to 1025, a Python verifier recomputed every id, reference, join, occupation, attachment, name and producer limit from the source world, 19/19 checks each, and checked every occupation used against the installed game's official modules. A repeat compile is byte-identical. Review: `evidence/fwg/campaign-review.md` |

```
worldbridge --compile-campaign SPLIT --contract worldgen/contracts/source-compatibility.json \
  --content worldgen/policies/campaign-content.json --cultures worldgen/policies/culture-presentations.json --out NEW_DIR
```

It writes `id-map.json` and `campaign.json`; both are canonical JSON. `campaign.json` names the world.json hash, the id map's hash and both policy hashes.

## Records and joins

All joins use stable source UIDs. Integer site and hamlet ids are used only to reach a UID.

| Record | From | Joins |
|---|---|---|
| Town | `settlements.sites[]` | **Kingdom and owner:** the realm holding the city, and its ruling clan. **Capital:** when the realm's `capital_uid` is this city. **Culture:** the core's `culture_id`, which must exist and belong to the city's civilization (the holding realm's culture only when the core has none, recorded in `cultureProvenance`). **Population:** metadata only |
| Village | `humans.hamlets[]` | **Bound:** to the town of its core city (`core_id` → site → uid). **Production and harbour:** by role. **Culture:** its own (must exist) or its town's. **Name:** synthesized |
| Castle | `humans.fortresses[]` | **Owner:** the realm of its garrison city. **Fortress without a garrison** (the writer's −1/"" sentinel): owned by the realm whose `culture_id` equals the fortress's. **Name:** synthesized |
| Kingdom | `heroes.realms[]` | **Ruler:** the sovereign, or a synthesized regent when `sovereign_uid` is null. **Capital:** must be one of the realm's cities. Carries `civilizationName` and `parentRaceId` |
| Clan | synthesized, one ruling house per realm (`clan:<realm uid>`) | **Leader:** the ruler. **Home:** the capital. **Members:** the realm's lords |
| Hero | `heroes.people[]`, living | **Occupation:** by role. **Affiliation:** Lord → clan. Town notable → home town, or the realm capital. Village notable (Headman) → the hamlet it is present at, or its home town. Wanderer → none. Carries `raceId` and its civilization's `presentation` |
| Port | harbour villages, gathered per town | sea nodes and best harbour quality |

**References.** Every reference the compiler consumes must resolve, or the compile is refused:
- each realm's culture, its capital and its cities;
- each city's, hamlet's and fortress's culture, with no duplicate culture ids;
- each hero's realm, home city (a home must be a generated city) and hamlet presence (for village notables);
- each hero's, city's and realm's civilization, which must have a presentation;
- each realm's sovereign.

Refusals carry a reason: `broken_join`, `duplicate_id`, `faction_distribution`, `sovereign_mismatch` or `unmapped_category`.

**Closure.** `closure_problems()` checks that every reference in the compiled records resolves:
- every town and castle is owned and every village bound;
- every notable's town or village exists;
- every kingdom has a capital among its towns and a ruler leading its ruling clan.

**Exclusions.** `exclude()` lets MMR-28 drop a village or castle after a navigation decision, re-checking closure:
- removing a harbour village drops the port when it was the last one;
- the Headman of a removed village moves to the village's town, with provenance;
- each exclusion is recorded (`exclusions`: id, kind, uid, reason), and its id stays reserved in the id map;
- towns are never removed (`preserved_settlement`);
- a refused exclusion leaves the records unchanged.

## Stable ids

- **Form:** `id = <prefix>_<kind code>_<10 hex of sha256("mnbl.stable-id/1\n" + kind + "\n" + uid)>`. Kind codes: t town, v village, c castle, k kingdom, l clan, h hero.
- **World prefix:** `mnbl_` + 8 hex of `sha256("mnbl.world-prefix/2\n" + sourceIdentity + "\n" + worldSha256)`. `sourceIdentity` is the source identity document (schema, stage, generator version, grid side, seeds) and `worldSha256` the hash of the split's world.json. Both are recorded in `id-map.json`.
  - Different worlds get different prefixes even when they share a seed: the five seed-42 research worlds have five prefixes.
  - The prefix separates worlds; it is not a uniqueness proof. Campaign scoping proper is MMR-30.
- **Shape:** ids use only `[a-z0-9_]`, are 26 characters, and never depend on names.
- **Synthesized records** use derived UIDs (`clan:<realm>`, `regent:<realm>`).
- **Collisions:** a collision is refused (`id_collision`), never renamed silently, and so is a repeated (kind, uid).
- **Source UIDs built from ordinals (for MMR-30).** Some generator person UIDs are built from ordinals: harbourmasters from the hamlet ordinal (`hero-harbourmaster-coastal-23-46`), college magisters (`hero-magister-college-1`). Within one world they are unique. Across worlds the same string can name different people, which the world prefix keeps apart. They are not stable when the generator renumbers, so MMR-30 must not key saves on them across worlds.

## Names and escaping

- **Source names are kept.** A repeated name is disambiguated with the policy ordinal ("Alpha 2"), recorded as such.
- **Order.** Names are assigned in source-UID order, so they do not change when the source reorders its arrays.
- **Synthesized names.** Hamlets and fortresses carry no names (0 of 221 hamlets at size 129), so they get names from the policy:
  - town without a name: "{realm} Town";
  - village: "{core} {label}", where the label comes from the role (Fields, Harbour, Fishery, Works, Landing);
  - castle with a garrison: "{core} Watch";
  - castle without a garrison: "{culture} Hold";
  - ruling clan: "House of {realm}";
  - regent: "Regent of {realm}".
- **Templates.** A template may use only its own placeholders with balanced braces, and the ordinal must contain `{name}` and `{n}`; anything else is refused on load (`invalid_template`). Substitution is one pass, so a source name containing a placeholder stays literal.
- **Uniqueness.** Settlement and clan names are unique across the world.
- **Escaping.** Every record carries four name forms:
  - `name`;
  - `nameXml`, XML-attribute escaped;
  - `nameLocalized`, `{=id}name` with braces turned into parentheses, because TextObject reads braces as variables;
  - `nameLocalizedXml`, the localized form XML-escaped.

  All four drop control characters, tab included.

## Category policy (complete, unknown fails)

| Category | Mapping |
|---|---|
| Source kinds | city → town, fortress → castle, hamlet → village |
| Hamlet roles | farming, fishing, resource: villages. harbor, "harbor + fishing", landing: harbour villages, which give their town a port. A `fishing` hamlet keeps its sea node but is not a port |
| Hero roles | All 17 roles in the generator's catalogue (`generated/content_heroes.cpp` kEpithets):<br>• sovereign, domain_holder, castellan, pretender and founder → Lord<br>• council and harbourmaster → Merchant (town)<br>• reeve → Headman (village)<br>• champion, magister, exile and legend → Wanderer<br>• warlord and dread → GangLeader (town)<br>• keeper, heresiarch and prophet → Preacher (town) |
| Without a realm | Domain holders, pretenders, founders, keepers and heresiarchs without a realm become Wanderers. These are ruin wardens, and shrine keepers whose people have no surviving city (the generator then leaves both home and realm null). Any other Lord role without a realm is refused |
| Occupations | Each one names its installed evidence: hero characters or templates in the official modules. Lord 584, Wanderer 82, Merchant 55, Headman 21, GangLeader 21, Preacher 16, recounted by the verifier. **ShipWright is not used:** its 14 installed characters are NavalDLC location characters, neither heroes nor templates. Naval roles are revisited by MMR-35 |
| Hero statuses | living: instantiated. legend: recorded in `notInstantiated` |
| Civilizations | All 12 in the generator's registry (`content_civilizations.cpp` kEntityIds, checked by a test) map to an official main culture, as presentation only (`culture-presentations.json`). Generated culture ids and labels are kept. An unlisted civilization is refused, because it is an unresolved mandatory reference. Missing source-specific art never blocks a world |

## Typed slots

Each record lists slots that are either `resolved` here or `pending` with an owner ticket:

| Slot | Status and owner |
|---|---|
| Kingdom culture | resolved |
| Kingdom banner and policies | pending MMR-31 |
| Kingdom troop tree and recruit templates | pending MMR-32 |
| Clan tier, banner and vassal clans | pending MMR-31 |
| Hero occupation | resolved (installed templates exist for every value) |
| Hero character template, age, traits and family | pending MMR-32 |
| Town position | pending MMR-28 |
| Town prototype and scenes | pending MMR-42 |
| Town economy | pending MMR-33 |
| Town and castle garrison | pending MMR-32 |
| Village type and hearth | pending MMR-33 |
| Port site | pending MMR-28 |
| Naval facility, shipyard and fleet | pending MMR-35 |

## Producer limits and minima

- **The "24-city ceiling" applies only to an explicit request.** It is the cap on an explicit `settlement_count` request (generator `config.cpp`, 0..24). Worlds that infer their own settings place cities by habitat packing (`settlement.cpp city_limit`). The pinned size-129 world has 45 cities against a founding target of 64; the size-33 world has 24 against 51.
- **The compiler requires the producer's own report and checks it:**
  - `founding.placed_cities` must equal the site count and stay within `target_cities`, which `founding.cpp` keeps within `city_limit`;
  - fortresses must stay within `fortress_count`;
  - land hamlets per core must stay within `hamlets_per_core`. That is the generator's cap on land hamlets (`humans.cpp`); harbour-shaped hamlets come from fisheries (`society.cpp`). Every research world peaks at exactly 3 land hamlets per core, and up to 5 hamlets with harbours included.
  - A world missing any of these is refused, and nothing is ever added or dropped.
- **Player-adopted rows** (`founded_by`) are refused (`unsupported_row`). The generator appends them past its limits and they belong to the player faction, which the registry creates (MMR-30). They are also the only rows with the ungarrisoned-fortress sentinel and no culture. None occur in generator-only worlds.
- **Minima** (`campaign-content.json`, each rule with its evidence):
  - at least one town: reasoned from references (the player's start town, each kingdom's home town); the smallest observed boot had 2 kingdoms and 2 towns (MMR-18);
  - every kingdom with a town;
  - every village bound (307 of 307 in the installed Settlements.xml);
  - every town and castle owned (133 of 133);
  - every clan with a leader and home;
  - every kingdom with a ruler.
- **The player aliases** (`main_hero`, `player_faction`) are created by the registry: deferred to MMR-30.

## Real worlds (pinned producer, seed 42)

| Size | Towns | Villages | Castles | Kingdoms (regents) | Heroes (legends not instantiated) | Ports | Compile |
|---|---|---|---|---|---|---|---|
| 33 | 24 | 81 | 22 | 20 (2) | 153 (6) | 15 | 1.1 s |
| 129 | 45 | 221 | 26 | 40 (8) | 327 (12) | 44 | 2.8 s |
| 257 | 49 | 235 | 28 | 42 (5) | 365 (12) | 47 | 4.8 s |
| 513 | 56 | 273 | 37 | 46 (3) | 394 (11) | 51 | 11.7 s |
| 1025 | 58 | 288 | 41 | 52 (7) | 478 (12) | 51 | 37.2 s |

Compile times are wall-clock. The fix-batch compiles ran beside CTest, so they are slower than the first pass (30.8 s at size 1025 then).

At size 129:
- **Kingdom presentations:** vlandia 11, empire 7, battania 6, aserai 6, khuzait 4, nord 4 and sturgia 2.
- **Heroes:** 99 Merchants, 92 Lords, 62 Headmen, 57 Wanderers, 13 Preachers and 4 GangLeaders.
- **Attachments:** all 62 Headmen are at the hamlet they are present at; 116 notables are at towns.

## Limits

- **One ruling clan per realm.** Lords join it. More noble clans and the faction balance are MMR-31.
- **Hero templates, ages, families and the hero count** an engine campaign can carry are pending (MMR-32).
- **Positions** come from the projection (MMR-25), and settlement placement and navigation reachability belong to MMR-28.
- **Presence is checked only where it is used** (hamlet presence of village notables). Presence at ports, shrines, colleges, camps, fortresses and ruins names other generator systems this compiler does not join.
- **Policy-based content.** The culture mapping and the occupation choices are policy, and MMR-31 to MMR-35 may refine them. Every value is versioned by the policy hash.
