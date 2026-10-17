"""Summarize MMR-62 (early save binding) into evidence/persistence/initial-save-recovery.json.

Two in-game processes: generated_campaign with initialSaveProbe=1 (publishes the initial save, plants every interruption boundary, is
killed after save B) and save_recovery (a new process: the startup pass at the main menu, then the probe's checks). Every value is copied or
counted from their results. Usage:
  python tools/summarize_initial_save.py --publish <generated_campaign result> --recover <save_recovery result> --out <summary.json>
      [--history <result.json> ...]
"""
import argparse
import datetime
import hashlib
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BOUNDARIES = {
    'published': ('normal publication (process 1)', 'published', True),
    'ironman': ('Ironman publication: the engine saves under IronmanModSaveName (process 1)', 'published', True),
    'afterSave': ('engine save over, process killed before the outcome', 'published', True),
    'beforeSave': ('intent written, engine never asked to save', 'abandoned', False),
    'tornHeader': ('torn save: header cut short', 'removed', False),
    'tornData': ('whole header before torn game data', 'removed', False),
}


def sha(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def rel(path):
    path = os.path.abspath(path)
    return os.path.relpath(path, REPO).replace('\\', '/') if path.startswith(REPO) else path


def load(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def source(path):
    run = load(path)
    return dict(file=rel(path), sha256=sha(path), processId=run['processId'], probe=run['probe'], status=run['status'], moduleSha256=run.get('moduleSha256'))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--publish', required=True)
    ap.add_argument('--recover', required=True)
    ap.add_argument('--history', action='append', default=[])
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    publish_run, recover_run = load(a.publish), load(a.recover)
    initial = publish_run['outputs']['worlds'][0]['initialSave']
    recovery = recover_run['outputs']['recovery']
    outcomes = recovery['outcomes']
    boundaries = []
    for key, (label, state, exists) in BOUNDARIES.items():
        row = outcomes[key]
        journal = row.get('journal') or {}
        boundaries.append(dict(boundary=key, what=label, expected=state, state=journal.get('state'), note=journal.get('note'), saveExists=row.get('saveExists'),
                               boundInstance=row.get('boundInstance'), bodyWhole=row.get('bodyWhole'),
                               passed=journal.get('state') == state and row.get('saveExists') == exists))
    startup = recovery['startup']
    staging = recovery['staging']
    checks = dict(
        publishedBeforeSuccess=initial['publish']['states'] == ['intent', 'published'] and initial['publish']['readBack']['bodyWhole'] is True,
        refusalsWriteNothing=all(r['refused'] and not r['journalWritten'] for r in initial['refusals'].values()),
        ironmanPublishedUnderEngineName=initial['ironman']['published'] is True and initial['ironman']['saveName'] == initial['ironman']['expectedName'] and initial['ironman']['states'] == ['intent', 'published'],
        storeLockExclusive=recovery.get('lockExclusive') is True,
        indexFindsInitialSave=any(r['kind'] == 'save' and r['name'] == initial['publish']['saveName'] for r in initial['publish']['index']['references']),
        processKilledAfterSave=publish_run['status'] == 'running' and initial.get('crash') is not None,
        startupPassHeldLock=startup.get('lockHeld') is True and 'error' not in startup,
        everyBoundaryRecovered=all(b['passed'] for b in boundaries),
        foreignUnreadableSaveKept=outcomes['foreign']['saveExists'] is True,
        indexFailsClosedWithGap=recovery['indexWithForeign']['discoveryComplete'] is False and recovery['indexWithForeign']['unreferenced']['retain'] is True,
        completeIndexStillRetains=recovery['indexComplete']['discoveryComplete'] is True and recovery['indexComplete']['unreferenced']['retain'] is True,
        decisionFollowsReferencesWhenAllKindsBound=recovery['indexComplete']['unreferencedIfAllSaveKindsBound']['retain'] is False and recovery['indexComplete']['worldIfAllSaveKindsBound']['retain'] is True,
        recoveryIdempotent=not any(recovery['recoverAgain'][k] for k in ('resolved', 'unresolved', 'unreadableJournals')),
        stagingRemovedSentinelsKept=not staging['plantedLeft'] and len(staging['sentinelsKept']) == len(recovery['plan']['sentinels']),
        noProbeFailures=not initial['failures'] and not recover_run['outputs']['failures'],
    )
    summary = dict(
        schema='mnbl.evidence.initial-save-recovery', version=1, ticket='MMR-62',
        generated=datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
        sources=dict(publish=source(a.publish), recover=source(a.recover), history=[source(h) for h in a.history]),
        checks=checks, passed=all(checks.values()),
        publish=dict(binding=initial['binding'], seconds=initial['publish']['seconds'], saveName=initial['publish']['saveName'], states=initial['publish']['states'],
                     readBack=initial['publish']['readBack'], uniqueGameId=initial['publish']['journal']['uniqueGameId'], refusals=initial['refusals'],
                     index={k: initial['publish']['index'][k] for k in ('savesListed', 'savesBound', 'discoveryComplete', 'gaps', 'retain', 'reason')},
                     planted=initial.get('planted'), crash=initial.get('crash'),
                     ironman={k: initial['ironman'].get(k) for k in ('saveName', 'expectedName', 'states', 'seconds', 'readBack', 'error')}),
        startup={k: startup.get(k) for k in ('lockHeld', 'recovery', 'staging', 'seconds')},
        boundaries=boundaries,
        foreign=outcomes['foreign'],
        index=dict(withForeign=recovery['indexWithForeign'], complete=recovery['indexComplete']),
        recoverAgain=recovery['recoverAgain'], staging=staging, cleaned=recovery.get('cleaned'), lockExclusive=recovery.get('lockExclusive'),
        dismissedDialogs=[d.get('title') for d in load(a.recover.replace('.json', '-observation.json')).get('dismissedDialogs', [])] if os.path.exists(a.recover.replace('.json', '-observation.json')) else None)
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=2)
        f.write('\n')
    print('wrote', a.out, 'passed', summary['passed'], {k: v for k, v in checks.items() if not v})


if __name__ == '__main__':
    main()
