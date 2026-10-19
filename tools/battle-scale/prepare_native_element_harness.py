"""Extract a closed companion initializer slice; never load or launch the game.

The isolated Windows harness executes these original bytes at their original
relative addresses. Agent constructors are NOT in this slice.
"""
from pathlib import Path
import hashlib, json, struct, sys
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone, pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_MEM_WRITE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RSP, UC_X86_REG_RAX

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE = 0x180000000
BODIES = [(0x5d8860, 0x5d88ac, 0x2c8), (0x5e58e0, 0x5e5905, 0x230),
          (0x694630, 0x694858, 0x230)]

def main():
    blob = SOURCE.read_bytes()
    assert hashlib.sha256(blob).hexdigest() == SHA
    pe = pefile.PE(data=blob)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
    spans = {}; rows = []; reads = []
    for first, end, size in BODIES:
        raw = pe.get_data(first, end-first); spans[first] = raw
        ins = list(md.disasm(raw, BASE+first))
        assert sum(i.size for i in ins) == len(raw) and ins[-1].mnemonic == 'ret'
        for i in ins:
            rows.append(dict(address=hex(i.address), bytes=i.bytes.hex(), text=i.mnemonic+' '+i.op_str))
            if i.group(capstone.CS_GRP_CALL):
                assert i.address == BASE+0x5e58f8 and i.operands[0].type == capstone.x86.X86_OP_IMM
                assert i.operands[0].imm == BASE+0x694630
            assert not i.group(capstone.CS_GRP_JUMP)
            assert i.mnemonic in {'mov', 'movaps', 'movups', 'xor', 'sub', 'add', 'call', 'ret'}
            _, writes = i.regs_access()
            assert capstone.x86.X86_REG_RCX not in writes and capstone.x86.X86_REG_ECX not in writes
            for op in i.operands:
                if op.type != capstone.x86.X86_OP_MEM: continue
                assert op.mem.index == 0
                if op.mem.base == capstone.x86.X86_REG_RIP:
                    assert not op.access & capstone.CS_AC_WRITE
                    rva = i.address+i.size+op.mem.disp-BASE
                    section = pe.get_section_by_rva(rva)
                    assert section and not section.Characteristics & 0x80000000
                    assert rva+op.size <= section.VirtualAddress+section.Misc_VirtualSize
                    spans[rva] = pe.get_data(rva, op.size)
                    assert len(spans[rva]) == op.size
                    reads.append(dict(instruction=hex(i.address), rva=hex(rva), bytes=spans[rva].hex()))
                else:
                    assert op.mem.base == capstone.x86.X86_REG_RCX
                    assert 0 <= op.mem.disp <= size-op.size
    entry = next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress == 0x5e58e0)
    assert entry.EndAddress == 0x5e5905 and entry.UnwindData == 0xb87098
    unwind = pe.get_data(entry.UnwindData, 8)
    assert unwind.hex() == '0104010004420000'
    spans[entry.UnwindData] = unwind
    pages = sorted({p for a, raw in spans.items() for p in range(a & ~4095, (a+len(raw)+4095)&~4095, 4096)})
    cases = []
    for first, _, size in BODIES[:2]:
        for poison in (0xa5, 0x5a):
            uc = Uc(UC_ARCH_X86, UC_MODE_64)
            for page in pages: uc.mem_map(BASE+page, 4096)
            for a, raw in spans.items(): uc.mem_write(BASE+a, raw)
            obj=0x60000100; stack=0x50000000; stop=0x190000000
            uc.mem_map(obj & ~4095, 4096); uc.mem_map(stack, 65536); uc.mem_map(stop, 4096)
            uc.mem_write(obj & ~4095, bytes([poison])*4096)
            rsp=stack+0x8008; uc.mem_write(rsp, struct.pack('<Q', stop))
            uc.reg_write(UC_X86_REG_RSP, rsp); uc.reg_write(UC_X86_REG_RCX, obj)
            writes=[]
            def check_write(_uc, _access, address, width, _value, _user):
                assert obj <= address and address+width <= obj+size or stack <= address and address+width <= stack+65536
                writes.append((address, width))
            uc.hook_add(UC_HOOK_MEM_WRITE, check_write)
            uc.emu_start(BASE+first, stop, count=1000)
            assert uc.reg_read(UC_X86_REG_RAX) == obj and uc.reg_read(UC_X86_REG_RSP) == rsp+8
            raw=bytes(uc.mem_read(obj, size))
            header=0 if size == 0x2c8 else 0x1c
            expected=bytearray(raw); expected[header:header+4]=b'\xff'*4
            assert bytes(uc.mem_read(obj-32,32)) == bytes([poison])*32
            assert bytes(uc.mem_read(obj+size,32)) == bytes([poison])*32
            cases.append(dict(entryRva=hex(first), size=size, poison=poison, expectedAfterFactoryHeader=expected.hex(), writes=len(writes)))
    lines=['#pragma once', '#include <array>', '#include <cstdint>', 'namespace native_elements_fixture {',
           'struct Span { std::uint32_t rva; const unsigned char* bytes; std::size_t size; };']
    for n,(a,raw) in enumerate(sorted(spans.items())):
        lines.append(f'inline constexpr unsigned char data{n}[]={{'+','.join(f'0x{b:02x}' for b in raw)+'};')
    lines.append(f'inline constexpr std::array<Span,{len(spans)}> spans{{{{'+','.join(f'{{0x{a:x},data{n},sizeof(data{n})}}' for n,(a,_) in enumerate(sorted(spans.items())))+'}};')
    lines.append(f'inline constexpr std::array<std::uint32_t,{len(pages)}> pages{{'+','.join(hex(p) for p in pages)+'};')
    for n,c in enumerate(cases):
        lines.append(f'inline constexpr unsigned char expected{n}[]={{'+','.join(f'0x{b:02x}' for b in bytes.fromhex(c['expectedAfterFactoryHeader']))+'};')
    lines.append('}')
    output=ROOT/'build/agent-storage/native_element_machine_code.hpp'
    output.write_text('\n'.join(lines)+'\n')
    receipt=dict(schemaVersion=1,status='closed-companion-initializer-slice-replayed',binarySha256=SHA,
        toolSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        generatedSha256=hashlib.sha256(output.read_bytes()).hexdigest(), originalInstructions=rows,
        constants=reads, unwind=unwind.hex(), cases=cases,
        limitations=['Agent ctor/dtor not executed; live engine binding and lifecycle are not provided.',
                    'Only two initialized poison patterns; no claim that constructors zero every byte.',
                    'No game launch, installation, engine quiescence or hook approval.'],gameLaunched=False)
    (ROOT/'evidence/engine-extension/native-element-companion-replay.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps(dict(companionCases=len(cases),originalInstructions=len(rows),mappedPages=len(pages))))

if __name__ == '__main__': main()
