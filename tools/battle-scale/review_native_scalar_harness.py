"""Independent read-only scalar adapter audit and synthetic/native replays."""
from pathlib import Path
import hashlib
import importlib.util
import json
import os
import random
import re
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools/battle-scale'), str(ROOT / 'build/battle-scale/python-libs')]
import capstone
import pefile
import unicorn
from unicorn import x86_const as x

RECEIPT_SHA = '762d0130b1a79e9ee45174a3bc55787eb4044c8312743528666b02d6251d8fb5'
ROUTE_SHA = 'fb5e74eff6c0086c97bfc75da29db2768fce1047ac15aacdb0557933116f2516'
REGS = ['rax', 'rcx', 'rdx', 'rbx', 'rbp', 'rsi', 'rdi', 'r8', 'r9', 'r10', 'r11', 'r12', 'r13', 'r14', 'r15']
REG_IDS = {name: getattr(x, 'UC_X86_REG_' + name.upper()) for name in REGS}


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def load(name):
    path = ROOT / 'tools/battle-scale' / (name + '.py')
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def extra_case(site, index, scalar, error=False):
    """Separate emulator fixture; root helper and root replay are not reused."""
    code, helper, stop, stack, payload, mission = (0x30100000, 0x30108000, 0x30109000,
                                                  0x40100000, 0x50100000, 0x60100000)
    route = site['route']
    raw = bytearray.fromhex(site['stub']['bytes'])
    struct.pack_into('<Q', raw, site['helperOffset'], helper)
    field, width = int(route['operation']['fieldOffset'], 16), route['operation']['widthBytes']
    rng = random.Random(site['siteId'] * 733 + index + scalar)
    incoming = {name: rng.getrandbits(64) for name in REGS}
    incoming[route['entryInputs']['missionRegister']] = mission
    incoming[route['entryInputs']['logicalIndexRegister']] = index & ((1 << 64) - 1)
    vector = {i: rng.getrandbits(128) for i in range(16)}
    contents = bytearray(rng.randbytes(4096))
    contents[field:field + width] = struct.pack('<I', scalar)[:width]
    uc = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
    for address, size in ((code, 0xa000), (stack, 0x10000), (payload, 4096)):
        uc.mem_map(address, size)
    uc.mem_write(code, bytes(raw))
    uc.mem_write(helper, b'\xc3')
    uc.mem_write(stop, b'\xc3')
    uc.mem_write(payload, bytes(contents))
    rsp = stack + 0x8008
    uc.mem_write(rsp, struct.pack('<Q', stop))
    uc.reg_write(x.UC_X86_REG_RSP, rsp)
    for name, value in incoming.items():
        uc.reg_write(REG_IDS[name], value)
    for i, value in vector.items():
        uc.reg_write(getattr(x, 'UC_X86_REG_XMM' + str(i)), value)
    calls = []
    def hook(emu, pc, size, unused):
        if pc == stop:
            emu.emu_stop()
        if pc != helper:
            return
        assert emu.reg_read(x.UC_X86_REG_RCX) == mission
        assert emu.reg_read(x.UC_X86_REG_RDX) == incoming[route['entryInputs']['logicalIndexRegister']]
        assert emu.reg_read(x.UC_X86_REG_R8) == site['siteId']
        assert emu.reg_read(x.UC_X86_REG_RSP) % 16 == 8
        calls.append(pc)
        if not error and route['operation']['kind'] == 'write':
            emu.mem_write(payload + field, b'\x01')
        for name in ('rax', 'rcx', 'rdx', 'r8', 'r9', 'r10', 'r11'):
            emu.reg_write(REG_IDS[name], rng.getrandbits(64))
        for i in range(6):
            emu.reg_write(getattr(x, 'UC_X86_REG_XMM' + str(i)), rng.getrandbits(128))
        emu.reg_write(x.UC_X86_REG_EFLAGS, 0x202)
        emu.reg_write(x.UC_X86_REG_RAX, (0x12345678 << 32) | 0xdeadbeef if error else scalar)
    uc.hook_add(unicorn.UC_HOOK_CODE, hook)
    caught = False
    try:
        uc.emu_start(code, stop + 1, count=1000)
    except unicorn.UcError as exc:
        assert error and uc.reg_read(x.UC_X86_REG_RIP) == code + site['trapOffset'], exc
        caught = True
    assert len(calls) == 1 and caught == error
    expected_payload = contents.copy()
    if not error and route['operation']['kind'] == 'write':
        expected_payload[field] = 1
    assert bytes(uc.mem_read(payload, 4096)) == bytes(expected_payload)
    if not error:
        assert uc.reg_read(x.UC_X86_REG_RSP) == rsp + 8 and uc.reg_read(x.UC_X86_REG_RIP) == stop
        oracle = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
        origin = int(route['windowStart'], 16)
        address = mission + index * 0xc60 + 0x2d50 + field
        oracle.mem_map(origin & ~4095, 8192)
        oracle.mem_map(address & ~4095, 8192)
        oracle.mem_write(origin, bytes.fromhex(route['originalBytes']))
        oracle.mem_write(address, struct.pack('<I', scalar)[:width])
        for name, value in incoming.items():
            oracle.reg_write(REG_IDS[name], value)
        oracle.emu_start(origin, int(route['windowEndExclusive'], 16), count=2)
        for name in REGS:
            assert uc.reg_read(REG_IDS[name]) == oracle.reg_read(REG_IDS[name]), (site['siteId'], index, name)
        for i, value in vector.items():
            assert uc.reg_read(getattr(x, 'UC_X86_REG_XMM' + str(i))) == value
        assert bytes(uc.mem_read(payload + field, width)) == bytes(oracle.mem_read(address, width))
    return {'site': site['siteId'], 'index': index, 'scalar': hex(scalar),
            'injectedHighStatusWithNonzeroLowValue': error, 'exactTrap': caught,
            'fullPayloadSentinelVerified': True, 'passed': True}


