"""Phase-sensitive companion occupancy evidence; no snapshot approval."""
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
BODIES = {0x5cd130: 0x5cd767, 0x694630: 0x694858, 0x672620: 0x67291e,
          0x695300: 0x6953a9, 0x5d94a0: 0x5d95eb, 0x5d9350: 0x5d949b,
          0x5d0837: 0x5d08f1, 0x694860: 0x6948b8,
          0x5d87d0: 0x5d8839}


def digest(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def row(i):
    return {'address': hex(i.address), 'bytes': i.bytes.hex(), 'mnemonic': i.mnemonic, 'operands': i.op_str}


class Replay:
    pool, stack, payload, agent, stop = 0x50000000, 0x40000000, 0x60000000, 0x61000000, 0x70000000
    def __init__(self, pe):
        self.u = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
        for first, size in ((BASE, (pe.OPTIONAL_HEADER.SizeOfImage + 4095) & ~4095),
                            (self.pool, 0x120000), (self.stack, 0x10000),
                            (self.payload, 0x1000), (self.agent, 0x1000), (self.stop, 0x1000)):
            self.u.mem_map(first, size)
        self.u.mem_write(BASE, pe.get_memory_mapped_image())
        self.u.mem_write(self.stop, b'\xc3')
        self.reads = []
        self.u.hook_add(unicorn.UC_HOOK_CODE, lambda u, pc, n, arg: u.emu_stop() if pc == self.stop else None)
        self.u.hook_add(unicorn.UC_HOOK_MEM_READ, lambda u, access, address, size, value, arg:
                        self.reads.append((address, size)))
    def write32(self, address, value):
        self.u.mem_write(address, struct.pack('<I', value & 0xffffffff))
    def read32(self, address):
        return struct.unpack('<i', self.u.mem_read(address, 4))[0]
    def run(self, rva, end=None, **registers):
        self.reads.clear()
        self.u.reg_write(x.UC_X86_REG_RSP, self.stack + 0x8008)
        self.u.mem_write(self.stack + 0x8008, struct.pack('<Q', self.stop))
        self.u.reg_write(x.UC_X86_REG_EFLAGS, 0x202)
        for name, value in registers.items():
            self.u.reg_write(getattr(x, 'UC_X86_REG_' + name.upper()), value & ((1 << 64) - 1))
        self.u.emu_start(BASE + rva, BASE + end if end else self.stop + 1, count=1000000)
        assert self.u.reg_read(x.UC_X86_REG_RIP) == (BASE + end if end else self.stop)


def build():
    assert digest(SOURCE) == SHA
    paths = [SOURCE, Path(__file__), ROOT / 'src/MNBL.AgentStorage/NativePoolSnapshot.hpp',
             ROOT / 'evidence/engine-extension/native-pool-index-chain-replay.json',
             ROOT / 'evidence/engine-extension/native-live-pool-growth-contract.json']
    pins = {str(p): digest(p) for p in paths}
    pe = pefile.PE(str(SOURCE))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    bodies, decoded = {}, {}
    for first, end in BODIES.items():
        raw = pe.get_data(first, end - first)
        ins = list(md.disasm(raw, BASE + first))
        assert sum(i.size for i in ins) == len(raw)
        decoded[first] = ins
        bodies[hex(first)] = {'endRva': hex(end), 'sha256': hashlib.sha256(raw).hexdigest(),
                             'instructions': list(map(row, ins))}
    # Full initializer write intervals: both are call-free and branch-free.
    initializer_writes = {}
    for entry, header, stride in ((0x694630, 0x1c, 0x230), (0x672620, 0, 0x2c8)):
        writes = []
        for ins in decoded[entry]:
            assert not ins.group(capstone.CS_GRP_CALL) and not ins.group(capstone.CS_GRP_JUMP)
            if ins.mnemonic.startswith('mov') and ins.operands and ins.operands[0].type == capstone.x86.X86_OP_MEM:
                op = ins.operands[0]
                assert ins.reg_name(op.mem.base) == 'rcx' and not op.mem.index
                assert 0 <= op.mem.disp and op.mem.disp + op.size <= stride
                writes.append({'instruction': row(ins), 'offset': op.mem.disp, 'size': op.size,
                               'overlapsHeader': op.mem.disp < header + 4 and header < op.mem.disp + op.size})
        initializer_writes[hex(entry)] = writes
    assert sum(w['overlapsHeader'] for w in initializer_writes['0x694630']) == 1
    assert not any(w['overlapsHeader'] for w in initializer_writes['0x672620'])
    assert next(i for i in decoded[0x694630] if i.address == BASE + 0x694632).bytes.hex() == 'c7411cffffffff'
    #695300 does not restore1c on any decoded branch: every store is elsewhere.
    later_stores = []
    for ins in decoded[0x695300]:
        assert not ins.group(capstone.CS_GRP_CALL)
        if ins.mnemonic.startswith('mov') and ins.operands[0].type == capstone.x86.X86_OP_MEM:
            op = ins.operands[0]
            assert op.mem.disp in (0x1c4, 0x1c9, 0x20c)
            later_stores.append(row(ins))
    replay = Replay(pe)
    cases = []
    for index in (0, 17, 2039, 32765):
        replay.u.mem_write(replay.payload, b'\xa5' * 0x1000)
        replay.write32(replay.payload + 0x1c, index)
        replay.run(0x694630, rcx=replay.payload)
        assert replay.read32(replay.payload + 0x1c) == -1
        replay.u.mem_write(replay.agent, b'\0' * 0x1000)
        replay.write32(replay.agent + 0x560, 1)
        replay.run(0x695300, rcx=replay.payload, rdx=replay.agent)
        assert replay.read32(replay.payload + 0x1c) == -1
        # Exact writer prefix on R8b=true path: no helper call or publication.
        replay.write32(replay.agent + 0x10, index)
        replay.run(0x694860, 0x6948b8, rcx=replay.payload, rdx=replay.agent, r8=1)
        assert replay.read32(replay.payload + 0x1c) == index
        replay.u.mem_write(replay.payload, b'\xa5' * 0x1000)
        replay.write32(replay.payload, index)
        replay.run(0x672620, rcx=replay.payload)
        assert replay.read32(replay.payload) == index
        cases.append({'index': index, '230After694630': -1, '230After695300State1': -1,
                      '230After694860WriterPrefix': index, '2c8HeaderAfter672620': index, 'passed': True})
    # Execute actual contiguous CreateAgent companion registration instructions,
    # including the real5d94a0 helper and counter/highwater writes. Other Agent
    # constructor/activation calls are not stubbed or claimed to execute.
    replay.u.mem_write(replay.pool, b'\0' * 0x120000)
    replay.u.mem_write(replay.pool + 0x116e80, b'\xff' * (2040 * 2))
    for index in range(2040):
        replay.write32(replay.pool + index * 0x230 + 0x1c, -1)
    live = []
    registrations = []
    for index in (2039, 0, 17, 3, 1024):
        record = replay.pool + index * 0x230
        replay.run(0x5cd1f3, 0x5cd222, rax=record, rcx=replay.pool,
                   r12=replay.pool - 0xc58750, r13=index)
        pool_reads = [(a - replay.pool, n) for a, n in replay.reads if replay.pool <= a < replay.pool + 0x120000]
        assert all(a >= 0x116e80 for a, _ in pool_reads), 'Insertion unexpectedly reads payload'
        assert replay.read32(record + 0x1c) == index
        live.append(index);live.sort()
        assert replay.read32(replay.pool + 0x117e70) == len(live)
        assert replay.read32(replay.pool + 0x117e74) == max(live) + 1
        links = list(struct.unpack('<2040h', replay.u.mem_read(replay.pool + 0x116e80, 4080)))
        expected = [-1] * 2040
        for offset, slot in enumerate(live):
            expected[slot] = live[offset + 1] if offset + 1 < len(live) else 2041
        assert links == expected
        replay.run(0x694630, rcx=record)
        assert replay.read32(record + 0x1c) == -1
        assert list(struct.unpack('<2040h', replay.u.mem_read(replay.pool + 0x116e80, 4080))) == links
        assert all(replay.read32(replay.pool + slot * 0x230 + 0x1c) == -1 for slot in live)
        registrations.append({'index': index, 'tableOccupied': live.copy(), 'count': len(live),
                              'highwater': max(live) + 1, 'allRegisteredPayloadHeadersAfterReset': -1,
                              'insertionPoolReadsOnlyMetadata': True, 'passed': True})
    # Deliberately contrasting header states isolate the native highwater rule.
    # The reset-header case is NOT asserted to be a supported removal phase.
    removals = []
    for restored in (False, True):
        replay.u.mem_write(replay.pool, b'\0' * 0x120000)
        links = [-1] * 2040;links[3] = 17;links[17] = 2041
        replay.u.mem_write(replay.pool + 0x116e80, struct.pack('<2040h', *links))
        for index in range(2040):
            replay.write32(replay.pool + index * 0x230 + 0x1c, index if restored and index in (3, 17) else -1)
        replay.write32(replay.pool + 0x117e70, 2);replay.write32(replay.pool + 0x117e74, 18)
        replay.run(0x5d0837, 0x5d08f1, rbp=17, rdi=17, rsi=replay.pool - 0xc58750,
                   r14=-1, r10=16, r9=16, r11=18)
        after = list(struct.unpack('<2040h', replay.u.mem_read(replay.pool + 0x116e80, 4080)))
        assert [i for i, link in enumerate(after) if link >= 0] == [3]
        assert replay.read32(replay.pool + 0x117e70) == 1
        highwater = replay.read32(replay.pool + 0x117e74)
        assert highwater == (4 if restored else 0)
        removals.append({'survivorHeaderRestored': restored, 'tableOccupied': [3], 'count': 1,
                         'highwater': highwater, 'supportedEnginePhaseClaimed': False,
                         'headersConsistentWithMembership': restored,
                         'scope': 'Exact removal slice with explicit incoming register contract; no full engine removal execution.'})
    # Exact-displacement scan within retained pdata boundaries. This finds
    # metadata uses, not every computed alias or all indirect callers.
    text = next(s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text')
    raw_text = text.get_data();entries = [e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION]
    starts = [e.BeginAddress for e in entries]
    matches = {}
    for displacement in (0x117e70, 0x117e74, 0x116e80, 0xd705c0, 0xd705c4, 0xd6f5d0):
        needle = struct.pack('<I', displacement);pos = raw_text.find(needle)
        while pos >= 0:
            rva = text.VirtualAddress + pos;n = bisect.bisect_right(starts, rva) - 1
            if n >= 0 and entries[n].BeginAddress <= rva < entries[n].EndAddress:
                e = entries[n]
                for ins in md.disasm(pe.get_data(e.BeginAddress, e.EndAddress - e.BeginAddress), BASE + e.BeginAddress):
                    if ins.address <= BASE + rva < ins.address + ins.size and any(
                        op.type == capstone.x86.X86_OP_MEM and op.mem.disp == displacement for op in ins.operands):
                        matches[ins.address] = {'runtimeRangeStart': hex(BASE + e.BeginAddress), 'instruction': row(ins)}
            pos = raw_text.find(needle, pos + 1)
    contexts = {}
    for first, end in ((0x5cd1d9, 0x5cd2b4), (0x5cd571, 0x5cd598),
                       (0x5cf54f, 0x5cf5a0), (0x5cfc4e, 0x5cfca1),
                       (0x5eb9b2, 0x5eba02), (0x6043fa, 0x604419),
                       (0x69f255, 0x69f26c), (0x603e80, 0x603ea5)):
        ins = list(md.disasm(pe.get_data(first, end-first), BASE+first))
        assert sum(i.size for i in ins) == end-first
        contexts[hex(first)] = list(map(row, ins))
    for path, sha in pins.items():
        assert digest(path) == sha
    return {
        'schemaVersion': 1, 'status': 'phase-sensitive-230-header-contract-snapshot-adoption-blocked',
        'binarySha256': SHA, 'dependencies': [{'path': p, 'sha256': sha} for p, sha in pins.items()],
        'nativeBodies': bodies, 'callerAndResetContexts': contexts,
        'initializerWriteIntervals': initializer_writes, '695300Stores': later_stores,
        'actualHelperReplay': cases, 'actualRegistrationAndResetReplay': registrations,
        'actualRemovalHighwaterDependencyReplay': removals,
        'exactDisplacementMetadataUses': [matches[a] for a in sorted(matches)],
        'conclusions': [
            'CreateAgent registers230 payload+1c=index and short table/count/highwater at5cd1f3..5cd222, but later694630 resets that same payload header to-1. Actual original registration and reset bytes reproduce nonempty valid link metadata with reset payload headers.',
            '694860 at6948b5 restores230+1c from Agent+10 as part of per-Agent data refresh.695300 never restores this field; its complete stores are+1c4,+1c9 and conditional target+20c.',
            '694860 also initializes a stack-local230 temporary in5eb9f9; therefore the field alone does not identify pool occupancy or even pool membership.69f267 refreshes Agent+908 when nonnull;604404 uses the incoming companion argument saved atentryRSP10/RBP6f.',
            '672620 does not write2c8+0: all writes in its complete call-free body are disjoint from that header. Actual original execution preserves index values. This establishes the A initializer behavior, not a proof of every later A/B mutation.',
            '230 insertion5d94a0 tests signed-short table membership and highwater without reading payload records. Allocation membership and next-link shape are table properties on this path; primary/companion shared index is established by CreateAgent registration.',
            '230 removal clears its table entry/count but recomputes highest occupancy from230 payload+1c. Reset payloads can make this highwater disagree with table membership; the artificial contrasting replay proves the dependency, not that the engine permits removal during that phase.',
            'Native reset loops5cf575..59e and5cfc75..9f call694630 while iterating the PRIMARY signed-short chain. A snapshot that is merely frozen is not automatically at a phase where every payload header equals every ledger index.'],
        'snapshotBoundaryRequired': [
            'Identify a documented native lifecycle/synchronization point after registration and required companion refresh, outside allocation, reset, activation, removal and worker mutation. No such universal boundary is proved here.',
            'Validate each pool short table membership/count and sorted successor chain under its exact native state; distinguish those allocator facts from phase-sensitive230 payload state.',
            'Do not silently normalize native counters/highwater or infer that every table/header mismatch is corruption. Incoherent-phase snapshots must be rejected or deferred with a specific reason.',
            'Do not import230 header negativity as unoccupied, and do not assert all five payload headers agree without a proved phase precondition.',
            'Primary+companion logical-index equality needs completion of the full creation/removal transaction, while secondary remains independent. Preserve that distinction.'],
        'openProofGaps': [
            'No complete transitive CreateAgent/activation call closure or engine phase barrier; full function return header state is not asserted.',
            'No whole-program inventory of computed aliases to230+1c or2c8+0. Metadata scan is exact-displacement/pdata bounded and cannot replace owner/dataflow closure.',
            'The persistent604404 caller and all paths to its supplied companion pointer need lifecycle evidence before claiming that refresh necessarily precedes a proposed snapshot.',
            'No active native Mission snapshot, registration, removal or adoption was executed. Old synthetic validator evidence does not satisfy these native phase obligations.'],
        'oldSnapshotValidatorApproved': False, 'replacementSnapshotValidatorApproved': False,
        'enginePatchApproved': False, 'gameLaunched': False}


if __name__ == '__main__':
    result = build()
    path = ROOT / 'evidence/engine-extension/native-companion-occupancy-contract.json'
    path.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(path), 'sha256': digest(path), 'status': result['status'],
                      'helperCases': len(result['actualHelperReplay']),
                      'registrations': len(result['actualRegistrationAndResetReplay'])}))
