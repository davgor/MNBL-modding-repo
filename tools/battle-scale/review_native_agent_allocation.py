"""Independent source-bound review of the original allocator, with adversarial replay.

Never invokes or rewrites the accepted producer. This executes original instructions
in Unicorn synthetic memory only; no DLL loading, engine activation or live hooks.
"""
from pathlib import Path
import hashlib
import json
import random
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ
from unicorn import x86_const as x
from patch_native_copy import safe_review_output

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
PRODUCER = ROOT / 'tools/battle-scale/replay_native_agent_allocation.py'
REPLAY = ROOT / 'evidence/engine-extension/native-agent-allocation-replay.json'
STORAGE = ROOT / 'src/MNBL.AgentStorage/AgentDomainStorage.hpp'
OUT = ROOT / 'evidence/engine-extension/native-agent-allocation-independent-review.json'
EXPECTED = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE, POOL, STACK, STOP = 0x180000000, 0x60000000, 0x50000000, 0x190000000
CAP, STRIDE = 2040, 0xc60
TABLE, COUNT, HIGH, SIZE = 0x629d00, 0x62acf0, 0x62acf4, 0x62acf8
ALLOC, RESET, VT = 0x5d8020, 0x607390, 0xb1d990


def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()


def links(live):
    result = [-1] * CAP
    ordered = sorted(live)
    for first, second in zip(ordered, ordered[1:] + [CAP + 1]): result[first] = second
    return struct.pack('<2040h', *result)


