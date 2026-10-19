"""MMR-51: offline check of the campaign soak regression fixture (tests/Runtime/CampaignSoakScenarios.json).
Every scenario has a unique id, a world the file defines, a source (natural, player, diagnostic, harness) and checks in
tools/summarize_campaign_soak.py; each world has the fields the probe reads (the probe also checks them, and B's files, before any game);
every regression names the run that found it, a scenario that now guards it, the cause, the fix and its guard (a mutation defined in
tests/Runtime/test_campaign_soak_regressions.py, or an input check); the committed fixture, once named, exists.
Usage: python tests/Runtime/check_campaign_soak_scenarios.py
"""
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WORLD_FIELDS = {'A': ('seeds', 'culture', 'observeHours', 'afterDiagnosticHours', 'movedFraction'),
                'B': ('fixtureSeed', 'fixturePlacement', 'catalogReceipt', 'observeHours', 'movedFraction')}


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'CampaignSoakScenarios.json'), encoding='utf-8') as f:
        plan = json.load(f)
    with open(os.path.join(REPO, 'tools', 'summarize_campaign_soak.py'), encoding='utf-8') as f:
        summarizer = f.read()
    with open(os.path.join(REPO, 'tests', 'Runtime', 'test_campaign_soak_regressions.py'), encoding='utf-8') as f:
        replay = f.read()
    problems, notes = [], []
    if plan.get('schema') != 'mnbl.campaign-soak-scenarios.v1':
        problems.append('schema is %r' % plan.get('schema'))
    worlds = plan.get('worlds') or {}
    for key, fields in WORLD_FIELDS.items():
        world = worlds.get(key)
        if world is None:
            problems.append('world %s missing' % key)
            continue
        for field in fields:
            if field not in world:
                problems.append('world %s has no %s' % (key, field))
        if not isinstance(world.get('observeHours'), (int, float)) or world['observeHours'] <= 0:
            problems.append('world %s has no observation window' % key)
        for kind, fraction in (world.get('movedFraction') or {}).items():
            if not isinstance(fraction, (int, float)) or not 0 < fraction <= 1:
                problems.append('world %s movedFraction.%s is %r' % (key, kind, fraction))
    for field in ('fixturePlacement', 'catalogReceipt'):
        rel = (worlds.get('B') or {}).get(field)
        if rel and not os.path.exists(os.path.join(REPO, rel)):
            notes.append('world B\'s %s %s is not present in this checkout (git-ignored; the probe refuses to start without it)' % (field, rel))
    if (worlds.get('A') or {}).get('kind') != 'generated' or (worlds.get('B') or {}).get('kind') != 'fixture':
        problems.append('world A must be generated and world B the fixture world')
    for rule in ('stuckHours', 'moveEpsilon', 'movedDistance', 'timeoutSeconds', 'stallSeconds', 'maxStalls'):
        if not isinstance((plan.get('rules') or {}).get(rule), (int, float)):
            problems.append('rule %s missing' % rule)
    ids = [s.get('id') for s in plan.get('scenarios') or []]
    if len(ids) != len(set(ids)):
        problems.append('duplicate scenario ids')
    checked = set(re.findall(r"rows\['([^']+)'\]", summarizer))
    for s in plan.get('scenarios') or []:
        if s.get('world') is not None and s.get('world') not in worlds:
            problems.append('scenario %s names world %r' % (s.get('id'), s.get('world')))
        if s.get('source') not in ('natural', 'player', 'diagnostic', 'harness'):
            problems.append('scenario %s has source %r' % (s.get('id'), s.get('source')))
        if not s.get('what'):
            problems.append('scenario %s has no description' % s.get('id'))
        if s.get('id') not in checked:
            problems.append('scenario %s has no checks in the summarizer' % s.get('id'))
    for extra in sorted(checked - set(ids)):
        problems.append('the summarizer checks %s, which the file does not define' % extra)
    mutations = set(re.findall(r"^\s+'([a-z-]+)': [a-z_]+,$", replay, re.M))
    for i, r in enumerate(plan.get('regressions') or []):
        for field in ('id', 'foundBy', 'scenario', 'failure', 'cause', 'fix', 'guard'):
            if not r.get(field):
                problems.append('regression %d has no %s' % (i, field))
        if r.get('scenario') not in ids:
            problems.append('regression %s names scenario %r' % (r.get('id'), r.get('scenario')))
        guard = r.get('guard') or {}
        if guard.get('kind') == 'mutation':
            if guard.get('mutation') not in mutations:
                problems.append('regression %s names mutation %r, which the replay does not define' % (r.get('id'), guard.get('mutation')))
            if guard.get('fails') not in ids:
                problems.append('regression %s guards scenario %r' % (r.get('id'), guard.get('fails')))
        elif guard.get('kind') == 'input':
            if not guard.get('check'):
                problems.append('regression %s has an input guard without its check' % r.get('id'))
        else:
            problems.append('regression %s has guard kind %r' % (r.get('id'), guard.get('kind')))
    fixture = plan.get('fixture')
    if fixture and not os.path.exists(os.path.join(REPO, fixture)):
        problems.append('the fixture %s does not exist' % fixture)
    if not fixture:
        notes.append('no fixture named yet')
    print('campaign soak scenarios: %d scenarios, %d regressions, %d problems' % (len(ids), len(plan.get('regressions') or []), len(problems)))
    for p in problems:
        print('   ' + p)
    for n in notes:
        print('   note: ' + n)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
