"""Root byte/hash verification after independent bounded source review."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
assert digest(SOURCE)==SHA
pe=pefile.PE(str(SOURCE));base=pe.OPTIONAL_HEADER.ImageBase
names=['native-segmented-companion-accesses.json','native-segmented-companion-extension.json']
checks=[];unique=set()
def verify(value):
    if isinstance(value,dict):
        if 'address' in value and 'bytes' in value:
            address=int(value['address'],16);raw=bytes.fromhex(value['bytes'])
            assert pe.get_data(address-base,len(raw))==raw,(hex(address),value['bytes'])
            unique.add((address,raw))
        for v in value.values():verify(v)
    elif isinstance(value,list):
        for v in value:verify(v)
for name in names:
    path=ROOT/'evidence/engine-extension'/name;doc=json.loads(path.read_text())
    for s in doc['sources']:assert digest(s['path'])==s['sha256'],s['path']
    verify(doc)
    checks.append({'path':str(path.relative_to(ROOT)),'sha256':digest(path),'verifiedSources':len(doc['sources'])})
out={'schemaVersion':1,'status':'accepted-bounded-companion-contributions-not-hook-approval',
     'sourceBinarySha256':SHA,'reviewScriptSha256':digest(__file__),'inputs':checks,
     'uniqueByteRows':len(unique),
     'review':['Stride creates only an offset fact; Mission must be independently established before naming a companion/history address',
               'Exact adjacent SIMD copies have sole CFG predecessor and unchanged stack address; only already-proven source qwords survive',
               'Higher stack arguments are seeded from exact outgoing call states, with callerRSP+20 mapped to calleeEntryRSP+28; not global ABI claims',
               'TEST R10,R10 immediately precedes CMOVE R10,RDI; only known zero or known live Mission-derived nonzero B base gets a selected fact',
               'Create path uses R13D primary index throughout230/A/B; Agent+20 caches A same-index address',
               'Delete preserves input index in nonvolatile registers and supplies same index to companion removal; this is bounded lifecycle coupling',
               'FMOD byte counts and separately allocated records remain distinct; six unresolved receiver associations are not claimed excluded'],
     'result':{'totalCompanionStrideCandidates':73,'provenCompanionContributions':41,
               'distinctHistoryUses':10,'distinctFmodAllocationOrRecordUses':5,'unresolved':17},
     'limits':['Owner contribution does not prove every result use is an address, complete incoming-call closure or safe hook substitution',
               'Companion metadata is physically separate; adoption must validate all four occupancy sets agree',
               'Secondary Agent domain remains independently allocated; no secondary companion mapping inferred',
               'No runtime engine execution, patch deployment, complete consumer closure or worker quiescence demonstrated'],
     'enginePatchApproved':False,'gameLaunched':False}
path=ROOT/'evidence/engine-extension/native-companion-growth-independent-review.json'
path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'uniqueByteRows':len(unique),'sha256':digest(path)}))
