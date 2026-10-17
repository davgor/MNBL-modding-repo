using System;
using NavalDLC.ComponentInterfaces;
using NavalDLC.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.CampaignBootstrap;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-39: War Sails' storms on a generated world (GeneratedStormPolicy, docs/water-climate.md). Chained over War Sails' own storm model
    // (BaseModel), it delegates everything except:
    //   * the hourly spawn chance: scaled by the area a generated weather node stands for, and zero outside the canonical range between the
    //     pole lines of a periodic world (the first gate of StormCampaignBehavior.TrySpawningNewStorm);
    //   * the damage at a position, and a position's storm wind strength: measured from the position's copy nearest the storm, so a storm
    //     reaches parties across the seam exactly as it reaches its own side (War Sails measures planar distance).
    // The non-virtual planar checks (storm collisions, spawn separation, wet-weather and storm-rider tests) are not reached; their error is
    // limited to storms within one effect radius (at most about 48) of the seam (recorded in docs/water-climate.md).
    internal sealed class GeneratedStormModel : MapStormModel
    {
        private double scale = -1;

        internal MapStormModel Installed { get { return BaseModel; } }
        public static int SuppressedSpawnChecks { get; private set; }
        public static int CopiedDamageQueries { get; private set; }
        internal static void ResetCounters() { SuppressedSpawnChecks = 0; CopiedDamageQueries = 0; }

        // The spawn-chance scale for the active map: its weather nodes' spacing against Calradia's.
        internal double SpawnChanceScale
        {
            get
            {
                if (scale < 0)
                {
                    var campaign = Campaign.Current;
                    float extent = campaign == null || campaign.MapSceneWrapper == null ? 0 : campaign.MapSceneWrapper.GetTerrainSize().X;
                    int dimension = campaign == null ? 0 : campaign.DefaultWeatherNodeDimension;
                    if (!(extent > 0) || dimension <= 0) return 1;
                    scale = GeneratedStormPolicy.SpawnChanceScale(extent, dimension);
                }
                return scale;
            }
        }

        private static PeriodicWorldCoordinates Coordinates { get { var world = PeriodicWorld.Current; return world == null ? null : world.Coordinates; } }

        public override float GetHourlyStormSpawnChanceForPosition(Vec2 position)
        {
            if (!GeneratedStormPolicy.CanSpawnAt(Coordinates, position.X, position.Y)) { SuppressedSpawnChecks++; return 0f; }
            return (float)(BaseModel.GetHourlyStormSpawnChanceForPosition(position) * SpawnChanceScale);
        }

        public override float GetPositionDamageForStorm(Storm storm, Vec2 position, Ship ship)
        {
            var c = Coordinates;
            if (c != null && storm != null)
            {
                float x = (float)GeneratedStormPolicy.NearestCopyX(c, storm.CurrentPosition.X, position.X);
                if (x != position.X) { CopiedDamageQueries++; position = new Vec2(x, position.Y); }
            }
            return BaseModel.GetPositionDamageForStorm(storm, position, ship);
        }

        // A storm sits in the canonical range; a position near the seam may be nearer to it through a copy one width away.
        public override float GetNormalizedWindStrengthOfStormForPosition(Vec2 position)
        {
            float best = BaseModel.GetNormalizedWindStrengthOfStormForPosition(position);
            var c = Coordinates;
            if (c == null) return best;
            foreach (int k in new[] { 1, -1 })
            {
                double x = position.X + k * c.Width;
                if (x < 0 || x > c.Extent) continue;
                best = Math.Max(best, BaseModel.GetNormalizedWindStrengthOfStormForPosition(new Vec2((float)x, position.Y)));
            }
            return best;
        }

        public override float MinimumWeatherStrengthInsideStorm { get { return BaseModel.MinimumWeatherStrengthInsideStorm; } }
        public override int MaximumNumberOfStorms { get { return BaseModel.MaximumNumberOfStorms; } }
        public override float GetHourlyIntensityChangeForStorm(Storm storm) { return BaseModel.GetHourlyIntensityChangeForStorm(storm); }
        public override void GetStormLifeSpan(out CampaignTime minimumLifeSpan, out CampaignTime maximumLifeSpan) { BaseModel.GetStormLifeSpan(out minimumLifeSpan, out maximumLifeSpan); }
        public override CampaignTime GetDevelopingStateDurationOfStorm(Storm storm) { return BaseModel.GetDevelopingStateDurationOfStorm(storm); }
        public override CampaignTime GetFinalizingStateDurationOfStorm(Storm storm) { return BaseModel.GetFinalizingStateDurationOfStorm(storm); }
        public override Storm.StormTypes GetSpawnedStormTypeForPosition(Vec2 position) { return BaseModel.GetSpawnedStormTypeForPosition(position); }
        public override bool CanPartyGetDamagedByStorm(MobileParty mobileParty) { return BaseModel.CanPartyGetDamagedByStorm(mobileParty); }
        public override float GetEffectRadiusOfStorm(Storm storm) { return BaseModel.GetEffectRadiusOfStorm(storm); }
        public override float GetEyeRadiusOfStorm(Storm storm) { return BaseModel.GetEyeRadiusOfStorm(storm); }
        public override float GetSpeedOfStorm(Storm storm) { return BaseModel.GetSpeedOfStorm(storm); }
        public override float GetMaximumWeatherStrengthAtEye(Storm storm) { return BaseModel.GetMaximumWeatherStrengthAtEye(storm); }
        public override float GetStormSpawnDistanceSquaredThresholdWithOtherStorms() { return BaseModel.GetStormSpawnDistanceSquaredThresholdWithOtherStorms(); }
    }
}
