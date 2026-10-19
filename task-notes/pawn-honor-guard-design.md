# Pawn Honor Guard — design epic

Status: design complete; implementation and Bannerlord runtime feasibility are untested.
Dependency: Dark Mechanicus epic `ep_01m4g9zx9kwwzpemjnb10zk0h0`, **Controller-first Bannerlord combat overhaul inspired by Dragon's Dogma 2**, with final acceptance `MMR-67`. It was located when registering this design; implementation must consume its latest accepted contracts and package, not assume a fixed ability set.

## Confirmed user requirements

- Design first, before implementation.
- Three available pawn slots; no pawn sharing or online hiring.
- Pawns are created at pawn stones in cities, with DD2-style character creation, basic abilities and personalities.
- They serve as a highly personable personal honor guard.
- Affection remains ambiguous; there is no explicit romance relationship or confession system.
- Fallen pawns are recovered at city pawn stones, rather than permanently dying.
- This is a follow-up epic to the combat rebuild.

All mechanics below are design proposals supporting those requirements, not claims about currently implemented behavior or DD2's hidden formulas.

## Player loop

Visit a city pawn stone, fill an empty slot by creating a pawn, choose their appearance, voice, inclination and starter combat abilities, then summon them into the personal guard. Repeat for up to three pawns. All three are persistent personal creations with equal access to personality and affection systems.

The stone presents the three identities and their status: accompanying, resting at the stone, or awaiting recovery. Resting and summoning preserve equipment, progression, personality and memories. Appearance adjustments do not erase their shared history. Recovery restores the same pawn rather than generating a replacement.

Starter abilities come from the combat rebuild's supported ability catalogue. The stone gives the player a small valid starting selection for each chosen combat role. Weapon requirements, costs, cooldowns and unlock rules are inherited from that combat system. Specific supernatural abilities are not assumed until the combat epic defines them.

## Honor guard behavior

The pawns form a dedicated personal group, available alongside the player's army. Proposed role: accompany and protect the player instead of becoming governors, caravan leaders or independent party leaders. They gain proficiency and equipment over time without becoming substitutes for an entire army.

- Field battles: maintain useful spacing, cover different approaches, intercept nearby threats, and regroup after a brief pursuit. Mounted and foot guards use appropriate spacing; they must not crowd the player's movement or aim.
- Sieges: regroup on reachable positions, pass through doors and ladders without all trying to occupy the same spot, and resume protection after traversal. They cannot teleport through blocked geometry.
- Towns, keeps and taverns: accompany the player where the scene permits, acknowledge surroundings and one another, and adopt relaxed behavior when safe. Restricted scenes need explicit scene-specific support rather than forced spawning.
- Campaign travel: their presence appears through short contextual exchanges and conversations on arrival or at rest; the world-map abstraction does not require free-roaming scene agents.
- If the player falls: surviving guards continue sensible local combat or retreat with the encounter outcome. Protection does not promise revival, rescue, victory or immunity to capture.

Provide four plain orders: **With me**, **Hold here**, **Attack my target**, and **Regroup**. Explicit orders establish the objective; personality shapes its execution. Attack orders have a pursuit limit and return behavior so a pawn does not disappear into the opposing army. Basic self-defense remains active.

## Personality

Use the four DD2 inclinations as the recognizable foundation. Capcom documents that inclinations affect speech and action; the following are original Bannerlord adaptations:

- **Kindhearted:** attentive, reassuring and protective; gives vulnerable allies nearby more attention and notices the player's injuries.
- **Calm:** measured and observant; favors safe positions, identifies danger and expresses care through practical advice.
- **Simple:** earnest and curious; enjoys new places, comments on small discoveries and brings warmth to the group.
- **Straightforward:** candid and bold; readily engages immediate threats and expresses concern through blunt remarks or teasing.

Inclination is independent of appearance, voice and weapon role. A calm swordsman and a straightforward archer should both work. Every inclination obeys the same protection and pursuit boundaries.

