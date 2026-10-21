# Heroes, families and rosters (MMR-32, 2026-10-15; MMR-280, 2026-10-20)

`HeroFactory` turns the compiler heroes (campaign.json v2: MMR-27 and the MMR-279 population pass, [population.md](population.md)) and the MMR-31 faction intents into generated heroes, and resolves the compiler slots MMR-32 owns. Since MMR-280 it emits exactly the planned population: every house, company and outlaw clan Lord, the houses' minors and dead parents, and the W planned wanderers, checked against the campaign v2 `census`. `CharacterTemplateResolver` proves that every installed template and troop they rely on resolves.

| | |
|---|---|
| Code (pure managed) | `src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs` and `CharacterTemplateResolver.cs` |
| Code (game) | `HeroBodies.cs` (explicit bodies), `GeneratedCharacterCreation.cs` (creation cultures), registered by `GeneratedGameManager` |
| Policy | `worldgen/policies/rosters.json` (`mnbl.rosters` v1), written by `tools/make_rosters_policy.py`. The rules are authored; the engine numbers come from the installed IL; the catalog and default references are hash-pinned. `worldgen/policies/population.json` gives the lord-trait distribution and the wanderer pool |
| Tests | `tests/Campaign/CharacterTests.cs` via `tools/Test-Characters.ps1`: the regenerated worlds A' (seed 42) and B' (seed 73) from their verified bundles and the installed merged catalog, 22 checks (`evidence/fwg/heroes-tests.txt`) |
| In game | `generated_campaign` probe with `creationAudit=1`, `restrictCreationCultures`, `creationCultures` and `heroBundle`, summarized by `tools/summarize_character_creation.py` (`evidence/runtime/character-creation.json`) |
| Evidence | `evidence/fwg/heroes.json`; review and disposition in `evidence/fwg/heroes-review.md` |

## What becomes what

How the installed engine creates heroes (installed IL) decides the split:
- **Lords** are XML. `Heroes.xml` holds only lords (all 453 official heroes are lords). `Hero.Deserialize` reads `faction`, `father`, `mother`, `spouse`, `alive`, `text`, `banner_item` and `preferred_upgrade_formation`, and takes its character from the NPCCharacter of the same id.
- **Notables and companions** are made at new game:
  - `NotablesCampaignBehavior.SpawnNotablesAtGameStart` calls `HeroCreator.CreateNotable` once per target, without counting notables already there;
  - `CompanionsCampaignBehavior.OnNewGameCreated` creates companions with `HeroCreator.CreateSpecialHero` from `is_template` Wanderer characters.

  No XML attribute gives a hero a home settlement.

| Kind | How | A' | B' |
|---|---|---|---|
| Planned lords | XML: an NPCCharacter made from an installed lord template, and a Hero in its MMR-31 affiliation clan: house adults, minors and dead parents, company and outlaw clan members | 338 (226 house adults, 22 minors, 24 dead parents, 45 company, 21 outlaw clan members) | 501 (326, 31, 36, 73, 35) |
| Notables | runtime intent: an installed notable template, home settlement, name and age | 805 (143 source, 662 synthesized) | 1,182 (184, 998) |
| Companions | runtime intent: a backstory-safe wanderer template, home town, name and age | 28 | 40 |

No lord, spouse or heir is synthesized here any more: the population pass plans every kin link (MMR-279), and the MMR-32 families block is gone.

**Dispositions.** Every compiler hero gets exactly one:
- `instantiated`;
- `bounded`, naming the policy rule that left it out (none on a compiled world: the census applies the notable caps, so a bounded notable refuses the build);
- `notInstantiated`, for the heroes the compiler does not instantiate (legends), or a lord of a dissolved realm.

A': 519 instantiated, 12 not instantiated. B': 723 and 27.

**Census (MMR-280).** What is instantiated must equal the campaign v2 `census`, or the build is refused: houses, mercenary companies and outlaw clans, adult house lords, minors, dead parents (outside every other count), company and outlaw clan members, source notables, and the wanderers W. `Result.Census` records it; the graph validator recounts it from the staged documents.

**Slots.** MMR-32 resolves every compiler slot it owns, 765 at size 129:
- each kingdom's `troopTreeAndRecruitTemplates`: its culture's basic and elite troops, militia and default party template;
- each town's and castle's `garrison`: its culture's `default_party_template`, from which `GarrisonTroopsCampaignBehavior.FillGarrisonPartyOnNewGame` fills it;
- each compiler hero's `characterTemplate` and `ageTraitsAndFamily`, or its disposition.

