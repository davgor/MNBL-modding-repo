#!/usr/bin/env python3
"""Bannerlord navigation-mesh codec: scene ``navmesh.bin`` (RNM1 + LZ4) and bare NMG prefabs.

Standard library only.  The field-by-field specification, the evidence behind each field and the
campaign face-id table live in ``docs/formats/navmesh-bin.md``.

Containers
    * Scene navmesh (``Modules/<m>/SceneObj/<scene>/navmesh.bin``): 48-byte ``RNM1`` header followed by
      one raw LZ4 *block* (no frame) whose decompressed bytes are an NMG payload.
    * Navigation prefab (``Modules/<m>/NavMeshPrefabs/<name>.bin``): little-endian u32 payload length
      followed by the uncompressed NMG payload.

Payload revisions handled here: ``NMG9`` (current; 6-int edges, per-face trailing byte), ``NMG8``
(5-int edges, per-face byte) and ``NMG7`` (5-int edges, no per-face byte).  ``NMG4``/``NMG5`` (legacy
Native prefabs only) are recognised but not decoded.

CLI::

    python tools/scenebin/navmesh_bin.py inspect  <navmesh.bin|prefab.bin> [--json]
    python tools/scenebin/navmesh_bin.py stats    <file> [--json]
    python tools/scenebin/navmesh_bin.py roundtrip <file-or-dir>... [--recompress] [--json OUT]
    python tools/scenebin/navmesh_bin.py synth    --out FILE (--fixture-seed N | --width W --height H
                                                  | --heightfield MNBLHF01.bin) [options]
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import math
import os
import struct
import sys
import tempfile
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple, Union

__all__ = [
    "NavMesh", "NavFace", "Rnm1Container", "NavMeshFormatError",
    "lz4_block_decompress", "lz4_block_compress",
    "decode_payload", "encode_payload", "unwrap_rnm1", "wrap_rnm1", "decode_prefab", "encode_prefab",
    "read_navmesh_file", "write_scene_navmesh", "write_prefab",
    "validate", "mesh_stats", "build_grid_navmesh", "navigation_fixture_prefab",
    "TERRAIN_TYPES", "LAND_TERRAIN_IDS", "NAVAL_TERRAIN_IDS",
]

# ---------------------------------------------------------------------------------------------
# Campaign face-id semantics (TaleWorlds.Core.TerrainType, TaleWorlds.Core.dll v1.4.8.119303).
# A face's group id is read back by SandBox.MapScene.GetFaceTerrainType as PathFaceRecord.FaceGroupIndex
# and cast directly to TerrainType.  See docs/formats/navmesh-bin.md for the IL evidence.
# ---------------------------------------------------------------------------------------------
TERRAIN_TYPES: Dict[int, str] = {
    1: "Plain", 2: "Desert", 3: "Snow", 4: "Forest", 5: "Steppe", 6: "Fording", 7: "Mountain",
    8: "Lake", 10: "Water", 11: "River", 13: "Canyon", 14: "RuralArea", 15: "Swamp", 16: "Dune",
    17: "Bridge", 18: "CoastalSea", 19: "OpenSea", 20: "Beach", 21: "Cliff", 22: "NonNavigableRiver",
    23: "LandRestriction", 24: "SeaRestriction", 25: "UnderBridge",
}
# DefaultPartyNavigationModel.IsTerrainTypeValidForDefault -> region 1 ("land") in SetNavMeshRegionMap.
LAND_TERRAIN_IDS = frozenset({1, 2, 3, 4, 5, 6, 15, 16, 17, 20})
# NavalPartyNavigationModel.IsTerrainTypeValidForNaval -> region 0 ("sea") when War Sails is active.
NAVAL_TERRAIN_IDS = frozenset({8, 10, 11, 18, 19, 23, 24, 25})

# Defaults for synthesized faces: (meta0, meta1, meta3, meta4, flag).  meta0 is 2 in every installed
# NMG7/8/9 face; meta1/meta3/flag are 0 on every campaign-map face; meta4 is an opaque int whose
# meaning is unknown -- -1 is the value used by the in-game-accepted NavigationFixture prefab.
DEFAULT_FACE_META0 = 2
DEFAULT_FACE_META4 = -1

EDGE_INTS = {b"NMG9": 6, b"NMG8": 5, b"NMG7": 5}
FACE_HAS_FLAG = {b"NMG9": True, b"NMG8": True, b"NMG7": False}
LEGACY_UNDECODED = (b"NMG4", b"NMG5")
STANDARD_TAIL = struct.pack("<I", 32) + bytes(256)

RNM1_MAGIC = b"RNM1"
RNM1_HEADER_SIZE = 48
RNM1_COMPRESSION_LZ4 = 3

OFFICIAL_MODULES = {"native", "sandbox", "sandboxcore", "storymode", "navaldlc", "custombattle",
                    "multiplayer", "birthanddeath", "fastmode"}


class NavMeshFormatError(ValueError):
    """Raised for malformed or unsupported navigation-mesh bytes."""


# =============================================================================================
# LZ4 block codec (raw block format; no frame).  Written for this project from the public block
# format description: token = (literal_len << 4) | (match_len - 4), 15 = "more length bytes follow",
# little-endian u16 offset, last sequence is literals only.
# =============================================================================================

def lz4_block_decompress(src: bytes, expected_size: Optional[int] = None) -> bytes:
    dst = bytearray()
    i, n = 0, len(src)
    while True:
        if i >= n:
            raise NavMeshFormatError("LZ4 block ended without a final literal sequence")
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                if i >= n:
                    raise NavMeshFormatError("truncated LZ4 literal length")
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        if i + lit > n:
            raise NavMeshFormatError("LZ4 literal run exceeds block")
        dst += src[i:i + lit]
        i += lit
        if i == n:
            break
        if i + 2 > n:
            raise NavMeshFormatError("truncated LZ4 match offset")
        offset = src[i] | (src[i + 1] << 8)
        i += 2
        if offset == 0 or offset > len(dst):
            raise NavMeshFormatError("invalid LZ4 match offset %d at output %d" % (offset, len(dst)))
        mlen = token & 15
        if mlen == 15:
            while True:
                if i >= n:
                    raise NavMeshFormatError("truncated LZ4 match length")
                b = src[i]
                i += 1
                mlen += b
                if b != 255:
                    break
        mlen += 4
        start = len(dst) - offset
        if offset >= mlen:
            dst += dst[start:start + mlen]
        else:  # overlapping copy: repeat the period
            period = bytes(dst[start:])
            reps, rem = divmod(mlen, offset)
            dst += period * reps + period[:rem]
        if expected_size is not None and len(dst) > expected_size:
            raise NavMeshFormatError("LZ4 output exceeds declared size")
    if expected_size is not None and len(dst) != expected_size:
        raise NavMeshFormatError("LZ4 output %d bytes, header declares %d" % (len(dst), expected_size))
    return bytes(dst)


_LZ4_MINMATCH = 4
_LZ4_LASTLITERALS = 5
_LZ4_MFLIMIT = 12
_LZ4_MAX_DISTANCE = 65535


def _lz4_put_length(out: bytearray, value: int) -> None:
    while value >= 255:
        out.append(255)
        value -= 255
    out.append(value)


def _lz4_emit_last_literals(out: bytearray, src: bytes, anchor: int) -> bytes:
    run = len(src) - anchor
    if run >= 15:
        out.append(0xF0)
        _lz4_put_length(out, run - 15)
    else:
        out.append(run << 4)
    out += src[anchor:]
    return bytes(out)


def lz4_block_compress(src: bytes, mode: str = "fast") -> bytes:
    """Compress to a raw LZ4 block.

    ``mode="store"`` emits one literal-only sequence (valid, no compression).  ``mode="fast"`` is a
    greedy single-probe hash matcher.  Neither reproduces the engine's own (LZ4HC-like) byte stream;
    both decompress to exactly ``src``.
    """
    src = bytes(src)
    n = len(src)
    out = bytearray()
    if mode == "store" or n < _LZ4_MFLIMIT + 1:
        return _lz4_emit_last_literals(out, src, 0)
    if mode != "fast":
        raise ValueError("unknown LZ4 mode %r" % mode)
    hash_bits = 16
    shift = 32 - hash_bits
    table = [-1] * (1 << hash_bits)
    unpack_u32 = struct.Struct("<I").unpack_from
    match_start_limit = n - _LZ4_MFLIMIT  # last position a match may start
    match_end_limit = n - _LZ4_LASTLITERALS
    anchor = 0
    ip = 0
    miss = 0
    while ip <= match_start_limit:
        seq = unpack_u32(src, ip)[0]
        h = ((seq * 2654435761) & 0xFFFFFFFF) >> shift
        ref = table[h]
        table[h] = ip
        if ref < 0 or ip - ref > _LZ4_MAX_DISTANCE or unpack_u32(src, ref)[0] != seq:
            miss += 1
            ip += 1 + (miss >> 6)  # accelerate through incompressible data
            continue
        miss = 0
        # extend backwards into pending literals
        while ip > anchor and ref > 0 and src[ip - 1] == src[ref - 1]:
            ip -= 1
            ref -= 1
        # extend forwards
        a = ip + _LZ4_MINMATCH
        b = ref + _LZ4_MINMATCH
        while a < match_end_limit and src[a] == src[b]:
            a += 1
            b += 1
        lit = ip - anchor
        mlen = a - ip - _LZ4_MINMATCH
        token = (min(lit, 15) << 4) | min(mlen, 15)
        out.append(token)
        if lit >= 15:
            _lz4_put_length(out, lit - 15)
        out += src[anchor:ip]
        out += struct.pack("<H", ip - ref)
        if mlen >= 15:
            _lz4_put_length(out, mlen - 15)
        # seed the table inside the match so the next search can find nearby repeats
        if a - 2 <= match_start_limit:
            p = a - 2
            table[((unpack_u32(src, p)[0] * 2654435761) & 0xFFFFFFFF) >> shift] = p
        ip = a
        anchor = a
    return _lz4_emit_last_literals(out, src, anchor)


# =============================================================================================
# NMG payload model
# =============================================================================================

class NavFace:
    """One navigation face.

    ``meta`` holds the five int32 values that follow the edge indices:
    ``(meta0, meta1, group, meta3, meta4)``.  ``group`` is the face group id ("navigation id",
    PathFaceRecord.FaceGroupIndex); on campaign maps it is a TerrainType value.  ``flag`` is the
    trailing u8 present in NMG8/NMG9 (``None`` for NMG7).
    """
    __slots__ = ("vertices", "edges", "meta", "flag")

    def __init__(self, vertices: Sequence[int], edges: Sequence[int], meta: Sequence[int],
                 flag: Optional[int] = 0):
        self.vertices = tuple(vertices)
        self.edges = tuple(edges)
        self.meta = tuple(meta)
        self.flag = flag

    @property
    def group(self) -> int:
        return self.meta[2]

    def __repr__(self) -> str:
        return "NavFace(v=%r, e=%r, meta=%r, flag=%r)" % (self.vertices, self.edges, self.meta, self.flag)


class NavMesh:
    """Decoded NMG payload.  ``edges`` are raw int records: NMG9 ``(f0, v0, v1, f3, f4, f5)``,
    NMG7/8 ``(f0, v0, v1, f3, f4)``; every installed file stores f0=f3=f4=-1 and f5=0."""

    def __init__(self, version: bytes = b"NMG9", vertices=None, edges=None, faces=None,
                 tail: bytes = STANDARD_TAIL):
        if version not in EDGE_INTS:
            raise NavMeshFormatError("unsupported NMG version %r" % (version,))
        self.version = version
        self.vertices: List[Tuple[float, float, float]] = list(vertices or [])
        self.edges: List[Tuple[int, ...]] = list(edges or [])
        self.faces: List[NavFace] = list(faces or [])
        self.tail = bytes(tail)

    def edge_vertices(self, edge_index: int) -> Tuple[int, int]:
        rec = self.edges[edge_index]
        return rec[1], rec[2]


def decode_payload(payload: bytes) -> NavMesh:
    """Decode an uncompressed NMG7/NMG8/NMG9 payload (starting with the 4-byte magic)."""
    payload = bytes(payload)
    magic = payload[:4]
    if magic in LEGACY_UNDECODED:
        raise NavMeshFormatError("%s payloads are recognised but not decoded" % magic.decode())
    if magic not in EDGE_INTS:
        raise NavMeshFormatError("not an NMG payload (magic %r)" % (magic,))
    nint = EDGE_INTS[magic]
    has_flag = FACE_HAS_FLAG[magic]
    size = len(payload)
    try:
        o = 4
        (nv,) = struct.unpack_from("<I", payload, o)
        o += 4
        if o + 12 * nv > size:
            raise NavMeshFormatError("vertex table exceeds payload")
        flat = struct.unpack_from("<%df" % (3 * nv), payload, o)
        o += 12 * nv
        vertices = list(zip(flat[0::3], flat[1::3], flat[2::3]))
        (ne,) = struct.unpack_from("<I", payload, o)
        o += 4
        if o + 4 * nint * ne > size:
            raise NavMeshFormatError("edge table exceeds payload")
        flat = struct.unpack_from("<%di" % (nint * ne), payload, o)
        o += 4 * nint * ne
        edges = [flat[k:k + nint] for k in range(0, nint * ne, nint)]
        (nf,) = struct.unpack_from("<I", payload, o)
        o += 4
        faces = []
        u32 = struct.Struct("<I").unpack_from
        meta5 = struct.Struct("<5i").unpack_from
        for _ in range(nf):
            (k,) = u32(payload, o)
            o += 4
            if k < 3 or k > 4096:
                raise NavMeshFormatError("implausible face degree %d at offset %d" % (k, o - 4))
            ids = struct.unpack_from("<%di" % (2 * k), payload, o)
            o += 8 * k
            meta = meta5(payload, o)
            o += 20
            flag = None
            if has_flag:
                flag = payload[o]
                o += 1
            faces.append(NavFace(ids[:k], ids[k:], meta, flag))
    except struct.error as exc:
        raise NavMeshFormatError("truncated %s payload: %s" % (magic.decode(), exc)) from None
    tail = payload[o:]
    if len(tail) < 4:
        raise NavMeshFormatError("missing NMG tail")
    (tail_count,) = struct.unpack_from("<I", tail, 0)
    if len(tail) != 4 + 8 * tail_count:
        raise NavMeshFormatError("tail length %d does not match its count %d" % (len(tail), tail_count))
    return NavMesh(magic, vertices, edges, faces, tail)


def encode_payload(mesh: NavMesh) -> bytes:
    """Serialize a NavMesh to its uncompressed NMG payload."""
    version = mesh.version
    nint = EDGE_INTS[version]
    has_flag = FACE_HAS_FLAG[version]
    parts = [version, struct.pack("<I", len(mesh.vertices))]
    flat_v = [c for vtx in mesh.vertices for c in vtx]
    if len(flat_v) != 3 * len(mesh.vertices):
        raise NavMeshFormatError("every vertex needs exactly 3 coordinates")
    parts.append(struct.pack("<%df" % len(flat_v), *flat_v))
    parts.append(struct.pack("<I", len(mesh.edges)))
    flat_e = []
    for rec in mesh.edges:
        if len(rec) != nint:
            raise NavMeshFormatError("%s edges need %d ints, got %d" % (version.decode(), nint, len(rec)))
        flat_e.extend(rec)
    parts.append(struct.pack("<%di" % len(flat_e), *flat_e))
    parts.append(struct.pack("<I", len(mesh.faces)))
    for f in mesh.faces:
        k = len(f.vertices)
        if len(f.edges) != k:
            raise NavMeshFormatError("face vertex/edge counts differ")
        parts.append(struct.pack("<I%di5i" % (2 * k), k, *f.vertices, *f.edges, *f.meta))
        if has_flag:
            parts.append(bytes((f.flag or 0,)))
        elif f.flag not in (None, 0):
            raise NavMeshFormatError("NMG7 faces have no flag byte")
    parts.append(mesh.tail)
    return b"".join(parts)


# =============================================================================================
# Containers
# =============================================================================================

class Rnm1Container:
    """Decoded scene ``navmesh.bin`` wrapper.

    Layout (little endian): ``"RNM1"`` | u32 size_after_8 | u32 compression(=3) | u32 reserved(=0) |
    u64 uncompressed_size | u64 size_after_8 | u64 scratch0 | u64 scratch1 | LZ4 block.
    ``scratch0``/``scratch1`` hold uninitialised engine memory in shipped files (pointer-like values,
    different in every file); they are preserved on round trip and written as 0 by default.
    """

    def __init__(self, payload: bytes, compression: int = RNM1_COMPRESSION_LZ4, reserved: int = 0,
                 scratch0: int = 0, scratch1: int = 0, block: Optional[bytes] = None):
        self.payload = payload
        self.compression = compression
        self.reserved = reserved
        self.scratch0 = scratch0
        self.scratch1 = scratch1
        self.block = block  # original compressed block, if read from disk


def unwrap_rnm1(data: bytes) -> Rnm1Container:
    data = bytes(data)
    if len(data) < RNM1_HEADER_SIZE or data[:4] != RNM1_MAGIC:
        raise NavMeshFormatError("not an RNM1 navmesh")
    size_a, compression, reserved, raw_size, size_b, scratch0, scratch1 = struct.unpack_from("<IIIQQQQ", data, 4)
    if size_a != len(data) - 8 or size_b != size_a:
        raise NavMeshFormatError("RNM1 size fields (%d, %d) do not match file length %d" % (size_a, size_b, len(data)))
    if compression != RNM1_COMPRESSION_LZ4:
        raise NavMeshFormatError("unknown RNM1 compression field %d" % compression)
    block = data[RNM1_HEADER_SIZE:]
    payload = lz4_block_decompress(block, raw_size)
    return Rnm1Container(payload, compression, reserved, scratch0, scratch1, block)


def wrap_rnm1(payload: bytes, compression_mode: str = "fast", scratch0: int = 0, scratch1: int = 0,
              block: Optional[bytes] = None) -> bytes:
    """Build an RNM1 file.  If ``block`` is given it must decompress to ``payload`` (used to keep the
    engine's original compressed bytes on an unmodified round trip)."""
    payload = bytes(payload)
    if block is None:
        block = lz4_block_compress(payload, compression_mode)
    elif lz4_block_decompress(block, len(payload)) != payload:
        raise NavMeshFormatError("supplied LZ4 block does not decode to the payload")
    total_after_8 = RNM1_HEADER_SIZE - 8 + len(block)
    header = RNM1_MAGIC + struct.pack("<IIIQQQQ", total_after_8, RNM1_COMPRESSION_LZ4, 0, len(payload),
                                      total_after_8, scratch0, scratch1)
    assert len(header) == RNM1_HEADER_SIZE
    return header + block


def decode_prefab(data: bytes) -> bytes:
    """Return the NMG payload of a ``NavMeshPrefabs/*.bin`` file (u32 length prefix + payload)."""
    if len(data) < 8:
        raise NavMeshFormatError("prefab too short")
    (n,) = struct.unpack_from("<I", data, 0)
    if n != len(data) - 4:
        raise NavMeshFormatError("prefab length prefix %d does not match file length %d" % (n, len(data)))
    return bytes(data[4:])


def encode_prefab(payload: bytes) -> bytes:
    return struct.pack("<I", len(payload)) + bytes(payload)


def read_navmesh_file(path: str):
    """Return ``(kind, container_or_None, payload_bytes)``; kind is ``"rnm1"`` or ``"prefab"``."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:4] == RNM1_MAGIC:
        c = unwrap_rnm1(data)
        return "rnm1", c, c.payload
    return "prefab", None, decode_prefab(data)


def _refuse_official_path(path: str) -> None:
    parts = [p.lower() for p in os.path.normpath(os.path.abspath(path)).split(os.sep)]
    for i, part in enumerate(parts[:-1]):
        if part == "modules" and i + 1 < len(parts) and parts[i + 1] in OFFICIAL_MODULES:
            if any("bannerlord" in q for q in parts[:i]):
                raise PermissionError("refusing to write into an official game module: %s" % path)
        if part == "bin" and any("bannerlord" in q for q in parts[:i]):
            raise PermissionError("refusing to write into the game bin folder: %s" % path)


def _atomic_write(path: str, data: bytes) -> None:
    _refuse_official_path(path)
    directory = os.path.dirname(os.path.abspath(path)) or "."
    os.makedirs(directory, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".navmesh-", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(fd, "wb") as fh:
            fh.write(data)
        os.replace(tmp, path)
    except BaseException:
        if os.path.exists(tmp):
            os.unlink(tmp)
        raise


def write_scene_navmesh(mesh: NavMesh, path: str, compression_mode: str = "fast",
                        scratch0: int = 0, scratch1: int = 0) -> bytes:
    payload = encode_payload(mesh)
    data = wrap_rnm1(payload, compression_mode, scratch0, scratch1)
    if encode_payload(decode_payload(unwrap_rnm1(data).payload)) != payload:
        raise NavMeshFormatError("self-check failed: written bytes do not decode to the mesh")
    _atomic_write(path, data)
    return data


def write_prefab(mesh: NavMesh, path: str) -> bytes:
    payload = encode_payload(mesh)
    data = encode_prefab(payload)
    if encode_payload(decode_payload(decode_prefab(data))) != payload:
        raise NavMeshFormatError("self-check failed: written bytes do not decode to the mesh")
    _atomic_write(path, data)
    return data


# =============================================================================================
# Validation and statistics
# =============================================================================================

def _signed_area_xy(vertices, idx) -> float:
    a = 0.0
    k = len(idx)
    for i in range(k):
        x0, y0 = vertices[idx[i]][0], vertices[idx[i]][1]
        x1, y1 = vertices[idx[(i + 1) % k]][0], vertices[idx[(i + 1) % k]][1]
        a += x0 * y1 - x1 * y0
    return 0.5 * a


def _is_convex_ccw(vertices, idx) -> bool:
    k = len(idx)
    for i in range(k):
        a = vertices[idx[i]]
        b = vertices[idx[(i + 1) % k]]
        c = vertices[idx[(i + 2) % k]]
        if (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]) <= 0.0:
            return False
    return True


def validate(mesh: NavMesh, check_geometry: bool = True) -> dict:
    """Structural validation for generated meshes.

    ``errors`` are invariants that both campaign maps (and 571 of 600 decodable installed files) obey
    and that generated meshes must obey; 29 shipped mission scenes carry a handful of same-direction
    shared edges or CW faces, so the engine evidently tolerates a few.  ``warnings`` are deviations
    that installed campaign maps also exhibit (unreferenced vertices, a duplicated vertex pair).
    """
    errors: List[str] = []
    warnings: List[str] = []
    nv, ne, nf = len(mesh.vertices), len(mesh.edges), len(mesh.faces)
    nint = EDGE_INTS[mesh.version]

    def err(msg):
        if len(errors) < 50:
            errors.append(msg)

    bad_edge_fields = 0
    pair_seen: Dict[Tuple[int, int], int] = {}
    duplicate_pairs = 0
    for ei, rec in enumerate(mesh.edges):
        if len(rec) != nint:
            err("edge %d has %d ints" % (ei, len(rec)))
            continue
        v0, v1 = rec[1], rec[2]
        if not (0 <= v0 < nv and 0 <= v1 < nv):
            err("edge %d references vertex outside 0..%d" % (ei, nv - 1))
        if v0 == v1:
            err("edge %d is degenerate" % ei)
        if rec[0] != -1 or rec[3] != -1 or rec[4] != -1 or (nint == 6 and rec[5] != 0):
            bad_edge_fields += 1
        key = (min(v0, v1), max(v0, v1))
        if key in pair_seen:
            duplicate_pairs += 1
        else:
            pair_seen[key] = ei

    uses: List[List[Tuple[int, int]]] = [[] for _ in range(ne)]
    referenced = bytearray(nv)
    non_ccw = non_convex = bad_meta0 = 0
    for fi, f in enumerate(mesh.faces):
        k = len(f.vertices)
        if k < 3 or len(f.edges) != k:
            err("face %d has degree %d / %d edges" % (fi, k, len(f.edges)))
            continue
        if len(set(f.vertices)) != k:
            err("face %d repeats a vertex" % fi)
        if len(f.meta) != 5:
            err("face %d meta has %d ints" % (fi, len(f.meta)))
            continue
        if f.meta[0] != DEFAULT_FACE_META0:
            bad_meta0 += 1
        ok_refs = True
        for j in range(k):
            a, b, e = f.vertices[j], f.vertices[(j + 1) % k], f.edges[j]
            if not (0 <= a < nv):
                err("face %d vertex %d out of range" % (fi, a))
                ok_refs = False
                continue
            referenced[a] = 1
            if not (0 <= e < ne):
                err("face %d edge %d out of range" % (fi, e))
                ok_refs = False
                continue
            rec = mesh.edges[e]
            if (rec[1], rec[2]) == (a, b):
                uses[e].append((fi, 1))
            elif (rec[1], rec[2]) == (b, a):
                uses[e].append((fi, -1))
            else:
                err("face %d side %d (%d->%d) does not match edge %d (%d,%d)" % (fi, j, a, b, e, rec[1], rec[2]))
                ok_refs = False
        if check_geometry and ok_refs:
            if _signed_area_xy(mesh.vertices, f.vertices) <= 0.0:
                non_ccw += 1
            elif not _is_convex_ccw(mesh.vertices, f.vertices):
                non_convex += 1

    use_hist = collections.Counter(len(u) for u in uses)
    boundary = use_hist.get(1, 0)
    interior = use_hist.get(2, 0)
    unused_edges = use_hist.get(0, 0)
    over_shared = sum(c for k, c in use_hist.items() if k > 2)
    same_direction = sum(1 for u in uses if len(u) == 2 and u[0][1] == u[1][1])
    if over_shared:
        err("%d edges are shared by more than two faces (non-manifold)" % over_shared)
    if same_direction:
        err("%d shared edges are traversed in the same direction by both faces (inconsistent winding)" % same_direction)
    if non_ccw:
        err("%d faces are not counter-clockwise in XY (campaign maps: none; 1 of 600 installed files has some)" % non_ccw)
    if non_convex:
        err("%d faces are not strictly convex in XY (campaign maps: none)" % non_convex)
    if bad_meta0:
        warnings.append("%d faces have meta0 != 2" % bad_meta0)
    if bad_edge_fields:
        warnings.append("%d edges have non-default reserved fields" % bad_edge_fields)
    if duplicate_pairs:
        warnings.append("%d edges duplicate another edge's vertex pair (seam not connected)" % duplicate_pairs)
    if unused_edges:
        warnings.append("%d edges are referenced by no face" % unused_edges)
    unreferenced = nv - sum(referenced)
    if unreferenced:
        warnings.append("%d vertices are referenced by no face" % unreferenced)
    if mesh.tail != STANDARD_TAIL:
        warnings.append("non-standard tail (%d bytes)" % len(mesh.tail))
    if check_geometry:
        for i, vtx in enumerate(mesh.vertices):
            if not all(math.isfinite(c) for c in vtx):
                err("vertex %d is not finite" % i)
                break

    # connected components through shared edges
    parent = list(range(nf))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for u in uses:
        if len(u) == 2:
            ra, rb = find(u[0][0]), find(u[1][0])
            if ra != rb:
                parent[ra] = rb
    islands = len({find(i) for i in range(nf)}) if nf else 0

    return {
        "ok": not errors,
        "errors": errors,
        "warnings": warnings,
        "counts": {"vertices": nv, "edges": ne, "faces": nf, "boundary_edges": boundary,
                   "interior_edges": interior, "unused_edges": unused_edges,
                   "unreferenced_vertices": unreferenced, "duplicate_edge_pairs": duplicate_pairs,
                   "islands": islands},
    }


def mesh_stats(mesh: NavMesh) -> dict:
    groups = collections.Counter(f.meta[2] for f in mesh.faces)
    degrees = collections.Counter(len(f.vertices) for f in mesh.faces)
    meta4 = collections.Counter(f.meta[4] for f in mesh.faces)
    flags = collections.Counter(f.flag for f in mesh.faces)
    if mesh.vertices:
        xs = [v[0] for v in mesh.vertices]
        ys = [v[1] for v in mesh.vertices]
        zs = [v[2] for v in mesh.vertices]
        bounds = {"min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]}
    else:
        bounds = None
    return {
        "version": mesh.version.decode(),
        "vertices": len(mesh.vertices), "edges": len(mesh.edges), "faces": len(mesh.faces),
        "degree_histogram": dict(sorted(degrees.items())),
        "group_histogram": [{"id": g, "count": c, "terrain_type": TERRAIN_TYPES.get(g),
                             "campaign_region": ("land" if g in LAND_TERRAIN_IDS else
                                                 "sea (War Sails; disabled without it)" if g in NAVAL_TERRAIN_IDS
                                                 else "disabled")}
                            for g, c in sorted(groups.items())],
        "meta4_histogram": dict(sorted(meta4.items())),
        "flag_histogram": {str(k): v for k, v in sorted(flags.items(), key=lambda kv: (kv[0] is None, kv[0] or 0))},
        "bounds": bounds,
    }


# =============================================================================================
# Synthesis
# =============================================================================================

FaceIdSource = Union[Callable[[int, int], Optional[int]], Sequence[Sequence[Optional[int]]]]


def build_grid_navmesh(heights: Sequence[Sequence[float]], cell_size: float,
                       face_id_fn: FaceIdSource, origin: Tuple[float, float] = (0.0, 0.0),
                       triangulate: Union[bool, str] = False, version: bytes = b"NMG9",
                       meta4: int = DEFAULT_FACE_META4, meta1: int = 0, meta3: int = 0,
                       flag: int = 0, z_offset: float = 0.0) -> NavMesh:
    """Build a regular-grid navmesh.

    ``heights`` has ``rows = ny + 1`` rows of ``cols = nx + 1`` samples; sample ``heights[j][i]`` is
    placed at ``(origin_x + i*cell_size, origin_y + j*cell_size, heights[j][i] + z_offset)``.
    Cell ``(i, j)`` (column i, row j) becomes a face with group id ``face_id_fn(i, j)`` (or
    ``face_id_fn[j][i]``); ``None`` leaves a hole.

    ``triangulate``: ``False`` -> quads; ``True``/``"fixed"`` -> two triangles per cell split along the
    (i,j)-(i+1,j+1) diagonal; ``"alternate"`` -> checkerboard diagonals; ``"auto"`` -> split each
    cell along the diagonal with the smaller height difference.

    Guarantees (checked by :func:`validate`): CCW faces in XY (as in every installed file), edge j of a
    face joins vertex j and j+1, every interior edge is a single record shared by exactly two faces in
    opposite directions, no duplicate vertex pairs, no unreferenced vertices.
    """
    rows = len(heights)
    if rows < 2:
        raise ValueError("need at least 2 rows of height samples")
    cols = len(heights[0])
    if cols < 2 or any(len(r) != cols for r in heights):
        raise ValueError("height rows must all have the same length >= 2")
    if not (cell_size > 0 and math.isfinite(cell_size)):
        raise ValueError("cell_size must be positive")
    if version not in EDGE_INTS:
        raise ValueError("unsupported version")
    nx, ny = cols - 1, rows - 1
    ox, oy = origin
    if callable(face_id_fn):
        ids = [[face_id_fn(i, j) for i in range(nx)] for j in range(ny)]
    else:
        ids = [list(r) for r in face_id_fn]
        if len(ids) != ny or any(len(r) != nx for r in ids):
            raise ValueError("face id grid must be %d rows x %d columns" % (ny, nx))
    for j in range(ny):
        for i in range(nx):
            g = ids[j][i]
            if g is not None and not (-2 ** 31 <= int(g) < 2 ** 31):
                raise ValueError("face id out of int32 range")

    # vertices: only those used by some face, numbered row-major
    used = [[False] * cols for _ in range(rows)]
    for j in range(ny):
        for i in range(nx):
            if ids[j][i] is not None:
                used[j][i] = used[j][i + 1] = used[j + 1][i] = used[j + 1][i + 1] = True
    vindex = [[-1] * cols for _ in range(rows)]
    vertices: List[Tuple[float, float, float]] = []
    for j in range(rows):
        for i in range(cols):
            if used[j][i]:
                z = float(heights[j][i]) + z_offset
                if not math.isfinite(z):
                    raise ValueError("non-finite height at (%d, %d)" % (i, j))
                vindex[j][i] = len(vertices)
                vertices.append((_f32(ox + i * cell_size), _f32(oy + j * cell_size), _f32(z)))

    edges: List[Tuple[int, ...]] = []
    edge_of: Dict[Tuple[int, int], int] = {}
    extra = (0,) if EDGE_INTS[version] == 6 else ()

    def edge_id(a: int, b: int) -> int:
        key = (a, b) if a < b else (b, a)
        e = edge_of.get(key)
        if e is None:
            e = len(edges)
            edge_of[key] = e
            edges.append((-1, a, b, -1, -1) + extra)
        return e

    faces: List[NavFace] = []
    face_flag = flag if FACE_HAS_FLAG[version] else None

    def add_face(loop: Sequence[int], group: int) -> None:
        k = len(loop)
        eids = [edge_id(loop[t], loop[(t + 1) % k]) for t in range(k)]
        faces.append(NavFace(loop, eids, (DEFAULT_FACE_META0, meta1, int(group), meta3, meta4), face_flag))

    mode = "quad" if not triangulate else ("fixed" if triangulate is True else str(triangulate))
    if mode not in ("quad", "fixed", "alternate", "auto"):
        raise ValueError("triangulate must be False, True, 'fixed', 'alternate' or 'auto'")
    for j in range(ny):
        for i in range(nx):
            g = ids[j][i]
            if g is None:
                continue
            v00, v10 = vindex[j][i], vindex[j][i + 1]
            v11, v01 = vindex[j + 1][i + 1], vindex[j + 1][i]
            if mode == "quad":
                add_face((v00, v10, v11, v01), g)
                continue
            if mode == "fixed":
                main_diag = True
            elif mode == "alternate":
                main_diag = (i + j) % 2 == 0
            else:
                main_diag = abs(heights[j][i] - heights[j + 1][i + 1]) <= abs(heights[j][i + 1] - heights[j + 1][i])
            if main_diag:   # split along v00-v11
                add_face((v00, v10, v11), g)
                add_face((v00, v11, v01), g)
            else:           # split along v10-v01
                add_face((v00, v10, v01), g)
                add_face((v10, v11, v01), g)
    return NavMesh(version, vertices, edges, faces, STANDARD_TAIL)


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


def navigation_fixture_prefab(seed: int) -> bytes:
    """Bytes that src/MNBL.ProceduralWorld/Diagnostics/NavigationFixture.cs writes for ``seed``:
    a 3x2-vertex, two-quad NMG9 prefab, vertex (10x, 10y, 1 + (seed-42)*x), face groups 0 and 1."""
    heights = [[1.0 + (seed - 42) * x for x in range(3)] for _ in range(2)]
    mesh = build_grid_navmesh(heights, 10.0, lambda i, j: i, meta4=-1)
    return encode_prefab(encode_payload(mesh))


def read_mnblhf01(path: str) -> List[List[float]]:
    """Read the repository's diagnostic heightfield stream: ASCII ``MNBLHF01``, u32 side, row-major f64."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:8] != b"MNBLHF01":
        raise ValueError("not an MNBLHF01 heightfield")
    (side,) = struct.unpack_from("<I", data, 8)
    if len(data) != 12 + 8 * side * side:
        raise ValueError("heightfield length does not match side %d" % side)
    flat = struct.unpack_from("<%dd" % (side * side), data, 12)
    return [list(flat[r * side:(r + 1) * side]) for r in range(side)]


def campaign_face_id_rule(heights: Sequence[Sequence[float]], sea_level: float,
                          open_sea_depth: float, mountain_slope: float, cell_size: float,
                          naval: bool = True) -> Callable[[int, int], int]:
    """Simple height/slope classifier used by ``synth``: below sea level -> CoastalSea (18) or
    OpenSea (19) (Water 10 without War Sails); steep -> Mountain (7, disabled); else Plain (1)."""
    def rule(i: int, j: int) -> int:
        hs = (heights[j][i], heights[j][i + 1], heights[j + 1][i], heights[j + 1][i + 1])
        mean = sum(hs) / 4.0
        if mean < sea_level:
            if not naval:
                return 10
            return 19 if mean < sea_level - open_sea_depth else 18
        slope = (max(hs) - min(hs)) / cell_size
        return 7 if slope > mountain_slope else 1
    return rule


# =============================================================================================
# CLI
# =============================================================================================

def _iter_inputs(paths: Iterable[str]):
    for p in paths:
        if os.path.isdir(p):
            for root, _dirs, files in os.walk(p):
                for name in sorted(files):
                    if name.lower().endswith(".bin") and (name.lower().startswith("navmesh")
                                                          or os.path.basename(root).lower() == "navmeshprefabs"):
                        yield os.path.join(root, name)
        else:
            yield p


def roundtrip_file(path: str, recompress: bool = False) -> dict:
    with open(path, "rb") as fh:
        data = fh.read()
    result = {"path": path, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    try:
        if data[:4] == RNM1_MAGIC:
            c = unwrap_rnm1(data)
            payload = c.payload
            result.update(container="rnm1", scratch0="0x%x" % c.scratch0, scratch1="0x%x" % c.scratch1)
        else:
            payload = decode_prefab(data)
            result["container"] = "prefab"
        result["version"] = payload[:4].decode("ascii", "replace")
        result["payload_bytes"] = len(payload)
        result["payload_sha256"] = hashlib.sha256(payload).hexdigest()
        if payload[:4] in LEGACY_UNDECODED:
            result["status"] = "undecoded-legacy"
            return result
        mesh = decode_payload(payload)
        re_payload = encode_payload(mesh)
        result["payload_identical"] = re_payload == payload
        if result["container"] == "rnm1":
            rebuilt = wrap_rnm1(re_payload, scratch0=c.scratch0, scratch1=c.scratch1, block=c.block)
        else:
            rebuilt = encode_prefab(re_payload)
        result["file_identical"] = rebuilt == data
        if recompress and result["container"] == "rnm1":
            ours = lz4_block_compress(re_payload, "fast")
            result["recompressed_bytes"] = len(ours)
            result["recompressed_decodes_identical"] = lz4_block_decompress(ours, len(re_payload)) == payload
            result["recompressed_equals_engine_block"] = ours == c.block
        v = validate(mesh, check_geometry=False)
        result["faces"] = len(mesh.faces)
        result["structural_errors"] = len(v["errors"])
        result["status"] = "ok" if result["payload_identical"] and result["file_identical"] else "MISMATCH"
    except (NavMeshFormatError, struct.error) as exc:
        result["status"] = "error"
        result["error"] = str(exc)
    return result


def _cmd_inspect(args) -> int:
    kind, container, payload = read_navmesh_file(args.file)
    info = {"file": args.file, "container": kind, "payload_magic": payload[:4].decode("ascii", "replace"),
            "payload_bytes": len(payload)}
    if container is not None:
        info.update(compression=container.compression, reserved=container.reserved,
                    compressed_block_bytes=len(container.block or b""),
                    scratch0="0x%016x" % container.scratch0, scratch1="0x%016x" % container.scratch1)
    if payload[:4] not in LEGACY_UNDECODED:
        mesh = decode_payload(payload)
        info["validation"] = validate(mesh)
        st = mesh_stats(mesh)
        info["groups"] = {str(g["id"]): g["count"] for g in st["group_histogram"]}
        info["bounds"] = st["bounds"]
    _emit(info, args.json)
    return 0 if info.get("validation", {"ok": True})["ok"] else 1


def _cmd_stats(args) -> int:
    _kind, _c, payload = read_navmesh_file(args.file)
    mesh = decode_payload(payload)
    st = mesh_stats(mesh)
    st["validation_counts"] = validate(mesh, check_geometry=False)["counts"]
    _emit(st, args.json)
    return 0


def _cmd_roundtrip(args) -> int:
    results = []
    for path in _iter_inputs(args.paths):
        r = roundtrip_file(path, args.recompress)
        results.append(r)
        if not args.quiet:
            print("%-9s %s %s" % (r["status"], r.get("version", "?"), path))
    summary = collections.Counter(r["status"] for r in results)
    out = {"summary": dict(summary), "files": len(results), "results": results}
    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=1)
    print(json.dumps(out["summary"]))
    bad = sum(c for s, c in summary.items() if s not in ("ok", "undecoded-legacy"))
    return 1 if bad else 0


def _cmd_synth(args) -> int:
    if args.fixture_seed is not None:
        data = navigation_fixture_prefab(args.fixture_seed)
        _atomic_write(args.out, data)
        print(json.dumps({"out": args.out, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}))
        return 0
    if args.heightfield:
        heights = [[z * args.height_scale for z in row] for row in read_mnblhf01(args.heightfield)]
        if args.stride > 1:
            heights = [row[::args.stride] for row in heights[::args.stride]]
        cell = args.cell * args.stride
    else:
        w, h = args.width, args.height
        if not w or not h:
            raise SystemExit("--width/--height or --heightfield is required")
        # deterministic synthetic relief: a low island with a ridge, sea around it
        cx, cy = w / 2.0, h / 2.0
        heights = []
        for j in range(h + 1):
            row = []
            for i in range(w + 1):
                r = math.hypot((i - cx) / cx, (j - cy) / cy)
                row.append(12.0 * (1.0 - r) + 3.0 * math.sin(i * 0.37 + args.seed) * math.cos(j * 0.23))
            heights.append(row)
        cell = args.cell
    rule = campaign_face_id_rule(heights, args.sea_level, args.open_sea_depth, args.mountain_slope, cell,
                                 naval=not args.no_naval)
    tri = {"none": False, "fixed": "fixed", "alternate": "alternate", "auto": "auto"}[args.triangulate]
    mesh = build_grid_navmesh(heights, cell, rule, origin=(args.origin_x, args.origin_y), triangulate=tri,
                              meta4=args.meta4)
    report = validate(mesh)
    if not report["ok"]:
        print(json.dumps(report, indent=1))
        return 1
    if args.prefab:
        data = write_prefab(mesh, args.out)
    else:
        data = write_scene_navmesh(mesh, args.out, args.compression)
    st = mesh_stats(mesh)
    print(json.dumps({"out": args.out, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                      "vertices": st["vertices"], "edges": st["edges"], "faces": st["faces"],
                      "groups": {str(g["id"]): g["count"] for g in st["group_histogram"]},
                      "validation": report["counts"]}, indent=1))
    return 0


def _emit(obj, as_json: bool) -> None:
    if as_json:
        print(json.dumps(obj, indent=1))
        return
    for k, v in obj.items():
        if isinstance(v, (dict, list)):
            print("%s: %s" % (k, json.dumps(v)))
        else:
            print("%s: %s" % (k, v))


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("inspect", help="container header, counts and validation")
    p.add_argument("file")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=_cmd_inspect)
    p = sub.add_parser("stats", help="face/vertex counts, id histogram, bounds")
    p.add_argument("file")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=_cmd_stats)
    p = sub.add_parser("roundtrip", help="parse+serialize and compare bytes")
    p.add_argument("paths", nargs="+")
    p.add_argument("--recompress", action="store_true", help="also check this module's LZ4 encoder")
    p.add_argument("--json", help="write full results to this path")
    p.add_argument("--quiet", action="store_true")
    p.set_defaults(fn=_cmd_roundtrip)
    p = sub.add_parser("synth", help="write a synthesized grid navmesh")
    p.add_argument("--out", required=True)
    p.add_argument("--fixture-seed", type=int, help="emit the NavigationFixture.cs prefab for this seed")
    p.add_argument("--heightfield", help="MNBLHF01 heightfield.bin to triangulate")
    p.add_argument("--stride", type=int, default=1, help="heightfield sample stride")
    p.add_argument("--height-scale", type=float, default=1.0, help="multiply heightfield samples")
    p.add_argument("--width", type=int, help="cells in x (synthetic relief)")
    p.add_argument("--height", type=int, help="cells in y (synthetic relief)")
    p.add_argument("--cell", type=float, default=4.0)
    p.add_argument("--origin-x", type=float, default=0.0)
    p.add_argument("--origin-y", type=float, default=0.0)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--sea-level", type=float, default=0.0)
    p.add_argument("--open-sea-depth", type=float, default=4.0)
    p.add_argument("--mountain-slope", type=float, default=1.0)
    p.add_argument("--no-naval", action="store_true", help="use Water (10) instead of sea ids")
    p.add_argument("--triangulate", choices=["none", "fixed", "alternate", "auto"], default="none")
    p.add_argument("--meta4", type=int, default=DEFAULT_FACE_META4)
    p.add_argument("--prefab", action="store_true", help="write a NavMeshPrefabs-style file instead of RNM1")
    p.add_argument("--compression", choices=["fast", "store"], default="fast")
    p.set_defaults(fn=_cmd_synth)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
