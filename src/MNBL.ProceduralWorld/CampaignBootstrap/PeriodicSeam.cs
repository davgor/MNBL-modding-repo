using System;
using System.Collections.Generic;
using System.Linq;
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
    // computed path instead of recomputing it. The map camera needs no move (MMR-66: it follows the party's image nearest it
    // and rebases itself, PeriodicMapCameraView).
    // AI behaviours can still pick a target across the seam (planar offsets around a seam settlement land in a strip), so
    // the same pass moves every saved order target to its canonical image; the next path query routes to the same point.
    public sealed class PeriodicSeamBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            CampaignEvents.OnBeforeSaveEvent.AddNonSerializedListener(this, OnBeforeSave);   // MMR-49
        }
        public override void SyncData(IDataStore dataStore) { }
        // MMR-49: every saved position canonical at the instant of the save (PeriodicSeam.CanonicalizeForSave).
        private void OnBeforeSave() { if (PeriodicWorld.Current != null && PeriodicPatches.IsApplied) PeriodicSeam.CanonicalizeForSave(); }
        private bool frozen;
        // MMR-64: a periodic world whose patches did not apply would run half periodic (periodic paths, planar proximity and distances);
        // it is frozen instead: campaign time stops and stays locked, and the reason is shown once.
        private void OnTick(float dt)
        {
            if (PeriodicWorld.Current != null && !PeriodicPatches.IsApplied)
            {
                var campaign = Campaign.Current;
                if (campaign != null) { campaign.TimeControlMode = CampaignTimeControlMode.Stop; campaign.SetTimeControlModeLock(true); }
                if (!frozen)
                {
                    frozen = true;
                    var text = new TaleWorlds.Localization.TextObject(Persistence.LoadPolicy.Texts.PeriodicFrozen);
                    text.SetTextVariable("REASON", (PeriodicPatches.LastError ?? "unknown").Split('\n')[0]);
                    InformationManager.DisplayMessage(new InformationMessage(text.ToString()));
                }
                return;
            }
            PeriodicSeam.RebaseAll();
        }
    }

    public static class PeriodicSeam
    {
        public sealed class RebaseEvent
        {
            public string Party; public float FromX, ToX, Y, Shift, CampaignHours; public int PathPointsShifted;
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
        // MMR-49: MobilePartyAi.BehaviorTarget (a saved CampaignVec2 the per-tick pass did not cover) and the War Sails anchor's saved positions.
        private static readonly FieldInfo BehaviorTargetField = Field(typeof(MobilePartyAi), "BehaviorTarget");
        private static readonly FieldInfo DisembarkField = Field(typeof(TaleWorlds.CampaignSystem.Naval.AnchorPoint), "_lastUsedDisembarkPosition");
        private static readonly PropertyInfo AnchorPositionProperty = typeof(TaleWorlds.CampaignSystem.Naval.AnchorPoint).GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo AnchorTargetProperty = typeof(TaleWorlds.CampaignSystem.Naval.AnchorPoint).GetProperty("TargetPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        // MMR-49 review: a map event's position (saved; set where the event began, possibly in a strip) and the map tracks' (saved; laid where a
        // party passed), with the tracks' locator, which indexes them by position.
        private static readonly FieldInfo MapEventPositionField = Field(typeof(TaleWorlds.CampaignSystem.MapEvents.MapEvent), "<Position>k__BackingField");
        private static readonly FieldInfo AllTracksField = Field(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior), "_allTracks");
        private static readonly FieldInfo TrackLocatorField = Field(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior), "_trackLocator");
        public static int SavePasses { get; private set; }
        public static int SaveFieldsCanonicalized { get; private set; }

        private static FieldInfo Field(Type owner, string name)
        {
            var f = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(owner.FullName, name);
            return f;
        }

        internal static void Reset() { Events.Clear(); Rebases = 0; TargetsCanonicalized = 0; SavePasses = 0; SaveFieldsCanonicalized = 0; if (Naval) ResetStorms(); }
        // MMR-39: War Sails' storms are kept canonical in the same pass (PeriodicStorms); its types load only when War Sails is present.
        private static bool Naval { get { return MNBL.ProceduralWorld.CampaignRegistry.GeneratedGameManager.NavalPresent; } }
        private static void ResetStorms() { PeriodicStorms.Reset(); }
        private static void RebaseStorms(PeriodicWorld world) { PeriodicStorms.RebaseAll(world); }

        public static void RebaseAll()
        {
            var world = PeriodicWorld.Current;
            if (world == null || Campaign.Current == null) return;
            foreach (var party in MobileParty.All)
                if (party != null && party.IsActive) { Rebase(world, party); CanonicalTargets(world, party); }
            if (Naval) RebaseStorms(world);
        }

        // MMR-49: before a save (CampaignEvents.OnBeforeSaveEvent, which SaveHandler.SaveTick raises before it collects the metadata and writes),
        // closes MMR-63's hole: the per-tick pass runs before the hourly AI, so a target an AI behaviour placed in a strip during a tick stayed
        // non-canonical until the next tick and a save in between wrote it. Here every party's position, order targets and BehaviorTarget, and
        // every anchor's position, target and last disembark position, are moved to their canonical image; unset values (NaN, invalid, zero)
        // are left alone. Returns how many fields moved. NonCanonicalSaved counts what is still not canonical (the probes assert zero).
        public static int CanonicalizeForSave()
        {
            var world = PeriodicWorld.Current;
            if (world == null || Campaign.Current == null) return 0;
            int moved = TargetsCanonicalized;
            RebaseAll();
            moved = TargetsCanonicalized - moved;
            foreach (var party in MobileParty.All)
            {
                if (party == null) continue;
                if (party.IsActive && Rebase(world, party, false)) moved++;
                if (party.Ai != null && CanonicalField(world, BehaviorTargetField, party.Ai)) moved++;
                var anchor = party.Anchor;
                if (anchor == null) continue;
                if (CanonicalProperty(world, AnchorPositionProperty, anchor)) moved++;
                if (CanonicalProperty(world, AnchorTargetProperty, anchor)) moved++;
                if (CanonicalField(world, DisembarkField, anchor)) moved++;
            }
            foreach (var mapEvent in MapEvents()) if (CanonicalField(world, MapEventPositionField, mapEvent)) moved++;
            var tracks = Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior>();
            if (tracks != null)
            {
                var locator = TrackLocatorField.GetValue(tracks);
                var update = locator == null ? null : locator.GetType().GetMethod("UpdateLocator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var track in Tracks())
                {
                    var v = track.Position;
                    if (!Movable(world, v)) continue;
                    track.Position = new CampaignVec2(new Vec2(world.Canonical(v.X), v.Y), v.IsOnLand);
                    if (update != null) update.Invoke(locator, new object[] { track });
                    moved++;
                }
            }
            SavePasses++; SaveFieldsCanonicalized += moved;
            return moved;
        }

        private static IEnumerable<TaleWorlds.CampaignSystem.MapEvents.MapEvent> MapEvents()
        {
            var manager = Campaign.Current == null ? null : Campaign.Current.MapEventManager;
            return manager == null ? Enumerable.Empty<TaleWorlds.CampaignSystem.MapEvents.MapEvent>() : manager.MapEvents.Where(e => e != null).ToList();
        }

        private static IEnumerable<TaleWorlds.CampaignSystem.Track> Tracks()
        {
            var tracks = Campaign.Current == null ? null : Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.MapTracksCampaignBehavior>();
            var all = tracks == null ? null : AllTracksField.GetValue(tracks) as IEnumerable<TaleWorlds.CampaignSystem.Track>;
            return all == null ? Enumerable.Empty<TaleWorlds.CampaignSystem.Track>() : all.Where(t => t != null).ToList();
        }

        // Saved positions not canonical now: party positions, order targets, BehaviorTarget, anchors.
        public static List<string> NonCanonicalSaved()
        {
            var world = PeriodicWorld.Current;
            var found = new List<string>();
            if (world == null || Campaign.Current == null) return found;
            Func<CampaignVec2, bool> off = v => Movable(world, v);
            foreach (var party in MobileParty.All)
            {
                if (party == null) continue;
                if (off(party.Position)) found.Add(party.StringId + ".Position");
                foreach (var field in TargetFields) if (off((CampaignVec2)field.GetValue(party))) found.Add(party.StringId + "." + field.Name);
                if (party.Ai != null && off((CampaignVec2)BehaviorTargetField.GetValue(party.Ai))) found.Add(party.StringId + ".BehaviorTarget");
                var anchor = party.Anchor;
                if (anchor == null) continue;
                if (off(anchor.Position)) found.Add(party.StringId + ".Anchor.Position");
                if (off(anchor.TargetPosition)) found.Add(party.StringId + ".Anchor.TargetPosition");
                if (off((CampaignVec2)DisembarkField.GetValue(anchor))) found.Add(party.StringId + ".Anchor.LastDisembark");
            }
            int index = 0;
            foreach (var mapEvent in MapEvents()) { if (off((CampaignVec2)MapEventPositionField.GetValue(mapEvent))) found.Add("MapEvent[" + index + "].Position"); index++; }
            index = 0;
            foreach (var track in Tracks()) { if (off(track.Position)) found.Add("Track[" + index + "].Position"); index++; }
            return found;
        }

        // A saved position that is set (unset is Vec2.Invalid, NaN, or zero) and not canonical. Not CampaignVec2.IsValid: that also requires a
        // navmesh face valid for the value's land flag, and a strip value whose flag does not match its terrain would be saved non-canonical
        // and go unreported (MMR-49 review, run 44940).
        private static bool Movable(PeriodicWorld world, CampaignVec2 v)
        {
            return !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !(v.X == 0 && v.Y == 0) && !world.IsCanonical(v.X);
        }

        private static bool CanonicalField(PeriodicWorld world, FieldInfo field, object owner)
        {
            var v = (CampaignVec2)field.GetValue(owner);
            if (!Movable(world, v)) return false;
            field.SetValue(owner, new CampaignVec2(new Vec2(world.Canonical(v.X), v.Y), v.IsOnLand));
            return true;
        }

        private static bool CanonicalProperty(PeriodicWorld world, PropertyInfo property, object owner)
        {
            var v = (CampaignVec2)property.GetValue(owner);
            if (!Movable(world, v)) return false;
            property.SetValue(owner, new CampaignVec2(new Vec2(world.Canonical(v.X), v.Y), v.IsOnLand));
            return true;
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

        public static bool Rebase(PeriodicWorld world, MobileParty party) { return Rebase(world, party, true); }

        // onMapOnly: the per-tick pass rebases a position on a navmesh face valid for its land flag (CampaignVec2.IsValid); the save pass
        // rebases any set position (Movable), so a save never writes a non-canonical one.
        private static bool Rebase(PeriodicWorld world, MobileParty party, bool onMapOnly)
        {
            var position = party.Position;
            if (onMapOnly ? !position.IsValid() || world.IsCanonical(position.X) : !Movable(world, position)) return false;
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

    }
}
