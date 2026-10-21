using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Engine;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-40: a world's read scene's settlement entities against the layout they were built from (PreparedSurface.Settlements), on the engine
    // thread over the inspection world. The rules are SettlementEntityRules (MMR-247: shared with the offline harness, which runs them over
    // the baked scene XML); this is the engine's side of them: the read scene's entities as ICheckedEntity.
    internal static class SettlementEntityChecks
    {
        public static Dictionary<string, object> Run(RuntimeWorld world, PreparedSurface prepared)
        {
            var report = new Dictionary<string, object>();
            foreach (var step in Steps(world, prepared, report)) { }
            return report;
        }

        // MMR-258: the check in steps, one settlement each (the runtime stage gives the engine thread back between slices of them, as it does
        // for the navigation routes: a look's hierarchy has more children to read). `report` is complete when the steps end.
        public static IEnumerable<bool> Steps(RuntimeWorld world, PreparedSurface prepared, Dictionary<string, object> report)
        {
            var model = prepared.NavigationPlan.Model;
            return SettlementEntityRules.Steps(new EngineScene(world), prepared.Settlements, prepared.SettlementGround, g => model.IsLand(g), SettlementPlacementPolicy.Installed(),
                prepared.Lakes.Count, RuntimeWorldBackend.HelperPrefix, GeneratedWaterSystem.Tag, SettlementEntityContract.SiegeCacheScript, prepared.SettlementReportPath, report);
        }

        private sealed class EngineScene : ICheckedScene
        {
            private readonly RuntimeWorld world;
            public EngineScene(RuntimeWorld world) { this.world = world; }
            public bool HasEntityNamed(string name) { return world.Scene.GetFirstEntityWithName(name) != null; }
            public bool HasScript(string name) { return world.Scene.GetFirstEntityWithScriptComponent(name) != null; }
            public ICheckedEntity CampaignEntity(string name) { var e = world.Scene.GetCampaignEntityWithName(name); return e == null ? null : new EngineEntity(e); }
            public IEnumerable<ICheckedEntity> RootEntities()
            {
                var all = new List<GameEntity>(); world.Scene.GetEntities(ref all);
                return all.Where(x => x.Parent == null).Select(x => (ICheckedEntity)new EngineEntity(x)).ToList();
            }
            public int RootEntityCount { get { return world.Scene.RootEntityCount; } }
            public bool NavFace(double x, double y, bool land, out int face, out int group) { int island; return world.NavFace(x, y, land, out face, out group, out island); }
        }

        private sealed class EngineEntity : ICheckedEntity
        {
            private readonly GameEntity e;
            public EngineEntity(GameEntity e) { this.e = e; }
            public string Name { get { return e.Name; } }
            public IEnumerable<string> Tags { get { return e.Tags ?? new string[0]; } }
            public bool HasTag(string tag) { return e.HasTag(tag); }
            public bool HasScript(string name) { return e.HasScriptComponent(name); }
            public int ChildCount { get { return e.ChildCount; } }
            public ICheckedEntity GetChild(int index) { return new EngineEntity(e.GetChild(index)); }
            public double[] GlobalPosition { get { var p = e.GlobalPosition; return new double[] { p.x, p.y, p.z }; } }
            public uint UpgradeLevelMask { get { return (uint)e.GetUpgradeLevelMask(); } }
        }
    }
}
