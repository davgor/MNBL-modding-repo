# navmesh.bin: what the engine loads (MMR-38)

This page answers two questions that [navmesh-bin.md](navmesh-bin.md) leaves open: §2.1 says whether zeroed scratch fields load is unknown, and §8 lists it and the LZ4 block form as open questions 1 and 2. Both are now answered (below).

Why the answers are not in `navmesh-bin.md` itself: that file's bytes are pinned by SHA-256 in `worldgen/calibration/naval-transitions/naval-transitions.json` (`source.faceGroupTable`). The pin is for its §5 face-group table, the IL-verified TerrainType ids. The calibration file is a world input inside every bundle's `policies/`, so changing the pin would change every world's content id. `navmesh-bin.md` therefore stays at the pinned bytes, and later findings go here. `tests/ci/test_calibration_pins.py` (run by the offline suites) fails when a pinned file drifts.

## Answers

1. **Zeroed scratch fields load** (`navmesh-bin.md` §2.1, offsets 32 and 40; §8 question 1). MMR-38 runs 42984, 42956 and 32580 loaded generated campaign navmeshes whose two scratch values are 0, and the engine read every face (`evidence/world/navigation-routes.json`). Copying the 16 bytes from an installed file is not needed.
2. **Our LZ4 blocks load** (§8 question 2).
   - A `store` block (one literal run) loads. That is the form the surface encoder and `NavMesh.Encode` write (MMR-38).
   - The `fast` blocks that the `tools/scenebin` builders write by default (`write_scene_navmesh`) loaded in the MMR-17/18 campaign scene slots.

Questions 3–6 of §8 are not answered here.
