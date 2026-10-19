"""MMR-52: offline replay of the land missions' regression fixture.
The committed fixture (tests/Runtime/LandMissionScenarios.json "fixture": a passing land_missions run's trimmed output) must pass every case
when recomputed by tools/summarize_land_missions.py; each regression guarded by a mutation reintroduces its failure into a copy of the
fixture, and the case it names must then fail.
Usage: python tests/Runtime/test_land_missions_regressions.py
"""
import copy
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(REPO, 'tools'))
from summarize_land_missions import recompute  # noqa: E402


def outputs(r):
    return r['outputs']


def battles(r):
    return [c for k, c in outputs(r)['cases'].items() if (k.startswith('field.') or k.startswith('B.field.') or k.startswith('gear.')) and not c.get('absent')]


def crafted_weapon_unfound(r):
    # Run 29768: every crafted weapon was counted as having no mesh.
    o = outputs(r)
    culture = sorted(o['culturesPresent'])[0]
    o['cultures'][culture]['incomplete'] = 1
    battles(r)[0]['gear']['incomplete'] = 1


def culture_without_hero(r):
    # Runs 29768 and 25280: a culture's battles fought bandits, so no hero of it was seen.
    o = outputs(r)
    culture = sorted(o['culturesPresent'])[-1]
    o['cultures'][culture]['heroes'] = 0


def hideout_not_entered(r):
    # Run 29768: the attack option was disabled; no troop selection, no mission.
    h = outputs(r)['cases']['hideout']
    h['setup'].pop('option', None)
    h['setup'].pop('troopSelection', None)


def hideout_did_not_finish(r):
    # Run 25280: the game hung in the boss's dialogue; the case never reached its end.
    h = outputs(r)['cases']['hideout']
    h.pop('seconds', None)
    h['fight'].pop('victory', None)


def siege_interrupted(r):
    # Run 29768: an encounter stopped the camp's preparation.
    s = outputs(r)['cases']['siege']['setup']
    s['interruption'] = {'hours': 2.0, 'menu': 'encounter'}
    s['prepared'] = False


def capture_teardown_timeout(r):
    # Run 29768: the defeated mission never tore down; no capture followed.
    c = outputs(r)['cases']['capture']
    c['error'] = 'System.TimeoutException: the mission did not tear down'
    c['captured'] = False
    c['escaped'] = False


def crossing_sampled_at_stride(r):
    # Run 29768: every fifth cell sampled, no crossing found.
    o = outputs(r)
    o['terrain']['stride'] = 5
    o['terrain']['crossingCells'] = 0
    o['cases']['crossing'] = {'absent': True}


def siege_returned_far(r):
    # Review: the siege's return was not judged (run 48860's party ended 3.82 map units from where it began).
    outputs(r)['cases']['siege']['returnDisplacement'] = 3.82


def agent_off_navmesh(r):
    # Review: no spawn position was checked.
    sp = battles(r)[0]['mission']['spawns']
    sp['offNavmesh'] = 1
    sp['samples'] = ['a troop off the navmesh']


def unknown_missing_line(r):
    # Review: a missing line naming neither the world nor an opened scene was only tallied.
    log = outputs(r)['cases']['town']['log']
    log['otherMissingCount'] = 1
    log['otherMissing'] = ['Unable to find mesh: generated_banner_material']


def parked_party_joined(r):
    # Review, run 17928: parties parked by earlier battles joined a battle as the player's allies.
    case = battles(r)[0]
    case['mapEvent']['involved'] = case['mapEvent']['involved'] + ['parked_lord_party_1']


def retreat_won(r):
    # Review: nothing showed the retreat left the battle to the enemy.
    outputs(r)['cases']['retreat']['outcome']['playerWon'] = True


def guard_from_older_battle(r):
    # Review: the guard row could be met by an earlier battle's observation of the same scene.
    outputs(r)['cases']['field.open']['guardObservations'] = 0


def siege_camp_never_built(r):
    # Review: a camp that never built would have passed once advanced.
    outputs(r)['cases']['siege']['setup']['preparationAdvanced']['observedProgress'] = 0.0


def tavern_not_opened(r):
    # Run 3940: the game hung as the tavern opened; the town case never reached its end.
    town = outputs(r)['cases']['town']
    town.pop('tavern', None)
    town.pop('seconds', None)


def escape_released_far(r):
    # Runs 37204 and 35564: the escape's two teleports; a release beyond both must still fail.
    capture = outputs(r)['cases']['capture']
    capture['returnDisplacement'] = 2.5 * capture['encounterRadius'] + 0.5


MUTATIONS = {
    'escape-released-far': escape_released_far,
    'tavern-not-opened': tavern_not_opened,
    'siege-returned-far': siege_returned_far,
    'agent-off-navmesh': agent_off_navmesh,
    'unknown-missing-line': unknown_missing_line,
    'parked-party-joined': parked_party_joined,
    'retreat-won': retreat_won,
    'guard-from-older-battle': guard_from_older_battle,
    'siege-camp-never-built': siege_camp_never_built,
    'crafted-weapon-unfound': crafted_weapon_unfound,
    'culture-without-hero': culture_without_hero,
    'hideout-not-entered': hideout_not_entered,
    'hideout-did-not-finish': hideout_did_not_finish,
    'siege-interrupted': siege_interrupted,
    'capture-teardown-timeout': capture_teardown_timeout,
    'crossing-sampled-at-stride': crossing_sampled_at_stride,
}


def main():
    with open(os.path.join(REPO, 'tests', 'Runtime', 'LandMissionScenarios.json'), encoding='utf-8') as f:
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
        scenarios, passed, total, absent = recompute(fixture, plan)
        bad = ['%s: %s' % (s['id'], c['check']) for s in scenarios for c in s['checks'] if not c['passed']]
        report(passed == total, 'the fixture %s passes every case (%d/%d checks; absent %s)%s' % (plan['fixture'], passed, total, absent, ('; not passed: ' + '; '.join(bad[:5])) if bad else ''))
        for reg in plan.get('regressions') or []:
            guard = reg.get('guard') or {}
            if guard.get('kind') != 'mutation':
                continue
            mutate = MUTATIONS.get(guard.get('mutation'))
            if mutate is None:
                report(False, '%s: mutation %s is defined' % (reg['id'], guard.get('mutation')))
                continue
            copy_ = copy.deepcopy(fixture)
            mutate(copy_)
            mutated = recompute(copy_, plan)[0]
            verdict = {s['id']: s['passed'] for s in mutated}
            report(verdict.get(guard['fails']) is False, '%s: reintroduced (%s), %s fails' % (reg['id'], guard['mutation'], guard['fails']))
    print('land missions regressions: %d checks, %d failed' % (len(lines), failed))
    for line in lines:
        print('   ' + line)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
