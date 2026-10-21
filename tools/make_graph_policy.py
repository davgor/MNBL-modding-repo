"""MMR-36: write worldgen/policies/graph-validation.json, the versioned policy CampaignGraphValidator reads (docs/campaign-graph-validation.md).

It fixes, outside the game, what a staged generated campaign may contain and which compiler slots must be resolved before activation:
  * registeredTypes: the object types the installed assemblies register (evidence/runtime/object-type-registrations.txt, the MMR-30 scan);
    a staged record of any other element is refused;
  * worldTypes: the types whose installed records are Calradia world instances (settlements, clans, kingdoms, heroes); staging or
    referencing one that is not a world alias is refused;
  * reusableTypes: the installed record types a world may re-emit unchanged or adapted (templates: cultures, characters, rosters, party
    templates, workshop types, location complexes); any other installed record staged in a world is refused;
  * settlementComponents: the settlement kinds a world may stage (Town, Village, Hideout, the retirement retreat's component);
  * deliveredOwners: the tickets whose compiler slots (campaign.json) are resolved by accepted factories, so a pending slot of theirs is
    refused; deferredOwners: the tickets not yet delivered, whose slots are listed as deferred (never passing silently);
  * policies: the resolving policies and templates whose identities (sha256) a compiler world's report binds (MMR-45: ActivationReceipt);
    requiredInventory: what every world's report binds (the graph policy itself, the catalog receipt, the live default inventory);
  * dispositions: the outcomes a compiler record without a generated object may have (each with a reason);
  * runtimeHeroes: the installed occupations of runtime notables' and companions' templates;
  * pendingRuntime: the runtime position and route requirements this read-only validation does not check;
  * positionTolerance: two coordinates closer than this on both axes are the same point (an installed Calradia coordinate copied).
Usage: python tools/make_graph_policy.py
"""
import collections
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN = 'evidence/runtime/object-type-registrations.txt'


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def od(*pairs):
    return collections.OrderedDict(pairs)


