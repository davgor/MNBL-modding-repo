import json,pathlib,re,collections,sys
P=(pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path(__file__).parent)/'out'
ms=json.loads((P/'original-instructions.json').read_text());M={x['token']:x for x in ms}
bs=json.loads((P/'proxy-bindings.json').read_text());B={x['fieldToken']:x for x in bs}
wrappers={}
for m in ms:
 ins=m['instructions'] or []
 if len(ins)>=3 and ins[0]['op']=='ldsfld' and ins[0]['targetToken'] in B and ins[-1]['op']=='ret' and ins[-2]['op']=='call' and all(i['op'].startswith('ldarg') for i in ins[1:-2]):
  wrappers[m['token']]=B[ins[0]['targetToken']]
usages=[]
for m in ms:
 for i in m['instructions'] or []:
  if i['op']=='ldsfld' and i['targetToken'] in B:
   b=B[i['targetToken']];usages.append({'methodToken':m['token'],'signature':m['signature'],'ilOffset':i['offset'],'kind':'delegate-field-load','fieldToken':b['fieldToken'],'targetToken':b['targetToken'],'targetSignature':b['targetSignature'],'callOpcode':b['encodedCallOpcode']})
  if i['op'] in ('call','callvirt') and i['targetToken'] in wrappers:
   b=wrappers[i['targetToken']];usages.append({'methodToken':m['token'],'signature':m['signature'],'ilOffset':i['offset'],'kind':'direct-pass-through-wrapper-call','wrapperToken':i['targetToken'],'fieldToken':b['fieldToken'],'targetToken':b['targetToken'],'targetSignature':b['targetSignature'],'callOpcode':b['encodedCallOpcode']})
(P/'resolved-proxy-usages.json').write_text(json.dumps(usages,indent=2))
enumtext=(P.parent.parent/'readable/AIInfluence.DynamicEvents.DiplomaticAction.cs').read_text();names=re.findall(r'^\s*(\w+),?$',enumtext,re.M); names=[x for x in names if x not in ('namespace','public')]
dispatch=M['06002A60'];ins=dispatch['instructions'];sw=next(i for i in ins if i['op']=='switch');routes=[]
for enumValue,off in enumerate(sw['branches'],1):
 block=ins[next(n for n,x in enumerate(ins) if x['offset']==off):];call=next(i for i in block if i['op'] in ('call','callvirt'));helper=M[call['targetToken']]
 direct=[{'ilOffset':i['offset'],'targetToken':i['targetToken'],'signature':i['operand']} for i in helper['instructions'] if i['op'] in ('call','callvirt','newobj') and (i['operand'].startswith(('System.','AIInfluence.','TaleWorlds.'))) and '<Module>' not in i['operand']]
 routes.append({'action':names[enumValue],'enumValue':enumValue,'dispatcher':'06002A60','switchOffset':sw['offset'],'caseEntryOffset':off,'handlerCallOffset':call['offset'],'handlerToken':helper['token'],'directNamedCalls':direct,'resolvedProxyUsages':[x for x in usages if x['methodToken']==helper['token']]})
(P/'diplomatic-routes.json').write_text(json.dumps(routes,indent=2))
reachable={x['source'] for x in json.loads((P/'political-reachable-edges.json').read_text())}
coverage={'boundDelegateFields':len(bs),'decodedTargets':sum(x['targetSignature'] is not None for x in bs),'unresolvedBindings':0,'encodedOpcodes':dict(collections.Counter(x['encodedCallOpcode'] for x in bs)),'directPassthroughWrappers':len(wrappers),'allUsageReferences':len(usages),'politicalReachableUsageReferences':sum(x['methodToken'] in reachable for x in usages),'diplomaticEnumCasesMapped':len(routes),'limitations':['Usages are static references, not evidence of execution or branch feasibility.','Delegate field loads identify a binding, not necessarily the immediately following invocation.','Only exact pass-through wrapper instruction patterns are collapsed; other helper control flow remains in original IL/readable C#.','Dynamic reflection outside decoded binders remains outside this mapping.']}
v=json.loads((P/'proxy-signature-validation.json').read_text())
coverage['signatureValidation']={'checked':len(v),'exactMatches':sum(x['exactSignatureMatch'] for x in v),'mismatches':[x for x in v if not x['exactSignatureMatch']]}
coverage['allIncomingBinderInputEdgesChecked']=sum(x.get('inputFlow',{}).get('allIncomingEdgesChecked',False) for x in bs)
coverage['binderInitializerExceptionRegions']=sum(M[x]['exceptionHandlerCount'] for x in {b['initializer'] for b in bs})
coverage['binderCallsOutsideStaticConstructors']=[{'methodToken':m['token'],'offset':i['offset']} for m in ms if m['name']!='.cctor' for i in m['instructions'] or [] if i['op']=='call' and 'RuntimeFieldHandle,System.Byte' in (i['operand'] or '')]
coverage['calliSites']=[{'methodToken':m['token'],'offset':i['offset']} for m in ms for i in m['instructions'] or [] if i['op']=='calli']
(P/'proxy-coverage.json').write_text(json.dumps(coverage,indent=2));print(json.dumps(coverage,indent=2))
for r in routes:
 print(r['action'],r['handlerToken'],[x['targetSignature'] for x in r['resolvedProxyUsages'] if 'TaleWorlds' in x['targetSignature'] and ('Action::' in x['targetSignature'] or 'Change' in x['targetSignature'])])
