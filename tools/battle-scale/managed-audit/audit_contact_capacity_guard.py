"""Exact offline guards for the skeleton contact builder and its cache hits.

Writes review JSON only; never changes or loads the target executable.
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(ROOT / 'tools/battle-scale')]
import capstone
import pefile
from patch_native_copy import safe_review_output

SOURCE = Path('D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/TaleWorlds.Native.dll')
SHA = '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
BASE = 0x180000000
BUILD_SITE = BASE + 0x5b79b8
HIT_SITE = BASE + 0x5b7a2c
RETURN = BASE + 0x5b80a2
BUILD_OLD = bytes.fromhex('0f1f840000000000')
BUILD_NEW = bytes.fromhex('4180fe3f7602cd29')
HIT_OLD = bytes.fromhex('4863c2488d0c80498d04c9e966060000')
# Encoding is independently decoded and compared below. The original block
# uses R9 + 40*EDX; the hit branch's RAX is already that address plus32.
HIT_NEW = bytes.fromhex('4883e820807808400f86') + struct.pack('<i', RETURN-(HIT_SITE+14)) + bytes.fromhex('cd29')

def builder_outcome(count):
    if not 0 <= count <= 255:
        raise ValueError('byte required')
    index = (count-1) & 255
    if index >= 128:
        return ('existing-empty-path', 0)
    if index > 63:
        return ('fail-fast', None)
    return ('bounded-builder', count)

def cached_outcome(count):
    if not 0 <= count <= 255:
        raise ValueError('byte required')
    return 'return' if count <= 64 else 'fail-fast'

def audit(output):
    data = SOURCE.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA:
        raise ValueError('Native build mismatch')
    pe = pefile.PE(data=data)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    def raw(a, n): return pe.get_data(a-BASE, n)
    def row(i): return dict(address=hex(i.address), bytes=i.bytes.hex(), decoded=i.mnemonic+' '+i.op_str)
    def body(a,b):
        ins = list(md.disasm(raw(BASE+a,b-a),BASE+a))
        if sum(i.size for i in ins) != b-a: raise ValueError('Decode gap')
        return ins
    builder = body(0x5b77f0,0x5b80cd)
    setter = body(0x6217e0,0x621867)
    by = {i.address:i for i in builder+setter}
    if raw(BUILD_SITE,8) != BUILD_OLD: raise ValueError('Builder alignment bytes differ')
    # Pin original instructions from decoded body, not a guessed overwrite.
    hit = [i for i in builder if HIT_SITE <= i.address < HIT_SITE+16]
    old_hit = b''.join(i.bytes for i in hit)
    if [i.mnemonic for i in hit] != ['movsxd','lea','lea','jmp'] or len(old_hit)!=16:
        raise ValueError('Cache hit block differs')
    expected_hit = ['rax, edx','rcx, [rax + rax*4]','rax, [r9 + rcx*8]',hex(RETURN)]
    if [i.op_str for i in hit] != expected_hit: raise ValueError('Hit arithmetic differs')
    patches=[]
    for a,old,new,expected in [(BUILD_SITE,BUILD_OLD,BUILD_NEW,[('cmp','r14b, 0x3f'),('jbe',hex(BASE+0x5b79c0)),('int','0x29')]),
        (HIT_SITE,old_hit,HIT_NEW,[('sub','rax, 0x20'),('cmp','byte ptr [rax + 8], 0x40'),('jbe',hex(RETURN)),('int','0x29')])]:
        decoded=list(md.disasm(new,a))
        if len(new)!=len(old) or [(i.mnemonic,i.op_str) for i in decoded]!=expected:
            raise ValueError('Guard decoding/size differs')
        patches.append(dict(address=hex(a),original=old.hex(),replacement=new.hex(),instructions=[row(i) for i in decoded]))
    required={0x5b7938:'lea rax, [r9 + 0x20]',0x5b7940:'cmp qword ptr [rax], r15',
        0x5b7943:'je '+hex(HIT_SITE),0x5b794e:'add rax, 0x28',
        0x5b7973:'movzx r14d, byte ptr [r15 + 0x148]',0x5b797b:'xor r12b, r12b',
        0x5b797e:'sub r14b, 1',0x5b7982:'js '+hex(BASE+0x5b7a98),
        0x5b7a78:'inc r12b',0x5b7a86:'sub r14b, 1',0x5b7a8a:'jns '+hex(BASE+0x5b79c0),
        0x5b7f88:'mov byte ptr [rsp + 0x28], r12b',
        0x62181e:'movzx ecx, byte ptr [rax + 8]',0x621822:'mov byte ptr [rbx + 0x868], cl'}
    for a,s in required.items():
        i=by[BASE+a]
        if i.mnemonic+' '+i.op_str != s: raise ValueError('Contract changed: '+hex(a))
    incoming={a:[] for a in (HIT_SITE,BUILD_SITE,BASE+0x5b79c0)}
    for i in builder:
        if i.group(capstone.CS_GRP_JUMP):
            if len(i.operands)!=1 or i.operands[0].type!=capstone.x86.X86_OP_IMM:
                raise ValueError('Unexpected indirect branch in builder')
            t=i.operands[0].imm
            if t in incoming:incoming[t].append(hex(i.address))
            if HIT_SITE<t<HIT_SITE+16 or BUILD_SITE<t<BUILD_SITE+8:
                raise ValueError('Branch into overwritten instruction interiors')
    if incoming[HIT_SITE]!=[hex(BASE+0x5b7943)] or incoming[BUILD_SITE] or incoming[BASE+0x5b79c0]!=[hex(BASE+0x5b7a8a)]:
        raise ValueError('Guard predecessor assumptions differ')
    # Incoming flags are dead: MOV/LEA are neutral and COMISS is the first
    # flags consumer/producer on the permitted builder path. Return path XOR
    # overwrites all arithmetic flags before the cookie-check call.
    if by[BASE+0x5b79c7].mnemonic!='comiss' or by[BASE+0x5b80aa].mnemonic!='xor':
        raise ValueError('Flag liveness contract differs')
    folder=ROOT/'evidence/engine-extension'
    bundle_path=folder/'native-closure-ghidra-evidence.json'
    late_path=folder/'native-pool-attack-late-spill-review.json'
    bundle=json.loads(bundle_path.read_text());late=json.loads(late_path.read_text())
    retained=[]
    for proc in ('0x1805b77f0','0x180621822'):
        r=next(r for r in reversed(bundle['records']) if r.get('operation')=='procedure_pseudo_code' and r.get('parameters',{}).get('procedure')==proc)
        retained.append(dict(procedure=proc,evidenceId=r['evidence_id'],normalizedResult=r['normalized_result']))
    for r in late['retainedCalleeEvidence']:
        if r['procedure'] in ('0x1806956c0','0x1806957d0','0x180695b50','0x180695e90','0x180696170'):retained.append(r)
    if len(retained)!=7: raise ValueError('Missing producer evidence')
    outcome=[dict(sourceBoneByte=n,outcome=builder_outcome(n)[0],maximumCapsules=builder_outcome(n)[1],cachedOutcome=cached_outcome(n)) for n in range(256)]
    assert all(r['maximumCapsules'] is None or r['maximumCapsules']<=64 for r in outcome)
    result=dict(schemaVersion=1,status='concrete-two-site-guard-awaiting-independent-review',targetSha256=SHA,
        gameExecuted=False,patches=patches,sources=[dict(path=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in (Path(__file__).resolve(),bundle_path,late_path)],
        rawBuilder=[row(i) for i in builder],rawVisualSetter=[row(i) for i in setter],retainedEvidence=retained,
        controlFlow=dict(incomingDirectBranches={hex(k):v for k,v in incoming.items()},flags='No live incoming flags consumed; COMISS/XOR overwrite before subsequent conditional use.',stackOrNonvolatileChanges='None added. Cache hit RAX is equivalent original return pointer; RCX temporary value differs but caller-saved. INT29 does not return.'),
        allByteOutcomes=outcome,
        cacheProof=['Miss lookup compares skeleton model pointer at each40-byte record+32. RAX points that key. Cache-hit replacement subtracts32 to recover the same record base as original index arithmetic, then checks actual record count byte+8.',
            'New records use R12B initially0, incremented at most once for each source bone. Guarded initial index0..63 decreases to-1, so count0..64. Count is savedRSP28 then copied into record+8 on both append/reallocate paths.',
            'Cache hits are checked directly, so no assumption that other native code cannot alter or populate the cache is needed for the returned count bound. Fresh-process native-copy application is required; no live injection.',
            '6217e0 copies checked returned byte+8 into visual+868. Only resets and this setter were identified as direct visual field writers; opaque memory corruption and third-party native mutation are outside isolated profile.'],
        bufferGeometry=dict(candidateBaseBodyRsp=hex(0x1610),candidateStride=hex(0xc20),maxAgentCandidateIndex=14,
            contactStride=hex(0x30),maxContactIndex=63,contactLastByteInclusive=hex(0x30+63*0x30),metadataAgentPointerOffset=hex(0xc08),
            highestAgentContactByteExclusive=hex(0x1610+14*0xc20+0x30+63*0x30+1),frameCookieBodyRsp=hex(0xd810),
            explanation='Four695* producers reset outputcount0 and write at current acceptedindex; count<=64 guarantees every attempted iteration index<=63, even when output written before acceptance. Lastbytec00 is before Agentmetadatac08 and nextrecordc20. Caller caps testedAgentcandidates15 independently of total Missionagentcapacity.'),
        limitations=['Fail-fast test restriction, not graceful support for arbitrary high-bone skeleton assets. Original count0 or129..255 signed-empty behavior is preserved.',
            'No target code executed. Guard instructions, INT29 handling, and game behavior require an explicitly released future test.',
            'This establishes this contact-builder and selected caller capacity only, not all remaining native buffers or whole-patch safety.'])
    output=safe_review_output(output,[SOURCE,bundle_path,late_path,Path(__file__).resolve()])
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(path=str(output),patches=patches,rawInstructions=len(builder)+len(setter))))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,default=ROOT/'evidence/engine-extension/native-contact-capacity-guard.json')
    audit(parser.parse_args().output)
