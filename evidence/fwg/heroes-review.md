# MMR-32 independent review (2026-10-15)

Reviewer: an independent read-only agent (no repository edits, no game launch). It re-ran `tools/Test-Characters.ps1` (14/14 at the time), built the sources into its own scratch programs against the size-129 bundle, injected faults, regenerated the roster policy (byte-identical) and checked the installed IL.

Below is the report verbatim, followed by the disposition of every finding.

---

MMR-32 independent review. I edited nothing in the repo and launched no game. tools/Test-Characters.ps1 passes 14/14. My scratch programs (%TEMP%\mmr32-review\Dump.cs, Dump2.cs, Exp.cs) build the same sources against the size-129 bundle, dump the XML and intents, and inject faults. I regenerated rosters.json from make_rosters_policy.py into %TEMP%; it is byte-identical to the committed file.

Overall: c1 is not met, c2 is only partly met, c3 is mostly met. There are no BLOCKERs and six MAJORs.

## Findings

### MAJOR

**M1. Female heroes are built from male-only templates, and 5 of them wear armour the engine forbids for women.**
- Where: HeroFactory.cs:162-163, :178, :188, :334.
- Defect: Sex is drawn independently of the template (`Unit("sex") < femaleShare`). The template is picked from `lord_templates`, which contain **0 female templates** in all 7 cultures. Result: 114 of the 241 XML heroes are female on a male template (93 spouses/heirs and 21 source lords).
- Failure: 5 of them get `spc_wolfskins_leader_1`, whose roster holds `battanian_savage_armor` (`NotUsableByFemale="true"`). `Helpers.CharacterHelper::CanUseItem` returns false for that item on a female character (campaign.il ~18473). Affected:
  - `mnbl_1609fb72_h_75255d3010`, a sovereign (a required leader);
  - `..._h_efea02e074`, a pretender;
  - three synthesized spouses: `..._00a25a7aeb`, `..._6237383c5e`, `..._bf6fdb7d27`.
- No test compares hero sex with the template's sex or with item gender flags.
- Fix:
  - pick sex-compatible equipment, e.g. the culture's EquipmentRosters flagged `IsFemaleTemplate`/`IsLordTemplate`;
  - add a resolver check against the `NotUsableByFemale`/`NotUsableByMale` item flags;
  - add a test for both.

