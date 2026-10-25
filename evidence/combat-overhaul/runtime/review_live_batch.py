"""Review retained native probe observations; never infer pass from trial names."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def load(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]

def combat(run):
    path = ROOT / 'combat' / f'run-{run}.jsonl'
    events = load(path)
    trials = []
    for end in (e['data'] for e in events if e['name'] == 'trial_end'):
        name, attack = end['Trial'], end['AttackId']
        hits = [e['data'] for e in events if e['name'] == 'agent_hit' and e['data']['Trial'] == name]
        positive = [h for h in hits if h['damage'] > 0]
        victims = sorted({h['victim'] for h in positive})
        policies = [e['data'] for e in events if e['name'] == 'native_defense_policy' and e['data']['Trial'] == name]
        contacts = [e['data'] for e in events if e['name'] == 'contact_enter' and e['data']['Trial'] == name]
        trials.append(dict(trial=name, attack=attack, hp_loss=end['targets'],
            positive_damage_victims=victims,
            positive_events_per_victim={str(v):sum(h['victim']==v for h in positive) for v in victims},
            zero_damage_events=sum(h['damage']==0 for h in hits),
            real_shield_contacts=sum(c['shield'] for c in contacts),
            native_defense_policy_calls=len(policies),
            correct_side_values=sorted({c['correctSideShieldBlock'] for c in contacts if 'correctSideShieldBlock' in c})))
    warnings = [e for e in events if e['name'] in ('combat_error','unexpected_contact_progress_reset','unexpected_additional_release')]
    return dict(run=run, sha256=hashlib.sha256(path.read_bytes()).hexdigest(), trials=trials,
                warnings=warnings, clean_exit=any(e.get('data',{}).get('message')=='requested_clean_exit' for e in events))

report = dict(scope='Diagnostic foot-human stock sword only; no production/campaign XP acceptance.',
    combat=[combat(run) for run in (39720,21296,46400,52064)],
    conclusions=dict(original_custom_animation='proven in30840; see animation/run-30840-review.json',
        native_unguarded_cleave='three distinct positive-damage victims once each, both directions',
        shield_continuation='partial: correct-side left passes; wrong-side right fails even at1.65m',
        abilities='basic mouse route/lifecycle passed11956 after earlier40336 crash; Escape/controller incomplete',
        stability='52064 late crash unconfirmed cause; preserve full gate', bulk_gate='closed'))
(ROOT / 'live-batch-review.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report['conclusions'],indent=2))
