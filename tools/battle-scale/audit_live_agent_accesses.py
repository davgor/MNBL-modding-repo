"""Offline G2 access inventory. Candidate detour windows are NOT approved hooks.

Only writes native-live-agent-access-review.json; never loads target code.
"""
from pathlib import Path
from collections import Counter, defaultdict
from datetime import datetime, timezone
import ast, hashlib, json, sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone, pefile
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
from scan_native_pool_references import merge_ranges, subtract_range

SOURCE = ROOT / 'build/battle-scale/native-run-20261017-102915-64ab5481/backup-0'
EXPECTED = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
OUT = ROOT / 'evidence/engine-extension/native-live-agent-access-review.json'
B = ROOT / 'evidence/engine-extension'
FILES = ['native-pool-layout.json', 'native-pool-reference-inventory.json',
         'native-helper-call-provenance.json', 'native-leaf-owner-contracts.json',
         'native-pool-interior-review.json', 'native-packed-index-review.json',
         'pool-managed-expansion-audit.json', 'native-cap-managed-contracts.json',
         'pool-semantic-review-high.json', 'pool-semantic-review-mid.json',
         'pool-semantic-review-low.json', 'native-secondary-pool-role-review.json']

def digest(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def augment_owner_flow(candidates):
    """Run the existing conservative prover's analysis prefix, never its writer.

    Exactly one native read is rebound to our hash-pinned backup. The original
    prover file and all of its proof artifacts remain untouched.
    """
    path=ROOT/'tools/battle-scale/prove_mission_aliases.py'
    source=path.read_text();marker='for root,contract in sorted(selected.items()):'
    assert source.count(marker)==1
    tree=ast.parse(source.split(marker)[0],filename=str(path))
    class ReadOnlySource(ast.NodeTransformer):
        replacements=0
        def visit_Assign(self,node):
            if any(isinstance(x,ast.Name) and x.id=='source' for x in node.targets):
                assert ast.unparse(node.value)=="Path(inv['binary']['path']).read_bytes()"
                node.value=ast.parse('Path('+repr(str(SOURCE))+').read_bytes()',mode='eval').body
                self.replacements+=1
            return self.generic_visit(node)
        def visit_Call(self,node):
            if isinstance(node.func,ast.Attribute):
                assert node.func.attr not in ('write_text','write_bytes','unlink','mkdir','rename'),node.func.attr
                if node.func.attr=='parse_args':node.args=[ast.List(elts=[],ctx=ast.Load())]
            return self.generic_visit(node)
    transform=ReadOnlySource();tree=transform.visit(tree);assert transform.replacements==1
    ns={'__file__':str(path),'__name__':'readonly_live_agent_owner_analysis'}
    exec(compile(ast.fix_missing_locations(tree),str(path),'exec'),ns)
    indexed={int(c['address'],16):c for c in candidates};details=[]
    for root,contract in ns['selected'].items():
        states,report=ns['analysis'][root]
        if states is None:continue
        insns=ns['cache'][root][0]
        # Definitions reaching an established Agent pointer use prove that the
        # multiply contributes to Agent addressing, rather than just sharing c60.
        for address,st in states.items():
            ins=insns[address]
            effective=[]
            for op in ins.operands:
                if op.type==X86_OP_MEM:
                    value=ns['address'](ins,op.mem,st)
                    if value is not None:effective.append(value)
            for v in effective:
                isagent=v[0]=='agent' or v[0]=='mission-index' and 0x2d50<=v[1]<0x39b0
                if not isagent:continue
                for definition in v[2]:
                    candidate=indexed.get(definition)
                    if candidate and 'index-stride-multiply:Agent' in candidate['classes']:
                        evidence={'procedure':hex(root),'definitionRva':hex(definition-ns['base']),'useInstruction':ns['flow'].basic(ins),'effectiveKind':v[0],'effectiveBias':v[1],
                                  'entryContract':contract,'definitionInstructions':[ns['flow'].basic(insns[x]) for x in sorted(v[2]) if x in insns]}
                        if 'ownerFlowProof' not in candidate:
                            candidate['ownerFlowProof']=evidence
                            if candidate['ownerStatus']=='unresolved-owner':candidate['ownerStatus']='fresh-must-flow-Agent-address'
            candidate=indexed.get(address)
            if candidate is None or candidate['ownerStatus']!='unresolved-owner':continue
            poolvalues=[v for v in effective if v[0] in ('mission','mission-index','agent')]
            if poolvalues:
                candidate['ownerFlowProof']={'procedure':hex(root),'entryContract':contract,'effectiveAddresses':[{'kind':v[0],'bias':v[1],'definitionRvas':[hex(x-ns['base']) for x in sorted(v[2])]} for v in poolvalues]}
                candidate['ownerStatus']='fresh-must-flow-owner'
            elif effective and all(v[0]=='stack' for v in effective) and all(k.startswith(('primary-inline','pointer-stride-lea','pool-base-or-metadata-memory')) for k in candidate['classes']):
                candidate['ownerStatus']='fresh-must-flow-stack-exclusion'
                candidate['ownerFlowProof']={'procedure':hex(root),'entryContract':contract,'effectiveAddresses':[{'kind':v[0],'bias':v[1]} for v in effective]}
        details.append({'procedure':hex(root),'status':report['status'],'reachableInstructions':len(states)})
    deps={path: digest(path)}
    for value in ns.values():
        if isinstance(value,Path) and value.is_file() and value.is_relative_to(ROOT):deps[value]=digest(value)
    for key in ('bundle_paths',):
        for value in ns.get(key,[]):deps[value]=digest(value)
    return {'proverPath':str(path),'proverSha256':digest(path),'sourceOverride':str(SOURCE),'sourceOverrideSha256':EXPECTED,
            'writerExecuted':False,'scope':'Original conservative prover prefix through selected contracts; original source read replaced with offline backup; no writer expression executed. Correlates must-proven Agent-address uses with multiply definitions, and exact effective owners at candidate sites.',
            'procedures':details,'blockedEntryContracts':[hex(x) for x in sorted(ns['blocked_entries'])],
            'analysisFailures':[x[1] for x in ns['analysis'].values() if x[0] is None],
            'dependencies':[{'path':str(p),'sha256':h} for p,h in sorted(deps.items())]}

def main():
    raw = SOURCE.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == EXPECTED
    docs = {n: json.loads((B/n).read_text(encoding='utf-8-sig')) for n in FILES}
    pe = pefile.PE(data=raw); base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
    stride = {0xc60: 'Agent', 0x230: 'companion230', 0x2c8: 'companion2c8'}
    layout = docs['native-pool-layout.json']; offsets = defaultdict(list)
    for pool in layout['pools']:
        origin = int(pool['missionOffset'],16)
        for key in ('missionOffset','indexTableOffset','counterOffset','highWaterOffset'):
            value = int(pool[key],16)
            offsets[value].append('mission:'+pool['name']+':'+key)
            if key != 'missionOffset': offsets[value-origin].append('pool:'+pool['name']+':'+key)
    proof = defaultdict(list)
    for name in ('pool-semantic-review-high.json','pool-semantic-review-mid.json','native-pool-interior-review.json','pool-semantic-review-low.json'):
        d = docs[name]
        for row in d.get('instructionSiteMappings', d.get('sites', [])):
            classification = row.get('classification','')
            if any(x in classification for x in ('mission','Mission','pool-relative','exclude')):
                address = int(row['address'],16)
                data = row.get('bytes',row.get('instructionBytes',row.get('rawInstruction',{}).get('bytes')))
                if data:
                    assert pe.get_data(address-base,len(bytes.fromhex(data))) == bytes.fromhex(data)
                    proof[address].append({'source':name,'classification':classification,'bytes':data,
                                           'reason':row.get('reason',row.get('provenanceKind','exact retained owner mapping'))})
    # Explicitly inspected routes; these ranges establish a local pool consumer,
    # not type provenance for every instruction elsewhere in their parent function.
    anchors = [
        ('primary_allocator',0x5d8020,0x5d81d5),
        ('pool_constructor',0x5d8220,0x5d82aa),
        ('pool_destructor',0x5d7df0,0x5d8020),
        ('spawn_and_companions',0x5cd130,0x5cd2b8),
        ('companion_release',0x5d8610,0x5d8729),
        ('agent_link_insert',0x5d91f0,0x5d9350),
        ('companion2c8_link_insert',0x5d9350,0x5d94a0),
        ('companion230_link_insert',0x5d94a0,0x5d9600),
        ('native_lookup_callback',0x70b150,0x70b1e0),
        ('create_result_field_pointers',0x7084a0,0x7085b0),
        ('primary_pointer_iterator',0x59e520,0x59e55b),
        ('agent_self_and_component_owner',0x607550,0x60765a),
        ('constant_capacity_getter',0x6f93b0,0x6f93b6)]
    def row(i): return {'rva':hex(i.address-base),'address':hex(i.address),'bytes':i.bytes.hex(),'mnemonic':i.mnemonic,'operands':i.op_str}
    bodies={name:{'startRva':hex(a),'endExclusiveRva':hex(z),'instructions':[row(i) for i in md.disasm(pe.get_data(a,z-a),base+a)]} for name,a,z in anchors}
    # Reciprocal constants are a deliberately overinclusive arithmetic search,
    # never proof that subtraction operands are pointers to Agent records.
    reciprocals=defaultdict(set)
    exact_division_inverses=defaultdict(set)
    for d in stride:
        odd=d
        while odd%2==0: odd//=2
        for divisor in (d,odd):
            for width in (32,64):
                exact_division_inverses[pow(odd,-1,1<<width)].add(d)
                for shift in range(width):
                    for v in ((1<<(width+shift))//divisor,((1<<(width+shift))+divisor-1)//divisor):
                        if 0x10000<v<(1<<width):reciprocals[v].add(d)
    sections=[(s.VirtualAddress,s.VirtualAddress+min(s.Misc_VirtualSize,s.SizeOfRawData)) for s in pe.sections if s.Characteristics&0x20000000]
    ranges=sorted(set((e.struct.BeginAddress,e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION if any(a<=e.struct.BeginAddress<e.struct.EndAddress<=z for a,z in sections)))
    covered=merge_ranges(ranges)
    todo=[(a,z,'pdata') for a,z in ranges]
    for a,z in sections:todo.extend((x,y,'outside-pdata-linear') for x,y in subtract_range(a,z,covered))
    candidates=[]; gaps=[]; direct_edges=[]; instruction_count=Counter()
    for a,z,kind in todo:
        block=pe.get_data(a,z-a);pos=0;xs=[];recovered=False
        while pos<len(block):
            for i in md.disasm(block[pos:],base+a+pos):
                xs.append((i,recovered));pos=i.address-base-a+i.size
            if pos<len(block):gaps.append({'rva':hex(a+pos),'rangeSource':kind});pos+=1;recovered=True
        for j,(i,after_gap) in enumerate(xs):
            instruction_count[kind]+=1; ops=i.operands; labels=[]; values=[]
            for op in ops:
                if op.type==X86_OP_IMM:
                    v=op.imm;u=v&((1<<(op.size*8))-1)
                    if v in stride and i.mnemonic=='imul':labels.append('index-stride-multiply:'+stride[v])
                    if abs(v) in stride and i.mnemonic in ('add','sub'):labels.append('pointer-stride-step:'+stride[abs(v)])
                    if v in (2039,2040,2041) and i.mnemonic in ('cmp','mov','movabs','push'):labels.append('fixed-capacity-or-sentinel')
                    if v in offsets and i.mnemonic in ('add','sub','mov','movabs'):labels.append('pool-base-or-metadata-address');values+=offsets[v]
                    if u in reciprocals and i.mnemonic in ('mov','movabs','imul'):
                        labels.append('reciprocal-division-candidate');values+=['possible-divisor:'+hex(d) for d in sorted(reciprocals[u])]
                    if u in exact_division_inverses and i.mnemonic in ('mov','movabs','imul'):
                        labels.append('exact-pointer-difference-division-candidate');values+=['possible-divisor:'+hex(d) for d in sorted(exact_division_inverses[u])]
                    if v in stride and i.mnemonic in ('mov','movabs') and any(x.mnemonic in ('idiv','div') for x,_ in xs[j+1:j+13]):
                        labels.append('register-divisor-candidate:'+stride[v])
                if op.type==X86_OP_MEM and op.mem.base!=X86_REG_RIP:
                    v=op.mem.disp
                    if v in offsets:labels.append('pool-base-or-metadata-memory');values+=offsets[v]
                    if 0x2d50<=v<0x39b0:labels.append('primary-inline-field-address')
                    if abs(v) in stride and i.mnemonic=='lea':labels.append('pointer-stride-lea:'+stride[abs(v)])
                    if op.size==2 and any('indexTableOffset' in x for x in offsets.get(v,())):labels.append('signed16-pool-link-or-index')
            # Factorized stride: immediate multiply by odd factor followed by a
            # left shift nearby. Register/data-flow equivalence still unresolved.
            if i.mnemonic=='imul' and any(o.type==X86_OP_IMM and o.imm in (99,35,89) for o in ops):
                near=[x for x,_ in xs[j+1:j+6]]
                if any(x.mnemonic in ('shl','sal') and any(o.type==X86_OP_IMM and o.imm in (3,4,5) for o in x.operands) for x in near):labels.append('factorized-stride-candidate')
            if i.mnemonic in ('call','jmp') or i.group(capstone.CS_GRP_JUMP):
                if ops and ops[0].type==X86_OP_IMM:direct_edges.append((i.address,ops[0].imm,kind,after_gap))
            if not labels:continue
            labels=sorted(set(labels));owner=proof.get(i.address,[])
            local=[name for name,x,y in anchors if x<=i.address-base<y and any(k.startswith(('index-stride','pointer-stride','pool-base','signed16','fixed-capacity','primary-inline')) for k in labels)]
            stack=any(o.type==X86_OP_MEM and i.reg_name(o.mem.base) in ('rsp','rbp') for o in ops)
            excluded=bool(owner) and all('exclude' in x['classification'] for x in owner)
            confidence='explicitly-excluded-owner' if excluded else 'reviewed-local-pool-route' if local else 'retained-exact-owner-proof' if owner else 'unresolved-owner'
            # Minimum 5-byte inline jump window, without approving relocation.
            window=[];size=0
            for x,_ in xs[j:j+8]:
                if window and x.address!=i.address+size:break
                window.append(x);size+=x.size
                if size>=5:break
            reloc=[]
            if size<5:reloc.append('insufficient-contiguous-5-byte-window')
            if any(x.group(capstone.CS_GRP_JUMP) or x.group(capstone.CS_GRP_CALL) or x.group(capstone.CS_GRP_RET) for x in window):reloc.append('control-transfer-in-window')
            if any(o.type==X86_OP_MEM and o.mem.base==X86_REG_RIP for x in window for o in x.operands):reloc.append('RIP-relative-relocation')
            if after_gap or kind!='pdata':reloc.append('instruction-boundary-needs-corroboration')
            candidates.append({**row(i),'rangeSource':kind,'rangeStartRva':hex(a),'rangeEndExclusiveRva':hex(z),'afterDecodeGap':after_gap,'classes':labels,'matchingRoles':sorted(set(values)),
                'ownerStatus':confidence,'ownerProof':owner,'localReviewedRoutes':local,'stackOrFrameRegisterPresent':stack,
                'contextBefore':[row(x) for x,_ in xs[max(0,j-4):j]],'contextAfter':[row(x) for x,_ in xs[j+1:j+7]],
                'hookWindow':{'startRva':hex(i.address-base),'length':size,'bytes':b''.join(x.bytes for x in window).hex(),'instructions':[row(x) for x in window],'relocationClasses':reloc,'approved':False}})
    incoming=defaultdict(list)
    for source,target,kind,gap in direct_edges:incoming[target].append({'sourceRva':hex(source-base),'rangeSource':kind,'afterDecodeGap':gap})
    for c in candidates:
        start=int(c['address'],16);n=c['hookWindow']['length']
        c['hookWindow']['knownDirectEdgesIntoInterior']=[{'targetRva':hex(target-base),'sources':incoming[target]} for target in range(start+1,start+n) if target in incoming]
        if c['hookWindow']['knownDirectEdgesIntoInterior']:c['hookWindow']['relocationClasses'].append('known-direct-edge-enters-overwritten-interior')
    owner_flow=augment_owner_flow(candidates)
    for c in candidates:
        if any(k.startswith('index-stride-multiply') for k in c['classes']):
            c['allResultUsesProvenAddressOnly']=False
            c['offsetSubstitutionApproved']=False
    # Candidate counts must not become assertions of owner closure.
    classes=Counter(k for c in candidates for k in c['classes']);owner_counts=Counter(c['ownerStatus'] for c in candidates)
    byclass={k:{'count':sum(k in c['classes'] for c in candidates),'unresolvedOwnerCount':sum(k in c['classes'] and c['ownerStatus']=='unresolved-owner' for c in candidates),
                 'rvas':[c['rva'] for c in candidates if k in c['classes']]} for k in sorted(classes)}
    managed=docs['pool-managed-expansion-audit.json']
    patterns=('Agent::.ctor','Agent::Clear','Agent::get_Index','AgentHelper::GetAgent','FindAgentWithIndexAux','get_MaxNumberOf','CompressionMission::.cctor','CompressionInfo/Integer::.ctor')
    methods=[x for x in managed['methods'] if any(p in x['signature'] for p in patterns)]
    findings=[
      {'id':'G2-primary-addresses','severity':'blocking','claim':'Primary index lookup is inlined affine arithmetic, not a single function to replace. 70b17f multiplies index by0xc60;70b186 reads managed handle at Mission+0x2d68.70850a and5cd1b8 independently recreate Agent addresses. Old relocation manifests omit many primary operands because their offsets did not move.','evidenceRvas':['0x70b17f','0x70b186','0x70850a','0x5cd1b8']},
      {'id':'G2-metadata-is-not-pointer-table','severity':'blocking','claim':'Pool+0x629d00 holds two-byte successor/index bookkeeping, not Agent pointers. Allocator writes the object index at Agent+0x10 as32bits, reads link entries with MOVSX, and writes capacity+1 terminal sentinels. Replacing only that table with pointers will not redirect the affine consumers.','evidenceRvas':['0x5d807e','0x5d812f','0x5d8149','0x5d814e']},
      {'id':'G2-old-layout-collision','severity':'blocking','claim':'Slot2040 starts exactly at primary metadata Mission+0x62ca50. Appending an allocation or reserving addresses after a live original Mission cannot create a contiguous extension there without displacing live bookkeeping and later objects. Segments can preserve original Agent addresses only after redirecting all resolver/iterator/metadata consumers.','arithmetic':{'slot2040':hex(0x2d50+2040*0xc60),'originalIndexTable':'0x62ca50'}},
      {'id':'G2-raw-pointer-lifetime','severity':'blocking','claim':'Managed Agent retains13 native field pointers; retained CIL finds stores only at construction/Clear. Native constructor also stores Agent self/owner/component pointers and spawn stores companion pointer at Agent+0x20. Existing raw addresses must stay stable; rewriting managed Index alone is insufficient.','evidenceRvas':['0x5cd28d','0x60758c','0x607636','0x607644','0x60764b']},
      {'id':'G2-iterators','severity':'blocking','claim':'Primary iterator59e54d advances a raw pointer by0xc60. Companion release5d86fa walks backwards by0x2c8. Segments need segment-aware iteration, including reverse scans, destructors, jobs and exception cleanup, not just index lookup replacement.','evidenceRvas':['0x59e54d','0x5d86fa','0x5d7e14','0x5d8253']},
      {'id':'G2-signed-links','severity':'blocking','claim':'Unchanged signed16 metadata and capacity+1 sentinel conservatively require capacity<=32766. First live2040->4080 must rewrite terminal2041 entries before that value becomes a valid index, or replace the link representation/algorithm. Genuine unbounded-by-short segmented storage requires widening and rewriting all signed loads, stores, comparisons and helper contracts; Agent.Index remainsInt32.','evidenceRvas':['0x5d812f','0x5d8149','0x5d81a6','0x5d8690','0x5d86a6']},
      {'id':'G2-companions','severity':'blocking','claim':'Spawn indexes all three companions with the same index and retains an Agent-to-companion pointer. They require coordinated stable growth and publication. The second0xc60 pool is lifecycle-observed with unknown activation semantics; it cannot be repurposed as overflow storage merely because its sampled count is zero.','evidenceRvas':['0x5cd1d9','0x5cd1e1','0x5cd222','0x5cd22a','0x5cd25f','0x5cd28d']},
      {'id':'G2-managed-and-wire','severity':'blocking','claim':'Native capacity getter is constant; managed spawn logic caches capacity and derives troop quota/2. Runtime growth requires a live admission/quota contract. Managed AgentList is expandable. Agent.Index isInt32; wire/replay descriptor is11bits(-1..2046), incompatible with4080 IDs. Single0 remains required until coordinated protocol changes.','evidenceRvas':['0x6f93b0']},
      {'id':'G2-owner-closure','severity':'blocking','claim':'Fresh numeric access classes include unrelated stack, image and object fields. Exact unresolved owner count is retained below. No claim that all dynamic/index arithmetic, indirect consumers, pointer subtraction/division, external modules or interior entrypoints are closed.'},
      {'id':'G2-publication','severity':'blocking','claim':'No proven live growth barrier yet. Constructors, linked metadata, companion state, job partitions, cached capacity and renderer requirements must be prepared before one synchronized capacity publication; failures must leave old storage, old IDs, old handles and capacity unchanged. Deletion/reuse/teardown must reject stale generation handles and destroy only constructed records once. This is a design obligation, not an observed existing engine mechanism.'}]
    approaches=[
      {'kind':'stable-segmented-blocks','fitsFinalRemoval':True,'preserved':'Existing Agent/field/companion pointers stay valid for each record lifetime; old blocks remain pinned until references retire.','required':'Introduce sidecar resolver and iteration contracts; rewrite every proven affine access, forward/backward stride scan, metadata algorithm and inverse pointer/index consumer. Widen link bookkeeping and add stable logical IDs plus generation handling. New blocks and all companions publish together under a proved barrier.','firstGrowth':'Old primary block can remain in place; new indices resolve to external blocks. Original inline metadata must be redirected or shadowed consistently, including all successor/sentinel consumers.','laterGrowth':'No fixed virtual reservation ceiling is needed, but existing Int32 indices, signed16 tables, packed IDs, allocation sizes, renderer/job limits and resource budgets remain distinct finite contracts until replaced.64bit sidecar handles alone do not widen engine ABIs.'},
      {'kind':'reserved-contiguous-address-space','fitsFinalRemoval':False,'preserved':'Affine addresses can remain stable within a reservation made before first construction.','required':'Reserve final regions before Mission construction and move metadata/tail layout outside potential growth spans before pointers escape. Separate reserved, committed, constructed and published capacity; adapt lifecycle and caches.','firstGrowth':'Cannot extend original live primary layout into its existing metadata. A newly constructed redesigned Mission can commit/construct additional records in its pre-reserved region.','laterGrowth':'Finite reservation budget is a ceiling. It is a useful implementation comparison or bounded prototype, not removal of the architectural limit.'}]
    lookup=next(c for c in candidates if c['rva']=='0x70b17f')
    assert lookup['bytes']=='4969c0600c0000' and lookup['hookWindow']['length']==7
    arithmetic_cases=[]
    for mission,agent in [(0x30591270000,0x30591272d50),(0x30591270000,0x30591272d50+2039*0xc60),
                          (0x30591270000,0x40800000000),(0x40800000000,0x30500000000)]:
        offset=(agent-(mission+0x2d50))&((1<<64)-1)
        actual=(offset+mission+0x2d68)&((1<<64)-1)
        assert actual==agent+0x18
        arithmetic_cases.append({'mission':hex(mission),'resolvedAgent':hex(agent),'returnedOffset':hex(offset),'effectiveHandleAddress':hex(actual)})
    lookup_case={'status':'bounded-wrapper-rewrite-study-not-approved','procedureRva':'0x70b150',
      'siteRva':'0x70b17f','continuationRva':'0x70b186','originalBytes':'4969c0600c0000','originalInstruction':'imul rax,r8,0xc60',
      'entryValues':{'rcx':'Mission pointer; live through the handle load at70b186','r8':'sign-extended Int32 index after negative, high-water and signed-link checks','rsp':'Original wrapper has PUSH RBX; SUB RSP,0x30. RSP is16-byte aligned at the site under Windows x64 ABI. Existing caller home area occupies RSP+0..1f; exception-state local atRSP+20 must remain intact.'},
      'proposedValue':'RAX = (resolvedAgent - (Mission + 0x2d50)) modulo2^64; keep all64bits. Then70b186 reads the unchanged managed reverse handle at resolvedAgent+0x18.',
      'allOffsetUses':[{'rva':'0x70b186','bytes':'8b9c08682d0000','instruction':'mov ebx,dword ptr [rax+rcx+0x2d68]','role':'sole use of the product before overwrite'}],
      'deadResultProof':'After70b186, RAX is not read as the product. Paths through70b1a1 overwrite it with a callback function pointer; paths skipping that block overwrite EAX at70b1b0. No branch returns to the product use in this wrapper.',
      'flags':'IMUL flags are not consumed before TEST EBX,EBX at70b198 overwrites arithmetic flags. Preserve flags conservatively in a general hook; this local observation is not a whole-program liveness theorem.',
      'registerPolicy':'Preserve RCX and all non-result registers/XMM state unless independently proven dead. A C++ helper call may clobber RCX/RDX/R8/R9/R10/R11 and XMM0..5; direct insertion without a preserving stub is invalid.',
      'window':lookup['hookWindow'],
      'exceptions':['This hook alone is insufficient:70b169 high-water and70b174 signed16 table still refer to original inline metadata. Redirect them to the same growth descriptor or replace the complete wrapper contract.','Only7 bytes are available. A5-byte relative jump/call requires a reachable near trampoline; an absolute14-byte jump does not fit. No executable patch is generated.','A resolver failure cannot return a null-derived offset and continue to dereference+18. It must follow a reviewed zero-handle rejection path with EAX=0 and intact original stack/lifecycle state.','No known decoded direct branch enters the stolen7-byte interior; arbitrary indirect entries, code pointers and external modules are not universally excluded.','Trampoline stack and Windows unwind/exception behavior need explicit implementation and validation. Avoid throwing across an unregistered stub.','Do not generalize this offset substitution to a site with mixed owners, companion bases, integer uses of the product, truncation, pointer subtraction or multiple differently based uses.','Metadata publication, index-generation domain and block retirement must remain coherent while lookups run; this wrapper provides no growth barrier.'],
      'arithmeticTests':arithmetic_cases,'approved':False}
    result={'schemaVersion':1,'status':'bounded-G2-live-access-inventory-growth-blocked','reviewedAtUtc':datetime.now(timezone.utc).isoformat(),'nativeSourcePath':str(SOURCE),'nativeSourceSha256':EXPECTED,'scriptSha256':digest(__file__),
      'gameExecuted':False,'installedFilesReadOrModified':False,'patchApproved':False,
      'sources':[{'path':str(B/n),'sha256':digest(B/n)} for n in FILES]+[{'path':str(ROOT/'tools/battle-scale/scan_native_pool_references.py'),'sha256':digest(ROOT/'tools/battle-scale/scan_native_pool_references.py')}],
      'scanCoverage':{'runtimeRanges':len(ranges),'outsideRuntimeRanges':len(todo)-len(ranges),'instructionCounts':dict(instruction_count),'decodeGapBytes':gaps,'numericCandidateCount':len(candidates),'ownerStatusCounts':dict(owner_counts),'classes':byclass},
      'hookWindowSummary':dict(Counter(k for c in candidates for k in (c['hookWindow']['relocationClasses'] or ['locally-straight-line-still-needs-ABI-unwind-review']))),
      'hookRequirements':['Owner proof and input/output register/flag/liveness contract for each site.','Decode full overwritten window and relocate RIP-relative operands/control transfers.','Prove incoming branches and interior entrypoints cannot land in stolen bytes; listed direct edges are not universal closure.','Preserve Windows x64 unwind/exception metadata and instruction boundaries; existing pdata fragments can split functions.','Atomically install hooks before unsafe consumers run; no game code is modified by this inventory.'],
      'candidates':candidates,'freshOwnerFlow':owner_flow,'exactAnchorBodies':bodies,'findings':findings,'architectureComparison':approaches,'lookupOffsetSubstitutionCase':lookup_case,
      'managedEvidence':{'sourceScope':managed['scope'],'scannedMethodBodies':managed['scannedMethodBodies'],'findings':managed['findings'],'methods':methods,'nativePointerFieldConsumers':managed['nativePointerFieldConsumers'],'capacityCacheConsumers':managed['capacityCacheConsumers'],'creationResultFields':docs['native-cap-managed-contracts.json']['agentCreationResultFields']},
      'packedIndexReview':{'source':'native-packed-index-review.json','candidateCount':docs['native-packed-index-review.json']['candidateCount'],'review':docs['native-packed-index-review.json']['review'],'limitations':docs['native-packed-index-review.json']['limitations']},
      'growthAcceptanceTests':['Record every existing Agent and13 field pointers, companion pointer, managed reverse handle and logical ID before growth; assert identity unchanged after.','Grow2040->4080 while preserving live originals; check indices2039,2040,2041,4079, rejection4080 and all terminal-link rewrites.','Inject failure at each block/companion/constructor phase; verify old-capacity operation and cleanup without partially published indices.','Cross every segment boundary with forward/reverse iteration, forced lookup/spawn, rider+horse admission, death/delete/reuse, jobs, renderer and mission teardown.','Test signed-link boundary32766->32767 only after widening contracts; test generation rollover/stale handle rejection, arithmetic overflow and Int32/packed-wire constraints separately.','Teardown after both successful and aborted growth; require exact once-only destruction for constructed records and no target pointers into freed blocks.'],
      'limitations':['Numeric candidates are not automatic rewrite authority. Exact owner-status counts deliberately preserve unresolved sites and known exclusions.','Full file-backed executable sections are covered by pdata anchored decoding plus outside-pdata linear decoding. Data islands, resynchronization, computed code and instruction overlap remain explicit limits.','Reciprocal/factorized arithmetic search is overinclusive and incomplete; it does not prove pointer-to-index semantics without data-flow ownership.','Retained managed CIL and REA evidence is reused with source hashes; no current installed assembly is read or executed.','This identifies concrete growth barriers and an actionable review queue, not a closed complete engine-consumer proof or test-ready live-growth patch.']}
    OUT.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'output':str(OUT),'sha256':digest(OUT),'candidates':len(candidates),'owners':dict(owner_counts),'classes':dict(classes),'gaps':len(gaps)}))

if __name__=='__main__':main()
