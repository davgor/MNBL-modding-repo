"""Independent companion byte/ABI replay; no game or installed file access."""
from pathlib import Path
import hashlib
import json
import os
import random
import struct
import subprocess
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
BODIES = ((0x5d8860, 0x5d88ac, 0x2c8), (0x5e58e0, 0x5e5905, 0x230),
          (0x694630, 0x694858, 0x230))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build():
    assert digest(SOURCE) == SHA
    pe = pefile.PE(str(SOURCE))
    assert pe.FILE_HEADER.Machine == 0x8664
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    spans = {}
    instructions = []
    writes_by_entry = {}
    for first, end, width in BODIES:
        raw = pe.get_data(first, end-first)
        spans[first] = raw
        decoded = list(md.disasm(raw, BASE+first))
        assert sum(i.size for i in decoded) == len(raw)
        assert decoded[-1].address+decoded[-1].size == BASE+end and decoded[-1].mnemonic == 'ret'
        destinations = []
        for i in decoded:
            instructions.append({'address': hex(i.address), 'bytes': i.bytes.hex(),
                                 'mnemonic': i.mnemonic, 'operands': i.op_str})
            assert not i.group(capstone.CS_GRP_JUMP)
            if i.group(capstone.CS_GRP_CALL):
                assert i.address == BASE+0x5e58f8 and i.op_str == '0x180694630'
            for index, operand in enumerate(i.operands):
                if operand.type != capstone.x86.X86_OP_MEM:
                    continue
                mem = operand.mem
                assert not mem.index
                if mem.base == capstone.x86.X86_REG_RIP:
                    # Independently reject a MOV-family destination; Capstone
                    # alone can mislabel SIMD memory stores as reads.
                    assert not (index == 0 and i.mnemonic.startswith('mov'))
                    address = i.address+i.size+mem.disp-BASE
                    section = pe.get_section_by_rva(address)
                    assert section and not section.Characteristics & 0x80000000
                    spans[address] = pe.get_data(address, operand.size)
                    assert len(spans[address]) == operand.size
                else:
                    assert mem.base == capstone.x86.X86_REG_RCX
                    assert index == 0 and i.mnemonic.startswith('mov')
                    assert 0 <= mem.disp and mem.disp+operand.size <= width
                    destinations.append((mem.disp, operand.size))
        writes_by_entry[first] = destinations
    pages = sorted({a & ~4095 for a, raw in spans.items()} |
                   {(a+len(raw)-1) & ~4095 for a, raw in spans.items()})
    cases = []
    rng = random.Random(0x606A205E58E0)
    nonvolatile = ('RBX', 'RBP', 'RSI', 'RDI', 'R12', 'R13', 'R14', 'R15')
    registers = ('RAX', 'RCX', 'RDX', 'RBX', 'RBP', 'RSI', 'RDI', 'R8', 'R9', 'R10', 'R11', 'R12', 'R13', 'R14', 'R15')
    for first, _, size in BODIES[:2]:
        for alignment_offset in ((0, 8) if size == 0x2c8 else (0,)):
            for poison in (0x00, 0x01, 0x55, 0x7f, 0x80, 0xa5, 0xfe, 0xff):
                uc = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
                for page in pages:
                    uc.mem_map(BASE+page, 4096)
                for a, raw in spans.items():
                    uc.mem_write(BASE+a, raw)
                object_page, stack, stop = 0x61000000, 0x52000000, 0x191000000
                obj = object_page+0x100+alignment_offset
                uc.mem_map(object_page, 4096)
                uc.mem_write(object_page, bytes([poison])*4096)
                uc.mem_map(stack, 65536)
                uc.mem_write(stack, bytes([0xcc])*65536)
                uc.mem_map(stop, 4096)
                rsp = stack+0x8008
                uc.mem_write(rsp, struct.pack('<Q', stop))
                initial = {}
                for name in registers:
                    number = getattr(x, 'UC_X86_REG_'+name)
                    initial[name] = rng.getrandbits(64)
                    uc.reg_write(number, initial[name])
                xmm = {}
                for index in range(16):
                    xmm[index] = rng.getrandbits(128)
                    uc.reg_write(getattr(x, 'UC_X86_REG_XMM'+str(index)), xmm[index])
                uc.reg_write(x.UC_X86_REG_RCX, obj)
                uc.reg_write(x.UC_X86_REG_RSP, rsp)
                uc.reg_write(x.UC_X86_REG_EFLAGS, 0x202)
                object_writes = set()
                def on_write(_uc, _access, address, width, _value, _data):
                    if obj <= address and address+width <= obj+size:
                        object_writes.update(range(address-obj, address-obj+width))
                    else:
                        assert stack <= address and address+width <= stack+65536
                uc.hook_add(unicorn.UC_HOOK_MEM_WRITE, on_write)
                uc.emu_start(BASE+first, stop, count=1000)
                assert uc.reg_read(x.UC_X86_REG_RIP) == stop
                assert uc.reg_read(x.UC_X86_REG_RSP) == rsp+8
                assert uc.reg_read(x.UC_X86_REG_RAX) == obj
                assert uc.reg_read(x.UC_X86_REG_RCX) == obj
                for name in nonvolatile:
                    assert uc.reg_read(getattr(x, 'UC_X86_REG_'+name)) == initial[name]
                for index in range(6, 16):
                    assert uc.reg_read(getattr(x, 'UC_X86_REG_XMM'+str(index))) == xmm[index]
                data = bytes(uc.mem_read(obj, size))
                expected_writes = set()
                for displacement, width in writes_by_entry[first] + (writes_by_entry[0x694630] if first == 0x5e58e0 else []):
                    expected_writes.update(range(displacement, displacement+width))
                assert object_writes == expected_writes
                assert all(data[index] == poison for index in range(size) if index not in object_writes)
                assert bytes(uc.mem_read(obj-32, 32)) == bytes([poison])*32
                assert bytes(uc.mem_read(obj+size, 32)) == bytes([poison])*32
                cases.append({'entryRva': hex(first), 'alignmentOffset': alignment_offset,
                              'poison': poison, 'writtenBytes': len(object_writes),
                              'unchangedBytes': size-len(object_writes), 'passed': True})
    return {'schemaVersion': 1, 'status': 'companion-byte-abi-review-pending-final-harness-binding',
            'binarySha256': SHA, 'independentCases': cases,
            'originalInstructions': instructions, 'mappedPages': [hex(p) for p in pages],
            'scope': 'Original companion constructors only; randomized incoming registers, all Win64 nonvolatile GPRs/XMMs, exact write coverage, untouched-byte preservation and8/16-byte placement.',
            'enginePatchApproved': False, 'gameLaunched': False}


