# Gamer lens: memory of what was said and population coverage

Target: saved epic revision 2, `build/research-ai-influence/red-team-plan.json`; FWGC++ HEAD `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`, clean when inspected. Review only; no game/model/build runs. Historical experimental outcomes below are the repository's reports, not reproduced trials.

## G1 — Available facts must not become memories of disclosure

The graph plan distinguishes truth, belief, claims and commitments, but does not define which facts become memories of what a player was told. A complete fact packet is not a delivered conversation. This matters after long absences: an NPC can insist it disclosed a treaty, quest condition or accusation which the player never saw.

Evidence: FWGC++ `communicator/eval/README.md:390-394` reports the early hero benchmark's memory keeping offers as told when no reply said them: the packet held eight offers and the line named three. Its later reports at `:556-558` still ask for the speaker's own words to be kept. `session_hero4.txt:26-48` includes compaction, shared work, a return, then another compaction; `:56-60` asks about the intervening period. These are concrete multi-visit consumers of that memory boundary. Plan MMR-315 c5 models witnessed/hearsay knowledge; MMR-316 only says follow-up turns use results/current facts; neither records actual disclosure as a separate event. MMR-317 claim provenance does not establish delivery.

Suggested additions:

- **MMR-315/316:** journal delivery receipts for actual speaker, audience, delivered text/claim references, time and conversation. Separate retrieved facts, candidate output, approved output, delivered content and attributed player speech. Rejected drafts, unused packet facts and unseen suffixes of cancelled streams never count as told/heard. Define whether partial delivery is visible and how that portion is retained; fallback records its own delivered content. Cross-speaker memories follow actual audience/witness permissions, and narrator/UI facts do not automatically become NPC knowledge. Keep historical disclosure independently of a later corrected world fact.
- **MMR-356:** a packet with several offers/treaty conditions but a reply mentioning only a subset; rejected draft replaced by fallback; cancel after partial display; overhearer versus absent NPC; save/load and compaction between visits. Assert exactly which information was disclosed, and ask the NPC what it already told the player. Positive control: a deliberately delivered claim is remembered, so empty memory cannot pass.

Adversarial review and limitations: this is a missing explicit acceptance boundary, not proof the unimplemented rewrite will fail. No assertion that current FWGC++ retains its historical defect. The code lens owns pre-display truth checks; this card owns resulting speech-memory semantics. Exact text retention alone does not prove fact-level disclosure inference is correct, so fixtures require independently labelled disclosed facts.

## G2 — Aggregate dialogue scores can hide populations that have no usable voice

The plan covers model sizes, tool categories and world seeds, but does not require a population-stratified dialogue result. That permits a model to pass on common rulers/quest-givers while rare generated peoples, ordinary NPCs or magic users sound interchangeable or fail consistently.

Evidence: FWGC++ `communicator/eval/README.md:520-524` explicitly says the benchmark heroes in that historical round were all castellans and records three missing dimensions: a self beyond the post, shared-event accounts, and conversational threads. `:659-669` reports persistent failures even after newer director work: questions about self collapse to a post/motto, the player leads every visit, and news contradicts the preceding line. These are documented failures of conversational range rather than evidence that a larger model or more history alone solves them. Current `communicator/eval/session_hero*.txt` fixtures select `who hero giver` across worlds; sessions 7/8 also select a nearby local. This verifies fixture focus, not the resolved archetype distribution of the current generated worlds. Plan MMR-325 c4 counts scenarios by tool/dialogue/intrigue/magic/failure category and MMR-326 c6 reports per-category scores; neither specifies NPC/population strata or tail-quality gates.

Suggested additions:

- **MMR-325/326:** freeze representative and rare strata for generated peoples, political rank/authority, ordinary versus notable NPCs, persona/stance, magic-on/off capabilities, and witnesses versus outsiders. Publish sample counts, uncertainty and worst-stratum results alongside aggregate scores. Include contrasting actors responding to the same event and the same actor on repeated visits; judge whether personality changes are earned, what was said is taken up, and shared events are recounted from the correct perspective. Define minimum usable dialogue/fallback coverage per supported stratum before results; do not claim every combination is tested.
- Integrate this into SDET's blind multi-visit quality protocol rather than creating a second judge system. Reuse existing graph/drift fixtures for political obligations; add population sampling to them.

Adversarial review and limitations: no current generated-world distribution was measured, and no raw historical judge transcripts were found/used by this lens. The historical all-castellan statement is bounded to the report's round; it is not asserted for every current hero or experiment. This is a sampling/acceptance omission in the new plan, not a demand to eliminate legitimate character consistency or to create prose novelty.

## Executed checks and controls

- `git -C C:/Users/davgo/Documents/GitHub/FWGC++ status --porcelain` returned empty; `rev-parse HEAD` returned the pinned commit.
- Loaded the plan with PowerShell `ConvertFrom-Json`, selected MMR-312/315/316/322/324/325/354/355/356 and read their complete body/criteria; searched speech/persona terms with `rg -n 'told|utter|delivered|conversation|personality|persona|said|heard|repetition|repeat|speaker' build/research-ai-influence/red-team-plan.json`. Positive controls: known conversation text in MMR-316 and personality text in MMR-355 were found.
- Read the cited README passages and `communicator/eval/judge_hero.md`; inspected `session_hero4.txt` directly.
- `rg -n '^who |^world ' communicator/eval -g 'session_hero*.txt'` enumerated fixture selectors. Initial attempt passing a literal wildcard path to `rg` failed on Windows; corrected to `-g`. No claim relies on the failed command.

## Dropped or routed findings

- Generic long-term memory, secret scopes, corrected facts, graph ablations, personality persistence, and independent expected-state oracles are already present; not filed as missing.
- Blind conversation-quality scoring is owned by SDET; this lens adds population coverage, not a duplicate quality gate.
- Confirmation of player-affecting actions is owned by product; truth/fallback validation and action menus by code.
- Historical judge aggregate counts were not independently reproduced; do not use them as measured model rankings.

Index line: **Gamer — G1 delivered-speech memory boundary; G2 population-stratified multi-visit quality and worst-stratum reporting.**

