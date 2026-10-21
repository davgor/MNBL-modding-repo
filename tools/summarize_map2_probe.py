#!/usr/bin/env python3
"""MMR-249: turn a collected `map_epic2` probe run into evidence/probe/map-epic-2-probe.json.

Reads the probe result (evidence/runtime/in-game/map_epic2-<pid>.json, written by tools/Collect-OwnedProbe.ps1), the engine
screenshots it names, and the slot report build/probe-slots/probe-slots.json, then measures every answer from pixels
(Pillow only): the layer compositing curve, the two-layer order, height blend, tile size and pixels per map unit, per-season
masks and season switches, tints, decal visibility per technique, decal cost and removal, the dynamic snow channel,
orientation and season response, map trees and the level-7 terrain. Screenshots stay on disk; they are cited by path and
SHA-256. Decisions are derived by the rules written next to each one.

Usage: python tools/summarize_map2_probe.py --result <map_epic2-PID.json> [--result <confirming run>] --slots build/probe-slots/probe-slots.json
       --out evidence/probe/map-epic-2-probe.json [--hashes <native-hashes.json>]
"""
import argparse
import hashlib
import json
import math
import os
import sys

from PIL import Image

EXTENT = 1600.0


def srgb_to_linear(v):
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


LIN = [srgb_to_linear(i) for i in range(256)]


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


# ------------------------------------------------------------------------------------------------------------- camera
def sub(a, b): return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def norm(a):
    n = math.sqrt(dot(a, a))
    return [a[0] / n, a[1] / n, a[2] / n]


class View:
    """The engine camera of Camera.LookAt(eye, target, up) + SetFovVertical(fov, width/height)."""

    def __init__(self, shot, size):
        self.eye, self.target, self.fov = shot["eye"], shot["target"], float(shot["fov"])
        self.w, self.h = size
        up = [0.0, 1.0, 0.0] if shot["viewKind"] == "topdown" else [0.0, 0.0, 1.0]
        self.d = norm(sub(self.target, self.eye))
        self.r = norm(cross(self.d, up))
        self.u = cross(self.r, self.d)
        self.ty = math.tan(self.fov / 2)
        self.tx = self.ty * self.w / self.h

    def project(self, p):
        v = sub(p, self.eye)
        z = dot(v, self.d)
        x, y = dot(v, self.r) / (z * self.tx), dot(v, self.u) / (z * self.ty)
        return (x + 1) / 2 * self.w, (1 - y) / 2 * self.h

    def px_per_unit_at_target(self):
        z = dot(sub(self.target, self.eye), self.d)
        return (self.h / 2) / (z * self.ty)


class Shot:
    def __init__(self, record, base):
        self.record = record
        path = record["path"]
        local = os.path.join(base, os.path.basename(path)) if base else path
        self.path = local if os.path.exists(local) else path
        self.ok = os.path.exists(self.path)
        if self.ok:
            self.image = Image.open(self.path).convert("RGB")
            self.px = self.image.load()
            self.view = View(record, self.image.size)

    def rgb(self, x, y, r=1):
        x, y = int(round(x)), int(round(y))
        acc, n = [0.0, 0.0, 0.0], 0
        for dy in range(-r, r + 1):
            for dx in range(-r, r + 1):
                xx, yy = x + dx, y + dy
                if 0 <= xx < self.image.size[0] and 0 <= yy < self.image.size[1]:
                    p = self.px[xx, yy]
                    acc[0] += LIN[p[0]]; acc[1] += LIN[p[1]]; acc[2] += LIN[p[2]]; n += 1
        return [v / n for v in acc] if n else None

    def at(self, wx, wy, wz=2.0, r=1):
        x, y = self.view.project([wx, wy, wz])
        return self.rgb(x, y, r)

    def region(self, x0, y0, x1, y1, wz=2.0, step=2):
        """Pixels inside the projected world rectangle (axis-aligned in the image for top-down shots)."""
        ax, ay = self.view.project([x0, y0, wz]); bx, by = self.view.project([x1, y1, wz])
        lx, hx = sorted((ax, bx)); ly, hy = sorted((ay, by))
        out = []
        for y in range(max(0, int(ly)), min(self.image.size[1], int(hy)), step):
            for x in range(max(0, int(lx)), min(self.image.size[0], int(hx)), step):
                out.append(self.px[x, y])
        return out


