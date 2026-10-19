"""Independent byte/dependency review and extra default-lifetime replay inputs."""
from pathlib import Path
import ast,hashlib,importlib.util,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path[:0]=[str(ROOT/'tools/battle-scale'),str(ROOT/'build/battle-scale/python-libs')]
import pefile,capstone
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
path=ROOT/'evidence/engine-extension/native-unpublished-element-runtime-contract.json'
assert digest(path)=='036733361062f0aa087b8f7321c0132aceaa85cb0ee244bb27fa984b9a4e5660'
report=json.loads(path.read_text());pins=dict(report['sources']);pins[str(path)]=digest(path);pins[str(Path(__file__))]=digest(__file__)
for p,h in pins.items():assert digest(p)==h,p
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
assert digest(source)==report['nativeSourceSha256'];pe=pefile.PE(str(source));base=pe.OPTIONAL_HEADER.ImageBase
md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
retained=0
for body in report['nativeBodies'].values():
    a=int(body['start'],16);z=int(body['endExclusive'],16);raw=bytes.fromhex(body['bytes'])
    assert len(raw)==z-a and pe.get_data(a-base,z-a)==raw
    decoded=list(md.disasm(raw,a));assert sum(i.size for i in decoded)==len(raw)
    assert [(hex(i.address),i.bytes.hex(),i.mnemonic,i.op_str) for i in decoded]==[(r['address'],r['bytes'],r['mnemonic'],r['operands']) for r in body['instructions']]
    retained+=len(decoded)
assert retained==784
assert report['summary']['replayCases']==24 and len(report['cases'])==24
assert all(c['passed'] and not c['outOfObjectWrites'] and not c['uninitializedObjectReads'] for c in report['cases'])
assert pe.DIRECTORY_ENTRY_LOAD_CONFIG.struct.GuardCFDispatchFunctionPointer==base+0xa2e4e8
assert pe.get_data(0x9e3660,2)==b'\xff\xe0'
mapped=pe.get_memory_mapped_image()
for row in report['imageReadRanges']:
    a=int(row['rva'],16);z=int(row['endExclusiveRva'],16)
    assert mapped[a:z].hex()==row['originalMappedBytes'] and pe.get_data(a,z-a).hex()==row['diskBytes']
assert not report['enginePatchApproved'] and not report['gameLaunched']
script=ROOT/'tools/battle-scale/audit_native_unpublished_element_runtime.py'
spec=importlib.util.spec_from_file_location('unpublished_contract_replay',script);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
assert json.loads(json.dumps(module.build()))==report
# Execute the same byte-bound path checks with independent poison/sentinel
# values. AST modifications are explicit review inputs, never source edits.
tree=ast.parse(script.read_text());changed=[]
class MoreInputs(ast.NodeTransformer):
    def visit_For(self,node):
        self.generic_visit(node)
        if isinstance(node.target,ast.Name) and node.target.id in ('poison','sentinel'):
            key=node.target.id
            values=(0x13,0x7f,0x80) if key=='poison' else (0x0101010101010101,0xffffffffffffffff,0x8000000000000000)
            node.iter=ast.Tuple(elts=[ast.Constant(v) for v in values],ctx=ast.Load());changed.append(key)
        return node
tree=ast.fix_missing_locations(MoreInputs().visit(tree));assert sorted(changed)==['poison','sentinel']
namespace={'__name__':'independent_default_inputs','__file__':str(script)}
exec(compile(tree,str(script),'exec'),namespace)
extra=namespace['build']();assert extra['summary']['replayCases']==27
assert all(c['passed'] and not c['outOfObjectWrites'] and not c['uninitializedObjectReads'] for c in extra['cases'])
for p,h in pins.items():assert digest(p)==h,p
result=dict(schemaVersion=1,status='accepted-bounded-default-unpublished-lifetime-contract',sources=pins,
    originalBodies=14,instructionRows=retained,retainedCases=24,additionalCases=27,
    extraInputPatterns=dict(poison=[0x13,0x7f,0x80],sentinel=['0x0101010101010101','0xffffffffffffffff','0x8000000000000000']),
    extraSummary=extra['summary'],
    checks=['All14 retained bodies,784 decoded instruction rows and read ranges independently match the exact original image.',
            'Full producer replay matches the saved result without its writer;27 additional poisoned/sentinel paths preserve write/read-initialization/ABI gates.',
            'Guard dispatch slot and original JMP RAX independently checked against PE load config.',
            'All retained and extra cases destroy only fully constructed never-activated private objects.'],
    limitations=['atan2f(-0,1) remains an explicit emulation model; no actual CRT or Windows loader execution is claimed.',
                 'Evidence is bounded to default fully completed objects with no activation, external publication or mutations beyond inactive headers.',
                 'No exception injection inside native constructors, live pool publication, game worker barrier or native patch approval.',
                 'Mapped arbitrary vtable addresses are stored by construction; the default path does not invoke their non-null resource methods.'],
    gameLaunched=False,enginePatchApproved=False)
dest=ROOT/'evidence/engine-extension/native-unpublished-element-contract-independent-review.json'
dest.write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(dict(status=result['status'],sha256=digest(dest),additionalCases=27)))
