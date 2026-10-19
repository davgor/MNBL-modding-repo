"""Offline, version-bound PE patch copies. Never loads or executes the target.

This validates byte transformations, not engine correctness. A candidate is still
unvalidated even when every static check passes. Blocked manifests cannot apply.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys

DEFAULT_GAME_ROOT = Path(r"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord")


class PatchError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise PatchError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def integer(value, name, maximum=0xFFFFFFFFFFFFFFFF):
    require(type(value) is int and 0 <= value <= maximum, name + " must be a nonnegative integer in range")
    return value


def hex_number(value, name, maximum=0xFFFFFFFFFFFFFFFF):
    require(isinstance(value, str) and re.fullmatch(r"0x[0-9a-fA-F]+", value), name + " must be a 0x hexadecimal string")
    result = int(value, 16)
    require(result <= maximum, name + " out of range")
    return result


def hex_bytes(value, name):
    require(isinstance(value, str) and re.fullmatch(r"(?:[0-9a-fA-F]{2})+", value), name + " must contain nonempty contiguous hexadecimal byte pairs")
    return bytes.fromhex(value)


def nonempty_text(value, name):
    require(isinstance(value, str) and value.strip(), name + " must be nonempty text")


def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON key: " + key)
        result[key] = value
    return result


def load_manifest(path):
    raw = Path(path).read_bytes()
    try:
        manifest = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=no_duplicate_keys)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise PatchError("invalid manifest JSON: " + str(exc)) from exc
    validate_manifest(manifest)
    return manifest, digest(raw)


def validate_manifest(m):
    require(isinstance(m, dict), "manifest must be an object")
    required = {"schemaVersion", "sourceSha256", "sourceSize", "imageBase", "targetCapacity", "status", "blockers", "patches"}
    require(required <= m.keys(), "manifest missing fields: " + str(sorted(required - m.keys())))
    require(type(m["schemaVersion"]) is int and m["schemaVersion"] == 1, "unsupported schemaVersion")
    require(isinstance(m["sourceSha256"], str) and re.fullmatch(r"[0-9a-f]{64}", m["sourceSha256"]), "sourceSha256 must be lowercase SHA-256")
    require(integer(m["sourceSize"], "sourceSize") > 0, "sourceSize must be positive")
    hex_number(m["imageBase"], "imageBase")
    require(type(m["targetCapacity"]) is int and m["targetCapacity"] == 4080, "targetCapacity must be 4080")
    require(m["status"] in ("blocked", "experimental-static-candidate"), "unknown manifest status")
    require(isinstance(m["blockers"], list), "blockers must be an array")
    for blocker in m["blockers"]:
        nonempty_text(blocker, "blocker")
    require(isinstance(m["patches"], list), "patches must be an array")
    require(m["patches"] or m["status"] == "blocked", "candidate must contain patches")
    ids = set()
    spans = []
    for patch in m["patches"]:
        require(isinstance(patch, dict), "patch must be an object")
        fields = {"id", "fileOffset", "rva", "original", "replacement", "reason", "evidenceIds"}
        require(fields <= patch.keys(), "patch missing required fields")
        nonempty_text(patch["id"], "patch id")
        require(patch["id"] not in ids, "duplicate patch id")
        ids.add(patch["id"])
        start = integer(patch["fileOffset"], "fileOffset")
        hex_number(patch["rva"], "rva", 0xFFFFFFFF)
        old = hex_bytes(patch["original"], "original")
        new = hex_bytes(patch["replacement"], "replacement")
        require(len(old) == len(new), "patch lengths must match")
        require(old != new, "patch must change at least one byte")
        require(start + len(old) <= m["sourceSize"], "patch exceeds sourceSize")
        nonempty_text(patch["reason"], "reason")
        require(isinstance(patch["evidenceIds"], list) and patch["evidenceIds"], "evidenceIds must be nonempty")
        for evidence in patch["evidenceIds"]:
            nonempty_text(evidence, "evidence id")
        spans.append((start, start + len(old)))
    spans.sort()
    require(all(a[1] <= b[0] for a, b in zip(spans, spans[1:])), "overlapping patches")


def parse_pe(data):
    """Validate enough PE geometry to map every edit unambiguously to code."""
    def unpack(fmt, offset):
        require(offset >= 0 and offset + struct.calcsize(fmt) <= len(data), "truncated PE header")
        return struct.unpack_from(fmt, data, offset)[0]

    require(data[:2] == b"MZ", "missing DOS signature")
    pe = unpack("<I", 0x3C)
    require(pe >= 0x40 and data[pe:pe + 4] == b"PE\0\0", "invalid PE signature")
    require(unpack("<H", pe + 4) == 0x8664, "target must be x64")
    count = unpack("<H", pe + 6)
    require(1 <= count <= 96, "invalid section count")
    optional_size = unpack("<H", pe + 20)
    optional = pe + 24
    require(optional_size >= 112 and optional + optional_size <= len(data), "truncated PE optional header")
    require(unpack("<H", optional) == 0x20B, "target must be PE32+")
    image_base = unpack("<Q", optional + 24)
    image_size = unpack("<I", optional + 56)
    headers_size = unpack("<I", optional + 60)
    table = optional + optional_size
    require(table + count * 40 <= headers_size <= len(data), "invalid PE header or section table size")
    directories = unpack("<I", optional + 108)
    require(directories <= 16 and 112 + directories * 8 <= optional_size, "invalid PE data directories")
    reloc = (0, 0)
    if directories > 5:
        reloc_rva = unpack("<I", optional + 112 + 5 * 8)
        reloc_size = unpack("<I", optional + 116 + 5 * 8)
        require(bool(reloc_rva) == bool(reloc_size), "invalid relocation directory")
        require(reloc_rva + reloc_size <= image_size, "relocation directory outside image")
        reloc = (reloc_rva, reloc_rva + reloc_size)
    sections = []
    for index in range(count):
        entry = table + index * 40
        name = data[entry:entry + 8].split(b"\0", 1)[0]
        virtual_size = unpack("<I", entry + 8)
        rva = unpack("<I", entry + 12)
        size = unpack("<I", entry + 16)
        offset = unpack("<I", entry + 20)
        flags = unpack("<I", entry + 36)
        require(rva >= headers_size and rva + max(size, virtual_size) <= image_size, "section outside virtual image")
        require(not size or headers_size <= offset and offset + size <= len(data), "section outside raw image")
        sections.append({"name": name, "offset": offset, "size": size, "rva": rva, "virtualSize": max(size, virtual_size), "flags": flags})
    for i, a in enumerate(sections):
        for b in sections[i + 1:]:
            require(not (a["size"] and b["size"] and a["offset"] < b["offset"] + b["size"] and b["offset"] < a["offset"] + a["size"]), "overlapping raw sections")
            require(not (a["virtualSize"] and b["virtualSize"] and a["rva"] < b["rva"] + b["virtualSize"] and b["rva"] < a["rva"] + a["virtualSize"]), "overlapping virtual sections")
    return image_base, sections, reloc


def validate_original(data, manifest):
    validate_manifest(manifest)
    require(len(data) == manifest["sourceSize"], "source size mismatch")
    require(digest(data) == manifest["sourceSha256"], "source SHA-256 mismatch")
    base, sections, reloc = parse_pe(data)
    require(base == hex_number(manifest["imageBase"], "imageBase"), "PE image base mismatch")
    for patch in manifest["patches"]:
        start = patch["fileOffset"]
        old = bytes.fromhex(patch["original"])
        end = start + len(old)
        matches = [s for s in sections if s["size"] and s["offset"] <= start and end <= s["offset"] + s["size"]]
        require(len(matches) == 1, "patch must be fully inside one raw section: " + patch["id"])
        section = matches[0]
        require(section["flags"] & 0x20000000, "patch section is not executable: " + patch["id"])
        require(section["name"] != b".reloc", "patch cannot modify relocation section")
        rva = section["rva"] + start - section["offset"]
        require(rva == hex_number(patch["rva"], "rva"), "fileOffset/RVA mismatch: " + patch["id"])
        require(not (rva < reloc[1] and reloc[0] < rva + len(old)), "patch overlaps base relocation directory")
        require(data[start:end] == old, "original bytes mismatch: " + patch["id"])


def verify_transform(before, after, patches):
    require(len(before) == len(after), "transformation changed file length")
    expected = bytearray(before)
    for patch in patches:
        start = patch["fileOffset"]
        old, new = bytes.fromhex(patch["original"]), bytes.fromhex(patch["replacement"])
        require(before[start:start + len(old)] == old, "transformation source bytes mismatch")
        expected[start:start + len(new)] = new
    require(after == expected, "transformation differs outside declared replacements or has missing edits")


def transform(data, manifest, reverse=False):
    validate_manifest(manifest)
    require(len(data) == manifest["sourceSize"], "source size mismatch")
    if reverse:
        original = bytearray(data)
        for patch in manifest["patches"]:
            start = patch["fileOffset"]
            new, old = bytes.fromhex(patch["replacement"]), bytes.fromhex(patch["original"])
            require(data[start:start + len(new)] == new, "patched bytes mismatch: " + patch["id"])
            original[start:start + len(old)] = old
        original = bytes(original)
        validate_original(original, manifest)
        verify_transform(original, data, manifest["patches"])
        return original
    validate_original(data, manifest)
    result = bytearray(data)
    for patch in manifest["patches"]:
        start = patch["fileOffset"]
        new = bytes.fromhex(patch["replacement"])
        result[start:start + len(new)] = new
    verify_transform(data, result, manifest["patches"])
    return bytes(result)


def within(path, root):
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def canonical_path(path):
    resolved = str(Path(path).resolve())
    # Windows final paths can carry a namespace prefix while referring to the
    # same local file. Normalize that spelling before installation comparisons.
    if os.name == "nt" and resolved.startswith("\\\\?\\"):
        resolved = "\\\\" + resolved[8:] if resolved.startswith("\\\\?\\UNC\\") else resolved[4:]
    return Path(resolved)


def safe_output(path, source, game_roots):
    path = Path(path).absolute()
    if os.name == "nt":
        require(not path.drive.startswith("\\\\"), "device and network output paths are forbidden")
    # Reject symlinks/junctions in the output ancestry, including dangling ones.
    for ancestor in (path, *path.parents):
        require(not ancestor.is_symlink() and not (hasattr(ancestor, "is_junction") and ancestor.is_junction()), "output must not traverse a symlink or junction")
    resolved = canonical_path(path)
    require(resolved != canonical_path(source), "refusing to write source or its alias")
    require(not path.exists(), "output already exists")
    require(path.parent.is_dir(), "output parent directory must already exist")
    if os.name == "nt":
        require(":" not in str(path)[len(path.drive):], "alternate data streams are forbidden")
    for root in game_roots:
        require(not within(resolved, canonical_path(root)), "refusing to write inside game installation")
    return path


def safe_review_output(path, protected_paths, game_roots=None):
    """Permit regenerating an ordinary JSON review, never a binary or input."""
    path = Path(path).absolute()
    require(path.suffix.lower() == ".json", "review output must be JSON")
    if os.name == "nt":
        require(not path.drive.startswith("\\\\"), "device and network output paths are forbidden")
        require(":" not in str(path)[len(path.drive):], "alternate data streams are forbidden")
    for ancestor in (path, *path.parents):
        require(not ancestor.is_symlink() and not (hasattr(ancestor, "is_junction") and ancestor.is_junction()), "review output must not traverse a symlink or junction")
    resolved = canonical_path(path)
    for protected in protected_paths:
        require(resolved != canonical_path(protected), "refusing to overwrite review input")
        if path.exists() and Path(protected).exists():
            require(not path.samefile(protected), "refusing to overwrite input alias")
    for root in [DEFAULT_GAME_ROOT] + list(game_roots or []):
        require(not within(resolved, canonical_path(root)), "refusing to write inside game installation")
    require(path.parent.is_dir(), "review output parent directory must exist")
    if path.exists():
        require(path.is_file() and path.stat().st_nlink == 1, "review output must be an ordinary unlinked file")
    return path


def run(operation, source, manifest_path, output=None, receipt=None, game_root=None):
    manifest, manifest_hash = load_manifest(manifest_path)
    source = Path(source)
    data = source.read_bytes()
    reverse = operation == "revert"
    require(operation in ("verify", "apply", "revert"), "unknown operation")
    transformed = transform(data, manifest, reverse=reverse)
    original, patched = (transformed, data) if reverse else (data, transformed)
    eligible = manifest["status"] != "blocked" and not manifest["blockers"]
    report = {
        "schemaVersion": 1, "operation": operation,
        "manifestSha256": manifest_hash, "originalSha256": digest(original),
        "patchedSha256": digest(patched), "sourceSize": len(data),
        "patchCount": len(manifest["patches"]), "targetCapacity": manifest["targetCapacity"],
        "status": manifest["status"], "applyEligible": eligible,
        "runtimeValidated": False, "targetExecuted": False,
        "warning": "Static byte validation only; engine safety and runtime behavior are unvalidated.",
        "blockers": manifest["blockers"],
    }
    if operation == "verify":
        require(output is None and receipt is None, "verify does not write output or receipt")
        return report
    if operation == "apply":
        require(eligible, "apply refused: manifest is blocked or has unresolved blockers")
    require(output is not None, "output is required")
    roots = [DEFAULT_GAME_ROOT] + ([Path(game_root)] if game_root else [])
    output = safe_output(output, source, roots)
    receipt = safe_output(receipt or str(output) + ".receipt.json", source, roots)
    require(output != receipt, "output and receipt paths must differ")
    require(output.resolve() != Path(manifest_path).resolve() and receipt.resolve() != Path(manifest_path).resolve(), "refusing to overwrite manifest")
    report.update(sourcePath=str(source.resolve()), outputPath=str(output), outputSha256=digest(transformed))
    created = []
    try:
        # Exclusive creation prevents clobbering any file created after preflight.
        with output.open("xb") as handle:
            created.append(output)
            handle.write(transformed)
            handle.flush()
            os.fsync(handle.fileno())
        written = output.read_bytes()
        require(written == transformed, "written file verification failed")
        if reverse:
            require(digest(written) == manifest["sourceSha256"], "restored source hash mismatch")
            verify_transform(written, data, manifest["patches"])
        else:
            verify_transform(data, written, manifest["patches"])
        with receipt.open("x", encoding="utf-8", newline="\n") as handle:
            created.append(receipt)
            json.dump(report, handle, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
    except BaseException as failure:
        cleanup_errors = []
        for created_path in reversed(created):
            try:
                created_path.unlink(missing_ok=True)
            except OSError as cleanup_error:
                cleanup_errors.append(str(created_path) + ": " + str(cleanup_error))
        if cleanup_errors:
            raise PatchError("operation failed: " + str(failure) + "; partial output cleanup failed: " + "; ".join(cleanup_errors)) from failure
        raise
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("verify", "apply", "revert"))
    parser.add_argument("--source", required=True, help="original file for verify/apply; patched copy for revert")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", help="new offline copy; must not exist or be inside the game installation")
    parser.add_argument("--receipt", help="new JSON receipt path; default OUTPUT.receipt.json")
    parser.add_argument("--game-root", help="additional protected installation directory; default installation remains protected")
    args = parser.parse_args(argv)
    try:
        report = run(args.operation, args.source, args.manifest, args.output, args.receipt, args.game_root)
    except (PatchError, OSError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
