import bpy,math,json,pathlib,hashlib
from mathutils import Quaternion,Vector
out=pathlib.Path(r'C:\Users\davgo\Documents\GitHub\MNBL-modding-repo\build\combat-feasibility\animation')
source=pathlib.Path(r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\modding_resources\skeletons\human_skeleton.fbx')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(source),use_anim=False)
rig=next(o for o in bpy.context.scene.objects if o.type=='ARMATURE')
bpy.context.view_layer.objects.active=rig
rig.animation_data_create()
action=bpy.data.actions.new('mnbl_probe_sweep')
rig.animation_data.action=action
scene=bpy.context.scene;scene.render.fps=30;scene.frame_start=1;scene.frame_end=61
# An original, deliberately broad two-second diagnostic torso/right-arm sweep.
# No stock animation keys are read; only the supplied bind rig is reused.
poses={1:(0,0,0,0),12:(-25,-45,15,20),25:(-25,-45,15,20),36:(30,65,-20,40),43:(30,65,-20,40),61:(0,0,0,0)}
for frame,(torso,arm,shoulder,elbow) in poses.items():
 for p in rig.pose.bones:
  p.rotation_mode='QUATERNION';p.rotation_quaternion=Quaternion((1,0,0,0));p.location=(0,0,0);p.scale=(1,1,1)
 rotations={
 'bip01_spine_10':(0,torso*0.35,0),
 'bip01_spine2_11':(0,torso*0.65,0),
 'bip01_r_clavicle_21':(0,shoulder,0),
 'bip01_r_upperarm_twist_22':(arm,0,shoulder),
 'bip01_r_upperarm_twist1_23':(arm*0.1,0,0),
 'bip01_r_foretwist_24':(0,0,elbow),
 'bip01_l_clavicle_14':(0,-shoulder*0.35,0),
 }
 for name,(x,y,z) in rotations.items():
  p=rig.pose.bones.get(name)
  if p:p.rotation_quaternion=Quaternion((1,0,0),math.radians(x))@Quaternion((0,1,0),math.radians(y))@Quaternion((0,0,1),math.radians(z))
 for p in rig.pose.bones:
  p.keyframe_insert(data_path='rotation_quaternion',frame=frame)
  p.keyframe_insert(data_path='location',frame=frame)
  p.keyframe_insert(data_path='scale',frame=frame)
scene.frame_set(1)
blend=out/'mnbl_probe_sweep.blend';bpy.ops.wm.save_as_mainfile(filepath=str(blend))
fbx=out/'mnbl_probe_sweep.fbx'
settings=dict(use_selection=True,object_types={'ARMATURE'},add_leaf_bones=False,primary_bone_axis='Y',secondary_bone_axis='X',use_armature_deform_only=False,armature_nodetype='NULL',bake_anim=True,bake_anim_use_all_bones=True,bake_anim_use_nla_strips=False,bake_anim_use_all_actions=False,bake_anim_force_startend_keying=True,bake_anim_step=1.0,bake_anim_simplify_factor=0.0,axis_forward='-Y',axis_up='Z',global_scale=1.0,apply_unit_scale=True,apply_scale_options='FBX_SCALE_NONE',use_space_transform=True)
bpy.ops.object.select_all(action='DESELECT');rig.select_set(True)
bpy.ops.export_scene.fbx(filepath=str(fbx),**settings)
samples={}
for f in (1,12,25,36,43,61):
 scene.frame_set(f);samples[str(f)]={name:list(rig.pose.bones[name].matrix.translation) for name in ('bip01_r_hand_26','bip01_l_hand_19','bip01_spine2_11')}
record={'status':'Authored and exported, engine import/playback pending','blender':bpy.app.version_string,'referenceSkeleton':str(source),'referenceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'armatureName':rig.name,'boneCount':len(rig.data.bones),'boneNames':[b.name for b in rig.data.bones],'fps':30,'startFrame':1,'endFrame':61,'durationSeconds':2,'authorship':'Original rotation keyframes scripted here; supplied bind skeleton only, no stock animation motion','phaseFrames':{'windup':[1,25],'sweep':[25,43],'recovery':[43,61]},'exportSettings':{k:sorted(v) if isinstance(v,set) else v for k,v in settings.items()},'samplesArmatureSpace':samples,'files':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (blend,fbx)}}
(out/'authoring-receipt.json').write_text(json.dumps(record,indent=2))
print(json.dumps({'boneCount':record['boneCount'],'files':record['files'],'samples':samples}))
