using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MNBL.ProceduralWorld.World
{
    // MMR-254: the land speed terms of a generated world (docs/runtime-navigation.md "Land face types and speed"), pure managed; the engine
    // seam is GeneratedPartySpeedModel. Every factor is the policy's (worldgen/policies/roads.json speed), except the ford factor a road deck
    // cancels, which is the installed DefaultPartySpeedCalculatingModel's own (passed in):
    //   * terrain  a factor per land face TerrainType (Snow, Swamp: the engine has no term of its own), on land only;
    //   * road     the road factor on a road cell (cells.bin v2 road bit), on land, or at sea too when roadAtSea;
    //   * ford     on a road deck (a Bridge face on a road cell, on land) the installed ford factor back, when cancelFordOnDecks.
    // Terms are written into caller arrays so the per-party hot path allocates nothing.
    internal sealed class RoadSpeed
    {
        public const int Terrain = 0, Road = 1, Ford = 2;
        public static readonly string[] TermNames = { "terrain", "road", "ford" };
        private readonly float[] terrain;
        private readonly int bridge;
        public float RoadFactor { get; private set; }
        public float FordCancel { get; private set; }
        public bool RoadAtSea { get; private set; }
        public bool CancelFordOnDecks { get; private set; }

        // terrainId: the installed TerrainType id of a name (TerrainType enum); fordFactor: the installed ford factor (negative).
        public RoadSpeed(RoadsPolicy policy, float fordFactor, Func<string, int> terrainId, int maxTerrain)
        {
            if (policy == null) throw new ArgumentNullException("policy");
            if (!(fordFactor < 0)) throw new InvalidDataException("The installed ford factor must be negative, not " + fordFactor.ToString(CultureInfo.InvariantCulture) + ".");
            terrain = new float[maxTerrain + 1];
            foreach (var kv in policy.TerrainFactors) terrain[terrainId(kv.Key)] = (float)kv.Value;
            bridge = terrainId("Bridge");
            RoadFactor = (float)policy.RoadFactor; FordCancel = -fordFactor; RoadAtSea = policy.RoadAtSea; CancelFordOnDecks = policy.CancelFordOnDecks;
        }

        public float TerrainFactor(int type) { return type >= 0 && type < terrain.Length ? terrain[type] : 0f; }

        // The terms for a party on face type `type`: count written, factors[k] and kinds[k] (Terrain, Road, Ford) in that order.
        public int Terms(int type, bool atSea, bool roadCell, float[] factors, int[] kinds)
        {
            int n = 0;
            if (!atSea)
            {
                float t = TerrainFactor(type);
                if (t != 0) { factors[n] = t; kinds[n++] = Terrain; }
            }
            if (roadCell && (!atSea || RoadAtSea)) { factors[n] = RoadFactor; kinds[n++] = Road; }
            if (roadCell && !atSea && CancelFordOnDecks && type == bridge) { factors[n] = FordCancel; kinds[n++] = Ford; }
            return n;
        }

        // The road bit of a world's travel cells by physical position (cells.bin v2 plane 4, bit 0; logical = physical - strip, pole margin;
        // columns wrap, rows outside the pole lines are no road).
        internal sealed class Cells
        {
            private readonly byte[] road;
            public int Cols { get; private set; }
            public int Rows { get; private set; }
            public double Spacing { get; private set; }
            public double OriginX { get; private set; }
            public double OriginY { get; private set; }
            public int RoadCells { get; private set; }

            public Cells(byte[] cellsBin, int cols, int rows, int columnOffset, int rowOffset, double spacing)
            {
                if (cellsBin == null || cellsBin.Length < 16 || Encoding.ASCII.GetString(cellsBin, 0, 8) != "MNBLTC02") throw new InvalidDataException("cells.bin is not MNBLTC02.");
                if (BitConverter.ToInt32(cellsBin, 8) != cols || BitConverter.ToInt32(cellsBin, 12) != rows || cellsBin.Length != 16 + 4 * cols * rows || !(spacing > 0))
                    throw new InvalidDataException("cells.bin does not match the travel grid (" + cols + " x " + rows + ").");
                int n = cols * rows;
                road = new byte[n];
                for (int i = 0; i < n; i++) if ((cellsBin[16 + 3 * n + i] & 1) != 0) { road[i] = 1; RoadCells++; }
                Cols = cols; Rows = rows; Spacing = spacing; OriginX = columnOffset * spacing; OriginY = rowOffset * spacing;
            }

            // From a bundle's travel-graph.json grid (cols, rows, columnOffset, rowOffset, spacing) and its cells.bin.
            public static Cells FromGrid(IDictionary<string, object> grid, byte[] cellsBin)
            {
                Func<string, int> i = k => Convert.ToInt32(grid[k], CultureInfo.InvariantCulture);
                return new Cells(cellsBin, i("cols"), i("rows"), i("columnOffset"), i("rowOffset"), Convert.ToDouble(grid["spacing"], CultureInfo.InvariantCulture));
            }

            public bool At(double x, double y)
            {
                double lx = (x - OriginX) / Spacing, ly = (y - OriginY) / Spacing;
                if (double.IsNaN(lx) || double.IsNaN(ly) || ly < 0 || ly >= Rows) return false;
                int c = (int)Math.Floor(lx) % Cols; if (c < 0) c += Cols;
                return road[(int)Math.Floor(ly) * Cols + c] != 0;
            }
        }
    }
}
