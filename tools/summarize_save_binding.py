"""MMR-48: summarizes save_binding runs into evidence/persistence/save-binding.json. Recomputes each expectation from the archived outputs
(evidence/runtime/in-game/save_binding-<pid>.json), not from the probe's own pass lines.
Usage: python tools/summarize_save_binding.py <accepted pid> [<earlier pid> ...]
"""
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KINDS = ('saveAs', 'quickSave', 'autoSave', 'ironman')


def load(pid):
    path = os.path.join(REPO, 'evidence', 'runtime', 'in-game', 'save_binding-%s.json' % pid)
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f), os.path.relpath(path, REPO).replace('\\', '/')


def save_ok(row, binding, kind):
    j = row.get('journal') or {}
    h = row.get('header') or {}
    return (j.get('kind') == kind and j.get('state') == 'published' and j.get('saveName') == row.get('expectedName')
            and h.get('instance') == binding.get('instance') and h.get('contentId') == binding.get('contentId') and h.get('package') == binding.get('package')
            and h.get('whole') is True and not row.get('compatibilityDifferences') and not row.get('problems')
            and (row.get('intentUtc') or '~') <= (row.get('fileWrittenUtc') or ''))


def run(pid):
    r, path = load(pid)
    o = r.get('outputs') or {}
    b = o.get('binding') or {}
    saves = o.get('saves') or {}
    startup = o.get('startup') or {}
    mutable = o.get('mutable') or {}
    before, after = o.get('immutableBefore') or {}, o.get('immutableAfter') or {}
    index = o.get('index') or {}
    refs = index.get('campaignReferences') or []
    rec = o.get('recovery') or {}
    t = o.get('transfer') or {}
    first, second = t.get('first') or {}, t.get('second') or {}
    pkg = b.get('package')
    after_counts = (((second.get('countsAfter') or {}).get('packages') or {}).get(pkg) or {})
    menu = o.get('mainMenu') or {}
    cleanup = o.get('cleanup') or {}
    names = [(saves.get(k) or {}).get('expectedName') for k in KINDS]
    initial = (o.get('initialSave') or {})
    autosave = (saves.get('autoSave') or {}).get('expectedName')
    expected_deleted = [n for n in names + [initial.get('saveName')] if n and (n != autosave or not cleanup.get('autosaveHadFile'))]
    rows = [
        ('the startup pass ran the later-save recovery (nothing unresolved, no unreadable journal); the index binds every save kind',
         isinstance(startup.get('laterRecovery'), dict) and startup['laterRecovery'].get('unresolved') == [] and startup['laterRecovery'].get('unreadableJournals') == [] and startup.get('saveKindsBound') is True),
        ('the committed campaign is bound (instance, package, content id); its initial save published', bool(b.get('instance')) and bool(pkg) and b.get('contentId') not in (None, 'none') and initial.get('state') == 'published'),
        ('mutable changes: a town changed owner, the main party moved', mutable.get('ownerAfter') not in (None, mutable.get('ownerBefore')) and mutable.get('partyAfter') != mutable.get('partyBefore')),
    ]
    for k in KINDS:
        rows.append(('%s: journaled before the write, read back bound to the exact world and campaign, whole, compatibility keys, published' % k, save_ok(saves.get(k) or {}, b, k)))
    rows += [
        ('the quick save replaced the Save As file (prior recorded)', (saves.get('quickSave') or {}).get('priorExisted') is True and ((saves.get('quickSave') or {}).get('journal') or {}).get('priorExists') is True),
        ('c2: package and bundle byte-identical after the changes and saves; bundle verifies with the same content id', before.get('package') == after.get('package') and before.get('bundle') == after.get('bundle')
         and before.get('package') not in (None, 'absent') and after.get('bundleProblems') == 0 and o.get('bundleWorldContentId') == b.get('contentId')),
        ('the index finds the initial save, the four saves and the active campaign', all(('save ' + n) in refs for n in [initial.get('saveName')] + names if n) and any(x.startswith('active campaign') for x in refs)),
        ('recovery with the game save list: torn removed, untouched abandoned', (rec.get('torn') or {}).get('state') == 'removed' and (rec.get('untouched') or {}).get('state') == 'abandoned'),
        ('c3: exported, imported (copied), imported again (reused); 4 references, 1 campaign; the target index complete; 4 saves loadable; nothing regenerated',
         second.get('indexComplete') is True and ((first.get('packages') or {}).get(pkg) == 'copied') and ((second.get('packages') or {}).get(pkg) == 'reused') and ((second.get('bundles') or {}).get(b.get('contentId')) == 'reused')
         and after_counts.get('references') == 4 and after_counts.get('instances') == 1 and first.get('regenerated') is False and (t.get('inspection') or {}).get('savesLoadable') == 4 and t.get('packageSchemaAgrees') is True),
        ('at the main menu with no game: every save found bound from its header, with the build keys; the binding detached', menu.get('game') is False and menu.get('bound') == 4
         and len(menu.get('compatibility') or {}) == 9 and menu.get('attachedAfterEnd') is False),
        ('cleanup: the probe saves and the campaign initial save removed, the autosave slot restored',
         bool(cleanup.get('deleted')) and all(v is True for v in cleanup['deleted'].values()) and cleanup.get('autosaveRestored') is True
         and all(n in cleanup['deleted'] for n in expected_deleted)),
    ]
    checks = [{'check': n, 'passed': bool(ok)} for n, ok in rows]
    return {'pid': pid, 'result': path, 'status': r.get('status'), 'failures': o.get('failures'), 'checks': checks,
            'passed': sum(c['passed'] for c in checks), 'total': len(checks),
            'saves': {k: {'name': (saves.get(k) or {}).get('expectedName'), 'seconds': (saves.get(k) or {}).get('seconds'), 'readbackSeconds': (saves.get(k) or {}).get('readbackSeconds')} for k in KINDS},
            'transfer': {'exportSeconds': t.get('exportSeconds'), 'importSeconds': t.get('importSeconds'), 'files': (t.get('export') or {}).get('files'), 'bytes': (t.get('export') or {}).get('bytes')},
            'binding': b, 'startSeconds': o.get('startSeconds')}


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    accepted = run(sys.argv[1])
    earlier = [run(p) for p in sys.argv[2:]]
    out = {'schema': 'mnbl.evidence.save-binding', 'version': 1, 'ticket': 'MMR-48', 'accepted': accepted, 'earlier': earlier}
    target = os.path.join(REPO, 'evidence', 'persistence', 'save-binding.json')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write('\n')
    print('accepted %s: %d/%d recomputed checks; status %s' % (accepted['pid'], accepted['passed'], accepted['total'], accepted['status']))
    for c in accepted['checks']:
        if not c['passed']:
            print('   not passed: ' + c['check'])
    for e in earlier:
        print('earlier %s: %d/%d; status %s' % (e['pid'], e['passed'], e['total'], e['status']))
    return 0 if accepted['passed'] == accepted['total'] and accepted['status'] == 'ok' else 1


if __name__ == '__main__':
    sys.exit(main())
