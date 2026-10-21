# Faction definitions (MMR-31, 2026-10-15; MMR-280, 2026-10-20)

`FactionFactory` turns the compiler records into closed kingdom and clan intents and builds the Kingdoms and Factions XML the MMR-30 registry loads. Since MMR-280 it emits the campaign.json v2 population blocks the MMR-279 population pass plans ([population.md](population.md)): the merged kingdoms with their starting wars, the noble houses, the mercenary companies and the outlaw clans. It also produces:
- the generated cultures' presentation;
- the kingdom stances and the rulers' relations;
- the lord-to-clan affiliations C03 writes into Heroes.xml.

Sources and scope:
- **From the generated world:** identities, ownership, membership, kin and wars.
- **Installed public assets:** official cultures, the banner mesh, palette and icons, policies, title strings, and the minor clans' troop lines (`population.json troopTemplates`).
- **No engine object is created.** Heroes and settlements are referenced by their compiler ids. C03 (MMR-32) instantiates the heroes, C04 (MMR-33) binds the fiefs, and C07 (MMR-36) proves registered closure.

| | |
|---|---|
| Code | `src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs` (pure managed) |
| Policy | `worldgen/policies/factions.json` (`mnbl.factions` v1), written by `tools/make_factions_policy.py`. The rules are authored; the asset pools are read from installed files and hash-pinned. `worldgen/policies/population.json` (MMR-248) gives the minor clans' troop lines and tiers and the war limits |
| Culture templates | `worldgen/templates/cultures/contract.json` (v2) and `<culture>.json` for the 7 main cultures, written by `tools/Scan-CultureContract.ps1` |
| Tests | `tests/Campaign/FactionTests.cs` via `tools/Test-Factions.ps1`: 34 checks on the regenerated worlds A' (seed 42) and B' (seed 73), read from their verified bundles. The receipt hash-pins the sources, the policy and every culture template |
| Evidence | `evidence/fwg/factions.json` (counts, hashes); review and disposition in `evidence/fwg/factions-review.md` (MMR-31) |

## Kingdoms and clans

| Concept | Rule |
|---|---|
| Kingdom | One per merged kingdom with at least one town (campaign v2 `kingdoms[]`, merged from FWGC++ realms by MMR-279; `memberRealms` and `mergeSteps` stay in the records). Its ruler, ruling house and capital are the compiler's, and each is checked: the ruler is a living compiler lord who leads the ruling house, and the capital is one of the kingdom's towns. Any failure refuses the build |
| Ruler | The highest-ranked member realm's sovereign, or the compiler's synthesized regent (`regent:<realm uid>`) when it has none. Never null |
| Noble houses | Every planned house (`houses[]`) is one clan of its kingdom: owner its leader, home its seat (one of its fiefs), its `fiefs` at most 4. Its members are its living planned Lords, adults and minors; a minor (under 18) is accepted only with both parents set in the house. Its dead parents (`alive` false) belong to it without being members, each the parent of a member. There is no clan per lord: the MMR-31 vassal-per-lord path and its `vassalClans` policy are gone (clean break) |
| Tiers | The ruling house from its kingdom's fiefs (towns plus castles): 4 from 1, 5 from 3, 6 from 6 (`rulingClan.tierByFiefs`). Every other house from its own fiefs: 3 from 1, 4 from 2, 5 from 3 (`houses.tierByFiefs`; default taken, MMR-280) |
| Mercenary companies | Every `mercenaryCompanies[]` record is a minor clan with no kingdom: `is_minor_faction` and `is_clan_type_mercenary`, no `super_faction`, owner its captain, tier 4 (`population.json companies.tier`), home its town |
| Outlaw clans | Every `outlawClans[]` record is a minor clan with no kingdom: `is_minor_faction`, `is_outlaw` and one of `is_mafia`, `is_nomad` or `is_sect` for its kind, owner its leader, tier 4 (`outlaws.tier`), home its town. Its `wars` must be exactly the kingdoms of its culture (the engine declares them, as for the installed outlaw clans), at most `outlaws.maxWarKingdoms` (2) |
| Minor clan troops | `default_party_template` and the 4 `minor_faction_character_templates` are the clan's line of `population.json troopTemplates` for its presentation culture: the culture's mercenary line for a company, the first outlaw line of its kind (else the first) for an outlaw clan (RT-03). A party template that is any main culture's `default_party_template` (a lord party template) is refused. HeroFactory checks that the template and the 4 characters are installed |
| Members of minor clans | Living adult planned Lords of the matching bucket whose `clan` is the clan and who belong to no kingdom |
| Names | Kingdom, house, company and outlaw clan names come clean and unique from the compiler's name cleaner (MMR-279, MMR-286: no Age tag, never an ordinal); two kingdoms or two clans with one name are refused, never renamed. The source name stays in the intent (`sourceName`), and a renamed kingdom's encyclopedia text names it (`texts.sourceNote`) |
| Membership | Every member must be a compiler lord of the clan's bucket, kingdom (houses) and `clan`. Every living house lord of a kingdom is in exactly one of its houses, every company and outlaw clan lord in exactly its clan, every dead parent of its house; the lords of a dissolved realm are in none. A wanderer in a clan, a lord in no clan or two clans, a minor without both parents, or a lord without a planned age and sex refuses the build |
| Zero-town realm | Dissolved: no kingdom or house intent. The record lists, for C03 and C04: all lords of the kingdom, dead parents included (each affiliated to nothing), its castles and its compiler houses, with the reason. A war naming it is dropped (`droppedWars`). The `refuse` setting fails instead. The merged worlds have none |
| Resources | `Clan.Deserialize` reads `tier` and sets renown from it (`CalculateInitialRenown`, installed IL), so tier is the resource set here. Gold and influence follow the installed new-game logic once C03 instantiates the heroes |

