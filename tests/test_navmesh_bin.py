"""Tests for tools/scenebin/navmesh_bin.py (stdlib unittest).

Run:  python -m unittest tests/test_navmesh_bin.py -v      (or: python tests/test_navmesh_bin.py)

Tests that read the installed game are skipped when it is absent; set BANNERLORD_ROOT to override the
default D:\\SteamLibrary\\steamapps\\common\\Mount & Blade II Bannerlord.  Nothing is written outside
temporary directories.
"""
import collections
import hashlib
import os
import random
import struct
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "scenebin"))
import navmesh_bin as nb  # noqa: E402

GAME = Path(os.environ.get("BANNERLORD_ROOT", r"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"))
MODULES = GAME / "Modules"

# SHA-256 of the 634-byte prefab NavigationFixture.cs writes for seeds 42 and 43 (identical to the files
# that code wrote into the deployed MNBL.ProceduralWorld/NavMeshPrefabs and the engine imported).
FIXTURE_SHA = {
    42: "d0da0fd6738f9c25bff324a2e272c5646de92ba439e9eba13c0c02dd57d783a6",
    43: "b860e1f90109b6251abf3dd76de23d23242d75a0eb22572afbfb6226f0f29f5f",
}


def csharp_fixture_reference(seed):
    """Independent re-statement of NavigationFixture.Write (hand-written byte layout, not the builder)."""
    out = bytearray()
    out += b"NMG9" + struct.pack("<i", 6)
    for y in range(2):
        for x in range(3):
            out += struct.pack("<fff", float(x) * 10, float(y) * 10, 1.0 + (seed - 42) * x)
    edges = [(0, 1), (1, 4), (4, 3), (3, 0), (1, 2), (2, 5), (5, 4)]
    out += struct.pack("<i", 7)
    for a, b in edges:
        out += struct.pack("<6i", -1, a, b, -1, -1, 0)
    out += struct.pack("<i", 2)
    for verts, eids, group in (((0, 1, 4, 3), (0, 1, 2, 3), 0), ((1, 2, 5, 4), (4, 5, 6, 1), 1)):
        out += struct.pack("<i4i4i", 4, *verts, *eids)
        out += struct.pack("<5i", 2, 0, group, 0, -1) + b"\x00"
    out += struct.pack("<i", 32) + bytes(256)
    return struct.pack("<i", len(out)) + bytes(out)


def edge_uses(mesh):
    uses = collections.defaultdict(list)
    for fi, f in enumerate(mesh.faces):
        k = len(f.vertices)
        for j in range(k):
            rec = mesh.edges[f.edges[j]]
            a, b = f.vertices[j], f.vertices[(j + 1) % k]
            direction = 1 if (rec[1], rec[2]) == (a, b) else (-1 if (rec[1], rec[2]) == (b, a) else 0)
            uses[f.edges[j]].append((fi, direction))
    return uses


