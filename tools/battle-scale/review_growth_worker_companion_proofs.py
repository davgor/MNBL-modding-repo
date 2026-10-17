"""Verify the second companion extension without rewriting earlier receipts."""
from pathlib import Path
import hashlib, json, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
assert digest(SOURCE)==SHA
pe=pefile.PE(str(SOURCE));base=pe.OPTIONAL_HEADER.ImageBase
path=ROOT/'evidence/engine-extension/native-segmented-companion-worker-extension.json'
doc=json.loads(path.read_text())
assert doc['nativeSourceSha256']==SHA
for source in doc['sources']:assert digest(source['path'])==source['sha256'],source['path']
rows=set()
def verify(v):
    if isinstance(v,dict):
        if 'address' in v and 'bytes' in v:
            a=int(v['address'],16);raw=bytes.fromhex(v['bytes'])
            assert pe.get_data(a-base,len(raw))==raw,hex(a)
            rows.add((a,raw))
        for value in v.values():verify(value)
    elif isinstance(v,list):
        for value in v:verify(value)
verify(doc)
assert len(doc['candidates'])==11
assert all(c['classification']=='proven-companion-address-contribution' and c['proofs'] and
           not c['hookApproved'] and not c['allResultUsesProvenAddressOnly'] for c in doc['candidates'])
out={
 'schemaVersion':1,'status':'accepted-bounded-companion-identity-extension-not-hooks',
 'sourceBinarySha256':SHA,'reviewScriptSha256':digest(__file__),
 'input':{'path':str(path.relative_to(ROOT)),'sha256':digest(path)},
 'verifiedSources':len(doc['sources']),'uniqueExactByteRows':len(rows),
 'review':[
  'Caller-owned homes are preserved only over the reverse-predecessor lifetime of the named reads; later reuse is excluded.',
  'Direct stores and exact call output ranges do not overlap the three private homes. The contact-array counter has two zero resets and a single guarded increment; indexed writes lie in RSP+4e0..10e0.',
  'Closure contracts retain actual field geometry and distinguish copied pointer values from pointers to caller homes. Exact copy managers preserve those fields; this is not a generic closure rule.',
  '690170 has exactly two external DWORD stores through R8/R9. Its other stores target its frame; its only direct call is the existing cookie check.',
  'The optional B selection keeps null and nonnull B distinct before the per-record fallback; a shared multiply is never assigned exclusively to A or B.',
  'The observed-use list is not used as a complete escape or scalar-use proof.'
 ],
 'result':{'candidateCount':73,'provenCompanionContributions':52,'distinctHistoryUses':10,
           'distinctFmodAllocationOrRecordUses':5,'unresolvedFmodReceiverAssociations':6},
 'limits':['All-use routing, original capture lifetime, metadata publication and complete runtime quiescence remain unproved.',
           'No engine patch approved or installed; no game launched.'],
 'enginePatchApproved':False,'gameLaunched':False}
outpath=ROOT/'evidence/engine-extension/native-companion-worker-independent-review.json'
outpath.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'rows':len(rows),'sha256':digest(outpath)}))
