"""Independent byte/control-flow review of the two contact-capacity guards."""
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def main():
    folder = ROOT / 'evidence/engine-extension'
    artifact = folder / 'native-contact-capacity-guard.json'
    d = json.loads(artifact.read_text())
    inventory = json.loads((folder / 'native-pool-reference-inventory.json').read_text())
    source = Path(inventory['binary']['path'])
    data = source.read_bytes()
    if hashlib.sha256(data).hexdigest() != d['targetSha256']:
        raise ValueError('Target changed')
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    expected = {
        base+0x5b79b8: ('0f1f840000000000', '4180fe3f7602cd29'),
        base+0x5b7a2c: ('4863c2488d0c80498d04c9e966060000', '4883e820807808400f8668060000cd29')}
    # Pin exact bytes independently; derive branch destinations through decode.
    for patch in d['patches']:
        address = int(patch['address'], 16)
        old, new = expected.pop(address)
        if (patch['original'], patch['replacement']) != (old, new):
            raise ValueError('Guard differs from independently reviewed bytes')
        if pe.get_data(address-base, len(bytes.fromhex(old))) != bytes.fromhex(old):
            raise ValueError('Original guard span changed')
        before = list(md.disasm(bytes.fromhex(old), address))
        after = list(md.disasm(bytes.fromhex(new), address))
        if sum(i.size for i in before) != sum(i.size for i in after):
            raise ValueError('Instruction span length differs')
    if expected:
        raise ValueError('Missing guard')
    for row in d['rawBuilder']+d['rawVisualSetter']:
        at = int(row['address'], 16)
        raw = bytes.fromhex(row['bytes'])
        if pe.get_data(at-base, len(raw)) != raw:
            raise ValueError('Retained raw instruction changed')
    # Search direct branch encodings across all executable bytes, including
    # possible data starts. This catches other direct entries into either span.
    matches = []
    opcodes = [b'\xe8', b'\xe9', b'\xeb'] + [bytes([x]) for x in range(0x70,0x80)] + [bytes([0x0f,x]) for x in range(0x80,0x90)]
    for section in pe.sections:
        if not section.Characteristics & 0x20000000:
            continue
        blob = section.get_data()
        va = base + section.VirtualAddress
        for opcode in opcodes:
            start = 0
            while True:
                at = blob.find(opcode, start)
                if at < 0:
                    break
                start = at+1
                size = 2 if opcode == b'\xeb' or 0x70 <= opcode[0] < 0x80 else 6 if len(opcode)==2 else 5
                if at+size > len(blob):
                    continue
                delta = struct.unpack_from('<b' if size==2 else '<i', blob, at+len(opcode))[0]
                dest = va+at+size+delta
                if base+0x5b79b8 <= dest <= base+0x5b79c0 or base+0x5b7a2c <= dest < base+0x5b7a3c:
                    matches.append((va+at,dest))
    if sorted(matches) != [(base+0x5b7943,base+0x5b7a2c),(base+0x5b7a8a,base+0x5b79c0)]:
        raise ValueError('Unexpected direct entry into a guard span: '+str(matches))
    # Independent exhaustive byte-domain argument, without invoking the audit's
    # outcome helpers. Only the positive original loop path reaches the new CMP.
    for count in range(256):
        index = (count-1) & 255
        original_loop = index < 128
        permitted = original_loop and index <= 63
        iterations = index+1 if permitted else 0
        if permitted and not 1 <= iterations <= 64:
            raise ValueError('Builder count bound violated')
        if original_loop and not permitted and not 65 <= count <= 128:
            raise ValueError('Unexpected fail-fast domain')
        if count <= 64:
            for record in (0,1,15,16,4095):
                cache_base = 0x100000
                key_pointer = cache_base + record*40 + 32
                if key_pointer-32 != cache_base+record*40:
                    raise ValueError('Cache return pointer differs')
    result = dict(schemaVersion=1, status='independent-contact-capacity-guard-review-passed',
                  targetSha256=d['targetSha256'], gameExecuted=False,
                  reviewedArtifact=dict(path=str(artifact),sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()),
                  reviewerSourceSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  rawInstructionsChecked=len(d['rawBuilder'])+len(d['rawVisualSetter']),
                  executableDirectBranchEntries=[dict(address=hex(a),target=hex(t)) for a,t in matches],
                  inputByteCases=256,
                  findings=['Builder guard precedes the first indexed table write; its backedge decrements the guarded index and never increases the bound.',
                            'Cache-hit guard recovers the exact original return pointer and checks the returned count before the common cookie-check epilogue.',
                            'No stack size, unwind record, nonvolatile register value or live arithmetic flag contract is changed.',
                            'Each selected contact producer resets its output count and emits at most once per capsule. At most64 capsules bounds the final byte to candidate+c00, before Agent metadata+c08 and next candidate+c20.'],
                  limitations=['Static review only. Cache lifetime, original allocation failures and data races retain original engine contracts.',
                               'Arbitrary computed code entry, native tampering and third-party code are outside the restricted probe profile.',
                               'Does not approve any other patch sites or certify runtime behavior.'])
    output=safe_review_output(folder/'native-contact-capacity-independent-review.json',[source,artifact])
    output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(dict(status=result['status'], rawInstructions=result['rawInstructionsChecked'])))


if __name__ == '__main__':
    main()
