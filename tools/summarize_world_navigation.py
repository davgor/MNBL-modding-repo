"""MMR-38: summarize the world_navigation probe runs and the offline navigation contract into evidence/world/navigation-routes.json.

usage: python tools/summarize_world_navigation.py --run evidence/runtime/in-game/world_navigation-<pid>.json
           [--run2 evidence/runtime/in-game/world_navigation-<pid2>.json] --offline evidence/world/navigation-contract.json
           --out evidence/world/navigation-routes.json

Every check is recomputed here from the run's reports (the engine answers), not copied from the probe's own verdicts alone. --run2, a
second process on the same module, shows the semantic reports are stable across processes.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import sys

MMR17 = ["landAroundWall_Default", "landIntoClosedRing_Default", "landIntoClosedRing_All", "landAroundLake_Default", "lakeShoreToLake_All",
         "seaToEnclosedLake_Naval", "landToLake_Default", "seaThroughChannel_Naval", "landToOtherLandmass_Default", "landToOtherLandmass_All"]
BRIDGES = ["bridge1Crossing_Default", "bridge1Under_Naval", "bridge2Crossing_Default", "bridge2Under_Naval", "bridge3Crossing_Default",
           "bridge3Under_Naval", "riverNoShortcut_Default", "riverFromSea_Naval"]
TILES = {"landAcrossTileRow_Default": "y=800", "landAcrossTileColumn_Default": "x=800", "seaAcrossTileRow_Naval": "y=800",
         "seaAcrossTileColumn_Naval": "x=800", "landAcrossSeam_Default": "x=1200", "seaAcrossSeam_Naval": "x=1200"}
STEPS = {"fixture", "fixture (engine fault: Bridge faces disabled)", "A", "A (engine fault: Bridge faces disabled)", "B", "A (warm)"}


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--run2")
    ap.add_argument("--offline", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    run = json.load(open(a.run, encoding="utf-8"))
    run2 = json.load(open(a.run2, encoding="utf-8")) if a.run2 else None
    offline = json.load(open(a.offline, encoding="utf-8"))
    o = run["outputs"]
    checks = []

    def check(ok, name, detail=""):
        checks.append({"name": name, "passed": bool(ok), "detail": detail})

    worlds = {w["label"]: w for w in o.get("worlds", [])}
    check(run.get("status") == "ok" and o.get("completed") is True and o.get("jobsLeft") == 0 and set(worlds) == STEPS,
          "the probe completed every step and left no staging job", "status %s, worlds %s" % (run.get("status"), sorted(worlds)))
    check(o.get("modelEqualsCalibration") is True, "in game, the installed War Sails navigation model equals the MMR-17 campaign calibration",
          json.dumps(o.get("model", {}).get("invalid", {})))

    # Static refusals.
    faults = {f["variant"]: f for f in o.get("staticFaults", [])}
    bridge_subjects = sorted({s for b in worlds["A"]["build"]["bridges"] for s in b["subjects"]})
    snap_ok = all(faults.get(v, {}).get("refused") and not faults[v].get("installed") and len(faults[v]["diagnostics"]) == 1
                  and faults[v]["diagnostics"][0]["code"] == "navigation.snap_refused" and any(i.startswith("mnbl_") for i in faults[v]["diagnostics"][0]["sourceIds"])
                  for v in ("fault:snap-sea", "fault:snap-component"))
    nb = faults.get("fault:no-bridges", {})
    nb_diag = nb.get("diagnostics") or []
    gnr = [d for d in nb_diag if d["code"] == "navigation.group_not_realized"]
    nb_ok = nb.get("refused") and not nb.get("installed") and gnr and all(d["sourceIds"] for d in gnr) \
        and all(any(d["code"] == "navigation.bridge_missing" and d["subject"] == s for d in nb_diag) for s in bridge_subjects)
    check(snap_ok and nb_ok, "invalid snaps and unrealized required routes refuse world A in Prepare, before anything is encoded or installed, naming source ids",
          "; ".join("%s: %s" % (v, ", ".join("%s %s %s" % (d["code"], d["subject"], d["sourceIds"]) for d in (f.get("diagnostics") or [])[:3])) for v, f in faults.items()))

    # Engine face tables and the navigation setup.
    for label, w in worlds.items():
        ft = w["report"]["engine"]["faceTable"]; setup = w["constructed"]["navigationSetup"]
        check(ft["engineFaces"] == ft["meshFaces"] == w["build"]["faces"] and ft["histogramEqual"] and ft["centresOutsideTheirMeshFace"] == 0 and w["build"]["faces"] <= 32768
              and setup["regionMapEqualsModel"] is True,
              "%s: the engine read the built navmesh face for face (count, TerrainType histogram, every face centre inside a built face of its type) with the installed region map" % label,
              "%d faces %s; land region %s" % (ft["engineFaces"], json.dumps(ft["engineByTerrainType"]), setup["landRegion"]))

    corner = {}
    for label in ("A", "B"):
        w = worlds[label]; r = w["report"]; routes = r["engine"]["routes"]; faces = r["engine"]["faces"]
        kinds = collections.Counter(x["check"] for x in routes)
        types = collections.Counter(x["type"] for x in routes)
        forbidden = [x for x in routes if x["expected"] == "forbidden"]
        back = sum(1 for x in routes if x["id"].endswith("(back)"))
        disagree = [x["id"] for x in routes if not x["agrees"]] + [x["id"] for x in faces if not x["agrees"]]
        check(w["activatable"] is True and w["gate"] == "activation allowed" and w.get("persisted") is True and not disagree and not r["failures"] and r["static"]["activatable"],
              "%s: the engine route matrix agrees with the intended topology: %d routes (%d forbidden or sanity, %d reverse), %d face checks; the report is persisted and the gate allows activation" % (label, len(routes), len(forbidden), back, len(faces)),
              "by check %s; by type %s; disagreements %s" % (dict(kinds), dict(types), disagree[:5]))
        required = {"villageRoute", "landRoute", "portFace", "portGate", "forbidden", "transition", "coastTransition", "sanity"} | ({"bridge"} if w["build"]["bridges"] else set())
        check(required <= set(kinds) and all(t in types for t in ("Default", "Naval", "All")) and back > 0 and forbidden and all(x["found"] is False for x in forbidden),
              "%s: the matrix covers land, sea and transition routes (ports and coasts) for every navigation capability (Default, Naval, All), allowed and forbidden, and no forbidden route found a path in any periodic image" % label,
              "kinds %s" % sorted(kinds))
        found = [x for x in routes if x["found"]]
        bounded = [x for x in found if x["expected"] == "reachable" and x["gating"]]
        check(bounded and all(x["meshShortestPath"] is not None and x["pathLength"] <= 1.25 * x["meshShortestPath"] + 0.5 * 6.25 for x in bounded),
              "%s: every expected route's engine path is within 1.25 x the mesh's own shortest path + half a cell (no detour, no wrong bridge)" % label,
              "%d routes; worst ratio %.3f" % (len(bounded), max(x["pathLength"] / x["meshShortestPath"] for x in bounded if x["meshShortestPath"])))
        shortcuts = [x["id"] for x in routes if x.get("samples") and ((x["type"] == "Default" and x["samples"]["offLand"]) or (x["type"] == "Naval" and x["samples"]["offSea"]))]
        check(not shortcuts, "%s: no sampled engine path leaves its domain (0.25-unit samples; no unintended shortcut)" % label, "%d sampled routes" % r["engine"]["sampledRoutes"])
        pg = [x for x in routes if x["id"].startswith("portGate distance")]
        tr = [x for x in routes if x["check"] in ("portFace", "seamTransition", "coastTransition")]
        groups = [x for x in routes if x["check"] == "transition"]
        check(pg and all(x["pathLength"] is not None and x["pathLength"] <= 12.5 and x["regionSwitches"] == 1 for x in pg) and all(x["regionSwitches"] == 1 for x in tr)
              and all(x["regionSwitches"] >= 2 for x in groups),
              "%s: region switches counted from the engine's cost: every port and coast transition and every port-to-gate path has exactly one, every All route between groups at least two; every port-to-gate path is within 12.5 units" % label,
              "max port-gate length %.3f over %d ports; %d transitions; %d group routes" % (max(x["pathLength"] for x in pg), len(pg), len(tr), len(groups)))
        br = [x for x in routes if x["check"] == "bridge"]
        check(all(x["agrees"] for x in br) and len([x for x in br if x["id"].startswith("bridge crossing")]) == 2 * sum(len(b["subjects"]) for b in w["build"]["bridges"]),
              "%s: every bridge crossing route crosses its deck's Bridge face, and every under-bridge route (where the water runs straight through a deck cell) crosses its UnderBridge face" % label,
              "%d crossing routes, %d under routes" % (len([x for x in br if x["id"].startswith("bridge crossing")]), len([x for x in br if x["id"].startswith("bridge under")])))
        check(r["engine"]["islands"]["memberIslands"] == 1 and not r["engine"]["islands"]["problems"],
              "%s: town gates, ports and kept castles lie on one engine island (MMR-28 islands; weak: the mesh is one connected piece, the members' All connectivity is the static check and the All routes)" % label, json.dumps(r["engine"]["islands"]))
        corner[label] = r["engine"]["cornerHops"]

    # The fixture.
    f = worlds["fixture"]; routes = {x["id"]: x for x in f["report"]["engine"]["routes"]}; faces = {x["id"]: x for x in f["report"]["engine"]["faces"]}
    gating_bad = [k for k, x in routes.items() if x["gating"] and not x["agrees"]] + [k for k, x in faces.items() if x["gating"] and not x["agrees"]]
    check(f["activatable"] is True and f["gate"] == "activation allowed" and not gating_bad, "fixture: every designed expectation agrees in the engine", "%d routes, %d faces" % (len(routes), len(faces)))
    mmr17 = [k for k in routes if k.split(" (")[0] in MMR17]
    check(all(any(k.startswith(n) for k in mmr17) for n in MMR17) and all(routes[k]["agrees"] for k in mmr17)
          and all(14 <= routes[k]["pathLength"] / 6.25 <= 30 for k in routes if k.startswith("landAroundWall_Default")),
          "fixture: the MMR-17 barrier, lake and narrow-channel cases, restated, pass on production navigation (a static mountain wall detours within [direct+4, 3*direct] cells, a closed ring refuses, the enclosed lake refuses the sea, the one-cell channel carries ships and refuses land)",
          "; ".join("%s %s %s" % (k, routes[k]["expected"], routes[k]["pathLength"]) for k in mmr17))
    br = [k for k in routes if k.split(" (")[0] in BRIDGES]
    deck_faces = [k for k in faces if k.startswith("deck ") or k.startswith("under ")]
    check(all(any(k.startswith(n) for k in br) for n in BRIDGES) and all(routes[k]["agrees"] for k in br) and len(deck_faces) == 8 and all(faces[k]["agrees"] for k in deck_faces),
          "fixture: bridges (one cell and two cells across x, one cell across y) carry land routes over their Bridge faces while ships pass under on their UnderBridge faces; a river is crossed only at a bridge",
          "; ".join("%s %s" % (k, routes[k]["pathLength"]) for k in br))
    tiles = {k: x for k, x in routes.items() if k.split(" (")[0] in TILES}
    tiles_ok = len(tiles) == 12 and all(x["agrees"] and x["found"] for x in tiles.values()) and all(
        (("x=400" if k.endswith("(back)") and "Seam" in k else TILES[k.split(" (")[0]]) in x["crosses"]) for k, x in tiles.items())
    check(tiles_ok, "fixture: land and sea routes cross the interior terrain chunk lines (x=800, y=800) and the periodic seam (x=400, x=1200) in both directions without detour (one lattice: no stitching)",
          "; ".join("%s %s" % (k, x["crosses"]) for k, x in tiles.items()))
    pad = [k for k in faces if k.startswith("beyond")]
    check(len(pad) == 6 and all(faces[k]["engine"] is None for k in pad) and faces["stripImageLand (cell 26,100)"]["agrees"] and faces["stripImageSea (cell 3,80)"]["agrees"],
          "fixture: no face beyond the pole lines (canonical and strip), strip images carry their canonical cell's terrain type")

    # Engine faults: refused in memory, never persisted.
    ff = worlds["fixture (engine fault: Bridge faces disabled)"]; ffr = {x["id"]: x for x in ff["report"]["engine"]["routes"]}
    crossings = [k for k in ffr if k.split(" (")[0] in ("bridge1Crossing_Default", "bridge2Crossing_Default", "bridge3Crossing_Default")]
    unders = [k for k in ffr if k.split(" (")[0] in ("bridge1Under_Naval", "bridge2Under_Naval", "bridge3Under_Naval")]
    check(ff["activatable"] is False and ff["gate"] == "activation refused" and ff.get("persisted") is False and crossings and all(not ffr[k]["found"] for k in crossings)
          and unders and all(ffr[k]["agrees"] for k in unders),
          "in game, with Bridge faces disabled in the fixture, every land crossing fails while every under-bridge sea route still passes (the layers are independent); the faulted report is refused and not persisted",
          "crossings %s; under %s" % ([ffr[k]["found"] for k in crossings], [ffr[k]["agrees"] for k in unders]))
    e = worlds["A (engine fault: Bridge faces disabled)"]
    ef = e["report"]["failures"]
    ok_routes = {x["id"] for x in worlds["A"]["report"]["engine"]["routes"] if x["agrees"]}
    gd = e.get("gateDiagnostics", [])
    check(e["activatable"] is False and e["gate"] == "activation refused" and e.get("persisted") is False and ef and all(x["code"] == "navigation.engine_route_failed" for x in ef)
          and all(any(x["subject"] == "bridge crossing " + s and s in x["sourceIds"] for x in ef) for s in bridge_subjects)
          and all(x["subject"] in ok_routes for x in ef) and any(d["subject"].startswith("bridge crossing") for d in gd) and any(d["code"] == "navigation.report_faulted" for d in gd),
          "with Bridge faces disabled in loaded world A, the engine routes over the bridges fail and the gate refuses activation, naming the crossings and the settlements that depend on them; the faulted report is not persisted",
          "; ".join("%s %s" % (x["subject"], x["sourceIds"]) for x in ef))
    check(worlds["A (warm)"]["sameReportAsColdA"] is True and worlds["A (warm)"]["installedAlready"] is True
          and worlds["A"]["reportSha256"] == worlds["A (warm)"]["reportSha256"] and worlds["A"]["navmeshSha256"] == worlds["A (warm)"]["navmeshSha256"],
          "the semantic report is stable in one process: warm world A reproduces cold A's report", worlds["A"]["reportSha256"])
    if run2:
        w2 = {w["label"]: w for w in run2["outputs"]["worlds"]}
        same = {k: (worlds[k]["reportSha256"] == w2.get(k, {}).get("reportSha256")) for k in ("fixture", "A", "B")}
        check(run2.get("status") == "ok" and all(same.values()) and run2.get("processId") != run.get("processId"),
              "the semantic reports of the fixture, A and B are identical across two game processes on the same module", json.dumps(same))
    text = json.dumps([w["report"] for w in worlds.values()])
    check('"faceIndex"' not in text and all(w["report"]["navigationSha256"] == w["navmeshSha256"] and w["report"]["worldContentId"] == w["worldContentId"]
                                            and w["report"]["validatorVersion"] == o["validatorVersion"] and w["report"]["policySha256"] == o["policySha256"] for w in worlds.values()),
          "reports are bound to the world content id, the navmesh hash, the policy hash and the validator version, and carry no native face index")
    check(offline.get("failures") == 0 and offline.get("checks", 0) >= 36, "offline navigation contract passed", "%s/%s" % (offline.get("checks", 0) - offline.get("failures", 0), offline.get("checks")))

    observed = routes.get("landIntoClosedRing_noExclusions (observed: pre-read disable)")
    out = {
        "schema": "mnbl.evidence.navigation-routes", "version": 2, "ticket": "MMR-38",
        "inputs": {"run": a.run, "runSha256": sha(a.run), "pid": run.get("processId"), "run2": a.run2, "run2Sha256": sha(a.run2) if a.run2 else None,
                   "offline": a.offline, "offlineSha256": sha(a.offline)},
        "checks": checks, "passed": sum(c["passed"] for c in checks), "total": len(checks),
        "worlds": {label: {"sceneName": w["sceneName"], "worldContentId": w["worldContentId"], "variant": w["variant"], "navmeshSha256": w["navmeshSha256"],
                           "reportSha256": w["reportSha256"], "activatable": w["activatable"], "gate": w["gate"], "persisted": w.get("persisted"), "faces": w["build"]["faces"],
                           "facesByTerrainType": w["build"]["facesByTerrainType"], "cellSemantics": w["build"].get("cellSemantics"), "bridges": len(w["build"]["bridges"]),
                           "summary": w["report"]["summary"], "tileLinesCrossed": w["report"]["engine"]["tileLinesCrossed"], "validationSeconds": w["validationSeconds"],
                           "snaps": w["report"]["static"]["snaps"], "navigationSetup": w["constructed"]["navigationSetup"]} for label, w in worlds.items()},
        "fixtureRoutes": {k: {"type": x["type"], "expected": x["expected"], "found": x["found"], "pathLength": x["pathLength"], "meshShortestPath": x["meshShortestPath"],
                              "regionSwitches": x["regionSwitches"], "crosses": x["crosses"], "agrees": x["agrees"], "gating": x["gating"]} for k, x in routes.items()},
        "staticFaults": {v: x.get("diagnostics") for v, x in faults.items()},
        "engineFault": ef,
        "observations": {
            "preReadDisable": "With no excluded faces, a path entered the mountain-ringed pocket (found %s); the pre-read disable calls returned %s: the installed SandBox MapScene.Load order (disable the faces invalid for every type, then read) does not disable faces read afterwards. Every path query excludes the invalid types explicitly, as the campaign does, so routing is unaffected." % (observed["found"] if observed else None, worlds["A"]["constructed"]["navigationSetup"]["disabledBeforeRead"]),
            "engineDistance": "Scene.GetPathDistanceBetweenAIFaces returns a cost: the path length plus the region switch cost (50) per embark or disembark; lengths and envelopes are measured on the returned path and switches counted from the difference.",
            "islands": "Every world's mesh is one connected piece and the engine reports one island; the fixture's mountain-closed pocket (a separate All component) shares it, so FaceIslandIndex is not All connectivity.",
            "cornerHops": {k: v for k, v in corner.items()},
            "cornerHopsNote": "All routes between groups whose land touches only diagonally embark and disembark at one vertex (two region switches, a zero-length sea leg). They are accepted as All connectivity (both groups touch the same water there, as MMR-28's basins model it); whether a moving party executes such a leg is observed only once compiled worlds are activated (MMR-45).",
        },
    }
    json.dump(out, open(a.out, "w", encoding="utf-8"), indent=1)
    print("%d/%d checks passed -> %s" % (out["passed"], out["total"], a.out))
    for c in checks:
        print(("ok    " if c["passed"] else "FAIL  ") + c["name"])
    return 0 if out["passed"] == out["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
