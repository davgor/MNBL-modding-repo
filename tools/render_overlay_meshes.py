"""Offline top-down preview of MMR-291's overlay meshes: veins, rivers and magic water as the runtime builds them.

Draws the meshes Test-OverlayMeshes writes with -Dump (DIR/<A|B|A-nerdun>/overlay.json) triangle by triangle over tools/render_magic_veins.py's
plain biome base map, so what is checked is the mesh data itself (positions in the chunk frame, UVs, vertex alpha, indices):
  * veins: each triangle's UVs mapped onto mnbl_magic_<look>_d.png (4 x 4 cells of 512 px), alpha over with the texture alpha times the
    vertex alpha, the colour lifted by the look's glow (the template's self-illumination adds the diffuse colour times the glow);
  * magic water: the same from mnbl_magicwater_d.png (4 x 8 cells of 512 x 256 px), its glow;
  * rivers: the water shader's depth colour by each triangle's mean depth (vertex z less the ground under it): the ground's colour fading to
    a river colour by 1 - exp(-5.5 depth), nothing where the depth is below zero (the dipped banks), as water_surface_worldmap.rs blends it.
Not the engine's look (no lighting, normals, refraction or reflections; flat per-triangle depth): it shows that the meshes carry the art where
the decals were meant to draw it. --wire overlays the triangle edges.

    python tools/render_overlay_meshes.py --dump build/ovl-dump/A/overlay.json --out a.png [--ppu 12] [--window X Y W H] [--hours H] [--wire]
        [--layers veins,rivers,magicWater]

Needs Pillow (no numpy).
"""
import argparse
import json
import math
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import render_magic_veins as rmv  # noqa: E402

SOURCES = os.path.join(os.path.dirname(HERE), "Modules", "MNBL.ProceduralWorld", "AssetSources")
RIVER = (38, 62, 84)   # about acc4's coast sea and lake colours


def affine(src, dst):
    """The 6 coefficients (a, b, c, d, e, f) with src = (a x + b y + c, d x + e y + f) for the three dst points (Pillow's AFFINE data)."""
    (x0, y0), (x1, y1), (x2, y2) = dst
    det = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
    if abs(det) < 1e-12:
        return None
    out = []
    for k in range(2):
        s0, s1, s2 = src[0][k], src[1][k], src[2][k]
        a = ((s1 - s0) * (y2 - y0) - (s2 - s0) * (y1 - y0)) / det
        b = ((s2 - s0) * (x1 - x0) - (s1 - s0) * (x2 - x0)) / det
        out += [a, b, s0 - a * x0 - b * y0]
    return tuple(out)


