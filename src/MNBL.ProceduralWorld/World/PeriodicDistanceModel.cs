using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-64: the versioned periodic distance contract over the installed distance surface (docs/periodic-coordinates.md). Pure managed.
    //   * ShortestOverImages is the periodic path rule of the campaign's map scene: the native (planar) path query is asked for each
    //     placement of the target (PeriodicWorldCoordinates.Placements: nearest displacement first, ties east; the start moved by whole
    //     widths when the target's copy lies outside the terrain) and the cheapest found path is the answer. Within the tolerance the earlier
    //     placement wins, so the answer does not depend on which display copy the caller passed. A placement whose straight-line length
    //     cannot beat or tie the best cost is never queried, and the query limit tightens to the best so far. The query keeps its own
    //     capability (excluded terrain), obstacles and directed region switch costs: a placement only moves the endpoints by whole widths, so
    //     a blocked corridor or an incompatible region stays unreachable however close a copy is. GeneratedMapScene's periodic path queries
    //     (which build the settlement navigation cache) use it. The navigation validator tries the same placements in the same order without
    //     pruning and keeps the shortest found path (MMR-38's rule), so it can pick another copy than the cost rule when the two disagree.
    //   * LowerBound: the wrapped geometric distance. It bounds a cost from below and prunes; it is never reused as a cost.
    //   * Coverage: every member of the installed MapDistanceModel (Bannerlord v1.4.8, War Sails v1.2.8: DefaultMapDistanceModel and
    //     NavalDLCMapDistanceModel declare all of them), and HelperCoverage: every planar distance call in the installed distance helpers
    //     (Helpers.DistanceHelper, Helpers.SettlementHelper), each with how it sees the periodic world. WorldWrapProbe checks both against the
    //     running assemblies (reflection, and Harmony's reading of the original IL), and the offline tests check Manifest against the pinned one.
    public static class PeriodicDistanceModel
    {
        public const string Version = "mmr64/1";
        public const double Tolerance = 1e-3;

        public delegate bool PlacementQuery(PeriodicWorldCoordinates.Placement placement, double limit, out double cost);

        public sealed class Result
        {
            public bool Found;
            public double Cost = Double.MaxValue;
            public PeriodicWorldCoordinates.Placement Placement;
            public int Rank = -1, Queries, Pruned, Candidates;
        }

        public static Result ShortestOverImages(PeriodicWorldCoordinates coordinates, double fromX, double fromY, double targetX, double targetY, double limit,
            PlacementQuery query, double tolerance = Tolerance)
        {
            if (coordinates == null || query == null) throw new ArgumentNullException();
            var placements = coordinates.Placements(fromX, targetX);
            var result = new Result { Candidates = placements.Count };
            double dy = targetY - fromY;
            for (int rank = 0; rank < placements.Count; rank++)
            {
                var placement = placements[rank];
                double straight = Math.Sqrt(placement.Delta * placement.Delta + dy * dy);
                if (result.Found && straight > result.Cost + tolerance) { result.Pruned++; continue; }
                double cost, bound = result.Found ? Math.Min(limit, result.Cost + tolerance) : limit;
                result.Queries++;
                if (!query(placement, bound, out cost)) continue;
                if (!result.Found || cost < result.Cost - tolerance) { result.Found = true; result.Cost = cost; result.Placement = placement; result.Rank = rank; }
            }
            return result;
        }

        public static double LowerBound(PeriodicWorldCoordinates coordinates, double ax, double ay, double bx, double by) { return coordinates.GeometricDistance(ax, ay, bx, by); }

        // ---- coverage of the installed MapDistanceModel ----
        public const string Cache = "cache";            // reads the settlement navigation cache, built from IMapScene path queries over placements
        public const string PartyLeg = "party-leg";     // a straight leg: CampaignVec2.Distance -> the wrapped geometric distance
        public const string Entrance = "entrance";      // the closest entrance per face: every candidate's path cost over placements, in periodic order
        public const string Capability = "capability";  // compares navigation capability or region only
        public const string Constant = "constant";      // no coordinates
        public const string Registration = "registration";

        public sealed class Coverage
        {
            public string Member { get; private set; }        // name(parameter type names), as System.Reflection prints them
            public IList<string> Treatments { get; private set; }
            public bool Directed { get; private set; }        // the result depends on direction; from and to are never swapped
            public string Reason { get; private set; }
            internal Coverage(string member, bool directed, string reason, string[] treatments)
            {
                Member = member; Directed = directed; Reason = reason; Treatments = Array.AsReadOnly((string[])treatments.Clone());
            }
        }

        // The installed settlement cache stores one value per unordered pair (NavigationCacheElement.Sort orders the two ends), so cache
        // distances are symmetric; direction enters through War Sails' transition adjustment and the region switch costs of path queries.
        public static readonly IList<Coverage> Manifest = Array.AsReadOnly(new[] {
            C("get_RegionSwitchCostFromLandToSea()", true, "the land to sea switch cost; passed unchanged, in its direction, to every periodic path query", Constant),
            C("get_RegionSwitchCostFromSeaToLand()", true, "the sea to land switch cost; as above", Constant),
            C("get_MaximumSpawnDistanceForCompanionsAfterDisband()", false, "a radius compared with GetDistance results, which are periodic", Constant),
            C("GetMaximumDistanceBetweenTwoConnectedSettlements(NavigationType)", false, "the navigation cache's largest connected settlement distance, from periodic path queries", Cache),
            C("GetLandRatioOfPathBetweenSettlements(Settlement,Settlement,Boolean,Boolean)", false, "the cache's land ratio of the periodic path: NavigationCache.GetLandRatioOfPath walks the path GetPathBetweenAIFaces returns, which runs continuously from the canonical start into the strip, so its planar segment lengths are exact", Cache),
            C("GetDistance(MobileParty,Settlement,Boolean,NavigationType,Single&)", true, "d(party, target) - d(entrance, target) + cache(entrance, target), the entrance being the closest to the party's face; both straight legs are periodic; War Sails adds the cache land ratio and the directed transition adjustment", PartyLeg, Entrance, Cache),
            C("GetDistance(MobileParty,MobileParty,NavigationType,Single&)", false, "delegates to the bounded party-to-party overload", PartyLeg),
            C("GetDistance(MobileParty,MobileParty,NavigationType,Single,Single&,Single&)", false, "the installed model's distance is the straight party-to-party line (two sites), compared with the maximum; made periodic", PartyLeg),
            C("GetDistance(Settlement,Settlement,Boolean,Boolean,NavigationType)", false, "the cache distance between gates or ports (one value per unordered pair)", Cache),
            C("GetDistance(Settlement,Settlement,Boolean,Boolean,NavigationType,Single&)", false, "as above, with the cache's land ratio", Cache),
            C("GetDistance(MobileParty,CampaignVec2&,NavigationType,Single&)", true, "the closest entrances of the party and the point, the periodic straight legs and the cache distance between the entrances; War Sails adds the directed transition adjustment", PartyLeg, Entrance, Cache),
            C("GetDistance(Settlement,CampaignVec2&,Boolean,NavigationType)", true, "d(settlement, point) - d(settlement, entrance) + cache(settlement, entrance), the entrance being the closest to the point's face; War Sails adds the directed transition adjustment", PartyLeg, Entrance, Cache),
            C("GetPortToGateDistanceForSettlement(Settlement)", false, "War Sails: the All-cache distance from the settlement's port to its gate; Default returns the constant 1e8 (no ports)", Cache, Constant),
            C("PathExistBetweenPoints(CampaignVec2&,CampaignVec2&,NavigationType)", false, "Default compares IsOnLand (capability only); War Sails compares the closest entrances of both points through the cache", Capability, Entrance, Cache),
            C("RegisterDistanceCache(NavigationType,INavigationCache)", false, "the generated caches are built through the periodic IMapScene after PeriodicPatches applies (GeneratedMapScene.Load), once per session", Registration),
            C("GetClosestEntranceToFace(PathFaceRecord,NavigationType)", false, "the per-face closest settlement cache: every candidate's periodic path distance is evaluated (both installed callers pass useEarlyOut = false, so the planar comparisons in that branch never run); the candidate order, which sets only search limits and exact ties, is periodic (MMR-64 patch); face indices are session-local", Entrance),
            C("GetNeighborsOfFortification(Town,NavigationType)", false, "the cache's neighbour sets (SandBoxNavigationCache.CheckBeingNeighbor: periodic path samples and closest-settlement checks)", Cache),
            C("GetTransitionCostAdjustment(Settlement,Boolean,Settlement,Boolean,Boolean,Boolean)", true, "War Sails: the directed region switch costs; the cache distance only decides whether the sign flips (finite below the squared map diagonal); Default returns 0", Constant, Cache),
            C("PossibleMaximumMapBoundary", false, "a const field (1e8), not a coordinate", Constant) });

        private static Coverage C(string member, bool directed, string reason, params string[] treatments) { return new Coverage(member, directed, reason, treatments); }

        // ---- the installed distance helpers ----
        // Every planar distance call in Helpers.DistanceHelper and Helpers.SettlementHelper (Sites: the call count in the original IL).
        public sealed class HelperSite
        {
            public string Type { get; private set; }
            public string Member { get; private set; }
            public int Sites { get; private set; }
            public string Treatment { get; private set; }
            public string Reason { get; private set; }
            internal HelperSite(string type, string member, int sites, string treatment, string reason) { Type = type; Member = member; Sites = sites; Treatment = treatment; Reason = reason; }
        }
        public const string HelperPatched = "patched";
        public static readonly IList<HelperSite> HelperCoverage = Array.AsReadOnly(new[] {
            new HelperSite("Helpers.DistanceHelper", "FindClosestDistanceFromMobilePartyToMobileParty(MobileParty,MobileParty,NavigationType,Single&)", 1, HelperPatched,
                "CampaignVec2.DistanceSquared(CampaignVec2) against the bird-fly threshold chooses between the straight distance and the entrance formula; made periodic"),
            new HelperSite("Helpers.DistanceHelper", "GetDistanceBetweenMobilePartyToMobileParty(MobileParty,MobileParty,NavigationType,Single&)", 2, HelperPatched,
                "d(p1, p2) - d(E1, E2) + cache(E1, E2) + transition: the two straight legs (CampaignVec2.Distance) are made periodic, as inside the models"),
            new HelperSite("Helpers.SettlementHelper", "FindNearestSettlementToPoint(CampaignVec2&,Func`2)", 1, HelperPatched,
                "the nearest settlement to a point by straight distance (CampaignVec2.Distance); made periodic") });

        // The planar distance methods a helper site calls (declaring type::name(parameter type names)).
        public static readonly IList<string> PlanarDistanceMethods = Array.AsReadOnly(new[] {
            "TaleWorlds.CampaignSystem.CampaignVec2::Distance(CampaignVec2)", "TaleWorlds.CampaignSystem.CampaignVec2::DistanceSquared(CampaignVec2)",
            "TaleWorlds.CampaignSystem.CampaignVec2::Distance(Vec2)", "TaleWorlds.CampaignSystem.CampaignVec2::DistanceSquared(Vec2)",
            "TaleWorlds.Library.Vec2::Distance(Vec2)", "TaleWorlds.Library.Vec2::DistanceSquared(Vec2)" });

        public static string PlanarName(MethodBase method) { return method.DeclaringType.FullName + "::" + Signature(method); }

        // Members of the installed type with no manifest entry, and manifest entries the installed type lacks.
        public static void Compare(IEnumerable<string> installed, out List<string> uncovered, out List<string> stale)
        {
            var names = new HashSet<string>(installed, StringComparer.Ordinal);
            var listed = new HashSet<string>(Manifest.Select(m => m.Member), StringComparer.Ordinal);
            uncovered = names.Where(n => !listed.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            stale = listed.Where(n => !names.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // The members a manifest covers, as Compare expects them: declared non-private methods (property accessors included, constructors
        // excluded) and non-private fields, compiler-generated ones excluded.
        public static List<string> Members(Type type)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            return type.GetMethods(all).Where(m => !m.IsPrivate && !m.Name.StartsWith("<", StringComparison.Ordinal)).Select(Signature)
                .Concat(type.GetFields(all).Where(f => !f.IsPrivate && !f.Name.StartsWith("<", StringComparison.Ordinal)).Select(f => f.Name))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        public static string Signature(MethodBase method)
        {
            return method.Name + "(" + String.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
        }

        // Manifest methods a model type does not declare itself (DefaultMapDistanceModel and NavalDLCMapDistanceModel declare all of them).
        public static List<string> NotOverridden(Type model)
        {
            var declared = new HashSet<string>(Members(model), StringComparer.Ordinal);
            return Manifest.Where(m => m.Member.EndsWith(")", StringComparison.Ordinal) && !declared.Contains(m.Member)).Select(m => m.Member).ToList();
        }

        public static List<Dictionary<string, object>> Record()
        {
            return Manifest.Select(m => new Dictionary<string, object> { ["member"] = m.Member, ["treatments"] = m.Treatments.ToArray(), ["directed"] = m.Directed, ["reason"] = m.Reason }).ToList();
        }

        public static List<Dictionary<string, object>> HelperRecord()
        {
            return HelperCoverage.Select(h => new Dictionary<string, object> { ["type"] = h.Type, ["member"] = h.Member, ["sites"] = h.Sites, ["treatment"] = h.Treatment, ["reason"] = h.Reason }).ToList();
        }
    }
}
