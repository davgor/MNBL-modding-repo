# MMR-29 independent review and disposition (2026-10-15)

The review was done by an independent agent that was told not to modify repository files. It worked only in its own scratch directories (`build/review29-*`). The report is reproduced verbatim below, followed by the disposition of every finding. All fixes were applied in one batch, then verified in one run:
- **C++ bundle tests:** 38/38.
- **C# host tests:** 41/41 on the real bundle.
- **Validator parity:** 35/35 variants agree across worldbridge, the C# host and Python.
- **Python summary:** 23/23.
- **CTest:** 9/9.
- **Mod project:** compiles with the new verifier.

## Review (verbatim)

```
MMR-29 independent review. I found no blockers and four majors. I changed nothing in the repo: `git status` still shows the same 47 entries. All my scratch work is under `build/review29-*`: `review29-a` (a fresh 137 MB publish), `review29-work` (small tampered bundles), `review29-probe` (a C++ probe linked against `worldbridge_projection`), `review29-cs` (a C# probe built with csc against `WorldDataBundle.cs`), and `review29-stages`/`review29-stages257` (stage re-runs).

## Findings

1. **MAJOR – the C# host crashes instead of refusing some damaged bundles.**
   - Where: `src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs:243-244`. The catch filter does not cover `InvalidDataException`, which `Json` (338), `ReadDocument` (318) and `Native` (288) throw. `System.IO.InvalidDataException` derives from `SystemException`, not from `IOException`.
   - Scenario: a zero-length or non-object `DATA_READY` or `manifest.json` (a typical result of an interrupted copy or restore), a document over 64 MB, or a directory passed with a trailing `\`. In each case `Open`, `Problems` and `OpenForSave` throw an unhandled exception instead of `RejectedException`. A save load would crash rather than show the required actionable message.
   - Verified with the C# probe on copies under `build/review29-work/damaged`:
     - Empty `DATA_READY`, `null` `DATA_READY`, empty manifest and `[]` manifest all gave "Unhandled Exception: System.IO.InvalidDataException: Expected a JSON object." `OpenForSave` did the same.
     - The real bundle path with a trailing `\` gave "Unhandled Exception: … Bundle path escapes the bundle: compiled/campaign/campaign.json".
     - C++ `--inspect-bundle` reports all of these cleanly. No test covers them.
   - Fix: add `InvalidDataException` to the filter (or catch everything except fatal exceptions), trim trailing separators after `GetFullPath`, and add tests for these cases.

2. **MAJOR – republishing silently keeps old compatibility metadata.**
   - Where: `src/WorldBridge/src/bundle_writer.cpp:140-149` is the reuse path. The engine versions (262-272), required capabilities, recipe and producer fields are manifest-only; WorldContentId doesn't cover them.
   - Scenario: after a Bannerlord or War Sails patch the same world compiles to the same WorldContentId. `--bundle` then prints `published`, exits 0 with `existed:true`, and hands back the old manifest with the old `gameVersion`. The host refuses it ("built for Bannerlord …"). Republishing can never fix this because bundles are never overwritten; only deleting it by hand works, which is risky when saves reference it.
   - Verified: I published into `build/review29-a/bundles`, then published again with a runtime-lock copy set to `gameVersion` `v1.4.9.999999`. Result: `{"status":"published","existed":true,"manifestSha256":"782e4ad9…"}`, and the manifest still says `v1.4.8.119303`.
   - Fix: when the target exists, compare the staged manifest's compatibility fields (engine, requiredCapabilities, coordinates, validation) and refuse with a clear error if they differ. Alternatively, move a compatibility document into `policies/` so it becomes part of the WorldContentId.

3. **MAJOR (needs a long root) – publish can report success for a bundle nobody can read.**
   - Where: `bundle_writer.cpp:92-155` never runs `validate()` on the staging directory before the rename or on the result after it. `world_bundle.hpp:36-39` documents a 74-character root budget, but nothing enforces it. The staging name is 25 characters and the final name 64, so staging can fit under MAX_PATH while the final paths don't.
   - Verified (`LongPathsEnabled=0`, the exe is not longPathAware):
     - With a 116-character root and a 114-byte bundle path, publish returned `existed=0`. `validate()` then reported "missing file compiled/ddd…/fff.bin", and every republish failed with `bundle_conflict`.
     - For the real bundle (longest path 79 bytes), any root of 115–153 characters triggers this.
     - Separately, calling `publish()` with a description that names a wrong policy hash also sealed and published a bundle that its own `validate()` rejects, after which the slot was permanently refused.
   - Fix: enforce the root-length budget, and run `validate(staging, false)` before the rename and only rename if it passes.

4. **MAJOR – the C++ validator is much weaker than the C# host.** `--inspect-bundle` and publish's reuse decision both depend on it.
   - Where: `bundle_validator.cpp`:
     - 28: `version` is compared as text, so the string `"1"` passes.
     - 49: `stoull` on the number text accepts the string `"100"` and `100.5`.
     - There is no totals check, no sort-order or uniqueness check, and no check that inventory rows map one-to-one onto files.
     - 72: the travel-graph checks run only if the file exists.
     - 89-97: `policies` and `validation` are optional, and their names and `reportPath` are not path-checked.
     - 57-58: junctions outside the three content roots are skipped.
   - Verified: in resealed copies under `build/review29-work/variants`, C++ said valid (exit 0) and C# refused for:
     - totals wrong;
     - inventory unsorted;
     - no `policies` and no `validation`;
     - `version` as the string `"1"`;
     - `bytes` as the string `"100"`;
     - `bytes` as `100.5`;
     - an extra case-variant row `source/World.json`;
     - `travelStatus` set to `"rejected"`;
     - `reportPath` set to `C:/Windows/win.ini` (C++ hashed a file outside the bundle and accepted it);
     - a top-level junction `extra` pointing at `C:\Windows\Fonts` (C# reported "linked directory extra").
   - The reverse also happens: a duplicate `migration` key makes C++ refuse with `duplicate_key`, while C# (JavaScriptSerializer keeps the last value) and Python accept it.
   - Impact: inspect says valid for bundles the host rejects, and publish reuses such a bundle and can never replace it.
   - Fix: give `validate()` the same rules as `Verify` and refuse reparse points anywhere in the bundle. Require a canonical manifest (bytes equal to `canonical(parse())`) or make C# refuse duplicate keys, and run one shared set of test fixtures against C++, C# and Python.

5. **MINOR (design) – save binding is stricter than the content.**
   - Where: `WorldDataBundle.cs:86-91, 111-114` bind saves to `manifestSha256`, and the manifest includes `compiler.binarySha256` (`bundle_writer.cpp:84-89, 256`), which is the hash of whichever worldbridge.exe ran.
   - Verified: a fresh publish with the current exe gave the same WorldContentId `30c425ea…`. The manifest differs only in `$.compiler.binarySha256` (`81b97d…` vs `2d0a1e…`), so `manifestSha256` is `756dd7b8…` vs `782e4ad9…`. `OpenForSave` with the real bundle's identity against the regenerated store refused: "has manifest 782e4ad9…, but this campaign was saved with manifest 756dd7b8…".
   - The doc states this ("restore the exact bundle"), so I treat it as a design issue rather than a bug. Suggest binding saves to WorldContentId plus a hash of the compatibility fields only, keeping binary hashes out of the binding.

6. **MINOR – a second publisher of the same content fails.**
   - Where: `bundle_writer.cpp:140-152`.
   - Verified: with a nested publish inside the "rename" hook, the outer publish threw "Cannot publish … (error 183)" even though a valid bundle was in place. Through the CLI this means exit 2 and no diagnostics file, because the error escapes `bundle_main` before the diagnostics write.
   - Unverified: I saw one transient `MoveFileExW` error 5 that I could not reproduce in 150 later publishes. There is no retry.
   - Fix: on error 183 or 5, re-check and validate the target and treat it as existing; write diagnostics on every failure path.

7. **MINOR – the c2 tests prove less than their labels claim.**
   - `bundle_transaction_tests.cpp:110-111`: `!validate(leftover).valid` always passes for any `.staging-*` directory because the name never matches. My probe shows the staging left by a crash at "rename" is a complete bundle that validates as a candidate.
   - `:117-127`: the power-loss case only damages a file inside staging, which `list_ready` filters by name anyway. For the `file:*` and `manifest` steps the "never validates" check is trivially true.
   - Missing tests: concurrent publish, long root, a different description on reuse, and a damaged `DATA_READY` on the host side.
   - `world_bundle.hpp:17` and `docs/world-bundle.md:47` call the rename "write-through", but `MOVEFILE_WRITE_THROUGH` only affects copy-and-delete moves. A same-volume directory rename is not flushed when the call returns. This loses a rename at worst; it never exposes a partial bundle.

8. **MINOR – evidence and docs are out of date.**
   - `publish-first.json`, `publish-again.json` and `inspect.json` (00:38) predate the final edits to `bundle_writer.cpp`, `bundle_validator.cpp` and `world_bundle.hpp` (00:39). `evidence/fwg/world-bundle.json` records binary `81b97d…`; the current exe is `2d0a1e…`.
   - I re-ran with the current exe and the claims still hold (same id, inspect valid, unit tests 28/28), but the evidence should be regenerated.
   - `docs/world-bundle.md:10` points to `evidence/fwg/bundle-review.md`, which does not exist.
   - `docs/world-bundle.md:22` doesn't mention exit 2 for refusals.
   - `docs/world-bundle.md:82` "Agreement" is true only for the real bundle (see 4).

9. **MINOR – campaign instance ID patterns disagree.** `world_identity.cpp:91` accepts UUID versions 1–8 for `campaignInstanceId`, but `activation-receipt.schema.json:17` allows only `[1-5]`. A request with a UUIDv7 would be accepted, yet its activation receipt could never validate. Verified by reading both.

10. **NIT – `safe_bundle_path` does not refuse DOS device names**, despite its comment (`world_bundle.hpp:41-42`, `bundle_writer.cpp:24-35`, `docs/world-bundle.md:39`).
    - My probe published and validated `compiled/con.json`, `compiled/aux`, `compiled/COM1.bin` and `compiled/x/lpt1` on this Windows 11 machine; on Windows 10 those are device names.
    - `compiled/nul` failed with "Cannot write".
    - The producer's file names make this unlikely in practice.

11. **NIT – the schema is looser than the code.** At `bundle-manifest.schema.json:11` the path pattern accepts `source/../x`, `compiled/a?b`, trailing dots or spaces, paths over 120 bytes and device names (checked with Python `re`). `compiler.binarySha256` at line 37 is a plain string, because `exe_sha256()` can return `""`.

12. **NIT – inspect usability.** `--inspect-bundle <dir>/` with a trailing slash (what shell completion produces) reports `valid=false` "the directory name is not the worldContentId" and exits 2 (verified). `"state":"DATA_READY"` is printed even when `DATA_READY` names a different manifest.

13. **Observation, not verified as a defect (P01–P03/U03 scope):** `SaveIdentity` has no `campaignInstanceId` or receipt hash, and `OpenForSave` opens any `DATA_READY` bundle without checking for an activation receipt.

## Checked and fine

- **MMR-26/27/28 outputs are unchanged.** I re-ran `--compile-terrain`, `--compile-campaign` and `--compile-travel` for s129, and `--compile-travel` for s257, with the current exe. Every output is identical to the pre-refactor artifacts and to the bundle's `compiled/`. `terrain.json` differs only in timings and is not bundled.
- **Volatile data:** I found no absolute paths or timestamps in the bundle content.
- **Links inside content:** `world_content` refuses links inside the three roots.
- **Flushing:** all files are flushed before the rename, and verification re-reads them from disk.
- **Crash or cancel before the rename** exposes nothing.

## Per-criterion verdicts

- **c1: met, with caveats.** Bundles the compiler writes are hash-complete and sealed by `DATA_READY`, no activation path exists, and a receipt inside a bundle is refused. Caveats are findings 3 and 4.
- **c2: met.** In both the tests and my probes, staging, flushing, the atomic rename and the name filter never exposed a partial world. The tests overstate what they prove (finding 7), and power loss isn't really simulated.
- **c3: met as worded.** The manifest carries WorldContentId, and the manifest hash gives exact binding. However, the `OpenForSave` helper crashes on damaged bundles instead of refusing them (finding 1, major), and it binds to build-specific data (finding 5).
```

