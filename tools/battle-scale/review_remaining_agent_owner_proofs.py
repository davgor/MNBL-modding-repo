"""Read-only replay and original-byte review of three conditional owner paths."""
from pathlib import Path
import hashlib, importlib.util, json, sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import pefile,capstone
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
artifact=ROOT/'evidence/engine-extension/native-remaining-agent-owner-proofs.json'
script=ROOT/'tools/battle-scale/audit_remaining_agent_owners.py'
assert digest(artifact)=='95f9bd0e0c16fbe8da7b4bb79dace261441bef226e39d882d9b8cdcb9604c083'
assert digest(script)=='07bebb4a3ab443ffd82b91c05283c13531cd4fd23e3cbee93514c36962091f00'
report=json.loads(artifact.read_text())
pins={d['path']:d['sha256'] for d in report['dependencies']}
pins[str(artifact)]=digest(artifact);pins[str(Path(__file__))]=digest(__file__)
for p,h in pins.items():assert digest(p)==h,p
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
assert digest(source)==report['binarySha256']
pe=pefile.PE(str(source));md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
rows=set()
def check(v):
    if isinstance(v,dict):
        if 'address' in v and 'bytes' in v:
            a=int(v['address'],16);raw=bytes.fromhex(v['bytes'])
            # Inert negative fixtures use a different schema and are not code rows.
            assert pe.get_data(a-0x180000000,len(raw))==raw
            i=list(md.disasm(raw,a));assert len(i)==1 and i[0].size==len(raw)
            rows.add((a,raw))
        for item in v.values():check(item)
    elif isinstance(v,list):
        for item in v:check(item)
check(report)
assert report['additionalProven']==3 and report['remainingUnresolvedCount']==21
assert {p['multiply']['address'] for p in report['proofs']}=={'0x1805e719e','0x1805f6f3a','0x18068b905'}
assert len(report['negativeBoundaryChecks'])==8 and all(x['passed'] for x in report['negativeBoundaryChecks'])
assert not report['enginePatchApproved'] and not report['gameLaunched']
assert all(not p['hookApproved'] and not p['allResultUsesProvenAddressOnly'] for p in report['proofs'])
assert len(report['constructorFieldRelations'])==2
assert {f['offset'] for f in report['constructorFieldRelations']}=={0x150,0xac0}
# Verify stores, widths, and register identity independently of abstract tags.
for rva,displacement,base,src in [(0x607636,0x150,'rbx','rbx'),(0x60760e,0,'rax','rbx'),(0x607611,0xac0,'rbx','rax')]:
    i=next(md.disasm(pe.get_data(rva,15),0x180000000+rva))
    dst,value=i.operands
    assert i.mnemonic=='mov' and dst.type==capstone.x86.X86_OP_MEM and dst.size==8
    assert i.reg_name(dst.mem.base)==base and dst.mem.disp==displacement and dst.mem.index==0
    assert value.type==capstone.x86.X86_OP_REG and value.size==8 and i.reg_name(value.reg)==src
spec=importlib.util.spec_from_file_location('remaining_owner_replay',script)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
replayed=json.loads(json.dumps(module.build()))
assert replayed==report,'Read-only replay differs'
for p,h in pins.items():assert digest(p)==h,p
receipt=dict(schemaVersion=1,status='accepted-three-conditional-owner-contributions',sources=pins,
    byteRows=len(rows),additionalOwners=3,remainingOwners=21,negativeCases=8,
    checks=['Entire producer build replay matched saved result without invoking its writer.',
            'All retained instruction rows match original bytes and decode to one complete instruction.',
            'Self and attached-wrapper stores independently checked for width/base/source/displacement.',
            'Only exact accepted entry/caller contexts are propagated; frozen canonical proofs remain unchanged.'],
    limitations=['Constructor-established fields are conditional on initialized live objects and intact relations; arbitrary-alias writes, copying, teardown and races are unproved.',
                 'No all-use/address-routing/lifetime/incoming-window approval follows from these owner contributions.',
                 'The attached wrapper relation does not close6df3a4; remaining indirect CFG work is explicit.'],
    enginePatchApproved=False,gameLaunched=False)
path=ROOT/'evidence/engine-extension/native-remaining-agent-owner-independent-review.json'
path.write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(dict(status=receipt['status'],sha256=digest(path),rows=len(rows))))
