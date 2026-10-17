"""Generate pinned machine code for the isolated Windows native harness."""
import hashlib,json,struct,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
raw=source.read_bytes();assert hashlib.sha256(raw).hexdigest()=='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
proof=json.loads((ROOT/'evidence/engine-extension/native-segmented-lookup-replay.json').read_text())
review_path=ROOT/'evidence/engine-extension/native-segmented-lookup-hook-review.json'
assert hashlib.sha256(review_path.read_bytes()).hexdigest()=='e6f6694a961fb1c86a761e44100411d48795627f846698f87a32cb016f0ca9b3'
review=json.loads(review_path.read_text())
assert proof['toolSha256']==hashlib.sha256((ROOT/'tools/battle-scale/replay_segmented_agent_lookup.py').read_bytes()).hexdigest()
assert proof['binarySha256']==hashlib.sha256(raw).hexdigest()
assert proof['wrapperRva']=='0x70b150'
assert proof['replacementSpanRva']==['0x70b165','0x70b18d']
assert proof['stubAddress']=='0x181000000'
expected_patch=bytes.fromhex('49bb')+struct.pack('<Q',0x181000000)+bytes.fromhex('41ffe3')+b'\x90'*27
assert len(expected_patch)==40 and bytes.fromhex(proof['patchBytes'])==expected_patch
assert proof['stubBytes']==review['proposedMechanicalFix']['stubBytes']
assert proof['unwindInfoHex']==review['proposedMechanicalFix']['unwindInfoHex']
pe=pefile.PE(data=raw);wrapper=bytearray(pe.get_data(0x70b150,0x90))
assert wrapper[0x15:0x3d].hex()==proof['originalBytes']
wrapper[0x15:0x3d]=bytes.fromhex(proof['patchBytes'])
items={'wrapper_code':wrapper,'stub_code':bytes.fromhex(proof['stubBytes']),
       'stub_unwind':bytes.fromhex(proof['unwindInfoHex'])}
content='#pragma once\n#include <array>\n#include <cstdint>\n'
for name,data in items.items():
 content+=f'inline constexpr std::array<std::uint8_t,{len(data)}> {name}{{'+','.join(hex(b) for b in data)+'};\n'
(ROOT/'build/agent-storage/lookup_machine_code.hpp').write_text(content)
print('Prepared pinned wrapper and reviewed 41-byte stub for isolated native harness')
