# Faction definitions (MMR-31, 2026-10-15)

`FactionFactory` turns the MMR-27 compiler records into closed kingdom and clan intents and builds the Kingdoms and Factions XML the MMR-30 registry loads. It also produces:
- the generated cultures' presentation;
- the kingdom stances and the rulers' relations;
- the lord-to-clan affiliations C03 writes into Heroes.xml.

Sources and scope:
- **From the generated world:** identities, ownership and relationships.
- **Installed public assets:** official cultures, the banner mesh, palette and icons, policies, and title strings.
- **No engine object is created.** Heroes and settlements are referenced by their compiler ids. C03 (MMR-32) instantiates the heroes, C04 (MMR-33) binds the fiefs, and C07 (MMR-36) proves registered closure.

| | |
|---|---|
| Code | `src/MNBL.ProceduralWorld/Campaign/FactionFactory.cs` (pure managed) |
| Policy | `worldgen/policies/factions.json` (`mnbl.factions` v1), written by `tools/make_factions_policy.py`. The rules are authored; the asset pools are read from installed files and hash-pinned |
| Culture templates | `worldgen/templates/cultures/contract.json` (v2) and `<culture>.json` for the 7 main cultures, written by `tools/Scan-CultureContract.ps1` |
| Tests | `tests/Campaign/FactionTests.cs` via `tools/Test-Factions.ps1`: 29 checks on the real size-129 compiler world, read from the verified MMR-29 bundle (`evidence/fwg/factions-tests.txt`). The receipt hash-pins the sources, the policy and every culture template |
| Evidence | `evidence/fwg/factions.json` (counts, hashes); review and disposition in `evidence/fwg/factions-review.md` |

## Kingdoms and clans

| Concept | Rule |
|---|---|
| Kingdom | One per realm with at least one town. Its ruler, ruling clan and capital are MMR-27's, and each is checked to resolve in the compiler graph: the ruler is a compiler lord, the ruler leads the ruling clan, and the capital is one of the kingdom's towns. Any failure refuses the build |
| Ruler | The realm's sovereign, or MMR-27's synthesized regent (`regent:<realm uid>`) when the source has none. Never null; 8 of 40 realms at size 129 are regent-ruled |
| Ruling clan | MMR-27's `clan:<realm uid>` ("House of {realm}"). It keeps the ruler, the regents, and every lord who is not a vassal founder. In the size-129 world every lord besides the 40 rulers founds a vassal clan, so each ruling clan holds only its ruler. Tier by fiefs (towns plus castles): 4 from 1 fief, 5 from 3, 6 from 6 |
| Vassal clans | Every living castellan, pretender or founder founds one clan of its realm and leads it. Uid `clan:<lord uid>`. Id from MMR-27's stable-id formula (`<prefix>l_<sha256("mnbl.stable-id/1\nclan\n" + uid)[:10]>`), checked to reproduce every compiler ruling-clan id and never colliding with an assigned id. Name "House of {leader}". Tier 3 for castellans and 2 for pretenders and founders. Home is the realm capital, which C04 may move to a bound fief |
| Names | Kingdom names and clan names are each unique in the world. A repeated name takes the ordinal form `{name} {n}`, counting from 2, in uid order, as the compiler does; vassal clan names also avoid the compiler's ruling clan names. The source name stays in the intent (`sourceName`). The size-129 world has two pairs of same-named realms, so `Dunmar (age 2)mark 2` and `Sirldorl (age 2)rik 2` are renamed |
| Membership | Every clan member must be a compiler lord of the clan's kingdom. Every lord of a kingdom must be in exactly one clan of it, and the lords of a dissolved realm in none. A wanderer in a clan, a lord with no clan or no kingdom, or a lord in two clans refuses the build |
| Zero-town realm | Dissolved: no kingdom or clan intent. The record lists, for C03 and C04: all lords of the realm (each affiliated to nothing), its castles (C04 must rebind or refuse them; villages follow their bound castle) and its compiler clans (which nothing may reference), with the reason. The `refuse` setting fails instead. The pinned worlds have none, because MMR-27 refuses a capital outside the towns and MMR-28 never removes a town |
| Resources | `Clan.Deserialize` reads `tier` and sets renown from it (`CalculateInitialRenown`, installed IL), so tier is the resource set here. Gold and influence follow the installed new-game logic once C03 instantiates the heroes |

**Size-129 world:**
- 40 kingdoms, 40 ruling clans and 52 vassal clans (35 pretenders, 16 castellans, 1 founder), covering all 92 lords exactly once.
- Tiers: 36 clans at tier 2, 16 at tier 3, 32 at tier 4 and 8 at tier 5.

## Affiliations: what C03 must write

The engine does not take clan membership from Factions.xml alone (installed IL):
- `Kingdom.Deserialize` sets `RulingClan = owner.Clan`. The owner's clan is read at Kingdoms time from the ruler's Heroes.xml `faction`. Nothing in Kingdoms.xml names the ruling clan.
- `Clan.Deserialize` calls `SetLeader(hero)`, which sets `hero.Clan` to the clan it leads.

