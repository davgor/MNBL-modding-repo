# MMR-27 independent review (2026-10-14)

Reviewer: an independent Claude subagent with no write access to the repo. It read every delivered file, checked the generator source at the pinned commit, surveyed sizes 33 to 1025, ran CTest, and probed edge cases with a scratch harness in %TEMP%\mmr27-review. The report below is verbatim; the disposition follows it.

---

Verdict: ACCEPT WITH FIXES

The ID scheme, the joins on real worlds and the evidence are sound. There are five must-fixes, each small and targeted.

**c1 (ids deterministic, unique, bounded, traceable): met.**
- I recomputed every id in Python for all 5 compiled worlds: unique, `[a-z0-9_]`, 26 chars, sorted, and the id-map validates against its schema.
- A fresh compile into %TEMP% is byte-identical to s129 and s33.
- A collision or a repeated (kind, uid) is refused, never renamed. Names do not affect ids.
- Caveats are S1 and S10 below.

**c2 (closed joins, explicit slots): partially met.**
- Joins are closed on real worlds. I rebuilt them from world.json for 129, 257, 513 and 1025 with 0 problems: town kingdom and owner, capital, kingdom towns, village to core town (core_id → site → uid), castle via core_uid, Lord clans, clan membership, notable home town, ruler and regent, and ports.
- But several unresolved references are silently accepted (M2), the heroes block is not version-pinned (M3), and one occupation is marked "resolved" with a value the installed game has no hero template for (M4).

**c3 (producer limits, no invented settlements): met, with two caveats.**
- The 24-city reading is correct against FWGC++ at the pinned commit ec5d1c1:
  - `config.cpp:261` caps settlement_count at 0..24.
  - `settlement.cpp:229-235` returns habitat packing when settings are inferred and settlement_count is not below 24.
  - `founding.cpp:103-104,170` keeps target_cities ≤ city_limit, so checking sites ≤ target_cities enforces the ceiling indirectly.
- Counts are preserved and no settlements are invented.
- I checked the minima evidence against the installed `Settlements.xml`: 307/307 villages bound; 133 Town components (57 towns, 76 castles), all owned.
- Caveat: a generator output the policy does not cover makes valid worlds fail (M1).
- Caveat: hamlets_per_core is wrongly described as not a cap (S3).

**c4 (missing names, harbour variants, nullable sovereigns, presentation): partially met.**
- The policies exist. Nullable sovereign and unmapped-civilization refusals are tested.
- The missing-town-name policy is untested (M5).
- Only 2 of the 4 harbour roles are tested, and nothing checks that all 12 registry civilizations are mapped (S11).

## Checks performed
- Read every delivered file: the compiler, stable_ids, reader additions, tests, policies, schema, docs, summarizer and evidence.
- FWGC++ catalogue checks:
  - Hero roles: all 17 kEpithets entries, plus every `p.role =` in `heroes.cpp`. All are covered.
  - Statuses: only living and legend (`heroes.cpp`, `person_state.cpp`).
  - Hamlet roles: farming and resource (`humans.cpp:1125`); harbor, "harbor + fishing", fishing and landing (`society.cpp:727`).
  - Kinds: city, hamlet, fortress.
  - Civilizations: 12 in `content_civilizations.cpp` kEntityIds, all mapped.
- Surveyed sizes 33 to 1025: role and status sets, home and realm patterns, no rural names, no core −1 fortresses, a clean city-to-realm partition.
- CTest: 7/7 pass. campaign_semantics_tests: 59/59. source_reader_tests: pass.
- Built a scratch harness in %TEMP%\mmr27-review against the Release libs to probe edge cases. It confirmed M1 and M2.
- Checked the installed catalog: `NPCCharacters`, `SPCultures`, `Settlements`.

## Must-fix
- **M1 – valid worlds rejected: a living keeper or heresiarch can have no home and no realm.**
  - In `heroes.cpp:1338-1380` the home is the nearest city of the ruin's own people, and is null when none survives. Status stays "living".
  - Both roles map to Preacher (settlement) with no withoutRealm, so `campaign_compiler.cpp:313` refuses with "neither a home city nor a realm". The harness confirmed this.
  - Fix: in `worldgen/policies/campaign-content.json:100,108`, add `"withoutRealm": "Wanderer"` to keeper and heresiarch. Add a test for each.
