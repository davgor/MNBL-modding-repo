"""Read-only verification using the owning loader without importing training dependencies."""
import ast
import json
import hashlib
from pathlib import Path

root = Path('C:/Users/davgo/Documents/GitHub/FWGC++')
tree = ast.parse((root / 'training/train_lora.py').read_text(encoding='utf-8'))
loader = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'load_pairs')
namespace = {'json': json}
exec(compile(ast.Module(body=[loader], type_ignores=[]), str(root / 'training/train_lora.py'), 'exec'), namespace)
train = namespace['load_pairs'](root / 'training/data/pairs_train.jsonl')
val = namespace['load_pairs'](root / 'training/data/pairs_val.jsonl')
assert train[0]['world'] == 101 and val[0]['world'] == 115
assert train[0]['act'] == 'greet' and train[0]['outcome'] == 'greeted'
assert train[0]['world'] != 115
result = {'train_rows': len(train), 'validation_rows': len(val),
          'train_worlds': sorted({r['world'] for r in train}),
          'validation_worlds': sorted({r['world'] for r in val}),
          'train_act_counts': {a: sum(r['act'] == a for r in train) for a in sorted({r['act'] for r in train})},
          'controls_passed': True}
assert set(result['train_worlds']).isdisjoint(result['validation_worlds'])
canonical = lambda row: json.dumps(row, sort_keys=True, ensure_ascii=False)
scripts = {}
missing = mismatches = 0
for row in train + val:
    name = row['script']
    path = root / 'training/data/scripts' / (name + '.jsonl')
    if not path.exists():
        missing += 1
        continue
    if name not in scripts:
        scripts[name] = {canonical(r) for r in namespace['load_pairs'](path)}
    emitted = {k: v for k, v in row.items() if k not in ('script', 'world')}
    mismatches += canonical(emitted) not in scripts[name]
assert canonical({'act': 'not-real'}) not in scripts[train[0]['script']]
result.update(script_count=len(scripts), missing_scripts=missing, mismatched_rows=mismatches)
result['sha256'] = {name: hashlib.sha256((root / 'training/data' / name).read_bytes()).hexdigest()
                    for name in ('pairs_train.jsonl', 'pairs_val.jsonl')}
print(json.dumps(result, indent=2))
