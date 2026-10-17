# Validating the whole generated campaign graph before activation (MMR-36, 2026-10-15)

A generated world reaches the engine as XML documents from several factories. Each factory checks its own rules, and the MMR-30 registry checks its registration plan. Neither sees the whole graph. This ticket adds that view: one read-only validation of everything staged, run before anything registers. It reports every failure at once.

It is the single owner of full reference closure and the unexpected-Calradia census. Later tickets cite it rather than repeat it.

| | |
|---|---|
| Validator | `src/MNBL.ProceduralWorld/Campaign/CampaignGraphValidator.cs` (pure managed), with the shared reference walker `Campaign/ReferenceSchema.cs` (also used by `GeneratedCampaignDataset`; it reports each reference with its property path) |
| Policy | `worldgen/policies/graph-validation.json`, written by `tools/make_graph_policy.py`; embedded in the module (`MNBL.ProceduralWorld.graph-validation.json`), so every registration uses a fixed, hashed policy |
| Gate | `GeneratedCampaignRegistration.Register`: after the dataset is built, before the fixture prefabs, the registry plan or any document load. The fixture world's input is built by `FixturePlacement.GraphInput`, shared with the offline tests |
| Rollback | a failure after the gate, while loading or at closure, is rolled back through C01 (`CampaignObjectRegistry.RollBack` → `RemoveWorldObjects`; `GeneratedGameManager` does the same when the post-registration closure fails) |
| Tests | `tests/Campaign/GraphFailureFixtures.cs` via `tools/Test-GraphValidation.ps1`: 70 checks (`evidence/campaign/graph-tests.txt`, `evidence/campaign/graph-validation.json`). The rollback checks are in `tests/Campaign/RegistrationTests.cs` (`tools/Test-CampaignRegistry.ps1`, 59 checks) |
| In game | the `generated_campaign` probe with `graphFault=<classes>` (`Automation/GraphGateProbe.cs`, fault classes in `Campaign/GraphFaults.cs`), summarized by `tools/summarize_graph_gate.py` (`evidence/runtime/graph-gate.json`) |

## What it validates

`CampaignGraphValidator.Validate(policy, input)` takes:
- the staged documents;
- the declared generated objects and the world aliases (`CampaignIdentity.Aliases`);
- the installed catalog and the verified live defaults;
- for a compiler world, `campaign.json` and `id-map.json`, plus what the factories produced:
  - the slot resolutions and dispositions (`CampaignGraphValidator.Resolutions`, `Dispositions`);
  - MMR-33's declared village rebindings;
- the runtime startup plan (MMR-34);
- the world's spawn positions: spawn anchors, player starts and pirate zones, or a fixture world's start points and starting party gates;
- the path and sha256 of each resolving policy, the catalog receipt and the live default inventory.

Every diagnostic carries a code, the source UID (from the id map), the generated id, the property path (`Components/Village/@bound`; for a failure inside an installed template, `<template>:<path>` against the generated record that reached it) and the reason.

Every stage runs whatever an earlier one found, and an input a stage cannot read becomes a diagnostic, never an exception. So one run reports every failure.

