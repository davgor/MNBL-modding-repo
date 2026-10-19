#!/usr/bin/env python3
"""Build an owned Bannerlord scene slot (scene.xscene, terrain.bin, navmesh.bin, flora.bin, atmosphere.xml).

Diagnostic builder for the native scene-bundle backend. Every binary is synthesised by our own writers
(terrain_bin.build_terrain, navmesh_bin.build_grid_navmesh). The scene.xscene skeleton (environment and
terrain layer definitions, which reference official material/texture names only) and atmosphere.xml are
taken from an installed battle scene at build time and are therefore local test fixtures, not package
content: the production writer must author its own.

Surfaces:
  calibration  asymmetric analytic test surface (distinct corners, off-centre peak, channel, ridge)
  fwgc:<path>  an MNBLHF01 heightfield.bin from worldbridge, vertically scaled for a diagnostic map

Writes <out>/<slot>/... and <out>/<slot>.expected.json (sample points with the heights the engine should
report: bilinear and two-triangle interpolations of the quantised grid).
"""
import argparse
import hashlib
import json
import math
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import navmesh_bin  # noqa: E402
import terrain_bin  # noqa: E402

GAME = r"D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord"
TEMPLATE_SCENE = "Modules/SandBoxCore/SceneObj/battle_terrain_001"
PLAIN, MOUNTAIN = 1, 7


def calibration(x, y):
    h = 2.0 + 0.02 * x + 0.05 * y
    h += 30.0 * math.exp(-((x - 160.0) ** 2 + (y - 90.0) ** 2) / (2 * 25.0 ** 2))
    h -= 6.0 * math.exp(-((y - 0.6 * x - 20.0) ** 2) / (2 * 6.0 ** 2))
    if 30.0 <= y <= 220.0:
        h += 15.0 * math.exp(-((x - 200.0) ** 2) / (2 * 4.0 ** 2))
    return h


def read_heightfield(path):
    data = open(path, "rb").read()
    if data[:8] != b"MNBLHF01":
        raise ValueError("not an MNBLHF01 heightfield: " + path)
    side = struct.unpack_from("<I", data, 8)[0]
    values = struct.unpack_from("<%dd" % (side * side), data, 12)
    return side, [list(values[r * side:(r + 1) * side]) for r in range(side)]


def surface_grid(kind, nx, ny, cells, node_size, vertical_scale):
    width, height = nx * cells + 1, ny * cells + 1
    spacing = node_size / cells
    if kind == "calibration":
        return [[calibration(c * spacing, r * spacing) for c in range(width)] for r in range(height)], {"kind": kind}
    if kind.startswith("fwgc:"):
        source = kind[5:]
        side, field = read_heightfield(source)
        if side != width or side != height:
            raise ValueError("heightfield side %d does not match %dx%d grid" % (side, width, height))
        sea = 0.0
        grid = [[sea + v * vertical_scale for v in row] for row in field]
        return grid, {"kind": "fwgc", "source": source, "sha256": hashlib.sha256(open(source, "rb").read()).hexdigest(),
                      "verticalScale": vertical_scale}
    raise ValueError("unknown surface " + kind)


def slope_weights(grid, spacing, threshold=0.6):
    """Layer 1 (rock) where the local gradient exceeds threshold (rise/run)."""
    h, w = len(grid), len(grid[0])
    out = []
    for r in range(h):
        row = []
        for c in range(w):
            dx = (grid[r][min(c + 1, w - 1)] - grid[r][max(c - 1, 0)]) / (2 * spacing)
            dy = (grid[min(r + 1, h - 1)][c] - grid[max(r - 1, 0)][c]) / (2 * spacing)
            s = math.hypot(dx, dy)
            row.append(max(0, min(255, int(round((s - threshold) / threshold * 255)))))
        out.append(row)
    return out


