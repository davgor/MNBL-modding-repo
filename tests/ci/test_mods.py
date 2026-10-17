"""tools/ci/mods.py against throwaway git repositories: registry coverage, version bumps, release planning, packaging."""
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout
from pathlib import Path

SPEC = importlib.util.spec_from_file_location('mods', Path(__file__).resolve().parents[2] / 'tools/ci/mods.py')
mods = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mods)

GAME = 'v1.4.8.119303'


def submodule(mod_id, version):
    return (f'<Module><Name value="{mod_id} mod"/><Id value="{mod_id}"/><Version value="{version}"/>'
            '<DependedModules><DependedModule Id="Native"/><DependedModule Id="Bannerlord.Harmony" DependentVersion="v2.4.2"/>'
            '</DependedModules></Module>')


class ModRepo:
    def __init__(self, path):
        self.root = Path(path)
        self.git('init', '-q', '-b', 'main')
        self.write('tools.lock.json', json.dumps({'game': {'version': GAME}}))
        self.write('config/runtime-api-lock.json', json.dumps({'gameVersion': GAME}))
        self.write('config/ci-references.json', json.dumps({'gameVersion': GAME, 'packages': []}))
        self.write('tools/ci/Build-A.ps1', '')
        self.registry([{'id': 'MNBL.A', 'release': True, 'build': 'tools/ci/Build-A.ps1', 'owns': ['Modules/MNBL.A/', 'src/MNBL.A/']},
                       {'id': 'MNBL.Probe', 'release': False, 'reason': 'test probe', 'owns': ['Modules/MNBL.Probe/']}])
        self.write('Modules/MNBL.A/SubModule.xml', submodule('MNBL.A', 'v0.1.0'))
        self.write('src/MNBL.A/A.cs', 'class A {}')
        self.write('Modules/MNBL.Probe/SubModule.xml', submodule('MNBL.Probe', 'v0.1.0'))
        self.commit('base')

    def git(self, *args):
        return subprocess.run(['git', '-c', 'user.name=test', '-c', 'user.email=test@example.invalid', *args],
                              cwd=self.root, check=True, capture_output=True, text=True).stdout.strip()

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def registry(self, entries):
        self.write('config/mods.json', json.dumps({'schema': 'mnbl.mod-registry', 'version': 1, 'mods': entries}))

    def commit(self, message):
        self.git('add', '-A')
        self.git('commit', '-q', '-m', message)
        return self.git('rev-parse', 'HEAD')


class ModsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = ModRepo(self.temp.name)
        self.base = self.repo.git('rev-parse', 'HEAD')

    def tearDown(self):
        self.temp.cleanup()

    def test_registered_tree_passes(self):
        self.assertEqual(mods.registry_errors(self.repo.root), [])

    def test_unregistered_module_folder_fails(self):
        self.repo.write('Modules/MNBL.New/SubModule.xml', submodule('MNBL.New', 'v0.1.0'))
        errors = mods.registry_errors(self.repo.root)
        self.assertTrue(any('Modules/MNBL.New/' in error for error in errors), errors)

    def test_unowned_source_folder_fails(self):
        self.repo.write('src/Shared/Util.cs', 'class U {}')
        errors = mods.registry_errors(self.repo.root)
        self.assertTrue(any('src/Shared/' in error for error in errors), errors)

    def test_change_without_bump_fails_and_bump_passes(self):
        self.repo.write('src/MNBL.A/A.cs', 'class A { int x; }')
        self.repo.commit('change A')
        errors = mods.version_errors(self.repo.root, self.base)
        self.assertTrue(any('MNBL.A' in error and 'bump' in error for error in errors), errors)
        self.repo.write('Modules/MNBL.A/SubModule.xml', submodule('MNBL.A', 'v0.1.1'))
        self.repo.commit('bump A')
        self.assertEqual(mods.version_errors(self.repo.root, self.base), [])

    def test_non_release_mod_needs_no_bump_but_cannot_go_down(self):
        self.repo.write('Modules/MNBL.Probe/Extra.xml', '<x/>')
        self.repo.commit('probe change')
        self.assertEqual(mods.version_errors(self.repo.root, self.base), [])
        self.repo.write('Modules/MNBL.Probe/SubModule.xml', submodule('MNBL.Probe', 'v0.0.9'))
        self.repo.commit('probe downgrade')
        self.assertTrue(any('went down' in error for error in mods.version_errors(self.repo.root, self.base)))

    def test_plan_skips_tagged_versions_and_non_release_mods(self):
        self.assertEqual(mods.release_plan(self.repo.root), ['MNBL.A'])
        self.repo.git('tag', 'MNBL.A/v0.1.0')
        self.assertEqual(mods.release_plan(self.repo.root), [])
        self.repo.write('Modules/MNBL.A/SubModule.xml', submodule('MNBL.A', 'v0.2.0'))
        self.repo.commit('bump A')
        self.assertEqual(mods.release_plan(self.repo.root), ['MNBL.A'])

    def test_package_zips_module_folder_and_rejects_wrong_version(self):
        stage = self.repo.root / 'stage'
        (stage / 'bin/Win64_Shipping_Client').mkdir(parents=True)
        (stage / 'SubModule.xml').write_text(submodule('MNBL.A', 'v0.1.0'), encoding='utf-8')
        (stage / 'bin/Win64_Shipping_Client/MNBL.A.dll').write_bytes(b'dll')
        with redirect_stdout(io.StringIO()):
            result = mods.package(self.repo.root, 'MNBL.A', stage, self.repo.root / 'dist')
        self.assertEqual(result['tag'], 'MNBL.A/v0.1.0')
        with zipfile.ZipFile(result['zip']) as archive:
            self.assertEqual(sorted(archive.namelist()), ['MNBL.A/SubModule.xml', 'MNBL.A/bin/Win64_Shipping_Client/MNBL.A.dll'])
        self.assertIn('Harmony (Steam Workshop item 2859188632) v2.4.2', Path(result['notes']).read_text(encoding='utf-8'))
        (stage / 'SubModule.xml').write_text(submodule('MNBL.A', 'v9.9.9'), encoding='utf-8')
        with self.assertRaises(mods.CheckError):
            mods.package(self.repo.root, 'MNBL.A', stage, self.repo.root / 'dist')


if __name__ == '__main__':
    unittest.main()
