#!/usr/bin/env python3
"""MMR-249: build the fixture scene slots for the map epic 2 in-game probe (`map_epic2` autorun probe).

Every slot is a 4 x 4-node terrain of 400-unit nodes (extent 1600, as a production world). Binaries are written by this
repository's own writers (terrain_bin.build_terrain, navmesh_bin.build_grid_navmesh, the FLR2 flora writer below). The
scene.xscene skeleton and atmosphere.xml come from the installed battle_terrain_001, and the layer blocks are assembled
from texture sets that installed vanilla scenes already use as terrain layers, so every texture name is one the engine
loads. These are local test fixtures (build/ output), never package content, as for build_slot.py.

Slots:
  mnbl_probe_l2      production template layers (grass + rock_k by slope), the cost baseline and the decal scene
  mnbl_probe_l6      6 weighted layers: plain colour ramps and a two-layer overlap (compositing), a slope-alpha ramp
                     against a plain ramp (height blend), two scale patches (tile size) and 1-/2-sample stripes
  mnbl_probe_l15     15 layers in every node: 12 biome textures, 2 tint modes, base (look, tints, cost at 15)
  mnbl_probe_snow    the production template with use_dynamic_weather_effects="true" (dynamic snow, seasons)
  mnbl_probe_snowf   as mnbl_probe_snow plus dynamic_flowmap_texture_name="main_map_snow_flowmap" (vanilla Main_map's pair)
  mnbl_probe_snowfr  as mnbl_probe_snowf plus terrain_render_version="1" (run 2; never became ready to render)
  mnbl_probe_snowp   the flag plus dynamic_flowmap_texture_name="mnbl_probe_snow_mask", with that pattern as a PNG in the
                     slot folder for Texture.LoadTextureFromPath before Read (run 3)
  mnbl_probe_forest  the production template plus map trees in flora.bin (look and cost of map trees)
  mnbl_probe_a6      world A as the production encoder writes it today: level 6, Sobel normals, vertical scale 0.01
  mnbl_probe_a6n     the same heights with normals from a smoothed copy of the field
  mnbl_probe_a7      level 7 (513 samples, 128 cells per node): cubic B-spline 2x of the same field, Sobel normals
  mnbl_probe_a7s     level 7 of a shaped field (despike, masked [1,4,1]/6 low-pass) at half the vertical scale,
                     normals from a smoothed copy

Writes <out>/<slot>/{scene.xscene,terrain.bin,navmesh.bin,flora.bin,atmosphere.xml}, <out>/probe-slots.json (sizes,
hashes, layer tables, the expected weight patterns) and <out>/probe-args.json (the ArgsJson for Start-OwnedProbe).
Usage: python tools/scenebin/build_probe_slots.py --bundle <world A bundle dir> [--out build/probe-slots] [--only a,b]
"""
import argparse
import hashlib
import json
import math
import os
import random
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import navmesh_bin  # noqa: E402
import terrain_bin  # noqa: E402

GAME = r"D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord"
TEMPLATE_SCENE = "Modules/SandBoxCore/SceneObj/battle_terrain_001"
NODES, NODE_SIZE, EXTENT = 4, 400.0, 1600.0
SEASONS = ("summer", "fall", "winter", "spring")
FLAGS = ("use_parallax", "use_displacement_mapping", "use_randomized_normalmap", "use_transparency_of_diffuse_alpha",
         "use_transparency_of_ground_slope", "use_dxt5_normal", "use_six_side_uv_mapping")

# Diffuse name -> an installed scene whose terrain layer uses it (texture set and physics material are read from there).
SOURCES = {
    "plain_black": "SandBox/scn_cutscene_declare_dragon_banner", "plain_magenta": "Native/mp_skirmish_map_006",
    "plain_green": "NavalDLC/nord_castle_b", "ground_grass_b_d_mainmap": "NavalDLC/Main_map",
    "desert_floor_a": "Native/benchmark_battle_11", "desert_floor_c_d": "Native/mp_sergeant_map_018",
    "ground_drygrass_a_d": "Native/mp_battle_map_001", "ground_forest_b_d_main_map": "Native/mp_siege_map_011_b",
    "forest_a_d": "Native/mp_duel_mode_map_004", "ground_mud_b_d": "Native/mp_duel_mode_map_004_w",
    "ground_beach_a_d": "Native/benchmark_battle_11", "ground_nord_grass_a_d": "NavalDLC/arena_nord_a",
    "main_map_mountains_d": "NavalDLC/Main_map", "rock_k_d": "Native/mp_duel_002", "ice_a_d": "Native/mp_duel_mode_map_004_w",
    "snow_a_d": "Native/mp_skirmish_map_005",
}

# MMR-249 gallery (mnbl_probe_l15): slot -> (diffuse, albedo mode, albedo colour). Scale 10 for every gallery layer.
GALLERY = [
    ("ground_grass_b_d_mainmap", 0, None), ("desert_floor_a", 0, None), ("desert_floor_c_d", 0, None),
    ("ground_drygrass_a_d", 0, None), ("ground_forest_b_d_main_map", 0, None), ("forest_a_d", 0, None),
    ("ground_mud_b_d", 0, None), ("ground_beach_a_d", 0, None), ("ground_nord_grass_a_d", 0, None),
    ("main_map_mountains_d", 0, None), ("rock_k_d", 0, None), ("ice_a_d", 0, None), ("snow_a_d", 0, None),
    ("desert_floor_a", 1, (0.2, 0.0, 0.0)), ("desert_floor_a", 2, (1.0, 0.55, 0.55)),
]
GALLERY_SCALE = 10.0

