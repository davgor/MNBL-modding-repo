"""Prepare an offline native4080 test package; never install or launch the game.

Refuses a blocked manifest and stale source/module/evidence. A package contains
the patched offline native copy, matching probe module and exact hash profile.
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

from patch_native_copy import PatchError, load_manifest, run, safe_output

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MANIFEST = ROOT / "evidence/engine-extension/native-pool-patch-4080.json"
DEFAULT_OUTPUT = ROOT / "build/battle-scale/native4080-test"
MODULE_IDS = ["Native", "SandBoxCore", "CustomBattle", "MNBL.BattleScale"]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def isolated_profile(source, module, patched_hash):
    """Bind shipped providers to retained audit hashes before producing a package."""
    game_root = source.resolve().parents[2]
    boundary_path = ROOT / "evidence/engine-extension/native-pool-module-boundaries.json"
    managed_path = ROOT / "evidence/engine-extension/pool-managed-expansion-audit.json"
    boundary = json.loads(boundary_path.read_text(encoding="utf-8-sig"))
    managed = json.loads(managed_path.read_text(encoding="utf-8-sig"))
    native_paths = {Path(x["path"]).resolve() for x in boundary["nativeModules"]}
    if boundary.get("skipped") or any(Path(x["path"]).resolve() not in native_paths for x in managed.get("skipped", [])):
        raise PatchError("Isolated profile requires complete retained module inventories")
    shipping = boundary["nativeModules"] + boundary["managedIdentities"]
    if len(shipping) != boundary["filesInspected"] or not shipping:
        raise PatchError("Inconsistent retained shipping inventory")
    trusted = {}

    def bind(path, expected=None, replacement=None):
        path = Path(path)
        if path.is_symlink() or path.is_junction():
            raise PatchError("Isolated profile does not admit linked provider files")
        path = path.resolve()
        try: relative = path.relative_to(game_root).as_posix()
        except ValueError: raise PatchError("Provider is outside the selected game installation")
        actual = sha(path)
        if expected is not None and actual != expected:
            raise PatchError("Installed provider changed since static audit: " + relative)
        key = relative.lower()
        if key in trusted:
            raise PatchError("Duplicate provider identity: " + relative)
        trusted[key] = {"relativePath": relative, "sha256": replacement or actual}

    for item in shipping:
        path = Path(item["path"])
        if path.resolve().parent != source.resolve().parent:
            raise PatchError("Shipping inventory belongs to another game installation")
        bind(path, item["sha256"], patched_hash if path.resolve() == source.resolve() else None)
    shipping_actual = {p.resolve() for p in source.parent.iterdir() if p.suffix.lower() in (".dll", ".exe")}
    if shipping_actual != {Path(x["path"]).resolve() for x in shipping}:
        raise PatchError("Unreviewed or missing shipping executable provider")
    audited = {Path(x["path"]).resolve(): x["sha256"] for x in managed["artifacts"]}
    for module_id in MODULE_IDS[:-1]:
        folder = game_root / "Modules" / module_id
        bind(folder / "SubModule.xml")
        binary_dir = folder / "bin/Win64_Shipping_Client"
        expected = {path for path in audited if path.parent == binary_dir.resolve()}
        actual = {path.resolve() for path in binary_dir.iterdir() if path.suffix.lower() in (".dll", ".exe")} if binary_dir.exists() else set()
        if actual != expected:
            raise PatchError("Unreviewed or missing official-module provider: " + module_id)
        if binary_dir.exists():
            for path in sorted(binary_dir.iterdir()):
                if path.suffix.lower() not in (".dll", ".exe"): continue
                if path.resolve() not in audited:
                    raise PatchError("Unreviewed official-module provider: " + str(path))
                bind(path, audited[path.resolve()])
    # Mono's bundled BCL is runtime infrastructure outside the nonrecursive92-file
    # inventory. Pin these files as infrastructure; never accept arbitrary paths.
    mono = source.parent / "mono"
    if mono.is_dir():
        for path in sorted(mono.rglob("*")):
            if path.is_file() and path.suffix.lower() in (".dll", ".exe"):
                bind(path)
    own_relative = "Modules/MNBL.BattleScale/bin/Win64_Shipping_Client/MNBL.BattleScale.dll"
    trusted[own_relative.lower()] = {"relativePath": own_relative, "sha256": sha(module)}
    own_xml = "Modules/MNBL.BattleScale/SubModule.xml"
    trusted[own_xml.lower()] = {"relativePath": own_xml, "sha256": sha(ROOT / own_xml)}
    platform_path = ROOT / 'evidence/engine-extension/native-platform-provider-review.json'
    external = []
    if platform_path.exists():
        platform = json.loads(platform_path.read_text(encoding='utf-8-sig'))
        allowed = {Path(r'C:\Program Files (x86)\Steam') / name for name in ('steamclient64.dll','tier0_s64.dll','vstdlib_s64.dll','gameoverlayrenderer64.dll')}
        allowed.update(Path(p) for p in (r'C:\ProgramData\NVIDIA\NGX\models\dlss\versions\131529\files\160_B9D3D54.bin',
            r'C:\Program Files\NVIDIA Corporation\NvTelemetry\NvTelemetryAPI64.dll',
            r'C:\Program Files\NVIDIA Corporation\NvTelemetry\NvTelemetryBridge64.dll'))
        if platform.get('status') != 'reviewed-pinned-platform-infrastructure':
            raise PatchError('Platform provider review is not accepted')
        for item in platform['files']:
            path = Path(item['path'])
            expected_vendor = 'Valve Corp' if path.parent == Path(r'C:\Program Files (x86)\Steam') else 'NVIDIA Corporation'
            if path not in allowed or item['signatureStatus'] != 'Valid' or expected_vendor not in item['signer'] or sha(path) != item['sha256']:
                raise PatchError('Pinned platform provider differs')
            external.append({'path':str(path),'sha256':item['sha256']})
    exit_path = ROOT / 'evidence/engine-extension/native-watchdog-shutdown-contract.json'
    exit_contract = json.loads(exit_path.read_text(encoding='utf-8-sig'))
    watchdog = source.parent / 'Watchdog/Watchdog.exe'
    if (exit_contract.get('status') != 'reviewed-build-specific-watchdog-shutdown'
            or exit_contract.get('nativeSourceSha256') != sha(source)
            or exit_contract.get('patchedCandidateSha256') != patched_hash
            or exit_contract.get('expectedWatchdogExitCode') != -1
            or Path(exit_contract['watchdogPath']).resolve() != watchdog.resolve()):
        raise PatchError('Shutdown contract does not cover the selected native/watchdog build')
    bind(watchdog, exit_contract['watchdogSha256'])
    return {"allowedModuleIds": MODULE_IDS, "installationRoot": str(game_root),
            "exitContract": {"path": str(exit_path), "sha256": sha(exit_path)},
            "requiredModuleArgument": "_MODULES_*" + "*".join(MODULE_IDS) + "*_MODULES_",
            "trustedFiles": sorted(trusted.values(), key=lambda x: x["relativePath"].lower()),
            "trustedExternalFiles": external,
            "identityEvidence": [{"path": str(x), "sha256": sha(x)} for x in (boundary_path, managed_path)],
            "providerPolicy": "Exact installed file paths and hashes; Windows System32, SysWOW64, WinSxS, Microsoft.NET and assembly directories trusted as OS/runtime infrastructure. Bundled Mono files pinned at packaging. Unknown loaded providers fail closed; no in-memory patch attestation."}


RANGED_PAGE_EDITS = ((555573, 'c1ea0b', 'c1ea0e'), (555585, 'c1e90b', 'c1e90e'), (555597, 'c1e10b', 'c1e10e'), (555661, 'c1ea0b', 'c1ea0e'), (555669, 'c1e10b', 'c1e10e'), (555837, '41c1ea0b', '41c1ea0e'), (555853, 'c1e90b', 'c1e90e'), (555868, 'c1e90b', 'c1e90e'), (555871, 'c1e80b', 'c1e80e'), (555888, 'c1e00b', 'c1e00e'), (555901, 'c1e00b', 'c1e00e'), (555961, 'c1e90b', 'c1e90e'), (555966, 'c1e00b', 'c1e00e'), (555983, 'c1ea0b', 'c1ea0e'), (555991, 'c1e10b', 'c1e10e'), (563715, 'c1e80b', 'c1e80e'), (563718, '4103ef', '4903ef'), (563723, '48c1ed0b', '48c1ed0e'), (563727, '483bf50f87e2000000', '4883fd7f76020f0b90'), (563800, 'ba00400000', 'ba00000200'), (563852, '41c1e00b', '41c1e00e'), (563856, '458d8800080000', '458d8800400000'), (563878, 'c1e80b', 'c1e80e'), (563886, 'c1e00b', 'c1e00e'), (1967418, 'c1eb0b', 'c1eb0e'), (1967423, 'c1e30b', 'c1e30e'), (1967757, '48c7858001000000400000', '48c7858001000000000200'), (2356744, 'c1e80b', 'c1e80e'), (2356752, '4103ec', '4903ec'), (2356755, '48c1ed0b', '48c1ed0e'), (2356759, '483bf50f87d7000000488d9f08040000482bee4803de4c8d7f0848ffc54d8d3cc70f1f840000000000', '83fd7f76020f0b483bf576020f0b488d9c3708040000482bee4c8d7cc70848ffc59090909090909090'), (2356822, 'ba00400000', 'ba00000200'), (2356876, '41c1e00b', '41c1e00e'), (2356880, '458d8800080000', '458d8800400000'), (2356902, 'c1e80b', 'c1e80e'), (2356910, 'c1e00b', 'c1e00e'), (2357079, 'c1ea0b', 'c1ea0e'), (2357090, 'c1e00b', 'c1e00e'))


def validate_ranged_page_edits(patches):
    for rva, original, replacement in RANGED_PAGE_EDITS:
        matches = [p for p in patches if p.get("address") == hex(0x180000000+rva)]
        if (len(matches) != 1 or matches[0]["rva"] != hex(rva)
                or matches[0]["original"] != original or matches[0]["replacement"] != replacement):
            raise PatchError("Native test preparation requires every exact ranged page edit: " + hex(rva))


def prepare(manifest_path, output, admission=False, workload="infantry", sustained_seconds=30, reuse_cycles=1):
    if workload not in ("infantry", "cavalry", "ranged") or not (120 if admission else 30) <= sustained_seconds <= 900 or not 1 <= reuse_cycles <= 10:
        raise PatchError("Invalid native workload configuration")
    if not admission and (workload != "infantry" or sustained_seconds != 30 or reuse_cycles != 1):
        raise PatchError("Expanded workloads require public spawn admission")
    manifest, manifest_hash = load_manifest(manifest_path)
    source = Path(manifest["sourcePath"])
    verified = run("verify", source, manifest_path)
    if (not verified["applyEligible"] or
            manifest.get("coverage", {}).get("semanticCoverageComplete") is not True or
            manifest.get("readiness", {}).get("staticReady") is not True):
        raise PatchError("Native test preparation refused: static patch safety review is incomplete")
    for item in manifest["sourceEvidence"].values():
        if sha(Path(item["path"])) != item["sha256"]:
            raise PatchError("Native test preparation refused: stale evidence " + item["path"])
    # Match the encoded RVA as well as the display address. A manifest label
    # alone does not establish where the offline writer will place the bytes.
    required_guards = [
        ("Single0", "0x180710c10", "0x710c10", "890dbe556900", "85c97402cd29"),
        ("contact producer capacity", "0x1805b79b8", "0x5b79b8", "0f1f840000000000", "4180fe3f7602cd29"),
        ("contact cache capacity", "0x1805b7a2c", "0x5b7a2c", "4863c2488d0c80498d04c9e966060000", "4883e820807808400f8668060000cd29"),
        ("renderer mapping capacity", "0x1800886d5", "0x886d5", "4d3bf70f8781000000488d9d080100004d2bfe4903de4c8d6d0849ffc74d8d6cc500", "4183ff2072020f0b4d39fe777d4a8d9c35080100004d2bfe4c8d6cc50849ffc79090"),
        ("renderer matrix capacity", "0x180089925", "0x89925", "488d99080100004803de4c8d710848ffc74d8d34c6660f1f440000", "8d1c3783fb1f76020f0b488d9c31080100004c8d74c10848ffc790"),
    ]
    for name, address, rva, original, replacement in required_guards:
        matches = [p for p in manifest["patches"] if p.get("address") == address]
        if (len(matches) != 1 or matches[0]["rva"] != rva or
                matches[0]["original"] != original or matches[0]["replacement"] != replacement):
            raise PatchError("Native test preparation requires the exact " + name + " fail-fast guard")
    # The producers, allocators and uploader must agree on page geometry.
    # Bounds guards alone cannot make a partially expanded page safe.
    renderer_geometry = [
        (0x886c8, 'c1e80b', 'c1e80e'), (0x886d1, '49c1ef0b', '49c1ef0e'),
        (0x88778, 'c1e90b', 'c1e90e'), (0x8877b, 'c1e80b', 'c1e80e'),
        (0x88792, 'c1e00b', 'c1e00e'), (0x887db, 'c1e90b', 'c1e90e'),
        (0x887e2, 'c1e00b', 'c1e00e'), (0x1e09e1, 'c1eb0b', 'c1eb0e'),
        (0x1e09e6, 'c1e30b', 'c1e30e'), (0x8870a, 'ba00200000', 'ba00000100'),
        (0x1e0b34, '48c7856002000000200000', '48c7856002000000000100')]
    for rva, original, replacement in renderer_geometry:
        matches = [p for p in manifest['patches'] if p.get('address') == hex(0x180000000+rva)]
        if (len(matches) != 1 or matches[0]['rva'] != hex(rva)
                or matches[0]['original'] != original or matches[0]['replacement'] != replacement):
            raise PatchError('Native test preparation requires every exact renderer page edit: '+hex(rva))
    validate_ranged_page_edits(manifest['patches'])
    for edit in manifest['patches']:
        if int(edit['address'],16) != int(manifest['imageBase'],16)+int(edit['rva'],16):
            raise PatchError('Patch address differs from actual encoded location')
    approval_name = 'native-pool-static-readiness-review.json'
    approval_path = ROOT/'evidence/engine-extension'/approval_name
    approval_ref = manifest['sourceEvidence'].get(approval_name, {})
    if (Path(approval_ref.get('path','')).resolve() != approval_path.resolve()
            or not approval_path.is_file() or sha(approval_path) != approval_ref.get('sha256')):
        raise PatchError('Missing exact static approval at package boundary')
    approval = json.loads(approval_path.read_text(encoding='utf-8-sig'))
    rows = [{key:p[key] for key in ('address','original','replacement')}
            for p in sorted(manifest['patches'],key=lambda p:int(p['address'],16))]
    canonical = hashlib.sha256(json.dumps(rows,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    expected_sources = {name:ref['sha256'] for name,ref in manifest['sourceEvidence'].items() if name!=approval_name}
    if (approval.get('approvedForRestrictedOfflineTest') is not True
            or approval.get('sourceSha256') != manifest['sourceSha256']
            or approval.get('canonicalPatchSetSha256') != canonical
            or approval.get('approvedSourceEvidenceSha256ByName') != expected_sources
            or approval.get('generatorSourceSha256') != manifest.get('generatorSourceSha256')
            or approval.get('generatorSourceSha256') != sha(ROOT/'tools/battle-scale/build_pool_patch_manifest.py')):
        raise PatchError('Manifest differs from exact static approval at package boundary')
    receipt_path = ROOT / "build/battle-scale/build-receipt.json"
    build = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
    module = ROOT / "build/battle-scale/module/MNBL.BattleScale.dll"
    if build["status"] != "built" or sha(module) != build["moduleSha256"]:
        raise PatchError("Missing or stale probe build")
    for item in build["sources"]:
        if sha(Path(item["path"])) != item["sha256"]:
            raise PatchError("Probe source changed after compilation: " + item["path"])
    isolation = isolated_profile(source, module, verified["patchedSha256"])
    # Reuse native writer path checks before creating anything, including
    # symlink/junction/UNC/installed-game protections. Require a fresh directory.
    output = safe_output(output, source, [source.parent.parent.parent])
    if output.exists():
        raise PatchError("Test package destination must not exist")
    if not output.parent.is_dir():
        raise PatchError("Test package parent must already exist")
    output.mkdir()
    created = []
    try:
        native = output / "TaleWorlds.Native.dll"
        native_receipt = output / "native-copy-receipt.json"
        run("apply", source, manifest_path, output=native, receipt=native_receipt)
        created.extend([native, native_receipt])
        def write(name, content):
            path = output / name
            with path.open("xb") as stream:
                created.append(path)
                stream.write(content)
        write("MNBL.BattleScale.dll", module.read_bytes())
        managed_info = None
        if admission:
            managed_copy = output / "TaleWorlds.MountAndBlade.dll"
            managed_receipt = output / "managed-admission-receipt.json"
            created.extend([managed_copy, managed_receipt])
            # The shipped Cecil build requires the modern .NET runtime used in review.
            subprocess.run(["pwsh", "-NoProfile", "-File", str(ROOT / "tools/battle-scale/Patch-ManagedSpawnAdmission.ps1"),
                            "-Source", str(source.parent / "TaleWorlds.MountAndBlade.dll"), "-Module", str(module),
                            "-Output", str(managed_copy), "-Receipt", str(managed_receipt)], check=True)
            managed_info = json.loads(managed_receipt.read_text(encoding="utf-8-sig"))
            if managed_info['patchedSha256'] != sha(managed_copy) or managed_info['moduleSha256'] != sha(module):
                raise PatchError("Managed admission copy does not match its receipt")
            relative = "bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll"
            matches = [x for x in isolation['trustedFiles'] if x['relativePath'] == relative]
            if len(matches) != 1 or matches[0]['sha256'] != managed_info['sourceSha256']:
                raise PatchError("Managed admission source is outside the pinned profile")
            matches[0]['sha256'] = managed_info['patchedSha256']
        write("SubModule.xml", (ROOT/"Modules/MNBL.BattleScale/SubModule.xml").read_bytes())
        write("native-pool-manifest.json", manifest_path.read_bytes())
        profile = {"schemaVersion": 2, "staticReady": True, "capacity": 4080,
                   "manifestSha256": manifest_hash, "patchedSha256": sha(native),
                   "sourceSha256": manifest["sourceSha256"], "probeModuleSha256": sha(module),
                   "configuredMaxThreadCount": 16,
                   "gameLaunchHeld": True, "runtimeVerified": False, **isolation}
        if managed_info:
            profile.update(schemaVersion=3, publicSpawnAdmission=True,
                           managedEngineSourceSha256=managed_info['sourceSha256'], managedEngineSha256=managed_info['patchedSha256'],
                           managedAdmissionReceiptSha256=sha(managed_receipt),
                           workload=workload, sustainedSeconds=sustained_seconds, reuseCycles=reuse_cycles)
        write("native-pool-test-profile.json", (json.dumps(profile, indent=2)+"\n").encode())
        write("build-receipt.json", receipt_path.read_bytes())
        workload_note = (f"Workload: {workload}; {sustained_seconds} real seconds of combat; {reuse_cycles} cycles of 64 physical-slot deletion/refill.\n"
                         "Mounted riders and horses each consume a physical slot. Public SpawnAgent rejection and private allocator exhaustion are both tested.\n"
                         "The managed engine copy also requires backup, deployment, provider hashing and restoration.\n") if admission else (
                         "Stages:512,2040,2041,3072,4080 physical infantry agents; native allocator exhaustion;64 deletions/refill;30s combat.\n"
                         "Legacy profile: public CreateAgent overflow is guarded only by the harness.\n")
        write("TEST-HELD.txt", (
            "Offline preparation only. Nothing was installed or launched.\n"
            "Launch requires current user authorization and a fresh check that no other Bannerlord is running.\n"
            "Future test setup must back up and verify the installed native DLL, install this exact copy,\n"
            "install the matching BattleScale module/profile, and restore the original after the run.\n"
            "Do not use the old Test-BattleScaleInGame script: it exercises a different mission.\n"
            "Use Test-NativePoolInGame.ps1 with this package; it waits for a free game window, backs up/restores files,\n"
            "and creates the unique native-pools:<GUID> request. Never reuse an old request or receipt.\n"
            "The future game command line must include /maxThreadCount 16; the probe checks actual native worker storage.\n"
            "Use exactly /singleplayer _MODULES_*Native*SandBoxCore*CustomBattle*MNBL.BattleScale*_MODULES_.\n"
            "Unknown loaded providers (including overlays outside Windows) fail the isolated-profile check.\n"
            + workload_note +
            "The probe verifies asynchronous mission teardown; the runner additionally requires normal process exit.\n"
            "Rendering/camera output requires separate inspection. No visible-count claim\n"
            "follows from allocation alone. Source/module/native hashes must match this profile.\n"
        ).encode())
    except BaseException:
        # Only files created by this call, inside this new checked directory.
        for path in reversed(created):
            path.unlink(missing_ok=True)
        output.rmdir()
        raise
    result = {"status": "offline-test-package-prepared", "path": str(output), **profile}
    print(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--admission", action="store_true")
    parser.add_argument("--workload", choices=("infantry", "cavalry", "ranged"), default="infantry")
    parser.add_argument("--sustained-seconds", type=int, default=30)
    parser.add_argument("--reuse-cycles", type=int, default=1)
    args = parser.parse_args()
    prepare(args.manifest, args.output, args.admission, args.workload, args.sustained_seconds, args.reuse_cycles)