The resolutions are in the intents under `slots`.

**Instantiating runtime intents (MMR-34).** The matching call is `HeroCreator.CreateSpecialHero(template, homeSettlement, null, null, age)` followed by `SetName`.
- Its `CreateHero` mints the StringId itself (`MBObjectManager.CreateObject`, `CharacterObject_<n>`).
- `NotablesCampaignBehavior.OnHeroCreated` then places a notable in its home settlement with `EnterSettlementAction`.
- `CreateNotable` would ignore the intent, since it draws its own template and age.

The stable identity of a runtime hero is therefore its intent id. MMR-34 must keep it in its own map from intent id to engine hero, saved with the campaign. It never becomes a StringId.

## Lords and families

**Template.** One of the presentation culture's installed `lord_templates`, drawn from the templates stream. The list is the installed one: sturgia's includes four NavalDLC templates of culture nord, and the hero still takes its own culture.

**What the hero keeps from the template:** its body range (face key template), skill set and traits other than the personality ones.

**What the world supplies:** the id, the localized source name, culture, the planned age and FWGC++ sex (campaign v2 `age` and `female`; a lord without them refuses the build), clan, `alive` and kin.

**Personality (RT-05).** The template's `<Traits>` personality entries (Mercy, Valor, Honor, Generosity, Calculating) are never copied. Each XML hero draws one vector of `population.json lordTraits.joint`, the 127 vectors of the 443 installed alive War Sails lords weighted by count, from the population traits stream seeded by its hero id, and the NPCCharacter carries its non-zero values. Lords are drawn in id order, and a draw that repeats a vector an earlier lord of the same culture already has is drawn once more (`TraitDraws` 2; default taken), so same-culture lords share a personality less often than the installed lords do. The share of same-culture house lords with an identical vector is 0.219 on A' and 0.346 on B', against 0.512 among the installed lords, recomputed by the tests from the catalog. Non-personality template traits (Commander, Manager and the like) are kept (default taken).

**Equipment.**
- No installed lord template is female, and their rosters carry men's gear; one Battanian template's armour is `NotUsableByFemale`.
- A man wears the template's equipment sets.
- A woman wears one battle and one civilian roster of her culture flagged `IsLordTemplate` and `IsFemaleTemplate` (never child or teenager), as the installed equipment selection flags them. Each culture has exactly one female battle roster (`*_bat_template_lady`, with 2-7 sets) and one to five civilian ones.
- A minor (under 18) wears, as civilian equipment, its culture's noble child roster below 14 or teenager roster from 14 (`IsLordTemplate` with `IsChildEquipmentTemplate` or `IsTeenagerEquipmentTemplate`, of its sex), as the engine's random child generator dresses its children; a girl's battle roster is her culture's female lord roster and a boy keeps his template's. Where every roster of the stage holds an item the minor's sex cannot wear, it keeps its lord civilian equipment (recorded in its provenance; none on A' and B').
- Every worn item must exist, fit its slot (`Equipment.IsItemFitsToSlot`) and be usable by the wearer's sex (`NotUsableByFemale` / `NotUsableByMale`). The tests re-derive this from the emitted XML.

