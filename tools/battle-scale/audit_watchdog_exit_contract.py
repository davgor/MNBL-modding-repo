"""Static, build-specific watchdog kill protocol. Never loads target code."""
from pathlib import Path
import hashlib
import json
import struct
import sys
import zlib

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT/'build/battle-scale/python-libs'), str(Path(__file__).parent)]
import pefile
import capstone
from patch_native_copy import safe_review_output

WATCHDOG = Path('D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/Watchdog/Watchdog.exe')
NATIVE = ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
DERIVED = ROOT/'build/battle-scale/watchdog-static'
SELECTED = ('Watchdog.dll','WatchdogLib.dll','System.Diagnostics.Process.dll')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def bundle():
    raw = WATCHDOG.read_bytes()
    assert digest(raw)=='01c871b3742700e1ccf15e89d94a7e019525e4e1917a6b7c31765188b5ff6983'
    signature = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
    at = raw.index(signature)
    assert raw.find(signature,at+1) == -1
    header = struct.unpack_from('<Q',raw,at-8)[0]
    major,minor,count = struct.unpack_from('<III',raw,header)
    assert (major,minor,count) == (6,0,470)
    pos = header+12
    def string():
        nonlocal pos
        size=0;shift=0
        while True:
            byte=raw[pos];pos+=1;size|=(byte&127)<<shift
            if byte<128:break
            shift+=7
            assert shift<35
        value=raw[pos:pos+size].decode('utf-8');pos+=size
        return value
    bundle_id=string()
    metadata=struct.unpack_from('<5Q',raw,pos);pos+=40
    entries=[];selected={}
    for _ in range(count):
        offset,size,compressed=struct.unpack_from('<QQQ',raw,pos);pos+=24
        kind=raw[pos];pos+=1;name=string()
        assert offset+(compressed or size)<=len(raw)
        if name in SELECTED:
            data=raw[offset:offset+(compressed or size)]
            if compressed:data=zlib.decompress(data,-15)
            assert len(data)==size and data[:2]==b'MZ'
            selected[name]=data
            entries.append(dict(name=name,offset=offset,size=size,compressedSize=compressed,kind=kind,sha256=digest(data)))
    assert set(selected)==set(SELECTED)
    return dict(source=str(WATCHDOG),sha256=digest(raw),signatureFileOffset=at,headerFileOffset=header,
                version=[major,minor],fileCount=count,bundleID=bundle_id,metadata=list(metadata),
                selectedEntries=entries),selected


