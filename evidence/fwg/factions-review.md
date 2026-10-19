# MMR-31 independent review (2026-10-15)

Reviewer: an independent read-only agent (no repository edits, no game launch). It re-ran `tools/Test-Factions.ps1` (24/24 at the time) and reproduced the recorded hashes with a harness of its own, then checked the installed IL with ildasm.

Below is the report verbatim, followed by the disposition of every finding. All findings were fixed in one batch, after which `tools/Test-Factions.ps1` passed 29/29 (`evidence/fwg/factions-tests.txt`) and the mod compiled (MSBuild, exit 0).

---

MMR-31 independent review. No repository files were changed. My scratch work is in %TEMP%\mmr31-review-il (ildasm output of the installed IL) and %TEMP%\mmr31-review-harness (my own harness, Review.cs, plus its XML and intents output). I ran tools/Test-Factions.ps1 on the given bundle: 24/24 checks pass, and it reproduces the intentsSha256 (cb110611…) and XML hashes recorded in evidence/fwg/factions.json.

**Bottom line:** one blocker. About half of all kingdom pairs would start at war in the engine, while the policy, docs, evidence and tests all say no kingdom starts at war.

## Findings

**1. BLOCKER: negative relationship values declare war.**
- **Where:** FactionFactory.cs:219-228 (value = 0 ± 20, AtWar=false) and 293-295 (writes `value` and `isAtWar="false"`). The same claim is made in make_factions_policy.py:87-91, docs/factions.md:52-57, FactionTests.cs:131-132, and evidence `relations.atWar=0`.
- **Defect:** the installed `Kingdom.Deserialize` (IL_0373–IL_03d6) only looks at the sign of `value`. If `value < 0` it calls `FactionManager.DeclareWar`; otherwise it calls `SetNeutral`. `isAtWar` can only add a war, never cancel one. The magnitude is thrown away; no relation number is stored.
- **Why it really becomes war:** `DeclareWar` calls `DiplomacyModel.GetShallowDiplomaticStance`. In `DefaultDiplomacyModel` that returns null for two non-bandit factions, so `SetStance(...,War)` runs. NavalDLC does not override it. The official XML uses exactly this convention: `value="-1" isAtWar="true"`.
- **Failure scenario:** in the size-129 world, 377 of 780 pairs are negative, so every kingdom starts at war with 13–23 others. The "0 ± 20 relation" itself has no effect in game.
- **How verified:** ildasm of Kingdom.Deserialize, FactionManager.DeclareWar and DefaultDiplomacyModel.GetShallowDiplomaticStance, plus a count over the generated relations in my harness.
- **Suggested fix:** emit no negative value (omit `<relationships>`, or write 0 / a non-negative value). If relation magnitudes are wanted, apply them after registration between rulers (C03/C07). Change the test to assert that no written value is below 0 unless a war is intended.

**2. MAJOR: duplicate kingdom names in the pinned world.**
- **Where:** FactionFactory.cs:137 (Name is taken as-is) and 210-218 (only clans are de-duplicated).
- **Defect:** two pairs of realms share a name: "Dunmar (age 2)mark" (k_182d59cd0c, k_de9f25ee81) and "Sirldorl (age 2)rik" (k_7217a84b0e, k_d2af303495). The compiler already added ordinals to their ruling clans ("… 2"), but the kingdoms are still identical in the encyclopedia, diplomacy and war notifications.
- **How verified:** my harness on the real bundle. An injected same-name pair is also accepted without complaint.
- **Suggested fix:** give display names an ordinal suffix, the way the compiler does for towns, and keep the source label in the intent.

**3. MINOR: clan banners are not distinct.**
- **Where:** FactionFactory.cs:179, 194, 253-256 and docs/factions.md:46 ("the clan's own culture icon").
- **Defect:** clan banners are the kingdom's colours plus one icon draw, with no uniqueness check.
- **Failure scenario:** in the pinned world, 3 pairs of same-kingdom clans have identical banners (l_998c1993cf/l_a80727fc08, l_fbd540327f/l_440980b2c7, l_79447272af/l_f029e69bdc). 4 clans (3 ruling, 1 vassal) have exactly their kingdom's banner. Those parties can't be told apart on the map.
- **How verified:** harness.
- **Suggested fix:** redraw for uniqueness within the world, or at least within the kingdom.

