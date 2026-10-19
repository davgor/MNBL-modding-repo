#!/usr/bin/env python3
"""MMR-1: grades the one-shot acceptance sequence (tools/Test-OneShot.ps1, build/mmr1/one-shot-raw.json) step by step.

Each of the ticket's steps 1-6 is split into aspects, each pass, fail, blocked or not-run, decided from what the runs recorded:
  - a probe's own verdict (status ok, no failures, completed) is the verdict of what it exercises;
  - what no run exercises is not-run, and what needs a second activatable world is blocked (only seed 42 activates; MMR-19's fixture world
    is not in the package);
  - the sequence's own checks: the package installed alone and matching its manifest, no generator process left after any run, the
    official files and every earlier save unchanged, the development module and store put back.
A step is fail if any aspect fails, otherwise blocked if any is blocked, otherwise not-run if any has not run, otherwise pass.
Writes evidence/one-shot/one-shot-run.json. User paths are replaced.
Usage: python tools/summarize_one_shot.py
"""
import hashlib
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(REPO, 'build', 'mmr1', 'one-shot-raw.json')
OUT = os.path.join(REPO, 'evidence', 'one-shot', 'one-shot-run.json')
BLOCKED_B = 'needs a second activatable world: only seed 42 activates (seed 73 is refused at the village binding), and MMR-19\'s fixture world is not in the package'


def load(rel):
    path = os.path.join(REPO, rel.replace('/', os.sep)) if rel else None
    if not path or not os.path.exists(path):
        return None
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def scrub(value):
    if isinstance(value, str):
        return re.sub(r'(?i)[A-Z]:\\\\?Users\\\\?[^\\\\/"]+', '<user>', re.sub(r'(?i)[A-Z]:/Users/[^/"]+', '<user>', value))
    if isinstance(value, list):
        return [scrub(v) for v in value]
    if isinstance(value, dict):
        return {k: scrub(v) for k, v in value.items()}
    return value


def get(d, *path):
    for p in path:
        if not isinstance(d, dict) or p not in d:
            return None
        d = d[p]
    return d


def status_of(aspects):
    states = [a['status'] for a in aspects]
    for s in ('fail', 'blocked', 'not-run'):
        if s in states:
            return s
    return 'pass'


