"""Isolated x64 instructions only; never executes Bannerlord or accesses a process."""
import importlib.util
import json
from pathlib import Path
import unittest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('renderer_guard_emulator', HERE/'test_renderer_guard_emulation.py')
emu = importlib.util.module_from_spec(spec)
spec.loader.exec_module(emu)
DOCUMENT = json.loads((ROOT/'evidence/engine-extension/native-ranged-stall-review.json').read_text())
EDIT = next(x for x in DOCUMENT['proposedEdits'] if x['rva']=='0x23f617')
OLD = bytes.fromhex(EDIT['originalBytes'])
NEW = bytes.fromhex(EDIT['replacementBytes'])
SHARED = next(x for x in DOCUMENT['proposedEdits'] if x['rva']=='0x89a0f')
SHIFT = DOCUMENT['layout']['candidateElementsPerPage'].bit_length()-1
CAPACITY = DOCUMENT['layout']['candidateCapacity']
START = 0x18023f617
STOP = {0x18023f640, 0x18023f6f7}


def candidate_span(start,end):
    """Reconstruct the actual proposal from retained source instructions and edits."""
    rows={int(i['address'],16):i for span in DOCUMENT['exactSpans'] for i in span['instructions']}
    data=bytearray();position=start
    while position<end:
        original=bytes.fromhex(rows[position]['bytes'])
        data.extend(original);position+=len(original)
    assert position==end
    for edit in DOCUMENT['proposedEdits']:
        at=int(edit['address'],16);size=len(bytes.fromhex(edit['originalBytes']))
        if start<=at<end:
            assert at+size<=end
            assert data[at-start:at-start+size].hex()==edit['originalBytes']
            data[at-start:at-start+size]=bytes.fromhex(edit['replacementBytes'])
    return bytes(data)


class RangedGuardEmulationTests(unittest.TestCase):
    def state(self, first, last):
        state = {name:0x5a5a00000000+n*0x123 for n,name in enumerate(emu.REGS)}
        state.update(rax=first,rsi=first,rbp=last,rdi=0x3456780000)
        return state

    def test_all_8256_valid_page_pairs_preserve_all_general_registers(self):
        for first in range(128):
            for last in range(first,128):
                state = self.state(first,last)
                self.assertEqual(emu.execute(NEW,START,state,STOP),emu.execute(OLD,START,state,STOP))

    def test_invalid_page_rejected_before_address_setup(self):
        for first,last in [(0,128),(128,128),(127,128),(0,0x1fffff),(0x7ffff,0x1fffff)]:
            result=emu.execute(NEW,START,self.state(first,last),STOP)
            self.assertEqual(result[:2],('invalid-instruction',START+5))

    def test_reversed_range_failfast(self):
        for first,last in [(1,0),(127,126),(0xfffff,127)]:
            result=emu.execute(NEW,START,self.state(first,last),STOP)
            self.assertEqual(result[:2],('invalid-instruction',START+12))

    def test_inclusive_end_is_33bit_and_detects_uint32_wrap(self):
        # MOV EAX,R12D; SHR EAX,12; MOV ESI,EAX; LEA EBP,[RBX-1];
        # ADD RBP,R12; SHR RBP,12. Both addends are explicitly zero extended.
        prefix=candidate_span(0x18023f605,0x18023f617)
        for start,count,valid in [(0,1,True),(0,CAPACITY,True),(CAPACITY-1,1,True),
                                  (CAPACITY,1,False),(CAPACITY-1,2,False),
                                  (0xfffffff0,0x40,False),(0xffffffff,1,False),
                                  (1,0xffffffff,False),(0,0xffffffff,False)]:
            state=self.state(0,0);state.update(r12=start,rbx=count)
            result=emu.execute(prefix+NEW,0x18023f605,state,STOP)
            self.assertEqual(result[0]=='boundary',valid,(start,count,result))
            if valid:
                self.assertEqual(result[1],0x18023f640)
                self.assertEqual(result[2]['rsi'],start>>SHIFT)
                self.assertEqual(result[2]['rbp'],((start+count-1)>>SHIFT)-(start>>SHIFT)+1)

    def test_shared_allocator_guard_preserves_valid_registers(self):
        old,new=bytes.fromhex(SHARED['originalBytes']),bytes.fromhex(SHARED['replacementBytes'])
        for first in range(128):
            for last in range(first,128):
                state=self.state(first,last)
                self.assertEqual(emu.execute(new,0x180089a0f,state,{0x180089a18,0x180089afa}),
                                 emu.execute(old,0x180089a0f,state,{0x180089a18,0x180089afa}))

    def test_shared_allocator_33bit_end_overflow_and_capacity(self):
        prefix=candidate_span(0x1800899fd,0x180089a0f)
        code=prefix+bytes.fromhex(SHARED['replacementBytes'])
        for start,count,valid in [(0,1,True),(0,CAPACITY,True),(CAPACITY-1,1,True),
                                  (CAPACITY,1,False),(CAPACITY-1,2,False),
                                  (0xfffffff0,0x40,False),(0xffffffff,1,False),(1,0xffffffff,False)]:
            state=self.state(0,0);state.update(r15=start,rdx=count)
            result=emu.execute(code,0x1800899fd,state,{0x180089a18})
            self.assertEqual(result[0]=='boundary',valid,(start,count,result))
            if valid:self.assertEqual(result[2]['rbp'],(start+count-1)>>SHIFT)

    def test_original_zero_count_skips_all_reservation_and_guard(self):
        code=bytes.fromhex('85db0f85c5000000448d63ffe9c2010000')
        state=self.state(0,0);state['rbx']=0
        result=emu.execute(code,0x18023f529,state,{0x18023f5f6,0x18023f6fc})
        self.assertEqual(result[1],0x18023f6fc)
        self.assertEqual(result[2]['r12'],0xffffffff)

    def test_actual_proposed_geometry_is_consistent(self):
        edits=DOCUMENT['proposedEdits']
        self.assertEqual(len(edits),38)
        shifts=[e for e in edits if len(e['candidateInstructions'])==1 and e['candidateInstructions'][0]['mnemonic'] in ('shl','shr')]
        self.assertEqual(len(shifts),29)
        for edit in shifts:self.assertEqual(bytes.fromhex(edit['replacementBytes'])[-1],SHIFT)
        by_rva={int(e['rva'],16):e for e in edits}
        for rva in [0x23f656,0x89a58]:
            self.assertEqual(int.from_bytes(bytes.fromhex(by_rva[rva]['replacementBytes'])[1:],'little'),(1<<SHIFT)*8)
        for rva in [0x23f690,0x89a90]:
            self.assertEqual(int.from_bytes(bytes.fromhex(by_rva[rva]['replacementBytes'])[-4:],'little'),1<<SHIFT)
        self.assertEqual(int.from_bytes(bytes.fromhex(by_rva[0x1e068d]['replacementBytes'])[-4:],'little'),(1<<SHIFT)*8)


if __name__=='__main__':unittest.main()
