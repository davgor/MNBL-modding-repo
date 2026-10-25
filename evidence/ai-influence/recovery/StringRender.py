"""Restore known string-decoder expressions in existing decompiled C#.

Usage: python StringRender.py RESEARCH_DIRECTORY
Uses RESEARCH_DIRECTORY/original-methods.json and /readable; writes /strings/readable.
This is a presentation transformation, not a buildable/runtime replacement.
"""
import json,re,pathlib,sys
root=pathlib.Path(sys.argv[1]);P=root/'strings'
results=json.loads((P/'decoded-strings.json').read_text())
methods=json.loads((root/'original-methods.json').read_text())
names={m['token']:m['name'].encode('unicode_escape').decode() for m in methods if m['type']=='<Module>'}
lookup={(names[r['decoderToken']],r['argument']):r['text'] for r in results}
out=P/'readable';out.mkdir(exist_ok=True)
count=0;remaining=0
pattern=re.compile(r'(?:global::)?<Module>\.((?:\\u[0-9a-f]{4})+)<string>\((-?\d+)\)')
for f in (root/'readable').glob('*.cs'):
    text=f.read_text(encoding='utf-8-sig')
    def replace(match):
        global count,remaining
        key=(match[1],int(match[2]))
        if key not in lookup:remaining+=1;return match[0]
        count+=1;return json.dumps(lookup[key],ensure_ascii=True)
    text=pattern.sub(replace,text)
    (out/f.name).write_text(text,encoding='utf-8')
summary={'replacedExpressions':count,'unmappedMatchingExpressions':remaining,'files':len(list(out.glob('*.cs'))),'scope':'Literal presentation replacement in the previously recovered top-level AIInfluence namespace C# files, not semantic recompilation.'}
(P/'render-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary))
