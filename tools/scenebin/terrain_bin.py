#!/usr/bin/env python3
"""Reader / writer for Mount & Blade II: Bannerlord ``terrain.bin`` files.

Three container generations are installed with Bannerlord 1.4.8 / War Sails 1.2.8:

* ``ZGR4`` - 16x16 node offset table + one LZ4 blob holding per-node records.
* ``ZGR5`` - like ZGR4, but the blob starts with a global "MIDX" block.
* ``ZGR6RTRN`` - section directory (MIDX, HGHT, NRML, WGHT, PHYM); every
  section is its own LZ4 blob, the per-node sections are prefixed by a
  per-node offset table.

The full field-by-field specification, including what is verified and what is
inferred, is in ``docs/formats/terrain-bin.md``.  Everything here is
standard-library only; Pillow is used for PNG decoding when it is installed
(a pure-Python PNG decoder is the fallback).

CLI::

    python terrain_bin.py inspect  <terrain.bin> [--json]
    python terrain_bin.py roundtrip <terrain.bin>
    python terrain_bin.py heights  <terrain.bin> [--scene scene.xscene] --png out.png [--png8 out8.png]
    python terrain_bin.py synth --size N --out terrain.bin [--node-size 100] [--scene-out scene_terrain.xml]
"""
from __future__ import annotations

import argparse
import array
import io
import json
import math
import os
import re
import struct
import sys
import zlib
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

try:  # Pillow is optional: only used to decode PNGs faster.
    from PIL import Image as _PILImage  # type: ignore
except Exception:  # pragma: no cover - exercised only without Pillow
    _PILImage = None

__all__ = [
    "TerrainFormatError", "lz4_decompress", "lz4_compress", "lz4_store", "Blob", "Section", "NodeRecord",
    "TerrainBin", "parse", "serialize", "decode_heights", "decode_raw_heights", "decode_normals",
    "build_terrain", "compute_normals", "encode_normal_component", "dominant_layers",
    "read_scene_terrain", "png_encode", "png_decode", "png_header", "nodes_xml", "infer_grid_from_edges",
    "SECTION_TAGS", "WEIGHT_SLOTS", "SEASONS", "MIDX_PER_NODE",
]


class TerrainFormatError(ValueError):
    """Raised when bytes do not follow the observed terrain.bin layout."""


# ---------------------------------------------------------------------------
# Constants (all observed in every installed file unless noted in the spec)
# ---------------------------------------------------------------------------
MAGIC_V6 = b"ZGR6RTRN"
MAGIC_V5 = b"ZGR5"
MAGIC_V4 = b"ZGR4"
SECTION_TAGS = ("MIDX", "HGHT", "NRML", "WGHT", "PHYM")
V6_HEADER_SIZE = 12 + 20 * len(SECTION_TAGS)  # 0x70
V45_TABLE_ENTRIES = 256                      # 16 x 16 node grid, u64 each
V45_TABLE_OFFSET = 4
V45_BLOBSIZE_OFFSET = 4 + 8 * V45_TABLE_ENTRIES  # 0x804
V45_BLOB_OFFSET = V45_BLOBSIZE_OFFSET + 4        # 0x808
WEIGHT_SLOTS = 16       # per-node weight-map slots (= scene <weightmap_levels> layers 0..15)
SEASONS = 4             # physics maps: summer, fall, winter, spring
MIDX_PER_NODE = 256     # MIDX images are 256 px per terrain node
MIDX_IMAGES = 4
CODEC_LZ4 = 3
BLOB_HEADER = struct.Struct("<IIQQQQ")  # codec, pad, usize, csize(incl. header), reserved, stamp
PNG_SIG = b"\x89PNG\r\n\x1a\n"
PNG_PHYS_PPM = 2835     # official PNGs carry pHYs 2835 px/m (72.009 dpi)
NORMAL_B_CONST = 63     # official NRML blue channel (constant in every file surveyed)
NORMAL_A_CONST = 0      # official NRML alpha channel (constant in every file surveyed)


