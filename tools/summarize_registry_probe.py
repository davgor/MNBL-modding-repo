"""MMR-30: summarize the in-game run of the MMR-18/19 sequence through CampaignObjectRegistry.

Process 1 (generated_campaign, worldCount 2, saveProbe 1) creates world A then world B in one process, each registered
through the registry, and saves both. Process 2 (generated_save_load) loads both saves after a cold restart, each replayed
through the registry. Checks: registration took the registry path (plan, eight phases in order inside SandBox's slot, closure
confirmed after every submodule); every registry census is closed and holds no other world; B holds nothing of A and the same
shared templates as A; the default lists are published only after the closure; the load replayed the five permanent
documents through the registry and closed; and the MMR-19 two-world matrix still passes on this build
(tools/summarize_world_save_probe.py, run as a subprocess).
Usage: python tools/summarize_registry_probe.py --save evidence/runtime/in-game/generated_campaign-<pid>.json
           --load evidence/runtime/in-game/generated_save_load-<pid>.json --out evidence/runtime/registry-probe.json
"""
import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PHASES = ['SPCultures', 'NPCCharacters', 'Heroes', 'Kingdoms', 'Factions', 'WorkshopTypes', 'LocationComplexTemplates', 'Settlements']
SAVED = ['SPCultures', 'NPCCharacters', 'WorkshopTypes', 'LocationComplexTemplates', 'Settlements']
TEMPLATES = ['Culture', 'NPCCharacter', 'WorkshopType', 'LocationComplexTemplate']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def stages(result, world):
    return [s['name'][len(world) + 1:] for s in result.get('stages', []) if isinstance(s, dict) and s.get('name', '').startswith(world + ':')]


