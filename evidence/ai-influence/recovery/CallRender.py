import pathlib,json,re,sys
P=pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path(__file__).parent
strings=json.loads((P.parent/'strings/decoded-strings.json').read_text())
lookup={(r['decoderToken'],r['argument']):r['text'] for r in strings}
out=P/'recovered';out.mkdir(exist_ok=True)
count=0;missing=[]
pattern=re.compile(r'RecoveredGlobal\.RecoveredMethod_(060000[0-9A-F]{2})<string>\((-?\d+)\)')
for f in (P/'out/readable').glob('*.cs'):
 s=f.read_text(encoding='utf-8-sig')
 def sub(m):
  global count
  k=(m[1],int(m[2]))
  if k not in lookup:missing.append({'file':f.name,'expression':m[0]});return m[0]
  count+=1;return json.dumps(lookup[k],ensure_ascii=True)
 (out/f.name).write_text(pattern.sub(sub,s),encoding='utf-8')
(P/'out/render-coverage.json').write_text(json.dumps({'files':len(list(out.glob('*.cs'))),'replacedStringExpressions':count,'unmappedStringExpressions':missing,'limitation':'Presentation-only renaming and literal restoration; not executable source equivalence.'},indent=2))
print(count,len(missing))
