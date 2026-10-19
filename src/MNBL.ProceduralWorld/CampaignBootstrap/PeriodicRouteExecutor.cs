using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace MNBL.ProceduralWorld.CampaignBootstrap
{
    // MMR-65: how a route crosses the seam (docs/periodic-movement.md). The execution itself is the accepted R10 mechanism:
    //   1. a path query takes the target's placement whose native path is cheapest (GeneratedMapScene, PeriodicDistanceModel.ShortestOverImages):
    //      the path runs through the seam into the strip beyond it, so its points are one continuous planar polyline in the party's frame;
    //   2. the party follows it natively (MobileParty movement; its position and path differences are frame-consistent, and every planar
    //      measure between two campaign positions is periodic, PeriodicSpatialQueries);
    //   3. once the party leaves the canonical range PeriodicSeam moves it, its path, its next targets and (for the player) the camera by
    //      whole widths on the same campaign tick, and canonicalizes its target fields; an attached army member keeps steering by its leader's
    //      target through the periodic difference, so it crosses and is moved on its own;
    //   4. a long-way route from a moved start (MMR-64) is shifted back to the caller's start, and its points beyond the terrain edge are never
    //      reached because the rebase comes first.
    // Saved state holds coordinates only: no native face or path handle is persisted (a face is re-derived from its position).
    // This class exposes the route segment and transition diagnostics MMR-41's cache verification and the probes read.
    public static class PeriodicRouteExecutor
    {
        private static readonly FieldInfo PathField = typeof(MobileParty).GetField("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public sealed class Segment
        {
            public int Index; public float FromX, FromY, ToX, ToY, Length;
            public int Seam;   // +1 crossing the seam eastward (x passes MaxX), -1 westward (x passes OriginX), 0 none
        }

        // The party's current path as segments in its own frame, with the seam crossings marked.
        public static List<Segment> Segments(PeriodicWorld world, MobileParty party)
        {
            var segments = new List<Segment>();
            var path = PathField == null ? null : PathField.GetValue(party) as NavigationPath;
            if (world == null || path == null || path.PathPoints == null) return segments;
            var points = new List<Vec2> { party.Position.ToVec2() };
            for (int i = 0; i < path.Size && i < path.PathPoints.Length; i++) points.Add(path.PathPoints[i]);
            for (int i = 1; i < points.Count; i++)
            {
                Vec2 a = points[i - 1], b = points[i];
                int seam = a.X < world.MaxX && b.X >= world.MaxX ? 1 : a.X >= world.OriginX && b.X < world.OriginX ? -1 : 0;
                segments.Add(new Segment { Index = i - 1, FromX = a.X, FromY = a.Y, ToX = b.X, ToY = b.Y, Length = a.Distance(b), Seam = seam });
            }
            return segments;
        }

        public static Dictionary<string, object> Describe(PeriodicWorld world, MobileParty party)
        {
            var segments = Segments(world, party);
            return new Dictionary<string, object> {
                ["party"] = party.StringId, ["position"] = new[] { party.Position.X, party.Position.Y }, ["canonical"] = world == null || world.IsCanonical(party.Position.X),
                ["atSea"] = party.IsCurrentlyAtSea, ["navigationCapability"] = party.NavigationCapability.ToString(), ["desiredNavigation"] = party.DesiredAiNavigationType.ToString(),
                ["transitionInProgress"] = party.IsTransitionInProgress,
                ["transitionEnd"] = party.IsTransitionInProgress ? new[] { party.EndPositionForNavigationTransition.X, party.EndPositionForNavigationTransition.Y } : null,
                ["segments"] = segments.Count, ["pathLength"] = segments.Sum(s => s.Length),
                ["seamCrossings"] = segments.Where(s => s.Seam != 0).Select(s => (object)new Dictionary<string, object> { ["segment"] = s.Index, ["direction"] = s.Seam > 0 ? "east" : "west",
                    ["from"] = new[] { s.FromX, s.FromY }, ["to"] = new[] { s.ToX, s.ToY } }).ToList(),
                ["longestSegment"] = segments.Count == 0 ? 0 : segments.Max(s => s.Length),
                ["rebases"] = PeriodicSeam.Events.Count(e => e.Party == party.StringId) };
        }
    }
}
