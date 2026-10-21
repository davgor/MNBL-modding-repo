"""MMR-52: offline check of the land missions' scenario file (tests/Runtime/LandMissionScenarios.json).
Every case has a unique id, a source (natural, player, harness) and checks in tools/summarize_land_missions.py; the worlds have the fields the
probe reads; the rules the probe and the summary read are present; every regression names the run that found it, a case that now guards it,
the cause, the fix and its guard (a mutation defined in tests/Runtime/test_land_missions_regressions.py); the committed fixture, once named,
exists and carries no user path.
Usage: python tests/Runtime/check_land_mission_scenarios.py
"""
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CASES = ('field', 'crossing', 'retreat', 'town', 'castle', 'village', 'hideout', 'siege', 'capture', 'gear', 'locations', 'names', 'assets', 'baselines')
RULES = ('returnBoundLand', 'missionReadySeconds', 'teardownSeconds', 'sampleSeconds', 'cellStride', 'settlementClearance', 'playerTroops', 'siegeTroops',
         'siegePreparationHours', 'siegeObserveHours', 'siegeCompletionFrom', 'battleClasses', 'substitutedClasses', 'crossingTerrains', 'stockMissingPatterns')
# MMR-247 F: the siege case's timing, sally guard and retry (a block of their own: a recorded run's rules must equal the file's).
SIEGE_SETUP = ('siegeCompletionHours', 'siegeObserveMinHours', 'siegeTimingMarginHours', 'siegeSallyMargin', 'siegeCrewLimit', 'siegeAttempts')
WORLD = ('kind', 'seeds', 'culture', 'designatedSave')
WORLD_B = ('kind', 'fixtureSeed', 'fixturePlacement', 'catalogReceipt')


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'LandMissionScenarios.json'), encoding='utf-8') as f:
        plan = json.load(f)
    with open(os.path.join(REPO, 'tools', 'summarize_land_missions.py'), encoding='utf-8') as f:
        summarizer = f.read()
    with open(os.path.join(REPO, 'tests', 'Runtime', 'test_land_missions_regressions.py'), encoding='utf-8') as f:
        replay = f.read()
    problems = []
    if plan.get('schema') != 'mnbl.land-mission-scenarios.v1':
        problems.append('schema is %r' % plan.get('schema'))
    for key, fields in (('world', WORLD), ('worldB', WORLD_B)):
        for field in fields:
            if field not in (plan.get(key) or {}):
                problems.append('%s has no %s' % (key, field))
    notes = []
    for path in ((plan.get('worldB') or {}).get('fixturePlacement'), (plan.get('worldB') or {}).get('catalogReceipt')):
        if path and not os.path.exists(os.path.join(REPO, path)):
            notes.append("world B's input %s is not present in this checkout (git-ignored, as for MMR-51; the probe records world B as failed without it)" % path)
    rules = plan.get('rules') or {}
    for rule in RULES:
        if rule not in rules:
            problems.append('rules.%s missing' % rule)
    siege = plan.get('siegeSetup') or {}
    for key in SIEGE_SETUP:
        if not isinstance(siege.get(key), (int, float)):
            problems.append('siegeSetup.%s missing' % key)
    if isinstance(siege.get('siegeObserveMinHours'), (int, float)) and not 0 < siege['siegeObserveMinHours'] <= (rules.get('siegeObserveHours') or 0):
        problems.append('siegeSetup.siegeObserveMinHours must be in (0, rules.siegeObserveHours]')
    if not 1 <= (siege.get('siegeAttempts') or 0) <= 3:
        problems.append('siegeSetup.siegeAttempts is %r (1 to 3)' % siege.get('siegeAttempts'))
    if rules.get('cellStride') != 1:
        problems.append('rules.cellStride is %r: a class or crossing is absent only when every cell was sampled' % rules.get('cellStride'))
    if not set(rules.get('substitutedClasses') or []) <= set(rules.get('battleClasses') or []):
        problems.append('a substituted class is not a battle class')
    ids = [c.get('id') for c in plan.get('cases') or []]
    if len(ids) != len(set(ids)):
        problems.append('duplicate case ids')
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
    print('land mission scenarios: %d cases, %d regressions, %d problems' % (len(ids), len(plan.get('regressions') or []), len(problems)))
    for p in problems:
        print('   problem: ' + p)
    for n in notes:
        print('   note: ' + n)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
