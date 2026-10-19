using System;
using System.Collections.Generic;
using FirstMod_Sp.Common.Automation;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Env
{
    /// <summary>
    /// Keeps training worlds unchanged (environment mode, -env PORT). Bots change the world as players do: cobwebs
    /// and thorns break, thin ice and cracked bricks collapse, doors stay open, pressure plates use up traps, sand
    /// falls (docs/Movement Hazards.md). Without this every run would train on a slowly different world, and the
    /// game would save it on exit.
    ///  - The world is never saved while the environment is active.
    ///  - The tile data is copied when the learner connects (Snapshot). Every RestoreEvery ticks, the chunks bots
    ///    have been near are compared with the copy, and changed tiles are put back once every bot that was near
    ///    the chunk has finished the episode it was in (in the real game a broken web stays broken, so it must not
    ///    come back while the bot that broke it is still on its way), and no bot is within KeepClearTiles.
    ///  - Tiles drop no items, and hostile projectiles (trap darts, boulders) are cleared in the same pass.
    /// </summary>
    public class WorldIntegrity : ModSystem
    {
        public const int ChunkSize = 16, RestoreEvery = 60, KeepClearTiles = 4;

        public static bool Enabled => NavigateEnv.Port > 0;

        private static TileTypeData[] types;
        private static WallTypeData[] walls;
        private static LiquidData[] liquids;
        private static TileWallWireStateData[] states;
        private static TileWallBrightnessInvisibilityData[] brightness;
        // Dirty chunk -> (bot slot -> that bot's episode number when it was last near the chunk).
        private static readonly Dictionary<int, Dictionary<int, int>> dirtyChunks = new Dictionary<int, Dictionary<int, int>>();
        /// <summary>Episode number per player slot; NavigateEnv increments it when a bot starts an episode.</summary>
        public static readonly int[] Episode = new int[Main.maxPlayers + 1];
        private static int chunksX, chunksY, ticks;
        public static long Restored, Passes;

        public override void Load() {
            On_WorldFile_SaveWorld();
            if (AutomationArgs.Enabled) BridgeCommands.Register("world_integrity", r => r.Reply(new {
                enabled = Enabled, snapshot = types != null, dirty_chunks = dirtyChunks.Count, restored_tiles = Restored, passes = Passes,
            }));
        }

        private static void On_WorldFile_SaveWorld() {
            Terraria.IO.On_WorldFile.SaveWorld_bool_bool += (orig, useCloudSaving, resetTime) => {
                if (Enabled) return;  // never write a training world back to disk
                orig(useCloudSaving, resetTime);
            };
        }

        public override void OnWorldUnload() {
            types = null;
            walls = null;
            liquids = null;
            states = null;
            brightness = null;
            dirtyChunks.Clear();
        }

        /// <summary>Copy the whole tile data (called when the learner connects, before any bot moves).</summary>
        public static void Snapshot() {
            types = (TileTypeData[])Main.tile.GetData<TileTypeData>().Clone();
            walls = (WallTypeData[])Main.tile.GetData<WallTypeData>().Clone();
            liquids = (LiquidData[])Main.tile.GetData<LiquidData>().Clone();
            states = (TileWallWireStateData[])Main.tile.GetData<TileWallWireStateData>().Clone();
            brightness = (TileWallBrightnessInvisibilityData[])Main.tile.GetData<TileWallBrightnessInvisibilityData>().Clone();
            chunksX = (Main.maxTilesX + ChunkSize - 1) / ChunkSize;
            chunksY = (Main.maxTilesY + ChunkSize - 1) / ChunkSize;
            dirtyChunks.Clear();
            ticks = 0;
        }

        /// <summary>Mark the 3 x 3 chunks around a bot (a trap or a falling block can change tiles a little away).</summary>
        public static void MarkAround(Player p) {
            if (types == null) return;
            int cx = (int)(p.Center.X / 16f) / ChunkSize, cy = (int)(p.Center.Y / 16f) / ChunkSize;
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= chunksX || y >= chunksY) continue;
                    int chunk = y * chunksX + x;
                    if (!dirtyChunks.TryGetValue(chunk, out var visitors)) dirtyChunks[chunk] = visitors = new Dictionary<int, int>();
                    visitors[p.whoAmI] = Episode[p.whoAmI];
                }
            }
        }

        public override void PostUpdateEverything() {
            if (!Enabled || types == null || !NavigateEnv.Active || ++ticks < RestoreEvery) return;
            ticks = 0;
            Passes++;
            foreach (Item item in Main.item) item.active = false;
            foreach (Projectile proj in Main.projectile) {
                if (proj.active && proj.hostile) proj.active = false;
            }
            var botCenters = new List<Vector2>();
            foreach (int slot in Systems.BotManagerSystem.ActiveBots) botCenters.Add(Main.player[slot].Center / 16f);

            TileTypeData[] curTypes = Main.tile.GetData<TileTypeData>();
            WallTypeData[] curWalls = Main.tile.GetData<WallTypeData>();
            LiquidData[] curLiquids = Main.tile.GetData<LiquidData>();
            TileWallWireStateData[] curStates = Main.tile.GetData<TileWallWireStateData>();
            TileWallBrightnessInvisibilityData[] curBrightness = Main.tile.GetData<TileWallBrightnessInvisibilityData>();
            int height = Main.tile.Height, worldH = Main.maxTilesY;  // arrays are column-major over the allocated height
            var done = new List<int>();
            foreach (var (chunk, visitors) in dirtyChunks) {
                bool episodeRunning = false;
                foreach (var (slot, episode) in visitors) {
                    if (Episode[slot] == episode) {
                        episodeRunning = true;
                        break;
                    }
                }
                if (episodeRunning) continue;  // a bot that was here is still in the same episode: keep its changes
                int x0 = chunk % chunksX * ChunkSize, y0 = chunk / chunksX * ChunkSize;
                bool pending = false;
                for (int x = x0; x < Math.Min(x0 + ChunkSize, Main.maxTilesX); x++) {
                    for (int y = y0; y < Math.Min(y0 + ChunkSize, worldH); y++) {
                        int i = y + x * height;  // column-major, as Tilemap stores it
                        if (curTypes[i].Type == types[i].Type && curStates[i].NonFrameBits == states[i].NonFrameBits
                            && curStates[i].TileFrameX == states[i].TileFrameX && curStates[i].TileFrameY == states[i].TileFrameY
                            && curWalls[i].Type == walls[i].Type && curLiquids[i].Amount == liquids[i].Amount
                            && curLiquids[i].LiquidType == liquids[i].LiquidType) continue;
                        if (NearBot(botCenters, x, y)) {
                            pending = true;
                            continue;
                        }
                        curTypes[i] = types[i];
                        curWalls[i] = walls[i];
                        curLiquids[i] = liquids[i];
                        curStates[i] = states[i];
                        curBrightness[i] = brightness[i];
                        Restored++;
                    }
                }
                if (!pending) done.Add(chunk);
            }
            foreach (int chunk in done) dirtyChunks.Remove(chunk);
        }

        private static bool NearBot(List<Vector2> centers, int x, int y) {
            foreach (Vector2 c in centers) {
                if (Math.Abs(c.X - x) <= KeepClearTiles + 1 && Math.Abs(c.Y - y) <= KeepClearTiles + 2) return true;
            }
            return false;
        }
    }

    /// <summary>No item drops from broken tiles in environment mode (bots would pick up cobwebs and thorns).</summary>
    public class WorldIntegrityTile : GlobalTile
    {
        public override bool CanDrop(int i, int j, int type) => !WorldIntegrity.Enabled;
    }
}
