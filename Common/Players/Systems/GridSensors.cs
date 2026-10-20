using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using System;
using System.Collections.Generic;

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
            Tile tile = Main.tile[x, y];
            // Closed doors open by themselves when the (local) player walks into them (DoorOpeningHelper); our bots
            // are updated as the local player. The locked Lihzahrd temple door does not open (WorldGen.cs:34235).
            if (tile.HasUnactuatedTile && (tile.TileType == TileID.TallGateClosed
                || tile.TileType == TileID.ClosedDoor && !(tile.TileFrameY >= 594 && tile.TileFrameY <= 646 && tile.TileFrameX < 54)))
                return PathTile.Air;
            float c = ClassifyTile(tile);
            if (c == SOLID) return PathTile.Solid;
            if (c == PLATFORM) return PathTile.Platform;
            return PathTile.Air;
        }

        // Pressure plates set traps off; trap blocks fire darts/spears/flames; boulders and explosives are traps too.
        private static readonly HashSet<int> TrapTiles = new HashSet<int> {
            TileID.PressurePlates, TileID.Traps, TileID.Boulder, TileID.Explosives, TileID.LandMine, TileID.GeyserTrap,
            TileID.RollingCactus, TileID.BouncyBoulder, TileID.LifeCrystalBoulder,
        };

        /// <summary>Planner cell info for world tile (x, y): PathDistanceField.Info* flags (docs/Movement Hazards.md).</summary>
        public static ushort PathInfo(int x, int y)
        {
            if (!WorldGen.InWorld(x, y)) return 0;
            Tile tile = Main.tile[x, y];
            ushort f = 0;
            if (tile.LiquidAmount > 0)
            {
                int type = tile.LiquidType;
                if (type == LiquidID.Lava || type == LiquidID.Shimmer) f |= PathDistanceField.InfoLethal;
                else if (tile.LiquidAmount > 32) f |= type == LiquidID.Honey ? PathDistanceField.InfoHoney : PathDistanceField.InfoWater;
            }
            if (tile.HasUnactuatedTile)
            {
                ushort type = tile.TileType;
                if (type == TileID.Cobweb) f |= PathDistanceField.InfoWeb;
                if (type == TileID.HoneyBlock) f |= PathDistanceField.InfoSticky;
                // Cactus only hurts in the Don't Starve seed (Collision.cs:3717).
                if (TileID.Sets.TouchDamageImmediate[type] > 0 && type != TileID.Cactus || TileID.Sets.TouchDamageHot[type]) f |= PathDistanceField.InfoHurts;
                if (TrapTiles.Contains(type)) f |= PathDistanceField.InfoTrap;
                if (TileID.Sets.NegatesFallDamage[type]) f |= PathDistanceField.InfoSoftLanding;
                if (tile.IsHalfBlock || tile.Slope != SlopeType.Solid) f |= PathDistanceField.InfoPartial;
            }
            return f;
        }
    }
}
