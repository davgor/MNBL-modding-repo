"""Compile and execute copied original default elements in isolated processes."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,os,subprocess
ROOT=Path(__file__).resolve().parents[2]
INPUTS=['src/MNBL.AgentStorage/AgentDomainStorage.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
        'src/MNBL.AgentStorage/NativeElementFactory.hpp','src/MNBL.AgentGrowthProbe/UnpublishedElementProbe.hpp',
        'tools/battle-scale/prepare_native_default_element_harness.py','tests/AgentStorage/native_default_element_harness.cpp',
        'tools/battle-scale/Test-NativeDefaultElements.cmd','tools/battle-scale/verify_native_default_elements.py',
        'evidence/engine-extension/native-unpublished-element-runtime-contract.json',
        'evidence/engine-extension/native-unpublished-element-contract-independent-review.json']
PRESERVED=['tools/battle-scale/prepare_native_element_harness.py','tests/AgentStorage/native_element_factory_tests.cpp',
           'tools/battle-scale/Test-NativeElementFactory.cmd','tools/battle-scale/prepare_segmented_scalar_harness.py',
           'tests/AgentStorage/native_scalar_harness.cpp','evidence/engine-extension/native-scalar-storage-integration.json']
OUTPUTS=['build/agent-storage/native_default_image.hpp','build/agent-storage/native_default_element_harness.exe',
         'build/agent-storage/native_default_element_harness_asan.exe','evidence/engine-extension/native-default-element-image.json']
def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def main():
    before={p:digest(ROOT/p) for p in INPUTS};preserved={p:digest(ROOT/p) for p in PRESERVED}
    provider=Path(os.environ['SystemRoot'])/'System32/ucrtbase.dll';provider_before=digest(provider)
    result=subprocess.run(['cmd','/d','/c',str(ROOT/'tools/battle-scale/Test-NativeDefaultElements.cmd')],cwd=ROOT,text=True,capture_output=True,timeout=180)
    assert result.returncode==0,result.stdout+result.stderr
    assert result.stdout.count('core30->60->90:487 completed callbacks;600 stable-pointer checks;450 inactive headers;')==4
    assert result.stdout.count('private2040->4080->6120:30600 native-construction callbacks;30600 stable-pointer checks;61200 inactive headers;')==2
    assert result.stdout.count('PASS40 poisoned real native element lifetimes;18 original function-entry unwinds;')==2
    actual_paths=[Path(line[len('CRT_PATH='):]) for line in result.stdout.splitlines() if line.startswith('CRT_PATH=')]
    assert len(actual_paths)==4 and all(p.resolve()==provider.resolve() for p in actual_paths)
    # Deliberate unsupported-entry negative is a separate process. Its failure
    # is not treated as successful element execution or native EH containment.
    negative=subprocess.run([str(ROOT/OUTPUTS[1]),'--unsupported-handler-must-fail'],cwd=ROOT,text=True,capture_output=True,timeout=30)
    assert negative.returncode&0xffffffff==0x80000003,(negative.returncode,negative.stdout,negative.stderr)
    assert 'EXPECTED_FAILURE:' in negative.stdout and 'PASS' not in negative.stdout
    assert before=={p:digest(ROOT/p) for p in INPUTS},'Input changed during execution'
    assert preserved=={p:digest(ROOT/p) for p in PRESERVED},'Accepted prototype changed'
    assert provider_before==digest(provider),'Host CRT changed during execution'
    image=json.loads((ROOT/OUTPUTS[-1]).read_text());assert image['generatedHeaderSha256']==digest(ROOT/OUTPUTS[0])
    for path,sha in image['sources'].items():assert digest(path)==sha,path
    contract=json.loads((ROOT/INPUTS[-2]).read_text());review=json.loads((ROOT/INPUTS[-1]).read_text())
    assert review['status']=='accepted-bounded-default-unpublished-lifetime-contract'
    for source in (contract,review):
        for path,sha in source['sources'].items():assert digest(path)==sha,path
    out={'schemaVersion':1,'status':'passed-real-copied-default-native-element-lifetimes-not-live-integration',
         'createdAtUtc':datetime.now(timezone.utc).isoformat(),'sources':before,'preservedAcceptedInputs':preserved,
         'outputs':{p:digest(ROOT/p) for p in OUTPUTS},'mathProvider':{'path':str(provider.resolve()),'sha256':provider_before,'symbol':'atan2f','verifiedSignedZeroBits':'0x80000000','addressOwnerVerifiedByVirtualQuery':True},
         'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr,
         'unsupportedPathNegative':{'expectedStatus':'0x80000003','actualExitCode':negative.returncode,'stdout':negative.stdout,'stderr':negative.stderr,'meaning':'Intentional unsupported EH entry traps and terminates only this separate negative test process; no exception is swallowed or counted as a completed native object.'},
         'checks':{'builds':['MSVC optimized','MSVC AddressSanitizer'],'committedPages':29,'originalCodeBodies':14,'registeredOriginalRuntimeFunctionsPerImage':9,
                   'perBuild':{'poisonedNativeObjectLifetimes':40,'smallCoreCycles':2,'smallCompletedCallbacksPerCycle':487,'smallPointerChecksPerCycle':600,'smallHeaderChecksPerCycle':450,
                               'largeCapacities':[2040,4080,6120],'largeCompletedCallbacks':30600,'largeStablePointerChecks':30600,'largeHeaderChecks':61200,'functionEntryUnwindChecks':18},
                   'allFivePoolsUseOriginalConstructors':True,'agentPoolsUseOriginalNonDeletingDestructor':True,'substitutedEngineOperations':0,'modeledMath':False,
                   'privateRollback':'Real small core injects allocation failure and a failure before constructor37; previously completed native objects are destroyed and old private capacity remains30. Both ledgers exhaust/reuse without native activation.',
                   'mapping':'Only approved code/data/unwind spans are copied. Copied data pages are read-only; code pages executable/read-only. Unproven executable-page gaps contain INT3; other pages remain uncommitted. Activation5cd130 is verified uncommitted.'},
         'limitations':['No game process, Mission object, live pool adoption/publication, native access redirection, worker barrier or real soldier activation.',
                        'Original unwind headers/codes/handler fields remain unchanged. Unreviewed C++/SEH cleanup handlers and funclets are not supplied; unexpected exceptions inside copied native bodies terminate/fail the test.',
                        'Only allocator or pre-constructor host failures are injected. Mid-constructor native exception containment and full active-object cleanup are not certified.',
                        'Original CFG dispatcher JMP RAX is used in the copied test image. This is not validation of a live module loader CFG policy or an installed hook target.',
                        'AddressSanitizer instruments host storage/allocation/test code; copied original machine instructions are not ASAN-instrumented. Poison/canary checks, page protections, and prior instruction audit provide separate bounds evidence.',
                        'Image vtable addresses remain coherent reserved RVAs but their unreviewed methods are not mapped or invoked. Objects must remain unactivated and unpublished.'],
         'enginePatchApproved':False,'gameLaunched':False,'installedNativeRead':False}
    p=ROOT/'evidence/engine-extension/native-default-element-execution.json';p.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'status':out['status'],'sha256':digest(p)}))
if __name__=='__main__':main()