| Code | Check |
|---|---|
| `graph.malformed_input` | every staged document has a root, and every record element and settlement component an id (the registry and the loaders would skip it silently). Every disposition names a UID or id; every id-map entry has its uid, id and kind; every exclusion its id, once; every compiler slot its owner and name |
| `graph.unknown_type` | every staged record is an element with an explicit schema (`ReferenceSchema.ElementType`) of a type the installed assemblies register (`evidence/runtime/object-type-registrations.txt`) |
| `graph.duplicate_id` | no `Type.id` is staged twice |
| `graph.unknown_object` | each record is either generated (world prefix or declared), a world alias, or an installed record of a reusable template type (cultures, characters, equipment rosters, party templates, workshop types, location complexes) |
| `graph.unexpected_vanilla` | no installed Calradia settlement, clan, kingdom or hero is staged, and no character of an installed hero. Its components are counted with it and its own references are not walked |
| `graph.official_reference` | no reference reaches an installed Calradia world instance or hero character that the world does not stage. A world alias counts only when it is staged |
| `graph.malformed_reference` | every reference parses the way the installed loaders read it |
| `graph.unresolved_reference` | full closure. Every reference of every staged record (top level, component or nested) resolves to one of: a staged record, an installed template (followed transitively; a broken reference inside a template is reported against the generated record that reached it), or a verified live default. References the installed loader tolerates when missing (`?<contract>\|Type.id`, e.g. an unknown skill id) are listed as optional, not refused |
| `graph.reference_type` | every typed property of a generated or alias record names an object of the type the installed `Deserialize` reads it as. Examples: a hero's `faction` is a clan; a settlement's `owner` is a clan; a kingdom's or clan's `owner` is a hero; `super_faction` is a kingdom; `bound` is a settlement; `village_type` is a village type; `culture` is a culture; upgrade targets and troops are characters; a location complex is a location complex template |
| `graph.ownership` | each kingdom's ruler leads exactly one of its clans (the ruling clan) and its capital is held by one of its clans; each clan has a staged leader of that clan; each town or castle has an owner |
| `graph.hero_role` | each hero has a staged hero character of its id (`is_hero`, not a template) and a clan; rulers, clan leaders and members of the world's noble clans are Lords |
| `graph.settlement_component` | each settlement has exactly one component of a supported kind (Town, Village, Hideout, the retreat's component), and the location complex that every installed settlement of its kind has (derived from the catalog: towns, castles, villages, hideouts and the retreat). Villages have a type. Building types resolve through closure (they are live defaults) |
| `graph.village_binding` | each village is bound to a town or castle of the world (its market) |
| `graph.port` | each port town has `building_shipyard` (installed `CanTownCreateShipFromHull` dereferences it), and each shipyard belongs to a town with a port |
| `graph.troop_upgrade` | the upgrade trees of every character the world stages or reaches through closure (installed troop trees included) have no cycle |
| `graph.party_intent` | each clan with lords has a town or castle home, where its lords' parties start, and each bandit clan has a staged home. Each runtime notable or companion has an installed template of its occupation (policy `runtimeHeroes`) and a generated home of its kind (notables: towns and villages; companions: towns). Each runtime relation joins two heroes of the world |
| `graph.fixed_position` | no settlement position, gate or port, no staged culture start point and no spawn position lies within 0.01 of an installed Calradia coordinate (any installed settlement, gate, port or culture start point) |
| `graph.silent_discard` | every compiler source UID (the id map's entries and `notInstantiated`) has a staged object, a runtime intent or an explicit disposition. A disposition claiming "instantiated" with nothing behind it is refused |
| `graph.disposition` | a disposition's outcome is a policy outcome (`dissolved`, `bounded`, `notInstantiated`, `excluded`) and gives a reason. A staged record or runtime intent has no contradicting disposition or exclusion |
| `graph.source_mismatch` | each staged record agrees with its compiler record: its kind (a compiler town is a town, not a castle); a village's binding, unless MMR-33 declared a rebinding with a reason; a town's port |
| `graph.pending_slot` | every compiler slot whose owner is delivered (MMR-27/28/31/32/33/35) is resolved. Where the staged graph can show the resolution, it must. Examples: a banner key, tier and kingdom for a clan; banner and colours, policies and a culture with basic and elite troops for a kingdom; prosperity for a town; type and hearth for a village; port and shipyard for a port; a hero's character and age; a garrison culture with a party template. Slots of records with a disposition are moot. Slots of owners not yet delivered are listed as deferred, never passed |
| `graph.policy_identity` | every snapshot names the graph policy, the catalog receipt and the live default inventory; a compiler world's also names every resolving policy and template |

## Census and dispositions

The census counts staged objects by type in each class: generated, aliases, re-emitted templates, unexpected vanilla, and unknown. It also counts the installed templates and live defaults the closure reaches; the full reached set is in the report. The real size-129 world, through all five factories (9 documents), validates with this census, recomputed in the test from the factories' results and the startup policy:

| Class | Objects |
|---|---|
| Generated | 92 clans, 40 kingdoms, 241 heroes and their 241 characters, 358 settlements (45 towns, 26 castles, 215 villages, 72 hideouts), with their 71 Town, 215 Village and 72 Hideout components |
| World aliases | 9 clans (the land bandit and pirate clans), plus the retirement retreat and its component |
| Re-emitted installed templates | 7 creation cultures (the startup policy's), carrying generated start points |

The closure reaches 3758 installed templates and live defaults, and 141 troop upgrade trees are checked. The test recomputes, without the validator, that every one of the 862 installed records the staged records name directly is among them, and that none is a Calradia world instance or hero character.

There are 711 source UIDs:
- staged: 45 towns, 26 castles, 215 villages, 40 clans, 40 kingdoms, 92 heroes;
- runtime intents: 200 heroes;
- explicit dispositions: 35 heroes bounded, 12 not instantiated, 6 villages excluded.

The factories also synthesize objects of their own, which are counted in the census: 52 vassal clans (MMR-31), and 149 XML heroes plus 737 runtime hero intents (MMR-32). 44 villages follow MMR-33's declared rebindings.

Slots:
- 2158 slots of delivered owners are resolved and, where the graph can show it, shown.
- The 70 slots of the 35 bounded heroes are moot.
- The 71 `prototypeAndScenes` slots (towns and castles) are listed as deferred to MMR-42.

The real bundle dissolves no realm, so a synthetic manifest tests that path: a dissolved realm and its clan get the factories' dispositions, and their slots are moot.

The report binds the identity of what the world resolved from, for MMR-45's ActivationReceipt to bind with the compiler's WorldContentId:
- the 15 resolving policies and templates: factions, presentations, rosters, economy, startup, naval, the settlement prototypes and the culture templates;
- the graph policy, the catalog receipt and the live default inventory;
- each with its path and sha256, plus a combined sha256.

The report's own canonical sha256 binds the staged documents' hashes and the default inventory, so two equal report hashes mean the same graph and inputs. Validating the same graph twice gives the same report.

The two in-game fixture worlds are validated offline exactly as the gate validates them. `FixturePlacement` is shared with `Prepare` and the gate, so the offline datasets and inputs are the in-game ones.
- World `mnbl_00059042_` (settlement, startup and naval fixtures): 2 kingdoms, 2 clans, 2 rulers, 11 settlements. Aliases: 10 clans, the player hero and character, and the retirement retreat with its component. Re-emitted: 7 cultures, 888 characters, 12 workshop types and 6 location complexes.
- World `mnbl_00059043_`: the plain fixture world.

Each world's census of generated objects and aliases equals the dataset's own declared list.

## Fault injection (c1)

Each fault class is injected into a fresh copy of the complete world. It must be reported with its code and only the codes it implies. The 55 classes cover these groups.

**Identity and input**
- an element with no object type;
- a record element without an id;
- a document without a root;
- a settlement staged twice;
- an object of another world;
- a disposition, id-map entry or slot missing its keys;
- an exclusion listed twice.

**References**
- missing references: at the top level (a hero's culture), in a component (a building type) and nested (an equipment set);
- a missing reference inside an installed template (an item of a roster a generated hero uses), pointed at that hero;
- a malformed reference (its clan's leader then belongs to no clan);
- wrong-type references: a hero's clan is a kingdom, a castle's owner is a hero, a village's type is a clan.

**Calradia**
- an installed town, hero, kingdom or clan staged;
- an installed hero's character staged;
- references to an installed clan, and to an installed hero's character;
- a reference to the retreat alias once the world stops staging it;
- a town, a culture start point or a spawn anchor at an installed coordinate.

**Semantics**
- a kingdom whose ruler leads none of its clans, and one whose capital is held outside it;
- a hero whose character is not a hero, and a clan leader who is a Wanderer;
- two components on one settlement;
- a town or village without its location complex;
- a village bound to a village;
- a port town without its shipyard, and a castle with one;
- a troop cycle among staged characters, and one inside an installed troop tree the world reaches;
- a clan homed at a village.

**Runtime intents**
- a notable whose template is a lord;
- a notable homed at a hideout;
- a relation to no hero.

**Sources**
- a compiler village dropped;
- a staged compiler hero removed although its disposition says instantiated;
- a disposition with no outcome, and one outside the policy;
- a staged town that is also excluded;
- a compiler town staged as a castle;
- a port town staged without its port;
- a village bound to another town than its compiler record;
- a kingdom without the policies its slot claims;
- a delivered slot left pending;
- a policy missing from the snapshot.

All 55 together give 77 diagnostics covering 22 codes in one pass. An unknown skill on a character is noted as optional, not refused.

## The gate (c3)

`Register` builds the dataset, creates the documents, and validates them with the embedded policy and the documents' own inputs. These are:
- the installed catalog the dataset was built from;
- the live defaults it was checked against;
- its declared objects, runtime plan and positions;
- the catalog receipt and settlement fixture policies.

A refused world throws `CampaignGraphValidator.GraphException`, carrying the report, before the map scene's fixture prefabs, the registry plan and any document load. Earlier in the new game, the game manager and the naval models are created and the dataset build counter is incremented. These hold none of the world's objects, and a refusal there ends the game's initialization. The registration report records the result as `graphValidation`.

A failure after the gate, which the validator does not predict (a load error, or a closure failure in the registry or after every submodule registered), is rolled back through C01 before it propagates. The fake engine tests cover both a closure failure and a load that fails at its last document: in each, no object of the world remains, while the templates and the pre-existing `main_hero` stay.

A refusal inside the engine's registration callback ends the process (`GeneratedGameManager.Traced` rethrows), so the probe observes the gate while the first world is live. For each class in `GraphFaults`:
1. It builds a fresh registration of the second world's placement.
2. It applies the fault, then calls the real `Register`.
3. It records the refusal and the stages reached: the primary evidence is that the run stops at the gate, before any registry plan or document load.
4. As corroboration, it records the second world's objects and every census type's total in the object manager and the campaign lists, before and after; whether the fixture prefabs changed; and whether a registry was created.

The same classes are refused offline on the same fixture world.

## Limits and handoffs

- **Saved games.** A load replays the frozen package without the catalog, so it is not re-validated. Binding the report's policy identity to the ActivationReceipt and resolving a load from those frozen versions is MMR-45's.
- **Runtime checks.** Positions on the generated scene and its navigation mesh (W05) and route reachability between settlements (U03) are listed in every report as pending, not checked.
- **Activation path (MMR-45).** The size-129 world is validated whole at activation (`CompiledWorldDataset`, BuildWorld), with the factories' spawn anchors, rebindings, slots and dispositions, against the pinned catalog. The in-game graph report (`a8275a2f…`, C07 of runs 22472, 9404, 43676) is the offline one (`tools/Test-CompiledWorld.ps1`).
- **Rollback in game.** The in-game rollback path is covered by the fake engine tests, not exercised in game. Loading a faulted world into a live campaign to roll it back would re-merge its re-emitted templates into that campaign.
- **MMR-42.** Its `prototypeAndScenes` slots stay deferred until it is delivered. Its owner then moves from `deferredOwners` to `deliveredOwners` in the policy, and any slot still pending is refused.
