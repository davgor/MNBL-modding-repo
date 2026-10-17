"""Review frozen copied-native element execution without rebuilding its inputs."""
from pathlib import Path
import hashlib, json, os, struct, subprocess, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def main():
    receipt=ROOT/'evidence/engine-extension/native-default-element-execution.json'
    assert digest(receipt)=='5f5f8fdc84577e2b4526a5d1874f56a5596faedcc9af682d91a7d43f8554fad9'
    report=json.loads(receipt.read_text());pinned={}
    for key in ('sources','preservedAcceptedInputs','outputs'):
        for p,sha in report[key].items():
            assert p not in pinned or pinned[p]==sha
            pinned[p]=sha;assert digest(ROOT/p)==sha,p
    image=json.loads((ROOT/'evidence/engine-extension/native-default-element-image.json').read_text())
    for p,sha in image['sources'].items():assert digest(p)==sha,p
    original=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    pe=pefile.PE(str(original));mapped=pe.get_memory_mapped_image()
    pages={};regions=image['regions'];byte_count=0
    for region in regions:
        raw=bytes.fromhex(region['bytes']);rva=region['rva']
        assert mapped[rva:rva+len(raw)]==raw,region['name']
        byte_count+=len(raw)
        for page in range(rva&~4095,(rva+len(raw)+4095)&~4095,4096):
            assert page not in pages or pages[page]==region['executable']
            pages[page]=region['executable']
    assert len(pages)==29
    assert sorted(pages.items())==[(row['rva'],row['executable']) for row in image['pagePermissions']]
    assert (0x5cd130&~4095) not in pages
    for handler in (0x9e3177,0x9e318f):
        assert pages[handler&~4095]
        assert not any(row['rva']<=handler<row['rva']+len(bytes.fromhex(row['bytes'])) for row in regions)
    for row in image['runtimeFunctions']:
        e=next(e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION if e.struct.BeginAddress==row['begin'])
        assert (e.EndAddress,e.UnwindData)==(row['end'],row['unwind'])
        raw=bytes.fromhex(row['originalUnwindBytes']);assert pe.get_data(e.UnwindData,len(raw))==raw
        flags=raw[0]>>3;assert flags&4==0
        offset=4+((raw[2]+1)&~1)*2
        if flags&3:assert struct.unpack_from('<I',raw,offset)[0]==row['handlerRva']
        else:assert row['handlerRva'] is None
    runtime=Path('C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64')
    env=dict(os.environ);env['PATH']=str(runtime)+os.pathsep+env.get('PATH','')
    provider=report['mathProvider'];assert digest(provider['path'])==provider['sha256']
    runs=[]
    for name in ('native_default_element_harness.exe','native_default_element_harness_asan.exe'):
        executable=ROOT/'build/agent-storage'/name
        run=subprocess.run([str(executable)],cwd=ROOT,env=env,capture_output=True,text=True,timeout=60)
        assert run.returncode==0,(run.stdout,run.stderr)
        assert run.stdout.count('core30->60->90:487 completed callbacks;600 stable-pointer checks;450 inactive headers;')==2
        assert run.stdout.count('private2040->4080->6120:30600 native-construction callbacks;30600 stable-pointer checks;61200 inactive headers;')==1
        assert run.stdout.count('PASS40 poisoned real native element lifetimes;18 original function-entry unwinds;')==1
        runs.append({'path':str(executable),'sha256':digest(executable),'exitCode':run.returncode,'stdout':run.stdout,'stderr':run.stderr})
    negative=subprocess.run([str(ROOT/'build/agent-storage/native_default_element_harness.exe'),'--unsupported-handler-must-fail'],cwd=ROOT,env=env,capture_output=True,text=True,timeout=30)
    assert negative.returncode&0xffffffff==0x80000003 and 'PASS' not in negative.stdout and 'EXPECTED_FAILURE:' in negative.stdout
    for p,sha in pinned.items():assert digest(ROOT/p)==sha,p
    assert digest(provider['path'])==provider['sha256']
    result={'schemaVersion':1,'status':'accepted-copied-default-native-element-execution-only',
            'receipt':{'path':str(receipt),'sha256':digest(receipt)},'reviewerSha256':digest(__file__),
            'reviewedSourceAndOutputSha256':pinned,'validatedOriginalRegionBytes':byte_count,
            'validatedPages':len(pages),'validatedRuntimeFunctions':len(image['runtimeFunctions']),
            'independentFrozenRuns':runs,'unsupportedPathNegative':{'exitCode':negative.returncode,'stdout':negative.stdout},
            'manualReview':['All five pool callbacks use original per-destination constructors; both Agent pools use original nondeleting destructor. Only atan2f is bound to actual System32 CRT; no fake Agent body remains.',
                            'Image reserve is relocatable; copied instructions use relative references. Runtime CFG slot is rebound to the copied original dispatcher. Default objects never invoke uncommitted virtual methods.',
                            'Construction failure cleanup releases dynamic unwind table before memory and CRT owner. Normal test scope destroys storage before callback contexts and copied runtime owner.',
                            'Read-only data and executable-read code pages are applied after copy and fixups. Unapproved code gaps are INT3; activation page remains reserved. Original unwind handler flags are retained.',
                            'Small cycles inject host allocation and pre-constructor failure only. Large cycles verify stable payload addresses and all inactive headers across2040/4080/6120; all30600 successful callbacks are matched by destruction.',
                            'Exception-handler negative deliberately executes a trap-filled handler in a separate process. It establishes fail-closed test behavior, not complete C++ cleanup for partially constructed native objects.'],
            'openFindings':[],
            'limitations':['Native copied instructions are not ASAN-instrumented. Host ASAN plus page/canary checks do not certify arbitrary active-object native accesses.',
                           'Entry-only unwind tests do not prove every instruction boundary or exceptional cleanup. Unexpected native exceptions fail the process.',
                           'No engine initialization, Mission ownership, active soldiers, live adoption, wider IDs, worker barrier, access redirection or installed patch tested.',
                           'Frozen executables rerun without rebuilding; producer receipt and source identities unchanged.'],
            'gameLaunched':False,'enginePatchApproved':False}
    path=ROOT/'evidence/engine-extension/native-default-element-independent-review.json'
    assert not path.exists()
    path.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'status':result['status'],'sha256':digest(path),'nativeRuns':len(runs)}))

if __name__=='__main__':main()
