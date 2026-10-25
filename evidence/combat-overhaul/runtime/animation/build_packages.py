import os,sys,pathlib,json
root=pathlib.Path(__file__).resolve().parent
os.environ['BANNERLORD_DIR']=r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
os.environ['BANNERLORD_SDK_HOME']=str(root/'sdk-scratch')
os.environ['BANNERLORD_MODULE_DIR']=str(root/'sdk-scratch'/'MNBL.CombatProbe')
stage=root/'sdk-scratch'/'Assets';stage.mkdir(parents=True,exist_ok=True)
os.environ['TPAC_STAGE']=str(stage)
sys.path.insert(0,str(root/'editor-free-sdk'/'sdk'))
import tpac,templates,tw_formats
out,take,keys=tpac.write_anim_package(str(root/'mnbl_probe_sweep.fbx'),str(stage),force=True,name='mnbl_probe_sweep_data')
print('ANIMATION',out,take,keys)
clip,duration,end=tpac.write_clip('mnbl_probe_sweep',take,61,30,out_dir=str(stage),src_range=(0,60))
print('CLIP',clip,duration,end)
cache=tpac.write_clip_cache(clip,str(root/'sdk-scratch'/'RuntimeDataCache'))
print('CACHE',cache[0], 'frames',cache[1]['B'])
import validate,selftest,hashlib
errors=validate.validate(str(root/'sdk-scratch'))
assert not errors,errors
roundtrips={p.name:selftest.rebuild_identical(str(p))[0] for p in stage.glob('*.tpac')}
assert all(roundtrips.values()),roundtrips
assert cache[1]['B']==61,cache[1]['B']
(root/'sdk-scratch'/'result.json').write_text(json.dumps({'animation':out,'take':take,'keys':keys,'clip':clip,'duration':duration,'endKey':end,'template':templates.clip_template()['source'],'validationErrors':errors,'roundtripIdentical':roundtrips,'cacheFrames':cache[1]['B'],'files':{str(p.relative_to(root/'sdk-scratch')):hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'sdk-scratch').rglob('*') if p.suffix in ('.tpac','.rdc')},'status':'writer output only; engine load unproven'},indent=2))
