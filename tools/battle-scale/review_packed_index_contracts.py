"""Reconcile the stale packed-index summary with the reviewed 66-site contract.

Read-only target inspection. This is a bounded candidate review, not an assertion
that every possible packed representation in the executable has been found.
"""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'build/battle-scale/python-libs'))
import capstone
import pefile
from patch_native_copy import safe_review_output


def main():
    folder = ROOT / 'evidence/engine-extension'
    inputs = [folder / n for n in ('native-pool-reference-inventory.json',
              'native-packed-index-candidates.json', 'pool-runtime-safety-contracts.json',
              'native-expansion-ghidra-evidence.json', 'native-closure-ghidra-evidence.json')]
    inventory, candidates, contracts, old, new = [json.loads(p.read_text()) for p in inputs]
    binary = Path(inventory['binary']['path'])
    data = binary.read_bytes()
    sha = hashlib.sha256(data).hexdigest()
    if any(sha != x for x in (inventory['binary']['sha256'], candidates['targetSha256'], contracts['targetSha256'])):
        raise ValueError('Target identities differ')
    pe = pefile.PE(data=data)
    base = pe.OPTIONAL_HEADER.ImageBase
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    proof = contracts['contracts']['native11BitCandidates']
    originals = {s['address']: s for s in candidates['sites']}
    reviewed = {s['address']: s for s in proof['sites']}
    if len(reviewed) != 66 or set(originals) != set(reviewed):
        raise ValueError('Packed candidate coverage changed')
    rows = []
    for address, site in reviewed.items():
        rva = int(address, 16) - base
        expected = bytes.fromhex(originals[address]['bytes'])
        if site['bytes'] != expected.hex() or pe.get_data(rva, len(expected)) != expected:
            raise ValueError('Instruction bytes changed at ' + address)
        ins = next(md.disasm(expected, base + rva))
        if ins.mnemonic + ' ' + ins.op_str != site['instruction']:
            raise ValueError('Instruction decode changed at ' + address)
        # Decode each supplied context from its exact recorded boundary, checking
        # the text against target bytes rather than trusting a copied summary.
        for context in site['context']:
            at = int(context['address'], 16)
            actual = next(md.disasm(pe.get_data(at - base, 15), at))
            if actual.mnemonic + ' ' + actual.op_str != context['instruction']:
                raise ValueError('Context decode changed at ' + context['address'])
        rows.append(site)
    records = {r['evidence_id']: r for b in (old, new) for r in b['records']}
    evidence = []
    for number in (490, 491, 492, 523, 524, 525):
        entry = contracts['evidence'][str(number)]
        record = records[entry['evidenceId']]
        if not isinstance(record.get('normalized_result'), str):
            raise ValueError('Missing retained native explanation')
        evidence.append({'evidenceId': record['evidence_id'], 'parameters': record['parameters'],
                         'normalizedResult': record['normalized_result']})
    managed = proof['managedParameterProof']
    if hashlib.sha256(Path(managed['assembly']).read_bytes()).hexdigest() != managed['sha256']:
        raise ValueError('Managed callback parameter assembly changed')
    result = dict(schemaVersion=2, status='66-listed-candidates-classified-bounded-static-review',
                  targetSha256=sha, gameExecuted=False, candidateCount=66,
                  unresolvedListedCandidates=[], findings=rows,
                  managedParameterProof=managed, retainedEvidence=evidence,
                  originalScanDecodeGapRanges=candidates['decodeGapRanges'],
                  sources=[dict(path=str(p), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in inputs],
                  review={'reviewer': 'root', 'sourceInstructionAndContextChecks': 'passed',
                          'conclusion': 'The four previously unresolved keys are action/item-usage keys, not Agent identifiers. All 66 listed operations are flags, action keys, division, linked-chunk growth, or page indexing. Preserve their constants.'},
                  limitations=['The supplemental scan covers selected exception-directory ranges and retains its decode gaps; this is not universal absence proof.',
                               'No arbitrary caller-domain or memory-corruption guarantee is made.',
                               'Managed network/replay identifiers remain incompatible; the Single0 gate remains required.'])
    output = safe_review_output(folder / 'native-packed-index-review.json', [binary, *inputs, Path(managed['assembly'])])
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'classified': len(rows), 'unresolvedListedCandidates': 0}))


if __name__ == '__main__':
    main()
