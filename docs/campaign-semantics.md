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

It writes `id-map.json` and `campaign.json`; both are canonical JSON. `campaign.json` (`mnbl.campaign-records` version 2, map epic 2; version 1 is gone) names the world.json hash, the id map's hash and both policy hashes. Each village carries `bound`, `boundKind` (town or castle), `sourceTown` and `origin` (source, reassigned or synthesized), and a synthesized one its `tier`; a port's harbour village is `pinned`, and castles list their `villages`. The `villageQuota` block is written only by the travel compiler, after the quota runs. The population blocks (`wars`, `houses`, `mercenaryCompanies`, `outlawClans`, `wanderers`, `census`, and each hero's `bucket`, `female`, `age`, `alive` and kin links) are written by the population pass after the quota ([population.md](population.md), MMR-279); this stage alone writes one provisional house per realm. The managed factories emit them (MMR-280): FactionFactory the merged kingdoms with their starting wars, the houses and the minor clans; HeroFactory every planned Lord, minor and dead parent as an XML hero and the planned wanderers as companions, checked against the `census` ([factions.md](factions.md), [heroes.md](heroes.md)).

## Records and joins

All joins use stable source UIDs. Integer site and hamlet ids are used only to reach a UID.

| Record | From | Joins |
|---|---|---|
| Town | `settlements.sites[]` | **Kingdom and owner:** the realm holding the city, and its provisional house (the population pass then sets the merged kingdom and the owning house). **Capital:** when the realm's `capital_uid` is this city. **Culture:** the core's `culture_id`, which must exist and belong to the city's civilization (the holding realm's culture only when the core has none, recorded in `cultureProvenance`). **Population:** metadata only |
| Village | `humans.hamlets[]` | **Bound:** to the town of its core city (`core_id` → site → uid), which is also its `sourceTown`; the travel compiler's village quota then binds exactly 3 per town and 2 per castle (MMR-252, [travel-graph.md](travel-graph.md) "Village quota"). **Production and harbour:** by role. **Culture:** its own (must exist) or its town's. **Name:** synthesized |
| Castle | `humans.fortresses[]` | **Owner:** the realm of its garrison city. **Fortress without a garrison** (the writer's −1/"" sentinel): owned by the realm whose `culture_id` equals the fortress's. **Name:** synthesized |
| Kingdom | `heroes.realms[]` | **Ruler:** the sovereign, or a synthesized regent when `sovereign_uid` is null. **Capital:** must be one of the realm's cities. Carries `civilizationName` and `parentRaceId` |
| House | synthesized, one provisional house per realm (`house:<realm uid>`, id kind clan) | **Leader:** the ruler. **Home and seat:** the capital. **Fiefs:** the realm's towns and castles. **Members:** the realm's lords. The population pass keeps it as the realm ruler's house and plans the others |
| Hero | `heroes.people[]`, living | **Occupation:** by role. **Bucket:** by role, or by council seat (`heroRoles[].bucket`, `seatBuckets`). **Affiliation:** Lord → house. Town notable → home town, or the realm capital. Village notable (Headman) → the hamlet it is present at, or its home town. Wanderer → none. Carries `raceId` and its civilization's `presentation` |
| Port | harbour villages, gathered per town | sea nodes and best harbour quality |

**References.** Every reference the compiler consumes must resolve, or the compile is refused:
- each realm's culture, its capital and its cities;
- each city's, hamlet's and fortress's culture, with no duplicate culture ids;
- each hero's realm, home city (a home must be a generated city) and hamlet presence (for village notables);
- each hero's, city's and realm's civilization, which must have a presentation;
- each realm's sovereign.

Refusals carry a reason: `broken_join`, `duplicate_id`, `faction_distribution`, `sovereign_mismatch` or `unmapped_category`.

**Closure.** `closure_problems()` checks that every reference in the compiled records resolves:
- every town and castle is owned and every village bound to a town or castle (`boundKind`) that lists it, with a town as its `sourceTown`;
- every notable's town or village exists;
- every kingdom has a capital among its towns and a ruler leading its ruling house;
- every town and castle is owned by a house of its kingdom, every house's home is one of its fiefs, every clan member is listed by its clan (house, company or outlaw clan), and kin links name heroes.

**Exclusions.** `exclude()` lets MMR-28 drop a village or castle after a navigation decision, re-checking closure:
- removing a harbour village drops the port when it was the last one; only a port the exclusion empties is erased, so a forced port that never had a village keeps its record (RT-12);
- the Headman of a removed village moves to the village's source town, with provenance (the village quota moves the notables of the villages it drops to a kept village of the same town first, RT-11);
- a castle with bound villages cannot be removed (closure);
- each exclusion is recorded (`exclusions`: id, kind, uid, reason), and its id stays reserved in the id map;
- towns are never removed (`preserved_settlement`);
- a refused exclusion leaves the records unchanged.

## Stable ids

- **Form:** `id = <prefix>_<kind code>_<10 hex of sha256("mnbl.stable-id/1\n" + kind + "\n" + uid)>`. Kind codes: t town, v village, c castle, k kingdom, l clan, h hero.
- **World prefix:** `mnbl_` + 8 hex of `sha256("mnbl.world-prefix/2\n" + sourceIdentity + "\n" + worldSha256)`. `sourceIdentity` is the source identity document (schema, stage, generator version, grid side, seeds) and `worldSha256` the hash of the split's world.json. Both are recorded in `id-map.json`.
  - Different worlds get different prefixes even when they share a seed: the five seed-42 research worlds have five prefixes.
  - The prefix separates worlds; it is not a uniqueness proof. Campaign scoping proper is MMR-30.
- **Shape:** ids use only `[a-z0-9_]`, are 26 characters, and never depend on names.
- **Synthesized records** use derived UIDs (`house:<realm>`, `regent:<realm>`; the population pass's in [population.md](population.md)).
- **Collisions:** a collision is refused (`id_collision`), never renamed silently, and so is a repeated (kind, uid).
- **Source UIDs built from ordinals (for MMR-30).** Some generator person UIDs are built from ordinals: harbourmasters from the hamlet ordinal (`hero-harbourmaster-coastal-23-46`), college magisters (`hero-magister-college-1`). Within one world they are unique. Across worlds the same string can name different people, which the world prefix keeps apart. They are not stable when the generator renumbers, so MMR-30 must not key saves on them across worlds.

## Names and escaping

- **The shared name cleaner names every kind (MMR-286).** Kingdoms, clans, towns, castles, villages, heroes and ruins go through
  `worldbridge::names` ([population.md](population.md) "The shared name cleaner"): the FWGC++ "(Age N)" tag is stripped and a
  collision is told apart by a lexicon root suffix of the civilization ("Sirldorl (Age 2)" twice: "Sirldorl", "Sirldorlkor"), never by
  a numeral. Names are unique per kind: kingdoms; clans (houses, companies and outlaw clans); settlements (towns, castles and villages
  together); heroes; ruins.
- **Source names are kept.** A record whose name differs from its FWGC++ name keeps it as `sourceName` (kingdoms, towns, heroes,
  ruins; written only when it differs), and its `nameProvenance` says why ("source; Age tag removed", "..., disambiguated with a
  lexicon suffix"). A renamed kingdom's and town's encyclopedia text names it (`factions.json` and `economy.json` `texts.sourceNote`).
- **Order.** Names are assigned in source-UID order, so they do not change when the source reorders its arrays; the first by uid
  keeps a repeated source name.
- **Coined names.** Hamlets and fortresses carry no names (0 of 221 hamlets at size 129), so villages, castles and a town without a
  name are coined FWGC++-style (`Unique::coin`): a compound of two lexicon roots of their civilization (`civilizations.entities[]`
  `heritage.genome.lexicon`), a root of the whole lexicon and then a root of the kind's concepts, both from rotations drawn by the uid:
  - village: the role's `hamletRoles[].concepts` (farming field, green, mill, house; fishing water, river, bay; resource stone, deep,
    mill, fire; harbours harbour, shore, bay or water; landing shore, ford, bay), "old" + "field" -> "Eldfen";
  - castle: `names.places.castle` (stone, hill, high, gate, hall), "Torpor";
  - town without a name: `names.places.town` (hall, market, gate, realm).
  The civilization is the culture's (`humans.cultures`), else the core town's; a concept the lexicon lacks is skipped; a civilization
  without a lexicon uses the cleaner's neutral roots. When every two-root compound is taken, three roots are used.
- **Regents** (a realm with a null sovereign) are named after an FWGC++ resident of the realm's civilization (`npcs.people` of the
  naming posts, in uid order from a start drawn by the realm), the first whose name no hero carries, with that resident's sex; without
  residents, a lexicon compound. The provisional realm house is "House of {realm}" (`names.rulingClan`; the population pass renames
  every house with the cleaner).
- **Templates.** `names.rulingClan` may use only `{realm}`, with balanced braces; anything else is refused on load
  (`invalid_template`). Substitution is one pass, so a source name containing a placeholder stays literal. `names.cleaner` must strip
  Age tags and allow no numerals.
- **Census.** `tools/summarize_names.py` counts Age tags, numerals and repeats per kind over compiled worlds
  (`evidence/map-epic-2/names.json`).
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
| House tier and banner | pending MMR-31 (FactionFactory, MMR-280: the ruling house's tier from its kingdom's fiefs, every other house's from its own) |
| Company and outlaw clan banner and colours | pending MMR-280 (FactionFactory: a colour pair unique world-wide, an icon of the clan's culture) |
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

- **There is no 24-city cap on generated worlds.** The generator refuses, and does not clamp, an explicit `settlement_count` above 24 (`config.cpp`, 0..24), and MNBL never sends `settlement_count`. Worlds that infer their own settings place cities up to the spacing and habitat-packing ceiling (`settlement.cpp city_limit`, between 15 and 103 on real worlds). The pinned size-129 world has 45 cities against a founding target of 64; the size-33 world has 24 against 51, a coincidence of history.
- **Every city is one town at every stage.** No city is dropped: the travel compiler repairs an unplaceable or unreachable town instead of rejecting its world, and asserts city preservation (sites, `placed_cities`, compiled towns, id-map towns and travel towns: one count, one uid set). See [travel-graph.md](travel-graph.md), "Town repairs" and "City preservation".
- **The compiler requires the producer's own report and checks it:**
  - `founding.placed_cities` must equal the site count and stay within `target_cities`, which `founding.cpp` keeps within `city_limit`;
  - fortresses must stay within `fortress_count`;
  - land hamlets per core must stay within `hamlets_per_core`. That is the generator's cap on land hamlets (`humans.cpp`); harbour-shaped hamlets come from fisheries (`society.cpp`). Every research world peaks at exactly 3 land hamlets per core, and up to 5 hamlets with harbours included.
  - A world missing any of these is refused. This compiler adds and drops nothing; the travel compiler's village quota (MMR-252) adds and drops villages afterwards, recorded in `villageQuota`.
- **Player-adopted rows** (`founded_by`) are refused (`unsupported_row`). The generator appends them past its limits and they belong to the player faction, which the registry creates (MMR-30). They are also the only rows with the ungarrisoned-fortress sentinel and no culture. None occur in generator-only worlds.
- **Minima** (`campaign-content.json`, each rule with its evidence):
  - at least one town: reasoned from references (the player's start town, each kingdom's home town); the smallest observed boot had 2 kingdoms and 2 towns (MMR-18);
  - every kingdom with a town;
  - every village bound (307 of 307 in the installed Settlements.xml);
  - every town and castle owned (133 of 133);
  - every house with a leader and home;
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

- **Provisional houses.** This stage writes one house per realm; the population pass merges the realms into kingdoms and plans the noble houses, companies, outlaw clans and wanderers ([population.md](population.md)).
- **Hero templates, traits and equipment** are MMR-32's; the population pass plans ages, sexes and kin, and MMR-280 emits exactly the planned heroes (no family is synthesized in managed code).
- **Positions** come from the projection (MMR-25), and settlement placement and navigation reachability belong to MMR-28.
- **Presence is checked only where it is used** (hamlet presence of village notables). Presence at ports, shrines, colleges, camps, fortresses and ruins names other generator systems this compiler does not join.
- **Policy-based content.** The culture mapping and the occupation choices are policy, and MMR-31 to MMR-35 may refine them. Every value is versioned by the policy hash.
