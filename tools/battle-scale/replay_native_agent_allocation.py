"""Replay the original primary allocator and its proven default virtual reset.

Synthetic inactive records and metadata, not activated Agents or engine calls.
The native short table is compared to an independent sorted-set model. No game
is started and no installed file is changed.
"""
from pathlib import Path
import hashlib, json, random, struct, sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone, pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.x86_const import *

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE, POOL, STACK, STOP = 0x180000000, 0x60000000, 0x50000000, 0x190000000
CAP, STRIDE, TABLE, COUNT, HIGH = 2040, 0xc60, 0x629d00, 0x62acf0, 0x62acf4
VTABLE, RESET, ALLOC = 0xb1d990, 0x607390, 0x5d8020
SIZE = HIGH + 4


def main():
    source = SOURCE.read_bytes()
    assert hashlib.sha256(source).hexdigest() == SHA
    pe = pefile.PE(data=source)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    entries = [e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if ALLOC <= e.struct.BeginAddress < 0x5d81d5]
    assert [(e.BeginAddress,e.EndAddress) for e in entries] == [(ALLOC,0x5d8062),(0x5d8062,0x5d818e),(0x5d818e,0x5d81d5)]
    assert struct.unpack('<Q', pe.get_data(VTABLE + 0x68, 8))[0] == BASE + RESET
    ctor = list(md.disasm(pe.get_data(0x606a6a, 10), BASE + 0x606a6a))
    assert ctor[0].mnemonic == 'lea' and ctor[0].address + ctor[0].size + ctor[0].operands[1].mem.disp == BASE + VTABLE
    assert ctor[1].bytes.hex() == '488901'
    spans = [(ALLOC, 0x5d81d5), (RESET, 0x6073ae)]
    instructions = {}
    bodies = []
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    pages = set()
    for start, end in spans:
        raw = pe.get_data(start, end-start)
        decoded = list(md.disasm(raw, BASE+start))
        assert sum(i.size for i in decoded) == len(raw)
        bodies.append({'rva': hex(start), 'endRva': hex(end), 'bytes': raw.hex()})
        for page in range((BASE+start) & ~4095, (BASE+end+4095) & ~4095, 4096):
            if page not in pages:
                pages.add(page); uc.mem_map(page,4096); uc.mem_write(page,b'\xcc'*4096)
        uc.mem_write(BASE+start, raw)
        for ins in decoded:
            instructions[ins.address] = ins
            if ins.mnemonic == 'call': assert ins.address == BASE + 0x5d815e
            for op in ins.operands:
                assert op.type != capstone.x86.X86_OP_MEM or op.mem.base != capstone.x86.X86_REG_RIP
    uc.mem_map((BASE+VTABLE) & ~4095,4096)
    uc.mem_write(BASE+VTABLE+0x68,pe.get_data(VTABLE+0x68,8))
    uc.mem_map(POOL,(SIZE+4095)&~4095)
    uc.mem_map(STACK,0x10000);uc.mem_map(STOP,4096)
    state = {}
    reset_trace = []
    def code_hook(u,address,size,_):
        assert address in instructions, hex(address)
        state['executed'].add(address)
        if address == BASE + RESET:
            index = state['expectedIndex']; assert index >= 0
            assert u.reg_read(UC_X86_REG_RCX) == POOL+index*STRIDE
            # The stock routine has committed header, table and count, but not
            # highwater, at the virtual call. This ordering is not atomic.
            actual = bytes(u.mem_read(POOL,SIZE))
            assert struct.unpack_from('<i',actual,COUNT)[0] == len(state['beforeLive'])+1
            assert struct.unpack_from('<i',actual,HIGH)[0] == state['beforeHigh']
            assert struct.unpack_from('<i',actual,index*STRIDE+0x10)[0] == index
            assert actual[TABLE:TABLE+CAP*2] == state['expectedTable']
            reset_trace.append(index)
    def write_hook(u,access,address,size,value,_):
        if STACK <= address and address+size <= STACK+0x10000:return
        offset = address-POOL
        index = state['expectedIndex']
        allowed = [(TABLE,TABLE+CAP*2),(COUNT,COUNT+4),(HIGH,HIGH+4)]
        if index >= 0:
            allowed += [(index*STRIDE+a,index*STRIDE+a+n) for a,n in [(0x10,4),(0x908,8),(0xb64,1),(0x558,8)]]
        assert any(a <= offset and offset+size <= b for a,b in allowed),(hex(address),size)
    uc.hook_add(UC_HOOK_CODE,code_hook);uc.hook_add(UC_HOOK_MEM_WRITE,write_hook)
    rng = random.Random(0xA110CA7E)
    scenarios = [set(),set(range(CAP)),{0},{CAP-1},{0,CAP-1},set(range(100)),set(range(1,CAP)),set(range(CAP-1))]
    scenarios += [set(range(CAP))-{hole} for hole in (1,63,64,1023,1024,2038)]
    scenarios += [{i for i in range(CAP) if rng.randrange(100)<percent} for percent in (1,5,25,50,75,95,99)]
    rows = []
    nonvolatile = [UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15]
    def table_for(live):
        table = [-1]*CAP;ordered = sorted(live)
        for ordinal,index in enumerate(ordered):table[index] = ordered[ordinal+1] if ordinal+1<len(ordered) else CAP+1
        return struct.pack('<'+'h'*CAP,*table)
    for scenario,live in enumerate(scenarios):
        for poison in (0x35,0xcd):
            blob = bytearray([poison])*SIZE
            for index in range(CAP):
                struct.pack_into('<Q',blob,index*STRIDE,BASE+VTABLE)
                struct.pack_into('<i',blob,index*STRIDE+0x10,index if index in live else -1)
            blob[TABLE:TABLE+CAP*2] = table_for(live)
            before_high = max(live)+1 if live else 0
            struct.pack_into('<ii',blob,COUNT,len(live),before_high)
            uc.mem_write(POOL,bytes(blob))
            expected_index = next((i for i in range(CAP) if i not in live),-1)
            after_live = live | {expected_index} if expected_index >= 0 else live
            expected_table = table_for(after_live)
            expected = bytearray(blob)
            if expected_index >= 0:
                offset = expected_index*STRIDE
                struct.pack_into('<i',expected,offset+0x10,expected_index)
                struct.pack_into('<Q',expected,offset+0x908,0)
                expected[offset+0xb64] = 0
                struct.pack_into('<Q',expected,offset+0x558,0xffffffffffffffff)
                expected[TABLE:TABLE+CAP*2] = expected_table
                struct.pack_into('<ii',expected,COUNT,len(after_live),max(after_live)+1)
            state.update(expectedIndex=expected_index,beforeLive=live,beforeHigh=before_high,expectedTable=expected_table,executed=set())
            rsp = STACK+0x8008
            uc.mem_write(rsp-0x200,b'\xa9'*0x400);uc.mem_write(rsp,struct.pack('<Q',STOP))
            for ordinal,register in enumerate(nonvolatile):uc.reg_write(register,0x1122334455000000+ordinal)
            uc.reg_write(UC_X86_REG_RSP,rsp);uc.reg_write(UC_X86_REG_RCX,POOL)
            before_resets = len(reset_trace)
            uc.emu_start(BASE+ALLOC,STOP,count=500000)
            assert uc.reg_read(UC_X86_REG_RIP)==STOP and uc.reg_read(UC_X86_REG_RSP)==rsp+8
            assert uc.reg_read(UC_X86_REG_RAX)==(expected_index & 0xffffffff)
            assert all(uc.reg_read(register)==0x1122334455000000+ordinal for ordinal,register in enumerate(nonvolatile))
            assert len(reset_trace)-before_resets == int(expected_index>=0)
            actual = bytes(uc.mem_read(POOL,SIZE))
            assert actual==bytes(expected),(scenario,poison,next((i for i,(a,b) in enumerate(zip(actual,expected)) if a!=b),None))
            rows.append({'scenario':scenario,'poison':poison,'beforeCount':len(live),'allocatedIndex':expected_index,
                         'resetCalls':len(reset_trace)-before_resets,'fullPoolSha256':hashlib.sha256(actual).hexdigest(),
                         'executedInstructionRvas':[hex(x-BASE) for x in sorted(state['executed'])]})
    out = {'schemaVersion':1,'status':'bounded-original-primary-allocation-replay','binarySha256':SHA,
           'toolSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'nativeBodies':bodies,
           'vtableRva':hex(VTABLE),'virtualResetOffset':'0x68','virtualResetRva':hex(RESET),'capacity':CAP,'cases':rows,
           'verified':['Lowest free header selected; full pool returns uint32(-1) without mutation',
                       'Exact sorted successor metadata and count/highwater after return',
                       'Full pool bytes match independent result; only index, reset fields and metadata change',
                       'At reset call, header/count/table already changed but highwater not yet updated',
                       'Return stack and all Windows nonvolatile GPRs preserved'],
           'limits':['Synthetic valid states with default Agent vtable; no active native objects',
                     'No native removal or activation path replayed here',
                     'No malformed metadata, concurrent readers, allocation failure or alternate derived vtable',
                     'Native metadata commit order is not an atomic publication protocol',
                     'Finite native capacity and signed-short chain retained; no wider index proof'],
           'gameLaunched':False,'enginePatchApproved':False}
    target = ROOT/'evidence/engine-extension/native-agent-allocation-replay.json'
    assert not target.exists(), 'Preserve prior evidence; select a new receipt path for reruns'
    target.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'cases':len(rows),'resetCalls':len(reset_trace),'status':out['status']}))


if __name__=='__main__':main()
