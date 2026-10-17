using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using ReLogic.Reflection;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Bridge commands. All run on the game thread. Positions in arguments and results are in tiles
    /// (world pixels / 16) unless a name says otherwise. A command targets one player with "bot" (index into the
    /// active bot list) or "player" (raw player slot); without either it targets the local player, or bot 0 on a
    /// server.
    /// </summary>
    public static class BridgeCommands
    {
        private static readonly Dictionary<string, Action<BridgeRequest>> Handlers = new Dictionary<string, Action<BridgeRequest>> {
            ["ping"] = r => r.Reply(new { pong = true, tick = BridgeSystem.Tick }),
            ["commands"] = r => r.Reply(Handlers.Keys.OrderBy(k => k).ToList()),
            ["status"] = r => r.Reply(Status()),
            ["errors"] = r => {
                r.Reply(new { count = BridgeSystem.ErrorCount, recent = BridgeSystem.RecentErrors.ToList() });
                if (r.Args.Value<bool?>("clear") == true) {
                    BridgeSystem.RecentErrors.Clear();
                    BridgeSystem.ErrorCount = 0;
                }
            },
            ["execute"] = ExecuteCode,
            ["pause"] = r => {
                BridgeSystem.Paused = true;
                r.Reply(new { paused = true, tick = BridgeSystem.Tick });
            },
            ["resume"] = r => {
                BridgeSystem.Paused = false;
                BridgeSystem.StepsLeft = 0;
                r.Reply(new { paused = false, tick = BridgeSystem.Tick });
            },
            ["speed"] = r => {
                BridgeSystem.Speed = Math.Clamp(Int(r, "ticks_per_frame", 1), 1, BridgeSystem.MaxTicksPerFrame);
                r.Reply(new { ticks_per_frame = BridgeSystem.Speed });
            },
            ["step"] = Step,
            ["wait_until"] = WaitUntil,
            ["spawn_bots"] = r => {
                Vector2? at = r.Args["x"] != null ? new Vector2(Float(r, "x", 0), Float(r, "y", 0)) : null;
                r.Reply(new { bots = SpawnBots(Int(r, "count", 1), at, Float(r, "spread", 2)) });
            },
            ["despawn_bots"] = r => {
                int count = BotManagerSystem.ActiveBots.Count;
                BotManagerSystem.RemoveAllBots();
                r.Reply(new { removed = count });
            },
            ["input"] = Input,
            ["state"] = r => r.Reply(r.Args.Value<bool?>("all") == true
                ? new { local = Main.dedServ ? null : PlayerInfo(Main.LocalPlayer), bots = BotManagerSystem.ActiveBots.Select(i => PlayerInfo(Main.player[i])).ToList() }
                : PlayerInfo(Target(r))),
            ["tiles"] = Tiles,
            ["entities"] = Entities,
            ["teleport"] = r => {
                Player player = Target(r);
                player.Center = new Vector2(Float(r, "x", 0) * 16f + 8f, Float(r, "y", 0) * 16f + 8f);
                player.velocity = Vector2.Zero;
                player.fallStart = player.fallStart2 = (int)(player.position.Y / 16f);
                r.Reply(PlayerInfo(player));
            },
            ["give"] = Give,
            ["spawn_npc"] = SpawnNpc,
            ["set_time"] = r => {
                if (r.Args["time"] != null) Main.time = r.Args.Value<double>("time");
                if (r.Args["day"] != null) Main.dayTime = r.Args.Value<bool>("day");
                r.Reply(new { time = Main.time, day = Main.dayTime });
            },
            ["screenshot"] = r => {
                if (Main.dedServ) {
                    r.Fail("a server draws nothing; use tiles or entities");
                    return;
                }
                ScreenCapture.Request(r);
            },
            ["quit"] = r => {
                BridgeSystem.RequestQuit(r.Args.Value<bool?>("save") ?? false);
                r.Reply(new { quitting = true });
            },
        };

        /// <summary>Add a command from another system (e.g. the benchmark).</summary>
        public static void Register(string name, Action<BridgeRequest> handler) {
            Handlers[name] = handler;
        }

        public static void Dispatch(BridgeRequest request) {
            if (!Handlers.TryGetValue(request.Cmd, out Action<BridgeRequest> handler)) {
                request.Fail($"unknown command '{request.Cmd}'; known: {string.Join(", ", Handlers.Keys.OrderBy(k => k))}");
                return;
            }
            try {
                handler(request);
            }
            catch (Exception e) {
                request.Fail(Describe(e));
            }
        }

        public static string Describe(Exception e) {
            while (e.InnerException != null) e = e.InnerException;
            return $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}";
        }

        // ---------- arguments ----------

        private static int Int(BridgeRequest r, string key, int fallback) => r.Args.Value<int?>(key) ?? fallback;

        private static float Float(BridgeRequest r, string key, float fallback) => r.Args.Value<float?>(key) ?? fallback;

        private static Player Target(BridgeRequest r) {
            if (r.Args["bot"] != null) {
                int n = Int(r, "bot", 0);
                if (n < 0 || n >= BotManagerSystem.ActiveBots.Count) throw new ArgumentException($"no bot {n}; there are {BotManagerSystem.ActiveBots.Count}");
                return Main.player[BotManagerSystem.ActiveBots[n]];
            }
            if (r.Args["player"] != null) return Main.player[Int(r, "player", 0)];
            if (!Main.dedServ) return Main.LocalPlayer;
            if (BotManagerSystem.ActiveBots.Count > 0) return Main.player[BotManagerSystem.ActiveBots[0]];
            throw new ArgumentException("no target: a server has no local player and there are no bots");
        }

        // ---------- commands ----------

        private static object Status() {
            return new {
                side = Main.dedServ ? "server" : "client",
                in_world = !Main.gameMenu,
                world = Main.gameMenu ? null : Main.worldName,
                world_tiles = new { w = Main.maxTilesX, h = Main.maxTilesY },
                spawn = new { x = Main.spawnTileX, y = Main.spawnTileY },
                tick = BridgeSystem.Tick,
                paused = BridgeSystem.Paused,
                ticks_per_frame = BridgeSystem.Speed,
                steps_left = BridgeSystem.StepsLeft,
                errors = BridgeSystem.ErrorCount,
                bots = BotManagerSystem.ActiveBots.Count,
                players = Enumerable.Range(0, Main.maxPlayers).Count(i => Main.player[i].active && !BotManagerSystem.IsBot[i]),
                time = Main.time,
                day = Main.dayTime,
                status_text = Main.statusText,
                fps = Main.frameRate,
                mod_version = ModContent.GetInstance<global::FirstMod_Sp.FirstMod_Sp>().Version.ToString(),
            };
        }

        private static void ExecuteCode(BridgeRequest r) {
            string code = r.Args.Value<string>("code");
            if (string.IsNullOrWhiteSpace(code)) {
                r.Fail("missing 'code'");
                return;
            }
            CSharpRunner.Compiled compiled = CSharpRunner.CompileBody(code, out string errors);
            if (compiled == null) {
                r.Fail("compile error:\n" + errors);
                return;
            }
            SnippetBase snippet = compiled.NewInstance();
            var watch = Stopwatch.StartNew();
            object result;
            try {
                result = snippet.Run();
            }
            catch (Exception e) {
                string output = snippet.Output.Count > 0 ? "\noutput before the error:\n" + string.Join("\n", snippet.Output) : "";
                r.Fail("runtime error: " + Describe(e) + output);
                return;
            }
            r.Reply(new {
                result = JsonOut.ToToken(result),
                output = snippet.Output,
                compile_ms = Math.Round(compiled.CompileMs, 1),
                run_ms = Math.Round(watch.Elapsed.TotalMilliseconds, 2),
            });
        }

        private static void Step(BridgeRequest r) {
            int ticks = Math.Max(1, Int(r, "ticks", 1));
            long target = BridgeSystem.Tick + ticks;
            if (BridgeSystem.Paused) BridgeSystem.StepsLeft += ticks;
            int timeoutMs = Int(r, "timeout_ms", 60000);
            BridgeSystem.AddWaiter(new BridgeSystem.Waiter {
                Request = r,
                Done = () => BridgeSystem.Tick >= target,
                Result = () => new { tick = BridgeSystem.Tick },
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs),
                OnTimeout = () => throw new TimeoutException($"{target - BridgeSystem.Tick} of {ticks} ticks still to run after {timeoutMs} ms (in a world? paused?)"),
            });
        }

        private static void WaitUntil(BridgeRequest r) {
            string condition = r.Args.Value<string>("condition");
            CSharpRunner.Compiled compiled = CSharpRunner.CompileCondition(condition ?? "", out string errors);
            if (compiled == null) {
                r.Fail("compile error:\n" + errors);
                return;
            }
            SnippetBase snippet = compiled.NewInstance();
            long start = BridgeSystem.Tick;
            int timeoutTicks = Int(r, "timeout_ticks", 3600);
            int timeoutMs = Int(r, "timeout_ms", 60000);
            BridgeSystem.AddWaiter(new BridgeSystem.Waiter {
                Request = r,
                Done = () => (bool)snippet.Run(),
                Result = () => new { met = true, tick = BridgeSystem.Tick, waited_ticks = BridgeSystem.Tick - start },
                OnTimeout = () => new { met = false, tick = BridgeSystem.Tick, waited_ticks = BridgeSystem.Tick - start },
                DeadlineTick = start + timeoutTicks,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs),
            });
        }

        /// <summary>Spawn bots spread out horizontally from a tile position (default: just above the world spawn).</summary>
        public static List<int> SpawnBots(int count, Vector2? atTiles, float spreadTiles) {
            Vector2 origin = atTiles ?? new Vector2(Main.spawnTileX, Main.spawnTileY - 3);
            var spawned = new List<int>();
            for (int i = 0; i < count; i++) {
                Vector2 tile = origin + new Vector2((i - (count - 1) / 2f) * spreadTiles, 0f);
                int slot = BotManagerSystem.SpawnBot(tile * 16f);
                if (slot < 0) break;
                spawned.Add(slot);
            }
            return spawned;
        }

        private static void Input(BridgeRequest r) {
            Player player = Target(r);
            if (r.Args.Value<bool?>("release") == true) {
                InputHolds.ByPlayer.Remove(player.whoAmI);
                r.Reply(new { player = player.whoAmI, released = true });
                return;
            }
            int ticks = Math.Max(1, Int(r, "ticks", 1));
            var hold = new InputHolds.Hold {
                Left = r.Args.Value<bool?>("left") ?? false,
                Right = r.Args.Value<bool?>("right") ?? false,
                Up = r.Args.Value<bool?>("up") ?? false,
                Down = r.Args.Value<bool?>("down") ?? false,
                Jump = r.Args.Value<bool?>("jump") ?? false,
                Hook = r.Args.Value<bool?>("hook") ?? false,
                Use = r.Args.Value<bool?>("use") ?? false,
                TicksLeft = ticks,
            };
            if (r.Args["aim"] is JObject aim) hold.Aim = new Vector2(aim.Value<float>("x") * 16f + 8f, aim.Value<float>("y") * 16f + 8f);
            InputHolds.ByPlayer[player.whoAmI] = hold;

            if (r.Args.Value<bool?>("wait") != true) {
                r.Reply(new { player = player.whoAmI, ticks });
                return;
            }
            long target = BridgeSystem.Tick + ticks;
            if (BridgeSystem.Paused) BridgeSystem.StepsLeft += ticks;
            BridgeSystem.AddWaiter(new BridgeSystem.Waiter {
                Request = r,
                Done = () => BridgeSystem.Tick >= target,
                Result = () => PlayerInfo(player),
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(Int(r, "timeout_ms", 60000)),
                OnTimeout = () => throw new TimeoutException("input ticks did not run in time (in a world? paused?)"),
            });
        }

        private static object PlayerInfo(Player p) {
            return new {
                player = p.whoAmI,
                name = p.name,
                bot = BotManagerSystem.IsBot[p.whoAmI],
                active = p.active,
                dead = p.dead,
                x = Math.Round(p.Center.X / 16f, 2),
                y = Math.Round(p.Center.Y / 16f, 2),
                vx = Math.Round(p.velocity.X, 3),
                vy = Math.Round(p.velocity.Y, 3),
                on_ground = p.velocity.Y == 0f,
                life = p.statLife,
                life_max = p.statLifeMax2,
                wet = p.wet,
                lava = p.lavaWet,
                selected = p.inventory[p.selectedItem].IsAir ? null : p.inventory[p.selectedItem].Name,
                hotbar = Enumerable.Range(0, 10).Where(i => !p.inventory[i].IsAir)
                    .Select(i => new { slot = i, item = p.inventory[i].Name, stack = p.inventory[i].stack }).ToList(),
            };
        }

        /// <summary>
        /// A tile region as text, one string per row. Legend: ' ' outside the world, '.' air, '#' solid,
        /// '=' platform, 'T' tree, '+' other non-solid tile, '~' water, '%' lava, 'h' honey, 's' shimmer;
        /// drawn over the tiles: 'P' local player, 'B' bot, 'p' other player, 'E' hostile NPC, 'N' friendly NPC, '*' item.
        /// </summary>
        private static void Tiles(BridgeRequest r) {
            int w = Math.Clamp(Int(r, "w", 80), 1, 400);
            int h = Math.Clamp(Int(r, "h", 45), 1, 400);
            int x0, y0;
            if (r.Args["x"] != null) {
                x0 = Int(r, "x", 0);
                y0 = Int(r, "y", 0);
            }
            else {
                Player center = Target(r);
                x0 = (int)(center.Center.X / 16f) - w / 2;
                y0 = (int)(center.Center.Y / 16f) - h / 2;
            }

            var grid = new char[h][];
            for (int row = 0; row < h; row++) {
                grid[row] = new char[w];
                for (int col = 0; col < w; col++) grid[row][col] = TileChar(x0 + col, y0 + row);
            }

            void Mark(Vector2 worldPos, char c) {
                int col = (int)(worldPos.X / 16f) - x0, row = (int)(worldPos.Y / 16f) - y0;
                if (row >= 0 && row < h && col >= 0 && col < w) grid[row][col] = c;
            }
            foreach (Item item in Main.item) if (item.active) Mark(item.Center, '*');
            foreach (NPC npc in Main.npc) if (npc.active) Mark(npc.Center, npc.friendly || npc.townNPC ? 'N' : 'E');
            for (int i = 0; i < Main.maxPlayers; i++) {
                Player p = Main.player[i];
                if (!p.active || p.dead) continue;
                Mark(p.Center, BotManagerSystem.IsBot[i] ? 'B' : (!Main.dedServ && i == Main.myPlayer ? 'P' : 'p'));
            }

            r.Reply(new {
                x0, y0, w, h,
                rows = grid.Select(row => new string(row)).ToList(),
                legend = "' ' outside, '.' air, '#' solid, '=' platform, 'T' tree, '+' non-solid tile, '~' water, '%' lava, 'h' honey, 's' shimmer, 'P' you, 'B' bot, 'p' player, 'E' hostile, 'N' friendly, '*' item",
            });
        }

        private static char TileChar(int x, int y) {
            if (!WorldGen.InWorld(x, y)) return ' ';
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && !tile.IsActuated) {
                ushort type = tile.TileType;
                if (Main.tileSolidTop[type]) return '=';
                if (Main.tileSolid[type]) return '#';
                if (type == TileID.Trees || type == TileID.PalmTree) return 'T';
                if (tile.LiquidAmount == 0) return '+';
            }
            if (tile.LiquidAmount > 0) {
                return tile.LiquidType switch {
                    LiquidID.Lava => '%',
                    LiquidID.Honey => 'h',
                    LiquidID.Shimmer => 's',
                    _ => '~',
                };
            }
            return '.';
        }

        private static void Entities(BridgeRequest r) {
            Vector2 center = r.Args["x"] != null ? new Vector2(Float(r, "x", 0), Float(r, "y", 0)) * 16f : Target(r).Center;
            float radius = Float(r, "radius", 50) * 16f;
            bool Near(Entity e) => e.active && Vector2.Distance(e.Center, center) <= radius;
            r.Reply(new {
                npcs = Main.npc.Where(Near).Take(100).Select(n => new {
                    index = n.whoAmI, type = n.type, name = n.FullName, x = Math.Round(n.Center.X / 16f, 1), y = Math.Round(n.Center.Y / 16f, 1),
                    life = n.life, life_max = n.lifeMax, hostile = !n.friendly && !n.townNPC, boss = n.boss, damage = n.damage,
                }).ToList(),
                items = Main.item.Where(Near).Take(100).Select(i => new {
                    index = i.whoAmI, name = i.Name, stack = i.stack, x = Math.Round(i.Center.X / 16f, 1), y = Math.Round(i.Center.Y / 16f, 1),
                }).ToList(),
                projectiles = Main.projectile.Where(Near).Take(100).Select(p => new {
                    index = p.whoAmI, type = p.type, name = p.Name, x = Math.Round(p.Center.X / 16f, 1), y = Math.Round(p.Center.Y / 16f, 1),
                    hostile = p.hostile, owner = p.owner,
                }).ToList(),
            });
        }

        private static int ResolveId(JToken token, IdDictionary search, string kind) {
            if (token == null) throw new ArgumentException($"missing {kind}");
            if (token.Type == JTokenType.Integer) return (int)token;
            string name = (string)token;
            if (search.TryGetId(name, out int id)) return id;
            throw new ArgumentException($"unknown {kind} '{name}' (use the {kind}ID name, e.g. IronPickaxe, or a numeric id)");
        }

        private static void Give(BridgeRequest r) {
            Player player = Target(r);
            int type = ResolveId(r.Args["item"], ItemID.Search, "Item");
            for (int slot = 0; slot < 50; slot++) {
                if (!player.inventory[slot].IsAir) continue;
                Item item = player.inventory[slot];
                item.SetDefaults(type);
                item.stack = Math.Clamp(Int(r, "stack", 1), 1, item.maxStack);
                r.Reply(new { player = player.whoAmI, slot, item = item.Name, stack = item.stack });
                return;
            }
            r.Fail("inventory is full");
        }

        private static void SpawnNpc(BridgeRequest r) {
            int type = ResolveId(r.Args["npc"], NPCID.Search, "NPC");
            Vector2 at = r.Args["x"] != null ? new Vector2(Float(r, "x", 0), Float(r, "y", 0)) * 16f : Target(r).Center + new Vector2(10 * 16f, 0f);
            int index = NPC.NewNPC(new EntitySource_DebugCommand("automation"), (int)at.X, (int)at.Y, type);
            NPC npc = Main.npc[index];
            r.Reply(new { index, name = npc.FullName, x = Math.Round(npc.Center.X / 16f, 1), y = Math.Round(npc.Center.Y / 16f, 1) });
        }
    }
}
