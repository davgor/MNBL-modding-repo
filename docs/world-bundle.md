# World data bundles (MMR-29, 2026-10-15)

`worldbridge --bundle` packages one world's compiler outputs into an immutable, content-addressed directory: the producer split, the compiled terrain, campaign records and travel graph, the compatibility the world needs, and every policy that shaped it. The bundle is sealed with `DATA_READY` after static checks. **DATA_READY is not activation**: the host may read the bundle to construct a scene, but only U03 may activate it, by writing an activation receipt outside the bundle after W02–W05 and C07 pass. Publication is not campaign commit.

| | |
|---|---|
| Code | `src/WorldBridge/src/bundle_writer.cpp` (build and publish), `src/bundle_validator.cpp` (validate, list), `commands/inspect_bundle.cpp`, `include/worldbridge/world_bundle.hpp`. Host: `src/MNBL.ProceduralWorld/CampaignBootstrap/WorldDataBundle.cs` |
| Contracts | `worldgen/contracts/bundle-manifest.schema.json` (`mnbl.world-bundle` v1), `worldgen/contracts/activation-receipt.schema.json` (`mnbl.activation-receipt` v1; defined here, written only by U03) |
| Tests | `src/WorldBridge/tests/bundle_transaction_tests.cpp` (CTest `bundle`, 38 checks; `evidence/fwg/bundle-tests.txt`; CTest 9/9 in `evidence/fwg/bundle-ctest.log`). `tests/WorldDataBundleTests.cs` through `tools/Test-WorldDataBundle.ps1` (41 checks on the real bundle). `tools/test_bundle_parity.py` (35 damaged variants through all three validators; `evidence/fwg/bundle-parity.json`) |
| Evidence | `evidence/fwg/world-bundle.json` (`tools/summarize_bundle.py`, 23 checks): a third, Python verification of the real size-129 bundle against the schema and against the MMR-26/27/28 outputs compiled separately. Review and disposition: `evidence/fwg/bundle-review.md` |

```
worldbridge --bundle SPLIT --contract worldgen/contracts/source-compatibility.json \
  --projection-policy worldgen/policies/projection.json --materials worldgen/policies/terrain-materials.json \
  --import worldgen/calibration/terrain-import/terrain-import.json --content worldgen/policies/campaign-content.json \
  --cultures worldgen/policies/culture-presentations.json --playability worldgen/policies/playability.json \
  --producer-lock worldgen/producer.lock.json --runtime-lock config/runtime-api-lock.json [--recipe REQUEST.json] \
  --root BUNDLE_ROOT --diagnostics DIAGNOSTICS_DIR
worldbridge --inspect-bundle BUNDLE_ROOT/<worldContentId>
```

`--bundle` exits:
- **0** when the bundle is published, or already was (`existed`);
- **3** when the travel graph rejected the world (nothing is published; the reasons are printed);
- **2** when the publish is refused or fails (`path_too_long`, `bundle_conflict`, `staging_invalid`, an I/O error).

Every outcome is also written to `DIAGNOSTICS_DIR/bundle-<random>.json`, with timings and absolute inputs, outside the bundle.

`--inspect-bundle` validates without Bannerlord and prints the identity and summary as canonical JSON. `state` is `DATA_READY` only for a valid bundle; otherwise it is `sealed-but-invalid` or `unsealed`. It exits 0 if the bundle is valid and 2 if not. A trailing separator on the directory is accepted.

## Layout and identity

```
BUNDLE_ROOT/<worldContentId>/
  source/      the producer split, byte for byte (world.json, scene.json, plans/...)
  compiled/    terrain/{heightfield,masks,weights,height-q16}.bin, terrain/projection.json,
               campaign/{id-map,campaign}.json, travel/{travel-graph,ports}.json, travel/cells.bin,
               validation.json, compatibility.json
  policies/    every input policy, contract and calibration, as <name>.json
  manifest.json
  DATA_READY   {"schema":"mnbl.data-ready","version":1,"worldContentId":...,"manifestSha256":...}, written last
```

- **WorldContentId** is `identity::world_content` over `source/`, `compiled/` and `policies/`. It is the SHA-256 of `mnbl.world-content-id/1\n` followed by the lines `<path>\t<bytes>\t<sha256>\n`, sorted by UTF-8 bytes, with paths spelled with `/` exactly as in the inventory. It is also the directory name.
  - Identical content gives the same id, whoever compiles it.
  - The MMR-24 attempt-level id is the same algorithm over the producer world only (`source/`), so the two ids differ by design: a bundle id also covers what the compiler made.
