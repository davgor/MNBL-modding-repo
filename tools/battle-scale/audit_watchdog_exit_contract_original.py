"""Stock-build watchdog kill protocol for the unmodified installed native DLL.

Reuses the frozen audit_watchdog_exit_contract.py bundle extraction and the
same managed/native proof spans, with no patch applied. Never loads target code.
"""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT/'build/battle-scale/python-libs'), str(Path(__file__).parent)]
import pefile
import capstone
import audit_watchdog_exit_contract as frozen
from patch_native_copy import safe_review_output

ORIGINAL_SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
WATCHDOG_SHA = '01c871b3742700e1ccf15e89d94a7e019525e4e1917a6b7c31765188b5ff6983'
INSTALLED_NATIVE = frozen.WATCHDOG.parents[1]/'TaleWorlds.Native.dll'
HISTORICAL = ROOT/'evidence/engine-extension/native-watchdog-shutdown-contract.json'
OUTPUT = ROOT/'evidence/engine-extension/native-original-watchdog-shutdown-contract.json'


def build():
    manifest, selected = frozen.bundle()
    assert manifest['sha256'] == WATCHDOG_SHA
    for name, data in selected.items():
        assert (frozen.DERIVED/name).read_bytes() == data
    managed_path = ROOT/'evidence/engine-extension/native-watchdog-managed-exit-evidence.json'
    managed = json.loads(managed_path.read_text(encoding='utf-8-sig'))
    assert managed['watchdogSha256'] == manifest['sha256']
    for name, data in selected.items():
        assert managed['extractedSha256'][name] == frozen.digest(data)

    def method_contains(fragment, required):
        matches = [m for m in managed['methods'] if fragment in m['method']]
        assert len(matches) == 1, fragment
        for line in required:
            assert line in matches[0]['il'], line
    method_contains('Debugger::process_command_message(', [
        'IL_0108: ldstr "kp"',
        'IL_0112: brtrue IL_0317',
        'IL_0318: call System.Void WatchdogLib.Debugger::kill_process()'])
    method_contains('Debugger::process_debuggee_message(', [
        'IL_0016: ldstr "#TW#"',
        'IL_005b: callvirt System.String System.String::Substring(System.Int32,System.Int32)',
        'IL_0068: call System.Void WatchdogLib.Debugger::process_command_message(System.String,System.String,Watchdog.WindowsNative/DebugEvent)'])
    method_contains('Debugger::kill_process()', [
        'IL_0001: ldfld System.Int32 WatchdogLib.Debugger::m_pid',
        'IL_0006: call System.Diagnostics.Process System.Diagnostics.Process::GetProcessById(System.Int32)',
        'IL_000b: callvirt System.Void System.Diagnostics.Process::Kill()'])
    method_contains('System.Diagnostics.Process::Kill()', [
        'IL_0018: ldc.i4.m1',
        'IL_0019: call System.Boolean Interop/Kernel32::TerminateProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle,System.Int32)'])
    assert managed['pinvokes'][0]['entryPoint'] == 'TerminateProcess'
    assert managed['pinvokes'][0]['module'] == 'kernel32.dll'

    raw = frozen.NATIVE.read_bytes()
    assert frozen.digest(raw) == ORIGINAL_SHA
    installed = INSTALLED_NATIVE.read_bytes()
    # The contract covers the binary that actually runs, not only the offline backup.
    assert frozen.digest(installed) == ORIGINAL_SHA and installed == raw
    pe = pefile.PE(data=raw)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    spans = {
        'normalMain': (0x47740, 0x47781),
        'cleanupTaskCreationAndWait': (0x47311, 0x473cf),
        'cleanupTaskEntryThunk': (0x736c0, 0x736c5),
        'shutdownWorker': (0x468a0, 0x46ad2),
        'fullShutdown': (0x520c0, 0x532f9),
        'watchdogCtorSelection': (0x459f0, 0x45aa7),
        'watchdogCtorReturn': (0x464a4, 0x464d7),
        'watchdogCtorVtable': (0x723c90, 0x723ce7),
        'watchdogActiveGetter': (0x723ec0, 0x723ec5),
        'initializerWatchdogReturn': (0x46ae0, 0x46b1b),
        'initializerWatchdogPublication': (0x46d1c, 0x46d67),
        'killCommandEmitter': (0x7247a0, 0x724819),
        'debugCommandFormatter': (0x723920, 0x723ba0),
    }
    rows = {}
    for name, (start, end) in spans.items():
        decoded = list(decoder.disasm(pe.get_data(start, end-start), 0x180000000+start))
        assert sum(i.size for i in decoded) == end-start, name
        rows[name] = [dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str) for i in decoded]
    assert pe.get_data(0xb28890, 5) == b'#TW#\0'
    assert pe.get_data(0xaed39c, 2) == b'-\0'
    assert pe.get_data(0xb288d8, 3) == b'kp\0'
    assert struct.unpack('<Q', pe.get_data(0xb28918, 8))[0] == 0x1807247a0
    assert struct.unpack('<Q', pe.get_data(0xae56c8, 8))[0] == 0x1800736c0
    imports = {i.address: i.name.decode() for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports if i.name}
    assert imports[0x180a2d450] == 'OutputDebugStringA'

    # Revalidation: the frozen patched-build contract decoded the same spans from
    # this same original image. Its rows must be byte-for-byte identical here.
    historical_bytes = HISTORICAL.read_bytes()
    historical = json.loads(historical_bytes.decode('utf-8-sig'))
    assert historical['nativeSourceSha256'] == ORIGINAL_SHA
    assert historical['watchdogSha256'] == WATCHDOG_SHA
    assert historical['nativeProofs'] == rows
    assert historical['bundleExtraction'] == manifest

    sources = {str(p): frozen.digest(p.read_bytes()) for p in (
        frozen.NATIVE, INSTALLED_NATIVE, frozen.WATCHDOG, managed_path, HISTORICAL, Path(__file__),
        Path(frozen.__file__), ROOT/'tools/battle-scale/managed-audit/Read-WatchdogExitContract.ps1')}
    return dict(schemaVersion=1, status='reviewed-build-specific-watchdog-shutdown',
                nativeSourceSha256=ORIGINAL_SHA, watchdogPath=str(frozen.WATCHDOG), watchdogSha256=manifest['sha256'],
                expectedWatchdogExitCode=-1, patchedCandidateSha256=ORIGINAL_SHA,
                buildKind='stock-original-no-native-patch',
                sourceHashes=sources, bundleExtraction=manifest, managedEvidence=managed,
                nativeProofs=rows,
                historicalContractRowsIdentical=True,
                historicalContractSha256=frozen.digest(historical_bytes),
                patchCount=0,
                findings=[
                    'Contract binds the unmodified installed TaleWorlds.Native.dll; patchedCandidateSha256 equals the original because no native edit is applied.',
                    'The native normal-shutdown task invokes 520c0, then, when the watchdog is active, invokes its virtual+28 at 46942.',
                    'Initializer 459f0 selects watchdog constructor 723c90, which installs vtable B288F0. Initializer 46b13 retains its return in R12 and 46d63 publishes it at global DA2998->18.',
                    'Vtable B288F0+28 points 7247a0. That method passes literal kp and empty arguments to 723920, which emits #TW#-kp with OutputDebugStringA.',
                    'WatchdogLib parses the debug string, dispatches kp to kill_process, looks up its configured m_pid, and calls Process.Kill().',
                    'The exact bundled System.Diagnostics.Process.Kill IL invokes kernel32.TerminateProcess(processHandle,-1). Exit -1 is the stock shutdown protocol.',
                    'All 13 decoded spans equal the rows recorded in the frozen patched-build contract, which decoded the same original image.'],
                acceptanceScope='Stock-build expected watchdog termination after successful main-menu probe and observed per-owned-PID shutdown protocol; not a global allowance for exit -1.',
                limitations=['Does not certify every native destructor or outer cleanup return completed.',
                             'A bare -1 exit without matching watchdog identity, owned process identity and ordered protocol evidence is not explained or approved.',
                             'No game execution, runtime attachment, deployment or target code execution occurred in this audit.'])


if __name__ == '__main__':
    assert not sys.argv[1:]
    result = build()
    safe_review_output(OUTPUT, list(result['sourceHashes']))
    OUTPUT.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(result['status'], hashlib.sha256(OUTPUT.read_bytes()).hexdigest())
