using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-40: builds a settlement's EntitySpec as live entities, for a campaign settlement the world's scene was not authored with (an MMR-34
    // hideout: hideouts are placed from the campaign build, after the scene is encoded). SettlementVisual.OnStartup asks for it through
    // IMapScene.AddNewEntityToMapScene (GeneratedMapScene). Tags, meshes, frames (yaw and scale), scene levels (the upgrade level mask of the
    // levels the scene XML would name, SettlementEntityFactory.SceneLevels), the raycast collider and the editor-only volume children are made
    // as the scene XML would; scripts are added by name (CreateAndAddScriptComponent), which binds the managed ones (BlockadePositionScript) and
    // skips names the engine does not know. Settlements whose children anything reads earlier (War Sails reads village drop-offs when its
    // wrapper initializes) must be baked into the scene instead (SettlementEntityFactory).
    internal static class SettlementEntityRuntime
    {
        public static GameEntity Create(Scene scene, EntitySpec spec, List<string> notes)
        {
            var entity = Build(scene, spec, null, notes);
            entity.SetReadyToRender(true);
            return entity;
        }

        private static GameEntity Build(Scene scene, EntitySpec spec, GameEntity parent, List<string> notes)
        {
            // MMR-258: a look's entities exist only as scene XML (the run-time path builds MMR-40's pieces from a record, never a look).
            if (spec.Raw.Count > 0) throw new InvalidOperationException("The run-time settlement " + spec.Name + " carries look entities, which only a baked scene holds.");
            var entity = GameEntity.CreateEmpty(scene, false, true, true);
            entity.Name = spec.Name;
            foreach (var tag in spec.Tags) entity.AddTag(tag);
            if (spec.Mesh != null)
            {
                var mesh = MetaMesh.GetCopy(spec.Mesh, false, true);
                if (mesh != null) entity.AddMultiMesh(mesh, true); else notes.Add("mesh " + spec.Mesh + " of " + spec.Name + " is not loaded");
            }
            if (spec.Physics != null) entity.AddSphereAsBody(Vec3.Zero, 1f, spec.BodyFlags.Contains("only_collide_with_raycast") ? BodyFlags.OnlyCollideWithRaycast : BodyFlags.None);
            foreach (var script in spec.Scripts)
            {
                try { entity.CreateAndAddScriptComponent(script.Key, true); }
                catch (Exception error) { notes.Add("script " + script.Key + " on " + spec.Name + ": " + error.GetType().Name); }
            }
            var frame = MatrixFrame.Identity;
            frame.origin = new Vec3((float)spec.Position[0], (float)spec.Position[1], (float)spec.Position[2]);
            if (spec.Yaw != 0) frame.rotation.RotateAboutUp((float)spec.Yaw);
            if (spec.Scale != null) frame.rotation.ApplyScaleLocal(new Vec3((float)spec.Scale[0], (float)spec.Scale[1], (float)spec.Scale[2]));
            if (parent == null) entity.SetGlobalFrame(in frame, true);
            else { parent.AddChild(entity, false); entity.SetLocalFrame(ref frame, true); }
            if (spec.Levels.Count > 0) entity.SetUpgradeLevelMask((GameEntity.UpgradeLevelMask)SettlementEntityFactory.LevelMask(spec.Levels));
            if (spec.EditorOnly) entity.SetVisibilityExcludeParents(false);
            foreach (var child in spec.Children) Build(scene, child, entity, notes);
            return entity;
        }
    }
}
