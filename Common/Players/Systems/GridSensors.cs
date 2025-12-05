using Microsoft.Xna.Framework;
using Terraria;
using System;

namespace FirstMod_Sp.Common.Systems 
{
    public static class GridSensors
    {
        public const int RANGE = 20; 
        public const int GRID_SIZE = RANGE * 2; // 40
        
        // CACHED ARRAY: Prevents allocating 1600 floats every single tick
        private static float[] _cachedGrid = new float[GRID_SIZE * GRID_SIZE];

        public static float[] GetObservationGrid(Player player)
        {
            // Reset the array cleanly
            Array.Clear(_cachedGrid, 0, _cachedGrid.Length);
            
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
                        grid[idx++] = 1f; // Out of bounds is solid
                        continue;
                    }

                    // OPTIMIZATION: Use direct array access instead of GetTileSafely
                    // This avoids internal dictionary lookups/safety checks since we already checked InWorld above.
                    Tile tile = Main.tile[x, y];
                    grid[idx++] = ClassifyTile(tile);
                }
            }
            return grid;
        }

        private static float ClassifyTile(Tile tile)
        {
            if (tile == null) return 0f;
            if (!tile.HasUnactuatedTile) return 0f; // Air
            
            // Note: Check dangerous tiles here if you want (Spikes = 1.0)
            
            if (Main.tileSolidTop[tile.TileType]) return 0.5f; // Platform
            if (Main.tileSolid[tile.TileType]) return 1f; // Solid
            
            return 0f;
        }
    }
}