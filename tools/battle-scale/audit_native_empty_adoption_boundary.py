"""Bounded empty-Mission attach boundary evidence; does not approve a hook."""
from pathlib import Path
import bisect
import hashlib
import json
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
import unicorn
from unicorn import x86_const as x

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE = 0x180000000
CAPACITY = 2040
# Pool offsets and metadata are ORIGINAL stock geometry, not a patched layout.
POOLS = [(0x2d50, 0xc60, 0x10, 0x629d00, 0x62acf0),
         (0x62da50, 0xc60, 0x10, 0x629d00, 0x62acf0),
         (0xc58750, 0x230, 0x1c, 0x116e80, 0x117e70),
         (0xd705c8, 0x2c8, 0, 0x1629c0, 0x1639b0),
         (0xed3f80, 0x2c8, 0, 0x1629c0, 0x1639b0)]


def digest(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def row(i): return dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str)


class Replay:
    mission, stack, result, stop = 0x40000000, 0x50000000, 0x51000000, 0x52000000
    def __init__(self, pe):
        self.u = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
        for address, size in ((BASE, (pe.OPTIONAL_HEADER.SizeOfImage+4095)&~4095),
                              (self.mission, 0x1185000), (self.stack, 0x10000),
                              (self.result, 0x1000), (self.stop, 0x1000)):
            self.u.mem_map(address, size)
        self.u.mem_write(BASE, pe.get_memory_mapped_image())
        self.u.mem_write(self.mission, b'\xa5'*0x1185000)
        self.writes = []
        self.executed = set()
        self.u.hook_add(unicorn.UC_HOOK_MEM_WRITE, self.write_hook)
        self.u.hook_add(unicorn.UC_HOOK_CODE, self.code_hook)
    def write_hook(self, u, access, address, size, value, _):
        if self.mission <= address < self.mission+0x1185000:
            self.writes.append((u.reg_read(x.UC_X86_REG_RIP), address, size))
    def code_hook(self, u, pc, size, _):
        self.executed.add(pc)
        if pc == self.stop: u.emu_stop()
    def run(self, first, end=None, forced_index=0, **registers):
        self.writes.clear(); self.executed.clear()
        rsp = self.stack+0x8008
        self.u.mem_write(self.stack, b'\0'*0x10000)
        self.u.mem_write(rsp, struct.pack('<Q', self.stop))
        # CreateAgent's ninth argument is entryRSP+0x48.
        self.u.mem_write(rsp+0x48, struct.pack('<i', forced_index))
        self.u.reg_write(x.UC_X86_REG_RSP, rsp)
        self.u.reg_write(x.UC_X86_REG_EFLAGS, 0x202)
        for name, value in registers.items():
            self.u.reg_write(getattr(x, 'UC_X86_REG_'+name.upper()), value & ((1<<64)-1))
        finish = BASE+end if end else self.stop
        self.u.emu_start(BASE+first, finish+1 if end is None else finish, count=2000000)
        assert self.u.reg_read(x.UC_X86_REG_RIP) == finish
        return dict(instructions=len(self.executed), missionWrites=len(self.writes))
    def empty(self):
        for offset, stride, header, table, count in POOLS:
            p = self.mission+offset
            if self.u.mem_read(p+count, 8) != b'\0'*8: return False
            if self.u.mem_read(p+table, CAPACITY*2) != b'\xff'*(CAPACITY*2): return False
            for i in range(CAPACITY):
                if self.u.mem_read(p+i*stride+header, 4) != b'\xff'*4: return False
        return True