# mnbl_probe_l6: slot -> (diffuse, scale, slope alpha). Layer 0 is the opaque base.
LAYERS6 = [("plain_black", 5.0, False), ("plain_magenta", 5.0, False), ("plain_green", 5.0, False),
           ("ground_drygrass_a_d", 5.0, True), ("ground_drygrass_a_d", 20.0, False), ("rock_k_d", 15.0, False)]

TREE_KINDS = [("map_spruce_mix", 3), ("worldmap_tree_beech_01", 1), ("worldmap_tree_acacia_01", 1)]


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


# ---------------------------------------------------------------------------------------------------------------------
# Layer XML
# ---------------------------------------------------------------------------------------------------------------------
def harvest_texture_sets():
    out = {}
    for diffuse, source in SOURCES.items():
        mod, scene = source.split("/")
        text = open(os.path.join(GAME, "Modules", mod, "SceneObj", scene, "scene.xscene"), "rb").read().decode("utf-8", "replace")
        block = text[text.find("<layers"):text.find("</layers>")]
        for m in re.finditer(r'<layer name="[^"]*">(.*?)</layer>', block, re.S):
            summer = re.search(r"<summer ([^>]*)>(.*?)</summer>", m.group(1), re.S)
            if not summer:
                continue
            tex = dict(re.findall(r'<texture type="(\w+)" name="([^"]*)"', summer.group(2)))
            if tex.get("diffusemap") != diffuse:
                continue
            physics = re.search(r'physics_material="([^"]*)"', summer.group(1))
            out[diffuse] = {"textures": tex, "physics": physics.group(1) if physics else "grass", "source": source}
            break
        if diffuse not in out:
            raise SystemExit("no installed layer uses diffuse " + diffuse + " in " + source)
    return out


def layer_xml(name, tset, scale, slope_alpha=False, albedo_mode=0, albedo_color=None, smooth=1.0):
    tex = tset["textures"]
    order = ("diffusemap", "areamap", "normalmap", "specularmap", "splattingmap", "displacementmap")
    values = {"areamap": "none", "splattingmap": "none"}
    for kind in ("diffusemap", "normalmap", "specularmap", "displacementmap"):
        values[kind] = tex.get(kind) or "none"
    color = albedo_color or (1.0, 1.0, 1.0)
    attrs = ('is_flora_layer="false" is_mesh_blend_layer="false" pitch_roll_yaw="0.000, 0.000, 0.000" scale="%.3f, %.3f" '
             'shear="0.000, 0.000" position_offset="0.000, 0.000" physics_material="%s" detail_level_adjustment="0" '
             'elevation_amount="0.000" parallax_amount="0.000" ground_slope_scale="1.000" bigdetailmap_mode="0" '
             'bigdetailmap_weight="0.000" albedo_factor_mode="%d" albedo_factor_color="%.3f, %.3f, %.3f" smooth_blend_amount="%.3f"'
             % (scale, scale, tset["physics"], albedo_mode, color[0], color[1], color[2], smooth))
    seasons = []
    for season in SEASONS:
        lines = ['\t\t\t\t<%s is_enabled="true" name="%s" %s>' % (season, name, attrs), "\t\t\t\t\t<textures>"]
        lines += ['\t\t\t\t\t\t<texture type="%s" name="%s"/>' % (kind, values[kind]) for kind in order]
        lines += ["\t\t\t\t\t</textures>", "\t\t\t\t\t<layer_flags>"]
        lines += ['\t\t\t\t\t\t<flag name="%s" value="%s"/>' % (f, "true" if (f == "use_transparency_of_ground_slope" and slope_alpha) else "false") for f in FLAGS]
        lines += ["\t\t\t\t\t</layer_flags>", "\t\t\t\t\t<meshes>", "\t\t\t\t\t</meshes>", "\t\t\t\t</%s>" % season]
        seasons.append("\n".join(lines))
    return '\t\t\t<layer name="%s">\n%s\n\t\t\t</layer>' % (name, "\n".join(seasons))


def layers_block(layers):
    return '<layers version="1">\n' + "\n".join(layers) + "\n\t\t</layers>"


