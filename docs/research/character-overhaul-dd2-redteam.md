# Character overhaul epic: red team and DD2 creator reverse engineering

*2026-10-18. Target: Dark Mechanicus epic `ep_01m4gbx49w72je0ezgjcfkxtjx`, "DD2-quality character overhaul and creator for Bannerlord humans and FWGC++ races". Saved revision `rv_01m514ayx1pfg3qt05x6aae1zc`: 63 tickets, 3 sprints, backlog, no run.*

**Status.**
- This is a review and a set of proposals. Nothing has been saved to the plan.
- No game was launched and no game file was changed.
- Every claim about native behaviour in Bannerlord or DD2 is either static evidence or an open probe (§8).
- DD2 assets were extracted only for local study and are not in this repository (§10).

**Goal under review.** Every character and NPC, and especially the player, reaches Dragon's Dogma 2 quality. The recorded decision stands: equivalent controls and quality on an original Bannerlord asset catalog, not DD2's assets. D1 asks you to confirm it.

**Supersedes** the static feasibility notes in `task-notes/character-overhaul.md`. That note's handoff still describes saved revision 1 (44 tickets); the current revision is 2 (63 tickets).

## 1. Verdict

**As saved, the epic cannot reach its goal.** The blockers come first, in the order they would stop the plan.

1. **A negative result has nowhere to go.**
   - Probes may record an engine limit, but MMR-167 cannot accept one.
   - s13 is written to stay unmet if Detailed Character Creation does not load on v1.4.8.
   - The sprint gates contradict each other: sprint-1 x4 accepts a recorded block, while sprint-2 n1 and MMR-226 do not.
   - Fix: NEW-D (a recorded user decision for every blocked capability) plus the gate rewrites in §7.
2. **Nobody makes the art, and nowhere stores it.**
   - Sixteen art tickets require human sculpt, hair and texture capability; nothing installed supplies it, and no ticket acquires it.
   - The repo has no Git LFS, GitHub rejects files over 100 MB, and the shipped `male_hair_b.tpac` is already 91.4 MB for 28 styles.
   - Decisions D2 and D15 are needed, plus a benchmark head pair you judge before production (NEW-B).
3. **Races are lost on reload, and NPCs never get them.**
   - Bannerlord saves no race. Heroes reload with their root XML template's race, so the player's same-sex descendants revert to race 0.
   - Single-player ignores `AgentBuildData.Race`.
   - ProceduralWorld's `HeroBodies.Draw` passes race 0.
   - No ticket wires the new creator into the generated-campaign flow where the player is made.
   - s8's "troops of a people use that race" also contradicts your 2026-10-15 decision that troops stay on the vanilla culture until a per-people content epic (D10).
4. **Sprint 1 waits on another backlog epic.** MMR-213 needs `worldgen/policies/peoples.json`, which no one has produced. Dark Mechanicus cannot express cross-epic edges, so this becomes a text gate (§7, EDGES).
5. **The plan breaks your lean-verification rule.** In-game checks sit in most work tickets, it has multiple review passes and hash ceremonies, and it has no research doc. Fixing this changes the sprint structure; D12 recommends 4 sprints with 4 live rounds.

**Static reverse engineering already answers much of sprint 1.**
- **DD2's creator is fully inventoried from its own data:** 474 human-visible controls in 196 families.
- **Shape comes from joints.** It is joint transforms on one shared head topology, with zero creator blend shapes.
- **Surfaces are baked.** Every surface input (skin colour, makeup, tattoos, scars, even metallic) is baked into a per-character texture pair. NPCs are 1,576 hand-authored records rendered from 1,832 prebaked texture sets.
- **On the Bannerlord side the evidence settles** the hook map, colour representation, troop seeding, the face cache, translucency flags and the shader route.
- **What remains is 31 named open unknowns** (§8), most of them settled by one live round. One decides the most: whether more than 63 deform keys per skin pack losslessly (U1).

**Recommended architecture: mirror how DD2 builds characters.**
- **Face.** One original shared head topology, rigged like DD2's face rig.
  - The rig's deformations are baked offline into morph frames on Bannerlord's existing 59 keys, which keeps all 504 authored hero faces addressable.
  - New keys are added only for the 11 face, 4 eyelid, 5 ear and 1 lash controls with no counterpart, and the body uses `bone_scale` keys.
- **Player and named heroes.** A runtime per-hero bake (albedo, normal, gloss), applied through managed `Material.CreateCopy` at a public mission listener and at tableau hooks.
- **Troops.** A bounded prebaked `face_texture` palette, as DD2 does for NPCs.
- **Shader work.** Only four items need it: metallic makeup, hair sheen, expression wrinkles and back-lit ear glow. D8 covers what happens if the module shader route fails.

**What "DD2 quality in gameplay" means.**
- DD2's own in-game skin uses a constant subsurface value and baked surfaces. Its per-character translucency and SSS exist only in the creator preview.
- Bannerlord's creator also out-renders its gameplay: translucency is off for every mission agent.
- So parity is judged against DD2's in-game look. Forcing translucency in missions is an optional lift, adopted only if its probe passes.

**Population.**
- Troop faces repeat every 2,000 seeds, and the same seed picks the gear, so seed twins match exactly.
- The shipped face cache can hand one race's face to another, so it must be replaced before races ship.
- Helmets hide both hair and beard on only 4–9% of troop loadouts, so troop faces matter.
- In the 100,000-soldier battle at least 95.9% of soldiers are non-agent figures, which need their own appearance contract (D7).

**The player.** Eight player-facing views and the 2D portrait pipeline carry no character identity today. StoryMode never gives the player's family a race. §6 and §7 cover every one.

## 2. Decisions needed from you

### Before any sprint-1 work

