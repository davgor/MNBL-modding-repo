"""MMR-36: summarize the graph gate of a generated_campaign run (worldCount 2, settlementFixtures/startupAudit/navalAudit=<repo>,
graphFault=<classes>) against the offline graph validation evidence, and record the result in that evidence.

Usage: python tools/summarize_graph_gate.py RUN_JSON OFFLINE_JSON OUT_JSON
OFFLINE_JSON is tools/Test-GraphValidation.ps1's evidence (evidence/campaign/graph-validation.json); it gains an "inGame" section.
The checks:
  * both worlds' registrations passed the graph gate before anything registered, and each in-game report is the offline report of the same
    fixture world (the same report sha256, which binds the staged documents' hashes, the live default inventory, the census, the reached
    templates, the optional references, the policy identity and the diagnostics), so the gate validated the same graph with the same
    inputs as the offline tests;
  * every fault class (GraphFaults) injected into a fresh registration of the second world's placement while the first world was live was
    refused by GeneratedCampaignRegistration.Register with CampaignGraphValidator.GraphException carrying the fault's code, before the
    fixture prefabs, the registry plan or any document load (the stages it reached); corroborating: no object of that world in the object
    manager or the campaign lists before or after, every census type's count unchanged, the fixture prefabs unchanged, no registry;
  * the run finished ok with no validation failure in either world.
"""
import hashlib
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    run_path, offline_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
    run = json.load(open(run_path, encoding='utf-8-sig'))
    offline = json.load(open(offline_path, encoding='utf-8-sig'))
    worlds = run['outputs']['worlds']
    checks = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    check(run.get('status') == 'ok' and len(worlds) == 2, 'the run finished ok with two worlds (%s, %d)' % (run.get('status'), len(worlds)))
    fixtures = {w['world']: w for w in offline['fixtureWorlds']}
    for i, w in enumerate(worlds):
        g = (w.get('registration') or {}).get('graphValidation') or {}
        prefix = w['worldId'] + '_'
        o = fixtures.get(prefix) or {}
        census = g.get('census') or {}
        check(g.get('ok') is True and g.get('diagnosticCount') == 0 and o.get('ok') is True and g.get('sha256') == o.get('reportSha256') and
              (w.get('registration') or {}).get('registrationCompleted') is True,
              'world %d (%s) passed the graph gate before registering, with the offline report of the same fixture world (%s): generated %s; aliases %s; re-emitted templates %s; %s optional references noted; policies %s' % (
                  i, prefix, (g.get('sha256') or 'MISSING')[:12] + (' = offline' if g.get('sha256') == o.get('reportSha256') else ' != offline ' + (o.get('reportSha256') or '')[:12]),
                  census.get('generated'), census.get('aliases'), census.get('reemittedTemplates'), g.get('optionalAbsent'),
                  ', '.join(sorted(((g.get('policyIdentity') or {}).get('policies') or {}).keys()))))
    gate = worlds[0].get('graphGate') or {}
    rows = gate.get('faults') or []
    expected = [x['fault'] for x in offline['gateFaults']]
    check(sorted(r['fault'] for r in rows) == sorted(expected), 'the probe injected every in-game fault class (%s)' % ', '.join(r['fault'] for r in rows))
    for r in rows:
        report = r.get('report') or {}
        stages = r.get('stages') or []
        zero = lambda counts: counts is not None and sum(counts.values()) == 0
        check(not r.get('error') and (r.get('refusal') or '').startswith('GraphException') and r.get('expectedCode') in (report.get('codes') or []) and
              zero(r.get('objectsBefore')) and zero(r.get('objectsAfter')) and r.get('totalsUnchanged') is True and r.get('fixturePrefabsUnchanged') is True and r.get('registryCreated') is False and
              r.get('registrationCompleted') is False and any(s.startswith('generated_graph_validation:') for s in stages) and
              not any(s.startswith('generated_xml_load') or s.startswith('generated_registry_plan') for s in stages),
              'fault %s on %s: Register refused it at the graph gate (%s; codes %s) after %s, before any registry plan or document load; no object of the world registered before or after and every census count unchanged (%d lists), fixture prefabs untouched, no registry' % (
                  r['fault'], r.get('world'), (r.get('refusal') or 'NOT refused')[:160], ', '.join(report.get('codes') or []), ' > '.join(stages),
                  len(r.get('objectsAfter') or {})))
    for i, w in enumerate(worlds):
        check(not w.get('validationFailures'), 'world %d: no validation failure (%d%s)' % (i, len(w.get('validationFailures') or []), (': ' + '; '.join(w['validationFailures'][:3])) if w.get('validationFailures') else ''))
    summary = {'schema': 'mnbl.evidence.graph-gate', 'version': 1, 'ticket': 'MMR-36', 'run': run_path.replace('\\', '/'), 'pid': run.get('processId'),
               'moduleSha256': run.get('moduleSha256'), 'policySha256': sha(os.path.join(REPO, 'worldgen/policies/graph-validation.json')),
               'worlds': [{'world': w['worldId'], 'graphValidation': (w.get('registration') or {}).get('graphValidation')} for w in worlds],
               'graphGate': gate, 'checks': checks, 'passed': sum(x['ok'] for x in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(summary, fh, indent=1)
        fh.write('\n')
    offline['inGame'] = {'summary': os.path.relpath(out_path, REPO).replace('\\', '/'), 'run': summary['run'], 'pid': summary['pid'], 'passed': summary['passed'], 'total': summary['total'],
                         'checks': [('ok    ' if x['ok'] else 'FAIL  ') + x['label'] for x in checks]}
    with open(offline_path, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(offline, fh, indent=1, sort_keys=True)
        fh.write('\n')
    for x in checks:
        print(('ok    ' if x['ok'] else 'FAIL  ') + x['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
