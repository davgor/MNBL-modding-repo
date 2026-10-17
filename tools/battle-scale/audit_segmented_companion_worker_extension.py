"""Second companion extension: caller-owned stack homes and exact job captures."""
from pathlib import Path
import argparse, ast, hashlib, inspect, json
from collections import Counter, defaultdict, deque
import audit_segmented_companion_extension as previous
from native_instruction_semantics import memory_operand_writes
from patch_native_copy import safe_review_output
ROOT=previous.ROOT

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def build():
    receipt=ROOT/'evidence/engine-extension/native-segmented-companion-extension.json'
    old=json.loads(receipt.read_text())
    for s in old['sources']:assert digest(s['path'])==s['sha256'],s['path']
    # Expose the previous read-only analysis's local state without executing its
    # main/writer or modifying either source or receipt.
    tree=ast.parse(inspect.getsource(previous.build));fn=tree.body[0]
    assert isinstance(fn.body[-1],ast.Return) and isinstance(fn.body[-1].value,ast.Dict)
    fn.body[-1].value=ast.Tuple(elts=[fn.body[-1].value,ast.Name(id='ns',ctx=ast.Load()),ast.Name(id='analyses',ctx=ast.Load())],ctx=ast.Load())
    scope=dict(previous.__dict__);exec(compile(ast.fix_missing_locations(tree),str(Path(previous.__file__)), 'exec'),scope)
    replay,ns,analyses=scope['build']();base=ns['base'];pe=ns['pe']
    import capstone
    from capstone.x86 import X86_OP_MEM,X86_OP_IMM
    md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);md.detail=True
    rows={};deps={Path(s['path']) for s in old['sources']}|{receipt,Path(__file__)}
    def rec(i):
        row=ns['flow'].basic(i);rows[i.address]=row;return row
    def span(a,z):
        body=list(md.disasm(pe.get_data(a-base,z-a),a));assert sum(i.size for i in body)==z-a
        return [rec(i) for i in body]
    candidates={int(r['multiply']['address'],16):r for r in old['candidates'] if r['classification']=='unresolved-owner' and int(r['multiply']['rva'],16)<0x700000}
    assert len(candidates)==11
    spill_path=ROOT/'evidence/engine-extension/native-pool-spill-review.json';deps.add(spill_path)
    spill=json.loads(spill_path.read_text())
    for row in spill['functionInstructions']:
        assert pe.get_data(int(row['address'],16)-base,len(bytes.fromhex(row['bytes']))).hex()==row['bytes']
    root=base+0x6763c0;insns,succ,bad,indirect=ns['decode'](root);assert not bad and not indirect
    pred=defaultdict(set)
    for a,ts in succ.items():
        for t in ts:pred[t].add(a)
    lifetime=set();todo=[base+x for x in (0x6768de,0x6768f2,0x676901,0x676a21)]
    while todo:
        a=todo.pop()
        if a in lifetime:continue
        lifetime.add(a);todo.extend(pred[a])
    stores={0x70:base+0x676497,0x78:base+0x676480,0x160:base+0x67648c}
    effects={int(a,16):e for e in spill['callEffects'] for a in e['calls']}
    writes=[];indexed=[];calls=[]
    for a in sorted(lifetime):
        ins=insns[a];rec(ins)
        if a<base+0x676479:continue
        if ins.mnemonic=='call':
            assert a in effects,hex(a)
            e=effects[a];calls.append({'instruction':rec(ins),'effect':e})
            for lo,length in e['outputs']:
                assert all(not(lo<h+8 and h<lo+length) for h in stores),(hex(a),lo,length)
        for ix,op in enumerate(ins.operands):
            if op.type!=X86_OP_MEM or ins.reg_name(op.mem.base) not in ('rbp','rsp'):continue
            offset=op.mem.disp+(0x100 if ins.reg_name(op.mem.base)=='rbp' else 0)
            if ins.mnemonic=='lea':assert all(not(h<=offset<h+8) for h in stores),hex(a)
            if not memory_operand_writes(ins,ix):continue
            if op.mem.index:
                assert a in (base+0x6766b4,base+0x6766bc,base+0x6766d0),hex(a)
                indexed.append(rec(ins));continue
            writes.append({'instruction':rec(ins),'offset':offset,'length':op.size})
            for home,store in stores.items():assert not(offset<home+8 and home<offset+op.size) or a==store,hex(a)
    assert len(indexed)==3
    # These are a bounded contact array: EDI is initialized/reset to zero,
    # incremented only while<64, and address=RBP+3e0+EDI*48.
    contact=span(base+0x67667a,base+0x6766e6)
    assert insns[base+0x67668b].op_str=='edi, 0x40' and insns[base+0x67668e].mnemonic=='jge'
    assert insns[base+0x6766aa].op_str=='rax, edi' and insns[base+0x6766ad].op_str=='rcx, [rax + rax*2]' and insns[base+0x6766b1].op_str=='rcx, rcx'
    indexed_range=(0x4e0,0x4e0+63*48+48)
    assert indexed_range==(0x4e0,0x10e0)
    # Contact index induction: exactly three writes to the saved counter;
    # the two resets store known zero and the sole increment follows <64.
    counter_writes=[]
    for a in sorted(lifetime):
        i=insns[a]
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM and i.reg_name(op.mem.base)=='rbp' and op.mem.disp==0xfe0 and memory_operand_writes(i,ix):counter_writes.append(a)
    assert counter_writes==[base+0x676560,base+0x6766e0,base+0x6767b1]
    counter_rows=[rec(insns[base+a]) for a in (0x676535,0x67655d,0x676560,0x67668b,0x67668e,0x6766d8,0x6766de,0x6766e0,0x676790,0x6767b1)]
    for e in effects.values():
        for lo,length in e['outputs']:assert not(lo<0x10e4 and 0x10e0<lo+length)
    transfer=ns['transfer']
    def preserve_private_homes(ins,st):
        out=transfer(ins,st)
        if ins.address in lifetime and ins.address >= base+0x676479:
            for home in stores:
                key=('slot',-0x6de8+home)
                if key in st and ins.address!=stores[home]:out[key]=st[key]
        return out
    ns['transfer']=preserve_private_homes
    new_analyses=[]
    for proc,contract,states in analyses:
        if proc==root:
            states,report=ns['analyze'](proc,contract)
            if states is not None:
                for reset in (0x676560,0x6767b1):
                    assert states[base+reset].get('r10',())[:2]==('constant',0),hex(reset)
                new_analyses.append((proc,contract,states))
    analyses+=new_analyses
    # Exact closure layouts, not a generic rule for arbitrary std::function.
    growth=ROOT/'evidence/engine-extension/native-live-growth-rea-evidence-2.json';deps.add(growth)
    growth_data=json.loads(growth.read_text())
    bounded_record=next(r for r in growth_data['records'] if r.get('parameters',{}).get('procedure')=='0x180690170')
    assert bounded_record['subject']['digest']['sha256']==previous.initial.SHA
    capture_bodies={}
    for start,end in ((0x68cf30,0x68d7f8),(0x68fc50,0x68fcf9),(0x68fd00,0x68fdde),(0x68fb00,0x68fb6f),(0x68fb70,0x68fc50),(0x68cc80,0x68ce0b),(0x690170,0x690451)):
        capture_bodies[hex(base+start)]=span(base+start,base+end)
    def exact(a,mnemonic,operands):
        i=next(md.disasm(pe.get_data(a,16),base+a));assert i.mnemonic==mnemonic and i.op_str==operands,(hex(a),i.op_str);return rec(i)
    capture_fields=[exact(*x) for x in [
        (0x68cf33,'mov','qword ptr [rax + 0x18], r8'),(0x68cf47,'lea','rbp, [rax - 0x58]'),
        (0x68cf70,'mov','rbx, r9'),(0x68d07a,'mov','qword ptr [rsp + 0x70], rbx'),
        (0x68d07f,'mov','rax, qword ptr [rbp + 0x80]'),(0x68d086,'mov','qword ptr [rsp + 0x78], rax'),
        (0x68d08b,'lea','rax, [rbp + 0x88]'),(0x68d092,'mov','qword ptr [rbp - 0x80], rax'),
        (0x68d071,'lea','rax, [rbp + 0x70]'),(0x68d075,'mov','qword ptr [rsp + 0x68], rax'),
        (0x68d6dd,'mov','rax, qword ptr [rbp + 0x80]'),(0x68d6e4,'mov','qword ptr [rsp + 0x70], rax'),
        (0x68d6e9,'lea','rax, [rbp + 0x88]'),(0x68d6f0,'mov','qword ptr [rsp + 0x78], rax')]]
    # Saved homes are outside the caller's outgoing argument area. Full body
    # retains every use; the only address materializations enter the two exact
    # captures, whose worker loads dereference those homes read-only.
    capture_home_uses=[]
    for row in capture_bodies[hex(base+0x68cf30)]:
        a=int(row['address'],16);i=next(md.disasm(bytes.fromhex(row['bytes']),a))
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM and i.reg_name(op.mem.base)=='rbp' and op.mem.disp in (0x70,0x80,0x88):
                assert not memory_operand_writes(i,ix)
                capture_home_uses.append(row)
    assert len(capture_home_uses)==9
    # Pin the field loads and copies that establish callee argument geometry.
    for a,m,o in [
      (0x68fc80,'mov','rax, qword ptr [r14 + 0x28]'),(0x68fc8c,'mov','r11, qword ptr [r14 + 0x20]'),
      (0x68fc90,'mov','rbx, qword ptr [r14 + 0x18]'),(0x68fc94,'mov','r10, qword ptr [rax]'),
      (0x68fca5,'mov','qword ptr [rsp + 0x38], r10'),(0x68fcbd,'mov','qword ptr [rsp + 0x30], r11'),
      (0x68fcc9,'mov','qword ptr [rsp + 0x28], rbx'),(0x68fcd6,'call','0x18068d800'),
      (0x68fb30,'mov','rax, qword ptr [rbx + 0x20]'),(0x68fb3c,'mov','rax, qword ptr [rax]'),
      (0x68fb45,'mov','qword ptr [rsp + 0x28], rax'),(0x68fb4a,'mov','rax, qword ptr [rbx + 0x18]'),
      (0x68fb4e,'mov','qword ptr [rsp + 0x20], rax'),(0x68fb53,'call','0x18068cc80'),
      (0x68fd7d,'movups','xmm0, xmmword ptr [rcx]'),(0x68fd80,'movups','xmmword ptr [rax], xmm0'),
      (0x68fd83,'movups','xmm1, xmmword ptr [rcx + 0x10]'),(0x68fd87,'movups','xmmword ptr [rax + 0x10], xmm1'),
      (0x68fd8b,'movups','xmm0, xmmword ptr [rcx + 0x20]'),(0x68fd8f,'movups','xmmword ptr [rax + 0x20], xmm0'),
      (0x68fbed,'movups','xmm0, xmmword ptr [rcx]'),(0x68fbf0,'movups','xmmword ptr [rax], xmm0'),
      (0x68fbf3,'movups','xmm1, xmmword ptr [rcx + 0x10]'),(0x68fbf7,'movups','xmmword ptr [rax + 0x10], xmm1'),
      (0x68fbfb,'movsd','xmm0, qword ptr [rcx + 0x20]'),(0x68fc00,'movsd','qword ptr [rax + 0x20], xmm0')]:capture_fields.append(exact(a,m,o))
    # Read-only home ABI of 690170 is exact: only R8/R9 output dwords; all
    # remaining stores target its own frame, and only cookie-check call exists.
    bounded_body=list(md.disasm(pe.get_data(0x690170,0x690451-0x690170),base+0x690170))
    bounded_writes=[]
    for i in bounded_body:
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM and memory_operand_writes(i,ix):
                bn=i.reg_name(op.mem.base)
                if bn in ('r8','r9'):
                    assert op.mem.index==0 and op.mem.disp==0 and op.size==4;bounded_writes.append(rec(i))
                else:
                    assert bn in ('rsp','rbp') and op.mem.index==0
                    localoffset=op.mem.disp+(0x100 if bn=='rbp' else 0)
                    assert 0<=localoffset and localoffset+op.size<=0x108
        if i.mnemonic=='call':assert i.operands[0].imm==base+0x9df5b0
    assert len(bounded_writes)==2
    ccins,ccsucc,ccbad,ccindirect=ns['decode'](base+0x68cc80);assert not ccbad and not ccindirect
    for i in ccins.values():
        for ix,op in enumerate(i.operands):
            if op.type==X86_OP_MEM and i.reg_name(op.mem.base)=='rsp':
                if memory_operand_writes(i,ix):
                    # Entry store uses+20, steady writes do not touch c8/d0/d8.
                    assert i.address==base+0x68cc80 or all(not(op.mem.disp<h+8 and h<op.mem.disp+op.size) for h in (0xc8,0xd0,0xd8))
                if i.mnemonic=='lea':assert op.mem.disp not in (0xc8,0xd0,0xd8)
        if i.mnemonic=='call':assert i.operands[0].imm in (base+0x690170,base+0x68dd10)
    previous_transfer=ns['transfer']
    def closure_transfer(ins,st):
        out=previous_transfer(ins,st)
        if ins.address in ccins and ins.mnemonic=='call':
            for h in (0x20,0x28,0x30):
                if ('slot',h) in st:out[('slot',h)]=st[('slot',h)]
        if ins.address==base+0x68e004:
            before=st.get('rdi');chosen=None
            if before and before[:2]==('constant',0):chosen=st.get('r10')
            elif before and before[:2]==('mission',0xed3f80):chosen=before
            if chosen:out['rcx']=(chosen[0],chosen[1],chosen[2]|{base+0x68dffe,base+0x68e001,ins.address})
        return out
    ddins,ddsucc,ddbad,ddindirect=ns['decode'](base+0x68dd10);assert not ddbad and not ddindirect
    assert ddins[base+0x68dffe].op_str=='rdi, rdi' and ddins[base+0x68e001].op_str=='rcx, rdi' and ddins[base+0x68e004].op_str=='rcx, r10'
    for dst,src in ((0x68e004,0x68e001),(0x68e001,0x68dffe)):
        assert [a for a,ts in ddsucc.items() if base+dst in ts]==[base+src]
    ns['transfer']=closure_transfer
    closure_contexts=[]
    for proc,contract,states in list(analyses):
        if proc!=base+0x68cf30:continue
        if contract.get('entryRegisters',{}).get('r8')!='mission' or contract.get('entryBiases',{}).get('r8')!=0:continue
        facts=contract.get('entryStackFacts',{})
        if not all(k in facts for k in ('40','48')):continue
        if facts['40']!={'kind':'mission','bias':0xd705c8}:continue
        if facts['48'] not in ({'kind':'mission','bias':0xed3f80},{'kind':'constant','bias':0}):continue
        if contract.get('entryRegisters',{}).get('r9')!='mission' or contract.get('entryBiases',{}).get('r9')!=0xc58750:continue
        for target,slots,regs,bias in (
            (0x68d800,{'48':{'kind':'mission','bias':0xc58750},'56':facts['40'],'64':facts['48']},{'r9':'mission'},{'r9':0}),
            (0x68cc80,{'40':facts['40'],'48':facts['48']},{'r9':'mission'},{'r9':0})):
            c={'kind':'exact-reviewed-capture-fields','entryPointerKind':None,'entryRegisters':regs,'entryBiases':bias,'entryStackFacts':slots,
               'sourceEntryContract':contract,'evidenceIds':contract.get('evidenceIds',[]),'captureProof':'captureContracts in this receipt; no generic capture ABI inferred.'}
            sts,rpt=ns['analyze'](base+target,c);assert sts is not None
            new_analyses.append((base+target,c,sts));closure_contexts.append(c)
            if target==0x68cc80:
                st=sts[base+0x68cd9c];rsp=st['rsp'][1]
                wanted={48:st.get(('slot',rsp+40)),56:st.get(('slot',rsp+48))}
                if all(v is not None for v in wanted.values()):
                    cc={'kind':'exact-capture-through-bounded-private-home','entryPointerKind':None,'entryRegisters':{'r8':'mission'},'entryBiases':{'r8':0},
                        'entryStackFacts':{str(k):{'kind':v[0],'bias':v[1]} for k,v in wanted.items()},'sourceEntryContract':c,
                        'callInstruction':rec(ccins[base+0x68cd9c]),'evidenceIds':[bounded_record['evidence_id']]}
                    ddstates,rpt=ns['analyze'](base+0x68dd10,cc);assert ddstates is not None
                    new_analyses.append((base+0x68dd10,cc,ddstates));closure_contexts.append(cc)
    results={a:[] for a in candidates}
    for proc,contract,states in new_analyses:
        decoded=ns['cache'][proc][0]
        for a,st in states.items():
            ins=decoded[a]
            after=ns['transfer'](ins,st)
            values=[(str(k),v) for k,v in st.items() if v[0].startswith('mission-companion-offset-')]
            values.extend(('after:'+str(k),v) for k,v in after.items() if v[0].startswith('mission-companion-offset-'))
            for ix,op in enumerate(ins.operands):
                if op.type==X86_OP_MEM:
                    v=ns['address'](ins,op.mem,st)
                    if v and v[0].startswith('mission-companion-offset-'):values.append(('operand:'+str(ix),v))
            for observation,v in values:
                stride=int(v[0].rsplit('-',1)[1],16)
                for pool,(origin,width) in previous.initial.POOLS.items():
                    if width!=stride or not 0<=v[1]-origin<width:continue
                    for definition in v[2]&candidates.keys():
                        if any(x['pool']==pool for x in results[definition]):continue
                        results[definition].append({'root':hex(proc),'entryContract':contract,'pool':pool,'fieldBias':v[1]-origin,
                            'use':rec(ins),'observation':observation,'definitions':[rec(decoded[x]) for x in sorted(v[2]) if x in decoded]})
    observed_uses={a:[] for a in candidates}
    for proc,c,states in new_analyses:
        for a,st in states.items():
            i=ns['cache'][proc][0][a]
            reads,_=i.regs_access()
            for r in reads:
                key=ns['reg'](i,r);v=st.get(key)
                if not v or not ('companion-offset-' in v[0]):continue
                for definition in v[2]&candidates.keys():
                    item={'instruction':rec(i),'register':key,'kind':v[0],'bias':v[1]}
                    if item not in observed_uses[definition]:observed_uses[definition].append(item)
    output=[{'multiply':r['multiply'],'classification':'proven-companion-address-contribution' if results[a] else 'unresolved-owner',
             'proofs':results[a],'observedResultUses':observed_uses[a],'hookApproved':False,'allResultUsesProvenAddressOnly':False} for a,r in candidates.items()]
    return {'schemaVersion':1,'status':'bounded-worker-companion-extension-not-hooks','nativeSourceSha256':previous.initial.SHA,
        'sources':[{'path':str(p),'sha256':digest(p)} for p in sorted(deps)],'candidates':output,
        'classificationCounts':dict(Counter(x['classification'] for x in output)),
        'privateHomesProof':{'root':hex(root),'frameRspMinusEntryRsp':-0x6de8,'homes':{hex(k):hex(v) for k,v in stores.items()},
            'reversePredecessorLifetime':[hex(a) for a in sorted(lifetime)],'directWrites':writes,'callEffects':calls,
            'indexedWriteProof':{'instructions':contact,'counterInstructions':counter_rows,'range':list(indexed_range),'counter':'RBP+fe0; initialized/reset0 and guarded<64 before increment; no callee outputs overlapcounter.'},
            'scope':'Exact direct stores, bounded indexed stores and audited callees establish private-home survival. Only already-established home facts survive conservative alias kills; no unknown spill becomes a pointer. Later home reuse is outside reverse-predecessor lifetime.'},
        'captureContracts':{'bodies':capture_bodies,'exactFieldInstructions':capture_fields,'homeUses':capture_home_uses,'contexts':closure_contexts,'boundedCalleeEvidenceId':bounded_record['evidence_id'],'boundedCalleeOutputStores':bounded_writes,'scope':'Identity propagation through exact first capture fields18/20/28 and second capture18/20; first48-byte and second40-byte managers preserve fields. Worker home dereferences are reads. This does not approve lifetime changes or asynchronous growth.'},
        'instructions':[r for _,r in sorted(rows.items())],
        'segmentedRoutingRequirements':['Resolve at each proven pool-base plus logical-index address, not at the shared IMUL; a single2c8 offset feeds both A and B.', 'Preserve the optional null B argument and per-record B flag fallback to A.', '6763c0 copies selected records through a local buffer; preserve bounded copy size and local-buffer identity rather than routing that temporary to storage.', 'Captured base pointers are not automatically a block-zero-only lookup contract. A live adapter must replace every indexed address and retain task capture lifetime while growth publishes new blocks.'],
        'limitations':['This slice resolves11 game-side identities only. Six FMOD receiver associations from the previous receipt remain unresolved and are not pool approvals.','Observed result uses are retained, but complete scalar-use/pointer-escape and segmented iteration closure is not certified. No live-growth safe point or hook installation approved.']}

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-segmented-companion-worker-extension.json'));args=p.parse_args()
    report=build();out=safe_review_output(args.output,[s['path'] for s in report['sources']]);out.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'path':str(out),'sha256':digest(out),'classificationCounts':report['classificationCounts']}))

if __name__=='__main__':main()