Add a small set of personal values, mannerisms and lasting memories so two pawns of the same inclination are distinguishable. Values affect their interpretation of mercy, courage, generosity or recklessness. Their baseline personality remains recognizable as familiarity changes the way they express it.

Store consequential memories with an event, participants, place and personal interpretation: a shared defeat, a narrow victory, a return after separation, or the player's response to the pawn's concern. Only events the pawn experienced or learned about may become memories. Keep a bounded selection of important memories rather than an unlimited transcript.

Give each pair of pawns its own familiarity and banter: friendly teasing, respect for another's skill, disagreement about risk, and concern after a companion returns from the stone. Their social life includes one another.

## Hidden affection

Track trust, respect, affection and unresolved hurt separately, hidden from the player. Shared time supplies context; meaningful events and attentive responses drive stronger changes. Repeat gifts, repeated dialogue and deliberately cycling death/recovery do not farm affection.

Increasing affection changes observable behavior: a warmer greeting, remembering a previous conversation, lingering nearby during quiet moments, relief after reunion, and more personal concern after danger. Combat competence and basic protection remain reliable at every affection level.

Expression follows personality. A kindhearted pawn asks whether the player is hurt. A calm pawn quietly checks their equipment. A straightforward pawn says they would prefer not to have to find another commander. A simple pawn remembers a small place they wanted to show the player.

The emotional arc is **familiarity → attachment → private tenderness**. It stays open to interpretation as devotion, friendship or love. There is no romance flag shown to the player, exclusive partner selection, confession gate, marriage route, compulsory jealousy or automatic romance outcome. All three can independently form deep attachments.

Affection may color dialogue after recovery, but it is never rewarded merely for the pawn being killed. Ordinary disagreement changes tone and can lead to a later conversation; it must not secretly degrade combat performance.

Illustrative exchange after a difficult reunion:

> “You took your time.”
>
> Later, when the others have moved ahead: “I knew you would come.”

The game leaves the meaning of that change in tone to the player.

## Defeat and recovery

A fallen pawn becomes unavailable for the remainder of the encounter. Afterward, their slot is marked as awaiting recovery at any city pawn stone. Recovering restores that existing identity, equipment, progression, memories and relationships. An encounter defeat must not permanently delete the pawn or create duplicates.

Loss therefore temporarily reduces the guard until the player reaches a city. Recovery is not a relationship reset. No resurrection fee, arbitrary wait timer or permanent injury is included in this proposal; those would be separate balance decisions.

## Chatter and presentation

Prioritize immediate danger, direct acknowledgements, important personal reactions, then ambient conversation. One speaker owns an event; casual chatter pauses during combat pressure. Use per-topic repetition limits and meaningful silence. Conversations must refer to actual current context and memories.

The stone shows identities, equipment, abilities, inclination and availability. It does not expose emotional scores. Emotional feedback belongs in dialogue and behavior. Custom expressive animation and authored voice production need separate feasibility/content work; the design does not claim those assets already exist.

## Delivery slices and observable acceptance

1. **Pawn identity and city stones.** Create three distinct pawns; save and reload their identities; rest and summon them without duplicates or memory loss. A fourth pawn cannot be added to the three slots. No sharing dependency exists.
2. **Combat integration.** Select valid basic abilities from the completed combat system. Verify loadouts, restrictions and progression persist and do not duplicate the combat epic's ability logic.
3. **Honor guard.** Verify orders, spacing, pursuit limits and regrouping in foot combat, mounted combat, a crowded siege and supported social scenes. Pawns remain useful without obstructing the player.
4. **Personality and memory.** Put different inclinations through the same events and verify distinct behavior and dialogue. Reload and check accurate recall. Pawn-to-pawn exchanges reference the correct participants.
5. **Ambiguous affection.** Verify gradual, readable differences without exposed scores or explicit romance. Repetition cannot farm attachment; emotional disagreement does not disable protection.
6. **Recovery and persistence.** Lose each pawn, return to a city stone and verify the same pawn returns with the same equipment and history. Repeat across save/load and encounter transitions; test all three being lost at once.

