"""MMR-259: read texture records from installed Bannerlord .tpac packs and decode them to PNG (Pillow only).

A pack starts with 'TPAC', the record count at 0x18 and the header length at 0x1C; the header (0x24 + length bytes) holds every
record, the data blobs follow it. A texture record reads:
  u32 len + name, ..., u32 len + source path ($BASE/Modules/<module>/AssetSources/.../<file>), u64 stamp, flag strings
  (for_terrain, is_normalmap, ...), u32 width, u32 height, u32 1, u8 mips, u16, u32 len + format (DXT1, BC5, ...),
  more flag strings (has_alpha, ...), u32 len + 'none', then a chunk table of (u64 offset, u64 size, u64 stored size) entries.
Two things the earlier scratch decoder got wrong (findings section 6):
  - a chunk whose stored size equals its size is stored raw, not LZ4;
  - the large packs (core.tpac, core_game.tpac: snow, ice, stone_desert) carry two chunks per texture, a raw low-resolution
    streaming chain first and the full chain second, and their headers run to megabytes. The full chain is found by its exact
    byte size, wherever it sits in the table, and the packs are read by seeking, never loaded whole.
Usage:
  python tools/art/tpac_textures.py list <pack.tpac> [name-substring]
  python tools/art/tpac_textures.py find <Modules dir> name[,name...]        (prints the pack that holds each name)
  python tools/art/tpac_textures.py extract <Modules dir> <out dir> name[,name...]   (top mip to <out>/<name>.png)
"""
import io
import os
import re
import struct
import sys

from PIL import Image

BLOCK = {'DXT1': 8, 'BC1': 8, 'BC4': 8, 'DXT3': 16, 'DXT5': 16, 'BC3': 16, 'BC5': 16, 'BC6H': 16, 'BC7': 16}
DXGI = {'DXT1': 71, 'BC1': 71, 'DXT3': 74, 'DXT5': 77, 'BC3': 77, 'BC4': 80, 'BC5': 83, 'BC6H': 95, 'BC7': 98}
FLAGS = (b'for_terrain', b'has_alpha', b'is_normalmap', b'is_specularmap', b'is_heightmap', b'srgb', b'dont_resize_in_atlas',
         b'is_decal', b'do_not_compress')
SOURCE = re.compile(rb'\$BASE/Modules/[^\x00-\x1f]{1,240}?\.(psd|tif|tga|png|dds|jpg|bmp)', re.I)
MODULES = ('Native', 'SandBox', 'SandBoxCore', 'NavalDLC')


