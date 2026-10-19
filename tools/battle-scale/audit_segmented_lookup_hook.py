"""Independent, offline review of one segmented lookup trampoline.

Only reads retained original bytes. Synthetic Windows unwind tests execute ntdll,
not copied game instructions; instruction behavior is tested solely in Unicorn.
Writes its own receipt and disposable independent-test files under build/.
"""
from pathlib import Path
from datetime import datetime, timezone
import ctypes, hashlib, importlib.util, json, os, struct, subprocess, sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
from capstone.x86 import X86_OP_MEM, X86_REG_RIP
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *

OUT = ROOT / 'evidence/engine-extension/native-segmented-lookup-hook-review.json'
BUILD = ROOT / 'build/agent-storage/independent-hook-review'
LO, HI = 0x70b165, 0x70b18d


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def candidate():
    path = ROOT / 'tools/battle-scale/replay_segmented_agent_lookup.py'
    spec = importlib.util.spec_from_file_location('reviewed_lookup_candidate', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)  # guarded main: no execution/write of root replay
    return mod


def insrow(i):
    return {'address': hex(i.address), 'bytes': i.bytes.hex(),
            'mnemonic': i.mnemonic, 'operands': i.op_str}


def references(pe, data, dis):
    """Conservative all-byte relative-opcode scan plus decoded RIP references.

    Every byte can start an opcode, including bytes hidden by linear decoding.
    Prefixes do not alter the opcode-end-relative target in the accepted 64-bit
    rel8/rel32 encodings. False positives are retained, not silently discarded.
    """
    base = pe.OPTIONAL_HEADER.ImageBase
    incoming, riprefs, rawriprefs, gaps = [], [], [], []
    decoded = set()
    scanned = 0
    lite = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    lite.skipdata = True
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:
            continue
        code = section.get_data()
        rva = section.VirtualAddress
        scanned += len(code)
        for a, size, mnemonic, operands in lite.disasm_lite(code, base + rva):
            if mnemonic == '.byte':
                gaps.append(hex(a - base))
                continue
            decoded.add(a - base)
            if 'rip' in operands:
                i = next(dis.disasm(code[a-base-rva:a-base-rva+size], a))
                for op in i.operands:
                    if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                        target = i.address + i.size + op.mem.disp - base
                        if LO <= target < HI:
                            riprefs.append(dict(insrow(i), targetRva=hex(target)))
        for p, opcode in enumerate(code):
            # Operand-size-prefixed XBEGIN can carry rel16 in64-bit mode.
            # Retain the unprefixed hypothesis too: conservative false positives
            # are preferable to omitting a direct transactional fallback edge.
            if opcode == 0xc7 and p+4 <= len(code) and code[p+1] == 0xf8:
                target=rva+p+4+int.from_bytes(code[p+2:p+4],'little',signed=True)
                if LO <= target < HI:
                    incoming.append({'opcodeRva':hex(rva+p),'targetRva':hex(target),
                         'kind':'possible-prefixed-xbegin-rel16','bytes':code[p:p+4].hex(),
                         'linearInstructionBoundary':rva+p in decoded,
                         'interiorTarget':target != LO,'sourceInsideSpan':LO<=rva+p<HI})
            width, length, kind = 0, 0, None
            if opcode in (0xe8, 0xe9):
                width, length, kind = 4, 5, 'call/jump-rel32'
            elif opcode == 0xeb or 0x70 <= opcode <= 0x7f or 0xe0 <= opcode <= 0xe3:
                width, length, kind = 1, 2, 'jump/loop-rel8'
            elif opcode == 0x0f and p+1 < len(code) and 0x80 <= code[p+1] <= 0x8f:
                width, length, kind = 4, 6, 'conditional-rel32'
            elif opcode == 0xc7 and p+1 < len(code) and code[p+1] == 0xf8:
                width, length, kind = 4, 6, 'xbegin-rel32'
            if width and p+length <= len(code):
                displacement = int.from_bytes(code[p+length-width:p+length], 'little', signed=True)
                target = rva+p+length+displacement
                if LO <= target < HI:
                    incoming.append({'opcodeRva': hex(rva+p), 'targetRva': hex(target),
                                     'kind': kind, 'bytes': code[p:p+length].hex(),
                                     'linearInstructionBoundary': rva+p in decoded,
                                     'interiorTarget': target != LO,
                                     'sourceInsideSpan': LO <= rva+p < HI})
        # Exhaustive possible RIP disp32 positions: instruction may carry up to
        # eight immediate bytes after displacement. Decode every feasible start
        # up to the architectural15-byte maximum, retaining overlapping hits.
        for p in range(len(code)-3):
            target_if_no_suffix=rva+p+4+struct.unpack_from('<i',code,p)[0]
            if not LO-8 <= target_if_no_suffix < HI:
                continue
            for start in range(max(0,p-11),p+1):
                ins=next(dis.disasm(code[start:start+15],base+rva+start,count=1),None)
                if not ins or ins.disp_size != 4 or start+ins.disp_offset != p:
                    continue
                for op in ins.operands:
                    if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                        target=ins.address+ins.size+op.mem.disp-base
                        if LO<=target<HI:
                            rawriprefs.append(dict(insrow(ins),targetRva=hex(target),
                                linearInstructionBoundary=rva+start in decoded))
    literals = []
    for target in range(LO, HI):
        for kind, pattern in [('rva32', struct.pack('<I', target)),
                              ('va64', struct.pack('<Q', base+target))]:
            offset = 0
            while True:
                offset = data.find(pattern, offset)
                if offset < 0:
                    break
                literals.append({'fileOffset': hex(offset), 'targetRva': hex(target),
                                 'kind': kind, 'sourceRva': hex(pe.get_rva_from_offset(offset))})
                offset += 1
    return {'executableBytesScannedAtEveryOpcodeOffset': scanned,
            'directRelativeCandidates': incoming, 'decodedRipRelativeReferences': riprefs,
            'allOffsetRipRelativeCandidates':rawriprefs,
            'unalignedWholeFileAddressLiterals': literals,
            'linearDecodeGapByteCount': len(gaps), 'linearDecodeGapRvas': gaps,
            'limitations': ['Direct relative encoding scan is conservative and retains unaligned/data hits.',
                'RIP-relative candidates also scanned at every possible disp32 position and every feasible instruction start; Capstone instruction decoding is the acceptance oracle.',
                'Raw absolute/RVA literal scan does not prove absence of computed, encoded, external-module or runtime-created targets.',
                'No runtime indirect-target observation was performed.']}