- **M2 – unresolved references accepted (the ticket requires validating every reference).**
  - `campaign_compiler.cpp:311-312`: a notable whose `home.uid` names an unknown city is silently moved to the realm capital (harness confirmed). Refuse `broken_join` when home_kind is non-empty and (≠ "city" or the uid is not a town). Fall back to the capital only when home is null.
  - Lines 276, 359 and 370: town, village and castle `culture_id` are never checked against humans.cultures (harness: "cul-zzz" accepted). Lines 224-225 also overwrite duplicate culture ids silently. Refuse unknown or duplicate culture ids, and refuse a culture whose civilization differs from the record's.
  - Line 295: hero `civilization_id` is never mapped. Call `presentation(p.civilization_id, "hero " + p.uid)` for instantiated heroes and emit the result.
- **M3 – the compiler consumes heroes.realms and heroes.people, but the heroes block is not pinned.**
  - `worldgen/contracts/source-compatibility.json:15-52` has no `"heroes"` entry, so version 6 is never checked and a `status:"failed"` heroes block is not reported as failed_block (`source_reader.cpp:336-357`).
  - Add `"heroes": [6]` to blocks and `"heroes"` to required_systems (lines 59-72), with a reader test.
- **M4 – harbourmaster → ShipWright is marked resolved, but the installed game has no ShipWright heroes.**
  - The installed catalog has zero ShipWright templates or heroes. All 14 ShipWright NPCCharacters are `is_hero=false` location characters (`shipwright_*`, `shipyard_worker_*`), referenced through `SPCultures shipwright=`.
  - Change `campaign-content.json:88-89` to either an occupation with installed notable templates (Merchant 55, Artisan 13), or keep ShipWright and make the occupation slot `pending` (owner MMR-32 or MMR-35) for this role.
  - Fix the claims in `docs/campaign-semantics.md:71,121` and the policy notes.
- **M5 – c4 test gap for missing town names.**
  - The `campaign_compiler.cpp:282` path is never exercised: every city in `campaign_semantics_tests.cpp:45` has a name.
  - Add a case with two nameless cities in one realm. Expect "Aster Town" and "Aster Town 2" with provenance "synthesized…". My harness got exactly that.

## Should-fix
- **S1 – the world prefix does not separate worlds.**
  - `stable_ids.hpp:6-7` and `docs/campaign-semantics.md:51` say it keeps two worlds apart, but it hashes only layoutSeed. All 5 research worlds share `mnbl_c021674d`, and each pair shares 2 to 60 ids that denote different objects.
  - Derive the prefix from the full source identity (seed plus config or identity hash), or correct the claim.
- **S2 – reeves attach to the wrong settlement.**
  - Reeves become Headman (a village-notable occupation) but are attached to the town. The source places each one at a hamlet: `presence.uid` = `hamlet-node-<n>`, 62/62 at size 129.
  - Read `presence` in `source_reader.cpp:612-620` and bind reeves to that village, falling back to the town only when it is absent.
- **S3 – producer limits.**
  - hamlets_per_core is a hard cap on generated land hamlets (`humans.cpp:1093-1100`; observed max 3 at every size, harbours come from fisheries). Enforce non-harbour-shaped hamlets per core ≤ hamlets_per_core, ignoring rows with `founded_by`, and fix `campaign-content.json:189` and `docs:101`.
  - Require `settlements.founding`. At `campaign_compiler.cpp:195-202` it is optional, so a world without it gets no producer check at all.
- **S4 – fantasy identity dropped from output.**
  - `Person.race_id` is read but never emitted. Realm `parent_race_id` and `civilization_name` are not read.
  - Emit hero `raceId` and `presentation` for MMR-32.
