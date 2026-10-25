# SDET review — AI Influence plan versus FWGC++ experiments

Reviewed 2026-10-25. Plan snapshot: `build/research-ai-influence/red-team-plan.json`, saved revision 2, hash `59cddddc2183487bb66984f720f3470d80fa611ae4b677c20e2c9c68948a3280`. FWGC++: `0ae1f3ed57161ed43d9b8b1ec0827f0345a87059`, clean at review. This is a plan review; no inference, training or product test suite was run.

## SDET-1: Correct tool/state scores do not establish good conversation

MMR-325 explicitly scores retrieval, temporal consistency, leakage, obligations, decisions, tool arguments, multistep outcomes and state integrity. It does not require independent blinded assessment of whether a reply actually engages the player, remains in character, repeats itself or fits the moment. MMR-326 requires model comparisons but no authored dialogue or task-tuned small-model comparator.

FWGC++'s experiment documents show why that distinction matters. `training/README.md:66-72` reports every one of 59 replies conforming to shape for both base and tuned models, while a blind judge accepted only 27 base and 44 tuned replies. `communicator/eval/README.md:410-426` reports the tuned model passing the truth check on all 24 turns but only 10 being fault-free; a later six-session comparison reports 46/127 fault-free with conversation, 43/127 without, versus 85/127 authored. These are historical README reports, not raw judgements reproduced in this review. They do not establish today's model scores. They do establish a documented experimental warning against treating automatic validity as player quality.

Proposed MMR-325 acceptance: preregister and archive blind, order-randomized multi-visit dialogue judging, independent of the runtime checker and candidate identity. Score sense, engagement/dodging, memory, repetition, invented facts, character and tone separately; keep per-turn evidence, judge instructions, disagreements and adjudication. Freeze floors for player quality alongside state correctness, and include delayed returns and emotionally consequential outcomes. Repeated judges should quantify judgement variability rather than treating a single rating as exact.

Proposed MMR-326 acceptance: include an authored/fallback dialogue comparator and a task-adapted small-model lane alongside untuned parents and LittleBit derivatives, where feasible; use the same packets, histories and runtime settings and report any baseline that is unavailable. This is an additional comparator, not a substitute for the required heavy LittleBit lane. Keep matched untuned/tuned compression comparisons so adaptation gains are not attributed to compression.

Guard that would catch this: a frozen transcript suite includes schema-valid and state-safe replies that dodge specific questions or repeat an introduction; the independent judge must reject those while accepting an engaging, grounded control. No new executable test was built because MNBL's future dialogue/evaluation implementation does not exist; a keyword check on this plan would not test the claimed behavior.

## SDET-2: Shared-model gains need separate role regression gates

MMR-327 pairs parent/derivative trials but does not explicitly require that every role sharing the changed weights retain its own acceptance floor. A stronger aggregate or voice score can hide worse intent interpretation, summarization or reaction selection.

`training/README.md:64-79` reports better fully correct interpretation and much better judged voice after tuning, but unsafe reads increasing from 1 to 2. The documented witness is "ok" interpreted as taking work even though no work had just been offered; confirmation contained the mutation. `training/AGENTS.md:27-30` therefore explicitly requires measuring both interpreter and voice against equally quantized base weights. `communicator/AGENTS.md` also requires before/after held-out reaction and summary evaluation for changes to those prompts.

Proposed MMR-325/327 acceptance: maintain an inventory of model roles (intent/tool choice, voice, summarization/memory extraction, reaction/initiative, and embedding retrieval if used). For each shared checkpoint, prompt, chat-template, tuning or compression change, rerun applicable held-out role suites with independent per-role regression budgets. Report unintended consequential actions and unnecessary confirmation requests separately from aggregate success; score summarization omissions/invention and reaction admissibility separately. Unsupported roles must be explicit rather than silently excluded. Unacceptable regression in one role cannot be traded against better prose elsewhere.

Guard that would catch this: include short ambiguous utterances with and without an outstanding offer and verify intended action plus confirmation frequency; include positive examples that should perform the action. For memory, use the same claim backed and unbacked by a packet. FWGC++ `communicator/tests/test_truth.cpp:199-207` demonstrates that paired control with "As I told you some weeks ago". No MNBL runtime test was authored for unimplemented roles.

## Evidence execution and controls

Executed from FWGC++:

```powershell
git status --short
git rev-parse HEAD
rg -n 'blind|judge|59|fault-free|truth|held.out' communicator/eval/README.md training/README.md
rg --files --hidden --no-ignore -g '*.txt' -g '*.json' -g '*.jsonl' -g '*.md' -g '*.log' -g '!**/.venv/**' -g '!**/node_modules/**' -g '!**/.git/**' -g '!**/_deps/**' -g '!**/models/**' | Select-String 'judge|blind|tuned|base|voice|round'
$train = Get-Content training/data/pairs_train.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
$val = Get-Content training/data/pairs_val.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
[pscustomobject]@{train=$train.Count; val=$val.Count; trainWorlds=($train.world | Sort-Object -Unique) -join ','; valWorlds=($val.world | Sort-Object -Unique) -join ','; firstTrainWorld=$train[0].world; firstValWorld=$val[0].world}
```

The raw pair files contain 6,524 training rows from worlds 101–114 and 423 validation rows from world 115. First-row schema/value controls returned 101 and 115; parsed world sets are disjoint. This validates that specific stored split, not the absence of every possible leakage route. No historical blind judgement files were found by the bounded inventory above. The current voice fixture is revised; it was not counted as a reconstruction of the old 59-case run. The experiment's own C++ row reader was inspected at `communicator/eval/main.cpp:202-225`; no substitute parser was used to infer historical fixture totals.

## Adversarial review and limitations

These are missing acceptance requirements, not proof that future implementation will fail. Existing plan already requires independent state/knowledge oracles, held-out seeds and histories, frozen scoring, uncertainty, raw trials, exact serving settings and raw-versus-constrained results. Claims that these are missing were dropped. No evidence establishes that heavier models are worse, that graph grounding fails, or that the reported FWGC++ historical counts can be reproduced on today's build. The unavailable raw blind records prevent independently verifying 44/59 versus 27/59. One initial relative-path inspection ran from MNBL and returned missing FWGC++ paths; it was corrected to the explicit FWGC++ working directory and no finding relies on that failure.

Index: SDET — add blinded multi-visit dialogue quality with authored/tuned comparators, and per-role regression gates; historical experiment outcomes are documented but raw judgements are unavailable.
