"""MMR-52: summarizes the land_missions probe (evidence/runtime/in-game/land_missions-<pid>.json) into evidence/gameplay/land-missions.json.
Every case of tests/Runtime/LandMissionScenarios.json is checked from the archived outputs, not from the probe's pass lines; a case absent from
world A is listed as absent, never passed (a battle class present in A without a usable cell is not absent: it fails).
Usage: python tools/summarize_land_missions.py <pid> [--fixture]   (--fixture also writes tests/Runtime/fixtures/land-missions-<pid>.json)
"""
import copy
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCENARIOS = os.path.join(REPO, 'tests', 'Runtime', 'LandMissionScenarios.json')


def clean(case):
    c = case or {}
    return [('the case ran to its end without error (%s)' % ((c.get('error') or '').split('\n')[0][:160] if c else 'not run'), bool(c) and 'seconds' in c and 'error' not in c and not c.get('failures'))]


def returned(case, rules, bound=None):
    """The party back where the case's installed path puts it (the entry position for a battle; the gate, the siege camp or the captor's
    position as the case records it), within the vanilla land bound, on land as before, on valid navigation."""
    c = case or {}
    d = c.get('returnDisplacement')
    where = (c.get('returnExpected') or {}).get('reason') or 'the entry position'
    b, a = c.get('before') or {}, c.get('after') or {}
    bound = rules['returnBoundLand'] if bound is None else bound
    return [('the party returned on valid navigation (%s)' % c.get('afterNavigation'), 'after' in c and c.get('afterNavigation') is None),
            ('back at %s within the vanilla return bound: %s of %s' % (where, 'none' if d is None else '%.3f' % d, round(bound, 3)), d is not None and d <= bound),
            ('on land, as before', bool(b) and bool(a) and b.get('isOnLand') is True and a.get('isOnLand') is True and a.get('atSea') is False)]


def battle(case, rules):
    c = case or {}
    m = c.get('mission') or {}
    g = c.get('gear') or {}
    expected = ((c.get('before') or {}).get('expectedBattleScene') or {})
    guard = c.get('mismatchGuard') or {}
    involved = (c.get('mapEvent') or {}).get('involved') or []
    return clean(c) + [
        ('the patch\'s scene opened: %s (expected %s, deterministic %s)' % (m.get('scene'), expected.get('scene'), expected.get('deterministic')),
         c.get('sceneMatchesPatch') is True and expected.get('deterministic') is True),
        ('the MMR-42 guard observed this battle\'s choice and agreed (%s new observations)' % c.get('guardObservations'), (c.get('guardObservations') or 0) > 0 and guard.get('agrees') is True),
        ('only the player\'s and the enemy\'s parties fought (%s)' % ', '.join(involved), len(involved) == 2 and 'player_party' in involved and (c.get('mapEvent') or {}).get('naval') is False),
        ('deployment finished and the mission ran (%.2f s of mission time)' % (m.get('missionTimeAdvanced') or 0), m.get('deployed') is True and (m.get('missionTimeAdvanced') or 0) > 0),
        ('every agent\'s gear complete: %s of %s incomplete' % (g.get('incomplete'), g.get('agents')), (g.get('agents') or 0) > 0 and g.get('incomplete') == 0),
    ] + returned(c, rules)


def scene(case, place, expected_key='expectedScene'):
    c = case or {}
    m = c.get(place) or {}
    exp = c.get(expected_key)
    if isinstance(exp, dict):
        exp = exp.get(place)
    return ('the %s opened its mapped scene %s (%s)' % (place, exp, m.get('scene')), bool(m) and m.get('scene') == exp and exp is not None)