| ID | Question | Recommendation | Why |
|---|---|---|---|
| D1 | How should 'bring in Dragon's Dogma 2's character creator' be read? | Confirm A, and adopt DD2's five-tab structure and its 196 families (474 controls) as the creator's organisation and parity denominator. | A matches the recorded decision. DD2's control ledger, joint tables and shader parameter sets are now statically extracted for study, so A plus DD2's structure delivers nearly everything C would, with nothing copied. B produces nothing shippable and adds RE Engine import tooling. |
| D2 | Which art-production route makes the heads, eyes, hair and bodies? | (a), proven by the NEW-B benchmark head pair before any production batch dispatches. Fall back to (b) only with a recorded quality risk. | Nothing installed supplies DD2-class sculpt or hair capability (AssetHelper has 0 character or Bannerlord adapters). The bake architecture moves most surface work into cheap 2D and data assets: 27 verified makeup masks plus lips, 66 base tattoos (+32 extra), 15 scars, palettes and presets. Human-artist time therefore concentrates on one shared head topology, about 6 base heads, 57 base hairstyles (78 with the 21 extras whose gating is untraced) with cover variants, 44 beards and 26 brow meshes. |
| D3 | Single-player only, or multiplayer too? | Single-player only. | The request reads as single-player. Multiplayer adds 497 armor ids (1,254 vs 757 single-player). It also uses separate paths: AgentBuildData.AgentRace, ClampForMultiplayer (which has a shipped KeyPart8-to-slot-7 bug) and its own face-seed path. Asking late would force sprint-1 probes to be rerun (PP-4). |
| D4 | What does 'unrestricted' fantasy skin colour mean? | (c). | Native colour is one scalar along an authored per-skin gradient (BE-3, confirmed), so free RGB cannot be stored in the key. DD2 itself bakes skin colour into ALBD, so a free colour costs nothing extra on the hero bake route. Troops stay on the native palette, which needs no per-agent work. |
| D10 | Which characters get their people's race in this epic, and who owns troop and civilian race? | (a). | The user decided on 2026-10-15 (peoples-epic comment cm_01m51aqjmdx8pv1210epc69eyq) that generated lords, clans and notables carry their people's culture. Troops, scenes, buildings and clothing stay on the vanilla culture until a separate per-people content epic, which has not been created yet. (b) would reverse that decision. Per-people templates alone never spawn, because all nine paths key on the presentation culture, and 4 of 7 presentation cultures (empire, vlandia, battania, nord) serve two races. Single-player ignores AgentBuildData.Race, so heroes need per-people templates under either option. |
| D12 | How many live game rounds does the epic run, and do editor imports and shader compiles count against the one-live-round-per-sprint rule? | Option 1, with editor sessions allowed in work tickets. | Your standing rule is big batches, one live round per sprint and no game launch inside work tickets; memory records that the battle-scale epic took 2 weeks because of verbose testing. Option 1 meets the rule with 4 rounds and still gives art two in-game rounds (sprints 2 and 3) after the benchmark pair. Option 2 buys two more in-game art rounds (four in 2a-2d against two in Option 1) plus a separate decide-and-freeze round (1b): three more live rounds and checkpoints in all. Art cannot be imported or validated without the editor, so editor sessions stay in work tickets in both options. If you disallow them, every editor import and shader compile (MMR-160 a1, NEW-B a1, MMR-163 a5(1), MMR-168 a4 and all art tickets' imports) moves into its sprint's live round. |
| D14 | How many Bannerlord races: three (human, elf, dwarf), or more for body-distinct peoples such as gnome and tidekin? | (a). A people escalates to its own race only through a NEW-D choice, and only if U14 (combat at about 0.53-0.58 scale) or U16 (bone_scale range) fails for it. | FWGC++ entity-rules.json and heritage-appearance.json list three races, with gnome under dwarf and tidekin under elf, and s8 says three races. Each Bannerlord skin has exactly one face_meta_mesh and one body_meta_mesh, so per-people heads must be either keys or extra races. (a) adds about 12 keys in all: up to 9 face deltas for the 9 non-base peoples, 2 ear forms and at least 1 leg-length bone_scale key; that is about 6 per skin if each race skin declares only its own peoples' keys. The U1 probe must cover that count, and B1's 108-140 omitted it. (b) adds 10 skins per new race and multiplies the equipment refits. |
| D15 | Where do the editor-built asset packages and the art sources live? | (b), with the sources in LFS or external storage as you prefer. | The repo has no Git LFS (.gitattributes is '* -text', kept for byte-exact receipts), and GitHub rejects files over 100 MB. The shipped male_hair_b.tpac is already 91.4 MB for 28 styles, so about 78 styles with covers, 40 skin sets per sex per race and the race bodies will exceed that limit or bloat history. The hosted release runner has no editor, so packages are built locally in every option; (b) also keeps the binaries out of history. Whatever you choose, tools/ci/mods.py check gains a repo-wide RE Engine resource-magic scan. The DD2-extracted-hash scan moves to a local pre-push hook, because the hosted runner has no extracted DD2 files. |
| D16 | One module, or a core creator module plus a separate FWGC races module? | (b). | The races depend on the peoples epic and ProceduralWorld (cross-epic gates, D10, D23); the core creator does not. A split lets the core release and be checked on vanilla Sandbox and StoryMode first, where most of the player identity sites live, and keeps ProceduralWorld coupling out of it. The cost is a second mods.json entry and module, plus a core-to-races contract (race ids and the record's race field). PS-4 left this open. Because it changes module paths in every race ticket, it is needed before sprint 1. |
| D17 | May the DD2-derived control ledger (control names, tab/part, UI and table ranges, mechanism classes; no assets) live in the MNBL repo, or only locally? | (a), unless you do not want DD2-derived tables in a public repository; in that case choose (b). | s1's traceability matrix, MMR-167 a1 and every acceptance node read the ledger. If it lives only locally, other sessions and the hosted runner cannot check parity. It holds no DD2 asset, and the repo-wide RE Engine magic scan (D15) keeps resource files out. DD2 captures are DD2 renders, so they stay local under PS-3's path rule in either option. |

### At the sprint-1 checkpoint

| ID | Question | Recommendation | Why |
|---|---|---|---|
| D5 | Family race: what race do creation-time family members and the children of mixed-race parents get? | (a). Family race is written to the race record at OnCharacterCreationIsOver for every creation-time family hero, and each also gets an appearance record. A child's race is written to the record at birth. No blended-race bodies. | StoryModeCharacterCreationCampaignBehavior.FinalizeParentsAndLittleSiblings copies StaticBodyProperties, Weight and Build to the parents only and never calls set_Race on any family hero. An elf player therefore gets human-race parents carrying elf-derived keys, and human-race siblings on their template bodies. DeliverOffSpring's same-race assert is non-fatal, and vanilla GetCharacterTemplateForOffspring already picks the same-sex parent, so (a) needs the least patching. Without the record, family members and children reload with their root template's race. |
| D6 | How do race lifespans map onto Bannerlord ages? | B. | All seven AgeModel age getters (BecomeInfant, BecomeChild, BecomeTeenager, HeroComesOfAge, BecomeOld, MiddleAdultHood, MaxAge) take no character, so A needs Harmony patches on every caller. B reuses the 5 maturity skins per race and still lets MMR-213 carry life_stages for display. |
| D7 | What do distant figures in the 100,000-soldier battle carry, and who owns that? | (a). The battle epic also owns a stable per-soldier or per-slot identity for appearance seeds. | At 100k, at least 95.92% of soldiers are non-Agent figures (89.8% at 40k, 59.2% at 10k, 0% at 2k). Per-soldier AgentVisuals stalled at 2,344 figures. BattleSimulation keeps no per-soldier identity, so this epic cannot seed it. Note that MMR-159's 4,080 case needs the battle-scale engine build. That build fails the stock-binary check in every acceptance node, so it can only be measured in a separate session you approve. |
| D8 | If the module shader route fails, what happens to metallic or coloured face specular, per-texel hair gloss/sheen, expression wrinkles and back-lit ear glow (transmittance) in missions? | Probe (a). If it fails, choose (c), not (b). | (b) cannot ship through tools/ci/mods.py and collides with the shared install. DD2 has no live per-layer metallic in game either: it reads one baked channel. The visible loss is small next to the cost. |
| D9 | Accept named narrowings for controls that Bannerlord has no mechanism for? | Bust (a). Walk style goes to a follow-up animation epic. Record both as user-approved narrowings in NEW-D. | There is no breast bone among the human bones, and armor does not follow body morphs. Walk style is gait and posture (action sets), not appearance. |
| D11 | What breadth targets does the frozen catalog use? | (a). Count the 21 extra hairstyles and 32 extra tattoos only if you choose to include them, and give the four unverified families your own targets. Head styles are presets over a few base sculpts on one topology, not 76 separate sculpts. | Under the bake route, masks and presets are cheap; hair and beards dominate art cost. Only counts the verifier reproduced should be frozen as parity targets. The extras' DLC or unlock gating is untraced. |
| D13 | If DCC does not load on v1.4.8, what happens to s13? | Re-scope: every race skin still declares DCC's 19 ids at time points 64-82 (so DCC works once a v1.4.8 release exists), and the runtime DCC check moves to a follow-up. | DCC is an optional companion. Its static binding resolved in round 1 (not re-verified in round 2). As written, s13 would dead-end the epic. |
| D18 | Should the save/load hero preview show the overhaul appearance, or stay native-only? | (a), if U5 shows the material override holds on the FillEntityWithBodyMeshesWithoutAgentVisuals entity; otherwise (b). | The player sees this preview on every load. No campaign, and so no SyncData record, is loaded there; the metadata string is the only campaign data the save list reads. (a) costs one metadata field, one postfix and a disk cache. (b) costs nothing, but the player visibly differs from the in-game look. |
| D19 | Do voice/pitch parity and DD2's BodyWeightData belong in this epic? | (a), recorded as a user-approved narrowing in NEW-D. | DD2 voice is outside the 474-control denominator, and original voice assets have no owner or toolchain. The pawn epic's s1 needs voice, so the host contract must pass voice through under either option. MMR-158 a3 left both items as 'applicability decisions' with no decision owner. |
| D20 | What quality bar do children and teens meet, given that DD2 has adult creator data only? | (a). | No DD2 reference exists for children or teens (DD2R-4), yet MMR-184 and MMR-187 demand a maturity-specific review against a rubric nobody has defined. A benchmark you judge is the only non-arbitrary bar, and it costs little next to the catalog. |
| D21 | Hair physics: Bannerlord has no traced equivalent of DD2's hair physics chain. Static hair, or authored cloth? | (a); (b) only through a probe in a follow-up. | Mapping row M08 records 'no traced equivalent', but the plan treats it as neither an unknown nor a narrowing. Per-agent cloth across crowds would cost frame time, and no probe exists for it. |
| D22 | Do custom battle and other non-campaign modes get appearance records, or stay native-only? | (b). | Custom battle shows the same lords as the campaign, and MissionFaceCacheView is registered by CustomBattleViews.OpenCustomBattleMission, so the troop path (MMR-179 a7) already reaches custom battle. Authored records are static data with no save, and hero bakes there go through the MMR-173 bake queue. A site that has no campaign hero (possibly GauntletBannerBuilderScreen) follows this decision. |
| D23 | Which way does the ProceduralWorld race seam point? | (a), landed once with the FWGC epic owner and batched with other ProceduralWorld changes. | The version bump and nav-cache rebuild are paid once, and the seam stays stable. (b) moves that cost onto every later ProceduralWorld refactor, where a break would show up only as wrong races in a census. |

Options per decision:

- **D1**: A: equivalent controls and quality, ORIGINAL assets (the recorded decision). B: a local importer of your own DD2 assets, never shipped. C: DD2's UX one-to-one with original assets.
- **D2**: (a) A commissioned human character/hair artist for topology, base heads, eyes and hair, with the agent pipeline (Blender, AssetHelper, MPFB CC0 data) for bakes, LODs, masks, XML and presets. (b) Agent pipeline only. (c) MetaHuman-derived meshes, subject to a licence check for non-Unreal use and redistribution. (d) Licensed kits with redistribution rights.
- **D3**: Single-player only; or single-player plus multiplayer.
- **D4**: (a) Any authored swatch: per-race XML gradient points, stored in the native key, free in portraits. (b) Free RGB for everyone: a mod-owned record plus a per-agent material override for every agent. (c) Free colour for the player and named heroes as a per-hero bake input, and an authored palette of at least 24 swatches for generated characters.
- **D5**: (a) Creation-time family takes the player's race: the StoryMode family (main_hero_mother, main_hero_father, StoryModeHeroes.ElderBrother, LittleBrother and LittleSister) and any Sandbox family heroes. Children born later take the same-sex parent's race (the vanilla template choice). (b) As (a), but children take the mother's race. (c) As (a), but children take the clan people's race. (d) As (a), and cross-race marriage is forbidden.
- **D6**: A: race-scaled; coming-of-age, aging and death follow each race's FWGC++ life_stages. B: display-scaled; every race uses human ages and life-stage visuals map proportionally.
- **D7**: (a) s3 covers engine Agents only; distant figures carry a frozen subset (the soldier's race height and proportion scale (under D10 (a) its vanilla presentation-culture race; people scale only if D10 = (b)), sex, skin-palette tint) that this epic publishes and the battle epic consumes. (b) Full appearance on distant figures. (c) Exclude distant figures.
- **D8**: (a) An editor-built module shader cache (probed in sprint 1, MMR-163 a5(1)). (b) Add .rs files under <install>/Shaders/Sources. The client runtime-compiles them, but they cannot be packaged and they write to the shared install. MMR-163 a5(2) probes this route only if you choose (b). (c) A named narrowing: metallic approximated in the bake (lower roughness, brighter albedo), hair gloss as authored per-style material variants, static wrinkle strength only, no mission ear transmittance.
- **D9**: Bust: (a) Size via thorax bone scale, drop Distance and Vertical; (b) body morph frames plus correctives on 748 armor meshes; (c) a dedicated skeleton with 88 or more re-authored action sets. Walk style (4 Pretender controls): out of scope, or a follow-up animation epic.
- **D10**: (a) RECOMMENDED: narrow s8 now. The player, heroes, lords, notables, wanderers/companions and children get their people's race through the per-hero race record, per-people hero templates (MMR-219) and the ProceduralWorld provider seam (direction D23). Troop and civilian race waits for the per-people content epic, and s8's troop/civilian clause is edited to say so. Notables and wanderers created after campaign start are covered under (a) as well. MMR-219 routes them to the people templates through the HeroCreationModel.GetRandomTemplateByOccupation override that MMR-174 already lists. MMR-219 first confirms statically that post-start notable and wanderer creation reaches that override, or names the path it patches instead. (b) Take troop and civilian race into this epic. MMR-219 then creates people-specific troop and civilian templates and rewires every spawn path that keys on the presentation culture, each with its own census criterion: (1) volunteer recruitment from Culture.BasicTroop and EliteBasicTroop: every volunteer recruited at a people-owned settlement has that people's race; (2) upgrade targets: every troop keeps its people's race through the whole upgrade tree; (3) militia: every militia agent spawned for a people's settlement; (4) garrisons: every garrison member of a people's settlement; (5) caravans: every caravan guard and caravan master of a people's clan; (6) villagers: every villager-party member and village LocationCharacter of a people's village; (7) bandits: every bandit-party member from a hideout in a people's region, or a recorded rule if bandits stay culture-based; (8) notables and wanderers spawned after campaign start: every notable and wanderer created after day 0 in or for a people-owned settlement has that people's race; (9) town LocationCharacters (townsfolk, merchants, guards): every civilian in a people-owned town scene.
- **D11**: (a) DD2 GUI-visible counts that the round-2 verifier reproduced: head presets 38/sex, skin styles 40/sex, muscle normals 40/sex, hair 57 base (+21 extra, gating untraced), beards 44, brow masks 14 plus brow meshes 26, eyeliner 13, cheek 4, eyeshadow 5, freckles 5, body hair 4, eyelash 3, tattoos 66 base (+32 extra, gating untraced), scars 15. Eye styles 3, lips 5, dirt 3 and ear presets 7 are unverified (the verifier could not reproduce them), so you set those targets yourself. (b) DD2 catalog sizes (heads 46 m / 44 f, skins 48/46, hair 63+22). (c) A smaller named target.
- **D12**: Option 1 (RECOMMENDED): 4 sprints, 4 live rounds. Sprint 1: static research doc, probes and benchmark pair; one live round runs every probe. Sprint 2: NEW-D decisions, contract freeze, then the player-first production slice (schema, runtime, bake, persistence, creator, adult heads, eyes, identity plumbing); one live round, which is also your visual checkpoint. Sprint 3: population, maturity, hair, markings, races and peoples, wardrobe, performance; one live round. Sprint 4: one review, saves, packaging; one live round. Option 2: the merger's 7-sprint split (1a probes, 1b decide and freeze, 2a player-first slice, 2b population/maturity/hair/markings, 2c races and peoples, 2d wardrobe/performance, 3 review and packaging), with 7 live rounds, for more in-game art iterations. In both options, editor imports and shader compiles are allowed inside work tickets under CLAIM/RELEASE, and the game launches only in acceptance rounds.
- **D13**: Keep s13 as a blocker; move it to a follow-up epic; or re-scope s13 to key compatibility.
- **D14**: (a) Three races, per s8. Gnome (a dwarf people) and tidekin (an elf people) differ through people-delta deform keys, bone_scale keys and presets over their race's head and body: gnome at 4.2 heads tall with long_pointed_jug ears; tidekin with finned_low_swept ears and a nictitating eyelid. (b) Gnome, and possibly tidekin, become their own Bannerlord races: 10 more skins each, their own head and body meshes, their own armor-fit batches in MMR-227, and an edit to s8.
- **D15**: (a) Git LFS for packages and sources. (b) Editor-built AssetPackages published as release artifacts outside git, pinned by URL and SHA-256, which the release build script fetches; sources (.blend, masks) in Git LFS or in external storage, as you choose. (c) Packages committed, split under 100 MB each, with a repository size budget.
- **D16**: (a) One module, MNBL.CharacterOverhaul, holding the creator, runtime, persistence, bake, human skins and the elf and dwarf races. (b) Split: MNBL.CharacterOverhaul (creator, runtime, appearance record, bake, identity plumbing, human skins.xslt; works in vanilla Sandbox and StoryMode) plus a races module (proposed name MNBL.CharacterOverhaul.Races). The races module holds the race skins, monsters, people hero templates, the race registry and the ProceduralWorld provider registration, and depends on the core. Both are registered in config/mods.json.
- **D17**: (a) Commit it as docs/character-overhaul/reference-controls.json; DD2 captures and extracted files stay outside the repo. (b) Keep it local under DD2-modding-repo/local; the repo holds only the generators (tools/dd2-reference/) and the parity matrix, keyed by our own control ids.
- **D18**: (a) Patched: append the record's bake hash to the MainHeroVisual metadata (MainHeroSaveVisualSupplier.GetMainHeroVisualCode) and postfix BasicCharacterTableau.RefreshCharacterTableau to apply a disk-cached bake (Texture.SaveToFile / LoadTextureFromPath, keyed by that hash), falling back to native when the cache is missing. (b) Native-only: the save list shows the native key face and race, without makeup, tattoos or free colour.
- **D19**: (a) Keep the vanilla voice selection and preview (MMR-178 a6) and pass them through ICharacterEditorHost; the pawn epic owns any voice extension its s1 needs. Record DD2 voice/pitch parity and BodyWeightData (its meaning was not traced in rounds 1-2) as out of scope. (b) Voice parity here: DD2-equivalent voice and pitch controls with original voice assets (a new audio-production discipline), plus tracing and mapping BodyWeightData.
- **D20**: (a) A child and a teen benchmark pair you approve (made like NEW-B, as ticket NEW-C in sprint 2) becomes the maturity rubric for MMR-184 and MMR-187. (b) The adult rubric, applied unchanged to children and teens. (c) A narrower bar: production topology and materials, no artifacts, correct race and resemblance to the parents, with no likeness or makeup parity.
- **D21**: (a) Static hair, as in vanilla, with DD2's physics chain recorded as a named narrowing. (b) Authored cloth simulation on long styles, after a probe (cloth on skinned hair meshes is unproven), inside a frame-time budget.
- **D22**: (a) Native-only outside a campaign: named characters render their native keys with the XML catalogs, and troops use the population system as in the campaign. (b) Read-only authored records: named characters use the authored appearance records (MMR-193) and config/character-overhaul/hero-likeness-overrides.json, keyed by character StringId and loaded without a campaign or save; troops as in (a).
- **D23**: (a) ProceduralWorld owns an IGeneratedHeroAppearance provider interface (default race 0). HeroBodies.Draw and the CreateSpecialHero template selection in GeneratedStartupBehavior.cs consult it, and the character (or races) module registers an implementation when both are loaded. Editing ProceduralWorld-owned files forces its version bump (config/mods.json 'owns') and a world-A nav-cache rebuild of about 6.5 min on the first campaign. (b) The character (or races) module Harmony-patches HeroBodies.Draw and the CreateSpecialHero call site from its own side. ProceduralWorld files and version stay unchanged, but the patches couple to ProceduralWorld internals and break silently when those change.

## 3. How DD2's creator actually works

All of this is static evidence. It comes from DD2's own data (build 25920212), extracted and parsed locally. No DD2 session was run. Each figure was found by one lens and then re-derived by a verifier using a different method. Where the verifier narrowed a figure, the narrowed value is the one shown.

### 3.1 The control set: the parity denominator

- **474 human-visible controls in 196 families.** Source: `natives/stm/appsystem/gui/userdata/guicharaeditdata.user.3`, joined to the `charaedit/ch000/edittable`, `editinfluencemap` and `editvalueclamp` tables.
  - Males see 472, females 465; 463 are shared.
  - The family count becomes 197 if `SelfDetailParam` placement wins over `DetailTypes` placement. `HeadStyle` and `EyeStyle` each sit in two tabs.
- **Families by tab:** Head 58, Body 38, Face 18 (hair, brows, beard, body hair), Makeup 46, Tattoo 36.
- **Controls by type:** 357 gauges, 58 colour pickers, 37 catalog selectors, 15 toggles, 4 colour presets, 3 presets.
- **The Tattoo tab holds 296 of the 474 controls.**
  - 290 sit in the 10 region parts: 280 are per-slot copies of one tattoo or scar editor (5 regions × 2 slots), and 10 are `EnableEdit2nd` toggles.
  - The other 6 are Dirt (4) and the two presets.
- **The remaining 136 of the 610 `EditEnum` entries are not human-visible:**
  - 39 are Beastren-only;
  - 86 are hidden sub-keys;
  - 6 have no GUI parameter;
  - 5 are headers.
- **Two earlier numbers were wrong denominators.**
  - "610/71/35" from the 10-16 pass counted enum names.
  - `FaceEditID`'s 71 entries are the union of the human table (55 keys) and the Beastren table (57).
- **Trust in the parse.** The RSZ type database's CRCs mismatch this build on 100% of instances. The layouts still pass every invariant:
  - 1,609/1,609 object references land on the declared type;
  - 2,842/2,842 quaternions are unit length;
  - `EditKeyHash == murmur3(UTF-16 key)` for 179/179 keys;
  - the DD2CCS GUI file yields the same 474.

### 3.2 Shape: joints, not blend shapes

- **No creator mesh carries blend shapes.** All 253 `character/_kit` meshes have zero blend shapes.
  - Positive control: the same detector finds blend shapes on monster meshes `ch21_004` and `ch22_000`.
  - So the joint-only creator is DD2's design choice, not an engine limit.
- **Face.** Face keys are per-joint upper and lower Translate/Rotate/Scale endpoints on an 89-joint face-edit rig (90 with root).
  - The rig is skinned to `head_000_m_blendtarget.mesh`, whose LOD0 positions equal the neutral `head_000_m`.
  - 51 of the 55 human face keys carry joints; the other 4 are parents that only drive children.
- **One topology for every human head.** All 85 human heads (43 male, 42 female) and the blendtarget share 15,489 vertices and 27,548 triangles at LOD0, with byte-identical index buffers.
  - A face delta computed on the rig therefore applies by vertex index to every head of both sexes.
  - The 5 Beastren heads use other topologies.
- **Cost of matching the face with linear morphs.** 48 of the 51 joint keys have non-mirrored endpoints.
  - That means about 98 linear targets at a 1e-4 m tolerance, or 96 at 1e-3.
  - Many of those keys are collinear with unequal magnitudes. Whether they need two targets, or one target with a per-direction gain, depends on the receiving morph system.
- **Body.** Body keys are joint TRS on the animated skeleton.
  - 29 of 32 male keys and 32 of 35 female keys carry joints; 3 per sex are parents.
  - 12 face drivers and 27 male body drivers re-weight other keys through influence maps.
  - 15 face clamps, 3 male clamps and 9 female clamps bound combined values.
- **Height.**
  - Uniform scale of 1.193 (upper) and 0.778 (lower) on 121 male / 104 female joints, within 0.12% of 215/180 and 140/180 cm.
  - It also translates `Hip` by +0.1865 / −0.2146 m and `L_Trap_AOffset` by −0.0094 m.
  - The table range is 140–215 cm; the GUI range is 160–215.
  - FWGC++ dwarf and gnome means (1.05–1.29 m) are below DD2's floor, so DD2 is no reference below 140 cm.
- **Arm length** is a 1-axis (local X) scale of 1.213 / 0.896 on 18 joints: 14 arm and twist bones plus 4 `*_Edit_*` muscle helpers. There are no finger bones.
- **Elf ears are one visible slider.**
  - `Face_Ears_Elf` drives 3 hidden children (Sharp, Length, Up/Down): 28 joint entries on 10 joints, `l/r_ear_flapA–E`.
  - Length and Up/Down clamp at [−45, 145]. There is no ear mesh swap.
  - 10 joint hashes in `Face_Ears_Size` match no bone in any extracted mesh.
- **Second skinning set.** Human heads and bodies carry a second skin-weight stream; `head_000_m` flags 69 of its 263 bones. This suggests, though no session observed it, that body edits reach the head and neck through a separate skinning set.

### 3.3 Surface: everything is baked

- **Where the inputs live.**
  - The creator **Preview** shader (210 parameters) and the **Bake** shader (184) hold every surface input: skin colour curves, makeup, freckles, brows, stubble, body hair, and 10 tattoo + 10 scar layers.
  - The in-game head material (`head_runtime_human.mdf2`, 197 properties) has none of them.
- **The runtime bake.** `PS_ForwardBakeRuntime` composites into a two-slice texture array. The in-game shader addresses it through `SharedTableSlot`, `ALBD_Id` and `NRRA_Id`. Channel layout:
  - `ALBD.rgb` = sqrt-encoded albedo;
  - `ALBD.a` = sqrt(1 − metallic). The D stands for dielectric, not alpha;
  - `NRRA.rg` = normal;
  - `NRRA.b` = tattoo/scar mask (not read in game);
  - `NRRA.a` = roughness.

  Bake targets are 2048 / 512 / 2048, RGBA8 compressed to BC3.
- **No live per-layer metallic in game.** Makeup and tattoo metallic survive only through the baked channel. The only live in-game metallic input is `EventBlood_Metallic`.
- **In-game SSS, translucency and sheen are not per-character.**
  - In-game skin SSS output is a constant (0.12375).
  - Per-character translucency, SSS and sheen exist only in the creator Preview.
  - So DD2's in-game look is the bar for gameplay parity, not its creator preview.
- **Bake modes.** `app.CharacterEditDefine.MaterialBake` = {None, RuntimeBake, ShareRuntimeBake, PreBake}. Its lifecycle runs InPreparation → WaitingForStreamingTexture → Baking → Baked → Discarded.
- **The player is runtime-baked.** This rests on static elimination, not observation: the player cannot be in the prebake catalogs, and no in-game head material composites creator layers live.

### 3.4 NPCs: authored, then prebaked

- **Authored records.**
  - `editdatatable_npc` has 1,055 entries carrying 1,576 full appearance records.
  - `editdatatable_humanenemy` has 176 records under only 8 entries, about 22 variants each.
  - `editdatatable_straypawn` has 5.
  - The creator data contains no procedural population generator.
- **Prebaked textures.**
  - 1,832 prebake folders, each holding head, body and hand albedo and normal maps (6 files).
  - They render through a separate master, `Character_Skin_Head_Ingame_PreBaked`.
  - One sampled NPC set, `ch300041_0`, is 12 files and 21.4 MB, with the head at 2048² BC7.
- **What this means for Bannerlord.** DD2's crowd quality comes from hand-authored, prebaked characters. Bannerlord generates its populations, so "DD2 quality for every NPC" has to be rebuilt as a bounded palette plus runtime bakes for named characters. Nothing can be copied across.

### 3.5 Catalog breadth (GUI-visible counts, adult only)

| Family | DD2 GUI count | Note |
|---|---|---|
| Head styles | 38 per sex | Presets on one topology; the catalog holds 46 male / 44 female |
| Skin styles | 40 per sex | Material swaps |
| Muscle styles | 40 per sex | Normal-map swaps on one body mesh per sex |
| Hair | 57 + 21 extra | Mesh + material + physics chain each; gating of the 21 extra not traced |
| Beards | 44 | Mesh + wrap deformer + stubble mask; male only |
| Brows | 14 painted + 26 meshes | |
| Eyeliner / eyeshadow / cheek / freckles | 13 / 5 / 4 / 5 | |
| Body hair / eyelashes | 4 / 3 | |
| Tattoos | 66 + 32 extra | Extra gating not traced |
| Scars | 15 | |
| Eye styles, lips, dirt, ear presets | 3, 5, 3, 7 | **Not reproduced by the verifier.** Do not freeze these. |

Other facts:
- **No maturity variants.** DD2's creator data covers adults only; no child or teen catalogs exist.
- **Voice** is not one of the 5 tabs (`charaeditvoicemsgdata.user.3` exists but was not traced).
- **The Walk Style tab** (`Pretender*`) controls gait and posture.
- **DD2CCS** ships byte-identical edit tables and meshes, and an identical creator Preview head material. Its differences are snow and fade VFX. It is a valid stand-in for shape and creator-material evidence. Its executable is packed (0 string hits).
- **LODs.** Every character mesh the lens parsed has 2 LODs (head: 15,489 → 1,666 vertices). Round 2 did not re-verify this.

## 4. What Bannerlord v1.4.8 can and cannot do

This section is static evidence only: shipped IL, XML and shader sources (v1.4.8.119303). The managed TaleWorlds DLLs matched `config/runtime-api-lock.json` when they were read. `TaleWorlds.Native.dll` is not pinned by that lock, and no native provider was configured, so anything that happens in native code is an open unknown (§8). It is never stated here as fact.

### 4.1 Where appearance enters, per character category

| Surface | Entry point | Kind |
|---|---|---|
| Skins, races and deform keys | `Module.CreateProcessedSkinsXMLForNative` → `MBObjectManager.GetMergedXmlForNative("soln_skins")` | Managed merge of every module's `skins.xml`. A module's XSLT is applied before that module's XML is merged, so new-race skins must declare all keys inline and the overhaul must load after DCC. |
| Player (creator) | `FaceGenVM` → `BodyGenerator.RefreshFace` / `SaveCurrentCharacter` → `CharacterObject.UpdatePlayerCharacterBodyProperties` | Public managed. The vanilla creator already shows a race selector when `FaceGen.GetRaceCount() > 1`. |
| Lords, notables, wanderers | `HeroCreationModel.GetStaticBodyProperties` / `GetRandomTemplateByOccupation`; `BodyPropertiesModel.Get{Hair,Beard,Tattoo}IndicesForCulture` | GameModel overrides; no Harmony needed |
| Children | `HeroCreator.DeliverOffSpring` → `HeroCreationModel.GetCharacterTemplateForOffspring` (same-sex parent) → native `GenerateParentKey` | GameModel plus native |
| Troops | `CharacterObject.GetBodyProperties(equipment, seed)` → native; in missions the public event `Mission.OnComputeTroopBodyProperties` | Managed event |
| Civilians | `LocationCharacter..ctor` (seed = hash(settlement + character) + equipment seed), which bypasses the troop event | Internal managed |
| Mission agents | `Agent.AddSkinMeshes` → `MBAgentVisuals.AddSkinMeshes` (native). `IMissionListener.OnEquipItemsFromSpawnEquipment` fires afterwards. | Public listener |
| 3D screen tableaus | `AgentVisuals.AddSkinMeshesToEntity`. It already resolves `CharacterObjectStringIdData` in single-player and then discards it. | Private, so Harmony is needed |
| 2D thumbnails | `CharacterImageIdentifier` → `CharacterThumbnailCache` (keyed on the `CharacterCode` string) → `CharacterSpawner.InitWithCharacter` | No identity. `CharacterCode.CreateFrom` ignores a token appended after the trailing `@---@`. |
| Save/load preview | `BasicCharacterTableau.RefreshCharacterTableau` → `MBAgentVisuals.FillEntityWithBodyMeshesWithoutAgentVisuals`, fed by the `MainHeroVisual` save metadata | A second native entry point |
| 8 more player views | Character creation narrative, options, review and clan-naming stages; `BannerEditorView`; `GauntletBannerBuilderScreen`; `GauntletEducationScreen`; `GauntletSceneNotification.OpenScene` | `AgentVisuals.Create` with no identity set |

### 4.2 Findings that change the design

- **Race is not saved.**
  - None of the 1,744 saveable members across 82 shipped assemblies carries race.
  - On reload, every non-original hero copies race from its **root** XML template, because `CharacterObject.CreateFrom` flattens origins.
  - `main_hero` is re-read from XML with race 0, so the player's same-sex descendants revert as well.
  - `BasicCharacterObject.set_Race` is **public**.
  - The only place race reaches a save is the `MainHeroVisual` preview string, which only the save list reads.
  - Consequence: a per-hero race record is required for **every** hero.
- **Single-player ignores `AgentBuildData.Race`.** Only multiplayer code reads it, and the skin race is locked to `Character.Race`.
  - The per-agent Monster (skeleton, capsule, action-set base) is honoured.
  - So every people needs race-specific templates, or a patch.
- **Race plumbing is all name-based.**
  - Each race needs the monsters `<race>`, `<race>_settlement`, `_settlement_slow`, `_settlement_fast` and `_child`.
  - All 88 `as_human*` action sets bind `human_skeleton`, so `base_monster="human"` reuses them. A dedicated skeleton means re-authoring at least 88 sets.
  - `DeliverOffSpring`'s same-race check is a non-fatal `SilentAssert`.
  - Native acceptance of a second `<race>` is untested (U15).
- **Colour is one scalar per channel.** Skin, hair, eye and tattoo colour are each an offset along a per-skin authored RGB gradient. Authored fantasy hues work in XML; the tattoo gradient already ships blue and purple. A free RGB picker cannot be stored in the native key.
- **Key capacity is the decisive native unknown.**
  - Each skin has 63 deform keys, `FaceGenerationParams.KeyWeights[320]` and a 512-bit key.
  - DCC Additional Sliders adds 19 `bone_scale` keys at time points 64–82.
  - Whether more than 63 keys pack losslessly is unknown (U1).
- **Troop faces repeat.**
  - `CharacterHelper.GetPartyMemberFaceSeed` ends in `rem 2000` (coordinator-run).
  - The same seed picks the gear through `Equipment.GetRandomEquipmentElements(new Random(seed))`, so seed twins match in face **and** equipment.
  - `fighter_empire` is shared by 45 non-hero characters; 42 templates serve 258 soldier definitions across the four modules (230 on 36 in the sandbox campaign).
- **The shipped face cache must go before races ship.**
  - `MissionFaceCacheView` reuses similar faces under a budget of `(CharacterDetail + 1) × 100`, with thresholds 100–5000 that rise as the cache fills.
  - In its distance score a race mismatch costs +10 and a sex mismatch +1000, so it can hand one race's face to another.
  - It is registered in 4 mission openers: field/caravan battle, siege with deployment, siege and custom battle.
- **Translucency is creator-only.**
  - `Agent.AddSkinMeshes` passes `useTranslucency = false, useTesselation = false` for every mission agent (coordinator-run; the constructor parameter names were checked). Portraits pass false/false too.
  - The creator face stage uses translucency on with tessellation off. The narrative, options and review stages use both on.
  - No shipped shader sets the `TRANSLUCENT` define.
- **Per-character shader inputs are minimal.**
  - One per-mesh float4 block, one tattoo/scar slot, scalar untinted face specular (F0 0.028), one iris colour per eye mesh, one hair colour.
  - **A per-hero baked texture can carry** albedo (makeup, freckles, brows, stubble, any number of tattoo and scar layers), normal, per-texel gloss/reflectivity, static iris and pupil size, and hair root/tip colour.
  - **It cannot carry** metallic or coloured face specular, per-texel hair gloss/sheen, dynamic expression wrinkles or mission ear transmittance. Those four need shader work.
- **Shader route.**
  - The shipping client runtime-compiles missing variants from `<install>/Shaders/Sources`: 22 compile events in logs and 1,906 compiled files in ProgramData.
  - Building a module shader cache is editor-only.
  - No route for module-local shader source is proven, and the install is shared with the battle-scale session.
- **`face_mesh_cache` is dead data.** It is parsed, and no shipped code reads it.
- **Faces mostly show.** Only 4.2% (party-template weighted) or 8.6% (per troop) of troop battle loadouts hide both hair and beard; about 20% are bareheaded. Helmets do not excuse troop faces.
- **Hero likeness depends on the keys.** 504 single-player heroes carry authored fixed keys (455 distinct), evaluated on `head_male_a` / `head_female_a`. Replacing the heads re-evaluates every famous face.
- **DCC compatibility.**
  - DCC v1.8.1's managed references resolve statically against v1.4.8 (Cecil: 123/123 and 153/153).
  - Runtime load is unverified (U21).
  - Its real conflicts are data ownership: time points 64–82, troop body ranges and its own `set_Race` writes.
- **The appearance record has a ready container.** `Dictionary<string,string>` is predefined as a save container (`SaveableBasicTypeDefiner`), so a `SyncData` record needs no save-id range.

### 4.3 Capability summary

| Mechanism | Status | Note |
|---|---|---|
| Deform-key morph frames | XML-verified | Native evaluation |
| More than 63 keys per skin | Native-unknown (U1) | DCC already declares 82 |
| Bone-scale keys | XML-verified for ±20% | Larger ranges and per-agent scale are unknown (U16) |
| Second race | Managed side race-indexed; native unknown (U15) | |
| Per-agent race in single-player | Not supported | Needs templates or a patch |
| Race persistence | Not supported | Needs a mod-owned record |
| Skin/hair/eye colour | Authored gradient | Free RGB only through a per-hero bake |
| Hair/beard/brow catalogs | XML-verified | Index bit widths unknown (U3) |
| Layered tattoos and makeup | Not in shader | Per-hero bake |
| Metallic makeup, hair sheen, expression wrinkles, mission ear glow | Absent | Shader route unproven (D8) |
| Translucency and tessellation in missions | Off by managed flag | Transpiler probe (U12/U17) |
| Identity at native body builders | Absent | Plumbing required (§6) |
| Aging and maturity | Managed; thresholds native | AgeModel getters take no character (D6) |

## 5. DD2 → Bannerlord change map

Every one of DD2's 196 families (474 controls) is in exactly one row. The completeness critic re-ran coverage and matched 196/196 with no count mismatch. Route abbreviations follow §6. Status says whether a row is agreed, gated on an unknown (§8), or waiting on a decision (§2).

| Group | DD2 mechanism | DD2 families / controls | Bannerlord route | Change | Assets | Status |
|---|---|---|---|---|---|---|
| M01 Face shape on existing Bannerlord keys | Joint TRS endpoints on the face-edit rig: 90 bones, 89 flagged useSecondaryWeight. The rig is skinned to head_000_m_blendtarget, whose positions are identical to neutral head_000_m. 85 human heads share one topology with byte-identical index buffers. 0 blend shapes in 253 kit meshes. 12 face drivers and 15 clamps. | 34 families / 34 controls (3 parents with no joints of their own: Slim_Fat, Eyebrows_In_Out, Bridge_Bulge) | deform-key morph frame on an existing key, re-authored on the new shared-topology heads | Keep shipped key ids and time points 1-59. Name-matched mapping: Slim_Fat_Cheeks→cheeks tp4; Face_Up_Down→center_height tp10; Eyebrows_Up_Down→brow_middle_height tp25 (+ brow_inner/outer driver); Eyebrows_In_Out→eyebrow_depth tp22; Eyebrows_Angle→driver on brow_outer/inner_height; Eyes_Up_Down→eye_position tp23; Eyes_Distance→eye_to_eye_distance tp16; Eyes_In_Out→eye_depth tp14; Eyes_Size→eye_size tp17; Eyes_Angle→driver eye_outer/inner_corner_height tp20/21; Nose_Sharp→nose_definition tp33; Nose_Length→nose_length tp27; Bridge_Straight→nose_bridge tp28; Bridge_Bulge→nose_bump tp37; Tip_Angle→nose_angle tp9; Nostril_Size→nostril_scale tp34; Nostril_Up→nostril_height tp32; Nostril_Width→nose_width tp31; Mouth_Up_Down→mouth_position tp36; Mouth_Width→mouth_width tp35; Mouth_Front_Back→lips_forward tp43; Mouth_In_Out→lip_shape_concave_convex tp55; Mouth_Corner→lips_frown tp41; Lip_Thickness→lip_thickness tp42; Cheeks_Up_Down→cheekbone_height tp5; Cheeks_In_Out→cheekbone_width tp6; Cheeks_Front→cheekbone_depth tp7; Jaw_Round_Square→'Jaw Shape' tp50; Jaw_Height→jaw_height tp49; Jaw_Width→jaw_line tp11; Chin_Height→chin_length tp40; Chin_Width→chin_shape tp39; Chin_In_Out→chin_forward tp38. Drivers and constraint/term rules carry DD2's influences. A '_neg' key is added only for non-collinear endpoints. MAPS DIFFER: arch-first adds one new DD2 key per family at tp≥83 (77-83 frames). CHOSEN engine-first: it needs no new key capacity, the one unproven native fact, and both maps keep vanilla time points for the 504 authored hero keys. | 59 frames per head for existing keys (54 visible plus head_scaling, hide_ears, old_face, kid_face, eyebump) on every head and LOD; likeness pass on the frozen likeness corpus | mechanism agreed; key allocation chosen from engine-first |
| M02 Face shape needing new keys | Face-rig joint TRS keys with no Bannerlord counterpart. Chin_Sharp is one-sided (clamp 0..75). | 11 / 11 | new deform key (tp≥83), native-unknown capacity | skins.xslt adds neck_fat, brow_narrow_wide, brow_inout_a, brow_inout_b, brow_straighten, nose_bridge_width, nose_bulge_a, nose_bulge_b, nose_tip_projection, chin_sharp (key_min 0) and submental. They go before 'weight' ('build' on toddlers), in the same order on all 10 skins of every race, each with str_facegen_skin.<id>. If capacity fails: path B (SetFaceGenerationParams) or path C (NEW-D). | 11 morph frames per head plus non-collinear '_neg' twins; helmet_scaling and deforms_hair authored per key | agree (gated on U1) |
| M03 Eyelid state | Unresolved in DD2: no face-table key. The EyesSquint EditEnum has no GUI record. | 2 families / 4 controls (left/right) | new deform keys (tp≥83) | eye_squint_l/r and eye_close_l/r carry left-only and right-only lid frames. A permanently blind eye can also use the native tattoo tags (e.g. 'blindrighteye'), which drive eye.rsh's per-eye scar flag. | 4 lid frames per head and LOD | agree; DD2 semantics unverified |
| M04 Ears | Joint TRS on ear joints. Face_Ears_Elf drives 3 hidden children (Sharp 8, Length 10, Up/Down 10 = 28 entries) on 10 joints (l/r_ear_flapA-E); Length and Up/Down clamp at -45..145. No ear mesh swap. 7 human presets (unverified: the round-2 verifier could not reproduce the count). 10 Face_Ears_Size joint hashes are unresolved. | 7 / 7 | existing ear_size (tp56), ear_shape (tp52) and hide_ears (tp51) plus new deform keys | New ear_angle, ear_tilt, ear_open, ear_lobe and ear_elf. ear_elf is one one-sided frame equal to the composed child shape at clamp maximum. Each race head carries its own base ear sculpt, and hide_ears is re-authored per head. Original ear presets live in JSON, with the count set by D11 (DD2's 7 was not reproduced in round 2). Under D14, gnome long_pointed_jug and tidekin finned_low_swept ears are people-delta ear keys (ear_gnome_jug on the dwarf skins, ear_tidekin_fin on the elf skins), not separate race heads. MAPS DIFFER: arch-first used 3 child keys plus a managed resolver. CHOSEN engine-first: DD2 shows one visible slider and the children are hidden, which saves 2-5 keys. Arch-first's prefix that zeroes ear_elf before ProduceNumericKeyWithParams(earsAreHidden=true) is adopted only if probe U13 shows the frame adds through ear-hiding helmets. | 5 new ear frames plus hide_ears per head; the elf race head's pointed base ear sculpt; 2 people-delta ear frames (gnome long_pointed_jug on the dwarf heads, tidekin finned_low_swept on the elf heads); original presets at the D11 count | chosen engine-first key count + arch-first safeguard (gated) |
| M05 Head style | PartSwapMeshHolder head swap on one topology plus a skin material swap | 1 / 1 (38 GUI-visible per sex; catalog 46 m / 44 f) | creator presets over shape keys plus a face_texture index | 38 presets per sex in head_presets.json, applied through the creator VM and ProduceNumericKeyWithParams. Optional K hidden basis keys (group -1) carry sculpt detail beyond the sliders if U1 and D11 allow. MAPS DIFFER: arch-first's primary was K exclusive basis keys, with a ReplaceMeshWithMesh fallback. CHOSEN engine-first: no capacity needed. ReplaceMeshWithMesh is untested on skin heads and not adopted. | 38 presets per sex per race; few base sculpts | chosen engine-first |
| M06 Skin style and troop prebake palette | mdf2 material swap (ALB/NRM/NRMR). NPCs use per-character prebaked ALBD/NRRA (1,832 sets, BC7 head) through Character_Skin_Head_Ingame_PreBaked. | 1 / 1 (40 GUI-visible per sex; catalog 48/46) | skins.xml catalog (face_textures, FaceGenerationParams.CurrentFaceTexture) | 40 face_texture entries per sex, same order on all maturity skins. Plus K troop palette entries: original baked combinations of skin, freckles, stubble and scars, picked natively for troops. DD2's human enemies use 176 records under 8 entries. | 40 sets per sex (d, n, s, old_d, old_n) plus K palette sets per race x sex | agree (gated on U3) |
| M07 Teeth | teeth_mat Teeth_Color; the two toggles are unresolved | 3 / 3 | skins.xml catalog (mouth_textures, CurrentMouthTexture) | N tint steps x 4 upper/lower presence states as mouth_texture entries on face_mouth_mesh (shipped: 6 male / 5 female) | mouth materials per race skin | agree; toggle semantics unverified |
| M08 Hair style | Mesh + material + physics chain per style | 1 / 1 (57 GUI base + 21 extra = 78; the extras' DLC/unlock gating is untraced; catalog 63+22) | skins.xml catalog (hair_meshes) | hair_mesh with cover_type1..4 and style_tags; entry 0 is bald; identical list across maturity skins; culture filtering via the BodyPropertiesModel.GetHairIndicesForCulture override; face follow via deforms_hair | 57 base styles (78 if D11 counts the 21 extras), cover meshes at the shipped ratio (about 190 for 78 styles), root-to-tip UV-v | agree; DD2 physics chain has no traced equivalent |
| M09 Beard style | Mesh + PartSwapWrapDeformerHolder (follows face edits) + stubble mask | 1 / 1 (44, male only) | skins.xml catalog (beard_meshes) with build-time morph-frame transfer | beard_mesh with cover_type1..4. Every head frame is copied into each beard by rig_bake at the same time points. Stubble goes into the head bake for heroes and into the palette sets for troops. MAPS DIFFER: engine-first relied on deforms_hair. CHOSEN arch-first's transfer: no deforms_beard attribute exists and the vanilla follow mechanism is unverified (U9). | 44 beards with covers, 44 stubble masks, per-beard frame copies | chosen arch-first follow method |
| M10 Eyebrows | Painted brow into head Eyebrow_MaskMap + brow meshes with wraps; colour and density baked | 3 / 3 (14 painted styles + 26 brow meshes) | skins.xml catalog (eyebrow_meshes) + per-hero bake | eyebrow_mesh entries with transferred frames. Painted brow, density and colour go into the head bake. An independent brow-mesh colour uses a per-hero brow-mesh material copy. Arch-first's 'vanilla ties brow colour to hair through eyebrow_col = factor2' is narrowed: that variable is never used in the face pixel path. | 26 brow meshes, 14 brow masks | agree |
| M11 Skin colour | BaseColor + hidden Color_Curve/Color_Contrast in Preview/Bake only; baked into ALBD.rgb | 1 / 1 | XML palette gradient (population) + per-hero bake input (player/heroes) | skin_color_gradient_points (24 or more, fantasy hues) per race skin; offset = index/(N-1). Free colour for heroes as a bake input with the tint neutralised [D4]. | gradient data per race skin | agree; free RGB per D4 |
| M12 Skin surface | Roughness baked into NRRA.a. SkinWrinkeStrength is unresolved; the in-game WrinkleMap1-3 are facial-animation maps. | 2 / 2 | per-hero bake | Gloss in SpecularMap.y; static wrinkle detail blended into the young and old normal and albedo. Dynamic expression wrinkles are a shader item (D8). | wrinkle detail masks per base head | agree |
| M13 Makeup and freckles | Catalog masks: eyeshadow 5, eyeliner 13, cheek 4 and freckles 5 are GUI-visible and verified; lips 5 was not reproduced in round 2 (the catalog holds 325 per-head lip textures). Colour, opacity, range and roughness exist in Preview/Bake only and are baked into ALBD/NRRA. | 24 / 24 | per-hero baked texture via Material.CreateCopy/SetTextureAtSlot | Composite into DiffuseMap and DiffuseOldMap (slots 0/1) and SpecularMap.y (slot 4). Troops get no runtime makeup; selected looks ship in palette sets. | 27 original masks (eyeshadow 5, eyeliner 13, cheek 4, freckles 5) plus lip masks at the D11 target, on the new head UV | agree (gated on U5, U6) |
| M14 Metallic makeup and tattoo layers | Baked into ALBD.a = sqrt(1-M) by PS_ForwardBakeRuntime; the in-game shader routes ALBD.a to GBuffer RT1.a. No live per-layer metallic in game (only EventBlood_Metallic). | 5 families / 14 controls (TattoosMetallic x10) | shader change | A faceshader_high copy reads one baked metal channel and tints specular by albedo (Face.rsh:68/93 is scalar, F0 0.028). Refined from both maps: one baked channel, not live per-layer parameters. If the route fails: the D8 narrowing. | modified shader plus a module cache | agree (gated on U18, D8) |
| M15 Tattoo and scar layers | Tattoo masks 2048x1024 BC7 (66 GUI base + 32 extra, extra gating untraced) and scar ALBA+NRRM (15). 10 tattoo + 10 scar slots; slot parameters are bindless heap indices in the bake; 0 tattoo parameters in game. | 27 families / 270 controls (x10: 5 regions x 2 slots) | per-hero bake (heroes) + native single tattoo slot (troops) | Heroes: record Layers[20] composited with UV transforms into the head (albedo, normal, spec) and the body/hands material copies. Troops: CurrentFaceTattoo + CurrentFaceTattooColorOffset1 (31 m / 34 f tattoo_materials) plus scars in palette sets. | 66 base tattoo masks (98 if D11 counts the 32 extras; 3-channel + alpha), 15 scar pairs, region UV rectangles | agree |
| M16 Creator state and presets | UI toggles and preset links (164 tattoo / 126 scar controls) | 5 families / 13 controls | mod-owned record + creator data | CharacterCreatorVM state; flags and the layer table in the record; tattoo/scar presets in JSON | original preset data | agree |
| M17 Eye surface | EyeStyle into IrisColorMaskMap + NormalMap. Separate l_eye_mat and r_eye_mat in game. IrisColor1-3, BaseColor (sclera), IrisSize and PupilExpand are static creator values. | 8 families / 24 controls (both/left/right) | per-hero bake (eye diffuse + parallax height) | Iris alpha mask with 3 colours, sclera, and static iris/pupil size baked in (standart.rsh:1057; eye.rsh:38-40). Troops use the 20-point eye gradient. MAPS DIFFER: engine-first left left/right colour dependent on the stock face_eye_mesh UVs. CHOSEN arch-first: an original eye mesh with left and right UV islands that pre-compensate eye.rsh:11's 1.2x remap, giving heterochromia without a shader change (inferred, untested). | original eye mesh; iris styles at the D11 target (DD2's 3 was not reproduced in round 2) | chosen arch-first |
| M18 Eyelashes | Fixed lash cards; EyelashMask (3 styles); length, density and root/tip parameters on the Character_Eyelash shader | 6 / 6 | per-hero bake + new lash_length key | Material copy on face_eyelash_mesh with a composited mask and root-to-tip colour; lash_length morph frame on the lash cards | 3 lash masks, lash cards, 1 frame | agree |
| M19 Hair colour | hair_mat Color_Base/Tip/TipRange | 3 / 3 | per-hero bake (heroes) + XML gradient (troops) | Root-to-tip gradient baked into the hair DiffuseMap with factor colour neutral (hair_functions.rsh:188-195; specular follows albedo). Troops use the 24-point hair gradient. Caveat: greying replaces hue with luminance inside the vertex-blue mask. | greyscale hair albedo with root-to-tip UV-v | agree |
| M20 Beard colour | beard_mat Color_Base/Tip/TipRange + head Beard_Color | 3 / 3 (male only) | per-hero bake on a beard material copy | Same as M19 on the beard mesh; stubble colour in the head bake | greyscale beard albedo | agree |
| M21 Hair and beard sheen and gloss | Character_Hair dual anisotropic specular parameters (matched by name) | 4 / 4 | shader change, or authored material variants | A shader reading g_mesh_vector_argument, or a no-shader approximation: per-style material variants at discrete gloss levels swapped with Mesh.SetMaterial (merger's option, inferred). MAPS DIFFER: engine-first hoped a material copy could change g_specular_coef/g_gloss_coef; arch-first found no managed specular/gloss setter on Material. CHOSEN arch-first's classification, with variants as the D8 fallback. | shader change or 2-3 material variants per style | chosen arch-first + fallback |
| M22 Body proportions | Joint TRS on 121 male / 109 female joints, including 20-21 *_Edit_* helpers. 29 of 32 male and 32 of 35 female keys carry joints; 3 per sex are parents. Arms_Length is a 1-axis scale on 18 arm, twist and helper joints, with no finger bones. 27 male drivers. | 27 / 27 | bone_scale deform key | Muscle_Upper→build tp61; Fat_Upper→weight tp60; Head_Size→head_scaling tp46. DCC ids at tp64-82: Torso_Length→TorsoZ, Fat_Lower→TorsoBelly, Head L/W/D→HeadHeight/Width/Depth, Neck_Thickness→Neck, Chest W/D→ThoraxX/Y, Waist W/D→TorsoX/Y, Pelvis_Width→TorsoHips, Buttock→TorsoButt, Shoulder→Shoulders, Arms Upper/Lower→UpperarmSize/ForearmSize, Legs Upper/Lower→Thigh/Calf. New keys at tp≥83: muscle_lower, neck_length, traps, pelvis_depth, arm_length. The two thickness parents become creator drivers. Translate/rotate channels and helper volume are approximated by scale; reach is unchanged. | XML on all 10 skins per race; armor tested at the extremes | agree |
| M23 Height | Uniform scale 1.193/0.778 on 121/104 joints, plus a Hip translate (+0.1865/-0.2146 m) and L_Trap_AOffset. Table 140-215 cm, GUI 160-215. | 1 / 1 | native height key → agent/frame scale | Height key tp62 via GetScaleFromKey in AgentVisuals.Refresh. It scales about the origin, so no hip offset is needed (inferred). Race floors come from skin min_scale and race monsters. Under D14, the dwarf race skins' range spans gnome (female min 0.95 m) to the dwarf maximum, and the elf race skins' range spans tidekin and elf; each people's default and range are set inside its race's span. Creator shows centimetres from MMR-213 data. | per-race min_scale and monsters | agree (gated on U14) |
| M24 Bust | Joint TRS on 2-3 dedicated joints | 3 / 3 (Distance and Vertical female only) | needs user decision | Recommended (D9 a): Size via biped_thorax y/z bone scale; Distance and Vertical recorded as a narrowing (no breast bone; armor does not follow body morphs). | none under option a | user decision D9 |
| M25 Muscle definition | PartSwapTextureHolder into body/hand NormalMap_Low; one body mesh per sex. MuscleStrength is unresolved. | 2 / 2 (40 per sex) | material copy with a shared catalog normal (troops) / bake blend (heroes) | Troops: shared body-material variants chosen by hash(StaticBodyProperties) and assigned per agent with Mesh.SetMaterial. That needs no per-agent texture, but it is a per-agent material assignment, and whether it persists is U5. Heroes: normal blended at strength in the bake. | 40 normal sets per sex per race body | agree; troop variants gated on U5 [G:U5] |
| M26 Body hair | BodyHair_MaskMap (4) + colour and density, baked | 3 / 3 (human males) | per-hero bake into body/hands albedo | Layer in the body composite | 4 masks | agree |
| M27 Dirt | body_mat Dusty_MaskArray; Dusty_* are live in-game parameters | 4 / 4 | native-unknown (faceDirtAmount) / static bake | Probe SkinGenerationParams.faceDirtAmount (HAS_DIRT is commented out in forward_face_functions.rsh). If inert, bake dirt statically and lose runtime dirt. | dirt patterns at the D11 target (DD2's 3 was not reproduced in round 2) | agree (gated on U22) |
| M28 Walk style | Gait and posture (PretenderData); unresolved | 4 / 4 | needs user decision | Recommended (D9): out of scope, moved to a follow-up animation epic (deform keys only scale; action sets are chosen by name) | none | user decision D9 |

<details><summary>Family names per row</summary>

- **M01 Face shape on existing Bannerlord keys**: Face_General_Slim__1__Fat, Face_General_Slim__1__Fat_Cheeks, Face_General_Face__Up__1__Down, Face_Eyebrows_Up__1__Down, Face_Eyebrows_In__1__Out, Face_Eyebrows_Angle, Face_Eyes_Up__1__Down, Face_Eyes_Distance, Face_Eyes_In__1__Out, Face_Eyes_Size, Face_Eyes_Angle, Face_Nose_Sharp, Face_Nose_Length, Face_Nose_Bridge__Straight, Face_Nose_Bridge__Bulge, Face_Nose_Tip__Angle, Face_Nose_Nostril__Size, Face_Nose_Nostril__Up, Face_Nose_Nostril__Width, Face_Mouth_Up__1__Down, Face_Mouth_Width, Face_Mouth_Front__1__Back, Face_Mouth_In__1__Out, Face_Mouth_Corner__Up1Down, Face_Mouth_Lip__Thickness, Face_Cheeks_Up__1__Down, Face_Cheeks_In__1__Out, Face_Cheeks_Front, Face_Jaw_Round__1__Square, Face_Jaw_Height, Face_Jaw_Width, Face_Jaw_Chin__Height, Face_Jaw_Chin__Width, Face_Jaw_Chin__In__1__Out
- **M02 Face shape needing new keys**: Face_General_Slim__1__Fat_Neck, Face_Eyebrows_Narrow__1__Wide, Face_Eyebrows_In__1__Out_A, Face_Eyebrows_In__1__Out_B, Face_Eyebrows_Straighten, Face_Nose_Bridge__Width, Face_Nose_Bridge__Bulge_Bulge__A, Face_Nose_Bridge__Bulge_Bulge__B, Face_Nose_Tip__In__1__Out, Face_Jaw_Chin__Sharp, Face_Jaw_Submental__In__1__Out
- **M03 Eyelid state**: EyeSquint, CloseEye
- **M04 Ears**: Face_Ears_Size, Face_Ears_Angle, Face_Ears_Open, Face_Ears_Tilt, Face_Ears_Elf, Face_Ears_Lobe, EarsEditPreset
- **M05 Head style**: HeadStyle
- **M06 Skin style and troop prebake palette**: SkinStyle
- **M07 Teeth**: TeethEnableUpper, TeethEnableLower, TeethColor
- **M08 Hair style**: HairStyle
- **M09 Beard style**: BeardStyle
- **M10 Eyebrows**: EyebrowStyle, EyebrowColor, EyebrowDensity
- **M11 Skin colour**: SkinColor
- **M12 Skin surface**: SkinRoughness, SkinWrinkeStrength
- **M13 Makeup and freckles**: EyeshadowStyle, EyeshadowColor1, EyeshadowColor2, EyeshadowThickness, EyeshadowSpread, EyeshadowOpacity, EyeshadowRoughness, EyelinerStyle, EyelinerColor, EyelinerThickness, EyelinerOpacity, EyelinerRoughness, CheekStyle, CheekColor, CheekOpacity, CheekRoughness, LipStyle, LipColor1, LipSpread, LipOpacity, LipRoughness, FreckleStyle, FreckleColor, FreckleOpacity
- **M14 Metallic makeup and tattoo layers**: EyeshadowMetallic, EyelinerMetallic, CheekMetallic, LipMetallic, TattoosMetallic
- **M15 Tattoo and scar layers**: TattoosStyle, TattoosColor1, TattoosColor2, TattoosColor3, TattoosVerticalPosition, TattoosHorizontalPosition, TattoosRotation, TattoosScale, TattoosVerticalScale, TattoosHorizontalScale, TattoosVerticalTiling, TattoosHorizontalTiling, TattoosOpacity, TattoosThickness, TattoosRoughness, ScarsStyle, ScarsVerticalPosition, ScarsHorizontalPosition, ScarsRotation, ScarsScale, ScarsVerticalScale, ScarsHorizontalScale, ScarsVerticalTiling, ScarsHorizontalTiling, ScarsOpacity, ScarsThickness, ScarsRoughness
- **M16 Creator state and presets**: EnableSeparatelyEyeEdit, EnableEdit2ndTattoo, EnableEdit2ndScar, TattooPreset, ScarPreset
- **M17 Eye surface**: EyeStyle, EyesIrisPrimaryColor, EyesIrisSecondaryColor, EyesIrisTertiaryColor, EyesEyeScleraColor, EyesIrisSize, EyesPupilSize, EyeIrisColorPreset
- **M18 Eyelashes**: EyelashStyle, EyelashLength, EyelashDensity, EyelashRootColor, EyelashTipColor, EyelashRootTipBlend
- **M19 Hair colour**: HairRootColor, HairTipColor, HairRootTipBlend
- **M20 Beard colour**: BeardRootColor, BeardTipColor, BeardRootTipBlend
- **M21 Hair and beard sheen and gloss**: HairSheen, HairGloss, BeardSheen, BeardGloss
- **M22 Body proportions**: General_Height_Torso__Length, General_Muscle_Upper, General_Muscle_Lower, General_Fat_Upper, General_Fat_Lower, Core_Neck__0__Head_Head__Size, Core_Neck__0__Head_Head__Length, Core_Neck__0__Head_Head__Width, Core_Neck__0__Head_Head__Depth, Core_Neck__0__Head_Neck__Length, Core_Neck__0__Head_Neck__Thickness, Core_Neck__0__Head_Traps__Size, Core_Torso_Chest__Width, Core_Torso_Chest__Depth, Core_Torso_Waist__Width, Core_Torso_Waist__Depth, Core_Pelvis_Width, Core_Pelvis_Depth, Core_Pelvis_Buttock__Size, Arms__0__Legs_Arms_Shoulder__Size, Arms__0__Legs_Arms_Thickness, Arms__0__Legs_Arms_Thickness_Upper, Arms__0__Legs_Arms_Thickness_Lower, Arms__0__Legs_Arms_Length, Arms__0__Legs_Legs_Thickness, Arms__0__Legs_Legs_Thickness_Upper, Arms__0__Legs_Legs_Thickness_Lower
- **M23 Height**: General_Height_Height
- **M24 Bust**: Core_Torso_Bust__Size, Core_Torso_Bust__Distance, Core_Torso_Bust__Vertical
- **M25 Muscle definition**: MuscleStyle, MuscleStrength
- **M26 Body hair**: BodyHairStyle, BodyHairColor, BodyHairDensity
- **M27 Dirt**: DirtDirtPattern, DirtCoverage, DirtOpacity, DirtColorPreset
- **M28 Walk style**: PretenderAge, PretenderSex, PretenderSpineBend, PretenderLowerSex

</details>

## 6. Implementation spec

**Legend**
- **[S]** Static evidence: shipped IL, XML, shader sources, FWGC++ data or DD2 data. It was read in round 1 or 2, by the merger or by the completeness critic. Nothing here has been observed at runtime.
- **[G:Un]** Gated on unknown Un (see unknowns).
- **[D:x]** Depends on user decision x.

### 6.0 Module and files

**Module manifest**
- `Modules/MNBL.CharacterOverhaul/SubModule.xml`
  - DependedModules: Native, SandBoxCore, Sandbox, Bannerlord.Harmony, Bannerlord.ButterLib, Bannerlord.UIExtenderEx. These are declared Workshop dependencies and are never bundled.
  - `<DependedModuleMetadata id="zzCharacterCreation_AdditionalSliders" order="LoadBeforeThis" optional="true"/>`. DCC's own SubModule.xml uses the `order` attribute. The `optional` attribute is unverified against the official launcher (U32).
  - `XmlNode` Monsters → `monsters`, as in Native/SubModule.xml.
- `ModuleData/project.mbproj`: entries `soln_skins` → skins.xml and `soln_monsters` → monsters.xml. These follow Native's project.mbproj.
- **[D:D16] Module split.** If the user chooses the split, the races module `Modules/MNBL.CharacterOverhaul.Races/` (proposed name; source `src/MNBL.CharacterOverhaul.Races/`) depends on the core. It holds:
  - the race skins.xml and monsters.xml;
  - the people hero templates;
  - `Races/RaceRegistry.cs`;
  - the ProceduralWorld provider registration;
  - `ModuleData/character_overhaul/people_presets.json` and `Races/PeopleDeltas.cs`;
  - MMR-215's race ear keys and sculpts, MMR-216's race skin gradient points and MMR-217 a4's non-human DCC declarations.

  The core keeps the creator, runtime, appearance record (with its race field), bake, identity plumbing, human skins.xslt (which also declares the human peoples' delta keys, driven by the races module's presets) and the DCC support for human skins. Both modules are registered in `config/mods.json`. Below, 'the module' means whichever module D16 assigns a file to.

**Data files**
- `ModuleData/skins.xml`: the elf and dwarf `<race>` blocks [D:D14].
- `ModuleData/skins.xslt`: edits to the human skins.
- `ModuleData/monsters.xml`.
- `ModuleData/module_strings.xml`: one `str_facegen_skin.<id>` per new key. FaceGenVM.Refresh asserts if one is missing.
- `ModuleData/character_overhaul/{head_presets,people_presets,ear_presets,tattoo_presets,scar_presets,eye_color_presets,dirt_color_presets}.json`. `people_presets` holds each people's delta-key weights, proportion keys and default presets over its race head and body [D:D14].
- `GUI/Prefabs/MNBLCharacterCreator.xml`.
- `AssetPackages/` [D:D15]:
  - built locally by the editor, because the hosted runner has none;
  - stored as D15 decides. Recommended: a release artifact outside git, pinned by URL and SHA-256 in `config/character-overhaul-assets.json`, which `tools/ci/Build-CharacterOverhaul.ps1` fetches into the stage;
  - no committed file may exceed 100 MB. That is GitHub's limit, the repo has no LFS ('* -text'), and the shipped male_hair_b.tpac is already 91.4 MB for 28 styles.

**Source**: `src/MNBL.CharacterOverhaul/`
- `Persistence/AppearanceRecordBehavior.cs`
- `Identity/CharacterCodeToken.cs`, `Identity/IdentityScope.cs` ([ThreadStatic] scope), `Identity/IdentitySitePatches.cs` (the eight AgentVisuals.Create sites, the CharacterSpawner.InitWithCharacter hop, MapConversationTableau)
- `Rendering/AppearanceMissionListener.cs`
- `Rendering/TableauAppearancePatches.cs`, `Rendering/SkinFlagTranspiler.cs`
- `Rendering/AppearanceBakeService.cs`, `BakeSlotTable.cs`, `BakeQueue.cs`, `BakeOutputEncoder.cs` (only if the BC route is chosen), `CosmeticComposition.cs`
- `Population/TroopAppearanceSeed.cs`, `TroopFaceBehavior.cs`, `OverhaulHeroCreationModel.cs`, `OverhaulBodyPropertiesModel.cs`
- `Races/RaceRegistry.cs`, `Races/PeopleDeltas.cs`
- `Creator/CharacterCreatorVM.cs`, `DependencyResolver.cs`, `FamilyRaceWriter.cs`
- `Compatibility/DccSupport.cs`

**Tools and registration**
- `tools/character-overhaul/rig_bake/`.
- `tools/dd2-reference/`: the generators only, with no DD2 data. The DD2-derived ledger is either committed (docs/character-overhaul/reference-controls.json) or kept under DD2-modding-repo/local [D:D17]. DD2 captures always stay outside the repo.
- `tools/ci/mods.py check`: a repo-wide scan that fails on any tracked file carrying RE Engine resource magic (the header signatures of every resource type the tools/dd2-reference generators read). `tools/ci/mods.py package` runs the same scan over the staged files.
- `tools/ci/hooks/pre-push`: local only. It fails on any file whose SHA-256 matches a file under `DD2-modding-repo/local/extracted`. It cannot run on the GitHub-hosted runners, which have no extracted DD2 files, and docs/releases.md says so.
- `config/mods.json`: an `MNBL.CharacterOverhaul` entry, plus the races module's entry if D16 splits.

### 6.1 Persistence

**[S] Record.** `AppearanceRecordBehavior : CampaignBehaviorBase`, `SyncData(IDataStore)`.
- Key `mnbl_co_appearance_v1`, type `Dictionary<string,string>`, mapping hero StringId to a versioned blob.
- No SaveableTypeDefiner or save-id range is needed: `SaveableBasicTypeDefiner.DefineContainerDefinitions` IL_0471 already constructs that container (merger check).
- Blob fields:
  - schema and catalog versions;
  - race id string;
  - a copy of the native key, to detect external edits by the barber or DCC;
  - extension key weights (path B or C only);
  - layer stack (up to 20);
  - makeup, eye left/right, hair and beard root/tip, and muscle parameters;
  - free colour [D:D4];
  - bake hash.

**[S] Race re-apply.**
- Harmony postfix on `CharacterObject.InitializeHeroCharacterOnAfterLoad`. It runs after `BasicCharacterObject.InitializeHeroBasicCharacterOnAfterLoad` (IL_003c-0043) has copied the root template's race.
- For original characters, including main_hero, re-apply in `OnGameLoaded`. main_hero is re-deserialized from NPCCharacters with race 0.
- Use the PUBLIC `BasicCharacterObject.set_Race`.
- Key the record for every hero. `CharacterObject.CreateFrom` flattens origins to the root template, so the player's same-sex descendants would otherwise revert.

**[S] Record writers.**
- Postfix on `CharacterObject.UpdatePlayerCharacterBodyProperties`.
- Postfix on `BodyGenerator.SaveCurrentCharacter`.
- `CampaignEvents.HeroCreated`.
- Commit of a pre-campaign creator record at `OnCharacterCreationIsOver`.
- **[D:D5] Family race**, written by `FamilyRaceWriter` at the same point: the player's race goes to the race record of every creation-time family hero, and each family hero gets an appearance record.
  - StoryMode: main_hero_mother, main_hero_father, StoryModeHeroes.ElderBrother, LittleBrother and LittleSister. `StoryModeCharacterCreationCampaignBehavior.FinalizeParentsAndLittleSiblings` copies StaticBodyProperties, Weight and Build from the narrative mother_character and father_character to main_hero_mother and main_hero_father only (IL_00b7-IL_013c). It gives StoryModeHeroes.ElderBrother equipment, culture and a name, and LittleBrother and LittleSister only names. It never calls set_Race on any of them. The siblings' body source (their storymode_* templates) was not traced.
  - Sandbox: `CharacterCreationCampaignBehavior.FaceGenUpdated` gives the narrative characters the player's race (FaceGen.GenerateParentKey with PlayerCharacter.Race), but only as a native key. Whether Sandbox then builds family heroes from them was not traced (U29).

**[G:U19]** The runtime round trip is checked in the live round.

### 6.2 Identity plumbing

**[S] Scope.** Only four shipped methods set `AgentVisualsData.CharacterObjectStringId`:
- CharacterTableau.InitializeAgentVisuals;
- AgentVisuals.SetCharacterObjectID;
- MobilePartyVisual.AddCharacterToPartyIcon;
- NavalMobilePartyVisual.AddRaidPartyVisual.

New plumbing is therefore needed for:
- 2D thumbnails;
- MapConversationTableau;
- the save/load preview;
- the eight AgentVisuals.Create callers below;
- the final hop to native.

**[S] Missions.** `AppearanceMissionListener : IMissionListener`, registered with `Mission.AddListener` from a `MissionBehavior.OnBehaviorInitialize`.
- `OnEquipItemsFromSpawnEquipment` fires after `Agent.AddSkinMeshes`: `Agent.EquipItemsFromSpawnEquipment` calls it at IL_0158, then fires the listener at IL_0170 (merger check).
  - That method is AddSkinMeshes' only caller.
  - Creation type 2 skips the skin build but still fires the listener.
- Resolve identity as agent.Character → CharacterObject.HeroObject → record.
- Walk `agent.AgentVisuals.GetSkeleton().GetAllMeshes()`, matched by mesh name: head, face_eye_mesh, face_eyelash_mesh, face_mouth_mesh, hair, beard, eyebrow, body, hands. On each match:
  - apply `Material.CreateCopy` + `SetTextureAtSlot` + `Mesh.SetMaterial`;
  - set a neutral `Mesh.set_Color` tint.
- Shipped precedent: MissionScreen's own listener and `AgentVisuals.ApplyBannerTextureToMesh`.
- [G:U5]

**[S] Single-player 3D tableaus and map party icons.**
- Harmony postfix on the private `AgentVisuals.AddSkinMeshesToEntity`.
- It reads `_data.CharacterObjectStringIdData`, which IL_0124-0140 already resolves and discards.
- `CharacterViewModel.FillFrom` sets that field on 28 single-player bindings (CharacterDeveloper, Clan, Encyclopedia hero/unit, Inventory, Party, Tournament).

**[S] 2D thumbnails.**
- A postfix on `CharacterCode.CreateNewCodeString` appends `mnbl:<heroId>:<hash8>@---@` after the trailing separator.
- The vanilla `CharacterCode.CreateFrom(string)` reads the race as the substring up to the next separator, so it ignores the token (merger check, IL_0108-012d).
- A postfix on `CreateFrom(string)` parses the token into a `ConditionalWeakTable`.
- `CharacterThumbnailCreationData` sets RenderId = CreateNewCodeString() + suffix (IL_0053-IL_0079), so the cache is keyed per record.
- **The missing hop.**
  - `CharacterThumbnailCache.FillEntityWithPose` calls `CharacterSpawner.InitWithCharacter`. That method builds AgentVisualsData from CharacterCode fields only (BodyProperties, Race, equipment, colours) and never sets CharacterObjectStringId.
  - Fix: a prefix on `CharacterSpawner.InitWithCharacter` (or on `CharacterThumbnailCache.FillEntityWithPose`) looks up the token parsed for its CharacterCode and sets it as the identity, either directly or through `IdentityScope`. The AddSkinMeshesToEntity postfix then resolves the record.

**[S] Eight AgentVisuals.Create callers with no identity** (critic RefScan, av_create_callers.txt)

| Site | Create call IL | Identity source |
|---|---|---|
| SandBox.GauntletUI.CharacterCreation.CharacterCreationNarrativeStageView.SpawnHumanNarrativeMenuCharacter | IL_004e | pre-campaign creator record (MMR-178 a7) |
| CharacterCreationOptionsStageView.AddCharacterEntity | IL_015a | pre-campaign creator record |
| CharacterCreationReviewStageView.AddCharacterEntity | IL_0154 | pre-campaign creator record |
| CharacterCreationClanNamingStageView.AddCharacterEntity | IL_0124 | pre-campaign creator record |
| SandBox.GauntletUI.BannerEditor.BannerEditorView.AddCharacterEntity | IL_0142, IL_0210 | hero StringId |
| TaleWorlds.MountAndBlade.GauntletUI.GauntletBannerBuilderScreen.AddCharacterEntities | IL_01ab, IL_036e | hero StringId; with no campaign hero, [D:D22] |
| SandBox.GauntletUI.GauntletEducationScreen.RefreshSceneCharacters | IL_01c4 | hero StringId |
| TaleWorlds.MountAndBlade.GauntletUI.SceneNotification.GauntletSceneNotification.OpenScene | IL_0367, IL_0428 | StringId of each shown hero |

- These are the player's most visible views:
  - The narrative, options and review stages render with translucency and tessellation both on (RA-1), the highest-fidelity views of the player in the game.
  - Scene notifications show the player and family full-frame (marriage, birth, death).
- **Mechanism** (either is allowed):
  - one Harmony prefix per site that sets `CharacterObjectStringId`; or
  - a `[ThreadStatic]` `IdentityScope`, opened by a prefix and closed by a finalizer on each site. A prefix on `AgentVisuals.Create` copies the scope into its AgentVisualsData's `CharacterObjectStringId` when that field is unset.
- [G:U28] The scope route depends on the copied CharacterObjectStringId surviving each site's later `AgentVisuals.Refresh(bool, AgentVisualsData, bool)` calls (BannerEditorView ctor and RefreshShieldAndCharacterAux; GauntletBannerBuilderScreen.AddCharacterEntities and RefreshShieldAndCharacterAux; CharacterCreationClanNamingStageView.RefreshCharacterEntity through GetCopyAgentVisualsData), and on AddSkinMeshesToEntity being reached synchronously inside Create. Any site that drops the id gets a prefix on its Refresh call.

**[S] Remaining surfaces.**
- `MapConversationTableau.SpawnOpponentLeader` / `SpawnOpponentBodyguardCharacter`: a prefix sets `AgentVisualsData.CharacterObjectStringId`.
- Save/load preview [D:D18]. It is built by `BasicCharacterTableau.RefreshCharacterTableau` through `MBAgentVisuals.FillEntityWithBodyMeshesWithoutAgentVisuals` (IL_0339), from the `MainHeroVisual` metadata that `MainHeroSaveVisualSupplier.GetMainHeroVisualCode` writes. The two options:
  - (a) append the bake hash to that metadata, and have a postfix on RefreshCharacterTableau apply a disk-cached bake (Texture.SaveToFile / LoadTextureFromPath), with native fallback [G:U5];
  - (b) accept a native-only preview.
- Creator: postfix `BodyGeneratorView.AddCharacterEntity` and `RefreshCharacterEntityAux`.

### 6.3 Face, ear and eyelid keys

**[S] Shipped facts.**
- 63 keys per skin.
- KeyWeights[320].
- `<constraints><constraint proportional_negative_and_positive><term key_id coefficient/>` is native key coupling.
- Per-key attributes: `deforms_hair`, `helmet_scaling_factor_min/max`, `auto_activate_when_ears_are_hidden` (hide_ears at time point 51).

**[S] Merge order.** `MBObjectManager.CreateMergedXmlFile` applies module i's XSLT (IL_003e) to the accumulated document before merging module i's XML (IL_008a) (merger check). Consequences:
- every new-race skin in our skins.xml writes ALL its keys inline;
- the overhaul must load after DCC.

**Existing keys.**
- Keep shipped ids and time points 1-59.
- Re-author each frame on the new shared-topology heads to its name-matched DD2 family. The full table is in mapping row M01.
- Parents and angle sliders become `DependencyResolver` drivers.
- DD2 influences become constraint/term entries.

**[G:U1] New keys.** They use time points ≥83, are inserted before `weight` (before `build` on toddler skins), and appear in the same order on all 10 skins of every race:
- face: neck_fat, brow_narrow_wide, brow_inout_a, brow_inout_b, brow_straighten, nose_bridge_width, nose_bulge_a, nose_bulge_b, nose_tip_projection, chin_sharp (key_min 0), submental;
- eyelids: eye_squint_l, eye_squint_r, eye_close_l, eye_close_r;
- ears: ear_angle, ear_tilt, ear_open, ear_lobe, ear_elf (one-sided);
- lashes: lash_length.

**[G:U1, D:D14] People-delta keys.** Same placement rules. Each race skin declares its own peoples' keys, or every skin declares all of them.
- Face deltas: one key per non-base people. The three base peoples (human_heartland, elf, dwarf) carry empty deltas in heritage-appearance.json, which leaves up to 9:
  - human skins: human_maritime, human_desert, human_cold, human_large_island, human_rainforest;
  - elf skins: tidekin;
  - dwarf skins: gnome, hill_dwarf, frosthold_dwarf.
- Ear forms outside ear_elf's range: ear_gnome_jug (long_pointed_jug, dwarf skins) and ear_tidekin_fin (finned_low_swept, elf skins).
- Proportions with no existing key: leg_length (bone_scale; see section 4).
- `people_presets.json` sets each people's default weights. heads_tall (gnome 4.2, dwarf and frosthold_dwarf 4.6, hill_dwarf 4.9, humans and elves 7.1-8.1) comes from head_scaling plus height.
- Tidekin's nictitating eyelid is geometry on the tidekin delta, plus an animation channel separate from the blink; MMR-220 owns it and records its route; U30 probes the channel in sprint 1.

**Key budget per skin** (the input to U1 and MMR-165 a5):
- The DD2 mapping: 63 shipped + 19 DCC + 5 body + about 21 face/eyelid/ear/lash + non-collinear splits ≈ 108-140.
- People-delta keys: about 6 per skin if each race skin declares only its own peoples' keys (human 5 + 1, elf 1 + 1 + 1, dwarf 3 + 1 + 1), or 12 if every skin declares all.
- Optional head-style basis keys on top.
- Worst case about 120-152, inside U1's 180-key probe case.

**[G:U10] Asymmetric endpoints.** A `_neg` twin is added only for non-collinear endpoints. Collinear endpoints with unequal magnitudes use one signed key with asymmetric key_min/key_max.

**Fallbacks if U1 fails.**
- Path B: `MBAgentVisuals.SetFaceGenerationParams` [G:U2]. Its only caller is the public wrapper `AgentVisuals.SetFaceGenerationParams`, which nothing calls.
- Path C: a narrowing recorded in NEW-D.

**[G:U13] Helmet-hide safeguard.** A prefix on `Agent.AddSkinMeshes` zeroes ear_elf (and the people-delta ear keys) before `MBBodyProperties.ProduceNumericKeyWithParams(earsAreHidden=true)`. It is added only if the elf frame adds through ear-hiding helmets.

**Head style**: 38 presets per sex over the keys, plus a face_texture index. Optional hidden basis keys need U1 and D11.

### 6.4 Body and height

**[S] Body keys.**
- `<deform_key group_id=0><bone_scales><bone_scale bone_type axis key_min key_max/>` on biped bones.
- The DCC-compatible ids at time points 64-82 are guarded by `not(../deform_key[@id='TorsoBelly'])` in our human XSLT.
- New keys at ≥83: muscle_lower, neck_length, traps, pelvis_depth, arm_length, plus the people-delta leg_length [D:D14].
- People proportions: shoulder ratio uses the DCC Shoulders key, and hand ratio the DCC HandSize key, both set by people presets.
- DD2's asymmetric scale endpoints carry over exactly: key_min and key_max are independent.
- Arm length does not change reach (`Agent.GetArmLength` = Monster.ArmLength × AgentScale).
- [G:U16] for ranges beyond ±20% (including leg_length), and for `MBAgentVisuals.ApplySkeletonScale` as the per-agent fallback.

**[S] Height.**
- Uses the native height key (time point 62) through `MBBodyProperties.GetScaleFromKey`, applied in `AgentVisuals.Refresh`.
- No hip offset is needed (inferred).
- Race floors come from skin `min_scale` [D:D14]:
  - the dwarf race skins span gnome (female min 0.95 m) to the dwarf maximum;
  - the elf race skins span tidekin and elf;
  - each people's default and range sit inside its race's span.
- [G:U14] covers the native output range and combat at about 0.53 scale (the dwarf race skin's floor, the gnome female minimum) and about 0.58 (the gnome mean).

**[D:D9] Bust**: Size via biped_thorax y/z scale.

### 6.5 Catalogs and palettes (XML)

**[S] Per race skin**
- `<face_textures>`: 40 skin styles per sex, plus K troop prebake-palette entries. Selected by FaceGenerationParams.CurrentFaceTexture.
- `<mouth_textures>`: teeth tints × 4 presence states.
- `<hair_meshes><hair_mesh cover_type1..4><style_tags/>`: entry 0 is bald, and the list is identical across maturities.
- `<beard_meshes>`, `<eyebrow_meshes>`.
- Gradient points: `<skin_color_gradient_points>` (≥24, fantasy hues included), `<hair_color_gradient_points>` (24), `<eye_color_gradient_points>` (20), `<tattoo_color_gradient_points>` (12).
- `<tattoo_materials>`: the single troop marking slot.
- [G:U3] for index bit widths and how random draws honour more than 4 entries.
- Catalog sizes follow D11. Only the GUI-visible counts that round 2 reproduced are frozen. The counts for eye styles, lips, dirt and ear presets are user targets.

**[S] Population filtering**: `BodyPropertiesModel.Get{Hair,Beard,Tattoo}IndicesForCulture(race, …)` overrides.

### 6.6 Per-hero surface bake (DD2's Preview/Bake/Ingame split)

**[S] Inputs and outputs.** `AppearanceBakeService` composites original masks with the record's parameters into:
- head DiffuseMap (slot 0) and DiffuseOldMap (slot 1), both baked so aging keeps the makeup;
- NormalMap (slot 2: scars, pores, static wrinkle strength);
- SpecularMap (slot 4: x reflectivity, y gloss from per-layer roughness, z cavity);
- eye diffusetex: three iris colours, sclera, static iris/pupil size in alpha, plus a parallax height map;
- hair and beard DiffuseMap: root-to-tip gradient along UV-v;
- body and hands albedo/normal: arm and leg tattoos, body hair, muscle normal at strength.

**Tint and assets.**
- The native tint is neutralised with a reserved white gradient point or `Mesh.Color`.
- The original eye mesh gives each eye its own UV island, pre-compensating eye.rsh:11's fixed 1.2× remap. That makes heterochromia a bake (inferred).

**Textures and memory.**
- `Texture.CreateFromByteArray(byte[], int, int)` and `Texture.CreateFromMemory(byte[])` take no format or mip argument. The format, colour space and mips they produce are [G:U6].
- **Output format (owner MMR-171).** Two routes:
  - a runtime block-compression encoder (`BakeOutputEncoder`: BC7 for albedo and spec, BC5 for normal) feeding CreateFromMemory, used only if U6 shows that path binds correctly;
  - otherwise uncompressed RGBA8 through CreateFromByteArray, with an explicit budget: 5.33 MiB per 1024² map with mips, so about 48 MiB per hero for about 9 maps and about 1 GiB for 20 heroes; 21.33 MiB per 2048² map.

  No compression ratio enters any budget until the encoder exists and U6 passes.
- **Compositing backend** (chosen by MMR-163 a2): CPU compositing into a byte array, or GPU compositing through `Texture.CreateRenderTarget`.
- Player at 2048²; other heroes at 1024².

**Slot table, queue and fallback.**
- `BakeSlotTable`: LRU keyed by bake hash, with `Texture.Release` on eviction.
- `BakeQueue` (MMR-173 a7): bakes are queued at spawn and run within a frozen per-frame time budget. A hero shows its native face_texture entry until its bake lands. The sizing case is an army arriving with many lords.
- Optional persistence through `Texture.SaveToFile` / `LoadTextureFromPath`. The D18 save/load preview uses it.
- On failure, the bake falls back to the hero's native face_texture entry. DD2 has a Failed bake state.

**[S, G:U5] Troops** never bake. They use the palette, catalogs, the single tattoo slot, and shared muscle-normal material variants chosen by `hash(StaticBodyProperties)`. Assigning a variant is still a per-agent `Mesh.SetMaterial`, and whether that assignment persists is U5.

**[G:U5, U6]** Material override persistence and texture formats decide the whole hero route.

### 6.7 Shader route (gated)

**[G:U18, D:D8]** Scope is limited to four items:
- metallic and coloured face specular: a faceshader_high copy reading one extra baked metal channel, matching DD2's ALBD.a = sqrt(1-M);
- per-texel hair and beard gloss/sheen: hair_shader_aniso reading g_mesh_vector_argument;
- dynamic expression wrinkles;
- back-lit ear transmittance in missions.

**Routes.**
- The module cache is editor-only (MMR-163 a5(1)).
- The client runtime-compiles from `<install>/Shaders/Sources`, which cannot be shipped through mods.py. It is probed (MMR-163 a5(2)) only if D8 = (b).

**[G:U12, U17] Translucency and tessellation.** `SkinFlagTranspiler` matches an IL pattern, never fixed offsets:
- In the SkinGenerationParams::.ctor call (13 parameters), the pattern is: the two consecutive `ldc.i4.0` that load useTranslucency and useTesselation, then the single faceCacheID load, then the call.
- `Agent.AddSkinMeshes` today: IL_008e/IL_008f, ldarg.3 at IL_0090, ctor at IL_0091.
- `BasicCharacterTableau.RefreshCharacterTableau` (portraits) today: IL_0326/IL_0327, ldc.i4.0 at IL_0328, ctor at IL_0329.
- If the pattern is not found exactly once, the transpiler logs and leaves the method unchanged.
- **Adoption** (MMR-171 a6): if U12 and U17 pass, it ships for the player and named heroes inside a frozen distance and count budget, with frame time measured in the live round. DD2's own in-game skin SSS is a constant (0.12375), and its per-character translucency exists only in the creator. This is therefore a fidelity lift beyond DD2 gameplay parity, not a parity requirement.

### 6.8 Population

**[S] Seeds.** Postfixes on `Helpers.CharacterHelper.GetPartyMemberFaceSeed` and `BasicCharacterObject.GetDefaultFaceSeed` return 31 bits from (campaign seed, party StringId, troop StringId, rank).
- The vanilla value wraps at 2,000.
- The seed also picks gear through `Equipment.GetRandomEquipmentElements(new Random(seed))`.
- [G:U20] for native purity across the full range.

**[S] Face cache.** `TroopFaceBehavior : MissionBehavior` subscribes to `Mission.OnComputeTroopBodyProperties` and removes `MissionFaceCacheView.GetRandomBodyPropertyForTroop` in `OnBehaviorInitialize`.
- Four openers register the view: SandBoxMissionViews.OpenBattleMission, OpenSiegeMissionWithDeployment and OpenSiegeMission, and CustomBattleViews.OpenCustomBattleMission.
- Its race mismatch costs only +10 and its sex mismatch only +1000, against similarity thresholds of 100 rising to 5000 with fill. The replacement treats race AND sex as hard keys. It must be replaced before races ship.

**[S] Civilians**: a postfix on `LocationCharacter..ctor` applies the same widening.

**[S] Heroes**: `HeroCreationModel` overrides `GetStaticBodyProperties`, `GetCharacterTemplateForOffspring` [D:D5] and `GetRandomTemplateByOccupation`. Under D10 (a), `GetRandomTemplateByOccupation` also routes notables and wanderers created after campaign start to the people templates; MMR-219 first confirms statically that post-start creation reaches it.

### 6.9 Races, monsters, ages

**[S] Granularity [D:D14].** Three Bannerlord races, per s8 and FWGC++ (entity-rules.json, heritage-appearance.json): human, plus the new elf and dwarf races.
- Gnome is a dwarf people, and tidekin an elf people. Their heads and proportions are people-delta keys, bone_scale keys and presets over the race head and body (sections 3 and 4).
- Each Bannerlord skin carries exactly one face_meta_mesh and one body_meta_mesh, so there is no other place for per-people heads.
- A people becomes its own race only through a NEW-D choice, if U14 or U16 fails for it.

**[S] skins.xml.** Each new race (elf, dwarf) gets 10 `<race id>` skins:
- 5 maturities × 2 sexes;
- skeleton=human_skeleton, which reuses the 88 as_human* action sets;
- face_meta_mesh = the race head on the shared topology;
- min_scale per maturity, spanning its peoples' FWGC++ ranges;
- all keys inline: time points 1-63, DCC 64-82, and overhaul and people-delta keys at ≥83.

**[S] monsters.xml**: `<race>`, `_settlement`, `_settlement_slow`, `_settlement_fast` and `_child`, each with base_monster=human.

**Templates [D:D10].**
- Under D10 (a):
  - NPCCharacter `race=` goes on per-people lord, notable and wanderer/companion hero templates.
  - Notables and wanderers created after campaign start reach those templates through `GetRandomTemplateByOccupation` (section 8).
  - Children take family race through the race record [D:D5].
  - Troops and civilians keep the vanilla presentation culture's characters until the per-people content epic exists (user decision of 2026-10-15, peoples-epic comment cm_01m51aqjmdx8pv1210epc69eyq).
- Under D10 (b): per-people troop and civilian templates, plus rewiring of the nine spawn paths D10 lists, each with a census criterion.
- AgentBuildData.Race is ignored in single-player, so heroes need per-people templates either way.

**ProceduralWorld seam [D:D23].**
- (a) ProceduralWorld owns `IGeneratedHeroAppearance` (default race 0). `HeroBodies.Draw` and the CreateSpecialHero template selection in `GeneratedStartupBehavior.cs` consult it. Cost: one ProceduralWorld version bump, and one world-A nav-cache rebuild of about 6.5 min on the first campaign.
- (b) Harmony patches from this side; no ProceduralWorld file changes.

**Family race [D:D5].** Creation-time family takes the player's race (section 1). Later children take the same-sex parent's race through GetCharacterTemplateForOffspring and the race record.

**[G:U15]** Native acceptance of a second race.

**[D:D6]** Ages: the AgeModel getters take no character, so display-scaled mapping is recommended.

### 6.10 Creator

**[S]**
- A Harmony postfix on `BodyGeneratorView..ctor` releases the `FaceGen` movie and loads `MNBLCharacterCreator` with `CharacterCreatorVM(BodyGenerator, IFaceGeneratorHandler)`.
- Five DD2 tabs come from the frozen ledger. `DependencyResolver` holds the original influence and clamp rules.
- Vanilla affordances kept: Randomize/All, Undo/Redo, Reset/All, dressed toggle, voice selection and preview [D:D19], camera, race selector, Ctrl+C/V. Ctrl+C emits `<BodyProperties version=4>` plus a record element.
- Added: light presets and an idle/walk preview.
- The barber uses the same VM with `BarberCampaignBehavior.GetFaceGenFilter`.
- `ICharacterEditorHost.Open(EditTarget, EditContext)` is the host contract for the peoples and pawn epics.
  - It opens inside CharacterCreationState for vanilla Sandbox, StoryMode and the generated campaign.
  - The pending record is shown in every creation stage (section 2).
  - It commits that record and family race at `OnCharacterCreationIsOver`.

### 6.11 Caches

**[S]**
- `ThumbnailCacheManager.ForceClearAllCache` (shipped caller: MapScreen) after creator or barber commits.
- `TaleWorlds.MountAndBlade.FaceGen.FlushFaceCache` after catalog or head changes. It lives on TaleWorlds.MountAndBlade.FaceGen, not TaleWorlds.Core.FaceGen, and its only shipped caller is MissionFaceCacheView.OnMissionScreenFinalize.
- face_mesh_cache needs nothing: no shipped code reads it.

### 6.12 DCC coexistence

**[S]**
- Key ids and time points 64-82 are reserved.
- Every race skin declares the 19 ids inline.
- The overhaul loads after Additional Sliders.
- The overhaul race record wins over DCC's set_Race writes, applied in OnGameLoaded.
- [G:U21] for runtime load on v1.4.8.

### 6.13 Packaging and provenance

**[S]**
- `tools/ci/mods.py package MNBL.CharacterOverhaul`, and the races module if D16 splits.
- Asset packages are stored per [D:D15]. No committed file exceeds 100 MB.
- The RE Engine resource-magic scan runs repo-wide in `tools/ci/mods.py check` and over the staged files in `package`, both on the hosted runners.
- The DD2-extracted-hash scan runs only in the local pre-push hook. The release notes and docs/releases.md state that it cannot run on the GitHub-hosted release runner.
- A licence ledger covers MPFB, AssetHelper and kit inputs.
- DD2 endpoint, influence and clamp tables are study reference only. Rig values are authored originally. The ledger's location follows [D:D17], and DD2 captures stay outside the repo.

### 6.14 Live rounds and engine identity

- **[D:D12] Option 1:** 4 sprints and 4 live rounds.
  - Sprint 1: probes and the benchmark pair.
  - Sprint 2: freeze, the player-first slice and the user's visual checkpoint.
  - Sprint 3: population, races and peoples, wardrobe, performance.
  - Sprint 4: review, saves, packaging.
  - Editor imports and shader compiles run inside work tickets under CLAIM/RELEASE. The game launches only in acceptance rounds. If D12 disallows editor sessions in work tickets, every editor import and shader compile moves into its sprint's live round.
- **Stock-binary check.** Before every live round, the acceptance node confirms stock binaries:
  - `tools/Test-ApiPins.ps1` against `config/runtime-api-lock.json` (TaleWorlds.MountAndBlade, Core, CampaignSystem and the other pinned assemblies);
  - `bin/Win64_Shipping_Client/TaleWorlds.Native.dll` against the SHA-256 recorded in MMR-157 a1. The lock does not cover it, and the 2026-10-17 BattleScale deploy replaced engine DLLs in the shared install.

  If any binary is not stock, stop and ask the user; never restore or re-pin.
- **Transpilers** match IL patterns, never fixed offsets (section 7).
- **DD2 runtime captures** happen in one DD2 session the user approves (MMR-158 a2), outside every work ticket's acceptance.

## 7. Ticket changes (proposals; nothing saved)

Exact text to apply to saved revision rv_01m514ayx1pfg3qt05x6aae1zc. **Conditional on** names the decision a change depends on. Source lists the findings it comes from (§9).

### EPIC intent (append)

After 'not permission to silently narrow the result.' insert: 'A narrowing or deferral that the user chooses in the capability-decision record (NEW-D) and saves as a success-criterion edit is explicit, not silent.' At the end of the intent add a section '## Recorded decisions' holding the user's answers verbatim, with the date, in two groups: 'Before sprint 1' (D1-D4, D10, D12, D14, D15, D16, D17) and 'At the sprint-1 checkpoint' (D5-D9, D11, D13, D18-D23, recorded through NEW-D a4).

*Source:* PP-1, PP-4, PS-7, BE-3, PS-6; critic gap 'decisions buried in tickets'; ruling R9 — *Why:* Makes user-chosen narrowing legal, and records each decision group when it falls due (R9), so sprint-1 probes are not rerun.

### EPIC s1 (replace-sentence)

Replace the first sentence with: 'Every inventoried DD2 human appearance control (the 474 human-visible controls in 196 families from guicharaeditdata.user.3, DD2 build 25920212) has equivalent observable behavior in Bannerlord for every race, or a user-approved narrowing recorded in NEW-D, documented in a versioned traceability matrix from reference evidence through mechanism, persistence and passing tests; combinations and dependent controls pass.'

*Source:* DD2R-1, PP-1 — *Why:* Freezes the denominator; ties narrowing to the decision record.

### EPIC s3 (replace)

All character categories of every people, and all sex/maturity definitions of every race (the ten native human definitions and their elf and dwarf equivalents), use the production system wherever they render as engine Agents, in portraits and in menus; soldiers drawn by the battle-scale distant-figure layer carry the frozen distant-appearance subset (the soldier's race height and proportion scale (under D10 (a) its vanilla presentation-culture race; people scale only if D10 = (b)), sex, skin-palette tint) that this epic publishes. Aging, maturation, inheritance and population generation retain valid identities, the correct race and varied appearances within the frozen duplicate-appearance budget.

*Source:* PX-4, PX-1, PS-5; round-4 checker defect 4 — *Why:* Distant figures cannot carry the full system; variety needs a number. Conditional on D7. Under D10 (a) a soldier has no people, so the subset carries its race's scale; a people scale derived from the party's clan would change when the figure becomes a full Agent.

### EPIC s7 (replace-phrase)

Replace 'passes independent full option, asset, equipment, maturity, persistence and visual acceptance audits' with 'passes the epic's one review (MMR-197) and the final live round's option, asset, equipment, maturity, persistence and visual checks'.

*Source:* PP-5; round-4 checker defect 5 — *Why:* The plural independent audits conflict with the one-review-per-epic rule, the same wording that sprint-3 exit x1 carried before round 3 rewrote it.

### EPIC s8 (replace-last-sentence) — *conditional on D10 = (a)*

Replace 'The player, heroes, notables, companions and troops of a people use that race in creator, gameplay, portraits and saves.' with 'The player, heroes, lords, notables, wanderers/companions and children of a people use that race in creator, gameplay, portraits and saves, through the per-hero race record, per-people hero templates and the ProceduralWorld provider seam. Troops and civilians keep the vanilla presentation culture's characters, and their race, until the separate per-people content epic exists (user decision of 2026-10-15, peoples-epic comment cm_01m51aqjmdx8pv1210epc69eyq); that epic owns troop and civilian race.'

*Source:* PS-1; critic gap 'D10 contradicts a recorded user decision'; ruling R4 — *Why:* The recorded user decision keeps troops, scenes, buildings and clothing on the vanilla culture until a per-people content epic exists. Per-people troop templates would never spawn without rewiring the nine culture-keyed spawn paths; D10 (b) lists them, in case the user wants that scope here instead.

### EPIC s11 (replace)

The creator offers every race an unrestricted fantasy skin colour: a free colour for the player and named heroes (a per-hero bake input) and an authored per-race palette of at least 24 swatches including non-natural hues for generated characters. Each people's FWGC++ swatches, with their weights, are its default and population palette; chosen colours persist and render identically in creator, gameplay, portraits and every detail level.

*Source:* BE-3, DD2R-3 — *Why:* Free RGB cannot live in the native key; the bake makes it free for heroes. Conditional on D4.

### EPIC s13 (replace-last-sentence)

If no DCC release loads on the pinned game build, MMR-217 records the blocked gate with evidence and the user's choice in NEW-D applies; by default every race skin still declares DCC's 19 key ids at time points 64-82 and the runtime check moves to a follow-up epic.

*Source:* PP-1, BE-6 — *Why:* Removes the s13 dead end. Conditional on D13.

### EPIC rationale (append (keep existing sentence))

Cross-epic gates. Dark Mechanicus 'requires' is scoped to one epic, and prerequisites must sit in the same or an earlier sprint, so cross-epic dependencies are recorded as text gates in sprint entry criteria plus ticket references of kind 'ticket' (see EDGES). MMR-213 a2 reads FWGC++ kEntityIds at the producer.lock commit and also checks worldgen/policies/peoples.json once peoples MMR-204 delivers it; MMR-219 a3 requires peoples MMR-204 accepted; MMR-225 requires peoples MMR-207 accepted. Sprint-3 entry criterion n2 (Option 2: sprint 2c) enforces this. Battle-scale ep_01m55wvmvkkat0krzcb9bdefx9 consumes this epic's distant-appearance subset and owns per-soldier identity. Under D10 (a), troop and civilian race belongs to the per-people content epic that the user has not yet created. Conditional on D12 = Option 1: replace the 'Why three milestones' heading sentence and its 'Milestone 2 delivers ...; milestone 3 provides independent finished-product validation' with 'Why four sprints (D12 Option 1): sprint 1 probes and the benchmark pair; sprint 2 decisions, contract freeze and the player-first slice; sprint 3 population, races and peoples, wardrobe and performance; sprint 4 the epic's one review (MMR-197, which absorbs the race audit), saves and packaging.' Replace 'Sprint 2 adds race runtime' with 'Sprint 3 adds race runtime'. Delete 'Sprint 3 adds an independent race audit before packaging.' Replace 'Final visual, lifecycle and compatibility audits can prepare in parallel, serializing live runtime checks; packaging waits for all three.' with 'The one review (MMR-197) and the lifecycle/compatibility probe steps (MMR-198) prepare in parallel; packaging waits for both.' Replace '-> independent audits ->' with '-> the one review (MMR-197) ->'.

*Source:* PP-3, PS-5, PX-4; ruling R11 — *Why:* PP-3's change (d) is unnecessary: the 'no sibling dependency' sentence names the combat, world and creature epics and predates the peoples epic, so only a gate is missing. The create_ticket schema answers the old U26 (cross-epic edges): cross-epic 'requires' edges cannot be expressed. U26 is therefore removed from the unknowns.

### SPRINTS (restructure) — *conditional on D12 = Option 1*

Option 1 (4 sprints, 4 live rounds). Sprint 1 'Research, probes and benchmark': MMR-157 (its a4 is the static research doc), MMR-158 (absorbs MMR-162), MMR-159, MMR-160, MMR-161, MMR-163, MMR-164, MMR-165, MMR-213, MMR-214, MMR-215, MMR-216, MMR-217, MMR-218, NEW-B; acceptance MMR-129. Entry n2: 'D1-D4, D10, D12, D14, D15, D16 and D17 are recorded in the epic intent.' Exit: 'One live round ran every sprint-1 probe-step file; every unknown assigned to a sprint-1 probe has a recorded result (U18 part (2) is excluded: if D8 = (b), the sprint-2 live round runs MMR-163 a5(2), MMR-167 records the four shader items' route as provisional until then, and the D8 (c) narrowing applies if it fails; U23 only when the user approves the DD2 session); the benchmark captures, the boon table and the sprint-1-checkpoint decision list (D5-D9, D11, D13, D18-D23) are in the checkpoint bundle for the user's verdict.' Sprint 2 'Decide, freeze, player-first slice': NEW-D, MMR-166, MMR-167, MMR-168, then MMR-169, 170, 171, 172, 173, 175, 176, 177, 178, 181, 182, and NEW-C if D20 = (a); new acceptance node (one live round: import MMR-168 diagnostics, smoke-test the chosen capacity path, run every slice ticket's probe steps, and MMR-163 a5(2) if D8 = (b)). Its checkpoint is the user's visual verdict on production adult heads and on the player in every identity site of MMR-177 a1. MMR-129's covering criteria for MMR-166, MMR-167 and MMR-168 (a10, a11, a12) and a14 move to this node. Entry n1: 'Sprint 1 accepted; every unknown assigned to a sprint-1 probe has a recorded result (U18 part (2) is excluded: if D8 = (b), the sprint-2 live round runs MMR-163 a5(2), MMR-167 records the four shader items' route as provisional until then, and the D8 (c) narrowing applies if it fails; U23 only when the user approves the DD2 session).' Exit x3: 'Production manifests, numeric budgets, supported mode and identity policies are frozen, and sprint-3 work is expanded into bounded exact-ID batches.' Exit x2/x4: 'proven, or carrying a recorded user decision in NEW-D; a blocked gate without a decision blocks the sprint.' Sprint 3 'Population, maturity, hair, markings, races and peoples, wardrobe, performance': MMR-174, 179, 180, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194, 195, 196, 219, 220, 221, 222, 223, 224, 225, 226, 227; acceptance MMR-155. Entry n2: 'Peoples MMR-204, MMR-207, MMR-209 and MMR-211 are accepted, or the user has moved MMR-219 a3 and MMR-225 to a later sprint.' Entry n3: 'If D20 = (a), the user's NEW-C verdict is recorded.' Entry n4: 'The user's sprint-2-checkpoint verdict on production adult heads and the identity-site captures (approve, or listed changes) is recorded in the epic intent's Recorded decisions section; listed changes become bounded sprint-3 work.' (Option 2: n4 belongs to 2b.) Sprint 4 'Review, saves, packaging': MMR-197 (absorbs MMR-228), MMR-198 (absorbs MMR-199), MMR-200; acceptance MMR-156. Each MMR-155 covering criterion for a sprint-2 ticket moves to the sprint-2 acceptance node. Sprint-3 and sprint-4 entry n1 reads: 'Previous milestone accepted, with required mechanisms proven or carrying a recorded NEW-D decision, and production work broken into explicit bounded batches.' Current sprint-1 exit x1 stays on sprint 1. Sprint 2 adds exit x5: 'The player-first slice is integrated, and the sprint-2 live round's captures of production adult heads and of the player in every MMR-177 a1 identity site are in the checkpoint bundle for the user's visual verdict.' Current sprint-2 exits x1-x4 move unchanged to sprint 3. Current sprint-3 exits move to sprint 4, with x1 reading 'MMR-197's single review and the sprint-4 live round pass' and x2 reading 'The clean packaged module installs, runs and reloads correctly from the release pipeline.'

*Source:* PP-5, PP-2, PP-1, PP-3; critic gap 'ticket changes conflict with the lean rule'; rulings R8(a), R9, R11; round-3 checker defects 11 and 15; round-4 checker defects 0 and 3 — *Why:* D12 Option 1 meets the one-live-round-per-sprint rule with 4 rounds; the live-round count is the user's decision, not a structural necessity. A contract freeze still cannot consume its own sprint's live results, so it sits in sprint 2, after sprint 1's round. The same holds for user verdicts on a sprint's captures: they come at the checkpoint, after the acceptance node, so they gate the next sprint (NEW-D a5, entry n3) rather than the work ticket that wrote the capture steps. One consequence: sprint-2 production tickets need exact-ID batches created inside the sprint (MMR-167 a6). U27 asks whether a revision can be adopted mid-sprint, which round2.json does not answer. If D12 picks Option 2, the slices map as follows: 1a = Option 1 sprint 1; 1b = NEW-D, MMR-166, MMR-167 and MMR-168, with their own live round; 2a = MMR-169, 170, 171, 172, 173, 175, 176, 177, 178, 181, 182 and NEW-C if D20 = (a) (user visual checkpoint); 2b = MMR-174, 179, 180, 183-190; 2c = MMR-219-226 (entry n2); 2d = MMR-191-196, 227 (acceptance MMR-155); 3 = Option 1 sprint 4. Every 'sprint-1 checkpoint', 'sprint-2 checkpoint', 'sprint-3 entry n2' and 'sprint-3 entry n3' in these changes then reads 1a, 2a, 2c and 2b.

### ALL work tickets (global rule) — *conditional on D12 (rule (2)'s editor-session clause; rules (1) and (3)-(5) apply under every D12 answer)*

Work-ticket rule: (1) Each criterion requiring an in-game, in-engine, capture, cold-reload, mounted, siege, crowd or 'in game' result is reworded to '<the same check> is written as fail-soft probe steps and desk-checked; the sprint's acceptance node runs it.' This applies equally to criteria and bodies written in these ticket changes, including MMR-179 a7, MMR-181/182 a6, MMR-183 a5, MMR-196 a4, MMR-219 a3, MMR-222 a3, MMR-223 a1, MMR-226 a1 and a4, the MMR-197 body (and the MMR-228 body appended to it) and the MMR-200 body and a2; the review and packaging tickets cite live-round evidence instead of running the game. (2) Work tickets never launch the game. Editor imports and shader compiles run inside work tickets under the shared-install CLAIM/RELEASE protocol (decision D12). If the user's D12 answer disallows editor sessions in work tickets, every editor step moves into its sprint's live round: MMR-160 a1 and NEW-B a1 imports and MMR-163 a5(1)'s module shader-cache build (sprint 1), MMR-168 a4's imports, and every art ticket's imports. (3) Per-ticket checks are: it compiles, existing suites pass, and targeted assertions for new pure logic. (4) Delete evidence-receipt and hash clauses: MMR-161 a1 'content hashes permit drift detection', MMR-168 a1 'source/build hashes', MMR-200 a1 'version/hash manifests' (keep version manifest), MMR-213 a1 'and file hash' (keep the drift test), MMR-217 a2 'and hash'. (5) Rename every artifact path Modules/CharacterOverhaul/ to Modules/MNBL.CharacterOverhaul/, src/CharacterOverhaul/ to src/MNBL.CharacterOverhaul/, tests/CharacterOverhaul.Tests/ to tests/MNBL.CharacterOverhaul.Tests/, and 'HumanOverhaul' to 'MNBL.CharacterOverhaul'.

*Source:* PP-5, PS-4; round-3 checker defect 13; round-4 checker defect 1 — *Why:* Enforces the standing lean-verification rule; fixes module naming that fails tools/ci/mods.py.

### MMR-129, MMR-155, MMR-156 and the new sprint-2 acceptance node (Option 2: the 1b, 2a, 2b and 2c nodes) (body-replace + criterion-add)

Body: 'Before the live round, confirm stock engine binaries: tools/Test-ApiPins.ps1 passes against config/runtime-api-lock.json, and bin/Win64_Shipping_Client/TaleWorlds.Native.dll (not in that lock; the 2026-10-17 BattleScale deploy replaced engine DLLs in the shared install) matches the SHA-256 recorded in MMR-157 a1. If any binary is not stock, stop and ask the user; never restore, verify or re-pin it. Then run this sprint's one live round: every work ticket's fail-soft probe steps in one batch; fix all failures in one batch; confirm with one more run; a third run only for a genuinely new blocker. Cite accepted evidence instead of re-proving it; earlier sprints get a smoke test only. Shared install: CLAIM/RELEASE plus process and native-test.lock checks before each launch.' Delete 'Independently verify the combined milestone' and 'repeat affected checks after changes'. Each covering criterion reads: 'The live round ran <ticket>'s probe steps and its recorded result is attached; failures are fixed in the batch or routed to NEW-D.' New criterion on every acceptance node: 'The binary identity check passed before the round, or the round did not run and the user's answer is recorded.' MMR-155 a37 (moves with MMR-226): 'With DCC loading, each of its 19 keys roundtrips on every race; if MMR-217 recorded a blocked gate, the NEW-D choice is shown applied.' Delete MMR-129 a6 (covers MMR-162); MMR-129 a2 covers MMR-158, which absorbs it. Delete MMR-156 a3 (covers MMR-199); a2 covers MMR-198, which absorbs it. Delete MMR-156 a7 (covers MMR-228); a1 covers MMR-197, which absorbs it.

*Source:* PP-5, PP-1; critic gap 'live rounds have no engine-identity check'; ruling R6; round-3 checker defect 16 — *Why:* One live round per sprint, with no re-proving. Evidence taken on a patched engine is not evidence for the pinned host. The native DLL that answers U1-U22 is not guarded by the lock.

### NEW-B (sprint 1) (new ticket) — *conditional on D2 (art route); D12 (where the editor imports run)*

Title: 'Produce the benchmark head pair with the chosen art route'. Size medium; skills character-art, sculpting, texture-art, hair-art; preferences.autonomy supervised. Prereqs MMR-157, MMR-160; dependent MMR-167. Body: 'Using the route the user chose (decision D2), make one original adult male and one adult female head on the shared head topology from MMR-160, with eyes, teeth, a per-hero baked skin set (young and old albedo, normal, spec), one hairstyle with its cover variants and the expression set. Record measured hours per head and per hairstyle; MMR-167 sizes batches from them. Write capture steps framed to match MMR-158's DD2 reference views: creator face stage (translucency on, tessellation off), creator review stage (both on), dialogue, daylight and interior (both off). Editor imports run inside this ticket under CLAIM/RELEASE as D12 allows; if D12 disallows editor sessions in work tickets, they run in the sprint-1 live round.' a1: 'Both heads import through the editor with every LOD and pass MMR-160's channel table; hours per head and per hairstyle are recorded.' a2: 'Capture probe steps for the five views are written and desk-checked.' a3: 'The capture probe steps are written so that the sprint-1 live round's captures go into the checkpoint bundle for the user's verdict (sprint-1 exit); production art does not dispatch until NEW-D records that verdict.'

*Source:* PP-2, PS-3, RA-4, RA-1; ruling R8(e); round-4 checker defect 0 — *Why:* The earliest test of art throughput and DD2-class quality; it gives the user a quality judgment before the catalog is built. Its editor imports depend on D12. The user's verdict comes at the sprint-1 checkpoint, after the acceptance node that waits for this ticket, so NEW-D a5 records it and MMR-167 consumes it.

### NEW-D (sprint 2) (new ticket)

Title: 'Record the user's decision for every blocked or limited capability and every sprint-1-checkpoint decision'. Size small; workType architecture. Prereq: sprint-1 acceptance (MMR-129); dependent MMR-167. Body: 'For each sprint-1 result recorded as failed, blocked, limited or unresolved, show the user the evidence, the affected success criteria and four options with cost. The results in scope: key capacity path C, the per-hero bake route, the shader route, DCC runtime load, small-scale combat agents, second-race acceptance, and the gnome or tidekin people-delta route if U14 or U16 fails for that people. The options: (1) an alternative mechanism as bounded sprint-2 or sprint-3 work, including promoting a people to its own Bannerlord race under D14; (2) re-pin the game build; (3) move the requirement to a named follow-up epic; (4) accept a named narrower behaviour. Also present D5-D9, D11, D13 and D18-D23 with their options and recommendations.' a1: 'Every blocked or limited probe result has a decision record naming the user's chosen option, its evidence and the affected criterion ids.' a2: 'Each option-1 choice becomes bounded tickets in the production expansion; each option-3 or option-4 choice is applied as a success-criterion edit in a saved revision before MMR-167 starts.' a3: 'If DCC does not load on the pinned build, the user's s13 choice is recorded here.' a4: 'The user's answers to D5-D9, D11, D13 and D18-D23 are recorded verbatim with the date and copied into the epic intent's Recorded decisions section before MMR-167 starts.' a5: 'The user's sprint-1-checkpoint verdict on the NEW-B benchmark pair (approve, or listed changes) is recorded verbatim with the date before MMR-167 starts; the approved captures become the rubric's reference images in MMR-167.'

*Source:* PP-1, RA-3, BE-2, BE-5; rulings R3, R8(a), R9; round-4 checker defect 0 — *Why:* Gives every negative result a decision path. It records the checkpoint decision group (R9) and is the only route by which a people becomes its own race (R3). It also records the user's verdict on the NEW-B pair, which NEW-B itself cannot wait for.

### NEW-C (sprint 2) (new ticket) — *conditional on D20 = (a)*

Title: 'Produce the child and teen benchmark pair'. Skills character-art, sculpting, texture-art; preferences.autonomy supervised; D2 body gate. Prereqs NEW-B, MMR-167; dependents MMR-184, MMR-187. a1: 'A child and a teen head and body import on the production topology with their maturity skins (editor under CLAIM/RELEASE, per D12).' a2: 'Capture probe steps matching NEW-B's five views are written; the sprint-2 live round runs them.' a3: 'The capture probe steps feed the sprint-2 checkpoint bundle for the user's verdict; MMR-184 and MMR-187 do not dispatch until that verdict, recorded in the epic intent's Recorded decisions section (sprint-3 entry n3), approves the pair as their maturity rubric.' Edges added with the ticket: NEW-B->NEW-C, MMR-167->NEW-C, NEW-C->MMR-184, NEW-C->MMR-187.

*Source:* DD2R-4, PP-2; round-3 checker defect 11; decision D20; round-4 checker defect 0 — *Why:* D20 (a) recommends a child and teen benchmark pair as the maturity rubric, but no ticket produced it, no sprint held it and no checkpoint judged it. D20 is decided at the sprint-1 checkpoint, after NEW-B has run, so the pair is made in sprint 2 and judged at the sprint-2 checkpoint (Option 2: 2a). No DD2 reference exists for children or teens. The verdict comes after the sprint-2 acceptance node, so sprint-3 entry n3 gates MMR-184 and MMR-187 on it.

### MMR-157 (body + criteria replace)

Body: replace 'Inventory and launch-test the installed' with 'Inventory the installed'; replace 'Establish an isolated HumanOverhaul module' with 'Establish the isolated module Modules/MNBL.CharacterOverhaul (source src/MNBL.CharacterOverhaul), plus the races module if D16 splits it'; replace 'Identify the actual DCC sculpt/retopology/UV/texture/rigging capabilities and operator; unavailable art capability is a real dependency, not something code generation is assumed to replace.' with 'Record the art route the user chose (decision D2) and the operator and DCC toolchain it names.' a1: 'Record exact game, editor, enabled official DLC, REFramework, DCC and build tool versions and the build/import commands. Also record the SHA-256 of bin/Win64_Shipping_Client/TaleWorlds.Native.dll, which config/runtime-api-lock.json does not cover, once the user confirms the installed copy is stock (round 2 found its hash matches the one in evidence/runtime/map-scene-trace.json). The module load/unload smoke is written as probe steps for the live round; the probe steps include an official-launcher check with DCC absent that records whether optional="true" on DependedModuleMetadata is honoured (U32).' a2: 'config/mods.json has an MNBL.CharacterOverhaul entry (release:true, build tools/ci/Build-CharacterOverhaul.ps1), and a second entry for the races module if D16 splits it, and python tools/ci/mods.py check passes; Harmony, ButterLib, UIExtenderEx and MCM are declared Workshop dependencies, never bundled; zzCharacterCreation and zzCharacterCreation_AdditionalSliders are listed in KNOWN_DEPENDENCIES as optional.' a3: 'One named integration owner holds the shared editor/game session under CLAIM/RELEASE. The D2 record names the operator and toolchain; if no operator exists, this ticket records a blocking decision instead of passing.' New a4: 'docs/research/character-overhaul-findings.md commits the round-1/round-2 static results (hook map, race persistence, identity paths, face cache, colour representation, shader slot map and compile route, the DD2 474/196 ledger and surface pipeline) with their evidence paths, and lists each remaining native unknown with its probe.' New a5: 'tools/ci/mods.py check fails on any tracked file whose header carries RE Engine resource magic (the header signatures of every resource type the tools/dd2-reference generators read); a test writes a synthetic file with such a header to a temporary directory and shows the scan fires. A local pre-push hook (tools/ci/hooks/pre-push, installed by one documented command) fails on any file whose SHA-256 matches a file under DD2-modding-repo/local/extracted. docs/releases.md states that this hash scan cannot run on the GitHub-hosted runners, which have no extracted DD2 files.'

*Source:* PP-5, PS-4, RA-4, PS-3; critic gaps 'engine identity' and 'release path for art'; rulings R6, R7 — *Why:* Covers the REA-before-planning output, module registration and the art-operator gate, with no game launch. Also records the Native.dll reference hash that every live round checks (R6). Adds the repo-wide guard against DD2 resource files, which runs on the hosted check runner, plus the local-only hash guard (R7); both land in sprint 1, before MMR-158 touches extracted inputs.

### MMR-158 (absorbs MMR-162) (title + body + criteria replace)

Title: 'Commit the DD2 control ledger and mechanism map'. Body: 'Generate the ledger statically from natives/stm/appsystem/gui/userdata/guicharaeditdata.user.3 joined to appsystem/charaedit/ch000/edittable, editinfluencemap and editvalueclamp tables with the round-2 generators, committed under tools/dd2-reference/ with no DD2 data; extracted inputs stay in DD2-modding-repo/local/extracted, and the ledger itself is committed or kept local per D17. Map every family to its DD2 mechanism (joint TRS on the face-edit or body rig, part swap, bake input to ALBD/NRRA, in-game parameter, preset, unresolved). DD2 creator meshes carry no blend shapes (253 kit meshes checked; monsters ch21_004/ch22_000 do, so the detector is validated). DD2 captures are limited to one named DD2 session that the user approves, covering only the three runtime unknowns (key evaluation curve, face-rig transfer to attachments, bake timing) and the matched framing views NEW-B needs.' a1: 'A versioned ledger lists all 474 human-visible controls in 196 families (male 472, female 465; DetailTypes placement wins over SelfDetailParam, otherwise 197), each with tab/part, interface type, UI and table ranges, sex applicability, parent/child and preset links and DD2 mechanism, plus the 136 non-visible entries with their reason.' a2: 'Capture steps for the one user-approved DD2 session are written: Face_Nose_Length and Arms__0__Legs_Arms_Length at -100/-50/0/50/100 (key evaluation curve); a face edit with a beard and brow mesh equipped (face-rig transfer to attachments); the MaterialBakerController phases on edit (bake timing); and NEW-B's five matched framing views. The session runs once, when the user approves it; its results feed MMR-159 and MMR-167, and it is not part of this ticket's acceptance.' a3: 'Unresolved families (SkinWrinkeStrength, MuscleStrength, EyeSquint, CloseEye, TeethEnableUpper/Lower, the 4 Pretender controls) stay explicit entries; voice and BodyWeightData follow D19, and posture follows D9.' a4: 'DD2 creator affordances are recorded separately from controls: light/environment presets, history, idle/walk motion preview, preset create/select, Left/Right/Both edit scope, colour-preset widgets, storage/takeover.' Delete MMR-162 and re-point its edges to MMR-158.

*Source:* DD2R-1, DD2R-2, PX-2, PP-5; critic gap 'lean rule'; rulings R8(b), R8(d), R9 — *Why:* The inventory and the mapping are already done statically. DD2 runtime captures shrink to the three runtime unknowns plus NEW-B's framing views, taken in one user-approved session outside any work ticket's acceptance (R8 b). The DD2CCS rerun reproducibility clause is dropped (R8 d). The ledger's location and the voice applicability decision now have D-numbers (D17, D19).

### MMR-159 (body + criteria replace)

Body: replace 'maximum intended supported battle size' with 'battle sizes of 2,040 real agents (stock cap). The 4,080-agent case (battle-scale engine build) is written as separate probe steps, which run only in a session the user approves outside the stock-binary live rounds, because that build fails the acceptance node's binary check'. a1: 'Baseline scenarios are written as probe steps with settings, hardware, seeds, camera paths and troop counts; the live round runs them with repeat count and variance.' a2: 'The acceptance envelope states numeric limits for 2,040 real agents, and for 4,080 only if that separate session ran, per-hero baked-texture memory at 2048² and 1024², the maximum simultaneous unique baked heroes, and a duplicate-appearance budget per N same-template agents; any missing decision blocks the contract gate.' a3: 'Visual criteria are written against the capture specification in MMR-158 a2: views, framing, lighting, and each context's useTranslucency/useTesselation flags (face-generator stage true/false; narrative, options and review stages true/true; missions and portraits false/false), plus the global Tesselation option. DD2 images are attached when the user-approved DD2 session runs, and MMR-167 adds the NEW-B captures the user approves; neither is part of this ticket's acceptance.' Add edge MMR-158 -> MMR-159.

*Source:* PX-4, PX-1, RA-1, PP-2, PP-6, DD2R-3; round-3 checker defects 4 and 5; rulings R6, R8(b) — *Why:* Battle-size and appearance budgets need numbers; the creator out-renders gameplay by construction.

### MMR-160 (body + criteria replace)

Body: 'Import an original diagnostic head on ONE shared topology for all head styles, races and both sexes (DD2: 85 human heads share one 15,489-vertex topology with byte-identical index buffers), with face, face_eye_mesh (left and right eyes on separate UV islands that pre-compensate eye.rsh:11's fixed 1.2x remap), face_eyelash_mesh and face_mouth_mesh submeshes. Already established statically: 63 keys per skin by id and time point (54 visible in groups 1-4, 5 hidden, 4 body/system), group ids drive FaceGenVM sliders, deforms_hair and helmet_scaling are per-key attributes. Test only: editor import of more than 63 morph frames, whether native samples head frames at bone-only time points (60-63 and DCC 64-82), whether one signed key mirrors its negative half, whether beards and brows follow face keys through their own morph frames, and the LOD chain.' a1: 'The diagnostic head imports through the editor with more than 63 morph frames and every LOD; import settings are recorded.' a2: 'A channel table maps all 63 declared keys to their kept semantics on the new head.' a3: 'Probe steps cover blink/speech/expression, key extremes with a beard and brow mesh, one signed key at key_min and key_max, and a bone-only key at maximum against the neutral head; one diagnostic extra face bone or key is animated independently of the blink at idle and in dialogue (U30).'

*Source:* DD2R-2, RA-2, PP-5; round-3 checker defect 10 — *Why:* The channel semantics are answered statically; test only the native unknowns.

### MMR-161 (criteria replace)

a1: 'Every supported official equipment mesh/variant has a stable manifest record and owning production family; the single-player roster is 757 armor ids (SandBoxCore 667 + NavalDLC 90), or 1,254 if D3 adds multiplayer.' a2 append: '..., the SandBox barber (HairOnlyFaceGen), the save/load hero preview (BasicCharacterTableau fed by MainHeroVisual save metadata), map party icons, MapConversationTableau and the battle-scale distant-figure layer with its D7 disposition.' a3: 'Roster reconciliation fails on missing or duplicate required entries; every people's characters are in scope; sibling boundaries are listed by epic id (ep_01m55wvmvkkat0krzcb9bdefx9, ep_01m4gbt20bt35fw8yrxmq9p5c7, ep_01m513vch3mz0md6eqybvt1nmh).'

*Source:* PP-4, PP-5, PX-2, BE-2, PS-5 — *Why:* Drops the hash clause; adds the surfaces round 2 found; fixes the 'human-only' contradiction.

### MMR-163 (absorbs the RA-3 shader-route ticket) (body + criteria replace)

Body: 'Already established statically (cite docs/research): per-character face inputs are one per-mesh float4 block with one tattoo/scar slot; face specular is scalar and untinted (Face.rsh:68/93); one iris colour per eye mesh; hair albedo = texture x factor colour; translucency is on only in creator scenes and tessellation only in the narrative/options/review stages; no shipped shader sets the TRANSLUCENT define; the client runtime-compiles missing variants from <install>/Shaders/Sources and only the editor builds module caches. DD2 bakes every creator surface input into ALBD/NRRA (ALBD.a = sqrt(1-metallic)), and its in-game SSS is a constant. Texture.CreateFromByteArray(byte[], int, int) and Texture.CreateFromMemory(byte[]) take no format or mip argument. Test only the remaining unknowns.' a1: 'Probe steps: a per-agent Material.CreateCopy + SetTextureAtSlot override and a Mesh.Color override, applied from IMissionListener.OnEquipItemsFromSpawnEquipment, survive LOD switches, re-equip and AgentVisuals refresh; the same override applies in a postfix on AgentVisuals.AddSkinMeshesToEntity; SkinGenerationParams.faceDirtAmount 0 versus 1 is captured; the same override is applied by a postfix on BasicCharacterTableau.RefreshCharacterTableau to the entity built by MBAgentVisuals.FillEntityWithBodyMeshesWithoutAgentVisuals (the save/load preview, for D18); and one shared, uncopied body material variant is assigned to two troop agents with Mesh.SetMaterial and survives the same LOD, re-equip and refresh steps (troop muscle variants, mapping M25).' a2: 'Probe steps: record the format, sRGB handling and mips that Texture.CreateFromByteArray (raw RGBA8) and Texture.CreateFromMemory (BC7 and BC5 DDS) actually produce, bound at face slots 0/1/2/4, eye slot 0 and hair slot 0. Measure bake time and memory per hero at 1024² and 2048² for both compositing backends: CPU compositing into a byte array, and GPU compositing through Texture.CreateRenderTarget. The result chooses the backend and feeds MMR-171's output-format choice; U11: the diagnostic eye mesh with split islands carries a two-colour baked diffuse, captured in creator and mission.' a3: 'Probe steps: a transpiler forces useTranslucency/useTesselation true in Agent.AddSkinMeshes by matching the IL pattern, never fixed offsets: the two consecutive ldc.i4.0 that load those two bool arguments, then the single faceCacheID load, then the call to SkinGenerationParams::.ctor (13 parameters). Today these sit at IL_008e/IL_008f, with ldarg.3 at IL_0090 and the ctor at IL_0091. If the pattern is not found exactly once, the transpiler logs and leaves the method unchanged. Back-lit ear capture and frame time at the frozen battle sizes.' a4: 'A binding table classifies every DD2 surface family as shipped input, per-hero bake or shader change; only metallic/coloured face specular, per-texel hair gloss/sheen, dynamic expression wrinkles and mission ear transmittance are shader candidates; per-eye colour is a bake on the original eye mesh.' a5: 'Shader-route probe: (1) the modding-kit editor builds Modules/<test>/Shaders/D3D11 compressed_shader_cache.sack + shader_mapping.bin from a copy of faceshader_high that reads one extra baked channel, without modifying <install>/Shaders/Sources; (2) only if the user chooses D8 (b) at the sprint-1 checkpoint, the next live round records whether the client compiles a new shader name whose .rs is added, not replaced, under <install>/Shaders/Sources, with the runtime cache in C:\ProgramData\Mount and Blade II Bannerlord\Shaders cleared first and restored after. A failure of (1) goes to D8 and NEW-D.' Keep the edge MMR-158 -> MMR-163. Remove the edge MMR-163 -> MMR-165 (listed in EDGES).

*Source:* RA-1, RA-2, RA-3, DD2R-3, PP-5; critic gaps 'engine identity', 'lean rule' and 'bake pipeline'; rulings R6, R8(c), R10; round-3 checker defects 6 and 23; ruling R13; round-4 checker defect 3 — *Why:* Static answers replace most of the old probe, and the shader route is folded in. R6: the transpiler matches an IL pattern. The ruling said 'two ldc.i4.0 immediately before the ctor'; coord/addskin.il and rendering-verifier/scan_client.json refine this, because one faceCacheID load sits between them and the ctor call. R8(c): the Sources probe writes the shared install and clears the shared ProgramData cache, so it runs only if D8 = (b). R10: neither texture factory takes a format or mip argument, so the probe records what they produce and chooses CPU or GPU compositing.

### MMR-164 (criteria replace)

a1: 'Probe steps exercise bone_scale keys at twice the shipped ±20% range for arm length, leg length (the D14 people-delta leg_length key, across the FWGC++ leg_length_ratio span 0.42-0.52), torso, thorax and limb thickness on clothed and unclothed samples, and MBAgentVisuals.ApplySkeletonScale on a human agent.' a2: 'Probe steps capture mounted/combat/ragdoll at those extremes with collision and attachment overlays; Agent.GetArmLength (Monster.ArmLength x AgentScale) is logged to show visual arm length does not change reach.' a3: replace 'A concrete production strategy identifies reusable transformations, required corrective classes and unsupported mechanisms.' with 'The probe-step file states how the live round's a1-a2 results select the production strategy (reusable transformations, required corrective classes, unsupported mechanisms); MMR-166 and MMR-167 apply it in sprint 2.' Keep a3's second sentence.

*Source:* BE-5, DD2R-2, PP-5; round-3 checker defect 7; ruling R3 — *Why:* bone_scale keys are accepted in XML statically (DCC precedent); only ranges and reach remain.

### MMR-165 (body + criteria replace; prereqs reduced)

Body: 'Static: KeyWeights is 320 floats, the key is 8 ulongs, race is not in the key, catalog indices and colour offsets are packed natively; whether more than 63 keys pack losslessly is unknown. Write the capacity probe; the full composition matrix moves to MMR-170.' a1: 'Probe steps: a diagnostic skin declares 82, 120 and 180 deform keys; each KeyWeights[i] is set to 0, 0.37 and 1 through MBBodyProperties.ProduceNumericKeyWithParams and read back with GetParamsFromKey before and after a cold save/load; max error per key is logged.' a2: 'Probe steps: MBAgentVisuals.SetFaceGenerationParams (no shipped caller) with a weight beyond key capacity on a spawned agent and on a tableau; capture before and after.' a3: 'Probe steps: round trips of catalog indices 0..127 (hair, beard, eyebrow, face_texture, mouth_texture, tattoo) and of colour offsets at 1/1000 steps on a skin with 128 dummy entries; bit widths and quantization are recorded; U3's 500-troop index histogram and a high-eyebrow-index hero aged into a child skin; U20's keys for seeds s and s+2000k compared, and again after a restart.' a4: 'Unit test (no game): CharacterCode.CreateFrom(string) parses a code with an appended token field after the trailing @---@, and CreateNewCodeString round-trips it.' a5: 'The probe-step file states the selection rule the live round applies: its a1-a2 result selects path A (native packing), B (unpacked push) or C (reduced channels, decided in NEW-D), with the key budget counted per skin as: 63 shipped + 19 DCC (time points 64-82) + 5 body + about 21 face, eyelid, ear and lash + non-collinear asymmetric splits + the D14 people-delta keys + any head-style basis keys D11 allows. The people-delta keys are up to 9 face deltas (human_maritime, human_desert, human_cold, human_large_island and human_rainforest on human skins; tidekin on elf skins; gnome, hill_dwarf and frosthold_dwarf on dwarf skins), 2 ear-form keys (gnome long_pointed_jug, tidekin finned_low_swept) and at least 1 leg-length bone_scale key: about 6 per skin if each race skin declares only its own peoples' keys, or 12 if every skin declares all of them. The worst case of about 120-152 must fit within a1's 180-key case, or the shortfall goes to NEW-D.' Prereqs: MMR-157, MMR-160 only.

*Source:* BE-2, DD2R-2, PP-5; critic gap 'race granularity'; ruling R3; round-4 checker defects 2 and 3 — *Why:* Key capacity is the single decisive native unknown, so it gets its own probe. R3/D14: each skin has one face_meta_mesh, so people deltas are keys, and the budget the probe tests must count them; B1's 108-140 omitted them. The people list and the empty deltas of the three base peoples (human_heartland, elf, dwarf) come from heritage-appearance.json at the producer.lock commit.

### MMR-166 (criterion replace)

a4: 'Static resource and DCC inspection supports each proposed adaptation method; representative fit checks, including mixed-component outfits, are written as probe steps for the live round.'

*Source:* PP-5 — *Why:* No live run in a work ticket.

### MMR-167 (criteria replace + add; prereq add)

a1: 'Each of the 196 DD2 families (474 controls) maps to its route (native key, skins.xml catalog, XML palette, bone_scale key, agent scale, per-hero bake, shader change, creator-only data), schema representation, production owner and test, or to the user's recorded decision in NEW-D; no blocked required control is relabeled unsupported-and-done without that decision.' a2: 'Versioned schema, asset IDs, identity/migration policy, supported mode (D3), numeric budgets (including the duplicate-appearance budget, the baked-hero slot count and the bake output format and memory chosen in MMR-171), the visual rubric and the NEW-B benchmark pair carry the user's recorded approval.' a3: 'The breadth spec states, per family, DD2's GUI-visible reference count as verified in round 2 and the D11 target: head presets 38/sex, skin styles 40/sex, muscle normals 40/sex, hair 57 base (+21 extra, gating untraced), beards 44, brow masks 14 plus brow meshes 26, eyeliner 13, cheek 4, eyeshadow 5, freckles 5, body hair 4, eyelash 3, tattoos 66 base (+32 extra, gating untraced), scars 15. Eye styles, lips, dirt and ear presets (DD2 counts 3, 5, 3 and 7, all unverified) carry the user's own D11 targets. Troops use a bounded prebaked face_texture palette with a numeric variant count, and every art family has a bounded batch graph and the D2 route.' a4 append: '..., the ear-form family (MMR-222), the per-hero race record, family race (D5), race age mapping (D6), race granularity and the people-delta key set (D14), the per-people hero templates and the ProceduralWorld seam with its direction (D10, D23), the module split (D16), the save/load preview (D18), voice and BodyWeightData (D19), the child/teen quality bar (D20), hair physics (D21) and non-campaign modes (D22).' a5: 'A likeness corpus is frozen: all faction leaders and spouses, StoryMode named heroes, NavalDLC naval_lords leaders and a seeded 40-lord sample from SandBox lords.xml, with matched framing and a pass threshold.' New a6 (D12 Option 1): 'Before any sprint-2 production ticket dispatches, the exact-ID child batches for the sprint-2 slice are in a saved revision the orchestrator has adopted, sized from NEW-B's measured hours; the sprint-3 batches are saved before the sprint-2 checkpoint.' Prereqs add NEW-D and NEW-B. MMR-167 body (conditional on D12 = Option 1): replace the two sentences beginning 'Before this capability milestone closes, the planner expands the FUTURE production sprint' and 'Do not ... defer required batch creation' with 'The planner creates exact-ID child batches with coverage dependencies as a6 states: the sprint-2 slice batches before any sprint-2 production ticket dispatches (drafted at the sprint-1 checkpoint from NEW-B's measured hours if U27 shows adoption is checkpoint-only), and the sprint-3 batches saved before the sprint-2 checkpoint, where the orchestrator adopts them. Do not invent batch IDs before inventory.' Under Option 2 the original body stands.

*Source:* PP-1, PP-2, RA-2, DD2R-1, DD2R-4, PX-3, PX-5, PS-6, PP-6; rulings R4, R8(a), R9, R10, R14 — *Why:* Routes per family, decision provenance, numeric breadth and a likeness gate. R14: only the GUI-visible counts verified in round 2 are frozen as parity targets. R4/R9: the decision list now runs to D23. R10: the bake output format is a budget line. R8: under Option 1 the freeze and the first production slice share sprint 2, so batches must exist before dispatch inside the sprint; whether a mid-sprint revision can be adopted is U27.

### MMR-168 (criteria replace + add) — *conditional on D15 (a4 storage)*

a1: 'Tool versions and import settings for original diagnostic assets are recorded.' a4: 'Compiled AssetPackages are produced locally by the editor import and stored as D15 decides. Recommended: a release artifact outside git, whose URL and SHA-256 are pinned in config/character-overhaul-assets.json, and which tools/ci/Build-CharacterOverhaul.ps1 fetches into the stage on the GitHub-hosted runner (it has no editor). ModuleData XML is committed, no committed file exceeds 100 MB, and the RE Engine magic scan in tools/ci/mods.py check (MMR-157 a5) passes on the repository.' a5: 'tools/character-overhaul/rig_bake bakes the original face-edit rig's endpoints into morph frames on every head of the shared topology and transfers them to every beard, brow and lash mesh (DD2 uses a per-item wrap deformer); an offline check reports the linear-superposition error of key pairs in mm.' a6: 'An AssetHelper Bannerlord adapter with head and hair profiles exists, or D2 records not using AssetHelper.'

*Source:* PP-5, PS-4, DD2R-2, RA-4; critic gap 'release path for art'; rulings R7, R8(d) — *Why:* No reproducibility ceremony (R8 d). The release runner has no editor, GitHub rejects files over 100 MB and the repo has no LFS, so packages cannot simply be committed (R7, D15). Attachments must follow face keys.

### MMR-170 (criterion append)

a2 append: 'including the worst-case simultaneous state of race, ears, the skin-colour representation and the 19 DCC keys on every race (moved from MMR-165 a4).'

*Source:* PP-5, BE-6 — *Why:* Composition testing moves out of the sprint-1 capacity probe.

### MMR-171 (body + criterion)

Body: replace 'through the mechanism established by the rendering probe' with 'through the route frozen in MMR-167'. a1 append: 'Heroes use a per-hero bake (face albedo young and old, normal, spec; eye diffuse; hair and beard diffuse; body and hands) cached by appearance hash, with assign, rebake-on-edit and release-on-despawn, falling back to the native face_texture palette on failure; troops never trigger per-agent bakes; metallic is a shader-route item or the D8 narrowing. The bake's output format is fixed here from MMR-163 a2. Either this ticket owns a runtime block-compression encoder (BC7 albedo/spec, BC5 normal) feeding Texture.CreateFromMemory, used only if U6 shows that path binds correctly; or the bake stays uncompressed RGBA8 through Texture.CreateFromByteArray, with an explicit budget frozen in MMR-167 (about 48 MiB per hero at 1024² for about 9 maps; about 1 GiB for 20 heroes).' New a6: 'If U12 and U17 pass, the pattern-matched useTranslucency/useTesselation transpiler (MMR-163 a3, with the same pattern match in BasicCharacterTableau.RefreshCharacterTableau) ships for the player and named heroes inside a frozen distance and simultaneous-count budget recorded in MMR-167. The sprint's live round measures frame time at the frozen battle sizes with it on and off. If either probe fails, mission skin shading stays native.'

*Source:* DD2R-3, RA-2, PX-5; critic gaps 'bake pipeline' and 'mission skin shading'; rulings R5, R6, R10 — *Why:* Follows DD2's own architecture and keeps crowd cost bounded. R10: the memory budgets assumed compression that nothing produced, so this ticket owns either the encoder or the uncompressed budget. R5: mission skin shading gets a production owner if its probes pass. DD2's own in-game skin SSS is a constant (0.12375) and its per-character translucency exists only in the creator, so this is a fidelity lift beyond DD2 gameplay parity, not a parity requirement, and a failed probe needs no NEW-D entry.

### MMR-172 (criterion append)

a1 append: '..., including the race id of EVERY hero: the record lives in SyncData key mnbl_co_appearance_v1 (Dictionary<string,string> keyed by hero StringId) and is re-applied through the public BasicCharacterObject.Race setter after CharacterObject.InitializeHeroCharacterOnAfterLoad and, for main_hero, in OnGameLoaded; a unit test (no game) asserts that the record serializes the race id and re-applies it through set_Race for the player, a template-created hero and a player child born after a race change, and that hair/beard/tattoo indices stay within that race's catalogs; probe steps check the reloaded race after a cold save/load, and the sprint's live round runs them.'

*Source:* BE-1; round-4 checker defect 1 — *Why:* Race is not saved, and lineage flattening reverts descendants.

### MMR-173 (criterion add)

a6: 'Baked appearance textures are cached by appearance hash and released on teardown; the bake slot-table size is a frozen budget; the CharacterCode record token keys the thumbnail cache.' New a7: 'A spawn-time bake queue runs per-hero bakes within a frozen per-frame time budget taken from MMR-159's envelope; a hero renders its native face_texture fallback until its bake lands. Probe steps spawn a battle in which an army with many lords arrives at once, and log per-frame bake time, worst frame time and time to the last bake.'

*Source:* DD2R-3, BE-2; critic gap 'bake pipeline'; ruling R10 — *Why:* Bake lifecycle and portrait cache identity. R10: nothing scheduled bakes at mission spawn.

### MMR-174 (body + criterion)

Body append: 'Override points: HeroCreationModel.GetStaticBodyProperties, GetCharacterTemplateForOffspring, GetRandomTemplateByOccupation; BodyPropertiesModel.GetHairIndicesForCulture, GetBeardIndicesForCulture, GetTattooIndicesForCulture.' Body: replace 'final production distributions must be reviewed again with the complete original catalog' with 'the sprint's live round runs the final production distributions with the complete original catalog'. a2 append: '...; population draws use FWGC++ swatch weights as frequencies, and a seeded 10,000-draw test per people lands within ±1% of each weight.'

*Source:* BE-4, PS-6; round-4 checker defect 5 — *Why:* Supported override points; weights are frequencies. The final-distribution check runs in the live round, not as a second review inside a work ticket.

### MMR-177 (criterion replace + add)

a1: 'The context inventory names both native body entry points: MBAgentVisuals.AddSkinMeshes (callers Agent.AddSkinMeshes, AgentVisuals.AddSkinMeshesToEntity) and MBAgentVisuals.FillEntityWithBodyMeshesWithoutAgentVisuals (caller BasicCharacterTableau, the save/load preview). It lists every caller site with its identity source:
- mission agents: IMissionListener.OnEquipItemsFromSpawnEquipment;
- single-player 3D tableaus: AgentVisualsData.CharacterObjectStringIdData, already set from CharacterViewModel.CharStringId on the CharacterDeveloper, Clan, Encyclopedia, Inventory, Party and Tournament screens; map party icons likewise;
- 2D thumbnails: the CharacterCode token, plus a prefix on CharacterSpawner.InitWithCharacter (or CharacterThumbnailCache.FillEntityWithPose) that copies the parsed token into AgentVisualsData.CharacterObjectStringId, because InitWithCharacter builds its data from CharacterCode fields only;
- MapConversationTableau.SpawnOpponentLeader and SpawnOpponentBodyguardCharacter: a prefix;
- the save/load preview: per D18;
- eight AgentVisuals.Create callers, none of which sets CharacterObjectStringId:
  - CharacterCreationNarrativeStageView.SpawnHumanNarrativeMenuCharacter, CharacterCreationOptionsStageView.AddCharacterEntity, CharacterCreationReviewStageView.AddCharacterEntity and CharacterCreationClanNamingStageView.AddCharacterEntity (source: the pre-campaign creator record, MMR-178 a7);
  - BannerEditorView.AddCharacterEntity, GauntletBannerBuilderScreen.AddCharacterEntities, GauntletEducationScreen.RefreshSceneCharacters and GauntletSceneNotification.OpenScene (source: the shown hero's StringId; a site with no campaign hero follows D22).
Mechanism: either one Harmony prefix per site that sets AgentVisualsData.CharacterObjectStringId, or a [ThreadStatic] identity scope opened around each site's AgentVisuals.Create call, which a prefix on AgentVisuals.Create copies into CharacterObjectStringId. Either way, the AddSkinMeshesToEntity postfix resolves the record.' a6: 'ThumbnailCacheManager.ForceClearAllCache runs after creator or barber commits, and FaceGen.FlushFaceCache after catalog or head changes.' New a7: 'Probe steps capture the player with a non-default record (free colour and one tattoo layer) in each listed site: every character-creation stage, the banner editor and builder, the education screen, one scene notification, a 2D thumbnail, the map conversation and the save/load preview. Each shows the baked appearance or its recorded D18/D22 disposition.' Do not add the round-1 face_mesh_cache check.

*Source:* BE-0, BE-2, PX-3; critic gaps 'views that show the player' and '2D-thumbnail hop'; ruling R1; round-3 checker defect 9 — *Why:* Corrected hook map; face_mesh_cache has no consumer. R1: eight more AgentVisuals.Create callers show the player or family full-frame and set no identity. The narrative and review stages are among them, and they render with translucency and tessellation both on. The 2D route also lacked its last hop. Whether the copied identity survives each site's later AgentVisuals.Refresh calls, and whether AddSkinMeshesToEntity runs synchronously inside Create, is U28.

### MMR-178 (body + criteria add) — *conditional on D5 (family race) for the a7 family clause*

Body append: 'The creator replaces the FaceGen movie inside the vanilla BodyGeneratorView with CharacterCreatorVM over the same BodyGenerator and IFaceGeneratorHandler, organised in DD2's five tabs from the frozen ledger; one slider may drive one or two keys through DependencyResolver (original influence and clamp rules); surface controls run a 512² live bake and a full bake on commit; DCC's 19 keys appear in the Body tab.' a6: 'Every vanilla BodyGeneratorView/FaceGenVM affordance is kept (Randomize, Randomize All, Undo, Redo, Reset, Reset All, dressed toggle, voice selection and preview, camera rotate/zoom/ascend, race selector when FaceGen.GetRaceCount()>1, Copy/Paste), and lighting/environment presets plus idle/walk motion preview are added; the barber (HairOnlyFaceGen, BarberCampaignBehavior.GetFaceGenFilter) uses the same VM with a filter.' a7: 'ICharacterEditorHost.Open(EditTarget, EditContext) opens the editor for a new main hero inside CharacterCreationState (vanilla Sandbox, StoryMode and the generated campaign), an existing hero and a pawn identity; a record made before world registration commits at OnCharacterCreationIsOver. At that point, per D5 (family race), the player's race is written to the race record of every creation-time family hero, and each gets an appearance record. In StoryMode these are main_hero_mother, main_hero_father and StoryModeHeroes.ElderBrother, LittleBrother and LittleSister. StoryModeCharacterCreationCampaignBehavior.FinalizeParentsAndLittleSiblings copies StaticBodyProperties, Weight and Build from the narrative mother_character and father_character to main_hero_mother and main_hero_father only (IL_00b7-IL_013c). It gives StoryModeHeroes.ElderBrother equipment, culture and a name, and LittleBrother and LittleSister only names. It never calls set_Race on any of them. The siblings' body source (their storymode_* templates) was not traced. In Sandbox they are any family heroes built from the narrative mother_character and father_character; whether Sandbox creates such heroes was not traced in round 2 (U29). A unit test (no game) asserts that FamilyRaceWriter writes the player's race to each listed hero's record. Probe steps check after a cold reload that their race equals the player's, and the sprint's live round runs them.'

*Source:* PX-2, PS-2, BE-5; critic gap 'StoryMode family'; ruling R2; round-3 checker defects 0, 10 and 14 — *Why:* FaceGenVM cannot host layer editors or one-to-two-key drivers; vanilla affordances are kept; a creator host contract serves the peoples and pawn epics. R2: without this, StoryMode parents keep the human race while carrying elf-derived keys, and the siblings stay human.

### MMR-179 (criteria add)

a6: 'The appearance seed is 31 bits from (campaign seed, party StringId, troop StringId, roster rank) via Harmony postfixes on Helpers.CharacterHelper.GetPartyMemberFaceSeed and BasicCharacterObject.GetDefaultFaceSeed, never the mod-2000 value; a fixture of 3 parties x 1,500 agents of one troop, and of 22 troop types sharing one template, reports exact-duplicate full appearances (face and gear, which share AgentEquipmentSeed) within the frozen budget.' a7: 'The overhaul's MissionBehavior owns Mission.OnComputeTroopBodyProperties and removes MissionFaceCacheView's handler (registered by SandBoxMissionViews.OpenBattleMission, OpenSiegeMissionWithDeployment, OpenSiegeMission and CustomBattleViews.OpenCustomBattleMission; budget (CharacterDetail+1)x100; distance threshold 100 rising to 5000 with fill), or patches its comparison so race and sex are hard keys; distinct-face counts are reported with and without it.' a8: 'Civilians (LocationCharacter: hash(settlementId+characterId)+equipmentSeed) get the same seed widening.'

*Source:* PX-1, BE-4 — *Why:* Seed cap, gear twins, corrected face-cache facts, cross-race reuse risk.

### MMR-180 (criterion add)

a6: 'Offspring race follows the recorded D5 rule through a HeroCreationModel.GetCharacterTemplateForOffspring override and the race record; HeroCreator.DeliverOffSpring's same-race SilentAssert is non-fatal and is not relied on; race ages follow D6.'

*Source:* BE-5, BE-1, PS-6 — *Why:* Cross-race births proceed silently today.

### MMR-181, MMR-182 (criterion replace + add) — *conditional on D12 (sprint naming)*

MMR-181 a4, replace entirely with: 'Probe steps capture creator, dialogue, daylight, interior and ordinary-troop views at close and mid distances, framed to match the approved NEW-B benchmark pair; the sprint-2 live round runs them and checks them against the frozen visual rubric, and the captures go into the sprint-2 checkpoint bundle for the user's verdict (sprint-2 exit x5).' MMR-182 a4: the same text plus ', as for the male heads (MMR-181 a4)'. a6: 'The likeness corpus rendered on head_male_a/head_female_a and on production heads in identical framing passes the frozen likeness check; failures are corrected through config/character-overhaul/hero-likeness-overrides.json keyed by NPCCharacter id, never by editing native keys.'

*Source:* PP-2, PX-3; ruling R8(a); round-4 checker defect 0 — *Why:* The user judges quality, and existing lords keep their likeness. Sprint naming follows D12 Option 1 (under Option 2, this is the sprint-2a checkpoint). The verdict comes at the checkpoint, after these tickets are accepted, so a4 asks only for the capture steps.

### MMR-183 (criterion add)

a5: 'Ctrl+C emits a versioned code carrying native and extended values; pasting a vanilla <BodyProperties version=4 ...> code loads through the kept key ids and time points; an extended code pasted into vanilla fails visibly rather than silently dropping values.'

*Source:* PX-2 — *Why:* Players share vanilla face codes.

### MMR-184, MMR-187 (criterion edit) — *conditional on D20 (whichever option is chosen sets the rubric)*

a5: replace 'the frozen visual rubric' with 'the maturity rubric set under D20'. MMR-187 body: replace 'without maturity-specific review' with 'that does not pass the maturity rubric set under D20'.

*Source:* DD2R-4; round-3 checker defect 11; decision D20 — *Why:* The adult rubric has no child or teen reference. D20 sets the maturity bar; under D20 (a) it is the NEW-C pair the user approves.

### MMR-188 (criterion add)

a6: 'Every style supplies the cover_type1..4 helmet variants of the shipped schema (shipped: 66 male / 54 female distinct cover meshes for 28 / 21 styles) and a root-to-tip UV-v convention for the per-hero hair bake; acceptance weighting favours civilian and settlement contexts and the roughly 20% bareheaded troop loadouts.'

*Source:* RA-4, RA-2, PX-4 — *Why:* Cover variants and the root/tip bake.

### MMR-189 (criterion add)

a6: 'Beards, moustaches, brows and lashes carry the head's morph frames at the same time points, transferred at build time by rig_bake, with no floating at key extremes.'

*Source:* DD2R-2 — *Why:* DD2 uses wrap deformers; no deforms_beard attribute exists.

### MMR-190 (body sentence replace)

Replace 'do not substitute a baked single appearance where independent controls are required' with 'independent controls stay independently editable in the appearance record, and the per-hero bake recomposes them on every edit (DD2's own model)'.

*Source:* DD2R-3, RA-2 — *Why:* As written, this clause forbids the route DD2 itself uses.

### MMR-193 (criterion add)

a4: 'Each people has presets and population palettes carrying its FWGC++ features and grooming deltas; named heroes get authored appearance records rather than procedural output (DD2 authors 1,576 NPC and 176 human-enemy records, the latter about 22 per enemy entry).'

*Source:* PP-6, DD2R-3 — *Why:* People deltas otherwise go unbuilt; DD2's crowd is authored.

### MMR-196 (criterion add)

a4: 'Per-hero bake memory and slot count meet the frozen budget (reference: one DD2 runtime-baked character is about 16.5 MiB at mip0 in BC3; one Bannerlord face map at 2048² RGBA8 with mips is 21.33 MiB).' Body: replace 'before independent final audits' with 'before the epic's one review (MMR-197)'. a3: replace 'affected visual/equipment checks are rerun' with 'probe steps for the affected visual/equipment checks are updated, and the sprint's live round runs them'.

*Source:* DD2R-3, RA-2 — *Why:* Bake memory is a budget line.

### MMR-197 (absorbs MMR-228) (merge)

Title: 'Review control parity, visual quality and every people's race features (the epic's one review)'. Append MMR-228's body and criteria a1-a3 to MMR-197. MMR-228's a2 reads 'DCC support is verified with and without DCC, or the NEW-D choice is shown applied.' MMR-228's a3 reads 'The finding list has no unresolved required defect, and the final review bundle shows non-human heroes (and non-human troops only if D10 = (b)).' New criterion: 'Each people's authored features, grooming, attire and life-stage deltas from heritage-appearance.json are visible in matched captures of its heroes (and of its troops only if D10 = (b)); tidekin captures show the nictitating eyelid in motion.' Delete MMR-228; replace edges MMR-196->228, 225->228, 228->200 with MMR-225->MMR-197. Remove 'Record failures and require fixes/retest' in favour of the acceptance node's single fix batch.

*Source:* PP-5, PP-1, PP-6; critic gap 'people deltas'; rulings R4, R12 — *Why:* At most one review pass per epic, immediately before the final live round. R12: PP-6's people-delta audit was dropped when MMR-228 merged here, so without this criterion a people could pass looking like a recoloured, rescaled human. R4: troops carry their people's race only under D10 (b).

### MMR-198 (absorbs MMR-199) (merge)

Title: 'Write the final lifecycle, mode and compatibility probe steps'. Merge MMR-199's body and criteria; MMR-199 a3 reads 'Every people's characters are in scope; sibling contracts are checked by epic id with combined-run evidence only where builds exist.' Delete MMR-199; remove edge MMR-199->MMR-200. MMR-198 body: replace 'Independently test finished assets/catalogs' with 'Write probe steps that test finished assets/catalogs'. Merged MMR-199 body: replace 'test the combined load order' with 'the probe steps include the combined load order'. Work-ticket rule (1) also covers the MMR-198 body and criteria, with the merged MMR-199 body and criteria.

*Source:* PP-5 — *Why:* Both are verification tickets whose live runs belong to MMR-156.

### MMR-200 (artifact + body + criteria replace)

Artifacts: replace dist/CharacterOverhaul/ with 'tools/ci/mods.py package MNBL.CharacterOverhaul'. Body: replace 'performs final independent outcome verification' with 'runs the final live round'. a1: 'tools/ci/mods.py package MNBL.CharacterOverhaul produces a complete module with a version manifest and all required original assets, stable catalogs and runtime code.' a4: 'The RE Engine resource-magic scan (MMR-157 a5) passes on the repository in tools/ci/mods.py check and on the staged files in tools/ci/mods.py package, both on the hosted runner. The DD2-extracted-hash scan runs only in the local pre-push hook (MMR-157 a5), and the release notes state that it cannot run on the GitHub-hosted release runner, which has no extracted DD2 files. The editor-built asset packages are stored per D15, and no committed file exceeds 100 MB. A licence ledger covers MPFB, AssetHelper and kit inputs, and no DCC file ships.'

*Source:* PS-4, PS-3, PP-5; critic gap 'release path for art'; ruling R7; round-4 checker defect 5 — *Why:* Moves packaging onto the release pipeline. R7: the hash guard cannot run on the hosted runner and did not cover the repository. The magic scan now covers both the repository and the package, and the hash scan's local-only limit is stated. Provenance is already required per art ticket, so only licences were unowned. The acceptance node no longer performs a separate independent verification, so the body names its live round instead.

### MMR-213 (criteria replace)

a1: 'All 3 races and 12 peoples have height and mass by sex, proportions, ear form, life_stages (onset_years, height_fraction, max_years), and skin/hair/eye swatch lists with their weights in file order, each cited to source path and producer.lock commit; the drift test fails on change.' a2: 'People ids equal FWGC++ kEntityIds at the producer.lock commit (the source peoples MMR-204 reads); the test also checks worldgen/policies/peoples.json once that file exists.'

*Source:* PP-3, PS-6, PP-5 — *Why:* Unblocks sprint 1; life stages and weights were missing.

### MMR-214 (body + criteria replace) — *conditional on D14*

Body: 'Already established statically: race is not saved; after load every non-original hero takes its root template's race and main_hero is re-read from XML with race 0; single-player skins use Character.Race and ignore AgentBuildData.Race, while AgentBuildData.Monster is honoured; monsters are looked up as <race>, <race>_settlement, _settlement_slow, _settlement_fast and _child; base_monster=human reuses the 88 as_human* action sets; skins merge in managed code, and a module's XSLT is applied before that module's own XML merges. Races follow s8 and D14: elf and dwarf beside human, while gnome (a dwarf people) and tidekin (an elf people) are people deltas over their race's head and body, not races. Probe only: native acceptance of a second <race>; agent scale at FWGC++ min/mean/max, including gnome (about 0.53 at the female minimum and 0.58 at the mean) on the dwarf race skin; and combat, mounted, siege-ladder and conversation behaviour at those scales.' a1: 'Probe steps register diagnostic elf and dwarf races (no gnome race) through Modules/MNBL.CharacterOverhaul/ModuleData/skins.xml with 5 monsters each and NPCCharacter race=, and spawn each in creator, mission, portrait and dialogue, together with a gnome-scale dwarf and an elf carrying a diagnostic tidekin delta key.' a2: 'Probe steps log GetScaleFromKey, AgentScale and GetEyeGlobalHeight at each race's min, mean and max height key, with the dwarf skin's height range spanning the gnome minimum.' a3: 'Mounted, combat, siege-ladder and conversation checks at the gnome and elf extremes are probe steps; each failure names its limiting API or asset.' a4: 'Probe steps record the vanilla race after cold load for the player, a template-created hero and a player child born after a race change (expected: revert; cross-check the MainHeroVisual save-metadata race), then the same with a diagnostic race record restoring it.'

*Source:* BE-1, BE-5, PP-5; critic gap 'race granularity'; ruling R3; round-3 checker defect 8 — *Why:* Most of MMR-214 is answered statically, so the probes target the native unknowns. R3: s8 and FWGC++ have three races, with gnome a dwarf people and tidekin an elf people. The probe therefore tests them as deltas, and the gnome's scale on its race skin. A failure routes to NEW-D, which alone may promote a people to a race.

### MMR-215 (body + criterion replace)

Body: replace 'Prove how ... are built: race-specific head meshes, deformation channels, attachments or a mix. Prove the creator can edit ear shape (length, tip, angle, lobe and form) per race' with 'First build pointed ears as deformation of the shared ear: DD2 builds elf ears from one visible slider driving 3 hidden children (28 joint entries on 10 ear joints, l/r_ear_flapA-E; Length and Up/Down clamped -45..145). Bannerlord route: a one-sided ear_elf deform key per head, each race head's own base ear sculpt, and hide_ears (time point 51) re-authored per head. Prove separate routes only for forms outside that range (gnome long jug, tidekin fin), as people-delta ear keys over the dwarf and elf race heads (D14). Creator controls follow DD2's human set: Size, Angle, Open, Tilt, Elf, Lobe and an ear-preset selector (DD2's 7-entry count is unverified; the D11 target applies).' a2: 'Probe steps cover hide_ears auto-activation with ear_elf=1 under each helmet cover type, with and without zeroing ear_elf before ProduceNumericKeyWithParams(earsAreHidden=true), and back-lit ear transmittance with the MMR-163 translucency transpiler.'

*Source:* DD2R-2, RA-1, DD2R-4; rulings R3, R14 — *Why:* DD2 data fixes the mechanism; the open question is helmet interaction. R3: gnome and tidekin ear forms are people-delta keys, not race heads. R14: the ear-preset count of 7 was not reproduced, so it is not frozen.

### MMR-216 (body + criteria replace)

Body: 'Native skin colour is one scalar offset along a per-skin authored RGB gradient (24 skin points per shipped skin; the shipped tattoo gradient already holds blue and purple). Route per D4: each race skin authors skin_color_gradient_points from the union of its peoples' swatches, including non-natural hues; free colour for the player and heroes is a per-hero bake input with the native tint neutralised (a reserved white gradient point or a Mesh.Color override).' a1: 'Probe steps render blue, green, purple and grey on head and body via gradient points and via the per-hero bake in creator, gameplay, portraits and every LOD.' a3: 'The probe-step file names MMR-165 a3's colour-offset round trip as the source of the offset quantization, which the sprint-1 live round records.'

*Source:* BE-3, DD2R-3 — *Why:* Splits authored palette and free colour.

### MMR-217 (criteria replace + add)

a1: 'Static binding is resolved (round 1 Cecil: Common 123/123, v1.3.13 153/153 against v1.4.8; the loader selects v1.3.13); probe steps for runtime load of DCC and the add-on in an isolated profile are written for the live round; if no release loads, the blocked gate goes to NEW-D.' a2: drop 'and hash'. a4: 'Human-skin key ids TorsoBelly…HandSize at time points 64-82 are reserved for Additional Sliders; overhaul keys use time points 83 and up; every non-human skin declares the same 19 ids before weight (before build on toddler skins) inline in its own skins.xml; MNBL.CharacterOverhaul loads after zzCharacterCreation_AdditionalSliders, because a module's XSLT runs before its own XML merges and DCC's XSLT targets race human only.'

*Source:* BE-6, PP-5 — *Why:* Data-ownership collisions, not binding, are the risk.

### MMR-218 (criterion append)

a2 append: '..., and names which culture field its hook reads (people culture or presentation culture), if peoples MMR-209 has not yet documented its field decisions, the choice is marked provisional and MMR-225 rechecks it against them.'

*Source:* PS-1 — *Why:* Shared presentation cultures.

### MMR-219 (body + criteria) — *conditional on D10 = (a); D23 (seam direction)*

Body: replace 'heroes, notables, companions and troops of each people' with 'heroes, lords, notables, wanderers/companions and children of each people'. Body append: 'Single-player ignores AgentBuildData.Race, so every people needs people-specific lord, notable and wanderer/companion CharacterObject hero templates with Race set; children take family race through the race record (D5, MMR-180). Under D10 (a), no troop or civilian templates are created here: troops and civilians keep the vanilla presentation culture's characters until the per-people content epic. The ProceduralWorld seam (D10; direction per D23): src/MNBL.ProceduralWorld/Campaign/HeroBodies.cs passes FaceGen.GetRaceOrDefault(raceId) from an IGeneratedHeroAppearance provider that the character module registers (default race 0), and the CreateSpecialHero template selection in GeneratedStartupBehavior.cs picks the people template; agreed with the FWGC epic owner. If D23 chooses the module-side patch instead, the same two behaviours come from Harmony patches in this module, and no ProceduralWorld file changes. Notables and wanderers created after campaign start reach the people templates through the HeroCreationModel.GetRandomTemplateByOccupation override that MMR-174 lists; this ticket first confirms statically that post-start notable and wanderer creation reaches that override, or names the path it patches instead (D10 (a)).' a1: 'Every people resolves to its race and delta for the player, heroes, lords, notables, wanderers/companions and children. Probe steps for a census over a generated world check that no such character has the wrong race and report troops and civilians on their vanilla presentation-culture race; the sprint's acceptance node (MMR-155 a30) runs them. Probe steps also force one notable and one wanderer to spawn after campaign start in a people-owned settlement and check their race.' a3: 'Race is derived from the people saved by peoples MMR-211, persists through cold reload through the MMR-172 record, and the people registry's race slots are filled (precondition: peoples MMR-204 accepted, else this ticket reports blocked).' MMR-155 a30 (covers MMR-219) reads: 'Run the race census over a generated world for the player, heroes, lords, notables, wanderers/companions and children, including the notable and wanderer forced to spawn after campaign start, switch people in the creator, then cold-reload; confirm every such character's race, troops and civilians on their vanilla race, and no leaked values.'

*Source:* PS-1, BE-5, PS-2, PP-3; critic gap 'D10 contradicts a recorded user decision'; rulings R4, R9 (D23); round-3 checker defects 2 and 14 — *Why:* The census can only pass if the creation paths carry race. R4: the recorded user decision (cm_01m51aqjmdx8pv1210epc69eyq) keeps troops on the vanilla culture until a separate epic exists, and per-people troop templates would never spawn without rewiring the spawn paths D10 (b) lists. Under D10 (b), this change is replaced by troop and civilian templates plus a census criterion per spawn path.

### MMR-220, MMR-221 (criteria + body)

Append the Batch rule paragraph used by the other human art tickets. a1 append: '..., including the people's FWGC++ features deltas.' a4: 'Every child batch the contract created for this family is integrated; closure cannot pass on a representative sample.'

*Source:* PP-6, RA-4 — *Why:* Bounded batches and people deltas were missing.

### MMR-220 (body append + criterion add)

Body append: 'This ticket owns tidekin's nictitating eyelid (heritage-appearance.json eye_shape large_dark_nictitating: a translucent third lid that flicks across the eye sideways): its geometry on the tidekin head delta, and an animation channel (a face-rig bone or morph frame, chosen and recorded here) that drives it independently of the blink.' New a5: 'The tidekin nictitating lid has authored geometry and a named animation channel, and probe steps capture it crossing the eye once at idle and once in dialogue.'

*Source:* PP-6; critic gap 'people deltas'; ruling R12; round-3 checker defect 10 — *Why:* R12: the player-experience handoff for tidekin's nictitating eyelid had no owner. Its route (bone or morph) is unproven in Bannerlord, so this ticket records its choice; U30 probes the route in sprint 1 (MMR-160 a3), and a failure goes to NEW-D.

### MMR-220, MMR-221 (criterion edit) — *conditional on D10 = (a)*

a1: replace 'on heroes and troops' with 'on heroes, and on troops only if D10 = (b)'.

*Source:* PS-1; rulings R4, R12 — *Why:* Under D10 (a), no troop carries an elf or dwarf race, so a troop clause would be unpassable.

### MMR-220, MMR-221 (body append) — *conditional on D14*

Under D14 (a), the elf and dwarf race heads and bodies are the only base meshes. Tidekin (MMR-220), and hill dwarf, frosthold dwarf and gnome (MMR-221), are authored as people-delta morph frames, ear keys, bone_scale presets and textures over their race's head and body, not as separate heads or bodies. The races/tidekin/ and races/gnome/ source folders hold those deltas. A people gets its own head and body only if NEW-D promotes it to a race.

*Source:* PP-6; round-3 checker defect 19; ruling R3 — *Why:* MMR-221 still asked for separate gnome and people heads and bodies, and MMR-220 named a separate tidekin frame, which contradicts D14 (a) and MMR-227 (ruling R3).

### MMR-222 (body + criterion replace)

Body: replace the control list '(length, tip, angle, lobe and form)' with 'DD2's human set: Size, Angle, Open, Tilt, Elf, Lobe and an ear-preset selector, with per-race additions recorded in the contract'. a3: 'Pointed ears pass the reference headgear set from MMR-215; full supported-headgear compatibility is handed to MMR-227 with explicit ear-form ids.' Append the Batch rule paragraph.

*Source:* PP-6, DD2R-2 — *Why:* Ear-headgear ownership was triplicated.

### MMR-223 (criterion replace)

a1: 'Any colour can be chosen for the player and named heroes (per-hero bake), and any of the race's authored swatches (at least 24, including blue, green, purple and grey) for generated characters; it persists through cold reload and appears identically in gameplay, portraits and every LOD.'

*Source:* BE-3 — *Why:* A free picker cannot live in the native key. Conditional on D4.

### MMR-224 (body append)

'DD2 height is a uniform skeleton scale (x0.778-1.193) plus a Hip translate that keeps the feet grounded; Bannerlord's frame scale (AgentVisuals.Refresh x MBBodyProperties.GetScaleFromKey) scales about the agent origin, so no hip offset is needed (inferred, probed in MMR-214). FWGC++ dwarf and gnome means (0.58-0.72 of 1.80 m) are below DD2's floor, so race base bodies and skin min_scale carry them.' a3 append: '..., and race persists through the MMR-172 record.'

*Source:* DD2R-2, BE-5 — *Why:* Height mechanism and race floors.

### MMR-223, MMR-224 (body + criterion edit) — *conditional on D10 = (a)*

MMR-223 body: replace "its population palette for heroes and troops" with "its population palette for heroes (and for troops only if D10 = (b))". MMR-224 body: replace "Generated heroes and troops of each people sample within min–max" with "Generated heroes of each people (and troops only if D10 = (b)) sample within min–max". MMR-224 a3: replace "Hero and troop generation use people heights" with "Hero generation (and troop generation only if D10 = (b)) uses people heights".

*Source:* PS-1; round-3 checker defect 3; ruling R4 — *Why:* Under D10 (a), no troop belongs to a people (ruling R4).

### MMR-225 (body + criterion)

Body append: 'Precondition: peoples ep_01m513vch3mz0md6eqybvt1nmh MMR-207 accepted; otherwise this ticket reports blocked.' a2: 'Each people's boon text is supplied through the people registry's boon slot and matches the effect values; peoples MMR-207 renders it on the creator selection.'

*Source:* PP-3, PS-2 — *Why:* Single owner for the boon display.

### MMR-226 (criteria replace + add)

a1: 'With DCC and Additional Sliders installed and loading, each of their 19 keys is editable in the new creator's Body tab and in DCC's editors and changes its intended region on every race; if MMR-217 recorded a blocked gate, this criterion follows the user's NEW-D choice.' a4: 'Precedence between DCC's troop BodyPropertyRange/tag overrides (SyncData dcc_propertiesMin/Max/tags) and DCC's BasicCharacterObject.set_Race writes, versus the overhaul's population and race records, is defined (the overhaul race record wins, applied in OnGameLoaded after DCC) and tested in both load orders.'

*Source:* PP-1, BE-6 — *Why:* Removes the sprint-2 dead end; data-ownership collision.

### MMR-227 (body + edge) — *conditional on D14*

Append the Batch rule paragraph and: 'The contract expands this family into per-race exact-ID batches (elf race and dwarf race), and each race's batch set closes over its own batches. Gnome and tidekin fit within their race's batches through the people-delta proportion keys (D14); a people gets its own batches only if NEW-D promotes it to its own race after U14 or U16 fails.' a4: 'Every child batch is integrated; closure cannot pass on a representative sample.' Add edge MMR-222 -> MMR-227.

*Source:* PP-6, RA-4; ruling R3; round-3 checker defect 20 — *Why:* 748 armor meshes x race bodies cannot be one medium ticket. R3: three races, per s8.

### Race tickets MMR-214 to MMR-217 and MMR-219 to MMR-227 (artifact paths) — *conditional on D16 = (b) split*

If D16 splits the modules: the race skins.xml (elf and dwarf races, all keys inline, including DCC's 19 ids), monsters.xml, the people hero templates, Races/RaceRegistry.cs and the ProceduralWorld provider registration move to Modules/MNBL.CharacterOverhaul.Races/ (source src/MNBL.CharacterOverhaul.Races/, tests tests/MNBL.CharacterOverhaul.Races.Tests/). That module is registered in config/mods.json (release:true) and depends on MNBL.CharacterOverhaul. MMR-214 a1's diagnostic races register there. The core keeps the creator and its controls, the runtime, the appearance record (with its race field), the bake, identity plumbing, human skins.xslt and DCC support for human skins. Rename the race-specific artifact paths in these tickets accordingly. MMR-215's race ear keys and sculpts, MMR-216's race skin gradient points and MMR-217 a4's non-human DCC declarations live in the races module. ModuleData/character_overhaul/people_presets.json and Races/PeopleDeltas.cs also live in the races module, because peoples come from the peoples epic, which the core does not depend on. The human peoples' delta keys are declared in the core's human skins.xslt, because they sit on the core's human skins, and the races module's presets drive them there.

*Source:* PS-4; critic gap 'decisions buried in tickets'; ruling R9 (D16); round-3 checker defect 21 — *Why:* Applies the D16 recommendation. Module paths must be fixed before sprint 1, because MMR-157 registers the modules and MMR-214 registers the diagnostic races.

### Art tickets MMR-175, 176, 181, 182, 184, 185, 187, 188, 189, 190, 191, 192, 220, 221, 222, 227, NEW-B, NEW-C (if D20 = (a)) (capability + body)

The capability.tools enum is closed (repo_read, repo_write, shell, browser, test_execution, network), so keep it unchanged and express the art requirement through: the art skills (kept); preferences.autonomy = 'supervised'; capability.constraints.environments (free text) naming the operator and DCC toolchain recorded under D2; and a body gate. Add to each body: 'Dispatch requires the operator and DCC toolchain recorded under decision D2 in MMR-157 a3; without them this ticket reports blocked.'

*Source:* RA-4, PS-3; ruling R15; round-3 checker defects 10 and 11 — *Why:* RA-4's capability.tools edit cannot be saved. The schema offers skills, autonomy, constraints.environments and body gates instead. Whether dispatch matching reads constraints.environments is unknown (U31), so the body gate stays.

### EDGES (add/remove + references)

Add: MMR-158->MMR-159, MMR-157->NEW-B, MMR-160->NEW-B, NEW-B->MMR-167, NEW-D->MMR-167, MMR-175->MMR-220, MMR-175->MMR-221, MMR-180->MMR-220, MMR-180->MMR-221, MMR-222->MMR-227, MMR-225->MMR-197. Remove: all MMR-162 edges (merged into MMR-158), MMR-162/163/164/214/215/216/217->MMR-165, MMR-196->MMR-228, MMR-225->MMR-228, MMR-228->MMR-200, MMR-199->MMR-200. Cross-epic: add no 'requires' edges ('requires' is scoped to one epic); add ticket references of kind 'ticket' instead: MMR-213 -> peoples MMR-204; MMR-218 -> peoples MMR-209; MMR-219 -> peoples MMR-204 and MMR-211; MMR-225 -> peoples MMR-207. The MMR-219 and MMR-225 gates are enforced by sprint-3 entry criterion n2; MMR-213 a2 and MMR-218 a2 are written to pass without the peoples tickets.

*Source:* PP-6, PP-2, PP-5, PP-3; ruling R11 — *Why:* Fixes graph defects and applies the merges. R11: cross-epic dependencies cannot be 'requires' edges; references record them without gating.

### Other epics (the user applies; not editable from this epic) (cross-epic edits)

Peoples ep_01m513vch3mz0md6eqybvt1nmh: MMR-205 a3 reads '...appearance, including the overhaul's extended record, race and height'; MMR-212 new a4 'With the character module installed, DiagnosticSandboxBootstrap accepts the overhaul creator stage and Hero.MainHero in MapState carries the chosen non-default appearance (elf, max height, blue skin)'; MMR-211 owns the player's people in saves; s2, MMR-205 a1 and MMR-208 a1 measure frame time with the shipped creator (the overhaul when installed), with vanilla as baseline. Pawn epic: replace 'no saved Dark Mechanicus epic ID yet' with ep_01m4gbx49w72je0ezgjcfkxtjx and point MMR-134 at ICharacterEditorHost. Battle-scale ep_01m55wvmvkkat0krzcb9bdefx9: add a stable per-soldier or per-slot appearance identity shared by promotion and demotion (none exists: BattleSimulation.cs:185 uses monotonic lease ids), and consume this epic's distant-appearance subset. FWGC epic: if D23 = (a), note the IGeneratedHeroAppearance provider seam in HeroBodies.Draw and GeneratedStartupBehavior.cs from MMR-219; if D23 = (b), note that the character module Harmony-patches those two sites.

*Source:* PS-2, PS-5, PX-4, PS-1; round-3 checker defect 22 — *Why:* Seams that no epic owns.

### Other epics: the per-people content epic (not yet created; the user creates it) (cross-epic note) — *conditional on D10 = (a)*

When the user creates the per-people content epic, its intent takes over troop and civilian race. It covers spawn paths (1)-(7) and (9) listed in D10 (b), each with its census criterion: volunteers from Culture.BasicTroop and EliteBasicTroop, upgrade targets, militia, garrisons, caravans, villagers, bandits, and town LocationCharacters. Path (8), post-start notables and wanderers, stays in this epic under D10 (a). It consumes this epic's race record, race skins and ICharacterEditorHost.

*Source:* PS-1; ruling R4; round-3 checker defect 2 — *Why:* Under D10 (a), troop and civilian race needs a named future owner, so the narrowing of s8 is not lost.

## 8. Open unknowns and the probe for each

| ID | Question | Probe | Needs |
|---|---|---|---|
| U1 | Does native pack more than 63 deform keys per skin into the 512-bit key losslessly, and at what bit width per key? The D14 worst case is about 120-152 keys per skin, including people-delta keys. | A diagnostic skin with 82, 120 and 180 keys. Set KeyWeights[i] = 0, 0.37 and 1 through MBBodyProperties.ProduceNumericKeyWithParams, read back with GetParamsFromKey before and after a cold save/load, and log max error per key (MMR-165 a1). | game-launch (or native-provider to decode IMBFaceGen produce/get_params_from_key) |
| U2 | Does the public MBAgentVisuals.SetFaceGenerationParams regenerate a skin from full KeyWeights beyond key capacity? Its only caller is the public wrapper AgentVisuals.SetFaceGenerationParams, which nothing calls. | On a spawned agent and on a tableau of a 180-key skin, set KeyWeights[150]=1 via AgentVisuals.SetFaceGenerationParams, then capture before and after. | game-launch |
| U3 | What are the bit widths of the catalog indices and colour offsets, does random selection honour more than 4 face_texture entries and their tags, and what happens when an index exceeds a maturity skin's list? | Round-trip indices 0..127 and offsets at 1/1000 steps on a skin with 128 dummy entries; spawn 500 troops of one template and histogram the indices; age a hero with a high eyebrow index into a child skin (MMR-165 a3). | game-launch |
| U4 | Does the editor import a head with more than 63 morph frames, including on LOD meshes? | Import an original diagnostic head with 100 shape keys and check the frame count in the resource browser. | editor session (modding-kit editor under CLAIM/RELEASE, D12) |
| U5 | Are head, eye, lash, mouth, hair, beard, brow and body skin meshes reachable through agent.AgentVisuals.GetSkeleton().GetAllMeshes() after AddSkinMeshes, and do Material.CreateCopy+SetTextureAtSlot, Mesh.SetMaterial and Mesh.Color overrides survive LOD switches, re-equip and Refresh? Troop muscle variants and the D18 save/load preview also depend on the answer. | An IMissionListener.OnEquipItemsFromSpawnEquipment listener logs mesh names, applies a tinted texture copy and a Mesh.Color, then zooms through the LODs, swaps the helmet and captures; repeat in an AddSkinMeshesToEntity tableau postfix and on the BasicCharacterTableau save/load preview entity. Assign one shared, uncopied body material variant to two troop agents with Mesh.SetMaterial and repeat the same steps (MMR-163 a1). | game-launch |
| U6 | Which formats, colour spaces and mip behaviour do Texture.CreateFromByteArray(byte[], int, int) and CreateFromMemory(byte[]) (BC7/BC5 DDS) produce, given that neither takes a format or mip argument, and can they bind at face slots 0/1/2/4? | Create a 1024² gradient both ways, read MemorySize, bind it on a copied face material at slots 0 and 2, and capture (MMR-163 a2). | game-launch |
| U7 | Does native sample head morph frames at bone-only time points (60-63, DCC 64-82), so a head without frames there deforms wrongly? | A head whose frames stop at tp59, with DCC installed: set TorsoBelly to maximum and compare the head against neutral. | game-launch |
| U8 | Is a face deform key one signed morph whose negative half mirrors the positive, so collinear asymmetric DD2 keys fit one key with asymmetric key_min/key_max? | Set face_width to key_min and key_max in the creator and compare against the positive-extreme frame; or inspect head_male_a frames in the editor. | editor session, else game-launch |
| U9 | Do vanilla beards and brows follow face keys through their own morph frames? | Decode a beard mesh record in Modules/Native/EmAssetPackages/human tpac for vertex-animation frames; otherwise set face_width to maximum with a full beard and capture. | neither (offline tpac decode), else game-launch |
| U10 | How large is the linear-superposition error of baked morph frames against joint composition on our own rig? | Offline rig_bake check: summed single-key deltas against composed rig poses for every key pair at ±100, max and mean vertex error in mm. | neither |
| U11 | Can an original eye mesh with left/right UV islands, pre-compensating eye.rsh:11's 1.2x remap, carry heterochromia through one baked diffuse? | Diagnostic eye mesh with split islands and a two-colour baked diffuse, captured in creator and mission (MMR-163 a2). | game-launch |
| U12 | What does native do with useTranslucency=true on mission agents (shader/define chosen, frame cost), and is ear transmittance visible? | Transpile Agent.AddSkinMeshes by IL pattern to ldc.i4.1: the two consecutive ldc.i4.0 before the faceCacheID load and the SkinGenerationParams ctor, today IL_008e/IL_008f. Capture a back-lit agent; record frame time at frozen battle sizes and the global Tesselation option. | game-launch (native-provider for the define selection) |
| U13 | With ear_elf=1 under an ear-hiding helmet, does native suppress other ear keys when hide_ears auto-activates, or does the elf frame poke through? | Elf head at ear_elf maximum with each helmet cover type, with and without the zeroing prefix; capture. | game-launch |
| U14 | What is the output range of MBBodyProperties.GetScaleFromKey over the height key and min_scale, and do combat agents work at the dwarf race skin's floor, about 0.53 scale (gnome female min 0.95 m of 1.80 m), and at the gnome mean, about 0.58 (D14)? | Log GetScaleFromKey, AgentScale and GetEyeGlobalHeight at height key 0/0.5/1 on skins with min_scale 1.07, 0.72, 0.58 and 0.53; spawn a gnome-minimum dwarf-race agent in combat, on a horse and on a siege ladder. | game-launch |
| U15 | Does native accept a second <race> merged from a module skins.xml, and what does it do with a key packed for one race rendered on another race's skin? | Register probe_elf/probe_dwarf with 5 monsters each, log FaceGen.GetRaceNames(), spawn agents, and reload a hero whose race reverts. | game-launch |
| U16 | Do bone_scale keys at twice the shipped ±20% range (including the people-delta leg_length key), and MBAgentVisuals.ApplySkeletonScale on human agents, animate cleanly with weapons, IK and equipment? | Custom battle with arm_length, leg_length and thorax keys at their extremes, and ApplySkeletonScale on one agent; capture swings and a capsule debug draw. | game-launch |
| U17 | Does forced tessellation on mission heads cost an acceptable frame time at the frozen battle sizes? If it does, MMR-171 a6 ships it. | Part of the U12 run: frame time with tessellation forced for heroes within a distance budget. | game-launch |
| U18 | Can the editor build a module shader cache from a modified faceshader_high without touching <install>/Shaders/Sources? And, only if D8 = (b), does the client compile a new shader name whose .rs is added under Sources? | MMR-163 a5(1) under CLAIM/RELEASE, rendering on a mission agent. Part (2) adds an .rs under <install>/Shaders/Sources, with C:\ProgramData\Mount and Blade II Bannerlord\Shaders cleared first and restored after; it runs in a later live round only if the user chooses D8 (b). | editor session, then game-launch (client) |
| U19 | Does the SyncData Dictionary<string,string> record round-trip a cold save/load on v1.4.8 (statically predefined at SaveableBasicTypeDefiner IL_0471)? | Write two hero records, save, exit, reload, compare. | game-launch |
| U20 | Is native GetRandomBodyProperties pure and deterministic over the full 31-bit seed range? | Generate keys for seeds s and s+2000k, compare, and repeat after a restart (MMR-165 a3). | game-launch |
| U21 | Does DCC v1.8.1 (with the add-on) load at runtime on v1.4.8? Static Cecil binding resolved in round 1. | Isolated profile load with logs; check the UIExtenderEx v3-vs-v2.13.2 dependency and whether the DCCHeroEditor/DCCTroopEditor prefabs still bind. | game-launch |
| U22 | Does native apply SkinGenerationParams.faceDirtAmount anywhere? | A prefix on Agent.AddSkinMeshes sets faceDirtAmount 0 and 1 on two identical agents; capture. | game-launch |
| U23 | What are the DD2 key evaluation curve, the face-rig transfer to attachments, and the bake timing? | The one user-approved DD2 REFramework session (MMR-158 a2): Face_Nose_Length and Arms_Length at -100/-50/0/50/100; a face edit with a beard and brow mesh equipped; observe the MaterialBakerController phases on edit. It runs outside every work ticket's acceptance. | game-launch (DD2, one session the user approves) |
| U24 | What do DD2's 13 unresolved families mean (SkinWrinkeStrength, MuscleStrength, EyeSquint, CloseEye, TeethEnableUpper/Lower, the edit toggles, the 4 Pretender controls)? | Parse app.charaedit.ch000.AppearanceData field names with a current RSZ type DB (the CRCs of both available DBs mismatch build 25920212), or run one-control diffs in DD2. | neither (newer RSZ DB), else game-launch (DD2) |
| U25 | Can MetaHuman-derived assets, MPFB data files (25 of 33 JSON without a licence field) and AssetHelper outputs be redistributed in a Bannerlord mod? | Read the current MetaHuman/UE EULA terms for non-Unreal use, the MPFB repository licence and AssetHelper's tool terms; record them in the licence ledger. | neither |
| U27 | Can a saved revision that adds exact-ID child batches to the active sprint be adopted mid-sprint, or only at a checkpoint? D12 Option 1 puts the contract freeze and the first production slice in sprint 2. | Read the darkmechanicus-planner and darkmechanicus-orchestrator prompts and the adopt_revision and redraft_next_sprint schemas. If adoption is checkpoint-only, the planner drafts the sprint-2 slice batches from NEW-B's measured hours at the sprint-1 checkpoint, and MMR-167 a6 confirms them. | neither |
| U28 | For each of the eight identity sites, does the CharacterObjectStringId copied into AgentVisualsData at AgentVisuals.Create survive that site's later AgentVisuals.Refresh(bool, AgentVisualsData, bool) calls (BannerEditorView ctor and RefreshShieldAndCharacterAux; GauntletBannerBuilderScreen.AddCharacterEntities and RefreshShieldAndCharacterAux; CharacterCreationClanNamingStageView.RefreshCharacterEntity through GetCopyAgentVisualsData), and is AddSkinMeshesToEntity reached synchronously inside Create? | Static IL walk of each Refresh call's data source; otherwise log CharacterObjectStringIdData from the postfix in the live round. Any site that drops the id gets a prefix on its Refresh call. | neither (static IL), else game-launch |
| U29 | Does Sandbox character creation build family heroes from the narrative mother_character and father_character (MMR-178 a7)? | Static IL trace from CharacterCreationCampaignBehavior's finalize path to Hero creation. | neither |
| U30 | Can a mod drive one extra face bone or morph frame at runtime independently of the blink, on a mission agent and in conversation (tidekin nictitating lid, MMR-220)? | MMR-160 a3 gains probe steps that animate one diagnostic extra bone or key on the diagnostic head at idle and in dialogue; a failure goes to NEW-D. | game-launch |
| U31 | Does Dark Mechanicus dispatch matching read capability.constraints.environments? | Read the match_capabilities schema and the orchestrator prompt. | neither |
| U32 | Does the official launcher honour optional="true" on DependedModuleMetadata? | Sprint-1 live-round launcher check with DCC absent (MMR-157 a1). | game-launch |

## 9. Findings: status after verification

| Finding | Status | Final statement |
|---|---|---|
| PP-1 | narrowed | Probes may record limits, but MMR-167 a1/a3 cannot pass on a blocked required control or without art capability, and no ticket records a user decision about a blocked or limited capability. A DCC load failure first becomes unpassable in sprint 2 (MMR-226 a1/a2, MMR-155 a37). The gates contradict each other (x4 and MMR-228 a2 versus n1). Boon and voice decisions are recorded elsewhere. Fixed by NEW-D and the gate rewrites. |
| PP-2 | stands (not re-verified in round 2) | There is no user-approved quality reference before the catalog is built. Adopted: NEW-B benchmark pair with the user verdict at the sprint-1 checkpoint, edge MMR-158->MMR-159, MMR-181/182 a4 rewrite, and the player-first slice in sprint 2. Under D20 (a), NEW-C adds a child and teen benchmark pair in sprint 2. |
| PP-3 | narrowed | MMR-213 a2 cannot pass until peoples MMR-204 delivers peoples.json, which exists nowhere; there are 0 cross-epic edges. The rationale already acknowledges the dependency in prose, so keep the sentence and add the kEntityIds rewrite plus the sprint-3 entry n2 gate. 'requires' is scoped to one epic, so the gate is text plus ticket references of kind 'ticket' (R11). |
| PP-4 | stands (not re-verified) | The single-player/multiplayer answer changes the roster (757 vs 1,254 armor ids) and sprint-1 probes. It is asked now as D3 and recorded in the intent. |
| PP-5 | confirmed (coordinator, against memory) | The plan contradicts the lean-verification and REA-before-planning rules. Structural consequence found by the merger: a contract freeze cannot consume its own sprint's live round, hence the contract freeze sits in sprint 2, after sprint 1's live round (D12). The research doc is folded into MMR-157 a4. |
| PP-6 | stands (not re-verified) | Adopted: edges MMR-175/180->MMR-220/221 and MMR-222->MMR-227, the MMR-222 a3 hand-off, the Batch rule on MMR-220, 221, 222 and 227, and people feature deltas in MMR-193/220/221. Round 3 adds the MMR-197 people-delta review criterion, tidekin's nictitating eyelid in MMR-220 (R12), and the authoring of tidekin, hill dwarf, frosthold dwarf and gnome as deltas over their race's head and body (D14). |
| PS-1 | narrowed | MMR-219 and MMR-155 a30 assign races to every category, but no ticket changes the creation paths: HeroBodies.Draw passes 0, CreateSpecialHero has no race parameter, and troops are shared presentation-culture templates (4 of 7 cultures serve two races). Single-player ignores AgentBuildData.Race. Needs D10, D23 and the MMR-219 body edit. Under D10 (a), which follows the user's 2026-10-15 decision, MMR-219 covers the player, heroes, lords, notables (including post-start spawns), wanderers/companions and children, and troop and civilian race goes to the per-people content epic (R4). |
| PS-2 | confirmed | No ticket integrates the overhaul creator into generated-campaign character creation, the pre-registration record or the bootstrap whitelist; MMR-219 a2 presupposes it. Adopted: ICharacterEditorHost in MMR-178 a7, which also covers StoryMode and writes family race under D5, plus the peoples and pawn edits. |
| PS-3 | narrowed | No ticket acquires art capability. Provenance is already required in about 15 art tickets; only licence and redistribution terms are unowned. Adopted: D2, NEW-B, the licence ledger plus package check in MMR-200, D15 asset storage and the repo-wide RE Engine magic scan in MMR-157 a5 (R7). |
| PS-4 | stands (not re-verified) | Rename to MNBL.CharacterOverhaul, register in config/mods.json (plus a races module if D16 splits), store editor-built asset packages per D15 (recommended: release artifacts outside git, not committed), and package via mods.py (MMR-157 a2, MMR-168 a4, MMR-200). |
| PS-5 | narrowed | Battle-scale collisions are real, but a 'stable BattleScale soldier id' does not exist (BattleSimulation keeps no per-soldier identity). The battle epic must add it and consume the distant-appearance subset (D7). |
| PS-6 | stands; supported | life_stages and swatch weights must be ported (MMR-213 a1, MMR-174 a2). Race ages need D6. The merger confirmed all seven AgeModel age getters take no character, which favours option B. |
| PS-7 | stands as decision | The reading of 'bring in DD2's creator' is asked as D1; recommended A plus DD2's tab/family structure. |
| PX-1 | confirmed | Troop faces come from at most 2,000 seeds per troop id, shared across template-mates. The same seed drives gear, so twins match fully. Counts carry scope caveats (230 soldiers on 36 templates in the sandbox campaign; obsolete_characters.xml is loaded but excluded). Adopted in MMR-179 a6 and MMR-167 a2. |
| PX-2 | stands (not re-verified) | Vanilla affordances and DD2 creator affordances are kept: MMR-158 a4, MMR-178 a6, MMR-183 a5, barber in MMR-161. |
| PX-3 | narrowed | 504 fixed-key heroes and 455 distinct keys confirmed; likeness needs a gate (MMR-167 a5, MMR-181/182 a6). face_mesh_cache is dead data with no consumer, so its proposed MMR-177 check is dropped. |
| PX-4 | narrowed | Helmet coverage numbers confirmed. The non-Agent share is at least 95.92% only at 100k (59.2% at 10k, 0% at 2k). No logical soldier id exists to seed from. s3 is rescoped (D7) and MMR-159 gets numeric battle sizes (2,040 real agents in the live rounds; 4,080 only in a separate session the user approves). |
| PX-5 | narrowed | DD2 NPCs are prebaked (1,832 sets confirmed) and human enemies draw from 176 records under 8 entries. Breadth must cite GUI-visible counts. A bounded native face_texture palette for troops is adopted. |
| RA-1 | narrowed | Missions and portraits use false/false; the creator face stage uses translucency on and tessellation off; narrative, options and review use both on. The TRANSLUCENT define is set by no shipped shader. Captures must record per-context flags; the forced-flag probe is in MMR-163 a3 and matches an IL pattern, never fixed offsets (R6). |
| RA-2 | narrowed | Per-character inputs are minimal. Only metallic/coloured face specular, per-texel hair gloss, dynamic wrinkles and mission ear transmittance need a shader. Pupil size and hair root/tip are bake-reachable; per-eye colour is a bake on an original eye mesh (inferred). |
| RA-3 | narrowed | The client runtime-compiles shaders from <install>/Shaders/Sources; only module-cache building is editor-only; no module source route is proven. The probe is folded into MMR-163 a5: the editor module-cache route (1) runs in sprint 1, and the Sources route (2), with its stale-cache warning, runs only if D8 = (b) (R8(c)); D8 covers failure. |
| RA-4 | confirmed (change narrowed) | No DD2-class art capability exists and none is acquired. The proposed capability.tools edit cannot be saved; use skills, autonomy 'supervised', capability.constraints.environments and a D2 dispatch gate instead (R15). |
| BE-0 | narrowed | The hook map stands with corrections: BasicCharacterTableau uses FillEntityWithBodyMeshesWithoutAgentVisuals, and identity already reaches AddSkinMeshesToEntity for single-player 3D tableaus. MMR-177 a1 is rewritten and also names the eight AgentVisuals.Create callers that set no identity and the CharacterSpawner.InitWithCharacter hop (R1). |
| BE-1 | narrowed | Race is not saved. Non-original heroes reload with their ROOT template's race and main_hero with XML race 0, so descendants revert. set_Race is public. A per-hero race record for every hero is required (MMR-172 a1). |
| BE-2 | narrowed | No identity reaches the native body builders. Plumbing is needed for 2D thumbnails (plus the CharacterSpawner.InitWithCharacter hop), conversation tableaus, the save/load preview, eight AgentVisuals.Create callers (MMR-177 a1) and the native hop, not for single-player 3D screen tableaus or missions (public listener). |
| BE-3 | confirmed | Colours are single scalar offsets along authored per-skin gradients. Authored fantasy hues are XML-feasible (the tattoo gradient already ships blue and purple); a free picker is not representable in the key. See D4. |
| BE-4 | narrowed | The cache mechanism is confirmed. The budget is (CharacterDetail+1)×100, not 100/200/450/750/5000. It is registered in 4 openers including custom battle. Its race (+10) and sex (+1000) mismatch costs are soft penalties against thresholds up to 5000, so the replacement treats both as hard keys and must land before races ship (R13). |
| BE-5 | partly confirmed | (1) Single-player ignores AgentBuildData.Race (Monster per agent is honoured): confirmed. (6) Same-race assert is non-fatal: confirmed. (2)-(5) and (7) were not re-verified, but the merger confirmed the managed skins merge order that (4) relies on. |
| BE-6 | stands (not re-verified in round 2) | Static DCC binding resolves; runtime load is a live-round probe. Reserve time points 64-82, declare the 19 ids inline on race skins, load after Additional Sliders (MMR-217 a4), and define precedence (MMR-226 a4). |
| DD2R-1 | confirmed (detail narrowed) | 474 controls in 196 families (tie-break dependent, else 197). The Tattoo tab holds 296 controls, 290 of them in region parts. The ledger generator replaces runtime inventory (MMR-158). |
| DD2R-2 | narrowed | Shape is joint TRS with no creator blend shapes. 85 human heads share one topology. Elf ears use 10 joints. Height adds a Hip translate. Arm length has no finger bones. 98 targets is tolerance-sensitive (96 at 1e-3). |
| DD2R-3 | narrowed | All surface inputs, including metallic (ALBD.a = sqrt(1-M)) and roughness (NRRA.a), are baked. NPCs use a separate prebaked master and 1,832 sets (not 1,839). In-game SSS is constant. The player is runtime-baked by static elimination. |
| DD2R-4 | narrowed | Breadth numbers are GUI-visible list sizes (catalogs hold more). Eye/lips/dirt/ear-preset counts were not reproduced. 2 LODs is unverified in round 2. DD2CCS is valid for creator-material evidence. |

### Refuted and narrowed items (round 2)

Each entry: the old statement, then the verdict, then what replaces it. The source is in brackets.

#### Bannerlord engine (BE)

- **BE-1** 'Hero race is not saved; the player reverts to main_hero's XML race; template heroes revert to their template.' **NARROWED.**
  - No saveable member carries race: 0 of 1,744 across 82 assemblies.
  - Non-original heroes copy race from their ROOT original template. CharacterObject.CreateFrom flattens _originCharacter.
  - main_hero is re-deserialized from NPCCharacters, and lords.xml main_hero has no race attribute, so it gets 0.
  - The player's same-sex descendants revert to race 0, so race-specific templates do not fix lineage.
  - Race reaches the save file only in the MainHeroVisual metadata string, which only the save list reads.
  - **Correction:** set_Race is PUBLIC on v1.4.8, not non-public. Both round-2 maps also said 'non-public via AccessTools', and that is wrong too.
  - 'An elf key renders on the human skin after reload' is inferred, not observed. [verifier be-persistence]
- **BE-2** 'No identity reaches any body build or portrait; every managed body build ends in MBAgentVisuals.AddSkinMeshes.' **NARROWED.**
  - A second native entry point exists: MBAgentVisuals.FillEntityWithBodyMeshesWithoutAgentVisuals, called by BasicCharacterTableau (the save/load preview).
  - Agent.AddSkinMeshes has this.Character.
  - AgentVisuals.AddSkinMeshesToEntity reads and resolves CharacterObjectStringIdData, then discards it. Single-player 3D screen tableaus already set it.
  - Plumbing is needed for 2D thumbnails (plus the CharacterSpawner.InitWithCharacter hop), the conversation tableau, the save/load preview, eight AgentVisuals.Create callers (MMR-177 a1) and the final hop to native.
  - Round-1 MMR-165 a5 wording ('none receive an identity') is withdrawn.
  - Engine-first map error: 'BasicCharacterTableau calls MBAgentVisuals.AddSkinMeshes at IL_0329'. IL_0329 is the SkinGenerationParams ctor, and the native call is FillEntityWithBodyMeshes at IL_0339.
  - Arch-first map error: 'all 22 sites end in MBAgentVisuals.AddSkinMeshes'. [verifier be-persistence]
- **BE-0** The hook map is **NARROWED**: it misfiles BasicCharacterTableau under AddSkinMeshes/AgentVisuals.Create. MMR-177 must name both native entry points.
- **BE-4** The face-cache description is part confirmed, part refuted, part narrowed. [verifier population-facecache]
  - (a) MissionFaceCacheView subscribes to OnComputeTroopBodyProperties and reuses similar non-hero faces: **CONFIRMED**.
  - (b) 'Budget 100/200/450/750/5000 keyed on CharacterDetail': **REFUTED**.
    - The budget is (CharacterDetail+1)×100, i.e. 100-500.
    - 100/200/450/750/5000 are similarity-distance thresholds by fill fraction 0.2-1.0.
    - Skip chance is 0.37×clamp((fill-0.4)/0.3, 0, 1).
    - The budget is not a cap.
    - The arch-first map repeated the wrong numbers.
  - (c) Registration: **NARROWED**.
    - It is registered by 4 openers: SandBox Battle, SiegeMissionWithDeployment, Siege (no shipped single-player opener literal), and CustomBattleViews.OpenCustomBattleMission.
    - It is absent from SiegeMissionNoDeployment, custom siege, hideout, ambush, village, settlement-entry, lords-hall and every NavalDLC battle view.
- **BE-5(1)** 'Per-agent race is ignored in single-player': **CONFIRMED with nuance.**
  - The per-agent Monster IS honoured in single-player, and gender and body key are per agent. Only the skin race is locked to Character.Race.
  - AgentData lives in TaleWorlds.Core.
- **BE-5(6)** 'DeliverOffSpring asserts mother.Race==father.Race': **CONFIRMED, but the SilentAssert is non-fatal.** Cross-race children are born with the same-sex parent's current race and reload with the root template's race.
- **BE-5(2)-(5),(7)** and **BE-6**: not re-verified in round 2.
  - The merger confirmed one consequence statically: CreateMergedXmlFile applies a module's XSLT before merging that module's XML, so race skins must declare all keys inline and the overhaul must load after DCC.

#### Population and player experience (PX)

- **PX-1** counts **CONFIRMED** with scope caveats.
  - 258 soldiers on 42 templates covers four modules. The sandbox campaign alone has 230 on 36.
  - fighter_nord exists only with NavalDLC.
  - All counts exclude obsolete_characters.xml, which the game loads.
  - Resolved NOT_EST: the same seed also picks the gear (GetRandomEquipmentElements(new Random(AgentEquipmentSeed))), so seed twins match in face and gear.
- **PX-3** counts **CONFIRMED.** 'face_mesh_cache marks cached faces' is **REFUTED**.
  - BasicCharacterObject.Deserialize parses it and no shipped code reads it.
  - The proposed MMR-177 face_mesh_cache check is dropped.
  - 459 of the 504 fixed-key heroes are flagged. lord_4_26_1 and the non-hero main_hero_mother are the other flagged entries.
  - The engine-first map's 'FlushFaceCache because 460 heroes carry face_mesh_cache' loses its stated reason. FlushFaceCache after head changes stays for the real FaceGen cache.
- **PX-4** '95.9% of soldiers are non-Agent figures': **NARROWED.**
  - It is at least 95.92% at 100k, at least 97.96% at the 2,040 cap, at least 89.8% at 40k, at least 59.2% at 10k and 0% at 2k. It is a floor, because horses and retiring-agent headroom also consume slots.
  - 'MMR-159 a2 says maximum intended supported battle size': the phrase is in the body; a2 says 'chosen battle load'.
  - Helmet numbers **CONFIRMED** (4.21/8.62 both hidden; 20.49/17.23 bareheaded; 44.01/52.61 hair hidden).
  - Resolved NOT_EST: equal set weighting is correct, because each slot draws uniformly.
- **PX-4b/PX-4c and PS-5** 'replace ticket.Id seeding with the logical soldier id': **NARROWED.**
  - BattleSimulation keeps no per-soldier identity (monotonic lease ids; 'only reserved or materialized soldiers have records').
  - The battle epic must add one first.
  - VisualCrowdMission's seed 12345 belongs to a single-archetype probe that the 100k epic already rules out.
- **PX-5:** 1,832 prebake folders is correct. Breadth comparisons must use GUI-visible counts, not file-list or catalog counts.

#### Rendering (RA)

- **RA-1** 'BodyGeneratorView pushes true for both flags': **REFUTED for tessellation.**
  - BodyGeneratorView (the creator face stage, the barber and the body generator screen) uses translucency TRUE and tessellation FALSE.
  - Both flags are on only in the narrative, options and review stages, the education screen and scene notifications.
  - The TRANSLUCENT-define finding is **CONFIRMED**. The gbuffer translucency route is also dead for faces.
  - Tessellation is also a global graphics option.
- **RA-2** 'Metallic/coloured specular, per-eye colour, pupil size and hair root/tip all need a shader': **NARROWED.**
  - Only metallic or coloured face specular truly does.
  - Pupil size and hair root/tip are bake-reachable.
  - Per-eye colour needs a shader only on the stock single eye mesh.
  - Per-texel hair gloss and dynamic wrinkles need a shader.
  - 'Per-mesh slots all spoken for' is slightly overstated: factor2.rgb and vector_argument_2.z/w are unread, but that means nothing without a shader change.
- **RA-3** 'Shader compile is editor-only': **REFUTED.**
  - The shipping client runtime-compiles from <install>/Shaders/Sources: 22 compile events in logs, and 1,906 DXBC files with embedded PDB in ProgramData, including face variants.
  - Only module compressed-cache building is editor-only.
  - 'No module source path string' is literally true, but the client holds composable fragments, so the remaining evidence is behavioural, not string absence.
  - New: every build-119303 log fails to delete about 1,907-1,918 stale runtime cache files.
  - The Arcane Magisterium cache: **CONFIRMED** that it holds no source. Its 596 variants appear nowhere in the base sack.
- **RA-4** **CONFIRMED.** Its recommended change ('set capability.tools to include blender and a human operator') **CANNOT BE SAVED**: capability.tools is a closed six-value enum and list_profiles is empty. The requirement goes into skills, preferences.autonomy='supervised', capability.constraints.environments and a body gate instead (R15).

#### DD2 reference (DD2R)

- **DD2R-1** 474/196 **CONFIRMED.**
  - 196 depends on a tie-break: DetailTypes placement wins over SelfDetailParam, otherwise 197.
  - '296 of 474 are region×slot copies' is **NARROWED** to: 296 is the whole Tattoo tab; 290 are in the 10 region parts (280 slot copies + 10 EnableEdit2nd toggles); 6 are Dirt and the two presets.
- **DD2R-2:**
  - **NARROWED:** 'all 8 parsed human heads share topology'. The lens's set included Beastren head_820_m. All 85 human heads plus the blendtarget share 15,489/27,548 and byte-identical index buffers. The 5 Beastren heads differ.
  - **NARROWED:** elf ears act on 10 joints (l/r_ear_flapA-E), not 16.
  - **NARROWED:** height also translates Hip (+0.1865/-0.2146 m) and L_Trap_AOffset, and the scale is within 0.12%, not 0.1%.
  - **REFUTED:** 'arm length includes finger bones'. The 18 joints are arm, twist and *_Edit_* biceps/triceps.
  - **NARROWED:** body keys: 29 of 32 male and 32 of 35 female carry joints; 3 per sex are parents.
  - **NARROWED:** 98 targets is tolerance-sensitive (98 at 1e-4 m, 96 at 1e-3). Arch-first's looser rule gave 92 and engine-first's metric 84.
  - Resolved NOT_EST: positive blend-shape controls exist (monster meshes ch21_004 and ch22_000).
  - New, not reconciled: round 2 reads a secondary weight stream on human heads, with head_000_m flagging 69 of 263 bones. Round 1 had dropped useSecondaryWeight as non-discriminating (259/263 on head_001_m).
- **DD2R-3:**
  - **REFUTED:** '1,839 prebake folders'. There are 1,832. The 21,973 paths are 21,966 textures plus 7 .mdf2 files.
  - **NARROWED:** '197 params' belongs to the mdf2. The .mmtr material buffer declares 205, including 8 hidden dead variables.
  - **REFUTED:** ALBD alpha as opacity and NRRA as normal-roughness-alpha. ALBD.a = sqrt(1-metallic) (dielectric); NRRA.rg = normal, .b = tattoo/scar mask (unused in game), .a = roughness. Prebaked BC7 NRRA uses R=roughness, G/A=normal.
  - **NARROWED:** NPCs use a separate master, Character_Skin_Head_Ingame_PreBaked.
  - **NARROWED:** SSS, translucency and sheen are creator-preview only. In-game skin SSS is the constant 0.12375, so per-race SSS is a quality aspiration, not DD2 parity.
  - The cross-lens claim 'DD2 needs live per-layer metallic in game' is **REFUTED**: metallic is baked.
  - The player is runtime-baked by static elimination, not observed.
- **DD2R-4:**
  - **NARROWED:** counts are GUI-visible list sizes; catalogs hold more.
  - Eye styles 3, lips 5, dirt 3 and ear presets 7 were **not reproducible** by the verifier's method (catalogs show 6 eye ids, 325 lip textures, 2 dirt prefabs; unlabeled length-7 arrays).
  - '2 LODs per mesh' is **UNVERIFIED** in round 2.
  - 'DD2CCS cannot stand in for material evidence' is **NARROWED**: the creator Preview head_mat is identical (210 params, 0 value differences), and the differences are snow/fade VFX only.

#### Plan and seams (PP, PS)

- **PP-1** **NARROWED.**
  - 'No ticket records a user decision' is too broad: the boon table (MMR-218 a3, MMR-129 a20) and voice/posture applicability (MMR-158 a3, the MMR-167 body) are recorded.
  - What is missing is a decision for blocked or limited capabilities.
  - A DCC failure first becomes unpassable in sprint 2 (MMR-226 a1/a2, MMR-155 a37), not at MMR-156 a6.
- **PP-3** **NARROWED.** The 'no sibling dependency' sentence names the combat, world and creature epics and predates the peoples epic. The revision-2 rationale already acknowledges the peoples dependency, so change (d) is withdrawn and the gate is kept.
- **PS-1** **NARROWED.** MMR-219 and MMR-155 a30 do assign races to every category. The gap is that no ticket changes ProceduralWorld's creation paths. Under D10 (a), troops and civilians leave this epic's race scope for the per-people content epic (R4).
- **PS-3** **NARROWED.** Per-asset provenance is already required in about 15 art tickets. Only licence and redistribution terms are unowned, so the proposed 'licence and provenance ledger' shrinks to a licence ledger.
- Product-seams missed an 8th epic (Map epic 2). It has no race or creator tickets, so PS-1 and PS-2 stand.

#### Change maps (round-2)

- **Both maps:** 'non-public set_Race'. It is public.
- **Arch-first:** 'MissionFaceCacheView budgets 100/200/450/750/5000'. Refuted (see BE-4).
- **Arch-first:** 'Arms_Length includes fingers'. Refuted.
- **Arch-first:** '1,839 prebake sets'. Refuted; 1,832.
- **Arch-first:** 'eyebrow_col = factor2 ties brow colour to hair colour'. Narrowed: the variable is never used in the face pixel path.
- **Arch-first:** registration list missed CustomBattle.
- **Engine-first:** 'ears on 16 joints'. Narrowed to 10.
- **Engine-first:** 'BasicCharacterTableau → MBAgentVisuals.AddSkinMeshes'. Refuted.
- **Both maps:** MMR-190's 'no baked single appearance' clause conflicts with DD2's own bake model. Reworded in ticket_changes.

#### Merger's own checks (all held)

- IMissionListener.OnEquipItemsFromSpawnEquipment fires after Agent.AddSkinMeshes. AddSkinMeshes' only caller is Agent.EquipItemsFromSpawnEquipment, and creation type 2 skips the skin build.
- XSLT runs before the module's own XML merges.
- Dictionary<string,string> is a predefined save container.
- CharacterCode.CreateFrom(string) ignores an appended token, and CreateNewCodeString ends with a trailing '@---@'.
- All seven AgeModel age getters take no character.

#### Round 3 (completeness critic, coordinator rulings, checker)

- **Section 9's gnome race:** **REFUTED** (R3). FWGC++ entity-rules.json and heritage-appearance.json list three races, with gnome a dwarf people and tidekin an elf people. Both are people deltas over their race's head and body (D14).
- **'InitWithCharacter hands the thumbnail token on':** **REFUTED** (R1). CharacterSpawner.InitWithCharacter builds AgentVisualsData from CharacterCode fields only, so the 2D route needs a prefix for its last hop.
- **BE-2 plumbing list:** **NARROWED** further (R1). Eight AgentVisuals.Create callers set no identity: the narrative, options, review and clan-naming creation stages, BannerEditorView, GauntletBannerBuilderScreen, GauntletEducationScreen and GauntletSceneNotification.OpenScene.
- **'BC7 is 4x smaller' in the bake budget:** withdrawn (R10). Neither texture factory takes a format or mip argument, and nothing produces BC7/BC5 at runtime. MMR-171 owns an encoder or an explicit RGBA8 budget.
- **'capability.tools cannot express the art requirement':** **NARROWED** (R15). The enum is closed, but capability.constraints.environments (free text) can name the operator and toolchain. The body gate stays, because dispatch matching on it is unverified (U31).
- **MissionFaceCacheView penalties:** **NARROWED** (R13). Sex mismatch is also only a soft penalty (+1000) against thresholds up to 5000, so the replacement treats race and sex as hard keys.
- **MMR-159's 4,080-agent case:** it needs the battle-scale engine build, which fails the stock-binary check, so it runs only in a separate session the user approves (R6).

#### Coordinator corrections

- **Ruling R2 overstated StoryMode.** It said FinalizeParentsAndLittleSiblings copies body properties to the whole family. round2/completeness-critic/sm_family.txt shows that it copies StaticBodyProperties (IL_00b7, IL_00d2), Weight and Build (IL_00e7-IL_013c) from the narrative mother_character and father_character to main_hero_mother and main_hero_father only. The siblings get names, equipment and culture only: StoryModeHeroes.ElderBrother gets equipment, culture and a name, while LittleBrother and LittleSister get only a name (SetName) and SetHasMet. No family hero gets set_Race. The siblings' body source (their storymode_* templates) was not traced. R2's directive still stands: all five get the player's race and an appearance record (D5, MMR-178 a7). D5's 'why', MMR-178 a7 and its rationale, implementation section 1 and the executive summary's Blocker 5 are corrected to match.

#### Coordinator rulings applied (R1-R15)

The output schema has no rulings field, so the rulings list is carried here.

- **R1 Identity sites.** MMR-177 a1 (eight AgentVisuals.Create callers with identity sources, the CharacterSpawner.InitWithCharacter / CharacterThumbnailCache.FillEntityWithPose hop, mechanism) and new a7; implementation section 2 (scope, site table, hop, mechanism); executive summary identity bullet; new U28 for the [ThreadStatic] route (whether the identity survives each site's later AgentVisuals.Refresh calls).
- **R2 StoryMode family.** D5 renamed 'family race'; MMR-178 a7 (StoryMode CharacterCreationState, family race records at OnCharacterCreationIsOver, StoryMode heroes named from sm_family.txt); implementation sections 1, 9 and 10. Whether Sandbox creates family heroes from the narrative characters was not traced in round 2, and the text says so. Round 3 corrects the ruling's premise: only the parents get the copied body properties (see Coordinator corrections), and the Sandbox question is now U29.
- **R3 Race granularity.** New D14. MMR-165 a5 key budget (people-delta keys counted from heritage-appearance.json: 9 non-base peoples, 2 ear forms, 1 leg-length key); MMR-214 body/a1/a2 (no gnome race); MMR-215 body; MMR-227; NEW-D (escalation only through NEW-D); implementation sections 3, 4 and 9 ('elf, dwarf'); mapping M04 and M23; executive summary B1 and B5. Round 3 adds leg length to MMR-164 a1, tests U14 at the 0.53 floor, adds dwarf's 4.6 heads tall to section 3, restores MMR-227's per-race closure clause, and adds the MMR-220/221 people-delta authoring change.
- **R4 D10 cites the recorded decision.** D10 rewritten, citing comment cm_01m51aqjmdx8pv1210epc69eyq (verified in .darkmechanicus); option (b) lists nine spawn paths, each with a census criterion. Option (a) applied conditionally: new EPIC s8 change; MMR-219 body/a1 and MMR-155 a30; MMR-197 merged a3 and new criterion; new MMR-220/221 a1 troop clause; EPIC rationale; new per-people content epic note; implementation section 9. The nine census criteria are designed in this spec, not taken from evidence. Round 3 adds the census criterion to option (b) item (8), keeps post-start notables and wanderers in this epic under (a) (MMR-219 body, a1 and MMR-155 a30), and makes the MMR-223/224 troop clauses conditional on D10 = (b).
- **R5 Mission skin-shading adoption.** MMR-171 new a6, with the rationale noting that DD2's in-game SSS is constant and its per-character translucency is creator-only, so this is beyond parity; implementation section 7; U17.
- **R6 Engine identity.** Acceptance-node body and new criterion; MMR-157 a1 records the TaleWorlds.Native.dll SHA-256; MMR-163 a3, MMR-171 a6, implementation sections 7 and 14, and U12 use IL-pattern transpilers. The ruling's 'immediately before' is refined from coord/addskin.il and rendering-verifier/scan_client.json: one faceCacheID load (ldarg.3 at IL_0090, or ldc.i4.0 at IL_0328) sits between the two ldc.i4.0 and the ctor call. Round 3 moves MMR-159's 4,080-agent case out of the stock-binary live rounds.
- **R7 Asset storage.** New D15. MMR-157 new a5 (repo-wide RE Engine magic scan in tools/ci/mods.py check; local pre-push hash hook and its stated limit); MMR-168 a4; MMR-200 a4; implementation sections 0 and 13. The exact magic list is not in round2.json; it is defined as the header signatures of the resource types the tools/dd2-reference generators read.
- **R8 Lean rule.** (a) D12 rewritten (Option 1 recommended, Option 2 described); SPRINTS restructured to Option 1, conditional on D12, with Option 2's slice mapping in the rationale; sprint references updated in NEW-B, NEW-D, MMR-181/182 and the EPIC rationale; new MMR-167 a6 and U27 for in-sprint batch adoption, which round2.json does not answer. (b) MMR-158 body/a2 and U23: one user-approved DD2 session, outside every ticket's acceptance. (c) MMR-163 a5(2) and U18 run only if D8 = (b). (d) Dropped the DD2CCS rerun from MMR-158 a1 and 'clean build gives the same mapping' from MMR-168 a1. (e) NEW-B is conditional on D12. Round 3: MMR-159 a3 no longer depends on DD2 captures; rule (2)'s editor fallback covers every editor step if D12 disallows editor sessions; the existing sprint exits are mapped to Option 1; three covering criteria that pointed at deleted tickets are deleted; NEW-C holds the D20 (a) benchmark pair.
- **R9 Buried decisions get D-numbers.** decisions_for_user D1-D23 with 'when' groups (before-sprint-1: D1-D4, D10, D12, D14-D17; sprint-1-checkpoint: D5-D9, D11, D13, D18-D23); EPIC intent Recorded decisions section; NEW-D a4; MMR-167 a4; SPRINTS sprint-1 entry n2 and exit; MMR-158 a3 (D19, D9); new D16 artifact-path change.
- **R10 Bake pipeline ownership.** MMR-171 a1 (encoder or RGBA8 budget, about 48 MiB per hero and about 1 GiB for 20 heroes); MMR-163 body/a2 (factories take no format or mip argument; CPU vs Texture.CreateRenderTarget backend); MMR-173 new a7 (spawn-time bake queue); MMR-167 a2; implementation section 6 ('BC7 4x smaller' replaced by [G:U6] plus the encoder).
- **R11 Cross-epic edges.** EPIC rationale (requires is scoped to one epic; text gates plus 'ticket' references); EDGES gains the references; sprint-3 entry n2; old U26 removed; its id is not reused (the identity-scope unknown is U28).
- **R12 People deltas.** MMR-197 new people-delta criterion (troops only if D10 = (b)); new MMR-220 change for tidekin's nictitating eyelid geometry and animation channel; implementation section 3.
- **R13 Labels.** Mapping M25 change/status relabelled [G:U5]; implementation section 6 troop line [S, G:U5]; implementation section 8 (race AND sex as hard keys; sex mismatch +1000 against thresholds up to 5000). Round 3: MMR-163 a1 and U5 probe the shared troop material assignment and the save/load preview entity.
- **R14 Breadth counts.** D11 and MMR-167 a3 list only the verified counts, with eye 3, lips 5, dirt 3 and ear presets 7 marked unverified; D2 why updated for consistency; MMR-215 ear-preset count unfrozen; mapping M04, M08, M13, M15, M17 and M27 patched for consistency.
- **R15 Summary corrections.** Executive summary: the identity-plumbing sentence is rewritten per R1, and 'capability.tools cannot express' becomes the closed enum plus skills, preferences.autonomy='supervised', constraints.environments and a body gate. The art-ticket capability change was aligned to match.

## 10. Method, evidence and limits

**How the review ran.** Every pass was read-only against the plan and the game installs.

1. **Six lenses, independently.**
   - **Bannerlord engine:** ildasm over 23 assemblies and REA managed inspection.
   - **DD2 reference:** extraction plus custom RSZ, mesh and material parsers.
   - **Rendering and assets:** shader sources, texture headers and the local toolchain.
   - **Plan contracts:** all 63 tickets, read with a graph script.
   - **Cross-epic seams:** all epics.
   - **Player experience:** XML distributions, logs and the DD2 file list.
2. **Six verifiers.** Each re-derived the blocking claims with a different method:
   - a reflection-based IL scanner over 82 assemblies instead of ildasm text;
   - its own RSZ and mesh readers, with a positive control for blend shapes;
   - its own XML readers;
   - a parse of the DXIL shader bytecode;
   - direct re-reads of the tickets.
3. **Change map and review.** Two independent change maps (engine-first, and DD2-architecture-first), one merger and one completeness critic.
4. **Fixes.** One pass applied coordinator rulings on the critic, then a fix-and-recheck loop.
5. **My own checks.** I ran several checks myself: the face-seed `rem 2000`, the `Agent.AddSkinMeshes` false/false flags and the constructor parameter names, the wording of your lean-verification rule, and the peoples-epic decision comment.

**What was not done.**
- No game was launched: not DD2, not DD2CCS, not Bannerlord, not the editor.
- No REFramework script was installed.
- No game file was changed.
- Nothing was saved to Dark Mechanicus; every ticket change here is a proposal.
- No native provider was used. Ghidra and IDA are not configured, so everything inside `TaleWorlds.Native.dll`, `DD2.exe` and `DD2CCS.exe` stays an open unknown, each with a probe in §8.

**Where things are.**
- **DD2 extraction:** 1,166 files (641 MB) from `re_chunk_000.pak` and 5.8 MB from the DD2CCS pak, written by the DD2-modding-repo REtool helper to `DD2-modding-repo/local/extracted/` (git-ignored). None of it is in this repo.
- **Evidence archive:** scripts, parsers, JSON outputs and structured round results (lens, verify, map, merge, critic, fix) are in `DD2-modding-repo/local/research/character-creator-redteam-2026-10-18/` (git-ignored, local only).
  - The archive is kept out of this repo because it holds DD2-derived values; see D17.
  - The 250 MB of ildasm text dumps were not archived. They can be regenerated from the pinned DLLs.

**Corrections to the coordinator (recorded, not silently fixed).**
- **C1.** "skins.xml has a `<race>` wrapper, so multiple races are supported" was wider than its evidence. The file declares one race; the managed API is race-indexed; native acceptance is untested.
- **C2.** The "~90 head meshes" under `charaedit/ch000/parts/head/mesh` are `.pfb.18` prefabs. The real meshes live under `character/_kit/`: 85 human heads plus Beastren heads, 2 bodies, 85 hair, 48 beards and 26 brows.
- **C4.** "The face packing lives in TaleWorlds.Native.dll" is an inference from managed interop. It is not established.
- **C5.** The 10-16 pass also read manifests, logs and scripts, not only enum names.
- **Round 2.** My paraphrase "shader compile is editor-only" was wrong. The client compiles at runtime; only module-cache building is editor-only.
- **Ruling R2.** I said StoryMode copies body properties to the player's siblings. It copies them to the two parents only. Siblings get names, equipment and culture.

**Limits worth stating.**
- **Native behaviour is unobserved.** This covers key packing, race acceptance, agent scale, material-override persistence and texture formats. Each is a probe, not a fact.
- **DD2 runtime behaviour is unobserved.** This covers key evaluation curves, face-rig transfer to attachments and bake timing.
- **The DD2 RSZ field names come from community type databases** whose CRCs mismatch build 25920212. The structural invariants pass, but names could be stale.
- **File-list counts are upper bounds.** At least one listed path is not in the pak.
- **The TaleWorlds.Native.dll hash matched older evidence,** so it is probably stock, but nothing guards it.

## 11. Offline unknowns resolved (2026-10-18)

Five §8 unknowns (U9, U27, U28, U29, U31) are settled by static reading. No game was launched; nothing in the install, the plan or any epic was changed. Answers below come from ildasm on game v1.4.8.119303 and from the Dark Mechanicus 0.18.0 source and skills 1.5.1 (the running server reports the same versions). TaleWorlds.Core, MountAndBlade, CampaignSystem, ObjectSystem, SandBox and NavalDLC match the SHA-256 values in `config/runtime-api-lock.json`. View, GauntletUI, SandBox.View, SandBox.GauntletUI and StoryMode are not in that lock; they carry the 2026-10-09 install timestamp like the pinned ones. Longer evidence, scripts and raw scans are in the session notes folder, under `offline-unknowns-2026-10-18/`.

**U9. No: vanilla beards and brows carry no morph frames, so they do not follow face deform keys.**
- *Where they are.* Not the human tpac. The 41 active `beard_mesh` names (skins.xml) are in `pack3.tpac` (every beards_c_* and beards_d_a–k) and `human_facial.tpac` (beards_d_l, m, o, p, r, s). The 15 active `eyebrow_mesh` names are in `pack_temp_29.tpac`.
- *Decode.* tpac rows hold `(u64 offset, u64 raw size, u64 stored size)` at a 69-byte stride; payloads are raw LZ4 blocks (the first starts at table size + 36), or uncompressed when stored size equals raw size. A mesh blob is: u32 N, N × vec4 positions, more vertex streams, indices, **u32 K**, K × float32 frame times, K frame blocks, then the skin block.
- *Control.* `head_male_a` LOD0 (`human.tpac` row 29,768, N = 1,269) has K = 101, times 0.0–100.0, and 101 blocks of 42,536 bytes from byte 178,012. Its LOD1–5 have K = 0.
- *Result.* The 56 names have 284 mesh blobs: 273 with K = 0 at the skin boundary, 11 with no skin block and three trailing zero dwords. None has a frame table or repeated N-blocks. Whole-pack scans agree: of 422 mesh blobs in `pack3.tpac` only `banner_replacement_mesh` has frames (251); `human_facial.tpac` (145, hair included) and `pack_temp_29.tpac` (275) have none.
- *Binding (sampled).* Five beards (c_a, c_j, c_k, c_z, d_s) are 98.7–100% weighted to bone 13, the bone carrying 94% of `head_male_a`'s weight. Three brows: male_eyebrow_1 is 100% bone 13, male_eyebrow_7 89%, female_eyebrow_4 56% bone 12 and 43% bone 13, with small bone 11 and 21 shares. They ride with the head and neck bones and do not deform.
- *Simplifies:* mapping rows M09 and M10 and MMR-189 a6. Their "vanilla follow mechanism is unverified (U9)" becomes "vanilla has none", so the build-time frame transfer into beards and brows is the only mechanism and new work with no vanilla precedent, and a6 is a build gate, not a parity check. U9's fallback (a live capture at maximum face_width) is not needed. Hair meshes in `human_facial.tpac` have no frames either; M08 asks for none.
- *Side findings.* The shipped head carries 101 frames, with non-zero deltas at every time 1–100 (60–63: 1.6–5.7 mm; 64–82: up to 27 mm against frame 0). So the shipped format holds more than 63 frames on a head, which narrows U4 to the editor import path. For U7 the vanilla head has data at the bone-only and DCC time points; whether native samples them stays a live question.

**U27. Not checkpoint-only: a saved revision can be adopted while the run is paused.**
- *Evidence.* `adopt_revision` ("Allowed only at a checkpoint or while paused, with no open attempts") is enforced by `adoption.ts` `requireAdoptableRun`: run state `awaiting_checkpoint` or `paused`, and any claimed, running or submitted attempt fails with `conflict`. Nothing restricts the active sprint: `assertNoClosedSprintRework` guards only sprints before it, `chooseActiveSprint` keeps its id, and `runWarnings.ts` warns only about passed sprints and sprints inserted before the active one. `pause_run` works from queued, running and awaiting_checkpoint.
- *Path.* Stop claiming, let open attempts finish, `pause_run`, edit and `validate_plan` the draft, Save (you in the desktop app, or a `--allow-save` session), `adopt_revision`, `resume_run`. The sprint's acceptance node requires the new tickets automatically; update its covers criteria before it is claimed.
- *Limit.* The skills forbid doing this unprompted: orchestrator §6 "Do not add tickets to a running plan yourself, except in the redraft loop of section 5" (future sprints only), planner §7 "Edit only future sprints". `redraft_next_sprint` is checkpoint-only and only moves leftovers and adds discoveries; it cannot create batches. So active-sprint batches need your explicit go-ahead.
- *Simplifies:* MMR-167 a6 and its body sentence. Delete "drafted at the sprint-1 checkpoint from NEW-B's measured hours if U27 shows adoption is checkpoint-only". The sprint-2 slice batches are drafted from the frozen contract after MMR-167 is accepted and adopted by pause, Save, adopt and resume with no open attempts; the sprint-3 batches are adopted at the sprint-2 checkpoint, a future sprint with no conflict. In the SPRINTS paragraph, replace "U27 asks whether a revision can be adopted mid-sprint, which round2.json does not answer" with this answer. Option 1's in-sprint batch creation is feasible, so Option 2's separate 1b freeze round is not needed to create the batches.

**U28. Yes for all eight sites; no site needs a prefix on its Refresh call.**
- *Create is synchronous.* `AgentVisuals.Create` → `newobj .ctor` (IL_0006); the ctor stores the caller's `AgentVisualsData` reference as `_data` (IL_0008) and calls the private `Refresh` at IL_008a. That method has one `ret` (IL_0394) and calls `AddSkinMeshesToEntity` at IL_0234 (equipment null) or, for a humanoid (`AgentFlag` 0x800, IL_0027) with an empty mount key, at IL_031c through `AddSkinArmorWeaponMultiMeshesToEntity` (call IL_0017). `AddSkinMeshesToEntity` reads `CharacterObjectStringIdData` at IL_0124 on every path. A prefix on `Create` that sets the id on its argument is therefore seen in the same call.
- *Survival.* Public `Refresh(bool, AgentVisualsData, bool)` sets `_data = data` (IL_0008). Every Refresh in the sites passes `GetCopyAgentVisualsData()`, and the `AgentVisualsData` copy constructor copies the field (IL_014b–IL_0150). Only that constructor and the fluent setter (MobilePartyVisual.AddCharacterToPartyIcon, CharacterTableau.InitializeAgentVisuals, AgentVisuals.SetCharacterObjectID) write it; nothing clears it.

| Site | Create IL (fresh data) | Later Refresh and its data |
|---|---|---|
| CharacterCreationNarrativeStageView.SpawnHumanNarrativeMenuCharacter | IL_004e (CreateAgentVisual IL_00be) | none; respawn re-enters Create after Reset (ClearCharacterVisuals IL_0010) |
| CharacterCreationOptionsStageView.AddCharacterEntity | IL_015a (IL_0049) | none |
| CharacterCreationReviewStageView.AddCharacterEntity | IL_0154 (IL_0049) | none |
| CharacterCreationClanNamingStageView.AddCharacterEntity | IL_0124 (IL_0086) | RefreshCharacterEntity IL_0082; GetCopy IL_002a |
| BannerEditorView.AddCharacterEntity | IL_0142, IL_0210 (IL_00a4, IL_0172) | .ctor IL_0405, IL_0492; RefreshShieldAndCharacterAux IL_00f6; all GetCopy |
| GauntletBannerBuilderScreen.AddCharacterEntities | IL_01ab, IL_036e (IL_00bd, IL_0280) | IL_01d6, IL_0399; RefreshShieldAndCharacterAux IL_00b9; all GetCopy |
| GauntletEducationScreen.RefreshSceneCharacters | IL_01c4 (CreateAgentVisual IL_006b) | none; Reset IL_004e then Create |
| GauntletSceneNotification.OpenScene | IL_0367, IL_0428 (IL_02cb, IL_03b9) | none in the class; PopupSceneSpawnPoint.InitializeWithAgentVisuals IL_0138, IL_017f and PopupSceneSwitchItemSequence.AttachItem IL_008e, all GetCopy |

- *Simplifies:* the §6.2 gate [G:U28] and MMR-177 a1. The scope route and the `Create` prefix work as written; delete "Any site that drops the id gets a prefix on its Refresh call". The scope only has to be open around each `Create` call, and respawn paths re-enter `Create`, so the prefix/finalizer goes on the method that contains each Create listed above, not on an outer one.
- *Not traced.* A synthetic id for a pre-campaign creator record reaches `MBObjectManager.GetObject<BasicCharacterObject>(string)` at IL_013b. That body has no assert (the `FailedAssert` is in the MBGUID overload), but `GetMBObject(string)` was not read, so use a real CharacterObject id or check it in the live round.

**U29. No: Sandbox character creation never builds family heroes from the narrative parents.**
- *Evidence.* `CharacterCreationCampaignBehavior` (TaleWorlds.CampaignSystem.dll, not SandBox) implements `OnCharacterCreationFinalize` as a bare `ret` (Code size 1). `FaceGenUpdated` matches `NarrativeMenuCharacter.StringId` against "mother_character" (IL_00bb) and "father_character" (IL_00e1) and only calls `UpdateBodyProperties` (IL_00d5, IL_00fb). The class has no `HeroCreator`, `Hero` constructor, `CharacterObject.CreateFrom` or `Hero.set_Mother/set_Father` call.
- *Finalize path.* `CharacterCreationManager.NextStage` calls `ApplyFinalEffects` (IL_008d), which calls each handler's `OnCharacterCreationFinalize` (IL_0141), then `CharacterCreationState.FinalizeCharacterCreationState` (IL_0098), which calls `OnCharacterCreationFinalized` (IL_0045) and `OnCharacterCreationIsOver` (IL_0054). A call-graph walk from those, from `InitializeData` (378 methods), `ApplyCulture`, `NarrativeMenuOption.ApplyFinalEffects` and every `OnCharacterCreationIsOver` subscriber (Aging, CharacterDevelopment, DynamicBody, Education, HeroKnownInformation, NavalInitialization) reaches no hero-creating or parent-linking call. The same walk from `HeroCreator.CreateSpecialHero` does find them.
- *Only consumer.* `StoryModeCharacterCreationCampaignBehavior.FinalizeParentsAndLittleSiblings` (StoryMode.dll) copies `NarrativeMenu.Characters` into existing heroes. Sandbox has no equivalent, and NavalDLC's `UpdateParentEquipment` only sets equipment and animation on the records.
- *Simplifies:* MMR-178 a7 and D5 option (a). Drop "In Sandbox they are any family heroes built from the narrative mother_character and father_character; whether Sandbox creates such heroes was not traced" and "and any Sandbox family heroes". `FamilyRaceWriter` is StoryMode-only, and §6.1's Sandbox bullet reduces to "narrative parents are previews". The previews still need identity, which U28 covers.

**U31. No: Dark Mechanicus dispatch matching does not read `constraints.environments` as a filter.**
- *Evidence.* `matchProfile` (`src/core/plan/capabilities.ts`) rejects a model only for a missing modality, a reasoning level above its highest, an input estimate above its context window, or a `modelOverride` mismatch; missing host tools go to `hostFailures`. `unknownRequirements()` (lines 102–112) turns each environment into the text `Environment requirement "<x>" cannot be verified: the host catalog declares no environments.` `HostCatalog` has no environments field, so it can never be met, and `dataLocation`, `maxDurationMinutes` and `maxCostUsd` are not read either. `claim_ticket` never refuses on it (`attempts.ts` `assignmentFallback` uses eligible and rejected only), and `readiness.ts` ignores capability data. Orchestrator skill, execution loop step 2: "Never treat `unknownRequirements` as satisfied: ask the person."
- *Simplifies:* the art-ticket capability change. Keep the body gate; keep `environments` only as an advisory flag, because every `match_capabilities` call will list it in `unknownRequirements` and the skill tells the orchestrator to ask you. Replace "Whether dispatch matching reads constraints.environments is unknown (U31)" with "it does not; the body gate is the only enforcement".

**Still open from these five.** Native runtime shape-projection of beards that is not stored in the assets (U9; no evidence for it, and the optional capture settles it), and `GetMBObject(string)` behaviour for an unregistered id (U28).
