"""Negative closure and implicit-clobber cases; no game/artifact execution."""
from pathlib import Path
import importlib.util
import hashlib
import sys
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'build/battle-scale/python-libs'),str(ROOT/'tools/battle-scale')]
import capstone
from bounded_native_register_effects import install, exception_metadata

spec=importlib.util.spec_from_file_location('review_flow',ROOT/'tools/battle-scale/classify_pool_references.py')
flow=importlib.util.module_from_spec(spec);spec.loader.exec_module(flow)

class MetadataPE:
    def __init__(self, blobs): self.blobs = blobs
    def get_data(self, rva, size):
        for start, raw in self.blobs.items():
            if start <= rva < start + len(raw): return raw[rva-start:rva-start+size]
        return b''


def metadata_fixture(bodies):
    entries = {}; groups = {}; blobs = {}
    for index, (start, raw) in enumerate(bodies.items()):
        end = start + len(bytes.fromhex(raw)); unwind = 0x100000 + index * 0x100
        entries[start] = SimpleNamespace(BeginAddress=start, EndAddress=end, UnwindData=unwind)
        groups[start] = {'runtimeRanges': [{'start': hex(start), 'endExclusive': hex(end)}]}
        blobs[unwind] = bytes.fromhex('01000000')
    return {'pe': MetadataPE(blobs), 'base': 0, 'entries': entries, 'groups': groups}


def summarize(bodies,target=0x1000,decode_gap=False,indirect=False,metadata=True,metadata_ns=None):
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    decoded={start:{i.address:i for i in md.disasm(bytes.fromhex(raw),start)}for start,raw in bodies.items()}
    def reg(ins,number):
        name=ins.reg_name(number)or''
        return flow.ALIASES.get(name,name if name.startswith('xmm')else None)
    ns={'leaf_register_effects':{},'flow':flow,'reg':reg,
        'eligible_procedure':lambda a:True,'groups':{a:{}for a in bodies},
        'add_leaf_group':lambda a:False,
        'decode':lambda a:(decoded[a],{},['gap']if decode_gap else[],['indirect']if indirect else[])}
    if metadata: ns.update(metadata_fixture(bodies))
    if metadata_ns is not None: ns.update(metadata_ns)
    install(ns)
    return ns['leaf_preserved_registers'](target)

