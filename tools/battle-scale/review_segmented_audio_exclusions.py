"""Root verification of bounded audio routes, never universal caller closure."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
path=ROOT/'evidence/engine-extension/native-segmented-audio-exclusions.json'
doc=json.loads(path.read_text());sources=doc['sources']
for s in sources:assert digest(s['path'])==s['sha256'],s['path']
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
assert digest(source)==doc['nativeSourceSha256']
pe=pefile.PE(str(source));base=pe.OPTIONAL_HEADER.ImageBase;rows=set()
def verify(v):
    if isinstance(v,dict):
        if 'address' in v and 'bytes' in v:
            a=int(v['address'],16);raw=bytes.fromhex(v['bytes'])
            assert pe.get_data(a-base,len(raw))==raw,hex(a);rows.add((a,raw))
        for x in v.values():verify(x)
    elif isinstance(v,list):
        for x in v:verify(x)
verify(doc)
assert len(doc['candidates'])==6
assert all(c['excludeFromMissionResolverOnReviewedRoutes'] and not c['universalIndirectCallerClosure'] and not c['hookApproved'] for c in doc['candidates'])
assert doc['registryValidatedSystemRoutes']['registry']['globalRva']=='0xcf0350'
assert pe.get_string_at_rva(0xa628f0)==b'FMOD IT Codec'
out={'schemaVersion':1,'status':'accepted-bounded-audio-allocation-associations',
     'sourceBinarySha256':doc['nativeSourceSha256'],'reviewScriptSha256':digest(__file__),
     'input':{'path':str(path.relative_to(ROOT)),'sha256':digest(path)},'sourceCount':len(sources),'uniqueByteRows':len(rows),
     'review':['System initialization and play entry points obtain the same exact registry member from CF0350+160[8]; the two lookup helpers validate membership/index bounds.',
               'Nonvolatile receiver carriers remain unchanged through the retained method fragments until ordinary epilogue restoration. This applies to the reviewed normal entries.',
               'IT codec descriptor binds explicit init, close and read callbacks; those routes retain the same0x46d8-byte instance receiver and its independently allocated+5e0 records.',
               'The matching230 stride does not make either independently allocated audio family a Mission companion.',
               'Unclassified trailing data and raw call byte candidates remain outside proven control-flow closure.'],
     'combinedNumericCandidateClassification':{'total':73,'companionContributions':52,'historyUses':10,'audioUsesOnReviewedRoutes':11},
     'limits':['All numeric candidates now have bounded associations; arbitrary indirect callers, alternate codec routes and whole-program consumer closure remain unproved.',
               'No pool rewriting is authorized at the audio sites by this review. No game launched or hook installed.'],
     'enginePatchApproved':False,'gameLaunched':False}
dst=ROOT/'evidence/engine-extension/native-segmented-audio-independent-review.json'
dst.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'rows':len(rows),'sha256':digest(dst)}))
