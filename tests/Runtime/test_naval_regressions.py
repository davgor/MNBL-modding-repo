"""MMR-53: offline replay of the naval missions' regression fixture.
The committed fixture (tests/Runtime/NavalScenarios.json "fixture": a passing naval_missions run's trimmed output) must pass every case when
recomputed by tools/summarize_naval_missions.py; each regression guarded by a mutation reintroduces its failure into a copy of the fixture,
and the case it names must then fail on the check its guard names.
Usage: python tests/Runtime/test_naval_regressions.py
"""
import copy
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(REPO, 'tools'))
from summarize_naval_missions import recompute  # noqa: E402


def cases(r):
    return r['outputs']['cases']


def repair_not_applied(r):
    # Run 27796: the repair option ran before the port menu's view attached its handler; the refresh threw.
    c = cases(r)['shipyard.repair']
    c['error'] = 'System.NullReferenceException: Object reference not set to an instance of an object.'
    c['repaired'] = {'hitPoints': c['setup']['hitPointsDamaged'], 'goldPaid': 0}


def ai_party_off_navigation(r):
    # Run 27796: AI positions read at the transition event (a party created at sea raises it before its position is set).
    t = r['outputs']['traffic']
    t['problemCount'] = 1
    t['problems'] = [{'party': 'villager_1', 'kind': 'villager', 'atSea': True, 'problem': 'not a valid campaign position'}]


def raid_not_looted(r):
    # Run 26772: the victory's loot screens went unanswered; the raid never progressed.
    cases(r)['raid']['villageState'] = 'Normal'


def raid_left_off_sea(r):
    # Run 43188: the raid's end menu for a raid led by another went unanswered; the case timed out.
    c = cases(r)['raid']
    c['error'] = 'System.TimeoutException: the party did not come back idle to the map (at village_raid_ended_leaded_by_someone_else)'
    c['atSeaAfter'] = None


def raid_without_landing_setup(r):
    # Run 26772: landing points counted by exact tag (0); the raid controller finds them by tag expression.
    cases(r)['raid']['fight']['sceneTags']['landing'] = 0


def raid_main_agent_down(r):
    # Run 42168: War Sails' landing left the player's agent down; the probe killed only with the main agent, so no defender died, the mission
    # ended without a result and the case timed out at the encounter.
    c = cases(r)['raid']
    c['fight']['killed'] = 0
    c['fight']['victory'] = False
    c['villageState'] = 'Normal'
    c['error'] = 'System.TimeoutException: the party did not come back idle to the map (at encounter)'


def raid_return_checked_on_land(r):
    # Run 36684: MMR-52's shared return check required land; the seaborne raid's return at sea failed with the land bound.
    c = cases(r)['raid']
    c['returnBound'] = r['outputs']['rules']['returnBoundLand']
    c['failures'] = ['the party returned off land']


def defeat_without_capture(r):
    # Run 36684: troops left ashore kept the player's side alive; the encounter went on and no capture followed.
    c = cases(r)['naval.defeat']
    c['captured'] = False
    c['error'] = 'System.InvalidOperationException: the naval defeat did not lead to capture (menus encounter)'


def raft_stranded(r):
    # Run 42652: the raft's wait menu could not start campaign time; the party stayed at sea in raft state.
    c = cases(r)['naval.defeat']
    c['raft']['atSea'] = True
    c['raft']['raftAtEnd'] = True


MUTATIONS = {
    'defeat-without-capture': defeat_without_capture,
    'raft-stranded': raft_stranded,
    'repair-not-applied': repair_not_applied,
    'ai-party-off-navigation': ai_party_off_navigation,
    'raid-not-looted': raid_not_looted,
    'raid-left-off-sea': raid_left_off_sea,
    'raid-without-landing-setup': raid_without_landing_setup,
    'raid-return-checked-on-land': raid_return_checked_on_land,
    'raid-main-agent-down': raid_main_agent_down,
}


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'NavalScenarios.json'), encoding='utf-8') as f:
        plan = json.load(f)
    lines, failed = [], 0

    def report(ok, text):
        nonlocal failed
        failed += 0 if ok else 1
        lines.append(('PASS ' if ok else 'FAIL ') + text)

    if not plan.get('fixture'):
        report(False, 'the scenario file names a fixture')
    else:
        with open(os.path.join(REPO, plan['fixture']), encoding='utf-8') as f:
            fixture = json.load(f)
        scenarios, passed, total = recompute(fixture, plan)
        bad = ['%s: %s' % (s['id'], c['check']) for s in scenarios for c in s['checks'] if not c['passed']]
        report(passed == total, 'the fixture %s passes every case (%d/%d checks)%s' % (plan['fixture'], passed, total, ('; not passed: ' + '; '.join(bad[:5])) if bad else ''))
        for reg in plan.get('regressions') or []:
            guard = reg.get('guard') or {}
            mutate = MUTATIONS.get(guard.get('mutation'))
            if mutate is None:
                report(False, '%s: mutation %s is defined' % (reg['id'], guard.get('mutation')))
                continue
            copy_ = copy.deepcopy(fixture)
            mutate(copy_)
            # The guarded case must fail on the check the guard names, not only on the generic "ran without error" row.
            case = next((s for s in recompute(copy_, plan)[0] if s['id'] == guard['fails']), None)
            failed_checks = [c['check'] for c in (case or {}).get('checks', []) if not c['passed']]
            named = guard.get('check')
            report(case is not None and case['passed'] is False and bool(named) and any(c.startswith(named) for c in failed_checks),
                   '%s: reintroduced (%s), %s fails on "%s"' % (reg['id'], guard['mutation'], guard['fails'], named))
    print('naval regressions: %d checks, %d failed' % (len(lines), failed))
    for line in lines:
        print('   ' + line)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