def main():
    protected = [SOURCE, PRODUCER, REPLAY, STORAGE, Path(__file__)]
    pins = {str(p.relative_to(ROOT)).replace('\\', '/'): sha(p) for p in protected}
    data = SOURCE.read_bytes()
    assert hashlib.sha256(data).hexdigest() == EXPECTED
    old = json.loads(REPLAY.read_text())
    assert old['binarySha256'] == EXPECTED and old['toolSha256'] == sha(PRODUCER)
    assert len(old['cases']) == 42 and sum(r['resetCalls'] for r in old['cases']) == 40
    pe = pefile.PE(data=data)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
    entries = [e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if ALLOC <= e.struct.BeginAddress < 0x5d81d5]
    assert [(e.BeginAddress, e.EndAddress) for e in entries] == [(ALLOC, 0x5d8062), (0x5d8062, 0x5d818e), (0x5d818e, 0x5d81d5)]
    ctor = pe.get_data(0x606a6a, 10)
    ci = list(md.disasm(ctor, BASE + 0x606a6a))
    assert len(ci) == 2 and ci[0].bytes.hex() == '488d051f6f5100' and ci[1].bytes.hex() == '488901'
    assert ci[0].address + ci[0].size + ci[0].operands[1].mem.disp == BASE + VT
    assert struct.unpack('<Q', pe.get_data(VT + 0x68, 8))[0] == BASE + RESET
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    insns = {}; pages = set(); raw_rows = []
    for body in old['nativeBodies']:
        start, end = int(body['rva'], 16), int(body['endRva'], 16)
        assert (start, end) in [(ALLOC, 0x5d81d5), (RESET, 0x6073ae)]
        raw = pe.get_data(start, end-start)
        assert raw.hex() == body['bytes']
        decoded = list(md.disasm(raw, BASE + start))
        assert sum(i.size for i in decoded) == len(raw)
        for page in range((BASE+start)&~4095, (BASE+end+4095)&~4095, 4096):
            if page not in pages:
                pages.add(page); cpu.mem_map(page, 4096); cpu.mem_write(page, b'\xcc'*4096)
        cpu.mem_write(BASE+start, raw)
        for i in decoded:
            insns[i.address] = i
            raw_rows.append({'rva': hex(i.address-BASE), 'bytes': i.bytes.hex(), 'instruction': i.mnemonic+' '+i.op_str})
    assert [i.address-BASE for i in insns.values() if i.mnemonic == 'call'] == [0x5d815e]
    cpu.mem_map((BASE+VT)&~4095, 4096); cpu.mem_write(BASE+VT+0x68, pe.get_data(VT+0x68, 8))
    cpu.mem_map(POOL, (SIZE+4095)&~4095)
    cpu.mem_map(STACK, 0x10000); cpu.mem_map(STOP, 4096)
    state = {}; observed = set(); rows = []

    def on_code(u, pc, size, _):
        assert pc in insns, hex(pc)
        state['executed'].add(pc); observed.add(pc)
        if pc == BASE+RESET:
            index = state['index']; assert index >= 0
            assert u.reg_read(x.UC_X86_REG_RCX) == POOL+index*STRIDE
            assert u.reg_read(x.UC_X86_REG_RSP) % 16 == 8
            assert bytes(u.mem_read(POOL, SIZE)) == state['atReset']
            state['resets'] += 1

    def on_mem(u, access, address, size, value, _):
        if STACK <= address and address+size <= STACK+0x10000: return
        if access == UC_MEM_READ:
            assert POOL <= address and address+size <= POOL+SIZE or address == BASE+VT+0x68 and size == 8
            return
        index = state['index']
        # Exhaustion must perform no pool write at all, even a same-value store.
        assert index >= 0
        allowed = [(TABLE, TABLE+CAP*2), (COUNT, COUNT+4), (HIGH, HIGH+4)]
        allowed += [(index*STRIDE+a, index*STRIDE+a+n) for a,n in [(0x10,4),(0x908,8),(0xb64,1),(0x558,8)]]
        assert any(POOL+a <= address and address+size <= POOL+b for a,b in allowed)
        state['writes'] += 1

    cpu.hook_add(UC_HOOK_CODE, on_code)
    cpu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, on_mem)
    rng = random.Random(0x5D8020)
    scenarios = []
    # Directed insertion branches: before first, between adjacent/nonadjacent
    # occupied positions, beyond highwater, and both ends of the physical array.
    for hole in (0,1,2,31,32,63,64,65,127,128,255,256,511,512,1023,1024,2038,2039):
        scenarios.append(('single-hole-'+str(hole), set(range(CAP))-{hole}, {}))
    for live in (set(), {1}, {2,3}, {63,65}, {0,2,4}, {0,2039}, set(range(0,CAP,2)), set(range(1,CAP,2))):
        scenarios.append(('directed-'+str(len(scenarios)), live, {}))
    for n in range(32):
        scenarios.append(('random-'+str(n), set(rng.sample(range(CAP), rng.randrange(CAP))), {}))
    # Repeated original allocations reuse their own resulting bytes (not a fresh
    # canonical serialization), detecting chain corruption across successive calls.
    for n in (0,1,63,64,1023,2039):
        scenarios.append(('sequential-'+str(n), set(range(CAP))-set(range(n,min(CAP,n+9))), {'repeat':10}))
    scenarios += [('full', set(range(CAP)), {}),
                  ('negative-header-minus2', set(range(CAP))-{64}, {'negative':-2}),
                  ('negative-header-intmin', set(range(CAP))-{2039}, {'negative':-(1<<31)}),
                  ('noncanonical-positive-headers', {0,2,2039}, {'positive':0x7fffffff}),
                  ('stale-highwater', {0,2}, {'high':CAP}),
                  ('incorrect-count', {0,2}, {'count':123}),
                  ('full-malformed-table-count-high', set(range(CAP)), {'corruptFull':True})]
    original_rng = random.Random(0xA110CA7E)
    original_scenarios = [set(),set(range(CAP)),{0},{CAP-1},{0,CAP-1},set(range(100)),set(range(1,CAP)),set(range(CAP-1))]
    original_scenarios += [set(range(CAP))-{hole} for hole in (1,63,64,1023,1024,2038)]
    original_scenarios += [{i for i in range(CAP) if original_rng.randrange(100)<p} for p in (1,5,25,50,75,95,99)]
    for ordinal, historical in enumerate(old['cases']):
        assert historical['scenario'] == ordinal//2 and historical['poison'] == (0x35,0xcd)[ordinal%2]
        scenarios.append(('historical-'+str(ordinal), original_scenarios[ordinal//2],
                          {'poison':historical['poison'],'historical':historical}))
    nonvol = [getattr(x,'UC_X86_REG_'+n) for n in ('RBX','RBP','RSI','RDI','R12','R13','R14','R15')]
    xmm = [getattr(x,'UC_X86_REG_XMM'+str(n)) for n in range(6,16)]
    for name, live_initial, options in scenarios:
        live = set(live_initial)
        blob = bytearray([options.get('poison',0x96)])*SIZE
        for i in range(CAP):
            struct.pack_into('<Q',blob,i*STRIDE,BASE+VT)
            struct.pack_into('<i',blob,i*STRIDE+0x10,options.get('positive',i) if i in live else options.get('negative',-1))
        blob[TABLE:TABLE+CAP*2] = links(live)
        struct.pack_into('<ii',blob,COUNT,options.get('count',len(live)),options.get('high',max(live)+1 if live else 0))
        if options.get('corruptFull'):
            blob[TABLE:TABLE+CAP*2] = bytes([0xf3])*(CAP*2)
            struct.pack_into('<ii',blob,COUNT,-123,-456)
        for step in range(options.get('repeat',1)):
            index = next((i for i in range(CAP) if i not in live),-1)
            expected = bytearray(blob); before_count,before_high = struct.unpack_from('<ii',blob,COUNT)
            after = live|{index} if index>=0 else live
            if index>=0:
                struct.pack_into('<i',expected,index*STRIDE+0x10,index)
                expected[TABLE:TABLE+CAP*2] = links(after)
                struct.pack_into('<i',expected,COUNT,before_count+1)
            at_reset = bytes(expected)
            if index>=0:
                struct.pack_into('<Q',expected,index*STRIDE+0x908,0)
                expected[index*STRIDE+0xb64] = 0
                struct.pack_into('<q',expected,index*STRIDE+0x558,-1)
                struct.pack_into('<i',expected,HIGH,max(before_high,index+1))
            state.update(index=index, atReset=at_reset, resets=0, writes=0, executed=set())
            cpu.mem_write(POOL,bytes(blob))
            rsp=STACK+0x8008
            cpu.mem_write(rsp-0x200,b'\x73'*0x400); cpu.mem_write(rsp,struct.pack('<Q',STOP))
            for k,r in enumerate(nonvol): cpu.reg_write(r,0x1234567890000000+k)
            for k,r in enumerate(xmm): cpu.reg_write(r,0x123456789abcdef00123456789abc000+k)
            cpu.reg_write(x.UC_X86_REG_EFLAGS,0x202 | (0x41 if step%2 else 0))
            cpu.reg_write(x.UC_X86_REG_RAX,0xffffffffffffffff)
            cpu.reg_write(x.UC_X86_REG_RSP,rsp); cpu.reg_write(x.UC_X86_REG_RCX,POOL)
            cpu.emu_start(BASE+ALLOC,STOP,count=500000)
            assert cpu.reg_read(x.UC_X86_REG_RIP)==STOP
            assert cpu.reg_read(x.UC_X86_REG_RSP)==rsp+8
            assert cpu.reg_read(x.UC_X86_REG_RAX)==index&0xffffffff
            assert state['resets']==int(index>=0)
            assert all(cpu.reg_read(r)==0x1234567890000000+k for k,r in enumerate(nonvol))
            assert all(cpu.reg_read(r)==0x123456789abcdef00123456789abc000+k for k,r in enumerate(xmm))
            actual=bytes(cpu.mem_read(POOL,SIZE))
            assert actual==expected,(name,step,next(i for i,(a,b) in enumerate(zip(actual,expected)) if a!=b))
            if 'historical' in options:
                historical=options['historical']
                assert hashlib.sha256(actual).hexdigest()==historical['fullPoolSha256']
                assert index==historical['allocatedIndex'] and state['resets']==historical['resetCalls']
                assert [hex(i-BASE) for i in sorted(state['executed'])]==historical['executedInstructionRvas']
            rows.append({'name':name,'step':step,'beforeCount':before_count,'beforeHighwater':before_high,
                         'allocatedIndex':index,'resetCalls':state['resets'],'nativePoolWrites':state['writes'],
                         'afterSha256':hashlib.sha256(actual).hexdigest(),
                         'instructionRvas':[hex(i-BASE) for i in sorted(state['executed'])]})
            blob=bytearray(actual);live=after
    # All 42 historical cases were independently reconstructed and replayed above;
    # the producer itself was neither invoked nor altered.
    assert all(BASE+int(rva,16) in insns for r in old['cases'] for rva in r['executedInstructionRvas'])
    result = {
        'schemaVersion':1,'status':'accepted-bounded-primary-allocation-replay',
        'reviewedSources':pins,'nativeSourceSha256':EXPECTED,'findings':[],
        'acceptedReplayCases':42,'acceptedResetCalls':40,'historicalCasesIndependentlyMatched':42,
        'independentCases':len(rows),'independentResetCalls':sum(r['resetCalls'] for r in rows),
        'instructionCount':len(insns),'observedInstructionCount':len(observed),
        'unobservedInstructionRvas':[hex(i-BASE) for i in sorted(set(insns)-observed)],
        'provenance':{'constructorVtableStoreRva':'0x606a6a','bytes':ctor.hex(),
                      'vtableSlotRva':hex(VT+0x68),'targetRva':hex(RESET),
                      'runtimeRanges':[[hex(e.BeginAddress),hex(e.EndAddress)] for e in entries]},
        'instructions':raw_rows,'adversarialCases':rows,
        'observations':[
            'All original bytes matched. Exact sole virtual reset target derives from the stock constructor vtable store, not a guessed function.',
            'Lowest signed-negative header wins; -2 and INT_MIN are also considered free. Nonnegative headers are occupied without index-equality validation.',
            'Full pool returns EAX=0xffffffff (RAX zero-extended) with zero native pool writes and zero reset calls, even if metadata is malformed.',
            'At virtual reset entry, count/header/sorted-successor table are committed but highwater is still its old value. This is not transactional publication.',
            'Success increments the prior count without reconciling it; highwater becomes max(prior highwater,index+1), retaining a stale upper bound.',
            'Default reset clears qword+908 and byte+b64, writes qword+558=-1; no construction/destruction or heap allocation occurs.',
            'Whole synthetic pool bytes, write footprints, return stack, all nonvolatile GPRs and XMM6..15 matched independent expectations.'
        ],
        'requiredStorageSemantics':[
            'A native-compatible allocation policy must choose the globally lowest free physical/logical index. Current LIFO release and grow-prepended free blocks do not preserve that policy. Add a separate lowest-free acquisition API or replace the policy deliberately, with regressions for older holes after growth and out-of-order release.',
            'Acquisition must perform the reviewed per-Agent reset, set its native active header, and publish synchronized count/highwater/successor metadata. AgentDomainStorage currently changes sidecar occupancy only; returning a handle is not native activation.',
            'Stock per-pool virtual reset can observe already-committed header/count/table and old highwater. Preserve this order for compatibility or constrain and prove a different callback/publication contract. Do not invoke unknown reentrant callbacks while holding the storage mutex.',
            'Preserve independent primary and secondary occupancy. This proof concerns only the c60 allocator; it does not establish companion allocation coupling or all SpawnAgent side effects.',
            'Exhaustion must leave records/metadata unchanged, and public callers must handle failure before index arithmetic. A storage no_slot result is not automatically compatible with unchecked stock CreateAgent.',
            'Use wide sidecar successor/index state only after routing all consumers; the original signed-short table and capacity+1 terminator remain finite. No direct wide-index native compatibility follows from this replay.',
            'Free/occupied sidecar state and native headers must be reconciled under an engine quiescence contract before adoption and thereafter updated together. Reject inconsistent imports rather than assuming the native allocator validates metadata.'
        ],
        'limitations':[
            'Unicorn emulation only; no game, copied native Windows execution, activation, concurrent readers or engine thread state.',
            'Only stock default vtable/reset; alternate dynamic vtables, exceptions and reset reentrancy remain unproved.',
            'Directed/random/sequential valid chains plus selected malformed states are bounded tests, not an exhaustive proof for all subsets or arbitrary corrupt successor chains.',
            'Three original pdata ranges were retained and decoded; this does not test Windows unwinding or certify hook entry/incoming control-flow closure.',
            'Original capacity 2040 and stride c60 only. No removal behavior, companion pools, wider indices, live growth or engine patch is approved.'
        ],'gameLaunched':False,'enginePatchApproved':False}
    assert pins == {str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in protected}
    safe_review_output(OUT,protected).write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:result[k] for k in ('status','independentCases','independentResetCalls','instructionCount','observedInstructionCount')}))


if __name__ == '__main__': main()
