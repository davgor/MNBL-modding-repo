"""Summarize MMR-45 into evidence/generation/activation-matrix.json.

Inputs:
  --offline   the component harness's evidence (tools/Test-Activation.ps1: evidence/generation/activation-tests.json): a failure injected at
              each commit boundary, a process stop after each boundary and inside the initial save's publication, each followed by recovery
  --compiled  the compiled world dataset's evidence (tools/Test-CompiledWorld.ps1: evidence/generation/compiled-world-tests.json)
  --commit    a world_activation run that committed (secondFault=<boundary>: the same world activated again with a failure, beside it)
  --stop      a world_activation run with stopInside=<boundary> (the process terminated inside the boundary) ...
  --recovered ... and the run whose startup recovered it (its startupActivationRecovery); pairs in order
Every value is copied or counted from the results. Usage:
  python tools/summarize_activation.py --offline O --compiled C --commit R [--stop S --recovered T ...] --out OUT [--history H ...]
"""
import argparse
import datetime
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BOUNDARIES = ['Admit', 'VerifyData', 'VerifyRuntime', 'BuildWorld', 'PromotePackage', 'StartCampaign', 'PostBuild', 'BindIdentity', 'PublishSave', 'WriteReceipt']


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    # A repository path, or only the file name: a path outside the repository names the user's profile.
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else os.path.basename(path)


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def source(path):
    run = load(path)
    return dict(file=rel(path), sha256=sha(path), processId=run.get('processId'), probe=run.get('probe'), status=run.get('status'),
                moduleSha256=run.get('moduleSha256'), args=run.get('args'), error=run.get('error'))


