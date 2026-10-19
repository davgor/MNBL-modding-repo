"""Independent offline bundle verification. Writes only to the specified absent scratch root.

The existing Python terrain/navmesh codecs are the reference implementation; strict PNG,
stored-DEFLATE, LZ4 literal and container boundary checks below also inspect the C++ bytes.
No native loading, asset availability, collision or cache-freshness claim is made.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET
import zlib

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools/scenebin"))
import terrain_bin as terrain
import navmesh_bin as nav
import build_slot as slot

checks = 0


def check(ok, why):
    global checks
    checks += 1
    if not ok:
        raise AssertionError(why)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def png(data):
    check(data[:8] == b"\x89PNG\r\n\x1a\n", "PNG signature")
    pos, chunks = 8, []
    while pos < len(data):
        n, = struct.unpack_from(">I", data, pos)
        tag = data[pos+4:pos+8]
        body = data[pos+8:pos+8+n]
        crc, = struct.unpack_from(">I", data, pos+8+n)
        check(crc == zlib.crc32(tag+body), "PNG CRC " + repr(tag))
        chunks.append((tag, body))
        pos += 12+n
    check(pos == len(data) and [x[0] for x in chunks] == [b"IHDR", b"pHYs", b"IDAT", b"IEND"], "PNG exact chunk boundaries/order")
    w, h, bits, colour, comp, filt, interlace = struct.unpack(">IIBBBBB", chunks[0][1])
    check((comp, filt, interlace) == (0, 0, 0), "PNG methods")
    check(chunks[1][1] == struct.pack(">IIB", 2835, 2835, 1) and not chunks[3][1], "PNG pHYs/IEND")
    z = chunks[2][1]
    check(z[:2] == b"\x78\x01", "zlib header")
    offset, raw = 2, bytearray()
    while True:
        final = z[offset]
        check(final in (0, 1), "stored DEFLATE only, aligned")
        n, inv = struct.unpack_from("<HH", z, offset+1)
        check(inv == n ^ 65535, "stored DEFLATE LEN/NLEN")
        raw.extend(z[offset+5:offset+5+n])
        offset += 5+n
        if final:
            break
    check(offset+4 == len(z), "DEFLATE exact end")
    check(struct.unpack_from(">I", z, offset)[0] == zlib.adler32(raw), "Adler32")
    check(zlib.decompress(z) == raw, "independent zlib decode")
    stride = w * (4 if colour == 6 else 1) * (bits//8)
    check(len(raw) == h*(stride+1), "PNG scanline size")
    check(all(raw[r*(stride+1)] == 0 for r in range(h)), "all filter-zero scanlines")
    return w, h, bits, colour, b"".join(raw[r*(stride+1)+1:(r+1)*(stride+1)] for r in range(h))


def literal(block, expected):
    check(block[0] & 15 == 0, "LZ4 token")
    n, pos = block[0] >> 4, 1
    if n == 15:
        while True:
            v = block[pos]
            pos += 1
            n += v
            if v != 255:
                break
    check(pos+n == len(block) and n == expected, "LZ4 literal exact length")
    check(nav.lz4_block_decompress(block, expected) == block[pos:], "independent LZ4 decode")


def containers(data):
    check(data[:12] == b"ZGR6RTRN\x02\x00\x00\x00", "terrain header")
    next_offset = 112
    for i, tag in enumerate((b"MIDX", b"HGHT", b"NRML", b"WGHT", b"PHYM")):
        t, version, off, csize, usize = struct.unpack_from("<4s4I", data, 12+20*i)
        check((t, version, off) == (tag, 1, next_offset), "section order/offset/version")
        p = off
        if i:
            offsets = struct.unpack_from("<16I", data, p)
            check(offsets[0] == 0 and all(a < b for a, b in zip(offsets, offsets[1:])) and offsets[-1] < usize, "node offsets")
            p += 64
        codec, pad, rawsize, size, reserved, stamp = struct.unpack_from("<IIQQQQ", data, p)
        check((codec, pad, rawsize, size, reserved, stamp) == (3, 0, usize, csize, 0, 0), "blob fields")
        literal(data[p+40:p+csize], usize)
        next_offset = p+csize
    check(next_offset == len(data), "no terrain trailing bytes")


def hf_write(path, grid):
    flat = [v for row in grid for v in row]
    path.write_bytes(b"MNBLHF01" + struct.pack("<I", len(grid)) + struct.pack("<%dd" % len(flat), *flat))


def verify(output, original_grid, extent, scale, template):
    grid = [[v*scale for v in row] for row in original_grid]
    side = len(grid)
    spacing = extent/(side-1)
    reference, info = terrain.build_terrain(grid, 4, 4, extent/4, layer_weights={1: slot.slope_weights(grid, spacing)}, physics_layers=[0, 1])
    actual_bytes = (output/"terrain.bin").read_bytes()
    containers(actual_bytes)
    actual, expected = terrain.parse(actual_bytes), terrain.parse(reference)
    check(actual.node_count == expected.node_count == 16, "node count")
    # Compare every PNG channel and every physical-material byte, not compressed representation.
    def compare_png(a, b):
        w, h, bits, colour, raw = png(a)
        aw, ah, ac, av = terrain.png_decode(a, use_pillow=False)
        bw, bh, bc, bv = terrain.png_decode(b, use_pillow=False)
        check((aw, ah, ac, list(av)) == (bw, bh, bc, list(bv)), "Python reference decoded PNG samples")
        check((w, h) == (aw, ah) and colour == (6 if ac == 4 else 0), "PNG dimensions/channels")
        raw_values = struct.unpack(">%dH" % (w*h), raw) if bits == 16 else raw
        check(list(raw_values) == list(av), "manual vs Python PNG decode")
    for a, b in zip(actual.midx_images(), expected.midx_images()):
        compare_png(a, b)
    check(len(actual.midx_images()) == 4, "four global MIDX images")
    for a, b in zip(actual.nodes(), expected.nodes()):
        compare_png(a.height_png, b.height_png)
        compare_png(a.normal_png, b.normal_png)
        check(a.weight_flags == b.weight_flags, "all 16 weight presence flags")
        for ap, bp in zip(a.weights, b.weights):
            if bp is None:
                check(ap is None, "omitted weight layer")
            else:
                compare_png(ap, bp)
        check(a.physics == b.physics, "four PHYM seasons including column-major transpose")
    quantized = terrain.decode_heights(expected, info["min_height"], info["max_height"], nx=4, ny=4)
    hf = (output/"quantized-heightfield.bin").read_bytes()
    check(hf[:12] == b"MNBLHF01" + struct.pack("<I", side), "quantized heightfield header")
    qvalues = struct.unpack("<%dd" % (side*side), hf[12:])
    check(qvalues == tuple(v for row in quantized for v in row), "all reconstructed world-unit quantized heights")
    meta = json.loads((output/"surface.json").read_text())
    for key, value in {"nodes": [4, 4], "nodeSize": extent/4, "extent": [extent, extent], "gridSide": side,
                       "spacing": spacing, "verticalScale": scale, "minHeight": info["min_height"], "maxHeight": info["max_height"],
                       "nativeValidated": False, "quantizedHeightfield": "quantized-heightfield.bin"}.items():
        check(meta[key] == value, "metadata " + key)
    check(meta["quantizationMaxError"] == (info["max_height"]-info["min_height"])/65535/2, "quantization bound")
    check(max(abs(a-b) for a, b in zip(qvalues, (v for row in grid for v in row))) <= meta["quantizationMaxError"]+1e-10, "actual quantization error")
    for point in meta["points"]:
        vals = slot.interpolate(quantized, point["x"], point["y"], spacing)
        check(all(abs(point[k]-v) < 1e-10 for k, v in zip(("bilinear", "triangleA", "triangleB"), vals)), "sample interpolation")
    check(len(meta["points"]) >= 29 and "official kernel approximate" in meta["normalSource"], "sample/provenance coverage")
    xml = ET.parse(output/"scene.xscene").getroot()
    check(xml.attrib["name"] == meta["sceneName"] == ET.parse(template/"scene.xscene").getroot().attrib["name"], "preserved owned scene name")
    check(not list(xml.find("entities")) and all(xml.find(n) is None for n in ("Paths", "camera_data", "editor_data")), "no copied geography")
    check(xml.find("terrain/outer_mesh/variable[@name='outer_mesh_name']").attrib["value"] == "", "no outer mesh")
    tx = xml.find("terrain")
    for key, value in (("node_size", extent/4), ("min_height", info["min_height"]), ("max_height", info["max_height"])):
        check(float(tx.attrib[key]) == value, "XML quantization/bounds " + key)
    refnodes = ET.fromstring(terrain.nodes_xml(info))
    for a, b in zip(tx.find("nodes"), refnodes):
        check(a.attrib == b.attrib, "all node extrema/complexity/masks/coordinates")
        check([(v.tag, v.attrib) for v in a.iter()] == [(v.tag, v.attrib) for v in b.iter()], "all node texture levels")
    check(xml.find("flora_bounding_rect").attrib == {"min": "0.000, 0.000", "max": "%.3f, %.3f" % (extent, extent)}, "flora bounds")
    check(xml.find("environment_properties/property[@name='physics_world_max_']").attrib["value"] == "%.3f, %.3f" % (extent+50, extent+50), "physics bounds")
    check(ET.tostring(tx.find("layers")) == ET.tostring(ET.parse(template/"scene.xscene").find("terrain/layers")) or
          [(e.tag, e.attrib) for e in tx.find("layers").iter()] == [(e.tag, e.attrib) for e in ET.parse(template/"scene.xscene").find("terrain/layers").iter()], "material contract preserved")
    step = (side-1)//32
    ng = [row[::step] for row in quantized[::step]]
    def group(c, r):
        corners = ng[r][c], ng[r][c+1], ng[r+1][c], ng[r+1][c+1]
        return 7 if (max(corners)-min(corners))/(extent/32) > 1 else 1
    expected_nav = nav.build_grid_navmesh(ng, extent/32, group, triangulate="auto", z_offset=.2)
    nb = (output/"navmesh.bin").read_bytes()
    container = nav.unwrap_rnm1(nb)
    literal(nb[48:], len(container.payload))
    check(container.payload == nav.encode_payload(expected_nav), "entire navmesh payload: vertices/edges/faces/groups/tail")
    check(container.scratch0 == container.scratch1 == container.reserved == 0, "navmesh deterministic reserved fields")
    result = nav.validate(nav.decode_payload(container.payload))
    check(not result["errors"], "navmesh topology validation")
    check((output/"flora.bin").read_bytes() == b"FLR2"+struct.pack("<II", 4, 0), "empty generated flora")
    check((output/"atmosphere.xml").read_bytes() == (template/"atmosphere.xml").read_bytes(), "passive atmosphere unchanged")
    check(set(p.name for p in output.iterdir()) == {"terrain.bin", "scene.xscene", "navmesh.bin", "flora.bin", "atmosphere.xml", "quantized-heightfield.bin", "surface.json"}, "exact output inventory")
    return {"path": str(output), "gridSide": side, "extent": extent, "files": {p.name: sha(p) for p in output.iterdir()}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", type=Path, required=True)
    ap.add_argument("--output", type=Path, required=True)
    ap.add_argument("--template", type=Path, required=True)
    ap.add_argument("--prepared-template", type=Path)
    args = ap.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    before = {str(p): sha(p) for t in (args.template, args.prepared_template) if t for p in t.iterdir() if p.is_file()}
    calls, reports = [], []
    def execute(hf, dest, extent=256, scale=1, template=None, bad=None):
        cmd = [str(args.exe), "--surface-heightfield", str(hf), "--surface-template", str(template or args.template),
               "--surface-output", str(dest), "--surface-extent", str(extent), "--surface-scale", str(scale)]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
        calls.append({"args": cmd, "exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr})
        check(proc.returncode == (2 if bad else 0), "CLI exit: " + proc.stderr)
        if bad:
            check(bad in proc.stderr, "actionable refusal: " + proc.stderr)
        return proc
    for side in (129, 257):
        grid = [[slot.calibration(c*256/(side-1), r*256/(side-1)) for c in range(side)] for r in range(side)]
        hf = args.output/('calibration%d.bin' % side)
        hf_write(hf, grid)
        dest = args.output/('calibration%d' % side)
        execute(hf, dest)
        reports.append(verify(dest, grid, 256, 1, args.template))
        if side == 129:
            original = {p.name: sha(p) for p in dest.iterdir()}
            execute(hf, dest, bad="output must be absent")
            check(original == {p.name: sha(p) for p in dest.iterdir()}, "existing result never overwritten")
            duplicate = args.output/"repeat129"
            execute(hf, duplicate)
            check(original == {p.name: sha(p) for p in duplicate.iterdir()}, "deterministic bytes independent of output directory")
    for seed, extent in ((42, 800), (43, 640)):
        source = REPO/('build/heightfield-source%d/heightfield.bin' % seed)
        check(source.is_file(), "required retained FWGC fixture")
        _, grid = slot.read_heightfield(source)
        dest = args.output/('seed%d' % seed)
        execute(source, dest, extent, .01)
        reports.append(verify(dest, grid, extent, .01, args.template))
    # Constant and exact half-code cases exercise empty rock layers and ties-to-even quantization.
    for label, grid in (("flat", [[7.25]*129 for _ in range(129)]),
                        ("ties", [[(0 if (r,c)==(0,0) else 1 if (r,c)==(0,1) else 10.5/65535 if c%2 else 11.5/65535) for c in range(129)] for r in range(129)])):
        hf = args.output/(label+'.bin'); hf_write(hf, grid)
        dest = args.output/label; execute(hf, dest)
        reports.append(verify(dest, grid, 256, 1, args.template))
    good = args.output/"calibration129.bin"
    for i, (payload, error) in enumerate(((b"bad", "magic/header"), (good.read_bytes()[:-1], "truncated"),
                                          (good.read_bytes()+b"x", "truncated"), (b"MNBLHF01"+struct.pack("<I", 130), "129 or 257"),
                                          (good.read_bytes()[:12]+struct.pack("<d", float('nan'))+good.read_bytes()[20:], "nonfinite"))):
        path = args.output/('bad%d.bin' % i); path.write_bytes(payload)
        dest = args.output/('bad%d' % i); execute(path, dest, bad=error)
        check(not dest.exists(), "invalid input leaves no bundle")
    for value in (0, -1, 'nan', 'inf', '2garbage', .001, 256.001):
        dest = args.output/('badextent'+str(value)); execute(good, dest, extent=value, bad="extent")
        check(not dest.exists(), "invalid extent leaves no bundle")
    for value in (0, -1, 'nan', 'inf', '1e300'):
        dest = args.output/('badscale'+str(value)); execute(good, dest, scale=value, bad="scale" if value != '1e300' else "scaled height")
        check(not dest.exists(), "invalid scale leaves no bundle")
    xml = (args.template/"scene.xscene").read_text()
    mutations = [(xml.replace('node_dimension_x="4"', 'node_dimension_x="3"', 1), "4x4"),
                 (xml.replace('<layer name="grass">', '<layer name="rock_k">', 1), "material order"),
                 (xml.replace('idx="1" idy="0"', 'idx="0" idy="0"', 1), "coordinates"),
                 (xml.replace('<entities>', '<entities><entity/>', 1), "entities must be empty"),
                 (xml.replace('<scene ', '<!DOCTYPE scene><scene ', 1), "XML"),
                 (xml.replace('version="2"', 'version="2" version="2"', 1), "duplicate XML attribute"),
                 (xml.replace('physics_material="stone"', 'physics_material="water"', 1), "physical material"),
                 (xml.replace('name="rock_k" is_flora_layer="false"', 'name="rock_k" is_flora_layer="true"', 1), "flora/mesh"),
                 (xml.replace('vista_tileset=""', 'vista_tileset="campaign"', 1), "tileset")]
    for i, (text, error) in enumerate(mutations):
        check(text != xml, "mutation actually changed XML")
        template = args.output/('templatebad%d' % i)
        shutil.copytree(args.template, template)
        (template/"scene.xscene").write_text(text)
        dest = args.output/('badtemplate%d' % i); execute(good, dest, template=template, bad=error)
        check(not dest.exists(), "invalid template leaves no bundle")
    if args.prepared_template:
        _, grid = slot.read_heightfield(good)
        dest = args.output/"prepared-template"
        execute(good, dest, template=args.prepared_template)
        reports.append(verify(dest, grid, 256, 1, args.prepared_template))
        check(json.loads((dest/"surface.json").read_text())["sceneName"] == "mnbl_runtime_surface", "prepared runtime scene name")
    check(before == {p: sha(Path(p)) for p in before}, "all supplied template files unchanged")
    old = subprocess.run([str(args.exe), '--self-test'], capture_output=True, text=True, timeout=30)
    check(old.returncode == 0, "existing reader self-test")
    # Exercise existing successful --source/--out mode against a retained FWGC source.
    legacy = args.output/"legacy-reader"
    old = subprocess.run([str(args.exe), '--source', str(REPO/'build/world42'), '--out', str(legacy)], capture_output=True, text=True, timeout=60)
    check(old.returncode == 0, "existing source mode " + old.stderr)
    check((legacy/'heightfield.bin').read_bytes() == (REPO/'build/heightfield-source42/heightfield.bin').read_bytes(), "source-mode heightfield unchanged")
    pinned = [REPO/'src/WorldBridge'/n for n in ('main.cpp', 'surface.cpp', 'surface.hpp', 'CMakeLists.txt')]
    pinned += [REPO/'tools/scenebin'/n for n in ('terrain_bin.py', 'navmesh_bin.py', 'build_slot.py')]
    receipt = {"schema": "mnbl.worldbridge-surface-tests", "assertions": checks, "cases": reports,
               "executable": {"path": str(args.exe), "sha256": sha(args.exe)}, "inputs": {str(p): sha(p) for p in pinned},
               "templateInputs": before, "calls": calls, "scope": "Offline codec/CLI equivalence only; no native runtime or cache claim."}
    (args.output/'checks.json').write_text(json.dumps(receipt, indent=2))
    print('PASS: %d assertions, %d fully decoded bundles; %s' % (checks, len(reports), args.output/'checks.json'))


if __name__ == '__main__':
    main()