def chain(w, h, mips, bs):
    tot = 0
    for m in range(mips):
        tot += max(1, (max(1, w >> m) + 3) // 4) * max(1, (max(1, h >> m) + 3) // 4) * bs
    return tot


def lz4_block(src, usize):
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        t = src[i]
        i += 1
        ln = t >> 4
        if ln == 15:
            while True:
                b = src[i]
                i += 1
                ln += b
                if b != 255:
                    break
        dst += src[i:i + ln]
        i += ln
        if i >= n:
            break
        off = src[i] | src[i + 1] << 8
        i += 2
        m = (t & 15) + 4
        if (t & 15) == 15:
            while True:
                b = src[i]
                i += 1
                m += b
                if b != 255:
                    break
        s = len(dst) - off
        if off == 0 or s < 0:
            raise ValueError('lz4: bad offset')
        if off >= m:
            dst += dst[s:s + m]
        else:
            for k in range(m):
                dst.append(dst[s + k])
        if len(dst) > usize:
            raise ValueError('lz4: overrun')
    if len(dst) != usize:
        raise ValueError('lz4: %d bytes, expected %d' % (len(dst), usize))
    return bytes(dst)


def header(path):
    with open(path, 'rb') as f:
        h = f.read(0x24)
        if h[:4] != b'TPAC':
            return None, 0, 0
        hl = struct.unpack_from('<I', h, 0x1C)[0]
        f.seek(0)
        return f.read(0x24 + hl), struct.unpack_from('<I', h, 0x18)[0], os.path.getsize(path)


def records(path):
    """Every texture record of a pack: name, source, flags, size, mips, format and the full-chain chunk (offset, size, stored)."""
    d, _, fsize = header(path)
    if d is None:
        return []
    out = []
    sources = list(SOURCE.finditer(d))
    for i, m in enumerate(sources):
        end = sources[i + 1].start() if i + 1 < len(sources) else len(d)
        seg_end = min(end, m.end() + 900)
        rec = None
        for fmt in BLOCK:
            pat = struct.pack('<I', len(fmt)) + fmt.encode()
            k = d.find(pat, m.end(), seg_end)
            if k < 0 or k < m.end() + 15:
                continue
            w, h, one = struct.unpack_from('<III', d, k - 15)
            mips = d[k - 3]
            if one != 1 or not (1 <= w <= 16384 and 1 <= h <= 16384) or not 1 <= mips <= 15:
                continue
            rec = (k, fmt, w, h, mips)
            break
        if rec is None:
            continue
        k, fmt, w, h, mips = rec
        # the record name: the length-prefixed string that ends shortly before the source path's length field
        name = None
        lo = max(0, m.start() - 4 - 96)
        for j in range(m.start() - 4 - 1, lo, -1):
            ln = struct.unpack_from('<I', d, j)[0] if j + 4 <= len(d) else 0
            if 3 <= ln <= 80 and j + 4 + ln <= m.start() - 4:
                s = d[j + 4:j + 4 + ln]
                if re.fullmatch(rb'[A-Za-z0-9_\-.]+', s) and (m.start() - 4) - (j + 4 + ln) <= 48:
                    name = s.decode()
                    break
        src = m.group(0).decode(errors='replace')
        if name is None:
            name = os.path.splitext(os.path.basename(src))[0]
        none = d.find(struct.pack('<I', 4) + b'none', k, seg_end + 200)
        flags = [f.decode() for f in FLAGS if f in d[m.end():none if none > 0 else k]]
        full = chain(w, h, mips, BLOCK[fmt])
        chunk = None
        stop = min(end, k + 400)
        pat = struct.pack('<Q', full)
        j = d.find(pat, k, stop)
        while j >= 0:
            off = struct.unpack_from('<Q', d, j - 8)[0]
            stored = struct.unpack_from('<Q', d, j + 8)[0]
            if len(d) <= off < fsize and 0 < stored <= full and off + stored <= fsize:
                chunk = (off, full, stored)
                break
            j = d.find(pat, j + 1, stop)
        out.append({'name': name, 'source': src, 'flags': flags, 'width': w, 'height': h, 'mips': mips, 'format': fmt,
                    'chain': full, 'chunk': chunk, 'pack': path})
    return out


def dds(fmt, w, h, payload):
    hdr = bytearray(128)
    struct.pack_into('<4sIIIIIII', hdr, 0, b'DDS ', 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000, h, w, len(payload), 0, 1)
    struct.pack_into('<II4s', hdr, 76, 32, 0x4, b'DX10')
    struct.pack_into('<I', hdr, 108, 0x1000)
    return bytes(hdr) + struct.pack('<IIIII', DXGI[fmt], 3, 0, 1, 0) + payload


def decode(rec):
    """The top mip of a record as a Pillow image (RGBA for alpha formats, RGB otherwise)."""
    off, usize, stored = rec['chunk']
    with open(rec['pack'], 'rb') as f:
        f.seek(off)
        blob = f.read(stored)
    raw = blob if stored == usize else lz4_block(blob, usize)
    w, h, fmt = rec['width'], rec['height'], rec['format']
    top = raw[:max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * BLOCK[fmt]]
    im = Image.open(io.BytesIO(dds(fmt, w, h, top)))
    im.load()
    return im


def packs(modules):
    for mod in MODULES:
        for sub in ('EmAssetPackages', 'AssetPackages'):
            base = os.path.join(modules, mod, sub)
            for dp, _, fn in os.walk(base):
                for f in sorted(fn):
                    if f.endswith('.tpac'):
                        yield os.path.join(dp, f)


def find(modules, names):
    """First decodable record per name (per-texture editor packs first, then the published packs)."""
    want, found = set(names), {}
    for p in packs(modules):
        d, _, _ = header(p)
        if d is None or not any(n.encode() in d for n in want - set(found)):
            continue
        for r in records(p):
            if r['name'] in want and r['name'] not in found and r['chunk']:
                found[r['name']] = r
        if len(found) == len(want):
            break
    return found


def main(argv):
    if len(argv) >= 2 and argv[0] == 'list':
        for r in records(argv[1]):
            if len(argv) < 3 or argv[2] in r['name']:
                print('%-40s %5dx%-5d %-4s mips=%-2d %-34s chunk=%s' % (r['name'], r['width'], r['height'], r['format'], r['mips'],
                                                                        ','.join(r['flags']), r['chunk']))
        return 0
    if len(argv) == 3 and argv[0] == 'find':
        names = argv[2].split(',')
        found = find(argv[1], names)
        for n in names:
            r = found.get(n)
            print(n, '->', '%s %dx%d %s %s' % (os.path.relpath(r['pack'], argv[1]), r['width'], r['height'], r['format'], r['flags']) if r else 'NOT FOUND')
        return 0 if len(found) == len(names) else 1
    if len(argv) == 4 and argv[0] == 'extract':
        names = argv[3].split(',')
        found = find(argv[1], names)
        os.makedirs(argv[2], exist_ok=True)
        for n in names:
            r = found.get(n)
            if not r:
                print(n, 'NOT FOUND')
                continue
            im = decode(r)
            im.save(os.path.join(argv[2], n + '.png'))
            print(n, im.size, im.mode, r['format'], 'raw' if r['chunk'][1] == r['chunk'][2] else 'lz4', os.path.relpath(r['pack'], argv[1]))
        return 0 if len(found) == len(names) else 1
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