**Worlds A' and B'** (seed 42 and 73, MMR-279's plan):

| | A' | B' |
|---|---|---|
| Kingdoms (ruling houses) | 5 | 7 |
| Houses (by origin) | 45 (5 kingdom rulers, 35 realm rulers, 5 castellans) | 65 (7, 47, 8 castellans, 3 pretenders) |
| House tiers 3 / 4 / 5 / 6 | 27 / 10 / 3 / 5 | 32 / 23 / 3 / 7 |
| Living house lords (minors), dead parents | 247 (21), 23 | 358 (30), 33 |
| Mercenary companies, outlaw clans (kinds) | 6, 5 (4 mafia, 1 nomad) | 9, 8 (6 mafia, 2 nomad) |
| Starting war pairs | 3 | 4 |
| Banners, all unique | 61 | 89 |

## Affiliations: what C03 must write

The engine does not take clan membership from Factions.xml alone (installed IL):
- `Kingdom.Deserialize` sets `RulingClan = owner.Clan`. The owner's clan is read at Kingdoms time from the ruler's Heroes.xml `faction`. Nothing in Kingdoms.xml names the ruling clan.
- `Clan.Deserialize` calls `SetLeader(hero)`, which sets `hero.Clan` to the clan it leads.

The factory emits `affiliations` for every compiler Lord: its clan and kingdom (none for a company or outlaw clan member), `dead` for a dead parent, or null for a lord of a dissolved realm. They equal the compiler's `hero.clan` for every lord. C03 writes `faction="Faction.<clan>"` from them. Every ruler is affiliated to its ruling house, so the engine's ruling clan is the intent's.

## Cultures

Every source civilization presents through one installed main culture (`worldgen/policies/culture-presentations.json`, MMR-27).
- **Checks:** a kingdom's culture must be its civilization's presentation. Every house's and minor clan's culture must be an installed main culture with a complete template. An overridden or invalid culture refuses the build; nothing falls back.
- **Metadata:** the generated culture uid, civilization and names are kept on each kingdom and in `cultures`, with the template each uses.
- **No culture of its own:** the world defines none. The factory emits only Kingdoms and Factions.

`tools/Scan-CultureContract.ps1` records the installed culture contract from the assemblies (contract v2):
- **What it reads:** every XML member `CultureObject.Deserialize` and `BasicCultureObject.Deserialize` read (from their IL), and the property each fills.
- **Setters**, read on the property's declaring type: 100 of the 108 members have a private setter and 5 are get-only lists filled through their backing field (`default_policies`, the three name lists, `cultural_feats`). 3 have a public setter: `militia_bonus`, `prosperity_bonus` and `encounter_background_mesh`.
- **Writers:** the methods whose IL calls each setter. For every member the only writer is `CultureObject.Deserialize` or `BasicCultureObject.Deserialize`. A private setter is searched in its declaring type and that type's nested types; a public one in its whole declaring assembly, so other modules' writes are not covered.
- **Constructors:** both types have one public constructor that takes no culture data, so a culture is complete only after Deserialize.

This is why a generated world reuses the installed, XML-initialized cultures and never constructs or patches one in code. Reusing a visual asset therefore never leaves a culture default null.
- **Required members:** the 92 members every installed main culture sets. All 7 templates set all of them, including the naval members (`shipwright`, `shipyard_worker`, `fishing_party_template`, `available_ship_hulls`, `settlement_patrol_template_coastal`, `naval_factor`).
  - For the installed main cultures `requiredMissing` is empty by construction, so the real guarantee is reusing them unchanged.
  - The check guards any other template. The tests show a template missing a required member is refused.
- **Optional members:** the bandit-only members and the optional bonuses and colours with defaults are empty in the installed main cultures too.

## Banners, colours, policies and texts

**Layout.** Every banner has two installed layers:
- the background is mesh 11, as on all 82 official noble clans, at size 4922;
- the icon, from the culture's `possible_clan_banner_icon_ids`, is at size 454;
- both are centred.

`tools/make_factions_policy.py` checks every culture icon and the background mesh against all installed `banner_icons.xml` files: Native, and NavalDLC for the Nord icons 165-174. Both files are hash-pinned.

**Colours.** Each kingdom and each minor clan gets an ordered pair of distinct colours from the installed 229-colour palette in Native's `banner_icons.xml`: the background colour and the icon colour.
- The pairs are drawn from the colors stream, kingdoms first in id order and then the minor clans in id order; each pair is unique world-wide, so an equal or used pair draws the next pair.
- A kingdom's `color`, `color2` and primary and secondary banner colours are its two colours; a minor clan writes its own `color` and `color2`. A house uses its kingdom's colours.

**Icons.**
- The kingdom's icon is one banner-stream draw.
- Each of its houses, the ruling house first and then the others by id, carries the kingdom's colours with an icon drawn until it differs from the kingdom's and every earlier house's.
- As in the official game, a ruling house's banner is not its kingdom's.
- A kingdom with more banners than its culture has icons is refused.
- A minor clan's icon is one banner-stream draw from its culture's icons.

**Uniqueness.** Colour pairs separate the kingdoms and minor clans, and icons separate a kingdom's own banners, so every banner is unique (61 on A', 89 on B').

**Other attributes and texts.**
- **Policies:** each kingdom's policies are its culture's installed `default_policies`.
- **Ruler title:** the installed localized string of the first official kingdom of that culture (for example `{=FmbgPGBL}Grand Prince`). All kingdoms presented through one culture share it. This reuses a string, not an official kingdom.
- **Texts:** the encyclopedia text of kingdoms, houses, companies ("A company of mercenaries based in {home}.") and outlaw clans ("An outlaw clan based in {home}.") comes from the policy templates.
  - All names and texts are `{=<id>}text`; the compiler's own `{=<id>}name` strings are rebuilt the same way.
  - Text is cleaned first. Control characters, characters XML forbids (U+FFFE, U+FFFF) and unpaired surrogates are dropped; a well-formed surrogate pair is kept. Braces become parentheses.
  - Values are substituted in one pass, and XML is escaped by the writer. The tests show the XML reparses and writes with a strict `XmlWriter`.
- **Attributes emitted:** only the attributes `Kingdom.Deserialize` and `Clan.Deserialize` read. `flag_mesh` and `settlement_banner_mesh` are not read, so they are not written. A minor clan carries its flags, `default_party_template`, `color`, `color2` and `<minor_faction_character_templates>`, as the installed `spclans.xml` writes them; its owner is written too, because its members are XML heroes.

## Diplomacy

- **Starting wars (RT-06):** each campaign v2 `wars[]` pair (MMR-279: about one per kingdom with a neighbour) starts at war. Both kingdoms carry `<relationships><relationship kingdom="Kingdom.<other>" value="-1" isAtWar="true"/></relationships>`, as the installed `spkingdoms.xml` writes its starting wars. The installed `Kingdom.Deserialize` reads only the sign of a relationship `value`: below 0 calls `FactionManager.DeclareWar`, otherwise `SetNeutral`. A repeated pair, a pair naming one kingdom twice or a kingdom with more than `population.json wars.maxPerKingdom` (2) wars refuses the build. A' starts 3 war pairs (every kingdom 1 or 2), B' 4.
- **Neutral otherwise:** every other pair starts neutral, the engine default for a pair with no stance, written nowhere.
- **Outlaw clans** are at war with the kingdoms of their culture, declared by the engine as for the installed outlaw clans; the intent records them.
- **Ruler relations:** the rulers of each pair get a relation of 0 ± 20 from the relations stream. This is a hero relation intent (`relations[].rulerRelation`) for C03 to apply once the heroes exist.
- **Clan relations** stay at the engine default.

## Determinism and streams

Records are processed in id order (names in uid order), so reordering them changes nothing. Every synthesized value comes from a named, versioned stream: `sha256("mnbl.faction-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity + "\n" + draw)`. The streams are `banner`, `colors` and `relations`.

Ownership, membership and wars use no stream. Re-versioning one stream changes only what it draws, and the tests check this:
- the banner stream: icons only;
- the colors stream: colours only;
- the relations stream: ruler relations only.

Colour pairs separate kingdoms and minor clans, so no icon depends on a colour draw. A kingdom's icons depend only on its own houses.

Within one stream, an earlier kingdom can change a later kingdom's or minor clan's colours only through a colour-pair collision. The tests show dissolving the first kingdom changes no other kingdom's or house's banner.

The same input gives the same intents (`intentsSha256` in the evidence) and the same XML.

## What this does not prove

- **No registration in game.** The faction XML references heroes and towns that C03 and C04 instantiate, and registered closure is C07's (MMR-36). Rulers and capitals are identity intents only.
- **The engine's ruling clan and leader membership** hold only once C03 writes the affiliations above; C07 proves them.
- **Minor clans in game:** MMR-281 sets `Hero.IsMinorFactionHero` on company and outlaw clan members right after registration (before the minor clans' parties spawn) and again on load, and moves a house left without a kingdom by a dissolution into the nearest kingdom of its culture (else the nearest) before the 28-day removal ([campaign-behavior-compatibility.md](campaign-behavior-compatibility.md) "Houses, companies and wanderers in play"). The outlaw wars the engine declares, the starting wars on day 0 and both rules in play are MMR-247's to see.
- **The unexpected-Calradia census is MMR-36's.**