def build():
    paths = [SOURCE, Path(__file__), ROOT/'evidence/engine-extension/native-companion-occupancy-contract.json',
             ROOT/'evidence/engine-extension/native-callback-contracts.json',
             ROOT/'evidence/engine-extension/native-unpublished-element-runtime-contract.json']
    pins = {str(p): digest(p) for p in paths}
    assert digest(SOURCE) == SHA
    pe = pefile.PE(str(SOURCE)); md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    entries = sorted((e.struct.BeginAddress,e.struct.EndAddress,e.struct.UnwindData)
                     for e in pe.DIRECTORY_ENTRY_EXCEPTION)
    starts = [e[0] for e in entries]
    def decode(a,z):
        data=pe.get_data(a,z-a);ins=list(md.disasm(data,BASE+a))
        assert sum(i.size for i in ins)==len(data)
        return ins
    spans = [(0x5ce3f0,0x5ceaca),(0x5cf3c0,0x5cfb4c),(0x5e5b30,0x5e5cdb),
             (0x70a4f0,0x70a5a9),(0x7084a0,0x7085b7),(0x5cd130,0x5cd767),
             (0x5d8220,0x5d82aa),(0x5d8740,0x5d87cf),(0x5d7df0,0x5d8018),
             (0x5d87d0,0x5d8839),(0x5d8390,0x5d860a),(0x645820,0x645844)]
    decoded = {a:decode(a,z) for a,z in spans}
    instructions = {i.address-BASE:i for body in decoded.values() for i in body}
    def exact(a,raw): assert instructions[a].bytes.hex()==raw,(hex(a),instructions[a].bytes.hex())
    exact(0x5e5b7b,'e87088feff'); exact(0x5e5b80,'488906')
    exact(0x5ceab0,'e80b090000'); exact(0x5ceab6,'488bc3')
    exact(0x5cd185,'44896910'); exact(0x5cd171,'e8aaae0000')
    exact(0x7084f6,'e8354cecff')
    # Validate direct-E8 candidates against full enclosing .pdata decode.
    section=pe.get_section_by_rva(0x1000);text=section.get_data();rva=section.VirtualAddress
    targets={a:[] for a in (0x5ce3f0,0x5e5b30,0x5cd130,0x5cf3c0)}
    cache={}
    for offset in range(len(text)-4):
        if text[offset]!=0xe8: continue
        address=rva+offset;target=address+5+struct.unpack_from('<i',text,offset+1)[0]
        if target not in targets: continue
        j=bisect.bisect_right(starts,address)-1
        assert j>=0 and address<entries[j][1]
        a,z,_=entries[j]
        if a not in cache: cache[a]={i.address-BASE:i for i in decode(a,z)}
        ins=cache[a].get(address)
        assert ins and ins.mnemonic=='call' and ins.op_str==hex(BASE+target)
        targets[target].append(row(ins))
    assert [int(i['address'],16)-BASE for i in targets[0x5ce3f0]]==[0x5e5b7b]
    assert [int(i['address'],16)-BASE for i in targets[0x5cd130]]==[0x7084f6]
    bindings=json.loads(paths[3].read_text())
    create=next(v for v in bindings['contracts'] if v['entryAddress']=='0x1807084a0')
    new=next(v for v in bindings['excludedEntries'] if v['entryAddress']=='0x18070a4f0')['bindings'][0]
    for binding in (create,new):
        for i in binding['nativeRegistrationInstructions']:
            assert pe.get_data(int(i['address'],16)-BASE,len(bytes.fromhex(i['bytes']))).hex()==i['bytes']
    re=Replay(pe);cases=[]
    # Native metadata suffixes run AFTER (not instead of claiming) constructors.
    # Payload constructor execution was independently tested in the separate
    # unpublished-element suite; this fixture tests metadata stores only.
    for pool in (0,1):
        cases.append(dict(case='Agent pool post-construction metadata',pool=pool,
            result=re.run(0x5d825f,0x5d829c,rbx=re.mission+POOLS[pool][0])))
    cases.append(dict(case='230 Mission post-construction metadata',pool=2,
        result=re.run(0x5ce4a5,0x5ce4ea,rbx=re.mission,r12=0)))
    for pool in (3,4):
        cases.append(dict(case='full native 2c8 pool constructor',pool=pool,
            result=re.run(0x5d8740,rcx=re.mission+POOLS[pool][0])))
    assert re.empty()
    # All inactive: exact reset helpers must not enter resource-release paths.
    for pool,entry in ((0,0x5d7df0),(2,0x5d87d0),(3,0x5d8390),(4,0x5d8390)):
        result=re.run(entry,rcx=re.mission+POOLS[pool][0])
        assert not any(i.group(capstone.CS_GRP_CALL) and i.address in re.executed for i in decoded[entry])
        assert re.empty()
        cases.append(dict(case='complete helper reset on inactive records',pool=pool,result=result))
    # The first CreateAgent prefix, on either branch, has touched only stack.
    for index in (-1,0,2039):
        result=re.run(0x5cd130,0x5cd168,forced_index=index,rcx=re.mission,rdx=7,r8=0,r9=0)
        assert not re.writes and re.u.reg_read(x.UC_X86_REG_R12)==re.mission
        assert re.u.reg_read(x.UC_X86_REG_RDI)==re.mission+0x2d50
        assert re.u.reg_read(x.UC_X86_REG_R13)==index&((1<<64)-1)
        cases.append(dict(case='CreateAgent prefix before allocation branch',forcedIndex=index,result=result))
    # Reject every inspected nonempty category, including negative noncanonical
    # headers, rather than interpreting all negative values as stock -1.
    negatives=[]
    for pool,(offset,stride,header,table,count) in enumerate(POOLS):
        for kind,at,size in [('header',offset+i*stride+header,4) for i in (0,1019,2039)]+[
                           ('link',offset+table+i*2,2) for i in (0,1019,2039)]+[
                           ('count',offset+count,4),('highwater',offset+count+4,4)]:
            address=re.mission+at;old=bytes(re.u.mem_read(address,size))
            re.u.mem_write(address,(b'\xfe\xff\xff\xff' if kind=='header' else b'\x01'+b'\0'*(size-1)))
            assert not re.empty();re.u.mem_write(address,old)
            negatives.append(dict(pool=pool,kind=kind,missionOffset=hex(at)))
    assert len(negatives)==40 and re.empty()
    assert pins=={str(p):digest(p) for p in paths}
    return dict(schemaVersion=1,status='bounded-empty-attach-candidates-no-hook-approval',sources=pins,
        stockCapacity=CAPACITY,stockPoolGeometry=[dict(pool=n,baseOffset=hex(a),stride=hex(b),headerOffset=hex(c),tableOffset=hex(d),countOffset=hex(e),highwaterOffset=hex(e+4)) for n,(a,b,c,d,e) in enumerate(POOLS)],
        bodies={hex(a):dict(endRva=hex(z),instructions=list(map(row,decoded[a]))) for a,z in spans},
        directTextCallers={hex(k):v for k,v in targets.items()},
        registration=[{k:b[k] for k in ('entryAddress','nativeRegistrationInstructions','callbackBindings','signature','entryRegister')} for b in (create,new)],
        nativeReplay=cases,initialMissionFixturePoison='0xa5',emptyGateNegativeCases=negatives,
        constructorOrdering=['5ce470/47d constructs both Agent pools; 5ce483..4e3 initializes230; 5ce4f1/4fe constructs both2c8 pools.',
            '5ceab0 calls5cf3c0; only after that call returns does5ceab6 restore Mission inRAX and5ceac9 return.',
            'Reset5cf3c0 calls primary reset5d7df0 at5cf6e6,230 reset5d87d0 at5cf6f2,A/B reset5d8390 at5cf6fe/70a. Secondary was initialized by constructor; this reset sequence does not reset it.',
            '5e5b7b constructor return reaches5e5b80 before wrapper first stores pointer to caller output[RSI]; reference-count calls and global container publication follow.',
            'Registered CreateMission471 enters70a4f0, calls5e5b30 at70a527 and eventually returns MissionRAX. Registered CreateAgent470 enters7084a0 with hidden resultRCX and MissionRDX, then calls5cd130 with MissionRCX.'],
        candidates=[dict(rva='0x5e5b80',state='constructor/reset returned; RAX is new Mission; RSI is caller result pointer; RDI is owning container',
            advantage='Before this wrapper publishes new pointer or acquires its first reference.',
            approval=False,remaining='Prove no constructor/subconstructor/reset callback leaked or used Mission concurrently; implement failure/lifetime policy without throwing into unverified native EH state.'),
            dict(rva='0x5cd168',state='R12=Mission; RDI=primary pool; R13=sign-extended requested index; only stack stores have occurred in this function',
            advantage='Common path before automatic allocation5cd171 and explicit header store5cd185/virtual reset5cd18c.',
            approval=False,remaining='Mission is already published. First CreateAgent observation and an empty snapshot do not prove a worker fence or absence of secondary allocations/reentrant calls.')],
        failClosedRequirements=['Exact original or separately proven patched geometry and full readable lifetime-owned Mission allocation; never infer it from counts or a numerical pointer alone.',
            'All five exact headers=-1, all signed-short links=-1, all five counts/highwaters=0. Check primary and secondary independently.',
            'Recognized completed constructor/lifecycle token, unique sidecar identity plus generation, and no concurrent native mutation through atomic commit. A repeated or reentrant attach must fail/defer, not publish twice.',
            'Borrow existing records without construction, destruction or freeing; retain native owner through readers and detach sidecar before native Mission destruction/reset/reuse.',
            'On invalid state or allocation failure do not publish partial storage. Preserve stock behavior only when every associated routing hook can also remain disabled for that Mission.',
            'Do not return -1 from CreateAgent as an improvised failure:7084fb..511 immediately multiplies returned index and derives a pointer with no failure guard.'],
        unresolved=['No complete transitive no-escape/reentrancy proof for Mission constructor or reset. Constructor calls subobject constructors, mutex initialization, global cleanup and potential indirect callbacks; these were not modeled as harmless.',
            'No native worker/join or exclusion barrier established by either candidate. Empty all-five metadata is necessary but not a concurrency proof.',
            'Direct-E8 inventory excludes indirect calls, function-pointer aliases and data references; sole decoded direct caller is not a whole-program exclusivity claim.',
            'Metadata suffix and inactive-reset replay does not execute full Mission construction/reset, native publication, actual hook unwind or C++ failure handling.',
            'First CreateAgent may occur after other native allocations or reset reuse; require explicit lifecycle identity and fail closed if populated.',
            'All five engine allocation/free/consumer paths still require coordinated routing and wide metadata before live growth.'],
        enginePatchApproved=False,liveAdoptionApproved=False,gameLaunched=False)


if __name__=='__main__':
    report=build();output=ROOT/'evidence/engine-extension/native-empty-adoption-boundary.json'
    output.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(dict(path=str(output),sha256=digest(output),status=report['status'],nativeCases=len(report['nativeReplay']),negativeCases=len(report['emptyGateNegativeCases']))))
