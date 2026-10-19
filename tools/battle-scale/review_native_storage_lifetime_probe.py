"""Independent frozen lifetime/probe tests and expanded no-engine C ABI checks."""
from pathlib import Path
import ctypes
import hashlib
import json
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import pefile

RECEIPTS = {
    'evidence/engine-extension/owned-native-storage-tests.json':
        '017085a1bf32c24caa60a64efe2a2124e5db506ebe5f7e4f0e593becb7823055',
    'evidence/engine-extension/unpublished-element-probe-offline-tests.json':
        '15d272e21659d4aec1b9e795be8d26fdf971ca98968c26ae3b8c65d53af695c9'}


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Result(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint64) for name in (
        'abi_size', 'abi_version', 'status', 'stage', 'capacity', 'pointer_checks', 'header_checks',
        'allocation_attempts', 'allocations', 'releases', 'constructor_attempts')] + [
        ('constructed', ctypes.c_uint64 * 5), ('destroyed', ctypes.c_uint64 * 5)] + [
        (name, ctypes.c_uint64) for name in ('allocation_failure_checked', 'constructor_failure_checked',
                                         'exhaustion_checked', 'reuse_checked')] + [('error', ctypes.c_char * 512)]


def build():
    pinned, reports = {}, {}
    for path, sha in RECEIPTS.items():
        assert digest(ROOT / path) == sha
        report = json.loads((ROOT / path).read_text())
        reports[path] = report
        for key in ('sources', 'outputs'):
            for source, expected in report[key].items():
                assert source not in pinned or pinned[source] == expected
                pinned[source] = expected
    for path, sha in pinned.items():
        assert digest(ROOT / path) == sha, path
    runtime = Path('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64')
    asan = runtime / 'clang_rt.asan_dynamic-x86_64.dll'
    assert asan.is_file()
    environment = dict(os.environ)
    environment['PATH'] = str(runtime) + os.pathsep + environment.get('PATH', '')
    runs = []
    for name, marker in [
        ('owned_native_storage_tests.exe', 'PASS owned native storage:'),
        ('owned_native_storage_asan.exe', 'PASS owned native storage:'),
        ('unpublished_element_probe_tests.exe', 'PASS unpublished element probe:'),
        ('unpublished_element_probe_asan.exe', 'PASS unpublished element probe:')]:
        path = ROOT / 'build/agent-storage' / name
        run = subprocess.run([str(path)], cwd=ROOT, env=environment, text=True,
                             capture_output=True, timeout=60)
        assert run.returncode == 0 and run.stdout.count(marker) == 1, (run.stdout, run.stderr)
        runs.append({'path': str(path), 'sha256': digest(path), 'exitCode': run.returncode,
                     'stdout': run.stdout, 'stderr': run.stderr})
    probe_path = ROOT / 'build/agent-storage/MNBL.NativeElementProbe.dll'
    pe = pefile.PE(str(probe_path))
    imports = [row.dll.decode() for row in pe.DIRECTORY_ENTRY_IMPORT]
    assert not any('taleworlds' in name.lower() for name in imports)
    exports = {row.name.decode(): row.address for row in pe.DIRECTORY_ENTRY_EXPORT.symbols if row.name}
    assert set(exports) == {'MNBLElementProbe', 'MNBLElementProbeResultSize'}
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
    kernel.GetModuleHandleW.restype = ctypes.c_void_p
    assert not kernel.GetModuleHandleW('TaleWorlds.Native.dll'), 'No-engine fixture must not contain game module'
    # This loads only the built private probe into this reviewer process. No
    # target native module is loaded, injected, opened or inspected.
    dll = ctypes.CDLL(str(probe_path))
    dll.MNBLElementProbeResultSize.argtypes = []
    dll.MNBLElementProbeResultSize.restype = ctypes.c_uint64
    dll.MNBLElementProbe.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
    dll.MNBLElementProbe.restype = ctypes.c_int
    size = ctypes.sizeof(Result)
    assert size == 712 and dll.MNBLElementProbeResultSize() == size
    assert Result.error.offset == 200 and ctypes.alignment(Result) == 8
    checks = []
    for supplied_size in (0, 1, size - 1, size, size + 1, (1 << 64) - 1):
        assert dll.MNBLElementProbe(None, supplied_size) == 1
        checks.append({'kind': 'null-output', 'suppliedBytes': supplied_size, 'returnCode': 1})
    for supplied_size in (0, 1, size - 1, size + 1, (1 << 64) - 1):
        buffer = ctypes.create_string_buffer(b'\xa5' * (size + 64))
        address = ctypes.addressof(buffer) + 32
        assert address % 8 == 0
        before = buffer.raw
        assert dll.MNBLElementProbe(address, supplied_size) == 1 and buffer.raw == before
        checks.append({'kind': 'wrong-size-no-write', 'suppliedBytes': supplied_size,
                       'wholeBufferUnchanged': True, 'returnCode': 1})
    for attempt in range(4):
        # Different poison on each call ensures fields are cleared afresh and
        # sequential rejection cannot silently retain a previous result.
        poison = bytes([0x55 + attempt])
        buffer = ctypes.create_string_buffer(poison * (size + 64))
        address = ctypes.addressof(buffer) + 32
        assert address % 8 == 0
        assert dll.MNBLElementProbe(address, size) == 3
        result = Result.from_address(address)
        assert (result.abi_size, result.abi_version, result.status, result.stage) == (size, 1, 2, 0)
        assert result.error == b'Native module is not loaded'
        for name, ctype in Result._fields_[4:-1]:
            value = getattr(result, name)
            assert all(item == 0 for item in value) if isinstance(value, ctypes.Array) else value == 0
        assert buffer.raw[:32] == poison * 32 and buffer.raw[32 + size:64 + size] == poison * 32
        checks.append({'kind': 'no-engine-rejection', 'attempt': attempt,
                       'returnCode': 3, 'status': result.status, 'stage': result.stage,
                       'allProbeCountersZero': True, 'bothCanariesPreserved': True,
                       'error': result.error.decode()})
    assert not kernel.GetModuleHandleW('TaleWorlds.Native.dll')
    for path, sha in pinned.items():
        assert digest(ROOT / path) == sha, path
    for path, sha in RECEIPTS.items():
        assert digest(ROOT / path) == sha, path
    return {
        'schemaVersion': 1, 'status': 'accepted-owned-lifetime-and-private-probe-offline-only',
        'reviewedReceipts': [{'path': path, 'sha256': sha} for path, sha in RECEIPTS.items()],
        'reviewedSourceAndOutputSha256': pinned, 'reviewerSourceSha256': digest(__file__),
        'independentExecutableRuns': runs, 'additionalExportAbiChecks': checks,
        'abi': {'resultBytes': size, 'resultAlignment': ctypes.alignment(Result),
                'errorOffset': Result.error.offset, 'exports': {name: hex(rva) for name, rva in exports.items()},
                'imports': imports, 'noTargetModuleDependencyOrLoad': True},
        'asanRuntime': {'path': str(asan), 'sha256': digest(asan)},
        'manualReview': [
            'Owner declares factory before storage, so storage and all callbacks are destroyed first. ReadLease declares retained Owner before inner lease; destruction releases the inner lock/state first, then may destroy Owner. Inner backend lease likewise declares state before shared lock.',
            'Lease move construction transfers both owner and lock/state, leaving the moved-from lease safely destructible. Copy and move assignment are unavailable. Public wrapper is noncopyable and implicitly nonmovable; no owner replacement can strand an older lock or raw callback context.',
            'A lease can outlive destruction of the public wrapper and still call all forwarded methods. Tests verify native destructor callbacks complete before the runtime owner releases, including moved lease, failed growth rollback and explicitly closed storage.',
            'Private probe uses a locally scoped factory, instrumented callbacks then storage; unwinding destroys storage while both callback layers and result remain alive. No Mission pointer, activation/reset call or published engine array is accepted.',
            'Stage2 injects one allocation failure before any new allocation. Stage3 injects failure before constructor number37 of the staged suffix, so only37 completed callbacks unwind; it is not a simulation of a partially executed native constructor.',
            'Observed successful callback totals120/97/90/90/90 equal487; all are destroyed.20 successful allocations are released after21 attempts.600 earlier-pointer checks and450 inactive-header checks cover30->60->90 private growth. Both domains exhaust/reuse independently through ledger-only APIs.',
            'C ABI validates null/exact byte size before writing, initializes the entire result, and reports fixed size/version. Atomic_flag guard has RAII release on success and caught C++ exceptions. Repeated no-engine rejection confirms the early failure path does not leave it set.',
            'The exported no-engine path rejects during loaded-module lookup before private allocations or native constructors. Probe DLL imports no TaleWorlds module and does not call LoadLibrary for it. The review loads only this probe DLL in its own process.'],
        'openFindings': [],
        'limitations': [
            'Main Agent constructor/destructor remain synthetic in the four executables. Actual companion bytes execute, but default real Agent constructor closure and live runtime/thread readiness are separate requirements.',
            'Probe is private allocation/cleanup validation, not visible soldiers, Mission growth, activation, access routing or whole-engine synchronization.',
            'The C ABI catches C++ exceptions under EHsc. It does not guarantee containment or cleanup after arbitrary native hardware/SEH faults, process termination or a throwing noexcept destructor.',
            'Move-from leases are only required to be destructible; their ordinary accessors are not safe to call after ownership has moved away. Raw get/physical pointers still require the retained lease and engine ownership contract.',
            'Concurrent active-call rejection was reviewed statically; the no-engine calls only test sequential early failure and gate release. Valid output pointers, alignment, exclusive buffer access and the native runtime/thread barrier remain caller responsibilities.',
            'Original imported runtime owner and adopted-array lifetimes do not establish Mission/job lifetime or a global mutation barrier.',
            'Frozen optimized/ASAN executables were rerun, not rebuilt; existing source/output hashes remained unchanged. No canonical receipt or source was modified.',
            'No game launch, game process access/control, installed-file access or deployment approval.'],
        'gameLaunched': False, 'enginePatchApproved': False}


if __name__ == '__main__':
    result = build()
    path = ROOT / 'evidence/engine-extension/native-storage-lifetime-probe-independent-review.json'
    path.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(path), 'sha256': digest(path), 'status': result['status'],
                      'nativeRuns': len(result['independentExecutableRuns']),
                      'abiChecks': len(result['additionalExportAbiChecks'])}))
