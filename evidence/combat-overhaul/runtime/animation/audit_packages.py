import os,sys,pathlib,json,struct
root=pathlib.Path(__file__).resolve().parent
os.environ['BANNERLORD_DIR']=r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
os.environ['BANNERLORD_SDK_HOME']=str(root/'sdk-scratch')
os.environ['BANNERLORD_MODULE_DIR']=str(root/'sdk-scratch'/'MNBL.CombatProbe')
sys.path.insert(0,str(root/'editor-free-sdk'/'sdk'))
import tpac,templates,tw_formats,validate
stage=root/'sdk-scratch'
clip=tw_formats.walk_package((stage/'Assets/mnbl_probe_sweep_anm.tpac').read_bytes())[0]
geo=tw_formats.walk_package((stage/'Assets/mnbl_probe_sweep_geo.tpac').read_bytes())
anim=next(r for r in geo if r['type']==tpac.ANIM_TYPE)
source=next(r for r in geo if r['type']==tpac.IMPORT_TYPE)
rec=clip['record'];template=templates.clip_template()['record'];parsed=tpac.parse_clip(rec,0)
diff=[i for i,(a,b) in enumerate(zip(rec,template)) if a!=b]
assert all(12<=i<24 or 40<=i<56 for i in diff),diff
assert all(not parsed[k] for k in ('sound','voice','facial','blends_with','continue_with','combat','anim2'))
assert parsed['lists']==bytes(8)
assert rec[40:56]==anim['guid']
assert anim['record'][12:28]==source['guid']
owner=anim['record'][29:45]
native=validate.native_skeletons()
skel=next((name,bones) for name,(guid,bones) in native.items() if guid==owner)
receipt=json.loads((root/'authoring-receipt.json').read_text())
names=[n for n in receipt['boneNames'] if not n.endswith('notused')]
mapped=sorted([(int(n.rsplit('_',1)[1]),n) for n in names])
assert [i for i,n in mapped]==list(range(28))
assert len(skel[1])==28
neutralMatches=[]
def neutral(r):
 r=bytearray(r);r[12:24]=bytes(12);r[40:56]=bytes(16);return bytes(r)
for path,it in templates._native_items(['EmAssetPackages/animations'],tpac.CLIP_TYPE):
 if templates._plain_clip(it['record']) and neutral(it['record'])==neutral(template):
  neutralMatches.append({'name':it['name'],'package':str(path)})
errors=validate.validate(str(stage));assert not errors,errors
out={'status':'static writer audit only; engine load unproven','changedOffsetsFromPlainTemplate':diff,'onlyAllowedChangedRanges':[[12,24],[40,56]],'emptyMetadata':['sound','voice','facial','blends_with','continue_with','combat','anim2','flagList','parameterList'],'remainingNeutralFields':{k:(v.hex() if isinstance(v,(bytes,bytearray)) else v) for k,v in parsed.items() if k not in ('fixed','lists')},'nativeClipsWithIdenticalNeutralMetadata':neutralMatches,'guidChain':{'clipGuid':clip['guid'].hex(),'clipAnimationGuid':rec[40:56].hex(),'animationGuid':anim['guid'].hex(),'animationImportGuid':anim['record'][12:28].hex(),'importGuid':source['guid'].hex(),'ownerSkeletonGuid':owner.hex(),'ownerSkeletonName':skel[0]},'runtimeBoneCount':len(skel[1]),'runtimeBoneMapping':mapped,'removedHelperBones':[n for n in receipt['boneNames'] if n.endswith('notused')],'validationErrors':errors}
(stage/'audit.json').write_text(json.dumps(out,indent=2));print(json.dumps({'guidChain':out['guidChain'],'boneCount':len(mapped),'matchingNeutralClips':len(neutralMatches),'humanCandidates':[n for n in neutralMatches if any(s in n['name'] for s in ('idle','stand','walk'))][:10]}))
