"""Independent byte review and concrete cache-producer replay; no game execution."""
from pathlib import Path
import hashlib, importlib.util, json, struct, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import capstone,pefile
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
from unicorn.x86_const import *
BASE=0x180000000
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def main():
    path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v2.json'
    assert digest(path)=='ea28a0415a95a3462d158d1493532aa3a9b4b884b0e32f2154896a6b1bb864aa'
    report=json.loads(path.read_text());pins={d['path']:d['sha256'] for d in report['dependencies']}
    for p,sha in pins.items():assert digest(p)==sha,p
    source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    assert digest(source)==report['binarySha256']
    pe=pefile.PE(str(source));md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);rows=set()
    def check(value):
        if isinstance(value,dict):
            if 'address' in value and 'bytes' in value:
                address=int(value['address'],16);raw=bytes.fromhex(value['bytes'])
                assert pe.get_data(address-BASE,len(raw))==raw
                decoded=list(md.disasm(raw,address));assert len(decoded)==1 and decoded[0].size==len(raw)
                rows.add((address,raw))
            for item in value.values():check(item)
        elif isinstance(value,list):
            for item in value:check(item)
    check(report)
    assert report['additionalProven']==2 and report['remainingUnresolvedCount']==18
    assert {p['multiply']['address'] for p in report['proofs']}=={'0x1806ba459','0x1806089a7'}
    assert len(report['producerPathsThroughCacheStore'])==10
    assert not report['conditionalRelation']['arbitraryWriterClosureProven']
    assert all(not p['hookApproved'] and not p['allResultUsesProvenAddressOnly'] for p in report['proofs'])
    spec=importlib.util.spec_from_file_location('owner_cache_producer',ROOT/'tools/battle-scale/audit_remaining_agent_owners_v2.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    assert json.loads(json.dumps(module.build()))==report
    # Execute the original complete leaf, independently of abstract owner tags.
    # Target indices remain valid; null/inactive target-AI produces null cache.
    AI,OWNER,TARGET_AI,MISSION,STACK,STOP=0x50000000,0x51000000,0x52000000,0x60000000,0x70000000,0x190000000
    uc=Uc(UC_ARCH_X86,UC_MODE_64)
    uc.mem_map(BASE+0x6bc000,4096);uc.mem_write(BASE+0x6bcab0,pe.get_data(0x6bcab0,0x118))
    for address,size in [(AI,0x3000),(OWNER,0x3000),(TARGET_AI,4096),(MISSION,0x630000),(STACK,0x10000),(STOP,4096)]:uc.mem_map(address,size)
    allowed={int(r['address'],16) for r in report['producerInstructions']};visited=set()
    def code(u,address,size,_):assert address in allowed;visited.add(address)
    def write(u,access,address,size,value,_):
        assert (AI+0xf0<=address and address+size<=AI+0x110) or (address==AI+0x10 and size==4)
    uc.hook_add(UC_HOOK_CODE,code);uc.hook_add(UC_HOOK_MEM_WRITE,write)
    cases=[]
    for selector in (0,1):
        for index in (-1,0,2039):
            for target_state in ('null-ai','inactive-ai','active-ai'):
                uc.mem_write(AI,b'\0'*0x3000);uc.mem_write(OWNER,b'\0'*0x3000)
                uc.mem_write(TARGET_AI,b'\0'*4096)
                uc.mem_write(AI+8,struct.pack('<Q',OWNER))
                uc.mem_write(OWNER+0x2258,bytes([selector]))
                uc.mem_write(OWNER+(0x225c if selector else 0x1504),struct.pack('<i',index))
                uc.mem_write(OWNER+0x14d8,struct.pack('<Q',MISSION))
                target=MISSION+0x2d50+max(index,0)*0xc60
                uc.mem_write(target,b'\0'*0xc60)
                uc.mem_write(target+0x880,struct.pack('<Q',0 if target_state=='null-ai' else TARGET_AI))
                uc.mem_write(TARGET_AI+0x8ce,bytes([int(target_state=='active-ai')]))
                uc.mem_write(target+0x55c,struct.pack('<i',-1))
                expected=target if index>=0 and target_state=='active-ai' else 0
                rsp=STACK+0x8008;uc.mem_write(rsp,struct.pack('<Q',STOP))
                uc.reg_write(UC_X86_REG_RSP,rsp);uc.reg_write(UC_X86_REG_RCX,AI+8)
                uc.emu_start(BASE+0x6bcab0,STOP,count=1000)
                assert uc.reg_read(UC_X86_REG_RIP)==STOP and uc.reg_read(UC_X86_REG_RSP)==rsp+8
                expected_bytes=bytearray(0x3000)
                struct.pack_into('<Q',expected_bytes,8,OWNER)
                struct.pack_into('<I',expected_bytes,0x10,0x200)
                struct.pack_into('<Q',expected_bytes,0xf0,expected)
                assert bytes(uc.mem_read(AI,0x3000))==expected_bytes
                cases.append({'selector':selector,'targetIndex':index,'targetState':target_state,'cacheValue':hex(expected),'wholeAiRecordMatches':True})
    for p,sha in pins.items():assert digest(p)==sha,p
    out={'schemaVersion':1,'status':'accepted-two-conditional-target-cache-owner-contributions',
         'reviewedArtifact':{'path':str(path),'sha256':digest(path)},'reviewerSha256':digest(__file__),
         'dependencies':pins,'originalInstructionRows':len(rows),'producerReplayMatched':True,
         'additionalConcreteMachineCases':cases,'machineInstructionRvasCovered':[hex(a-BASE) for a in sorted(visited)],
         'additionalOwners':2,'remainingOwners':18,'negativeBoundaryCases':len(report['negativeBoundaryChecks']),
         'checks':['Whole saved producer result reproduced without writing frozen output.',
                   'Every retained native instruction matches original bytes and one complete decoded instruction.',
                   'Both target-index selectors and negative/first/last index plus null/inactive/active target AI replayed in original leaf.',
                   'Entire AI fixture matches expected cache and valid-flag writes; no write outside exact cache range allowed.'],
         'openFindings':[],
         'limitations':['Cache provenance remains conditional on exact producer, nonnull live target and no intervening mutation. Cached pointers require lifetime protection through use.',
                        'Concrete cases cover non-mounted target path with mount index-1; other producer paths are statically enumerated only.',
                        'No arbitrary writer closure, native range safety, sidecar publication, all-use routing or hook approval follows.',
                        'No game launched or installed files modified.'],
         'enginePatchApproved':False,'gameLaunched':False}
    destination=ROOT/'evidence/engine-extension/native-remaining-agent-owner-v2-independent-review.json'
    assert not destination.exists()
    destination.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'status':out['status'],'sha256':digest(destination),'machineCases':len(cases),'rows':len(rows)}))

if __name__=='__main__':main()