- **compiled/compatibility.json** (`mnbl.bundle-compatibility`) names the game and War Sails versions (from the runtime lock) and the required runtime capabilities (R02–R05, R08–R10).
  - Because it is content, the WorldContentId covers compatibility. A game or War Sails update compiles the same world to a new bundle, never to a reuse of one with stale versions.
  - The manifest repeats both, and every validator checks that the two agree.
- **The manifest** carries:
  - the schema version, the WorldContentId and its algorithm;
  - the producer lock, binary, commit and generator version;
  - the bridge version and binary hash;
  - the recipe hash and seed, or null outside a generation;
  - every policy hash;
  - the engine versions, the runtime lock hash and the editor (none);
  - the required capabilities;
  - the inventory, with bytes and SHA-256;
  - the coordinate contract and the validation report hash;
  - retention, runtime-derived and migration notes.

  It is canonical JSON and holds nothing volatile.
- **Provenance members** record how the bundle was first made: `source.recipe`, `source.producer`, `compiler` and `engine.runtimeLockSha256`. They may differ when identical content is published again.
- **compiled/** holds exactly the stage outputs: the MMR-26 terrain, the MMR-27 id map, and the MMR-28 travel graph, ports, cells and post-exclusion `campaign.json`.
  - They are compiled in memory from the same policies, and `summarize_bundle.py` shows that they equal the artifacts each stage's own command writes.
  - `validation.json` records the static checks the bundle passed, with every runtime check still pending.
- **Paths** must:
  - be under the three roots, with single `/` separators;
  - be at most 120 bytes long;
  - have no segment Windows cannot hold (empty, `.` or `..`, reserved characters, a trailing dot or space, or a DOS device name such as `CON`, `NUL`, `COM1` or `LPT1`, with or without an extension).

  The real bundle's longest path is 79 bytes.

## Publication

`publish` checks every path. It also checks the root: every file must stay below MAX_PATH under `<root>\<worldContentId>`, which allows a root of up to 73 characters. A root that is too deep is refused with `path_too_long` before anything is written. The publish then writes into `BUNDLE_ROOT/.staging-<random>`:
1. Every file, each with `CREATE_NEW` and a flush.
2. It computes the WorldContentId and re-reads every file against the bytes it meant to write.
3. It writes `manifest.json`, then `DATA_READY`, both flushed.
4. It validates the whole staging directory as a candidate, with the same `validate` consumers run. A failure (for example, a description naming a wrong policy hash) is `staging_invalid`, and nothing is published.
5. It renames the staging directory to `<worldContentId>`.

**The rename.** It is one same-volume `MoveFileExW`, which is atomic and journaled, but not flushed when the call returns. After a power loss, either the staging directory or the bundle exists, and each is complete. A transient denial (a scanner holding a handle) is retried.

**Never overwritten.** If the target already exists, it is reused only when it validates and its manifest equals the new one apart from provenance (`existed: true`, first manifest kept, nothing rewritten). A damaged target, or one that describes the same content differently, is refused with `bundle_conflict`, which names the differing members, and the target is left untouched. A concurrent publisher of the same content that wins the rename is handled the same way.

**Failure and crash.** A failure or cancel removes the staging directory. A crash leaves it in place:
- it is never under a bundle name;
- `list_ready` lists only 64-hex names that hold `DATA_READY`, so the staging directory is never listed;
- its cleanup belongs to MMR-62, which removes it at the next start under the store lock (`Persistence/StagingCleanup.cs`, `docs/save-binding.md`).

A crash after the rename leaves a complete bundle, because `DATA_READY` was flushed first. Listing is not validation: a bundle whose data was torn after the rename is listed as sealed, but it never validates, so nothing uses it.

## Validation rules

worldbridge `validate`, the C# host and `summarize_bundle.verify_bundle` apply the same rules:
- **Parsing:** strict JSON (UTF-8 without a BOM, no repeated keys, no NaN or Infinity). `schema`/`version` are `mnbl.world-bundle`/1 (a number).
- **DATA_READY** names this manifest and this id, and the directory name is the id (for a published bundle).
- **Inventory:** rows have a safe path, integer bytes and a lowercase SHA-256. They are strictly sorted by UTF-8 bytes, and the totals add up.
- **Files on disk** equal the inventory exactly, with names compared exactly, sizes and hashes matching, nothing unlisted (a receipt is named as such), and no junction, symlink or hard link anywhere.
- **Required files:** all three roots exist and every required file is present.
- **Identity:** the WorldContentId recomputes.
- **Hashes:** every policy, and the validation report (which must say `travelStatus: ok`), match the hashes the manifest names.
- **Compatibility:** the manifest's engine versions and capabilities equal `compiled/compatibility.json`.

worldbridge also cross-checks the travel outputs against the compiled files beside them. The C# host also checks compatibility against the host. `tools/test_bundle_parity.py` runs 35 variants through all three validators, and they agree on every verdict. The variants are damaged, resealed (a manifest edit with `DATA_READY` rewritten to match), linked, renamed or pretty-printed bundles.

## Host verification (C#)

`WorldDataBundle.Open(directory, host)` runs before any resource is constructed from a bundle. It is pure managed code with no TaleWorlds types, so the tests run it without the game.
- It applies the rules above. Then it checks that `compiled/compatibility.json` names the installed game and War Sails versions and only capabilities the host provides.
- It never throws for a damaged bundle. Every problem is collected into one `RejectedException`, with a message that says what to do: restore an intact copy, install the matching versions, or update MNBL. No migration is offered (MMR-50, `docs/save-compatibility.md`).
- `ReadVerified(path)` checks a file's hash again when it is read.
- The caller supplies the installed versions and capabilities. No construction path consumes the class yet; the runtime surface and the generation job wire it in (MMR-37, MMR-44).

**Saves.** `SaveIdentity()` gives the binding a save stores: `mnbl.world-bundle-binding.v1` and the WorldContentId. The id covers every file, compatibility included, so it names the exact world. The manifest hash is deliberately not part of the binding: it changes with provenance (another compiler build publishing identical content), and a save does not depend on provenance.

`OpenForSave(root, identity, host)` resolves only that content. A missing bundle, a damaged one or an incompatible host is refused with an actionable message, and a saved world is never regenerated.

Still to come: writing the binding into real save headers, binding it to the campaign instance's activation receipt, and proving load and save in the shipping client. These belong to P01–P03 and U03.

**Activation receipts** (`activation-receipt.schema.json`) bind a bundle's id and manifest to:
- one `campaignInstanceId` (an RFC 4122/9562 UUID, versions 1–8, as generation requests accept);
- one `runtimeDerivedBundleSha256`;
- W02–W05 and C07, each `passed` with an evidence hash;
- `activatedBy: "U03"`.

Receipts live outside the bundle, so identical content can back separate campaigns without changing its id. Resolved game-policy slots (C02–C04) and engine caches belong to the RuntimeDerivedBundle, bound by its own hash. They never rewrite a data bundle.

## What this does not prove

- **Engine activation is not proven here.** Neither are file locks during play, resource lifetime, or whether engine-generated resources can be moved or renamed. A filesystem rename is not proof of a safe engine activation. What is known: MMR-16 (R02) observed that a loaded scene's `terrain.bin` cannot be replaced while it is loaded, so scene names must be immutable. A bundle is never renamed or rewritten after publication. The activation transaction and its runtime proof belong to MMR-44 and U03.
- **Cleanup is not implemented.** The bundle records what cleanup needs (`retention`: kind, what references it, when it may be deleted). The cleanup service is MMR-47, and abandoned staging is MMR-62 (done: `docs/save-binding.md`).
- **Migration is not implemented.** A bundle of another schema is refused. No migration tool exists or is offered (MMR-50, `docs/save-compatibility.md`); the manifest's `migration` field states that one would be needed. Nothing is rewritten at load.

## Real bundle (size 129)

`build/research-bundle29/run.sh` publishes the size-129 research world twice and inspects it. Results:
- **Identity:** `f596b58cdcfc18798542fdef5a4d9c9f6d84abddca1425af8d0b87f521191d2d`, 889 files, 143,659,383 bytes.

  | Root | Files | Size |
  |---|---|---|
  | `source/` | 868 | 141.7 MB |
  | `compiled/` | 12 | 1.9 MB |
  | `policies/` | 9 | 38 KB |
- **Timing:** the first publish took 9.4 s, 6.8 s of it compiling (cold cache). The second took 5.7 s.
- **Republish:** the second publish reused the bundle (`existed: true`, same manifest).
- **Agreement:** worldbridge, the C# verifier and the Python summary recompute the same WorldContentId and accept the bundle. Publishing with a runtime lock naming another game version produced a different WorldContentId, which is a new bundle, not a stale reuse.