- **S5 – no explicit troop slots.**
  - The only troop slots are town and castle `garrison`. `culture-presentations.json:19` claims troop-tree slots exist.
  - Add pending `troopTree` / `partyTemplates` slots (kingdom or clan, owner MMR-32), or correct the note.
- **S6 – no XML-ready localized name.**
  - `nameXml` keeps braces and `nameLocalized` is not XML-escaped.
  - Emit `nameLocalizedXml = xml_escape(localized(id, name))` and test it.
  - `localized()` also drops tab while `xml_escape` keeps it; make the two consistent.
- **S7 – exclude() leaves no trace.**
  - It discards `reason` and leaves id_map and producer counts untouched.
  - Record `{id, kind, uid, reason}` in `Compiled.exclusions` and emit it.
- **S8 – a bad ordinal template hangs the compiler.**
  - At `campaign_compiler.cpp:76-79`, an ordinal template without `{n}` loops forever.
  - Validate the placeholders of each template in `load_content`.
- **S10 – some source person uids are built from ordinals.**
  - Examples: `hero-harbourmaster-coastal-23-46`, `hero-magister-college-1`. The same string appears in different worlds for different people.
  - Document this for MMR-30, or re-key harbourmasters through presence → hamlet id → hamlet uid.
- **S11 – tests.**
  - Add tests for the 'harbor' and 'landing' roles.
  - Assert the policy covers all 12 kEntityIds civilizations.
- **Minor items.**
  - The towns minimum of 1 is reasoned from references, while the MMR-18 evidence shows a 2-kingdom, 2-town boot. Say so.
  - Name ordinals follow source array order; sort by uid so names stay stable when the source renumbers.
  - The ungarrisoned-fortress culture rule can never match the generator's core −1 rows, which carry an empty `culture_id` (`humans.cpp:770-786`, player rows only). Document it.
  - Player-adopted fortresses are appended past fortress_count (`humans.cpp:1074-1077`), so such a world would be refused. Document it.

The independent verifier (`tools/summarize_campaign.py`) re-implements the compiler's rules faithfully and its 18 checks pass. It shares the compiler's interpretation, so it cannot catch policy errors such as M1, M2 or M4.

All scratch files are in %TEMP%\mmr27-review. Nothing in the repo was changed apart from the CTest output under build/worldbridge.

---

## Disposition

All 5 must-fix and all 11 should-fix items were applied in one batch, followed by one test run and one compile-and-verify run. Two items deviate from the reviewer's suggested fix, as stated below (S3 player rows, S10).

**Results after the batch:**
- `campaign_semantics_tests`: 88/88. `source_reader_tests`: 139/139.
- CTest: 7/7.
  - The first CTest run had 6/7: `process_contract` case 19 ("descendant terminated with the job") failed while the size-1025 compile was loading the CPU.
  - Re-run alone, it passed. That code is process supervision, which this ticket did not touch.
- All five research worlds compile. The independent verifier now fully re-derives every world (33, 129, 257, 513, 1025) at 19/19 checks each; before, only three were re-derived.
- Each world has its own prefix. The s129 repeat is byte-identical.