def interpolate(grid, x, y, spacing):
    c, r = x / spacing, y / spacing
    c0, r0 = min(int(c), len(grid[0]) - 2), min(int(r), len(grid) - 2)
    fc, fr = c - c0, r - r0
    h00, h10, h01, h11 = grid[r0][c0], grid[r0][c0 + 1], grid[r0 + 1][c0], grid[r0 + 1][c0 + 1]
    bilinear = h00 * (1 - fc) * (1 - fr) + h10 * fc * (1 - fr) + h01 * (1 - fc) * fr + h11 * fc * fr
    # Two triangles split along (c0,r0)-(c0+1,r0+1) and along the other diagonal.
    tri_a = h00 + (h10 - h00) * fc + (h11 - h10) * fr if fc >= fr else h00 + (h11 - h01) * fc + (h01 - h00) * fr
    tri_b = h00 + (h10 - h00) * fc + (h01 - h00) * fr if fc + fr <= 1 else h11 + (h01 - h11) * (1 - fc) + (h10 - h11) * (1 - fr)
    return bilinear, tri_a, tri_b


def scene_xscene(template_text, slot, info, nx, ny, node_size, flora_rect):
    text = template_text
    text = re.sub(r'<scene name="[^"]*"', '<scene name="%s"' % slot, text, count=1)
    text = re.sub(r"<entities>.*?</entities>", "<entities>\n\t</entities>", text, count=1, flags=re.S)
    text = re.sub(r"<flora_bounding_rect[^>]*/>",
                  '<flora_bounding_rect min="%.3f, %.3f" max="%.3f, %.3f"/>' % flora_rect, text, count=1)
    extent_x, extent_y = nx * node_size, ny * node_size
    text = re.sub(r'(<property name="physics_world_min" value=")[^"]*"', r'\g<1>%.3f, %.3f"' % (-50.0, -50.0), text, count=1)
    text = re.sub(r'(<property name="physics_world_max_" value=")[^"]*"', r'\g<1>%.3f, %.3f"' % (extent_x + 50, extent_y + 50), text, count=1)

    def terrain_attrs(match):
        tag = match.group(0)
        for name, value in (("node_dimension_x", "%d" % nx), ("node_dimension_y", "%d" % ny),
                            ("node_size", "%.3f" % node_size), ("min_height", "%.3f" % info["min_height"]),
                            ("max_height", "%.3f" % info["max_height"]), ("vista_tileset", "")):
            tag, count = re.subn(r'%s="[^"]*"' % name, '%s="%s"' % (name, value), tag)
            if count != 1:
                raise ValueError("template <terrain> lacks attribute " + name)
        return tag

    text = re.sub(r"<terrain [^>]*>", terrain_attrs, text, count=1)
    text = re.sub(r"\t*<nodes>.*?</nodes>", terrain_bin.nodes_xml(info), text, count=1, flags=re.S)
    return text


