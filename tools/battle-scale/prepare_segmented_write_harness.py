"""Build/replay one exact inline write adapter in isolated address spaces.

Never edits the installed game. Native lifetime proof is limited to the helper's
leased byte write; all other engine readers and production installation remain
outside this fixture.
"""
from pathlib import Path
import hashlib,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
from unicorn.x86_const import *
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
SITE=0x1805e5e96;STUB=0x181100000;HELPER=STUB+0x1000;STOP=STUB+0x1100
REGS=['rax','rcx','rdx','rbx','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15']
CODES=[0,1,2,3,5,6,7,8,9,10,11,12,13,14,15]
NONVOL={3,5,6,7,12,13,14,15}
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def branch(here,target):return b'\xe8'+struct.pack('<i',target-here-5)

def make_stub(helper):
    raw=bytearray.fromhex('4869c8600c0000');codes=[]
    raw+=bytes.fromhex('4881ec08010000');alloc_offset=len(raw)
    for index,reg in enumerate(CODES):
        raw+=bytes([0x4c if reg>=8 else 0x48,0x89,0x84+((reg&7)<<3),0x24])+struct.pack('<I',32+index*8)
        if reg in NONVOL:codes.append((len(raw),reg,32+index*8))
    prolog=len(raw)
    # Volatile XMM saves lie above the helper's32-byte home area. Nonvolatile
    # SIMD registers are preserved by the called Windows x64 C++ function.
    for reg in range(6):raw+=bytes([0x0f,0x11,0x84+(reg<<3),0x24])+struct.pack('<I',160+reg*16)
    raw+=bytes.fromhex('488b8c2450000000 488b942420000000 48b8')+struct.pack('<Q',helper)+bytes.fromhex('ffd0')
    for reg in range(6):raw+=bytes([0x0f,0x10,0x84+(reg<<3),0x24])+struct.pack('<I',160+reg*16)
    for index,reg in enumerate(CODES):
        raw+=bytes([0x4c if reg>=8 else 0x48,0x8b,0x84+((reg&7)<<3),0x24])+struct.pack('<I',32+index*8)
    raw+=bytes.fromhex('4881c408010000 c3')
    # One fixed frame and genuine nonvolatile MOV saves. All volatile restores
    # occur before the canonical ADD RSP,108;RET epilogue.
    unwind=bytearray([1,prolog,len(codes)*2+2,0])
    for offset,reg,slot in reversed(codes):unwind+=bytes([offset,(reg<<4)|4])+struct.pack('<H',slot//8)
    unwind+=bytes([alloc_offset,1])+struct.pack('<H',264//8)
    while len(unwind)%4:unwind+=b'\x00'
    return bytes(raw),bytes(unwind)

def main():
    assert digest(SOURCE)==SHA
    pe=pefile.PE(str(SOURCE));original=pe.get_data(0x5e5e96,15)
    assert original.hex()=='4869c8600c0000c68439d032000001'
    assert pe.get_data(0x5e5ea5,6).hex()=='4883c4205fc3'
    source_paths=[Path(__file__),ROOT/'evidence/engine-extension/native-segmented-agent-consumers.json',
                  ROOT/'evidence/engine-extension/native-live-agent-access-review.json']
    before={str(p):digest(p) for p in source_paths}
    inventory=json.loads(source_paths[2].read_text())
    owner=next(c for c in inventory['candidates'] if c['rva']=='0x5e5e96')['ownerFlowProof']
    assert owner['effectiveBias']==0x580
    consumer=next(c for c in json.loads(source_paths[1].read_text())['candidates'] if c['multiplyRva']=='0x5e5e96')
    assert len(consumer['contexts'])==1 and consumer['contexts'][0]['eventCounts']=={'memory-write-address':1}
    stub,unwind=make_stub(HELPER);patched=branch(SITE,STUB)+b'\x90'*10+pe.get_data(0x5e5ea5,6)
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    decoded=list(md.disasm(stub,STUB));assert sum(i.size for i in decoded)==len(stub)
    stack=0x50000000;mission=0x60000000
    uc_regs={name:globals()['UC_X86_REG_'+name.upper()] for name in REGS}
    cases=[]
    for index in (-1,0,2039,2040,4079,4080,65535,70000,70001,0x7fffffff):
      for active in (False,True):
        uc=Uc(UC_ARCH_X86,UC_MODE_64)
        for a,n in ((SITE&~4095,4096),(STUB,8192),(stack,65536),(0x70000000,4096)):
            uc.mem_map(a,n)
        uc.mem_write(SITE,patched);uc.mem_write(STUB,stub);uc.mem_write(HELPER,b'\xc3');uc.mem_write(STOP,b'\xc3')
        initial={name:0x1122334400000000+j for j,name in enumerate(REGS)}
        initial['rax']=index&((1<<64)-1);initial['rdi']=mission
        for name,value in initial.items():uc.reg_write(uc_regs[name],value)
        xmm={i:int.from_bytes(bytes([i+1])*16,'little') for i in range(16)}
        for i,v in xmm.items():uc.reg_write(globals()['UC_X86_REG_XMM'+str(i)],v)
        rsp=stack+0x8000;old_rdi=0xabcdef1234567890
        uc.reg_write(UC_X86_REG_RSP,rsp);uc.mem_write(rsp+0x20,struct.pack('<QQ',old_rdi,STOP))
        calls=[]
        def observe(emu,address,size,user):
            if address==STOP:emu.emu_stop();return
            if address!=HELPER:return
            assert emu.reg_read(UC_X86_REG_RCX)==mission
            actual=emu.reg_read(UC_X86_REG_RDX);assert actual==initial['rax']
            assert (emu.reg_read(UC_X86_REG_RSP)+8)%16==0
            valid=active and 0<=index<=70000
            if valid:emu.mem_write(0x70000580,b'\x01')
            calls.append(valid)
            for name in ('rax','rcx','rdx','r8','r9','r10','r11'):emu.reg_write(uc_regs[name],0xeeeeeeeeeeeeeeee)
            for i in range(6):emu.reg_write(globals()['UC_X86_REG_XMM'+str(i)],0xffffffffffffffffffffffffffffffff)
            emu.reg_write(UC_X86_REG_EFLAGS,0x202)
        uc.hook_add(UC_HOOK_CODE,observe);uc.emu_start(SITE,STOP+1,count=1000)
        assert uc.reg_read(UC_X86_REG_RIP)==STOP and uc.reg_read(UC_X86_REG_RSP)==rsp+0x30
        for name,value in initial.items():
            expected=old_rdi if name=='rdi' else (initial['rax']*0xc60)&((1<<64)-1) if name=='rcx' else value
            assert uc.reg_read(uc_regs[name])==expected,(index,name)
        for i,v in xmm.items():assert uc.reg_read(globals()['UC_X86_REG_XMM'+str(i)])==v
        valid=active and 0<=index<=70000
        assert calls==[valid] and bytes(uc.mem_read(0x70000580,1))==(b'\x01' if valid else b'\x00')
        cases.append({'index':index,'active':active,'writeOccurred':valid,'passed':True})
    assert before=={str(p):digest(p) for p in source_paths}
    header='#pragma once\n#include <array>\n'
    for name,raw in [('write_stub_template',stub),('write_stub_unwind',unwind)]:
        header+=f'inline constexpr std::array<unsigned char,{len(raw)}> {name}'+ '{'+','.join(hex(x) for x in raw)+'};\n'
    helper_offset=stub.index(struct.pack('<Q',HELPER));header+=f'inline constexpr std::size_t write_helper_offset={helper_offset};\n'
    header+=f'inline constexpr std::array<unsigned int,{len(decoded)}> write_instruction_offsets'+'{'+','.join(str(i.address-STUB) for i in decoded)+'};\n'
    (ROOT/'build/agent-storage/write_machine_code.hpp').write_text(header)
    report={'schemaVersion':1,'status':'isolated-inline-write-replay-not-installable','binarySha256':SHA,'sources':before,
            'siteRva':'0x5e5e96','originalBytes':original.hex(),'patchBytes':patched[:15].hex(),'stubBytes':stub.hex(),
            'unwindBytes':unwind.hex(),'instructionOffsets':[i.address-STUB for i in decoded],
            'helperImmediateOffset':helper_offset,'cases':cases,'passedCases':len(cases),
            'ownerContract':owner,'consumerProof':consumer,
            'flagsContract':'Original IMUL is executed first. Its flags are not read by the replaced MOV or following original ADD RSP20; ADD overwrites arithmetic flags before POP/RET. No generic flag-preservation claim.',
            'helperContract':'RCX=Mission,RDX=sign-extended index. noexcept. Validate domain/index and hold storage read lease through byte write atAgent+580; no Agent pointer returned.',
            'limits':['Synthetic helper model only; Windows runtime unwind registration and real C++ storage integration require separate test.',
                      'No complete incoming-branch/interior-entry proof, engine thread/lifetime barrier, or installed-game patch.',
                      'Invalid lookups are ignored by the prospective helper; existing game-wide invalid-index contracts remain separate.',
                      'Network callback registration establishes this owner context; other incoming callers and multiplayer protocol growth are not approved.'],
            'enginePatchApproved':False,'gameLaunched':False}
    out=ROOT/'evidence/engine-extension/native-segmented-write-replay.json';out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'cases':len(cases),'stubBytes':len(stub),'sha256':digest(out)}))
if __name__=='__main__':main()
