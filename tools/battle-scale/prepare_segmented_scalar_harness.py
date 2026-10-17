"""Build seven isolated scalar adapters; never patches a game image.

The error path is a deliberate fixture UD2. No production error policy or
native entry/unwind integration is approved by this synthetic CALL fixture.
"""
from pathlib import Path
import hashlib,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from unicorn import Uc,UcError,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
from unicorn.x86_const import *
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
ROUTES=ROOT/'evidence/engine-extension/native-simple-agent-operation-routes.json'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
ROUTE_SHA='fb5e74eff6c0086c97bfc75da29db2768fce1047ac15aacdb0557933116f2516'
BASE=0x180000000;STUB=0x181100000;HELPER=STUB+0x8000;STOP=STUB+0x9000
REGS=['rax','rcx','rdx','rbx','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15']
CODES=[0,1,2,3,5,6,7,8,9,10,11,12,13,14,15]
NONVOL={3,5,6,7,12,13,14,15};SLOTS={n:32+i*8 for i,n in enumerate(REGS)}
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def canonical(reg):return {'eax':'rax','ecx':'rcx','edx':'rdx'}.get(reg,reg)
def memreg(reg,slot,load=True,width64=True):
    code=CODES[REGS.index(reg)];rex=(0x48 if width64 else 0x40)|(4 if code>=8 else 0)
    return bytes([rex,0x8b if load else 0x89,0x84+((code&7)<<3),0x24])+struct.pack('<I',slot)
def frame(prefix=b''):
    raw=bytearray(prefix)+bytearray.fromhex('4881ec08010000');allocation=len(raw);saves=[]
    for reg,code in zip(REGS,CODES):
        raw+=memreg(reg,SLOTS[reg],False)
        if code in NONVOL:saves.append({'endOffset':len(raw),'code':code,'slot':SLOTS[reg]})
    return raw,allocation,saves,len(raw)