**Bodies.**
- `Hero.Deserialize` gives a hero its character's body minimum, and nothing re-randomizes an XML hero, so every hero of one template would share one face.
- Each XML hero therefore carries a seed from the bodies stream. In game, `HeroBodies` draws an explicit body with the engine's own face generator: `BodyProperties.GetRandomBodyProperties` over the template's installed range and tags, for the hero's sex.
- It replaces the face key template in the NPCCharacters document before registration (MMR-45's `CompiledWorldRegistration` calls it inside the new game, where the body ranges are registered: world A, 241 bodies).
- In run 17280 the engine drew 241 distinct bodies (114 female) over 13 body templates. Drawing again with the same seeds gave the same bodies, and none is a template minimum.
- Every hero uses the installed human body. The source `raceId` is kept as metadata, since custom skeletons are out of scope.

**Ages and sex** are the planned ones (FWGC++'s NPC roster, clamped by the population pass). Rulers and clan leaders are always living adults (20 or more).

**Clan:** each lord's clan is the MMR-31 affiliation, so `Kingdom.Deserialize` takes the ruling clan from the ruler's clan.

**Kin** (campaign v2, MMR-279), all checked:
- a spouse link is written on both heroes, between living lords of one clan and opposite sexes;
- father (a man) and mother (a woman) are lords of the child's clan, a living parent at least 18 years older;
- a dead parent (`alive` false) is an XML hero of its house written `alive="false"` in Heroes.xml (as the installed `dead_lord_*` heroes are), the parent of a member, outside the census and every party and lord rule (never a ruler, leader or spouse); its planned age is its age at death, so it may exceed 70 and the age gap is not checked;
- a minor is accepted only with both parents set.

**Random child generator.** `InitialChildGenerationCampaignBehavior` stays on (owner decision 15): it adds ceil(adults / 2) − existing children per house, each cloned from a same-sex adult Lord of the house and dressed from its culture's four child and teenager civilian rosters. So every house has a living adult Lord of each sex and every lord culture has the four rosters; HeroFactory and the graph validator (`graph.child_generator`) refuse otherwise. Planned members are XML heroes, so the generator counts them.

**Bound.** The XML lords are exactly the planned Lords; the census counts them.

## Notables

**Targets** are the installed `DefaultNotableSpawnModel` values: a town needs 2 merchants, 2 gang leaders and an artisan; a village needs a headman and 2 rural notables. Recruitment, workshops and issues expect them, and vanilla spawns exactly these.

**Filling a settlement:**
1. Source notables fill their settlement first, in uid order, up to the caps: town merchant 4, gang leader 2, artisan 2, preacher 1; village headman 1, rural notable 2.
2. A source notable beyond its cap is `bounded`.
3. Every target still unmet is filled by a synthesized notable:
   - uid `notable:<settlement uid>:<occupation>:<n>`;
   - an installed notable template of that occupation;
   - the name "{first name from the culture's list} of {settlement}".

**Sex** follows the template, as `HeroCreator` does. **Ages** are 25-65.

**Settlement culture:**
- a town uses its presentation;
- a village or castle uses the presentation of its source culture uid (as towns and kingdoms carry it), else its bound settlement's or kingdom's.

The map is in the intents (`settlementCultures`), and the garrison slots use it. MMR-33 should give settlements the same cultures.

## Companions

**Count:** W, campaign v2 `census.wanderers`, the smallest integer not below the float32 product 0.6f × towns, as `CompanionsCampaignBehavior.OnNewGameCreated` counts (`for (i = 0; (float)i < 0.6f * towns; i++)`): 28 at 45 towns (0.6f × 45 = 27.000002), 40 at 66 (RT-04).
- **Selection:** the companions are exactly campaign v2's `wanderers[]` (MMR-279: champions, magisters, exiles and domain holders rotating by civilization). A Wanderer not planned, a planned one missing, or a census W other than the planned count refuses the build. None is synthesized. In play the daily tick keeps W (rotation of unmet, unhired wanderers and a top-up made in the installed four steps with the world's `wandererReserve` names; MMR-281, [campaign-behavior-compatibility.md](campaign-behavior-compatibility.md) "Houses, companies and wanderers in play").
- **Age:** the planned one.
- **Home:** a town of the companion's culture.
- **Template (MMR-287, RT-32):** from the backstory-safe pool (`WandererPool`, the one accessor every generated wanderer uses: HeroFactory here, MMR-280's emitter and MMR-281's top-up). A wanderer's conversation lines are keyed by its template id; `tools/census_wanderer_backstories.py` finds the 38 of 78 installed templates whose lines name a Calradian proper noun (an installed settlement, realm, non-bandit clan or hero, Calradia, the emperor, or a reviewed lore noun such as Svalfjord or the Company of the Boar; story-only personal names stay) and lists them in `population.json wanderers.backstoryUnsafe`. The pick keeps the wanderer's FWGC++ sex (campaign v2 `female`), takes the culture's templates least used so far (distinct while the pool allows, then repeats, recorded), and only when the culture has no safe template of that sex the first culture of `wanderers.fallback` that has one (recorded; nord has none). Safe templates per culture (female/male): aserai 2/7, battania 3/4, empire 1/3, khuzait 2/3, nord 0/0, sturgia 3/4, vlandia 2/6.
- **Name:** the source name, which keeps its FWGC++ epithet ("Ulkdurn Wyrmward", "Taelkelev of the Water Line"); a source without one takes the template's epithet ("the Scholar", `wanderers.templates[].epithet`).

## Troops and rosters

Generated cultures present through installed main cultures (MMR-31), so their troops are the installed ones. For every presentation culture (7) and every installed bandit culture (7: looters, sea raiders, mountain, forest, desert, steppe bandits and southern pirates), the resolver walks every root and everything it reaches through upgrade targets and party-template stacks:
- **Recruitment:** basic and elite troop trees; notables offer volunteers of the settlement culture.
- **Garrison:** `default_party_template`.
- **Militia:** the militia troops and `militia_party_template`.
- **Caravans and patrols:** caravan templates, the patrol templates and their troops.
- **Naval:**
  - `settlement_patrol_template_coastal` and `fishing_party_template`, with the ship hulls under their `ship_hulls` (`PartyTemplateObject.Deserialize` reads `ship_hulls/ShipTemplateStack`);
  - `shipwright` and `shipyard_worker`;
  - `available_ship_hulls`.
- **Bandits:** `bandit_*` troops, `bandit_boss_party_template`, and the `default_party_template` of every installed bandit clan (deserters and northern pirates included), from which `BanditSpawnCampaignBehavior` spawns.
- **Required roots:** each presentation culture must have basic, elite, melee and ranged militia troops and the default, villager and militia party templates. Each bandit culture must have a basic troop.

Each troop and template must resolve:
- its body (face key template or body key);
- its skill set;
- battle equipment (and civilian equipment for a hero; stealth sets do not count) through equipment sets and their items;
- its upgrade targets and upgrade item category.

Each item must exist, fit its slot and suit the troop's sex. The engine accepts both `Item.<id>` and bare item ids in equipment (`Equipment.DeserializeNode`), and installed rosters use both.

What refuses the build:
- an upgrade cycle;
- a missing reference or a misfit item in a troop;
- a broken required root.

A broken optional hero template or lord roster is excluded from its pool and reported (`excludedTemplates`); only an empty pool refuses. The ticket asks to fail only when a mandatory reference cannot be represented. At size 129 the presentation cultures reach 46-59 troops each, with upgrade depth 5, and nothing installed is excluded.

## Character creation

**Which cultures are listed.** Character creation lists the cultures the world presents through.
- The installed handlers add aserai, battania, empire, khuzait, sturgia and vlandia (`CharacterCreationCampaignBehavior`, priority 800) and nord (`NavalCharacterCreationCampaignBehavior`, priority 1000).
- `GeneratedCharacterCreation` registers a handler at priority 2000. Once every handler has added its cultures, it removes those the world does not present through. `CharacterCreationContent` has no removal method, so it edits the private `_characterCreationCultures` dictionary, checked against v1.4.8; a mismatch throws.
- A world culture the installed handlers do not offer is reported as missing.
- **The installed culture screen needs a patch.** `CharacterCreationCultureStageVM.SortCultureList` places the six vanilla cultures with `Enumerable.Single`, which throws when one is missing; run 18560 crashed there. While a restricted list is active, a Harmony prefix (`mnbl.proceduralworld.creation`) skips that sort and keeps the list's own order. With all six present, or with no restriction, the installed sort runs unchanged. The patch is removed when character creation finalizes.
- **Run 17280** (the size-129 world's 7 cultures) kept all 7.
- **Run 27608** (a two-culture world, empire and sturgia) kept only those 2 and skipped the installed sort.

**Origins and assets.** The in-game audit checks every listed culture:
- **Origins:** every parent origin is selected through the engine's own `OnSelect`. Parent options check the culture; later menus check the parent occupation (education: urban or rural).
- **Later menus:** for each origin, every later menu must offer an option, and each of those options is selected with `CurrentMenu` set to its menu, to collect the title types it sets. Youth options set the title type that names the player's rosters.
- **Characters:** for every origin and title type, the characters every menu shows (`GetNarrativeMenuCharacterArgs`) must resolve their equipment rosters and hand items.
- **Body:** the culture's creation body property must resolve.

In run 17280 every one of the 7 cultures had 8 origins, urban and rural, each with an option in all 5 later menus. They had 6-7 title types each, and 480-560 menu characters resolved per culture, with no problem.

**Start position.** `CultureObject.StartingPoint`, used when non-zero and navigable, belongs to MMR-34, which must never reuse a Calradia coordinate.

## Identities and determinism

- **Source heroes** keep their compiler ids.
- **Synthesized heroes** (notables only) take MMR-27 stable ids, `<prefix>h_<sha256("mnbl.stable-id/1\nhero\n" + uid)[:10]>`, of documented uids: `notable:<settlement uid>:<occupation>:<n>`. The population pass's synthesized kin have compiler ids.
- **Collisions:** an id that is generated twice, collides with an assigned id or is not world-qualified refuses the build. This matters for XML heroes, whose ids are their StringIds: the engine renames a duplicate registered id silently, while XML merges into the existing object.

**Streams.** Every synthesized value comes from a named, versioned stream: `sha256("mnbl.hero-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity + "\n" + draw)`. The streams are `templates`, `ages` (notables), `names` and `bodies`; personalities use population.json's `traits` stream (`mnbl.population-stream/1`). The `sex`, `family` and `companions` streams are gone with the families block and the companion ranking. Re-versioning a stream changes what it draws and what is derived from that, never ownership, occupations, clans or families. The tests show:
- the names stream: only synthesized names;
- the templates stream: templates, homes and rosters, and with them a notable's sex (which follows its template) and a synthesized name that follows that sex;
- the bodies stream: only body seeds;
- the traits stream: only personalities.

**Bounds.**
- Notables follow settlements (targets and caps), companions are the planned W, lords the planned houses and minor clans.
- Twenty more copies of every source merchant exceed the per-settlement caps the census applied, and are refused.

**Reproducibility** covers the frozen world bundle; explicit bodies are reproducible from their seeds. HeroCreator and the engine still randomize what the runtime intents leave to them (a runtime hero's body within its template's range, birthdays), so campaign save state is not byte-identical.

**Engine numbers.** The policy's engine numbers come from the `TaleWorlds.CampaignSystem` assembly that `config/runtime-api-lock.json` pins, and the tests check that pin. `Build.ps1` verifies the installed game against it, so a game update fails the tests until the numbers are re-read.

## Lifecycle audit (installed IL)

- **XML heroes:** `Hero.Deserialize` → `SetCharacterObject` sets `CharacterObject.HeroObject`, so the link goes both ways. Skills come from `HeroCreationModel.GetDefaultSkillsForHero`; the static body from the character's `BodyPropertiesMin`, hence the explicit bodies.
- **Family links:** `set_Spouse` is reciprocal, and `set_Father`/`set_Mother` add the child.
- **Clan membership:** `Hero.set_Clan` calls `Clan.OnLordRemoved` and `OnLordAdded`, so membership is kept on both sides, and fires `OnHeroChangedClan`. `Clan.Deserialize` → `SetLeader` sets the leader's clan (see `docs/factions.md`).
- **Runtime heroes:** `HeroCreator.CreateSpecialHero` / `CreateNotable` → `CreateHero` → `InitializeHeroFromSettings` set:
  - born settlement, clan and supporter;
  - body (from the template range) and name;
  - occupation (`SetNewOccupation`), traits and default skills;
  - civilian and battle equipment;
  - then `OnHeroCreated`, which places notables.
- **Duplicate ids:** `MBObjectManager.ObjectTypeRecord.RegisterObject` gives a duplicate StringId a counter suffix without an error.
- **Setters are not enough:** setter availability does not prove initialization. Runtime heroes therefore go through HeroCreator, never through property assignment.

## What this does not prove

- **The full world in game (MMR-45).** The XML, with HeroBodies' explicit bodies, is registered and closed in world A's activated campaign: 1,344 objects, closure 0 problems. The notable and companion intents are instantiated by MMR-34's startup replacement. Recruitment in play is MMR-51's; in-mission troop gear is MMR-52's.
- **The character-creation audit runs in the sandbox world.** The culture list, origins and creation assets do not depend on the map; start positions do (MMR-34).
- **Ruler relations** (`relations`, from MMR-31) are hero relation intents for the startup to apply once the heroes exist.
- **The generated population in game:** MMR-281 marks `IsMinorFactionHero` on company and outlaw clan members right after registration and runs the wanderer tick (tested offline, `tools/Test-PopulationRuntime.ps1`); the engine children the generator adds, the marking's effect on the hire dialogue and the rotation over 30+ days are for MMR-247's census.
