"""Bounded original-binary Mission spill proof for three Missile initializer fields."""
from pathlib import Path
import json,hashlib,sys,struct
ROOT=Path(__file__).resolve().parents[3]
sys.path[:0]=[str(ROOT/'build/battle-scale/python-libs'),str(ROOT/'tools/battle-scale')]
import capstone,pefile
from audit_pool_tick_spill import backward_proof
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
 folder=ROOT/'evidence/engine-extension'
 inventory_path=folder/'native-pool-reference-inventory.json';owner_path=folder/'pool-patch-generator-review.json'
 inv=json.loads(inventory_path.read_text());binary=Path(inv['binary']['path']);source=binary.read_bytes()
 sha=hashlib.sha256(source).hexdigest()
 if sha!='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':raise ValueError('Unsupported original DLL')
 pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase;memory=pe.get_memory_mapped_image()
 md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
 def span(a,z):
  rows=list(md.disasm(pe.get_data(a-base,z-a),a))
  if sum(i.size for i in rows)!=z-a:raise ValueError('Incomplete decode '+hex(a))
  return rows
 def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),instruction=i.mnemonic+' '+i.op_str)
 bundles=[folder/n for n in ['native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json']]
 records=[r for p in bundles for r in json.loads(p.read_text())['records']]
 def evidence(rva):
  rows=[r for r in records if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(base+rva)]
  if not rows:raise ValueError('Missing retained procedure '+hex(rva))
  r=rows[-1];return dict(procedure=hex(base+rva),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result'])
 callees=[evidence(a) for a in [0x660c00,0x6d7000,0x433b70,0x660710,0x5bc1a0,0x1be5f0,0x671320,0x6de6f0,0x08e630,0x6df070,0x34b7d0,0x35df00,0x2c1920,0x42d800,0x309160,0x359130,0x36e7c0,0x464420,0x625a70,0x047bd0,0x36e550,0x36e3a0,0x6290e0,0x6288e0]]
 body=span(base+0x660c00,base+0x661753);by={i.address:i for i in body};store=base+0x660c37;last=base+0x661503
 assert by[store].op_str=='qword ptr [rbp - 0x68], rdx'
 assert by[last].op_str=='rax, qword ptr [rbp - 0x68]'
 # Seven pushes and140 allocation, RBP=entryRSP-78: bodyRBP=bodyRSP+100.
 assert by[base+0x660c0e].op_str=='rbp, [rax - 0x78]'
 assert by[base+0x660c12].op_str=='rsp, 0x140'
 writes=[];local_addresses=[];calls=[]
 for i in body:
  if store<=i.address<=last:
   if i.mnemonic=='call':calls.append(i)
   for operand_index,op in enumerate(i.operands):
    if op.type!=capstone.x86.X86_OP_MEM or i.reg_name(op.mem.base) not in ['rbp','rsp']:continue
    if op.mem.index:raise ValueError('Indexed stack operand requires separate proof')
    off=op.mem.disp+(0x100 if i.reg_name(op.mem.base)=='rbp' else 0)
    if memory_operand_writes(i,operand_index):
     writes.append(dict(raw(i),stackOffset=off,size=op.size))
     if off<0xa0 and off+op.size>0x98 and i.address!=store:raise ValueError('Mission spill overwritten')
    if i.mnemonic=='lea':
     local_addresses.append(dict(raw(i),stackOffset=off))
     if 0x98<=off<0xa0:raise ValueError('Mission spill address escaped')
  if i.address>last and i.group(capstone.CS_GRP_JUMP):
   if i.operands[0].type!=capstone.x86.X86_OP_IMM or i.operands[0].imm<=last:raise ValueError('Later path reenters lifetime')
 local_effects=[
  dict(calls=[0x660cb9],callee='6d7000',outputs=[],proof='R9=RSP90 points to one smart-pointer slot. param4 and saved aliases local178/local168 are only dereferenced. Nested reference-count calls receive the pointee, never the caller slot.'),
  dict(calls=[0x660f2a,0x660ffe],callee='433b70',outputs=[],proof='All caller-local arguments are readonly plane/position inputs; the16byte outputR9 is Missile+68.'),
  dict(calls=[0x66100f],callee='660710',outputs=[[0x54,16]],proof='RDX receives four float result fields only. Other helpers receive callee-local matrices, not this output.'),
  dict(calls=[0x6611ef],callee='5bc1a0',outputs=[[0x70,8]],proof='Writes one pointeeQWORD and returns exactlyRDX; virtual referencecount call sees pointee.'),
  dict(calls=[0x661215],callee='1be5f0',outputs=[],proof='RDX is previous returnedRSP70. All uses dereference its firstQWORD; only resulting heapentity pointers are forwarded.'),
  dict(calls=[0x66125b],callee='671320',outputs=[[0xa0,64]],proof='RDX output contains exactly8QWORDs, no further callees.'),
  dict(calls=[0x661266],callee='6de6f0',outputs=[],proof='RDX is prior matrix resultRSPa0. Forwarded only to installed entity-component virtual158. Every concrete implementation below is input-only, including synchronous child dispatch.'),
  dict(calls=[0x6612cc],callee='08e630',outputs=[[0xa0,44]],proof='Writes matrixfloat offsets0,4,8,10,14,18,20,24,28; no calls or negative offsets.')]
 local_sites={base+a for e in local_effects for a in e['calls']}
 external_sites=[0x660c8e,0x660e3f,0x6610d7,0x661137,0x661174,0x6611a0,0x6611c9,0x66129a,0x6612f3,0x66130f,0x6613b4,0x6613be,0x6613d6]
 if local_sites|{base+a for a in external_sites}!={i.address for i in calls}:raise ValueError('Call coverage differs')
 for effect in local_effects:
  if any(a<0xa0 and a+n>0x98 for a,n in effect['outputs']):raise ValueError('Output reaches saved Mission')
 # Enumerate installed concrete rglEntity_component class family from PE RTTI.
 rtti=[]
 for sec in pe.sections:
  if sec.Name.rstrip(bytes([0]))!=b'.rdata':continue
  lo=sec.VirtualAddress;hi=lo+sec.Misc_VirtualSize
  for a in range(lo,hi-24,4):
   sig,off,cd,typ,hier,selfr=struct.unpack_from('<6I',memory,a)
   if sig!=1 or selfr!=a or typ>=len(memory)-16 or hier>=len(memory)-16:continue
   hs,ha,n,arr=struct.unpack_from('<4I',memory,hier)
   if n>50 or arr+n*4>len(memory):continue
   descriptors=[struct.unpack_from('<I',memory,arr+j*4)[0] for j in range(n)]
   if any(r+4>len(memory) for r in descriptors):continue
   bases=[struct.unpack_from('<I',memory,r)[0] for r in descriptors]
   if 0xd13f30 not in bases:continue
   pos=memory.find(struct.pack('<Q',base+a),lo,hi)
   while pos>=0:
    vt=pos+8;target=struct.unpack_from('<Q',memory,vt+0x158)[0]
    rtti.append(dict(className=memory[typ+16:typ+180].split(bytes([0]))[0].decode(),vtable=hex(base+vt),locator=hex(base+a),locatorBytes=memory[a:a+24].hex(),baseTypeDescriptors=[hex(x) for x in bases],typeMethod=hex(struct.unpack_from('<Q',memory,vt+0xa0)[0]),matrixMethod=hex(target),slotBytes=memory[vt+0x158:vt+0x160].hex()))
    pos=memory.find(struct.pack('<Q',base+a),pos+1,hi)
 expected={base+x for x in [0x047bd0,0x2c1920,0x309160,0x359130,0x36e7c0,0x42d800,0x464420,0x625a70]}
 if len(rtti)!=11 or {int(r['matrixMethod'],16) for r in rtti}!=expected:raise ValueError('Component vtable domain differs')
 helpers=[]
 for a,z in [(0x2c9e90,0x2c9f18),(0x279700,0x27979e),(0x2797a0,0x2797f2),(0x2bafb0,0x2bafbf),(0x629150,0x629185)]:helpers.extend(raw(i) for i in span(base+a,base+z))
 # Reviewed component methods never write through the supplied matrix pointer.
 component_proof=['Meta_mesh and Weapon_meta_mesh158 target2c1920: input compared with2c9e90, tested with2797a0->279700, copied64bytes to object330. Both virtual170 slots equal2bafb0, which replacesRDX withreceiver214 before tailcall; no local input forwarded there.',
 'Composite42d800, particle309160, decal359130 andlight464420 copy/read input and write onlyreceiver fields. Decal downstream08e250 receivesreceiver-owned buffers.',
 'Cloth36e7c0 forwards matrix unchanged to component+48; constructor36e550->36e3a0 installs the supplied mesh there. Weapon initializer supplies6df070-createdWeapon_meta_mesh. Existing entitycomponent selections are by concrete virtualA0 type.',
 'Face625a70 packages matrix into local callback,6288e0 synchronously iterates childcomponent pointers and6290e0 forwards the same matrix to child158; it never writes matrix or stores it beyond that synchronous call. Cleanup629150 withmode0 branches directly toXOR EAX,EAX;RET withoutdereferencingcapture. The recursive contract is input-only on the same installed component domain.',
 '047bd0 is RET. The abstract class itself has purecall type query; no instantiated abstract receiver is assumed. Foreign native component implementations are outside this installed-build test contract.']
 review=json.loads(owner_path.read_text())
 owner=review['highOperandRecheck']['supportingOwnerProofs']['missileMissionOwner']
 mappings=[]
 for a in [0x661507,0x661516,0x661586]:
  i=by[base+a];nodes,origins,count=backward_proof(body,i.address,'rax',store,last,'rbp',-0x68)
  mappings.append(dict(address=hex(i.address),instructionBytes=i.bytes.hex(),operandIndex=1,originalValue=hex(i.operands[1].mem.disp),missionBiasBytes=0,missionRegister='rax',classification='mission-relative-field',provenanceKind='bounded-Missile-initializer-Mission-spill',provenanceInstructions=[raw(by[store])]+[raw(by[x]) for x in nodes],reloadOrigins=[hex(x) for x in origins],backwardStateCount=count,evidenceIds=[x['evidenceId'] for x in callees]))
 result=dict(schemaVersion=1,sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [inventory_path,owner_path,*bundles,Path(__file__).resolve(),ROOT/'tools/battle-scale/native_instruction_semantics.py']],targetSha256=sha,status='three-exact-fields-proved-under-installed-component-contract',gameExecuted=False,entryContract=owner,functionInstructions=[raw(i) for i in body],initialStore=raw(by[store]),lastReload=raw(by[last]),savedMissionBodyRspOffset=0x98,directStackWrites=writes,localAddressMaterializations=local_addresses,callInstructions=[raw(i) for i in calls],localPointerCallEffects=local_effects,otherCallSites=[hex(base+x) for x in external_sites],otherCallContract='These13 call sites receive external object pointers or scalar values, never an address into this stack frame. The retained complete function body supplies argument setup.',componentVtables=rtti,componentMatrixInputProof=component_proof,readonlyHelperInstructions=helpers,retainedCalleeEvidence=callees,instructionSiteMappings=mappings,limitations=['Normal-flow saved-pointer provenance only. Does not certify the whole pool expansion or arbitrary native component extensions.','Foreign vtables, malformed engine objects and preexisting memory corruption are outside the isolated installed-build profile.'])
 output=safe_review_output(folder/'native-pool-missile-spill-review.json',[binary,inventory_path,owner_path,*bundles,Path(__file__).resolve(),ROOT/'tools/battle-scale/native_instruction_semantics.py']);output.write_text(json.dumps(result,indent=2)+'\n')
 print(json.dumps(dict(mappedOperands=len(mappings),callSites=len(calls),componentVtables=len(rtti))))
if __name__=='__main__':build()
