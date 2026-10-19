"""Independent offline machine/entry-boundary review of two ranged page guards."""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from unicorn import Uc, UcError, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_ERR_INSN_INVALID
from unicorn import x86_const as x
from patch_native_copy import safe_review_output

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
PROPOSAL = ROOT / 'evidence/engine-extension/native-ranged-stall-review.json'
OUTPUT = ROOT / 'evidence/engine-extension/native-ranged-guard-independent-review.json'
EXPECTED = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE = 0x180000000
REGS = {n: getattr(x, 'UC_X86_REG_' + n.upper()) for n in
        ('rax','rbx','rcx','rdx','rsi','rdi','rbp','rsp','r8','r9','r10','r11','r12','r13','r14','r15')}


def execute(code, rva, state, end):
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map((BASE+rva) & ~0xfff, 0x2000)
    cpu.mem_write(BASE+rva, code)
    for name, value in state.items(): cpu.reg_write(REGS[name], value)
    writes = []
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda uc,a,addr,size,value,user: writes.append((addr,size,value)))
    def stop(uc,pc,size,user):
        if pc == BASE+end: uc.emu_stop()
    cpu.hook_add(UC_HOOK_CODE, stop)
    trapped = False
    try: cpu.emu_start(BASE+rva, BASE+end, count=40)
    except UcError as error:
        if error.errno != UC_ERR_INSN_INVALID: raise
        trapped = True
    assert not writes, writes
    pc = cpu.reg_read(x.UC_X86_REG_RIP)
    assert trapped or pc == BASE+end, hex(pc)
    return trapped, pc, {n: cpu.reg_read(reg) for n,reg in REGS.items()}


def initial(first=0,last=0):
    state = {n:0x123400000000 + i*0x100 for i,n in enumerate(REGS)}
    state.update(rax=first,rsi=first,rbp=last,rdi=0x1234567800,rcx=0x2234567800)
    return state


