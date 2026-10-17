"""Exercise package creation/refusal against a synthetic PE, never the game."""
import contextlib
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/"tools/battle-scale"))
import prepare_native_pool_test as package
from test_native_patch_copy import synthetic_pe, manifest_for


class NativeTestPackageTests(unittest.TestCase):
    def test_all_ranged_edits_required_at_exact_address(self):
        edits=[dict(address=hex(0x180000000+r),rva=hex(r),original=o,replacement=n) for r,o,n in package.RANGED_PAGE_EDITS]
        package.validate_ranged_page_edits(edits)
        for index in range(len(edits)):
            for mode in ('missing','weakened','moved','duplicate'):
                with self.subTest(index=index,mode=mode):
                    changed=[dict(e) for e in edits]
                    if mode=='missing':changed.pop(index)
                    elif mode=='weakened':changed[index]['replacement']=changed[index]['original']
                    elif mode=='moved':changed[index]['rva']=hex(int(changed[index]['rva'],16)+1)
                    else:changed.append(dict(changed[index]))
                    with self.assertRaisesRegex(package.PatchError,'exact ranged page edit'):
                        package.validate_ranged_page_edits(changed)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        source_dir = self.root/"Game/bin/Win64_Shipping_Client"
        source_dir.mkdir(parents=True)
        self.source = source_dir/"synthetic.dll"
        data = bytearray(synthetic_pe()) + bytearray(0x8000)
        struct.pack_into("<H", data, 0x86, 7)
        struct.pack_into("<I", data, 0x98+60, 0x400)
        struct.pack_into("<I", data, 0x98+56, 0x713000)
        struct.pack_into("<IIII", data, 0x188+8, 0x1000, 0x710000, 0x1000, 0x400)
        struct.pack_into("<IIII", data, 0x188+40+8, 0x200, 0x712000, 0x200, 0x1600)
        data[0x188+80:0x188+80+7] = b".guards"
        struct.pack_into("<IIII", data, 0x188+80+8, 0x1000, 0x5b7000, 0x1000, 0x1800)
        struct.pack_into("<I", data, 0x188+80+36, 0x60000020)
        data[0x188+120:0x188+128] = b".render\0"
        struct.pack_into("<IIII", data, 0x188+120+8, 0x2000, 0x88000, 0x2000, 0x2800)
        struct.pack_into("<I", data, 0x188+120+36, 0x60000020)
        data[0x188+160:0x188+168] = b".upload\0"
        struct.pack_into("<IIII", data, 0x188+160+8, 0x1000, 0x1e0000, 0x1000, 0x4800)
        struct.pack_into("<I", data, 0x188+160+36, 0x60000020)
        data[0x1010:0x1017] = bytes.fromhex("890dbe556900c3")
        self.contact_guards = [
            {"id": "synthetic-contact-producer", "address": "0x1805b79b8", "rva": "0x5b79b8", "fileOffset": 0x21b8,
             "original": "0f1f840000000000", "replacement": "4180fe3f7602cd29"},
            {"id": "synthetic-contact-cache", "address": "0x1805b7a2c", "rva": "0x5b7a2c", "fileOffset": 0x222c,
             "original": "4863c2488d0c80498d04c9e966060000", "replacement": "4883e820807808400f8668060000cd29"},
        ]
        self.renderer_guards = [
            {"id":"synthetic-renderer-mapping", "address":"0x1800886d5", "rva":"0x886d5", "fileOffset":0x2ed5,
             "original":"4d3bf70f8781000000488d9d080100004d2bfe4903de4c8d6d0849ffc74d8d6cc500",
             "replacement":"4183ff2072020f0b4d39fe777d4a8d9c35080100004d2bfe4c8d6cc50849ffc79090"},
            {"id":"synthetic-renderer-matrix", "address":"0x180089925", "rva":"0x89925", "fileOffset":0x4125,
             "original":"488d99080100004803de4c8d710848ffc74d8d34c6660f1f440000",
             "replacement":"8d1c3783fb1f76020f0b488d9c31080100004c8d74c10848ffc790"}]
        for guard in self.contact_guards + self.renderer_guards:
            guard.update(reason="Synthetic packaging test only", evidenceIds=["synthetic-only"])
            original = bytes.fromhex(guard["original"])
            for offset in (guard["fileOffset"], guard["fileOffset"] + 0x40):
                data[offset:offset+len(original)] = original
        # Use the independently reviewed byte artifact for the geometry fixture.
        reviewed = json.loads((ROOT/'evidence/engine-extension/native-bone-mapping-patch.json').read_text())
        self.renderer_geometry = []
        for edit in reviewed['edits']:
            rva = int(edit['rva'],16)
            if rva in (0x886d5,0x89925): continue
            offset = rva - (0x88000 if rva < 0x90000 else 0x1e0000) + (0x2800 if rva < 0x90000 else 0x4800)
            entry = dict(id='renderer-geometry-'+edit['rva'],address=hex(0x180000000+rva),rva=edit['rva'],fileOffset=offset,
                original=edit['originalBytes'],replacement=edit['replacementBytes'],reason='Independent reviewed geometry fixture',evidenceIds=['synthetic-only'])
            self.renderer_geometry.append(entry)
            for location in (offset,offset+0x400):
                data[location:location+len(bytes.fromhex(entry['original']))] = bytes.fromhex(entry['original'])
        # Two additional sections carry the independently reviewed face-morph pages.
        for index, name, rva, offset in ((5,b'.face',0x87000,0x5800),(6,b'.human',0x23f000,0x6800)):
            header=0x188+40*index
            data[header:header+8]=name.ljust(8,b'\0')
            struct.pack_into('<IIII',data,header+8,0x1000,rva,0x1000,offset)
            struct.pack_into('<I',data,header+36,0x60000020)
        self.ranged_edits=[]
        for rva, original, replacement in package.RANGED_PAGE_EDITS:
            base, raw = (0x87000,0x5800) if rva<0x88000 else (0x88000,0x2800) if rva<0x90000 else (0x1e0000,0x4800) if rva<0x200000 else (0x23f000,0x6800)
            offset=rva-base+raw
            entry=dict(id='ranged-'+hex(rva),address=hex(0x180000000+rva),rva=hex(rva),fileOffset=offset,
                original=original,replacement=replacement,reason='Reviewed ranged geometry fixture',evidenceIds=['synthetic-only'])
            self.ranged_edits.append(entry)
            data[offset:offset+len(bytes.fromhex(original))]=bytes.fromhex(original)
        self.source.write_bytes(data)
        self.manifest = manifest_for(data)
        self.manifest.update(sourcePath=str(self.source), coverage={"semanticCoverageComplete": True},
                             readiness={"staticReady": True}, sourceEvidence={})
        self.manifest["patches"] = [{"id": "synthetic-single-guard", "address": "0x180710c10",
            "rva": "0x710c10", "fileOffset": 0x1010, "original": "890dbe556900", "replacement": "85c97402cd29",
            "reason": "Synthetic packaging test only", "evidenceIds": ["synthetic-only"]}] + self.contact_guards + self.renderer_guards + self.renderer_geometry + self.ranged_edits
        self.manifest['patches'].append(dict(id='approved-extra',address='0x180088100',rva='0x88100',fileOffset=0x2900,
            original='00',replacement='90',reason='Approved synthetic additional instruction',evidenceIds=['synthetic-only']))
        self.manifest_path = self.root/"manifest.json"
        self.save_manifest()
        self.module = self.root/"build/battle-scale/module/MNBL.BattleScale.dll"
        self.module.parent.mkdir(parents=True)
        self.module.write_bytes(b"synthetic module bytes; never executed")
        xml = self.root/"Modules/MNBL.BattleScale/SubModule.xml"
        xml.parent.mkdir(parents=True)
        xml.write_text("<synthetic-test-only/>")
        self.input = self.root/"Probe.cs"
        self.input.write_text("// synthetic source hash input")
        self.build = {"status": "built", "moduleSha256": package.sha(self.module),
            "sources": [{"path": str(self.input), "sha256": package.sha(self.input)}]}
        (self.root/"build/battle-scale/build-receipt.json").write_text(json.dumps(self.build))
        audit = self.root/"evidence/engine-extension"
        audit.mkdir(parents=True)
        generator = self.root/'tools/battle-scale/build_pool_patch_manifest.py'
        generator.parent.mkdir(parents=True)
        generator.write_bytes(b'synthetic generator identity')
        self.manifest['generatorSourceSha256'] = package.sha(generator)
        approval = audit/'native-pool-static-readiness-review.json'
        rows = [{key:p[key] for key in ('address','original','replacement')}
                for p in sorted(self.manifest['patches'],key=lambda p:int(p['address'],16))]
        approval.write_text(json.dumps(dict(approvedForRestrictedOfflineTest=True,sourceSha256=self.manifest['sourceSha256'],
            canonicalPatchSetSha256=package.hashlib.sha256(json.dumps(rows,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
            generatorSourceSha256=package.sha(generator),approvedSourceEvidenceSha256ByName={})))
        self.manifest['sourceEvidence'] = {approval.name:dict(path=str(approval),sha256=package.sha(approval))}
        self.save_manifest()
        (audit/"native-pool-module-boundaries.json").write_text(json.dumps({
            "filesInspected": 1, "skipped": [], "nativeModules": [{"path": str(self.source), "sha256": package.sha(self.source)}], "managedIdentities": []}))
        self.official = self.root/"Game/Modules/Native/bin/Win64_Shipping_Client/Official.dll"
        self.official.parent.mkdir(parents=True)
        self.official.write_bytes(b"synthetic official assembly")
        (audit/"pool-managed-expansion-audit.json").write_text(json.dumps({"skipped": [], "artifacts": [
            {"path": str(self.official), "sha256": package.sha(self.official)}]}))
        for name in package.MODULE_IDS[:-1]:
            descriptor = self.root/"Game/Modules"/name/"SubModule.xml"
            descriptor.parent.mkdir(parents=True, exist_ok=True)
            descriptor.write_text("<synthetic-module/>")
        self.watchdog = source_dir / 'Watchdog/Watchdog.exe'
        self.watchdog.parent.mkdir()
        self.watchdog.write_bytes(b'inert synthetic watchdog')
        self.exit_contract_path = audit / 'native-watchdog-shutdown-contract.json'
        self.exit_contract = dict(status='reviewed-build-specific-watchdog-shutdown',
            nativeSourceSha256=package.sha(self.source),
            patchedCandidateSha256=package.run('verify', self.source, self.manifest_path)['patchedSha256'],
            expectedWatchdogExitCode=-1, watchdogPath=str(self.watchdog), watchdogSha256=package.sha(self.watchdog))
        self.exit_contract_path.write_text(json.dumps(self.exit_contract))
        self.output = self.root/"build/battle-scale/new-package"
        self.root_patch = patch.object(package, "ROOT", self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)

    def save_manifest(self):
        self.manifest_path.write_text(json.dumps(self.manifest))

    def prepare(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return package.prepare(self.manifest_path, self.output)

    def test_fresh_directory_prepares_matching_held_package(self):
        before = self.source.read_bytes()
        result = self.prepare()
        self.assertEqual(result["status"], "offline-test-package-prepared")
        self.assertEqual(self.source.read_bytes(), before)
        profile = json.loads((self.output/"native-pool-test-profile.json").read_text())
        self.assertEqual(profile["patchedSha256"], package.sha(self.output/"TaleWorlds.Native.dll"))
        self.assertEqual(profile["probeModuleSha256"], package.sha(self.output/"MNBL.BattleScale.dll"))
        self.assertEqual(profile["configuredMaxThreadCount"], 16)
        self.assertEqual(profile["schemaVersion"], 2)
        self.assertEqual(profile["allowedModuleIds"], package.MODULE_IDS)
        identities = {x["relativePath"]: x["sha256"] for x in profile["trustedFiles"]}
        self.assertEqual(identities["bin/Win64_Shipping_Client/synthetic.dll"], profile["patchedSha256"])
        self.assertEqual(identities["Modules/Native/bin/Win64_Shipping_Client/Official.dll"], package.sha(self.official))
        self.assertEqual(identities['bin/Win64_Shipping_Client/Watchdog/Watchdog.exe'], package.sha(self.watchdog))
        self.assertEqual(profile['exitContract']['sha256'], package.sha(self.exit_contract_path))
        self.assertTrue(profile["gameLaunchHeld"])
        self.assertFalse(profile["runtimeVerified"])
        self.assertEqual((self.output/"TaleWorlds.Native.dll").read_bytes()[0x1010:0x1017], bytes.fromhex("85c97402cd29c3"))

    def test_changed_watchdog_is_rejected(self):
        self.watchdog.write_bytes(b'different watchdog build')
        with self.assertRaises(package.PatchError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_expanded_workload_requires_admission(self):
        with self.assertRaisesRegex(package.PatchError, 'require public spawn admission'):
            package.prepare(self.manifest_path, self.output, workload='cavalry', sustained_seconds=300)
        self.assertFalse(self.output.exists())

    def test_invalid_workload_refuses_before_output(self):
        for profile, seconds, cycles in [('unknown', 300, 3), ('ranged', 0, 3), ('cavalry', 300, 0)]:
            with self.subTest(profile=profile, seconds=seconds, cycles=cycles):
                with self.assertRaisesRegex(package.PatchError, 'Invalid native workload'):
                    package.prepare(self.manifest_path, self.output, True, profile, seconds, cycles)
                self.assertFalse(self.output.exists())

    def test_admission_patch_failure_cleans_partial_offline_copy(self):
        def fail(command, **kwargs):
            Path(command[command.index('-Output') + 1]).write_bytes(b'partial managed output')
            raise RuntimeError('injected managed patch failure')
        with patch.object(package.subprocess, 'run', side_effect=fail), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(RuntimeError, 'injected managed patch'):
                package.prepare(self.manifest_path, self.output, True, 'cavalry', 300, 3)
        self.assertFalse(self.output.exists())

    def test_admission_binds_managed_copy_and_workload(self):
        managed = self.source.parent / 'TaleWorlds.MountAndBlade.dll'
        managed.write_bytes(b'inert original managed fixture')
        audit = self.root / 'evidence/engine-extension/native-pool-module-boundaries.json'
        inventory = json.loads(audit.read_text())
        inventory['managedIdentities'] = [dict(path=str(managed), sha256=package.sha(managed))]
        inventory['filesInspected'] += 1
        audit.write_text(json.dumps(inventory))
        def patch_copy(command, **kwargs):
            output = Path(command[command.index('-Output') + 1])
            output.write_bytes(b'inert patched managed fixture')
            receipt = Path(command[command.index('-Receipt') + 1])
            receipt.write_text(json.dumps(dict(sourceSha256=package.sha(managed), patchedSha256=package.sha(output), moduleSha256=package.sha(self.module))))
        with patch.object(package.subprocess, 'run', side_effect=patch_copy), contextlib.redirect_stdout(io.StringIO()):
            result = package.prepare(self.manifest_path, self.output, True, 'ranged', 300, 3)
        self.assertTrue(result['publicSpawnAdmission'])
        self.assertEqual((result['schemaVersion'], result['workload'], result['sustainedSeconds'], result['reuseCycles']), (3, 'ranged', 300, 3))
        self.assertEqual(result['managedEngineSourceSha256'], package.sha(managed))
        trusted = {x['relativePath']: x['sha256'] for x in result['trustedFiles']}
        self.assertEqual(trusted['bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.dll'], package.sha(self.output / managed.name))

    def test_exit_contract_for_other_native_build_is_rejected(self):
        self.exit_contract['nativeSourceSha256'] = '0' * 64
        self.exit_contract_path.write_text(json.dumps(self.exit_contract))
        with self.assertRaises(package.PatchError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_exit_contract_for_other_patch_is_rejected(self):
        self.exit_contract['patchedCandidateSha256'] = '0' * 64
        self.exit_contract_path.write_text(json.dumps(self.exit_contract))
        with self.assertRaises(package.PatchError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_blocked_manifest_does_not_create_directory(self):
        self.manifest["status"] = "blocked"
        self.manifest["blockers"] = ["synthetic gap"]
        self.save_manifest()
        with self.assertRaises(package.PatchError): self.prepare()
        self.assertFalse(self.output.exists())

    def test_incomplete_coverage_does_not_create_directory(self):
        self.manifest["coverage"]["semanticCoverageComplete"] = False
        self.save_manifest()
        with self.assertRaises(package.PatchError): self.prepare()
        self.assertFalse(self.output.exists())

    def test_explicit_static_readiness_cannot_be_missing_false_or_text(self):
        for readiness in ({}, {"staticReady": False}, {"staticReady": "true"}):
            with self.subTest(readiness=readiness):
                self.manifest["readiness"] = readiness
                self.save_manifest()
                with self.assertRaisesRegex(package.PatchError, "static patch safety"): self.prepare()
                self.assertFalse(self.output.exists())

    def test_coverage_requires_a_boolean_approval(self):
        self.manifest["coverage"]["semanticCoverageComplete"] = "true"
        self.save_manifest()
        with self.assertRaisesRegex(package.PatchError, "static patch safety"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_stale_module_and_source_are_rejected(self):
        self.module.write_bytes(b"changed")
        with self.assertRaisesRegex(package.PatchError, "stale probe"): self.prepare()
        self.module.write_bytes(b"synthetic module bytes; never executed")
        self.input.write_text("changed")
        with self.assertRaisesRegex(package.PatchError, "source changed"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_existing_destination_is_not_overwritten(self):
        self.output.mkdir()
        sentinel = self.output/"keep.txt"
        sentinel.write_text("keep")
        with self.assertRaises(package.PatchError): self.prepare()
        self.assertEqual(sentinel.read_text(), "keep")

    def test_game_destination_rejected_before_creation(self):
        self.output = self.root/"Game/new-package"
        with self.assertRaises(package.PatchError): self.prepare()
        self.assertFalse(self.output.exists())

    def test_missing_mode_guard_is_rejected(self):
        self.manifest["patches"][0]["address"] = "0x180710c20"
        self.save_manifest()
        with self.assertRaisesRegex(package.PatchError, "Single0"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_each_contact_guard_is_required_before_writing(self):
        for missing in self.contact_guards:
            with self.subTest(guard=missing["id"]):
                original = self.manifest["patches"]
                self.manifest["patches"] = [p for p in original if p["id"] != missing["id"]]
                self.save_manifest()
                with self.assertRaisesRegex(package.PatchError, "contact.*fail-fast guard"): self.prepare()
                self.assertFalse(self.output.exists())
                self.manifest["patches"] = original

    def test_contact_guard_replacement_cannot_be_weakened(self):
        for guard in self.contact_guards:
            with self.subTest(guard=guard["id"]):
                replacement = guard["replacement"]
                guard["replacement"] = replacement[:-4] + "9090"
                self.save_manifest()
                with self.assertRaisesRegex(package.PatchError, "contact.*fail-fast guard"): self.prepare()
                self.assertFalse(self.output.exists())
                guard["replacement"] = replacement

    def test_correct_contact_label_cannot_hide_wrong_patch_location(self):
        for guard in self.contact_guards:
            with self.subTest(guard=guard["id"]):
                old_rva, old_offset = guard["rva"], guard["fileOffset"]
                guard["rva"] = hex(int(old_rva, 16) + 0x40)
                guard["fileOffset"] += 0x40
                self.save_manifest()
                with self.assertRaisesRegex(package.PatchError, "contact.*fail-fast guard"): self.prepare()
                self.assertFalse(self.output.exists())
                guard["rva"], guard["fileOffset"] = old_rva, old_offset

    def test_renderer_guards_cannot_be_missing_weakened_or_moved(self):
        import copy
        original = copy.deepcopy(self.manifest['patches'])
        for target in self.renderer_guards:
            for mutation in ('missing', 'weakened', 'moved'):
                with self.subTest(guard=target['id'], mutation=mutation):
                    self.manifest['patches'] = copy.deepcopy(original)
                    guard = next(p for p in self.manifest['patches'] if p['id'] == target['id'])
                    if mutation == 'missing': self.manifest['patches'].remove(guard)
                    elif mutation == 'weakened': guard['replacement'] = guard['replacement'].replace('0f0b','9090')
                    else:
                        guard['rva'] = hex(int(guard['rva'],16)+0x40)
                        guard['fileOffset'] += 0x40
                    self.save_manifest()
                    with self.assertRaisesRegex(package.PatchError, 'renderer.*fail-fast guard'): self.prepare()
                    self.assertFalse(self.output.exists())

    def test_every_renderer_geometry_edit_is_required_exactly(self):
        import copy
        original = copy.deepcopy(self.manifest['patches'])
        for target in self.renderer_geometry:
            for mutation in ('missing','weakened','moved'):
                with self.subTest(site=target['rva'],mutation=mutation):
                    self.manifest['patches'] = copy.deepcopy(original)
                    edit = next(p for p in self.manifest['patches'] if p['id']==target['id'])
                    if mutation=='missing': self.manifest['patches'].remove(edit)
                    elif mutation=='weakened': edit['replacement'] = '90'*(len(edit['replacement'])//2)
                    else:
                        edit['rva'] = hex(int(edit['rva'],16)+0x400)
                        edit['fileOffset'] += 0x400
                    self.save_manifest()
                    with self.assertRaisesRegex(package.PatchError,'every exact renderer page edit'): self.prepare()
                    self.assertFalse(self.output.exists())

    def test_unapproved_extra_patch_cannot_reuse_static_ready_flags(self):
        self.manifest['patches'].append(dict(id='unapproved',address='0x180088120',rva='0x88120',fileOffset=0x2920,
            original='00',replacement='90',reason='Unapproved synthetic edit',evidenceIds=['synthetic-only']))
        self.save_manifest()
        with self.assertRaisesRegex(package.PatchError,'differs from exact static approval'): self.prepare()
        self.assertFalse(self.output.exists())

    def test_approved_address_cannot_hide_moved_nonrequired_patch(self):
        extra = next(p for p in self.manifest['patches'] if p['id']=='approved-extra')
        extra['rva']='0x88140'
        extra['fileOffset']=0x2940
        self.save_manifest()
        with self.assertRaisesRegex(package.PatchError,'address differs from actual encoded location'): self.prepare()
        self.assertFalse(self.output.exists())

    def test_changed_generator_invalidates_package_approval(self):
        (self.root/'tools/battle-scale/build_pool_patch_manifest.py').write_bytes(b'changed generator')
        with self.assertRaisesRegex(package.PatchError,'differs from exact static approval'): self.prepare()
        self.assertFalse(self.output.exists())

    def test_changed_audited_provider_refuses_before_creation(self):
        self.official.write_bytes(b"unreviewed replacement")
        with self.assertRaisesRegex(package.PatchError, "changed since static audit"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_extra_shipping_provider_refuses_before_creation(self):
        (self.source.parent/"Injected.dll").write_bytes(b"unreviewed")
        with self.assertRaisesRegex(package.PatchError, "shipping executable"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_unreviewed_official_module_provider_refuses(self):
        (self.official.parent/"Injected.dll").write_bytes(b"unreviewed")
        with self.assertRaisesRegex(package.PatchError, "official-module provider"): self.prepare()
        self.assertFalse(self.output.exists())

    def test_missing_audited_official_provider_refuses(self):
        self.official.unlink()
        with self.assertRaisesRegex(package.PatchError, "missing official-module"): self.prepare()
        self.assertFalse(self.output.exists())


if __name__ == "__main__": unittest.main()
