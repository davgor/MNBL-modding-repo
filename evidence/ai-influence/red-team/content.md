# Content lens: calibration artifacts and provenance

Target: immutable epic revision 2, `build/research-ai-influence/red-team-plan.json`; comparison source FWGC++ commit `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`. Review date 2026-10-25. FWGC++ working tree remained clean. Raw training data is ignored local output, not committed by that SHA; its counts describe the files inspected, not a reproducible checkout alone.

## C3 — Require representative production-exported compression data

MMR-327 records QAT/calibration sources and tool/history preservation, but has no acceptance requirement checking coverage of the actual data supplied to compression. MMR-325's broad evaluation coverage is already present; that does not establish calibration/training coverage. A large corpus can still mostly teach greetings while missing rare but consequential political, magic, refusal and commitment states.

FWGC++ provides a useful positive pattern and a warning. `training/AGENTS.md:20` requires pairs from the production `Voice::messages` exporter rather than recreated prompts. `training/make_pairs.py:182` calls `communicator_eval --export-pairs`; its header explains that unsuitable scripts are dropped. The existing saved corpus contains 6,524 training rows and 423 validation rows. Training includes 25 `accept_quest` and 17 `decline_quest` examples; validation has two and one respectively. These are voice-training examples, not an action-training benchmark. `training/README.md:84` describes lost recall behavior and missing bystander/repeated-calming examples. This does not show that the original corpus was wrong for its stated task; it shows why importing it as sufficient evidence for the much broader new design would be unjustified.

### Proposed acceptance patch

Append to MMR-327: “Generate calibration/QAT and optional task-tuning records through the production serializers/exporters; pin schema, prompt/template and source-world revisions. Preserve role/output markers and log truncation or malformed-record exclusions. Publish coverage by inference role, tool/action family, success/refusal/repair state, history horizon, secrecy and rare obligations, with preregistered minimum coverage or explicit justified exclusions. Record rejected/dropped exports and audit retained records against source packets/events. Keep tuning/calibration separate from held-out histories and do not treat the legacy FWGC++ voice corpus as political/magic/tool coverage.”

This sharpens an existing ticket rather than adding a third sprint or making optional fine-tuning mandatory. Compression recipes may use different corpus designs; their coverage must be visible and justified.

## Independent artifact verification

Independently checked the SDET split claim using Python JSON parsing, then repeated using the owning `training/train_lora.py:27` `load_pairs` function extracted unchanged from its AST. AST extraction avoids importing PyTorch/PEFT or starting training. Results:

- Training: 6,524 records, worlds 101–114; validation: 423 records, world 115; no world overlap.
- Positive control: first training row is world 101, script `w101_townsfolk0`, act `greet`; first validation world is 115.
- All 6,947 combined records join to their named `training/data/scripts/<script>.jsonl` output after removing only aggregation metadata `script` and `world`: zero missing script files and zero record mismatches, across 750 scripts. JSON object key order is ignored; values are not normalized.
- Negative control: a fabricated `{"act":"not-real"}` record is absent from the first script's record set.

The reader definition used is exactly `return [json.loads(line) for line in open(path, encoding="utf-8")]`. Reproduction from FWGC++ root: use Python `ast.parse(Path('training/train_lora.py').read_text())`, select the `FunctionDef` named `load_pairs`, compile that unchanged node with `json` in its namespace, and call it on `pairs_train.jsonl`, `pairs_val.jsonl`, and each row's named script JSONL. Compare JSON objects after removing the two metadata keys. For distribution, count the actual `act` field with `collections.Counter`, never name substrings. No models, builds or training commands were run.

Exact bounded count/positive-control command (PowerShell, FWGC++ working directory):

```powershell
python -c 'import ast,json,pathlib; p=pathlib.Path("training"); t=ast.parse((p/"train_lora.py").read_text()); f=next(n for n in t.body if isinstance(n,ast.FunctionDef) and n.name=="load_pairs"); ns={"json":json}; exec(compile(ast.Module(body=[f],type_ignores=[]),"training/train_lora.py","exec"),ns); sets={n:ns["load_pairs"](p/"data"/("pairs_"+n+".jsonl")) for n in ("train","val")}; print([(n,len(v),sorted(set(r["world"] for r in v)),v[0]["world"]) for n,v in sets.items()])'
```

## Adversarial review and limitations

The 44/59 tuned versus 27/59 base blind scores are historical README claims, not results reproduced by this review. Raw blind judgements have not been established. The raw corpus predates current contract evolution; it is not a claim about current model quality or every current dialogue case. Record membership validates export provenance within these local files; it does not prove every utterance true, uniqueness of examples, held-out semantic independence, or that artifacts were generated by the currently pinned binaries. Those limitations prevent claiming a comprehensive model evaluation.

No claim is made that 42 consequential examples is universally too few, that specific LittleBit recipes fail, or that the corpus must be uniformly balanced. The finding is a missing measurable corpus-coverage requirement, not a retrospectively invented threshold.

## Dropped and routed findings

- A missing train/validation world split was refuted: raw worlds are disjoint and the plan already requires separate tuning/calibration/dev/held-out seeds and histories.
- Broken local export joins were refuted: all inspected rows match their source script artifacts.
- Blind dialogue quality, authored/task-tuned comparators and separate role regression are owned by SDET; no duplicate finding here.
- Pre-display truth checks and admissible tool menus are owned by code; not repeated here.

Index line: content — C3 production-exported compression corpus coverage and rejection ledger; independent raw split and 6,947-record provenance join verified.


