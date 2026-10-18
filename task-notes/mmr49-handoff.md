# MMR-49 (accepted 2026-10-18 02:26 UTC)

Accepted: attempt `at_01m54zy8dmgs8xkmanykbpdy8x`, create run 48048 (37/37) and cold run 39860 (20/20). The summary is `evidence/persistence/two-world-matrix.json`; the review, confirming runs and limits are in `evidence/fwg/world-loading-review.md`; the design is in `docs/world-loading.md`.

Carried-forward limits:
- World B is MMR-19's fixture world, not a second compiled world (production seed yield).
- `ReturnToMenu`, the failure-after-start path, is not exercised in game.
- The deferred re-encode behind a running campaign is not exercised in game.

Leftover probe saves from earlier tickets (`mnbl_1609fb72_start_*` and `mnbl_mmr19_*`) remain in the game's save folder. They were not created by MMR-49's accepted runs, which deleted their own.
