"""Offline top-down preview of a world's magic water veins with the ground veins they continue (MMR-290).

Draws the stamps Test-MagicWater writes with -Dump (DIR/<A|B|A-nerdun>/water.json) over tools/render_magic_veins.py's plain base map:
ground-layer stamps (MMR-288's land veins, and magic water's inland and shore-glow stamps) alpha over from mnbl_magic_<look>_d (4 x 4 cells
of 512 px, the box over the inner square as render_magic_veins draws them), water-layer stamps ADDED to the picture from mnbl_magicwater_d
(4 x 8 cells of 512 x 256 px; the box spans the whole cell), the way the campaign map's water shader adds a decal's colour to the water. Not
the engine's look (no lighting, normals, depth fade or water colour variation): it shows where the water veins run and how they join the land.

    python tools/render_magic_water.py --dump build/mw-dump/A/water.json --out a.png [--ppu 4] [--window X Y W H | --zone auto | --centre X Y HALF]
        [--hours H] [--graph] [--ground-dir .../AssetSources/magic_ground] [--water-dir .../AssetSources/magic_water]
"""
import argparse
import json
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import render_magic_veins as rmv  # noqa: E402

SOURCES = os.path.join(os.path.dirname(HERE), "Modules", "MNBL.ProceduralWorld", "AssetSources")
CELL_W, CELL_H = 512, 256
SEA = (24, 40, 62)  # about acc4's coast and deep sea


def water_cell(cache, path, row, col):
    if path not in cache:
        cache[path] = Image.open(path).convert("RGBA")
    key = (path, row, col)
    if key not in cache:
        atlas = cache[path]
        cell = atlas.crop((col * CELL_W, row * CELL_H, (col + 1) * CELL_W, (row + 1) * CELL_H))
        cache[key] = Image.composite(cell.convert("RGB"), Image.new("RGB", cell.size), cell.getchannel("A"))  # premultiplied: what the shader adds
    return cache[key]


def add_stamps(img, stamps, path, ppu, x0, y0, period):
    cache = {}
    for s in stamps:
        lw, lh = max(1, int(round(s["length"] * ppu))), max(1, int(round(s["width"] * ppu)))
        cell = water_cell(cache, path, s["row"], s["col"]).resize((lw, lh), Image.BILINEAR)
        turned = cell.rotate(-math.degrees(s["yaw"]), resample=Image.BILINEAR, expand=True)
        for shift in (-period, 0.0, period):
            cx, cy = (s["x"] + shift - x0) * ppu, (s["y"] - y0) * ppu
            left, top = int(round(cx - turned.width / 2)), int(round(cy - turned.height / 2))
            if left > img.width or top > img.height or left + turned.width < 0 or top + turned.height < 0:
                continue
            box = (left, top, left + turned.width, top + turned.height)
            region = img.crop(box)
            img.paste(ImageChops.add(region, turned), box[:2])


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--dump", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ground-dir", default=os.path.join(SOURCES, "magic_ground"))
    ap.add_argument("--water-dir", default=os.path.join(SOURCES, "magic_water"))
    ap.add_argument("--ppu", type=float, default=4.0)
    ap.add_argument("--window", type=float, nargs=4, metavar=("X", "Y", "W", "H"))
    ap.add_argument("--centre", type=float, nargs=3, metavar=("X", "Y", "HALF"))
    ap.add_argument("--zone", choices=["auto"], help="a 96 x 64 window on the densest water stamps")
    ap.add_argument("--hours", type=float)
    ap.add_argument("--graph", action="store_true")
    ap.add_argument("--title", default="")
    a = ap.parse_args(argv)
    with open(a.dump, encoding="utf-8") as fh:
        doc = json.load(fh)
    f = doc["frame"]
    period = f["width"]
    stamps = doc["stamps"]
    if a.hours is not None:
        stamps = [s for s in stamps if s["revealAt"] <= a.hours]
    if a.window:
        x0, y0, w, h = a.window
    elif a.centre:
        x0, y0, w, h = a.centre[0] - a.centre[2], a.centre[1] - a.centre[2], 2 * a.centre[2], 2 * a.centre[2]
    elif a.zone:
        wet = [s for s in stamps if s["layer"] == "water"]
        best, centre = -1, None
        for c in wet[::3]:
            n = sum(1 for s in wet if abs((s["x"] - c["x"] + period / 2) % period - period / 2) <= 48 and abs(s["y"] - c["y"]) <= 32)
            if n > best:
                best, centre = n, c
        x0, y0, w, h = centre["x"] - 48, centre["y"] - 32, 96, 64
    else:
        x0, y0, w, h = f["strip"], f["poleMargin"], f["width"], f["height"]
    rmv.WATER = SEA
    img = rmv.base_map(doc, a.ppu, x0, y0, w, h)
    ground = sorted([s for s in stamps if s["layer"] == "ground"], key=lambda s: (s["kind"] == "burst", -s["width"]))
    rmv.draw_stamps(img, doc, ground, a.ground_dir, a.ppu, x0, y0, period)
    water = sorted([s for s in stamps if s["layer"] == "water"], key=lambda s: (s["kind"] == "burst", -s["width"]))
    add_stamps(img, water, os.path.join(a.water_dir, "mnbl_magicwater_d.png"), a.ppu, x0, y0, period)
    if a.graph:
        rmv.draw_graph(img, doc, a.ppu, x0, y0, period, a.hours)
    if a.title:
        d = ImageDraw.Draw(img)
        d.rectangle([0, 0, 8 + 7 * len(a.title), 18], fill=(0, 0, 0))
        d.text((4, 4), a.title, fill=(255, 255, 255))
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    img.save(a.out)
    print("%s: %d ground + %d water stamps, window %.1f %.1f %.1f %.1f, %dx%d px" % (a.out, len(ground), len(water), x0, y0, w, h, img.width, img.height))
    return 0


if __name__ == "__main__":
    sys.exit(main())