def main():
    retained = ROOT / 'evidence/engine-extension/native-element-factory-integration.json'
    assert digest(retained) == '2f49a1bd4447d3f01a4587105be72ab29075620291fc354f4f898eb723119f1e'
    integration = json.loads(retained.read_text())
    bound = {**integration['sources'], **integration['outputs']}
    for path, sha in bound.items():
        assert digest(ROOT/path) == sha, path
    result = build()
    # Existing executable identities are frozen by the retained integration
    # receipt. Run them directly; never invoke the canonical build/writer.
    runtime = Path('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64')
    assert (runtime/'clang_rt.asan_dynamic-x86_64.dll').is_file()
    environment = dict(os.environ)
    environment['PATH'] = str(runtime)+os.pathsep+environment.get('PATH', '')
    runs = []
    for executable in ('native_element_factory_tests.exe', 'native_element_factory_asan.exe'):
        path = ROOT/'build/agent-storage'/executable
        run = subprocess.run([str(path)], cwd=ROOT, env=environment, capture_output=True, text=True)
        assert run.returncode == 0 and run.stdout.count('PASS native element factory:') == 1, (run.stdout, run.stderr)
        runs.append({'path': str(path), 'sha256': digest(path), 'exitCode': run.returncode,
                     'stdout': run.stdout, 'stderr': run.stderr})
    for path, sha in bound.items():
        assert digest(ROOT/path) == sha, path
    assert digest(retained) == '2f49a1bd4447d3f01a4587105be72ab29075620291fc354f4f898eb723119f1e'
    result['status'] = 'accepted-isolated-native-element-factory-not-engine-integration'
    result['reviewedIntegration'] = {'path': str(retained), 'sha256': digest(retained)}
    result['reviewedSourceSha256'] = integration['sources']
    result['verifiedOutputSha256'] = integration['outputs']
    result['independentExecutableRuns'] = runs
    result['asanRuntime'] = {'path': str(runtime/'clang_rt.asan_dynamic-x86_64.dll'),
                             'sha256': digest(runtime/'clang_rt.asan_dynamic-x86_64.dll')}
    result['manualReview'] = [
        'NativeElementFactory call signatures match retained Windows x64 constructor RCX=this/RAX=this and non-deleting destructor RCX=this contracts.',
        'Per-destination construction plus exactly four-byte inactive-header writes; no whole-object copy or zeroing in the factory. Poison initialization exists only in the test wrappers.',
        '16-byte allocations satisfy Agent and230 alignment and both alternating8-byte-aligned2c8 positions. All five strides and header offsets match the native lifecycle contract.',
        'Backend completed count advances only after successful callback return; reverse rollback destroys completed Agent objects, while a failed constructor owns its own incomplete-object cleanup.',
        'Companion destruction is intentionally absent only for default unpublished objects or objects already normally removed/reset. Active removal remains an engine responsibility.',
        'CodeSlice commits only retained pages, changes code to execute/read and constants to read-only, flushes instruction cache, registers original230 wrapper unwind, checks registration, and deregisters before unmapping. Constructor catch rolls back registration and reservation.',
        'Factory and its runtime owner are explicitly required to outlive storage and every read lease; callbacks retain a raw factory context and do not enforce that lifetime automatically.']
    result['virtualAllocReview'] = {
        'finding': 'No reachable nonnull-wrong-base allocation leak for this granularity-aligned fixed address under the documented API contract.',
        'reason': 'Nonnull lpAddress selects the granularity-rounded requested region; incompatible reservation fails without allocating pages.0x180000000 is already aligned.',
        'source': 'https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc'}
    result['openFindings'] = []
    result['limitations'] = [
        'Main Agent constructor/destructor remain synthetic; this review does not execute their native allocator, vtable, C++ EH or live-runtime dependencies.',
        'Optimized/ASAN existing executables were independently rerun, not rebuilt. Their exact source/generated-code/executable identities match the retained successful build receipt.',
        'ASAN covers compiled harness/storage code, not copied native instructions. Separate24-case emulation checks the native companion writes and Win64 preserved registers.',
        'Wrapper unwind registration/readback is exercised; asynchronous exceptions or full live-engine unwind integration are not tested here.',
        'Production module binding, activation/removal, quiescence, complete access routing, active teardown and factory lifetime enforcement remain separate integration work.',
        'No game launch, installed-file access or modification, or engine patch approval.']
    result['reviewerSourceSha256'] = digest(__file__)
    dest = ROOT / 'evidence/engine-extension/native-element-factory-independent-review.json'
    dest.write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps({'path': str(dest), 'sha256': digest(dest), 'passed': len(result['independentCases'])}))


if __name__ == '__main__':
    main()