**4. MINOR: the in-engine ruling clan depends on an unstated C03 requirement.**
- **Where:** FactionFactory.cs:290 and docs/factions.md:72.
- **Defect, from the IL:**
  - `Kingdom.Deserialize` sets `RulingClan = owner.Clan`, read from the ruler's Heroes.xml `faction` (C03) at Kingdoms time. Nothing in Kingdoms.xml carries `RulingClanId`.
  - `Clan.Deserialize` → `SetLeader(hero)` calls `hero.set_Clan(this)`. That is what moves the 52 vassal leaders out of the ruling clan; their compiler `hero.clan` still points at the ruling clan (52 of 52), and so does `RegistrationPlan.FromCompilerRecords`.
- **Current state:** it works today only because the compiler's `hero.clan` for every ruler equals rulingClan (verified 40/40).
- **Suggested fix:** document both facts for C03/C07. Optionally emit a hero→clan map with the intents.

**5. MINOR: clan membership closure is weaker than the code comment claims.**
- **Where:** FactionFactory.cs:172-173 and 199-209. The comment at :199 says members are "lords of its kingdom".
- **Defects:**
  - No occupation check on members: an injected Wanderer is accepted into a ruling clan.
  - A member with no `kingdom` field passes (`!= null &&`).
  - A lord missing from its clan's `members` silently gets no clan. Only the test checks that all 92 lords are covered.
- **How verified:** harness experiments and code reading.
- **Suggested fix:** require occupation Lord and a matching kingdom, and check in the factory that every lord appears in exactly one clan.

**6. MINOR: clan cultures are not validated.**
- **Where:** FactionFactory.cs:177 and :191.
- **Defect:**
  - A ruling clan's `presentation` is emitted without the `required()` check: an injected `looters` becomes `Culture.looters` on a noble clan.
  - A vassal leader's invalid presentation silently falls back to the kingdom's culture.
  - Kingdom culture vs civilization presentation is only checked in the test, not in the factory. An overridden presentation is accepted.
- **How verified:** harness.
- **Suggested fix:** refuse instead of falling back.

**7. MINOR: the setter inventory doesn't match the installed assembly.**
- **Where:** tools/Scan-CultureContract.ps1:28-33 (`Access` queries `$cultureType`), and contract.json.
- **Defect:** 12 BasicCultureObject members are recorded as `setter: none`, but they have private setters. Querying a base-class property through the derived type hides its private accessors. Affected: color, color2, cloth_alternative_color1/2, banner_background/foreground_color1/2, is_main_culture, faction_banner_key, is_bandit, can_have_settlement.
- **Doc mismatch:** docs/factions.md:37 says "100 of the 108", but contract.json summary says 105.
- **Gap:** the ticket asks for constructor/initialization paths; the contract doesn't inventory them.
- **How verified:** reflection on TaleWorlds.Core and CampaignSystem.
- **Suggested fix:** use `prop.DeclaringType.GetProperty(...)`.

**8. MINOR: the "complete culture template" check can't fail.**
- **Where:** Scan-CultureContract.ps1:80, FactionFactory.cs:104, FactionTests.cs:85-87.
- **Defect:** "required" means "set by every installed main culture", so `requiredMissing` is empty for every main culture by construction. The c2 check and test can never fail. The real guarantee comes from reusing the installed cultures unchanged.
- **What does hold:** the member coverage is correct. My diff of the IL strings of CultureObject/BasicCultureObject.Deserialize against contract.json shows no member missing. The 6 naval members are set for all 7 main cultures.

**9. MINOR: the banner stream isn't fully independent.**
- **Where:** FactionFactory.cs:157-165; claim in docs/factions.md:61-66.
- **Defect:** the uniqueness redraw links the colors stream, and the set of earlier kingdoms, to which icons get drawn.
- **Failure scenario:** with a 2-colour palette, re-versioning only `colors` changes 4 kingdom icons, and dissolving one kingdom changes 2 other kingdoms' banners.
- **Scope:** with the real 229-colour palette there is no effect on this world, so it is latent. The result is still deterministic and doesn't depend on record order. No hash-set or dictionary-order dependence in the outputs.
- **How verified:** harness.