def main():
    registered = []
    with open(os.path.join(REPO, SCAN), encoding='utf-8-sig') as f:
        for line in f:
            if line.strip() and not line.startswith('#'):
                element = line.split()[0]
                if element not in registered:
                    registered.append(element)
    world = ['Faction', 'Hero', 'Kingdom', 'Settlement']
    reusable = ['Culture', 'EquipmentRoster', 'LocationComplexTemplate', 'NPCCharacter', 'PartyTemplate', 'WorkshopType']
    components = ['Hideout', 'RetirementSettlementComponent', 'Town', 'Village']
    for t in world + reusable + components:
        assert t in registered, t
    templates = sorted('worldgen/templates/cultures/' + f for f in os.listdir(os.path.join(REPO, 'worldgen/templates/cultures')) if f.endswith('.json'))
    policies = ['worldgen/policies/culture-presentations.json', 'worldgen/policies/economy.json', 'worldgen/policies/factions.json',
                'worldgen/policies/naval-content.json', 'worldgen/policies/rosters.json', 'worldgen/policies/population.json', 'worldgen/policies/startup.json',
                'worldgen/templates/settlements/prototypes.json', 'worldgen/policies/mission-scenes.json'] + templates
    for p in policies:
        assert os.path.isfile(os.path.join(REPO, p)), p
    policy = od(
        ('schema', 'mnbl.graph-validation'),
        ('version', 1),
        ('rule', 'CampaignGraphValidator refuses a staged generated campaign with any diagnostic, before it registers; the codes are its '
                 'diagnostic classes (graph.*); a refusal is never retryable on its own (it is a factory or policy defect, not a seed)'),
        ('registeredTypes', sorted(registered)),
        ('worldTypes', world),
        ('reusableTypes', reusable),
        ('settlementComponents', components),
        ('deliveredOwners', ['MMR-27', 'MMR-28', 'MMR-31', 'MMR-32', 'MMR-33', 'MMR-35', 'MMR-42', 'MMR-279', 'MMR-280']),
        ('deferredOwners', od()),
        ('policies', policies),
        ('requiredInventory', ['worldgen/policies/graph-validation.json', 'runtime:catalogReceipt', 'runtime:liveDefaults']),
        ('dispositions', od(('dissolved', 'MMR-31: a realm with no town after the compiler and travel exclusions, and its clans'),
                            ('bounded', 'MMR-32: a source hero beyond the bound the roster policy keeps'),
                            ('notInstantiated', 'MMR-27/32: a source hero the compiler or the hero factory does not instantiate'),
                            ('excluded', 'MMR-27: a compiler exclusion (campaign.json exclusions)'))),
        ('runtimeHeroes', od(('notableOccupations', ['Artisan', 'GangLeader', 'Headman', 'Merchant', 'Preacher', 'RuralNotable']),
                             ('companionOccupations', ['Wanderer']))),
        ('pendingRuntime', ['W05: runtime settlement, gate and port positions on the generated scene and its navigation mesh (not checked here)',
                            'U03: runtime route reachability between generated settlements (not checked here)']),
        ('positionTolerance', 0.01),
        ('minorClans', od(
            ('rule', 'MMR-280: mercenary companies and outlaw clans are minor clans (no super_faction) owned by a member, homed at a town, with default_party_template and '
                     'the 4 minor_faction_character_templates of their population.json troopTemplates line, never a lord party template'),
            ('company', ['is_minor_faction', 'is_clan_type_mercenary']),
            ('outlaw', ['is_minor_faction', 'is_outlaw']),
            ('outlawKinds', ['is_mafia', 'is_nomad', 'is_sect']),
            ('characterTemplates', 4))),
        ('childGenerator', od(
            ('rule', 'InitialChildGenerationCampaignBehavior clones each child from a same-sex adult of a non-minor clan and dresses it from the four civilian rosters '
                     'of its culture (Lord + child or teenager, male and female); with no same-sex adult it draws from another clan of the culture and can throw at new game'),
            ('rosters', 'the EquipmentRosters of the culture flagged IsLordTemplate with IsChildEquipmentTemplate or IsTeenagerEquipmentTemplate, for each value of IsFemaleTemplate'),
            ('adultAge', 18))),
        ('codes', od(
            ('graph.policy', 'the validation policy is malformed'),
            ('graph.malformed_input', 'a staged document without a root, a record element or component without an id, or a disposition, id-map entry, exclusion or slot without its keys'),
            ('graph.reference_type', 'a reference naming an object of another type than its property holds'),
            ('graph.disposition', 'a disposition that is not a policy outcome, gives no reason, or contradicts a staged object'),
            ('graph.source_mismatch', 'a staged record that disagrees with its compiler record (kind, village binding, port)'),
            ('graph.unknown_type', 'a staged record of an element with no supported or registered object type'),
            ('graph.duplicate_id', 'the same Type.id staged twice'),
            ('graph.unknown_object', 'a staged record that is neither generated (world prefix), a world alias nor a permitted re-emitted template'),
            ('graph.unexpected_vanilla', 'an installed Calradia settlement, clan, kingdom or hero, or the character of a Calradia hero, staged in the generated world'),
            ('graph.official_reference', 'a reference to an installed Calradia world instance (or hero character) the world does not stage; world aliases count only when staged'),
            ('graph.malformed_reference', 'a reference value the installed loaders cannot parse'),
            ('graph.unresolved_reference', 'a reference resolving to nothing staged, installed or verified as a live default'),
            ('graph.ownership', 'a kingdom without its ruler, ruling clan or capital; a clan without its leader; a fief without an owner'),
            ('graph.hero_role', 'a hero without its hero character or clan; a ruler, clan leader or noble clan member who is not a Lord'),
            ('graph.settlement_component', 'a settlement without exactly one component of a supported kind, the location complex every installed settlement of its kind has, or its village type'),
            ('graph.village_binding', 'a village not bound to a town or castle of the world, or a generated town or castle with no bound village (RT-31)'),
            ('graph.port', 'a port town without a shipyard, or a shipyard without a town port'),
            ('graph.troop_upgrade', 'a cycle in the upgrade tree of a character the world stages or reaches'),
            ('graph.party_intent', 'a clan with lords but no fortified home, a bandit clan without a home, a runtime hero without an installed template of its occupation or a home of its kind, a runtime relation to no hero of the world'),
            ('graph.fixed_position', 'a settlement, start point or spawn position at an installed Calradia coordinate (settlement, gate, port or start point)'),
            ('graph.silent_discard', 'a compiler record with neither a generated object nor an explicit disposition'),
            ('graph.pending_slot', 'a compiler slot still pending whose owner is delivered (or not deferred), or claimed resolved while the staged graph lacks it'),
            ('graph.policy_identity', 'a resolving policy or required inventory missing from the identity snapshot'),
            ('graph.minor_clan', 'a mercenary company or outlaw clan (campaign v2, MMR-280) without the flags of its kind, a default party template, four installed character templates or a town home, or with a kingdom'),
            ('graph.census', 'the staged heroes and clans disagree with the campaign v2 census (houses, adult lords, minors, dead parents, company and outlaw members, wanderers W)'),
            ('graph.child_generator', 'a noble house the random child generator of the engine cannot serve: no adult Lord of each sex, or a lord culture without the four child and teen civilian equipment rosters'))),
        ('provenance', od(('objectTypeScan', SCAN), ('objectTypeScanSha256', sha(os.path.join(REPO, SCAN))))),
    )
    out = os.path.join(REPO, 'worldgen/policies/graph-validation.json')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(policy, f, indent=1)
        f.write('\n')
    print('wrote', out, len(registered), 'registered types,', len(policies), 'resolving policies')


if __name__ == '__main__':
    main()
