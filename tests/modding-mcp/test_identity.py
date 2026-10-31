import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('mcp_identity', REPO / 'tools/modding-mcp/identity.py')
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class IdentityTests(unittest.TestCase):
    def test_changed_and_missing_content_fails(self):
        with tempfile.TemporaryDirectory(prefix='mnbl-mcp-identity-') as directory:
            root = Path(directory).resolve()
            file = root / 'SubModule.xml'
            file.write_bytes(b'<Module/>')
            manifest = {'schema': 'mnbl.modding-mcp.identity.v1',
                        'binaries_and_module_descriptors': [identity.file_identity(file, root)]}
            self.assertTrue(identity.verify(root, manifest)['passed'])
            file.write_bytes(b'<Changed>')  # same length: size alone cannot detect drift
            result = identity.verify(root, manifest)
            self.assertFalse(result['passed'])
            self.assertEqual(result['failures'][0]['fields'], ['sha256'])
            file.unlink()
            self.assertEqual(identity.verify(root, manifest)['failures'][0]['status'], 'unreadable')

    def test_empty_and_escaping_manifests_fail_closed(self):
        with tempfile.TemporaryDirectory(prefix='mnbl-mcp-identity-') as directory:
            root = Path(directory)
            with self.assertRaises(ValueError):
                identity.verify(root, {'schema': 'mnbl.modding-mcp.identity.v1', 'binaries_and_module_descriptors': []})
            with self.assertRaises(ValueError):
                identity.verify(root, {'schema': 'mnbl.modding-mcp.identity.v1', 'binaries_and_module_descriptors': [{'path': '../escape'}]})

    def test_mvid_matches_independent_rea_producers(self):
        # Real independently retained REA metadata, not this parser's captured values.
        bundle = json.loads((REPO / 'evidence/modding-mcp/api-audit-rea-2026-10-30.json').read_text())
        checked = 0
        for record in bundle['records']:
            if record['operation'] != 'inspect_managed_members':
                continue
            result = record['normalized_result']
            if 'module' not in result:
                continue
            data = Path(result['artifact']['path']).read_bytes()
            self.assertEqual(hashlib.sha256(data).hexdigest(), result['artifact']['sha256'])
            self.assertEqual(identity.managed_mvid(data), result['module']['mvid'])
            checked += 1
        self.assertEqual(checked, 3)

    def test_alias_duplicate_paths_fail_closed(self):
        with tempfile.TemporaryDirectory(prefix='mnbl-mcp-identity-') as directory:
            root = Path(directory).resolve()
            file = root / 'SubModule.xml'
            file.write_bytes(b'<Module/>')
            record = identity.file_identity(file, root)
            for alias in ['./SubModule.xml', 'submodule.xml']:
                with self.assertRaises(ValueError):
                    identity.verify(root, {'schema': 'mnbl.modding-mcp.identity.v1',
                                          'binaries_and_module_descriptors': [record, {**record, 'path': alias}]})

    def test_native_has_no_managed_identity(self):
        manifest = json.loads((REPO / 'evidence/modding-mcp/research/identity/manifest.json').read_text())
        path = Path(manifest['game_root']) / 'bin/Win64_Shipping_wEditor/TaleWorlds.Native.dll'
        self.assertIsNone(identity.managed_mvid(path.read_bytes()))


if __name__ == '__main__':
    unittest.main(verbosity=2)
