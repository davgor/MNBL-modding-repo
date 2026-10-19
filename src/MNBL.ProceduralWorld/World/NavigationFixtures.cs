using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MNBL.ProceduralWorld.Surface
{
    // MMR-38: the navigation fixture world and the fault variants, both built by the production builder (RuntimeNavigationBuilder) and
    // validated by the production validator.
    //   * Fixture: a compiled world's terrain with designed travel cells (logical 64 x 128 cells): the MMR-17 cases restated on production
    //     navigation (a mountain barrier with a detour and a closed ring; an enclosed lake; an off-landmass island behind a one-cell sea
    //     channel, the narrow passage MMR-17's ticket named but never built), plus what MMR-17 did not cover: rivers with a straight
    //     one-cell bridge, a two-cell bridge and a north-south bridge, each over under-bridge faces; routes across the terrain chunk (tile)
    //     lines and across the periodic seam on land and at sea; the open sea; the pole padding; a strip image. Every expectation is designed
    //     here, not derived from the mesh.
    //   * Faults: a village moved into the open sea (snap refused), a village moved onto another land component (snap refused), the joining
    //     crossings dropped (bridges not realized, groups split). The engine fault (bridge faces disabled in a loaded scene) is the probe's.
    internal static class NavigationFixtures
    {
        public const string Variant = "fixture";

        internal sealed class Check
        {
            public string Id, Group, Kind = "route", Type;
            public int[] From, To, Cell;
            public double[] Point;          // explicit logical point (or physical when PhysicalPoint)
            public bool FromLand = true, ToLand = true, Land = true, Expected, Gating = true, PhysicalPoint;
            public int MinTransitions = -1, MaxTransitions = -1;
            public double MaxFactor = -1, MinFactor = -1;
            public double[] Detour;
            public int[] Excluded;
            public List<int[]> Through;
            public bool ThroughLand;
            public string[] Cross, Expect = new string[0];
        }

        private static void Fill(TravelCells t, int r0, int r1, int c0, int c1, byte k)
        {
            for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) t.Classes[t.Index(r, c)] = k;
        }

        public static NavigationInput Build(NavigationInput production, NavigationPolicy policy)
        {
            var t = production.Cells.Clone();
            if (t.Rows != 64 || t.Cols != 128) throw new InvalidDataException("The navigation fixture is designed for a 64 x 128 cell globe, not " + t.Rows + " x " + t.Cols + ".");
            for (int i = 0; i < t.Classes.Length; i++) t.Classes[i] = TravelCells.Sea;
            Fill(t, 8, 55, 16, 111, TravelCells.Land);        // mainland M
            Fill(t, 8, 55, 90, 90, TravelCells.River);        // river R1: north coast to south coast
            Fill(t, 44, 48, 91, 91, TravelCells.River);       //   two cells wide at rows 44-48
            Fill(t, 36, 36, 91, 111, TravelCells.River);      // river R2: from R1 to the east coast
            Fill(t, 20, 32, 40, 41, TravelCells.Mountain);    // barrier wall, two cells thick, open at both ends
            Fill(t, 44, 52, 24, 32, TravelCells.Mountain);    // closed mountain ring...
            Fill(t, 45, 51, 25, 31, TravelCells.Land);        //   ...around a land pocket
            Fill(t, 12, 14, 66, 68, TravelCells.Lake);        // enclosed 3 x 3 lake ringed by land
            Fill(t, 18, 30, 113, 117, TravelCells.Land);      // island I behind the one-cell channel at column 112
            Fill(t, 20, 44, 120, 127, TravelCells.Land);      // continent S across the periodic seam
            Fill(t, 20, 44, 0, 8, TravelCells.Land);
            t.OpenSea = t.ComputeOpenSea(production.OpenSeaCells);
            var land = t.Label(k => k == TravelCells.Land);
            Func<int, int, int> L = (r, c) => land[t.Index(r, c)];
            var input = new NavigationInput {
                WorldContentId = production.WorldContentId, Variant = Variant, Surface = production.Surface, Cells = t, FaceGroupNames = production.FaceGroupNames,
                OpenSeaCells = production.OpenSeaCells, MaxBridgeCells = production.MaxBridgeCells, MaxPortGateUnits = production.MaxPortGateUnits, MainGroup = -1 };
            input.Crossings.Add(new CrossingRecord { Subject = "fixture bridge 1 (one cell, x)", Kind = "river", Cells = new List<int[]> { new[] { 24, 90 } }, LandA = L(24, 89), LandB = L(24, 91) });
            input.Crossings.Add(new CrossingRecord { Subject = "fixture bridge 2 (two cells, x)", Kind = "river", Cells = new List<int[]> { new[] { 46, 90 }, new[] { 46, 91 } }, LandA = L(46, 89), LandB = L(46, 92) });
            input.Crossings.Add(new CrossingRecord { Subject = "fixture bridge 3 (one cell, y)", Kind = "river", Cells = new List<int[]> { new[] { 36, 100 } }, LandA = L(35, 100), LandB = L(37, 100) });
            input.FixtureChecks = Checks(production, policy);
            input.InputsSha256 = Json.Sha(String.Join("\n", new[] { "mnbl.navigation-fixture/1", Json.Sha(t.Classes), Json.Sha(t.OpenSea), policy.Sha256, production.Surface.InputsSha256 }));
            return input;
        }

        private static List<Check> Checks(NavigationInput production, NavigationPolicy policy)
        {
            var names = production.FaceGroupNames;
            string plain = names["land"], bridge = names["bridge"], under = names["underBridge"], lake = names["lake"], open = names["openSea"], coastal = names["coastalSea"], mountain = names["mountain"];
            var checks = new List<Check>();
            Action<string, string, int[], bool, int[], bool, bool, Action<Check>, bool> route = (group, id, from, fromLand, to, toLand, expected, configure, back) =>
            {
                foreach (var pass in back ? new[] { false, true } : new[] { false })
                {
                    var c = new Check { Group = group, Id = id + (pass ? " (back)" : ""), From = pass ? to : from, FromLand = pass ? toLand : fromLand, To = pass ? from : to, ToLand = pass ? fromLand : toLand, Expected = expected };
                    if (configure != null) configure(c);
                    checks.Add(c);
                }
            };
            Func<string, Action<Check>> type = name => c => c.Type = name;
            // Barrier (MMR-17's obstruction, now a static mountain wall; R03: no runtime face toggles).
            // The wall's direct crossing is 10 cells; the MMR-17 acceptance: direct + minDetourExtraCells <= detour <= maxDetourFactor * direct.
            route("barrier", "landAroundWall_Default", new[] { 26, 36 }, true, new[] { 26, 46 }, true, true, c => { c.Type = "Default"; c.Detour = new double[] { 10 + policy.MinDetourExtraCells, policy.MaxDetourFactor * 10 }; }, true);
            route("barrier", "landIntoClosedRing_Default", new[] { 48, 21 }, true, new[] { 48, 28 }, true, false, type("Default"), true);
            route("barrier", "landIntoClosedRing_All", new[] { 48, 21 }, true, new[] { 48, 28 }, true, false, type("All"), false);
            // Observation, not gating: with no excluded faces, the ring is closed only if the installed pre-read disable of the faces invalid
            // for every type (SandBox MapScene.Load's DisableUnwalkableNavigationMeshes, repeated by Construct) took effect.
            route("barrier", "landIntoClosedRing_noExclusions (observed: pre-read disable)", new[] { 48, 21 }, true, new[] { 48, 28 }, true, false, c => { c.Type = "All"; c.Excluded = new int[0]; c.Gating = false; }, false);
            // Enclosed lake.
            route("lake", "landAroundLake_Default", new[] { 13, 64 }, true, new[] { 13, 70 }, true, true, type("Default"), true);
            route("lake", "lakeShoreToLake_All", new[] { 13, 64 }, true, new[] { 13, 67 }, false, true, c => { c.Type = "All"; c.MinTransitions = 1; }, false);
            route("lake", "seaToEnclosedLake_Naval", new[] { 4, 67 }, false, new[] { 13, 67 }, false, false, type("Naval"), false);
            route("lake", "landToLake_Default", new[] { 13, 64 }, true, new[] { 13, 67 }, false, false, type("Default"), false);
            // Narrow channel and the off-landmass island.
            route("channel", "seaThroughChannel_Naval", new[] { 15, 112 }, false, new[] { 33, 112 }, false, true, c => { c.Type = "Naval"; c.MaxFactor = 1.2; }, true);
            route("channel", "landToOtherLandmass_Default", new[] { 24, 110 }, true, new[] { 24, 115 }, true, false, type("Default"), true);
            route("channel", "landToOtherLandmass_All", new[] { 24, 110 }, true, new[] { 24, 115 }, true, true, c => { c.Type = "All"; c.MinTransitions = 2; }, true);
            // Bridges over under-bridge faces; rivers are crossed only at bridges.
            Func<bool, int[][], Action<Check>> through = (deck, cells) => c => { c.Through = cells.ToList(); c.ThroughLand = deck; };
            Func<string, double, bool, int[][], Action<Check>> bridgeRoute = (t, factor, deck, cells) => c => { c.Type = t; c.MaxFactor = factor; through(deck, cells)(c); };
            route("bridge", "bridge1Crossing_Default", new[] { 24, 88 }, true, new[] { 24, 92 }, true, true, bridgeRoute("Default", 1.2, true, new[] { new[] { 24, 90 } }), true);
            route("bridge", "bridge1Under_Naval", new[] { 21, 90 }, false, new[] { 27, 90 }, false, true, bridgeRoute("Naval", 1.2, false, new[] { new[] { 24, 90 } }), true);
            route("bridge", "bridge2Crossing_Default", new[] { 46, 88 }, true, new[] { 46, 93 }, true, true, bridgeRoute("Default", 1.2, true, new[] { new[] { 46, 90 }, new[] { 46, 91 } }), true);
            route("bridge", "bridge2Under_Naval", new[] { 42, 90 }, false, new[] { 50, 90 }, false, true, bridgeRoute("Naval", 1.3, false, new[] { new[] { 46, 90 }, new[] { 46, 91 } }), true);
            route("bridge", "bridge3Crossing_Default", new[] { 34, 100 }, true, new[] { 38, 100 }, true, true, bridgeRoute("Default", 1.2, true, new[] { new[] { 36, 100 } }), true);
            route("bridge", "bridge3Under_Naval", new[] { 36, 96 }, false, new[] { 36, 104 }, false, true, bridgeRoute("Naval", 1.2, false, new[] { new[] { 36, 100 } }), true);
            route("bridge", "riverNoShortcut_Default", new[] { 30, 88 }, true, new[] { 30, 92 }, true, true, c => { c.Type = "Default"; c.MinFactor = 2; }, true);
            route("bridge", "riverFromSea_Naval", new[] { 4, 90 }, false, new[] { 30, 90 }, false, true, type("Naval"), true);
            // Tile (terrain chunk) lines and the periodic seam.
            route("tiles", "landAcrossTileRow_Default", new[] { 28, 30 }, true, new[] { 36, 30 }, true, true, c => { c.Type = "Default"; c.Cross = new[] { "y=800" }; }, true);
            route("tiles", "landAcrossTileColumn_Default", new[] { 26, 60 }, true, new[] { 26, 68 }, true, true, c => { c.Type = "Default"; c.Cross = new[] { "x=800" }; }, true);
            route("tiles", "seaAcrossTileRow_Naval", new[] { 28, 12 }, false, new[] { 36, 12 }, false, true, c => { c.Type = "Naval"; c.Cross = new[] { "y=800" }; }, true);
            route("tiles", "seaAcrossTileColumn_Naval", new[] { 3, 58 }, false, new[] { 3, 70 }, false, true, c => { c.Type = "Naval"; c.Cross = new[] { "x=800" }; }, true);
            route("tiles", "landAcrossSeam_Default", new[] { 32, 124 }, true, new[] { 32, 4 }, true, true, c => { c.Type = "Default"; c.MaxFactor = 1.2; c.Cross = new[] { c.Id.EndsWith("(back)") ? "x=400" : "x=1200" }; }, true);
            route("tiles", "seaAcrossSeam_Naval", new[] { 12, 124 }, false, new[] { 12, 4 }, false, true, c => { c.Type = "Naval"; c.MaxFactor = 1.2; c.Cross = new[] { c.Id.EndsWith("(back)") ? "x=400" : "x=1200" }; }, true);
            // Faces.
            Action<string, string, int[], double[], bool, string[], bool, bool> face = (group, id, cell, point, isLand, expect, gating, physical) =>
                checks.Add(new Check { Kind = "face", Group = group, Id = id, Cell = cell, Point = point, Land = isLand, Expect = expect, Gating = gating, PhysicalPoint = physical });
            face("faces", "openSea", new[] { 61, 64 }, null, false, new[] { open }, true, false);
            face("faces", "coastalSea", new[] { 7, 64 }, null, false, new[] { coastal }, true, false);
            face("faces", "plain", new[] { 26, 60 }, null, true, new[] { plain }, true, false);
            face("faces", "lakeAsWater", new[] { 13, 67 }, null, false, new[] { lake }, true, false);
            face("faces", "lakeAsLand", new[] { 13, 67 }, null, true, new string[0], true, false);
            face("faces", "wallAsLand", new[] { 26, 40 }, null, true, new string[0], true, false);
            face("faces", "wallAsSea (Mountain is in the sea region; observed, not gating)", new[] { 26, 40 }, null, false, new[] { mountain }, false, false);
            foreach (var b in new[] { new[] { 24, 90 }, new[] { 46, 90 }, new[] { 46, 91 }, new[] { 36, 100 } })
            {
                face("bridge", "deck " + b[0] + "," + b[1], b, null, true, new[] { bridge }, true, false);
                face("bridge", "under " + b[0] + "," + b[1], b, null, false, new[] { under }, true, false);
            }
            foreach (bool isLand in new[] { true, false })
            {
                face("padding", "beyondNorthPole" + (isLand ? "Land" : "Sea"), null, new[] { 100.0, -3.125 }, isLand, new string[0], true, false);
                face("padding", "beyondSouthPole" + (isLand ? "Land" : "Sea"), null, new[] { 100.0, 403.125 }, isLand, new string[0], true, false);
                face("padding", "beyondPoleInStrip" + (isLand ? "Land" : "Sea"), null, new[] { 100.0, 590.0 }, isLand, new string[0], true, true);
            }
            face("strip", "stripImageLand (cell 26,100)", null, new[] { 228.125, 765.625 }, true, new[] { plain }, true, true);
            face("strip", "stripImageSea (cell 3,80)", null, new[] { 103.125, 621.875 }, false, new[] { open }, true, true);
            return checks;
        }
    }

    internal static class NavigationVariants
    {
        public static readonly string[] Faults = { "fault:snap-sea", "fault:snap-component", "fault:no-bridges" };

        public static NavigationInput Apply(string name, NavigationInput production, NavigationPolicy policy)
        {
            if (name == null || name == "production") return production;
            if (name == NavigationFixtures.Variant) return NavigationFixtures.Build(production, policy);
            var input = Copy(production); input.Variant = name;
            var t = input.Cells;
            var village = input.Settlements.Where(s => s.Kind == "village" && !input.Excluded.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();
            switch (name)
            {
                case "fault:snap-sea":
                {
                    if (village == null) throw new InvalidDataException("The world has no village to move.");
                    int best = -1, bestDistance = int.MaxValue;
                    for (int i = 0; i < t.Classes.Length; i++)
                    {
                        if (t.OpenSea[i] != 1) continue;
                        int dr = Math.Abs(i / t.Cols - village.Row), dc = Math.Abs(i % t.Cols - village.Col); dc = Math.Min(dc, t.Cols - dc);
                        if (Math.Max(dr, dc) < bestDistance) { bestDistance = Math.Max(dr, dc); best = i; }
                    }
                    if (best < 0) throw new InvalidDataException("The world has no open sea.");
                    Move(village, best / t.Cols, best % t.Cols, t);
                    break;
                }
                case "fault:snap-component":
                {
                    if (village == null) throw new InvalidDataException("The world has no village to move.");
                    var landOf = t.Label(k => k == TravelCells.Land);
                    var town = input.Settlements.Where(s => s.Kind == "town" && !input.Excluded.Contains(s.Id) && s.Land != village.Land).OrderBy(s => s.Id, StringComparer.Ordinal)
                        .FirstOrDefault(s => !Enumerable.Range(-2, 5).Any(dr => Enumerable.Range(-2, 5).Any(dc => t.InRows(s.Row + dr) && landOf[t.Index(s.Row + dr, s.Col + dc)] == village.Land)));
                    if (town == null) throw new InvalidDataException("No town lies two cells clear of the village's land component.");
                    village.X = town.X; village.Y = town.Y; village.Row = town.Row; village.Col = town.Col;
                    break;
                }
                case "fault:no-bridges":
                    input.Crossings.Clear();
                    break;
                default:
                    throw new ArgumentException("Unknown navigation variant " + name + ".");
            }
            input.InputsSha256 = Json.Sha(production.InputsSha256 + "\n" + name);
            return input;
        }

        private static void Move(SettlementRecord s, int r, int c, TravelCells t) { s.Row = r; s.Col = c; s.X = (c + .5) * t.Spacing; s.Y = (r + .5) * t.Spacing; }

        private static NavigationInput Copy(NavigationInput p)
        {
            var c = p.Shallow();
            c.Settlements = p.Settlements.Select(s => new SettlementRecord { Id = s.Id, Uid = s.Uid, Kind = s.Kind, Method = s.Method, Row = s.Row, Col = s.Col, Land = s.Land, Group = s.Group, X = s.X, Y = s.Y }).ToList();
            c.Crossings = new List<CrossingRecord>(p.Crossings);
            return c;
        }
    }
}
