"""Execute original call-free pool-link helpers on synthetic memory in Unicorn.

No Bannerlord process is started. This tests metadata semantics, not live engine
constructors, concurrency or patch installation.
"""
from pathlib import Path
import hashlib, json, random, struct, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile, capstone
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_EDX,UC_X86_REG_RSP,UC_X86_REG_RIP
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE=0x180000000;POOL=0x60000000;STACK=0x50000000;STOP=0x190000000
CAPACITY=2040;STRIDE=0x2c8;TABLE=0x1629c0;COUNT=0x1639b0;HIGH=0x1639b4

def main():
    blob=SOURCE.read_bytes();assert hashlib.sha256(blob).hexdigest()==SHA
    pe=pefile.PE(data=blob);md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    bodies={};constant_pages=set();constants=[]
    for rva in (0x5d9350,0x5d8610):
        entry=next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress==rva)
        raw=pe.get_data(rva,entry.EndAddress-rva)
        ins=list(md.disasm(raw,BASE+rva))
        assert sum(i.size for i in ins)==len(raw)
        assert not any(i.mnemonic=='call' for i in ins)
        for i in ins:
            for o in i.operands:
                if o.type==capstone.x86.X86_OP_MEM and o.mem.base==capstone.x86.X86_REG_RIP:
                    assert not o.access & capstone.CS_AC_WRITE
                    address=i.address+i.size+o.mem.disp
                    assert BASE<=address<BASE+pe.OPTIONAL_HEADER.SizeOfImage
                    constant=pe.get_data(address-BASE,o.size);assert len(constant)==o.size
                    constants.append({'instruction':hex(i.address),'address':hex(address),'bytes':constant.hex()})
                    constant_pages.update(range(address&~4095,(address+o.size+4095)&~4095,4096))
            if i.group(capstone.CS_GRP_JUMP):
                assert i.operands[0].type==capstone.x86.X86_OP_IMM and BASE+rva<=i.operands[0].imm<BASE+entry.EndAddress
        bodies[hex(rva)]={'bytes':raw.hex(),'endRva':hex(entry.EndAddress)}
    uc=Uc(UC_ARCH_X86,UC_MODE_64)
    uc.mem_map(BASE+0x5d8000,0x2000);uc.mem_write(BASE+0x5d8000,pe.get_data(0x5d8000,0x2000))
    for page in sorted(constant_pages):
        if BASE+0x5d8000<=page<BASE+0x5da000:continue
        uc.mem_map(page,4096);uc.mem_write(page,pe.get_data(page-BASE,4096))
    uc.mem_map(POOL,0x164000);uc.mem_map(STACK,0x10000);uc.mem_map(STOP,0x1000);uc.mem_write(STOP,b'\xc3')
    for i in range(CAPACITY):uc.mem_write(POOL+i*STRIDE,struct.pack('<i',-1))
    uc.mem_write(POOL+TABLE,b'\xff\xff'*CAPACITY)
    live=set();rows=[];mismatches=[]
    def read32(offset):return struct.unpack('<i',uc.mem_read(POOL+offset,4))[0]
    def write32(offset,v):uc.mem_write(POOL+offset,struct.pack('<i',v))
    def invoke(rva,index):
        rsp=STACK+0x8008;uc.mem_write(rsp,struct.pack('<Q',STOP))
        uc.reg_write(UC_X86_REG_RSP,rsp);uc.reg_write(UC_X86_REG_RCX,POOL);uc.reg_write(UC_X86_REG_EDX,index)
        uc.emu_start(BASE+rva,STOP,count=200000)
        assert uc.reg_read(UC_X86_REG_RIP)==STOP and uc.reg_read(UC_X86_REG_RSP)==rsp+8
    def check(operation,index):
        table=struct.unpack('<'+str(CAPACITY)+'h',uc.mem_read(POOL+TABLE,CAPACITY*2))
        ordered=sorted(live);expected={i:(ordered[n+1] if n+1<len(ordered) else CAPACITY+1) for n,i in enumerate(ordered)}
        assert read32(COUNT)==len(live) and read32(HIGH)==(ordered[-1]+1 if ordered else 0)
        for i in range(CAPACITY):
            assert read32(i*STRIDE)==(i if i in live else -1)
            if table[i]!=expected.get(i,-1):
                mismatches.append({'operation':len(rows),'kind':operation,'index':index,'slot':i,
                                   'actual':table[i],'proposedSortedNext':expected.get(i,-1)})
        rows.append({'kind':operation,'index':index,'live':len(live),'highwater':read32(HIGH),
                     'tableSha256':hashlib.sha256(uc.mem_read(POOL+TABLE,CAPACITY*2)).hexdigest()})
    def insert(i):
        assert i not in live
        write32(i*STRIDE,i)
        invoke(0x5d9350,i)
        write32(COUNT,read32(COUNT)+1);write32(HIGH,max(read32(HIGH),i+1))
        live.add(i);check('insert',i)
    def remove(i):
        assert i in live
        invoke(0x5d8610,i);live.remove(i);check('remove',i)
    check('empty',-1)
    for i in (2039,0,1024,1,2038,512,1023):insert(i)
    for i in (2039,1024,0,2038,512,1,1023):remove(i)
    rng=random.Random(0xBADD2040)
    for _ in range(400):
        if live and rng.randrange(3)==0:remove(rng.choice(sorted(live)))
        else:
            i=rng.randrange(CAPACITY)
            if i not in live:insert(i)
    for i in sorted(live,reverse=True):remove(i)
    out={'schemaVersion':1,'status':'bounded-original-helper-metadata-replay',
         'binarySha256':SHA,'toolSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
         'nativeBodies':bodies,'nativeConstants':constants,'capacity':CAPACITY,'operations':rows,
         'sortedSuccessorHypothesisMatches':not mismatches,'mismatches':mismatches,
         'checks':['All observed slot headers, live counts and highwater match independent set model',
                   'Original helper return address and stack restored for every call',
                   'Selected helpers have no CALL or out-of-body branch; read-only RIP-relative constants copied from original image'],
         'limits':['Synthetic 2c8 pool; no game process or engine objects',
                   'Insertion performs reviewed CreateAgent header write/helper/count/highwater order',
                   'No arbitrary malformed input or concurrent mutation passed to unsafe original helpers',
                   'Finite deterministic scenarios; equality is not a universal proof'],
         'gameLaunched':False,'enginePatchApproved':False}
    path=ROOT/'evidence/engine-extension/native-pool-index-chain-replay.json'
    path.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'operations':len(rows),'mismatches':len(mismatches),'firstMismatches':mismatches[:5]}))
if __name__=='__main__':main()
