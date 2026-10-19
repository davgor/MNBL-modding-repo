"""MMR-49: summarizes the two-world load matrix (two_world_matrix create phase, then the cold phase in a new process) into
evidence/persistence/two-world-matrix.json. Recomputes every expectation from the archived outputs
(evidence/runtime/in-game/two_world_matrix-<pid>.json), not from the probe's own pass lines.
Usage: python tools/summarize_two_world_matrix.py <create pid> <cold pid> [<load-check pid> ...]
"""
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(pid):
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'two_world_matrix-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f), os.path.relpath(path, REPO).replace('\\', '/')


def step_ok(steps, name):
    v = steps.get(name)
    return v is not None and not (isinstance(v, dict) and 'error' in v)


def loaded(world, key, how, other_world=None, cold=False):
    created = world.get('fromCreate') or world
    l = world.get('load') or {}
    r = l.get('loader') or {}
    rows = [
        ('%s %s: routed from the normal load entry (%s) to the world loader, restored, the installed load never ran' % (key, how, l.get('entry')),
         l.get('routed') is True and r.get('state') == 'Loaded' and l.get('vanillaStarted') is False),
        ('%s %s: equal to its save (time, parties and fleets, anchors, settlements, prisoners, relations, player); %d AI parties chose a new movement target before the snapshot (control without a load: %d)'
         % (key, how, len(l.get('aiRetargets') or []), len([d for d in (l.get('controlDrift') or []) if '/moveTarget' in d])),
         l.get('differences') == [] and len(l.get('aiRetargets') or []) <= max(2, (l.get('comparedParties') or 0) // 50)),
        ('%s %s: nothing of the other world registered; this world\'s objects, frame and terrain context only' % (key, how),
         sum((l.get('otherWorldObjects') or {'x': 1}).values()) == 0 and sum((l.get('objects') or {}).values()) > 0
         and l.get('frame') == created.get('frame') and l.get('terrainContexts') == l.get('expectedTerrainContexts')
         and l.get('expectedTerrainContexts') == ([] if created.get('contentId') == 'none' else [r.get('scene')])),
        ('%s %s: no generator process started (only the surface encoder may run, to re-install a swept scene)' % (key, how),
         r.get('generatorStarted') is False and set((r.get('processesStarted') or {}).keys()) <= {'surface_encoder'}),
        ('%s %s: every position canonical after the load' % (key, how), l.get('nonCanonical') == []),
    ]
    if str(l.get('entry', '')).startswith('Continue'):
        rows.append(('%s %s: the preload screen behind Continue called the load on every frame; one load started, the repeats were ignored' % (key, how),
                     l.get('routed') is True and (l.get('ignoredRepeats') or 0) > 0))
    a = l.get('after') or {}
    if key == 'A':
        rows.append(('A %s: the embarked AI party is still at sea' % how, a.get('embarkedAtSea') is True))
        rows.append(('A %s: the pursuit across the seam ends in one encounter with the one pursuer; the player does not cross again' % how,
                     a.get('pursuerCount') == 1 and a.get('encounter') is True and (a.get('mapEventsWithPlayer') or 0) <= 1 and a.get('playerRecrossings') == 0))
        rows.append(('A %s: the fleet called across the seam kept its target and transit and arrived where it was sent' % how,
                     a.get('movingAtLoad') is True and a.get('arrived') is True and a.get('positionAfter') == a.get('target')))
    else:
        rows.append(('B %s: campaign time runs and parties move; the town the player took stays so; the lord the player took is held at the load (then %s); party positions and targets canonical' % (how, a.get('prisonerAfterRun')),
                     (a.get('hoursRun') or 0) > 0 and (a.get('partiesMoved') or 0) > 0 and a.get('ownedByPlayer') is True and a.get('prisonerHeldAtLoad') is True
                     and a.get('prisonerAfterRun') in ('held', 'escaped') and a.get('nonCanonicalPartiesAfterRun') == []))
        rows.append(('B %s: loaded from inside the running world A (the loader ended A first)' % how, l.get('fromRunningGame') is True and r.get('endedRunningGame') is True))
    if cold and key == 'A':
        # The encoder re-derives the settlement layout beside the scene (checked against the kept layout); navigation and playability come
        # from the copies the activation kept with the campaign.
        rows.append(('A cold: the swept scene re-encoded from the bundle (surface encoder only); navigation and playability reports restored from the activation evidence, the settlement layout re-derived',
                     r.get('sceneReinstalled') is True and (r.get('processesStarted') or {}).get('surface_encoder', 0) >= 1 and set((r.get('processesStarted') or {}).keys()) <= {"surface_encoder"}
                     and sorted(r.get('reportsRestored') or []) == ['navigation.json', 'playability.json'] and (r.get('reportsRederived') or []) == ['settlement-layout.json']))
    return rows


def create_checks(o):
    w = o.get('worlds') or {}
    a, b = w.get('A') or {}, w.get('B') or {}
    sa, sb = a.get('steps') or {}, b.get('steps') or {}
    battle = sa.get('navalBattleAfter') or {}
    anchors = sa.get('anchors') or {}
    rows = [
        ('A: an AI lord party embarked at sea', sa.get('embarkAtSea') is True),
        ('A: an AI lord fleet came out of a naval battle with pirates', step_ok(sa, 'navalBattle') and (sa.get('navalBattle') or {}).get('naval') is True
         and battle.get('lordActive') is True and battle.get('lordInEvent') is False and bool(battle.get('lordShips'))),
        ('A: town ownership changed, a lord party spawned, a lord taken prisoner, a relation changed',
         all(step_ok(sa, s) for s in ('ownership', 'spawnedParty', 'prisoner', 'relation'))),
        ('A: a town under siege', step_ok(sa, 'siege') and (sa.get('siege') or {}).get('siege') is True),
        ('A: a fleet called across the seam, its anchor moving at the save; another anchored by the seam', step_ok(sa, 'anchors') and anchors.get('moving') is True),
        ('A: the player crossed the seam just before the save; a bandit pursuing from across the seam', a.get('crossed') is True and step_ok(sa, 'pursuit')),
        ('B (%s %s%s): a second distinct world; town ownership changed' % ('fixture world' if b.get('fixture') else 'production world', b.get('worldId'), '' if b.get('fixture') else ', seed %s, content %s' % (b.get('seed'), (b.get('contentId') or '')[:16])),
         (b.get('fixture') is True or (bool(b.get('contentId')) and b.get('contentId') != a.get('contentId'))) and b.get('prefix') != a.get('prefix') and step_ok(sb, 'ownership')),
    ]
    for key, world in (('A', a), ('B', b)):
        j = world.get('saveJournal') or {}
        rows.append(('%s saved: published through the save journal, every saved position canonical' % key, j.get('state') == 'published' and world.get('nonCanonicalAtSave') == []))
        planted = world.get('planted') or []
        if key == 'A' or world.get('periodic'):
            rows.append(('%s saved: the non-canonical values planted in fields the per-tick pass does not cover (%s) were written canonical' % (key, ', '.join(p.get('field', '?') for p in planted) or 'none'),
                         len(planted) >= (3 if key == 'A' else 1) and all(p.get('nonCanonicalPlanted') is True and p.get('canonicalAfterSave') is True for p in planted)))
    rows += loaded(a, 'A', 'same process') + loaded(b, 'B', 'same process')
    ref = o.get('refusals') or {}
    expected = {'torn': 'damaged-save', 'missing-bundle': 'missing-world', 'other-game-version': 'incompatible-build'}
    rows.append(('the refused loads ran from inside a running campaign (world B)', ref.get('fromRunningGame') is True))
    for name, code in expected.items():
        r = ref.get(name) or {}
        rows.append(('refused load %s: %s before anything changed; the running campaign kept running on its map, the caller\'s cancel ran' % (name, code),
                     (r.get('code') == code and r.get('runningGameKept') is True and r.get('gameStarted') is False and r.get('endedRunningGame') is False
                      and r.get('cancelCalled') is True and r.get('vanillaStarted') is False) or (name == 'torn' and str(r.get('state', '')).startswith('not offered'))))
    rows.append(('the refused loads changed nothing in world A', ref.get('worldUnchanged') is True))
    c = o.get('continueRefusal') or {}
    rows.append(('refused Continue: one refusal (missing-world) for the preload screen\'s repeated calls; its OK returned to the main menu, no game started',
                 c.get('code') == 'missing-world' and c.get('routed') == 1 and (c.get('ignoredRepeats') or 0) > 0 and c.get('preloadActiveBeforeOk') is True
                 and c.get('menuAfterOk') is True and c.get('gameStarted') is False))
    return rows


def baselines(o):
    return [('baseline %s: no game, every tracked static at its baseline' % b.get('at'), b.get('game') is False and b.get('held') == {} and (b.get('tracked') or 0) >= 15)
            for b in o.get('baselines') or []]


def run(pid, kind):
    r, path = load(pid)
    o = r.get('outputs') or {}
    if kind == 'create':
        rows = create_checks(o) + baselines(o)
    elif kind == 'cold':
        w = o.get('worlds') or {}
        rows = [('cold: world A\'s scene and reports swept before its load', 'scene' in ((o.get('sweptA') or {}).get('removed') or []))]
        rows += loaded(w.get('A') or {}, 'A', 'cold restart', cold=True) + loaded(w.get('B') or {}, 'B', 'cold restart', cold=True) + baselines(o)
        # A caller that keeps the saves for later steps (tools/Test-OneShot.ps1: cleanup=false, the seam and compatibility steps load them)
        # asks for no deletion; the row then records that none was made.
        if (r.get('args') or {}).get('cleanup') is False:
            rows.append(('cold: the probe\'s saves kept for the caller\'s later steps (cleanup=false), none deleted', o.get('cleanup') is None))
        else:
            rows.append(('cold: the probe\'s saves and the campaigns\' initial saves deleted', bool(o.get('cleanup')) and all(v is True for v in (o.get('cleanup') or {}).values())))
    else:
        rows = [('load check: restored through the world loader', (o.get('loader') or {}).get('state') == 'Loaded' and o.get('vanillaStarted') is False)] + baselines(o)
    checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows]
    return {'pid': pid, 'kind': kind, 'result': path, 'status': r.get('status'), 'failures': o.get('failures'), 'checks': checks,
            'passed': sum(c['passed'] for c in checks), 'total': len(checks)}


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    create, cold = run(sys.argv[1], 'create'), run(sys.argv[2], 'cold')
    checks = [run(p, 'load') for p in sys.argv[3:]]
    out = {'schema': 'mnbl.evidence.two-world-matrix', 'version': 1, 'ticket': 'MMR-49', 'create': create, 'cold': cold, 'loadChecks': checks}
    target = os.path.join(REPO, 'evidence', 'persistence', 'two-world-matrix.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    for run_ in (create, cold):
        print('%s %s: %d/%d recomputed checks; status %s' % (run_['kind'], run_['pid'], run_['passed'], run_['total'], run_['status']))
        for c in run_['checks']:
            if not c['passed']:
                print('   not passed: ' + c['check'])
    return 0 if all(x['passed'] == x['total'] and x['status'] == 'ok' for x in (create, cold)) else 1


if __name__ == '__main__':
    sys.exit(main())
