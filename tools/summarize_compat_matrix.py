"""MMR-50: summarizes the compat_matrix probe (evidence/runtime/in-game/compat_matrix-<pid>.json) into evidence/persistence/compat-matrix.json.
Recomputes every expectation from the archived outputs, not from the probe's pass lines.
Usage: python tools/summarize_compat_matrix.py <pid>
"""
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIXTURES = {'incompatible-schema': 'incompatible-build', 'incompatible-build': 'incompatible-build', 'missing-dlc': 'incompatible-build', 'corrupted-hash': 'damaged-world'}


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    pid = sys.argv[1]
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'compat_matrix-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        result = json.load(f)
    o = result.get('outputs') or {}
    before, after, current = o.get('reportBeforeSweep') or {}, o.get('reportAfterSweep') or {}, o.get('reportCurrent') or {}
    load = o.get('load') or {}
    started = load.get('processesStarted') or {}
    rows = [
        ('c1 supported: the source save is reported supported (%s)' % o.get('source'), before.get('class') == 'supported' and before.get('refusal') is None),
        ('c1 rebuildable: its world\'s scene and reports swept, the report says rebuildable derived cache (scene re-encoded, reports restored, layout re-derived)',
         after.get('class') == 'rebuildable-derived-cache' and 'scene' in (after.get('rebuilds') or []) and 'scene' in (o.get('swept') or [])),
        ('c1 the report agrees with the load: the rebuildable save loads through the world loader, the surface encoder the only process started; the reports the report said restored were restored, the layout it said re-derived was re-derived; the bundle and package unchanged by the rebuild',
         load.get('state') == 'Loaded' and o.get('vanillaStarted') is False and load.get('sceneReinstalled') is True and set(started.keys()) <= {'surface_encoder'}
         and load.get('generatorStarted') is False and o.get('worldUnchangedByRebuild') is True
         and sorted(r['component'][len('report '):] for r in (after.get('rows') or []) if r.get('component', '').startswith('report ') and 'restored' in (r.get('detail') or '')) == sorted(load.get('reportsRestored') or [])
         and sorted(r['component'][len('report '):] for r in (after.get('rows') or []) if r.get('component', '').startswith('report ') and 're-derived' in (r.get('detail') or '')) == sorted(load.get('reportsRederived') or [])),
        ('c1 a save in this build\'s format (every build key recorded) is reported supported', current.get('class') == 'supported'
         and all(('mnbl.save.' + k) in (o.get('currentHeader') or {}) for k in ('contract', 'module', 'moduleAssemblySha256', 'game', 'naval', 'backend', 'navigationCache', 'saveIds', 'worldIdentity'))),
    ]
    fx = o.get('fixtures') or {}
    for name, code in FIXTURES.items():
        r = fx.get(name) or {}
        rows.append(('c2 fixture %s: reported unsupported (%s) and refused by the load with that code before any game started; the installed load never ran; the fixture\'s bytes unchanged' % (name, code),
                     r.get('reportClass') == 'unsupported-world' and r.get('reportCode') == code and r.get('loaderCode') == code and r.get('gameStarted') is False
                     and r.get('installedLoadRan') is False and r.get('cancelled') is True and r.get('fixtureUnchanged') is True))
    p = o.get('preserved') or {}
    rows.append(('c2 the refused loads kept every byte of the source saves, the world\'s bundle and its package', bool(p) and all(v is True for v in p.values())))
    for b in o.get('baselines') or []:
        rows.append(('baseline %s: no game, every tracked static at its baseline' % b.get('at'), b.get('game') is False and b.get('held') == {} and (b.get('tracked') or 0) >= 15))
    rows.append(('the probe\'s own save deleted', bool(o.get('cleanup')) and all(v is True for v in (o.get('cleanup') or {}).values())))
    checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows]
    out = {'schema': 'mnbl.evidence.compat-matrix', 'version': 1, 'ticket': 'MMR-50', 'pid': pid, 'result': os.path.relpath(path, REPO).replace('\\', '/'),
           'status': result.get('status'), 'failures': o.get('failures'), 'checks': checks, 'passed': sum(c['passed'] for c in checks), 'total': len(checks),
           'reports': {'source': before, 'afterSweep': after, 'current': current, 'fixtures': {k: (fx.get(k) or {}).get('report') for k in FIXTURES}}}
    target = os.path.join(REPO, 'evidence', 'persistence', 'compat-matrix.json')
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    print('compat_matrix %s: %d/%d recomputed checks; status %s' % (pid, out['passed'], out['total'], out['status']))
    for c in checks:
        if not c['passed']:
            print('   not passed: ' + c['check'])
    return 0 if out['passed'] == out['total'] and out['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