def activation_row(a):
    if a is None:
        return None
    journal = a.get('journal') or {}
    host = a.get('host') or {}
    seconds = {b: (host.get(b) or {}).get('seconds') for b in BOUNDARIES if isinstance(host.get(b), dict)}
    return dict(jobPhase=a.get('jobPhase'), instance=a.get('instance'), journalState=a.get('journalState'), failedAt=journal.get('failedAt'),
                phases=journal.get('phases'), rollback=journal.get('rollback'), evidence=journal.get('evidence'), error=a.get('error'),
                generateSeconds=a.get('generateSeconds'), commitSeconds=a.get('commitSeconds'), boundarySeconds=seconds,
                atMainMenu=a.get('atMainMenu'), savesBound=a.get('savesBound'), worldObjectsLeft=a.get('worldObjectsLeft'), cleanup=a.get('cleanup'))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--offline', required=True)
    ap.add_argument('--compiled', required=True)
    ap.add_argument('--commit', required=True)
    ap.add_argument('--stop', action='append', default=[])
    ap.add_argument('--recovered', action='append', default=[])
    ap.add_argument('--out', required=True)
    ap.add_argument('--history', action='append', default=[])
    a = ap.parse_args()
    if len(a.stop) != len(a.recovered):
        raise SystemExit('every --stop needs the --recovered run after it')
    offline, compiled, commit = load(a.offline), load(a.compiled), load(a.commit)
    checks = []

    def check(name, ok, detail):
        checks.append(dict(name=name, passed=bool(ok), detail=detail))

    # Offline: the component harness's matrix.
    matrix = offline.get('matrix', [])
    check('offlineHarnessPassed', offline.get('failures') == 0 and offline.get('checks', 0) > 0, '%s checks, %s failures' % (offline.get('checks'), offline.get('failures')))
    injected = {r['boundary'] for r in matrix if r.get('mode') == 'injected failure'}
    stopped = {r['boundary'] for r in matrix if r.get('mode') == 'process stop after the boundary'}
    check('offlineFailureAtEveryBoundary', injected >= set(BOUNDARIES), 'injected failures at ' + ', '.join(b for b in BOUNDARIES if b in injected))
    check('offlineStopAfterEveryBoundary', stopped >= set(BOUNDARIES), 'process stops after ' + ', '.join(b for b in BOUNDARIES if b in stopped))
    check('offlineEarlierWorldsIntact', all(r.get('earlierWorldIntact') for r in matrix), '%d cases, every earlier committed world unchanged' % len(matrix))
    check('offlineNoAmbiguousBinding', all((r.get('outcome') == 'committed') == (r.get('saveBound') == 1 and r.get('receipt')) and (r.get('outcome') == 'committed' or r.get('saveBound') == 0) for r in matrix),
          'a save is bound to an instance exactly when its activation committed with a receipt')
    check('compiledWorldPassed', compiled.get('failures') == 0 and compiled.get('checks', 0) > 0, '%s checks, %s failures' % (compiled.get('checks'), compiled.get('failures')))

    # In game: the committed activation.
    out = commit['outputs']
    first = out.get('activation') or {}
    receipt = first.get('receipt') or {}
    check('commitRunPassed', commit.get('status') == 'ok' and not out.get('failures'), 'run %s: %s; failures %s' % (commit.get('processId'), commit.get('status'), out.get('failures')))
    check('activationCommitted', first.get('journalState') == 'committed' and first.get('jobPhase') == 'Playing',
          'journal %s, job %s, phases %s' % (first.get('journalState'), first.get('jobPhase'), ' > '.join((first.get('journal') or {}).get('phases') or [])))
    check('receiptBindsInstance', receipt.get('schema') == 'mnbl.activation-receipt' and receipt.get('worldContentId') == first.get('worldContentId') and
          (receipt.get('campaignInstanceId') or '').replace('-', '') == first.get('instance') and sorted((receipt.get('validations') or {}).keys()) == ['C07', 'W02', 'W03', 'W04', 'W05'],
          'receipt %s binds %s to %s' % ((receipt.get('sha256') or '')[:12], (receipt.get('worldContentId') or '')[:12], receipt.get('campaignInstanceId')))
    header = first.get('saveHeader') or {}
    save = first.get('saveJournal') or {}
    check('initialSavePublished', save.get('state') == 'published' and header.get('bodyWhole') is True and header.get('instance') == first.get('instance'),
          'save %s: %s, whole %s, bound to %s' % (save.get('saveName'), save.get('state'), header.get('bodyWhole'), header.get('instance')))
    gameplay = first.get('gameplay') or {}
    days = (commit.get('args') or {}).get('gameplayDays', 1)
    check('campaignPlays', gameplay and not gameplay.get('failures') and (gameplay.get('campaignDaysAdvanced') or 0) >= days - 0.01 and not gameplay.get('stalls'),
          'campaign time advanced %.3f of %s days, %d stalls, failures %s' % (gameplay.get('campaignDaysAdvanced') or 0, days, len(gameplay.get('stalls') or []), gameplay.get('failures')))
    second = out.get('secondActivation')
    if second is not None:
        check('secondActivationRolledBack', second.get('journalState') == 'failed' and second.get('savesBound') == 0 and second.get('atMainMenu') and second.get('worldObjectsLeft') == 0,
              'failed at %s; saves bound %s; objects left %s' % ((second.get('journal') or {}).get('failedAt'), second.get('savesBound'), second.get('worldObjectsLeft')))
        before, after = second.get('firstWorldBefore') or {}, second.get('firstWorldAfter') or {}
        check('committedWorldUnchanged', before and before == after, '%d package, receipt, save and journal hashes compared' % len(before))

    # Stops and their recovery at the next start.
    stops = []
    for s_path, r_path in zip(a.stop, a.recovered):
        s, r = load(s_path), load(r_path)
        sa = s['outputs'].get('activation') or {}
        rows = r['outputs'].get('startupActivationRecovery') or []
        # The probe records the instance only once the commit returns, so a stopped run has none: one activation runs at a time, so the
        # next start recovers exactly one active journal, the stopped one.
        mine = [x for x in rows if x.get('instance') == sa.get('instance')] if sa.get('instance') else (rows if len(rows) == 1 else [])
        stops.append(dict(stop=source(s_path), recovered=source(r_path), stoppedInside=sa.get('stoppedInside'), instance=sa.get('instance'), recovery=mine))
        expected = 'committed' if sa.get('stoppedInside') in ('PublishSave',) else 'abandoned'
        check('stopInside%sRecovered' % sa.get('stoppedInside'), len(mine) == 1 and mine[0].get('outcome') == expected,
              'stopped inside %s (instance %s); the next start recovered it: %s' % (sa.get('stoppedInside'), sa.get('instance'), mine))

    report = dict(schema='mnbl.evidence.activation-matrix', version=1, ticket='MMR-45', generated=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  boundaries=BOUNDARIES,
                  offline=dict(file=rel(a.offline), sha256=sha(a.offline), checks=offline.get('checks'), failures=offline.get('failures'), matrix=matrix),
                  compiled=dict(file=rel(a.compiled), sha256=sha(a.compiled), checks=compiled.get('checks'), failures=compiled.get('failures'), record=compiled.get('record')),
                  commit=dict(run=source(a.commit), startupActivationRecovery=out.get('startupActivationRecovery'), activation=activation_row(first), receipt=receipt,
                              saveJournal=save, saveHeader=header, gameplay=dict((k, gameplay.get(k)) for k in ('campaignDaysAdvanced', 'realSecondsForTime', 'stalls', 'failures', 'timeControl', 'playerIgnoredUntilDays') if k in gameplay),
                              second=activation_row(second), firstWorldSnapshot=(second or {}).get('firstWorldAfter')),
                  stops=stops, history=[source(h) for h in a.history], checks=checks, passed=all(c['passed'] for c in checks))
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(report, f, indent=1)
        f.write('\n')
    print('%s: %d/%d checks' % (a.out, sum(c['passed'] for c in checks), len(checks)))
    for c in checks:
        print(('ok    ' if c['passed'] else 'FAIL  ') + c['name'] + ': ' + str(c['detail'])[:300])


if __name__ == '__main__':
    main()
