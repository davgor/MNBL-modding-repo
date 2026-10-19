"""Bounded saved Mission proof for the installed 685a90 Agent job consumer."""
from pathlib import Path
import json, hashlib, sys
ROOT=Path(__file__).resolve().parents[3]
sys.path[:0]=[str(ROOT/'build/battle-scale/python-libs'),str(ROOT/'tools/battle-scale')]
import pefile,capstone
from audit_pool_tick_spill import backward_proof,canonical
from patch_native_copy import safe_review_output
from native_instruction_semantics import memory_operand_writes

def build():
 folder=ROOT/'evidence/engine-extension'
 paths=[folder/n for n in ['native-pool-reference-inventory.json','pool-semantic-review-mid.json','managed-core-callback-bodies.json','native-single-mode-guard.json']]
 inv,mid,managed,mode=[json.loads(p.read_text()) for p in paths]
 bundles=[folder/n for n in ['native-ghidra-evidence.json','native-pool-ghidra-evidence.json','native-expansion-ghidra-evidence.json','native-closure-ghidra-evidence.json']]
 bundles=[p for p in bundles if p.exists()];paths+=bundles
 records=[r for p in bundles for r in json.loads(p.read_text()).get('records',[])]
 def evidence(a):
  rows=[r for r in records if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==hex(0x180000000+a)]
  if not rows:raise ValueError('Missing retained body '+hex(a))
  r=rows[-1];return dict(procedure=hex(0x180000000+a),evidenceId=r['evidence_id'],normalizedResult=r['normalized_result'])
 callees=[evidence(a) for a in [0x685a90,0x5d4e20,0x61a0c0,0x2ca050,0x690c30,0x690470,0x6899a0,0x6093e0,0x5d3e70,0x646770,0x57f8e0,0x5d42e0,0x6133b0,0x685930]]
 binary=Path(inv['binary']['path']);source=binary.read_bytes();sha=hashlib.sha256(source).hexdigest()
 if sha!='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':raise ValueError('Unsupported original DLL')
 pe=pefile.PE(data=source);base=pe.OPTIONAL_HEADER.ImageBase
 md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
 def span(a,z):
  rows=list(md.disasm(pe.get_data(a-base,z-a),a))
  if sum(i.size for i in rows)!=z-a:raise ValueError('Decode gap')
  return rows
 def raw(i):return dict(address=hex(i.address),bytes=i.bytes.hex(),decoded=i.mnemonic+' '+i.op_str)
 body=span(base+0x685a90,base+0x6863f0);by={i.address:i for i in body}
 registration_initializer=span(base+0x56ff70,base+0x56ffde)
 initby={i.address:i for i in registration_initializer}
 if initby[base+0x56ffae].op_str!='ecx, ecx' or initby[base+0x56ffc9].op_str!='dword ptr [rdx + 8], ecx':raise ValueError('Registration counter initialization differs')
 owner=mid['missionMethodFlowReview']['entryContracts']['0x180685a90']
 if owner['entryRegisters'].get('rcx')!='agent-path:32':raise ValueError('Missing typed Agent job record owner')
 for a,s in [(0x685a96,'rbp, [rsp - 0x1f0]'),(0x685a9e,'rsp, 0x2f0'),(0x685abd,'r13, qword ptr [rcx + 0x20]'),(0x685b25,'rax, qword ptr [r13 + 0xab8]'),(0x685b33,'qword ptr [rbp - 0x70], rax')]:
  if by[base+a].op_str!=s:raise ValueError('Frame or owner changed '+hex(a))
 store=base+0x685b33;last=base+0x6862b5
 # Entire loop through386 belongs to the lifetime: later callbacks can affect
 # the next iteration. The final epilogue does not branch back into the body.
 writes=[];addresses=[];calls=[]
 for i in body:
  if store<=i.address<base+0x68639c:
   if i.mnemonic=='call':calls.append(i)
   for operand_index,o in enumerate(i.operands):
    if o.type!=capstone.x86.X86_OP_MEM or i.reg_name(o.mem.base) not in ('rsp','rbp'):continue
    if o.mem.index:raise ValueError('Indexed stack operand')
    off=o.mem.disp+(0x100 if i.reg_name(o.mem.base)=='rbp' else 0)
    if memory_operand_writes(i,operand_index):
     writes.append(dict(raw(i),stackOffset=off,size=o.size))
     if off<0x98 and off+o.size>0x90 and i.address!=store:raise ValueError('Mission slot overwritten')
    if i.mnemonic=='lea':
     addresses.append(dict(raw(i),stackOffset=off))
     if 0x90<=off<0x98:raise ValueError('Mission slot escaped')
  if i.address>=base+0x68639c and i.group(capstone.CS_GRP_JUMP):raise ValueError('Late epilogue branch')
 expected=[0x685c5e,0x685fa5,0x686056,0x6860a8,0x6860e9,0x686104,0x6861fb,0x686277,0x6862fb]
 if [i.address-base for i in calls]!=expected:raise ValueError('Lifetime call coverage differs')
 derived=[]
 for i in body:
  for n,o in enumerate(i.operands):
   if o.type==capstone.x86.X86_OP_MEM and i.reg_name(o.mem.base) not in ('rsp','rbp','rip') and memory_operand_writes(i,n):derived.append(raw(i))
 if [int(x['address'],16)-base for x in derived]!=[0x686002,0x68601f,0x686211,0x68635d,0x686363,0x68638e,0x686395]:raise ValueError('Unreviewed derived write')
 particle=next(x for x in managed['layouts'] if x['type']=='TaleWorlds.MountAndBlade.HitParticleResultData')
 reaction=next(x for x in managed['layouts'] if x['type']=='TaleWorlds.MountAndBlade.MeleeCollisionReaction')
 if [f['type'] for f in particle['fields']]!=['System.Int32']*3 or 'SequentialLayout' not in particle['attributes']:raise ValueError('Particle ABI differs')
 if reaction['fields']!=[dict(name='value__',type='System.Int32',offset=-1)]:raise ValueError('Reaction ABI differs')
 managed_methods=[x for x in managed['methods'] if x['name'] in ['Mission_MeleeHitCallback','Mission_OnAgentHitBlocked'] or x['name']=='Invoke' and any(n in x['type'] for n in ['Mission_MeleeHitCallback_delegate','Mission_OnAgentHitBlocked_delegate'])]
 effects=[
  dict(call='0x180685c5e',callee='6133b0',outputs=[],proof='RCX is external R14 object; no caller-local pointer argument.'),
  dict(call='0x180685fa5',callee='5d4e20',outputs=[[0x78,4],[0x80,12],[0xb0,None]],proof='Table210 is delegate61 Mission_MeleeHitCallback. param2=RSPb0 AttackCollisionData typed ref; param6=Agent+6c Single ref external; param7=RSP78 Int32 reaction ref; param12=RSP80 three-Int32 particle ref. Other struct values are copied into callee-local buffers. Typed writes have nonnegative offsets; no ref can reach saved90..97.'),
  dict(call='0x180686056',callee='61a0c0',outputs=[[0x280,16]],proof='Four float writes through RDX retained in R9; helper5e4480 receives only callee-local input/output, writes64 bytes to its RDX and preserves R9/R11.'),
  dict(call='0x1806860a8',callee='2ca050',outputs=[[0x248,16]],proof='Complete leaf writes only four float fields at RDX offsets0..c. RCX/R8 local inputs readonly; no negative writes.'),
  dict(call='0x1806860e9',callee='690c30',outputs=[],proof='Input particle RSP80 reads three Int32s; input vector RSP248 copied16 bytes by690470.690470 copies scalar particle IDs before replacing its local parameter slot with refcount handles; it does not retain caller pointers. Queue copies own values. Network branch excluded by Single0.'),
  dict(call='0x180686104',callee='685930',outputs=[],proof='RCX=Agent+28; other arguments scalar; no caller stack address.'),
  dict(call='0x1806861fb',callee='6899a0',outputs=[],proof='Caller sets local1b8+4c=17 immediately before call. Prelude reads external Agent fields; case17 only reads local input fields and forwards scalar values. Its only post-switch forwarding of param3 is63cca0 in network modes, unreachable under enforced Single0.'),
  dict(call='0x180686277',callee='5d42e0',outputs=[],proof='Pointer stack argument5 is external RBX+RDI+c28; argument7 is zero. Callee reads stack100 and110 after pushes38/allocationa0, corresponding to caller20/30. Argument6 caller28 is never read; stale local address there is not consumed. RCX Mission, other used arguments external/scalar.'),
  dict(call='0x1806862fb',callee='6093e0',outputs=[[0xb0,None]],proof='AttackCollisionData startsRSPb0 above Mission90. Native input uses nonnegative fields and passes input+70 to5d3e70, which copies16 bytes only. Table240 delegate67 OnAgentHitBlocked receives typed AttackCollisionData ref. No raw address arithmetic is exposed to managed callback.'),
 ]
 for e in effects:
  for a,n in e['outputs']:
   if a<0x98 and (n is None or a+n>0x90):raise ValueError('Bounded output overlaps saved Mission')
 helpers=[];helper_rows={}
 for a,z in [(0x61a0c0,0x61a258),(0x5e4480,0x5e4678),(0x2ca050,0x2ca197)]:
  rows=span(base+a,base+z);helper_rows[a]=rows;helpers.extend(raw(i) for i in rows)
  if any('r11' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]} for i in rows):raise ValueError('R11 modified')
  cs=[i.operands[0].imm-base for i in rows if i.mnemonic=='call']
  if cs!=([0x5e4480] if a==0x61a0c0 else []):raise ValueError('Unexpected helper call')
  if a==0x5e4480 and any('r9' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]} for i in rows):raise ValueError('Output alias R9 modified')
  if any(i.group(capstone.CS_GRP_JUMP) and (i.operands[0].type!=capstone.x86.X86_OP_IMM or not base+a<=i.operands[0].imm<base+z) for i in rows):raise ValueError('Unreviewed helper branch')
 seeds={}
 for a in [0x685f27,0x685f70,0x685fd5,0x6862b5]:
  i=by[base+a]
  if i.operands[1].type!=capstone.x86.X86_OP_MEM or i.op_str.split(', ',1)[1]!='qword ptr [rbp - 0x70]':raise ValueError('Reload changed')
  # Prove every predecessor passes the initial store, including loop edges.
  nodes,origins,count=backward_proof(body,i.address+i.size,i.reg_name(i.operands[0].reg),store,last,'rbp',-0x70)
  seeds[hex(i.address)]=dict(register=i.reg_name(i.operands[0].reg),pointerKind='mission',biasBytes=0,instruction=raw(i),backwardStates=count,provenanceInstructions=[raw(by[x]) for x in nodes])
 # R11 crosses two calls. Exact complete helpers above establish preservation,
 # then no other R11 definition exists between the reload and this field.
 interval=[i for i in body if base+0x685fd5<i.address<base+0x6860c1]
 if any('r11' in {canonical(i.reg_name(r)) for r in i.regs_access()[1]} for i in interval):raise ValueError('Mission R11 redefined')
 i=by[base+0x6860c1]
 # Suppress only the generic volatile-call rule for these two specifically
 # proved R11-preserving calls; retain original CFG, register writes and bytes.
 class PreservedCall:
  def __init__(self,original):self.original=original
  def __getattr__(self,key):return getattr(self.original,key)
  @property
  def mnemonic(self):return 'nop'
 proof_body=[PreservedCall(x) if x.address in (base+0x686056,base+0x6860a8) else x for x in body]
 nodes,origins,count=backward_proof(proof_body,i.address,'r11',store,last,'rbp',-0x70)
 mapping=dict(address=hex(i.address),instructionBytes=i.bytes.hex(),operandIndex=1,originalValue=hex(i.operands[1].mem.disp),missionBiasBytes=0,missionRegister='r11',classification='mission-relative-field',provenanceKind='bounded-Agent-job-Mission-spill-and-exact-R11-preservation',provenanceInstructions=[raw(by[store])]+[raw(by[a]) for a in nodes],reloadOrigins=[hex(a) for a in origins],backwardStateCount=count,evidenceIds=[x['evidenceId'] for x in callees])
 paths+=[Path(__file__).resolve(),ROOT/'tools/battle-scale/native_instruction_semantics.py']
 result=dict(schemaVersion=1,targetSha256=sha,status='bounded-installed-Single0-Agent-job-Mission-spill-reviewed',gameExecuted=False,sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in paths],entryContract=owner,functionInstructions=[raw(i) for i in body],helperInstructions=helpers,frame=dict(rbpMinusRsp=256,missionSlot=[0x90,8],initialStore=raw(by[store]),loopBackedge=raw(by[base+0x686386])),directStackWrites=writes,localAddressMaterializations=addresses,callEffects=effects,retainedCalleeEvidence=callees,managedCallbackMethods=managed_methods,managedParticleLayout=particle,managedReactionLayout=reaction,callbackBindingProof='CoreCallbacksGenerated.Initialize indices61/67; native646770 cases3d/43 store+1f8/+228 fromManaged_interface+18, reachingglobal table210/240.57f8e0counter passed thenincremented;56ff70 initializeszero;CoreManaged initializes delegates in ascending array order.',exactReloadSeeds=seeds,instructionSiteMappings=[mapping],limitations=['Installed typed managed callback ABI and enforced Single0 mode are required. Foreign hooks and unsafe arbitrary stack writes are outside profile.','Only listed saved-pointer seeds and exact field are established. No game executed or whole patch certified.'])
 result['callbackInitializerInstructions']=[raw(i) for i in registration_initializer]
 result['derivedMemoryWrites']=derived
 result['derivedWriteProof']='Two RDX writes lock/unlock external Agent skeleton bone storage: Agent+810 pointee+18 plus signed bone index*100. Five R13 writes target the original Agent from record+20; R13 has no intervening reassignment. No computed pointer store targets this frame.'
 out=safe_review_output(folder/'native-job-mission-spill-review.json',[binary,*paths]);out.write_text(json.dumps(result,indent=2)+'\n')
 print(json.dumps(dict(reloads=len(seeds),fields=1,lifetimeCalls=len(calls),instructions=len(body))))
if __name__=='__main__':build()
