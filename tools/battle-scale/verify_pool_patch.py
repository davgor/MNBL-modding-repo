"""Verify the real review manifest entirely in memory; never emit or load a DLL."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile

from patch_native_copy import PatchError, load_manifest, run, transform, safe_review_output

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "evidence/engine-extension"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify(manifest_path, receipt_path):
    manifest, manifest_hash = load_manifest(manifest_path)
    source_path = Path(manifest["sourcePath"])
    receipt_path = safe_review_output(receipt_path, [source_path, manifest_path] +
                                     [r["path"] for r in manifest["sourceEvidence"].values()])
    before = source_path.read_bytes()
    report = run("verify", source_path, manifest_path)
    for name, reference in manifest["sourceEvidence"].items():
        if sha(Path(reference["path"]).read_bytes()) != reference["sha256"]:
            raise ValueError("Evidence changed after manifest generation: " + name)
    changed = transform(before, manifest)
    restored = transform(changed, manifest, reverse=True)
    if restored != before:
        raise ValueError("Full-file reverse transformation did not recover source")
    # Exercise the actual application entry point, including its manifest gate.
    # This branch is only run for blocked artifacts; no patched DLL may be written.
    refused = None
    if manifest["status"] == "blocked" or manifest["blockers"]:
        with tempfile.TemporaryDirectory(prefix="mnbl-patch-gate-") as directory:
            output = Path(directory) / "must-not-exist.dll"
            try:
                run("apply", source_path, manifest_path, output=output)
            except PatchError as error:
                refused = str(error)
                if "manifest is blocked" not in refused:
                    raise
            else:
                raise ValueError("Blocked manifest was accepted for application")
            if list(Path(directory).iterdir()):
                raise ValueError("Blocked application left output files")
    after = source_path.read_bytes()
    if after != before:
        raise ValueError("Installed source changed during verification")
    static_ready = (manifest["status"] == "experimental-static-candidate" and not manifest["blockers"]
                    and manifest.get("coverage", {}).get("semanticCoverageComplete") is True
                    and manifest.get("readiness", {}).get("staticReady") is True)
    report.update({"manifestSha256": manifest_hash,
                   "sourceEvidenceHashesVerified": len(manifest["sourceEvidence"]),
                   "inMemoryForwardAndReversePassed": True,
                   "actualChangedByteCount": sum(a != b for a, b in zip(before, changed)),
                   "blockedApplicationRefusal": refused,
                   "installedSourceUnchanged": True,
                   "patchedDllWritten": False,
                   "gameLaunched": False,
                   "testReady": static_ready,
                   "runtimeVerified": False,
                   "launchHold": True,
                   "note": ("Byte transformation verified for the independently reviewed restricted static candidate. Runtime safety, rendering and performance remain unverified; launch hold is active."
                            if static_ready else "Byte correctness only. Static readiness review remains blocked.")})
    receipt_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=EVIDENCE / "native-pool-patch-4080.json")
    parser.add_argument("--receipt", type=Path, default=EVIDENCE / "native-pool-patch-static-verification.json")
    args = parser.parse_args()
    verify(args.manifest, args.receipt)
