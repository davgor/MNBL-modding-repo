using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MNBL.ProceduralWorld.Automation
{
    // MMR-247: the places the acceptance probe looks at, chosen from the world's own data without the game (pure managed; tests/World/
    // Map2AcceptancePlanTests.cs runs it on the A'/B' bundles). Positions are logical (x in [0, width), y in [0, height] between the pole lines);
    // the probe turns them physical with the periodic frame (physical = logical + (originX, originY)).
    internal static class Map2AcceptancePlan
    {
        public sealed class Spot
        {
            public string Biome;
            public double X, Y;          // logical
            public int Score, Window;    // same-biome samples in the (2r+1)^2 window around the spot, of Window
            public int Samples;          // samples of the biome on the whole grid
            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["biome"] = Biome, ["logical"] = new[] { X, Y }, ["score"] = Score, ["window"] = Window, ["share"] = Window == 0 ? 0 : (double)Score / Window, ["samples"] = Samples };
            }
        }

        // The sampling grid: columns at (i + 0.5) * step over the width (periodic), rows at (j + 0.5) * step over the height.
        public sealed class Grid
        {
            public int Cols, Rows; public double Step; public string[] Biomes;
            public double X(int i) { return (i + .5) * Step; }
            public double Y(int j) { return (j + .5) * Step; }
            public string At(int i, int j) { return j < 0 || j >= Rows ? null : Biomes[j * Cols + ((i % Cols) + Cols) % Cols]; }
        }

        public static Grid Sample(Func<double, double, string> biomeAt, double width, double height, double step)
        {
            if (biomeAt == null) throw new ArgumentNullException("biomeAt");
            if (!(width > 0) || !(height > 0) || !(step > 0) || Double.IsNaN(width) || Double.IsNaN(height)) throw new ArgumentException("A sampling grid needs a positive width, height and step.");
            var g = new Grid { Step = step, Cols = Math.Max(1, (int)Math.Floor(width / step)), Rows = Math.Max(1, (int)Math.Floor(height / step)) };
            g.Biomes = new string[g.Cols * g.Rows];
            for (int j = 0; j < g.Rows; j++) for (int i = 0; i < g.Cols; i++) g.Biomes[j * g.Cols + i] = biomeAt(g.X(i), g.Y(j));
            return g;
        }

        // For each wanted biome present on the grid: the most interior sample (the largest count of same-biome samples in the window of radius
        // `radius` samples around it, columns wrapping, rows clamped by the pole lines; ties: the first in row-major order from the equator
        // band outward, so a spot is not hugging a pole). A biome absent from the grid has no spot.
        public static Dictionary<string, Spot> Interior(Grid g, IEnumerable<string> wanted, int radius)
        {
            var result = new Dictionary<string, Spot>(StringComparer.Ordinal);
            if (radius < 0) throw new ArgumentException("radius");
            foreach (var biome in wanted.Distinct())
            {
                int total = g.Biomes.Count(b => b == biome);
                if (total == 0) continue;
                // A summed-area table of the biome's indicator over the grid with `radius` wrapped columns on each side.
                int w = g.Cols + 2 * radius, h = g.Rows;
                var sum = new int[(w + 1) * (h + 1)];
                for (int j = 0; j < h; j++)
                    for (int k = 0; k < w; k++)
                    {
                        int v = g.At(k - radius, j) == biome ? 1 : 0;
                        sum[(j + 1) * (w + 1) + k + 1] = v + sum[j * (w + 1) + k + 1] + sum[(j + 1) * (w + 1) + k] - sum[j * (w + 1) + k];
                    }
                Spot best = null; int bestOrder = int.MaxValue;
                foreach (int j in RowsFromEquator(h))
                    for (int i = 0; i < g.Cols; i++)
                    {
                        if (g.At(i, j) != biome) continue;
                        int r0 = Math.Max(0, j - radius), r1 = Math.Min(h - 1, j + radius), c0 = i, c1 = i + 2 * radius;   // window columns in the padded table
                        int score = sum[(r1 + 1) * (w + 1) + c1 + 1] - sum[r0 * (w + 1) + c1 + 1] - sum[(r1 + 1) * (w + 1) + c0] + sum[r0 * (w + 1) + c0];
                        int window = (r1 - r0 + 1) * (2 * radius + 1);
                        int order = Order(h, j) * g.Cols + i;
                        if (best == null || score > best.Score || (score == best.Score && order < bestOrder))
                        { best = new Spot { Biome = biome, X = g.X(i), Y = g.Y(j), Score = score, Window = window, Samples = total }; bestOrder = order; }
                    }
                if (best != null) result[biome] = best;
            }
            return result;
        }

        private static IEnumerable<int> RowsFromEquator(int rows)
        {
            int mid = rows / 2;
            yield return mid;
            for (int d = 1; d <= rows; d++) { if (mid - d >= 0) yield return mid - d; if (mid + d < rows) yield return mid + d; }
        }
        private static int Order(int rows, int j) { int mid = rows / 2, d = Math.Abs(j - mid); return j < mid ? 2 * d - 1 : 2 * d; }

        // Every biome on the grid with its sample count (the world's biome census at the probe's resolution).
        public static SortedDictionary<string, int> Census(Grid g)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var b in g.Biomes) { string k = b ?? "(none)"; int n; counts.TryGetValue(k, out n); counts[k] = n + 1; }
            return counts;
        }

        public sealed class SeamCrossing { public double Y, WestX, EastX, Inset; public string West, East; public int Run; }

        // Rows where a party crossing the seam (logical x = 0 = width) goes from one land biome into another: the west end `inset` units
        // before the cut (x = width - inset), the east end `inset` after it (x = inset), both land, of different biomes, and every sample
        // of the row between them (every `step`) land too, so the crossing never meets water. For each row the smallest of `insets` that
        // gives one. Ordered by the inset (the shortest crossing first), then by the run of neighbouring rows with the same pair (a broad
        // crossing first), then from the equator outward.
        public static List<SeamCrossing> Seam(Func<double, double, string> biomeAt, Func<string, bool> land, double width, double height, double step, double[] insets, int limit)
        {
            var rows = new List<SeamCrossing>();
            int n = Math.Max(1, (int)Math.Floor(height / step));
            var found = new SeamCrossing[n];
            for (int j = 0; j < n; j++)
            {
                double y = (j + .5) * step;
                foreach (var inset in insets.OrderBy(v => v))
                {
                    string west = biomeAt(width - inset, y), east = biomeAt(inset, y);
                    if (west == null || east == null || west == east || !land(west) || !land(east)) continue;
                    bool dry = true;
                    for (double x = -inset; x <= inset && dry; x += step) { string b = biomeAt(x < 0 ? width + x : x, y); dry = b != null && land(b); }
                    if (!dry) continue;
                    found[j] = new SeamCrossing { Y = y, WestX = width - inset, EastX = inset, Inset = inset, West = west, East = east };
                    break;
                }
            }
            for (int j = 0; j < n; j++)
            {
                var r = found[j];
                if (r == null) continue;
                int a = j, b = j;
                while (a > 0 && found[a - 1] != null && found[a - 1].West == r.West && found[a - 1].East == r.East) a--;
                while (b < n - 1 && found[b + 1] != null && found[b + 1].West == r.West && found[b + 1].East == r.East) b++;
                r.Run = Math.Min(j - a, b - j);
                rows.Add(r);
            }
            return rows.OrderBy(r => r.Inset).ThenByDescending(r => r.Run).ThenBy(r => Math.Abs(r.Y - height / 2)).Take(limit).ToList();
        }

        public static readonly double[] SeamInsets = { 1.5, 3, 5, 8, 12, 18, 25 };

        // Points on a ring (radius, count) around a centre, the centre first: the pad check around a settlement and the face samples around a fief.
        public static List<double[]> Ring(double x, double y, double radius, int count)
        {
            var list = new List<double[]> { new[] { x, y } };
            for (int k = 0; k < count; k++) { double a = 2 * Math.PI * k / count; list.Add(new[] { x + radius * Math.Cos(a), y + radius * Math.Sin(a) }); }
            return list;
        }

        // The largest slope (degrees) between the centre height and each ring height over the ring radius: a settlement's pad flatness.
        public static double PadSlopeDegrees(IList<double> heights, double radius)
        {
            if (heights == null || heights.Count < 2 || !(radius > 0)) return Double.NaN;
            double worst = 0;
            for (int i = 1; i < heights.Count; i++)
            {
                if (Double.IsNaN(heights[i]) || Double.IsNaN(heights[0])) return Double.NaN;
                worst = Math.Max(worst, Math.Atan(Math.Abs(heights[i] - heights[0]) / radius) * 180 / Math.PI);
            }
            return worst;
        }

        // The season factor's extremes (MBMapScene.GetSeasonTimeFactor: a triangle wave, 1 at mid-winter, 0 at mid-summer): whether a factor
        // counts as the winter or the summer extreme.
        public const double WinterFrom = 0.93, SummerBelow = 0.07;
        public static string Extreme(double season)
        {
            if (Double.IsNaN(season)) return null;
            return season >= WinterFrom ? "winter" : season <= SummerBelow ? "summer" : null;
        }

        // Share of the sampled days on which a place was Snowy (null when nothing was sampled).
        public static double? Share(IList<bool> days) { return days == null || days.Count == 0 ? (double?)null : days.Count(d => d) / (double)days.Count; }

        // ---------------------------------------------------------------- world 1's order (acc4 triage hang-2)

        // The core runs first: no mission and no encounter, so a native hang in a battle can no longer lose it. The cast step takes no battle
        // (its zone battle is w1.battle.mutated, from the cast save). The risky tail runs after the soak's save, the siege last (the slowest,
        // and MMR-52's hang class). w1.start / w1.load come before both.
        public static readonly string[] World1Core = { "w1.identity", "w1.census0", "w1.save0", "w1.frames", "w1.memoryDay", "w1.faces", "w1.biomes", "w1.relief", "w1.water",
            "w1.dressing", "w1.magic", "w1.magicWater", "w1.looks", "w1.speed", "w1.seam", "w1.incidents", "w1.ruinsPage", "w1.season", "w1.cast", "w1.saveCast", "w1.continue",
            "w1.soak", "w1.saveSoak" };
        public static readonly string[] World1Tail = { "w1.conversation", "w1.battle.snow", "w1.battle.forest", "w1.battle.settlement", "w1.battle.mutated", "w1.town",
            "w1.castle", "w1.village", "w1.siege" };

        // Each step's wall-clock budget in seconds (0: none). Past it the step is ended in process (status timeout), its iterator disposed (its
        // finally blocks end the mission, clear the encounter, put parties back, make peace) and the campaign recovered. A native hang (the
        // engine thread blocked) is out of reach here: the orchestrator's CPU-flat kill and relaunch cover it.
        private static readonly Dictionary<string, double> Budgets = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["identity"] = 120, ["census0"] = 300, ["save0"] = 300, ["frames"] = 300, ["memoryDay"] = 300, ["faces"] = 180, ["biomes"] = 900, ["relief"] = 300, ["water"] = 900,
            ["dressing"] = 300, ["magic"] = 600, ["magicWater"] = 300, ["looks"] = 900, ["speed"] = 180, ["seam"] = 300, ["incidents"] = 300, ["ruinsPage"] = 180, ["season"] = 2700,
            ["cast"] = 1200, ["saveCast"] = 300, ["continue"] = 1200, ["soak"] = 1800, ["saveSoak"] = 300, ["conversation"] = 90, ["battle.snow"] = 300, ["battle.forest"] = 300,
            ["battle.settlement"] = 300, ["battle.mutated"] = 600, ["town"] = 300, ["castle"] = 300, ["village"] = 300, ["siege"] = 1800,
            ["census"] = 300, ["after1"] = 300, ["decalLab"] = 900, ["saveLab"] = 300, ["hangRepro.war"] = 900, ["hangRepro.castle"] = 900 };
        public static double Budget(string step)
        {
            if (step == null) return 0;
            int dot = step.IndexOf('.');
            string key = dot < 0 ? step : step.Substring(dot + 1);
            double v; if (Budgets.TryGetValue(key, out v)) return v;
            return HangRepros.Any(r => r.Step == step) ? 900 : 0;   // every hang repro variant (MMR-247 X)
        }

        // ---------------------------------------------------------------- land-cell samples (acc4 triage fails-1)

        // The travel cells to sample the in-game face at: per land type (cells.bin's land type plane, class 0 = land), its cells in scan order
        // spread evenly over at most perType samples. Deterministic; cell indices (row-major) by type name order of the ids.
        public static SortedDictionary<int, List<int>> CellSamples(byte[] classes, byte[] landTypes, int perType)
        {
            if (classes == null || landTypes == null || classes.Length != landTypes.Length) throw new ArgumentException("the class and land type planes must have the same size");
            if (perType < 1) throw new ArgumentException("perType");
            var byType = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i] != 0 || landTypes[i] == 0) continue;
                List<int> l; if (!byType.TryGetValue(landTypes[i], out l)) byType[landTypes[i]] = l = new List<int>(); l.Add(i);
            }
            var result = new SortedDictionary<int, List<int>>();
            foreach (var kv in byType)
            {
                var all = kv.Value; var pick = new List<int>();
                if (all.Count <= perType) pick.AddRange(all);
                else for (int k = 0; k < perType; k++) pick.Add(all[(int)((k + 0.5) * all.Count / perType)]);
                result[kv.Key] = pick;
            }
            return result;
        }

        // MMR-247 (results/MMR-247-H.md): why a land case starts from a fresh load of the designated save, or null to run on the map as it is.
        // acc4 and acc5 blocked the engine thread four times (field.forest twice, castle, siege), each time in the first frames of a case that
        // teleported the player and changed its diplomacy (war declared, a castle given) after a mission had run on the same loaded map; those
        // actions on a map with no mission since its load (snow, settlement and mutated battles, MMR-52's pre-epic run) never blocked. So with
        // the rule enabled, a case after a mission on this map reloads first, when there is a save to reload.
        public static string FreshMapReason(bool enabled, bool missionOnThisMap, bool noReload, string save)
        {
            if (!enabled || !missionOnThisMap || noReload || String.IsNullOrEmpty(save)) return null;
            return "a mission has run on this map since it loaded (acc4/acc5 engine-thread blocks followed that): " + save + " loaded first";
        }

        // Whether no water lies at a point or at 8 points `radius` around it (hang-1: a battle point away from rivers, bridges and decks).
        public static bool DryAround(Func<double, double, bool> water, double x, double y, double radius)
        {
            foreach (var p in Ring(x, y, radius, 8)) if (water(p[0], p[1])) return false;
            return true;
        }

        // ---------------------------------------------------------------- the siege case's setup (MMR-247 F, acc6 c11)

        // SallyOutsCampaignBehavior (1.4.8 IL): a besieged fortification is checked on its hourly settlement tick in every campaign hour h with
        // Floor(h) % 4 == 0 (also when a map event starts against a party of the siege). Its garrison sallies out when the defenders' strength
        // (the settlement's SallyOut parties, GetCustomStrength(Attacker, PlainBattle), plus its faction's parties near the camp) exceeds
        // SallyRatio x the besiegers' near the camp (each times its aggressiveness, 1 for the main party; 1.5 x for a siege outside or a
        // blockade). acc6: 150 recruits against a 194-man garrison sallied out 2.06 h after the camp went up, before the advanced camp completed.
        public const int SallyCheckEvery = 4;
        public const double SallyRatio = 2;

        // Campaign hours from `now` to the start of the next hour in which the sally check runs; 0 inside such an hour (its check may be to come).
        public static double HoursToSallyCheck(double now)
        {
            if (Double.IsNaN(now) || Double.IsInfinity(now)) throw new ArgumentException("now");
            if ((long)Math.Floor(now) % SallyCheckEvery == 0) return 0;
            return Math.Ceiling(now / SallyCheckEvery) * SallyCheckEvery - now;
        }

        public sealed class SiegeTiming
        {
            public double WaitHours;     // campaign hours to let pass at peace, before the war and the camp (0: none)
            public double ObserveHours;  // the camp's own preparation observed this long, then advanced
            public double UntilCheck;    // hours from the camp going up (after the wait) to the next sally check hour
            public bool Fits;            // observe + completion + margin end before that hour
            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["waitHours"] = WaitHours, ["observeHours"] = ObserveHours, ["untilSallyCheck"] = UntilCheck, ["fits"] = Fits };
            }
        }

        // The siege's timing at campaign hour `now`: the camp's own preparation is observed for `observe` hours (shortened to fit, never under
        // `minObserve`: MMR-52 requires the camp to have built by itself), then advanced so the engine's tick completes it within `completion`
        // hours, all at least `margin` hours before the next sally check. When even the shortest observation does not fit, the case first lets
        // the check hour pass (at peace, before the war and the camp): a whole cycle less that hour is then free.
        public static SiegeTiming Timing(double now, double observe, double completion, double margin, double minObserve)
        {
            if (!(minObserve > 0) || observe < minObserve || completion < 0 || margin < 0) throw new ArgumentException("the siege timing needs observe >= minObserve > 0 and a non-negative completion and margin");
            double until = HoursToSallyCheck(now), wait = 0;
            if (until < minObserve + completion + margin)
            {
                double checkHour = until == 0 ? Math.Floor(now) : Math.Round(now + until);
                wait = checkHour + 1 - now;
                until = SallyCheckEvery - 1;
            }
            double obs = ObserveFor(until, observe, completion, margin, minObserve);
            return new SiegeTiming { WaitHours = wait, ObserveHours = obs, UntilCheck = until, Fits = obs + completion + margin <= until + 1e-9 };
        }

        // The observation that ends `completion + margin` before a sally check `untilCheck` hours away: `observe`, shortened, never under `minObserve`.
        public static double ObserveFor(double untilCheck, double observe, double completion, double margin, double minObserve)
        {
            return Math.Max(minObserve, Math.Min(observe, untilCheck - completion - margin));
        }

        // The progress the camp is advanced to: what the installed rate builds in the last `completion` hours, so the engine's own tick
        // completes it (SiegeEvent's tick calls CreateSiegeObject when it does), never below `floor` (MMR-52's siegeCompletionFrom) nor at 1.
        public static double CompletionFrom(double ratePerHour, double completion, double floor)
        {
            if (!(ratePerHour > 0) || Double.IsInfinity(ratePerHour)) return floor;
            return Math.Max(floor, Math.Min(0.9999, 1 - ratePerHour * completion));
        }

        // Troops to add to the besieger so the garrison does not sally out: SallyRatio x the besiegers' strength at least `margin` x the
        // defenders' (the engine sallies out above 1 x), each added troop bringing `perTroop`; at most `cap` (0 when nothing is needed or known).
        public static int SallyCrew(double defenders, double besiegers, double perTroop, double margin, int cap)
        {
            if (!(defenders > 0) || !(perTroop > 0) || cap <= 0) return 0;
            double need = margin * defenders / SallyRatio - Math.Max(0, besiegers);
            if (need <= 0) return 0;
            return (int)Math.Min(cap, Math.Ceiling(need / perTroop));
        }

        // Whether the stock rule sallies the garrison out against the besiegers (a plain siege, not outside or a blockade).
        public static bool SalliesOut(double defenders, double besiegers) { return defenders > SallyRatio * besiegers; }

        // ---------------------------------------------------------------- snow (acc4 triage notrun-6)

        // The installed DefaultMapWeatherModel snow rule (1.4.8 IL; GeneratedClimateSystem's constants): c = Lerp(0.55, -0.1, s),
        // v = SmoothStep(c - 0.65, c + 0.65, byte / 255), a place is Snowy when v^2 > 0.55. s (the season factor) is uniform over the year, so
        // the yearly Snowy share of a weather node's byte is the share of s in [0, 1] for which that holds.
        public static bool Snowy(double season, int snowByte)
        {
            double c = 0.55 + (-0.1 - 0.55) * season, t = Math.Max(0, Math.Min(1, (snowByte / 255.0 - (c - 0.65)) / 1.3)), v = t * t * (3 - 2 * t);
            return v * v > 0.55;
        }
        public static double SnowyShare(int snowByte)
        {
            const int steps = 2000; int n = 0;
            for (int k = 0; k < steps; k++) if (Snowy((k + 0.5) / steps, snowByte)) n++;
            return (double)n / steps;
        }

        // Whether a campaign hour (0..24) is in the daylight window the season screenshots are taken in.
        public const double MiddayFrom = 11, MiddayTo = 13;
        public static bool Midday(double hour) { return hour >= MiddayFrom && hour < MiddayTo; }

        public static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- the hang repro (MMR-247 H, run after the decal lab)

        // The deliberate repro of acc4/acc5's engine-thread blocks (results/MMR-247-H.md), each variant its own step from a map with no mission
        // since its load: a field battle, then the block's diplomacy change and teleport (war: as field.forest, a far teleport then war on a
        // fresh kingdom; castle: as the castle case, the castle given to the player then the teleport to its gate), then ReproFrames frames.
        // MMR-247 X: each variant also sets the bisect switches (results/MMR-247-X.md) for its launch: how the map dressing's bridges and ruin
        // markers take a new image (MapDressingInstaller.EntityImages: Recreate the fix, Move the pre-fix control, Frozen not at all), whether
        // the magic ground layers refresh (MagicGroundRuntime.RefreshEnabled) and whether the settlement display copies mirror their settlement
        // (PeriodicMapPresentation.CopySyncEnabled). war and castle run the product (the fix); the war* variants isolate the cause.
        public sealed class HangReproVariant
        {
            public string Step, Kind, Reading;
            public string DressingEntities = "Recreate";   // a World.EntityImageMode name (this file is compiled alone by its tests)
            public bool MagicRefresh = true, CopySync = true;
            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["kind"] = Kind, ["dressingEntities"] = DressingEntities, ["magicRefresh"] = MagicRefresh, ["copySync"] = CopySync, ["reading"] = Reading };
            }
        }
        public static readonly HangReproVariant[] HangRepros = {
            new HangReproVariant { Step = "w1.hangRepro.warControl", Kind = "war", DressingEntities = "Move", Reading = "the pre-fix behaviour: expected to crash (c0000005 at TaleWorlds.Native+0x55e625) as acc5rep did" },
            new HangReproVariant { Step = "w1.hangRepro.war", Kind = "war", Reading = "the fix (bridges and markers instantiated again at a new image): expected to run" },
            new HangReproVariant { Step = "w1.hangRepro.warNoDressingMoves", Kind = "war", DressingEntities = "Frozen", Reading = "pre-fix, the dressing entities never moved: runs if moving them is the cause" },
            new HangReproVariant { Step = "w1.hangRepro.warNoMagic", Kind = "war", DressingEntities = "Move", MagicRefresh = false, Reading = "pre-fix, the magic layers never refreshed: still crashes if the dressing entities are the cause" },
            new HangReproVariant { Step = "w1.hangRepro.warNoCopySync", Kind = "war", DressingEntities = "Move", CopySync = false, Reading = "pre-fix, the display copies never synced: still crashes if the dressing entities are the cause" },
            new HangReproVariant { Step = "w1.hangRepro.castle", Kind = "castle", Reading = "the fix on the castle path: expected to run" },
            new HangReproVariant { Step = "w1.hangRepro.castleControl", Kind = "castle", DressingEntities = "Move", Reading = "the castle path pre-fix: expected to crash (not in the default list)" },
        };
        public static readonly string[] HangReproVariants = HangRepros.Select(r => r.Step).ToArray();
        // The decal lab's own repro after its grids (the decallab phase): the product's two variants.
        public static readonly string[] LabReproVariants = { "w1.hangRepro.war", "w1.hangRepro.castle" };
        public const int ReproFrames = 300;
        public const double ReproTeleport = 256, ReproTeleportMin = 150;   // acc5's forest case moved the player about 256 units (790 -> 1046)

        // The far teleport's target: the candidate whose distance from `from` is nearest `want`, never nearer than `min`; -1 when none is
        // (ties: the first). distance: the map's (periodic) distance.
        public static int FarPoint(IList<double[]> candidates, double[] from, double want, double min, Func<double[], double[], double> distance)
        {
            int best = -1; double bestOff = Double.MaxValue;
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                double d = distance(from, candidates[i]);
                if (Double.IsNaN(d) || d < min) continue;
                double off = Math.Abs(d - want);
                if (off < bestOff) { best = i; bestOff = off; }
            }
            return best;
        }

        // ---------------------------------------------------------------- the decal lab (MMR-247 D, acc5)

        // What a lab column draws: a material (Texture: a diffuse texture swapped into a copy of it, else null), its intended atlas cell, the
        // SetFactor1 its factor rows use, and whether its box turns a quarter (a vanilla stroke runs along the texture's v).
        // MMR-291: Mesh names an overlay mesh column ("vein", "river", "magicWater": that layer's material on a test piece, OverlayMeshRuntime's
        // engine target), laid as its own grid on the same spot after the decal grid (LabMeshRows); null for a decal column.
        public sealed class LabColumn { public string Name, Material, Texture, Mesh; public float[] Cell; public uint Factor; public bool Turn; }

        // How a lab row frames its decals: atlas "full" ([1, 1, 0, 0]) or the column's "cell"; the column's factor or none; the frame: "ground"
        // (z the terrain at the centre, half depth 3: the road decals' and every water decal's before MMR-247 W), "fit" (W's water box: FitBox
        // as a pool, then clipped over the planes), "clip" (half depth 3, clipped over the planes: W's WaterPlanes.Box), "plane" (z the sea
        // datum, half depth 0.2: MMR-290's sea stamps); the box's half extents and half depth.
        // MMR-291-W: a mesh row's pass against the water (roads.json overlayMeshes passes) and its lift over the surface (NaN: the layer's).
        public sealed class LabRow { public string Name, Atlas, Frame, Pass; public bool Factor; public double Half, HalfDepth, Y, Lift = Double.NaN; }

        public static readonly LabRow[] LabRows = {
            new LabRow { Name = "full", Atlas = "full", Frame = "ground", Half = 1.4, HalfDepth = 3, Y = 0 },
            new LabRow { Name = "cell", Atlas = "cell", Frame = "ground", Half = 1.4, HalfDepth = 3, Y = 4 },
            new LabRow { Name = "factor", Atlas = "full", Factor = true, Frame = "ground", Half = 1.4, HalfDepth = 3, Y = 8 },
            new LabRow { Name = "fit", Atlas = "full", Frame = "fit", Half = 1.4, HalfDepth = 3, Y = 12 },
            new LabRow { Name = "clip", Atlas = "full", Frame = "clip", Half = 1.4, HalfDepth = 3, Y = 16 },
            new LabRow { Name = "plane", Atlas = "full", Frame = "plane", Half = 1.4, HalfDepth = 0.2, Y = 20 },
            new LabRow { Name = "large", Atlas = "full", Factor = true, Frame = "ground", Half = 2.3, HalfDepth = 6, Y = 25.5 } };
        public const double LabColumnPitch = 5;
        // MMR-291-W: the rows a mesh column draws (acc6lab answered MMR-291's rows full, cell, factor, plane and large: the template copies draw on
        // land and nothing on the sea, even flat on the sea plane): its cell piece in each pass against the water (roads.json overlayMeshes passes:
        // blend the copy as it is, depth with depth write and alpha test, forward, afterPostfx), on the spot's surface (frame "surface": the ground at
        // the land spot, the sea datum at the sea spot) plus the layer's lift; under: the depth pass 0.05 below the surface (at the sea spot under the
        // water's skin, seen only through its shallow refraction; under the ground at the land spot). A river column draws its ribbon on the ground in
        // every row (the control: the water material draws over our sea, acc6lab).
        public static readonly LabRow[] LabMeshRowDefs = {
            new LabRow { Name = "blend", Atlas = "cell", Frame = "surface", Pass = "blend", Half = 1.4, Y = 0 },
            new LabRow { Name = "depth", Atlas = "cell", Frame = "surface", Pass = "depth", Half = 1.4, Y = 4 },
            new LabRow { Name = "under", Atlas = "cell", Frame = "surface", Pass = "depth", Lift = -0.05, Half = 1.4, Y = 8 },
            new LabRow { Name = "forward", Atlas = "cell", Frame = "surface", Pass = "forward", Half = 1.4, Y = 12 },
            new LabRow { Name = "afterPostfx", Atlas = "cell", Frame = "surface", Pass = "afterPostfx", Half = 1.4, Y = 16 } };
        public static readonly string[] LabMeshRows = LabMeshRowDefs.Select(r => r.Name).ToArray();

        // One lab decal: its column and row, offset (dx, dy) from the lab's centre (columns west to east, rows south to north: nearest the
        // camera at bearing 0 first), atlas, factor (0: none), frame, turn and box. MMR-291: Mesh names a mesh column's layer; MMR-291-W: a mesh
        // piece's pass and lift (NaN: the layer's).
        public sealed class LabDecal
        {
            public string Id, Column, Row, Material, Texture, Frame, Mesh, Pass; public float[] Atlas; public uint Factor; public bool Turn;
            public double Dx, Dy, Half, HalfDepth, Lift = Double.NaN;
            public Dictionary<string, object> Record()
            {
                return new Dictionary<string, object> { ["id"] = Id, ["column"] = Column, ["row"] = Row, ["material"] = Material, ["texture"] = Texture, ["frame"] = Frame,
                    ["atlas"] = Atlas.Select(v => (double)v).ToArray(), ["factor"] = Factor == 0 ? null : Factor.ToString("x8", CultureInfo.InvariantCulture), ["turn"] = Turn,
                    ["offset"] = new[] { Dx, Dy }, ["half"] = Half, ["halfDepth"] = HalfDepth, ["mesh"] = Mesh, ["pass"] = Pass, ["lift"] = Double.IsNaN(Lift) ? null : (object)Lift };
            }
        }

        // The lab's grid: every column in every row, centred on the lab's spot; MMR-291: the mesh columns in their rows (LabMeshRowDefs) as a
        // second grid centred on the same spot (drawn after the decal grid is gone).
        public static List<LabDecal> LabLayout(IList<LabColumn> columns)
        {
            if (columns == null || columns.Count == 0) throw new ArgumentException("the decal lab needs columns");
            var list = new List<LabDecal>();
            var decals = columns.Where(c => c.Mesh == null).ToList(); var meshes = columns.Where(c => c.Mesh != null).ToList();
            foreach (var group in new[] { decals, meshes })
            {
                var rows = group == meshes ? LabMeshRowDefs : LabRows;
                double y0 = (rows.First().Y + rows.Last().Y) / 2;
                for (int c = 0; c < group.Count; c++)
                {
                    var col = group[c];
                    if (col.Cell == null || col.Cell.Length != 4) throw new ArgumentException("lab column " + col.Name + " has no atlas cell");
                    foreach (var row in rows)
                        list.Add(new LabDecal { Id = col.Name + "/" + row.Name, Column = col.Name, Row = row.Name, Material = col.Material, Texture = col.Texture, Frame = row.Frame, Mesh = col.Mesh,
                            Atlas = row.Atlas == "cell" ? col.Cell : new[] { 1f, 1f, 0f, 0f }, Factor = row.Factor ? col.Factor : 0u, Turn = col.Turn, Pass = row.Pass, Lift = row.Lift,
                            Dx = (c - (group.Count - 1) / 2.0) * LabColumnPitch, Dy = row.Y - y0, Half = row.Half, HalfDepth = row.HalfDepth });
                }
            }
            return list;
        }

        // The lab's two spots among candidate points (physical): the flattest dry one (its ground spread over the lab's footprint, sampled every
        // `step`, below maxSpread; every sample land, above the datum by `above`; no avoided point within `clear`), and the sea one nearest it whose
        // every footprint sample lies at least `deep` below the datum. Null when none qualifies.
        public static double[] LabLand(IEnumerable<double[]> candidates, Func<double, double, double> ground, Func<double, double, bool> water, IList<double[]> avoid,
                                       double halfX, double halfY, double step, double maxSpread, double above, double clear)
        {
            double[] best = null; double bestSpread = Double.MaxValue;
            foreach (var p in candidates)
            {
                if (avoid != null && avoid.Any(q => Math.Abs(q[0] - p[0]) < halfX + clear && Math.Abs(q[1] - p[1]) < halfY + clear)) continue;
                double lo = Double.MaxValue, hi = Double.MinValue; bool ok = true;
                for (double dx = -halfX; dx <= halfX + 1e-9 && ok; dx += step)
                    for (double dy = -halfY; dy <= halfY + 1e-9 && ok; dy += step)
                    {
                        double g = ground(p[0] + dx, p[1] + dy);
                        if (Double.IsNaN(g) || water(p[0] + dx, p[1] + dy) || g < above) { ok = false; break; }
                        lo = Math.Min(lo, g); hi = Math.Max(hi, g);
                    }
                if (!ok || hi - lo > maxSpread || hi - lo >= bestSpread) continue;
                best = p; bestSpread = hi - lo;
            }
            return best;
        }

        public static double[] LabSea(IEnumerable<double[]> candidates, Func<double, double, double> ground, double[] near, double halfX, double halfY, double step, double deep)
        {
            double[] best = null; double bestDistance = Double.MaxValue;
            foreach (var p in candidates)
            {
                double d = near == null ? 0 : Math.Sqrt(Math.Pow(p[0] - near[0], 2) + Math.Pow(p[1] - near[1], 2));
                if (d >= bestDistance) continue;
                bool ok = true;
                for (double dx = -halfX; dx <= halfX + 1e-9 && ok; dx += step)
                    for (double dy = -halfY; dy <= halfY + 1e-9 && ok; dy += step)
                    {
                        double g = ground(p[0] + dx, p[1] + dy);
                        if (Double.IsNaN(g) || g > -deep) { ok = false; break; }
                    }
                if (!ok) continue;
                best = p; bestDistance = d;
            }
            return best;
        }
    }
}
