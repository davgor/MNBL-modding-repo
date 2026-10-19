"""Separate, bounded companion owner extension. Never modifies the base audit."""
from pathlib import Path
import argparse, hashlib, json, sys
from collections import Counter
import audit_segmented_companion_accesses as initial
from patch_native_copy import safe_review_output
ROOT=initial.ROOT

def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def build():
    baseline=ROOT/'evidence/engine-extension/native-segmented-companion-accesses.json'
    original=json.loads(baseline.read_text())
    for source in original['sources']:assert digest(source['path'])==source['sha256'],source['path']
    ns,path,prefix=initial.load_flow();base=ns['base'];pe=ns['pe']
    from capstone.x86 import X86_OP_MEM,X86_OP_IMM
    candidates={int(r['multiply']['address'],16):r for r in original['candidates'] if r['classification']=='unresolved-owner'}
    assert len(candidates)==22
    instruction_records={}
    def record(i):
        row=ns['flow'].basic(i);instruction_records[i.address]=row;return row
    # Two exact SIMD copies preserve the high qword containing the owner.
    # The first copies a history-buffer iterator from RSP50..5f toRSP40..4f.
    copies={0x1805c8ac7:(0x1805c86a0,0x1805c8ac2,'0f28442450','660f7f442440'),
            0x180647a50:(0x1806478e0,0x180647a4c,'0f2845b7','660f7f45b7')}
    copy_proofs=[]
    for store,(root,load,loadhex,storehex) in copies.items():
        insns,succ,bad,indirect=ns['decode'](root);assert not bad and not indirect
        assert insns[load].bytes.hex()==loadhex and insns[store].bytes.hex()==storehex
        assert [a for a,targets in succ.items() if store in targets]==[load]
        copy_proofs.append({'root':hex(root),'load':record(insns[load]),'store':record(insns[store]),
            'proof':'Sole predecessor is the adjacent load intoXMM0; exact16-byte store copies the source stack bytes, including any independently established high-qword owner. No pointer identity is invented for unknown source bytes.'})
    optional_insns,optional_succ,bad,indirect=ns['decode'](base+0x68efc0)
    assert not bad and not indirect
    test=optional_insns[base+0x68f26f];select=optional_insns[base+0x68f272]
    assert test.mnemonic=='test' and test.op_str=='r10, r10'
    assert select.mnemonic=='cmove' and select.op_str=='r10, rdi'
    assert [a for a,t in optional_succ.items() if select.address in t]==[test.address]
    optional_proof={'test':record(test),'select':record(select),'proof':'Immediately preceding TEST R10,R10 supplies ZF. Exact incoming0 selectsRDI; an established valid live-Mission+companionB address is nonzero and retainsR10. Unknown or other values stay unknown. This is a call-context proof, not a globally assumed non-null B argument.'}
    transfer=ns['transfer']
    def extended_transfer(ins,st):
        out=transfer(ins,st)
        if ins.address in copies:
            root,load,_,_=copies[ins.address];loadins=ns['cache'][root][0][load]
            source=ns['address'](loadins,loadins.operands[1].mem,st)
            dest=ns['address'](ins,ins.operands[0].mem,st)
            if source and dest and source[0]==dest[0]=='stack':
                for offset in (0,8):
                    v=st.get(('slot',source[1]+offset))
                    if v:out[('slot',dest[1]+offset)]=(v[0],v[1],v[2]|{load,ins.address})
        if ins.address==base+0x68f272:
            before=st.get('r10');chosen=None
            if before and before[:2]==('constant',0):chosen=st.get('rdi')
            elif before and before[:2]==('mission',0xed3f80):chosen=before
            if chosen:out['r10']=(chosen[0],chosen[1],chosen[2]|{test.address,select.address})
        return out
    ns['transfer']=extended_transfer
    assert extended_transfer(select,{'r10':ns['val']('constant',0),'rdi':ns['val']('mission',0xd705c8)})['r10'][:2]==('mission',0xd705c8)
    assert extended_transfer(select,{'r10':ns['val']('mission',0xed3f80),'rdi':ns['val']('mission',0xd705c8)})['r10'][:2]==('mission',0xed3f80)
    assert 'r10' not in extended_transfer(select,{'rdi':ns['val']('mission',0xd705c8)})
    # No general receiver propagation. Existing known roots and exact caller
    # contexts are replayed; higher arguments carry a per-call contract only.
    analyses=[]
    for root,contract in ns['selected'].items():
        if root in (0x1805c86a0,0x1806478e0):states,report=ns['analyze'](root,contract)
        else:states,report=ns['analysis'][root]
        if states is not None:analyses.append((root,contract,states))
    targets={base+r for r in (0x6763c0,0x68cf30,0x68d800,0x68dd10,0x68eea0,0x68efc0)}
    contexts=[];seen=set();reports=[]
    for iteration in range(5):
        added=[]
        for caller,contract,states in analyses:
            insns=ns['cache'][caller][0]
            for a,st in states.items():
                ins=insns[a]
                if ins.mnemonic!='call' or not ins.operands or ins.operands[0].type!=X86_OP_IMM:continue
                target=ins.operands[0].imm
                if target not in targets or target not in ns['groups']:continue
                rsp=st.get('rsp')
                if not rsp or rsp[0]!='stack':continue
                registers={r:v for r,v in st.items() if r in ('rcx','rdx','r8','r9') and v[0] in ('mission','agent')}
                slots={40+8*i:st.get(('slot',rsp[1]+32+8*i)) for i in range(4)}
                slots={k:v for k,v in slots.items() if v and (v[0]=='mission' or v[:2]==('constant',0))}
                if not slots and not any(v[0]=='mission' and v[1]!=0 for v in registers.values()):continue
                key=(target,tuple(sorted((r,v[:2]) for r,v in registers.items())),tuple(sorted((k,v[:2]) for k,v in slots.items())))
                if key in seen:continue
                seen.add(key)
                definitions=set().union(*(v[2] for v in list(registers.values())+list(slots.values())))
                context={'kind':'exact-context-companion-worker-arguments','entryPointerKind':None,
                    'entryRegisters':{r:v[0] for r,v in registers.items()},'entryBiases':{r:v[1] for r,v in registers.items()},
                    'entryStackFacts':{str(k):{'kind':v[0],'bias':v[1]} for k,v in slots.items()},
                    'evidenceIds':contract.get('evidenceIds',[]),'call':record(ins),'caller':hex(caller),'callerEntryContract':contract,
                    'definitions':[record(insns[x]) for x in sorted(definitions) if x in insns],
                    'proof':'Exact must-state outgoing slots mapped callerRSP+20 to calleeEntryRSP+28. Constant zero remains scalar, not a pool identity. Nonzero unknown arguments are not typed.'}
                states2,report=ns['analyze'](target,context);reports.append(report)
                if states2 is not None:added.append((target,context,states2));contexts.append(context)
        analyses.extend(added)
        if not added:break
    found={a:[] for a in candidates}
    diagnostics={a:[] for a in candidates}
    for root,contract,states in analyses:
        insns=ns['cache'][root][0]
        for a in candidates.keys()&states.keys():
            diagnostics[a].append({'root':hex(root),'entryContract':contract,'context':[
                {'instruction':record(insns[x]),'owners':{str(k):{'kind':v[0],'bias':v[1]} for k,v in states.get(x,{}).items()}}
                for x in sorted(insns) if a-24<=x<=a+40]})
        for a,st in states.items():
            ins=insns[a]
            values=[('register:'+str(k),v) for k,v in st.items() if v[0].startswith('mission-companion-offset-')]
            post=extended_transfer(ins,st)
            values.extend(('result:'+str(k),v) for k,v in post.items() if v[0].startswith('mission-companion-offset-') and v!=st.get(k))
            for ix,op in enumerate(ins.operands):
                if op.type==X86_OP_MEM:
                    v=ns['address'](ins,op.mem,st)
                    if v and v[0].startswith('mission-companion-offset-'):values.append(('operand:'+str(ix),v))
            for observation,v in values:
                stride=int(v[0].rsplit('-',1)[1],16)
                for pool,(origin,width) in {**initial.POOLS,**initial.HISTORY}.items():
                    if width!=stride or not 0<=v[1]-origin<width:continue
                    for definition in v[2]&candidates.keys():
                        if any(p['pool']==pool and p['fieldBias']==v[1]-origin for p in found[definition]):continue
                        found[definition].append({'root':hex(root),'entryContract':contract,'pool':pool,'fieldBias':v[1]-origin,
                            'use':record(ins),'observation':observation,
                            'definitions':[record(insns[x]) for x in sorted(v[2]) if x in insns]})
    rows=[]
    for a,old in candidates.items():
        uses=found[a]
        rows.append({'multiply':old['multiply'],'classification':('proven-companion-address-contribution' if any(x['pool'] in initial.POOLS for x in uses)
                    else 'proven-distinct-history-buffer' if uses else 'unresolved-owner'),
                    'proofs':uses,'diagnostics':diagnostics[a],'hookApproved':False,'allResultUsesProvenAddressOnly':False})
    import capstone
    decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
    decoder.detail=True
    def span(a,z):
        body=list(decoder.disasm(pe.get_data(a,z-a),base+a));assert sum(i.size for i in body)==z-a
        return [record(i) for i in body]
    spawn=span(0x5cd130,0x5cd29c);spawnby={int(i['address'],16):i for i in spawn}
    expected={0x5cd176:('mov','r13d, eax'),0x5cd1b5:('movsxd','rbx, r13d'),
        0x5cd1e1:('imul','rax, rbx, 0x230'),0x5cd1f3:('mov','dword ptr [rax + 0x1c], r13d'),
        0x5cd22a:('imul','rdi, rbx, 0x2c8'),0x5cd235:('mov','dword ptr [rsi], r13d'),
        0x5cd267:('mov','dword ptr [rdi + rbx], r13d'),0x5cd28d:('mov','qword ptr [r14 + 0x20], rsi')}
    for rva,(mnemonic,operands) in expected.items():
        i=spawnby[base+rva];assert (i['mnemonic'],i['operands'])==(mnemonic,operands)
    helper_path=ROOT/'evidence/engine-extension/native-pool-helper-caller-review.json'
    helpers=json.loads(helper_path.read_text())
    links=[r for r in helpers['entries'] if r['target'] in ('0x1805d9350','0x1805d94a0')]
    releases=[r for r in helpers['entries'] if r['target']=='0x1805d8610']
    assert {r['address'] for r in links}=={'0x1805cd1fa','0x1805cd23b','0x1805cd271'}
    assert {r['address'] for r in releases}=={'0x1805d08fa','0x1805d0908'}
    delete_start=span(0x5d00a0,0x5d0756)
    for ins in decoder.disasm(pe.get_data(0x5d00c9,0x5d0756-0x5d00c9),base+0x5d00c9):
        _,writes=ins.regs_access()
        assert not {'rdi','edi','di','dil'}&{ins.reg_name(r) for r in writes}
    delete_tail=span(0x5d0756,0x5d090d)
    assert delete_tail[0]['mnemonic']=='mov' and delete_tail[0]['operands']=='rbp, rdi'
    for ins in decoder.disasm(pe.get_data(0x5d0759,0x5d090d-0x5d0759),base+0x5d0759):
        _,writes=ins.regs_access()
        assert not {'rbp','ebp','bp','bpl','rdi','edi','di','dil'}&{ins.reg_name(r) for r in writes}
    coupling={'status':'reviewed-primary-create-delete-index-coupling-not-universal-ledger-equivalence',
        'spawnInstructions':spawn,'releaseInstructions':delete_start+delete_tail,
        'retainedLinkCallers':links,'retainedReleaseCallers':releases,
        'facts':['Primary allocator return or explicit requested index is retained inR13D, then signextended intoRBX.',
                 '230 usesRBX*230 and storesR13D atheader+1c. A usesRBX*2c8 and storesR13D atheader0. B reuses exactly that2c8 offset and storesR13D atheader0.',
                 'Link helpers receiveEDX=R13D; they are not independent slot selectors on this route.',
                 'Agent+20 receives the same-index A address inRSI. No second allocation or alternate index chooses this cached pointer.',
                 'Delete stores incomingEDX inRDI at5d00c6.5d0756 copiesRDI toRBP; no intervening tail instruction writes either nonvolatile register.230 header clear usesRBP*230 at5d0837; its table clear usesRDI*2 at5d0898. A/B calls both receiveEDX=EDI.230 removal is inline with separate metadata.',
                 'All component tables/counts/high-water fields are physically separate. The reviewed lifecycle couples occupancy but does not erase metadata identity.',
                 'SecondaryC60 has separate metadata and no established gameplay mapping to these companions. It must not share primary occupancy.'],
        'adapterRequirement':'A future primary-bundle allocator must reserve/commit/release the same logical slot in primary,230,A,B atomically and preserve each metadata contract. Assert four-domain occupancy agreement at adoption; refuse inconsistent state. Secondary remains an independent domain. Whole-engine arbitrary-writer equivalence is not certified.'}
    deps={Path(x['path']) for x in original['sources']}|{baseline,Path(__file__)}
    return {'schemaVersion':1,'status':'bounded-companion-owner-extension-not-hooks','nativeSourceSha256':initial.SHA,
        'sources':[{'path':str(p),'sha256':digest(p)} for p in sorted(deps)],'inputCandidateCount':22,
        'classificationCounts':dict(Counter(r['classification'] for r in rows)),'candidates':rows,
        'copyProofs':copy_proofs,'optionalBaseSelectProof':optional_proof,'higherArgumentContexts':contexts,'analysisReports':reports,
        'primaryCompanionIndexCoupling':coupling,
        'instructions':[v for _,v in sorted(instruction_records.items())],
        'limitations':['No hook approval or all-use rewrite closure. Exact call contexts do not authorize a blanket replacement for every caller of that function.']}

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',default=str(ROOT/'evidence/engine-extension/native-segmented-companion-extension.json'));args=p.parse_args()
    report=build();out=safe_review_output(args.output,[x['path'] for x in report['sources']]);out.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'path':str(out),'sha256':digest(out),'classificationCounts':report['classificationCounts']}))

if __name__=='__main__':main()
