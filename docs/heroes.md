# Heroes, families and rosters (MMR-32, 2026-10-15)

`HeroFactory` turns the MMR-27 compiler heroes and the MMR-31 faction intents into generated heroes, and resolves the compiler slots MMR-32 owns. `CharacterTemplateResolver` proves that every installed template and troop they rely on resolves.

| | |
|---|---|
| Code (pure managed) | `src/MNBL.ProceduralWorld/Campaign/HeroFactory.cs` and `CharacterTemplateResolver.cs` |
| Code (game) | `HeroBodies.cs` (explicit bodies), `GeneratedCharacterCreation.cs` (creation cultures), registered by `GeneratedGameManager` |
| Policy | `worldgen/policies/rosters.json` (`mnbl.rosters` v1), written by `tools/make_rosters_policy.py`. The rules are authored; the engine numbers come from the installed IL; the catalog and default references are hash-pinned |
| Tests | `tests/Campaign/CharacterTests.cs` via `tools/Test-Characters.ps1`: the real size-129 world (verified MMR-29 bundle) and the installed merged catalog, 19 checks (`evidence/fwg/heroes-tests.txt`) |
| In game | `generated_campaign` probe with `creationAudit=1`, `restrictCreationCultures`, `creationCultures` and `heroBundle`, summarized by `tools/summarize_character_creation.py` (`evidence/runtime/character-creation.json`) |
| Evidence | `evidence/fwg/heroes.json`; review and disposition in `evidence/fwg/heroes-review.md` |

## What becomes what

How the installed engine creates heroes (installed IL) decides the split:
- **Lords** are XML. `Heroes.xml` holds only lords (all 453 official heroes are lords). `Hero.Deserialize` reads `faction`, `father`, `mother`, `spouse`, `alive`, `text`, `banner_item` and `preferred_upgrade_formation`, and takes its character from the NPCCharacter of the same id.
- **Notables and companions** are made at new game:
  - `NotablesCampaignBehavior.SpawnNotablesAtGameStart` calls `HeroCreator.CreateNotable` once per target, without counting notables already there;
  - `CompanionsCampaignBehavior.OnNewGameCreated` creates companions with `HeroCreator.CreateSpecialHero` from `is_template` Wanderer characters.

  No XML attribute gives a hero a home settlement.

| Kind | How | Count at size 129 |
|---|---|---|
| Source lords | XML: an NPCCharacter made from an installed lord template, and a Hero in its MMR-31 affiliation clan | 92 |
| Spouses and heirs | XML, adapter-synthesized: one spouse per clan leader aged 25 or more; 0-2 adult heirs per couple | 88 spouses, 61 heirs |
| Notables | runtime intent: an installed notable template, home settlement, name and age | 909 (172 source, 737 synthesized) |
| Companions | runtime intent: an installed wanderer template, home town, name and age | 28 |

**Dispositions.** Every compiler hero gets exactly one:
- `instantiated`;
- `bounded`, naming the policy rule that left it out;
- `notInstantiated`, for the 12 the compiler does not instantiate, or a lord of a dissolved realm.

At size 129: 292 instantiated, 35 bounded (29 wanderers over the companion bound, 6 notables over a cap), 12 not instantiated.

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

**What the hero keeps from the template:** its body range (face key template), skill set and traits.

**What the world supplies:** the id, the localized source name, culture, age and sex.

**Equipment.**
- No installed lord template is female, and their rosters carry men's gear; one Battanian template's armour is `NotUsableByFemale`.
- A man wears the template's equipment sets.
- A woman wears one battle and one civilian roster of her culture flagged `IsLordTemplate` and `IsFemaleTemplate` (never child or teenager), as the installed equipment selection flags them. Each culture has exactly one female battle roster (`*_bat_template_lady`, with 2-7 sets) and one to five civilian ones.
- Every worn item must exist, fit its slot (`Equipment.IsItemFitsToSlot`) and be usable by the wearer's sex (`NotUsableByFemale` / `NotUsableByMale`). The tests re-derive this from the emitted XML.

