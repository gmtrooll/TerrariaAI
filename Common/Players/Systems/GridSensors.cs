using Microsoft.Xna.Framework;
using Terraria;

namespace FirstMod_Sp.Common.Systems 
{
    public static class GridSensors
    {
        // 20 Radius = 40x40 Grid
        public const int RANGE = 20; 
        public const int GRID_SIZE = RANGE * 2; // 40

        // Returns a flattened array (Size 1600)
        // We use float[] because the CNN expects floats (0.0 to 1.0)
        public static float[] GetObservationGrid(Player player)
        {
            float[] grid = new float[GRID_SIZE * GRID_SIZE];
            int idx = 0;

            int playerTileX = (int)(player.Center.X / 16f);
            int playerTileY = (int)(player.Center.Y / 16f);

            // Scan Top-Left to Bottom-Right
            for (int y = playerTileY - RANGE; y < playerTileY + RANGE; y++)
            {
                for (int x = playerTileX - RANGE; x < playerTileX + RANGE; x++)
                {
                    Tile tile = Framing.GetTileSafely(x, y);
                    grid[idx++] = ClassifyTile(tile);
                }
            }
            return grid;
        }

        private static float ClassifyTile(Tile tile)
        {
            if (!tile.HasUnactuatedTile) return 0f; // Air
            if (Main.tileSolidTop[tile.TileType]) return 0.5f; // Platform
            if (Main.tileSolid[tile.TileType]) return 1f; // Solid
            return 0f;
        }
    }
}