using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MNBL.ProceduralWorld.CampaignRegistry
{
    // MMR-34: where a generated world's parties may spawn (docs/campaign-behavior-compatibility.md). Pure managed; the in-game
    // counterpart is GeneratedSpawnAssertions, which checks live parties against the engine navmesh.
    //   * CellMap: the MMR-28 travel cells (cells.bin v2 after repairs: "MNBLTC02", u32 cols, u32 rows, cols*rows class bytes first); a cell
    //     (r, c) covers logical [c, c+1] x [r, r+1] * spacing, columns wrap. Passable cells are the policy's classes (land); land
    //     components are 4-neighbour connected passable cells, wrapping east-west. A component belongs to the MMR-28 land group of the
    //     settlements on it (the compiler's groups join components across its required bridges); a component with no settlement has
    //     no group and holds no spawn.
    //   * Anchors: the positions the installed behaviours spawn parties at (player start, caravans at town gates, villagers at village
    //     centres, lords at their clan's home settlement gate, bandits at hideouts). Each must be passable and in a land component
    //     that holds a town; a failing anchor gets an actionable problem naming the subject, the cell and its class.
    internal static class GeneratedSpawnModel
    {
        // Travel cell classes (src/WorldBridge/include/worldbridge/travel_graph.hpp, enum Cls).
        private static readonly string[] ClassNames = { "land", "mountain", "sea", "lake", "river" };

        public sealed class Cell { public int Row, Col; public double X, Y; }

        public sealed class Anchor { public string Kind, Subject, Problem; public double X, Y; public int Row, Col, Group; public string CellClass; }

        public sealed class CellMap
        {
            private readonly byte[] classes;
            private readonly bool[] passable;
            private readonly int[] component;
            private readonly Dictionary<int, int> groupOf = new Dictionary<int, int>();
            public int Cols { get; private set; }
            public int Rows { get; private set; }
            public double Spacing { get; private set; }
            public double Width { get { return Cols * Spacing; } }

            // travelSettlements: the travel graph's settlements (point, group), which give each component its MMR-28 land group.
            public CellMap(IDictionary<string, object> grid, byte[] cellsBin, IEnumerable<string> passableClasses, IEnumerable<IDictionary<string, object>> travelSettlements)
            {
                if (cellsBin == null || cellsBin.Length < 16 || Encoding.ASCII.GetString(cellsBin, 0, 8) != "MNBLTC02") throw new ArgumentException("cells.bin is not MNBLTC02");
                Cols = BitConverter.ToInt32(cellsBin, 8); Rows = BitConverter.ToInt32(cellsBin, 12);
                Spacing = Convert.ToDouble(grid["spacing"], CultureInfo.InvariantCulture);
                if (Cols != Convert.ToInt32(grid["cols"], CultureInfo.InvariantCulture) || Rows != Convert.ToInt32(grid["rows"], CultureInfo.InvariantCulture) || cellsBin.Length != 16 + 4 * Cols * Rows)
                    throw new ArgumentException("cells.bin does not match the travel grid");
                classes = new byte[Cols * Rows]; Array.Copy(cellsBin, 16, classes, 0, classes.Length);
                var allowed = new HashSet<int>(passableClasses.Select(n => Array.IndexOf(ClassNames, n)).Where(i => i >= 0));
                passable = classes.Select(c => allowed.Contains(c)).ToArray();
                component = Enumerable.Repeat(-1, classes.Length).ToArray();
                int next = 0;
                for (int start = 0; start < classes.Length; start++)
                {
                    if (!passable[start] || component[start] >= 0) continue;
                    var queue = new Queue<int>(); queue.Enqueue(start); component[start] = next;
                    while (queue.Count > 0)
                    {
                        int i = queue.Dequeue(), r = i / Cols, c = i % Cols;
                        foreach (var n in new[] { r > 0 ? i - Cols : -1, r < Rows - 1 ? i + Cols : -1, r * Cols + (c + Cols - 1) % Cols, r * Cols + (c + 1) % Cols })
                            if (n >= 0 && passable[n] && component[n] < 0) { component[n] = next; queue.Enqueue(n); }
                    }
                    next++;
                }
                Conflicts = new List<string>();
                foreach (var s in travelSettlements)
                {
                    if (!s.ContainsKey("point")) continue;   // an unplaceable (excluded) settlement: no point, no group
                    var p = (IDictionary<string, object>)s["point"];
                    int comp = Component(Convert.ToDouble(p["x"], CultureInfo.InvariantCulture), Convert.ToDouble(p["y"], CultureInfo.InvariantCulture));
                    int group = Convert.ToInt32(s["group"], CultureInfo.InvariantCulture);
                    int known;
                    if (comp < 0) continue;
                    if (groupOf.TryGetValue(comp, out known) && known != group) Conflicts.Add("land component " + comp + " holds settlements of groups " + known + " and " + group);
                    else groupOf[comp] = group;
                }
            }

            public List<string> Conflicts { get; private set; }

            public int Index(double x, double y)
            {
                if (double.IsNaN(x) || double.IsNaN(y) || y < 0 || y >= Rows * Spacing) return -1;
                int c = (int)Math.Floor(x / Spacing) % Cols; if (c < 0) c += Cols;
                int r = (int)Math.Floor(y / Spacing);
                return r * Cols + c;
            }
            public bool Passable(double x, double y) { int i = Index(x, y); return i >= 0 && passable[i]; }
            public int Component(double x, double y) { int i = Index(x, y); return i < 0 ? -1 : component[i]; }
            // The MMR-28 land group of a position's component, or -1 (not passable, or a component without settlements).
            public int Group(double x, double y) { int c = Component(x, y), g; return c >= 0 && groupOf.TryGetValue(c, out g) ? g : -1; }
            public string ClassOf(double x, double y) { int i = Index(x, y); return i < 0 ? "outside" : classes[i] < ClassNames.Length ? ClassNames[classes[i]] : "class " + classes[i]; }
            // MMR-287: whether a cell of the class lies within radius cells (rows and columns, columns wrap) of a cell.
            public bool Near(int row, int col, int radius, string className)
            {
                int wanted = Array.IndexOf(ClassNames, className);
                if (wanted < 0) return false;
                for (int r = Math.Max(0, row - radius); r <= Math.Min(Rows - 1, row + radius); r++)
                    for (int d = -radius; d <= radius; d++)
                        if (classes[r * Cols + ((col + d) % Cols + Cols) % Cols] == wanted) return true;
                return false;
            }
            // The passable cells of a land group (every component that belongs to it).
            public IEnumerable<Cell> Cells(int landGroup)
            {
                int g;
                for (int i = 0; i < component.Length; i++)
                    if (component[i] >= 0 && groupOf.TryGetValue(component[i], out g) && g == landGroup)
                        yield return new Cell { Row = i / Cols, Col = i % Cols, X = (i % Cols + 0.5) * Spacing, Y = (i / Cols + 0.5) * Spacing };
            }
            // Distance on the periodic world (columns wrap; rows do not).
            public double Distance(double x1, double y1, double x2, double y2)
            {
                double dx = Math.Abs(x1 - x2); dx = Math.Min(dx, Width - dx);
                double dy = y1 - y2;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public static List<Anchor> Anchors(CellMap map, GeneratedCampaignInitializer.Result startup, SettlementFactory.Result world, FactionFactory.Result factions, HashSet<int> townGroups)
        {
            var list = new List<Anchor>();
            var byId = world.Settlements.ToDictionary(s => s.Id, StringComparer.Ordinal);
            Action<string, string, double, double> add = (kind, subject, x, y) => {
                int i = map.Index(x, y), group = map.Group(x, y);
                var a = new Anchor { Kind = kind, Subject = subject, X = x, Y = y, Row = i < 0 ? -1 : i / map.Cols, Col = i < 0 ? -1 : i % map.Cols, Group = group, CellClass = map.ClassOf(x, y) };
                if (!map.Passable(x, y)) a.Problem = kind + " spawn anchor " + subject + " at (" + Number(x) + ", " + Number(y) + ") is on a " + a.CellClass + " cell (" + a.Row + ", " + a.Col + "), not passable land: fix the MMR-28 placement or let MMR-40 snap it";
                else if (!townGroups.Contains(group)) a.Problem = kind + " spawn anchor " + subject + " at (" + Number(x) + ", " + Number(y) + ") is on land group " + group + ", which holds no town";
                list.Add(a); };
            foreach (var s in startup.Starts) add("player", s.Culture + ":" + s.Town, s.X, s.Y);
            foreach (var t in world.Settlements.Where(s => s.Kind == "town")) add("caravan", t.Id, t.GateX, t.GateY);
            foreach (var v in world.Settlements.Where(s => s.Kind == "village")) add("villager", v.Id, v.X, v.Y);
            foreach (var c in factions.AllClans.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                SettlementFactory.SettlementIntent home;
                if (c.HomeId == null || !byId.TryGetValue(c.HomeId, out home)) { list.Add(new Anchor { Kind = "lord", Subject = c.Id, Problem = "lord spawn anchor of clan " + c.Id + " has no generated home settlement (" + c.HomeId + "): MMR-31 must give it one" }); continue; }
                add("lord", c.Id + "@" + home.Id, home.GateX, home.GateY);
            }
            foreach (var h in startup.Hideouts) add("bandit", h.Id, h.X, h.Y);
            return list;
        }

        private static string Number(double x) { return x.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