def lum(rgb):
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]


def mean_rgb(pixels):
    if not pixels:
        return None
    n = float(len(pixels))
    return [sum(LIN[p[c]] for p in pixels) / n for c in range(3)]


def white_fraction(pixels):
    if not pixels:
        return None
    w = sum(1 for p in pixels if min(p) > 170 and max(p) - min(p) < 40)
    return w / float(len(pixels))


def period(values, min_lag, max_lag):
    """Lag of the strongest autocorrelation peak of a 1-D signal (mean removed)."""
    n = len(values)
    if n < 2 * min_lag + 4:
        return None, None
    m = sum(values) / n
    v = [x - m for x in values]
    var = sum(x * x for x in v) / n or 1e-12
    best, lag_best, prev, rising = None, None, None, False
    corr = []
    for lag in range(1, min(max_lag, n // 2) + 1):
        c = sum(v[i] * v[i + lag] for i in range(n - lag)) / ((n - lag) * var)
        corr.append(c)
    # first local maximum after the first zero crossing, else the global maximum beyond min_lag
    crossed = False
    for i in range(1, len(corr) - 1):
        lag = i + 1
        if corr[i] < 0:
            crossed = True
        if crossed and lag >= min_lag and corr[i] >= corr[i - 1] and corr[i] >= corr[i + 1] and corr[i] > 0.05:
            return lag, round(corr[i], 3)
    for i, c in enumerate(corr):
        if i + 1 >= min_lag and (best is None or c > best):
            best, lag_best = c, i + 1
    return lag_best, round(best, 3) if best is not None else None


def diff_fraction(a, b, boxes, threshold=18):
    """Fraction of pixels inside image boxes whose max channel difference exceeds threshold."""
    changed = total = 0
    for (x0, y0, x1, y1) in boxes:
        for y in range(max(0, int(y0)), min(a.image.size[1], int(y1))):
            for x in range(max(0, int(x0)), min(a.image.size[0], int(x1))):
                p, q = a.px[x, y], b.px[x, y]
                total += 1
                if max(abs(p[0] - q[0]), abs(p[1] - q[1]), abs(p[2] - q[2])) > threshold:
                    changed += 1
    return (changed / float(total)) if total else None, total


def mean_abs_diff(a, b, step=3):
    t, n = 0.0, 0
    for y in range(0, a.image.size[1], step):
        for x in range(0, a.image.size[0], step):
            p, q = a.px[x, y], b.px[x, y]
            t += abs(p[0] - q[0]) + abs(p[1] - q[1]) + abs(p[2] - q[2]); n += 3
    return t / n


def boxes_around(shot, centers, half, wz=None):
    out = []
    for c in centers:
        z = c[2] if len(c) > 2 and wz is None else (wz if wz is not None else 2.0)
        pts = [shot.view.project([c[0] + dx, c[1] + dy, z]) for dx in (-half, half) for dy in (-half, half)]
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        out.append((min(xs), min(ys), max(xs), max(ys)))
    return out


def laplacian_energy(shot, frac=0.5):
    w, h = shot.image.size
    x0, x1, y0, y1 = int(w * (1 - frac) / 2), int(w * (1 + frac) / 2), int(h * (1 - frac) / 2), int(h * (1 + frac) / 2)
    g = lambda x, y: lum(shot.px[x, y])
    total, n = 0.0, 0
    for y in range(y0 + 1, y1 - 1, 2):
        for x in range(x0 + 1, x1 - 1, 2):
            total += abs(4 * g(x, y) - g(x - 1, y) - g(x + 1, y) - g(x, y - 1) - g(x, y + 1)); n += 1
    return total / n if n else None


# ------------------------------------------------------------------------------------------------------------- analyses
def compositing(shot):
    """Band A (magenta ramp over black) and band B (green 128 over the magenta ramp) of node (1,1), il = 9..48."""
    out = {"bandA": [], "bandB": []}
    yA, yB = 400 + 7.5 * 6.25, 400 + 23.5 * 6.25
    full_a = shot.at(400 + 56 * 6.25, yA, r=2)
    full_b = shot.at(400 + 56 * 6.25, yB, r=2)
    for il in range(9, 49, 3):
        w = min(1.0, il / 48.0)
        a = shot.at(400 + il * 6.25, yA, r=2)
        b = shot.at(400 + il * 6.25, yB, r=2)
        out["bandA"].append({"il": il, "w": round(w, 4), "rLin": round(a[0], 5), "bLin": round(a[2], 5), "alphaR": round(a[0] / full_a[0], 4) if full_a[0] else None})
        out["bandB"].append({"il": il, "w": round(w, 4), "rLin": round(b[0], 5), "gLin": round(b[1], 5)})
    # Alpha-over (layer 15 composited first, front to back): A: r = w R; B (green = layer 2 above magenta = layer 1, alpha .502):
    # r = w (1 - .502) R, g = .502 G. The other order would give r = w R, g = .502 (1 - w) G.
    a_err = [abs(p["alphaR"] - p["w"]) for p in out["bandA"] if p["alphaR"] is not None]
    out["bandAmaxAbsErrorVsLinear"] = round(max(a_err), 4) if a_err else None
    rB = [p["rLin"] / full_a[0] for p in out["bandB"]] if full_a[0] else []
    ws = [p["w"] for p in out["bandB"]]
    fit_hi = sum((r - w * (1 - 0.502)) ** 2 for r, w in zip(rB, ws))
    fit_lo = sum((r - w) ** 2 for r, w in zip(rB, ws))
    gB = [p["gLin"] for p in out["bandB"]]
    out["bandB"] = out["bandB"]
    out["bandBgreenSpread"] = round((max(gB) - min(gB)) / (max(gB) or 1), 4) if gB else None
    out["orderFit"] = {"higherIndexOnTopSse": round(fit_hi, 5), "lowerIndexOnTopSse": round(fit_lo, 5)}
    out["fullMagentaLin"] = full_a
    out["fullBandBLin"] = full_b
    return out


def height_blend(shot):
    """Bands C (slope alpha on, scale 5) and D (off, scale 20): luminance ratio to the full patch along the ramp."""
    out = {}
    for name, y, x_full in (("slopeAlpha", 400 + 39.5 * 6.25, 750.0), ("plain", 400 + 56.5 * 6.25, 750.0)):
        full = mean_rgb(shot.region(705, y - 25, 795, y + 25))
        L = lum(full)
        rows = []
        for il in range(9, 49, 3):
            col = mean_rgb(shot.region(400 + il * 6.25 - 2, y - 20, 400 + il * 6.25 + 2, y + 20, step=1))
            rows.append({"il": il, "w": round(il / 48.0, 4), "ratio": round(lum(col) / L, 4) if L else None})
        err = [abs(r["ratio"] - r["w"]) for r in rows if r["ratio"] is not None]
        out[name] = {"rows": rows, "maxAbsErrorVsLinear": round(max(err), 4) if err else None, "fullLuminanceLin": round(L, 5)}
    return out


def stripes(shot):
    """Column x = 409.4 (period-4 stripes, 25 units) across node (1,1): measured period in pixels vs the camera model."""
    v = shot.view
    xs = 400 + 1.5 * 6.25
    ax, ay = v.project([xs, 790.0, 2.0]); bx, by = v.project([xs, 410.0, 2.0])
    n = int(abs(by - ay))
    values = [lum(shot.rgb(ax, ay + i * (by - ay) / n, 0)) for i in range(n)]
    model = v.px_per_unit_at_target()
    lag, corr = period(values, int(25 * model * 0.5), int(25 * model * 2))
    return {"expectedPeriodUnits": 25.0, "modelPxPerUnit": round(model, 4), "measuredPeriodPx": lag, "correlation": corr,
            "measuredPxPerUnit": round(lag / 25.0, 4) if lag else None}


def tile(shot, x0, y0, x1, y1, max_lag=320):
    """Texture repeat period: the strongest peak of the autocorrelation averaged over rows (x) and over columns (y)."""
    v = shot.view
    k = v.px_per_unit_at_target()
    ax, ay = v.project([x0, y1, 2.0]); bx, by = v.project([x1, y0, 2.0])
    out = {"pxPerUnit": round(k, 4)}
    for axis in ("x", "y"):
        lines = []
        if axis == "x":
            for y in range(int(ay), int(by), 4):
                lines.append([lum(shot.rgb(x, y, 0)) for x in range(int(ax), int(bx))])
        else:
            for x in range(int(ax), int(bx), 4):
                lines.append([lum(shot.rgb(x, y, 0)) for y in range(int(ay), int(by))])
        acc = [0.0] * (max_lag + 1)
        for line in lines:
            n = len(line); m = sum(line) / n; d = [a - m for a in line]; var = sum(a * a for a in d) / n or 1e-12
            for lag in range(1, min(max_lag, n - 1) + 1):
                acc[lag] += sum(d[i] * d[i + lag] for i in range(n - lag)) / ((n - lag) * var)
        curve = [a / len(lines) for a in acc]
        peaks = [(lag, curve[lag]) for lag in range(3, max_lag) if curve[lag] > curve[lag - 1] and curve[lag] >= curve[lag + 1]]
        best = max((p[1] for p in peaks), default=None)
        # The fundamental: the shortest lag whose peak reaches 80 % of the strongest (multiples of it peak as well).
        lag, corr = next(((l, c) for l, c in peaks if c >= 0.8 * best), (None, None)) if best is not None else (None, None)
        out[axis] = {"periodPx": lag, "correlation": round(corr, 3) if corr is not None else None, "periodUnits": round(lag / k, 3) if lag else None}
    return out


def magenta_fraction(shot, x0, y0, x1, y1):
    px = shot.region(x0, y0, x1, y1)
    if not px:
        return None
    return round(sum(1 for p in px if p[0] > 60 and p[2] > 60 and p[1] < 0.5 * min(p[0], p[2])) / float(len(px)), 4)


def snow_quadrants(shot):
    q = {"NW": shot.region(20, 820, 780, 1580), "NE": shot.region(820, 820, 1580, 1580), "SW": shot.region(20, 20, 780, 780), "SE": shot.region(820, 20, 1580, 780)}
    out = {k: round(white_fraction(v), 4) if v else None for k, v in q.items()}
    # The memory bar (rows 200-215, columns 0-63): unflipped rows-first mapping puts it at x 0-400, y 1600(1 - 216/256)..1600(1 - 200/256).
    out["barIfRowsAreNorthToSouth"] = round(white_fraction(shot.region(30, 260, 370, 335)), 4)
    out["barIfRowsAreSouthToNorth"] = round(white_fraction(shot.region(30, 1265, 370, 1340)), 4)
    out["barIfTransposed"] = round(white_fraction(shot.region(1265, 30, 1340, 370)), 4)
    ramp = []
    for i in range(16):
        x = 820 + i * 47.5
        f = white_fraction(shot.region(x, 40, x + 40, 760))
        ramp.append({"x": round(x + 20, 1), "rValue": int(round((x + 20 - 800) / 800 * 255)), "white": round(f, 4) if f is not None else None})
    out["seRamp"] = ramp
    thr = [r for r in ramp if r["white"] is not None and r["white"] > 0.5]
    out["seRampThresholdR"] = thr[0]["rValue"] if thr else None
    out["meanLinearLuminance"] = round(lum(mean_rgb(shot.region(0, 0, 1600, 1600, step=6))), 5)
    return out


def frames_table(results):
    rows = []
    for scene in results:
        for f in scene.get("frames", []):
            rows.append({"scene": scene["scene"], "label": scene["label"], "id": f["id"], "meanMs": round(f["meanMs"], 3), "p95Ms": round(f["p95Ms"], 3),
                         "maxMs": round(f["maxMs"], 3), "rendererFps": f.get("rendererFps"), "mainFps": f.get("mainFps"), "decals": f.get("decals"),
                         "gpuMemoryMb": f.get("gpuMemoryMb"), "cpuMemoryMb": f.get("cpuMemoryMb"),
                         "meanMainMs": round(f["meanMainMs"], 4) if "meanMainMs" in f else None})
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--result", action="append", required=True, help="collected probe result JSON; a later one overrides scenes of an earlier one")
    ap.add_argument("--slots", required=True)
    ap.add_argument("--shots", default=None, help="folder holding the screenshots (default: next to the result, else the recorded path)")
    ap.add_argument("--hashes", default=None)
    ap.add_argument("--answers", default=None, help="JSON with the reviewed answers and decisions, merged at the top of the output")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    slots = json.load(open(a.slots, encoding="utf-8"))
    runs, scenes = [], {}
    for path in a.result:
        result = json.load(open(path, encoding="utf-8-sig"))
        runs.append({"result": os.path.relpath(path).replace("\\", "/"), "sha256": sha(path), "pid": result.get("processId"), "status": result.get("status"),
                     "final": result.get("final"), "error": result.get("error")})
        base = a.shots or os.path.dirname(os.path.abspath(path))
        if "map2" not in result.get("outputs", {}):
            runs[-1]["note"] = "no probe outputs were checkpointed (the process stopped before the first scene's checkpoint)"
            continue
        for s in result["outputs"]["map2"]["scenes"]:
            if s.get("status") == "skipped":
                continue
            s["_base"] = base
            s["_pid"] = result.get("processId")
            scenes[s["label"].split("_", 1)[1]] = s
        env = result["outputs"]["map2"]
    out = {"schema": "mnbl.map-epic-2-probe", "version": 1, "ticket": "MMR-249", "runs": runs,
           "environment": {k: env.get(k) for k in ("threadId", "resolution", "applicationVersion", "frameLimiterWithSleep")},
           "slots": {k: {kk: v[kk] for kk in ("heightmapLevel", "samples", "layersPerNode", "floraInstances", "dynamicWeather")} for k, v in slots["slots"].items()},
           "screenshots": [], "measurements": {}}
    if a.hashes:
        out["nativeHashes"] = json.load(open(a.hashes, encoding="utf-8-sig"))

    def shots_of(key):
        s = scenes.get(key)
        if not s:
            return {}
        res = {}
        for r in s.get("shots", []):
            # Prefer the collected copy: Collect-OwnedProbe names <label>_<id>Screenshot as <probe>-<pid>-<label>_<id>Screenshot.bmp.
            alt = os.path.join(s["_base"], "map_epic2-%s-%s_%sScreenshot.bmp" % (s["_pid"], s["label"], r["id"]))
            if os.path.exists(alt):
                r = dict(r); r["path"] = alt; shot = Shot(r, None)
            else:
                shot = Shot(r, s["_base"])
            res[r["id"]] = shot
            shown = os.path.relpath(shot.path) if shot.ok and not os.path.relpath(shot.path).startswith("..") else shot.path
            out["screenshots"].append({"scene": s["label"], "id": r["id"], "path": shown.replace("\\", "/"), "exists": shot.ok,
                                       "sha256": sha(shot.path) if shot.ok else None})
        return res

    m = out["measurements"]
    m["scenes"] = {k: {"status": s.get("status"), "failure": (s.get("failure") or "")[:400] or None, "load": s.get("load"),
                       "steps": [{kk: st.get(kk) for kk in ("name", "status", "error", "seconds")} for st in s.get("steps", [])]} for k, s in scenes.items()}
    m["frames"] = frames_table(scenes.values())

    l6 = shots_of("l6")
    if "node11_topdown" in l6 and l6["node11_topdown"].ok:
        m["compositing"] = compositing(l6["node11_topdown"])
        m["heightBlend"] = height_blend(l6["node11_topdown"])
        m["stripes"] = stripes(l6["node11_topdown"])
    for key, box in (("scale5_topdown", (705, 605, 795, 688)), ("scale20_topdown", (705, 706, 795, 795))):
        if key in l6 and l6[key].ok:
            m.setdefault("tileSize", {})[key] = tile(l6[key], *box)
    if "map38_flat" in l6 and l6["map38_flat"].ok:
        v = l6["map38_flat"].view
        m["map38"] = {"pxPerUnitAtTarget": round(v.px_per_unit_at_target(), 3), "resolution": [v.w, v.h], "fovVertical": v.fov,
                      "distance": 38.0, "elevationRad": 38 * 0.0075 + 0.35}
    seasons = {}
    for sid in ("season0_node11_topdown", "seasonfactor1_node11_topdown", "wintertime1_node11_topdown", "reset_node11_topdown"):
        if sid in l6 and l6[sid].ok:
            seasons[sid] = {"magentaBandAEvenNode11": magenta_fraction(l6[sid], 460, 402, 700, 495),
                            "meanLinLuminance": round(lum(mean_rgb(l6[sid].region(400, 400, 800, 800, step=4))), 5)}
    for sid in ("season0_overview", "seasonfactor1_overview", "wintertime1_overview"):
        if sid in l6 and l6[sid].ok:
            seasons[sid] = {"meanAbsDiffVsSeason0": round(mean_abs_diff(l6[sid], l6["season0_overview"]), 3) if "season0_overview" in l6 else None}
    m["seasonMasksL6"] = seasons

    l15 = shots_of("l15")
    if "node11_topdown" in l15 and l15["node11_topdown"].ok:
        sh = l15["node11_topdown"]
        bands = []
        for k in range(1, 15):
            c = mean_rgb(sh.region(400 + 25 * k + 3, 430, 400 + 25 * k + 16, 770))
            bands.append({"slot": k, "diffuse": slots["slots"]["mnbl_probe_l15"]["layers"][k]["diffuse"], "albedoMode": slots["slots"]["mnbl_probe_l15"]["layers"][k]["albedoMode"],
                          "meanLin": [round(x, 5) for x in c]})
        base = mean_rgb(sh.region(402, 430, 418, 770))
        m["gallery"] = {"bands": bands, "baseMeanLin": [round(x, 5) for x in base]}
        b1, b13, b14 = bands[0]["meanLin"], bands[12]["meanLin"], bands[13]["meanLin"]
        m["tints"] = {"mode1AddMinusUntinted": [round(b13[i] - b1[i], 4) for i in range(3)], "mode1Requested": [0.2, 0.0, 0.0],
                      "mode2RatioToUntinted": [round(b14[i] / b1[i], 4) if b1[i] else None for i in range(3)], "mode2Requested": [1.0, 0.55, 0.55]}

    dec = scenes.get("l2_decals")
    dshots = shots_of("l2_decals")
    if dec and dshots:
        tech = dec.get("decalTechniques", {})
        res = {"materialShader": tech.get("pathMaterialShader"), "materialFound": tech.get("pathMaterialFound")}
        seq = [("d1", "d0_baseline", "d1_sceneDecals", 1.5), ("d2", "d1_sceneDecals", "d2_runtimeTexture", 2.0),
               ("d3", "d2_runtimeTexture", "d3_entityDecals", 1.5), ("d4", "d3_entityDecals", "d4_meshDecals", 1.5)]
        for key, before, after, half in seq:
            if key not in tech or before not in dshots or after not in dshots or not dshots[after].ok:
                continue
            centers = tech[key]["centers"]
            pts = [[c[0], c[1], c[2] if len(c) > 2 else None] for c in centers]
            z = None
            boxes = []
            for c in pts:
                zc = c[2] if c[2] is not None else dshots[after].record.get("target", [0, 0, 2])[2]
                boxes += boxes_around(dshots[after], [[c[0], c[1], zc]], half, wz=zc)
            frac, total = diff_fraction(dshots[before], dshots[after], boxes)
            res[key] = {"technique": tech[key]["technique"], "count": tech[key]["count"], "changedFraction": round(frac, 4) if frac is not None else None, "pixels": total}
            if key == "d2":
                mag = cyan = 0
                for (x0, y0, x1, y1) in boxes:
                    for y in range(max(0, int(y0)), min(dshots[after].image.size[1], int(y1))):
                        for x in range(max(0, int(x0)), min(dshots[after].image.size[0], int(x1))):
                            p = dshots[after].px[x, y]
                            if p[0] > 140 and p[2] > 140 and p[1] < 90: mag += 1
                            if p[1] > 140 and p[2] > 140 and p[0] < 90: cyan += 1
                            if p[0] > 140 and p[1] > 140 and p[2] < 90: res.setdefault("d2yellow", 0); res["d2yellow"] += 1
                res[key]["magentaPixels"], res[key]["cyanPixels"] = mag, cyan
        counts = dec.get("decalCounts", {})
        res["counts"] = [f for f in m["frames"] if f["label"] == dec["label"] and f["id"].startswith(("count", "removed"))]
        res["removeMs"], res["recreate200Ms"] = counts.get("removeMs"), counts.get("recreate200Ms")
        res["createRows"] = counts.get("rows")
        if "removed" in dshots and "count0" in dshots and dshots["removed"].ok:
            res["removedVsCount0MeanAbsDiff"] = round(mean_abs_diff(dshots["removed"], dshots["count0"]), 3)
        if "recreated200" in dshots and "count200" in dshots and dshots["recreated200"].ok:
            res["recreated200VsCount200MeanAbsDiff"] = round(mean_abs_diff(dshots["recreated200"], dshots["count200"]), 3)
            res["count200VsCount0MeanAbsDiff"] = round(mean_abs_diff(dshots["count200"], dshots["count0"]), 3)
        m["decals"] = res

    # Run 2 / 3: decal variants and counts with main-thread time, dynamic snow variants.
    d2 = scenes.get("l2_decals2")
    d2shots = shots_of("l2_decals2")
    if d2 and d2shots:
        res = {"variants": {}}
        seq = ["v0_baseline", "v1_copyTextureRegistered", "v2_copyPlain", "v3_factorTint", "v4_entityRegistered", "v5_groundOverlays"]
        for before, after in zip(seq, seq[1:]):
            a_, b_ = d2shots.get(before), d2shots.get(after)
            if not (a_ and b_ and a_.ok and b_.ok):
                continue
            w, h = a_.image.size
            frac, total = diff_fraction(a_, b_, [(0, 0, w, h)], threshold=10)
            entry = {"changedPixels": int(round(frac * total)) if frac is not None else None}
            if after == "v3_factorTint":
                entry["magentaPixels"] = sum(1 for y in range(0, h, 2) for x in range(0, w, 2)
                                             if b_.px[x, y][0] > 70 and b_.px[x, y][2] > 70 and b_.px[x, y][1] < 0.6 * min(b_.px[x, y][0], b_.px[x, y][2]))
            res["variants"][after] = entry
        res["record"] = {k: v for k, v in d2.get("decalVariants", {}).items() if k.startswith("v5_materials")}
        res["counts"] = [f for f in m["frames"] if f["label"] == d2["label"]]
        res["create"] = d2.get("decalCounts2")
        m["decals2"] = res
    snow2 = {}
    for key in ("snow_snow2", "snowf_snow2", "snowfr_snow2", "snowf_snow3", "snowp_snow3"):
        s = scenes.get(key)
        if not s:
            continue
        sh = shots_of(key)
        entry = {"status": s.get("status"), "steps": [(x.get("name"), x.get("status")) for x in s.get("steps", [])], "load": s.get("load")}
        base = {q: sh.get("inherent_summer_" + q) for q in ("q_nw", "q_ne", "q_sw", "q_se")}
        diffs = {}
        for sid, shot in sh.items():
            q = sid[-4:]
            if base.get(q) is not None and base[q].ok and shot.ok and sid != "inherent_summer_" + q:
                diffs[sid] = round(mean_abs_diff(base[q], shot), 3)
                diffs[sid + "_white"] = round(white_fraction([shot.px[x, y] for y in range(0, shot.image.size[1], 4) for x in range(0, shot.image.size[0], 4)]), 4)
        entry["meanAbsDiffVsInherentSummer"] = diffs
        snow2[key] = entry
    m["snow2"] = snow2

    snow = scenes.get("snow")
    sshots = shots_of("snow")
    if snow and sshots:
        res = {"dynamicSnow": snow.get("dynamicSnow", {}).get("pattern"), "threadId": snow.get("dynamicSnow", {}).get("threadId"), "seasons": snow.get("seasons")}
        for sid, shot in sshots.items():
            if sid.endswith("snow_topdown") and shot.ok:
                res[sid] = snow_quadrants(shot)
        m["snow"] = res

    world = {}
    for key in ("a6", "a6n", "a7", "a7s", "l2", "forest"):
        sh = shots_of(key)
        if not sh:
            continue
        world[key] = {"load": scenes[key].get("load"), "laplacian": {sid: round(laplacian_energy(s), 5) for sid, s in sh.items() if s.ok and sid in ("mountain_oblique", "map38_mountain", "map38_coast", "forest_oblique", "map38_dense", "map38_flat")}}
    m["worldAndForest"] = world
    for key in ("l2", "l6", "l15", "forest", "snow", "a6", "a6n", "a7", "a7s", "l2_decals"):
        if key not in scenes:
            m["scenes"].setdefault(key, {"status": "not-run"})
    if a.answers:
        reviewed = json.load(open(a.answers, encoding="utf-8"))
        out = dict(list(reviewed.items()) + [(k, v) for k, v in out.items() if k not in reviewed])
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(out, handle, indent=1)
        handle.write("\n")
    print(json.dumps({k: (v if k != "frames" else len(v)) for k, v in m.items() if k not in ("scenes", "worldAndForest")}, indent=1)[:6000])


if __name__ == "__main__":
    main()