def recompute(result, plan):
    o = result.get('outputs') or {}
    rules = plan['rules']
    cases = o.get('cases') or {}
    terrain = o.get('terrain') or {}
    present = terrain.get('classesPresent') or {}
    rows, absent = {}, []

    terrain_b = o.get('terrainB') or {}
    present_b = terrain_b.get('classesPresent') or {}
    world_b = o.get('worldB') or {}

    def lacking(key, cells_b, b_key):
        """A case world A lacks (sampled at every cell): world B's case, or absent when B, sampled at every cell, lacks it too."""
        if terrain.get('stride') != 1:
            return None, [('%s looked for at every cell of A (stride %s)' % (key, terrain.get('stride')), False)]
        if not terrain_b:
            return None, [('%s absent from A: world B sampled for it (%s)' % (key, world_b.get('error') or 'B not run'), False)]
        if terrain_b.get('stride') != 1:
            return None, [('%s looked for at every cell of B (stride %s)' % (key, terrain_b.get('stride')), False)]
        case = cases.get(b_key)
        if not cells_b:
            absent.append(key)
            return None, [('%s absent from A and B at full resolution (recorded)' % key, True)]
        if case is None or case.get('absent'):
            return None, [('a usable cell for %s in B (%s cells)' % (key, cells_b), False)]
        return case, None

    field, fought = [], {}
    for cls in rules['battleClasses']:
        if present.get(cls):
            case = cases.get('field.' + cls)
            if case is None or case.get('absent'):
                field.append(('a usable cell of the present class %s (%d cells)' % (cls, present.get(cls)), False))
                continue
            fought[cls] = case
            field += [(cls + ': ' + n, ok) for n, ok in battle(case, rules)]
            continue
        case, fail = lacking('field.' + cls, present_b.get(cls), 'B.field.' + cls)
        if fail:
            field += fail
            continue
        fought[cls] = case
        field += [('B ' + cls + ': ' + n, ok) for n, ok in battle(case, rules)]
    subs = [c for c in rules['substitutedClasses'] if c in fought]
    passed_subs = [c for c in subs if all(ok for n, ok in battle(fought[c], rules))]
    field.append(('a substituted class fought (%s fought, %s passed)' % (subs, passed_subs), bool(passed_subs)))
    rows['field'] = field or [('no battle class present', False)]

    crossing = cases.get('crossing') or {}
    if terrain.get('crossingCells'):
        if crossing.get('absent') or not crossing:
            rows['crossing'] = [('a usable crossing cell in A (%s Bridge or Fording cells, none usable)' % terrain.get('crossingCells'), False)]
        else:
            rows['crossing'] = [('on a %s face: %s' % ((crossing.get('candidate') or {}).get('face'), n), ok) for n, ok in battle(crossing, rules)]
    else:
        case, fail = lacking('crossing', terrain_b.get('crossingCells'), 'B.crossing')
        rows['crossing'] = fail or [('B, on a %s face: %s' % ((case.get('candidate') or {}).get('face'), n), ok) for n, ok in battle(case, rules)]
    retreat = cases.get('retreat') or {}
    outcome = retreat.get('outcome') or {}
    rows['retreat'] = battle(retreat, rules) + [('left by Mission.RetreatMission', retreat.get('retreat') is True and bool(retreat.get('mission'))),
        ('the battle left to the enemy (player won %s, enemy active %s with %s healthy, %s)' % (outcome.get('playerWon'), outcome.get('enemyActive'), outcome.get('enemyHealthy'), outcome.get('battleState')),
         outcome.get('playerWon') is False and outcome.get('enemyActive') is True and (outcome.get('enemyHealthy') or 0) > 0)]

    town = cases.get('town') or {}
    conv = town.get('centerConversation') or {}
    rows['town'] = clean(town) + [scene(town, 'center', 'expectedScenes'), scene(town, 'tavern', 'expectedScenes'),
        ('a %s spoken to in the center (%s, %s): first line "%s"' % ('notable' if conv.get('notable') else 'merchant', conv.get('character'), conv.get('occupation'), (conv.get('firstLine') or '')[:60]),
         conv.get('started') is True and conv.get('withAgent') is True and bool(conv.get('firstLine')) and conv.get('ended') is True)] + returned(town, rules)
    castle = cases.get('castle') or {}
    rows['castle'] = clean(castle) + [scene(castle, 'center'), ('the castle given for the visit and given back', (castle.get('setup') or {}).get('givenToPlayer') is True and (castle.get('setup') or {}).get('givenBack') is True)] + returned(castle, rules)
    village = cases.get('village') or {}
    rows['village'] = clean(village) + [scene(village, 'center')] + returned(village, rules)
    hideout = cases.get('hideout') or {}
    fight = hideout.get('fight') or {}
    hs = hideout.get('setup') or {}
    sel = hs.get('troopSelection') or {}
    rows['hideout'] = clean(hideout) + [('entered through its assault at hour %s (attack hours %s), the troop selection answered (%s of %s offered, %s to %s)' % (hs.get('hourOfDay'), hs.get('attackHours'), sel.get('selected'), sel.get('offered'), sel.get('min'), sel.get('max')),
                                         hs.get('option') == 'assault' and bool(sel) and (sel.get('min') or 0) <= (sel.get('selected') or 0) <= (sel.get('max') or 0)),
        ('the hideout mission won (%s bandits fell, %s boss conversations)' % (fight.get('killed'), fight.get('bossConversations')), fight.get('victory') is True),
        ('the hideout no longer infested', hideout.get('infestedAfter') is False), scene(hideout, 'mission')] + returned(hideout, rules)
    siege = cases.get('siege') or {}
    sm = siege.get('mission') or {}
    ss = siege.get('setup') or {}
    adv = ss.get('preparationAdvanced') or {}
    rows['siege'] = clean(siege) + [
        ('the camp built by itself: %s after %s campaign hours at %s per hour (%s hours needed naturally)' % (adv.get('observedProgress'), adv.get('atHours'), ss.get('preparationPerHour'), adv.get('naturalHoursNeeded')),
         (adv.get('observedProgress') or 0) > 0 and (ss.get('preparationPerHour') or 0) > 0),
        ('then advanced to %s (diagnostic setup, recorded) and completed by the engine\'s tick in %.1f hours in all, without interruption (%s)' % (adv.get('to'), ss.get('preparationHours') or 0, ss.get('interruption')),
         bool(adv) and ss.get('prepared') is True and not ss.get('interruption')),
        ('the assault opened the mapped scene %s (%s) with deployment finished' % (siege.get('expectedScene'), sm.get('scene')), sm.get('scene') == siege.get('expectedScene') and sm.get('deployed') is True)] + returned(siege, rules)
    capture = cases.get('capture') or {}
    cf = capture.get('fight') or {}
    rows['capture'] = clean(capture) + [('the player defeated in the mission (%s fell)' % cf.get('fallen'), cf.get('defeated') is True),
        ('taken prisoner by %s' % capture.get('captor'), capture.get('captured') is True), ('escaped', capture.get('escaped') is True)] + returned(capture, rules, 2 * 1.25 * capture['encounterRadius'] + 0.01 if isinstance(capture.get('encounterRadius'), (int, float)) else None)

    cultures = o.get('culturesPresent') or []
    seen = o.get('cultures') or {}
    rows['gear'] = [('%s: %d troops and %d heroes seen in mission, %d incomplete' % (c, (seen.get(c) or {}).get('troops', 0), (seen.get(c) or {}).get('heroes', 0), (seen.get(c) or {}).get('incomplete', 0)),
                     (seen.get(c) or {}).get('troops', 0) > 0 and (seen.get(c) or {}).get('heroes', 0) > 0 and (seen.get(c) or {}).get('incomplete', 0) == 0) for c in cultures] or [('cultures present recorded', False)]
    loc = o.get('locationAudit') or {}
    entered = [k for k in ('town', 'castle', 'village') if cases.get(k)]
    rows['locations'] = [('%s settlements\' location scenes meet the contracts (%s problems) and exist (%s missing of %s references)' % (loc.get('settlements'), loc.get('problemCount'), len(loc.get('engineMissing') or []), loc.get('sceneReferences')),
                          (loc.get('settlements') or 0) > 0 and loc.get('problemCount') == 0 and not loc.get('engineMissing'))] + [scene(cases.get(k), 'center', 'expectedScenes' if k == 'town' else 'expectedScene') for k in entered]
    names = o.get('names') or {}
    menus = o.get('menus') or {}
    empty = [k for k, v in menus.items() if not any(x.get('holds') for x in v)]
    rows['names'] = [('%s names and encyclopedia links resolved (%s problems)' % (names.get('checked'), names.get('problemCount')), (names.get('checked') or 0) > 0 and names.get('problemCount') == 0),
                     ('every location menu opened had its options (%d menus; without: %s)' % (len(menus), empty), bool(menus) and not empty)]
    asset_rows = []
    for name, c in sorted(cases.items()):
        if c.get('absent'):
            continue
        log = c.get('log') or {}
        asset_rows.append(('%s: no missing asset in the log (%s of the world, %s of an unknown kind: %s), no engine assert (%s, canary %s)' % (name, log.get('worldMissingCount'), log.get('otherMissingCount'), (log.get('otherMissing') or [])[:2], c.get('assertCount'), c.get('assertCanarySeen')),
                           log.get('read') is True and log.get('worldMissingCount') == 0 and log.get('otherMissingCount') == 0 and c.get('assertCount') == 0 and c.get('assertCanarySeen') is True))
        for place in ('mission', 'center', 'tavern'):
            m = c.get(place)
            if not isinstance(m, dict) or 'scene' not in m:
                continue
            sp = m.get('spawns') or {}
            asset_rows.append(('%s %s: every agent spawned on the navigation mesh inside the boundaries (%s agents; %s off it on a scene object %s; %s off it with nothing under it, %s outside; %s)' % (name, place, sp.get('agents'), sp.get('offNavmeshUsingObject'), sp.get('usedObjects'), sp.get('offNavmesh'), sp.get('outsideBoundaries'), sp.get('samples')),
                               (sp.get('agents') or 0) > 0 and sp.get('offNavmesh') == 0 and sp.get('outsideBoundaries') == 0 and sp.get('mainOnNavmesh') is True))
    rows['assets'] = asset_rows or [('cases recorded', False)]
    bl = o.get('baselines') or []
    rows['baselines'] = [('the run\'s rules are the scenario file\'s', o.get('rules') == plan['rules'])] + [('%s: no game, every tracked static at its baseline' % x.get('at'), x.get('game') is False and x.get('held') == {} and (x.get('tracked') or 0) >= 15) for x in bl] +         [('the %s baseline recorded' % at, any(x.get('at') == at for x in bl)) for at in ('start', 'end')]

    scenarios = []
    for s in plan['cases']:
        checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows.get(s['id'], [('no outputs for the case', False)])]
        scenarios.append({'id': s['id'], 'source': s['source'], 'what': s['what'], 'passed': all(c['passed'] for c in checks), 'checks': checks})
    total = sum(len(s['checks']) for s in scenarios)
    passed = sum(c['passed'] for s in scenarios for c in s['checks'])
    return scenarios, passed, total, absent


