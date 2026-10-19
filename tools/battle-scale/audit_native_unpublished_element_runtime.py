"""Bound the default unpublished element lifetime without running the game.

Original x64 bodies run in Unicorn with poisoned payloads, original image data,
and one explicit atan2f(-0,1) model. This is not real engine execution or an
approval for active objects, public pool publication, or exception injection
inside a partially completed native constructor.
"""
from pathlib import Path
from collections import Counter
import argparse,hashlib,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import capstone,pefile
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_READ,UC_HOOK_MEM_WRITE
from unicorn.x86_const import *
from patch_native_copy import safe_review_output
SOURCE=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
SHA='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE=0x180000000;STACK=0x50000000;BUFFER=0x60000000;OBJECT=BUFFER+0x100;MATH=0x40000000;STOP=MATH+0x100
SPANS={
 'agentConstructor':(0x606a20,0x607074),'agentDestructor':(0x6070c0,0x607388),
 'memberValueInit':(0x696420,0x69652d),'memberReferenceInit':(0x619d20,0x619dd6),
 'nullReferenceConstructor':(0x4dcb0,0x4dcbb),'nullReferenceDestructor':(0x47830,0x47851),
 'aiDestructor':(0x618090,0x618115),'vectorConstructor':(0x9dfd98,0x9dfe08),
 'vectorDestructor':(0x9dfe08,0x9dfe73),'atan2fThunk':(0x9e3213,0x9e3219),
 'defaultCfgDispatcher':(0x9e3660,0x9e3662),
 'companion230Constructor':(0x5e58e0,0x5e5905),'companion230ValueInit':(0x694630,0x694858),
 'companion2c8Constructor':(0x5d8860,0x5d88ac)}
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def row(i):return {'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
def merge_ranges(values):
 out=[]
 for a,z in sorted(values):
  if out and a<=out[-1][1]:out[-1][1]=max(out[-1][1],z)
  else:out.append([a,z])
 return out
def build():
 paths=[SOURCE,Path(__file__),ROOT/'tools/battle-scale/patch_native_copy.py',
        ROOT/'evidence/engine-extension/native-live-pool-growth-contract.json',
        ROOT/'evidence/engine-extension/native-pool-helper-escape-review.json',
        ROOT/'evidence/engine-extension/native-live-growth-rea-evidence.json']
 before={str(p):digest(p) for p in paths};assert digest(SOURCE)==SHA
 pe=pefile.PE(data=SOURCE.read_bytes());md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
 decoded={};bodies={}
 for name,(a,z) in SPANS.items():
  raw=pe.get_data(a,z-a);ins=list(md.disasm(raw,BASE+a));assert sum(i.size for i in ins)==len(raw),(name,hex(a))
  decoded.update({i.address:i for i in ins});bodies[name]={'start':hex(BASE+a),'endExclusive':hex(BASE+z),'bytes':raw.hex(),'instructions':[row(i) for i in ins]}
 config=pe.DIRECTORY_ENTRY_LOAD_CONFIG.struct
 assert config.GuardCFDispatchFunctionPointer==BASE+0xa2e4e8
 assert struct.unpack('<Q',pe.get_data(0xa2e4e8,8))[0]==BASE+0x9e3660
 imported=next((d.dll.decode(),s.name.decode(),s.address) for d in pe.DIRECTORY_ENTRY_IMPORT for s in d.imports if s.name==b'atan2f')
 assert imported==('api-ms-win-crt-math-l1-1-0.dll','atan2f',BASE+0xa2dfa8)
 image=pe.get_memory_mapped_image();cases=[];unique_reads=set();all_calls=[];all_executed=set();all_writes={};all_object_reads=set()
 specs=[('agent',0xc60,0x606a20,0x6070c0,0x10),('companion230',0x230,0x5e58e0,None,0x1c),('companion2c8',0x2c8,0x5d8860,None,0)]
 for name,size,ctor,dtor,header in specs:
  for poison in (0x00,0x55,0xaa,0xff):
   for sentinel in (0,0x777788889999aaaa):
    uc=Uc(UC_ARCH_X86,UC_MODE_64);uc.mem_map(BASE,(pe.OPTIONAL_HEADER.SizeOfImage+4095)&~4095);uc.mem_write(BASE,image)
    for a,n in ((STACK,0x10000),(BUFFER,0x2000),(MATH,0x1000)):uc.mem_map(a,n)
    uc.mem_write(BASE+0xa2dfa8,struct.pack('<Q',MATH));uc.mem_write(BASE+0xda29b8,struct.pack('<Q',sentinel))
    # Deliberately non-null registry callback global: it must remain unread
    # because freshly constructed Agent+18 is zero, not because global is null.
    uc.mem_write(BASE+0xda2a28,struct.pack('<Q',0x7777000011110000))
    uc.mem_write(MATH,b'\xc3');uc.mem_write(STOP,b'\xc3');uc.mem_write(BUFFER,b'\x69'*0x2000);uc.mem_write(OBJECT,bytes([poison])*size)
    initialized=set();writes=[];reads=[];calls=[];executed=[];math_calls=[];phase='constructor'
    regs={i.reg_name(o.reg):globals().get('UC_X86_REG_'+i.reg_name(o.reg).upper()) for i in decoded.values() for o in i.operands if o.type==capstone.x86.X86_OP_REG}
    def read(emu,access,address,width,value,user):
     pc=emu.reg_read(UC_X86_REG_RIP)
     if STACK<=address and address+width<=STACK+0x10000:return
     if OBJECT<=address and address+width<=OBJECT+size:
      missing=set(range(address-OBJECT,address-OBJECT+width))-initialized
      assert not missing,('read before initialization',name,hex(pc),hex(address-OBJECT),width,sorted(missing))
      reads.append((address-OBJECT,width));all_object_reads.add((name,pc,address-OBJECT,width));return
     assert BASE<=address and address+width<=BASE+len(image),('unexpected external read',hex(pc),hex(address),width)
     unique_reads.add((address-BASE,address-BASE+width))
    def write(emu,access,address,width,value,user):
     pc=emu.reg_read(UC_X86_REG_RIP)
     if STACK<=address and address+width<=STACK+0x10000:return
     assert OBJECT<=address and address+width<=OBJECT+size,('publication/out-of-bounds write',hex(pc),hex(address),width)
     initialized.update(range(address-OBJECT,address-OBJECT+width));writes.append((pc,address-OBJECT,width));all_writes[(pc,address-OBJECT,width)]=True
    def instruction(emu,pc,length,user):
     if pc==STOP:emu.emu_stop();return
     if pc==MATH:
      assert emu.reg_read(UC_X86_REG_XMM0)&0xffffffff==0x80000000
      assert emu.reg_read(UC_X86_REG_XMM1)&0xffffffff==0x3f800000
      assert (emu.reg_read(UC_X86_REG_RSP)+8)%16==0
      math_calls.append({'phase':phase,'xmm0FloatBits':'0x80000000','xmm1FloatBits':'0x3f800000','resultFloatBits':'0x80000000'})
      for n in ('RAX','RCX','RDX','R8','R9','R10','R11'):emu.reg_write(globals()['UC_X86_REG_'+n],0xeeeeeeeeeeeeeeee)
      for n in range(6):emu.reg_write(globals()['UC_X86_REG_XMM'+str(n)],(1<<128)-1)
      emu.reg_write(UC_X86_REG_XMM0,0x80000000);return
     assert pc in decoded,('unreviewed reachable instruction',hex(pc))
     i=decoded[pc];executed.append(pc);all_executed.add(pc)
     assert not any(o.type==capstone.x86.X86_OP_MEM and i.reg_name(o.mem.segment) in ('gs','fs') for o in i.operands),'Reachable TLS dependency'
     if i.mnemonic=='call':
      o=i.operands[0]
      if o.type==capstone.x86.X86_OP_IMM:target=o.imm
      elif o.type==capstone.x86.X86_OP_REG:target=emu.reg_read(regs[i.reg_name(o.reg)])
      else:
       def rv(r):return pc+i.size if i.reg_name(r)=='rip' else emu.reg_read(regs[i.reg_name(r)]) if r else 0
       addr=rv(o.mem.base)+rv(o.mem.index)*o.mem.scale+o.mem.disp;target=struct.unpack('<Q',emu.mem_read(addr,8))[0]
      item={'phase':phase,'site':hex(pc),'target':hex(target),'rcx':hex(emu.reg_read(UC_X86_REG_RCX)),'rdx':hex(emu.reg_read(UC_X86_REG_RDX)),'r8':hex(emu.reg_read(UC_X86_REG_R8)),'r9':hex(emu.reg_read(UC_X86_REG_R9))}
      if target==BASE+0x9e3660:
       callback=emu.reg_read(UC_X86_REG_RAX);assert callback==BASE+(0x4dcb0 if phase=='constructor' else 0x47830)
       item['cfgDispatchTarget']=hex(callback)
      calls.append(item)
    uc.hook_add(UC_HOOK_MEM_READ,read);uc.hook_add(UC_HOOK_MEM_WRITE,write);uc.hook_add(UC_HOOK_CODE,instruction)
    def execute(rva):
     rsp=STACK+0x8008;uc.reg_write(UC_X86_REG_RSP,rsp);uc.mem_write(rsp,struct.pack('<Q',STOP));uc.reg_write(UC_X86_REG_RCX,OBJECT)
     nonvolatile=['RBX','RBP','RSI','RDI','R12','R13','R14','R15']
     for index,reg in enumerate(nonvolatile):uc.reg_write(globals()['UC_X86_REG_'+reg],0xabcdef0000000000+index)
     for index in range(6,16):uc.reg_write(globals()['UC_X86_REG_XMM'+str(index)],int.from_bytes(bytes([index])*16,'little'))
     uc.emu_start(BASE+rva,STOP+1,count=20000)
     assert uc.reg_read(UC_X86_REG_RIP)==STOP and uc.reg_read(UC_X86_REG_RSP)==rsp+8
     for index,reg in enumerate(nonvolatile):assert uc.reg_read(globals()['UC_X86_REG_'+reg])==0xabcdef0000000000+index
     for index in range(6,16):assert uc.reg_read(globals()['UC_X86_REG_XMM'+str(index)])==int.from_bytes(bytes([index])*16,'little')
    execute(ctor);assert uc.reg_read(UC_X86_REG_RAX)==OBJECT
    uc.mem_write(OBJECT+header,struct.pack('<i',-1));initialized.update(range(header,header+4))
    default=bytes(uc.mem_read(OBJECT,size));destructor_reads=[]
    if name=='agent':
     zero32=[0x18];zero64=[0xab8,0x888,0xac0,0xad8,0xb80,0xae0,0xae8,0xb88,0x898,0x890,0x880,0x668,0x660,0x658,0x650,0x648,0x5e8,0x4e0]+list(range(0x670,0x6a0,8))
     for off in zero32:assert default[off:off+4]==bytes(4),hex(off)
     for off in zero64:assert default[off:off+8]==bytes(8),hex(off)
     read_start=len(reads);phase='destructor';execute(dtor);destructor_reads=reads[read_start:]
     assert [c['site'] for c in calls if c['phase']=='destructor' and c['target']!=hex(BASE+0x9e3660)]==['0x180607173','0x1806072ab']
     assert all(c['rcx']=='0x0' for c in calls if c['site']=='0x180607173')
     assert len(math_calls)==1
    else:assert not math_calls
    assert bytes(uc.mem_read(BUFFER,0x100))==b'\x69'*0x100 and bytes(uc.mem_read(OBJECT+size,0x2000-0x100-size))==b'\x69'*(0x2000-0x100-size)
    all_calls.extend(calls)
    cases.append({'kind':name,'poison':hex(poison),'emptyValueSentinel':hex(sentinel),'instructionSteps':len(executed),'uniqueExecutedInstructions':len(set(executed)),'objectWriteEvents':len(writes),'initializedBytes':len(initialized),'defaultObjectSha256':hashlib.sha256(default).hexdigest(),'destructorReadRanges':merge_ranges((a,a+n) for a,n in destructor_reads),'calls':calls,'mathModelCalls':math_calls,'outOfObjectWrites':0,'uninitializedObjectReads':0,'passed':True})
 bundle=json.loads(paths[-1].read_text());rea=[{'evidenceId':r['evidence_id'],'procedure':r['parameters']['procedure'],'result':r['normalized_result']} for r in bundle['records'] if r.get('parameters',{}).get('procedure') in ('0x180606a20','0x1806070c0')]
 assert len(rea)==2
 data_reads=[{'rva':hex(a),'endExclusiveRva':hex(z),'diskBytes':pe.get_data(a,z-a).hex(),'originalMappedBytes':image[a:z].hex(),'loaderZeroFilled':len(pe.get_data(a,z-a))<z-a} for a,z in merge_ranges(unique_reads)]
 for forbidden in (0xda2a28,0xda9d60,0xd9ae48):
  assert not any(a<=forbidden<z for a,z in unique_reads),'Default path consulted registry/Mission/task-owner global'
 assert before=={str(p):digest(p) for p in paths}
 return {'schemaVersion':1,'status':'default-unpublished-lifetime-closed-under-explicit-runtime-dependencies','nativeSourceSha256':SHA,'sources':before,'nativeBodies':bodies,'retainedRea':rea,
  'summary':{'replayCases':len(cases),'uniqueExecutedInstructions':len(all_executed),'retainedInstructions':len(decoded),'unexpectedCallTargets':0,'observedEngineAllocationCalls':0,'observedRegistryOrMissionReads':0,'observedGlobalWrites':0,'tlsInstructionsOnDefaultPath':0},
  'cases':cases,'imageReadRanges':data_reads,'objectMemoryWrites':[{'instruction':hex(pc),'offset':hex(off),'width':n} for pc,off,n in sorted(all_writes)],
  'destructorPreconditions':{'fullyConstructedAndNeverActivated':True,'managedCallbackHandleOffset':'0x18','managedCallbackHandleValue':0,'missionOffset':'0xab8','missionValue':0,'allOwnedReferencesAndVectorsRemainDefault':True,'noSecondDestruction':True},
  'callClosure':{'constructorDirect':['696420 local value initialization','9e3213 imported atan2f(-0,1)','619d20 local reference initialization; +30 already zero','9dfd98 six callbacks to04dcb0, each stores null'],
                 'constructorConditional':'619d94 and60700e reference-release virtual calls are unreachable after the reviewed null stores; poisoned-buffer replay does not suppress those instructions.',
                 'destructorReachable':['618090(RCX=0) returns immediately at618114','9dfe08 six callbacks to047830 over null references'],
                 'destructorConditional':'Every owned-resource/free/virtual-release branch is controlled by constructor-written zeros; Agent+18 skips both managed-registry callback calls even with a non-null global.',
                 'guardCf':{'pointerSlot':hex(config.GuardCFDispatchFunctionPointer),'originalTarget':'0x1809e3660','bytes':'ffe0','meaning':'Standard loader CFG dispatch; original image is JMP RAX. Only reviewed internal callback addresses are supplied. A real copied-image fixture must bind a valid local dispatcher or preserve this relocated thunk; a live module uses its loader-established dispatcher.'},
                 'math':{'dll':imported[0],'symbol':imported[1],'iat':hex(imported[2]),'model':'Only exact atan2f(-0.0,1.0) return -0.0 is modeled. All caller-volatile GPR/XMM values are deliberately clobbered. CRT implementation and loader binding are not executed by this audit.'}},
  'privateProbeAssessment':{'boundedPreconditionSupported':True,'liveProbeImplementationReviewed':False,
     'scope':'Only fully completed, unactivated, privately allocated default objects. Their addresses and metadata are never stored into an existing Mission, published directory, native job, managed Agent wrapper, or registry.',
     'construction':'Call606a20 for each separate c60 destination;5e58e0 for230;5d8860 for2c8. Set headers10/1c/0 to-1 after success. Preserve16-byte Agent/230 and8-byte2c8 element alignment. Do not memcpy default templates.',
     'failureInjection':'Allocator failure or failure BEFORE entering a constructor only. Reverse-destroy previously completed Agents with6070c0; default companions require no resource destructor. Do not invoke dtor on incomplete or already destroyed element.',
     'runtimeDependencies':['Exact original loaded native bytes and standard Win64 ABI; original module loader established relocations, CRT import and CFG dispatch.',
                            'Default data constants/empty-value global are copied only; no Mission/worker fence is needed for these private objects unless an unreviewed callback or activation is introduced.',
                            'Real CRT atan2f binding is required for a native copied-image test; Unicorn math modeling is not a native runtime result.'],
     'unknowns':['Actual probe DLL, loader identity checks, allocator alignment, exception containment and cleanup must be separately reviewed.',
                 'Asynchronous faults or exceptions inside a real partially completed engine constructor are not injected/proven here; use native EH and never run a second cleanup on failed element.',
                 'No live growth publication, existing Agent access, active destruction, job/render quiescence or game-wide integration approval.']},
  'copiedImageNextTest':{'feasibleClosedSlice':True,'requiredBindings':['Copy original instruction bytes and referenced image constants with correct relocations; keep vtable addresses coherent even though no default-path virtual method is invoked.','Bind only actual CRT atan2f atA2DFA8; preserve the original relocated CFG JMP RAX dispatcher atA2E4E8.','Do not null or bypass reachable release/allocation callbacks; unexpected paths must fail the fixture.','Keep image writable data separate from installed module and prevent arbitrary image execution; invoke only reviewed element entries.'], 'actualNativeExecutionPerformed':False},
  'enginePatchApproved':False,'gameLaunched':False,'installedNativeRead':False}
def main():
 p=argparse.ArgumentParser();p.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-unpublished-element-runtime-contract.json'));args=p.parse_args()
 out=build();path=safe_review_output(args.output,list(out['sources']));path.write_text(json.dumps(out,indent=2)+'\n');print(json.dumps({'status':out['status'],'summary':out['summary'],'sha256':digest(path)}))
if __name__=='__main__':main()
