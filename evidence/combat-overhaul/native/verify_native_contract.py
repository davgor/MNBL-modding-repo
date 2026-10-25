"""Verify retained collision-job instruction bytes against installed PE, without execution."""
import hashlib,json,struct
from pathlib import Path

root=Path(__file__).resolve().parents[3]
target=Path(r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\TaleWorlds.Native.dll')
prior=root/'evidence/engine-extension/native-job-mission-spill-review.json'
d=json.loads(prior.read_text(encoding='utf-8-sig')); b=target.read_bytes()
pe=struct.unpack_from('<I',b,0x3c)[0]
n,optsize=struct.unpack_from('<H',b,pe+6)[0],struct.unpack_from('<H',b,pe+20)[0]
opt=pe+24; imagebase=struct.unpack_from('<Q',b,opt+24)[0]
sections=[]
for i in range(n):
 s=opt+optsize+i*40
 vs,va,rs,rp=struct.unpack_from('<IIII',b,s+8)
 sections.append((va,rs,rp))
def readva(addr,size):
 rva=int(addr,16)-imagebase
 for va,rs,rp in sections:
  if va<=rva and rva+size<=va+rs:return b[rp+rva-va:rp+rva-va+size]
 raise ValueError(addr)
checked=[]
for x in d['functionInstructions']:
 expected=bytes.fromhex(x['bytes']);actual=readva(x['address'],len(expected))
 checked.append(dict(x,match=actual==expected))
focused=[x for x in checked if 0x180685f20<=int(x['address'],16)<=0x180685fb3 or 0x1806862f0<=int(x['address'],16)<=0x18068639c]
out={'scope':'Static byte verification of retained Ghidra collision-job instructions; no runtime execution or all-path proof',
 'target':str(target),'targetSha256':hashlib.sha256(b).hexdigest(),'priorPath':str(prior.relative_to(root)),
 'priorSha256':hashlib.sha256(prior.read_bytes()).hexdigest(),'expectedTargetSha256':d['targetSha256'],
 'targetMatches':hashlib.sha256(b).hexdigest()==d['targetSha256'],'instructionCount':len(checked),
 'allInstructionsMatch':all(x['match'] for x in checked),'focusedInstructions':focused,
 'callbackBindingProof':d['callbackBindingProof'],'callEffects':[x for x in d['callEffects'] if x['callee']=='5d4e20'],
 'retainedEvidence':[x for x in d['retainedCalleeEvidence'] if x['procedure'] in ('0x180685a90','0x1805d4e20')],
 'limitations':d['limitations']}
(Path(__file__).parent/'native-contract-byte-check.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:out[k] for k in ('targetMatches','instructionCount','allInstructionsMatch')}))