# ---------------------------------------------------------------------------------------------------------------------
# Scene XML (the build_slot.py substitution, plus our layer block, terrain flags and per-season masks)
# ---------------------------------------------------------------------------------------------------------------------
def scene_xml(template, slot, info, layers=None, dynamic_weather=False, season_masks=None):
    text = template
    text = re.sub(r'<scene name="[^"]*"', '<scene name="%s"' % slot, text, count=1)
    text = re.sub(r"<entities>.*?</entities>", "<entities>\n\t</entities>", text, count=1, flags=re.S)
    text = re.sub(r"<flora_bounding_rect[^>]*/>", '<flora_bounding_rect min="0.000, 0.000" max="%.3f, %.3f"/>' % (EXTENT, EXTENT), text, count=1)
    text = re.sub(r'(<property name="physics_world_min" value=")[^"]*"', r'\g<1>-50.000, -50.000"', text, count=1)
    text = re.sub(r'(<property name="physics_world_max_" value=")[^"]*"', r'\g<1>%.3f, %.3f"' % (EXTENT + 50, EXTENT + 50), text, count=1)

    def terrain_attrs(match):
        tag = match.group(0)
        for name, value in (("node_dimension_x", "%d" % NODES), ("node_dimension_y", "%d" % NODES),
                            ("node_size", "%.3f" % NODE_SIZE), ("min_height", "%.3f" % info["min_height"]),
                            ("max_height", "%.3f" % info["max_height"]), ("vista_tileset", "")):
            tag, count = re.subn(r'%s="[^"]*"' % name, '%s="%s"' % (name, value), tag)
            if count != 1:
                raise ValueError("template <terrain> lacks attribute " + name)
        if dynamic_weather:
            # True: the flag alone; a string: extra terrain attributes after the flag (MMR-249 run 2 snow variants).
            extra = dynamic_weather if isinstance(dynamic_weather, str) else ""
            tag = tag[:-1] + ' use_dynamic_weather_effects="true"' + extra + '>'
        return tag

    text = re.sub(r"<terrain [^>]*>", terrain_attrs, text, count=1)
    if layers is not None:
        text, n = re.subn(r'<layers version="1">.*?</layers>', lambda m: layers_block(layers), text, count=1, flags=re.S)
        if n != 1:
            raise ValueError("template has no layer block")
    nodes = terrain_bin.nodes_xml(info)
    if season_masks:
        def node(m):
            key = (int(m.group(1)), int(m.group(2)))
            line = m.group(0)
            summer = int(re.search(r'layer_is_used_mask_summer="(\d+)"', line).group(1))
            for season, drop in season_masks.get(key, {}).items():
                line = re.sub(r'layer_is_used_mask_%s="\d+"' % season, 'layer_is_used_mask_%s="%d"' % (season, summer & ~drop), line)
            return line
        nodes = re.sub(r'<node idx="(\d+)" idy="(\d+)"[^>]*>', node, nodes)
    text = re.sub(r"\t*<nodes>.*?</nodes>", lambda m: nodes, text, count=1, flags=re.S)
    return text


# ---------------------------------------------------------------------------------------------------------------------
# Fields
# ---------------------------------------------------------------------------------------------------------------------
def synthetic_heights(side):
    spacing = EXTENT / (side - 1)
    grid = []
    for r in range(side):
        y = r * spacing
        row = []
        for c in range(side):
            x = c * spacing
            h = 2.0 + 50.0 * math.exp(-((x - 1400.0) ** 2 + (y - 200.0) ** 2) / (2 * 35.0 ** 2))
            h += 20.0 * math.exp(-((x - 1400.0) ** 2 + (y - 600.0) ** 2) / (2 * 60.0 ** 2))
            row.append(h)
        grid.append(row)
    return grid


def slope_rock(grid, spacing, threshold=0.6):
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


def ramp(il, end=48):
    return int(round(255.0 * min(1.0, il / float(end))))


def l6_weights(side):
    """The per-node pattern on node-local sample indices (global index mod 64); see the module docstring."""
    cells = side - 1
    per = cells // NODES
    w = {k: [[0] * side for _ in range(side)] for k in range(1, 6)}
    for r in range(side):
        jl = r % per
        for c in range(side):
            il = c % per
            if jl < 32:
                w[1][r][c] = ramp(il)
            if 16 <= jl < 32:
                w[2][r][c] = 128
            if 32 <= jl < 48:
                w[3][r][c] = ramp(il)
            if jl >= 48:
                w[4][r][c] = ramp(il)
            if il < 4:
                w[5][r][c] = 255 if jl % 4 < 2 else 0
            elif il < 8:
                w[5][r][c] = 255 if jl % 8 < 4 else 0
    return w


def l15_weights(side):
    """Layer k (1..14) is a 4-sample band at node-local columns 4k..4k+3, in every row of every node."""
    cells = side - 1
    per = cells // NODES
    w = {k: [[0] * side for _ in range(side)] for k in range(1, 15)}
    for r in range(side):
        for c in range(side):
            k = (c % per) // 4
            if 1 <= k <= 14:
                w[k][r][c] = 255
    return w


def read_mnbl(path, magic, count_per_sample, fmt):
    data = open(path, "rb").read()
    if data[:8] != magic:
        raise ValueError("%s is not %s" % (path, magic.decode()))
    side = struct.unpack_from("<I", data, 8)[0]
    n = side * side * count_per_sample
    values = struct.unpack_from("<%d%s" % (n, fmt), data, 12)
    return side, values


def world_fields(bundle):
    terrain = os.path.join(bundle, "compiled", "terrain")
    side, h = read_mnbl(os.path.join(terrain, "heightfield.bin"), b"MNBLHF01", 1, "d")
    wside, wt = read_mnbl(os.path.join(terrain, "weights.bin"), b"MNBLWT01", 1, "B")
    if side != 257 or wside != 257:
        raise ValueError("expected 257-sample compiled terrain")
    heights = [list(h[r * side:(r + 1) * side]) for r in range(side)]
    rock = [list(wt[r * side:(r + 1) * side]) for r in range(side)]
    return heights, rock


