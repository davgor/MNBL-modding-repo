"""MMR-53: offline check of the naval scenario file (tests/Runtime/NavalScenarios.json).
Every case has a unique id, a source (natural, player, harness) and checks in tools/summarize_naval_missions.py; the world and the rules the
probe reads are present; the regression targets name settlements of world A; every regression names the run that found it, a case that now
guards it, the cause, the fix and its guard (a mutation defined in tests/Runtime/test_naval_regressions.py); the committed fixture, once named,
exists and carries no user path.
Usage: python tests/Runtime/check_naval_scenarios.py
"""
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CASES = ('shipyard', 'route.port', 'route.island', 'dropoff', 'targets', 'ai', 'pirates', 'pirate.battle', 'route.seam', 'pirate.seam', 'naval.defeat', 'raid', 'blockade', 'assets', 'baselines')
RULES = ('crew', 'defeatTroops', 'grain', 'goldMargin', 'repairDamageFraction', 'voyageHours', 'voyageSeconds', 'raftSeconds', 'raidSeconds', 'landingSeconds', 'coastSeconds', 'straitEndDistance', 'returnBoundLand',
         'returnBoundNaval', 'missionReadySeconds', 'teardownSeconds', 'sampleSeconds', 'stockMissingPatterns')
TARGETS = ('start', 'mainland', 'island', 'village', 'raidVillage', 'blockadeTown')


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'NavalScenarios.json'), encoding='utf-8') as f:
        plan = json.load(f)
    with open(os.path.join(REPO, 'tools', 'summarize_naval_missions.py'), encoding='utf-8') as f:
        summarizer = f.read()
    with open(os.path.join(REPO, 'tests', 'Runtime', 'test_naval_regressions.py'), encoding='utf-8') as f:
        replay = f.read()
    problems = []
    if plan.get('schema') != 'mnbl.naval-scenarios.v1':
        problems.append('schema is %r' % plan.get('schema'))
    for field in ('kind', 'seeds', 'culture', 'designatedSave'):
        if field not in (plan.get('world') or {}):
            problems.append('world has no %s' % field)
    for rule in RULES:
        if rule not in (plan.get('rules') or {}):
            problems.append('rules.%s missing' % rule)
    targets = plan.get('targets') or {}
    for key in TARGETS:
        if not re.fullmatch(r'mnbl_[0-9a-f]{8}_[tv]_[0-9a-f]{10}', targets.get(key) or ''):
            problems.append('targets.%s is %r, not a settlement of the world' % (key, targets.get(key)))
    ids = [c.get('id') for c in plan.get('cases') or []]
    if tuple(ids) != CASES:
        problems.append('cases are %s, not %s' % (ids, list(CASES)))
    for c in plan.get('cases') or []:
        if c.get('source') not in ('natural', 'player', 'harness'):
            problems.append('%s: source %r' % (c.get('id'), c.get('source')))
        if not c.get('what'):
            problems.append('%s: no description' % c.get('id'))
        if "rows['%s']" % c.get('id') not in summarizer:
            problems.append('%s: no checks in the summarizer' % c.get('id'))
    seen = set()
    for reg in plan.get('regressions') or []:
        rid = reg.get('id')
        if rid in seen:
            problems.append('duplicate regression %s' % rid)
        seen.add(rid)
        for field in ('foundBy', 'scenario', 'failure', 'cause', 'fix', 'guard'):
            if not reg.get(field):
                problems.append('regression %s has no %s' % (rid, field))
        if reg.get('scenario') not in ids:
            problems.append('regression %s names an unknown case %r' % (rid, reg.get('scenario')))
        guard = reg.get('guard') or {}
        if guard.get('kind') != 'mutation':
            problems.append('regression %s is not guarded by a mutation' % rid)
        else:
            if guard.get('fails') not in ids:
                problems.append('regression %s: its mutation fails an unknown case %r' % (rid, guard.get('fails')))
            if not guard.get('check'):
                problems.append('regression %s: its guard names no check' % rid)
            if "'%s'" % guard.get('mutation') not in replay:
                problems.append('regression %s: mutation %s is not defined in the replay' % (rid, guard.get('mutation')))
    fixture = plan.get('fixture')
    if fixture:
        path = os.path.join(REPO, fixture)
        if not os.path.exists(path):
            problems.append('fixture missing: %s' % fixture)
        else:
            with open(path, encoding='utf-8') as f:
                text = f.read()
            if re.search(r'[A-Za-z]:[\\/]+Users[\\/]+(?!<)', text):
                problems.append('the fixture carries a user path')
    print('naval scenarios: %d cases, %d regressions, %d problems' % (len(ids), len(plan.get('regressions') or []), len(problems)))
    for p in problems:
        print('   problem: ' + p)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