def trim(result):
    """The run's result without what no check reads (stages, timings, the snapshots' detail), for a
    committed fixture; the repository root becomes <repo> and any other user home <home> in every string."""
    r = copy.deepcopy({k: v for k, v in result.items() if k not in ('stages', 'timings', 'requestFile', 'store', 'menuScreen')})
    o = r.get('outputs') or {}
    for case in (o.get('cases') or {}).values():
        for key in ('reloadBefore',):
            case.pop(key, None)
        for snapshot in ('before', 'after'):
            snap = case.get(snapshot)
            if isinstance(snap, dict):
                case[snapshot] = {k: v for k, v in snap.items() if k in ('position', 'isOnLand', 'atSea', 'expectedBattleScene', 'navigation', 'face')}
        mission = case.get('mission')
        if isinstance(mission, dict):
            mission.pop('initializerRecord', None)
            mission.pop('terrainSamples', None)
    for key in ('prepare', 'load'):
        v = o.get(key)
        if isinstance(v, dict):
            v.pop('loader', None)
    roots = sorted({REPO, REPO.replace('\\', '/'), REPO.replace('/', '\\')}, key=len, reverse=True)
    home = re.compile(r'[A-Za-z]:[\\/]Users[\\/][^\\/"]+', re.I)

    def scrub(x):
        if isinstance(x, str):
            for root in roots:
                x = x.replace(root, '<repo>')
            return home.sub('<home>', x)
        if isinstance(x, list):
            return [scrub(v) for v in x]
        if isinstance(x, dict):
            return {k: scrub(v) for k, v in x.items()}
        return x
    return scrub(r)