def bspline2x(grid):
    """Uniform cubic B-spline evaluated at every half sample (clamped ends): 2n-1 samples per axis."""
    def axis(rows):
        out = []
        for row in rows:
            n = len(row)
            p = lambda i: row[min(max(i, 0), n - 1)]
            line = []
            for i in range(n):
                line.append((p(i - 1) + 4 * p(i) + p(i + 1)) / 6.0)
                if i + 1 < n:
                    line.append((p(i - 1) + 23 * p(i) + 23 * p(i + 1) + p(i + 2)) / 48.0)
            out.append(line)
        return out
    first = axis(grid)
    cols = [list(c) for c in zip(*first)]
    return [list(r) for r in zip(*axis(cols))]


def bilinear2x(grid):
    n = len(grid)
    out = [[0.0] * (2 * n - 1) for _ in range(2 * n - 1)]
    for r in range(2 * n - 1):
        r0, fr = r // 2, (r % 2) * 0.5
        r1 = min(r0 + 1, n - 1)
        for c in range(2 * n - 1):
            c0, fc = c // 2, (c % 2) * 0.5
            c1 = min(c0 + 1, n - 1)
            out[r][c] = (grid[r0][c0] * (1 - fc) * (1 - fr) + grid[r0][c1] * fc * (1 - fr)
                         + grid[r1][c0] * (1 - fc) * fr + grid[r1][c1] * fc * fr)
    return out


def smooth3(grid, passes=2):
    """[1,2,1]/4 separable smoothing, edges clamped: the field the smoothed normals are built from."""
    g = [row[:] for row in grid]
    n, m = len(g), len(g[0])
    for _ in range(passes):
        t = [[(g[r][max(c - 1, 0)] + 2 * g[r][c] + g[r][min(c + 1, m - 1)]) / 4.0 for c in range(m)] for r in range(n)]
        g = [[(t[max(r - 1, 0)][c] + 2 * t[r][c] + t[min(r + 1, n - 1)][c]) / 4.0 for c in range(m)] for r in range(n)]
    return g


def shape(grid, vertical=0.5):
    """A preview of MMR-278's light shaping: lower the vertical scale, despike land, masked [1,4,1]/6 low-pass on land."""
    g = [[v * vertical for v in row] for row in grid]
    n, m = len(g), len(g[0])
    spikes = 0
    out = [row[:] for row in g]
    for r in range(1, n - 1):
        for c in range(1, m - 1):
            v = g[r][c]
            if v <= 0:
                continue
            ring = [g[r + dr][c + dc] for dr in (-1, 0, 1) for dc in (-1, 0, 1) if dr or dc]
            hi = max(ring)
            if v > hi:
                out[r][c] = hi
                spikes += 1
    g = out
    land = [[v > 0.05 for v in row] for row in g]
    t = [[(g[r][max(c - 1, 0)] + 4 * g[r][c] + g[r][min(c + 1, m - 1)]) / 6.0 if land[r][c] else g[r][c] for c in range(m)] for r in range(n)]
    g = [[(t[max(r - 1, 0)][c] + 4 * t[r][c] + t[min(r + 1, n - 1)][c]) / 6.0 if land[r][c] else t[r][c] for c in range(m)] for r in range(n)]
    return g, spikes