def main():
    if not os.path.exists(RAW):
        print('no ' + RAW)
        return 1
    with open(RAW, encoding='utf-8-sig') as f:
        raw = json.load(f)
    steps = raw.get('steps') or {}
    runs, probes = {}, {}
    for label, step in steps.items():
        if not label.startswith('probe ') or not isinstance(step, dict):
            continue
        name = label[len('probe '):]
        doc = load(step.get('result')) if step.get('result') else None
        out = (doc or {}).get('outputs') or {}
        fails = out.get('failures') if isinstance(out.get('failures'), list) else []
        ok = bool(doc) and doc.get('status') == 'ok' and not fails and out.get('completed') is True and not step.get('stopped')
        runs[name] = {'probe': step.get('probe'), 'pid': step.get('pid'), 'status': step.get('status'), 'ok': ok, 'failures': fails[:12],
                      'error': step.get('error'), 'stopped': step.get('stopped'), 'seconds': step.get('seconds'),
                      'generatorProcessesAfter': step.get('generatorProcessesAfter') or [], 'result': step.get('result'),
                      'resultSha256': sha(os.path.join(REPO, step['result'])) if step.get('result') and os.path.exists(os.path.join(REPO, step['result'])) else None}
        probes[name] = out

    def run_ok(name):
        return bool(runs.get(name, {}).get('ok'))

    def A(name, status, note, run_names):
        return {'aspect': name, 'status': status, 'note': note, 'runs': [n for n in run_names if n in runs] or run_names}

    def by_run(name, aspect, note):
        return A(aspect, 'pass' if run_ok(name) else ('not-run' if name not in runs else 'fail'),
                 note if run_ok(name) else '%s: %s' % (name, runs.get(name, {}).get('failures') or runs.get(name, {}).get('error') or runs.get(name, {}).get('stopped') or 'no result'), [name])

    profile, flow = probes.get('step1 profile', {}), probes.get('step1 identities', {})
    land, naval, visit = probes.get('step2 land', {}), probes.get('step3 naval', {}), probes.get('step2 visit', {})
    create, cold, compat = probes.get('step4-5 create', {}), probes.get('step5 cold', {}), probes.get('step6 refusals', {})
    manifest = load('evidence/release/package-manifest.json') or {}
    files = {f['path']: f['sha256'] for f in get(manifest, 'manifest', 'files') or []}
    tools = load('tools.lock.json') or {}
    build = profile.get('build') or {}
    pinned_dll, pinned_bridge = files.get('bin/Win64_Shipping_Client/MNBL.ProceduralWorld.dll'), files.get('bin/Win64_Shipping_Client/worldbridge.exe')
    identity_ok = (build.get('moduleAssemblySha256') == pinned_dll and build.get('producerSha256') == get(tools, 'producer', 'sha256')
                   and build.get('worldbridgeSha256') == pinned_bridge)
    a, b = profile.get('A') or {}, profile.get('B') or {}
    install = steps.get('install') or {}
    orphans = {n: r['generatorProcessesAfter'] for n, r in runs.items() if r['generatorProcessesAfter']}
    before, after = steps.get('snapshot before') or {}, steps.get('snapshot after') or {}
    official_same = bool(before.get('official')) and before.get('official') == after.get('official') and before.get('binaries') == after.get('binaries')
    saves_kept = bool(before.get('saves')) and all((after.get('saves') or {}).get(k) == v for k, v in (before.get('saves') or {}).items())
    mb, ma = get(steps, 'snapshot before matrix', 'store') or {}, get(steps, 'snapshot after probes', 'store') or {}
    bundles_kept = bool(mb.get('bundles')) and all((ma.get('bundles') or {}).get(k) == v for k, v in (mb.get('bundles') or {}).items())
    undone = steps.get('undone') or {}
    # The navigation caches each run read and whether it built them (the kept engine logs: build/mmr1/engine-logs).
    caches = {}
    for name, r in runs.items():
        log = os.path.join(REPO, 'build', 'mmr1', 'engine-logs', 'rgl_log_%s.txt' % r['pid'])
        if os.path.exists(log):
            with open(log, encoding='utf-8', errors='replace') as f:
                text = f.read()
            caches[name] = {'keys': sorted(set(re.findall(r'navigation-caches[\\/]([0-9a-f]{8})', text))), 'built': 'cache creation progress % 0' in text}
    built_in = sorted(n for n, c in caches.items() if c['built'])
    cache_keys = sorted(set(k for c in caches.values() for k in c['keys']))
    # The producer's pinned runtime against this installation (the producer process contract re-run with the offline suites).
    contract = load('evidence/one-shot/producer-process-contract-20261019.json') or {}
    preflight = get(contract, 'results', 'preflight') or {}

    plan = [
        ('1', 'Verify pinned producer/game/DLC identities; from the actual New Campaign screen generate A, select a generated culture, finish character creation and enter play; confirm full-globe coverage and source-derived faction/settlement identities.', [
            A('the package installed alone, matching its manifest, no development receipt', 'pass' if install.get('matchesManifest') is True and install.get('developmentReceipt') is False and (steps.get('swapped') or {}).get('cleanStoreBefore') is False else 'fail',
              '%s files; the development module and store were moved aside' % install.get('files'), []),
            A('the running module, fwg.exe and worldbridge.exe are the package\'s and the pins', 'pass' if identity_ok else ('not-run' if not build else 'fail'),
              'module %s, producer %s, worldbridge %s; game %s, War Sails %s' % ((build.get('moduleAssemblySha256') or '')[:12], (build.get('producerSha256') or '')[:12], (build.get('worldbridgeSha256') or '')[:12], build.get('game'), build.get('navalDlc')), ['step1 profile']),
            A('the producer\'s pinned runtime (ucrtbase.dll) matches this installation', 'fail' if preflight.get('passed') is False else ('pass' if preflight.get('passed') is True else 'not-run'),
              'the producer process contract\'s preflight (evidence/one-shot/producer-process-contract-20261019.json) fails %s: a Windows update replaced the system UCRT after the pin (evidence/one-shot/ucrt-drift.json). The in-game generation path does not check it; world A\'s content id is the accepted one' % preflight.get('failedChecks'), []),
            by_run('step1 profile', 'world A generated from the New Campaign screen, its culture chosen, character creation finished, the map entered, saved and loaded',
                   'seed %s: generated in %.1f s, content id %s; the map after %.1f s (navigation caches built); save %s, loaded in %.1f s' % (a.get('seed'), get(a, 'generate', 'seconds') or 0, (get(a, 'geography', 'worldContentId') or '')[:16],
                   get(a, 'start', 'mapAfter') or 0, get(a, 'save', 'name'), get(a, 'load', 'loaderSeconds') or 0)),
            by_run('step1 identities', 'source-derived identities: every preview settlement and kingdom matches the live campaign; creation lists exactly the world\'s cultures', 'new_campaign_flow, world A'),
            A('full-globe coverage', 'pass' if run_ok('step1 profile') and get(a, 'geography', 'cells') else 'not-run',
              'every cell of world A\'s periodic %s x %s grid is classified from the generated globe (%s cells: %s)' % (get(a, 'geography', 'grid', 'cols'), get(a, 'geography', 'grid', 'rows'),
              sum((get(a, 'geography', 'cells') or {}).values()) if isinstance(get(a, 'geography', 'cells'), dict) else get(a, 'geography', 'cells'), json.dumps(get(a, 'geography', 'cells'))), ['step1 profile']),
        ]),
        ('2', 'Travel on land, enter a generated settlement, trade/recruit, enter one land mission and return to the same world.', [
            by_run('step2 visit', 'travel by land to a generated town, trade and recruit there, leave; a day of campaign stays coherent', 'campaign_soak world A (trade and recruitment through the screens\' logic, not the screens)'),
            by_run('step2 land', 'land missions and settlement interactions, returning to the map each time', 'land_missions world A cases %s' % sorted((land.get('cases') or {}).keys())),
            A('land missions needing a battle class world A lacks', 'blocked' if get(land, 'worldB', 'blocked') else 'not-run', BLOCKED_B + ' (world A lacks %s)' % land.get('lackingInA'), ['step2 land']),
            A('the same world after a mission (world identity re-checked)', 'not-run', 'the mission cases check position, land or sea, navigation and camera on return; the world identity is checked at the load only', ['step2 land']),
        ]),
        ('3', 'Reach a generated operational port, embark, sail to another valid destination, enter/return from a naval encounter and disembark; the seaborne raid, blockade and anchor scenario.', [
            by_run('step3 naval', 'ship purchase, port and island voyages, drop-off and coastal landing, blockade and anchor, pirate battle (retreat), seaborne raid, defeat with the raft', 'naval_missions world A cases %s' % sorted((naval.get('cases') or {}).keys())),
        ]),
        ('4', 'East/west crossing for player and AI on land and sea, route execution, nearby-party encounter/visibility, distance caches, camera and picking.', [
            by_run('step4-5 create', 'the player crosses the seam on land, an AI party pursues across it, a fleet is called across it and an anchored fleet stays by it; all survive save and load', 'two_world_matrix create, world A'),
            A('the navigation caches built once and reused for the generated world', 'pass' if built_in == ['step1 profile'] and len(cache_keys) == 1 else ('not-run' if not caches else 'fail'),
              'cache keys read: %s; built in: %s (expected: only A\'s first start). %s' % (cache_keys, built_in,
              'The matrix\'s same-process load built them again although they existed (its load took %.0f s); the cold load after its scene was swept and re-encoded read another key (%.1f s). Cause not identified.' % (get(create, 'worlds', 'A', 'load', 'seconds') or 0, get(cold, 'worlds', 'A', 'load', 'seconds') or 0)
              if built_in != ['step1 profile'] or len(cache_keys) != 1 else ''), [n for n in caches]),
            A('camera and picking across the seam on the generated world; the player and an AI party sailing across it; a naval encounter at the seam', 'not-run', 'no probe drives these on world A (camera and picking were shown on the periodic fixture, MMR-66)', []),
        ]),
        ('5', 'Save A with mutable state; generate B in the same process and note the difference; save B, cold-restart, load A then B; ordinary loads must not launch FWGC++.', [
            by_run('step4-5 create', 'A saved with mutable ownership, party, prisoner, siege, ship and anchor state, loaded in the same process and equal to its save', 'two_world_matrix create'),
            by_run('step5 cold', 'after a cold restart, A loads (scene swept and re-encoded) equal to its save, and no generator process starts during the load', 'two_world_matrix cold'),
            A('B (seed 73) generated in the same process as A, with different geography and routes', 'pass' if run_ok('step1 profile') and get(b, 'generate', 'state') == 'Generated'
              and get(b, 'geography', 'worldContentId') and get(b, 'geography', 'coastlineSha256') != get(a, 'geography', 'coastlineSha256') else ('not-run' if not b else 'fail'),
              'B generated in %.1f s, content id %s; land cells A %s vs B %s, coastlines differ' % (get(b, 'generate', 'seconds') or 0, (get(b, 'geography', 'worldContentId') or '')[:16], get(a, 'geography', 'landCells'), get(b, 'geography', 'landCells')), ['step1 profile']),
            A('save B, load B after the cold restart, B\'s landmarks, ownership, parties, ships and anchors', 'blocked', BLOCKED_B, []),
        ]),
        ('6', 'Cancel another generation and load an isolated invalid copy of world data; usable-menu recovery, unchanged original hashes, no orphan producer or cross-world callbacks.', [
            by_run('step1 identities', 'a second generation cancelled through the setup screen (New Seed, then Back): interrupted, the committed world unchanged', 'new_campaign_flow'),
            by_run('step4-5 create', 'invalid copies of A\'s save (torn, missing world, other build) and a Continue into a missing world refused before anything loads; A\'s package and bundle unchanged', 'two_world_matrix create, refusals %s' % sorted((get(create, 'refusals') or {}).keys())[:8]),
            by_run('step6 refusals', 'more invalid copies (incompatible schema and build, missing DLC, corrupted manifest hash) refused; A\'s save, package and bundle unchanged', 'compat_matrix'),
            A('no generator process left after any run', 'pass' if runs and not orphans else 'fail', 'fwg and worldbridge after every run: %s' % (orphans or 'none'), list(runs)),
            A('the world bundles unchanged through the matrix, refusal and cold-load runs', 'pass' if bundles_kept else 'fail', 'store snapshots before and after the two_world_matrix and compat_matrix runs', []),
            A('an invalid copy of world data loaded from a separate test root', 'not-run', 'the store and save paths are fixed (SubModule.Store); the invalid copies are written beside the originals and removed', []),
            A('no cross-world callbacks', 'blocked', BLOCKED_B, []),
        ]),
        ('sequence', 'The sequence itself: the installation and the development environment.', [
            A('the official module descriptors and engine binaries unchanged', 'pass' if official_same else 'fail', 'snapshots before and after', []),
            A('every save from before the sequence unchanged', 'pass' if saves_kept else 'fail', '%d saves' % len(before.get('saves') or {}), []),
            A('the development module and store put back', 'pass' if undone.get('development module restored') is True and undone.get('development store restored') is True else 'fail', str({k: v for k, v in undone.items() if 'restored' in k}), []),
        ]),
    ]
    # Reruns of a failed run on the same build (build/mmr1/reruns.json: label, result, why). A failure seen in the sequence stays failed; the
    # rerun says whether it reproduced.
    reruns = []
    for r in (load('build/mmr1/reruns.json') or []):
        doc = load(r.get('result'))
        out = (doc or {}).get('outputs') or {}
        fails = out.get('failures') if isinstance(out.get('failures'), list) else []
        ok = bool(doc) and doc.get('status') == 'ok' and not fails and out.get('completed') is True
        reruns.append({'of': r.get('label'), 'result': r.get('result'), 'why': r.get('why'), 'ok': ok, 'failures': fails[:12],
                       'loadedSave': get(out, 'load', 'save'), 'package': get(out, 'load', 'loader', 'binding', 'package'), 'scenarioSha256': get(out, 'scenarioFile', 'sha256'),
                       'sequenceRun': {'loadedSave': get(probes.get(r.get('label'), {}), 'load', 'save'), 'package': get(probes.get(r.get('label'), {}), 'load', 'loader', 'binding', 'package'),
                                       'scenarioSha256': get(probes.get(r.get('label'), {}), 'scenarioFile', 'sha256')},
                       'resultSha256': sha(os.path.join(REPO, r['result'])) if r.get('result') and os.path.exists(os.path.join(REPO, r['result'])) else None})
    for _, _, asp in plan:
        for x in asp:
            if x['status'] != 'fail':
                continue
            for r in reruns:
                if r['of'] in x['runs']:
                    x['note'] += ' | rerun on the same build (%s, from %s rather than %s): %s' % (r['result'].rsplit('/', 1)[-1], r['loadedSave'], r['sequenceRun']['loadedSave'],
                        'passed; the failure did not reproduce from that save, so whether it is intermittent or depends on the campaign state is not established' if r['ok'] else 'failed again: %s' % r['failures'][:3])
    out_steps = [{'step': s, 'text': t, 'status': status_of(asp), 'aspects': asp} for s, t, asp in plan]
    identities = {
        'commit': raw.get('commit'), 'sourceTreeClean': raw.get('workingTreeClean'),
        'package': {'sha256': raw.get('zipSha256'), 'moduleAssemblySha256': pinned_dll, 'worldbridgeSha256': pinned_bridge,
                    'manifestSource': get(manifest, 'manifest', 'source')},
        'producerSha256': get(tools, 'producer', 'sha256'), 'game': build.get('game'), 'navalDlc': build.get('navalDlc'),
        'worlds': {'A': {'seed': a.get('seed'), 'worldContentId': get(a, 'geography', 'worldContentId'), 'save': get(a, 'save', 'name'),
                         'binding': get(a, 'load', 'loader', 'binding'), 'scene': get(a, 'load', 'loader', 'scene'),
                         'seconds': {'generate': get(a, 'generate', 'seconds'), 'mapAfterStart': get(a, 'start', 'mapAfter'), 'load': get(a, 'load', 'loaderSeconds')}},
                   'B': {'seed': b.get('seed'), 'worldContentId': get(b, 'geography', 'worldContentId'), 'activated': False, 'seconds': {'generate': get(b, 'generate', 'seconds')}}},
        'request': {'jobA': get(a, 'job', 'jobId'), 'jobB': get(b, 'job', 'jobId'), 'recipeVersion': build.get('recipeVersion'), 'overrides': build.get('overrides'),
                    'qualityAttempts': build.get('qualityAttempts')},
        'campaigns': {'profile': get(a, 'load', 'loader', 'binding', 'instance'), 'matrix': get(create, 'worlds', 'A', 'instance'),
                      'designated': get(probes.get('step3 prepare', {}), 'prepare', 'instance')},
        'matrixSave': get(create, 'worlds', 'A', 'save'), 'designatedSave': 'mnbl_oneshot_A_designated',
        'navigationCaches': {'keys': cache_keys, 'builtIn': built_in, 'loadSeconds': {'profile same-process': get(a, 'load', 'loaderSeconds'),
                             'matrix same-process': get(create, 'worlds', 'A', 'load', 'seconds'), 'matrix cold': get(cold, 'worlds', 'A', 'load', 'seconds')}},
    }
    doc = {'schema': 'mnbl.one-shot-run', 'version': 1, 'ticket': 'MMR-1', 'raw': 'build/mmr1/one-shot-raw.json',
           'startedUtc': raw.get('startedUtc'), 'finishedUtc': raw.get('finishedUtc'),
           'status': status_of([{'status': s['status']} for s in out_steps]),
           'summary': {s['step']: s['status'] for s in out_steps}, 'steps': out_steps, 'runs': runs, 'reruns': reruns, 'identities': identities,
           'sequenceError': steps.get('error')}
    doc = scrub(doc)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, indent=1, ensure_ascii=False)
        f.write('\n')
    print('one-shot: %s (%s)' % (doc['status'], ', '.join('%s %s' % kv for kv in doc['summary'].items())))
    for s in out_steps:
        for x in s['aspects']:
            if x['status'] != 'pass':
                print('  %s %-8s %s: %s' % (s['step'], x['status'], x['aspect'], x['note'][:160]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