def main():
    args = [x for x in sys.argv[1:] if not x.startswith('--')]
    if len(args) != 1:
        print(__doc__)
        return 2
    pid = args[0]
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'land_missions-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        result = json.load(f)
    with open(SCENARIOS, 'rb') as f:
        raw = f.read()
    plan = json.loads(raw.decode('utf-8'))
    scenarios, passed, total, absent = recompute(result, plan)
    o = result.get('outputs') or {}
    out = {'schema': 'mnbl.evidence.land-missions', 'version': 1, 'ticket': 'MMR-52', 'pid': pid, 'result': os.path.relpath(path, REPO).replace('\\', '/'),
           'status': result.get('status'), 'error': result.get('error'), 'scenarioFile': {'run': o.get('scenarioFile'), 'current': hashlib.sha256(raw).hexdigest()},
           'failures': o.get('failures'), 'passed': passed, 'total': total, 'absent': absent, 'scenarios': scenarios,
           'load': {k: (o.get('load') or {}).get(k) for k in ('save', 'loaded', 'seconds', 'worldId')}, 'terrain': o.get('terrain'), 'culturesPresent': o.get('culturesPresent'),
           'cultures': o.get('cultures'), 'locationAudit': o.get('locationAudit'), 'names': o.get('names'), 'menus': o.get('menus'),
           'cases': {k: {kk: vv for kk, vv in v.items() if kk not in ('before', 'after')} for k, v in (o.get('cases') or {}).items()},
           'captures': sorted(k for k in o if k.endswith('Screenshot'))}
    target = os.path.join(REPO, 'evidence', 'gameplay', 'land-missions.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    if '--fixture' in sys.argv:
        fixture = os.path.join(REPO, 'tests', 'Runtime', 'fixtures', 'land-missions-%s.json' % pid)
        os.makedirs(os.path.dirname(fixture), exist_ok=True)
        with open(fixture, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(trim(result), f, indent=1, ensure_ascii=False)
            f.write('\n')
        print('fixture: ' + os.path.relpath(fixture, REPO).replace('\\', '/'))
    print('land_missions %s: %d/%d recomputed checks; %d/%d cases; absent %s; status %s' % (pid, passed, total, sum(s['passed'] for s in scenarios), len(scenarios), absent, out['status']))
    for s in scenarios:
        for c in s['checks']:
            if not c['passed']:
                print('   not passed: %s: %s' % (s['id'], c['check']))
    return 0 if passed == total and out['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