def exception_info(pe, dis):
    entry = next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION
                 if e.struct.BeginAddress == 0x70b150)
    head = pe.get_data(entry.UnwindData, 4)
    tail = entry.UnwindData + 4 + ((head[2]+1)&~1)*2
    handler, fi = struct.unpack('<II', pe.get_data(tail, 8))
    fields = struct.unpack('<10I', pe.get_data(fi, 40))
    assert fields[0] == 0x19930522
    rows = []
    for n in range(fields[5]):
        pc, state = struct.unpack('<Ii', pe.get_data(fields[6]+8*n, 8))
        rows.append({'rva': hex(pc), 'state': state,
                     'entryRva': hex(fields[6]+8*n), 'insideReplacement': LO <= pc < HI})
    actions = []
    for n in range(fields[1]):
        target_state, action = struct.unpack('<iI', pe.get_data(fields[2]+8*n, 8))
        body = []
        for ins in dis.disasm(pe.get_data(action, 128), action):
            body.append(insrow(ins))
            if ins.mnemonic in ('ret', 'jmp'):
                break
        actions.append({'state': n, 'toState': target_state, 'actionRva': hex(action), 'instructions': body})
    assert rows == [
        {'rva':'0x70b150','state':-1,'entryRva':hex(fields[6]),'insideReplacement':False},
        {'rva':'0x70b17f','state':0,'entryRva':hex(fields[6]+8),'insideReplacement':True},
        {'rva':'0x70b1c0','state':-1,'entryRva':hex(fields[6]+16),'insideReplacement':False}]
    return {'functionRva':hex(entry.BeginAddress), 'endRva':hex(entry.EndAddress),
            'unwindRva':hex(entry.UnwindData), 'unwindHeaderAndCodes':pe.get_data(entry.UnwindData,tail-entry.UnwindData).hex(),
            'handlerRva':hex(handler), 'funcInfoRva':hex(fi), 'funcInfoBytes':pe.get_data(fi,40).hex(),
            'ipToState':rows, 'actions':actions,
            'interpretation':'State 0 cleanup at 0x9fe660 conditionally destroys frame+0x48 only when frame+0x20 bit0 is set. Prologue writes zero; stub never sets it. Success returns at 0x70b18d into state0 before original flag operations. Failure returns at 0x70b1d6 into state-1. Original callback and cleanup byte ranges remain unchanged.',
            'requirement':'New stub must unwind saved RBX and allocated 0x30 bytes for every PC. Its own resolver must not throw C++ exceptions or publish an unprotected pointer. A no-handler unwind description is proposed only while the cleanup flag is proven zero.'}


