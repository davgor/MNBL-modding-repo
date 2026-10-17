"""MMR-44: summarize the generation_job probe run and the offline job tests into evidence/generation/generation-job.json.

usage: python tools/summarize_generation_job.py --run evidence/runtime/in-game/generation_job-<pid>.json [--run ...]
           --offline evidence/generation/job-state-machine-tests.json --schema worldgen/contracts/progress-event.schema.json
           --out evidence/generation/generation-job.json

Every check is recomputed from what the run recorded (each job's phases, events, timings, cleanup, process counts and affinity
counters), not copied from the probe's own verdicts alone. The last --run is the accepted one; earlier runs are listed for the record.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys

PIPELINE = ["ValidatingRequest", "GeneratingSource", "Compiling", "ValidatingData", "BuildingScene", "RegisteringCampaign", "ValidatingRuntime", "ReadyForReview"]
GROUPS = {"ValidatingRequest": "source", "GeneratingSource": "source", "Compiling": "compilation", "ValidatingData": "compilation",
          "BuildingScene": "engine", "RegisteringCampaign": "engine", "ValidatingRuntime": "engine"}


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", action="append", required=True)
    ap.add_argument("--offline", required=True)
    ap.add_argument("--schema", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    runs = [(p, load(p)) for p in a.run]
    path, run = runs[-1]
    offline, schema = load(a.offline), load(a.schema)
    o = run["outputs"]
    jobs = {j["label"]: j for j in o["jobs"]}
    checks = []

    def check(ok, name, detail=""):
        checks.append({"name": name, "passed": bool(ok), "detail": detail})

    check(run.get("status") == "ok" and o.get("completed") is True and o.get("failures") == [],
          "the probe completed every step without a failure", "status %s, failures %s" % (run.get("status"), o.get("failures")))

    full = jobs["full pipeline"]
    timings = {t["phase"]: t["seconds"] for t in full["timings"]}
    check(full["phase"] == "ReadyForReview" and full["phases"] == PIPELINE and all(timings.get(p) is not None for p in PIPELINE[:-1]),
          "c1: progress covers the whole pipeline in order, each phase timed", " > ".join("%s %.1fs" % (p, timings.get(p) or 0) for p in PIPELINE[:-1]))
    codes = full["messageCodes"]
    check(any(c.startswith("attempt_") for c in codes) and "bundle_published" in codes and "navigation_verified" in codes and "registration_at_campaign_start" in codes,
          "c1: the events say what each phase did (attempts, bundle, navigation) and that campaign registration happens at campaign start", ", ".join(codes))
    worst = max(j["maxFrameGapSeconds"] for j in o["jobs"])
    check(worst < 0.5 and full["events"] == full["eventsLog"] and full["droppedStale"] == 0 and full["droppedOutOfOrder"] == 0 and o["scheduler"]["longestItemSeconds"] < 0.25,
          "c1: the game kept responding in every scenario (largest frame gap %.3f s, under 0.5 s; longest engine item %.3f s); every event in order and logged" % (worst, o["scheduler"]["longestItemSeconds"]),
          "%d events" % full["events"])

    expected = {"cancel during source generation": "source", "cancel during compilation": "compilation",
                "cancel with the scene install queued": "engine", "cancel after the inspection world was constructed": "engine"}
    for label, group in expected.items():
        j = jobs[label]
        trig = j.get("triggeredAt")
        check(j["phase"] == "Cancelled" and GROUPS.get(trig) == group and j.get("cancelAccepted") is True and j["generatorProcessesAfter"] == 0
              and j["jobDirectoryReleased"] is True and j["owned"] == [] and j.get("bundleReleased", True) is True,
              "c2: %s (%s group, at %s): Cancelled, own resources released, no generator process, files gone" % (label, group, trig), "; ".join(j["cleanup"]))
    check(jobs["cancel with the scene install queued"].get("installNeverRan") is True and jobs["cancel with the scene install queued"].get("withdrawnOrDropped", 0) >= 1,
          "c2/c3: the queued scene install of the cancelled job never ran (withdrawn or dropped)")
    check("released inspection world" in jobs["cancel after the inspection world was constructed"]["cleanup"],
          "c2: the constructed inspection world was torn down on the engine thread")
    crash = jobs["producer crash"]
    check(crash["phase"] == "Failed" and crash["errorCategory"] == "BadSource" and crash.get("killedProducers") and crash["generatorProcessesAfter"] == 0 and crash["jobDirectoryReleased"] is True,
          "c2: a killed producer fails the job as a bad source (%s); no generator process left" % crash.get("errorCode"), str(crash.get("failureDetail"))[:300])
    source = jobs["cancel during source generation"]
    check(source.get("requestSeedSource") == "random", "a Random World seed reaches the request as seedSource random")

    late = o["lateCallback"]
    check(late["posted"] is True and late["ran"] is False and late["dropped"] is True, "c3: a cancelled job's late engine callback, posted while a later job ran, was dropped unrun")
    sched = o["scheduler"]
    check(sched["affinityViolations"] == 0 and all(j["stageOnEngineThread"] == 0 and j["engineItemOffEngineThread"] == 0 for j in o["jobs"]),
          "c3: thread affinity: no stage on the engine thread, every engine item on it", "longest engine item %.3f s (%s)" % (sched["longestItemSeconds"], sched["longestItem"]))
    check(o["finalTopScreen"].endswith("GauntletInitialScreen") and o["generatorProcessesAfter"] == 0 and o["bundleStagingLeft"] == 0,
          "c2: at the end the main menu is usable, no generator process runs and no bundle staging is left")
    check(offline.get("failures") == 0 and offline.get("checks", 0) >= 16, "the offline job tests pass", "%s checks" % offline.get("checks"))
    enum = set(schema["properties"]["phase"]["enum"])
    check(set(PIPELINE) <= enum and {"Cancelled", "Failed", "Committing", "Playing"} <= enum and schema["properties"]["protocolVersion"]["const"] == 1,
          "the progress-event contract names every phase and protocol version 1")

    evidence = {
        "schema": "mnbl.evidence.generation-job-summary", "version": 1, "ticket": "MMR-44",
        "run": {"path": path.replace("\\", "/"), "sha256": sha(path), "pid": run["processId"], "moduleSha256": run.get("moduleSha256"), "utc": run.get("utc")},
        "earlierRuns": [{"path": p.replace("\\", "/"), "pid": r["processId"], "status": r.get("status"), "failures": r["outputs"].get("failures"), "moduleSha256": r.get("moduleSha256")} for p, r in runs[:-1]],
        "offline": {"path": a.offline.replace("\\", "/"), "sha256": sha(a.offline), "checks": offline.get("checks"), "failures": offline.get("failures")},
        "pipeline": {"seconds": full["seconds"], "phaseSeconds": timings, "maxFrameGapSeconds": full["maxFrameGapSeconds"]},
        "jobs": [{k: j.get(k) for k in ("label", "phase", "triggeredAt", "seconds", "maxFrameGapSeconds", "errorCategory", "errorCode", "cleanup", "generatorProcessesAfter")} for j in o["jobs"]],
        "limits": [
            "Campaign registration, commit and play are MMR-45 (R07: registration inside the new game); RegisteringCampaign reports that and does nothing.",
            "The preview and its Start Campaign action are MMR-46; jobs stop at ReadyForReview.",
            "Engine items are single native calls: navigation validation holds the engine thread up to about 0.7 s.",
            "The producer crash is a killed fwg process, not a natural crash.",
        ],
        "checks": checks, "passed": all(c["passed"] for c in checks),
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