**M2. Body properties are never assigned, so heroes sharing a template share one face.**
- Where: HeroFactory.cs:348-361 (CharactersXml keeps the template's `face_key_template`); docs/heroes.md:40.
- Engine behaviour (installed IL):
  - `Hero.Deserialize` sets `StaticBodyProperties = CharacterObject.GetBodyPropertiesMin()` (campaign.il 158255);
  - `GetBodyPropertiesMin` returns `BodyPropertyRange.BodyPropertyMin`;
  - nothing at new game re-randomizes XML heroes (the only callers of `set_StaticBodyProperties` are CreateFrom, UpdatePlayerCharacterBodyProperties, ModifyHair, CharacterData and InitializeHeroFromSettings).
- Result: the 241 XML heroes have only 13 distinct faces — `fighter_sturgia` ×41, `fighter_wolfskin` ×40, `fighter_company_of_boar` ×31. Each is the template's near-all-zero minimum key (e.g. `00000400000000002110…`). The female heroes get the male fighter minimum too.
- No official hero relies on this: all 453 installed heroes use explicit `BodyProperties` keys.
- This misses the ticket's "assign … sex/body properties". The doc's "an official body" is misleading.
- Fix: derive a per-hero body deterministically, e.g. `BodyProperties.GetRandomBodyProperties(race, isFemale, min, max, hairCover, seed, hair, beard, tattoo)` with the seed from a hero stream, applied at registration. Then document it.

**M3. Ship hulls in party templates are never checked (false pass on naval coverage).**
- Where: CharacterTemplateResolver.cs:148-152; docs/heroes.md:89 ("the ship stacks").
- Defect: the resolver looks for `ShipTemplateStack` under `stacks/*`. In the installed `partyTemplates.xml`, all 78 `ShipTemplateStack` elements sit under `<ship_hulls>` and none under `<stacks>`. `PartyTemplateObject.Deserialize` reads `ship_hulls`/`ShipTemplateStack`. So the branch is dead code.
- The test's `Ships.Count > 0` passes only because of `available_ship_hulls`.
- Fault injection: setting `patrol_party_empire_template_coastal`'s hull to `ShipHull.mnbl_missing_hull` was ACCEPTED.
- Fix: walk `ship_hulls/ShipTemplateStack`, and add that injection to the tests.

**M4. The c2 in-game audit covers less than it claims.**
- Where: DiagnosticSandboxBootstrap.cs:249-255, :272-288; docs/heroes.md:108-109; rosters.json:174.
- (a) Title types:
  - `SetSelectedCulture` resets `SelectedTitleType` to `DefaultSelectedTitleType`, which is `"guard"` (set in OnCharacterCreationInitialized).
  - Title types are set by **youth** options' OnSelect, not by the first menu: e.g. `YouthStaffOptionOnSelect` sets `"retainer"`; others set bard, hunter, infantry, kern, mercenary, skirmisher, artisan, seafarer or vagabond.
  - All 56 audited origins in run 35312 report `titleType: "guard"`.
  - So the per-player rosters `player_char_creation_<culture>_<title>_<m|f>` were checked in game only for `guard`. "For every title type the first menu selects" is false.
- (b) Stale parent occupation:
  - Education conditions read `SelectedParentOccupation`, not the culture: `EducationHerderOptionOnCondition = !IsUrbanOccupation(…)`, `EducationWatcherOptionOnCondition = IsUrbanOccupation(…)`.
  - The audit evaluates "suitable" right after `SetSelectedCulture`, which does not reset the parent occupation. Each culture is therefore judged with the previous culture's last origin (`shipmaster_urban`, which the campaign treats as rural), or with null for the first culture.
  - Every culture reported the same rural education set plus docks. The urban set (watcher, ganger, docker, marketer, tutor, poor horser) was never evaluated.
  - "Each option's OnCondition checks only the selected culture" is false.
- Fix: for each origin, run OnSelect and then evaluate later menus, and drive each youth option to collect its title type. Or narrow the documented claim to what is actually checked.

**M5. The c2 culture list matches the world only by coincidence.**
- Where: docs/heroes.md:105; rosters.json:173; GeneratedCampaignBootProbe.cs:84.
- Defect: `InitializeCharacterCreationCultures` hard-codes aserai, battania, empire, khuzait, sturgia and vlandia; NavalDLC adds nord. The presentation set depends on which civilizations are present (culture-presentations.json).
- Failure: a world with only human_maritime and dwarf presents through {empire, sturgia}, but character creation still lists 7. The probe's equality check then fails, and nothing in MMR-32 restricts the list.
- Only one compiled world (size 129, all 7 cultures) exists in build/, so this case is untested.
- Fix: filter or patch the creation culture list to the presentation set, or redefine c2 as "superset with valid starts" and coordinate with MMR-34.

**M6. The stable ids of runtime heroes cannot survive the HeroCreator path the doc prescribes.**
- Where: docs/heroes.md:117-118 and :140; HeroFactory.cs:232 and :405.
- What HeroCreator does (installed IL):
  - `CreateNotable` and `CreateSpecialHero` go through the private `CreateHero` → `CharacterObject.CreateFrom` → `MBObjectManager.CreateObject<CharacterObject>()`. That mints `"CharacterObject_1"` plus a counter suffix, and the Hero takes the same id.
  - `CreateNotable` also draws its own template (`GetRandomTemplateByOccupation`) and its own age, so it ignores the intent's template and age.
  - Only `CreateBasicHero(stringId, character, …)` keeps a chosen id, and it needs a CharacterObject already registered under that id, which MMR-32 does not emit.
- Failure: the 764 runtime intents (737 synthesized notables and 27 companions) carry `<prefix>h_…` ids that the engine path cannot honour. The doc's collision rationale ("the engine renames a duplicate id silently") applies only if these ids become StringIds.
- Fix: say how MMR-34 keeps the identity — either emit non-hero NPCCharacters under those ids for `CreateBasicHero`, or document a side mapping. Also state that `CreateSpecialHero(template, settlement, null, null, age)` plus `SetName` is the matching call.

### MINOR

7. **Bandit spawn templates are not walked.** `BanditSpawnCampaignBehavior` uses `Clan.get_DefaultPartyTemplate()` of the bandit clans (looters_template, sea_raiders_template, southern_pirates_template, northern_pirates_template, …). The resolver only walks bandit culture roots (HeroFactory.cs:131). Injecting a missing troop into `looters_template` was ACCEPTED. The troops happen to be covered via `bandit_*`, but the pirate ship hulls are not. Docs line 90 overstates coverage.
8. **The population test cannot fail** (CharacterTests.cs:144-146). HeroFactory never reads `population`. The property itself holds: inflating source Merchant/Wanderer records ×21 (3447 heroes in) gave 1229 out, with companions fixed at 27 and notables capped. Replace the test with that inflation test.
9. **"Re-versioning one stream never changes another" is false** (rosters.json:6, heroes.md:120-123). Templates stream v2 changes 759 templates, 108 sexes and 54 names, because notable/companion sex follows the template and names follow sex. The test hides this by excluding synthesized names (CharacterTests.cs:170).
10. **Any broken optional template or root refuses the whole build** (CharacterTemplateResolver.cs:189-190, :168). Breaking one of 26 empire notable templates (`spc_notable_empire_0`) refused the build. That contradicts the ticket's "Fail only when a mandatory engine reference cannot be represented validly". Exclude the template and fail only if the pool is empty or a required root is broken.
11. **The "restored culture" check cannot fail.** `restoredCulture` is the value captured before the audit (DiagnosticSandboxBootstrap.cs:261/324), not a read after the restore. summarize_character_creation.py:49 therefore cannot detect a bad restore.
12. **Child age is documented wrongly.** rosters.json:72 says child age is uniform in [1, min(parent ages) − 18]; the code uses [18, …], capped at 30, and has children only when that bound is ≥ 18. heroes.md:48 omits the cap.
13. **Companion count differs from vanilla by one.** The installed `OnNewGameCreated` loops `while (i < 0.6f*T)`, which gives 28 companions at 45 towns. The policy's floor gives 27. The doc implies parity (heroes.md:76, rosters.json:112).
14. **Lords are not bounded by any roster policy.** They follow the compiler's lord records, and relatives scale with clans. Only notables, companions and relatives-per-clan are capped.
15. **MMR-32-owned slots stay pending:**
    - `kingdoms.troopTreeAndRecruitTemplates` ×40;
    - `towns`/`castles` `garrison` ×71;
    - hero `characterTemplate`/`ageTraitsAndFamily`.

    The intents give troops per culture only. The garrison culture actually comes from settlement XML; `settlementCultures` is advisory only ("MMR-33 should use the same map").
16. **The engine numbers are hand-copied and never re-checked.** make_rosters_policy.py hand-codes the IL-derived numbers. `campaignSystemSha256` is recorded but nothing verifies it, so a game update would go unnoticed.

### NIT

- Item-to-slot fit is not checked. `Equipment.DeserializeNode` drops a non-fitting item with a FailedAssert; injecting a sword into the Head slot was accepted.
- `equipmentType="Stealth"` sets count as battle equipment (CharacterTemplateResolver.cs:116-118).
- Docs cite evidence/fwg/heroes.json, heroes-tests.txt and heroes-review.md, none of which exist.
- The comment at DiagnosticSandboxBootstrap.cs:38 still says the audit runs at the culture stage.
- heroes.md:44 says "25% female"; the realized share is 21/92.
- heroes.md:64 "{culture name}" actually means a first name from the culture's name list.
- `Validate`'s `TryGetValue(k.RulerId)` (HeroFactory.cs:299/305) and `lordTemplates[leader.Culture]` (:178) throw ArgumentNullException on null instead of a HeroException.

## Checked and found correct

- **Occupation enum** values match the IL: Lord 3, Wanderer 16, Artisan 17, Merchant 18, Preacher 19, Headman 20, GangLeader 21, RuralNotable 22.
- **Notable targets** match `DefaultNotableSpawnModel` (town Merchant 2 / GangLeader 2 / Artisan 1; village Headman 1 / RuralNotable 2). `SpawnNotablesAtGameStart` calls `CreateNotable` once per target, unconditionally.
- **Companions:** factor 0.6; age 18 + 5 + RandomInt(12) = 23-34; created with `CreateSpecialHero`; the template list is every is_template Wanderer.
- **No XML home settlement:** neither `Hero.Deserialize` nor `CharacterObject.Deserialize` reads one.
- **Character deserialization:**
  - `age` is read as an int (default max(20, range max));
  - `is_female` is read;
  - `skills` is ignored when `skill_template` is set;
  - EquipmentSet references are filtered by `equipmentType` via `AddEquipmentRoster`;
  - removing `is_template` and setting `is_hero` is sufficient;
  - traits, level, culture and occupation come from the character (`SetInitialValuesFromCharacter`).
- **Hero relations:**
  - `Hero.Deserialize` reads exactly the attributes the doc lists;
  - `set_Spouse` is reciprocal, so writing both sides is harmless (no ex-spouse entry);
  - `set_Father`/`set_Mother` add to `_children`;
  - the Heroes XML is not replayed on load, so no duplicate children.
- **Equipment ids:** `Equipment.DeserializeNode` accepts bare item ids, and installed rosters use both forms (7 bare in EquipmentRosters, 25 in NPCCharacters).
- **Upgrade graph:** DFS cycle detection is correct, including self-loops. Missing target, cycle, missing roster, missing body, missing item and missing stack troop are each refused (fault-injected).
- **Garrisons:** `FillGarrisonPartyOnNewGame` uses `Town.Culture.DefaultPartyTemplate`.
- **Stable ids and determinism:**
  - the stable-id formula matches stable_ids.cpp;
  - duplicate, assigned-id and prefix checks are in place;
  - two builds and a build from reordered records give identical intents and XML;
  - the policy regenerates byte-identically.
- **Counts:** 92 lords, 88 spouses, 61 heirs, 909 notables (172 source, 737 synthesized), 27 companions. Dispositions are 291 instantiated, 36 bounded, 12 notInstantiated, exactly one per compiler hero.
- **Families:** spouse links are symmetric, fathers male and mothers female, every parent at least 18 years older, everyone in the clan, rulers and clan leaders adult and alive. `Validate` is live: narrowing pretender ages to 10-12 was refused.
- **Run 35312:**
  - the module hash 5301ed… matches build-api-inputs;
  - the current source hashes of DiagnosticSandboxBootstrap, GeneratedCampaignBootProbe and HeroFactory match that build;
  - 0 problems and no dismissed dialogs;
  - every one of the 56 parent-origin OnSelect calls succeeded.
- **Lifecycle claims:** SetCharacterObject → HeroObject; set_Clan → OnLordRemoved/OnLordAdded/OnHeroChangedClan; RegisterObject suffixes duplicate ids; skills via `GetDefaultSkillsForHero`.
- **Official data claims:** all 453 official heroes are lords; sturgia's `lord_templates` include 4 nord templates.
- **Start positions:** leaving them to MMR-34 is a reasonable split for c2.

## Verdict per criterion

- **c1: NOT MET.** References exist and resolve, but:
  - female required leaders and spouses carry invalid equipment (M1);
  - bodies are degenerate shared minimum keys (M2);
  - the naval ship hulls and bandit spawn templates the criterion covers are never checked (M3, minor 7).
- **c2: PARTIALLY MET.** In game, the 7 listed cultures, each culture's origin OnSelect, the parent rosters and the `guard`-title player rosters resolve. However:
  - title types and urban-origin menus were not covered, and the docs overclaim (M4);
  - the list equals the world's cultures only because this world presents through all 7 (M5).
- **c3: MOSTLY MET.** Notables, companions and relatives are policy-bounded, deterministic and documented. However:
  - the population test cannot fail (minor 8);
  - lords are not policy-bounded (minor 14);
  - the stream-independence claim is false (minor 9);
  - runtime stable ids have no engine realization path (M6).

---

## Disposition

All findings were fixed in one batch. After the fixes:
- `tools/Test-Characters.ps1` passes 19/19 (`evidence/fwg/heroes-tests.txt`), and `tools/Test-Factions.ps1` still passes 29/29.
- The mod builds (Build.ps1, verified inputs).
- Two in-game runs on the final module pass 43/43 together (`evidence/runtime/character-creation.json`):
  - run 17280: the size-129 world's 7 cultures, plus hero bodies;
  - run 27608: a two-culture world, empire and sturgia.
- Run 17280 built the size-129 heroes in game and got intents identical to the offline build (`f7ac38271c00…`).

Along the way, run 18560 crashed. The installed culture screen cannot sort a restricted list (see M5). That run's process was stopped and the crash fixed before runs 27608 and 17280.

| # | Severity | Disposition |
|---|---|---|
| M1 | Major | **Fixed.** A woman now wears her culture's installed female lord rosters, one battle and one civilian, flagged `IsLordTemplate` and `IsFemaleTemplate` and never child or teenager. A man keeps his template's equipment. The resolver checks every worn item: it must exist, fit its slot and suit the wearer's sex (`NotUsableByFemale` / `NotUsableByMale`), for heroes and troops. A roster that fails is excluded. The tests re-derive the worn items from the emitted XML and the catalog: 114 women, 0 items unusable by the wearer's sex. An injected `battanian_savage_armor` in a female roster gets that roster excluded and never worn |
| M2 | Major | **Fixed.** Every XML hero carries a seed from a new `bodies` stream, plus its template's body range. The game-side `HeroBodies` draws an explicit body with `BodyProperties.GetRandomBodyProperties` (race 0, sex, range min/max, the range's hair/beard/tattoo tags, seed) and replaces the face key template in the NPCCharacters document; MMR-45 calls it on the registration path. In run 17280 the engine drew 241 distinct bodies (114 female) over 13 templates. Drawing again gave identical bodies, none is a template minimum, and all 241 NPCCharacters carry an explicit body. Docs corrected |
| M3 | Major | **Fixed.** The resolver walks `ship_hulls/ShipTemplateStack`, which `PartyTemplateObject.Deserialize` reads. A missing hull injected into `patrol_party_aserai_template_coastal` is refused (tested) |
| M4 | Major | **Fixed.** The audit was rewritten. For each culture, every parent origin is selected through OnSelect, and every later menu is evaluated for that origin (each must offer an option). Each later option is selected with `CurrentMenu` set to its menu, collecting the title types it sets. Then the menu characters of every origin and title type are resolved. In run 17280 each culture had 8 origins (urban and rural), all 5 later menus non-empty for each, and 6-7 title types (e.g. guard, hunter, infantry, retainer, seafarer, skirmisher); 480-560 menu characters resolved per culture over 256 OnSelect calls, with 0 problems. Docs and policy now describe exactly this |
| M5 | Major | **Fixed.** `GeneratedCharacterCreation` registers a content handler at priority 2000, after vanilla (800) and NavalDLC (1000). Once all cultures are added, it removes those the world does not present through (by reflection on `_characterCreationCultures`, version-checked) and reports any missing. The installed `CharacterCreationCultureStageVM.SortCultureList` uses `Enumerable.Single` on the six vanilla cultures and crashed with a restricted list (run 18560). So while a restricted list is active, a Harmony prefix (`mnbl.proceduralworld.creation`) skips that sort; it is removed at finalize and inert otherwise. Run 27608 (empire and sturgia) lists exactly those two, with the full audit. Run 17280 keeps all 7 |
| M6 | Major | **Fixed (documented contract).** Docs, the policy and the HeroFactory header now name the matching call, `HeroCreator.CreateSpecialHero(template, settlement, null, null, age)` + `SetName`. They state that the engine mints the StringId and that `NotablesCampaignBehavior.OnHeroCreated` places notables. The runtime identity is the intent id, which MMR-34 must keep in its own saved map. `CreateNotable` must not be used, because it ignores the template and age. A comment records this on MMR-34 |
| 7 | Minor | **Fixed.** The `default_party_template` of every installed bandit clan (Factions.xml `is_bandit`) is walked, including deserters and northern pirates. A missing troop in `looters_template` is refused (tested) |
| 8 | Minor | **Fixed.** The test now adds 20 more copies of every source merchant and wanderer (3,447 source heroes). Notables stay within caps, there are 28 companions and the XML heroes are unchanged (1,230 heroes, 3,092 bounded) |
| 9 | Minor | **Fixed.** The claim now says what the test shows. A stream changes what it draws and what derives from it: a runtime hero's sex follows its template, a synthesized name follows the sex, a family follows the ages. No stream changes ownership, occupations, clans or families. The test checks this for names, templates, bodies and companions |
| 10 | Minor | **Fixed.** A broken optional hero template or lord roster is excluded and reported (`excludedTemplates`); only an empty pool refuses. Troops and required roots still refuse, as the ticket demands for upgrade links. Tests: a broken lord template is excluded and unused; a culture left with no usable artisan template is refused |
| 11 | Minor | **Fixed.** The audit reads `SelectedCulture` back after the restore and reports a mismatch as a problem |
| 12 | Minor | **Fixed.** The policy and docs give heir ages as [18, min(min parent age − 18, 30)], with no heir when the bound is below 18 |
| 13 | Minor | **Fixed.** Companions follow the installed float loop, giving 28 at 45 towns. Policy, docs and tests updated |
| 14 | Minor | **Fixed (bound stated and tested).** Source lords are MMR-27's lord records, never per population. XML heroes are at most source lords + 3 per clan (241 ≤ 92 + 276), which the tests check |
| 15 | Minor | **Fixed.** HeroFactory resolves all 765 MMR-32-owned compiler slots (`slots` in the intents): kingdom troop trees and recruit templates, town and castle garrison templates (culture `default_party_template`), and each hero's template and age/traits/family or its disposition. The tests check the count and that every referenced object resolves |
| 16 | Minor | **Fixed.** The tests check the policy's `campaignSystemSha256` against the `config/runtime-api-lock.json` pin, which Build.ps1 verifies against the installed game. The generator docstring says the numbers must be re-read on a change |
| NIT | Nit | **All fixed:** item-to-slot fit is checked (a sword in a head slot is refused); stealth sets do not count as battle equipment; the evidence files now exist; the bootstrap comment is updated; the docs give the realized female share (21/92) and "first name from the culture's list"; and `Validate` and the family code are null-safe and raise `HeroException` |

**Criteria after the fixes:**
- **c1 met:**
  - required roles: all 40 rulers and 92 clan leaders are adult XML lords;
  - every town and village meets the installed notable targets;
  - every hero's template, worn equipment (by sex and slot) and body range resolve, and the engine drew explicit, distinct bodies in game;
  - the troop graphs of 7 presentation and 7 bandit cultures resolve, ship hulls and bandit spawn templates included;
  - every broken-reference class is fault-injected and refused.
- **c2 met:**
  - character creation lists exactly the world's cultures (7 at size 129; 2 in the restricted run);
  - every listed culture's origins, later menus, title types and creation assets resolve in game;
  - start positions stay with MMR-34.
- **c3 met:**
  - notables, companions and relatives are bounded by policy (inflation-tested);
  - lords are bounded by MMR-27's records;
  - synthesized identities are stable MMR-27-style ids of documented uids, and the runtime identity contract is documented;
  - output is deterministic, offline and in game.
