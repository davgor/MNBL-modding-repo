# MNBL modding repository

Mods for Mount & Blade II: Bannerlord v1.4.8 with War Sails. The main one is **MNBL Procedural World**: from the main menu, **New Procedural Campaign** generates a new world from a seed, inside the running game, and starts a Sandbox campaign in it. The world comes with its own terrain, settlements, kingdoms, clans, heroes, economy and land and sea routes, on a flat map that wraps from east to west. Its saves stay bound to that world.

**Status (2026-10-19): a tested local package, not a release.** Dark Mechanicus epic `ep_01m4fpnf646t254mnh69jhz107` is implemented, and its final one-shot acceptance run (MMR-1) passes on package `51abd1cc…`: every step, on worlds A (seed 42) and B (seed 73), with seed 43 generating as the profile's next seed. The same run first passed on `e20f2667…`; it was re-run after the seed-43 fixes.
- **Seeds:** offline, 19 of seeds 42–73 give a world at the first quality attempt, and all 19 pass the activation's offline checks; a request gets three attempts. In game, seeds 42 and 73 were played, and seeds 43, 56 and 67 generate and start. Before the 2026-10-19 fixes, 43 was refused at runtime navigation and 56 and 67 at settlement placement (`settlement.port_far`); the offline checks now cover both.
- **First start:** each new world blocks the game for about 7 minutes while its navigation is prepared.
- **Other open items:** a read-only game folder is unsupported, and the package needs the Visual C++ runtime. Copied official culture text sits in the assembly, which is an open decision before any public release.

Where each epic outcome stands, with its evidence, is in [evidence/one-shot/acceptance-index.json](evidence/one-shot/acceptance-index.json):

| Outcome | Status |
|---|---|
| s1 pinned producer and version-matched setup | pass. The producer's UCRT pin was renewed after a Windows update replaced the system's `ucrtbase.dll` |
| s2 in-game generation from New Campaign | pass, with the seed limit above (an owner's judgement: chosen seeds generally play, not every one) |
| s3 periodic flat globe | pass, with three named presentation limits near the cut (an owner's judgement): the hero behaviour text, siege machines on the far side, routes leaving the terrain ([docs/periodic-presentation.md](docs/periodic-presentation.md)) |
| s4 coherent generated campaign | pass |
| s5 War Sails on the generated world | pass. Boarding, ship damage, battles fought to the end and blockade battles were not run |
| s6 two worlds saved and loaded across a cold restart | pass: A and B, both production worlds |
| s7 clean package and the one-shot scenario | pass: [docs/one-shot-acceptance.md](docs/one-shot-acceptance.md) |

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

- **World pipeline:** `world-projection`, `terrain-compiler`, `campaign-semantics`, `travel-graph`, `population` (kingdoms, noble houses, companies, outlaw clans, wanderers), `world-bundle`, `biome-presentation` (map epic 2's shared biome table and policy keys).
- **Campaign content:** `campaign-registry`, `factions`, `heroes`, `settlements`, `campaign-behavior-compatibility`, `naval-content`, `campaign-graph-validation`.
- **Runtime map:** `runtime-backend-decision`, `runtime-surface`, `runtime-navigation`, `water-climate`, `settlement-entities`, `settlement-looks`, `playability`, `periodic-coordinates`, `periodic-movement`, `periodic-presentation`, `map-dressing`.
- **Missions:** `mission-scenes`, `land-missions`, `naval-missions`.
- **Player flow:** `world-setup`, `generation-job`, `world-activation`, `world-preview`, `recovery`, `first-test-profile`.
- **Saves:** `save-binding`, `world-loading`, `save-compatibility`.
- **Release:** `packaging`, `releases`.

`docs/runtime-capabilities.md` is the running log of what the engine was shown to do; later docs supersede some of its entries.

Generated scenes, user worlds, raw in-game run outputs and build outputs are never committed. The FWGC++ producer source stays in its own repository and is never modified here.
