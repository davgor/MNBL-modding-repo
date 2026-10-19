"""Pin the original file, executable bytes and unwind table for native binding."""
from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'build/battle-scale/python-libs'))
import pefile
source=ROOT/'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
raw=source.read_bytes();sha=hashlib.sha256(raw).hexdigest()
assert sha=='2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'
pe=pefile.PE(data=raw)
sections={s.Name.rstrip(b'\0').decode():s for s in pe.sections}
assert [s.Name.rstrip(b'\0') for s in pe.sections if s.Characteristics&0x20000000]==[b'.text']
text=sections['.text'];pdata=sections['.pdata']
for block in pe.DIRECTORY_ENTRY_BASERELOC:
    for e in block.entries:
        if e.type:
            assert not text.VirtualAddress<=e.rva<text.VirtualAddress+text.Misc_VirtualSize
            assert not pdata.VirtualAddress<=e.rva<pdata.VirtualAddress+pdata.Misc_VirtualSize
pins=[('headers',0,pe.OPTIONAL_HEADER.SizeOfHeaders),('text',text.VirtualAddress,text.Misc_VirtualSize),
      ('unwind',pdata.VirtualAddress,pdata.Misc_VirtualSize)]
lines=['#pragma once','#include <array>','#include <cstdint>','namespace mnbl::storage::native_identity {',
       f'inline constexpr std::size_t file_size={len(raw)};',f'inline constexpr std::size_t image_size=0x{pe.OPTIONAL_HEADER.SizeOfImage:x};',
       'inline constexpr std::array<unsigned char,32> file_hash{'+','.join('0x'+sha[i:i+2] for i in range(0,64,2))+'};',
       'struct Region { std::size_t offset, size; bool requires_execute; std::array<unsigned char,32> hash; };',
       f'inline constexpr std::array<Region,{len(pins)}> regions{{{{']
rows=[]
for name,rva,size in pins:
    digest=hashlib.sha256(pe.get_data(rva,size)).hexdigest()
    execute=name=='text'
    lines.append('{'+f'0x{rva:x},0x{size:x},'+('true,' if execute else 'false,')+'{'+','.join('0x'+digest[i:i+2] for i in range(0,64,2))+'}},')
    rows.append(dict(name=name,rva=hex(rva),size=size,sha256=digest,requiresExecute=execute))
lines+=['}};','}']
dest=ROOT/'build/agent-storage/native_element_identity.hpp';dest.write_text('\n'.join(lines)+'\n')
receipt=dict(schemaVersion=1,status='original-native-binding-identity',binarySha256=sha,fileSize=len(raw),
    imageSize=pe.OPTIONAL_HEADER.SizeOfImage,regions=rows,executableAndPdataRelocations=0,
    generatorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),generatedSha256=hashlib.sha256(dest.read_bytes()).hexdigest(),
    limits=['Header/text/pdata identity only; mutable runtime state is not an initialization or lifetime proof.',
            'Binding must precede authorized text modification and exclude concurrent module/code changes.'],gameLaunched=False)
(ROOT/'evidence/engine-extension/native-element-binding-identity.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(dict(status=receipt['status'],regions=len(rows))))
