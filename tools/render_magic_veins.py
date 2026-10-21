"""Offline top-down preview of a world's magic ground veins (MMR-288).

Draws the stamps Test-MagicGround writes with -Dump (DIR/<A|B|A-nerdun>/veins.json) over a plain biome-colour base map: each stamp is its
atlas cell (mnbl_magic_<look>_d.png, 4 x 4 cells of 512 px, margin included; row by core group, column by kind) scaled to its box
(length L along the vein, width W across), turned by its yaw and laid over the base with its alpha. Not the engine's look (no lighting,
no normal or specular map, no terrain relief): it shows where the veins run, how they branch and thin, and how a live cast spreads.

    python tools/render_magic_veins.py --veins build/dump/A/veins.json --out a.png [--ppu 4] [--window X Y W H | --zone auto]
        [--hours H] [--water] [--graph] [--atlas-dir Modules/MNBL.ProceduralWorld/AssetSources/magic_ground]

Needs Pillow (no numpy). The frame is canonical physical (x from the strip, y from the pole margin); north (row 0) is at the top.
"""
import argparse
import json
import math
import os
import sys

from PIL import Image, ImageDraw

CELL = 512
PALETTE = {  # plain base colours by natural biome core
    "desert": (205, 182, 128), "grassland": (128, 150, 82), "forest": (66, 104, 56), "rainforest": (46, 92, 50), "marsh": (90, 110, 80),
    "exposed_rock": (128, 120, 112), "boreal_forest": (72, 96, 76), "cold_tundra": (150, 156, 140), "land_ice": (226, 232, 238),
    "tundra": (150, 156, 140), "snow": (230, 234, 240), "ocean": (46, 78, 120), "lake": (60, 100, 140),
}
WATER = (46, 78, 120)
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ATLAS = os.path.join(os.path.dirname(HERE), "Modules", "MNBL.ProceduralWorld", "AssetSources", "magic_ground")


def base_map(doc, ppu, x0, y0, w, h):
    f = doc["frame"]
    side, n, width, height = f["side"], f["columns"], f["width"], f["height"]
    strip, pole, meridian = f["strip"], f["poleMargin"], f["meridian"]
    cores, core, water = doc["base"]["cores"], doc["base"]["core"], doc["base"]["water"]
    cw, ch = width / n, height / n
    # One pixel per sample in a period-wide image, then scaled: sample (r, c) sits at logical (W frac(c/n - meridian/360), H r/n).
    small = Image.new("RGB", (n, side), WATER)
    px = small.load()
    shift = meridian / 360.0
    for r in range(side):
        for c in range(n):
            i = r * side + c
            u = c / n - shift
            u -= math.floor(u)
            col = int(round(u * n)) % n
            if water[i]:
                colour = WATER
            else:
                name = cores[core[i]] if core[i] >= 0 else None
                colour = PALETTE.get(name, (120, 120, 120))
            px[col, r] = colour
    # Physical frame: column k covers x in strip + (k - 0.5) cw .. (k + 0.5) cw; row r covers pole + (r - 0.5) ch ...
    out = Image.new("RGB", (int(round(w * ppu)), int(round(h * ppu))), WATER)
    big = small.resize((int(round(n * cw * ppu)), int(round(side * ch * ppu))), Image.NEAREST)
    period = big.width
    ox = (strip - 0.5 * cw - x0) * ppu
    oy = (pole - 0.5 * ch - y0) * ppu
    start = ox - math.ceil((ox + period) / period) * period
    k = start
    while k < out.width:
        out.paste(big, (int(round(k)), int(round(oy))))
        k += period
    return out


def cell_image(cache, atlas_dir, look, row, col):
    key = (look, row, col)
    if key not in cache:
        path = os.path.join(atlas_dir, "mnbl_magic_%s_d.png" % look)
        if path not in cache:
            cache[path] = Image.open(path).convert("RGBA")
        atlas = cache[path]
        # The decal box spans the whole cell, margin included (the atlas argument selects col/4, row/4 at 0.25 x 0.25).
        x, y = col * CELL, row * CELL
        cache[key] = atlas.crop((x, y, x + CELL, y + CELL))
    return cache[key]


def draw_stamps(img, doc, stamps, atlas_dir, ppu, x0, y0, period):
    cache = {}
    for s in stamps:
        if s["row"] < 0:
            continue
        lw, lh = max(1, int(round(s["length"] * ppu))), max(1, int(round(s["width"] * ppu)))
        cell = cell_image(cache, atlas_dir, s["look"], s["row"], s["col"]).resize((lw, lh), Image.BILINEAR)
        alpha = s.get("alpha", 1.0)
        if alpha < 1:  # the decal's SetFactor1 alpha (a multiply of the stamp's alpha)
            cell.putalpha(cell.getchannel("A").point(lambda v: int(round(v * alpha))))
        # Image y runs with map y, so a yaw from +x toward +y turns clockwise on screen.
        turned = cell.rotate(-math.degrees(s["yaw"]), resample=Image.BILINEAR, expand=True)
        for shift in (-period, 0.0, period):
            cx, cy = (s["x"] + shift - x0) * ppu, (s["y"] - y0) * ppu
            left, top = int(round(cx - turned.width / 2)), int(round(cy - turned.height / 2))
            if left > img.width or top > img.height or left + turned.width < 0 or top + turned.height < 0:
                continue
            img.paste(turned, (left, top), turned)


