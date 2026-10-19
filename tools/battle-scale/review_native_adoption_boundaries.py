"""Independent frozen replay review of phase mismatch and empty-attach candidates."""
from pathlib import Path
import hashlib,importlib.util,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import pefile,capstone

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def main():
    inputs=[('native-companion-occupancy-contract.json','a44b7c371cd4a5a87f66760bce3c85f364b456611dbc00ca9e20af0f13e8bc37','audit_native_companion_occupancy.py'),
            ('native-empty-adoption-boundary.json','08f9cd63da22cc2a079048010fe4cf3507c2e362a2dbedd5425f32e730a67393','audit_native_empty_adoption_boundary.py')]
    source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    assert digest(source)=='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
    pe=pefile.PE(str(source));md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);rows=set();pins={};reports=[]
    def check(value):
        if isinstance(value,dict):
            if 'address' in value and 'bytes' in value:
                a=int(value['address'],16);raw=bytes.fromhex(value['bytes'])
                assert pe.get_data(a-0x180000000,len(raw))==raw
                decoded=list(md.disasm(raw,a));assert len(decoded)==1 and decoded[0].size==len(raw)
                rows.add((a,raw))
            for item in value.values():check(item)
        elif isinstance(value,list):
            for item in value:check(item)
    for name,sha,script in inputs:
        path=ROOT/'evidence/engine-extension'/name;assert digest(path)==sha
        report=json.loads(path.read_text());reports.append(report);pins[str(path)]=sha
        dependencies=report.get('sources') or {d['path']:d['sha256'] for d in report['dependencies']}
        for p,h in dependencies.items():assert digest(p)==h; pins[p]=h
        check(report)
        spec=importlib.util.spec_from_file_location(script[:-3],ROOT/'tools/battle-scale'/script)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        assert json.loads(json.dumps(module.build()))==report,'Frozen replay differs: '+name
    phase,empty=reports
    assert not phase['oldSnapshotValidatorApproved'] and not phase['replacementSnapshotValidatorApproved']
    assert [r['highwater'] for r in phase['actualRemovalHighwaterDependencyReplay']]==[0,4]
    assert all(r['tableOccupied']==[3] and r['count']==1 and not r['supportedEnginePhaseClaimed'] for r in phase['actualRemovalHighwaterDependencyReplay'])
    assert len(phase['actualRegistrationAndResetReplay'])==5
    assert all(r['allRegisteredPayloadHeadersAfterReset']==-1 for r in phase['actualRegistrationAndResetReplay'])
    assert len(empty['nativeReplay'])==12 and len(empty['emptyGateNegativeCases'])==40
    assert empty['initialMissionFixturePoison']=='0xa5' and not empty['liveAdoptionApproved']
    assert {c['rva'] for c in empty['candidates']}=={'0x5e5b80','0x5cd168'}
    # Independently inspect proposed first publication and earlier explicit
    # allocation write, which rules out using the later helper call as a gate.
    assert pe.get_data(0x5e5b7b,8).hex()=='e87088feff488906'
    assert pe.get_data(0x5cd185,4).hex()=='44896910'
    for p,h in pins.items():assert digest(p)==h,p
    out={'schemaVersion':1,'status':'accepted-phase-mismatch-and-empty-boundary-evidence-no-live-adoption-approval',
         'sources':pins,'reviewerSha256':digest(__file__),'originalInstructionRows':len(rows),
         'readOnlyProducerReplays':2,'nativeHelperCases':4,'nativeRegistrationResetCases':5,'nativeRemovalContrasts':2,
         'nativeEmptyBoundaryCases':12,'emptyGateRejections':40,
         'findings':['Current header-based occupied230 snapshot rule cannot be used at arbitrary frozen points; table membership survives initializer resetting payload header.',
                     'Table-only replacement is insufficient because original removal still scans payload headers to compute highwater.',
                     'An empty-only attach contract avoids importing phase-sensitive occupied state, but still requires completed construction, lifetime ownership and exclusion of native writers.',
                     'Constructor-return candidate precedes this wrapper publication; no transitive no-escape proof or hook approval is established.',
                     'CreateAgent common prefix is before both mutations; explicit branch writes header before its helper, so hooking only5cd195 would be late.',
                     'Do not synthesize CreateAgent failure result without fixing registered caller, which derives Agent pointer unconditionally.'],
         'openFindings':['Whole constructor/reset no-escape and mutation barrier unresolved.','No live adoption/lifecycle token or routed wide metadata yet.'],
         'limitations':['Replay uses synthetic memory and exact retained slices; full native Mission construction/activation/removal does not execute.',
                        'Negative tests validate necessary metadata checks, not absence of readers/jobs or native object lifetime.',
                        'No frozen source/evidence modified and no game launched.'],
         'liveAdoptionApproved':False,'enginePatchApproved':False,'gameLaunched':False}
    destination=ROOT/'evidence/engine-extension/native-adoption-boundary-independent-review.json'
    assert not destination.exists()
    destination.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'status':out['status'],'sha256':digest(destination),'rows':len(rows)}))

if __name__=='__main__':main()
