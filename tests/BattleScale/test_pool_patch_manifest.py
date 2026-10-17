"""Offline regressions for instruction geometry that can corrupt native patches."""
import importlib.util
import hashlib
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/battle-scale"))
import build_pool_patch_manifest as generator
import audit_pool_tick_spill as tick_spill
from native_instruction_semantics import memory_operand_writes


class InstructionGeometryTests(unittest.TestCase):
    def setUp(self):
        self.decoder = generator.capstone.Cs(generator.capstone.CS_ARCH_X86, generator.capstone.CS_MODE_64)
        self.decoder.detail = True

    def decode(self, raw):
        instructions = list(self.decoder.disasm(bytes.fromhex(raw), 0x1805CEA31))
        self.assertEqual(len(instructions), 1)
        self.assertEqual(instructions[0].size, len(bytes.fromhex(raw)))
        return instructions[0]

    def test_word_store_disp32_does_not_overwrite_imm16(self):
        #66 changes operand width, not the ModRMmod2 displacement width.
        original = self.decode("66c7833c4b18010000")
        result, offset, size = generator.replace_displacement_bytes(original, 0x1184B3C, 0x2184B3C)
        self.assertEqual((offset, size), (3, 4))
        self.assertEqual(result.hex(), "66c7833c4b18020000")
        patched = self.decode(result.hex())
        self.assertEqual(patched.operands[0].size, 2)
        self.assertEqual(patched.operands[0].mem.disp, 0x2184B3C)
        self.assertEqual(patched.operands[1].imm, 0)
        self.assertEqual(result[7:], b"\x00\x00")

    def test_sib_word_store_keeps_sentinel_immediate(self):
        original = self.decode("66c7844b009d6200f907")
        result, offset, size = generator.replace_displacement_bytes(original, 0x629D00, 0xC53A00)
        self.assertEqual((offset, size), (4, 4))
        self.assertEqual(result.hex(), "66c7844b003ac500f907")
        patched = self.decode(result.hex())
        self.assertEqual(patched.operands[0].mem.disp, 0xC53A00)
        self.assertEqual(patched.operands[1].imm, 2041)

    def test_disp8_signed_bounds_are_enforced(self):
        original = self.decode("488b43f8")
        result, _, size = generator.replace_displacement_bytes(original, -8, 120)
        self.assertEqual(size, 1)
        self.assertEqual(self.decode(result.hex()).operands[1].mem.disp, 120)
        with self.assertRaises(ValueError):
            generator.replace_displacement_bytes(original, -8, 128)

    def test_stack_flag_in_mission_function_is_not_a_pointer_offset(self):
        scalar=self.decode('c744244401000001')
        self.assertEqual(scalar.operands[1].imm,0x1000001)
        with self.assertRaisesRegex(ValueError,'Data immediate'):
            generator.require_pointer_operand_shape(scalar,1)
        high=json.loads((ROOT/'evidence/engine-extension/pool-semantic-review-high.json').read_text())
        row=next(s for s in high['sites'] if s['address']=='0x1807096fb')
        self.assertEqual(row['classification'],'other_numeric_arithmetic')
        manifest=json.loads((ROOT/'evidence/engine-extension/native-pool-patch-4080.json').read_text())
        self.assertNotIn('0x1807096fb',{s['address'] for s in manifest['patches']})

    def test_high_region_never_uses_procedure_only_authorization(self):
        folder=ROOT/'evidence/engine-extension'
        mid=json.loads((folder/'pool-semantic-review-mid.json').read_text())
        supplement=json.loads((folder/'pool-patch-generator-review.json').read_text())
        proofs={(s['address'],s['operandIndex']) for s in mid['instructionSiteMappings']}
        proofs.update((s['address'],s['operandIndex']) for s in supplement['highOperandRecheck']['instructionSiteMappings'])
        high=json.loads((folder/'pool-semantic-review-high.json').read_text())
        selected={(s['address'],o['operandIndex']) for s in high['sites'] if s['classification']=='mission_relative'
                  for o in s['matchedOperands'] if o['operandIndex'] in s['largeOperandIndexes']}
        manifest=json.loads((folder/'native-pool-patch-4080.json').read_text())
        for patch in manifest['patches']:
            if 'exact_high_review' not in patch['scopes']:continue
            for operand in patch['operandChanges']:
                key=(patch['address'],operand['operandIndex'])
                if key in selected:self.assertIn(key,proofs)
        # New proofs may have arrived since this blocked manifest snapshot;
        # withholding a newly proved site is conservative and remains valid.
        patched_high={p['address'] for p in manifest['patches'] if 'exact_high_review' in p['scopes']}
        self.assertFalse(patched_high & {s['address'] for s in manifest['highReviewOperandsWithheld']})

    def test_old_value_mismatch_is_rejected(self):
        with self.assertRaises(ValueError):
            generator.replace_displacement_bytes(self.decode("66c7833c4b18010000"), 0x1184B3D, 0x2184B3C)

    def test_spill_provenance_rejects_one_bad_predecessor(self):
        # Store Mission; reload RBX; conditional overwrite on one predecessor;
        # a linear scan of the good branch would incorrectly approve the join.
        start=0x1000
        bad=bytes.fromhex('48894c2470488b5c247085c0740331db90488b8300000001c3')
        body=list(self.decoder.disasm(bad,start))
        use=next(i for i in body if i.mnemonic=='mov' and i.op_str.startswith('rax,'))
        with self.assertRaisesRegex(ValueError,'Unproved reaching definition'):
            tick_spill.backward_proof(body,use.address,'rbx',start,start+5)
        good=bytes.fromhex('48894c2470488b5c247085c07403909090488b8300000001c3')
        body=list(self.decoder.disasm(good,start))
        use=next(i for i in body if i.mnemonic=='mov' and i.op_str.startswith('rax,'))
        _,origins,_=tick_spill.backward_proof(body,use.address,'rbx',start,start+5)
        self.assertEqual(origins,[start+5])

    def test_spill_reload_must_be_dominated_by_initial_store(self):
        start=0x1000
        # A branch skips the initial store, although the store is lexically
        # earlier than the reload. This path must never become a pointer proof.
        body=list(self.decoder.disasm(bytes.fromhex('85c0740548894c2470488b5c2470488b8300000001c3'),start))
        use=next(i for i in body if i.mnemonic=='mov' and i.op_str.startswith('rax,'))
        with self.assertRaisesRegex(ValueError,'bypasses initial Mission store'):
            tick_spill.backward_proof(body,use.address,'rbx',start+4,start+9)

    def test_simd_store_overlapping_pointer_home_is_not_readonly(self):
        # A16-byte store startingeightbytesbefore aQWORDhome destroysit,
        # even where Capstone incorrectly labels MOVUPS destination READ.
        store=self.decode('0f114c2468')
        load=self.decode('0f104c2468')
        damaged=[]
        for ins in (store,load):
            for n,op in enumerate(ins.operands):
                if memory_operand_writes(ins,n) and op.mem.disp<0x78 and op.mem.disp+op.size>0x70:
                    damaged.append(ins.mnemonic)
        self.assertEqual(damaged,['movups'])

    def test_job_loop_byte_comparison_avoids_sign_extended_minus_one(self):
        wrong = self.decode("83faff")
        corrected = self.decode("80faff")
        self.assertEqual(wrong.operands[1].imm, -1)
        self.assertEqual(corrected.operands[1].imm, 255)
        self.assertEqual(corrected.reg_name(corrected.operands[0].reg), "dl")
        completed = 0
        while True:
            completed += 1
            if not ((completed & 0xFF) < 255):
                break
        self.assertEqual(completed, 255)

    def test_computed_companion_field_remains_inside_last_expanded_slot(self):
        profile=json.loads((ROOT/'evidence/engine-extension/native-pool-layout.json').read_text())
        layout=generator.ExpandedLayout(profile,4080,expand_jobs=True)
        # ADD offset; ADD Mission differs from a Mission-base displacement, but
        # must resolve to the same field in every companion slot.
        field=layout.mission_offset(0xd70740)
        pool=next(p for p in layout.pools if int(p['elementStride'],16)==0x2c8 and int(p['missionOffset'],16)==layout.mission_offset(0xd705c8))
        self.assertEqual(field-int(pool['missionOffset'],16),0x178)
        last=field+4079*0x2c8
        self.assertGreaterEqual(last,int(pool['lastSlotOffset'],16))
        self.assertLess(last+16,int(pool['indexTableOffset'],16))

    def test_static_approval_is_bound_to_code_evidence_and_exact_patches(self):
        patches=[{'address':'0x180000010','original':'90','replacement':'cc'}]
        sources={'proof.json':{'sha256':'a'*64}}
        approval={'approvedForRestrictedOfflineTest':True,'sourceSha256':'b'*64,
            'canonicalPatchSetSha256':generator.canonical_patch_set_sha256(patches),
            'generatorSourceSha256':'c'*64,'approvedSourceEvidenceSha256ByName':{'proof.json':'a'*64}}
        check=lambda a,p=patches,s=sources,g='c'*64,u={'remaining':0}: generator.readiness_approval_errors(a,'b'*64,p,s,g,u)
        self.assertEqual(check(approval),[])
        self.assertTrue(check(None))
        self.assertTrue(check(dict(approval,approvedForRestrictedOfflineTest=1)))
        self.assertTrue(check(approval,p=[dict(patches[0],replacement='91')]))
        self.assertTrue(check(approval,s={'proof.json':{'sha256':'d'*64}}))
        self.assertTrue(check(approval,s={}))
        self.assertTrue(check(approval,g='d'*64))
        self.assertTrue(check(approval,u={'remaining':1}))
        # The approval's own hash must not create a cyclic dependency.
        self.assertEqual(check(approval,s={**sources,'native-pool-static-readiness-review.json':{'sha256':'e'*64}}),[])

    def test_saved_manifest_readiness_and_every_instruction_span(self):
        path = ROOT / "evidence/engine-extension/native-pool-patch-4080.json"
        manifest = json.loads(path.read_text())
        ready=manifest['status']=='experimental-static-candidate'
        self.assertEqual(manifest['readiness']['staticReady'],ready)
        self.assertEqual(manifest['coverage']['semanticCoverageComplete'],ready)
        self.assertFalse(manifest['readiness']['runtimeVerified'])
        self.assertTrue(manifest['readiness']['launchHold'])
        if ready:
            approval=json.loads((ROOT/'evidence/engine-extension/native-pool-static-readiness-review.json').read_text())
            self.assertEqual(generator.readiness_approval_errors(approval,manifest['sourceSha256'],manifest['patches'],
                manifest['sourceEvidence'],hashlib.sha256(Path(generator.__file__).read_bytes()).hexdigest(),
                manifest['readiness']['unresolvedStaticObligations']),[])
            self.assertFalse(manifest['blockers'])
        else:self.assertTrue(manifest['blockers'])
        renderer = json.loads((ROOT/'evidence/engine-extension/native-bone-mapping-patch.json').read_text())
        renderer_by_rva = {p['rva']:p for p in renderer['edits']}
        self.assertEqual({p['rva'] for p in manifest['patches'] if 'renderer_page_geometry_and_bounds' in p['scopes']}, set(renderer_by_rva))
        ranged = json.loads((ROOT/'evidence/engine-extension/native-ranged-page-patch.json').read_text())
        ranged_by_rva = {p['rva']:p for p in ranged['edits']}
        self.assertEqual({p['rva'] for p in manifest['patches'] if 'ranged_page_geometry_and_bounds' in p['scopes']}, set(ranged_by_rva))
        renderer_by_rva.update(ranged_by_rva)
        for patch in manifest["patches"]:
            if patch.get('decodeContract') == 'reviewed-renderer-page-and-guard-span':
                reviewed = renderer_by_rva[patch['rva']]
                for field, raw_field, instructions in (('original','originalBytes','originalInstructions'),
                                                       ('replacement','replacementBytes','candidateInstructions')):
                    self.assertEqual(patch[field], reviewed[raw_field])
                    decoded = list(self.decoder.disasm(bytes.fromhex(patch[field]),int(patch['address'],16)))
                    self.assertEqual(sum(i.size for i in decoded),len(bytes.fromhex(patch[field])))
                    self.assertEqual([{'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
                        for i in decoded],reviewed[instructions])
                continue
            if patch.get('decodeContract') == 'reviewed-single-mode-zero-three-instruction-span':
                self.assertEqual(patch['address'],'0x180710c10')
                self.assertEqual(patch['original'],'890dbe556900')
                self.assertEqual(patch['replacement'],'85c97402cd29')
                continue  # The dedicated mode-guard tests verify both branches.
            if patch.get('decodeContract') == 'reviewed-contact-capacity-multi-instruction-span':
                address=int(patch['address'],16)
                expected={0x1805b79b8:(8,0x1805b79c0),0x1805b7a2c:(16,0x1805b80a2)}
                self.assertIn(address,expected)
                size,target=expected[address]
                old=list(self.decoder.disasm(bytes.fromhex(patch['original']),address))
                new=list(self.decoder.disasm(bytes.fromhex(patch['replacement']),address))
                self.assertEqual(sum(i.size for i in old),size)
                self.assertEqual(sum(i.size for i in new),size)
                self.assertEqual(len(bytes.fromhex(patch['original'])),size)
                self.assertEqual(len(bytes.fromhex(patch['replacement'])),size)
                self.assertEqual(new[-2].mnemonic,'jbe')
                self.assertEqual(new[-2].operands[0].imm,target)
                self.assertEqual(new[-1].mnemonic,'int')
                self.assertEqual(new[-1].operands[0].imm,0x29)
                continue  # Dedicated guard tests enumerate all256 byte inputs.
            old, new = self.decode(patch["original"]), self.decode(patch["replacement"])
            self.assertEqual(old.size, new.size)
            self.assertEqual(old.mnemonic, new.mnemonic)
            for change in patch["operandChanges"]:
                operand = new.operands[change["operandIndex"]]
                actual = operand.imm if operand.type == generator.X86_OP_IMM else operand.mem.disp
                self.assertEqual(actual, change["replacementValue"])


if __name__ == "__main__":
    unittest.main()
