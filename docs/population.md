# Population pass (map epic 2, MMR-279)

The population pass plans the people of a generated campaign: kingdoms merged from FWGC++ realms, starting wars, noble houses, mercenary companies, outlaw clans and tavern wanderers, with a census that places every living FWGC++ hero. It is `worldbridge::population::plan` (`src/WorldBridge/src/population.cpp`), run inside `travel::run` after the village quota's final binding ([travel-graph.md](travel-graph.md) "Village quota"), so the generation driver's travel gate, `--compile-travel` and `--bundle` all write the same `campaign.json` v2. It creates no Bannerlord objects: FactionFactory and HeroFactory emit them (MMR-280), and the wanderer tick keeps W in game (MMR-281).

| | |
|---|---|
| Policy | `worldgen/policies/population.json` (MMR-248; `--population`, the request's `population` policy), `campaign-content.json` (hero-role buckets, `names.cleaner`), `culture-presentations.json` |
| Sources | `heroes` (people with seat, fame tier and fame, epithet, lost ruin, companion join condition, org and presence situation; orgs; camps; the castellan roll ledger), `npcs` (hero-linked sex and age; resident name pools; life spans), `nomads.groups` (bandit and deserter bands), `civilizations.entities[].heritage` (naming pattern, lexicon roots). `npcs` and `nomads` are pinned in `source-compatibility.json` |
| Output | `campaign.json` v2: `kingdoms[].memberRealms`/`mergeSteps`, `wars`, `houses`, `mercenaryCompanies`, `outlawClans`, `wanderers`, `wandererReserve` (MMR-281), per-hero `bucket`, `female`, `age`, `alive`, `spouse`/`father`/`mother`, `census`. Schema: `worldgen/contracts/campaign.schema.json` |
| Evidence | `evidence/fwg/population.json` (`tools/summarize_population.py`): worlds A' (seed 42) and B' (seed 73), every criterion recomputed from the records |
| Tests | `population_tests` (CTest `population`): the name cleaner, W, the age mapping and the pass on a synthetic world |

## Targets

F is the world's fiefs (towns + castles), at War Sails ratios per 133 fiefs (one base everywhere, RT-03):

| Target | Rule | A' (F 71, 45 towns) | B' (F 105, 66 towns) |
|---|---|---|---|
| Kingdoms K | round(9 F / 133), at least 1 | 5 | 7 |
| Houses H | round(82 F / 133), at least min(3 K, F) | 44 | 65 |
| Adult lords | about 4.94 H | 217 | 321 |
| Mercenary companies M | round(12 F / 133), 6..10 members | 6 | 9 |
| Outlaw clans O | round(10 F / 133), 4..6 members | 5 | 8 |
| Wanderers W | the smallest integer not below the float32 product 0.6f x towns (War Sails' own rule, RT-04) | 28 | 40 |

## Kingdoms (RT-01)

The compile stage writes one kingdom and one provisional house per realm. First, a castle whose villages have no town of its realm within the engine trade limit (3 x the town spacing S; VillageTradeBoundCampaignBehavior, which SettlementFactory checks) moves with its villages to the realm whose towns serve them all nearest (seed 61; noted in `census.notes` and the castle's `ownerProvenance`), so the merge keeps it with its market. The pass then merges the realms, in `kingdomMerge.order`:
1. **civilizationLand.** Same-civilization realms that border on land merge, the smallest first with its nearest such neighbour, up to the even share ceil(F / K). Two realms border on land when a fief of one is among the 3 nearest fiefs of the same land group of a fief of the other.
2. **presentationProximity.** While more than K units remain, the K largest (by fiefs, then the realm ranking) seed the kingdoms and take the others, the largest first, each into the kingdom within `maxFiefs` (17, the largest War Sails kingdom) at the least cost: its distance to the kingdom's seed, times 1.5 for another presentation culture, 1.5 for crossing water (no shared land group) and 1.5 beyond the even share. Distance to the seed keeps kingdoms compact.
3. **splitAboveMax.** A kingdom of several realms above `maxFiefs` splits around its primary and its farthest realm until each part fits. A single realm above the maximum stays and is noted.

The highest-ranked member realm rules: a realm with a sovereign before a regent's, then more fiefs, more towns, more people, then the uid. Its kingdom record, capital and house stay; the others' sovereigns or regents head vassal houses that keep their fiefs. `memberRealms` lists the realms highest-ranked first, and `mergeSteps` the step that joined each (`seed` for the primary; `crossesWater` when it shared no land group). A merged realm's kingdom id leaves the id map.

## Starting wars (RT-06)

In a `wars`-stream order, each kingdom without a war takes the partner with the fewest wars, a land border first, then the nearest (by sea), never above `wars.maxPerKingdom` (2). `wars[]` names each pair with its partner kind; MMR-280 writes the `isAtWar` relationships.

## Houses

- **Seats.** Each kingdom gets its share of H, at least its realms, min(3, its fiefs) and ceil(fiefs / 4). Every realm ruler (kingdom ruler, then the other realm rulers) heads a house. Further houses, and forced seats that keep every house at 4 fiefs or fewer, are carved from the kingdom's house with the most fiefs, by seat priority: a castle with its FWGC++ castellan, a castle with a castellan synthesized from the roll ledger (named after the fortress's commander), a pretender (on the town that shelters them), a non-capital town's magistrate, else a forced seat with a synthesized head. A house's seat is one of its fiefs and its home.
- **Members.** House-bucket heroes (`heroRoles` bucket house: sovereigns, regents, castellans, pretenders, founders, council magistrates and commanders) join the house holding their seat: a castellan the house owning its castle, a pretender the house owning the town that shelters them, a council member the house owning its town. Above the cap of 10, the lowest-ranked (never the head or a castellan of the house's castles) move to the nearest house of the kingdom with room.
- **Sizes.** A chaotic 3..8 draw per house (`houses` stream), at least its planned members plus a spouse when they are of one sex, then trimmed or grown one member at a time, within 3..8, toward the adult target `adultLordsPerHouse` x H. Parented minors are planned for houses whose head is at least 20, in a drawn order, up to `minorShare` of all planned members.
- **Kin** (`kin` stream), named after FWGC++ residents of the head's civilization and sex in the culture's naming pattern: a spouse for the head (the other sex, age within -8..+6), siblings sharing one emitted dead parent (`alive` false, not a member), adult children (at most the younger parent's age less 20) and the planned minors, children of the head and the spouse. Every house has an adult of each sex, and every member's presentation is one of the 7 vanilla lord cultures (the random child generator clones a same-sex adult of the house, findings section 16).
- **Sex and age** of FWGC++ heroes come from the hero-linked NPC record: the people's life-stage band (young, middle, old) maps linearly onto 20..34, 35..49 and 50..70 Bannerlord years (rulers and clan leaders are at least 20, rosters.json `lords.minimumAge`).
- **Names.** "House R", "Clan R", "Line of R", "Workshop of R", "R Cohort" or "Crew of the R" by the head's naming pattern, R the head's given name.

## Mercenary companies, outlaw clans, wanderers

- **Companies.** Captains in `companies.captains` order: remnant champions whose join condition is "hired" (by fame), living warlords, domain holders, then synthesized captains. Name, culture and home town come from the order (its name without the article and the Age tag) and the captain's presence or home town. Members: the domain holder warding the order's ruin, the living warlords, then whatever the wanderers leave, then soldiers named after FWGC++ garrison residents up to a 6..10 draw (`companies` stream). Templates: the culture's line in `troopTemplates`.
- **Outlaw clans.** One per source, in `outlaws.leaders` order: bandit bands, deserter bands, ruin camps, then synthesized. Kind: bandits mafia, deserters nomad, camps sect; a culture without that kind takes its first line (recorded). Home and culture: the nearest town whose presentation culture has at most `maxWarKingdoms` (2) kingdoms, the war guard; `wars` lists those kingdoms. 4..6 synthesized members named after residents.
- **Wanderers.** Champions not used as captains, magisters, exiles and the remaining domain holders, rotating by civilization, up to W, each with its FWGC++ sex and epithet (`wanderers[]`). Their templates come only from the backstory-safe pool (`HeroFactory.WandererPool`, MMR-287; [heroes.md](heroes.md) "Companions").
- **Wanderer reserve (MMR-281, RT-33).** The names the runtime tick gives the wanderers it makes (rotation, top-up), drawn after every other name: per presentation culture of the towns, `wanderers.reserve` (24) FWGC++ residents of the civilizations its towns present, in turn. Each entry's sex is drawn at that civilization's residents' share and the resident is of that sex; its given name is one no hero carries (a lexicon compound when the residents run out; a culture whose names run out keeps fewer, noted in `census.notes`). Its epithet is an archetypal FWGC++ epithet: one that two or more of the world's champions, magisters, exiles or domain holders carry (Age tag stripped; **default taken**), drawn per entry; with none, the runtime adds the template's. A' 168 entries (7 cultures), B' 144 (6).

## In play: registered templates and the soak census (MMR-262)

- **Registered wanderer templates.** A new game registers an installed NPCCharacter only when the world's documents root it, and the runtime top-up and rotation take only registered templates (`GeneratedPopulationBehavior`'s `TemplateUsable`). `CompiledWorldDataset` therefore roots, besides the startup plan's templates, every backstory-safe template of `wanderers.templates` that the catalog resolves by HeroFactory's own predicate (`CharacterTemplateResolver.WandererTemplates`), and the closure re-emits them with their skill sets and equipment. A' and B' register all 40 safe templates; on A' the 28 day-0 wanderers use 23 (aserai men 1 of 7, vlandia men 1 of 6, khuzait women 0 of 2), which was all a top-up could choose from before (`tools/Test-PopulationRuntime.ps1`: a khuzait woman's and an aserai man's top-up on A' now take an own-culture template no living wanderer has). The NPCCharacters document changes with it, so a save of a world activated before this change is refused on Continue (`not-rebuildable`: the load rebuilds the documents with this code and `LoadPolicy.RebuiltDocuments` finds NPCCharacters different), as the clean break intends.
- **Soak census** (`GeneratedPopulationBehavior.Census()`, recorded by `CampaignSoakProbe` at day 0 and after `populationDays`, and by the startup audit). Besides MMR-281's totals and histograms:
  - `houseDetail`: per house, `kingdom` (null when it has none), `size` (alive heroes without the engine's children), `engineChildren`, `eliminated`, `planned`. A planned house stays listed once eliminated (`removed` if it left `Clan.All`), so "no house of a dissolved kingdom was removed" can be read; `housesEliminated` counts them.
  - `minorClanDetail`: per company or outlaw clan (eliminated ones included), `kind`, `aliveHeroes`, `flaggedHeroes` (`IsMinorFactionHero`), `kingdom`, `underMercenaryService`, `eliminated`.
  - `fiefOwners`: town or castle id -> owner clan id (c18's day-0 owners against the compiled ones).
  - `kingdomChanges`: every `OnClanChangedKingdom` since the behaviour started, `clan:old->new:detail` ("-" for none), such as a company's `JoinAsMercenary`/`LeaveAsMercenary`, defections and dissolutions. In memory only, no save key (the soak runs without a save in between), capped at 500 (`kingdomChangesDropped` counts the rest). It is recorded before the homeless-house rule's early return, in its own guard, so a census fault never reaches the campaign event.
  - The player's clan is neither a house nor a minor clan: the installed `player_faction` carries `is_minor_faction`, and the census leaves it out as the engine's minor-clan spawner does; its heroes count in `minorFactionHeroesOutsideMinorClans` if flagged.
  - Each detail is computed in its own guard: a fault becomes a "census failed: ..." string in that field and the rest of the census stays.

## Census (RT-11)

HeroFactory's per-settlement notable caps (`census.notableCaps`, copied from rosters.json) are applied to the source notables in uid order, after the village quota moved the reeves of dropped villages. A notable beyond its cap overflows to the house owning its settlement (else the nearest house of its kingdom with room), then a wanderer; regal heroes never spill to a company. Every overflow is listed in `census.overflow`. Every living FWGC++ hero then ends in exactly one bucket (house, company, outlaw, wanderer) or as an instantiated notable; any other outcome refuses the world (`population_unassigned`), as does a planned house, company or outlaw clan that breaks the policy (`population_invalid`). `census.engineChildren` is the engine's random child generator's expected addition, ceil(adults / 2) less the planned minors per house (`engineChildrenByHouse`).

## The shared name cleaner

`worldbridge::names` (`name_cleaner.hpp`; `campaign-content.json` `names.cleaner`), reused by MMR-286 for every other display name:
- `strip_age_tag`: "(Age N)" goes wherever it stands, in any case, with the whitespace before it ("Dunmar (age 2)mark" → "Dunmarmark");
- `house_name` and `person_name`: the naming patterns above; a patronymic for `given_plus_father` ("Hilde Arnsdottir");
- `compound`: lexicon roots into one word ("Korbrand");
- `Unique::take`: a collision takes a lexicon root as a suffix of its last word, from a per-entity rotation of the civilization's lexicon, then two roots; never a numeral (`has_numeral` recognises digits and roman ordinals).
- `Unique::coin` (MMR-286): a new place name, two lexicon roots, the second one of the kind's concepts (`root_of`); `salt_of` is the
  FNV-1a salt of a uid for the rotations. The compiler and the village quota name villages, castles and nameless towns with it
  ([campaign-semantics.md](campaign-semantics.md) "Names and escaping").

MMR-286 applies the cleaner in the compiler before this pass: source heroes, kingdoms and towns arrive without Age tags, and regents
arrive with a resident's given name and sex, which the pass keeps (`facts` draws no sex for them).

Synthesized given names are unique world-wide: a resident name already used falls back to a two-root lexicon compound.

## Determinism

Every draw is a `population.json` stream value: the first 8 bytes of sha256("mnbl.population-stream/1\n" + stream + "\n" + version + "\n" + world prefix + "\n" + entity + "\n" + draw), big-endian. Records are processed in uid or id order. A re-run is byte-identical (CTest, and two `--compile-travel` runs of seed 42).

## Real worlds (A' and B')

| | Kingdoms | Houses | Adult lords | Minors | Companies | Outlaw clans | Wanderers | Wars | Engine children |
|---|---|---|---|---|---|---|---|---|---|
| A' (seed 42) | 5 (fiefs 11..17) | 45 | 226 (5.02 per house) | 22 | 6 | 5 | 28 | 3 | 102 |
| B' (seed 73) | 7 (fiefs 9..17) | 65 | 326 (5.02 per house) | 31 | 9 | 8 | 40 | 4 | 144 |

All 319 (A') and 440 (B') living FWGC++ heroes are placed: A' 115 house, 33 company, 28 wanderer, 143 notable; B' 176 house, 40 company, 40 wanderer, 184 notable; 0 unassigned. Every castellan is in the house owning its castle.

(A'/B' as regenerated after MMR-261's producer re-pin: new world ids reseed the synthesized members, so minors, children and company sizes moved; every check still holds.) The offline sweep of seeds 43..61 (run before that re-pin) (`evidence/fwg/population.json` `sweep`): every world meets the same checks, K equals its target on all 19, and every world passes the settlement harness; seed 61 (refused by SettlementFactory since MMR-251) passes after one castle moved to the realm its villages trade with.

## Defaults taken

- The merge's land border is "among the 3 nearest fiefs of the same land group"; "nearest by sea route" is the straight periodic distance between fiefs, not a sea path.
- The second merge step seeds K kingdoms with the K largest units and assigns the rest by the weighted distance above, so K is reached within `maxFiefs`.
- Outlaw kinds: bandits mafia, deserters nomad, ruin camps sect, synthesized mafia.
- A castle whose villages cannot trade with a town of its realm changes realm before the merge (rather than refusing the world at SettlementFactory).
- Every house holds 1..4 fiefs (MMR-280's validation requires each house's home to be one of its fiefs).
- Resident name pools: the posts magistrate, commander, garrison_officer, sergeant, soldier and garrison_trooper.
- Life-stage mapping onto 20..34 / 35..49 / 50..70.
- `census.notableCaps` became an object holding the caps (it was a description string).

## Limits

- The managed emitters are MMR-280's ([factions.md](factions.md), [heroes.md](heroes.md)): FactionFactory writes the kingdoms with their `wars[]` as `isAtWar` relationships, the houses and the minor clans; HeroFactory every planned Lord, minor and dead parent and the W wanderers, refusing a world whose instantiated population differs from `census`. What the runtime keeps of them (`IsMinorFactionHero`, the wanderer tick, the homeless-house rule) is MMR-281's ([campaign-behavior-compatibility.md](campaign-behavior-compatibility.md) "Houses, companies and wanderers in play"); what the engine does with them over a campaign (engine children, rotation) is MMR-247's census.
- Kingdom geography: on an island world merged kingdoms span several land groups (A' up to 5 per kingdom; 10 of its 40 realms joined across water); B' (one main land group) has none across water. Whether lords travel between them as planned in game is MMR-247's to see.