def build():
    manifest, selected = bundle()
    for name,data in selected.items():
        assert (DERIVED/name).read_bytes()==data
    managed_path=ROOT/'evidence/engine-extension/native-watchdog-managed-exit-evidence.json'
    managed=json.loads(managed_path.read_text(encoding='utf-8-sig'))
    assert managed['watchdogSha256']==manifest['sha256']
    for name,data in selected.items():assert managed['extractedSha256'][name]==digest(data)
    def method_contains(fragment, required):
        matches=[m for m in managed['methods'] if fragment in m['method']]
        assert len(matches)==1
        for line in required:assert line in matches[0]['il'],line
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
    assert managed['pinvokes'][0]['entryPoint']=='TerminateProcess'
    assert managed['pinvokes'][0]['module']=='kernel32.dll'
    raw=NATIVE.read_bytes()
    native_sha=digest(raw)
    assert native_sha=='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe=pefile.PE(data=raw)
    decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
    # Whole emitting and shutdown-worker bodies, with selected complete
    # constructor, publication and task-dispatch blocks.
    spans={
        'normalMain':(0x47740,0x47781),
        'cleanupTaskCreationAndWait':(0x47311,0x473cf),
        'cleanupTaskEntryThunk':(0x736c0,0x736c5),
        'shutdownWorker':(0x468a0,0x46ad2),
        'fullShutdown':(0x520c0,0x532f9),
        'watchdogCtorSelection':(0x459f0,0x45aa7),
        'watchdogCtorReturn':(0x464a4,0x464d7),
        'watchdogCtorVtable':(0x723c90,0x723ce7),
        'watchdogActiveGetter':(0x723ec0,0x723ec5),
        'initializerWatchdogReturn':(0x46ae0,0x46b1b),
        'initializerWatchdogPublication':(0x46d1c,0x46d67),
        'killCommandEmitter':(0x7247a0,0x724819),
        'debugCommandFormatter':(0x723920,0x723ba0),
    }
    rows={}
    for name,(start,end) in spans.items():
        decoded=list(decoder.disasm(pe.get_data(start,end-start),0x180000000+start))
        assert sum(i.size for i in decoded)==end-start,name
        rows[name]=[dict(address=hex(i.address),bytes=i.bytes.hex(),mnemonic=i.mnemonic,operands=i.op_str) for i in decoded]
    assert pe.get_data(0xb28890,5)==b'#TW#\0'
    assert pe.get_data(0xaed39c,2)==b'-\0'
    assert pe.get_data(0xb288d8,3)==b'kp\0'
    assert struct.unpack('<Q',pe.get_data(0xb28918,8))[0]==0x1807247a0
    assert struct.unpack('<Q',pe.get_data(0xae56c8,8))[0]==0x1800736c0
    imports={i.address:i.name.decode() for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports if i.name}
    assert imports[0x180a2d450]=='OutputDebugStringA'
    patch_path=ROOT/'build/battle-scale/native4080-watchdog-static-input.json'
    patch=json.loads(patch_path.read_text(encoding='utf-8-sig'))
    protected=list(spans.values())+[(0xb28890,0xb28895),(0xaed39c,0xaed39e),(0xb288d8,0xb288db),
                                  (0xb288e8,0xb28940),(0xae5660,0xae56d0),(0xa2d450,0xa2d458)]
    edits=patch.get('patches',patch.get('edits'))
    assert isinstance(edits,list)
    patched=bytearray(raw)
    for edit in edits:
        rva=int(edit['rva'],16) if isinstance(edit['rva'],str) else edit['rva']
        original=bytes.fromhex(edit['original']);replacement=bytes.fromhex(edit['replacement'])
        assert len(original)==len(replacement)
        assert not any(rva<end and rva+len(original)>start for start,end in protected),hex(rva)
        offset=pe.get_offset_from_rva(rva)
        assert patched[offset:offset+len(original)]==original
        patched[offset:offset+len(original)]=replacement
    sources={str(p):digest(p.read_bytes()) for p in (NATIVE,WATCHDOG,managed_path,patch_path,Path(__file__),
             ROOT/'tools/battle-scale/managed-audit/Read-WatchdogExitContract.ps1')}
    return dict(schemaVersion=1,status='reviewed-build-specific-watchdog-shutdown',
                nativeSourceSha256=native_sha,watchdogPath=str(WATCHDOG),watchdogSha256=manifest['sha256'],
                expectedWatchdogExitCode=-1,patchedCandidateSha256=digest(patched),
                sourceHashes=sources,bundleExtraction=manifest,managedEvidence=managed,
                nativeProofs=rows,protectedShutdownRvaSpans=[[hex(a),hex(b)] for a,b in protected],
                patchOverlapCount=0,patchCount=len(edits),
                findings=[
                    'The native normal-shutdown task invokes520c0, then, when the watchdog is active, invokes its virtual+28 at46942.',
                    'Initializer459f0 selects watchdog constructor723c90, which installs vtableB288F0. Initializer46b13 retains its return inR12 and46d63 publishes it atglobalDA2998->18.',
                    'VtableB288F0+28 points7247a0. That method passes literal kp and empty arguments to723920, which emits #TW#-kp with OutputDebugStringA.',
                    'WatchdogLib parses the debug string, dispatches kp to kill_process, looks up its configured m_pid, and calls Process.Kill().',
                    'The exact bundled System.Diagnostics.Process.Kill IL invokes kernel32.TerminateProcess(processHandle,-1). The exit value is deliberate in this shutdown protocol.',
                    '520c0 performs the recorded final subsystem cleanup, including the device-release diagnostic at532c6, before the kill request. Additional worker releases and outer472b0 return remain after that request and may not finish.',
                    'Every current patch was checked for nonoverlap with the retained route functions, relevant vtables, strings and debug-output import. Candidate is reconstructed in memory only.'],
                acceptanceScope='Build-pinned expected watchdog termination after successful mission teardown and observed per-owned-PID shutdown protocol; not a global allowance for exit-1.',
                limitations=['Does not certify every native destructor or outer cleanup return completed.',
                             'A bare-1 exit without matching watchdog identity, owned process identity and ordered protocol evidence is not explained or approved.',
                             'No game execution, runtime attachment, deployment or target code execution occurred in this audit.'])


if __name__=='__main__':
    if sys.argv[1:]==['--extract']:
        manifest,selected=bundle();DERIVED.mkdir(parents=True,exist_ok=True)
        for name,data in selected.items():
            destination=DERIVED/name
            if destination.exists():assert destination.read_bytes()==data
            else:destination.write_bytes(data)
        print('Verified three selected bundled managed assemblies; no execution.')
    else:
        assert not sys.argv[1:]
        result=build()
        output=ROOT/'evidence/engine-extension/native-watchdog-shutdown-contract.json'
        safe_review_output(output,list(result['sourceHashes']))
        output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
        print(result['status'])
