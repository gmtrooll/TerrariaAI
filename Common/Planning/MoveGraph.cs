using System;
using System.Collections.Generic;
using System.IO;

namespace FirstMod_Sp.Common.Planning
{
    /// <summary>
    /// The physics movement graph of one world and loadout, built offline from MoveSampler edges
    /// (Training/navigate/movegraph.py, file Training/movegraphs/&lt;world&gt;_l&lt;loadout&gt;.bin: int32 rows
    /// from_x, from_y, to_x, to_y, ticks, hurt, program). Nodes are standing spots (feet cells). Only moves that
    /// landed without losing life are kept.
    /// </summary>
    public sealed class MoveGraph
    {
        private readonly Dictionary<long, int> nodeOf = new Dictionary<long, int>();
        private readonly List<int> nodeX = new List<int>(), nodeY = new List<int>();
        // Reverse adjacency (CSR): for node d, the moves p -> d as (p, ticks).
        private int[] inStart = Array.Empty<int>(), inFrom = Array.Empty<int>(), inTicks = Array.Empty<int>();

        public int NodeCount => nodeX.Count;
        public int EdgeCount => inFrom.Length;

        private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        public int NodeAt(int x, int y) => nodeOf.TryGetValue(Key(x, y), out int n) ? n : -1;
        public int X(int node) => nodeX[node];
        public int Y(int node) => nodeY[node];

        private int AddNode(int x, int y) {
            long k = Key(x, y);
            if (nodeOf.TryGetValue(k, out int n)) return n;
            n = nodeX.Count;
            nodeOf[k] = n;
            nodeX.Add(x);
            nodeY.Add(y);
            return n;
        }

        public static MoveGraph Load(string path) {
            var g = new MoveGraph();
            var from = new List<int>();
            var to = new List<int>();
            var ticks = new List<int>();
            using (var r = new BinaryReader(File.OpenRead(path))) {
                long rows = r.BaseStream.Length / (7 * 4);
                for (long i = 0; i < rows; i++) {
                    int fx = r.ReadInt32(), fy = r.ReadInt32(), tx = r.ReadInt32(), ty = r.ReadInt32(), t = r.ReadInt32(), hurt = r.ReadInt32();
                    r.ReadInt32();  // program
                    int a = g.AddNode(fx, fy);
                    if (tx < 0 || hurt != 0 || t <= 0) continue;
                    int b = g.AddNode(tx, ty);
                    if (a == b) continue;
                    from.Add(a);
                    to.Add(b);
                    ticks.Add(t);
                }
            }
            // Group the moves by their destination.
            int n = g.NodeCount;
            g.inStart = new int[n + 1];
            foreach (int b in to) g.inStart[b + 1]++;
            for (int i = 0; i < n; i++) g.inStart[i + 1] += g.inStart[i];
            g.inFrom = new int[from.Count];
            g.inTicks = new int[from.Count];
            var fill = (int[])g.inStart.Clone();
            for (int e = 0; e < from.Count; e++) {
                int slot = fill[to[e]]++;
                g.inFrom[slot] = from[e];
                g.inTicks[slot] = ticks[e];
            }
            return g;
        }

        /// <summary>Cheapest time in ticks from every node to a goal node (backward Dijkstra); -1 = cannot reach.</summary>
        public int[] TicksTo(IEnumerable<int> goals) {
            var dist = new int[NodeCount];
            Array.Fill(dist, -1);
            var queue = new PriorityQueue<int, int>();
            foreach (int g in goals) {
                if (g < 0 || dist[g] == 0) continue;
                dist[g] = 0;
                queue.Enqueue(g, 0);
            }
            while (queue.TryDequeue(out int d, out int dd)) {
                if (dd != dist[d]) continue;
                for (int e = inStart[d]; e < inStart[d + 1]; e++) {
                    int p = inFrom[e], nd = dd + inTicks[e];
                    if (dist[p] >= 0 && dist[p] <= nd) continue;
                    dist[p] = nd;
                    queue.Enqueue(p, nd);
                }
            }
            return dist;
        }
    }

    /// <summary>
    /// Physics route distance to one target, in tile-equivalents (ticks at full run speed per tile, 16 / 3), so the
    /// progress reward keeps the scale of the grid planner's. Option C (docs/Physics Planner Design.md): used for the
    /// reward and time limits only; the observation's waypoints still come from the grid planner.
    /// </summary>
    public sealed class PhysicsField
    {
        public const float TicksPerTile = 16f / 3f;
        private readonly MoveGraph graph;
        private readonly int[] ticks;

        public PhysicsField(MoveGraph graph, float targetX, float targetY, float goalRadiusTiles) {
            this.graph = graph;
            // Goals: standing spots whose body centre (about 1.3 tiles above the feet) is near the target, as in
            // PathDistanceField's seeds.
            var goals = new List<int>();
            int r = (int)Math.Ceiling(goalRadiusTiles) + 2;
            for (int x = (int)targetX - r; x <= (int)targetX + r; x++) {
                for (int y = (int)targetY - r; y <= (int)targetY + r + 2; y++) {
                    int n = graph.NodeAt(x, y);
                    if (n < 0) continue;
                    float cx = x + 0.5f - targetX, cy = y + 1f - 1.3f - targetY;
                    if (cx * cx + cy * cy <= goalRadiusTiles * goalRadiusTiles) goals.Add(n);
                }
            }
            ticks = graph.TicksTo(goals);
        }

        /// <summary>Distance from a standing spot, in tile-equivalents; -1 if it is no node or cannot reach.</summary>
        public float Distance(int x, int y) {
            int n = graph.NodeAt(x, y);
            if (n < 0 || ticks[n] < 0) return -1f;
            return ticks[n] / TicksPerTile;
        }
    }
}