def unwind_test(stub):
    """Call the real Windows unwinder on synthetic stack/context/metadata only."""
    if os.name != 'nt':
        return {'passed':False, 'reason':'Requires Windows x64 RtlVirtualUnwind'}
    nt = ctypes.WinDLL('ntdll')
    fn = nt.RtlVirtualUnwind
    fn.argtypes = [ctypes.c_uint32, ctypes.c_uint64, ctypes.c_uint64,
                   ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
                   ctypes.POINTER(ctypes.c_uint64), ctypes.c_void_p]
    fn.restype = ctypes.c_void_p
    # Metadata-only borrowed frame: UWOP_ALLOC_SMALL 48, UWOP_PUSH_NONVOL RBX.
    # CodeOffset0 and SizeOfProlog0 mean both apply at every stub instruction.
    unwind = bytes.fromhex('0100020000520030')
    image = ctypes.create_string_buffer(4096)
    base = ctypes.addressof(image)
    ctypes.memmove(base+0x100, stub, len(stub))
    ctypes.memmove(base+0x200, unwind, len(unwind))
    rf = (ctypes.c_uint32*3)(0x100,0x100+len(stub),0x200)
    results = []
    # All byte PCs are conservative; includes return from resolver (offset5).
    for offset in range(len(stub)):
        stack = ctypes.create_string_buffer(256)
        low = ctypes.addressof(stack)
        frame = (low+31)&~15
        saved_rbx, saved_return = 0x123456789abcdef0, 0x1122334455667788
        ctypes.memmove(frame+0x30, struct.pack('<QQ',saved_rbx,saved_return),16)
        context_buffer = ctypes.create_string_buffer(1280)
        context = (ctypes.addressof(context_buffer)+15)&~15
        for off,value in [(144,0xeeeeeeee),(152,frame),(248,base+0x100+offset)]:
            ctypes.memmove(context+off, struct.pack('<Q',value),8)
        handler_data = ctypes.c_void_p()
        establisher = ctypes.c_uint64()
        result = fn(0,base,base+0x100+offset,ctypes.byref(rf),context,
                    ctypes.byref(handler_data),ctypes.byref(establisher),None)
        rbx,rsp,rip = [struct.unpack('<Q',ctypes.string_at(context+off,8))[0] for off in (144,152,248)]
        passed=(rbx,rsp,rip,result)==(saved_rbx,frame+0x40,saved_return,None)
        results.append({'stubOffset':offset,'restoredRbx':rbx==saved_rbx,
                        'restoredRsp':rsp==frame+0x40,'restoredCallerRip':rip==saved_return,
                        'passed':passed})
    return {'passed':all(r['passed'] for r in results),'cases':results,'proposedUnwindInfoHex':unwind.hex(),
            'runtimeFunctionRvas':{'begin':'stub','end':'stub+'+str(len(stub)),'unwind':'metadata'},
            'registrationPerformed':False,
            'limitations':['Tests explicit supplied RUNTIME_FUNCTION with RtlVirtualUnwind, not RtlAddFunctionTable registration/lifetime/concurrent removal.',
                           'Does not execute game/stub/helper code natively or dispatch C++/SEH exceptions.',
                           'Metadata must reside within one valid image-base-relative32 range with its registered function table.']}


