using System;
using System.Collections.Generic;

namespace FirstMod_Sp.Common.Systems
{
    public enum PathTile : byte { Air = 0, Platform = 1, Solid = 2 }

    /// <summary>
    /// World-anchored "how many tiles of walking/jumping to the target" field, built once per
    /// target by a reverse BFS and then looked up per bot per decision tick.
    ///
    /// Movement model (a cell is the tile the player's feet occupy):
    ///  - A cell is passable if it and the two cells above it are not Solid, and the same holds for the
    ///    column to its left or to its right: the player is 42 x 20 px (Player.width/height), so its
    ///    body always covers 3 rows and 2 columns. One-tile-wide shafts and notches are not passable.
    ///  - Left/right: always allowed into a passable cell (walking or air control).
    ///  - Down: allowed into any passable cell, also through platforms (holding down drops through them).
    ///  - Up: allowed into a passable cell only if it is at most maxJumpTiles above the first
    ///    supporting tile (Solid or Platform) below it in the same column. This stops the BFS from
    ///    finding paths straight up into the sky. Platforms can be jumped through from below.
    /// Horizontal movement in the air is not limited, so the model over-estimates reachability
    /// across gaps rather than declaring reachable targets unreachable.
    ///
    /// This file has no Terraria dependency so the algorithm can be compiled and tested alone.
    /// </summary>
    public sealed class PathDistanceField
    {
        public const int UNREACHABLE = -1;

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
        public static PathDistanceField Build(Func<int, int, PathTile> tileAt, float targetTileX, float targetTileY,
            int radius, int maxJumpTiles, float goalRadiusTiles, int maxAirGapTiles = int.MaxValue)
        {
            int size = radius * 2 + 1;
            int originX = (int)Math.Floor(targetTileX) - radius;
            int originY = (int)Math.Floor(targetTileY) - radius;
            int n = size * size;

            var tiles = new PathTile[n];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tiles[y * size + x] = tileAt(originX + x, originY + y);

            PathTile T(int x, int y) => (x < 0 || y < 0 || x >= size || y >= size) ? PathTile.Solid : tiles[y * size + x];
            bool Clear(int x, int y) => T(x, y) != PathTile.Solid && T(x, y - 1) != PathTile.Solid && T(x, y - 2) != PathTile.Solid;
            bool Passable(int x, int y) => Clear(x, y) && (Clear(x - 1, y) || Clear(x + 1, y));

            // Height of each cell above the first supporting tile below it (same column).
            var heightAboveGround = new int[n];
            for (int x = 0; x < size; x++)
            {
                int h = int.MaxValue;
                for (int y = size - 1; y >= 0; y--)
                {
                    PathTile below = T(x, y + 1);
                    if (y + 1 < size && (below == PathTile.Solid || below == PathTile.Platform)) h = 0;
                    else if (h != int.MaxValue) h++;
                    heightAboveGround[y * size + x] = h;
                }
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
                        if (Passable(x, y) && heightAboveGround[i] <= maxJumpTiles) last = x;
                        nearJumpable[i] = x - last;
                    }
                    last = int.MaxValue / 2;
                    for (int x = size - 1; x >= 0; x--)
                    {
                        int i = y * size + x;
                        if (Passable(x, y) && heightAboveGround[i] <= maxJumpTiles) last = x;
                        nearJumpable[i] = Math.Min(nearJumpable[i], last - x);
                    }
                }
            }

            var dist = new int[n];
            var next = new byte[n];
            Array.Fill(dist, UNREACHABLE);
            var queue = new Queue<int>();

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
                    queue.Enqueue(y * size + x);
                }
            }

            // Reverse BFS: pop d, relax every p with a forward edge p -> d.
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int dx = idx % size, dy = idx / size;
                int nextDist = dist[idx] + 1;

                // p left/right of d: horizontal edge p -> d, through air only near jumpable ground.
                if (maxAirGapTiles == int.MaxValue || nearJumpable[idx] <= maxAirGapTiles)
                {
                    TryRelax(dx - 1, dy);
                    TryRelax(dx + 1, dy);
                }
                // p above d: falling into d (through a platform too, by holding down).
                TryRelax(dx, dy - 1);
                // p below d: jumping up into d, only within jump height of the ground.
                if (heightAboveGround[idx] <= maxJumpTiles) TryRelax(dx, dy + 1);

                void TryRelax(int px, int py)
                {
                    if (px < 0 || py < 0 || px >= size || py >= size) return;
                    int p = py * size + px;
                    if (dist[p] != UNREACHABLE || !Passable(px, py)) return;
                    dist[p] = nextDist;
                    next[p] = (byte)(dx > px ? 1 : dx < px ? 2 : dy > py ? 3 : 4);
                    queue.Enqueue(p);
                }
            }

            return new PathDistanceField(originX, originY, size, dist, next);
        }
    }
}
