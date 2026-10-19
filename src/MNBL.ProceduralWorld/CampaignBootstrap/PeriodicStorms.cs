using System;
using System.Collections.Generic;
using System.Reflection;
using NavalDLC;
using NavalDLC.Map;
using TaleWorlds.Library;
using MNBL.ProceduralWorld.Surface;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-39: keeps War Sails' storms canonical on a periodic world, as PeriodicSeam keeps parties (GeneratedStormPolicy). A storm that drifts
    // out of the canonical range into a strip is moved by whole widths together with its wet-weather trail, one that drifts past a pole line
    // is reflected back (the calm polar padding would otherwise carry it south for the rest of its life), and its visual is marked dirty;
    // the move writes the storm's private position directly (Storm.set_CurrentPosition would test the map borders), so the storm is neither
    // ended at the terrain edge nor ever present twice. Positions are canonical whenever the game saves (the storm list is saved).
    public static class PeriodicStorms
    {
        public sealed class RebaseEvent { public Storm Storm; public string Kind; public float FromX, ToX, Y, ToY, Shift; public int TrailPointsShifted; }
        public static readonly List<RebaseEvent> Events = new List<RebaseEvent>();
        private static readonly FieldInfo Position = Field(typeof(Storm), "_currentPosition");
        private static readonly FieldInfo Trail = Field(typeof(Storm), "_previousPositionsAndRadius");
        private static readonly FieldInfo TrailPosition = Field(typeof(Storm).GetNestedType("PreviousData", BindingFlags.Public | BindingFlags.NonPublic), "Position");
        private static readonly FieldInfo TrailRadius = Field(typeof(Storm).GetNestedType("PreviousData", BindingFlags.Public | BindingFlags.NonPublic), "EffectRadius");
        private static readonly FieldInfo CurrentDirection = Field(typeof(Storm), "_currentMoveDirection");
        private static readonly FieldInfo DesiredDirection = Field(typeof(Storm), "_desiredMoveDirection");

        private static FieldInfo Field(Type owner, string name)
        {
            var f = owner == null ? null : owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(owner == null ? "NavalDLC.Map.Storm/PreviousData" : owner.FullName, name);
            return f;
        }

        internal static void Reset() { Events.Clear(); }

        public static void RebaseAll(PeriodicWorld world)
        {
            var manager = NavalDLCManager.Instance == null ? null : NavalDLCManager.Instance.StormManager;
            if (world == null || manager == null) return;
            foreach (var storm in manager.SpawnedStorms) Rebase(world, storm);
        }

        // A storm out of the canonical range is moved by whole widths; one past a pole line is reflected back across it with its move direction
        // turned towards the equator (GeneratedStormPolicy). The trail moves with it.
        public static bool Rebase(PeriodicWorld world, Storm storm)
        {
            var c = world.Coordinates;
            var at = (Vec2)Position.GetValue(storm);
            double shift = GeneratedStormPolicy.RebaseShift(c, at.X), y = GeneratedStormPolicy.PoleReflectY(c, at.Y);
            bool reflect = y != at.Y;
            if (shift == 0 && !reflect) return false;
            Position.SetValue(storm, new Vec2((float)(at.X + shift), (float)y));
            if (reflect)
            {
                float sign = at.Y < c.OriginY ? 1f : -1f;   // +y runs south: past the north line turn south, past the south line north
                foreach (var field in new[] { CurrentDirection, DesiredDirection })
                {
                    var d = (Vec2)field.GetValue(storm);
                    field.SetValue(storm, new Vec2(d.X, sign * Math.Abs(d.Y)));
                }
            }
            int moved = 0;
            var trail = (Array)Trail.GetValue(storm);
            if (trail != null)
                for (int i = 0; i < trail.Length; i++)
                {
                    object entry = trail.GetValue(i);
                    if (!((float)TrailRadius.GetValue(entry) > 0)) continue;
                    var p = (Vec2)TrailPosition.GetValue(entry);
                    TrailPosition.SetValue(entry, new Vec2((float)(p.X + shift), (float)GeneratedStormPolicy.PoleReflectY(c, p.Y)));
                    trail.SetValue(entry, i);
                    moved++;
                }
            storm.SetVisualDirty();
            Events.Add(new RebaseEvent { Storm = storm, Kind = shift != 0 && reflect ? "seam+pole" : shift != 0 ? "seam" : "pole", FromX = at.X, ToX = (float)(at.X + shift),
                Y = at.Y, ToY = (float)y, Shift = (float)shift, TrailPointsShifted = moved });
            return true;
        }
    }
}
