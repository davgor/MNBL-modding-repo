"""Static decoder for the hash-pinned AIInfluence v6.0.2 constant pool.

First run StringExport.csproj on the original artifact. This script interprets
the recovered arithmetic and LZMA format; it never loads/executes target code.
Usage: python StringDecode.py OUTPUT_DIRECTORY
"""
import hashlib,json,lzma,pathlib,struct,sys

P=pathlib.Path(sys.argv[1])
inputs=json.loads((P/'static-inputs.json').read_text())
assert inputs['originalSha256']=='472658f15a0ee00b6b05904830fda257593653f74fdf3c8fec1345267a73862d'
assert not inputs['failures'], 'Unresolved CFG predecessors must not be ignored'
encrypted=(P/'encrypted-constants.bin').read_bytes()
assert hashlib.sha256(encrypted).hexdigest()==inputs['fieldSha256']
assert len(encrypted)==41152*4
words=struct.unpack('<41152I',encrypted)
key=[];v=20020359
for _ in range(16):
    v^=v>>12;v^=(v<<25)&0xffffffff;v^=v>>27;v&=0xffffffff;key.append(v)
raw=bytearray()
for j in range(0,len(words),16):
    for k in range(16):
        val=words[j+k]^key[k];raw+=struct.pack('<I',val);key[k]^=val
size=struct.unpack('<I',raw[5:9])[0]
assert size<=64*1024*1024, 'Bound decompression'
props=raw[0];lc=props%9;props//=9;lp=props%5;pb=props//5
dictionary=struct.unpack('<I',raw[1:5])[0]
d=lzma.LZMADecompressor(format=lzma.FORMAT_RAW,filters=[{'id':lzma.FILTER_LZMA1,'dict_size':dictionary,'lc':lc,'lp':lp,'pb':pb}])
blob=d.decompress(raw[9:],max_length=size)
assert len(blob)==size
# Original generic decoder IL, tokens 06000018 through 0600001C:
# transform id modulo 2^32, extract tag (high 2 bits), use low 30 bits*4.
# String arm reads little-endian uint32 byte count then UTF-8 bytes.
specs={
 '06000018':(-2143416819,1950472576,1),
 '06000019':(-760314179,-592256723,2),
 '0600001A':(-304007915,-361441848,0),
 '0600001B':(-507144943,-1243599762,0),
 '0600001C':(-1204271471,-779201099,2),
}
results=[]
for call in inputs['calls']:
    mul,xor,string_tag=specs[call['decoderToken']]
    value=((call['argument']*mul)^xor)&0xffffffff
    tag=value>>30;offset=(value&0x3fffffff)<<2
    assert tag==string_tag
    length=struct.unpack_from('<I',blob,offset)[0]
    assert offset+4+length<=len(blob)
    text=blob[offset+4:offset+4+length].decode('utf-8',errors='strict')
    results.append(dict(call,tag=tag,constantOffset=offset,text=text))
(P/'constants.bin').write_bytes(blob)
(P/'decoded-strings.json').write_text(json.dumps(results,ensure_ascii=True,indent=2)+'\n')
summary={
 'originalSha256':inputs['originalSha256'],
 'initializerToken':inputs['initializerToken'],
 'fieldToken':inputs['initializerFieldToken'],
 'fieldRva':inputs['fieldRva'],'fieldFileOffset':inputs['fieldFileOffset'],
 'encryptedBytes':len(encrypted),'encryptedSha256':inputs['fieldSha256'],
 'blobBytes':len(blob),'blobSha256':hashlib.sha256(blob).hexdigest(),
 'decodedCalls':len(results),'distinctStrings':len(set(r['text'] for r in results)),
 'cfgPredecessorFailures':len(inputs['failures']),
 'invalidTagOrRangeOrUtf8':0,
 'decoderSpecs':{t:{'multiply':x[0],'xor':x[1],'stringTag':x[2]} for t,x in specs.items()},
 'coverage':'All direct generic string calls to the five original module constant decoders; every predecessor through only nop/unconditional branches resolves to one integer literal.',
 'limitations':['Does not assert runtime behavior or full semantic equivalence.','Dynamically computed or reflection-only uses outside these statically present direct calls are not proven absent.','Does not claim game save/user data decryption; only shipped code constants.'],
 'targetExecuted':False
}
(P/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary,ensure_ascii=True))
