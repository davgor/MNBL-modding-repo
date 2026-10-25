import pathlib,json,hashlib
base=pathlib.Path(__file__).resolve().parent
events=[json.loads(x) for x in (base/'run-30840.jsonl').read_text().splitlines() if x.strip()]
samples=[e['data'] for e in events if e['name']=='custom_animation_sample']
matching=[s for s in samples if s['action']==5167]
ticks=[s for s in matching if s['phase']=='tick']
duration=(ticks[-1]['elapsed']-ticks[1]['elapsed'])/(ticks[-1]['progress']-ticks[1]['progress'])
module=pathlib.Path(r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules\MNBL.CombatProbe')
writer=json.loads((base/'sdk-writer-result.json').read_text())
files={}
for rel,expected in writer['files'].items():
 p=module/rel;actual=hashlib.sha256(p.read_bytes()).hexdigest()
 files[rel]={'expectedSha256':expected,'observedSha256':actual,'matches':actual==expected}
assert all(x['matches'] for x in files.values()),files
for name in ('action_sets.xml','action_types.xml','project.mbproj'):
 p=module/'ModuleData'/name;files['ModuleData/'+name]={'observedSha256':hashlib.sha256(p.read_bytes()).hexdigest(),'retainedCopyMatches':p.read_bytes()==(base/'ModuleData'/name).read_bytes()}
evidence={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in base.glob('run-30840*') if p.is_file()}
out={'runPid':30840,'status':'basic original custom animation playback passed; production combat integration unproven','matchingSamples':len(matching),'matchingTickSamples':len(ticks),'inferredDurationFromProgressSeconds':duration,'lastCustomSample':ticks[-1],'firstCompletedSample':next(s for s in samples if s['phase']=='tick' and s['action']==-1),'restoration':next(e['data'] for e in events if e['name']=='custom_animation_restored'),'moduleMvid':next(e['data']['mvid'] for e in events if e['name']=='loaded'),'packageHashCheck':'Read-only comparison after run; not an execution-time asset-read trace','installedModule':str(module),'files':files,'evidenceSha256':evidence}
(base/'run-30840-review.json').write_text(json.dumps(out,indent=2))
print(json.dumps({'matching':len(matching),'ticks':len(ticks),'duration':duration,'allPackagesMatch':True}))
