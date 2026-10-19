"""Export exact helper CALL owner states from the replayed native CFG proof; no target execution."""
import sys,runpy,json,hashlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/battle-scale'))
from patch_native_copy import safe_review_output
m=runpy.run_path(str(ROOT/'tools/battle-scale/prove_mission_aliases.py'))
# Independent source-byte enumeration avoids a hash dependency cycle with the
# helper escape audit, which consumes this exact call-state export.
targets={0x180000000+x for x in (0x5d7df0,0x5d8020,0x5d81e0,0x5d8220,0x5d8390,0x5d8610,0x5d8730,0x5d8740,0x5d87d0,0x5d8840,0x5d91f0,0x5d9350,0x5d94a0)}
candidates=[]
for section in m['pe'].sections:
 if not section.Characteristics & 0x20000000:continue
 data=section.get_data()
 for offset in range(len(data)-4):
  if data[offset]!=0xe8:continue
  address=m['pe'].OPTIONAL_HEADER.ImageBase+section.VirtualAddress+offset
  target=address+5+int.from_bytes(data[offset+1:offset+5],'little',signed=True)
  if target in targets:
   candidates.append({'address':hex(address),'bytes':data[offset:offset+5].hex(),'decoded':'call '+hex(target),'target':hex(target),'discovery':'every-byte direct-call candidate; exact CFG boundary independently checked below'})
extra=[0x1805c5471,0x1805dfe33,0x1805e0078,0x1805e0173,0x1805e0763,0x1805e0be4,0x1805e1065,0x18068f345,0x1805d3a5b,0x1805fe238,0x18061f5b8,0x180706580]
rows=[]
for candidate in candidates+[{'address':hex(x)} for x in extra]:
 address=int(candidate['address'],16);matches=[]
 for root,(states,report) in m['analysis'].items():
  if states is None or address not in states:continue
  insns=m['cache'][root][0];ins=insns.get(address)
  if ins is None:continue
  vals={str(r):{'pointerKind':v[0],'biasBytes':v[1],'definitionAddresses':[hex(x) for x in sorted(v[2])],'definitionInstructions':[m['flow'].basic(insns[x]) for x in sorted(v[2]) if x in insns]} for r,v in states[address].items()}
  matches.append({'address':hex(address),'functionStart':hex(root),'instruction':m['flow'].basic(ins),'entryContract':m['contracts'][root],'converged':report['status']=='converged','state':vals,'nearbyInstructions':[m['flow'].basic(i) for a,i in sorted(insns.items()) if address-80<=a<=address+40]})
 rows.append({'candidate':candidate,'analyses':matches})
out={'sourceSha256':m['d']['targetSha256'],'scope':'Exact pre-instruction must-analysis states; missing provenance remains unproved','proverSha256':hashlib.sha256((ROOT/'tools/battle-scale/prove_mission_aliases.py').read_bytes()).hexdigest(),'calls':rows[:len(candidates)],'immediates':rows[len(candidates):]}
out['sourceEvidence']=[{'path':str(x),'sha256':hashlib.sha256(x.read_bytes()).hexdigest()} for x in [ROOT/'evidence/engine-extension/pool-semantic-review-mid.json',Path(__file__).resolve()]]
path=safe_review_output(ROOT/'evidence/engine-extension/native-helper-call-provenance.json',[Path(m['inv']['binary']['path']),*[Path(x['path']) for x in out['sourceEvidence']],*m['bundle_paths']])
path.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'output':str(path),'calls':len(candidates),'proven':sum(any('rcx' in y['state'] for y in x['analyses']) for x in out['calls']),'missing':[x['candidate']['address'] for x in out['calls'] if not any('rcx' in y['state'] for y in x['analyses'])]}))