class RegisterEffectTests(unittest.TestCase):
    def test_incomplete_fallthrough_rejected(self):
        for raw in ['90','e80b000000','750e']:
            with self.subTest(raw=raw):self.assertEqual(summarize({0x1000:raw,0x1010:'c3'}),set())
    def test_unmodeled_vector_or_state_restore_rejected(self):
        for raw in ['c5fc57c0c3','0fae09c3','0fae29c3','c5fc77c3','480fae09c3','480fae29c3']:
            with self.subTest(raw=raw):self.assertEqual(summarize({0x1000:raw}),set())
    def test_transitive_partial_register_write(self):
        result=summarize({0x1000:'e80b000000c3',0x1010:'b101c3'})
        self.assertNotIn('rcx',result);self.assertIn('r8',result)
    def test_transitive_unknown_call_clobbers_all(self):
        self.assertEqual(summarize({0x1000:'e80b000000c3',0x1010:'e80b000000c3'}),set())
    def test_recursive_call_clobbers_all(self):
        self.assertEqual(summarize({0x1000:'e8fbffffffc3'}),set())
    def test_direct_tail_preservation(self):
        result=summarize({0x1000:'e90b000000',0x1010:'b201c3'})
        self.assertNotIn('rdx',result);self.assertIn('rcx',result)
    def test_indirect_call_and_tail_rejected(self):
        for raw in ['ffd0c3','ffe0','48ff18c3','48ff28c3']:
            with self.subTest(raw=raw):self.assertEqual(summarize({0x1000:raw}),set())
    def test_privileged_or_unmodeled_instruction_family_rejected(self):
        for raw in ['fac3','0f01cfc3','0f01d7c3','0fa2c3']:
            with self.subTest(raw=raw):self.assertEqual(summarize({0x1000:raw}),set())
    def test_gap_indirect_or_empty_decode_rejected(self):
        self.assertEqual(summarize({0x1000:'c3'},decode_gap=True),set())
        self.assertEqual(summarize({0x1000:'c3'},indirect=True),set())
        self.assertEqual(summarize({0x1000:''}),set())
    def test_branch_path_write_counted(self):
        result=summarize({0x1000:'7502b101c3'})
        self.assertNotIn('rcx',result);self.assertIn('r9',result)
    def test_nonstandard_exit_and_no_exit_rejected(self):
        for raw in ['f4','48cf','c20800','ebfe']:
            with self.subTest(raw=raw):self.assertEqual(summarize({0x1000:raw}),set())

    def test_missing_exception_metadata_rejects_summary(self):
        self.assertEqual(summarize({0x1000:'c3'},metadata=False),set())

    def test_handler_free_and_reviewed_leaf(self):
        ns=metadata_fixture({0x1000:'c3'})
        self.assertIsNotNone(exception_metadata(ns,0x1000))
        ns['entries']={}
        self.assertIsNone(exception_metadata(ns,0x1000))
        ns['reviewed_leaf_ranges']=[{'entry':'0x1000','endExclusive':'0x1001'}]
        self.assertIsNotNone(exception_metadata(ns,0x1000))

    def test_unknown_flags_and_truncated_metadata(self):
        for raw in ['','01','09000000','19000000','39000000','02000000','01010000','01000100']:
            with self.subTest(raw=raw):
                ns=metadata_fixture({0x1000:'c3'});ns['pe'].blobs[0x100000]=bytes.fromhex(raw)
                # 01010000 has a valid version/flags/count despite prolog size;
                # malformed code bounds are decode responsibility, not EH gate.
                if raw=='01010000':self.assertIsNotNone(exception_metadata(ns,0x1000))
                else:self.assertIsNone(exception_metadata(ns,0x1000))

    def cleanup_fixture(self):
        ns=metadata_fixture({0x1000:'c3'})
        ns['pe'].blobs[0x100000]=bytes.fromhex('11000000')+struct.pack('<II',0x9e3177,0x200000)
        ns['pe'].blobs[0x200000]=struct.pack('<10I',0x19930522,1,0x1234,0,0,3,0x3456,0x20,0,0)
        return ns

    def test_cleanup_only_requires_stock_binary_identity(self):
        ns=self.cleanup_fixture()
        self.assertIsNone(exception_metadata(ns,0x1000))
        self.assertIsNotNone(exception_metadata(ns,0x1000,True))

    def test_stock_identity_uses_pe_buffer_not_reused_source_name(self):
        raw=b'exact simulated immutable PE input'
        ns=self.cleanup_fixture();ns['source']=0x1806089d0;ns['pe'].__data__=raw
        with patch('bounded_native_register_effects.STOCK_SHA256',hashlib.sha256(raw).hexdigest()):
            self.assertIn('r8',summarize({0x1000:'c3'},metadata_ns=ns))
            ns['source']=raw;ns['pe'].__data__=b'another image'
            self.assertEqual(summarize({0x1000:'c3'},metadata_ns=ns),set())

    def test_catch_spec_and_unknown_cpp_metadata_rejected(self):
        for field,value in [(0,0x19930521),(3,1),(4,0x2345),(8,0x4567)]:
            with self.subTest(field=field):
                ns=self.cleanup_fixture();info=list(struct.unpack('<10I',ns['pe'].blobs[0x200000]));info[field]=value
                ns['pe'].blobs[0x200000]=struct.pack('<10I',*info)
                self.assertIsNone(exception_metadata(ns,0x1000,True))
        ns=self.cleanup_fixture();ns['pe'].blobs[0x100000]=bytes.fromhex('11000000')+struct.pack('<II',0x123456,0x200000)
        self.assertIsNone(exception_metadata(ns,0x1000,True))
        ns=self.cleanup_fixture();ns['pe'].blobs[0x200000]=b'\x00'*39
        self.assertIsNone(exception_metadata(ns,0x1000,True))

    def test_chain_parent_must_be_exact_same_group_and_checked(self):
        ns=metadata_fixture({0x1000:'c3',0x2000:'c3'})
        ns['pe'].blobs[0x100100]=bytes.fromhex('21000000')+struct.pack('<III',0x1000,0x1001,0x100000)
        self.assertIsNone(exception_metadata(ns,0x2000))
        ns['groups'][0x2000]['runtimeRanges']+=ns['groups'][0x1000]['runtimeRanges']
        self.assertIsNotNone(exception_metadata(ns,0x2000))
        ns['pe'].blobs[0x100000]=bytes.fromhex('09000000')
        self.assertIsNone(exception_metadata(ns,0x2000))

    def test_chain_cycle_mismatch_and_truncation_rejected(self):
        for parent,end,unwind in [(0x1000,0x1001,0x100000),(0x1000,0x1002,0x100000),(0x1000,0x1001,0x100001)]:
            ns=metadata_fixture({0x1000:'c3'})
            ns['pe'].blobs[0x100000]=bytes.fromhex('21000000')+struct.pack('<III',parent,end,unwind)
            self.assertIsNone(exception_metadata(ns,0x1000))
        ns=metadata_fixture({0x1000:'c3'});ns['pe'].blobs[0x100000]=bytes.fromhex('21000000')
        self.assertIsNone(exception_metadata(ns,0x1000))

if __name__=='__main__':unittest.main()