The compiler's `hero.clan` still names the ruling clan for all 52 vassal founders, so it is not what C03 writes. Instead the factory emits `affiliations` (92 at size 129): every lord's clan and kingdom, or null for a lord of a dissolved realm. C03 writes `faction="Faction.<clan>"` from it. Every ruler is affiliated to its ruling clan, so the engine's ruling clan is the intent's.

## Cultures

Every source civilization presents through one installed main culture (`worldgen/policies/culture-presentations.json`, MMR-27).
- **Checks:** a kingdom's culture must be its civilization's presentation. Every ruling and vassal clan's culture must be an installed main culture with a complete template. An overridden or invalid culture refuses the build; nothing falls back.
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

**Colours.** Each kingdom gets an ordered pair of distinct colours from the installed 229-colour palette in Native's `banner_icons.xml`: the background colour and the icon colour.
- The pair is drawn from the colors stream and is unique among kingdoms; an equal or repeated pair draws the next pair.
- `color`, `color2` and the primary and secondary banner colours are the two colours.

**Icons.**
- The kingdom's icon is one banner-stream draw.
- Each of its clans, the ruling clan first and then the vassal clans by id, carries the kingdom's colours with an icon drawn until it differs from the kingdom's and every earlier clan's.
- As in the official game, a ruling clan's banner is not its kingdom's.
- A kingdom with more banners than its culture has icons is refused.

**Uniqueness.** Colour pairs separate the kingdoms and icons separate a kingdom's own banners, so all 132 banners at size 129 are unique.

**Other attributes and texts.**
- **Policies:** each kingdom's policies are its culture's installed `default_policies`.
- **Ruler title:** the installed localized string of the first official kingdom of that culture (for example `{=FmbgPGBL}Grand Prince`). All realms presented through one culture share it; for instance the 7 empire-presented realms all use "Senator". This reuses a string, not an official kingdom.
- **Texts:** the encyclopedia text of kingdoms and clans comes from the policy templates.
  - All names and texts are `{=<id>}text`; the compiler's own `{=<id>}name` strings are rebuilt the same way.
  - Text is cleaned first. Control characters, characters XML forbids (U+FFFE, U+FFFF) and unpaired surrogates are dropped; a well-formed surrogate pair is kept. Braces become parentheses.
  - Values are substituted in one pass, and XML is escaped by the writer. The tests show the XML reparses and writes with a strict `XmlWriter`.
- **Attributes emitted:** only the attributes `Kingdom.Deserialize` and `Clan.Deserialize` read. `flag_mesh` and `settlement_banner_mesh` are not read, so they are not written.

## Diplomacy

The source carries no realm relations, so the diplomacy is synthesized:
- **Stance:** every kingdom pair starts neutral. No kingdom starts at war; wars are left to the campaign's own diplomacy.
- **Nothing in Kingdoms.xml:** it carries no `<relationships>`. The installed `Kingdom.Deserialize` reads only the sign of a relationship `value`:
  - below 0 calls `FactionManager.DeclareWar`, which really declares war under `DefaultDiplomacyModel`;
  - otherwise it calls `SetNeutral`;
  - no magnitude is stored, and `isAtWar` can only add a war.

  Neutral is the engine default for a pair with no stance. An earlier draft wrote values of 0 ± 20, which would have started about half of all pairs at war; see the review.
- **Ruler relations:** the rulers of each pair get a relation of 0 ± 20 from the relations stream. This is a hero relation intent (`relations[].rulerRelation`) for C03 to apply once the heroes exist.
- **Clan relations** stay at the engine default.

## Determinism and streams

Records are processed in id order (names in uid order), so reordering them changes nothing. Every synthesized value comes from a named, versioned stream: `sha256("mnbl.faction-stream/1\n" + stream + "\n" + version + "\n" + prefix + "\n" + entity + "\n" + draw)`. The streams are `banner`, `colors` and `relations`.

Ownership and membership use no stream. Re-versioning one stream changes only what it draws, and the tests check this:
- the banner stream: icons only;
- the colors stream: colours only;
- the relations stream: ruler relations only.

Colour pairs separate kingdoms, so no icon depends on a colour draw. A kingdom's icons depend only on its own clans.

Within one stream, an earlier kingdom can change a later kingdom's colours only through a colour-pair collision. None occurs at size 129, and the tests show dissolving the first kingdom changes no other banner.

The same input gives the same intents (`intentsSha256` in the evidence) and the same XML.

## What this does not prove

- **No registration in game.** The faction XML references heroes and towns that C03 and C04 instantiate, and registered closure is C07's (MMR-36). Rulers and capitals are identity intents only.
- **The engine's ruling clan and leader membership** hold only once C03 writes the affiliations above; C07 proves them.
- **The unexpected-Calradia census is MMR-36's.**
- **Fiefs:** clan homes and the vassal clans' fiefs are bound by C04.
