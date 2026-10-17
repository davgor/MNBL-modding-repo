"""Independent review of exact registration and conditional virtual contexts."""
from pathlib import Path
import hashlib,importlib.util,json,struct,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import capstone,pefile
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs-v3.json'
assert digest(path)=='4aefbd31a60cee46f14a8fab8170a4bceb5ac68c9c38daf129f4c6c4966a358b'
report=json.loads(path.read_text());pins={d['path']:d['sha256'] for d in report['dependencies']}
for p,sha in pins.items():assert digest(p)==sha,p
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
assert digest(source)==report['binarySha256']
pe=pefile.PE(str(source));md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
rows=set()
def check(value):
    if isinstance(value,dict):
        if 'address' in value and 'bytes' in value:
            address=int(value['address'],16);raw=bytes.fromhex(value['bytes'])
            assert pe.get_data(address-0x180000000,len(raw))==raw
            instructions=list(md.disasm(raw,address));assert len(instructions)==1 and instructions[0].size==len(raw)
            rows.add((address,raw))
        for item in value.values():check(item)
    elif isinstance(value,list):
        for item in value:check(item)
check(report)
assert {p['multiply']['address'] for p in report['proofs']}=={'0x18070e8e3','0x1806536a3','0x1805f771c','0x1805f8bf7'}
assert report['remainingUnresolvedCount']==14 and not report['virtualCall']['receiverDynamicTypeUniversallyProven']
lea=next(md.disasm(pe.get_data(0x646306,7),0x180646306))
assert lea.reg_name(lea.operands[0].reg)=='rdx' and lea.address+lea.size+lea.operands[1].mem.disp==0x18070e860
assert struct.unpack('<Q',pe.get_data(0xb22808+0x190,8))[0]==0x1806535a0
install=next(md.disasm(pe.get_data(0x64ca93,7),0x18064ca93))
assert install.address+install.size+install.operands[1].mem.disp==0x180b22808
metadata=report['managedMetadata']
assert metadata['registration']['callbackId']==625
delegate=next(m for m in metadata['methods'] if '/SendExistingObjectsDelegate::Invoke' in m['name'])
assert delegate['returnType']=='System.Void'
assert [p['type'] for p in delegate['parameters']]==['System.Int32','System.UIntPtr']
assert all(not p['hookApproved'] and not p['allResultUsesProvenAddressOnly'] for p in report['proofs'])
spec=importlib.util.spec_from_file_location('owner_v3_replay',ROOT/'tools/battle-scale/audit_remaining_agent_owners_v3.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
assert json.loads(json.dumps(module.build()))==report
for p,sha in pins.items():assert digest(p)==sha,p
out={'schemaVersion':1,'status':'accepted-four-bounded-registration-and-virtual-owner-contributions',
     'reviewedArtifact':{'path':str(path),'sha256':digest(path)},'reviewerSha256':digest(__file__),
     'sources':pins,'originalInstructionRows':len(rows),'producerReadOnlyReplayMatched':True,
     'additionalOwners':4,'remainingOwners':14,'negativeCases':len(report['negativeBoundaryChecks']),
     'checks':['All native instruction rows match original file and decode as complete instructions.',
               'Native registration LEA independently resolves70e860, callback625 managed switch and void(int32,uintptr) delegate place Mission in RDX.',
               'Original vtable slot190 independently resolves6535a0; constructor LEA resolves same vtable. Dynamic type remains an explicit conditional premise.',
               'Complete producer replay independently rereads managed metadata with Cecil, validates source hashes and reproduces exact caller contexts without mutating accepted contracts.'],
     'openFindings':[],
     'limitations':['Network callback identity does not approve multiplayer or change packed network IDs.',
                    'Virtual contribution applies only to original dynamic vtable. Live Mission/Agent lifetimes and unmodified bindings remain caller obligations.',
                    'Owner contributions do not certify all consumers, bounds, hook windows, barriers or native capacity growth.'],
     'gameLaunched':False,'enginePatchApproved':False}
destination=ROOT/'evidence/engine-extension/native-remaining-agent-owner-v3-independent-review.json'
assert not destination.exists();destination.write_text(json.dumps(out,indent=2)+'\n')
print(json.dumps({'status':out['status'],'sha256':digest(destination),'rows':len(rows)}))
