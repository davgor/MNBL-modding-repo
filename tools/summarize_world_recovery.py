"""MMR-47: summarizes world_recovery run pairs (crash, then resume) and the world_setup host-problem path into
evidence/generation/recovery/world-recovery.json. Recomputes each expectation from the archived outputs
(evidence/runtime/in-game/world_recovery-<pid>.json, world_setup-<pid>.json), not from the probe's own pass lines.
Usage: python tools/summarize_world_recovery.py <crash pid> <resume pid> <world_setup pid> [<earlier crash pid> <earlier resume pid> ...]
"""
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(name):
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', name)
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f), os.path.relpath(path, REPO).replace('\\', '/')


def pair(crash_pid, resume_pid):
    crash, crash_path = load('world_recovery-%s.json' % crash_pid)
    resume, resume_path = load('world_recovery-%s.json' % resume_pid)
    plan = (crash.get('outputs') or {}).get('plan') or {}
    o = resume.get('outputs') or {}
    startup = o.get('startup') or {}
    cleanup = startup.get('cleanup') or {}
    staging = (cleanup.get('staging') or {}).get('removed') or []
    removed_jobs = cleanup.get('finishedJobsRemoved') or []
    planted = plan.get('planted') or {}
    offer = o.get('offer') or {}
    resumed = o.get('resumed') or {}
    discard = o.get('discard') or {}
    cancelled = o.get('cancelled') or {}
    rows = [
        ('crash during the source generation', plan.get('phaseAtCrash') == 'GeneratingSource' and plan.get('choiceRecorded') is True),
        ('startup: root verified, planted staging and leftover removed, crashed job kept, worlds unchanged',
         startup.get('root') == 'verified' and any(s.endswith(planted.get('staging', '\0')) for s in staging) and planted.get('leftover') in removed_jobs
         and plan.get('jobId') not in removed_jobs and o.get('worldsKept') is True),
        ('the crashed job offered (resumable, its seed)', offer.get('offeredId') == plan.get('jobId') and offer.get('resumable') is True and offer.get('seed') == plan.get('seed')),
        ('continued in its own directory, request resumed', resumed.get('state') == 'Generated' and resumed.get('requestResumed') is True
         and os.path.basename(str(resumed.get('jobRoot'))) == plan.get('jobId')),
        ('the interrupted attempt ran again, the same world', resumed.get('attemptRerun') is True and resumed.get('sameWorldAsBefore') is True and resumed.get('bridgeStatus') == 'accepted'),
        ('another generator\'s job: discard only, only it removed', discard.get('offered') is True and discard.get('removed') is True and discard.get('worldsKept') is True),
        ('a cancelled generation shows its reference, diagnostics retained', cancelled.get('category') == 'InterruptedGeneration' and bool(cancelled.get('correlationId'))
         and cancelled.get('correlationId', '\0') in (cancelled.get('reference') or '') and 'events.jsonl' in (cancelled.get('recordFiles') or [])),
        ('releases: the reviewed world by the next Generate, the cancelled job on close', o.get('resumedReleasedByNextGenerate') is True and o.get('cancelledReleasedOnClose') is True),
    ]
    checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows]
    return {'crash': {'pid': crash_pid, 'result': crash_path, 'status': crash.get('status')},
            'resume': {'pid': resume_pid, 'result': resume_path, 'status': resume.get('status'), 'failures': o.get('failures')},
            'checks': checks, 'passed': sum(c['passed'] for c in checks), 'total': len(checks),
            'resumeSeconds': resumed.get('seconds'), 'attempts': resumed.get('attempts'), 'worldContentId': resumed.get('worldContentId'),
            'cleanup': {'stagingRemoved': staging, 'jobsRemoved': removed_jobs, 'jobsKept': [ {k: j.get(k) for k in ('id', 'phase', 'kind', 'reason')} for j in cleanup.get('jobs') or []]},
            'offered': offer.get('unfinished'), 'cancelled': cancelled}


def host(setup_pid):
    r, path = load('world_setup-%s.json' % setup_pid)
    m = (r.get('outputs') or {}).get('missingWarSails') or {}
    ok = m.get('entryDisabled') is False and m.get('canGenerate') is False and m.get('generating') is False and 'Reference MNBL-' in (m.get('reference') or '')
    return {'pid': setup_pid, 'result': path, 'status': r.get('status'), 'failures': (r.get('outputs') or {}).get('failures'),
            'missingWarSails': m, 'check': {'check': 'without War Sails the setup opens, refuses to generate and shows the problem with its reference', 'passed': ok}}


def main():
    if len(sys.argv) < 4 or (len(sys.argv) - 4) % 2:
        print(__doc__)
        return 2
    accepted = pair(sys.argv[1], sys.argv[2])
    setup = host(sys.argv[3])
    earlier = [pair(sys.argv[i], sys.argv[i + 1]) for i in range(4, len(sys.argv), 2)]
    out = {'schema': 'mnbl.evidence.world-recovery', 'version': 1, 'ticket': 'MMR-47', 'accepted': accepted, 'hostProblem': setup, 'earlier': earlier}
    target = os.path.join(REPO, 'evidence', 'generation', 'recovery', 'world-recovery.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    print('accepted %s/%s: %d/%d recomputed checks; resume status %s' % (accepted['crash']['pid'], accepted['resume']['pid'], accepted['passed'], accepted['total'], accepted['resume']['status']))
    for c in accepted['checks']:
        if not c['passed']:
            print('   not passed: ' + c['check'])
    print('world_setup %s: %s (%s)' % (setup['pid'], 'passed' if setup['check']['passed'] else 'NOT passed', setup['status']))
    for e in earlier:
        print('earlier %s/%s: %d/%d; resume status %s' % (e['crash']['pid'], e['resume']['pid'], e['passed'], e['total'], e['resume']['status']))
    return 0 if accepted['passed'] == accepted['total'] and setup['check']['passed'] and setup['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