**10. MINOR: invalid XML characters get through.**
- **Where:** FactionFactory.cs:322-326.
- **Defect:** only `Char.IsControl` is stripped. U+FFFE, or a lone surrogate (legal in JSON as `\ud800`), passes through: OuterXml no longer reparses, and a checking XmlWriter throws.
- **Also:** compiler `nameLocalized` values (kingdom name/short_name, ruling clan name) are used verbatim with no sanitizing.
- **How verified:** harness.
- **Suggested fix:** filter with `XmlConvert.IsXmlChar` plus surrogate-pair handling.

**11. MINOR (cannot happen in the pinned worlds): the zero-town dissolution can't be acted on.**
- **Where:** FactionFactory.cs:127-134.
- **Defect:** the record lists only the ruling clan's members. The lords' compiler `clan`/`kingdom` fields still name the dropped clan and kingdom. C03's Heroes.xml would then point `faction` at a clan that is never defined, leaving a presumed, not-ready clan. Castles and villages of such a realm aren't addressed.
- **Suggested fix:** emit orphan rebinding (lords and fiefs) for C03/C04.

**12. NIT: clan-name de-duplication can create a new duplicate.**
- **Where:** FactionFactory.cs:210-218.
- **Failure scenario:** two vassal leaders named "Ann" plus one named "Ann 2" ends with two clans called "House of Ann 2". It can also collide with names the compiler already suffixed.

**13. NIT: policy robustness.**
- FactionFactory.cs:158 loops forever if the palette has 1 colour.
- An empty icon pool or empty palette throws DivideByZero (`ulong % 0`) at :162/:255 instead of a FactionException.
- Nord icons 165–174 exist only in NavalDLC's banner_icons.xml. make_factions_policy.py pins only Native's file, and nothing checks icon ids against an installed banner_icons.xml.

**14. NIT: tests and docs.**
- docs/factions.md:14 points to evidence/fwg/factions-review.md, which doesn't exist.
- FactionTests.cs:67 has dead code: `.Sum(c => 0)`.
- The test receipt doesn't hash-pin worldgen/templates/cultures/*.json.
- The ruler title is the first official kingdom's string per culture, so all 7 empire-presented realms get "Senator". That's a string reuse, not copied geopolitics. Nothing Calradia-geopolitical is copied.

## Checked and found correct
- Every emitted Kingdom/Faction attribute is one the installed Deserialize methods read.
- Colours parse via `Convert.ToUInt32(s,16)`, which accepts both `0x…` and bare hex.
- The banner format is 10 ints per layer, max 32 layers. Background mesh 11 is a pattern, and the palette ids are valid.
- `policy_coastal_guard_edict` (NavalDLC) is registered in `InitializeSubModuleGameObjects`, before the XML loads.
- The stable-id formula matches stable_ids.cpp (prefix, kind code `l`, 10 hex), and collisions are refused just as the compiler refuses them.
- Within the Kingdoms phase, relationship elements reference later kingdoms as presumed objects. The official XML does the same. MMR-30's forward-reference rule doesn't see them because it scans only top-level attributes.

## Per-criterion verdicts
- **c1: Met at the intent level.** The code (:143-147) and the test check that all 40 kingdoms name a compiler ruler (lord, 8 of them regents), a ruling clan the ruler leads, and a capital among the kingdom's towns. Caveat: in the engine, the ruling clan comes from C03's ruler `Hero.faction` (finding 4).
- **c2: Substantially met, with minor defects.** All 10 civilizations present through installed main cultures. All Deserialize members and the 6 naval members are covered. But the setter inventory is wrong for 12 members, the completeness check is tautological, and ruling/vassal clan cultures aren't validated.
- **c3: Met for reproducibility.** Two builds give the same intents and XML, reordering records changes nothing, and my independent harness reproduces the evidence hashes. Stream independence holds only when there are no banner collisions (finding 9). The relationships it reproduces start ~377 wars, which breaks the diplomacy default (finding 1). That has to be fixed before acceptance, even though c3 itself only asks for determinism.

---

## Disposition

