"""Static PE import/export boundaries in one explicitly selected game bin directory.

Never imports or executes game code. pefile is loaded from a caller-owned tools dir.
"""
import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path


def decode(value):
    return value.decode("utf-8", "replace") if isinstance(value, bytes) else value


def inspect(path, pefile):
    raw = path.read_bytes()
    pe = pefile.PE(data=raw, fast_load=True)
    directories = pefile.DIRECTORY_ENTRY
    pe.parse_data_directories(directories=[directories[name] for name in (
        "IMAGE_DIRECTORY_ENTRY_IMPORT", "IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT",
        "IMAGE_DIRECTORY_ENTRY_EXPORT", "IMAGE_DIRECTORY_ENTRY_DEBUG",
        "IMAGE_DIRECTORY_ENTRY_EXCEPTION")])
    imports = []
    for kind, attr in (("normal", "DIRECTORY_ENTRY_IMPORT"), ("delay", "DIRECTORY_ENTRY_DELAY_IMPORT")):
        for module in getattr(pe, attr, []):
            imports.append({"kind": kind, "dll": decode(module.dll), "symbols": [
                {"name": decode(s.name), "ordinal": s.ordinal if s.import_by_ordinal else None,
                 "iatVa": hex(s.address)} for s in module.imports]})
    exports = []
    if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
        for s in pe.DIRECTORY_ENTRY_EXPORT.symbols:
            exports.append({"name": decode(s.name), "ordinal": s.ordinal, "rva": hex(s.address),
                            "forwarder": decode(s.forwarder)})
    debug = []
    for entry in getattr(pe, "DIRECTORY_ENTRY_DEBUG", []):
        rec = {"type": entry.struct.Type, "rawOffset": hex(entry.struct.PointerToRawData), "size": entry.struct.SizeOfData}
        blob = raw[entry.struct.PointerToRawData:entry.struct.PointerToRawData + entry.struct.SizeOfData]
        if blob.startswith(b"RSDS") and len(blob) >= 24:
            import uuid
            rec.update(signature="RSDS", guid=str(uuid.UUID(bytes_le=blob[4:20])), age=int.from_bytes(blob[20:24], "little"), pdbPath=decode(blob[24:].split(b"\0", 1)[0]))
        debug.append(rec)
    cli = pe.OPTIONAL_HEADER.DATA_DIRECTORY[directories["IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR"]]
    # CLR flags at COR20 header +16 identify IL-only versus mixed-mode classification.
    cli_flags = int.from_bytes(pe.get_data(cli.VirtualAddress + 16, 4), "little") if cli.VirtualAddress else None
    sections = [{"name": decode(s.Name.rstrip(b"\0")), "rva": hex(s.VirtualAddress),
                 "virtualSize": s.Misc_VirtualSize, "rawOffset": s.PointerToRawData, "rawSize": s.SizeOfRawData}
                for s in pe.sections]
    exceptions = getattr(pe, "DIRECTORY_ENTRY_EXCEPTION", [])
    known = [0x5ce3f0, 0x5cef70, 0x5e5b30, 0x5d8220, 0x5d8020, 0x5cd130, 0x7084a0, 0x70b150, 0x6f7460]
    bounds = []
    if path.name.lower() == "taleworlds.native.dll":
        for rva in known:
            matches = [e for e in exceptions if e.struct.BeginAddress <= rva < e.struct.EndAddress]
            bounds.append({"requestedRva": hex(rva), "runtimeFunctions": [
                {"beginRva": hex(e.struct.BeginAddress), "endRvaExclusive": hex(e.struct.EndAddress), "unwindInfoRva": hex(e.struct.UnwindData)} for e in matches]})
    record = {"name": path.name, "path": str(path), "sha256": hashlib.sha256(raw).hexdigest(),
              "size": len(raw), "machine": hex(pe.FILE_HEADER.Machine), "imageBase": hex(pe.OPTIONAL_HEADER.ImageBase),
              "entrypointRva": hex(pe.OPTIONAL_HEADER.AddressOfEntryPoint), "clrDirectoryPresent": bool(cli.VirtualAddress),
              "clrFlags": cli_flags, "ilOnlyFlag": bool(cli_flags & 1) if cli_flags is not None else False,
              "imports": imports, "exports": exports, "debug": debug, "sections": sections,
              "exceptionDirectoryEntries": len(exceptions), "knownPoolProcedureBounds": bounds}
    pe.close()
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-bin", default="D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client")
    parser.add_argument("--python-libs", default="build/battle-scale/python-libs")
    parser.add_argument("--output", default="evidence/engine-extension/native-pool-module-boundaries.json")
    args = parser.parse_args()
    sys.path.insert(0, str(Path(args.python_libs).resolve()))
    import pefile
    root = Path(args.game_bin).resolve()
    inventory, skipped = [], []
    for path in sorted(root.iterdir()):
        if not path.is_file() or path.suffix.lower() not in (".dll", ".exe"):
            continue
        try:
            inventory.append(inspect(path, pefile))
        except Exception as exc:
            skipped.append({"path": str(path), "error": str(exc)})
    native = [m for m in inventory if not m["ilOnlyFlag"]]
    names = {m["name"].lower() for m in inventory}
    edges = [{"importer": m["name"], "importedDll": imp["dll"], "kind": imp["kind"], "symbols": imp["symbols"]}
             for m in inventory for imp in m["imports"] if imp["dll"].lower() in names]
    term = re.compile(r"agent|mission", re.I)
    symbol_hits = []
    for m in inventory:
        for export in m["exports"]:
            if term.search(export["name"] or ""):
                symbol_hits.append({"module": m["name"], "direction": "export", **export})
        for imp in m["imports"]:
            for symbol in imp["symbols"]:
                if term.search(symbol["name"] or ""):
                    symbol_hits.append({"module": m["name"], "direction": "import", "dll": imp["dll"], **symbol})
    # Retain full native import/export metadata. Managed imports remain in internalModuleEdges.
    result = {"schemaVersion": 1, "inspectedUtc": datetime.now(timezone.utc).isoformat(), "scope": str(root),
              "recursion": False, "method": "Execution-free pefile import/export/delay-import/debug/exception metadata parsing", "pefileVersion": pefile.__version__,
              "filesInspected": len(inventory), "nativeOrMixedModeCount": len(native), "ilOnlyCount": len(inventory)-len(native),
              "skipped": skipped, "nativeModules": native, "managedIdentities": [{k: m[k] for k in ("name", "path", "sha256", "clrFlags")} for m in inventory if m["ilOnlyFlag"]],
              "internalModuleEdges": edges, "missionAgentImportExportHits": symbol_hits,
              "findings": [], "unknowns": [
                  "Import/export names cannot prove absence of shared C++ layout. Opaque pointers, dynamic GetProcAddress lookups, callbacks, registration tables and inline code can cross module boundaries without Mission/Agent names.",
                  "Modules outside this one selected bin directory, dynamically downloaded or modded modules, and runtime load paths are excluded.",
                  "PE exception .pdata bounds identify compiled code regions, not types, ownership, semantic method boundaries or all leaf functions.",
                  "PDB paths are debug-directory metadata only; no PDB was loaded or fetched.",
                  "No target execution, live module enumeration or patch safety certification occurred."],
              "validation": {"targetExecuted": False, "filesModified": False, "parsedMetadataOnly": True}}
    out = Path(args.output)
    out.write_text(json.dumps(result, indent=2), encoding="utf-8")
    reread = json.loads(out.read_text(encoding="utf-8"))
    assert reread["filesInspected"] == len(inventory)
    assert all(len(m["sha256"]) == 64 for m in native)
    print(json.dumps({"output": str(out.resolve()), "inspected": len(inventory), "native": len(native), "managed": len(inventory)-len(native), "skipped": len(skipped), "namedHits": len(symbol_hits), "edges": len(edges)}))


if __name__ == "__main__":
    main()