def main():
    raw = SOURCE.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == EXPECTED
    proposal = json.loads(PROPOSAL.read_text())
    assert proposal['nativeSourceSha256'] == EXPECTED
    pe = pefile.PE(data=raw)
    dec = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    edits = {int(e['rva'],16):e for e in proposal['proposedEdits']}
    for rva,e in edits.items():
        old,new = bytes.fromhex(e['originalBytes']),bytes.fromhex(e['replacementBytes'])
        assert pe.get_data(rva,len(old)) == old and len(old) == len(new)
    assert edits[0x23f610]['replacementBytes'] == '4903ec'
    assert edits[0x89a06]['replacementBytes'] == '4903ef'
    assert edits[0x89a0f]['replacementBytes'] == '4883fd7f76020f0b90'
    def patched(start,end):
        code = bytearray(pe.get_data(start,end-start))
        for rva,e in edits.items():
            b=bytes.fromhex(e['replacementBytes'])
            if start <= rva < end:
                assert rva+len(b) <= end
                code[rva-start:rva-start+len(b)] = b
        return bytes(code)
    guard_specs = [(0x23f617,0x23f640),(0x89a0f,0x89a18)]
    results=[]
    for start,end in guard_specs:
        old,new = pe.get_data(start,end-start),patched(start,end)
        valid=0
        for first in range(128):
            for last in range(first,128):
                state=initial(first,last)
                assert execute(old,start,state,end) == execute(new,start,state,end)
                valid+=1
        invalid=[]
        for first,last in [(0,128),(127,128),(128,128),(0,255),(0,0x1fffff)]:
            out=execute(new,start,initial(first,last),end)
            assert out[0]
            invalid.append({'first':first,'last':last,'trapRva':hex(out[1]-BASE)})
        if start == 0x23f617:
            for first,last in [(1,0),(127,126),(0xffffffff,127)]:
                out=execute(new,start,initial(first,last),end)
                assert out[0]
                invalid.append({'first':first,'last':last,'trapRva':hex(out[1]-BASE)})
        results.append({'start':hex(start),'end':hex(end),'validPagePairsAllRegistersEqual':valid,
                        'invalidCases':invalid,'replacementInstructions':[{'rva':hex(i.address-BASE),'bytes':i.bytes.hex(),'asm':i.mnemonic+' '+i.op_str} for i in dec.disasm(new,BASE+start)]})
    # Start after the atomic reservation, with its exact uint32 outputs. This
    # checks inclusive-end arithmetic independently of the page-only tests.
    arithmetic=[]
    for start,end,owner in [(0x23f605,0x23f640,'r12'),(0x899fd,0x89a18,'r15')]:
        for count in (1,2,16383,16384,16385,2097152,0x80000000,0xffffffff):
            for old in (0,1,16383,16384,2097151,2097152,0x7fffffff,0xfffffffe,0xffffffff):
                state=initial();state[owner]=old;state['rbx']=count;state['rdx']=count
                out=execute(patched(start,end),start,state,end)
                valid=old+count <= 2097152
                assert out[0] == (not valid), (hex(start),old,count,out)
                if valid:
                    assert out[2]['rax'] == old//16384 and out[2]['rsi'] == old//16384
                    assert out[2]['rbp'] == ((old+count-1)//16384 - old//16384 +1 if owner=='r12' else (old+count-1)//16384)
                arithmetic.append({'allocator':hex(start),'oldCount':old,'requested':count,'accepted':valid})
    # Exhaustive raw direct-branch encoding scan does not depend on linear
    # disassembly. Any candidate must then be classified against exact bodies.
    spans=[(0x23f610,0x23f640),(0x89a06,0x89a18)]
    candidates=[]
    for sec in pe.sections:
        if not sec.Characteristics & 0x20000000: continue
        b=sec.get_data();rv=sec.VirtualAddress
        for n,op in enumerate(b):
            size=0;disp=0
            if op in (0xe8,0xe9) and n+5<=len(b):size=5;disp=struct.unpack_from('<i',b,n+1)[0]
            elif op==0x0f and n+6<=len(b) and 0x80<=b[n+1]<=0x8f:size=6;disp=struct.unpack_from('<i',b,n+2)[0]
            elif (op==0xeb or 0x70<=op<=0x7f or 0xe0<=op<=0xe3) and n+2<=len(b):size=2;disp=struct.unpack_from('<b',b,n+1)[0]
            target=rv+n+size+disp
            if size and any(a<=target<z for a,z in spans):
                candidates.append({'sourceRva':hex(rv+n),'bytes':b[n:n+size].hex(),'targetRva':hex(target)})
    # Exact containing instructions prove these three apparent branch opcodes
    # are operand bytes, rather than independently reachable instructions.
    false_proofs=[
      {'sourceRva':'0x899ed','bytes':'7424','targetRva':'0x89a13','instructionRva':'0x899eb','instructionBytes':'4889742450'},
      {'sourceRva':'0x89a14','bytes':'e200','targetRva':'0x89a16','instructionRva':'0x89a12','instructionBytes':'0f87e2000000'},
      {'sourceRva':'0x23f62f','bytes':'7f08','targetRva':'0x23f639','instructionRva':'0x23f62d','instructionBytes':'4c8d7f08'}]
    assert candidates == [{k:v for k,v in f.items() if not k.startswith('instruction')} for f in false_proofs], candidates
    for f in false_proofs:
        code=bytes.fromhex(f['instructionBytes'])
        at=int(f['instructionRva'],16)
        assert pe.get_data(at,len(code)) == code
        entry=next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=at<e.struct.EndAddress)
        body=pe.get_data(entry.BeginAddress,entry.EndAddress-entry.BeginAddress)
        decoded=list(dec.disasm(body,BASE+entry.BeginAddress))
        assert sum(i.size for i in decoded)==len(body)
        anchored=next(i for i in decoded if i.address==BASE+at)
        assert bytes(anchored.bytes)==code
        f['runtimeFragmentStart']=hex(entry.BeginAddress)
    literal_hits=[]
    for a,z in spans:
        for value in range(a,z):
            for width,bias in [(4,0),(8,BASE)]:
                needle=(value+bias).to_bytes(width,'little');at=raw.find(needle)
                while at>=0:
                    literal_hits.append({'fileOffset':at,'width':width,'targetRva':hex(value)})
                    at=raw.find(needle,at+1)
    assert not literal_hits,literal_hits
    unwind=[]
    for start,end in guard_specs:
        e=next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress<=start<e.struct.EndAddress)
        unwind.append({'guard':hex(start),'fragmentStart':hex(e.BeginAddress),'fragmentEnd':hex(e.EndAddress),'unwindRva':hex(e.UnwindData)})
    # Conditional nonvolatile saves remain at their exact original offsets.
    for rva,code in [(0x89a18,'48895c2440'),(0x89a20,'4c89642458'),(0x89a2c,'4c89742428')]:
        assert pe.get_data(rva,len(bytes.fromhex(code))).hex()==code
        assert not any(p<rva+len(bytes.fromhex(code)) and rva<p+len(bytes.fromhex(e['replacementBytes'])) for p,e in edits.items())
    for start,end in [(0x23f50a,0x23f53a),(0x899d9,0x899e6)]:
        assert patched(start,end)==pe.get_data(start,end-start)
    result={'schemaVersion':1,'status':'independent-guard-review-passed','nativeSourceSha256':EXPECTED,
      'proposalSha256':hashlib.sha256(PROPOSAL.read_bytes()).hexdigest(),'scriptSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
      'gameExecuted':False,'nativeFileWritten':False,'guards':results,'inclusiveEndArithmeticCases':arithmetic,
      'rawDirectBranchCandidates':candidates,'candidateClassification':'Three candidates are operand bytes in exact original MOV/JA/LEA instructions; no actual incoming direct target found.',
      'falseCandidateInstructionProofs':false_proofs,
      'absoluteVa64AndRva32LiteralHits':literal_hits,'unwindFragments':unwind,
      'conclusions':['All8256 valid first/last page pairs preserve every general register and destination for each guard.',
       '144 upstream unsigned-count cases accept only oldCount+request<=2097152; 64-bit inclusive-end arithmetic prevents32-bit wrap.',
       'Both guards perform no memory writes before their continuation; invalid cases trap withUD2 before any pointer/flag indexing.',
       'Secondary allocator nonzero-count entry makes first<=last mathematically guaranteed after wide addition. Original zero-count early return remains outside edits.',
       'Existing XADD increments count before guard; rejection is process failfast, not recoverable exhaustion.',
       'No stack/save instruction position changes, no actual incoming direct branch, and noVA64/RVA32 materialized interior address found.'],
      'limitations':['No claim of arbitrary dynamically computed indirect-target completeness.','This receipt reviews guards and their arithmetic/control-flow, not every payload-size consumer or asynchronous GPU lifetime.','No game execution or patched-file deployment performed.']}
    safe_review_output(OUTPUT,[SOURCE,PROPOSAL,Path(__file__)])
    OUTPUT.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'output':str(OUTPUT),'validPairs':sum(r['validPagePairsAllRegistersEqual'] for r in results),'arithmeticCases':len(arithmetic)}))


if __name__ == '__main__': main()
