using System;
using System.Collections.Generic;

namespace FirstMod_Sp.Common.Systems
{
    public enum PathTile : byte { Air = 0, Platform = 1, Solid = 2 }

    /// <summary>
    /// World-anchored "how long is the walk/jump to the target" field, built once per target by a reverse
    /// cheapest-path search and then looked up per bot per decision tick. Distances are in tile-equivalents:
    /// one ordinary tile costs 1, slow cells more (see cell info), so the time limit and the progress reward follow
    /// the real time a route takes.
    ///
    /// Movement model (a cell is the tile the player's feet occupy):
    ///  - A cell is passable if it and the two cells above it are not Solid, and the same holds for the
    ///    column to its left or to its right: the player is 42 x 20 px (Player.width/height), so its
    ///    body always covers 3 rows and 2 columns. One-tile-wide shafts and notches are not passable.
    ///  - With consistentBody, a step down also needs the same side column clear in both cells (counting only
    ///    full blocks: slopes and half blocks let the body through, the bots solve such routes). Steps up are
    ///    exempt: walking into a 1-tile ledge lifts the player onto it in one tick (Collision.StepUp), which
    ///    switches sides without sweeping through the gap. Otherwise
    ///    a cell clear only on its right can connect to the cell below that is clear only on its left: that
    ///    "switches sides" through a 1-tile gap the 1.25-tile body cannot pass (a drop under an overhang,
    ///    eval_02 3869,396: 7 of navigate_v1's 37 failures, Oct 20).
    ///  - Left/right: always allowed into a passable cell (walking or air control).
    ///  - Down: allowed into any passable cell, also through platforms (holding down drops through them).
    ///  - Up: allowed into a passable cell only if it is at most maxJumpTiles above the first
    ///    supporting tile (Solid or Platform) below it in the same column. This stops the search from
    ///    finding paths straight up into the sky. Platforms can be jumped through from below.
    ///    Taking off in honey (with cell info) lowers that height: Player.HoneyCollision moves the body at a
    ///    quarter of its velocity, so a jump from a honey pool's floor lifts the feet about 3 tiles at any depth
    ///    (no swimming without flippers), and the extra jump adds less (HoneyJumpTiles; navigate.liquid_jump).
    ///    So does a honey block floor (InfoSticky, StickyJumpTiles).
    /// Optional cell info (docs/Movement Hazards.md) adds:
    ///  - costs: water 2, honey 4, cobweb 8 per cell the body is in (cobwebs cap speed and cancel jumps), 4 per cell
    ///    standing on a honey block (run speed x 0.25);
    ///  - blocked cells: the body touching a damaging tile, lava or shimmer, or within 2 tiles of a trap;
    ///  - unsafe falls: air cells more than maxSafeFallTiles above their landing are blocked, unless the fall
    ///    ends in liquid or on a tile that negates fall damage (falls over 25 tiles hurt: Player.cs:24324).
    ///
    /// This file has no Terraria dependency so the algorithm can be compiled and tested alone.
    /// </summary>
    public sealed class PathDistanceField
    {
        public const int UNREACHABLE = -1;

        // Cell info flags (per tile, from the infoAt callback).
        public const ushort InfoWater = 1, InfoHoney = 2, InfoWeb = 4, InfoHurts = 8, InfoTrap = 16, InfoSoftLanding = 32, InfoLethal = 64,
            InfoPartial = 128;  // a sloped or half block: Solid for the planner, but bodies slide along / into it
        public const ushort InfoSticky = 256;  // a honey block (Player.sticky while standing on it: jumpHeight / 10, jumpSpeed / 5)
        public const int CostWater = 2, CostHoney = 4, CostSticky = 4, CostWeb = 8, MaxCost = 8;

        /// <summary>
        /// Jump height from the floor of a honey pool `depth` cells deep, for a player whose dry jump is maxJumpTiles.
        /// Measured feet rise (navigate.liquid_jump, Oct 20): dry 6.1 one jump / 13.1 with Tsunami's extra jump;
        /// honey 3.1 one jump at every depth; with the extra jump at its best moment (--sweep) 10.1 from pools 1-3
        /// deep (it fires once the body is out), 6.1-6.5 from deeper ones (it fires inside). Water jumps go as high
        /// as dry ones or higher: not limited.
        /// </summary>
        public static int HoneyJumpTiles(int maxJumpTiles, int depth) =>
            maxJumpTiles <= 6 ? Math.Min(maxJumpTiles, 3) : depth <= 3 ? Math.Min(maxJumpTiles, 10) : 6;