def draw_graph(img, doc, ppu, x0, y0, period, hours):
    d = ImageDraw.Draw(img)
    nodes = doc["nodes"]
    for x, y, parent, width, order, water, live, reveal in nodes:
        if parent < 0 or (hours is not None and reveal > hours):
            continue
        px, py = nodes[parent][0], nodes[parent][1]
        dx = x - px
        dx -= round(dx / period) * period
        for shift in (-period, 0.0, period):
            a = ((px + shift - x0) * ppu, (py - y0) * ppu)
            b = ((px + dx + shift - x0) * ppu, (y - y0) * ppu)
            d.line([a, b], fill=(40, 200, 255) if water else (255, 120, 0) if live else (20, 20, 20), width=2 if live else 1)
    for i in doc["sources"]:
        if hours is not None and nodes[i][7] > hours:
            continue
        x, y = nodes[i][0], nodes[i][1]
        cx, cy = (x - x0) * ppu, (y - y0) * ppu
        d.ellipse([cx - 3, cy - 3, cx + 3, cy + 3], outline=(255, 255, 255))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--veins", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--atlas-dir", default=DEFAULT_ATLAS)
    ap.add_argument("--ppu", type=float, default=4.0, help="pixels per map unit (the game shows about 37 at the closest camera)")
    ap.add_argument("--window", type=float, nargs=4, metavar=("X", "Y", "W", "H"), help="canonical physical window (default: the whole world)")
    ap.add_argument("--zone", choices=["auto"], help="a 96 x 64 window on the densest drawn generated stamps")
    ap.add_argument("--centre", type=float, nargs=3, metavar=("X", "Y", "HALF"), help="window centred on a point")
    ap.add_argument("--hours", type=float, help="campaign hours: draw the stamps revealed by then (live casts grow over revealHours)")
    ap.add_argument("--water", action="store_true", help="also draw the graph's water stamps (MMR-290's; not drawn by the ground overlay)")
    ap.add_argument("--graph", action="store_true", help="overlay the vein graph's edges (black land, cyan water, orange live) and sources (circles)")
    ap.add_argument("--title", default="")
    a = ap.parse_args(argv)
    with open(a.veins, encoding="utf-8") as fh:
        doc = json.load(fh)
    f = doc["frame"]
    period = f["width"]
    stamps = [s for s in doc["stamps"] if (s["drawn"] or (a.water and s["onWater"]))]
    if a.hours is not None:
        stamps = [s for s in stamps if s["revealAt"] <= a.hours]
    if a.window:
        x0, y0, w, h = a.window
    elif a.centre:
        x0, y0, w, h = a.centre[0] - a.centre[2], a.centre[1] - a.centre[2], 2 * a.centre[2], 2 * a.centre[2]
    elif a.zone:
        # The densest 96 x 64 window of drawn generated stamps (centred on a stamp).
        drawn = [s for s in doc["stamps"] if s["drawn"] and not s["live"]]
        best, centre = -1, None
        for c in drawn:
            n = sum(1 for s in drawn if abs((s["x"] - c["x"] + period / 2) % period - period / 2) <= 48 and abs(s["y"] - c["y"]) <= 32)
            if n > best:
                best, centre = n, c
        x0, y0, w, h = centre["x"] - 48, centre["y"] - 32, 96, 64
    else:
        x0, y0, w, h = f["strip"], f["poleMargin"], f["width"], f["height"]
    img = base_map(doc, a.ppu, x0, y0, w, h)
    stamps.sort(key=lambda s: (s["kind"] == "burst", -s["width"]))  # bursts on top
    draw_stamps(img, doc, stamps, a.atlas_dir, a.ppu, x0, y0, period)
    if a.graph:
        draw_graph(img, doc, a.ppu, x0, y0, period, a.hours)
    if a.title:
        d = ImageDraw.Draw(img)
        d.rectangle([0, 0, 8 + 7 * len(a.title), 18], fill=(0, 0, 0))
        d.text((4, 4), a.title, fill=(255, 255, 255))
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    img.save(a.out)
    print("%s: %d stamps, window %.1f %.1f %.1f %.1f, %dx%d px" % (a.out, len(stamps), x0, y0, w, h, img.width, img.height))
    return 0


if __name__ == "__main__":
    sys.exit(main())
