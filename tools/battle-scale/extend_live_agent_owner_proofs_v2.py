"""Add five exact owner contributions without changing accepted v1 inputs."""
from pathlib import Path
import hashlib,json
from prove_native_ai_leaf_owner import read_namespace
ROOT=Path(__file__).resolve().parents[2]
def digest(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()

def main():
    files={name:ROOT/'evidence/engine-extension'/name for name in (
        'native-live-agent-owner-extension.json','native-live-owner-private-home-v2.json','native-ai-leaf-owner-proof.json')}
    docs={k:json.loads(p.read_text()) for k,p in files.items()}
    v1=docs['native-live-agent-owner-extension.json'];home=docs['native-live-owner-private-home-v2.json'];leaf=docs['native-ai-leaf-owner-proof.json']
    deps={str(p):digest(p) for p in files.values()}
    for source in [*v1['dependencies'],*home['sources']]:
        assert digest(source['path'])==source['sha256'],source['path'];deps[source['path']]=source['sha256']
    for p,h in leaf['reviewedSourceSha256'].items():
        assert digest(ROOT/p)==h,p;deps[str(ROOT/p)]=h
    deps[str(Path(__file__))]=digest(__file__)
    ns=read_namespace();base=ns['base'];pe=ns['pe'];rows=set()
    def verify(value):
        if isinstance(value,dict):
            if 'address' in value and 'bytes' in value:
                a=int(value['address'],16);raw=bytes.fromhex(value['bytes'])
                assert pe.get_data(a-base,len(raw))==raw,hex(a);rows.add((a,raw))
            for v in value.values():verify(v)
        elif isinstance(value,list):
            for v in value:verify(v)
    verify(home);verify(leaf)
    assert len(home['callEffects'])==20 and len(home['lifetimeInstructions'])==368
    assert home['v2GlobalStoreDominatesReloads'] and len(home['v2MemoryWrites'])==62
    assert all(int(r['address'],16)!=base+0x6f2f49 for r in home['lifetimeInstructions'])
    seeds={int(s['instruction']['address'],16):s for s in home['reloadSeeds']}
    assert set(seeds)=={base+0x6f2928,base+0x6f2afd}
    original=ns['transfer']
    def transfer(i,st):
        out=original(i,st)
        if i.address in seeds:
            s=seeds[i.address];assert i.bytes.hex()==s['instruction']['bytes']
            out[s['register']]=ns['val']('mission',0,[base+0x6f2798,base+0x6f27c9,i.address])
        return out
    ns['transfer']=transfer
    entry=base+0x6f2770
    states,report=ns['analyze'](entry,{'kind':'global-with-two-exact-reviewed-private-reloads','entryRegisters':{},'entryPointerKind':None})
    assert states is not None
    targets={base+x for x in (0x6f2921,0x6f2b96,0x6f2c11,0x6f3194)};found={}
    from capstone.x86 import X86_OP_MEM
    def collect(root,states,wanted):
        insns=ns['cache'][root][0]
        for a,st in states.items():
            i=insns[a];values=list(st.values())+list(ns['transfer'](i,st).values())
            values += [ns['address'](i,op.mem,st) for op in i.operands if op.type==X86_OP_MEM]
            for value in values:
                if value is None or value[0]!='agent':continue
                for definition in value[2]&wanted:
                    found.setdefault(definition,{'multiply':ns['flow'].basic(insns[definition]),'root':hex(root),
                        'use':ns['flow'].basic(i),'fieldBias':value[1],
                        'definitionInstructions':[ns['flow'].basic(insns[x]) for x in sorted(value[2]) if x in insns],
                        'hookApproved':False,'allResultUsesProvenAddressOnly':False})
    collect(entry,states,targets);assert set(found)==targets
    # Keep the leaf's proof conditional on the established exact direct caller.
    # Do not insert a globally inferred AI receiver contract.
    caller=base+0x712560;call=base+0x71272e;entry=base+0x7114c0
    caller_states,_=ns['analysis'][caller]
    assert caller_states[call]['rcx'][:2]==('agent-ai',0) and caller not in ns['blocked_entries']
    assert pe.get_data(0x7114c0,0x32).hex()==leaf['bodyBytes']
    assert ns['add_leaf_group'](entry)
    states,leaf_report=ns['analyze'](entry,leaf['entryContract']);assert states is not None
    collect(entry,states,{base+0x7114d4})
    assert len(found)==5
    previous={int(r,16) for r in v1['remainingRvas']};resolved={x-base for x in found}
    assert resolved<=previous
    for p,h in deps.items():assert digest(p)==h,p
    out={'schemaVersion':1,'status':'accepted-exact-owner-contributions-not-engine-hooks',
         'binarySha256':v1['binarySha256'],'dependencies':[{'path':p,'sha256':h} for p,h in sorted(deps.items())],
         'independentOriginalByteRows':len(rows),'canonicalV1Unresolved':len(previous),'additionalProven':len(found),
         'cumulativeNewOwnerContributions':v1['newlyProvenCount']+len(found),'remainingUnresolvedCount':len(previous-resolved),
         'remainingRvas':[hex(x) for x in sorted(previous-resolved)],'proofs':list(found.values()),
         'review':['Reviewed original callee code and retained REA:711220/63e220/746070 only write named scalar outputs; diagnostic calls receive values and static strings.',
                   '6ead40 writes exactly three output floats and reads its input vector;698600/2692d0 only compare records.698570 resets external record+10, not RSP70. Corrected all-call argument flow excludes the private home under valid-input preconditions.',
                   'Original Mission home write dominates both reloads through the exact bounded switch CFG. Later actual RSP30 reuse remains excluded.',
                   '7114c0 is an exact50-byte leaf reached from the already-proven712560 caller context, with no pdata overlap. No universal receiver ABI inferred.'],
         'privateHomePreconditions':home['argumentFlow']['entryPreconditions'],
         'externalRecordNonAliasProof':home['externalRecordNonAliasProof'],
         'limitations':['Well-formed existing packet/vector/descriptor objects and valid native record indices are required for the four private-home contributions. Corrupt-input aliasing is not certified.',
                        'These five conditional contribution proofs do not establish all incoming calls, all value uses, segmented routing, or thread lifetime safety.',
                        'Canonical v1 inputs and accepted game binaries remain unchanged. No game launched or patch installed.'],
         'enginePatchApproved':False,'gameLaunched':False}
    dst=ROOT/'evidence/engine-extension/native-live-agent-owner-extension-v2.json'
    dst.write_text(json.dumps(out,indent=2)+'\n')
    print(json.dumps({'additional':len(found),'remaining':len(previous-resolved),'sha256':digest(dst)}))
if __name__=='__main__':main()