def draw_textured(img, tex, tri_px, tri_uv, alpha, glow):
    xs, ys = [p[0] for p in tri_px], [p[1] for p in tri_px]
    left, top = int(math.floor(min(xs))), int(math.floor(min(ys)))
    right, bottom = int(math.ceil(max(xs))) + 1, int(math.ceil(max(ys))) + 1
    if right <= 0 or bottom <= 0 or left >= img.width or top >= img.height or right - left < 1 or bottom - top < 1:
        return
    local = [(x - left, y - top) for x, y in tri_px]
    src = [(u * tex.width, v * tex.height) for u, v in tri_uv]
    coeffs = affine(src, local)
    if coeffs is None:
        return
    size = (right - left, bottom - top)
    patch = tex.transform(size, Image.AFFINE, coeffs, resample=Image.BILINEAR)
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).polygon(local, fill=255)
    a = patch.getchannel("A").point(lambda v: int(v * alpha))
    a = Image.composite(a, Image.new("L", size, 0), mask)
    rgb = patch.convert("RGB")
    if glow > 0:
        rgb = rgb.point(lambda v: min(255, int(v * (1 + glow))))
    region = img.crop((left, top, right, bottom))
    region.paste(rgb, (0, 0), a)
    img.paste(region, (left, top))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--dump", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--ppu", type=float, default=12)
    ap.add_argument("--window", type=float, nargs=4)
    ap.add_argument("--hours", type=float, default=float("inf"))
    ap.add_argument("--wire", action="store_true")
    ap.add_argument("--layers", default="rivers,veins,magicWater")
    ap.add_argument("--ground-dir", default=os.path.join(SOURCES, "magic_ground"))
    ap.add_argument("--water-dir", default=os.path.join(SOURCES, "magic_water"))
    a = ap.parse_args(argv)
    doc = json.load(open(a.dump, encoding="utf-8"))
    x0, y0, w, h = a.window or doc["window"]
    period = doc["frame"]["width"]
    heights = doc.get("heights")
    if heights:
        # The biome base marks river cells as water; with the dumped ground, water is painted where the scene shows it instead (below), so the
        # river meshes are seen against the ground they run over.
        doc["base"]["water"] = [0] * len(doc["base"]["water"])
    img = rmv.base_map(doc, a.ppu, x0, y0, w, h).convert("RGB")
    if heights:
        # Where the dumped ground lies under the sea datum or a shown lake plane, the scene shows water: paint the base so (the biome base map
        # is per source sample and coarser than the coast).
        n, V, lakes = heights["nx"], heights["values"], doc.get("lakes") or []
        mask = Image.new("L", (n, heights["ny"]), 0)
        mp = mask.load()
        for j in range(heights["ny"]):
            for i in range(n):
                x, y, g = heights["x0"] + i * heights["step"], heights["y0"] + j * heights["step"], V[j * n + i]
                if g < 0 or any(l[0] <= x <= l[2] and l[1] <= y <= l[3] and l[4] > g for l in lakes):
                    mp[i, j] = 255
        scale = heights["step"] * a.ppu
        big = mask.resize((int(round(n * scale)), int(round(heights["ny"] * scale))), Image.BILINEAR)
        sea = Image.new("RGB", img.size, rmv.WATER)
        layer = Image.new("L", img.size, 0)
        layer.paste(big, (int(round((heights["x0"] - x0) * a.ppu)), int(round((heights["y0"] - y0) * a.ppu))))
        img = Image.composite(sea, img, layer.point(lambda v: 255 if v >= 128 else 0))
    textures = {}

    def texture(path):
        if path not in textures:
            textures[path] = Image.open(path).convert("RGBA")
        return textures[path]

    def ground(x, y):
        """The dumped ground (bilinear over the half-unit crop), None outside it."""
        if not heights:
            return None
        fx, fy = (x - heights["x0"]) / heights["step"], (y - heights["y0"]) / heights["step"]
        i, j = int(math.floor(fx)), int(math.floor(fy))
        if i < 0 or j < 0 or i + 1 >= heights["nx"] or j + 1 >= heights["ny"]:
            return None
        u, v, n, V = fx - i, fy - j, heights["nx"], heights["values"]
        return (V[j * n + i] * (1 - u) + V[j * n + i + 1] * u) * (1 - v) + (V[(j + 1) * n + i] * (1 - u) + V[(j + 1) * n + i + 1] * u) * v

    def split(tri, depth):
        """A triangle (points of x, y, z, u, v, world x, world y) in 4^depth sub-triangles."""
        out = [tri]
        for _ in range(depth):
            nxt = []
            for t in out:
                m = [[(t[k][q] + t[(k + 1) % 3][q]) / 2 for q in range(len(t[0]))] for k in range(3)]
                nxt += [[t[0], m[0], m[2]], [m[0], t[1], m[1]], [m[2], m[1], t[2]], [m[0], m[1], m[2]]]
            out = nxt
        return out

    def depth_of(t):
        """The mean depth of a (sub-)triangle: its z over the ground at its corners (None where no ground is known)."""
        gs = [ground(p[5], p[6]) for p in t]
        if any(g is None for g in gs):
            return None
        return sum(p[2] - g for p, g in zip(t, gs)) / 3

    layers = a.layers.split(",")
    meshes = [m for m in doc["meshes"] if (m.get("revealAt") is None or m["revealAt"] <= a.hours)]
    counts = {}
    for layer in layers:
        for m in (m for m in meshes if m["layer"] == layer):
            ox, oy = m["origin"]
            # the mesh's image nearest the window's centre
            cx = x0 + w / 2
            ox = cx + (((ox - cx) + period / 2) % period) - period / 2
            P, U, A, I = m["positions"], m["uvs"], m["alpha"], m["indices"]
            px = [((ox + P[3 * v] - x0) * a.ppu, (oy + P[3 * v + 1] - y0) * a.ppu) for v in range(len(A))]
            if not px or max(p[0] for p in px) < 0 or min(p[0] for p in px) > img.width or max(p[1] for p in px) < 0 or min(p[1] for p in px) > img.height:
                continue
            counts[layer] = counts.get(layer, 0) + len(I) // 3
            # corner records: pixel x, pixel y, z, u, v, world x, world y
            corner = [[px[v][0], px[v][1], P[3 * v + 2], U[2 * v], U[2 * v + 1], ox + P[3 * v], oy + P[3 * v + 1]] for v in range(len(A))]
            if layer == "rivers":
                draw = ImageDraw.Draw(img, "RGBA")
                for t in range(0, len(I), 3):
                    for sub in split([corner[v] for v in I[t:t + 3]], 2):
                        d = depth_of(sub)
                        if d is None or d <= 0:
                            continue
                        draw.polygon([(q[0], q[1]) for q in sub], fill=RIVER + (int(255 * (1 - math.exp(-5.5 * d))),))
                continue
            if layer == "veins":
                tex = texture(os.path.join(a.ground_dir, "mnbl_magic_%s_d.png" % m["look"]))
            else:
                tex = texture(os.path.join(a.water_dir, "mnbl_magicwater_d.png"))
            for t in range(0, len(I), 3):
                k = I[t:t + 3]
                alpha, glow = sum(A[v] for v in k) / (3 * 255.0), m.get("glow") or 0
                tri = [corner[v] for v in k]
                d = depth_of(tri)
                # a triangle (partly) under the terrain: only its parts over the ground show (the depth test); the ground under a sea or lake
                # plane is under the water, which does not hide a blended mesh
                pieces = [tri] if d is None or all((ground(q[5], q[6]) or -1e9) <= q[2] + 0.02 for q in tri) else [s_ for s_ in split(tri, 2) if (depth_of(s_) or 0) >= -0.02]
                for sub in pieces:
                    draw_textured(img, tex, [(q[0], q[1]) for q in sub], [(q[3], q[4]) for q in sub], alpha, glow)
    if a.wire:
        draw = ImageDraw.Draw(img, "RGBA")
        for m in meshes:
            if m["layer"] not in layers:
                continue
            ox, oy = m["origin"]
            cx = x0 + w / 2
            ox = cx + (((ox - cx) + period / 2) % period) - period / 2
            P, I = m["positions"], m["indices"]
            colour = {"veins": (255, 220, 120, 70), "rivers": (120, 200, 255, 70), "magicWater": (255, 120, 255, 70)}[m["layer"]]
            for t in range(0, len(I), 3):
                pts = [((ox + P[3 * v] - x0) * a.ppu, (oy + P[3 * v + 1] - y0) * a.ppu) for v in I[t:t + 3]]
                draw.line(pts + [pts[0]], fill=colour, width=1)
    ImageDraw.Draw(img).text((6, 6), "%s  window %.0f,%.0f %gx%g  ppu %g  triangles %s%s" % (os.path.basename(os.path.dirname(a.dump)), x0, y0, w, h, a.ppu,
                                                                                         ", ".join("%s %d" % kv for kv in sorted(counts.items())),
                                                                                         "" if math.isinf(a.hours) else "  hours %g" % a.hours), fill=(255, 255, 255))
    img.save(a.out)
    print(a.out, img.size, counts)
    return 0


if __name__ == "__main__":
    sys.exit(main())
