import bpy,json,pathlib,hashlib
from mathutils import Vector
out=pathlib.Path(__file__).resolve().parent
receipt=json.loads((out/'authoring-receipt.json').read_text())
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(out/'mnbl_probe_sweep.fbx'),use_anim=True,anim_offset=0.0)
rig=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE')
samples={};errors=[]
for f in (1,12,25,36,43,61):
 bpy.context.scene.frame_set(f)
 samples[str(f)]={name:list(rig.pose.bones[name].matrix.translation) for name in ('bip01_r_hand_26','bip01_l_hand_19','bip01_spine2_11')}
 for name,xyz in samples[str(f)].items():
  errors.append((Vector(xyz)-Vector(receipt['samplesArmatureSpace'][str(f)][name])).length)
result={'status':'FBX roundtrip validated; engine import and playback still unproven','boneNamesPreserved':sorted(receipt['boneNames'])==sorted(b.name for b in rig.data.bones),'boneCount':len(rig.data.bones),'maxArmatureSpacePositionError':max(errors),'armatureScale':list(rig.scale),'actionNames':[a.name for a in bpy.data.actions],'samples':samples}
assert result['boneNamesPreserved']
assert max(errors)<0.0001, result
(out/'roundtrip-validation.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
