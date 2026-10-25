"""Static evaluator restricted to proxy-token construction. Never executes target code."""
import json,pathlib,re,collections,sys
P=(pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path(__file__).parent)/'out'
MS=json.loads((P/'original-instructions.json').read_text()); M={m['token']:m for m in MS}
FS=json.loads((P/'fields.json').read_text()); F={f['token']:f for f in FS}
R={x['token']:x['signature'] for x in json.loads((P/'memberrefs.json').read_text())}; R.update({m['token']:m['signature'] for m in MS})
def u(x):return x&0xffffffff
def signed(x):return (x&0x7fffffff)-(x&0x80000000)
def arithmetic(op,a,b=None):
 if op=='neg':return u(-a)
 if op=='not':return u(~a)
 if op=='add':return u(a+b)
 if op=='sub':return u(a-b)
 if op=='mul':return u(a*b)
 if op=='xor':return u(a^b)
 if op=='and':return u(a&b)
 if op=='or':return u(a|b)
 if op=='shl':return u(a<<(b&31))
 if op=='shr':return u(signed(a)>>(b&31))
 if op=='shr.un':return u(a)>>(b&31)
 raise ValueError(op)
def const(i):
 op=i['op'];return int(i['operand']) if op in ('ldc.i4','ldc.i4.s') else -1 if op=='ldc.i4.m1' else int(op.rsplit('.',1)[-1])
def idx(i):return int(i['op'].rsplit('.',1)[-1]) if i['op'][-1].isdigit() else int(re.search(r'(\d+)',i['operand']).group())
def attr_hash(attr):
 ctor=M[attr['ctor']]; st=[];fields={};args=[{},*[u(int(x)) for x in attr['args']]]
 for i in ctor['instructions']:
  op=i['op']
  if op.startswith('ldarg'):st.append(args[idx(i)])
  elif op.startswith('ldc.i4'):st.append(u(const(i)))
  elif op in ('neg','not'):st.append(arithmetic(op,st.pop()))
  elif op in ('add','sub','mul','xor','and','or','shl','shr','shr.un'):
   b=st.pop();a=st.pop();st.append(arithmetic(op,a,b))
  elif op=='call' and 'System.Attribute::.ctor' in i['operand']:st.pop()
  elif op=='stfld':v=st.pop();st.pop();fields[i['targetToken']]=v
  elif op=='ret':pass
  else:raise ValueError(('attr ctor',op,i))
 getters=[m for m in MS if m['type']==ctor['type'] and m['token']!=ctor['token'] and m['instructions'] and len(m['instructions'])==3 and m['instructions'][1]['op']=='ldfld']
 assert len(getters)==1
 return fields[getters[0]['instructions'][1]['targetToken']]
def decode(field,binder,opkey):
 f=F[field]; st=[];loc={};args=[f,opkey]
 for i in M[binder]['instructions']:
  op=i['op'];s=i['operand'] or ''
  if op.startswith('ldarg'):st.append(args[idx(i)])
  elif op.startswith('ldloc'):st.append(loc[idx(i)])
  elif op.startswith('stloc'):loc[idx(i)]=st.pop()
  elif op.startswith('ldc.i4'):st.append(u(const(i)))
  elif op=='dup':st.append(st[-1])
  elif op=='pop':st.pop()
  elif op in ('neg','not'):st.append(arithmetic(op,st.pop()))
  elif op in ('add','sub','mul','xor','and','or','shl','shr','shr.un'):
   b=st.pop();a=st.pop();st.append(arithmetic(op,a,b))
  elif op=='ldlen':st.append(len(st.pop()))
  elif op.startswith('conv.'):pass
  elif op in ('ldelem.u1','ldelem.ref'):
   n=st.pop();a=st.pop();st.append(a[n])
  elif op in ('call','callvirt'):
   if 'GetFieldFromHandle' in s:pass
   elif '::get_Module()' in s:st.pop();st.append('module')
   elif '::get_MetadataToken()' in s:
    a=st.pop();st.append(int(a['token'],16) if isinstance(a,dict) else a)
   elif '::ResolveSignature(' in s:st.pop();st.pop();st.append(bytes.fromhex(f['sig']))
   elif '::GetOptionalCustomModifiers()' in s:st.pop();st.append([int(f['modifier'],16)])
   elif '::get_Name()' in s:st.pop();st.append(f['name'])
   elif '::get_Chars(' in s:n=st.pop();a=st.pop();st.append(ord(a[n]))
   elif '::GetCustomAttributes(' in s:st.pop();st.pop();st.append(f['attributes'])
   elif '::GetHashCode()' in s:st.append(attr_hash(st.pop()))
   elif '::ResolveMethod(' in s:
    token=st.pop();st.pop();return f'{token:08X}'
   else:raise ValueError(('binder call',binder,s))
  else:raise ValueError(('binder op',binder,op,i))
 raise ValueError('no resolve')
