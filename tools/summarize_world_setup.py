"""MMR-43: summarize the world_setup probe runs and the offline setup tests into evidence/runtime/world-setup.json.

usage: python tools/summarize_world_setup.py --run evidence/runtime/in-game/world_setup-<pid>.json [--run ...]
           --offline <world-setup-tests.json from tools/Test-WorldSetup.ps1 -Evidence> --budget worldgen/policies/one-shot-budget.json
           --out evidence/runtime/world-setup.json

Every check is recomputed from what the run recorded (the screen's texts, states, timings and the files and processes it left), not
copied from the probe's own verdicts alone. The last --run is the accepted one; earlier runs are listed with their status for the record.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys

HASH = re.compile(r"\b[0-9a-f]{64}\b")
PATH = re.compile(r"[A-Za-z]:\\|/jobs/|\\jobs\\")


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def stated_seconds(budget, phase):
    p = budget["phases"].get(phase)
    if p is None:
        return None
    step = max(1, budget["roundToSeconds"])
    return int(math.ceil(max(p["seconds"]) / step) * step)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", action="append", required=True)
    ap.add_argument("--offline", required=True)
    ap.add_argument("--budget", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    runs = [(p, load(p)) for p in a.run]
    path, run = runs[-1]
    offline, budget = load(a.offline), load(a.budget)
    o = run["outputs"]
    checks = []

    def check(ok, name, detail=""):
        checks.append({"name": name, "passed": bool(ok), "detail": detail})

    def player_text(*texts):
        return all(isinstance(t, str) and "{=" not in t and not HASH.search(t) and not PATH.search(t) for t in texts)

    check(run.get("status") == "ok" and o.get("completed") is True and o.get("failures") == [],
          "the probe completed every step without a failure", "status %s, failures %s" % (run.get("status"), o.get("failures")))

    menu = o["menu"]
    check(menu["order"] == menu["sandboxOrder"] + 1 and menu["disabled"] is False and menu["host"] == [] and menu["entry"] == "New Procedural Campaign",
          "New Procedural Campaign is on the main menu directly after SandBox and enabled on this host", "; ".join(menu["entries"][:5]))

    opened = o["opened"]
    check(opened["movieLoaded"] is True and opened["topScreen"].endswith("WorldSetupScreen") and opened["gameExists"] is False and o["generated"]["gameExists"] is False,
          "executing the entry pushes the setup screen with its movie; no game exists while it is open or after generation (nothing registered)")
    check(player_text(opened["title"], opened["coverage"], opened["profile"], opened["seedHint"]) and "no region to choose" in opened["coverage"],
          "profile and full-globe coverage are fixed player text (localised, no id, hash or path)", opened["coverage"])

    seeds = o["seeds"]
    accepted = {s["text"] for s in seeds if s["canGenerate"]}
    rejected = [s for s in seeds if not s["canGenerate"]]
    check(accepted == {"4294967295", "0", str(run["args"].get("seed", 42))} and all(s["error"] and player_text(s["error"]) for s in rejected)
          and all(s["error"] == "" for s in seeds if s["canGenerate"]),
          "the seed field accepts exactly the valid seeds; every rejected text shows its reason and blocks Generate",
          "; ".join("'%s' %s" % (s["text"], s["problem"]) for s in seeds))

    rw = o["randomWorld"]
    check(rw["button"] != rw["afterKey"] and rw["typingKey"] == "None" and rw["key"] == "RandomWorld" and rw["source"] == "random" and rw["canGenerate"] is True
          and all(0 <= int(rw[k]) <= 4294967295 for k in ("button", "afterKey")),
          "Random World (button, and the Randomize key unless the seed box is being typed in) draws a new valid seed each time",
          "%s, %s; key glyph %s" % (rw["button"], rw["afterKey"], rw.get("keyGlyph")))

    settings = o["settings"]
    check([s["name"] for s in settings] == ["World size", "Generation stages"] and all(s["locked"] and player_text(s["reason"]) for s in settings)
          and "different world" in settings[0]["reason"],
          "only the profile's two producer controls appear, each locked with why; size is explained as content, not quality",
          "; ".join("%s %s" % (s["name"], s["value"]) for s in settings))

    gen = stated_seconds(budget, "generateWorld")
    keys = o["keys"]
    # MMR-56 added the first campaign start (startCampaign, stated in whole minutes rounded up); a run on a build whose budget had none shows
    # no start sentence.
    start = budget["phases"].get("startCampaign")
    minutes = None if start is None else max(1, int(math.ceil(max(start["seconds"]) / 60.0)))
    check(("about %d seconds" % gen) in o["waitText"] and set(budget["phases"]) <= {"generateWorld", "startCampaign"}
          and (("about %d minutes" % minutes) in o["waitText"] if minutes else "campaign" not in o["waitText"]),
          "the waiting time is the budget's largest generation measurement rounded up (%d s)%s" % (gen, "" if minutes is None else ", and its first campaign start's in whole minutes (%d)" % minutes), o["waitText"])
    check(keys["generationSeconds"] <= gen, "the generation finished within the stated waiting time", "%.1f s of %d s" % (keys["generationSeconds"], gen))
    check(keys["maxFrameGapSeconds"] < 0.5, "the engine thread kept ticking while generating (largest frame gap under 0.5 s)", "largest frame gap %.3f s" % keys["maxFrameGapSeconds"])

    check(keys["exitIdle"] == "Back" and keys["afterExit"].endswith("GauntletInitialScreen") and keys["confirm"] == "Generate" and len(keys["stages"]) >= 2,
          "Exit leaves to the main menu; Confirm generates, with progress stages", " > ".join(keys["stages"]))

    g = o["generated"]
    fields = g["requestFields"]
    names = [p.rsplit(" ", 2)[0] for p in g["peopleNames"]]
    check(g["state"] == "Generated" and str(fields["masterSeed"]) in g["resultText"] and all(n in g["peoples"] for n in names) and len(g["cultures"]) > 0
          and player_text(g["resultText"], g["peoples"], g["creation"], g["startUnavailable"]),
          "the generated world's own peoples are named with the culture each is shown as, and the cultures character creation offers; no id, hash or path in the ordinary flow",
          g["resultText"] + " " + g["peoples"] + " " + g["creation"])
    check(fields["recipeVersion"] == 3 and fields["overrides"] == {"phase": 16, "size": 129} and fields["maxAttempts"] == 3 and fields["seedSource"] == "explicit"
          and fields["producerLockSha256"] == sha("worldgen/producer.lock.json"),
          "the job received the shown choice as generation request fields: the F03 one-shot profile, the typed seed, the producer lock's sha256", json.dumps(fields, sort_keys=True))
    check(g["diagnosticsShown"] is False and "producer lock" in g["diagnosticsWhenShown"] and "job " in g["diagnosticsWhenShown"],
          "diagnostics (job, hashes) are hidden until asked for")

    c = o["cancel"]
    check(c["exitWhileGenerating"] == "Back" and c["screenStayed"] is True and c["category"] == "InterruptedGeneration" and c["generatorProcesses"] == 0
          and player_text(c["errorTitle"], c["errorAction"]),
          "Exit while generating cancels: the interrupted-generation category, the screen stays, no generator process left", c["errorTitle"])
    check(c["firstJobReleased"] is True and c["cancelledJobReleasedOnClose"] is True and o["setupJobsLeft"] == 0,
          "job files are released: the previous job on a new Generate, the last when the screen closes; none left")

    m = o["missingWarSails"]
    check(m["entryDisabled"] is True and m["entryReason"] and m["canGenerate"] is False and m["generating"] is False and player_text(m["entryReason"], m["hostProblem"]),
          "without War Sails the entry and Generate are refused with the missing-DLC category's text", m["hostProblem"])
    check(o["finalTopScreen"].endswith("GauntletInitialScreen"), "the main menu is usable at the end")

    check(offline.get("failures") == 0 and offline.get("checks", 0) >= 20, "the offline setup tests pass", "%s checks" % offline.get("checks"))

    shots = {k: o[k] for k in ("setupScreenshot", "generatedScreenshot", "diagnosticsScreenshot", "cancelScreenshot", "missingWarSailsScreenshot")}
    evidence = {
        "schema": "mnbl.evidence.world-setup-summary",
        "version": 1,
        "ticket": "MMR-43",
        "run": {"path": path.replace("\\", "/"), "sha256": sha(path), "pid": run["processId"], "moduleSha256": run.get("moduleSha256"), "utc": run.get("utc")},
        "earlierRuns": [{"path": p.replace("\\", "/"), "pid": r["processId"], "status": r.get("status"), "failures": r["outputs"].get("failures"), "moduleSha256": r.get("moduleSha256")} for p, r in runs[:-1]],
        "offline": {"path": a.offline.replace("\\", "/"), "sha256": sha(a.offline), "checks": offline.get("checks"), "failures": offline.get("failures"), "worlds": offline.get("worlds")},
        "budget": {"status": budget["status"], "generateSeconds": gen, "sha256": sha(a.budget)},
        "generation": {"seconds": keys["generationSeconds"], "maxFrameGapSeconds": keys["maxFrameGapSeconds"]},
        "screenshots": {k: v.replace("\\", "/") for k, v in shots.items()},
        "limits": [
            "Physical keyboard and controller devices are not driven: the probe feeds synthetic GenericPanelGameKeyCategory key states through the screen's own mapping.",
            "No campaign start: the hand-off to GeneratedGameManager and generated-culture character creation is MMR-45/46. The cultures the setup names are computed from the source world and the presentation policy; they equal the compiled campaigns' presentations on the two tested bundles (offline), not proven for every world.",
            "The interim runner is the R01 generation (no transaction, no quality retries); MMR-44 replaces it.",
            "Waiting times are provisional (MMR-56).",
        ],
        "checks": checks,
        "passed": all(c["passed"] for c in checks),
    }
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(evidence, f, indent=1, ensure_ascii=False)
        f.write("\n")
    for c in checks:
        print(("ok    " if c["passed"] else "FAIL  ") + c["name"])
    print("%d/%d checks passed" % (sum(c["passed"] for c in checks), len(checks)))
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
