"""Replay one prospective segmented lookup hook. Never edits/loads the game.

The resolver is an emulator model, not engine integration. The stub has no
registered runtime unwind metadata and is NOT an installable patch.
"""
from pathlib import Path
import hashlib, json, struct, sys
from datetime import datetime, timezone
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'build/battle-scale/python-libs'))
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *

SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
EXPECTED='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE=0x180000000
ENTRY=BASE+0x70b150
START=BASE+0x70b165
END=BASE+0x70b18d
STUB=BASE+0x1000000
RESOLVER=STUB+0x100
RETAIN=STUB+0x200
RELEASE=STUB+0x210
STOP=STUB+0x300
STACK=0x50000000
MISSION=0x60000000
TABLE=0x61000000
GLOBAL=BASE+0x70b198+0x697890

def branch(op, here, target): return bytes([op])+struct.pack('<i', target-(here+5))

def main():
    data=SOURCE.read_bytes(); assert hashlib.sha256(data).hexdigest()==EXPECTED
    pe=pefile.PE(data=data)
    original=pe.get_data(0x70b150,0x90)
    assert original[START-ENTRY:END-ENTRY].hex()==('85d2786d3b9144da62007d654c63c2664239844150ca62007c574969c0600c00008b9c08682d0000')
    # Existing wrapper owns the aligned stack frame and shadow space. The helper
    # consumes RCX=Mission, EDX=signed index; returns Agent* or null in RAX.
    # On success skip the old displacement calculation and load Agent+18 directly.
    # JMP rel32 back into the wrapper is misrecognized as a completed epilogue
    # by the Windows unwinder. R11 register jumps retain borrowed-frame unwind
    # semantics (independently checked at every stub byte PC).
    stub=branch(0xe8,STUB,RESOLVER)+bytes.fromhex('4885c074108b5818')
    stub+=bytes.fromhex('49bb')+struct.pack('<Q',END)+bytes.fromhex('41ffe3')
    stub+=bytes.fromhex('33c0')
    stub+=bytes.fromhex('49bb')+struct.pack('<Q',BASE+0x70b1d6)+bytes.fromhex('41ffe3')
    patched=bytearray(original)
    # The entry transfer also borrows the existing frame. It needs the same
    # non-epilogue register jump as both stub continuation transfers.
    entry_jump=bytes.fromhex('49bb')+struct.pack('<Q',STUB)+bytes.fromhex('41ffe3')
    patched[START-ENTRY:END-ENTRY]=entry_jump+b'\x90'*(END-START-len(entry_jump))
    # Distinct blocks deliberately do not preserve a common stride across block
    # boundaries. No original Mission pool/table pages are mapped in the emulator.
    pointers={0:0x70000000,2039:0x71000000,2040:0x90000000,
              4079:0xa0000000,4080:0xc0000000,70000:0x140000000}
    identifiers={i:100+i for i in pointers}
    results=[]
    for index in [-1,0,2039,2040,4079,4080,4090,70000,70001,0x7fffffff]:
      for occupied in ([True,False] if index in pointers else [False]):
       for registered in (False,True):
        uc=Uc(UC_ARCH_X86,UC_MODE_64)
        for address,size in [(ENTRY&~4095,4096),(STUB,4096),(STACK,0x10000),
                             (GLOBAL&~4095,4096),(TABLE,4096)]:uc.mem_map(address,size)
        uc.mem_write(ENTRY,bytes(patched));uc.mem_write(STUB,stub)
        for address in (RESOLVER,RETAIN,RELEASE,STOP):uc.mem_write(address,b'\xc3')
        uc.mem_write(GLOBAL,struct.pack('<Q',TABLE if registered else 0))
        uc.mem_write(TABLE+0x30,struct.pack('<Q',RETAIN));uc.mem_write(TABLE+0x18,struct.pack('<Q',RELEASE))
        for i,pointer in pointers.items():
            uc.mem_map(pointer,4096);uc.mem_write(pointer+0x18,struct.pack('<I',identifiers[i]))
        calls=[]
        def observe(emu,address,size,user):
            if address==STOP:emu.emu_stop();return
            if address==RESOLVER:
                assert emu.reg_read(UC_X86_REG_RCX)==MISSION
                actual=emu.reg_read(UC_X86_REG_EDX)
                if actual&0x80000000:actual-=1<<32
                assert actual==index
                # Inside a Windows x64 callee RSP+8 is 16-byte aligned.
                assert (emu.reg_read(UC_X86_REG_RSP)+8)%16==0
                emu.reg_write(UC_X86_REG_RAX,pointers.get(actual,0) if occupied else 0)
                # Deliberately clobber volatile registers, as a real helper may.
                for reg in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R10,UC_X86_REG_R11):emu.reg_write(reg,0xeeeeeeee)
            if address in (RETAIN,RELEASE):calls.append((address,emu.reg_read(UC_X86_REG_ECX)))
        uc.hook_add(UC_HOOK_CODE,observe)
        rsp=STACK+0x8008;uc.mem_write(rsp,struct.pack('<Q',STOP))
        uc.reg_write(UC_X86_REG_RSP,rsp);uc.reg_write(UC_X86_REG_RCX,MISSION)
        uc.reg_write(UC_X86_REG_EDX,index&0xffffffff);uc.reg_write(UC_X86_REG_RBX,0x123456789abcdef0)
        uc.emu_start(ENTRY,STOP+1,count=1000)
        expected=identifiers[index] if occupied else 0
        assert uc.reg_read(UC_X86_REG_RIP)==STOP
        assert uc.reg_read(UC_X86_REG_EAX)==expected
        assert uc.reg_read(UC_X86_REG_RBX)==0x123456789abcdef0
        assert uc.reg_read(UC_X86_REG_RSP)==rsp+8
        assert calls==([(RETAIN,expected),(RELEASE,expected)] if expected and registered else [])
        results.append({'index':index,'occupied':occupied,'managedCallbacksRegistered':registered,
                        'returnedHandle':expected,'callbackCount':len(calls),'passed':True})
    output={'schemaVersion':1,'status':'passed-isolated-machine-code-replay-not-installable',
      'createdAtUtc':datetime.now(timezone.utc).isoformat(),'binarySha256':EXPECTED,
      'sourcePath':str(SOURCE),'toolSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
      'wrapperRva':'0x70b150','replacementSpanRva':['0x70b165','0x70b18d'],
      'originalBytes':original[START-ENTRY:END-ENTRY].hex(),
      'patchBytes':bytes(patched[START-ENTRY:END-ENTRY]).hex(),'stubAddress':hex(STUB),'stubBytes':stub.hex(),
      'unwindInfoHex':'0100020000520030',
      'resolverContract':'RCX=Mission, EDX=signed native index; RAX=valid stable Agent pointer or null; preserve nonvolatile registers; noexcept; descriptor lifetime and reader barrier required externally.',
      'checks':results,'passedCases':len(results),
      'limitations':['Resolver is an emulator model, not the C++ backend or live engine.',
        'Bounded incoming-reference and Windows unwind proof retained separately in native-segmented-lookup-hook-review.json; no stub unwind record registered in the game.',
        'Does not implement construction, spawn publication, worker barrier, or other inline consumers.',
        'Existing managed callback retain/release sequence preserved and exercised; exception behavior not emulated.',
        'Native API remains signed32; wider storage handles do not automatically widen native callers.',
        'No game launched and no installed binary changed.']}
    out=ROOT/'evidence/engine-extension/native-segmented-lookup-replay.json'
    out.write_text(json.dumps(output,indent=2)+'\n')
    print(json.dumps({'status':output['status'],'passedCases':len(results),'output':str(out)}))
if __name__=='__main__':main()
