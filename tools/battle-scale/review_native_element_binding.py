"""Read-only independent identity reconstruction and frozen executable replay."""
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import pefile

EXPECTED_RECEIPT = 'dc64059e40ea2fc465f56ef20085fc239440b7e30ba2b4b7083f686f56c1f027'
ORIGINAL_SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build():
    retained = ROOT / 'evidence/engine-extension/native-element-binding-tests.json'
    assert digest(retained) == EXPECTED_RECEIPT
    integration = json.loads(retained.read_text())
    assert integration['status'] == 'passed-offline-native-binding-identity-tests'
    bound = {**integration['sources'], **integration['outputs']}
    for path, sha in bound.items():
        assert digest(ROOT / path) == sha, path
    source = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    assert digest(source) == ORIGINAL_SHA
    raw = source.read_bytes()
    pe = pefile.PE(data=raw)
    assert pe.FILE_HEADER.Machine == 0x8664
    identity = json.loads((ROOT / 'evidence/engine-extension/native-element-binding-identity.json').read_text())
    assert identity['binarySha256'] == ORIGINAL_SHA and identity['fileSize'] == len(raw)
    assert identity['imageSize'] == pe.OPTIONAL_HEADER.SizeOfImage
    assert identity['generatorSha256'] == bound['tools/battle-scale/prepare_native_element_binding.py']
    assert identity['generatedSha256'] == bound['build/agent-storage/native_element_identity.hpp']
    sections = {s.Name.rstrip(b'\0').decode(): s for s in pe.sections}
    executable = [s for s in pe.sections if s.Characteristics & 0x20000000]
    assert len(executable) == 1 and executable[0].Name.rstrip(b'\0') == b'.text'
    specs = [('headers', 0, pe.OPTIONAL_HEADER.SizeOfHeaders, False),
             ('text', sections['.text'].VirtualAddress, sections['.text'].Misc_VirtualSize, True),
             ('unwind', sections['.pdata'].VirtualAddress, sections['.pdata'].Misc_VirtualSize, False)]
    # Rebuild loader layout independently of the production generator. The
    # original file hash is established before trusting any PE length/offset.
    image = bytearray(pe.OPTIONAL_HEADER.SizeOfImage)
    image[:pe.OPTIONAL_HEADER.SizeOfHeaders] = raw[:pe.OPTIONAL_HEADER.SizeOfHeaders]
    for s in pe.sections:
        first, size = s.VirtualAddress, s.SizeOfRawData
        assert first + size <= len(image)
        image[first:first + size] = raw[s.PointerToRawData:s.PointerToRawData + size]
    actual_rows = []
    for name, first, size, needs_execute in specs:
        assert size and first + size <= len(image)
        section_hash = hashlib.sha256(image[first:first + size]).hexdigest()
        actual_rows.append({'name': name, 'rva': hex(first), 'size': size,
                            'sha256': section_hash, 'requiresExecute': needs_execute})
    assert actual_rows == identity['regions']
    generated = (ROOT / 'build/agent-storage/native_element_identity.hpp').read_text()
    rows = re.findall(r'\{0x([0-9a-f]+),0x([0-9a-f]+),(true|false),\{([^}]+)\}\},', generated)
    assert len(rows) == 3
    for generated_row, expected in zip(rows, actual_rows):
        first, size, flag, hash_text = generated_row
        encoded_hash = bytes(int(x, 16) for x in hash_text.split(',')).hex()
        assert (int(first, 16), int(size, 16), flag == 'true', encoded_hash) == (
            int(expected['rva'], 16), expected['size'], expected['requiresExecute'], expected['sha256'])
    file_hash = re.search(r'file_hash\{([^}]+)\}', generated).group(1)
    assert bytes(int(x, 16) for x in file_hash.split(',')).hex() == ORIGINAL_SHA
    assert f'file_size={len(raw)};' in generated
    assert f'image_size=0x{len(image):x};' in generated
    relocations = [entry.rva for block in pe.DIRECTORY_ENTRY_BASERELOC for entry in block.entries if entry.type]
    for name, first, size, _ in specs:
        assert not [rva for rva in relocations if first <= rva < first + size], name
    text_first, text_size = specs[1][1:3]
    bound_entries = [0x606a20, 0x6070c0, 0x5e58e0, 0x5d8860]
    assert all(text_first <= rva < text_first + text_size for rva in bound_entries)
    # Explicitly demonstrate that the loaded function table is not the full
    # exception/unwind metadata. This is a limitation, not an approved mutation.
    metadata_rows = []
    for rva in bound_entries:
        entries = [e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress == rva]
        for e in entries:
            metadata_rows.append({'functionRva': hex(rva), 'unwindRva': hex(e.UnwindData),
                                  'coveredByLoadedIdentityRegions': any(first <= e.UnwindData < first + size
                                                                       for _, first, size, _ in specs)})
    assert metadata_rows and any(not row['coveredByLoadedIdentityRegions'] for row in metadata_rows)
    runtime = Path('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64')
    asan = runtime / 'clang_rt.asan_dynamic-x86_64.dll'
    assert asan.is_file()
    environment = dict(os.environ)
    environment['PATH'] = str(runtime) + os.pathsep + environment.get('PATH', '')
    runs = []
    for name in ('native_element_binding_tests.exe', 'native_element_binding_asan.exe'):
        executable = ROOT / 'build/agent-storage' / name
        run = subprocess.run([str(executable)], cwd=ROOT, env=environment,
                             capture_output=True, text=True, timeout=60)
        assert run.returncode == 0 and run.stdout.count('PASS native binding:') == 1, (run.stdout, run.stderr)
        for expected in ('55 code/header/unwind mutations', '4 wrong-module rejections',
                         'nonexecutable MEM_IMAGE rejected', 'executable MEM_IMAGE accepted'):
            assert expected in run.stdout
        runs.append({'path': str(executable), 'sha256': digest(executable), 'exitCode': run.returncode,
                     'stdout': run.stdout, 'stderr': run.stderr})
    for path, sha in bound.items():
        assert digest(ROOT / path) == sha, path
    assert digest(retained) == EXPECTED_RECEIPT and digest(source) == ORIGINAL_SHA
    return {
        'schemaVersion': 1, 'status': 'accepted-offline-native-binding-not-live-engine-integration',
        'reviewedIntegration': {'path': str(retained), 'sha256': EXPECTED_RECEIPT},
        'reviewedSourceSha256': integration['sources'], 'verifiedOutputSha256': integration['outputs'],
        'reviewerSourceSha256': digest(__file__), 'originalBinarySha256': ORIGINAL_SHA,
        'independentIdentityReconstruction': {'regions': actual_rows, 'fileSize': len(raw),
            'imageSize': len(image), 'executableRegionCount': 1, 'selectedRegionRelocations': 0,
            'allFourBoundEntriesWithinPinnedExecutableRegion': True,
            'unwindMetadataCoverageExamples': metadata_rows},
        'independentExecutableRuns': runs,
        'asanRuntime': {'path': str(asan), 'sha256': digest(asan)},
        'resolvedFindings': [{
            'id': 'loaded-code-execute-permission', 'severity': 'blocking-before-binding-use',
            'originalHeaderSha256': '0c111521027551caaa5d18afc26e5330680f52a2398e5fcc1c1b2e6ebb19d673',
            'finding': 'Byte-identical MEM_IMAGE text with execute permission removed was accepted by the original readable-only gate.',
            'resolution': 'Generated text region requires execute permission. Every VirtualQuery region covering that text is checked for readable+executable protection; binder forwards the flag.',
            'regression': 'Both independently rerun executables reject their own nonexecutable MEM_IMAGE header as code and accept their own executable function page.'}],
        'manualReview': [
            'GetModuleHandleExW FROM_ADDRESS increments a reference to an already-loaded module. Exact base equality is checked; interior-address rejection releases the acquired reference. There is no LoadLibrary or target constructor invocation in binding.',
            'RAII owner exists before path/file/hash checks. Every subsequent rejection destroys owner; File closes its handle on read/hash exceptions. Successful Bindings retain owner through shared_ptr and NativeElementFactory holds that owner.',
            'GetModuleFileNameW truncation rejects. CreateFile excludes simultaneous writers/deletion during exact size/full-file SHA verification. File identity is distinct from the subsequently checked loaded bytes.',
            'VirtualQuery verifies each region is committed MEM_IMAGE belonging to the exact allocation base, with no guard/noaccess. Each code region additionally requires execute+read. Integer overflow and image-relative range checks precede pointer arithmetic.',
            'Selected regions independently match full PE headers, all text VirtualSize bytes, and pdata function-table bytes. All four function RVAs are inside the required executable region. No selected-region relocations occur in the exact original.',
            'A reference count retains the module mapping, not engine initialization or the callable-object lifetime. The factory and its runtime owner must outlive storage/leases; callbacks still hold raw factory context.',
            'Executable/source pins bind the retained optimized/ASAN build receipt; replay executes only inert identity tests against the offline original and unrelated modules in its own process.'],
        'apiReferences': [
            {'url': 'https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw',
             'supports': 'FROM_ADDRESS lookup and incremented reference released with FreeLibrary; no new module loading.'},
            {'url': 'https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualquery',
             'supports': 'Region state/type/allocation/protection query; image copy-on-write remains MEM_IMAGE, so byte identity remains necessary.'},
            {'url': 'https://learn.microsoft.com/en-us/windows/win32/memory/memory-protection-constants',
             'supports': 'Readable protection alone is not execute permission.'}],
        'openFindings': [],
        'limitations': [
            'No positive binding to a running Bannerlord module or actual Agent constructor/destructor execution. This is not game integration approval.',
            'Pdata is the runtime function table, not all unwind or C++ exception metadata. Loaded rdata, IAT, mutable globals and constructor dependencies are not fully identity-checked.',
            'Caller must provide initialized engine state and exclude concurrent module/code/protection mutation. Query+hash cannot eliminate a race with unauthorized writes or VirtualProtect.',
            'Module reference lifetime does not synchronize jobs, enforce factory/storage lifetime, activate/remove Agents, route native accesses or widen indices.',
            'Frozen optimized/ASAN outputs were independently rerun, not rebuilt; identity reconstruction did not execute the production generator or overwrite its output.',
            'No game launch, game process inspection/control, installed-file writes or patch installation.'],
        'gameLaunched': False, 'enginePatchApproved': False}


if __name__ == '__main__':
    result = build()
    path = ROOT / 'evidence/engine-extension/native-element-binding-independent-review.json'
    path.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'path': str(path), 'sha256': digest(path), 'status': result['status'],
                      'runs': len(result['independentExecutableRuns']), 'openFindings': len(result['openFindings'])}))