| Item | Disposition |
|---|---|
| M1 | **Fixed.** keeper and heresiarch gain `withoutRealm: Wanderer`. Confirmed in `heroes.cpp:1376-1380` that home and realm are set together, so a null home means a null realm. Test: a keeper and a heresiarch without home or realm become Wanderers. A policy copy without the rule is refused (`broken_join`) |
| M2 | **Fixed.**<br>• Any instantiated hero's home must be a generated city (kind `city`, known uid), otherwise `broken_join`. Notables fall back to the realm capital only when they have no home.<br>• Realm, city, hamlet and fortress `culture_id`s must exist. A city's culture must belong to its civilization. Duplicate culture ids are refused (`duplicate_id`).<br>• Hero civilizations are mapped and emitted as `presentation`.<br>• Tests cover 9 new refusals |
| M3 | **Fixed.**<br>• `source-compatibility.json` pins `heroes: [6]` and requires the `heroes` system.<br>• The reader fixture (`make_fixture.py`) now carries an empty heroes block.<br>• New reader cases: `heroes_failed` → failed_block, `heroes_version_7` → unsupported_version, `heroes_system_missing` → missing_system. `campaign_inputs` now fills the fixture's block |
| M4 | **Fixed.**<br>• harbourmaster → Merchant (town), and ShipWright is removed from the occupation table.<br>• Every occupation now names its installed evidence, and the loader requires it.<br>• The verifier gains `--game`: it counts is_hero/is_template NPCCharacters per occupation in the official modules, independently of the policy. All used occupations have them: Lord 584, Wanderer 82, Merchant 55, Headman 21, GangLeader 21, Preacher 16.<br>• Docs and policy notes are corrected |
| M5 | **Fixed.** Test: two nameless cities of realm Aster become "Aster Town" and "Aster Town 2", both marked synthesized |
| S1 | **Fixed.**<br>• The prefix is `sha256("mnbl.world-prefix/2\n" + sourceIdentity + "\n" + worldSha256)[0:8]`: the source identity document plus the world.json hash. Both are recorded in `id-map.json`, and the schema is updated.<br>• The five research worlds now have five prefixes (`distinctWorldPrefixes: true`).<br>• Docs state it separates worlds but is not a uniqueness proof (MMR-30) |
| S2 | **Fixed.**<br>• The reader reads `presence`.<br>• Occupations gain a settlement kind (town or village); Headman is village.<br>• Reeves attach to the hamlet they are present at: 17/62/76/89/112 at sizes 33/129/257/513/1025, all of them. The fallback to the home town is recorded in provenance.<br>• `exclude()` moves a removed village's Headman to its town |
| S3 | **Fixed, one deviation.**<br>• Land (non-harbour-shaped) hamlets per core are enforced against `hamlets_per_core` (max 3 in every world).<br>• The founding report, `fortress_count` and `hamlets_per_core` are now required.<br>• Deviation: player-adopted rows (`founded_by`) are refused (`unsupported_row`) rather than ignored. They sit past every producer limit and belong to the player faction (MMR-30); none occur in generator-only worlds. The reader reads `founded_by` |
| S4 | **Fixed.**<br>• Heroes emit `raceId` and `presentation`.<br>• Kingdoms emit `civilizationName` and `parentRaceId`, which the reader now reads.<br>• A regent takes its realm's parent race |
| S5 | **Fixed.** Kingdoms gain a pending `troopTreeAndRecruitTemplates` slot, owner MMR-32. The culture-presentations note now names the actual slots |
| S6 | **Fixed.**<br>• Records emit `nameLocalizedXml`, produced by `localized_xml()`.<br>• `xml_escape` now drops tab like `localized`.<br>• Tests cover both |
| S7 | **Fixed.**<br>• `exclusions` (id, kind, uid, reason) are recorded and emitted.<br>• `exclude()` commits only when closure holds.<br>• An excluded id stays reserved in the id map (schema note) |
| S8 | **Fixed.**<br>• `load_content` refuses unknown placeholders, unbalanced braces, and an ordinal without `{name}` and `{n}` (`invalid_template`).<br>• `fill()` is now a single pass.<br>• Tests cover the checks |
| S10 | **Documented, not re-keyed** (the reviewer offered either). Docs list the ordinal-built UIDs for MMR-30. Within a world they are unique, and the S1 prefix keeps worlds apart |
| S11 | **Fixed.** Tests cover the `harbor` and `landing` roles and assert that the culture policy covers exactly the 12 kEntityIds civilizations |
| Minor | **Fixed.**<br>• The towns minimum evidence is reworded as reasoned from references (smallest boot: 2 kingdoms, 2 towns).<br>• Names are assigned in source-UID order; the verifier now recomputes every name.<br>• The sentinel rule is documented as player-rows-only, and an ungarrisoned fortress without a culture is refused with that reason.<br>• Player-adopted fortresses are refused explicitly (see S3) |
| Verifier | It also re-derives every reference, every name and the installed-template evidence. It still shares the compiler's reading of the joins, which the unit tests and this review cover |
