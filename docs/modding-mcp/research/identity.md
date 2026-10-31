# MMR-391: installed identity and readiness

Execution began2026-10-31. Run `rn_01m68w9j6dd891ynbrjs7brr1r`; source branch `codex/modding-mcp`. The identity gate is implemented; no MCP server or live engine capability is delivered by this ticket.

## Captured identity

`evidence/modding-mcp/research/identity/manifest.json` pins176 binary/module-descriptor files from the selected local game installation.166 have managed MVIDs. Selection includes client/editor/server top-level TaleWorlds/Bannerlord binaries and official Native, SandBoxCore, SandBox, StoryMode and NavalDLC module binaries/descriptors. Native DLLs have null MVID. Module XML versions and content-directory presence are recorded separately. This is not a hash of every game asset or a snapshot of a running process.

`tools/modding-mcp/identity.py` reads PE/CLI metadata without loading target code. SHA-256 is the compatibility gate; MVID provides managed identity corroboration. Installed paths are provided by the caller. Output is refused beneath the game root. Captured size/mtime stability guards against a file changing during its read. Verification checks all manifest-listed paths and refuses missing files, changed bytes/MVID, escaping paths, duplicates and empty manifests.

The editor Engine and Native hashes match the planning REA targets. Editor and shipping Native hashes remain distinct; native addresses cannot transfer between them. Prior evidence compatibility is detailed in [prior-evidence.md](prior-evidence.md), including cases without sufficient historical build attribution.

## Readiness boundary

`doctor.json` is a fresh invocation of the repository's `tools/Doctor.ps1 -RequireNavalEditor` with a new output destination. Required installation checks and War Sails editor-content depot registration passed. Official module versions, manifest status and build prerequisites are retained there. `manifest.json` additionally records editor/client content presence, including NavalDLC editor packs.

These establish installation readiness, not live asset resolution, successful editor startup or full API safety. Runtime verification remains false. MMR-391 criterion c1 remains unmet until a narrow current-build editor/NavalDLC readiness smoke succeeds; it is not silently deferred to later tickets. MMR-392/393/394 own deeper lifecycle/resource/runtime probes before production contracts. No historical successful clip, manual publishing report or nonempty directory is promoted to current live proof.

The previous visual-map owner explicitly released shared editor/game/install control after restoration checks. Root checked that no game/watchdog/launcher was running and that the shared native lock could be opened exclusively, then acknowledged ownership in `task-notes/desktop-coordination.md`. No game/editor launch occurred for identity capture.

## Current startup attempt: blocked

The subsequent bounded readiness attempt started the editor launcher, but did not enter Editor or resolve NavalDLC assets. The Windows helper assigned the returned Bannerlord window to an unrelated Launcher.exe and rejected app-bound, ID-only and direct returned-window observation. A manual startup/Main_map check was requested; no answer was received during this attempt. This is a tooling blocker, not evidence of an engine failure or an unavailable engine API.

`readiness-attempt.json` records the failure and cleanup. Only the owned launcher/watchdog were stopped; no game or scene had opened. Eight prior configuration files were restored and independently hash-checked, all seven official Main_map files remained unchanged, no target process remained, and the native lock was released. `post-startup-verification.json` confirms all176 pinned identities still match. Local configuration backups remain under ignored `build/modding-mcp/readiness-20261031/`, not distributed as server inputs. No installed module or engine binary was changed.

Independent review accepted the bounded c2/c3 research artifacts at inventory SHA-256 `7092c33b0aa8bcd15fc71101507c4d1a45e76673d3342ed1e9d38e3b57e64ef5`:362 documented/derived records,17 separate installed candidates,25 owned gaps, exact target/source bindings and zero accepted capabilities. C1 remains unmet, so MMR-391 is not accepted and downstream implementation has not begun.

## Validation

- `verification.json`: all176 captured identities match fresh reads.
- `tests.log`: five tests pass, including MVID agreement with three independent retained REA producers; same-length byte changes and missing files fail; empty/escaping/aliased-duplicate manifests fail; a native DLL has no managed MVID. Independent review reproduced canonical aliases bypassing the first implementation; the corrected resolved Windows Path comparison rejects both dot-prefix and case aliases.
- `negative-control-manifest.json` deliberately changes the expected Engine SHA only. `negative-control-result.json` reports the mismatch; the CLI exits1. Installed bytes were never changed.

Reproduce using Python3.12 and the installed game path:

```powershell
python tools/modding-mcp/identity.py verify --game-root 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord' --manifest evidence/modding-mcp/research/identity/manifest.json --output build/modding-mcp/identity-check.json
python -m unittest discover -s tests/modding-mcp -p test_identity.py -v
```

The real-artifact tests intentionally require the pinned local binaries and retained REA files. They are not portable synthetic substitutes for local compatibility acceptance. File adapters and direct engine APIs are both approved backends; each future operation still needs mode/build-specific outcome evidence.
