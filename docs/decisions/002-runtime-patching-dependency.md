# 002: Runtime patching through the Bannerlord.Harmony module

**Date:** 2026-10-14

**Status:** accepted (user decision during MMR-63 planning)

## Context

A periodic east/west world (the product contract's connected edges) needs engine behaviour changed in non-virtual code: the encounter radius in `PartyBase.CanPartyInteract`, visibility, the `LocatorGrid` distance filter, the native map-click path check in `MapCameraView.HandleLeftMouseButtonClick`, target-position reads during movement and others (`task-notes/mmr63-periodic-seam-research.md`). Game models and our `IMapScene` reimplementation cover only part of this. Until now the module has shipped without Harmony, and no official assembly references it.

## Decision

Runtime patches use the community **Bannerlord.Harmony** module (Steam Workshop 2859188632; 0Harmony 2.4.2.0, module v2.4.2.248 at the time of the decision). The mod does not bundle `0Harmony.dll`.

- `MNBL.ProceduralWorld` declares `Bannerlord.Harmony` as a dependency that loads before it. Probe launches list it first in `_MODULES_`.
- Patches are limited to behaviour that models and the `IMapScene` layer cannot reach. Each patch is applied by our module, recorded in run evidence (target method and patch kind), and removed or guarded when generated mode is inactive, so official campaigns are untouched.
- Install, deploy and the doctor tooling check that the dependency is present, and fail clearly when it is not.

The user later widened this: **Bannerlord.Harmony, Bannerlord.ButterLib, Bannerlord.UIExtenderEx and MCM (Bannerlord.MBOptionScreen)** are all acceptable dependencies. They are installed locally from the Workshop: 2859188632, 2859232415, 2859222409 and 2859238197. The **PlayerSettlement** Workshop mod (3720376888) is a reference for runtime settlement creation calls. Read its approach; do not depend on it.

## Consequences

- Players must subscribe to Bannerlord.Harmony. This is common practice for Bannerlord mods.
- Harmony version drift becomes a compatibility input, so its version is pinned in the API lock and checked at startup.