        /// <summary>
        /// Jump height from a honey block floor (Player.sticky). Measured (navigate.liquid_jump --floor): 1.85 tiles
        /// for one jump; Tsunami's extra jump fires in the air at full strength, 8.8 at its best moment.
        /// </summary>
        public static int StickyJumpTiles(int maxJumpTiles) => maxJumpTiles <= 6 ? 1 : Math.Min(maxJumpTiles, 8);

        public readonly int OriginX; // world tile of field cell (0, 0)
        public readonly int OriginY;
        public readonly int Size;
        private readonly int[] _dist;
        private readonly byte[] _next; // per cell: the BFS step toward the target (0 none, 1 right, 2 left, 3 down, 4 up)

        private static readonly int[] StepX = { 0, 1, -1, 0, 0 };
        private static readonly int[] StepY = { 0, 0, 0, 1, -1 };

        private PathDistanceField(int originX, int originY, int size, int[] dist, byte[] next)
        {
            OriginX = originX;
            OriginY = originY;
            Size = size;
            _dist = dist;
            _next = next;
        }

        /// <summary>Path distance in tiles from a feet cell, or UNREACHABLE / outside the field.</summary>
        public int GetDistance(int worldTileX, int worldTileY)
        {
            int x = worldTileX - OriginX;
            int y = worldTileY - OriginY;
            if (x < 0 || y < 0 || x >= Size || y >= Size) return UNREACHABLE;
            return _dist[y * Size + x];
        }

        /// <summary>The cell <paramref name="steps"/> moves further along the planned path from a feet cell (or the
        /// end of the path, if nearer). When the feet cell is off the field (e.g. mid-jump), starts from the nearest
        /// reachable cell within 2 columns and 4 rows. Returns false if there is none.</summary>
        public bool Waypoint(int worldTileX, int worldTileY, int steps, out int waypointX, out int waypointY)
        {
            waypointX = waypointY = 0;
            int bx = worldTileX - OriginX, by = worldTileY - OriginY;
            int start = -1, best = int.MaxValue;
            for (int r = 0; r <= 4 && start < 0; r++)
            {
                for (int y = by - r; y <= by + r; y++)
                {
                    for (int x = bx - Math.Min(r, 2); x <= bx + Math.Min(r, 2); x++)
                    {
                        if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                        int d = _dist[y * Size + x];
                        if (d != UNREACHABLE && d < best) { best = d; start = y * Size + x; }
                    }
                }
            }
            if (start < 0) return false;
            int cell = start;
            for (int i = 0; i < steps && _next[cell] != 0; i++)
                cell += StepY[_next[cell]] * Size + StepX[_next[cell]];
            waypointX = OriginX + cell % Size;
            waypointY = OriginY + cell / Size;
            return true;
        }