# ---------------------------------------------------------------------------
# LZ4 block format (https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md)
# ---------------------------------------------------------------------------
def lz4_decompress(src: bytes, usize: Optional[int] = None) -> bytes:
    """Decompress one raw LZ4 *block* (no frame header)."""
    dst = bytearray()
    i = 0
    n = len(src)
    try:
        while True:
            tok = src[i]
            i += 1
            ll = tok >> 4
            if ll == 15:
                b = 255
                while b == 255:
                    b = src[i]
                    i += 1
                    ll += b
            if ll:
                if i + ll > n:
                    raise TerrainFormatError("LZ4 literal run past end of block")
                dst += src[i:i + ll]
                i += ll
            if i >= n:
                break
            off = src[i] | (src[i + 1] << 8)
            i += 2
            ml = tok & 15
            if ml == 15:
                b = 255
                while b == 255:
                    b = src[i]
                    i += 1
                    ml += b
            ml += 4
            start = len(dst) - off
            if off == 0 or start < 0:
                raise TerrainFormatError("LZ4 match offset out of range")
            if ml <= off:
                dst += dst[start:start + ml]
            else:  # overlapping copy == periodic repetition of the last `off` bytes
                pat = bytes(dst[start:])
                dst += (pat * (ml // off + 1))[:ml]
    except IndexError as exc:
        raise TerrainFormatError("truncated LZ4 block") from exc
    if usize is not None and len(dst) != usize:
        raise TerrainFormatError("LZ4 size mismatch: got %d, header says %d" % (len(dst), usize))
    return bytes(dst)


def _lz4_len(out: bytearray, extra: int) -> None:
    while extra >= 255:
        out.append(255)
        extra -= 255
    out.append(extra)


def _common_length(src: bytes, a: int, b: int, limit: int) -> int:
    """Length of the common run src[a:] == src[b:] with b + length <= limit (a < b)."""
    total = 0
    step = 16
    maxlen = limit - b
    while total < maxlen:
        s = min(step, maxlen - total)
        if src[a + total:a + total + s] == src[b + total:b + total + s]:
            total += s
            if step < 65536:
                step <<= 1
        elif s == 1:
            break
        else:
            step = max(1, s >> 1)
    return total


def lz4_compress(src: bytes, acceleration: int = 1) -> bytes:
    """Greedy LZ4 block compressor (pure Python, spec-conformant end-of-block rules).

    The output is a standard LZ4 block: the last 5 bytes are literals and the
    last match starts at least 12 bytes before the end of the input.
    """
    src = bytes(src)
    n = len(src)
    out = bytearray()
    anchor = 0
    if n >= 13:
        mflimit = n - 12          # last match must start before this
        matchlimit = n - 5        # matches may not cover the last 5 bytes
        table: Dict[bytes, int] = {}
        i = 0
        misses = 1 << 6
        accel = max(1, acceleration)
        while i < mflimit:
            key = src[i:i + 4]
            ref = table.get(key)
            table[key] = i
            if ref is None or i - ref > 65535:
                i += (misses >> 6) * accel
                misses += 1
                continue
            misses = 1 << 6
            ml = 4 + _common_length(src, ref + 4, i + 4, matchlimit)
            while i > anchor and ref > 0 and src[i - 1] == src[ref - 1]:
                i -= 1
                ref -= 1
                ml += 1
            lit = i - anchor
            tok_ll = 15 if lit >= 15 else lit
            tok_ml = 15 if ml - 4 >= 15 else ml - 4
            out.append((tok_ll << 4) | tok_ml)
            if lit >= 15:
                _lz4_len(out, lit - 15)
            out += src[anchor:i]
            off = i - ref
            out.append(off & 255)
            out.append(off >> 8)
            if ml - 4 >= 15:
                _lz4_len(out, ml - 4 - 15)
            i += ml
            anchor = i
            if i - 2 >= 0 and i - 2 + 4 <= n:
                table[src[i - 2:i + 2]] = i - 2
    lit = n - anchor
    out.append((15 if lit >= 15 else lit) << 4)
    if lit >= 15:
        _lz4_len(out, lit - 15)
    out += src[anchor:]
    return bytes(out)


def lz4_store(src: bytes) -> bytes:
    """A valid LZ4 block that contains only literals (no matches)."""
    out = bytearray()
    lit = len(src)
    out.append((15 if lit >= 15 else lit) << 4)
    if lit >= 15:
        _lz4_len(out, lit - 15)
    out += src
    return bytes(out)


# ---------------------------------------------------------------------------
# PNG helpers (standard PNG; the engine stores ordinary PNG files)
# ---------------------------------------------------------------------------
def _png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def png_header(data: bytes) -> Tuple[int, int, int, int, int]:
    """(width, height, bit_depth, colour_type, interlace) from a PNG stream."""
    if data[:8] != PNG_SIG or data[12:16] != b"IHDR":
        raise TerrainFormatError("not a PNG stream")
    w, h, bd, ct, _cm, _fm, il = struct.unpack(">IIBBBBB", data[16:29])
    return w, h, bd, ct, il


_CHANNELS = {0: 1, 2: 3, 4: 2, 6: 4}


def png_encode(width: int, height: int, bit_depth: int, colour_type: int, raw: bytes,
               filter_type: int = 2, level: int = 9, phys: bool = True) -> bytes:
    """Encode raw big-endian samples (row-major, no filter bytes) as a PNG.

    filter_type: 0 = None for every row, 2 = Up for every row (row 0 uses None).
    """
    ch = _CHANNELS[colour_type]
    stride = width * ch * (bit_depth // 8)
    if len(raw) != stride * height:
        raise ValueError("raw size %d != %d" % (len(raw), stride * height))
    parts = []
    prev = None
    for r in range(height):
        row = raw[r * stride:(r + 1) * stride]
        if filter_type == 2 and prev is not None:
            parts.append(b"\x02" + _bytewise_sub(row, prev))
        else:
            parts.append(b"\x00" + row)
        prev = row
    idat = zlib.compress(b"".join(parts), level)
    ihdr = struct.pack(">IIBBBBB", width, height, bit_depth, colour_type, 0, 0, 0)
    out = [PNG_SIG, _png_chunk(b"IHDR", ihdr)]
    if phys:
        out.append(_png_chunk(b"pHYs", struct.pack(">IIB", PNG_PHYS_PPM, PNG_PHYS_PPM, 1)))
    out.append(_png_chunk(b"IDAT", idat))
    out.append(_png_chunk(b"IEND", b""))
    return b"".join(out)


def _bytewise_sub(row: bytes, prev: bytes) -> bytes:
    """(row[i] - prev[i]) & 0xFF for all i without a per-byte Python loop."""
    # Widen every byte to a 16-bit big-integer lane holding 256 + row[i]; subtracting the
    # widened prev leaves 1..511 in each lane, so no borrow crosses lanes.  The low byte of
    # each lane is the wanted difference.
    n = len(row)
    if n == 0:
        return b""
    # Use 16-bit lanes to keep borrows inside the lane.
    r16 = bytearray(2 * n)
    p16 = bytearray(2 * n)
    r16[1::2] = row
    p16[1::2] = prev
    r16[0::2] = b"\x01" * n  # +256 per lane
    big = int.from_bytes(r16, "big") - int.from_bytes(p16, "big")
    lanes = big.to_bytes(2 * n, "big")
    return lanes[1::2]


def _unfilter(data: bytes, width: int, height: int, bpp: int, stride: int) -> bytearray:
    out = bytearray(stride * height)
    prev = bytearray(stride)
    pos = 0
    for r in range(height):
        ft = data[pos]
        line = bytearray(data[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        if ft == 0:
            pass
        elif ft == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 255
        elif ft == 2:
            line = bytearray(_bytewise_add(bytes(line), bytes(prev)))
        elif ft == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        else:
            raise TerrainFormatError("bad PNG filter %d" % ft)
        out[r * stride:(r + 1) * stride] = line
        prev = line
    return out


def _bytewise_add(row: bytes, prev: bytes) -> bytes:
    n = len(row)
    r16 = bytearray(2 * n)
    p16 = bytearray(2 * n)
    r16[1::2] = row
    p16[1::2] = prev
    big = int.from_bytes(r16, "big") + int.from_bytes(p16, "big")  # lanes <= 0x1FE, no carry
    return big.to_bytes(2 * n, "big")[1::2]


def png_decode(data: bytes, use_pillow: Optional[bool] = None):
    """Decode a non-interlaced 8/16-bit grey or RGBA PNG.

    Returns (width, height, channels, samples) where samples is an
    ``array('H')`` for 16-bit images and ``bytes`` for 8-bit images,
    interleaved per pixel, row-major (row 0 first).
    """
    w, h, bd, ct, il = png_header(data)
    ch = _CHANNELS.get(ct)
    if ch is None or bd not in (8, 16) or il:
        raise TerrainFormatError("unsupported PNG (bit depth %d, colour type %d, interlace %d)" % (bd, ct, il))
    if use_pillow is None:
        use_pillow = _PILImage is not None
    if use_pillow and _PILImage is not None:
        im = _PILImage.open(io.BytesIO(data))
        im.load()
        if bd == 16 and ch == 1:
            if im.mode in ("I;16", "I;16L"):
                a = array.array("H")
                a.frombytes(im.tobytes())
                if sys.byteorder != "little":
                    a.byteswap()
                return w, h, 1, a
            if im.mode == "I;16B":
                a = array.array("H")
                a.frombytes(im.tobytes())
                if sys.byteorder == "little":
                    a.byteswap()
                return w, h, 1, a
            vals = im.get_flattened_data() if hasattr(im, "get_flattened_data") else im.getdata()
            return w, h, 1, array.array("H", vals)
        if bd == 8:
            return w, h, ch, im.tobytes()
    # pure-Python path
    p = 8
    idat = []
    while p < len(data):
        ln = struct.unpack(">I", data[p:p + 4])[0]
        kind = data[p + 4:p + 8]
        if kind == b"IDAT":
            idat.append(data[p + 8:p + 8 + ln])
        if kind == b"IEND":
            break
        p += 12 + ln
    raw = zlib.decompress(b"".join(idat))
    bpp = ch * bd // 8
    stride = w * bpp
    pix = _unfilter(raw, w, h, bpp, stride)
    if bd == 16:
        a = array.array("H")
        a.frombytes(bytes(pix))
        if sys.byteorder == "little":
            a.byteswap()
        return w, h, 1 if ch == 1 else ch, a
    return w, h, ch, bytes(pix)


# ---------------------------------------------------------------------------
# Compressed blob (40-byte header + LZ4 block)
# ---------------------------------------------------------------------------
@dataclass
class Blob:
    """One compressed stream.

    Layout (little endian):  u32 codec (3 = LZ4 block) | u32 pad (0) |
    u64 usize | u64 csize (header + payload) | u64 reserved | u64 stamp | payload.
    ``reserved`` and ``stamp`` hold uninitialised memory in official files and
    are preserved verbatim so the round trip is exact.
    """
    codec: int
    pad: int
    usize: int
    csize: int
    reserved: int
    stamp: int
    payload: bytes
    _cache: Optional[bytes] = field(default=None, repr=False, compare=False)

    @classmethod
    def parse(cls, buf: bytes, offset: int, length: int) -> "Blob":
        if length < BLOB_HEADER.size:
            raise TerrainFormatError("blob shorter than its header")
        codec, pad, usize, csize, reserved, stamp = BLOB_HEADER.unpack_from(buf, offset)
        if csize != length:
            raise TerrainFormatError("blob csize %d != available %d" % (csize, length))
        return cls(codec, pad, usize, csize, reserved, stamp, bytes(buf[offset + BLOB_HEADER.size:offset + length]))

    @classmethod
    def from_data(cls, data: bytes, compress: bool = True) -> "Blob":
        payload = lz4_compress(data) if compress else lz4_store(data)
        blob = cls(CODEC_LZ4, 0, len(data), BLOB_HEADER.size + len(payload), 0, 0, payload)
        blob._cache = bytes(data)
        return blob

    def data(self) -> bytes:
        if self._cache is None:
            if self.codec != CODEC_LZ4:
                raise TerrainFormatError("unknown blob codec %d" % self.codec)
            self._cache = lz4_decompress(self.payload, self.usize)
        return self._cache

    def to_bytes(self) -> bytes:
        return BLOB_HEADER.pack(self.codec, self.pad, self.usize, self.csize, self.reserved, self.stamp) + self.payload


# ---------------------------------------------------------------------------
# Per-node record content (shared by all versions)
# ---------------------------------------------------------------------------
@dataclass
class NodeRecord:
    height_png: Optional[bytes] = None           # 16-bit grey, (2^L+1)^2
    normal_png: Optional[bytes] = None           # 8-bit RGBA
    weights: List[Optional[bytes]] = field(default_factory=lambda: [None] * WEIGHT_SLOTS)  # 8-bit grey PNG per layer
    physics: List[Tuple[int, bytes]] = field(default_factory=list)  # [(season, u8 map)] x4
    aux8: Optional[bytes] = None                 # ZGR4/5 only: u8 per sample (meaning unknown)
    weight_flags: Optional[List[int]] = None     # raw slot flags (0 absent / 1 present)


def _read_lenpng(buf: bytes, p: int) -> Tuple[bytes, int]:
    n = struct.unpack_from("<I", buf, p)[0]
    png = buf[p + 4:p + 4 + n]
    if png[:8] != PNG_SIG:
        raise TerrainFormatError("expected PNG at %d" % (p + 4))
    return png, p + 4 + n


def _parse_physics(buf: bytes, p: int) -> Tuple[List[Tuple[int, bytes]], int]:
    out = []
    for _ in range(SEASONS):
        season, n = struct.unpack_from("<II", buf, p)
        if p + 8 + n > len(buf):
            raise TerrainFormatError("physics map past end")
        out.append((season, buf[p + 8:p + 8 + n]))
        p += 8 + n
    return out, p


def _write_physics(phys: Sequence[Tuple[int, bytes]]) -> bytes:
    return b"".join(struct.pack("<II", s, len(m)) + bytes(m) for s, m in phys)


def _parse_weights(buf: bytes, p: int) -> Tuple[List[Optional[bytes]], List[int], int]:
    out: List[Optional[bytes]] = []
    flags = []
    for _ in range(WEIGHT_SLOTS):
        flag = struct.unpack_from("<I", buf, p)[0]
        p += 4
        flags.append(flag)
        if flag:
            png, p = _read_lenpng(buf, p)
            out.append(png)
        else:
            out.append(None)
    return out, flags, p


def _write_weights(weights: Sequence[Optional[bytes]], flags: Optional[Sequence[int]] = None) -> bytes:
    parts = []
    for i, w in enumerate(weights):
        flag = (flags[i] if flags else 1) if w is not None else 0
        parts.append(struct.pack("<I", flag))
        if w is not None:
            parts.append(struct.pack("<I", len(w)) + w)
    return b"".join(parts)


def _lenpng(png: bytes) -> bytes:
    return struct.pack("<I", len(png)) + png


# ---------------------------------------------------------------------------
# ZGR6RTRN sections
# ---------------------------------------------------------------------------
@dataclass
class Section:
    tag: str
    version: int          # always 1 in installed files
    offset: int           # absolute file offset of the section (table first)
    csize: int            # stored size of the blob (header + payload); 0 = empty section
    usize: int            # decompressed size of the blob
    node_offsets: Optional[List[int]]  # None for MIDX (global); offsets into decompressed data
    blob: Optional[Blob]

    def chunks(self) -> List[bytes]:
        """Per-node decompressed chunks (HGHT/NRML/WGHT/PHYM)."""
        if self.blob is None or self.node_offsets is None:
            return []
        data = self.blob.data()
        offs = self.node_offsets
        return [data[offs[i]:(offs[i + 1] if i + 1 < len(offs) else len(data))] for i in range(len(offs))]


@dataclass
class TerrainBin:
    """Object model of one terrain.bin.  ``version`` is 4, 5 or 6."""
    version: int
    # ZGR6
    rtrn_version: int = 2
    sections: List[Section] = field(default_factory=list)
    # ZGR4 / ZGR5
    node_table: List[int] = field(default_factory=list)  # 256 u64 offsets into blob data, [idx*16 + idy]
    blob: Optional[Blob] = None
    trailing: bytes = b""                                 # bytes after the last structure (none observed)

    # ---------------- parsing ----------------
    @classmethod
    def parse(cls, data: bytes) -> "TerrainBin":
        data = bytes(data)
        if data[:8] == MAGIC_V6:
            return cls._parse_v6(data)
        if data[:4] in (MAGIC_V4, MAGIC_V5):
            return cls._parse_v45(data)
        raise TerrainFormatError("unknown magic %r" % data[:8])

    @classmethod
    def _parse_v6(cls, data: bytes) -> "TerrainBin":
        rtrn_version = struct.unpack_from("<I", data, 8)[0]
        dirs = []
        for i in range(len(SECTION_TAGS)):
            base = 12 + 20 * i
            tag = data[base:base + 4].decode("latin1")
            ver, off, csize, usize = struct.unpack_from("<4I", data, base + 4)
            dirs.append((tag, ver, off, csize, usize))
        if [d[0] for d in dirs] != list(SECTION_TAGS):
            raise TerrainFormatError("unexpected section tags %r" % [d[0] for d in dirs])
        order = sorted(range(len(dirs)), key=lambda k: (dirs[k][2], k))
        sections: List[Optional[Section]] = [None] * len(dirs)
        node_count = None
        for rank, k in enumerate(order):
            tag, ver, off, csize, usize = dirs[k]
            end = len(data)
            for k2 in order[rank + 1:]:
                if dirs[k2][2] > off:
                    end = dirs[k2][2]
                    break
            if csize == 0:
                sections[k] = Section(tag, ver, off, 0, usize, None if tag == "MIDX" else [], None)
                continue
            table_bytes = end - off - csize
            if tag == "MIDX":
                if table_bytes != 0:
                    raise TerrainFormatError("MIDX has %d unexpected bytes" % table_bytes)
                blob = Blob.parse(data, off, csize)
                sections[k] = Section(tag, ver, off, csize, usize, None, blob)
                continue
            if table_bytes <= 0 or table_bytes % 4:
                raise TerrainFormatError("%s: bad node table size %d" % (tag, table_bytes))
            n = table_bytes // 4
            if node_count is None:
                node_count = n
            elif node_count != n:
                raise TerrainFormatError("%s: node count %d != %d" % (tag, n, node_count))
            offs = list(struct.unpack_from("<%dI" % n, data, off))
            blob = Blob.parse(data, off + table_bytes, csize)
            if blob.usize != usize:
                raise TerrainFormatError("%s: directory usize %d != blob usize %d" % (tag, usize, blob.usize))
            sections[k] = Section(tag, ver, off, csize, usize, offs, blob)
        return cls(version=6, rtrn_version=rtrn_version, sections=[s for s in sections if s is not None])

    @classmethod
    def _parse_v45(cls, data: bytes) -> "TerrainBin":
        version = 4 if data[:4] == MAGIC_V4 else 5
        table = list(struct.unpack_from("<%dQ" % V45_TABLE_ENTRIES, data, V45_TABLE_OFFSET))
        bsize = struct.unpack_from("<I", data, V45_BLOBSIZE_OFFSET)[0]
        blob = Blob.parse(data, V45_BLOB_OFFSET, bsize)
        trailing = data[V45_BLOB_OFFSET + bsize:]
        return cls(version=version, node_table=table, blob=blob, trailing=trailing)

    # ---------------- serialisation ----------------
    def serialize(self) -> bytes:
        if self.version == 6:
            return self._serialize_v6()
        return self._serialize_v45()

    def _serialize_v6(self) -> bytes:
        body = bytearray()
        dir_entries = []
        pos = V6_HEADER_SIZE
        by_tag = {s.tag: s for s in self.sections}
        for tag in SECTION_TAGS:
            s = by_tag[tag]
            if s.blob is None:
                dir_entries.append((tag, s.version, pos, 0, s.usize))
                continue
            blob_bytes = s.blob.to_bytes()
            chunk = bytearray()
            if s.node_offsets is not None:
                chunk += struct.pack("<%dI" % len(s.node_offsets), *s.node_offsets)
            chunk += blob_bytes
            dir_entries.append((tag, s.version, pos, len(blob_bytes), s.blob.usize))
            body += chunk
            pos += len(chunk)
        head = bytearray(MAGIC_V6 + struct.pack("<I", self.rtrn_version))
        for tag, ver, off, csize, usize in dir_entries:
            head += tag.encode("latin1") + struct.pack("<4I", ver, off, csize, usize)
        return bytes(head + body)

    def _serialize_v45(self) -> bytes:
        magic = MAGIC_V4 if self.version == 4 else MAGIC_V5
        blob_bytes = self.blob.to_bytes() if self.blob else b""
        return (magic + struct.pack("<%dQ" % V45_TABLE_ENTRIES, *self.node_table)
                + struct.pack("<I", len(blob_bytes)) + blob_bytes + self.trailing)

    # ---------------- logical content ----------------
    def section(self, tag: str) -> Section:
        for s in self.sections:
            if s.tag == tag:
                return s
        raise KeyError(tag)

    @property
    def node_count(self) -> int:
        if self.version == 6:
            for s in self.sections:
                if s.node_offsets:
                    return len(s.node_offsets)
            return 0
        return len(self.v45_node_positions())

    def v45_node_positions(self) -> List[Tuple[int, int, int]]:
        """[(idx, idy, offset)] for every used 16x16 table entry, in offset order.

        The table is x-major: entry index = idx * 16 + idy.
        """
        used = [(i // 16, i % 16, v) for i, v in enumerate(self.node_table) if v or i == 0]
        return sorted(used, key=lambda t: t[2])

    def midx_images(self) -> List[bytes]:
        """Global MIDX PNG streams (4 in every installed ZGR5/ZGR6 file; ZGR4 has none)."""
        if self.version == 6:
            s = self.section("MIDX")
            if s.blob is None:
                return []
            return _split_lenpngs(s.blob.data())
        if self.version == 5:
            data = self.blob.data()
            first = min(v for _x, _y, v in self.v45_node_positions())
            return _split_lenpngs(data[4:first])
        return []

    def midx_header(self) -> Optional[int]:
        """ZGR5 only: the u32 that precedes the MIDX images (2 in 25 files, 1 in 10; meaning unknown)."""
        if self.version == 5:
            return struct.unpack_from("<I", self.blob.data(), 0)[0]
        return None

    def nodes(self) -> List[NodeRecord]:
        """Per-node records in storage order (ZGR6: index = idy*nx + idx)."""
        if self.version == 6:
            n = self.node_count
            recs = [NodeRecord() for _ in range(n)]
            for s in self.sections:
                if s.tag == "MIDX" or s.blob is None:
                    continue
                for i, c in enumerate(s.chunks()):
                    r = recs[i]
                    if s.tag == "HGHT":
                        r.height_png, p = _read_lenpng(c, 0)
                    elif s.tag == "NRML":
                        r.normal_png, p = _read_lenpng(c, 0)
                    elif s.tag == "WGHT":
                        r.weights, r.weight_flags, p = _parse_weights(c, 0)
                    else:
                        r.physics, p = _parse_physics(c, 0)
                    if p != len(c):
                        raise TerrainFormatError("%s node %d: %d trailing bytes" % (s.tag, i, len(c) - p))
            return recs
        data = self.blob.data()
        pos = self.v45_node_positions()
        recs = []
        for k, (_x, _y, off) in enumerate(pos):
            end = pos[k + 1][2] if k + 1 < len(pos) else len(data)
            c = data[off:end]
            r = NodeRecord()
            r.physics, p = _parse_physics(c, 0)
            n = struct.unpack_from("<I", c, p)[0]
            r.aux8 = c[p + 4:p + 4 + n]
            p += 4 + n
            r.height_png, p = _read_lenpng(c, p)
            r.weights, r.weight_flags, p = _parse_weights(c, p)
            r.normal_png, p = _read_lenpng(c, p)
            if p != len(c):
                raise TerrainFormatError("node %d: %d trailing bytes" % (k, len(c) - p))
            recs.append(r)
        return recs

    def node_grid_positions(self, nx: Optional[int] = None) -> List[Tuple[int, int]]:
        """(idx, idy) of each record returned by nodes()."""
        if self.version == 6:
            if nx is None:
                nx, _ny = self.grid_shape()
            return [(i % nx, i // nx) for i in range(self.node_count)]
        return [(x, y) for x, y, _o in self.v45_node_positions()]

    def grid_shape(self) -> Tuple[int, int]:
        """(node_dimension_x, node_dimension_y).

        ZGR4/5: from the 16x16 table.  ZGR6: from the MIDX image size
        (256 px per node, verified on every installed ZGR6 file), falling back
        to shared-edge matching of the height maps.
        """
        if self.version != 6:
            pos = self.v45_node_positions()
            return max(p[0] for p in pos) + 1, max(p[1] for p in pos) + 1
        imgs = self.midx_images()
        if imgs:
            w, h, _bd, _ct, _il = png_header(imgs[0])
            if w % MIDX_PER_NODE == 0 and h % MIDX_PER_NODE == 0 and (w // MIDX_PER_NODE) * (h // MIDX_PER_NODE) == self.node_count:
                return w // MIDX_PER_NODE, h // MIDX_PER_NODE
        return infer_grid_from_edges(self)


def _split_lenpngs(buf: bytes) -> List[bytes]:
    out = []
    p = 0
    while p < len(buf):
        png, p = _read_lenpng(buf, p)
        out.append(png)
    return out


def parse(data: bytes) -> TerrainBin:
    return TerrainBin.parse(data)


def serialize(model: TerrainBin) -> bytes:
    return model.serialize()


# ---------------------------------------------------------------------------
# Decoding helpers
# ---------------------------------------------------------------------------
def _node_heights(png: bytes):
    w, h, ch, a = png_decode(png)
    if ch != 1 or not isinstance(a, array.array):
        raise TerrainFormatError("height map is not 16-bit grey")
    return w, h, a


def infer_grid_from_edges(model: TerrainBin) -> Tuple[int, int]:
    """Choose (nx, ny) whose shared node edges match exactly (ZGR6 fallback)."""
    recs = model.nodes()
    n = len(recs)
    maps = [_node_heights(r.height_png) for r in recs]
    best = None
    for nx in range(1, n + 1):
        if n % nx:
            continue
        ny = n // nx
        bad = 0
        checked = 0
        for i in range(n):
            x, y = i % nx, i // nx
            w, h, a = maps[i]
            if x + 1 < nx:
                w2, h2, b = maps[i + 1]
                if w == w2 and h == h2:
                    checked += 1
                    bad += any(a[r * w + w - 1] != b[r * w2] for r in range(h))
            if y + 1 < ny:
                w2, h2, b = maps[i + nx]
                if w == w2 and h == h2:
                    checked += 1
                    bad += a[(h - 1) * w:h * w] != b[:w2]
        score = (bad / max(checked, 1), -checked)
        if best is None or score < best[0]:
            best = (score, (nx, ny))
    return best[1]


def _resample_square(a, w: int, size: int) -> List[float]:
    """Bilinear resample a w x w grid to size x size (corner aligned)."""
    if w == size:
        return [float(v) for v in a]
    out = [0.0] * (size * size)
    f = (w - 1) / (size - 1)
    for r in range(size):
        y = r * f
        y0 = min(int(y), w - 2)
        ty = y - y0
        for c in range(size):
            x = c * f
            x0 = min(int(x), w - 2)
            tx = x - x0
            v00 = a[y0 * w + x0]
            v01 = a[y0 * w + x0 + 1]
            v10 = a[(y0 + 1) * w + x0]
            v11 = a[(y0 + 1) * w + x0 + 1]
            out[r * size + c] = (v00 * (1 - tx) + v01 * tx) * (1 - ty) + (v10 * (1 - tx) + v11 * tx) * ty
    return out


def decode_raw_heights(model: TerrainBin, nx: Optional[int] = None, ny: Optional[int] = None):
    """Stitch the 16-bit node maps into one grid.

    Returns (width, height, samples) where samples is row-major (row = +Y,
    column = +X) of raw 16-bit values (floats when nodes of lower resolution
    had to be bilinearly upsampled).  Adjacent nodes share their border
    samples, so the global grid is (nx*(R-1)+1) x (ny*(R-1)+1).
    """
    if nx is None or ny is None:
        gx, gy = model.grid_shape()
        nx = nx or gx
        ny = ny or gy
    recs = model.nodes()
    pos = model.node_grid_positions(nx)
    maps = [_node_heights(r.height_png) for r in recs]
    R = max(m[0] for m in maps)
    W = nx * (R - 1) + 1
    H = ny * (R - 1) + 1
    mixed = any(m[0] != R for m in maps)
    out = array.array("d", bytes(8 * W * H)) if mixed else array.array("H", bytes(2 * W * H))
    for (x, y), (w, h, a) in zip(pos, maps):
        if w != h:
            raise TerrainFormatError("non-square node map")
        src = array.array("d", _resample_square(a, w, R)) if mixed else a
        for r in range(R):
            row0 = (y * (R - 1) + r) * W + x * (R - 1)
            out[row0:row0 + R] = src[r * R:(r + 1) * R]
    return W, H, out


def decode_heights(model: TerrainBin, min_height: Optional[float] = None, max_height: Optional[float] = None,
                   nx: Optional[int] = None, ny: Optional[int] = None,
                   xscene_path: Optional[str] = None) -> List[List[float]]:
    """2-D height grid in world units: grid[row][col], row = +Y, col = +X.

    height = min_height + v * (max_height - min_height) / 65535, where v is the
    16-bit PNG sample.  terrain.bin does not store min/max: they come from the
    scene.xscene <terrain min_height max_height> attributes (pass them, or pass
    xscene_path).  Sample (row, col) sits at world (col*s, row*s) with
    s = node_size / (R - 1), R = node map width.
    """
    if (min_height is None or max_height is None) and xscene_path:
        st = read_scene_terrain(xscene_path)
        min_height = st["min_height"] if min_height is None else min_height
        max_height = st["max_height"] if max_height is None else max_height
        nx = nx or st["node_dimension_x"]
        ny = ny or st["node_dimension_y"]
    if min_height is None or max_height is None:
        raise ValueError("min_height/max_height (scene.xscene <terrain>) are required for world units")
    W, H, raw = decode_raw_heights(model, nx, ny)
    scale = (max_height - min_height) / 65535.0
    return [[min_height + raw[r * W + c] * scale for c in range(W)] for r in range(H)]


def decode_normals(png: bytes) -> List[Tuple[float, float, float]]:
    """Decode one NRML node map to unit vectors (x, y, z) with z reconstructed.

    Channels are floor(n*127.5 + 127.5); the bucket midpoint (v + 0.5)/127.5 - 1
    is used, so a flat normal (127, 127) decodes to exactly (0, 0, 1).
    """
    w, h, ch, px = png_decode(png)
    if ch != 4:
        raise TerrainFormatError("normal map is not RGBA")
    out = []
    for i in range(w * h):
        nx = (px[4 * i] + 0.5) / 127.5 - 1.0
        ny = (px[4 * i + 1] + 0.5) / 127.5 - 1.0
        nz = math.sqrt(max(0.0, 1.0 - nx * nx - ny * ny))
        out.append((nx, ny, nz))
    return out


# ---------------------------------------------------------------------------
# scene.xscene helpers
# ---------------------------------------------------------------------------
def read_scene_terrain(xscene_path: str) -> Dict[str, object]:
    """Read the <terrain> attributes, layer names and per-node attributes from scene.xscene."""
    with open(xscene_path, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    i = text.find("<terrain ")
    if i < 0:
        raise TerrainFormatError("no <terrain> element in %s" % xscene_path)
    j = text.find("</terrain>", i)
    block = text[i:j if j > 0 else len(text)]
    head = block[:block.find(">") + 1]
    attrs = dict(re.findall(r'(\w+)="([^"]*)"', head))
    layers = re.findall(r'<layer name="([^"]*)"', block)
    nodes = []
    for m in re.finditer(r"<node ([^>]*)>(.*?)</node>", block, re.S):
        a = dict(re.findall(r'(\w+)="([^"]*)"', m.group(1)))
        levels = dict(re.findall(r'<variable name="(\w+)" value="([^"]*)"', m.group(2)))
        wl = re.findall(r'<variable layer="(\d+)" value="(\d+)"', m.group(2))
        a["texture_levels"] = levels
        a["weightmap_levels"] = {int(k): int(v) for k, v in wl}
        nodes.append(a)

    def num(k, d=None, f=float):
        try:
            return f(attrs[k])
        except Exception:
            return d
    return {
        "attrs": attrs,
        "node_dimension_x": num("node_dimension_x", None, int),
        "node_dimension_y": num("node_dimension_y", None, int),
        "node_size": num("node_size"),
        "min_height": num("min_height"),
        "max_height": num("max_height"),
        "layers": layers,
        "nodes": nodes,
    }


def nodes_xml(info: Dict[str, object], indent: str = "\t\t") -> str:
    """Render a <nodes> block for scene.xscene from build_terrain()'s info dict."""
    lines = [indent + "<nodes>"]
    level = info["heightmap_level"]
    for nd in info["nodes"]:
        m = nd["layer_mask"]
        lines.append(indent + '\t<node idx="%d" idy="%d" occluders_enabled="true" layer_is_used_mask_summer="%d" '
                     'layer_is_used_mask_fall="%d" layer_is_used_mask_winter="%d" layer_is_used_mask_spring="%d" '
                     'layer_is_used_mask="true" height_complexity="%.3f" min_height="%.3f" max_height="%.3f">'
                     % (nd["idx"], nd["idy"], m, m, m, m, nd["height_complexity"], nd["min_height"], nd["max_height"]))
        lines.append(indent + "\t\t<texture_levels>")
        lines.append(indent + '\t\t\t<variable name="heightmap_level" value="%d"/>' % level)
        lines.append(indent + '\t\t\t<variable name="min_heightmap_level" value="2"/>')
        lines.append(indent + '\t\t\t<variable name="normalmap_level" value="%d"/>' % level)
        lines.append(indent + '\t\t\t<variable name="weightmap_levels">')
        for k in range(WEIGHT_SLOTS):
            lines.append(indent + '\t\t\t\t<variable layer="%d" value="%d"/>' % (k, level if (m >> k) & 1 else 0))
        lines.append(indent + "\t\t\t</variable>")
        lines.append(indent + "\t\t</texture_levels>")
        lines.append(indent + "\t</node>")
    lines.append(indent + "</nodes>")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------
def _quantize(h: float, lo: float, hi: float) -> int:
    v = int(round((h - lo) / (hi - lo) * 65535.0))
    return 0 if v < 0 else (65535 if v > 65535 else v)


def encode_normal_component(n: float) -> int:
    """8-bit normal channel: floor(n * 127.5 + 127.5), clamped (best fit to official data)."""
    v = int(math.floor(n * 127.5 + 127.5))
    return 0 if v < 0 else (255 if v > 255 else v)


def compute_normals(q: Sequence[int], W: int, H: int, spacing: float, scale: float) -> bytearray:
    """RGBA normal bytes for a global quantised grid.

    Sobel gradient on the stitched grid (edge samples clamped), world-space
    normal n = normalize(-dh/dx, -dh/dy, 1); R = x, G = y, B = 63, A = 0.
    Computing on the stitched grid makes shared node edges identical, as in
    official files.  (The official kernel is not known exactly; Sobel + floor
    reproduces 63% of official samples exactly and the rest within a few codes.)
    """
    out = bytearray(4 * W * H)
    k = scale / (4.0 * spacing)  # Sobel weights sum to 4; divided by the sample distance below
    for r in range(H):
        r0 = r - 1 if r > 0 else 0
        r1 = r + 1 if r < H - 1 else H - 1
        ra = r0 * W
        rb = r * W
        rc = r1 * W
        ky = k / (r1 - r0)
        for c in range(W):
            c0 = c - 1 if c > 0 else 0
            c1 = c + 1 if c < W - 1 else W - 1
            dx = ((q[ra + c1] + 2 * q[rb + c1] + q[rc + c1]) - (q[ra + c0] + 2 * q[rb + c0] + q[rc + c0])) * (k / (c1 - c0))
            dy = ((q[rc + c0] + 2 * q[rc + c] + q[rc + c1]) - (q[ra + c0] + 2 * q[ra + c] + q[ra + c1])) * ky
            inv = 1.0 / math.sqrt(dx * dx + dy * dy + 1.0)
            o = 4 * (rb + c)
            out[o] = encode_normal_component(-dx * inv)
            out[o + 1] = encode_normal_component(-dy * inv)
            out[o + 2] = NORMAL_B_CONST
            out[o + 3] = NORMAL_A_CONST
    return out


def dominant_layers(weights: Dict[int, Sequence[int]], count: int) -> bytearray:
    """Per-sample dominant layer used for PHYM.

    Layers are alpha-blended bottom-up (layer 0 = opaque base); the layer with
    the largest visible contribution w_k * prod_{j>k}(1 - w_j) wins, ties go to
    the higher layer.  This reproduces 99.8% of battle_terrain_001's PHYM; see
    the spec for the open flora-layer question.
    """
    out = bytearray(count)
    layers = sorted((k for k in weights if k != 0), reverse=True)
    if not layers:
        return out
    for i in range(count):
        rem = 1.0
        best = -1.0
        arg = 0
        for k in layers:
            a = weights[k][i] / 255.0
            if a:
                c = a * rem
                if c > best + 1e-12:
                    best = c
                    arg = k
                rem *= 1.0 - a
            if rem <= 0.0:
                break
        base = (weights[0][i] / 255.0 if 0 in weights else 1.0) * rem
        out[i] = arg if best >= base and best > 0.0 else 0
    return out


def build_terrain(heights_2d: Sequence[Sequence[float]], node_dimension_x: int, node_dimension_y: int,
                  node_size: float, layer_weights: Optional[Dict[int, Sequence[Sequence[int]]]] = None,
                  physics=None, min_height: Optional[float] = None,
                  max_height: Optional[float] = None, compress: bool = True,
                  write_normals: bool = True, write_weights: bool = True,
                  midx_value: int = 255,
                  physics_layers: Optional[Iterable[int]] = None) -> Tuple[bytes, Dict[str, object]]:
    """Synthesise a new ZGR6RTRN terrain.bin (no official bytes are used).

    heights_2d: rows (+Y) x columns (+X) of world heights, size
        (ny*C+1) x (nx*C+1), C = 2**heightmap_level cells per node (4..1024).
        Sample (row r, column c) is at world (c*node_size/C, r*node_size/C).
    layer_weights: {layer_index: 2-D grid of 0..255} on the same grid.  Layer 0
        defaults to all 255 (the opaque base layer, as in every official file).
        Layers that are zero over a whole node are omitted from that node.
    physics: per-sample layer index used for the PHYM map: a 2-D grid (same for
        all four seasons) or a list of four 2-D grids (summer, fall, winter,
        spring).  Default: dominant_layers() of layer_weights, restricted to
        physics_layers when given (e.g. leave out flora-only layers; which layers
        the editor includes is not fully resolved, see the spec).
    min_height/max_height: quantisation range; MUST be written to scene.xscene
        <terrain min_height=".." max_height=".."> exactly (official files use 3
        decimals).  Default = data range snapped outward to multiples of 0.001.
    write_normals/write_weights: False writes an empty NRML/WGHT section (the
        layout used by both campaign Main_map files, which texture through a
        tileset); battle-style scenes in the install always carry both.

    Returns (terrain_bin_bytes, info); info carries the scene.xscene values that
    must accompany the file (see nodes_xml()).
    """
    nx, ny = int(node_dimension_x), int(node_dimension_y)
    H = len(heights_2d)
    W = len(heights_2d[0]) if H else 0
    if nx < 1 or ny < 1 or W < 2 or H < 2 or (W - 1) % nx or (H - 1) % ny:
        raise ValueError("grid %dx%d does not split into %dx%d nodes with shared edges" % (W, H, nx, ny))
    if any(len(row) != W for row in heights_2d):
        raise ValueError("ragged heights_2d")
    C = (W - 1) // nx
    if (H - 1) // ny != C:
        raise ValueError("nodes must be square (same cells per node in X and Y)")
    level = C.bit_length() - 1
    if C < 4 or (1 << level) != C:
        raise ValueError("cells per node must be a power of two >= 4 (got %d)" % C)
    R = C + 1
    flat = array.array("d", (float(v) for row in heights_2d for v in row))
    if any(v != v or v in (float("inf"), float("-inf")) for v in flat):
        raise ValueError("heights must be finite")
    # scene.xscene stores min/max with 3 decimals; default to a data range snapped
    # outward to 0.001 so the written attributes reproduce the quantisation exactly.
    lo = math.floor(min(flat) * 1000.0) / 1000.0 if min_height is None else float(min_height)
    hi = math.ceil(max(flat) * 1000.0) / 1000.0 if max_height is None else float(max_height)
    if hi <= lo:
        hi = lo + 1.0
    q = array.array("H", (_quantize(v, lo, hi) for v in flat))
    del flat
    scale = (hi - lo) / 65535.0
    spacing = float(node_size) / C
    normals = compute_normals(q, W, H, spacing, scale) if write_normals else None

    weights_flat: Dict[int, bytes] = {}
    if layer_weights:
        for k, grid in layer_weights.items():
            if not 0 <= int(k) < WEIGHT_SLOTS:
                raise ValueError("layer index %r out of range 0..15" % k)
            if len(grid) != H or any(len(row) != W for row in grid):
                raise ValueError("layer %d weight grid must be %dx%d" % (k, W, H))
            weights_flat[int(k)] = bytes(min(255, max(0, int(v))) for row in grid for v in row)
    if 0 not in weights_flat:
        weights_flat[0] = b"\xff" * (W * H)
    if physics is None:
        eligible = set(weights_flat) if physics_layers is None else (set(int(k) for k in physics_layers) | {0})
        dom = bytes(dominant_layers({k: v for k, v in weights_flat.items() if k in eligible}, W * H))
        phys_seasons = [dom] * SEASONS
    else:
        per_season = (len(physics) == SEASONS and len(physics[0]) > 0
                      and isinstance(physics[0][0], (list, tuple, bytes, bytearray)))
        grids = physics if per_season else [physics] * SEASONS
        phys_seasons = []
        for g in grids:
            if len(g) != H or any(len(row) != W for row in g):
                raise ValueError("physics grid must be %dx%d" % (W, H))
            phys_seasons.append(bytes(int(v) for row in g for v in row))

    hght, nrml, wght, phym = [], [], [], []
    nodes_info = []
    for iy in range(ny):
        for ix in range(nx):
            r0 = iy * C
            c0 = ix * C
            raw16 = bytearray()
            nrm = bytearray()
            nmin, nmax = 65535, 0
            gsum = 0
            for r in range(r0, r0 + R):
                seg = q[r * W + c0:r * W + c0 + R]
                nmin = min(nmin, min(seg))
                nmax = max(nmax, max(seg))
                raw16 += struct.pack(">%dH" % R, *seg)
                if normals is not None:
                    nrm += normals[4 * (r * W + c0):4 * (r * W + c0 + R)]
                if r + 1 < r0 + R:
                    nxt = q[(r + 1) * W + c0:(r + 1) * W + c0 + R]
                    gsum += sum(abs(seg[i + 1] - seg[i]) + abs(nxt[i] - seg[i]) for i in range(R - 1))
            hght.append(_lenpng(png_encode(R, R, 16, 0, bytes(raw16))))
            if normals is not None:
                nrml.append(_lenpng(png_encode(R, R, 8, 6, bytes(nrm), filter_type=0)))
            mask = 0
            slots: List[Optional[bytes]] = [None] * WEIGHT_SLOTS
            for k in sorted(weights_flat):
                wf = weights_flat[k]
                sub = b"".join(wf[r * W + c0:r * W + c0 + R] for r in range(r0, r0 + R))
                if k != 0 and not any(sub):
                    continue
                slots[k] = png_encode(R, R, 8, 0, sub)
                mask |= 1 << k
            if write_weights:
                wght.append(_write_weights(slots))
            # PHYM samples are x-major (index = column * R + row), unlike the PNG maps.
            seasons = []
            for s, pf in enumerate(phys_seasons):
                buf = bytearray(R * R)
                for r in range(R):
                    buf[r::R] = pf[(r0 + r) * W + c0:(r0 + r) * W + c0 + R]
                seasons.append((s, bytes(buf)))
            phym.append(_write_physics(seasons))
            nodes_info.append({
                "idx": ix, "idy": iy, "layer_mask": mask,
                "min_height": lo + nmin * scale, "max_height": lo + nmax * scale,
                # guessed metric (official meaning unknown): mean |slope| clamped to 1
                "height_complexity": min(1.0, gsum * scale / (2.0 * (R - 1) * (R - 1) * spacing)),
            })

    def section(tag: str, chunks: List[bytes]) -> Section:
        if not chunks:
            return Section(tag, 1, 0, 0, 0, [], None)
        offs = []
        p = 0
        for c in chunks:
            offs.append(p)
            p += len(c)
        blob = Blob.from_data(b"".join(chunks), compress=compress)
        return Section(tag, 1, 0, blob.csize, blob.usize, offs, blob)

    mw, mh = nx * MIDX_PER_NODE, ny * MIDX_PER_NODE
    midx_png = png_encode(mw, mh, 8, 0, bytes([midx_value]) * (mw * mh))
    midx_blob = Blob.from_data(_lenpng(midx_png) * MIDX_IMAGES, compress=compress)
    model = TerrainBin(version=6, rtrn_version=2, sections=[
        Section("MIDX", 1, 0, midx_blob.csize, midx_blob.usize, None, midx_blob),
        section("HGHT", hght), section("NRML", nrml), section("WGHT", wght), section("PHYM", phym),
    ])
    data = model.serialize()
    info = {
        "node_dimension_x": nx, "node_dimension_y": ny, "node_size": float(node_size),
        "min_height": lo, "max_height": hi, "heightmap_level": level, "resolution": R,
        "grid_width": W, "grid_height": H, "spacing": spacing, "nodes": nodes_info,
        "layers_used": sorted(weights_flat),
    }
    return data, info


# ---------------------------------------------------------------------------
# Inspection / CLI
# ---------------------------------------------------------------------------
def describe(model: TerrainBin, deep: bool = True) -> Dict[str, object]:
    d: Dict[str, object] = {"version": model.version}
    if model.version == 6:
        d["rtrn_version"] = model.rtrn_version
        d["sections"] = []
        for s in model.sections:
            e = {"tag": s.tag, "version": s.version, "offset": s.offset, "csize": s.csize, "usize": s.usize,
                 "nodes": None if s.node_offsets is None else len(s.node_offsets)}
            if s.blob:
                e["blob"] = {"codec": s.blob.codec, "pad": s.blob.pad, "reserved": s.blob.reserved, "stamp": "0x%x" % s.blob.stamp}
            d["sections"].append(e)
    else:
        d["blob"] = {"codec": model.blob.codec, "csize": model.blob.csize, "usize": model.blob.usize,
                     "reserved": model.blob.reserved, "stamp": "0x%x" % model.blob.stamp}
        d["midx_header"] = model.midx_header()
    d["node_count"] = model.node_count
    if deep:
        nx, ny = model.grid_shape()
        d["grid"] = [nx, ny]
        d["midx"] = [list(png_header(p)[:4]) for p in model.midx_images()]
        recs = model.nodes()
        res = sorted({png_header(r.height_png)[0] for r in recs if r.height_png})
        d["height_resolutions"] = res
        d["weight_slots_used"] = sorted({k for r in recs for k, w in enumerate(r.weights) if w is not None})
        d["has_normals"] = all(r.normal_png is not None for r in recs)
    return d


def _write_gray_png(path: str, W: int, H: int, values: Sequence[float], bits: int) -> None:
    lo = min(values)
    hi = max(values)
    span = (hi - lo) or 1.0
    maxv = (1 << bits) - 1
    # Flip rows so north (+Y) is at the top of the preview image.
    rows = []
    for r in range(H - 1, -1, -1):
        seg = values[r * W:(r + 1) * W]
        vals = [int(round((v - lo) / span * maxv)) for v in seg]
        rows.append(struct.pack(">%d%s" % (W, "H" if bits == 16 else "B"), *vals))
    with open(path, "wb") as fh:
        fh.write(png_encode(W, H, bits, 0, b"".join(rows), filter_type=2, level=6))


def _cmd_inspect(args) -> int:
    with open(args.file, "rb") as fh:
        data = fh.read()
    model = TerrainBin.parse(data)
    info = describe(model, deep=not args.shallow)
    if args.json:
        print(json.dumps(info, indent=2))
    else:
        for k, v in info.items():
            print("%s: %s" % (k, v))
    return 0


def _cmd_roundtrip(args) -> int:
    with open(args.file, "rb") as fh:
        data = fh.read()
    out = TerrainBin.parse(data).serialize()
    ok = out == data
    print("%s %s (%d bytes)" % ("IDENTICAL" if ok else "DIFFERENT", args.file, len(data)))
    return 0 if ok else 1


def _cmd_heights(args) -> int:
    with open(args.file, "rb") as fh:
        model = TerrainBin.parse(fh.read())
    scene = args.scene or os.path.join(os.path.dirname(os.path.abspath(args.file)), "scene.xscene")
    nx = ny = None
    lo, hi = 0.0, 65535.0
    if os.path.exists(scene):
        st = read_scene_terrain(scene)
        nx, ny = st["node_dimension_x"], st["node_dimension_y"]
        lo, hi = st["min_height"], st["max_height"]
    W, H, raw = decode_raw_heights(model, nx, ny)
    scale = (hi - lo) / 65535.0
    vals = [lo + v * scale for v in raw]
    print("grid %dx%d  height range %.3f .. %.3f (scene min/max %.3f/%.3f)" % (W, H, min(vals), max(vals), lo, hi))
    if args.png:
        _write_gray_png(args.png, W, H, vals, 16)
    if args.png8:
        _write_gray_png(args.png8, W, H, vals, 8)
    return 0


def _cmd_synth(args) -> int:
    n = args.size
    C = args.cells
    W = n * C + 1
    span = n * args.node_size
    heights = []
    for r in range(W):
        y = r / (W - 1) * span
        row = []
        for c in range(W):
            x = c / (W - 1) * span
            row.append(20.0 + 15.0 * math.sin(x / span * 2 * math.pi) * math.cos(y / span * 3 * math.pi)
                       + 5.0 * math.sin((x + 2 * y) / 37.0))
        heights.append(row)
    data, info = build_terrain(heights, n, n, args.node_size)
    with open(args.out, "wb") as fh:
        fh.write(data)
    print("wrote %s (%d bytes) grid %dx%d level %d min %.3f max %.3f" % (
        args.out, len(data), W, W, info["heightmap_level"], info["min_height"], info["max_height"]))
    if args.scene_out:
        with open(args.scene_out, "w", encoding="utf-8") as fh:
            fh.write('<terrain node_dimension_x="%d" node_dimension_y="%d" node_size="%.3f" min_height="%.3f" max_height="%.3f">\n'
                     % (n, n, args.node_size, info["min_height"], info["max_height"]))
            fh.write(nodes_xml(info) + "\n</terrain>\n")
    return 0


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("inspect")
    p.add_argument("file")
    p.add_argument("--json", action="store_true")
    p.add_argument("--shallow", action="store_true", help="container only, no PNG decoding")
    p.set_defaults(fn=_cmd_inspect)
    p = sub.add_parser("roundtrip")
    p.add_argument("file")
    p.set_defaults(fn=_cmd_roundtrip)
    p = sub.add_parser("heights")
    p.add_argument("file")
    p.add_argument("--scene")
    p.add_argument("--png")
    p.add_argument("--png8")
    p.set_defaults(fn=_cmd_heights)
    p = sub.add_parser("synth")
    p.add_argument("--size", type=int, required=True, help="nodes per side")
    p.add_argument("--cells", type=int, default=64, help="cells per node (power of two)")
    p.add_argument("--node-size", type=float, default=100.0)
    p.add_argument("--out", required=True)
    p.add_argument("--scene-out")
    p.set_defaults(fn=_cmd_synth)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