class Lz4Tests(unittest.TestCase):
    def roundtrip(self, data):
        for mode in ("fast", "store"):
            block = nb.lz4_block_compress(data, mode)
            self.assertEqual(nb.lz4_block_decompress(block, len(data)), data, mode)

    def test_edge_cases(self):
        for data in (b"", b"a", b"abcdefghijkl", b"abcdefghijklm", b"\x00" * 13, b"\x00" * 100000,
                     b"ab" * 5000, bytes(range(256)) * 300):
            self.roundtrip(data)

    def test_random_and_structured(self):
        rng = random.Random(7)
        self.roundtrip(bytes(rng.getrandbits(8) for _ in range(70000)))
        words = [bytes(rng.getrandbits(8) for _ in range(rng.randint(1, 12))) for _ in range(40)]
        self.roundtrip(b"".join(rng.choice(words) for _ in range(30000)))

    def test_fast_mode_compresses_repetition(self):
        data = struct.pack("<6i", -1, 1, 2, -1, -1, 0) * 5000
        self.assertLess(len(nb.lz4_block_compress(data, "fast")), len(data) // 20)

    def test_overlapping_match_decode(self):
        # literal "ab", then match offset 2 length 10 -> "abababababab"
        block = bytes([0x26]) + b"ab" + struct.pack("<H", 2) + bytes([0x50]) + b"xyzzy"
        self.assertEqual(nb.lz4_block_decompress(block), b"ab" * 6 + b"xyzzy")

    def test_rejects_bad_offset(self):
        with self.assertRaises(nb.NavMeshFormatError):
            nb.lz4_block_decompress(bytes([0x10]) + b"a" + struct.pack("<H", 5) + bytes([0x50]) + b"12345")


class FixtureEquivalenceTests(unittest.TestCase):
    def test_builder_reproduces_navigation_fixture_cs(self):
        for seed in (40, 42, 43, 50):
            ours = nb.navigation_fixture_prefab(seed)
            self.assertEqual(ours, csharp_fixture_reference(seed), seed)
            self.assertEqual(len(ours), 634)
        for seed, digest in FIXTURE_SHA.items():
            self.assertEqual(hashlib.sha256(nb.navigation_fixture_prefab(seed)).hexdigest(), digest)

    def test_fixture_decodes_with_expected_fields(self):
        mesh = nb.decode_payload(nb.decode_prefab(nb.navigation_fixture_prefab(43)))
        self.assertEqual(mesh.version, b"NMG9")
        self.assertEqual([v[2] for v in mesh.vertices], [1.0, 2.0, 3.0, 1.0, 2.0, 3.0])
        self.assertEqual([f.group for f in mesh.faces], [0, 1])
        self.assertEqual([f.meta for f in mesh.faces], [(2, 0, 0, 0, -1), (2, 0, 1, 0, -1)])
        self.assertTrue(nb.validate(mesh)["ok"])

    @unittest.skipUnless((MODULES / "MNBL.ProceduralWorld" / "NavMeshPrefabs").is_dir(), "deployed fixture absent")
    def test_deployed_csharp_outputs_match(self):
        refs = {nb.navigation_fixture_prefab(s): s for s in (42, 43)}
        files = sorted((MODULES / "MNBL.ProceduralWorld" / "NavMeshPrefabs").glob("mnbl_probe_*.bin"))
        self.assertTrue(files)
        for f in files:
            self.assertIn(f.read_bytes(), refs, f.name)


class GridBuilderTests(unittest.TestCase):
    def check_invariants(self, mesh, nx, ny, holes=0, tris_per_cell=0):
        report = nb.validate(mesh)
        self.assertTrue(report["ok"], report["errors"])
        self.assertEqual(report["warnings"], [])
        uses = edge_uses(mesh)
        for e, u in uses.items():
            self.assertIn(len(u), (1, 2), "edge %d used %d times" % (e, len(u)))
            self.assertNotIn(0, [d for _, d in u])
            if len(u) == 2:
                self.assertEqual(sorted(d for _, d in u), [-1, 1], "shared edge %d not opposite" % e)
        self.assertEqual(len(uses), len(mesh.edges))
        pairs = {tuple(sorted(r[1:3])) for r in mesh.edges}
        self.assertEqual(len(pairs), len(mesh.edges))
        for f in mesh.faces:
            self.assertGreater(nb._signed_area_xy(mesh.vertices, f.vertices), 0)
        if not holes:
            cells = nx * ny
            faces = cells * (tris_per_cell or 1)
            self.assertEqual(len(mesh.faces), faces)
            self.assertEqual(len(mesh.vertices), (nx + 1) * (ny + 1))
            # disk: V - E + F = 1 ; boundary = perimeter segments
            self.assertEqual(len(mesh.vertices) - len(mesh.edges) + len(mesh.faces), 1)
            self.assertEqual(report["counts"]["boundary_edges"], 2 * (nx + ny))
            self.assertEqual(report["counts"]["islands"], 1)

    def heights(self, nx, ny, seed=1):
        rng = random.Random(seed)
        return [[rng.uniform(-5, 20) for _ in range(nx + 1)] for _ in range(ny + 1)]

    def test_quads(self):
        nx, ny = 23, 17
        mesh = nb.build_grid_navmesh(self.heights(nx, ny), 2.5, lambda i, j: 1 + (i + j) % 3,
                                     origin=(100.0, -50.0))
        self.check_invariants(mesh, nx, ny)
        self.assertEqual(collections.Counter(len(f.vertices) for f in mesh.faces), {4: nx * ny})

    def test_triangulations(self):
        nx, ny = 11, 9
        for mode in (True, "alternate", "auto"):
            mesh = nb.build_grid_navmesh(self.heights(nx, ny, 3), 4.0, lambda i, j: 18, triangulate=mode)
            self.check_invariants(mesh, nx, ny, tris_per_cell=2)
            self.assertEqual({len(f.vertices) for f in mesh.faces}, {3})

    def test_holes_and_id_grid(self):
        nx, ny = 8, 6
        ids = [[None if (2 <= i <= 4 and 2 <= j <= 3) else (19 if i < 4 else 1) for i in range(nx)] for j in range(ny)]
        mesh = nb.build_grid_navmesh(self.heights(nx, ny), 3.0, ids)
        self.check_invariants(mesh, nx, ny, holes=1)
        self.assertEqual(len(mesh.faces), nx * ny - 6)
        rep = nb.validate(mesh)
        # outer perimeter + hole perimeter (3x2 cells -> 10 edges)
        self.assertEqual(rep["counts"]["boundary_edges"], 2 * (nx + ny) + 10)
        self.assertEqual(rep["counts"]["unreferenced_vertices"], 0)
        # sea/land faces share edges (as on the War Sails campaign map)
        uses = edge_uses(mesh)
        cross = sum(1 for u in uses.values() if len(u) == 2 and
                    mesh.faces[u[0][0]].group != mesh.faces[u[1][0]].group)
        self.assertEqual(cross, ny - 2)  # column boundary i=3|4 minus the rows interrupted by the hole

    def test_islands_counted(self):
        ids = [[1, None, 1], [1, None, 1]]
        mesh = nb.build_grid_navmesh(self.heights(3, 2), 1.0, ids)
        self.assertEqual(nb.validate(mesh)["counts"]["islands"], 2)

    def test_serialize_roundtrip_all_versions(self):
        nx, ny = 6, 5
        for version in (b"NMG9", b"NMG8", b"NMG7"):
            mesh = nb.build_grid_navmesh(self.heights(nx, ny), 2.0, lambda i, j: 4, version=version, triangulate="auto")
            payload = nb.encode_payload(mesh)
            again = nb.decode_payload(payload)
            self.assertEqual(nb.encode_payload(again), payload)
            self.assertEqual(again.vertices, mesh.vertices)
            for mode in ("fast", "store"):
                data = nb.wrap_rnm1(payload, mode)
                c = nb.unwrap_rnm1(data)
                self.assertEqual(c.payload, payload)
                self.assertEqual(struct.unpack_from("<I", data, 4)[0], len(data) - 8)
                self.assertEqual(struct.unpack_from("<Q", data, 24)[0], len(data) - 8)
                self.assertEqual(struct.unpack_from("<Q", data, 16)[0], len(payload))
                self.assertEqual(struct.unpack_from("<I", data, 8)[0], 3)
            prefab = nb.encode_prefab(payload)
            self.assertEqual(nb.decode_prefab(prefab), payload)

    def test_file_writers_and_official_path_refusal(self):
        mesh = nb.build_grid_navmesh(self.heights(4, 4), 2.0, lambda i, j: 1)
        with tempfile.TemporaryDirectory() as tmp:
            scene = os.path.join(tmp, "navmesh.bin")
            nb.write_scene_navmesh(mesh, scene)
            kind, _c, payload = nb.read_navmesh_file(scene)
            self.assertEqual(kind, "rnm1")
            self.assertEqual(payload, nb.encode_payload(mesh))
            prefab = os.path.join(tmp, "p.bin")
            nb.write_prefab(mesh, prefab)
            self.assertEqual(nb.read_navmesh_file(prefab)[0], "prefab")
        bad = os.path.join("D:\\", "x", "Mount & Blade II Bannerlord", "Modules", "SandBox", "SceneObj", "t", "navmesh.bin")
        with self.assertRaises(PermissionError):
            nb.write_scene_navmesh(mesh, bad)

    def test_validator_detects_defects(self):
        mesh = nb.build_grid_navmesh(self.heights(3, 3), 1.0, lambda i, j: 1)
        f = mesh.faces[0]
        mesh.faces[0] = nb.NavFace(tuple(reversed(f.vertices)), tuple(reversed(f.edges)), f.meta, f.flag)
        rep = nb.validate(mesh)
        self.assertFalse(rep["ok"])
        mesh = nb.build_grid_navmesh(self.heights(3, 3), 1.0, lambda i, j: 1)
        mesh.faces.append(mesh.faces[0])
        self.assertTrue(any("non-manifold" in e or "same direction" in e for e in nb.validate(mesh)["errors"]))

    def test_campaign_rule_ids(self):
        h = [[-10, -10, 0, 0.5], [-10, -10, 0, 0.5], [-10, -1, 20, 0.5]]
        rule = nb.campaign_face_id_rule(h, 0.0, 4.0, 1.0, 1.0)
        self.assertEqual(rule(0, 0), 19)
        self.assertEqual(rule(1, 0), 19)
        self.assertEqual(rule(2, 0), 1)
        self.assertEqual(rule(2, 1), 7)
        self.assertEqual(nb.campaign_face_id_rule(h, 0.0, 4.0, 1.0, 1.0, naval=False)(0, 0), 10)
        self.assertTrue(nb.LAND_TERRAIN_IDS.isdisjoint(nb.NAVAL_TERRAIN_IDS))


@unittest.skipUnless(MODULES.is_dir(), "game install absent")
class InstalledFileTests(unittest.TestCase):
    SAMPLES = [
        "SandBox/SceneObj/Main_map/navmesh.bin",
        "NavalDLC/SceneObj/Main_map/navmesh.bin",
        "SandBoxCore/SceneObj/battle_terrain_001/navmesh.bin",   # NMG8
        "SandBox/SceneObj/battania_town_house_b_interior_b_house/navmesh.bin",  # NMG7
        "NavalDLC/NavMeshPrefabs/cog_ship_navmesh.bin",
    ]

    def test_byte_identical_roundtrip_samples(self):
        for rel in self.SAMPLES:
            path = MODULES / rel
            if not path.exists():
                continue
            r = nb.roundtrip_file(str(path), recompress=True)
            self.assertEqual(r["status"], "ok", (rel, r))
            if r["container"] == "rnm1":
                self.assertTrue(r["recompressed_decodes_identical"], rel)

    def test_campaign_maps_semantics(self):
        expected_groups = {
            "SandBox": {1, 2, 4, 5, 6, 7, 8, 10, 11, 13, 14},
            "NavalDLC": {1, 2, 4, 5, 7, 11, 14, 18, 19, 22, 25},
        }
        for module, groups in expected_groups.items():
            path = MODULES / module / "SceneObj" / "Main_map" / "navmesh.bin"
            if not path.exists():
                continue
            mesh = nb.decode_payload(nb.unwrap_rnm1(path.read_bytes()).payload)
            self.assertEqual({f.group for f in mesh.faces}, groups)
            rep = nb.validate(mesh)
            self.assertTrue(rep["ok"], rep["errors"])
            self.assertTrue(all(f.meta[0] == 2 and f.meta[1] == 0 and f.meta[3] == 0 and f.flag == 0
                                for f in mesh.faces))
            self.assertTrue(all(r[0] == -1 and r[3] == -1 and r[4] == -1 and r[5] == 0 for r in mesh.edges))
            self.assertEqual(mesh.tail, nb.STANDARD_TAIL)


if __name__ == "__main__":
    unittest.main(verbosity=2)
