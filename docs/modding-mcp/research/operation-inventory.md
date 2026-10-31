# Modding Kit operation inventory

This is an unreconciled research inventory, **not a statement of complete API coverage**. It records **362 operations** (360 documented actions and 2 derived validation requirements), **83 fetched official pages**, and **25 explicit gaps**, with **17 retained installed-artifact candidates in a separate collection**. No game/editor process, authoring tool, build or runtime probe was executed for this inventory. No capability is accepted as implemented.

The machine-readable source is [official-operations.json](../../../evidence/modding-mcp/research/inventory/official-operations.json). Each record has a stable ID, family, action, intended modes, authoring/runtime semantics, official URLs, route candidate, evidence references and implementation owner. Source records retain retrieval timestamps and SHA-256 identities of the fetched HTML.

## Scope and evidence

The official [documentation index](https://moddocs.bannerlord.com/) and its relative navigation links were fetched, including current scene/resource editors, asset management, audio, mission recipes, multiplayer and War Sails. All relative navigation links discovered by that bounded crawl have source records. This does **not** mean every feature is enumerated: page images/videos were not interpreted, every linked external tutorial was not crawled, long recipes group parameters, and undocumented installed commands are absent until reconciliation.

The inventory also uses the prior [source/API audit](../../../evidence/modding-mcp/primary-source-api-audit.json), [sprite/font tool audit](../../../evidence/modding-mcp/ui-resource-tool-audit.json) and [research findings](../../research/modding-mcp-findings.md). SDK reuse is pinned to [211907728117ec836998b72908f84238a2d179c2](https://github.com/theartur2000/bannerlord-editor-free-sdk/tree/211907728117ec836998b72908f84238a2d179c2); Scene Toolkit examples are pinned to [4edd557f4f7f8814d5a11831cecef791df14748b](https://github.com/fiefevictionnotice/BannerlordSceneToolkit/tree/4edd557f4f7f8814d5a11831cecef791df14748b). These source observations were not re-executed.

The following route counts apply only to the 362 documentation-derived records; the separate installed candidate collection has its own narrower observation levels.

Route labels apply to candidates:

- **Unverified (344):** the action is documented, but its operation-specific callable route remains unresolved.
- **Declared (16):** a command, member or callback is documented/deployed as a declaration; invocation and outcome are unproved here.
- **Traced (2):** prior source/CIL follows texture package creation or sprite generation. Neither is a new engine acceptance result.
- **Verified:** no records. The earlier run 30840 clip playback remains narrow historical evidence for one animation, separately retained in the prior audit.

Intended modes describe implementation targets, not demonstrated availability. A generic entity setter does not establish editor selection, undo, saving or cold reload. A public runtime skeleton/material API does not establish resource authoring.

## Local identity and retained installed candidates

Every operation now references [the local identity manifest](../../../evidence/modding-mcp/research/identity/manifest.json), SHA-256 `9931205f132f98009d489926861ac3b29fd8aa1bfd0aed7a80747d2228752c35`, captured at `2026-10-31T11:44:58.102054+00:00`. This identifies the intended local target only. **Compatibility between those binaries and the current documentation is unknown; runtime compatibility is unverified.** The installed candidate records additionally contain exact artifact paths, hashes and MVIDs. Their retained audit hashes match the manifest; no target code was executed to establish compatibility.

The JSON's separate `installed_candidates` collection contains 17 records from the retained managed and native audits:

- Three editor callbacks: scene save, prefab save and terrain reload. The first two inspected bodies contain only `ret`. They are notifications, not commands that persist or reload content.
- Two internal undo-component registration helpers. Their list changes are traced; they do not push transactions or execute undo/redo.
- Generic command dispatch/completion and resource-modification checks. Managed delegation is traced, but command grammar, import semantics and outcomes are unresolved.
- Engine scene creation/read wrappers. These do not establish active editor-document creation/opening.
- Three native publish registration candidates: `publish`, `publish_for_ds` and `publish_for_em`. Construction/registration evidence does not establish their handler ABI, arguments or completion.
- Four Qt action-slot candidates for save, save-as, quick save and publish. Wiring is traced; callable bodies are unresolved, and UI-only execution remains excluded.

Each includes evidence record/token/address references and links to related documented operations. These are overlapping route leads, **not 17 additional delivered features**. Registration and UI-wiring candidates retain an unverified route status even though the bounded static observation is traced.

The six records with `active-editor-document` semantics—create, open, save, undo, redo and scene options—now target **editor only**. Offline file writing cannot open or save the current dirty document or participate in its undo stack. Runtime mode naming is normalized to `client-runtime`; `shipping-game` is no longer a separate label.

Workshop create/update remain `external-service` operations, distinct from local package generation. Their presence is not upload authorization. Execution requires explicit publication authorization, a specific destination and target account; this local-first research task authorizes no external publication.

## Concrete routes and boundaries

[Font documentation](https://moddocs.bannerlord.com/asset-management/how_to_add_custom_fonts/) supplies a stronger route candidate than the earlier managed-only audit: GenerateFont.bat consumes GenerateFontSettings.txt and describes font/output paths, atlas and glyph settings, and .fnt/.bfnt/.png output. The native executable has not been traced or executed. The example joins `0.5-mode`; do not copy its tokenization as a proven command.

[Sprite documentation](https://moddocs.bannerlord.com/asset-management/generating_and_loading_ui_sprite_sheets/) describes source naming, sheet generation, import and loading. The shipped library's public static `SpriteEditorDomain.Start(SpriteEditorDomainConfiguration)` is statically traced in the separate audit. Its executable waits on `Console.ReadKey`; a library adapter avoids that wrapper. Directory-clearing behavior requires scoped outputs and recovery.

[Navigation editing](https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/) and the [inspector](https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh_inspector/) describe topology edits, generation, prefab import/export and diagnostics as separate actions. Public navigation queries and SDK file generation are candidates, not equivalence proof for all editor operations. The orphan-selection heading/description disagree about vertices versus faces.

The [barrier builder](https://moddocs.bannerlord.com/authoring-mission-scenes/script-components/scene_barrier_builder/) generates barriers from a path. This is separate from generic path editing and does not imply road or river mesh generation.

[War Sails map documentation](https://moddocs.bannerlord.com/war-sails/wsworldmap/) adds naval face types, directional flow painting, ports, blockades, settlement-position persistence and distance-cache generation. Numeric face IDs appear in an uninspected image. [Ship authoring](https://moddocs.bannerlord.com/war-sails/ship_creation/) adds nested child files, buoyancy generation, depth rendering and editor previews. [Raid setup](https://moddocs.bannerlord.com/war-sails/what_makes_seaborne_village_raid_scene/) adds coastal navigation and ordered landing/disembarkation markers. These are persistent scene/derived-data requirements except explicitly marked previews.

[Spawn diagnostics](https://moddocs.bannerlord.com/authoring-mission-scenes/script-components/spawn_point_debug_tool/) cover counts, invalid navigation positions and camera focus. Mission-boundary checks have a documented console command and are mode-specific. Scene-editor performance is not final mission performance; the [performance guide](https://moddocs.bannerlord.com/bestpractices/scene_performance_guide/) explicitly requires gameplay testing.

## Implementation ownership

Counts are research workload groupings, not delivered features or completeness percentages.

- **MMR-403 — Scene document lifecycle:** 10 documentation-derived operation records.
- **MMR-404 — Entities, prefabs and transforms:** 18 documentation-derived operation records.
- **MMR-405 — Components and script properties:** 29 documentation-derived operation records.
- **MMR-406 — Viewport, camera and capture:** 17 documentation-derived operation records.
- **MMR-407 — Terrain and foliage:** 25 documentation-derived operation records.
- **MMR-408 — Navigation, paths, roads and rivers:** 42 documentation-derived operation records.
- **MMR-409 — Lighting, weather, water and scene effects:** 21 documentation-derived operation records.
- **MMR-410 — Scene validation and performance:** 12 documentation-derived operation records.
- **MMR-411 — Asset catalog and dependencies:** 6 documentation-derived operation records.
- **MMR-412 — Textures and reimport:** 6 documentation-derived operation records.
- **MMR-413 — Materials and shaders:** 13 documentation-derived operation records.
- **MMR-414 — Meshes, LOD and collision:** 17 documentation-derived operation records.
- **MMR-415 — Skeletons, animation, cloth and morphs:** 33 documentation-derived operation records.
- **MMR-416 — Audio, particles, UI, fonts, sprites and localization:** 39 documentation-derived operation records.
- **MMR-417 — Modules, XML, load order, build and deploy:** 13 documentation-derived operation records.
- **MMR-418 — Complete publishing and verification:** 7 documentation-derived operation records.
- **MMR-419 — Runtime graph and events:** 5 documentation-derived operation records.
- **MMR-420 — Campaign and mission controls:** 3 documentation-derived operation records.
- **MMR-421 — Combat, animation and physics probes:** 1 documentation-derived operation records.
- **MMR-422 — World map and War Sails:** 35 documentation-derived operation records.
- **MMR-423 — Runtime save/load/replay:** 1 documentation-derived operation records.
- **MMR-424 — Diagnostics:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-425 — Install and repair:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-426 — Documentation:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-427 — Integration:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-428 — Adversarial review:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-429 — Early publish core:** 0 documentation-derived operation records; product/reconciliation requirements remain in the gap list.
- **MMR-430 — Dedicated server harness:** 9 documentation-derived operation records.

MMR-429 owns early publish-core feasibility while complete publication records map to MMR-418. MMR-424–428 are cross-cutting delivery/acceptance work, not invented kit actions.

## Reconciliation gaps

- **gap.installed-reconciliation** (MMR-403, MMR-411, MMR-424): Installed menus, command registrations, bindings and mode/build variants are not reconciled with this documentation inventory.
- **gap.source-detail** (MMR-405, MMR-410, MMR-422): Grouped scene recipes and the long ship guide have not been expanded into every script parameter or holder. Images, embedded videos and linked external tutorials were not inspected.
- **gap.scene-durability** (MMR-403): Active dirty-scene save, save-as, reload, crash recovery and global undo/redo require exact native or managed routes; offline scene writers do not substitute.
- **gap.terrain-parity** (MMR-407): Terrain brush falloff, every node/layer option, edit-data preservation, foliage deletion and stochastic painting need installed reconciliation and file round trips.
- **gap.navigation-parity** (MMR-408): Complete topology editing, generation algorithms, undo and path tangent semantics are unresolved; public queries are not authoring proof.
- **gap.roads-rivers** (MMR-408): No dedicated road/river generation workflow found in the fetched current TOC. Paths, barriers, water and flowmap are distinct operations.
- **gap.resource-lifecycle** (MMR-411, MMR-412): Asset deletion, dependency graph, source reimport, import cancellation, resource database repair and every registered importer require installed inventory.
- **gap.mesh-lods** (MMR-414): LOD generation/editing, format matrix, complete collision cooking and mesh deformation options exceed documented import subset.
- **gap.morphs** (MMR-415): No dedicated morph authoring leaf found in current TOC. Pinned SDK morph writer explicitly lacked engine verification.
- **gap.particles** (MMR-416): No dedicated particle-authoring leaf found. Component attachment and audio references do not establish particle asset CRUD, curves or serialization.
- **gap.skeleton-cloth** (MMR-415): Empty/short Skeletons leaf and resource editor descriptions do not reconcile all ragdoll joints, cloth cooking, reimport and runtime reset paths.
- **gap.weather-capture** (MMR-406, MMR-409): Atmosphere selection, reflection settings and water controls do not expose every weather simulation, capture or viewport screenshot operation.
- **gap.bakes** (MMR-409, MMR-410): PRT grid/edit-data workflows are documented; full reflection, terrain, lightmap, shader-cache and other installed bake commands remain unreconciled.
- **gap.ui-localization** (MMR-416): Sprite/font workflows and XML usage do not inventory full Gauntlet widgets, UI inspection, localization translation/pluralization or layout authoring.
- **gap.font-native** (MMR-416): Documented batch/settings options remain untraced against native FontAtlas. Sample contains concatenated 0.5-mode text; exact tokenization must be established.
- **gap.audio-toolchain** (MMR-416): FMOD Studio export scripting, mixer ownership, banks, event schemas and full audible behavior need toolchain/version and output verification; legacy sound page is explicitly outdated.
- **gap.module-build-deploy** (MMR-417, MMR-425): Module examples are not a complete reproducible build/install/repair contract; proprietary dependency discovery and reinstall sources remain implementation requirements.
- **gap.publish-matrix** (MMR-418, MMR-429): Client/editor/server publishing requires exact commands, source inclusion, dependency closure, diagnostics and independent cold-load proof for each target.
- **gap.workshop-conflict** (MMR-418): Official override and upload pages differ on editability of editor-target output. Reconcile actual package contents; uploader config generation is not upload completion.
- **gap.runtime-graph** (MMR-419): Script callbacks and extension examples do not enumerate runtime graphs, event subscription, object lifetime or campaign/mission schema.
- **gap.runtime-control** (MMR-420, MMR-421): Documented mission launch and animation examples do not inventory authoritative combat, AI, physics or campaign controls. Existing REA/runtime evidence must supply candidates.
- **gap.save-replay** (MMR-423): SyncData example concerns behavior persistence only. General runtime save/load, campaign compatibility, replay and deterministic resumption are undocumented in fetched leaves.
- **gap.naval-depth** (MMR-422): Full sails/oars/boarding/upgrade/ballista and water-simulation contracts remain partially grouped; image-only navigation IDs and base-map APIs need reconciliation.
- **gap.server-mode** (MMR-430): Server hosting/custom modes are documented, but installed server identity, headless behavior and complete command list remain untested.
- **gap.acceptance-tooling** (MMR-424, MMR-425, MMR-426, MMR-427, MMR-428): Bounded diagnostics, reinstall, documentation, integration and adversarial tests are product requirements, not certified Modding Kit operations.

The asset browser and skeleton asset leaves are sparse. No dedicated particle or morph authoring leaf was found in this crawl. This is a documentation gap, not proof the engine has no route. The legacy sound page labels itself outdated; current FMOD documentation is the source for audio authoring.

## Acceptance contract

Owner-approved file adapters may satisfy durable operations if they produce independently checked outputs, preserve ownership and dependencies, handle dirty editor state and partial writes, and pass cold reload in the intended consumer/build. Offline scene writes do not save dirty live documents or join editor undo. UI-only fallback and unavailable/no-effect responses do not count.

For each operation, the integration audit still needs installed build/mode identity, exact entry point, input/output contract, engine-thread and lifetime constraints, mutation/dirty-state semantics, independent observations and recovery. Runtime state/effects need separate lifecycle proof. Reconcile installed menus, registration/binding inventories and undocumented script actions before selecting the denominator for MMR-395.

This file and its JSON are inputs to that reconciliation. They deliberately make no whole-kit guarantee.