def build(args):
    nx = ny = args.nodes
    cells = 1 << args.level
    spacing = args.node_size / cells
    grid, source = surface_grid(args.surface, nx, ny, cells, args.node_size, args.vertical_scale)
    weights = {1: slope_weights(grid, spacing)}
    tbin, info = terrain_bin.build_terrain(grid, nx, ny, args.node_size, layer_weights=weights, physics_layers=[0, 1])
    quantized = terrain_bin.decode_heights(terrain_bin.parse(tbin), info["min_height"], info["max_height"])

    # Navmesh: coarser grid on the quantised terrain, lifted like official meshes (0.1-0.4 m above ground).
    step = max(1, int(round(args.nav_cell / spacing)))
    nav_rows = [[quantized[r][c] for c in range(0, len(grid[0]), step)] for r in range(0, len(grid), step)]
    nav_cell = step * spacing

    def face_id(i, j):
        corners = (nav_rows[j][i], nav_rows[j][i + 1], nav_rows[j + 1][i], nav_rows[j + 1][i + 1])
        return MOUNTAIN if (max(corners) - min(corners)) / nav_cell > args.mountain_slope else PLAIN

    mesh = navmesh_bin.build_grid_navmesh(nav_rows, nav_cell, face_id, triangulate="auto", z_offset=0.2)
    problems = navmesh_bin.validate(mesh)
    errors = problems.get("errors", []) if isinstance(problems, dict) else list(problems)

    slot_dir = os.path.join(args.out, args.slot)
    os.makedirs(slot_dir, exist_ok=True)
    template = os.path.join(GAME, TEMPLATE_SCENE)
    xml = scene_xscene(open(os.path.join(template, "scene.xscene"), encoding="utf-8").read(), args.slot, info,
                       nx, ny, args.node_size, (0.0, 0.0, nx * args.node_size, ny * args.node_size))
    with open(os.path.join(slot_dir, "scene.xscene"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(xml)
    with open(os.path.join(slot_dir, "terrain.bin"), "wb") as handle:
        handle.write(tbin)
    navmesh_bin.write_scene_navmesh(mesh, os.path.join(slot_dir, "navmesh.bin"))
    with open(os.path.join(slot_dir, "flora.bin"), "wb") as handle:
        handle.write(b"FLR2" + struct.pack("<II", 4, 0))
    with open(os.path.join(template, "atmosphere.xml"), "rb") as src, open(os.path.join(slot_dir, "atmosphere.xml"), "wb") as dst:
        dst.write(src.read())

    extent = nx * args.node_size
    fractions = [0.05, 0.27, 0.5, 0.73, 0.95]
    points = [(round(fx * extent, 3), round(fy * extent, 3)) for fy in fractions for fx in fractions]
    points += [(160.0, 90.0), (200.0, 120.0), (100.0, 80.0)] if args.surface == "calibration" else []
    expected = []
    for x, y in points:
        bilinear, tri_a, tri_b = interpolate(quantized, x, y, spacing)
        entry = {"x": x, "y": y, "bilinear": bilinear, "triangleA": tri_a, "triangleB": tri_b}
        if args.surface == "calibration":
            entry["analytic"] = calibration(x, y)
        expected.append(entry)
    stats = navmesh_bin.mesh_stats(mesh)
    files = {}
    for name in sorted(os.listdir(slot_dir)):
        path = os.path.join(slot_dir, name)
        files[name] = {"bytes": os.path.getsize(path), "sha256": hashlib.sha256(open(path, "rb").read()).hexdigest()}
    report = {"slot": args.slot, "surface": source, "nodes": [nx, ny], "nodeSize": args.node_size,
              "heightmapLevel": args.level, "spacing": spacing, "extent": [extent, extent],
              "minHeight": info["min_height"], "maxHeight": info["max_height"],
              "navmesh": {"cell": nav_cell, "faces": stats.get("faces") if isinstance(stats, dict) else None,
                          "stats": stats if isinstance(stats, dict) else str(stats), "validationErrors": [str(e) for e in errors]},
              "files": files, "points": expected,
              "fixtureNote": "scene.xscene skeleton/layers and atmosphere.xml copied from installed battle_terrain_001 for local testing only"}
    with open(os.path.join(args.out, args.slot + ".expected.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=1)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--slot", required=True)
    parser.add_argument("--out", default="build/slots")
    parser.add_argument("--surface", default="calibration")
    parser.add_argument("--nodes", type=int, default=4)
    parser.add_argument("--level", type=int, default=6)
    parser.add_argument("--node-size", type=float, default=64.0)
    parser.add_argument("--vertical-scale", type=float, default=0.01)
    parser.add_argument("--nav-cell", type=float, default=8.0)
    parser.add_argument("--mountain-slope", type=float, default=1.0)
    args = parser.parse_args()
    report = build(args)
    print(json.dumps({k: report[k] for k in ("slot", "nodes", "nodeSize", "minHeight", "maxHeight", "files")}, indent=1))
    print("navmesh:", json.dumps(report["navmesh"])[:400])


if __name__ == "__main__":
    main()
