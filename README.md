# FWGC++ procedural Bannerlord campaigns

**Incomplete: this repository currently contains a diagnostic mod and tested producer tooling, not a playable procedural campaign.** The full implementation is tracked in Dark Mechanicus epic `ep_01m4fpnf646t254mnh69jhz107`.

**Verified milestone (2026-10-13):** generated seed-42 native terrain renders, and
a Sandbox campaign boots onto it with War Sails loaded. The diagnostic deliberately
retains official campaign identities, settlement locations and navigation topology;
it is paused and disables autosaves. It is not the completed generated campaign.
The main menu now offers **FWGC: Preview generated terrain** and **FWGC: Sandbox
terrain test (paused)**. The old procedural-mesh crash probes are no longer menu
entries. Evidence and remaining limits are in `docs/runtime-capabilities.md`.

The required outcome is local world generation inside New Campaign, the full globe as a flat map with connected east/west edges, generated campaign content, War Sails travel and safe per-world saves. See `docs/decisions/001-product-contract.md`.

## Build and inspect

From PowerShell in this repository:

```powershell
./tools/Doctor.ps1
./tools/Build-Fwg.ps1
./tools/Build.ps1
./tools/Test-GenerationProbe.ps1
./tools/Test-Heightfield.ps1
python tests/test_source_reader.py
```

To reproduce the native campaign bootstrap fixture locally after the seed-42
heightfield has been extracted to `build/heightfield-source42/heightfield.bin`:

```powershell
python tools/scenebin/build_campaign_fixture.py
python tests/check_campaign_fixture.py
./tools/Build.ps1
./tools/Deploy-Dev.ps1
./tools/Deploy-SceneSlots.ps1 -Map 'build/campaign-fixture/mnbl_campaign_42=SceneObj/mnbl_campaign_42'
./tools/Start-OwnedProbe.ps1 -Probe sandbox_boot
# After the run exits:
./tools/Collect-OwnedProbe.ps1
```

The fixture extracts installed scene scaffolding into ignored local build output.
It does not alter official modules. Do not save the diagnostic campaign or
redistribute its extracted assets. The automated run captures its map and exits;
the menu action stays on the paused map for inspection. Runtime world generation,
generated campaign content/navigation, periodic edges and world-bound saves remain
unfinished.

The scripts accept the documented local installation paths or use this PC's recorded defaults. Visual Studio 2022 Build Tools, Windows SDK 10.0.26100.0 and .NET Framework 4.8.1 references are required. FWGC++ source remains in its existing sibling repository; it is not modified. `tools.lock.json` pins the tested source and binary. No dependency download occurs during these builds.

`config/runtime-api-lock.json` pins exact game/DLC/framework inputs. The build
checks the compiler's resolved references and records source/output hashes in
`evidence/runtime/build-api-inputs.json`. Deployment refuses changed APIs, stale
sources or outputs, and missing build evidence. Run `./tools/Test-Deployment.ps1`
to check these refusals against disposable copies; official files are untouched.

`tools/Deploy-Dev.ps1` copies only the diagnostic module into `Modules/MNBL.ProceduralWorld` and refuses deployment while Bannerlord is running. The module depends on the installed Native, SandBoxCore, Sandbox, StoryMode and NavalDLC modules. It does not replace official files or existing saves.

The diagnostic menu offers seed 42/43 generation, cancellation, native terrain
preview and the paused Sandbox terrain test. Successful source generation runs the
pinned reader and stores reports beneath
`%LOCALAPPDATA%/MNBL.ProceduralWorld/jobs/<unique job>/`. Those jobs are not
automatically activated as campaigns: the preview and Sandbox test use the locally
built seed-42 scene fixtures. The final Sandbox screenshot is
`evidence/runtime/in-game/sandbox_boot-23152.png`. The shared bootstrap was verified
through automated startup; the menu click itself has not been separately tested.

The C++ `worldbridge` reads a split root with bounded JSON tokens, nesting and input size. It exports selected source blocks and a lossless diagnostic heightfield, checking pinned schema/block versions, a full square height grid, seam/pole continuity and exact layout-seed text. It refuses traversal and missing scene references. It does not ingest scene/plan files or compile gameplay objects, routes, materials, save bindings or a production world bundle. `source-validation.json` explicitly records `campaignReady: false`; it is not an activation receipt.

## Verified checks

- Fresh producer build and five CTest targets passed; seed 42/43 outputs differ; invalid uint32 seed is rejected with exit 2.
- Supervisor harness passed generation, paths containing spaces, cancellation, pre-cancellation and producer-hash refusal.
- C++ reader contract tests and fourteen source integration cases passed; real 129×129 worlds were read successfully.
- Installed engine metadata is recorded with hashes; runtime capability claims remain open in `docs/runtime-capabilities.md`.

The full playable campaign, terrain picking, generated land/sea navigation, globe
wrapping, generated factions/settlements, New Campaign UI, mission transitions and
save/load acceptance are unfinished. Passing the source tests and paused boot does
not close those tickets.


