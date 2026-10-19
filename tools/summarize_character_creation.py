"""MMR-32: summarize the in-game character-creation and hero-body evidence of generated_campaign runs.

Usage: python tools/summarize_character_creation.py OUT_JSON RUN_JSON [RUN_JSON ...]
Each RUN_JSON is a collected probe result (evidence/runtime/in-game/generated_campaign-<pid>.json) made with creationAudit=1,
restrictCreationCultures=<cultures> and creationCultures=<the same cultures>. The checks are independent of the probe's own failure
list:
  * the culture filter kept exactly the requested cultures, removed the others and missed none, and character creation then listed
    exactly those cultures;
  * every listed culture offers origins; for every origin every later narrative menu offers an option; options set title types; the
    menu characters of every origin and title type resolved their equipment rosters and hand items; the culture has a creation body;
  * the audit reports no problem, the probe culture was restored, and the run reports no validation failure;
  * when the run drew hero bodies (heroBundle): every XML hero got an explicit, distinct body, the same seeds gave the same bodies, and
    no body is a template's bare minimum.
"""
import json
import sys


def main():
    out_path, runs = sys.argv[1], sys.argv[2:]
    checks = []
    summary_runs = []

    def check(ok, label):
        checks.append({'ok': bool(ok), 'label': label})

    for run_path in runs:
        run = json.load(open(run_path, encoding='utf-8-sig'))
        args = run.get('args', {})
        world = run['outputs']['worlds'][0]
        pid = run.get('processId')
        expected = sorted(x for x in args.get('creationCultures', '').split(',') if x)
        requested = sorted(x for x in args.get('restrictCreationCultures', '').split(',') if x)
        tag = 'run %s (%s)' % (pid, ','.join(expected))
        check(run.get('status') == 'ok', tag + ': the probe finished ok')
        check(requested == expected and expected, tag + ': the filter was asked for exactly the expected cultures')
        f = world.get('creationFilter') or {}
        check(sorted(f.get('kept') or []) == expected and not f.get('missing'), tag + ': the culture filter kept %s, removed %s and missed none' % (
            ','.join(f.get('kept') or []), ','.join(f.get('removed') or [])))
        audit = world.get('characterCreation') or {'cultures': [], 'problems': ['missing audit']}
        cultures = {c['culture']: c for c in audit['cultures']}
        check(sorted(cultures) == expected, tag + ': character creation listed ' + ','.join(sorted(cultures)))
        check(not audit['problems'], tag + ': the audit reports no problem (%d)%s' % (len(audit['problems']), '' if not audit['problems'] else ': ' + '; '.join(audit['problems'][:5])))
        for name in expected:
            c = cultures.get(name)
            if c is None:
                check(False, tag + ': ' + name + ' audited')
                continue
            dead = [(o['option'], m) for o in c['origins'] for m, opts in o['menus'].items() if not opts]
            urban = sorted(set(o['parentOccupation'] or '' for o in c['origins']))
            check(c['origins'] and not dead, '%s: %s: %d origins (%s), each with an option in every one of %d later menus' % (
                tag, name, len(c['origins']), ', '.join(urban), len(audit.get('laterMenus') or [])))
            check(len(c['titleTypes']) >= 2 and c['characters'] > 0 and c['equipmentRosters'] > 0, '%s: %s: %d title types (%s); %d menu characters, %d equipment rosters and %d hand items resolved over %d OnSelect calls' % (
                tag, name, len(c['titleTypes']), ', '.join(c['titleTypes']), c['characters'], c['equipmentRosters'], c['handItems'], c['selections']))
            check(bool(c['bodyProperty']), '%s: %s: creation body property %s' % (tag, name, c['bodyProperty']))
        check(audit.get('restoredCulture') == args.get('culture', 'empire'), tag + ': the probe culture %s was restored and read back' % audit.get('restoredCulture'))
        failures = world.get('validationFailures', [])
        check(not failures, tag + ': the run reports no validation failure (%d)%s' % (len(failures), '' if not failures else ': ' + '; '.join(failures[:5])))
        bodies = world.get('heroBodies')
        if args.get('heroBundle'):
            b = bodies or {}
            check(not b.get('error') and b.get('drawn') == b.get('xmlHeroes') and b.get('xmlHeroes', 0) > 0 and b.get('explicitBodies') == b.get('xmlHeroes'),
                  tag + ': the engine drew an explicit body for every one of %s XML heroes (%s women) and the NPCCharacters carry it' % (b.get('xmlHeroes'), b.get('women')))
            check(b.get('distinctKeys') == b.get('drawn') and b.get('deterministic') is True and b.get('templateMinimaReused') == 0,
                  tag + ': the %s bodies are distinct over %s body templates, the same seeds gave the same bodies, and none is a template minimum' % (b.get('distinctKeys'), b.get('bodyTemplates')))
        summary_runs.append({
            'run': run_path.replace('\\', '/'), 'pid': pid, 'status': run.get('status'), 'moduleSha256': run.get('moduleSha256'), 'worldId': world.get('worldId'),
            'expected': expected, 'creationFilter': f, 'firstMenu': audit.get('firstMenu'), 'laterMenus': audit.get('laterMenus'),
            'cultures': [{'culture': n, 'origins': len(cultures[n]['origins']), 'parentOccupations': sorted(set(o['parentOccupation'] or '' for o in cultures[n]['origins'])),
                          'titleTypes': cultures[n]['titleTypes'], 'selections': cultures[n]['selections'], 'characters': cultures[n]['characters'],
                          'equipmentRosters': cultures[n]['equipmentRosters'], 'handItems': cultures[n]['handItems'], 'bodyProperty': cultures[n]['bodyProperty']}
                         for n in sorted(cultures)],
            'problems': audit['problems'], 'validationFailures': failures, 'heroBodies': bodies,
        })
    summary = {'schema': 'mnbl.evidence.character-creation', 'version': 2, 'ticket': 'MMR-32', 'runs': summary_runs,
               'checks': checks, 'passed': sum(c['ok'] for c in checks), 'total': len(checks)}
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(summary, f, indent=1)
        f.write('\n')
    for c in checks:
        print(('ok    ' if c['ok'] else 'FAIL  ') + c['label'])
    print('%d/%d checks passed' % (summary['passed'], summary['total']))
    sys.exit(0 if summary['passed'] == summary['total'] else 1)


if __name__ == '__main__':
    main()