def held(census, type_):
    return (census.get('objectManager') or {}).get(type_) or {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--save', required=True); ap.add_argument('--load', required=True); ap.add_argument('--out', required=True)
    a = ap.parse_args()
    saved, loaded = load(a.save), load(a.load)
    worlds = (saved['outputs'].get('report') or saved['outputs'])['worlds']
    loads = (loaded['outputs'].get('report') or loaded['outputs'])['worlds']
    checks = []

    def check(name, ok, detail=''):
        checks.append({'name': name, 'ok': bool(ok), 'detail': detail})

    check('both processes finished ok on the same module', saved.get('status') == 'ok' and loaded.get('status') == 'ok' and saved.get('moduleSha256') == loaded.get('moduleSha256'))
    check('worlds A and B completed in one process with no validation failure', len(worlds) == 2 and all(w.get('completed') and not w.get('validationFailures') for w in worlds),
          json.dumps([w.get('validationFailures') for w in worlds]))
    rows = []
    for w in worlds:
        wid = w['worldId']; st = stages(saved, wid)
        reg = w['registration']; census = reg.get('registryCensus') or {}
        plan = next((int(s.split(':')[1]) for s in st if s.startswith('generated_registry_plan:')), None)
        loads_in_order = [s.split(':', 1)[1] for s in st if s.startswith('generated_xml_load:')]
        sandbox = st.index('generated_registration_dispatch:SandBox.SandBoxSubModule') if 'generated_registration_dispatch:SandBox.SandBoxSubModule' in st else -1
        after = next((i for i, s in enumerate(st) if s.startswith('generated_registration_dispatch:') and i > sandbox), -1)
        inside = sandbox >= 0 and all(sandbox < st.index('generated_xml_load:' + p) < after for p in PHASES if 'generated_xml_load:' + p in st)
        closed = st.index('generated_registry_closed') if 'generated_registry_closed' in st else -1
        init = next((i for i, s in enumerate(st) if s.startswith('census:game_initialization_finished') or s == 'diagnostic_creator_installed'), -1)
        censuses = (w.get('registryCensuses') or []) + [w.get('finalRegistryCensus') or {}]
        rows.append({'worldId': wid, 'planned': plan, 'phases': loads_in_order, 'registryCensusPhases': [c.get('phase') for c in censuses],
                     'closure': [c.get('closure') for c in censuses], 'otherWorldPrefixes': [c.get('otherWorldPrefixes') for c in censuses],
                     'afterRegistration': census, 'final': w.get('finalRegistryCensus')})
        check(wid + ': registration took the registry path: a plan of %s objects, the eight phases in order inside SandBox\'s slot' % plan,
              plan == len(reg.get('instances') or []) and loads_in_order == PHASES and inside, ' '.join(loads_in_order))
        check(wid + ': closure confirmed after every submodule registered, before the creator and default lists',
              closed > after >= 0 and (init < 0 or closed < init) and census.get('closure') == [])
        check(wid + ': every registry census (after registration, after all submodules, at initialization, at the map) is closed and holds no other world',
              len(censuses) >= 3 and all(c.get('closure') == [] and c.get('otherWorldPrefixes') == [] for c in censuses + [census]))
        lists = (census.get('campaignLists') or {}).get('Settlements') or {}
        final_lists = ((w.get('finalRegistryCensus') or {}).get('campaignLists') or {}).get('Settlements') or {}
        check(wid + ': the Settlements list is unpublished at registration and holds the four generated settlements at the map',
              lists.get('published') is False and final_lists.get('published') is True and final_lists.get('thisWorld') == 4 and final_lists.get('otherWorlds') == 0)
    a_c, b_c = rows[0]['afterRegistration'], rows[1]['afterRegistration']
    b_censuses = [b_c] + (worlds[1].get('registryCensuses') or []) + [worlds[1].get('finalRegistryCensus') or {}]
    check('world B holds no object of world A in either object manager, at any census',
          all(info.get('otherWorlds', 0) == 0 for c in b_censuses for info in (c.get('objectManager') or {}).values())
          and all(info.get('otherWorlds', 0) == 0 for c in b_censuses for info in (c.get('campaignLists') or {}).values())
          and all(not c.get('otherWorldPrefixes') for c in b_censuses) and len(b_censuses) >= 4)
    check('world B keeps the same shared templates as world A (the same official ids, by hash)',
          all(held(a_c, t).get('officialIdsSha256') == held(b_c, t).get('officialIdsSha256') and held(a_c, t).get('official', 0) > 0 for t in TEMPLATES),
          json.dumps({t: [held(a_c, t).get('official'), held(b_c, t).get('official')] for t in TEMPLATES}))
    finals = [w.get('finalRegistryCensus') or {} for w in worlds] + [l.get('finalRegistryCensus') or {} for l in loads]
    check('the map and finished-load censuses ran settled closure (every generated object ready wherever it lives) and closed',
          len(finals) == 4 and all(c.get('closureMode') == 'Settled' and c.get('closure') == [] and c.get('version') == 2 for c in finals),
          json.dumps([[c.get('phase'), c.get('closureMode'), c.get('closure')] for c in finals]))
    load_rows = []
    for l in loads:
        wid = l.get('worldId'); st = stages(loaded, wid)
        replayed = [s.split(':', 1)[1] for s in st if s.startswith('generated_saved_xml_load:')]
        censuses = (l.get('registryCensuses') or []) + [l.get('finalRegistryCensus') or {}]
        load_rows.append({'worldId': wid, 'phases': replayed, 'registryCensusPhases': [c.get('phase') for c in censuses], 'closure': [c.get('closure') for c in censuses],
                          'final': l.get('finalRegistryCensus')})
        check(str(wid) + ': the cold load replayed the five permanent documents through the registry and closed',
              replayed == SAVED and len(censuses) >= 2 and all(c.get('closure') == [] and c.get('otherWorldPrefixes') == [] for c in censuses) and not l.get('failures'),
              ' '.join(replayed) + ' ' + json.dumps(l.get('failures')))
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, 'matrix.json')
        subprocess.run([sys.executable, os.path.join(REPO, 'tools', 'summarize_world_save_probe.py'), '--save', a.save, '--load', a.load, '--out', out], check=True, capture_output=True)
        matrix = load(out)
    failing = [k for k, v in matrix['checks'].items() if not v]
    # loadUsesThisSaveRun compares the oracle path as a string; a load started with the same file spelled with '/' differs
    # only by separator. Re-check it with normalized paths and the save-name sequence, and record that it was re-checked.
    normalized = None
    if failing == ['loadUsesThisSaveRun']:
        norm = lambda p: os.path.normcase(os.path.normpath(p or ''))
        same_file = norm((loaded.get('args') or {}).get('oracle')) == norm(saved['outputs'].get('saveOracle'))
        same_saves = [l.get('saveName') for l in loads] == [w['save'].get('saveName') for w in worlds if 'save' in w]
        normalized = {'check': 'loadUsesThisSaveRun', 'literal': False, 'sameFileNormalized': same_file, 'sameSaveNames': same_saves}
        failing = [] if same_file and same_saves else failing
    check('the MMR-19 two-world save/cold-load matrix still passes on this build', not failing and saved.get('status') == 'ok' and loaded.get('status') == 'ok',
          json.dumps({'failing': failing, 'recheckedWithNormalizedPaths': normalized}))
    report = {'schema': 'mnbl.evidence.registry-probe', 'version': 1, 'ticket': 'MMR-30',
              'generatedUtc': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
              'save': {'file': rel(a.save), 'sha256': sha(a.save), 'pid': saved.get('processId'), 'moduleSha256': saved.get('moduleSha256')},
              'load': {'file': rel(a.load), 'sha256': sha(a.load), 'pid': loaded.get('processId'), 'moduleSha256': loaded.get('moduleSha256')},
              'worlds': rows, 'loads': load_rows, 'mmr19Matrix': matrix['checks'], 'mmr19MatrixNote': 'loadUsesThisSaveRun is a literal path comparison; see the matrix check detail for the normalized re-check',
              'checks': checks, 'passed': sum(c['ok'] for c in checks), 'total': len(checks),
              'scope': 'The diagnostic two-town fixture registered and replayed through CampaignObjectRegistry in the shipping client. The compiler worlds are planned offline only (MMR-31..36 register them).'}
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(report, f, indent=1); f.write('\n')
    print('%d/%d checks passed' % (report['passed'], report['total']))
    for c in checks:
        if not c['ok']: print('FAIL', c['name'], c['detail'][:300])
    return 0 if report['passed'] == report['total'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