Full visual character-creator quality is dependent on the separate character overhaul. This epic owns invoking that creator for pawn identities and preserving its output, not claiming that the overhaul is already complete.

## Task acceptance and review evidence

- A1 — accepted: the design specifies three local slots, city creation/recovery, campaign presence, battle behavior and social-scene behavior.
- A2 — accepted: the four inclinations, memory interpretation, pair familiarity, hidden relationship dimensions and visible ambiguous-affection signals are specified. All adapted mechanics are labeled as proposals.
- A3 — accepted: the user selected created pawns from city stones, ambiguous affection without explicit romance, recovery at city stones and placement after the combat rebuild.
- A4 — accepted: /root/honor_guard_design_review provided a read-only critique. Root inspected it and integrated proximity/pursuit limits, avoidance of crowding, prioritized chatter, readable affection and reliable protection despite disagreement. No worker or operation remains pending.

Design validation is coverage and consistency review, not implementation testing. No gameplay code was added, no Bannerlord API compatibility was established and no runtime tests were performed.

## Sources and workspace constraints

- Capcom's DD2 inclination descriptions: https://game.capcom.com/manual/dd2/en/ps5/page/4/2 . Direct opening returned HTTP 403; a targeted official-domain search returned the complete four inclination descriptions. Used only to ground the broad inspiration; no hidden-affinity formula was verified.
- User instructions require concise reporting, clear distinction between proposals and verified behavior, and clarification for ambiguous requirements.
- Chat-provided AGENTS.md applies; no AGENTS.md was found at the workspace or inspected ancestors.
- No existing companion source was found in this repository. Existing task-notes/character-overhaul.md, .darkmechanicus/ and .mcp.json are separate work and were preserved.

## Completion and next action

The requested design is complete. Implementation is a subsequent task after the combat rebuild establishes its ability model. Its acceptance gates are listed above and have not been executed.

## Dark Mechanicus registration

The user subsequently requested adding this epic to Dark Mechanicus. This authorizes saving the epic, not starting gameplay implementation.

- DM1: accepted; owner root; used the configured Dark Mechanicus MCP server and inspected its capabilities and plan schemas. Session has plan.save capability; creation and saving were explicitly authorized by the user.
- DM2: accepted; owner /root/dark_mechanicus_dependencies; identified combat predecessor and current MMR-92 contracts, MMR-109 ability executor and MMR-123 mission lifecycle. No duplicate pawn epic or saved appearance-overhaul epic existed. Root confirmed through server reads and history search.
- DM3: accepted; owner root; saved and read back the complete epic with requirements, 19 work tickets, 30 dependency edges and one final acceptance node covering every work ticket. Exact saved bundle equals the reviewed draft; saved validation has zero errors and warnings, storage has zero pending or failed writes, and no execution run exists.

Saved epic: **Pawn Honor Guard: three city-created companions with hidden affection** (`ep_01m4gbt20bt35fw8yrxmq9p5c7`), Backlog. Final acceptance: **MMR-128**. Saved revision 1: `rv_01m4gc3wf9ta87s9j9pcccf61c`; content hash `sha256:df7d35a92ffbded401d27eddf3c8cd04c08266f2caeab4fdc0d557cf260c1d5b`.

Independent review found missing explicit campaign-persistence implementation ownership and overbroad audiovisual acceptance. Root added MMR-148 for the campaign state store before consumers, preserved independent persistence verification, and clarified that supported voice selection is required while unspecified bespoke recordings/animations are not blockers. City-stone coverage now explicitly includes every native city in the pinned game. Root verified these changes and full acceptance coverage before saving.

Combat provenance is informational rather than a scheduler-enforced cross-epic edge. MMR-130 verifies actual upstream acceptance and delivered artifacts before dependent implementation can pass. The character-creator dependency remains the separate character-overhaul design until a delivered creator interface is available. No unrelated epic was edited by this task.

Registration is complete. Gameplay implementation remains future Backlog work.