def slope_stats(grid, spacing, land_only=True):
    n, m = len(grid), len(grid[0])
    slopes = []
    for r in range(n - 1):
        for c in range(m - 1):
            if land_only and grid[r][c] <= 0.05:
                continue
            dx = (grid[r][c + 1] - grid[r][c]) / spacing
            dy = (grid[r + 1][c] - grid[r][c]) / spacing
            slopes.append(math.degrees(math.atan(math.hypot(dx, dy))))
    slopes.sort()
    if not slopes:
        return {}
    return {"p50": round(slopes[len(slopes) // 2], 2), "p95": round(slopes[int(len(slopes) * .95)], 2), "max": round(slopes[-1], 2), "samples": len(slopes)}


def features(grid, spacing):
    """A mountain (highest land sample) and a steep coast (largest land-to-sea drop) for the world views."""
    n = len(grid)
    best, coast = None, None
    for r in range(8, n - 8):
        for c in range(8, n - 8):
            v = grid[r][c]
            if best is None or v > best[0]:
                best = (v, c, r)
            if v > 0.5:
                for dr, dc in ((0, 1), (1, 0), (0, -1), (-1, 0)):
                    w = grid[r + dr][c + dc]
                    if w < 0 and (coast is None or v - w > coast[0]):
                        coast = (v - w, c, r)
    return {"mountain": [best[1] * spacing, best[2] * spacing, best[0]], "coast": [coast[1] * spacing, coast[2] * spacing, coast[0]]}


# ---------------------------------------------------------------------------------------------------------------------
# Flora (FLR2: u32 payload bytes, u32 count, then per instance u32 name length, name, u32 variation, 4x4 f32 matrix rows
# (rotation x scale, then position, 1), 4 f32 colour). Layout read from the installed Main_map flora.bin (69,500 trees).
# ---------------------------------------------------------------------------------------------------------------------
def flora_bin(instances):
    body = bytearray()
    for name, variation, x, y, z, yaw, scale in instances:
        raw = name.encode()
        cs, sn = math.cos(yaw) * scale, math.sin(yaw) * scale
        body += struct.pack("<I", len(raw)) + raw + struct.pack("<I", variation)
        body += struct.pack("<16f", cs, sn, 0, 0, -sn, cs, 0, 0, 0, 0, scale, 0, x, y, z, 1)
        body += struct.pack("<4f", 1, 1, 1, 1)
    return b"FLR2" + struct.pack("<II", len(body) + 4, len(instances)) + bytes(body)


def forest_instances(height_at):
    rng = random.Random(249)
    out = []
    # Dense (vanilla-like ~0.45 trees per unit^2) in node (0,0); sparse (0.1) in node (1,0).
    for (x0, y0, density) in ((0.0, 0.0, 0.45), (400.0, 0.0, 0.10)):
        count = int(400 * 400 * density)
        for _ in range(count):
            x, y = x0 + rng.random() * 400.0, y0 + rng.random() * 400.0
            kind, variations = TREE_KINDS[0] if rng.random() < 0.7 else TREE_KINDS[1] if rng.random() < 0.9 else TREE_KINDS[2]
            out.append((kind, rng.randrange(variations), x, y, height_at(x, y), rng.random() * 2 * math.pi, 0.18 + rng.random() * 0.14))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# Slots
# ---------------------------------------------------------------------------------------------------------------------
def write_slot(out, slot, template_text, atmosphere, grid, weights, physics_layers=None, layers=None, dynamic_weather=False,
               season_masks=None, flora=None, normal_source=None):
    tbin, info = terrain_bin.build_terrain(grid, NODES, NODES, NODE_SIZE, layer_weights=weights, physics_layers=physics_layers,
                                           normal_heights=normal_source)
    slot_dir = os.path.join(out, slot)
    os.makedirs(slot_dir, exist_ok=True)
    with open(os.path.join(slot_dir, "scene.xscene"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(scene_xml(template_text, slot, info, layers, dynamic_weather, season_masks))
    with open(os.path.join(slot_dir, "terrain.bin"), "wb") as handle:
        handle.write(tbin)
    quantized = terrain_bin.decode_heights(terrain_bin.parse(tbin), info["min_height"], info["max_height"])
    step = max(1, (len(grid) - 1) // 32)
    nav_rows = [[quantized[r][c] for c in range(0, len(grid[0]), step)] for r in range(0, len(grid), step)]
    mesh = navmesh_bin.build_grid_navmesh(nav_rows, step * EXTENT / (len(grid) - 1), lambda i, j: 1, triangulate="auto", z_offset=0.2)
    navmesh_bin.write_scene_navmesh(mesh, os.path.join(slot_dir, "navmesh.bin"))
    with open(os.path.join(slot_dir, "flora.bin"), "wb") as handle:
        handle.write(flora_bin(flora or []))
    with open(os.path.join(slot_dir, "atmosphere.xml"), "wb") as handle:
        handle.write(atmosphere)
    files = {n: {"bytes": os.path.getsize(os.path.join(slot_dir, n)), "sha256": sha(os.path.join(slot_dir, n))} for n in sorted(os.listdir(slot_dir))
             if os.path.isfile(os.path.join(slot_dir, n))}
    masks = sorted(set(n["layer_mask"] for n in info["nodes"]))
    return {"slot": slot, "heightmapLevel": info["heightmap_level"], "samples": info["resolution"] * NODES - (NODES - 1),
            "spacing": info["spacing"], "minHeight": info["min_height"], "maxHeight": info["max_height"],
            "nodeLayerMasks": masks, "layersPerNode": sorted(set(bin(m).count("1") for m in masks)),
            "floraInstances": len(flora or []), "dynamicWeather": dynamic_weather, "files": files}


def bilinear_at(grid, x, y):
    n = len(grid)
    s = EXTENT / (n - 1)
    c, r = min(x / s, n - 1.001), min(y / s, n - 1.001)
    c0, r0 = int(c), int(r)
    fc, fr = c - c0, r - r0
    return (grid[r0][c0] * (1 - fc) * (1 - fr) + grid[r0][c0 + 1] * fc * (1 - fr) + grid[r0 + 1][c0] * (1 - fc) * fr
            + grid[r0 + 1][c0 + 1] * fc * fr)


def map38(target_xy, z, name):
    """The campaign camera at its default distance 38 (MapCameraView: elevation 0.0075 d + 0.35, vertical FOV 0.6981)."""
    d = 38.0
    e = d * 0.0075 + 0.35
    tx, ty = target_xy
    return {"name": name, "eye": [tx, ty - d * math.cos(e), z + d * math.sin(e)], "target": [tx, ty, z], "fov": 0.69813168, "kind": "map38"}


def topdown(center_xy, z, height, name, fov=0.6):
    return {"name": name, "eye": [center_xy[0], center_xy[1], z + height], "target": [center_xy[0], center_xy[1], z], "fov": fov, "kind": "topdown"}


def overview(name="overview"):
    return {"name": name, "eye": [800.0, -350.0, 1150.0], "target": [800.0, 800.0, 0.0], "fov": 0.9, "kind": "oblique"}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bundle", required=True, help="world A bundle directory (compiled/terrain/heightfield.bin, weights.bin)")
    ap.add_argument("--out", default="build/probe-slots")
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    only = set(s for s in a.only.split(",") if s)
    template_dir = os.path.join(GAME, TEMPLATE_SCENE)
    template = open(os.path.join(template_dir, "scene.xscene"), encoding="utf-8").read().replace("\r\n", "\n")
    atmosphere = open(os.path.join(template_dir, "atmosphere.xml"), "rb").read()
    sets = harvest_texture_sets()
    os.makedirs(a.out, exist_ok=True)
    report = {"schema": "mnbl.probe-slots", "version": 1, "ticket": "MMR-249", "extent": EXTENT, "nodes": NODES, "nodeSize": NODE_SIZE,
              "template": {"scene": TEMPLATE_SCENE, "sceneXsceneSha256": sha(os.path.join(template_dir, "scene.xscene"))},
              "textureSets": sets, "slots": {}}
    args = {"scenes": []}

    def want(slot):
        return not only or slot in only

    side6 = NODES * 64 + 1
    spacing6 = EXTENT / (side6 - 1)
    synth = synthetic_heights(side6)
    rock = slope_rock(synth, spacing6)
    flat = 2.0
    hill_z = bilinear_at(synth, 1400.0, 160.0)
    cost_views = [overview(), map38((600.0, 600.0), flat, "map38_flat"), map38((1400.0, 165.0), bilinear_at(synth, 1400.0, 165.0), "map38_hill")]

    if want("mnbl_probe_l2"):
        report["slots"]["mnbl_probe_l2"] = write_slot(a.out, "mnbl_probe_l2", template, atmosphere, synth, {1: rock}, physics_layers=[0, 1])
        args["scenes"].append({"name": "mnbl_probe_l2", "kind": "cost", "views": cost_views})
    if want("mnbl_probe_l6"):
        layers = [layer_xml("p6_%d" % k, sets[d], s, slope) for k, (d, s, slope) in enumerate(LAYERS6)]
        # Per-season masks: even nodes drop layer 1 (magenta) in winter only.
        masks = {}
        for iy in range(NODES):
            for ix in range(NODES):
                if (ix + iy) % 2 == 0:
                    masks[(ix, iy)] = {"winter": 1 << 1}
        report["slots"]["mnbl_probe_l6"] = write_slot(a.out, "mnbl_probe_l6", template, atmosphere, synth, l6_weights(side6),
                                                       physics_layers=[0], layers=layers, season_masks=masks)
        report["slots"]["mnbl_probe_l6"]["layers"] = [{"slot": k, "diffuse": d, "scale": s, "slopeAlpha": slope} for k, (d, s, slope) in enumerate(LAYERS6)]
        report["slots"]["mnbl_probe_l6"]["pattern"] = {
            "nodeLocal": "sample indices il, jl = global index mod 64 (6.25 units each)",
            "layer1": "plain_magenta ramp w = round(255 min(1, il/48)) for jl < 32", "layer2": "plain_green w = 128 for 16 <= jl < 32",
            "layer3": "ground_drygrass_a_d (slope alpha, scale 5) ramp for 32 <= jl < 48", "layer4": "ground_drygrass_a_d (scale 20) ramp for jl >= 48",
            "layer5": "rock_k_d stripes: il < 4 period 4 (2 on), 4 <= il < 8 period 8 (4 on), all rows",
            "winterMask": "nodes with (idx + idy) even have layer 1 removed from layer_is_used_mask_winter"}
        args["scenes"].append({"name": "mnbl_probe_l6", "kind": "layers", "views": cost_views + [
            topdown((600.0, 600.0), flat, 646.0, "node11_topdown"),
            topdown((750.0, 647.0), flat, 160.0, "scale5_topdown"), topdown((750.0, 750.0), flat, 160.0, "scale20_topdown"),
            topdown((200.0, 600.0), flat, 646.0, "node01_topdown"),
            map38((420.0 + 8.0, 650.0), flat, "map38_stripes"), map38((600.0, 450.0), flat, "map38_ramp")]})
    if want("mnbl_probe_l15"):
        layers = [layer_xml("p15_%d" % k, sets[d], GALLERY_SCALE, False, mode, color) for k, (d, mode, color) in enumerate(GALLERY)]
        report["slots"]["mnbl_probe_l15"] = write_slot(a.out, "mnbl_probe_l15", template, atmosphere, synth, l15_weights(side6),
                                                        physics_layers=list(range(15)), layers=layers)
        report["slots"]["mnbl_probe_l15"]["layers"] = [{"slot": k, "diffuse": d, "albedoMode": mode, "albedoColor": color, "scale": GALLERY_SCALE,
                                                        "bandX": [400.0 + 25.0 * k, 400.0 + 25.0 * k + 18.75] if k else None} for k, (d, mode, color) in enumerate(GALLERY)]
        bands = [map38((400.0 + 25.0 * k + 9.375, 600.0), flat, "band%02d" % k) for k in range(1, 15)]
        args["scenes"].append({"name": "mnbl_probe_l15", "kind": "gallery", "views": cost_views + [topdown((600.0, 600.0), flat, 646.0, "node11_topdown")] + bands})
    if want("mnbl_probe_forest"):
        trees = forest_instances(lambda x, y: bilinear_at(synth, x, y))
        report["slots"]["mnbl_probe_forest"] = write_slot(a.out, "mnbl_probe_forest", template, atmosphere, synth, {1: rock},
                                                           physics_layers=[0, 1], flora=trees)
        report["slots"]["mnbl_probe_forest"]["trees"] = {"kinds": [k for k, _ in TREE_KINDS], "dense": "node (0,0), 0.45 per unit^2", "sparse": "node (1,0), 0.10 per unit^2",
                                                         "scale": "0.18-0.32 (vanilla Main_map median 0.25)"}
        args["scenes"].append({"name": "mnbl_probe_forest", "kind": "forest", "views": cost_views + [
            map38((200.0, 200.0), flat, "map38_dense"), map38((600.0, 200.0), flat, "map38_sparse"), map38((400.0, 200.0), flat, "map38_edge"),
            {"name": "forest_oblique", "eye": [400.0, -150.0, 260.0], "target": [400.0, 200.0, 0.0], "fov": 0.7, "kind": "oblique"}]})
    if want("mnbl_probe_snow"):
        report["slots"]["mnbl_probe_snow"] = write_slot(a.out, "mnbl_probe_snow", template, atmosphere, synth, {1: rock},
                                                         physics_layers=[0, 1], dynamic_weather=True)
        args["scenes"].append({"name": "mnbl_probe_snow", "kind": "snow", "views": [
            topdown((800.0, 800.0), flat, 1700.0, "snow_topdown", fov=0.9), map38((400.0, 1200.0), flat, "map38_nw"),
            map38((1200.0, 400.0), flat, "map38_se"), map38((1400.0, 165.0), bilinear_at(synth, 1400.0, 165.0), "map38_hill")]})

    snow_views = [topdown((400.0, 1200.0), flat, 600.0, "q_nw", fov=0.9), topdown((1200.0, 1200.0), flat, 600.0, "q_ne", fov=0.9),
                  topdown((400.0, 400.0), flat, 600.0, "q_sw", fov=0.9), topdown((1200.0, 400.0), flat, 600.0, "q_se", fov=0.9),
                  map38((400.0, 1200.0), flat, "map38_nw")]
    for slot, extra in (("mnbl_probe_snowf", ' dynamic_flowmap_texture_name="main_map_snow_flowmap"'),
                        ("mnbl_probe_snowfr", ' terrain_render_version="1" dynamic_flowmap_texture_name="main_map_snow_flowmap"')):
        if want(slot):
            report["slots"][slot] = write_slot(a.out, slot, template, atmosphere, synth, {1: rock}, physics_layers=[0, 1], dynamic_weather=extra)
            args["scenes"].append({"name": slot, "kind": "snow2", "views": snow_views})

    if want("mnbl_probe_snowp"):
        report["slots"]["mnbl_probe_snowp"] = write_slot(a.out, "mnbl_probe_snowp", template, atmosphere, synth, {1: rock}, physics_layers=[0, 1],
                                                          dynamic_weather=' dynamic_flowmap_texture_name="mnbl_probe_snow_mask"')
        mask = bytearray()
        for y in range(256):
            for x in range(256):
                v = 255 if (y < 128 and x < 128) or (200 <= y < 216 and x < 64) else (2 * (x - 128) if (y >= 128 and x >= 128) else 0)
                mask += bytes((v, 0, 0, 255))
        with open(os.path.join(a.out, "mnbl_probe_snowp", "mnbl_probe_snow_mask.png"), "wb") as handle:
            handle.write(terrain_bin.png_encode(256, 256, 8, 6, bytes(mask)))
        report["slots"]["mnbl_probe_snowp"]["snowMask"] = "mnbl_probe_snow_mask.png: the probe's runtime red pattern (rows first, top row first)"

    if any(want(s) for s in ("mnbl_probe_a6", "mnbl_probe_a6n", "mnbl_probe_a7", "mnbl_probe_a7s")):
        heights, wrock = world_fields(a.bundle)
        spacing = EXTENT / 256
        feat = features(heights, spacing)
        report["worldA"] = {"bundle": os.path.abspath(a.bundle), "heightfieldSha256": sha(os.path.join(a.bundle, "compiled", "terrain", "heightfield.bin")),
                            "features": feat, "slope": {"level6": slope_stats(heights, spacing)}}
        mz = feat["mountain"]
        cz = feat["coast"]

        def world_views(grid):
            return [overview(), map38((mz[0], mz[1] - 12.0), bilinear_at(grid, mz[0], mz[1] - 12.0), "map38_mountain"),
                    map38((cz[0], cz[1]), max(0.0, bilinear_at(grid, cz[0], cz[1])), "map38_coast"),
                    {"name": "mountain_oblique", "eye": [mz[0], mz[1] - 110.0, mz[2] + 70.0], "target": [mz[0], mz[1], bilinear_at(grid, mz[0], mz[1])], "fov": 0.7, "kind": "oblique"}]
        if want("mnbl_probe_a6"):
            report["slots"]["mnbl_probe_a6"] = write_slot(a.out, "mnbl_probe_a6", template, atmosphere, heights, {1: wrock}, physics_layers=[0, 1])
            args["scenes"].append({"name": "mnbl_probe_a6", "kind": "world", "views": world_views(heights)})
        if want("mnbl_probe_a6n"):
            report["slots"]["mnbl_probe_a6n"] = write_slot(a.out, "mnbl_probe_a6n", template, atmosphere, heights, {1: wrock}, physics_layers=[0, 1],
                                                            normal_source=smooth3(heights))
            args["scenes"].append({"name": "mnbl_probe_a6n", "kind": "world", "views": world_views(heights)})
        if want("mnbl_probe_a7") or want("mnbl_probe_a7s"):
            rock7 = [[int(round(v)) for v in row] for row in bilinear2x([[float(v) for v in row] for row in wrock])]
            if want("mnbl_probe_a7"):
                h7 = bspline2x(heights)
                report["worldA"]["slope"]["level7"] = slope_stats(h7, spacing / 2)
                report["slots"]["mnbl_probe_a7"] = write_slot(a.out, "mnbl_probe_a7", template, atmosphere, h7, {1: rock7}, physics_layers=[0, 1])
                args["scenes"].append({"name": "mnbl_probe_a7", "kind": "world", "views": world_views(h7)})
            if want("mnbl_probe_a7s"):
                shaped, spikes = shape(heights)
                h7s = bspline2x(shaped)
                report["worldA"]["shaping"] = {"verticalScale": 0.005, "despikedSamples": spikes, "lowPass": "[1,4,1]/6 on land samples (z > 0.05), separable",
                                               "normals": "Sobel of [1,2,1]/4 x2 smoothed field", "slopeLevel7": slope_stats(h7s, spacing / 2)}
                report["slots"]["mnbl_probe_a7s"] = write_slot(a.out, "mnbl_probe_a7s", template, atmosphere, h7s, {1: rock7}, physics_layers=[0, 1],
                                                                normal_source=smooth3(h7s))
                args["scenes"].append({"name": "mnbl_probe_a7s", "kind": "world", "views": world_views(h7s)})

    # Decals run in mnbl_probe_l2 again after the world scenes, then the snow scene closes the run (riskiest native calls last).
    order = ["mnbl_probe_l2", "mnbl_probe_l6", "mnbl_probe_l15", "mnbl_probe_forest", "mnbl_probe_a6", "mnbl_probe_a6n", "mnbl_probe_a7", "mnbl_probe_a7s"]
    scenes = {s["name"]: s for s in args["scenes"]}
    sequence = [scenes[n] for n in order if n in scenes]
    if "mnbl_probe_l2" in scenes:
        sequence.append({"name": "mnbl_probe_l2", "kind": "decals", "hillZ": hill_z, "views": [
            map38((1400.0, 160.0), hill_z, "decal_hill"), map38((600.0, 600.0), flat, "decal_flat")]})
    if "mnbl_probe_snow" in scenes:
        sequence.append(scenes["mnbl_probe_snow"])
    args = {"scenes": sequence, "frames": 240, "settleFrames": 40, "stepTimeoutSeconds": 600}
    # Run 2 (the confirming run): uncapped-frame attempt and main-thread cost, decal variants, snow variants. No colour-grade manager.
    run2 = []
    for n in ("mnbl_probe_l2", "mnbl_probe_l6", "mnbl_probe_l15", "mnbl_probe_forest", "mnbl_probe_a6", "mnbl_probe_a7"):
        if n in scenes:
            run2.append({"name": n, "kind": "costonly", "views": [v for v in scenes[n]["views"] if v["name"] in ("overview", "map38_flat", "map38_mountain")]})
    if "mnbl_probe_l2" in scenes:
        run2.append({"name": "mnbl_probe_l2", "kind": "decals2", "views": [map38((600.0, 600.0), flat, "decal_rows"), map38((600.0, 300.0), flat, "decal_counts")]})
    for n in ("mnbl_probe_snow", "mnbl_probe_snowf", "mnbl_probe_snowfr"):
        if n in scenes or n in report["slots"]:
            run2.append({"name": n, "kind": "snow2", "views": snow_views})
    run3 = [{"name": "mnbl_probe_snowf", "kind": "snow3", "preRead": "bind", "views": snow_views},
            {"name": "mnbl_probe_snowp", "kind": "snow3", "preRead": "loadPath", "views": snow_views},
            {"name": "mnbl_probe_snowp", "kind": "snow3", "preRead": "none", "views": snow_views}]
    with open(os.path.join(a.out, "probe-args-run3.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"scenes": run3, "frames": 60, "settleFrames": 40, "stepTimeoutSeconds": 180}, handle)
    args2 = {"scenes": run2, "frames": 240, "settleFrames": 40, "stepTimeoutSeconds": 600, "forceVsyncOff": True, "skip": ["color_grade_manager"]}
    with open(os.path.join(a.out, "probe-args-run2.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(args2, handle)
    with open(os.path.join(a.out, "probe-slots.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=1)
    with open(os.path.join(a.out, "probe-args.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(args, handle)
    for slot, info in report["slots"].items():
        print(slot, "level", info["heightmapLevel"], "layers/node", info["layersPerNode"], "flora", info["floraInstances"],
              "terrain.bin", info["files"]["terrain.bin"]["bytes"])


if __name__ == "__main__":
    main()