def build():
    receipt_path = ROOT / 'evidence/engine-extension/native-scalar-storage-integration.json'
    route_path = ROOT / 'evidence/engine-extension/native-simple-agent-operation-routes.json'
    assert digest(receipt_path) == RECEIPT_SHA and digest(route_path) == ROUTE_SHA
    receipt = json.loads(receipt_path.read_text())
    routes = json.loads(route_path.read_text())
    replay_path = ROOT / 'evidence/engine-extension/native-segmented-scalar-replay.json'
    replay = json.loads(replay_path.read_text())
    pinned = {**receipt['sources'], **receipt['outputs'], **receipt['preservedAcceptedInputs']}
    pinned.update({row['path']: row['sha256'] for row in routes['sources']})
    pinned.update(replay['sources'])
    for path, sha in pinned.items():
        assert digest(ROOT / path) == sha, path
    raw_path = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    pe = pefile.PE(str(raw_path))
    assert digest(raw_path) == replay['nativeSourceSha256'] == routes['nativeSourceSha256']
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    # Full route-catalog reconstruction is read-only: its main writer is not run.
    catalog = load('audit_simple_agent_operation_routes')
    assert catalog.build() == routes
    machine = load('prepare_segmented_scalar_harness')
    owner_inventory = json.loads((ROOT / 'evidence/engine-extension/native-live-agent-access-review.json').read_text())
    owners = {row['rva']: row for row in owner_inventory['candidates']}
    header = (ROOT / 'build/agent-storage/scalar_machine_code.hpp').read_text()
    verified_rows = set()
    def verify_original(value):
        if isinstance(value, dict):
            if 'address' in value and 'bytes' in value:
                address = int(value['address'], 16)
                raw = bytes.fromhex(value['bytes'])
                assert pe.get_data(address - base, len(raw)) == raw
                verified_rows.add((address, raw))
            for child in value.values():
                verify_original(child)
        elif isinstance(value, list):
            for child in value:
                verify_original(child)
    verify_original(routes)
    route_checks, extra, unwind = [], [], []
    original_replays = 0
    for number, site in enumerate(replay['sites']):
        route = routes['routes'][number]
        assert site['route'] == route and site['siteId'] == number
        regenerated = machine.make(route, number)
        assert regenerated == {k: v for k, v in site.items() if k != 'cases'}
        assert machine.replay(regenerated) == site['cases']
        original_replays += len(site['cases'])
        assert route['ownerProof'] == owners[route['multiplyRva']]['ownerFlowProof']
        original = bytes.fromhex(route['originalBytes'])
        address = int(route['windowStart'], 16)
        assert pe.get_data(address - base, len(original)) == original
        instructions = list(md.disasm(original, address))
        assert len(instructions) == 2 and instructions[0].mnemonic == 'imul' and instructions[1].mnemonic == 'mov'
        product = instructions[0].reg_name(instructions[0].operands[0].reg)
        index = instructions[0].reg_name(instructions[0].operands[1].reg)
        mission = route['entryInputs']['missionRegister']
        assert len({product, index, mission}) == 3
        assert index == route['entryInputs']['logicalIndexRegister']
        assert instructions[0].operands[0].size == 8 and instructions[0].operands[2].imm == 0xc60
        memory = next(o for o in instructions[1].operands if o.type == capstone.x86.X86_OP_MEM)
        assert {instructions[1].reg_name(memory.mem.base), instructions[1].reg_name(memory.mem.index)} == {product, mission}
        assert memory.mem.scale == 1 and memory.mem.disp - 0x2d50 == int(route['operation']['fieldOffset'], 16)
        assert route['operation']['widthBytes'] == memory.size
        if route['operation']['kind'] == 'read':
            assert memory.size == 4 and instructions[1].operands[0].size == 4
        else:
            assert memory.size == 1 and instructions[1].operands[1].imm == 1
        # Flag inputs are unused along the exact straight-line continuation;
        # undefined flag outputs release requirements rather than specify bits.
        remaining = set(('CF', 'OF', 'ZF', 'SF', 'AF', 'PF'))
        next_address = int(route['windowEndExclusive'], 16)
        for row in route['flags']['continuationThroughOverwrite']:
            assert int(row['address'], 16) == next_address
            ins = next(md.disasm(bytes.fromhex(row['bytes']), next_address))
            assert not any(ins.group(g) for g in (capstone.CS_GRP_CALL, capstone.CS_GRP_JUMP, capstone.CS_GRP_RET))
            for flag in list(remaining):
                assert not ins.eflags & (getattr(capstone.x86, 'X86_EFLAGS_TEST_' + flag) |
                                         getattr(capstone.x86, 'X86_EFLAGS_PRIOR_' + flag))
                if any(ins.eflags & getattr(capstone.x86, 'X86_EFLAGS_' + action + '_' + flag)
                       for action in ('MODIFY', 'RESET', 'SET', 'UNDEFINED')):
                    remaining.remove(flag)
            next_address += ins.size
        assert not remaining
        for kind in ('stub', 'entry'):
            for member in ('bytes', 'unwind'):
                encoded = re.search(r'scalar_' + str(number) + '_' + kind + '_' + member + r'\{([^}]+)\}', header).group(1)
                assert bytes(int(part, 0) for part in encoded.split(',')).hex() == site[kind][member]
            frame = site[kind]
            info = bytes.fromhex(frame['unwind'])
            assert info[:4] == bytes((1, frame['prologEnd'], 18, 0))
            rows = [info[o:o + 4] for o in range(4, 36, 4)]
            decoded = [(r[0], r[1] >> 4, struct.unpack('<H', r[2:])[0] * 8) for r in rows]
            assert all(r[1] & 15 == 4 for r in rows)
            assert decoded == [(r['endOffset'], r['code'], r['slot']) for r in reversed(frame['nonvolatileSaves'])]
            assert {r[1] for r in decoded} == {3, 5, 6, 7, 12, 13, 14, 15}
            assert info[36:] == bytes((frame['allocationEnd'], 1, 33, 0))
            raw = bytes.fromhex(frame['bytes'])
            assert raw[frame['allocationEnd'] - 7:frame['allocationEnd']] == bytes.fromhex('4881ec08010000')
            assert raw[frame['epilogStart']:] == bytes.fromhex('4881c408010000c3')
            for offset, code, slot in decoded:
                ins = next(md.disasm(raw[offset - 8:offset], offset - 8))
                assert ins.mnemonic == 'mov' and ins.operands[0].type == capstone.x86.X86_OP_MEM
                assert ins.reg_name(ins.operands[0].mem.base) == 'rsp' and ins.operands[0].mem.disp == slot
            unwind.append({'site': number, 'frame': kind, 'savedNonvolatiles': len(decoded),
                           'frameBytes': 264, 'independentlyParsed': True})
        for index_value, scalar in [(0, 0), (2039, 1), (2040, 0x7fffffff), (4079, 0x80000000),
                                    (4080, 0xffffffff), (6119, 0), (65536, 0x80000000),
                                    (0x100000007, 0xffffffff)]:
            extra.append(extra_case(site, index_value, scalar))
        for index_value in (-1, -2, 0x7fffffffffffffff):
            extra.append(extra_case(site, index_value, 0, True))
        route_checks.append({'site': number, 'multiplyRva': route['multiplyRva'],
                             'originalInstructionBytesAndOwnerRecordVerified': True,
                             'productIndexMissionDistinct': True, 'continuationFlagsDead': True})
    runtime = Path('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64')
    environment = dict(os.environ)
    environment['PATH'] = str(runtime) + os.pathsep + environment.get('PATH', '')
    runs = []
    for name in ('native_scalar_harness.exe', 'native_scalar_harness_asan.exe'):
        executable = ROOT / 'build/agent-storage' / name
        run = subprocess.run([str(executable)], cwd=ROOT, env=environment, text=True,
                             capture_output=True, timeout=60)
        assert run.returncode == 0, (run.stdout, run.stderr)
        assert '85680 operations;2040->4080->6120->8160->10200;' in run.stdout
        assert '670 instruction-boundary Windows unwinds;91 exact UD2 error-path unwinds;' in run.stdout
        runs.append({'path': str(executable), 'sha256': digest(executable), 'exitCode': run.returncode,
                     'stdout': run.stdout, 'stderr': run.stderr})
    for path, sha in pinned.items():
        assert digest(ROOT / path) == sha, path
    assert digest(receipt_path) == RECEIPT_SHA and digest(route_path) == ROUTE_SHA
    return {'schemaVersion': 1, 'status': 'accepted-isolated-scalar-adapters-not-game-hooks',
            'reviewedIntegration': {'path': str(receipt_path), 'sha256': RECEIPT_SHA},
            'reviewedRouteCatalog': {'path': str(route_path), 'sha256': ROUTE_SHA,
                                    'fullReadOnlyReplayMatched': True, 'routes': route_checks},
            'reviewedSourceSha256': receipt['sources'], 'verifiedOutputSha256': receipt['outputs'],
            'preservedAcceptedInputs': receipt['preservedAcceptedInputs'],
            'allVerifiedDependencySha256': pinned, 'reviewerSourceSha256': digest(__file__),
            'verifiedOriginalByteRows': len(verified_rows),
            'retainedEmulatorCasesReplayed': original_replays,
            'additionalIndependentEmulatorCases': extra,
            'independentUnwindMetadataParsing': unwind,
            'independentExecutableRuns': runs,
            'manualReview': [
                'Seven exact IMUL+MOV pairs preserve product outputs and all unrelated GPRs. Product is distinct from Mission/index for every selected route, so computing it before saving inputs is safe here. Saved load destinations are replaced with zero-extended scalar results before restoration.',
                'Fixed264-byte frames reserve32-byte helper shadow space,120-byte GPR slots,8-byte packed result and96-byte XMM0..5 storage without overlap. Standard-CALL entry alignment is maintained through entry and stub. XMM6..15 are untouched and rely on normal helper ABI preservation.',
                'Helper holds a primary-domain read lease through get and scalar memory operation and returns only32-bit scalar/error status. Read leases protect storage lifetime; they do not serialize same-field writes.',
                'Nonzero high32 status traps at exact generated UD2, even with nonzero low bits. Zero success returns normally. Narrow SEH filter requires illegal-instruction code, exact site trap PC and thread-local error; unrelated faults continue searching.',
                'Helper catches C++ exceptions before noexcept exit; lease destruction precedes translated UD2. The native tests execute both generated frames during actual exception propagation.',
                'Fourteen sorted runtime entries register stub and entry frames, verify table lookup, roll back failed constructor readback, and unregister before executable memory release. Both frames use ordinary allocation/save unwind codes and recognized ADD RSP/RET epilogues.',
                'Borrowed first-block lifetime is retained by storage and freed exactly once at close; only owned suffixes receive synthetic destructor callbacks. Growth and close wait while the scalar helper lease remains held, and async failure cleanup releases the held helper before future destruction.'],
            'openFindings': [],
            'limitations': [
                'Only isolated standard-CALL fixtures are approved. Real native entry alignment, redirected/interior entries, C++ handler state and production error policy remain unproved.',
                'Route catalog remains bounded consumer/owner evidence; it does not turn incomplete MAY-use or indirect-entry closure into global hook approval.',
                'Original file bytes are executed in emulator for each comparison. Native storage uses synthetic records/callbacks; no actual Bannerlord constructors, engine barriers or game patches execute.',
                'Independent wide-index emulator successes beyond32 bits prove capture/transport only, not practical native storage allocation at those indices.',
                'Flags are deliberately not generally preserved. The exact continuation kills their prior requirements; undefined outputs do not promise a particular bit pattern.',
                'Frozen native binaries were independently rerun, not rebuilt. ASAN covers compiled fixture/backend code, not generated instruction instrumentation.',
                'No game or installed-file access, game process control, canonical report overwrite or engine patch approval.'],
            'gameLaunched': False, 'enginePatchApproved': False}


if __name__ == '__main__':
    result = build()
    path = ROOT / 'evidence/engine-extension/native-scalar-storage-independent-review.json'
    path.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(path), 'sha256': digest(path), 'status': result['status'],
                      'extraCases': len(result['additionalIndependentEmulatorCases']),
                      'nativeRuns': len(result['independentExecutableRuns'])}))