def abi_replay(mod, original, patch, stub):
    rows=[]
    nonvol=[UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,
            UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15]
    for index in [-0x80000000,-1,0,2039,2040,4079,4080,70000,0x7fffffff]:
      for occupied in (False,True):
       for handle in (0,0x12345678):
        for registered in (False,True):
            uc=Uc(UC_ARCH_X86,UC_MODE_64)
            ptr=0x140000000
            for a,n in [(mod.ENTRY&~4095,4096),(mod.STUB,4096),(mod.STACK,0x10000),
                        (mod.GLOBAL&~4095,4096),(mod.TABLE,4096),(ptr,4096)]:uc.mem_map(a,n)
            wrapper=bytearray(original); wrapper[LO-0x70b150:HI-0x70b150]=patch
            uc.mem_write(mod.ENTRY,bytes(wrapper));uc.mem_write(mod.STUB,stub)
            for a in (mod.RESOLVER,mod.RETAIN,mod.RELEASE,mod.STOP):uc.mem_write(a,b'\xc3')
            uc.mem_write(mod.GLOBAL,struct.pack('<Q',mod.TABLE if registered else 0))
            uc.mem_write(mod.TABLE+0x30,struct.pack('<Q',mod.RETAIN))
            uc.mem_write(mod.TABLE+0x18,struct.pack('<Q',mod.RELEASE))
            uc.mem_write(ptr+0x18,struct.pack('<I',handle))
            callbacks=[]
            valid=occupied and index>=0
            def observe(emu,a,size,user):
                if a==mod.STOP:emu.emu_stop();return
                if a==mod.RESOLVER:
                    assert emu.reg_read(UC_X86_REG_RCX)==mod.MISSION
                    assert emu.reg_read(UC_X86_REG_EDX)==index&0xffffffff
                    sp=emu.reg_read(UC_X86_REG_RSP)
                    assert (sp+8)%16==0
                    # Exercise all four callee-owned home slots. The wrapper
                    # flag at caller RSP+20 must remain untouched.
                    emu.mem_write(sp+8,b'\xa5'*32)
                    emu.reg_write(UC_X86_REG_RAX,ptr if valid else 0)
                if a in (mod.RETAIN,mod.RELEASE):
                    callbacks.append((a,emu.reg_read(UC_X86_REG_ECX)))
                    sp=emu.reg_read(UC_X86_REG_RSP);assert (sp+8)%16==0
                    emu.mem_write(sp+8,b'\x5a'*32)
                    emu.reg_write(UC_X86_REG_RAX,0xaaaaaaaaaaaaaaaa)
                if a in (mod.RESOLVER,mod.RETAIN,mod.RELEASE):
                    for r in [UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R10,UC_X86_REG_R11]:emu.reg_write(r,0xeeeeeeeeeeeeeeee)
                    for r in range(UC_X86_REG_XMM0,UC_X86_REG_XMM5+1):emu.reg_write(r,(1<<128)-1)
            uc.hook_add(UC_HOOK_CODE,observe)
            rsp=mod.STACK+0x8008
            uc.mem_write(rsp,struct.pack('<Q',mod.STOP));uc.reg_write(UC_X86_REG_RSP,rsp)
            uc.reg_write(UC_X86_REG_RCX,mod.MISSION);uc.reg_write(UC_X86_REG_EDX,index&0xffffffff)
            for n,r in enumerate(nonvol):uc.reg_write(r,0x123456789abcdef0+n)
            for r in range(UC_X86_REG_XMM6,UC_X86_REG_XMM15+1):uc.reg_write(r,0x112233445566778899aabbccddeeff00+r)
            uc.emu_start(mod.ENTRY,mod.STOP+1,count=1000)
            expected=handle if valid else 0
            assert uc.reg_read(UC_X86_REG_RIP)==mod.STOP
            assert uc.reg_read(UC_X86_REG_EAX)==expected
            assert uc.reg_read(UC_X86_REG_RSP)==rsp+8
            assert all(uc.reg_read(r)==0x123456789abcdef0+n for n,r in enumerate(nonvol))
            assert all(uc.reg_read(r)==0x112233445566778899aabbccddeeff00+r for r in range(UC_X86_REG_XMM6,UC_X86_REG_XMM15+1))
            assert callbacks==([(mod.RETAIN,expected),(mod.RELEASE,expected)] if expected and registered else [])
            rows.append({'index':index,'occupied':occupied,'sourceHandle':handle,'registered':registered,'expectedHandle':expected,'passed':True})
    return {'passed':True,'caseCount':len(rows),'cases':rows,
            'checks':['No original Mission/table mapped','Signed INT_MIN, negative and INT_MAX inputs',
                      'Zero/nonzero managed handles','All GPR and XMM nonvolatile registers preserved',
                      'Volatile GPR/XMM clobbers in resolver and callbacks',
                      'All32 bytes shadow space overwritten by callees','Call alignment, final RSP, retain/release pair'],
            'limitations':['Resolver remains a model; no real storage lookup, stale generation, mission association or job barrier is validated.']}