def unwind(prolog,allocation,saves):
    raw=bytearray([1,prolog,len(saves)*2+2,0])
    for s in reversed(saves):raw+=bytes([s['endOffset'],(s['code']<<4)|4])+struct.pack('<H',s['slot']//8)
    raw+=bytes([allocation,1])+struct.pack('<H',33)
    while len(raw)%4:raw+=b'\0'
    return bytes(raw)
def make(route,site_id):
    prefix=bytes.fromhex(route['instructions'][0]['bytes'])
    raw,alloc,saves,prolog=frame(prefix)
    for reg in range(6):raw+=bytes([0x0f,0x11,0x84+(reg<<3),0x24])+struct.pack('<I',160+reg*16)
    raw+=memreg('rcx',SLOTS[route['entryInputs']['missionRegister']])
    raw+=memreg('rdx',SLOTS[route['entryInputs']['logicalIndexRegister']])
    raw+=b'\x41\xb8'+struct.pack('<I',site_id)
    raw+=b'\x48\xb8';helper_offset=len(raw);raw+=struct.pack('<Q',HELPER)+b'\xff\xd0'
    raw+=memreg('rax',152,False)
    # Packed result high DWORD is an explicit fixture error, never a zero read.
    raw+=bytes.fromhex('83bc249c00000000 7402');trap=len(raw);raw+=b'\x0f\x0b'
    if route['operation']['kind']=='read':
        raw+=memreg('rax',152,width64=False)
        raw+=memreg('rax',SLOTS[canonical(route['operation']['register'])],False)
    for reg in range(6):raw+=bytes([0x0f,0x10,0x84+(reg<<3),0x24])+struct.pack('<I',160+reg*16)
    for reg in REGS:raw+=memreg(reg,SLOTS[reg])
    epilog=len(raw);raw+=bytes.fromhex('4881c408010000c3')
    # ABI test wrapper captures original incoming arguments before overwriting
    # either register. Its original context is not a Bannerlord native frame.
    entry,ea,es,ep=frame()
    entry+=memreg(route['entryInputs']['missionRegister'],SLOTS['rcx'])
    entry+=memreg(route['entryInputs']['logicalIndexRegister'],SLOTS['rdx'])
    call_offset=len(entry);entry+=b'\xe8'+struct.pack('<i',-(0x400+call_offset+5))
    if route['operation']['kind']=='read':
        dest=CODES[REGS.index(canonical(route['operation']['register']))]
        entry+=bytes([0x8b,0xc0+dest])  # MOV EAX,destination32 (all three low regs)
    else:entry+=bytes.fromhex('b801000000')
    entry+=memreg('rax',152,False)
    for reg in REGS:entry+=memreg(reg,SLOTS[reg])
    entry+=memreg('rax',152,width64=False)
    ee=len(entry);entry+=bytes.fromhex('4881c408010000c3')
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    def pack(code,allocation,saved,prologue,epilogue):
        ds=list(md.disasm(code,0));assert sum(i.size for i in ds)==len(code)
        return {'bytes':bytes(code).hex(),'unwind':unwind(prologue,allocation,saved).hex(),'instructionOffsets':[i.address for i in ds],'allocationEnd':allocation,'prologEnd':prologue,'epilogStart':epilogue,'nonvolatileSaves':saved}
    assert len(raw)<0x300 and len(entry)<0x300
    return {'siteId':site_id,'route':route,'stub':pack(raw,alloc,saves,prolog,epilog),'entry':pack(entry,ea,es,ep,ee),'helperOffset':helper_offset,'trapOffset':trap}

def replay(site):
    route=site['route'];rva=int(route['multiplyRva'],16);address=BASE+rva
    regs={n:globals()['UC_X86_REG_'+n.upper()] for n in REGS};stack=0x50000000;mission=0x60000000;payload=0x70000000
    cases=[]
    scenarios=[(n,'ok') for n in (0,2039,2040,4079,4080,6119,6120,65535,66048,70000)]
    scenarios +=[(-1,'invalid-index'),(70001,'invalid-index'),(0x7fffffffffffffff,'invalid-index'),(0,'foreign-owner'),(1,'unoccupied'),(0,'injected-failure')]
    for index,scenario in scenarios:
        value=(0x80000000|((index*7919+site['siteId']*101)&0x7fffffff))&0xffffffff
        initial={n:0x1122334400000000+j for j,n in enumerate(REGS)}
        initial[route['entryInputs']['missionRegister']]=mission if scenario!='foreign-owner' else mission+8
        initial[route['entryInputs']['logicalIndexRegister']]=index&((1<<64)-1)
        xmm={i:int.from_bytes(bytes([i+1])*16,'little') for i in range(16)}
        uc=Uc(UC_ARCH_X86,UC_MODE_64)
        for a,n in ((STUB,0xa000),(stack,65536),(payload,4096)):uc.mem_map(a,n)
        uc.mem_write(STUB,bytes.fromhex(site['stub']['bytes']));uc.mem_write(HELPER,b'\xc3');uc.mem_write(STOP,b'\xc3')
        field=int(route['operation']['fieldOffset'],16);width=route['operation']['widthBytes']
        uc.mem_write(payload+field,struct.pack('<I',value)[:width])
        for n,v in initial.items():uc.reg_write(regs[n],v)
        for i,v in xmm.items():uc.reg_write(globals()['UC_X86_REG_XMM'+str(i)],v)
        rsp=stack+0x8008;uc.reg_write(UC_X86_REG_RSP,rsp);uc.mem_write(rsp,struct.pack('<Q',STOP));calls=[]
        def callback(emu,pc,size,user):
            if pc==STOP:emu.emu_stop();return
            if pc!=HELPER:return
            assert emu.reg_read(UC_X86_REG_RCX)==initial[route['entryInputs']['missionRegister']]
            assert emu.reg_read(UC_X86_REG_RDX)==initial[route['entryInputs']['logicalIndexRegister']]
            assert emu.reg_read(UC_X86_REG_R8)==site['siteId']
            assert (emu.reg_read(UC_X86_REG_RSP)+8)%16==0
            good=scenario=='ok';calls.append(good)
            if good and route['operation']['kind']=='write':emu.mem_write(payload+field,b'\x01')
            for n in ('rax','rcx','rdx','r8','r9','r10','r11'):emu.reg_write(regs[n],0xeeeeeeeeeeeeeeee)
            for i in range(6):emu.reg_write(globals()['UC_X86_REG_XMM'+str(i)],(1<<128)-1)
            emu.reg_write(UC_X86_REG_EFLAGS,0x202)
            emu.reg_write(UC_X86_REG_RAX,value if good else 0x100000000)
        uc.hook_add(UC_HOOK_CODE,callback)
        trapped=False
        try:uc.emu_start(STUB,STOP+1,count=1000)
        except UcError:
            assert scenario!='ok' and uc.reg_read(UC_X86_REG_RIP)==STUB+site['trapOffset'];trapped=True
        assert calls==[scenario=='ok']
        if scenario=='ok':
            assert not trapped and uc.reg_read(UC_X86_REG_RSP)==rsp+8 and uc.reg_read(UC_X86_REG_RIP)==STOP
            # Compare against actual original machine instructions, not a
            # duplicate scalar model. Map only the addressed original page.
            oracle=Uc(UC_ARCH_X86,UC_MODE_64);oracle.mem_map(address&~4095,8192)
            original_field=mission+index*0xc60+0x2d50+field;oracle.mem_map(original_field&~4095,4096)
            oracle.mem_write(address,bytes.fromhex(route['originalBytes']));oracle.mem_write(original_field,struct.pack('<I',value)[:width])
            for n,v in initial.items():oracle.reg_write(regs[n],v)
            oracle.emu_start(address,int(route['windowEndExclusive'],16),count=2)
            for n in REGS:assert uc.reg_read(regs[n])==oracle.reg_read(regs[n]),(site['siteId'],index,n)
            for i,v in xmm.items():assert uc.reg_read(globals()['UC_X86_REG_XMM'+str(i)])==v
            assert bytes(uc.mem_read(payload+field,width))==bytes(oracle.mem_read(original_field,width))
        else:
            assert trapped and bytes(uc.mem_read(payload+field,width))==struct.pack('<I',value)[:width]
        cases.append({'index':index,'scenario':scenario,'explicitTrap':trapped,'passed':True})
    return cases

def main():
    assert digest(SOURCE)==SHA and digest(ROUTES)==ROUTE_SHA
    source_paths=[SOURCE,ROUTES,Path(__file__)];before={str(p):digest(p) for p in source_paths}
    routes=json.loads(ROUTES.read_text());assert routes['nativeSourceSha256']==SHA and len(routes['routes'])==7
    pe=pefile.PE(data=SOURCE.read_bytes());sites=[]
    for i,r in enumerate(routes['routes']):
        assert pe.get_data(int(r['multiplyRva'],16),r['windowLength']).hex()==r['originalBytes']
        assert r['operation']['kind']=='write' or r['operation']['widthBytes']==4
        site=make(r,i);site['cases']=replay(site);sites.append(site)
    header='#pragma once\n#include <array>\n#include <cstddef>\n#include <span>\n'
    for s in sites:
        for kind in ('stub','entry'):
            for key in ('bytes','unwind'):
                raw=bytes.fromhex(s[kind][key]);header+=f'inline constexpr std::array<unsigned char,{len(raw)}> scalar_{s["siteId"]}_{kind}_{key}'+'{'+','.join(hex(x) for x in raw)+'};\n'
            offsets=s[kind]['instructionOffsets'];header+=f'inline constexpr std::array<unsigned int,{len(offsets)}> scalar_{s["siteId"]}_{kind}_offsets'+'{'+','.join(str(x) for x in offsets)+'};\n'
    header+='struct ScalarTemplate { std::span<const unsigned char> stub,unwind,entry,entry_unwind; std::span<const unsigned int> stub_offsets,entry_offsets; unsigned helper_offset,trap_offset,stub_alloc,stub_epilog,entry_alloc,entry_epilog,field,width; bool writes; };\n'
    header+='inline constexpr std::array<ScalarTemplate,7> scalar_templates{{\n'
    for s in sites:
        i=s['siteId'];header+='{'+','.join(f'scalar_{i}_{n}' for n in ('stub_bytes','stub_unwind','entry_bytes','entry_unwind','stub_offsets','entry_offsets'))+','
        header+=','.join(str(x) for x in (s['helperOffset'],s['trapOffset'],s['stub']['allocationEnd'],s['stub']['epilogStart'],s['entry']['allocationEnd'],s['entry']['epilogStart'],int(s['route']['operation']['fieldOffset'],16),s['route']['operation']['widthBytes']))+','+('true' if s['route']['operation']['kind']=='write' else 'false')+'},\n'
    header+='}};\n'
    assert before=={str(p):digest(p) for p in source_paths}
    header_path=ROOT/'build/agent-storage/scalar_machine_code.hpp';header_path.write_text(header)
    report={'schemaVersion':1,'status':'passed-isolated-scalar-adapter-replay-not-installable','nativeSourceSha256':SHA,'sources':before,'generatedHeaderSha256':digest(header_path),'sites':sites,'passedCases':sum(len(s['cases']) for s in sites),'errorContract':'Packed high32 nonzero causes exact generated UD2. The fixture handles that exception and records explicit failure. No silent zero/ignored write and no production error policy.','limitations':['Standard CALL entry with RSP8mod16 only; real native replacement call alignment/incoming entries/unwind scope remains unproved.','Normal-path register and scalar semantics only; no general arithmetic flag preservation claim. Accepted per-site continuation proofs make arithmetic flags dead.','Helper lease must cover operation; no Agent pointer escapes. Concurrent same-location writes still need engine ownership.'],'enginePatchApproved':False,'gameLaunched':False}
    out=ROOT/'evidence/engine-extension/native-segmented-scalar-replay.json';out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'sites':len(sites),'passedCases':report['passedCases'],'sha256':digest(out)}))
if __name__=='__main__':main()
