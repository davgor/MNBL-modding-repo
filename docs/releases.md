# Mod releases

Each player-facing mod is released on its own. A merge into `main` publishes a GitHub release only for mods whose
`SubModule.xml` Version has not been released yet. Other mods are left alone.

## Releasing a change

1. Change the mod.
2. Bump `<Version value="vX.Y.Z"/>` in `Modules/<id>/SubModule.xml`.
3. Open a PR into `main`. **Mod checks** fails if a release mod's shipped files changed without a version bump.
4. Merge. **Mod releases** builds the mod on a GitHub-hosted Windows runner and publishes
   `<id>-<version>.zip` under the tag `<id>/<version>` (for example `MNBL.BattleScale/v0.1.1`).

A version is released once. To ship the same version again, delete its tag and release, then re-run
**Mod releases** from the Actions tab.

**Procedural World is not ready for a release.** It is `release: true` at v0.0.1 with no tag yet, so the first
merge of its branch into `main` would publish it as an ordinary release. The release notes would not say that it is a
local test package with open decisions (`docs/packaging.md`) and an unrun acceptance scenario
(`evidence/one-shot/acceptance-index.json`). Before that merge, the owner decides: `release: false`, a prerelease, or
releasing after MMR-1. `tools/Package.ps1` needs `release: true`, because `mods.py package` refuses a mod without it.

## Adding a mod

Every `Modules/<id>/` and `src/<dir>/` folder must belong to a mod in [config/mods.json](../config/mods.json);
the checks fail otherwise. A new entry needs:

- `id`: the folder name and the `Id` in its `SubModule.xml`.
- `owns`: the paths that ship in the mod (sources, the module folder, build inputs; not tests). Changing any of
  these requires a version bump. If `project` names a csproj, the files it embeds count as owned too.
- `release: true` and a `build` script, or `release: false` with a `reason` (probes and test harnesses).

A build script takes `-References`, `-Stage` and `-Work`, and fills `-Stage` with the module folder exactly as it
should appear under `Modules/<id>/`. [tools/ci/Build-BattleScale.ps1](../tools/ci/Build-BattleScale.ps1) is the
smallest example.

`mods.py package` refuses any file named like an official game, DLC or dependency file, before it writes
anything, and writes `<id>-<version>.manifest.json` with every file's size and SHA-256, which the release
uploads beside the zip. An optional `package` block lists the only files the zip may hold (`files`), the
provenance of each, the dependencies, runtime requirements, licenses and content notes, and what is
deliberately not redistributed. With it, an unlisted file, a file without provenance, or a missing listed
file is refused too. Procedural World has one; how its
package was built and tested installed on its own is in [packaging.md](packaging.md).

## How the runner builds without the game

`tools/ci/mods.py references` downloads the packages pinned in
[config/ci-references.json](../config/ci-references.json) (BUTR's Bannerlord reference assemblies, Harmony and the
.NET Framework 4.8.1 reference pack) and lays them out like a game install. The projects then build with
`GameRoot`, `WorkshopRoot` and `TargetFrameworkRootPath` pointing at that layout. The compiled DLLs were
byte-identical to the local builds against the installed game (checked 2026-10-17 for both mods).
`gameVersion` there must match `tools.lock.json`; moving to a new game build means re-pinning both.

Procedural World also ships the pinned FWGC++ producer. The runner downloads it from this repository's
`fwg-producer/<commit>` prerelease and checks it against `tools.lock.json`. After the producer pin changes,
publish the new binary once with `./tools/ci/Publish-Producer.ps1`.

## Testing the tooling locally

```powershell
python tools/ci/mods.py check --base origin/main
python -m unittest discover -s tests/ci
```
