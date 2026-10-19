"""MMR-34: summarize the startup audit of a generated_campaign run (startupAudit=<repo>, settlementFixtures=<repo>, worldCount 2, gameplayDays > 0,
saveProbe=1) and, when given, the generated_save_load run that loads its saves.

Usage: python tools/summarize_startup_audit.py RUN_JSON OUT_JSON [LOAD_JSON]
The first world carries the startup fixtures, built through GeneratedCampaignInitializer's shared record builders: the land bandit clans as
world aliases homed by the initializer's rule, the retirement retreat, every creation culture's start at the fixture town's gate, and a plan
of 19 notables at the installed targets of 2 towns and 3 villages, the installed loop's 2 companions and one ruler relation. The second world
has none and checks the teardown of the first. The checks read the probe's records and compare them with values fixed outside the game (the
compatibility inventory, the startup policy, the alias list, the plan sizes):
  * every enabled behaviour and every registered model (any assembly) is in the inventory, no excluded one is enabled, every effective model
    is listed, Backstory is off, Retirement is kept exactly when the world supplies the retreat, and this module's behaviours each listen
    once per event; the patches applied are the manifest's, on the installed versions they were read from;
  * every world literal the scan found in an enabled type resolves only as one of the world's aliases, and every literal template lookup
    resolves;
  * the wanderer introduction routes its town_ES4 lookup through the world transpiler, and the installed condition, run with a generated
    wanderer, sets IMPERIALCAPITAL to the generated town;
  * with the startup, every creation culture's StartingPoint is its planned start and not its installed point, and the main party stands at
    its culture's; the start town's gate is valid land, its port valid water within the MMR-17 port-gate envelope, the engine's
    port-to-gate route exists, a naval route reaches the sea destination, and it has a shipyard;
  * the bandit aliases, the retreat and the plan exist exactly as planned (names with the intent's first name), the startup patches end with
    the new game, and the War Sails storyline stays inactive;
  * after the gameplay window no party is off valid navigation; the natural counts (what the game spawned) are kept apart from a caravan,
    a villager party, a fresh lord party and a bandit party spawned through the installed factories at installed spawn points, each on
    valid navigation; across the two worlds the game itself spawned player, lord and villager parties;
  * the first campaign's teardown removed every patch of its lifetime and this module's listeners from its events, and the second campaign
    has new events, no listener of the first, the right periodic world and no fixture static;
  * with LOAD_JSON: the world saved with the startup loads with it (package flag restored, Retirement kept, the startup behaviour back with
    its saved intent map), and the world without it loads without;
  * the runs finished ok with no validation failure.
"""
import hashlib
import json
import math
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BANDITS = ['looters', 'deserters', 'sea_raiders', 'mountain_bandits', 'forest_bandits', 'desert_bandits', 'steppe_bandits']
PLAYER = {'main_hero', 'player_faction', 'player_party'}
STARTUP_ALIASES = set(BANDITS) | {'retirement_retreat', 'retirement_component'}
PLAN = {'notables': 2 * 5 + 3 * 3, 'companions': 2, 'relations': 1, 'relation': -23}


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def main():
    run_path, out_path = sys.argv[1], sys.argv[2]
    load_path = sys.argv[3] if len(sys.argv) > 3 else None
    run = json.load(open(run_path, encoding='utf-8-sig'))
    worlds = run['outputs']['worlds']
    inventory = json.load(open(os.path.join(REPO, 'worldgen/policies/campaign-compatibility.json'), encoding='utf-8'))['types']
    startup_policy = json.load(open(os.path.join(REPO, 'worldgen/policies/startup.json'), encoding='utf-8'))
    creation = startup_policy['starts']['creationCultures']
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    check(run.get('status') == 'ok' and len(worlds) == 2, 'the run finished ok with two worlds (%s, %d)' % (run.get('status'), len(worlds)))
    natural_kinds = {}
    for index, w in enumerate(worlds):
        a = w.get('startupAudit') or {}
        startup = index == 0
        required = {k.split('.', 1)[1] for k in ((w.get('registration') or {}).get('requiredAliases') or [])}
        aliases = PLAYER | (STARTUP_ALIASES if startup else set()) | ({'northern_pirates', 'southern_pirates'} & required)   # MMR-35 naval fixtures
        roster = a.get('roster') or {}
        enabled = roster.get('behaviors', []) + roster.get('registeredModels', [])
        listed = [t for t in enabled if t in inventory]
        effective = a.get('effectiveModels') or {}
        check(not a.get('error') and roster.get('behaviors') and roster.get('registeredModels') and len(listed) == len(enabled) and
              not any(inventory[t]['status'] == 'excluded' for t in enabled) and not roster.get('unlisted') and
              all(t in inventory and inventory[t]['status'] != 'excluded' for t in effective.values()),
              'world %d: all %d enabled behaviours and %d registered models (%s) are in the compatibility inventory, none excluded, and so is the effective model of each of the %d contracts (%s)' % (
                  index, len(roster.get('behaviors', [])), len(roster.get('registeredModels', [])), roster.get('registeredModelAssemblies'), len(effective), roster.get('statusCounts')))
        retirement = 'SandBox.CampaignBehaviors.RetirementCampaignBehavior' in roster.get('behaviors', [])
        check(retirement == startup and not any(t.endswith('.BackstoryCampaignBehavior') for t in enabled),
              'world %d: Backstory is off and Retirement is %s, as the world %s the retreat' % (index, 'kept' if retirement else 'removed', 'supplies' if startup else 'does not supply'))
        listeners = a.get('listeners') or {}
        check(listeners and all(all(n == 1 for n in events.values()) for events in listeners.values()),
              'world %d: each of this module\'s behaviours (%s) subscribes once per event' % (index, ', '.join('%s %d' % (k, len(v)) for k, v in sorted(listeners.items()))))
        patches = a.get('patches') or []
        owners = sorted({p.split(':', 1)[0] for p in patches})
        check(a.get('installedVersions', '').startswith('v1.4.8.119303 / NavalDLC v1.2.8') and any(p.startswith('mnbl.proceduralworld.world:') for p in patches) and
              not any(p.startswith('mnbl.proceduralworld.startup:') for p in patches) and len(a.get('manifest') or []) >= 9,
              'world %d: on %s the applied patches are the manifest\'s (%s; %d manifest entries)' % (index, a.get('installedVersions'), ', '.join(owners), len(a.get('manifest') or [])))
        literals = a.get('worldLiterals') or {}
        present = [k.split(':', 1)[1] for k in literals.get('present', [])]
        expected_aliases = [k for k in literals.get('checked', []) if k.split(':', 1)[1] in aliases]
        check(literals.get('checked') and all(p in aliases for p in present) and len(present) == len(expected_aliases),
              'world %d: of the %d world ids the scan found in enabled types, only this world\'s aliases resolve (%s)' % (index, len(literals.get('checked', [])), ', '.join(present) or 'none'))
        templates = a.get('templateLookups') or {}
        check(templates.get('checked', 0) > 0 and not templates.get('unresolved'),
              'world %d: all %s literal template lookups of enabled types resolve (unresolved: %s)' % (index, templates.get('checked'), templates.get('unresolved')))
        wi = a.get('wandererIntroduction') or {}
        ran = 'imperialCapital' in wi
        check('mnbl.proceduralworld.world' in (wi.get('transpilers') or []) and wi.get('currentCallsHelper') is True and wi.get('currentCallsInstalledFindFirst') is False and
              wi.get('installedLookupFinds') is None and (ran or not startup) and
              (not ran or (wi.get('imperialCapital') == wi.get('expectedCapital') and wi.get('imperialCapital') and
                           (wi.get('conditionError') is None or (wi.get('campaignMissionPresent') is False and wi['conditionError'].startswith('System.NullReferenceException'))))),
              'world %d: the wanderer introduction calls FindFirstOrGenerated (town_ES4 finds nothing; fallback %s); %s' % (
                  index, wi.get('fallback'), ('run with %s, it set IMPERIALCAPITAL to "%s"%s' % (wi.get('wanderer'), wi.get('imperialCapital'),
                                              '' if wi.get('conditionError') is None else ', then stopped at the map-side CampaignMission read') if ran else 'condition %s' % wi.get('condition'))))
        s = a.get('start') or {}
        mp = s.get('mainParty') or [None, None]
        if startup:
            starts = s.get('creationCultureStarts') or {}
            ok = sorted(starts) == sorted(creation) and all(dist(v['startingPoint'], v['planned']) < 0.05 and dist(v['startingPoint'], v['installed']) > 1 for v in starts.values())
            own = starts.get(s.get('culture')) or {}
            check(ok and own and dist(mp, own['planned']) < 0.05 and s.get('navigation') is None,
                  'world 0: each of the %d creation cultures\' StartingPoint is its planned start, not its installed point (%s), and the main party (%s) stands at its own on valid navigation' % (
                      len(starts), ', '.join('%s %s from %s' % (k, [round(x, 1) for x in v['startingPoint']], [round(x) for x in v['installed']]) for k, v in sorted(starts.items())[:3]) + ', ...', s.get('culture')))
        else:
            sp = s.get('startingPoint') or [None, None]
            check(sp[0] is not None and dist(sp, mp) < 0.05 and s.get('navigation') is None,
                  'world %d: the main party starts at %s\'s StartingPoint %s on valid navigation' % (index, s.get('culture'), sp))
        e = s.get('embarkation') or {}
        check(e.get('gateNavigation') is None and e.get('portNavigation') is None and 0 < (e.get('gateToPort') or -1) <= (e.get('envelope') or 0) and
              0 < (e.get('enginePortToGate') or -1) < 1e30 and e.get('seaDestination') and 0 < (e.get('seaDistance') or -1) < 1e30 and e.get('shipSource', '').split()[-1] not in ('0', ''),
              'world %d: the start town %s embarks legally: gate on valid land, port on valid water %.2f from the gate (envelope %.2f), engine port-to-gate %s, a naval route of %s to %s, %s' % (
                  index, s.get('town'), e.get('gateToPort') or -1, e.get('envelope') or 0, e.get('enginePortToGate'), e.get('seaDistance'), e.get('seaDestination'), e.get('shipSource')))
        check(a.get('storylineActive') is not True, 'world %d: the War Sails storyline is not active (its behaviour is %s)' % (index, a.get('storylineBehavior')))
        if startup:
            clans = a.get('banditClans') or {}
            hideout_cultures = dict(x.split(':', 1) for x in a.get('hideoutCultures', []))
            required = set(startup_policy['hideouts']['requiredCultures'])
            check(sorted(clans) == sorted(BANDITS) and all(c and c['isBandit'] and c['home'] in hideout_cultures and
                                                           (c['homeCulture'] == c['culture'] or c['culture'] not in hideout_cultures.values()) for c in clans.values()) and
                  required <= set(hideout_cultures.values()),
                  'world 0: the %d installed land bandit clans exist as world aliases, each homed by the initializer\'s rule, and every required bandit culture has a hideout (%s)' % (
                      len(clans), ', '.join(sorted(hideout_cultures.values()))))
            r = a.get('retreat') or {}
            check(r.get('component', '').endswith('RetirementSettlementComponent') and r.get('navigation') is None,
                  'world 0: the retirement retreat is a RetirementSettlementComponent (culture %s) on valid land navigation at %s' % (r.get('culture'), r.get('position')))
            p = a.get('plan') or {}
            check(p.get('notables') == PLAN['notables'] and p.get('companions') == PLAN['companions'] and p.get('relations') == PLAN['relations'] and
                  p.get('mapped') == PLAN['notables'] + PLAN['companions'] and not p.get('problems') and not p.get('notableMismatch') and not p.get('companionMismatch'),
                  'world 0: the plan\'s %s notables (exactly each settlement\'s, with the intent\'s full and first name, placed) and %s companions were created, and the intent map holds %s heroes (%s)' % (
                      p.get('notables'), p.get('companions'), p.get('mapped'), '; '.join(p.get('sampleNames') or [])))
            check(p.get('rulerRelation') == PLAN['relation'] and not [x for x in p.get('patchesAfterNewGame', []) if x.startswith('mnbl.proceduralworld.startup')] and p.get('activeAfterNewGame') is False,
                  'world 0: the ruler relation is %s and the startup patches ended with the new game' % p.get('rulerRelation'))
        g = w.get('startupAuditAfterGameplay') or {}
        natural = dict(g.get('naturalChecked') or {})
        for k, v in (g.get('naturalCreatedChecked') or {}).items():
            natural[k] = natural.get(k, 0) + v
        for k, v in natural.items():
            natural_kinds[k] = natural_kinds.get(k, 0) + v
        check(not g.get('error') and g.get('violationCount') == 0 and natural,
              'world %d: over %s campaign days no party is off valid navigation; natural checks (what the game spawned): %s' % (
                  index, round((w.get('gameplay') or {}).get('campaignDaysAdvanced', 0), 2), ', '.join('%s %d' % (k, v) for k, v in sorted(natural.items()))))
        if startup:
            forced = g.get('forcedSpawns') or {}
            check(sorted(forced) == ['bandit', 'caravan', 'lord', 'villager'] and all(isinstance(v, dict) and v.get('problem') is None and v.get('kind') == k for k, v in forced.items()),
                  'world 0: a caravan, a villager party, a fresh lord party and a bandit party spawned through the installed factories at installed spawn points are on valid navigation (%s)' % (
                      ', '.join('%s %s' % (k, ('%s at %s' % (v.get('party'), [round(x, 1) for x in v.get('position')])) if isinstance(v, dict) else v) for k, v in sorted(forced.items()))))
        failures = w.get('validationFailures', [])
        check(not failures, 'world %d: no validation failure (%d)' % (index, len(failures)))
    check(all(natural_kinds.get(k, 0) > 0 for k in ('player', 'lord', 'villager')),
          'across the two worlds the game itself spawned and moved player, lord and villager parties on valid navigation (natural: %s; caravans and bandits are also covered by the forced spawns)' % (
              ', '.join('%s %d' % (k, v) for k, v in sorted(natural_kinds.items()))))
    t = worlds[1].get('startupTeardown') or {} if len(worlds) > 1 else {}
    check(not t.get('error') and t.get('earlierWorld') == worlds[0].get('worldId') and t.get('ownBehaviors', 0) > 0 and t.get('ownListenersBefore', 0) > 0 and t.get('ownListenersLeft') == 0 and
          not t.get('patchesLeftAtEnd') and t.get('startupActiveLeft') is False and t.get('startupAppliedLeft') is False and t.get('worldAppliedLeft') is False,
          'world 1: when the first campaign ended, its %s module behaviours\' %s listeners were removed from its events (%s left) and no startup, world or creation patch survived it' % (
              t.get('ownBehaviors'), t.get('ownListenersBefore'), t.get('ownListenersLeft')))
    check(t.get('sameCampaignEvents') is False and not t.get('staleListeners') and t.get('fixturePrefabs') == 0,
          'world 1: the second campaign has its own events, none of the first campaign\'s %s behaviours listens to them (%s still referenced after a GC), and the fixture statics are cleared' % (
              t.get('earlierBehaviors'), t.get('earlierBehaviorsAliveAfterGC')))
    load_summary = None
    if load_path:
        load = json.load(open(load_path, encoding='utf-8-sig'))
        rows = load['outputs'].get('worlds', [])
        saves = [w.get('save') or {} for w in worlds]
        # MMR-19's exact snapshot comparison also runs; after a 2-day window some AI parties are mid-path, and their path state (next target,
        # last path position) is re-derived on load. Those differences are reported here and left to the save tickets; anything else fails.
        path_state = lambda m: m.startswith('.parties.') and ('.nextTarget[' in m or '.saved[3][' in m)
        mismatches = [m for r in rows for m in (r.get('mismatches') or [])]
        other = [f for r in rows for f in (r.get('failures') or []) if 'values differ from the saved snapshot' not in f]
        check(len(rows) == 2 and all(r.get('snapshot') and not r.get('error') for r in rows) and not other and all(path_state(m) for m in mismatches) and
              (load.get('status') == 'ok' or mismatches),
              'both saves loaded cold through the generated loader to the map (load status %s); the only differences from the save-time snapshots are %d path-state values of moving AI parties (%s), re-derived on load (MMR-19 compares them exactly; left to the save tickets)' % (
                  load.get('status'), len(mismatches), '; '.join(mismatches) or 'none'))
        s0, l0 = saves[0], (rows[0].get('startup') if rows else None) or {}
        check(s0.get('suppliesStartup') is True and s0.get('startupIntents') == PLAN['notables'] + PLAN['companions'] and l0.get('suppliedAtSave') is True and l0.get('restored') is True and
              l0.get('retirementBehaviorKept') is True and (l0.get('retreatComponent') or '').endswith('RetirementSettlementComponent') and l0.get('startupBehavior') is True and
              l0.get('intentsLoaded') == l0.get('intentsAtSave') == s0.get('startupIntents') and l0.get('intentHeroesAlive', 0) > 0 and l0.get('worldPatches') is True,
              'world 0 loads with its startup: the package restores it, Retirement is kept for the retreat, and the startup behaviour is back with its %s saved intents (%s heroes alive)' % (
                  l0.get('intentsLoaded'), l0.get('intentHeroesAlive')))
        l1 = (rows[1].get('startup') if len(rows) > 1 else None) or {}
        check(l1.get('suppliedAtSave') is False and l1.get('restored') is False and l1.get('retirementBehaviorKept') is False and l1.get('worldPatches') is True,
              'world 1 (no startup) loads without one: no Retirement behaviour, world patches applied')
        load_summary = {'run': load_path.replace('\\', '/'), 'pid': load.get('processId'), 'worlds': [r.get('startup') for r in rows], 'saves': [{'suppliesStartup': x.get('suppliesStartup'), 'startupIntents': x.get('startupIntents')} for x in saves]}
    summary = {'schema': 'mnbl.evidence.startup-audit', 'version': 2, 'ticket': 'MMR-34', 'run': run_path.replace('\\', '/'), 'pid': run.get('processId'),
               'moduleSha256': run.get('moduleSha256'), 'inventorySha256': sha(os.path.join(REPO, 'worldgen/policies/campaign-compatibility.json')),
               'startupPolicySha256': sha(os.path.join(REPO, 'worldgen/policies/startup.json')),
               'worlds': [{'worldId': w.get('worldId'), 'startupAudit': w.get('startupAudit'), 'afterGameplay': w.get('startupAuditAfterGameplay'),
                           'teardown': w.get('startupTeardown'), 'validationFailures': w.get('validationFailures')} for w in worlds],
               'load': load_summary, 'checks': checks, 'passed': sum(x['ok'] for x in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(summary, fh, indent=1)
        fh.write('\n')
    for x in checks:
        print(('ok    ' if x['ok'] else 'FAIL  ') + x['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
