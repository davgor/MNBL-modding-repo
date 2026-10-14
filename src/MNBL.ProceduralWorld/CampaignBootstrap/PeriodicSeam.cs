using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-63: keeps every mobile party canonical. A party that walks off the canonical range into a strip (a copy
    // of the other side of the seam) is moved by +/-Width together with everything that is expressed in its
    // current frame: its path points, NextTargetPosition and NextLongTermPathPoint. Its order target
    // (MoveTargetPoint, PathLastFace, _pathLastPosition) is canonical already and stays, so the engine keeps the
    // computed path instead of recomputing it. The main party's map camera moves with it in the same frame.
    // AI behaviours can still pick a target across the seam (planar offsets around a seam settlement land in a strip), so
    // the same pass moves every saved order target to its canonical image; the next path query routes to the same point.
    public sealed class PeriodicSeamBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents() { CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick); }
        public override void SyncData(IDataStore dataStore) { }
        private void OnTick(float dt) { PeriodicSeam.RebaseAll(); }
    }

    public static class PeriodicSeam
    {
        public sealed class RebaseEvent
        {
            public string Party; public float FromX, ToX, Y, Shift, CampaignHours; public int PathPointsShifted; public bool Camera;
            public List<string> CameraFields;
        }
        // Evidence for the probe; cleared when a new periodic world loads.
        public static readonly List<RebaseEvent> Events = new List<RebaseEvent>();
        public static int Rebases { get; private set; }

        private static readonly FieldInfo PathField = Field(typeof(MobileParty), "Path");
        private static readonly FieldInfo NextTargetField = Field(typeof(MobileParty), "NextTargetPosition");
        private static readonly FieldInfo NextLongTermField = Field(typeof(MobileParty), "NextLongTermPathPoint");
        private static readonly FieldInfo[] TargetFields = {
            Field(typeof(MobileParty), "MoveTargetPoint"), Field(typeof(MobileParty), "_pathLastPosition"),
            Field(typeof(MobileParty), "_targetPosition"), Field(typeof(MobileParty), "<EndPositionForNavigationTransition>k__BackingField") };
        public static int TargetsCanonicalized { get; private set; }

        private static FieldInfo Field(Type owner, string name)
        {
            var f = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(owner.FullName, name);
            return f;
        }

        internal static void Reset() { Events.Clear(); Rebases = 0; TargetsCanonicalized = 0; }

        public static void RebaseAll()
        {
            var world = PeriodicWorld.Current;
            if (world == null || Campaign.Current == null) return;
            foreach (var party in MobileParty.All)
                if (party != null && party.IsActive) { Rebase(world, party); CanonicalTargets(world, party); }
        }

        private static void CanonicalTargets(PeriodicWorld world, MobileParty party)
        {
            foreach (var field in TargetFields)
            {
                var v = (CampaignVec2)field.GetValue(party);
                if (float.IsNaN(v.X) || (v.X == 0 && v.Y == 0) || world.IsCanonical(v.X)) continue;  // unset, or canonical
                field.SetValue(party, new CampaignVec2(new Vec2(world.Canonical(v.X), v.Y), v.IsOnLand));
                TargetsCanonicalized++;
            }
        }

        public static bool Rebase(PeriodicWorld world, MobileParty party)
        {
            var position = party.Position;
            if (!position.IsValid() || world.IsCanonical(position.X)) return false;
            float shift = world.Canonical(position.X) - position.X;
            var e = new RebaseEvent { Party = party.StringId, FromX = position.X, ToX = position.X + shift, Y = position.Y, Shift = shift,
                                      CampaignHours = (float)CampaignTime.Now.ToHours };
            party.Position = new CampaignVec2(new Vec2(position.X + shift, position.Y), position.IsOnLand);
            var path = PathField.GetValue(party) as NavigationPath;
            if (path != null && path.PathPoints != null)
            {
                for (int i = 0; i < path.Size && i < path.PathPoints.Length; ++i)
                    path.PathPoints[i] = new Vec2(path.PathPoints[i].X + shift, path.PathPoints[i].Y);
                e.PathPointsShifted = path.Size;
            }
            ShiftCampaignVec2(NextTargetField, party, shift);
            ShiftCampaignVec2(NextLongTermField, party, shift);
            if (party == MobileParty.MainParty) { e.CameraFields = ShiftCamera(world, position.X, shift); e.Camera = e.CameraFields.Count > 0; }
            Events.Add(e);
            Rebases++;
            return true;
        }

        private static void ShiftCampaignVec2(FieldInfo field, MobileParty party, float shift)
        {
            var value = field.GetValue(party);
            if (value is CampaignVec2)
            {
                var v = (CampaignVec2)value;
                if (v.IsValid()) field.SetValue(party, new CampaignVec2(new Vec2(v.X + shift, v.Y), v.IsOnLand));
            }
            else if (value is Vec2)
            {
                var v = (Vec2)value;
                field.SetValue(party, new Vec2(v.X + shift, v.Y));
            }
        }

        // SandBox.View's MapCameraView (not referenced) lerps toward its target: every positional field near the old
        // party position moves by the same shift so the camera keeps its offset instead of gliding across the map.
        private static List<string> ShiftCamera(PeriodicWorld world, float oldX, float shift)
        {
            var shifted = new List<string>();
            object screen = ScreenManager.TopScreen;
            if (screen == null) return shifted;
            object view = null;
            for (var type = screen.GetType(); type != null && view == null; type = type.BaseType)
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType.Name == "MapCameraView") { view = f.GetValue(screen); break; }
            if (view == null) return shifted;
            for (var type = view.GetType(); type != null && type != typeof(object); type = type.BaseType)
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var value = f.GetValue(view);
                    if (value is Vec3)
                    {
                        var v = (Vec3)value;
                        if (Math.Abs(v.x - oldX) < world.Width / 2) { v.x += shift; f.SetValue(view, v); shifted.Add(f.Name); }
                    }
                    else if (value is Vec2)
                    {
                        var v = (Vec2)value;
                        if (Math.Abs(v.X - oldX) < world.Width / 2) { f.SetValue(view, new Vec2(v.X + shift, v.Y)); shifted.Add(f.Name); }
                    }
                    else if (value is MatrixFrame)
                    {
                        var m = (MatrixFrame)value;
                        if (Math.Abs(m.origin.x - oldX) < world.Width / 2) { m.origin.x += shift; f.SetValue(view, m); shifted.Add(f.Name); }
                    }
                }
            return shifted;
        }
    }
}