def checked_origins(ins,call_index,field,key):
 """All incoming CFG edges must carry the same adjacent field/literal pair."""
 byoff={i['offset']:n for n,i in enumerate(ins)}; pred={n:[] for n in range(len(ins))}
 for n,i in enumerate(ins):
  op=i['op']; targets=[]
  if i.get('branch') is not None:targets.append(byoff[i['branch']])
  if i.get('branches') is not None:targets.extend(byoff[x] for x in i['branches'])
  if op not in ('br','br.s','leave','leave.s','ret','throw','rethrow','endfinally') and n+1<len(ins):targets.append(n+1)
  for t in targets:pred[t].append(n)
 def leaves(n,seen=None):
  seen=set() if seen is None else seen
  if n in seen:raise ValueError('cyclic input producer')
  if ins[n]['op'] not in ('nop','br','br.s'):return [n]
  seen=seen|{n};return [x for p in pred[n] for x in leaves(p,seen)]
 literals=[x for p in pred[call_index] for x in leaves(p)]
 assert literals and all(ins[x]['op'].startswith('ldc.i4') and const(ins[x])==key for x in literals),('nonconstant binder input',literals)
 fields=[x for n in literals for p in pred[n] for x in leaves(p)]
 assert fields and all(ins[x]['op']=='ldtoken' and ins[x]['targetToken']==field for x in fields),('nonconstant field input',fields)
 return {'literalOffsets':sorted(set(ins[x]['offset'] for x in literals)),'fieldOffsets':sorted(set(ins[x]['offset'] for x in fields)),'allIncomingEdgesChecked':True}
bindings=[];errors=[]
for m in MS:
 if m['name']!='.cctor':continue
 ins=m['instructions'] or []
 for n,i in enumerate(ins):
  if i['op']=='call' and i['targetToken'] in M and M[i['targetToken']]['type']=='<Module>' and 'RuntimeFieldHandle,System.Byte' in i['operand']:
   try:
    assert m.get('exceptionHandlerCount')==0, 'binder initializer has exception-region control flow or missing EH metadata'
    f=ins[n-2]['targetToken']; key=const(ins[n-1]); origins=checked_origins(ins,n,f,key); token=decode(f,i['targetToken'],key)
    nameIndex=[2,0,1,0,0,2,2,0,2,3,4,0,2,1,1,1][int(i['targetToken'],16)-0x6000006]; opcode=ord(F[f]['name'][nameIndex])^key
    assert opcode in (0x28,0x6f,0x73)
    bindings.append({'inputFlow':origins,'encodedCallOpcode':hex(opcode),'fieldToken':f,'initializer':m['token'],'initializerOffset':i['offset'],'binder':i['targetToken'],'opKey':key,'targetToken':token,'targetSignature':R.get(token),'fieldName':F[f]['name']})
   except Exception as e:errors.append({'initializer':m['token'],'offset':i['offset'],'error':str(e)})
(P/'proxy-bindings.json').write_text(json.dumps(bindings,indent=2))
(P/'proxy-errors.json').write_text(json.dumps(errors,indent=2))
print('bindings',len(bindings),'errors',len(errors),'unresolved targets',sum(x['targetSignature'] is None for x in bindings));print(errors[:2]);print([x for x in bindings if x['fieldToken']=='040020C0'])