| # | Severity | Disposition |
|---|---|---|
| 1 | Blocker | **Fixed.** Kingdoms.xml carries no `<relationships>`. Every pair starts neutral, the engine default. I confirmed in the installed IL that Deserialize loops over child nodes by name, so leaving the element out is safe. The 0 ± 20 value is now a ruler-to-ruler relation intent (`relations[].rulerRelation`, relations stream) for C03 to apply once the heroes exist. The policy (`diplomacy.stance`, `stanceRule`, `rulerRelation`), the docs, the tests (no `relationship` element, all 780 pairs neutral) and the evidence (`diplomacy.relationshipElements` = 0) were all updated |
| 2 | Major | **Fixed.** Kingdom names are unique in uid order using the policy ordinal `{name} {n}` (`names.ordinal`, same as the compiler). The source name is kept as `sourceName`. In the pinned world the two renamed realms are `Dunmar (age 2)mark 2` and `Sirldorl (age 2)rik 2`; this is in the evidence (`renamedKingdoms`) |
| 3 | Minor | **Fixed.** Colour pairs are unique among kingdoms. Each clan's icon differs from its kingdom's and from every earlier clan's in that kingdom, so all 132 banners are unique and no clan banner equals its kingdom's. The official game gives ruling clans banners different from their kingdoms', which I checked in the catalog. A kingdom with more banners than icons is refused |
| 4 | Minor | **Fixed.** The factory emits `affiliations`, a lord-to-clan and lord-to-kingdom map for C03's Heroes.xml `faction`. Every ruler is in its ruling clan and every vassal founder in its own clan, which the tests check. Both engine facts are documented under "Affiliations: what C03 must write", and the gap is listed under "What this does not prove" |
| 5 | Minor | **Fixed.** Members must be lords of the clan's kingdom (occupation and non-null matching kingdom). The factory checks that every lord of a kingdom is in exactly one clan, and that lords of a dissolved realm are in none. The tests show a wanderer in a clan and a lord in no clan are each refused |
| 6 | Minor | **Fixed.** The kingdom culture must equal its civilization's presentation. Ruling and vassal clan cultures must be complete installed templates. There is no fallback: each case is refused, and each is tested |
| 7 | Minor | **Fixed.** Setters are now read on the declaring type: 100 private, 5 get-only, 3 public (`militia_bonus`, `prosperity_bonus`, `encounter_background_mesh`). Contract v2 adds each member's writers (IL call sites of its setter; only `CultureObject.Deserialize` or `BasicCultureObject.Deserialize`) and both constructors (public, no parameters). Docs numbers corrected. The culture templates are unchanged byte for byte |
| 8 | Minor | **Fixed (reframed).** The contract and docs now say `requiredMissing` is empty by construction for installed main cultures, and that the guarantee is reusing them unchanged. The check guards other templates, and a test shows an injected `requiredMissing` is refused. The test also asserts that the world defines no culture (only Kingdoms and Factions documents) |
| 9 | Minor | **Fixed.** Icons no longer depend on colours: colour pairs separate kingdoms, and a kingdom's icons depend only on its own clans. Within one stream, an earlier kingdom can change a later one's colours only through a colour-pair collision; this is documented. The test dissolves the first kingdom by id and shows no other banner changes |
| 10 | Minor | **Fixed.** `Clean` drops control characters, non-XML characters and lone surrogates, and keeps well-formed pairs. It is applied to every name and text, and the compiler's `{=id}name` strings are rebuilt through it. The test checks reparse plus a strict `XmlWriter` with U+0007, U+FFFE, a lone U+D800 and a kept U+1F451 |
| 11 | Minor | **Fixed.** The dissolution record lists all lords of the realm (affiliation null), its castles (C04 must rebind or refuse them) and its compiler clans (which nothing may reference). Documented and tested |
| 12 | Nit | **Fixed.** Clan names go through the same `UniqueNames` as the compiler, seeded with the ruling clan names. The Ann/Ann/Ann 2 case gives three distinct names (`House of Ann`, `House of Ann 2`, `House of Ann 2 2`) |
| 13 | Nit | **Fixed.** A palette with fewer than two colours, too few unique colour pairs, an empty or too small icon pool, and an ordinal without `{n}` are each refused with a `FactionException` and tested; an empty draw pool also throws `FactionException`. `make_factions_policy.py` checks every culture icon id and the background mesh against all installed `banner_icons.xml` files (Native, NavalDLC) and pins both hashes |
| 14 | Nit | **Fixed.** This file now exists, the dead code is removed, and the receipt hash-pins every culture template and the contract. The shared ruler title per presentation is documented |

**Criteria after the fixes:**
- **c1 met.** Kingdom intents resolve in the compiler graph. The affiliations give C03 what it needs for the engine's ruling clan.
- **c2 met.** The presentation is exhaustive, clan cultures are validated, and the contract inventory is correct and includes writers and constructors.
- **c3 met.** The output is deterministic, each stream changes only what it draws, and no kingdom starts at war.
