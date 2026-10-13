using Microsoft.Xna.Framework;
using Terraria;
using System;

namespace FirstMod_Sp.Common.Systems
{
    public static class GridSensors
    {
        public const int RANGE = 20;
        public const int GRID_SIZE = RANGE * 2; // 40

        public const float AIR = 0f;
        public const float PLATFORM = 0.5f;
        public const float SOLID = 1f;

        // CACHED ARRAY: Prevents allocating 1600 floats every single tick.
        // The returned array is shared and overwritten by the next call: callers must copy it
        // out (RLNetworkSystem does so immediately with Buffer.BlockCopy) before calling again.
        private static float[] _cachedGrid = new float[GRID_SIZE * GRID_SIZE];

        public static float[] GetObservationGrid(Player player)
        {
            float[] grid = _cachedGrid;
            int idx = 0;

            int playerTileX = (int)(player.Center.X / 16f);
            int playerTileY = (int)(player.Center.Y / 16f);

            for (int y = playerTileY - RANGE; y < playerTileY + RANGE; y++)
            {
                for (int x = playerTileX - RANGE; x < playerTileX + RANGE; x++)
                {
                    // Check bounds to prevent crashes at map edges
                    if (!WorldGen.InWorld(x, y)) {
                        grid[idx++] = SOLID; // Out of bounds is solid
                        continue;
                    }

                    // OPTIMIZATION: Use direct array access instead of GetTileSafely
                    // This avoids internal dictionary lookups/safety checks since we already checked InWorld above.
                    grid[idx++] = ClassifyTile(Main.tile[x, y]);
                }
            }
            return grid;
        }

        public static float ClassifyTile(Tile tile)
        {
            if (!tile.HasUnactuatedTile) return AIR; // Air or actuated

            // Note: Check dangerous tiles here if you want (Spikes = 1.0)

            if (Main.tileSolidTop[tile.TileType]) return PLATFORM; // tileSolid is also true for platforms, so check first
            if (Main.tileSolid[tile.TileType]) return SOLID;

            return AIR; // Furniture, grass, torches...
        }

        /// <summary>Same classification as the vision grid, for the path distance field.</summary>
        public static PathTile ClassifyPathTile(int x, int y)
        {
            if (!WorldGen.InWorld(x, y)) return PathTile.Solid;
            float c = ClassifyTile(Main.tile[x, y]);
            if (c == SOLID) return PathTile.Solid;
            if (c == PLATFORM) return PathTile.Platform;
            return PathTile.Air;
        }
    }
}
