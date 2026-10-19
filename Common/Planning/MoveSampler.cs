using System;
using System.Collections.Generic;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Scenarios;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Planning
{
    /// <summary>
    /// Physics move sampling for the physics planner (docs/Physics Planner Design.md). A dedicated bot is placed on
    /// a standing spot, given an input program (walk, or jump after a run-up, holding jump for a while, with or
    /// without the double jump) and stepped with the game's own Player.Update until it stands again. The landing
    /// spot, the time and whether it lost life become one edge of the movement graph. Nothing about the physics is
    /// re-implemented: the bot moves exactly as training bots do.
    ///
    /// The world is restored after every program (a simulated jump may break a web, open a door or step on a
    /// pressure plate), and stray items and projectiles are removed. Run this on its own server, not during training.
    ///
    /// Bridge commands:
    ///   sample_moves {x, y, w, h, loadout (0 none, 1 Tsunami), runups?, holds?} -> edges for every standing spot
    ///     in the rectangle as edges_b64: base64 int32 rows [from_x, from_y, to_x, to_y, ticks, hurt, program],
    ///     to = -1 when it never landed.
    ///   sim_move {x, y, dir, runup, hold, double, loadout, trace?} -> one program's result (and tick-by-tick
    ///     positions with trace), for validation against real bots.
    /// </summary>
    public class MoveSampler : ModSystem
    {
        public const int MaxTicks = 240;
        private static int simSlot = -1;

        // Tile snapshot for restoring the world after each program.
        private static TileTypeData[] types;
        private static WallTypeData[] walls;
        private static LiquidData[] liquids;
        private static TileWallWireStateData[] states;
        private static TileWallBrightnessInvisibilityData[] brightness;

        public struct Program
        {
            public int Dir;      // -1 left, +1 right
            public int RunUp;    // tiles to run before jumping; -1 = walk one tile, no jump
            public int Hold;     // ticks to hold the first jump
            public int Double;   // extra jump: 0 none, 1 early (right after the first jump ends), 2 at the apex
            public bool Drop;    // hold down (drop through a platform)
            public int Id;
        }

        public struct Result
        {
            public int ToX, ToY, Ticks;
            public bool Hurt, Landed;
            public float ApexTiles;  // highest point above the start, in tiles (feet)
            public List<float[]> Trace;
        }

        public override void Load() {
            if (!AutomationArgs.Enabled) return;
            BridgeCommands.Register("sample_moves", SampleMoves);
            BridgeCommands.Register("sim_move", SimMove);
        }

        public override void OnWorldUnload() {
            types = null;
            simSlot = -1;
        }

        public static List<Program> Programs(bool hasDouble, int[] runups, int[] holds) {
            var list = new List<Program>();
            int id = 0;
            foreach (int dir in new[] { -1, 1 }) {
                list.Add(new Program { Dir = dir, RunUp = -1, Id = id++ });  // walk one tile (or off a ledge)
                foreach (int runup in runups) {
                    foreach (int hold in holds) {
                        list.Add(new Program { Dir = dir, RunUp = runup, Hold = hold, Id = id++ });
                        // Extra jump at the apex, after a full first jump: pressing it earlier is always lower
                        // and shorter (measured: apex 11.6 vs 12.8 tiles), and a short first hold adds nothing.
                        if (hasDouble && hold == holds[holds.Length - 1])
                            list.Add(new Program { Dir = dir, RunUp = runup, Hold = hold, Double = 2, Id = id++ });
                    }
                }
            }
            list.Add(new Program { Dir = 0, RunUp = -1, Drop = true, Id = id++ });  // drop through a platform
            return list;
        }

        private static Player SimBot(int loadout) {
            if (simSlot < 0 || !Main.player[simSlot].active || !BotManagerSystem.IsBot[simSlot]) {
                simSlot = BotManagerSystem.SpawnBot(new Vector2(Main.spawnTileX * 16f, Main.spawnTileY * 16f - 48f));
                if (simSlot < 0) throw new InvalidOperationException("no free player slot for the simulation bot");
            }
            Player p = Main.player[simSlot];
            int accessory = loadout == 1 ? ItemID.TsunamiInABottle : ItemID.None;
            if (p.armor[3].type != accessory) p.armor[3].SetDefaults(accessory);
            return p;
        }

        private static void TakeSnapshot() {
            if (types != null) return;
            types = (TileTypeData[])Main.tile.GetData<TileTypeData>().Clone();
            walls = (WallTypeData[])Main.tile.GetData<WallTypeData>().Clone();
            liquids = (LiquidData[])Main.tile.GetData<LiquidData>().Clone();
            states = (TileWallWireStateData[])Main.tile.GetData<TileWallWireStateData>().Clone();
            brightness = (TileWallBrightnessInvisibilityData[])Main.tile.GetData<TileWallBrightnessInvisibilityData>().Clone();
        }

        /// <summary>Put back every tile in the box that differs from the snapshot (cheap: the box is small).</summary>
        private static void RestoreBox(int x0, int y0, int x1, int y1) {
            var curTypes = Main.tile.GetData<TileTypeData>();
            var curWalls = Main.tile.GetData<WallTypeData>();
            var curLiquids = Main.tile.GetData<LiquidData>();
            var curStates = Main.tile.GetData<TileWallWireStateData>();
            var curBrightness = Main.tile.GetData<TileWallBrightnessInvisibilityData>();
            int height = Main.tile.Height;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(Main.maxTilesX - 1, x1); y1 = Math.Min(Main.maxTilesY - 1, y1);
            for (int x = x0; x <= x1; x++) {
                for (int y = y0; y <= y1; y++) {
                    int i = y + x * height;
                    curTypes[i] = types[i];
                    curWalls[i] = walls[i];
                    curLiquids[i] = liquids[i];
                    curStates[i] = states[i];
                    curBrightness[i] = brightness[i];
                }
            }
            foreach (Item item in Main.item) item.active = false;
            foreach (Projectile proj in Main.projectile) proj.active = false;
        }

        /// <summary>Run one program from the standing spot (feet cell x, y). The world is restored afterwards.</summary>
        public static Result Run(Player p, int x, int y, Program prog, bool trace = false) {
            TakeSnapshot();
            var data = BotManagerSystem.BotStates.TryGetValue(p.whoAmI, out BotData d) ? d : (BotManagerSystem.BotStates[p.whoAmI] = new BotData());
            // Place: feet on the floor of cell (x, y), standing still, full life, jumps refreshed by a settle tick.
            p.position = new Vector2(x * 16 + 8 - p.width / 2f, (y + 1) * 16 - p.height);
            p.velocity = Vector2.Zero;
            p.fallStart = p.fallStart2 = (int)(p.position.Y / 16f);
            p.statLife = p.statLifeMax2;
            p.immuneTime = 0;
            p.jump = 0;
            p.dead = false;
            var result = new Result { ToX = -1, ToY = -1, Trace = trace ? new List<float[]>() : null };
            float startFeet = p.position.Y + p.height;
            int minX = x, maxX = x, minY = y, maxY = y;
            int life = p.statLifeMax2;
            bool airborne = false, jumped = false, released = false, doubled = false;
            int jumpTick = -1;
            for (int t = -2; t < MaxTicks; t++) {
                data.Reset();
                if (t >= 0) {
                    float moved = (p.Center.X - (x * 16 + 8)) * Math.Sign(prog.Dir == 0 ? 1 : prog.Dir);
                    if (prog.Dir < 0) data.ControlLeft = true;
                    if (prog.Dir > 0) data.ControlRight = true;
                    if (prog.Drop) data.ControlDown = true;
                    if (prog.RunUp >= 0 && !jumped && moved >= prog.RunUp * 16f - 0.01f && !airborne) {
                        jumped = true;
                        jumpTick = t;
                    }
                    if (jumped && t - jumpTick < prog.Hold) data.ControlJump = true;
                    else if (jumped && prog.Double > 0 && !doubled) {
                        // Release for at least one tick, then press again: at once (early) or once falling (apex).
                        if (!released) released = true;
                        else if (prog.Double == 1 || p.velocity.Y >= 0f) {
                            doubled = true;
                            jumpTick = t;
                            prog.Hold = 30;
                        }
                    }
                    if (doubled && t - jumpTick < 30) data.ControlJump = true;  // held long enough for any bottle's duration
                }
                int real = Main.myPlayer;
                Main.myPlayer = p.whoAmI;
                try {
                    p.Update(p.whoAmI);
                }
                finally {
                    Main.myPlayer = real;
                }
                if (p.dead || p.statLife < life) result.Hurt = true;
                if (p.dead) break;
                int cx = (int)(p.Center.X / 16f), cy = (int)((p.Bottom.Y - 1f) / 16f);
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                result.ApexTiles = Math.Max(result.ApexTiles, (startFeet - (p.position.Y + p.height)) / 16f);
                result.Trace?.Add(new[] { t, p.position.X / 16f, (p.position.Y + p.height) / 16f, p.velocity.X, p.velocity.Y });
                if (t < 0) continue;
                bool grounded = p.velocity.Y == 0f;
                if (!grounded) airborne = true;
                // Done: standing again after being in the air, or a walk that moved one tile on the ground.
                bool walkDone = prog.RunUp < 0 && !prog.Drop && grounded && Math.Abs(cx - x) >= 1;
                if ((airborne && grounded) || walkDone || (prog.Drop && airborne && grounded)) {
                    result.Landed = true;
                    result.ToX = cx;
                    result.ToY = cy;
                    result.Ticks = t + 1;
                    break;
                }
                if (prog.Drop && t > 10 && !airborne) break;  // nothing to drop through
            }
            data.Reset();
            p.velocity = Vector2.Zero;
            RestoreBox(minX - 14, minY - 14, maxX + 14, maxY + 14);
            return result;
        }

        private static int Int(BridgeRequest r, string key, int fallback) => r.Args[key] != null ? r.Args.Value<int>(key) : fallback;

        private static void SimMove(BridgeRequest r) {
            Player p = SimBot(Int(r, "loadout", 1));
            var prog = new Program { Dir = Int(r, "dir", 1), RunUp = Int(r, "runup", 0), Hold = Int(r, "hold", 15), Double = Int(r, "double", 0) };
            var res = Run(p, Int(r, "x", 0), Int(r, "y", 0), prog, r.Args.Value<bool?>("trace") ?? false);
            r.Reply(new { landed = res.Landed, to = new[] { res.ToX, res.ToY }, ticks = res.Ticks, hurt = res.Hurt, apex_tiles = Math.Round(res.ApexTiles, 3), trace = res.Trace });
        }

        private static void SampleMoves(BridgeRequest r) {
            int x0 = Int(r, "x", 0), y0 = Int(r, "y", 0), w = Int(r, "w", 32), h = Int(r, "h", 32), loadout = Int(r, "loadout", 1);
            int[] runups = r.Args["runups"] is JArray ru ? ru.ToObject<int[]>() : new[] { 0, 2, 4 };
            int[] holds = r.Args["holds"] is JArray ho ? ho.ToObject<int[]>() : new[] { 4, 15 };
            Player p = SimBot(loadout);
            var programs = Programs(loadout == 1, runups, holds);
            var edges = new List<int[]>();
            int nodes = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int x = x0; x < x0 + w; x++) {
                for (int y = y0; y < y0 + h; y++) {
                    if (!ScenarioGenerator.Standable(x, y)) continue;
                    nodes++;
                    foreach (Program prog in programs) {
                        if (prog.Drop && GridSensors.ClassifyPathTile(x, y + 1) != PathTile.Platform) continue;
                        Result res = Run(p, x, y, prog);
                        if (res.Landed && res.ToX == x && res.ToY == y) continue;  // went nowhere
                        edges.Add(new[] { x, y, res.ToX, res.ToY, res.Ticks, res.Hurt ? 1 : 0, prog.Id });
                    }
                }
            }
            p.position = new Vector2(Main.spawnTileX * 16f, Main.spawnTileY * 16f - 48f);
            // Edges as one base64 block of int32 rows: the bridge's JSON output truncates lists over 2,000 items.
            var raw = new int[edges.Count * 7];
            for (int i = 0; i < edges.Count; i++) Array.Copy(edges[i], 0, raw, i * 7, 7);
            var bytes = new byte[raw.Length * 4];
            Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
            r.Reply(new { nodes, programs = programs.Count, edge_count = edges.Count, edges_b64 = Convert.ToBase64String(bytes), ms = watch.ElapsedMilliseconds });
        }
    }
}
