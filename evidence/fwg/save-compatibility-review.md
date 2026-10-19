# MMR-50 independent review and fixes

One independent review of the MMR-50 working tree. The reviewer was an agent with read-only access. It did the following without editing files or launching the game:
- read the policy, the loader it must agree with, the probe and the installed IL;
- ran the offline harnesses (Test-Compatibility 40/40, Test-SaveBinding 77/77, Test-WorldLoader 42/42);
- ran a Test-Recovery check and the inspector on the real store;
- compiled the module.

The first review attempt failed before it started: the API refused the subagent (`oauth_org_not_allowed`). The retry ran.

It reported 1 blocker, 4 major and 11 minor findings. All were fixed in one batch, then:
- the offline harnesses ran: Test-Compatibility 56/56, Recovery 10/10, Test-WorldDataBundle 41/41 managed plus 38/38 C++ bundle transaction checks, and the rest green;
- one in-game confirming run followed on the fixed build: `compat_matrix` 24328, 13/13 recomputed checks, status ok.

## Blocker

1. **No passing in-game evidence for the current code.** Run 31360 ran before a fix to the probe. Its fixtures ran inside the still-running campaign, because the probe did not drive nested enumerators, so `EndGame` never ran. In that run, report codes and loader codes matched and every byte was preserved. Fixed:
   - the probe runs its body through `TwoWorldMatrixProbe.Nested`;
   - runs 43648 and, after this batch, 24328 passed;
   - the offline evidence is written (`evidence/persistence/compatibility-tests.json`, `.txt`);
   - the docs are filled in.

## Major

2. **Test-Recovery failed.** The inspector's "loadable" is now the policy's verdict, and the test's synthetic save had no `ApplicationVersion`, no game data and a `world.json` that did not hash to its binding. Fixed: the fixture is a real one, with a header, whole data and a manifest that verifies, including scene, seed, scene files and documents. 10/10.
3. **Test-WorldDataBundle asserted the removed "migration tool" advice.** Fixed:
   - the test now asserts that the refusal says there is no migration and offers no tool;
   - the ticket id was dropped from the text players see.

   The producer's manifest `migration` field ("a newer bundle schema needs an explicit migration tool") stays as written: it is baked into every published bundle. It says a tool would be needed, not that one exists, and rewording it would make republished bundles describe the same content differently.
4. **A swept scene was reported rebuildable whatever the build.** The scene name includes the encoder's identity (encoder binary, template, water and settlement versions, entity contract, navmesh), and none of that is in the build keys. A module update could re-encode differently, and the loader then refuses (not-rebuildable). Fixed:
   - a scene rebuild is tested only when the save's module, assembly and backend equal the running build's;
   - otherwise the row is marked `conditional`, and the report says the re-encoding is verified at load and refused if it differs;
   - a save without build keys is conditional too.

   Confirming run 24328 shows a conditional rebuild of a save made before MMR-48 being verified and loading.
5. **Game data another program held was reported supported, while the loader refused it as damaged.** Fixed: an unreadable body (`BodyWhole` null) is `save-unavailable` in both the policy and the loader. A from-files test holds the save open for writing.

## Minor

6. **Precedence of the instance check.** It now comes after the build check, as in the loader. A null instance renders as "(none)", and the naval wording carries LoadPolicy's guard.
7. **Loader checks the policy did not make.** Added:
   - the header's scene and seed against the package (damaged-save);
   - a manifest that hashes right but lacks scene, seed, scene files or documents (damaged-world);
   - a settlement layout of another world (not-rebuildable).

   The navigation and playability gates and the campaign rebuild are labelled as verified at load.
8. **Deferred re-encode.** Documented as a limit. A swept scene loaded from inside another running compiled campaign is encoded after the running game ends (MMR-49), and that path is not exercised in game.
9. **The settlement layout's source.** With a swept scene the report now says re-derived by the encoder, which is what the loader does. The summarizer compares the report's restored and re-derived reports with the load's.
10. **Locked world files threw out of `Gather`.** They are now a fact: a world file another program holds refuses as `load-failed`, as the loader's unexpected-error refusal does. A from-files test covers it.
11. **Saves from before MMR-48 skipped every fingerprint.** Their header's game version (`ApplicationVersion`) and War Sails version (`Module_NavalDLC`, absent when the save was made without it) are now compared, by the policy and the loader alike (`LoadPolicy.LegacyBuild`, `LegacyCompatibility`). `Inspect-World.ps1` reads the game version from the runtime lock instead of a constant.
12. **Only one previous-save format.** Added the real current-format header from run 43648 (`mmr50-current.header.json`, every build key).
13. **Save-ID evidence.** The rescan records base ids only; the module defines no saveable type. The docs require recording each definer's local-id span before a first id is assigned.
14. **War Sails installed against active.** The running build's naval key is now "absent" unless War Sails is active (`ModuleHelper.IsModuleActive`). The in-game missing-DLC fixture tests a War Sails version that is not installed; absence is tested offline, by the policy and the loader.
15. **Stale migration wording.** Fixed in `docs/world-bundle.md`. The scan covers every message literal of the module.
16. **The rebuild's own byte preservation.** The probe now hashes the world's bundle and package before the sweep, and checks them after the rebuildable load as well as after the refusals.

Out of scope, noted by the reviewer: MMR-49's world-B prisoner check accepts an escape. That was recorded and accepted in MMR-49's run 48048.

## Results

**In game: compat_matrix 24328, 13/13 recomputed checks, status ok** (`evidence/persistence/compat-matrix.json`).
- **The source save** (`mnbl_1609fb72_start_a762a2d366bc`, made before MMR-48) is reported supported. Once its world's scene and reports are swept, it is reported rebuildable derived cache: scene conditional, navigation and playability restored, layout re-derived.
- **The load agrees with the report.** It went through the world loader, with the surface encoder as the only process started. It restored exactly the reports the report named and re-derived the layout it named, and the bundle and package bytes were unchanged.
- **A save in this build's format,** with every build key, is reported supported.
- **The four fixtures** made from it (another binding contract, another save id version, a War Sails version that is not installed, a corrupted package hash) were each reported unsupported. Each was refused by the load with the same code (`incompatible-build` ×3, `damaged-world`) before any game started. The installed load never ran, the caller's cancel ran, and every byte of the fixtures, the source saves, the bundle and the package was preserved.
- **Baselines** held at the start, after the load and save, and after the refusals.

**Offline:**
- Test-Compatibility 56/56;
- Test-WorldLoader 42/42;
- Test-SaveBinding 77/77;
- Recovery 10/10;
- Test-WorldDataBundle 41/41, plus `bundle_transaction_tests` 38/38: a republish under new provenance reuses the bundle unchanged, and a redescribed or damaged one is refused and left untouched;
- the full batch (see the submission).

**The real store** (`tools/Inspect-World.ps1`): 46 saves bound, all supported.

## Limits

- **Cross-build re-encoding.** A swept scene under another module build is reported conditional, not predicted: whether that build's encoder reproduces the package's scene is known only by encoding it, which the load does and verifies.
- **The in-game missing-DLC fixture** names a War Sails version that is not installed. A game with War Sails absent or inactive is tested offline, by the policy and the loader's `LoadPolicy.Compatibility` and `LegacyCompatibility`.