## Disposition

| # | Disposition | What changed | Verified by |
|---|---|---|---|
| 1 | **Fixed** | `WorldDataBundle.Verify`:<br>• catches every non-fatal exception, so problems are reported and never thrown;<br>• trims trailing separators after `GetFullPath`;<br>• reads JSON through a strict reader. | C# tests: an empty `DATA_READY`, a `[]` manifest, a trailing separator, and `OpenForSave` with an empty `DATA_READY` (refused, not crashed). Parity variants "an empty DATA_READY" and "a manifest that is not an object": all three validators refuse, and none crashes. |
| 2 | **Fixed (both suggestions)** | • **Compatibility in the identity:** new `compiled/compatibility.json` (game and War Sails versions, required capabilities), so the WorldContentId covers compatibility.<br>• **Reuse rule:** a bundle is reused only if its manifest equals the new one apart from the provenance members (`source.recipe`, `source.producer`, `compiler`, `engine.runtimeLockSha256`). Otherwise `bundle_conflict` names the differing members. | CLI: a runtime lock naming `v1.4.9.999999` published a new bundle (`e35c500c…`, `existed: false`) instead of reusing `f596b58c…`. Unit tests: reuse with only provenance differing is accepted; reuse with a different `retention` is refused, naming it. |
| 3 | **Fixed** | • `check_root_budget` refuses a root whose files would exceed 259 characters, or whose directories would exceed 247, under `<root>\<id>` (`path_too_long`), before anything is written.<br>• `publish` validates the staging directory as a candidate after `DATA_READY` and before reuse or rename (`staging_invalid`, new step `validated`). | Unit tests: a 200-character root is refused and nothing is created; a wrong policy hash in the description is refused with nothing published. CLI: a deep root exits 2, creates no directory, and the failure is recorded in the diagnostics. |
| 4 | **Fixed** | `validate()` rewritten with the C# rules:<br>• numeric version and integer byte counts;<br>• strictly sorted unique rows with safe paths, and totals;<br>• exact on-disk names (a case-variant row is a missing file);<br>• reparse points refused anywhere, and hard links refused;<br>• required files (the documented layout);<br>• `policies` and `validation` required and path-checked;<br>• the id recomputed from the verified rows;<br>• compatibility.json equal to the manifest.<br>The C# side gained a strict JSON reader that refuses repeated keys, single quotes, NaN and a BOM. Python got the same rules (`verify_bundle`).<br>Not adopted: a canonical-manifest requirement (C# has no canonicalizer). Instead all three accept any strict JSON, and a pretty-printed resealed manifest is a parity variant that all three accept. | `tools/test_bundle_parity.py` runs every case the reviewer listed, plus the BOM, NaN, single-quote, device-name, long-path, required-file, compatibility, link and rename cases: 35 variants, all three validators agree, each for the intended reason (`evidence/fwg/bundle-parity.json`). |
| 5 | **Fixed (as suggested)** | Saves bind to the WorldContentId alone (`mnbl.world-bundle-binding.v1`). It now covers compatibility (see 2), and the manifest hash, which carries provenance, is out of the binding. | C# tests: a resealed manifest with another compiler binary hash still resolves for the save; a save naming other content never resolves. |
| 6 | **Fixed** | • A failed rename re-checks the target. If it now exists, it goes through the reuse rule (a concurrent winner is reused).<br>• Access or sharing denials are retried up to 6 times with backoff.<br>• `bundle_main` writes diagnostics for every failure before rethrowing. | Unit test: a nested publish inside the `rename` hook leads the outer publish to reuse the winner's bundle, with no staging left. CLI: a failure leaves a `status: failed` diagnostics file. The transient error 5 was not reproduced, by the reviewer or by me; the retry is defensive. |
| 7 | **Fixed** | • The crash check now asserts that the staging left behind is an *incomplete* candidate before `DATA_READY` and a *complete* one after it (`ready`, `validated`, `rename`), never under a bundle name and never listed.<br>• Power loss is tested at the heightfield file, `manifest`, `ready`, `validated` and `rename`. After `DATA_READY`, the non-trivial cases show a torn candidate never validates.<br>• New: a bundle torn after the rename survives is listed as sealed but never validates.<br>• New: concurrent publish, long root, a description conflict on reuse, staging validation, and an empty `DATA_READY` (C++ and C#).<br>• The "write-through" wording is corrected in the header and docs: the rename is atomic and journaled but not flushed when the call returns. | `evidence/fwg/bundle-tests.txt` (38/38). |
| 8 | **Fixed** | • Evidence regenerated with the final binary: real bundle `f596b58c…` (889 files), publish, republish and inspect outputs, `world-bundle.json` 23/23, `bundle-parity.json`, `bundle-tests.txt`, `bundle-ctest.log`.<br>• This file now exists.<br>• The docs cover exit 2 and diagnostics, and the agreement claim rests on the parity run. | — |
| 9 | **Fixed** | The activation receipt's `campaignInstanceId` accepts UUID versions 1–8, as `valid_uuid` does. | Read both. |
| 10 | **Fixed** | `safe_bundle_path`, the C# `SafePath` and the Python `safe_path` refuse the DOS device names CON, PRN, AUX, NUL, COM1–9 and LPT1–9, case-insensitive, with or without an extension. | Unit tests (`compiled/con.json`, `compiled/x/LPT1`, `compiled/Aux`); parity variant "a DOS device name, consistent bundle". |
| 11 | **Fixed** | • The schema path pattern mirrors `safe_bundle_path`: three roots, segments, reserved characters, trailing dot or space, device names, at most 120 characters (the code counts bytes; equal for ASCII). It was checked against `safe_path` on 19 samples.<br>• `compiler.binarySha256` is a SHA-256; `exe_sha256` now refuses rather than returning "". | Schema validation in `summarize_bundle.py` (the real manifest validates). |
| 12 | **Fixed** | • `validate` normalizes the path, so a trailing separator names the same directory.<br>• `inspect` reports `state: DATA_READY` only for a valid bundle; otherwise `sealed-but-invalid` or `unsealed`. | Unit test (trailing separator); parity variant "a trailing separator". |
| 13 | **Deferred (owned)** | The campaign-instance binding and the check for an activation receipt at load belong to U03 and P01–P03. The docs say so. | — |

Criteria after the fixes:
- **c1:** the caveats (3, 4) are resolved.
- **c2:** the tests now prove what their labels claim, including a tear after DATA_READY and after the rename.
- **c3:** the binding is the WorldContentId, which covers compatibility. Damaged bundles are refused, not crashed on.
