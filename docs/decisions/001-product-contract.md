# FWGC campaign contract

The saved Dark Mechanicus epic ep_01m4fpnf646t254mnh69jhz107, revision 2, owns acceptance. Implementation began on 2026-10-09. This document freezes the confirmed product requirements; it does not assert engine support.

The player generates a seed-specific, full-globe flat campaign from New Campaign. East/west edges connect consistently for rendering, picking, camera, player and AI paths, distances, encounters, weather, missions and saves. Both poles have one logical identity. No cropped region, disconnected seam or player-only teleport satisfies this contract.

Generation and conversion run locally under game supervision. No per-world editor, prebuilt map catalogue, manual copying, publishing, restart or player-launched command is accepted. Reusable resources may be authored during development. Existing official Bannerlord and War Sails assets supply characters, troops, buildings and mission scenes; names, world identities, relationships, geography and campaign content are generated. Sandbox is the first-test mode; StoryMode assets may load to satisfy War Sails dependencies, but its fixed story is not part of generated campaign initialization.

The initial diagnostic recipe is seed 42 (then 43), recipe 3, size 129, phase 16. The rebuilt producer's complete config, effective_config, recipe, topology and physical_scale are materialized in evidence/fwg/resolved-profile.json and linked by hash from the diagnostic profile. The request and producer binary hashes bind those defaults. Three deterministic quality attempts are permitted, with every requested/rejected/final seed recorded. A mechanical failure is not a quality retry. Exact accepted-seed replay preserves frozen input bytes and producer identity.

The UI flow is Seed, Random World, Generate; progress and cancellation; then preview with Start Campaign, New Seed, Back. Full-globe coverage is fixed explanatory text. A diagnostic menu action is a probe, not this completed UI. Engine operations stay on the main thread. Cancellation owns only its job and must not alter accepted worlds or saves.

The implementer owns ordinary template mapping choices under MMR-31/32/33/35/42. Any unresolved clan, culture, hero, troop, settlement, port, ship or mission reference is a typed pending requirement, and MMR-36 must resolve and validate every one before activation. Versioned mapping policy becomes immutable campaign identity; display names never supply IDs. Reusable official asset definitions remain separate from world instances. No silent Calradia fallback is allowed.

The runtime backend is selected only after rendering, terrain queries, picking, land/sea navigation, periodic topology, naval weather/flow, mission return and early save restoration have executable evidence. Inability to prove one blocks its dependent production. A procedural triangle alone does not prove terrain, collision or navigation.

Final acceptance is one clean-package local gameplay session including generated worlds A and B, land and naval gameplay, and cold-restart alternating loads with matching immutable world identities. Corrupt or missing world bindings fail before simulation. Save recovery never regenerates from seed. All seven epic success criteria remain required; no intermediate product approvals are introduced.