**Bodies.**
- `Hero.Deserialize` gives a hero its character's body minimum, and nothing re-randomizes an XML hero, so every hero of one template would share one face.
- Each XML hero therefore carries a seed from the bodies stream. In game, `HeroBodies` draws an explicit body with the engine's own face generator: `BodyProperties.GetRandomBodyProperties` over the template's installed range and tags, for the hero's sex.
- It replaces the face key template in the NPCCharacters document before registration (MMR-45's `CompiledWorldRegistration` calls it inside the new game, where the body ranges are registered: world A, 241 bodies).
- In run 17280 the engine drew 241 distinct bodies (114 female) over 13 body templates. Drawing again with the same seeds gave the same bodies, and none is a template minimum.
- Every hero uses the installed human body. The source `raceId` is kept as metadata, since custom skeletons are out of scope.

**Ages by role:** sovereign 35-62, regent 40-66, pretender 22-45, castellan 28-56, founder 25-50. Rulers and clan leaders are always adults (20 or more) and alive.

**Sex:** each source lord is female with probability 0.25 from the sex stream (21 of 92 at size 129).

**Clan:** each lord's clan is the MMR-31 affiliation, so `Kingdom.Deserialize` takes the ruling clan from the ruler's clan.

**Families**, all adapter-synthesized:
- every clan leader aged 25 or more gets a spouse of the opposite sex, in the clan, with the spouse link written on both heroes;
- each couple gets 0-2 adult heirs aged 18 to min(min(parent age) − 18, 30), with father and mother set; a couple whose upper bound is below 18 has none;
- names are first names from the culture's installed name lists;
- at most 3 relatives per clan.

Without relatives, every clan is a single hero and would die with its leader.

**Bound.** The source lords are MMR-27's lord records (sovereign or regent per realm, castellans, pretenders, founders), never one per person of a population. XML heroes number at most source lords + 3 per clan: 241 ≤ 92 + 3 × 92.

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

**Count:** as many as the installed loop creates. `CompanionsCampaignBehavior.OnNewGameCreated` runs `for (i = 0; (float)i < 0.6f * towns; i++)`, so 28 at 45 towns (0.6f × 45 = 27.000002).
- **Selection:** source wanderers are ranked by the companions stream over their uid, so record order does not matter. None is synthesized, because the source supplies 57.
- **Age:** 23-34, from the installed HeroComesOfAge (18) + 5 + [0, 12).
- **Home:** a town of the companion's culture.
- **Template:** a Wanderer template of that culture.

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
- **Synthesized heroes** take MMR-27 stable ids, `<prefix>h_<sha256("mnbl.stable-id/1\nhero\n" + uid)[:10]>`, of documented uids: `spouse:<leader uid>`, `heir:<leader uid>:<n>`, `notable:<settlement uid>:<occupation>:<n>`.
- **Collisions:** an id that is generated twice, collides with an assigned id or is not world-qualified refuses the build. This matters for XML heroes, whose ids are their StringIds: the engine renames a duplicate registered id silently, while XML merges into the existing object.

**Streams.** Every synthesized value comes from a named, versioned stream: `sha256("mnbl.hero-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity + "\n" + draw)`. The streams are `templates`, `ages`, `sex`, `names`, `family`, `companions` and `bodies`. Re-versioning a stream changes what it draws and what is derived from that, never ownership, occupations, clans or families. The tests show:
- the names stream: only synthesized names;
- the templates stream: templates, homes and rosters, and with them a notable's or companion's sex (which follows its template) and a synthesized name that follows that sex;
- the bodies stream: only body seeds;
- the companions stream: only which wanderers become companions.

**Bounds.**
- Notables follow settlements (targets and caps), companions follow towns, relatives follow clans.
- Twenty more copies of every source merchant and wanderer (3,447 source heroes) still give the same capped notables and 28 companions, with 3,092 bounded.

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
