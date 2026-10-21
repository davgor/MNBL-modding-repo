using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using MNBL.ProceduralWorld.World;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-254: the one party speed model of a generated world (docs/runtime-navigation.md "Land face types and speed"; findings sections 3 and
    // 8). Added with CampaignGameStarter.AddModel<PartySpeedModel> after War Sails' own (its InitializeGameStarter ran in the base), so the chain
    // is this -> NavalDLCPartySpeedCalculationModel -> DefaultPartySpeedCalculatingModel (roads.json speed.chain); no Harmony. Base and minimum
    // speed and the base speed are the installed chain's; the final speed is the installed chain's plus RoadSpeed's terms for the party's
    // current face (its TerrainType, read as every installed consumer reads it) and its cell's road bit, then the installed minimum again.
    // The world's road cells come from its bundle (RoadSources, by scene, registered with the battle terrain context before the campaign
    // starts or loads); the map scene activates its own on load (Activate) and clears it when destroyed.
    internal sealed class GeneratedPartySpeedModel : PartySpeedModel
    {
        public static readonly Dictionary<string, RoadSpeed.Cells> RoadSources = new Dictionary<string, RoadSpeed.Cells>(StringComparer.Ordinal);
        private static RoadSpeed.Cells active;
        internal static RoadSpeed.Cells Active { get { return active; } }
        internal static void Activate(string sceneName) { RoadSpeed.Cells cells; active = sceneName != null && RoadSources.TryGetValue(sceneName, out cells) ? cells : null; }
        internal static void Deactivate() { active = null; }

        private static readonly TextObject[] Texts = {
            new TextObject("{=mnbl_speed_terrain}Terrain"), new TextObject("{=mnbl_speed_road}Road"), new TextObject("{=mnbl_speed_road_bridge}Bridge on a road") };
        private readonly RoadSpeed terms;
        // CalculateFinalSpeed runs on TWParallel workers (CampaignTickCacheDataStore's parallel movement ticks -> CalculateSpeedForPartyUnified),
        // so the scratch terms are per thread, not the model's: shared arrays would hand one party another's factors.
        [ThreadStatic] private static float[] tFactors;
        [ThreadStatic] private static int[] tKinds;

        public GeneratedPartySpeedModel(RoadsPolicy policy)
        {
            terms = new RoadSpeed(policy, InstalledFordFactor(), name => (int)Enum.Parse(typeof(TerrainType), name, false), MaxTerrain());
        }

        internal PartySpeedModel Installed { get { return BaseModel; } }
        internal RoadSpeed Terms { get { return terms; } }

        // The installed ford factor (DefaultPartySpeedCalculatingModel.MovingAtWaterEffect, -0.3 in 1.4.8), which a road deck cancels.
        internal static float InstalledFordFactor()
        {
            var field = typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultPartySpeedCalculatingModel).GetField("MovingAtWaterEffect", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field == null || !field.IsLiteral) throw new MissingFieldException("DefaultPartySpeedCalculatingModel", "MovingAtWaterEffect");
            return (float)field.GetRawConstantValue();
        }
        private static int MaxTerrain() { int max = 0; foreach (var v in Enum.GetValues(typeof(TerrainType))) max = Math.Max(max, (int)v); return max; }

        public override float BaseSpeed { get { return BaseModel.BaseSpeed; } }
        public override float MinimumSpeed { get { return BaseModel.MinimumSpeed; } }

        public override ExplainedNumber CalculateBaseSpeed(MobileParty party, bool includeDescriptions = false, int additionalTroopOnFootCount = 0, int additionalTroopOnHorseCount = 0)
        {
            return BaseModel.CalculateBaseSpeed(party, includeDescriptions, additionalTroopOnFootCount, additionalTroopOnHorseCount);
        }

        public override ExplainedNumber CalculateFinalSpeed(MobileParty mobileParty, ExplainedNumber finalSpeed)
        {
            var factors = tFactors ?? (tFactors = new float[3]); var kinds = tKinds ?? (tKinds = new int[3]);
            var speed = BaseModel.CalculateFinalSpeed(mobileParty, finalSpeed);
            var campaign = Campaign.Current;
            if (mobileParty == null || campaign == null || campaign.MapSceneWrapper == null) return speed;
            var face = mobileParty.CurrentNavigationFace;
            if (!face.IsValid()) return speed;
            int type = (int)campaign.MapSceneWrapper.GetFaceTerrainType(face);
            var cells = active;
            bool road = false;
            if (cells != null) { var p = mobileParty.Position.ToVec2(); road = cells.At(p.X, p.Y); }
            int n = terms.Terms(type, mobileParty.IsCurrentlyAtSea, road, factors, kinds);
            if (n == 0) return speed;
            for (int k = 0; k < n; k++) speed.AddFactor(factors[k], Texts[kinds[k]]);
            speed.LimitMin(MinimumSpeed);
            return speed;
        }
    }
}