EXTRA_CPP = r'''
#include "retained_storage_tests.inc"
struct ThrowAllocation {
  static void* allocate(void* context, std::size_t bytes) {
    auto& f = *static_cast<Fixture*>(context);
    if(f.allocation_calls == f.fail_allocation) throw std::bad_alloc();
    return Fixture::allocate(context, bytes);
  }
};
int main() {
  if (retained_test_main()) return 1;
  try {
    Fixture f;
    {
      StableAgentStorage s(3, small, f.callbacks()); s.grow(3);
      auto h0=s.acquire(), h1=s.acquire(); require(s.release(h0), "release first");
      s.grow(7); // grow with both a recycled and a never-used old slot
      for(auto i : {3ULL,4ULL,5ULL,6ULL,7ULL,8ULL,0ULL,2ULL})
        require(s.acquire().index==i,"old free chain lost or duplicated after growth");
      require(s.acquire().index==no_slot,"hole growth exhaustion");
      { auto a=s.read(); auto b=std::move(a); require(b.valid(h1),"move lease lost ownership");
        bool caught=false; try{b.get(h1,5);}catch(const std::out_of_range&){caught=true;}
        require(caught,"invalid companion pool accepted"); }
      require(!s.release({no_slot,1}) && !s.release({1,999}),"foreign generation/sentinel accepted");
      require(s.try_close()&&s.try_close(),"close not idempotent");
      require(!s.release(h1),"release after close");
      {auto lease=s.read();require(!lease.valid(h1)&&lease.capacity()==0&&lease.live()==0,"closed lease");}
      for(int operation=0;operation<2;++operation){bool caught=false;
        try{if(operation==0)s.grow(1);else s.acquire();}catch(const std::logic_error&){caught=true;}
        require(caught,"mutation after close accepted");}
    } f.clean();
    for(std::size_t failure=0;failure<10;++failure){
      Fixture g; auto cb=g.callbacks(); cb.allocate=ThrowAllocation::allocate;
      {StableAgentStorage s(3,small,cb);s.grow(3);auto h=s.acquire();
       g.fail_allocation=g.allocation_calls+failure;
       bool caught=false;try{s.grow(9);}catch(const std::bad_alloc&){caught=true;}
       require(caught,"throw allocation not reached");
       {auto lease=s.read(); require(lease.capacity()==3&&lease.live()==1&&lease.valid(h),"throw allocation published state");}
       require(g.allocations.size()==5&&g.constructed.size()==15&&g.reverse_ok,"throw allocation cleanup");
      }g.clean();
    }
    std::cout<<"PASS independent additions: growth preserves old holes; moved lease; invalid pool/handle; closed mutations; 10 throwing allocator points\n";
  }catch(const std::exception& e){std::cerr<<"FAIL independent: "<<e.what()<<'\n';return 1;}
  return 0;
}
'''


