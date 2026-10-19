# MNBL modding repository

Mods for Mount & Blade II: Bannerlord v1.4.8 with War Sails. The main one is **MNBL Procedural World**: from the main menu, **New Procedural Campaign** generates a new world from a seed, inside the running game, and starts a Sandbox campaign in it. The world comes with its own terrain, settlements, kingdoms, clans, heroes, economy and land and sea routes, on a flat map that wraps from east to west. Its saves stay bound to that world.

**Status (2026-10-19): a tested local package, not a release.** Dark Mechanicus epic `ep_01m4fpnf646t254mnh69jhz107` is implemented, and its final one-shot acceptance run (MMR-1) has been run. By the epic's rules the run fails, because failures were observed (see s7 below).
- **Seeds:** only seed 42 makes a playable world today. Most seeds fail the generator's quality checks, and seed 73 previews but is refused at Start Campaign.
- **First start:** each new world blocks the game for about 7 minutes while its navigation is prepared.
- **Other open items:** a read-only game folder is unsupported, and the package needs the Visual C++ runtime. Copied official culture text sits in the assembly, which is an open decision before any public release.

Where each epic outcome stands, with its evidence, is in [evidence/one-shot/acceptance-index.json](evidence/one-shot/acceptance-index.json):

| Outcome | Status |
|---|---|
| s1 pinned producer and version-matched setup | fail: everything else passes, but a Windows update replaced the system's `ucrtbase.dll` after the producer's runtime was pinned, so the producer contract's runtime check now fails. Re-pinning is an owner decision |
| s2 in-game generation from New Campaign | fail: only seed 42 gives a playable world |
| s3 periodic flat globe | fail: minor known defects at the seam (planar UI lists, a missed storm tick); seam encounters not run |
| s4 coherent generated campaign | fail: seed 73's villages cannot be bound |
| s5 War Sails on the generated world | fail. Earlier runs passed ports, fleets, routes, voyages and a pirate encounter on world A. On the current package, the one-shot island voyage turned back (it did not reproduce from another save), and the navigation caches were rebuilt on one load although they existed. Boarding, ship damage, battles fought to the end and blockade battles were not run |
| s6 two worlds saved and loaded across a cold restart | blocked: the second world is a fixture, since only one seed activates |
| s7 clean package and the one-shot scenario | fail. The one-shot run is in [docs/one-shot-acceptance.md](docs/one-shot-acceptance.md). Every in-game check of step 1 passed; it fails only on the pinned UCRT. A land battle (a third party joined) and the naval island voyage (turned back) each failed and did not reproduce from another save. The navigation caches were rebuilt on one load. The second-world steps are blocked, and the seam camera, picking and sailing on a generated world were not run |

Broad seed certification, long soaks, endurance, all-season play and performance certification are deferred and listed there as not run.

**Merging this branch into `main` would publish a release.** `config/mods.json` marks `MNBL.ProceduralWorld` `release: true`, and its v0.0.1 has no tag yet. The release workflow therefore publishes it as an ordinary GitHub release, whose notes do not say it is a test package. Decide before merging: `release: false`, a prerelease, or waiting for MMR-1. See [docs/releases.md](docs/releases.md).

## Start here

- **Play:** [docs/runbooks/player-worlds.md](docs/runbooks/player-worlds.md) covers install, the New Campaign flow, seeds, times, where worlds and saves live, backup, compatibility, troubleshooting by error code, and known limits.
- **Develop:** [docs/runbooks/development.md](docs/runbooks/development.md) covers prerequisites, build, offline and in-game tests, deploy, the package, and the shared-installation rules.
- **Release:** [docs/releases.md](docs/releases.md) is the per-mod GitHub release process. [docs/packaging.md](docs/packaging.md) covers the local package and its clean-install test.
- **Product contract:** [docs/decisions/001-product-contract.md](docs/decisions/001-product-contract.md).

Quick build, from PowerShell in this repository, with the prerequisites in the development runbook:

```powershell
./tools/Build-Fwg.ps1 -Source <path to FWGC++ at the pinned commit>
python tests/check_producer_lock.py --source <path to FWGC++>
./tools/Test-ApiPins.ps1
./tools/Build.ps1
./tools/Deploy-Dev.ps1      # refuses while the game is running
```

## Mods in this repository

| Mod | What it is | Registry |
|---|---|---|
| `MNBL.ProceduralWorld` | Procedural campaign worlds (this README) | [config/mods.json](config/mods.json) |
| `MNBL.BattleScale` | Battle Scale experiment ([docs/battle-scale.md](docs/battle-scale.md)) | [config/mods.json](config/mods.json) |
| `MNBL.AgentGrowthProbe` | In-game engine probe for local tests; not released | [config/mods.json](config/mods.json) |

## How the documentation is organised

Each implementation ticket has a doc in `docs/` (named in its first line) and a review record in `evidence/fwg/*-review.md`.

- **World pipeline:** `world-projection`, `terrain-compiler`, `campaign-semantics`, `travel-graph`, `world-bundle`.
- **Campaign content:** `campaign-registry`, `factions`, `heroes`, `settlements`, `campaign-behavior-compatibility`, `naval-content`, `campaign-graph-validation`.
- **Runtime map:** `runtime-backend-decision`, `runtime-surface`, `runtime-navigation`, `water-climate`, `settlement-entities`, `playability`, `periodic-coordinates`, `periodic-movement`, `periodic-presentation`.
- **Missions:** `mission-scenes`, `land-missions`, `naval-missions`.
- **Player flow:** `world-setup`, `generation-job`, `world-activation`, `world-preview`, `recovery`, `first-test-profile`.
- **Saves:** `save-binding`, `world-loading`, `save-compatibility`.
- **Release:** `packaging`, `releases`.

`docs/runtime-capabilities.md` is the running log of what the engine was shown to do; later docs supersede some of its entries.

Generated scenes, user worlds, raw in-game run outputs and build outputs are never committed. The FWGC++ producer source stays in its own repository and is never modified here.