        /// <param name="tileAt">Classifies world tile (x, y); called once per field cell.</param>
        /// <param name="targetTileX">Target position in tiles (fractional).</param>
        /// <param name="radius">Field covers target +/- radius tiles.</param>
        /// <param name="goalRadiusTiles">Feet cells whose body centre is within this many tiles of
        /// the target are goals (distance 0).</param>
        /// <param name="maxAirGapTiles">Sideways moves through air are allowed only within this many tiles (in the
        /// same row) of a cell the player could jump from (height above ground at most maxJumpTiles). Without the
        /// limit the model let the player walk across the open sky, so far targets looked reachable that are not.
        /// int.MaxValue keeps the old behaviour.</param>
        /// <param name="infoAt">Optional cell info flags of world tile (x, y) (Info* constants); null = none.</param>
        /// <param name="maxSafeFallTiles">Longest allowed hard fall; int.MaxValue = no limit.</param>
        /// <param name="consistentBody">Vertical steps need a common clear side column (see the class notes).</param>
        public static PathDistanceField Build(Func<int, int, PathTile> tileAt, float targetTileX, float targetTileY,
            int radius, int maxJumpTiles, float goalRadiusTiles, int maxAirGapTiles = int.MaxValue,
            Func<int, int, ushort> infoAt = null, int maxSafeFallTiles = int.MaxValue, bool consistentBody = false)
        {
            int size = radius * 2 + 1;
            int originX = (int)Math.Floor(targetTileX) - radius;
            int originY = (int)Math.Floor(targetTileY) - radius;
            int n = size * size;

            var tiles = new PathTile[n];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tiles[y * size + x] = tileAt(originX + x, originY + y);

            var info = new ushort[n];
            if (infoAt != null)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        info[y * size + x] = infoAt(originX + x, originY + y);

            PathTile T(int x, int y) => (x < 0 || y < 0 || x >= size || y >= size) ? PathTile.Solid : tiles[y * size + x];
            ushort I(int x, int y) => (x < 0 || y < 0 || x >= size || y >= size) ? (ushort)0 : info[y * size + x];
            bool Clear(int x, int y) => T(x, y) != PathTile.Solid && T(x, y - 1) != PathTile.Solid && T(x, y - 2) != PathTile.Solid;
            bool Full(int x, int y) => T(x, y) == PathTile.Solid && (I(x, y) & InfoPartial) == 0;
            bool ClearOfFull(int x, int y) => !Full(x, y) && !Full(x, y - 1) && !Full(x, y - 2);

            // Per feet cell: traversal cost and whether the body may be there at all.
            var cost = new byte[n];
            var blocked = new bool[n];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int c = 1;
                    bool block = false;
                    if (infoAt != null)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            ushort b = I(x, y - j);
                            if ((b & InfoWeb) != 0) c = Math.Max(c, CostWeb);
                            if ((b & InfoHoney) != 0) c = Math.Max(c, CostHoney);
                            if ((b & InfoWater) != 0) c = Math.Max(c, CostWater);
                            if ((b & InfoLethal) != 0) block = true;
                        }
                        if ((I(x, y + 1) & InfoSticky) != 0) c = Math.Max(c, CostSticky);  // run speed x 0.25 (Player.sticky)
                        // Touching: the body box (3 columns, as it covers 2 of them at any offset) plus the floor row.
                        for (int i = -1; i <= 1 && !block; i++)
                            for (int j = -2; j <= 1 && !block; j++)
                                if ((I(x + i, y + j) & InfoHurts) != 0) block = true;
                        for (int i = -2; i <= 2 && !block; i++)
                            for (int j = -4; j <= 2 && !block; j++)
                                if ((I(x + i, y + j) & InfoTrap) != 0) block = true;
                    }
                    cost[y * size + x] = (byte)c;
                    blocked[y * size + x] = block;
                }
            }

            // Unsafe falls: distance down to where a fall ends (a supporting tile below, or liquid in the cell).
            if (maxSafeFallTiles != int.MaxValue)
            {
                for (int x = 0; x < size; x++)
                {
                    int h = int.MaxValue;
                    bool soft = false;
                    for (int y = size - 1; y >= 0; y--)
                    {
                        PathTile below = T(x, y + 1);
                        if ((I(x, y) & (InfoWater | InfoHoney)) != 0) { h = 0; soft = true; }
                        else if (y + 1 < size && (below == PathTile.Solid || below == PathTile.Platform)) { h = 0; soft = (I(x, y + 1) & InfoSoftLanding) != 0; }
                        else if (h != int.MaxValue) h++;
                        if (h != int.MaxValue && h > maxSafeFallTiles && !soft) blocked[y * size + x] = true;
                    }
                }
            }

            bool Passable(int x, int y) => Clear(x, y) && (Clear(x - 1, y) || Clear(x + 1, y)) && !blocked[y * size + x];

            // Height of each cell above the first supporting tile below it (same column), and the jump height from
            // that ground: lower when the player takes off in honey.
            var heightAboveGround = new int[n];
            var jumpTiles = new int[n];
            for (int x = 0; x < size; x++)
            {
                int h = int.MaxValue, jump = maxJumpTiles;
                for (int y = size - 1; y >= 0; y--)
                {
                    PathTile below = T(x, y + 1);
                    if (y + 1 < size && (below == PathTile.Solid || below == PathTile.Platform))
                    {
                        h = 0;
                        jump = maxJumpTiles;
                        if ((I(x, y) & InfoHoney) != 0)
                        {
                            int depth = 0;
                            while (depth < 4 && (I(x, y - depth) & InfoHoney) != 0) depth++;
                            jump = HoneyJumpTiles(maxJumpTiles, depth);
                        }
                        if ((I(x, y + 1) & InfoSticky) != 0) jump = Math.Min(jump, StickyJumpTiles(maxJumpTiles));
                    }
                    else if (h != int.MaxValue) h++;
                    heightAboveGround[y * size + x] = h;
                    jumpTiles[y * size + x] = jump;
                }
            }
            // Jumpable: within the dry jump height of the ground below, and within the take-off's jump height in this
            // column or a non-solid neighbour (the body spans 2 columns: it can leave shallow honey beside a deep part).
            // Away from honey jumpTiles == maxJumpTiles, so this is the plain height rule.
            bool Within(int x, int y) => x >= 0 && x < size && heightAboveGround[y * size + x] <= jumpTiles[y * size + x];
            bool Jumpable(int i)
            {
                if (heightAboveGround[i] > maxJumpTiles) return false;
                int x = i % size, y = i / size;
                return Within(x, y) || T(x - 1, y) != PathTile.Solid && Within(x - 1, y) || T(x + 1, y) != PathTile.Solid && Within(x + 1, y);
            }

            // Per cell: horizontal distance (same row) to the nearest passable cell within jump height of the ground.
            var nearJumpable = new int[n];
            Array.Fill(nearJumpable, int.MaxValue);
            if (maxAirGapTiles != int.MaxValue)
            {
                for (int y = 0; y < size; y++)
                {
                    int last = int.MinValue / 2;
                    for (int x = 0; x < size; x++)
                    {
                        int i = y * size + x;
                        if (Passable(x, y) && Jumpable(i)) last = x;
                        nearJumpable[i] = x - last;
                    }
                    last = int.MaxValue / 2;
                    for (int x = size - 1; x >= 0; x--)
                    {
                        int i = y * size + x;
                        if (Passable(x, y) && Jumpable(i)) last = x;
                        nearJumpable[i] = Math.Min(nearJumpable[i], last - x);
                    }
                }
            }

            var dist = new int[n];
            var next = new byte[n];
            Array.Fill(dist, UNREACHABLE);
            // Dial's algorithm: costs are small integers, so a ring of MaxCost + 1 buckets is a priority queue.
            var buckets = new Queue<int>[MaxCost + 1];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new Queue<int>();
            int pending = 0;

            // Seeds: passable feet cells whose body centre (about 1.3 tiles above the feet) is near the target.
            float goalSq = goalRadiusTiles * goalRadiusTiles;
            int r = (int)Math.Ceiling(goalRadiusTiles) + 2;
            for (int y = radius - r; y <= radius + r; y++)
            {
                for (int x = radius - r; x <= radius + r; x++)
                {
                    if (x < 0 || y < 0 || x >= size || y >= size || !Passable(x, y)) continue;
                    float cx = originX + x + 0.5f - targetTileX;
                    float cy = originY + y + 1f - 1.3f - targetTileY;
                    if (cx * cx + cy * cy > goalSq) continue;
                    dist[y * size + x] = 0;
                    buckets[0].Enqueue(y * size + x);
                    pending++;
                }
            }

            // Reverse search: pop d in order of distance, relax every p with a forward edge p -> d
            // (dist[p] = dist[d] + the cost of traversing p).
            for (int current = 0; pending > 0; current++)
            {
                Queue<int> bucket = buckets[current % buckets.Length];
                while (bucket.Count > 0)
                {
                    int idx = bucket.Dequeue();
                    pending--;
                    if (dist[idx] != current) continue;  // stale entry: a cheaper one was processed already
                    int dx = idx % size, dy = idx / size;

                    // p left/right of d: horizontal edge p -> d, through air only near jumpable ground.
                    if (maxAirGapTiles == int.MaxValue || nearJumpable[idx] <= maxAirGapTiles)
                    {
                        TryRelax(dx - 1, dy);
                        TryRelax(dx + 1, dy);
                    }
                    // p above d: falling into d (through a platform too, by holding down).
                    TryRelax(dx, dy - 1);
                    // p below d: jumping up into d, only within jump height of the ground.
                    if (Jumpable(idx)) TryRelax(dx, dy + 1);

                    void TryRelax(int px, int py)
                    {
                        if (px < 0 || py < 0 || px >= size || py >= size) return;
                        int p = py * size + px;
                        if (!Passable(px, py)) return;
                        if (consistentBody && px == dx && py < dy) {  // p above d: falling from p into d
                            int top = Math.Min(py, dy), bottom = Math.Max(py, dy);
                            if (!(ClearOfFull(px - 1, top) && ClearOfFull(px - 1, bottom)) && !(ClearOfFull(px + 1, top) && ClearOfFull(px + 1, bottom))) return;
                        }
                        int nd = current + cost[p];
                        if (dist[p] != UNREACHABLE && dist[p] <= nd) return;
                        dist[p] = nd;
                        next[p] = (byte)(dx > px ? 1 : dx < px ? 2 : dy > py ? 3 : 4);
                        buckets[nd % buckets.Length].Enqueue(p);
                        pending++;
                    }
                }
            }

            return new PathDistanceField(originX, originY, size, dist, next);
        }
    }
}
