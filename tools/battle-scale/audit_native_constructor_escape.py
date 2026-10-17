"""Bounded Mission constructor escape-route audit, never hook approval.

Raw PE ranges, complete retained REA responses and exact call-site context are
kept together. Local helper closure does not certify external CRT/resource code.
"""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn import x86_const as ux
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output

SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
EXPECTED='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BOUNDARY=ROOT/'evidence/engine-extension/native-empty-adoption-boundary.json'
DEFAULT=ROOT/'evidence/engine-extension/native-unpublished-element-runtime-contract.json'
BUNDLE=ROOT/'evidence/engine-extension/native-live-growth-rea-evidence-5.json'
OUT=ROOT/'evidence/engine-extension/native-constructor-escape-audit.json'


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    reset_path=ROOT/'build/battle-scale/native-queue/044.response.json'
    protected=[SOURCE,BOUNDARY,DEFAULT,Path(__file__),reset_path,BUNDLE]
    pins={str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in protected}
    raw=SOURCE.read_bytes();assert hashlib.sha256(raw).hexdigest()==EXPECTED
    pe=pefile.PE(data=raw);base=pe.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    entries={e.struct.BeginAddress:e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION}
    def chain_root(e):
        seen=set()
        while True:
            assert e.UnwindData not in seen;seen.add(e.UnwindData)
            header=pe.get_data(e.UnwindData,4)
            if not (header[0]>>3)&4:return e.BeginAddress
            trailer=e.UnwindData+4+((header[2]+1)//2)*4
            begin,end,unwind=struct.unpack('<III',pe.get_data(trailer,12))
            e=entries[begin];assert e.EndAddress==end and e.UnwindData==unwind
    def body(start,leaf_end=None):
        ranges=[(start,leaf_end)] if leaf_end else sorted((e.BeginAddress,e.EndAddress) for e in entries.values() if chain_root(e)==start)
        assert ranges,hex(start)
        instructions=[]
        for first,last in ranges:
            code=pe.get_data(first,last-first);ins=list(md.disasm(code,base+first))
            assert sum(i.size for i in ins)==len(code),(hex(first),hex(last))
            instructions+=ins
        return ranges,instructions
    selected={0x5ce3f0:None,0x5cf3c0:None,0x5b5d40:None,0x5ccb90:None,
              0x5d78f0:None,0x09f840:None,0x5b64e0:None,0x5cec60:None,
              0x5e5270:None,0x5e5380:None,0x5b82b0:None,0x05e2f0:0x05e2fc,
              0x5dbcb0:None,0x05e330:None,0x698980:None,0x08a420:None}
    decoded={start:body(start,end) for start,end in selected.items()}
    boundary=json.loads(BOUNDARY.read_text())
    for start in (0x5ce3f0,0x5cf3c0):
        for row in boundary['bodies'][hex(start)]['instructions']:
            code=bytes.fromhex(row['bytes']);assert pe.get_data(int(row['address'],16)-base,len(code))==code
    assert sha(BUNDLE)=='e800c06b3c058af9aa6b51920911a480baae0481093b2286bd46e001829ddd4e'
    wanted={hex(base+x)for x in (0x5ce3f0,0x5b5d40,0x5ccb90,0x5d78f0,0x09f840,0x5b64e0,0x5cec60)}
    evidence=[item['evidence'] for item in json.loads(BUNDLE.read_text())['records'] if item['evidence'].get('operation')=='procedure_pseudo_code' and item['evidence'].get('parameters',{}).get('procedure') in wanted]
    assert len(evidence)==7
    evidence.append(json.loads(reset_path.read_text()))
    for record in evidence:
        assert record['subject']['digest']['sha256']==EXPECTED
        assert record['operation']=='procedure_pseudo_code'
    def rows(instructions):
        return [{'address':hex(i.address),'bytes':i.bytes.hex(),'instruction':i.mnemonic+' '+i.op_str} for i in instructions]
    imports={i.address:(d.dll.decode(),i.name.decode() if i.name else str(i.ordinal)) for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports}
    memset_thunk=list(md.disasm(pe.get_data(0x9e3195,6),base+0x9e3195))
    assert len(memset_thunk)==1 and memset_thunk[0].mnemonic=='jmp'
    memset_slot=memset_thunk[0].address+memset_thunk[0].size+memset_thunk[0].operands[0].mem.disp
    assert imports[memset_slot][1]=='memset'
    calls=[]
    for owner,(_,instructions) in decoded.items():
        for n,i in enumerate(instructions):
            if i.mnemonic!='call':continue
            row={'ownerRva':hex(owner),'rva':hex(i.address-base),'bytes':i.bytes.hex(),'target':i.op_str,
                 'precedingContext':rows(instructions[max(0,n-12):n]),'classification':'unresolved-transitive-call'}
            if i.operands[0].type==capstone.x86.X86_OP_MEM and i.operands[0].mem.base==capstone.x86.X86_REG_RIP:
                address=i.address+i.size+i.operands[0].mem.disp
                row['import']=imports.get(address);row['iatRva']=hex(address-base)
            calls.append(row)
    by_call={int(r['rva'],16):r for r in calls}
    def annotate(site,kind,note):
        by_call[site]['classification']=kind;by_call[site]['review']=note
    annotate(0x5ce448,'embedded-renderer-constructor','RCX=Mission+20; renderer initializer includes resource manager calls, owned references and CRT mutex initialization. Child contracts below are partial.')
    annotate(0x5ceab0,'whole-Mission-reset','RCX=RBX=Mission; complete reset includes unconditional subobject calls and global cleanup. Empty pools alone do not close these effects.')
    annotate(0x5cf50a,'conditional-empty-path-closed','RCX=R15=Mission, DL=1. In 5ccb90, highwater0, equal empty vector bounds and Scene=null skip every nested call; this remains conditional on those fields not being changed by earlier callbacks.')
    annotate(0x5cf4e3,'embedded-container-reset','RCX=Mission+1044e70. 5d78f0 unconditionally invokes 08a420 on subobject+8; allocations/element loops are skipped only when fresh count+4=0.')
    for site in (0x5ce470,0x5ce47d,0x5ce493,0x5ce4f1,0x5ce4fe,0x5ce883,0x5ce8a6):
        annotate(site,'default-pool-or-element-construction','Original inactive element constructors have a separately reviewed closed default lifetime. That evidence does not certify the surrounding Mission or later activation.')
    for site in (0x5ce5c1,0x5ce5d5,0x5ced4c,0x5ced67,0x5cee2b,0x5cee46):
        annotate(site,'external-CRT-subobject-pointer','Embedded mutex/condition-variable address is passed to imported CRT code. Exact provider write/retention/exception contract is not established here.')
    annotate(0x5ce7fc,'bounded-memory-initialization','5e5380 performs scalar writes plus three memset calls; exact external memset implementation remains outside this audit.')
    annotate(0x5ce86b,'local-leaf-initialization','5e5270 has no calls; initializes local vector headers and two 255-entry pointer/ready arrays. No external destination is apparent in retained instructions.')
    annotate(0x5ce910,'embedded-queue-constructor','Mission+11843e0 passed to 5cec60; initializes two subobjects, allocates owned backing arrays, invokes imported condition/mutex initialization. No direct thread-start call appears in this body.')
    # Exact narrow argument-death proof: incoming RCX is not read anywhere before
    # its first overwrite in 09f840. This addresses only that apparent argument.
    ignored=[]
    for i in decoded[0x09f840][1]:
        reads,writes=i.regs_access()
        assert capstone.x86.X86_REG_RCX not in reads and capstone.x86.X86_REG_ECX not in reads
        ignored.append(i)
        if capstone.x86.X86_REG_RCX in writes or capstone.x86.X86_REG_ECX in writes:break
    assert ignored[-1].address==base+0x09f8d3
    # The alternate string-null branch goes straight to09f8df then overwrites
    # RCX with global manager at09f8ef, also without a use of incoming RCX.
    alternate=[i for i in decoded[0x09f840][1] if base+0x09f8df<=i.address<=base+0x09f8ef]
    for i in alternate:
        assert capstone.x86.X86_REG_RCX not in i.regs_access()[0]
    assert alternate[-1].bytes.hex()=='488bcf'
    # 5b64e0 has no calls and all memory destinations use unchanged RCX.
    leaf=decoded[0x5b64e0][1];leaf_writes=[]
    assert not any(i.mnemonic=='call' for i in leaf)
    for i in leaf:
        assert capstone.x86.X86_REG_RCX not in i.regs_access()[1]
        for n,op in enumerate(i.operands):
            if op.type==capstone.x86.X86_OP_MEM and memory_operand_writes(i,n):
                if op.mem.base==capstone.x86.X86_REG_RSP:
                    assert i.address in (base+0x5b64ed,base+0x5b64f2) and op.mem.disp in (0,16) and op.size==16
                    continue
                assert op.mem.base==capstone.x86.X86_REG_RCX and op.mem.index==0
                assert 0x88<=op.mem.disp and op.mem.disp+op.size<=0xac
                leaf_writes.append(i)
    for owner in (0x5b82b0,0x5e5270,0x05e2f0):
        assert not any(i.mnemonic=='call' for i in decoded[owner][1])
    # Execute only the conditionally empty paths, without substituting any call.
    # An unexpected nested call fails instead of being modeled as harmless.
    empty_replays=[]
    for target,size in ((0x5ccb90,0x1185000),(0x5d78f0,0x1000),(0x08a420,0x1000)):
        for poison,begin in ((0x35,0),(0xcd,0x71234560)):
            cpu=Uc(UC_ARCH_X86,UC_MODE_64);mapped=set();allowed={}
            included=(target,0x08a420) if target==0x5d78f0 else (target,)
            for selected_target in included:
                ranges,instructions=decoded[selected_target]
                for a,b in ranges:
                    for page in range((base+a)&~4095,(base+b+4095)&~4095,4096):
                        if page not in mapped:mapped.add(page);cpu.mem_map(page,4096);cpu.mem_write(page,b'\xcc'*4096)
                    cpu.mem_write(base+a,pe.get_data(a,b-a))
                allowed.update({i.address:i for i in instructions})
            obj,stack,stop=0x60000000,0x50000000,0x190000000
            cpu.mem_map(obj,size);cpu.mem_map(stack,0x10000);cpu.mem_map(stop,4096)
            before=bytearray([poison])*size
            if target==0x5ccb90:
                struct.pack_into('<i',before,0x62da44,0)
                struct.pack_into('<QQ',before,0x1044e50,begin,begin)
                struct.pack_into('<Q',before,0x18,0)
                expected=bytearray(before);expected[0x10438f0]=1
            elif target==0x5d78f0:
                struct.pack_into('<i',before,4,0);struct.pack_into('<QQ',before,8,begin,begin)
                expected=bytearray(before);struct.pack_into('<i',expected,0,0);struct.pack_into('<i',expected,0x48,0)
            else:
                struct.pack_into('<QQ',before,0,begin,begin);expected=bytearray(before)
            cpu.mem_write(obj,bytes(before));rsp=stack+0x8008;cpu.mem_write(rsp,struct.pack('<Q',stop))
            cpu.reg_write(ux.UC_X86_REG_RSP,rsp);cpu.reg_write(ux.UC_X86_REG_RCX,obj)
            cpu.reg_write(ux.UC_X86_REG_RDX,1 if target==0x5ccb90 else 0)
            executed=[];writes=[];nested=[]
            def visit(u,pc,n,_):
                assert pc in allowed,hex(pc);executed.append(hex(pc-base))
                i=allowed[pc]
                if i.mnemonic=='call':
                    assert target==0x5d78f0 and i.operands[0].type==capstone.x86.X86_OP_IMM and i.operands[0].imm==base+0x08a420
                    nested.append(hex(pc-base))
            def write(u,access,address,n,value,_):
                if stack<=address and address+n<=stack+0x10000:return
                assert obj<=address and address+n<=obj+size
                writes.append({'offset':hex(address-obj),'size':n,'value':hex(value&((1<<(n*8))-1))})
            cpu.hook_add(UC_HOOK_CODE,visit);cpu.hook_add(UC_HOOK_MEM_WRITE,write)
            cpu.emu_start(base+target,stop,count=10000)
            assert cpu.reg_read(ux.UC_X86_REG_RIP)==stop and cpu.reg_read(ux.UC_X86_REG_RSP)==rsp+8
            assert bytes(cpu.mem_read(obj,size))==expected
            empty_replays.append({'targetRva':hex(target),'poison':poison,'emptyVectorBeginAndEnd':hex(begin),
                                  'executedInstructionRvas':executed,'nestedCalls':nested,'writes':writes,
                                  'afterSha256':hashlib.sha256(expected).hexdigest()})
    annotate(0x5cf4e3,'conditional-empty-path-closed','Actual 5d78f0 plus08a420 replayed with count+4=0 and vector begin=end. No substituted calls; only local reset writes. Prior callback stability remains an external precondition.')
    global_writes=[]
    for owner,(_,instructions) in decoded.items():
        for i in instructions:
            for n,op in enumerate(i.operands):
                if op.type==capstone.x86.X86_OP_MEM and op.mem.base==capstone.x86.X86_REG_RIP and memory_operand_writes(i,n):
                    global_writes.append({'ownerRva':hex(owner),'instruction':rows([i])[0],'destinationRva':hex(i.address+i.size+op.mem.disp-base)})
    result={'schemaVersion':1,'status':'bounded-constructor-escape-investigation-open','nativeSourceSha256':EXPECTED,
            'reviewedSources':pins,'retainedEvidence':evidence,
            'bodies':{hex(k):{'ranges':[[hex(a),hex(b)]for a,b in ranges],'instructions':rows(ins)}for k,(ranges,ins)in decoded.items()},
            'calls':calls,'directRipRelativeWrites':global_writes,
            'memsetImport':{'thunkInstructions':rows(memset_thunk),'provider':imports[memset_slot],'iatRva':hex(memset_slot-base)},
            'conditionalEmptyOriginalInstructionReplays':empty_replays,
            'closedNarrowProofs':[
                {'name':'09f840 ignores apparent renderer member RCX','instructions':rows(ignored),'alternateNullStringPath':rows(alternate),
                 'scope':'Incoming RCX discarded. Resource manager receives global manager, caller-local output and local string. This does not prove callback nonreentrancy, ambient-global access or the full renderer constructor.'},
                {'name':'5b64e0 scalar renderer configuration leaf','writes':rows(leaf_writes),
                 'scope':'No calls; unchanged RCX owns all nonstack writes at88..ab. Two stack writes save XMM6/7. Global configuration reads do not publish the receiver.'},
                {'name':'05e2f0 element leaf','instructions':rows(decoded[0x05e2f0][1]),
                 'scope':'Only writes qword receiver+8=0 and returns receiver; no publication.'}],
            'freshPathConditions':[
                '5ccb90(Mission,1) can be closed only with primary highwater0, empty1044e50/58 vector and Scene18=null.',
                'Constructor-zero scene/resource fields conditionally skip several virtual releases in reset; no claim that unknown prior callbacks cannot change them.',
                '5e5270 establishes zero pointer tables used by the first two reset cleanup loops. Its body is call-free; transitive concurrent mutation remains outside the proof.',
                'Default element no-escape evidence is limited to inactive constructors/destructors, not whole-Mission construction or arbitrary reused reset states.'],
            'remainingExactBoundaries':[
                {'callee':'698980','site':'5ce7b2','argument':'Mission+1044898,8','remaining':'Complete resize/allocation path and no callback retaining owner; fragmented pdata is retained.'},
                {'callee':'_Mtx_init_in_situ / _Cnd_init_in_situ','sites':['5ce5c1','5ce5d5','5ced4c','5ced67','5cee2b','5cee46'],
                 'remaining':'Exact CRT initialization no-retention/write-bound/failure contract; external synchronization initialization is not an engine worker fence.'},
                {'callee':'5b5d40 resource calls','remaining':'Resource clone/material updates and virtual releases can call external code; no whole-path reentrancy/ambient-Mission publication exclusion yet.'},
                {'callee':'1b9930,1b9ca0,6e9ea0 and global D9AE20 cleanup','remaining':'Reset global cleanup can occur independently of fresh Mission emptiness; no direct Mission argument alone does not exclude ambient state callbacks.'},
                {'callee':'native allocator729b40 / CRT memset / vector EH iterators','remaining':'Owned allocation and local initialization are distinguished from unreviewed exception/allocator hooks and reentrant behavior.'}],
            'conclusion':'The before-wrapper-publication location5e5b80 remains a candidate only. No complete constructor no-escape or safe live-adoption barrier has been proved.',
            'limits':['Raw call inventory is per selected unwind-chain ranges; exception funclets and arbitrary indirect targets are not a whole-program closure.',
                      'No direct RIP store is not proof of no publication: register-based/global-indirect stores and callees still matter.',
                      'Six Unicorn cases execute original conditionally empty helper paths only, not full native Mission construction. No game, native constructor execution or synchronization probe occurred.'],
            'gameLaunched':False,'liveAdoptionApproved':False,'enginePatchApproved':False}
    assert pins=={str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in protected}
    safe_review_output(OUT,protected).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'status':result['status'],'bodies':len(decoded),'instructions':sum(len(i)for _,i in decoded.values()),'calls':len(calls),'sha256':sha(OUT)}))


if __name__=='__main__':main()