def storage_tests():
    BUILD.mkdir(parents=True,exist_ok=True)
    retained=(ROOT/'tests/AgentStorage/stable_storage_tests.cpp').read_text()
    retained=retained.replace('"../../src/MNBL.AgentStorage/StableAgentStorage.hpp"',
                              '"'+(ROOT/'src/MNBL.AgentStorage/StableAgentStorage.hpp').as_posix()+'"')
    retained=retained.replace('int main() {','int retained_test_main() {')
    # main's implicit return0 must become explicit when it is renamed.
    closing=retained.rfind('}')
    retained=retained[:closing]+'    return 0;\n'+retained[closing:]
    (BUILD/'retained_storage_tests.inc').write_text(retained)
    cpp=BUILD/'independent_storage_tests.cpp';cpp.write_text(EXTRA_CPP)
    cmd=BUILD/'run.cmd'
    cmd.write_text('@echo off\ncall "C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat" >nul\n'
        'if errorlevel 1 exit /b %errorlevel%\n'
        'cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 /I "'+str(ROOT)+'" "'+str(cpp)+'" /Fo:"'+str(BUILD/'test.obj')+'" /Fe:"'+str(BUILD/'test.exe')+'"\n'
        'if errorlevel 1 exit /b %errorlevel%\n"'+str(BUILD/'test.exe')+'"\nexit /b %errorlevel%\n')
    run=subprocess.run(['cmd.exe','/c',str(cmd)],cwd=ROOT,capture_output=True,text=True,timeout=120)
    assert run.returncode==0,run.stdout+run.stderr
    return {'passed':True,'exitCode':run.returncode,'stdout':run.stdout,'stderr':run.stderr,
            'generatedFixtureSha256':digest(cpp),'generatedCommandSha256':digest(cmd),
            'generatedRetainedTestsSha256':digest(BUILD/'retained_storage_tests.inc'),
            'retainedTestAdaptation':'Generated copy only: absolute header path, renamed main, explicit successful return0 because renamed non-main functions do not have main implicit return.',
            'testExecutableSha256':digest(BUILD/'test.exe'),
            'review':'No intrinsic defect found in bounded storage mechanics. Growth preserves stable payloads and old free chain, commits only after all five pools construct, rollback destroys completed prefix in reverse order, shared leases retain State and block mutation.',
            'contractLimits':['Handles are storage-instance-local: identical index/generation from another instance is not rejected; integration must bind Mission to the correct State.',
                            'Acquire/release change occupancy and generation only; they do not reset or reconstruct engine entities.',
                            'Callbacks must supply proper alignment, lifetime, nonreentrancy and constructor semantics; Header fixtures are not game objects.',
                            'Raw pointers are protected only while the lease/barrier is held; engine workers do not participate automatically.',
                            'Generation saturation is source-reviewed, not tested through UINT64_MAX reuse cycles.',
                            'The 30ms blocked-growth test has a scheduling false-pass possibility if the worker is descheduled after announcing entry; lock behavior is also source-reviewed.']}


