"""Independent original-byte/import/CFG checks and read-only proof replay."""
from pathlib import Path
import hashlib,importlib.util,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import pefile,capstone
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
path=ROOT/'evidence/engine-extension/native-agent-wrapper-external-tail-proof.json'
assert digest(path)=='34957deb6b4e790cc2e62a68b1d12a13cf9e6313ad971dd847692e6563e7c5bf'
report=json.loads(path.read_text());pins={r['path']:r['sha256'] for r in report['dependencies']}
pins[str(path)]=digest(path);pins[str(Path(__file__))]=digest(__file__)
for p,h in pins.items():assert digest(p)==h,p
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
assert digest(source)==report['binarySha256'];pe=pefile.PE(str(source))
base=pe.OPTIONAL_HEADER.ImageBase;md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
start=int(report['bodyStart'],16);end=int(report['bodyEndExclusive'],16)
raw=pe.get_data(start-base,end-start);assert hashlib.sha256(raw).hexdigest()==report['bodySha256']
instructions=list(md.disasm(raw,start));assert sum(i.size for i in instructions)==len(raw)
decoded={i.address:i for i in instructions};tail=decoded[0x1806df4f2]
assert tail.mnemonic=='jmp' and tail.operands[0].type==capstone.x86.X86_OP_MEM
mem=tail.operands[0].mem;assert tail.reg_name(mem.base)=='rip' and mem.index==0
slot=tail.address+tail.size+mem.disp
imports=[(d.dll.decode(),i.name.decode()) for d in pe.DIRECTORY_ENTRY_IMPORT for i in d.imports if i.address==slot]
assert imports==[('MSVCP140.dll','?_Throw_C_error@std@@YAXH@Z')]
assert decoded[tail.address-4].mnemonic=='add' and decoded[tail.address-4].op_str=='rsp, 0x28'
edges={}
for a,i in decoded.items():
    if a==tail.address or i.group(capstone.CS_GRP_RET):successors=[]
    elif i.group(capstone.CS_GRP_JUMP):
        assert i.operands[0].type==capstone.x86.X86_OP_IMM
        successors=[i.operands[0].imm]+([] if i.mnemonic=='jmp' else [a+i.size])
    else:successors=[a+i.size]
    assert all(s in decoded for s in successors)
    edges[hex(a)]=[hex(s) for s in successors]
assert edges==report['localSuccessors'] and report['changedEdgeSources']==[hex(tail.address)]
assert report['remainingUnresolvedCount']==20 and report['additionalProven']==1
assert report['proofs'][0]['multiply']['address']=='0x1806df3a4'
assert not report['enginePatchApproved'] and not report['gameLaunched']
assert len(report['negativeBoundaryChecks'])==10 and all(c['passed'] for c in report['negativeBoundaryChecks'])
for row in report['unwindRecords']:
    assert pe.get_data(int(row['unwindRva'],16),len(bytes.fromhex(row['bytes']))).hex()==row['bytes']
spec=importlib.util.spec_from_file_location('external_tail_replay',ROOT/'tools/battle-scale/audit_agent_wrapper_external_tail.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
assert json.loads(json.dumps(module.build()))==report
for p,h in pins.items():assert digest(p)==h,p
result=dict(schemaVersion=1,status='accepted-conditional-import-tail-owner-contribution',sources=pins,
    instructions=len(instructions),negativeCases=10,remainingOwners=20,
    checks=['Exact imported external tail independently decoded and resolved through PE imports.',
            'Every ordinary local CFG edge independently reconstructed; full producer replay matched without writing its artifact.',
            'Original body and three unwind records matched byte-for-byte; ten exact negative/boundary checks retained.'],
    limitations=report['limitations'],gameLaunched=False,enginePatchApproved=False)
dest=ROOT/'evidence/engine-extension/native-agent-wrapper-tail-independent-review.json'
dest.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(dict(status=result['status'],sha256=digest(dest))))
