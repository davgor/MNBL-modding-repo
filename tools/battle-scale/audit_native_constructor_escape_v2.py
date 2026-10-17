"""Narrow constructor no-escape extension: exact CRT leaves and external edges.

This preserves v1 and never loads an engine/CRT DLL. Unicorn cases execute
original bytes only, with unexpected calls rejected rather than stubbed out.
"""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
from unicorn import x86_const as x
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output

SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
V1=ROOT/'evidence/engine-extension/native-constructor-escape-audit.json'
CRT=Path('C:/Windows/System32/MSVCP140.dll')
OUT=ROOT/'evidence/engine-extension/native-constructor-escape-v2.json'
BUNDLE=ROOT/'evidence/engine-extension/native-live-growth-rea-evidence-6.json'
BUNDLE_SHA='af039b6ce64f34fc4fc224f8f1f39523af1d6fb4c0698849e8f7005d70d97c07'
NATIVE_SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
CRT_SHA='7c26614e1d733892c2deac7e245ce115504b1d80592dd0a01b08e3e5a55f89ca'


def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    protected=[SOURCE,V1,CRT,BUNDLE,Path(__file__)]
    pins={str(p):digest(p) for p in protected}
    assert digest(SOURCE)==NATIVE_SHA and digest(CRT)==CRT_SHA
    old=json.loads(V1.read_text());assert old['nativeSourceSha256']==NATIVE_SHA
    native=pefile.PE(data=SOURCE.read_bytes());crt=pefile.PE(data=CRT.read_bytes())
    base=native.OPTIONAL_HEADER.ImageBase
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    entries={e.struct.BeginAddress:e.struct for e in native.DIRECTORY_ENTRY_EXCEPTION}
    def chain_root(e):
        seen=set()
        while True:
            assert e.UnwindData not in seen;seen.add(e.UnwindData)
            h=native.get_data(e.UnwindData,4)
            if not (h[0]>>3)&4:return e.BeginAddress
            b,n,u=struct.unpack('<III',native.get_data(e.UnwindData+4+((h[2]+1)//2)*4,12))
            e=entries[b];assert e.EndAddress==n and e.UnwindData==u
    bodies={}
    for target in (0x698a70,0x1b9930,0x1b9ca0,0x07e7d0):
        ranges=sorted((e.BeginAddress,e.EndAddress)for e in entries.values()if chain_root(e)==target)
        assert ranges
        ins=[]
        for a,b in ranges:
            decoded=list(md.disasm(native.get_data(a,b-a),base+a));assert sum(i.size for i in decoded)==b-a
            ins+=decoded
        bodies[target]=(ranges,ins)
    def rows(ins):return [{'rva':hex(i.address-base),'bytes':i.bytes.hex(),'instruction':i.mnemonic+' '+i.op_str}for i in ins]
    assert digest(BUNDLE)==BUNDLE_SHA
    bundle=json.loads(BUNDLE.read_text())
    assert bundle['requests']==[45,46,47,48]
    evidence=[record['evidence'] for record in bundle['records']]
    assert {r['parameters']['procedure']for r in evidence}=={hex(base+k)for k in bodies}
    assert all(r['subject']['digest']['sha256']==NATIVE_SHA for r in evidence)
    # Exact leaf exports, not API-name assumptions or the currently installed
    # application's unspecified provider resolution.
    exports={e.name.decode():e for e in crt.DIRECTORY_ENTRY_EXPORT.symbols if e.name}
    crt_bodies={}
    for name,expected_start,expected_end in [('_Cnd_init_in_situ',0x3aa0,0x3aaa),('_Mtx_init_in_situ',0x17d30,0x17d47)]:
        e=exports[name];assert e.address==expected_start and not e.forwarder
        code=crt.get_data(expected_start,expected_end-expected_start)
        ins=list(md.disasm(code,base+expected_start));assert sum(i.size for i in ins)==len(code)
        assert ins[-1].mnemonic=='ret' and all(i.mnemonic not in ('call','jmp')for i in ins)
        for i in ins:
            for n,op in enumerate(i.operands):
                if op.type==capstone.x86.X86_OP_MEM and memory_operand_writes(i,n):
                    assert op.mem.base==capstone.x86.X86_REG_RCX and not op.mem.index
        crt_bodies[name]={'rva':hex(expected_start),'endRva':hex(expected_end),'instructions':rows(ins)}
    executions=[]
    for name,body in crt_bodies.items():
        start,end=int(body['rva'],16),int(body['endRva'],16)
        for poison in (0x35,0xcd):
            for flags in (0,2,0x102,0xffffffff):
                cpu=Uc(UC_ARCH_X86,UC_MODE_64);page=(base+start)&~4095
                cpu.mem_map(page,4096);cpu.mem_write(base+start,crt.get_data(start,end-start))
                obj,stack,stop=0x60000000,0x50000000,0x190000000
                cpu.mem_map(obj,4096);cpu.mem_map(stack,4096);cpu.mem_map(stop,4096)
                before=bytes([poison])*4096;expected=bytearray(before);receiver=obj+0x101
                if name.startswith('_Cnd'):expected[0x101:0x111]=bytes(16)
                else:
                    struct.pack_into('<I',expected,0x101,flags)
                    expected[0x109:0x119]=bytes(16)
                    struct.pack_into('<iI',expected,0x149,-1,0)
                cpu.mem_write(obj,before);rsp=stack+0x808;cpu.mem_write(rsp,struct.pack('<Q',stop))
                cpu.reg_write(x.UC_X86_REG_RSP,rsp);cpu.reg_write(x.UC_X86_REG_RCX,receiver);cpu.reg_write(x.UC_X86_REG_RDX,flags)
                writes=[]
                def write(u,a,address,n,value,_):
                    assert obj<=address and address+n<=obj+4096
                    writes.append({'offset':address-receiver,'size':n,'value':value&((1<<(8*n))-1)})
                cpu.hook_add(UC_HOOK_MEM_WRITE,write)
                cpu.emu_start(base+start,stop,count=100)
                assert cpu.reg_read(x.UC_X86_REG_RIP)==stop and cpu.reg_read(x.UC_X86_REG_RSP)==rsp+8
                assert bytes(cpu.mem_read(obj,4096))==expected
                executions.append({'provider':'pinned-System32-MSVCP140','export':name,'poison':poison,'flags':flags,'writes':writes})
    # Empty *global resource* containers also skip virtual releases, but their
    # emptiness is independent of a fresh Mission and cannot be assumed.
    for target in (0x1b9930,0x1b9ca0):
        ranges,ins=bodies[target]
        for poison in (0x35,0xcd):
            cpu=Uc(UC_ARCH_X86,UC_MODE_64);mapped=set();instructions={i.address:i for i in ins}
            for a,b in ranges:
                for page in range((base+a)&~4095,(base+b+4095)&~4095,4096):
                    if page not in mapped:mapped.add(page);cpu.mem_map(page,4096);cpu.mem_write(page,b'\xcc'*4096)
                cpu.mem_write(base+a,native.get_data(a,b-a))
            obj,stack,stop=0x60000000,0x50000000,0x190000000
            cpu.mem_map(obj,4096);cpu.mem_map(stack,0x10000);cpu.mem_map(stop,4096)
            before=bytearray([poison])*4096
            if target==0x1b9930:
                slot=base+0xd96d78;cpu.mem_map(slot&~4095,4096);cpu.mem_write(slot,struct.pack('<Q',obj))
                struct.pack_into('<QQ',before,0x50,0,0);expected=bytearray(before)
            else:
                struct.pack_into('<QQ',before,0x10,0,0);expected=bytearray(before)
                expected[0:12]=bytes(12)
            cpu.mem_write(obj,bytes(before));rsp=stack+0x8008;cpu.mem_write(rsp,struct.pack('<Q',stop))
            cpu.reg_write(x.UC_X86_REG_RSP,rsp);cpu.reg_write(x.UC_X86_REG_RCX,obj)
            trace=[];writes=[]
            def visit(u,pc,n,_):
                assert pc in instructions and instructions[pc].mnemonic!='call',hex(pc)
                trace.append(hex(pc-base))
            def write(u,a,address,n,value,_):
                if stack<=address and address+n<=stack+0x10000:return
                assert obj<=address and address+n<=obj+4096
                writes.append({'offset':hex(address-obj),'size':n,'value':value&((1<<(8*n))-1)})
            cpu.hook_add(UC_HOOK_CODE,visit);cpu.hook_add(UC_HOOK_MEM_WRITE,write)
            cpu.emu_start(base+target,stop,count=10000)
            assert cpu.reg_read(x.UC_X86_REG_RIP)==stop and cpu.reg_read(x.UC_X86_REG_RSP)==rsp+8
            assert bytes(cpu.mem_read(obj,4096))==expected
            executions.append({'provider':'original-native','rva':hex(target),'poison':poison,'requiredCondition':'selected global resource vector begin=end=0','writes':writes,'trace':trace})
    calls=[]
    for target,(_,ins)in bodies.items():
        for n,i in enumerate(ins):
            if i.mnemonic=='call':calls.append({'ownerRva':hex(target),'rva':hex(i.address-base),'bytes':i.bytes.hex(),'target':i.op_str,'context':rows(ins[max(0,n-10):n])})
    renderer=[c for c in old['calls']if c['ownerRva']=='0x5b5d40']
    report={'schemaVersion':2,'status':'conditional-local-closure-external-callback-boundary-open',
            'nativeSourceSha256':NATIVE_SHA,'reviewedSources':pins,'retainedEvidence':evidence,
            'newBodies':{hex(k):{'ranges':[[hex(a),hex(b)]for a,b in ranges],'instructions':rows(ins)}for k,(ranges,ins)in bodies.items()},
            'newCallBoundaries':calls,'rendererCallContexts':renderer,
            'crtProvider':{'path':str(CRT),'sha256':CRT_SHA,'actualRuntimeBindingObserved':False,
                           'exports':crt_bodies,'requiredRuntimeCheck':'Verify the loaded MSVCP140 module identity and both Native IAT targets resolve to these exact unmodified exports before relying on the leaf contracts.'},
            'originalInstructionExecutions':executions,
            'closedWithinStatedConditions':[
                'Pinned MSVCP140 mutex/CV initialization are call-free local writes only. Sixteen poison/flag cases include deliberately unaligned views; no callback/global publication/thread start occurs in those exact bodies.',
                'Initial698980 input has old pointer/count zero and new dimension8; it calls698a70, which allocates8 bytes through global allocator729b40, stores the result in the member and memset-zeroes it. The owner is retained locally; allocator/memset transitive behavior is not certified.',
                '09f840 already proved to ignore renderer RCX, and returns caller-local output. Renderer moves that reference into+440 and clears the same local before conditional release.',
                '07e7d0 receives a globally loaded mesh resource and caller-local out pointer; retained decompilation returns that same out pointer. Clone internals invoke resource virtual methods, never receiving a direct renderer/Mission argument at this call site.',
                '1b9930 and1b9ca0 skip all nested calls when their GLOBAL resource containers are empty, confirmed in four original-instruction cases without substituting any call.'
            ],
            'preciseExternalDependencies':[
                {'kind':'runtime-provider-identity','sites':['5ce5c1','5ce5d5','5b6283','5ced4c','5ced67','5cee2b','5cee46'],
                 'proofNeeded':'Loaded CRT hash/IAT target verification. A matching DLL on disk is not evidence it is the active provider.'},
                {'kind':'global-resource-cleanup-callbacks','callers':['5cf9f8->1b9930','5cfa04->1b9ca0'],
                 'runtimeInputs':['*[Native+D96D78] vector+50/+58 and each entry receiver+0/+8','*[Native+D96D70] vector+10/+18, nested vectors and entry+40 references'],
                 'proofNeeded':'At the constructor invocation, either prove these independent global containers empty and immutable until the calls, or capture their actual receiver/vtable+38 targets and close those bodies including reentrancy. Fresh Mission metadata does not imply global-resource emptiness.'},
                {'kind':'renderer-resource-callbacks','callers':['5b6381->09f840->0befd0','5b63cd->07e7d0','5b647a->27fb60','5b6489->26c990'],
                 'proofNeeded':'Resolve actual loaded/clone resource types and virtual targets; verify any shared caches, allocator hooks, TLS/current-Mission references or callbacks cannot acquire the new Mission through an external route. No direct Mission argument is only an argument-flow fact.'},
                {'kind':'reset-global-special-branch','callers':['5cf9d0->6e9ea0','global D9AE20 lock/free loop'],
                 'proofNeeded':'Observe or establish global DA6248/DA6240 and global manager state for this invocation; close callbacks and allocator hooks on the actual taken paths.'},
                {'kind':'unwind-and-publication','proofNeeded':'Successful constructor return alone does not prove no asynchronous reader or reference escaped. Any adapter at5e5b80 still needs an independently verified construction/lifetime token, owner retention, mutation exclusion and failure/unwind policy.'}
            ],
            'runtimeProofRecommendation':{
                'notExecuted':True,
                'boundedObservation':'Instrument the owned constructor entry/return and exact unresolved call sites; record new Mission range, thread identity, resolved provider/receiver/vtable targets, global vector snapshots and any callback reentry. Correlate records to the same construction identity.',
                'acceptanceLimit':'A single trace can select a closed build/profile-specific path; absence of a logged pointer escape is not universal proof. General no-escape still requires complete pointer publication coverage or an explicit engine exclusion/lifetime contract.'},
            'conclusion':'Local CRT uncertainty is narrowed to provider identity and global cleanup is narrowed to exact runtime-dependent receiver sets. Complete before-return no-escape remains unproved; post-constructor adoption is not approved.',
            'gameLaunched':False,'nativeConstructorExecuted':False,'liveAdoptionApproved':False,'enginePatchApproved':False}
    assert pins=={str(p):digest(p)for p in protected}
    safe_review_output(OUT,protected).write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'status':report['status'],'cases':len(executions),'sha256':digest(OUT)}))


if __name__=='__main__':main()