def main():
    mod=candidate();data=mod.SOURCE.read_bytes();assert hashlib.sha256(data).hexdigest()==mod.EXPECTED
    pe=pefile.PE(data=data)
    dis=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);dis.detail=True
    replay_path=ROOT/'evidence/engine-extension/native-segmented-lookup-replay.json'
    replay=json.loads(replay_path.read_text())
    assert replay['toolSha256']==digest(ROOT/'tools/battle-scale/replay_segmented_agent_lookup.py')
    assert replay['binarySha256']==mod.EXPECTED
    original=pe.get_data(0x70b150,0x90)
    assert original[LO-0x70b150:HI-0x70b150].hex()==replay['originalBytes']
    patch=bytes.fromhex(replay['patchBytes']);stub=bytes.fromhex(replay['stubBytes'])
    assert len(patch)==40 and len(stub)==41
    assert patch==mod.branch(0xe9,mod.START,mod.STUB)+b'\x90'*35
    historical_stub=(mod.branch(0xe8,mod.STUB,mod.RESOLVER)+bytes.fromhex('4885c074088b5818')+
                     mod.branch(0xe9,mod.STUB+13,mod.END)+bytes.fromhex('33c0')+
                     mod.branch(0xe9,mod.STUB+20,mod.BASE+0x70b1d6))
    assert historical_stub.hex()=='e8fb0000004885c074088b5818e97bb170ff33c0e9bdb170ff'
    refs=references(pe,data,dis)
    eh=exception_info(pe,dis)
    uw_original=unwind_test(historical_stub)
    # Windows identifies the original rel32 return jumps as epilogues even
    # though the wrapper frame is still present. A register jump (ModRM mod3)
    # is not an eligible x64 epilogue jump. R11 is volatile at this boundary.
    def jump_r11(target):return bytes.fromhex('49bb')+struct.pack('<Q',target)+bytes.fromhex('41ffe3')
    alternative=(mod.branch(0xe8,mod.STUB,mod.RESOLVER)+bytes.fromhex('4885c074108b5818')+
                 jump_r11(mod.END)+bytes.fromhex('33c0')+jump_r11(mod.BASE+0x70b1d6))
    assert len(alternative)==41
    assert alternative==stub
    assert replay['unwindInfoHex']=='0100020000520030'
    uw=unwind_test(alternative)
    assert uw['passed'],uw
    abi=abi_replay(mod,original,patch,stub)
    historical_abi=abi_replay(mod,original,patch,historical_stub)
    st=storage_tests()
    paths=['tools/battle-scale/replay_segmented_agent_lookup.py',
           'evidence/engine-extension/native-segmented-lookup-replay.json',
           'src/MNBL.AgentStorage/StableAgentStorage.hpp',
           'tests/AgentStorage/stable_storage_tests.cpp',
           'tools/battle-scale/Test-StableAgentStorage.cmd',
           'tools/battle-scale/audit_segmented_lookup_hook.py']
    out={'schemaVersion':1,'status':'bounded-mechanical-candidate-verified-not-installable',
         'createdAtUtc':datetime.now(timezone.utc).isoformat(),
         'binary':{'path':str(mod.SOURCE),'sha256':mod.EXPECTED},
         'reviewedSourceSha256':{p:digest(ROOT/p) for p in paths},
         'scope':{'replacementSpanRva':[hex(LO),hex(HI)],'replacementLength':40,
                  'patchApproved':False,'gameLaunched':False,'installedFilesAccessed':False},
         'originalInstructions':[insrow(i) for i in dis.disasm(original,mod.ENTRY)],
         'patchBytes':patch.hex(),'stubBytes':stub.hex(),
         'stubInstructions':[insrow(i) for i in dis.disasm(stub,mod.STUB)],
         'referenceScan':refs,'exceptionMetadata':eh,
         'historicalDirectJumpCandidate':{'stubBytes':historical_stub.hex(),
             'priorSourceSha256':'13c9fa914cbf26548c85af120e97a768ae969e48db33de30ad30ab564a75a1da',
             'withProposedMetadataUnwindTest':uw_original,'abiReplay':historical_abi},
         'proposedMechanicalFix':{'stubBytes':alternative.hex(),
             'stubInstructions':[insrow(i) for i in dis.disasm(alternative,mod.STUB)],
             'unwindInfoHex':uw['proposedUnwindInfoHex'],
             'rootCandidateModifiedByReviewer':False,'integratedInCurrentRootCandidate':True,
             'description':'Root adopted both direct return JMP rel32 replacements with MOV R11,absolute continuation; JMP R11, and JZ displacement16. R11 is volatile and no subsequent continuation consumes its prior value. Requires two64-bit continuation address fixups plus rel32 resolver call fixup.'},
         'syntheticWindowsUnwindTest':uw,
         'independentAbiReplay':abi,'storageReviewAndTests':st,
         'findings':[
            {'id':'HOOK-UNWIND-01','severity':'blocking-for-installation','status':'code-fix-integrated-and-verified-registration-lifetime-open',
             'description':'Historical25-byte root stub used direct return jumps which Windows misrecognized as epilogues even with metadata: incorrect caller RIP/RSP/RBX at offsets13 and20. Root adopted the independently reviewed41-byte R11-return-jump fix; this receipt now binds that current source. Runtime unwind table registration and lifetime are not yet implemented.',
             'fix':'Register a dedicated RUNTIME_FUNCTION with UNWIND_INFO0100020000520030 for current41-byte stub. Keep table/code/helper alive until no executing readers or unwinds remain. Real Windows RtlVirtualUnwind recovered caller RIP,RSP,RBX at all41 byte PCs.'},
            {'id':'HOOK-RESOLVER-02','severity':'blocking-for-installation','status':'open-integration',
             'description':'Candidate calls an emulator resolver model, not a real Mission-associated storage backend. Signed range, occupancy/generation, current Mission association, stale native IDs and reader/job-barrier lifetime remain required. Storage handles alone do not supply those contracts.'},
            {'id':'HOOK-REFERENCES-03','severity':'scope-limitation','status':'bounded-static-scan-only',
             'description':'Every executable byte was scanned for supported direct relative branch encodings, every feasible RIP-disp32/start combination was checked with Capstone, and every raw file offset for span VA/RVA literals. Computed/encoded/foreign-module indirect entry cannot be excluded statically by this receipt.'}],
         'integrationRequirements':['Implement and independently review real resolver with exact ABI and lifetime barrier.',
              'Register unwind metadata and test registration/unregistration and concurrent lifetime. Confirm actual process control-flow and dynamic-code policies permit the generated register jumps and code allocation.',
              'Entry JMP and resolver CALL are rel32: allocate the stub within signed2GiB of entry and resolver, or use a separately reviewed near relay/absolute helper transfer. A future resolver DLL address is not guaranteed reachable. Current branch encoder fails closed with struct.pack overflow.',
              'Preserve full40-byte expected-byte/hash gate and unchanged original success/failure destinations, callback code and EH tables.',
              'Link all other indexed Agent consumers, pointer-stride loops, companion metadata and narrow identifiers before dynamic live growth; one wrapper does not close the prior access inventory.']}
    OUT.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'output':str(OUT),'status':out['status'],
                      'directCandidates':len(refs['directRelativeCandidates']),
                      'ripReferences':len(refs['decodedRipRelativeReferences']),
                      'literalReferences':len(refs['unalignedWholeFileAddressLiterals']),
                      'abiCases':abi['caseCount'],'unwindCases':len(uw.get('cases',[])),
                      'storagePassed':st['passed'],'sha256':digest(OUT)}))


if __name__=='__main__':main()
